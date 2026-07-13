# StreamBoost Vencord plugin

StreamBoost detects Discord Go Live and controls the StreamBoost native module,
which amplifies final stereo/multichannel outgoing `AudioFrame` PCM immediately
before WebRTC consumes it. Discord's normal mono microphone frames bypass the
gain. It does not change Windows, headset, game, or participant volume.

If the native module rejects a future Discord build because its structural safety
checks no longer match, the plugin automatically falls back to NoiseToggle's
headless audio relay. The relay stays pre-warmed, changes capture source in place,
and keeps a stable process ID for both application and desktop streams.

While sharing a screen, the StreamBoost icon directly to the left of Discord's
**Stop Streaming** button opens a small slider popup.
The same slider is also available under **Vencord Settings → Plugins → StreamBoost**;
both show the easy percentage and its decibel equivalent. Application streams
capture only the selected process tree. Full-desktop streams capture everything
except Discord's process tree, which also prevents voice-chat echo and relay
feedback because Discord launches the relay as its child.

This is a desktop-only custom plugin and must be linked or copied to
`src/userplugins/streamBoost.desktop` in a Vencord source checkout before building.
