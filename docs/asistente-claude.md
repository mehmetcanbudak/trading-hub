[← Volver al índice](./README.md)

# Claude en Trading Hub

Trading Hub usa **Claude Code**, la CLI de Claude, con tu propia sesión y tu suscripción de Claude: no hay claves de
API ni cuentas aparte, y lo que hace se descuenta de tu límite de uso de Claude Code. Sin Claude Code la plataforma
funciona igual; solo te quedás sin el asistente, el laboratorio y el agente de riesgo.

| Dónde | Qué hace |
|---|---|
| **Backtesting → Código**, panel de Claude | Escribe, explica y corrige estrategias e indicadores |
| El mismo panel con **Laboratorio** | Además corre backtests, optimizaciones, walk-forward y Monte Carlo, y deja un reporte con veredicto |
| **En vivo → Portafolios** | El agente de riesgo de cada portafolio (ver [En vivo](./en-vivo.md#el-agente-de-riesgo)) |
| **En vivo → Propuestas** | «Pedir análisis a Claude»: el laboratorio revisa la cartera en vivo y propone tamaños |

## Cómo encuentra Claude Code

Trading Hub busca el ejecutable `claude` en `/usr/local/bin`, `/opt/homebrew/bin`, `~/.claude/local`, `~/.local/bin`,
`~/.npm-global/bin`, en cada carpeta de tu `PATH` y en la copia que trae la app de escritorio de Claude
(`~/Library/Application Support/Claude/claude-code/…`). Si encuentra varias, usa la de versión más nueva. Con la
variable `TRADINGHUB_CLAUDE_CLI` podés indicarle otra ruta. Cómo instalarlo está en
[Instalación](./instalacion.md#claude-code-opcional).

- El panel de Claude muestra arriba «Claude Code *versión* · tu suscripción», o avisa si no lo encontró.
- Si Claude Code pide iniciar sesión, aparece **Iniciar sesión en Claude Code**: abre la Terminal con `claude /login`
  para que entres con tu cuenta. Después repetís el pedido.
- Si tu versión es vieja para el modelo, el agente de riesgo lo avisa: actualizala (la app de Claude se actualiza sola,
  o con `claude update`).

## Modelo, esfuerzo y permisos

- El asistente y el laboratorio usan **Opus 5.5** (`claude-opus-5-5`) con esfuerzo **xhigh** («Extra»). Si tu Claude
  Code no conoce la opción de esfuerzo, se omite. El agente de riesgo arranca igual y se cambia por portafolio (Opus
  5.5, Sonnet 5.5, Haiku 5.5 o Fable 5.1; esfuerzo Bajo, Medio, Alto, Extra o Máximo).
- Se corre sin ventana (`claude -p`), dentro de la carpeta de estrategias, con permiso para editar archivos ahí, en la
  carpeta de indicadores y en la de capturas del laboratorio. Solo tiene herramientas de archivos (`Read`, `Write`,
  `Edit`, `MultiEdit`, `Glob`, `Grep`): sin terminal ni web.
- No carga tus otros servidores MCP (`--strict-mcp-config`): en modo laboratorio, solo el de Trading Hub. Tampoco hereda
  variables `CLAUDE*` o `ANTHROPIC*` de otra sesión: usa tu inicio de sesión normal.
- El agente de riesgo corre más restringido: sin herramientas, sin tu configuración ni tus hooks cuando tu versión lo
  permite, y sin guardar la sesión. Solo recibe los números del portafolio y devuelve una propuesta.

## El asistente de estrategias

1. Abrí **Backtesting → Código**. El panel de Claude está a la derecha del editor; con el botón de la ventana lo sacás
   a una ventana aparte (y lo volvés a acoplar).
2. Escribí el pedido y tocá **Enviar** (⌘Enter). Hay sugerencias para empezar: crear una ruptura del rango de los
   primeros 30 minutos, explicar la estrategia abierta, agregar un filtro horario, poner un stop por ATR…
3. Mientras trabaja ves lo que escribe y lo que toca («Leyendo», «Escribiendo» o «Editando», con el archivo); el editor
   queda en solo lectura y lo que cambia en el archivo abierto aparece en vivo. **Detener** lo corta.
4. Al terminar: si creó una estrategia nueva, se abre sola; Trading Hub la compila y ofrece **Probar en backtest** o,
   con errores, **Pedirle que los arregle**. Si tocó indicadores, los compila y te dice si ya están en «Mis indicadores»
   o qué falla.

Con cada pedido Trading Hub le pasa el archivo abierto, la carpeta de indicadores y los errores de compilación del
momento. Sus instrucciones traen la API completa de estrategias y de indicadores (la de [Estrategias](./estrategias.md)
y la de [Indicadores](./indicadores.md)) y estas reglas: código C# que compile sin paquetes externos, sin archivos, red,
hilos ni `Random` sin semilla, comentarios cortos en español y, al final, 2 a 5 líneas de explicación.

Las conversaciones se guardan en `~/Library/Application Support/Trading Hub/Chats`. Con el botón de historial retomás
una donde quedó (Claude sigue con todo lo que ya sabía); **Nueva conversación** empieza de cero.

## El laboratorio

Con **Laboratorio** activado, Claude Code recibe las herramientas de Trading Hub como un servidor MCP: el mismo
ejecutable de la app abierto con `--mcp`, sin ventanas, que habla JSON-RPC por la entrada y la salida estándar. La
configuración queda en `~/Library/Application Support/Trading Hub/claude-lab-mcp.json` (servidor `tradinghub`). Solo se
habilitan estas herramientas, y cada una puede tardar hasta una hora (una optimización larga):

| Herramienta | Qué hace |
|---|---|
| `list_strategies` | Lista las estrategias con su compilación, sus parámetros (con los rangos de `[Optimize]`) y su calentamiento: los indicadores de `OnInit` y las velas que tardan en estar listos |
| `compile_strategy` | Compila una estrategia y devuelve los errores (línea y mensaje), los parámetros y el calentamiento |
| `chart_snapshot` | Corre la estrategia en un rango y guarda una imagen del gráfico con sus entradas, salidas y dibujos, más las velas, operaciones y registro en hora local |
| `list_indicators` · `compile_indicator` | Lista tus indicadores propios y compila uno, con sus errores |
| `list_data` | Los datos tick a tick guardados por instrumento (los únicos que usan los backtests) |
| `download_data` | Baja días completos de los archivos públicos de Binance Futures, Binance Spot y Bybit (hasta 62 días por pedido) |
| `run_backtest` | Backtest tick a tick con comisiones y deslizamiento; opcionalmente con Monte Carlo |
| `run_optimization` | Optimización en grilla (hasta 5.000 combinaciones por defecto); devuelve las mejores, el % de combinaciones que ganan y la recomendada por meseta |
| `run_walk_forward` | Walk-forward inteligente (grilla o bayesiana, meseta, PBO y caminos CPCV); también puede solo estimar cuánto va a tardar |
| `list_runs` | Las corridas guardadas de una estrategia |
| `analyze_portfolio` · `suggest_portfolio` | Combina corridas en un portafolio con su correlación, o busca la combinación poco correlacionada que más mejora el Sharpe |
| `live_portfolio` | La cartera en vivo, solo lectura: cada estrategia con su tamaño, posición, resultado, profit factor y drawdown |
| `propose_sizing` | Deja propuestas de tamaño pendientes; no aplica nada |
| `save_report` | Arma y guarda el informe profesional |

Todo lo que corre queda en el historial de la estrategia con la nota «Laboratorio de Claude», igual que si lo corrieras
vos. Sus instrucciones le marcan un método: revisar la estrategia y los datos (y bajar 30 a 90 días si es cripto),
backtest base, optimización con rangos razonables y al menos 30 operaciones por combinación, walk-forward con la
ventana de optimización 3 a 4 veces la de prueba, Monte Carlo sobre lo validado, mejorar el código si tiene fallas
claras (comparando siempre con la versión anterior), dar un veredicto y terminar con `save_report`. No puede inventar
números: todo lo que escribe tiene que salir de una herramienta. Los veredictos son:

| Veredicto | Cuándo |
|---|---|
| **ganadora** | Gana fuera de muestra, eficiencia walk-forward ≥ 0,5, profit factor fuera de muestra ≥ 1,2, al menos 30 operaciones, drawdown p95 de Monte Carlo razonable y ventanas estables |
| **prometedora** | Resultados positivos pero con poca muestra o algo de inestabilidad |
| **perdedora** | Pierde fuera de muestra o solo gana sobreajustada |
| **descartada** | No compila, no opera o la idea no tiene sentido con esos datos |

Para la cartera en vivo usa `live_portfolio` y `propose_sizing`: más tamaño (hasta ×3) a las que rinden, menos o pausa
a las que se degradan, siempre con un motivo y sin sacar conclusiones con menos de unas 10 operaciones. Las propuestas
esperan tu aprobación en **En vivo → Propuestas**.

## Reportes

`save_report` le pide a Trading Hub que arme el informe con todas las métricas, tablas y gráficos de las corridas
(resumen ejecutivo, estrategia, datos y supuestos, rendimiento, mensual, análisis de operaciones, drawdowns,
optimización con la sensibilidad de cada parámetro, walk-forward, Monte Carlo, portafolio y glosario). Claude aporta el
veredicto, el resumen, el análisis, los riesgos y los próximos pasos.

- Se guardan en `~/Documents/Trading Hub/Estrategias/Reportes`: un `.md` por informe y una carpeta `…_archivos` con los
  gráficos.
- Se ven en **Backtesting → Reportes**, como una hoja. **Exportar PDF / HTML** arma un HTML con los gráficos adentro y
  lo abre en el navegador, desde donde lo imprimís o lo guardás como PDF; **Mostrar en Finder** abre la carpeta.
- El veredicto también aparece al lado de cada estrategia en el menú de estrategias.
- Sin Claude, **Informe** en el Historial o en el Portafolio arma el mismo informe, sin el análisis escrito.

## Qué le llega a Claude

Lo que escribís, los archivos que lee (tus estrategias e indicadores) y lo que devuelven las herramientas (métricas,
operaciones, resúmenes de datos, imágenes del gráfico y, si se lo pedís, el estado de la cartera en vivo) viajan a
Anthropic a través de tu Claude Code, con tu cuenta. Las claves de tus conexiones están en el Llavero y ninguna
herramienta las expone. Más en [Privacidad](./privacidad.md).
