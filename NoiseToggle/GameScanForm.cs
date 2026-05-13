namespace NoiseToggle;

internal sealed class GameScanForm : Form
{
    private readonly CheckedListBox _list = new();

    public IReadOnlyList<GameRule> SelectedRules { get; private set; } = [];

    public GameScanForm(IReadOnlyCollection<string> existingProcesses)
    {
        Text = "Scan installed games";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MaximizeBox = false;
        MinimizeBox = false;
        ClientSize = new Size(560, 430);
        BackColor = Color.FromArgb(238, 238, 238);

        var label = new Label
        {
            Text = "Detected game/app shortcuts",
            Location = new Point(16, 16),
            AutoSize = true,
            Font = new Font(Font.FontFamily, 9, FontStyle.Bold)
        };

        _list.Location = new Point(18, 44);
        _list.Size = new Size(520, 320);
        _list.CheckOnClick = true;

        var addButton = new Button
        {
            Text = "Add selected",
            Location = new Point(332, 382),
            Width = 98,
            DialogResult = DialogResult.OK
        };
        addButton.Click += (_, _) => AcceptSelection();

        var cancelButton = new Button
        {
            Text = "Cancel",
            Location = new Point(440, 382),
            Width = 98,
            DialogResult = DialogResult.Cancel
        };

        Controls.AddRange([label, _list, addButton, cancelButton]);
        AcceptButton = addButton;
        CancelButton = cancelButton;

        var existing = existingProcesses.ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var candidate in GameScanner.ScanInstalledShortcuts())
        {
            if (!existing.Contains(candidate.ProcessName))
            {
                _list.Items.Add(candidate);
            }
        }

        if (_list.Items.Count == 0)
        {
            _list.Items.Add("No new game shortcuts found");
            _list.Enabled = false;
            addButton.Enabled = false;
        }
    }

    private void AcceptSelection()
    {
        SelectedRules = _list.CheckedItems
            .OfType<GameCandidate>()
            .Select(c => new GameRule
            {
                ProcessName = c.ProcessName,
                BroadcastNoiseRemovalEnabled = false,
                KrispEnabled = true
            })
            .ToList();
    }
}
