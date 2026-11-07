[← Documentation index](README.md)

# Data

**Data** (⌘3) manages local history used by charts, replay and backtests. The left column lists **Stored** instruments, the middle their days, and the right offers history downloads, Databento and file import. The header shows instrument count/size, Refresh and Open folder.

![Stored history and download cards](img/datos.png)

## Sources of history

- Incoming live data records automatically, including Rithmic, when **Settings → Data → Record incoming live data** is enabled (default).
- Complete-day public archive/API downloads.
- Imported CSV/TXT files.
- Long-range CME futures history from Databento.

Trade files are organized by connection, instrument and UTC day. Synthetic quote/bar data must not be mistaken for actual exchange executions.

## Stored history

An instrument shows connection, day count and disk usage. Selecting it lists days newest-first with size-proportional bars and the complete date range/size. Use ⌘/⇧-click for multiple days.

The **Lossless archive** action accepts up to 31 selected closed UTC days. It captures their stored metadata locally, provides progress/cancellation and creates lossless archives without deleting/replacing the original raw files. Copy/read/codec failures remain visible. Day/replay/delete selection is frozen while that owned operation settles; true Quit cancels/drains it before shared history resources leave.
Each archived day has a bounded **512 MiB** input limit. An oversized/changed/unreadable day is a visible failure with the original still authoritative, not a partial success or permission to remove raw history.

Non-IB downloads, replacements, deletion and archival coordinate physical-day ownership with recording. A conflict refuses before changing the affected files. An already-owned CSV publication reuses its ownership rather than trying to acquire it again. This does not forbid legitimate closed-day downloads or replace IB's publication coordinator.

True Quit closes recording admission and awaits the actual writer, accepted queue tail, flush and physical lease release. It retains that completion even if an instrument stopped, reconnected or left an ordinarily disposed hub. Ordinary view/feed disposal remains bounded. Cancellation cannot force a blocked filesystem write to finish, and a recording failure is not a successful saved-tail claim.

### Delete days

Select an instrument and days, choose **Delete** and confirm. Only those days are removed, but **deletion cannot be undone**. Do not delete data being recorded or needed by running strategies.

## Replay

1. Select an instrument and one or more stored days.
2. Choose **Replay**, or double-click a day.
3. A new ▶ **Practice** tab is created with its own run ID, replay clock, portfolio, account, orders, fills and journal. It starts paused, copies the selected period/indicators and warms up only from local files.

| Control | Action |
|---|---|
| Play / Pause | Start / pause replay. |
| Speed | 1×, 2×, 5×, 10×, 30×, 60× or 300× real time. |
| Clock / progress | Current replay timestamp; Completed at the end. |
| +5 min / +30 min / +1 h | Skip forward. |

Practice drawings and tabs are not restored with the ordinary workspace. Practice fills belong **only** to their run: concurrent normal simulation and other practice runs do not share their balance, positions or journal. A practice chart cannot select a real account or use cross-trading/micro-contract routing.

Strategy Apply, period changes and temporary **Visual only** mode preserve the exact practice account's ledger and owned native-style protection. Visual mode uses a separate private shadow account; it cannot restore the practice memento into that shadow or move its fills into normal simulation. Returning to practice reattaches only the same run/data/account. Natural completion first drains the final queued input batch; producer-finished alone is not completed execution.

**Random / blind practice** selects among locally available instruments/days with an explicit seed and duration. Reusing the same candidates, seed, fee configuration and source data reproduces selection and estimated fills. Blind mode conceals instrument/date/absolute-time labels until **Reveal** or the run ends; it is UI concealment, not security isolation. The underlying local data remains accessible to its owner.

**End / close practice position** stops that run's strategies and pending tape, cancels owned orders and closes its practice position at the run's available mark. **Result / journal** identifies run/seed, fees and source quality/resolution/corrections. Fill/fee assumptions are deterministic simulation, not a promise of executable market liquidity. Results and fills never enter the normal simulated-account history.

## Download history

1. Select an instrument; a downloadable active-chart instrument is proposed automatically.
2. Under the UTC-day range choose Yesterday, 7/30/90 days, 6 months, 1/2 years, or enter From/To. Today is excluded because it is incomplete.
3. Review day count and source.
4. **Download** shows current day, progress and remaining work. **Cancel** stops it.

