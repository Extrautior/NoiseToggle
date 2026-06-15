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
        Timeout = TimeSpan.FromSeconds(8)
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

        for (var attempt = 1; attempt <= 2; attempt++)
        {
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
            if (body?.Ok == true && body.Enabled == enabled)
            {
                AppLog.Info($"NVIDIA Broadcast private bridge set microphone noise removal to {(enabled ? "on" : "off")}.");
                return true;
            }

            var verified = await TryGetNoiseRemovalStateAsync(cancellationToken);
            if (verified == enabled)
            {
                AppLog.Info($"NVIDIA Broadcast private bridge verified microphone noise removal is {(enabled ? "on" : "off")}.");
                return true;
            }

            AppLog.Info($"NVIDIA Broadcast private bridge did not verify requested state on attempt {attempt} ({body?.Error ?? "state mismatch"}).");
        }

        return false;
    }

    public async Task<bool?> TryGetNoiseRemovalStateAsync(CancellationToken cancellationToken)
    {
        if (!await IsBridgeReadyAsync(cancellationToken))
        {
            return null;
        }

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, BridgeUri("microphone-noise-removal"));
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _settings.BridgeToken);

            using var response = await _httpClient.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            var body = await response.Content.ReadFromJsonAsync<BroadcastBridgeResponse>(cancellationToken: cancellationToken);
            return body?.Ok == true ? body.Enabled : null;
        }
        catch
        {
            return null;
        }
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
                WorkingDirectory = Path.GetDirectoryName(BroadcastExe),
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

    private sealed record BroadcastBridgeResponse(bool Ok, bool? Enabled, string? Source, string? Error);
}
