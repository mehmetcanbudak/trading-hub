[← Volver al índice](./README.md)

# Desarrollo

Para quien quiera tocar el código. Trading Hub es C# sobre .NET 10, con Avalonia 11.3 para la interfaz y SkiaSharp 2.88
para dibujar los gráficos. Los textos de la interfaz y los comentarios del código están en español.

## La solución

`TradingHub.slnx` tiene diez proyectos:

| Proyecto | Qué hace |
|---|---|
| `src/TradingHub.Core` | Mercado y datos: instrumentos, operaciones, velas, archivos de ticks, importación de CSV, fuentes de historia (la combinada de Rithmic y Databento), las conexiones (Binance, Bybit, Rithmic, Tradovate, OANDA, Alpaca, cTrader, Databento, la prueba de Interactive Brokers, el simulador y el replay) y la parte de operar (cuentas, cuenta simulada, stop y objetivos, historial). Sin paquetes externos. |
| `src/TradingHub.Sdk` | La API de indicadores, con la misma forma que la de ATAS: `Indicator`, series, dibujo, atributos y textos. |
| `src/TradingHub.Charting` | El motor de gráficos con Skia: modelo, velas, footprint, DOM, cinta, dibujos y temas. |
| `src/TradingHub.Scripting` | Compila C# en memoria con Roslyn (indicadores y estrategias) y adapta los namespaces de ATAS. |
| `src/TradingHub.Strategies` | La API de estrategias, el motor de backtest, la optimización, el walk-forward inteligente, la robustez y la validación, los portafolios y la ejecución en vivo con su contabilidad. Solo depende de Core. |
| `src/TradingHub.Indicators.Native` | Los indicadores incluidos. |
| `src/TradingHub.Indicators.Technical` | El lugar de los indicadores técnicos portados; en la versión pública solo trae los tipos de editores que usan. |
| `src/TradingHub.App` | La app (ejecutable `TradingHub`): `Views` (secciones), `Windows` (ventanas), `Controls`, `Services` (conexiones y Llavero, Claude Code, En vivo, agente de riesgo, condición de los mercados, barra de menú, modo sin dormir…), `Lab` (servidor MCP y reportes) y `Styles`. Con `--mcp` corre como servidor del laboratorio, sin ventanas. |
| `tests/TradingHub.Tests` | Las pruebas, con xUnit. |
| `tools/TradingHub.UiSnapshot` | La app sin pantalla, con escenas que verifican y capturan la interfaz. |

Las versiones de Avalonia y SkiaSharp, el framework y la versión del producto están en `Directory.Build.props`. El
nombre visible sale de `TradingHub.Common.Product.Name` y las carpetas, de `Product` y `AppPaths`.

## Compilar y correr

```bash
export PATH="$HOME/.dotnet:$PATH"
dotnet build TradingHub.slnx          # todo
dotnet test tests/TradingHub.Tests    # pruebas
./build-app.sh                        # arma dist/Trading Hub.app (firmada ad hoc)
./build-app.sh --install              # y la copia a /Applications
```

`build-app.sh` publica en Release para `osx-arm64`, autocontenida, y arma el paquete fuera del Escritorio (iCloud le
agrega atributos que rompen la firma). Un archivo `build.local.env`, que no se sube, puede definir `BUNDLE_ID` y
`SIGN_IDENTITY`; `TH_OUT` y `TH_STAGE` cambian las carpetas de salida y de armado. Más en
[Instalación](./instalacion.md).

Para correr la app desde el código sin tocar tus datos ni tu Llavero:

```bash
TRADINGHUB_DATA_DIR=/tmp/th-dev TRADINGHUB_KEYCHAIN_FILE=/tmp/th-dev/llavero.json dotnet run --project src/TradingHub.App
```

