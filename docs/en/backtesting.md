[← Documentation index](./README.md)

# Backtesting

**Backtesting** (⌘2) contains Code, Backtest, Optimization, Walk-forward, History, Portfolio and Reports. Select the strategy above the tabs. See [Strategies](./estrategias.md) for code and [Live](./en-vivo.md) for execution.

## Select data

Backtests iterate stored trades from recording, downloads or [imports](./datos.md). Without stored data the list is empty.

- **Instrument (stored data)** displays symbol, source, day count, range and size.
- Futures with Databento history offer combined Rithmic + Databento (Rithmic priority; Databento fills empty minutes), Only Rithmic, or Databento. Tradovate has equivalent combined/individual choices.
- From/To includes the entire final day. Quick ranges are one week, one/three/six months, one year and All, counted backward from the latest stored day.
- Changing instrument selects its latest seven stored days and resets percentage commission to 0.04% for Binance/Bybit crypto, 0 elsewhere.

Bar period accepts case-insensitive codes and chart abbreviations. Existing Spanish aliases remain compatible: `diario`, `semanal`, `mensual`.

| Code | Bars |
|---|---|
| S30, M5, H1, D1/Daily, W1/Weekly, Mo1/Monthly | Time-based. |
| Tick500 | 500 trades. |
| Volume1000 | 1,000 volume units. |
| Range20 | 20-tick range. |
| Delta300 | Absolute delta reaches 300. |

Quick buttons include M1, M5, M15, H1, H4, D1, Tick500 and Range20. **Renko is unavailable in backtests.**

### Source timing and fidelity

Price location and **availability** are separate. Interval facts are scheduled when their source window becomes available, not backdated to its opening. Unknown/inferred legacy bar availability requires a declared-timing reimport before a causal run; the engine rejects it before strategy callbacks. Time bars must contain each complete source window with an aligned phase. Non-time bars cannot invent an observed trade sequence from interval summaries.

Measured compact history conserves exact grouped volume/side totals and signed price information, but is not individual observed trades, raw intrabar extrema or an observed MaxTrade. Synthetic OHLC/quotes and partial coverage remain labelled. Even where accepted for analysis, input counts/paths can be estimates; a newly learned interval-close signal cannot execute against an earlier anchor in that same source window. Existing raw-trade sequence rules are retained.

Non-IB cache reuse validates content from the exact decoded files or committed recorder prefix, including each combined-source constituent and missing day. Same-length corrections, preserved timestamps and partial deletions therefore cannot justify reusing old research input. Raw and verified archived representations of the same content share an identity. IB retains its separate snapshot authority.

## Execution costs and rules

| Setting | Default | Meaning |
|---|---|---|
| Initial capital | 10,000 | Base for percentage return, drawdown and Sharpe. |
| Commission % per side | 0.04% crypto; 0 otherwise | Percentage of quantity × price × point value at entry and exit. |
| Fixed commission per unit | 0 | Per contract/unit per side, added to percentage commission; appropriate for futures. |
| Slippage | 1 tick | Adverse movement on market/stop fills. |
| Limit fills | Trade-through only | Alternative touch-fill mode is optimistic. |
| Session zone/start | UTC / 0 | LocalTime, TimeBetween, session VWAP/CVD/reset and hourly statistics. |

Zones include UTC, Buenos Aires, New York, Chicago/CME and London. Parameters separate strategy inputs from position management. Settings are saved per strategy; chart strategies reuse parameters, and live runners also inherit period/session configuration.

### What a backtest simulates

| Order | Fill |
|---|---|
| Market | Next trade, plus adverse slippage; an OnBar order uses the next bar's first trade. |
| Limit | Limit price after trade-through, or touch in optimistic mode. Already marketable limits fill like market orders, capped by the limit. |
| Stop | Triggering trade price plus adverse slippage; gaps can worsen execution. |
| Automatic stop/target | Stop/limit behavior with OCO cancellation. |

An order cannot fill on its originating trade. At range end, open exposure closes at the last price with **End of backtest**. PnL is price difference × quantity × point value in instrument currency (NQ: 20 per point). Context is seeded from available prior-month history. This simulation is not an exchange queue or a guarantee of live fills.

