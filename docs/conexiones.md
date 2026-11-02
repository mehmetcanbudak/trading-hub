[← Volver al índice](README.md)

# Conexiones

En **Conexiones** (⌘4) configurás de dónde salen los datos de mercado y con qué cuentas operás. A la izquierda está la
lista de conexiones; a la derecha, el detalle de la que elegiste.

En la lista, cada conexión muestra un resumen («datos públicos · sin cuenta», «datos + cuenta testnet», el usuario y el
sistema de Rithmic…) y un punto de color: gris si está desactivada, verde o rojo según el resultado de la última prueba,
y neutro si todavía no la probaste.

## Agregar y probar una conexión

1. Tocá **Nueva conexión**, abajo de la lista, y elegí el tipo.
2. Completá los datos que pide (cambian según el tipo; están más abajo).
3. Tocá **Probar conexión**. Primero guarda y después prueba; el resultado aparece arriba del formulario.
4. Si cambiás algo, tocá **Guardar**.

Arriba del formulario también están:

- el **nombre** de la conexión (lo podés cambiar) y su tipo;
- el interruptor **Activada / Desactivada**: una conexión desactivada no aparece en el buscador de instrumentos ni da
  cuentas para operar;
- **Eliminar**, que borra la conexión y sus secretos del Llavero. Los datos grabados no se tocan. Las de tipo
  Simulador, Binance, Bybit y Rithmic solo se pueden eliminar si hay más de una del mismo tipo; si no, desactivalas.

Casi todos los tipos tienen además **Historia al abrir un gráfico** (de 30 minutos a 7 días): cuánta historia se pide
al abrir un instrumento. Lo que ya está grabado o descargado en [Datos](datos.md) carga al instante; el resto se le
pide a la conexión.

## Dónde quedan tus claves

Las contraseñas, los secrets de las API keys, los tokens y la API key de Databento se guardan en el **Llavero de
macOS**, nunca en archivos. En **Acceso a Llaveros** aparecen como contraseñas con el nombre «Trading Hub». Una vez
guardado, el campo ya no muestra el valor: dice «•••••••• (guardado en el Llavero)». Para reemplazarlo, escribí el
nuevo y guardá; si lo dejás vacío, queda el que estaba.

En `connections.json`, dentro de la carpeta de datos, solo quedan datos que no son secretos: el nombre, el usuario, el
servidor, el identificador público de la API key (por ejemplo, la API key de Binance o el Key ID de Alpaca) y las
opciones.

## Qué da cada tipo

| Conexión | Qué da | Qué necesitás | Órdenes |
|---|---|---|---|
| Simulador | Mercado simulado sin internet | Nada | Cuenta simulada |
| Binance Futures / Binance Spot | Datos de cripto e historia gratis | Nada para datos; API key y secret para operar | Testnet o real |
| Bybit | Datos de los perpetuos en USDT | Nada | No (solo datos) |
| Rithmic | Futuros de CME: datos, historia y órdenes | Usuario, contraseña, sistema y servidor | Práctica o real |
| AMP Futures | Lo mismo que Rithmic | Las credenciales de Rithmic que te manda AMP | Práctica o real |
| Tradovate | Futuros de CME: cuentas para operar y datos | Usuario, contraseña, CID y secret de la API key | Demo o real |
| cTrader | CFDs y forex de brokers con cTrader | Una aplicación de la Open API y autorizar tu cuenta | No (solo datos) |
| OANDA | CFDs de índices, oro y forex | Un token de la API | No (solo datos) |
| Alpaca | Acciones y ETF de EE.UU. | API key ID y secret key | No (solo datos) |
| Databento | Historia de futuros de CME desde 2010 | API key (pago por uso) | No (solo historia) |
| Interactive Brokers | Por ahora, solo prueba de conexión | TWS o IB Gateway con la API habilitada | No |

Cualquier conexión con datos sirve para operar con la **cuenta simulada**.

## Simulador

Mercado simulado con libro y operaciones realistas, ideal para probar sin conexión. Trae SIM-BTC, SIM-ETH, SIM-ES y
SIM-NQ. No necesita configuración.

## Binance Futures y Binance Spot

Los datos de mercado de Binance son públicos: sin cuenta ya tenés gráficos, DOM, cinta e historia. Para operar, la
sección **Cuenta para operar** pide:

1. Elegir **Testnet (dinero de prueba)** o **Cuenta real**. Viene en testnet.
2. Pegar la **API key** y el **API secret**. Creá la key solo con permiso de trading, sin retiros. El enlace «¿Cómo
   creo una API key?» abre la página de testnet o la de tu cuenta real, según lo que elegiste.
3. Con cuenta real, tildar **Permitir órdenes REALES desde Trading Hub**. Sin esa casilla la cuenta queda como «real
   (solo lectura)» y no aparece para operar.
