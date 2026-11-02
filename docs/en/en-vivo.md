[← Documentation index](README.md)

# Live strategies

**Live** (⌘7) gathers runners launched here, from Backtesting or from charts. Tabs are Strategies, Market, Portfolios, Trades and Proposals. **New live strategy** and **Stop all** appear above. Always begin with simulation.

## Simulated, test and real accounts

- Simulated account is always available, filling against the book queue and market tape.
- Binance testnet, Tradovate demo and Rithmic practice do not involve real money.
- Real accounts require explicit permission in their [connection](conexiones.md#real-and-test-accounts) and are marked **REAL**.
- Manually launching/resuming a real strategy requires one confirmation; afterward its generated orders **do not ask individually**. Startup automatic resumption does not ask, so real-account automatic resumption is disabled by default.

## Launch a strategy

### From Backtesting or Live

1. Choose **Run live** in Backtest's live-launch card, or **New live strategy** in Live.
2. Choose strategy, instrument, account and period: M1/M2/M3/M5/M10/M15/M30/H1/H4 or its backtest period.
3. Review the last backtest's parameters.
4. Set market conditions: range/mixed/uptrend/downtrend and low/normal/high volatility relative to the prior month.
5. Set size multiplier, monetary daily-loss/drawdown limits (**0 = unlimited**) and whether hitting a limit also closes the position.
6. Assign a user portfolio or leave it in the automatic portfolio.
7. Choose **Start live**.

The backtest's zone/session start determines the daily-loss boundary. Before orders are allowed, the runner warms bars/indicators using loaded instrument history and seeds context from prior-month hourly data. Insufficient loaded bars can delay indicator readiness. The same strategy cannot run twice on the same instrument/account. Futures notices identify actual order quantity and contract.

### From a chart

The robot menu lists strategies; Backtesting also offers **Add to active chart**. Initially the strategy is **visual only**: historical signals use a no-cost overlay simulation, and new visual activity uses a separate private `Visual` simulated account, not your account.

In its legend/settings choose live execution, select an account and Apply. It then appears in Live with source **Chart**. Changing chart instrument pauses executing strategies; removing the overlay stops it but **does not automatically close its position**. See [Chart strategies](graficos.md#strategies-on-charts). Detailed historical statistics can separately apply backtest cost assumptions; distinguish those from the visual overlay.

## Size and smart contracts

- Size multiplies programmed quantities: ×1 is original size, ×0 blocks new exposure. The strategy still sees its own logical units; submissions reconcile scaled desired exposure with actual holdings, including after a size change.
- Quantities round down to the instrument step, but nonzero desired futures exposure has a one-unit minimum.
- **Smart contracts**, enabled in Settings, can use NQ→MNQ, ES→MES, YM→MYM, RTY→M2K, GC→MGC, CL→MCL and other supported pairs. ×0.5 of one NQ becomes five MNQ. Whole-mini exposure uses minis. An existing position stays in its contract until flat, then can restart in the appropriate contract with the same ledger.
- Tables, detail windows, menu bar and legend show actual planned quantity/contract, such as **5 MNQ · micro**.

Without an available micro, reducing a fractional multiplier below one contract may **not reduce actual exposure**. Use zero when no smaller executable position is appropriate.

## Risk limits and pause

| Limit | Behavior |
|---|---|
| Daily loss | When session-day realized plus open PnL reaches the loss limit, cancel pending entries and block further entries until the next session. |
| Maximum drawdown | Stop when the peak-to-current equity decline, including open PnL, reaches the limit. |

**Close position when a limit is reached** defaults on. Limit events create table/status notices. **Pause** instead cancels pending entries and blocks new exposure while continuing to manage an existing position toward exit.

## Strategies table and detail window

Summary cards show total/day PnL, running count, closed trades and pending changes. Rows show state (including context pause), size/contract, position, today's/net PnL, trades, PF, current drawdown and limits.

Open a row for:

- Pause/resume, close position, stop, stop-and-close, restart/resume on chart, show chart/trades, reset history (only while flat) and delete.
- Risk size/limits; changes apply from subsequent orders.
- Market-condition filters, applied immediately without restart, with a current eligibility preview.
- Parameters and **Apply and restart**; history warms again and existing positions remain managed. The stop loss and take profit stay working at the broker during the restart and the restarted strategy takes them over.
- Portfolio and Activity/log output.

Each runner maintains its own ledger even when sharing an account with other strategies or manual trades. On restart, a ledger position confirmed by an authoritative account snapshot remains managed: the strategy takes over stop loss and take profit left at the broker by recorded IDs rather than blindly duplicating them. **Missing, delayed or incomplete account data is unknown, not proof of a flat account.** Account reconciliation and order replay must complete before new exposure is allowed; an unavailable snapshot must not be treated as a successful close merely because a timeout elapsed. Reconcile conflicting or uncertain positions/orders at the broker before resuming.

**Stop all** is the emergency action: after confirmation, stop all live/chart runners, cancel their orders and request market closure of their positions. Check broker positions afterward; disconnected/rejected orders can prevent the requested closure.

## Incident checks

Stale market data, provider disconnects, rejected orders, missing execution acknowledgments or unexplained PnL divergence call for pausing/stopping automation as appropriate and reconciling actual positions, working orders and fills using the broker's own tools. Do not rely only on local charts or notifications; [live-ledger estimates](#trades) are not broker statements.

**Pause does not itself close existing positions.** A cancel/close request, including **Stop all**, does not prove broker execution during an outage. Pending acknowledgment is not confirmation. Check acknowledgments and actual broker state before retrying an order or resuming automation; do not submit an uncertain retry that could duplicate exposure.

Changing a Tradovate login or mode creates a new account context. Existing positions, working orders, unresolved snapshots and pending strategy requests keep the original context alive for confirmation/reduction; it cannot add new exposure. Reconcile at the original broker before discarding that context. A stopped strategy with pending close/cancel acknowledgment cannot be restarted, deleted or have its history reset until that transition settles.

Binance Spot native position quantity is the total base-asset wallet holding (**free + locked**), including holdings used by other quote pairs or manual activity. Strategy exit requests remain limited to the strategy ledger quantity, not the whole wallet. Locked assets may not be immediately sellable; Binance can reject an exit for insufficient free balance. Spot supplies no aggregate native cost basis here, so a zero average-price/PnL field is not a measured zero investment result; use the strategy ledger and broker records.

Sibling-exit cancellation/resizing is broker-emulated where native atomic OCO is unavailable. Close/cancel acknowledgments and competing fills can race; reduce-only protection is account-wide where supported, not a transaction isolating each strategy or manual holding. Do not treat fake-provider passes as proof of atomic protection at a real broker.


## Trades

View/filter/sort closed strategy trades by strategy, market, side, entry/exit/price, duration, quantity, points, PnL, MAE/MFE and captured favorable excursion. Sort newest/oldest/best/worst PnL or worst MAE. Clicking opens the trade on a chart.

**Important accounting limit:** live-strategy ledgers record fills using the current last market price, not authoritative broker execution prices, and **exclude commissions**. Do not use these estimates as brokerage statements. Accounts uses its separate account-fill history.

Ledger write-prefix recovery is **process-local**, not crash durability: it can recover the successfully written prefix known to the running process after an interrupted append. It does not prove persistence across process/power loss, replace backups or establish authoritative broker fills. After a crash or uncertain write, reconcile with the broker and retain the original records for diagnosis.


## Portfolios

- **Automatic · all live strategies** contains runners not assigned to a custom portfolio. Create a named portfolio with reference capital (default 25,000) and members; a strategy belongs to at most one custom portfolio.
- Inspect combined realized/open PnL, trades, PF, maximum drawdown, Sharpe when more than a week exists, average correlation, equity, member metrics and daily-PnL correlation matrix. Red indicates similar outcomes, green more offsetting outcomes.
- Add a new strategy, Start all (confirm real destinations), Stop all members (**leaving positions open**), Edit or Delete. Deleting a portfolio leaves runners active and returns them to automatic grouping.

## Risk agent

Claude can review recent results, drawdown versus history, daily-PnL volatility and correlation, proposing size and optionally daily-loss/drawdown changes.

| Setting | Default |
|---|---|
| Mode | Propose: approve each change. Automatic applies only when no member trades a real account. |
| Daily risk / portfolio drawdown budget | 2% / 10% of capital. |
| Per-strategy size bounds | ×0.25 to ×3; zero can pause new exposure. |
| Review interval | 60 minutes, minimum ten; needs three new trades, or runs after eight hours regardless. |
| Model / effort | Configured default Opus 5.5 / Extra; selectors also include Sonnet 5.5, Haiku 5.5 and Fable 5.1, with Low through Maximum effort. Availability depends on your CLI/account. |
| Instructions | Free-form preferences, for example a maximum ×2 crypto exposure. |

**Review now** requests an immediate review. Automatic reviews are capped at eight per day, twelve minutes per review and US$2 budget when the CLI supports that option. The agent has no tools: it receives supplied portfolio figures and returns bounded proposals. The card shows latest review, summary and accumulated estimated usage cost. See [Claude](asistente-claude.md).

AI proposals are not investment advice or guaranteed loss prevention. Review evidence and executable contract size.

## Proposals

Approve individually or **Approve all**. Real-account changes require confirmation; sizing changes affect subsequent orders.

Sources:

- **Rules:** with at least ten trades, examine recent-20 PF. Propose zero when PF <0.7, drawdown near worst and at least 15 recent trades; halve when PF <0.9 or drawdown is near worst; increase 25%, capped at ×3, when PF ≥1.5 with positive expectancy and low drawdown.
- **Ask Claude for analysis:** opens the Backtesting laboratory to inspect live exposure and propose sizing.
- A portfolio's risk agent.

## Market

For each active strategy market, show regime, 24-hour efficiency/change duration, prior-month volatility percentile, a 48-hour strip, member strategies and whether current filters permit entry. A final matrix highlights current conditions against each strategy's filter. Recalculation runs about every 30 seconds using prior-month hourly closes and the same context measurement as the filters.

## Closing the window and reopening

- **Menu bar/system tray:** enabled by default, window closure hides the app while strategies continue. The icon/menu/live panel shows strategies and positions; Windows also supports right-click. Quit with ⌘Q on macOS, Alt+F4 on Windows, or **Quit Trading Hub**; see [First steps](primeros-pasos.md#menu-bar-icon).
- **Quitting with open positions:** explicit Quit and closing the main window with background operation disabled use the same protection-aware flow. The dialog offers **Keep stops at broker and quit** (default), **Cancel stops and quit** (leaves positions open), **Close positions and quit** (requests market closure, never the default), and **Don't quit** (Esc). Retained stop loss/target orders are recorded for adoption on resume, but have no app-managed trailing/resizing or sibling-OCO management while the app is closed; the simulated account's orders end with the process. Without held live positions, cancellation of outstanding strategy orders can still require acknowledgment. Quit waits about ten seconds for requested closes/cancels and reports unresolved state before exit; acknowledging a warning is not proof the broker canceled or closed anything. Check broker positions/orders yourself. If manually closing a position before resume, also reconcile/cancel its retained exits to prevent an unintended reversing fill. Verify the broker's disconnected-order policy.
- **Language restart:** **Restart now** in Settings follows this same quit path. **Don't quit** cancels the restart without starting a second instance; the saved language preference remains for next launch. Restart is not a shortcut around pending orders or the live-position decision.
- **Automatic resumption:** shortly after startup, previously running strategies can restart with saved parameters, size and limits; paused strategies return paused. Real destinations wait for manual resume unless explicitly enabled for automatic resumption. A notice summarizes outcomes. A resumed strategy, on the panel or on its chart, takes over the stop loss and take profit kept at the broker instead of placing new ones.
- **Keep awake:** can prevent idle sleep while strategies or a risk review run and optionally support closed-lid operation. See [Settings](ajustes.md). Power/thermal behavior still requires real-hardware validation.

Native Windows lifecycle/tray/power behavior, actual real/demo broker protection/reconnect/fills and physical macOS sleep/lid/logout/quit scenarios remain separate release gates; source review or headless scenes do not certify them.

