using System.Diagnostics;

namespace NoiseToggle;

internal sealed class TrayAppContext : ApplicationContext
{
    private readonly AppSettings _settings;
    private readonly DiscordBridgeClient _discord;
    private readonly BroadcastController _broadcast;
    private readonly GlobalHotkey _hotkey = new();
    private readonly NotifyIcon _trayIcon;
    private readonly ToolStripMenuItem _statusItem;
    private readonly ToolStripMenuItem _toggleItem;
    private readonly ToolStripMenuItem _startupItem;
    private readonly System.Windows.Forms.Timer _gameMonitorTimer = new();
    private string? _activeGameProcess;
    private AudioStateSnapshot? _preGameState;
    private bool _busy;

    public TrayAppContext()
    {
        _settings = AppSettings.Load();
        _settings.StartWithWindows = StartupManager.IsEnabled();
        if (_settings.StartWithWindows)
        {
            StartupManager.SetEnabled(true);
        }
        _settings.Save();
        _discord = new DiscordBridgeClient(_settings);
        _broadcast = new BroadcastController(_settings);
        TryInstallBridgeOnStartup();

        _statusItem = new ToolStripMenuItem { Enabled = false };
        _toggleItem = new ToolStripMenuItem("Toggle now", null, async (_, _) => await ToggleAsync());
        _startupItem = new ToolStripMenuItem("Start with Windows", null, (_, _) => ToggleStartup())
        {
            Checked = _settings.StartWithWindows
        };

        var menu = new ContextMenuStrip();
        menu.Items.Add(_statusItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(_toggleItem);
        menu.Items.Add(new ToolStripMenuItem("Settings...", null, (_, _) => ShowSettings()));
        menu.Items.Add(_startupItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("Install Discord/Vencord bridge", null, (_, _) => InstallDiscordBridge()));
        menu.Items.Add(new ToolStripMenuItem("Install NVIDIA Broadcast bridge", null, (_, _) => InstallBroadcastBridge()));
        menu.Items.Add(new ToolStripMenuItem("Restore NVIDIA Broadcast backup", null, (_, _) => RestoreBroadcastBridge()));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("Exit", null, (_, _) => ExitThread()));

        _trayIcon = new NotifyIcon
        {
            Icon = AppIcon.Load(),
            Text = "NoiseToggle",
            ContextMenuStrip = menu,
            Visible = true
        };
        _trayIcon.DoubleClick += (_, _) => ShowSettings();

        _hotkey.Pressed += async (_, _) => await ToggleAsync();
        _gameMonitorTimer.Interval = 5000;
        _gameMonitorTimer.Tick += async (_, _) => await CheckGameMonitorAsync();
        _gameMonitorTimer.Start();

        RegisterHotkey();
        UpdateStatus($"Mode: {_settings.LastMode} ({_settings.Hotkey})");
    }

    private async Task ToggleAsync()
    {
        var nextMode = _settings.LastMode == ToggleMode.Broadcast ? ToggleMode.Krisp : ToggleMode.Broadcast;
        await SwitchToModeAsync(nextMode, showBalloon: true);
    }

