using System.IO.Pipes;
using System.Text;
using System.Xml.Linq;

namespace NoiseToggle;

internal sealed class IcueNoiseRemovalClient
{
    private const string PipeName = "corsair_automation_sdk";
    private const string ObjectName = "AutomationSdkIpc";
    // This Qt Remote Objects signature identifies the method layout tested with iCUE 5.52.93.
    private const string InterfaceSignature = "120464ba8ff8637340e7291135b2154b1a54f4a6";
    private const int GetBroadcastDevices = 33;
    private const int ActivateBroadcastMode = 34;
    private const int DeactivateBroadcastMode = 35;
    private const int NoiseRemovalMode = 1;
    private const byte InvokeMetaMethod = 0;
    private const uint QtInt = 2;
    private const uint QtString = 10;
    private const uint QtCustomType = 65536;

    private static readonly string SettingsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Corsair", "CUE5", "config.cuecfg");

    public async Task<bool?> GetNoiseRemovalStateAsync(CancellationToken cancellationToken)
    {
        await using var pipe = await ConnectAsync(cancellationToken);
        await GetDeviceIdAsync(pipe, cancellationToken);
        return ReadSettings().NoiseRemoval;
    }

    public async Task SetNoiseRemovalAsync(bool enabled, CancellationToken cancellationToken)
    {
        await using var pipe = await ConnectAsync(cancellationToken);
        var deviceId = await GetDeviceIdAsync(pipe, cancellationToken);
        var current = ReadSettings();
        if (current.NoiseRemoval == enabled)
        {
            return;
        }

        // iCUE stores a single mode. Refuse to replace an active echo-removal mode
        // until the combined mode and pre-game restoration can be verified.
        if (current.Type != "Denoiser")
        {
            throw new InvalidOperationException("Select Noise Removal only in iCUE before switching with NoiseToggle.");
        }

        using var result = await InvokeAsync(pipe, enabled ? ActivateBroadcastMode : DeactivateBroadcastMode, 2,
            enabled ? [deviceId, NoiseRemovalMode, current.Intensity] : [deviceId], cancellationToken);
        Require(ReadVariantType(result) == QtInt);
        var error = result.ReadInt32();
        if (error != 0)
        {
            throw new InvalidOperationException($"iCUE rejected the noise removal request (SDK error {error}).");
        }

        for (var attempt = 0; attempt < 12; attempt++)
        {
            await Task.Delay(100, cancellationToken);
            try
            {
                if (ReadSettings().NoiseRemoval == enabled)
                {
                    return;
                }
            }
            catch (IOException) when (attempt < 11) { }
            catch (System.Xml.XmlException) when (attempt < 11) { }
        }

        throw new InvalidOperationException("iCUE accepted the request, but its noise removal setting did not update.");
    }

