[← Documentation index](README.md)

# Development

Trading Hub uses C#/.NET 10, Avalonia 11.3 and SkiaSharp 2.88. English is canonical source text; the runtime interface, generated report prose and assistant replies support English, Spanish and Simplified Chinese. Generated C#, filenames, class/parameter identifiers and code comments remain English in every language. Original developer comments and Spanish documentation are retained where they are not user-facing.

## Solution

`TradingHub.slnx` includes:

| Project | Responsibility |
|---|---|
| src/TradingHub.Core | Instruments, trades/bars, files/imports/history merging, provider feeds, simulation/replay, accounts/brackets/journals; no external packages. |
| src/TradingHub.Sdk | ATAS-shaped indicator, series, rendering, attribute and localization APIs. |
| src/TradingHub.Charting | Skia chart/footprint/DOM/tape/drawing/theme engine. |
| src/TradingHub.Scripting | Roslyn in-memory compilation and ATAS namespace adaptation. |
| src/TradingHub.Strategies | Strategy API, backtests, optimization, walk-forward, robustness/validation, portfolios and live ledgers; depends on Core. |
| src/TradingHub.Indicators.Native | Included studies. |
| src/TradingHub.Indicators.Technical | Compatibility stubs and locally imported technical studies; external ATAS source is not bundled. |
| src/TradingHub.App | Views, Windows, Controls, Services, Styles and Lab. `--mcp` starts the laboratory without windows. |
| tests/TradingHub.Tests | xUnit regression suite. |
| tools/TradingHub.UiSnapshot | Headless rendering and UI scenarios. |
| tools/TradingHub.I18n | Catalog extraction/checking and complete upstream-dictionary/template coverage. |

Versions/framework/product metadata are in Directory.Build.props. Product.Name defines branding; Product/AppPaths define storage. Do not rename those paths for localization.

## Build and run

```bash
export PATH="$HOME/.dotnet:$PATH"
dotnet build TradingHub.slnx -c Release
dotnet test tests/TradingHub.Tests -c Release
./build-app.sh            # Build dist/Trading Hub.app only
./build-app.sh --install  # Replace the Applications bundle
```

Packaging publishes self-contained osx-arm64, staging outside Desktop to avoid iCloud extended attributes interfering with signing. Git-ignored build.local.env can set BUNDLE_ID and SIGN_IDENTITY; TH_OUT and TH_STAGE override output/staging. See [Installation](instalacion.md).

Windows packaging is implemented for both `win-x64` and `win-arm64`: `.\build-windows.ps1` on Windows with .NET SDK 10/NSIS, or `./build-windows.sh` on macOS with .NET SDK 10/NSIS. It produces per-user installers under `dist`; see [Windows](portar-a-windows.md). Packaging and shared tests do not certify native installer, Credential Manager, CLI/shim, tray/DPI, power or brokerage behavior.


For isolated development:

```bash
fixture="$(mktemp -d /tmp/tradinghub-dev.XXXXXX)"
TRADINGHUB_DATA_DIR="$fixture/data" \
TRADINGHUB_STRATEGIES_DIR="$fixture/strategies" \
TRADINGHUB_INDICATORS_DIR="$fixture/indicators" \
TRADINGHUB_BACKTESTS_DIR="$fixture/backtests" \
TRADINGHUB_LIVE_DIR="$fixture/live" \
TRADINGHUB_KEYCHAIN_FILE="$fixture/fake-keychain.json" \
TRADINGHUB_SLEEP_GUARD=off \
dotnet run --project src/TradingHub.App
```

A data-directory override also isolates default script folders and suppresses normal menu-bar/power integration. Explicit script variables make the separation clear. **Never run a second writer against the installed app's real history files.**

## Tests

The suite covers backtest fills (StrategyTests), brackets/OCO, context filters, robustness/validation, smart walk-forward, live runners/portfolios, portfolio analysis, core/data integrity/history/CSV, account statistics, drawings/legend and broker/data/signature protocols.

