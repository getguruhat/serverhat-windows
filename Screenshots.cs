using System.Diagnostics;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using Timer = System.Windows.Forms.Timer;

namespace ServerHat;

/// `ServerHat.exe --screenshots <folder>`, run by .github/workflows/screenshots.yml: starts
/// the real tray app with a few example servers in a temporary folder and saves the menu,
/// the panel (light and dark), the approval window and the Edit window as PNGs for the README.
///
/// The running servers are ServerHat.exe in its `--test-listen` mode (see SmokeTest.cs);
/// their commands don't show in the pictures.
internal static class Screenshots
{
    public static int Run(string output)
    {
        output = Path.GetFullPath(output);
        Directory.CreateDirectory(output);
        var root = Path.Combine(Path.GetTempPath(), "ServerHat-screenshots-" + Process.GetCurrentProcess().Id);
        Paths.ConfigFolder = Path.Combine(root, "config");
        Paths.LogFolder = Path.Combine(root, "logs");
        Settings.Key = @"Software\GuruHat\ServerHat-screenshots-" + Process.GetCurrentProcess().Id;
        Directory.CreateDirectory(Paths.ConfigFolder);

        var exe = Application.ExecutablePath;
        ServerConfig Listening(string name, int port) => new()
        {
            Id = ServerConfig.Slug(name), Name = name, Command = exe, Args = ["--test-listen", port.ToString()], Port = port, StartAtLogin = true,
        };
        var allowed = new List<ServerConfig>
        {
            Listening("Docs site", 5173),
            Listening("API", 8000),
            new() { Id = "storybook", Name = "Storybook", Cwd = @"~\code\app", Command = "npm", Args = ["run", "storybook"], Port = 6006 },
            new() { Id = "worker", Name = "Worker", Command = "cmd", Args = ["/c", "echo Error: Cannot find module './queue'& exit /b 1"], StartAtLogin = true },
        };
        // What Claude Code just added, waiting for approval.
        var pending = new ServerConfig
        {
            Id = "blog", Name = "Blog", Cwd = @"~\code\blog", Command = "npm", Args = ["run", "dev", "--", "--port", "4321"], Port = 4321,
            Env = new Dictionary<string, string> { ["NODE_ENV"] = "development", ["PUBLIC_API_URL"] = "http://127.0.0.1:8000" },
        };
        Settings.ApprovedCommands = allowed.Select(ServerManager.Fingerprint).ToList();
        new ConfigStore().Save(new ConfigFile { Servers = [.. allowed, pending] });

        var failures = 0;
        var app = new TrayApp();
        var start = new Timer { Interval = 4000 };
        start.Tick += async (_, _) =>
        {
            start.Stop();
            try
            {
                var area = Screen.PrimaryScreen.WorkingArea;
                var panel = app.Panel;
                panel.StayOpen = true;
                foreach (var (dark, name) in new[] { (false, "panel.png"), (true, "panel-dark.png") })
                {
                    panel.ForceDark = dark;
                    panel.ShowNear(new Point(area.Right - 200, area.Bottom - 10));
                    await Task.Delay(800);
                    if (!Save(panel.Bounds, Path.Combine(output, name))) failures++;
                    if (!dark) Save(Rectangle.FromLTRB(panel.Left - 40, panel.Top - 40, Screen.PrimaryScreen.Bounds.Right, Screen.PrimaryScreen.Bounds.Bottom), Path.Combine(output, "panel-in-place.png"));
                    panel.HideNow();
                }
                panel.StayOpen = false;

                var menu = app.ShowMenu(new Point(area.Left + 40, area.Top + 40));
                await Task.Delay(800);
                if (!Save(menu.Bounds, Path.Combine(output, "menu.png"))) failures++;
                menu.Close();

                ApprovalWindow.Ask(app.Manager, app.Manager.Server("blog")!);
                if (!await SaveForm<ApprovalWindow>(Path.Combine(output, "approval.png"))) failures++;

                EditWindow.ShowFor(app.Manager, app.Manager.Server("blog")!);
                if (!await SaveForm<EditWindow>(Path.Combine(output, "edit.png"))) failures++;

                // Whatever else is on screen, such as the notification, for a look.
                Save(Screen.PrimaryScreen.Bounds, Path.Combine(output, "screen.png"));
            }
            catch (Exception e)
            {
                Console.Error.WriteLine(e);
                failures++;
            }
            finally
            {
                app.Quit();
            }
        };
        start.Start();
        Application.Run(app);
        try { Microsoft.Win32.Registry.CurrentUser.DeleteSubKeyTree(Settings.Key, false); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        try { Directory.Delete(root, true); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        return failures;
    }

    private static async Task<bool> SaveForm<T>(string path) where T : Form
    {
        await Task.Delay(800);
        if (Application.OpenForms.OfType<T>().FirstOrDefault() is not { } form) return false;
        form.TopMost = true;
        form.Activate();
        await Task.Delay(500);
        var saved = Save(Frame(form), path);
        form.Close();
        return saved;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect { public int Left, Top, Right, Bottom; }

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out Rect rect, int size);

    /// The window as it looks, without the invisible resize border that Bounds includes.
    private static Rectangle Frame(Form form)
    {
        if (DwmGetWindowAttribute(form.Handle, 9 /* DWMWA_EXTENDED_FRAME_BOUNDS */, out var r, Marshal.SizeOf<Rect>()) == 0)
            return Rectangle.FromLTRB(r.Left, r.Top, r.Right, r.Bottom);
        return form.Bounds;
    }

    private static bool Save(Rectangle area, string path)
    {
        if (area.Width <= 0 || area.Height <= 0) return false;
        using var bitmap = new Bitmap(area.Width, area.Height, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bitmap)) g.CopyFromScreen(area.Location, Point.Empty, area.Size);
        bitmap.Save(path, ImageFormat.Png);
        Console.WriteLine($"saved {path} ({area.Width}×{area.Height})");
        return true;
    }
}
