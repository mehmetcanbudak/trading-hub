# TradeDock → our English branch: parity review

Reviewed 2026-10-22; public tips rechecked at approximately 21:18 UTC.

## Follow-up: documentation restored — 2026-10-22

The two documentation findings below are addressed in the local working tree: [indicator validation](../en/indicadores.md#validation-checklist), [partial-fill/cancellation testing](../en/estrategias.md#automatic-stop-target-and-trailing), and [live incident checks](../en/en-vivo.md#incident-checks). No application or trading logic changed. This follow-up is not yet committed or published; the review below remains a record of the pinned commits, not the updated working tree.

The follow-up's English source/documentation audit passes: 22 Markdown files, 177 local links/anchors, 16 guide section checks and zero source-audit failures. Existing guide code blocks are unchanged; full application/UI/broker tests were not rerun for this documentation-only fix.

## Original conclusion at the reviewed commits

**No TradeDock commits or translated application text are missing from our branch.** Our branch contains all 28 community commits and ten additional commits. However, **two small documentation-checklist gaps** remain after replacing its condensed guides with fuller guides. These are advice/documentation omissions, not evidence that indicator calculations, order handling or incident controls were removed.

Do not merge or cherry-pick the community branch again. For strict documentation parity, restore the short validation and incident guidance identified below; no application change is indicated by this review.

## Fixed revisions and scope

| Role | Repository / branch | Commit |
|---|---|---|
| Original application baseline | ralphtradesz/trading-hub, main | `9ee74a4c527ac43120423acd41e006ec9d7877ad` |
| Community English work | [indigiti/tradedock, translation/english-documentation-ui](https://github.com/indigiti/tradedock/tree/65ae0229bc0b36ba44156cfaaa530607c382ec25) | `65ae0229bc0b36ba44156cfaaa530607c382ec25` |
| Our published English work | [mehmetcanbudak/trading-hub, feat/english-localization](https://github.com/mehmetcanbudak/trading-hub/tree/5f7c19e7528f47582f34f14d2c06e71d388b9858) | `5f7c19e7528f47582f34f14d2c06e71d388b9858` |

The merge-base is the community tip. `git rev-list --left-right --count COMMUNITY...OURS` returns **0 / 10**: zero community-only commits, ten ours-only commits. [Pinned changes since the community tip](https://github.com/mehmetcanbudak/trading-hub/compare/65ae0229bc0b36ba44156cfaaa530607c382ec25...5f7c19e7528f47582f34f14d2c06e71d388b9858).

The parity surface is the **47 files changed by the community relative to the original baseline**, together with our replacement localization/validation mechanisms. All 47 exist in ours; none was deleted. Five are byte-identical: `CLAUDE.es.md`, `README.es.md`, `docs/README.es.md`, `tools/english-ui-glossary.json`, and `src/TradingHub.App/Windows/ChartWindow.cs`. The other 42 were compared by content/purpose, not declared equivalent merely because of ancestry.

The specification is the community [draft PR #1](https://github.com/indigiti/tradedock/pull/1) and its [pinned goals, risks and regression requirements](https://github.com/indigiti/tradedock/blob/65ae0229bc0b36ba44156cfaaa530607c382ec25/docs/en/TRANSLATION_STATUS.md). Standards sources are our [CLAUDE.md](https://github.com/mehmetcanbudak/trading-hub/blob/5f7c19e7528f47582f34f14d2c06e71d388b9858/CLAUDE.md), [development rules](https://github.com/mehmetcanbudak/trading-hub/blob/5f7c19e7528f47582f34f14d2c06e71d388b9858/docs/en/desarrollo.md#rules), and [localization dashboard](https://github.com/mehmetcanbudak/trading-hub/blob/5f7c19e7528f47582f34f14d2c06e71d388b9858/docs/en/TRANSLATION_STATUS.md). No separate CONTRIBUTING/CODING_STANDARDS file was found in the scoped tracked-file search. Both review axes were performed directly, **not by independent reviewers**.

Untracked research notes and unrelated `plans/` were excluded from the committed parity comparison. This is not an OI-feature review or a comprehensive audit of every trading algorithm.

## Standards

**No documented-standard violation identified within this parity scope.**

- Attribution, original-author links, the source-available/MIT + Commons Clause explanation and retained Spanish entry documents remain. Our README explicitly credits TradeDock rather than treating its 28 commits as independently authored work. [Community README](https://github.com/indigiti/tradedock/blob/65ae0229bc0b36ba44156cfaaa530607c382ec25/README.md), [our README](https://github.com/mehmetcanbudak/trading-hub/blob/5f7c19e7528f47582f34f14d2c06e71d388b9858/README.md).
- English presentation is separated from stable account/storage/catalog identities. The one deliberately restored catalog comparison is explained below, with a regression check rather than a blanket translation of an internal identifier.
- Validation documents distinguish reviewed built-in English coverage from user/provider text, real-money execution, paid-model output and complete Windows desktop support. Our review did not treat a green build as a language or trading-safety guarantee. [Our evidence and limitations](https://github.com/mehmetcanbudak/trading-hub/blob/5f7c19e7528f47582f34f14d2c06e71d388b9858/docs/en/TRANSLATION_STATUS.md).

No additional code-smell finding is asserted by this focused parity review.

## Spec

### Finding 1 — Low: explicit validation advice was not fully retained

The community indicator guide ends with a short **Validation checklist**: compare against known outputs on fixed history, check live forming candles and multiple timeframes, and guard against look-ahead/state leakage. Its strategy guide also explicitly says **“Always test partial fills and cancellation paths.”**

Our replacement guides provide much more API detail and explain lifecycle, warmup, protective orders and unsandboxed code, but do not retain that concrete indicator-validation checklist or the explicit partial-fill/cancellation testing instruction. This is a loss of useful English guidance, not a demonstrated execution regression.

Sources:

- [Community indicator checklist, lines 33–35](https://github.com/indigiti/tradedock/blob/65ae0229bc0b36ba44156cfaaa530607c382ec25/docs/en/indicadores.md#L33-L35).
- [Community strategy protection/testing advice, lines 19–21](https://github.com/indigiti/tradedock/blob/65ae0229bc0b36ba44156cfaaa530607c382ec25/docs/en/estrategias.md#L19-L21).
- [Our full indicator guide](https://github.com/mehmetcanbudak/trading-hub/blob/5f7c19e7528f47582f34f14d2c06e71d388b9858/docs/en/indicadores.md) and [strategy protection section](https://github.com/mehmetcanbudak/trading-hub/blob/5f7c19e7528f47582f34f14d2c06e71d388b9858/docs/en/estrategias.md#automatic-stop-target-and-trailing).

**Smallest follow-up:** restore a short indicator validation list and one strategy-testing sentence. Do not add an abstraction, change order logic or claim those checks have been performed merely by documenting them.

### Finding 2 — Low: the live-incident checklist was not retained as a checklist

TradeDock explicitly lists stale data, disconnects, rejected orders, missing execution acknowledgments and PnL divergence, then advises pausing/stopping as appropriate and reconciling with the broker rather than relying only on local charts.

Our live guide still explains pause/stop behavior, broker reconciliation at restart, approximate live-ledger PnL and checking positions after Stop all. It therefore retains important related warnings, but lacks the original concise incident checklist covering those triggers together.

Sources:

- [Community incident checklist, lines 43–45](https://github.com/indigiti/tradedock/blob/65ae0229bc0b36ba44156cfaaa530607c382ec25/docs/en/en-vivo.md#L43-L45).
- [Our risk/pause and emergency-action guidance](https://github.com/mehmetcanbudak/trading-hub/blob/5f7c19e7528f47582f34f14d2c06e71d388b9858/docs/en/en-vivo.md#risk-limits-and-pause), [live accounting limit](https://github.com/mehmetcanbudak/trading-hub/blob/5f7c19e7528f47582f34f14d2c06e71d388b9858/docs/en/en-vivo.md#trades), and [connection-status explanations](https://github.com/mehmetcanbudak/trading-hub/blob/5f7c19e7528f47582f34f14d2c06e71d388b9858/docs/en/primeros-pasos.md#right-side-of-the-title-bar).

**Smallest follow-up:** restore a short “Incident checks” paragraph in `docs/en/en-vivo.md`. This requires no application or trading-state change.

### Application text: retained, with one intentional compatibility correction

A temporary Roslyn parser aligned original/community tokens in all **20 community-translated C# files**. It found **775 changed string/interpolated-text occurrences**. The community token changes inspected by the helper were text changes, not changed C# expressions or numeric values.

Our source retains **774 of those occurrences verbatim in the same enclosing members**, with sufficient per-member occurrence counts. The sole exception is `IndicatorsWindow.FillAvailable`: TradeDock changed the comparison `i.Source != "Técnicos"` to `i.Source != "Technical"`. The catalog still uses the stable source identity `"Técnicos"`; our comparison restores that identity so built-in technical studies are not incorrectly labeled as extra plugins.

This is **not a lost English label**. It is an internal comparison, while known legacy metadata has a separate English display mapping. A UI regression explicitly scans a fixture with the legacy source and checks that the technical indicator has no erroneous **extra** badge.

- [Community comparison](https://github.com/indigiti/tradedock/blob/65ae0229bc0b36ba44156cfaaa530607c382ec25/src/TradingHub.App/Windows/IndicatorsWindow.cs#L159).
- [Our comparison](https://github.com/mehmetcanbudak/trading-hub/blob/5f7c19e7528f47582f34f14d2c06e71d388b9858/src/TradingHub.App/Windows/IndicatorsWindow.cs#L159), [catalog source identity](https://github.com/mehmetcanbudak/trading-hub/blob/5f7c19e7528f47582f34f14d2c06e71d388b9858/src/TradingHub.App/Services/AppServices.cs#L365), [English metadata mapping](https://github.com/mehmetcanbudak/trading-hub/blob/5f7c19e7528f47582f34f14d2c06e71d388b9858/src/TradingHub.Core/Common/EnglishText.cs#L55), and [UI regression](https://github.com/mehmetcanbudak/trading-hub/blob/5f7c19e7528f47582f34f14d2c06e71d388b9858/tools/TradingHub.UiSnapshot/EnglishScene.cs#L218-L224).

This is static text/context parity evidence, not proof that every control has been exercised under every runtime condition. The full **604-entry glossary is byte-identical**, including entries not used as changed literals in these files: [community](https://github.com/indigiti/tradedock/blob/65ae0229bc0b36ba44156cfaaa530607c382ec25/tools/english-ui-glossary.json), [ours](https://github.com/mehmetcanbudak/trading-hub/blob/5f7c19e7528f47582f34f14d2c06e71d388b9858/tools/english-ui-glossary.json).

### Documentation and tooling coverage

All **16 topic guides plus the English index** remain. Each topic guide was read against its community counterpart; all are longer, but length alone was not treated as coverage evidence.

| Guide | Parity result |
|---|---|
| Settings | Preferences, real-order confirmations, live resumption, power, shortcuts and storage retained; defaults and compatibility details added. |
| Claude | CLI discovery/login, permissions, laboratory, reports and data-sharing retained; tool/API details and provider-output limitations added. |
| Backtesting | Data, costs/fills, results, Monte Carlo, optimization, walk-forward, overfitting and portfolios retained; formulas and API/UI limits added. |
| Connections | Provider capabilities, credential handling and explicit real-order enablement retained; entitlement caveats and detailed setup added. |
| Data | History, replay, deletion, import and cost-confirmed Databento retained; source/storage/merge details added. |
| Development | Build/tests, scenes, isolation, providers and studies/tools retained; current audit and platform evidence documented. |
| Live | Launch, sizing, limits, portfolios, risk proposals and close/resume behavior retained; see incident-checklist finding. |
| Strategies | Lifecycle, parameters, orders, indicators, drawings, examples and Claude retained/expanded; see testing-advice finding. |
| Charts | History/styles, footprint, navigation/layouts, indicators/templates, DOM/tape, replay and strategies retained/expanded. |
| Indicators | Included/custom studies and ATAS import retained; example, SDK and DLL details added; see validation-checklist finding. |
| Installation | Prerequisites, manual/Claude install, signing, local files, updates/removal and troubleshooting retained; correct fork/English branch selected explicitly. |
| Install with Claude | Setup, verified installation, preservation and updates retained; explicit English-branch instructions added. |
| Trading | Account selection, Chart Trader, DOM/chart orders, protection, cross trading, hotkeys/history and real-money safeguards retained/expanded. |
| Windows | Platform dependency inventory, staged port and validation guidance retained; shared CI is distinguished from unverified native desktop integration. |
| First steps | Simulation, navigation, charts, practice controls, menu-bar behavior and next steps retained/expanded. |
| Privacy | Local storage, network/Claude disclosure, Keychain, real-money controls, test isolation and deletion retained/expanded. |

Both guide collections are available at the [pinned community directory](https://github.com/indigiti/tradedock/tree/65ae0229bc0b36ba44156cfaaa530607c382ec25/docs/en) and [pinned ours directory](https://github.com/mehmetcanbudak/trading-hub/tree/5f7c19e7528f47582f34f14d2c06e71d388b9858/docs/en). Root README, documentation entry points and installer instructions were also compared. Our README uses English screenshots rather than the original Spanish screenshot paths.

The community workflow's .NET build, regressions, documentation/source validation and five smoke scenes are not missing. Our workflow retains those responsibilities, adds Windows shared-runtime validation and tracker tests, and runs **16 isolated macOS scenarios**, including all seven navigation pages and the English assertion scenario. [Community workflow](https://github.com/indigiti/tradedock/blob/65ae0229bc0b36ba44156cfaaa530607c382ec25/.github/workflows/english-translation-validation.yml), [our workflow](https://github.com/mehmetcanbudak/trading-hub/blob/5f7c19e7528f47582f34f14d2c06e71d388b9858/.github/workflows/english-translation-validation.yml).

The old line-by-line approved-literal checker is intentionally replaced, not accidentally discarded: the current checker validates working-tree documentation, Roslyn text candidates, explicit compatibility exceptions and reviewed non-text structure. It does not require unsafe identity translations to satisfy the old exact-substitution rule. [Community checker](https://github.com/indigiti/tradedock/blob/65ae0229bc0b36ba44156cfaaa530607c382ec25/tools/check_english_translation.py), [our checker](https://github.com/mehmetcanbudak/trading-hub/blob/5f7c19e7528f47582f34f14d2c06e71d388b9858/tools/check_english_translation.py), [our Roslyn audit](https://github.com/mehmetcanbudak/trading-hub/blob/5f7c19e7528f47582f34f14d2c06e71d388b9858/tools/TradingHub.UiSnapshot/EnglishLocalizationAudit.cs).

The community dashboard's generated-SDK-string blocker is addressed through [English defaults](https://github.com/mehmetcanbudak/trading-hub/blob/5f7c19e7528f47582f34f14d2c06e71d388b9858/src/TradingHub.Sdk/Localization/Strings.English.cs) and [override precedence](https://github.com/mehmetcanbudak/trading-hub/blob/5f7c19e7528f47582f34f14d2c06e71d388b9858/src/TradingHub.Sdk/Localization/Strings.cs), rather than editing generated `Strings.g.cs`. [Compatibility tests](https://github.com/mehmetcanbudak/trading-hub/blob/5f7c19e7528f47582f34f14d2c06e71d388b9858/tests/TradingHub.Tests/EnglishLocalizationTests.cs) cover the defaults, overrides and legacy mappings.

## Checks actually performed

- Rechecked both public branch SHAs, community PR state and exact-head CI metadata using GitHub's API. TradeDock PR #1 remains open/draft; its own dashboard remains blocked. Its exact-tip [CI run 39140931062](https://github.com/indigiti/tradedock/actions/runs/39140931062) succeeded; this does not make its incomplete localization a release approval.
- Confirmed our exact-tip [CI run 39239369636](https://github.com/mehmetcanbudak/trading-hub/actions/runs/39239369636) succeeded for both `macos-build-and-tests` and `windows-shared-runtime`. These are existing hosted results, not newly run full suites in this review.
- Exported and compared all 47 community-changed files at original/community/our revisions; checked ancestry, file existence and blob equality.
- Ran the temporary Roslyn literal/context/count comparison successfully across the 20 C# files. Its first build failed because the temporary project recursively included exported source files; limiting compilation to the parser's own `Program.cs` resolved it. This was an analysis-helper failure, not an application regression. No community application was executed.
- Fresh local `python3 tools/check_english_translation.py`: **PASS** — 22 Markdown files, 173 local links/anchors, 16 guide checks; 184 source files, 11,531 text tokens, 129 documented compatibility exceptions, **zero audit failures**. These guide checks compare the historical Spanish guide coverage; they do not detect every advisory paragraph unique to the condensed community guides, hence the manual findings above.
- Fresh local `dotnet test tests/TradingHub.Tests -c Release --no-restore --filter FullyQualifiedName~EnglishLocalizationTests`: **29 passed, zero failed/skipped**.
- Read all 16 community/our guide pairs and verified the checklist observations with scoped searches of `docs/en/`, `CLAUDE.md` and `README.md`. The absence claim concerns explicit checklist/advice text in those documents, not an absence of underlying test coverage or controls.
- `git diff --check` passed for tracked changes. No source, workflow, application installation, saved state or branch setting was changed for this review. This report is local/uncommitted.

Temporary evidence: `/tmp/tradinghub-english-parity.uvyUfA/` contains exported files, `files.json`, `literal-parity.json`, parser source, current API responses and local check logs. It is disposable, not a published artifact.

No fresh full regression/UI run, real brokerage execution, paid Claude call, privileged power test or complete Windows desktop test was performed. OI/zoom integration and unrelated worktrees were not reviewed or changed.

**Review totals:** Standards: **0 findings** within scope. Spec: **2 low-priority documentation findings**; no missing community commits or translated application text identified. English parity is not certification of trading safety.
