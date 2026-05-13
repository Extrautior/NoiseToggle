using System.ComponentModel;
using System.Runtime.InteropServices;

namespace NoiseToggle;

internal sealed class GlobalHotkey : NativeWindow, IDisposable
{
    private const int HotkeyId = 0x4E54;
    private const int WmHotkey = 0x0312;
    private bool _disposed;

    public event EventHandler? Pressed;

    public GlobalHotkey()
    {
        CreateHandle(new CreateParams());
    }

    public void Register(HotkeyDefinition hotkey)
    {
        Unregister();
        if (!RegisterHotKey(Handle, HotkeyId, hotkey.Modifiers, hotkey.Key))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), $"Could not register hotkey {hotkey.DisplayText}.");
        }
    }

    public void Unregister()
    {
        if (Handle != IntPtr.Zero)
        {
            UnregisterHotKey(Handle, HotkeyId);
        }
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WmHotkey)
        {
            Pressed?.Invoke(this, EventArgs.Empty);
            return;
        }

        base.WndProc(ref m);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        Unregister();
        DestroyHandle();
        _disposed = true;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);
}

internal readonly record struct HotkeyDefinition(uint Modifiers, uint Key, string DisplayText)
{
    private const uint ModAlt = 0x0001;
    private const uint ModControl = 0x0002;
    private const uint ModShift = 0x0004;
    private const uint ModWin = 0x0008;
    private const uint ModNoRepeat = 0x4000;

    public static HotkeyDefinition Default => new(ModControl | ModAlt | ModNoRepeat, (uint)Keys.K, "Ctrl+Alt+K");

    public static HotkeyDefinition FromKeyEvent(KeyEventArgs e)
    {
        var modifiers = ModNoRepeat;
        var parts = new List<string>();
        if (e.Control)
        {
            modifiers |= ModControl;
            parts.Add("Ctrl");
        }

        if (e.Alt)
        {
            modifiers |= ModAlt;
            parts.Add("Alt");
        }

        if (e.Shift)
        {
            modifiers |= ModShift;
            parts.Add("Shift");
        }

        if ((e.Modifiers & Keys.LWin) == Keys.LWin || (e.Modifiers & Keys.RWin) == Keys.RWin)
        {
            modifiers |= ModWin;
            parts.Add("Win");
        }

        var key = e.KeyCode;
        if (key is Keys.ControlKey or Keys.Menu or Keys.ShiftKey or Keys.LWin or Keys.RWin)
        {
            throw new InvalidOperationException("Press a normal key.");
        }

        parts.Add(key.ToString());
        return new HotkeyDefinition(modifiers, (uint)key, string.Join("+", parts));
    }

    public static HotkeyDefinition Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return Default;
        }

        var modifiers = ModNoRepeat;
        Keys? key = null;
        foreach (var rawPart in text.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            if (rawPart.Equals("Ctrl", StringComparison.OrdinalIgnoreCase) ||
                rawPart.Equals("Control", StringComparison.OrdinalIgnoreCase))
            {
                modifiers |= ModControl;
            }
            else if (rawPart.Equals("Alt", StringComparison.OrdinalIgnoreCase))
            {
                modifiers |= ModAlt;
            }
            else if (rawPart.Equals("Shift", StringComparison.OrdinalIgnoreCase))
            {
                modifiers |= ModShift;
            }
            else if (rawPart.Equals("Win", StringComparison.OrdinalIgnoreCase) ||
                     rawPart.Equals("Windows", StringComparison.OrdinalIgnoreCase))
            {
                modifiers |= ModWin;
            }
            else if (Enum.TryParse<Keys>(rawPart, true, out var parsedKey))
            {
                key = parsedKey;
            }
        }

        return key is null ? Default : new HotkeyDefinition(modifiers, (uint)key.Value, text);
    }
}
