[← Volver al índice](./README.md)

# Operar

Se opera desde la sección **Trading**: con el Chart Trader, con el DOM, desde el gráfico o con atajos de teclado. Cada
gráfico opera con la cuenta que le elijas; de entrada es la **Cuenta simulada**.

## Elegir la cuenta

Cada gráfico recuerda su cuenta. Se elige en el Chart Trader, en **Cuenta**. Siempre está la Cuenta simulada; las demás
aparecen según el instrumento del gráfico y tus [conexiones](./conexiones.md):

| Gráfico | Cuentas que aparecen |
|---|---|
| Cualquiera | Cuenta simulada |
| Binance Futures o Spot | Tu cuenta de Binance de esa conexión: «(testnet)» o «— REAL» |
| Futuros de Rithmic | Tus cuentas de Rithmic: «(práctica)» o «— REAL» |
| Futuros de CME de Rithmic o Tradovate | Tus cuentas de Tradovate: «(demo)» o «— REAL» |

Las cuentas de Rithmic aparecen cuando su sesión de órdenes termina de conectarse; mientras tanto el Chart Trader dice
por qué todavía no están. Con una cuenta real elegida, el Chart Trader muestra «CUENTA REAL» en naranja.

## El Chart Trader

![El Chart Trader del gráfico y, a la derecha, el panel con el DOM, sus botones de ejecución y la cinta](img/operar.png)

Está en dos lugares que muestran lo mismo:

- **En cada gráfico**, a la derecha. Se pliega con la flechita o con ⌥T, y cada gráfico opera solo lo suyo.
- **En el panel derecho**, arriba del DOM. Sigue al gráfico activo («Opera en el gráfico activo: …»). Si lo sacás a
  una ventana aparte, al cerrarla vuelve al panel.

De arriba hacia abajo:

| Parte | Qué hace |
|---|---|
| Cuenta | Dónde van las órdenes de este gráfico. |
| Cross trading | Ejecutar en otro instrumento (ver [Cross trading](#cross-trading)). |
| Cantidad | Con botones rápidos: 1, 2, 5 y 10 si el instrumento se opera en unidades enteras; fracciones en cripto. |
| Comprar MKT / Vender MKT | A mercado. |
| Comprar Ask / Vender Ask | Límite al precio del ask (abajo de cada botón se ve el precio). Comprar en el ask llena enseguida, con precio tope; vender en el ask espera en la oferta. |
| Comprar Bid / Vender Bid | Límite al precio del bid. Comprar en el bid espera en la demanda; vender en el bid llena enseguida, con precio tope. |
| Canc. compras / Canc. ventas | Cancela las órdenes de ese lado. |
| Cancelar todo / Revertir | Cancela todas las órdenes / da vuelta la posición. |
| Breakeven | Mueve el stop al precio de entrada. |
| Cerrar 25 % / 50 % / 75 % | Cierra esa parte a mercado; el stop y los objetivos se ajustan solos. |
| Cerrar posición | Cierra todo a mercado y cancela las órdenes. |
| Resultado | PnL abierto y la posición («LARGO 2 @ precio», «Plana · resultado del día»). |
| SL / TP automáticos | Stop y objetivos (ver [Stop y objetivos](#stop-y-objetivos)). |

El resultado y la posición se muestran debajo de los botones, así sus cambios no corren los botones de lugar.

## Tipos de órdenes

| Tipo | Cómo se manda | En la cuenta simulada |
|---|---|---|
| Mercado | MKT, Cerrar, Revertir, parciales | Llena al mejor bid o ask del libro (o al último precio si no hay libro). |
| Límite | Ask / Bid, clic en el DOM, clic derecho en el gráfico | Llena si el precio opera a través de tu precio, o si en tu precio se operó más volumen que el que tenías adelante en la cola. |
| Stop | Clic en el DOM más allá del precio (ver la tabla de abajo), clic derecho en el gráfico | Se dispara cuando opera el precio y llena a mercado. |

El stop loss y los objetivos solo pueden reducir la posición, nunca darla vuelta.

## Operar con el DOM

| Acción | Resultado |
|---|---|
| Clic en la columna Bid (u Órdenes de compra) | Compra límite en ese precio; si está en el ask o arriba, compra stop. |
| Clic en la columna Ask (u Órdenes de venta) | Venta límite en ese precio; si está en el bid o abajo, venta stop. |
| Clic derecho o ⌥ clic | Cancela tus órdenes de ese precio. |
| Arrastrar una orden tuya | La mueve a otro precio. |
| Rueda / botón de centrar | Desplaza la escalera / vuelve al precio. |

La cantidad es la del gráfico activo. Si activás **Doble clic para operar** en Configurar DOM, un clic simple ya no
manda órdenes (cancelar sigue funcionando con un clic). El ícono del signo de pregunta del DOM resume estos gestos.

Debajo del DOM están los **botones de ejecución** (se muestran u ocultan desde su encabezado): cantidad con botones
rápidos, Comprar MKT, Vender MKT, C. Bid, C. Ask, V. Bid, V. Ask, Cancelar, Revertir y Cerrar. Operan el gráfico activo.

## Operar desde el gráfico

- **Clic derecho** en un precio: «Comprar límite» y «Vender stop» si está abajo del último precio, «Vender límite» y
  «Comprar stop» si está arriba, con la cantidad del Chart Trader. También «Cancelar todas las órdenes».
- **Arrastrar** la línea de una orden la mueve de precio.
- **Clic derecho sobre una orden tuya**: cancelarla; en un objetivo, «Cantidad del objetivo» (25, 50, 75 o 100 % de la
  posición); en el stop, «Stop a breakeven»; en una orden de entrada pendiente, «Adjuntar stop y objetivos de la
  plantilla».
- **Con posición abierta**, el clic derecho ofrece «Cerrar la mitad a mercado», «Cerrar todo a mercado», «Stop a
  breakeven» y «Poner stop y objetivos de la plantilla».

## Stop y objetivos

La sección **SL / TP automáticos** del Chart Trader guarda una plantilla por instrumento (la misma en el panel del
gráfico y en el de la derecha):

1. Activá el interruptor de **SL / TP automáticos**.
2. Poné el **SL** en ticks desde la entrada.
3. Poné el **TP** en ticks. Con **+ objetivo** agregás hasta 3 objetivos parciales, cada uno con su porcentaje de la
   posición (el 100 % se reparte solo; la × de cada uno lo quita).
4. **Breakeven al TP1** (viene tildado) mueve el stop a la entrada cuando se llena el primer objetivo.

Con la plantilla activa, al abrir una posición se ponen solos el stop y los objetivos en OCO: cuando uno cierra la
posición, lo que queda se cancela. En una orden de entrada límite o stop, quedan adjuntos y se envían cuando se llena.
Si agrandás la posición, cubren todo lo abierto; si cerrás una parte, se ajustan a lo que queda.

**Aplicar a la posición** reemplaza el stop y los objetivos de la posición abierta por los de la plantilla.

También se ponen a mano en el gráfico: arrastrá los botones SL / TP de la posición o de una orden pendiente, o
arrastrá desde la línea de la posición (hacia la ganancia crea un objetivo; hacia la pérdida, el stop).

## Cross trading

Mirás un instrumento y las órdenes van a otro, por ejemplo mirás el ES y ejecutás en el MES. En el Chart Trader tocá
**Cross trading**, buscá el instrumento (primero aparecen los del mismo subyacente) y elegilo; «Ninguno» vuelve a
operar el del gráfico. Las órdenes, el stop y los objetivos van al instrumento elegido con los precios tal cual. Si los
precios de los dos difieren, el Chart Trader avisa «ojo: precios distintos».

## Atajos para operar

En **Ajustes → Atajos de teclado**, grupo **Operar**, podés asignar: comprar y vender a mercado, comprar límite en el
bid, vender límite en el ask, cerrar la posición, darla vuelta, cancelar todas las órdenes, cerrar el 25 % o el 50 %,
stop a breakeven, poner stop y objetivos de la plantilla, y pasar al siguiente o al anterior botón rápido de cantidad.
Vienen sin asignar. Funcionan sobre el gráfico activo y no se disparan mientras escribís en un campo de texto (salvo
que el atajo use ⌘, ⌥, ⌃ o una tecla de función).

## La sección Cuenta

**Cuenta** (⌘5) muestra una cuenta por vez. El botón de arriba abre la lista, agrupada por conexión: la Cuenta
simulada y tus cuentas de Rithmic y Tradovate.

Tarjetas: **Saldo**, **Resultado del día**, **PnL abierto**, **Realizado hoy**, **Resultado total** (en la simulada) o
**Disponible para operar** (en las del broker), y **Órdenes activas**. En las cuentas del broker el saldo es el que
informa Rithmic o Tradovate.

| Pestaña | Contenido |
|---|---|
| Posiciones | Contrato, lado, cantidad, precio promedio, PnL abierto, realizado hoy y botón «Cerrar». |
| Órdenes | Las de hoy, activas o terminadas, con su estado y botón «Cancelar». |
| Ejecuciones | Cada ejecución con hora, contrato, lado, cantidad, precio y comisión. |
| Operaciones | Cada operación cerrada: entrada, salida, duración y resultado. |
| Estadísticas | Hoy, 7 días, 30 días o todo: curva de saldo, resultado neto, % ganadoras, profit factor, drawdown máximo, expectativa, rachas, largos y cortos, y el resultado de cada día. |

Las filas de posiciones y órdenes no cambian de lugar: las nuevas se agregan abajo, así un clic no cae en la fila de
al lado. El historial se guarda en tu Mac y sobrevive al cerrar la app.

En la Cuenta simulada podés cambiar el **Saldo inicial** y usar **Reiniciar la cuenta simulada**: vuelve al saldo
inicial y empieza un historial nuevo (el anterior queda guardado aparte). Para reiniciar tenés que estar sin posiciones
ni órdenes.

## Seguridad

- **Nada real por defecto.** Cada conexión tiene su casilla de órdenes reales, apagada de entrada. Sin ella, la cuenta
  real no aparece para operar (ver [Conexiones](./conexiones.md#cuentas-reales-y-cuentas-de-prueba)).
- **Sin confirmaciones en las cuentas de prueba.** En la cuenta simulada y en las cuentas demo, de práctica o
  testnet, cada clic manda la orden al instante.
- **Confirmación en cuentas reales.** De fábrica, cada orden a una cuenta real abre la confirmación «Confirmar orden
  REAL» con el detalle y la cuenta. Lo mismo pasa al mover una orden, cerrar, revertir, cancelar desde los botones y
  usar parciales o breakeven. Para no tenerla tienen que estar apagadas las dos opciones: la de la conexión («Pedir
  confirmación antes de cada orden real» en Binance, «Confirmar cada orden real» en Rithmic) y **Ajustes → Operar →
  Confirmar órdenes reales**. La conexión de Tradovate no tiene esa casilla, así que sus órdenes reales siempre piden
  confirmación. En Cuenta, «Cerrar» y «Cancelar» de una cuenta real también piden confirmación.
- **Cantidad máxima por orden.** En las cuentas reales, una orden más grande que el máximo de la conexión se bloquea y
  la barra de estado dice «Orden bloqueada».
- **Límites de las estrategias.** Las estrategias automáticas tienen pérdida máxima del día y drawdown máximo
  (0 = sin límite), y en **En vivo** está **Frenar todo**, que detiene todas, cancela sus órdenes y cierra sus
  posiciones.
- **Avisos visibles.** Las cuentas reales se marcan en naranja con «— REAL» o «CUENTA REAL», en el Chart Trader y en
  Cuenta.
