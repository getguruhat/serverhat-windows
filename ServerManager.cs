using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32.SafeHandles;
using Timer = System.Windows.Forms.Timer;

namespace ServerHat;

/// Starts and stops the configured servers. Everything here runs on the UI thread;
/// output and exit notices from other threads are posted back to it.
internal sealed class ServerManager
{
    public List<ServerConfig> Servers { get; private set; } = [];
    public string? ConfigError { get; private set; }
    /// Raised after anything a person would see changes: a state, the list, an error.
    public event Action? Changed;
    /// Raised once per pending command fingerprint, to show a notification for it.
    public event Action<ServerConfig>? ApprovalNeeded;
    /// Fingerprints of what the user has allowed to run. See `NeedsApproval`.
    private HashSet<string> approved = [];
    private readonly HashSet<string> askedForApproval = [];

    private readonly Dictionary<string, ServerState> states = [];
    private readonly ConfigStore store = new();
    private readonly Dictionary<string, LogBuffer> logs = [];
    private readonly Dictionary<string, RunningProcess> running = [];
    private readonly HashSet<string> restartAfterExit = [];
    private readonly SynchronizationContext context;
    private readonly Timer ticker = new() { Interval = 1000 };
    private int ticks;

    private sealed class RunningProcess(Spawned spawned)
    {
        public Spawned Spawned { get; } = spawned;
        public int Pid => Spawned.Pid;
        public RegisteredWaitHandle? ExitWait { get; set; }
        public bool StopRequested { get; set; }
        public bool WaitingForPort { get; set; } = true;
        public bool Reaped { get; set; }
        public DateTime Started { get; } = DateTime.UtcNow;
    }

    public ServerManager()
    {
        context = SynchronizationContext.Current ?? throw new InvalidOperationException("ServerManager needs the UI thread's SynchronizationContext.");
        var file = store.Load();
        Servers = file.Servers;
        ConfigError = store.LoadError;
        store.OnChange = ConfigChangedOnDisk;
        store.Watch();
        foreach (var server in Servers) states[server.Id] = new ServerState();
        if (Settings.ApprovedCommands is { } saved)
        {
            approved = [.. saved];
        }
        else
        {
            // First run with approvals: everything already set up counts as allowed.
            approved = [.. Servers.Select(Fingerprint)];
            SaveApprovals();
        }
        // After the tray has subscribed to ApprovalNeeded.
        context.Post(_ => AskForPendingApprovals(), null);
        ticker.Tick += (_, _) => Tick();
        ticker.Start();
        foreach (var server in Servers.Where(s => s.StartAtLogin).ToList())
        {
            try { Start(server.Id); } catch (ManagerError) { }
        }
        foreach (var server in Servers) RefreshPort(server.Id);
    }

    // MARK: Lookup

    public ServerConfig? Server(string id) => Servers.FirstOrDefault(s => s.Id == id);
    public ServerState State(string id) => states.TryGetValue(id, out var state) ? state : new ServerState();
    public IEnumerable<ServerState> States => states.Values;
    public int RunningCount => running.Count;

    public LogBuffer Log(string id)
    {
        if (logs.TryGetValue(id, out var log)) return log;
        log = new LogBuffer(id);
        logs[id] = log;
        return log;
    }

    // MARK: Editing

    public ServerConfig Add(ServerConfig server, bool approvedByUser = false)
    {
        server = server.Copy();
        if (server.Id.Length == 0) server.Id = ServerConfig.Slug(server.Name);
        if (server.Problem is { } problem) throw new ManagerError(problem);
        if (Server(server.Id) is not null) throw new ManagerError($"a server with id \"{server.Id}\" already exists", 409);
        Servers.Add(server);
        states[server.Id] = new ServerState();
        if (approvedByUser) Approve(server.Id);
        Save();
        RefreshPort(server.Id);
        AskForPendingApprovals();
        return server;
    }

