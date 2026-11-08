using System.ComponentModel;
using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using Timer = System.Windows.Forms.Timer;

namespace ServerHat;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        // Stand-in servers for the smoke test; see SmokeTest.cs.
        if (args.Length >= 2 && args[0] == "--test-listen") { SmokeTest.Listen(int.Parse(args[1])); return; }
        if (args.Length >= 1 && args[0] == "--test-child") { Thread.Sleep(Timeout.Infinite); return; }
        if (args.Length >= 1 && args[0] == "--print-args") { foreach (var arg in args.Skip(1)) Console.WriteLine("arg:" + arg); return; }
        // Started by Claude Code, Codex or another MCP client; see McpServer.cs.
        if (args.Length >= 1 && args[0] == "--mcp") { Environment.ExitCode = McpServer.Run(); return; }

        // DPI awareness is set in App.config and app.manifest.
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());
        if (args.Contains("--smoke-test")) { Environment.ExitCode = SmokeTest.Run(); return; }
        if (args.Length >= 2 && args[0] == "--screenshots") { Environment.ExitCode = Screenshots.Run(args[1]); return; }

        using var mutex = new Mutex(true, "GuruHat.ServerHat.Windows", out var first);
        if (!first)
        {
            MessageBox.Show(Strings.Current.AlreadyRunning,
                "ServerHat", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        Application.Run(new TrayApp());
    }

    /// Opens a file in its usual app, or in Notepad when .json or .log has none.
    public static void OpenFile(string path)
    {
        try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); }
        catch (Win32Exception)
        {
            try { Process.Start(new ProcessStartInfo("notepad.exe", Spawner.Quote(path)) { UseShellExecute = true }); }
            catch (Win32Exception) { }
        }
    }
}

/// The notification-area icon and its menu: every server with its status and actions,
/// the way the macOS version's menu bar window lists them.
internal sealed class TrayApp : ApplicationContext
{
    private readonly ServerManager manager;
    private readonly ApiServer api;
    private readonly NotifyIcon tray;
    private readonly TrayPanel panel;
    private readonly ContextMenuStrip menu = new() { ShowImageMargin = true };
    private readonly Timer refresh = new() { Interval = 500 };
    /// The first update check runs a minute after launch, then once a day.
    private readonly Timer updateTimer = new() { Interval = 60 * 1000 };
    private readonly Dictionary<string, ToolStripMenuItem> serverItems = [];
    private readonly Dictionary<string, Icon> icons = [];
    private readonly Dictionary<Color, Bitmap> dots = [];
    private Font? bold;
    private UpdateCheck.Release? update;
    /// The server the latest balloon asked about, so clicking it opens that server's review.
    private string? balloonServer;
    /// Which servers the menu shows and which of them wait for approval; a change rebuilds it.
    private string layout = "";
    private bool dirty = true;
    private bool quitting;

    public TrayApp()
    {
        var firstRun = !File.Exists(Paths.ConfigFile);
        manager = new ServerManager();
        manager.Changed += () => dirty = true;
        api = new ApiServer(manager);
        api.Start();

        tray = new NotifyIcon { ContextMenuStrip = menu, Visible = true };
        manager.ApprovalNeeded += AskForApproval;
        tray.BalloonTipClicked += (_, _) =>
        {
            if (balloonServer is { } id && manager.Server(id) is { } server) ApprovalWindow.Ask(manager, server);
        };
        menu.Opening += (_, e) => { Build(); e.Cancel = false; };
        // Left click opens the panel, like the macOS menu bar panel; right click, the menu.
        panel = new TrayPanel(this);
        tray.MouseUp += (_, e) =>
        {
            if (e.Button != MouseButtons.Left) return;
            if (panel.Visible) panel.HideNow();
            else if (!panel.JustHidden) panel.ShowNear(Cursor.Position);
        };
        refresh.Tick += (_, _) => Refresh();
        refresh.Start();
        Build();
        Refresh();
        if (firstRun && manager.PendingApprovals.Count == 0)
            tray.ShowBalloonTip(8000, Strings.Current.WelcomeTitle, Strings.Current.WelcomeMessage, ToolTipIcon.Info);

        updateTimer.Tick += async (_, _) =>
        {
            updateTimer.Interval = 24 * 60 * 60 * 1000;
            await CheckForUpdateAsync();
        };
        // The Microsoft Store updates its own version.
        if (!StorePackage.Packaged) updateTimer.Start();
        Application.ApplicationExit += (_, _) => StopEverything();
    }

    // MARK: Icon

