using System.Text;

namespace ServerHat;

internal enum ServerStatus { Stopped, Starting, Running, Stopping, Crashed, PortInUse, Failed }

internal static class ServerStatusNames
{
    /// The names the API uses, the same as the macOS version's.
    public static string ApiName(this ServerStatus status) => status switch
    {
        ServerStatus.PortInUse => "port-in-use",
        _ => status.ToString().ToLowerInvariant(),
    };

    public static string Label(this ServerStatus status) => status switch
    {
        ServerStatus.Stopped => "Stopped",
        ServerStatus.Starting => "Starting…",
        ServerStatus.Running => "Running",
        ServerStatus.Stopping => "Stopping…",
        ServerStatus.Crashed => "Crashed",
        ServerStatus.PortInUse => "Port in use",
        _ => "Couldn't start",
    };
}

internal sealed record ServerState
{
    public ServerStatus Status { get; init; } = ServerStatus.Stopped;
    /// A sentence for people: why it crashed, who holds the port, and so on.
    public string? Detail { get; init; }
    public int? Pid { get; init; }
    public DateTime? StartedAt { get; init; }
    public int? ExitCode { get; init; }
}

internal sealed class ManagerError(string message, int status = 400) : Exception(message)
{
    public int Status { get; } = status;
}

/// The last lines a server printed, kept in memory for the menu, the log window and the
/// API, and appended to %LOCALAPPDATA%\ServerHat\Logs\<id>.log. Used on the UI thread only.
internal sealed class LogBuffer(string id)
{
    private const int MaxLines = 5000;
    private const long MaxFileBytes = 5 * 1024 * 1024;
    private static readonly Encoding Utf8 = new UTF8Encoding(false);

    private readonly List<string> lines = [];
    private readonly List<byte> partial = [];
    private FileStream? file;

    public string FilePath { get; } = Path.Combine(Paths.LogFolder, id + ".log");
    /// Goes up with every change, so the log window knows when to redraw.
    public int Revision { get; private set; }
    public IReadOnlyList<string> Lines => lines;

    public void BeginRun(string header)
    {
        try
        {
            Directory.CreateDirectory(Paths.LogFolder);
            var info = new FileInfo(FilePath);
            if (info.Exists && info.Length > MaxFileBytes)
            {
                var old = Path.Combine(Paths.LogFolder, id + ".1.log");
                File.Delete(old);
                File.Move(FilePath, old);
            }
            file?.Dispose();
            // Shared, so the log can be opened in an editor while the server runs.
            file = new FileStream(FilePath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { file = null; }
        Append(header);
    }

    public void EndRun(string footer)
    {
        FlushPartial();
        Append(footer);
        file?.Dispose();
        file = null;
    }

    public void Append(byte[] data)
    {
        foreach (var b in data)
        {
            if (b == 0x0A)
            {
                Append(Utf8.GetString(partial.ToArray()));
                partial.Clear();
            }
            else partial.Add(b);
        }
        // A line with no newline yet (a prompt, a progress bar) still shows up eventually.
        if (partial.Count > 16 * 1024) FlushPartial();
    }

    public void Append(string line)
    {
        var clean = line.EndsWith("\r") ? line.Substring(0, line.Length - 1) : line;
        lines.Add(clean);
        if (lines.Count > MaxLines) lines.RemoveRange(0, lines.Count - MaxLines);
        if (file is not null)
        {
            try
            {
                var bytes = Utf8.GetBytes(clean + "\r\n");
                file.Write(bytes, 0, bytes.Length);
                file.Flush();
            }
            catch (IOException) { }
        }
        Revision++;
    }

    public List<string> Tail(int count) => lines.Skip(Math.Max(0, lines.Count - Math.Max(0, count))).ToList();

    public void Clear()
    {
        lines.Clear();
        Revision++;
    }

    private void FlushPartial()
    {
        if (partial.Count == 0) return;
        var text = Utf8.GetString(partial.ToArray());
        partial.Clear();
        Append(text);
    }
}