    /// Replaces a server's settings. A running server keeps its old settings until it restarts.
    public ServerConfig Update(string id, ServerConfig updated, bool approvedByUser = false)
    {
        var index = Servers.FindIndex(s => s.Id == id);
        if (index < 0) throw new ManagerError($"no server \"{id}\"", 404);
        updated = updated.Copy();
        updated.Id = id;
        if (updated.Problem is { } problem) throw new ManagerError(problem);
        Servers[index] = updated;
        if (approvedByUser) Approve(id);
        Save();
        AskForPendingApprovals();
        return updated;
    }

    public void Remove(string id)
    {
        var index = Servers.FindIndex(s => s.Id == id);
        if (index < 0) throw new ManagerError($"no server \"{id}\"", 404);
        Stop(id);
        Servers.RemoveAt(index);
        if (!running.ContainsKey(id)) states.Remove(id);
        Save();
    }

    /// Adds every configuration in a `.claude/launch.json` that isn't already here
    /// (same folder, command and arguments). Returns the servers it added.
    public List<ServerConfig> ImportLaunchJson(string path, bool approvedByUser = false)
    {
        List<ServerConfig> found;
        try { found = LaunchJsonImporter.Servers(path); }
        catch (Exception e) when (e is ConfigException or IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            throw new ManagerError("couldn't read launch.json: " + e.Message);
        }
        var added = new List<ServerConfig>();
        foreach (var candidate in found)
        {
            var duplicate = Servers.Any(s => string.Equals(s.WorkingDirectory.TrimEnd('\\'), candidate.WorkingDirectory.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)
                                             && s.Command == candidate.Command && s.Args.SequenceEqual(candidate.Args));
            if (duplicate) continue;
            var baseId = candidate.Id;
            var n = 2;
            while (Server(candidate.Id) is not null) candidate.Id = $"{baseId}-{n++}";
            added.Add(Add(candidate, approvedByUser));
        }
        return added;
    }

    public void ReloadConfig() => ConfigChangedOnDisk(store.Load());

    private void Save()
    {
        store.Save(new ConfigFile { Servers = Servers });
        ConfigError = null;
        Changed?.Invoke();
    }

    private void ConfigChangedOnDisk(ConfigFile file)
    {
        ConfigError = store.LoadError;
        if (store.LoadError is not null) { Changed?.Invoke(); return; }
        var newIds = new HashSet<string>(file.Servers.Select(s => s.Id));
        foreach (var old in Servers.Where(s => !newIds.Contains(s.Id)).ToList())
        {
            Stop(old.Id);
            if (!running.ContainsKey(old.Id)) states.Remove(old.Id);
        }
        Servers = file.Servers;
        foreach (var server in Servers.Where(s => !states.ContainsKey(s.Id)))
        {
            states[server.Id] = new ServerState();
            RefreshPort(server.Id);
        }
        AskForPendingApprovals();
        Changed?.Invoke();
    }

    // MARK: Approval

    /// True when a server's folder, command, arguments or environment is something the
    /// user hasn't allowed yet: it was added or changed through the API (so by an agent)
    /// or by editing servers.json. It can't start until the user clicks Allow. Changes
    /// made in ServerHat's own windows count as allowed.
    public bool NeedsApproval(string id) => Server(id) is { } server && !approved.Contains(Fingerprint(server));

    public List<ServerConfig> PendingApprovals => Servers.Where(s => NeedsApproval(s.Id)).ToList();

    public void Approve(string id)
    {
        if (Server(id) is not { } server) return;
        // Forget approvals for settings no server has any more.
        var current = new HashSet<string>(Servers.Select(Fingerprint));
        var fingerprint = Fingerprint(server);
        approved.IntersectWith(current);
        approved.Add(fingerprint);
        askedForApproval.Remove(fingerprint);
        SaveApprovals();
        Changed?.Invoke();
    }

    /// Sends one review notification for each pending command fingerprint.
    public void AskForApproval(string id)
    {
        if (Server(id) is not { } server || !NeedsApproval(id) || ApprovalNeeded is null) return;
        if (!askedForApproval.Add(Fingerprint(server))) return;
        ApprovalNeeded(server);
    }

