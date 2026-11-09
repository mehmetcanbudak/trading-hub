# Binance USDⓈ-M Futures demo session plan

This plan certifies the Binance futures order lifecycle on Binance's demo venue with test funds. Real orders stay disabled throughout. It is a procedure for a future session, not a record of results.

## Venue

Binance documents `https://demo-fapi.binance.com` (REST) and `wss://demo-fstream.binance.com` (WebSocket) as the USDⓈ-M testnet bases ([general information](https://developers.binance.com/docs/derivatives/usds-margined-futures/general-info)). Trading Hub uses that REST host for a Binance Futures connection with **Testnet** enabled. The former `testnet.binancefuture.com` host answers from the same service, and its web pages redirect to `demo.binance.com`.

Binance Spot is different: Spot Testnet (`testnet.binance.vision`) and Spot Demo Mode (`demo-api.binance.com`) are separate products with separate keys ([Spot demo mode](https://developers.binance.com/docs/binance-spot-api-docs/demo-mode/general-info)). Trading Hub's Spot testnet setting keeps using Spot Testnet. Spot is not covered by this plan.

## Preparation by the account owner

The assistant never sees the API secret.

1. Open Binance Demo Trading and create an API key with trading permission only, no withdrawals, in [API Key Management](https://demo.binance.com/en/my/settings/api-management).
2. In Trading Hub, open **Connections → Binance Futures**, turn **Testnet** on, and enter the key and secret. Leave **Allow REAL orders** off. The connection card shows "data + testnet account".
3. Decide who operates the app during the session: the owner, or the assistant through UI automation approved for that session.

## Isolation

Launch the installed app with a dedicated data folder. Set `TRADINGHUB_DATA_DIR`, `TRADINGHUB_LIVE_DIR` and `TRADINGHUB_STRATEGIES_DIR` to folders under `~/TradingHubTestnet/`. The personal workspace, ledgers and connections stay untouched. The secret is stored in the macOS Keychain under the new connection's id. An isolated data folder disables the tray icon, so quit with the app's Quit action.

## Checks before any order

- **Conditional orders.** Stops are placed through `/fapi/v1/algoOrder`. Place one stop far from the market first. If the demo venue rejects it, stop and record the error.
- **Prices.** Charts use mainnet prices, and the demo venue has its own book (for example 74,275.50 on the demo venue against 74,230.00 on mainnet on 2026-11-09). Before every priced order, compare the chart with the demo mark price and set offsets from the demo price.
- **Size.** Use one symbol (BTCUSDT), the minimum quantity and isolated margin.

## Scenarios

| # | Scenario | Pass when |
|---|---|---|
| 1 | Connect and wait for readiness | Orders and position are ready, and the balance is shown as testnet |
| 2 | Limit entry with a partial fill if the book allows, then cancel the remainder | The filled part is booked once, the remainder is canceled, and fees match the venue's trade history |
| 3 | Entry with stop and target, then move the stop | Exactly one stop is working after each move |
| 4 | Stop triggers while the position is open | The stop's MARKET order executes to completion, is never canceled, leaves the position flat, and no second stop is armed |
| 5 | Close part of the position manually while a triggered stop's MARKET order executes | The order is not shrunk or canceled by the app, and the venue limits the reduce-only order so the position ends flat, never reversed |
| 6 | Cancel all, then Flatten with a working stop | Everything is canceled and the position is closed once |
| 7 | Network off and on with a working stop | Orders and fills recover after reconnect with no duplicates |
| 8 | Small live strategy with stop and target; quit mid-position and relaunch | Recovery places no duplicate orders, and the ledger matches the venue's trade history |

Scenario 5 checks a venue behavior the app relies on. Binance documents that a reduce-only order in the same direction as the position is rejected, and that a market or stop-market reduce-only order with a quantity at or above the open position is accepted with the highest priority ([failed orders FAQ](https://www.binance.com/en/support/faq/detail/360039707291)). What the venue does with the excess quantity has not been observed.

## Evidence

For each scenario keep app screenshots, the venue's order and trade history, app log excerpts, and a ledger-to-venue comparison of quantities, prices, fees and PnL. Record results in [TRANSLATION_STATUS.md](./TRANSLATION_STATUS.md). The Binance item under remaining acceptance closes only when every scenario passes.

## Stop conditions

Stop immediately if an unexpected order appears, two stops are working at once, a position has no stop when one is expected, or a closing order is canceled. Use **Stop all and close**, record everything and fix the cause before continuing.

## Not covered

Real-money behavior, mainnet liquidity and latency, and Binance Spot.