FakeRithmicServer and FakeHttpServer/FakeBrokers model provider messages for OANDA, Alpaca, Databento, cTrader and Tradovate. Tests use temporary directories and fake credentials, not real brokerage accounts.

EnglishLocalizationTests additionally checks SDK defaults/overrides, legacy display mapping, unchanged parameter keys, footprint serialization, old/new report recognition and chart-period aliases/labels. Existing legacy drawing JSON and simulated-account ledger recovery tests remain compatibility guards.

Execution regressions cover native client-ID reuse, accepted-versus-unsent replacement lineage, current-child adoption, interrupted first native-index publication, corrected prices/fees, durable deduplication and restart projection. `AccountSnapshot` captures fills, analysis and `HistoryRevision` from one journal snapshot; view caches must use that revision rather than execution count. An inactive runner with no close/order work uses the committed ledger for display without replacing an active runner's book. Fake-venue and owned-filesystem proofs are not authenticated provider or physical power-loss certification.

```bash
dotnet test tests/TradingHub.Tests --filter FullyQualifiedName~SmartWalkForward
python3 tools/check_english_translation.py
```

The translation checker checks English documentation links/section coverage, Roslyn string candidates, compatibility exceptions and reviewed non-text source structure. It inspects the current working tree, including new files, rather than only committed changes. A heuristic scan is not proof that arbitrary user/provider content is English.

## Headless scenes

```bash
dotnet run --project tools/TradingHub.UiSnapshot -- <output> 4 <scene> [<scene>...] [--open connection:SYMBOL] [--size 1600x950]
```

The number is initial loading seconds. With no scenes, Trading is captured. Assertion scenes return nonzero on failure. Existing diagnostic scenes may retain Spanish console wording; scene names are stable test identifiers.

`--check-script-monitor` is a separate early entry with its own real dispatcher and fresh six-root fixture, preserved unchanged from reviewed startup commit `85c43954ebd4cf6acccda850cc86f356c2bdf9f5`. Its eight compiler/monitor/disposal cases and both workflow steps remain; the reported pass belongs to that commit, not an automatically repeated KVP result. The normal KVP authoring/shutdown scenes additionally exercise owned Pine/CLI/private-store drains.

