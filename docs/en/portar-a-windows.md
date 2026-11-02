[← Documentation index](README.md)

# Windows installation and platform integration

Windows support introduced upstream in v1.1 and the v1.2 runtime languages are implemented in this source integration. The platform code is no longer just a porting plan. **Implementation and cross-platform builds do not certify native desktop behavior or brokerage execution.** Consult the [validation dashboard](TRANSLATION_STATUS.md) for actual evidence, not a release promise.

## Install or build

Target systems are **64-bit Windows 10/11**, either Intel/AMD **x64** or Windows on ARM **ARM64**. Installers are self-contained: end users do not need a separate .NET runtime. Claude Code is optional and uses your own signed-in CLI/session.

The original author's [upstream releases](https://github.com/ralphtradesz/trading-hub/releases) offer `TradingHub-Setup-x64.exe` and `TradingHub-Setup-arm64.exe`. Those are upstream artifacts, **not fork releases**, and must not be assumed to include this fork's safety work. No published fork installer is claimed here. Verify publisher/source and signatures; an unsigned installer may trigger SmartScreen. Do not bypass warnings for an untrusted download.

To package this checkout on Windows, install Git, .NET SDK 10 and [NSIS](https://nsis.sourceforge.io), then run from the repository root:

```powershell
winget install NSIS.NSIS
.\build-windows.ps1
# Optional explicit package version:
# .\build-windows.ps1 -Version 1.2.0
```

From macOS with .NET SDK 10:

```bash
brew install makensis
./build-windows.sh
# Optional explicit package version: ./build-windows.sh 1.2.0
```

Both scripts publish `src/TradingHub.App/TradingHub.App.csproj` in Release with `--self-contained true` for `win-x64` and `win-arm64`, replacing their respective staging directories `dist/win-x64` and `dist/win-arm64`. NSIS consumes `installer/TradingHub.nsi` and writes `dist/TradingHub-Setup-x64.exe` and `dist/TradingHub-Setup-arm64.exe`. Without an override the version comes from project metadata. A publish-only build is not an installer validation.

## Per-user installation, updates and uninstall

- Default application directory: `%LOCALAPPDATA%\Programs\Trading Hub`; no administrator elevation. Start-menu/Desktop shortcuts and an HKCU uninstall entry are created.
- The installer supports English, Spanish and Simplified Chinese, following Windows with English fallback. Runtime language is selected separately in [Settings](ajustes.md#language-and-regional-display).
- Fully quit Trading Hub before update/uninstall, using **Quit Trading Hub** in the tray or **Alt+F4**. A normal window close can merely hide it. Resolve the protection-aware quit dialog first; never force-kill a process managing orders to satisfy an installer.
- The installer waits for `TradingHub.exe` to stop and offers Retry/Cancel rather than forcibly terminating it. Updates replace application files only. Uninstall through Windows Settings → Apps → Trading Hub; user data and stored credentials are intentionally retained.
- First startup provides a simulated account; do not enable real-order permissions to test installation.

| Persistent location | Contents |
|---|---|
| `%APPDATA%\Trading Hub` | `settings.json`, nonsecret `connections.json`, workspace, history, accounts and live/backtest state. |
| Windows Documents known folder → `Trading Hub\Estrategias` | Strategies and `Reportes` reports. The known folder may be redirected; do not assume a hard-coded `C:\Users` path. |
| Windows Documents known folder → `Trading Hub\Indicadores` | Custom C# indicators. |
| Windows Credential Manager | Native generic credentials with stable targets `Trading Hub/<account>`, scoped to the current user on this computer. |

The product identity `Trading Hub`, Spanish subdirectory names, saved JSON/CSV fields, broker payload keys, order IDs and strategy parameter names do not change with display language. `TRADINGHUB_KEYCHAIN_FILE` selects a plaintext **test-only** fake store, never a production credential fallback.

## Implemented platform seams

| Area | Windows implementation | macOS preserved behavior |
|---|---|---|
| Startup/single instance | Windows startup skips native Mac display waiting; a second launch requests the existing window instead of another writer for the same data root. | Mac display readiness and platform lifecycle remain separate. |
| Secrets | Native `CredRead`/`CredWrite`/`CredDelete` via Credential Manager; actual failures are reported. | Keychain service `Trading Hub`. |
| Power | `SetThreadExecutionState` inhibits idle system sleep while live strategies/agent work runs; display may sleep. Windows lid closure follows OS power settings, not the Mac helper. | IOKit assertions and optional privileged closed-lid helper. |
| Tray | Tray icon, cursor/taskbar-aware panel placement and right-click menu. | Menu-bar icon/panel. |
| Shell/audio | Windows shell open/reveal for links, files and reports; platform sound handling. | Native open/reveal and Mac sounds. |
| Claude | Discovers native `claude.exe` and supported npm/pnpm/Yarn `.cmd` shims through user install locations/PATH; resolves shims to Node or a native executable rather than sending prompts through `cmd.exe`. Unrecognized shims fail visibly. Login opens a terminal. `TRADINGHUB_CLAUDE_CLI` overrides discovery. | Supported Mac CLI/desktop locations and Terminal login. |
| Input/window | Ctrl-based command shortcuts, native caption area, platform wording and DPI-aware tray placement. | Command shortcuts and Mac title bar. |
| Text | Windows monospace fallbacks Cascadia Mono/Consolas; Segoe UI chart/report text and multilingual glyph fallback/shaping. | Menlo/SF Mono and Helvetica Neue with fallback/shaping. |

Windows cannot override a laptop's lid-close policy. If choosing “Do nothing” in Windows Power Options, understand ventilation/battery risks; never put a running closed-lid laptop in a bag. See [Settings](ajustes.md#keep-awake).

## Validation and release gates

Shared-runtime tests, headless scenes and cross-publishing are distinct from native integration. **Native Windows on both architectures, real/demo brokerage execution, and physical macOS sleep/lid/logout/quit behavior are not certified by this documentation.** Before a release, record actual results for:

1. Fresh install, launch, second launch, update and uninstall on x64 and ARM64; multilingual installer text and retained data/credentials.
2. Credential Manager round-trip/delete/failure reporting under isolated test identities, without using personal secrets.
3. Claude native executable and `.cmd` shim discovery, version/login paths, spaces in paths and proper argument quoting.
4. Tray/window hide/show/quit, taskbars and multiple monitors at differing DPI, title-bar dragging/maximize and Ctrl shortcuts.
5. English/Spanish/Simplified Chinese controls, charts and reports; numeric culture, CJK glyphs/shaping, keyboard text entry and migration of old English settings.
6. Idle sleep/release, display sleep, laptop lid behavior and application lifecycle on real hardware; separately retain macOS lifecycle/power checks.
7. Simulated/fake-provider partial fills, pending cancels, reconnect/replay, inherited protection and quit/restart acknowledgment paths; then separately authorized demo-provider scenarios. A demo session is not evidence of real-money certification.

Build/test/UI commands and temporary-root rules are in [Development](desarrollo.md). Never use real history, credentials, orders or paid calls as test fixtures. A cancel/close request is not confirmation; reconcile uncertain exposure and working orders in the broker's own tools before retrying.

## Maintaining platform parity

Use a new isolated worktree for platform updates and preserve the existing protection-aware order/quit behavior; [the sync guide](upstream-sync.md) records the integration sequence. Keep native APIs behind their existing platform seams, report credential/startup failures, and retain unresolved order contexts rather than inferring a flat account. New UI source, generators and C# examples stay English; runtime catalogs supply Spanish/Chinese display text without renaming persistent identities.

Rust/Swift references require a separate reviewed decision; a C# merge or successful package build does not advance their pinned source. Linux still needs native secret/power/shell implementations and validation. Do not describe a Windows source integration as a Linux release or a native-hardware certification.

