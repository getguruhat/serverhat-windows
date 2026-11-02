using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;

namespace ServerHat;

/// A small JSON API on 127.0.0.1 so Claude Code, Codex and scripts can configure and
/// control ServerHat. It answers exactly like the macOS version (same routes, JSON and
/// errors). See README.md › API.
///
/// Every request except GET /v1/health needs `Authorization: Bearer <token>`, where the
/// token is in a file only this user can read. Requests with another Host header (DNS
/// rebinding) or from a web page (an Origin or cross-site Sec-Fetch-Site header) are refused.
internal sealed class ApiServer
{
    public const int DefaultPort = 47317;
    public static string TokenPath => Path.Combine(Paths.ConfigFolder, "api-token");
    public static string InfoPath => Path.Combine(Paths.ConfigFolder, "api.json");
    private const int MaxBody = 1 << 20;
    private static readonly byte[] HeaderEnd = Encoding.ASCII.GetBytes("\r\n\r\n");

    public int Port { get; }
    public string? Problem { get; private set; }
    public string BaseUrl => $"http://127.0.0.1:{Port}";
    private readonly ServerManager manager;
    private readonly string token;
    private TcpListener? listener;

    public ApiServer(ServerManager manager, int? port = null)
    {
        this.manager = manager;
        Port = port ?? Settings.ApiPort;
        token = TokenFile.LoadOrCreate(TokenPath);
    }

    /// Call on the UI thread: requests are answered there, next to the ServerManager.
    public void Start()
    {
        try
        {
            var socket = new TcpListener(IPAddress.Loopback, Port);
            // No other program can take the port over while ServerHat has it.
            if (Environment.OSVersion.Platform == PlatformID.Win32NT) socket.ExclusiveAddressUse = true;
            socket.Start();
            listener = socket;
        }
        catch (SocketException e)
        {
            Problem = $"API couldn't listen on 127.0.0.1:{Port}: {e.Message}";
            return;
        }
        WriteInfo();
        AcceptLoop();
    }

    public void Stop()
    {
        var socket = listener;
        listener = null;
        socket?.Stop();
    }

    public static string Version => typeof(ApiServer).Assembly.GetName().Version?.ToString(3) ?? "dev";

