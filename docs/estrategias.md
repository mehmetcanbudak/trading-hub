[← Volver al índice](./README.md)

# Estrategias en C#

Una estrategia de Trading Hub es una clase de C# que hereda de `Strategy`. La misma clase, sin cambios, corre en el
backtest, en la optimización y el walk-forward, puesta en un gráfico y en vivo en una cuenta. Acá está la API completa
y dos ejemplos que compilan tal cual.

## Dónde viven y cómo se compilan

- Cada estrategia es un archivo `.cs` en `~/Documents/Trading Hub/Estrategias`. La carpeta se abre desde el menú de
  estrategias de **Backtesting** («Abrir carpeta de estrategias»), desde el menú **Código** o desde **Ajustes → Datos**.
- Con la carpeta vacía, Trading Hub deja cuatro ejemplos: *Cruce de medias*, *Ruptura de rango*, *Absorción con delta*
  y *Reversión a la VWAP*. Las compila la propia app con Roslyn, en memoria: no necesitás nada más.
- Se compilan al tocar **Compilar** (⌘B) o **Compilar y probar**, al abrirlas en el editor, cuando Claude termina de
  editarlas y cuando vas a usar una que todavía no se había compilado. **Guardar** (⌘S) solo escribe el archivo: si
  cambiaste el código, compilá antes de correr un backtest, porque se usa la última versión compilada.
- Hace falta una clase pública, no abstracta, que herede de `Strategy`, con constructor sin parámetros y
  `using TradingHub.Strategies;` (`System`, `System.Linq` y `System.Collections.Generic` ya están). Usá una por archivo,
  con su mismo nombre: el historial de backtests se guarda con el nombre de la clase y, si la renombrás, empieza otro.
- Al compilar se aplican las mismas adaptaciones que a los indicadores (namespaces de ATAS y los símbolos `TRADINGHUB`
  y `CROSS_PLATFORM`): ver [Indicadores](./indicadores.md).

## Parámetros

- `[Strategy("Nombre visible", Description = "…")]` sobre la clase: el nombre y la descripción que muestra la app.
- `[Parameter("Nombre", Description = "…")]` sobre una propiedad pública con `get` y `set`: aparece en el formulario de
  parámetros del backtest, de En vivo y del gráfico. Tipos: `int`, `long`, `double`, `decimal`, `bool`, enums y `string`.
- `[Optimize(mín, máx, paso)]`, además: entra marcada en la optimización y el walk-forward (igual podés marcar a mano
  cualquier parámetro numérico).

Todas las estrategias traen estos parámetros de gestión, que podés fijar en el constructor:

| Propiedad | En la app | Por defecto | Qué hace |
|---|---|---|---|
| `Quantity` | Cantidad | 1 | Cantidad de cada orden si no pasás otra |
| `StopLossTicks` | Stop loss (ticks) | 0 | Stop automático a N ticks del precio promedio |
| `TakeProfitTicks` | Take profit (ticks) | 0 | Objetivo automático a N ticks |
| `TrailingStopTicks` | Trailing stop (ticks) | 0 | Stop que acompaña al precio a N ticks |
| `RegimeFilter` | Filtro de régimen | Todas | Solo abre en ciertas condiciones de mercado |
| `MinVolatilityRank`, `MaxVolatilityRank` | Volatilidad mínima / máxima (percentil) | 0 y 100 | Solo abre con esa volatilidad |

## Ciclo de vida

| Método | Cuándo se llama |
|---|---|
| `OnInit()` | Al arrancar. Creá acá los indicadores (y poné `UseFootprint = true` si usás el volumen por precio). |
| `OnBar(Bar bar)` | Al cerrar cada vela. Es el lugar normal para decidir. |
| `OnTick(Trade trade)` | En cada operación del mercado. Es más lento. Necesita `using TradingHub.Market;` (`Trade` tiene `Time`, `Price`, `Volume` y `Side`). |
| `OnOrderFilled(StrategyOrder order)` | Al ejecutarse una orden. En el backtest también avisa los stops y objetivos automáticos; en vivo, solo las órdenes que mandó la estrategia. |
| `OnSessionStart(DateOnly session)` | Al empezar cada sesión nueva (no se llama para la primera sesión de los datos). |
| `OnStop()` | Al terminar el backtest o al detener la estrategia. |

