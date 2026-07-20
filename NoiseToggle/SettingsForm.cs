using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.ComponentModel;

namespace NoiseToggle;

internal sealed class SettingsForm : Form
{
    private readonly AppSettings _settings;
    private readonly TextBox _hotkeyBox = StyledTextBox();
    private readonly ModernToggle _startupToggle = new();
    private readonly ModernToggle _autoSwitchToggle = new();
    private readonly DarkListBox _ruleList = new();
    private readonly TextBox _processBox = StyledTextBox();
    private readonly ComboBox _actionBox = StyledComboBox();
    private readonly ModernToggle _waveLinkEnabledToggle = new();
    private readonly ModernToggle _captureWheelToggle = new();
    private readonly ModernToggle _foregroundPressToggle = new();
    private readonly ModernToggle _activeChannelsToggle = new();
    private readonly ModernToggle _showHudToggle = new();
    private readonly ModernNumberField _volumeStep = new(1, 25, 1);
    private readonly ModernNumberField _hudMonitor = new(1, Math.Max(1, Screen.AllScreens.Length), 1);
    private readonly ModernNumberField _hudOpacity = new(65, 98, 1);
    private readonly ModernNumberField _hudHide = new(500, 10000, 100);
    private readonly ModernNumberField _activeHold = new(250, 30000, 250);
    private readonly Label _errorLabel = new();
    private readonly Panel _pageHost = new();
    private readonly List<GameRule> _rules;
    private HotkeyDefinition _capturedHotkey;

    public SettingsForm(AppSettings settings)
    {
        _settings = settings;
        _capturedHotkey = HotkeyDefinition.Parse(settings.Hotkey);
        _rules = settings.GameRules.ToList();

        Text = "NoiseToggle Settings";
        Icon = AppIcon.Load();
        FormBorderStyle = FormBorderStyle.FixedSingle;
        StartPosition = FormStartPosition.CenterScreen;
        MaximizeBox = false;
        MinimizeBox = false;
        ClientSize = new Size(980, 740);
        BackColor = UiColors.Background;
        ForeColor = UiColors.PrimaryText;
        Font = new Font("Segoe UI Variable Text", 9.5f, FontStyle.Regular, GraphicsUnit.Point);
        AutoScaleMode = AutoScaleMode.Dpi;

        var header = new Panel
        {
            Dock = DockStyle.Top,
            Height = 96,
            BackColor = UiColors.Header
        };
        header.Controls.Add(CreateLabel("NoiseToggle", 24, 17, 22f, true));
        header.Controls.Add(CreateLabel("Audio controls and app automation", 26, 61, 9.5f, false, UiColors.SecondaryText));

        var sidebar = new Panel
        {
            Location = new Point(0, 96),
            Size = new Size(210, 644),
            BackColor = UiColors.Sidebar
        };
        sidebar.Controls.Add(CreateLabel("SETTINGS", 22, 18, 8f, true, UiColors.SecondaryText));

        _pageHost.Location = new Point(210, 96);
        _pageHost.Size = new Size(770, 584);
        _pageHost.BackColor = UiColors.Background;

        var generalPage = BuildGeneralPage();
        var appsPage = BuildAppsPage();
        var waveLinkPage = BuildWaveLinkPage();
        _pageHost.Controls.AddRange([generalPage, appsPage, waveLinkPage]);

        var generalNav = new NavigationButton("General") { Location = new Point(12, 48) };
        var appsNav = new NavigationButton("App rules") { Location = new Point(12, 96) };
        var waveNav = new NavigationButton("Wave Link") { Location = new Point(12, 144) };
        sidebar.Controls.AddRange([generalNav, appsNav, waveNav]);

        void SelectPage(Control page, NavigationButton selected)
        {
            generalPage.Visible = page == generalPage;
            appsPage.Visible = page == appsPage;
            waveLinkPage.Visible = page == waveLinkPage;
            generalNav.Selected = selected == generalNav;
            appsNav.Selected = selected == appsNav;
            waveNav.Selected = selected == waveNav;
            page.BringToFront();
        }

        generalNav.Click += (_, _) => SelectPage(generalPage, generalNav);
        appsNav.Click += (_, _) => SelectPage(appsPage, appsNav);
        waveNav.Click += (_, _) => SelectPage(waveLinkPage, waveNav);

        var footer = new Panel
        {
            Location = new Point(210, 680),
            Size = new Size(770, 60),
            BackColor = UiColors.Background
        };
        _errorLabel.Location = new Point(28, 19);
        _errorLabel.Size = new Size(470, 24);
        _errorLabel.ForeColor = UiColors.Error;

        var applyButton = new ModernButton("Apply", primary: true)
        {
            Location = new Point(560, 11),
            Size = new Size(88, 38),
            DialogResult = DialogResult.OK
        };
        applyButton.Click += SaveButtonOnClick;
        var cancelButton = new ModernButton("Cancel")
        {
            Location = new Point(658, 11),
            Size = new Size(88, 38),
            DialogResult = DialogResult.Cancel
        };
        footer.Controls.AddRange([_errorLabel, applyButton, cancelButton]);

        Controls.AddRange([header, sidebar, _pageHost, footer]);
        AcceptButton = applyButton;
        CancelButton = cancelButton;
        RefreshRuleList();
        SelectPage(generalPage, generalNav);
    }

