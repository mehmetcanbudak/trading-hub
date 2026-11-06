using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Avalonia.Threading;

namespace TradingHub.App.Services;

/// <summary>
/// Runs the user's subscription-authenticated Claude Code CLI on macOS or Windows, restricted to the
/// requested workspace, and streams its response. No provider API credentials are required.
/// </summary>
public sealed class ClaudeCodeBridge : StrategyChatProcess
{
    public ClaudeCodeBridge(TradingViewResearchService? research = null) : base(research) { }
    private static string? _helpText;
    private static string? _helpCli;

    /// <summary>¿La CLI instalada conoce esta opción? (se lee una vez su --help)</summary>
    public static async Task<Func<string, bool>> SupportsAsync(string cli, CancellationToken cancellationToken = default)
    {
        if (_helpText == null || !PathComparer.Equals(_helpCli, cli))
        {
            string text = "";
            try
            {
                text = (await HelpAsync(cli, Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), cancellationToken, "--help")).Replace("\r\n", "\n");
            }
            catch (Exception) when (!cancellationToken.IsCancellationRequested)
            {
                // sin ayuda: solo las opciones básicas
            }

            _helpText = text;
            _helpCli = cli;
        }

        var help = _helpText;
        return flag => help.Contains(flag + " ", StringComparison.Ordinal) || help.Contains(flag + ",", StringComparison.Ordinal) || help.Contains(flag + "\n", StringComparison.Ordinal);
    }


    private static string? _best;
    private static DateTime _bestAt;
    private static readonly Dictionary<string, (DateTime Stamp, Version? Version)> Versions = new(PathComparer);

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
        if (!string.IsNullOrEmpty(custom)) return File.Exists(custom) ? custom : null;
        lock (Versions)
        {
            if (_best != null && File.Exists(_best) && (DateTime.UtcNow - _bestAt).TotalMinutes < 10) return _best;
            var found = (OperatingSystem.IsWindows() ? WindowsCandidates() : Candidates()).Where(File.Exists).Distinct(PathComparer).ToList();
            // Prefer a native executable over an npm shim when their versions match.
            _best = found.Select(p => (Path: p, Version: VersionOf(p))).OrderByDescending(x => x.Version ?? new Version(0, 0))
                .ThenBy(x => IsBatch(x.Path)).Select(x => x.Path).FirstOrDefault();
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

    private static StringComparer PathComparer => OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    internal static bool IsBatch(string path) =>
        path.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".bat", StringComparison.OrdinalIgnoreCase);

    /// <summary>Official installer, npm, WinGet, current/saved PATH, and the Claude desktop application's bundled CLI.</summary>
    private static IEnumerable<string> WindowsCandidates()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var list = WindowsCandidates(home, appData, localAppData, WindowsPath()).ToList();
        list.AddRange(WhereClaude());
        var bundled = Path.Combine(appData, "Claude", "claude-code");
        try
        {
            if (Directory.Exists(bundled))
                foreach (var version in Directory.GetDirectories(bundled))
                {
                    list.Add(Path.Combine(version, "claude.exe"));
                    foreach (var installation in Directory.GetDirectories(version))
                        list.Add(Path.Combine(installation, "claude.exe"));
                }
        }
        catch (Exception)
        {
            // Other installations remain available if the bundled directory cannot be read.
        }

        return list;
    }

    internal static IEnumerable<string> WindowsCandidates(string home, string appData, string localAppData, string? path)
    {
        var dirs = new List<string>
        {
            Path.Combine(home, ".local", "bin"),
            Path.Combine(home, ".claude", "local"),
            Path.Combine(appData, "npm"),
            Path.Combine(localAppData, "Microsoft", "WinGet", "Links"),
        };
        foreach (var dir in (path ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            dirs.Add(Environment.ExpandEnvironmentVariables(dir.Trim('"')));
        foreach (var dir in dirs)
        {
            yield return Path.Combine(dir, "claude.exe");
            yield return Path.Combine(dir, "claude.cmd");
            yield return Path.Combine(dir, "claude.bat");
        }
    }

    private static string WindowsPath() => string.Join(';',
        Environment.GetEnvironmentVariable("PATH") ?? "",
        Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.User) ?? "",
        Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.Machine) ?? "");

    private static IEnumerable<string> WhereClaude()
    {
        try
        {
            var psi = new ProcessStartInfo("where.exe")
            {
                RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true,
            };
            psi.ArgumentList.Add("claude");
            using var process = Process.Start(psi)!;
            var read = process.StandardOutput.ReadToEndAsync();
            if (read.Wait(TimeSpan.FromSeconds(5))) return ParseWhere(read.Result);
            if (!process.HasExited) process.Kill();
        }
        catch (Exception)
        {
            // Explicit installer paths and PATH discovery do not depend on where.exe.
        }

        return Array.Empty<string>();
    }

    internal static List<string> ParseWhere(string output) =>
        output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(line => line.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || IsBatch(line))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    private static string? FindOnWindowsPath(string executable) =>
        WindowsPath().Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(dir => Path.Combine(Environment.ExpandEnvironmentVariables(dir.Trim('"')), executable))
            .FirstOrDefault(File.Exists);

    /// <summary>
    /// Bypass npm/pnpm/yarn batch interpreters: cmd.exe cannot preserve multiline prompts, quotes, or
    /// metacharacters. Launch their JavaScript entry point with node, or their native executable directly.
    /// </summary>
    internal static (string File, string[] Prefix) WindowsCommand(string cli, Func<string, string?>? findOnPath = null)
    {
        string current = cli;
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while (IsBatch(current) && visited.Count < 16 && visited.Add(Path.GetFullPath(current)))
        {
            string? target = null;
            try
            {
                target = CmdShimTarget(File.ReadAllText(current));
            }
            catch (IOException)
            {
                // Older npm installations have a predictable entry point next to the shim.
            }
            catch (UnauthorizedAccessException)
            {
                // The known npm entry point may still be readable.
            }

            target ??= Path.GetFileNameWithoutExtension(cli).Equals("codex", StringComparison.OrdinalIgnoreCase)
                ? @"node_modules\@openai\codex\bin\codex.js" : @"node_modules\@anthropic-ai\claude-code\cli.js";
            var dir = Path.GetDirectoryName(Path.GetFullPath(current))!;
            var full = Path.GetFullPath(Path.Combine(dir, target.Replace('\\', Path.DirectorySeparatorChar)));
            if (!File.Exists(full)) break;
            if (IsBatch(full))
            {
                current = full;
                continue;
            }

            if (full.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) return (full, Array.Empty<string>());
            if (!(full.EndsWith(".js", StringComparison.OrdinalIgnoreCase) || full.EndsWith(".cjs", StringComparison.OrdinalIgnoreCase) ||
                  full.EndsWith(".mjs", StringComparison.OrdinalIgnoreCase))) break;
            var node = Path.Combine(dir, "node.exe");
            if (!File.Exists(node)) node = (findOnPath ?? FindOnWindowsPath)("node.exe") ?? "node.exe";
            return (node, new[] { full });
        }

        if (!IsBatch(current)) return (current, Array.Empty<string>());
        throw new InvalidOperationException("Cannot resolve this assistant command shim without altering its arguments. Reinstall the selected CLI using its official installer.");
    }

    internal static string? CmdShimTarget(string content)
    {
        string? target = null;
        foreach (Match match in Regex.Matches(content, @"""%~?dp0%?\\?([^""%]+)"""))
        {
            var relative = match.Groups[1].Value.Trim();
            if (relative.Length > 0 && !relative.EndsWith("node.exe", StringComparison.OrdinalIgnoreCase)) target = relative;
        }

        return target;
    }

    internal const int MaxWindowsCommandLine = 32000;

    /// <summary>Conservative bound including Windows argument quoting and escaping.</summary>
    internal static int WindowsCommandLineLength(string fileName, IEnumerable<string> args) =>
        fileName.Length + 2 + args.Sum(argument => 1 + argument.Length + 2 + argument.Count(character => character is '"' or '\\'));

    internal static string? MovePromptToStdinIfTooLong(ProcessStartInfo psi)
    {
        if (!OperatingSystem.IsWindows() || WindowsCommandLineLength(psi.FileName, psi.ArgumentList) <= MaxWindowsCommandLine) return null;
        int index = psi.ArgumentList.IndexOf("-p");
        if (index < 0 || index + 1 >= psi.ArgumentList.Count) return null;
        string prompt = psi.ArgumentList[index + 1];
        psi.ArgumentList.RemoveAt(index + 1);
        psi.StandardInputEncoding = new UTF8Encoding(false);
        return prompt;
    }

    /// <summary>Write the exact prompt without a BOM or an extra newline while output is read concurrently.</summary>
    internal static async Task FeedStdinAsync(Process process, string? prompt)
    {
        try
        {
            if (prompt != null) await process.StandardInput.WriteAsync(prompt);
        }
        finally
        {
            process.StandardInput.Close();
        }
    }

    internal static string WindowsLoginArguments(string cli, bool keepOpen = true) => $"/d /v:off /s /{(keepOpen ? "k" : "c")} \"\"{cli}\" /login\"";

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
            if (OperatingSystem.IsWindows())
            {
                // Expand the path once through an environment variable, so literal % characters in it survive.
                var login = new ProcessStartInfo("cmd.exe", WindowsLoginArguments("%TRADINGHUB_CLAUDE_LOGIN%")) { UseShellExecute = false };
                login.Environment["TRADINGHUB_CLAUDE_LOGIN"] = cli;
                Process.Start(login)?.Dispose();
                return;
            }

            var psi = new ProcessStartInfo("/usr/bin/osascript") { UseShellExecute = false };
            psi.ArgumentList.Add("-e");
            var command = "'" + cli.Replace("'", "'\\''") + "' /login";
            psi.ArgumentList.Add("tell application \"Terminal\" to do script \"" + command.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"");
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
        var (file, prefix) = OperatingSystem.IsWindows() ? WindowsCommand(cli) : (cli, Array.Empty<string>());
        var psi = new ProcessStartInfo(file)
        {
            WorkingDirectory = workDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
            CreateNoWindow = OperatingSystem.IsWindows(),
            StandardOutputEncoding = Encoding.UTF8,
        };
        foreach (var argument in prefix) psi.ArgumentList.Add(argument);

        // Retain the subscription session, but do not inherit temporary API credentials or nested-session flags.
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        foreach (var key in psi.Environment.Keys.ToList())
            if ((key.StartsWith("CLAUDE", comparison) || key.StartsWith("ANTHROPIC", comparison))
                && !(OperatingSystem.IsWindows() && key.Equals("CLAUDE_CODE_GIT_BASH_PATH", comparison)))
                psi.Environment.Remove(key);
        if (OperatingSystem.IsWindows())
        {
            psi.Environment["PATH"] = WindowsPath();
            psi.StandardErrorEncoding = Encoding.UTF8;
            psi.StandardInputEncoding = new UTF8Encoding(false);
        }
        else psi.Environment["PATH"] = "/usr/local/bin:/opt/homebrew/bin:/usr/bin:/bin:/usr/sbin:/sbin:" + (Environment.GetEnvironmentVariable("PATH") ?? "");
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
        var launch = StrategyMcpProcess.LabCommand();
        var env = StrategyMcpProcess.ChildEnvironment();
        var config = new Dictionary<string, object>
        {
            ["mcpServers"] = new Dictionary<string, object>
            {
                ["tradinghub"] = new Dictionary<string, object> { ["type"] = "stdio", ["command"] = launch.Command, ["args"] = launch.Arguments, ["env"] = env },
            },
        };
        Directory.CreateDirectory(AppPaths.Root);
        var path = Path.Combine(AppPaths.Root, "claude-lab-mcp.json");
        File.WriteAllText(path, JsonSerializer.Serialize(config, new JsonSerializerOptions { WriteIndented = true }));
        return path;
    }

    public override string Engine => "claude";
    protected override string? FindExecutable() => FindCli();
    private bool _streamedText;
    protected override void ResetParser() => _streamedText = false;
    protected override string Input(StrategyChatRequest request) => request.Prompt;

    protected override async Task<ProcessStartInfo> BuildStartAsync(string cli, StrategyChatRequest request, CancellationToken token)
    {
        var psi = Prepare(cli, request.WorkDirectory);
        psi.StandardInputEncoding = new UTF8Encoding(false);
        Add(psi, "-p", "--output-format", "stream-json", "--verbose", "--include-partial-messages",
            "--tools", request.TextOnly ? "" : "Read,Write,Edit,MultiEdit,Glob,Grep", "--permission-mode", request.TextOnly ? "plan" : "acceptEdits", "--strict-mcp-config");
        Add(psi, "--append-system-prompt", request.SystemPrompt + Localization.Lang.ForClaude);
        var supports = await SupportsAsync(cli, token);
        token.ThrowIfCancellationRequested();
        if (request.TextOnly)
        {
            foreach (string flag in new[] { "--restricted", "--safe-mode", "--tools", "--no-session-persistence" })
                if (!supports(flag)) throw new InvalidOperationException($"Draft-only Pine handoff requires {flag}. Update Claude Code before sending source.");
            Add(psi, "--restricted", "--safe-mode", "--no-session-persistence");
        }
        if (!string.IsNullOrWhiteSpace(request.Model)) Add(psi, "--model", request.Model);
        if (!string.IsNullOrWhiteSpace(request.Effort) && supports("--effort")) Add(psi, "--effort", request.Effort);
        foreach (var dir in request.ExtraDirectories)
            if (Directory.Exists(dir) && supports("--add-dir")) Add(psi, "--add-dir", dir);
        var servers = new Dictionary<string, JsonElement>();
        if (request.Laboratory)
        {
            using var lab = JsonDocument.Parse(File.ReadAllText(WriteLabConfig()));
            foreach (var property in lab.RootElement.GetProperty("mcpServers").EnumerateObject()) servers.Add(property.Name, property.Value.Clone());
        }
        if (request.TradingView != null)
        {
            using var research = JsonDocument.Parse(request.TradingView.CreateMcpConfigJson());
            foreach (var property in research.RootElement.GetProperty("mcpServers").EnumerateObject()) servers.Add(property.Name, property.Value.Clone());
        }
        // Inline config is non-secret; strict mode excludes every inherited MCP server, including when off.
        Add(psi, "--mcp-config", JsonSerializer.Serialize(new { mcpServers = servers }));
        var allowed = new List<string>();
        if (request.Laboratory) allowed.AddRange(LabTools.Select(t => "mcp__tradinghub__" + t));
        if (request.TradingView != null) allowed.AddRange(request.TradingView.ApprovedToolNames.Select(t => "mcp__tradingview_research__" + t));
        if (allowed.Count != 0) Add(psi, "--allowedTools", string.Join(",", allowed));
        if (SessionId != null && !request.TextOnly) Add(psi, "--resume", SessionId);
        return psi;
    }

    protected override void Parse(JsonElement root)
    {
        if (Value(root, "session_id") is { Length: > 0 } id) ObserveSession(id);
        switch (Value(root, "type"))
        {
            case "stream_event":
                if (!root.TryGetProperty("event", out var ev)) break;
                if (Value(ev, "type") == "message_start") _streamedText = false;
                if (Value(ev, "type") == "content_block_delta" && ev.TryGetProperty("delta", out var delta) && Value(delta, "type") == "text_delta")
                { _streamedText = true; EmitText(Value(delta, "text")); }
                break;
            case "assistant":
                if (!root.TryGetProperty("message", out var message) || !message.TryGetProperty("content", out var blocks) || blocks.ValueKind != JsonValueKind.Array) break;
                foreach (var block in blocks.EnumerateArray())
                {
                    if (Value(block, "type") == "text" && !_streamedText) EmitText(Value(block, "text"));
                    else if (Value(block, "type") == "tool_use")
                    {
                        string? file = null;
                        if (block.TryGetProperty("input", out var input)) file = Value(input, "file_path") is { Length: > 0 } path ? path : Value(input, "pattern");
                        EmitTool(Value(block, "name"), file);
                    }
                }
                _streamedText = false;
                break;
            case "user": EmitToolFinished(); break;
            case "result":
                Terminal = true;
                Failed = root.TryGetProperty("is_error", out var error) && error.ValueKind == JsonValueKind.True;
                FinalText = Value(root, "result");
                if (Failed && FinalText.Length == 0)
                    FinalText = root.TryGetProperty("errors", out var errors) && errors.ValueKind == JsonValueKind.Array
                        ? string.Join("\n", errors.EnumerateArray().Select(e => e.ValueKind == JsonValueKind.String ? e.GetString() : e.GetRawText()))
                        : "Claude request failed: " + Value(root, "subtype");
                if (root.TryGetProperty("total_cost_usd", out var cost) && cost.ValueKind == JsonValueKind.Number) ReportedCost = cost.GetDouble();
                break;
        }
    }

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
           Check reported financial quality before interpreting live metrics. Null financial values are unavailable, not zero;
           explicitly labelled estimates and legacy records are not confirmed execution economics. Do not infer zero fees,
           exchange rates or slippage, or recommend increasing exposure solely from incomplete or estimated results.
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
        Use English filenames, class/parameter/local identifiers, display labels, comments and explanations for new code. Rename existing identifiers only when explicitly requested, after checking saved references.
        """;

    /// <summary>Instrucciones para que Claude escriba estrategias que compilen en Trading Hub.</summary>
    public const string StrategySystemPrompt = """
        You are Trading Hub's strategy assistant, working inside the user's strategy directory (the current directory).
        Always respond briefly in English. Use English filenames, class/parameter/local identifiers, display labels, descriptions and comments for all new code.

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
        - Keep comments short and in English. Preserve existing user identifiers, parameter keys, order IDs and data formats unless the user explicitly requests a rename and saved references have been checked.
        - Create a clearly named PascalCase file for a new strategy; edit the specified file for an existing one.
        - Fix supplied compilation diagnostics in the affected file.
        - Finish in 2–5 English lines explaining the strategy or changes without pasting code already visible in the editor.
        """;
}
