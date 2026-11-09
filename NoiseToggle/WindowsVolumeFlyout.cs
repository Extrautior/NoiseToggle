using System.Runtime.InteropServices;

namespace NoiseToggle;

internal sealed class WindowsVolumeFlyout : IDisposable
{
    private const int ExtendedStyle = -20;
    private const int LayeredStyle = 0x00080000;
    private const uint UseAlpha = 2;
    private const string OwnerProperty = "NoiseToggle.Hs80MaxFlyout";
    private IntPtr _hiddenWindow;

    public void SetSuppressed(bool suppress)
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000))
            return;
        if (!suppress)
        {
            Restore();
            return;
        }
        if (_hiddenWindow != IntPtr.Zero && IsWindow(_hiddenWindow))
            return;

        _hiddenWindow = IntPtr.Zero;
        var window = FindFlyout();
        if (window == IntPtr.Zero)
            return;

        if (GetProp(window, OwnerProperty) != IntPtr.Zero)
        {
            _hiddenWindow = window;
            return;
        }

        var style = GetWindowLong(window, ExtendedStyle);
        if ((style & LayeredStyle) != 0 || !SetProp(window, OwnerProperty, new IntPtr(1)))
            return;

        SetWindowLong(window, ExtendedStyle, style | LayeredStyle);
        if (SetLayeredWindowAttributes(window, 0, 0, UseAlpha))
            _hiddenWindow = window;
        else
        {
            SetWindowLong(window, ExtendedStyle, style);
            RemoveProp(window, OwnerProperty);
        }
    }

    private static IntPtr FindFlyout()
    {
        GetWindowThreadProcessId(GetShellWindow(), out var shellProcess);
        if (shellProcess == 0)
            return IntPtr.Zero;

        var match = IntPtr.Zero;
        var window = IntPtr.Zero;
        while ((window = FindWindowEx(IntPtr.Zero, window, "XamlExplorerHostIslandWindow", "")) != IntPtr.Zero)
        {
            GetWindowThreadProcessId(window, out var process);
            if (process != shellProcess || !GetWindowRect(window, out var rect))
                continue;

            // Explorer also uses this host class for fullscreen surfaces. Only match
            // a small horizontal flyout with the expected XAML content window.
            var scale = Math.Max(96u, GetDpiForWindow(window)) / 96.0;
            var width = (rect.Right - rect.Left) / scale;
            var height = (rect.Bottom - rect.Top) / scale;
            if (width is < 120 or > 360 || height is < 32 or > 100 || width < height * 2)
                continue;
            if (FindWindowEx(window, IntPtr.Zero, "Windows.UI.Composition.DesktopWindowContentBridge",
                "DesktopWindowXamlSource") == IntPtr.Zero)
                continue;
            if (match != IntPtr.Zero)
                return IntPtr.Zero;
            match = window;
        }
        return match;
    }

    private void Restore()
    {
        if (_hiddenWindow != IntPtr.Zero && RemoveProp(_hiddenWindow, OwnerProperty) != IntPtr.Zero)
        {
            var style = GetWindowLong(_hiddenWindow, ExtendedStyle);
            SetWindowLong(_hiddenWindow, ExtendedStyle, style & ~LayeredStyle);
        }
        _hiddenWindow = IntPtr.Zero;
    }

    public void Dispose() => Restore();

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetShellWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string windowClass, string title);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindow(IntPtr window);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr window, out Rect rect);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr window);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetProp(IntPtr window, string name);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetProp(IntPtr window, string name, IntPtr value);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr RemoveProp(IntPtr window, string name);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowLong(IntPtr window, int index);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int SetWindowLong(IntPtr window, int index, int value);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetLayeredWindowAttributes(IntPtr window, uint colorKey, byte alpha, uint flags);
}
