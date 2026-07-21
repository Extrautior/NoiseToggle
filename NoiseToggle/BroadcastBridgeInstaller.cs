using System.Diagnostics;

namespace NoiseToggle;

internal static class BroadcastBridgeInstaller
{
    public const string InstallArgument = "--install-broadcast-bridge";
    public const string RestoreArgument = "--restore-broadcast-bridge";

    public static void Install()
    {
        StartElevated(InstallArgument);
    }

    public static void Restore()
    {
        StartElevated(RestoreArgument);
    }

    public static bool TryParseMaintenanceAction(string argument, out BroadcastBridgeMaintenanceAction action)
    {
        if (string.Equals(argument, InstallArgument, StringComparison.OrdinalIgnoreCase))
        {
            action = BroadcastBridgeMaintenanceAction.Install;
            return true;
        }

        if (string.Equals(argument, RestoreArgument, StringComparison.OrdinalIgnoreCase))
        {
            action = BroadcastBridgeMaintenanceAction.Restore;
            return true;
        }

        action = default;
        return false;
    }

    private static void StartElevated(string argument)
    {
        var executable = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(executable) || !File.Exists(executable))
            throw new FileNotFoundException("NoiseToggle could not locate its own executable for the elevated bridge installer.");

        Process.Start(new ProcessStartInfo
        {
            FileName = executable,
            Arguments = argument,
            WorkingDirectory = AppContext.BaseDirectory,
            UseShellExecute = true,
            Verb = "runas"
        });
    }
}

internal enum BroadcastBridgeMaintenanceAction
{
    Install,
    Restore
}
