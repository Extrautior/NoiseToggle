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

    public static bool IsVencordBridgeCurrent()
    {
        if (!File.Exists(VencordPatcherPath))
        {
            return false;
        }

        return IsCurrentVencordBridge(File.ReadAllText(VencordPatcherPath));
    }

    private static bool IsCurrentVencordBridge(string existing)
    {
        return CountOccurrences(existing, VencordBeginMarker) == 1 &&
               CountOccurrences(existing, VencordEndMarker) == 1 &&
               ExtractBridgeBlock(existing)?.ReplaceLineEndings("\n") == VencordPatchSource.Trim().ReplaceLineEndings("\n");
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
        if (IsCurrentVencordBridge(existing))
        {
            return true;
        }

        var clean = RemoveBridgeBlocks(existing).TrimEnd();
        var updated = clean + Environment.NewLine + Environment.NewLine + VencordPatchSource.Trim() + Environment.NewLine;
        if (updated == existing)
        {
            return true;
        }

        var backupPath = VencordPatcherPath + ".noisetoggle.bak";
        if (!File.Exists(backupPath))
        {
            File.WriteAllText(backupPath, clean + Environment.NewLine);
        }

        File.WriteAllText(VencordPatcherPath, updated);
        return true;
    }

    private static string RemoveBridgeBlocks(string source)
    {
        var result = source;
        while (true)
        {
            var begin = result.IndexOf(VencordBeginMarker, StringComparison.Ordinal);
            if (begin < 0)
            {
                return result;
            }

            var end = result.IndexOf(VencordEndMarker, begin + VencordBeginMarker.Length, StringComparison.Ordinal);
            result = end < 0
                ? result[..begin]
                : result[..begin] + result[(end + VencordEndMarker.Length)..];
        }
    }

    private static string? ExtractBridgeBlock(string source)
    {
        var begin = source.IndexOf(VencordBeginMarker, StringComparison.Ordinal);
        var end = source.IndexOf(VencordEndMarker, begin + VencordBeginMarker.Length, StringComparison.Ordinal);
        return begin >= 0 && end >= 0
            ? source[begin..(end + VencordEndMarker.Length)].Trim()
            : null;
    }

    private static int CountOccurrences(string source, string value)
    {
        var count = 0;
        var index = 0;
        while ((index = source.IndexOf(value, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += value.Length;
        }
        return count;
    }

    private const string SuppressionControllerSource = """
async requested => {
    const unwrap = module => [module, module?.default, module?.Z, module?.ZP]
        .filter((value, index, values) => value && values.indexOf(value) === index);

    const findModule = predicate => {
        const matchingExport = module => unwrap(module).find(candidate =>
            candidate[Symbol.toStringTag] !== "IntlMessagesProxy" && predicate(candidate));
        const vencord = globalThis.Vencord?.Webpack;
        const betterDiscord = globalThis.BdApi?.Webpack;
        for (const find of [
            vencord?.find?.bind(vencord),
            betterDiscord?.getModule?.bind(betterDiscord)
        ]) {
            try {
                const found = matchingExport(find?.(module => !!matchingExport(module)));
                if (found) return found;
            } catch {}
        }
        return null;
    };

    const wait = () => new Promise(resolve => setTimeout(resolve, 100));
    let store;
    let actions;
    for (let attempt = 0; attempt < 10; attempt++) {
        store = findModule(candidate =>
            typeof candidate.getNoiseCancellation === "function" &&
            typeof candidate.getNoiseSuppression === "function" &&
            (typeof candidate.getMediaEngine === "function" || typeof candidate.getInputDeviceId === "function"));
        actions = requested === null ? null : findModule(candidate =>
            typeof candidate.setNoiseCancellation === "function" &&
            typeof candidate.setNoiseSuppression === "function");
        if (store && (requested === null || actions)) break;
        await wait();
    }
    if (!store) throw new Error("Discord's noise suppression state is unavailable");

    const readMode = () => {
        const krisp = store.getNoiseCancellation();
        const standard = store.getNoiseSuppression();
        if (typeof krisp !== "boolean" || typeof standard !== "boolean") {
            throw new Error("Discord returned an unsupported noise suppression state");
        }
        return krisp ? "krisp" : standard ? "standard" : "none";
    };

    const report = mode => ({ ok: true, mode, krispEnabled: mode === "krisp", KrispEnabled: mode === "krisp" });
    const current = readMode();
    if (requested === null || current === requested) return report(current);
    if (!actions) throw new Error("Discord's noise suppression controls are unavailable");

    if (requested === "krisp") {
        await Promise.resolve(actions.setNoiseSuppression(false));
        await Promise.resolve(actions.setNoiseCancellation(true));
    } else {
        await Promise.resolve(actions.setNoiseCancellation(false));
        await Promise.resolve(actions.setNoiseSuppression(requested === "standard"));
    }

    for (let attempt = 0; attempt < 10; attempt++) {
        if (readMode() === requested) return report(requested);
        await wait();
    }
    throw new Error("Discord's live noise suppression mode did not match " + requested);
}
""";

    private const string PluginSource = """
/**
 * @name NoiseToggleBridge
 * @description Authenticated localhost bridge for NoiseToggle Discord Krisp control.
 * @version 3.0.0
 * @author Extrautior
 */
module.exports = class NoiseToggleBridge {
    start() {
        const crypto = require("crypto");
        const fs = require("fs");
        const http = require("http");
        const path = require("path");
        const settingsPath = path.join(process.env.APPDATA, "NoiseToggle", "settings.json");
        const readSettings = () => JSON.parse(fs.readFileSync(settingsPath, "utf8"));

        const controlSuppression = (
""" + SuppressionControllerSource + """
        );

        const send = (res, status, payload) => {
            const body = JSON.stringify(payload);
            res.writeHead(status, {
                "Content-Type": "application/json",
                "Content-Length": Buffer.byteLength(body),
                "Cache-Control": "no-store"
            });
            res.end(body);
        };
        const loopback = req => ["127.0.0.1", "::1", "::ffff:127.0.0.1"].includes(req.socket?.remoteAddress);
        const sameToken = (actual, expected) => {
            const left = Buffer.from(actual || "");
            const right = Buffer.from(expected || "");
            return left.length === right.length && left.length > 0 && crypto.timingSafeEqual(left, right);
        };

        const settings = readSettings();
        const token = settings.BridgeToken || settings.bridgeToken;
        const port = settings.BridgePort || settings.bridgePort || 28473;
        this.server = http.createServer(async (req, res) => {
            if (!loopback(req) || !sameToken(req.headers.authorization, `Bearer ${token}`)) {
                return send(res, 401, { error: "Unauthorized" });
            }
            if (req.method === "GET" && req.url === "/health") return send(res, 200, { ok: true, bridgeVersion: 3 });

            if (req.method === "GET" && req.url === "/state") {
                try {
                    return send(res, 200, await controlSuppression(null));
                } catch (error) {
                    return send(res, 500, { error: String(error?.message || error) });
                }
            }
            if (req.method === "POST" && ["/suppression", "/krisp"].includes(req.url)) {
                let body = "";
                req.on("data", chunk => {
                    body += chunk;
                    if (body.length > 64 * 1024) req.destroy();
                });
                req.on("end", async () => {
                    try {
                        const request = JSON.parse(body || "{}");
                        const mode = req.url === "/krisp" && typeof request.enabled === "boolean"
                            ? (request.enabled ? "krisp" : "none")
                            : request.mode;
                        if (!["none", "standard", "krisp"].includes(mode)) return send(res, 400, { error: "Invalid noise suppression mode" });
                        send(res, 200, await controlSuppression(mode));
                    } catch (error) {
                        send(res, 500, { error: String(error?.message || error) });
                    }
                });
                return;
            }
            send(res, 404, { error: "Not found" });
        });
        this.server.listen(port, "127.0.0.1");
    }

    stop() {
        this.server?.close();
        this.server = null;
    }
};
""";

    internal const string VencordPatchSource = """
/* NoiseToggleBridge BEGIN */
/* NoiseToggleBridge VERSION 3 */
(() => {
    if (global.__NoiseToggleBridgeVersion === 3) return;
    global.__NoiseToggleBridgeVersion = 3;

    const crypto = require("crypto");
    const electron = require("electron");
    const fs = require("fs");
    const http = require("http");
    const path = require("path");
    const settingsPath = path.join(process.env.APPDATA, "NoiseToggle", "settings.json");
    const readSettings = () => JSON.parse(fs.readFileSync(settingsPath, "utf8"));

    const getDiscordWindow = () => {
        const windows = electron.BrowserWindow.getAllWindows().filter(window => !window.isDestroyed());
        return windows.find(window => {
            try {
                return /^https:\/\/(canary\.|ptb\.)?discord\.com\//i.test(window.webContents.getURL());
            } catch {
                return false;
            }
        }) || windows[0];
    };

    const rendererSource = mode => `(
""" + SuppressionControllerSource + """
    )(${JSON.stringify(mode)})`;

    const evalInDiscord = async mode => {
        const window = getDiscordWindow();
        if (!window) throw new Error("Discord window was not found");
        return await window.webContents.executeJavaScript(rendererSource(mode), true);
    };
    const send = (res, status, payload) => {
        const body = JSON.stringify(payload);
        res.writeHead(status, {
            "Content-Type": "application/json",
            "Content-Length": Buffer.byteLength(body),
            "Cache-Control": "no-store"
        });
        res.end(body);
    };
    const loopback = req => ["127.0.0.1", "::1", "::ffff:127.0.0.1"].includes(req.socket?.remoteAddress);
    const sameToken = (actual, expected) => {
        const left = Buffer.from(actual || "");
        const right = Buffer.from(expected || "");
        return left.length === right.length && left.length > 0 && crypto.timingSafeEqual(left, right);
    };

    const start = () => {
        let settings;
        try {
            settings = readSettings();
        } catch (error) {
            console.error("[NoiseToggleBridge] Missing settings:", error);
            return;
        }

        const port = settings.BridgePort || settings.bridgePort || 28473;
        const token = settings.BridgeToken || settings.bridgeToken;
        const server = http.createServer((req, res) => {
            if (!loopback(req) || !sameToken(req.headers.authorization, `Bearer ${token}`)) {
                return send(res, 401, { error: "Unauthorized" });
            }
            if (req.method === "GET" && req.url === "/health") {
                return send(res, 200, { ok: true, bridgeVersion: 3 });
            }
            if (req.method === "GET" && req.url === "/state") {
                evalInDiscord(null)
                    .then(state => send(res, 200, state))
                    .catch(error => send(res, 500, { error: String(error?.message || error) }));
                return;
            }
            if (req.method === "POST" && ["/suppression", "/krisp"].includes(req.url)) {
                let body = "";
                req.on("data", chunk => {
                    body += chunk;
                    if (body.length > 64 * 1024) req.destroy();
                });
                req.on("end", () => {
                    try {
                        const request = JSON.parse(body || "{}");
                        const mode = req.url === "/krisp" && typeof request.enabled === "boolean"
                            ? (request.enabled ? "krisp" : "none")
                            : request.mode;
                        if (!["none", "standard", "krisp"].includes(mode)) {
                            return send(res, 400, { error: "Invalid noise suppression mode" });
                        }
                        evalInDiscord(mode)
                            .then(state => send(res, 200, state))
                            .catch(error => send(res, 500, { error: String(error?.message || error) }));
                    } catch (error) {
                        send(res, 400, { error: String(error?.message || error) });
                    }
                });
                return;
            }
            send(res, 404, { error: "Not found" });
        });

        server.on("error", error => console.error("[NoiseToggleBridge] Server error:", error));
        server.listen(port, "127.0.0.1", () =>
            console.log(`[NoiseToggleBridge] v3 listening on 127.0.0.1:${port}`));
    };

    try {
        electron.app?.whenReady ? electron.app.whenReady().then(start) : start();
    } catch (error) {
        console.error("[NoiseToggleBridge] Startup failed:", error);
    }
})();
/* NoiseToggleBridge END */
""";
}
