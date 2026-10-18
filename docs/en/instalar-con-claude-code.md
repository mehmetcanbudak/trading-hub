[← Documentation index](README.md)

# Install Trading Hub with Claude Code

Your own Claude Code can build the app on your Mac, install it in Applications and connect optional AI features to your existing Claude session. Programming experience is not required.

## Before starting

- Apple Silicon Mac (M1 or later), macOS 12 or later.
- [Claude Code](https://claude.com/claude-code) installed and signed in with an account that permits CLI use.

## Steps

1. Obtain **this English branch**, not the Spanish main branch:
   ```bash
   git clone --branch feat/english-localization https://github.com/mehmetcanbudak/trading-hub.git
   ```
   Alternatively download the ZIP while that branch is selected on GitHub.
2. Open Terminal in the folder:
   ```bash
   cd trading-hub
   claude
   ```
3. Paste:
   ```text
   Install Trading Hub on my Mac following this folder's CLAUDE.md.
   Check Apple Silicon/macOS compatibility, install .NET SDK 10 if needed in ~/.dotnet without sudo,
   build and install using ./build-app.sh --install, verify Claude Code discovery
   (create a safe ~/.local/bin link only if needed), open the app and explain the first steps.
   Ask before replacing an unrelated application named Trading Hub in Applications.
   Preserve my data and credentials and keep real-money trading disabled.
   ```

## What happens

- Claude checks architecture and macOS version.
- If necessary, SDK 10 downloads from Microsoft into ~/.dotnet without changing system directories or requesting an administrator password.
- The first build restores dependencies, then installs `/Applications/Trading Hub.app`.
- The app discovers your CLI/session. Backtesting chat, laboratory and live risk analysis use your account, not a separate API key. Actual model availability still depends on the CLI/account.
- The app opens using simulation. Real trading remains disabled until you explicitly configure/permit it.

No brokerage credentials or personal information are required to install. A donation is optional, never an installation condition.

## Update

Fully quit with ⌘Q, update the English branch using `git pull --ff-only` (or obtain its new ZIP), and ask Claude to update Trading Hub. Preserve uncommitted changes and build.local.env. Data, connections and scripts live outside the checkout and must not be reset.

## Troubleshooting

Ask Claude to show the actual error and resolve it, rather than claiming success.

- **CLI not found:** locate it and create ~/.local/bin/claude only if appropriate; do not overwrite an unrelated existing file.
- **macOS cannot verify the app:** a local build is ad-hoc signed. Rebuild on your Mac if the app came from elsewhere, or follow the signing guidance.
- **Build failure:** confirm `~/.dotnet/dotnet --list-sdks` includes 10.x and package restoration has internet access.

See [Installation](instalacion.md) for signing, storage, updates and optional removal.
