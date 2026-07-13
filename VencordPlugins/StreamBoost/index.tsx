/*
 * StreamBoost for Vencord.
 * SPDX-License-Identifier: GPL-3.0-or-later
 */

import "./style.css";

import { definePluginSettings } from "@api/Settings";
import definePlugin, { OptionType, type PluginNative } from "@utils/types";
import { Popout, React, ReactDOM, showToast, Toasts, Tooltip, UserStore } from "@webpack/common";

const Native = VencordNative.pluginHelpers.StreamBoost as PluginNative<typeof import("./native")>;

interface DirectHookStatus {
    state: string;
    installed: boolean;
    enabled: boolean;
    gainPercent: number;
    callbackCount: number;
    processedCallbackCount: number;
    lastCallbackTick: number;
    lastFrames: number;
    lastChannels: number;
    lastSampleRate: number;
    targetRva: number;
    error?: string;
}

interface DirectHookBridge {
    initialize(): boolean;
    setEnabled(enabled: boolean): boolean;
    setGain(percent: number): number;
    getStatus(): DirectHookStatus;
}

declare global {
    interface Window {
        StreamBoostDirect?: DirectHookBridge;
    }
}

interface StreamConnection {
    setSoundshareSource(processId: number | null, useLoopback: boolean): void;
}

interface StreamEvent {
    streamKey: string;
}

interface OriginalSource {
    connection: StreamConnection;
    processId: number;
    useLoopback: boolean;
}

let relayPid: number | null = null;
let originalSource: OriginalSource | null = null;
let switchingSource = false;
let healthTimer: ReturnType<typeof setInterval> | null = null;
let prewarmPromise: Promise<void> | null = null;
let notReadyToastShown = false;
let activeBackend: "direct" | "relay" | null = null;
let directPreparePromise: Promise<boolean> | null = null;

function directHook(): DirectHookBridge | undefined {
    return window.StreamBoostDirect;
}

function directStatus(): DirectHookStatus | null {
    try {
        return directHook()?.getStatus() ?? null;
    } catch {
        return null;
    }
}

function applyGain(percent: number): void {
    try {
        directHook()?.setGain(percent);
    } catch {
        // The relay still receives the update below.
    }
    void Native.setGain(percent).catch(() => undefined);
}

function toDecibels(percent: number): number {
    return 20 * Math.log10(percent / 100);
}

function renderGain(percent: number): string {
    const decibels = toDecibels(percent);
    return `${Math.round(percent)}% (${decibels >= 0 ? "+" : ""}${decibels.toFixed(1)} dB)`;
}

function GainPopout() {
    const { gainPercent } = settings.use(["gainPercent"]);
    const [hookStatus, setHookStatus] = React.useState<DirectHookStatus | null>(() => directStatus());

    React.useEffect(() => {
        const refresh = () => setHookStatus(directStatus());
        refresh();
        const timer = setInterval(refresh, 500);
        return () => clearInterval(timer);
    }, []);

    const setGain = (percent: number) => {
        settings.store.gainPercent = percent;
        applyGain(percent);
    };

    return (
        <div className="vc-streamboost-popout">
            <div className="vc-streamboost-popout-header">
                <span>Outgoing stream volume</span>
                <strong>{renderGain(gainPercent)}</strong>
            </div>
            <div className="vc-streamboost-popout-description">
                Only changes what people watching your stream hear.
            </div>
            <div className="vc-streamboost-popout-status">
                {hookStatus?.installed
                    ? hookStatus.enabled
                        ? hookStatus.processedCallbackCount > 0
                            ? `Direct hook active · ${hookStatus.processedCallbackCount} audio callbacks boosted`
                            : "Direct hook enabled · waiting for outgoing audio callbacks"
                        : "Direct hook ready · your stream has not enabled it"
                    : "Direct hook unavailable · NoiseToggle relay fallback"}
            </div>
            <input
                className="vc-streamboost-popout-slider"
                type="range"
                min={100}
                max={1000}
                step={10}
                value={gainPercent}
                aria-label="Outgoing stream boost percentage"
                onChange={event => setGain(Number(event.currentTarget.value))}
            />
            <div className="vc-streamboost-popout-scale">
                <span>100% (normal)</span>
                <span>1000% (+20 dB)</span>
            </div>
        </div>
    );
}

