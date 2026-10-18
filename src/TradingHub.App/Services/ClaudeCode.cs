using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Avalonia.Threading;

namespace TradingHub.App.Services;

/// <summary>Resultado de un pedido a Claude Code.</summary>
public sealed record ClaudeResult(bool IsError, string Text, double CostUsd, double Seconds, bool NeedsLogin);

/// <summary>
/// Puente con Claude Code (la CLI <c>claude</c> instalada en la Mac, con la sesión de la suscripción
/// del usuario). Corre en modo no interactivo dentro de la carpeta de estrategias, con permiso solo
/// para leer y editar archivos de esa carpeta, y transmite la respuesta a medida que llega.
/// </summary>
public sealed class ClaudeCodeBridge
{
    private static string? _helpText;
    private static string? _helpCli;

    /// <summary>¿La CLI instalada conoce esta opción? (se lee una vez su --help)</summary>
    public static async Task<Func<string, bool>> SupportsAsync(string cli)
    {
        if (_helpText == null || _helpCli != cli)
        {
            string text = "";
            try
            {
                var psi = Prepare(cli, Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
                psi.ArgumentList.Add("--help");
                using var p = Process.Start(psi)!;
                p.StandardInput.Close();
                var read = p.StandardOutput.ReadToEndAsync();
                if (await Task.WhenAny(read, Task.Delay(15000)) == read) text = await read;
                else p.Kill(true);
            }
            catch
            {
                // sin ayuda: solo las opciones básicas
            }

            _helpText = text;
            _helpCli = cli;
        }

        var help = _helpText;
        return flag => help.Contains(flag + " ", StringComparison.Ordinal) || help.Contains(flag + ",", StringComparison.Ordinal) || help.Contains(flag + "\n", StringComparison.Ordinal);
    }

    private Process? _proc;
    private bool _streamedText;

    /// <summary>Id de la conversación (para seguirla con --resume).</summary>
    public string? SessionId { get; private set; }

    public bool IsRunning => _proc is { HasExited: false };

    /// <summary>Texto de Claude a medida que lo escribe.</summary>
    public event Action<string>? Text;

    /// <summary>Claude usó una herramienta (leyó, creó o editó un archivo).</summary>
    public event Action<string, string?>? ToolUsed;

    /// <summary>Terminó una herramienta (por ejemplo, ya se escribió el archivo).</summary>
    public event Action? ToolFinished;

    public event Action<ClaudeResult>? Finished;

    public void NewConversation() => SessionId = null;

    /// <summary>Sigue una conversación guardada (el próximo mensaje va con --resume a esa sesión).</summary>
    public void Resume(string? sessionId) => SessionId = sessionId;

    private static string? _best;
    private static DateTime _bestAt;
    private static readonly Dictionary<string, (DateTime Stamp, Version? Version)> Versions = new();

    /// <summary>Modelo y esfuerzo con que trabaja Claude en Trading Hub (Opus 5.5 con esfuerzo extra).</summary>
    public static string Model { get; set; } = "claude-opus-5-5";
    public static string Effort { get; set; } = "xhigh";

    /// <summary>
    /// Ruta de la CLI de Claude Code (o null si no está instalada). Si hay varias (la de la Terminal, la que trae la app
    /// de Claude…) usa la más nueva: los modelos nuevos (Opus 5.5) piden una versión reciente.
    /// </summary>
    public static string? FindCli()
    {
        var custom = Environment.GetEnvironmentVariable("TRADINGHUB_CLAUDE_CLI");
        if (!string.IsNullOrEmpty(custom) && File.Exists(custom)) return custom;
        lock (Versions)
        {
            if (_best != null && File.Exists(_best) && (DateTime.UtcNow - _bestAt).TotalMinutes < 10) return _best;
            var found = Candidates().Where(File.Exists).Distinct().ToList();
            _best = found.Select(p => (Path: p, Version: VersionOf(p))).OrderByDescending(x => x.Version ?? new Version(0, 0)).Select(x => x.Path).FirstOrDefault();
            _bestAt = DateTime.UtcNow;
            return _best;
        }
    }

    private static IEnumerable<string> Candidates()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var list = new List<string>
        {
            "/usr/local/bin/claude",
            "/opt/homebrew/bin/claude",
            Path.Combine(home, ".claude", "local", "claude"),
            Path.Combine(home, ".local", "bin", "claude"),
            Path.Combine(home, ".npm-global", "bin", "claude"),
        };
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(':', StringSplitOptions.RemoveEmptyEntries))
            list.Add(Path.Combine(dir, "claude"));
        // la que trae la app de Claude (se actualiza sola): …/claude-code/2.1.295/<id>/claude.app/Contents/MacOS/claude
        var bundled = Path.Combine(home, "Library", "Application Support", "Claude", "claude-code");
        try
        {
            if (Directory.Exists(bundled))
                foreach (var v in Directory.GetDirectories(bundled))
                    foreach (var h in Directory.GetDirectories(v))
                        list.Add(Path.Combine(h, "claude.app", "Contents", "MacOS", "claude"));
        }
        catch (Exception)
        {
            // sin permiso para mirar: se usan las otras
        }

