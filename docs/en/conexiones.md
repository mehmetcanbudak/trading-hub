[← Documentation index](./README.md)

# Connections

**Connections** (⌘4) configures market-data providers and trading accounts. Select a profile on the left to edit it on the right. List summaries show public-data/account status or the provider username/system. A gray dot means disabled, green/red reflects the last connection test, and neutral means not tested.

## Add and test a connection

1. Choose **New connection** and a type.
2. Fill in that provider's fields, described below.
3. **Test connection** saves first, then tests and shows the result above the form.
4. Use **Save** after subsequent edits.

The form also has a customizable name, type, Enabled switch and Delete action. Disabled profiles do not supply new instruments or trading accounts. Deleting never deletes recorded history. Captured Bybit credential contexts needed for unresolved requests, exposure or protection are retained for reconciliation/recovery rather than erased under an owned acknowledgment. The final built-in Simulator, Binance, Bybit or Rithmic profile cannot always be deleted; disable it instead.

Most providers have **History when opening a chart**, from 30 minutes to seven days. Already stored/downloaded data loads locally; missing history comes from the provider.

## Where credentials are stored

Passwords, API secrets, tokens and Databento keys use **macOS Keychain** (service `Trading Hub`) or native **Windows Credential Manager** (stable generic targets `Trading Hub/<account>`), rather than ordinary files. Saved secret fields show a masked native-store placeholder. Enter a new value to replace one; leaving the field blank preserves the saved value. Native Windows credential behavior remains a separate validation gate, not proof supplied by shared-runtime tests.

`connections.json` contains nonsecret metadata such as name, username, server, public API identifier (for example Binance API key ID or Alpaca Key ID) and options. Both Bybit API credentials, Longbridge access/refresh tokens and proxy/notification authorization secrets use the private store. Treat even nonsecret account metadata as private when sharing screenshots or diagnostics.

## Provider capabilities

For Bybit strategy ownership, a profile/display name is not an account identity. Persistent strategy/journal scope binds the provider, environment, normalized API authority and **freshly verified native UID**. Same-UID credential rotation preserves ownership; a different UID/origin cannot adopt old history/protection. Unknown UID waits visibly without a shared “unknown”/name fallback or silently discarding the saved startup intent.

| Provider | Data | Requirements | Orders |
|---|---|---|---|
| Simulator | Offline simulated market. | None. | Simulated account. |
| Binance Futures / Spot | Public crypto/live history. | None for public data; API key/secret for account trading. | Testnet or real. |
| Bybit | USDT linear-perpetual data; testnet data has a distinct identity. | None for public data; appropriate UTA API credentials for execution. | One-way UTA Demo, Testnet or Real, with explicit real-order permission. |
| Rithmic | CME futures, history and orders. | Username, password, system and server. | Practice or real. |
| AMP Futures | Rithmic integration. | AMP-issued Rithmic credentials. | Practice or real. |
| Tradovate | CME data and trading accounts. | Username/password plus API CID/secret; relevant entitlements. | Demo or real. |
| cTrader | CFD/forex data. | Open API application and authorized account. | Not implemented. |
| OANDA | Index/commodity CFD and forex data. | API token. | Not implemented. |
| Alpaca | US stocks and ETFs. | API key ID and secret. | Not implemented. |
| Databento | Historical CME ticks, including long ranges. | Paid-by-usage API key. | No live data or orders. |
| Interactive Brokers | Selected index-futures live/tick/bar data and stored/offline history. | API-enabled TWS or IB Gateway, compatible wire version and data entitlements; offline mode needs local data only. | Not implemented. |
| Longbridge | Configured quotes, recent tape, candles and partial depth. | Client ID, explicit OAuth grant and instrument/session metadata. | Data only; no Longbridge order route. |
| Aggregate crypto | Composite Binance/Bybit trade/candle data with constituent coverage. | Public constituent data or configured source profiles. | None: the composite price is not an executable venue, including ordinary simulation. |

Individual supported market-data sources can feed ordinary simulation; practice uses its isolated run account. The aggregate composite intentionally exposes **no order route**. Provider eligibility, subscriptions, API approval, retention, free tiers and prices can change; verify them with the provider rather than assuming the original project's advertised allowances still apply.