    private static (bool NoiseRemoval, string Type, int Intensity) ReadSettings()
    {
        using var stream = new FileStream(SettingsPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var values = XDocument.Load(stream).Root?.Elements("map")
            .SingleOrDefault(map => (string?)map.Attribute("name") == "NvidiaMaxine")?.Elements("value");
        string? GetValue(string name) => values?.SingleOrDefault(value => (string?)value.Attribute("name") == name)?.Value;
        var type = GetValue("Type");
        if (!bool.TryParse(GetValue("Enabled"), out var enabled) || type is null ||
            !int.TryParse(GetValue("Intensity"), out var intensity) || intensity is < 0 or > 100)
        {
            throw new InvalidOperationException("iCUE's NVIDIA Broadcast settings are unavailable.");
        }
        if (enabled && type != "Denoiser")
        {
            throw new InvalidOperationException("Select Noise Removal only in iCUE before switching with NoiseToggle.");
        }

        return (enabled, type, intensity);
    }

    private static async Task<NamedPipeClientStream> ConnectAsync(CancellationToken cancellationToken)
    {
        var pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        try
        {
            try
            {
                await pipe.ConnectAsync(2000, cancellationToken);
            }
            catch (TimeoutException ex)
            {
                throw new InvalidOperationException("Start iCUE and make sure its SDK is enabled.", ex);
            }

            using (var handshake = await ReadPacketAsync(pipe, PacketType.Handshake, cancellationToken))
            {
                Require(ReadQtString(handshake) == "QtRO 2.0");
            }
            using (var objects = await ReadPacketAsync(pipe, PacketType.ObjectList, cancellationToken))
            {
                Require(objects.ReadUInt32() == 1 && ReadQtString(objects) == ObjectName &&
                    ReadQtString(objects) == ObjectName && ReadAscii(objects) == InterfaceSignature);
            }
            await SendPacketAsync(pipe, PacketType.AddObject, writer =>
            {
                WriteQtString(writer, ObjectName);
                writer.Write(true);
            }, cancellationToken);

            using var initialization = await ReadPacketAsync(pipe, PacketType.InitializeDynamic, cancellationToken);
            Require(ReadQtString(initialization) == ObjectName);
            return pipe;
        }
        catch
        {
            await pipe.DisposeAsync();
            throw;
        }
    }

    private static async Task<string> GetDeviceIdAsync(NamedPipeClientStream pipe, CancellationToken cancellationToken)
    {
        using var reply = await InvokeAsync(pipe, GetBroadcastDevices, 1, [], cancellationToken);
        Require(ReadVariantType(reply) == QtCustomType &&
            ReadAscii(reply) == "QRemoteObjectPackets::QSQ_" &&
            ReadAscii(reply) == "QList<AuSdkNvidiaBroadcastDevice>" &&
            ReadAscii(reply) == "AuSdkNvidiaBroadcastDevice");

        // The Qt sequential container embeds a QDataStream in its default big-endian byte order.
        using var devices = new BinaryReader(new MemoryStream(ReadBytes(reply)));
        Require(ReadAscii(devices, bigEndian: true) == "AuSdkNvidiaBroadcastDevice");
        if (ReadLength(devices, bigEndian: true) != 1)
        {
            throw new InvalidOperationException("iCUE must report exactly one NVIDIA Broadcast-compatible audio device.");
        }

        Require(ReadVariantType(devices, bigEndian: true) == QtString);
        var deviceId = ReadQtString(devices, bigEndian: true);
        Require(Guid.TryParse(deviceId, out _));
        return deviceId;
    }

    private static async Task<BinaryReader> InvokeAsync(NamedPipeClientStream pipe, int method, int serial,
        object[] arguments, CancellationToken cancellationToken)
    {
        await SendPacketAsync(pipe, PacketType.Invoke, writer =>
        {
            WriteQtString(writer, ObjectName);
            writer.Write((int)InvokeMetaMethod);
            writer.Write(method);
            writer.Write(arguments.Length);
            foreach (var argument in arguments)
            {
                if (argument is string text)
                {
                    writer.Write(QtString);
                    writer.Write(false);
                    WriteQtString(writer, text);
                }
                else
                {
                    writer.Write(QtInt);
                    writer.Write(false);
                    writer.Write((int)argument);
                }
            }
            writer.Write(serial);
            writer.Write(-1); // This invocation has no property index.
        }, cancellationToken);

        var reply = await ReadPacketAsync(pipe, PacketType.InvokeReply, cancellationToken);
        if (ReadQtString(reply) == ObjectName && reply.ReadInt32() == serial)
        {
            return reply;
        }
        reply.Dispose();
        throw new InvalidDataException("iCUE returned an unexpected automation reply.");
    }

    private static async Task SendPacketAsync(Stream pipe, PacketType type, Action<BinaryWriter> write,
        CancellationToken cancellationToken)
    {
        using var packet = new MemoryStream();
        using var writer = new BinaryWriter(packet);
        writer.Write(0);
        writer.Write((ushort)type);
        write(writer);
        var length = (int)packet.Length;
        packet.Position = 0;
        writer.Write(length - sizeof(int));
        await pipe.WriteAsync(packet.GetBuffer().AsMemory(0, length), cancellationToken);
    }

    private static async Task<BinaryReader> ReadPacketAsync(Stream pipe, PacketType expected, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(3));
        var header = new byte[4];
        while (true)
        {
            await pipe.ReadExactlyAsync(header, timeout.Token);
            var length = BitConverter.ToInt32(header);
            Require(length is >= 2 and <= 65536);
            var payload = new byte[length];
            await pipe.ReadExactlyAsync(payload, timeout.Token);
            var reader = new BinaryReader(new MemoryStream(payload));
            if (reader.ReadUInt16() == (ushort)expected)
            {
                return reader;
            }
            reader.Dispose();
        }
    }

    private static uint ReadVariantType(BinaryReader reader, bool bigEndian = false)
    {
        var type = ReadLength(reader, bigEndian);
        Require(!reader.ReadBoolean());
        return type;
    }

    private static uint ReadLength(BinaryReader reader, bool bigEndian) =>
        bigEndian ? System.Buffers.Binary.BinaryPrimitives.ReverseEndianness(reader.ReadUInt32()) : reader.ReadUInt32();

    private static byte[] ReadBytes(BinaryReader reader, bool bigEndian = false)
    {
        var length = ReadLength(reader, bigEndian);
        Require(length <= reader.BaseStream.Length - reader.BaseStream.Position);
        return reader.ReadBytes((int)length);
    }

    private static string ReadAscii(BinaryReader reader, bool bigEndian = false) =>
        Encoding.ASCII.GetString(ReadBytes(reader, bigEndian)).TrimEnd('\0');

    private static string ReadQtString(BinaryReader reader, bool bigEndian = false) =>
        (bigEndian ? Encoding.BigEndianUnicode : Encoding.Unicode).GetString(ReadBytes(reader, bigEndian));

    private static void WriteQtString(BinaryWriter writer, string value)
    {
        var bytes = Encoding.Unicode.GetBytes(value);
        writer.Write(bytes.Length);
        writer.Write(bytes);
    }

    private static void Require(bool condition)
    {
        if (!condition)
        {
            throw new InvalidDataException("This iCUE Automation SDK response is incompatible with NoiseToggle.");
        }
    }

    private enum PacketType : ushort
    {
        Handshake = 1,
        InitializeDynamic = 3,
        AddObject = 4,
        Invoke = 6,
        InvokeReply = 7,
        ObjectList = 9
    }
}
