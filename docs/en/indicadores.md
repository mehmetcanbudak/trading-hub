[← Documentation index](./README.md)

# Indicators

Open **Indicators** from the chart toolbar or **Chart → Indicators** (⌘I). Search the library by name/category and double-click or Add to chart. Select an active indicator to edit properties. Templates save/apply settings and choose defaults for new instances; see [Charts](./graficos.md#indicators).

Strategy indicator factories such as Sma/Ema/Rsi are a separate, simpler [API](./estrategias.md#indicators).

## Included indicators

| Indicator | Category | Main options |
|---|---|---|
| Moving average | Trend | Period, simple/exponential/weighted method, source. |
| Bollinger Bands | Trend | Period and deviation multiplier. |
| VWAP with bands | Volume / Order flow | Session/week/month reset and up to three volume-weighted deviation bands. |
| Volume | Volume / Order flow | Color by delta or candle direction. |
| Delta | Volume / Order flow | Histogram or candles showing intrabar delta high/low. |
| Cumulative delta (CVD) | Volume / Order flow | Candles with optional session reset. |
| CVD by market | Volume / Order flow | One cumulative delta line per market, exchange or spot/perpetual group of an aggregate, the sign-colored aggregate CVD and an unattributed line. |
| Derivatives: funding, open interest, liquidations | Volume / Order flow | Binance and Bybit perpetual funding, open interest (level or change) and observed liquidations, per exchange and combined. |
| Big Trades | Volume / Order flow | Combine fills of large aggressive orders across levels; fixed/adaptive percentile thresholds, price placement, colors and sizes. |
| Session volume profile | Volume / Order flow | Session/week/visible history, volume/delta/bid-ask, value-area percentage, POC, VAH/VAL. |
| Volume profile | Volume / Order flow | One entry for session/week/month/quarter/year/all-history profiles: ticks per row or price grouping, volume/delta/bid-ask view, untested POCs and head-to-tail merging (right-click or automatic). |
| VWAP suite | Volume / Order flow | One entry with selectable parts: anchored VWAP with bands, VWAP Claire, multi-period, prior/closing, rolling and session-window VWAP, each with its own settings. |
| RSI | Oscillators | Period. |
| MACD | Oscillators | Fast/slow averages and signal. |

Built-ins come from TradingHub.Indicators.Native.

### Library groups

The library lists **★ My indicators** first, then the default groups in this order: Moving averages, Trend and strength, Oscillators, Volatility and channels, Volume, Order flow (delta, CVD, footprint), Volume profile, VWAP, Order book, Derivatives (open interest, liquidations), Options, Market structure and key levels, Candles and patterns, Sessions, Macro and other tools. Empty default groups are hidden; any other category becomes its own group after these.

- Click a group header to open or close it. Right-click a header to rename the group (empty restores the default name), create a new group below it, move the group up/down, sort it A → Z, delete a group you created (its indicators return to their default groups) or restore the default arrangement.
- Right-click an indicator to add it, view/edit its source, **Move to group** (including a new group), or **Move up / Move down** (also Alt+↑ / Alt+↓). Drag an indicator onto another row or a group header to move it.
- The arrangement is stored in `settings.json` (`IndicatorLibrary`). It never changes indicator types, saved charts or templates. While searching, only groups with matches are shown.
- **Rename…** gives an indicator type a display name (alias) used in the library, chart legends, the indicator settings title and its alerts, including instances already on charts. It is display only: the type, saved charts, templates and series keep their identities, and **Restore the default name** (or an empty name) brings back the original. Aliases are stored in `settings.json` (`IndicatorLibrary.Aliases`) and apply per indicator type, not per chart instance.
- **Remove from library…** hides an indicator from the library list only (`IndicatorLibrary.Hidden`). Charts, saved charts and templates that use it still load and draw it. Right-click a group header, an indicator or the empty list and use **Show hidden indicators (N)** to restore one or all.
- Studies join a group through their category attribute, for example `[Category(IndicatorCategories.Vwap)]`; the older Volume / Order flow, Trend and Levels categories map to Order flow, Trend and strength, and Market structure and key levels.

### Technical indicators

TradingHub.Indicators.Technical supports locally importing the public ATAS technical collection, roughly 250 indicators. They are **not bundled** because public source access does not grant unrestricted redistribution. The repository supplies only necessary compatibility/editor types. See [Import ATAS](#import-atas-indicators). Available imported indicators appear in their categories.

## Custom indicators

- Place C# files (`.cs`) or [KVS scripts](#kvs-scripts) (`.kvs`) in `~/Documents/Trading Hub/Indicadores`. Subfolders are supported; bin/obj are ignored. The example `DeltaPercentageExample.cs` (**Example: delta % per bar**) is written once into an empty folder; a hidden `.example-created` marker in that folder means deleting the example keeps it deleted, and an existing file is never replaced.
- File create/save/delete/rename triggers recompilation after about 0.6 seconds without changes; **Code → Recompile indicators** forces it.
- Files compile together. On failure the last successful assembly remains available and the first error is shown. Success reports compile duration.
- Custom indicators appear first in **My indicators**, with a star/custom badge. Remove and re-add an already loaded instance to use updated code.

### Editor

Open **Code → Indicator editor** (⇧⌘E on macOS, Ctrl+Shift+E on Windows) or the library code button. New creates a class-named template; the script button creates a KVS script from an English starter (EMA bands with an alert). **Save and compile** (⌘S / ⌘B; Ctrl+S / Ctrl+B on Windows) recompiles. Click diagnostics to jump to their line; the folder button opens the platform file manager. Renaming keeps the file's language: a `.kvs` file cannot be renamed to `.cs` or the reverse.

### Example

New examples use English class/property/series identifiers and display metadata. Existing user indicators keep their identifiers unless an explicitly requested migration has checked saved chart and template references.

```csharp
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Drawing;
using TradingHub.Indicators;

[DisplayName("Average range")]
[Category("My indicators")]
public class AverageRange : Indicator
{
    private readonly ValueDataSeries _series = new("Range", "Average range") { Color = Color.Orange, Width = 2 };
    private int _period = 14;

    [Display(Name = "Period", GroupName = "Settings")]
    [Range(1, 1000)]
    public int Period
    {
        get => _period;
        set { _period = value; RecalculateValues(); }
    }

    public AverageRange()
    {
        Panel = IndicatorDataProvider.NewPanel; // Separate panel below price.
        DataSeries[0] = _series;
    }

    protected override void OnCalculate(int bar, decimal value)
    {
        if (bar < Period - 1) return; // Wait for enough bars.
        decimal sum = 0;
        for (int i = 0; i < Period; i++)
        {
            var candle = GetCandle(bar - i);
            sum += candle.High - candle.Low;
        }
        _series[bar] = sum / Period;
    }
}
```

### API summary

- Public Indicator subclass, parameterless constructor, DisplayName and Category metadata. System/Linq/Collections.Generic are implicit. Add ComponentModel, DataAnnotations, Drawing and TradingHub.Indicators. Custom rendering may require TradingHub.Indicators.Drawing, TradingHub.Rendering.Context and TradingHub.Rendering.Tools.
- Series: ValueDataSeries for lines or `VisualMode.Histogram`; RangeDataSeries with `new RangeValue { Upper = a, Lower = b }`; PaintbarsDataSeries for candle colors. Replace DataSeries[0] for the primary series; add others with DataSeries.Add.
- Panel defaults to price; use IndicatorDataProvider.NewPanel for a separate pane.
- Parameters use public properties with Display/GroupName and optional Range validation. Store changed values and call RecalculateValues in setters.
- OnCalculate receives zero-based bar index and closing value. GetCandle exposes OHLC, Volume, Ask, Bid, Delta, MaxDelta, MinDelta and Time. CurrentBar is bar count. Guard negative indices during warmup.
- Optional rendering: set EnableCustomDrawing and subscribe to DrawingLayouts.Final in the constructor. Override OnRender(RenderContext, DrawingLayouts); use ChartInfo.GetXByBar/GetYByPrice and context DrawLine/FillRectangle/DrawString. Visible indices are FirstVisibleBarNumber through LastVisibleBarNumber.

The [Claude assistant](./asistente-claude.md) can also write indicators into this directory. **Untrusted C# executes in-process, not in a sandbox.**

## KVS scripts

KVS is a small declarative language for indicators: declare what goes in, what comes out and what is calculated on every bar. A `.kvs` file is translated to one C# indicator class and compiled **by the same compiler, in the same pass and with the same references as your C# files**; generated code never runs through any other path. Scripts appear under **My indicators** (category **KVS scripts**) as type `TradingHub.UserScripts.Kvs.Kvs_<file name>` (non-ASCII letters and symbols become `_`). Renaming the file therefore renames the type: saved charts keep referring to the old name.

```js
name "EMA bands"
param length = 20 { min: 1, max: 500, description: "Average length (bars)" }
param mult = 2
input src = ohlcv.close
input buys = trades.volume { side: "BUY" }
output basis = line { overlay: true, color: "#f2b53a", width: 2 }
output upper = line { overlay: true, color: "#3d8bfd" }
level 0
onBar {
  basis = ema(src, length)
  upper = basis + mult * stdev(src, length)
  if (crossover(src, upper)) { alert("Closed above the band") }
}
```

| Part | Meaning |
|---|---|
| `name "…"` | Library name (default: the file name). |
| `param n = 20 { min, max, description }` | Setting shown in the indicator properties: a number or `true` / `false`. Changing it recalculates. |
| `input x = …` | `ohlcv.open/high/low/close/volume/hl2/hlc3/ohlc4/time`, `trades.volume { side: "BUY" / "SELL" }`, `trades.buy`, `trades.sell`, `trades.delta`, `trades.count`, `book.bid` / `book.ask` / `book.delta { within: ticks }` (average resting size near the best price over the bar's recorded depth samples). `open`, `high`, `low`, `close`, `volume`, `hl2`, `hlc3`, `ohlc4`, `time`, `delta`, `buyvol`, `sellvol` and `trades` work without a declaration. |
| `output x = line \| histogram \| dots { … }` | Options: `overlay: true` (on price; otherwise every output goes to a separate panel), `color`, `width`, `opacity`, `label`, `show_zero: true`, `color_by: "sign", colors: [positive, negative]`. |
| `level 70 { color }` | Horizontal panel line. |
| `onStart { }` / `onBar { }` | Run before a full recalculation / for every bar (the forming bar is recalculated on updates). |
| `var x = …` | Variable; also a series, so `x[1]` is its previous-bar value. |
| `x[n]` | Value of any series `n` bars ago (missing before the first bar). |
| `if (…) { } else if (…) { } else { }`, `and`, `or`, `not`, `? :` | Conditions. |
| `alert("…")` | In-app alert, once per bar and message, only on the last bar; recalculating does not repeat it. |

Functions: `ema sma rma wma stdev highest lowest sum change cum crossover crossunder` and `abs min max sqrt log log10 exp pow round floor ceil sign nz na sin cos tan`; constants `bar_index`, `na`, `PI`. The wrun call form is accepted too: `param("n", 20, { min: 1 })`, `input("c", ohlcv.close)`, `output("x", line, overlay, { color: "#fff" })`, `function onBar() { … }` and `out_x(value)`.

Semantics and data honesty:

- Every function call keeps its own state, so call windowed functions on every bar (not only inside an `if`). `sma`, `wma`, `stdev`, `highest`, `lowest` and `sum` return `na` until their whole window has values; `ema` and `rma` start from the first value; `stdev` is the population deviation.
- Missing values are `na` (NaN). Outputs do not draw `na`, and also hide exact zeros unless `show_zero: true`.
- `trades.buy`, `trades.sell`, `trades.volume { side }` and `delta` are `na` on bars that have volume but no aggressor split, instead of an invented zero. `book.*` is `na` without usable recorded depth for the bar; nothing is estimated.
- Names cannot reuse C# keywords or members of the indicator base class (for example `Name` or `Panel`), and each name is declared once.

Errors: script errors report the `.kvs` line and column; C# errors in generated code (for example assigning a comparison to a number) are mapped back to the script line, at the statement's first column. Any error fails the whole compilation, so the last successful indicators stay loaded, as with C# files.

## Validation checklist

Before relying on a new or changed indicator:

- Compare its output with known results on a fixed historical dataset using the same inputs and parameters.
- Check warmup, repeated updates to the live forming candle and several timeframes.
- Check for look-ahead bias: a historical trading signal must not use bars that were unavailable at its decision time.
- Verify that separate charts/instruments do not leak calculation state into one another and that a fresh recalculation reproduces the expected results.

Use isolated data and simulation first; successful compilation alone does not establish calculation correctness.


## Extra DLLs

Copy compatible indicator DLLs to Application Support/Trading Hub/Indicators or an Indicators folder beside the app executable. They load at startup under their Category, or Other, with an extra badge. Load failures are reported without preventing other DLLs from loading. Only load trusted binaries.

## Import ATAS indicators

The source is [AtasPlatform/Indicators](https://github.com/AtasPlatform/Indicators).

**Import the collection locally**, from the repository root:

```bash
git clone https://github.com/AtasPlatform/Indicators.git third_party/ATAS-Indicators
python3 tools/port_atas.py
./build-app.sh --install
```

The porter copies Technical sources into `src/TradingHub.Indicators.Technical/Atas`, adapts namespaces and applies selected compile/division-by-zero compatibility fixes. It excludes dependencies on unsupported ATAS internals such as window editors, heatmap and DOM v3. Additional exclusions use `TRADINGHUB_PORT_EXCLUDE=File1.cs,File2.cs`.

Update locally with:

```bash
git -C third_party/ATAS-Indicators pull
python3 tools/port_atas.py
./build-app.sh --install
```

Both third_party and generated Atas sources are git-ignored, so they are not automatically redistributed in a fork.

**One file at a time:** copy sources into the custom indicator folder without rebuilding the app. Include dependencies: ATAS CCI/Momentum, for example, depend on its SMA. The same approach works for other compatible public C# sources, subject to their licenses.

Imported authors' labels, generated commentary or custom user overrides may use their original language; they are not rewritten destructively. English is the host's canonical SDK/source language; built-in display text follows the selected English, Spanish or Simplified Chinese runtime language. Generated C# and new sample identifiers stay English.

## ATAS API compatibility

Trading Hub provides corresponding Indicator, OnCalculate, DataSeries, OnRender, GetCandle and ChartInfo APIs. Compilation adapts namespaces:

| Original | Compiled as |
|---|---|
| ATAS.Indicators / .Drawing / .Technical | TradingHub.Indicators / corresponding subnamespaces. |
| ATAS.DataFeedsCore | TradingHub.Trading. |
| OFT.Rendering and Context, Context.GDIPlus, Settings, Tools, Control | TradingHub.Rendering and corresponding namespaces. |
| OFT.Attributes / .Editors / OFT.Localization | TradingHub.Attributes / .Editors / TradingHub.Localization. |
| Utils.Common / .Logging / .Collections | TradingHub.Common and corresponding namespaces. |

CrossColor/CrossColors alias System.Drawing.Color; CrossPen, CrossPens, CrossSolidBrush, CrossKey and CrossKeyEventArgs are also supplied. TRADINGHUB and CROSS_PLATFORM are defined, permitting `#if TRADINGHUB` adaptations in shared source. Strategy compilation uses the same compatibility processing. Not every ATAS feature exists; diagnostics identify unsupported APIs.

## Selected additional native studies

These selected studies complement, rather than replace, the existing moving average and **Session volume profile**. The [dashboard](./TRANSLATION_STATUS.md) distinguishes the accepted non-KVP runtime from the separate KVP capability candidate and its actual validation evidence.

- **QuanTAlib moving average** and the ribbon offer SMA, EMA, WMA, HMA, ALMA, JMA, KAMA, T3, ZLEMA, DEMA, TEMA, VIDYA and SuperSmoother. Type, Period, Source and Show warmup control display; readiness still follows library `IsHot`. Showing warmup does not make a value ready. Genuine zero/negative results remain distinct from unavailable values. Latest-bar replacement updates streaming state; arbitrary historical edits replay the series.
- **QuanTAlib Camarilla** supplies PP and H1–H6/L1–L6 from the previous **observed** session. Initial loaded history may begin partway through a session; no levels are invented before rollover. Select Chart session, CME (18:00 New York), or Custom session with a valid time zone/start hour.
- **Anchored Flow VWAP** selects Manual, Swing or VolumeSpike anchor modes. Manual UTC uses `yyyy-MM-dd HH:mm` (empty means loaded-history start). Swing anchors become known only after the configured closed bars on each side; primary VWAP/band series are **causal and never backfilled into earlier strategy bars**. Optional **Retrospective chart lines** preserve the visual pivot-to-confirmation segment as drawing-only data; `RetrospectiveValueAt` must be interpreted with `RetrospectiveConfirmationAt`, not as an earlier live signal. Volume spikes compare preceding bars, not a baseline contaminated by the candidate. Weight selects AbsoluteDelta, Volume or AbsolutePriceNotional (absolute price × volume).
- **Refill Zones (heuristic)** uses Minimum bucket volume, Floor-price bucket size, Absorption only and Project until mitigation. It is an absorption/refill heuristic, **not proof of book replenishment or icebergs**. Optional estimated OHLC uses threshold × 0.4 and at most 200 buckets.
- **Composite Volume Profile** uses host Session/Week/Month boundaries, nearest-price grouping (ties away from zero), Value area (%) and optional chronological composition when the next POC is inside the current value area. Month is session-based, not simply a UTC date label. Equal-volume POCs choose the lower price; equal adjacent value-area volume expands upward first. POC projection uses a 60-profile lookback with up to 10 tested POCs; untested projections follow their configured limit (zero means all within the finite lookback).
- **Open interest** shows server-observed OI and adjacent-known-cutoff signed change. See [Data fidelity](./datos.md#open-interest-observation-fidelity) for retention, freshness and missing-value rules.
- **CVD by market** splits an aggregate crypto instrument's delta by the market that printed it (see [Data fidelity](./datos.md#per-market-delta-and-crypto-derivatives)). On a single-market chart it shows only the aggregate line.
- **Derivatives** derives the perpetual symbol from the chart's base asset (BTC-USDT-SWAP, BTC-USD and BTC become BTCUSDT) unless **Perpetual symbol** is set. Choose funding, open interest, open-interest change or liquidations; unknown periods are gaps, not zero.

For all three flow/profile studies, **Allow estimated OHLC** is off by default. Any observed footprint rows win over aggregate fallback, but mixed observed/estimated contributions remain marked incomplete; observed rows include Between volume and do not certify exchange-authoritative aggressor side (IB sides are inferred). Composite OHLC fallback is bounded to 200 samples. Coverage annotations distinguish observed, estimated, incomplete and missing bars; absence of a plot is not automatically a zero signal. Estimates and retrospective anchors must not be represented as observed trades in strategy evaluation.

## Extended profile, VWAP and causal study pack

The KVP capability adoption reuses the existing native catalog and persistent study types rather than adding a second analysis engine. Source attribution and the independent SMC/HTF implementation boundary are in [Third-party notices](../../THIRD_PARTY_NOTICES.md#kvp-capability-reference).

| Study / group | Available behavior and important boundary |
|---|---|
| **Composite Volume Profile** — VP Ultimate extras | Buyer/seller POCs, inner and outer value areas/tails, display-only raw/logarithmic/median-cap volume transforms and correction-aware completed-profile caching. Display transforms never change conserved source volume, delta or reported totals. Existing saved Composite settings remain valid. |
| **Session volume profile** | Physical market-tick row sizes or target-row sizing (2–4,096 rows), never finer than actual source resolution; configurable POC/value-area widths and supported stroke styles. Missing/synthetic omitted contributions mark partial coverage, not an authoritative empty profile. |
| **Naked POCs** | Simultaneous session/week/month/year completed-profile levels, bounded retention and optional touched-level display. A level becomes strategy-available only after its source period completes; the later touch and its confirmation are separate facts. |
| **Multi-period VWAP** | Simultaneous session, week, calendar month, quarter and year VWAP. |
| **Prior and closing VWAP levels** | Prior completed-period VWAP, weighted bands and bounded closing-level history; no backfilled strategy knowledge before period completion. |
| **Rolling VWAP** | Moving minutes, session-calendar days/months/years, minimum source bars and bands. Calendar months/years are actual calendar arithmetic, not fixed 30/365-day substitutes. Whole source bars crossing a window boundary retain their source-bar resolution. |
| **Session-window VWAP** | Configurable local-time/overnight windows and time zone with DST. Membership follows the opening time of each available source bar; intrabar boundaries are not invented. |
| **Session backgrounds / Key levels** | Configurable session windows/background/ranges; four-hour/session/week/month/quarter/year opens, prior highs/lows/midpoints and completed Monday ranges. First loaded periods can be partial. |
| **Robust volume / Double inside day / Delta bubbles** | Robust volume and zero-MAD handling, nested/contained inside-day breakouts with confirmation/expiry, and configurable delta/volume bubble heuristics. A heuristic is not an observed order-book or institutional-trading fact. |
| **Cumulative delta (CVD)** | Candles, line, area, histogram or steps; never/session/week/month/quarter/year resets; simple/exponential smoothing; confirmed divergences with optional retrospective visual painting. Series values remain confirmation-timed. Missing or disallowed inferred-side data creates a gap, not zero delta. Compact data cannot supply raw intrabar delta extrema. |
| **Market structure** | Swings, BOS/CHoCH, order blocks/breakers, FVG, equal highs/lows, sweeps and CISD with configurable confirmation, tolerance, mitigation and layer visibility. Source location and availability/confirmation bar are exposed separately. |
| **Higher-timeframe structure** | Mapped or explicitly selected periods, closed HTF candles/FVG, PO3/T-Spot and projections. Forming previews are explicitly labelled and are not silently published as confirmed strategy values. |
| **Triple Bollinger** | Three independently mapped/configured periods, completed-candle length and population deviation multipliers. Warmup stays unavailable; monthly aggregation follows actual session calendars. |
| **VWAP Claire** | Explicitly **bar-based HLC3 estimates**, with 30/90/365-calendar-day completed-session windows and optional session/week/month anchors. Values are held within the current session; short history is partial, not tick-exact VWAP. |
| **Volume profile** (unified entry) | The library entry for period profiles, built on the Composite engine: session, week, month, quarter, year or **All loaded history**; **Row sizing** by ticks per row (default one instrument tick, never finer than the chart's footprint row) or price grouping; **Profile view** volume, delta or bid/ask (sides only from observed or measured rows). The older **Session volume profile** and **Composite Volume Profile** entries stay in the library and saved charts open unchanged. |
| **Profile merging** (Volume profile and Composite Volume Profile) | Profiles join **head to tail**: the next period is appended to the previous one, rows are summed exactly (volume conserved) and POC/value area are recomputed with the same deterministic ties (lower-price POC, upper value-area expansion). Manual: right-click a profile → *Profiles: mark this profile as the merge start*, then right-click a later profile → *Profiles: merge from the marked start to this profile*; *split* and *remove all manual merges* undo it. Manual merges are saved as UTC period-start ranges in **Manual merges** (editable text), so they survive timeframe changes. Automatic: **Automatic merge rule** joins the next period when its POC is inside the current value area, or when the two **overlap** by at least **Minimum overlap to merge (%)** of the smaller value area or full range; automatic merging never crosses a manual block. Merged profiles show *Merged · N periods*. |
| **VWAP suite** | One entry hosting the existing VWAP studies as parts — anchored VWAP with bands, VWAP Claire, multi-period, prior and closing levels, rolling and session-window VWAP. Each part is switched on in **Parts**, configured in **Part settings**, keeps its own coverage labels and series identities, and is not calculated while off. The individual studies remain available for saved charts. |
| **Orderbook Delta** | Recorded-depth bid/ask, delta, imbalance and change; explicit sample-average, duration-average, last or maximum semantics; tick distance or a mid-relative 0–X % band. See [Depth fidelity](./datos.md#recorded-depth-and-heatmap-fidelity). |
| **Order book changes** / **Large book orders** | Recorded snapshot differences per price (added, filled by observed footprint, pulled) and large-level pull/chase **heuristics**; gaps and stale intervals are skipped and counted. See [Book-change studies](./graficos.md#book-change-studies). |

Raw prints, measured compact buckets, synthetic OHLC and inferred aggressor sides remain distinct through the SDK. Measured compact rows may support an explicitly aggregated profile; they are not individual Big Trades or observed event-complete footprints. Estimated OHLC opt-ins remain visible. Repeated forming-bar replacement must replace the prior contribution rather than add it again.

Large cumulative-print history waits for the real chart aggregation prefix, including a bounded initial 100,000-row build, to catch up before being handed to studies. Continuations finish the queued handoff without mapping unconsumed later prints onto the last prefix bar; matching live/forming tail identity is retained. History/live corrections and adaptive thresholds replace prior contributions rather than duplicating them.

**Big Trades adaptive thresholds** use the same causal rolling recent-window percentile for historical replay and live updates. The provisional open group is excluded from completed-sample ranks; a rejected/inferred group rolls back its contribution and restores any evicted completed sample. Historical bubbles are not recomputed from a future whole-history distribution. Compact/synthetic/uncertain-size data is not silently presented as individual aggressive orders.

Completed-study cache defaults are **64 entries / 8 MiB**. Dedicated disk cache is explicit opt-in; keys bind feed/instrument/provider/source, corrected distributions/revision, quality/resolution/availability, session/time-zone and study configuration. Checksum/schema/key/size failures fall back to real computation, not another instrument's cached result or a claimed cache hit.

**Closed profiles across timeframes.** A completed source period of the Volume / Composite profile (and the naked-POC sources) is published to a process-wide, memory-only cache (1,024 entries / 64 MiB) and restored when another timeframe of the **same loaded history** calculates the same period, without re-reading its footprint rows. The key binds provider and instrument, the history identity and its correction revision (any history correction or replacement misses), the period bounds, footprint row size, grouping, value-area and estimate/inferred-side settings, combined coverage and resolution, and conserved volume/bid/ask/trade totals. Reuse only applies when every bar of the period lies inside it, no estimated OHLC rows are involved and inferred-side handling cannot differ by bar; per-timeframe coverage counters are always recomputed. The first loaded period and the developing period are always calculated.

**Neighbouring timeframes in the background.** For histories of at least 200,000 prints, a time-based chart on the 1m/5m/15m/1h/4h/1d ladder builds the next longer and shorter timeframe on a background thread after each rebuild, so switching to them restores the aggregation instead of re-reading the history on the UI thread. The worker reads only prints captured before it started, stops on correction/replacement, is cancelled by the next rebuild and joined when the chart closes, and only fills free space in the bounded candle cache (it never evicts a series you used). The `kvp-performance` scene records the UI frame delay during these builds and the switch cost.

## Order-flow, book and derivatives library

These studies sit in the **Order flow**, **Order book** and **Derivatives** library groups. They reuse the aggregate's per-market source identities, recorded depth and the public derivatives service; nothing here is a trading signal. Estimates and heuristics say so in their names and legends, and missing data is drawn as a gap, never as zero.

| Study | Group | What it shows and its boundary |
|---|---|---|
| **Flow by exchange (ranked, spot / perpetual)** | Order flow | Cumulative net aggressor notional (market buys − market sells, price × size) per exchange of an aggregate, ranked by traded notional over the visible bars, 15 minutes, 1 hour or 24 hours; spot (blue) and perpetual (amber) lines per row, an all-markets row with a spot/perpetual disagreement note, and 1/5/15/60-minute and trade-size lamps. Prints of an unknown market are reported as unattributed and never assigned to an exchange; bars without measured prints break the lines. |
| **Bar statistics strip** | Order flow | One column per bar: volume, measured delta, CVD (per visible range or session), delta %, buy/sell volume and buy %, volume change, relative volume, most bought/sold at one price, POC, diagonal imbalances and stacks, trade counts, average trade, retail and whale delta/CVD by trade-size band, range, range %, session-VWAP distance, resting book net (0.1–2.5 %), observed liquidations, open-interest change and funding. Unmeasured delta, bars without individual prints (compact packets are not trades), unrecorded depth and unobserved derivatives are empty cells. |
| **NL/NS: new longs and shorts (estimate)** | Derivatives | Binance + Bybit open-interest change per bar classified by measured aggressor delta (rising OI with buying = new longs, with selling = new shorts; falling OI = closes) or by price/OI quadrants with a regime box. Bars without both OI cutoffs or a measured delta are gaps. |
| **Liquidity tracker (distance-weighted book)** | Order book | Recorded resting bid/ask notional weighted by 2^(−distance / half-life) in basis points from each side's best price, as two lines or an imbalance; per-level notional filters. Needs depth recording. |
| **Book mirror** | Order book | On hover, compares the book between the mid and the mouse with the equally wide band on the other side (live book, or the latest recorded snapshot). |
| **Liquidation levels (third-party estimate)** | Derivatives | Hyperliquid BTC liquidation levels from a third-party service, drawn right of the last bar; bins price already traded through are removed and snapshots older than three hours are not drawn. **Off until enabled in Settings → Third-party data** (see [Privacy](./privacidad.md#network-access)); BTC only. The three largest bins per side are alertable levels. |
| **Print absorption (heuristic)** | Order flow | One-sided market prints at one price inside a millisecond window whose notional reaches a fixed or automatic threshold. Uses individually observed prints only; not proof of icebergs. |
| **Rejected aggression (trapped traders heuristic)** | Order flow | A closed bar whose wick holds more net aggression than any equal stretch of the bar and which closed at least N ATR beyond those aggressors' average price; the average is an alertable level until price closes back beyond it. Needs an observed footprint. |
| **Highlights and notifications** | Order flow | Bars whose volume or measured delta exceeds mean + k deviations of the preceding bars. Optional notifications: highlighted bar close, 10-second flow burst, walls appearing/pulled within 1 %, book balance flips. |
| **Spot versus perpetual flow dominance** | Order flow | Per-bar spot and perpetual delta of an aggregate, each as a z-score against its own preceding bars (twelve needed). Unattributed prints are excluded rather than assigned to perpetuals. |
| **Decision board** | Order flow | Six factors (close vs EMA, RSI, volume vs mean, measured aggressor flow, OI change, long share of observed liquidations) with the lean and a 64-bar net-score line. A factor without data shows "—" and does not count. A summary, not a recommendation. |
| **Delta with deviation bands** | Order flow | Measured delta per bar or rolling, base units or notional, with ±1/2/3 σ bands of recent available bars. |
| **Volume concentration bubbles** | Order flow | High-volume bars (three tiers above the preceding mean) with bubbles at the prices where most traded, colored by the dominant side. Bars without a footprint get no bubble. |
| **Footprint imbalance alerts** | Order flow | Longest buying/selling imbalance stack and the large cell of each observed footprint, each also a series holding its price for "greater than 0" alerts. |
| **Liquidity imbalance oscillator** / **Order book pressure** | Order book | Recorded book imbalance between two mid-relative distances (−100…100, smoothed), and per-bar 1/2.5/5/10 % pressure bands. Bars without a recorded book stay blank. |
| **Leverage liquidation bands (reference)** | Derivatives | Liquidation prices of a long/short opened at each bar's price at five leverages, ignoring maintenance margin and fees. |
| **Liquidation map (estimate from open interest)** | Derivatives | A model: OI increases split by measured buy/sell volume over a fixed leverage mix, minus levels price already traversed; reports how many bars were modelled and skipped. |
| **Liquidation bursts** | Derivatives | Observed long/short liquidation notional per bar with bubbles above the norm; only observed stream windows are drawn. |

Existing studies cover the rest of the reference behavior: **Cumulative delta (CVD)** now joins the two CVD swings of its latest confirmed divergences with a dashed line (**Connect the two CVD swings…**, 0 turns it off); per-candle absorption is the low-impact heuristic of **Delta bubbles**; **Big Trades** adds a notional fixed threshold, **At most per bar**, a **Whale** ring, drawing at the bar center and size shown as notional.

Notional throughout these studies is price × size × the instrument's contract multiplier (its point value: 50 for an ES contract, 1 for spot and linear crypto), in the quote currency, so trade-size bands, thresholds and book notional are comparable between futures and crypto.

Notifications of these studies go through the chart's alert path into the Alert Center (sound and external destinations follow your alert settings). They fire only for live events less than 30 seconds old, never for loaded history, recalculation or replay, and at most four per ten seconds.

## Built-in source, editable forks and Pine review

Use the library's **View source / edit** action or right-click **View source…**. An exact built-in type opens its embedded source read-only. **Make editable copy** (or right-click **Edit a copy…**, which reopens the existing copy if there is one) checks a Roslyn semantic type/dependency closure, preflights compilation without executing it and publishes a separate user type only after complete success. Unsupported dependencies are rejected before any partial source copy. Original resources, authorship, license notices, existing custom files and saved type identities remain unchanged; the original stays in the library and in saved charts.

Each copy folder holds a `.source-copy.json` record of the original source documents it came from and of the files as generated. At startup, after an update:

- an **untouched** copy whose original changed is regenerated from the new original under the **same type name** (charts using it keep working), but only after the new copy compiles with your other sources; the status bar lists the refreshed copies;
- an **edited** copy (changed, added or removed files) whose original changed is never modified: an in-app notice appears and the library shows **original updated**. Right-click it to **Keep my edits and stop reminding me**, or to restore the new original; restoring moves your whole previous copy to `Indicator copy backups` next to the indicator folder first.

Copies made before this tracking existed have no record and are left alone.

Custom source supports save/recompile, conflict-aware filename rename and explicit deletion. Renaming the file does **not** rename its C# type or rewrite chart/template identities. The owned recursive exact-content monitor sees atomic saves and same-size/timestamp-preserved changes, debounces edits and retains the last-good catalog after compiler errors. Hiding the main window retains hot reload; true shutdown cancels and drains it without native watcher startup.

**Import Pine** preserves the original bytes under collision-safe names. Importing, navigating and opening the source/request/instructions preview send nothing. A separate cancel-default **Send reviewed source** confirmation hands exactly the reviewed source to the selected Claude/Codex CLI in a fresh draft-only workspace, without strategy/indicator-directory, laboratory or research access. The CLI/provider may add its own account/system context and apply its own billing/retention rules. The response remains text: no automatic C# save, compile or execution. This is AI-assisted drafting, not a Pine compiler/runtime, equivalence guarantee or arbitrary-code sandbox.
