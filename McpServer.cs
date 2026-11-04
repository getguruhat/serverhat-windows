using System.Diagnostics;
using System.Net.Http;
using System.Text;

namespace ServerHat;

/// `ServerHat.exe --mcp`: a Model Context Protocol server on stdin/stdout, so Claude Code,
/// Codex and other MCP clients get ServerHat as tools, as on macOS. It drives the running
/// app through the local API (opening the app if it isn't running) and reads the API token
/// itself, so the token never appears in an MCP config or a chat.
///
/// ServerHat.exe is a Windows app, not a console one, but MCP clients start it with pipes for
/// its standard input and output, and those work the same.
internal static class McpServer
{
    private static readonly string[] SupportedVersions = ["2025-06-18", "2025-03-26", "2024-11-05"];

    public static int Run()
    {
        var utf8 = new UTF8Encoding(false);
        using var input = new StreamReader(Console.OpenStandardInput(), utf8);
        using var output = new StreamWriter(Console.OpenStandardOutput(), utf8) { AutoFlush = true, NewLine = "\n" };
        while (input.ReadLine() is { } line)
        {
            if (line.Trim().Length == 0) continue;
            object? message;
            try { message = Json.Parse(line); }
            catch (System.Text.Json.JsonException) { message = null; }
            if (message is not Dictionary<string, object?> request)
            {
                Send(output, Map("jsonrpc", "2.0", "id", null, "error", Map("code", -32700L, "message", "parse error")));
                continue;
            }
            if (Handle(request) is { } answer) Send(output, answer);
        }
        return 0;
    }

    private static void Send(StreamWriter output, Dictionary<string, object?> message) => output.WriteLine(Json.Compact(message));

    private static Dictionary<string, object?> Map(params object?[] pairs)
    {
        var map = new Dictionary<string, object?>();
        for (var i = 0; i < pairs.Length; i += 2) map[(string)pairs[i]!] = pairs[i + 1];
        return map;
    }

    // MARK: JSON-RPC

    /// The answer to a request; null for notifications such as notifications/initialized.
    internal static Dictionary<string, object?>? Handle(Dictionary<string, object?> message)
    {
        if (message.TryGetValue("method", out var m) is false || m is not string method || !message.TryGetValue("id", out var id)) return null;
        var parameters = message.TryGetValue("params", out var p) && p is Dictionary<string, object?> d ? d : [];
        Dictionary<string, object?> Reply(Dictionary<string, object?> result) => Map("jsonrpc", "2.0", "id", id, "result", result);
        switch (method)
        {
            case "initialize":
                var requested = parameters.TryGetValue("protocolVersion", out var v) ? v as string : null;
                return Reply(Map(
                    "protocolVersion", requested is not null && SupportedVersions.Contains(requested) ? requested : SupportedVersions[0],
                    "capabilities", Map("tools", Map()),
                    "serverInfo", Map("name", "serverhat", "title", "ServerHat", "version", UpdateCheck.Current.ToString()),
                    "instructions",
                        "ServerHat runs the user's local web servers from the Windows notification area. Use these tools instead "
                        + "of starting dev servers in your own shell, so they keep running after your session and the "
                        + "user can control them. List servers before adding one, and don't stop or remove servers the "
                        + "user didn't mention. New or changed commands require the user's approval in ServerHat. "
                        + "If a start is actually blocked for approval, tell the user once with the exact command "
                        + "and folder shown in the error. The notification opens a review with Allow, Remove and "
                        + "Later choices. Don't repeat generic approval warnings."));
            case "ping":
                return Reply(Map());
            case "tools/list":
                return Reply(Map("tools", Tools));
            case "tools/call":
                var name = parameters.TryGetValue("name", out var n) ? n as string ?? "" : "";
                var args = parameters.TryGetValue("arguments", out var a) && a is Dictionary<string, object?> given ? given : [];
                return Reply(Call(name, args));
            default:
                return Map("jsonrpc", "2.0", "id", id, "error", Map("code", -32601L, "message", "method not found: " + method));
        }
    }

