using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace NoiseToggle;

internal sealed class WaveLinkClient : IAsyncDisposable
{
    private readonly ClientWebSocket _socket = new();
    private readonly ConcurrentDictionary<int, TaskCompletionSource<JsonElement>> _pending = new();
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private Task? _receiver;
    private int _mutationId = 100;

    public WaveLinkClient(string host, int port)
    {
        Host = host;
        Port = port;
        _socket.Options.SetRequestHeader("Origin", "streamdeck://");
        _socket.Options.SetRequestHeader("Host", $"{host}:{port}");
    }

    private string Host { get; }
    private int Port { get; }

    public async Task ConnectAsync(CancellationToken cancellationToken)
    {
        await _socket.ConnectAsync(new Uri($"ws://{Host}:{Port}/"), cancellationToken);
        _receiver = ReceiveLoopAsync(_lifetime.Token);
    }

    public async Task<WaveLinkState> GetStateAsync(CancellationToken cancellationToken)
    {
        var channelsResult = await RequestAsync(3, "getChannels", null, cancellationToken);
        var mixesResult = await RequestAsync(4, "getMixes", null, cancellationToken);
        var channels = ExtractCollection(ExtractResult(channelsResult), "channels")
            .Deserialize<List<WaveChannel>>(JsonOptions) ?? [];
        var mixes = ExtractCollection(ExtractResult(mixesResult), "mixes")
            .Deserialize<List<WaveMix>>(JsonOptions) ?? [];
        return new WaveLinkState(channels, mixes);
    }

    public async Task SetChannelMixLevelAsync(
        string channelId, string mixId, decimal level, CancellationToken cancellationToken)
    {
        var id = Interlocked.Increment(ref _mutationId);
        var parameters = new
        {
            id = channelId,
            mixes = new[] { new { id = mixId, level = Math.Clamp(level, 0m, 1m) } }
        };
        await RequestAsync(id, "setChannel", parameters, cancellationToken);
    }

    private async Task<JsonElement> RequestAsync(
        int id, string method, object? parameters, CancellationToken cancellationToken)
    {
        var completion = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_pending.TryAdd(id, completion))
            throw new InvalidOperationException($"Wave Link request {id} is already pending.");

        var request = parameters is null
            ? JsonSerializer.Serialize(new { id, jsonrpc = "2.0", method })
            : JsonSerializer.Serialize(new { id, jsonrpc = "2.0", method, @params = parameters });
        try
        {
            await _sendLock.WaitAsync(cancellationToken);
            try
            {
                await _socket.SendAsync(Encoding.UTF8.GetBytes(request), WebSocketMessageType.Text, true, cancellationToken);
            }
            finally
            {
                _sendLock.Release();
            }

            return await completion.Task.WaitAsync(TimeSpan.FromSeconds(4), cancellationToken);
        }
        finally
        {
            _pending.TryRemove(id, out _);
        }
    }

    private async Task ReceiveLoopAsync(CancellationToken cancellationToken)
    {
        var buffer = new byte[64 * 1024];
        try
        {
            while (!cancellationToken.IsCancellationRequested && _socket.State == WebSocketState.Open)
            {
                using var message = new MemoryStream();
                WebSocketReceiveResult result;
                do
                {
                    result = await _socket.ReceiveAsync(buffer, cancellationToken);
                    if (result.MessageType == WebSocketMessageType.Close)
                        return;
                    message.Write(buffer, 0, result.Count);
                } while (!result.EndOfMessage);

                if (result.MessageType != WebSocketMessageType.Text)
                    continue;
                using var document = JsonDocument.Parse(message.ToArray());
                var root = document.RootElement;
                if (root.TryGetProperty("id", out var idElement) &&
                    idElement.TryGetInt32(out var id) && _pending.TryGetValue(id, out var pending))
                    pending.TrySetResult(root.Clone());
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            foreach (var pending in _pending.Values)
                pending.TrySetException(ex);
        }
        finally
        {
            foreach (var pending in _pending.Values)
                pending.TrySetException(new IOException("Wave Link disconnected."));
        }
    }

    private static JsonElement ExtractResult(JsonElement response)
    {
        if (response.TryGetProperty("error", out var error))
            throw new InvalidOperationException($"Wave Link error: {error}");
        return response.TryGetProperty("result", out var result)
            ? result
            : throw new InvalidOperationException("Wave Link returned no result.");
    }

    private static JsonElement ExtractCollection(JsonElement result, string propertyName)
    {
        if (result.ValueKind == JsonValueKind.Array)
            return result;
        if (result.ValueKind == JsonValueKind.Object && result.TryGetProperty(propertyName, out var collection))
            return collection;
        throw new InvalidOperationException($"Wave Link returned an unexpected {propertyName} payload.");
    }

    public async ValueTask DisposeAsync()
    {
        _lifetime.Cancel();
        if (_socket.State == WebSocketState.Open)
        {
            try { await _socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "NoiseToggle closing", CancellationToken.None); }
            catch { }
        }
        if (_receiver is not null)
        {
            try { await _receiver; } catch { }
        }
        _socket.Dispose();
        _lifetime.Dispose();
        _sendLock.Dispose();
    }

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };
}
