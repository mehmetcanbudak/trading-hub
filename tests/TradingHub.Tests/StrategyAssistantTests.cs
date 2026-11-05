using System.Text;
using System.Text.Json;
using TradingHub.App.Services;

namespace TradingHub.Tests;

[CollectionDefinition("StrategyAssistant", DisableParallelization = true)]
public sealed class StrategyAssistantCollection { }

public sealed class UnixAssistantFactAttribute : FactAttribute
{
    public UnixAssistantFactAttribute() { if (OperatingSystem.IsWindows()) Skip = "Spawned POSIX fixture; Windows shim resolution has separate cross-platform coverage."; }
}

[Collection("StrategyAssistant")]
public sealed class StrategyAssistantTests
{
    [Fact]
    public void LegacyHistoryAndSettingsStayClaudeAndUnknownEngineCannotBeResumed()
    {
        var legacy = JsonSerializer.Deserialize<ChatConversation>("{\"Id\":\"old\",\"SessionId\":\"claude-only\",\"Messages\":[]}")!;
        Assert.Equal("claude", legacy.Engine);
        Assert.Equal("claude", JsonSerializer.Deserialize<AppSettings>("{}")!.StrategyChatEngine);
        var unknown = JsonSerializer.Deserialize<ChatConversation>("{\"Engine\":\"future\",\"SessionId\":\"foreign\"}")!;
        Assert.False(StrategyChatEngines.IsKnown(unknown.Engine));
        Assert.Throws<ArgumentException>(() => StrategyChatEngines.Create(unknown.Engine));
        var claude = StrategyChatEngines.Create("claude"); var codex = StrategyChatEngines.Create("codex");
        claude.Resume(legacy.SessionId); codex.Resume("codex-only");
        Assert.Equal("claude-only", claude.SessionId); Assert.Equal("codex-only", codex.SessionId);
        Assert.Throws<ArgumentException>(() => codex.Resume("--last"));
        Assert.Throws<ArgumentException>(() => claude.Resume("../foreign-session"));
        Assert.Equal("codex-only", codex.SessionId);
        codex.NewConversation(); Assert.Null(codex.SessionId); Assert.Equal("claude-only", claude.SessionId);
    }

    [Fact]
    public void RequestCapturesDirectoriesAndBillingPreservesUnknownVersusReportedZero()
    {
        var directories = new List<string> { "first" };
        var request = new StrategyChatRequest("prompt", "workspace", "instructions", extraDirectories: directories);
        directories[0] = "changed";
        Assert.Equal("first", request.ExtraDirectories[0]);
        var unknown = new ChatMessage { Role = "assistant", Text = "Done" };
        var zero = new ChatMessage { Role = "assistant", Text = "Done", ReportedCostUsd = 0 };
        Assert.Null(JsonSerializer.Deserialize<ChatMessage>(JsonSerializer.Serialize(unknown))!.ReportedCostUsd);
        Assert.Equal(0d, JsonSerializer.Deserialize<ChatMessage>(JsonSerializer.Serialize(zero))!.ReportedCostUsd);
    }

