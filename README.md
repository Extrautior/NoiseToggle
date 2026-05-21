# NoiseToggle

NoiseToggle is a lightweight Windows tray app for switching microphone noise suppression between NVIDIA Broadcast and Discord Krisp.

## Features

- Tray app with manual toggle and mode status
- Global hotkey, including single-key hotkeys
- Optional Start with Windows setting
- Discord/Vencord bridge installer from the tray menu
- NVIDIA Broadcast bridge installer and restore action from the tray menu
- Per-game auto switching with installed-game scan and running-process picker
- Settings stored in `%APPDATA%\NoiseToggle\settings.json`

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

This modifies only the local install and creates a local backup. To undo it, use:

```text
Restore NVIDIA Broadcast backup
```

If the NVIDIA Broadcast bridge is not installed or stops working after an NVIDIA Broadcast update, NoiseToggle falls back to UI automation.

## Build

Requires the .NET 9 SDK on Windows.

```powershell
.\build-installer.ps1
```

The installer is written to:

```text
publish\NoiseToggleSetup.exe
```
