[← Documentation index](README.md)

# Development

Trading Hub uses C#/.NET 10, Avalonia 11.3 and SkiaSharp 2.88. This fork's built-in interface, generated reports and assistant instructions are English. Original developer comments and Spanish documentation are retained where they are not user-facing.

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

For isolated development:

```bash
fixture="$(mktemp -d /tmp/tradinghub-dev.XXXXXX)"
TRADINGHUB_DATA_DIR="$fixture/data" \
TRADINGHUB_STRATEGIES_DIR="$fixture/strategies" \
TRADINGHUB_INDICATORS_DIR="$fixture/indicators" \
TRADINGHUB_KEYCHAIN_FILE="$fixture/fake-keychain.json" \
TRADINGHUB_SLEEP_GUARD=off \
dotnet run --project src/TradingHub.App
```

A data-directory override also isolates default script folders and suppresses normal menu-bar/power integration. Explicit script variables make the separation clear. **Never run a second writer against the installed app's real history files.**

## Tests

The suite covers backtest fills (StrategyTests), brackets/OCO, context filters, robustness/validation, smart walk-forward, live runners/portfolios, portfolio analysis, core/data integrity/history/CSV, account statistics, drawings/legend and broker/data/signature protocols.

FakeRithmicServer and FakeHttpServer/FakeBrokers model provider messages for OANDA, Alpaca, Databento, cTrader and Tradovate. Tests use temporary directories and fake credentials, not real brokerage accounts.

EnglishLocalizationTests additionally checks SDK defaults/overrides, legacy display mapping, unchanged parameter keys, footprint serialization, old/new report recognition and chart-period aliases/labels. Existing legacy drawing JSON and simulated-account ledger recovery tests remain compatibility guards.

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

| Scene | Coverage |
|---|---|
| trading, accounts, strategies, live, data, connections, settings | Navigate and capture the section. |
| english | English windows/dialogs, safety copy and related localization assertions using isolated fixtures. |
| conexiones | Fake provider setup, tests, downloads, Databento estimates, Tradovate accounts and OANDA quotes. |
| panel | Right Chart Trader/DOM/tape, DOM execution controls and detached windows. |
| footprint | Footprint text colors and editor. |
| mercado | Regime analysis and condition-filtered reruns. |
| theme | Dark/light themes and chart settings. |
| choice, picker, esquinas | History selector, instrument picker and corner diagnostics. |
| data+rithmic, data+import | Fake Rithmic history and generated CSV import. |
| accounts+demo | Simulated and fake Rithmic activity. |
| connections+rithmic, connections+amp | Profile/server selector. |
| trading+… | layout2/layout2r/layout3/layout4/layout6; history<hours>[+period]/historyall; jitter; float; brackets; drawings; drawsettings; links; toolbar; trader; accounts; crossdemo; strategy; domfloat; domsettings. |
| strategies+… | backtest, optimize, wfo, code, history, reports, portfolio, claudefloat, claude, claudelab; append +File to select a class/file. |

Isolation behavior:

- Sleep guard is off and a fake JSON Keychain replaces real Keychain access.
- Without explicit script-directory overrides, scripts are copied to temporary folders, never edited in place.
- Without a data override, configuration/history is copied into temporary storage; Rithmic history is excluded, account history resets, API keys/real permissions are sanitized and Rithmic uses a local fake server.
- A process-held lock serializes scenarios that share temporary fixture names, waiting up to 20 minutes.
- Explicit environment overrides are respected. Prefer fresh temporary fixtures for reproducible checks rather than copying personal state.

**strategies+claude / strategies+claudelab invoke the real CLI and can consume paid allowance unless TRADINGHUB_CLAUDE_CLI points to a fake executable.** Never run them casually as localization tests.

Diagnostics: TRADINGHUB_SNAP_INDICATORS="Name|Other" adds selected studies; TRADINGHUB_SCRIPTS_DEBUG=1 prints compilation results; TRADINGHUB_SNAP_DEBUG=1 reports active connection/data state.

## Environment variables

| Variable | Purpose |
|---|---|
| TRADINGHUB_DATA_DIR | Data-root override. |
| TRADINGHUB_STRATEGIES_DIR / TRADINGHUB_INDICATORS_DIR | Script-root overrides. |
| TRADINGHUB_KEYCHAIN_FILE | JSON fake secrets instead of Keychain; plaintext, **test use only**. |
| TRADINGHUB_BACKTESTS_DIR / TRADINGHUB_LIVE_DIR | Backtest/live storage overrides. |
| TRADINGHUB_SLEEP_GUARD=off | Disable power changes. |
| TRADINGHUB_CLAUDE_CLI | Explicit Claude executable. |
| TRADINGHUB_MCP_COMMAND | Laboratory executable override; default is this app. |
| TRADINGHUB_OANDA_REST / _OANDA_STREAM | OANDA test endpoints. |
| TRADINGHUB_ALPACA_DATA / _ALPACA_TRADING / _ALPACA_STREAM | Alpaca test endpoints, each with the full TRADINGHUB prefix. |
| TRADINGHUB_TRADOVATE_REST / _TRADOVATE_WS / _TRADOVATE_MD | Tradovate test endpoints. |
| TRADINGHUB_CTRADER_URL / TRADINGHUB_DATABENTO_URL | cTrader/Databento test endpoints. |

## Add a provider

1. Implement IMarketDataFeed under Core/Feeds: GetInstrumentsAsync, batched LoadTradesAsync and Subscribe delivering trades/depth/status to IMarketDataSink. Add IKlineHistory, IDayDownloader, ISessionAwareFeed or ITickerStatsProvider where appropriate.
2. For execution, implement ITradingAccount: market/limit/stop, cancellation/modification, flatten/reverse, position and IsReal.
3. Add ConnectionKind without changing persisted enum values, MarketDataId, nonsecret ConnectionProfile fields, GetFeed and TestAsync. Use SetSecret/GetSecret for Keychain credentials and an endpoint override for fake tests.
4. Add ConnectionsView UI and, for trading, AppServices.AccountsFor/AccountFor. Real orders require explicit permission.
5. Add fake server fixtures and protocol/data tests.

## Add an indicator or laboratory tool

- Built-in study: an Indicator subclass in Indicators.Native with parameterless constructor, DisplayName and category metadata. The assembly is scanned at startup. Custom studies need only the user indicator folder.
- Laboratory tool: schema in LabServer.Tools, dispatch in LabServer.CallAsync, and allowlisted name in ClaudeCodeBridge.LabTools. Keep JSON keys stable.

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
