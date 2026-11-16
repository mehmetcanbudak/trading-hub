# Exchange-side protection: venue decisions and demo checklist

[Exchange-side protection](./operar.md#exchange-side-protection) is an opt-in per connection. It is verified against fake venues that model each venue's documented contract. That is not evidence of the real venue's behaviour. This page records which venues use it and the demo-account checks that must pass before relying on it with real money. It is a procedure for a future session, not a record of results.

## Venue decisions

| Venue | Documented linked contract | Decision |
|---|---|---|
| Tradovate | [`POST /order/placeoco`](https://partner.tradovate.com/api/rest-api-endpoints/orders/place-oco) links two orders: when one fills, the other is cancelled. Each leg accepts its own `clOrdId`, and order entities report `ocoId` and `clOrdId` ([order item](https://partner.tradovate.com/api/rest-api-endpoints/orders/order-item)). [`placeOSO`](https://partner.tradovate.com/api/rest-api-endpoints/orders/place-oso) attaches brackets to an entry. | **Supported, opt-in.** A whole-position stop and target are sent with `placeOCO`. Entries keep their app-side attachments: `placeOSO` is not used, so an entry and its brackets never depend on an undocumented partial-entry rule. |
| Bybit (USDT linear) | [`/v5/position/trading-stop`](https://bybit-exchange.github.io/docs/v5/position/trading-stop) sets position TP/SL. `Full` is position-wide, market-only, cancelled when the position closes and resized with it. `Partial` adds TP/SL pairs, and a one-sided amendment unbinds a pair. [Order-attached TP/SL](https://bybit-exchange.github.io/docs/v5/order/create-order) uses the same modes. | **App-managed.** The system creates these orders without the client identity that ownership, pre-send journaling and unknown-submission recovery require, so their fills cannot be attributed to an owner. `Full` would close a strategy's share of the position together with the rest and turns a resting limit target into a market order. |
| Binance Spot | [`POST /api/v3/orderList/oco`](https://developers.binance.com/docs/binance-spot-api-docs/rest-api/trading-endpoints#new-order-list---oco-trade): "activation of one order immediately cancels the other", and cancelling one leg cancels the whole list. A list leg's price cannot be amended: only [Order Amend Keep Priority](https://developers.binance.com/docs/binance-spot-api-docs/faqs/order_amend_keep_priority) reduces quantity. | **App-managed.** A partially filled target cancels the stop, and moving either leg needs cancel-then-place while both lists would lock the same balance. Either would leave a holding unprotected. |
| Binance USDⓈ-M Futures | No linked order type. Stops and targets are separate [algo orders](https://developers.binance.com/docs/derivatives/usds-margined-futures/trade/rest-api/New-Algo-Order); `closePosition` closes the whole position but does not cancel a sibling. | **App-managed.** There is no linked contract to use. |
| Rithmic | R \| Protocol has OCO and server-side bracket requests (templates 328 and 330, with bracket-level updates). Their semantics are in the reference guide distributed with Rithmic's SDK under its conformance process ([APIs](https://www.rithmic.com/apis)); no public reference documents partial-fill or restart behaviour. | **App-managed.** Not verifiable from a public primary source, and Rithmic requires conformance testing before production order routing. |

## Tradovate demo checklist

Use a Tradovate **demo** account and an isolated data folder, as in the [Binance demo plan](./binance-demo-session.md#isolation). Enter credentials locally; the assistant never sees them. Keep **Allow orders on real Tradovate accounts** off. Use one micro contract (for example MES), quantity 1 or 2, and prices far enough from the market that nothing fills unless the step asks for it.

Before each step, note the time and what the Tradovate web platform shows, so the app's state can be compared with the venue's.

1. **Off by default.** A new Tradovate connection shows **Exchange-side protection (Tradovate OCO)** unchecked. With it off, open a position with Automatic SL/TP: two separate orders appear in Tradovate, and Chart Trader says **App-managed protection**.
2. **Linked pair.** Switch it on, close the position and open a new one. Tradovate shows one OCO strategy with both legs, each with the client order ID Trading Hub shows. Chart Trader says **Exchange-side protection**.
3. **Venue cancellation without the app.** Quit Trading Hub. Move the market or the target so the target fills. Confirm in Tradovate that the stop was cancelled. Start Trading Hub again: no stop or target is placed for the flat position.
4. **Partial fill.** With quantity 2, let the target fill 1. Record whether Tradovate keeps, resizes or cancels the stop. With Trading Hub running, the stop must end at 1 (resized, or replaced by an app-managed stop with a visible reason). Repeat with Trading Hub closed and record the stop's quantity: Tradovate documents no partial-fill rule and no reduce-only orders.
5. **Amendment.** Drag the stop and then the target. Each leg keeps its order ID and stays in the OCO group in Tradovate.
6. **Position change.** Add 1 to the position, then reduce it by 1 with a market order. Both legs follow the position.
7. **Removing one leg.** Remove the target in Trading Hub. Record whether Tradovate also cancels the stop. Either way, the position must keep a stop.
8. **Restart adoption.** With a linked pair working, quit and restart. Chart Trader shows the same two orders as **Exchange-side protection**, and Tradovate shows no new orders.
9. **Refusal.** If a refusal can be provoked safely (for example a stop on the wrong side of the market), confirm that separate app-managed orders replace the refused pair and Chart Trader shows the reason.
10. **Live strategy.** Run a strategy with a stop loss and take profit on the demo account. Its pair appears as one OCO in Tradovate.

## Evidence

Record, per step: the app's status line and Chart Trader text, the Tradovate order list (IDs, `ocoId`, quantities, prices, statuses) and the time. Attach screenshots without account numbers or credentials.

## Stop conditions

Stop and switch the option off if a position is ever left without a working stop, an order is duplicated, or the app and Tradovate disagree about which orders are working.

## Venues that stay app-managed

Binance, Bybit and Rithmic keep app-managed protection (see [Venue decisions](#venue-decisions)). Their app-managed behaviour, including Binance's reduce-only capping, is covered by the [Binance demo plan](./binance-demo-session.md). Revisit a decision only when the venue documents a linked contract that carries client identities and keeps a partially filled position protected.