En cada vela se actualizan primero el contexto de mercado y los indicadores, y después se llama a `OnBar`. La vela se
da por cerrada con la primera operación de la siguiente, y una orden a mercado enviada en `OnBar` se llena con esa
operación (desde `OnTick`, con la que sigue). La hora local (`LocalTime`, `TimeBetween`, el día de cada sesión) sale de
la **zona y el inicio de sesión** del backtest; en vivo, de los del último backtest de la estrategia, y en un gráfico,
de **Ajustes → Zona horaria de los gráficos** con la sesión a las 0 h (si ahí está «Hora de esta Mac», se usa UTC).

## Datos de la vela y del mercado

- `Bars[0]` es la última vela cerrada y `Bars[1]` la anterior (`Bars.Count`; `Bars.At(i)` cuenta desde la primera);
  `Bar` es la última cerrada, `FormingBar` la que se está formando y `CurrentBar` su número, desde 0.
- Cada `Bar` trae `Time` (apertura, UTC), `CloseTime` (su última operación, UTC), `Open`, `High`, `Low`, `Close`,
  `Volume`, `BuyVolume` y `SellVolume` (agresores contra el ask y contra el bid), `Delta`, `MaxDelta`, `MinDelta`,
  `Trades`, `IsBullish`, `IsBearish`, `Range`, `Body`, `Typical` e `Index`. `PocPrice` (el precio con más volumen) y
  `Levels` (volumen comprador y vendedor de cada precio) necesitan `UseFootprint = true` en `OnInit`.
- `Price` (último precio), `Time` (hora del mercado, UTC), `LocalTime` (la misma hora en la zona de la sesión),
  `TickSize`, `Instrument` (`Symbol`, `TickSize`, `PointValue`…), `Period` e `IsBacktest`.
- Posición: `Position` (positiva = largo, negativa = corto), `IsFlat`, `IsLong`, `IsShort`, `AveragePrice`, `OpenPnL`,
  `NetProfit` (resultado realizado) y `ActiveOrders`.

## Órdenes

| Método | Qué hace |
|---|---|
| `EnterLong(cant?, nombre?)` / `EnterShort(…)` | Entra a mercado. Si estaba del otro lado, da vuelta la posición; si ya estaba de ese lado, no hace nada. |
| `Buy(cant?, nombre?)` / `Sell(…)` | Orden a mercado. |
| `BuyLimit(precio, cant?, nombre?)` / `SellLimit(…)` | Orden límite. |
| `BuyStop(precio, cant?, nombre?)` / `SellStop(…)` | Orden stop: se dispara al llegar al precio y sale a mercado. |
| `ClosePosition(nombre?)` | Cancela las órdenes pendientes y cierra la posición a mercado. |
| `CancelAll()` · `Cancel(orden)` · `Modify(orden, precio)` | Cancela todas las pendientes, cancela una o le cambia el precio (límites y stops). |

Sin cantidad se usa `Quantity`, y los precios se redondean al tick. Cada método devuelve un `StrategyOrder` con `Id`,
`Side`, `Type`, `Quantity`, `Price`, `Name`, `State` (`Active`, `Filled` o `Canceled`), `IsActive`, `FillPrice`,
`FillTime` e `IsProtective` (stop u objetivo automático). El nombre de la orden que cierra aparece en «Salida por» en
el backtest y, en vivo, va en el comentario de la orden. Cómo se simulan los llenados está en [Backtesting](./backtesting.md).

## Stop, objetivo y trailing automáticos

Con `StopLossTicks` o `TakeProfitTicks` mayores a 0, cada vez que cambia la posición se ponen un stop y un objetivo a
esa distancia del precio promedio, unidos como OCO (si se ejecuta uno, se cancela el otro). `TrailingStopTicks` hace que
el stop acompañe al precio cuando va a favor, sin retroceder; si `StopLossTicks` es 0, el stop arranca a esa distancia.
`ClosePosition` y `CancelAll` no tocan estas órdenes: se cancelan solas cuando la posición queda en cero. En vivo son
órdenes reales en la cuenta (el OCO lo maneja Trading Hub) y el trailing mueve el stop como mucho una vez por segundo.

## Indicadores

Se crean en `OnInit` y se guardan en campos; Trading Hub los actualiza solo antes de cada `OnBar`.