    protected override void OnHandleCreated(EventArgs eventArgs)
    {
        base.OnHandleCreated(eventArgs);
        var enabled = 1;
        _ = DwmSetWindowAttribute(Handle, 20, ref enabled, sizeof(int));
        var rounded = 2;
        _ = DwmSetWindowAttribute(Handle, 33, ref rounded, sizeof(int));
    }

    private Panel BuildGeneralPage()
    {
        var page = CreatePage("General", "Core shortcuts and startup behavior.");
        var hotkeyCard = new SettingsCard { Location = new Point(28, 104), Size = new Size(714, 132) };
        hotkeyCard.Controls.Add(CreateLabel("Global hotkey", 24, 22, 11f, true));
        hotkeyCard.Controls.Add(CreateLabel("Click the field, then press any key combination.", 24, 53, 9f, false, UiColors.SecondaryText));
        _hotkeyBox.Location = new Point(430, 39);
        _hotkeyBox.Size = new Size(250, 36);
        _hotkeyBox.ReadOnly = true;
        _hotkeyBox.Text = _capturedHotkey.DisplayText;
        _hotkeyBox.KeyDown += HotkeyBoxOnKeyDown;
        hotkeyCard.Controls.Add(_hotkeyBox);

        var startupCard = new SettingsCard { Location = new Point(28, 252), Size = new Size(714, 92) };
        startupCard.Controls.Add(CreateLabel("Start with Windows", 24, 20, 11f, true));
        startupCard.Controls.Add(CreateLabel("Keep NoiseToggle available in the system tray after sign-in.", 24, 49, 9f, false, UiColors.SecondaryText));
        _startupToggle.Location = new Point(642, 32);
        _startupToggle.Checked = _settings.StartWithWindows;
        startupCard.Controls.Add(_startupToggle);

        page.Controls.AddRange([hotkeyCard, startupCard]);
        return page;
    }

