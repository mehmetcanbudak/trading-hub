# Workbench

[← English documentation](./README.md)

The **Workbench** page (**⌘6** on macOS, **Ctrl+6** on Windows, or **Workbench** in the top bar) gathers market-analysis
panels for one instrument: an aggregated trades tape, a full-panel depth heat map, a liquidation map, a derivatives
dashboard, an options dashboard and a toolbox of calculators. Everything is drawn natively by Trading Hub from the
connections and data layers you already use; no web pages, browsers, Python processes or bundled third-party
applications are involved. **Nothing on this page places, changes or cancels orders.**

## Instrument and panels

- By default the Workbench **follows the active chart** (blind practice replays are never followed). Untick
  **Follow the active chart** and use the instrument picker to analyse another market; picking opens it on its
  connection exactly like opening a chart.
- Choose a panel in the left list. The **open-in-window** button next to each panel detaches it into its own window;
  closing that window returns the panel to the page, and windows that were open at quit reopen the next time the
  Workbench page is opened. Several panels can be visible at once this way.
- A panel works only while it is visible (in the page or in its window): hidden panels release their streams and stop
  requesting data.
- Choices (selected panel, thresholds, ranges and calculator inputs) are remembered in the settings file.

## Aggregated trades

A live tape of every print of the instrument. On an **aggregate** crypto connection each constituent venue is listed
separately (for example Binance Futures, Bybit and OKX), so this is a multi-venue tape.

- Prints from the same venue, side and millisecond are merged into one line (switchable). Lines below **Hide below**
  are not listed; **Large** and **Huge** lines are highlighted. Thresholds are in quote notional (price × size × point
  value).
- The right side shows the price with large-trade bubbles and liquidation diamonds, per-venue buy (up) and sell (down)
  notional per bar of the chosen length, and liquidations per bar (longs below, shorts above). Volume bars always count
  every print, whatever the display threshold.
- **Coverage is explicit.** The coverage line names each venue's state (live, partial, connecting, reconnecting,
  error, missing); bars of a venue that was not connected are outlined as partial. Time before the panel started
  observing is hatched, not drawn as zero.
- **Liquidations** come from the public Binance and Bybit liquidation streams for USD-quoted crypto perpetuals, only
  while the panel listens. Bars the streams did not observe are hatched; partly observed bars are outlined. Binance
  publishes at most one liquidation per symbol per second, so its stream is a sample.
- Venue buttons hide a venue's lines from the tape (bars still include it).
- **Alert on large trades / Alert on liquidations** (off by default) raise a *local* alert in the alert history at most
  every three seconds. Its sound follows **Settings → Sound alerts**; these alerts are never forwarded to external
  destinations, even when you have authorized some.

## Depth heat map

A full-panel time × price map of **resting L2 size** (not traded volume), built from the same depth recorder and
renderer as the chart's liquidity heat map, with the instrument's trades as dots and the current book at the right edge.

- Depth history exists only when **Settings → History and depth → Record received depth** is on. That opt-in is never
  changed here: with recording off the panel says so and shows only the current book.
- Shaded intervals are missing or stale depth; amber underlines mark partial or truncated books. Choose the window
  (5 minutes to 4 hours) and the price grouping in ticks.

## Liquidation map

Three estimates on one price axis, each labelled:

- **Local estimate from open interest** (left): open-interest increases on Binance and Bybit over the last seven days of
  15-minute candles, split by each candle's measured buy/sell volume and spread over a 5×–100× leverage mix; levels the
  price already traded through are removed. It is the same model as the chart study *Liquidation map (estimate from
  open interest)* and needs both venues' open interest and candles with buy volume.
- **Third-party estimate** (right): the published Hyperliquid BTC liquidation levels, only after you enable **Settings →
  Third-party data → Third-party liquidation levels**; snapshots older than three hours are not drawn.
- **Leverage grid** (dashed lines): where a long or short opened at the current price would be liquidated at 5×, 10×,
  25×, 50× and 100×, ignoring maintenance margin.