function VoiceControl() {
    const { gainPercent } = settings.use(["gainPercent"]);
    const buttonRef = React.useRef<HTMLButtonElement>(null);
    const [show, setShow] = React.useState(false);

    return (
        <Popout
            position="top"
            align="right"
            animation={Popout.Animation.NONE}
            shouldShow={show}
            onRequestClose={() => setShow(false)}
            targetElementRef={buttonRef}
            renderPopout={() => <GainPopout />}
        >
            {() => (
                <Tooltip text={`Stream volume boost: ${renderGain(gainPercent)}`} position="top">
                    {tooltipProps => (
                        <button
                            {...tooltipProps}
                            ref={buttonRef}
                            type="button"
                            className="vc-streamboost-control-button"
                            onClick={() => setShow(value => !value)}
                            aria-label={`Stream boost ${Math.round(gainPercent)}%`}
                            aria-expanded={show}
                        >
                            <svg width="20" height="20" viewBox="0 0 24 24" aria-hidden="true">
                                <path fill="currentColor" d="M3 10h2v4H3v-4Zm4-4h2v12H7V6Zm4 2h2v8h-2V8Zm4-6h2v20h-2V2Zm4 7h2v6h-2V9Z" />
                            </svg>
                        </button>
                    )}
                </Tooltip>
            )}
        </Popout>
    );
}

function findVoiceControlsTarget(): HTMLElement | null {
    const disconnect = document.querySelector<HTMLElement>('button[aria-label="Disconnect"]');
    const krisp = Array.from(document.querySelectorAll<HTMLElement>("button[aria-label]"))
        .find(button => /Krisp/i.test(button.getAttribute("aria-label") ?? ""));
    if (!disconnect || !krisp) return null;

    let commonParent = disconnect.parentElement;
    while (commonParent && !commonParent.contains(krisp)) commonParent = commonParent.parentElement;
    return commonParent;
}

function QuickControlPortal() {
    const [target, setTarget] = React.useState<HTMLElement | null>(null);

    React.useEffect(() => {
        let frame = 0;
        const refresh = () => {
            cancelAnimationFrame(frame);
            frame = requestAnimationFrame(() => setTarget(findVoiceControlsTarget()));
        };

        refresh();
        const observer = new MutationObserver(refresh);
        observer.observe(document.body, { childList: true, subtree: true });
        return () => {
            observer.disconnect();
            cancelAnimationFrame(frame);
        };
    }, []);

    return target ? ReactDOM.createPortal(<VoiceControl />, target) : null;
}

function startHealthMonitor(): void {
    if (healthTimer) clearInterval(healthTimer);
    healthTimer = setInterval(async () => {
        if (relayPid === null) return;
        const running = await Native.isRelayRunning().catch(() => false);
        if (running || relayPid === null) return;

        const source = originalSource;
        relayPid = null;
        originalSource = null;
        if (source) {
            switchingSource = true;
            try {
                source.connection.setSoundshareSource(source.processId, source.useLoopback);
            } catch {
                // The stream connection may already be closing.
            } finally {
                switchingSource = false;
            }
            showToast("Stream boost relay stopped; original Discord stream audio was restored", Toasts.Type.FAILURE);
        }

        void Native.stopRelay().catch(() => undefined).finally(() => prewarmRelay(false));
    }, 2000);
}

