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
        Timeout = Timeout.InfiniteTimeSpan
    };

    public BroadcastBridgeClient(AppSettings settings)
    {
        _settings = settings;
    }

    public async Task<bool> TrySetNoiseRemovalAsync(bool enabled, CancellationToken cancellationToken)
    {
        var maxRecoveryAttempts = enabled ? 2 : 1;

        for (var recoveryAttempt = 1; recoveryAttempt <= maxRecoveryAttempts; recoveryAttempt++)
        {
            if (!await EnsureBridgeReadyAsync(cancellationToken))
            {
                if (enabled && recoveryAttempt < maxRecoveryAttempts)
                {
                    AppLog.Info("NVIDIA Broadcast private bridge is not available while enabling; restarting Broadcast before retry.");
                    await RestartBroadcastHiddenAsync(cancellationToken);
                    continue;
                }

                AppLog.Info("NVIDIA Broadcast private bridge is not available; falling back to UI automation.");
                return false;
            }

            if (!await WaitForLiveStateAvailableAsync(cancellationToken))
            {
                if (enabled && recoveryAttempt < maxRecoveryAttempts)
                {
                    AppLog.Info("NVIDIA Broadcast bridge is healthy but the live microphone effect is unavailable; restarting Broadcast before retry.");
                    await RestartBroadcastHiddenAsync(cancellationToken);
                    continue;
                }

                AppLog.Info("NVIDIA Broadcast live microphone effect is unavailable; falling back to UI automation.");
                return false;
            }

            for (var attempt = 1; attempt <= 2; attempt++)
            {
                var result = await TryPostNoiseRemovalAsync(enabled, cancellationToken);
                if (result)
                {
                    return true;
                }

                AppLog.Info($"NVIDIA Broadcast private bridge did not verify requested state on attempt {attempt}.");
            }

            if (enabled && recoveryAttempt < maxRecoveryAttempts)
            {
                AppLog.Info("NVIDIA Broadcast did not enable microphone noise removal; restarting Broadcast hidden and retrying once.");
                await RestartBroadcastHiddenAsync(cancellationToken);
                continue;
            }
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

            using var response = await SendAsync(request, TimeSpan.FromSeconds(3), cancellationToken);
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

    private async Task<bool> TryPostNoiseRemovalAsync(bool enabled, CancellationToken cancellationToken)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, BridgeUri("microphone-noise-removal"))
            {
                Content = JsonContent.Create(new { enabled })
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _settings.BridgeToken);

            using var response = await SendAsync(request, TimeSpan.FromSeconds(15), cancellationToken);
            var body = await response.Content.ReadFromJsonAsync<BroadcastBridgeResponse>(cancellationToken: cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                AppLog.Info($"NVIDIA Broadcast private bridge returned HTTP {(int)response.StatusCode} ({body?.Error ?? "no bridge error body"}).");
                return false;
            }

            if (body?.Ok != true || body.Enabled != enabled)
            {
                AppLog.Info($"NVIDIA Broadcast private bridge returned an unverified state ({body?.Error ?? "state mismatch"}).");
                return false;
            }

            await Task.Delay(500, cancellationToken);
            var verified = await TryGetNoiseRemovalStateAsync(cancellationToken);
            if (verified == enabled)
            {
                AppLog.Info($"NVIDIA Broadcast private bridge verified microphone noise removal is {(enabled ? "on" : "off")}.");
                return true;
            }

            AppLog.Info($"NVIDIA Broadcast private bridge changed state, but follow-up live verification returned {(verified is null ? "unknown" : verified.Value ? "on" : "off")}.");
            return false;
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException ex)
        {
            AppLog.Error("NVIDIA Broadcast private bridge request timed out.", ex);
            return false;
        }
        catch (Exception ex)
        {
            AppLog.Error("NVIDIA Broadcast private bridge request failed.", ex);
            return false;
        }
    }

    private async Task<bool> EnsureBridgeReadyAsync(CancellationToken cancellationToken)
    {
        if (await IsBridgeReadyAsync(cancellationToken))
        {
            return true;
        }

        TryStartBroadcastHidden();

        for (var i = 0; i < 16; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Delay(500, cancellationToken);
            if (await IsBridgeReadyAsync(cancellationToken))
            {
                return true;
            }
        }

        return false;
    }

    private async Task<bool> WaitForLiveStateAvailableAsync(CancellationToken cancellationToken)
    {
        for (var i = 0; i < 20; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (await TryGetNoiseRemovalStateAsync(cancellationToken) is not null)
            {
                return true;
            }

            await Task.Delay(500, cancellationToken);
        }

        return false;
    }

    private async Task<bool> IsBridgeReadyAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, BridgeUri("health"));
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _settings.BridgeToken);
            using var response = await SendAsync(request, TimeSpan.FromSeconds(2), cancellationToken);
            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);
        return await _httpClient.SendAsync(request, timeoutSource.Token);
    }

    private static bool TryStartBroadcastHidden()
    {
        if (!File.Exists(BroadcastExe) || Process.GetProcessesByName("NVIDIA Broadcast").Length > 0)
        {
            return false;
        }

        try
        {
            Environment.SetEnvironmentVariable("__COMPAT_LAYER", null);
            Process.Start(new ProcessStartInfo(BroadcastExe)
            {
                Arguments = "--launch-hidden",
                WorkingDirectory = Path.GetDirectoryName(BroadcastExe),
                UseShellExecute = true
            });
            return true;
        }
        catch (Exception ex)
        {
            AppLog.Error("Could not start NVIDIA Broadcast hidden for private bridge.", ex);
            return false;
        }
    }

    private static async Task RestartBroadcastHiddenAsync(CancellationToken cancellationToken)
    {
        foreach (var process in Process.GetProcessesByName("NVIDIA Broadcast"))
        {
            try
            {
                process.CloseMainWindow();
            }
            catch
            {
                // Some Electron child processes do not own a closeable window.
            }
        }

        await Task.Delay(1200, cancellationToken);

        foreach (var process in Process.GetProcessesByName("NVIDIA Broadcast"))
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }
            }
            catch (Exception ex)
            {
                AppLog.Error("Could not stop a stuck NVIDIA Broadcast process during bridge recovery.", ex);
            }
            finally
            {
                process.Dispose();
            }
        }

        for (var i = 0; i < 20; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Process.GetProcessesByName("NVIDIA Broadcast").Length == 0)
            {
                break;
            }

            await Task.Delay(500, cancellationToken);
        }

        if (!TryStartBroadcastHidden())
        {
            AppLog.Info("NVIDIA Broadcast was not restarted because a Broadcast process is still present or the executable was not found.");
        }
    }

    private Uri BridgeUri(string route)
    {
        return new Uri($"http://127.0.0.1:{_settings.BroadcastBridgePort}/noisetoggle/v1/{route}");
    }

    private sealed record BroadcastBridgeResponse(bool Ok, bool? Enabled, string? Source, string? Error);
}