    private void Refresh()
    {
        if (!dirty) return;
        dirty = false;
        var states = manager.States.ToList();
        var running = states.Count(s => s.Status is ServerStatus.Running or ServerStatus.Starting);
        var crashed = states.Any(s => s.Status is ServerStatus.Crashed or ServerStatus.Failed);
        var pending = manager.PendingApprovals.Count > 0;
        tray.Icon = TrayIcon(crashed ? "crashed" : pending ? "pending" : running > 0 ? "running" : "idle");
        var text = running == 0 ? "ServerHat" : Strings.F(Strings.Current.TipRunning, running);
        if (crashed) text += Strings.Current.TipNeedsAttention;
        else if (pending) text += Strings.Current.TipWaiting;
        tray.Text = text.Length > 63 ? text.Substring(0, 63) : text;
        if (menu.Visible) UpdateServerItems();
    }

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr handle);

    /// A globe in the taskbar's text color, with a green dot while servers run, an orange
    /// one when a command waits for approval and a red one when a server crashed.
    private Icon TrayIcon(string kind)
    {
        var dark = Theme.DarkTaskbar;
        var key = kind + (dark ? "-dark" : "-light");
        if (icons.TryGetValue(key, out var cached)) return cached;
        var size = SystemInformation.SmallIconSize.Width;
        using var bitmap = new Bitmap(size, size, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bitmap))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var ink = dark ? Color.White : Color.FromArgb(28, 28, 28);
            var s = size / 16f;
            using var pen = new Pen(ink, Math.Max(1f, 1.25f * s));
            var globe = new RectangleF(1.5f * s, 1.5f * s, 13f * s, 13f * s);
            g.DrawEllipse(pen, globe);
            g.DrawEllipse(pen, globe.X + 3.6f * s, globe.Y, globe.Width - 7.2f * s, globe.Height);
            g.DrawLine(pen, globe.X, globe.Y + globe.Height / 2, globe.Right, globe.Y + globe.Height / 2);
            g.DrawLine(pen, globe.X + 1.6f * s, globe.Y + 3.4f * s, globe.Right - 1.6f * s, globe.Y + 3.4f * s);
            g.DrawLine(pen, globe.X + 1.6f * s, globe.Bottom - 3.4f * s, globe.Right - 1.6f * s, globe.Bottom - 3.4f * s);
            if (kind != "idle")
            {
                var badge = new RectangleF(size - 7.5f * s, size - 7.5f * s, 7f * s, 7f * s);
                using var clear = new SolidBrush(dark ? Color.FromArgb(32, 32, 32) : Color.FromArgb(243, 243, 243));
                g.FillEllipse(clear, RectangleF.Inflate(badge, 1.2f * s, 1.2f * s));
                using var fill = new SolidBrush(kind switch
                {
                    "running" => Color.FromArgb(16, 185, 80),
                    "pending" => Pending,
                    _ => Color.FromArgb(232, 58, 48),
                });
                g.FillEllipse(fill, badge);
            }
        }
        var handle = bitmap.GetHicon();
        var icon = (Icon)Icon.FromHandle(handle).Clone();
        DestroyIcon(handle);
        icons[key] = icon;
        return icon;
    }

    private Bitmap Dot(Color color)
    {
        if (dots.TryGetValue(color, out var cached)) return cached;
        var size = (int)Math.Round(16 * menu.DeviceDpi / 96f);
        var bitmap = new Bitmap(size, size);
        using (var g = Graphics.FromImage(bitmap))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using var brush = new SolidBrush(color);
            var d = size * 0.55f;
            g.FillEllipse(brush, (size - d) / 2, (size - d) / 2, d, d);
        }
        dots[color] = bitmap;
        return bitmap;
    }

    private static readonly Color Pending = Color.FromArgb(245, 130, 32);

    private static Color StatusColor(ServerStatus status) => status switch
    {
        ServerStatus.Running => Color.FromArgb(16, 185, 80),
        ServerStatus.Starting or ServerStatus.Stopping => Color.FromArgb(234, 179, 8),
        ServerStatus.Crashed or ServerStatus.Failed => Color.FromArgb(232, 58, 48),
        ServerStatus.PortInUse => Color.FromArgb(245, 130, 32),
        _ => Color.FromArgb(160, 160, 160),
    };

    // MARK: Menu

    private static string Escape(string text) => text.Replace("&", "&&");

    private static string Short(string text, int max = 110) => text.Length > max ? text.Substring(0, max - 1) + "…" : text;

    private void Build()
    {
        menu.SuspendLayout();
        foreach (var item in menu.Items.Cast<ToolStripItem>().ToList()) item.Dispose();
        menu.Items.Clear();
        serverItems.Clear();

        var header = new ToolStripMenuItem(HeaderText()) { Enabled = false, Font = bold ??= new Font(menu.Font, FontStyle.Bold) };
        menu.Items.Add(header);
        foreach (var problem in new[] { manager.ConfigError, api.Problem }.OfType<string>())
            menu.Items.Add(new ToolStripMenuItem("⚠ " + Escape(Short(problem))) { Enabled = false, ToolTipText = problem });

        var startAll = new ToolStripMenuItem(Strings.Current.StartAll, null, (_, _) => manager.StartAll()) { Enabled = manager.Servers.Count > 0 };
        var stopAll = new ToolStripMenuItem(Strings.Current.StopAll, null, (_, _) => manager.StopAll()) { Enabled = manager.RunningCount > 0 };
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(startAll);
        menu.Items.Add(stopAll);
        menu.Items.Add(new ToolStripSeparator());

        if (manager.Servers.Count == 0)
            menu.Items.Add(new ToolStripMenuItem(Strings.Current.NoServersMenu) { Enabled = false });
        layout = Layout();
        foreach (var server in manager.Servers)
        {
            var id = server.Id;
            var item = new ToolStripMenuItem { Tag = id };
            item.DropDownItems.Add(new ToolStripMenuItem("…")); // replaced when the submenu opens
            item.DropDownOpening += (_, _) => FillServerMenu(item, id);
            serverItems[id] = item;
            menu.Items.Add(item);
            if (manager.NeedsApproval(id)) AddApprovalItems(server);
        }
        UpdateServerItems();

        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem(Strings.Current.AddServerMenu, null, (_, _) => EditWindow.ShowFor(manager, null)));
        menu.Items.Add(new ToolStripMenuItem(Strings.Current.ImportLaunchMenu, null, (_, _) => ImportLaunchJson()));
        menu.Items.Add(new ToolStripMenuItem(Strings.Current.OpenServersJson, null, (_, _) => Program.OpenFile(Paths.ConfigFile)));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem(Strings.Current.CopyForClaude, null, (_, _) => CopySetup(codex: false)));
        menu.Items.Add(new ToolStripMenuItem(Strings.Current.CopyForCodex, null, (_, _) => CopySetup(codex: true)));
        menu.Items.Add(new ToolStripSeparator());
        var atLogin = new ToolStripMenuItem(Strings.Current.OpenAtSignIn) { Checked = Settings.OpenAtLogin };
        atLogin.Click += (_, _) =>
        {
            try { Settings.OpenAtLogin = !Settings.OpenAtLogin; }
            catch (Exception e) when (e is UnauthorizedAccessException or System.Security.SecurityException or IOException) { MessageBox.Show(e.Message, "ServerHat"); }
        };
        menu.Items.Add(atLogin);
        var approve = new ToolStripMenuItem(Strings.Current.ApproveNewServers) { Checked = manager.RequireApproval };
        approve.Click += (_, _) => manager.RequireApproval = !manager.RequireApproval;
        menu.Items.Add(approve);
        if (update is { } available)
            menu.Items.Add(new ToolStripMenuItem(Strings.F(Strings.Current.UpdateMenu, available.Version), null, (_, _) => AboutWindow.Open(available.Page)));
        menu.Items.Add(new ToolStripMenuItem(Strings.Current.AboutServerHat, null, (_, _) => AboutWindow.ShowShared()));
        menu.Items.Add(new ToolStripMenuItem(Strings.Current.QuitServerHat, null, (_, _) => Quit()));
        menu.ResumeLayout();
    }

    private string HeaderText()
    {
        var running = manager.States.Count(s => s.Status is ServerStatus.Running or ServerStatus.Starting);
        return manager.Servers.Count == 0 ? "ServerHat" : Strings.F(Strings.Current.MenuTitle, running, manager.Servers.Count);
    }

    private string Layout() => string.Join("\n", manager.Servers.Select(s => s.Id + (manager.NeedsApproval(s.Id) ? " " + ServerManager.Fingerprint(s) : "")));

    /// A server added or changed through the API or servers.json, waiting for the user: its
    /// full command, folder and environment names, with Allow and Remove.
    private void AddApprovalItems(ServerConfig server)
    {
        var id = server.Id;
        var active = manager.State(id).Status is ServerStatus.Running or ServerStatus.Starting or ServerStatus.Stopping;
        void Line(string text, bool strong = false)
        {
            var line = new ToolStripMenuItem(Escape(text)) { Enabled = false, Padding = new Padding(16, 0, 0, 0) };
            if (strong && bold is not null) line.Font = bold;
            menu.Items.Add(line);
        }
        Line(active ? Strings.Current.ChangedAllow : Strings.Current.NewAllow, true);
        foreach (var part in Wrap(ServerManager.CommandLine(server))) Line(part);
        foreach (var part in Wrap("in " + server.Cwd)) Line(part);
        if (server.Env.Count > 0)
            foreach (var part in Wrap(Strings.F(Strings.Current.WithEnvironment, string.Join(", ", server.Env.Keys.OrderBy(k => k, StringComparer.Ordinal))))) Line(part);
        menu.Items.Add(new ToolStripMenuItem(Strings.Current.Allow, null, (_, _) => Allow(id)) { Padding = new Padding(16, 0, 0, 0) });
        menu.Items.Add(new ToolStripMenuItem(Strings.Current.RemoveMenu, null, (_, _) => { if (manager.Server(id) is { } current) ConfirmRemove(current); }) { Padding = new Padding(16, 0, 0, 0) });
        menu.Items.Add(new ToolStripSeparator());
    }

    /// Splits long text into menu-width lines, so the whole command shows.
    private static IEnumerable<string> Wrap(string text, int width = 90)
    {
        for (var i = 0; i < text.Length; i += width) yield return text.Substring(i, Math.Min(width, text.Length - i));
    }

    private void Allow(string id) => ApprovalWindow.Allow(manager, id);

    /// One notification per pending command fingerprint; clicking it opens the review.
    internal void AskForApproval(ServerConfig server)
    {
        if (Quiet) return;
        var body = Strings.F(Strings.Current.ReviewMessage, ServerManager.CommandLine(server), server.Cwd);
        balloonServer = server.Id;
        tray.ShowBalloonTip(15000, Short(Strings.F(Strings.Current.ReviewTitle, server.Name), 63), Short(body, 255), ToolTipIcon.Warning);
    }

    /// Updates names and statuses in place, so an open menu follows along without closing.
    private void UpdateServerItems()
    {
        if (Layout() != layout)
        {
            if (menu.Visible) Build();
            return;
        }
        if (menu.Items.Count > 0 && menu.Items[0] is ToolStripMenuItem header) header.Text = HeaderText();
        foreach (var server in manager.Servers)
        {
            var item = serverItems[server.Id];
            var state = manager.State(server.Id);
            var port = server.Port is { } p ? $"  :{p}" : "";
            var pending = manager.NeedsApproval(server.Id);
            item.Text = $"{Escape(server.Name)}{port}  —  {(pending ? Strings.Current.WaitingForApproval : state.Status.Label())}";
            item.Image = Dot(pending ? Pending : StatusColor(state.Status));
            item.ToolTipText = state.ShownDetail ?? state.Detail ?? "";
        }
    }

    private void FillServerMenu(ToolStripMenuItem item, string id)
    {
        foreach (var old in item.DropDownItems.Cast<ToolStripItem>().ToList()) old.Dispose();
        item.DropDownItems.Clear();
        if (manager.Server(id) is not { } server) return;
        var state = manager.State(id);
        var items = item.DropDownItems;
        items.Add(new ToolStripMenuItem(Escape(Short(state.Shown()))) { Enabled = false, ToolTipText = state.ShownDetail ?? state.Detail });
        items.Add(new ToolStripSeparator());
        if (state.Status is ServerStatus.Running or ServerStatus.Starting or ServerStatus.Stopping)
            items.Add(new ToolStripMenuItem(Strings.Current.Stop, null, (_, _) => manager.Stop(id)) { Enabled = state.Status != ServerStatus.Stopping });
        else
            items.Add(new ToolStripMenuItem(Strings.Current.Start, null, (_, _) => Try(() => manager.Start(id))) { Enabled = state.Status != ServerStatus.PortInUse });
        items.Add(new ToolStripMenuItem(Strings.Current.Restart, null, (_, _) => Try(() => manager.Restart(id))));
        items.Add(new ToolStripMenuItem(Strings.Current.OpenInBrowser, null, (_, _) => { if (server.OpenUrl is { } url) AboutWindow.Open(url); }) { Enabled = server.OpenUrl is not null });
        items.Add(new ToolStripSeparator());
        items.Add(new ToolStripMenuItem(Strings.Current.ViewLogs, null, (_, _) => LogWindow.ShowFor(manager, server)));
        items.Add(new ToolStripMenuItem(Strings.Current.EditMenu, null, (_, _) => EditWindow.ShowFor(manager, server)));
        items.Add(new ToolStripMenuItem(Strings.Current.OpenFolder, null, (_, _) => Program.OpenFile(server.WorkingDirectory)) { Enabled = Directory.Exists(server.WorkingDirectory) });
        if (state.Status == ServerStatus.PortInUse && state.Pid is { } pid)
        {
            items.Add(new ToolStripSeparator());
            items.Add(new ToolStripMenuItem(Strings.F(Strings.Current.EndProcessMenu, server.Port), null, (_, _) => ConfirmEnd(server, state, pid)));
        }
        items.Add(new ToolStripSeparator());
        items.Add(new ToolStripMenuItem(Strings.Current.RemoveMenu, null, (_, _) => ConfirmRemove(server)));
    }

    /// Start and Restart failures already show in the server's status, as on macOS.
    private static void Try(Action action)
    {
        try { action(); } catch (ManagerError) { }
    }

    internal void ConfirmRemove(ServerConfig server)
    {
        var answer = MessageBox.Show(Strings.Current.RemoveMessage,
            Strings.F(Strings.Current.RemoveTitle, server.Name), MessageBoxButtons.OKCancel, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2);
        if (answer == DialogResult.OK) Try(() => manager.Remove(server.Id));
    }

    internal void ConfirmEnd(ServerConfig server, ServerState state, int pid)
    {
        var answer = MessageBox.Show((state.ShownDetail ?? state.Detail ?? "") + "\n\n" + Strings.Current.EndMessage,
            Strings.F(Strings.Current.EndTitle, server.Port), MessageBoxButtons.OKCancel, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2);
        if (answer == DialogResult.OK) ServerManager.EndProcess(pid);
    }

    /// Copies the command that adds ServerHat's MCP server to Claude Code or Codex.
    internal void CopySetup(bool codex)
    {
        try { Clipboard.SetText(McpServer.SetupCommand(codex)); }
        catch (ExternalException) { return; }
        tray.ShowBalloonTip(8000, codex ? Strings.Current.CopiedForCodex : Strings.Current.CopiedForClaude,
            Strings.F(Strings.Current.CopiedMessage, codex ? "Codex" : "Claude Code"), ToolTipIcon.Info);
    }

    internal void ImportLaunchJson()
    {
        using var dialog = new OpenFileDialog
        {
            Title = Strings.Current.ImportDialogTitle,
            Filter = "launch.json|launch.json|" + Strings.Current.JsonFiles + " (*.json)|*.json",
            CheckFileExists = true,
        };
        if (dialog.ShowDialog() != DialogResult.OK) return;
        try
        {
            // Importing from the menu is the user's own choice, so it counts as allowed.
            var added = manager.ImportLaunchJson(dialog.FileName, approvedByUser: true);
            if (added.Count == 0)
                MessageBox.Show(Strings.Current.NothingToImport, Strings.Current.NothingToImportTitle, MessageBoxButtons.OK, MessageBoxIcon.Information);
            else
                MessageBox.Show(string.Join(", ", added.Select(s => s.Name)), added.Count == 1 ? Strings.Current.ImportedOne : Strings.F(Strings.Current.ImportedMany, added.Count), MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (ManagerError e)
        {
            MessageBox.Show(e.Message, Strings.Current.CouldntImport, MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    // MARK: Updates and quitting

    /// Shows the newer release in the menu, and asks once per version whether to download it.
    private async Task CheckForUpdateAsync()
    {
        if (await UpdateCheck.NewerAsync("getguruhat/serverhat-windows") is not { } release) return;
        update = release;
        if (Settings.AnnouncedUpdate == release.Version.ToString()) return;
        Settings.AnnouncedUpdate = release.Version.ToString();
        var answer = MessageBox.Show(Strings.F(Strings.Current.UpdateMessage, UpdateCheck.Current),
            Strings.F(Strings.Current.UpdateTitle, release.Version), MessageBoxButtons.YesNo, MessageBoxIcon.Information);
        if (answer == DialogResult.Yes) AboutWindow.Open(release.Page);
    }

    private void StopEverything()
    {
        if (quitting) return;
        quitting = true;
        refresh.Stop();
        updateTimer.Stop();
        api.Stop();
        manager.Shutdown();
        panel.Dispose();
        tray.Visible = false;
    }

    // For the panel and Screenshots.cs.
    internal ServerManager Manager => manager;
    internal string? ApiProblem => api.Problem;
    internal UpdateCheck.Release? Update => update;
    internal TrayPanel Panel => panel;
    internal ContextMenuStrip ShowMenu(Point at) { menu.Show(at); return menu; }
    /// No approval notifications, for Screenshots.cs: on Windows 11 one stays over the panel
    /// for its whole time, whatever the app does.
    internal static bool Quiet;

    internal void Quit()
    {
        StopEverything();
        tray.Dispose();
        ExitThread();
    }
}