4. Opcional: **Pedir confirmación antes de cada orden real** (viene tildada) y **Cantidad máxima por orden** (0 = sin
   límite).

## Bybit

Datos públicos de los perpetuos lineales en USDT. Operar con cuenta de Bybit todavía no está disponible.

## Rithmic

Conexión directa por R|Protocol, la API oficial de Rithmic: no hace falta tener R|Trader Pro abierto. Viene
desactivada; activala cuando cargues tus datos.

- **Usuario** y **Contraseña** (el botón del ojo muestra lo que escribís). Te los da tu broker o tu prop firm.
- **Servidor**: al abrir la sección, Trading Hub consulta los servidores de Rithmic y lista cada combinación de
  sistema y región con su latencia en milisegundos; las más rápidas se ven en verde. El buscador filtra (paper, 01,
  Frankfurt, el nombre de tu prop firm…) y el botón de recargar vuelve a pedir la lista. Colo 75 solo funciona desde el
  datacenter de la CME.
- **Usar un servidor que no está en la lista**: para cargar la dirección (`wss://…`) y el sistema a mano.
- **Cotizaciones agregadas**: Rithmic junta las actualizaciones del libro que llegan en el mismo instante. Debajo te
  avisa si el sistema elegido las ofrece; si no, se conecta sin ellas.
- **Tiempo de espera de conexión (ms)**: 10.000 por defecto.
- **Avanzado**: el **Nombre de la aplicación** (algunos sistemas exigen uno aprobado) y los **Productos para el
  buscador** (ES, NQ, MES, MNQ, CL…). De cada producto se busca el contrato del mes, por ejemplo ES → ESZ6.

Tus cuentas de Rithmic aparecen en el Chart Trader de los gráficos de Rithmic y en Cuenta cuando la conexión está
activa y la sesión de órdenes termina de conectarse. Rithmic deja una sola sesión por usuario: si entrás con el mismo
usuario desde otro programa, una de las dos sesiones se corta.

### Si Rithmic no conecta o no te deja operar

- **«Rithmic rechazó el usuario… (permission denied)»**: el **Servidor** tiene que decir el mismo sistema que te dio
  tu broker o tu prop firm (en ATAS, «Rithmic paper trading» es «Rithmic Paper Trading»; las prop firms tienen el suyo:
  buscalo por el nombre). Revisá la contraseña con el botón del ojo. Si todo coincide, tu usuario puede no tener
  habilitado el acceso por API (R|Protocol): eso lo habilita Rithmic o tu broker, y algunos sistemas piden además un
  **Nombre de la aplicación** aprobado (en **Avanzado**).