    private Panel BuildAppsPage()
    {
        var page = CreatePage("App rules", "Switch Broadcast and Krisp automatically for selected apps.");
        var card = new SettingsCard { Location = new Point(28, 104), Size = new Size(714, 430) };
        card.Controls.Add(CreateLabel("Automatic switching", 24, 19, 11f, true));
        card.Controls.Add(CreateLabel("Apply a saved audio mode while a matching process is running.", 24, 47, 9f, false, UiColors.SecondaryText));
        _autoSwitchToggle.Location = new Point(642, 27);
        _autoSwitchToggle.Checked = _settings.AutoSwitchForGames;
        card.Controls.Add(_autoSwitchToggle);

        _ruleList.Location = new Point(24, 82);
        _ruleList.Size = new Size(472, 222);
        _ruleList.SelectedIndexChanged += RuleListOnSelectedIndexChanged;
        card.Controls.Add(_ruleList);

        var remove = new ModernButton("Remove") { Location = new Point(516, 82), Size = new Size(166, 36) };
        remove.Click += RemoveButtonOnClick;
        var running = new ModernButton("Add running app") { Location = new Point(516, 128), Size = new Size(166, 36) };
        running.Click += AddRunningButtonOnClick;
        var scan = new ModernButton("Scan installed apps") { Location = new Point(516, 174), Size = new Size(166, 36) };
        scan.Click += ScanButtonOnClick;
        card.Controls.AddRange([remove, running, scan]);

        var divider = new Panel { Location = new Point(24, 322), Size = new Size(658, 1), BackColor = UiColors.Border };
        card.Controls.Add(divider);
        card.Controls.Add(CreateLabel("Process", 24, 342, 8.5f, true, UiColors.SecondaryText));
        _processBox.Location = new Point(24, 365);
        _processBox.Size = new Size(180, 36);
        _processBox.PlaceholderText = "cs2.exe";
        card.Controls.Add(_processBox);

        card.Controls.Add(CreateLabel("Action", 220, 342, 8.5f, true, UiColors.SecondaryText));
        _actionBox.Location = new Point(220, 365);
        _actionBox.Size = new Size(280, 36);
        _actionBox.Items.AddRange([
            "Broadcast off, Krisp on",
            "Broadcast on, Krisp off",
            "Broadcast off, Krisp off",
            "Broadcast on, Krisp on"
        ]);
        _actionBox.SelectedIndex = 0;
        card.Controls.Add(_actionBox);

        var update = new ModernButton("Add or update", primary: true)
        {
            Location = new Point(516, 365),
            Size = new Size(166, 36)
        };
        update.Click += AddOrUpdateButtonOnClick;
        card.Controls.Add(update);
        page.Controls.Add(card);
        return page;
    }

    private Panel BuildWaveLinkPage()
    {
        var page = CreatePage("Wave Link", "Control the focused or active channel from the keyboard wheel.");

        var wheelCard = new SettingsCard { Location = new Point(28, 104), Size = new Size(714, 145) };
        AddToggleRow(wheelCard, "Enable wheel controls", "Connect media-volume keys to Wave Link.", _waveLinkEnabledToggle, 17, _settings.WaveLink.Enabled);
        AddToggleRow(wheelCard, "Capture media wheel", "Prevent the same input from changing Windows master volume.", _captureWheelToggle, 72, _settings.WaveLink.CaptureWheel);
        page.Controls.Add(wheelCard);

        var behaviorCard = new SettingsCard { Location = new Point(28, 265), Size = new Size(714, 132) };
        AddToggleRow(behaviorCard, "Follow focused app", "Each new wheel use starts on the focused app. Press to browse channels.", _foregroundPressToggle, 14, _settings.WaveLink.SelectForegroundOnPress);
        AddToggleRow(behaviorCard, "Only active channels", "Keep the wheel list limited to channels producing audio.", _activeChannelsToggle, 68, _settings.WaveLink.OnlyActiveChannels);
        page.Controls.Add(behaviorCard);

        var tuningCard = new SettingsCard { Location = new Point(28, 413), Size = new Size(714, 146) };
        _volumeStep.Value = _settings.WaveLink.StepPercent;
        var displayCount = Math.Max(1, Screen.AllScreens.Length);
        _hudMonitor.Value = Math.Clamp(_settings.WaveLink.HudMonitor, 1, displayCount);
        _hudOpacity.Value = (int)Math.Round(_settings.WaveLink.HudOpacity * 100d);
        _hudHide.Value = _settings.WaveLink.HudAutoHideMilliseconds;
        _activeHold.Value = _settings.WaveLink.ActiveHoldMilliseconds;
        _showHudToggle.Checked = _settings.WaveLink.ShowHud;

        AddNumberSetting(tuningCard, "Volume step", "%", _volumeStep, 20, 20);
        AddNumberSetting(tuningCard, $"Display (1–{displayCount})", "", _hudMonitor, 190, 20);
        AddNumberSetting(tuningCard, "Opacity", "%", _hudOpacity, 340, 20);
        AddNumberSetting(tuningCard, "Hide after", "ms", _hudHide, 510, 20);
        AddNumberSetting(tuningCard, "Keep active", "ms", _activeHold, 20, 77);
        tuningCard.Controls.Add(CreateLabel("Show HUD", 340, 84, 9.5f, true));
        _showHudToggle.Location = new Point(636, 78);
        tuningCard.Controls.Add(_showHudToggle);
        page.Controls.Add(tuningCard);
        return page;
    }

