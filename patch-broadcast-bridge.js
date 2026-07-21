;/* NoiseToggle Broadcast Bridge BEGIN */
(function () {
    "use strict";

    const ntBridgeVersion = 7;
    try {
        const ntCrypto = require("crypto");
        const ntElectron = require("electron");
        const ntFs = require("fs");
        const ntHttp = require("http");
        const ntPath = require("path");
        const ntSettingsPath = ntPath.join(process.env.APPDATA || "", "NoiseToggle", "settings.json");

        function ntLog(level, message, error) {
            try {
                const target = log && typeof log[level] === "function" ? log : console;
                target[level](message, error || "");
            } catch (_) {}
        }

        function ntSettings() {
            try {
                return JSON.parse(ntFs.readFileSync(ntSettingsPath, "utf8"));
            } catch (_) {
                return {};
            }
        }

        function ntPort() {
            const configured = Number(ntSettings().BroadcastBridgePort);
            return Number.isInteger(configured) && configured > 0 && configured <= 65535 ? configured : 28474;
        }

        function ntJson(res, status, body) {
            const text = JSON.stringify(body);
            res.writeHead(status, {
                "Content-Type": "application/json",
                "Content-Length": Buffer.byteLength(text),
                "Cache-Control": "no-store"
            });
            res.end(text);
        }

        function ntLoopback(req) {
            const remote = (req.socket && req.socket.remoteAddress) || "";
            return remote === "127.0.0.1" || remote === "::1" || remote === "::ffff:127.0.0.1";
        }

        function ntSameToken(actual, expected) {
            if (!actual || !expected) return false;
            const left = Buffer.from(actual);
            const right = Buffer.from(expected);
            return left.length === right.length && ntCrypto.timingSafeEqual(left, right);
        }

        function ntAuthorized(req) {
            if (!ntLoopback(req)) return false;
            const token = ntSettings().BridgeToken || "";
            return ntSameToken(req.headers.authorization || "", "Bearer " + token);
        }

        function ntReadBody(req) {
            return new Promise((resolve, reject) => {
                let data = "";
                let settled = false;
                req.on("data", chunk => {
                    if (settled) return;
                    data += chunk;
                    if (data.length > 64 * 1024) {
                        settled = true;
                        reject(new Error("request-too-large"));
                        req.destroy();
                    }
                });
                req.on("end", () => {
                    if (settled) return;
                    try {
                        resolve(data ? JSON.parse(data) : {});
                    } catch (_) {
                        reject(new Error("invalid-json"));
                    }
                });
                req.on("error", reject);
            });
        }

        function ntReadPersistedState() {
            try {
                const appSettings = ntPath.join(process.env.APPDATA || "", "nvidia-broadcast", "AppSetting.json");
                const json = JSON.parse(ntFs.readFileSync(appSettings, "utf8"));
                const effect = json?.AppStorage?.MaxineEffects?.MicrophoneEffects?.microphoneNoiseRemoval;
                return typeof effect?.enabled === "boolean" ? effect.enabled : null;
            } catch (_) {
                return null;
            }
        }

        function ntWindowReady() {
            return !!(mainWin && !mainWin.isDestroyed() && !mainWin.webContents.isDestroyed());
        }

        function ntGatewayCall(channel, replyPrefix, payload, timeoutMs) {
            return new Promise((resolve, reject) => {
                if (!ntWindowReady()) {
                    reject(new Error("main-window-not-ready"));
                    return;
                }

                const replyChannel = replyPrefix + Date.now() + "-" + ntCrypto.randomBytes(4).toString("hex");
                let finished = false;
                const finish = (error, value) => {
                    if (finished) return;
                    finished = true;
                    clearTimeout(timer);
                    ntElectron.ipcMain.removeListener(replyChannel, onReply);
                    error ? reject(error) : resolve(value);
                };
                const onReply = (_event, value) => finish(null, value);
                const timer = setTimeout(
                    () => finish(new Error(channel + "-timeout")),
                    timeoutMs || 5000
                );

                ntElectron.ipcMain.once(replyChannel, onReply);
                mainWin.webContents.send(channel, { ...payload, replyChannel });
            });
        }

        function ntReadEffectStrength(effect) {
            let value = effect?.value;
            for (let depth = 0; depth < 32; depth++) {
                if (typeof value === "number" && Number.isFinite(value)) return value;
                if (!value || typeof value !== "object" || !Object.prototype.hasOwnProperty.call(value, "strength")) {
                    return null;
                }
                value = value.strength;
            }
            return null;
        }

        function ntHasCanonicalStrength(effect) {
            return !!(
                effect?.value &&
                typeof effect.value === "object" &&
                typeof effect.value.strength === "number" &&
                Number.isFinite(effect.value.strength)
            );
        }

        function ntFindNoiseEffect(value, seen) {
            if (!value || typeof value !== "object") return null;
            seen = seen || new Set();
            if (seen.has(value)) return null;
            seen.add(value);

            if (value.effectId === "microphoneNoiseRemoval" && typeof value.enabled === "boolean") {
                return {
                    enabled: value.enabled,
                    strength: ntReadEffectStrength(value),
                    canonical: ntHasCanonicalStrength(value),
                    effect: value
                };
            }

            if (Object.prototype.hasOwnProperty.call(value, "microphoneNoiseRemoval")) {
                const effect = value.microphoneNoiseRemoval;
                if (typeof effect === "boolean") return { enabled: effect, effect: value };
                if (effect && typeof effect.enabled === "boolean") {
                    return {
                        enabled: effect.enabled,
                        strength: ntReadEffectStrength(effect),
                        canonical: ntHasCanonicalStrength(effect),
                        effect
                    };
                }
            }

            for (const child of Object.values(value)) {
                const found = ntFindNoiseEffect(child, seen);
                if (found) return found;
            }
            return null;
        }

        async function ntGatewayState() {
            const raw = await ntGatewayCall(
                "gateway-get-enabled-effects",
                "gateway-get-effects-result-",
                { effectType: "microphone" },
                5000
            );
            const found = ntFindNoiseEffect(raw);
            return found && typeof found.strength === "number"
                ? {
                    ok: found.canonical,
                    enabled: found.enabled,
                    strength: found.strength,
                    canonical: found.canonical,
                    effective: !found.enabled || found.strength > 0,
                    source: "nvidia-gateway",
                    raw,
                    effect: found.effect,
                    error: found.canonical ? undefined : "invalid-effect-parameters"
                }
                : { ok: false, enabled: null, source: "nvidia-gateway", error: "effect-not-found", raw };
        }

        async function ntGatewaySet(enabled, current) {
            const value = typeof current?.strength === "number" && current.strength > 0
                ? current.strength
                : 1;
            return await ntGatewayCall(
                "gateway-enable-effect",
                "gateway-enable-effect-result-",
                { effectId: "microphoneNoiseRemoval", enabled, value },
                10000
            );
        }

        async function ntGetNoiseRemovalOnce() {
            const errors = [];
            try {
                const gateway = await ntGatewayState();
                if (gateway.ok) return gateway;
                errors.push(gateway.error || "gateway-state-unavailable");
            } catch (error) {
                errors.push(String(error?.message || error));
            }

            return {
                ok: false,
                enabled: null,
                source: "unavailable",
                persistedEnabled: ntReadPersistedState(),
                error: errors.join("; ") || "live-state-unavailable"
            };
        }

        async function ntGetNoiseRemoval() {
            let state = null;
            for (let attempt = 0; attempt < 20; attempt++) {
                state = await ntGetNoiseRemovalOnce();
                if (state.ok) return state;
                await new Promise(resolve => setTimeout(resolve, 150));
            }
            return state || {
                ok: false,
                enabled: null,
                source: "unavailable",
                persistedEnabled: ntReadPersistedState(),
                error: "live-state-unavailable"
            };
        }

        async function ntVerifyNoiseRemoval(enabled) {
            for (let attempt = 0; attempt < 20; attempt++) {
                const state = await ntGetNoiseRemovalOnce();
                if (state.ok && state.enabled === enabled) return state;
                await new Promise(resolve => setTimeout(resolve, 150));
            }
            return await ntGetNoiseRemovalOnce();
        }

        async function ntSetNoiseRemoval(enabled) {
            const errors = [];
            let gatewayReply = null;
            let current = null;

            try {
                current = await ntGatewayState();
                if (typeof current.strength !== "number") {
                    throw new Error(current.error || "live-state-unavailable");
                }
                gatewayReply = await ntGatewaySet(enabled, current);
                const verified = await ntVerifyNoiseRemoval(enabled);
                if (
                    gatewayReply?.success === true &&
                    verified.ok &&
                    verified.canonical === true &&
                    verified.enabled === enabled &&
                    (!enabled || verified.strength > 0)
                ) {
                    return { ok: true, enabled, source: "nvidia-gateway", gatewayReply, verified };
                }
                errors.push(
                    enabled && verified?.strength === 0
                        ? "effect-strength-is-zero"
                        : "gateway-live-state-mismatch"
                );
            } catch (error) {
                errors.push(String(error?.message || error));
            }

            const finalState = await ntGetNoiseRemoval();
            return {
                ok: false,
                enabled: finalState.enabled,
                source: finalState.source,
                persistedEnabled: ntReadPersistedState(),
                error: errors.join("; ") || "live-effect-not-verified",
                gatewayReply,
                finalState
            };
        }

        async function ntDebug() {
            const state = await ntGetNoiseRemoval();
            const data = {
                ok: true,
                bridgeVersion: ntBridgeVersion,
                appVersion: require(ntPath.join(ntElectron.app.getAppPath(), "package.json")).version,
                hasMainWindow: ntWindowReady(),
                mainWindowUrl: ntWindowReady() ? mainWin.webContents.getURL() : null,
                state,
                persistedEnabled: ntReadPersistedState(),
                backendKeys: []
            };
            try {
                data.backendKeys = Object.keys(backend_comm || {}).sort();
            } catch (error) {
                data.backendKeysError = String(error?.message || error);
            }
            return data;
        }

        const ntServer = ntHttp.createServer(async (req, res) => {
            try {
                if (!ntAuthorized(req)) return ntJson(res, 401, { ok: false, error: "unauthorized" });
                const url = new URL(req.url, "http://127.0.0.1:" + ntPort());

                if (req.method === "GET" && url.pathname === "/noisetoggle/v1/health") {
                    return ntJson(res, 200, { ok: true, bridgeVersion: ntBridgeVersion });
                }
                if (req.method === "GET" && url.pathname === "/noisetoggle/v1/debug") {
                    return ntJson(res, 200, await ntDebug());
                }
                if (req.method === "GET" && url.pathname === "/noisetoggle/v1/microphone-noise-removal") {
                    const result = await ntGetNoiseRemoval();
                    return ntJson(res, result.ok ? 200 : 503, result);
                }
                if (req.method === "POST" && url.pathname === "/noisetoggle/v1/microphone-noise-removal") {
                    const body = await ntReadBody(req);
                    if (typeof body.enabled !== "boolean") {
                        return ntJson(res, 400, { ok: false, error: "enabled-must-be-boolean" });
                    }
                    const result = await ntSetNoiseRemoval(body.enabled);
                    return ntJson(res, result.ok ? 200 : 500, result);
                }
                return ntJson(res, 404, { ok: false, error: "not-found" });
            } catch (error) {
                ntLog("error", "NoiseToggle bridge request failed", error);
                return ntJson(res, 500, { ok: false, error: String(error?.message || error) });
            }
        });

        ntServer.on("error", error => ntLog("error", "NoiseToggle bridge server error", error));
        ntServer.listen(ntPort(), "127.0.0.1", () => {
            ntLog("info", "NoiseToggle Broadcast bridge v" + ntBridgeVersion + " listening on 127.0.0.1:" + ntPort());
        });
    } catch (error) {
        try {
            log?.error?.("NoiseToggle Broadcast bridge failed to initialize", error);
        } catch (_) {}
    }
})();
/* NoiseToggle Broadcast Bridge END */
