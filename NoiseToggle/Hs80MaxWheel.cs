using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using NAudio.CoreAudioApi;

namespace NoiseToggle;

internal sealed class Hs80MaxWheel : NativeWindow, IDisposable
{
    private const string DevicePathPrefix = @"\\?\HID#VID_1B1C&PID_2A0F&MI_03&Col01#";
    private const int WmInput = 0x00FF;
    private const int WmRestoreVolume = 0x8001;
    private const uint RidInput = 0x10000003;
    private const uint RidiDeviceName = 0x20000007;
    private const int RestoreDelayMilliseconds = 45;
    private const int BurstMilliseconds = 350;
    private const int ReportLeadMilliseconds = 100;
    private readonly Action<MediaWheelAction> _onTurn;
    private readonly Func<bool> _capture;
    private readonly MMDeviceEnumerator _devices = new();
    private readonly System.Windows.Forms.Timer _refresh = new() { Interval = 5000 };
    private readonly System.Windows.Forms.Timer _restore = new() { Interval = RestoreDelayMilliseconds };
    private readonly object _volumeLock = new();
    private readonly WindowsVolumeFlyout _flyout = new();
    private MMDevice? _endpoint;
    private AudioEndpointVolume? _volume;
    private Guid _restoreContext;
    private float _lastVolume;
    private float _volumeBeforeHardwareChange;
    private int _lastHardwareDirection;
    private long _lastHardwareChange;
    private float _burstVolume;
    private long _lastTurn;
    private int _restorePosted;

