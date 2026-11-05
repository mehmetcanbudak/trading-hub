[← Documentation index](README.md)

# Settings

**Settings** (⌘, on macOS; Ctrl+, on Windows) saves changes immediately. Provider-specific options are in [Connections](conexiones.md), chart options in [Charts](graficos.md), per-instrument brackets in [Trading](operar.md), and portfolio risk agents in [Live](en-vivo.md).

## General

| Setting | Default | Effect |
|---|---|---|
| Appearance | Dark | Dark/light across the app; also available through the title-bar sun/moon button. Per-chart color overrides remain intact. |
| Chart time zone | This Mac's time | Time axes, tape and sessions. Common zones include Buenos Aires, New York, Chicago, London, Madrid and UTC, followed by the complete list. Chart strategies also use this setting, except the Mac-local option falls back to UTC for them. |
| Alert sounds | On | Price alerts, Big Trades and indicator alerts. |

## Language and regional display

**Settings → General → Language** offers English (`en`), Spanish (`es`) and Simplified Chinese (`zh-CN`). The choice is saved immediately but applies when Trading Hub reopens; **Restart now** appears for a pending change. Existing English-fork `settings.json` without `Language` stays English. A fresh installation follows the OS: Spanish for `es`, Simplified Chinese for `zh`, English otherwise.

**Restart now** uses the same protection-aware quit flow as explicit Quit, rather than launching a second process first. **Don't quit** cancels the restart without changing running strategies; the saved language choice remains for the next opening. With live positions, choose deliberately whether to retain broker stops/targets, cancel them while leaving exposure open, or request market closure. Pending close/cancel acknowledgments are waited for up to about ten seconds, then unresolved state is reported before exit. A warning is not execution confirmation: check actual broker positions/orders. After that decision, owned provider/history/authoring processes and pending work are canceled/drained before any replacement launch and desktop exit. Closing to the tray does not perform this disposal. See [Live](en-vivo.md#closing-the-window-and-reopening).

Interface text, reports and Claude reply/report prose follow the selected language. Generated C# code, filenames, classes, parameter names, identifiers and code comments remain **English in every UI language**; existing user code is not renamed. Stored keys, provider payloads, numeric interchange formats and folder names remain stable. Display culture follows the selection (`en-US`, `es-AR`, `zh-CN`); invariant parsing/serialization contracts do not change. Platform fonts plus glyph fallback/shaping support CJK text in controls, charts and reports; native font/DPI appearance still needs platform visual validation. Arbitrary user/provider/system text and missing catalog entries may remain in their original language or English.

Saved live strategies awaiting native account-identity validation remain visible and retain their startup intent; they do not read/adopt another account's ledger while waiting. True Quit cancels and awaits pending history/compiler/identity starts before shared resources leave. A second user close during that drain cannot force the main window closed; close-to-tray still keeps services alive.

Saved chart strategy owner keys survive workspace save/restore independently of display/profile names. Native UID/authority validation cannot silently rebind that ledger; only an explicit strategy-account change may clear its owner anchor, leaving old native protection at its original account. Longbridge feed requests, reconnect/auth refresh and the owned loopback/browser callback also drain before shared HTTP/private-store disposal.


## Trading

| Setting | Default | Effect |
|---|---|---|
| Confirm real orders | On | Confirm manual Chart Trader/DOM/hotkey actions on real accounts. Combines with provider confirmation; bypass is possible only when both relevant options are off, where supported. Does not add per-order dialogs to automatic strategies. |
| Simulated account | — | Displays initial balance, edited in Accounts. Simulated fills use book queue and market tape. |

## Live strategies

| Setting | Default | Effect |
|---|---|---|
| Keep running in menu bar/system tray when window closes | On | Hiding the window leaves runners active. The icon opens a live strategies/positions panel (right-click also opens a Windows tray menu). Quit completely with ⌘Q on macOS, Alt+F4 on Windows, or Quit Trading Hub; explicit quit uses the protection-aware flow. |
| Resume strategies when Trading Hub opens | On | Restart previously running strategies after startup with saved parameters, size and limits; paused runners remain paused. Off means manual resume. |
| Also resume real accounts | Off | Recommended off: real-money runners await manual action. Available only when general resumption is enabled. |
| Automatically choose mini/micro contract | On | Fractional mini exposure can use an equivalent micro quantity, such as 1 NQ ×0.5 →5 MNQ. Whole exposure uses minis. Existing positions remain in their contract until flat. |

