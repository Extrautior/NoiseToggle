using System.Text.RegularExpressions;

namespace NoiseToggle;

internal static partial class WaveLinkPortDiscovery
{
    public static IReadOnlyList<int> GetCandidatePorts(int configuredPort)
    {
        var result = new List<int>();
        var discovered = DiscoverCurrentPort();
        if (discovered is > 0 and <= 65535)
            result.Add(discovered.Value);
        if (configuredPort is > 0 and <= 65535 && !result.Contains(configuredPort))
            result.Add(configuredPort);
        return result;
    }

    private static int? DiscoverCurrentPort()
    {
        try
        {
            var packages = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Packages");
            if (!Directory.Exists(packages))
                return null;

            var logs = Directory.EnumerateDirectories(packages, "Elgato.WaveLink_*")
                .Select(path => Path.Combine(path, "LocalState", "Logs"))
                .Where(Directory.Exists)
                .SelectMany(path => Directory.EnumerateFiles(path, "*.log"))
                .Select(path => new FileInfo(path))
                .OrderByDescending(file => file.LastWriteTimeUtc)
                .Take(8);

            foreach (var log in logs)
            {
                var port = ReadLastServerPort(log.FullName);
                if (port is not null)
                    return port;
            }
        }
        catch (Exception ex)
        {
            AppLog.Error("Could not discover the current Wave Link websocket port.", ex);
        }
        return null;
    }

    private static int? ReadLastServerPort(string path)
    {
        int? found = null;
        try
        {
            using var stream = new FileStream(
                path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            while (reader.ReadLine() is { } line)
            {
                var match = ServerStartedPattern().Match(line);
                if (match.Success && int.TryParse(match.Groups[1].Value, out var port) &&
                    port is > 0 and <= 65535)
                    found = port;
            }
        }
        catch (IOException)
        {
            // Wave Link can rotate a log between enumeration and opening it.
        }
        catch (UnauthorizedAccessException)
        {
        }
        return found;
    }

    [GeneratedRegex(@"Successfully started websocket server on port\s+(\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex ServerStartedPattern();
}
