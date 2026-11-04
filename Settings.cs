using Microsoft.Win32;

namespace ServerHat;

/// ServerHat's few preferences, in HKEY_CURRENT_USER\Software\GuruHat\ServerHat (the
/// Windows counterpart of `defaults write com.guruhat.serverhat`).
internal static class Settings
{
    /// The smoke test points this at a key of its own.
    public static string Key { get; set; } = @"Software\GuruHat\ServerHat";
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    /// The same value name the installer's "Start ServerHat when I sign in" task writes.
    private const string RunValue = "GuruHat ServerHat";

    /// The API port: `apiPort` (a DWORD), or 47317.
    public static int ApiPort => Read("apiPort") is int port && port is >= 1 and <= 65535 ? port : ApiServer.DefaultPort;

    /// The newest version the update dialog was shown for, so it asks once per version.
    public static string? AnnouncedUpdate
    {
        get => Read("updateAnnouncedVersion") as string;
        set { using var key = Registry.CurrentUser.CreateSubKey(Key); key.SetValue("updateAnnouncedVersion", value ?? ""); }
    }

    /// Fingerprints of the commands the user has allowed (`approvedCommands`, a
    /// REG_MULTI_SZ), or null before the first version with approvals ran.
    public static List<string>? ApprovedCommands
    {
        get => Read("approvedCommands") is string[] list ? list.ToList() : null;
        set
        {
            using var key = Registry.CurrentUser.CreateSubKey(Key);
            key.SetValue("approvedCommands", value?.ToArray() ?? Array.Empty<string>(), RegistryValueKind.MultiString);
        }
    }

    /// "Open ServerHat at sign-in", as a Run entry for this user, or the Store package's
    /// startup task.
    public static bool OpenAtLogin
    {
        get
        {
            if (StorePackage.Packaged) return StorePackage.StartupEnabled;
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(RunValue) is string;
        }
        set
        {
            if (StorePackage.Packaged) { StorePackage.StartupEnabled = value; return; }
            using var key = Registry.CurrentUser.CreateSubKey(RunKey);
            if (value) key.SetValue(RunValue, '"' + Application.ExecutablePath + '"'); else key.DeleteValue(RunValue, false);
        }
    }

    private static object? Read(string name)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(Key);
            return key?.GetValue(name);
        }
        catch (Exception e) when (e is System.Security.SecurityException or IOException or UnauthorizedAccessException) { return null; }
    }
}
