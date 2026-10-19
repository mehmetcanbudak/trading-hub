# Trading Hub — instructions for Claude Code

Trading Hub is a free, source-available desktop trading platform for Apple Silicon macOS, built with C#/.NET 10 and Avalonia. It runs locally and was originally created by [@ralphtradesz](https://x.com/ralphtradesz). **The requested localization language for this fork is English.** The original Spanish instructions are preserved in [CLAUDE.es.md](CLAUDE.es.md).

## Project workflow: dashboard-first validation

Before changing code, inspect [the localization validation dashboard](docs/en/TRANSLATION_STATUS.md) and identify scope, risks, dependencies, existing behavior, and release gates. Work on a branch, not directly on `main`. Never declare a feature complete without evidence from builds, tests, data checks, and relevant UI scenarios. Keep unresolved failures visible and keep the PR in draft until release criteria are met. Protect existing order routing, credentials, historical data, strategy behavior, and macOS compatibility.

## Upstream monitoring and English maintenance

Read [the sync guide](docs/en/upstream-sync.md) and [the upstream ledger](.github/upstream-english.json). `python3 tools/check_upstream.py` fetches/reports pending changes without merging, switching branches or advancing the checkpoint. The fork-only scheduled workflow maintains one tracker issue; its notification token cannot write code. Incorporate upstream changes through a separate sync branch and reviewed PR into **mehmetcanbudak/trading-hub:feat/english-localization**, never the original author's repository. Keep main and Rust/Swift worktrees untouched. Translate all new surfaces/examples/docs, preserve persistent identities, run the established gates and update TRANSLATION_STATUS with evidence before advancing `integrated_commit`. The historical C# audit baseline is separate: do not change it just to silence upstream differences.

## If asked to install Trading Hub

Explain each step briefly; do not request personal information, broker credentials, passwords, or API secrets for installation.

1. **Check the Mac.** `uname -m` must return `arm64` (Apple Silicon), and `sw_vers -productVersion` must be 12 or newer. Do not attempt this macOS build on Intel (`x86_64`). For Windows, see [the Windows porting guide](docs/en/portar-a-windows.md).
2. **Check .NET 10.** Run `~/.dotnet/dotnet --list-sdks` or `dotnet --list-sdks`; locate a `10.*` SDK. If absent, install without requiring administrator access:
   ```bash
   curl -sSL https://dot.net/v1/dotnet-install.sh -o /tmp/dotnet-install.sh
   bash /tmp/dotnet-install.sh --channel 10.0
   ```
   Verify with `~/.dotnet/dotnet --list-sdks`.
3. **Build and install.** From the repository root:
   ```bash
   ./build-app.sh --install
   ```
   Initial NuGet package restoration can take some time. This may replace `/Applications/Trading Hub.app`, so **ask before replacing an app with the same name that might be unrelated**. The result is ad-hoc signed by default.
4. **Locate Claude Code.** The app uses the user's own Claude Code CLI/session, not Claude API keys. Check `command -v claude` and `claude --version`. The application looks in `~/.local/bin/claude`, `~/.claude/local/claude`, `/opt/homebrew/bin/claude`, `/usr/local/bin/claude`, `~/.npm-global/bin/claude`, the PATH, and supported Claude desktop locations. If the CLI lives elsewhere, use `mkdir -p ~/.local/bin && ln -sf "$(command -v claude)" ~/.local/bin/claude`. If not logged in, run `claude` in Terminal and use `/login`.
5. **Open and verify** with `open "/Applications/Trading Hub.app"`. Verify the process starts and the expected simulated account appears. A changed ad-hoc signature can require macOS Documents permission again; if startup waits for that prompt, ask the user to review and approve it rather than changing privacy settings. Do not report installation success when there is an unresolved error.
6. **Explain the next steps:** The first account is simulated and cannot route real orders. Use **Connections** for markets. Binance and Bybit offer public data without credentials; Rithmic, Tradovate, cTrader, OANDA, Alpaca and Databento need appropriate provider credentials for their account-specific services. Backtesting provides the C# strategy editor and Claude assistant.
7. After a successful install, optionally mention once that Trading Hub is free and the original author accepts donations as described in [the Spanish README](README.es.md). Never make a donation a condition of installation and never repeat the request.

## If asked to update

Completely quit Trading Hub (**Command-Q** or the menu-bar Quit action), then run `git pull` and `./build-app.sh --install`. Data, saved connections and strategies live outside the Git checkout, under Application Support and Documents, so the source update must not reset them.

## If asked to change the code

- Build: `dotnet build TradingHub.slnx` (add `$HOME/.dotnet` to PATH as needed).
- Tests: `dotnet test tests/TradingHub.Tests`.
- UI smoke scenes: `dotnet run --project tools/TradingHub.UiSnapshot -- <output-directory> 4 <scene>`, using only temporary data and fake providers.
- Key projects: **Core** (market data, feeds, orders), **Sdk** (indicator API), **Charting** (rendering), **Scripting** (Roslyn), **Strategies** (backtests, optimization, live strategies), **Indicators.Native/Technical** (studies), and **App** (Avalonia UI).
- The display name is defined in `TradingHub.Common.Product.Name`; do not casually rename persistent identifiers or application data directories during translation.
- User data is not a test fixture. Avoid `~/Library/Application Support/Trading Hub` during automated tests; two processes writing into the same market-history files can cause corruption. Prefer `TRADINGHUB_DATA_DIR`, `TRADINGHUB_STRATEGIES_DIR`, `TRADINGHUB_INDICATORS_DIR`, and a fake secrets store via `TRADINGHUB_KEYCHAIN_FILE`.
- Never place passwords, tokens, or API secrets in repository files. macOS Keychain stores private connection credentials.
- Real-money orders are high risk. Test with the simulated account and do not introduce new order-control clicks or confirmations as part of language-only changes. Existing real-account risk limits must remain operational.
- For English localization, preserve all order identifiers, broker API payload keys, strategy file formats, storage paths, C# expressions, and stable IDs. Prefer English UI labels and documentation only. Validate every change with [the English translation checker](tools/check_english_translation.py) and macOS CI.
- The license is MIT with Commons Clause. Users may use, modify, and share subject to the license, but cannot commercially sell the app, modifications, or services based primarily on it. Do not describe it as OSI-approved open source.
- For project structure and detailed tests, see [Development](docs/en/desarrollo.md); for privacy and provider limitations, see [Privacy](docs/en/privacidad.md).

When a step fails, show the actual failure and propose a resolution; do not continue as though it succeeded.