    private void AskForPendingApprovals()
    {
        var pending = PendingApprovals;
        askedForApproval.IntersectWith(pending.Select(Fingerprint));
        foreach (var server in pending) AskForApproval(server.Id);
    }

    private void SaveApprovals()
    {
        try { Settings.ApprovedCommands = approved.OrderBy(f => f, StringComparer.Ordinal).ToList(); }
        catch (Exception e) when (e is System.Security.SecurityException or IOException or UnauthorizedAccessException) { }
    }

    public static string Fingerprint(ServerConfig server)
    {
        var parts = new[] { server.WorkingDirectory, server.Command }.Concat(server.Args)
            .Concat(server.Env.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => $"{p.Key}={p.Value}"));
        using var sha = SHA256.Create();
        var hash = sha.ComputeHash(Encoding.UTF8.GetBytes(Json.Write(parts.Cast<object?>().ToList())));
        return string.Concat(hash.Select(b => b.ToString("x2", CultureInfo.InvariantCulture)));
    }

    public static string CommandLine(ServerConfig server) => string.Join(" ", new[] { server.Command }.Concat(server.Args).Select(Spawner.Quote));

    // MARK: Running

    public void Start(string id)
    {
        var server = Server(id) ?? throw new ManagerError($"no server \"{id}\"", 404);
        if (NeedsApproval(id))
        {
            AskForApproval(id);
            throw new ManagerError($"Approval needed for {server.Name}: {CommandLine(server)} in {server.Cwd}. Click its ServerHat notification to review the command and Allow it, or open the ServerHat menu.", 403);
        }
        if (running.TryGetValue(id, out var current))
        {
            // Start while it's still stopping means "start it again once it has stopped".
            if (current.StopRequested) restartAfterExit.Add(id);
            return;
        }
        if (server.Problem is { } problem)
        {
            SetState(id, new ServerState { Status = ServerStatus.Failed, Detail = problem });
            throw new ManagerError(problem);
        }
        if (server.Port is { } busyPort && PortProbe.IsListening(busyPort))
        {
            SetState(id, new ServerState { Status = ServerStatus.PortInUse, Detail = $"Port {busyPort} is already in use." });
            DescribePortOwner(id, busyPort);
            throw new ManagerError($"port {busyPort} is already in use", 409);
        }

        var log = Log(id);
        log.BeginRun($"=== {Timestamp()} Starting in {server.Cwd}: {CommandLine(server)}");
        Spawned spawned;
        try
        {
            spawned = Spawner.Spawn(server, Spawner.Environment(server));
        }
        catch (ManagerError e)
        {
            log.EndRun("=== Couldn't start: " + e.Message);
            SetState(id, new ServerState { Status = ServerStatus.Failed, Detail = e.Message });
            throw;
        }

        var process = new RunningProcess(spawned) { WaitingForPort = server.Port is not null };
        running[id] = process;
        ReadOutput(spawned.Output, log);
        process.ExitWait = ThreadPool.RegisterWaitForSingleObject(new Native.ProcessWaitHandle(spawned.Process),
            (_, _) => context.Post(_ => Reap(id, process), null), null, Timeout.Infinite, true);
        SetState(id, new ServerState
        {
            Status = server.Port is null ? ServerStatus.Running : ServerStatus.Starting,
            Detail = server.Port is { } port ? $"Waiting for port {port}…" : null,
            Pid = spawned.Pid,
            StartedAt = process.Started,
        });
    }

    /// Reads the server's output on a thread of its own and hands it to the UI thread.
    private void ReadOutput(SafeFileHandle output, LogBuffer log)
    {
        var thread = new Thread(() =>
        {
            using var stream = new FileStream(output, FileAccess.Read, 4096, false);
            var buffer = new byte[8192];
            while (true)
            {
                int count;
                try { count = stream.Read(buffer, 0, buffer.Length); }
                catch (IOException) { break; }
                if (count <= 0) break;
                var data = new byte[count];
                Array.Copy(buffer, data, count);
                context.Post(_ => log.Append(data), null);
            }
        }) { IsBackground = true, Name = "ServerHat output" };
        thread.Start();
    }

    public void Stop(string id)
    {
        restartAfterExit.Remove(id);
        if (!running.TryGetValue(id, out var process))
        {
            if (states.TryGetValue(id, out var state) && state.Status is ServerStatus.Crashed or ServerStatus.Failed)
            {
                states[id] = new ServerState();
                RefreshPort(id);
                Changed?.Invoke();
            }
            return;
        }
        if (process.StopRequested) return;
        process.StopRequested = true;
        SetState(id, State(id) with { Status = ServerStatus.Stopping, Detail = null });
        // Ctrl+Break reaches every console program in the server's process group, which
        // is how a terminal would stop it. Whatever is still running after five seconds
        // is ended with the rest of its Job Object.
        SendCtrlBreak(process.Pid);
        After(5000, () =>
        {
            if (!running.TryGetValue(id, out var still) || still != process) return;
            Log(id).Append("=== Still running after 5 seconds; ending its process tree");
            Native.TerminateJobObject(process.Spawned.Job, 1);
        });
    }

    public void Restart(string id)
    {
        if (Server(id) is null) throw new ManagerError($"no server \"{id}\"", 404);
        if (NeedsApproval(id)) Start(id); // explains, without stopping the old one
        if (running.ContainsKey(id))
        {
            Stop(id);
            restartAfterExit.Add(id);
        }
        else
        {
            if (State(id).Status != ServerStatus.PortInUse) states[id] = new ServerState();
            Start(id);
        }
    }

    public void StartAll()
    {
        foreach (var server in Servers.Where(s => !running.ContainsKey(s.Id) && !NeedsApproval(s.Id)).ToList())
        {
            try { Start(server.Id); } catch (ManagerError) { }
        }
    }

    public void StopAll()
    {
        foreach (var id in running.Keys.ToList()) Stop(id);
    }

    /// Called when ServerHat quits: ask everything to stop, give it a moment, then end the rest.
    public void Shutdown()
    {
        ticker.Stop();
        var processes = running.Values.ToList();
        foreach (var process in processes) SendCtrlBreak(process.Pid);
        var deadline = DateTime.UtcNow.AddSeconds(3);
        foreach (var process in processes)
        {
            var left = Math.Max(0, (int)(deadline - DateTime.UtcNow).TotalMilliseconds);
            Native.WaitForSingleObject(process.Spawned.Process, (uint)left);
        }
        foreach (var process in processes)
        {
            Native.TerminateJobObject(process.Spawned.Job, 1);
            process.ExitWait?.Unregister(null);
        }
    }

    private void Reap(string id, RunningProcess process)
    {
        if (process.Reaped || !running.TryGetValue(id, out var current) || current != process) return;
        if (Native.WaitForSingleObject(process.Spawned.Process, 0) != Native.WAIT_OBJECT_0) return;
        process.Reaped = true;
        process.ExitWait?.Unregister(null);
        Native.GetExitCodeProcess(process.Spawned.Process, out var raw);
        // Children the server left behind would otherwise keep the port busy.
        Native.TerminateJobObject(process.Spawned.Job, 1);
        Native.CloseHandle(process.Spawned.Job);
        Native.CloseHandle(process.Spawned.Process);
        running.Remove(id);

        var code = unchecked((int)raw);
        var how = raw >= 0xC0000000 ? $"exit code 0x{raw:X8}" : $"exit code {code}";
        var log = Log(id);
        // Let the last output arrive before writing the footer.
        After(200, () => log.EndRun($"=== {Timestamp()} Exited with {how}"));

        if (Server(id) is null) { states.Remove(id); Changed?.Invoke(); return; }
        if (process.StopRequested)
        {
            SetState(id, new ServerState());
        }
        else if (code == 0)
        {
            SetState(id, new ServerState { Status = ServerStatus.Stopped, Detail = "Exited on its own.", ExitCode = 0 });
        }
        else
        {
            var lastLine = log.Lines.LastOrDefault(l => l.Trim().Length > 0 && !l.StartsWith("==="));
            var detail = raw >= 0xC0000000 ? $"Crashed with exit code 0x{raw:X8}." : $"Crashed with exit code {code}.";
            if (lastLine is not null) detail += " " + (lastLine.Length > 160 ? lastLine.Substring(0, 160) : lastLine);
            SetState(id, new ServerState { Status = ServerStatus.Crashed, Detail = detail, ExitCode = code });
        }
        if (restartAfterExit.Remove(id))
            After(300, () => { try { Start(id); } catch (ManagerError) { } });
        else
            RefreshPort(id);
    }

    private void Tick()
    {
        foreach (var pair in running.ToList())
        {
            var (id, process) = (pair.Key, pair.Value);
            Reap(id, process); // in case the exit notice was missed
            if (!running.TryGetValue(id, out var still) || still != process || !process.WaitingForPort || process.StopRequested) continue;
            if (Server(id)?.Port is not { } port) continue;
            if (PortProbe.IsListening(port))
            {
                process.WaitingForPort = false;
                SetState(id, State(id) with { Status = ServerStatus.Running, Detail = null });
            }
            else if ((DateTime.UtcNow - process.Started).TotalSeconds > 90)
            {
                process.WaitingForPort = false;
                SetState(id, State(id) with { Status = ServerStatus.Running, Detail = $"Running, but nothing is listening on port {port}." });
            }
        }
        // Every few seconds, notice ports that something outside ServerHat opened or closed.
        if (++ticks % 3 == 0)
            foreach (var server in Servers.Where(s => !running.ContainsKey(s.Id)).ToList()) RefreshPort(server.Id);
    }

    private void RefreshPort(string id)
    {
        if (running.ContainsKey(id) || Server(id) is not { Port: { } port } || !states.TryGetValue(id, out var state)) return;
        var busy = PortProbe.IsListening(port);
        if (busy && state.Status == ServerStatus.Stopped)
        {
            SetState(id, new ServerState { Status = ServerStatus.PortInUse, Detail = $"Port {port} is already in use." });
            DescribePortOwner(id, port);
        }
        else if (!busy && state.Status == ServerStatus.PortInUse)
        {
            SetState(id, new ServerState());
        }
    }

    private void DescribePortOwner(string id, int port)
    {
        Task.Run(() =>
        {
            if (PortProbe.Owner(port) is not { } owner) return;
            context.Post(_ =>
            {
                if (State(id).Status != ServerStatus.PortInUse) return;
                SetState(id, State(id) with { Detail = $"Port {port} is in use by {owner.Command} (pid {owner.Pid}).", Pid = owner.Pid });
            }, null);
        });
    }

    private void SetState(string id, ServerState state)
    {
        if (states.TryGetValue(id, out var old) && old == state) return;
        states[id] = state;
        Changed?.Invoke();
    }

    /// Kept in a field so the garbage collector doesn't free it while Windows holds it.
    private static readonly Native.ConsoleCtrlHandler IgnoreCtrl = _ => true;

    /// Sends Ctrl+Break to a server's process group by briefly sharing its console.
    /// ServerHat ignores the event itself.
    private static void SendCtrlBreak(int pid)
    {
        Native.FreeConsole();
        if (!Native.AttachConsole((uint)pid)) return;
        Native.SetConsoleCtrlHandler(IgnoreCtrl, true);
        Native.GenerateConsoleCtrlEvent(Native.CTRL_BREAK_EVENT, (uint)pid);
        Native.FreeConsole();
    }

    /// Ends a process ServerHat didn't start (the one holding a server's port), after the person confirms.
    public static void EndProcess(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            process.Kill();
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception) { }
    }

    private static async void After(int milliseconds, Action action)
    {
        await Task.Delay(milliseconds);
        action();
    }

    public static string Timestamp() => DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
}
