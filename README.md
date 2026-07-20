# NoiseToggle

NoiseToggle is a lightweight Windows tray app for switching microphone noise suppression between NVIDIA Broadcast and Discord Krisp.

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
- Optional focused-app follow mode resolves real Windows audio sessions (including child processes), selects focus at the start of each wheel-use burst, and preserves manual channel browsing
- Windows 11-style dark settings interface with sidebar navigation and animated controls
- Dark Windows 11 tray menu and a refreshed multi-resolution audio-toggle app icon
- Settings stored in `%APPDATA%\NoiseToggle\settings.json`

## Current compatibility

NoiseToggle v0.1.15 was built and verified on July 20, 2026. Its microphone-control bridges remain compatible with the versions verified for v0.1.7:

- NVIDIA Broadcast `2.2.0.10298`
- Discord Stable `1.0.9241`
- Vencord patcher commit `e8415d7`

The bridge protocols use an authenticated random token, bind only to `127.0.0.1`, and verify the live effect state after every change.

Wave Link wheel control requires Elgato Wave Link to be running locally with a `Personal Mix`. It communicates only with Wave Link's local service and can be disabled independently in NoiseToggle settings.
NoiseToggle discovers Wave Link's dynamic local WebSocket port, retries the connection after startup, and reconnects automatically if Wave Link is restarted.
It refreshes Wave Link channel state every five seconds so newly created, renamed, or removed channels are picked up without restarting NoiseToggle.
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

Download `NoiseToggleSetup.exe` from the latest GitHub release and run it.

The installer places the app under:

```text
%LOCALAPPDATA%\Programs\NoiseToggle
```

It also creates a Start Menu shortcut and starts the tray app after install.

## Bridge Notes

Discord control requires BetterDiscord or Vencord patch support. If Discord or Vencord is reinstalled later, use the tray menu action:

```text
Install Discord/Vencord bridge
```

If you use Vencord, [VencordAutoRepair](https://github.com/Extrautior/VencordAutoRepair) can also keep the NoiseToggle Vencord bridge repaired automatically after Discord/Vencord reinstalls or updates.

NVIDIA Broadcast direct control requires patching the local NVIDIA Broadcast app. Use:

```text
Install NVIDIA Broadcast bridge
```

This modifies only the local install. Before patching, NoiseToggle preserves a clean backup named for the installed Broadcast version, for example:

```text
app.asar.noisetoggle-clean-2.2.0.10298
```

The installer refuses to replace that backup with a different or already patched archive. To undo the patch, use:

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