    private static Panel CreatePage(string title, string subtitle)
    {
        var page = new Panel { Dock = DockStyle.Fill, BackColor = UiColors.Background, Visible = false };
        page.Controls.Add(CreateLabel(title, 28, 18, 20f, true));
        page.Controls.Add(CreateLabel(subtitle, 30, 62, 9.5f, false, UiColors.SecondaryText));
        return page;
    }

    private static void AddToggleRow(
        Control parent, string title, string description, ModernToggle toggle, int y, bool value)
    {
        parent.Controls.Add(CreateLabel(title, 24, y, 10.5f, true));
        parent.Controls.Add(CreateLabel(description, 24, y + 26, 8.75f, false, UiColors.SecondaryText));
        toggle.Location = new Point(642, y + 10);
        toggle.Checked = value;
        parent.Controls.Add(toggle);
    }

    private static void AddNumberSetting(
        Control parent, string title, string suffix, ModernNumberField field, int x, int y)
    {
        parent.Controls.Add(CreateLabel(title, x, y, 8.5f, true, UiColors.SecondaryText));
        field.Location = new Point(x, y + 23);
        field.Size = new Size(118, 34);
        parent.Controls.Add(field);
        if (!string.IsNullOrWhiteSpace(suffix))
            parent.Controls.Add(CreateLabel(suffix, x + 124, y + 31, 8.5f, false, UiColors.SecondaryText));
    }

    private static Label CreateLabel(
        string text, int x, int y, float size, bool bold, Color? color = null)
    {
        return new Label
        {
            Text = text,
            Location = new Point(x, y),
            AutoSize = true,
            BackColor = Color.Transparent,
            ForeColor = color ?? UiColors.PrimaryText,
            Font = new Font("Segoe UI Variable Text", size, bold ? FontStyle.Bold : FontStyle.Regular, GraphicsUnit.Point)
        };
    }

    private static TextBox StyledTextBox() => new()
    {
        BackColor = UiColors.Input,
        ForeColor = UiColors.PrimaryText,
        BorderStyle = BorderStyle.FixedSingle,
        Font = new Font("Segoe UI Variable Text", 10f),
        Multiline = true,
        Padding = new Padding(8, 6, 8, 6)
    };

    private static ComboBox StyledComboBox() => new()
    {
        BackColor = UiColors.Input,
        ForeColor = UiColors.PrimaryText,
        FlatStyle = FlatStyle.Flat,
        DropDownStyle = ComboBoxStyle.DropDownList,
        Font = new Font("Segoe UI Variable Text", 9.5f)
    };

    private void HotkeyBoxOnKeyDown(object? sender, KeyEventArgs eventArgs)
    {
        eventArgs.SuppressKeyPress = true;
        try
        {
            _capturedHotkey = HotkeyDefinition.FromKeyEvent(eventArgs);
            _hotkeyBox.Text = _capturedHotkey.DisplayText;
            _errorLabel.Text = "";
        }
        catch (Exception ex)
        {
            _errorLabel.Text = ex.Message;
        }
    }

