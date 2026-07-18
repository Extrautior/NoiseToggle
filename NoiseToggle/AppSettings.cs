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
    public WaveLinkSettings WaveLink { get; set; } = new();

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
            loaded.WaveLink ??= new WaveLinkSettings();
            loaded.WaveLink.Normalize();

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
        WaveLink ??= new WaveLinkSettings();
        WaveLink.Normalize();
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

internal sealed class WaveLinkSettings
{
    public bool Enabled { get; set; } = true;
    public bool CaptureWheel { get; set; } = true;
    public bool SelectForegroundOnPress { get; set; } = true;
    public bool OnlyActiveChannels { get; set; } = true;
    public bool ShowHud { get; set; } = true;
    public string Host { get; set; } = "localhost";
    public int Port { get; set; } = 8009;
    public string Mix { get; set; } = "Personal Mix";
    public List<string> Channels { get; set; } = [];
    public int StepPercent { get; set; } = 5;
    public float ActivityThreshold { get; set; } = 0.003f;
    public int ActiveHoldMilliseconds { get; set; } = 3500;
    public int HudMonitor { get; set; } = 2;
    public int HudAutoHideMilliseconds { get; set; } = 1800;
    public double HudOpacity { get; set; } = 0.86d;

    public void Normalize()
    {
        Host = string.IsNullOrWhiteSpace(Host) ? "localhost" : Host.Trim();
        Port = Math.Clamp(Port, 1, 65535);
        Mix = string.IsNullOrWhiteSpace(Mix) ? "Personal Mix" : Mix.Trim();
        Channels = Channels.Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x.Trim()).Take(12).ToList();
        StepPercent = Math.Clamp(StepPercent, 1, 25);
        ActivityThreshold = Math.Clamp(ActivityThreshold, 0.0001f, 0.25f);
        ActiveHoldMilliseconds = Math.Clamp(ActiveHoldMilliseconds, 250, 30000);
        HudMonitor = Math.Clamp(HudMonitor, 1, 16);
        HudAutoHideMilliseconds = Math.Clamp(HudAutoHideMilliseconds, 500, 10000);
        HudOpacity = Math.Clamp(HudOpacity, 0.65d, 0.98d);
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
