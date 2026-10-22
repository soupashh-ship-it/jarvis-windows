using System.Net;
using System.Net.Http;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Jarvis.Core;

/// <summary>
/// Exposes Jarvis's own policy-gated tools to Antigravity over a loopback-only MCP endpoint.
/// The Antigravity CLI uses this endpoint for native function calls; tool execution still goes
/// through AgentSession, PolicyEngine, and the normal approval UI.
/// </summary>
public sealed class JarvisMcpServer : IAsyncDisposable
{
    public const string ServerUrl = "http://127.0.0.1:8767/mcp";
    public static readonly string SessionToken = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
    private const string ListenerPrefix = "http://127.0.0.1:8767/";
    private const int MaxRequestBytes = 2 * 1024 * 1024;
    private const int MaxConcurrentRequests = 16;
    private static readonly TimeSpan RequestBodyTimeout = TimeSpan.FromSeconds(10);

    private readonly HttpListener _listener = new();
    private readonly ToolRegistry _tools;
    private readonly EventBus _events;
    private readonly Func<string, JsonElement, CancellationToken, Task<ToolResult>> _execute;
    private readonly SemaphoreSlim _executionGate = new(1, 1);
    private readonly SemaphoreSlim _requestGate = new(MaxConcurrentRequests, MaxConcurrentRequests);
    private readonly ConcurrentDictionary<long, Task> _requests = new();
    private CancellationTokenSource? _shutdown;
    private Task? _acceptLoop;
    private int _announcedConnection;
    private long _requestId;
    private static readonly HttpClient HealthClient = CreateHealthClient();