    // MARK: Tools

    private static Dictionary<string, object?> ServerFields() => Map(
        "name", Map("type", "string", "description", "What the panel shows."),
        "cwd", Map("type", "string", "description", "Absolute folder the command runs in. ~ and %USERPROFILE% work."),
        "command", Map("type", "string", "description", "A program on PATH (node, npm, python) or a path to one. npm, npx, pnpm and yarn work as they are. Run directly, not through a shell: no pipes, && or cd. For shell syntax use cmd with args [\"/c\", \"…\"]."),
        "args", Map("type", "array", "items", Map("type", "string"), "description", "Arguments, one string each."),
        "port", Map("type", "integer", "description", "The port the server really listens on, so ServerHat can tell when it's up."),
        "url", Map("type", "string", "description", "What Open in browser opens. Defaults to http://127.0.0.1:<port>."),
        "env", Map("type", "object", "additionalProperties", Map("type", "string"), "description", "Extra environment variables."),
        "startAtLogin", Map("type", "boolean", "description", "Start this server whenever ServerHat starts."));

    private static Dictionary<string, object?> IdProperty() => Map("id", Map("type", "string", "description", "The server's id, from list_servers."));

    private static Dictionary<string, object?> With(Dictionary<string, object?> properties, params object?[] more)
    {
        foreach (var pair in Map(more)) properties[pair.Key] = pair.Value;
        return properties;
    }

    private static Dictionary<string, object?> Tool(string name, string title, string description, Dictionary<string, object?>? properties = null,
        string[]? required = null, bool readOnly = false, bool destructive = false) => Map(
        "name", name,
        "title", title,
        "description", description,
        "inputSchema", Map("type", "object", "properties", properties ?? Map(), "required", (required ?? []).ToList()),
        "annotations", Map("title", title, "readOnlyHint", readOnly, "destructiveHint", destructive, "openWorldHint", false));

    private static readonly object WaitField = Map("type", "integer", "description", "Seconds to wait, up to 60. Default 30.");

    internal static readonly List<object?> Tools =
    [
        Tool("list_servers", "List servers",
            "Every server in ServerHat with its settings and status (stopped, starting, running, stopping, crashed, port-in-use or failed). Check this before adding a server.",
            readOnly: true),
        Tool("get_server", "Get server", "One server's settings and status.", IdProperty(), ["id"], readOnly: true),
        Tool("start_server", "Start server",
            "Start a server and wait until it's running or has failed. If approval is required, the error names the command and folder; report that specific prompt once. If startup fails, the answer includes recent output.",
            With(IdProperty(), "wait", WaitField), ["id"]),
        Tool("stop_server", "Stop server", "Stop a server (and whatever it started).", IdProperty(), ["id"], destructive: true),
        Tool("restart_server", "Restart server",
            "Stop a server and start it again, for example after changing its settings. Waits until it's running or has failed. If approval is required, report the specific command and folder once.",
            With(IdProperty(), "wait", WaitField), ["id"]),
        Tool("get_logs", "Get logs", "The last lines a server printed. Use them to diagnose a crash.",
            With(IdProperty(), "lines", Map("type", "integer", "description", "How many lines, up to 1000. Default 100.")), ["id"], readOnly: true),
        Tool("add_server", "Add server",
            "Add a server to ServerHat. If start is true and approval is needed, the user gets one notification showing the command and folder; clicking it opens Allow/Remove/Later choices.",
            With(ServerFields(),
                "id", Map("type", "string", "description", "Short unique id. Made from name if left out."),
                "start", Map("type", "boolean", "description", "Start it after adding and wait until it's running.")),
            ["name", "command", "cwd"]),
        Tool("update_server", "Update server",
            "Change some of a server's settings. Only the fields you send change; null clears port or url. Restart the server afterwards to use them. Changing command, args, cwd or env needs the user's approval again.",
            With(ServerFields(), "id", Map("type", "string", "description", "The server's id, from list_servers.")), ["id"]),
        Tool("remove_server", "Remove server",
            "Stop a server and remove it from ServerHat. Its folder isn't touched. Only do this when the user asks.",
            IdProperty(), ["id"], destructive: true),
        Tool("import_launch_json", "Import launch.json",
            "Add the servers in a project's .claude\\launch.json. Ones already in ServerHat are skipped.",
            Map("path", Map("type", "string", "description", "Absolute path to the project folder or its .claude\\launch.json.")), ["path"]),
    ];

