using System.Text.Json.Serialization;

namespace NoiseToggle;

internal sealed class WaveChannel
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("mixes")]
    public List<WaveChannelMix> Mixes { get; set; } = [];
}

internal sealed class WaveChannelMix
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("level")]
    public decimal Level { get; set; }
}

internal sealed class WaveMix
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("name")]
    public string Name { get; set; } = "";
}

internal sealed record WaveLinkState(List<WaveChannel> Channels, List<WaveMix> Mixes);

internal sealed record WaveLinkHudModel(
    string Channel,
    decimal Volume,
    float Peak,
    bool Adjusting,
    int SelectedIndex,
    int ChannelCount,
    int ActiveCount,
    string Mix,
    string? Status);
