/*
 * StreamBoost native helper for Vencord.
 * SPDX-License-Identifier: GPL-3.0-or-later
 */

import { spawn, type ChildProcessWithoutNullStreams } from "child_process";
import { app } from "electron";
import { existsSync } from "fs";
import { createInterface } from "readline";

interface RelayConfig {
    sourcePid: number;
    fullDesktop: boolean;
    gainPercent: number;
}

interface RelayReady {
    processId: number;
    outputDevice: string;
    gainPercent: number;
    decibels: number;
}

interface RelayMessage extends Partial<RelayReady> {
    type: "ready" | "gain" | "error";
    error?: string;
}

let relay: ChildProcessWithoutNullStreams | null = null;
let operation = Promise.resolve<unknown>(undefined);

function findNoiseToggle(): string {
    const candidates = [
        `${process.env.LOCALAPPDATA}\\Programs\\NoiseToggle\\NoiseToggle.exe`,
        `${app.getPath("userData")}\\NoiseToggle.exe`
    ];

    const executable = candidates.find(candidate => candidate && existsSync(candidate));
    if (!executable) {
        throw new Error("NoiseToggle 0.2.0 or newer is not installed");
    }

    return executable;
}

async function stopCurrentRelay(): Promise<void> {
    const child = relay;
    relay = null;
    if (!child || child.exitCode !== null) return;

    try {
        child.stdin.write('{"type":"stop"}\n');
    } catch {
        // The process may already be shutting down.
    }

    await new Promise<void>(resolve => {
        const timeout = setTimeout(() => {
            if (child.exitCode === null) child.kill();
            resolve();
        }, 1500);
        child.once("exit", () => {
            clearTimeout(timeout);
            resolve();
        });
    });
}

async function startRelayInner(config: RelayConfig): Promise<RelayReady> {
    await stopCurrentRelay();

    const sourcePid = config.fullDesktop ? process.pid : Math.trunc(config.sourcePid);
    if (sourcePid <= 0) throw new Error("Discord did not provide a valid stream audio process");

    const child = relay = spawn(findNoiseToggle(), [
        "--stream-relay",
        "--source-pid", String(sourcePid),
        "--capture-mode", config.fullDesktop ? "exclude" : "include",
        "--gain-percent", String(Math.round(config.gainPercent))
    ], {
        windowsHide: true,
        stdio: ["pipe", "pipe", "pipe"]
    });

    child.stderr.setEncoding("utf8");
    let stderr = "";
    child.stderr.on("data", chunk => stderr = (stderr + chunk).slice(-4000));

    return await new Promise<RelayReady>((resolve, reject) => {
        const lines = createInterface({ input: child.stdout });
        const timeout = setTimeout(() => finish(new Error("NoiseToggle audio relay timed out")), 8000);
        let settled = false;

        const finish = (error?: Error, ready?: RelayReady) => {
            if (settled) return;
            settled = true;
            clearTimeout(timeout);
            lines.close();
            if (error) {
                if (relay === child) relay = null;
                if (child.exitCode === null) child.kill();
                reject(error);
            } else {
                resolve(ready!);
            }
        };

        lines.on("line", line => {
            try {
                const message = JSON.parse(line) as RelayMessage;
                if (message.type === "error") {
                    finish(new Error(message.error || "NoiseToggle audio relay failed"));
                } else if (message.type === "ready" && message.processId && message.outputDevice) {
                    finish(undefined, {
                        processId: message.processId,
                        outputDevice: message.outputDevice,
                        gainPercent: message.gainPercent ?? config.gainPercent,
                        decibels: message.decibels ?? 0
                    });
                }
            } catch {
                // Ignore non-protocol output while waiting for the ready message.
            }
        });
        child.once("error", error => finish(error));
        child.once("exit", code => finish(new Error(
            stderr.trim() || `NoiseToggle audio relay exited with code ${code ?? "unknown"}`)));
    });
}

export function startRelay(_: Electron.IpcMainInvokeEvent, config: RelayConfig): Promise<RelayReady> {
    const next = operation.then(() => startRelayInner(config), () => startRelayInner(config));
    operation = next.catch(() => undefined);
    return next;
}

export async function setGain(_: Electron.IpcMainInvokeEvent, percent: number): Promise<void> {
    const child = relay;
    if (!child || child.exitCode !== null) return;
    child.stdin.write(JSON.stringify({
        type: "gain",
        percent: Math.max(100, Math.min(1000, Math.round(percent)))
    }) + "\n");
}

export async function setSource(_: Electron.IpcMainInvokeEvent, config: RelayConfig): Promise<void> {
    const child = relay;
    if (!child || child.exitCode !== null) throw new Error("NoiseToggle audio relay is not running");

    const sourcePid = config.fullDesktop ? process.pid : Math.trunc(config.sourcePid);
    if (sourcePid <= 0) throw new Error("Discord did not provide a valid stream audio process");

    child.stdin.write(JSON.stringify({
        type: "source",
        sourcePid,
        captureMode: config.fullDesktop ? "exclude" : "include"
    }) + "\n");
}

export async function pauseRelay(): Promise<void> {
    const child = relay;
    if (!child || child.exitCode !== null) return;
    child.stdin.write('{"type":"pause"}\n');
}

export function isRelayRunning(): boolean {
    return relay !== null && relay.exitCode === null;
}

export function stopRelay(): Promise<void> {
    const next = operation.then(stopCurrentRelay, stopCurrentRelay);
    operation = next;
    return next;
}

app.once("before-quit", () => {
    const child = relay;
    relay = null;
    if (child?.exitCode === null) child.kill();
});
