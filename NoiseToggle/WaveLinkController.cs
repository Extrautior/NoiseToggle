using System.Threading.Channels;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace NoiseToggle;

internal sealed class WaveLinkController : IAsyncDisposable
{
    private readonly WaveLinkSettings _settings;
    private readonly WaveLinkHudForm _hud;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Channel<MediaWheelAction> _actions = Channel.CreateUnbounded<MediaWheelAction>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });
    private readonly Task _actionWorker;
    private readonly object _stateLock = new();
    private WaveLinkClient? _waveLink;
    private WaveLinkActivityMonitor? _activityMonitor;
    private MediaWheelHook? _hook;
    private Task? _stateRefreshWorker;
    private List<WaveChannel> _allChannels = [];
    private List<WaveChannel> _channels = [];
    private WaveActivitySnapshot _activity = new(
        new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase),
        new HashSet<string>(StringComparer.OrdinalIgnoreCase),
        new Dictionary<int, IReadOnlySet<string>>());
    private WaveMix? _mix;
    private int _selected;
    private bool _adjusting;
    private bool _followForeground;
    private bool _manualMode;
    private uint _automaticProcessId;
    private DateTimeOffset _lastWheelAction = DateTimeOffset.MinValue;

    public WaveLinkController(WaveLinkSettings settings, WaveLinkHudForm hud)
    {
        _settings = settings;
        _hud = hud;
        _followForeground = settings.SelectForegroundOnPress;
        _adjusting = _followForeground;
        _actionWorker = ProcessActionsAsync();
    }

    public event Action<string>? StatusChanged;

    public bool IsReady { get; private set; }

    public async Task StartAsync()
    {
        IsReady = false;
        Render("Connecting to Wave Link…");
        StatusChanged?.Invoke("Wave Link: connecting...");
        try
        {
            Exception? connectionError = null;
            foreach (var port in WaveLinkPortDiscovery.GetCandidatePorts(_settings.Port))
            {
                var candidate = new WaveLinkClient(_settings.Host, port);
                try
                {
                    await candidate.ConnectAsync(_lifetime.Token);
                    _waveLink = candidate;
                    if (port != _settings.Port)
                        AppLog.Info($"Wave Link discovered websocket port {port} (configured fallback: {_settings.Port}).");
                    break;
                }
                catch (Exception ex)
                {
                    connectionError = ex;
                    await candidate.DisposeAsync();
                }
            }
            if (_waveLink is null)
                throw connectionError ?? new InvalidOperationException("Wave Link websocket endpoint was not found.");

            var state = await _waveLink.GetStateAsync(_lifetime.Token);
            _mix = state.Mixes.FirstOrDefault(item =>
                string.Equals(item.Name, _settings.Mix, StringComparison.OrdinalIgnoreCase));
            if (_mix is null)
                throw new InvalidOperationException($"Wave Link mix '{_settings.Mix}' was not found.");

            _allChannels = _settings.Channels.Count == 0
                ? state.Channels
                : _settings.Channels
                    .Select(name => state.Channels.FirstOrDefault(channel =>
                        string.Equals(channel.Name, name, StringComparison.OrdinalIgnoreCase)))
                    .Where(channel => channel is not null)
                    .Cast<WaveChannel>()
                    .ToList();
            if (_allChannels.Count == 0)
                throw new InvalidOperationException("None of the configured Wave Link channels exist.");

            _channels = _settings.OnlyActiveChannels ? [] : [.. _allChannels];
            if (_settings.OnlyActiveChannels)
            {
                _activityMonitor = new WaveLinkActivityMonitor(
                    _allChannels.Select(item => item.Name),
                    _settings.ActivityThreshold,
                    _settings.ActiveHoldMilliseconds);
                _activityMonitor.SnapshotUpdated += OnActivitySnapshot;
                _activityMonitor.Start();
            }

            _hook = new MediaWheelHook(_settings.CaptureWheel);
            _hook.ActionReceived += action =>
            {
                _hud.ShowHud();
                _actions.Writer.TryWrite(action);
            };
            _hook.Install();
            IsReady = true;
            _stateRefreshWorker = RefreshStateLoopAsync();
            Render();
            StatusChanged?.Invoke("Wave Link wheel: ready");
            AppLog.Info($"Wave Link wheel connected to {_settings.Mix} with {_allChannels.Count} channels.");
        }
        catch (Exception ex)
        {
            IsReady = false;
            Render(ex.Message);
            _hud.ShowHud(5000);
            StatusChanged?.Invoke("Wave Link wheel: unavailable");
            AppLog.Error("Wave Link wheel failed to start.", ex);
        }
    }

    private async Task ProcessActionsAsync()
    {
        try
        {
            await foreach (var action in _actions.Reader.ReadAllAsync(_lifetime.Token))
            {
                if (_mix is null || _waveLink is null)
                    continue;

                var now = DateTimeOffset.UtcNow;
                var interactionTimeout = TimeSpan.FromMilliseconds(
                    Math.Max(900, _settings.HudAutoHideMilliseconds + 200));
                if (_settings.SelectForegroundOnPress && now - _lastWheelAction > interactionTimeout)
                {
                    lock (_stateLock)
                    {
                        _followForeground = true;
                        _manualMode = false;
                        _automaticProcessId = 0;
                        _adjusting = true;
                    }
                }
                _lastWheelAction = now;

                if (action == MediaWheelAction.Press)
                {
                    lock (_stateLock)
                    {
                        if (_channels.Count == 0)
                            ApplyActiveFilterLocked();
                        if (_channels.Count == 0)
                            continue;

                        if (_settings.SelectForegroundOnPress)
                        {
                            if (!_manualMode)
                            {
                                // Press enters browsing. The next press locks the browsed channel.
                                _manualMode = true;
                                _followForeground = false;
                                _adjusting = false;
                            }
                            else
                            {
                                _adjusting = !_adjusting;
                            }
                        }
                        else
                        {
                            _adjusting = !_adjusting;
                        }
                        ApplyActiveFilterLocked();
                    }
                    Render();
                    continue;
                }

                var direction = action == MediaWheelAction.Clockwise ? 1 : -1;
                WaveChannel? channel;

                if (_settings.SelectForegroundOnPress && !_manualMode)
                {
                    var foregroundProcessId = GetForegroundProcessId();
                    lock (_stateLock)
                    {
                        ApplyActiveFilterLocked();
                        if (_followForeground || foregroundProcessId != _automaticProcessId)
                            SelectForegroundChannelLocked(foregroundProcessId);
                        // Keep this channel stable for the rest of the interaction. After
                        // inactivity—or a real focus change—the next turn resolves it again.
                        _followForeground = false;
                        _automaticProcessId = foregroundProcessId;
                        _adjusting = true;
                    }
                }

                if (!_adjusting)
                {
                    lock (_stateLock)
                    {
                        if (_channels.Count == 0)
                            continue;
                        _selected = (_selected + direction + _channels.Count) % _channels.Count;
                    }
                    Render();
                    continue;
                }

                lock (_stateLock)
                    channel = _channels.Count > 0 ? _channels[_selected] : null;
                if (channel is null)
                    continue;
                var channelMix = channel.Mixes.FirstOrDefault(item => item.Id == _mix.Id);
                if (channelMix is null)
                    continue;

                var next = Math.Clamp(channelMix.Level + direction * (_settings.StepPercent / 100m), 0m, 1m);
                channelMix.Level = next;
                Render();
                await _waveLink.SetChannelMixLevelAsync(channel.Id, _mix.Id, next, _lifetime.Token);
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            IsReady = false;
            Render(ex.Message);
            StatusChanged?.Invoke("Wave Link wheel: error");
            AppLog.Error("Wave Link wheel action failed.", ex);
        }
    }

    private void OnActivitySnapshot(WaveActivitySnapshot snapshot)
    {
        lock (_stateLock)
        {
            _activity = snapshot;
            ApplyActiveFilterLocked();
        }
        Render();
    }

    private async Task RefreshStateLoopAsync()
    {
        try
        {
            while (!_lifetime.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(5), _lifetime.Token);
                var waveLink = _waveLink;
                if (waveLink is null)
                    continue;

                var state = await waveLink.GetStateAsync(_lifetime.Token);
                ApplyStateRefresh(state);
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            IsReady = false;
            StatusChanged?.Invoke("Wave Link wheel: reconnecting...");
            AppLog.Error("Wave Link state refresh failed; the connection will be restarted.", ex);
        }
    }

    private void ApplyStateRefresh(WaveLinkState state)
    {
        var refreshedMix = state.Mixes.FirstOrDefault(item =>
            string.Equals(item.Name, _settings.Mix, StringComparison.OrdinalIgnoreCase));
        if (refreshedMix is null)
            throw new InvalidOperationException($"Wave Link mix '{_settings.Mix}' was not found during refresh.");

        var refreshedChannels = ResolveConfiguredChannels(state.Channels);
        if (refreshedChannels.Count == 0)
            return;

        bool changed;
        lock (_stateLock)
        {
            var currentId = _channels.Count > 0 && _selected < _channels.Count
                ? _channels[_selected].Id
                : null;
            changed = !_allChannels.Select(ChannelIdentity).SequenceEqual(
                refreshedChannels.Select(ChannelIdentity), StringComparer.OrdinalIgnoreCase);

            _mix = refreshedMix;
            _allChannels = refreshedChannels;
            if (_settings.OnlyActiveChannels)
            {
                ApplyActiveFilterLocked();
            }
            else
            {
                _channels = [.. _allChannels];
                var preserved = currentId is null
                    ? -1
                    : _channels.FindIndex(channel => channel.Id == currentId);
                _selected = preserved >= 0 ? preserved : 0;
            }
        }

        _activityMonitor?.UpdateChannels(refreshedChannels.Select(channel => channel.Name));
        if (changed)
            AppLog.Info($"Wave Link channel list refreshed: {refreshedChannels.Count} channels.");
        Render();
    }

    private List<WaveChannel> ResolveConfiguredChannels(IEnumerable<WaveChannel> channels)
    {
        var available = channels.ToList();
        return _settings.Channels.Count == 0
            ? available
            : _settings.Channels
                .Select(name => available.FirstOrDefault(channel =>
                    string.Equals(channel.Name, name, StringComparison.OrdinalIgnoreCase)))
                .Where(channel => channel is not null)
                .Cast<WaveChannel>()
                .ToList();
    }

    private static string ChannelIdentity(WaveChannel channel) => $"{channel.Id}\n{channel.Name}";

    private void ApplyActiveFilterLocked()
    {
        if (!_settings.OnlyActiveChannels)
            return;
        var current = _channels.Count > 0 && _selected < _channels.Count ? _channels[_selected] : null;
        var filtered = _allChannels.Where(channel => _activity.ActiveNames.Contains(channel.Name)).ToList();
        if (_adjusting && current is not null && filtered.All(item => item.Id != current.Id))
            filtered.Insert(0, current);
        _channels = filtered;
        if (current is null || _channels.Count == 0)
            _selected = 0;
        else
        {
            var preserved = _channels.FindIndex(item => item.Id == current.Id);
            _selected = preserved >= 0 ? preserved : 0;
        }
    }

    private static uint GetForegroundProcessId()
    {
        var foreground = GetForegroundWindow();
        if (foreground == IntPtr.Zero)
            return 0;
        _ = GetWindowThreadProcessId(foreground, out var processId);
        return processId;
    }

    private void SelectForegroundChannelLocked(uint processId)
    {
        if (processId == 0)
            return;

        string processName;
        string windowTitle;
        try
        {
            using var process = Process.GetProcessById((int)processId);
            processName = process.ProcessName;
            windowTitle = process.MainWindowTitle;
        }
        catch
        {
            return;
        }

        var relatedProcesses = GetProcessTree(processId);
        var sessionChannelNames = relatedProcesses
            .SelectMany(id => _activity.ProcessChannels.TryGetValue(id, out var names)
                ? names.AsEnumerable()
                : Enumerable.Empty<string>())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var sessionMatch = _allChannels
            .Where(channel => sessionChannelNames.Contains(channel.Name))
            .OrderByDescending(channel => _activity.ActiveNames.Contains(channel.Name))
            .ThenByDescending(channel => _activity.Peaks.GetValueOrDefault(channel.Name))
            .FirstOrDefault();
        if (sessionMatch is not null)
        {
            SelectChannelLocked(sessionMatch, $"audio session for {processName}");
            return;
        }

        var processKey = NormalizeAppName(processName);
        var titleKey = NormalizeAppName(windowTitle);
        var best = _allChannels
            .Select(channel => (Channel: channel, Score: ForegroundMatchScore(channel.Name, processKey, titleKey)))
            .Where(item => item.Score > 0)
            .OrderByDescending(item => item.Score)
            .ThenByDescending(item => _activity.Peaks.GetValueOrDefault(item.Channel.Name))
            .FirstOrDefault();
        if (best.Channel is null)
            return;

        SelectChannelLocked(best.Channel, $"name match for {processName}");
    }

    private void SelectChannelLocked(WaveChannel selected, string reason)
    {
        var previous = _channels.Count > 0 ? _channels[_selected] : null;
        var index = _channels.FindIndex(channel => channel.Id == selected.Id);
        if (index < 0)
        {
            _channels.Insert(0, selected);
            index = 0;
        }
        _selected = index;
        if (previous?.Id != selected.Id)
            AppLog.Info($"Wave Link focused selection: {selected.Name} ({reason}).");
    }

    private static HashSet<int> GetProcessTree(uint rootProcessId)
    {
        var result = new HashSet<int> { (int)rootProcessId };
        var snapshot = CreateToolhelp32Snapshot(0x00000002, 0);
        if (snapshot == IntPtr.Zero || snapshot == new IntPtr(-1))
            return result;

        try
        {
            var entries = new List<(int ProcessId, int ParentId)>();
            var entry = new ProcessEntry32 { Size = (uint)Marshal.SizeOf<ProcessEntry32>() };
            if (Process32First(snapshot, ref entry))
            {
                do
                {
                    entries.Add(((int)entry.ProcessId, (int)entry.ParentProcessId));
                    entry.Size = (uint)Marshal.SizeOf<ProcessEntry32>();
                } while (Process32Next(snapshot, ref entry));
            }

            var added = true;
            while (added)
            {
                added = false;
                foreach (var candidate in entries)
                {
                    if (result.Contains(candidate.ParentId) && result.Add(candidate.ProcessId))
                        added = true;
                }
            }
        }
        finally
        {
            CloseHandle(snapshot);
        }
        return result;
    }

    private static int ForegroundMatchScore(string channelName, string processKey, string titleKey)
    {
        var channelKey = NormalizeAppName(channelName);
        if (channelKey.Length == 0)
            return 0;
        if (channelKey.Equals(processKey, StringComparison.OrdinalIgnoreCase))
            return 100;
        if (processKey.Length >= 4 &&
            (channelKey.Contains(processKey, StringComparison.OrdinalIgnoreCase) ||
             processKey.Contains(channelKey, StringComparison.OrdinalIgnoreCase)))
            return 80;
        if (channelKey.Length >= 4 && titleKey.Contains(channelKey, StringComparison.OrdinalIgnoreCase))
            return 60;
        return 0;
    }

    private static string NormalizeAppName(string value) =>
        new(value.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

    private void Render(string? status = null)
    {
        WaveChannel? channel;
        decimal level;
        float peak;
        int selected;
        int total;
        int activeCount;
        lock (_stateLock)
        {
            channel = _channels.Count > 0 ? _channels[_selected] : null;
            level = channel?.Mixes.FirstOrDefault(item => item.Id == _mix?.Id)?.Level ?? 0m;
            peak = channel is null ? 0f : _activity.Peaks.GetValueOrDefault(channel.Name);
            selected = _selected;
            total = _channels.Count;
            activeCount = _activity.ActiveNames.Count;
        }
        if (status is null && _settings.OnlyActiveChannels && channel is null)
            status = "No channel is producing audio";
        _hud.UpdateHud(new WaveLinkHudModel(
            channel?.Name ?? "Listening…", level, peak, _adjusting,
            selected, total, activeCount, _mix?.Name ?? _settings.Mix, status));
    }

    public async ValueTask DisposeAsync()
    {
        IsReady = false;
        _lifetime.Cancel();
        _actions.Writer.TryComplete();
        _hook?.Dispose();
        try { await _actionWorker; } catch { }
        if (_stateRefreshWorker is not null)
        {
            try { await _stateRefreshWorker; } catch { }
        }
        if (_activityMonitor is not null)
            await _activityMonitor.DisposeAsync();
        if (_waveLink is not null)
            await _waveLink.DisposeAsync();
        _lifetime.Dispose();
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint processId);

    [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern bool Process32First(IntPtr snapshot, ref ProcessEntry32 entry);

    [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern bool Process32Next(IntPtr snapshot, ref ProcessEntry32 entry);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct ProcessEntry32
    {
        public uint Size;
        public uint Usage;
        public uint ProcessId;
        public IntPtr DefaultHeapId;
        public uint ModuleId;
        public uint Threads;
        public uint ParentProcessId;
        public int BasePriority;
        public uint Flags;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string ExecutableFile;
    }
}
