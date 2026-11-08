using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using Timer = System.Windows.Forms.Timer;

namespace ServerHat;

/// The panel the globe opens, the Windows counterpart of the macOS menu bar panel: a
/// header with what's running and Add, Settings and Quit buttons, then one card with every
/// server, each with its status, Start or Stop and a ⋯ menu. A server waiting for approval
/// shows its command, folder and environment names with Allow and Remove.
///
/// It's drawn by hand, at the monitor's DPI and in the system's light or dark colors, so
/// it looks the same at any scaling. Everything is laid out again on each paint; `Render`
/// with no Graphics only measures.
internal sealed class TrayPanel : Form
{
    private const int Width0 = 360, Pad = 12, MaxList = 560;

    private readonly TrayApp app;
    private readonly ServerManager manager;
    private readonly ToolTip tip = new() { ShowAlways = true };
    private readonly Timer timer = new() { Interval = 200 };
    private readonly List<Hit> hits = [];
    private Point mouse = new(-1, -1);
    private bool mouseDown;
    private string? shownTip;
    private bool dirty = true;
    private int scroll;
    private int listContent;
    private Rectangle listViewport;
    private DateTime hiddenAt;
    private bool menuOpen;
    private Palette colors = Palette.Light;
    private Fonts? fonts;

    /// For the screenshots: keep it open even when another window is active.
    public bool StayOpen { get; set; }
    /// For the screenshots: dark or light whatever Windows uses.
    public bool? ForceDark { get; set; }

    private sealed record Hit(Rectangle Rect, Action Click, string? Tip, bool InList);

    public TrayPanel(TrayApp app)
    {
        this.app = app;
        manager = app.Manager;
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        AutoScaleMode = AutoScaleMode.None;
        KeyPreview = true;
        Text = "ServerHat";
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        manager.Changed += () => dirty = true;
        timer.Tick += (_, _) =>
        {
            if (!dirty || !Visible) return;
            dirty = false;
            Fit();
            Invalidate();
        };
    }

    protected override CreateParams CreateParams
    {
        get
        {
            var p = base.CreateParams;
            p.ClassStyle |= 0x20000; // CS_DROPSHADOW
            p.ExStyle |= 0x80; // WS_EX_TOOLWINDOW: not in Alt+Tab
            return p;
        }
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        // Rounded corners like Windows 11's own flyouts (ignored on Windows 10).
        var round = 2; // DWMWCP_ROUND
        DwmSetWindowAttribute(Handle, 33 /* DWMWA_WINDOW_CORNER_PREFERENCE */, ref round, sizeof(int));
    }

    // MARK: Showing

    /// True just after a click elsewhere closed it, so the click on the globe that caused
    /// that doesn't open it again.
    public bool JustHidden => (DateTime.UtcNow - hiddenAt).TotalMilliseconds < 300;

    /// Opens above (or beside) the taskbar, next to where the globe was clicked.
    public void ShowNear(Point click)
    {
        scroll = 0;
        var screen = Screen.FromPoint(click);
        if (!IsHandleCreated) CreateHandle();
        Location = screen.WorkingArea.Location; // so DeviceDpi is this monitor's
        colors = (ForceDark ?? Theme.Current == Theme.Dark) ? Palette.Dark : Palette.Light;
        Size = new Size(S(Width0), Render(null));
        var area = screen.WorkingArea;
        var bounds = screen.Bounds;
        var gap = S(12);
        int x = click.X - Width / 2, y;
        if (area.Bottom < bounds.Bottom) y = area.Bottom - Height - gap; // taskbar at the bottom
        else if (area.Top > bounds.Top) y = area.Top + gap;
        else
        {
            y = click.Y - Height / 2;
            x = area.Left > bounds.Left ? area.Left + gap : area.Right - Width - gap;
        }
        Location = new Point(Clamp(x, area.Left + gap, area.Right - Width - gap), Clamp(y, area.Top + gap, area.Bottom - Height - gap));
        dirty = false;
        Show();
        Activate();
        timer.Start();
    }

    public void HideNow()
    {
        if (!Visible) return;
        Hide();
        hiddenAt = DateTime.UtcNow;
        timer.Stop();
        tip.Hide(this);
        shownTip = null;
    }

