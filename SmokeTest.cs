using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
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
    private const int ApiPort = 47391, ServerPort = 47392;
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

    private static async Task Steps()
    {
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

        var manager = new ServerManager();
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

        // Add, start through a .cmd shim, wait, and find the whole tree.
        var added = await Send("POST", "/v1/servers", $"{{\"name\":\"Smoke\",\"command\":\"lwtest\",\"args\":[\"{ServerPort}\"],\"port\":{ServerPort},\"cwd\":\"~\"}}");
        Check(added.Status == 201 && Field(added, "id") as string == "smoke", "POST /v1/servers adds \"smoke\": " + added.Status);
        Check((await Send("POST", "/v1/servers", "{\"name\":\"Smoke\",\"command\":\"lwtest\"}")).Status == 409, "same id again: 409");
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
        await Send("POST", "/v1/servers/crash/start");
        var crash = await Until("crash", "crashed", 10);
        Check(State(crash) == "crashed" && Field(crash, "status", "exitCode") is long code && code == 3 && Detail(crash).Contains("boom"), "a crash is reported with its exit code and last line: " + Detail(crash));
        await Send("POST", "/v1/servers", "{\"name\":\"Missing\",\"command\":\"definitely-not-a-command-lw\"}");
        var missing = await Send("POST", "/v1/servers/missing/start");
        Check(missing.Status == 400 && State(await Send("GET", "/v1/servers/missing")) == "failed", "a command that isn't on PATH: failed");
        string[] batchArgs = ["a b", "x&y", "100%", "^caret", "(paren)", "!bang!", "semi;colon"];
        await Send("POST", "/v1/servers", "{\"name\":\"Batch args\",\"command\":\"lwargs\",\"args\":" + Json.Write(batchArgs.ToList()) + "}");
        await Send("POST", "/v1/servers/batch-args/start");
        await Until("batch-args", "stopped", 10);
        var printed = Strings(await Send("GET", "/v1/servers/batch-args/logs"), "lines").Where(l => l.StartsWith("arg:")).Select(l => l.Substring(4)).ToList();
        Check(printed.SequenceEqual(batchArgs), "arguments reach a program behind a .cmd unchanged: " + string.Join(" | ", printed));
        string[] exeArgs = ["a b", @"back\slash\", "q\"uote", ""];
        await Send("POST", "/v1/servers", "{\"name\":\"Exe args\",\"command\":" + Json.Write(exe) + ",\"args\":" + Json.Write(new List<object?> { "--print-args" }.Concat(exeArgs).ToList()) + "}");
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
        Check((await Send("GET", "/v1/servers/hand-edited")).Status == 200, "an edit to servers.json is picked up");
        File.WriteAllText(Paths.ConfigFile, "{ broken");
        await Task.Delay(1500);
        var broken = await Send("GET", "/v1/servers");
        Check(Field(broken, "configError") as string == "servers.json: not valid JSON" && (Field(broken, "servers") as List<object?>)?.Count > 0,
            "broken servers.json is reported and the last good servers stay");
        File.WriteAllText(Paths.ConfigFile, good);
        await Task.Delay(1500);

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

        try { Directory.Delete(root, true); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    // MARK: HTTP

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