## Results

New runs retain the source of their captured compiled generation. Cards show net PnL/return, profit factor, win rate, maximum drawdown/recovery, Sharpe/Sortino and expectancy, plus equity. Marked walk-forward results use the different contribution units and coverage rules below.

- **Trades:** number, side, entry/exit and prices (Mac-local time), quantity, net PnL, MAE/MFE, bars held and exit-order reason.
- **Metrics:** 28 values covering commission, return, average trade, streaks, time exposed, long/short results, simulated ticks and bars.
- **By market condition**, **Monte Carlo**, and **Log**; log entries use UTC and are capped at 5,000.

| Metric | Definition |
|---|---|
| Profit factor | Gross profit / gross loss; infinity without losses. |
| Win rate | Positive-net trades / all trades. |
| Expectancy | Win probability × average win + loss probability × average loss. |
| Maximum drawdown | Largest equity-peak decline, including open PnL at bar close; percentage relative to that peak. |
| Recovery factor | Net PnL / maximum drawdown. |
| Sharpe / Sortino | Mean daily returns / total or downside deviation, annualized by √252 or √365 for crypto. |

### By market condition

Each trade stores its entry regime—range, mixed, uptrend, downtrend—and volatility category: low ≤33rd percentile, normal between 33 and 67, high ≥67, relative to the prior month. The table shows count, net, PF, win rate and average; small samples are flagged.

Select conditions and **Rerun only in these conditions** to run a genuinely new context-filtered backtest, not merely remove rows from the old result. Decisions use information available at the time and exits still pass. Results are compared above. Selecting a filter after inspecting these results fits it to those data: validate on another period or walk-forward.

### Monte Carlo

The simulation shuffles trades 1,000 times, reporting median, 95th/99th-percentile and worst observed drawdown, plus risk of ruin (drawdown ≥50% of capital). Forty paths are plotted. Reordering historical trades does not model every possible future loss or changing market regime.

## History

Filter All, Backtests, Optimizations, Walk-forward or Favorites. Each run includes metrics, equity, parameters, settings and an editable note. Actions favorite/delete, view exact code, generate a report, or use parameters and rerun. Files live under Application Support/Trading Hub/Backtests, grouped by strategy identity.

New saved runs also retain input quality, resolution, source identity, correction revision, known availability, warnings and captured content identity. History, laboratory output, portfolio components and reports expose those facts. Missing legacy evidence stays unknown. A digest identifies the input used but does not archive a complete market-data copy or guarantee that future data remains available.

## Optimization

1. Enable parameter min/max/step ranges; Optimize attributes preselect them. A warning appears above 3,000 combinations; more than 20,000 is refused.
2. Rank by PnL/drawdown (default), net PnL, PF (capped at 100 for ranking), Sharpe, Sortino, recovery, expectancy or win rate.
3. Set minimum trades (default 10). Walk-forward eligibility counts actual closing fills, not boundary valuations. Smaller samples receive no fitness score.
4. Run the grid using Backtest data/costs; work is parallelized across available cores minus one.

**Timeframe optimization:** enable the bar-period input and choose M1, M5, M15, M30, H1, H4 and/or D1. Each parameter combination is tested on each selected period, loading ticks once and rebuilding bars per period. Rows and **Use in Backtest** include the chosen period. Walk-forward treats period as another parameter; trial-count diagnostics include period alternatives.

The table shows up to 500 ranked rows. Double-click / **Use in Backtest** copies settings and reruns. The top row may be a lucky peak, so validate it. Optimization and walk-forward runs are saved in History.

Saved optimization bounds retained rows to 1,000 without modifying the live result. Profitability and parameter marginals describe the complete measured population, not that ranked prefix. The exact recommended combination is saved separately even when it lies outside retained rows. A truncated legacy run without full-population evidence or an exact recommendation reports that information as unavailable rather than using row zero.

## Walk-forward

