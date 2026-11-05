[← Documentation index](README.md)

# Indicators

Open **Indicators** from the chart toolbar or **Chart → Indicators** (⌘I). Search the library by name/category and double-click or Add to chart. Select an active indicator to edit properties. Templates save/apply settings and choose defaults for new instances; see [Charts](graficos.md#indicators).

Strategy indicator factories such as Sma/Ema/Rsi are a separate, simpler [API](estrategias.md#indicators).

## Included indicators

| Indicator | Category | Main options |
|---|---|---|
| Moving average | Trend | Period, simple/exponential/weighted method, source. |
| Bollinger Bands | Trend | Period and deviation multiplier. |
| VWAP with bands | Volume / Order flow | Session/week/month reset and up to three volume-weighted deviation bands. |
| Volume | Volume / Order flow | Color by delta or candle direction. |
| Delta | Volume / Order flow | Histogram or candles showing intrabar delta high/low. |
| Cumulative delta (CVD) | Volume / Order flow | Candles with optional session reset. |
| Big Trades | Volume / Order flow | Combine fills of large aggressive orders across levels; fixed/adaptive percentile thresholds, price placement, colors and sizes. |
| Session volume profile | Volume / Order flow | Session/week/visible history, volume/delta/bid-ask, value-area percentage, POC, VAH/VAL. |
| RSI | Oscillators | Period. |
| MACD | Oscillators | Fast/slow averages and signal. |

Built-ins come from TradingHub.Indicators.Native and are grouped by category.

### Technical indicators

TradingHub.Indicators.Technical supports locally importing the public ATAS technical collection, roughly 250 indicators. They are **not bundled** because public source access does not grant unrestricted redistribution. The repository supplies only necessary compatibility/editor types. See [Import ATAS](#import-atas-indicators). Available imported indicators appear in their categories.

## Custom indicators

- Place C# files in `~/Documents/Trading Hub/Indicadores`. Subfolders are supported; bin/obj are ignored. The initial example is `DeltaPercentageExample.cs`, displayed as **Example: delta % per bar**.
- File create/save/delete/rename triggers recompilation after about 0.6 seconds without changes; **Code → Recompile indicators** forces it.
- Files compile together. On failure the last successful assembly remains available and the first error is shown. Success reports compile duration.
- Custom indicators appear first in **My indicators**, with a star/custom badge. Remove and re-add an already loaded instance to use updated code.

### Editor

Open **Code → Indicator editor** (⇧⌘E on macOS, Ctrl+Shift+E on Windows) or the library code button. New creates a class-named template; **Save and compile** (⌘S / ⌘B; Ctrl+S / Ctrl+B on Windows) recompiles. Click diagnostics to jump to their line; the folder button opens the platform file manager.

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

The [Claude assistant](asistente-claude.md) can also write indicators into this directory. **Untrusted C# executes in-process, not in a sandbox.**

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

These selected studies complement, rather than replace, the existing moving average and **Session volume profile**. The [dashboard](TRANSLATION_STATUS.md) distinguishes the accepted non-KVP runtime from the separate KVP capability candidate and its actual validation evidence.

- **QuanTAlib moving average** and the ribbon offer SMA, EMA, WMA, HMA, ALMA, JMA, KAMA, T3, ZLEMA, DEMA, TEMA, VIDYA and SuperSmoother. Type, Period, Source and Show warmup control display; readiness still follows library `IsHot`. Showing warmup does not make a value ready. Genuine zero/negative results remain distinct from unavailable values. Latest-bar replacement updates streaming state; arbitrary historical edits replay the series.
- **QuanTAlib Camarilla** supplies PP and H1–H6/L1–L6 from the previous **observed** session. Initial loaded history may begin partway through a session; no levels are invented before rollover. Select Chart session, CME (18:00 New York), or Custom session with a valid time zone/start hour.
- **Anchored Flow VWAP** selects Manual, Swing or VolumeSpike anchor modes. Manual UTC uses `yyyy-MM-dd HH:mm` (empty means loaded-history start). Swing anchors become known only after the configured closed bars on each side; primary VWAP/band series are **causal and never backfilled into earlier strategy bars**. Optional **Retrospective chart lines** preserve the visual pivot-to-confirmation segment as drawing-only data; `RetrospectiveValueAt` must be interpreted with `RetrospectiveConfirmationAt`, not as an earlier live signal. Volume spikes compare preceding bars, not a baseline contaminated by the candidate. Weight selects AbsoluteDelta, Volume or AbsolutePriceNotional (absolute price × volume).
- **Refill Zones (heuristic)** uses Minimum bucket volume, Floor-price bucket size, Absorption only and Project until mitigation. It is an absorption/refill heuristic, **not proof of book replenishment or icebergs**. Optional estimated OHLC uses threshold × 0.4 and at most 200 buckets.
- **Composite Volume Profile** uses host Session/Week/Month boundaries, nearest-price grouping (ties away from zero), Value area (%) and optional chronological composition when the next POC is inside the current value area. Month is session-based, not simply a UTC date label. Equal-volume POCs choose the lower price; equal adjacent value-area volume expands upward first. POC projection uses a 60-profile lookback with up to 10 tested POCs; untested projections follow their configured limit (zero means all within the finite lookback).
- **Open interest** shows server-observed OI and adjacent-known-cutoff signed change. See [Data fidelity](datos.md#open-interest-observation-fidelity) for retention, freshness and missing-value rules.

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
| **Orderbook Delta** | Recorded-depth bid/ask, delta, imbalance and change; explicit sample-average, duration-average, last or maximum semantics. See [Depth fidelity](datos.md#recorded-depth-and-heatmap-fidelity). |

Raw prints, measured compact buckets, synthetic OHLC and inferred aggressor sides remain distinct through the SDK. Measured compact rows may support an explicitly aggregated profile; they are not individual Big Trades or observed event-complete footprints. Estimated OHLC opt-ins remain visible. Repeated forming-bar replacement must replace the prior contribution rather than add it again.

Large cumulative-print history waits for the real chart aggregation prefix, including a bounded initial 100,000-row build, to catch up before being handed to studies. Continuations finish the queued handoff without mapping unconsumed later prints onto the last prefix bar; matching live/forming tail identity is retained. History/live corrections and adaptive thresholds replace prior contributions rather than duplicating them.

**Big Trades adaptive thresholds** use the same causal rolling recent-window percentile for historical replay and live updates. The provisional open group is excluded from completed-sample ranks; a rejected/inferred group rolls back its contribution and restores any evicted completed sample. Historical bubbles are not recomputed from a future whole-history distribution. Compact/synthetic/uncertain-size data is not silently presented as individual aggressive orders.

Completed-study cache defaults are **64 entries / 8 MiB**. Dedicated disk cache is explicit opt-in; keys bind feed/instrument/provider/source, corrected distributions/revision, quality/resolution/availability, session/time-zone and study configuration. Checksum/schema/key/size failures fall back to real computation, not another instrument's cached result or a claimed cache hit.

## Built-in source, editable forks and Pine review

Use the library's **View source / edit** action. An exact built-in type opens its embedded source read-only. **Make editable copy** checks a Roslyn semantic type/dependency closure, preflights compilation without executing it and publishes a separate user type only after complete success. Unsupported dependencies are rejected before any partial source copy. Original resources, authorship, license notices, existing custom files and saved type identities remain unchanged.

Custom source supports save/recompile, conflict-aware filename rename and explicit deletion. Renaming the file does **not** rename its C# type or rewrite chart/template identities. The owned recursive exact-content monitor sees atomic saves and same-size/timestamp-preserved changes, debounces edits and retains the last-good catalog after compiler errors. Hiding the main window retains hot reload; true shutdown cancels and drains it without native watcher startup.

**Import Pine** preserves the original bytes under collision-safe names. Importing, navigating and opening the source/request/instructions preview send nothing. A separate cancel-default **Send reviewed source** confirmation hands exactly the reviewed source to the selected Claude/Codex CLI in a fresh draft-only workspace, without strategy/indicator-directory, laboratory or research access. The CLI/provider may add its own account/system context and apply its own billing/retention rules. The response remains text: no automatic C# save, compile or execution. This is AI-assisted drafting, not a Pine compiler/runtime, equivalence guarantee or arbitrary-code sandbox.