| Método | Qué es |
|---|---|
| `Sma(n, fuente)` / `Ema(n, fuente)` | Media simple / exponencial (la exponencial arranca con la simple) |
| `Rsi(n = 14, fuente)` · `Atr(n = 14)` · `StdDev(n, fuente)` | RSI de 0 a 100 · rango verdadero promedio en precio · desvío estándar |
| `Highest(n, fuente = High)` / `Lowest(n, fuente = Low)` | Máximo / mínimo de las últimas n velas, incluida la última |
| `Bollinger(n = 20, k = 2, fuente)` | Media (`Middle`) con `Upper`, `Lower`, `UpperAgo(i)` y `LowerAgo(i)` |
| `Macd(12, 26, 9)` | MACD con `Signal` e `Histogram` |
| `Vwap()` · `CumulativeDelta(resetEachSession = true)` | VWAP de la sesión · delta acumulado |
| `Custom("nombre", bars => valor)` | Una serie propia, calculada en cada vela |

`fuente` es un `PriceSource`: `Close` (por defecto), `Open`, `High`, `Low`, `Typical`, `Median`, `Volume` o `Delta`.
Cada indicador es una serie: `ind[0]` es el valor de la última vela cerrada y `ind[1]` el anterior; también tiene
`Value`, `Count` e `IsReady` (ya tiene velas suficientes), y se puede usar directo como `decimal`. Como `Highest` y
`Lowest` incluyen la última vela, para una ruptura compará el cierre con `[1]`. Precios y volúmenes son `decimal`:
convertí los `double` con `(decimal)`.

## Ayudas

- `CrossAbove(a, b)` / `CrossBelow(a, b)`: la serie `a` cruzó por arriba / por abajo de `b` (serie o número) en la
  última vela. `Ticks(n)`: n ticks en precio. `Round(precio)`: redondea al tick.
- `TimeBetween(desde, hasta)`: si la hora local de la sesión está entre dos `TimeSpan` (si `desde` es mayor, cruza la
  medianoche). `Log("texto")`: va al **Registro** del backtest (hasta 5.000 líneas, hora UTC) y a la **Actividad** en vivo.

## Dibujos en el gráfico

Se ven al poner la estrategia en un gráfico, sobre la historia y en vivo. Los tiempos son UTC (como `bar.Time`) y los
colores, `"#RRGGBB"`, `"#AARRGGBB"` o un nombre (`verde`, `rojo`, `azul`, `celeste`, `amarillo`, `naranja`, `violeta`,
`rosa`, `gris`, `blanco`); sin color va el de la estrategia. Repetir un id reemplaza el dibujo (sirve para estirar un
rango vela a vela). Hay un tope de 3.000 dibujos (se van los más viejos) y de 20 líneas de `Plot`.

| Método | Dibuja |
|---|---|
| `DrawBox(id, desde, precio1, hasta, precio2, color, fillOpacity = 15, text)` | Rectángulo |
| `DrawLine(id, t1, p1, t2, p2, color, width, style)` | Línea (`LineStyle.Solid`, `Dashed` o `Dotted`) |
| `DrawHorizontalLine(id, precio, desde, color, width, style, text)` | Nivel hasta el borde derecho |
| `DrawArrowUp` / `DrawArrowDown(id, tiempo, precio, color, text)` | Flecha con la punta en el precio |
| `DrawMarker(id, tiempo, precio, forma, color, text)` | `MarkerShape.Circle`, `Square`, `Diamond`, `Cross`, `ArrowUp` o `ArrowDown` |
| `DrawText(id, tiempo, precio, texto, color)` | Texto |
| `Plot("nombre", valor, color, width, style)` | Línea continua vela a vela; un valor `null` la corta |
| `RemoveDrawing(id)` / `ClearDrawings()` | Borra uno o todos |

## Contexto de mercado y filtro de contexto

Cada estrategia mide, solo con velas ya cerradas, cómo viene el mercado: con los cierres por hora de las últimas 24
horas o, si sus velas son de una hora o más, con sus últimas 25 velas.

| Propiedad | Qué es |
|---|---|
| `MarketTrend` | Eficiencia de la tendencia: \|movimiento neto\| / recorrido total (0 = rango puro, 1 = tendencia limpia; al azar da ~0,2) |
| `Regime` | `Range` (hasta 0,15), `Mixed`, `Trend` (desde 0,35) o `Unknown` sin datos |
| `MarketDirection` · `MarketChange` | 1 si subió y −1 si bajó (alcista o bajista) · movimiento neto en % |
| `MarketVolatility` · `VolatilityRank` | Desvío de los retornos por hora, en % · su percentil entre los últimos 30 días (0 = la más baja) |
| `ContextAllowsEntries` | Si el filtro de contexto deja abrir ahora |