    [UnixAssistantFact]
    public async Task CodexConsumesLargeUnicodeStdinAndReportsTokensWithoutInventingCost()
    {
        using var fixture = new CliFixture("codex");
        fixture.Reply("{\"type\":\"thread.started\",\"thread_id\":\"codex-owned\"}\n{\"type\":\"item.completed\",\"item\":{\"id\":\"m1\",\"type\":\"agent_message\",\"text\":\"Edited both files.\"}}\n{\"type\":\"turn.completed\",\"usage\":{\"input_tokens\":71,\"cached_input_tokens\":12,\"output_tokens\":8}}\n");
        string prompt = string.Concat(Enumerable.Repeat("Unicode: İstanbul 東京 😀 \"quoted\" & $HOME\n", 5000));
        var result = await fixture.Bridge.SendAsync(new(prompt, fixture.Root, "owned instructions", true, model: "custom-model", effort: "high")).WaitAsync(TimeSpan.FromSeconds(15));
        Assert.Equal(StrategyChatStatus.Succeeded, result.Status);
        Assert.Equal("Edited both files.", result.Text); Assert.Null(result.CostUsd);
        Assert.Equal(new StrategyTokenUsage(71, 12, 8), result.Usage);
        Assert.Equal("codex-owned", fixture.Bridge.SessionId);
        Assert.EndsWith(prompt, File.ReadAllText(Path.Combine(fixture.Root, "stdin.txt")));
        var arguments = File.ReadAllText(Path.Combine(fixture.Root, "args.txt")).Split('\0', StringSplitOptions.RemoveEmptyEntries);
        Assert.Contains("--ignore-user-config", arguments); Assert.Contains("--ignore-rules", arguments);
        Assert.Contains("approval_policy=\"never\"", arguments); Assert.Contains("sandbox_workspace_write.network_access=false", arguments);
        Assert.Contains("features.hooks=false", arguments);
        Assert.DoesNotContain("--last", arguments);
    }

    [UnixAssistantFact]
    public async Task ExactResumeFailureNeverStartsANewThreadAndNewConversationClearsIt()
    {
        using var fixture = new CliFixture("codex");
        fixture.Bridge.Resume("exact-session");
        fixture.Reply("{\"type\":\"turn.failed\",\"error\":{\"message\":\"Session not found\"}}\n");
        var result = await fixture.Send(); Assert.Equal(StrategyChatStatus.Failed, result.Status);
        Assert.Equal("exact-session", fixture.Bridge.SessionId);
        var arguments = File.ReadAllText(Path.Combine(fixture.Root, "args.txt")).Split('\0', StringSplitOptions.RemoveEmptyEntries);
        int resume = Array.IndexOf(arguments, "resume"); Assert.True(resume >= 0); Assert.Equal("exact-session", arguments[resume + 1]);
        fixture.Reply("{\"type\":\"thread.started\",\"thread_id\":\"wrong-session\"}\n{\"type\":\"turn.completed\"}\n");
        Assert.Equal(StrategyChatStatus.Failed, (await fixture.Send()).Status);
        Assert.Equal("exact-session", fixture.Bridge.SessionId);
        fixture.Bridge.NewConversation(); Assert.Null(fixture.Bridge.SessionId);
    }

    [UnixAssistantFact]
    public async Task ToolCompletionMalformedOrEmptyOutputCannotBecomeSuccessfulTurn()
    {
        using var fixture = new CliFixture("codex");
        foreach (string output in new[] { "", "not-json\n", "{\"type\":\"item.completed\",\"item\":{\"id\":\"tool\",\"type\":\"command_execution\",\"status\":\"completed\"}}\n" })
        {
            fixture.Reply(output); Assert.Equal(StrategyChatStatus.Failed, (await fixture.Send()).Status);
        }
        fixture.Reply("{\"type\":\"turn.completed\"}\n", exitCode: 7);
        Assert.Equal(StrategyChatStatus.Failed, (await fixture.Send()).Status);
    }

    [UnixAssistantFact]
    public async Task AuthenticationQuotaModelAndPermissionFailuresRemainDistinct()
    {
        using var fixture = new CliFixture("codex");
        foreach (var (message, login) in new[] { ("Not logged in. Run codex login.", true), ("Quota exhausted", false), ("Invalid model custom-id", false), ("Permission denied by sandbox", false), ("MCP startup failed", false) })
        {
            fixture.Reply(JsonSerializer.Serialize(new { type = "turn.failed", error = new { message } }) + "\n");
            var result = await fixture.Send(); Assert.Equal(StrategyChatStatus.Failed, result.Status); Assert.Equal(message, result.Text); Assert.Equal(login, result.NeedsLogin);
        }
    }

