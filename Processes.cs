using System.ComponentModel;
using System.Diagnostics;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

namespace ServerHat;

/// A started server: its root process, the Job Object that holds it and everything it
/// starts, and the read end of its output pipe.
internal sealed record Spawned(int Pid, IntPtr Process, IntPtr Job, SafeFileHandle Output);

/// Starts a server in its own Job Object (so Stop also ends whatever it started: npm →
/// node → vite) and its own console process group (so it can be asked to stop with
/// Ctrl+Break first). It inherits no handles except its output pipe and NUL for input.
internal static class Spawner
{
    public static Spawned Spawn(ServerConfig server, Dictionary<string, string> environment)
    {
        var cwd = server.WorkingDirectory;
        if (!Directory.Exists(cwd)) throw new ManagerError($"The folder {server.Cwd} doesn't exist.");
        var executable = Resolve(Paths.Expand(server.Command.Trim()), cwd, environment.TryGetValue("PATH", out var path) ? path : "",
            environment.TryGetValue("PATHEXT", out var pathext) ? pathext : ".COM;.EXE;.BAT;.CMD")
            ?? throw new ManagerError($"Couldn't find \"{server.Command}\" on your PATH.");

        string application;
        string commandLine;
        var extension = Path.GetExtension(executable).ToLowerInvariant();
        if (extension is ".cmd" or ".bat")
        {
            // Batch files (npm.cmd, pnpm.cmd, yarn.cmd) run in cmd.exe, which has its own quoting.
            application = ComSpec();
            commandLine = Quote(application) + " /d /e:on /v:off /s /c \"" + Quote(executable) + string.Concat(server.Args.Select(a => " " + BatchQuote(a))) + "\"";
        }
        else if (extension is ".exe" or ".com")
        {
            application = executable;
            commandLine = Quote(executable) + string.Concat(server.Args.Select(a => " " + Quote(a)));
        }
        else throw new ManagerError($"\"{executable}\" isn't a program Windows can run (.exe, .cmd, .bat or .com).");

        var inheritable = new Native.SECURITY_ATTRIBUTES { nLength = Marshal.SizeOf<Native.SECURITY_ATTRIBUTES>(), bInheritHandle = 1 };
        if (!Native.CreatePipe(out var readPipe, out var writePipe, ref inheritable, 0)) throw Win32("Couldn't create a pipe");
        Native.SetHandleInformation(readPipe, Native.HANDLE_FLAG_INHERIT, 0);
        var nul = Native.CreateFile("NUL", Native.GENERIC_READ, Native.FILE_SHARE_READ | Native.FILE_SHARE_WRITE, ref inheritable, Native.OPEN_EXISTING, 0, IntPtr.Zero);

        var handles = Marshal.AllocHGlobal(IntPtr.Size * 2);
        var attributeList = IntPtr.Zero;
        var environmentBlock = IntPtr.Zero;
        var job = IntPtr.Zero;
        try
        {
            // Only these two handles are passed on, even though others are inheritable.
            Marshal.WriteIntPtr(handles, 0, writePipe);
            Marshal.WriteIntPtr(handles, IntPtr.Size, nul);
            var size = IntPtr.Zero;
            Native.InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref size);
            attributeList = Marshal.AllocHGlobal(size);
            if (!Native.InitializeProcThreadAttributeList(attributeList, 1, 0, ref size)) throw Win32("Couldn't prepare the process");
            if (!Native.UpdateProcThreadAttribute(attributeList, 0, Native.PROC_THREAD_ATTRIBUTE_HANDLE_LIST, handles, (IntPtr)(IntPtr.Size * 2), IntPtr.Zero, IntPtr.Zero))
                throw Win32("Couldn't prepare the process");

            var info = new Native.STARTUPINFOEX();
            info.StartupInfo.cb = Marshal.SizeOf<Native.STARTUPINFOEX>();
            info.StartupInfo.dwFlags = Native.STARTF_USESTDHANDLES | Native.STARTF_USESHOWWINDOW;
            info.StartupInfo.wShowWindow = 0; // SW_HIDE, for servers that open a window of their own
            info.StartupInfo.hStdInput = nul;
            info.StartupInfo.hStdOutput = writePipe;
            info.StartupInfo.hStdError = writePipe;
            info.lpAttributeList = attributeList;

            environmentBlock = Marshal.StringToHGlobalUni(EnvironmentBlock(environment));

            job = Native.CreateJobObject(IntPtr.Zero, null);
            if (job == IntPtr.Zero) throw Win32("Couldn't create a Job Object");
            var limits = new Native.JOBOBJECT_EXTENDED_LIMIT_INFORMATION();
            // If ServerHat itself ends, Windows ends the servers too.
            limits.BasicLimitInformation.LimitFlags = Native.JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE;
            Native.SetInformationJobObject(job, Native.JobObjectExtendedLimitInformation, ref limits, Marshal.SizeOf<Native.JOBOBJECT_EXTENDED_LIMIT_INFORMATION>());

            var flags = Native.CREATE_SUSPENDED | Native.CREATE_NEW_PROCESS_GROUP | Native.CREATE_NO_WINDOW
                        | Native.CREATE_UNICODE_ENVIRONMENT | Native.EXTENDED_STARTUPINFO_PRESENT;
            if (!Native.CreateProcess(application, new StringBuilder(commandLine), IntPtr.Zero, IntPtr.Zero, true, flags,
                    environmentBlock, cwd, ref info, out var process))
                throw Win32($"Couldn't start {executable}");
            // Suspended until it's in the job, so nothing it starts can slip out.
            if (!Native.AssignProcessToJobObject(job, process.hProcess))
            {
                var error = Win32("Couldn't put the server in a Job Object");
                Native.TerminateProcess(process.hProcess, 1);
                Native.CloseHandle(process.hThread);
                Native.CloseHandle(process.hProcess);
                throw error;
            }
            Native.ResumeThread(process.hThread);
            Native.CloseHandle(process.hThread);
            var spawned = new Spawned(process.dwProcessId, process.hProcess, job, new SafeFileHandle(readPipe, true));
            job = IntPtr.Zero;
            readPipe = IntPtr.Zero;
            return spawned;
        }
        finally
        {
            if (job != IntPtr.Zero) Native.CloseHandle(job);
            if (readPipe != IntPtr.Zero) Native.CloseHandle(readPipe);
            // The child has its own copies now; the pipe reports end of file once they're all closed.
            Native.CloseHandle(writePipe);
            Native.CloseHandle(nul);
            if (attributeList != IntPtr.Zero) { Native.DeleteProcThreadAttributeList(attributeList); Marshal.FreeHGlobal(attributeList); }
            Marshal.FreeHGlobal(handles);
            if (environmentBlock != IntPtr.Zero) Marshal.FreeHGlobal(environmentBlock);
        }
    }

    private static ManagerError Win32(string what) => new($"{what}: {new Win32Exception(Marshal.GetLastWin32Error()).Message}");

    private static string ComSpec()
    {
        var comspec = System.Environment.GetEnvironmentVariable("ComSpec");
        return !string.IsNullOrEmpty(comspec) && File.Exists(comspec) ? comspec! : Path.Combine(System.Environment.SystemDirectory, "cmd.exe");
    }

    /// The environment the server sees: ServerHat's own, with PATH read fresh from the
    /// registry (so a Node or Python installed after sign-in is found without signing out),
    /// then the server's `env`.
    public static Dictionary<string, string> Environment(ServerConfig server)
    {
        var env = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (System.Collections.DictionaryEntry entry in System.Environment.GetEnvironmentVariables())
            env[(string)entry.Key] = (string?)entry.Value ?? "";
        env["PATH"] = CurrentPath(env.TryGetValue("PATH", out var own) ? own : "");
        foreach (var pair in server.Env) env[pair.Key] = pair.Value;
        return env;
    }

    /// The machine PATH, then the user's PATH, then anything else ServerHat's own PATH has.
    private static string CurrentPath(string own)
    {
        string Read(RegistryKey root, string key)
        {
            try
            {
                using var k = root.OpenSubKey(key);
                return k?.GetValue("Path", "", RegistryValueOptions.DoNotExpandEnvironmentNames) is string value ? System.Environment.ExpandEnvironmentVariables(value) : "";
            }
            catch (Exception e) when (e is System.Security.SecurityException or IOException or UnauthorizedAccessException) { return ""; }
        }
        var parts = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var list in new[] { Read(Registry.LocalMachine, @"SYSTEM\CurrentControlSet\Control\Session Manager\Environment"), Read(Registry.CurrentUser, "Environment"), own })
            foreach (var dir in list.Split([';'], StringSplitOptions.RemoveEmptyEntries).Select(d => d.Trim()).Where(d => d.Length > 0))
                if (seen.Add(dir.TrimEnd('\\'))) parts.Add(dir);
        return string.Join(";", parts);
    }

    /// "A=1\0B=2\0\0", sorted the way CreateProcess expects.
    private static string EnvironmentBlock(Dictionary<string, string> env)
    {
        var block = new StringBuilder();
        foreach (var pair in env.Where(p => p.Key.Length > 0 && !p.Key.Contains("=")).OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase))
            block.Append(pair.Key).Append('=').Append(pair.Value).Append('\0');
        if (block.Length == 0) block.Append('\0');
        return block.ToString(); // StringToHGlobalUni adds the final \0
    }

    /// Finds a command the way a Windows terminal would: on PATH, trying each PATHEXT
    /// extension, so `npm` finds `npm.cmd` (and not the `npm` shell script next to it).
    public static string? Resolve(string command, string cwd, string path, string pathext)
    {
        var extensions = pathext.Split([';'], StringSplitOptions.RemoveEmptyEntries).Select(e => e.Trim()).Where(e => e.StartsWith(".")).ToList();
        if (extensions.Count == 0) extensions = [".COM", ".EXE", ".BAT", ".CMD"];

        IEnumerable<string> Candidates(string full)
        {
            if (Path.HasExtension(full)) yield return full;
            foreach (var ext in extensions) yield return full + ext.ToLowerInvariant();
        }
        string? First(string full)
        {
            try { return Candidates(full).FirstOrDefault(File.Exists); }
            catch (ArgumentException) { return null; }
        }

        if (command.IndexOfAny(['\\', '/', ':']) >= 0)
        {
            try { return First(Path.GetFullPath(Path.IsPathRooted(command) ? command : Path.Combine(cwd, command))); }
            catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException) { return null; }
        }
        foreach (var dir in path.Split([';'], StringSplitOptions.RemoveEmptyEntries))
        {
            var trimmed = dir.Trim().Trim('"');
            if (trimmed.Length == 0) continue;
            string full;
            try { full = Path.Combine(trimmed, command); } catch (ArgumentException) { continue; }
            if (First(full) is { } found) return found;
        }
        return null;
    }

    /// Quotes one argument for a program that parses its command line like the C runtime
    /// (node.exe, python.exe and nearly everything else).
    public static string Quote(string arg)
    {
        if (arg.Length > 0 && arg.IndexOfAny([' ', '\t', '\n', '\v', '"']) < 0) return arg;
        var text = new StringBuilder("\"");
        for (var i = 0; i < arg.Length; i++)
        {
            var backslashes = 0;
            while (i < arg.Length && arg[i] == '\\') { backslashes++; i++; }
            if (i == arg.Length) { text.Append('\\', backslashes * 2); break; }
            if (arg[i] == '"') text.Append('\\', backslashes * 2 + 1).Append('"');
            else text.Append('\\', backslashes).Append(arg[i]);
        }
        return text.Append('"').ToString();
    }

    /// Quotes one argument for a batch file run by cmd.exe, so characters such as & | > ^ %
    /// reach the program as text instead of being run as commands. The same approach as
    /// Rust's standard library after CVE-2024-24576 ("BatBadBut").
    public static string BatchQuote(string arg)
    {
        if (arg.IndexOfAny(['\0', '\r', '\n']) >= 0) throw new ManagerError("Arguments for .cmd and .bat programs can't contain line breaks.");
        const string special = " \t&()[]{}^=;!'+,`~|<>\"%";
        if (arg.Length > 0 && arg.IndexOfAny(special.ToCharArray()) < 0) return arg;
        var text = new StringBuilder("\"");
        var backslashes = 0;
        foreach (var c in arg)
        {
            if (c == '\\') { backslashes++; text.Append(c); continue; }
            if (c == '"') { text.Append('\\', backslashes).Append("\"\""); }
            // %cd:~,% expands to nothing, so "%%cd:~,%" leaves a lone % that can't start a variable.
            else if (c == '%') text.Append("%%cd:~,%");
            else text.Append(c);
            backslashes = 0;
        }
        text.Append('\\', backslashes);
        return text.Append('"').ToString();
    }
}

