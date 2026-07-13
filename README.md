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
- Automatic outgoing Discord Go Live audio boost for application and full-desktop streams
- Vencord StreamBoost voice-control popup and slider from 100% to 1000%, with the equivalent dB value shown while adjusting
- Soft peak compression that preserves audible gain while preventing boosted stream audio from clipping
- Discord voice exclusion during full-desktop capture, preventing viewers from hearing themselves
- Settings stored in `%APPDATA%\NoiseToggle\settings.json`

## Current compatibility

NoiseToggle v0.2.0 was verified on July 13, 2026 with:

- NVIDIA Broadcast `2.2.0.10298`
- Discord Stable `1.0.9245`
- Vencord commit `94cc541`
- Windows 11 build `26200`
- VB-Audio CABLE render endpoint

The bridge protocols use an authenticated random token, bind only to `127.0.0.1`, and verify the live effect state after every change.

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

## Discord StreamBoost

The desktop-only custom Vencord plugin lives in `VencordPlugins/StreamBoost`.
Its primary Windows backend hooks Discord's final `AudioSendStream::SendAudioData`
stage and amplifies stereo/multichannel sound-share `AudioFrame` PCM immediately
before WebRTC consumes it. Normal mono microphone frames bypass the gain. This has
no second capture path, virtual-device latency, or change to headphone volume.

The older NoiseToggle relay remains an automatic compatibility fallback when a
future Discord update fails the native hook's structural safety checks:

- Application/game streams capture only the selected process and its child processes.
- Full-desktop streams capture everything except Discord's process tree. Discord
  launches the relay as its child, so the same exclusion also prevents relay feedback.
- Audio is rendered to an active `CABLE Input`/`CABLE In` endpoint, so the boosted
  copy is not played through the streamer's headphones.
- If the direct hook and relay fallback are both unavailable, Discord keeps its
  original sound-share source and shows an error toast instead of changing system volume.
- Stopping the stream leaves the silent relay pre-warmed for the next Go Live.
  Disabling the plugin or quitting Discord stops it.

To build the plugin, copy or link `VencordPlugins/StreamBoost` to
`src/userplugins/streamBoost.desktop` in a Vencord source checkout, then run:

```powershell
pnpm install --frozen-lockfile
pnpm build
pnpm inject
```

Enable **StreamBoost** under **Vencord Settings → Plugins** and restart Discord once.
While sharing a screen, use the StreamBoost icon directly to the left of Discord's
**Stop Streaming** button to open its small gain popup. Custom Vencord plugins
are compiled into Vencord, so rebuild after updating Vencord.

## Build

Requires the .NET 9 SDK on Windows.

```powershell
git submodule update --init --recursive
.\build-installer.ps1
```

The installer is written to:

```text
publish\NoiseToggleSetup.exe
```