Select parameters using the completed in-sample prefix, then measure their next out-of-sample window. The combined result is a **warm mark-to-market research splice, not an executable portfolio**. Each configuration keeps its own prior strategy state, positions and capital. Switching the selected configuration does not transfer positions, resize from stitched capital or charge deployment turnover.

- Defaults: 14 in-sample days and seven out-of-sample days. Rolling windows keep fixed training length; anchored windows start at the same initial day and grow. The UI previews the number of windows.
- Reuses Optimization parameter/ranking/minimum-trade settings and Backtest data/costs.
- Search: Automatic (grid up to 300 combinations, Bayesian above), Full grid, or Bayesian; Bayesian uses 80 trials per window.

Results disclose completed-window coverage, accounting and attribution policies, out-of-sample net/count/PF, WF efficiency, profitable-window percentage, drawdown, overfitting, CPCV paths and per-window dates, inputs and source/stitched equity. A later failure can leave a valid completed prefix. Unavailable windows are not zero-return windows. With no completed out-of-sample window, monetary summaries remain unavailable.

### Implementation details

1. **One warmed run per combination.** Capture authoritative accounting state before processing an event whose availability reaches a cutoff, and before terminal flush/forced-close callbacks. Training uses only those prefix states and actual fills. A trade's later exit cannot rewrite an earlier training result.
2. **Tick compression:** adjacent same-price, same-side trades in the same second are combined. Time/range OHLC, volume, delta, footprint and fill prices remain equivalent, but Bar.Trades counts can differ. Used only for time/range optimization/walk-forward when OnTick is not overridden.
3. **Grid / Bayesian TPE:** grid evaluates all points. TPE starts with a Latin hypercube of 8–30 points, roughly one quarter of its budget, then models the top 20% versus the rest and proposes batches maximizing `l(x)/g(x)`.
4. **Plateau selection:** score a point using `(its fitness + one-grid-step neighbors' fitness + median) / (neighbor count + 2)`. The median shrinks sparse neighborhoods; an invalid neighbor scores below the worst valid result. Prefer robust regions over isolated peaks.
5. **Window accounting.** Net PnL is closing marked equity minus opening marked equity. Closing-fill contributions and a final boundary valuation conserve that change, including carried open PnL, proportional fees and decimal residuals. The result retains the original entry and measured boundaries. It does not replace the strategy's execution basis. PF, win rate and expectancy describe these contributions, not independent completed trades.
6. **Risk and efficiency.** Window and CPCV risk retain the original decimal intraday equity path rather than hourly floating-point samples. Daily returns use causal availability. Efficiency compares out-of-sample daily profit with in-sample daily profit. It is unavailable with missing evidence and zero when completed training did not profit.
7. **PBO / CSCV.** Split daily results into up to ten blocks, choose half as training in every combination, select its highest-Sharpe candidate, and rank it in the other half. With rank `r` (1 = worst of N), `ω = r/(N+1)`; lower-half selection means `ln(ω/(1−ω)) ≤ 0`. PBO is that proportion. Selection loss frequency is separate. Requires at least four configurations and 20 days.
8. **Retrospective CPCV.** Purged grouped validation with embargo is not a forward-only executable handoff. Repeated positions and boundary valuations do not establish additional independent trades. Path summaries use only available eligible paths and disclose available versus total coverage. No eligible path means unavailable money, profitable share and median, not zero-valued substitutes.

## Deflated Sharpe and validation

Trying many variants increases the chance that the best looks profitable by luck. Deflated Sharpe estimates confidence after accounting for the trials, using trade-level out-of-sample SR (mean / deviation), skew γ3, kurtosis γ4 (3 for normal returns) and n trades:

```text
PSR(SR*) = Φ((SR − SR*) · √(n − 1) / √(1 − γ3·SR + (γ4 − 1)/4 · SR²))
SR* = √(max(V, 1/n)) · E[max Z_N]
E[max Z_N] ≈ (1 − γ)·Φ⁻¹(1 − 1/N) + γ·Φ⁻¹(1 − 1/(N·e)), γ ≈ 0.5772
```

N is the configuration count, V their Sharpe variance and Φ the normal CDF. SR* approximates the lucky best of N no-edge strategies.

