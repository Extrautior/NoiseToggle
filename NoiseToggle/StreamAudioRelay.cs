using System.Buffers;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace NoiseToggle;

/// <summary>
/// Headless audio relay used by the Vencord StreamBoost plugin. Discord launches this
/// process as its child so desktop capture can exclude Discord, the relay, and all of
/// Discord's renderer processes from the source mix in one process-tree exclusion.
/// </summary>
internal sealed class StreamAudioRelay : IDisposable
{
    private const int SampleRate = 48_000;
    private const int ChannelCount = 2;
    private const float PeakCeiling = 0.98f;
    private const float LimiterKnee = 0.80f;

    private readonly WasapiPlayer _player;
    private readonly BufferedWaveProvider _buffer;
    private WasapiRecorder? _recorder;
    private RelayOptions _options;
    private float _requestedGain;
    private float _appliedGain = 1f;
    private bool _disposed;

    private StreamAudioRelay(
        RelayOptions options,
        WasapiPlayer player,
        BufferedWaveProvider buffer)
    {
        _options = options;
        _requestedGain = PercentToGain(options.GainPercent);
        _buffer = buffer;
        _player = player;
    }

    private static async Task<StreamAudioRelay> CreateAsync(RelayOptions options, MMDevice outputDevice)
    {
        var format = WaveFormat.CreateIeeeFloatWaveFormat(SampleRate, ChannelCount);
        var buffer = new BufferedWaveProvider(format, TimeSpan.FromMilliseconds(750))
        {
            ReadFully = true,
            DiscardOnBufferOverflow = true
        };

        var player = new WasapiPlayerBuilder()
            .WithDevice(outputDevice)
            .WithSharedMode()
            .WithEventSync()
            .WithLatency(40)
            .WithMmcssThreadPriority("Pro Audio")
            .Build();
        player.Init(buffer);

        var relay = new StreamAudioRelay(options, player, buffer);
        try
        {
            await relay.SetSourceAsync(options);
            return relay;
        }
        catch
        {
            relay.Dispose();
            throw;
        }
    }

    private static async Task<WasapiRecorder> BuildRecorderAsync(RelayOptions options)
    {
        var format = WaveFormat.CreateIeeeFloatWaveFormat(SampleRate, ChannelCount);
        var builder = new WasapiRecorderBuilder()
            .WithSharedMode()
            .WithEventSync()
            .WithBufferLength(20)
            .WithFormat(format)
            .WithMmcssThreadPriority("Pro Audio")
            .WithProcessLoopback(
                checked((uint)options.SourceProcessId),
                options.ExcludeProcessTree
                    ? ProcessLoopbackMode.ExcludeTargetProcessTree
                    : ProcessLoopbackMode.IncludeTargetProcessTree);

        return await Task.Run(builder.BuildAsync);
    }

