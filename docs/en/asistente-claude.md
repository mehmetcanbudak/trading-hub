[← Documentation index](./README.md)

# Claude in Trading Hub

Trading Hub defaults to **Claude Code** using your own logged-in session and subscription, not a separate Trading Hub/Claude API account. Strategy chat can optionally use your own Codex CLI session. Usage counts against the selected provider's allowance; provider availability, pricing and quota still apply. Without either CLI, trading/charting remains available. The risk agent remains Claude-only.

| Location | Capability |
|---|---|
| Backtesting → Code, Claude panel | Write, explain and repair strategies/indicators. |
| Same panel, Laboratory enabled | Run backtests, optimization, walk-forward and Monte Carlo; create reports/verdicts. |
| Live → Portfolios | Per-portfolio [risk agent](./en-vivo.md#risk-agent). |
| Live → Proposals | Ask the laboratory to review current strategies and propose sizes. |

## Finding Claude Code

The app searches `/usr/local/bin`, `/opt/homebrew/bin`, `~/.claude/local`, `~/.local/bin`, `~/.npm-global/bin`, PATH and supported Claude desktop CLI locations under `~/Library/Application Support/Claude/claude-code/…`. It chooses the newest discovered version. Override with `TRADINGHUB_CLAUDE_CLI`; see [Installation](./instalacion.md#claude-code-optional).

On Windows, discovery also supports native `claude.exe` and recognized npm/pnpm/Yarn `claude.cmd` shims in user installation locations and PATH. Supported shims are resolved to their Node entry point or native executable, so multiline and oversized prompts do not pass through `cmd.exe`; an unknown shim fails visibly. The explicit override supports these forms. Login opens a Windows terminal rather than macOS Terminal. Native discovery/login/quoting evidence is tracked in the validation dashboard.


The panel displays CLI version/subscription or a not-found message. **Sign in to Claude Code** opens Terminal with `claude /login`; retry after signing in. Unsupported model/version errors require a compatible CLI; update through the installed distribution's normal update mechanism, such as `claude update` where supported.

## Model, effort and permissions

- The source defaults to `claude-opus-5-5` with `xhigh` effort (Extra), omitting effort when unsupported. Risk agents can select the configured Opus/Sonnet/Haiku/Fable model options and Low/Medium/High/Extra/Maximum effort. These identifiers are configuration defaults, **not a guarantee your CLI/provider offers them**.
- Runs noninteractively with `claude -p` in the strategy directory. File editing is permitted in strategy, indicator and laboratory-screenshot folders. Assistant tools are limited to Read, Write, Edit, MultiEdit, Glob and Grep—no terminal or web tool.
- `--strict-mcp-config` avoids inheriting unrelated MCP servers; laboratory mode exposes Trading Hub's server, and separately approved optional research can add the narrow research proxy. Inherited CLAUDE*/ANTHROPIC* variables are removed so the normal CLI login is used.
- The risk agent is more restricted: no tools, no saved session, and no user settings/hooks when the CLI supports those restrictions. It receives figures and returns a proposal.

These are process/tool permissions, not a security sandbox for compiled C# or a promise that arbitrary provider/CLI versions enforce identical restrictions.

## Strategy assistant

1. Open **Backtesting → Code**. The Claude panel sits beside the editor; detach/dock it with the window button.
2. Enter a request and Send (⌘Enter). Suggestions include opening-range breakouts, explaining the active strategy, time filters and ATR stops.
3. Read progress and file operations. The editor is read-only during work and reloads changed code. **Stop** interrupts the CLI.
4. A newly created strategy opens automatically and compiles. Success offers a backtest; errors offer a repair request. Edited indicators recompile and report whether they are available in My indicators.

Each request includes the active file, indicator folder and current compilation errors. System instructions document both APIs and request self-contained compiling C#, no external packages/files/network/threads, deterministic seeded randomness and short **English** code comments/labels. The selected interface language overrides reply/report prose instructions, never generated C# code, filenames or identifiers.

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
| live_portfolio | Read-only live sizes, positions, accounting quality, nullable confirmed results and explicitly labelled estimates. |
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

The codes remain Spanish for saved-report compatibility; UI and new explanatory text follow the selected runtime language. These are screening heuristics, not guarantees.

For live exposure, tools can propose up to ×3 for stronger strategies, reductions/zero for deterioration, with evidence and no firm conclusions from tiny samples. Proposals await approval in **Live → Proposals**.

### Live-portfolio accounting

Inspect `financial_quality` before interpreting live results. **Null means unavailable, not zero.**

| Field | Meaning |
|---|---|
| `financial_quality` | `authoritative` for complete reported execution economics, `estimated` for legacy estimates, or `pending` while required prices, fees or fee-currency information are missing. |
| `net`, `open_pnl`, `metrics`, recent metrics and drawdown | Confirmed values are nullable. Strategy `net` includes realized accounting plus an available open valuation. |
| Fields ending in `_estimate` | Best-available numeric values, retained separately rather than presented as confirmed results. |
| `last_trades` | Includes quality, nullable confirmed `pnl` and prices, plus explicit `pnl_estimate`, `entry_price_estimate` and `exit_price_estimate` values. |

Legacy history remains estimated; it is not rewritten as broker-reported executions. Reported fill fees retain their currency, and unknown fees or conversions are not invented. Execution PnL is not a complete broker cash statement including funding or financing.

Do not recommend increased exposure solely from estimated or incomplete financial results. These accounting fields do not certify authenticated provider coverage or exactly-once order delivery.


## Reports

save_report combines executive summary, strategy/data/assumptions, metrics, monthly/trade/drawdown analysis, parameter sensitivity, walk-forward, Monte Carlo, portfolio and glossary. Claude contributes verdict, interpretation, risks and next steps.

- Stored in `~/Documents/Trading Hub/Estrategias/Reportes`: Markdown plus a `…_archivos` asset folder. Existing folder names stay stable.
- Read in **Backtesting → Reports**. **Export PDF / HTML** embeds charts into HTML and opens a browser for printing/saving PDF. **Show in Finder** reveals the files.
- Verdicts also appear alongside strategies in the picker.
- Without Claude, History/Portfolio **Report** creates the numerical report without AI narrative.

Old reports, user-written strategy labels and third-party text are not automatically rewritten during installation.

## What is sent to Claude

Your prompt, files the CLI reads, tool results (metrics, trades, data summaries, chart images) and requested live-portfolio information can be sent to Anthropic through your CLI/account. Keychain credentials are not exposed by Trading Hub laboratory tools, but do not put secrets into prompts or source files. See [Privacy](./privacidad.md).

Prompts request replies and report prose in the selected English/Spanish/Simplified Chinese interface language while generated C# and identifiers stay English. A provider outage, unsupported model, existing conversation context or explicit user request can affect output. Real paid Claude calls are not required for localization regression checks; use fake CLI fixtures. Existing reports/conversations are not rewritten.

## Optional strategy-chat engine and isolation

The strategy panel selects Claude (default) or Codex, with an engine-specific model/effort selection and custom model support. Codex discovery includes its normal user/PATH locations; `TRADINGHUB_CODEX_CLI` selects an explicit executable. A missing executable is a visible failure, not a fallback to a different engine. Conversations are engine-bound: exact session resume stays with the originating engine, and switching engines does not reuse a foreign session. Existing chat paths, detached geometry and `LabSource=claude` persisted identities remain unchanged.

Codex runs `exec` with `--ignore-user-config`, `--ignore-rules`, `--json`, workspace-write sandbox and approval policy never; CLI network access is disabled in that workspace sandbox. Required isolation flags must be advertised by the installed CLI or the request fails. Stop cancels the owned request/process. Token counts and USD cost are nullable: unavailable accounting must not be presented as free usage or a fabricated zero. The finite fake-CLI replay/compile/cancel evidence in the dashboard is not a paid-provider success.

The existing 16 Laboratory tools and sizing proposals do not apply trades. Local child servers receive the six isolated storage overrides plus selected runtime environment values, not arbitrary secret environment variables. `TRADINGHUB_MCP_COMMAND` is a Laboratory fixture override only: the research route always launches the actual application entry point. CLI sandbox network restrictions **do not constrain remote MCP services or compiled in-process C#**.

## Optional TradingView research

Research is **off by default**. The provider requires an eligible Essential-or-higher subscription (trials excluded); this is a provider policy prerequisite, not verified interoperability. Authorization is explicit app-owned OAuth outside chat, using dedicated OS secret-store entries; it neither scrapes CLI credentials nor rewrites global CLI configuration. A local grant ID is not verified remote account identity.

Discovery fetches authenticated, paginated tool **metadata**, not tool results. Review the offered names/descriptions/schema and choose from the fixed 19-tool research intersection. All watchlist and alert tools and unknown tools are denied, even if metadata claims read-only; `get_active_watchlist` is denied because its name does not make its behavior safe. Catalog/schema fingerprints, changed grants, reauthorization, canceled review and revocation invalidate approval before a call; changed metadata requires review again.

Only approved research calls may reach `https://mcp.tradingview.com/mcp`. Queries/arguments and tool results can leave the machine and then enter the selected model context; consider both providers' retention/cost policies. Missing supported application registration fails explicitly; no fabricated hosted client metadata or reused CLI OAuth session is supplied. No real TradingView OAuth/entitlement/account certification is claimed from fake-server tests. See [Privacy](./privacidad.md#optional-assistant-and-research-data) and [Development](./desarrollo.md#selected-non-kvp-reproduction).
