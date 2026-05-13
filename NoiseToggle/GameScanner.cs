using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace NoiseToggle;

internal static class GameScanner
{
    private static readonly string[] CommonGameFolderNames =
    [
        "SteamLibrary\\steamapps\\common",
        "Steam\\steamapps\\common",
        "Epic Games",
        "XboxGames",
        "Games",
        "games",
        "GOG Games",
        "Riot Games",
        "Ubisoft Game Launcher\\games",
        "EA Games"
    ];

    private static readonly string[] InterestingPathParts =
    [
        "\\steam\\",
        "\\steamapps\\",
        "\\epic games\\",
        "\\ea games\\",
        "\\ubisoft\\",
        "\\gog galaxy\\",
        "\\xboxgames\\",
        "\\riot games\\",
        "\\battlenet\\",
        "\\battle.net\\"
    ];

    private static readonly string[] ExcludedExeNameParts =
    [
        "steam",
        "epicgameslauncher",
        "epicwebhelper",
        "eadesktop",
        "ealauncher",
        "origin",
        "ubisoftconnect",
        "upc",
        "gog galaxy",
        "galaxyclient",
        "bsglauncher",
        "battle.net",
        "blizzard",
        "launcher",
        "installer",
        "setup",
        "unins",
        "uninstall",
        "crash",
        "report",
        "redist",
        "vcredist",
        "dotnet",
        "unitycrashhandler",
        "easyanticheat",
        "eac",
        "battleye",
        "beservice",
        "anticheat",
        "helper",
        "bootstrapper"
    ];

