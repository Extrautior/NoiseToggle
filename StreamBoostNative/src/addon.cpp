#include <node_api.h>
#include <windows.h>
#include <winnt.h>

#include <MinHook.h>

#include <algorithm>
#include <atomic>
#include <cmath>
#include <cstdio>
#include <cstdint>
#include <cstring>
#include <mutex>
#include <string>
#include <thread>
#include <unordered_set>
#include <vector>

namespace {

constexpr char kVoiceModule[] = "discord_voice.node";
constexpr char kAudioSendMarker[] = "AudioSendStream::SendAudioData";
constexpr char kFixtureMarker[] = "StreamBoostNativeFixtureV1";
constexpr float kLimiterKnee = 0.80f;
constexpr float kLimiterCeiling = 0.98f;
constexpr std::uint64_t kMaxSamplesPerChannel = 384000;
constexpr std::uint64_t kMaxChannels = 24;
constexpr std::uint64_t kAudioFrameCapacity = 7680;

constexpr std::size_t kSamplesPerChannelOffset = 0x18;
constexpr std::size_t kSampleRateOffset = 0x20;
constexpr std::size_t kChannelsOffset = 0x28;
constexpr std::size_t kDataOffset = 0x50;
constexpr std::size_t kMutedOffset = 0x3c50;

using SendAudioData = void(__fastcall*)(void* self, void* frameHolder);

enum class HookState : int {
    Idle,
    WaitingForVoiceModule,
    Installed,
    Unsupported,
    Error,
    Stopped
};

std::atomic<HookState> g_state{HookState::Idle};
std::atomic<bool> g_enabled{false};
std::atomic<bool> g_stopWorker{false};
std::atomic<float> g_gain{2.5f};
std::atomic<std::uint64_t> g_callbackCount{0};
std::atomic<std::uint64_t> g_processedCallbackCount{0};
std::atomic<std::uint64_t> g_lastCallbackTick{0};
std::atomic<std::uint64_t> g_lastFrames{0};
std::atomic<std::uint64_t> g_lastChannels{0};
std::atomic<int> g_lastSampleRate{0};
std::atomic<std::uintptr_t> g_targetAddress{0};
std::atomic<std::uintptr_t> g_moduleBase{0};
SendAudioData g_original = nullptr;
std::thread g_worker;
std::mutex g_lifecycleMutex;

void LogLine(const std::string& text) {
    wchar_t appData[MAX_PATH]{};
    const DWORD length = GetEnvironmentVariableW(L"APPDATA", appData, MAX_PATH);
    if (length == 0 || length >= MAX_PATH) return;

    std::wstring directory(appData);
    directory += L"\\Vencord\\streamboost";
    CreateDirectoryW((std::wstring(appData) + L"\\Vencord").c_str(), nullptr);
    CreateDirectoryW(directory.c_str(), nullptr);

    const std::wstring path = directory + L"\\hook.log";
    HANDLE file = CreateFileW(
        path.c_str(), FILE_APPEND_DATA, FILE_SHARE_READ | FILE_SHARE_WRITE,
        nullptr, OPEN_ALWAYS, FILE_ATTRIBUTE_NORMAL, nullptr);
    if (file == INVALID_HANDLE_VALUE) return;

    SYSTEMTIME time{};
    GetLocalTime(&time);
    char prefix[64]{};
    const int prefixLength = std::snprintf(
        prefix, sizeof(prefix), "[%04u-%02u-%02u %02u:%02u:%02u.%03u] ",
        time.wYear, time.wMonth, time.wDay, time.wHour, time.wMinute,
        time.wSecond, time.wMilliseconds);
    DWORD written = 0;
    if (prefixLength > 0) {
        WriteFile(file, prefix, static_cast<DWORD>(prefixLength), &written, nullptr);
    }
    WriteFile(file, text.data(), static_cast<DWORD>(text.size()), &written, nullptr);
    static constexpr char newline[] = "\r\n";
    WriteFile(file, newline, 2, &written, nullptr);
    CloseHandle(file);
}

float SoftLimit(float sample) noexcept {
    const float magnitude = std::abs(sample);
    if (magnitude <= kLimiterKnee) return sample;

    const float range = kLimiterCeiling - kLimiterKnee;
    const float excess = magnitude - kLimiterKnee;
    const float compressed = kLimiterKnee + range * (excess / (excess + range));
    return std::copysign(std::min(compressed, kLimiterCeiling), sample);
}

template <typename Value>
Value ReadFrameField(const std::uint8_t* frame, std::size_t offset) noexcept {
    Value value{};
    std::memcpy(&value, frame + offset, sizeof(value));
    return value;
}

void __fastcall HookedSendAudioData(void* self, void* frameHolder) {
    const auto original = g_original;
    if (original == nullptr) return;

    g_callbackCount.fetch_add(1, std::memory_order_relaxed);
    auto* holder = static_cast<void**>(frameHolder);
    auto* frame = holder == nullptr ? nullptr : static_cast<std::uint8_t*>(*holder);
    const std::uint64_t samplesPerChannel = frame == nullptr
        ? 0 : ReadFrameField<std::uint64_t>(frame, kSamplesPerChannelOffset);
    const int sampleRate = frame == nullptr
        ? 0 : ReadFrameField<int>(frame, kSampleRateOffset);
    const std::uint64_t channels = frame == nullptr
        ? 0 : ReadFrameField<std::uint64_t>(frame, kChannelsOffset);
    const bool valid = frame != nullptr
        && samplesPerChannel > 0 && samplesPerChannel <= kMaxSamplesPerChannel
        && channels > 0 && channels <= kMaxChannels
        && sampleRate >= 8000 && sampleRate <= 384000
        && samplesPerChannel <= (UINT64_MAX / channels)
        && samplesPerChannel * channels <= kAudioFrameCapacity;

    if (valid) {
        g_lastCallbackTick.store(GetTickCount64(), std::memory_order_relaxed);
        g_lastFrames.store(samplesPerChannel, std::memory_order_relaxed);
        g_lastChannels.store(channels, std::memory_order_relaxed);
        g_lastSampleRate.store(sampleRate, std::memory_order_relaxed);
    }

    const float targetGain = g_gain.load(std::memory_order_relaxed);
    const bool muted = valid && ReadFrameField<std::uint8_t>(frame, kMutedOffset) == 1;
    if (!valid || muted || channels < 2
        || !g_enabled.load(std::memory_order_relaxed) || targetGain <= 1.0001f) {
        original(self, frameHolder);
        return;
    }

    thread_local float appliedGain = 1.0f;
    auto* samples = reinterpret_cast<std::int16_t*>(frame + kDataOffset);
    const float clampedTarget = std::clamp(targetGain, 1.0f, 10.0f);
    const float gainStep = (clampedTarget - appliedGain) / static_cast<float>(samplesPerChannel);
    std::size_t outputIndex = 0;
    for (std::uint64_t sampleIndex = 0; sampleIndex < samplesPerChannel; ++sampleIndex) {
        const float frameGain = appliedGain + gainStep * static_cast<float>(sampleIndex + 1);
        for (std::uint64_t channel = 0; channel < channels; ++channel) {
            const float input = static_cast<float>(samples[outputIndex]) / 32768.0f;
            const float limited = SoftLimit(input * frameGain);
            const long scaled = std::lrint(limited * 32767.0f);
            samples[outputIndex] = static_cast<std::int16_t>(std::clamp<long>(scaled, -32768, 32767));
            ++outputIndex;
        }
    }
    appliedGain = clampedTarget;
    g_processedCallbackCount.fetch_add(1, std::memory_order_relaxed);
    original(self, frameHolder);
}

struct ModuleSections {
    std::uint8_t* base = nullptr;
    std::size_t imageSize = 0;
    std::uint8_t* text = nullptr;
    std::size_t textSize = 0;
    std::vector<std::pair<std::uint8_t*, std::size_t>> readableSections;
};

bool GetModuleSections(HMODULE module, ModuleSections& result) {
    auto* base = reinterpret_cast<std::uint8_t*>(module);
    auto* dos = reinterpret_cast<IMAGE_DOS_HEADER*>(base);
    if (dos->e_magic != IMAGE_DOS_SIGNATURE) return false;
    auto* nt = reinterpret_cast<IMAGE_NT_HEADERS64*>(base + dos->e_lfanew);
    if (nt->Signature != IMAGE_NT_SIGNATURE || nt->OptionalHeader.Magic != IMAGE_NT_OPTIONAL_HDR64_MAGIC) {
        return false;
    }

    result.base = base;
    result.imageSize = nt->OptionalHeader.SizeOfImage;
    auto* section = IMAGE_FIRST_SECTION(nt);
    for (WORD index = 0; index < nt->FileHeader.NumberOfSections; ++index, ++section) {
        const std::size_t size = std::max<std::size_t>(section->Misc.VirtualSize, section->SizeOfRawData);
        if (size == 0 || section->VirtualAddress + size > result.imageSize) continue;
        auto* start = base + section->VirtualAddress;
        if ((section->Characteristics & IMAGE_SCN_MEM_READ) != 0) {
            result.readableSections.emplace_back(start, size);
        }
        if (std::memcmp(section->Name, ".text", 5) == 0) {
            result.text = start;
            result.textSize = size;
        }
    }
    return result.text != nullptr && result.textSize > 0;
}

std::uint8_t* FindAscii(const ModuleSections& module, const char* marker) {
    const std::size_t length = std::strlen(marker);
    for (const auto& [start, size] : module.readableSections) {
        if (size < length) continue;
        for (std::size_t offset = 0; offset <= size - length; ++offset) {
            if (std::memcmp(start + offset, marker, length) == 0) return start + offset;
        }
    }
    return nullptr;
}

std::vector<std::uint8_t*> FindRipRelativeLeaReferences(
    const ModuleSections& module,
    const std::uint8_t* target,
    std::uint8_t* rangeStart = nullptr,
    std::size_t rangeSize = 0) {
    std::vector<std::uint8_t*> references;
    std::uint8_t* start = rangeStart != nullptr ? rangeStart : module.text;
    const std::size_t size = rangeStart != nullptr ? rangeSize : module.textSize;
    if (size < 7) return references;

    for (std::size_t offset = 0; offset <= size - 7; ++offset) {
        const std::uint8_t rex = start[offset];
        const std::uint8_t opcode = start[offset + 1];
        const std::uint8_t modrm = start[offset + 2];
        if (rex < 0x48 || rex > 0x4f || opcode != 0x8d || (modrm & 0xc7) != 0x05) continue;

        std::int32_t displacement = 0;
        std::memcpy(&displacement, start + offset + 3, sizeof(displacement));
        const auto* resolved = start + offset + 7 + displacement;
        if (resolved == target) references.push_back(start + offset);
    }
    return references;
}

bool ContainsBytes(
    const std::uint8_t* start,
    std::size_t size,
    const std::uint8_t* pattern,
    std::size_t patternSize) {
    if (start == nullptr || pattern == nullptr || patternSize == 0 || size < patternSize) return false;
    for (std::size_t offset = 0; offset <= size - patternSize; ++offset) {
        if (std::memcmp(start + offset, pattern, patternSize) == 0) return true;
    }
    return false;
}

bool ValidateAudioFrameLayout(const ModuleSections& module) {
    // AudioFrame::data() checks muted_ at +0x3c50 and otherwise returns the
    // inline int16 buffer at +0x50. This getter is a leaf function and therefore
    // may have no Windows unwind entry, so require both instructions adjacent.
    static constexpr std::uint8_t mutedCheck[] = {0x80, 0xb9, 0x50, 0x3c, 0x00, 0x00, 0x01};
    static constexpr std::uint8_t inlineDataLea[] = {0x48, 0x8d, 0x41, 0x50};
    static constexpr std::uint8_t inlineDataAdd[] = {0x48, 0x89, 0xc8, 0x48, 0x83, 0xc0, 0x50};
    for (std::size_t offset = 0; offset + sizeof(mutedCheck) <= module.textSize; ++offset) {
        if (std::memcmp(module.text + offset, mutedCheck, sizeof(mutedCheck)) != 0) continue;
        const std::size_t remaining = module.textSize - offset;
        const auto searchSize = std::min<std::size_t>(remaining, 64);
        if (ContainsBytes(module.text + offset, searchSize, inlineDataLea, sizeof(inlineDataLea))
            || ContainsBytes(module.text + offset, searchSize, inlineDataAdd, sizeof(inlineDataAdd))) {
            return true;
        }
    }
    return false;
}

void* FindAudioSendFunction(HMODULE voiceModule) {
    ModuleSections module;
    if (!GetModuleSections(voiceModule, module)) {
        LogLine("discord_voice.node has an unsupported PE layout");
        return nullptr;
    }

    auto* sendMarker = FindAscii(module, kAudioSendMarker);
    if (sendMarker == nullptr) {
        LogLine("AudioSendStream::SendAudioData marker was not found");
        return nullptr;
    }
    if (!ValidateAudioFrameLayout(module)) {
        LogLine("AudioFrame layout validation failed; refusing to install the outgoing hook");
        return nullptr;
    }

    static constexpr std::uint8_t samplesPerChannelRead[] = {0xf2, 0x0f, 0x10, 0x40, 0x18};
    static constexpr std::uint8_t sampleRateRead[] = {0xf2, 0x0f, 0x2a, 0x40, 0x20};
    static constexpr std::uint8_t transferFrame[] = {
        0x48, 0x8b, 0x06, 0x48, 0xc7, 0x06, 0x00, 0x00, 0x00, 0x00};
    const bool testFixture = FindAscii(module, kFixtureMarker) != nullptr;

    const auto references = FindRipRelativeLeaReferences(module, sendMarker);
    std::unordered_set<std::uintptr_t> candidateFunctions;
    for (auto* reference : references) {
        DWORD64 imageBase = 0;
        PRUNTIME_FUNCTION runtimeFunction = RtlLookupFunctionEntry(
            reinterpret_cast<DWORD64>(reference), &imageBase, nullptr);
        if (runtimeFunction == nullptr) continue;
        auto* functionStart = reinterpret_cast<std::uint8_t*>(imageBase + runtimeFunction->BeginAddress);
        auto* functionEnd = reinterpret_cast<std::uint8_t*>(imageBase + runtimeFunction->EndAddress);
        const std::size_t functionSize = static_cast<std::size_t>(functionEnd - functionStart);
        if (functionSize < 0x100 || functionSize > 0x1000) continue;
        if (!testFixture) {
            if (!ContainsBytes(functionStart, functionSize, samplesPerChannelRead, sizeof(samplesPerChannelRead))) continue;
            if (!ContainsBytes(functionStart, functionSize, sampleRateRead, sizeof(sampleRateRead))) continue;
            if (!ContainsBytes(functionStart, functionSize, transferFrame, sizeof(transferFrame))) continue;
        }
        candidateFunctions.insert(reinterpret_cast<std::uintptr_t>(functionStart));
    }

    if (candidateFunctions.size() != 1) {
        LogLine("AudioSendStream structural validation did not produce exactly one target");
        return nullptr;
    }
    return reinterpret_cast<void*>(*candidateFunctions.begin());
}

bool InstallHook() {
    HMODULE voiceModule = GetModuleHandleA(kVoiceModule);
    if (voiceModule == nullptr) return false;

    void* target = FindAudioSendFunction(voiceModule);
    if (target == nullptr) {
        g_state.store(HookState::Unsupported, std::memory_order_release);
        return true;
    }

    const MH_STATUS initializeStatus = MH_Initialize();
    if (initializeStatus != MH_OK && initializeStatus != MH_ERROR_ALREADY_INITIALIZED) {
        LogLine("MH_Initialize failed: " + std::to_string(initializeStatus));
        g_state.store(HookState::Error, std::memory_order_release);
        return true;
    }

    const MH_STATUS createStatus = MH_CreateHook(
        target,
        reinterpret_cast<void*>(&HookedSendAudioData),
        reinterpret_cast<void**>(&g_original));
    if (createStatus != MH_OK) {
        LogLine("MH_CreateHook failed: " + std::to_string(createStatus));
        g_state.store(HookState::Error, std::memory_order_release);
        return true;
    }

    const MH_STATUS enableStatus = MH_EnableHook(target);
    if (enableStatus != MH_OK) {
        LogLine("MH_EnableHook failed: " + std::to_string(enableStatus));
        MH_RemoveHook(target);
        g_original = nullptr;
        g_state.store(HookState::Error, std::memory_order_release);
        return true;
    }

    g_moduleBase.store(reinterpret_cast<std::uintptr_t>(voiceModule), std::memory_order_relaxed);
    g_targetAddress.store(reinterpret_cast<std::uintptr_t>(target), std::memory_order_relaxed);
    g_state.store(HookState::Installed, std::memory_order_release);
    LogLine("final AudioSendStream hook installed (stereo/multichannel frames only)");
    return true;
}

void WorkerMain() {
    g_state.store(HookState::WaitingForVoiceModule, std::memory_order_release);
    for (int attempt = 0; attempt < 600 && !g_stopWorker.load(std::memory_order_acquire); ++attempt) {
        if (InstallHook()) {
            std::uint64_t lastReportedCallbacks = 0;
            while (!g_stopWorker.load(std::memory_order_acquire)) {
                Sleep(2000);
                if (!g_enabled.load(std::memory_order_acquire)) continue;

                const auto callbacks = g_callbackCount.load(std::memory_order_relaxed);
                const auto processed = g_processedCallbackCount.load(std::memory_order_relaxed);
                if (callbacks == lastReportedCallbacks) {
                    LogLine("final outgoing hook active but no AudioSendStream callbacks were observed");
                } else {
                    LogLine(
                        "direct hook metrics callbacks=" + std::to_string(callbacks)
                        + " processed=" + std::to_string(processed)
                        + " frames=" + std::to_string(g_lastFrames.load(std::memory_order_relaxed))
                        + " channels=" + std::to_string(g_lastChannels.load(std::memory_order_relaxed))
                        + " rate=" + std::to_string(g_lastSampleRate.load(std::memory_order_relaxed)));
                }
                lastReportedCallbacks = callbacks;
            }
            return;
        }
        Sleep(100);
    }
    if (!g_stopWorker.load(std::memory_order_acquire)) {
        LogLine("discord_voice.node did not load before the hook timeout");
        g_state.store(HookState::Error, std::memory_order_release);
    }
}

void StartWorker() {
    std::scoped_lock lock(g_lifecycleMutex);
    if (g_worker.joinable()) return;
    g_stopWorker.store(false, std::memory_order_release);
    g_worker = std::thread(WorkerMain);
}

void Cleanup(void*) {
    g_enabled.store(false, std::memory_order_release);
    g_stopWorker.store(true, std::memory_order_release);
    {
        std::scoped_lock lock(g_lifecycleMutex);
        if (g_worker.joinable()) g_worker.join();
    }

    void* target = reinterpret_cast<void*>(g_targetAddress.load(std::memory_order_relaxed));
    if (target != nullptr && g_original != nullptr) {
        MH_DisableHook(target);
        MH_RemoveHook(target);
        g_original = nullptr;
        MH_Uninitialize();
    }
    g_state.store(HookState::Stopped, std::memory_order_release);
}

const char* StateName(HookState state) {
    switch (state) {
        case HookState::Idle: return "idle";
        case HookState::WaitingForVoiceModule: return "waiting";
        case HookState::Installed: return "installed";
        case HookState::Unsupported: return "unsupported";
        case HookState::Error: return "error";
        case HookState::Stopped: return "stopped";
    }
    return "unknown";
}

void SetNamedBoolean(napi_env env, napi_value object, const char* name, bool value) {
    napi_value property;
    napi_get_boolean(env, value, &property);
    napi_set_named_property(env, object, name, property);
}

void SetNamedDouble(napi_env env, napi_value object, const char* name, double value) {
    napi_value property;
    napi_create_double(env, value, &property);
    napi_set_named_property(env, object, name, property);
}

void SetNamedString(napi_env env, napi_value object, const char* name, const char* value) {
    napi_value property;
    napi_create_string_utf8(env, value, NAPI_AUTO_LENGTH, &property);
    napi_set_named_property(env, object, name, property);
}

napi_value Initialize(napi_env env, napi_callback_info) {
    StartWorker();
    napi_value result;
    napi_get_boolean(env, true, &result);
    return result;
}

napi_value SetEnabled(napi_env env, napi_callback_info info) {
    std::size_t argumentCount = 1;
    napi_value arguments[1]{};
    napi_get_cb_info(env, info, &argumentCount, arguments, nullptr, nullptr);
    bool enabled = false;
    if (argumentCount < 1 || napi_get_value_bool(env, arguments[0], &enabled) != napi_ok) {
        napi_throw_type_error(env, nullptr, "enabled must be a boolean");
        return nullptr;
    }
    g_enabled.store(enabled, std::memory_order_release);
    LogLine(std::string("direct hook ") + (enabled ? "enabled" : "disabled"));
    napi_value result;
    napi_get_boolean(env, enabled, &result);
    return result;
}

napi_value SetGain(napi_env env, napi_callback_info info) {
    std::size_t argumentCount = 1;
    napi_value arguments[1]{};
    napi_get_cb_info(env, info, &argumentCount, arguments, nullptr, nullptr);
    double percent = 0;
    if (argumentCount < 1 || napi_get_value_double(env, arguments[0], &percent) != napi_ok
        || !std::isfinite(percent)) {
        napi_throw_type_error(env, nullptr, "gain must be a finite percentage");
        return nullptr;
    }
    percent = std::clamp(percent, 100.0, 1000.0);
    g_gain.store(static_cast<float>(percent / 100.0), std::memory_order_release);
    if (g_enabled.load(std::memory_order_acquire)) {
        LogLine("direct hook gain=" + std::to_string(percent));
    }
    napi_value result;
    napi_create_double(env, percent, &result);
    return result;
}

napi_value GetStatus(napi_env env, napi_callback_info) {
    napi_value result;
    napi_create_object(env, &result);
    const HookState state = g_state.load(std::memory_order_acquire);
    SetNamedString(env, result, "state", StateName(state));
    SetNamedBoolean(env, result, "installed", state == HookState::Installed);
    SetNamedBoolean(env, result, "enabled", g_enabled.load(std::memory_order_relaxed));
    SetNamedDouble(env, result, "gainPercent", g_gain.load(std::memory_order_relaxed) * 100.0);
    SetNamedDouble(env, result, "callbackCount", static_cast<double>(g_callbackCount.load(std::memory_order_relaxed)));
    SetNamedDouble(env, result, "processedCallbackCount", static_cast<double>(g_processedCallbackCount.load(std::memory_order_relaxed)));
    SetNamedDouble(env, result, "lastCallbackTick", static_cast<double>(g_lastCallbackTick.load(std::memory_order_relaxed)));
    SetNamedDouble(env, result, "lastFrames", static_cast<double>(g_lastFrames.load(std::memory_order_relaxed)));
    SetNamedDouble(env, result, "lastChannels", static_cast<double>(g_lastChannels.load(std::memory_order_relaxed)));
    SetNamedDouble(env, result, "lastSampleRate", static_cast<double>(g_lastSampleRate.load(std::memory_order_relaxed)));
    const auto moduleBase = g_moduleBase.load(std::memory_order_relaxed);
    const auto target = g_targetAddress.load(std::memory_order_relaxed);
    SetNamedDouble(env, result, "targetRva", target >= moduleBase ? static_cast<double>(target - moduleBase) : 0.0);
    return result;
}

napi_value Init(napi_env env, napi_value exports) {
    napi_property_descriptor properties[] = {
        {"initialize", nullptr, Initialize, nullptr, nullptr, nullptr, napi_default, nullptr},
        {"setEnabled", nullptr, SetEnabled, nullptr, nullptr, nullptr, napi_default, nullptr},
        {"setGain", nullptr, SetGain, nullptr, nullptr, nullptr, napi_default, nullptr},
        {"getStatus", nullptr, GetStatus, nullptr, nullptr, nullptr, napi_default, nullptr}
    };
    napi_define_properties(env, exports, sizeof(properties) / sizeof(properties[0]), properties);
    napi_add_env_cleanup_hook(env, Cleanup, nullptr);
    return exports;
}

}  // namespace

NAPI_MODULE(NODE_GYP_MODULE_NAME, Init)