Con `TRADINGHUB_DATA_DIR`, las estrategias y los indicadores también van a subcarpetas de esa carpeta, no aparece el
ícono de la barra de menú y el modo sin dormir no toca la energía de la Mac. Nunca corras una segunda copia sobre los
datos reales con la app instalada abierta: dos procesos grabando los mismos archivos los dañan.

## Pruebas

`dotnet test tests/TradingHub.Tests` corre todas. Cubren el motor de backtest y sus llenados (`StrategyTests`), stop,
objetivos y OCO (`BracketTests`), el contexto de mercado y el filtro (`ContextTests`), la robustez y la validación
(`RobustnessTests`), el walk-forward inteligente (`SmartWalkForwardTests`), la ejecución en vivo (`LiveRunnerTests`,
`LivePortfolioTests`), portafolios e informes (`PortfolioTests`), los datos (`CoreTests`, `DataIntegrityTests`,
`HistorySourcesTests`, `LongHistoryTests`, `CsvImportTests`), las cuentas (`AccountTests`), los dibujos y la leyenda
(`DrawingAndLegendTests`) y las conexiones (`RithmicTests`, `BrokerConnectionTests`, `DataSourceTests`,
`BinanceSignatureTests`).

Las pruebas usan carpetas temporales y servidores de mentira con los mensajes reales de cada servicio
(`FakeRithmicServer`, y `FakeHttpServer` con `FakeBrokers` para OANDA, Alpaca, Databento, cTrader y Tradovate): nunca
entran a una cuenta real. Para correr un grupo: `dotnet test tests/TradingHub.Tests --filter FullyQualifiedName~SmartWalkForward`.

## Escenas sin pantalla

```bash
dotnet run --project tools/TradingHub.UiSnapshot -- <carpeta> 4 <escena> [<escena>…] [--open conexión:SÍMBOLO] [--size 1600x950]
```

`<carpeta>` es donde guarda las capturas PNG y `4`, los segundos que espera a que la app cargue antes de las escenas
(sin escenas, captura Trading). Las que tienen verificaciones imprimen `OK` o `FALLA` por cada una, y el proceso termina
con la cantidad de fallas como código de salida.

| Escena | Qué hace |
|---|---|
| `trading`, `accounts`, `strategies`, `live`, `data`, `connections`, `settings` | Abre esa sección y la captura |
| `conexiones` | Tradovate, cTrader, OANDA, Alpaca y Databento contra servidores falsos: alta, prueba, descarga en Datos, costo de Databento, cuentas de Tradovate y precios en vivo de OANDA |
| `panel` | El panel derecho de Trading (Chart Trader, DOM y cinta), los botones del DOM y las ventanas aparte |
| `footprint` | Las escaleras del footprint con colores propios y el editor de footprint |
| `mercado` | El backtest según el mercado y la corrida filtrada por condición |
| `theme` | Modo oscuro y claro, con la configuración del gráfico |
| `choice`, `picker`, `esquinas` | El botón de historia de la barra, el selector de instrumentos y un diagnóstico de bordes redondeados |
| `data+rithmic`, `data+import` | Datos con Rithmic (servidor falso) e importación de un CSV de prueba |
| `accounts+demo` | La sección Cuenta con órdenes en la simulada y en Rithmic falso |
| `connections+rithmic`, `connections+amp` | El perfil de Rithmic o de AMP con su lista de servidores |
| `trading+…` | `layout2`, `layout2r`, `layout3`, `layout4`, `layout6`, `history<horas>[+período]`, `historyall`, `jitter`, `float`, `brackets`, `drawings`, `drawsettings`, `links`, `toolbar`, `trader`, `accounts`, `crossdemo`, `strategy`, `domfloat`, `domsettings` |
| `strategies+…` | `backtest`, `optimize` y `wfo` (corren y capturan), `code`, `history`, `reports`, `portfolio`, `claudefloat`, `claude`, `claudelab`; con `+Archivo` al final elige esa estrategia |

Cómo se aíslan de tus datos:

- Pone `TRADINGHUB_SLEEP_GUARD=off` y un `TRADINGHUB_KEYCHAIN_FILE` temporal con secretos de mentira: nunca lee el Llavero.
- Copia tus estrategias e indicadores a carpetas temporales (`TRADINGHUB_STRATEGIES_DIR`, `TRADINGHUB_INDICATORS_DIR`).
- Usa una carpeta de datos temporal (`TRADINGHUB_DATA_DIR`) con copias de `settings.json`, `workspace.json`,
  `connections.json` y de lo bajado (`Ticks`, `Klines`, `Drawings`, salvo Rithmic), nunca los originales. A las
  conexiones les saca las API keys, les apaga las órdenes reales y lleva Rithmic a un servidor falso con el usuario
  `demo`; el historial de las cuentas empieza vacío.
- No corren dos a la vez: un candado en la carpeta temporal hace esperar a la siguiente (hasta 20 minutos).
- Si ya definiste alguna de esas variables, respeta la tuya.

Ojo: `strategies+claude` y `strategies+claudelab` mandan un pedido de verdad a tu Claude Code (gastan de tu límite),
salvo que `TRADINGHUB_CLAUDE_CLI` apunte a otro ejecutable. Para diagnosticar: `TRADINGHUB_SNAP_INDICATORS="Nombre|Otro"`
agrega esos indicadores al gráfico activo, `TRADINGHUB_SCRIPTS_DEBUG=1` muestra cómo compilaron los indicadores
propios y `TRADINGHUB_SNAP_DEBUG=1`, el estado de la conexión.

## Variables de entorno

| Variable | Qué cambia |
|---|---|
| `TRADINGHUB_DATA_DIR` | La carpeta de datos (en vez de `~/Library/Application Support/Trading Hub`) |
| `TRADINGHUB_STRATEGIES_DIR`, `TRADINGHUB_INDICATORS_DIR` | Las carpetas de estrategias e indicadores |
| `TRADINGHUB_KEYCHAIN_FILE` | Un JSON con los secretos, en vez del Llavero |
| `TRADINGHUB_BACKTESTS_DIR`, `TRADINGHUB_LIVE_DIR` | El historial de backtests y la cartera en vivo |
| `TRADINGHUB_SLEEP_GUARD=off` | No toca la energía de la Mac |
| `TRADINGHUB_CLAUDE_CLI` | La ruta de la CLI de Claude Code |
| `TRADINGHUB_MCP_COMMAND` | El ejecutable que levanta el laboratorio (por defecto, la misma app) |
| `TRADINGHUB_OANDA_REST`, `TRADINGHUB_OANDA_STREAM`, `TRADINGHUB_ALPACA_DATA`, `TRADINGHUB_ALPACA_TRADING`, `TRADINGHUB_ALPACA_STREAM`, `TRADINGHUB_TRADOVATE_REST`, `TRADINGHUB_TRADOVATE_WS`, `TRADINGHUB_TRADOVATE_MD`, `TRADINGHUB_CTRADER_URL`, `TRADINGHUB_DATABENTO_URL` | Apuntan cada conexión a otro servidor (los usan las escenas con servidores falsos) |

## Agregar una conexión

1. En `src/TradingHub.Core/Feeds/<Nombre>/`, una clase que implemente `IMarketDataFeed`: `GetInstrumentsAsync`,
   `LoadTradesAsync` (historia por lotes) y `Subscribe`, que entrega operaciones, profundidad y estado a un
   `IMarketDataSink`. Según lo que dé el servicio, sumá `IKlineHistory` (velas para historia larga), `IDayDownloader`
   (bajar días completos en Datos), `ISessionAwareFeed` (mercados que cierran) o `ITickerStatsProvider` (precio y
   variación de 24 horas en el selector).
2. Si se puede operar, una cuenta que implemente `ITradingAccount` (mercado, límite, stop, cancelar, modificar, cerrar,
   dar vuelta, posición e `IsReal`).
