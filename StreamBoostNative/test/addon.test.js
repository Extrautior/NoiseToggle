const assert = require("node:assert/strict");

const fixture = require("../build/Release/discord_voice.node");
const addon = require("../build/Release/streamboost_hook.node");

const wait = milliseconds => new Promise(resolve => setTimeout(resolve, milliseconds));
const approximately = (actual, expected, tolerance = 2) =>
    assert.ok(Math.abs(actual - expected) <= tolerance, `${actual} was not close to ${expected}`);

(async () => {
    for (const name of ["getStatus", "initialize", "setEnabled", "setGain"]) {
        assert.equal(typeof addon[name], "function", `${name} export is missing`);
    }

    assert.equal(addon.initialize(), true);
    let status;
    for (let attempt = 0; attempt < 100; attempt++) {
        status = addon.getStatus();
        if (status.installed || ["unsupported", "error"].includes(status.state)) break;
        await wait(20);
    }
    assert.equal(status.state, "installed", JSON.stringify(status));
    assert.ok(status.targetRva > 0);

    assert.equal(addon.setGain(20), 100);
    assert.equal(addon.setGain(2500), 1000);
    assert.equal(addon.setGain(200), 200);
    assert.equal(addon.setEnabled(true), true);

    fixture.invoke(new Int16Array([3277, -3277, 16384, -16384]), 2, 2, 48000);
    const boosted = fixture.getLastSamples();
    approximately(boosted[0], 4915);
    approximately(boosted[1], -4915);
    approximately(boosted[2], 29318);
    approximately(boosted[3], -29318);

    status = addon.getStatus();
    assert.equal(status.callbackCount, 1);
    assert.equal(status.processedCallbackCount, 1);
    assert.equal(status.lastFrames, 2);
    assert.equal(status.lastChannels, 2);
    assert.equal(status.lastSampleRate, 48000);

    fixture.invoke(new Int16Array([1000, -1000]), 2, 1, 48000);
    assert.deepEqual(Array.from(fixture.getLastSamples()), [1000, -1000]);
    assert.equal(addon.getStatus().processedCallbackCount, 1, "mono microphone audio must bypass gain");

    assert.equal(addon.setEnabled(false), false);
    fixture.invoke(new Int16Array([8192, -8192]), 1, 2, 44100);
    assert.deepEqual(Array.from(fixture.getLastSamples()), [8192, -8192]);
    assert.equal(addon.getStatus().processedCallbackCount, 1);
    console.log("StreamBoost final outgoing hook, gain, limiter, microphone bypass, metrics, and disabled bypass tests passed");
})().catch(error => {
    console.error(error);
    process.exitCode = 1;
});