    [UnixAssistantFact]
    public async Task UnsupportedIsolationFailsBeforePromptAndStderrPressureDoesNotDeadlock()
    {
        using var fixture = new CliFixture("codex");
        File.WriteAllText(Path.Combine(fixture.Root, "help.txt"), "--json --sandbox\n");
        var unsupported = await fixture.Send(); Assert.Equal(StrategyChatStatus.Failed, unsupported.Status);
        Assert.Contains("--ignore-user-config", unsupported.Text); Assert.False(File.Exists(Path.Combine(fixture.Root, "stdin.txt")));
        fixture.WriteHelp();
        fixture.Reply("{\"type\":\"turn.completed\"}\n");
        File.WriteAllText(Path.Combine(fixture.Root, "pressure"), "yes");
        Assert.Equal(StrategyChatStatus.Succeeded, (await fixture.Send()).Status);
    }

    [UnixAssistantFact]
    public async Task CancelDuringCapabilityReadAndTurnRestoresEditableSessionWithoutLogin()
    {
        using var fixture = new CliFixture("codex");
        foreach (string phase in new[] { "help-delay", "turn-delay" })
        {
            File.WriteAllText(Path.Combine(fixture.Root, phase), "yes");
            using var cancellation = new CancellationTokenSource();
            var pending = fixture.Bridge.SendAsync(new("fixture", fixture.Root, "instructions"), cancellation.Token);
            await WaitForFile(Path.Combine(fixture.Root, phase + "-started"));
            cancellation.Cancel();
            var result = await pending.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(StrategyChatStatus.Cancelled, result.Status); Assert.False(result.NeedsLogin); Assert.False(fixture.Bridge.IsRunning);
            File.Delete(Path.Combine(fixture.Root, phase));
        }
        fixture.Bridge.NewConversation(); Assert.Null(fixture.Bridge.SessionId);
    }

    [UnixAssistantFact]
    public async Task ClaudeReportedZeroAndMissingCostAreNotConflated()
    {
        using var fixture = new CliFixture("claude");
        fixture.Reply("{\"type\":\"result\",\"result\":\"ok\",\"is_error\":false,\"total_cost_usd\":0}\n");
        Assert.Equal(0d, (await fixture.Send()).CostUsd);
        fixture.Reply("{\"type\":\"result\",\"result\":\"ok\",\"is_error\":false}\n");
        Assert.Null((await fixture.Send()).CostUsd);
    }
    [UnixAssistantFact]
    public async Task BrokenPromptPipeFailsInsteadOfReportingSuccessfulOutput()
    {
        using var fixture = new CliFixture("codex");
        File.WriteAllText(Path.Combine(fixture.Root, "close-stdin"), "yes");
        var result = await fixture.Bridge.SendAsync(new(new string('x', 2_000_000), fixture.Root, "instructions")).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(StrategyChatStatus.Failed, result.Status);
        Assert.False(fixture.Bridge.IsRunning);
    }