        return list;
    }

    /// <summary>Versión de una CLI ("2.1.295 (Claude Code)" → 2.1.295), con caché por archivo.</summary>
    private static Version? VersionOf(string path)
    {
        try
        {
            var stamp = File.GetLastWriteTimeUtc(path);
            if (Versions.TryGetValue(path, out var v) && v.Stamp == stamp) return v.Version;
            var psi = Prepare(path, Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
            psi.ArgumentList.Add("--version");
            using var p = Process.Start(psi)!;
            p.StandardInput.Close();
            var read = p.StandardOutput.ReadToEndAsync();
            Version? version = null;
            if (read.Wait(TimeSpan.FromSeconds(8)) && Version.TryParse(read.Result.Trim().Split(' ')[0], out var parsed)) version = parsed;
            else if (!p.HasExited) p.Kill(true);
            Versions[path] = (stamp, version);
            return version;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>Versión instalada ("2.1.251 (Claude Code)") o null.</summary>
    public static async Task<string?> VersionAsync()
    {
        var cli = FindCli();
        if (cli == null) return null;
        try
        {
            var psi = Prepare(cli, Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
            psi.ArgumentList.Add("--version");
            using var p = Process.Start(psi)!;
            string output = await p.StandardOutput.ReadToEndAsync();
            await p.WaitForExitAsync();
            return output.Trim();
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Abre la Terminal con Claude Code para iniciar sesión (/login) con la suscripción.</summary>
    public static void OpenLoginTerminal()
    {
        var cli = FindCli() ?? "claude";
        try
        {
            var psi = new ProcessStartInfo("/usr/bin/osascript") { UseShellExecute = false };
            psi.ArgumentList.Add("-e");
            psi.ArgumentList.Add($"tell application \"Terminal\" to do script \"'{cli}' /login\"");
            psi.ArgumentList.Add("-e");
            psi.ArgumentList.Add("tell application \"Terminal\" to activate");
            Process.Start(psi);
        }
        catch
        {
            // sin Terminal: el usuario puede escribir "claude" a mano
        }
    }

    internal static ProcessStartInfo Prepare(string cli, string workDir)
    {
        var psi = new ProcessStartInfo(cli)
        {
            WorkingDirectory = workDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
            StandardOutputEncoding = Encoding.UTF8,
        };

        // entorno limpio: que no herede credenciales temporales de otra sesión de Claude
        foreach (var key in psi.Environment.Keys.ToList())
            if (key.StartsWith("CLAUDE", StringComparison.Ordinal) || key.StartsWith("ANTHROPIC", StringComparison.Ordinal))
                psi.Environment.Remove(key);
        psi.Environment["PATH"] = "/usr/local/bin:/opt/homebrew/bin:/usr/bin:/bin:/usr/sbin:/sbin:" + (Environment.GetEnvironmentVariable("PATH") ?? "");
        psi.Environment["TERM"] = "dumb";
        return psi;
    }

    /// <summary>Herramientas del laboratorio (servidor MCP de Trading Hub).</summary>
    public static readonly string[] LabTools =
    {
        "list_strategies", "compile_strategy", "list_data", "download_data", "run_backtest", "run_optimization", "run_walk_forward", "list_runs",
        "analyze_portfolio", "suggest_portfolio", "live_portfolio", "propose_sizing", "save_report", "list_indicators", "compile_indicator", "chart_snapshot",
    };

    /// <summary>Config MCP que levanta Trading Hub en modo laboratorio (el mismo ejecutable con --mcp).</summary>
    internal static string WriteLabConfig()
    {
        // TRADINGHUB_MCP_COMMAND: otro ejecutable de Trading Hub (para pruebas); si no, este mismo
        var exe = Environment.GetEnvironmentVariable("TRADINGHUB_MCP_COMMAND") is { Length: > 0 } custom ? custom : Environment.ProcessPath ?? "TradingHub";
        var env = new Dictionary<string, string>();
        if (Environment.GetEnvironmentVariable("DOTNET_ROOT") is { Length: > 0 } root) env["DOTNET_ROOT"] = root;
        var config = new Dictionary<string, object>
        {
            ["mcpServers"] = new Dictionary<string, object>
            {
                ["tradinghub"] = new Dictionary<string, object> { ["type"] = "stdio", ["command"] = exe, ["args"] = new[] { "--mcp" }, ["env"] = env },
            },
        };
        Directory.CreateDirectory(AppPaths.Root);
        var path = Path.Combine(AppPaths.Root, "claude-lab-mcp.json");
        File.WriteAllText(path, JsonSerializer.Serialize(config, new JsonSerializerOptions { WriteIndented = true }));
        return path;
    }

    /// <summary>Manda un pedido. Los eventos llegan en el hilo de UI. Con <paramref name="lab"/> Claude puede correr pruebas.</summary>
    public async Task SendAsync(string prompt, string workDir, string systemPrompt, bool lab = false, IReadOnlyList<string>? extraDirs = null)
    {
        if (IsRunning) return;
        var cli = FindCli();
        if (cli == null)
        {
            Post(() => Finished?.Invoke(new ClaudeResult(true, "Claude Code was not found on this Mac. Install it from claude.com/claude-code and retry.", 0, 0, false)));
            return;
        }

        var psi = Prepare(cli, workDir);
        foreach (var a in new[]
                 {
                     "-p", prompt,
                     "--output-format", "stream-json",
                     "--verbose",
                     "--include-partial-messages",
                     "--append-system-prompt", systemPrompt,
                     "--tools", "Read,Write,Edit,MultiEdit,Glob,Grep",
                     "--permission-mode", "acceptEdits",
                     "--strict-mcp-config",
                 })
            psi.ArgumentList.Add(a);
        // Opus 5.5 con esfuerzo extra (las opciones que esta CLI no conoce se omiten)
        var supports = await SupportsAsync(cli);
        if (!string.IsNullOrWhiteSpace(Model))
        {
            psi.ArgumentList.Add("--model");
            psi.ArgumentList.Add(Model);
        }

        if (!string.IsNullOrWhiteSpace(Effort) && supports("--effort"))
        {
            psi.ArgumentList.Add("--effort");
            psi.ArgumentList.Add(Effort);
        }

        // otras carpetas donde puede escribir (por ejemplo la de indicadores)
        foreach (var dir in extraDirs ?? Array.Empty<string>())
        {
            if (!Directory.Exists(dir) || !supports("--add-dir")) continue;
            psi.ArgumentList.Add("--add-dir");
            psi.ArgumentList.Add(dir);
        }

        if (lab)
        {
            // laboratorio: Trading Hub como servidor MCP (backtest, optimización, walk-forward, reportes)
            psi.ArgumentList.Add("--mcp-config");
            psi.ArgumentList.Add(WriteLabConfig());
            psi.ArgumentList.Add("--allowedTools");
            psi.ArgumentList.Add(string.Join(",", LabTools.Select(t => "mcp__tradinghub__" + t)));
            // una optimización o un walk-forward pueden tardar varios minutos
            psi.Environment["MCP_TOOL_TIMEOUT"] = "3600000";
            psi.Environment["MCP_TIMEOUT"] = "60000";
        }

        if (SessionId != null)
        {
            psi.ArgumentList.Add("--resume");
            psi.ArgumentList.Add(SessionId);
        }

        var sw = Stopwatch.StartNew();
        var finalText = new StringBuilder();
        var stderr = new StringBuilder();
        bool isError = false, gotResult = false;
        double cost = 0;
        try
        {
            _proc = Process.Start(psi)!;
            _proc.StandardInput.Close();
            _proc.ErrorDataReceived += (_, e) =>
            {
                if (e.Data != null) stderr.AppendLine(e.Data);
            };
            _proc.BeginErrorReadLine();
            string? line;
            while ((line = await _proc.StandardOutput.ReadLineAsync()) != null)
            {
                if (line.Length == 0 || line[0] != '{') continue;
                try
                {
                    using var doc = JsonDocument.Parse(line);
                    var root = doc.RootElement;
                    string type = root.TryGetProperty("type", out var t) ? t.GetString() ?? "" : "";
                    if (root.TryGetProperty("session_id", out var sid) && sid.ValueKind == JsonValueKind.String) SessionId = sid.GetString();
                    switch (type)
                    {
                        case "stream_event":
                            HandleStreamEvent(root);
                            break;
                        case "assistant":
                            HandleAssistant(root);
                            break;
                        case "user":
                            Post(() => ToolFinished?.Invoke());
                            break;
                        case "result":
                            gotResult = true;
                            isError = root.TryGetProperty("is_error", out var ie) && ie.ValueKind == JsonValueKind.True;
                            if (root.TryGetProperty("result", out var r) && r.ValueKind == JsonValueKind.String) finalText.Append(r.GetString());
                            if (root.TryGetProperty("total_cost_usd", out var c) && c.ValueKind == JsonValueKind.Number) cost = c.GetDouble();
                            break;
                    }
                }
                catch (JsonException)
                {
                    // línea que no es JSON: se ignora
                }
            }

            await _proc.WaitForExitAsync();
            if (!gotResult)
            {
                isError = true;
                finalText.Append(stderr.Length > 0 ? stderr.ToString().Trim() : $"Claude Code exited without a response (code {_proc.ExitCode}).");
            }
        }
        catch (Exception ex)
        {
            isError = true;
            finalText.Append("Could not run Claude Code: " + ex.Message);
        }
        finally
        {
            _proc?.Dispose();
            _proc = null;
        }

        string text = finalText.ToString();
        bool needsLogin = isError && (text.Contains("authenticate", StringComparison.OrdinalIgnoreCase) || text.Contains("/login", StringComparison.OrdinalIgnoreCase) || text.Contains("not logged in", StringComparison.OrdinalIgnoreCase) || text.Contains("OAuth", StringComparison.OrdinalIgnoreCase));
        var result = new ClaudeResult(isError, text, cost, sw.Elapsed.TotalSeconds, needsLogin);
        Post(() => Finished?.Invoke(result));
    }

    private void HandleStreamEvent(JsonElement root)
    {
        if (!root.TryGetProperty("event", out var ev)) return;
        string evType = ev.TryGetProperty("type", out var et) ? et.GetString() ?? "" : "";
        if (evType == "message_start") _streamedText = false;
        if (evType != "content_block_delta" || !ev.TryGetProperty("delta", out var delta)) return;
        if (delta.TryGetProperty("type", out var dt) && dt.GetString() == "text_delta" && delta.TryGetProperty("text", out var txt))
        {
            _streamedText = true;
            string s = txt.GetString() ?? "";
            Post(() => Text?.Invoke(s));
        }
    }

    private void HandleAssistant(JsonElement root)
    {
        if (!root.TryGetProperty("message", out var msg) || !msg.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array) return;
        foreach (var block in content.EnumerateArray())
        {
            string bt = block.TryGetProperty("type", out var b) ? b.GetString() ?? "" : "";
            if (bt == "text" && !_streamedText && block.TryGetProperty("text", out var txt))
            {
                string s = txt.GetString() ?? "";
                Post(() => Text?.Invoke(s));
            }
            else if (bt == "tool_use")
            {
                string name = block.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                string? file = null;
                if (block.TryGetProperty("input", out var input) && input.ValueKind == JsonValueKind.Object)
                {
                    if (input.TryGetProperty("file_path", out var fp)) file = fp.GetString();
                    else if (input.TryGetProperty("pattern", out var pat)) file = pat.GetString();
                    else if (name.StartsWith("mcp__", StringComparison.Ordinal))
                    {
                        // laboratorio: estrategia, instrumento y rango
                        string S(string k) => input.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
                        var parts = new[] { S("file"), S("symbol"), S("from").Length > 0 ? $"{S("from")} → {S("to")}" : "", S("title") }.Where(x => x.Length > 0);
                        file = string.Join(" · ", parts);
                    }
                }

                Post(() => ToolUsed?.Invoke(name, file));
            }
        }

        _streamedText = false;
    }

    public void Cancel()
    {
        try
        {
            if (_proc is { HasExited: false }) _proc.Kill(entireProcessTree: true);
        }
        catch
        {
            // ya terminó
        }
    }

    private static void Post(Action a) => Dispatcher.UIThread.Post(a);

    /// <summary>Método de trabajo del laboratorio (se suma a las instrucciones de estrategias).</summary>
    public const string LabSystemPrompt = """
        LABORATORY MODE. Use the mcp__tradinghub__* tools to evaluate strategies autonomously, without requesting routine decisions from the user:
        list_strategies, compile_strategy, list_data, download_data, run_backtest (with monte_carlo_runs), run_optimization,
        run_walk_forward, list_runs and save_report. Runs are saved in the strategy's Trading Hub history.

        Method (choose reasonable parameters, ranges and data; iterate until the evidence supports a conclusion):
        1. Inspect the strategy with list_strategies and Read, and data with list_data. For binance-futures, binance-spot or
           bybit-linear, use download_data if history is insufficient; aim for 30–90 days (about 5 seconds per day).
        2. Run a baseline backtest with default parameters, 0.04% commission and 1 tick of slippage unless the user specifies otherwise.
        3. Optimize sensible ranges, below about 2,000 combinations, using NetProfitOverDrawdown or Sharpe and min_trades ≥ 30.
           Inspect the proportion of profitable combinations (robustness), not just the best one.
        4. Run walk-forward with the same ranges, using an optimization window 3–4 times the test window. Judge out-of-sample
           efficiency, profitable-window percentage, profit factor and drawdown.
        5. Run Monte Carlo with monte_carlo_runs: 1000 on the best validated parameters.
        6. If the logic has clear defects, edit the .cs, compile_strategy and repeat evaluation. Always compare with the previous
           version; reject changes that improve only in-sample results.
        7. Report verdicts in English. The persisted API codes remain unchanged for compatibility:
           - ganadora (profitable): positive out-of-sample PnL, walk-forward efficiency ≥ 0.5, out-of-sample PF ≥ 1.2,
             at least 30 trades, reasonable Monte Carlo p95 drawdown for the capital, and stable results across windows.
           - prometedora (promising): positive results but insufficient sample or some instability; more data needed.
           - perdedora (unprofitable): out-of-sample losses or profits only through overfitting.
           - descartada (rejected): does not compile or trade, or the idea is unsuitable for these data.
        8. For multiple strategies or instruments with backtests, use analyze_portfolio to measure daily-PnL correlation and
           suggest_portfolio to find combinations with correlation < 0.5 that improve aggregate Sharpe and reduce drawdown.
           For a live-portfolio review, use live_portfolio: inspect total/recent-20-trade PF, current/maximum drawdown and results.
           Use propose_sizing to suggest larger sizes (up to ×3) for consistent performers, or reductions/pause (0) for degradation,
           with concrete numerical reasons. Do not draw conclusions from fewer than about 10 trades. Nothing applies automatically:
           the user approves every change. A live review does not require save_report unless requested.
        9. ALWAYS finish strategy evaluation with save_report. Trading Hub generates the detailed report, metrics, tables and charts
           from the runs. Supply backtest_run_id (best validated, including Monte Carlo), optimization_run_id, walk_forward_run_id,
           and portfolio when applicable. Write verdict, summary (2–5 lines), analysis, risks and next_steps in English Markdown.
           Discuss strengths/weaknesses, optimization sensitivity, walk-forward efficiency and stability, Monte Carlo and code-version
           comparisons. Explain code changes in extra_markdown.
        Never invent results: every reported number must come from a tool. Finish the chat in 2–4 English lines with the verdict
        and key numbers; put details in the report.
        Use chart_snapshot to inspect exact bars, entry/exit marks, drawings and indicator lines. Read the returned PNG with Read;
        also inspect local-time bars, trades, drawings and logs. Use this when a user reports a timing mismatch, or to verify that
        implemented logic matches its description. Bar timestamps are opening times; inspect the exact bar and order involved.
        """;

    /// <summary>Instrucciones para que Claude escriba indicadores propios (además de estrategias).</summary>
    public const string IndicatorSystemPrompt = """
        INDICATORS (when the request is for an indicator, not a strategy):
        - Write one .cs file per indicator in the user's indicator directory, supplied in context and permitted for editing.
          Match the class and filename. Saving recompiles it automatically under Indicators → My indicators. Do not put it in
          the strategies directory.
        - Usings: System.ComponentModel, System.ComponentModel.DataAnnotations, System.Drawing (Color.Orange,
          Color.FromArgb(a, r, g, b)), TradingHub.Indicators, TradingHub.Indicators.Drawing and, for custom rendering,
          TradingHub.Rendering.Context and TradingHub.Rendering.Tools. System, System.Linq and System.Collections.Generic are implicit.
        - Use [DisplayName("Display name")] [Category("My indicators")] public class MyIndicator : Indicator, with a parameterless constructor.
        - Drawable series: ValueDataSeries for lines/histograms, for example new ValueDataSeries("Id", "Name")
          { Color = Color.Orange, Width = 2, VisualType = VisualMode.Line }; VisualMode.Histogram is also supported.
          RangeDataSeries uses series[bar] = new RangeValue { Upper = upper, Lower = lower }; PaintbarsDataSeries uses
          series[bar] = Color.Lime. Assign the primary series to DataSeries[0] in the constructor and add others with DataSeries.Add.
        - Panel defaults to price. Set Panel = IndicatorDataProvider.NewPanel in the constructor for a separate oscillator panel.
        - Parameters are public properties with [Display(Name = "Period", GroupName = "Settings")] and [Range(1, 1000)].
          Store the value and call RecalculateValues() from the setter.
        - protected override void OnCalculate(int bar, decimal value) runs per bar (zero-based index, value = close).
          GetCandle(bar) exposes Open, High, Low, Close, Volume, Ask, Bid, Delta, MaxDelta, MinDelta and Time.
          CurrentBar is the number of bars. Write series[bar], read series[bar - 1], and guard negative indices during warmup.
        - Optional custom drawing: set EnableCustomDrawing = true and SubscribeToDrawingEvents(DrawingLayouts.Final) in the constructor.
          Override OnRender(RenderContext context, DrawingLayouts layout). Use ChartInfo.GetXByBar(bar), ChartInfo.GetYByPrice(price),
          context.DrawLine(new RenderPen(color, 1), x1, y1, x2, y2), context.FillRectangle(color, new Rectangle(x, y, width, height)),
          and context.DrawString(text, new RenderFont("Arial", 9), color, x, y). Visible bars are FirstVisibleBarNumber through
          LastVisibleBarNumber.
        - With laboratory tools, use compile_indicator and list_indicators, fixing errors until compilation succeeds.
          Without laboratory mode, Trading Hub compiles at completion and reports diagnostics.
        Write display labels, comments and explanations in English. Do not rename stable identifiers in existing user code.
        """;

    /// <summary>Instrucciones para que Claude escriba estrategias que compilen en Trading Hub.</summary>
    public const string StrategySystemPrompt = """
        You are Trading Hub's strategy assistant, working inside the user's strategy directory (the current directory).
        Always respond briefly in English. Write all new display labels, descriptions and comments in English.

        Trading Hub strategy API:
        - One .cs file per strategy in the current directory; class and filename match. Use using TradingHub.Strategies;
          System, System.Linq and System.Collections.Generic are implicit.
        - [Strategy("Display name", Description = "...")] public class MyStrategy : Strategy, with a parameterless constructor.
        - Editable parameters: public properties with [Parameter("Name")]; add [Optimize(min, max, step)] for optimization ranges.
        - Lifecycle: protected override void OnInit() creates indicators; protected override void OnBar(Bar bar) runs at bar close.
          Optional methods: OnTick(Trade trade), OnOrderFilled(StrategyOrder order), OnSessionStart(DateOnly session), OnStop().
        - Bars[0] is the latest closed bar; Bars[1] is the previous one. Bar provides Open, High, Low, Close, Volume,
          BuyVolume, SellVolume, Delta, MaxDelta, MinDelta, Trades, PocPrice, IsBullish, IsBearish, Range, Body, Typical,
          Time, CloseTime and Index. bar.Levels maps price to (Buy, Sell) when UseFootprint = true is set in OnInit.
          Also available: Price, Time (UTC), LocalTime, TickSize, CurrentBar, Instrument, IsBacktest.
        - Create indicators in OnInit and retain fields: Sma(n), Ema(n), Rsi(n), Atr(n), Vwap(), Bollinger(n, k)
          (.Upper/.Lower/.Middle), Macd(12,26,9) (.Signal/.Histogram), Highest(n), Lowest(n), StdDev(n), CumulativeDelta(),
          Custom("Name", bars => value). Read ind[0] (current), ind[1] (previous), ind.Value and ind.IsReady.
          PriceSource values: Close, Open, High, Low, Typical, Median, Volume, Delta.
        - Orders: EnterLong(), EnterShort() reverse when necessary; Buy(quantity), Sell(quantity), BuyLimit(price), SellLimit(price),
          BuyStop(price), SellStop(price), ClosePosition(), CancelAll(), Cancel(order), Modify(order, price).
          Automatic OCO protection: StopLossTicks, TakeProfitTicks, TrailingStopTicks, configurable in the constructor.
          Quantity is the default order quantity. Do not assume an immediate fill.
        - Position: Position (>0 long, <0 short), IsFlat, IsLong, IsShort, AveragePrice, OpenPnL, NetProfit, ActiveOrders.
        - Helpers: CrossAbove(a, b), CrossBelow(a, b) for series or a series against a number; Ticks(n), Round(price),
          TimeBetween(TimeSpan from, TimeSpan to) in local session time, and Log("text"). Prices and volumes are decimal;
          cast double to decimal where required.
        - Chart drawings appear over historical and live bars. Use UTC times such as bar.Time and Bars[n].Time.
          Colors accept #RRGGBB, #AARRGGBB and English names such as green, red, blue, cyan, yellow, orange, purple, pink,
          gray and white. Omitted colors use the strategy color. Reusing an ID replaces the drawing; use distinct IDs for
          distinct objects, for example $"range-{bar.Time:yyyyMMdd}".
            DrawBox(id, from, price1, to, price2, color, fillOpacity: 15, text) draws a rectangle.
            DrawLine(id, time1, price1, time2, price2, color, width, LineStyle.Solid|Dashed|Dotted)
            DrawHorizontalLine(id, price, from: null, color, width, style, text) extends to the right edge.
            DrawArrowUp(id, time, price, color, text), DrawArrowDown(...),
            DrawMarker(id, time, price, MarkerShape.Circle|Square|Diamond|Cross|ArrowUp|ArrowDown, color, text),
            DrawText(id, time, price, "text", color),
            Plot("Name", value, color, width, style) draws a continuous bar-by-bar line; call in OnBar, null breaks the line.
            RemoveDrawing(id), ClearDrawings().
          Example: Plot("High", _max[0], "green"); Plot("Low", _min[0], "red");
          DrawArrowUp($"breakout-{bar.Time:yyyyMMddHHmm}", bar.Time, bar.Close, "green", "breakout").
          Maximum 3,000 drawings; oldest are removed. Do not create unnecessary new objects every bar.
        - Market context uses closed bars only, never future data: MarketTrend is 24-hour trend efficiency (0 = pure range,
          1 = clean trend, random ≈ 0.2). Regime is MarketRegime.Range up to 0.15, Mixed, Trend from 0.35, or Unknown without data.
          MarketDirection is 1 for an upward 24-hour move and -1 for a downward move. MarketChange is net 24-hour percentage change.
          MarketVolatility is the standard deviation of hourly returns over 24 hours, in percent. VolatilityRank is its percentile
          over the last month (0 = lowest, 100 = highest). Values may be null. Live context starts with the last month of hourly history.
        - Built-in context filters require no custom code: RegimeFilter accepts All, RangeOnly, NoTrend, TrendOnly, NoRange,
          UpTrendOnly, DownTrendOnly, MixedOnly, RangeOrUpTrend, RangeOrDownTrend, MixedOrUpTrend, MixedOrDownTrend,
          NoDownTrend, NoUpTrend, NoMixed. MinVolatilityRank and MaxVolatilityRank range from 0 to 100. Outside selected conditions,
          entries and position increases are blocked; exits always pass. Configure these in backtesting, optimization,
          walk-forward and live execution. ContextAllowsEntries reports whether an entry is currently permitted.

        Rules:
        - Generate compiling C# 13 code with no external packages. Do not use files, network access, threads or unseeded Random.
        - Keep comments short and in English. Preserve existing user identifiers, parameter keys, order IDs and data formats.
        - Create a clearly named PascalCase file for a new strategy; edit the specified file for an existing one.
        - Fix supplied compilation diagnostics in the affected file.
        - Finish in 2–5 English lines explaining the strategy or changes without pasting code already visible in the editor.
        """;
}
