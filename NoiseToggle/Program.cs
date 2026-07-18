namespace NoiseToggle;

static class Program
{
    [STAThread]
    static async Task<int> Main(string[] args)
    {
        if (args.Contains("--stream-relay", StringComparer.OrdinalIgnoreCase))
        {
            return await StreamAudioRelay.RunAsync(args);
        }

        ApplicationConfiguration.Initialize();
        using var singleInstance = new Mutex(true, "NoiseToggle.TrayApp", out var isFirstInstance);
        if (!isFirstInstance)
        {
            MessageBox.Show("NoiseToggle is already running.", "NoiseToggle", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return 0;
        }

        Application.Run(new TrayAppContext());
        return 0;
    }
}
