using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Reflection;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace ServerHat;

/// Window colors, following the Windows "app mode" light/dark setting.
internal sealed record Theme(Color Back, Color Text, Color Secondary, Color Line, Color Accent)
{
    public static readonly Theme Light = new(Color.FromArgb(249, 249, 249), Color.FromArgb(27, 27, 27), Color.FromArgb(96, 96, 96),
        Color.FromArgb(220, 220, 220), Color.FromArgb(0, 95, 184));
    public static readonly Theme Dark = new(Color.FromArgb(44, 44, 44), Color.White, Color.FromArgb(168, 168, 168),
        Color.FromArgb(68, 68, 68), Color.FromArgb(76, 194, 255));

    public static Theme Current
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is 0 ? Dark : Light;
        }
    }

    /// True when the taskbar is dark, so the tray icon is drawn in white.
    public static bool DarkTaskbar
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("SystemUsesLightTheme") is not 1;
        }
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    /// A dark title bar in dark mode (Windows 10 20H1 and later; ignored before).
    public static void TitleBar(Form form, Theme theme)
    {
        if (!form.IsHandleCreated) return;
        var dark = theme == Dark ? 1 : 0;
        DwmSetWindowAttribute(form.Handle, 20 /* DWMWA_USE_IMMERSIVE_DARK_MODE */, ref dark, sizeof(int));
    }
}

/// "About ServerHat", like the macOS About window: the app icon, name, version, and the
/// Support GuruHat link. One window is reused; showing it again brings it to the front.
internal sealed class AboutWindow : Form
{
    public const string DonationUrl = "https://www.paypal.com/donate/?hosted_button_id=FHGYWD46722JS";
    private const string SiteUrl = "https://guruhat.com";
    private const int ContentWidth = 390, ContentHeight = 430;
    private const TextFormatFlags Centered = TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix | TextFormatFlags.HorizontalCenter;

    private static AboutWindow? shared;

    private readonly Image? icon;
    private Theme theme = Theme.Light;
    private Font? name, body, strong, small, glyph;
    private float scale;
    private Rectangle support, site;
    private Rectangle? hovering;

    private AboutWindow()
    {
        // Embedded in ServerHat.csproj, so the icon stays sharp at 88 px.
        using (var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("ServerHat.png"))
            icon = stream is null ? null : Image.FromStream(stream);
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = true;
        StartPosition = FormStartPosition.CenterScreen;
        AutoScaleMode = AutoScaleMode.None;
        DoubleBuffered = true;
        KeyPreview = true;
        Text = "About ServerHat";
        Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
        Relayout();
    }

    public static void ShowShared()
    {
        if (shared is null || shared.IsDisposed)
        {
            shared = new AboutWindow();
            shared.Show();
        }
        if (shared.WindowState == FormWindowState.Minimized) shared.WindowState = FormWindowState.Normal;
        shared.Activate();
    }

