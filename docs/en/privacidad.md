[← Documentation index](./README.md)

# Privacy and security

Trading Hub runs locally: app, market data, scripts, backtests and connection credentials. It has no Trading Hub account/backend and the reviewed source does not implement usage telemetry or automatic error uploads. Configured providers, explicitly enabled assistant/research actions and consented external alert destinations still make network requests.

## Local storage

| Location | Contents |
|---|---|
| macOS Application Support/Trading Hub; Windows `%APPDATA%\Trading Hub` | Settings, nonsecret connection metadata, workspace, ticks/bars, drawings, templates, account records, backtests, live runners and portfolios. |
| Documents/Trading Hub | Strategies, Estrategias/Reportes reports and custom indicators. |
| macOS Keychain | Passwords, secrets, tokens and Databento keys under service Trading Hub. |
| Windows Credential Manager | Native generic credentials under stable targets `Trading Hub/<account>`, scoped to this user/computer. |

See [Installation](./instalacion.md#where-your-data-lives) for exact paths. AI-related local files include Chats, the compatibility-named `Capturas del laboratorio` screenshot folder (most recent 60), and claude-lab-mcp.json. Unexpected errors go to local crash.log; they are not automatically submitted.

## Credentials

- Production secrets use macOS `security`/Keychain or native Windows Credential Manager (`CredRead`/`CredWrite`/`CredDelete`), not normal JSON files.
- connections.json contains name, username, server, public API identifiers and options. Public IDs/account names can still be sensitive metadata; avoid sharing them unnecessarily.
- Deleting a connection never deletes history. Captured Bybit private contexts needed for unresolved operations/exposure/protection remain available until safely reconciled and drained; failed private-store writes do not silently replace them. Windows uninstall intentionally retains data, scripts and credentials.
- Laboratory tools do not expose stored credentials. Do not put credentials into source, prompts or reports, where file-reading tools could access them.
- Both Bybit API credentials, Longbridge tokens, proxy authentication, Telegram tokens and **complete capability webhook URLs** belong in the private store. A webhook hostname/path/query can itself be a credential; ordinary alert state/logs must not retain a supposedly harmless origin or full URL.
- Native private-store work has bounded admission/cancellation and owned process/stdout/stderr drain. Local alerts never wait on native secret reads. Revocation closes local egress permission even when private cleanup/persistence fails, with the failure visible; deleting a setting is not proof of confirmed credential removal.
- Already-durable external alert bodies are stored as revision-bound **AES-GCM ciphertext** with a private per-grant key, not plaintext placeholders or a general credential vault. Private-store unavailability pauses delivery without discarding that encrypted outbox across shutdown/restart. Authenticated-metadata/ciphertext tampering cannot be bypassed by Retry. Intake still waiting for required private material is visibly **not yet durable**; a local alert is not proof of a persisted external delivery.
- Deferred intake captures its original destination/grant revision at acceptance. Grant changes/new recipients never acquire an earlier event implicitly. Active credential redactions remain pinned separately from bounded preview history; final confirmed material is applied again before sealing/sending, so unsaved previews cannot evict live secret protection.

## Network access

| Feature | When | Destination |
|---|---|---|
| Market data | Opening/using an instrument on an enabled connection. | Configured Binance, Bybit, Longbridge, aggregate constituents, public crypto markets, Rithmic, Tradovate, Interactive Brokers, OANDA, Alpaca or cTrader provider. |
| Public crypto markets | Listing, candles and live data of an enabled public crypto connection or aggregate constituent. No account, key or user identifier is sent. | OKX `www.okx.com`, `ws.okx.com`; Bitget `api.bitget.com`, `ws.bitget.com`; Hyperliquid `api.hyperliquid.xyz`; Deribit `www.deribit.com`; MEXC `contract.mexc.com`; Coinbase `api.exchange.coinbase.com`, `ws-feed.exchange.coinbase.com`; Bybit spot `api.bybit.com`, `stream.bybit.com`. |
| Derivatives pane, derivatives studies and Workbench panels | Only while a chart shows the **Derivatives** indicator or a study that uses open interest, funding or liquidations (NL/NS, Bar statistics strip derivative rows, Decision board, Liquidation map estimate, Liquidation bursts), or while the Workbench **Aggregated trades**, **Liquidation map** or **Derivatives** panel is visible (in the page or its window). Liquidation streams stay connected while such a study or panel needs them; observed liquidations and connection windows are stored under the market-data folder in `Derivatives/Liquidations`. The Workbench derivatives panel also reads each venue's current premium (mark/index) and long/short account ratio about once a minute. | Binance `fapi.binance.com` (funding, open interest, `premiumIndex`, `globalLongShortAccountRatio`) and `fstream.binance.com` (forceOrder); Bybit `api.bybit.com` (funding, open interest, `tickers`, `account-ratio`) and `stream.bybit.com` (allLiquidation). |
| Workbench derivatives (additional venues) | Only while the Workbench **Derivatives** panel is visible: per-venue funding, open interest, premium and long/short about once a minute, and the cross-coin funding board at most every few minutes. Bitget's contract list and Hyperliquid's coin list are read at most once an hour to find each coin's listing. Public endpoints; no account, key or user identifier is sent. | Binance `fapi.binance.com` (`/fapi/v1/fundingInfo`, all-symbol `premiumIndex`); Bybit `api.bybit.com` (all-symbol `tickers`); OKX `www.okx.com` (`/api/v5/public/funding-rate`, `funding-rate-history`, `mark-price`, `open-interest`, `/api/v5/market/index-tickers`, `/api/v5/rubik/stat/contracts/open-interest-history`, `long-short-account-ratio-contract`); Bitget `api.bitget.com` (`/api/v2/mix/market/contracts?productType=USDT-FUTURES`, `current-fund-rate`, `history-fund-rate`, `ticker`, `open-interest`, `account-long-short`); Hyperliquid `api.hyperliquid.xyz` (`POST /info` `fundingHistory`, `metaAndAssetCtxs`). |
| Third-party liquidation levels | **Off by default.** Only after enabling **Settings → Third-party data → Third-party liquidation levels**, and only while a BTC chart shows the **Liquidation levels (third-party estimate)** study or the Workbench **Liquidation map** panel is visible for BTC: at most one request per minute. No account, key or user identifier is sent; the service sees your IP address and request time. Turning the setting off removes the client. The response is a third-party estimate (Hyperliquid positions from HyperTracker snapshots), not exchange data. | `levels.hiverust.dev` (`/v1/btc/liquidations.json`), operated by a third party. |
| Options data | Only while an **Options data** connection is enabled and a chart shows an options study (or the connection is tested). Without that connection no options request is made. Per-strike snapshots (30-minute, kept a week) and daily summaries are stored under the market-data folder in `Options/`. | Deribit source: `www.deribit.com` public API (`/api/v2/public/get_book_summary_by_currency`, `get_last_trades_by_currency_and_time`, `get_volatility_index_data`); no account, key or user identifier. Custom JSON API: the URL you enter, with your privately stored key in the header you choose. |
| ETF flows | Only while an **ETF flows** connection is enabled and a chart shows **Spot ETF flows** (or the connection is tested). | The URL you enter (`/flows`), with your privately stored key in the header you choose. |
| Polymarket odds | Only after you enter a market in **Polymarket odds vs price**; the market slug and outcome token are sent, refreshed about every two minutes. | Polymarket public APIs `gamma-api.polymarket.com` (market lookup) and `clob.polymarket.com` (`prices-history`); no account or key. |
| Correlation coefficient | Only while a chart shows **Correlation coefficient**; the compared symbol (ETHUSDT by default) is requested. | Binance public klines (`fapi.binance.com` for futures, `api.binance.com` for spot); no account or key. |
| History | User download or laboratory download_data request. | Public archives or configured provider APIs. |
| Long-history/context bars | Loading older charts or context without stored ticks. | Relevant market provider; crypto can use public Binance/Bybit bars without account login. |
| Orders | Manual or strategy submission to a broker account. | That broker; simulated orders are internal. |
| Databento | Connection tests, availability/cost estimates and confirmed paid downloads. | Databento using the stored key. |
| Strategy assistants | Assistant or laboratory invocation; the risk agent uses Claude Code. | Anthropic through the user's Claude Code account, or the selected optional Codex provider for strategy chat. |
| Interactive Brokers data | Connection test, enabled live feed, or an explicit historical-data request. Stored/offline mode does not connect. | Configured TWS/IB Gateway, local by default. No IB orders. |
| Links / cTrader authorization | Clicking external links or authorizing access. | System browser and the selected provider/author site. |
| Candle maintenance | Only after explicit enable/selected instruments, or an explicit Run. | The configured provider, with bounded nonoverlapping work and visible partial/failure status. |
| External alerts | Only while an explicitly consented destination remains authorized. | The privately stored webhook or Telegram endpoint; persistent at-least-once retry may duplicate after uncertain remote acceptance. Practice events remain local. |
| Pine handoff | Only after original-source preview and explicit selected-assistant send confirmation. | The user's own selected CLI/model provider; text-only drafting with no automatic C# save/compile/execute. |
| Local market-data API | **Off by default.** Only after enabling **Settings → Local market-data API**. It is a local-only server: it listens on `127.0.0.1` (never other interfaces), answers only requests whose `Host` is `127.0.0.1`/`localhost` with its port, refuses browser requests from origins you did not list, is read-only and never returns credentials, accounts, orders or positions. Any program running under your account on this computer can read the market data it serves while it is on. It serves only instruments already open in the app: candles are built from trades already loaded, and it never opens a connection, signs in or requests history. See [Workbench](./workbench.md#local-market-data-api). | No outbound destination of its own; inbound loopback connections on the configured port (default `48231`). |

There is no built-in updater or automatic crash-report submission in the reviewed source. Building requires NuGet/network access independently of normal app operation. Providers and CLI software have their own policies; this is not a packet-level audit of third-party components.

## Claude

Prompts, supplied strategy/indicator files, metrics, trades, data summaries, chart images and requested live exposure may be sent to the selected assistant provider. Strategy chat starts in the strategy folder. The application requests engine-specific file tools, directory access and MCP permissions; ordinary Claude chat does not enable terminal or web tools. These requests are not proof of an operating-system filesystem sandbox. Effective access depends on the selected CLI's enforced permissions and version. Review access before sending private content. Risk reviews have no tools and receive only supplied portfolio figures.

Data handling depends on the selected CLI account and provider policies. See [Claude and optional Codex](./asistente-claude.md). Local storage does **not** mean AI requests stay local. Historical conversations/reports are retained in their original language/content.

## Real money

- Simulation is the default; real destinations require per-connection permission.
- Existing manual-order confirmation rules remain; manually launching/resuming an automatic real strategy asks once. Automatic real resumption is disabled by default because it does not show that dialog.
- Live sizing, daily-loss/drawdown limits and Stop all remain available. Automatic risk-agent changes are not allowed for portfolios containing real-account members.

See [Connections](./conexiones.md#real-and-test-accounts), [Trading safety](./operar.md#safety) and [Live](./en-vivo.md). Limits/actions cannot guarantee execution during outages. Imported C# and plugin DLLs are unsandboxed and should be trusted/reviewed before running.

## macOS permissions

- **Administrator password:** only for explicitly enabled closed-lid operation, using a privileged power helper. It restores normal sleep when the app exits; see [Settings](./ajustes.md#keep-awake).
- **Control Terminal:** Claude sign-in opens Terminal with `claude /login`.
- Locally built apps use ad-hoc signing unless a certificate is configured; see [Signing](./instalacion.md#ad-hoc-signing).

Localization tests do not enable privileged closed-lid operation or request broker credentials.

## Development isolation

Tests/scenes use six fresh data/script/backtest/live roots and fake JSON credentials, with sleep/power integration disabled. Screenshot tooling starts from empty owned fixtures, never copies personal profiles/history/scripts, disables external/default providers and uses explicitly configured fakes. See [Development](./desarrollo.md).

TRADINGHUB_KEYCHAIN_FILE is a **plaintext testing override**, not encrypted production credential storage. Do not place real secrets there or include it in diagnostics.

## Delete everything

This is optional irreversible removal, not an update step:

1. Fully quit using ⌘Q on macOS, Alt+F4 on Windows, or Quit Trading Hub. Resolve protection choices and broker acknowledgment warnings first.
2. Back up anything required; delete the platform's data directory, and Documents/Trading Hub only if you also intend to erase scripts/reports.
3. In Keychain Access or Windows Credential Manager remove the relevant Trading Hub entries.
4. Remove the Applications bundle or uninstall through Windows Settings → Apps; see [Uninstalling](./instalacion.md#uninstalling) and [Windows](./portar-a-windows.md).

The English upgrade itself does not delete data, rename stable folders or translate personal records in place.

## Optional assistant and research data

Strategy chat defaults to Claude and optionally uses the user's own Codex CLI login, subscription and quota. Prompts, source files read/edited by the CLI and selected Laboratory tool results can reach that engine's provider. Cost/token reporting can be unavailable; null is not zero/free. Histories remain local and engine-bound, but local storage does not prevent provider-side processing/retention.

TradingView research starts off. Explicit app-owned OAuth uses dedicated native secret entries; nonsecret grant/review metadata and schema fingerprints are stored in `tradingview-research.json`. Discovery retrieves paginated metadata; approved calls send arguments to the official remote MCP endpoint and may send returned research into model context. Review/revocation is enforced before calls, but cannot retract data already sent or provider charges already incurred. Essential+ eligibility excludes trials; supported client registration and real account interoperability remain unverified.

No CLI credential scraping or global CLI configuration rewrite is used. A workspace CLI network restriction is not a remote-MCP firewall or an in-process C# sandbox. Do not put secrets in prompts, sources or shared captures; see [assistant boundaries](./asistente-claude.md#optional-tradingview-research). IB data requires its own Gateway/entitlements unless explicitly offline; OI uses Binance public observations. Imported bars remain synthetic, not execution records.

Built-in source viewing does not execute that source. Editable copies preserve attribution and require real compilation; imported C#/DLLs remain unsandboxed. Pine originals are preserved byte-for-byte, the complete request/instructions are previewed, and explicit consent is separate from import. The selected CLI can add its own account/system context and retain content under its policies; the app's draft-only handoff is not a security/equivalence guarantee.

Depth recording is a separate off-by-default local data collection choice; turning on a heatmap is not consent to record. Optional verified storage relocation leaves originals intact and honors environment overrides. Captured app HTTP/WebSocket proxy policy does not control native IB TCP or external CLI/browser networking. True Quit/restart waits producer/provider/assistant work, then the application-global private store, before replacement/desktop exit; close-to-tray does not dispose those services.
