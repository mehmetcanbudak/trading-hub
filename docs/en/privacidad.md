[← Documentation index](README.md)

# Privacy and security

Trading Hub runs locally: app, market data, scripts, backtests and connection credentials. It has no Trading Hub account/backend and the reviewed source does not implement usage telemetry or automatic error uploads. Configured providers and optional Claude Code still make network requests.

## Local storage

| Location | Contents |
|---|---|
| Application Support/Trading Hub | Settings, nonsecret connection metadata, workspace, ticks/bars, drawings, templates, account records, backtests, live runners and portfolios. |
| Documents/Trading Hub | Strategies, Estrategias/Reportes reports and custom indicators. |
| macOS Keychain | Passwords, secrets, tokens and Databento keys under service Trading Hub. |

See [Installation](instalacion.md#where-your-data-lives) for exact paths. AI-related local files include Chats, the compatibility-named `Capturas del laboratorio` screenshot folder (most recent 60), and claude-lab-mcp.json. Unexpected errors go to local crash.log; they are not automatically submitted.

## Credentials

- Production secrets use macOS `security` and Keychain, not normal JSON files.
- connections.json contains name, username, server, public API identifiers and options. Public IDs/account names can still be sensitive metadata; avoid sharing them unnecessarily.
- Deleting a connection also deletes its Keychain secrets.
- Laboratory tools do not expose stored credentials. Do not put credentials into source, prompts or reports, where file-reading tools could access them.

## Network access

| Feature | When | Destination |
|---|---|---|
| Market data | Opening/using an instrument on an enabled connection. | Configured Binance, Bybit, Rithmic, Tradovate, OANDA, Alpaca or cTrader provider. |
| History | User download or laboratory download_data request. | Public archives or configured provider APIs. |
| Long-history/context bars | Loading older charts or context without stored ticks. | Relevant market provider; crypto can use public Binance/Bybit bars without account login. |
| Orders | Manual or strategy submission to a broker account. | That broker; simulated orders are internal. |
| Databento | Connection tests, availability/cost estimates and confirmed paid downloads. | Databento using the stored key. |
| Claude Code | Assistant, laboratory or risk-agent invocation. | Anthropic through the user's CLI/account. |
| Interactive Brokers probe | Test connection only. | TWS/IB Gateway, local by default. |
| Links / cTrader authorization | Clicking external links or authorizing access. | System browser and the selected provider/author site. |

There is no built-in updater or automatic crash-report submission in the reviewed source. Building requires NuGet/network access independently of normal app operation. Providers and CLI software have their own policies; this is not a packet-level audit of third-party components.

## Claude

Prompts, read strategy/indicator files, metrics/trades/data summaries, chart images and requested live exposure may be sent to Anthropic. The assistant's working directory is the strategy folder, with file tools limited to strategy/indicator/screenshot roots and without terminal/web or unrelated MCP tools. Risk reviews have no tools, only supplied portfolio figures.

Data handling depends on your Claude account and Anthropic policies. See [Claude](asistente-claude.md). Local storage does **not** mean AI requests stay local. Historical conversations/reports are retained in their original language/content.

## Real money

- Simulation is the default; real destinations require per-connection permission.
- Existing manual-order confirmation rules remain; manually launching/resuming an automatic real strategy asks once. Automatic real resumption is disabled by default because it does not show that dialog.
- Live sizing, daily-loss/drawdown limits and Stop all remain available. Automatic risk-agent changes are not allowed for portfolios containing real-account members.

See [Connections](conexiones.md#real-and-test-accounts), [Trading safety](operar.md#safety) and [Live](en-vivo.md). Limits/actions cannot guarantee execution during outages. Imported C# and plugin DLLs are unsandboxed and should be trusted/reviewed before running.

## macOS permissions

- **Administrator password:** only for explicitly enabled closed-lid operation, using a privileged power helper. It restores normal sleep when the app exits; see [Settings](ajustes.md#keep-awake).
- **Control Terminal:** Claude sign-in opens Terminal with `claude /login`.
- Locally built apps use ad-hoc signing unless a certificate is configured; see [Signing](instalacion.md#ad-hoc-signing).

Localization tests do not enable privileged closed-lid operation or request broker credentials.

## Development isolation

Tests/scenes use temporary data, strategy and indicator directories plus fake JSON credentials. Default screenshot tooling copies/sanitizes configuration, disables real orders and points Rithmic at a local fake server. Explicit fresh fixtures are preferred to copying user history. See [Development](desarrollo.md).

TRADINGHUB_KEYCHAIN_FILE is a **plaintext testing override**, not encrypted production credential storage. Do not place real secrets there or include it in diagnostics.

## Delete everything

This is optional irreversible removal, not an update step:

1. Fully quit using ⌘Q / Quit Trading Hub.
2. Back up anything required; delete Application Support/Trading Hub, and Documents/Trading Hub only if you also intend to erase scripts/reports.
3. In Keychain Access remove the relevant Trading Hub entries.
4. Remove the Applications bundle; see [Uninstalling](instalacion.md#uninstalling).

The English upgrade itself does not delete data, rename stable folders or translate personal records in place.
