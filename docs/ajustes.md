[← Volver al índice](./README.md)

# Ajustes

**Ajustes** (⌘,) tiene las preferencias generales de Trading Hub. Cada cambio se guarda al momento. Lo que es de cada
conexión está en [Conexiones](./conexiones.md), lo de cada gráfico en [Gráficos](./graficos.md), el stop y los objetivos de
cada instrumento en [Operar](./operar.md) y el agente de riesgo de cada portafolio en [En vivo](./en-vivo.md).

## General

| Opción | Por defecto | Qué hace |
|---|---|---|
| Apariencia | Oscuro | **Oscuro** (negro) o **Claro** (blanco) en toda la plataforma. También se cambia con el botón de sol y luna de la barra de arriba. Los colores propios de cada gráfico se mantienen. |
| Zona horaria de los gráficos | Hora de esta Mac | La hora del eje de tiempo, la cinta y las sesiones. Arriba están las habituales (Buenos Aires, Nueva York, Chicago, Londres, Madrid y UTC) y después todas. Es también la zona de las estrategias puestas en un gráfico: con «Hora de esta Mac», ellas usan UTC. |
| Sonidos de alerta | Activado | Suenan las alertas de precio, Big Trades y los avisos de indicadores. |

## Operar

| Opción | Por defecto | Qué hace |
|---|---|---|
| Confirmar órdenes reales | Activado | Antes de mandar una orden manual (Chart Trader, DOM o atajos) a una cuenta real, pide confirmación. Se suma a la opción de cada conexión: la orden sale sin preguntar solo si las dos están apagadas. No afecta a las estrategias en vivo. |
| Cuenta simulada | — | Muestra el saldo inicial (se cambia en la sección **Cuenta**). Las órdenes del simulador se llenan con la cola del libro y el tape real. |

## Estrategias en vivo

| Opción | Por defecto | Qué hace |
|---|---|---|
| Seguir en la barra de menú al cerrar la ventana | Activado | Cerrar la ventana no cierra Trading Hub: queda en la barra de menú de la Mac con las estrategias en vivo trabajando, y su ícono abre un panel con las estrategias y las posiciones en tiempo real. Para cerrarlo del todo, ⌘Q o «Salir de Trading Hub» en ese panel. |
| Reanudarlas solas al abrir Trading Hub | Activado | Las que corrían cuando se cerró Trading Hub (o se apagó la Mac) vuelven a arrancar unos segundos después de abrir, con sus parámetros, tamaño y límites; las pausadas vuelven pausadas. Apagado, esperan que las reanudes desde En vivo. |
| También las de cuentas reales | Apagado | Apagado (lo recomendado), las que operan dinero real esperan que las reanudes vos. Solo se puede activar si la anterior está activada. |
| Elegir el contrato solo (minis o micros) | Activado | Si el tamaño de una estrategia de futuros pide una fracción del mini (por ejemplo 1 NQ × 0,5), las órdenes van al micro con la misma exposición (5 MNQ), mirando el mismo gráfico. Con minis enteros usa el mini. Con una posición abierta sigue en el mismo contrato y cambia cuando queda plana. |

## Modo sin dormir

| Opción | Por defecto | Qué hace |
|---|---|---|
| Mantener la Mac despierta | Activado | Mientras haya estrategias en vivo o el agente de riesgo trabajando, la Mac no se duerme por inactividad (la pantalla sí puede apagarse). No pide contraseña y se suelta solo cuando no queda nada corriendo. |
| Seguir con la tapa cerrada | Apagado | Al cerrar la tapa, macOS duerme todo. Con esto activado, cuando haya algo corriendo Trading Hub te pide la contraseña de administrador (una vez por cada vez que lo abrís) y la Mac sigue funcionando con la tapa cerrada mientras Trading Hub esté abierto. Se deshace solo al cerrar Trading Hub, aunque se cierre de golpe. |
| Solo con el cargador conectado | Activado | Con batería, la Mac se duerme al cerrar la tapa aunque la opción anterior esté activada. Se puede tocar solo si la anterior está activada. |

Debajo se ve el estado: si la Mac está despierta y por qué, y si el modo de tapa cerrada está activo. Con la tapa cerrada
la Mac no ventila igual: no la guardes en un bolso así.

Por dentro, el primero usa una aserción de energía de macOS, como hace una app de video. El de la tapa cerrada corre un
ayudante con permisos de administrador que deja `pmset disablesleep` en 1 mientras exista un aviso en la carpeta de
datos y Trading Hub esté abierto (con «Solo con el cargador», solo mientras está enchufada) y lo vuelve a 0 al terminar.

## Atajos de teclado

Funcionan en la sección Trading y en los gráficos en ventanas aparte, sobre el gráfico activo. Mientras escribís en un
campo de texto solo responden los que usan ⌃, ⌥ o ⌘, o una tecla de función.

| Grupo | Acciones (con su atajo de fábrica) |
|---|---|
| Operar | Comprar a mercado, Vender a mercado, Comprar límite en el bid, Vender límite en el ask, Cerrar la posición, Dar vuelta la posición, Cancelar todas las órdenes, Cerrar el 25 %, Cerrar el 50 %, Stop a breakeven, Poner stop y objetivos de la plantilla, Cantidad: siguiente preset y Cantidad: preset anterior. **Vienen sin asignar** a propósito. |
| Gráfico | Período 1 minuto (⌥1), 5 minutos (⌥2), 15 minutos (⌥3), 1 hora (⌥4), 4 horas (⌥5) y diario (⌥6); estilo velas, footprint y Heikin Ashi; escala automática; ir al último precio; dibujar línea horizontal (⌥H); cursor (soltar herramienta). |
| Ventanas | Diseño de un gráfico, diseño de cuatro gráficos, pestaña siguiente (⌃Tab) y anterior (⌃⇧Tab), sacar el gráfico a una ventana, mostrar u ocultar el DOM, el Chart Trader del gráfico (⌥T) y el panel derecho. |

Para cambiar uno, hacé clic en su botón y presioná la combinación: Esc cancela y ⌫ lo deja sin asignar. Si otra acción
ya usaba esa combinación, se la saca y te avisa. **Restablecer los de fábrica** vuelve todo al principio. En cuentas
reales, los atajos de operar siguen pidiendo confirmación si «Confirmar órdenes reales» está activado.

## Datos

| Opción | Qué hace |
|---|---|
| Grabar lo que llega en vivo | Activado por defecto. Guarda cada operación que llega para reabrir los gráficos al instante, hacer replay y backtests. |
| Carpeta de datos | `~/Library/Application Support/Trading Hub`, con **Abrir** |
| Indicadores propios (C#) | `~/Documents/Trading Hub/Indicadores`, con **Abrir** |
| Estrategias (C#) | `~/Documents/Trading Hub/Estrategias`, con **Abrir** |

Más sobre lo guardado en [Datos](./datos.md) y [Privacidad](./privacidad.md).

## Acerca de

Al final está la versión de Trading Hub, la de .NET y el crédito del autor, con un enlace a su perfil.

## Dónde se guardan

Los ajustes van a `settings.json`, en la carpeta de datos. Ese archivo guarda también lo que elegís en otras partes de
la app: el tamaño de la ventana, los instrumentos y períodos favoritos, el panel derecho de Trading, la configuración del
DOM, el stop y los objetivos de cada instrumento, el estilo por defecto de cada dibujo, las plantillas con que arrancan
los gráficos y los indicadores nuevos, y la posición de la ventana del chat de Claude. Si el archivo se daña, Trading Hub
arranca con los valores de fábrica.