None of these is exchange data or a list of real positions.

## Derivatives

A table plus small charts for the coin's USDT perpetual on **Binance USD-M**, **Bybit linear**, **OKX** (USDT swap),
**Bitget** (USDT-M) and **Hyperliquid** (perp), from their public credential-free APIs: funding per settlement and
normalized to 8 hours, next settlement, open interest (coin units and USD at the mark price) with 1-hour and 24-hour
change, perpetual premium ((mark − index) / index; Hyperliquid uses its oracle price as the index and settles hourly), the
venues' long/short account ratio, and observed long and short liquidations over 24 hours.

- **What each venue publishes:** Bitget and Hyperliquid publish current open interest only (no history, so no change
  figures); OKX publishes the long/short ratio itself (shares are derived from it); Hyperliquid publishes no long/short
  ratio. These cells say "not published" rather than showing a number.
- **Liquidations** are streamed from Binance and Bybit only; OKX, Bitget and Hyperliquid show "not streamed".
- **Funding board:** BTC, ETH, SOL, XRP, DOGE and the selected coin across the five venues, normalized to 8 hours,
  colour-scaled, with the largest venue spread annualized ((highest − lowest 8-hour rate) × 3 × 365, needs at least two
  venues). Binance symbols without an entry in its funding-interval list settle every 8 hours. Refreshed at most every
  few minutes while the panel is visible; a coin a venue does not list is a gap.
- **Price and CVD** of the selected instrument from its own trades: CVD counts only trades whose aggressor side was
  measured; steps with inferred sides or compact/partial data are flagged, and steps without trades are gaps.
- **Live liquidation feed:** newest first, venue, side, size and notional, with a minimum-notional filter and a coverage
  line naming which venue streams are observed.

A value the venue did not publish, or whose request failed, is a dash (the failure is named under the table), and chart
spans without data are hatched gaps. Liquidation totals are "not observed" unless the stream was listening; a partly
observed day is marked partial. Ranges: 24 hours, 3 days or 7 days.

## Options

From your **Options data** connection (Deribit public data or your own JSON API, see [Connections](./conexiones.md)):
gamma exposure by strike with spot, gamma flip, call wall, put wall and max pain marked; total GEX versus price;
the IV smile of the expiry nearest 30 days; the ATM IV term structure; a per-expiry table (call and put open interest,
put/call ratio, max pain, ATM IV, 25-delta risk reversal and butterfly, one-standard-deviation expected move); and large
option trades of the last 24 hours, with page-limited spans named. With the Deribit source, a **DVOL** chart shows the
volatility index over the last 30 completed UTC days (missing days hatched, never zero) with today's forming value kept
apart; it reuses the source's existing daily request. Sources that publish no volatility index say so.

Without an options-data connection the panel shows how to add one and makes no requests. GEX is an estimate from public
open interest under the customers-buy-puts / sell-calls convention (USD per 1 % move), not dealer books.

## Toolbox

Calculators that only compute from the values you type:

- **Position size**: account balance, risk as a percentage or an amount, side, entry, stop, optional target, fee per
  side, point value and quantity step (prefilled from the instrument). It uses the chart long/short position drawing's
  rules: loss per unit = |entry − stop| × point value + fees on entry and exit; the quantity is rounded *down* to the
  step so the loss at the stop never exceeds the risk. Shows notional, the leverage it needs, profit at the target,
  reward/risk and the break-even win rate. Linear estimate; excludes slippage, funding and FX.
- **Liquidation price (approximate)**: isolated margin on a linear (USDT) perpetual —
  long ≈ entry × (1 − 1/leverage) / (1 − maintenance rate), short ≈ entry × (1 + 1/leverage) / (1 + maintenance rate).
  Exchanges use tiered maintenance rates and the mark price; trust the exchange's figure.
