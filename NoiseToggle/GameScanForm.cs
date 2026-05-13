namespace NoiseToggle;

internal sealed class GameScanForm : Form
{
    private readonly CheckedListBox _list = new();
    private readonly TextBox _searchBox = new();
    private readonly Label _countLabel = new();
    private readonly List<GameCandidate> _candidates;
    private readonly HashSet<string> _checkedProcesses = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<GameRule> SelectedRules { get; private set; } = [];

    public GameScanForm(IReadOnlyCollection<string> existingProcesses)
    {
        Text = "Scan installed games";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MaximizeBox = false;
        MinimizeBox = false;
        ClientSize = new Size(560, 462);
        BackColor = Color.FromArgb(238, 238, 238);

        var label = new Label
        {
            Text = "Detected games/apps",
            Location = new Point(16, 16),
            AutoSize = true,
            Font = new Font(Font.FontFamily, 9, FontStyle.Bold)
        };

        _searchBox.Location = new Point(18, 44);
        _searchBox.Size = new Size(520, 26);
        _searchBox.PlaceholderText = "Search by game name or .exe";
        _searchBox.TextChanged += (_, _) => ApplyFilter();

        _countLabel.Location = new Point(18, 76);
        _countLabel.Size = new Size(520, 20);

        _list.Location = new Point(18, 102);
        _list.Size = new Size(520, 290);
        _list.CheckOnClick = true;

        var addButton = new Button
        {
            Text = "Add selected",
            Location = new Point(332, 414),
            Width = 98,
            DialogResult = DialogResult.OK
        };
        addButton.Click += (_, _) => AcceptSelection();

        var cancelButton = new Button
        {
            Text = "Cancel",
            Location = new Point(440, 414),
            Width = 98,
            DialogResult = DialogResult.Cancel
        };

        Controls.AddRange([label, _searchBox, _countLabel, _list, addButton, cancelButton]);
        AcceptButton = addButton;
        CancelButton = cancelButton;

        var existing = existingProcesses.ToHashSet(StringComparer.OrdinalIgnoreCase);
        _candidates = GameScanner.ScanInstalledShortcuts()
            .Where(candidate => !existing.Contains(candidate.ProcessName))
            .ToList();

        ApplyFilter();

        if (_candidates.Count == 0)
        {
            _list.Items.Add("No new games/apps found");
            _list.Enabled = false;
            _searchBox.Enabled = false;
            addButton.Enabled = false;
        }
    }

    private void ApplyFilter()
    {
        PersistCheckedItems();

        var query = _searchBox.Text.Trim();
        var filtered = string.IsNullOrWhiteSpace(query)
            ? _candidates
            : _candidates
                .Where(c =>
                    c.DisplayName.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                    c.ProcessName.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                    c.Path.Contains(query, StringComparison.OrdinalIgnoreCase))
                .ToList();

        _list.BeginUpdate();
        _list.Items.Clear();
        foreach (var candidate in filtered)
        {
            _list.Items.Add(candidate, _checkedProcesses.Contains(candidate.ProcessName));
        }

        if (filtered.Count == 0)
        {
            _list.Items.Add("No matching games/apps found");
        }

        _list.EndUpdate();
        _countLabel.Text = $"{filtered.Count} shown / {_candidates.Count} detected";
    }

    private void PersistCheckedItems()
    {
        foreach (GameCandidate candidate in _list.CheckedItems.OfType<GameCandidate>())
        {
            _checkedProcesses.Add(candidate.ProcessName);
        }

        foreach (GameCandidate candidate in _list.Items.OfType<GameCandidate>().Where(c => !_list.CheckedItems.Contains(c)))
        {
            _checkedProcesses.Remove(candidate.ProcessName);
        }
    }

    private void AcceptSelection()
    {
        PersistCheckedItems();
        SelectedRules = _list.CheckedItems
            .OfType<GameCandidate>()
            .Concat(_candidates.Where(c => _checkedProcesses.Contains(c.ProcessName)))
            .DistinctBy(c => c.ProcessName, StringComparer.OrdinalIgnoreCase)
            .Select(c => new GameRule
            {
                ProcessName = c.ProcessName,
                BroadcastNoiseRemovalEnabled = false,
                KrispEnabled = true
            })
            .ToList();
    }
}