Pueden ser `null` mientras falta historia (7 cierres; el percentil, al menos un día). El backtest y la estrategia en
vivo arrancan con los cierres por hora del mes anterior que haya guardados (en vivo, además, con las velas del exchange),
así el contexto suele existir desde la primera vela. El **filtro de contexto** no requiere código: son los parámetros
`RegimeFilter`, `MinVolatilityRank` y `MaxVolatilityRank`. `RegimeFilter` admite cualquier combinación de rango, mixto, tendencia alcista y bajista: `All`,
`RangeOnly`, `NoTrend`, `TrendOnly`, `NoRange`, `UpTrendOnly`, `DownTrendOnly`, `MixedOnly`, `RangeOrUpTrend`,
`RangeOrDownTrend`, `MixedOrUpTrend`, `MixedOrDownTrend`, `NoDownTrend`, `NoUpTrend` y `NoMixed`. Fuera de ese contexto
la estrategia no abre ni agranda posiciones: de cada orden pasa solo la parte que cierra lo abierto y el resto vuelve
cancelado («Filtro de contexto: no abre operaciones ahora» en el registro). Las salidas siempre pasan y, sin datos para
juzgar, el filtro no bloquea. En vivo se cambia sin reiniciar: ver [En vivo](./en-vivo.md).

## Ejemplo 1: cruce de medias con horario

```csharp
using TradingHub.Strategies;

[Strategy("Cruce de medias con horario", Description = "Largo cuando la media rápida cruza arriba de la lenta y corto al revés, solo dentro del horario.")]
public class CruceDeMediasHorario : Strategy
{
    [Parameter("Media rápida"), Optimize(5, 30, 5)] public int Rapida { get; set; } = 10;
    [Parameter("Media lenta"), Optimize(20, 100, 10)] public int Lenta { get; set; } = 40;
    [Parameter("Desde (hora local)")] public int HoraDesde { get; set; } = 9;
    [Parameter("Hasta (hora local)")] public int HoraHasta { get; set; } = 16;

    private Ema _rapida = null!, _lenta = null!;

    // stop automático a 40 ticks del precio promedio y objetivo a 80, en OCO
    public CruceDeMediasHorario() { StopLossTicks = 40; TakeProfitTicks = 80; }

    protected override void OnInit() { _rapida = Ema(Rapida); _lenta = Ema(Lenta); }

    protected override void OnBar(Bar bar)
    {
        if (!_lenta.IsReady) return;
        Plot("Rápida", _rapida[0], "celeste"); // las dos medias en el gráfico, vela a vela
        Plot("Lenta", _lenta[0], "naranja");

        // fuera del horario no se queda posicionada ni abre nada
        if (!TimeBetween(new TimeSpan(HoraDesde, 0, 0), new TimeSpan(HoraHasta, 0, 0)))
        {
            if (!IsFlat) ClosePosition("Fin del horario");
        }
        else if (CrossAbove(_rapida, _lenta)) EnterLong(); // si estaba corta, da vuelta la posición
        else if (CrossBelow(_rapida, _lenta)) EnterShort();
    }
}
```

## Ejemplo 2: ruptura del rango de apertura

Mide el rango de los primeros minutos desde la apertura y lo opera con una orden stop de cada lado. Probalo en un
futuro de índices con la zona de la sesión **Nueva York** (apertura 9:30).

