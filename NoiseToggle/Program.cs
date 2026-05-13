namespace NoiseToggle;

static class Program
{
    [STAThread]
    static void Main()
    {
        ApplicationConfiguration.Initialize();
        using var singleInstance = new Mutex(true, "NoiseToggle.TrayApp", out var isFirstInstance);
        if (!isFirstInstance)
        {
            MessageBox.Show("NoiseToggle is already running.", "NoiseToggle", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        Application.Run(new TrayAppContext());
    }
}
