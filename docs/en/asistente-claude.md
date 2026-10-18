[← Documentation index](README.md)

# Claude in Trading Hub

Trading Hub runs **Claude Code** using your own logged-in session and subscription, not a separate Trading Hub/Claude API account. Usage counts against your CLI allowance. Without it, the platform still works except for the assistant, laboratory and risk agent.

| Location | Capability |
|---|---|
| Backtesting → Code, Claude panel | Write, explain and repair strategies/indicators. |
| Same panel, Laboratory enabled | Run backtests, optimization, walk-forward and Monte Carlo; create reports/verdicts. |
| Live → Portfolios | Per-portfolio [risk agent](en-vivo.md#risk-agent). |
| Live → Proposals | Ask the laboratory to review current strategies and propose sizes. |

## Finding Claude Code

The app searches `/usr/local/bin`, `/opt/homebrew/bin`, `~/.claude/local`, `~/.local/bin`, `~/.npm-global/bin`, PATH and supported Claude desktop CLI locations under `~/Library/Application Support/Claude/claude-code/…`. It chooses the newest discovered version. Override with `TRADINGHUB_CLAUDE_CLI`; see [Installation](instalacion.md#claude-code-optional).

The panel displays CLI version/subscription or a not-found message. **Sign in to Claude Code** opens Terminal with `claude /login`; retry after signing in. Unsupported model/version errors require a compatible CLI; update through the installed distribution's normal update mechanism, such as `claude update` where supported.

## Model, effort and permissions

- The source defaults to `claude-opus-5-5` with `xhigh` effort (Extra), omitting effort when unsupported. Risk agents can select the configured Opus/Sonnet/Haiku/Fable model options and Low/Medium/High/Extra/Maximum effort. These identifiers are configuration defaults, **not a guarantee your CLI/provider offers them**.
- Runs noninteractively with `claude -p` in the strategy directory. File editing is permitted in strategy, indicator and laboratory-screenshot folders. Assistant tools are limited to Read, Write, Edit, MultiEdit, Glob and Grep—no terminal or web tool.
- `--strict-mcp-config` avoids inheriting unrelated MCP servers; laboratory mode exposes Trading Hub's server only. Inherited CLAUDE*/ANTHROPIC* variables are removed so the normal CLI login is used.
- The risk agent is more restricted: no tools, no saved session, and no user settings/hooks when the CLI supports those restrictions. It receives figures and returns a proposal.

These are process/tool permissions, not a security sandbox for compiled C# or a promise that arbitrary provider/CLI versions enforce identical restrictions.

## Strategy assistant

1. Open **Backtesting → Code**. The Claude panel sits beside the editor; detach/dock it with the window button.
2. Enter a request and Send (⌘Enter). Suggestions include opening-range breakouts, explaining the active strategy, time filters and ATR stops.
3. Read progress and file operations. The editor is read-only during work and reloads changed code. **Stop** interrupts the CLI.
4. A newly created strategy opens automatically and compiles. Success offers a backtest; errors offer a repair request. Edited indicators recompile and report whether they are available in My indicators.

Each request includes the active file, indicator folder and current compilation errors. System instructions document both APIs and request self-contained compiling C#, no external packages/files/network/threads, deterministic seeded randomness, short **English** comments/labels and a short explanation.

Conversations are stored under Application Support/Trading Hub/Chats. History resumes an existing CLI session; **New conversation** starts fresh. Historical conversations remain as originally written rather than being destructively translated.

## Laboratory

Laboratory mode starts the same app with `--mcp`, without windows, speaking JSON-RPC over standard input/output. Configuration is `claude-lab-mcp.json` in Application Support, server name `tradinghub`. Individual operations can have up to an hour for long computation.

| Tool | Function |
|---|---|
| list_strategies | Compilation, parameter/range metadata and indicator warmup requirements. |
| compile_strategy | Compile and return diagnostics, parameters and warmup. |
| chart_snapshot | Run a strategy and save chart signals/drawings plus local-time bars/trades/log. |
| list_indicators / compile_indicator | List/compile custom indicators with diagnostics. |
| list_data | Stored tick-level datasets available to backtests. |
| download_data | Public Binance Futures/Spot or Bybit archives, up to 62 days per request. |
| run_backtest | Tick-level test with costs and optional Monte Carlo. |
| run_optimization | Grid search, default maximum 5,000 combinations; top results, profitable share and plateau recommendation. |
| run_walk_forward | Grid/Bayesian walk-forward, plateau, PBO and CPCV; can estimate duration only. |
| list_runs | Saved runs for a strategy. |
| analyze_portfolio / suggest_portfolio | Combine runs/correlation or find lower-correlation combinations improving Sharpe. |
| live_portfolio | Read-only live sizes, positions, PnL, PF and drawdown. |
| propose_sizing | Create pending proposals; does not apply them. |
| save_report | Generate and save a professional report. |

Tool names and JSON keys are stable API identifiers, not translated display labels.

Runs enter History with a Claude laboratory note. Instructions prescribe reviewing code/data (and obtaining 30–90 crypto days as appropriate), baseline backtest, reasonable optimization ranges/minimum 30 trades, walk-forward training windows three to four times test length, Monte Carlo on validated results, justified code improvements compared against the prior version, then a verdict and save_report. Numerical claims must come from tool results.

| English verdict | Stored code | Criteria |
|---|---|---|
| Profitable | ganadora | Positive out-of-sample PnL, WF efficiency ≥0.5, OOS PF ≥1.2, ≥30 trades, reasonable Monte Carlo p95 drawdown and stable windows. |
| Promising | prometedora | Positive but small sample or instability. |
| Unprofitable | perdedora | OOS losses or gains attributable to overfitting. |
| Rejected | descartada | Does not compile/trade or is unsuitable for the data. |

The codes remain Spanish for saved-report compatibility; UI and new explanatory text are English. These are screening heuristics, not guarantees.

For live exposure, tools can propose up to ×3 for stronger strategies, reductions/zero for deterioration, with evidence and no firm conclusions from tiny samples. Proposals await approval in **Live → Proposals**.

## Reports

save_report combines executive summary, strategy/data/assumptions, metrics, monthly/trade/drawdown analysis, parameter sensitivity, walk-forward, Monte Carlo, portfolio and glossary. Claude contributes verdict, interpretation, risks and next steps.

- Stored in `~/Documents/Trading Hub/Estrategias/Reportes`: Markdown plus a `…_archivos` asset folder. Existing folder names stay stable.
- Read in **Backtesting → Reports**. **Export PDF / HTML** embeds charts into HTML and opens a browser for printing/saving PDF. **Show in Finder** reveals the files.
- Verdicts also appear alongside strategies in the picker.
- Without Claude, History/Portfolio **Report** creates the numerical report without AI narrative.

Old reports, user-written strategy labels and third-party text are not automatically rewritten during installation.

## What is sent to Claude

Your prompt, files the CLI reads, tool results (metrics, trades, data summaries, chart images) and requested live-portfolio information can be sent to Anthropic through your CLI/account. Keychain credentials are not exposed by Trading Hub laboratory tools, but do not put secrets into prompts or source files. See [Privacy](privacidad.md).

The English prompts request English responses; a provider outage, unsupported model, existing conversation context or explicit user request can affect output. Real paid Claude calls are not required for localization regression checks; use fake CLI fixtures.