    public Hs80MaxWheel(Action<MediaWheelAction> onTurn, Func<bool> capture)
    {
        _onTurn = onTurn;
        _capture = capture;
        try
        {
            CreateHandle(new CreateParams { Parent = new IntPtr(-3) }); // HWND_MESSAGE
            Register(0x00000100, Handle); // RIDEV_INPUTSINK
            _restore.Tick += (_, _) =>
            {
                _restore.Stop();
                RestoreVolume();
            };
            _refresh.Tick += (_, _) =>
            {
                RefreshEndpoint();
                UpdateFlyout();
            };
            RefreshEndpoint();
            _refresh.Start();
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    protected override void WndProc(ref Message message)
    {
        if (message.Msg == WmInput)
            ReadInput(message.LParam);
        else if (message.Msg == WmRestoreVolume)
        {
            Interlocked.Exchange(ref _restorePosted, 0);
            RestoreVolume();
        }
        base.WndProc(ref message);
    }

    private void ReadInput(IntPtr input)
    {
        var headerSize = (uint)(8 + 2 * IntPtr.Size);
        uint size = 0;
        if (GetRawInputData(input, RidInput, null, ref size, headerSize) != 0 ||
            size < headerSize + 10)
            return;
        var data = new byte[size];
        if (GetRawInputData(input, RidInput, data, ref size, headerSize) != size ||
            BitConverter.ToUInt32(data, 0) != 2) // RIM_TYPEHID
            return;

        var device = IntPtr.Size == 8
            ? new IntPtr(BitConverter.ToInt64(data, 8))
            : new IntPtr(BitConverter.ToInt32(data, 8));
        uint nameLength = 512;
        var name = new StringBuilder((int)nameLength);
        if (GetRawInputDeviceInfo(device, RidiDeviceName, name, ref nameLength) == uint.MaxValue ||
            !name.ToString().StartsWith(DevicePathPrefix, StringComparison.OrdinalIgnoreCase))
            return;

        var reportSize = BitConverter.ToUInt32(data, (int)headerSize);
        var reportCount = BitConverter.ToUInt32(data, (int)headerSize + 4);
        var offset = (int)headerSize + 8;
        if (reportSize != 2 || reportCount > (size - offset) / 2)
            return;
        for (var index = 0; index < reportCount; index++, offset += 2)
        {
            // The USB receiver sends report 0x0C with a volume up/down bit, then a release.
            if (data[offset] != 0x0C || data[offset + 1] is not (1 or 2))
                continue;
            var direction = data[offset + 1] == 1 ? 1 : -1;
            if (_capture())
            {
                UpdateFlyout();
                if (_volume is not null)
                    TrackTurn(direction);
            }
            _onTurn(direction > 0 ? MediaWheelAction.Clockwise : MediaWheelAction.CounterClockwise);
        }
    }

    public void UpdateFlyout() => _flyout.SetSuppressed(_volume is not null && _capture());

    private void TrackTurn(int direction)
    {
        bool hardwareStepAlreadyApplied;
        lock (_volumeLock)
        {
            var now = Environment.TickCount64;
            hardwareStepAlreadyApplied = now - _lastHardwareChange <= ReportLeadMilliseconds &&
                _lastHardwareDirection == direction;
            if (_lastTurn == 0 || now - _lastTurn > BurstMilliseconds)
            {
                // Windows may apply the step before delivering WM_INPUT.
                _burstVolume = hardwareStepAlreadyApplied ? _volumeBeforeHardwareChange : _lastVolume;
            }
            _lastTurn = now;
        }
        if (hardwareStepAlreadyApplied)
            PostRestore();
        _restore.Start();
    }

    private void OnVolumeNotification(AudioVolumeNotificationData data)
    {
        var restore = false;
        lock (_volumeLock)
        {
            if (data.EventContext == Guid.Empty && data.MasterVolume != _lastVolume)
            {
                _volumeBeforeHardwareChange = _lastVolume;
                _lastHardwareChange = Environment.TickCount64;
                _lastHardwareDirection = Math.Sign(data.MasterVolume - _lastVolume);
                restore = _lastTurn != 0 && _lastHardwareChange - _lastTurn <= BurstMilliseconds;
            }
            else if (data.EventContext != _restoreContext && data.MasterVolume != _lastVolume)
            {
                // Honor deliberate endpoint volume changes made by another app.
                _burstVolume = data.MasterVolume;
                _lastHardwareChange = 0;
            }
            _lastVolume = data.MasterVolume;
        }
        if (restore)
            PostRestore();
    }

    private void PostRestore()
    {
        if (Interlocked.Exchange(ref _restorePosted, 1) == 0 &&
            !PostMessage(Handle, WmRestoreVolume, IntPtr.Zero, IntPtr.Zero))
            Interlocked.Exchange(ref _restorePosted, 0);
    }

    private void RestoreVolume()
    {
        if (_volume is null || !_capture())
            return;

        float target;
        lock (_volumeLock)
        {
            if (_lastTurn == 0)
                return;
            target = _burstVolume;
        }
        try
        {
            if (Math.Abs(_volume.MasterVolumeLevelScalar - target) > 0.0001f)
                _volume.MasterVolumeLevelScalar = target;
        }
        catch (COMException)
        {
            // The headset can disconnect between a report and its volume notification.
        }
    }

    private void RefreshEndpoint()
    {
        try
        {
            if (_endpoint?.State == DeviceState.Active)
                return;
            ReleaseEndpoint();
            foreach (var device in _devices.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
            {
                if (_endpoint is null && device.FriendlyName.Contains(
                    "CORSAIR HS80 MAX WIRELESS Gaming Headset", StringComparison.OrdinalIgnoreCase))
                    _endpoint = device;
                else
                    device.Dispose();
            }
            if (_endpoint is null)
                return;

            _volume = _endpoint.AudioEndpointVolume;
            _restoreContext = Guid.NewGuid();
            _volume.NotificationGuid = _restoreContext;
            lock (_volumeLock)
            {
                _lastVolume = _volume.MasterVolumeLevelScalar;
                _volume.OnVolumeNotification += OnVolumeNotification;
            }
        }
        catch (COMException)
        {
            ReleaseEndpoint();
        }
    }

    private void ReleaseEndpoint()
    {
        if (_volume is not null)
            _volume.OnVolumeNotification -= OnVolumeNotification;
        _volume = null;
        _endpoint?.Dispose();
        _endpoint = null;
        lock (_volumeLock)
        {
            _lastHardwareChange = 0;
            _lastTurn = 0;
        }
    }

    public void Dispose()
    {
        _flyout.Dispose();
        _refresh.Dispose();
        _restore.Dispose();
        ReleaseEndpoint();
        _devices.Dispose();
        if (Handle == IntPtr.Zero)
            return;
        try
        {
            Register(0x00000001, IntPtr.Zero); // RIDEV_REMOVE
        }
        finally
        {
            DestroyHandle();
        }
    }

    private static void Register(uint flags, IntPtr target)
    {
        var device = new RawInputDevice { Page = 0x0C, Usage = 0x01, Flags = flags, Target = target };
        if (!RegisterRawInputDevices(ref device, 1, (uint)Marshal.SizeOf<RawInputDevice>()))
            throw new Win32Exception(Marshal.GetLastWin32Error());
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RawInputDevice
    {
        public ushort Page;
        public ushort Usage;
        public uint Flags;
        public IntPtr Target;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostMessage(IntPtr window, int message, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RegisterRawInputDevices(ref RawInputDevice device, uint count, uint size);

    [DllImport("user32.dll")]
    private static extern uint GetRawInputData(IntPtr input, uint command, byte[]? data, ref uint size, uint headerSize);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern uint GetRawInputDeviceInfo(IntPtr device, uint command, StringBuilder data, ref uint size);
}
