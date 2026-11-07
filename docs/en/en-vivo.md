[← Documentation index](./README.md)

# Live strategies

**Live** (⌘7) gathers runners launched here, from Backtesting or from charts. Tabs are Strategies, Market, Portfolios, Trades and Proposals. **New live strategy** and **Stop all** appear above. Always begin with simulation.

## Simulated, test and real accounts

- Simulated account is always available, filling against the book queue and market tape.
- Binance testnet, Tradovate demo and Rithmic practice do not involve real money.
- Real accounts require explicit permission in their [connection](./conexiones.md#real-and-test-accounts) and are marked **REAL**.
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

### Execution facts and saved ownership

Where the account exposes confirmed execution facts, the runner uses the actual execution price, UTC time and available account-currency fees, not the latest market mark or wall clock. Facts from all owned orders are merged chronologically, using a reliable account execution sequence for equal-time ties when supplied. Late/time corrections rebuild the ordered ledger and its trades, curve/peak/drawdown; fee-only corrections recompute financial results without inventing another execution. Missing execution/cost coverage holds new exposure and remains visible.

The ordinary legacy `SimBroker` path remains an **estimated mark/time and unknown-fee fallback**, not a confirmed-execution provider. Practice's deterministic configured fees are simulation facts, not exchange certification. A supported Bybit runner freezes provider/environment/authority/verified-native-UID scope before saved history/protection is read. Same-account credential rotation can preserve ownership; display names, an unverified UID or another account/origin cannot alias the ledger.

### From a chart

The robot menu lists strategies; Backtesting also offers **Add to active chart**. Initially the strategy is **visual only**: historical signals use a no-cost overlay simulation, and new visual activity uses a separate private `Visual` simulated account, not your account.

In its legend/settings choose live execution, select an account and Apply. It then appears in Live with source **Chart**. Changing chart instrument pauses executing strategies; removing the overlay stops it but **does not automatically close its position**. See [Chart strategies](./graficos.md#strategies-on-charts). Detailed historical statistics can separately apply backtest cost assumptions; distinguish those from the visual overlay.

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

A ledger persistence error also pauses **new or reversing exposure**. Existing protective/reducing orders, cancellation acknowledgments, closing and late-fill accounting continue. Repair storage and explicitly resume; resume first saves already pending fills and remains paused if recovery cannot complete. Restoring storage never silently resumes trading.

## Strategies table and detail window

Summary cards show total/day PnL, running count, closed trades and pending changes. Rows show state (including context pause), size/contract, position, today's/net PnL, trades, PF, current drawdown and limits.

Open a row for:

- Pause/resume, close position, stop, stop-and-close, restart/resume on chart, show chart/trades, reset history (only while flat) and delete.
- Risk size/limits; changes apply from subsequent orders.
- Market-condition filters, applied immediately without restart, with a current eligibility preview.
- Parameters and **Apply and restart**; history warms again and existing positions remain managed. The stop loss and take profit stay working at the broker during the restart and the restarted strategy takes them over.
- Portfolio and Activity/log output.

Each runner maintains its own ledger even when sharing an account with other strategies or manual trades. On restart, a ledger position confirmed by an authoritative account snapshot remains managed: the strategy takes over stop loss and take profit left at the broker by recorded IDs rather than blindly duplicating them. **Missing, delayed or incomplete account data is unknown, not proof of a flat account.** Account reconciliation and order replay must complete before new exposure is allowed; an unavailable snapshot must not be treated as a successful close merely because a timeout elapsed. Reconcile conflicting or uncertain positions/orders at the broker before resuming.

When owned quantities arrive before their execution prices, protection retains that owner's acknowledged or local provisional prices; it does not use an account-wide average blended with manual trades or other strategies. Reported owned execution prices can later establish the basis without inventing another fill or loosening an already trailed stop. Provisional protection is not confirmed financial accounting.

An untouched stop calculated locally from the market mark is provisional, even if its native placement acknowledges before execution prices arrive. The actual owned basis can replace that provisional price. This exception is tied to the original order, prepared intent and unchanged local/native price; explicit modification, trailing or a changed native/owner/child binding makes it an established bound. A stop adopted from the broker is not treated as a new local provisional calculation.

Unreadable history or a damaged append intent is shown as **History unavailable**, not a flat position or zero PnL. The strategy remains listed; affected totals are unavailable, and launch/restart, portfolio analysis and risk proposals cannot use fabricated figures. Retain the files and reconcile the ledger before restarting.

**Stop all** is the emergency action: after confirmation, stop all live/chart runners, cancel their orders and request market closure of their positions. Check broker positions afterward; disconnected/rejected orders can prevent the requested closure.

## Incident checks

Stale market data, provider disconnects, rejected orders, missing execution acknowledgments or unexplained PnL divergence call for pausing/stopping automation as appropriate and reconciling actual positions, working orders and fills using the broker's own tools. Do not rely only on local charts or notifications; [live-ledger estimates](#trades) are not broker statements.

**Pause does not itself close existing positions.** A cancel/close request, including **Stop all**, does not prove broker execution during an outage. Pending acknowledgment is not confirmation. Check acknowledgments and actual broker state before retrying an order or resuming automation; do not submit an uncertain retry that could duplicate exposure.

Changing a Tradovate login or mode creates a new account context. Existing positions, working orders, unresolved snapshots and pending strategy requests keep the original context alive for confirmation/reduction; it cannot add new exposure. Rithmic likewise retains the original trading session while any account consumer remains unresolved; only confirmed idle state permits a new trading login. Reconcile at the original broker before discarding that context. A stopped strategy with pending close/cancel acknowledgment cannot be restarted, deleted or have its history reset until that transition settles.

Binance Spot native position quantity is the total base-asset wallet holding (**free + locked**), including holdings used by other quote pairs or manual activity. Strategy exit requests remain limited to the strategy ledger quantity, not the whole wallet. Locked assets may not be immediately sellable; Binance can reject an exit for insufficient free balance. Spot supplies no aggregate native cost basis here, so a zero average-price/PnL field is not a measured zero investment result; use the strategy ledger and broker records.

The integrated Binance, Rithmic and Tradovate adapters do **not** submit native OCO groups. Their sibling-exit cancellation/resizing is client-coordinated. Owned orders remain tracked through delayed acceptance, cancellation and amendment acknowledgment; incomplete snapshots and disappearance are not fill proof. Broker-confirmed working values—not optimistic ticket values or command receipts—release replacement capacity. Explicit fill deltas cap protection while an independent position report catches up. These safeguards do not make competing fills atomic.
Binance Futures enforces its supported native `reduceOnly` payloads, but that is account-wide reduction, not strategy-isolated OCO; Spot, Rithmic and Tradovate simple exits here do not gain exchange enforcement from a local reducing marker. Keep the application and connection operational, and do not treat fake-provider passes as real-broker certification.

A triggered Binance conditional stop may become an acknowledged executing `MARKET` child. Its type transition, zero limit price or an identified fill arriving before the next native quantity snapshot is not amendment drift: the exact owned working child remains current while its remaining quantity and sibling capacity reconcile. Terminal state and unrelated reductions still require reconciliation; this does not make sibling fills atomic.

Execution-history totals never become native cumulative fills or terminal acknowledgments. If identified fills reveal an older executing-stop snapshot, Binance Futures makes one bounded read of that exact current native order before publishing the newer position. A still-incoherent view remains visibly nonready for protection changes; valid native replay still permits bounded manual reduction and explicit cancellation. Only returned native order facts update the working handle.

A replay-ready aggregate account position does not prove that a strategy's owned quantity disappeared. Native order fills and account PnL updates can arrive independently, and different owners can offset at the account. Startup and Close therefore do not insert a latest-price estimated fill merely because a native position snapshot does not cover the ledger. A close submits only the currently bounded quantity, waits for that exact request's completed native fill and quantity projection, then reevaluates any remaining exposure. An uncertain or still-working request is not resent; unreconciled native coverage remains pending. Simulator-reset estimates remain separate.

Native owned quantity enters the runner through immutable, owner-qualified cumulative captures. A mutable order handle cannot book a second quantity observation or release tracking for newly reported fills before their capture arrives. Demo/testnet accounts follow the same native rule; the simulator's legacy fallback remains separate.

Strategy sizing retains the original submitted strategy-unit/native-unit fraction across partial fills and price-only amendments. The amendment's remaining native quantity is not a new allocation ratio. Execution-first and durable-ledger-first delivery therefore book the same strategy units, and a later entry does not compensate for an incorrectly inflated virtual position.

For providers with scoped physical-order accounting, independent same-account sources can know different native snapshots. Protection compares its exact current executing child against the same owner/generation's canonical physical-order quantity, including independently captured units whose execution prices are still pending. The original physical ownership baseline remains distinct from a later adoption prefix. Fee updates therefore do not discard that child merely because another retained source observed a further fill first; unrelated owners, instruments, native identities and logical aliases cannot supply its quantity. This comparison never changes the broker handle's cumulative fill or establishes an execution price. Pending prices continue to block new risk without necessarily latching a user-visible Pause.

Bybit Close and Reverse retain the provider's durable cancellation/reconciliation workflow rather than immediately trading the old position size. Pending openings must settle through native cancellation acknowledgment and a reconciled position before the bounded action is sized. This is client coordination, not atomic native OCO or account-side certification.

An order submission with a lost/ambiguous reply is **unknown**, not automatically rejected. Exact preassigned client/tag identities reconcile it onto the original owned handle; cancellation intent survives delayed binding, and uncertain placement is not retried as new exposure. Prepared dispatches, accepted native ownership and execution checkpoints are persisted in account-scoped `Live/execution-state` records. Binance restart recovery checks exact native identities and the current working-child claim; a reused client ID cannot transfer an already accepted order to unrelated work. Recovery does not authorize another submission or prove exactly-once broker delivery. Reconcile actual broker orders and fills before resuming an unresolved destination; other provider interfaces still require their own recovery validation.


## Trades

View/filter/sort closed strategy trades by strategy, market, side, entry/exit/price, duration, quantity, points, PnL, MAE/MFE and captured favorable excursion. Sort newest/oldest/best/worst PnL or worst MAE. Clicking opens the trade on a chart.

Live accounting distinguishes **authoritative**, **estimated** legacy history and **pending** financial facts. An integrated execution-report source supplies actual execution prices and identified charges; order status, requested prices, notifications and current market marks are not substitutes. Previously saved last-market-price history remains an estimate rather than silently becoming confirmed accounting. Missing or nonconvertible charges are unknown, not zero. Funding, financing and a complete broker cash statement remain outside this ledger.

Incomplete prices, fees or current marks make affected confirmed totals unavailable. A stopped open position has no confirmed current open valuation; flat exposure has a known zero open value without a mark. A total containing an unavailable member is unavailable, not a subtotal that silently omits it; a known closed-trade count remains available independently. The laboratory API likewise keeps saved open valuations only in explicitly labelled estimate fields and reads position from the committed ledger, not a stale saved runner snapshot. Risk checks use the configured session: an unresolved charge on a closed prior-session trade does not alone block a new flat session's daily-loss allowance, while current-session charges, still-open exposure and all-history drawdown uncertainty remain conservative. Accounts has its separate account-fill history; reconcile against the broker's own records.

Corrected native reports revise the same execution rather than inventing another fill. Account trades/statistics and chart annotations refresh even when the execution count is unchanged. Completed, stopped strategy views and restored paused chart overlays use committed ledger corrections; running, closing and still-working runners retain their live accounting state.

Saved KVP string-ID executions retain their original account-scoped identifiers and sequence. Verified same-account protection adoption can correct an already-owned financial prefix, but that imported history does not acquire native ownership or change the successor's native quantity baseline. Old-prefix fee corrections, new post-adoption fills and genuine manual history remain distinct.

When execution costs are pending, chart overlays retain actual entry/exit coordinates but withhold numeric net-profit labels and profit-coloured trade links. The same rule applies to restored paused charts. Complete later reports restore revised monetary annotations without adding another execution.

Running strategies also apply same-ID quantity corrections without creating synthetic native quantity observations. A temporarily flat financial ledger is not permission to reopen while its exact owned native `MARKET` exit is still working: the strategy retains that child and holds new exposure until native terminal acknowledgment. Explicit cancellation and genuine reducing actions remain available.

Ledger appends use a checksummed write-ahead intent and disk flush before acknowledgment. Native files retain `Flush(true)`; supported Mac files additionally request full-drive flush, and supported macOS/Linux directory synchronization protects publication/deletion ordering. Unsupported stronger capabilities do not become a power-loss promise; synchronization errors remain visible and do not acknowledge pending work. A fresh process reads the committed JSONL prefix plus a published intent once; the next save completes that recorded byte range before adding another event. Identical legitimate fills remain distinct. Readers, writers and deletion share an interprocess lease; historical JSONL fields, ledger keys and paths stay compatible.

For owned execution facts, a shared, generation-bound admission record retains the complete fact and immutable receipt/revision binding before the credential-scoped receipt is saved. The existing ledger lease and write-ahead path recover pending admitted heads; an older retained receipt cannot outrank a later admission. A genuinely new correction can still lower a fee. These revisions order local durable admissions, not exchange events or wall-clock timestamps, and do not move native order ownership between credentials.

Recovered committed heads reach live and stopped ledger consumers through a nonrecursive notification pump, without invoking their callbacks under the ownership registry locks. History reset checks the recovered durable book and its generation under the same ledger lease used for admission and deletion. Newly committed, unconsumed or native-pending work prevents retirement; a stale locally flat view cannot erase reopened exposure. Before destructive retirement or interrupted-deletion recovery, existing owners receive a sticky admission fence. A storage failure, healthy provider synchronization or explicit Resume cannot reauthorize that old owner. A later subscription must prove the actual stored generation; only a successfully established fresh generation can admit new exposure.

Keep the entire Live directory together, including `execution-state`, `.wal`, `.id`, `.executions` authority directories and lock/deletion sidecars. **Do not manually delete an intent or downgrade to an older binary while recovery is pending.** Explicit Resume can finish existing pending work without inventing another execution; a damaged or inconsistent intent requires investigation, not a guessed flat balance. Existing malformed committed lines are preserved, skipped and counted; this is not proof that incomplete historical accounting is authoritative.

The guarantee covers **process-crash-consistent journal replay**, not machine power loss, filesystem rollback/tampering, events lost before a durable intent or exactly-once broker-event delivery. Successful OS synchronization calls are not hardware certification. Report provenance and completeness determine financial authority independently of storage recovery. Neither replaces backups or broker reconciliation.


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

**Review now** requests an immediate review. Automatic reviews are capped at eight per day, twelve minutes per review and US$2 budget when the CLI supports that option. The agent has no tools: it receives supplied portfolio figures and returns bounded proposals. The card shows latest review, summary and accumulated estimated usage cost. See [Claude](./asistente-claude.md).

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

- **Menu bar/system tray:** enabled by default, window closure hides the app while strategies continue. The icon/menu/live panel shows strategies and positions; Windows also supports right-click. Quit with ⌘Q on macOS, Alt+F4 on Windows, or **Quit Trading Hub**; see [First steps](./primeros-pasos.md#menu-bar-icon).
- **Quitting with open positions:** explicit Quit and closing the main window with background operation disabled use the same protection-aware flow. The dialog offers **Keep stops at broker and quit** (default), **Cancel stops and quit** (leaves positions open), **Close positions and quit** (requests market closure, never the default), and **Don't quit** (Esc). Retained stop loss/target orders are recorded for adoption on resume, but have no app-managed trailing/resizing or sibling-OCO management while the app is closed; the simulated account's orders end with the process. Without held live positions, cancellation of outstanding strategy orders can still require acknowledgment. Quit waits about ten seconds for requested closes/cancels and reports unresolved state before exit; acknowledging a warning is not proof the broker canceled or closed anything. Check broker positions/orders yourself. If manually closing a position before resume, also reconcile/cancel its retained exits to prevent an unintended reversing fill. Verify the broker's disconnected-order policy.
- **Language restart:** **Restart now** in Settings follows this same quit path. **Don't quit** cancels the restart without starting a second instance; the saved language preference remains for next launch. Restart is not a shortcut around pending orders or the live-position decision.
- **Automatic resumption:** shortly after startup, previously running strategies can restart with saved parameters, size and limits; paused strategies return paused. Real destinations wait for manual resume unless explicitly enabled for automatic resumption. A notice summarizes outcomes. A resumed strategy, on the panel or on its chart, takes over the stop loss and take profit kept at the broker instead of placing new ones.
- **Keep awake:** can prevent idle sleep while strategies or a risk review run and optionally support closed-lid operation. See [Settings](./ajustes.md). Power/thermal behavior still requires real-hardware validation.

Native Windows lifecycle/tray/power behavior, actual real/demo broker protection/reconnect/fills and physical macOS sleep/lid/logout/quit scenarios remain separate release gates; source review or headless scenes do not certify them.

