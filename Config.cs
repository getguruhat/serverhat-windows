using System.Text;
using System.Text.Json;

namespace ServerHat;

/// Where ServerHat keeps its files. The smoke test points these at a temporary folder.
internal static class Paths
{
    /// %APPDATA%\ServerHat: servers.json, api-token and api.json.
    public static string ConfigFolder { get; set; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ServerHat");
    /// %LOCALAPPDATA%\ServerHat\Logs: one <id>.log per server.
    public static string LogFolder { get; set; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ServerHat", "Logs");

    public static string ConfigFile => Path.Combine(ConfigFolder, "servers.json");
    public static string Home => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    /// "~", "~\code" and "%USERPROFILE%\code" all work in servers.json.
    public static string Expand(string path)
    {
        var expanded = Environment.ExpandEnvironmentVariables(path);
        if (expanded == "~") return Home;
        if (expanded.StartsWith("~/") || expanded.StartsWith("~\\")) return Path.Combine(Home, expanded.Substring(2));
        return expanded;
    }

    /// C:\Users\you\code → ~\code, the way the macOS version writes ~/code.
    public static string Abbreviate(string path)
    {
        var home = Home.TrimEnd('\\', '/');
        if (string.Equals(path, home, StringComparison.OrdinalIgnoreCase)) return "~";
        if (path.Length > home.Length && path.StartsWith(home, StringComparison.OrdinalIgnoreCase) && (path[home.Length] == '\\' || path[home.Length] == '/'))
            return "~" + path.Substring(home.Length);
        return path;
    }
}

/// A servers.json or request body that doesn't have the expected shape. The messages
/// match the macOS version's ("missing \"command\" at top level", "wrong type at .port").
internal sealed class ConfigException(string message) : Exception(message);

/// Reads typed values out of a parsed JSON object, keeping track of where it is for error messages.
internal sealed class JsonReader(Dictionary<string, object?> values, string path)
{
    private string Where(string key) => path + "." + key;
    private ConfigException Missing(string key) => new($"missing \"{key}\" at {(path.Length == 0 ? "top level" : path)}");
    private static ConfigException Wrong(string at) => new($"wrong type at {at}");

    public static JsonReader Of(object? value, string path) =>
        value is Dictionary<string, object?> d ? new JsonReader(d, path) : throw Wrong(path.Length == 0 ? "top level" : path);

    public bool Has(string key) => values.TryGetValue(key, out var v) && v is not null;

    public string String(string key) =>
        !values.TryGetValue(key, out var v) ? throw Missing(key) : v as string ?? throw Wrong(Where(key));

    public string? OptionalString(string key) =>
        !Has(key) ? null : values[key] as string ?? throw Wrong(Where(key));

    public int Int(string key) => !values.ContainsKey(key) ? throw Missing(key) : OptionalInt(key) ?? throw Wrong(Where(key));

    public int? OptionalInt(string key)
    {
        if (!Has(key)) return null;
        return values[key] switch
        {
            long l when l is >= int.MinValue and <= int.MaxValue => (int)l,
            long or double => throw new ConfigException($"bad value at {Where(key)}"),
            _ => throw Wrong(Where(key)),
        };
    }

    public bool? OptionalBool(string key) => !Has(key) ? null : values[key] as bool? ?? throw Wrong(Where(key));

    public List<string>? OptionalStrings(string key)
    {
        if (!Has(key)) return null;
        if (values[key] is not List<object?> list) throw Wrong(Where(key));
        return list.Select((item, i) => item as string ?? throw Wrong($"{Where(key)}[{i}]")).ToList();
    }

    public Dictionary<string, string>? OptionalStringMap(string key)
    {
        if (!Has(key)) return null;
        if (values[key] is not Dictionary<string, object?> map) throw Wrong(Where(key));
        return map.ToDictionary(p => p.Key, p => p.Value as string ?? throw Wrong($"{Where(key)}.{p.Key}"));
    }

    public List<JsonReader> Objects(string key)
    {
        if (!values.TryGetValue(key, out var v)) throw Missing(key);
        if (v is not List<object?> list) throw Wrong(Where(key));
        var at = Where(key);
        return list.Select((item, i) => Of(item, $"{at}[{i}]")).ToList();
    }
}

/// One local web server, as stored in servers.json.
internal sealed class ServerConfig
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    /// The folder the command runs in. `~` is expanded.
    public string Cwd { get; set; } = "~";
    /// A program on PATH (`node`, `npm`, `python`; `.exe` and `.cmd` are found too), or a path.
    public string Command { get; set; } = "";
    public List<string> Args { get; set; } = [];
    public int? Port { get; set; }
    /// What "Open in browser" opens. Defaults to http://127.0.0.1:<port>.
    public string? Url { get; set; }
    public Dictionary<string, string> Env { get; set; } = [];
    /// Start this server whenever ServerHat starts (so at sign-in, when ServerHat opens at sign-in).
    public bool StartAtLogin { get; set; }

    /// Hand-edited files may leave out anything optional. Reads in the same order as the
    /// macOS version, so the first problem reported is the same one.
    public static ServerConfig FromJson(JsonReader r)
    {
        var name = r.String("name");
        return new ServerConfig
        {
            Name = name,
            Id = r.OptionalString("id") ?? Slug(name),
            Cwd = r.OptionalString("cwd") ?? "~",
            Command = r.String("command"),
            Args = r.OptionalStrings("args") ?? [],
            Port = r.OptionalInt("port"),
            Url = r.OptionalString("url"),
            Env = r.OptionalStringMap("env") ?? [],
            StartAtLogin = r.OptionalBool("startAtLogin") ?? false,
        };
    }

    /// The servers.json form. Like the macOS version, a missing port or url is left out.
    public Dictionary<string, object?> ToJson()
    {
        var json = new Dictionary<string, object?>
        {
            ["id"] = Id, ["name"] = Name, ["cwd"] = Cwd, ["command"] = Command,
            ["args"] = Args.Cast<object?>().ToList(),
            ["env"] = Env.ToDictionary(p => p.Key, p => (object?)p.Value),
            ["startAtLogin"] = StartAtLogin,
        };
        if (Port is { } port) json["port"] = port;
        if (Url is { } url) json["url"] = url;
        return json;
    }

    public ServerConfig Copy() => new()
    {
        Id = Id, Name = Name, Cwd = Cwd, Command = Command, Args = [.. Args], Port = Port, Url = Url,
        Env = new Dictionary<string, string>(Env), StartAtLogin = StartAtLogin,
    };

    public bool SameAs(ServerConfig other) => Json.Write(ToJson()) == Json.Write(other.ToJson());

    public string WorkingDirectory
    {
        get
        {
            try { return Path.GetFullPath(Paths.Expand(Cwd)); }
            catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException) { return Paths.Expand(Cwd); }
        }
    }