    private void SaveButtonOnClick(object? sender, EventArgs eventArgs)
    {
        _settings.Hotkey = _capturedHotkey.DisplayText;
        _settings.StartWithWindows = _startupToggle.Checked;
        _settings.AutoSwitchForGames = _autoSwitchToggle.Checked;
        _settings.GameRules = _rules.ToList();
        _settings.WaveLink.Enabled = _waveLinkEnabledToggle.Checked;
        _settings.WaveLink.CaptureWheel = _captureWheelToggle.Checked;
        _settings.WaveLink.SelectForegroundOnPress = _foregroundPressToggle.Checked;
        _settings.WaveLink.OnlyActiveChannels = _activeChannelsToggle.Checked;
        _settings.WaveLink.ShowHud = _showHudToggle.Checked;
        _settings.WaveLink.StepPercent = _volumeStep.Value;
        _settings.WaveLink.HudMonitor = Math.Clamp(_hudMonitor.Value, 1, Math.Max(1, Screen.AllScreens.Length));
        _settings.WaveLink.HudOpacity = _hudOpacity.Value / 100d;
        _settings.WaveLink.HudAutoHideMilliseconds = _hudHide.Value;
        _settings.WaveLink.ActiveHoldMilliseconds = _activeHold.Value;
        _settings.Save();
    }

    private void AddOrUpdateButtonOnClick(object? sender, EventArgs eventArgs)
    {
        if (!string.IsNullOrWhiteSpace(_processBox.Text))
            AddOrUpdateRule(CreateRule(_processBox.Text, _actionBox.SelectedIndex));
    }

    private void AddRunningButtonOnClick(object? sender, EventArgs eventArgs)
    {
        using var picker = new RunningProcessPickerForm();
        if (picker.ShowDialog(this) == DialogResult.OK && picker.SelectedRule is not null)
            AddOrUpdateRule(picker.SelectedRule);
    }

    private void ScanButtonOnClick(object? sender, EventArgs eventArgs)
    {
        using var scan = new GameScanForm(_rules.Select(rule => rule.ProcessName).ToList());
        if (scan.ShowDialog(this) != DialogResult.OK)
            return;
        foreach (var rule in scan.SelectedRules)
            AddOrUpdateRule(rule);
    }

    private void RemoveButtonOnClick(object? sender, EventArgs eventArgs)
    {
        if (_ruleList.SelectedItem is not GameRule selected)
            return;
        _rules.RemoveAll(rule => rule.ProcessName.Equals(selected.ProcessName, StringComparison.OrdinalIgnoreCase));
        RefreshRuleList();
    }

    private void RuleListOnSelectedIndexChanged(object? sender, EventArgs eventArgs)
    {
        if (_ruleList.SelectedItem is not GameRule selected)
            return;
        _processBox.Text = selected.ProcessName;
        _actionBox.SelectedIndex = selected.ActionText switch
        {
            "Broadcast on, Krisp off" => 1,
            "Broadcast off, Krisp off" => 2,
            "Broadcast on, Krisp on" => 3,
            _ => 0
        };
    }

    private void AddOrUpdateRule(GameRule rule)
    {
        _rules.RemoveAll(existing => existing.ProcessName.Equals(rule.ProcessName, StringComparison.OrdinalIgnoreCase));
        _rules.Add(rule);
        _rules.Sort((left, right) => string.Compare(left.ProcessName, right.ProcessName, StringComparison.OrdinalIgnoreCase));
        RefreshRuleList();
        _processBox.Text = rule.ProcessName;
    }

    private static GameRule CreateRule(string processName, int actionIndex)
    {
        var normalized = AppSettings.NormalizeProcessName(processName);
        return actionIndex switch
        {
            1 => new GameRule { ProcessName = normalized, BroadcastNoiseRemovalEnabled = true, KrispEnabled = false },
            2 => new GameRule { ProcessName = normalized, BroadcastNoiseRemovalEnabled = false, KrispEnabled = false },
            3 => new GameRule { ProcessName = normalized, BroadcastNoiseRemovalEnabled = true, KrispEnabled = true },
            _ => new GameRule { ProcessName = normalized, BroadcastNoiseRemovalEnabled = false, KrispEnabled = true }
        };
    }