| Scene | Coverage |
|---|---|
| trading, accounts, strategies, live, data, connections, settings | Navigate and capture the section. |
| english | English windows/dialogs, safety copy and related localization assertions using isolated fixtures. |
| safety | Financial-safety and protection-aware quit scenarios, including pending close/cancel acknowledgment and changed Tradovate contexts; fake providers only. |
| ib-excursions, ib-excursions-import | Actual live excursion consumer after a corrected entry price or same-length/preserved-mtime IB history replacement; owned synthetic bars only. |
| ib-data-refresh | Actual Data refresh remains responsive while an owned public import holds publication; the UI releases the import and displays the complete new day index. |
| ib-live-trades | Actual rendered live trade table refresh after a same-length/preserved-mtime IB replacement; its MAE/MFE must match the validated service, not an older per-view result. |
| ib-recorded-prefix | Actual live excursion consumer under an owned recorder lease: an incomplete prefix is unavailable, then fully drained valid prices establish the closed range. |
| live-financial | Actual simulator execution charges in the live portfolio KPI; saves the rendered surface and independent accounting receipt. Requires an explicit isolated live-store override. |
| live-stopped-correction | Actual simulator round trip revised after stopping: durable and rendered portfolio net change from 94 to 53 while the detached runner remains at 94. Uses an owned explicit portfolio. |
| live-stopped-open | Stopped open financial totals remain unavailable while a separately known closed-trade count stays visible; repeat in English, Spanish and Chinese. |
| live-stopped-chart-revision | A restored paused chart reads the latest committed ledger correction rather than the detached stopped runner's stale marks/links. |
| account-journal-revision | Same execution correction with two effective fills: Account analysis, summary, Trades, Statistics and chart marks/links must change from net 500 to 400. Requires a fresh explicit application-store override. |
| live-excursions | Actual excursion service recalculates MAE/MFE from owned raw history after correcting the same entry execution; no broker data or model calls. |
| conexiones | Fake provider setup, tests, downloads, Databento estimates, Tradovate accounts and OANDA quotes. |
| panel | Right Chart Trader/DOM/tape layout, scrolling and detached windows. |
| footprint | Footprint text colors and editor. |
| mercado | Regime analysis and condition-filtered reruns. |
| theme | Dark/light themes and chart settings. |
| choice, picker, esquinas | History selector, instrument picker and corner diagnostics. |
| data+rithmic, data+import | Fake Rithmic history and generated CSV import. |
| accounts+demo | Simulated and fake Rithmic activity. |
| connections+rithmic, connections+amp | Profile/server selector. |
| trading+… | layout2/layout2r/layout3/layout4/layout6; history<hours>[+period]/historyall; jitter; float; brackets; drawings; drawsettings; links; toolbar; trader; accounts; crossdemo; strategy; domfloat; domsettings. |
| strategies+… | backtest, optimize, wfo, code, history, reports, portfolio, claudefloat, claude, claudelab; append +File to select a class/file. |
| kvp-operations | Actual history/depth settings; opt-in root keeper cache-write failure/rendered failed-month/retry/cursor recovery; preview/stage/restart relocation controls with cancellation/permission/hash faults. |
| kvp-proxy | Actual settings policy, parsed provider HTTP history and authenticated WebSocket/chart consumers, captured reconnect contexts and failed-proxy no-fallback. |
| kvp-chart, kvp-chart-accounts | Actual typed text/keyboard/IME/navigation/drawing controls; different-provider/composite account preservation and actual workspace restore without implicit routing. |
| kvp-studies | Complete approved study groups through real catalog/settings/renderer consumers and independent numerical examples; repeat with `TRADINGHUB_LANG=es` and `zh-CN`. |
| kvp-authoring | Built-in source resources/fork/edit/rename/delete/real compilation, recursive content monitoring and explicitly approved fake Pine drafting. |
| kvp-depth-history | Real archive/cache/provenance and depth sink/writer/selected-reader/heatmap/Orderbook Delta consumers, corrections, gaps, stale/quota/I/O coverage. |
| kvp-providers | Actual Longbridge/aggregate/Bybit configured consumers against loopback/injected fakes, truthful account fields, owned ACKs and credential retirement. |
| kvp-replay-alerts | Actual practice/reveal/end/result and compiled-strategy consumers, isolated run journals, causal availability, notification consent/persistence/retry/revocation. |
| kvp-shutdown, kvp-shutdown-platform | **Standalone processes only:** real default terminal drains held Bybit, cached Longbridge requests/PKCE loopback/browser callbacks, Pine/compiler/pending-start and global private-store work; new requests/second red-close cannot force replacement/desktop exit early. |
| kvp-performance | Warmed actual CPU calculation/raster frames and wide-book writer/selected-history/heatmap resource measurements; no real provider or claimed Metal kernel. |


