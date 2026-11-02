using System.Runtime.InteropServices;
using System.Text;
using Timer = System.Windows.Forms.Timer;

namespace ServerHat;

/// A server's output, following along as it prints. One window per server.
internal sealed class LogWindow : Form
{
    private static readonly Dictionary<string, LogWindow> open = [];

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")]
    private static extern bool GetScrollInfo(IntPtr hwnd, int bar, ref ScrollInfo info);
    [StructLayout(LayoutKind.Sequential)]
    private struct ScrollInfo { public int cbSize, fMask, nMin, nMax, nPage, nPos, nTrackPos; }
    private const int EM_GETFIRSTVISIBLELINE = 0xCE, EM_LINESCROLL = 0xB6, SB_VERT = 1, SIF_ALL = 0x17;

    private readonly ServerManager manager;
    private readonly string id;
    private readonly LogBuffer log;
    private readonly TextBox text = new()
    {
        Multiline = true, ReadOnly = true, WordWrap = false, ScrollBars = ScrollBars.Both, MaxLength = 0,
        Dock = DockStyle.Fill, Font = new Font("Consolas", 9.5f), BorderStyle = BorderStyle.None,
    };
    private readonly Label status = new() { AutoSize = false, AutoEllipsis = true, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft };
    private readonly Timer timer = new() { Interval = 150 };
    private int shown = -1;

    public static void ShowFor(ServerManager manager, ServerConfig server)
    {
        if (open.TryGetValue(server.Id, out var window) && !window.IsDisposed)
        {
            if (window.WindowState == FormWindowState.Minimized) window.WindowState = FormWindowState.Normal;
            window.Activate();
            return;
        }
        window = new LogWindow(manager, server);
        open[server.Id] = window;
        window.FormClosed += (_, _) => open.Remove(server.Id);
        window.Show();
        window.Activate();
    }

    private LogWindow(ServerManager manager, ServerConfig server)
    {
        this.manager = manager;
        id = server.Id;
        log = manager.Log(server.Id);
        Text = $"{server.Name} — Logs";
        Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
        Font = SystemFonts.MessageBoxFont;
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(760, 480);
        StartPosition = FormStartPosition.CenterScreen;
        KeyPreview = true;

        var clear = new Button { Text = "Clear", AutoSize = true, Anchor = AnchorStyles.Right };
        clear.Click += (_, _) => log.Clear();
        var openFile = new Button { Text = "Open log file", AutoSize = true, Anchor = AnchorStyles.Right };
        openFile.Click += (_, _) => Program.OpenFile(log.FilePath);
        var bar = new TableLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, ColumnCount = 3, Padding = new Padding(8, 6, 8, 6) };
        bar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        bar.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        bar.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        bar.Controls.Add(status, 0, 0);
        bar.Controls.Add(clear, 1, 0);
        bar.Controls.Add(openFile, 2, 0);
        Controls.Add(text);
        Controls.Add(bar);

        timer.Tick += (_, _) => UpdateText(false);
        timer.Start();
        UpdateText(true);
    }

    private void UpdateText(bool first)
    {
        var state = manager.State(id);
        status.Text = state.Detail ?? state.Status.Label();
        if (log.Revision == shown) return;
        shown = log.Revision;
        var info = new ScrollInfo { cbSize = Marshal.SizeOf<ScrollInfo>(), fMask = SIF_ALL };
        var atBottom = first || !text.IsHandleCreated || !GetScrollInfo(text.Handle, SB_VERT, ref info) || info.nPos + info.nPage >= info.nMax;
        var firstLine = text.IsHandleCreated ? SendMessage(text.Handle, EM_GETFIRSTVISIBLELINE, IntPtr.Zero, IntPtr.Zero) : IntPtr.Zero;
        text.Text = string.Join("\r\n", log.Lines);
        if (atBottom)
        {
            text.SelectionStart = text.TextLength;
            text.ScrollToCaret();
        }
        else SendMessage(text.Handle, EM_LINESCROLL, IntPtr.Zero, firstLine);
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (keyData == Keys.Escape) { Close(); return true; }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) { timer.Dispose(); text.Font.Dispose(); }
        base.Dispose(disposing);
    }
}