| Provider | Download |
|---|---|
| Binance Futures / Spot | Public daily archives without credentials. |
| Bybit | Public daily archives without credentials. |
| Rithmic | Trades for each day's actual contract. Expired-contract history is limited by provider retention. |
| Tradovate | Ticks subject to CME API-data entitlement. |
| cTrader | Broker bid quotes from Open API. |
| OANDA | Five-second bars converted to synthetic trades. |
| Alpaca | Consolidated SIP stock trades, subject to entitlement/delay. |

- Complete days are skipped, so canceled downloads can resume.
- API downloads skip Saturdays. A successfully downloaded day replaces that day's live-recorded file.
- ES can exceed a million trades daily; long ranges take time and space.
- Empty holidays/weekends/unpublished days are counted separately. Missing expired Rithmic contracts are reported.
- Sources without download support can still record the live data you view.

## Import files

Import Dukascopy/HistData or MetaTrader/TradingView exports for index CFDs, forex, gold and other markets. Formats are detected automatically: bid/ask ticks, bars, or date/price/OHLC CSV.

1. Obtain files; Dukascopy/HistData buttons open their download pages.
2. Enter a symbol or leave it blank for filename inference.
3. Choose the file's time convention:

   | Setting | Typical source |
   |---|---|
   | UTC | Dukascopy / TradingView. |
   | Fixed New York, UTC−5 | HistData. |
   | UTC+2 | MetaTrader winter. |
   | UTC+3 | MetaTrader summer. |

   These are fixed offsets, not an automatic daylight-saving conversion; verify the file's actual convention.
4. Choose one or more CSV/TXT files using **Import files**.
5. Review imported rows/days, range, inferred display price increment and the explicit **quality summary**. The instrument appears under imported history and in Backtesting. The stored feed ID remains `importado` for compatibility.

Filename inference: USATECH/NAS100/NSX/US100/USTEC/NDX → US100; USA500/SPX/US500 → US500; USA30/US30/DJI/WS30 → US30; DEU/GER/DAX → GER40; XAU/GOLD → XAUUSD. EURUSD, GBPUSD, USDJPY, AUDUSD, USDCAD, USDCHF, NZDUSD, XAGUSD and BTCUSD retain their pair names. Otherwise the first filename word is used; if that is empty, the legacy ticker `IMPORTADO` is retained so existing imports do not split across renamed identities.

CFDs do not have centralized exchange execution volume. Quote observations retain prices/timestamps but store unknown executed volume as **zero**, with explicit synthetic/inferred-side/partial provenance and no observed trades/footprint. OHLC paths are synthetic; reported volume, including a genuine zero, is retained, while missing volume is labelled and stored as zero rather than invented unit volume. Inferred/unknown bar intervals and source availability remain explicit. These inputs can support price analysis, but cannot silently become authoritative footprint/delta, individual large trades or causally known intrabar execution.

## Databento

Databento supplies paid historical CME ticks, including aggressor side, beyond Rithmic's retained range. Signup credits, availability and prices vary. The app estimates cost and requires confirmation before downloading.

