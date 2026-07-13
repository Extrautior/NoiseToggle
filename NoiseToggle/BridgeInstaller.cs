using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace NoiseToggle;

internal static class BridgeInstaller
{
    public static string PluginDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "BetterDiscord", "plugins");

    public static string PluginPath => Path.Combine(PluginDirectory, "NoiseToggleBridge.plugin.js");

    private static string DefaultVencordPatcherPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Vencord", "dist", "patcher.js");

    public static string VencordPatcherPath =>
        VencordPatcherPaths.FirstOrDefault(File.Exists) ?? DefaultVencordPatcherPath;

    public static IReadOnlyList<string> VencordPatcherPaths => DiscoverVencordPatcherPaths();

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
        var patchers = VencordPatcherPaths.Where(File.Exists).ToArray();
        if (patchers.Length == 0)
        {
            return false;
        }

        return patchers.All(path =>
        {
            var existing = File.ReadAllText(path);
            return CountOccurrences(existing, VencordBeginMarker) == 1 &&
                   CountOccurrences(existing, VencordEndMarker) == 1 &&
                   ExtractBridgeBlock(existing) == VencordPatchSource.Trim();
        });
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
        var installed = false;
        foreach (var path in VencordPatcherPaths.Where(File.Exists))
        {
            InstallVencordPatch(path);
            installed = true;
        }

        return installed;
    }

    private static void InstallVencordPatch(string patcherPath)
    {
        var existing = File.ReadAllText(patcherPath);
        var clean = RemoveBridgeBlocks(existing).TrimEnd();
        var updated = clean + Environment.NewLine + Environment.NewLine + VencordPatchSource.Trim() + Environment.NewLine;
        if (updated == existing)
        {
            return;
        }

        var backupPath = patcherPath + ".noisetoggle.bak";
        if (!File.Exists(backupPath))
        {
            File.WriteAllText(backupPath, clean + Environment.NewLine);
        }

        File.WriteAllText(patcherPath, updated);
    }

    private static IReadOnlyList<string> DiscoverVencordPatcherPaths()
    {
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            DefaultVencordPatcherPath
        };

        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        foreach (var discordFolder in new[] { "Discord", "DiscordCanary", "DiscordPTB" })
        {
            var root = Path.Combine(localAppData, discordFolder);
            if (!Directory.Exists(root))
            {
                continue;
            }

            foreach (var appDirectory in Directory.EnumerateDirectories(root, "app-*"))
            {
                var asarPath = Path.Combine(appDirectory, "resources", "app.asar");
                TryDiscoverDevPatcher(asarPath, paths);
            }
        }

        return paths.ToArray();
    }

    private static void TryDiscoverDevPatcher(string asarPath, HashSet<string> paths)
    {
        try
        {
            if (!File.Exists(asarPath) || new FileInfo(asarPath).Length > 1024 * 1024)
            {
                return;
            }

            var text = Encoding.UTF8.GetString(File.ReadAllBytes(asarPath));
            var match = Regex.Match(text, "require\\(\\\"(?<path>[A-Za-z]:\\\\\\\\[^\\\"]+patcher\\.js)\\\"\\)");
            if (!match.Success)
            {
                return;
            }

            var patcherPath = JsonSerializer.Deserialize<string>($"\"{match.Groups["path"].Value}\"");
            if (!string.IsNullOrWhiteSpace(patcherPath))
            {
                paths.Add(Path.GetFullPath(patcherPath));
            }
        }
        catch
        {
            // A normal packaged Discord app has a binary app.asar with no development patcher path.
        }
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

    private const string PluginSource = """
/**
 * @name NoiseToggleBridge
 * @description Authenticated localhost bridge for NoiseToggle Discord Krisp control.
 * @version 2.0.0
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

        const unwrap = module => [module, module?.default, module?.Z, module?.ZP]
            .filter((value, index, values) => value && values.indexOf(value) === index);
        const setterNames = [
            "setNoiseCancellation", "setNoiseCancellationEnabled", "setNoiseSuppression",
            "setNoiseSuppressionEnabled", "setInputNoiseCancellation", "setKrispEnabled", "setKrisp"
        ];
        const getterNames = [
            "getNoiseCancellation", "isNoiseCancellationEnabled", "getNoiseCancellationEnabled",
            "getNoiseSuppression", "isNoiseSuppressionEnabled", "getNoiseSuppressionEnabled",
            "getInputNoiseCancellation", "getKrispEnabled", "isKrispEnabled"
        ];

        const findModule = predicate => {
            const webpack = globalThis.BdApi?.Webpack;
            const matchingExport = module => unwrap(module).find(predicate) || null;
            try {
                const found = webpack?.getModule?.(module => !!matchingExport(module));
                return matchingExport(found);
            } catch {
                return null;
            }
        };
        const usable = candidate => candidate && candidate[Symbol.toStringTag] !== "IntlMessagesProxy";
        const findStore = () => findModule(candidate =>
            usable(candidate) &&
            getterNames.some(name => typeof candidate[name] === "function") &&
            (typeof candidate.getMediaEngine === "function" || typeof candidate.getInputDeviceId === "function"));
        const findActions = () => findModule(candidate =>
            usable(candidate) &&
            setterNames.some(name => typeof candidate[name] === "function") &&
            (typeof candidate.setNoiseSuppression === "function" || typeof candidate.setInputDevice === "function"));

        const readState = store => {
            for (const name of getterNames) {
                if (typeof store?.[name] === "function") return { enabled: !!store[name](), getter: name };
            }
            throw new Error("Discord media engine store has no supported Krisp getter");
        };

        const setState = async enabled => {
            const store = findStore();
            const actions = findActions();
            if (!store) throw new Error("Discord media engine store was not found");
            if (!actions) throw new Error("Discord media engine actions were not found");
            const setter = setterNames.find(name => typeof actions[name] === "function");
            if (!setter) throw new Error("Discord media engine actions have no supported Krisp setter");
            await Promise.resolve(actions[setter](enabled));
            for (let attempt = 0; attempt < 20; attempt++) {
                const state = readState(store);
                if (state.enabled === enabled) return { ...state, setter };
                await new Promise(resolve => setTimeout(resolve, 100));
            }
            throw new Error("Discord Krisp live state did not match the requested value");
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

        const settings = readSettings();
        const token = settings.BridgeToken || settings.bridgeToken;
        const port = settings.BridgePort || settings.bridgePort || 28473;
        this.server = http.createServer((req, res) => {
            if (!loopback(req) || !sameToken(req.headers.authorization, `Bearer ${token}`)) {
                return send(res, 401, { error: "Unauthorized" });
            }
            if (req.method === "GET" && req.url === "/health") return send(res, 200, { ok: true, bridgeVersion: 2 });

            if (req.method === "GET" && req.url === "/state") {
                try {
                    const state = readState(findStore());
                    return send(res, 200, { krispEnabled: state.enabled, KrispEnabled: state.enabled });
                } catch (error) {
                    return send(res, 500, { error: String(error?.message || error) });
                }
            }
            if (req.method === "POST" && req.url === "/krisp") {
                let body = "";
                req.on("data", chunk => {
                    body += chunk;
                    if (body.length > 64 * 1024) req.destroy();
                });
                req.on("end", async () => {
                    try {
                        const enabled = JSON.parse(body || "{}").enabled;
                        if (typeof enabled !== "boolean") return send(res, 400, { error: "enabled must be boolean" });
                        const state = await setState(enabled);
                        send(res, 200, { ok: true, krispEnabled: state.enabled, KrispEnabled: state.enabled });
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
/* NoiseToggleBridge VERSION 2 */
(() => {
    if (global.__NoiseToggleBridgeVersion === 2) return;
    global.__NoiseToggleBridgeVersion = 2;

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

    const rendererSource = enabled => `
        (async () => {
            const requested = ${enabled};
            const W = globalThis.Vencord?.Webpack;
            const BD = globalThis.BdApi?.Webpack;
            const setterNames = [
                "setNoiseCancellation", "setNoiseCancellationEnabled", "setNoiseSuppression",
                "setNoiseSuppressionEnabled", "setInputNoiseCancellation", "setKrispEnabled", "setKrisp"
            ];
            const getterNames = [
                "getNoiseCancellation", "isNoiseCancellationEnabled", "getNoiseCancellationEnabled",
                "getNoiseSuppression", "isNoiseSuppressionEnabled", "getNoiseSuppressionEnabled",
                "getInputNoiseCancellation", "getKrispEnabled", "isKrispEnabled"
            ];
            const unwrap = module => [module, module?.default, module?.Z, module?.ZP]
                .filter((value, index, values) => value && values.indexOf(value) === index);
            const usable = candidate => candidate && candidate[Symbol.toStringTag] !== "IntlMessagesProxy";
            const findModule = predicate => {
                const matchingExport = module => unwrap(module).find(predicate) || null;
                try {
                    const found = W?.find?.(module => !!matchingExport(module));
                    const match = matchingExport(found);
                    if (match) return match;
                } catch {}
                try {
                    const found = BD?.getModule?.(module => !!matchingExport(module));
                    const match = matchingExport(found);
                    if (match) return match;
                } catch {}
                return null;
            };
            const findStore = () => findModule(candidate =>
                usable(candidate) &&
                getterNames.some(name => typeof candidate[name] === "function") &&
                (typeof candidate.getMediaEngine === "function" || typeof candidate.getInputDeviceId === "function"));
            const findActions = () => findModule(candidate =>
                usable(candidate) &&
                setterNames.some(name => typeof candidate[name] === "function") &&
                (typeof candidate.setNoiseSuppression === "function" || typeof candidate.setInputDevice === "function"));

            let store = null;
            let actions = null;
            for (let attempt = 0; attempt < 50; attempt++) {
                store = findStore();
                actions = requested === null ? null : findActions();
                if (store && (requested === null || actions)) break;
                await new Promise(resolve => setTimeout(resolve, 100));
            }
            if (!store) throw new Error("Discord media engine store was not found");

            const readState = () => {
                for (const name of getterNames) {
                    if (typeof store[name] === "function") return { enabled: !!store[name](), getter: name };
                }
                throw new Error("Discord media engine store has no supported Krisp getter");
            };
            if (requested === null) return { ok: true, ...readState() };

            if (!actions) throw new Error("Discord media engine actions were not found");
            const setter = setterNames.find(name => typeof actions[name] === "function");
            if (!setter) throw new Error("Discord media engine actions have no supported Krisp setter");
            await Promise.resolve(actions[setter](requested));
            for (let attempt = 0; attempt < 20; attempt++) {
                const state = readState();
                if (state.enabled === requested) return { ok: true, ...state, setter };
                await new Promise(resolve => setTimeout(resolve, 100));
            }
            const state = readState();
            return { ok: false, ...state, setter, error: "live-state-mismatch" };
        })();
    `;

    const evalInDiscord = async enabled => {
        const window = getDiscordWindow();
        if (!window) throw new Error("Discord window was not found");
        return await window.webContents.executeJavaScript(rendererSource(enabled), true);
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
                return send(res, 200, { ok: true, bridgeVersion: 2 });
            }
            if (req.method === "GET" && req.url === "/state") {
                evalInDiscord(null)
                    .then(state => {
                        if (!state?.ok || typeof state.enabled !== "boolean") {
                            return send(res, 500, { error: state?.error || "Discord Krisp live state was unavailable" });
                        }
                        send(res, 200, { krispEnabled: state.enabled, KrispEnabled: state.enabled });
                    })
                    .catch(error => send(res, 500, { error: String(error?.message || error) }));
                return;
            }
            if (req.method === "POST" && req.url === "/krisp") {
                let body = "";
                req.on("data", chunk => {
                    body += chunk;
                    if (body.length > 64 * 1024) req.destroy();
                });
                req.on("end", () => {
                    try {
                        const enabled = JSON.parse(body || "{}").enabled;
                        if (typeof enabled !== "boolean") {
                            return send(res, 400, { error: "enabled must be boolean" });
                        }
                        evalInDiscord(enabled)
                            .then(state => {
                                if (!state?.ok || state.enabled !== enabled) {
                                    return send(res, 500, { error: state?.error || "Discord Krisp live state did not change" });
                                }
                                send(res, 200, { ok: true, krispEnabled: state.enabled, KrispEnabled: state.enabled });
                            })
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
            console.log(`[NoiseToggleBridge] v2 listening on 127.0.0.1:${port}`));
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