Aggregate numeric trade IDs remain constituent IDs and are not globally unique or monotonic. Canonical identity is source identity plus exact source event ID (`TradeIdentity.From`); uncertain observations stay explicitly partial rather than being deduplicated by equal price/time/size. First-venue book loss emits a missing/gap state independently of healthy tape from the other venue. Only a real usable fresh first-venue snapshot restores the book.

## Simulator

Offline order book and trade simulation with SIM-BTC, SIM-ETH, SIM-ES and SIM-NQ. No configuration required.

## Binance Futures and Spot

Public charts, DOM, tape and history need no account. For trading:

1. Choose **Testnet** (default) or **Real account**.
2. Enter API key and secret. Grant trading permissions only; **do not grant withdrawals**. The API-key help link follows the selected testnet/live environment.
3. For real trading, enable **Allow REAL orders from Trading Hub**. Otherwise it stays read-only and is not offered as a trading destination.
4. Review per-real-order confirmation, enabled by default, and maximum order quantity. **0 means unlimited.**

## Bybit

Choose **Demo**, **Testnet** or **Real** explicitly. Demo uses Bybit's demo execution environment with public main-market data; Testnet has its own market-data/history identity. API key and secret must belong to the selected environment. Real orders additionally require **Allow REAL orders from Trading Hub**, the existing confirmations and configured quantity limits. Never grant withdrawal permission.

The execution adapter accepts supported **UTA, USDT-settled linear perpetuals in one-way mode**, not hedge-mode, inverse/USDC or an assumed equivalent account. Unsupported metadata, missing positions/orders or unresolved submissions disable new exposure. A timed-out/ambiguous placement is reconciled by its stable client/order identity, not resubmitted as a new create. Cancellation/amendment remains pending until authoritative reports confirm it; partial/late actual executions retain their execution identities and available fee currency.

Native reducing exits use `reduceOnly`; local stop/target sibling handling is **not atomic exchange OCO**. Live runners retain stable client identity across pre-ACK process recovery and adopt only their owned protection. Provider holdings do not automatically become a strategy's owned position. Revoking real-order permission blocks increases/reversals but keeps cancellation and genuine reductions available.

Accounts keeps actual wallet values visible independently of missing execution history. UTA wallet equity is account-wide USD; displayed supported-contract open/closed PnL is provider-reported USDT, not an invented conversion or complete round-trip reconstruction. Missing/unconverted fees, unknown position/order snapshots and incomplete opening cost basis stay unavailable/labelled; bounded fill history is not permission to claim complete account performance.

Editing credentials creates a new immutable context. Previous contexts continue receiving owned acknowledgments/fills and remain durably recoverable until confirmed idle and drained; a failed private-store readback rejects the new context without erasing the old one. True Quit/restart awaits the session's owned HTTP/poll/fill work before releasing shared resources. These source capabilities still require real-provider entitlement/native validation; fake fixtures do not certify production accounts.

## Longbridge

Longbridge is **data only**. Configure client/region and supported instruments with explicit exchange, currency, tick/display grid, quantity and session/time-zone/calendar metadata. Missing metadata is not guessed from a symbol suffix or current price; a display analysis grid is not an exchange order-valid tick schedule.

Connect uses an owned loopback callback, PKCE and bounded matching state; cancellation, wrong/expired state and failed token persistence cannot publish an authorization. Refresh/access tokens remain in the private store. Quotes are quotes, not fabricated executed-volume prints. Recent tape, zero-volume candles and snapshot/partial depth carry their available source resolution/coverage. Reconnect requires a new book baseline; showing quotes alone does not establish continuous depth history or full historical tape.

## Aggregate crypto

The aggregate retains available constituent trade/candle data when another venue fails and exposes provider/time-specific partial coverage. Unknown buy-side volume is not silently treated as observed aggression. Stored/cached results keep source and correction identity.

The DOM/heatmap is explicitly the **designated first venue only**. It is not consolidated and does not fail over to another venue as though the venue were unchanged. Neither the composite instrument nor its first-venue book authorizes Binance, Bybit, Rithmic or Tradovate orders; choose a separate executable individual-source chart and account.

## Rithmic

Direct official R|Protocol integration; R|Trader Pro is not required. The profile starts disabled.

