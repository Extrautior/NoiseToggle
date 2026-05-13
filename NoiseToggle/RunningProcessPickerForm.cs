using System.Diagnostics;

namespace NoiseToggle;

internal sealed class RunningProcessPickerForm : Form
{
    private readonly ListBox _processList = new();

    public GameRule? SelectedRule { get; private set; }

    public RunningProcessPickerForm()
    {
        Text = "Add running app";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MaximizeBox = false;
        MinimizeBox = false;
        ClientSize = new Size(420, 420);

        var label = new Label
        {
            Text = "Select a running game or app",
            Location = new Point(14, 12),
            AutoSize = true
        };

        _processList.Location = new Point(16, 38);
        _processList.Size = new Size(388, 320);
        _processList.DoubleClick += (_, _) => AcceptSelection();

        var addButton = new Button
        {
            Text = "Add",
            Location = new Point(248, 374),
            DialogResult = DialogResult.OK
        };
        addButton.Click += (_, _) => AcceptSelection();

        var cancelButton = new Button
        {
            Text = "Cancel",
            Location = new Point(329, 374),
            DialogResult = DialogResult.Cancel
        };

        Controls.AddRange([label, _processList, addButton, cancelButton]);
        AcceptButton = addButton;
        CancelButton = cancelButton;
        Load += (_, _) => PopulateProcesses();
    }

    private void PopulateProcesses()
    {
        var processes = Process.GetProcesses()
            .Select(p =>
            {
                try
                {
                    return string.IsNullOrWhiteSpace(p.MainWindowTitle)
                        ? null
                        : new ProcessItem(AppSettings.NormalizeProcessName(p.ProcessName), p.MainWindowTitle);
                }
                catch
                {
                    return null;
                }
            })
            .OfType<ProcessItem>()
            .DistinctBy(p => p.ProcessName, StringComparer.OrdinalIgnoreCase)
            .OrderBy(p => p.ProcessName, StringComparer.OrdinalIgnoreCase)
            .ToList();

        _processList.Items.Clear();
        foreach (var process in processes)
        {
            _processList.Items.Add(process);
        }
    }

    private void AcceptSelection()
    {
        if (_processList.SelectedItem is not ProcessItem item)
        {
            DialogResult = DialogResult.None;
            return;
        }

        SelectedRule = new GameRule
        {
            ProcessName = item.ProcessName,
            BroadcastNoiseRemovalEnabled = false,
            KrispEnabled = true
        };
        DialogResult = DialogResult.OK;
        Close();
    }

    private sealed record ProcessItem(string ProcessName, string WindowTitle)
    {
        public override string ToString()
        {
            return $"{ProcessName} - {WindowTitle}";
        }
    }
}