- **Funding cost**: notional × rate per settlement × whole settlements while holding; also per day and annualized.
- **Session clock**: Asia (18–02), London (02–08), New York morning (08–12) and afternoon (12–16) in New York time with
  your local times, and the next settlement of the common 8-hour funding schedule.
- **Structure summary**: a read-only summary of the selected instrument at 5m, 15m, 1h or 4h, computed by the same
  structure, key-level, session and session-VWAP studies used on charts over candles from the connection's candle history:
  trend, last break of structure / change of character, sweep and CISD, PO3 phase, nearest fair-value gaps, order blocks
  or breakers and liquidity above and below, previous trading-day and week high/low/open, and session ranges. Only
  closed bars count; every item shows when it formed and the close that confirmed it, and its distance to the price.
  Levels that are not published yet are dashes. It refreshes at most once a minute while visible.

## Local market-data API

An optional, **read-only** HTTP/WebSocket API on `127.0.0.1` lets tools you run yourself on this computer read the
market data Trading Hub already has. It is **off by default**: enable it in **Settings → Local market-data API**.

### Security model

- Listens on `127.0.0.1` only; other computers on your network cannot reach it. If another program already listens on
  the chosen port (on loopback or on all interfaces), Settings reports "Port … is already in use." and the API does not
  start, so it never silently takes over that program's local connections.
- Read-only: every route is `GET`. Nothing can be created, changed or deleted; the API never opens new market
  subscriptions, places orders or touches accounts or positions.
- No credentials, API keys, user names, account ids, gateway URLs, trading namespaces, device ids or order permissions
  are ever returned.
- DNS-rebinding defense: the `Host` header is required and must be `127.0.0.1:<port>` or `localhost:<port>` with the
  exact bound port (`421` otherwise).
- Cross-site defense: when a browser sends an `Origin` header it must exactly match an entry in **Allowed browser
  origins** (`scheme://host[:port]`, no path); other origins, and `Sec-Fetch-Site: cross-site` requests without an
  allowed origin, get `403`. Allowed origins receive `Access-Control-Allow-Origin: <origin>` and `Vary: Origin`; the API
  never answers `*` and never allows credentials. Non-browser tools (curl, scripts) send no `Origin` and need no entry.
- Bounds: one request per connection; request line ≤ 2048 bytes (`414`); headers ≤ 8 KiB and ≤ 64 lines (`431`);
  headers within 5 s (`408`); request bodies rejected (`413`); at most 16 concurrent connections and 4 streams (`503`);
  about 30 requests per second with bursts of 60 (`429` with `Retry-After`).
- Every response carries `Cache-Control: no-store`, `X-Content-Type-Options: nosniff` and
  `Content-Type: application/json; charset=utf-8`.
- Quitting Trading Hub (or turning the setting off) closes the listener and every stream as part of the app's owned
  shutdown drain.

Anything running under your user account on this computer can read the data while the API is on. Turn it off when you
do not need it.

### Errors

Errors are JSON objects such as `{"error":{"code":"not_open","message":"…"}}`.

| Status | Codes |
| --- | --- |
| 400 | `bad_request`, `bad_host` (missing Host), `bad_timeframe`, `bad_range` |
| 403 | `origin_not_allowed` |
| 404 | `not_found` (unknown route), `not_open`, `unknown_connection`, `unknown_symbol`, `no_klines` |
| 405 | `method_not_allowed` (with `Allow: GET`) |
| 408 / 413 / 414 / 431 | `request_timeout`, `body_not_allowed`, `uri_too_long`, `headers_too_large` |
| 421 | `bad_host` (Host is not loopback with the bound port) |
| 426 | `websocket_required`, `websocket_version` |
| 429 | `rate_limited` |
| 502 | `provider_error` |
| 503 | `busy`, `too_many_streams`, `unavailable` (for example while Trading Hub shuts down) |

`OPTIONS` preflight requests are answered with `204` only for allowed origins (`Access-Control-Allow-Methods: GET`),
otherwise `403`. Other methods return `405`.

### Routes