    public static List<GameCandidate> ScanInstalledShortcuts()
    {
        var candidates = new List<GameCandidate>();
        candidates.AddRange(ScanShortcuts());
        candidates.AddRange(ScanRegistryInstalls());
        candidates.AddRange(ScanSteamLibraries());
        candidates.AddRange(ScanEpicManifests());
        candidates.AddRange(ScanCommonGameFolders());

        return candidates
            .Where(c => !IsExcludedExecutable(c.ProcessName))
            .GroupBy(c => c.ProcessName, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.OrderByDescending(c => c.Score).ThenBy(c => c.DisplayName).First())
            .OrderBy(c => c.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static IEnumerable<GameCandidate> ScanShortcuts()
    {
        var shortcutRoots = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.StartMenu),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu),
            Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory)
        };

        var candidates = new List<GameCandidate>();
        foreach (var root in shortcutRoots.Where(Directory.Exists))
        {
            foreach (var shortcut in Directory.EnumerateFiles(root, "*.lnk", SearchOption.AllDirectories))
            {
                var target = TryResolveShortcut(shortcut);
                if (string.IsNullOrWhiteSpace(target) ||
                    !target.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ||
                    !File.Exists(target))
                {
                    continue;
                }

                var lowerTarget = target.ToLowerInvariant();
                if (!InterestingPathParts.Any(lowerTarget.Contains))
                {
                    continue;
                }

                candidates.Add(new GameCandidate(
                    Path.GetFileNameWithoutExtension(shortcut),
                    AppSettings.NormalizeProcessName(target),
                    target,
                    70));
            }
        }

        return candidates;
    }

    private static IEnumerable<GameCandidate> ScanRegistryInstalls()
    {
        var registryPaths = new[]
        {
            @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall",
            @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall"
        };

        foreach (var hive in new[] { Registry.CurrentUser, Registry.LocalMachine })
        {
            foreach (var path in registryPaths)
            {
                using var key = hive.OpenSubKey(path);
                if (key is null)
                {
                    continue;
                }

                foreach (var subKeyName in key.GetSubKeyNames())
                {
                    using var subKey = key.OpenSubKey(subKeyName);
                    if (subKey is null)
                    {
                        continue;
                    }

                    var displayName = subKey.GetValue("DisplayName") as string;
                    if (string.IsNullOrWhiteSpace(displayName) || IsExcludedDisplayName(displayName))
                    {
                        continue;
                    }

                    var installLocation = subKey.GetValue("InstallLocation") as string;
                    var displayIcon = CleanIconPath(subKey.GetValue("DisplayIcon") as string);
                    var exePath = File.Exists(displayIcon) &&
                                  displayIcon.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) &&
                                  !IsExcludedExecutable(displayIcon)
                        ? displayIcon
                        : FindLikelyGameExe(installLocation, displayName);

                    if (string.IsNullOrWhiteSpace(exePath) || !File.Exists(exePath))
                    {
                        continue;
                    }

                    yield return new GameCandidate(displayName, AppSettings.NormalizeProcessName(exePath), exePath, 90);
                }
            }
        }
    }

    private static IEnumerable<GameCandidate> ScanSteamLibraries()
    {
        foreach (var steamApps in GetSteamAppsFolders())
        {
            var common = Path.Combine(steamApps, "common");
            if (!Directory.Exists(common))
            {
                continue;
            }

            foreach (var gameDir in SafeEnumerateDirectories(common))
            {
                var displayName = Path.GetFileName(gameDir);
                var exePath = FindLikelyGameExe(gameDir, displayName);
                if (!string.IsNullOrWhiteSpace(exePath))
                {
                    yield return new GameCandidate(displayName, AppSettings.NormalizeProcessName(exePath), exePath, 95);
                }
            }
        }
    }

    private static IEnumerable<string> GetSteamAppsFolders()
    {
        var roots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var drive in DriveInfo.GetDrives().Where(d => d.IsReady))
        {
            AddIfDirectory(roots, Path.Combine(drive.RootDirectory.FullName, "Steam", "steamapps"));
            AddIfDirectory(roots, Path.Combine(drive.RootDirectory.FullName, "SteamLibrary", "steamapps"));
        }

        foreach (var steamRoot in roots.Select(r => Directory.GetParent(r)?.FullName).Where(r => !string.IsNullOrWhiteSpace(r)).ToList())
        {
            var libraryFolders = Path.Combine(steamRoot!, "steamapps", "libraryfolders.vdf");
            if (!File.Exists(libraryFolders))
            {
                continue;
            }

            foreach (var line in File.ReadLines(libraryFolders))
            {
                var parts = line.Split('"', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                if (parts.Length < 2 || !parts.Contains("path"))
                {
                    continue;
                }

                var pathValue = parts.Last().Replace(@"\\", @"\");
                AddIfDirectory(roots, Path.Combine(pathValue, "steamapps"));
            }
        }

        return roots;
    }

    private static IEnumerable<GameCandidate> ScanEpicManifests()
    {
        var manifests = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Epic", "EpicGamesLauncher", "Data", "Manifests");
        if (!Directory.Exists(manifests))
        {
            yield break;
        }

        foreach (var manifest in Directory.EnumerateFiles(manifests, "*.item"))
        {
            string text;
            try
            {
                text = File.ReadAllText(manifest);
            }
            catch
            {
                continue;
            }

            var displayName = ExtractJsonString(text, "DisplayName") ?? ExtractJsonString(text, "AppName");
            var installLocation = ExtractJsonString(text, "InstallLocation");
            var exePath = FindLikelyGameExe(installLocation, displayName);
            if (!string.IsNullOrWhiteSpace(displayName) && !string.IsNullOrWhiteSpace(exePath))
            {
                yield return new GameCandidate(displayName, AppSettings.NormalizeProcessName(exePath), exePath, 95);
            }
        }
    }

    private static IEnumerable<GameCandidate> ScanCommonGameFolders()
    {
        foreach (var drive in DriveInfo.GetDrives().Where(d => d.IsReady))
        {
            foreach (var folderName in CommonGameFolderNames)
            {
                var root = Path.Combine(drive.RootDirectory.FullName, folderName);
                if (!Directory.Exists(root))
                {
                    continue;
                }

                foreach (var gameDir in SafeEnumerateDirectories(root))
                {
                    var displayName = Path.GetFileName(gameDir);
                    if (IsExcludedDisplayName(displayName))
                    {
                        continue;
                    }

                    var exePath = FindLikelyGameExe(gameDir, displayName);
                    if (!string.IsNullOrWhiteSpace(exePath))
                    {
                        yield return new GameCandidate(displayName, AppSettings.NormalizeProcessName(exePath), exePath, 60);
                    }
                }
            }
        }
    }

    private static string? FindLikelyGameExe(string? root, string? displayName)
    {
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
        {
            return null;
        }

        var normalizedDisplay = NormalizeComparableName(displayName ?? Path.GetFileName(root));
        var candidates = SafeEnumerateFiles(root, "*.exe", maxDepth: 4)
            .Where(f => !IsExcludedExecutable(Path.GetFileName(f)))
            .Select(f => new
            {
                Path = f,
                Name = Path.GetFileNameWithoutExtension(f),
                DirectoryName = Path.GetFileName(Path.GetDirectoryName(f) ?? ""),
                File = new FileInfo(f)
            })
            .Where(c => TryGetFileLength(c.File) > 128 * 1024)
            .Select(c => new
            {
                c.Path,
                Score =
                    ScoreName(c.Name, normalizedDisplay) +
                    ScoreName(c.DirectoryName, normalizedDisplay) / 2 +
                    (c.Path.Contains(@"\Binaries\Win64\", StringComparison.OrdinalIgnoreCase) ? 12 : 0) +
                    (c.Path.Contains(@"\Win64\", StringComparison.OrdinalIgnoreCase) ? 6 : 0) +
                    (TryGetFileLength(c.File) > 5 * 1024 * 1024 ? 8 : 0) -
                    (c.Path.Contains(@"\Engine\", StringComparison.OrdinalIgnoreCase) ? 20 : 0)
            })
            .OrderByDescending(c => c.Score)
            .ToList();

        return candidates.FirstOrDefault()?.Path;
    }

    private static int ScoreName(string value, string normalizedDisplay)
    {
        var normalized = NormalizeComparableName(value);
        if (string.IsNullOrWhiteSpace(normalized) || string.IsNullOrWhiteSpace(normalizedDisplay))
        {
            return 0;
        }

        if (normalized.Equals(normalizedDisplay, StringComparison.OrdinalIgnoreCase))
        {
            return 60;
        }

        if (normalized.Contains(normalizedDisplay, StringComparison.OrdinalIgnoreCase) ||
            normalizedDisplay.Contains(normalized, StringComparison.OrdinalIgnoreCase))
        {
            return 35;
        }

        return normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Count(part => normalizedDisplay.Contains(part, StringComparison.OrdinalIgnoreCase)) * 8;
    }

    private static string NormalizeComparableName(string value)
    {
        var chars = value
            .Where(c => char.IsLetterOrDigit(c) || char.IsWhiteSpace(c))
            .Select(char.ToLowerInvariant)
            .ToArray();
        return new string(chars).Trim();
    }

    private static IEnumerable<string> SafeEnumerateDirectories(string root)
    {
        try
        {
            return Directory.EnumerateDirectories(root);
        }
        catch
        {
            return [];
        }
    }

    private static IEnumerable<string> SafeEnumerateFiles(string root, string pattern, int maxDepth)
    {
        var results = new List<string>();
        var queue = new Queue<(string Path, int Depth)>();
        queue.Enqueue((root, 0));

        while (queue.Count > 0)
        {
            var (path, depth) = queue.Dequeue();
            try
            {
                results.AddRange(Directory.EnumerateFiles(path, pattern));

                if (depth >= maxDepth)
                {
                    continue;
                }

                foreach (var directory in Directory.EnumerateDirectories(path))
                {
                    queue.Enqueue((directory, depth + 1));
                }
            }
            catch
            {
                // Ignore inaccessible folders.
            }
        }

        return results;
    }

    private static void AddIfDirectory(HashSet<string> paths, string path)
    {
        if (Directory.Exists(path))
        {
            paths.Add(path);
        }
    }

    private static long TryGetFileLength(FileInfo file)
    {
        try
        {
            return file.Length;
        }
        catch
        {
            return 0;
        }
    }

    private static string? CleanIconPath(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var cleaned = value.Trim().Trim('"');
        var comma = cleaned.LastIndexOf(',');
        if (comma > 2)
        {
            cleaned = cleaned[..comma].Trim().Trim('"');
        }

        return cleaned;
    }

    private static string? ExtractJsonString(string json, string propertyName)
    {
        var needle = "\"" + propertyName + "\"";
        var index = json.IndexOf(needle, StringComparison.OrdinalIgnoreCase);
        if (index < 0)
        {
            return null;
        }

        var colon = json.IndexOf(':', index);
        var firstQuote = json.IndexOf('"', colon + 1);
        var secondQuote = firstQuote >= 0 ? json.IndexOf('"', firstQuote + 1) : -1;
        return firstQuote >= 0 && secondQuote > firstQuote ? json[(firstQuote + 1)..secondQuote].Replace(@"\\", @"\") : null;
    }

    private static bool IsExcludedExecutable(string processName)
    {
        var name = Path.GetFileNameWithoutExtension(processName).ToLowerInvariant();
        return ExcludedExeNameParts.Any(name.Contains);
    }

    private static bool IsExcludedDisplayName(string displayName)
    {
        var name = displayName.ToLowerInvariant();
        return name.Contains("launcher") ||
               name.Contains("redistributable") ||
               name.Contains("prerequisite") ||
               name.Contains("online services") ||
               name.Contains("sdk") ||
               name.Contains("driver");
    }

    private static string? TryResolveShortcut(string shortcutPath)
    {
        Type? shellType = null;
        object? shell = null;
        object? shortcut = null;
        try
        {
            shellType = Type.GetTypeFromProgID("WScript.Shell");
            if (shellType is null)
            {
                return null;
            }

            shell = Activator.CreateInstance(shellType);
            shortcut = shellType.InvokeMember("CreateShortcut", System.Reflection.BindingFlags.InvokeMethod, null, shell, [shortcutPath]);
            return shortcut?.GetType().InvokeMember("TargetPath", System.Reflection.BindingFlags.GetProperty, null, shortcut, null) as string;
        }
        catch
        {
            return null;
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
}

internal sealed record GameCandidate(string DisplayName, string ProcessName, string Path, int Score)
{
    public override string ToString()
    {
        return $"{DisplayName} ({ProcessName})";
    }
}
