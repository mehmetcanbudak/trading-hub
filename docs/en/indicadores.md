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

Open **Code → Indicator editor** (⇧⌘E) or the library code button. New creates a class-named template; **Save and compile** (⌘S / ⌘B) recompiles. Click diagnostics to jump to their line; the folder button opens Finder.

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

Imported authors' labels, generated commentary or custom user overrides may use their original language; they are not rewritten destructively. The host's SDK defaults and built-in interface are English.

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