Base URL `http://127.0.0.1:<port>/api/v1` (default port `48231`). Times are Unix milliseconds (UTC). `conn` accepts a
connection id from `/connections` or its market-data id (the `connection` value of `/open`). Symbols are
case-insensitive.

| Route | Response |
| --- | --- |
| `GET /info` | `{"app","version","apiVersion":1,"readOnly":true,"port"}` |
| `GET /connections` | `[{"id","name","kind","enabled","marketData"}]` for connections with chart market data. |
| `GET /open` | Instruments open in Trading Hub: `[{"connection","symbol","tickSize","lastPrice","trades","hasDepth"}]` (`lastPrice` is `null` before the first trade). |
| `GET /klines?conn=&symbol=&tf=1m\|5m\|15m\|1h\|4h\|1d&from=&to=&limit=` | `[[openTime, open, high, low, close, volume, buyVolume\|null]]` from the candle cache; missing bars are filled from the connection's own provider exactly as a chart would. `limit` ≤ 5000 (default 1000); `from` is raised to `to − limit × tf`. 4h/1d are built from 1-hour candles aligned to UTC. |
| `GET /trades?conn=&symbol=&limit=` | `[[time, price, size, "b"\|"s"\|null, venue\|null]]`, latest trades of an **already open** instrument (`404 not_open` otherwise); `limit` ≤ 5000 (default 500); `venue` is set on aggregate connections. |
| `GET /depth?conn=&symbol=&levels=` | `{"time","bids":[[price,size]],"asks":[...]}` of an open instrument; `levels` ≤ 500 (default 100). |
| `GET /liquidity?conn=&symbol=&minutes=&levels=` | `{"recording","tickSize","truncated","samples":[{"time","usable","gapBefore","bids","asks"}]}` from the opt-in depth recorder; `minutes` ≤ 120 (default 30), `levels` ≤ 100 (default 20), newest 2000 samples. With recording off: `{"recording":false,…,"samples":[]}` — nothing is synthesized. |
| `WS /stream?conn=&symbol=` | RFC 6455 WebSocket for an open instrument (same Host/Origin rules). Every 250 ms: `{"type":"trades","symbol","data":[[time,price,size,side,venue]]}` (≤ 5000) and, when the book changed, `{"type":"book","symbol","time","bids","asks"}` (≤ 100 levels). A slow client gets `{"type":"gap","symbol","dropped":n}` before newer trades. Client messages are ignored (≤ 4 KiB each); the server closes normally when the API is turned off or Trading Hub quits. |

Example:

```bash
curl -s "http://127.0.0.1:48231/api/v1/trades?conn=binance-futures&symbol=BTCUSDT&limit=10"
```

### Using it from your own tools

Trading Hub does not bundle, download or launch any external analysis application. If you install and run your own —
for example a self-hosted aggregated-trades page (such as aggr) or a heat-map tool (such as FlowMap), or a script — you
can feed it from this API:

1. Turn the API on, note the base URL shown in Settings, and open the instruments you want in Trading Hub (the trades,
   depth, liquidity and stream routes serve open instruments only).
2. **Scripts and native tools** (curl, Python, Node, a desktop app) need no further setup: they send no `Origin`.
3. **Browser-based tools served from your own local web server** (for example `http://localhost:3000`) must have that
   exact origin added to **Allowed browser origins**; requests from any other web page are refused.
4. Tools hard-wired to exchange APIs expect those exchanges' message formats. Point them here through their own
   custom-source option if they have one, or with a small adapter you write that maps `/stream` messages and `/klines`
   rows to the format they expect. Keep such adapters on `127.0.0.1` as well.

## Privacy

The Workbench makes requests only through features already listed in [Privacy](./privacidad.md): your connections'
market data, the public Binance/Bybit derivatives endpoints and liquidation streams (while the tape, derivatives or
liquidation panels are visible), the opt-in third-party liquidation levels and your options-data connection. The local
API is a local-only server that is off by default.
