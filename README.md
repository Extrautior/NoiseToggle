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
- Settings stored in `%APPDATA%\NoiseToggle\settings.json`

## Current compatibility

NoiseToggle v0.1.5 was verified on June 15, 2026 with:

- NVIDIA Broadcast `2.2.0.10298`
- Discord Stable `1.0.9241`
- Vencord patcher commit `e8415d7`

The bridge protocols use an authenticated random token, bind only to `127.0.0.1`, and verify the live effect state after every change.

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