    [UnixAssistantFact]
    public async Task ResearchRevocationCancelsOwnedRequestAndStaleApprovalCannotLaunch()
    {
        string root = Path.Combine(Path.GetTempPath(), "assistant-research-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var transport = new ResearchTransport();
            await using var research = new TradingViewResearchService(Path.Combine(root, "state.json"), new EmptySecrets(),
                (_, _) => Task.FromResult<ITradingViewResearchTransport>(transport));
            var review = await research.ConnectAndReviewAsync();
            var access = await research.ApproveAsync(review, new[] { "get_ohlcv" });
            using var fixture = new CliFixture("codex", research);
            File.WriteAllText(Path.Combine(fixture.Root, "turn-delay"), "yes");
            var pending = fixture.Bridge.SendAsync(new("fixture", fixture.Root, "instructions", tradingView: access));
            await WaitForFile(Path.Combine(fixture.Root, "turn-delay-started"));
            await research.RevokeAsync();
            var cancelled = await pending.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(StrategyChatStatus.Cancelled, cancelled.Status);
            Assert.False(cancelled.NeedsLogin); Assert.False(fixture.Bridge.IsRunning);
            File.Delete(Path.Combine(fixture.Root, "turn-delay-started"));
            var rejected = await fixture.Bridge.SendAsync(new("fixture", fixture.Root, "instructions", tradingView: access));
            Assert.Equal(StrategyChatStatus.Failed, rejected.Status);
            Assert.False(File.Exists(Path.Combine(fixture.Root, "turn-delay-started")));
            Assert.Equal(0, transport.ToolCalls);
        }
        finally { Directory.Delete(root, true); }
    }
    [UnixAssistantFact]
    public async Task EveryEngineLabResearchCombinationExposesOnlyItsExactLocalServersAndTools()
    {
        string[] expectedLab =
        [
            "list_strategies", "compile_strategy", "list_data", "download_data", "run_backtest", "run_optimization",
            "run_walk_forward", "list_runs", "analyze_portfolio", "suggest_portfolio", "live_portfolio", "propose_sizing",
            "save_report", "list_indicators", "compile_indicator", "chart_snapshot",
        ];
        string root = Path.Combine(Path.GetTempPath(), "assistant-policy-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var transport = new ResearchTransport();
            await using var research = new TradingViewResearchService(Path.Combine(root, "state.json"), new EmptySecrets(),
                (_, _) => Task.FromResult<ITradingViewResearchTransport>(transport));
            var review = await research.ConnectAndReviewAsync();
            var access = await research.ApproveAsync(review, new[] { "get_ohlcv" });
            foreach (string engine in new[] { "claude", "codex" })
            {
                using var fixture = new CliFixture(engine, research);
                foreach (bool lab in new[] { false, true })
                foreach (bool enabled in new[] { false, true })
                {
                    fixture.Reply(engine == "claude" ? "{\"type\":\"result\",\"result\":\"ok\",\"is_error\":false}\n" : "{\"type\":\"turn.completed\"}\n");
                    var result = await fixture.Bridge.SendAsync(new("fixture", fixture.Root, "instructions", lab, tradingView: enabled ? access : null));
                    Assert.Equal(StrategyChatStatus.Succeeded, result.Status);
                    var args = File.ReadAllText(Path.Combine(fixture.Root, "args.txt")).Split('\0', StringSplitOptions.RemoveEmptyEntries);
                    if (engine == "claude")
                    {
                        Assert.Contains("--strict-mcp-config", args);
                        using var config = JsonDocument.Parse(args[Array.IndexOf(args, "--mcp-config") + 1]);
                        var servers = config.RootElement.GetProperty("mcpServers").EnumerateObject().Select(p => p.Name).ToArray();
                        Assert.Equal((lab ? 1 : 0) + (enabled ? 1 : 0), servers.Length);
                        Assert.Equal(lab, servers.Contains("tradinghub")); Assert.Equal(enabled, servers.Contains("tradingview_research"));
                        var expected = (lab ? expectedLab.Select(t => "mcp__tradinghub__" + t) : Array.Empty<string>())
                            .Concat(enabled ? new[] { "mcp__tradingview_research__get_ohlcv" } : Array.Empty<string>()).ToArray();
                        int allowed = Array.IndexOf(args, "--allowedTools");
                        if (expected.Length == 0) Assert.Equal(-1, allowed);
                        else Assert.Equal(expected, args[allowed + 1].Split(','));
                    }
                    else
                    {
                        string config = Assert.Single(args, a => a.StartsWith("mcp_servers=", StringComparison.Ordinal));
                        if (!lab && !enabled) Assert.Equal("mcp_servers={}", config);
                        Assert.Equal(lab, config.Contains("\"tradinghub\"=", StringComparison.Ordinal));
                        Assert.Equal(enabled, config.Contains("\"tradingview_research\"=", StringComparison.Ordinal));
                        if (lab) Assert.Contains("\"enabled_tools\"=" + JsonSerializer.Serialize(expectedLab), config);
                        if (enabled) Assert.Contains("\"enabled_tools\"=[\"get_ohlcv\"]", config);
                        Assert.DoesNotContain("mcp__tradingview", config);
                    }
                }
            }
            Assert.Equal(0, transport.ToolCalls);
        }
        finally { Directory.Delete(root, true); }
    }

