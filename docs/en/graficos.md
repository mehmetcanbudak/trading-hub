[← Documentation index](./README.md)

# Charts

Charts live in **Trading** (⌘1). Each has drawing tools on the left and its own Chart Trader on the right. The top toolbar controls the active chart, meaning the last chart clicked.

## Chart toolbar

The toolbar is one row of groups: instrument · periods and history · chart style and footprint controls · analysis.

| Control | Action |
|---|---|
| Link color | Join a color group; see [Linked charts](#link-charts-by-color). |
| Instrument (⌘K) | Symbol, price and session change; click to select another instrument. |
| Periods / History | Favorite periods, the full period selector (the arrow) and how much history to load. |
| Chart style | Candles, Bars, Line, Heikin Ashi or Footprint; footprint adds its template and ticks-per-row controls. |
| Indicators | Open indicator settings (⌘I); the badge shows how many indicators the chart has. |
| ★ | Favorite indicators (one click adds one) and chart templates. In **Indicators**, right-click a library item → **Add to favorites**. |
| View | Navigation, rectangle zoom, reset, clock/time zone and shortcuts; the clock follows it. |
| ▶ | Practice replay. |
| Robot / palette | Chart strategies and chart settings. |
| Camera / full screen | Save a PNG of the chart (double resolution) in **Pictures/Trading Hub**; toggle full screen for the chart's window. |
| Layout | Number and arrangement of charts in this tab. |
| Panels | Show/hide the right panel, Chart Trader, DOM and tape; open DOM settings. |
| Detach / **+** | Detach the chart (⇧⌘D) or create a tab (⌘T). |

In narrower windows the toolbar stays one row: button labels become icons; below about 1,380 pixels the pinned periods fold into the period selector, and the clock, toolbar link color, screenshot and full-screen buttons are hidden (each chart header keeps its own link control). Only an extremely narrow window wraps a whole group to a second row, never cutting buttons.

## Instrument and history

The instrument selector searches enabled connections and displays symbol, description, price, 24-hour change and volume. Enter opens the active chart; ⌘Enter creates a tab; the star marks a favorite.

When a nonempty query has no matches in the current non-favorites profile, the selector can offer results from other enabled market-data profiles. Incomplete provider searches remain visibly incomplete. Switching the browse profile does not choose an instrument or change the trading account: an explicit picked result/Enter is still required. Favorites and an empty query do not trigger this fallback.

History ranges run from 30 minutes to five years, plus **All history** and **From a date**. Recent history uses individual trades and supports footprint. Older provider history uses one-minute bars, or five-/15-minute bars for ranges over 400 days; synthetic trades derived from these bars do **not** provide true footprint information. Tick history downloaded through [Data](./datos.md) remains tick-level. The status bar indicates where footprint-capable history begins.

When the chart itself has focus, committed text opens **Chart symbol / period**. A bare number can be either an exact numeric symbol or a minute period; both are separate visible choices. Choose the data connection explicitly, use arrows to select a visible result and Enter to apply it. A prefix without a selected row is not an implicit order or instrument selection. Escape cancels; `?` opens shortcut help. IME preedit is not committed chart text. Old asynchronous results are discarded after query, tab, focus, model, connection or window changes.

Quick-entry data navigation retains the selected account identity. If the account is incompatible with the new source, it is visibly **unavailable**, with no fallback order route, until a compatible account is explicitly selected. A composite crypto source has no executable account, including the shared simulator. Practice keeps its run-owned instrument/account; open a separate normal chart to choose another symbol. Existing order hotkeys and their confirmations are not changed by chart entry.

## Data and recording status

A small **Data warning** or **Not recording** badge keeps diagnostics out of the chart center. Hover or click the badge for the full current message, or navigate to the chart with the keyboard. **Escape** closes details, not the underlying condition; a new hover can open them again. Badge clicks do not start chart or order gestures. Ordinary chart clicks do not automatically open a large warning.

**“The bounded raw window is full”** means the chart reached its in-memory raw-trade limit, not that macOS ran out of storage. Additional coverage uses explicitly labelled candle history **when the provider has it**. Candle-derived or compact history does not become raw ticks or acquire missing footprint/aggressor detail.

**Not recording** means live data can remain readable while new ticks are not being saved. Read the complete cause—for example, another writer owns the history files or storage is unavailable—rather than deleting history or assuming recording continued. Recovery clears the obsolete disclosure automatically. The same controls work in detached chart windows.


## Periods

The defaults are 1m, 5m, 15m, 1h, 4h and D. The selector includes:

| Group | Periods |
|---|---|
| Seconds | 5s, 10s, 15s, 30s. |
| Minutes | 1m, 2m, 3m, 5m, 10m, 15m, 30m. |
| Hours | 1h, 2h, 4h, 6h, 12h. |
| Days | D, W and Month. |
| Ticks | 100, 250, 500 and 1000 trades per bar. |
| Volume | Vol 100, 500 and 1000 units per bar. |
| Range and others | Range 10/20 ticks, Delta 50/100 (close when absolute delta reaches the threshold), Renko 10. |

- **Right-click** a selector period to add/remove its toolbar favorite. Favorites are highlighted.
- **Custom:** enter a code and press Enter, for example `M3`, `H2`, `S20`, `Tick500`, `Volume100`, `Range20`, `Delta75` or `Renko5`.

## Chart styles

Choose **Candles**, **Bars**, **Line**, **Heikin Ashi** or **Footprint**. Switching to footprint increases bar width to make room for numbers.

## Footprint

![Footprint, Delta, Chart Trader and side panel](img/footprint.png)

Footprint adds two controls:

- **Template:** Bid x Ask (classic), Volume ladder + profile, Delta ladder + volume profile, Double ladder (bid | ask), Double delta ladder, Volume (cells), Delta (cells), Bid / ask histogram, Per-bar volume profile and Bid x Ask + profile. User templates have a leading star. **Edit columns** opens the editor.
- **Ticks per row:** Auto, which adapts to zoom, or 1–1000 ticks.

### Footprint editor

Open with ⇧⌘F, **Edit columns** in the footprint template selector or **Right-click → Edit footprint**. Changes appear immediately.

1. Choose a starting template.
2. Add left-to-right **Ladder**, **Profile**, **Cells**, **Spacer** or **Bar** columns. Arrows reorder and the trash button removes them.
3. Select a column and configure:
   - type: thin candle, colored cells, numerical ladder, histogram profile or spacer;
   - data: volume, delta, bid/aggressive selling, ask/aggressive buying, bid × ask or trade count;
   - relative width, profile direction, strong-delta/diagonal-imbalance/bar-POC/high-volume highlighting, volume/delta/bid-ask/heat-map/solid/no-background coloring, text alignment and numbers over bars;
   - per-column background and text colors; Automatic uses theme defaults.
4. General options include bar totals, POC boxes, imbalance marks, imbalance ratio and minimum volume (default ratio 3), strong-delta threshold, highlight minimum volume and scaling colors per bar versus all visible data.
5. Name and **Save as template** to reuse the layout.

## Navigate a chart

| Gesture | Result |
|---|---|
| Wheel | Horizontal zoom around the pointer. With **Chart settings → Axes → Wheel zooms the whole chart** the price range zooms by the same proportion around the pointer, like an image (off by default). |
| ⇧ + wheel / horizontal gesture | Pan through time. |
| Wheel on price axis / ⌘ + wheel | Vertical zoom. |
| + / − keys | Zoom around the chart center with the same rule as the wheel. |
| ⇧ + drag on empty space | Measure like the ruler without saving a drawing; the next click clears it. |
| Drag price axis / double-click | Change scale / restore automatic scale. |
| Drag time axis / double-click | Change bar width / return to latest data. |
| Lower-right return button | Appears after scrolling back; returns to latest data. |

See [First steps](./primeros-pasos.md#keyboard-shortcuts) for keys. **Chart → Auto scale** is ⌥⌘A; **Latest price** is ⌘End. Zoom always stays inside the bar-width bounds; when the bar width reaches its limit, whole-chart zoom stops scaling price too.

The selected deep-zoom path permits a minimum width of 0.1 pixel per candle. Dense rendering reduces visible candles into per-pixel OHLC/close-extrema geometry; the final visible candle supplies the deliberate color policy, not the aggregate bucket's net direction. This is rendering only: analytic bars, strategy inputs and stored history are not downsampled. Normal pointer-focal zoom, live-edge behavior and time panning remain in effect. The actual 50,000-candle scene passed in the unmerged fixture worktree; it does not certify every GPU/native host (see [dashboard](./TRANSLATION_STATUS.md#unmerged-non-kvp-adoption--2026-11-04)).

The **View** control also exposes rectangle zoom, reset view, automatic scale, session boundaries, chart clock/time zone and chart shortcuts. Rectangle selection clips to the main price pane and changes the selected time/price bounds; reset returns to automatic price scale and the live edge. Existing order, drawing-handle and indicator gestures take precedence. Per-chart time zones survive symbol/period changes rather than being silently replaced by the global preference.

## Multiple charts

- Layouts include one chart; two columns or rows; three columns; one large chart with two to the right or below; four (2 × 2); and six (3 × 2). New slots copy the active chart with other periods. Choosing a smaller layout keeps unused charts saved in the tab.
- Each chart in a multi-chart layout has a link-color control, title, maximize/restore, detach and close buttons.
- A detached chart keeps its toolbar, drawings and Chart Trader and can move to another monitor. ⌘W closes it, ⌘I opens indicators and ⌘K opens instrument search. **Return to main window** docks it into a tab.
- The context menu also offers **Move chart to new tab**.

### Link charts by color

Charts in the same red, green, blue, yellow, purple or orange group can share instrument, period and crosshair. The link menu chooses what synchronizes, globally for all groups. Joining a group adopts the instrument and period of its existing members.

## Chart settings

Open the palette icon or **Chart settings** from the context menu. Tabs cover background, candles, axes, price, footprint and trades. They also configure grid, bar data, last-price line, session boundaries and entry/exit line width/style. Each color has a palette, code and opacity; changed colors are marked. **Reset all** restores defaults; **Apply to all charts** copies the appearance.

The context menu controls the **Chart legend**—instrument, OHLC and indicator names—and **Mark my trades**, showing fill triangles and entry-to-exit lines, including previous days. Only one chart context menu is open at a time: another right-click closes the previous menu first, so an older menu's order prices cannot stay on screen.

## Drawings

The drawing bar on the left groups the tools: cursor (Esc); trend line, ray, extended line, horizontal line/ray, vertical line and cross line; rectangle/zone and ellipse; Fibonacci retracement; brush, highlighter and path; arrow; anchored VWAP and fixed-range profile; long position, short position and ruler; text. The small arrow to the right of a button (or a right-click on it) opens its group; the button keeps the last tool used.

- **Double-click** a tool to keep it active after each drawing (a dot marks it; Esc releases it). **Keep drawing** keeps every tool active.
- **Magnet:** weak snaps a point to the bar's high, low, open or close when the pointer is within about 14 pixels; strong always snaps; the arrow chooses the mode. Holding ⇧ always snaps.
- **Lock all** keeps drawings selectable but unmovable; **Hide all** hides them while they stay saved and their alerts keep running. Both are saved per chart. The eye's arrow also hides the indicators.
- **Delete** removes the selected drawing (Delete key); its arrow can delete all drawings of the instrument **without confirmation**. The star groups favorite tools together on the bar.
- A selected drawing shows a floating bar with color, width, line style, lock, quick price alert (horizontal lines, rays and cross lines), settings and delete. The bar can wait for a click instead of appearing right after drawing.
- **Path:** one click per point; double-click, a click on the previous point or Enter finishes; Esc keeps the points already placed. **Brush** and **highlighter** draw freehand and move as a whole.
- Shortcuts: ⌥H horizontal line, ⌥J horizontal ray, ⌥V vertical line, ⌥C cross line, ⌥F Fibonacci, ⌥⇧R rectangle, ⌥B brush, ⌥N text and ⌥⇧T trend line (⌥T keeps showing/hiding the Chart Trader). All are editable in Settings → Keyboard shortcuts.
- Drawings are shared by charts of the same instrument and saved automatically. New drawing kinds are stored with new numbers, so older drawing files read unchanged.
- Double-click to edit color, width/style, fill, text, extensions, Fibonacci levels, exact prices, visibility, period restriction and locking. Save the style as the default, duplicate or delete the drawing.
- Long/short position tools start with a 1:2 risk/reward stop and target. Entering quantity adds monetary estimates and marks target/stop touches. If both are touched in a bar, the conservative stop outcome is used.

**Draw with a gesture** (off by default; the gesture button on the drawing bar turns it on, and holding that button shows the guide): hold the mouse button still on empty chart space until the ring fills (about a third of a second), then trace a shape without releasing. A horizontal or vertical stroke makes that line, a diagonal a trend line (a ray when it reaches the right edge), a loop an ellipse, a box a rectangle, a stroke with a hook an arrow, a Z a Fibonacci retracement between the first and last strokes, and any other shape a path. A scribble over drawings erases the ones it crosses, except locked ones; the chip that appears offers **Undo**. After a shape, the chip switches the same stroke to related kinds or deletes it. Moving before the ring fills pans the chart as usual; Esc cancels a stroke.

Order lines, stop/target handles and buttons, the legend, indicators and existing drawings always take the press first: a gesture can only start on empty chart space, never changes orders and is cancelled by a right-click, lost focus or any confirmation dialog.

The extended position drawing can size from account risk using instrument point value, allowed quantity step, account/risk denomination and explicit estimated fees. Zero stop distance, insufficient risk or incompatible/unknown currency conversion produces no false tradable quantity. Monetary calculations support zero/negative prices where meaningful; percentage-of-entry labels are omitted when the entry is nonpositive. This is a sizing estimate, not an order, an FX service or a guarantee of executable liquidity.

**Anchored VWAP** and **Fixed-range volume / delta profile** use available trade/provenance inputs. Range endpoints are inclusive UTC instants; the profile uses bounded sparse decimal rows rather than an array spanning every possible price tick. POC/value-area and raw volume totals are separate from display scaling. Corrections, source/provider, period, row size and resolution invalidate results. Shared drawing parameters do not share mutable calculator/geometry state between charts; clones calculate independently. Volume-bar split fractions are consumed once, including repeated forming-bar updates. CPU calculation is retained; no GPU prerequisite or unmeasured Metal claim applies.

**Price alerts:** right-click a price to create a dotted horizontal alert. Horizontal lines/rays and cross lines can also enable **Alert when price touches**. Crossing triggers the optional alert sound and a status message, then disables the alert.

The title-bar bell or **Go → Alerts / delivery status** opens unified local price, indicator, strategy, risk and execution notifications. External webhook/Telegram destinations require explicit consent and secret-store configuration; local notification delivery does not depend on external success. Practice notifications do not egress. See [Alert delivery](./ajustes.md#local-and-external-alerts) for pending/retry/revoke semantics.

## Indicators

The **Indicators** window (⌘I) has three columns:

1. **Library:** search, then double-click or **Add to chart**. Right-click an item → **Add to favorites** to star it; the toolbar ★ adds favorites with one click.
2. **On chart:** show/hide, move between the price panel and a separate panel, or remove.
3. **Settings:** indicator properties and templates—save, apply and choose defaults for newly added instances.

Built-ins include Moving average, Bollinger Bands, VWAP with bands, Volume, Delta, Cumulative delta (CVD), Big Trades, Session volume profile, RSI and MACD. Legend buttons hide, configure or remove an indicator; double-clicking its name opens settings.

Custom C# indicators use **Code → Indicator editor** (⇧⌘E) or the library's code icon. Files in `~/Documents/Trading Hub/Indicadores` recompile on save and appear first under **My indicators**, marked with a star.

## Chart templates

The toolbar star or the context menu lets you:

- save this chart's style, footprint, colors, visibility and indicators—**not** instrument or period;
- apply a saved template;
- choose whether new tabs copy the active chart or use a template;
- delete a template.

## DOM and tape

DOM follows the active chart in the right panel. Default columns show buy orders, bid size, price, ask size, sell orders and session volume. Its header groups prices by ×1–×1000 ticks, toggles execution buttons, centers the ladder, opens settings or detaches it.

**DOM settings** controls columns (including session delta and volume traded at bid/ask), ordering, widths, row height, font, automatic centering, double-click trading, order-book highlight thresholds, bars and colors.

The **trade tape** combines fills from one aggressive order with the same millisecond and side. Wheel scrolling pauses following; click the header to return to the latest trades. See [DOM trading](./operar.md#trade-with-dom).

**Recorded liquidity heatmap** is a separate resting-book overlay. Displaying it does not enable depth recording. Source/venue, gaps, stale/partial books and I/O errors remain visible; the aggregate crypto DOM/heatmap is explicitly first-venue, not consolidated. See [Recorded depth and heatmap fidelity](./datos.md#recorded-depth-and-heatmap-fidelity).

## Volume profile

- **Session volume profile** supports session, weekly or visible-history profiles, volume/delta/bid-ask modes, POC, a default 70% value area and VAH/VAL lines.
- Footprint **Profile** columns display per-bar profiles.

## Replay

[Data → Replay](./datos.md#replay) opens selected local history in an isolated practice run. **Go → Random / blind practice** offers reproducible seeded local selection and optional instrument/date concealment, Reveal and a run-owned result/journal. Each run has its own account, fills, fees, portfolio and clock; it cannot route real orders or contaminate ordinary simulation.

## Strategies on charts

The robot icon adds a strategy from Backtesting; see [Strategies](./estrategias.md). It marks historical and new entries/exits and can remain visual-only or execute in an account. Configure or remove it from the legend.

**Chart history results** below its settings includes:

- net historical PnL using that market's backtest commission and slippage;
- a miniature equity/drawdown curve;
- trade count, win rate, profit factor, drawdown, average trade, average/largest win/loss, streaks, Sharpe, Sortino, recovery, exposure, long/short breakdown, commissions and bars per trade;
- the commission and slippage assumptions used.

These historical results are not a guarantee of live fills or future returns.
