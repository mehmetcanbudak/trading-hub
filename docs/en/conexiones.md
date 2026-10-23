[← Documentation index](README.md)

# Connections

**Connections** (⌘4) configures market-data providers and trading accounts. Select a profile on the left to edit it on the right. List summaries show public-data/account status or the provider username/system. A gray dot means disabled, green/red reflects the last connection test, and neutral means not tested.

## Add and test a connection

1. Choose **New connection** and a type.
2. Fill in that provider's fields, described below.
3. **Test connection** saves first, then tests and shows the result above the form.
4. Use **Save** after subsequent edits.

The form also has a customizable name, type, Enabled switch and Delete action. Disabled profiles do not supply instruments or trading accounts. Deleting removes the profile and its Keychain secrets, **not recorded history**. The final built-in Simulator, Binance, Bybit or Rithmic profile cannot always be deleted; disable it instead.

Most providers have **History when opening a chart**, from 30 minutes to seven days. Already stored/downloaded data loads locally; missing history comes from the provider.

## Where credentials are stored

Passwords, API secrets, tokens and Databento keys use **macOS Keychain**, service `Trading Hub`, rather than ordinary files. Saved secret fields show a masked Keychain placeholder. Enter a new value to replace one; leaving the field blank preserves the saved value.

`connections.json` contains nonsecret metadata such as name, username, server, public API identifier (for example Binance API key ID or Alpaca Key ID) and options. Treat even nonsecret account metadata as private when sharing screenshots or diagnostics.

## Provider capabilities

| Provider | Data | Requirements | Orders |
|---|---|---|---|
| Simulator | Offline simulated market. | None. | Simulated account. |
| Binance Futures / Spot | Public crypto/live history. | None for public data; API key/secret for account trading. | Testnet or real. |
| Bybit | USDT linear perpetuals. | None for public data. | Not implemented. |
| Rithmic | CME futures, history and orders. | Username, password, system and server. | Practice or real. |
| AMP Futures | Rithmic integration. | AMP-issued Rithmic credentials. | Practice or real. |
| Tradovate | CME data and trading accounts. | Username/password plus API CID/secret; relevant entitlements. | Demo or real. |
| cTrader | CFD/forex data. | Open API application and authorized account. | Not implemented. |
| OANDA | Index/commodity CFD and forex data. | API token. | Not implemented. |
| Alpaca | US stocks and ETFs. | API key ID and secret. | Not implemented. |
| Databento | Historical CME ticks, including long ranges. | Paid-by-usage API key. | No live data or orders. |
| Interactive Brokers | Connectivity probe only. | API-enabled TWS or IB Gateway. | Not implemented. |

Any supported market-data source can feed the simulated account. Provider eligibility, subscriptions, API approval, retention, free tiers and prices can change; verify them with the provider rather than assuming the original project's advertised allowances still apply.

## Simulator

Offline order book and trade simulation with SIM-BTC, SIM-ETH, SIM-ES and SIM-NQ. No configuration required.

## Binance Futures and Spot

Public charts, DOM, tape and history need no account. For trading:

1. Choose **Testnet** (default) or **Real account**.
2. Enter API key and secret. Grant trading permissions only; **do not grant withdrawals**. The API-key help link follows the selected testnet/live environment.
3. For real trading, enable **Allow REAL orders from Trading Hub**. Otherwise it stays read-only and is not offered as a trading destination.
4. Review per-real-order confirmation, enabled by default, and maximum order quantity. **0 means unlimited.**

## Bybit

Public USDT linear-perpetual data only. Routing orders to a Bybit account is not implemented.

## Rithmic

Direct official R|Protocol integration; R|Trader Pro is not required. The profile starts disabled.

- **Username/password:** issued by the broker or prop firm; the eye button reveals entered text.
- **Server:** lists system/region combinations with measured latency. Faster choices are highlighted. Search paper, system number, region or firm name; refresh requests a new list. Colo 75 is intended for the CME datacenter, not ordinary internet clients.
- **Custom server:** manually enter `wss://…` and system name when absent from the list.
- **Aggregated quotes:** combine simultaneous order-book updates when supported; otherwise the connection falls back without aggregation.
- **Connection timeout:** default 10,000 ms.
- **Advanced:** application name (some systems require provider approval) and instrument-search product roots such as ES, NQ, MES, MNQ and CL. Search resolves each to the active contract, for example ES → ESZ6 in the original examples.

Accounts appear in Rithmic-chart tickets and Accounts once the order session connects. Rithmic generally allows one session per username; another program signing in can disconnect one session. Verify application approval with your provider before relying on it.

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

Tokens are stored in Keychain and refreshed automatically. CFD bid quotes are modeled as unit-volume trades, **not exchange executions**: footprint/delta are approximations.

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
- No live feed or instrument-search entry: use **Data → CME futures with Databento**. See [Data](datos.md#databento).

## Interactive Brokers

Enable the API in TWS / IB Gateway (Configure → API → Settings), then enter host, port and Client ID. Common ports are 7497 for TWS paper, 7496 for TWS live and 4002 for Gateway paper. This integration **only tests connectivity**, not data or orders.

## Real and test accounts

| Provider | Test destination | Real destination |
|---|---|---|
| Binance | Testnet submits without confirmation. | Requires explicit real-order permission. |
| Rithmic / AMP | Rithmic Paper Trading / Rithmic Test submit directly. | Other systems, including prop firms, require explicit permission. |
| Tradovate | Demo submits directly. | Requires explicit real Tradovate permission. |

Without permission, real destinations are absent from Chart Trader, and an order that would open, add to or reverse a position is blocked when it is sent, even from a chart or live strategy that still uses the account. Rithmic/Tradovate accounts still appear in Accounts for balances, positions and history, where Cancel and Close stay available (with confirmation) because they only reduce exposure. Real accounts are visibly marked **REAL** / **REAL ACCOUNT**.

### Enable real-money trading

1. Validate everything using simulation or the provider's test account first.
2. Select the correct live environment: Binance Testnet/Real, Tradovate Demo/Real, or a real Rithmic/AMP server/system.
3. Enable that profile's real-order permission.
4. Set maximum order quantity; larger requests are blocked.
5. Save and test the connection.

See [Trading safety](operar.md#safety) for confirmations and risk limits. These instructions explain the existing capability; translation validation does not authorize or test real-money orders.
