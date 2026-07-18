using NAudio.CoreAudioApi;

namespace NoiseToggle;

internal sealed record WaveActivitySnapshot(
    IReadOnlyDictionary<string, float> Peaks,
    IReadOnlySet<string> ActiveNames,
    IReadOnlyDictionary<int, IReadOnlySet<string>> ProcessChannels);

internal sealed class WaveLinkActivityMonitor : IAsyncDisposable
{
    private readonly HashSet<string> _channelNames;
    private readonly float _threshold;
    private readonly TimeSpan _holdTime;
    private readonly CancellationTokenSource _lifetime = new();
    private Task? _worker;

    public event Action<WaveActivitySnapshot>? SnapshotUpdated;

    public WaveLinkActivityMonitor(IEnumerable<string> channelNames, float threshold, int holdMilliseconds)
    {
        _channelNames = channelNames.ToHashSet(StringComparer.OrdinalIgnoreCase);
        _threshold = threshold;
        _holdTime = TimeSpan.FromMilliseconds(holdMilliseconds);
    }

    public void Start() => _worker ??= Task.Run(() => RunAsync(_lifetime.Token));

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        using var enumerator = new MMDeviceEnumerator();
        var endpoints = new List<(string Channel, MMDevice Device)>();
        var lastActive = new Dictionary<string, DateTimeOffset>(StringComparer.OrdinalIgnoreCase);
        IReadOnlyDictionary<int, IReadOnlySet<string>> processChannels =
            new Dictionary<int, IReadOnlySet<string>>();
        var nextRefresh = DateTimeOffset.MinValue;
        var nextSessionRefresh = DateTimeOffset.MinValue;
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var now = DateTimeOffset.UtcNow;
                if (now >= nextRefresh)
                {
                    DisposeEndpoints(endpoints);
                    endpoints = DiscoverEndpoints(enumerator);
                    nextRefresh = now.AddSeconds(10);
                    nextSessionRefresh = DateTimeOffset.MinValue;
                }

                if (now >= nextSessionRefresh)
                {
                    processChannels = DiscoverProcessChannels(endpoints);
                    nextSessionRefresh = now.AddSeconds(1);
                }

                var peaks = new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);
                foreach (var endpoint in endpoints)
                {
                    try
                    {
                        var peak = endpoint.Device.AudioMeterInformation.MasterPeakValue;
                        peaks[endpoint.Channel] = Math.Max(peaks.GetValueOrDefault(endpoint.Channel), peak);
                        if (peak >= _threshold)
                            lastActive[endpoint.Channel] = now;
                    }
                    catch
                    {
                        nextRefresh = DateTimeOffset.MinValue;
                    }
                }

                var active = lastActive
                    .Where(pair => now - pair.Value <= _holdTime)
                    .Select(pair => pair.Key)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
                SnapshotUpdated?.Invoke(new WaveActivitySnapshot(peaks, active, processChannels));
                await Task.Delay(100, cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        finally
        {
            DisposeEndpoints(endpoints);
        }
    }

    private List<(string Channel, MMDevice Device)> DiscoverEndpoints(MMDeviceEnumerator enumerator)
    {
        var result = new List<(string Channel, MMDevice Device)>();
        foreach (var device in enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
        {
            var channel = _channelNames.FirstOrDefault(name => Matches(device.FriendlyName, name));
            if (channel is null)
            {
                device.Dispose();
                continue;
            }
            result.Add((channel, device));
        }
        return result;
    }

    private static bool Matches(string endpointName, string channelName) =>
        endpointName.Equals(channelName, StringComparison.OrdinalIgnoreCase) ||
        endpointName.StartsWith(channelName + " (", StringComparison.OrdinalIgnoreCase) ||
        endpointName.StartsWith(channelName + " - ", StringComparison.OrdinalIgnoreCase);

    private static IReadOnlyDictionary<int, IReadOnlySet<string>> DiscoverProcessChannels(
        IEnumerable<(string Channel, MMDevice Device)> endpoints)
    {
        var result = new Dictionary<int, HashSet<string>>();
        foreach (var endpoint in endpoints)
        {
            try
            {
                var manager = endpoint.Device.AudioSessionManager;
                manager.RefreshSessions();
                for (var index = 0; index < manager.Sessions.Count; index++)
                {
                    var processId = manager.Sessions[index].GetProcessID;
                    if (processId == 0 || processId > int.MaxValue)
                        continue;
                    if (!result.TryGetValue((int)processId, out var channels))
                    {
                        channels = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                        result[(int)processId] = channels;
                    }
                    channels.Add(endpoint.Channel);
                }
            }
            catch
            {
                // Audio sessions can disappear while Windows is enumerating them.
            }
        }

        return result.ToDictionary(
            pair => pair.Key,
            pair => (IReadOnlySet<string>)pair.Value);
    }

    private static void DisposeEndpoints(IEnumerable<(string Channel, MMDevice Device)> endpoints)
    {
        foreach (var endpoint in endpoints)
            endpoint.Device.Dispose();
    }

    public async ValueTask DisposeAsync()
    {
        _lifetime.Cancel();
        if (_worker is not null)
        {
            try { await _worker; } catch { }
        }
        _lifetime.Dispose();
    }
}
