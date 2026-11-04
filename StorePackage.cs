using System.Runtime.InteropServices;
using Windows.ApplicationModel;

namespace ServerHat;

/// The things that work differently when ServerHat runs from its Microsoft Store (MSIX)
/// package: opening at sign-in, the command MCP clients run, and updates.
internal static class StorePackage
{
    /// Matches the StartupTask TaskId in msix/AppxManifest.xml.
    private const string TaskId = "ServerHatStartup";
    private const int NoPackage = 15700; // APPMODEL_ERROR_NO_PACKAGE

    [DllImport("kernel32.dll")]
    private static extern int GetCurrentPackageFullName(ref uint length, IntPtr name);

    public static readonly bool Packaged = IsPackaged();

    private static bool IsPackaged()
    {
        uint length = 0;
        try { return GetCurrentPackageFullName(ref length, IntPtr.Zero) != NoPackage; }
        catch (EntryPointNotFoundException) { return false; }
    }

    /// The Store installs into a folder named after the version, so MCP clients run the
    /// app execution alias instead (msix/AppxManifest.xml), which stays put across updates.
    public static string ExecutablePath => Packaged
        ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "WindowsApps", "ServerHat.exe")
        : Application.ExecutablePath;

    /// "Open ServerHat when I sign in" for the Store version, as the package's startup task.
    public static bool StartupEnabled
    {
        get => GetTask().State is StartupTaskState.Enabled or StartupTaskState.EnabledByPolicy;
        set
        {
            var task = GetTask();
            if (!value) { task.Disable(); return; }
            var state = Task.Run(async () => await task.RequestEnableAsync()).GetAwaiter().GetResult();
            // Once the user turns ServerHat off in Windows' own startup settings, only they can turn it back on.
            if (state == StartupTaskState.DisabledByUser)
                MessageBox.Show(Strings.Current.StartupBlocked,
                    "ServerHat", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
    }

    private static StartupTask GetTask() =>
        Task.Run(async () => await StartupTask.GetAsync(TaskId)).GetAwaiter().GetResult();
}
