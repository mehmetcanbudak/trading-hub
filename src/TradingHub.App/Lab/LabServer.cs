using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using TradingHub.App.Services;
using TradingHub.Data;
using TradingHub.Market;
using TradingHub.Strategies;

namespace TradingHub.App.Lab;

/// <summary>
/// Laboratorio para Claude: Trading Hub corre como servidor MCP (JSON-RPC por stdin/stdout) cuando se abre con
/// <c>--mcp</c>. Claude Code lo usa para listar y compilar estrategias, ver y bajar datos, correr
/// backtests, optimizaciones, walk-forward y Monte Carlo por su cuenta, y guardar un reporte.
/// Todo lo que corre queda en el historial de la estrategia, igual que si lo corriera el usuario.
/// </summary>
internal static class LabServer
{
    private static StrategyCatalog? _catalog;
    private static TickData? _ticks;
    private static string? _ticksKey;
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public static async Task<int> RunAsync()
    {
        var stdout = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" };
        // cualquier Console.WriteLine de otra parte va a stderr: stdout es solo del protocolo
        Console.SetOut(Console.Error);
        var stdin = new StreamReader(Console.OpenStandardInput(), Encoding.UTF8);
        StrategyStore.Root = StoreRoot;

        string? line;
        while ((line = await stdin.ReadLineAsync()) != null)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            JsonNode? msg;
            try
            {
                msg = JsonNode.Parse(line);
            }
            catch
            {
                continue;
            }

            var id = msg?["id"];
            string? method = msg?["method"]?.GetValue<string>();
            if (id == null) continue; // notificación: no lleva respuesta

            JsonNode? result = null;
            (int Code, string Message)? error = null;
            try
            {
                switch (method)
                {
                    case "initialize":
                        result = Initialize(msg!["params"]);
                        break;
                    case "ping":
                        result = new JsonObject();
                        break;
                    case "tools/list":
                        result = new JsonObject { ["tools"] = Tools() };
                        break;
                    case "tools/call":
                        result = await CallAsync(msg!["params"]);
                        break;
                    default:
                        error = (-32601, $"Unknown method: {method}");
                        break;
                }
            }
            catch (Exception ex)
            {
                error = (-32603, ex.Message);
            }

            var resp = new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id.DeepClone() };
            if (error is { } e) resp["error"] = new JsonObject { ["code"] = e.Code, ["message"] = e.Message };
            else resp["result"] = result;
            await stdout.WriteLineAsync(resp.ToJsonString());
        }

        return 0;
    }

    private static JsonNode Initialize(JsonNode? p) => new JsonObject
    {
        ["protocolVersion"] = p?["protocolVersion"]?.GetValue<string>() ?? "2025-06-18",
        ["capabilities"] = new JsonObject { ["tools"] = new JsonObject() },
        ["serverInfo"] = new JsonObject { ["name"] = "tradinghub-lab", ["version"] = "1.0" },
        ["instructions"] = "Trading Hub laboratory: C# strategies, saved trade-level data, backtesting, optimization, walk-forward, Monte Carlo and reports.",
    };

    // ===================================================================== herramientas

    private static JsonObject Schema(params (string Name, string Type, string Description, bool Required)[] props)
    {
        var properties = new JsonObject();
        var required = new JsonArray();
        foreach (var (name, type, desc, req) in props)
        {
            properties[name] = new JsonObject { ["type"] = type, ["description"] = desc };
            if (req) required.Add(name);
        }

        return new JsonObject { ["type"] = "object", ["properties"] = properties, ["required"] = required };
    }

    private static readonly (string Name, string Type, string Description, bool Required)[] RangeProps =
    {
        ("file", "string", "Strategy filename without .cs (e.g. MovingAverageCrossover)", true),
        ("feed", "string", "Data connection (e.g. binance-futures); see list_data", true),
        ("symbol", "string", "Symbol (e.g. BTCUSDT)", true),
        ("from", "string", "From (YYYY-MM-DD, UTC)", true),
        ("to", "string", "To (YYYY-MM-DD, UTC, inclusive)", true),
        ("period", "string", "Bars: M1, M5, M15, H1, Tick500, Volume100, Range20… (default M5)", false),
        ("parameters", "object", "Fixed parameters {Name: value}; omitted parameters use defaults", false),
        ("capital", "number", "Initial capital (10000)", false),
        ("commission_percent", "number", "Commission as % of notional per side (0.04). For futures use 0 and commission_per_unit", false),
        ("commission_per_unit", "number", "Fixed commission per contract or unit per side (e.g. 2.5 for CME futures); added to percentage commission", false),
        ("slippage_ticks", "integer", "Slippage in ticks for market and stop orders (1)", false),
        ("timezone", "string", "Session time zone (e.g. America/New_York); defaults to UTC", false),
        ("session_start_hour", "number", "Local session start hour (0)", false),
    };

    private static JsonArray Tools()
    {
        JsonObject Tool(string name, string description, JsonObject schema) => new()
        {
            ["name"] = name,
            ["description"] = description,
            ["inputSchema"] = schema,
        };

        var optimizeProps = RangeProps.Concat(new[]
        {
            ("ranges", "object", "Ranges {Parameter: {min, max, step}}; defaults to the strategy's [Optimize] attributes", false),
            ("metric", "string", "Metric to maximize: NetProfit, ProfitFactor, Sharpe, Sortino, RecoveryFactor, NetProfitOverDrawdown, WinRate, Expectancy (default NetProfitOverDrawdown)", false),
            ("min_trades", "integer", "Minimum trades for a combination to qualify (10)", false),
        }).ToArray();

        return new JsonArray
        {
            Tool("list_strategies", "Lists strategy .cs files, compilation status, parameters (with [Optimize] ranges) and warmup: " +
                                    "indicators created in OnInit with their periods (e.g. ATR(14), including code constants) and the bars required to become ready " +
                                    "(with default parameters and, when different, [Optimize] maxima). Each walk-forward window starts without history and consumes these initial bars for warmup.", Schema()),
            Tool("compile_strategy", "Compiles a strategy and returns diagnostics (line and message), parameters and warmup requirements (indicator periods and required bars).", Schema(("file", "string", "Filename without .cs", true))),
            Tool("chart_snapshot", "Runs a strategy over a range and saves a chart image with bars, entries/exits, drawings and lines. Returns the PNG path for Read plus local-time bars, strategy line values, trades, drawings and logs. Use it to explain entry or exit timing.",
                Schema(("file", "string", "Strategy (filename without .cs)", true), ("feed", "string", "Connection (as listed by list_data)", true), ("symbol", "string", "Symbol", true),
                    ("period", "string", "Period: M1, M5, M15, H1…", true), ("from", "string", "From, local time: YYYY-MM-DD HH:mm (or date only)", true),
                    ("to", "string", "To, local time: YYYY-MM-DD HH:mm (date only includes the entire day)", true),
                    ("parameters", "object", "Fixed parameters {Name: value}; omitted parameters use defaults", false),
                    ("warmup_days", "number", "Prior days used for indicator warmup (default 3)", false),
                    ("timezone", "string", "Time zone (defaults to Trading Hub's setting)", false))),
            Tool("list_indicators", "User-defined indicators: files, classes and display names from the indicator directory.", Schema()),
            Tool("compile_indicator", "Compiles custom indicators and returns diagnostics for the requested file (line and message). Compiled indicators appear under Indicators → My indicators.",
                Schema(("file", "string", "Filename without .cs (in the indicator directory)", true))),
            Tool("list_data", "Saved trade-level data by instrument (available days). Backtests only use these data.", Schema()),
            Tool("download_data", "Downloads complete daily public tick archives (Binance Futures/Spot and Bybit; about 5 seconds per day). Maximum 62 days per call.",
                Schema(("feed", "string", "binance-futures, binance-spot or bybit-linear", true), ("symbol", "string", "Symbol", true),
                    ("from", "string", "First day YYYY-MM-DD", true), ("to", "string", "Last day YYYY-MM-DD (inclusive)", true))),
            Tool("run_backtest", "Trade-level backtest with commissions and slippage. Returns full metrics and the saved run ID.",
                Schema(RangeProps.Concat(new[]
                {
                    ("monte_carlo_runs", "integer", "If greater than 0, adds Monte Carlo trade reordering with this number of simulations", false),
                    ("include_trades", "boolean", "Include the first and last trades", false),
                }).ToArray())),
            Tool("run_optimization", "Parallel grid optimization. Returns the best parameter combinations. Beware of overfitting: validate with walk-forward.",
                Schema(optimizeProps.Concat(new[]
                {
                    ("top", "integer", "Number of combinations to return (10)", false),
                    ("max_combinations", "integer", "Maximum combinations (5000)", false),
                }).ToArray())),
            Tool("run_walk_forward", "Walk-forward: each window selects parameters using only that window, then tests them out of sample in the next. Runs each combination once " +
                                     "over the entire history (repeated ticks combined, indicators warmed as in live execution) and slices by window. Searches the full grid for ≤ 300 combinations " +
                                     "or uses Bayesian optimization (TPE) otherwise; selects the best plateau, not the highest peak, and measures overfitting with PBO and purged " +
                                     "combinatorial cross-validation (CPCV) paths. Returns stitched out-of-sample results. Repeating an identical request waits for the existing run; do not relaunch it.",
                Schema(optimizeProps.Concat(new[]
                {
                    ("in_sample_days", "integer", "Days per optimization window", true),
                    ("out_sample_days", "integer", "Days per out-of-sample window", true),
                    ("anchored", "boolean", "Windows anchored to the start (false = rolling)", false),
                    ("search", "string", "Search: auto (grid up to 300 combinations, otherwise Bayesian), grid or bayesian", false),
                    ("budget", "integer", "Combinations evaluated by Bayesian search per window (80)", false),
                    ("estimate_only", "boolean", "Only estimate runtime (times one combination; saves nothing)", false),
                }).ToArray())),
            Tool("list_runs", "Saved strategy runs (backtests, optimizations and walk-forwards) with metrics.",
                Schema(("file", "string", "Filename without .cs", true), ("limit", "integer", "Number of runs (20)", false))),
            Tool("analyze_portfolio", "Combines saved strategy runs into a portfolio: aggregate metrics, daily-return correlation matrix and diversification benefit.",
                Schema(("components", "array", "List of {file, run_id (optional: latest backtest if omitted), weight (optional, 1)}", true),
                    ("capital", "number", "Portfolio capital (10000)", false))),
            Tool("suggest_portfolio", "Searches the latest strategy backtests for a low-correlation combination that most improves aggregate Sharpe.",
                Schema(("max_components", "integer", "Maximum strategies (5)", false), ("max_correlation", "number", "Maximum correlation between two strategies (0.5)", false),
                    ("capital", "number", "Portfolio capital (10000)", false))),
            Tool("live_portfolio", "Live portfolio (read-only): each strategy currently or previously running algorithms in Trading Hub, with live parameters, size, position, PnL, trades, win rate, profit factor (overall and last 20 trades), current/maximum drawdown and recent trades with prices and quantities. " +
                                   "Identifies each execution contract (e.g. MNQ for an NQ strategy): quantities and PnL refer to that contract.",
                Schema(("recent_trades", "integer", "Recent trades per strategy (10)", false))),
            Tool("propose_sizing", "Proposes position-size changes for live strategies. Applies NOTHING: proposals remain pending until the user approves or rejects each in Live → Proposals.",
                Schema(("summary", "string", "Analysis summary in 1 to 3 lines", true),
                    ("items", "array", "List of {key (from live_portfolio), proposed_size (multiplier: 1 = normal, 0 = paused, maximum 3), reason}", true))),
            Tool("save_report", "Generates and saves a detailed report from saved runs: executive summary, data and assumptions, performance, trades, risk, long/short breakdown, monthly/hourly/weekday results, drawdowns, optimization sensitivity, walk-forward, Monte Carlo and portfolio. Supply the verdict and analysis in English. View it in Backtesting → Reports.",
                Schema(("title", "string", "Short report title", true),
                    ("verdict", "string", "Verdict code: ganadora (profitable), prometedora (promising), perdedora (unprofitable) or descartada (rejected)", true),
                    ("file", "string", "Evaluated strategy (without .cs); omitted run IDs default to its latest runs", false),
                    ("backtest_run_id", "string", "Main backtest run (preferably with Monte Carlo)", false),
                    ("optimization_run_id", "string", "Optimization run", false),
                    ("walk_forward_run_id", "string", "Walk-forward run", false),
                    ("portfolio", "array", "Optional portfolio components [{file, run_id, weight}] to include a portfolio section", false),
                    ("capital", "number", "Portfolio capital (10000)", false),
                    ("summary", "string", "Executive summary of 2 to 5 lines with the conclusion", true),
                    ("analysis", "string", "Markdown analysis: strengths, weaknesses, robustness, overfitting and comparisons between code versions", true),
                    ("risks", "string", "Risks as a Markdown list", false),
                    ("next_steps", "string", "Next steps as a Markdown list", false),
                    ("extra_markdown", "string", "Optional extra sections (e.g. code changes)", false))),
        };
    }

    private static async Task<JsonNode> CallAsync(JsonNode? p)
    {
        string name = p?["name"]?.GetValue<string>() ?? "";
        var args = p?["arguments"] as JsonObject ?? new JsonObject();
        try
        {
            JsonNode result = name switch
            {
                "list_strategies" => ListStrategies(),
                "compile_strategy" => CompileStrategy(Str(args, "file")),
                "chart_snapshot" => await ChartSnapshotAsync(args),
                "list_indicators" => ListIndicators(),
                "compile_indicator" => CompileIndicator(Str(args, "file")),
                "list_data" => ListData(),
                "download_data" => await DownloadDataAsync(args),
                "run_backtest" => await RunBacktestAsync(args),
                "run_optimization" => await RunOptimizationAsync(args),
                "run_walk_forward" => await RunWalkForwardAsync(args),
                "list_runs" => ListRuns(args),
                "analyze_portfolio" => AnalyzePortfolio(args),
                "live_portfolio" => LivePortfolio(args),
                "propose_sizing" => ProposeSizing(args),
                "suggest_portfolio" => SuggestPortfolio(args),
                "save_report" => SaveReport(args),
                _ => throw new ArgumentException($"Unknown tool: {name}"),
            };
            return Content(result.ToJsonString(new JsonSerializerOptions { WriteIndented = false }), false);
        }
        catch (Exception ex)
        {
            return Content("Error: " + ex.Message, true);
        }
    }

    private static JsonObject Content(string text, bool isError) => new()
    {
        ["content"] = new JsonArray { new JsonObject { ["type"] = "text", ["text"] = text } },
        ["isError"] = isError,
    };

    // ===================================================================== argumentos

    private static string Str(JsonObject a, string key, string? def = null)
    {
        var v = a[key];
        if (v == null) return def ?? throw new ArgumentException($"Missing «{key}».");
        return v.GetValueKind() == JsonValueKind.String ? v.GetValue<string>() : v.ToJsonString();
    }

    private static double Num(JsonObject a, string key, double def)
    {
        var v = a[key];
        if (v == null) return def;
        return v.GetValueKind() == JsonValueKind.String ? double.Parse(v.GetValue<string>(), Inv) : v.GetValue<double>();
    }

    private static bool Bool(JsonObject a, string key, bool def) => a[key] is { } v ? v.GetValueKind() == JsonValueKind.True || (v.GetValueKind() == JsonValueKind.String && v.GetValue<string>() == "true") : def;

    private static DateTime Date(JsonObject a, string key, bool endOfDay)
    {
        var s = Str(a, key);
        if (!DateTime.TryParse(s, Inv, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var d))
            throw new ArgumentException($"Invalid date in «{key}»: {s} (use YYYY-MM-DD)");
        d = DateTime.SpecifyKind(d, DateTimeKind.Utc);
        // "hasta" con solo fecha incluye todo ese día
        return endOfDay && d.TimeOfDay == TimeSpan.Zero ? d.AddDays(1) : d;
    }

    private static Dictionary<string, object?> Params(JsonObject a)
    {
        var res = new Dictionary<string, object?>();
        if (a["parameters"] is not JsonObject o) return res;
        foreach (var (k, v) in o)
        {
            if (v == null) continue;
            res[k] = v.GetValueKind() switch
            {
                JsonValueKind.Number => v.GetValue<double>(),
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                _ => v.ToString(),
            };
        }

        return res;
    }

    // ===================================================================== estrategias

    /// <summary>Carpeta del historial (TRADINGHUB_BACKTESTS_DIR la cambia, para pruebas).</summary>
    private static string StoreRoot => Environment.GetEnvironmentVariable("TRADINGHUB_BACKTESTS_DIR") is { Length: > 0 } dir ? dir : AppPaths.Backtests;

    private static StrategyCatalog Catalog
    {
        get
        {
            if (_catalog != null) return _catalog;
            _catalog = new StrategyCatalog();
            StrategyStore.Root = StoreRoot; // el catálogo la pisa al crearse
            return _catalog;
        }
    }

    private static StrategyFile FileOf(string file)
    {
        Catalog.Refresh();
        file = Path.GetFileNameWithoutExtension(file);
        var f = Catalog.Files.FirstOrDefault(x => x.FileName.Equals(file, StringComparison.OrdinalIgnoreCase))
                ?? throw new ArgumentException($"Strategy not found: «{file}». Use list_strategies.");
        var res = Catalog.Compile(f);
        if (!res.Success || f.Type == null)
        {
            var errs = res.Diagnostics.Where(d => d.Severity == "error").Take(10).Select(d => $"line {d.Line}: {d.Message}");
            throw new ArgumentException($"«{file}» does not compile: {string.Join(" | ", errs)}");
        }

        return f;
    }

    private static JsonArray ParametersOf(Type t)
    {
        var arr = new JsonArray();
        var instance = (Strategy)Activator.CreateInstance(t)!;
        foreach (var p in Strategy.Parameters(t))
        {
            var o = new JsonObject
            {
                ["name"] = p.Name,
                ["label"] = Strategy.ParameterName(p),
                ["type"] = p.PropertyType.IsEnum ? "enum" : p.PropertyType.Name.ToLowerInvariant(),
                ["value"] = JsonValue.Create(p.GetValue(instance) is IFormattable fv ? fv.ToString(null, Inv) : p.GetValue(instance)?.ToString()),
            };
            if (p.PropertyType.IsEnum) o["options"] = new JsonArray(Enum.GetNames(p.PropertyType).Select(n => (JsonNode)n).ToArray());
            if (p.GetCustomAttributes(typeof(OptimizeAttribute), true).FirstOrDefault() is OptimizeAttribute opt)
                o["optimize"] = new JsonObject { ["min"] = opt.Min, ["max"] = opt.Max, ["step"] = opt.Step };
            arr.Add(o);
        }

        return arr;
    }

    /// <summary>
    /// Velas de calentamiento: hasta que están listos los indicadores que crea en OnInit (con los parámetros por defecto y,
    /// si cambia, con los máximos de [Optimize]). Cada ventana del walk-forward arranca sin historia y las gasta.
    /// </summary>
    private static JsonObject WarmupOf(Type t)
    {
        var w = StrategyWarmup.Measure(t);
        var o = new JsonObject
        {
            ["bars"] = w.Bars,
            ["indicators"] = new JsonArray(w.Indicators.Select(n => (JsonNode)n).ToArray()),
        };
        var max = new Dictionary<string, object?>();
        foreach (var p in Strategy.Parameters(t))
            if (p.GetCustomAttributes(typeof(OptimizeAttribute), true).FirstOrDefault() is OptimizeAttribute opt)
                max[p.Name] = opt.Max;
        if (max.Count > 0 && StrategyWarmup.Measure(t, max) is { Error: null } wm && wm.Bars != w.Bars) o["bars_at_optimize_max"] = wm.Bars;
        // «bars» no es el calentamiento completo: se lo aclara siempre al agente
        var note = "«bars» only counts indicators created in OnInit: it excludes manual historical reads (Bars[n]) " +
                   "and indicators created later. Add those bars if the code uses them.";
        if (!w.Complete) note = (w.Error != null ? $"Could not fully measure warmup ({w.Error}). " : $"After {StrategyWarmup.MaxBars} bars they were still not ready. ") + note;
        o["note"] = note;
        return o;
    }

    private static JsonNode ListStrategies()
    {
        Catalog.Refresh();
        var arr = new JsonArray();
        foreach (var f in Catalog.Files)
        {
            var res = Catalog.Compile(f);
            var o = new JsonObject
            {
                ["file"] = f.FileName,
                ["name"] = f.DisplayName,
                ["compiles"] = res.Success && f.Type != null,
                ["errors"] = res.Diagnostics.Count(d => d.Severity == "error"),
            };
            if (f.Type != null)
            {
                o["description"] = Strategy.Description(f.Type);
                o["parameters"] = ParametersOf(f.Type);
                o["warmup"] = WarmupOf(f.Type);
                o["runs"] = StrategyStore.List(f.Id).Count;
            }

            arr.Add(o);
        }

        return new JsonObject { ["folder"] = AppPaths.Strategies, ["strategies"] = arr };
    }

    internal static JsonNode CompileStrategy(string file)
    {
        Catalog.Refresh();
        file = Path.GetFileNameWithoutExtension(file);
        var f = Catalog.Files.FirstOrDefault(x => x.FileName.Equals(file, StringComparison.OrdinalIgnoreCase))
                ?? throw new ArgumentException($"Strategy not found: «{file}».");
        var res = Catalog.Compile(f, force: true);
        var errors = new JsonArray(res.Diagnostics.Where(d => d.Severity == "error").Take(30)
            .Select(d => (JsonNode)new JsonObject { ["line"] = d.Line, ["column"] = d.Column, ["id"] = d.Id, ["message"] = d.Message }).ToArray());
        var o = new JsonObject { ["file"] = f.FileName, ["ok"] = res.Success && f.Type != null, ["errors"] = errors };
        if (f.Type != null)
        {
            o["parameters"] = ParametersOf(f.Type);
            o["warmup"] = WarmupOf(f.Type);
        }

        return o;
    }

    // ===================================================================== el gráfico (lo que mira Claude)

    /// <summary>Carpeta de las capturas del gráfico que mira Claude (se guardan las últimas 60).</summary>
    internal static string CapturesDir => Path.Combine(AppPaths.Root, "Capturas del laboratorio");

    private static DateTime LocalArg(JsonObject a, string key, TimeZoneInfo tz, bool endOfDay)
    {
        string text = Str(a, key).Trim().Replace('T', ' ');
        string[] formats = { "yyyy-MM-dd HH:mm", "yyyy-MM-dd HH:mm:ss", "yyyy-MM-dd H:mm", "yyyy-MM-dd" };
        if (!DateTime.TryParseExact(text, formats, Inv, DateTimeStyles.None, out var local)) throw new ArgumentException($"«{key}» must be YYYY-MM-DD HH:mm (local time).");
        if (endOfDay && text.Length <= 10) local = local.AddDays(1);
        return TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(local, DateTimeKind.Unspecified), tz);
    }

    /// <summary>
    /// Corre la estrategia en el rango (con días previos para los indicadores), arma el gráfico con sus operaciones, dibujos
    /// y líneas, lo guarda como imagen y devuelve lo que pasó vela a vela (hora local).
    /// </summary>
    internal static async Task<JsonNode> ChartSnapshotAsync(JsonObject a)
    {
        var file = FileOf(Str(a, "file"));
        string tzId = a["timezone"] is { } tzNode ? tzNode.GetValue<string>() : AppSettings.Load().TimeZoneId ?? TimeZoneInfo.Local.Id;
        TimeZoneInfo tz;
        try { tz = TimeZoneInfo.FindSystemTimeZoneById(tzId); }
        catch { tz = TimeZoneInfo.Local; }
        string periodCode = Str(a, "period", "M5");
        if (!ChartPeriod.TryParse(periodCode, out var period)) throw new ArgumentException($"Invalid period: {periodCode}");
        var fromUtc = LocalArg(a, "from", tz, false);
        var toUtc = LocalArg(a, "to", tz, true);
        if (toUtc <= fromUtc) throw new ArgumentException("to must be later than from.");
        double warmDays = Num(a, "warmup_days", period.IsTimeBased && period.ApproxDuration >= TimeSpan.FromHours(1) ? 15 : 3);
        var inst = await ResolveInstrumentAsync(Str(a, "feed"), Str(a, "symbol").ToUpperInvariant());
        var ticks = await Task.Run(() => TickData.Load(AppPaths.Ticks, inst, fromUtc.AddDays(-warmDays), toUtc, null, CancellationToken.None));
        if (ticks.Count == 0) throw new ArgumentException($"No saved ticks for {inst.Symbol} ({inst.FeedId}) in this range. See list_data or download days with download_data.");

        // la estrategia sobre todo lo cargado (como en el gráfico: sin costos y sin cerrar al final)
        var strategy = file.Create();
        strategy.SetParameters(ParamsFor(a, file));
        var settings = new BacktestSettings
        {
            FeedId = inst.FeedId, Symbol = inst.Symbol, FromUtc = ticks.FirstTime, ToUtc = ticks.LastTime.AddSeconds(1), Period = periodCode,
            CommissionPercent = 0, SlippageTicks = 0, TimeZoneId = tz.Id, CloseAtEnd = false, InitialCapital = 10_000,
        };
        var result = await Task.Run(() => new BacktestEngine(strategy, ticks, settings) { ContextHistory = MarketConditions.HourlyBefore(inst.FeedId, inst.Symbol, ticks.FirstTime) }.Run(CancellationToken.None));
        if (!result.Ok) throw new InvalidOperationException("Strategy failed: " + result.Error);

        // el gráfico, igual que en la app
        var data = new InstrumentData(inst, new TradingHub.Feeds.SimulatedFeed());
        for (int i = ticks.Start; i < ticks.End; i++) data.Trades.Add(ticks.Get(i));
        var model = new TradingHub.Charting.ChartModel(data, period, new TradingHub.Charting.ChartSettings { TimeZoneId = tz.Id, Style = TradingHub.Charting.ChartStyle.Candles });
        model.Viewport.BarWidth = 12;
        model.Rebuild();
        var color = new SkiaSharp.SKColor(180, 120, 255);
        var (signals, links) = StrategyDrawingMap.Trades(result.Trades, result.OpenPosition);
        var drawings = strategy.Canvas.Drawings();
        var plots = strategy.Canvas.Plots();
        model.Strategies.Add(new TradingHub.Charting.StrategyOverlay
        {
            Name = file.DisplayName,
            Status = "laboratory snapshot",
            Color = color,
            Signals = signals,
            Links = links,
            Shapes = drawings.Select(d => StrategyDrawingMap.ToShape(d, color)).ToList(),
            Lines = plots.Select(pl => StrategyDrawingMap.ToLine(pl.Name, pl.Color, pl.Width, pl.Style, pl.Points, color)).ToList(),
        });
        const int W = 1600, H = 900;
        var renderer = new TradingHub.Charting.Rendering.ChartRenderer();
        var state = new TradingHub.Charting.Rendering.ChartRenderState { Crosshair = false };
        using var surface = SkiaSharp.SKSurface.Create(new SkiaSharp.SKImageInfo(W, H));
        renderer.Render(surface.Canvas, model, W, H, state);
        model.CenterOn(fromUtc, toUtc);
        renderer.Render(surface.Canvas, model, W, H, state);
        Directory.CreateDirectory(CapturesDir);
        string name = $"{file.FileName}-{inst.Symbol}-{periodCode}-{TimeZoneInfo.ConvertTimeFromUtc(fromUtc, tz):yyyyMMdd-HHmm}.png";
        string path = Path.Combine(CapturesDir, string.Concat(name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c)));
        using (var image = surface.Snapshot())
        using (var png = image.Encode(SkiaSharp.SKEncodedImageFormat.Png, 92))
            await File.WriteAllBytesAsync(path, png.ToArray());
        foreach (var old in new DirectoryInfo(CapturesDir).GetFiles("*.png").OrderByDescending(f => f.LastWriteTimeUtc).Skip(60)) old.Delete();

        // lo que pasó en el rango, con hora local
        string L(DateTime utc) => TimeZoneInfo.ConvertTimeFromUtc(utc, tz).ToString("yyyy-MM-dd HH:mm", Inv);
        var plotAt = plots.Select(pl => (pl.Name, Values: pl.Points.Where(x => x.Value != null).GroupBy(x => x.Time).ToDictionary(g => g.Key, g => g.Last().Value!.Value))).ToList();
        var bars = new JsonArray();
        var series = model.Series;
        for (int i = 0, n = 0; i < series.Count && n < 200; i++)
        {
            var c = series[i];
            if (c.Time < fromUtc || c.Time >= toUtc) continue;
            n++;
            var bar = new JsonObject
            {
                ["time"] = L(c.Time),
                ["open"] = series.TicksToPrice(c.OpenTicks),
                ["high"] = series.TicksToPrice(c.HighTicks),
                ["low"] = series.TicksToPrice(c.LowTicks),
                ["close"] = series.TicksToPrice(c.CloseTicks),
                ["volume"] = c.Volume,
            };
            foreach (var (plotName, values) in plotAt.Take(6))
                if (values.TryGetValue(c.Time, out var v)) bar[plotName] = v;
            bars.Add(bar);
        }

        bool InWindow(DateTime t) => t >= fromUtc && t < toUtc;
        var trades = new JsonArray(result.Trades.Where(t => InWindow(t.EntryTime) || InWindow(t.ExitTime)).Take(50).Select(t => (JsonNode)new JsonObject
        {
            ["side"] = t.IsLong ? "long" : "short",
            ["entry"] = L(t.EntryTime), ["entry_price"] = t.EntryPrice, ["entry_order"] = t.EntryName,
            ["exit"] = L(t.ExitTime), ["exit_price"] = t.ExitPrice, ["exit_order"] = t.ExitName,
            ["net_pnl"] = Math.Round(t.NetPnl, 2),
        }).ToArray());
        var shapes = new JsonArray(drawings.Where(d => InWindow(d.Time1) || InWindow(d.Time2) || d.Kind == DrawingKind.HorizontalLine).Take(80).Select(d => (JsonNode)new JsonObject
        {
            ["id"] = d.Id, ["kind"] = d.Kind.ToString(), ["time"] = d.Time1 == DateTime.MinValue ? "" : L(d.Time1), ["time2"] = d.Time2 == DateTime.MinValue ? "" : L(d.Time2),
            ["price"] = d.Price1, ["price2"] = d.Price2, ["text"] = d.Text,
        }).ToArray());
        var log = new JsonArray(result.Log.Select(line =>
                DateTime.TryParseExact(line.Length >= 19 ? line[..19] : "", "yyyy-MM-dd HH:mm:ss", Inv, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var t)
                    ? (Time: (DateTime?)t, Line: line) : (Time: null, Line: line))
            .Where(x => x.Time is { } t && InWindow(t)).Take(100).Select(x => (JsonNode)$"{L(x.Time!.Value)}  {x.Line[19..].Trim()}").ToArray());
        return new JsonObject
        {
            ["image"] = path,
            ["timezone"] = tz.Id,
            ["note"] = "Read the PNG image with Read. Bar timestamps indicate opening time; a market order submitted at a bar close fills in the next bar.",
            ["bars"] = bars,
            ["trades"] = trades,
            ["open_position"] = result.OpenPosition is { } op ? new JsonObject { ["side"] = op.IsLong ? "long" : "short", ["since"] = L(op.EntryTime), ["price"] = op.AveragePrice, ["quantity"] = op.Quantity } : null,
            ["drawings"] = shapes,
            ["log"] = log,
        };
    }

    // ===================================================================== indicadores propios

    private static List<string> IndicatorFiles() =>
        Directory.Exists(AppPaths.UserIndicators)
            ? Directory.EnumerateFiles(AppPaths.UserIndicators, "*.cs", SearchOption.AllDirectories).Where(f => !f.Contains("/bin/") && !f.Contains("/obj/")).OrderBy(f => f).ToList()
            : new List<string>();

    /// <summary>Compila todos los indicadores propios juntos (como la app): los nombres visibles de los que compilan.</summary>
    private static (TradingHub.Scripting.ScriptCompileResult Result, List<(string Class, string Name)> Indicators) CompileIndicators()
    {
        var res = new TradingHub.Scripting.ScriptCompiler().Compile(IndicatorFiles(), "TradingHubUserIndicatorsLab");
        var list = new List<(string, string)>();
        if (res.Success && res.Assembly != null)
            foreach (var t in res.Assembly.GetTypes().Where(t => !t.IsAbstract && typeof(TradingHub.Indicators.Indicator).IsAssignableFrom(t)))
                list.Add((t.Name, TradingHub.Indicators.Indicator.DefaultName(t)));
        return (res, list);
    }

    internal static JsonNode ListIndicators()
    {
        var files = IndicatorFiles();
        var (res, indicators) = CompileIndicators();
        return new JsonObject
        {
            ["folder"] = AppPaths.UserIndicators,
            ["files"] = new JsonArray(files.Select(f => (JsonNode)Path.GetRelativePath(AppPaths.UserIndicators, f)).ToArray()),
            ["compiles"] = res.Success,
            ["indicators"] = new JsonArray(indicators.Select(i => (JsonNode)new JsonObject { ["class"] = i.Class, ["name"] = i.Name }).ToArray()),
        };
    }

    internal static JsonNode CompileIndicator(string file)
    {
        file = Path.GetFileNameWithoutExtension(file);
        var path = IndicatorFiles().FirstOrDefault(f => Path.GetFileNameWithoutExtension(f).Equals(file, StringComparison.OrdinalIgnoreCase))
                   ?? throw new ArgumentException($"Indicator not found: «{file}» in {AppPaths.UserIndicators}.");
        var (res, indicators) = CompileIndicators();
        var errors = res.Diagnostics.Where(d => d.Severity == "error").ToList();
        // errores de este archivo primero; los de otros archivos también frenan la compilación de todos
        var mine = errors.Where(d => string.IsNullOrEmpty(d.File) || Path.GetFullPath(d.File) == Path.GetFullPath(path)).Take(30);
        var others = errors.Where(d => !string.IsNullOrEmpty(d.File) && Path.GetFullPath(d.File) != Path.GetFullPath(path)).Take(10);
        return new JsonObject
        {
            ["file"] = Path.GetFileName(path),
            ["ok"] = res.Success,
            ["errors"] = new JsonArray(mine.Select(d => (JsonNode)new JsonObject { ["line"] = d.Line, ["column"] = d.Column, ["id"] = d.Id, ["message"] = d.Message }).ToArray()),
            ["errors_in_other_files"] = new JsonArray(others.Select(d => (JsonNode)new JsonObject { ["file"] = Path.GetFileName(d.File), ["line"] = d.Line, ["message"] = d.Message }).ToArray()),
            ["indicators"] = new JsonArray(indicators.Select(i => (JsonNode)new JsonObject { ["class"] = i.Class, ["name"] = i.Name }).ToArray()),
        };
    }

    // ===================================================================== datos

    private static JsonNode ListData()
    {
        var arr = new JsonArray();
        foreach (var s in HistorySources.Datasets(AppPaths.Ticks))
        {
            decimal? tick = HistorySources.StoredTickSize(AppPaths.Ticks, s.FeedId, s.Symbol);
            arr.Add(new JsonObject
            {
                ["feed"] = s.FeedId,
                ["symbol"] = s.Symbol,
                ["source"] = HistorySources.Describe(s),
                ["tick_size"] = tick is { } t ? (double)t : null,
                ["first_day"] = s.First.ToString("yyyy-MM-dd", Inv),
                ["last_day"] = s.Last.ToString("yyyy-MM-dd", Inv),
                ["days"] = s.Days.Count,
                ["size_mb"] = Math.Round(s.Bytes / 1048576.0, 1),
            });
        }

        return new JsonObject
        {
            ["instruments"] = arr,
            ["note"] = "Days with saved ticks. A range with missing consecutive days is still usable, but those days have no trades. For Binance/Bybit, download more with download_data. " +
                       "Futures with Databento history: rithmic combines Rithmic with Databento gaps (use unless instructed otherwise); rithmic-solo uses only Rithmic; databento uses only Databento.",
        };
    }

    private static async Task<JsonNode> DownloadDataAsync(JsonObject a)
    {
        string feed = Str(a, "feed"), symbol = Str(a, "symbol").ToUpperInvariant();
        if (!PublicArchives.Supports(feed)) throw new ArgumentException("Public archives are only available for binance-futures, binance-spot and bybit-linear.");
        var from = DateOnly.FromDateTime(Date(a, "from", false));
        var to = DateOnly.FromDateTime(Date(a, "to", false));
        if (to.DayNumber - from.DayNumber > 61) throw new ArgumentException("Maximum 62 days per call.");
        var inst = await ResolveInstrumentAsync(feed, symbol, preferExchange: true);
        int ok = 0, skipped = 0, missing = 0;
        long trades = 0;
        for (var d = from; d <= to; d = d.AddDays(1))
        {
            if (HistoricalData.HasDay(AppPaths.Ticks, feed, symbol, d))
            {
                skipped++;
                continue;
            }

            var list = await PublicArchives.DownloadDayAsync(inst, d, null, CancellationToken.None);
            if (list == null)
            {
                missing++;
                continue;
            }

            HistoricalData.WriteDay(AppPaths.Ticks, inst, d, list);
            trades += list.Count;
            ok++;
        }

        _ticks = null;
        return new JsonObject { ["downloaded_days"] = ok, ["already_had"] = skipped, ["not_published"] = missing, ["trades"] = trades };
    }

    /// <summary>Instrumento con su tick y valor del punto (del exchange si se puede; si no, del archivo).</summary>
    private static async Task<Instrument> ResolveInstrumentAsync(string feed, string symbol, bool preferExchange = false)
    {
        if (preferExchange || feed.StartsWith("binance") || feed.StartsWith("bybit"))
        {
            try
            {
                TradingHub.Feeds.IMarketDataFeed? f = feed switch
                {
                    "binance-futures" => new TradingHub.Feeds.Binance.BinanceFeed(true),
                    "binance-spot" => new TradingHub.Feeds.Binance.BinanceFeed(false),
                    "bybit-linear" => new TradingHub.Feeds.Bybit.BybitFeed(),
                    _ => null,
                };
                if (f != null)
                {
                    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                    var list = await f.GetInstrumentsAsync(cts.Token);
                    f.Dispose();
                    var inst = list.FirstOrDefault(i => i.Symbol.Equals(symbol, StringComparison.OrdinalIgnoreCase));
                    if (inst != null) return inst;
                }
            }
            catch
            {
                // sin red: se arma con el archivo
            }
        }

        decimal tick = HistorySources.StoredTickSize(AppPaths.Ticks, feed, symbol) ?? 0.01m;
        var spec = TradingHub.Feeds.Rithmic.RithmicFeed.SpecFor(symbol.Split(' ', ':')[0]);
        bool futures = MarketInfo.IsFutures(feed);
        return new Instrument
        {
            Symbol = symbol,
            FeedId = feed,
            Exchange = futures ? TradingHub.Market.FuturesCatalog.Find(symbol)?.Exchange ?? "CME" : feed,
            TickSize = spec?.Tick ?? tick,
            VolumeStep = futures || feed is TradingHub.Feeds.Alpaca.AlpacaFeed.FeedId or TradingHub.Feeds.Oanda.OandaFeed.FeedId ? 1 : 0.001m,
            PointValue = futures ? spec?.PointValue ?? 1 : 1,
        };
    }

    private static async Task<(BacktestSettings Settings, TickData Data)> PrepareAsync(JsonObject a, bool compress = false)
    {
        var settings = new BacktestSettings
        {
            FeedId = Str(a, "feed"),
            Symbol = Str(a, "symbol").ToUpperInvariant(),
            FromUtc = Date(a, "from", false),
            ToUtc = Date(a, "to", true),
            Period = Str(a, "period", "M5"),
            InitialCapital = (decimal)Num(a, "capital", 10_000),
            CommissionPercent = (decimal)Num(a, "commission_percent", 0.04),
            CommissionPerUnit = (decimal)Num(a, "commission_per_unit", 0),
            SlippageTicks = (int)Num(a, "slippage_ticks", 1),
            TimeZoneId = a["timezone"] is { } tz ? tz.GetValue<string>() : null,
            SessionStart = TimeSpan.FromHours(Num(a, "session_start_hour", 0)),
        };
        if (!ChartPeriod.TryParse(settings.Period, out _)) throw new ArgumentException($"Invalid period: {settings.Period}");
        if (settings.ToUtc <= settings.FromUtc) throw new ArgumentException("to must be later than from.");

        // comprimidos (ticks repetidos juntados) para optimizar y validar: la misma historia con muchos menos eventos
        string key = $"{settings.FeedId}|{settings.Symbol}|{settings.FromUtc:O}|{settings.ToUtc:O}|{(compress ? "c" : "")}";
        if (_ticks == null || _ticksKey != key)
        {
            _ticks = null;
            GC.Collect();
            var inst = await ResolveInstrumentAsync(settings.FeedId, settings.Symbol);
            var data = await Task.Run(() => TickData.Load(AppPaths.Ticks, inst, settings.FromUtc, settings.ToUtc, null, CancellationToken.None, compress));
            if (data.Count == 0)
                throw new ArgumentException($"No saved ticks for {settings.Symbol} ({settings.FeedId}) in this range. See list_data or download days with download_data.");
            _ticks = data;
            _ticksKey = key;
        }

        return (settings, _ticks);
    }

    // ===================================================================== corridas

    private static JsonObject MetricsJson(PerformanceMetrics m) => new()
    {
        ["net_profit"] = Math.Round(m.NetProfit, 2),
        ["return_percent"] = Math.Round(m.ReturnPercent, 2),
        ["profit_factor"] = Math.Round(m.ProfitFactor, 2),
        ["trades"] = m.TotalTrades,
        ["win_rate"] = Math.Round(m.WinRate, 1),
        ["average_trade"] = Math.Round(m.AverageTrade, 2),
        ["payoff_ratio"] = Math.Round(m.PayoffRatio, 2),
        ["expectancy"] = Math.Round(m.Expectancy, 2),
        ["max_drawdown"] = Math.Round(m.MaxDrawdown, 2),
        ["max_drawdown_percent"] = Math.Round(m.MaxDrawdownPercent, 2),
        ["recovery_factor"] = Math.Round(m.RecoveryFactor, 2),
        ["sharpe"] = Math.Round(m.SharpeRatio, 2),
        ["sortino"] = Math.Round(m.SortinoRatio, 2),
        ["long_net_profit"] = Math.Round(m.LongNetProfit, 2),
        ["short_net_profit"] = Math.Round(m.ShortNetProfit, 2),
        ["max_consecutive_losses"] = m.MaxConsecutiveLosses,
        ["exposure_percent"] = Math.Round(m.ExposurePercent, 1),
        ["commission"] = Math.Round(m.Commission, 2),
    };

    private static JsonObject ParamsJson(IReadOnlyDictionary<string, object?> p)
    {
        var o = new JsonObject();
        foreach (var (k, v) in p) o[k] = JsonValue.Create(v is IFormattable f ? f.ToString(null, Inv) : v?.ToString());
        return o;
    }

    private static void Save(StrategyRun run, StrategyFile file)
    {
        run.Notes = string.IsNullOrEmpty(run.Notes) ? "Claude laboratory" : run.Notes;
        StrategyStore.Save(run, File.ReadAllText(file.Path));
    }

    /// <summary>Archivo con los parámetros fijos de una estrategia mientras se valida (el filtro de una variante).</summary>
    internal static string PinsPath => Path.Combine(AppPaths.Root, "lab-parametros-fijos.json");

    /// <summary>Parámetros fijos de esta estrategia (no se pueden cambiar ni optimizar).</summary>
    internal static Dictionary<string, object?> PinnedFor(StrategyFile file)
    {
        var res = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        try
        {
            if (!File.Exists(PinsPath)) return res;
            using var doc = JsonDocument.Parse(File.ReadAllText(PinsPath));
            if (!doc.RootElement.TryGetProperty(file.FileName, out var pins) || pins.ValueKind != JsonValueKind.Object) return res;
            foreach (var p in pins.EnumerateObject()) res[p.Name] = p.Value.Clone();
        }
        catch (Exception)
        {
            // sin parámetros fijos legibles: los que pasó el agente
        }

        return res;
    }

    /// <summary>Los parámetros del pedido con los fijos encima.</summary>
    private static Dictionary<string, object?> ParamsFor(JsonObject a, StrategyFile file)
    {
        var res = Params(a);
        foreach (var (k, v) in PinnedFor(file)) res[k] = v;
        return res;
    }

    private static async Task<JsonNode> RunBacktestAsync(JsonObject a)
    {
        var file = FileOf(Str(a, "file"));
        var (settings, data) = await PrepareAsync(a);
        var strategy = file.Create();
        strategy.SetParameters(ParamsFor(a, file));
        // el contexto de mercado arranca con el mes anterior, como en vivo
        var res = await Task.Run(() => new BacktestEngine(strategy, data, settings) { ContextHistory = MarketConditions.HourlyBefore(settings.FeedId, settings.Symbol, settings.FromUtc) }.Run(CancellationToken.None));
        if (!res.Ok) throw new InvalidOperationException("Strategy failed: " + res.Error);
        var run = new StrategyRun
        {
            StrategyId = file.Id,
            StrategyName = file.DisplayName,
            Kind = RunKind.Backtest,
            Settings = settings,
            Parameters = res.Parameters,
            Metrics = res.Metrics,
            Trades = res.Trades,
            Equity = BacktestResult.Downsample(res.Equity, 2000),
            Ticks = res.Ticks,
            Bars = res.Bars,
            Seconds = res.Seconds,
        };

        var o = new JsonObject
        {
            ["run_id"] = run.Id,
            ["parameters"] = ParamsJson(res.Parameters),
            ["metrics"] = MetricsJson(res.Metrics),
            ["ticks"] = res.Ticks,
            ["bars"] = res.Bars,
            ["seconds"] = Math.Round(res.Seconds, 1),
            ["data_from"] = data.FirstTime.ToString("yyyy-MM-dd HH:mm", Inv),
            ["data_to"] = data.LastTime.ToString("yyyy-MM-dd HH:mm", Inv),
        };

        int mcRuns = (int)Num(a, "monte_carlo_runs", 0);
        if (mcRuns > 0 && res.Trades.Count > 2)
        {
            var mc = Optimizer.MonteCarlo(res.Trades, settings.InitialCapital, Math.Clamp(mcRuns, 100, 20000));
            run.MonteCarlo = mc;
            o["monte_carlo"] = new JsonObject
            {
                ["runs"] = mc.Runs,
                ["drawdown_p50"] = Math.Round(mc.DrawdownP50, 2),
                ["drawdown_p95"] = Math.Round(mc.DrawdownP95, 2),
                ["drawdown_p99"] = Math.Round(mc.DrawdownP99, 2),
                ["worst_drawdown"] = Math.Round(mc.WorstDrawdown, 2),
                ["ruin_probability_percent"] = Math.Round(mc.RuinProbabilityPercent, 2),
            };
        }

        if (Bool(a, "include_trades", false))
        {
            var sample = res.Trades.Take(5).Concat(res.Trades.Count > 10 ? res.Trades.Skip(res.Trades.Count - 5) : Array.Empty<ClosedTrade>());
            o["sample_trades"] = JsonSerializer.SerializeToNode(sample);
        }

        Save(run, file);
        return o;
    }

    private static List<ParameterRange> Ranges(JsonObject a, StrategyFile file)
    {
        var ranges = ParameterRange.FromStrategy(file.Type!, file.Create());
        if (a["ranges"] is JsonObject custom)
        {
            foreach (var r in ranges) r.Enabled = false;
            foreach (var (name, v) in custom)
            {
                var r = ranges.FirstOrDefault(x => x.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                        ?? throw new ArgumentException($"«{name}» is not a numeric strategy parameter.");
                if (v is not JsonObject spec) continue;
                r.Enabled = true;
                r.Min = Num(spec, "min", r.Min);
                r.Max = Num(spec, "max", r.Max);
                r.Step = Num(spec, "step", r.Step);
            }
        }

        // los fijos no se optimizan
        var pinned = PinnedFor(file);
        foreach (var r in ranges.Where(r => pinned.ContainsKey(r.Name))) r.Enabled = false;
        if (!ranges.Any(r => r.Enabled)) throw new ArgumentException("No optimization parameters: provide ranges or mark parameters with [Optimize].");
        return ranges;
    }

    private static FitnessMetric Metric(JsonObject a) =>
        Enum.TryParse<FitnessMetric>(Str(a, "metric", nameof(FitnessMetric.NetProfitOverDrawdown)), true, out var m) ? m : FitnessMetric.NetProfitOverDrawdown;

    private static async Task<JsonNode> RunOptimizationAsync(JsonObject a)
    {
        var file = FileOf(Str(a, "file"));
        var ranges = Ranges(a, file);
        long combos = Optimizer.Combinations(ranges.Where(r => r.Enabled));
        long max = (long)Num(a, "max_combinations", 5000);
        if (combos > max) throw new ArgumentException($"There are {combos:N0} combinations (limit {max:N0}). Narrow the ranges or increase step sizes.");
        var (settings, data) = await PrepareAsync(a, Optimizer.CanCompress(file.Type!, Str(a, "period", "M5")));
        var metric = Metric(a);
        int minTrades = (int)Num(a, "min_trades", 10);
        var baseParams = ParamsFor(a, file);
        var res = await Task.Run(() => Optimizer.Optimize(file.Create, baseParams, ranges, data, settings, metric, minTrades, null, CancellationToken.None,
            contextHistory: MarketConditions.HourlyBefore(settings.FeedId, settings.Symbol, settings.FromUtc)));
        int top = (int)Num(a, "top", 10);
        var run = new StrategyRun
        {
            StrategyId = file.Id,
            StrategyName = file.DisplayName,
            Kind = RunKind.Optimization,
            Settings = settings,
            Parameters = res.Best?.Parameters ?? new(),
            Metrics = res.Best?.Metrics,
            Optimization = new OptimizationResult
            {
                Metric = res.Metric, Ranges = res.Ranges, Rows = res.Rows.Take(2000).ToList(), Combinations = res.Combinations, Seconds = res.Seconds,
                RecommendedIndex = res.RecommendedIndex < 2000 ? res.RecommendedIndex : -1,
            },
            Seconds = res.Seconds,
        };
        Save(run, file);
        return new JsonObject
        {
            ["run_id"] = run.Id,
            ["combinations"] = res.Combinations,
            ["seconds"] = Math.Round(res.Seconds, 1),
            ["metric"] = metric.ToString(),
            ["top"] = new JsonArray(res.Rows.Take(top).Select(r => (JsonNode)new JsonObject
            {
                ["parameters"] = ParamsJson(r.Parameters),
                ["fitness"] = double.IsFinite(r.Fitness) ? Math.Round(r.Fitness, 4) : null,
                ["metrics"] = MetricsJson(r.Metrics),
            }).ToArray()),
            ["profitable_combinations_percent"] = res.Rows.Count > 0 ? Math.Round(100.0 * res.Rows.Count(r => r.Metrics.NetProfit > 0) / res.Rows.Count, 1) : 0,
            ["recommended"] = res.Recommended is { } rec ? new JsonObject
            {
                ["parameters"] = ParamsJson(rec.Parameters),
                ["metrics"] = MetricsJson(rec.Metrics),
                ["note"] = "The best plateau (average with neighboring grid combinations), not the highest peak: the recommended selection.",
            } : null,
        };
    }

    /// <summary>Walk-forwards que están corriendo (el mismo pedido otra vez espera al que corre, no arranca otro).</summary>
    private static readonly ConcurrentDictionary<string, Task<JsonNode>> RunningWalkForwards = new();

    private static async Task<JsonNode> RunWalkForwardAsync(JsonObject a)
    {
        if (Bool(a, "estimate_only", false)) return await EstimateWalkForwardAsync(a);
        string key = a.ToJsonString();
        var task = RunningWalkForwards.GetOrAdd(key, _ => Task.Run(() => WalkForwardCoreAsync(a)));
        try
        {
            return await task;
        }
        finally
        {
            RunningWalkForwards.TryRemove(new KeyValuePair<string, Task<JsonNode>>(key, task));
        }
    }

    private static SearchMode SearchOf(JsonObject a) => Str(a, "search", "auto").Trim().ToLowerInvariant() switch
    {
        "grid" or "grilla" => SearchMode.Grid,
        "bayesian" or "bayesiana" or "tpe" => SearchMode.Bayesian,
        _ => SearchMode.Auto,
    };

    /// <summary>Cuánto va a tardar: corre una combinación sobre todo el rango y multiplica por las que hay que correr.</summary>
    private static async Task<JsonNode> EstimateWalkForwardAsync(JsonObject a)
    {
        var file = FileOf(Str(a, "file"));
        var ranges = Ranges(a, file);
        bool compress = Optimizer.CanCompress(file.Type!, Str(a, "period", "M5"));
        var (settings, data) = await PrepareAsync(a, compress);
        var space = new ParameterSpace(ranges, file.Type!);
        int inDays = Math.Max(1, (int)Num(a, "in_sample_days", 30)), outDays = Math.Max(1, (int)Num(a, "out_sample_days", 10));
        double total = (settings.ToUtc - settings.FromUtc).TotalDays;
        int windows = Math.Max(1, (int)Math.Ceiling((total - inDays) / outDays));
        var mode = SearchOf(a);
        bool grid = mode == SearchMode.Grid || (mode == SearchMode.Auto && space.Size <= Optimizer.GridLimit);
        int budget = (int)Num(a, "budget", Optimizer.DefaultBudget);
        long configs = grid ? space.Size : Math.Min(space.Size, (long)budget * windows / 2 + budget);
        var s = file.Create();
        s.SetParameters(ParamsFor(a, file));
        var sw = System.Diagnostics.Stopwatch.StartNew();
        await Task.Run(() => new BacktestEngine(s, data, settings) { ContextHistory = MarketConditions.HourlyBefore(settings.FeedId, settings.Symbol, settings.FromUtc) }.Run());
        double one = sw.Elapsed.TotalSeconds;
        int cores = Math.Max(1, Environment.ProcessorCount - 1);
        double estimate = one * configs / Math.Min(cores, Math.Max(1, configs)) * 1.15 + 3;
        return new JsonObject
        {
            ["events"] = data.Count,
            ["compressed"] = data.Compressed,
            ["search"] = grid ? "grid" : "bayesian",
            ["combinations_in_space"] = space.Size,
            ["combinations_to_run"] = configs,
            ["windows"] = windows,
            ["seconds_per_combination"] = Math.Round(one, 2),
            ["estimated_seconds"] = Math.Round(estimate),
            ["note"] = "Each combination runs once over the entire range. If too slow, reduce the grid, budget or date range.",
        };
    }

    private static async Task<JsonNode> WalkForwardCoreAsync(JsonObject a)
    {
        var file = FileOf(Str(a, "file"));
        var ranges = Ranges(a, file);
        var (settings, data) = await PrepareAsync(a, Optimizer.CanCompress(file.Type!, Str(a, "period", "M5")));
        var metric = Metric(a);
        int inDays = (int)Num(a, "in_sample_days", 0), outDays = (int)Num(a, "out_sample_days", 0);
        if (inDays <= 0 || outDays <= 0) throw new ArgumentException("in_sample_days and out_sample_days must be greater than zero.");
        int minTrades = (int)Num(a, "min_trades", 5);
        var baseParams = ParamsFor(a, file);
        bool anchored = Bool(a, "anchored", false);
        var search = SearchOf(a);
        int budget = (int)Num(a, "budget", 0);
        // cada ventana arranca su contexto de mercado con lo anterior a ella (incluido el mes antes del rango), como en vivo
        var res = await Task.Run(() => Optimizer.WalkForward(file.Create, baseParams, ranges, data, settings, metric, inDays, outDays, anchored, minTrades, null, CancellationToken.None,
            MarketConditions.HourlyBefore(settings.FeedId, settings.Symbol, settings.FromUtc), search, budget));
        var run = new StrategyRun
        {
            StrategyId = file.Id,
            StrategyName = file.DisplayName,
            Kind = RunKind.WalkForward,
            Settings = settings,
            Parameters = baseParams, // los fijos (por ejemplo el filtro de contexto); los optimizados están en cada ventana
            Metrics = res.OutSampleMetrics,
            Trades = res.OutSampleTrades,
            Equity = BacktestResult.Downsample(res.OutSampleEquity, 2000),
            WalkForward = res,
            Seconds = res.Seconds,
        };
        Save(run, file);
        return new JsonObject
        {
            ["run_id"] = run.Id,
            ["windows"] = new JsonArray(res.Windows.Select(w => (JsonNode)new JsonObject
            {
                ["n"] = w.Number,
                ["in_sample"] = $"{w.InSampleFrom:yyyy-MM-dd} → {w.InSampleTo:yyyy-MM-dd}",
                ["out_sample"] = $"{w.OutSampleFrom:yyyy-MM-dd} → {w.OutSampleTo:yyyy-MM-dd}",
                ["best_parameters"] = ParamsJson(w.BestParameters),
                ["in_sample_net"] = Math.Round(w.InSample.NetProfit, 2),
                ["out_sample_net"] = Math.Round(w.OutSample.NetProfit, 2),
                ["out_sample_trades"] = w.OutSample.TotalTrades,
                ["efficiency"] = Math.Round(w.Efficiency, 2),
            }).ToArray()),
            ["out_of_sample_metrics"] = MetricsJson(res.OutSampleMetrics),
            ["walk_forward_efficiency"] = Math.Round(res.WalkForwardEfficiency, 2),
            ["profitable_windows_percent"] = Math.Round(res.ProfitableWindowsPercent, 1),
            ["search"] = res.Search == "grilla" ? "grid" : "bayesian",
            ["combinations_run"] = res.Evaluated,
            ["combinations_in_space"] = res.SpaceSize,
            ["compression"] = Math.Round(res.Compression, 3),
            ["overfitting"] = res.Pbo is { } pbo ? new JsonObject
            {
                ["pbo"] = Math.Round(pbo, 3),
                ["best_loses_out_of_sample"] = res.PboLoss is { } l ? Math.Round(l, 3) : null,
                ["splits"] = res.PboSplits,
                ["note"] = "Selects the best combination on half the history and evaluates it on the other half, across all possible splits. best_loses_out_of_sample is the fraction that lost money " +
                           "(< 0.2 is good; ≥ 0.5 suggests overfitting); pbo is the fraction ranked in the bottom half (high PBO with all combinations profitable can indicate a plateau, not a problem).",
            } : null,
            ["cpcv_paths"] = res.CpcvPaths.Count > 0 ? new JsonArray(res.CpcvPaths.Select(p => (JsonNode)new JsonObject
            {
                ["n"] = p.Number,
                ["net"] = Math.Round(p.Net, 2),
                ["sharpe"] = Math.Round(p.Sharpe, 2),
                ["max_drawdown"] = Math.Round(p.MaxDrawdown, 2),
            }).ToArray()) : null,
            ["seconds"] = Math.Round(res.Seconds, 1),
        };
    }

    private static JsonNode ListRuns(JsonObject a)
    {
        var file = FileOf(Str(a, "file"));
        int limit = (int)Num(a, "limit", 20);
        return new JsonObject
        {
            ["runs"] = new JsonArray(StrategyStore.List(file.Id).Take(limit).Select(r => (JsonNode)new JsonObject
            {
                ["run_id"] = r.Id,
                ["kind"] = r.Kind.ToString(),
                ["created"] = r.CreatedUtc.ToString("yyyy-MM-dd HH:mm", Inv),
                ["data"] = $"{r.FeedId} {r.Symbol} {r.Period} {r.FromUtc:yyyy-MM-dd} → {r.ToUtc:yyyy-MM-dd}",
                ["net_profit"] = Math.Round(r.NetProfit, 2),
                ["profit_factor"] = Math.Round(r.ProfitFactor, 2),
                ["max_drawdown"] = Math.Round(r.MaxDrawdown, 2),
                ["trades"] = r.Trades,
                ["win_rate"] = Math.Round(r.WinRate, 1),
                ["sharpe"] = Math.Round(r.Sharpe, 2),
                ["efficiency"] = r.Efficiency is double e ? Math.Round(e, 2) : null,
                ["parameters"] = r.ParametersText,
                ["notes"] = r.Notes,
            }).ToArray()),
        };
    }

    // ===================================================================== reportes

    private static StrategyRun? LoadRun(StrategyFile file, string? runId, RunKind kind)
    {
        if (!string.IsNullOrWhiteSpace(runId))
            return StrategyStore.Load(file.Id, runId) ?? throw new ArgumentException($"Run not found: {runId} of {file.FileName}. Use list_runs.");
        var latest = StrategyStore.List(file.Id).Where(r => r.Kind == kind).OrderByDescending(r => r.CreatedUtc).FirstOrDefault();
        return latest == null ? null : StrategyStore.Load(file.Id, latest.Id);
    }

    private static List<PortfolioComponent> Components(JsonArray list)
    {
        var res = new List<PortfolioComponent>();
        foreach (var item in list.OfType<JsonObject>())
        {
            var file = FileOf(Str(item, "file"));
            var run = LoadRun(file, item["run_id"]?.GetValue<string>(), RunKind.Backtest)
                      ?? throw new ArgumentException($"{file.FileName} has no saved backtests: run run_backtest first.");
            if (run.Trades is not { Count: > 0 }) throw new ArgumentException($"Run {run.Id} of {file.FileName} has no trades.");
            res.Add(PortfolioComponent.FromRun(run, (decimal)Num(item, "weight", 1)));
        }

        if (res.Count == 0) throw new ArgumentException("Provide at least one strategy in components.");
        return res;
    }

    private static JsonObject PortfolioJson(PortfolioResult r)
    {
        int n = r.Components.Count;
        var matrix = new JsonArray();
        for (int i = 0; i < n; i++)
        {
            var row = new JsonArray();
            for (int j = 0; j < n; j++) row.Add(Math.Round(r.Correlation[i, j], 3));
            matrix.Add(row);
        }

        return new JsonObject
        {
            ["period"] = $"{r.From:yyyy-MM-dd} → {r.To:yyyy-MM-dd}",
            ["no_overlap"] = r.NoOverlap,
            ["components"] = new JsonArray(r.Components.Select((c, i) => (JsonNode)new JsonObject
            {
                ["n"] = i + 1,
                ["strategy"] = c.StrategyName,
                ["run_id"] = c.RunId,
                ["label"] = c.Label,
                ["weight"] = c.Weight,
                ["metrics_in_common_period"] = MetricsJson(r.ComponentMetrics[i]),
            }).ToArray()),
            ["portfolio_metrics"] = MetricsJson(r.Metrics),
            ["correlation_matrix"] = matrix,
            ["average_correlation"] = Math.Round(r.AverageCorrelation, 3),
            ["max_correlation"] = Math.Round(r.MaxCorrelation, 3),
            ["sum_of_component_drawdowns"] = Math.Round(r.SumOfComponentDrawdowns, 2),
            ["drawdown_reduction_percent"] = Math.Round(r.DrawdownReductionPercent, 1),
            ["average_component_sharpe"] = Math.Round(r.AverageComponentSharpe, 2),
        };
    }

    private static JsonNode AnalyzePortfolio(JsonObject a)
    {
        var list = a["components"] as JsonArray ?? throw new ArgumentException("Missing components.");
        var comps = Components(list);
        return PortfolioJson(PortfolioAnalyzer.Analyze(comps, (decimal)Num(a, "capital", 10_000)));
    }

    private static JsonNode SuggestPortfolio(JsonObject a)
    {
        decimal capital = (decimal)Num(a, "capital", 10_000);
        Catalog.Refresh();
        var candidates = new List<PortfolioComponent>();
        foreach (var f in Catalog.Files)
        {
            if (!Catalog.Compile(f).Success || f.Type == null) continue;
            // el último backtest de cada instrumento y período
            foreach (var g in StrategyStore.List(f.Id).Where(r => r.Kind == RunKind.Backtest).GroupBy(r => (r.Symbol, r.Period)))
            {
                var run = StrategyStore.Load(f.Id, g.OrderByDescending(r => r.CreatedUtc).First().Id);
                if (run?.Trades is { Count: >= 5 }) candidates.Add(PortfolioComponent.FromRun(run));
            }
        }

        if (candidates.Count < 2) throw new ArgumentException("Backtests from at least two strategies or instruments are required to build a portfolio.");
        var chosen = PortfolioAnalyzer.SuggestUncorrelated(candidates, capital, (int)Num(a, "max_components", 5), Num(a, "max_correlation", 0.5));
        if (chosen.Count == 0) throw new ArgumentException("No strategy has positive PnL with at least 5 trades.");
        var result = PortfolioAnalyzer.Analyze(chosen.Select(i => candidates[i]).ToList(), capital);
        var o = PortfolioJson(result);
        o["candidates_considered"] = candidates.Count;
        o["candidates"] = new JsonArray(candidates.Select(c => (JsonNode)new JsonObject
        {
            ["strategy"] = c.StrategyName,
            ["run_id"] = c.RunId,
            ["label"] = c.Label,
            ["net_profit"] = Math.Round(c.Metrics.NetProfit, 2),
            ["sharpe"] = Math.Round(c.Metrics.SharpeRatio, 2),
        }).ToArray());
        return o;
    }

    private static string LiveRoot => Environment.GetEnvironmentVariable("TRADINGHUB_LIVE_DIR") is { Length: > 0 } dir ? dir : Path.Combine(AppPaths.Root, "Live");

    /// <summary>Cuántos <paramref name="contract"/> equivalen a 1 de <paramref name="symbol"/> (10 MNQ = 1 NQ); null si no se sabe.</summary>
    private static decimal? ContractRatio(string symbol, string contract)
    {
        if (contract.Equals(symbol, StringComparison.OrdinalIgnoreCase)) return 1;
        decimal a = FuturesCatalog.Find(symbol)?.PointValue ?? 0, b = FuturesCatalog.Find(contract)?.PointValue ?? 0;
        return a > 0 && b > 0 ? a / b : null;
    }

    /// <summary>Últimas operaciones en vivo, cada una con el contrato en que se ejecutó (el del llenado que la abrió).</summary>
    private static JsonArray LiveTrades(LiveRunnerInfo info, int recent)
    {
        var ledger = LiveStore.LoadLedger(info.Key, info.PointValue);
        return new JsonArray(ledger.Trades.TakeLast(recent).Select(t =>
        {
            // los llenados viejos no traen contrato: son del instrumento de la estrategia
            string contract = ledger.Fills.FirstOrDefault(f => f.Time == t.EntryTime)?.Symbol ?? info.Symbol;
            var ratio = ContractRatio(info.Symbol, contract);
            return (JsonNode)new JsonObject
            {
                ["side"] = t.IsLong ? "long" : "short",
                ["entry"] = t.EntryTime.ToString("yyyy-MM-dd HH:mm", Inv),
                ["entry_price"] = t.EntryPrice,
                ["exit"] = t.ExitTime.ToString("yyyy-MM-dd HH:mm", Inv),
                ["exit_price"] = t.ExitPrice,
                ["contract"] = contract,
                ["quantity"] = t.Quantity,
                ["instrument_quantity"] = ratio is { } r ? Math.Round(t.Quantity / r, 4) : null,
                ["pnl"] = Math.Round(t.NetPnl, 2),
            };
        }).ToArray());
    }

    internal static JsonNode LivePortfolio(JsonObject a)
    {
        LiveStore.Root = LiveRoot;
        int recent = (int)Num(a, "recent_trades", 10);
        var stats = LiveStore.Stats();
        return new JsonObject
        {
            ["strategies"] = new JsonArray(stats.Select(s => (JsonNode)new JsonObject
            {
                ["key"] = s.Info.Key,
                ["strategy"] = s.Info.Strategy,
                ["instrument"] = $"{s.Info.FeedId}:{s.Info.Symbol} {s.Info.Period}",
                // en qué contrato manda las órdenes (MNQ para una del NQ con tamaño chico) y cuántos de esos son 1 del instrumento
                ["exec_symbol"] = s.Info.ExecSymbol ?? s.Info.Symbol,
                ["exec_ratio"] = ContractRatio(s.Info.Symbol, s.Info.ExecSymbol ?? s.Info.Symbol),
                ["account"] = s.Info.Account,
                ["running"] = s.Info.Running,
                ["parameters"] = JsonSerializer.SerializeToNode(s.Info.Parameters),
                ["size"] = s.Info.Size,
                ["position"] = s.Info.Position,
                ["open_pnl"] = Math.Round(s.Info.OpenPnL, 2),
                ["net"] = Math.Round(s.Net, 2),
                ["metrics"] = MetricsJson(s.Metrics),
                ["recent_profit_factor"] = s.RecentTrades > 0 ? Math.Round(Math.Min(s.RecentProfitFactor, 999), 2) : null,
                ["recent_expectancy"] = Math.Round(s.RecentExpectancy, 2),
                ["recent_trades_count"] = s.RecentTrades,
                ["current_drawdown"] = Math.Round(s.CurrentDrawdown, 2),
                ["last_trades"] = LiveTrades(s.Info, recent),
            }).ToArray()),
            ["pending_proposals"] = LiveStore.LoadProposals().Count(p => p.IsPending),
            ["note"] = "Sizes multiply each strategy's requested quantity (1 = normal, 0 = paused). Use propose_sizing to change them; the user approves every change. " +
                       "Times are UTC. entry_price and exit_price are the latest market prices when each fill was recorded, not broker-reported fill prices, " +
                       "so they cannot measure exact slippage. Live PnL uses those prices and excludes commissions (the strategy ledger does not record them): " +
                       "compare against a zero-commission backtest or account for fees separately. " +
                       "position, quantity and pnl refer to the execution contract (exec_symbol per strategy, contract per trade; e.g. MNQ for an NQ strategy) " +
                       "and already include the size multiplier. instrument_quantity converts to the strategy's instrument (5 MNQ = 0.5 NQ; exec_ratio is execution contracts per instrument unit) " +
                       "and pnl is for that quantity: scale it for a backtest comparison (pnl × backtest quantity / instrument_quantity).",
        };
    }

    private static JsonNode ProposeSizing(JsonObject a)
    {
        LiveStore.Root = LiveRoot;
        var runners = LiveStore.LoadRunners();
        var list = a["items"] as JsonArray ?? throw new ArgumentException("Missing items.");
        var proposal = new SizingProposal { Source = "claude", Summary = Str(a, "summary", "") };
        foreach (var item in list.OfType<JsonObject>())
        {
            string key = Str(item, "key");
            var info = runners.FirstOrDefault(r => r.Key == key) ?? throw new ArgumentException($"Strategy not found: «{key}» in the portfolio. Use keys from live_portfolio.");
            decimal size = Math.Clamp((decimal)Num(item, "proposed_size", (double)info.Size), 0, LiveSizing.MaxSize);
            if (size == info.Size) continue;
            proposal.Items.Add(new SizingItem
            {
                Key = key,
                Strategy = $"{info.Strategy} · {info.Symbol}",
                CurrentSize = info.Size,
                ProposedSize = Math.Round(size, 2),
                Reason = Str(item, "reason", ""),
            });
        }

        if (proposal.Items.Count == 0) return new JsonObject { ["created"] = false, ["note"] = "No changes from current sizes." };
        LiveStore.AddProposal(proposal);
        return new JsonObject
        {
            ["created"] = true,
            ["proposal_id"] = proposal.Id,
            ["items"] = proposal.Items.Count,
            ["note"] = "Pending: the user reviews each change in Live → Proposals. Nothing has been applied.",
        };
    }

    private static JsonNode SaveReport(JsonObject a)
    {
        string title = Str(a, "title").Trim();
        string verdict = (a["verdict"]?.GetValue<string>() ?? "").Trim().ToLowerInvariant();
        StrategyFile? file = a["file"] is { } fn ? FileOf(fn.GetValue<string>()) : null;
        var input = new ReportInput
        {
            Title = title,
            Verdict = ReportStore.Verdicts.FirstOrDefault(v => verdict.StartsWith(v[..5])) ?? "",
            StrategyFile = file?.FileName,
            StrategyName = file?.DisplayName,
            Description = file?.Type != null ? Strategy.Description(file.Type) : null,
            Summary = a["summary"]?.GetValue<string>() ?? "",
            Analysis = a["analysis"]?.GetValue<string>() ?? a["markdown"]?.GetValue<string>() ?? "",
            Risks = a["risks"]?.GetValue<string>() ?? "",
            NextSteps = a["next_steps"]?.GetValue<string>() ?? "",
            Extra = a["extra_markdown"]?.GetValue<string>() ?? "",
            PortfolioCapital = (decimal)Num(a, "capital", 10_000),
        };
        if (file != null)
        {
            input.Backtest = LoadRun(file, a["backtest_run_id"]?.GetValue<string>(), RunKind.Backtest);
            input.Optimization = LoadRun(file, a["optimization_run_id"]?.GetValue<string>(), RunKind.Optimization);
            input.WalkForward = LoadRun(file, a["walk_forward_run_id"]?.GetValue<string>(), RunKind.WalkForward);
        }

        if (a["portfolio"] is JsonArray pl && pl.Count > 0) input.Portfolio = PortfolioAnalyzer.Analyze(Components(pl), input.PortfolioCapital);
        if (input.Backtest == null && input.Portfolio == null && input.WalkForward == null)
            throw new ArgumentException("No runs for the report: run run_backtest or provide a portfolio before save_report.");
        var (markdown, images) = ReportBuilder.Build(input);
        var report = ReportStore.Save(title, markdown, file?.FileName, input.Verdict, images);
        return new JsonObject { ["saved"] = report.Path, ["verdict"] = report.Verdict, ["charts"] = images.Count };
    }
}
