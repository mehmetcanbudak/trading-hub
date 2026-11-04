using System.Collections.Immutable;
using System.Net;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using TradingHub.App.Services;
using TradingHub.App.Windows;

internal static class NonKvpTradingViewScene
{
    public static int Run(MainWindow window, Action<double> pump, string outDir)
    {
        Directory.CreateDirectory(outDir);
        var remote = new FakeResearchTransport();
        int connections = 0;
        var service = new TradingViewResearchService(Path.Combine(outDir, "research-fixture", "state.json"), new MemorySecrets(),
            (_, _) => { connections++; return Task.FromResult<ITradingViewResearchTransport>(remote); });
        var dialog = new TradingViewResearchWindow(service);
        var checks = new List<string>();
        void Check(bool condition, string name)
        {
            if (!condition) throw new InvalidOperationException("TradingView scene: " + name);
            checks.Add(name);
        }
        void Finish(Task task)
        {
            var until = DateTime.UtcNow.AddSeconds(20);
            while (!task.IsCompleted && DateTime.UtcNow < until) pump(.03);
            if (!task.IsCompleted) throw new TimeoutException("TradingView scene operation did not complete.");
            task.GetAwaiter().GetResult();
            pump(.15);
        }
        void Capture(string name)
        {
            pump(.25);
            using var frame = dialog.CaptureRenderedFrame();
            if (frame == null) throw new InvalidOperationException("TradingView settings did not render.");
            frame.Save(Path.Combine(outDir, name + ".png"));
        }
        T Named<T>(string name) where T : Control =>
            dialog.GetVisualDescendants().OfType<T>().Single(control => control.Name == name);
        void Click(string name)
        {
            var button = Named<Button>(name);
            Check(button.IsEffectivelyVisible && button.IsEnabled, name + " is an actionable rendered button");
            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            if (dialog.PendingOperation != null) Finish(dialog.PendingOperation);
        }
        try
        {
            Check(service.CaptureAccess() == null && connections == 0, "Off performs no network/discovery");
            dialog.Show(window);
            Capture("nonkvp-tradingview-off");
            Click("ResearchConnect");
            Check(remote.ListCount == 2 && remote.Calls == 0, "Both authenticated metadata pages discovered without tool/model execution");
            Check(service.CaptureAccess() == null, "Discovery does not approve");
            foreach (string toolName in new[] { "get_ohlcv", "get_active_watchlist" })
                Check(Named<CheckBox>("ResearchTool_" + toolName).GetVisualDescendants().OfType<TextBlock>()
                    .Any(label => label.IsEffectivelyVisible && label.Text == toolName), "Review renders exact tool identifier " + toolName);
            Capture("nonkvp-tradingview-review-denied-watchlist");
            Click("ResearchCancel");
            Check(service.CaptureAccess() == null, "Cancel keeps tools off");
            Click("ResearchReview");
            dialog.SetToolSelected("get_ohlcv", true);
            Click("ResearchApprove");
            var access = service.CaptureAccess();
            Check(access != null && access.ApprovedToolNames.SequenceEqual(["get_ohlcv"]), "Only selected research candidate approved");
            Capture("nonkvp-tradingview-enabled");
            string requests = string.Join('\n', new[] {
                "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"initialize\",\"params\":{\"protocolVersion\":\"2025-06-18\",\"capabilities\":{},\"clientInfo\":{\"name\":\"fixture\",\"version\":\"1\"}}}",
                "{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"tools/list\"}",
                "{\"jsonrpc\":\"2.0\",\"id\":3,\"method\":\"tools/call\",\"params\":{\"name\":\"get_ohlcv\",\"arguments\":{\"symbol\":\"FAKE:TEST\"}}}",
                "{\"jsonrpc\":\"2.0\",\"id\":4,\"method\":\"tools/call\",\"params\":{\"name\":\"get_active_watchlist\",\"arguments\":{}}}" });
            var output = new StringWriter();
            Finish(TradingViewResearchMcp.RunSessionAsync(service, access!.ApprovalId, new StringReader(requests), output));
            var replies = output.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(line => JsonDocument.Parse(line)).ToArray();
            try
            {
                Check(replies.Single(r => r.RootElement.GetProperty("id").GetInt32() == 3).RootElement.TryGetProperty("result", out _)
                    && replies.Single(r => r.RootElement.GetProperty("id").GetInt32() == 4).RootElement.TryGetProperty("error", out _)
                    && remote.Calls == 1, "Actual stdio dispatcher forwards approved research and denies readonly-labeled mutator before invocation");
                var listed = replies.Single(r => r.RootElement.GetProperty("id").GetInt32() == 2).RootElement.GetProperty("result").GetProperty("tools");
                Check(listed.GetArrayLength() == 1 && listed[0].GetProperty("name").GetString() == "get_ohlcv", "Filtered stdio catalog excludes all denied tools");
            }
            finally { foreach (var reply in replies) reply.Dispose(); }
            File.WriteAllText(Path.Combine(outDir, "nonkvp-tradingview-stdio.jsonl"), output.ToString());
            Click("ResearchOff");
            Check(service.CaptureAccess() == null, "Off revokes access immediately");
            remote.Error = new HttpRequestException("fixture", null, HttpStatusCode.TooManyRequests);
            Click("ResearchConnect");
            Check(Named<TextBlock>("ResearchStatus").Text!.Contains("429", StringComparison.Ordinal), "Rate limit visible without fabricated data");
            Capture("nonkvp-tradingview-rate-limit-error");
            File.WriteAllText(Path.Combine(outDir, "nonkvp-tradingview-results.json"), JsonSerializer.Serialize(new { checks, remoteCalls = remote.Calls, fakeOnly = true }));
            return 0;
        }
        finally
        {
            dialog.Close();
            Finish(service.DisposeAsync().AsTask());
        }
    }
    private sealed class MemorySecrets : ISecretStore
    {
        private readonly Dictionary<string, string> _values = new();
        public string Get(string account) => _values.GetValueOrDefault(account, "");
        public void Set(string account, string? secret) { if (string.IsNullOrEmpty(secret)) _values.Remove(account); else _values[account] = secret; }
    }
    private sealed class FakeResearchTransport : ITradingViewResearchTransport
    {
        public event Action? CatalogChanged { add { } remove { } }
        public event Action? Disconnected { add { } remove { } }
        public int Calls { get; private set; }
        public int ListCount { get; private set; }
        public Exception? Error { get; set; }
        public Task<TradingViewToolPage> ListToolsAsync(string? cursor, CancellationToken cancellationToken)
        {
            if (Error != null) throw Error;
            ListCount++;
            string name = cursor == null ? "get_ohlcv" : "get_active_watchlist";
            var definition = JsonSerializer.SerializeToElement(new { name,
                description = cursor == null ? "Fixture historical research bars." : "Activates or creates a watchlist despite its read-only annotation. DENIED.",
                inputSchema = new { type = "object", properties = new { symbol = new { type = "string" } } },
                annotations = new { readOnlyHint = true } });
            return Task.FromResult(new TradingViewToolPage(ImmutableArray.Create(definition), cursor == null ? "second" : null));
        }
        public Task<JsonElement> CallToolAsync(string name, JsonElement arguments, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(JsonSerializer.SerializeToElement(new { content = new[] { new { type = "text", text = "Fixture research data; no broker/account/model accessed." } } }));
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