const settings = definePluginSettings({
    gainPercent: {
        type: OptionType.SLIDER,
        displayName: "Stream audio boost",
        description: "Boost only the audio sent to your Discord stream. 100% is unchanged; the limiter prevents clipping.",
        markers: [100, 250, 400, 600, 800, 1000],
        default: 250,
        stickToMarkers: false,
        componentProps: {
            onValueRender: renderGain,
            onMarkerRender: (value: number) => `${value}%`
        },
        onChange(value: number) {
            applyGain(value);
        }
    }
});

async function prepareDirectHook(): Promise<boolean> {
    if (directPreparePromise) return directPreparePromise;

    directPreparePromise = (async () => {
        const direct = directHook();
        if (!direct) return false;

        try {
            direct.initialize();
            direct.setGain(settings.store.gainPercent);
            direct.setEnabled(false);

            for (let attempt = 0; attempt < 60; attempt++) {
                const status = direct.getStatus();
                if (status.installed) {
                    console.info("[StreamBoost] Direct audio hook ready", status);
                    return true;
                }
                if (status.state === "unavailable" || status.state === "error") {
                    console.warn("[StreamBoost] Direct audio hook unavailable", status);
                    return false;
                }
                await new Promise(resolve => setTimeout(resolve, 100));
            }

            console.warn("[StreamBoost] Direct audio hook timed out", direct.getStatus());
            return false;
        } catch (error) {
            console.error("[StreamBoost] Direct audio hook failed", error);
            return false;
        }
    })();

    return directPreparePromise;
}

function activateDirectHook(): boolean {
    const direct = directHook();
    const status = directStatus();
    if (!direct || !status?.installed) return false;

    try {
        const newlyActivated = activeBackend !== "direct" || !status.enabled;
        originalSource = null;
        activeBackend = "direct";
        direct.setGain(settings.store.gainPercent);
        direct.setEnabled(true);
        void Native.pauseRelay().catch(() => undefined);
        console.info("[StreamBoost] Direct hook enabled for own stream", direct.getStatus());
        if (newlyActivated) {
            showToast(`Direct stream boost ${renderGain(settings.store.gainPercent)}`, Toasts.Type.SUCCESS);
        }
        return true;
    } catch (error) {
        activeBackend = null;
        console.error("[StreamBoost] Could not enable direct hook; using relay fallback", error);
        return false;
    }
}

async function prewarmRelay(showFailure: boolean): Promise<void> {
    if (relayPid !== null) return;
    if (prewarmPromise) return prewarmPromise;

    prewarmPromise = (async () => {
        try {
            const ready = await Native.startRelay({
                sourcePid: 0,
                fullDesktop: true,
                gainPercent: settings.store.gainPercent
            });
            await Native.pauseRelay();
            relayPid = ready.processId;
            notReadyToastShown = false;
            startHealthMonitor();
        } catch (error) {
            relayPid = null;
            if (showFailure) {
                showToast(
                    `Stream boost could not prepare: ${String((error as Error)?.message || error)}`,
                    Toasts.Type.FAILURE);
            }
        } finally {
            prewarmPromise = null;
        }
    })();

    return prewarmPromise;
}

async function stopRelay(restoreOriginal: boolean): Promise<void> {
    if (healthTimer) {
        clearInterval(healthTimer);
        healthTimer = null;
    }
    const source = originalSource;
    originalSource = null;
    const hadActiveStream = source !== null;
    relayPid = null;

    if (restoreOriginal && hadActiveStream && source) {
        switchingSource = true;
        try {
            source.connection.setSoundshareSource(source.processId, source.useLoopback);
        } catch {
            // The stream connection may already be closing.
        } finally {
            switchingSource = false;
        }
    }

    await Native.stopRelay().catch(() => undefined);
}

function isOwnStream({ streamKey }: StreamEvent): boolean {
    return streamKey.endsWith(UserStore.getCurrentUser().id);
}

