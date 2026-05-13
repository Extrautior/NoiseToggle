namespace NoiseToggle;

internal sealed class SettingsForm : Form
{
    private readonly AppSettings _settings;
    private readonly TextBox _hotkeyBox = new();
    private readonly CheckBox _startupBox = new();
    private readonly CheckBox _autoSwitchBox = new();
    private readonly ListBox _ruleList = new();
    private readonly TextBox _processBox = new();
    private readonly ComboBox _actionBox = new();
    private readonly Label _errorLabel = new();
    private readonly List<GameRule> _rules = [];
    private HotkeyDefinition _capturedHotkey;

    public SettingsForm(AppSettings settings)
    {
        _settings = settings;
        _capturedHotkey = HotkeyDefinition.Parse(settings.Hotkey);
        _rules = settings.GameRules.ToList();

        Text = "NoiseToggle Control Panel";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterScreen;
        MaximizeBox = false;
        MinimizeBox = false;
        ClientSize = new Size(760, 560);
        BackColor = Color.FromArgb(236, 236, 236);
        Font = new Font("Segoe UI", 9);

        var header = new Panel
        {
            BackColor = Color.FromArgb(31, 31, 31),
            Location = new Point(0, 0),
            Size = new Size(760, 58)
        };
        var headerText = new Label
        {
            Text = "NoiseToggle",
            ForeColor = Color.White,
            Font = new Font("Segoe UI", 16, FontStyle.Bold),
            Location = new Point(18, 14),
            AutoSize = true
        };
        var accent = new Panel
        {
            BackColor = Color.FromArgb(118, 185, 0),
            Location = new Point(0, 56),
            Size = new Size(760, 3)
        };
        header.Controls.Add(headerText);

        var hotkeyGroup = CreateGroup("Hotkey", 18, 78, 724, 112);
        _hotkeyBox.Location = new Point(18, 30);
        _hotkeyBox.Width = 280;
        _hotkeyBox.ReadOnly = true;
        _hotkeyBox.Text = _capturedHotkey.DisplayText;
        _hotkeyBox.KeyDown += HotkeyBoxOnKeyDown;

        var hint = new Label
        {
            Text = "Click the box, then press any key or key combination.",
            Location = new Point(316, 34),
            Width = 370,
            AutoSize = false
        };

        _startupBox.Text = "Start with Windows";
        _startupBox.Location = new Point(18, 68);
        _startupBox.Width = 250;
        _startupBox.Checked = settings.StartWithWindows;
        hotkeyGroup.Controls.AddRange([_hotkeyBox, hint, _startupBox]);

        var gamesGroup = CreateGroup("Game/App Detection", 18, 204, 724, 285);
        _autoSwitchBox.Text = "Enable automatic switching for configured games/apps";
        _autoSwitchBox.Location = new Point(18, 30);
        _autoSwitchBox.Width = 460;
        _autoSwitchBox.Checked = settings.AutoSwitchForGames;

        _ruleList.Location = new Point(18, 62);
        _ruleList.Size = new Size(430, 150);
        _ruleList.SelectedIndexChanged += RuleListOnSelectedIndexChanged;

        var processLabel = new Label
        {
            Text = "Process",
            Location = new Point(18, 224),
            AutoSize = true
        };
        _processBox.Location = new Point(78, 220);
        _processBox.Width = 150;
        _processBox.PlaceholderText = "cs2.exe";

        var actionLabel = new Label
        {
            Text = "Action",
            Location = new Point(244, 224),
            AutoSize = true
        };
        _actionBox.Location = new Point(294, 220);
        _actionBox.Width = 210;
        _actionBox.DropDownStyle = ComboBoxStyle.DropDownList;
        _actionBox.Items.AddRange([
            "Broadcast off, Krisp on",
            "Broadcast on, Krisp off",
            "Broadcast off, Krisp off",
            "Broadcast on, Krisp on"
        ]);
        _actionBox.SelectedIndex = 0;

        var addButton = CreateButton("Add/Update", 522, 219, 90);
        addButton.Click += AddOrUpdateButtonOnClick;

        var removeButton = CreateButton("Remove", 462, 62, 92);
        removeButton.Click += RemoveButtonOnClick;

        var runningButton = CreateButton("Add running...", 462, 100, 112);
        runningButton.Click += AddRunningButtonOnClick;

        var scanButton = CreateButton("Scan installed...", 462, 138, 112);
        scanButton.Click += ScanButtonOnClick;

        gamesGroup.Controls.AddRange([
            _autoSwitchBox, _ruleList, processLabel, _processBox, actionLabel, _actionBox,
            addButton, removeButton, runningButton, scanButton
        ]);

        _errorLabel.Location = new Point(22, 505);
        _errorLabel.Width = 500;
        _errorLabel.ForeColor = Color.Firebrick;

        var saveButton = CreateButton("Apply", 562, 508, 80);
        saveButton.DialogResult = DialogResult.OK;
        saveButton.Click += SaveButtonOnClick;

        var cancelButton = CreateButton("Cancel", 654, 508, 80);
        cancelButton.DialogResult = DialogResult.Cancel;

        Controls.AddRange([header, accent, hotkeyGroup, gamesGroup, _errorLabel, saveButton, cancelButton]);
        AcceptButton = saveButton;
        CancelButton = cancelButton;
        RefreshRuleList();
    }