`ValidationGate` applies fixed rules to out-of-sample results:

| Check | Pass | Uncertain | Fail |
|---|---|---|---|
| Sample | ≥30 trades | | Fewer. |
| PnL | Net >0 | | Net ≤0. |
| Profit factor | ≥1.2 | ≥1.0 | <1.0. |
| WF efficiency | ≥0.5 | ≥0.3 | <0.3. |
| Profitable windows | ≥60% | ≥40% | <40%. |
| Window count | ≥4 | Fewer | |
| Parameter dispersion / range width | ≤0.35 | Higher | |
| Profitable neighbors | ≥50% | Lower | |
| Profitable combinations | ≥25% | Lower | |
| Selection loses in held-out half | <20% | <50% | ≥50%. |
| CPCV paths | ≥70% profitable, positive median | Fewer | Median ≤0. |
| Deflated Sharpe | ≥90% | ≥50% | <50%. |
| Edge decay, ≥30 trades | Sustained | Last third below half the first, or t ≤−1.5 | First third profitable, last loses, t ≤−2. |
| Regimes, groups ≥8 trades and ≥15% | Profitable in all | Loses in some | |

Any fail produces Fail; otherwise any uncertain check produces Uncertain, else Pass. Profitable out-of-sample results failing only sample-dependent checks can report **Insufficient sample**, estimating additional trades needed under the same edge, capped at eight times the sample.

Marked walk-forward contributions do not provide independent completed-trade confidence. Trade-only deflated-Sharpe, Monte Carlo, decay, duration and regime diagnostics remain unavailable when their assumptions are not satisfied. Actual closing fills can establish selection eligibility without turning boundary marks into trades.

The Walk-forward UI shows efficiency, profitable windows, overfitting and CPCV. The full deflated-Sharpe/decay/regime gate is a tested library facility, **not a claim that every UI launch automatically enforces it**. See [Development](./desarrollo.md).

## Portfolio

Add saved runs across strategies/instruments. Choose Equal, Equal risk (inverse daily-PnL volatility), or Manual weights and capital. Inspect combined metrics/equity, daily-PnL correlations (below 0.5 is a diversification heuristic) and drawdown reduction versus summed individual drawdowns.

Marked walk-forward components require saved available coverage. Combination uses their common contiguous out-of-sample interval. Missing coverage, internal unavailable gaps and disjoint intervals are refused rather than filled with zero returns. A partial component retains its completed prefix. The combined research result is still not an executable portfolio.

**Suggest combination** searches recent runs for up to five components with correlation below 0.5 and improved Sharpe. **Report** generates a portfolio report. Historical correlation and optimized combinations can change or overfit.

## Reports

History and Portfolio **Report** actions, or Claude's laboratory, create Markdown reports with metrics, tables and charts in the selected English, Spanish or Simplified Chinese interface language. Claude can add analysis and a verdict; generated C# remains English. Files live in the Documents known folder under `Trading Hub/Estrategias/Reportes` (`~/Documents/Trading Hub/Estrategias/Reportes` on a standard Mac). The report reader's PDF/HTML export opens a browser for printing or saving PDF. Existing reports are not rewritten. See [Claude](./asistente-claude.md).

## Additional data and assistant fidelity

IB imported minute bars remain synthetic/non-observed through backtests and footprint aggregation; real recorded minutes take precedence. They cannot establish observed intrabar fills, true aggressor footprint or equivalence to tick replay. OI has finite five-minute/latest-30-day history and ten-minute freshness; unavailable cutoffs are not zero. Retrospective closed-confirmed swing anchors are not contemporaneous forecasts. See [Data](./datos.md#interactive-brokers-bars-and-offline-history) and [studies](./indicadores.md#selected-additional-native-studies).

Optional Codex strategy chat uses a separate engine-bound history; Claude remains the default and risk-agent engine. The existing Laboratory tools produce runs/reports/proposals, not applied trades. [Research](./asistente-claude.md#optional-tradingview-research) requires separate explicit metadata approval and may incur provider/model costs; fake-CLI evidence is not paid-model or real-OAuth certification.
