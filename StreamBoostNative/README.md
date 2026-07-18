# StreamBoost Native

This Windows N-API addon amplifies Discord's outgoing sound-share PCM at `AudioSendStream::SendAudioData`, immediately before WebRTC consumes the final `AudioFrame`. It does not create a virtual audio device, capture the desktop a second time, or alter headphone volume.

The locator requires the `AudioSendStream::SendAudioData` diagnostic marker, the expected frame-transfer instructions, and the current `AudioFrame` data/mute layout to resolve to exactly one bounded function. If those structural checks fail after a Discord update, the addon stays disabled and the Vencord plugin uses the NoiseToggle relay fallback. Only stereo or multichannel frames are amplified, so Discord's normal mono microphone path is not changed.

## Build and test

Requirements: Node.js, Python, and Visual Studio 2022 with the Desktop development with C++ workload.

```powershell
npm install
npm run build
npm test
```

The test builds a test-only module named `discord_voice.node`, lets the production locator and MinHook detour attach to it, then verifies gain smoothing, soft limiting, callback metrics, mono microphone bypass, and disabled bypass with synthetic 16-bit `AudioFrame` PCM.

The production artifact is `build/Release/streamboost_hook.node`. The `discord_voice.node` file in that folder is only a test fixture and must not be distributed as part of the runtime installation.
