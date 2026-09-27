using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AgentsDashboard.Core.Agents;

/// <summary>An error the other side answered a request with.</summary>
public sealed class JsonRpcException(int code, string message) : Exception(message)
{
    public int Code { get; } = code;
}

/// <summary>
/// JSON-RPC 2.0 over a pair of streams, one message per line.
/// </summary>
/// <remarks>
/// This is the framing ACP agents use over stdio: each message is one line of
/// JSON. Both sides send requests, so the connection answers requests as well as
/// making them. An incoming request is handled off the read loop, because the
/// one that matters most, a permission prompt, waits for a person and must not
/// stop the updates that arrive meanwhile.
/// </remarks>
public sealed class JsonRpcConnection : IAsyncDisposable
{
    public const int MethodNotFound = -32601;
    public const int InternalError = -32603;

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly StreamReader _reader;
    private readonly Stream _output;
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly ConcurrentDictionary<long, TaskCompletionSource<JsonElement>> _pending = new();
    private readonly CancellationTokenSource _stop = new();
    private long _nextId;
    private Task? _loop;
    private int _closed;
    private int _disposed;

    public JsonRpcConnection(Stream input, Stream output)
    {
        _reader = new StreamReader(input, new UTF8Encoding(false));
        _output = output;
    }

    /// <summary>A notification from the other side: the method and its params.</summary>
    public event Action<string, JsonElement>? Notified;

    /// <summary>Raised once when the other side goes away, with the reason when there is one.</summary>
    public event Action<Exception?>? Closed;

    /// <summary>
    /// Answers a request from the other side. Returns the result to send back;
    /// throwing a <see cref="JsonRpcException"/> sends that error instead. Without
    /// a handler every request is answered "method not found".
    /// </summary>
    public Func<string, JsonElement, CancellationToken, Task<object?>>? RequestHandler { get; set; }

    public bool IsClosed => Volatile.Read(ref _closed) != 0;

    public void Start() => _loop ??= Task.Run(ReadLoopAsync);

    /// <summary>Sends a request and waits for its answer.</summary>
    public async Task<JsonElement> RequestAsync(string method, object? parameters, CancellationToken ct = default)
    {
        if (IsClosed)
        {
            throw new IOException("The connection is closed.");
        }

        var id = Interlocked.Increment(ref _nextId);
        var answer = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = answer;

        try
        {
            await WriteAsync(new { jsonrpc = "2.0", id, method, @params = parameters }, ct).ConfigureAwait(false);
            using (ct.Register(() => answer.TrySetCanceled(ct)))
            {
                return await answer.Task.ConfigureAwait(false);
            }
        }
        finally
        {
            _pending.TryRemove(id, out _);
        }
    }

    /// <summary>Sends a notification, which has no answer.</summary>
    public Task NotifyAsync(string method, object? parameters, CancellationToken ct = default) =>
        WriteAsync(new { jsonrpc = "2.0", method, @params = parameters }, ct);

    private async Task WriteAsync(object message, CancellationToken ct)
    {
        var line = JsonSerializer.Serialize(message, Options) + "\n";
        var bytes = Encoding.UTF8.GetBytes(line);

        await _writeGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await _output.WriteAsync(bytes, ct).ConfigureAwait(false);
            await _output.FlushAsync(ct).ConfigureAwait(false);
        }
        catch (ObjectDisposedException e)
        {
            // The process's stream was closed under the write, which is how a
            // dying agent looks on Windows as often as a broken pipe does. Either
            // way the other side is gone, and callers handle that as IOException.
            throw new IOException("The connection is closed.", e);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    private async Task ReadLoopAsync()
    {
        Exception? reason = null;
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                var line = await _reader.ReadLineAsync(_stop.Token).ConfigureAwait(false);
                if (line is null)
                {
                    break;
                }

                if (line.Length > 0)
                {
                    Dispatch(line);
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException)
        {
            reason = e;
        }

        Close(reason);
    }

    private void Dispatch(string line)
    {
        JsonElement message;
        try
        {
            using var doc = JsonDocument.Parse(line);
            message = doc.RootElement.Clone();
        }
        catch (JsonException)
        {
            // Not a protocol message. Agents sometimes print a stray line; the
            // protocol is carried by the ones that parse.
            return;
        }

        if (message.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        var hasMethod = message.TryGetProperty("method", out var methodElement)
            && methodElement.ValueKind == JsonValueKind.String;
        var hasId = message.TryGetProperty("id", out var id) && id.ValueKind is JsonValueKind.Number or JsonValueKind.String;
        var parameters = message.TryGetProperty("params", out var p) ? p : default;

        if (hasMethod && hasId)
        {
            _ = AnswerAsync(id.Clone(), methodElement.GetString()!, parameters);
            return;
        }

        if (hasMethod)
        {
            Notified?.Invoke(methodElement.GetString()!, parameters);
            return;
        }

        if (hasId && id.ValueKind == JsonValueKind.Number && id.TryGetInt64(out var ours)
            && _pending.TryGetValue(ours, out var answer))
        {
            if (message.TryGetProperty("error", out var error))
            {
                var code = error.TryGetProperty("code", out var c) && c.TryGetInt32(out var n) ? n : InternalError;
                var text = error.TryGetProperty("message", out var m) ? m.GetString() ?? "" : "";
                answer.TrySetException(new JsonRpcException(code, text));
            }
            else
            {
                answer.TrySetResult(message.TryGetProperty("result", out var result) ? result : default);
            }
        }
    }

    private async Task AnswerAsync(JsonElement id, string method, JsonElement parameters)
    {
        object reply;
        try
        {
            if (RequestHandler is not { } handler)
            {
                throw new JsonRpcException(MethodNotFound, $"{method} is not supported.");
            }

            var result = await handler(method, parameters, _stop.Token).ConfigureAwait(false);
            reply = new { jsonrpc = "2.0", id, result = result ?? new { } };
        }
        catch (JsonRpcException e)
        {
            reply = new { jsonrpc = "2.0", id, error = new { code = e.Code, message = e.Message } };
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            reply = new { jsonrpc = "2.0", id, error = new { code = InternalError, message = e.Message } };
        }
        catch (OperationCanceledException)
        {
            return;
        }

        try
        {
            await WriteAsync(reply, _stop.Token).ConfigureAwait(false);
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException or OperationCanceledException)
        {
            // The other side is gone; there is nobody to answer.
        }
    }

    private void Close(Exception? reason)
    {
        if (Interlocked.Exchange(ref _closed, 1) != 0)
        {
            return;
        }

        foreach (var answer in _pending.Values)
        {
            answer.TrySetException(new IOException("The agent process closed the connection.", reason));
        }

        Closed?.Invoke(reason);
    }

    public async ValueTask DisposeAsync()
    {
        // Disposed both by whoever notices the process die and by the host when
        // it shuts down, and those can race. The second must not touch _stop.
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        await _stop.CancelAsync().ConfigureAwait(false);
        Close(null);
        if (_loop is not null)
        {
            try
            {
                await _loop.ConfigureAwait(false);
            }
            catch (Exception e) when (e is OperationCanceledException or IOException)
            {
            }
        }

        _stop.Dispose();
        _writeGate.Dispose();
    }
}