    private static GroupBox CreateGroup(string title, int x, int y, int width, int height)
    {
        return new GroupBox
        {
            Text = title,
            Location = new Point(x, y),
            Size = new Size(width, height),
            BackColor = Color.FromArgb(245, 245, 245),
            ForeColor = Color.FromArgb(32, 32, 32)
        };
    }

    private static Button CreateButton(string text, int x, int y, int width)
    {
        return new Button
        {
            Text = text,
            Location = new Point(x, y),
            Size = new Size(width, 28),
            BackColor = Color.FromArgb(225, 225, 225),
            FlatStyle = FlatStyle.System
        };
    }

    private void HotkeyBoxOnKeyDown(object? sender, KeyEventArgs e)
    {
        e.SuppressKeyPress = true;
        try
        {
            _capturedHotkey = HotkeyDefinition.FromKeyEvent(e);
            _hotkeyBox.Text = _capturedHotkey.DisplayText;
            _errorLabel.Text = "";
        }
        catch (Exception ex)
        {
            _errorLabel.Text = ex.Message;
        }
    }

    private void SaveButtonOnClick(object? sender, EventArgs e)
    {
        _settings.Hotkey = _capturedHotkey.DisplayText;
        _settings.StartWithWindows = _startupBox.Checked;
        _settings.AutoSwitchForGames = _autoSwitchBox.Checked;
        _settings.GameRules = _rules.ToList();
        _settings.Save();
    }

    private void AddOrUpdateButtonOnClick(object? sender, EventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_processBox.Text))
        {
            return;
        }

        AddOrUpdateRule(CreateRule(_processBox.Text, _actionBox.SelectedIndex));
    }

    private void AddRunningButtonOnClick(object? sender, EventArgs e)
    {
        using var picker = new RunningProcessPickerForm();
        if (picker.ShowDialog(this) == DialogResult.OK && picker.SelectedRule is not null)
        {
            AddOrUpdateRule(picker.SelectedRule);
        }
    }

    private void ScanButtonOnClick(object? sender, EventArgs e)
    {
        using var scan = new GameScanForm(_rules.Select(r => r.ProcessName).ToList());
        if (scan.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }

        foreach (var rule in scan.SelectedRules)
        {
            AddOrUpdateRule(rule);
        }
    }

    private void RemoveButtonOnClick(object? sender, EventArgs e)
    {
        if (_ruleList.SelectedItem is GameRule selected)
        {
            _rules.RemoveAll(r => r.ProcessName.Equals(selected.ProcessName, StringComparison.OrdinalIgnoreCase));
            RefreshRuleList();
        }
    }

    private void RuleListOnSelectedIndexChanged(object? sender, EventArgs e)
    {
        if (_ruleList.SelectedItem is not GameRule selected)
        {
            return;
        }

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
        _rules.RemoveAll(r => r.ProcessName.Equals(rule.ProcessName, StringComparison.OrdinalIgnoreCase));
        _rules.Add(rule);
        _rules.Sort((a, b) => string.Compare(a.ProcessName, b.ProcessName, StringComparison.OrdinalIgnoreCase));
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
            {
                _ruleList.SelectedItem = rule;
            }
        }
    }
}