    private void RefreshRuleList()
    {
        var selected = (_ruleList.SelectedItem as GameRule)?.ProcessName;
        _ruleList.Items.Clear();
        foreach (var rule in _rules)
        {
            _ruleList.Items.Add(rule);
            if (rule.ProcessName.Equals(selected, StringComparison.OrdinalIgnoreCase))
                _ruleList.SelectedItem = rule;
        }
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int valueSize);
}

internal static class UiColors
{
    public static readonly Color Background = Color.FromArgb(32, 32, 32);
    public static readonly Color Header = Color.FromArgb(28, 28, 28);
    public static readonly Color Sidebar = Color.FromArgb(29, 29, 29);
    public static readonly Color Card = Color.FromArgb(43, 43, 43);
    public static readonly Color Input = Color.FromArgb(51, 51, 51);
    public static readonly Color Border = Color.FromArgb(63, 63, 63);
    public static readonly Color Hover = Color.FromArgb(56, 56, 56);
    public static readonly Color Accent = Color.FromArgb(96, 205, 255);
    public static readonly Color PrimaryText = Color.FromArgb(247, 247, 247);
    public static readonly Color SecondaryText = Color.FromArgb(183, 183, 189);
    public static readonly Color Error = Color.FromArgb(255, 153, 164);
}

internal sealed class SettingsCard : Panel
{
    public SettingsCard()
    {
        BackColor = UiColors.Card;
        DoubleBuffered = true;
    }

    protected override void OnPaint(PaintEventArgs eventArgs)
    {
        eventArgs.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        var rectangle = new RectangleF(0.5f, 0.5f, Width - 1f, Height - 1f);
        using var path = RoundedPath(rectangle, 12f);
        using var fill = new SolidBrush(UiColors.Card);
        using var border = new Pen(UiColors.Border);
        eventArgs.Graphics.FillPath(fill, path);
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

internal sealed class ModernToggle : Control
{
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 15 };
    private bool _checked;
    private float _position;

    public ModernToggle()
    {
        Size = new Size(46, 26);
        Cursor = Cursors.Hand;
        TabStop = true;
        DoubleBuffered = true;
        _timer.Tick += (_, _) => Animate();
    }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool Checked
    {
        get => _checked;
        set
        {
            _checked = value;
            _position = value ? 1f : 0f;
            Invalidate();
        }
    }

    protected override void OnClick(EventArgs eventArgs)
    {
        _checked = !_checked;
        _timer.Start();
        base.OnClick(eventArgs);
    }

    protected override void OnKeyDown(KeyEventArgs eventArgs)
    {
        if (eventArgs.KeyCode == Keys.Space)
        {
            OnClick(EventArgs.Empty);
            eventArgs.Handled = true;
        }
        base.OnKeyDown(eventArgs);
    }

    private void Animate()
    {
        var target = _checked ? 1f : 0f;
        _position += (target - _position) * 0.35f;
        if (Math.Abs(target - _position) < 0.02f)
        {
            _position = target;
            _timer.Stop();
        }
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs eventArgs)
    {
        eventArgs.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        var track = new RectangleF(1, 2, 44, 22);
        using var trackPath = Capsule(track);
        using var trackBrush = new SolidBrush(_checked ? UiColors.Accent : Color.FromArgb(82, 82, 86));
        eventArgs.Graphics.FillPath(trackBrush, trackPath);
        var knobX = 4f + _position * 20f;
        using var knob = new SolidBrush(_checked ? Color.FromArgb(20, 70, 88) : Color.White);
        eventArgs.Graphics.FillEllipse(knob, knobX, 5, 16, 16);
        if (Focused)
        {
            using var focus = new Pen(UiColors.Accent);
            eventArgs.Graphics.DrawPath(focus, trackPath);
        }
    }

    private static GraphicsPath Capsule(RectangleF rectangle)
    {
        var path = new GraphicsPath();
        path.AddArc(rectangle.X, rectangle.Y, rectangle.Height, rectangle.Height, 90, 180);
        path.AddArc(rectangle.Right - rectangle.Height, rectangle.Y, rectangle.Height, rectangle.Height, 270, 180);
        path.CloseFigure();
        return path;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            _timer.Dispose();
        base.Dispose(disposing);
    }
}

internal sealed class ModernButton : Button
{
    private readonly bool _primary;
    private bool _hovered;