    private static Dictionary<string, object?> Call(string name, Dictionary<string, object?> args)
    {
        try
        {
            var id = args.TryGetValue("id", out var i) ? i as string ?? "" : "";
            var path = "/v1/servers/" + Uri.EscapeDataString(id);
            string Number(string key, long fallback, long max) =>
                Math.Min(max, Math.Max(0, args.TryGetValue(key, out var value) && value is long given ? given : fallback)).ToString();
            switch (name)
            {
                case "list_servers": return Result(Api.Request("GET", "/v1/servers"));
                case "get_server": return Result(Api.Request("GET", path));
                case "start_server":
                case "restart_server":
                    var action = name == "start_server" ? "start" : "restart";
                    return Result(WithLogsIfFailed(Api.Request("POST", $"{path}/{action}?wait={Number("wait", 30, 60)}"), id));
                case "stop_server": return Result(Api.Request("POST", path + "/stop?wait=10"));
                case "get_logs": return Result(Api.Request("GET", $"{path}/logs?lines={Number("lines", 100, 1000)}"));
                case "add_server":
                    var body = new Dictionary<string, object?>(args);
                    var start = body.TryGetValue("start", out var s) && s is true;
                    body.Remove("start");
                    var added = Api.Request("POST", "/v1/servers", body);
                    if (!start || !added.Ok || added.Json is not Dictionary<string, object?> server || !server.TryGetValue("id", out var newValue) || newValue is not string newId) return Result(added);
                    return Result(WithLogsIfFailed(Api.Request("POST", $"/v1/servers/{Uri.EscapeDataString(newId)}/start?wait=60"), newId));
                case "update_server":
                    var changes = new Dictionary<string, object?>(args);
                    changes.Remove("id");
                    return Result(Api.Request("PATCH", path, changes));
                case "remove_server": return Result(Api.Request("DELETE", path));
                case "import_launch_json":
                    return Result(Api.Request("POST", "/v1/import", Map("path", args.TryGetValue("path", out var p) ? p as string ?? "" : "")));
                default: return Failure("unknown tool " + name);
            }
        }
        catch (Exception e) when (e is Api.Error or HttpRequestException or TaskCanceledException or IOException)
        {
            return Failure(e is Api.Error ? e.Message : "ServerHat didn't answer: " + e.Message);
        }
    }

    /// Adds the server's recent output when it didn't come up, so the agent can fix it in one step.
    private static Api.Answer WithLogsIfFailed(Api.Answer answer, string id)
    {
        if (!answer.Ok || answer.Json is not Dictionary<string, object?> server
            || server.TryGetValue("status", out var st) is false || st is not Dictionary<string, object?> status
            || status.TryGetValue("state", out var state) is false || state is not ("crashed" or "failed" or "port-in-use")) return answer;
        try
        {
            if (Api.Request("GET", $"/v1/servers/{Uri.EscapeDataString(id)}/logs?lines=40").Json is Dictionary<string, object?> logs && logs.TryGetValue("lines", out var lines))
                server["recentLogs"] = lines;
        }
        catch (Exception e) when (e is Api.Error or HttpRequestException or TaskCanceledException or IOException) { }
        return answer;
    }

    private static Dictionary<string, object?> Result(Api.Answer answer) =>
        Map("content", new List<object?> { Map("type", "text", "text", Json.Write(answer.Json ?? Map("status", (long)answer.Status))) }, "isError", !answer.Ok);

    private static Dictionary<string, object?> Failure(string message) =>
        Map("content", new List<object?> { Map("type", "text", "text", message) }, "isError", true);

    // MARK: API client