    private async Task SwitchToModeAsync(ToggleMode nextMode, bool showBalloon)
    {
        if (_busy)
        {
            return;
        }

        _busy = true;
        _toggleItem.Enabled = false;

        var failures = new List<string>();
        var successes = 0;

        try
        {
            UpdateStatus($"Switching to {nextMode}...");
            AppLog.Info($"Switching from {_settings.LastMode} to {nextMode}.");

            if (nextMode == ToggleMode.Krisp)
            {
                successes += await ApplyStatesAsync(broadcastNoiseRemoval: false, krisp: true, failures);
            }
            else
            {
                successes += await ApplyStatesAsync(broadcastNoiseRemoval: true, krisp: false, failures);
            }

            if (successes > 0)
            {
                _settings.LastMode = nextMode;
                _settings.Save();
                UpdateStatus($"Mode: {nextMode} ({_settings.Hotkey})");
            }

            if (failures.Count == 0)
            {
                if (showBalloon)
                {
                    ShowBalloon("NoiseToggle", $"Switched to {nextMode} mode.");
                }
            }
            else if (successes > 0)
            {
                UpdateStatus($"Partial: {nextMode} ({_settings.Hotkey})");
                AppLog.Info($"Partial toggle to {nextMode}: {string.Join("; ", failures)}");
                if (showBalloon)
                {
                    ShowBalloon("NoiseToggle partial toggle", string.Join(Environment.NewLine, failures), ToolTipIcon.Warning);
                }
            }
            else
            {
                throw new InvalidOperationException(string.Join(Environment.NewLine, failures));
            }
        }
        catch (Exception ex)
        {
            AppLog.Error("Toggle failed.", ex);
            UpdateStatus($"Error: {ex.Message}");
            ShowBalloon("NoiseToggle error", ex.Message, ToolTipIcon.Error);
        }
        finally
        {
            _toggleItem.Enabled = true;
            _busy = false;
        }
    }

