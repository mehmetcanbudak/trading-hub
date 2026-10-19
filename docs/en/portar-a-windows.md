[← Documentation index](README.md)

# Port Trading Hub to Windows with Claude

.NET 10, Avalonia and SkiaSharp are cross-platform. Most data/feed/chart/backtest/strategy/indicator logic is OS-independent, but the current distribution targets Apple Silicon macOS. The English branch now passes a shared Windows build, all 222 regression tests and English source/documentation auditing in CI, plus win-x64 cross-publishing. Native Windows startup, secure credentials, power/tray/shell integration and packaging remain unverified. **This is still an implementation plan, not a tested Windows desktop release.**

On the target PC, install 64-bit Windows 10/11 (x64 or ARM64), [Git](https://git-scm.com), [.NET SDK 10](https://dotnet.microsoft.com/download) and a supported [Claude Code](https://claude.com/claude-code) distribution, then sign in. Use the provider's current Windows installation instructions.

## macOS-specific inventory

| Location | Current behavior | Windows adaptation |
|---|---|---|
| build-app.sh | osx-arm64 publish, .app bundle, codesign, ditto. | build-windows.ps1 for self-contained win-x64/win-arm64, ICO and optional Inno Setup/MSIX. |
| Connections.cs / Keychain | /usr/bin/security and Keychain. | Credential Manager via advapi32 CredWrite/CredRead/CredDelete, or user-bound DPAPI encrypted storage. Preserve Get/Set interface; never plaintext secrets. |
| SleepGuard.cs | IOKit power assertions and privileged pmset closed-lid helper through osascript. | SetThreadExecutionState with ES_CONTINUOUS and ES_SYSTEM_REQUIRED. Hide unsupported closed-lid controls or explain Windows power settings. |
| Program.cs | CGGetActiveDisplayList startup display wait. | Guard with OperatingSystem.IsMacOS; skip on Windows. |
| MenuBar.cs | Avalonia TrayIcon and objc_msgSend pointer position. | Windows tray icon, GetCursorPos and taskbar-aware popup placement. |
| MainWindow Shell / StrategiesView.Reports | macOS open for URLs/folders/reports. | ProcessStartInfo with UseShellExecute; explorer.exe /select for revealing a file. |
| TradingView.cs | afplay sounds. | Supported WAV/SoundPlayer or Avalonia audio mechanism; retain sound assets. |
| ClaudeCode.cs | macOS CLI paths/PATH and osascript Terminal login. | Discover claude.exe/claude.cmd in USERPROFILE/.local/bin, APPDATA/npm, LOCALAPPDATA/Programs and PATH; launch login with cmd or Windows Terminal. |
| MainWindow title bar | Left-side macOS traffic lights and extended client decorations. | Reserve right-side Windows caption-button space; test dragging/maximize. |
| KeyModifiers.Meta and shortcut hints | Command key. | Shared platform command modifier, Control on Windows; show Ctrl in labels. |
| Menlo font usages | macOS monospace. | Cascadia Mono, Consolas, monospace through the existing shared UI font convention. |
| Product paths | SpecialFolder-based application data/Documents. | Verify APPDATA/Trading Hub and Documents/Trading Hub; no arbitrary data relocation. |
| Platform wording | Mac, Keychain, menu bar, ⌘Q. | Platform-appropriate PC, Credential Manager, system tray and Windows quit/close shortcuts. |

Feeds/protocols, backtests, walk-forward, Skia rendering, Roslyn and MCP are broadly portable, but must still be tested on each target architecture.

## Staged plan

1. **Build/start:** build the solution and run tests on Windows. Guard native calls with OS checks; use small platform seams only where necessary. Keep macOS working.
2. **Secure credentials:** implement Windows storage behind the same contract, with fake stores for tests that never read/write personal credentials.
3. **System integration:** power, tray, URL/folder opening, audio and CLI discovery/login.
4. **Interface:** title bar, Ctrl bindings, fonts and platform-specific English text. Run relevant headless scenes.
5. **Packaging:** self-contained PowerShell build/icon, optional installer.
6. **Verification:** unit/UI tests and manual simulation/public-data checks before any real-money consideration.

## Prompt for Claude Code

Clone this fork's English branch on Windows, start Claude in the repository and paste:

```text
Port Trading Hub to Windows without breaking macOS. It is a C#/.NET 10 desktop app using
Avalonia and SkiaSharp, solution TradingHub.slnx. First read CLAUDE.md, README.md,
docs/en/desarrollo.md and docs/en/portar-a-windows.md.

Rules:
- Preserve the macOS build and behavior. Guard native operations with OS checks or small,
  necessary platform implementations; do not duplicate complete source files.
- Never store credentials in plaintext. Use Windows Credential Manager or user-bound DPAPI.
  Tests must use fake stores and temporary roots, never personal credentials/data.
- Preserve existing account permissions, confirmations, order routing and risk limits.
  Do not add/remove order-control actions as incidental porting work.
- Keep new interface text English. Preserve stable storage/JSON/CSV/strategy identifiers.
- Read current official API documentation before implementing platform integrations.

Work in stages, building TradingHub.slnx and running tests/TradingHub.Tests after each:
1. Windows startup/build: remove unconditional macOS display waiting; check paths/fonts.
2. Windows secret storage behind the existing credential interface.
3. Power assertions, tray/popup placement, shell open/reveal, sound and Claude discovery/login.
4. Windows title bar, Ctrl shortcuts and platform-aware wording.
5. build-windows.ps1 for self-contained win-x64/win-arm64 with icon, dist output and,
   only if needed, an optional installer.
6. Relevant headless scenes (trading, conexiones, panel, footprint, mercado, english),
   all regression tests, and a concrete manual simulation checklist.

Hide unsupported features such as macOS closed-lid behavior and explain alternatives.
Show actual errors and unresolved gaps; never claim an untested platform works.
```

## Afterward

- Share a staged PR if desired, with separate evidence for both platforms.
- Keep cross-platform tests green; one platform's change must not silently break another.
- Linux follows similar seams: Secret Service/libsecret, an appropriate sleep inhibitor such as systemd-inhibit, and xdg-open. It also requires implementation and verification.
