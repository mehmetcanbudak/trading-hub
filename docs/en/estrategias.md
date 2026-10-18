[← Documentation index](README.md)

# C# strategies

A Trading Hub strategy is a C# class derived from `Strategy`. The same class runs in backtests, optimization, walk-forward, chart overlays and live accounts. This guide covers the API and two complete examples.

## Files and compilation

- Files live in `~/Documents/Trading Hub/Estrategias`. Open the folder from Backtesting's strategy menu, Code, or Settings → Data.
- An empty folder receives Moving-average crossover, Range breakout, Delta absorption and VWAP mean-reversion examples. The app compiles them in memory using Roslyn.
- Compile with **Compile** (⌘B), **Compile and test**, when opening a strategy, after Claude edits, or before first use. **Save** (⌘S) writes the file only: after editing, compile before backtesting, which otherwise uses the last compiled version.
- Use a public, nonabstract `Strategy` subclass with a parameterless constructor and `using TradingHub.Strategies;`. System, System.Linq and System.Collections.Generic are implicit. Prefer one class per same-named file. History is keyed by class name: renaming creates a different history identity.
- Compilation applies the same ATAS namespace adaptations and `TRADINGHUB` / `CROSS_PLATFORM` symbols as [Indicators](indicadores.md).

## Parameters

- `[Strategy("Display name", Description = "…")]` supplies class metadata.
- `[Parameter("Label", Description = "…")]` on public read/write properties exposes them in backtest, live and chart settings. Supported types include int, long, double, decimal, bool, enums and string.
- `[Optimize(min, max, step)]` preselects a numerical range for optimization/walk-forward. Other numerical properties can also be enabled manually.

Common management properties can be initialized in the constructor:

| Property | Display | Default | Effect |
|---|---|---|---|
| `Quantity` | Quantity | 1 | Default order size when omitted. |
| `StopLossTicks` | Stop loss (ticks) | 0 | Protective stop distance from average entry. |
| `TakeProfitTicks` | Take profit (ticks) | 0 | Target distance. |
| `TrailingStopTicks` | Trailing stop (ticks) | 0 | Stop follows favorable price movement. |
| `RegimeFilter` | Regime filter | All | Restrict conditions for new exposure. |
| `MinVolatilityRank`, `MaxVolatilityRank` | Minimum / maximum volatility percentile | 0 / 100 | Restrict volatility conditions. |

Property identifiers—not translated labels—are used in saved parameter dictionaries. Keep identifiers stable when editing existing scripts.

## Lifecycle

| Override | Called |
|---|---|
| `OnInit()` | At startup; create indicators and enable `UseFootprint` if required. |
| `OnBar(Bar bar)` | After each completed bar; usual decision point. |
| `OnTick(Trade trade)` | Every market trade; slower. `Trade` is in TradingHub.Market and has Time, Price, Volume and Side. |
| `OnOrderFilled(StrategyOrder order)` | After a fill. Backtests include automatic stops/targets; live callbacks reflect the strategy's submitted orders. |
| `OnSessionStart(DateOnly session)` | At subsequent session starts, not the first session in the dataset. |
| `OnStop()` | At backtest end or when stopped. |

Context and indicators update before OnBar. The first trade of the next bar closes the previous bar; an OnBar market order fills against that trade, whereas an OnTick submission waits for the next trade.

LocalTime, TimeBetween and session-day calculations use the backtest's session time zone/start. Live strategies inherit their last backtest settings. Chart strategies use the configured chart time zone and midnight session start; when that setting is the Mac's local time, the chart-strategy fallback is UTC.

## Bar and market data

- `Bars[0]` is the latest closed bar, `Bars[1]` the previous one; `Bars.At(i)` counts from the beginning. Also available: Bars.Count, Bar, FormingBar and zero-based CurrentBar.
- A Bar provides Time and CloseTime (UTC), Open, High, Low, Close, Volume, BuyVolume, SellVolume, Delta, MaxDelta, MinDelta, Trades, IsBullish, IsBearish, Range, Body, Typical and Index. `PocPrice` and per-price `Levels` require `UseFootprint = true` in OnInit.
- Market properties: Price, UTC Time, session LocalTime, TickSize, Instrument (including Symbol and PointValue), Period and IsBacktest.
- Position properties: signed Position, IsFlat/IsLong/IsShort, AveragePrice, OpenPnL, realized NetProfit and ActiveOrders.

