# Trading Hub — English localization validation

**Reviewed:** 2026-10-19
**Branch:** `feat/english-localization`
**Implementation:** reviewed built-in English coverage plus a user-authorized five-sample source migration, installed and visually verified in the native app. Expanded local checks pass; hosted Windows follow-up is being verified. This is not a guarantee about arbitrary user/provider content or trading safety.

## Scope and provenance

This fork reuses the 28-commit community translation at `65ae0229bc0b36ba44156cfaaa530607c382ec25` from [indigiti/TradeDock](https://github.com/indigiti/tradedock/tree/translation/english-documentation-ui). Its original [draft PR](https://github.com/indigiti/tradedock/pull/1) remains a separate upstream review, not a release approval for this fork. Original application baseline: `9ee74a4c527ac43120423acd41e006ec9d7877ad`.

Completed coverage includes navigation, controls/tooltips, secondary windows, safety dialogs, chart/DOM/footprint labels, native indicator/SDK settings, compiler diagnostics, live/backtest status/errors, reports, built-in examples and English Claude/risk-agent instructions. All 16 detailed feature/setup/porting guides now cover their original sections, API examples and safety rules rather than being condensed summaries.

## Verified evidence

| Check | Result | Evidence |
|---|---|---|
| Release application/solution build | Passed | .NET SDK 10.0.402, Apple Silicon macOS; two pre-existing warnings noted below. |
| Unit/regression suite | **221 passed** | Original 192 cases plus 29 localization/compatibility cases; complete Release runs passed after repairing the fixture race. |
| Fake Tradovate request-log race | Fixed in test fixture only | ConcurrentQueue replaces a concurrently read/written List; previously failing test passed **20/20** repeated runs. |
| Source localization audit | Passed | **184 files / 11,531 text tokens**, zero unresolved candidates; **129** explicit compatibility/language-code exceptions. |
| Non-text C# review guard | Passed | Baseline token fingerprints plus 36 reviewed display/compatibility/sample files; raw strategy/indicator template C# is recursively fingerprinted. |
| English guides and links | Passed | **21 Markdown files**, **160 local links/anchors**, **16 guide section-coverage checks**. Section counts supplement, not replace, semantic review. |
| English UI scenario | **120 checks, zero failures** | Seven main pages; nine secondary windows; chart-setting tabs; technical-source badge; actual safety dialogs; reports and compiler output. |
| Safety cancellation | Passed | Cancel real-account launch, paid Databento download, data deletion and emergency stop; no runner launched, paid data requested or history deleted. |
| Built-in/example compilation | Passed | Five strategy templates, the initial custom indicator, the New indicator editor template and all three complete guide examples compile with English code identifiers. The New indicator regression was observed failing before its fix. |
| Existing sample migration | Passed locally | Five original files backed up and hash-verified outside watched folders. Explicitly authorized English filenames/classes/parameters/private/local names; no saved references existed. All non-text tokens match after the identifier map; four old/new strategies retain identical trades/equity with default and customized parameters (143–589 trades per replay). Indicator threshold default and series ID unchanged. |
| Claude bridge, without paid usage | Passed | Fake CLI exercises actual bridge success/session parsing, authentication recognition and empty-response fallback. Both new-code prompts request English identifiers. No real model call or session was used. |
| Closed-lid helper, without power changes | Passed | Fake RunAdminScript captures the English administrator explanation and verifies assertion/flag state and cleanup. No privileged helper or pmset change executed. |
| Windows | Build and 221 shared tests passed; audit follow-up pending | Hosted run 39170947948 confirms all 221 tests pass after fixing Unix-only fixture assumptions: a leaked lock and concurrent-file damage setup. Production locks and recovery assertions are unchanged. Windows CRLF checkout exposed fingerprint-only differences in raw strings; normalization has a red/green regression and preserves numeric-mutation detection. Updated audit CI is being rerun. Desktop/system integration remains outside this evidence. |
| Report generation/export | Passed | Backtest, optimization, walk-forward and portfolio prose; parameter display names; fallback verdict metadata; self-contained English HTML with embedded charts. |
| Test isolation guards | Passed | Direct production-data root and a symlink alias both refused before application startup. Sleep guard disabled; fake Keychain/providers used. |
| Installer syntax | Passed | `zsh -n build-app.sh`; only comments/messages translated, packaging behavior unchanged. |
| Broader scenarios / narrow-window visual review | Passed | Final **16-scenario** run exited 0 with **65 screenshots**; fresh **1100×760** run exited 0 with 120 English checks. Main pages, secondary windows and safety dialogs visually inspected. |
| Negative audit gates | Passed | Unaccented Spanish, accented Spanish and new unreviewed English source were rejected. Temporary probes removed. |
| Hosted GitHub workflow | Passed | [Workflow](../../.github/workflows/english-translation-validation.yml), [run 39163792434](https://github.com/mehmetcanbudak/trading-hub/actions/runs/39163792434) validates implementation commit `2dd4ed9`: Release build, 221 tests, source/docs checks and all 16 UI scenarios, including 93 English checks. Isolated screenshots and HTML reports uploaded as `tradinghub-english-ui`; this is this fork's previous implementation evidence, not older TradeDock CI. The sample follow-up will run both macOS and shared Windows jobs on this fork. |
| Installed English app | Passed | Updated bundle installed and strict signature verification passed. Native Backtesting shows compiled DeltaAbsorption.cs with English code, and Custom indicator editor shows compiled DeltaPercentageExample.cs with English code. All five files migrated with permission; no saved references needed rewriting. Previous bundle/source/state rollback remains outside watched folders, and real-order permissions remain disabled. |

## Compatibility is not missing translation

The [review manifest](../../tools/english-localization-review.json) documents each retained literal. Important examples:

- `Estrategias`, `Indicadores`, `Reportes`, old live-state/cache/lock filenames and report asset filenames;
- existing class/property names, enum values, order tags, feed IDs such as `importado` and `rithmic-solo`, parser/CSV aliases and historical export headers;
- the legacy simulated-account name embedded in saved ledger keys, old drawing-alert sentinels, report verdict codes and proposal/catalog source tags;
- exact old messages needed to recognize saved validation reports;
- English culture/language identifiers such as `en-US`, which are not Spanish prose.

English display helpers translate known legacy labels without renaming their underlying data. SDK overrides leave generated Strings.g.cs intact and preserve user override precedence. Exact legacy Simulator profile names are migrated for display; arbitrary custom names are not rewritten.

Personal history, existing reports/conversations, third-party indicators, arbitrary provider responses and system dialogs retain their own content/language. Existing scripts are not renamed automatically. In this follow-up the user explicitly authorized migrating the five local shipped samples, including code identifiers, after a backup and saved-reference check. Installation must not rewrite other personal content just to achieve a misleading language score.

## Problems repaired during review

- Stale translated-label selectors in snapshot scenarios.
- Technical indicator source mismatch (`Technical` display versus internal `Técnicos`).
- Spanish safety text and partial English/Spanish status/report fragments, including paid-download charging language.
- Strategy/indicator/laboratory/risk-agent prompts requesting Spanish output.
- English default indicator settings without editing generated compatibility resources.
- Saved-account ledger recovery and old drawing alerts broken by translating internal identifiers; both restored and regression-tested.
- Existing saved validation prefixes and automatic-agent source tags preserved.
- Imported data display labels localized while retaining the stored feed identity.
- Documentation installation commands now select this fork's English branch rather than Spanish main.
- Live chart status translates the legacy account label for display, and the live-launch dropdown refreshes compiled English strategy metadata.
- Data-page action buttons sit below the title to avoid narrow-window header clipping.
- A concurrent fake Tradovate request log was made thread-safe without changing real broker execution.
- Existing Spanish sample source was migrated with explicit permission, and all future strategy/indicator generators now use English filenames and code identifiers, including the separate New indicator template.
- English guide code examples and Claude new-code instructions now use English identifiers too; arbitrary existing user code is still preserved unless a rename is requested.

## Limits and deliberate exclusions

Two existing warnings remain: CS0414 in CumulativeDeltaCandles and CS0108 for ChartTraderPanel.Template. They are not introduced language failures.

No real-money orders, real provider credentials, actual paid Claude calls or privileged closed-lid operation are used as translation tests. Existing fake broker scenarios, the fake Claude bridge and captured power-helper script test built-in English paths safely. Windows cross-publishing is checked, and shared Windows runtime tests are added to CI, but a complete Windows desktop port is not certified. Real brokerage connectivity/fills, provider licensing/model availability, actual paid-model output, physical closed-lid/thermal behavior and arbitrary extensions remain unverified. Raw vendor/model/system responses can have their own language; diagnostics are not silently rewritten or discarded.

A language heuristic and passing tests cannot prove arbitrary runtime content is English or certify trading safety. Completion claims apply to the reviewed built-in surfaces and full English guides, with the explicit compatibility/content exclusions above.

## Reproduce

```bash
export PATH="$HOME/.dotnet:$PATH"
dotnet build TradingHub.slnx -c Release
dotnet test tests/TradingHub.Tests -c Release
python3 tools/check_english_translation.py

fixture="$(mktemp -d /tmp/tradinghub-english.XXXXXX)"
TRADINGHUB_DATA_DIR="$fixture/data" \
TRADINGHUB_STRATEGIES_DIR="$fixture/strategies" \
TRADINGHUB_INDICATORS_DIR="$fixture/indicators" \
TRADINGHUB_KEYCHAIN_FILE="$fixture/fake-keychain.json" \
TRADINGHUB_SLEEP_GUARD=off \
dotnet run -c Release --project tools/TradingHub.UiSnapshot -- \
  "$fixture/screenshots" 2 \
  trading settings connections data strategies accounts live english \
  panel footprint mercado conexiones data+rithmic data+import trading+strategy theme
```

For a narrow-window check, use a fresh fixture with the same four isolation variables and disabled sleep guard, then run the seven main pages plus `english --size 1100x760`. Scene identifiers such as `mercado` and `conexiones` remain stable test contracts.

Final follow-up evidence: `/tmp/tradinghub-samples-final.L3HCDr/screenshots` and `/tmp/tradinghub-samples-narrow.40OfLU/screenshots`. The original five-file migration checks/replays are in `/tmp/tradinghub-sample-migration.R7Ns8W/check.log`; the local sample backup is `~/Library/Application Support/Trading Hub Backups/english-samples-20261019-002254/`. Native installed-app captures are kept locally, not published with personal profile data. The rollback copy is under `~/Library/Application Support/Trading Hub Backups/english-20261018-231314/`. A new ad-hoc signature can require macOS Documents permission again; the user approved that prompt during this installation. Temporary evidence is not a permanent release artifact; CI uploads isolated screenshots and HTML reports when that workflow runs.
