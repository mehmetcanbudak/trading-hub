# External acceptance checklist

Automated tests, fake venues and isolated UI scenes cover the code. The checks below need something the automation cannot supply: your accounts, a network without the blocks seen during development, physical hardware or your presence. None of them is required to use the app in simulation. Each one closes a specific gap that the [validation dashboard](./TRANSLATION_STATUS.md) and the [correctness program](./correctness-program.md) list as unverified.

Never paste API secrets into a chat or a repository file. Enter them only in **Connections**, which stores them in the macOS Keychain. Keep **Allow REAL orders** off unless a step says otherwise.

## Broker demo and testnet sessions

| Check | What it proves | Procedure |
|---|---|---|
| Binance USDⓈ-M Futures demo | Order lifecycle, algo-order stops, amendments, refusals and recovery against Binance's real demo venue | [Binance demo session plan](./binance-demo-session.md) |
| Binance: adopt a partly filled website order | Dragging a website-placed order that already filled partly adopts it with the exchange's confirmed baseline (only later fills are owned); an order older than 7 days with fills, and one older than about 3 months, behave as [Connections](./conexiones.md) describes | Add to the Binance demo session: place a limit order on the Binance website, let it fill partly, then drag it in Trading Hub. Repeat with an order whose fills are more than 7 days old. Record the status messages and the account journal |
| Bybit demo or testnet | Bybit one-way unified-account execution, triggered stops, fee facts and recovery | Use a Bybit demo account key with trading permission only; follow the same scenario list as the Binance plan (market, limit, stop, amend, cancel, restart with working orders) |
| Tradovate demo: exchange-side protection | How Tradovate actually orders OCO fill/cancel events, whether cancelling one leg cancels the other, partial-fill behaviour and position timestamps | [Exchange-side protection demo checklist](./native-protection-demo-checklist.md) |
| Rithmic / AMP demo | Native order ownership, reconnect Close and restart adoption on a real Rithmic gateway | Rithmic paper-trading credentials; flatten, reconnect and restart scenarios from [Trading](./operar.md) |
| Interactive Brokers data | Live TWS/Gateway market data, history backfill and reconnect | A running TWS or IB Gateway with market-data permissions; see [Data](./datos.md#interactive-brokers-bars-and-offline-history) |

## Accounts and sign-ins

| Check | What it proves |
|---|---|
| A real Claude Code request and a real Codex request | Strategy chat, tool calls and cancellation against your signed-in CLI (uses your plan's allowance) |
| TradingView research sign-in | The explicitly approved research tools against your TradingView account |
| Longbridge OAuth | Authorization, refresh and data entitlements for your Longbridge account |
| Webhook and Telegram alerts | Delivery, retries and revocation to your own endpoints (consented destinations only) |
| Options data and ETF flows with your own keys | The custom options and ETF-flow connections against the service you use |

## Network-dependent public data

These public endpoints were unreachable from the development network (TLS handshakes timed out or returned a block page), so only their documented payload shapes are tested. Run the app from an unrestricted network and open a chart that uses each source:

| Source | Used by |
|---|---|
| Deribit (`www.deribit.com`) | Public crypto market connection (Deribit), options data (Deribit source), options and DVOL studies, Workbench options panel |
| Polymarket (`gamma-api.polymarket.com`, `clob.polymarket.com`) | Polymarket odds versus price |

OKX, Bitget, Hyperliquid, MEXC, Coinbase, Bybit spot, Binance and the third-party liquidation-levels service passed a live read-only smoke test during development.

## Hardware and presence

| Check | What it proves |
|---|---|
| Sleep, lid close and wake while connected | Feeds reconnect, the sleep guard behaves, working orders and protections reconcile after wake |
| Sustained load and heat | No throttling-related stalls with several charts, depth recording and the Workbench open for a trading session |
| Physical power loss | The durable-state design survives a real power cut; development used simulated power cuts only (see [Live strategies](./en-vivo.md)) |
| Intel Mac | The `osx-x64` package was launched only under Rosetta on Apple Silicon; run it on Intel hardware |
| Native Windows, hands-on | Installer, Credential Manager, tray, DPI and brokerage behaviour on a real Windows 10/11 machine (deferred by choice; CI covers build and tests) |

## Recording results

Record each completed check in the dashboard with the date, account type (demo, testnet or real), app commit and the observed result, including failures. A failed check stays visible until it is fixed and re-run.
