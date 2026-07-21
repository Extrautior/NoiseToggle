using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace NoiseToggle;

internal sealed record BroadcastBridgeMaintenanceResult(string Message, string BackupPath);

internal static class BroadcastBridgeMaintenance
{
    private const string BeginMarker = "/* NoiseToggle Broadcast Bridge BEGIN */";
    private const string EndMarker = "/* NoiseToggle Broadcast Bridge END */";
    private static readonly string[] LegacyMarkers =
    [
        "/* NoiseToggle Broadcast Bridge v4 */",
        "/* NoiseToggle Broadcast Bridge v5 */"
    ];

    private static readonly string BroadcastDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
        "NVIDIA Corporation",
        "NVIDIA Broadcast");

    private static readonly string BroadcastExecutable = Path.Combine(BroadcastDirectory, "NVIDIA Broadcast.exe");
    private static readonly string BroadcastResources = Path.Combine(BroadcastDirectory, "resources");
    private static readonly string TargetArchive = Path.Combine(BroadcastResources, "app.asar");

    public static Task<BroadcastBridgeMaintenanceResult> InstallAsync(
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default) =>
        Task.Run(() => Install(progress, cancellationToken), cancellationToken);

    public static Task<BroadcastBridgeMaintenanceResult> RestoreAsync(
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default) =>
        Task.Run(() => Restore(progress, cancellationToken), cancellationToken);

    private static BroadcastBridgeMaintenanceResult Install(
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        AssertInstalled();
        var payloadPath = Path.Combine(AppContext.BaseDirectory, "patch-broadcast-bridge.js");
        if (!File.Exists(payloadPath))
            throw new FileNotFoundException("The bundled NVIDIA Broadcast bridge payload is missing.", payloadPath);

        var version = GetBroadcastVersion();
        var cleanBackup = GetVersionedBackupPath(version);
        var backupMetadata = cleanBackup + ".json";
        var pendingArchive = Path.Combine(BroadcastResources, $"app.asar.noisetoggle-pending-{Guid.NewGuid():N}");
        var broadcastStopped = false;

        try
        {
            progress?.Report("Checking NVIDIA Broadcast and its clean backup...");
            cancellationToken.ThrowIfCancellationRequested();

            var targetInspection = InspectArchive(TargetArchive, version);
            string cleanSource;
            if (targetInspection.HasBridge)
            {
                if (!File.Exists(cleanBackup))
                {
                    throw new InvalidOperationException(
                        $"NVIDIA Broadcast is already patched, but its clean backup is missing. Repair or reinstall NVIDIA Broadcast {version}, then try again.");
                }

                AssertCleanArchive(cleanBackup, version);
                cleanSource = cleanBackup;
            }
            else
            {
                AssertCleanArchive(TargetArchive, version);
                PreserveCleanBackup(TargetArchive, cleanBackup, backupMetadata, version);
                cleanSource = cleanBackup;
            }

            progress?.Report("Building the native bridge archive...");
            CreatePatchedArchive(cleanSource, pendingArchive, payloadPath, version, cancellationToken);
            AssertPatchedArchive(pendingArchive, version);

            progress?.Report("Restarting NVIDIA Broadcast with the bridge...");
            StopBroadcast();
            broadcastStopped = true;
            Thread.Sleep(900);
            File.Replace(pendingArchive, TargetArchive, null, ignoreMetadataErrors: true);

            return new BroadcastBridgeMaintenanceResult(
                $"NoiseToggle NVIDIA Broadcast bridge v7 was installed for NVIDIA Broadcast {version}.",
                cleanBackup);
        }
        finally
        {
            if (File.Exists(pendingArchive))
                File.Delete(pendingArchive);
            if (broadcastStopped)
                StartBroadcastHiddenDetached();
        }
    }

    private static BroadcastBridgeMaintenanceResult Restore(
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        AssertInstalled();
        var version = GetBroadcastVersion();
        var versionedBackup = GetVersionedBackupPath(version);
        var legacyBackup = Path.Combine(BroadcastResources, "app.asar.noisetoggle-backup");
        var backup = File.Exists(versionedBackup) ? versionedBackup : legacyBackup;
        if (!File.Exists(backup))
            throw new FileNotFoundException($"No clean NoiseToggle backup was found for NVIDIA Broadcast {version}.");

        var pendingArchive = Path.Combine(BroadcastResources, $"app.asar.noisetoggle-restore-{Guid.NewGuid():N}");
        var broadcastStopped = false;
        try
        {
            progress?.Report("Validating the clean NVIDIA Broadcast backup...");
            cancellationToken.ThrowIfCancellationRequested();
            AssertCleanArchive(backup, version);
            File.Copy(backup, pendingArchive, overwrite: false);

            progress?.Report("Restoring and restarting NVIDIA Broadcast...");
            StopBroadcast();
            broadcastStopped = true;
            Thread.Sleep(900);
            File.Replace(pendingArchive, TargetArchive, null, ignoreMetadataErrors: true);

            return new BroadcastBridgeMaintenanceResult(
                $"NVIDIA Broadcast {version} was restored from its clean NoiseToggle backup.",
                backup);
        }
        finally
        {
            if (File.Exists(pendingArchive))
                File.Delete(pendingArchive);
            if (broadcastStopped)
                StartBroadcastHiddenDetached();
        }
    }

    internal static void CreatePatchedArchive(
        string sourceArchive,
        string outputArchive,
        string payloadPath,
        string expectedVersion,
        CancellationToken cancellationToken = default)
    {
        var archive = AsarArchiveModel.Open(sourceArchive);
        AssertPackageVersion(archive, expectedVersion);

        var mainEntry = archive.FindPackedFile("build/electron/main.js");
        var originalMain = Encoding.UTF8.GetString(archive.ReadFile(mainEntry));
        var bridge = ReadBridgePayload(payloadPath);
        var patchedMainText = RemoveBridgeBlocks(originalMain).TrimEnd() + "\n" + bridge.Trim() + "\n";
        if (CountOccurrences(patchedMainText, BeginMarker) != 1 ||
            CountOccurrences(patchedMainText, EndMarker) != 1)
        {
            throw new InvalidDataException("The native bridge payload did not produce exactly one complete bridge block.");
        }

        var patchedMain = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(patchedMainText);
        mainEntry.Node["size"] = patchedMain.LongLength;
        var blockSize = ReadIntegrityBlockSize(mainEntry.Node);
        mainEntry.Node["integrity"] = CreateIntegrityNode(patchedMain, blockSize);

        var orderedEntries = archive.PackedFiles
            .OrderBy(entry => entry.SourceOffset)
            .ThenBy(entry => entry.Order)
            .ToList();

        long outputOffset = 0;
        foreach (var entry in orderedEntries)
        {
            entry.Node["offset"] = outputOffset.ToString(CultureInfo.InvariantCulture);
            outputOffset = checked(outputOffset + (ReferenceEquals(entry, mainEntry) ? patchedMain.LongLength : entry.Size));
        }

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputArchive))!);
        using var source = new FileStream(sourceArchive, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, FileOptions.RandomAccess);
        using var output = new FileStream(outputArchive, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1024 * 1024, FileOptions.SequentialScan);
        WriteHeader(output, archive.Root);

        var buffer = new byte[1024 * 1024];
        foreach (var entry in orderedEntries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (ReferenceEquals(entry, mainEntry))
            {
                output.Write(patchedMain);
                continue;
            }

            CopyRange(source, output, checked(archive.FilesDataOffset + entry.SourceOffset), entry.Size, buffer, cancellationToken);
        }

        output.Flush(flushToDisk: true);
    }

    private static void PreserveCleanBackup(
        string source,
        string backup,
        string metadataPath,
        string version)
    {
        var sourceHash = GetSha256(source);
        if (File.Exists(backup))
        {
            var backupHash = GetSha256(backup);
            if (!string.Equals(sourceHash, backupHash, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"A different clean backup already exists for NVIDIA Broadcast {version}. It was left untouched.");
            }
            return;
        }

        File.Copy(source, backup, overwrite: false);
        var metadata = new
        {
            BroadcastVersion = version,
            Sha256 = sourceHash,
            CreatedUtc = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture),
            Source = "Unpatched installed app.asar"
        };
        File.WriteAllText(metadataPath, JsonSerializer.Serialize(metadata, new JsonSerializerOptions { WriteIndented = true }), new UTF8Encoding(false));
    }

    private static void AssertInstalled()
    {
        if (!File.Exists(TargetArchive) || !File.Exists(BroadcastExecutable))
            throw new FileNotFoundException($"NVIDIA Broadcast was not found at {BroadcastDirectory}.");
    }

    private static string GetBroadcastVersion()
    {
        var version = FileVersionInfo.GetVersionInfo(BroadcastExecutable).ProductVersion;
        if (string.IsNullOrWhiteSpace(version))
            throw new InvalidOperationException("Could not detect the installed NVIDIA Broadcast version.");
        return string.Concat(version.Select(character =>
            char.IsLetterOrDigit(character) || character is '.' or '_' or '-' ? character : '_'));
    }

    private static string GetVersionedBackupPath(string version) =>
        Path.Combine(BroadcastResources, $"app.asar.noisetoggle-clean-{version}");

    private static ArchiveInspection InspectArchive(string archivePath, string expectedVersion)
    {
        var archive = AsarArchiveModel.Open(archivePath);
        AssertPackageVersion(archive, expectedVersion);
        var main = Encoding.UTF8.GetString(archive.ReadFile(archive.FindPackedFile("build/electron/main.js")));
        return new ArchiveInspection(ContainsAnyBridgeMarker(main));
    }

    private static void AssertCleanArchive(string archivePath, string expectedVersion)
    {
        var inspection = InspectArchive(archivePath, expectedVersion);
        if (inspection.HasBridge)
            throw new InvalidDataException($"{archivePath} contains a NoiseToggle bridge and is not a clean NVIDIA Broadcast archive.");
    }

    private static void AssertPatchedArchive(string archivePath, string expectedVersion)
    {
        var archive = AsarArchiveModel.Open(archivePath);
        AssertPackageVersion(archive, expectedVersion);
        var mainEntry = archive.FindPackedFile("build/electron/main.js");
        var main = archive.ReadFile(mainEntry);
        var text = Encoding.UTF8.GetString(main);
        if (CountOccurrences(text, BeginMarker) != 1 || CountOccurrences(text, EndMarker) != 1)
            throw new InvalidDataException("The generated NVIDIA Broadcast archive does not contain exactly one complete NoiseToggle bridge.");
        AssertIntegrity(mainEntry.Node, main);
    }

    private static void AssertPackageVersion(AsarArchiveModel archive, string expectedVersion)
    {
        var packageEntry = archive.FindPackedFile("package.json");
        using var package = JsonDocument.Parse(archive.ReadFile(packageEntry));
        var packageVersion = package.RootElement.GetProperty("version").GetString();
        if (string.IsNullOrWhiteSpace(packageVersion) ||
            !expectedVersion.StartsWith(packageVersion, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                $"The NVIDIA Broadcast archive contains version {packageVersion ?? "unknown"}, not installed build {expectedVersion}.");
        }
    }

    private static string ReadBridgePayload(string payloadPath)
    {
        var bridge = File.ReadAllText(payloadPath, Encoding.UTF8).Trim();
        if (CountOccurrences(bridge, BeginMarker) != 1 || CountOccurrences(bridge, EndMarker) != 1)
            throw new InvalidDataException("The bundled bridge payload is malformed.");
        return bridge;
    }

    private static string RemoveBridgeBlocks(string source)
    {
        var result = source;
        while (true)
        {
            var begin = result.IndexOf(BeginMarker, StringComparison.Ordinal);
            if (begin < 0)
                break;
            var end = result.IndexOf(EndMarker, begin + BeginMarker.Length, StringComparison.Ordinal);
            result = end >= 0
                ? result.Remove(begin, end + EndMarker.Length - begin)
                : result[..begin];
        }

        foreach (var marker in LegacyMarkers)
        {
            var index = result.IndexOf(marker, StringComparison.Ordinal);
            if (index >= 0)
                result = result[..index];
        }

        return result.TrimEnd();
    }

    private static int ReadIntegrityBlockSize(JsonObject fileNode)
    {
        if (fileNode["integrity"] is JsonObject integrity &&
            integrity["blockSize"] is JsonValue value &&
            value.TryGetValue<int>(out var blockSize) &&
            blockSize > 0)
        {
            return blockSize;
        }
        return 4 * 1024 * 1024;
    }

    private static JsonObject CreateIntegrityNode(byte[] data, int blockSize)
    {
        var blocks = new JsonArray();
        for (var offset = 0; offset < data.Length; offset += blockSize)
        {
            var length = Math.Min(blockSize, data.Length - offset);
            blocks.Add(ToLowerHex(SHA256.HashData(data.AsSpan(offset, length))));
        }

        if (data.Length == 0)
            blocks.Add(ToLowerHex(SHA256.HashData(ReadOnlySpan<byte>.Empty)));

        return new JsonObject
        {
            ["algorithm"] = "SHA256",
            ["hash"] = ToLowerHex(SHA256.HashData(data)),
            ["blockSize"] = blockSize,
            ["blocks"] = blocks
        };
    }

    private static void AssertIntegrity(JsonObject fileNode, byte[] data)
    {
        if (fileNode["integrity"] is not JsonObject expected)
            throw new InvalidDataException("The generated main.js entry is missing integrity metadata.");

        var actual = CreateIntegrityNode(data, ReadIntegrityBlockSize(fileNode));
        if (!JsonNode.DeepEquals(expected, actual))
            throw new InvalidDataException("The generated main.js integrity metadata does not match its contents.");
    }

    private static void WriteHeader(Stream output, JsonObject root)
    {
        var json = root.ToJsonString(new JsonSerializerOptions { WriteIndented = false });
        var jsonBytes = new UTF8Encoding(false).GetBytes(json);
        var stringLength = jsonBytes.Length;
        var stringPayloadSize = Align4(checked(4 + stringLength));
        var headerPickleSize = checked(4 + stringPayloadSize);

        using var writer = new BinaryWriter(output, Encoding.UTF8, leaveOpen: true);
        writer.Write(4);
        writer.Write(headerPickleSize);
        writer.Write(stringPayloadSize);
        writer.Write(stringLength);
        writer.Write(jsonBytes);
        var padding = stringPayloadSize - 4 - stringLength;
        if (padding > 0)
            writer.Write(new byte[padding]);
    }

    private static int Align4(int value) => checked((value + 3) & ~3);

    private static void CopyRange(
        Stream source,
        Stream destination,
        long sourceOffset,
        long length,
        byte[] buffer,
        CancellationToken cancellationToken)
    {
        source.Position = sourceOffset;
        var remaining = length;
        while (remaining > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var requested = (int)Math.Min(buffer.Length, remaining);
            var read = source.Read(buffer, 0, requested);
            if (read == 0)
                throw new EndOfStreamException("The NVIDIA Broadcast archive ended before all indexed file data was read.");
            destination.Write(buffer, 0, read);
            remaining -= read;
        }
    }

    private static string GetSha256(string path)
    {
        using var stream = File.OpenRead(path);
        return ToLowerHex(SHA256.HashData(stream));
    }

    private static string ToLowerHex(byte[] hash) => Convert.ToHexString(hash).ToLowerInvariant();

    private static bool ContainsAnyBridgeMarker(string value) =>
        value.Contains(BeginMarker, StringComparison.Ordinal) ||
        LegacyMarkers.Any(marker => value.Contains(marker, StringComparison.Ordinal));

    private static int CountOccurrences(string value, string marker)
    {
        var count = 0;
        var position = 0;
        while ((position = value.IndexOf(marker, position, StringComparison.Ordinal)) >= 0)
        {
            count++;
            position += marker.Length;
        }
        return count;
    }

    private static void StopBroadcast()
    {
        foreach (var process in Process.GetProcessesByName("NVIDIA Broadcast"))
        {
            using (process)
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                    process.WaitForExit(5000);
                }
                catch
                {
                    if (!process.HasExited)
                        throw;
                }
            }
        }
    }

    private static void StartBroadcastHiddenDetached()
    {
        if (!File.Exists(BroadcastExecutable))
            return;

        var shellType = Type.GetTypeFromProgID("Shell.Application")
            ?? throw new InvalidOperationException("Windows Shell.Application is unavailable.");
        object? shell = null;
        try
        {
            shell = Activator.CreateInstance(shellType);
            shellType.InvokeMember(
                "ShellExecute",
                BindingFlags.InvokeMethod,
                null,
                shell,
                [BroadcastExecutable, "--launch-hidden", BroadcastDirectory, "open", 0]);
        }
        finally
        {
            if (shell is not null)
                Marshal.FinalReleaseComObject(shell);
        }
    }

    private sealed record ArchiveInspection(bool HasBridge);

    private sealed class AsarArchiveModel
    {
        private AsarArchiveModel(string path, JsonObject root, long filesDataOffset, List<AsarPackedFile> packedFiles)
        {
            Path = path;
            Root = root;
            FilesDataOffset = filesDataOffset;
            PackedFiles = packedFiles;
        }

        public string Path { get; }
        public JsonObject Root { get; }
        public long FilesDataOffset { get; }
        public List<AsarPackedFile> PackedFiles { get; }

        public static AsarArchiveModel Open(string path)
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);
            if (reader.ReadInt32() != 4)
                throw new InvalidDataException($"{path} is not a supported Electron ASAR archive.");

            var headerPickleSize = reader.ReadInt32();
            var stringPayloadSize = reader.ReadInt32();
            var stringLength = reader.ReadInt32();
            if (headerPickleSize < 8 || stringPayloadSize < 4 || stringLength <= 1 ||
                headerPickleSize != stringPayloadSize + 4 || stringLength > stringPayloadSize - 4)
            {
                throw new InvalidDataException($"{path} has an invalid ASAR header.");
            }

            var stringBytes = reader.ReadBytes(stringLength);
            if (stringBytes.Length != stringLength)
                throw new InvalidDataException($"{path} has a truncated ASAR header string.");
            var json = Encoding.UTF8.GetString(stringBytes);
            var root = JsonNode.Parse(json)?.AsObject()
                ?? throw new InvalidDataException($"{path} has an invalid ASAR JSON index.");
            var filesDataOffset = checked(8L + headerPickleSize);

            var packedFiles = new List<AsarPackedFile>();
            var order = 0;
            CollectFiles(root, string.Empty, packedFiles, ref order);
            foreach (var file in packedFiles)
            {
                if (file.SourceOffset < 0 || file.Size < 0 ||
                    checked(filesDataOffset + file.SourceOffset + file.Size) > stream.Length)
                {
                    throw new InvalidDataException($"ASAR entry {file.ArchivePath} points outside {path}.");
                }
            }

            return new AsarArchiveModel(path, root, filesDataOffset, packedFiles);
        }

        public AsarPackedFile FindPackedFile(string archivePath) =>
            PackedFiles.FirstOrDefault(file => string.Equals(file.ArchivePath, archivePath, StringComparison.Ordinal))
            ?? throw new FileNotFoundException($"The NVIDIA Broadcast ASAR entry {archivePath} was not found.");

        public byte[] ReadFile(AsarPackedFile file)
        {
            if (file.Size > int.MaxValue)
                throw new InvalidDataException($"ASAR entry {file.ArchivePath} is too large to read into memory.");
            using var stream = new FileStream(Path, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, FileOptions.RandomAccess);
            stream.Position = checked(FilesDataOffset + file.SourceOffset);
            var data = new byte[(int)file.Size];
            stream.ReadExactly(data);
            return data;
        }

        private static void CollectFiles(
            JsonObject directory,
            string parentPath,
            List<AsarPackedFile> result,
            ref int order)
        {
            if (directory["files"] is not JsonObject files)
                throw new InvalidDataException("An ASAR directory is missing its files index.");

            foreach (var item in files)
            {
                if (item.Value is not JsonObject node)
                    throw new InvalidDataException($"ASAR entry {item.Key} has invalid metadata.");
                var archivePath = string.IsNullOrEmpty(parentPath) ? item.Key : parentPath + "/" + item.Key;
                if (node["files"] is JsonObject)
                {
                    CollectFiles(node, archivePath, result, ref order);
                    continue;
                }

                if (IsTrue(node["unpacked"]) || node["link"] is not null)
                    continue;

                var offsetText = node["offset"]?.GetValue<string>()
                    ?? throw new InvalidDataException($"ASAR entry {archivePath} is missing its offset.");
                if (!long.TryParse(offsetText, NumberStyles.None, CultureInfo.InvariantCulture, out var offset))
                    throw new InvalidDataException($"ASAR entry {archivePath} has an invalid offset.");
                var size = node["size"]?.GetValue<long>()
                    ?? throw new InvalidDataException($"ASAR entry {archivePath} is missing its size.");
                result.Add(new AsarPackedFile(archivePath, node, offset, size, order++));
            }
        }

        private static bool IsTrue(JsonNode? node) =>
            node is JsonValue value && value.TryGetValue<bool>(out var result) && result;
    }

    private sealed record AsarPackedFile(
        string ArchivePath,
        JsonObject Node,
        long SourceOffset,
        long Size,
        int Order);
}