- **Username/password:** issued by the broker or prop firm; the eye button reveals entered text.
- **Server:** lists system/region combinations with measured latency. Faster choices are highlighted. Search paper, system number, region or firm name; refresh requests a new list. Colo 75 is intended for the CME datacenter, not ordinary internet clients.
- **Custom server:** manually enter `wss://…` and system name when absent from the list.
- **Aggregated quotes:** combine simultaneous order-book updates when supported; otherwise the connection falls back without aggregation.
- **Connection timeout:** default 10,000 ms.
- **Advanced:** application name (some systems require provider approval) and instrument-search product roots such as ES, NQ, MES, MNQ and CL. Search resolves each to the active contract, for example ES → ESZ6 in the original examples.

Accounts appear in Rithmic-chart tickets and Accounts once the order session connects. Rithmic generally allows one session per username; another program signing in can disconnect one session. Verify application approval with your provider before relying on it.

The fork retains its existing stronger working-revision/quantity/price acknowledgment protections. Source comparison found that KVP's `KeepPendingPrice` optimistically overrides displayed price until a matching report, termination or five-second timeout, whereas this fork waits for an exact **later WorkingRevision plus quantity and price**, terminal state or definite rejection; unknown outcomes stay pending and native price is not replaced before ACK. The KVP amendment engine was therefore not imported. This is source-review rationale, not a fresh runtime pass. Available Rithmic/AMP depth is one BBO level per side and remains partial; after reconnect fresh **price and size on both sides** are required before a refresh. It is not event-complete L2 or MBO coverage.

### If Rithmic will not connect or trade

- **Permission denied / username rejected:** the **Server** must select the exact system issued by your broker or prop firm. ATAS “Rithmic paper trading” corresponds to **Rithmic Paper Trading**; prop firms can use their own named systems. Check the password with the eye button without sharing it. If these match, ask the provider whether your user has R|Protocol API access and whether an approved **Application name** is required under **Advanced**.
- **Data works but the account is missing from Chart Trader or read-only:** Rithmic 01 and prop-firm systems are treated as real destinations, even when a firm's own terminology sounds simulated. Review [Real and test accounts](#real-and-test-accounts). Enable **Allow REAL orders from Trading Hub** only deliberately after validating the correct account/environment and risk limits; it is not a harmless connection-test workaround.
- **Unexpected disconnect:** Rithmic permits one session per username. Close R|Trader Pro, ATAS, NinjaTrader or other competing sessions as appropriate. Reconcile orders, fills and positions in the broker's tools before resuming automation; do not retry an uncertain order merely because data reconnected.


## AMP Futures

Uses the Rithmic credentials supplied by AMP. A new profile starts disabled with Chicago and **Rithmic Paper Trading**. For a real account select the appropriate **Rithmic 01** system/region. Other settings match Rithmic.

## Tradovate

1. Choose **Demo** (default) or **Real account**.
2. Enter Tradovate username/password.
3. Enter API **CID** and **Secret**, obtained through Tradovate Settings → API Access.
4. Review instrument-search product roots; each resolves to the active monthly contract.
5. Explicitly allow real Tradovate orders if needed and set a maximum order size.

Accounts appear in Chart Trader and Accounts. CME futures charts from Rithmic or Tradovate can use them. API market data requires the appropriate separate CME/API entitlement; without it, an entitled data connection can supply the chart while Tradovate handles execution. Tradovate Trader using the same username can interrupt the app's data session.

## cTrader

Supports CFD indices, forex, gold and oil with tick history, including broker demo accounts where available.

1. **Application, once:** create an app at openapi.ctrader.com using cTrader ID. Register the displayed redirect URI, `http://127.0.0.1:53682/ctrader`; the Copy button copies it. Enter Client ID and Secret.
2. **Authorize account:** choose demo or real, then **Connect with cTrader**. Complete browser authorization within five minutes. An application Playground access token can alternatively be pasted.
3. **Account number:** when several accounts share a token, enter the desired login; 0 selects the first.

Tokens are stored in the native credential store and refreshed automatically. CFD bid quotes are modeled as unit-volume trades, **not exchange executions**: footprint/delta are approximations.

## OANDA

Practice/real account data for index CFDs such as NAS100_USD, SPX500_USD, US30_USD and DE30_EUR, plus gold, oil and forex. History uses five-second bars where available.

- Select practice or real environment.
- Generate a token through **Manage API Access**; the form links to account/token setup.
- Enter the `101-001-…` account number, or leave blank for the token's first account.

The token is used for data only. Midpoint quotes count as unit-volume synthetic trades; no OANDA orders are submitted.

## Alpaca