    [UnixAssistantFact]
    public async Task PineHandoffUsesSelectedRealBridgeWithNoExecutionToolsOrAutomaticSourcePublication()
    {
        foreach (string engine in new[] { "claude", "codex" })
        {
            using var fixture = new CliFixture(engine);
            fixture.Reply(engine == "claude" ? "{\"type\":\"result\",\"result\":\"Unvalidated C# draft\",\"is_error\":false}\n"
                : "{\"type\":\"item.completed\",\"item\":{\"id\":\"draft\",\"type\":\"agent_message\",\"text\":\"Unvalidated C# draft\"}}\n{\"type\":\"turn.completed\"}\n");
            var source = new PineImportSource(Path.Combine(fixture.Root, "kept.pine"), "kept.pine", "plot(close)", "original-byte-hash");
            File.WriteAllText(source.Path, source.Text);
            var preview = PineImports.Preview(source, engine, "selected-model", "high");
            var result = await PineAssistantHandoff.SendAsync(preview, true, fixture.Bridge);
            Assert.Equal(StrategyChatStatus.Succeeded, result!.Status);
            Assert.Contains("plot(close)", File.ReadAllText(Path.Combine(fixture.Root, "stdin.txt")));
            var arguments = File.ReadAllText(Path.Combine(fixture.Root, "args.txt")).Split('\0');
            Assert.Contains("selected-model", arguments);
            Assert.DoesNotContain("--add-dir", arguments);
            Assert.DoesNotContain("--resume", arguments);
            if (engine == "claude")
            {
                Assert.Equal("", arguments[Array.IndexOf(arguments, "--tools") + 1]);
                Assert.Contains("--restricted", arguments);
                Assert.Contains("--safe-mode", arguments);
                Assert.Contains("--no-session-persistence", arguments);
                using var config = JsonDocument.Parse(arguments[Array.IndexOf(arguments, "--mcp-config") + 1]);
                Assert.Empty(config.RootElement.GetProperty("mcpServers").EnumerateObject());
            }
            else
            {
                Assert.Equal("read-only", arguments[Array.IndexOf(arguments, "--sandbox") + 1]);
                Assert.Contains("features.shell_tool=false", arguments);
                Assert.Contains("features.apps=false", arguments);
                Assert.Contains("mcp_servers={}", arguments);
            }
            Assert.Equal("plot(close)", File.ReadAllText(source.Path));
            Assert.Empty(Directory.EnumerateFiles(fixture.Root, "*.cs", SearchOption.AllDirectories));
        }
    }

    [UnixAssistantFact]
    public async Task MissingCodexTextOnlyFeatureControlRejectsBeforeAnySourceEgress()
    {
        using var fixture = new CliFixture("codex");
        File.WriteAllText(Path.Combine(fixture.Root, "features.txt"), "unrelated stable true\n");
        var result = await fixture.Bridge.SendAsync(new("private source", fixture.Root, "instructions", textOnly: true));
        Assert.Equal(StrategyChatStatus.Failed, result.Status);
        Assert.Contains("shell_tool", result.Text);
        Assert.False(File.Exists(Path.Combine(fixture.Root, "stdin.txt")));
    }

