[← Volver al índice](./README.md)

# En vivo

La sección **En vivo** (⌘7) junta todas las estrategias que corren en tiempo real, se hayan lanzado desde acá, desde
Backtesting o desde un gráfico. Tiene las pestañas **Estrategias**, **Mercado**, **Portafolios**, **Operaciones** y
**Propuestas**, y arriba los botones **Nueva estrategia en vivo** y **Frenar todo**. Probá siempre primero en la cuenta
simulada.

## Cuentas: simulada, de prueba y reales

- La **cuenta simulada** está siempre. Sus órdenes se llenan con la cola del libro y el tape real del mercado.
- Las cuentas testnet de Binance, demo de Tradovate y de práctica de Rithmic tampoco operan dinero real.
- Las cuentas **reales** aparecen solo si activaste «Permitir órdenes reales» en esa conexión y se ven en rojo, con
  «— REAL» (ver [Conexiones](./conexiones.md#cuentas-reales-y-cuentas-de-prueba)).
- Al lanzar o reanudar a mano una estrategia en una cuenta real, Trading Hub te pide confirmarlo una vez. Después manda
  sus órdenes sin confirmar cada una: la seguridad va por el tamaño y los límites de riesgo. La reanudación automática
  al abrir no pregunta; por eso, para las cuentas reales viene apagada.

## Lanzar una estrategia

### Desde Backtesting o desde En vivo

1. En **Backtesting → Backtest**, tarjeta «Llevar a vivo», tocá **Ejecutar en vivo…** (lleva la estrategia y sus
   parámetros), o en **En vivo** tocá **Nueva estrategia en vivo**.
2. **Qué y dónde**: la estrategia, el instrumento, la cuenta y las velas (M1, M2, M3, M5, M10, M15, M30, H1, H4 o el
   período de su backtest).
3. **Parámetros**: vienen de lo último que configuraste en su backtest.
4. **Condición de mercado**: en qué mercado abre operaciones (rango, mixto, tendencia alcista, tendencia bajista) y con
   qué volatilidad (baja, normal, alta frente al último mes). Es el filtro de contexto de la estrategia.
5. **Riesgo**: tamaño (×), pérdida máxima del día y drawdown máximo (en dinero; 0 = sin límite), y si al tocar un
   límite también cierra su posición.
6. **Portafolio**: uno tuyo o ninguno (va al automático).
7. **Iniciar en vivo**.

La zona y el inicio de sesión son los de su backtest: definen el «día» de la pérdida máxima del día. Antes de operar, la
estrategia arma sus velas e indicadores con la historia que ya tiene cargada ese instrumento (las horas de historia de
la conexión, o lo que cargó un gráfico), sin mandar órdenes, y su contexto de mercado se completa con el último mes por
hora. Si necesita más velas que las cargadas, sus indicadores tardan en estar listos. La misma estrategia no puede
correr dos veces en el mismo instrumento y la misma cuenta. En futuros, un aviso dice cuántos contratos y de cuál manda
cada orden.

### Desde un gráfico

En la barra del gráfico, el botón del robot lista tus estrategias (también podés usar **Poner en el gráfico activo**
desde Backtesting). La estrategia arranca **solo visual**: marca dónde habría entrado y salido sobre la historia del
gráfico (un backtest sin comisiones ni deslizamiento) y sigue en vivo con una cuenta simulada propia, «Visual», que no
toca la tuya. En su configuración (desde el mismo menú o la leyenda del gráfico) elegís **Ejecutar en vivo con una
cuenta**, la cuenta y **Aplicar**. Desde ahí aparece en En vivo con el origen «Gráfico» y se gestiona como las demás. Si
cambiás el instrumento del gráfico, las que ejecutan se pausan; si la quitás del gráfico, se detiene y su posición queda
como está. Más en [Gráficos](./graficos.md#estrategias-en-el-gráfico).

## Tamaño y contrato inteligente

- El **tamaño** multiplica las órdenes de la estrategia (1 = como la programaste; 0 = no abre nada nuevo). La estrategia
  sigue viendo su posición en sus propias unidades: cada orden manda la diferencia entre lo que corresponde con el
  tamaño actual y lo que tiene, así una salida cierra justo lo abierto aunque hayas cambiado el tamaño en el medio.
- Se redondea hacia abajo al paso del instrumento, pero si la estrategia quiere estar en el mercado manda al menos una
  unidad.
- **Contrato inteligente** (en Ajustes, activado): en futuros con micro (NQ → MNQ, ES → MES, YM → MYM, RTY → M2K,
  GC → MGC, CL → MCL, entre otros), si el tamaño pide una fracción del mini, las órdenes van al micro con la misma
  exposición: ×0,5 de 1 NQ son 5 MNQ. Con minis enteros usa el mini. Con una posición abierta sigue en su contrato y
  cambia cuando queda plana (se reinicia sola, con la misma contabilidad).
- En la tabla, la ficha, la barra de menú y la leyenda del gráfico siempre se ve cuántos contratos y de cuál manda cada
  orden, por ejemplo «5 MNQ · micro».

## Límites de riesgo y pausa

| Límite | Al tocarlo |
|---|---|
| Pérdida máxima del día | Cuando lo cerrado en el día de la sesión más lo abierto llega a −límite, cancela sus entradas pendientes y no abre nada más hasta la próxima sesión («Frenada por hoy»). |
| Drawdown máximo | Cuando la caída desde el máximo de su curva (contando lo abierto) llega al límite, se detiene. |

Con «Al tocar un límite, cerrar también su posición» (activado por defecto), además cierra lo que tenga abierto. Cada
límite tocado deja un aviso arriba de la tabla y en la barra de estado. **Pausar** es distinto: no abre posiciones
nuevas, cancela sus entradas pendientes y sigue gestionando la que tenga abierta hasta cerrarla.

## Estrategias: la tabla y la ficha

Arriba: **Resultado**, **Hoy**, cuántas hay **En vivo**, **Operaciones cerradas** (tocá para ver todas) y **Cambios
pendientes**. La tabla muestra estrategia, estado (con «en pausa por contexto» cuando su filtro no la deja abrir),
tamaño y contrato, posición, hoy, neto, operaciones, profit factor, drawdown actual y límites. Tocando una se abre su
ficha:

- **Acciones**: Pausar o Reanudar, Cerrar posición, Detener, Detener y cerrar, Volver a arrancar (o Reanudar en el
  gráfico), Ver en el gráfico, Ver operaciones, Empezar de cero (borra su historial, solo sin posición) y Borrar.
- **Riesgo**: tamaño y límites, con **Aplicar riesgo** (el tamaño nuevo rige desde la próxima orden).
- **Condición de mercado**: se aplica ya, sin reiniciar, y muestra si con el mercado de ahora abriría operaciones.
- **Parámetros**: **Aplicar y reiniciar** (vuelve a calentar con la historia; si tiene posición, la sigue gestionando).
- **Portafolio** y **Actividad** (lo que va haciendo y lo que escribe con `Log`).

Cada estrategia lleva su propia contabilidad, aunque comparta la cuenta con otras o con tus operaciones manuales. Si
vuelve a arrancar con una posición en su contabilidad y la cuenta la tiene, la sigue gestionando y le vuelve a poner el
stop y el objetivo (sin duplicarlos si ya estaban en el broker); si la cuenta no la tiene, la cierra en su contabilidad
al último precio. Con un broker externo espera hasta 45 segundos a que informe la posición antes de decidir.

**Frenar todo** es el botón de emergencia: después de confirmar, detiene todas las estrategias en vivo (las de esta
sección y las de los gráficos), cancela sus órdenes y cierra sus posiciones a mercado.

## Operaciones

Todas las operaciones cerradas en vivo: estrategia, mercado, lado, entrada y salida con su precio, duración, cantidad,
puntos, resultado, MAE, MFE y «Captura» (cuánto del mejor momento se llevó). Se filtran por estrategia, se ordenan
(recientes, viejas, mejor o peor resultado, peor MAE) y tocando una se ve en el gráfico. Tené en cuenta que la
contabilidad de cada estrategia anota cada llenado al último precio de mercado del momento (no el que informa el
broker) y no descuenta comisiones.

## Portafolios

- El **automático** («Automático · todas en vivo») junta las que corren sin un portafolio tuyo. Con **Nuevo
  portafolio** armás otros con nombre, capital de referencia (25.000 por defecto) y estrategias; cada estrategia está en
  un solo portafolio tuyo.
- Cada uno muestra resultado (cerrado y abierto), operaciones, profit factor, drawdown máximo, Sharpe (con más de una
  semana de datos), correlación media entre sus estrategias, la curva conjunta, una tabla por estrategia y la matriz de
  correlación del resultado diario (rojo: ganan y pierden los mismos días; verde: se compensan).
- Botones: **Agregar estrategia nueva**, **Iniciar todas** (confirma si hay cuentas reales), **Detener todas** (las
  posiciones quedan como están), **Editar** y **Borrar** (sus estrategias siguen y pasan al automático).

## El agente de riesgo

Cada portafolio puede tener un agente de riesgo: Claude mira el resultado, la racha reciente, el drawdown contra su
historia, la volatilidad del resultado diario y la correlación de cada estrategia, y reparte el riesgo proponiendo el
tamaño de cada una y, si hace falta, su pérdida máxima del día y su drawdown máximo.

| Opción | Por defecto |
|---|---|
| Modo | **Propone**: cada cambio lo aprobás vos. **Automático**: aplica solo, y solo si ninguna estrategia del portafolio opera una cuenta real |
| Riesgo diario (% del capital) y drawdown máximo del portafolio (%) | 2 % y 10 % |
| Tamaño mínimo y máximo por estrategia | ×0,25 y ×3 (0 para que no abra nada nuevo) |
| Revisar cada (min) | 60 (como mínimo 10), si hubo al menos 3 operaciones nuevas, y cada 8 horas aunque no haya |
| Modelo y esfuerzo | Opus 5.5 con esfuerzo Extra; también Sonnet 5.5, Haiku 5.5 o Fable 5.1, de Bajo a Máximo |
| Indicaciones | Texto libre: «priorizá las de NQ», «nunca más de ×2 en cripto»… |

**Revisar ahora** lo corre en el momento. Como mucho hace 8 revisiones automáticas por día, cada una con 12 minutos y
un tope de uso de US$ 2 (si tu Claude Code admite esa opción); no tiene herramientas, solo los números que le pasa
Trading Hub. Lo que propone queda dentro de tus topes y aparece debajo del portafolio y en **Propuestas**. La tarjeta
del agente muestra su última revisión, el resumen y el costo estimado acumulado. Cómo se conecta con tu cuenta de
Claude: [Claude](./asistente-claude.md).

## Propuestas

Los cambios de tamaño sugeridos esperan tu aprobación, uno por uno o con **Aprobar todos**; los de cuentas reales piden
confirmación y rigen desde la próxima orden. Vienen de tres lados:

- **Sugerir con reglas**: con 10 operaciones o más y mirando el profit factor de las últimas 20, propone pausar (×0) si
  es menor a 0,7 con el drawdown cerca de su peor y al menos 15 operaciones recientes, reducir a la mitad si es menor a
  0,9 o el drawdown está cerca de su peor, y subir 25 % (hasta ×3) si es de 1,5 o más con expectativa positiva y poco
  drawdown.
- **Pedir análisis a Claude**: abre el laboratorio en Backtesting y le pide revisar la cartera en vivo y proponer.
- El **agente de riesgo** de cada portafolio.

## Mercado

Para cada mercado de las estrategias que corren: su **condición** (rango, mixto o tendencia alcista o bajista, con la
eficiencia de las últimas 24 horas y desde hace cuánto), su **volatilidad** (percentil del último mes), una tira con las
últimas 48 horas y qué estrategias operan ahí y si su filtro las deja abrir. Al final, una tabla dice en qué
condiciones opera cada estrategia, con la de ahora marcada. Se recalcula cada 30 segundos con los cierres por hora del
último mes, con la misma medida que usan los filtros de contexto.

## Al cerrar la ventana y al abrir

- **Barra de menú**: con la opción activada (lo está por defecto), cerrar la ventana deja a Trading Hub corriendo en la
  barra de menú de la Mac, con las estrategias trabajando. Su menú muestra las estrategias en vivo y las posiciones
  abiertas, y el panel tiene más detalle. Para cerrarlo del todo: ⌘Q o «Salir de Trading Hub». Ver
  [Primeros pasos](./primeros-pasos.md#el-ícono-en-la-barra-de-menú).
- **Reanudación automática**: unos segundos después de abrir, vuelven a arrancar solas las estrategias que corrían al
  cerrar (o al apagarse la Mac), con sus parámetros, tamaño y límites; las pausadas vuelven pausadas. Las de cuentas
  reales esperan que las reanudes, salvo que actives también esa opción. Un aviso resume qué se reanudó.
- **Modo sin dormir**: mientras haya estrategias en vivo o el agente de riesgo trabajando, la Mac no se duerme por
  inactividad, y si lo activás sigue con la tapa cerrada. Ver [Ajustes](./ajustes.md).