3. En `src/TradingHub.App/Services/Connections.cs`: un valor nuevo en `ConnectionKind`, su `MarketDataId`, los datos
   no secretos en `ConnectionProfile`, el feed en `ConnectionManager.GetFeed` y la prueba en `TestAsync`. Los secretos,
   con `SetSecret` y `GetSecret` (van al Llavero). Sumá una variable `TRADINGHUB_…` para cambiar la dirección del
   servidor en las pruebas.
4. El formulario en `src/TradingHub.App/Views/ConnectionsView.cs` y, si opera, sus cuentas en `AppServices.AccountsFor`
   y `AccountFor`: las reales solo con «Permitir órdenes reales».
5. Un servidor de mentira en `tests/TradingHub.Tests` (podés partir de `FakeHttpServer`) con sus pruebas.

## Agregar un indicador o una herramienta del laboratorio

- **Indicador incluido**: una clase en `src/TradingHub.Indicators.Native` que herede de `Indicator`, con constructor sin
  parámetros, `[DisplayName]` y `[Category(IndicatorCategories.…)]`. La app escanea ese ensamblado al arrancar. Para uno
  tuyo no hace falta tocar el código: va en la carpeta de indicadores (ver [Indicadores](./indicadores.md)).
- **Herramienta del laboratorio**: su esquema en `LabServer.Tools()` y su caso en `LabServer.CallAsync`
  (`src/TradingHub.App/Lab/LabServer.cs`), y su nombre en `ClaudeCodeBridge.LabTools`
  (`src/TradingHub.App/Services/ClaudeCode.cs`), que es la lista de herramientas permitidas.

## La validación desde código

`ValidationGate.Evaluate` (en `TradingHub.Strategies`) aplica las reglas de [Backtesting](./backtesting.md) a un
walk-forward. `tests/TradingHub.Tests/RobustnessTests.cs` tiene ejemplos.

```csharp
var gate = ValidationGate.Evaluate(new GateInput
{
    WalkForward = walkForward,   // el resultado de Optimizer.WalkForward
    Optimization = optimizacion, // opcional: para la meseta y la robustez
    Trials = 12,                 // configuraciones probadas, para el Sharpe deflactado
    TrialVariance = 0.01,        // varianza de sus Sharpe por operación
    MinTrades = 30,
});
Console.WriteLine($"{gate.StatusText} · Sharpe deflactado {gate.Deflated:P0}");
foreach (var c in gate.Checks) Console.WriteLine($"{c.Status} · {c.Name}: {c.Text}");
```

`LiveStrategyRunner.Guard` acepta además un `ILiveOrderGuard`, un limitador que revisa cada orden que abre o agranda una
posición antes de mandarla (las que achican siempre pasan); la app no trae uno, pero las pruebas muestran cómo se usa.

## Reglas

- **Nunca escribas contraseñas, tokens ni claves en archivos del repositorio.** La app los guarda en el Llavero
  (`Keychain` en `Connections.cs`); las herramientas de prueba usan `TRADINGHUB_KEYCHAIN_FILE` con datos de mentira.
- **Nunca corras herramientas sobre los datos reales** (la carpeta de datos y `~/Documents/Trading Hub`): usá las
  variables de arriba. Dos procesos grabando los mismos archivos los dañan.
- **Sin confirmaciones que bloqueen los botones de operar.** La seguridad va por los límites de riesgo (tamaño, pérdida
  del día, drawdown, Frenar todo) y por tener que habilitar el dinero real en cada conexión, no por trabas entre el clic
  y la orden. Los botones de operar tampoco se tienen que mover: la escena `trading+jitter` mide si la barra cambia de
  alto o si el botón de comprar se corre cuando cambia la variación del día.
- Probá siempre con la cuenta simulada, y con servidores falsos cuando toques una conexión.
- Los textos de la interfaz van en español, con voseo.
