using System.Text.Json;

namespace TradingHub.App.Services;

/// <summary>A narrow JSON-RPC stdio surface. No generic remote method forwarding, GUI or trading startup.</summary>
public static class TradingViewResearchMcp
{
    public static async Task<int> RunAsync(string[] args)
    {
        int index = Array.IndexOf(args, "--approval");
        if (index < 0 || index + 1 >= args.Length || !Guid.TryParseExact(args[index + 1], "N", out _))
        {
            await Console.Error.WriteLineAsync("Research MCP requires --approval <reviewed approval id>.");
            return 2;
        }
        await using var service = new TradingViewResearchService(Path.Combine(AppPaths.Root, "tradingview-research.json"), Keychain.Store);
        return await RunSessionAsync(service, args[index + 1], Console.In, Console.Out).ConfigureAwait(false);
    }

    /// <summary>The same enforcing stdio dispatcher is exercised with fake resources in tests and the UI scene.</summary>
    public static async Task<int> RunSessionAsync(TradingViewResearchService service, string approvalId,
        TextReader input, TextWriter output, CancellationToken cancellationToken = default)
    {
        using var writes = new SemaphoreSlim(1, 1);
        var requests = new Dictionary<string, CancellationTokenSource>(StringComparer.Ordinal);
        var requestsGate = new object();
        var jobs = new List<Task>();
        bool initialized = false;
        async Task SendAsync(object message)
        {
            // Accepted requests retain their one reply even when session cancellation stops input.
            await writes.WaitAsync().ConfigureAwait(false);
            try { await output.WriteLineAsync(JsonSerializer.Serialize(message)).ConfigureAwait(false); await output.FlushAsync().ConfigureAwait(false); }
            finally { writes.Release(); }
        }
        async Task ErrorAsync(object? id, int code, string message) => await SendAsync(new { jsonrpc = "2.0", id, error = new { code, message } }).ConfigureAwait(false);
        async Task HandleAsync(JsonElement message, JsonElement id, CancellationToken token)
        {
            object? result;
            try
            {
                string? method = message.GetProperty("method").GetString();
                var parameters = message.TryGetProperty("params", out var p) ? p : default;
                if (method == "initialize")
                {
                    if (initialized) { await ErrorAsync(id, -32600, "Already initialized.").ConfigureAwait(false); return; }
                    string? requested = parameters.ValueKind == JsonValueKind.Object && parameters.TryGetProperty("protocolVersion", out var version) ? version.GetString() : null;
                    string negotiated = requested is "2024-11-05" or "2025-03-26" or "2025-06-18" or "2025-11-25" ? requested : "2025-06-18";
                    initialized = true;
                    result = new { protocolVersion = negotiated, capabilities = new { tools = new { listChanged = false } },
                        serverInfo = new { name = "Trading Hub Research Boundary", version = "1.0" } };
                }
                else if (!initialized) { await ErrorAsync(id, -32002, "Initialize the research boundary first.").ConfigureAwait(false); return; }
                else if (method == "ping") result = new { };
                else if (method == "tools/list")
                {
                    if (parameters.ValueKind == JsonValueKind.Object && parameters.TryGetProperty("cursor", out _))
                        throw new TradingViewResearchException("This filtered catalog has no additional page.");
                    var tools = await service.GetApprovedToolsAsync(approvalId, token).ConfigureAwait(false);
                    result = new { tools = tools.Select(t => t.Definition).ToArray() };
                }
                else if (method == "tools/call")
                {
                    if (parameters.ValueKind != JsonValueKind.Object || !parameters.TryGetProperty("name", out var toolName)
                        || toolName.ValueKind != JsonValueKind.String)
                    { await ErrorAsync(id, -32602, "Tool name and object arguments are required.").ConfigureAwait(false); return; }
                    string name = toolName.GetString()!;
                    JsonElement arguments = parameters.TryGetProperty("arguments", out var a) ? a : JsonSerializer.SerializeToElement(new { });
                    if (arguments.ValueKind != JsonValueKind.Object)
                    { await ErrorAsync(id, -32602, "Tool arguments must be an object.").ConfigureAwait(false); return; }
                    result = await service.ForwardAsync(approvalId, name, arguments, token).ConfigureAwait(false);
                }
                else { await ErrorAsync(id, -32601, "Method not exposed by the research boundary.").ConfigureAwait(false); return; }
                await SendAsync(new { jsonrpc = "2.0", id, result }).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                await ErrorAsync(id, -32001, TradingViewResearchService.SafeError(ex)).ConfigureAwait(false);
            }
        }
        try
        {
            while (await input.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                JsonElement message;
                try { using var document = JsonDocument.Parse(line); message = document.RootElement.Clone(); }
                catch (JsonException) { await ErrorAsync(null, -32700, "Invalid JSON.").ConfigureAwait(false); continue; }
                if (message.ValueKind != JsonValueKind.Object || !message.TryGetProperty("jsonrpc", out var rpc)
                    || rpc.ValueKind != JsonValueKind.String || rpc.GetString() != "2.0"
                    || !message.TryGetProperty("method", out var method) || method.ValueKind != JsonValueKind.String)
                { await ErrorAsync(null, -32600, "Invalid JSON-RPC request.").ConfigureAwait(false); continue; }
                if (!message.TryGetProperty("id", out var id))
                {
                    if (method.GetString() == "notifications/cancelled" && message.TryGetProperty("params", out var p)
                        && p.ValueKind == JsonValueKind.Object && p.TryGetProperty("requestId", out var requestId))
                    {
                        lock (requestsGate)
                        {
                            if (requests.TryGetValue(requestId.GetRawText(), out var pending)) pending.Cancel();
                        }
                    }
                    continue;
                }
                if (id.ValueKind is not (JsonValueKind.String or JsonValueKind.Number))
                { await ErrorAsync(null, -32600, "Request id must be a string or number.").ConfigureAwait(false); continue; }
                var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                string key = id.GetRawText();
                bool added;
                lock (requestsGate) added = requests.TryAdd(key, linked);
                if (!added)
                { linked.Dispose(); await ErrorAsync(id, -32600, "Duplicate request id.").ConfigureAwait(false); continue; }
                async Task DispatchAsync(JsonElement request, JsonElement requestId, string requestKey, CancellationTokenSource cts)
                {
                    try { await HandleAsync(request, requestId, cts.Token).ConfigureAwait(false); }
                    finally
                    {
                        // Lookup/cancel and remove/dispose share ownership; a completed source cannot escape this gate.
                        lock (requestsGate)
                        {
                            requests.Remove(requestKey);
                            cts.Dispose();
                        }
                    }
                }
                jobs.RemoveAll(static task => task.IsCompletedSuccessfully);
                // Initialization completes before accepting dependent requests. Calls remain cancellable while reading stdin.
                if (method.GetString() == "initialize") await DispatchAsync(message, id, key, linked).ConfigureAwait(false);
                else jobs.Add(DispatchAsync(message, id, key, linked));
            }
            await Task.WhenAll(jobs).ConfigureAwait(false);
            return 0;
        }
        catch (OperationCanceledException)
        {
            lock (requestsGate)
            {
                foreach (var request in requests.Values) request.Cancel();
            }
            await Task.WhenAll(jobs).ConfigureAwait(false);
            return 0;
        }
    }
}