    public static void Open(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException) { }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Relayout();
    }

    private void Relayout()
    {
        theme = Theme.Current;
        BackColor = theme.Back;
        Theme.TitleBar(this, theme);
        if (DeviceDpi / 96f != scale)
        {
            scale = DeviceDpi / 96f;
            foreach (var font in new[] { name, body, strong, small, glyph }) font?.Dispose();
            Font Make(string family, float px) => new(family, px * scale, FontStyle.Regular, GraphicsUnit.Pixel);
            name = new Font("Segoe UI", 22 * scale, FontStyle.Bold, GraphicsUnit.Pixel);
            body = Make("Segoe UI", 15);
            strong = Make("Segoe UI Semibold", 15);
            small = Make("Segoe UI", 12);
            glyph = Make("Segoe UI Symbol", 22);
        }
        ClientSize = new Size(S(ContentWidth), S(ContentHeight));
        Invalidate();
    }

    protected override void OnDpiChanged(DpiChangedEventArgs e)
    {
        base.OnDpiChanged(e);
        Relayout();
    }

    protected override void OnPaint(PaintEventArgs e) => Render(e.Graphics);

    private int S(float value) => (int)Math.Round(value * scale);

    private void Render(Graphics g)
    {
        g.Clear(theme.Back);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        var width = S(ContentWidth);
        var pad = S(28);
        var y = S(34);

        if (icon is not null)
        {
            var size = S(88);
            g.DrawImage(icon, new Rectangle((width - size) / 2, y, size, size));
            y += size + S(14);
        }
        y += Centre(g, "ServerHat", name!, theme.Text, y) + S(10);
        y += Centre(g, $"Version {ApiServer.Version}", body!, theme.Secondary, y) + S(14);
        y += Centre(g, "Your local web servers, one click away.", strong!, theme.Text, y) + S(8);
        y += Centre(g, "Free and open source (MIT). No account, no analytics.", small!, theme.Secondary, y) + S(20);

        using (var pen = new Pen(theme.Line, Math.Max(1, scale))) g.DrawLine(pen, pad, y, width - pad, y);
        y += S(20);

        // "♥ Support GuruHat" as a link in the accent blue, underlined while hovered.
        const string heart = "♥";
        const string label = "Support GuruHat";
        var heartSize = TextRenderer.MeasureText(heart, glyph, Size.Empty, TextFormatFlags.NoPadding);
        var labelSize = TextRenderer.MeasureText(label, body, Size.Empty, TextFormatFlags.NoPadding);
        var gap = S(10);
        var total = heartSize.Width + gap + labelSize.Width;
        var height = Math.Max(heartSize.Height, labelSize.Height);
        support = new Rectangle((width - total) / 2, y, total, height);
        TextRenderer.DrawText(g, heart, glyph, new Point(support.X, y + (height - heartSize.Height) / 2), theme.Accent, TextFormatFlags.NoPadding);
        var labelAt = new Point(support.X + heartSize.Width + gap, y + (height - labelSize.Height) / 2);
        TextRenderer.DrawText(g, label, body, labelAt, theme.Accent, TextFormatFlags.NoPadding);
        if (hovering == support) Underline(g, labelAt, labelSize);
        y = support.Bottom + S(10);
        y += Centre(g, "Donations keep GuruHat apps free. Thank you!", small!, theme.Secondary, y) + S(16);

        var siteSize = TextRenderer.MeasureText("guruhat.com", small, Size.Empty, TextFormatFlags.NoPadding);
        site = new Rectangle((width - siteSize.Width) / 2, y, siteSize.Width, siteSize.Height);
        TextRenderer.DrawText(g, "guruhat.com", small, site.Location, theme.Accent, TextFormatFlags.NoPadding);
        if (hovering == site) Underline(g, site.Location, siteSize);
    }

    private void Underline(Graphics g, Point at, Size size)
    {
        using var pen = new Pen(theme.Accent, Math.Max(1, scale));
        g.DrawLine(pen, at.X, at.Y + size.Height, at.X + size.Width, at.Y + size.Height);
    }

    /// Writes one centered line at y and returns its height.
    private int Centre(Graphics g, string text, Font font, Color color, int y)
    {
        var height = TextRenderer.MeasureText(text, font, Size.Empty, Centered).Height;
        TextRenderer.DrawText(g, text, font, new Rectangle(0, y, S(ContentWidth), height), color, Centered);
        return height;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        Rectangle? over = support.Contains(e.Location) ? support : site.Contains(e.Location) ? site : null;
        if (over == hovering) return;
        hovering = over;
        Cursor = over is null ? Cursors.Default : Cursors.Hand;
        Invalidate();
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (e.Button != MouseButtons.Left) return;
        if (support.Contains(e.Location)) Open(DonationUrl);
        else if (site.Contains(e.Location)) Open(SiteUrl);
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (keyData == Keys.Escape) { Close(); return true; }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            icon?.Dispose();
            foreach (var font in new[] { name, body, strong, small, glyph }) font?.Dispose();
        }
        base.Dispose(disposing);
    }
}
