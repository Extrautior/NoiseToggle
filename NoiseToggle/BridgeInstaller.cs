namespace NoiseToggle;

internal static class BridgeInstaller
{
    public static string PluginDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "BetterDiscord", "plugins");

    public static string PluginPath => Path.Combine(PluginDirectory, "NoiseToggleBridge.plugin.js");
    public static string VencordPatcherPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Vencord", "dist", "patcher.js");

    private const string VencordBeginMarker = "/* NoiseToggleBridge BEGIN */";
    private const string VencordEndMarker = "/* NoiseToggleBridge END */";

    public static string Install(AppSettings settings)
    {
        settings.Save();
        var installed = new List<string>();
        if (InstallBetterDiscordPlugin())
        {
            installed.Add("BetterDiscord");
        }

        if (InstallVencordPatch())
        {
            installed.Add("Vencord");
        }

        return installed.Count == 0
            ? "No Vencord or BetterDiscord install was found. Install Vencord, then run this again."
            : $"Installed bridge for {string.Join(" and ", installed)}. Restart Discord.";
    }

    private static bool InstallBetterDiscordPlugin()
    {
        if (!Directory.Exists(PluginDirectory))
        {
            return false;
        }

        File.WriteAllText(PluginPath, PluginSource);
        return true;
    }

    private static bool InstallVencordPatch()
    {
        if (!File.Exists(VencordPatcherPath))
        {
            return false;
        }

        var existing = File.ReadAllText(VencordPatcherPath);
        if (existing.Contains(VencordBeginMarker, StringComparison.Ordinal))
        {
            var begin = existing.IndexOf(VencordBeginMarker, StringComparison.Ordinal);
            var end = existing.IndexOf(VencordEndMarker, begin, StringComparison.Ordinal);
            if (end >= 0)
            {
                end += VencordEndMarker.Length;
                File.WriteAllText(VencordPatcherPath, existing[..begin] + VencordPatchSource + existing[end..]);
            }

            return true;
        }

        var backupPath = VencordPatcherPath + ".noisetoggle.bak";
        if (!File.Exists(backupPath))
        {
            File.Copy(VencordPatcherPath, backupPath);
        }

        File.WriteAllText(VencordPatcherPath, existing + Environment.NewLine + VencordPatchSource);
        return true;
    }

    private const string PluginSource = """
/**
 * @name NoiseToggleBridge
 * @description Local-only bridge for NoiseToggle. Exposes Discord Krisp/noise suppression control on 127.0.0.1.
 * @version 1.0.0
 * @author Codex
 */
module.exports = class NoiseToggleBridge {
    start() {
        const fs = require("fs");
        const path = require("path");
        const http = require("http");
        const settingsPath = path.join(process.env.APPDATA, "NoiseToggle", "settings.json");
        const settings = JSON.parse(fs.readFileSync(settingsPath, "utf8"));
        const token = settings.BridgeToken || settings.bridgeToken;
        const port = settings.BridgePort || settings.bridgePort || 28473;

        const findModule = filter => {
            if (globalThis.Vencord?.Webpack?.find) return globalThis.Vencord.Webpack.find(filter);
            if (globalThis.Vencord?.Webpack?.findByProps) {
                try {
                    const result = globalThis.Vencord.Webpack.findByProps("getNoiseCancellation");
                    if (result && filter(result)) return result;
                } catch {}
            }
            if (globalThis.BdApi?.Webpack?.getModule) return globalThis.BdApi.Webpack.getModule(filter);
            return null;
        };

        const findVoice = () => {
            const candidates = [
                m => m?.setNoiseCancellation && (m?.getNoiseCancellation || m?.isNoiseCancellationEnabled),
                m => m?.setNoiseSuppression && (m?.getNoiseSuppression || m?.isNoiseSuppressionEnabled),
                m => m?.setKrispEnabled || m?.getKrispEnabled
            ];
            for (const filter of candidates) {
                const mod = findModule(filter);
                if (mod) return mod;
            }
            return null;
        };

        const getState = () => {
            const voice = findVoice();
            if (!voice) throw new Error("Could not find Discord voice/noise suppression module");
            if (typeof voice.getNoiseCancellation === "function") return !!voice.getNoiseCancellation();
            if (typeof voice.isNoiseCancellationEnabled === "function") return !!voice.isNoiseCancellationEnabled();
            if (typeof voice.getNoiseSuppression === "function") return !!voice.getNoiseSuppression();
            if (typeof voice.isNoiseSuppressionEnabled === "function") return !!voice.isNoiseSuppressionEnabled();
            if (typeof voice.getKrispEnabled === "function") return !!voice.getKrispEnabled();
            return false;
        };

        const setState = enabled => {
            const voice = findVoice();
            if (!voice) throw new Error("Could not find Discord voice/noise suppression module");
            if (typeof voice.setNoiseCancellation === "function") return voice.setNoiseCancellation(enabled);
            if (typeof voice.setNoiseSuppression === "function") return voice.setNoiseSuppression(enabled);
            if (typeof voice.setKrispEnabled === "function") return voice.setKrispEnabled(enabled);
            throw new Error("Found voice module but no supported setter");
        };

        const authOk = req => req.headers.authorization === `Bearer ${token}`;
        this.server = http.createServer((req, res) => {
            const send = (code, payload) => {
                res.writeHead(code, { "Content-Type": "application/json" });
                res.end(JSON.stringify(payload));
            };

            if (!authOk(req)) return send(401, { error: "Unauthorized" });

            try {
                if (req.method === "GET" && req.url === "/state") {
                    return send(200, { krispEnabled: getState(), KrispEnabled: getState() });
                }

                if (req.method === "POST" && req.url === "/krisp") {
                    let body = "";
                    req.on("data", chunk => body += chunk);
                    req.on("end", () => {
                        try {
                            const payload = JSON.parse(body || "{}");
                            setState(!!payload.enabled);
                            send(200, { ok: true, krispEnabled: getState(), KrispEnabled: getState() });
                        } catch (err) {
                            send(500, { error: String(err?.message || err) });
                        }
                    });
                    return;
                }

                send(404, { error: "Not found" });
            } catch (err) {
                send(500, { error: String(err?.message || err) });
            }
        });
        this.server.listen(port, "127.0.0.1");
        console.log(`[NoiseToggleBridge] Listening on 127.0.0.1:${port}`);
    }

    stop() {
        if (this.server) {
            this.server.close();
            this.server = null;
        }
    }
};
""";

    private const string VencordPatchSource = """
/* NoiseToggleBridge BEGIN */
(() => {
    if (global.__NoiseToggleBridgeStarted) return;
    global.__NoiseToggleBridgeStarted = true;

    const fs = require("fs");
    const path = require("path");
    const http = require("http");
    const electron = require("electron");

    const settingsPath = path.join(process.env.APPDATA, "NoiseToggle", "settings.json");
    const readSettings = () => JSON.parse(fs.readFileSync(settingsPath, "utf8"));
    const getDiscordWindow = () => {
        const windows = electron.BrowserWindow.getAllWindows();
        return windows.find(w => {
            try {
                const url = w.webContents.getURL();
                return !w.isDestroyed() && /discord(app)?\.com|discord\.com/.test(url);
            } catch {
                return false;
            }
        }) || windows.find(w => !w.isDestroyed());
    };

    const rendererSource = enabled => `
        (() => {
            const requested = ${enabled};
            const W = globalThis.Vencord?.Webpack;
            const BD = globalThis.BdApi?.Webpack;

            const unwrap = module => {
                const out = [];
                const add = value => {
                    if (value && typeof value === "object" && !out.includes(value)) out.push(value);
                };
                add(module);
                add(module?.default);
                add(module?.Z);
                add(module?.ZP);
                return out;
            };

            const hasAny = (module, props) => unwrap(module).some(value => props.some(prop => typeof value?.[prop] === "function"));
            const findByAnyProps = props => {
                const found = [];
                const add = module => unwrap(module).forEach(value => {
                    if (value && !found.includes(value)) found.push(value);
                });

                for (const prop of props) {
                    try { if (W?.findByProps) add(W.findByProps(prop)); } catch {}
                    try { if (BD?.getByKeys) add(BD.getByKeys(prop)); } catch {}
                }

                try { if (W?.find) add(W.find(module => hasAny(module, props))); } catch {}
                try { if (BD?.getModule) add(BD.getModule(module => hasAny(module, props))); } catch {}

                return found.find(value => props.some(prop => typeof value?.[prop] === "function")) || null;
            };

            const setterNames = [
                "setNoiseCancellation",
                "setNoiseCancellationEnabled",
                "setNoiseSuppression",
                "setNoiseSuppressionEnabled",
                "setInputNoiseCancellation",
                "setKrispEnabled",
                "setKrisp",
                "toggleNoiseCancellation",
                "toggleNoiseSuppression"
            ];
            const getterNames = [
                "getNoiseCancellation",
                "isNoiseCancellationEnabled",
                "getNoiseCancellationEnabled",
                "getNoiseSuppression",
                "isNoiseSuppressionEnabled",
                "getNoiseSuppressionEnabled",
                "getInputNoiseCancellation",
                "getKrispEnabled",
                "isKrispEnabled"
            ];

            const voice = findByAnyProps([...setterNames, ...getterNames]);
            if (!voice) throw new Error("Discord voice/noise suppression module was not found");

            const getState = () => {
                for (const name of getterNames) {
                    if (typeof voice[name] === "function") return !!voice[name]();
                }

                return requested === null ? false : !!requested;
            };

            if (requested !== null) {
                let called = false;
                for (const name of setterNames) {
                    if (typeof voice[name] === "function") {
                        voice[name](!!requested);
                        called = true;
                        break;
                    }
                }

                if (!called) throw new Error("Discord voice module has no supported Krisp setter");
            }

            return getState();
        })();
    `;

    const evalInDiscord = async enabled => {
        const win = getDiscordWindow();
        if (!win) throw new Error("Discord window was not found");
        return await win.webContents.executeJavaScript(rendererSource(enabled), true);
    };

    const send = (res, code, payload) => {
        res.writeHead(code, { "Content-Type": "application/json" });
        res.end(JSON.stringify(payload));
    };

    const start = () => {
        let settings;
        try {
            settings = readSettings();
        } catch (err) {
            console.error("[NoiseToggleBridge] Missing settings:", err);
            return;
        }

        const port = settings.BridgePort || settings.bridgePort || 28473;
        const token = settings.BridgeToken || settings.bridgeToken;
        const server = http.createServer((req, res) => {
            if (req.headers.authorization !== `Bearer ${token}`) {
                send(res, 401, { error: "Unauthorized" });
                return;
            }

            if (req.method === "GET" && req.url === "/state") {
                evalInDiscord(null)
                    .then(state => send(res, 200, { krispEnabled: !!state, KrispEnabled: !!state }))
                    .catch(err => send(res, 500, { error: String(err?.message || err) }));
                return;
            }

            if (req.method === "POST" && req.url === "/krisp") {
                let body = "";
                req.on("data", chunk => body += chunk);
                req.on("end", () => {
                    let enabled = false;
                    try {
                        enabled = !!JSON.parse(body || "{}").enabled;
                    } catch {}

                    evalInDiscord(enabled)
                        .then(state => send(res, 200, { ok: true, krispEnabled: !!state, KrispEnabled: !!state }))
                        .catch(err => send(res, 500, { error: String(err?.message || err) }));
                });
                return;
            }

            send(res, 404, { error: "Not found" });
        });

        server.on("error", err => console.error("[NoiseToggleBridge] Server error:", err));
        server.listen(port, "127.0.0.1", () => console.log(`[NoiseToggleBridge] Listening on 127.0.0.1:${port}`));
    };

    try {
        electron.app?.whenReady ? electron.app.whenReady().then(start) : start();
    } catch (err) {
        console.error("[NoiseToggleBridge] Startup failed:", err);
    }
})();
/* NoiseToggleBridge END */
""";
}