    public string? OpenUrl => !string.IsNullOrEmpty(Url) ? Url : Port is { } port ? $"http://127.0.0.1:{port}" : null;

    /// Problems that would stop the server from starting, or null.
    public string? Problem
    {
        get
        {
            if (Id.Length == 0 || Id.Contains("/")) return "id must be non-empty and contain no \"/\"";
            // The id names the log file.
            if (Id.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || Id.Trim('.', ' ').Length == 0) return "id can't contain \\ : * ? \" < > | or be only dots";
            if (Name.Trim().Length == 0) return "name is required";
            if (Command.Trim().Length == 0) return "command is required";
            if (Port is { } port && port is < 1 or > 65535) return "port must be 1–65535";
            return null;
        }
    }

    public static string Slug(string text)
    {
        var lowered = new string(text.ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) ? c : '-').ToArray());
        var joined = string.Join("-", lowered.Split(['-'], StringSplitOptions.RemoveEmptyEntries));
        return joined.Length == 0 ? "server" : joined;
    }
}

internal sealed class ConfigFile
{
    public int Version { get; set; } = 1;
    public List<ServerConfig> Servers { get; set; } = [];

    public static ConfigFile FromJson(object? root)
    {
        var r = JsonReader.Of(root, "");
        return new ConfigFile { Version = r.Int("version"), Servers = r.Objects("servers").Select(ServerConfig.FromJson).ToList() };
    }

    public Dictionary<string, object?> ToJson() => new()
    {
        ["version"] = Version,
        ["servers"] = Servers.Select(s => (object?)s.ToJson()).ToList(),
    };
}

/// Reads and writes %APPDATA%\ServerHat\servers.json, and calls OnChange when someone
/// else (a person, Claude Code, Codex) edits it. OnChange runs on the thread that called Watch.
internal sealed class ConfigStore
{
    public Action<ConfigFile>? OnChange { get; set; }
    /// Set when the file can't be parsed; the last good config stays in use.
    public string? LoadError { get; private set; }
    private byte[]? lastData;
    private FileSystemWatcher? watcher;
    private SynchronizationContext? context;
    private int pending;