/// Which ports have a listener, and which process it is, from Windows' TCP table. This is
/// instant, where connecting to a closed port on localhost takes Windows about two seconds.
internal static class PortProbe
{
    private const int AF_INET = 2, AF_INET6 = 23, TCP_TABLE_OWNER_PID_LISTENER = 3;

    private readonly record struct Listener(int Port, int Pid, bool Reachable);

    /// True when something listens on the port at 127.0.0.1, ::1 or every address.
    public static bool IsListening(int port) => Listeners().Any(l => l.Port == port && l.Reachable);

    /// The process listening on a port, if Windows says.
    public static (int Pid, string Command)? Owner(int port)
    {
        var listener = Listeners().Where(l => l.Port == port).OrderByDescending(l => l.Reachable).Cast<Listener?>().FirstOrDefault();
        if (listener is not { } found) return null;
        if (found.Pid is 0 or 4) return (found.Pid, "System");
        try
        {
            using var process = Process.GetProcessById(found.Pid);
            return (found.Pid, process.ProcessName);
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or Win32Exception) { return (found.Pid, "an unknown process"); }
    }

    private static List<Listener> Listeners()
    {
        var result = new List<Listener>();
        Read(AF_INET, 24, row =>
        {
            var address = (uint)Marshal.ReadInt32(row, 4);
            var local = new IPAddress(address);
            result.Add(new Listener(Port(Marshal.ReadInt32(row, 8)), Marshal.ReadInt32(row, 20),
                local.Equals(IPAddress.Any) || (address & 0xFF) == 127));
        });
        Read(AF_INET6, 56, row =>
        {
            var bytes = new byte[16];
            Marshal.Copy(row, bytes, 0, 16);
            var local = new IPAddress(bytes);
            result.Add(new Listener(Port(Marshal.ReadInt32(row, 20)), Marshal.ReadInt32(row, 52),
                local.Equals(IPAddress.IPv6Any) || local.Equals(IPAddress.IPv6Loopback)));
        });
        return result;
    }

    /// The port is in network byte order in the low 16 bits.
    private static int Port(int raw) => ((raw & 0xFF) << 8) | ((raw >> 8) & 0xFF);

    private static void Read(int family, int rowSize, Action<IntPtr> row)
    {
        var size = 0;
        Native.GetExtendedTcpTable(IntPtr.Zero, ref size, false, family, TCP_TABLE_OWNER_PID_LISTENER, 0);
        for (var attempt = 0; attempt < 3 && size > 0; attempt++)
        {
            var buffer = Marshal.AllocHGlobal(size);
            try
            {
                var result = Native.GetExtendedTcpTable(buffer, ref size, false, family, TCP_TABLE_OWNER_PID_LISTENER, 0);
                if (result == 122) continue; // ERROR_INSUFFICIENT_BUFFER: the table grew; try again with the new size
                if (result != 0) return;
                var count = Marshal.ReadInt32(buffer);
                for (var i = 0; i < count; i++) row(buffer + 4 + i * rowSize);
                return;
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }
    }
}