## Keep awake

| Setting | Default | Effect |
|---|---|---|
| Keep Mac/PC awake | On | Prevent idle system sleep while strategies/risk reviews run; display can sleep. No administrator password; released when no work remains. |
| Continue with lid closed | Off | When enabled and work is active, requests administrator permission, once per launch, for a helper that keeps the Mac running with its lid closed. Helper cleanup restores normal sleep when the app exits, including monitored unexpected exit. |
| Only when connected to power | On | Closed-lid operation stops on battery; available only with the preceding option enabled. |

The status text explains current assertions and closed-lid mode. **Do not put a running closed-lid Mac into a bag; cooling is restricted.**

Idle prevention uses macOS power assertions. Closed-lid support uses a privileged helper managing `pmset disablesleep` while the app and a data-directory notice exist, optionally only on AC power, returning it to 0 afterward. This changes a system power setting; review conflicts with other tools. Automated localization tests disable the sleep guard and do not validate real thermal/power behavior.

On Windows, idle prevention uses `SetThreadExecutionState`. The macOS closed-lid and charger controls are not applied: **With the lid closed** explains Windows Power Options and offers opening them. Lid closure follows the OS policy; Trading Hub cannot override it. Physical Windows power behavior and macOS helper/lid/thermal/lifecycle behavior are separate, uncompleted native release gates.


## Keyboard shortcuts

Bindings apply to the active chart in Trading or detached windows. While typing, only ⌃/⌥/⌘ or function-key bindings are eligible.

| Group | Actions / defaults |
|---|---|
| Trading | Buy/sell market, buy bid, sell ask, close, reverse, cancel all, close 25%/50%, breakeven, apply brackets, next/previous quick quantity. Intentionally unassigned. |
| Chart | 1m/5m/15m/1h/4h/daily (⌥1–⌥6), Candles/Footprint/Heikin Ashi, auto scale, latest price, horizontal line (⌥H), cursor; configurable rectangle zoom/reset/session/clock/help/quick-entry navigation. |
| Windows | One/four-chart layouts, next/previous tab (⌃Tab / ⌃⇧Tab), detach chart, toggle DOM, chart Chart Trader (⌥T), right panel. |

Click a binding and press keys. Esc cancels; Delete clears it. Reusing a combination unassigns its previous action and displays a notice. **Restore defaults** resets bindings. Real-order confirmations still apply to hotkey actions when enabled.