- **Conecta, ves los datos, pero no podés operar tu cuenta** (no aparece en el Chart Trader o en Cuenta dice «solo
  lectura»): las cuentas de Rithmic 01 y de las prop firms son reales. Tildá **«Permitir órdenes en cuentas reales»**
  en la conexión y guardá (ver [Cuentas reales y cuentas de prueba](#cuentas-reales-y-cuentas-de-prueba)).
- **Se desconecta sola**: Rithmic deja una sola sesión por usuario. Cerrá R|Trader Pro, ATAS, NinjaTrader o cualquier
  otro programa conectado con el mismo usuario.

## AMP Futures

AMP opera sobre Rithmic: te manda un usuario y una contraseña de Rithmic. La conexión se crea desactivada, con el
servidor de Chicago y el sistema «Rithmic Paper Trading» (la demo). Para la cuenta real elegí «Rithmic 01» en el
servidor más cercano. El resto es igual que en Rithmic.

## Tradovate

Conexión directa a la API oficial de Tradovate.

1. Elegí **Demo (simulada de Tradovate)** o **Cuenta real**. Viene en demo.
2. Completá **Usuario** y **Contraseña** de Tradovate.
3. Completá el **CID** y el **Secret** de tu API key. El enlace «¿Dónde saco la API key? (Settings → API Access)» abre
   Tradovate.
4. Revisá los **Productos para el buscador** (de cada uno se usa el contrato del mes).
5. Para operar una cuenta real, tildá **Permitir órdenes en cuentas reales de Tradovate**. Opcional: **Cantidad máxima
   por orden**.

Tus cuentas aparecen en el Chart Trader y en Cuenta, y se pueden operar desde los gráficos de futuros de CME de
Tradovate o de Rithmic. Tradovate solo da datos de mercado por API si tu cuenta tiene habilitados los datos de
CME para API (una licencia aparte); sin eso igual podés operar mirando los gráficos de otra conexión. Tradovate deja
una sola sesión de datos por usuario: si abrís Tradovate Trader con el mismo usuario, puede cortar la de Trading Hub.

## cTrader

Gratis con cualquier broker que tenga cTrader y una cuenta demo: CFDs de índices, forex, oro y petróleo, con historia
tick a tick.

1. **Tu aplicación (una sola vez)**: creá una aplicación en openapi.ctrader.com (entrás con tu cTrader ID) y agregale
   la dirección de redirección que muestra la pantalla (`http://127.0.0.1:53682/ctrader`; el botón «Copiar» la copia).
   Después pegá acá el **Client ID** y el **Secret**.
2. **Autorizar tu cuenta**: elegí **Cuenta demo** o **Cuenta real** y tocá **Conectar con cTrader**. Se abre el
   navegador; entrás con tu cTrader ID y autorizás. Tenés 5 minutos. También podés pegar un token de acceso (el del
   «Playground» de tu aplicación).
3. **Número de cuenta**: si el token tiene varias cuentas, el login de la que querés usar (0 = la primera).

Trading Hub guarda el token en el Llavero y lo renueva solo. Los CFDs no tienen operaciones reales: cada cotización
(bid) cuenta como una operación de volumen 1, así que el footprint y el delta son aproximados.

## OANDA

Gratis con una cuenta de práctica: CFDs de índices (NAS100_USD, SPX500_USD, US30_USD, DE30_EUR…), oro, petróleo y
forex, con años de historia en velas de 5 segundos.

- **Cuenta de práctica (gratis)** o **Cuenta real**.
- **Token**: lo generás en «Manage API Access» (hay enlaces para crear la cuenta y el token).
- **Cuenta**: el número 101-001-…; vacío usa la primera del token.

Trading Hub usa el token solo para datos; no manda órdenes a OANDA. Como en cTrader, cada cotización (precio medio)
cuenta como una operación de volumen 1.

## Alpaca

Con la cuenta de práctica (gratis, desde cualquier país) tenés la historia tick a tick de todas las bolsas de EE.UU.
desde 2016, salvo los últimos 15 minutos. Sirve para estudiar los índices a través de SPY, QQQ, IWM y DIA.

- **API key ID** y **Secret key** de tu cuenta de práctica.
- **Datos en vivo**: IEX (gratis, una bolsa) o SIP (todas las bolsas, plan pago).

Las claves se usan solo para datos; no se mandan órdenes a Alpaca.

## Databento

Historia tick a tick de los futuros de CME (NQ, ES, MNQ, MES, CL, GC…) desde 2010, con el lado agresor real de cada
operación. Es pago por uso; las cuentas nuevas traen créditos gratis.

- Solo pide la **API key** (`db-…`). Hay enlaces para crear la cuenta y ver los precios.
- **Probar conexión** muestra desde y hasta qué fecha hay datos y cuánto cuesta un día de NQ tick a tick. No baja nada.
- No da datos en vivo ni aparece en el buscador de instrumentos: se usa desde **Datos → Futuros de CME con
  Databento**. Ver [Datos](datos.md#databento).

## Interactive Brokers

Abrí TWS o IB Gateway y habilitá la API (Configure → API → Settings). Completá **Host**, **Puerto** (habituales: 7497
TWS paper, 7496 TWS real, 4002 Gateway paper) y **Client ID**. Por ahora Trading Hub solo verifica la conexión: no da
datos ni órdenes.

## Cuentas reales y cuentas de prueba

Todas las cuentas para operar aparecen en el selector **Cuenta** del Chart Trader. Siempre está la **Cuenta
simulada**. Las demás dependen de la conexión:

| Conexión | Cuenta de prueba | Cuenta real |
|---|---|---|
| Binance | Testnet: opera directo | Con «Permitir órdenes REALES desde Trading Hub» |
| Rithmic y AMP | Sistemas «Rithmic Paper Trading» y «Rithmic Test»: operan directo | Cualquier otro sistema (Rithmic 01, prop firms…), con «Permitir órdenes en cuentas reales» |
| Tradovate | Demo: opera directo | Con «Permitir órdenes en cuentas reales de Tradovate» |

Sin esa autorización, una cuenta real no aparece en el selector del Chart Trader. Las de Rithmic y Tradovate quedan
en **solo lectura** en la sección Cuenta: ves el saldo, las posiciones y el historial, pero no podés mandarles
órdenes. Las cuentas reales se marcan con «— REAL» en el selector y con el aviso «CUENTA REAL».

### Habilitar el dinero real

1. Probá antes todo con la cuenta simulada o con la cuenta de prueba del broker.
2. En la conexión, pasá a la cuenta real: en Binance, con el interruptor **Testnet / Cuenta real**; en Tradovate, con
   **Demo / Cuenta real**; en Rithmic y AMP, eligiendo el sistema real en **Servidor**.
3. Tildá la casilla de órdenes reales de esa conexión.
4. Poné una **Cantidad máxima por orden**: Trading Hub bloquea las órdenes más grandes.
5. Guardá y probá la conexión.

Cómo se confirman las órdenes reales y qué límites hay está en [Operar](operar.md#seguridad).
