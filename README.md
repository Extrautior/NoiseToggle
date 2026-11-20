# NoiseToggle

NoiseToggle is a lightweight Windows tray app for switching microphone noise suppression between NVIDIA Broadcast and Discord Krisp. It supports both the standalone NVIDIA Broadcast app and the NVIDIA Broadcast integration in Corsair iCUE.

## Features

- Tray app with manual toggle and mode status
- Global hotkey, including single-key hotkeys
- Optional Start with Windows setting
- Discord/Vencord bridge installer from the tray menu
- NVIDIA Broadcast bridge installer and restore action from the tray menu
- Per-game auto switching with installed-game scan and running-process picker
- Exact pre-game Broadcast and Krisp state restoration when the game exits
- Integrated AJAZZ/media-wheel control for active Wave Link channels with a topmost translucent HUD
- Wave Link wheel, display, opacity, volume-step, active-channel, and HUD timing settings in the main control panel
- Optional focused-app follow mode prefers the focused app when its Windows audio session is producing audio, including audio from child processes. Otherwise it picks an active Wave Link channel
- Windows 11-style dark settings interface with sidebar navigation and animated controls
- Dark Windows 11 tray menu and a refreshed multi-resolution audio-toggle app icon
- Settings stored in `%APPDATA%\NoiseToggle\settings.json`

## Current compatibility

NoiseToggle v0.1.19 adds Corsair iCUE support, tested on iCUE `5.52.93` with an HS80 MAX Wireless headset. The standalone microphone-control bridges were previously verified with:

- NVIDIA Broadcast `2.2.0.10298`
- Discord Stable `1.0.9241`
- Vencord patcher commit `e8415d7`

The bridge protocols use an authenticated random token, bind only to `127.0.0.1`, and verify the live effect state after every change.

The Corsair HS80 MAX USB receiver's volume wheel can control Wave Link. With **Capture media wheel** enabled, NoiseToggle restores the headset's Windows volume during wheel use. On Windows 11, NoiseToggle keeps the Explorer volume flyout transparent while headset wheel capture is active, so it cannot flash between turns. Turning capture off restores the flyout. Windows still applies the hardware step before NoiseToggle restores the level. To browse channels, press the keyboard's media mute key, turn the wheel, then press mute again to adjust the selected channel.

Wave Link wheel control requires Elgato Wave Link to be running locally with a `Personal Mix`. It communicates only with Wave Link's local service and can be disabled independently in NoiseToggle settings.
NoiseToggle discovers Wave Link's dynamic local WebSocket port, retries the connection after startup, and reconnects automatically if Wave Link is restarted.
It refreshes Wave Link channel state every five seconds so newly created, renamed, or removed channels are picked up without restarting NoiseToggle.
Temporary Wave Link response delays are tolerated without dropping wheel control. Genuine disconnects reconnect quietly with backoff instead of repeatedly opening the HUD.
HUD display selection uses the current Windows screen order rather than unstable `DISPLAY1`/`DISPLAY2` device names, so it continues to work after GPU driver reinstalls renumber the monitors.

The Broadcast bridge sends NVIDIA's scalar effect strength instead of feeding
the full settings object back into the gateway. It rejects malformed or
zero-strength enabled states rather than treating a visual toggle as proof that
native audio processing changed.

When enabling NVIDIA Broadcast noise removal, NoiseToggle verifies the live
effect state after the change. If Broadcast is running but its audio pipeline
does not actually enable the effect, NoiseToggle restarts Broadcast hidden,
waits for the private bridge to return, and retries once before reporting a
failure.

## Install

Download `NoiseToggleSetup.exe` from the [latest GitHub release](https://github.com/Extrautior/NoiseToggle/releases/latest) and run it. Run the installer over an existing NoiseToggle installation to upgrade and retain your settings.

The installer places the app under:

```text
%LOCALAPPDATA%\Programs\NoiseToggle
```

It also creates a Start Menu shortcut and starts the tray app after install.

## Bridge Notes

### Corsair iCUE noise removal

For a compatible Corsair headset, enable **Use iCUE for NVIDIA noise removal**
in the NoiseToggle tray menu. Keep iCUE running and its SDK enabled. Select
**Noise Removal** as the only NVIDIA Broadcast effect in iCUE. The hotkey and
game rules use iCUE's local Automation SDK while iCUE is in the tray. NoiseToggle
uses the tray status and log for switch results in this mode.

Tested on iCUE 5.52.93 with one HS80 MAX Wireless headset. This option requires
one NVIDIA Broadcast-compatible audio device and a matching Automation SDK
interface. The standalone NVIDIA Broadcast bridge is only used when the iCUE
option is off.

Discord control requires BetterDiscord or Vencord patch support. If Discord or Vencord is reinstalled later, use the tray menu action:

```text
Install Discord/Vencord bridge
```

If you use Vencord, [VencordAutoRepair](https://github.com/Extrautior/VencordAutoRepair) can also keep the NoiseToggle Vencord bridge repaired automatically after Discord/Vencord reinstalls or updates.

NVIDIA Broadcast direct control requires patching the local NVIDIA Broadcast app. Use:

```text
Install NVIDIA Broadcast bridge
```

The bridge installer is fully self-contained. It edits NVIDIA Broadcast's Electron archive natively in .NET and does not require Node.js, npm, PowerShell, or any downloaded tooling.

This modifies only the local install. Before patching, NoiseToggle preserves a clean backup named for the installed Broadcast version, for example:

```text
app.asar.noisetoggle-clean-2.2.0.10298
```

The native installer validates the NVIDIA version, archive layout, generated bridge block, and SHA-256 integrity metadata before replacing the live archive. It refuses to replace a clean backup with a different or already patched archive. To undo the patch, use:

```text
Restore NVIDIA Broadcast backup
```

The Broadcast bridge reads and changes the live microphone noise-removal effect through NVIDIA Broadcast's current internal gateway. A saved configuration value alone is never reported as success. If the bridge is unavailable, NoiseToggle can still fall back to UI automation.

The Vencord bridge removes outdated NoiseToggle blocks before inserting one current block. BetterDiscord remains supported through `NoiseToggleBridge.plugin.js`.

## Build

Requires the .NET 9 SDK on Windows.

```powershell
.\build-installer.ps1
```

The installer is written to:

```text
publish\NoiseToggleSetup.exe
```