```csharp
using TradingHub.Strategies;

[Strategy("Ruptura del rango de apertura", Description = "Mide el rango de los primeros minutos y entra con órdenes stop cuando se rompe. Cierra todo a la hora de cierre.")]
public class RupturaRangoApertura : Strategy
{
    [Parameter("Hora de apertura (local)")] public int HoraApertura { get; set; } = 9;
    [Parameter("Minuto de apertura")] public int MinutoApertura { get; set; } = 30;
    [Parameter("Minutos del rango"), Optimize(15, 60, 15)] public int MinutosRango { get; set; } = 30;
    [Parameter("Hora de cierre (local)")] public int HoraCierre { get; set; } = 16;
    [Parameter("Margen sobre el rango (ticks)"), Optimize(0, 8, 2)] public int Margen { get; set; } = 2;

    private DateOnly _dia;
    private DateTime _inicioRango;
    private decimal _techo, _piso;
    private bool _hayRango, _ordenesPuestas;
    private StrategyOrder? _compra, _venta;

    // sin StopLossTicks, el stop arranca a 60 ticks y después acompaña al precio
    public RupturaRangoApertura() => TrailingStopTicks = 60;

    protected override void OnBar(Bar bar)
    {
        // apertura de la vela en hora local: Time es UTC y LocalTime, la misma hora en la zona de la sesión
        DateTime abrio = bar.Time + (LocalTime - Time);
        var apertura = new TimeSpan(HoraApertura, MinutoApertura, 0);
        var finRango = apertura + new TimeSpan(0, MinutosRango, 0);
        var cierre = new TimeSpan(HoraCierre, 0, 0);
        var ahora = LocalTime.TimeOfDay;

        var dia = DateOnly.FromDateTime(abrio);
        if (dia != _dia) // día nuevo: no queda nada del anterior
        {
            CancelAll();
            if (!IsFlat) ClosePosition("Cierre del día");
            (_dia, _techo, _piso, _hayRango, _ordenesPuestas) = (dia, decimal.MinValue, decimal.MaxValue, false, false);
        }

        // 1) el rango: las velas que abren entre la apertura y el fin del rango
        if (abrio.TimeOfDay >= apertura && abrio.TimeOfDay < finRango)
        {
            if (!_hayRango) _inicioRango = bar.Time;
            _hayRango = true;
            _techo = Math.Max(_techo, bar.High);
            _piso = Math.Min(_piso, bar.Low);
            DrawBox($"rango-{dia:yyyyMMdd}", _inicioRango, _techo, bar.CloseTime, _piso, "azul", 15, "rango");
        }

        // 2) cerrado el rango, una orden stop de cada lado (una sola vez por día)
        if (_hayRango && !_ordenesPuestas && ahora >= finRango && ahora < cierre && IsFlat)
        {
            _compra = BuyStop(_techo + Ticks(Margen), name: "Ruptura arriba");
            _venta = SellStop(_piso - Ticks(Margen), name: "Ruptura abajo");
            _ordenesPuestas = true;
            Log($"Rango del día: {_piso} a {_techo}");
        }

        bool niveles = _ordenesPuestas && ahora < cierre; // los niveles del rango, mientras dura el día
        Plot("Techo", niveles ? _techo : (decimal?)null, "verde");
        Plot("Piso", niveles ? _piso : (decimal?)null, "rojo");

        // 3) a la hora de cierre no queda nada abierto
        if (ahora >= cierre) { CancelAll(); if (!IsFlat) ClosePosition("Cierre del día"); }
    }

    protected override void OnOrderFilled(StrategyOrder order)
    {
        if (order != _compra && order != _venta) return;
        var otra = order == _compra ? _venta : _compra; // entró por un lado: se cancela el otro
        if (otra is { IsActive: true }) Cancel(otra);
        if (order.Side == OrderSide.Buy) DrawArrowUp($"in-{order.Id}", order.FillTime, order.FillPrice, "verde", "ruptura");
        else DrawArrowDown($"in-{order.Id}", order.FillTime, order.FillPrice, "rojo", "ruptura");
    }
}
```

`bar.Time + (LocalTime - Time)` pasa la apertura de la vela a la hora local de la sesión, así el rango no depende de
cuándo llega la primera operación de la vela siguiente. Las dos órdenes stop quedan esperando y, cuando una se ejecuta,
`OnOrderFilled` cancela la otra; el trailing y el cierre del día protegen la posición.

## El editor (Backtesting → Código)

Arriba a la izquierda elegís la estrategia o creás una con **Nueva estrategia…** (plantillas: Cruce de medias, Ruptura
de rango, Absorción con delta, Reversión a la VWAP o Vacía); al lado se ve «Compilada · N ms» o la cantidad de errores.
La lista tiene un buscador y arriba la sección **Favoritos**: la estrella de cada estrategia la suma o la saca, sin
cerrar la lista.
El editor resalta C# y lista abajo los errores (tocando uno vas a la línea). Botones: **Guardar** (⌘S), **Compilar**
(⌘B), **Compilar y probar** (compila y corre el backtest), **Referencia** (resumen de la API) y el tacho, que borra el
archivo y conserva su historial. A la derecha está el panel de Claude: lo que escribe aparece en el editor.

## Que Claude la escriba

En el panel de Claude describís lo que querés, por ejemplo «una ruptura del rango de los primeros 30 minutos con stop
por ATR». Claude Code crea o edita el archivo en la carpeta de estrategias, Trading Hub lo compila al terminar y, si
compila, te ofrece **Probar en backtest**; si no, **Pedirle que los arregle**. Con **Laboratorio** activado, además corre
backtests, optimizaciones y walk-forward por su cuenta y deja un reporte. El detalle está en [Claude](./asistente-claude.md).
