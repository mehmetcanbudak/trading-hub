[← Volver al índice](./README.md)

# Backtesting

La sección **Backtesting** (⌘2) junta todo lo que es prueba. Arriba elegís la estrategia y abajo tenés las pestañas
**Código**, **Backtest**, **Optimización**, **Walk-forward**, **Historial**, **Portafolio** y **Reportes**. Cómo se
escribe una estrategia está en [Estrategias](./estrategias.md); lo que corre en tiempo real, en [En vivo](./en-vivo.md).

## Elegir los datos

El backtest recorre, una por una, las operaciones reales guardadas: lo que se grabó mientras tenías gráficos abiertos y
lo que bajaste o importaste en [Datos](./datos.md). Sin nada guardado, la lista está vacía.

- **Instrumento (datos guardados)** muestra `SÍMBOLO · fuente · N días`, y debajo el rango y lo que ocupa.
- Los futuros que además tienen historia de [Databento](./datos.md#databento) aparecen tres veces:

  | Entrada | Qué usa |
  |---|---|
  | **Rithmic + Databento** | La combinada: manda Rithmic y Databento completa solo los minutos en que Rithmic no tiene operaciones |
  | **solo Rithmic** | Solo lo grabado o bajado de Rithmic |
  | **Databento** | Solo lo bajado de Databento |

  Con Tradovate pasa lo mismo («Tradovate + Databento», «solo Tradovate»).
- **Desde** y **Hasta** (el último día entra completo), con los rangos rápidos **1 semana**, **1 mes**, **3 meses**,
  **6 meses**, **1 año** y **Todo**, contados hacia atrás desde el último día guardado (Todo arranca en el primero).
- Al cambiar de instrumento el rango pasa a los últimos 7 días guardados, y la comisión % a 0,04 si es cripto (Binance
  o Bybit) o a 0 en el resto.

**Período de las velas**: un código como `M5`. Hay botones para `M1`, `M5`, `M15`, `H1`, `H4`, `D1`, `Tick500` y
`Range20`. No importan las mayúsculas y acepta la forma de la barra del gráfico y nombres en castellano: `d1`, `D`,
`1D`, `Daily` o `diario` son lo mismo, igual que `5m` y `M5`, `1h` y `H1`, o `semanal` y `W1`.

| Código | Tipo de vela |
|---|---|
| `S30`, `M5`, `H1`, `D1` o `Daily`, `W1` o `Weekly`, `Mo1` o `Monthly` | De tiempo (segundos, minutos, horas, días, semanas, meses) |
| `Tick500` | Cada 500 operaciones |
| `Volume1000` | Cada 1.000 de volumen |
| `Range20` | Cada vez que el rango supera 20 ticks |
| `Delta300` | Cuando el delta de la vela llega a ±300 |

Renko no está disponible en backtests.

## Ejecución: costos y reglas

| Campo | Por defecto | Qué hace |
|---|---|---|
| Capital inicial | 10.000 | Base del retorno %, el drawdown % y el Sharpe |
| Comisión % por lado | 0,04 en cripto, 0 en el resto | Porcentaje del nominal (cantidad × precio × valor del punto) en cada entrada y cada salida |
| Comisión fija por unidad | 0 | Por contrato o unidad y por lado; se suma a la porcentual (en futuros, usá esta) |
| Deslizamiento (ticks) | 1 | Ticks en contra en las órdenes a mercado y en los stops |
| Órdenes límite | Solo si el precio lo atraviesa | La alternativa es «Apenas lo toca (optimista)» |
| Zona de la sesión e inicio (h) | UTC, 0 | Definen `LocalTime`, `TimeBetween`, el día de cada sesión (VWAP, delta acumulado, `OnSessionStart`) y las estadísticas por hora |

Las zonas disponibles son UTC, Buenos Aires, Nueva York, Chicago (CME) y Londres. La tarjeta **Parámetros** tiene los de
la estrategia y, aparte, los de gestión de la posición. Lo que configurás queda guardado por estrategia: esos parámetros
son los que toma después la estrategia puesta en un gráfico, y [En vivo](./en-vivo.md) usa además las velas y la sesión.

### Cómo se llenan las órdenes

| Orden | Se llena |
|---|---|
| A mercado | Con la operación siguiente a la orden, más o menos el deslizamiento. Si la mandó `OnBar`, con la primera operación de la vela siguiente. |
| Límite | Al precio límite cuando una operación lo atraviesa (o lo toca, en modo optimista). Si al mandarla el precio ya estaba del otro lado, se llena como a mercado, nunca peor que el límite. |
| Stop | Cuando una operación llega al precio, al precio de esa operación más o menos el deslizamiento (con un hueco, peor). |
| Stop y objetivo automáticos | Como stop y como límite, unidos como OCO. |

Ninguna orden se llena con la operación que la originó. Al final del rango, la posición abierta se cierra al último
precio («Fin del backtest»). El resultado está en la moneda del instrumento: diferencia de precio × cantidad × valor del
punto (en el NQ, 20 por punto). El contexto de mercado arranca con el mes anterior guardado, como en vivo.

## Resultados

Cada backtest queda en el **Historial** con el código exacto que se usó. Arriba se ven **Resultado neto** (y su % del
capital), **Profit factor**, **Ganadoras**, **Drawdown máx.** (en % y con el factor de recuperación), **Sharpe** (y
Sortino) y **Expectativa**, y la curva de capital. Debajo, cinco pestañas:

- **Operaciones**: número, lado, entrada y salida con su precio (en la hora de tu Mac), cantidad, resultado neto, MAE y
  MFE (lo máximo que fue en contra y a favor), velas y «Salida por» (el nombre de la orden que cerró).
- **Métricas**: 28 números, entre ellos comisiones, retorno, promedio por operación, rachas, velas promedio, tiempo en
  el mercado, resultado de largos y cortos, ticks simulados y velas.
- **Según el mercado**, **Monte Carlo** y **Registro** (lo que la estrategia escribió con `Log`, con hora UTC, hasta
  5.000 líneas), que se explican abajo.

| Métrica | Cómo se calcula |
|---|---|
| Profit factor | Ganancia bruta / pérdida bruta (∞ si no hubo pérdidas) |
| Ganadoras | Operaciones con resultado neto mayor a 0, sobre el total |
| Expectativa | % ganadoras × ganancia promedio + % perdedoras × pérdida promedio |
| Drawdown máximo | Mayor caída desde un pico de la curva de capital (con lo abierto, al cierre de cada vela); el % es sobre ese pico |
| Factor de recuperación | Resultado neto / drawdown máximo |
| Sharpe y Sortino | Media de los retornos diarios / su desvío (Sortino solo cuenta los días negativos), × √252, o × √365 en cripto |

### Según el mercado

Cada operación guarda cómo estaba el mercado al entrar, con las mismas medidas del contexto de las estrategias: rango,
mixto, tendencia alcista o bajista (eficiencia de las últimas 24 horas) y volatilidad baja, normal o alta (hasta el
percentil 33 del último mes, entre 33 y 67, o desde 67). La tabla da operaciones, neto, profit factor, % de ganadoras y
promedio de cada condición, y marca «poca muestra» cuando hay muy pocas.

Debajo elegís condiciones y tocás **Volver a correr solo en estas condiciones**. No es un recorte del backtest anterior:
es uno nuevo con el filtro de contexto, donde la estrategia decide en cada vela con lo que se sabía hasta ese momento
(las salidas siempre pasan), igual que en vivo. Arriba se comparan las dos corridas. Ojo: si elegís las condiciones
mirando estos mismos resultados, el filtro está ajustado a esos datos; validalo con walk-forward o en otro período.

### Monte Carlo

**Simular 1.000 órdenes al azar** reordena las operaciones 1.000 veces y mide el drawdown de cada camino: el típico
(mediana), el de 1 de cada 20 caminos (percentil 95), el extremo (percentil 99) y el peor visto, más el **riesgo de
ruina** (caminos con un drawdown de al menos el 50 % del capital). El gráfico muestra 40 de esos caminos.

## Historial

Filtrás por **Todas**, **Backtests**, **Optimizaciones**, **Walk-forward** o la estrella (favoritas). Cada corrida trae
sus métricas, la curva, los parámetros y la configuración, con una nota editable. Botones: estrella, borrar, **Ver
código** (la versión exacta), **Informe** (arma el informe profesional en Reportes) y **Usar parámetros** (los copia al
backtest y lo corre). Se guarda en `~/Library/Application Support/Trading Hub/Backtests`, una carpeta por estrategia.

## Optimización

1. En **Qué probar** marcás los parámetros a recorrer con su mínimo, máximo y paso (los que tienen `[Optimize]` ya
   vienen marcados). Se ve cuántas combinaciones son: con más de 3.000 te avisa y con más de 20.000 no corre.
2. **Ordenar por**: Resultado / drawdown (por defecto), Resultado neto, Profit factor (con tope de 100), Sharpe,
   Sortino, Factor de recuperación, Expectativa o % ganadoras.
3. **Mínimo de operaciones** (10): las combinaciones con menos quedan sin métrica, para no premiar la suerte.
4. **Optimizar**. Usa los datos y la ejecución de la pestaña Backtest y prueba todas las combinaciones (grilla), en
   paralelo con todos los núcleos menos uno.

**Optimizar el timeframe.** Al final de **Qué probar** está **Período de las velas**: lo tildás y marcás los períodos
a probar (`M1`, `M5`, `M15`, `M30`, `H1`, `H4` y `D1`). Cada combinación de parámetros se prueba en cada período
marcado: la historia tick a tick se carga una sola vez y las velas se arman en cada período. En la tabla cada fila
dice su período, y **Usar en Backtest** también lo copia. En el walk-forward el período se elige en cada ventana como
un parámetro más, y la medición de sobreajuste cuenta cada período como una prueba más.

La tabla muestra hasta 500 filas ordenadas por la métrica. **Usar en Backtest** (o doble clic) copia los parámetros y
corre el backtest. La mejor fila suele ser un pico de suerte: validá con walk-forward. Las optimizaciones y los
walk-forward también quedan en el Historial.

## Walk-forward

Optimiza en una ventana (dentro de muestra), prueba lo elegido en los días siguientes, que la optimización no vio (fuera
de muestra), avanza y repite. Lo que cuenta es el resultado fuera de muestra encadenado.

- **Dentro de muestra** (14 días) y **Fuera de muestra** (7 días). **Móvil**: la ventana de optimización tiene largo
  fijo y avanza. **Anclada**: arranca siempre en el primer día y crece. Antes de correr se ve cuántas ventanas salen.
- Usa los parámetros, la métrica y el mínimo de operaciones de Optimización, y los datos y costos de Backtest.
- **Búsqueda de parámetros**: **Automática** (grilla hasta 300 combinaciones, bayesiana si hay más), **Grilla
  completa** o **Bayesiana**. La bayesiana mide 80 combinaciones por ventana.

Resultados: **Fuera de muestra** (neto, operaciones y profit factor), **Eficiencia WF**, **Ventanas ganadoras**,
**Drawdown fuera**, **Sobreajuste** y **Caminos (CPCV)**, la curva fuera de muestra con cada ventana marcada (V1, V2…) y
una tabla por ventana con sus fechas, los parámetros elegidos, el neto dentro y fuera, y su eficiencia.

### Cómo trabaja por dentro

1. **Una corrida por combinación.** Cada combinación se corre una sola vez sobre toda la historia, con los indicadores
   calientes y el contexto de mercado como en vivo, y cada ventana se mide recortando ese resultado (las operaciones que
   entraron en la ventana y el capital hora por hora). Ninguna ventana usa datos de después de ella.
2. **Compresión de ticks.** Las operaciones seguidas al mismo precio, del mismo lado y en el mismo segundo se juntan en
   una con el volumen sumado. Las velas de tiempo o de rango quedan idénticas (apertura, máximo, mínimo, cierre, volumen,
   delta y footprint) y las órdenes se llenan a los mismos precios, con muchos menos eventos. Se usa en la optimización y
   el walk-forward solo con velas de tiempo o de rango y si la estrategia no usa `OnTick`; lo único que cambia es
   `Bar.Trades`, la cantidad de operaciones de cada vela.
3. **Grilla o bayesiana.** La grilla prueba todo y deja ver el mapa entero. La bayesiana (TPE, *Tree-structured Parzen
   Estimator*) arranca con puntos bien repartidos (un hipercubo latino de entre 8 y 30, un cuarto del presupuesto) y
   después propone de a tandas: separa el 20 % mejor de lo medido, estima para cada parámetro qué valores son
   frecuentes entre las buenas (`l(x)`) y entre las malas (`g(x)`), y prueba donde `l(x)/g(x)` es más alto.
4. **Meseta, no pico.** En cada ventana se elige por el promedio con las vecinas: para cada combinación,
   `puntaje = (suma de su métrica y la de las vecinas a un paso de grilla + mediana) / (vecinas + 2)`. La mediana tira
   hacia el medio a las que tienen pocas vecinas medidas, y una vecina sin métrica cuenta peor que la peor. Una combinación
   que gana sola entre vecinas que pierden casi siempre es suerte; una zona entera que gana, no.
5. **Eficiencia.** `ganancia diaria fuera de muestra / ganancia diaria dentro de muestra`, sumando todas las ventanas
   (y lo mismo por ventana). Más de 0,5 es buena señal; 0 si dentro de muestra no ganó.
6. **PBO con CSCV** (probabilidad de sobreoptimización con validación cruzada combinatoriamente simétrica). Con el
   resultado diario de todas las combinaciones corridas, la historia se parte en 10 bloques (menos si hay pocos días) y
   se prueban todas las formas de elegir la mitad como «dentro» (252): en cada una se toma la de mejor Sharpe ahí y se mira su
   lugar `r` en la otra mitad (1 = la peor de `N`). Con `ω = r / (N + 1)`, queda en la mitad de abajo si
   `ln(ω / (1 − ω)) ≤ 0`; **PBO** es la proporción de veces que pasa. Trading Hub muestra además cuántas veces la
   elegida **ganó plata** en la otra mitad (tarjeta **Sobreajuste**: verde con menos del 20 % de pérdidas, rojo desde el
   50 %), porque una PBO alta con todas ganando es una meseta, no un problema. Hacen falta 4 combinaciones y 20 días.
7. **CPCV con embargo** (validación cruzada combinatoria purgada). La historia se parte en 8 grupos seguidos (menos si
   hay pocos días); para cada par de grupos de prueba (28 pares) se elige la combinación por meseta con los días
   restantes, sacando los que quedan a menos del **embargo** de los grupos de prueba (la duración típica de una
   operación, entre 1 y 5 días), y se mide en esos dos grupos. Como cada grupo es de prueba en 7 pares, salen 7 caminos
   fuera de muestra completos, cada uno con su neto, Sharpe y drawdown. La tarjeta
   **Caminos (CPCV)** es verde si gana al menos el 70 % y la mediana es positiva, y roja si la mediana no gana.

## Sharpe deflactado y la validación

Probar muchas variantes hace que la mejor parezca buena por azar. El **Sharpe deflactado** mide la probabilidad de que
la ventaja sea real después de descontar cuántas cosas se probaron. Con el Sharpe por operación fuera de muestra
(`SR` = media / desvío del resultado neto), su asimetría `γ3`, su curtosis `γ4` (3 en una normal) y `n` operaciones:

```
PSR(SR*) = Φ( (SR − SR*) · √(n − 1) / √(1 − γ3·SR + (γ4 − 1)/4 · SR²) )
SR*      = √(max(V, 1/n)) · E[máx Z_N],   E[máx Z_N] ≈ (1 − γ)·Φ⁻¹(1 − 1/N) + γ·Φ⁻¹(1 − 1/(N·e)),   γ ≈ 0,5772
```

`N` es la cantidad de configuraciones probadas, `V` la varianza de sus Sharpe y `Φ` la normal acumulada: `SR*` es el
Sharpe que mostraría por suerte la mejor de `N` estrategias sin ventaja.

La validación son reglas fijas (`ValidationGate` en `TradingHub.Strategies`) que juzgan solo lo de fuera de muestra:

| Prueba | Pasa | Dudosa | No pasa |
|---|---|---|---|
| Muestra | ≥ 30 operaciones | | menos |
| Resultado | neto > 0 | | neto ≤ 0 |
| Profit factor | ≥ 1,2 | ≥ 1,0 | < 1,0 |
| Eficiencia walk-forward | ≥ 0,5 | ≥ 0,3 | < 0,3 |
| Consistencia (ventanas que ganan) | ≥ 60 % | ≥ 40 % | < 40 % |
| Ventanas | ≥ 4 | menos | |
| Estabilidad (dispersión de los parámetros elegidos / ancho del rango) | ≤ 0,35 | más | |
| Meseta (vecinas de la mejor que ganan) | ≥ 50 % | menos | |
| Robustez (combinaciones que ganan) | ≥ 25 % | menos | |
| Sobreajuste (la elegida pierde en la otra mitad) | < 20 % | < 50 % | ≥ 50 % |
| Caminos CPCV | ≥ 70 % ganan | menos | mediana ≤ 0 |
| Sharpe deflactado | ≥ 90 % | ≥ 50 % | < 50 % |
| Desgaste (con 30 operaciones o más) | se sostiene | el último tercio rinde menos de la mitad que el primero, o t ≤ −1,5 | el primero ganaba, el último pierde y t ≤ −2 |
| Regímenes (grupos con al menos 8 operaciones y el 15 %) | gana en todos | pierde en alguno | |

Con alguna «no pasa» el veredicto es **no pasa**; con alguna dudosa, **dudosa**; si no, **pasa**. Si gana fuera de
muestra y solo falla lo que depende de la cantidad de operaciones, el veredicto es **falta muestra**, con cuántas veces
más operaciones harían falta (proyectando el Sharpe deflactado con la misma ventaja, hasta 8 veces).

En esta versión, la pestaña Walk-forward muestra la eficiencia, las ventanas ganadoras, el sobreajuste y los caminos (los
dos últimos, con los umbrales de esta tabla). La validación completa, con el Sharpe deflactado, el desgaste y los
regímenes, está en la biblioteca, cubierta por las pruebas, para usarla desde código (ver [Desarrollo](./desarrollo.md)).

## Portafolio

Combina backtests guardados de varias estrategias o instrumentos: **Agregar** (elegís la corrida), pesos **Igual**,
**Riesgo parejo** (cada una pesa la inversa de la volatilidad de su resultado diario) o **Manual**, y el capital. Muestra
las métricas del conjunto, la curva, la matriz de correlación de los resultados diarios (conviene que esté por debajo de
0,5) y cuánto baja el drawdown frente a la suma de los individuales. **Sugerir combinación** busca entre los últimos
backtests de todas tus estrategias la combinación con correlación menor a 0,5 que más mejora el Sharpe (hasta 5).
**Informe** arma el informe del portafolio.

## Reportes

Informes en Markdown con todas las métricas, tablas y gráficos: los arma **Informe** desde el Historial o el Portafolio,
o Claude con el laboratorio (con su veredicto y su análisis). Se guardan en `~/Documents/Trading Hub/Estrategias/Reportes`,
se ven como una hoja y **Exportar PDF / HTML** los abre en el navegador para imprimirlos o guardarlos como PDF. Ver
[Claude](./asistente-claude.md).