For safety/quit coverage, use a fresh fixture with all six storage overrides and the sleep guard disabled, as in [the dashboard reproduction command](TRANSLATION_STATUS.md#reproduce). The `safety` scene includes `RunQuit`; `quit` is not a separate assertion scene.

For multilingual visual captures, use separate fresh fixtures and the seven main pages:

```bash
for language in es zh-CN; do
  fixture="$(mktemp -d /tmp/tradinghub-language.XXXXXX)"
  TRADINGHUB_DATA_DIR="$fixture/data" \
  TRADINGHUB_STRATEGIES_DIR="$fixture/strategies" \
  TRADINGHUB_INDICATORS_DIR="$fixture/indicators" \
  TRADINGHUB_BACKTESTS_DIR="$fixture/backtests" \
  TRADINGHUB_LIVE_DIR="$fixture/live" \
  TRADINGHUB_KEYCHAIN_FILE="$fixture/fake-keychain.json" \
  TRADINGHUB_SLEEP_GUARD=off TRADINGHUB_LANG="$language" \
  dotnet run --project tools/TradingHub.UiSnapshot -- \
    "$fixture/captures" 4 trading settings connections data strategies accounts live
done
```

These commands are reproduction instructions, not claims that the scenarios passed in this integration. Record actual results in the dashboard. CJK shaping, regional formatting and real desktop restart/quit need visual/native review in addition to assertions.

Isolation behavior:

- Sleep guard is off and a fake JSON Keychain replaces real Keychain access.
- Missing overrides receive fresh, empty owned fixture folders under the output fixture root. Personal scripts, settings, credentials, account history and market files are never copied into a scenario.
- External/default providers are disabled and real-order permissions/keys sanitized; explicitly configured scene-owned fake endpoints/feeds are the only provider substrate. Rithmic uses a local fake server.
- A process-held lock serializes scenarios that share temporary fixture names, waiting up to 20 minutes.
- Explicit environment overrides are respected and checked against protected production paths. Prefer six fresh SSD runtime roots; keep screenshots/receipts on an evidence volume when storage is constrained.

**strategies+claude / strategies+claudelab invoke the real CLI and can consume paid allowance unless TRADINGHUB_CLAUDE_CLI points to a fake executable.** Never run them casually as localization tests.

Diagnostics: TRADINGHUB_SNAP_INDICATORS="Name|Other" adds selected studies; TRADINGHUB_SCRIPTS_DEBUG=1 prints compilation results; TRADINGHUB_SNAP_DEBUG=1 reports active connection/data state.

## Environment variables

| Variable | Purpose |
|---|---|
| TRADINGHUB_DATA_DIR | Data-root override. |
| TRADINGHUB_STRATEGIES_DIR / TRADINGHUB_INDICATORS_DIR | Script-root overrides. |
| TRADINGHUB_KEYCHAIN_FILE | JSON fake secrets instead of macOS Keychain/Windows Credential Manager; plaintext, **test use only**. |
| TRADINGHUB_BACKTESTS_DIR / TRADINGHUB_LIVE_DIR | Backtest/live storage overrides. |
| TRADINGHUB_SLEEP_GUARD=off | Disable power changes. |
| TRADINGHUB_CLAUDE_CLI | Explicit Claude executable. |
| TRADINGHUB_LANG | UiSnapshot language selection (`en`, `es`, `zh-CN`); desktop language is persisted in Settings. |
| TRADINGHUB_MCP_COMMAND | Laboratory executable override; default is this app. |
| TRADINGHUB_OANDA_REST / _OANDA_STREAM | OANDA test endpoints. |
| TRADINGHUB_ALPACA_DATA / _ALPACA_TRADING / _ALPACA_STREAM | Alpaca test endpoints, each with the full TRADINGHUB prefix. |
| TRADINGHUB_TRADOVATE_REST / _TRADOVATE_WS / _TRADOVATE_MD | Tradovate test endpoints. |
| TRADINGHUB_CTRADER_URL / TRADINGHUB_DATABENTO_URL | cTrader/Databento test endpoints. |
| TRADINGHUB_BYBIT_REST / _BYBIT_MARKET_REST / _BYBIT_MARKET_WS | Isolated Bybit account/public-data endpoints; never point acceptance at a real account. |

## Add a provider

1. Implement IMarketDataFeed under Core/Feeds: GetInstrumentsAsync, batched LoadTradesAsync and Subscribe delivering trades/depth/status to IMarketDataSink. Add IKlineHistory, IDayDownloader, ISessionAwareFeed or ITickerStatsProvider where appropriate.
2. For execution, implement ITradingAccount: market/limit/stop/exit, cancellation/modification, flatten/reverse, position and IsReal. Native adapters must expose truthful PositionReady/OrdersReady boundaries, invalidate them on disconnect and wait for complete replay before declaring absent positions/orders. Missing data is unknown, never synthetic flat; keep pending requests tracked through acknowledgment.
3. Add ConnectionKind without changing persisted enum values, MarketDataId, nonsecret ConnectionProfile fields, GetFeed and TestAsync. Use SetSecret/GetSecret for Keychain credentials and an endpoint override for fake tests.
4. Add ConnectionsView UI and, for trading, AppServices.AccountsFor/AccountFor. Real orders require explicit permission.
5. Add fake server fixtures and protocol/data tests.

## Add an indicator or laboratory tool

- Built-in study: an Indicator subclass in Indicators.Native with parameterless constructor, DisplayName and category metadata. The assembly is scanned at startup. Custom studies need only the user indicator folder.
- Laboratory tool: schema in LabServer.Tools, dispatch in LabServer.CallAsync, and allowlisted name in ClaudeCodeBridge.LabTools. Keep JSON keys stable.

## Custom-indicator monitoring

On macOS, custom C# indicators use one cancellable, nonoverlapping 250 ms content scan instead of activating `FileSystemWatcher`, whose .NET macOS implementation calls global POSIX `sync()`. Other platforms retain native watching. Recursive sources exclude `bin`/`obj`; compilation and catalog publication remain on the UI dispatcher after the existing 600 ms debounce. Captured source contents, rather than a second read of mutable paths, are compiler inputs. Invalid or unreadable sources retain the last-good catalog and recover after a later successful read.

Only actual application exit disposes the script monitor. Closing to the menu bar keeps hot reload active; disposal rejects queued, debounced and in-progress compilation results.

After building Release, run the actual compiler/dispatcher fixture:

```bash
dotnet tools/TradingHub.UiSnapshot/bin/Release/net10.0/TradingHub.UiSnapshot.dll --check-script-monitor
```

It creates six fresh temporary storage overrides before app-path initialization, uses no providers, personal scripts or model CLI, and prints its retained fixture directory. Both platform CI jobs run it. This fixture does not replace native packaged startup/quit verification.

## Validation from code

ValidationGate.Evaluate applies the [Backtesting](backtesting.md#deflated-sharpe-and-validation) rules. RobustnessTests contains complete examples. Given existing walkForward and optimization results:

```csharp
var gate = ValidationGate.Evaluate(new GateInput
{
    WalkForward = walkForward,
    Optimization = optimization, // Optional plateau/robustness inputs.
    Trials = 12,
    TrialVariance = 0.01,
    MinTrades = 30,
});
Console.WriteLine($"{gate.StatusText} · Deflated Sharpe {gate.Deflated:P0}");
foreach (var check in gate.Checks)
    Console.WriteLine($"{check.Status} · {check.Name}: {check.Text}");
```

LiveStrategyRunner.Guard accepts ILiveOrderGuard to inspect exposure-increasing orders; reducing orders still pass. The app does not install a universal custom guard by default; tests demonstrate integration.

## Rules

- Never commit passwords, tokens or keys. Use Keychain in production and fake secrets in tests.
- Never run automation against personal data/scripts; isolate every writer.
- Preserve existing real-order confirmations, account permissions and risk controls. Localization must not add/remove order actions or move buttons as PnL changes. trading+jitter checks toolbar/button stability.
- Validate provider work with fake servers and simulation first.
- New built-in user-facing text is English. Keep persisted IDs, parser aliases, JSON/CSV contracts, filenames and generated localization keys stable. SDK English overrides belong in Strings.English.cs, not generated Strings.g.cs.
- Review every non-text change; do not update validation baselines merely to silence a failure. Check the [localization dashboard](TRANSLATION_STATUS.md) and report untested real-broker/hardware behavior explicitly.

## Selected non-KVP reproduction

The selected work is now integrated into `memoshi`; the former external `feat/non-kvp-adoption` checkout is historical. Reproduce from a review branch in an isolated source worktree, never the installed app or personal data directories. The [dashboard](TRANSLATION_STATUS.md#canonical-non-kvp-integration--2026-11-04) distinguishes canonical results from earlier external receipts and remaining release gates. Before any app, headless import or fake-provider scene, choose a fresh fixture root:

```bash
export PATH="$HOME/.dotnet:$PATH"
export FIXTURE="$(mktemp -d /tmp/tradinghub-nonkvp.XXXXXX)"
export TRADINGHUB_DATA_DIR="$FIXTURE/data"
export TRADINGHUB_STRATEGIES_DIR="$FIXTURE/strategies"
export TRADINGHUB_INDICATORS_DIR="$FIXTURE/indicators"
export TRADINGHUB_BACKTESTS_DIR="$FIXTURE/backtests"
export TRADINGHUB_LIVE_DIR="$FIXTURE/live"
export TRADINGHUB_KEYCHAIN_FILE="$FIXTURE/fake-keychain.json"
export TRADINGHUB_SLEEP_GUARD=off
export TRADINGHUB_CLAUDE_CLI=/usr/bin/false
export TRADINGHUB_CODEX_CLI=/usr/bin/false
dotnet build TradingHub.slnx -c Release
dotnet test tests/TradingHub.Tests -c Release
dotnet tools/TradingHub.UiSnapshot/bin/Release/net10.0/TradingHub.UiSnapshot.dll "$FIXTURE/captures" 4 nonkvp-search-zoom
```

Other actual-host fake scenes are `nonkvp-studies`, `nonkvp-oi`, `nonkvp-ib`, `nonkvp-assistant` and `nonkvp-tradingview`. Give each an isolated fixture/capture root; keep real orders, recording and automatic resume disabled except scene-owned fake recording. These commands are reproduction instructions, not newly executed receipts. Do not run public providers, paid models or OAuth to satisfy localization gates.

The built App's import syntax is documented in [Data](datos.md#interactive-brokers-bars-and-offline-history). Its research stdio entry is `dotnet src/TradingHub.App/bin/Release/net10.0/TradingHub.dll --research-mcp --approval <reviewed-approval-id>`; the ID must be an existing reviewed 32-hex GUID (`N` format), not a token or arbitrary bypass. Missing/invalid approval exits 2; initialization does not grant permission to call tools. Use fake servers for verification, not a real account.

Parent acceptance additionally requires English/source review, source/catalog candidate/template and original 3,300-entry coverage gates, English/safety/Quit and multilingual UI scenes, tracker tests, corrected visuals and research cancellation stress. Build/publish output must contain `LICENSE`, `THIRD_PARTY_NOTICES.md` and every `licenses/` text; inspect actual output and publish trees rather than treating csproj declarations as proof. Existing platform/native/provider release gates still apply.

## Selected KVP integration reproduction

The complete approved capability scope comes from reviewed feature commit `5647faad561b0d7988d88cd0856bf13ad9e2f3de`, originally based at `e6fde5a065da9aa0fdcae15b69f2a94702819932`. Reproduce from a review branch of canonical `memoshi`; the former isolated feature worktree is not a required runtime or installation input. The [dashboard](TRANSLATION_STATUS.md#canonical-kvp-promotion--2026-11-05) distinguishes source acceptance from actual package/install receipts. Source pin/archive/full license are in [Third-party notices](../../THIRD_PARTY_NOTICES.md#kvp-capability-reference). Branding/directory migration, KVP Windows distribution/updater and the external Windows-only VPFM launcher are excluded; native Windows compatibility remains a separate gate.

Use the six isolated overrides above for **every** process, fake secret storage and sleep-off. One combined `dotnet build TradingHub.slnx -c Release --artifacts-path /owned/evidence/build` keeps all SDK bin/obj outputs external; fixture copy targets resolve real `GetTargetPath` rather than assuming the conventional layout. Stage the **complete** test/child-fixture, UiSnapshot and I18n runtime sets with matching SHA-256, size and modes. Keep the UI runtime's real repository ancestor context for guide/source checks. Run the **unfiltered built test DLL** and report that exact command/counts, not an unrun canonical project command. Financial-timing fixtures must not repeat the known whole-suite external-HDD timeout conditions. If shared Data capacity is unhealthy, a separately verified task-owned bounded RAM filesystem can hold disposable fixtures; use sparse-capable APFS with sufficient headroom for the unchanged 512 MiB-plus-one-byte boundary. Never format an existing user disk or create a large backing image on the full Data volume. RAM functional tests do not certify physical storage durability. Receipts/captures stay external; no local package growth or deletion of other worktrees, personal data, caches or rollback copies.

For example, after the complete staged runtime has been verified:

```bash
dotnet vstest /absolute/staged/TradingHub.Tests.dll \
  '--logger:trx;LogFileName=full.trx' --ResultsDirectory:/owned/evidence/tests
```

Wait for all feature/consumer writers to settle before the combined Release build and full suite; do not run competing builds against half-finished files. Run the complete tracker, English/source/docs, original dictionary and current-candidate/template gates without filtering existing failures, changing timeouts or resetting historical baselines. Then run English, safety, inherited non-KVP consumers and every `kvp-*` scene above sequentially. Give each shutdown mode its own process and six fresh roots because it really disposes the default AppServices composition.

For that prebuilt/staged layout, `python3 tools/check_english_translation.py --ui-snapshot-assembly /absolute/staged/TradingHub.UiSnapshot.dll` runs the **same unchanged** complete Roslyn source guard plus documentation/self checks without an implicit second build. Without this optional argument, the established default command is unchanged. A missing/invalid assembly is an error, not a source-gate bypass.

The ordinary safety scene's reused fake desktop proves the finite protection decision/ACK state machine, not actual shared-service disposal. The separate default-composition shutdown scenes prove owned pending/active drains, no late publication, rejected new work, idempotence and replacement-before-exit ordering. Hiding the main window retains source monitoring and active services.

`kvp-performance` compares the same correct production CPU implementation's uncached recomputation against retained per-chart caches, including warm-ups, managed allocations and complete Skia raster frames; it does not execute an excluded reference-fork or fixed-base binary. It separately measures wide-book input through the owned writer, fresh selected reader and visible heatmap, retaining actual disk bytes/drops/time and clearly labelled rate/retention projections. For actual external-volume I/O, explicitly select a fresh owned external `TRADINGHUB_DATA_DIR` for this process while keeping all other private roots/TMPDIR isolated; do not label RAM I/O as physical disk performance. Display-only versus recording-enabled is a resource-cost comparison, not equivalent persistence. No GPU/Metal improvement is inferred from a kernel-only estimate; the bounded CPU reference remains. See [the measured workload and limits](TRANSLATION_STATUS.md#apple-silicon-cpu-and-depth-measurements).

Source-review export is explicit and separate from approval:

```bash
dotnet /absolute/staged/TradingHub.UiSnapshot.dll \
  --source-fingerprints "$PWD" /path/to/individually-reviewed-paths.json /path/to/fingerprints.json
```

The input maps each reviewed relative `src/...cs` path to its own rationale. Export calls the existing, unchanged Roslyn `Structure` algorithm, including recursive template treatment; it does not edit or auto-approve the manifest. Read/review actual source and preserve the historical audit baseline/checkpoint before merging exact hashes. Bilingual additions belong in reviewed `tools/TradingHub.I18n/overrides.json`; publish with the existing provenance/collision process, retain all original upstream aliases, and re-extract the final source before complete coverage/template checks.

Results belong in [the dashboard](TRANSLATION_STATUS.md#kvp-capability-adoption-candidate--2026-11-04) only after observed receipt/UI evidence. Fake providers/CLIs do not certify real entitlements, OAuth/model access, installed/native Windows, physical power/thermal behavior, signatures/notarization, distribution or exchange-atomic protection.
