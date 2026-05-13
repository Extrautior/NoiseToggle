using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace NoiseToggle;

internal sealed class DiscordBridgeClient
{
    private readonly AppSettings _settings;
    private readonly HttpClient _http = new()
    {
        Timeout = TimeSpan.FromSeconds(3)
    };

    public DiscordBridgeClient(AppSettings settings)
    {
        _settings = settings;
    }

    public async Task<bool?> GetKrispStateAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var request = CreateRequest(HttpMethod.Get, "/state");
            using var response = await _http.SendAsync(request, cancellationToken);
            await EnsureSuccessAsync(response, cancellationToken);

            using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            var payload = await JsonSerializer.DeserializeAsync<BridgeState>(stream, cancellationToken: cancellationToken);
            return payload?.KrispEnabled;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            throw BridgeUnavailable(ex);
        }
    }

    public async Task SetKrispStateAsync(bool enabled, CancellationToken cancellationToken)
    {
        try
        {
            using var request = CreateRequest(HttpMethod.Post, "/krisp");
            request.Content = new StringContent(JsonSerializer.Serialize(new { enabled }), Encoding.UTF8, "application/json");
            using var response = await _http.SendAsync(request, cancellationToken);
            await EnsureSuccessAsync(response, cancellationToken);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            throw BridgeUnavailable(ex);
        }
    }

    private HttpRequestMessage CreateRequest(HttpMethod method, string path)
    {
        var request = new HttpRequestMessage(method, $"http://127.0.0.1:{_settings.BridgePort}{path}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _settings.BridgeToken);
        return request;
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        var detail = TryReadError(body);
        throw new InvalidOperationException($"Discord bridge returned {(int)response.StatusCode}: {detail}. Restart Discord once so it loads the updated NoiseToggle bridge.");
    }

    private static string TryReadError(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return "no error details";
        }

        try
        {
            using var document = JsonDocument.Parse(body);
            if (document.RootElement.TryGetProperty("error", out var error))
            {
                return error.GetString() ?? body;
            }
        }
        catch
        {
            // Return the raw body below.
        }

        return body.Length > 180 ? body[..180] + "..." : body;
    }

    private static InvalidOperationException BridgeUnavailable(Exception inner)
    {
        return new InvalidOperationException("Discord bridge is not running. Restart Discord once so Vencord loads the NoiseToggle bridge, then try the toggle again.", inner);
    }

    private sealed class BridgeState
    {
        public bool KrispEnabled { get; set; }
    }
}
