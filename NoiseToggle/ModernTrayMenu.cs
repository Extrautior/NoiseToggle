using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace NoiseToggle;

internal sealed class ModernContextMenuStrip : ContextMenuStrip
{
    public ModernContextMenuStrip()
    {
        Renderer = new ModernMenuRenderer();
        BackColor = UiColors.Card;
        ForeColor = UiColors.PrimaryText;
        Font = new Font("Segoe UI Variable Text", 9.5f);
        Padding = new Padding(6);
        ShowImageMargin = false;
        ShowCheckMargin = true;
        DropShadowEnabled = true;
        Opening += (_, _) => ApplyItemMetrics();
    }

    protected override void OnHandleCreated(EventArgs eventArgs)
    {
        base.OnHandleCreated(eventArgs);
        var dark = 1;
        _ = DwmSetWindowAttribute(Handle, 20, ref dark, sizeof(int));
        var rounded = 2;
        _ = DwmSetWindowAttribute(Handle, 33, ref rounded, sizeof(int));
    }

    private void ApplyItemMetrics()
    {
        foreach (ToolStripItem item in Items)
        {
            item.AutoSize = false;
            item.Size = item is ToolStripSeparator ? new Size(304, 9) : new Size(304, 34);
            item.Margin = Padding.Empty;
            item.Padding = new Padding(8, 0, 8, 0);
        }
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int valueSize);
}

internal sealed class ModernMenuRenderer : ToolStripProfessionalRenderer
{
    public ModernMenuRenderer() : base(new ModernMenuColorTable())
    {
        RoundedEdges = true;
    }

    protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs eventArgs)
    {
        var item = eventArgs.Item;
        var bounds = new RectangleF(3, 2, item.Width - 6, item.Height - 4);
        if (!item.Selected || !item.Enabled)
            return;
        eventArgs.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using var path = RoundedPath(bounds, 6f);
        using var fill = new SolidBrush(UiColors.Hover);
        eventArgs.Graphics.FillPath(fill, path);
    }

    protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs eventArgs)
    {
        eventArgs.TextColor = eventArgs.Item.Enabled ? UiColors.PrimaryText : UiColors.SecondaryText;
        eventArgs.TextFormat |= TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis;
        base.OnRenderItemText(eventArgs);
    }

    protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs eventArgs)
    {
        using var line = new Pen(UiColors.Border);
        var y = eventArgs.Item.Height / 2f;
        eventArgs.Graphics.DrawLine(line, 14, y, eventArgs.Item.Width - 14, y);
    }

    protected override void OnRenderItemCheck(ToolStripItemImageRenderEventArgs eventArgs)
    {
        eventArgs.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        var rectangle = new RectangleF(eventArgs.ImageRectangle.X + 2, eventArgs.ImageRectangle.Y + 2, 18, 18);
        using var path = RoundedPath(rectangle, 4f);
        using var background = new SolidBrush(UiColors.Accent);
        using var check = new Pen(Color.FromArgb(17, 55, 70), 2f)
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round
        };
        eventArgs.Graphics.FillPath(background, path);
        eventArgs.Graphics.DrawLines(check,
        [
            new PointF(rectangle.X + 4, rectangle.Y + 9),
            new PointF(rectangle.X + 8, rectangle.Y + 13),
            new PointF(rectangle.X + 15, rectangle.Y + 5)
        ]);
    }

    protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs eventArgs)
    {
        eventArgs.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        var rectangle = new RectangleF(0.5f, 0.5f, eventArgs.ToolStrip.Width - 1f, eventArgs.ToolStrip.Height - 1f);
        using var path = RoundedPath(rectangle, 9f);
        using var border = new Pen(UiColors.Border);
        eventArgs.Graphics.DrawPath(border, path);
    }

    private static GraphicsPath RoundedPath(RectangleF rectangle, float radius)
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
}

internal sealed class ModernMenuColorTable : ProfessionalColorTable
{
    public override Color ToolStripDropDownBackground => UiColors.Card;
    public override Color ImageMarginGradientBegin => UiColors.Card;
    public override Color ImageMarginGradientMiddle => UiColors.Card;
    public override Color ImageMarginGradientEnd => UiColors.Card;
    public override Color MenuBorder => UiColors.Border;
    public override Color MenuItemBorder => Color.Transparent;
    public override Color MenuItemSelected => UiColors.Hover;
    public override Color MenuItemSelectedGradientBegin => UiColors.Hover;
    public override Color MenuItemSelectedGradientEnd => UiColors.Hover;
    public override Color CheckBackground => UiColors.Accent;
    public override Color CheckSelectedBackground => UiColors.Accent;
    public override Color CheckPressedBackground => UiColors.Accent;
    public override Color SeparatorDark => UiColors.Border;
    public override Color SeparatorLight => UiColors.Border;
}
