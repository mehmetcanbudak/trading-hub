[← Volver al índice](./README.md)

# Privacidad y seguridad

Trading Hub corre entero en tu Mac: la app, tus datos, tus estrategias, los backtests y las claves de tus conexiones.
No tiene servidores propios, no pide crear una cuenta y no manda telemetría ni estadísticas de uso. Solo se conecta a lo
que vos configurás, y cuando lo usás.

## Qué queda en tu Mac

| Lugar | Qué hay |
|---|---|
| `~/Library/Application Support/Trading Hub` | Ajustes, conexiones (sin secretos), espacio de trabajo, historia tick a tick, velas, dibujos, plantillas, historial de las cuentas, backtests, estrategias en vivo y sus portafolios |
| `~/Documents/Trading Hub` | Tus estrategias (con sus reportes en `Estrategias/Reportes`) y tus indicadores |
| Llavero de macOS | Contraseñas, secretos, tokens y la API key de Databento, bajo el nombre «Trading Hub» |

El detalle de cada archivo está en [Instalación](./instalacion.md#dónde-guarda-tus-datos). Lo que tiene que ver con Claude
también queda en la carpeta de datos: las conversaciones (`Chats`), las imágenes del gráfico que miró el laboratorio
(`Capturas del laboratorio`, las últimas 60) y la configuración del laboratorio (`claude-lab-mcp.json`). Los errores
inesperados se anotan en `crash.log`, que no sale de tu Mac.

## Tus claves

- Las contraseñas, los secretos de las API keys, los tokens y la API key de Databento van al **Llavero de macOS**,
  nunca a archivos. Trading Hub los lee y los escribe con la herramienta `security` de macOS.
- En `connections.json` quedan solo datos que no son secretos: nombre, usuario, servidor, la parte pública de la API key
  (la API key de Binance o el Key ID de Alpaca) y las opciones. Ver [Conexiones](./conexiones.md#dónde-quedan-tus-claves).
- Al borrar una conexión se borran también sus secretos del Llavero.
- Las claves no están en archivos que Claude pueda leer, y ninguna herramienta del laboratorio las lee.

## Qué se conecta a internet y cuándo

| Qué | Cuándo | Con quién |
|---|---|---|
| Datos de mercado | Al abrir un gráfico o un instrumento de una conexión activada | El exchange o broker de esa conexión (Binance, Bybit, Rithmic, Tradovate, OANDA, Alpaca, cTrader) |
| Historia | Cuando bajás días en Datos, o Claude con `download_data` | Los archivos públicos de Binance y Bybit, o el servidor de tu conexión |
| Historia en velas del exchange | Para cargar historia larga en un gráfico y para el contexto de mercado de los días sin ticks guardados | El exchange del mercado abierto; en cripto, las velas públicas de Binance o Bybit, sin abrir ninguna sesión |
| Órdenes | Cuando operás vos o una estrategia en una cuenta de un broker | Ese broker. La cuenta simulada no manda nada afuera |
| Databento | Al probar la conexión (consulta el rango disponible y el costo de un día de NQ), al tocar **Calcular costo** y al **Descargar**, que antes te muestra el costo y te pide confirmar | Databento, con tu API key |
| Claude Code | Cuando usás el asistente, el laboratorio o el agente de riesgo | Anthropic, con tu cuenta, a través de la CLI de Claude Code |
| Interactive Brokers | Solo cuando probás la conexión | Tu TWS o IB Gateway (por defecto, en tu propia Mac) |
| Enlaces | Solo si los tocás (páginas de alta de cada servicio, la autorización de cTrader, el crédito del autor) | Se abren en tu navegador |

Nada más: no hay chequeo de actualizaciones ni envío de errores. Para compilar la app hacen falta internet y los paquetes
de NuGet, pero eso pasa al instalarla, no al usarla.

## Claude

Lo que hace Claude pasa por tu Claude Code y tu cuenta: lo que le escribís, los archivos que lee (tus estrategias e
indicadores) y lo que le devuelven las herramientas (métricas, operaciones, resúmenes de datos, imágenes del gráfico y,
si se lo pedís, el estado de tu cartera en vivo) viajan a Anthropic. Trading Hub lo arranca dentro de la carpeta de
estrategias, con permiso para editar solo ahí, en la de indicadores y en la de capturas, con herramientas de archivos (sin
terminal ni web) y sin tus otros servidores MCP. El agente de riesgo ni siquiera tiene herramientas: recibe los números
del portafolio y devuelve una propuesta. Lo que Claude Code haga con esos datos depende de tu cuenta y de las políticas de
Anthropic. Ver [Claude](./asistente-claude.md).

## Dinero real

- Trading Hub arranca con la **cuenta simulada**. Las órdenes reales están apagadas en cada conexión hasta que actives
  «Permitir órdenes reales» (ver [Conexiones](./conexiones.md#cuentas-reales-y-cuentas-de-prueba)).
- Las órdenes manuales a cuentas reales piden confirmación si así lo dejaste (ver [Operar](./operar.md#seguridad)), y
  lanzar o reanudar a mano una estrategia en una cuenta real se confirma una vez. La reanudación automática al abrir no
  pregunta: por eso, para las cuentas reales, viene apagada.
- Las estrategias en vivo tienen tamaño, pérdida máxima del día y drawdown máximo, y **Frenar todo** las detiene a todas
  (ver [En vivo](./en-vivo.md)). El agente de riesgo nunca aplica cambios solo si hay una cuenta real en el portafolio.

## Permisos que puede pedir macOS

- **Contraseña de administrador**: solo si activás «Seguir con la tapa cerrada» en Ajustes, para que la Mac no se duerma
  con la tapa cerrada mientras Trading Hub esté abierto. Se deshace solo al cerrarlo.
- **Controlar la Terminal**: si tocás «Iniciar sesión en Claude Code», Trading Hub abre la Terminal con `claude /login`.
- La app se firma «ad hoc» al compilarla en tu Mac (ver [Instalación](./instalacion.md#firma-ad-hoc)).

## Las herramientas de desarrollo no tocan tus datos

Las pruebas y las escenas sin pantalla usan carpetas temporales (`TRADINGHUB_DATA_DIR`, `TRADINGHUB_STRATEGIES_DIR`,
`TRADINGHUB_INDICATORS_DIR`) y un archivo de claves de mentira (`TRADINGHUB_KEYCHAIN_FILE`) en vez del Llavero. Las
escenas copian tu configuración y tu historia a una carpeta temporal, sacan las API keys, apagan las órdenes reales y
llevan Rithmic a un servidor falso. Ver [Desarrollo](./desarrollo.md).

## Borrar todo

1. Cerrá Trading Hub del todo (⌘Q o «Salir de Trading Hub» en la barra de menú).
2. Borrá `~/Library/Application Support/Trading Hub` y, si querés, `~/Documents/Trading Hub`.
3. En **Acceso a Llaveros**, buscá «Trading Hub» y borrá esas contraseñas.
4. Borrá la app de Aplicaciones (ver [Instalación](./instalacion.md#desinstalar)).
