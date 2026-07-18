using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Runtime.InteropServices;

namespace NoiseToggle;

internal sealed class WaveLinkHudForm : Form
{
    private const int AnimationDurationMilliseconds = 170;
    private readonly WaveLinkSettings _settings;
    private readonly System.Windows.Forms.Timer _animationTimer = new() { Interval = 15 };
    private readonly System.Windows.Forms.Timer _hideTimer = new();
    private readonly Stopwatch _animationClock = new();
    private WaveLinkHudModel _model = new(
        "Listening…", 0m, 0f, false, 0, 0, 0, "Personal Mix", "Connecting to Wave Link…");
    private Point _restingLocation;
    private double _animationStartOpacity;
    private int _animationStartY;
    private bool _appearing;

    public WaveLinkHudForm(WaveLinkSettings settings)
    {
        _settings = settings;
        Text = "NoiseToggle Wave Link";
        ClientSize = new Size(468, 178);
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        TopMost = true;
        ShowInTaskbar = false;
        BackColor = Color.FromArgb(30, 30, 32);
        DoubleBuffered = true;
        Opacity = 0;
        AutoScaleMode = AutoScaleMode.None;

        _hideTimer.Interval = _settings.HudAutoHideMilliseconds;
        _hideTimer.Tick += (_, _) =>
        {
            _hideTimer.Stop();
            BeginTransition(false);
        };
        _animationTimer.Tick += (_, _) => AnimateFrame();
        Shown += (_, _) =>
        {
            PositionAtBottomRight();
            Location = new Point(_restingLocation.X, _restingLocation.Y + 14);
        };
    }

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            const int transparent = 0x00000020;
            const int toolWindow = 0x00000080;
            const int noActivate = 0x08000000;
            const int dropShadow = 0x00020000;
            var parameters = base.CreateParams;
            parameters.ExStyle |= transparent | toolWindow | noActivate;
            parameters.ClassStyle |= dropShadow;
            return parameters;
        }
    }

    protected override void OnHandleCreated(EventArgs eventArgs)
    {
        base.OnHandleCreated(eventArgs);
        const int cornerPreference = 33;
        var rounded = 2;
        _ = DwmSetWindowAttribute(Handle, cornerPreference, ref rounded, sizeof(int));
        PositionAtBottomRight();
        UpdateRoundedRegion();
    }

    protected override void OnSizeChanged(EventArgs eventArgs)
    {
        base.OnSizeChanged(eventArgs);
        UpdateRoundedRegion();
    }

    public void ApplySettings()
    {
        if (IsDisposed)
            return;
        if (InvokeRequired)
        {
            BeginInvoke(ApplySettings);
            return;
        }
        _hideTimer.Interval = _settings.HudAutoHideMilliseconds;
        PositionAtBottomRight();
        if (Opacity > 0)
            Opacity = _settings.HudOpacity;
        Invalidate();
    }

    public void UpdateHud(WaveLinkHudModel model)
    {
        if (IsDisposed)
            return;
        if (InvokeRequired)
        {
            BeginInvoke(() => UpdateHud(model));
            return;
        }
        _model = model;
        Invalidate();
    }

    public void ShowHud(int? holdMilliseconds = null)
    {
        if (!_settings.ShowHud || IsDisposed)
            return;
        if (InvokeRequired)
        {
            BeginInvoke(() => ShowHud(holdMilliseconds));
            return;
        }

        PositionAtBottomRight();
        if (!Visible)
        {
            Opacity = 0;
            Show();
        }
        if (Opacity <= 0.001d)
            Location = new Point(_restingLocation.X, _restingLocation.Y + 14);
        ReassertTopmost();
        BeginTransition(true);
        _hideTimer.Stop();
        _hideTimer.Interval = holdMilliseconds ?? _settings.HudAutoHideMilliseconds;
        _hideTimer.Start();
    }

    private void BeginTransition(bool appearing)
    {
        _appearing = appearing;
        _animationStartOpacity = Opacity;
        _animationStartY = Location.Y;
        _animationClock.Restart();
        _animationTimer.Start();
    }

    private void AnimateFrame()
    {
        var progress = Math.Clamp(_animationClock.Elapsed.TotalMilliseconds / AnimationDurationMilliseconds, 0d, 1d);
        var eased = 1d - Math.Pow(1d - progress, 3d);
        if (_appearing)
        {
            Opacity = Lerp(_animationStartOpacity, _settings.HudOpacity, eased);
            Location = new Point(_restingLocation.X, (int)Math.Round(Lerp(_animationStartY, _restingLocation.Y, eased)));
        }
        else
        {
            Opacity = Lerp(_animationStartOpacity, 0d, eased);
            Location = new Point(_restingLocation.X, (int)Math.Round(Lerp(_animationStartY, _restingLocation.Y + 10, eased)));
        }

        if (progress < 1d)
            return;
        _animationTimer.Stop();
        _animationClock.Stop();
        if (!_appearing)
            Opacity = 0;
    }

    protected override void OnPaintBackground(PaintEventArgs eventArgs) =>
        eventArgs.Graphics.Clear(Color.FromArgb(30, 30, 32));

    protected override void OnPaint(PaintEventArgs eventArgs)
    {
        base.OnPaint(eventArgs);
        var graphics = eventArgs.Graphics;
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        graphics.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
        graphics.CompositingQuality = CompositingQuality.HighQuality;

        var bounds = new RectangleF(0.5f, 0.5f, ClientSize.Width - 1f, ClientSize.Height - 1f);
        using var surface = RoundedRectangle(bounds, 15f);
        using var background = new SolidBrush(Color.FromArgb(30, 30, 32));
        using var border = new Pen(Color.FromArgb(68, 68, 72), 1f);
        graphics.FillPath(background, surface);
        graphics.DrawPath(border, surface);

        var accentColor = Color.FromArgb(96, 205, 255);
        var primaryColor = Color.FromArgb(250, 250, 250);
        var secondaryColor = Color.FromArgb(183, 183, 189);
        using var accent = new SolidBrush(accentColor);
        using var primary = new SolidBrush(primaryColor);
        using var secondary = new SolidBrush(secondaryColor);
        using var quiet = new SolidBrush(Color.FromArgb(64, 64, 68));
        using var titleFont = new Font("Segoe UI Variable Display", 18f, FontStyle.Bold, GraphicsUnit.Point);
        using var volumeFont = new Font("Segoe UI Variable Display", 20f, FontStyle.Bold, GraphicsUnit.Point);
        using var labelFont = new Font("Segoe UI Variable Text", 8.25f, FontStyle.Bold, GraphicsUnit.Point);
        using var detailFont = new Font("Segoe UI Variable Text", 8.25f, FontStyle.Regular, GraphicsUnit.Point);

        graphics.FillHudRoundedRectangle(accent, new RectangleF(0, 39, 3, 88), 1.5f);
        graphics.DrawString(_model.Adjusting ? "ADJUSTING" : "SELECT CHANNEL", labelFont, accent, new PointF(31, 20));
        var active = _model.ActiveCount == 1 ? "1 ACTIVE" : $"{_model.ActiveCount} ACTIVE";
        DrawRightAligned(graphics, active, labelFont, _model.ActiveCount > 0 ? accent : secondary, ClientSize.Width - 31, 20);

        var channelText = string.IsNullOrWhiteSpace(_model.Status) ? _model.Channel : _model.Status;
        using var channelFormat = new StringFormat { Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap };
        graphics.DrawString(channelText, titleFont, primary, new RectangleF(30, 47, 285, 36), channelFormat);
        var percentage = Math.Clamp((int)Math.Round(_model.Volume * 100m), 0, 100);
        DrawRightAligned(graphics, $"{percentage}%", volumeFont, primary, ClientSize.Width - 31, 44);

        var volumeTrack = new RectangleF(31, 94, ClientSize.Width - 62, 7);
        graphics.FillHudRoundedRectangle(quiet, volumeTrack, 3.5f);
        var volumeWidth = volumeTrack.Width * percentage / 100f;
        if (volumeWidth > 0)
            graphics.FillHudRoundedRectangle(accent, new RectangleF(volumeTrack.X, volumeTrack.Y, Math.Max(7, volumeWidth), 7), 3.5f);

        graphics.DrawString("SIGNAL", labelFont, secondary, new PointF(31, 113));
        var meter = new RectangleF(85, 119, ClientSize.Width - 116, 4);
        graphics.FillHudRoundedRectangle(quiet, meter, 2f);
        var peakWidth = meter.Width * Math.Clamp(_model.Peak, 0f, 1f);
        if (peakWidth > 0)
            graphics.FillHudRoundedRectangle(accent, new RectangleF(meter.X, meter.Y, Math.Max(4, peakWidth), 4), 2f);

        var position = _model.ChannelCount > 0 ? $"{_model.SelectedIndex + 1}/{_model.ChannelCount}" : "—";
        graphics.DrawString($"{_model.Mix}  ·  {position}", detailFont, secondary, new PointF(31, 145));
        DrawRightAligned(
            graphics,
            _model.Adjusting ? "Turn to set level  ·  Press to browse" : "Turn to choose  ·  Press to adjust",
            detailFont, secondary, ClientSize.Width - 31, 145);
    }

    private static void DrawRightAligned(Graphics graphics, string text, Font font, Brush brush, float right, float top)
    {
        var width = graphics.MeasureString(text, font).Width;
        graphics.DrawString(text, font, brush, new PointF(right - width, top));
    }

    private void ReassertTopmost()
    {
        if (!IsHandleCreated)
            return;
        const uint noSize = 0x0001;
        const uint noMove = 0x0002;
        const uint noActivate = 0x0010;
        const uint showWindow = 0x0040;
        _ = SetWindowPos(Handle, new IntPtr(-1), 0, 0, 0, 0, noSize | noMove | noActivate | showWindow);
    }

    private void PositionAtBottomRight()
    {
        var desired = $"DISPLAY{_settings.HudMonitor}";
        var screen = Screen.AllScreens.FirstOrDefault(item => item.DeviceName.EndsWith(desired, StringComparison.OrdinalIgnoreCase))
                     ?? Screen.AllScreens.FirstOrDefault(item => !item.Primary)
                     ?? Screen.PrimaryScreen
                     ?? Screen.AllScreens[0];
        var area = screen.WorkingArea;
        _restingLocation = new Point(area.Right - Width - 28, area.Bottom - Height - 28);
        if (!Visible || !_animationTimer.Enabled)
            Location = _restingLocation;
    }

    private void UpdateRoundedRegion()
    {
        if (!IsHandleCreated || ClientSize.Width <= 0 || ClientSize.Height <= 0)
            return;
        using var path = RoundedRectangle(new RectangleF(0, 0, ClientSize.Width, ClientSize.Height), 15f);
        Region?.Dispose();
        Region = new Region(path);
    }

    private static GraphicsPath RoundedRectangle(RectangleF rectangle, float radius)
    {
        var path = new GraphicsPath();
        var diameter = radius * 2;
        path.AddArc(rectangle.X, rectangle.Y, diameter, diameter, 180, 90);
        path.AddArc(rectangle.Right - diameter, rectangle.Y, diameter, diameter, 270, 90);
        path.AddArc(rectangle.Right - diameter, rectangle.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(rectangle.X, rectangle.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        return path;
    }

    private static double Lerp(double start, double end, double amount) => start + (end - start) * amount;

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _animationTimer.Dispose();
            _hideTimer.Dispose();
            Region?.Dispose();
        }
        base.Dispose(disposing);
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int valueSize);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(
        IntPtr window, IntPtr insertAfter, int x, int y, int width, int height, uint flags);
}

internal static class WaveHudGraphicsExtensions
{
    public static void FillHudRoundedRectangle(
        this Graphics graphics, Brush brush, RectangleF rectangle, float radius)
    {
        using var path = new GraphicsPath();
        var diameter = radius * 2;
        path.AddArc(rectangle.X, rectangle.Y, diameter, diameter, 180, 90);
        path.AddArc(rectangle.Right - diameter, rectangle.Y, diameter, diameter, 270, 90);
        path.AddArc(rectangle.Right - diameter, rectangle.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(rectangle.X, rectangle.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        graphics.FillPath(brush, path);
    }
}