    private static HttpClient CreateHealthClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", SessionToken);
        return client;
    }

    public JarvisMcpServer(ToolRegistry tools, EventBus events,
        Func<string, JsonElement, CancellationToken, Task<ToolResult>> execute)
    {
        _tools = tools;
        _events = events;
        _execute = execute;
    }

    public bool IsRunning => _listener.IsListening;

    public static async Task VerifyAvailableAsync(CancellationToken ct = default)
    {
        try
        {
            using var response = await HealthClient.GetAsync("http://127.0.0.1:8767/health", ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                throw new ModelException("Jarvis's local tool bridge is not responding. Restart Jarvis before asking it to use tools.");
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
            var root = document.RootElement;
            if (!root.TryGetProperty("service", out var service) || service.GetString() != "jarvis-mcp" ||
                !root.TryGetProperty("ready", out var ready) || !ready.GetBoolean() ||
                !root.TryGetProperty("tools", out var tools) || tools.GetInt32() <= 0)
                throw new ModelException("Jarvis's local tool bridge did not pass its readiness check.");
        }
        catch (ModelException) { throw; }
        catch (Exception ex)
        {
            throw new ModelException("Jarvis's local tool bridge is unavailable. Restart Jarvis and try again.", ex);
        }
    }

    public static async Task WaitForClientConnectionAsync(CancellationToken ct = default)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(8);
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                using var response = await HealthClient.GetAsync("http://127.0.0.1:8767/health", ct).ConfigureAwait(false);
                if (response.IsSuccessStatusCode)
                {
                    using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
                    if (document.RootElement.TryGetProperty("connected", out var connected) && connected.GetBoolean())
                        return;
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch { }
            await Task.Delay(150, ct).ConfigureAwait(false);
        }
        throw new ModelException("Antigravity CLI started but did not connect to Jarvis's tool bridge, so requested actions are unavailable. Check agy's MCP configuration and restart Jarvis.");
    }

    public Task StartAsync(CancellationToken ct = default)
    {
        if (_listener.IsListening) return Task.CompletedTask;
        _listener.Prefixes.Add(ListenerPrefix);
        try
        {
            _listener.Start();
        }
        catch
        {
            _listener.Prefixes.Clear();
            throw;
        }

        _shutdown = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _acceptLoop = Task.Run(() => AcceptLoopAsync(_shutdown.Token), CancellationToken.None);
        Log.Info($"Jarvis MCP tools listening on {ServerUrl} ({_tools.Count} tools).");
        _events.Emit("tool_bridge_ready", "Jarvis tools are ready for Antigravity.", true,
            ("tools", _tools.Count), ("url", ServerUrl));
        return Task.CompletedTask;
    }

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            HttpListenerContext context;
            try
            {
                context = await _listener.GetContextAsync().WaitAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (HttpListenerException) when (ct.IsCancellationRequested || !_listener.IsListening) { break; }
            catch (ObjectDisposedException) { break; }
            catch (Exception ex)
            {
                if (!ct.IsCancellationRequested) Log.Warn("Jarvis MCP listener failed", ex);
                break;
            }

            var requestId = Interlocked.Increment(ref _requestId);
            if (!_requestGate.Wait(0))
            {
                try
                {
                    context.Response.StatusCode = (int)HttpStatusCode.ServiceUnavailable;
                    context.Response.Close();
                }
                catch { try { context.Response.Abort(); } catch { } }
                continue;
            }

            var requestTask = HandleSafelyAndReleaseAsync(context, ct);
            _requests[requestId] = requestTask;
            _ = requestTask.ContinueWith(completed => _requests.TryRemove(requestId, out _),
                CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }
    }

    private async Task HandleSafelyAndReleaseAsync(HttpListenerContext context, CancellationToken ct)
    {
        try { await HandleSafelyAsync(context, ct).ConfigureAwait(false); }
        finally { _requestGate.Release(); }
    }

    private async Task HandleSafelyAsync(HttpListenerContext context, CancellationToken ct)
    {
        try
        {
            if (!context.Request.IsLocal || !IsLoopbackOrigin(context.Request.Headers["Origin"]) ||
                !HasValidSessionToken(context.Request.Headers["Authorization"]))
            {
                context.Response.StatusCode = (int)HttpStatusCode.Forbidden;
                context.Response.Close();
                return;
            }

            if (string.Equals(context.Request.HttpMethod, "GET", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(context.Request.Url?.AbsolutePath.TrimEnd('/'), "/health", StringComparison.Ordinal))
            {
                await WriteJsonAsync(context.Response, new JsonObject
                {
                    ["service"] = "jarvis-mcp",
                ["ready"] = IsRunning,
                ["connected"] = Volatile.Read(ref _announcedConnection) != 0,
                    ["tools"] = _tools.Count
                }, ct).ConfigureAwait(false);
                return;
            }

            if (!string.Equals(context.Request.Url?.AbsolutePath.TrimEnd('/'), "/mcp", StringComparison.Ordinal))
            {
                context.Response.StatusCode = (int)HttpStatusCode.NotFound;
                context.Response.Close();
                return;
            }

            if (string.Equals(context.Request.HttpMethod, "DELETE", StringComparison.OrdinalIgnoreCase))
            {
                context.Response.StatusCode = (int)HttpStatusCode.OK;
                context.Response.Close();
                return;
            }

            if (!string.Equals(context.Request.HttpMethod, "POST", StringComparison.OrdinalIgnoreCase))
            {
                context.Response.Headers["Allow"] = "POST, DELETE";
                context.Response.StatusCode = (int)HttpStatusCode.MethodNotAllowed;
                context.Response.Close();
                return;
            }

            if (context.Request.ContentLength64 > MaxRequestBytes)
            {
                context.Response.StatusCode = (int)HttpStatusCode.RequestEntityTooLarge;
                context.Response.Close();
                return;
            }

            string body;
            using (var bodyTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                bodyTimeout.CancelAfter(RequestBodyTimeout);
                try
                {
                    body = await ReadRequestBodyAsync(context.Request, bodyTimeout.Token).ConfigureAwait(false);
                }
                catch (RequestTooLargeException)
                {
                    context.Response.StatusCode = (int)HttpStatusCode.RequestEntityTooLarge;
                    context.Response.Close();
                    return;
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    context.Response.StatusCode = (int)HttpStatusCode.RequestTimeout;
                    context.Response.Close();
                    return;
                }
            }

            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("method", out var methodElement) ||
                methodElement.ValueKind != JsonValueKind.String)
            {
                await WriteJsonAsync(context.Response, RpcError(
                    root.ValueKind == JsonValueKind.Object && root.TryGetProperty("id", out var invalidId)
                        ? invalidId : default,
                    -32600, "Invalid JSON-RPC request."), ct).ConfigureAwait(false);
                return;
            }

            var method = methodElement.GetString() ?? "";
            var hasId = root.TryGetProperty("id", out var id);
            if (!hasId)
            {
                // MCP notifications have no response body. They still complete the HTTP exchange.
                context.Response.StatusCode = (int)HttpStatusCode.Accepted;
                context.Response.Close();
                return;
            }

            var result = await DispatchAsync(method, root, ct).ConfigureAwait(false);
            if (result.ErrorCode is { } code)
                await WriteJsonAsync(context.Response, RpcError(id, code, result.ErrorMessage ?? "Request failed."), ct)
                    .ConfigureAwait(false);
            else
                await WriteJsonAsync(context.Response, RpcSuccess(id, result.Value ?? new JsonObject()), ct)
                    .ConfigureAwait(false);
        }
        catch (JsonException ex)
        {
            try { await WriteJsonAsync(context.Response, RpcError(default, -32700, "Parse error: " + ex.Message), ct).ConfigureAwait(false); }
            catch { try { context.Response.Abort(); } catch { } }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            try { context.Response.Abort(); } catch { }
        }
        catch (Exception ex)
        {
            Log.Warn("Jarvis MCP request failed", ex);
            try { await WriteJsonAsync(context.Response, RpcError(default, -32603, "Internal server error."), ct).ConfigureAwait(false); }
            catch { try { context.Response.Abort(); } catch { } }
        }
    }

    private static async Task<string> ReadRequestBodyAsync(HttpListenerRequest request, CancellationToken ct)
    {
        using var content = new MemoryStream();
        var buffer = new byte[81920];
        while (true)
        {
            var read = await request.InputStream.ReadAsync(buffer.AsMemory(), ct).ConfigureAwait(false);
            if (read == 0) break;
            if (content.Length + read > MaxRequestBytes) throw new RequestTooLargeException();
            content.Write(buffer, 0, read);
        }

        return request.ContentEncoding.GetString(content.GetBuffer(), 0, (int)content.Length);
    }

    private sealed class RequestTooLargeException : Exception { }

    private async Task<DispatchResult> DispatchAsync(string method, JsonElement root, CancellationToken ct)
    {
        if (method == "ping") return DispatchResult.Success(new JsonObject());

        if (method == "initialize")
        {
            var protocol = "2025-06-18";
            if (root.TryGetProperty("params", out var initParams) &&
                initParams.TryGetProperty("protocolVersion", out var version) &&
                version.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(version.GetString()))
                protocol = version.GetString()!;

            return DispatchResult.Success(new JsonObject
            {
                ["protocolVersion"] = protocol,
                ["capabilities"] = new JsonObject
                {
                    ["tools"] = new JsonObject { ["listChanged"] = false }
                },
                ["serverInfo"] = new JsonObject { ["name"] = "jarvis", ["version"] = "1.0.0" },
                ["instructions"] = "Jarvis computer-control tools. Every call passes through Jarvis's own safety policy and approval flow."
            });
        }

        if (method == "tools/list")
        {
            if (Interlocked.Exchange(ref _announcedConnection, 1) == 0)
            {
                Log.Info("Antigravity connected to Jarvis MCP tools.");
                _events.Emit("tool_bridge_connected", "Antigravity connected to Jarvis tools.", true,
                    ("tools", _tools.Count));
            }

            var listed = new JsonArray();
            foreach (var tool in _tools.All)
            {
                listed.Add(new JsonObject
                {
                    ["name"] = tool.Name,
                    ["description"] = tool.Description,
                    ["inputSchema"] = JsonNode.Parse(tool.ParametersJson)
                });
            }
            return DispatchResult.Success(new JsonObject { ["tools"] = listed });
        }

        if (method == "tools/call")
        {
            if (!root.TryGetProperty("params", out var callParams) ||
                !callParams.TryGetProperty("name", out var nameElement) ||
                nameElement.ValueKind != JsonValueKind.String)
                return DispatchResult.Error(-32602, "Tool name is required.");

            var name = nameElement.GetString()!;
            var arguments = callParams.TryGetProperty("arguments", out var args) && args.ValueKind != JsonValueKind.Null
                ? args : JsonSerializer.SerializeToElement(new Dictionary<string, string>());
            if (arguments.ValueKind != JsonValueKind.Object)
                return DispatchResult.Error(-32602, "Tool arguments must be a JSON object.");

            await _executionGate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                var result = await _execute(name, arguments, ct).ConfigureAwait(false);
                var content = new JsonArray { new JsonObject { ["type"] = "text", ["text"] = result.Content } };
                if (!string.IsNullOrWhiteSpace(result.ImageBase64))
                    content.Add(new JsonObject
                    {
                        ["type"] = "image",
                        ["data"] = result.ImageBase64,
                        ["mimeType"] = string.IsNullOrWhiteSpace(result.ImageMime) ? "image/jpeg" : result.ImageMime
                    });

                return DispatchResult.Success(new JsonObject
                {
                    ["content"] = content,
                    ["isError"] = !result.Success
                });
            }
            finally { _executionGate.Release(); }
        }

        return DispatchResult.Error(-32601, $"Method '{method}' is not supported.");
    }

    private static bool IsLoopbackOrigin(string? origin)
    {
        if (string.IsNullOrWhiteSpace(origin)) return true;
        return Uri.TryCreate(origin, UriKind.Absolute, out var uri) && uri.IsLoopback;
    }

    private static bool HasValidSessionToken(string? authorization)
    {
        const string prefix = "Bearer ";
        if (authorization is null || !authorization.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return false;
        var supplied = Encoding.UTF8.GetBytes(authorization[prefix.Length..].Trim());
        var expected = Encoding.UTF8.GetBytes(SessionToken);
        return supplied.Length == expected.Length && CryptographicOperations.FixedTimeEquals(supplied, expected);
    }

    private static JsonObject RpcSuccess(JsonElement id, JsonNode result) => new()
    {
        ["jsonrpc"] = "2.0",
        ["id"] = id.ValueKind == JsonValueKind.Undefined ? null : JsonNode.Parse(id.GetRawText()),
        ["result"] = result
    };

    private static JsonObject RpcError(JsonElement id, int code, string message) => new()
    {
        ["jsonrpc"] = "2.0",
        ["id"] = id.ValueKind == JsonValueKind.Undefined ? null : JsonNode.Parse(id.GetRawText()),
        ["error"] = new JsonObject { ["code"] = code, ["message"] = message }
    };

    private static async Task WriteJsonAsync(HttpListenerResponse response, JsonObject body, CancellationToken ct)
    {
        var bytes = Encoding.UTF8.GetBytes(body.ToJsonString());
        response.StatusCode = (int)HttpStatusCode.OK;
        response.ContentType = "application/json; charset=utf-8";
        response.ContentLength64 = bytes.Length;
        response.KeepAlive = false;
        await response.OutputStream.WriteAsync(bytes.AsMemory(), ct).ConfigureAwait(false);
        response.Close();
    }

    public async Task StopAsync()
    {
        if (_shutdown is null) return;
        try { _shutdown.Cancel(); } catch { }
        try { _listener.Stop(); } catch { }
        if (_acceptLoop is not null)
        {
            try { await _acceptLoop.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); }
            catch { }
        }
        var activeRequests = _requests.Values.ToArray();
        if (activeRequests.Length > 0)
        {
            try { await Task.WhenAll(activeRequests).WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false); }
            catch { }
        }
        try { _listener.Close(); } catch { }
        _shutdown.Dispose();
        _shutdown = null;
        _acceptLoop = null;
        _listener.Prefixes.Clear();
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        if (_requests.IsEmpty)
        {
            _executionGate.Dispose();
            _requestGate.Dispose();
        }
    }

    private sealed record DispatchResult(JsonNode? Value, int? ErrorCode, string? ErrorMessage)
    {
        public static DispatchResult Success(JsonNode value) => new(value, null, null);
        public static DispatchResult Error(int code, string message) => new(null, code, message);
    }
}