    /// Talks to the app's local API, opening ServerHat first if it isn't running.
    private static class Api
    {
        public sealed record Answer(int Status, object? Json)
        {
            public bool Ok => Status is >= 200 and < 300;
        }

        public sealed class Error(string message) : Exception(message);

        // Up to 60 s waiting for approval, then up to 60 s for the server to come up.
        private static readonly HttpClient client = new() { Timeout = TimeSpan.FromSeconds(135) };
        private static string? baseUrl;
        private static string token = "";

        static Api() => client.DefaultRequestHeaders.ExpectContinue = false;

        public static Answer Request(string method, string path, object? body = null)
        {
            if (baseUrl is null) Connect();
            try { return Send(method, path, body); }
            catch (HttpRequestException)
            {
                // ServerHat may have quit since the last call.
                Connect();
                return Send(method, path, body);
            }
        }

        private static Answer Send(string method, string path, object? body)
        {
            using var request = new HttpRequestMessage(new HttpMethod(method), baseUrl + path);
            request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + token);
            if (body is not null) request.Content = new StringContent(Json.Write(body), Encoding.UTF8, "application/json");
            using var response = client.SendAsync(request).GetAwaiter().GetResult();
            var text = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
            object? json = null;
            try { if (text.Trim().Length > 0) json = Json.Parse(text); }
            catch (System.Text.Json.JsonException) { }
            return new Answer((int)response.StatusCode, json);
        }

        private static void Connect()
        {
            if (!ReadInfoAndCheckHealth())
            {
                OpenApp();
                var deadline = DateTime.UtcNow.AddSeconds(15);
                while (DateTime.UtcNow < deadline)
                {
                    Thread.Sleep(250);
                    if (ReadInfoAndCheckHealth()) break;
                }
            }
            if (baseUrl is null || token.Length == 0)
                throw new Error("ServerHat isn't running and couldn't be opened. Ask the user to open ServerHat from the Start menu.");
        }

        /// Reads api.json and the token file (both written by the app), then checks /v1/health.
        private static bool ReadInfoAndCheckHealth()
        {
            var url = "http://127.0.0.1:" + ApiServer.DefaultPort;
            var tokenFile = ApiServer.TokenPath;
            try
            {
                if (Json.Parse(File.ReadAllText(ApiServer.InfoPath)) is Dictionary<string, object?> info)
                {
                    if (info.TryGetValue("url", out var u) && u is string written) url = written;
                    if (info.TryGetValue("tokenFile", out var t) && t is string file) tokenFile = Paths.Expand(file);
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Text.Json.JsonException) { }
            try
            {
                using var health = client.GetAsync(url + "/v1/health", new CancellationTokenSource(TimeSpan.FromSeconds(2)).Token).GetAwaiter().GetResult();
                if ((int)health.StatusCode != 200) return false;
                var read = File.ReadAllText(tokenFile).Trim();
                if (read.Length == 0) return false;
                baseUrl = url.TrimEnd('/');
                token = read;
                return true;
            }
            catch (Exception e) when (e is HttpRequestException or TaskCanceledException or IOException or UnauthorizedAccessException or UriFormatException or InvalidOperationException)
            {
                return false;
            }
        }

        /// Starts the ServerHat.exe this process is; it shows only its notification-area icon.
        /// Shell execute, so the app doesn't inherit the MCP client's pipes and keep them open.
        private static void OpenApp()
        {
            try { Process.Start(new ProcessStartInfo(Application.ExecutablePath) { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(Application.ExecutablePath)! })?.Dispose(); }
            catch (System.ComponentModel.Win32Exception) { }
        }
    }

    // MARK: Setup commands

    /// The command that adds ServerHat to Claude Code's or Codex's MCP servers, for the
    /// Copy for Claude Code and Copy for Codex items.
    public static string SetupCommand(bool codex)
    {
        var exe = "\"" + StorePackage.ExecutablePath + "\"";
        return codex ? $"codex mcp add serverhat -- {exe} --mcp" : $"claude mcp add --scope user serverhat -- {exe} --mcp";
    }
}
