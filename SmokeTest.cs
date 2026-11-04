using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.Win32;
using Timer = System.Windows.Forms.Timer;

namespace ServerHat;

/// `ServerHat.exe --smoke-test`, run by GitHub Actions on Windows: starts the real
/// ServerManager and API in a temporary folder, drives them over HTTP the way Claude Code
/// would, and writes smoke-test.log. Exit code 0 means every check passed.
///
/// The servers it runs are ServerHat.exe itself in stand-in modes: `--test-listen <port>`
/// listens on a port and starts a `--test-child` that never exits, and `--print-args`
/// prints its arguments. `lwtest.cmd` puts a batch file in between, like npm.cmd.
internal static class SmokeTest
{
    private const int ApiPort = 47391, ServerPort = 47392, McpPort = 47393;
    private static readonly List<string> log = [];
    private static int failures;
    private static string token = "";

    public static int Run()
    {
        var context = new ApplicationContext();
        var start = new Timer { Interval = 1 };
        start.Tick += async (_, _) =>
        {
            start.Stop();
            try { await Steps(); }
            catch (Exception e) { Fail("unexpected " + e); }
            finally { context.ExitThread(); }
        };
        start.Start();
        Application.Run(context);
        Note(failures == 0 ? "PASS: every check passed" : $"FAIL: {failures} check(s) failed");
        File.WriteAllLines("smoke-test.log", log);
        return failures == 0 ? 0 : 1;
    }

    private static void Note(string line) { log.Add(line); Console.WriteLine(line); }
    private static void Fail(string what) { failures++; Note("FAIL " + what); }
    private static void Check(bool ok, string what) { if (ok) Note("ok   " + what); else Fail(what); }

    /// Every translation formats with the same placeholders as English, and the menu shows
    /// the Windows display language while the API stays English.
    private static void Translations()
    {
        var english = global::ServerHat.Strings.For(new System.Globalization.CultureInfo("en-US"));
        var texts = typeof(global::ServerHat.Strings).GetProperties().Where(p => p.PropertyType == typeof(string)).ToList();
        static string Holes(string text) => string.Join(",", System.Text.RegularExpressions.Regex.Matches(text, @"\{\d\}").Cast<System.Text.RegularExpressions.Match>().Select(m => m.Value).OrderBy(v => v));
        foreach (var name in new[] { "es-ES", "de-DE", "fr-FR", "pt-BR", "ja-JP", "zh-CN", "ko-KR" })
        {
            var strings = global::ServerHat.Strings.For(new System.Globalization.CultureInfo(name));
            var wrong = texts.Where(p => Holes((string)p.GetValue(strings)!) != Holes((string)p.GetValue(english)!)).Select(p => p.Name).ToList();
            Check(wrong.Count == 0, $"{name} placeholders match English{(wrong.Count > 0 ? ": " + string.Join(", ", wrong) : "")}");
            Check(strings.StartAll != english.StartAll && strings.ApprovalExplanation != english.ApprovalExplanation, $"{name} is translated");
            try { _ = texts.Select(p => string.Format((string)p.GetValue(strings)!, "a", "b", "c")).ToList(); Check(true, $"{name} formats"); }
            catch (FormatException e) { Fail($"{name} formats: {e.Message}"); }
        }
        Check(global::ServerHat.Strings.For(new System.Globalization.CultureInfo("de-AT")).StartAll == "Alle starten", "regional German uses German");
        Check(global::ServerHat.Strings.For(new System.Globalization.CultureInfo("zh-TW")).StartAll == english.StartAll, "Traditional Chinese falls back to English");
        Check(global::ServerHat.Strings.For(new System.Globalization.CultureInfo("es-MX")).StartAll == "Iniciar todos", "Mexican Spanish uses Spanish");
    }