    [UnixAssistantFact]
    public async Task AppShutdownAndCanceledPineHandoffRetainOwnershipUntilChildExitAndHeldStderrDrain()
    {
        using var fixture = new CliFixture("codex");
        File.WriteAllText(Path.Combine(fixture.Root, "hold-stderr"), "owned fixture");
        File.WriteAllText(Path.Combine(fixture.Root, "turn-delay"), "hold active CLI");
        var owner = new PineHandoffRegistry();
        var source = new PineImportSource(Path.Combine(fixture.Root, "source.pine"), "source.pine", "plot(close)", "fixture");
        File.WriteAllText(source.Path, source.Text);
        Task<StrategyChatResult?> pending = owner.RunAsync(cancellation => PineAssistantHandoff.SendAsync(
            PineImports.Preview(source, "codex", null, null), true, fixture.Bridge, cancellation));
        try
        {
            await WaitForFile(Path.Combine(fixture.Root, "held-stderr-pid"));
            await WaitForFile(Path.Combine(fixture.Root, "held-cli-pid"));
            await WaitForFile(Path.Combine(fixture.Root, "held-workspace"));
            using var process = System.Diagnostics.Process.GetProcessById(int.Parse(File.ReadAllText(Path.Combine(fixture.Root, "held-cli-pid")), System.Globalization.CultureInfo.InvariantCulture));
            string workspace = File.ReadAllText(Path.Combine(fixture.Root, "held-workspace")).TrimEnd('\r', '\n');
            Task shutdown = owner.StopAsync();
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Throws<ObjectDisposedException>(() => { _ = owner.RunAsync(_ => Task.CompletedTask); });
            Assert.False(pending.IsCompleted);
            Assert.False(shutdown.IsCompleted);
            Assert.True(fixture.Bridge.IsRunning);
            Assert.True(Directory.Exists(workspace));
            File.WriteAllText(Path.Combine(fixture.Root, "release-stderr"), "drain now");
            var result = await pending.WaitAsync(TimeSpan.FromSeconds(15));
            await shutdown.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(StrategyChatStatus.Cancelled, result!.Status);
            Assert.False(fixture.Bridge.IsRunning);
            Assert.False(Directory.Exists(workspace));
            Assert.True(File.Exists(Path.Combine(fixture.Root, "held-stderr-done")));
            Assert.Equal("plot(close)", File.ReadAllText(source.Path));
        }
        finally
        {
            File.WriteAllText(Path.Combine(fixture.Root, "release-stderr"), "release owned helper");
            Task shutdown = owner.StopAsync();
            if (File.Exists(Path.Combine(fixture.Root, "held-stderr-pid"))) await WaitForFile(Path.Combine(fixture.Root, "held-stderr-done"));
            await shutdown.WaitAsync(TimeSpan.FromSeconds(20));
            await pending.WaitAsync(TimeSpan.FromSeconds(20));
        }
    }


    private static async Task WaitForFile(string path)
    {
        var until = DateTime.UtcNow.AddSeconds(10);
        while (!File.Exists(path) && DateTime.UtcNow < until) await Task.Delay(10);
        Assert.True(File.Exists(path), "The owned CLI did not reach the requested fixture phase.");
    }

    private sealed class EmptySecrets : ISecretStore
    {
        public string Get(string account) => "";
        public void Set(string account, string? secret) { Assert.True(string.IsNullOrEmpty(secret)); }
    }
    private sealed class ResearchTransport : ITradingViewResearchTransport
    {
        public int ToolCalls { get; private set; }
        public event Action? CatalogChanged;
        public event Action? Disconnected;
        public void ChangeCatalog() => CatalogChanged?.Invoke();
        public void Disconnect() => Disconnected?.Invoke();
        public Task<TradingViewToolPage> ListToolsAsync(string? cursor, CancellationToken token) =>
            Task.FromResult(new TradingViewToolPage([JsonSerializer.SerializeToElement(new { name = "get_ohlcv", description = "Fixture market research", inputSchema = new { type = "object", properties = new { } } })], null));
        public Task<JsonElement> CallToolAsync(string name, JsonElement arguments, CancellationToken token)
        {
            ToolCalls++;
            throw new InvalidOperationException("Assistant process tests must not call remote tools.");
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }


