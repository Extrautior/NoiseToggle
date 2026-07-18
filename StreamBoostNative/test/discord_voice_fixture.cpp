#include <node_api.h>
#include <windows.h>

#include <algorithm>
#include <array>
#include <cstdint>
#include <cstring>
#include <vector>

namespace {

constexpr char kAudioSendMarker[] = "AudioSendStream::SendAudioData";
constexpr char kFixtureMarker[] = "StreamBoostNativeFixtureV1";
constexpr std::size_t kSamplesPerChannelOffset = 0x18;
constexpr std::size_t kSampleRateOffset = 0x20;
constexpr std::size_t kChannelsOffset = 0x28;
constexpr std::size_t kDataOffset = 0x50;
constexpr std::size_t kMutedOffset = 0x3c50;
constexpr std::size_t kFrameBytes = 0x3c68;
constexpr std::size_t kCapacity = 7680;

std::array<std::int16_t, kCapacity> g_zeroSamples{};
std::vector<std::int16_t> g_lastSamples;

__declspec(noinline) const std::int16_t* FixtureAudioFrameData(const std::uint8_t* frame) {
    if (frame[kMutedOffset] == 1) return g_zeroSamples.data();
    return reinterpret_cast<const std::int16_t*>(frame + kDataOffset);
}

template <typename Value>
Value ReadField(const std::uint8_t* frame, std::size_t offset) {
    Value value{};
    std::memcpy(&value, frame + offset, sizeof(value));
    return value;
}

#pragma optimize("", off)
__declspec(noinline) void __fastcall FixtureSendAudioData(void* self, void* frameHolder) {
    // The diagnostics are unreachable during normal tests, but their xrefs let
    // the production locator resolve this test function exactly as it resolves
    // Discord's AudioSendStream::SendAudioData.
    if (self == reinterpret_cast<void*>(static_cast<std::uintptr_t>(1))) {
        OutputDebugStringA(kAudioSendMarker);
    }
    if (self == reinterpret_cast<void*>(static_cast<std::uintptr_t>(2))) {
        OutputDebugStringA(kFixtureMarker);
    }

    auto* holder = static_cast<void**>(frameHolder);
    auto* frame = holder == nullptr ? nullptr : static_cast<std::uint8_t*>(*holder);
    if (frame == nullptr) {
        g_lastSamples.clear();
        return;
    }

    const auto samplesPerChannel = ReadField<std::uint64_t>(frame, kSamplesPerChannelOffset);
    const auto sampleRate = ReadField<int>(frame, kSampleRateOffset);
    const auto channels = ReadField<std::uint64_t>(frame, kChannelsOffset);
    const auto count = std::min<std::uint64_t>(samplesPerChannel * channels, kCapacity);
    const auto* data = FixtureAudioFrameData(frame);
    g_lastSamples.assign(data, data + count);

    // Keep the fixture function large enough for the production function-size
    // bound while also making all three frame metadata fields observable.
    volatile double diagnostic = sampleRate == 0
        ? 0.0 : static_cast<double>(samplesPerChannel) / static_cast<double>(sampleRate);
    diagnostic += static_cast<double>(channels);
    for (std::uint64_t index = 0; index < std::min<std::uint64_t>(count, 64); ++index) {
        diagnostic += static_cast<double>(data[index]) * 0.000001;
    }
    if (diagnostic == 123456.0) OutputDebugStringA("fixture");
}
#pragma optimize("", on)

template <typename Value>
void WriteField(std::uint8_t* frame, std::size_t offset, Value value) {
    std::memcpy(frame + offset, &value, sizeof(value));
}

napi_value Invoke(napi_env env, napi_callback_info info) {
    std::size_t argumentCount = 4;
    napi_value arguments[4]{};
    napi_get_cb_info(env, info, &argumentCount, arguments, nullptr, nullptr);
    if (argumentCount != 4) {
        napi_throw_type_error(env, nullptr, "invoke expects Int16Array, samples per channel, channels, and sample rate");
        return nullptr;
    }

    napi_typedarray_type type;
    std::size_t length = 0;
    void* data = nullptr;
    napi_value arrayBuffer;
    std::size_t byteOffset = 0;
    if (napi_get_typedarray_info(
            env, arguments[0], &type, &length, &data, &arrayBuffer, &byteOffset) != napi_ok
        || type != napi_int16_array) {
        napi_throw_type_error(env, nullptr, "first argument must be an Int16Array");
        return nullptr;
    }

    double samplesPerChannelNumber = 0;
    double channelsNumber = 0;
    std::int32_t sampleRate = 0;
    napi_get_value_double(env, arguments[1], &samplesPerChannelNumber);
    napi_get_value_double(env, arguments[2], &channelsNumber);
    napi_get_value_int32(env, arguments[3], &sampleRate);
    const auto samplesPerChannel = static_cast<std::uint64_t>(samplesPerChannelNumber);
    const auto channels = static_cast<std::uint64_t>(channelsNumber);
    if (samplesPerChannel == 0 || channels == 0
        || samplesPerChannel * channels != length || length > kCapacity) {
        napi_throw_range_error(env, nullptr, "samples per channel times channels must match the input length and fit AudioFrame");
        return nullptr;
    }

    alignas(16) std::array<std::uint8_t, kFrameBytes> frame{};
    WriteField(frame.data(), kSamplesPerChannelOffset, samplesPerChannel);
    WriteField(frame.data(), kSampleRateOffset, sampleRate);
    WriteField(frame.data(), kChannelsOffset, channels);
    std::memcpy(frame.data() + kDataOffset, data, length * sizeof(std::int16_t));
    void* holder = frame.data();
    FixtureSendAudioData(nullptr, &holder);

    napi_value result;
    napi_get_undefined(env, &result);
    return result;
}

napi_value GetLastSamples(napi_env env, napi_callback_info) {
    void* output = nullptr;
    napi_value arrayBuffer;
    const std::size_t byteLength = g_lastSamples.size() * sizeof(std::int16_t);
    napi_create_arraybuffer(env, byteLength, &output, &arrayBuffer);
    if (byteLength > 0) std::memcpy(output, g_lastSamples.data(), byteLength);

    napi_value typedArray;
    napi_create_typedarray(env, napi_int16_array, g_lastSamples.size(), arrayBuffer, 0, &typedArray);
    return typedArray;
}

napi_value Init(napi_env env, napi_value exports) {
    napi_property_descriptor properties[] = {
        {"invoke", nullptr, Invoke, nullptr, nullptr, nullptr, napi_default, nullptr},
        {"getLastSamples", nullptr, GetLastSamples, nullptr, nullptr, nullptr, napi_default, nullptr},
    };
    napi_define_properties(env, exports, sizeof(properties) / sizeof(properties[0]), properties);
    return exports;
}

}  // namespace

NAPI_MODULE(NODE_GYP_MODULE_NAME, Init)
