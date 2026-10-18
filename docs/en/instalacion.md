[← Documentation index](README.md)

# Installation

Trading Hub is built locally from the repository. The build script assembles `Trading Hub.app`, signs it and optionally copies it to Applications. The first build takes a few minutes while NuGet dependencies download.

For this fork's completed English work, use branch **`feat/english-localization`**, not the Spanish `main` branch:

```bash
git clone --branch feat/english-localization https://github.com/mehmetcanbudak/trading-hub.git
cd trading-hub
```

The initial community translation came from [TradeDock's English branch](https://github.com/indigiti/tradedock/tree/translation/english-documentation-ui); that draft alone is not this completed build.

## Requirements

| Requirement | Details |
|---|---|
| Mac | Apple Silicon, M1 or later; this build targets `arm64`. |
| macOS | 12 or later. |
| .NET 10 SDK | Build-time only; installs into `~/.dotnet` without an administrator password. |
| Internet | Required for the first NuGet restore. |
| Claude Code | Optional; only AI-assisted features need it. See [below](#claude-code-optional). |

The installed application is self-contained: running it does not require a separate .NET installation.

```bash
uname -m                 # Must report arm64
sw_vers -productVersion  # Must be 12 or later
```

An `x86_64` result means an Intel Mac, which this build does not support.

## Option 1: Ask Claude Code

1. Install Claude Code and sign in to your Claude account.
2. Open Terminal in the repository and run `claude`.
3. Ask: “Install Trading Hub by following CLAUDE.md.”

The repository's instructions tell Claude to check compatibility, install SDK 10 into `~/.dotnet` if missing, run `./build-app.sh --install`, check CLI discovery and open the app. Installation requires no personal details, brokerage passwords or API keys. Before replacing an unrelated application also named Trading Hub, it must ask you.

## Option 2: Install manually

1. Check for SDK 10:
   ```bash
   ~/.dotnet/dotnet --list-sdks
   ```
   If a line starts with `10.`, continue at step 3.
2. Install if needed:
   ```bash
   curl -sSL https://dot.net/v1/dotnet-install.sh -o /tmp/dotnet-install.sh
   bash /tmp/dotnet-install.sh --channel 10.0
   ```
   Run `~/.dotnet/dotnet --list-sdks` again to verify.
3. Build and install from the repository root:
   ```bash
   ./build-app.sh --install
   ```
4. Open from Applications, or run:
   ```bash
   open "/Applications/Trading Hub.app"
   ```

The build script temporarily adds `~/.dotnet` to its own PATH; no shell configuration change is required.

### What `build-app.sh` does

- Publishes a self-contained Release build for `osx-arm64`.
- Assembles the bundle in a temporary directory, outside Desktop: iCloud-added extended attributes can break code signing.
- Signs the app and places a copy in `dist/Trading Hub.app`.
- With `--install`, removes the existing `/Applications/Trading Hub.app`, copies the new bundle and verifies its signature. Back up an existing bundle first if you need a rollback.

Without `--install`, Applications is untouched.

## Ad-hoc signing

Without a developer certificate the script signs ad hoc. The locally built app normally opens on its build machine without an additional Gatekeeper prompt. macOS privacy permissions are separate: a changed ad-hoc signature can request **Documents-folder access** again after an update. Review that prompt and allow it if you want to use the standard strategy/indicator folders; startup may wait for your response.

- On another Mac, use **Right-click → Open** for the first launch if required.
- To use a certificate, create the git-ignored `build.local.env` in the repository:
  ```bash
  SIGN_IDENTITY="certificate name"
  BUNDLE_ID="com.your-domain.tradinghub"
  ```
  The identity must match `security find-identity -v -p codesigning`. If no matching identity is found, the script falls back to ad-hoc signing. A stable certificate helps macOS remember permissions across updates.

## Claude Code (optional)

Trading Hub runs your installed Claude Code CLI with your existing session, not a separate Claude API key. Usage counts against your normal Claude allowance. It is used in:

- **Backtesting → Claude**: the strategy-writing and debugging chat.
- **Live → Portfolios**: request Claude analysis and approve proposed sizing changes.

Charts, trading, data management, backtests and live strategies work without Claude Code.

The app searches these locations and chooses the newest CLI it finds:

- `~/.local/bin/claude`, `~/.claude/local/claude`, `/opt/homebrew/bin/claude`, `/usr/local/bin/claude`, `~/.npm-global/bin/claude`;
- directories in PATH;
- supported Claude desktop application locations.

If your CLI is elsewhere:

```bash
mkdir -p ~/.local/bin && ln -sf "$(command -v claude)" ~/.local/bin/claude
```

`TRADINGHUB_CLAUDE_CLI` selects an explicit executable. Sign in from Terminal using `claude` and `/login`. If the Backtesting chat encounters a login requirement, it offers **Sign in to Claude Code**.

## Where your data lives

| Location | Contents |
|---|---|
| `~/Library/Application Support/Trading Hub` | Settings, nonsecret connections, workspace, market history, drawings, templates, account records, backtests and live state. |
| `~/Documents/Trading Hub/Estrategias` | C# strategies; `Reportes` inside contains Markdown reports. |
| `~/Documents/Trading Hub/Indicadores` | Custom C# indicators. |
| macOS Keychain | Connection passwords, secrets and tokens under service name `Trading Hub`. |

Spanish folder names are compatibility identifiers, not untranslated UI. Do not rename them during an upgrade.

| File or folder in Application Support | Contents |
|---|---|
| `settings.json` | General settings, hotkeys, DOM and bracket templates. |
| `connections.json` | Connection metadata without passwords or secrets. |
| `workspace.json` | Tabs, charts, layouts and open panels. |
| `Ticks/` | Trade-by-trade files by connection, instrument and day. |
| `Klines/` | Downloaded bars by month, for fast long-history loading. |
| `Drawings/` | Per-instrument drawings. |
| `ChartTemplates/`, `IndicatorTemplates/`, `FootprintTemplates/` | Saved chart, indicator and footprint templates. |
| `Accounts/` | Account fills used by Accounts history. |
| `Backtests/`, `Live/` | Saved backtests and live-strategy state. |
| `crash.log` | Unexpected errors for troubleshooting. |

The first launch creates the Documents folders, including `EjemploDeltaPorcentual.cs`. Example strategies are created when the strategy folder is empty. Existing user files are not overwritten merely to translate them.

Open folders using **File → Open data folder**, **Code → Open indicators folder**, **Code → Open strategies folder**, or the **Open** buttons under **Settings → Data**.

## Updating

1. Fully quit with **⌘Q** or **Quit Trading Hub** from its menu-bar icon. Closing the window normally leaves the app and strategies running.
2. Update the selected English branch:
   ```bash
   git pull --ff-only
   ```
   If using ZIP downloads, replace the source folder with the new English-branch archive, preserving `build.local.env` if present. Do not discard uncommitted changes.
3. Rebuild:
   ```bash
   ./build-app.sh --install
   ```

You can instead ask Claude Code to update Trading Hub. Data, connections, credentials and strategies are outside the repository and must remain intact.

## Uninstalling

These are optional destructive instructions, **not part of installation or translation**. Back up anything you want to retain.

1. Fully quit Trading Hub.
2. Optionally remove connection secrets through **Connections → Delete**. Built-in profiles may not be deletable; remove their secrets with Keychain Access.
3. Remove `/Applications/Trading Hub.app` using Finder, or:
   ```bash
   rm -rf "/Applications/Trading Hub.app"
   ```
4. In **Keychain Access**, search for `Trading Hub` and delete the relevant entries. The equivalent Terminal loop removes all matching generic passwords:
   ```bash
   while security delete-generic-password -s "Trading Hub" >/dev/null 2>&1; do :; done
   ```
5. **Only to erase history, accounts and settings permanently**, remove the data folder:
   ```bash
   rm -rf ~/Library/Application\ Support/Trading\ Hub
   ```
6. Remove `~/Documents/Trading Hub` only if you also intend to delete your strategies, indicators and reports.
7. Optionally remove the repository (including `dist/`), `~/.dotnet` if nothing else uses it, and a Claude symlink created solely for Trading Hub.

## Troubleshooting

- **`dotnet` not found:** the script uses `~/.dotnet` automatically. Confirm that `~/.dotnet/dotnet --list-sdks` lists SDK 10.
- **Build failure:** read the first actual error. Check internet access for package restore and SDK version 10.
- **Process running but no window after an update:** first check for the macOS Documents-folder permission prompt. It can hold filesystem-watcher initialization until you respond, without creating a crash log.
- **Crash or unresponsive app:** inspect `crash.log` in the data directory. Never use a real trading account as a test fixture.
