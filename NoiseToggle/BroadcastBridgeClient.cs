using System.Diagnostics;
using System.Net.Http.Headers;
using System.Net.Http.Json;

namespace NoiseToggle;

internal sealed class BroadcastBridgeClient
{
    private static readonly string BroadcastExe =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "NVIDIA Corporation", "NVIDIA Broadcast", "NVIDIA Broadcast.exe");

    private readonly AppSettings _settings;
    private readonly HttpClient _httpClient = new()
    {
        Timeout = TimeSpan.FromSeconds(3)
    };

    public BroadcastBridgeClient(AppSettings settings)
    {
        _settings = settings;
    }

    public async Task<bool> TrySetNoiseRemovalAsync(bool enabled, CancellationToken cancellationToken)
    {
        if (!await IsBridgeReadyAsync(cancellationToken))
        {
            TryStartBroadcastHidden();

            for (var i = 0; i < 10; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await Task.Delay(300, cancellationToken);
                if (await IsBridgeReadyAsync(cancellationToken))
                {
                    break;
                }
            }
        }

        if (!await IsBridgeReadyAsync(cancellationToken))
        {
            AppLog.Info("NVIDIA Broadcast private bridge is not available; falling back to UI automation.");
            return false;
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, BridgeUri("microphone-noise-removal"))
        {
            Content = JsonContent.Create(new { enabled })
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _settings.BridgeToken);

        using var response = await _httpClient.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            AppLog.Info($"NVIDIA Broadcast private bridge returned HTTP {(int)response.StatusCode}; falling back to UI automation.");
            return false;
        }

        var body = await response.Content.ReadFromJsonAsync<BroadcastBridgeResponse>(cancellationToken: cancellationToken);
        if (body?.Ok == true)
        {
            AppLog.Info($"NVIDIA Broadcast private bridge set microphone noise removal to {(enabled ? "on" : "off")}.");
            return true;
        }

        AppLog.Info($"NVIDIA Broadcast private bridge did not confirm success ({body?.Error ?? "unknown error"}); falling back to UI automation.");
        return false;
    }

    private async Task<bool> IsBridgeReadyAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, BridgeUri("health"));
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _settings.BridgeToken);
            using var response = await _httpClient.SendAsync(request, cancellationToken);
            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    private static void TryStartBroadcastHidden()
    {
        if (!File.Exists(BroadcastExe) || Process.GetProcessesByName("NVIDIA Broadcast").Length > 0)
        {
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(BroadcastExe)
            {
                Arguments = "--launch-hidden",
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            AppLog.Error("Could not start NVIDIA Broadcast hidden for private bridge.", ex);
        }
    }

    private Uri BridgeUri(string route)
    {
        return new Uri($"http://127.0.0.1:{_settings.BroadcastBridgePort}/noisetoggle/v1/{route}");
    }

    private sealed record BroadcastBridgeResponse(bool Ok, string? Error);
}
