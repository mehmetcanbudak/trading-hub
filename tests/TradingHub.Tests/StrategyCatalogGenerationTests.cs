using System.Text.Json.Nodes;
using TradingHub.App.Lab;
using TradingHub.Data;
using TradingHub.Market;
using TradingHub.App.Services;
using TradingHub.Common;
using TradingHub.Scripting;
using TradingHub.Strategies;

namespace TradingHub.Tests;

[Collection("StrategyAssistant")]
public sealed class StrategyCatalogGenerationTests
{
    [Fact]
    public void CompileReturnsInitiatingBehaviorWhenChangedRecompilesSameFile()
    {
        Assert.False(string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("TRADINGHUB_DATA_DIR")),
            "Start the test process with an isolated data directory before resolving application paths.");
        Assert.False(string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("TRADINGHUB_STRATEGIES_DIR")),
            "Start the test process with an isolated strategies directory before constructing the catalog.");
        Assert.Equal(StorageLocations.CreateStore().Resolve().StrategiesRoot, AppPaths.Strategies);

        string root = Directory.CreateTempSubdirectory("tradinghub-catalog-generation-").FullName;
        string path = Path.Combine(AppPaths.Strategies, "CatalogGenerationMarker" + Guid.NewGuid().ToString("N") + ".cs");
        string previousStoreRoot = StrategyStore.Root;
        string? previousBacktests = Environment.GetEnvironmentVariable("TRADINGHUB_BACKTESTS_DIR");
        try
        {
            Environment.SetEnvironmentVariable("TRADINGHUB_BACKTESTS_DIR", Path.Combine(root, "backtests"));
            Directory.CreateDirectory(AppPaths.Strategies);
            File.WriteAllText(path, Source("A"));
            var catalog = new StrategyCatalog();
            var file = Assert.Single(catalog.Files, candidate => candidate.Path == path);
            bool recompiled = false;
            catalog.Changed += () =>
            {
                if (recompiled) return;
                recompiled = true;
                File.WriteAllText(path, Source("B"));
                catalog.Compile(file, force: true);
            };

            var initiating = catalog.Compile(file, force: true);
            var next = catalog.Compile(file);

            Assert.True(recompiled);
            Assert.Equal("B", Instantiate(next).ToString());
            Assert.Equal("A", Instantiate(initiating).ToString());
        }
        finally
        {
            StrategyStore.Root = previousStoreRoot;
            Environment.SetEnvironmentVariable("TRADINGHUB_BACKTESTS_DIR", previousBacktests);
            File.Delete(path);
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task OlderCompilationCompletesItsOwnFactoryWithoutReplacingNewerPublication()
    {
        using var fixture = new GenerationFixture();
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var compiler = new ScriptCompiler();
        var catalog = new StrategyCatalog((sources, name) =>
        {
            if (sources.Single().Text == Source("A"))
            {
                entered.Set();
                if (!release.Wait(TimeSpan.FromSeconds(60))) throw new TimeoutException("Held compilation was not released.");
            }
            return compiler.CompileSources(sources, name);
        });
        File.WriteAllText(fixture.Path, Source("A"));
        catalog.Refresh();
        var file = Assert.Single(catalog.Files, candidate => candidate.Path == fixture.Path);
        var first = Task.Run(() => catalog.Compile(file, force: true));
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(60)));
            File.WriteAllText(fixture.Path, Source("B"));
            var second = catalog.Compile(file, force: true);
            release.Set();
            var initiating = await first.WaitAsync(TimeSpan.FromSeconds(60));

            Assert.Equal("A", Instantiate(initiating).ToString());
            Assert.Equal("B", Instantiate(second).ToString());
            Assert.Equal("B", file.Generation!.Create().ToString());
            Assert.Equal(Source("A"), initiating.Generation!.Source.Text);
            Assert.Equal(Source("B"), file.Generation.Source.Text);
        }
        finally
        {
            release.Set();
            await first.WaitAsync(TimeSpan.FromSeconds(60));
        }
    }

    [Fact]
    public void UnchangedTimestampDoesNotReuseAnotherSourcesFactory()
    {
        using var fixture = new GenerationFixture();
        File.WriteAllText(fixture.Path, Source("A"));
        var catalog = new StrategyCatalog();
        var file = Assert.Single(catalog.Files, candidate => candidate.Path == fixture.Path);
        var first = catalog.Compile(file);
        var stamp = File.GetLastWriteTimeUtc(fixture.Path);
        File.WriteAllText(fixture.Path, Source("B"));
        File.SetLastWriteTimeUtc(fixture.Path, stamp);
        var second = catalog.Compile(file);

        Assert.Equal("A", Instantiate(first).ToString());
        Assert.Equal("B", Instantiate(second).ToString());
        Assert.NotEqual(first.Generation!.SourceDigest, second.Generation!.SourceDigest);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void UnavailableSourceReturnsFailedCompilationWithoutRunningAnEmptySource(bool unreadable)
    {
        using var fixture = new GenerationFixture();
        var compiler = new ScriptCompiler();
        int compilerCalls = 0;
        var catalog = new StrategyCatalog((sources, name) =>
        {
            compilerCalls++;
            return compiler.CompileSources(sources, name);
        });
        var file = Assert.Single(catalog.Files, candidate => candidate.Path == fixture.Path);
        var original = catalog.Compile(file);
        Assert.Equal("A", Instantiate(original).ToString());
        File.Delete(fixture.Path);
        if (unreadable) Directory.CreateDirectory(fixture.Path);
        try
        {
            var failed = catalog.Compile(file);
            Assert.False(failed.Success);
            Assert.Null(failed.Generation);
            Assert.Null(failed.Result.Assembly);
            Assert.Same(failed, file.LastCompile);
            Assert.Null(file.Generation);
            Assert.Contains(failed.Diagnostics, diagnostic => diagnostic.File == fixture.Path &&
                diagnostic.Severity == "error" && diagnostic.Id == "TH02");
            Assert.Equal(1, compilerCalls);
            Assert.Equal("A", Instantiate(original).ToString());
        }
        finally
        {
            if (unreadable) Directory.Delete(fixture.Path);
        }

        File.WriteAllText(fixture.Path, "");
        var empty = catalog.Compile(file);
        Assert.False(empty.Success);
        Assert.DoesNotContain(empty.Diagnostics, diagnostic => diagnostic.Id == "TH02");
        Assert.Equal(2, compilerCalls);
        File.WriteAllText(fixture.Path, Source("B"));
        Assert.Equal("B", Instantiate(catalog.Compile(file)).ToString());
        Assert.Equal(3, compilerCalls);
    }

    [Fact]
    public void SourceCaptureDoesNotSwallowUnexpectedCompilerFailures()
    {
        using var fixture = new GenerationFixture();
        var failure = new InvalidOperationException("The compiler failed outside source capture.");
        var catalog = new StrategyCatalog((_, _) => throw failure);
        var file = Assert.Single(catalog.Files, candidate => candidate.Path == fixture.Path);

        Assert.Same(failure, Assert.Throws<InvalidOperationException>(() => catalog.Compile(file)));
        Assert.Null(file.LastCompile);
    }

    [Fact]
    public async Task LaboratoryBacktestKeepsPreparedParametersAndExactSourceWhileFileAndPinsChange()
    {
        using var fixture = new GenerationFixture();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        string original = TradingSource(fixture.ClassName, "A");
        File.WriteAllText(fixture.Path, original);
        fixture.Pin(2);
        StrategyGenerationGate.Constructed = marker =>
        {
            if (marker != "A") return;
            entered.TrySetResult();
            if (!release.Wait(TimeSpan.FromSeconds(60))) throw new TimeoutException("Held strategy construction was not released.");
        };
        var request = fixture.Request("run_backtest");
        Task<JsonNode>? pending = null;
        try
        {
            pending = Task.Run(() => LabServer.CallAsync(request));
            await EnteredAsync(entered.Task, pending);
            File.WriteAllText(fixture.Path, TradingSource(fixture.ClassName, "B"));
            Assert.True(LabServer.CompileStrategy(fixture.ClassName)["ok"]!.GetValue<bool>());
            fixture.Pin(9);
            request["arguments"]!["parameters"]!["Units"] = 7;
            request["arguments"]!["capital"] = 50000;
            release.Set();
            var result = ToolResult(await pending.WaitAsync(TimeSpan.FromSeconds(60)));
            var run = StrategyStore.Load(fixture.ClassName, result["run_id"]!.GetValue<string>());

            Assert.NotNull(run);
            Assert.Equal("Generation A", run.StrategyName);
            Assert.Equal(2, Convert.ToInt32(result["parameters"]!["Units"]!.GetValue<string>()));
            Assert.Equal(10000m, run.Settings.InitialCapital);
            Assert.True(run.Metrics!.NetProfit > 0);
            Assert.Equal(original, StrategyStore.LoadCode(run));
            Assert.Equal(64, run.CodeHash.Length);

            StrategyGenerationGate.Constructed = null;
            var next = ToolResult(await LabServer.CallAsync(fixture.Request("run_backtest")).WaitAsync(TimeSpan.FromSeconds(60)));
            var nextRun = StrategyStore.Load(fixture.ClassName, next["run_id"]!.GetValue<string>());
            Assert.NotNull(nextRun);
            Assert.Equal("Generation B", nextRun.StrategyName);
            Assert.True(nextRun.Metrics!.NetProfit < 0);
            Assert.Equal(TradingSource(fixture.ClassName, "B"), StrategyStore.LoadCode(nextRun));
        }
        finally
        {
            StrategyGenerationGate.Constructed = null;
            release.Set();
            if (pending != null) await pending.WaitAsync(TimeSpan.FromSeconds(60));
        }
    }

    [Fact]
    public async Task LaboratoryOptimizationKeepsEveryFactoryAndSavedSourceFromItsCapturedGeneration()
    {
        using var fixture = new GenerationFixture();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        string original = TradingSource(fixture.ClassName, "A");
        File.WriteAllText(fixture.Path, original);
        StrategyGenerationGate.Initialized = marker =>
        {
            if (marker != "A") return;
            entered.TrySetResult();
            if (!release.Wait(TimeSpan.FromSeconds(60))) throw new TimeoutException("Held optimizer execution was not released.");
        };
        int combinations = Math.Max(8, Environment.ProcessorCount * 2);
        var request = fixture.Request("run_optimization");
        request["arguments"]!["ranges"] = new JsonObject
        {
            ["Units"] = new JsonObject { ["min"] = 1, ["max"] = combinations, ["step"] = 1 },
        };
        request["arguments"]!["top"] = combinations;
        request["arguments"]!["min_trades"] = 1;
        request = JsonNode.Parse(request.ToJsonString())!.AsObject();
        Task<JsonNode>? pending = null;
        try
        {
            pending = Task.Run(() => LabServer.CallAsync(request));
            await EnteredAsync(entered.Task, pending);
            File.WriteAllText(fixture.Path, TradingSource(fixture.ClassName, "B"));
            Assert.True(LabServer.CompileStrategy(fixture.ClassName)["ok"]!.GetValue<bool>());
            release.Set();
            var result = ToolResult(await pending.WaitAsync(TimeSpan.FromSeconds(60)));
            var rows = result["top"]!.AsArray();
            Assert.Equal(combinations, rows.Count);
            Assert.All(rows, row => Assert.True(row!["metrics"]!["net_profit"]!.GetValue<decimal>() > 0));
            var run = StrategyStore.Load(fixture.ClassName, result["run_id"]!.GetValue<string>());
            Assert.NotNull(run);
            Assert.Equal("Generation A", run.StrategyName);
            Assert.Equal(original, StrategyStore.LoadCode(run));
        }
        finally
        {
            StrategyGenerationGate.Initialized = null;
            release.Set();
            if (pending != null) await pending.WaitAsync(TimeSpan.FromSeconds(60));
        }
    }

    [Fact]
    public void SourceArtifactsKeepWhitespaceDistinctAndLegacyHashesUnchanged()
    {
        using var fixture = new GenerationFixture();
        const string firstSource = "alpha\n";
        const string secondSource = " \r\nalpha\r\n";
        var first = new StrategyRun { StrategyId = fixture.ClassName };
        var second = new StrategyRun { StrategyId = fixture.ClassName };
        StrategyStore.Save(first, firstSource);
        StrategyStore.Save(second, secondSource);

        Assert.Equal(64, first.CodeHash.Length);
        Assert.Equal(64, second.CodeHash.Length);
        Assert.NotEqual(first.CodeHash, second.CodeHash);
        Assert.Equal(firstSource, StrategyStore.LoadCode(StrategyStore.Load(first.StrategyId, first.Id)!));
        Assert.Equal(secondSource, StrategyStore.LoadCode(StrategyStore.Load(second.StrategyId, second.Id)!));

        var legacy = new StrategyRun { StrategyId = fixture.ClassName, CodeHash = "8ed3f6ad685b" };
        string codeDir = System.IO.Path.Combine(StrategyStore.Root, fixture.ClassName, "codigo");
        File.WriteAllText(System.IO.Path.Combine(codeDir, legacy.CodeHash + ".cs"), secondSource);
        StrategyStore.Save(legacy);
        var restored = StrategyStore.Load(legacy.StrategyId, legacy.Id)!;
        Assert.Equal("8ed3f6ad685b", restored.CodeHash);
        Assert.Equal(secondSource, StrategyStore.LoadCode(restored));
        Assert.Equal("8ed3f6ad685b", restored.CodeHash);
    }

    private static async Task EnteredAsync(Task entered, Task<JsonNode> pending)
    {
        var completed = await Task.WhenAny(entered, pending).WaitAsync(TimeSpan.FromSeconds(60));
        if (ReferenceEquals(completed, pending))
        {
            var result = ToolResult(await pending);
            Assert.Fail("The laboratory request returned before entering its actual strategy gate. " + result.ToJsonString());
        }
        Assert.True(entered.IsCompletedSuccessfully);
        await entered;
    }

    private static JsonNode ToolResult(JsonNode response)
    {
        string text = response["content"]![0]!["text"]!.GetValue<string>();
        Assert.False(response["isError"]!.GetValue<bool>(), text);
        return JsonNode.Parse(text)!;
    }

    private static string TradingSource(string className, string marker) => $$"""
        using TradingHub.Strategies;
        using TradingHub.Tests;

        [Strategy("Generation {{marker}}")]
        public sealed class {{className}} : Strategy
        {
            [Parameter("Units"), Optimize(1, 4, 1)]
            public int Units { get; set; } = 1;
            private bool _entered, _closed;
            public {{className}}() => StrategyGenerationGate.Constructed?.Invoke("{{marker}}");
            protected override void OnInit() => StrategyGenerationGate.Initialized?.Invoke("{{marker}}");
            protected override void OnBar(Bar bar)
            {
                if (!_entered)
                {
                    {{(marker == "A" ? "Buy" : "Sell")}}(Units);
                    _entered = true;
                }
                else if (!_closed)
                {
                    ClosePosition();
                    _closed = true;
                }
            }
        }
        """ + "\r\n ";

    private sealed class GenerationFixture : IDisposable
    {
        private readonly string _root = Directory.CreateTempSubdirectory("tradinghub-generation-").FullName;
        private readonly string _previousStoreRoot = StrategyStore.Root;
        private readonly string? _previousBacktests = Environment.GetEnvironmentVariable("TRADINGHUB_BACKTESTS_DIR");
        private readonly byte[]? _previousPins;
        private readonly string _ticksDirectory;
        public string ClassName { get; } = "Generation" + Guid.NewGuid().ToString("N");
        public string Path { get; }

        public GenerationFixture()
        {
            Assert.False(string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("TRADINGHUB_DATA_DIR")));
            Assert.False(string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("TRADINGHUB_STRATEGIES_DIR")));
            Path = System.IO.Path.Combine(AppPaths.Strategies, ClassName + ".cs");
            Directory.CreateDirectory(AppPaths.Strategies);
            File.WriteAllText(Path, Source("A"));
            Environment.SetEnvironmentVariable("TRADINGHUB_BACKTESTS_DIR", System.IO.Path.Combine(_root, "backtests"));
            _ = new StrategyCatalog();
            _previousPins = File.Exists(LabServer.PinsPath) ? File.ReadAllBytes(LabServer.PinsPath) : null;
            var instrument = new Instrument { FeedId = "generation-fixture", Symbol = ClassName.ToUpperInvariant(), TickSize = 1m, VolumeStep = 1m };
            _ticksDirectory = System.IO.Path.Combine(AppPaths.Ticks, instrument.FeedId, instrument.Symbol);
            var start = new DateTime(2026, 10, 1, 12, 0, 0, DateTimeKind.Utc);
            var ticks = Enumerable.Range(0, 12).Select(i => new Trade(i + 1, start.AddSeconds(i * 30), 100m + i * 10, 1m, TradeSide.Buy)).ToList();
            HistoricalData.WriteDay(AppPaths.Ticks, instrument, DateOnly.FromDateTime(start), ticks, replace: true);
        }

        public void Pin(int units)
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(LabServer.PinsPath)!);
            File.WriteAllText(LabServer.PinsPath, new JsonObject
            {
                [ClassName] = new JsonObject { ["Units"] = units },
            }.ToJsonString());
        }

        public JsonObject Request(string tool) => JsonNode.Parse(new JsonObject
        {
            ["name"] = tool,
            ["arguments"] = new JsonObject
            {
                ["file"] = ClassName, ["feed"] = "generation-fixture", ["symbol"] = ClassName.ToUpperInvariant(),
                ["from"] = "2026-10-01", ["to"] = "2026-10-01", ["period"] = "M1",
                ["capital"] = 10000, ["commission_percent"] = 0, ["slippage_ticks"] = 0,
                ["parameters"] = new JsonObject { ["Units"] = 1, ["StopLossTicks"] = 0, ["TakeProfitTicks"] = 0 },
            },
        }.ToJsonString())!.AsObject();

        public void Dispose()
        {
            if (_previousPins == null) File.Delete(LabServer.PinsPath);
            else File.WriteAllBytes(LabServer.PinsPath, _previousPins);
            StrategyStore.Root = _previousStoreRoot;
            Environment.SetEnvironmentVariable("TRADINGHUB_BACKTESTS_DIR", _previousBacktests);
            File.Delete(Path);
            if (Directory.Exists(_ticksDirectory)) Directory.Delete(_ticksDirectory, recursive: true);
            Directory.Delete(_root, recursive: true);
        }
    }

    private static Strategy Instantiate(StrategyCompilation result)
    {
        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));
        Assert.NotNull(result.Generation);
        return result.Generation.Create();
    }

    private static string Source(string marker) => $$"""
        using TradingHub.Strategies;

        public sealed class CatalogGenerationMarker : Strategy
        {
            public override string ToString() => "{{marker}}";
        }
        """;
}

public static class StrategyGenerationGate
{
    public static Action<string>? Constructed;
    public static Action<string>? Initialized;
}
