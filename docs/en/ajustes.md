[← Documentation index](README.md)

# Settings

**Settings** (⌘,) saves changes immediately. Provider-specific options are in [Connections](conexiones.md), chart options in [Charts](graficos.md), per-instrument brackets in [Trading](operar.md), and portfolio risk agents in [Live](en-vivo.md).

## General

| Setting | Default | Effect |
|---|---|---|
| Appearance | Dark | Dark/light across the app; also available through the title-bar sun/moon button. Per-chart color overrides remain intact. |
| Chart time zone | This Mac's time | Time axes, tape and sessions. Common zones include Buenos Aires, New York, Chicago, London, Madrid and UTC, followed by the complete list. Chart strategies also use this setting, except the Mac-local option falls back to UTC for them. |
| Alert sounds | On | Price alerts, Big Trades and indicator alerts. |

## Trading

| Setting | Default | Effect |
|---|---|---|
| Confirm real orders | On | Confirm manual Chart Trader/DOM/hotkey actions on real accounts. Combines with provider confirmation; bypass is possible only when both relevant options are off, where supported. Does not add per-order dialogs to automatic strategies. |
| Simulated account | — | Displays initial balance, edited in Accounts. Simulated fills use book queue and market tape. |

## Live strategies

| Setting | Default | Effect |
|---|---|---|
| Keep running in menu bar when window closes | On | Hiding the window leaves runners active. The icon opens a live strategies/positions panel. Quit completely with ⌘Q or Quit Trading Hub. |
| Resume strategies when Trading Hub opens | On | Restart previously running strategies after startup with saved parameters, size and limits; paused runners remain paused. Off means manual resume. |
| Also resume real accounts | Off | Recommended off: real-money runners await manual action. Available only when general resumption is enabled. |
| Automatically choose mini/micro contract | On | Fractional mini exposure can use an equivalent micro quantity, such as 1 NQ ×0.5 →5 MNQ. Whole exposure uses minis. Existing positions remain in their contract until flat. |

## Keep awake

| Setting | Default | Effect |
|---|---|---|
| Keep Mac awake | On | Prevent idle system sleep while strategies/risk reviews run; display can sleep. No administrator password; released when no work remains. |
| Continue with lid closed | Off | When enabled and work is active, requests administrator permission, once per launch, for a helper that keeps the Mac running with its lid closed. Helper cleanup restores normal sleep when the app exits, including monitored unexpected exit. |
| Only when connected to power | On | Closed-lid operation stops on battery; available only with the preceding option enabled. |

The status text explains current assertions and closed-lid mode. **Do not put a running closed-lid Mac into a bag; cooling is restricted.**

Idle prevention uses macOS power assertions. Closed-lid support uses a privileged helper managing `pmset disablesleep` while the app and a data-directory notice exist, optionally only on AC power, returning it to 0 afterward. This changes a system power setting; review conflicts with other tools. Automated localization tests disable the sleep guard and do not validate real thermal/power behavior.

## Keyboard shortcuts

Bindings apply to the active chart in Trading or detached windows. While typing, only ⌃/⌥/⌘ or function-key bindings are eligible.

| Group | Actions / defaults |
|---|---|
| Trading | Buy/sell market, buy bid, sell ask, close, reverse, cancel all, close 25%/50%, breakeven, apply brackets, next/previous quick quantity. Intentionally unassigned. |
| Chart | 1m/5m/15m/1h/4h/daily (⌥1–⌥6), Candles/Footprint/Heikin Ashi, auto scale, latest price, horizontal line (⌥H), cursor. |
| Windows | One/four-chart layouts, next/previous tab (⌃Tab / ⌃⇧Tab), detach chart, toggle DOM, chart Chart Trader (⌥T), right panel. |

Click a binding and press keys. Esc cancels; Delete clears it. Reusing a combination unassigns its previous action and displays a notice. **Restore defaults** resets bindings. Real-order confirmations still apply to hotkey actions when enabled.

## Data

| Setting | Effect |
|---|---|
| Record incoming live data | On by default; stores trades for fast reopening, replay and backtests. |
| Data folder | `~/Library/Application Support/Trading Hub`, Open button. |
| Custom indicators (C#) | `~/Documents/Trading Hub/Indicadores`, Open button. |
| Strategies (C#) | `~/Documents/Trading Hub/Estrategias`, Open button. |

See [Data](datos.md) and [Privacy](privacidad.md).

## About

Shows Trading Hub and .NET versions and the original author's profile link.

## Storage

`settings.json` also stores window size, favorite instruments/periods, Trading side-panel state, DOM configuration, per-instrument brackets, default drawing styles, default chart/indicator templates and detached Claude chat position. If it cannot be read, the app falls back to defaults; keep a backup before manual edits.

Display localization does not rename persisted fields or migrate folder identities. Existing custom names, code and historical records remain yours.
