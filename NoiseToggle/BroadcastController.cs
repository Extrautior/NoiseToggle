using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.UIA3;

namespace NoiseToggle;

internal sealed class BroadcastController
{
    private readonly BroadcastBridgeClient _bridge;

    private static readonly string BroadcastExe =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "NVIDIA Corporation", "NVIDIA Broadcast", "NVIDIA Broadcast.exe");

    private static readonly string BroadcastSettingsPath =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "nvidia-broadcast", "AppSetting.json");

    public BroadcastController(AppSettings settings)
    {
        _bridge = new BroadcastBridgeClient(settings);
    }

    public async Task SetNoiseRemovalAsync(bool enabled, CancellationToken cancellationToken)
    {
        if (await _bridge.TrySetNoiseRemovalAsync(enabled, cancellationToken))
        {
            SaveDesiredState(enabled);
            return;
        }

        await SetNoiseRemovalWithUiAsync(enabled, cancellationToken);
    }

    public async Task<bool?> GetNoiseRemovalStateAsync(CancellationToken cancellationToken)
    {
        var bridgeState = await _bridge.TryGetNoiseRemovalStateAsync(cancellationToken);
        if (bridgeState is not null)
        {
            return bridgeState;
        }

        return ReadPersistedState();
    }

    private Task SetNoiseRemovalWithUiAsync(bool enabled, CancellationToken cancellationToken)
    {
        return Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            SaveDesiredState(enabled);

            using var automation = new UIA3Automation();
            var windowResult = GetOrStartBroadcastWindow(automation, cancellationToken);

            if (windowResult is null)
            {
                throw new InvalidOperationException("NVIDIA Broadcast window could not be opened. The desired config value was saved, but the live Broadcast effect could not be verified.");
            }

            var window = windowResult.Window;
            var windowHandle = new IntPtr(window.Properties.NativeWindowHandle.ValueOrDefault);

            try
            {
                SetForegroundWindow(windowHandle);
                Thread.Sleep(500);
                EnsureAudioPage(window);

                var toggle = FindNoiseToggle(window);
                if (toggle is null)
                {
                    throw new InvalidOperationException("Could not find the NVIDIA Broadcast microphone noise-removal control. The desired config value was saved, but the live Broadcast effect could not be verified.");
                }

                ToggleElementToState(toggle, enabled);
                AppLog.Info($"NVIDIA Broadcast microphone noise removal set to {(enabled ? "on" : "off")}.");
            }
            finally
            {
                if (windowResult.OpenedByNoiseToggle)
                {
                    HideWindow(windowHandle);
                }
            }
        }, cancellationToken);
    }

    private static bool? ReadPersistedState()
    {
        if (!File.Exists(BroadcastSettingsPath))
        {
            return null;
        }

        try
        {
            var json = JsonNode.Parse(File.ReadAllText(BroadcastSettingsPath))?.AsObject();
            var enabled = json?["AppStorage"]?["MaxineEffects"]?["MicrophoneEffects"]?["microphoneNoiseRemoval"]?["enabled"];
            return enabled?.GetValue<bool>();
        }
        catch (Exception ex)
        {
            AppLog.Error("Could not read NVIDIA Broadcast settings file.", ex);
            return null;
        }
    }

    private static bool SaveDesiredState(bool enabled)
    {
        if (!File.Exists(BroadcastSettingsPath))
        {
            AppLog.Info($"NVIDIA Broadcast settings file not found at {BroadcastSettingsPath}.");
            return false;
        }

        try
        {
            var json = JsonNode.Parse(File.ReadAllText(BroadcastSettingsPath))?.AsObject();
            if (json is null)
            {
                AppLog.Info("NVIDIA Broadcast settings file was empty or invalid.");
                return false;
            }

            var appStorage = EnsureObject(json, "AppStorage");
            var maxineEffects = EnsureObject(appStorage, "MaxineEffects");
            var microphoneEffects = EnsureObject(maxineEffects, "MicrophoneEffects");
            var noiseRemoval = EnsureObject(microphoneEffects, "microphoneNoiseRemoval");
            noiseRemoval["enabled"] = enabled;

            File.WriteAllText(BroadcastSettingsPath, json.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            AppLog.Info($"Saved NVIDIA Broadcast config microphoneNoiseRemoval.enabled={enabled}.");
            return true;
        }
        catch (Exception ex)
        {
            AppLog.Error("Could not update NVIDIA Broadcast settings file.", ex);
            return false;
        }
    }

    private static JsonObject EnsureObject(JsonObject parent, string propertyName)
    {
        if (parent[propertyName] is JsonObject existing)
        {
            return existing;
        }

        var created = new JsonObject();
        parent[propertyName] = created;
        return created;
    }

    private static void ToggleElementToState(AutomationElement toggle, bool enabled)
    {
        var togglePattern = toggle.Patterns.Toggle.PatternOrDefault;
        if (togglePattern is not null)
        {
            var isOn = togglePattern.ToggleState.ValueOrDefault == ToggleState.On;
            if (isOn != enabled)
            {
                togglePattern.Toggle();
                Thread.Sleep(600);
            }

            var isNowOn = togglePattern.ToggleState.ValueOrDefault == ToggleState.On;
            if (isNowOn == enabled)
            {
                return;
            }
        }

        var invokePattern = toggle.Patterns.Invoke.PatternOrDefault;
        if (invokePattern is not null)
        {
            invokePattern.Invoke();
            Thread.Sleep(600);
            togglePattern = toggle.Patterns.Toggle.PatternOrDefault;
            if (togglePattern is not null && (togglePattern.ToggleState.ValueOrDefault == ToggleState.On) == enabled)
            {
                return;
            }
        }

        ClickElementCenter(toggle);
        Thread.Sleep(600);

        togglePattern = toggle.Patterns.Toggle.PatternOrDefault;
        if (togglePattern is null)
        {
            throw new InvalidOperationException("Found the NVIDIA Broadcast noise-removal control, but Windows UI Automation could not verify its live state.");
        }

        if ((togglePattern.ToggleState.ValueOrDefault == ToggleState.On) != enabled)
        {
            throw new InvalidOperationException("Found the NVIDIA Broadcast noise-removal control, but Windows UI Automation could not change its state.");
        }
    }

    private static BroadcastWindowResult? GetOrStartBroadcastWindow(UIA3Automation automation, CancellationToken cancellationToken)
    {
        var window = FindBroadcastWindow(automation);
        if (window is not null)
        {
            return new BroadcastWindowResult(window, OpenedByNoiseToggle: false);
        }

        if (!File.Exists(BroadcastExe))
        {
            return null;
        }

        Process.Start(new ProcessStartInfo(BroadcastExe) { UseShellExecute = true });
        for (var i = 0; i < 10; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Thread.Sleep(500);
            window = FindBroadcastWindow(automation);
            if (window is not null)
            {
                return new BroadcastWindowResult(window, OpenedByNoiseToggle: true);
            }
        }

        return null;
    }

    private static Window? FindBroadcastWindow(UIA3Automation automation)
    {
        return automation.GetDesktop()
            .FindAllChildren(cf => cf.ByControlType(ControlType.Window))
            .Select(e => e.AsWindow())
            .FirstOrDefault(w => SafeName(w).Contains("NVIDIA Broadcast", StringComparison.OrdinalIgnoreCase));
    }

    private static void EnsureAudioPage(Window root)
    {
        try
        {
            var audioButton = root.FindAllDescendants()
                .FirstOrDefault(e => SafeName(e).Equals("Audio", StringComparison.OrdinalIgnoreCase));
            audioButton?.AsButton().Invoke();
            Thread.Sleep(300);
        }
        catch
        {
            // If the Audio item cannot be clicked, the current page may already contain the microphone controls.
        }
    }

    private static AutomationElement? FindNoiseToggle(Window root)
    {
        var microphoneToggle = root.FindFirstDescendant(cf => cf.ByAutomationId("microphoneNoiseRemoval"));
        if (microphoneToggle is not null)
        {
            return microphoneToggle;
        }

        var candidates = root.FindAllDescendants()
            .Where(e =>
            {
                var type = e.Properties.ControlType.ValueOrDefault;
                if (type != ControlType.CheckBox)
                {
                    return false;
                }

                var name = SafeName(e);
                return name.Contains("noise", StringComparison.OrdinalIgnoreCase) ||
                       name.Contains("background", StringComparison.OrdinalIgnoreCase) ||
                       name.Contains("removal", StringComparison.OrdinalIgnoreCase);
            })
            .ToList();

        return candidates.FirstOrDefault(e => (e.Properties.AutomationId.ValueOrDefault ?? "").Contains("microphone", StringComparison.OrdinalIgnoreCase)) ??
               candidates.FirstOrDefault(e => SafeName(e).Contains("micro", StringComparison.OrdinalIgnoreCase)) ??
               candidates.FirstOrDefault();
    }

    private static string SafeName(AutomationElement element)
    {
        try
        {
            return element.Properties.Name.ValueOrDefault ?? string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    private static void ClickElementCenter(AutomationElement element)
    {
        var rectangle = element.BoundingRectangle;
        var x = (int)(rectangle.Left + rectangle.Width / 2);
        var y = (int)(rectangle.Top + rectangle.Height / 2);

        if (x <= 0 || y <= 0)
        {
            throw new InvalidOperationException("The NVIDIA Broadcast noise-removal control was found, but its screen position was invalid.");
        }

        SetCursorPos(x, y);
        mouse_event(MouseEventLeftDown, 0, 0, 0, UIntPtr.Zero);
        mouse_event(MouseEventLeftUp, 0, 0, 0, UIntPtr.Zero);
    }

    private static void HideWindow(IntPtr windowHandle)
    {
        if (windowHandle == IntPtr.Zero)
        {
            return;
        }

        ShowWindow(windowHandle, ShowWindowHide);
        AppLog.Info("Hid NVIDIA Broadcast window after toggle.");
    }

    private sealed record BroadcastWindowResult(Window Window, bool OpenedByNoiseToggle);

    private const int ShowWindowHide = 0;
    private const uint MouseEventLeftDown = 0x0002;
    private const uint MouseEventLeftUp = 0x0004;

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern bool SetCursorPos(int x, int y);

    [DllImport("user32.dll")]
    private static extern void mouse_event(uint dwFlags, uint dx, uint dy, uint dwData, UIntPtr dwExtraInfo);
}