    private async Task CheckGameMonitorAsync()
    {
        if (_busy)
        {
            return;
        }

        if (!_settings.AutoSwitchForGames || _settings.GameRules.Count == 0)
        {
            if (_preGameState is not null)
            {
                AppLog.Info("Auto game monitor disabled while a game rule was active; restoring captured pre-game state.");
                await RestorePreGameStateAsync();
            }

            _activeGameProcess = null;
            return;
        }

        var activeRule = FindActiveGameRule();
        var activeProcess = activeRule?.ProcessName;
        if (string.Equals(activeProcess, _activeGameProcess, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        _activeGameProcess = activeProcess;

        if (activeRule is null)
        {
            AppLog.Info("Auto game monitor detected game exit.");
            await RestorePreGameStateAsync();
            return;
        }

        if (_preGameState is null)
        {
            _preGameState = await CaptureCurrentAudioStateAsync();
            AppLog.Info($"Auto game monitor captured pre-game state: {_preGameState}.");
        }

        AppLog.Info($"Auto game monitor applying rule for {activeRule.ProcessName}: {activeRule.ActionText}.");
        await ApplyRuleAsync(activeRule);
    }

    private async Task<int> ApplyStatesAsync(bool broadcastNoiseRemoval, bool krisp, List<string> failures)
    {
        var successes = 0;
        if (krisp)
        {
            if (await TryRunStepAsync("NVIDIA Broadcast", token => _broadcast.SetNoiseRemovalAsync(broadcastNoiseRemoval, token), failures, TimeSpan.FromSeconds(30)))
            {
                successes++;
            }

            if (await TryRunStepAsync("Discord Krisp", token => _discord.SetKrispStateAsync(true, token), failures, TimeSpan.FromSeconds(5)))
            {
                successes++;
            }
        }
        else
        {
            if (await TryRunStepAsync("Discord Krisp", token => _discord.SetKrispStateAsync(false, token), failures, TimeSpan.FromSeconds(5)))
            {
                successes++;
            }

            if (await TryRunStepAsync("NVIDIA Broadcast", token => _broadcast.SetNoiseRemovalAsync(broadcastNoiseRemoval, token), failures, TimeSpan.FromSeconds(30)))
            {
                successes++;
            }
        }

        return successes;
    }

    private async Task ApplyRuleAsync(GameRule rule)
    {
        if (_busy)
        {
            return;
        }

        _busy = true;
        _toggleItem.Enabled = false;
        var failures = new List<string>();

        try
        {
            UpdateStatus($"Game: {rule.ProcessName}");
            var successes = await ApplyStatesAsync(rule.BroadcastNoiseRemovalEnabled, rule.KrispEnabled, failures);
            if (successes > 0)
            {
                UpdateStatus($"Game: {rule.ProcessName}");
                AppLog.Info($"Auto game monitor applied game rule for {rule.ProcessName}: {rule.ActionText}.");
            }

            if (failures.Count > 0)
            {
                AppLog.Info($"Auto game monitor partial failure: {string.Join("; ", failures)}");
                ShowBalloon("NoiseToggle game rule partial failure", string.Join(Environment.NewLine, failures), ToolTipIcon.Warning);
            }
        }
        catch (Exception ex)
        {
            AppLog.Error("Auto game monitor failed.", ex);
        }
        finally
        {
            _toggleItem.Enabled = true;
            _busy = false;
        }
    }

    private async Task<AudioStateSnapshot> CaptureCurrentAudioStateAsync()
    {
        bool? broadcast = null;
        bool? krisp = null;

        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(4));
            broadcast = await _broadcast.GetNoiseRemovalStateAsync(cts.Token);
        }
        catch (Exception ex)
        {
            AppLog.Error("Could not capture pre-game NVIDIA Broadcast state.", ex);
        }

        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(4));
            krisp = await _discord.GetKrispStateAsync(cts.Token);
        }
        catch (Exception ex)
        {
            AppLog.Error("Could not capture pre-game Discord Krisp state.", ex);
        }

        return new AudioStateSnapshot(broadcast, krisp);
    }

    private async Task RestorePreGameStateAsync()
    {
        if (_preGameState is null)
        {
            return;
        }

        if (_busy)
        {
            return;
        }

        var snapshot = _preGameState;
        _preGameState = null;
        _busy = true;
        _toggleItem.Enabled = false;
        var failures = new List<string>();
        var successes = 0;

        try
        {
            UpdateStatus("Restoring pre-game audio...");
            AppLog.Info($"Auto game monitor restoring pre-game state: {snapshot}.");

            if (snapshot.BroadcastNoiseRemovalEnabled is bool broadcast)
            {
                if (await TryRunStepAsync("NVIDIA Broadcast", token => _broadcast.SetNoiseRemovalAsync(broadcast, token), failures, TimeSpan.FromSeconds(30)))
                {
                    successes++;
                }
            }
            else
            {
                failures.Add("NVIDIA Broadcast: previous state was unknown");
            }

            if (snapshot.KrispEnabled is bool krisp)
            {
                if (await TryRunStepAsync("Discord Krisp", token => _discord.SetKrispStateAsync(krisp, token), failures, TimeSpan.FromSeconds(5)))
                {
                    successes++;
                }
            }
            else
            {
                failures.Add("Discord Krisp: previous state was unknown");
            }

            if (successes > 0)
            {
                AppLog.Info("Auto game monitor restored pre-game state.");
                UpdateStatus($"Mode: {_settings.LastMode} ({_settings.Hotkey})");
            }

            if (failures.Count > 0)
            {
                AppLog.Info($"Auto game monitor restore partial failure: {string.Join("; ", failures)}");
                ShowBalloon("NoiseToggle restore partial failure", string.Join(Environment.NewLine, failures), ToolTipIcon.Warning);
            }
        }
        catch (Exception ex)
        {
            AppLog.Error("Auto game monitor restore failed.", ex);
        }
        finally
        {
            _toggleItem.Enabled = true;
            _busy = false;
        }
    }

    private GameRule? FindActiveGameRule()
    {
        var configured = _settings.GameRules
            .ToDictionary(r => Path.GetFileNameWithoutExtension(r.ProcessName), StringComparer.OrdinalIgnoreCase);

        foreach (var process in Process.GetProcesses())
        {
            try
            {
                if (configured.TryGetValue(process.ProcessName, out var rule))
                {
                    return rule;
                }
            }
            catch
            {
                // Processes can exit while being inspected.
            }
        }

        return null;
    }

    private static async Task<bool> TryRunStepAsync(string name, Func<CancellationToken, Task> step, List<string> failures, TimeSpan timeout)
    {
        try
        {
            using var cts = new CancellationTokenSource(timeout);
            await step(cts.Token);
            AppLog.Info($"{name} step succeeded.");
            return true;
        }
        catch (Exception ex)
        {
            AppLog.Error($"{name} step failed.", ex);
            failures.Add($"{name}: {ex.Message}");
            return false;
        }
    }

    private void ShowSettings()
    {
        using var form = new SettingsForm(_settings);
        if (form.ShowDialog() != DialogResult.OK)
        {
            return;
        }

        try
        {
            RegisterHotkey();
            StartupManager.SetEnabled(_settings.StartWithWindows);
            _startupItem.Checked = _settings.StartWithWindows;
            _activeGameProcess = FindActiveGameRule()?.ProcessName;
            UpdateStatus($"Mode: {_settings.LastMode} ({_settings.Hotkey})");
            ShowBalloon("NoiseToggle", "Settings saved.");
        }
        catch (Exception ex)
        {
            ShowBalloon("NoiseToggle settings error", ex.Message, ToolTipIcon.Error);
        }
    }

    private void ToggleStartup()
    {
        _settings.StartWithWindows = !_settings.StartWithWindows;
        StartupManager.SetEnabled(_settings.StartWithWindows);
        _settings.Save();
        _startupItem.Checked = _settings.StartWithWindows;
    }

    private void InstallDiscordBridge()
    {
        try
        {
            var result = BridgeInstaller.Install(_settings);
            ShowBalloon("NoiseToggle", result);
        }
        catch (Exception ex)
        {
            ShowBalloon("Discord bridge install failed", ex.Message, ToolTipIcon.Error);
        }
    }

    private void InstallBroadcastBridge()
    {
        try
        {
            BroadcastBridgeInstaller.Install();
            ShowBalloon("NoiseToggle", "NVIDIA Broadcast bridge installer started. Accept the UAC prompt.");
        }
        catch (Exception ex)
        {
            ShowBalloon("Broadcast bridge install failed", ex.Message, ToolTipIcon.Error);
        }
    }

    private void RestoreBroadcastBridge()
    {
        try
        {
            BroadcastBridgeInstaller.Restore();
            ShowBalloon("NoiseToggle", "NVIDIA Broadcast restore started. Accept the UAC prompt.");
        }
        catch (Exception ex)
        {
            ShowBalloon("Broadcast restore failed", ex.Message, ToolTipIcon.Error);
        }
    }

    private void TryInstallBridgeOnStartup()
    {
        try
        {
            if (Directory.Exists(BridgeInstaller.PluginDirectory) || File.Exists(BridgeInstaller.VencordPatcherPath))
            {
                BridgeInstaller.Install(_settings);
            }
        }
        catch
        {
            // Manual install remains available from the tray menu if startup install fails.
        }
    }

    private void RegisterHotkey()
    {
        var hotkey = HotkeyDefinition.Parse(_settings.Hotkey);
        _hotkey.Register(hotkey);
        _settings.Hotkey = hotkey.DisplayText;
        _settings.Save();
    }

    private void UpdateStatus(string text)
    {
        _statusItem.Text = text.Length > 55 ? text[..55] + "..." : text;
        _trayIcon.Text = text.Length > 63 ? text[..63] : text;
    }

    private void ShowBalloon(string title, string message, ToolTipIcon icon = ToolTipIcon.Info)
    {
        _trayIcon.BalloonTipTitle = title;
        _trayIcon.BalloonTipText = message.Length > 250 ? message[..250] + "..." : message;
        _trayIcon.BalloonTipIcon = icon;
        _trayIcon.ShowBalloonTip(3000);
    }

    protected override void ExitThreadCore()
    {
        _hotkey.Dispose();
        _gameMonitorTimer.Stop();
        _gameMonitorTimer.Dispose();
        _trayIcon.Visible = false;
        _trayIcon.Dispose();
        base.ExitThreadCore();
    }

    private sealed record AudioStateSnapshot(bool? BroadcastNoiseRemovalEnabled, bool? KrispEnabled)
    {
        public override string ToString()
        {
            return $"Broadcast={(BroadcastNoiseRemovalEnabled?.ToString() ?? "unknown")}, Krisp={(KrispEnabled?.ToString() ?? "unknown")}";
        }
    }
}