    public ConfigFile Load()
    {
        Directory.CreateDirectory(Paths.ConfigFolder);
        byte[] data;
        try { data = File.ReadAllBytes(Paths.ConfigFile); }
        catch (FileNotFoundException) { var empty = new ConfigFile(); Save(empty); return empty; }
        catch (DirectoryNotFoundException) { var empty = new ConfigFile(); Save(empty); return empty; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { LoadError = "servers.json: " + e.Message; return new ConfigFile(); }
        lastData = data;
        try
        {
            var file = ConfigFile.FromJson(ParseFile(data));
            file.Servers = Uniqued(file.Servers);
            LoadError = null;
            return file;
        }
        catch (ConfigException e)
        {
            LoadError = "servers.json: " + e.Message;
            return new ConfigFile();
        }
    }

    /// Notepad may save with a byte order mark; JSON parsers don't want it.
    public static object? ParseFile(byte[] data)
    {
        var start = data.Length >= 3 && data[0] == 0xEF && data[1] == 0xBB && data[2] == 0xBF ? 3 : 0;
        try { return Json.Parse(new UTF8Encoding(false, true).GetString(data, start, data.Length - start)); }
        catch (Exception e) when (e is JsonException or DecoderFallbackException) { throw new ConfigException("not valid JSON"); }
    }

    public void Save(ConfigFile file)
    {
        var data = Encoding.UTF8.GetBytes(Json.Write(file.ToJson()) + "\n");
        lastData = data;
        Directory.CreateDirectory(Paths.ConfigFolder);
        WriteAtomically(Paths.ConfigFile, data);
        LoadError = null;
    }

    /// Writes next to the file and swaps it in, so readers never see half a file.
    public static void WriteAtomically(string path, byte[] data)
    {
        var temp = path + ".tmp";
        try
        {
            File.WriteAllBytes(temp, data);
            if (File.Exists(path)) File.Replace(temp, path, null, true); else File.Move(temp, path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            // An editor holding the file open can make the swap fail; write it in place instead.
            try { File.WriteAllBytes(path, data); File.Delete(temp); } catch (Exception again) when (again is IOException or UnauthorizedAccessException) { }
        }
    }

    /// Watches the folder rather than the file, because editors and Save replace the
    /// file, which a watcher on the file alone would miss.
    public void Watch()
    {
        context = SynchronizationContext.Current;
        watcher = new FileSystemWatcher(Paths.ConfigFolder)
        {
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,
            IncludeSubdirectories = false,
        };
        FileSystemEventHandler changed = (_, _) => Schedule();
        watcher.Changed += changed;
        watcher.Created += changed;
        watcher.Deleted += changed;
        watcher.Renamed += (_, _) => Schedule();
        watcher.EnableRaisingEvents = true;
    }

    /// Lets the writer finish before reading, and reads once for a burst of events.
    private void Schedule()
    {
        if (Interlocked.Exchange(ref pending, 1) == 1) return;
        Task.Delay(200).ContinueWith(_ =>
        {
            Interlocked.Exchange(ref pending, 0);
            if (context is null) ReloadIfChanged(); else context.Post(_ => ReloadIfChanged(), null);
        });
    }

    private void ReloadIfChanged()
    {
        byte[] data;
        try { data = File.ReadAllBytes(Paths.ConfigFile); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return; }
        if (lastData is not null && data.SequenceEqual(lastData)) return;
        var previousError = LoadError;
        var file = Load();
        if (LoadError is null || LoadError != previousError) OnChange?.Invoke(file);
    }

    private static List<ServerConfig> Uniqued(List<ServerConfig> servers)
    {
        var seen = new HashSet<string>();
        foreach (var server in servers)
        {
            var candidate = server.Id;
            var n = 2;
            while (seen.Contains(candidate)) candidate = $"{server.Id}-{n++}";
            server.Id = candidate;
            seen.Add(candidate);
        }
        return servers;
    }
}

/// Reads `.claude/launch.json` files: `{version, configurations: [{name, runtimeExecutable, runtimeArgs, port, url}]}`.
internal static class LaunchJsonImporter
{
    /// `path` may be the launch.json file, its `.claude` folder, or the project folder.
    public static List<ServerConfig> Servers(string path)
    {
        var file = Path.GetFullPath(Paths.Expand(path));
        if (Directory.Exists(file))
            file = Path.GetFileName(file.TrimEnd('\\', '/')) == ".claude" ? Path.Combine(file, "launch.json") : Path.Combine(file, ".claude", "launch.json");
        if (!File.Exists(file)) throw new ConfigException($"The file “{Path.GetFileName(file)}” couldn’t be opened because there is no such file.");
        var launch = JsonReader.Of(ConfigStore.ParseFile(File.ReadAllBytes(file)), "");
        // launch.json lives in <project>\.claude\, and its commands run from <project>.
        var claudeFolder = Path.GetDirectoryName(file)!;
        var project = Path.GetFileName(claudeFolder) == ".claude" ? Path.GetDirectoryName(claudeFolder)! : claudeFolder;
        var servers = new List<ServerConfig>();
        foreach (var c in launch.Objects("configurations"))
        {
            var name = c.String("name");
            var exe = c.OptionalString("runtimeExecutable");
            var args = c.OptionalStrings("runtimeArgs");
            var port = c.OptionalInt("port");
            var url = c.OptionalString("url");
            var rel = c.OptionalString("cwd");
            var env = c.OptionalStringMap("env");
            if (string.IsNullOrEmpty(exe)) continue;
            var cwd = project;
            if (!string.IsNullOrEmpty(rel))
                cwd = Path.IsPathRooted(rel) || rel!.StartsWith("~") ? rel! : Path.GetFullPath(Path.Combine(project, rel));
            servers.Add(new ServerConfig
            {
                Id = ServerConfig.Slug(name), Name = name, Cwd = Paths.Abbreviate(cwd), Command = exe!,
                Args = args ?? [], Port = port, Url = url, Env = env ?? [],
            });
        }
        return servers;
    }
}
