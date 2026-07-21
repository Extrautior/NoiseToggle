namespace NoiseToggle;

internal sealed class BridgeMaintenanceForm : Form
{
    private readonly BroadcastBridgeMaintenanceAction _action;
    private readonly Label _status = new();
    private readonly ProgressBar _progress = new();
    private bool _started;

    public BridgeMaintenanceForm(BroadcastBridgeMaintenanceAction action)
    {
        _action = action;
        Text = action == BroadcastBridgeMaintenanceAction.Install
            ? "Installing NVIDIA Broadcast bridge"
            : "Restoring NVIDIA Broadcast";
        ClientSize = new Size(520, 164);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ControlBox = false;
        ShowInTaskbar = true;
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = UiColors.Background;
        ForeColor = UiColors.PrimaryText;
        Icon = AppIcon.Load();

        var title = new Label
        {
            AutoSize = true,
            Location = new Point(26, 24),
            Font = new Font("Segoe UI Variable Display", 14f, FontStyle.Bold),
            ForeColor = UiColors.PrimaryText,
            Text = action == BroadcastBridgeMaintenanceAction.Install
                ? "Preparing the native bridge"
                : "Preparing the clean backup"
        };

        _status.AutoSize = false;
        _status.Location = new Point(28, 67);
        _status.Size = new Size(464, 36);
        _status.Font = new Font("Segoe UI Variable Text", 9.5f);
        _status.ForeColor = UiColors.SecondaryText;
        _status.Text = "Starting...";

        _progress.Location = new Point(28, 119);
        _progress.Size = new Size(464, 8);
        _progress.Style = ProgressBarStyle.Marquee;
        _progress.MarqueeAnimationSpeed = 24;

        Controls.AddRange([title, _status, _progress]);
        Shown += async (_, _) => await RunAsync();
    }

    private async Task RunAsync()
    {
        if (_started)
            return;
        _started = true;

        var progress = new Progress<string>(message => _status.Text = message);
        try
        {
            var result = _action == BroadcastBridgeMaintenanceAction.Install
                ? await BroadcastBridgeMaintenance.InstallAsync(progress)
                : await BroadcastBridgeMaintenance.RestoreAsync(progress);

            _progress.Style = ProgressBarStyle.Continuous;
            _progress.Value = 100;
            MessageBox.Show(
                $"{result.Message}{Environment.NewLine}{Environment.NewLine}Clean backup:{Environment.NewLine}{result.BackupPath}",
                "NoiseToggle",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            AppLog.Error($"NVIDIA Broadcast bridge {_action.ToString().ToLowerInvariant()} failed.", ex);
            MessageBox.Show(
                ex.Message,
                $"NVIDIA Broadcast bridge {_action.ToString().ToLowerInvariant()} failed",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }
        finally
        {
            Close();
        }
    }
}