    protected override void OnDeactivate(EventArgs e)
    {
        base.OnDeactivate(e);
        if (!menuOpen && !StayOpen) HideNow();
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (keyData == Keys.Escape) { HideNow(); return true; }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    /// Resizes to the content, keeping the edge next to the taskbar where it is.
    private void Fit()
    {
        var height = Render(null);
        if (height == Height) return;
        var screen = Screen.FromControl(this).WorkingArea;
        var bottomAnchored = Bottom > screen.Top + screen.Height / 2;
        var bottom = Bottom;
        Height = height;
        if (bottomAnchored) Top = bottom - height;
    }

    protected override void OnDpiChanged(DpiChangedEventArgs e)
    {
        base.OnDpiChanged(e);
        fonts?.Dispose();
        fonts = null;
        dirty = true;
    }

    // MARK: Mouse

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        mouse = e.Location;
        var hit = HitAt(e.Location);
        Cursor = hit is null ? Cursors.Default : Cursors.Hand;
        if (hit?.Tip != shownTip)
        {
            shownTip = hit?.Tip;
            if (shownTip is null) tip.Hide(this);
            else tip.Show(shownTip, this, e.X, e.Y + S(20), 4000);
        }
        Invalidate();
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        mouse = new Point(-1, -1);
        tip.Hide(this);
        shownTip = null;
        Invalidate();
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        mouseDown = e.Button == MouseButtons.Left;
        Invalidate();
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        var wasDown = mouseDown;
        mouseDown = false;
        Invalidate();
        if (wasDown && e.Button == MouseButtons.Left && HitAt(e.Location) is { } hit) hit.Click();
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);
        if (listContent <= listViewport.Height) return;
        scroll = Clamp(scroll - e.Delta * S(40) / 120, 0, listContent - listViewport.Height);
        Invalidate();
    }

    private Hit? HitAt(Point p) => hits.LastOrDefault(h => h.Rect.Contains(p) && (!h.InList || listViewport.Contains(p)));

    /// A menu under a button; the panel stays open while it's showing.
    private void DropMenu(Rectangle under, params ToolStripItem[] items)
    {
        var menu = new ContextMenuStrip { ShowImageMargin = false, ShowCheckMargin = items.OfType<ToolStripMenuItem>().Any(i => i.Checked) };
        menu.Items.AddRange(items);
        menu.Closed += (_, _) =>
        {
            menuOpen = false;
            BeginInvoke(new Action(() =>
            {
                menu.Dispose();
                if (Visible && !StayOpen && ActiveForm != this) HideNow();
            }));
        };
        menuOpen = true;
        menu.Show(this, new Point(under.Left, under.Bottom + S(4)));
    }

    /// Runs a menu action after the panel has closed, so dialogs it opens come to the front.
    private ToolStripMenuItem Item(string text, Action action, bool enabled = true) =>
        new(text, null, (_, _) => { HideNow(); action(); }) { Enabled = enabled };

    // MARK: Drawing

    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        e.Graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
        Render(e.Graphics);
    }

    private int S(float value) => (int)Math.Round(value * DeviceDpi / 96f);

    private Fonts F => fonts ??= new Fonts(DeviceDpi / 96f);

    /// Lays out (and with a Graphics, draws) the whole panel, and returns its height.
    private int Render(Graphics? g)
    {
        hits.Clear();
        var width = S(Width0);
        g?.Clear(colors.Page);
        var x = S(Pad);
        var inner = width - 2 * S(Pad);
        var y = S(Pad);
        y = Header(g, x, y, inner) + S(10);

        if (manager.Servers.Count == 0) y = EmptyState(g, x, y, inner) + S(10);
        foreach (var problem in new[] { manager.ConfigError, app.ApiProblem }.OfType<string>()) y = Notice(g, x, y, inner, problem) + S(10);
        if (manager.Servers.Count > 0) y = ServerCard(g, x, y, inner) + S(10);
        if (app.Update is { } update) y = UpdateCard(g, x, y, inner, update) + S(10);
        return y - S(10) + S(Pad);
    }

    private int Header(Graphics? g, int x, int y, int width)
    {
        var f = F;
        var h = S(30);
        var button = S(30);
        var gap = S(6);
        var capsule = new Rectangle(x, y, width - 3 * (button + gap), h);
        if (g is not null)
        {
            FillRound(g, capsule, h / 2, colors.Card);
            StrokeRound(g, capsule, h / 2, colors.Stroke);
            var tx = capsule.Left + S(12);
            FilledGlyph(g, Glyphs.Globe, new Rectangle(tx, y + (h - S(16)) / 2, S(16), S(16)), colors.Accent, colors.Card, f.GlyphTiny);
            tx += S(22);
            var active = manager.States.Count(s => s.Status is ServerStatus.Running or ServerStatus.Starting);
            string? right = null;
            var rightColor = colors.Secondary;
            if (active > 0) { right = Strings.F(Strings.Current.Active, active); rightColor = colors.Green; }
            else if (manager.Servers.Count > 0) right = Strings.Current.AllStopped;
            var rightWidth = right is null ? 0 : Measure(right, active > 0 ? f.SmallBold : f.Small).Width;
            var rx = capsule.Right - S(12) - rightWidth;
            Label(g, "ServerHat", f.Bold, colors.Text, new Rectangle(tx, y, rx - tx - S(8), h), TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
            if (right is not null)
            {
                if (active > 0)
                {
                    using var dot = new SolidBrush(colors.Green);
                    g.FillEllipse(dot, rx - S(12), y + h / 2 - S(3.5f), S(7), S(7));
                }
                Label(g, right, active > 0 ? f.SmallBold : f.Small, rightColor, new Rectangle(rx, y, rightWidth + S(2), h), TextFormatFlags.VerticalCenter);
            }
        }
        var bx = capsule.Right + gap;
        var add = new Rectangle(bx, y, button, button);
        var settings = new Rectangle(bx + button + gap, y, button, button);
        var quit = new Rectangle(bx + 2 * (button + gap), y, button, button);
        CircleButton(g, add, Glyphs.Add, colors.Text, colors.Card, true, Strings.Current.AddOrImport,
            () => DropMenu(add, Item(Strings.Current.AddServerMenu, () => EditWindow.ShowFor(manager, null)), Item(Strings.Current.ImportLaunchMenu, app.ImportLaunchJson)));
        CircleButton(g, settings, Glyphs.Settings, colors.Text, colors.Card, true, Strings.Current.SettingsAndAbout, () => DropMenu(settings, SettingsItems()));
        CircleButton(g, quit, Glyphs.Power, colors.Text, colors.Card, true, Strings.Current.QuitServerHat, ConfirmQuit);
        return y + h;
    }

    private ToolStripItem[] SettingsItems()
    {
        var atLogin = new ToolStripMenuItem(Strings.Current.OpenAtSignIn) { Checked = Settings.OpenAtLogin };
        atLogin.Click += (_, _) =>
        {
            try { Settings.OpenAtLogin = !Settings.OpenAtLogin; }
            catch (Exception e) when (e is UnauthorizedAccessException or System.Security.SecurityException or IOException) { MessageBox.Show(e.Message, "ServerHat"); }
        };
        var approve = new ToolStripMenuItem(Strings.Current.ApproveNewServers) { Checked = manager.RequireApproval };
        approve.Click += (_, _) => manager.RequireApproval = !manager.RequireApproval;
        return [
            Item(Strings.Current.OpenServersJson, () => Program.OpenFile(Paths.ConfigFile)),
            Item(Strings.Current.OpenLogsFolder, () => { Directory.CreateDirectory(Paths.LogFolder); Program.OpenFile(Paths.LogFolder); }),
            atLogin,
            approve,
            new ToolStripSeparator(),
            Item(Strings.Current.CopyForClaude, () => app.CopySetup(codex: false)),
            Item(Strings.Current.CopyForCodex, () => app.CopySetup(codex: true)),
            new ToolStripSeparator(),
            Item(Strings.Current.AboutServerHat, AboutWindow.ShowShared),
        ];
    }

    /// Quitting stops every server ServerHat started, so ask first when any are running.
    private void ConfirmQuit()
    {
        var count = manager.RunningCount;
        if (count > 0)
        {
            HideNow();
            var answer = MessageBox.Show(count == 1 ? Strings.Current.QuitOne : Strings.F(Strings.Current.QuitMany, count), Strings.Current.QuitTitle,
                MessageBoxButtons.OKCancel, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2);
            if (answer != DialogResult.OK) return;
        }
        app.Quit();
    }

    private int EmptyState(Graphics? g, int x, int y, int width)
    {
        var f = F;
        var pad = S(12);
        var textX = x + pad + S(42) + S(12);
        var textWidth = x + width - pad - textX;
        var hint = Strings.Current.NoServersHint;
        var hintHeight = Measure(hint, f.Small, textWidth, wrap: true).Height;
        var titleHeight = Measure(Strings.Current.NoServers, f.Title).Height;
        var top = Math.Max(S(42), titleHeight + S(2) + hintHeight);
        var height = pad + top + S(14) + S(38) + S(8) + S(38) + pad;
        var card = new Rectangle(x, y, width, height);
        if (g is not null)
        {
            Card(g, card);
            using (var tint = new SolidBrush(Color.FromArgb(33, colors.Accent))) g.FillEllipse(tint, x + pad, y + pad + (top - S(42)) / 2, S(42), S(42));
            Glyph(g, Glyphs.Globe, new Rectangle(x + pad, y + pad + (top - S(42)) / 2, S(42), S(42)), colors.Accent, f.Glyph);
            var ty = y + pad + (top - titleHeight - S(2) - hintHeight) / 2;
            Label(g, Strings.Current.NoServers, f.Title, colors.Text, new Rectangle(textX, ty, textWidth, titleHeight), 0);
            Label(g, hint, f.Small, colors.Secondary, new Rectangle(textX, ty + titleHeight + S(2), textWidth, hintHeight), TextFormatFlags.WordBreak);
        }
        var by = y + pad + top + S(14);
        PillButton(g, new Rectangle(x + pad, by, width - 2 * pad, S(38)), Strings.Current.AddAServer, colors.Accent, Color.White, f.Bold, () => { HideNow(); EditWindow.ShowFor(manager, null); });
        PillButton(g, new Rectangle(x + pad, by + S(46), width - 2 * pad, S(38)), Strings.Current.ImportLaunch, colors.Control, colors.Text, f.Bold, () => { HideNow(); app.ImportLaunchJson(); });
        return y + height;
    }

    private int Notice(Graphics? g, int x, int y, int width, string text)
    {
        var pad = S(12);
        var textWidth = width - 2 * pad - S(26);
        var height = Measure(text, F.Small, textWidth, wrap: true).Height + 2 * pad;
        if (g is not null)
        {
            Card(g, new Rectangle(x, y, width, height));
            Glyph(g, Glyphs.Warning, new Rectangle(x + pad, y + pad, S(18), S(18)), colors.Orange, F.GlyphSmall);
            Label(g, text, F.Small, colors.Text, new Rectangle(x + pad + S(26), y + pad, textWidth, height - 2 * pad), TextFormatFlags.WordBreak);
        }
        return y + height;
    }

    private int UpdateCard(Graphics? g, int x, int y, int width, UpdateCheck.Release update)
    {
        var height = S(44);
        var card = new Rectangle(x, y, width, height);
        var hot = Hot(card);
        if (g is not null)
        {
            Card(g, card, hot);
            Glyph(g, Glyphs.Download, new Rectangle(x + S(12), y, S(20), height), colors.Accent, F.Glyph);
            Label(g, Strings.F(Strings.Current.UpdateTitle, update.Version), F.Medium, colors.Text, new Rectangle(x + S(40), y, width - S(70), height), TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
            Glyph(g, Glyphs.Chevron, new Rectangle(x + width - S(28), y, S(16), height), colors.Secondary, F.GlyphTiny);
        }
        hits.Add(new Hit(card, () => { HideNow(); AboutWindow.Open(update.Page); }, null, false));
        return y + height;
    }

    private int ServerCard(Graphics? g, int x, int y, int width)
    {
        var f = F;
        var pad = S(12);
        var inner = width - 2 * pad;
        var cx = x + pad;
        var top = y + pad;
        var cy = top;
        var servers = manager.Servers;

        // The card's frame is drawn first, once its height is known.
        int SummaryRow(Graphics? gg)
        {
            var h = S(26);
            var active = servers.Count(s => manager.State(s.Id).Status is ServerStatus.Running or ServerStatus.Starting);
            if (gg is not null)
                Label(gg, Strings.F(Strings.Current.ServerCount, servers.Count), f.Medium, colors.Secondary, new Rectangle(cx, cy, inner / 2, h), TextFormatFlags.VerticalCenter);
            var right = cx + inner;
            if (manager.RunningCount > 0)
            {
                var w = Measure(Strings.Current.StopAll, f.SmallBold).Width + S(22);
                right -= w;
                TintButton(gg, new Rectangle(right, cy + (h - S(26)) / 2, w, S(26)), Strings.Current.StopAll, colors.Red, () => manager.StopAll(), list: false);
                right -= S(6);
            }
            if (active < servers.Count)
            {
                var w = Measure(Strings.Current.StartAll, f.SmallBold).Width + S(22);
                right -= w;
                TintButton(gg, new Rectangle(right, cy + (h - S(26)) / 2, w, S(26)), Strings.Current.StartAll, colors.Accent, () => manager.StartAll(), list: false);
            }
            return h;
        }

        // Measure everything, then draw: the list scrolls when it's taller than MaxList.
        var summary = servers.Count > 1 ? S(26) + S(12) + 1 + S(12) : 0;
        var rowHeights = servers.Select(s => Row(null, s, 0, 0, inner)).ToList();
        listContent = rowHeights.Sum() + Math.Max(0, servers.Count - 1) * (S(24) + 1);
        var viewport = Math.Min(listContent, S(MaxList));
        scroll = Clamp(scroll, 0, Math.Max(0, listContent - viewport));
        var height = pad + summary + viewport + pad;
        var card = new Rectangle(x, y, width, height);
        if (g is not null) Card(g, card);

        if (servers.Count > 1)
        {
            cy += SummaryRow(g) + S(12);
            if (g is not null) Divider(g, cx, cy, inner);
            cy += 1 + S(12);
        }
        listViewport = new Rectangle(x, cy, width, viewport);
        var state = g?.Save();
        g?.SetClip(new Rectangle(x, cy - S(2), width, viewport + S(4)));
        var ry = cy - scroll;
        for (var i = 0; i < servers.Count; i++)
        {
            if (i > 0)
            {
                ry += S(12);
                if (g is not null) Divider(g, cx, ry, inner);
                ry += 1 + S(12);
            }
            // Rows outside the viewport are measured but not drawn or clickable.
            var visible = ry + rowHeights[i] > cy && ry < cy + viewport;
            Row(visible ? g : null, servers[i], cx, ry, inner, register: visible);
            ry += rowHeights[i];
        }
        if (state is not null) g!.Restore(state);
        if (g is not null && listContent > viewport)
        {
            // A thin scroll indicator.
            var barHeight = Math.Max(S(24), viewport * viewport / listContent);
            var barY = cy + (viewport - barHeight) * scroll / (listContent - viewport);
            FillRound(g, new Rectangle(x + width - S(6), barY, S(3), barHeight), S(1.5f), colors.Grey);
        }
        return y + height;
    }

    /// One server; returns its height. Measures only when `g` is null.
    private int Row(Graphics? g, ServerConfig server, int x, int y, int width, bool register = false)
    {
        if (manager.NeedsApproval(server.Id)) return PendingRow(g, server, x, y, width, register);
        var f = F;
        var state = manager.State(server.Id);
        var id = server.Id;
        var active = state.Status is ServerStatus.Running or ServerStatus.Starting or ServerStatus.Stopping;
        var button = S(28);
        var gap = S(6);
        var buttons = (state.Status == ServerStatus.Running ? 3 : 2);
        var buttonsWidth = buttons * button + (buttons - 1) * gap;
        var textX = x + S(22) + S(10);
        var textWidth = width - S(32) - buttonsWidth - S(4);
        var nameHeight = Measure(server.Name, f.Bold).Height;
        var link = state.Status == ServerStatus.Running ? server.OpenUrl : null;
        var detail = Detail(server, state);
        var detailFont = f.SmallMedium;
        var detailHeight = link is not null ? Measure("x", detailFont).Height
            : Math.Min(Measure(detail, detailFont, textWidth, wrap: true).Height, 2 * Measure("x", detailFont).Height);
        var height = Math.Max(button, nameHeight + S(2) + detailHeight);
        if (g is null && !register) return height;

        var color = StatusColor(state.Status);
        var textTop = y + (height - nameHeight - S(2) - detailHeight) / 2;
        if (g is not null)
        {
            StatusIcon(g, state.Status, new Rectangle(x, y, S(22), height), color);
            Label(g, server.Name, f.Bold, colors.Text, new Rectangle(textX, textTop, textWidth, nameHeight), TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine);
        }
        var detailRect = new Rectangle(textX, textTop + nameHeight + S(2), textWidth, detailHeight);
        if (link is not null)
        {
            var shown = Display(link) + " ↗";
            var size = Measure(shown, detailFont);
            var linkRect = new Rectangle(detailRect.X, detailRect.Y, Math.Min(size.Width, textWidth), detailRect.Height);
            if (g is not null) Label(g, shown, detailFont, colors.Accent, linkRect, TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine, underline: Hot(linkRect));
            if (register) hits.Add(new Hit(linkRect, () => AboutWindow.Open(link), Strings.F(Strings.Current.OpenLink, link), true));
        }
        else if (g is not null)
        {
            Label(g, detail, detailFont, state.Status == ServerStatus.Stopped ? colors.Secondary : color, detailRect, TextFormatFlags.WordBreak | TextFormatFlags.EndEllipsis);
        }

        // Quiet grey buttons, so the status colors stand out. Restart only while it's running.
        var bx = x + width - buttonsWidth;
        var by = y + (height - button) / 2;
        if (state.Status == ServerStatus.Running)
        {
            CircleButton(g, new Rectangle(bx, by, button, button), Glyphs.Restart, colors.Text, colors.Control, true, Strings.Current.Restart,
                () => Try(() => manager.Restart(id)), list: register);
            bx += button + gap;
        }
        if (active)
            CircleButton(g, new Rectangle(bx, by, button, button), Glyphs.Stop, colors.Text, colors.Control, state.Status != ServerStatus.Stopping, Strings.Current.Stop,
                () => manager.Stop(id), list: register);
        else
            CircleButton(g, new Rectangle(bx, by, button, button), Glyphs.Play, colors.Accent, colors.Control, state.Status != ServerStatus.PortInUse, Strings.Current.Start,
                () => Try(() => manager.Start(id)), list: register);
        bx += button + gap;
        var more = new Rectangle(bx, by, button, button);
        CircleButton(g, more, Glyphs.More, colors.Text, colors.Control, true, Strings.Current.ServerMenu, () => DropMenu(more, ServerItems(server)), list: register);
        return height;
    }

    private ToolStripItem[] ServerItems(ServerConfig server)
    {
        var id = server.Id;
        var state = manager.State(id);
        var items = new List<ToolStripItem>();
        if (server.OpenUrl is { } url) items.Add(Item(Strings.Current.OpenInBrowser, () => AboutWindow.Open(url)));
        items.Add(Item(Strings.Current.ViewLogs, () => LogWindow.ShowFor(manager, server)));
        items.Add(Item(Strings.Current.EditMenu, () => EditWindow.ShowFor(manager, server)));
        items.Add(Item(Strings.Current.OpenFolder, () => Program.OpenFile(server.WorkingDirectory), Directory.Exists(server.WorkingDirectory)));
        if (state.Status == ServerStatus.PortInUse && state.Pid is { } pid)
        {
            items.Add(new ToolStripSeparator());
            items.Add(Item(Strings.F(Strings.Current.EndProcessMenu, server.Port), () => app.ConfirmEnd(server, state, pid)));
        }
        items.Add(new ToolStripSeparator());
        items.Add(Item(Strings.Current.RemoveMenu, () => app.ConfirmRemove(server)));
        return [.. items];
    }

    /// A server added or changed through the API or servers.json, waiting for the user.
    private int PendingRow(Graphics? g, ServerConfig server, int x, int y, int width, bool register)
    {
        var f = F;
        var id = server.Id;
        var active = manager.State(id).Status is ServerStatus.Running or ServerStatus.Starting or ServerStatus.Stopping;
        var subtitle = active ? Strings.Current.ChangedAllow : Strings.Current.NewAllow;
        var textX = x + S(32);
        var textWidth = width - S(32);
        var nameHeight = Measure(server.Name, f.Bold).Height;
        var subtitleHeight = Measure(subtitle, f.SmallMedium, textWidth, wrap: true).Height;
        var head = Math.Max(S(22), nameHeight + S(2) + subtitleHeight);

        var boxPad = S(10);
        var boxInner = width - 2 * boxPad;
        var command = ServerManager.CommandLine(server);
        var lineHeight = Measure("x", f.Mono).Height;
        var commandHeight = Math.Min(Measure(command, f.Mono, boxInner, wrap: true).Height, 3 * lineHeight);
        var smallHeight = Measure("x", f.Small).Height;
        var envNames = string.Join(", ", server.Env.Keys.OrderBy(k => k, StringComparer.Ordinal));
        var boxHeight = boxPad + commandHeight + S(3) + smallHeight + (server.Env.Count > 0 ? S(3) + smallHeight : 0) + boxPad;
        var pill = S(26);
        var height = head + S(10) + boxHeight + S(10) + pill;
        if (g is null && !register) return height;

        if (g is not null)
        {
            Glyph(g, Glyphs.Hand, new Rectangle(x, y, S(22), head), colors.Orange, f.Symbol);
            Label(g, server.Name, f.Bold, colors.Text, new Rectangle(textX, y, textWidth, nameHeight), TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine);
            Label(g, subtitle, f.SmallMedium, colors.Orange, new Rectangle(textX, y + nameHeight + S(2), textWidth, subtitleHeight), TextFormatFlags.WordBreak);
            var box = new Rectangle(x, y + head + S(10), width, boxHeight);
            FillRound(g, box, S(10), colors.Control);
            var ty = box.Top + boxPad;
            // Character breaks, so a long path or argument wraps instead of being cut.
            Label(g, command, f.Mono, colors.Text, new Rectangle(box.Left + boxPad, ty, boxInner, commandHeight), TextFormatFlags.WordBreak | TextFormatFlags.EndEllipsis);
            ty += commandHeight + S(3);
            Label(g, Strings.F(Strings.Current.InFolder, server.Cwd), f.Small, colors.Secondary, new Rectangle(box.Left + boxPad, ty, boxInner, smallHeight), TextFormatFlags.PathEllipsis | TextFormatFlags.SingleLine);
            if (server.Env.Count > 0)
                Label(g, Strings.F(Strings.Current.WithEnvironment, envNames), f.Small, colors.Secondary, new Rectangle(box.Left + boxPad, ty + smallHeight + S(3), boxInner, smallHeight), TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine);
        }
        var py = y + head + S(10) + boxHeight + S(10);
        var allowWidth = Measure(Strings.Current.Allow, f.SmallBold).Width + S(28);
        var removeWidth = Measure(Strings.Current.Remove, f.SmallBold).Width + S(28);
        var editWidth = Measure(Strings.Current.EditMenu, f.SmallBold).Width + S(28);
        var shown = ServerManager.Fingerprint(server);
        TintButton(g, new Rectangle(x, py, allowWidth, pill), Strings.Current.Allow, colors.Accent, () =>
        {
            // Only allow what the panel showed.
            if (manager.Server(id) is { } now && ServerManager.Fingerprint(now) == shown) ApprovalWindow.Allow(manager, id);
        }, list: register);
        TintButton(g, new Rectangle(x + allowWidth + S(8), py, removeWidth, pill), Strings.Current.Remove, colors.Red, () =>
        {
            HideNow();
            if (manager.Server(id) is { } current) app.ConfirmRemove(current);
        }, list: register);
        TintButton(g, new Rectangle(x + width - editWidth, py, editWidth, pill), Strings.Current.EditMenu, colors.Secondary, () =>
        {
            HideNow();
            if (manager.Server(id) is { } current) EditWindow.ShowFor(manager, current);
        }, list: register);
        return height;
    }

    private static void Try(Action action)
    {
        try { action(); } catch (ManagerError) { } // shows in the server's status
    }

    private static string Detail(ServerConfig server, ServerState state)
    {
        var detail = state.Shown();
        return state.Detail is null && state.Status != ServerStatus.Running && server.Port is { } port ? $"{detail} · :{port}" : detail;
    }

    /// "127.0.0.1:4319" for http://127.0.0.1:4319/, keeping any path.
    private static string Display(string url)
    {
        foreach (var prefix in new[] { "http://", "https://" })
            if (url.StartsWith(prefix, StringComparison.Ordinal)) url = url.Substring(prefix.Length);
        return url.EndsWith("/", StringComparison.Ordinal) ? url.Substring(0, url.Length - 1) : url;
    }

    private Color StatusColor(ServerStatus status) => status switch
    {
        ServerStatus.Running => colors.Green,
        ServerStatus.Starting or ServerStatus.Stopping => colors.Yellow,
        ServerStatus.Crashed or ServerStatus.Failed => colors.Red,
        ServerStatus.PortInUse => colors.Orange,
        _ => colors.Grey,
    };

    /// The Mac's symbols: bolt.fill, hourglass, exclamationmark.triangle.fill,
    /// exclamationmark.circle.fill and globe.americas.
    private void StatusIcon(Graphics g, ServerStatus status, Rectangle rect, Color color)
    {
        var f = F;
        switch (status)
        {
            case ServerStatus.Running: Glyph(g, Glyphs.Bolt, rect, color, f.Emoji); break;
            case ServerStatus.Starting or ServerStatus.Stopping: Glyph(g, Glyphs.Clock, rect, color, f.GlyphStatus); break;
            case ServerStatus.Crashed or ServerStatus.Failed: Glyph(g, Glyphs.Warning, rect, color, f.GlyphStatus); break;
            case ServerStatus.PortInUse:
                var size = S(16);
                var circle = new Rectangle(rect.X + (rect.Width - size) / 2, rect.Y + (rect.Height - size) / 2, size, size);
                using (var brush = new SolidBrush(color)) g.FillEllipse(brush, circle);
                Label(g, "!", f.SmallBold, colors.Card, circle, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
                break;
            default: Glyph(g, Glyphs.Globe, rect, color, f.GlyphStatus); break;
        }
    }

    // MARK: Pieces

    private bool Hot(Rectangle rect) => rect.Contains(mouse);

    private void Card(Graphics g, Rectangle rect, bool hot = false)
    {
        FillRound(g, rect, S(16), hot ? Mix(colors.Card, colors.Text, 0.04f) : colors.Card);
        StrokeRound(g, rect, S(16), colors.Stroke);
    }

    private void Divider(Graphics g, int x, int y, int width)
    {
        using var pen = new Pen(colors.Stroke);
        g.DrawLine(pen, x, y, x + width, y);
    }

    private void CircleButton(Graphics? g, Rectangle rect, string glyph, Color foreground, Color fill, bool enabled, string tip, Action click, bool list = false)
    {
        if (enabled) hits.Add(new Hit(rect, click, tip, list));
        if (g is null) return;
        var hot = enabled && Hot(rect);
        var color = hot ? Mix(fill, colors.Text, mouseDown ? 0.16f : 0.08f) : fill;
        using (var brush = new SolidBrush(color)) g.FillEllipse(brush, rect);
        if (fill == colors.Card)
            using (var pen = new Pen(colors.Stroke)) g.DrawEllipse(pen, rect.X, rect.Y, rect.Width - 1, rect.Height - 1);
        Glyph(g, glyph, rect, enabled ? foreground : Mix(foreground, fill, 0.65f), F.GlyphSmall);
    }

    /// A small tinted capsule, for secondary actions such as Start all and Allow.
    private void TintButton(Graphics? g, Rectangle rect, string text, Color color, Action click, bool list)
    {
        hits.Add(new Hit(rect, click, null, list));
        if (g is null) return;
        var hot = Hot(rect);
        var background = Mix(colors.Card, color, hot ? (mouseDown ? 0.28f : 0.2f) : 0.12f);
        FillRound(g, rect, rect.Height / 2, background);
        Label(g, text, F.SmallBold, color, rect, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
    }

    private void PillButton(Graphics? g, Rectangle rect, string text, Color fill, Color foreground, Font font, Action click)
    {
        hits.Add(new Hit(rect, click, null, false));
        if (g is null) return;
        var color = Hot(rect) ? Mix(fill, mouseDown ? Color.Black : colors.Text, 0.1f) : fill;
        FillRound(g, rect, rect.Height / 2, color);
        Label(g, text, font, foreground, rect, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
    }

    /// A glyph knocked out of a filled circle, for symbols the icon fonts only have as outlines.
    private static void FilledGlyph(Graphics g, string glyph, Rectangle rect, Color fill, Color color, Font font)
    {
        using (var brush = new SolidBrush(fill)) g.FillEllipse(brush, rect);
        Glyph(g, glyph, rect, color, font);
    }

    private static void Glyph(Graphics g, string glyph, Rectangle rect, Color color, Font font) =>
        TextRenderer.DrawText(g, glyph, font, rect, color, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);

    private static void Label(Graphics g, string text, Font font, Color color, Rectangle rect, TextFormatFlags flags, bool underline = false)
    {
        if (underline)
        {
            using var lined = new Font(font, font.Style | FontStyle.Underline);
            TextRenderer.DrawText(g, text, lined, rect, color, flags | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding);
            return;
        }
        TextRenderer.DrawText(g, text, font, rect, color, flags | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding);
    }

    private static Size Measure(string text, Font font, int width = int.MaxValue, bool wrap = false)
    {
        var flags = TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding | (wrap ? TextFormatFlags.WordBreak : TextFormatFlags.SingleLine);
        return TextRenderer.MeasureText(text.Length == 0 ? " " : text, font, new Size(width, int.MaxValue), flags);
    }

    private static GraphicsPath RoundPath(Rectangle rect, float radius)
    {
        var r = Math.Min(radius, Math.Min(rect.Width, rect.Height) / 2f);
        var d = 2 * r;
        var path = new GraphicsPath();
        var box = new RectangleF(rect.X, rect.Y, rect.Width - 1, rect.Height - 1);
        if (d <= 0) { path.AddRectangle(box); return path; }
        path.AddArc(box.X, box.Y, d, d, 180, 90);
        path.AddArc(box.Right - d, box.Y, d, d, 270, 90);
        path.AddArc(box.Right - d, box.Bottom - d, d, d, 0, 90);
        path.AddArc(box.X, box.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    private static void FillRound(Graphics g, Rectangle rect, float radius, Color color)
    {
        using var path = RoundPath(rect, radius);
        using var brush = new SolidBrush(color);
        g.FillPath(brush, path);
    }

    private static void StrokeRound(Graphics g, Rectangle rect, float radius, Color color)
    {
        using var path = RoundPath(rect, radius);
        using var pen = new Pen(color);
        g.DrawPath(pen, path);
    }

    /// `a` moved `amount` of the way to `b`, opaque, so GDI text can sit on it.
    private static Color Mix(Color a, Color b, float amount)
    {
        int Channel(int x, int y) => (int)Math.Round(x + (y - x) * amount);
        return Color.FromArgb(255, Channel(a.R, b.R), Channel(a.G, b.G), Channel(a.B, b.B));
    }

    private static int Clamp(int value, int min, int max) => max < min ? min : value < min ? min : value > max ? max : value;

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            timer.Dispose();
            tip.Dispose();
            fonts?.Dispose();
        }
        base.Dispose(disposing);
    }

    // MARK: Colors, fonts and symbols

    /// The macOS menu's palette: a grey page, cards that stand out from it, vivid accents.
    private sealed record Palette(Color Page, Color Card, Color Accent, Color Green, Color Red, Color Orange, Color Yellow,
        Color Text, Color Secondary, Color Grey, Color Stroke, Color Control)
    {
        public static readonly Palette Light = new(
            Color.FromArgb(243, 244, 247), Color.White, Color.FromArgb(67, 98, 255), Color.FromArgb(22, 173, 94),
            Color.FromArgb(225, 58, 58), Color.FromArgb(245, 128, 25), Color.FromArgb(239, 168, 0),
            Color.FromArgb(26, 28, 35), Color.FromArgb(107, 113, 126), Color.FromArgb(153, 157, 166),
            Color.FromArgb(225, 226, 230), Color.FromArgb(239, 240, 243));
        public static readonly Palette Dark = new(
            Color.FromArgb(20, 20, 22), Color.FromArgb(30, 31, 34), Color.FromArgb(82, 111, 255), Color.FromArgb(67, 207, 126),
            Color.FromArgb(239, 85, 85), Color.FromArgb(255, 157, 67), Color.FromArgb(255, 200, 59),
            Color.FromArgb(245, 246, 248), Color.FromArgb(157, 162, 173), Color.FromArgb(126, 131, 142),
            Color.FromArgb(50, 51, 55), Color.FromArgb(46, 47, 51));
    }

    private sealed class Fonts : IDisposable
    {
        public Font Bold, Medium, Small, SmallMedium, SmallBold, Title, Mono, Glyph, GlyphSmall, GlyphTiny, GlyphStatus, Emoji, Symbol;

        public Fonts(float scale)
        {
            Font Make(string family, float px, FontStyle style = FontStyle.Regular) => new(family, px * scale, style, GraphicsUnit.Pixel);
            const string ui = "Segoe UI", semibold = "Segoe UI Semibold";
            Bold = Make(semibold, 14);
            Title = Make(semibold, 15.5f);
            Medium = Make(ui, 13.5f);
            Small = Make(ui, 12.5f);
            SmallMedium = Make(ui, 12.5f);
            SmallBold = Make(semibold, 12.5f);
            Mono = Make("Consolas", 12.5f);
            var icons = FontFamily.Families.Any(f => f.Name == "Segoe Fluent Icons") ? "Segoe Fluent Icons" : "Segoe MDL2 Assets";
            Glyph = Make(icons, 18);
            GlyphStatus = Make(icons, 16);
            GlyphSmall = Make(icons, 13);
            GlyphTiny = Make(icons, 10);
            Emoji = Make("Segoe UI Emoji", 15);
            Symbol = Make("Segoe UI Symbol", 15);
        }

        public void Dispose()
        {
            foreach (var font in new[] { Bold, Medium, Small, SmallMedium, SmallBold, Title, Mono, Glyph, GlyphSmall, GlyphTiny, GlyphStatus, Emoji, Symbol }) font.Dispose();
        }
    }

    /// Segoe MDL2 Assets / Segoe Fluent Icons code points, solid where the Mac's symbols are filled.
    private static class Glyphs
    {
        public const string Add = "", More = "", Settings = "", Stop = "", Restart = "",
            Chevron = "", Play = "", Globe = "", Warning = "", Power = "",
            Download = "", Clock = "";
        // In Segoe UI Emoji and Segoe UI Symbol, which GDI draws in one color, filled.
        public const string Bolt = "⚡", Hand = "✋";
    }
}