    private sealed class CliFixture : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "assistant Unicode İstanbul 'quoted' " + Guid.NewGuid().ToString("N"));
        public IStrategyChatBridge Bridge { get; }
        private readonly string _variable;
        private readonly string? _previous;
        public CliFixture(string engine, TradingViewResearchService? research = null)
        {
            Directory.CreateDirectory(Root);
            string cli = Path.Combine(Root, "fake-" + engine);
            File.WriteAllText(cli, "#!/bin/sh\nroot=$(dirname \"$0\")\ncase \" $* \" in *' --help '*) if [ -f \"$root/help-delay\" ]; then printf started > \"$root/help-delay-started\"; /bin/sleep 30; fi; /bin/cat \"$root/help.txt\"; exit 0;; *' features list '*) /bin/cat \"$root/features.txt\"; exit 0;; esac\nif [ -f \"$root/hold-stderr\" ]; then printf '%s' \"$$\" > \"$root/held-cli-pid.tmp\"; /bin/mv \"$root/held-cli-pid.tmp\" \"$root/held-cli-pid\"; /bin/pwd > \"$root/held-workspace.tmp\"; /bin/mv \"$root/held-workspace.tmp\" \"$root/held-workspace\"; /bin/sh -c '/bin/sh \"$1/stderr-holder\" \"$1\" \"$$\" >/dev/null &' holder \"$root\"; fi\nif [ -f \"$root/turn-delay\" ]; then printf started > \"$root/turn-delay-started\"; /bin/sleep 30; fi\nif [ -f \"$root/close-stdin\" ]; then exec 0<&-; exit 9; fi\nprintf '%s\\0' \"$@\" > \"$root/args.txt\"\n/bin/cat > \"$root/stdin.txt\"\nif [ -f \"$root/pressure\" ]; then i=0; while [ $i -lt 6000 ]; do printf 'diagnostic-pressure-xxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx\\n' >&2; i=$((i+1)); done; fi\n/bin/cat \"$root/reply.jsonl\"\nexit $(/bin/cat \"$root/exit.txt\")\n", new UTF8Encoding(false));
            File.SetUnixFileMode(cli, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            File.WriteAllText(Path.Combine(Root, "stderr-holder"), "#!/bin/sh\nroot=$1\nformer=$2\nwhile [ \"$(/bin/ps -o ppid= -p \"$$\" | /usr/bin/tr -d ' ')\" = \"$former\" ]; do /bin/sleep 0.02; done\nprintf '%s' \"$$\" > \"$root/held-stderr-pid.tmp\"\n/bin/mv \"$root/held-stderr-pid.tmp\" \"$root/held-stderr-pid\"\nwhile [ ! -f \"$root/release-stderr\" ]; do /bin/sleep 0.02; done\nprintf 'owned stderr fully drained\\n' >&2\nprintf done > \"$root/held-stderr-done\"\n", new UTF8Encoding(false));
            WriteHelp(); Reply("");
            File.WriteAllText(Path.Combine(Root, "features.txt"), "shell_tool stable true\napps stable true\n");
            _variable = engine == "codex" ? "TRADINGHUB_CODEX_CLI" : "TRADINGHUB_CLAUDE_CLI";
            _previous = Environment.GetEnvironmentVariable(_variable); Environment.SetEnvironmentVariable(_variable, cli);
            Bridge = engine == "codex" ? new CodexBridge(research) : new ClaudeCodeBridge(research);
        }
        public void WriteHelp() => File.WriteAllText(Path.Combine(Root, "help.txt"), "--ignore-user-config --ignore-rules --sandbox --json --effort VALUE --add-dir DIR --restricted --safe-mode --tools LIST --no-session-persistence\n");
        public void Reply(string json, int exitCode = 0) { File.WriteAllText(Path.Combine(Root, "reply.jsonl"), json); File.WriteAllText(Path.Combine(Root, "exit.txt"), exitCode.ToString(System.Globalization.CultureInfo.InvariantCulture)); }
        public Task<StrategyChatResult> Send() => Bridge.SendAsync(new("fixture", Root, "instructions")).WaitAsync(TimeSpan.FromSeconds(15));
        public void Dispose() { Bridge.Cancel(); Environment.SetEnvironmentVariable(_variable, _previous); Directory.Delete(Root, true); }
    }
}