    private static async Task Steps()
    {
        // The checks below read English text; the API's answers are English in every language.
        Translations();
        global::ServerHat.Strings.Use(new System.Globalization.CultureInfo("ja-JP"));
        var japanese = new ServerState { Status = ServerStatus.PortInUse }.Shown();
        global::ServerHat.Strings.Use(new System.Globalization.CultureInfo("en-US"));
        Check(japanese == "ポート使用中", "the panel's status follows the display language");
        var root = Path.Combine(Path.GetTempPath(), "ServerHat-smoke-" + Process.GetCurrentProcess().Id);
        Paths.ConfigFolder = Path.Combine(root, "config");
        Paths.LogFolder = Path.Combine(root, "logs");
        var bin = Path.Combine(root, "bin");
        Directory.CreateDirectory(bin);
        var exe = Application.ExecutablePath;
        File.WriteAllText(Path.Combine(bin, "lwtest.cmd"), $"@\"{exe}\" --test-listen %*\r\n");
        File.WriteAllText(Path.Combine(bin, "lwargs.cmd"), $"@\"{exe}\" --print-args %*\r\n");
        Environment.SetEnvironmentVariable("PATH", bin + ";" + Environment.GetEnvironmentVariable("PATH"));
        Note("folder " + root);
        // Approvals go to a registry key of the test's own.
        Settings.Key = @"Software\GuruHat\ServerHat-smoke-" + Process.GetCurrentProcess().Id;
        // A server set up before approvals existed.
        Directory.CreateDirectory(Paths.ConfigFolder);
        File.WriteAllText(Paths.ConfigFile, "{\"version\":1,\"servers\":[{\"name\":\"Existing\",\"command\":\"lwargs\",\"args\":[\"old\"]}]}");

        var manager = new ServerManager();
        var notified = new Dictionary<string, int>();
        manager.ApprovalNeeded += server => notified[server.Id] = notified.TryGetValue(server.Id, out var n) ? n + 1 : 1;
        var api = new ApiServer(manager, ApiPort);
        api.Start();
        Check(api.Problem is null, "API listens on 127.0.0.1:" + ApiPort + (api.Problem is null ? "" : ": " + api.Problem));
        token = File.ReadAllText(ApiServer.TokenPath).Trim();
        Check(token.Length == 64, "api-token holds a 64-character token");
        Check(TokenFile.IsPrivate(ApiServer.TokenPath), "api-token's access list has only the current user");
        Check(Json.Parse(File.ReadAllText(ApiServer.InfoPath)) is Dictionary<string, object?> info && info["url"] as string == "http://127.0.0.1:" + ApiPort, "api.json has the API's URL");

        // The checks and the exact bytes the macOS version sends.
        var health = await Send("GET", "/v1/health", auth: false);
        Check(health.Raw.StartsWith("HTTP/1.1 200 No Error\r\nContent-Type: application/json; charset=utf-8\r\nContent-Length: 41\r\nCache-Control: no-store\r\nX-Content-Type-Options: nosniff\r\nConnection: close\r\n\r\n")
              && health.Body == "{\n  \"app\" : \"ServerHat\",\n  \"ok\" : true\n}\n", "GET /v1/health answers without a token, byte for byte like macOS");
        Check((await Send("GET", "/v1/servers", auth: false)).Status == 401, "no token: 401");
        Check((await Send("GET", "/v1/servers", headers: ["Authorization: Bearer " + new string('0', 64)], auth: false)).Status == 401, "wrong token: 401");
        Check((await Send("GET", "/v1/servers", headers: ["Origin: https://example.com"])).Status == 403, "Origin header: 403");
        Check((await Send("GET", "/v1/servers", host: "evil.example:" + ApiPort)).Status == 403, "other Host header: 403");
        Check((await Send("GET", "/v1/servers", headers: ["Sec-Fetch-Site: cross-site"])).Status == 403, "Sec-Fetch-Site cross-site: 403");
        Check((await Send("GET", "/v1/servers", headers: ["Sec-Fetch-Site: none"])).Status == 200, "Sec-Fetch-Site none: 200");
        Check((await Send("OPTIONS", "/v1/servers")).Status == 403, "OPTIONS: 403");
        Check((await Send("POST", "/v1/servers", "{\"name\":\"x\"}")).Body == "{\n  \"error\" : \"missing \\\"command\\\" at top level\"\n}\n", "missing command: the macOS error text");
        Check((await Send("POST", "/v1/servers", "x", contentType: "text/plain")).Status == 415, "non-JSON body: 415");

        // Approval: what an agent adds or changes can't run until the user allows it.
        await Approval(manager, notified);

        // Add, start through a .cmd shim, wait, and find the whole tree.
        var added = await Send("POST", "/v1/servers", $"{{\"name\":\"Smoke\",\"command\":\"lwtest\",\"args\":[\"{ServerPort}\"],\"port\":{ServerPort},\"cwd\":\"~\"}}");
        Check(added.Status == 201 && Field(added, "id") as string == "smoke", "POST /v1/servers adds \"smoke\": " + added.Status);
        Check((await Send("POST", "/v1/servers", "{\"name\":\"Smoke\",\"command\":\"lwtest\"}")).Status == 409, "same id again: 409");
        manager.Approve("smoke");
        var started = await Send("POST", "/v1/servers/smoke/start?wait=30");
        Check(State(started) == "running", "start?wait=30 through lwtest.cmd: " + State(started) + " " + Detail(started));
        var rootPid = Pid(started);
        await Task.Delay(500);
        var lines = Strings(await Send("GET", "/v1/servers/smoke/logs?lines=50"), "lines");
        Check(lines.Any(l => l.Contains("listening on " + ServerPort)), "logs show the server's output");
        var childPid = lines.Where(l => l.StartsWith("child ")).Select(l => int.Parse(l.Substring(6))).FirstOrDefault();
        Check(childPid > 0 && Running(childPid), "the server started a child process " + childPid);

        // A second server on the same port.
        await Send("POST", "/v1/servers", $"{{\"name\":\"Other\",\"command\":\"lwtest\",\"args\":[\"{ServerPort}\"],\"port\":{ServerPort}}}");
        manager.Approve("other");
        Check((await Send("POST", "/v1/servers/other/start")).Status == 409, "starting another server on a busy port: 409");
        await Task.Delay(1500);
        var other = await Send("GET", "/v1/servers/other");
        Check(State(other) == "port-in-use" && Detail(other).Contains("in use by ServerHat"), "port-in-use names the process holding it: " + Detail(other));

        // Stop must end the batch file, the server and its child.
        var stopped = await Send("POST", "/v1/servers/smoke/stop?wait=20");
        Check(State(stopped) == "stopped", "stop?wait=20: " + State(stopped));
        await Task.Delay(500);
        Check(!Running(rootPid) && !Running(childPid), $"stop ended the whole process tree (cmd {rootPid}, child {childPid})");
        Check(!PortProbe.IsListening(ServerPort), "the port is free again");
        await Task.Delay(3500);
        Check(State(await Send("GET", "/v1/servers/other")) == "stopped", "the other server notices the port is free");

        // Crashes, missing commands, argument quoting.
        await Send("POST", "/v1/servers", "{\"name\":\"Crash\",\"command\":\"cmd\",\"args\":[\"/c\",\"echo boom& exit /b 3\"]}");
        manager.Approve("crash");
        await Send("POST", "/v1/servers/crash/start");
        var crash = await Until("crash", "crashed", 10);
        Check(State(crash) == "crashed" && Field(crash, "status", "exitCode") is long code && code == 3 && Detail(crash).Contains("boom"), "a crash is reported with its exit code and last line: " + Detail(crash));
        await Send("POST", "/v1/servers", "{\"name\":\"Missing\",\"command\":\"definitely-not-a-command-lw\"}");
        manager.Approve("missing");
        var missing = await Send("POST", "/v1/servers/missing/start");
        Check(missing.Status == 400 && State(await Send("GET", "/v1/servers/missing")) == "failed", "a command that isn't on PATH: failed");
        string[] batchArgs = ["a b", "x&y", "100%", "^caret", "(paren)", "!bang!", "semi;colon"];
        await Send("POST", "/v1/servers", "{\"name\":\"Batch args\",\"command\":\"lwargs\",\"args\":" + Json.Write(batchArgs.ToList()) + "}");
        manager.Approve("batch-args");
        await Send("POST", "/v1/servers/batch-args/start");
        await Until("batch-args", "stopped", 10);
        var printed = Strings(await Send("GET", "/v1/servers/batch-args/logs"), "lines").Where(l => l.StartsWith("arg:")).Select(l => l.Substring(4)).ToList();
        Check(printed.SequenceEqual(batchArgs), "arguments reach a program behind a .cmd unchanged: " + string.Join(" | ", printed));
        string[] exeArgs = ["a b", @"back\slash\", "q\"uote", ""];
        await Send("POST", "/v1/servers", "{\"name\":\"Exe args\",\"command\":" + Json.Write(exe) + ",\"args\":" + Json.Write(new List<object?> { "--print-args" }.Concat(exeArgs).ToList()) + "}");
        manager.Approve("exe-args");
        await Send("POST", "/v1/servers/exe-args/start");
        await Until("exe-args", "stopped", 10);
        printed = Strings(await Send("GET", "/v1/servers/exe-args/logs"), "lines").Where(l => l.StartsWith("arg:")).Select(l => l.Substring(4)).ToList();
        Check(printed.SequenceEqual(exeArgs), "arguments reach an .exe unchanged: " + string.Join(" | ", printed));

        // Editing through the API.
        var patched = await Send("PATCH", "/v1/servers/smoke", "{\"port\": null}");
        Check(patched.Status == 200 && Field(patched, "port") is null && Field(patched, "openURL") is null, "PATCH with null clears port");
        await Send("PATCH", "/v1/servers/smoke", $"{{\"port\": {ServerPort}}}");
        Check((await Send("PATCH", "/v1/servers/smoke", "{\"id\":\"renamed\"}")).Status == 400, "the id can't be changed");
        Check((await Send("DELETE", "/v1/servers/crash")).Body.Contains("\"removed\" : \"crash\""), "DELETE removes a server");

        // Importing .claude/launch.json.
        var project = Path.Combine(root, "project");
        Directory.CreateDirectory(Path.Combine(project, ".claude"));
        File.WriteAllText(Path.Combine(project, ".claude", "launch.json"), "{\"version\":\"0.0.1\",\"configurations\":[{\"name\":\"Web App\",\"runtimeExecutable\":\"npm\",\"runtimeArgs\":[\"run\",\"dev\"],\"port\":3000}]}");
        var import = await Send("POST", "/v1/import", "{\"path\":" + Json.Write(project) + "}");
        Check(import.Status == 200 && (Field(import, "added") as List<object?>)?.Count == 1, "import adds the launch.json configuration");
        Check((Field(await Send("POST", "/v1/import", "{\"path\":" + Json.Write(project) + "}"), "added") as List<object?>)?.Count == 0, "importing again adds nothing");

        // Editing servers.json by hand.
        var good = File.ReadAllText(Paths.ConfigFile);
        File.WriteAllText(Paths.ConfigFile, good.Replace("\"servers\" : [", "\"servers\" : [\n    { \"name\" : \"Hand edited\", \"command\" : \"node\" },"));
        await Task.Delay(1500);
        var handEdited = await Send("GET", "/v1/servers/hand-edited");
        Check(handEdited.Status == 200, "an edit to servers.json is picked up");
        Check(Field(handEdited, "needsApproval") is true && (await Send("POST", "/v1/servers/hand-edited/start")).Status == 403, "a server added in servers.json needs approval");
        File.WriteAllText(Paths.ConfigFile, "{ broken");
        await Task.Delay(1500);
        var broken = await Send("GET", "/v1/servers");
        Check(Field(broken, "configError") as string == "servers.json: not valid JSON" && (Field(broken, "servers") as List<object?>)?.Count > 0,
            "broken servers.json is reported and the last good servers stay");
        File.WriteAllText(Paths.ConfigFile, good);
        await Task.Delay(1500);

        // The MCP server, started the way Claude Code and Codex start it.
        await Mcp(manager);

        // Quitting stops what ServerHat started.
        var again = await Send("POST", "/v1/servers/smoke/start?wait=30");
        Check(State(again) == "running", "start again: " + State(again));
        await Task.Delay(500);
        var againChild = Strings(await Send("GET", "/v1/servers/smoke/logs?lines=20"), "lines").Where(l => l.StartsWith("child ")).Select(l => int.Parse(l.Substring(6))).LastOrDefault();
        var againRoot = Pid(again);
        manager.Shutdown();
        api.Stop();
        await Task.Delay(1000);
        Check(!Running(againRoot) && !Running(againChild), "quitting ends the servers ServerHat started");

        try { Registry.CurrentUser.DeleteSubKeyTree(Settings.Key, false); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        try { Directory.Delete(root, true); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    private static async Task Approval(ServerManager manager, Dictionary<string, int> notified)
    {
        var existing = await Send("GET", "/v1/servers/existing");
        Check(existing.Status == 200 && Field(existing, "needsApproval") is null && Settings.ApprovedCommands?.Count == 1,
            "servers from before approvals count as allowed");

        var added = await Send("POST", "/v1/servers", "{\"name\":\"Pending\",\"command\":\"lwargs\",\"args\":[\"hello\"]}");
        Check(added.Status == 201 && Field(added, "needsApproval") is true, "a server added through the API needs approval");
        var expected = "{\n  \"approvalNote\" : \"New or changed command: it can't start until the user clicks Allow in the ServerHat menu.\",\n"
                       + "  \"args\" : [\n    \"hello\"\n  ],\n  \"command\" : \"lwargs\",\n  \"cwd\" : \"~\",\n  \"env\" : {\n\n  },\n  \"id\" : \"pending\",\n"
                       + "  \"log\" : " + Json.Write(manager.Log("pending").FilePath) + ",\n  \"name\" : \"Pending\",\n  \"needsApproval\" : true,\n"
                       + "  \"startAtLogin\" : false,\n  \"status\" : {\n    \"state\" : \"stopped\"\n  }\n}\n";
        var pending = await Send("GET", "/v1/servers/pending");
        Check(pending.Body == expected, "a pending server's JSON is byte for byte like macOS:\n" + pending.Body);

        const string refusal = "{\n  \"error\" : \"Approval needed for Pending: lwargs hello in ~. Click its ServerHat notification to review the command and Allow it, or open the ServerHat menu.\"\n}\n";
        var start = await Send("POST", "/v1/servers/pending/start");
        Check(start.Raw.StartsWith("HTTP/1.1 403 Forbidden\r\n") && start.Body == refusal, "start answers 403 with the macOS text: " + start.Body.Trim());
        Check((await Send("POST", "/v1/servers/pending/restart")).Body == refusal, "restart answers the same 403");
        var waited = DateTime.UtcNow;
        Check((await Send("POST", "/v1/servers/pending/start?wait=1")).Status == 403 && DateTime.UtcNow - waited >= TimeSpan.FromSeconds(0.9),
            "start?wait=1 waits a second for approval, then answers 403");
        await Send("POST", "/v1/start-all");
        await Task.Delay(1000);
        Check(!Strings(await Send("GET", "/v1/servers/pending/logs"), "lines").Any(l => l.StartsWith("arg:")), "start-all leaves a pending server alone");
        Check(Strings(await Send("GET", "/v1/servers/existing/logs"), "lines").Contains("arg:old"), "start-all starts the allowed ones");
        Check(notified.TryGetValue("pending", out var once) && once == 1, $"one notification for the pending command, however often it's started ({once})");

        async Task AllowSoon() { await Task.Delay(1000); manager.Approve("pending"); }
        var allowing = AllowSoon();
        var allowed = await Send("POST", "/v1/servers/pending/start?wait=15");
        await allowing;
        Check(allowed.Status == 200 && Field(allowed, "needsApproval") is null, "start?wait=15 goes ahead once the user allows it: " + allowed.Status);
        await Until("pending", "stopped", 10);
        Check(Strings(await Send("GET", "/v1/servers/pending/logs"), "lines").Contains("arg:hello"), "the allowed server ran");
        Check(Settings.ApprovedCommands?.Contains(ServerManager.Fingerprint(manager.Server("pending")!)) == true && !File.ReadAllText(Paths.ConfigFile).Contains("needsApproval"),
            "approvals are kept in the registry, not in servers.json");

        var renamed = await Send("PATCH", "/v1/servers/pending", "{\"name\":\"Pending\",\"port\":47393,\"url\":\"http://127.0.0.1:47393/x\",\"startAtLogin\":false}");
        Check(renamed.Status == 200 && Field(renamed, "needsApproval") is null, "changing name, port, url or startAtLogin doesn't need approval");
        await Send("PATCH", "/v1/servers/pending", "{\"port\":null,\"url\":null}");
        var changed = await Send("PATCH", "/v1/servers/pending", "{\"args\":[\"changed\"]}");
        Check(Field(changed, "needsApproval") is true && (await Send("POST", "/v1/servers/pending/start")).Status == 403, "changing args needs approval again");
        Check(notified["pending"] == 2, "the changed command gets a notification of its own");
        manager.Approve("pending");
        Check((await Send("POST", "/v1/servers/pending/start")).Status == 200, "and starts once allowed");
        await Until("pending", "stopped", 10);
        Check(Field(await Send("PATCH", "/v1/servers/pending", "{\"env\":{\"SMOKE\":\"1\"}}"), "needsApproval") is true, "changing env needs approval");
        Check(Field(await Send("PATCH", "/v1/servers/pending", "{\"cwd\":\"~/..\"}"), "needsApproval") is true, "changing cwd needs approval");
        var edited = manager.Server("pending")!.Copy();
        edited.Args = ["edited"];
        manager.Update("pending", edited, approvedByUser: true);
        Check(Field(await Send("GET", "/v1/servers/pending"), "needsApproval") is null, "a change saved in ServerHat's own window counts as allowed");
    }

    // MARK: HTTP

    /// Runs `ServerHat.exe --mcp` with pipes for its input and output, pointed at the test's
    /// folder, and goes through what an agent does: list, add, start once allowed, read logs, stop.
    private static async Task Mcp(ServerManager manager)
    {
        var start = new ProcessStartInfo(Application.ExecutablePath, "--mcp")
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = new UTF8Encoding(false),
        };
        start.EnvironmentVariables["SERVERHAT_SUPPORT_FOLDER"] = Paths.ConfigFolder;
        using var mcp = Process.Start(start)!;
        mcp.StandardInput.AutoFlush = true;
        var nextId = 0L;
        async Task<Dictionary<string, object?>?> Ask(string method, Dictionary<string, object?>? parameters = null)
        {
            var request = new Dictionary<string, object?> { ["jsonrpc"] = "2.0", ["id"] = ++nextId, ["method"] = method };
            if (parameters is not null) request["params"] = parameters;
            await mcp.StandardInput.WriteAsync(Json.Compact(request) + "\n");
            var read = mcp.StandardOutput.ReadLineAsync();
            if (await Task.WhenAny(read, Task.Delay(90000)) != read) { Fail("no MCP answer to " + method); return null; }
            var line = await read;
            return line is null ? null : Json.Parse(line) as Dictionary<string, object?>;
        }
        static object? At(object? value, params string[] keys)
        {
            foreach (var key in keys) value = value is Dictionary<string, object?> d && d.TryGetValue(key, out var v) ? v : null;
            return value;
        }
        async Task<(bool Error, string Text)> Tool(string name, Dictionary<string, object?> args)
        {
            var answer = await Ask("tools/call", new() { ["name"] = name, ["arguments"] = args });
            var text = (At(answer, "result", "content") as List<object?>)?.FirstOrDefault() is Dictionary<string, object?> c && c["text"] is string t ? t : "";
            return (At(answer, "result", "isError") is true, text);
        }

        var hello = await Ask("initialize", new() { ["protocolVersion"] = "2025-06-18", ["capabilities"] = new Dictionary<string, object?>(), ["clientInfo"] = new Dictionary<string, object?> { ["name"] = "smoke", ["version"] = "1" } });
        Check(At(hello, "result", "protocolVersion") as string == "2025-06-18" && At(hello, "result", "serverInfo", "name") as string == "serverhat", "MCP initialize answers as serverhat");
        await mcp.StandardInput.WriteAsync("{\"jsonrpc\":\"2.0\",\"method\":\"notifications/initialized\"}\n");
        var tools = (At(await Ask("tools/list"), "result", "tools") as List<object?>)?.Select(t => At(t, "name") as string).ToList() ?? [];
        Check(tools.Count == 10 && tools.Contains("start_server") && tools.Contains("get_logs"), "MCP lists 10 tools: " + string.Join(", ", tools));
        var listed = await Tool("list_servers", []);
        Check(!listed.Error && listed.Text.Contains("\"id\" : \"smoke\""), "MCP list_servers shows the servers");

        var added = await Tool("add_server", new() { ["name"] = "Mcp", ["command"] = "lwtest", ["args"] = new List<object?> { McpPort.ToString() }, ["port"] = (long)McpPort, ["cwd"] = "~" });
        Check(!added.Error && added.Text.Contains("\"needsApproval\" : true"), "MCP add_server adds a server that waits for approval");
        var blocked = await Tool("start_server", new() { ["id"] = "mcp", ["wait"] = 0L });
        Check(blocked.Error && blocked.Text.Contains("lwtest"), "MCP start_server before approval is refused and names the command: " + blocked.Text.Replace("\n", " "));
        manager.Approve("mcp");
        var started = await Tool("start_server", new() { ["id"] = "mcp" });
        Check(!started.Error && started.Text.Contains("\"state\" : \"running\""), "MCP start_server waits until it's running");
        await Task.Delay(500);
        var logs = await Tool("get_logs", new() { ["id"] = "mcp", ["lines"] = 20L });
        Check(!logs.Error && logs.Text.Contains("listening on " + McpPort), "MCP get_logs returns the server's output");
        var stopped = await Tool("stop_server", new() { ["id"] = "mcp" });
        Check(!stopped.Error && stopped.Text.Contains("\"state\" : \"stopped\""), "MCP stop_server stops it");
        Check(!(await Tool("remove_server", new() { ["id"] = "mcp" })).Error && manager.Server("mcp") is null, "MCP remove_server removes it");
        Check((await Tool("get_server", new() { ["id"] = "nope" })).Error, "MCP reports an unknown server as an error");
        Check(At(await Ask("no/such/method"), "error", "code") is long code && code == -32601, "MCP answers an unknown method with -32601");

        mcp.StandardInput.Close();
        var exited = await Task.Run(() => mcp.WaitForExit(10000));
        Check(exited && mcp.ExitCode == 0, "MCP exits when its input closes");
        if (!exited) mcp.Kill();
    }

    private sealed record Reply(int Status, string Raw, string Body, object? Json);

    /// A raw HTTP request, so the test controls every header.
    private static async Task<Reply> Send(string method, string path, string? body = null, string[]? headers = null, bool auth = true,
        string? host = null, string contentType = "application/json")
    {
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, ApiPort);
        var request = new StringBuilder($"{method} {path} HTTP/1.1\r\nHost: {host ?? "127.0.0.1:" + ApiPort}\r\n");
        if (auth) request.Append($"Authorization: Bearer {token}\r\n");
        foreach (var header in headers ?? []) request.Append(header).Append("\r\n");
        var bytes = body is null ? [] : Encoding.UTF8.GetBytes(body);
        if (body is not null) request.Append($"Content-Type: {contentType}\r\nContent-Length: {bytes.Length}\r\n");
        request.Append("\r\n");
        var stream = client.GetStream();
        var head = Encoding.ASCII.GetBytes(request.ToString());
        await stream.WriteAsync(head, 0, head.Length);
        await stream.WriteAsync(bytes, 0, bytes.Length);
        var response = new MemoryStream();
        await stream.CopyToAsync(response);
        var raw = Encoding.UTF8.GetString(response.ToArray());
        var split = raw.IndexOf("\r\n\r\n", StringComparison.Ordinal);
        var text = split >= 0 ? raw.Substring(split + 4) : "";
        var status = raw.Length >= 12 && int.TryParse(raw.Substring(9, 3), out var s) ? s : 0;
        object? json = null;
        try { json = Json.Parse(text); } catch (System.Text.Json.JsonException) { }
        return new Reply(status, raw, text, json);
    }

    private static object? Field(Reply reply, params string[] keys)
    {
        object? value = reply.Json;
        foreach (var key in keys) value = value is Dictionary<string, object?> d && d.TryGetValue(key, out var v) ? v : null;
        return value;
    }

    private static string State(Reply reply) => Field(reply, "status", "state") as string ?? $"(HTTP {reply.Status}: {reply.Body.Trim()})";
    private static string Detail(Reply reply) => Field(reply, "status", "detail") as string ?? "";
    private static int Pid(Reply reply) => Field(reply, "status", "pid") is long pid ? (int)pid : 0;
    private static List<string> Strings(Reply reply, string key) => (Field(reply, key) as List<object?>)?.OfType<string>().ToList() ?? [];

    /// Polls a server until it reaches `state`, for up to `seconds`.
    private static async Task<Reply> Until(string id, string state, int seconds)
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (true)
        {
            var reply = await Send("GET", "/v1/servers/" + id);
            if (State(reply) == state || DateTime.UtcNow > deadline) return reply;
            await Task.Delay(250);
        }
    }

    private static bool Running(int pid)
    {
        if (pid <= 0) return false;
        try
        {
            using var process = Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch (ArgumentException) { return false; }
        catch (InvalidOperationException) { return false; }
    }

    // MARK: Stand-in server

    /// `--test-listen <port>`: answers HTTP on the port and starts a child that never exits.
    public static void Listen(int port)
    {
        var listener = new TcpListener(IPAddress.Loopback, port);
        listener.Start();
        Console.WriteLine("listening on " + port);
        var child = Process.Start(new ProcessStartInfo(Application.ExecutablePath, "--test-child") { UseShellExecute = false, CreateNoWindow = true });
        Console.WriteLine("child " + child!.Id);
        while (true)
        {
            using var client = listener.AcceptTcpClient();
            var reply = Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Length: 2\r\nConnection: close\r\n\r\nok");
            client.GetStream().Write(reply, 0, reply.Length);
        }
    }
}
