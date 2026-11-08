[← Volver al índice](./README.md)

# Instalación

Trading Hub se compila en tu propia Mac a partir del código del repositorio. Un script arma la app «Trading Hub.app»,
la firma y la copia a Aplicaciones. La primera vez tarda unos minutos porque baja los paquetes que necesita.

## Requisitos

| Qué | Detalle |
|---|---|
| Mac | Apple Silicon (M1 o posterior). Por ahora la app se arma solo para `arm64`. |
| macOS | 12 o más nuevo. |
| SDK de .NET 10 | Hace falta solo para compilar. Se instala en `~/.dotnet`, sin contraseña de administrador. |
| Internet | La primera compilación baja los paquetes NuGet. |
| Claude Code | Opcional. Solo para las funciones con IA (ver [Claude Code](#claude-code-opcional)). |

La app que queda instalada es autocontenida: para usarla no necesitás tener .NET instalado.

Para revisar tu Mac, abrí la Terminal y corré:

```bash
uname -m                 # tiene que decir arm64
sw_vers -productVersion  # tiene que ser 12 o más
```

Si `uname -m` dice `x86_64`, tu Mac es Intel y por ahora no está soportada.

## Opción 1: pedírselo a Claude Code

Si usás Claude Code, puede hacer la instalación por vos. El repositorio trae un archivo `CLAUDE.md` con los pasos que
tiene que seguir.

1. Instalá Claude Code e iniciá sesión con tu cuenta de Claude.
2. Abrí una Terminal en la carpeta del repositorio y corré `claude`.
3. Escribile: «Instalá Trading Hub».

Claude Code va a revisar que tu Mac sea compatible, instalar el SDK de .NET 10 en `~/.dotnet` si falta, correr
`./build-app.sh --install`, verificar que la app encuentre tu Claude Code y abrirla. No te va a pedir datos personales
ni claves: no hacen falta para instalar. Si en Aplicaciones ya hay otra app llamada «Trading Hub», te tiene que
preguntar antes de reemplazarla.

## Opción 2: instalación manual

1. Fijate si ya tenés el SDK:
   ```bash
   ~/.dotnet/dotnet --list-sdks
   ```
   Si aparece una línea que empieza con `10.`, pasá al paso 3.
2. Instalá el SDK de .NET 10 en `~/.dotnet`:
   ```bash
   curl -sSL https://dot.net/v1/dotnet-install.sh -o /tmp/dotnet-install.sh
   bash /tmp/dotnet-install.sh --channel 10.0
   ```
   Después volvé a correr `~/.dotnet/dotnet --list-sdks` para confirmar.
3. Desde la carpeta del repositorio, compilá e instalá:
   ```bash
   ./build-app.sh --install
   ```
4. Abrí la app desde Aplicaciones o con:
   ```bash
   open "/Applications/Trading Hub.app"
   ```

No hace falta agregar `~/.dotnet` al `PATH`: el script lo agrega solo mientras corre.

### Qué hace `build-app.sh`

- Publica la app en modo Release para `osx-arm64`, autocontenida.
- Arma el paquete en una carpeta temporal, fuera del Escritorio: iCloud le agrega atributos a los archivos que rompen
  la firma.
- Firma la app y deja una copia en `dist/Trading Hub.app`, dentro del repositorio.
- Con `--install`, borra `/Applications/Trading Hub.app` si existe, copia la nueva y verifica la firma. Si todo salió
  bien, termina con «Instalado en /Applications/Trading Hub.app».

Sin `--install` solo arma `dist/Trading Hub.app` y no toca Aplicaciones.

## Firma ad hoc

Si no tenés un certificado de desarrollador, el script firma la app «ad hoc» (lo vas a ver como «Firmada ad hoc»).
En la Mac donde la compilaste abre sin avisos.

- Si copiás la app a otra Mac, la primera vez abrila con clic derecho → **Abrir**.
- Si tenés un certificado de firma, creá un archivo `build.local.env` en la carpeta del repositorio. El script lo lee
  solo y git lo ignora:
  ```bash
  SIGN_IDENTITY="nombre del certificado"
  BUNDLE_ID="com.tu-dominio.tradinghub"
  ```
  `SIGN_IDENTITY` tiene que coincidir con un nombre de `security find-identity -v -p codesigning`. Si no lo encuentra,
  firma ad hoc igual. Con un certificado fijo, macOS recuerda los permisos que le diste a la app entre actualizaciones.

## Claude Code (opcional)

Trading Hub no usa claves de API de Claude: corre la CLI de Claude Code que ya tenés instalada, con tu sesión. Lo que
hace Claude se descuenta de tu límite de uso normal. Se usa en dos lugares:

- **Backtesting → Claude**: un chat que escribe y corrige estrategias.
- **En vivo → Portafolios**: el botón «Pedir análisis a Claude», que propone cambios de tamaño que vos aprobás.

Sin Claude Code el resto de la plataforma funciona igual (gráficos, operar, datos, backtesting y estrategias en vivo).

La app busca la CLI en estos lugares y usa la versión más nueva que encuentre:

- `~/.local/bin/claude`, `~/.claude/local/claude`, `/opt/homebrew/bin/claude`, `/usr/local/bin/claude`,
  `~/.npm-global/bin/claude`;
- las carpetas del `PATH`;
- la copia que trae la app de escritorio de Claude.

Si `command -v claude` apunta a otro lugar, creá un enlace:

```bash
mkdir -p ~/.local/bin && ln -sf "$(command -v claude)" ~/.local/bin/claude
```

También podés indicar una CLI puntual con la variable de entorno `TRADINGHUB_CLAUDE_CLI`. Para iniciar sesión, corré
`claude` en la Terminal y usá `/login`. Si Claude Code pide iniciar sesión mientras usás el chat de Backtesting, la app
muestra el botón «Iniciar sesión en Claude Code».

## Dónde guarda tus datos

| Lugar | Qué hay |
|---|---|
| `~/Library/Application Support/Trading Hub` | Ajustes, conexiones (sin secretos), espacio de trabajo, historia de operaciones, dibujos, plantillas, historial de las cuentas, backtests y estrategias en vivo. |
| `~/Documents/Trading Hub/Estrategias` | Tus estrategias en C#. Adentro, `Reportes` guarda los reportes en Markdown. |
| `~/Documents/Trading Hub/Indicadores` | Tus indicadores en C#. |
| Llavero de macOS | Contraseñas, secrets y tokens de las conexiones, bajo el nombre «Trading Hub». |

Adentro de la carpeta de datos:

| Archivo o carpeta | Contenido |
|---|---|
| `settings.json` | Ajustes generales, atajos, DOM y plantillas de stop/objetivos. |
| `connections.json` | Las conexiones configuradas, sin contraseñas ni secrets. |
| `workspace.json` | Pestañas, gráficos, diseños y paneles abiertos. |
| `Ticks/` | Historia de operaciones: un archivo por conexión, instrumento y día. |
| `Klines/` | Velas bajadas de las conexiones (guardadas por mes), para cargar historia larga rápido. |
| `Drawings/` | Dibujos de cada instrumento. |
| `ChartTemplates/`, `IndicatorTemplates/`, `FootprintTemplates/` | Plantillas de gráfico, de indicador y de footprint. |
| `Accounts/` | Ejecuciones de cada cuenta (el historial de la sección Cuenta). |
| `Backtests/` y `Live/` | Corridas de backtesting y estado de las estrategias en vivo. |
| `crash.log` | Errores inesperados, por si tenés que reportar un problema. |

La primera vez que abrís la app se crean las carpetas de `Documents`. En `Indicadores` aparece un ejemplo
(`EjemploDeltaPorcentual.cs`) y en `Estrategias` se escriben estrategias de ejemplo cuando la carpeta está vacía.

Para abrir las carpetas desde la app: **Archivo → Abrir carpeta de datos**, **Código → Abrir carpeta de indicadores**,
**Código → Abrir carpeta de estrategias**, o los botones «Abrir» de **Ajustes → Datos**.

## Actualizar

1. Cerrá Trading Hub del todo con ⌘Q o con «Salir de Trading Hub» desde su ícono en la barra de menú. Cerrar la
   ventana no alcanza: de fábrica, la app sigue corriendo arriba a la derecha.
2. Traé la versión nueva:
   ```bash
   git pull
   ```
   Si bajaste el código como ZIP, bajá el nuevo y reemplazá la carpeta (guardá tu `build.local.env` si tenías uno).
3. Compilá e instalá:
   ```bash
   ./build-app.sh --install
   ```

También podés abrir la carpeta en Claude Code y pedirle «actualizá Trading Hub». Tus datos, conexiones, claves y
estrategias quedan como estaban porque viven fuera de la carpeta del repositorio.

## Desinstalar

1. Cerrá Trading Hub del todo (⌘Q o «Salir de Trading Hub»).
2. Antes de borrar nada, si querés sacar las claves del Llavero desde la app: en **Conexiones**, el botón «Eliminar»
   de cada conexión borra sus secretos. Las conexiones que vienen de fábrica no siempre se pueden eliminar; sus
   secretos se borran en el paso 4.
3. Borrá la app:
   ```bash
   rm -rf "/Applications/Trading Hub.app"
   ```
4. Borrá los secretos que queden en el Llavero: abrí **Acceso a Llaveros**, buscá «Trading Hub» y eliminá esos
   ítems. Desde la Terminal, este comando los borra uno por uno hasta que no queda ninguno:
   ```bash
   while security delete-generic-password -s "Trading Hub" >/dev/null 2>&1; do :; done
   ```
5. Si no vas a volver a usarla, borrá la carpeta de datos (historia, cuentas, ajustes). No se puede deshacer:
   ```bash
   rm -rf ~/Library/Application\ Support/Trading\ Hub
   ```
6. Tus estrategias e indicadores están en `~/Documents/Trading Hub`. Borralos solo si no los querés guardar.
7. Opcional: la carpeta del repositorio (con `dist/`), el SDK en `~/.dotnet` si no lo usa otro programa, y el enlace
   `~/.local/bin/claude` si lo creaste solo para Trading Hub.

## Si algo falla

- **`dotnet` no se encuentra**: el script usa `~/.dotnet` solo. Revisá que `~/.dotnet/dotnet --list-sdks` muestre
  una versión 10.
- **La compilación corta con un error**: leé el primer error que muestra la Terminal. Revisá que tengas internet (la
  primera vez baja paquetes) y que el SDK instalado sea el 10.
- **La app se cierra o algo no responde**: mirá `crash.log` en la carpeta de datos.