    public static async Task<int> RunAsync(string[] args)
    {
        try
        {
            var options = RelayOptions.Parse(args);
            using var enumerator = new MMDeviceEnumerator();
            using var outputDevice = FindSilentOutput(enumerator, options.OutputDeviceName);
            using var relay = await CreateAsync(options, outputDevice);

            relay.Start();
            WriteProtocolMessage(new
            {
                type = "ready",
                processId = Environment.ProcessId,
                outputDevice = outputDevice.FriendlyName,
                gainPercent = options.GainPercent,
                decibels = Math.Round(PercentToDecibels(options.GainPercent), 1)
            });

            while (await Console.In.ReadLineAsync() is { } line)
            {
                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                using var command = JsonDocument.Parse(line);
                var root = command.RootElement;
                var type = root.TryGetProperty("type", out var typeElement) ? typeElement.GetString() : null;
                if (string.Equals(type, "stop", StringComparison.OrdinalIgnoreCase))
                {
                    break;
                }

                if (string.Equals(type, "gain", StringComparison.OrdinalIgnoreCase) &&
                    root.TryGetProperty("percent", out var percentElement))
                {
                    var percent = Math.Clamp(percentElement.GetSingle(), 100f, 1000f);
                    relay.SetGain(percent);
                    WriteProtocolMessage(new
                    {
                        type = "gain",
                        gainPercent = percent,
                        decibels = Math.Round(PercentToDecibels(percent), 1)
                    });
                }

                if (string.Equals(type, "source", StringComparison.OrdinalIgnoreCase))
                {
                    var sourceOptions = RelayOptions.ParseSourceCommand(root, relay._options);
                    await relay.SetSourceAsync(sourceOptions);
                }

                if (string.Equals(type, "pause", StringComparison.OrdinalIgnoreCase))
                {
                    relay.PauseSource();
                }
            }

            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex);
            WriteProtocolMessage(new { type = "error", error = ex.Message });
            return 1;
        }
    }

    private static MMDevice FindSilentOutput(MMDeviceEnumerator enumerator, string? requestedName)
    {
        var devices = enumerator
            .EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active)
            .ToArray();

        static bool Matches(MMDevice device, string value) =>
            device.FriendlyName.Contains(value, StringComparison.OrdinalIgnoreCase);

        MMDevice? selected = null;
        if (!string.IsNullOrWhiteSpace(requestedName))
        {
            selected = devices.FirstOrDefault(device => Matches(device, requestedName));
        }

        selected ??= devices.FirstOrDefault(device => Matches(device, "CABLE Input"));
        selected ??= devices.FirstOrDefault(device => Matches(device, "CABLE In"));

        foreach (var device in devices)
        {
            if (!ReferenceEquals(device, selected))
            {
                device.Dispose();
            }
        }

        return selected ?? throw new InvalidOperationException(
            "No active VB-Audio CABLE render endpoint was found. Normal Discord stream audio was left unchanged.");
    }

    private void Start()
    {
        _player.Play();
    }

    private async Task SetSourceAsync(RelayOptions options)
    {
        var replacement = await BuildRecorderAsync(options);
        WasapiRecorder? previous = null;
        try
        {
            previous = Interlocked.Exchange(ref _recorder, null);
            if (previous is not null)
            {
                previous.DataAvailable -= ProcessAudio;
                previous.StopRecording();
                previous.Dispose();
                previous = null;
            }

            replacement.DataAvailable += ProcessAudio;
            replacement.StartRecording();
            _options = options;
            Interlocked.Exchange(ref _recorder, replacement);
        }
        catch
        {
            replacement.DataAvailable -= ProcessAudio;
            replacement.Dispose();
            previous?.Dispose();
            throw;
        }
    }

    private void PauseSource()
    {
        var recorder = Interlocked.Exchange(ref _recorder, null);
        if (recorder is not null)
        {
            recorder.DataAvailable -= ProcessAudio;
            try
            {
                recorder.StopRecording();
            }
            catch
            {
                // The source may already have stopped while a stream was closing.
            }

            recorder.Dispose();
        }

        _buffer.ClearBuffer();
    }

    private void SetGain(float percent)
    {
        Volatile.Write(ref _requestedGain, PercentToGain(percent));
    }

    private void ProcessAudio(ReadOnlySpan<byte> input, AudioClientBufferFlags flags)
    {
        if (_disposed || input.IsEmpty)
        {
            return;
        }

        var rented = ArrayPool<byte>.Shared.Rent(input.Length);
        try
        {
            var output = rented.AsSpan(0, input.Length);
            input.CopyTo(output);

            if ((flags & AudioClientBufferFlags.Silent) != 0)
            {
                output.Clear();
            }
            else
            {
                ApplyGainAndLimiter(MemoryMarshal.Cast<byte, float>(output));
            }

            _buffer.AddSamples(rented, 0, input.Length);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }
    }

    private void ApplyGainAndLimiter(Span<float> samples)
    {
        var requested = Volatile.Read(ref _requestedGain);
        var previous = _appliedGain;
        var denominator = Math.Max(1, samples.Length - 1);
        for (var index = 0; index < samples.Length; index++)
        {
            var blend = (float)index / denominator;
            var gain = previous + (requested - previous) * blend;
            var amplified = samples[index] * gain;
            samples[index] = gain <= 1.0001f
                ? Math.Clamp(amplified, -PeakCeiling, PeakCeiling)
                : SoftLimit(amplified);
        }

        _appliedGain = requested;
    }

    private static float SoftLimit(float sample)
    {
        var magnitude = Math.Abs(sample);
        if (magnitude <= LimiterKnee)
        {
            return sample;
        }

        var range = PeakCeiling - LimiterKnee;
        var compressed = LimiterKnee + range * (1f - MathF.Exp(-(magnitude - LimiterKnee) / range));
        return MathF.CopySign(Math.Min(compressed, PeakCeiling), sample);
    }

    private static float PercentToGain(float percent) => Math.Clamp(percent, 100f, 1000f) / 100f;

    private static double PercentToDecibels(float percent) =>
        20d * Math.Log10(PercentToGain(percent));

    private static void WriteProtocolMessage(object payload)
    {
        Console.Out.WriteLine(JsonSerializer.Serialize(payload));
        Console.Out.Flush();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        PauseSource();

        try
        {
            _player.Stop();
        }
        catch
        {
            // The audio device may already have disappeared during Discord shutdown.
        }

        _player.Dispose();
    }

    private sealed record RelayOptions(
        int SourceProcessId,
        bool ExcludeProcessTree,
        float GainPercent,
        string? OutputDeviceName)
    {
        public static RelayOptions Parse(string[] args)
        {
            string? ReadValue(string name)
            {
                var index = Array.FindIndex(args, value => value.Equals(name, StringComparison.OrdinalIgnoreCase));
                return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
            }

            var processText = ReadValue("--source-pid");
            if (!int.TryParse(processText, NumberStyles.None, CultureInfo.InvariantCulture, out var processId) || processId <= 0)
            {
                throw new ArgumentException("--source-pid must be a running process ID.");
            }

            var mode = ReadValue("--capture-mode") ?? "include";
            if (!mode.Equals("include", StringComparison.OrdinalIgnoreCase) &&
                !mode.Equals("exclude", StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException("--capture-mode must be include or exclude.");
            }

            var gainText = ReadValue("--gain-percent") ?? "250";
            if (!float.TryParse(gainText, NumberStyles.Float, CultureInfo.InvariantCulture, out var gainPercent))
            {
                throw new ArgumentException("--gain-percent must be a number from 100 to 1000.");
            }

            return new RelayOptions(
                processId,
                mode.Equals("exclude", StringComparison.OrdinalIgnoreCase),
                Math.Clamp(gainPercent, 100f, 1000f),
                ReadValue("--output-device"));
        }

        public static RelayOptions ParseSourceCommand(JsonElement root, RelayOptions current)
        {
            if (!root.TryGetProperty("sourcePid", out var processElement) ||
                !processElement.TryGetInt32(out var processId) || processId <= 0)
            {
                throw new ArgumentException("source command requires a running sourcePid.");
            }

            var mode = root.TryGetProperty("captureMode", out var modeElement)
                ? modeElement.GetString()
                : "include";
            if (!string.Equals(mode, "include", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(mode, "exclude", StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException("source captureMode must be include or exclude.");
            }

            return current with
            {
                SourceProcessId = processId,
                ExcludeProcessTree = string.Equals(mode, "exclude", StringComparison.OrdinalIgnoreCase)
            };
        }
    }
}
