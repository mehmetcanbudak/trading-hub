# Trading Hub en Windows

El soporte de Windows 10/11 x64 y ARM64 introducido en upstream v1.1 y los idiomas de v1.2 están incorporados en el código de esta integración. La fuente y la documentación principal de este fork son **en inglés**; la interfaz permite English, Español y 中文（简体）. Los ajustes antiguos del fork inglés siguen en inglés.

La guía mantenida del fork es [Windows installation and platform integration](docs/en/portar-a-windows.md). Este archivo conserva la referencia en español. Los instaladores de [las publicaciones del autor original](https://github.com/ralphtradesz/trading-hub/releases) son artefactos upstream: no se debe asumir que incluyen los cambios de seguridad del fork. Consultá el [estado de validación](docs/en/TRANSLATION_STATUS.md) antes de afirmar que una plataforma o una operación está certificada.

## Qué depende de cada sistema

| Área | macOS | Windows implementado |
|---|---|---|
| Empaquetado | `build-app.sh`, bundle Apple Silicon y firma | `build-windows.ps1` / `build-windows.sh`, publicación self-contained x64/ARM64 e instaladores NSIS por usuario |
| Credenciales | Llavero de macOS | Administrador de credenciales, `CredRead` / `CredWrite` / `CredDelete`; errores visibles |
| Instancia única | Ciclo de vida de macOS | Mutex/evento y exclusión del archivo de la carpeta de datos; una segunda apertura activa la ventana existente |
| Energía | IOKit y ayudante opcional para tapa cerrada | `SetThreadExecutionState`; la tapa depende de la política de Windows |
| Ventana y bandeja | Barra de menú, botones y atajos ⌘ | Bandeja, posición/DPI, botones de ventana y atajos Ctrl |
| Enlaces, archivos y sonidos | Integración nativa de Mac | Shell/Explorer y sonido de Windows |
| Claude Code | Rutas/Terminal de Mac | Ejecutable nativo y shims npm/pnpm/Yarn reconocidos, resueltos a Node o al ejecutable; sin enviar los prompts por `cmd.exe` |
| Texto | Fuentes de Mac y fallback | Cascadia Mono/Consolas, Segoe UI, fallback y composición CJK |

Los motores de datos, gráficos, indicadores, estrategias, backtest y laboratorio MCP siguen compartidos. No se renombran IDs, claves de API, campos guardados, clases de usuario ni carpetas por cambiar el idioma.

## Instalar o compilar

Para compilar: Git, .NET SDK 10 y [NSIS](https://nsis.sourceforge.io). Desde la raíz en PowerShell:

```powershell
winget install NSIS.NSIS
.\build-windows.ps1
```

En macOS con .NET SDK 10 y `makensis` instalado:

```bash
./build-windows.sh
```

Los resultados son `dist/TradingHub-Setup-x64.exe` y `dist/TradingHub-Setup-arm64.exe`. Los usuarios del instalador no necesitan instalar el runtime de .NET por separado. La instalación predeterminada es `%LOCALAPPDATA%\Programs\Trading Hub`, sin elevación. Cerrá completamente la app con su acción de salir antes de actualizar o desinstalar; el instalador espera o permite cancelar, no mata una app que pueda estar administrando órdenes.

Los datos permanecen en `%APPDATA%\Trading Hub`, los scripts bajo la carpeta conocida Documentos → `Trading Hub\Estrategias` / `Indicadores`, y los secretos en el Administrador de credenciales. La actualización y desinstalación no deben borrar esos datos ni credenciales. La primera cuenta es simulada.

## Plan de validación

1. Compilar y ejecutar las pruebas completas en macOS y Windows.
2. Verificar las auditorías de fuente inglesa, estructura histórica y cobertura de los 3300 registros upstream de traducción en ambos catálogos.
3. Ejecutar las escenas `english` y `safety` con datos temporales y proveedores falsos; `safety` también comprueba la salida y el reinicio. Para español/chino, capturar las siete páginas principales y revisar números, textos y glifos.
4. Probar instalación, apertura, segunda instancia, actualización y desinstalación nativas en x64 y ARM64; revisar credenciales de prueba, CLI, bandeja, DPI y ventanas.
5. Mantener aparte las pruebas autorizadas con brokers demo y las de energía/tapa/cierre de sesión en hardware real. Las pruebas falsas y la revisión de código no certifican ejecución bursátil ni OCO atómico.

Usá todas las variables de aislamiento de [Development](docs/en/desarrollo.md) y `TRADINGHUB_SLEEP_GUARD=off`. Un pedido de cancelación o cierre no equivale a confirmación: conciliá posiciones, órdenes y ejecuciones en las herramientas del broker antes de reintentar.

## El prompt para Claude Code

```text
Revisá el soporte de Windows de este fork sin romper macOS. Leé CLAUDE.md y el dashboard de validación, trabajá en una rama/worktree aislada y preservá el comportamiento de seguridad existente. La fuente nueva, los generadores y los ejemplos C# son ingleses; el idioma de la interfaz no cambia IDs ni datos persistentes.

Comprobá credenciales de prueba, instancia única, CLI y argumentos, bandeja/DPI, ventanas, idiomas, empaquetado y conservación de datos. Respetá las confirmaciones y los permisos existentes. Conservá los contextos de órdenes pendientes hasta recibir confirmación; nunca supongas posición cero porque falten datos.

Ejecutá los gates existentes con proveedores falsos y directorios temporales. Mostrá fallos reales, evidencia y límites; no uses cuentas reales, secretos personales, datos históricos del usuario ni cambios privilegiados de energía como fixtures. No avances el checkpoint upstream ni las referencias de los ports sin revisión y evidencia.
```

## Después

La integración se revisa mediante un PR en el fork, dirigido a `memoshi`; main y los worktrees Rust/Swift quedan aparte. Mantener sus referencias fijadas es una decisión separada. Linux requiere sus propias integraciones y validación nativas; soporte Windows no implica una publicación Linux.