## Orders

| Method | Effect |
|---|---|
| `EnterLong(quantity?, name?)` / `EnterShort(…)` | Enter at market; reverse the opposite position, do nothing if already on the requested side. |
| `Buy(quantity?, name?)` / `Sell(…)` | Market buy / sell. |
| `BuyLimit(price, quantity?, name?)` / `SellLimit(…)` | Limit order. |
| `BuyStop(price, quantity?, name?)` / `SellStop(…)` | Stop-triggered market order. |
| `ClosePosition(name?)` | Cancel pending entry orders and close at market. |
| `CancelAll()` / `Cancel(order)` / `Modify(order, price)` | Cancel pending orders, cancel one, or change a limit/stop price. |

Omitted quantity uses Quantity; prices round to tick size. StrategyOrder exposes Id, Side, Type, Quantity, Price, Name, State (Active/Filled/Canceled), IsActive, FillPrice, FillTime and IsProtective. Exit names appear in backtest reasons and live broker comments. See [fill simulation](backtesting.md#what-a-backtest-simulates).

## Automatic stop, target and trailing

Positive StopLossTicks/TakeProfitTicks create protective orders at those distances from average entry whenever position size changes. They act as OCO: filling one cancels the other. TrailingStopTicks advances a stop only favorably; without StopLossTicks it supplies the initial stop distance.

ClosePosition/CancelAll do not directly manage those automatic protective orders; protection is cleared when the position becomes flat. Live protective orders are submitted to the account, but **Trading Hub manages their OCO relationship**. Live trailing updates are throttled to at most once per second. Loss of app/connectivity can therefore affect protection management.

## Indicators

Create indicators in OnInit and retain them in fields; the app updates them before OnBar.

| Factory | Result |
|---|---|
| `Sma(n, source)` / `Ema(n, source)` | Simple / exponential average; EMA seeds with SMA. |
| `Rsi(n = 14, source)` / `Atr(n = 14)` / `StdDev(n, source)` | RSI, average true range in price units, standard deviation. |
| `Highest(n, source = High)` / `Lowest(n, source = Low)` | Rolling maximum/minimum including the latest bar. |
| `Bollinger(n = 20, k = 2, source)` | Middle, Upper, Lower, UpperAgo(i), LowerAgo(i). |
| `Macd(12, 26, 9)` | MACD, Signal and Histogram. |
| `Vwap()` / `CumulativeDelta(resetEachSession = true)` | Session VWAP / cumulative delta. |
| `Custom("name", bars => value)` | A custom per-bar series. |

PriceSource supports Close (default), Open, High, Low, Typical, Median, Volume and Delta. `indicator[0]` is latest, `[1]` previous; Value, Count and IsReady are available, and a series converts to decimal. Since Highest/Lowest include the current bar, compare breakout prices against `[1]`. Price/volume arithmetic is decimal; cast double values explicitly.

## Helpers

- CrossAbove/CrossBelow compare a series against another series or a number on the last closed bar. Ticks(n) converts ticks to price; Round(price) rounds to tick size.
- TimeBetween(from, to) uses session-local TimeSpan values and handles overnight intervals when from > to.
- Log writes to the backtest log (up to 5,000 UTC-timestamped lines) and live Activity.

## Chart drawings

Drawings appear on historical and live chart strategies. Times are UTC. Colors accept `#RRGGBB` / `#AARRGGBB`; existing Spanish aliases (`verde`, `rojo`, `azul`, `celeste`, `amarillo`, `naranja`, `violeta`, `rosa`, `gris`, `blanco`) remain supported for old scripts. Omitted colors use the strategy color. Reusing an ID replaces its drawing. Limits are 3,000 drawings (oldest removed) and 20 Plot lines.

| Method | Drawing |
|---|---|
| `DrawBox(id, from, price1, to, price2, color, fillOpacity = 15, text)` | Rectangle. |
| `DrawLine(id, t1, p1, t2, p2, color, width, style)` | Line; LineStyle.Solid, Dashed or Dotted. |
| `DrawHorizontalLine(id, price, from, color, width, style, text)` | Level extended to the right. |
| `DrawArrowUp/DrawArrowDown(id, time, price, color, text)` | Arrow tip at price. |
| `DrawMarker(id, time, price, shape, color, text)` | Circle, Square, Diamond, Cross, ArrowUp or ArrowDown. |
| `DrawText(id, time, price, text, color)` | Text. |
| `Plot("name", value, color, width, style)` | Continuous per-bar line; null creates a gap. |
| `RemoveDrawing(id)` / `ClearDrawings()` | Remove one / all. |

## Market context and context filters

Context uses completed hourly closes over roughly 24 hours; periods of at least one hour use their last 25 bars.

| Property | Meaning |
|---|---|
| MarketTrend | Absolute net movement / total path; 0 is range, 1 is clean trend, random walking is roughly 0.2. |
| Regime | Range at ≤0.15, Trend at ≥0.35, Mixed between, Unknown without data. |
| MarketDirection / MarketChange | +1/−1 direction and net percentage change. |
| MarketVolatility / VolatilityRank | Hourly-return deviation in percent, and percentile against the preceding 30 days. |
| ContextAllowsEntries | Whether the configured context permits entry now. |

Values may be null until enough history exists: seven closes, and at least a day for percentile ranking. Backtests/live runners seed from available prior-month hourly history; live runners also receive exchange bars.

No additional code is needed for RegimeFilter and Min/MaxVolatilityRank. RegimeFilter allows combinations of range, mixed, uptrend and downtrend: All, RangeOnly, NoTrend, TrendOnly, NoRange, UpTrendOnly, DownTrendOnly, MixedOnly, RangeOrUpTrend, RangeOrDownTrend, MixedOrUpTrend, MixedOrDownTrend, NoDownTrend, NoUpTrend and NoMixed.

Outside the filter, orders cannot open/increase exposure: only the closing portion passes, with the remainder canceled and logged. Exits always pass. Missing context does not block. Live filters can change without restarting; see [Live](en-vivo.md).

## Example 1: Moving-average crossover with trading hours

The original examples' class/property identifiers below are retained to preserve existing saved-history and parameter identities. Labels, comments, order descriptions and drawing text are English.

```csharp
using TradingHub.Strategies;

[Strategy("Moving-average crossover with hours", Description = "Trade fast/slow crosses only within configured hours.")]
public class CruceDeMediasHorario : Strategy
{
    [Parameter("Fast average"), Optimize(5, 30, 5)] public int Rapida { get; set; } = 10;
    [Parameter("Slow average"), Optimize(20, 100, 10)] public int Lenta { get; set; } = 40;
    [Parameter("From (local hour)")] public int HoraDesde { get; set; } = 9;
    [Parameter("To (local hour)")] public int HoraHasta { get; set; } = 16;
    private Ema _rapida = null!, _lenta = null!;

    // Protective stop at 40 ticks and target at 80 ticks, managed as OCO.
    public CruceDeMediasHorario() { StopLossTicks = 40; TakeProfitTicks = 80; }
    protected override void OnInit() { _rapida = Ema(Rapida); _lenta = Ema(Lenta); }
    protected override void OnBar(Bar bar)
    {
        if (!_lenta.IsReady) return;
        Plot("Fast", _rapida[0], "#00BFFF");
        Plot("Slow", _lenta[0], "#FFA500");
        if (!TimeBetween(new TimeSpan(HoraDesde, 0, 0), new TimeSpan(HoraHasta, 0, 0)))
        {
            if (!IsFlat) ClosePosition("End of trading hours");
        }
        else if (CrossAbove(_rapida, _lenta)) EnterLong();
        else if (CrossBelow(_rapida, _lenta)) EnterShort();
    }
}
```

## Example 2: Opening-range breakout

Measure the initial range and place a stop on each side. For US index futures, use the New York session zone and a 09:30 opening.

```csharp
using TradingHub.Strategies;

[Strategy("Opening-range breakout", Description = "Trade range breaks with stop orders and flatten at closing time.")]
public class RupturaRangoApertura : Strategy
{
    [Parameter("Opening hour (local)")] public int HoraApertura { get; set; } = 9;
    [Parameter("Opening minute")] public int MinutoApertura { get; set; } = 30;
    [Parameter("Range minutes"), Optimize(15, 60, 15)] public int MinutosRango { get; set; } = 30;
    [Parameter("Closing hour (local)")] public int HoraCierre { get; set; } = 16;
    [Parameter("Range buffer (ticks)"), Optimize(0, 8, 2)] public int Margen { get; set; } = 2;
    private DateOnly _dia;
    private DateTime _inicioRango;
    private decimal _techo, _piso;
    private bool _hayRango, _ordenesPuestas;
    private StrategyOrder? _compra, _venta;

    // With no fixed stop, trailing protection starts 60 ticks away.
    public RupturaRangoApertura() => TrailingStopTicks = 60;
    protected override void OnBar(Bar bar)
    {
        DateTime abrio = bar.Time + (LocalTime - Time);
        var apertura = new TimeSpan(HoraApertura, MinutoApertura, 0);
        var finRango = apertura + new TimeSpan(0, MinutosRango, 0);
        var cierre = new TimeSpan(HoraCierre, 0, 0);
        var ahora = LocalTime.TimeOfDay;
        var dia = DateOnly.FromDateTime(abrio);
        if (dia != _dia)
        {
            CancelAll();
            if (!IsFlat) ClosePosition("Day close");
            (_dia, _techo, _piso, _hayRango, _ordenesPuestas) = (dia, decimal.MinValue, decimal.MaxValue, false, false);
        }
        // Only bars opening inside the initial window define the range.
        if (abrio.TimeOfDay >= apertura && abrio.TimeOfDay < finRango)
        {
            if (!_hayRango) _inicioRango = bar.Time;
            _hayRango = true;
            _techo = Math.Max(_techo, bar.High);
            _piso = Math.Min(_piso, bar.Low);
            DrawBox($"rango-{dia:yyyyMMdd}", _inicioRango, _techo, bar.CloseTime, _piso, "#0000FF", 15, "Range");
        }
        if (_hayRango && !_ordenesPuestas && ahora >= finRango && ahora < cierre && IsFlat)
        {
            _compra = BuyStop(_techo + Ticks(Margen), name: "Break above");
            _venta = SellStop(_piso - Ticks(Margen), name: "Break below");
            _ordenesPuestas = true;
            Log($"Daily range: {_piso} to {_techo}");
        }
        bool niveles = _ordenesPuestas && ahora < cierre;
        Plot("Range high", niveles ? _techo : (decimal?)null, "#00FF00");
        Plot("Range low", niveles ? _piso : (decimal?)null, "#FF0000");
        if (ahora >= cierre) { CancelAll(); if (!IsFlat) ClosePosition("Day close"); }
    }
    protected override void OnOrderFilled(StrategyOrder order)
    {
        if (order != _compra && order != _venta) return;
        var otra = order == _compra ? _venta : _compra;
        if (otra is { IsActive: true }) Cancel(otra);
        if (order.Side == OrderSide.Buy) DrawArrowUp($"in-{order.Id}", order.FillTime, order.FillPrice, "#00FF00", "Breakout");
        else DrawArrowDown($"in-{order.Id}", order.FillTime, order.FillPrice, "#FF0000", "Breakout");
    }
}
```

`bar.Time + (LocalTime - Time)` converts bar opening time to the session zone, avoiding dependence on when the next bar's first trade arrives. The two stops wait for a break; OnOrderFilled cancels the opposite entry. Trailing and the session close manage the position.

## Editor: Backtesting → Code

Select/search/favorite a strategy or create one from Moving-average crossover, Range breakout, Delta absorption, VWAP mean-reversion or Empty templates. The star adds/removes a favorite without closing the list. Compilation status shows duration or error count.

The C# editor highlights code and lists clickable diagnostics. Controls: Save (⌘S), Compile (⌘B), Compile and test, API reference and Delete. Deleting a source file retains its backtest history. The Claude panel appears alongside; its changes reload in the editor.

## Ask Claude to write it

Describe a strategy, for example an opening 30-minute range breakout with an ATR stop. Claude creates/edits the strategy file; compilation follows. Successful compilation offers **Test in backtest**; errors offer **Ask Claude to fix**. Laboratory mode additionally runs tests/optimizations/walk-forward and creates a report. See [Claude](asistente-claude.md).

Imported C# executes in-process without a security sandbox. Review untrusted strategies/indicators before compiling or running them.