export default definePlugin({
    name: "StreamBoost",
    description: "Automatically boosts your outgoing Go Live audio without changing headphone volume",
    authors: [{ name: "Extrautior", id: 0n }],
    tags: ["Voice", "Utility"],
    settings,

    patches: [
        {
            find: "#{intl::USER_PROFILE_ACCOUNT_POPOUT_BUTTON_A11Y_LABEL}",
            replacement: {
                match: /(?<=\i\.jsxs?\)\()(\i),{(?=[^}]*?userTag:\i,occluded:)/,
                replace: "$self.PanelWrapper,{VencordOriginal:$1,"
            }
        },
        {
            find: "soundshareEventDriven",
            replacement: {
                match: /soundsharePid:(\i),soundshareEventDriven:!0,soundshareLoopback:(\i)/,
                replace: "soundsharePid:$self.routeSoundsharePid(this,$1,$2),soundshareEventDriven:!0,soundshareLoopback:$2"
            }
        }
    ],

    flux: {
        STREAM_CREATE(event: StreamEvent) {
            if (!isOwnStream(event)) return;
            if (!activateDirectHook()) {
                console.warn("[StreamBoost] Own stream started without a ready direct hook");
                void prewarmRelay(false);
            }
        },
        STREAM_DELETE(event: StreamEvent) {
            if (isOwnStream(event)) {
                originalSource = null;
                activeBackend = null;
                try {
                    directHook()?.setEnabled(false);
                } catch {
                    // The relay cleanup below is still safe.
                }
                void Native.pauseRelay().catch(() => undefined);
            }
        }
    },

    PanelWrapper({ VencordOriginal, ...props }: {
        VencordOriginal: React.ComponentType<any>;
        [key: string]: any;
    }) {
        return (
            <>
                <QuickControlPortal />
                <VencordOriginal {...props} />
            </>
        );
    },

    routeSoundsharePid(connection: StreamConnection, processId: number | null, useLoopback: boolean): number {
        const numericPid = Number(processId ?? 0);
        if (switchingSource || (activeBackend === "relay" && numericPid === relayPid)) return numericPid;

        if (numericPid <= 0 && !useLoopback) {
            originalSource = null;
            activeBackend = null;
            try {
                directHook()?.setEnabled(false);
            } catch {
                // The relay cleanup below is still safe.
            }
            void Native.pauseRelay().catch(() => undefined);
            return numericPid;
        }

        if (activateDirectHook()) return numericPid;

        const preparedPid = relayPid;
        if (preparedPid === null) {
            void prewarmRelay(false);
            if (!notReadyToastShown) {
                notReadyToastShown = true;
                showToast("Stream boost was not ready; this stream is using normal Discord audio", Toasts.Type.FAILURE);
            }
            return numericPid;
        }

        const source = originalSource = { connection, processId: numericPid, useLoopback };
        activeBackend = "relay";
        void Native.setSource({
            sourcePid: numericPid,
            fullDesktop: useLoopback,
            gainPercent: settings.store.gainPercent
        })
            .then(() => {
                if (originalSource !== source) return;
                showToast(`Stream boost ${renderGain(settings.store.gainPercent)}`, Toasts.Type.SUCCESS);
            })
            .catch(error => {
                if (originalSource !== source) return;
                originalSource = null;
                activeBackend = null;
                switchingSource = true;
                try {
                    source.connection.setSoundshareSource(source.processId, source.useLoopback);
                } catch {
                    // The stream connection may already be closing.
                } finally {
                    switchingSource = false;
                }
                showToast(
                    `Stream boost stayed off: ${String((error as Error)?.message || error)}`,
                    Toasts.Type.FAILURE);
            });

        return preparedPid;
    },

    start() {
        void prepareDirectHook().then(ready => {
            if (!ready) void prewarmRelay(true);
        });
    },

    stop() {
        activeBackend = null;
        try {
            directHook()?.setEnabled(false);
        } catch {
            // Continue cleaning up the relay fallback.
        }
        void stopRelay(true);
    }
});
