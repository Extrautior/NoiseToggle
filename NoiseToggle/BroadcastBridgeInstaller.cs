using System.Diagnostics;

namespace NoiseToggle;

internal static class BroadcastBridgeInstaller
{
    public static void Install()
    {
        var appDir = AppContext.BaseDirectory;
        var installerPath = Path.Combine(appDir, "install-nvidia-broadcast-bridge.ps1");
        var patchPath = Path.Combine(appDir, "patch-broadcast-bridge.js");

        if (!File.Exists(installerPath) || !File.Exists(patchPath))
        {
            throw new FileNotFoundException("The NVIDIA Broadcast bridge installer files are missing next to NoiseToggle.exe.");
        }

        Process.Start(new ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments = $"-NoProfile -ExecutionPolicy Bypass -File \"{installerPath}\"",
            UseShellExecute = true,
            Verb = "runas"
        });
    }

    public static void Restore()
    {
        var restorePath = Path.Combine(AppContext.BaseDirectory, "RESTORE-NVIDIA-BROADCAST.ps1");
        if (!File.Exists(restorePath))
        {
            throw new FileNotFoundException("The NVIDIA Broadcast restore script is missing next to NoiseToggle.exe.");
        }

        Process.Start(new ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments = $"-NoProfile -ExecutionPolicy Bypass -File \"{restorePath}\"",
            UseShellExecute = true,
            Verb = "runas"
        });
    }
}
