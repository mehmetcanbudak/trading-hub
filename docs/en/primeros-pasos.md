[← Documentation index](README.md)

# First steps

A tour of the main window. Start with [Installation](instalacion.md) if the app is not installed yet.

## First launch

Trading opens with a five-minute `SIM-BTC` footprint chart, Big Trades and Delta indicators. This comes from **Simulator**, an offline practice market.

Orders initially use the **simulated account**. No real money is involved until you explicitly enable it for an appropriate profile in [Connections](conexiones.md).

| Built-in connection | Initially | Provides |
|---|---|---|
| Simulator | Enabled | Simulated SIM-BTC, SIM-ETH, SIM-ES and SIM-NQ. |
| Binance Futures | Enabled | Public crypto data without an account. |
| Binance Spot | Enabled | Public crypto data without an account. |
| Bybit | Enabled | Public USDT linear-perpetual data. |
| Rithmic | Disabled | CME futures; provider credentials required. |

## Main window

### Sections in the title bar

Navigation is integrated into the macOS title bar next to the window controls.

| Section | Shortcut | Purpose |
|---|---|---|
| Trading | ⌘1 | Charts, DOM, tape and Chart Trader. |
| Accounts | ⌘5 | Balance, positions, orders, fills, trades and statistics. |
| Backtesting | ⌘2 | C# strategies, backtests, optimization and Claude chat. |
| Live | ⌘7 | Running strategies, portfolios and **Stop all**. |
| Data | ⌘3 | Stored history, downloads, imports, Databento and replay. |
| Connections | ⌘4 | Markets, brokers and accounts. |
| Settings | ⌘, | Appearance, time zone, hotkeys, sleep prevention and folders. |

Below 1360 pixels wide, navigation shows icons with names in tooltips. Drag the title bar to move the window; double-click to maximize.

### Right side of the title bar

- The simulated account's daily PnL and balance; click to open Accounts.
- A sun/moon control for light and dark appearance.
- The active chart's connection-status pill. Click for **Reconnect now** and **Connections…**.

| Status | Meaning |
|---|---|
| Connected | Data is arriving. |
| Connecting / reconnecting, attempt N | Establishing the connection; missing data is backfilled after interruptions where the provider supports it. |
| No data for N minutes | Connected but no recent events have arrived. |
| Market closed; opening day/time | Futures are outside trading hours, not necessarily disconnected. |
| Error / disconnected | Check the profile in Connections. |

After system sleep, the app reconnects and attempts to fill the missing history.

### Status bar

The bottom-left area shows the latest order, history, alert or error notice. Hover to read the full message. On the right is the active chart summary—trade/bar counts and starting time—or history-loading progress.

## Trading section

![Trading with charts, Chart Trader, DOM and tape](img/trading.png)

1. **Chart toolbar:** instrument, history, period, style, indicators, footprint, colors, strategies, templates, layout, panels, detach and new tab. See [Charts](graficos.md).
2. **Tabs:** visible when more than one exists; each holds one to six charts.
3. **Chart:** drawing tools on the left and its own collapsible Chart Trader on the right.
4. **Right panel:** Chart Trader, DOM and tape. Drag the edge for width and dividers for height. Each section can be hidden or detached.
5. **Replay controls:** appear below when the active chart is a replay. See [Data](datos.md#replay).

For orders, see [Trading](operar.md).

## Simulated account

- Starts at 10,000. Change this in **Accounts → Initial balance**.
- Works with Simulator or real market data from supported connections.
- Market orders fill at the best available bid/ask. Limits fill when price trades through, or when subsequent volume at the limit exceeds the queue ahead at submission. Stops trigger on a trade at the trigger price and then fill at market.
- Recorded balance/history survives app restarts. Use **Reset simulated account** in Accounts to start over; close positions and cancel orders first.

## Open a chart

1. Click the instrument selector, the first chart-toolbar button, or press ⌘K.
2. Choose a connection, or **Favorites**.
3. Search for BTC, ETH, NQ, etc.; move through results with arrow keys.
4. Enter or double-click opens in the active chart; ⌘Enter opens a new tab.
5. Use a row's star to mark it as a favorite.

Opening a chart loads the connection's configured recent history. Use the history menu for more. **⌘T** or **+** creates a tab copied from the active chart unless a default new-tab template is configured. **⌘W** closes the active chart; at least one tab remains.

Add account-backed markets in [Connections](conexiones.md).

## macOS application menus

| Menu | Actions |
|---|---|
| File | New chart (⌘T), Close chart (⌘W), Find instrument (⌘K), Save workspace (⌘S), Open data folder. |
| Chart | Indicators (⌘I), Edit footprint (⇧⌘F), Auto scale (⌥⌘A), Latest price (⌘End), Layout, Detach chart (⇧⌘D), Panels, DOM settings. |
| Go | All seven sections and their shortcuts. |
| Code | Indicator editor (⇧⌘E), Open indicators folder, Recompile indicators, Strategy backtesting, Live strategies, Open strategies folder. |

The workspace—tabs, charts, layouts and panels—is also saved automatically when closing and restored at startup.

## Menu-bar icon

The macOS menu-bar icon offers:

- active-chart connection status;
- live strategies with size, position and PnL;
- open positions;
- **Live panel**, **Open Trading Hub**, and **Quit Trading Hub**.

The live panel updates strategies and positions in real time and offers the same open/quit actions.

**Closing is not quitting.** By default the red window button hides the window while strategies keep running. This is controlled by **Settings → Live strategies → Keep running in menu bar when window closes**. Disabling it removes the icon and makes window closure quit the application. Use **⌘Q** or **Quit Trading Hub** to exit completely. Clicking the Dock icon reopens a hidden window.

## Keyboard shortcuts

**Settings → Keyboard shortcuts** contains configurable shortcuts for Trading and detached charts. Click a binding and press the desired keys; Esc cancels, Delete clears, and **Restore defaults** restores the original bindings.

| Default | Action |
|---|---|
| ⌥1–⌥6 | 1-minute, 5-minute, 15-minute, 1-hour, 4-hour and daily periods. |
| ⌥H | Draw a horizontal line. |
| ⌥T | Toggle the chart's Chart Trader. |
| ⌃Tab / ⌃⇧Tab | Next / previous tab. |

Order actions—buy, sell, close, reverse, cancel, partial exits, breakeven and others—are intentionally **unassigned**. See [Trading shortcuts](operar.md#trading-shortcuts).

| Chart key | Action |
|---|---|
| Esc | Return to cursor, cancel an order drag or clear a highlighted trade. |
| Delete / Backspace | Delete selected drawings. |
| Home / End | Return to the latest data with automatic scaling. |
| ← / → | Pan the chart. |
| + / − | Increase / decrease bar width. |

## Settings worth reviewing

- **General:** theme, chart time zone and alert sounds.
- **Trading:** confirm real orders; see [Safety](operar.md#safety).
- **Live strategies:** menu-bar behavior, startup resumption and automatic mini/micro contract selection.
- **Keep awake:** prevent sleep while strategies run; optional closed-lid behavior.
- **Data:** record incoming live data and open local folders.

## Next steps

- [Connections](conexiones.md): markets and brokers.
- [Charts](graficos.md): periods, footprint, drawings, indicators and templates.
- [Trading](operar.md): orders, brackets and account history.
- [Data](datos.md): stored history, downloads, imports, Databento and replay.