    private void WriteInfo()
    {
        var info = new Dictionary<string, object?>
        {
            ["url"] = BaseUrl,
            ["tokenFile"] = TokenPath,
            ["configFile"] = Paths.ConfigFile,
            ["docs"] = "https://github.com/getguruhat/serverhat-windows#api",
            ["version"] = Version,
        };
        try { ConfigStore.WriteAtomically(InfoPath, Encoding.UTF8.GetBytes(Json.Write(info))); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    // MARK: HTTP

    internal sealed record Request(string Method, string Path, Dictionary<string, string> Query, Dictionary<string, string> Headers, byte[] Body);

    internal sealed record Response(int Status, object? Body)
    {
        public static Response Error(int status, string message) => new(status, new Dictionary<string, object?> { ["error"] = message });
    }

    private async void AcceptLoop()
    {
        while (listener is { } socket)
        {
            TcpClient client;
            try { client = await socket.AcceptTcpClientAsync(); }
            catch (ObjectDisposedException) { return; }
            catch (InvalidOperationException) { return; }
            catch (SocketException) { continue; }
            Serve(client);
        }
    }

    private async void Serve(TcpClient client)
    {
        using (client)
        {
            try
            {
                var stream = client.GetStream();
                // Drop connections that never finish sending a request. (Once it has arrived,
                // a ?wait= of up to 60 seconds may keep the connection open.)
                using var timeout = new CancellationTokenSource(30_000);
                var dropSlowClient = timeout.Token.Register(client.Close);
                var buffer = new MemoryStream();
                var chunk = new byte[65536];
                Response response;
                while (true)
                {
                    var count = await stream.ReadAsync(chunk, 0, chunk.Length);
                    buffer.Write(chunk, 0, count);
                    var parsed = Parse(buffer.ToArray(), out var bad);
                    if (parsed is null && bad is null && count > 0 && buffer.Length <= MaxBody + 65536) continue;
                    dropSlowClient.Dispose();
                    response = parsed is not null ? await Handle(parsed)
                        : Response.Error(400, bad ?? "incomplete or oversized request");
                    break;
                }
                var bytes = Encode(response);
                await stream.WriteAsync(bytes, 0, bytes.Length);
            }
            catch (Exception e) when (e is IOException or ObjectDisposedException or SocketException or InvalidOperationException) { }
        }
    }

    /// The request, or null with `bad` set when it's malformed, or null with `bad` null when more is needed.
    internal static Request? Parse(byte[] data, out string? bad)
    {
        bad = null;
        var end = IndexOf(data, HeaderEnd);
        if (end < 0) return null;
        string head;
        try { head = new UTF8Encoding(false, true).GetString(data, 0, end); }
        catch (DecoderFallbackException) { bad = "headers aren't UTF-8"; return null; }
        var lines = head.Split(["\r\n"], StringSplitOptions.None);
        var parts = lines[0].Split([' '], StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 3) { bad = "bad request line"; return null; }
        var headers = new Dictionary<string, string>();
        foreach (var line in lines.Skip(1))
        {
            var colon = line.IndexOf(':');
            if (colon < 0) continue;
            headers[line.Substring(0, colon).Trim(' ', '\t').ToLowerInvariant()] = line.Substring(colon + 1).Trim(' ', '\t');
        }
        var length = int.TryParse(headers.TryGetValue("content-length", out var declared) ? declared : "0",
            NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var n) ? n : -1;
        if (length < 0 || length > MaxBody) { bad = "bad Content-Length"; return null; }
        if (headers.ContainsKey("transfer-encoding")) { bad = "chunked bodies aren't supported; send Content-Length"; return null; }
        var bodyStart = end + HeaderEnd.Length;
        if (data.Length - bodyStart < length) return null;
        var body = new byte[length];
        Array.Copy(data, bodyStart, body, 0, length);

        var target = parts[1];
        var hash = target.IndexOf('#');
        if (hash >= 0) target = target.Substring(0, hash);
        var mark = target.IndexOf('?');
        var path = mark >= 0 ? target.Substring(0, mark) : target;
        var query = new Dictionary<string, string>();
        if (mark >= 0)
        {
            foreach (var item in target.Substring(mark + 1).Split('&'))
            {
                var eq = item.IndexOf('=');
                var name = eq >= 0 ? item.Substring(0, eq) : item;
                query[Unescape(name)] = eq >= 0 ? Unescape(item.Substring(eq + 1)) : "";
            }
        }
        return new Request(parts[0].ToUpperInvariant(), path, query, headers, body);
    }

    private static string Unescape(string text)
    {
        try { return Uri.UnescapeDataString(text); } catch (UriFormatException) { return text; }
    }

    private static int IndexOf(byte[] data, byte[] pattern)
    {
        for (var i = 0; i <= data.Length - pattern.Length; i++)
        {
            var j = 0;
            while (j < pattern.Length && data[i + j] == pattern[j]) j++;
            if (j == pattern.Length) return i;
        }
        return -1;
    }

    internal static byte[] Encode(Response response)
    {
        var json = Encoding.UTF8.GetBytes(Json.Write(response.Body) + "\n");
        var head = $"HTTP/1.1 {response.Status} {Reason(response.Status)}\r\n"
                   + "Content-Type: application/json; charset=utf-8\r\n"
                   + $"Content-Length: {json.Length}\r\n"
                   + "Cache-Control: no-store\r\n"
                   + "X-Content-Type-Options: nosniff\r\n"
                   + "Connection: close\r\n\r\n";
        return Encoding.ASCII.GetBytes(head).Concat(json).ToArray();
    }

    /// The reason phrases the macOS version sends (Apple's names for the codes, capitalized).
    private static string Reason(int status) => status switch
    {
        200 => "No Error",
        201 => "Created",
        400 => "Bad Request",
        401 => "Unauthorized",
        403 => "Forbidden",
        404 => "Not Found",
        405 => "Method Not Allowed",
        409 => "Conflict",
        415 => "Unsupported Media Type",
        _ => "Internal Server Error",
    };

    // MARK: Checks

    private Response? Refusal(Request request)
    {
        var allowedHosts = new[] { $"127.0.0.1:{Port}", $"localhost:{Port}" };
        if (!request.Headers.TryGetValue("host", out var host) || !allowedHosts.Contains(host.ToLowerInvariant()))
            return Response.Error(403, $"Host must be 127.0.0.1:{Port}");
        if (request.Headers.ContainsKey("origin"))
            return Response.Error(403, "requests from web pages aren't allowed");
        if (request.Headers.TryGetValue("sec-fetch-site", out var site) && site.ToLowerInvariant() != "none")
            return Response.Error(403, "requests from web pages aren't allowed");
        if (request.Method == "OPTIONS") return Response.Error(403, "CORS isn't supported");
        if (request.Path == "/v1/health") return null;
        if (!request.Headers.TryGetValue("authorization", out var auth) || !auth.StartsWith("Bearer ", StringComparison.Ordinal)
            || !ConstantTimeEqual(auth.Substring(7).Trim(' ', '\t'), token))
            return Response.Error(401, $"missing or wrong token; send Authorization: Bearer $(Get-Content '{TokenPath}')");
        if (request.Method is "POST" or "PUT" or "PATCH" && request.Body.Length > 0
            && !(request.Headers.TryGetValue("content-type", out var type) && type.ToLowerInvariant().StartsWith("application/json", StringComparison.Ordinal)))
            return Response.Error(415, "send JSON with Content-Type: application/json");
        return null;
    }

    private static bool ConstantTimeEqual(string a, string b)
    {
        var x = Encoding.UTF8.GetBytes(a);
        var y = Encoding.UTF8.GetBytes(b);
        if (x.Length != y.Length) return false;
        var diff = 0;
        for (var i = 0; i < x.Length; i++) diff |= x[i] ^ y[i];
        return diff == 0;
    }

    // MARK: Routes

    internal async Task<Response> Handle(Request request)
    {
        if (Refusal(request) is { } refusal) return refusal;
        var segments = request.Path.Split(['/'], StringSplitOptions.RemoveEmptyEntries).Select(Unescape).ToList();
        if (segments.FirstOrDefault() != "v1") return Response.Error(404, "unknown path; the API lives under /v1");
        var route = segments.Skip(1).ToList();
        try
        {
            switch (request.Method, route.Count)
            {
                case ("GET", 1) when route[0] == "health":
                    return new Response(200, new Dictionary<string, object?> { ["ok"] = true, ["app"] = "ServerHat" });
                case ("GET", 1) when route[0] == "servers":
                    return new Response(200, new Dictionary<string, object?>
                    {
                        ["servers"] = AllServers(), ["configFile"] = Paths.ConfigFile, ["configError"] = manager.ConfigError,
                    });
                case ("POST", 1) when route[0] == "servers":
                {
                    var config = DecodeServer(JsonObject(request.Body));
                    if (config.Id.Length == 0) config.Id = ServerConfig.Slug(config.Name);
                    return new Response(201, Describe(manager.Add(config)));
                }
                case ("POST", 1) when route[0] == "import":
                {
                    var body = JsonObject(request.Body);
                    if (!body.TryGetValue("path", out var value) || value is not string path)
                        throw new ManagerError("send {\"path\": \"/folder/with/.claude/launch.json\"}");
                    var added = manager.ImportLaunchJson(path);
                    return new Response(200, new Dictionary<string, object?> { ["added"] = added.Select(s => (object?)Describe(s)).ToList() });
                }
                case ("POST", 1) when route[0] == "start-all":
                    manager.StartAll();
                    return new Response(200, new Dictionary<string, object?> { ["servers"] = AllServers() });
                case ("POST", 1) when route[0] == "stop-all":
                    manager.StopAll();
                    return new Response(200, new Dictionary<string, object?> { ["servers"] = AllServers() });
                case ("POST", 1) when route[0] == "reload":
                    manager.ReloadConfig();
                    return new Response(200, new Dictionary<string, object?> { ["servers"] = AllServers(), ["configError"] = manager.ConfigError });
                case (_, 2) when route[0] == "servers":
                    return HandleServer(route[1], request);
                case ("GET", 3) when route[0] == "servers" && route[2] == "logs":
                {
                    var id = route[1];
                    if (manager.Server(id) is null) throw new ManagerError($"no server \"{id}\"", 404);
                    var count = Math.Min(5000, request.Query.TryGetValue("lines", out var lines) && int.TryParse(lines, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var n) ? n : 200);
                    var log = manager.Log(id);
                    return new Response(200, new Dictionary<string, object?>
                    {
                        ["id"] = id, ["lines"] = log.Tail(count).Cast<object?>().ToList(), ["file"] = log.FilePath,
                    });
                }
                case ("POST", 3) when route[0] == "servers" && route[2] is "start" or "stop" or "restart":
                {
                    var id = route[1];
                    if (manager.Server(id) is null) throw new ManagerError($"no server \"{id}\"", 404);
                    var wait = Math.Min(60, request.Query.TryGetValue("wait", out var text) && double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var w) ? w : 0);
                    if (route[2] != "stop" && manager.NeedsApproval(id) && wait > 0)
                    {
                        // Give the user the wait time to click Allow, then go ahead (or explain).
                        manager.AskForApproval(id);
                        await WaitForApproval(id, wait);
                    }
                    switch (route[2])
                    {
                        case "start": manager.Start(id); break;
                        case "stop": manager.Stop(id); break;
                        default: manager.Restart(id); break;
                    }
                    await WaitUntilSettled(id, wait);
                    return manager.Server(id) is { } server ? new Response(200, Describe(server)) : Response.Error(404, $"no server \"{id}\"");
                }
                default:
                    return Response.Error(404, $"no route for {request.Method} {request.Path}");
            }
        }
        catch (ManagerError e) { return Response.Error(e.Status, e.Message); }
        catch (ConfigException e) { return Response.Error(400, e.Message); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { return Response.Error(400, e.Message); }
    }

    private Response HandleServer(string id, Request request)
    {
        var current = manager.Server(id) ?? throw new ManagerError($"no server \"{id}\"", 404);
        switch (request.Method)
        {
            case "GET":
                return new Response(200, Describe(current));
            case "PATCH" or "PUT":
            {
                var body = JsonObject(request.Body);
                if (body.TryGetValue("id", out var newId) && newId is string changed && changed != id)
                    throw new ManagerError("id can't be changed; remove and add instead");
                Dictionary<string, object?> merged;
                if (request.Method == "PATCH")
                {
                    merged = current.ToJson();
                    // null clears an optional setting such as "port" or "url".
                    foreach (var pair in body)
                        if (pair.Value is null) merged.Remove(pair.Key); else merged[pair.Key] = pair.Value;
                }
                else merged = new Dictionary<string, object?>(body);
                merged["id"] = id;
                var updated = manager.Update(id, DecodeServer(merged));
                var description = Describe(updated);
                if (manager.State(id).Pid is not null && !updated.SameAs(current)) description["note"] = "restart the server to use the new settings";
                return new Response(200, description);
            }
            case "DELETE":
                manager.Remove(id);
                return new Response(200, new Dictionary<string, object?> { ["removed"] = id });
            default:
                return Response.Error(405, "use GET, PATCH, PUT or DELETE");
        }
    }

    /// Returns once the user has allowed the server (or it's gone), or after `seconds`.
    private async Task WaitForApproval(string id, double seconds)
    {
        var deadline = DateTime.UtcNow.AddSeconds(double.IsNaN(seconds) ? 0 : seconds);
        while (DateTime.UtcNow < deadline && manager.NeedsApproval(id)) await Task.Delay(250);
    }

    /// Returns once the server is no longer starting or stopping, or after `seconds`.
    private async Task WaitUntilSettled(string id, double seconds)
    {
        var deadline = DateTime.UtcNow.AddSeconds(double.IsNaN(seconds) ? 0 : seconds);
        // A restart passes through "stopped" before starting again.
        await Task.Delay(seconds > 0 ? 500 : 0);
        while (DateTime.UtcNow < deadline && manager.State(id).Status is ServerStatus.Starting or ServerStatus.Stopping)
            await Task.Delay(250);
    }

    // MARK: JSON

    private static Dictionary<string, object?> JsonObject(byte[] data)
    {
        if (data.Length == 0) return [];
        try
        {
            if (ConfigStore.ParseFile(data) is Dictionary<string, object?> body) return body;
        }
        catch (ConfigException) { }
        throw new ManagerError("body must be a JSON object");
    }

    private static ServerConfig DecodeServer(Dictionary<string, object?> body)
    {
        var values = new Dictionary<string, object?>(body);
        values.Remove("status");
        values.Remove("log");
        values.Remove("note");
        if (!values.ContainsKey("id")) values["id"] = "";
        return ServerConfig.FromJson(new JsonReader(values, ""));
    }

    private List<object?> AllServers() => manager.Servers.Select(s => (object?)Describe(s)).ToList();

    private Dictionary<string, object?> Describe(ServerConfig server)
    {
        var json = server.ToJson();
        var state = manager.State(server.Id);
        var status = new Dictionary<string, object?> { ["state"] = state.Status.ApiName() };
        if (state.Detail is { } detail) status["detail"] = detail;
        if (state.Pid is { } pid) status["pid"] = pid;
        if (state.StartedAt is { } started) status["startedAt"] = started.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
        if (state.ExitCode is { } code) status["exitCode"] = code;
        json["status"] = status;
        if (manager.NeedsApproval(server.Id))
        {
            json["needsApproval"] = true;
            json["approvalNote"] = "New or changed command: it can't start until the user clicks Allow in the ServerHat menu.";
        }
        json["log"] = manager.Log(server.Id).FilePath;
        if (server.OpenUrl is { } url) json["openURL"] = url;
        return json;
    }
}
