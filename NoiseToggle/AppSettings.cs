using System.Text.Json;
using System.Text.Json.Serialization;

namespace NoiseToggle;

internal sealed class AppSettings
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public string Hotkey { get; set; } = "Ctrl+Alt+K";
    public string BridgeToken { get; set; } = Guid.NewGuid().ToString("N");
    public int BridgePort { get; set; } = 28473;
    public int BroadcastBridgePort { get; set; } = 28474;
    public ToggleMode LastMode { get; set; } = ToggleMode.Broadcast;
    public bool StartWithWindows { get; set; }
    public bool AutoSwitchForGames { get; set; }
    public List<string> GameProcesses { get; set; } = [];
    public List<GameRule> GameRules { get; set; } = [];

    public static string AppDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "NoiseToggle");

    public static string SettingsPath => Path.Combine(AppDirectory, "settings.json");

    public static AppSettings Load()
    {
        Directory.CreateDirectory(AppDirectory);

        if (!File.Exists(SettingsPath))
        {
            var created = new AppSettings();
            created.Save();
            return created;
        }

        try
        {
            var json = File.ReadAllText(SettingsPath);
            var loaded = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions) ?? new AppSettings();
            if (string.IsNullOrWhiteSpace(loaded.BridgeToken))
            {
                loaded.BridgeToken = Guid.NewGuid().ToString("N");
            }

            if (loaded.BridgePort <= 0)
            {
                loaded.BridgePort = 28473;
            }

            if (loaded.BroadcastBridgePort <= 0)
            {
                loaded.BroadcastBridgePort = 28474;
            }

            loaded.MigrateGameProcesses();
            loaded.NormalizeGameRules();

            loaded.Save();
            return loaded;
        }
        catch
        {
            var backupPath = SettingsPath + ".broken-" + DateTime.Now.ToString("yyyyMMdd-HHmmss");
            File.Copy(SettingsPath, backupPath, overwrite: true);
            var recovered = new AppSettings();
            recovered.Save();
            return recovered;
        }
    }

    public void Save()
    {
        Directory.CreateDirectory(AppDirectory);
        MigrateGameProcesses();
        NormalizeGameRules();
        File.WriteAllText(SettingsPath, JsonSerializer.Serialize(this, JsonOptions));
    }

    public static string NormalizeProcessName(string value)
    {
        var name = Path.GetFileName(value.Trim());
        return name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? name : name + ".exe";
    }

    private void MigrateGameProcesses()
    {
        if (GameProcesses.Count == 0)
        {
            return;
        }

        foreach (var process in GameProcesses)
        {
            if (!GameRules.Any(r => r.ProcessName.Equals(process, StringComparison.OrdinalIgnoreCase)))
            {
                GameRules.Add(new GameRule
                {
                    ProcessName = process,
                    BroadcastNoiseRemovalEnabled = false,
                    KrispEnabled = true
                });
            }
        }

        GameProcesses = [];
    }

    private void NormalizeGameRules()
    {
        GameRules = GameRules
            .Where(r => !string.IsNullOrWhiteSpace(r.ProcessName))
            .Select(r => r with { ProcessName = NormalizeProcessName(r.ProcessName) })
            .GroupBy(r => r.ProcessName, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .OrderBy(r => r.ProcessName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}

internal sealed record GameRule
{
    public string ProcessName { get; init; } = "";
    public bool BroadcastNoiseRemovalEnabled { get; init; }
    public bool KrispEnabled { get; init; } = true;

    public string ActionText =>
        (BroadcastNoiseRemovalEnabled, KrispEnabled) switch
        {
            (false, true) => "Broadcast off, Krisp on",
            (true, false) => "Broadcast on, Krisp off",
            (false, false) => "Broadcast off, Krisp off",
            (true, true) => "Broadcast on, Krisp on"
        };

    public override string ToString()
    {
        return $"{ProcessName} -> {ActionText}";
    }
}

internal enum ToggleMode
{
    Broadcast,
    Krisp
}