Committed text on a focused chart opens explicit symbol/period entry; numeric-symbol ambiguity remains visible. IME preedit, popup focus and stale searches cannot become trading hotkeys. These navigation controls add no keyboard order submission or new confirmation gestures. See [Charts](graficos.md#instrument-and-history).

## Data

| Setting | Effect |
|---|---|
| Record incoming live data | On by default; stores trades for fast reopening, replay and backtests. |
| Data folder | macOS: `~/Library/Application Support/Trading Hub`; Windows: `%APPDATA%\Trading Hub`. Open button. |
| Custom indicators (C#) | Documents known folder → `Trading Hub/Indicadores`, Open button. |
| Strategies (C#) | Documents known folder → `Trading Hub/Estrategias`, Open button. |

See [Data](datos.md) and [Privacy](privacidad.md).

## History and depth

Recent raw history, raw/compact memory limits, measured compact **1/5/15/60-minute** buckets and candle fallback are separate choices. Compact buckets remain aggregates, not individual large executions. The chart's automatic price-step suggestion is a bounded display-row grouping calculation, not a change to the instrument's exchange order tick.

**Record received depth** is off by default. Heatmap display and recorded-day reading do not enable recording. Per-instrument live/selected-history/queue byte budgets and disk/retention caps bound resources; dense books can consume the disk cap rapidly, so configured days are a maximum retention policy, not a promise of that much continuous coverage. Partial books, gaps, staleness, dropped/evicted coverage and I/O failures stay visible. See [Data fidelity](datos.md#raw-compact-and-candle-history) and [depth semantics](datos.md#recorded-depth-and-heatmap-fidelity).

**Candle maintenance** is an explicit opt-in with instrument selection/tracking, years, interval, per-cycle limit, Run and Cancel. It does not overlap itself, preserves finer recorded data and retains failed older month/resolution slots for retry. Status reports partial/error coverage rather than clearing an old failure merely because a later request succeeded. **Use saved instrument catalogs only** avoids discovery/ticker requests and labels saved time/provider/stale status.

## Network proxy

Choose **Use system proxy**, **Direct connection** or **Custom proxy**. Custom addresses contain scheme/host/port only; username/password are separate private-store values. Saving rejects invalid addresses or unconfirmed secret writes; ordinary settings/logs do not store proxy passwords.

Policy is captured when each app-owned HTTP/WebSocket consumer is constructed: public/private provider requests, discovery/history/archive downloads, OAuth/refresh/revocation, research and notification delivery use explicit factories. Active contexts are not changed through process-global network defaults, including while an old context owns a pending broker acknowledgment. Credential-bearing transports reject redirects. New consumers/reconnects use their captured policy; use protection-aware Quit/restart when a new policy must replace an active context. A failed explicit proxy does not authorize silent direct fallback.

Native Interactive Brokers TCP and external assistant/browser processes have their own networking; they are not advertised as app-proxy-covered.

## Local and external alerts

Open the title-bar bell or **Go → Alerts / delivery status** for consolidated price, indicator, strategy, risk and execution events. Local presentation remains independent of external delivery/disk success. Live compiled strategies can call `Strategy.Alert`; historical recalculation is not an external-notification replay. Practice events remain local.

Webhook/Telegram delivery is disabled until a destination and **explicit external-delivery consent** are saved. Review the actual channel payload preview before enabling. Keep Telegram bot tokens and webhook authorization in the private store; they are not ordinary settings/event-file fields. User strategy messages can contain private text: review what you choose to share rather than assuming every arbitrary message is safe.

Pending deliveries and stable event IDs survive a process restart. Bounded retries expose pending/sending/sent/failed/canceled outcomes, with explicit retry and revoke controls. A Telegram response must acknowledge success; an HTTP send or a remote acceptance lost before local acknowledgment may be delivered again. This is **at-least-once notification delivery**, not exactly-once execution or proof a human read a message. Revocation prevents further authorized attempts; notification errors never bypass or block order protection.

## Storage locations and safe relocation

The storage panel separately previews **application**, **market** and **script** roots. Existing `Trading Hub`, `Indicadores`, `Estrategias`, provider IDs and saved formats keep their identities; this is an opt-in location choice, not a branding/directory migration.

Preview canonicalizes and validates every selected source/target, then hashes a complete copy plan. Approval stages a pending change only. The protection-aware restart must first stop/drain the old writer; startup obtains an exclusive data-root lease before copy/hash/revalidation and atomic pointer publication. No root switches until the entire verified plan succeeds. Permission failures, interruptions, mismatches, alias retargets and cancellation retain the original authoritative roots and files; successful copy also leaves the old files intact.

All explicit `TRADINGHUB_DATA_DIR`, `TRADINGHUB_STRATEGIES_DIR`, `TRADINGHUB_INDICATORS_DIR`, `TRADINGHUB_BACKTESTS_DIR`, `TRADINGHUB_LIVE_DIR` and `TRADINGHUB_KEYCHAIN_FILE` overrides keep precedence. A published selected root that becomes unavailable fails startup closed instead of displaying a new empty account tree. Review the visible failure before retrying; do not delete the old tree as a workaround.

An explicit isolated data root or a valid saved script-root selection does not require an unused personal Documents folder. Default script paths are acquired and validated only when that default is actually needed; an unavailable or invalid **required** default remains a visible error, never a relative-path or alternate-root fallback.

## About

Shows Trading Hub and .NET versions and the original author's profile link.

## Storage

`settings.json` also stores window size, favorite instruments/periods, Trading side-panel state, DOM configuration, per-instrument brackets, default drawing styles, default chart/indicator templates and detached Claude chat position. If ordinary settings cannot be read, defaults may apply; keep a backup before manual edits. This is distinct from selected storage-root authority: an unavailable published storage root does **not** silently revert to a fresh default data tree.

Display localization does not rename persisted fields or migrate folder identities. Existing custom names, code and historical records remain yours.
