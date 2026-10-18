# Trading Hub — English localization validation

**Reviewed:** 2026-10-18
**Branch:** `feat/english-localization`
**Implementation:** complete and locally installed for reviewed built-in text and English guides. Broad/narrow UI checks and native macOS startup passed; this is not a guarantee about arbitrary user/provider content or trading safety.

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
| Non-text C# review guard | Passed | Baseline token fingerprints plus 33 reviewed display/compatibility files; raw strategy/indicator template C# is recursively fingerprinted. |
| English guides and links | Passed | **21 Markdown files**, **160 local links/anchors**, **16 guide section-coverage checks**. Section counts supplement, not replace, semantic review. |
| English UI scenario | **93 checks, zero failures** | Seven main pages; nine secondary windows; chart-setting tabs; technical-source badge; actual safety dialogs; reports and compiler output. |
| Safety cancellation | Passed | Cancel real-account launch, paid Databento download, data deletion and emergency stop; no runner launched, paid data requested or history deleted. |
| Built-in/example compilation | Passed | Five templates and all three complete strategy/indicator guide examples compile. |
| Report generation/export | Passed | Backtest, optimization, walk-forward and portfolio prose; parameter display names; fallback verdict metadata; self-contained English HTML with embedded charts. |
| Test isolation guards | Passed | Direct production-data root and a symlink alias both refused before application startup. Sleep guard disabled; fake Keychain/providers used. |
| Installer syntax | Passed | `zsh -n build-app.sh`; only comments/messages translated, packaging behavior unchanged. |
| Broader scenarios / narrow-window visual review | Passed | Final **16-scenario** run exited 0 with **65 screenshots**; fresh **1100×760** run exited 0 with 93 English checks. Main pages, secondary windows and safety dialogs visually inspected. |
| Negative audit gates | Passed | Unaccented Spanish, accented Spanish and new unreviewed English source were rejected. Temporary probes removed. |
| Hosted GitHub workflow | Updated; publication validation pending | [Workflow](../../.github/workflows/english-translation-validation.yml); do not substitute older TradeDock CI for this working tree. |
| Installed English app | Passed | Previous app and all 11 local data/document files backed up and hash-verified; install and strict signature verification passed. Native English startup verified with the existing profile after the user approved macOS Documents access. Existing scripts/indicators unchanged; real-order permissions remain disabled. |

## Compatibility is not missing translation

The [review manifest](../../tools/english-localization-review.json) documents each retained literal. Important examples:

- `Estrategias`, `Indicadores`, `Reportes`, old live-state/cache/lock filenames and report asset filenames;
- existing class/property names, enum values, order tags, feed IDs such as `importado` and `rithmic-solo`, parser/CSV aliases and historical export headers;
- the legacy simulated-account name embedded in saved ledger keys, old drawing-alert sentinels, report verdict codes and proposal/catalog source tags;
- exact old messages needed to recognize saved validation reports;
- English culture/language identifiers such as `en-US`, which are not Spanish prose.

English display helpers translate known legacy labels without renaming their underlying data. SDK overrides leave generated Strings.g.cs intact and preserve user override precedence. Exact legacy Simulator profile names are migrated for display; arbitrary custom names are not rewritten.

Personal history, existing reports/conversations/scripts, third-party indicators, arbitrary provider responses and system dialogs retain their own content/language. Installation must not rewrite or delete them just to achieve a misleading language score.

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

## Limits and deliberate exclusions

Two existing warnings remain: CS0414 in CumulativeDeltaCandles and CS0108 for ChartTraderPanel.Template. They are not introduced language failures.

No real-money orders, real provider credentials, actual paid Claude calls, privileged closed-lid operation or Windows runtime are used as translation tests. Live brokerage connectivity/fills, provider licensing/model availability, power/thermal behavior and every possible user extension remain unverified.

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

Final local evidence: `/tmp/tradinghub-english-final.7YtAup/screenshots` and `/tmp/tradinghub-english-narrow.tAp20W/screenshots`. Native installed-app captures are kept locally, not published with personal profile data. The rollback copy is under `~/Library/Application Support/Trading Hub Backups/english-20261018-231314/`. A new ad-hoc signature can require macOS Documents permission again; the user approved that prompt during this installation. Temporary evidence is not a permanent release artifact; CI uploads isolated screenshots and HTML reports when that workflow runs.
