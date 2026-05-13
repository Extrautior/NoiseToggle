using System.Diagnostics;
using System.IO.Compression;
using System.Reflection;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace NoiseToggle.Installer;

internal static class Program
{
    private const string AppName = "NoiseToggle";
    private const string PayloadResource = "NoiseToggle.Installer.Payload.NoiseTogglePayload.zip";

    [STAThread]
    private static void Main()
    {
        ApplicationConfiguration.Initialize();

        try
        {
            var installDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Programs",
                AppName);

            var result = MessageBox.Show(
                $"Install {AppName} to:{Environment.NewLine}{installDir}",
                $"{AppName} Setup",
                MessageBoxButtons.OKCancel,
                MessageBoxIcon.Information);

            if (result != DialogResult.OK)
            {
                return;
            }

            Install(installDir);
            MessageBox.Show($"{AppName} installed and started.", $"{AppName} Setup", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, $"{AppName} setup failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private static void Install(string installDir)
    {
        Directory.CreateDirectory(installDir);
        StopRunningApp();
        ExtractPayload(installDir);
        CreateStartMenuShortcut(installDir);
        WriteUninstallEntry(installDir);
        Process.Start(new ProcessStartInfo(Path.Combine(installDir, "NoiseToggle.exe")) { UseShellExecute = true });
    }

    private static void StopRunningApp()
    {
        foreach (var process in Process.GetProcessesByName("NoiseToggle"))
        {
            try
            {
                process.CloseMainWindow();
                if (!process.WaitForExit(1500))
                {
                    process.Kill(entireProcessTree: true);
                }
            }
            catch
            {
                // Continue installing; locked files will report a normal setup error.
            }
        }
    }

    private static void ExtractPayload(string installDir)
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(PayloadResource)
            ?? throw new InvalidOperationException("Installer payload is missing. Rebuild with build-installer.ps1.");

        using var archive = new ZipArchive(stream, ZipArchiveMode.Read);
        foreach (var entry in archive.Entries)
        {
            var destination = Path.GetFullPath(Path.Combine(installDir, entry.FullName));
            if (!destination.StartsWith(Path.GetFullPath(installDir), StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (string.IsNullOrEmpty(entry.Name))
            {
                Directory.CreateDirectory(destination);
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            entry.ExtractToFile(destination, overwrite: true);
        }
    }

    private static void CreateStartMenuShortcut(string installDir)
    {
        var programs = Environment.GetFolderPath(Environment.SpecialFolder.Programs);
        var shortcutPath = Path.Combine(programs, "NoiseToggle.lnk");
        var exePath = Path.Combine(installDir, "NoiseToggle.exe");

        var shellType = Type.GetTypeFromProgID("WScript.Shell") ?? throw new InvalidOperationException("Windows Script Host is unavailable.");
        object? shell = null;
        object? shortcut = null;

        try
        {
            shell = Activator.CreateInstance(shellType);
            shortcut = shellType.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, shell, [shortcutPath]);
            var shortcutType = shortcut!.GetType();
            shortcutType.InvokeMember("TargetPath", BindingFlags.SetProperty, null, shortcut, [exePath]);
            shortcutType.InvokeMember("WorkingDirectory", BindingFlags.SetProperty, null, shortcut, [installDir]);
            shortcutType.InvokeMember("IconLocation", BindingFlags.SetProperty, null, shortcut, [$"{exePath},0"]);
            shortcutType.InvokeMember("Save", BindingFlags.InvokeMethod, null, shortcut, null);
        }
        finally
        {
            if (shortcut is not null)
            {
                Marshal.FinalReleaseComObject(shortcut);
            }

            if (shell is not null)
            {
                Marshal.FinalReleaseComObject(shell);
            }
        }
    }

    private static void WriteUninstallEntry(string installDir)
    {
        using var key = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Uninstall\NoiseToggle");
        var exePath = Path.Combine(installDir, "NoiseToggle.exe");
        key.SetValue("DisplayName", "NoiseToggle");
        key.SetValue("DisplayIcon", exePath);
        key.SetValue("InstallLocation", installDir);
        key.SetValue("Publisher", "Extrautior");
        key.SetValue("NoModify", 1, RegistryValueKind.DWord);
        key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
        key.SetValue("UninstallString", $"powershell.exe -NoProfile -ExecutionPolicy Bypass -Command \"Stop-Process -Name NoiseToggle -ErrorAction SilentlyContinue; Remove-Item -LiteralPath '{installDir.Replace("'", "''")}' -Recurse -Force; Remove-Item -LiteralPath '$env:APPDATA\\Microsoft\\Windows\\Start Menu\\Programs\\NoiseToggle.lnk' -Force -ErrorAction SilentlyContinue; Remove-Item -Path 'HKCU:\\Software\\Microsoft\\Windows\\CurrentVersion\\Uninstall\\NoiseToggle' -Recurse -Force\"");
    }
}