/// Add Server / Edit <name>. One at a time.
internal sealed class EditWindow : Form
{
    private static EditWindow? current;

    private readonly ServerManager manager;
    private readonly ServerConfig? original;
    private readonly TextBox name = new(), cwd = new(), command = new(), args = new(), port = new(), url = new();
    private readonly TextBox env = new() { Multiline = true, AcceptsReturn = true, ScrollBars = ScrollBars.Vertical, Font = new Font("Consolas", 9.5f), Height = 80 };
    private readonly CheckBox startAtLogin = new() { Text = "Start this server when ServerHat starts", AutoSize = true };
    private readonly CheckBox openAtLogin = new() { Text = "Open ServerHat when I sign in to Windows", AutoSize = true };
    private readonly Label error = new() { AutoSize = true, ForeColor = Color.Firebrick, MaximumSize = new Size(470, 0) };

    public static void ShowFor(ServerManager manager, ServerConfig? editing)
    {
        current?.Close();
        current = new EditWindow(manager, editing);
        current.FormClosed += (_, _) => current = null;
        current.Show();
        current.Activate();
    }

    private EditWindow(ServerManager manager, ServerConfig? editing)
    {
        this.manager = manager;
        original = editing;
        Text = editing is null ? "Add Server" : $"Edit {editing.Name}";
        Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
        Font = SystemFonts.MessageBoxFont;
        AutoScaleMode = AutoScaleMode.Dpi;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;

        var grid = new TableLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = 3, Padding = new Padding(16) };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 340));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        var row = 0;
        void Field(string label, Control box, string? hint = null, Control? extra = null)
        {
            grid.Controls.Add(new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(0, 6, 8, 0) }, 0, row);
            box.Dock = DockStyle.Fill;
            grid.Controls.Add(box, 1, row);
            if (extra is not null) grid.Controls.Add(extra, 2, row);
            if (hint is not null && box is TextBox text) Cue(text, hint);
            row++;
        }
        var choose = new Button { Text = "Choose…", AutoSize = true };
        choose.Click += (_, _) => ChooseFolder();
        Field("Name", name);
        Field("Folder", cwd, null, choose);
        Field("Command", command, "node, npm, python, or a path");
        Field("Arguments", args, "server.mjs --port 4319");
        Field("Port", port, "optional");
        Field("URL", url, "optional, defaults to http://127.0.0.1:<port>");
        Field("Environment", env);
        grid.Controls.Add(new Label { Text = "One NAME=value per line. Arguments split on spaces; use quotes for spaces inside one.", AutoSize = true, ForeColor = SystemColors.GrayText, MaximumSize = new Size(440, 0) }, 1, row++);
        grid.Controls.Add(startAtLogin, 1, row++);
        grid.Controls.Add(openAtLogin, 1, row++);
        grid.Controls.Add(error, 1, row++);

        var save = new Button { Text = editing is null ? "Add" : "Save", AutoSize = true, MinimumSize = new Size(80, 0) };
        var cancel = new Button { Text = "Cancel", AutoSize = true, MinimumSize = new Size(80, 0), DialogResult = DialogResult.Cancel };
        save.Click += (_, _) => Save();
        cancel.Click += (_, _) => Close();
        var buttons = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, AutoSize = true, Dock = DockStyle.Fill, Margin = new Padding(0, 8, 0, 0) };
        buttons.Controls.Add(cancel);
        buttons.Controls.Add(save);
        grid.Controls.Add(buttons, 1, row);
        grid.SetColumnSpan(buttons, 2);
        Controls.Add(grid);
        AcceptButton = save;
        CancelButton = cancel;

        cwd.Text = "~";
        openAtLogin.Checked = Settings.OpenAtLogin;
        if (editing is not null)
        {
            name.Text = editing.Name;
            cwd.Text = editing.Cwd;
            command.Text = editing.Command;
            args.Text = string.Join(" ", editing.Args.Select(Display));
            port.Text = editing.Port?.ToString() ?? "";
            url.Text = editing.Url ?? "";
            env.Text = string.Join("\r\n", editing.Env.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => $"{p.Key}={p.Value}"));
            startAtLogin.Checked = editing.StartAtLogin;
        }
        openAtLogin.Visible = startAtLogin.Checked;
        startAtLogin.CheckedChanged += (_, _) => openAtLogin.Visible = startAtLogin.Checked;
        openAtLogin.CheckedChanged += (_, _) =>
        {
            try { Settings.OpenAtLogin = openAtLogin.Checked; }
            catch (Exception e) when (e is UnauthorizedAccessException or System.Security.SecurityException or IOException) { error.Text = e.Message; }
        };
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SendMessage(IntPtr hwnd, int message, IntPtr wParam, string lParam);

    /// Grey hint text in an empty box (EM_SETCUEBANNER).
    private static void Cue(TextBox box, string hint) => box.HandleCreated += (_, _) => SendMessage(box.Handle, 0x1501, IntPtr.Zero, hint);

    private void Save()
    {
        error.Text = "";
        var environment = new Dictionary<string, string>();
        foreach (var line in env.Text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).Where(l => l.Trim().Length > 0))
        {
            var eq = line.IndexOf('=');
            if (eq < 0) { error.Text = $"Environment line \"{line}\" needs NAME=value."; return; }
            environment[line.Substring(0, eq).Trim()] = line.Substring(eq + 1);
        }
        var portText = port.Text.Trim();
        if (portText.Length > 0 && !int.TryParse(portText, out _)) { error.Text = "Port must be a number."; return; }
        var config = new ServerConfig
        {
            Id = original?.Id ?? "", Name = name.Text.Trim(), Cwd = cwd.Text, Command = command.Text.Trim(),
            Args = SplitArguments(args.Text), Port = portText.Length > 0 ? int.Parse(portText) : null,
            Url = url.Text.Length == 0 ? null : url.Text, Env = environment, StartAtLogin = startAtLogin.Checked,
        };
        try
        {
            if (original is not null) manager.Update(original.Id, config); else manager.Add(config);
            Close();
        }
        catch (ManagerError e) { error.Text = e.Message; }
    }

    private void ChooseFolder()
    {
        using var dialog = new FolderBrowserDialog { Description = "Choose the folder the command runs in", ShowNewFolderButton = false };
        var start = Paths.Expand(cwd.Text);
        if (Directory.Exists(start)) dialog.SelectedPath = start;
        if (dialog.ShowDialog(this) == DialogResult.OK) cwd.Text = Paths.Abbreviate(dialog.SelectedPath);
    }

    /// An argument as it's typed in the Arguments box, quoted when it has spaces.
    private static string Display(string arg)
    {
        if (arg.Length > 0 && arg.IndexOfAny([' ', '\t', '"', '\'']) < 0) return arg;
        return arg.Contains("\"") ? "'" + arg + "'" : "\"" + arg + "\"";
    }

    /// Splits like a shell would for simple cases: spaces separate, quotes group.
    public static List<string> SplitArguments(string text)
    {
        var result = new List<string>();
        var current = new StringBuilder();
        char? quote = null;
        var inArg = false;
        foreach (var ch in text)
        {
            if (quote is { } q)
            {
                if (ch == q) quote = null; else current.Append(ch);
            }
            else if (ch is '\'' or '"') { quote = ch; inArg = true; }
            else if (ch is ' ' or '\t')
            {
                if (inArg) { result.Add(current.ToString()); current.Clear(); inArg = false; }
            }
            else { current.Append(ch); inArg = true; }
        }
        if (inArg) result.Add(current.ToString());
        return result;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) env.Font.Dispose();
        base.Dispose(disposing);
    }
}
