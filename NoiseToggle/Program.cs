namespace NoiseToggle;

static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();

        if (args.Length == 1 && BroadcastBridgeInstaller.TryParseMaintenanceAction(args[0], out var action))
        {
            Application.Run(new BridgeMaintenanceForm(action));
            return;
        }

        using var singleInstance = new Mutex(true, "NoiseToggle.TrayApp", out var isFirstInstance);
        if (!isFirstInstance)
        {
            MessageBox.Show("NoiseToggle is already running.", "NoiseToggle", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        Application.Run(new TrayAppContext());
    }
}