    public ModernButton(string text, bool primary = false)
    {
        Text = text;
        _primary = primary;
        FlatStyle = FlatStyle.Flat;
        FlatAppearance.BorderSize = 0;
        ForeColor = primary ? Color.FromArgb(15, 43, 55) : UiColors.PrimaryText;
        Font = new Font("Segoe UI Variable Text", 9f, FontStyle.Regular);
        Cursor = Cursors.Hand;
        DoubleBuffered = true;
        UseVisualStyleBackColor = false;
    }

    protected override void OnMouseEnter(EventArgs eventArgs)
    {
        _hovered = true;
        Invalidate();
        base.OnMouseEnter(eventArgs);
    }

    protected override void OnMouseLeave(EventArgs eventArgs)
    {
        _hovered = false;
        Invalidate();
        base.OnMouseLeave(eventArgs);
    }

    protected override void OnPaint(PaintEventArgs eventArgs)
    {
        eventArgs.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        var color = _primary
            ? (_hovered ? Color.FromArgb(117, 214, 255) : UiColors.Accent)
            : (_hovered ? UiColors.Hover : UiColors.Input);
        var rectangle = new RectangleF(0.5f, 0.5f, Width - 1f, Height - 1f);
        using var path = RoundedPath(rectangle, 7f);
        using var fill = new SolidBrush(color);
        using var border = new Pen(_primary ? UiColors.Accent : UiColors.Border);
        eventArgs.Graphics.FillPath(fill, path);
        eventArgs.Graphics.DrawPath(border, path);
        TextRenderer.DrawText(
            eventArgs.Graphics, Text, Font, Rectangle.Round(rectangle), ForeColor,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
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

internal sealed class NavigationButton : Control
{
    private bool _selected;
    private bool _hovered;

    public NavigationButton(string text)
    {
        Text = text;
        Size = new Size(186, 40);
        Cursor = Cursors.Hand;
        Font = new Font("Segoe UI Variable Text", 10f);
        ForeColor = UiColors.PrimaryText;
        DoubleBuffered = true;
    }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool Selected
    {
        get => _selected;
        set { _selected = value; Invalidate(); }
    }

    protected override void OnMouseEnter(EventArgs eventArgs) { _hovered = true; Invalidate(); base.OnMouseEnter(eventArgs); }
    protected override void OnMouseLeave(EventArgs eventArgs) { _hovered = false; Invalidate(); base.OnMouseLeave(eventArgs); }
    protected override void OnMouseUp(MouseEventArgs eventArgs) { if (eventArgs.Button == MouseButtons.Left) OnClick(EventArgs.Empty); base.OnMouseUp(eventArgs); }

    protected override void OnPaint(PaintEventArgs eventArgs)
    {
        eventArgs.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        if (_selected || _hovered)
        {
            using var background = new SolidBrush(_selected ? Color.FromArgb(50, 50, 52) : Color.FromArgb(42, 42, 44));
            eventArgs.Graphics.FillRoundedRectangle(background, new RectangleF(0, 0, Width, Height), 7f);
        }
        if (_selected)
        {
            using var accent = new SolidBrush(UiColors.Accent);
            eventArgs.Graphics.FillRoundedRectangle(accent, new RectangleF(1, 9, 3, 22), 1.5f);
        }
        TextRenderer.DrawText(eventArgs.Graphics, Text, Font, new Rectangle(18, 0, Width - 24, Height), ForeColor,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter);
    }
}

internal sealed class ModernNumberField : UserControl
{
    private readonly int _minimum;
    private readonly int _maximum;
    private readonly int _increment;
    private readonly TextBox _text = new();
    private int _value;

    public ModernNumberField(int minimum, int maximum, int increment)
    {
        _minimum = minimum;
        _maximum = maximum;
        _increment = increment;
        BackColor = UiColors.Input;
        DoubleBuffered = true;

        var minus = new ModernButton("−") { Location = new Point(2, 2), Size = new Size(29, 30) };
        var plus = new ModernButton("+") { Anchor = AnchorStyles.Top | AnchorStyles.Right, Location = new Point(87, 2), Size = new Size(29, 30) };
        minus.Click += (_, _) => Value -= _increment;
        plus.Click += (_, _) => Value += _increment;

        _text.BorderStyle = BorderStyle.None;
        _text.BackColor = UiColors.Input;
        _text.ForeColor = UiColors.PrimaryText;
        _text.TextAlign = HorizontalAlignment.Center;
        _text.Font = new Font("Segoe UI Variable Text", 9.5f);
        _text.Location = new Point(34, 8);
        _text.Size = new Size(50, 20);
        _text.LostFocus += (_, _) => CommitText();
        _text.KeyDown += (_, eventArgs) =>
        {
            if (eventArgs.KeyCode == Keys.Enter)
            {
                CommitText();
                eventArgs.SuppressKeyPress = true;
            }
        };
        Controls.AddRange([minus, _text, plus]);
    }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public int Value
    {
        get => _value;
        set
        {
            _value = Math.Clamp(value, _minimum, _maximum);
            _text.Text = _value.ToString();
        }
    }

    private void CommitText()
    {
        Value = int.TryParse(_text.Text, out var parsed) ? parsed : _value;
    }

    protected override void OnResize(EventArgs eventArgs)
    {
        base.OnResize(eventArgs);
        if (Controls.Count < 3)
            return;
        Controls[2].Location = new Point(Width - 31, 2);
        _text.Location = new Point(34, 8);
        _text.Width = Math.Max(24, Width - 68);
        using var path = new GraphicsPath();
        path.AddArc(0, 0, 14, 14, 180, 90);
        path.AddArc(Width - 14, 0, 14, 14, 270, 90);
        path.AddArc(Width - 14, Height - 14, 14, 14, 0, 90);
        path.AddArc(0, Height - 14, 14, 14, 90, 90);
        path.CloseFigure();
        Region?.Dispose();
        Region = new Region(path);
    }
}

internal sealed class DarkListBox : ListBox
{
    public DarkListBox()
    {
        BackColor = UiColors.Input;
        ForeColor = UiColors.PrimaryText;
        BorderStyle = BorderStyle.FixedSingle;
        DrawMode = DrawMode.OwnerDrawFixed;
        ItemHeight = 28;
        IntegralHeight = false;
        Font = new Font("Segoe UI Variable Text", 9f);
    }

    protected override void OnDrawItem(DrawItemEventArgs eventArgs)
    {
        if (eventArgs.Index < 0)
            return;
        var selected = (eventArgs.State & DrawItemState.Selected) != 0;
        using var background = new SolidBrush(selected ? Color.FromArgb(43, 85, 103) : UiColors.Input);
        eventArgs.Graphics.FillRectangle(background, eventArgs.Bounds);
        var text = Items[eventArgs.Index]?.ToString() ?? "";
        TextRenderer.DrawText(eventArgs.Graphics, text, Font,
            new Rectangle(eventArgs.Bounds.X + 10, eventArgs.Bounds.Y, eventArgs.Bounds.Width - 14, eventArgs.Bounds.Height),
            UiColors.PrimaryText, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
    }
}

internal static class ModernGraphicsExtensions
{
    public static void FillRoundedRectangle(
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