Supports US stock/ETF history, including SPY, QQQ, IWM and DIA. The original integration targets consolidated SIP trade history from 2016 with a delay for the most recent 15 minutes, subject to account entitlement.

Enter the paper account's API key ID and secret. Select IEX (one exchange, available on the free tier where offered) or SIP (consolidated live data, generally paid) for live data. Credentials are used for data only; no Alpaca orders are submitted.

## Databento

Historical CME ticks, including aggressor side and long history beyond Rithmic retention. Availability can extend back to 2010 depending on the dataset/product. It is **paid by usage**; current signup credits and terms must be checked with Databento.

- Enter the `db-…` API key; signup/pricing links are provided.
- **Test connection** reports date availability and estimates a day of NQ tick data without downloading it.
- No live feed or instrument-search entry: use **Data → CME futures with Databento**. See [Data](./datos.md#databento).

## Interactive Brokers

Enable the API in TWS / IB Gateway (Configure → API → Settings), then enter host, port and a distinct Client ID. Common ports are 7497 for TWS paper, 7496 for TWS live and 4002 for Gateway paper. This selected adapter is **data only, never IB order execution**. It negotiates wire version **187 exclusively**, not arbitrary TWS API versions. Account/API approval and market-data entitlements remain provider prerequisites, not verified eligibility.

Supported roots are MNQ, MES, M2K, MYM, NQ, ES, RTY and YM. Same-instrument historical tick requests are paced at 15 seconds. Live trade side is inferred, not exchange-authoritative aggression; depth requires a fresh best bid/offer and reconnect invalidates stale state. Calendar contract rollover is not volume-roll selection or back-adjustment: price discontinuities are not adjusted away.

New chart defaults use the 18:00 America/New_York session; saved and custom sessions are preserved. Stable profile/data scopes keep history associated with the chosen profile and refresh after imports. Deleting a profile retires its retained transport; it does not delete history. Explicit stored/offline mode never connects to Gateway. See [Data](./datos.md#interactive-brokers-bars-and-offline-history) for import policy and fidelity. This data-only capability is part of accepted canonical `memoshi`; the [dashboard](./TRANSLATION_STATUS.md) separates its source/fake-fixture acceptance from real IB entitlements and native-provider checks.

## Real and test accounts

| Provider | Test destination | Real destination |
|---|---|---|
| Binance | Testnet submits without confirmation. | Requires explicit real-order permission. |
| Bybit | Demo or Testnet submits to that selected environment. | Real requires explicit permission and reconciled one-way UTA state. |
| Rithmic / AMP | Rithmic Paper Trading / Rithmic Test submit directly. | Other systems, including prop firms, require explicit permission. |
| Tradovate | Demo submits directly. | Requires explicit real Tradovate permission. |

Without permission, real destinations are absent from Chart Trader, and an order that would open, add to or reverse a position is blocked when it is sent, even from a chart or live strategy that still uses the account. Rithmic/Tradovate accounts still appear in Accounts for balances, positions and history, where Cancel and Close stay available (with confirmation) because they only reduce exposure. Real accounts are visibly marked **REAL** / **REAL ACCOUNT**.

### Enable real-money trading

1. Validate everything using simulation or the provider's test account first.
2. Select the correct live environment: Binance Testnet/Real, Bybit Demo/Testnet/Real, Tradovate Demo/Real, or a real Rithmic/AMP server/system.
3. Enable that profile's real-order permission.
4. Set maximum order quantity; larger requests are blocked.
5. Save and test the connection.

See [Trading safety](./operar.md#safety) for confirmations and risk limits. These instructions explain the existing capability; translation validation does not authorize or test real-money orders.

## Explicit network proxy policy

[Settings → Network proxy](./ajustes.md#network-proxy) applies captured system/direct/custom policy to app-owned ordinary HTTP and WebSocket transports, including discovery/history/private APIs, OAuth/revocation, public archives and research. Credentials are separate private-store values. New transports capture new policy; active contexts are not globally retargeted or forcibly disconnected during owned acknowledgments. Private HTTP and credential-bearing WebSocket handshakes reject redirects.

Interactive Brokers' native TCP connection and external assistant/browser processes are not app HTTP/WebSocket transports and are not represented as proxy-covered. Use the protection-aware Quit/restart path when a new connection policy must apply; a proxy error is not permission to retry an uncertain order or fall back silently to a direct route.