Add a Databento API key in [Connections](conexiones.md#databento), or use the card's Connections button.

1. Choose a volume-based continuous contract: NQ, ES, MNQ, MES, YM, RTY, CL, GC, MCL, MGC, ZN or 6E.
2. Choose From/To; the default is approximately three months.
3. **Also download days already available from Rithmic**:
   - Off: skip complete Rithmic days and fill missing history, reducing cost.
   - On: download the entire requested Databento range.
4. **Calculate cost** shows pending days, range and estimated US-dollar usage.
5. **Download** opens a paid-download confirmation whose default is **Cancel**: Enter cancels; confirm with a click or ⌘Enter. **Usage beyond available credits can be charged to the payment method on your Databento account.**

Download is enabled only after estimating cost. Changing market, dates or the checkbox invalidates the estimate. Existing complete Databento days are skipped rather than charged again; Saturdays and today are excluded. Empty days are counted separately. Cancel retains completed files; provider charges already incurred are not undone.

The card also summarizes locally available Databento markets, days and date ranges.

### Separate storage

Databento files stay separate from Rithmic files. In Backtesting, a futures instrument with both sources has:

| Choice | Used data |
|---|---|
| Rithmic + Databento | Rithmic takes priority; Databento supplies minutes with no Rithmic trades. |
| Only Rithmic | Rithmic alone. |
| Databento | Databento alone. |

The same applies to Tradovate + Databento / Only Tradovate / Databento. Combined-data summaries show each provider's contributed days. Combining is performed when reading; original files remain separate.

## File locations

History is under `~/Library/Application Support/Trading Hub/Ticks`, then connection (`rithmic`, `databento`, `importado`, etc.), instrument and day. **Open folder** opens Finder. See [Storage](instalacion.md#where-your-data-lives).

Compatibility headers and feed IDs are intentionally retained, including recognized Spanish CSV column aliases and the historical live-trade CSV export column names. Translating identifiers would break existing consumers; new report prose and UI are English.

## Raw, compact and candle history

**Settings → History and depth** independently configures recent individual-trade retention, bounded initial raw/compact memory and measured compact display buckets of **1, 5, 15 or 60 minutes**. Larger raw windows do not turn older candle history into raw executions. Old raw days can be losslessly compressed; legacy binary history remains readable, with provenance-aware records preserving exact decimal quantities and availability times. Compact caches and candle fallbacks never overwrite finer raw history.

Quality follows the data through trade stores, candles, SDK indicators, replay and strategy/backtest inputs:

| Quality | Meaning |
|---|---|
| Raw | Individual source events at the available feed resolution. Aggressor side can still be unknown/inferred. |
| Compact | Measured aggregate buckets with an explicit resolution; not individual large trades or event-complete footprint. |
| Synthetic | OHLC-derived/quote-modelled estimates, not observed exchange executions. |
| Inferred side | A side classification estimate; measured price/quantity is not permission to describe the side as observed. |
| Partial | Known missing constituent/time/retention coverage. It is not an empty successful complete interval. |

Mixed coverage remains mixed. Missing footprint/delta is distinct from a genuine zero. A chart period finer than its compact source cannot recover unavailable intrabucket timing or extrema. Capacity limits, cache fallback and unavailable/corrupt source warnings stay visible; they do not silently describe truncated history as complete.

Current-month candle maintenance fetches missing coverage plus a bounded correction overlap rather than downloading the whole month on every request. Completed candle/profile cache identities include provider/instrument, period/configuration, source quality and correction identity. Corrupt or mismatched cache data is recomputed from intact source; same-length price-distribution corrections cannot reuse stale profiles.

## Opt-in candle maintenance and offline catalogs

**Settings → Candle maintenance** is off until enabled. Select/track instruments, lookback years, interval and per-cycle limits; Run and Cancel operate on the same owned, nonoverlapping worker. A disabled keeper performs no background network work. Earlier failed month/resolution slots remain retryable and visible even if later slots succeed; successfully retained finer history is not replaced by candle summaries.

The instrument selector can use saved provider-scoped catalogs offline. Rows disclose provider, captured time and stale/offline status; **Use saved instrument catalogs only** prevents discovery/ticker requests. Browsing or selecting a data connection never silently chooses another trading account. A retained incompatible account stays visibly unavailable until a compatible account is explicitly selected.

## Recorded depth and heatmap fidelity

Depth recording is a separate **off-by-default** opt-in. Enabling a heatmap or Orderbook Delta does not grant recording permission. Recording stores received L2 changes and bounded exact snapshots with provider/venue, availability time, sequence information where supplied, gaps and I/O status. Memory, queue, selected-history, disk and retention budgets bound the recorder; loss, quota truncation and failures remain visible.

The **Liquidity heatmap** displays resting size, not traded volume, clips to the selected visible chart/range and shades missing/stale intervals. Opening an older visible range loads its recorded depth without replacing another chart's selection. A reconnect, missing refresh, source change or write failure cannot become a fresh carried-forward zero book. Snapshot/BBO-only and partial provider coverage remain labelled; none of this is exchange-event-complete or MBO history.

A fresh, validated **known side** can remain visible in the DOM even when the other side is unavailable. That is explicitly **missing / partial book** coverage, not a complete two-sided snapshot: heatmap/Orderbook Delta analytics remain unavailable until their full-book requirements are met. The known side still expires at the freshness limit; disconnects, sequence gaps, invalid updates and a required new full refresh cannot be bypassed by an ordinary partial update.

**Orderbook Delta** distinguishes sample-average (one equal vote per received usable snapshot), duration-average (known covered time only), last fresh snapshot and separate-side maximum. For example, bid/ask `10/2` for one second followed by `30/6` for three seconds gives sample-average `20/4` (delta 16), but four-second duration-average `25/5` (delta 20). A two-second staleness limit makes the terminal last book unavailable after three seconds; gaps are not carried forward.

The aggregate crypto book is explicitly from its designated **first venue**, never a consolidated or failover book. Rithmic/AMP's available BBO is one level per side and needs fresh prices **and sizes on both sides** after reconnect before a refresh is advertised. Longbridge depth is partial/snapshot-based where its API does not establish continuous sequence coverage. Source labels and the supplied feed resolution are part of the interpretation.


## Interactive Brokers bars and offline history

The selected data-only IB route supports local CSV minute bars as well as Gateway history. Import preview is immutable and revalidated at commit, with writer leases; changing inputs requires a fresh preview. Default policy preserves recorded real-trade minutes byte-for-byte and fills only uncovered minutes. **Replace covered minutes** is destructive and must be explicit. Backups and complete rollback authority are preserved before publication. Files are still renamed separately, but cooperating readers acquire one snapshot for the entire logical range: they see a complete committed generation or complete recovered originals, not a mixture of published days. Recovery validates every affected target before restoring any of them; unrelated changes or damaged authority block the read rather than becoming successful empty history. Backups are not automatically pruned.

Synthetic minute bars are stored separately under `ib-bars`; real recorded minutes take precedence when reading combined history. Imported OHLC is not observed tick execution, including through strategy backtests and footprint aggregation. Do not infer aggressor flow or intrabar execution from it. Active-writer readers can read a committed prefix, but strict import validation remains strict and corruption after the writer is quiescent is not silently hidden. Offline mode never contacts Gateway.

Snapshot source stamps include a pre-mutation change epoch, scoped file metadata and SHA-256 of the scoped source bytes. Saved context and Backtesting/Lab tick caches validate the exact loaded range before reuse, including replacements that preserve file lengths and modification times without publishing an application epoch. Stamps are captured under the same ownership as the source read, before materialization or callbacks; callbacks and network waits do not retain snapshot ownership. Complete-generation visibility still requires cooperating application writers: content hashing can detect a stable external replacement, but cannot make arbitrary concurrent raw-file modification atomic.

Live-trade MAE/MFE also validates its economic basis and IB source stamp; refreshing the trade table cannot reuse an older view-only result after a price or history correction. A relevant active-recording prefix that does not bracket the closed trade stays unavailable rather than becoming a cached zero or incomplete extreme. Once saved prints establish the range, it can be calculated again. This is evidence about the available range, not proof of complete exchange coverage or observed intrabar execution from synthetic bars. **Data → Refresh** scans off the UI thread, so a pending import does not prevent cancellation or other UI interaction; completed indices are applied together.

Native commit files retain disk flush; supported Mac files additionally request full-drive flush, and supported macOS/Linux directory synchronization orders recovery-authority, target and commit-marker publication. A failed managed-parent synchronization remains retryable even when its newly created child directory survives. Successful OS calls are not hardware power-loss, filesystem-rollback or exactly-once guarantees. A post-publication synchronization/finalization error is not reported as if the original files had necessarily been restored.

The headless entry point runs before GUI/singleton/credential initialization. From a built App DLL (replace paths with your isolated fixture paths):

```bash
dotnet src/TradingHub.App/bin/Release/net10.0/TradingHub.dll --import-ib /absolute/path/to/csv-folder MNQ --preview
dotnet src/TradingHub.App/bin/Release/net10.0/TradingHub.dll --import-ib /absolute/path/to/csv-folder MNQ
dotnet src/TradingHub.App/bin/Release/net10.0/TradingHub.dll --import-ib /absolute/path/to/csv-folder MNQ --replace
```

The symbol is optional where the input supplies it. Only `--preview` and `--replace` are accepted import switches; unknown arguments and invalid input print `IB import failed: …` and exit 2. Preview exits 0 without publication. See [Development](desarrollo.md#selected-non-kvp-reproduction) before running: never point a fixture import at personal history.

## Open-interest observation fidelity

**Open interest** is available only for Binance USDⓈ-M Futures, never synthetic OI or a substituted symbol. Live values use server observation time. Historical requests use five-minute observations, at most 500 per page, limited to the latest 30 days. The displayed observation envelope is not continuous coverage.

Lookup never looks ahead. Time bars use a close-exclusive cutoff clamped to the current clock; non-time bars use their actual last-trade time. Range bounds apply, and samples older than ten minutes are unavailable. Zero is a real value, not missing data; signed change requires both adjacent candle cutoffs to be known. Gaps do not become zero or skip across an unknown adjacent bar. A busy recorder warning remains visible: normal/narrow/short charts show readable full consequences; an extremely small chart retains a compact **Not recording** status with full translated pointer/keyboard disclosure. Recovery clears both warning and stale focused disclosure. Retained writer ownership and drain protect this recorder's lifecycle, not stronger market-history or financial-ledger durability guarantees.
