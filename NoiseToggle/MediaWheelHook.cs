using System.Diagnostics;
using System.Runtime.InteropServices;

namespace NoiseToggle;

internal enum MediaWheelAction
{
    CounterClockwise,
    Clockwise,
    Press
}

internal sealed class MediaWheelHook : IDisposable
{
    private const int WhKeyboardLl = 13;
    private const int WmKeyDown = 0x0100;
    private const int WmKeyUp = 0x0101;
    private const int WmSysKeyDown = 0x0104;
    private const int WmSysKeyUp = 0x0105;
    private const int VkVolumeMute = 0xAD;
    private const int VkVolumeDown = 0xAE;
    private const int VkVolumeUp = 0xAF;
    private readonly HookProc _callback;
    private IntPtr _hook;

    public MediaWheelHook(bool capture)
    {
        Capture = capture;
        _callback = HookCallback;
    }

    public bool Capture { get; set; }
    public event Action<MediaWheelAction>? ActionReceived;

    public void Install()
    {
        using var process = Process.GetCurrentProcess();
        using var module = process.MainModule;
        _hook = SetWindowsHookEx(WhKeyboardLl, _callback, GetModuleHandle(module?.ModuleName), 0);
        if (_hook == IntPtr.Zero)
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
    }

    private IntPtr HookCallback(int code, IntPtr messagePointer, IntPtr dataPointer)
    {
        if (code >= 0)
        {
            var message = messagePointer.ToInt32();
            var keyboard = Marshal.PtrToStructure<KbdLlHookStruct>(dataPointer);
            var action = keyboard.VkCode switch
            {
                VkVolumeDown => MediaWheelAction.CounterClockwise,
                VkVolumeUp => MediaWheelAction.Clockwise,
                VkVolumeMute => MediaWheelAction.Press,
                _ => (MediaWheelAction?)null
            };
            if (action is not null)
            {
                if (message is WmKeyDown or WmSysKeyDown)
                    ActionReceived?.Invoke(action.Value);
                if (Capture && message is WmKeyDown or WmKeyUp or WmSysKeyDown or WmSysKeyUp)
                    return new IntPtr(1);
            }
        }
        return CallNextHookEx(_hook, code, messagePointer, dataPointer);
    }

    public void Dispose()
    {
        if (_hook == IntPtr.Zero)
            return;
        UnhookWindowsHookEx(_hook);
        _hook = IntPtr.Zero;
    }

    private delegate IntPtr HookProc(int code, IntPtr messagePointer, IntPtr dataPointer);

    [StructLayout(LayoutKind.Sequential)]
    private struct KbdLlHookStruct
    {
        public int VkCode;
        public int ScanCode;
        public int Flags;
        public int Time;
        public IntPtr ExtraInfo;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, HookProc callback, IntPtr module, uint threadId);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWindowsHookEx(IntPtr hook);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr messagePointer, IntPtr dataPointer);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr GetModuleHandle(string? moduleName);
}
