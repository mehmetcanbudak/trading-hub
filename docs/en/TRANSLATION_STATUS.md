# Trading Hub — English localization validation

**Reviewed:** 2026-11-03
**Branch:** `memoshi`
**Implementation:** canonical English-first C# branch consolidating the English, safety, upstream-sync and historical main histories. Upstream Windows v1.1 and multilingual v1.2 are included; existing English installations remain English. The installation follow-up repairs signed macOS bundle copying, repeated main-window startup after a tray reopen and concurrent Rithmic working-order publication exposed by Windows ARM64. Normal installation and isolated native UI validation are user-authorized; Rust/Swift worktrees and pins are not changed by this work. This is not real-broker or physical-power certification.

**Review:** [Integration PR #4](https://github.com/mehmetcanbudak/trading-hub/pull/4) and safety PR #3 retain the review/evidence history. The original safety source is not approved unchanged; its corrections are included. The user authorized consolidation into `memoshi`, then normal macOS installation and isolated release validation, not an automatic public release or a port rebase.

## Scope and provenance

This fork reuses the 28-commit community translation at `65ae0229bc0b36ba44156cfaaa530607c382ec25` from [indigiti/TradeDock](https://github.com/indigiti/tradedock/tree/translation/english-documentation-ui). Its original [draft PR](https://github.com/indigiti/tradedock/pull/1) remains a separate upstream review, not a release approval for this fork. Original application baseline: `9ee74a4c527ac43120423acd41e006ec9d7877ad`.

Completed coverage includes navigation, controls/tooltips, secondary windows, safety dialogs, chart/DOM/footprint labels, native indicator/SDK settings, compiler diagnostics, live/backtest status/errors, reports, built-in examples and English Claude/risk-agent instructions. All 16 detailed feature/setup/porting guides now cover their original sections, API examples and safety rules rather than being condensed summaries.

## Current upstream integration — 2026-11-02

Source revisions:

- English starting point: `5f7c19e7528f47582f34f14d2c06e71d388b9858`.
- Safety PR #3 starting point: `5fe1bdf53c1aad92fabfbc73576c75bbeb8f0a1f`.
- Windows/platform upstream: `9c17ef1957ccd20ad2b04feadf2c69241504a86b`.
- Multilingual upstream: `c28900e03ba473ee21f3983e65c1adf980ae0077`; live upstream recheck on 2026-11-02 found no later main commit.
- Both upstream commits are reconciled. `main`, `feat/english-localization`, `fix/live-order-safety` and `sync/upstream-c28900e` are ancestors of the consolidated branch; their old refs are retained for rollback. All six dirty English tracked-file changes were already represented in the integration; three previously untracked historical notes under `docs/research/` and `plans/` are now preserved without rewriting their dated conclusions.

### Required safety behavior

These are integration requirements, not optional translation details:

1. **Quit and restart:** closing a window must retain background live strategies by default. Reopening the existing main window must preserve its chart pages and must not rerun startup auto-resume. Canceling Quit or a language restart must preserve the existing process and start no replacement. A replacement waits for the old process to exit and preserves its arguments. Explicit stop/close waits for acknowledgments and reports unresolved orders or positions honestly.
2. **Dangerous confirmations:** Cancel remains the initial/default safe choice; Enter/Escape cannot accidentally approve a destructive action. The deliberate confirm gesture uses the platform's Command/Ctrl modifier. Existing real-account launch, paid-download, deletion and emergency-stop safeguards remain operational.
3. **Order ownership and recovery:** stable IDs, legacy ledger identities and manual holdings remain intact. Unknown snapshots are not flat; complete position/order replay precedes adoption. Concurrent order/PnL callbacks must not overwrite newer working protection with an older order snapshot. Partial fills resize/cancel sibling protection without closing unrelated holdings. Cancel acknowledgments and late fills remain tracked after a strategy is stopped. A failed or partial ledger append cannot be counted twice in the same process.
4. **Permission and connection boundaries:** permission is checked at order send; revoked permission blocks new exposure while allowing reductions. Replaced credential/transport contexts reject new exposure but remain available for cancellation and close acknowledgments until confirmed idle. Closing an owned Spot quantity must not liquidate manual or unrelated-asset balances.

Sibling protection remains emulated, not atomic broker-native OCO. Ledger append recovery is process-local, not a promise of crash durability. Those limits must not be hidden by translated text or passing fake-provider tests.

### Current evidence

| Gate | Observed result | Scope / remaining limit |
|---|---|---|
| Complete Release solution build | Passed; 0 errors | .NET 10.0.402 on Apple Silicon after the one-time-startup fix. This build emitted the pre-existing CS0108 warning; the historical CS0414 warning below remains unchanged. |
| Full compatibility/safety suite | **351 passed, 7 Windows-only skipped, 358 total** | Full suite after the Rithmic publication repair: `release-install-session/unit-after-rithmic-fix/rithmic-publication-fixed.trx`. Both original partial-stop/user-limit theory cases and their ownership/sibling assertions are unchanged. Native Windows cases require the new hosted matrix. |
| Failing-before safety regressions | Confirmed | Pinned safety source: ledger suite 3 failed/8 passed; corrected runner/replay/close suite 24 failed/112 passed. The integrated full suite passes. |
| English source/review guard | **191 files, 11,979 text tokens, 111 documented exceptions; 0 failures** | Full source/docs audit after reviewing the main-window startup guard; 92 explicit fingerprints. Original `9ee74a4` audit baseline and numeric/template checks are unchanged. |
| Canonical branch consolidation | **Local configuration and runtime gates passed** | At consolidation, `memoshi` used the `617259d` runtime already verified by all three hosted native jobs. That cutover passed Release build, 351 tests/7 Windows-only skips, 13 tracker tests, source/docs/catalog audits and 120 English/88 safety/42 quit UI checks. The installation/startup repair below is an additional reviewed runtime change with its own new regression and native evidence. Original dirty English state is backed up, and this task leaves port refs/worktrees/pins untouched. |
| Binance replacement acknowledgment fixture | **Corrected after an observed CI timing failure; local full suite passes** | Initial `memoshi` run 41511774403 failed the regular case on macOS and conditional case on Windows ARM64: a fixed three-second reply timer expired before the observer asserted the old ID. Windows x64 completed all gates. The fixture now holds the actual HTTP acknowledgment asynchronously and waits for a successful completed poll, retaining every ID/price/quantity/single-handle assertion. A smoke using the real `BinanceBroker` implementation against fake HTTP holds replies for 4.2 seconds across 16 polls for each order type, then verifies exact exchange values and handle identity after release. Both cases and 351 full-suite tests pass locally; 7 Windows-only skips remain. No production routing/source change. |
| Hosted macOS runner acquisition | **Resolved for the installation follow-up; complete native job passed** | [Run 41604359692](https://github.com/mehmetcanbudak/trading-hub/actions/runs/41604359692), revision `dd1d9b8`, obtained `macos-26` ARM64 and passed every step, including the newly added real build/install/strict-signature gate. The earlier `macos-15` allocation failures remain infrastructure history, not product-test passes. No gate was disabled. |
| Rithmic working-order publication | **Controlled failure before; passing after; full local suite passes** | Holding an older sync before the broker gate reproduced a newly acknowledged stop disappearing from `ActiveOrders`. Capturing, applying and publishing under the same existing gate prevents that forced stale-snapshot interleaving; lock-free list readers use volatile publication and notifications remain outside the gate. This concurrency fix still requires fresh native Windows CI. |
| Negative audit enforcement | **5 expected failures**, restored baseline passes | Rejects unaccented and accented Spanish, an application numeric change, a raw generated-C# numeric change, and a new unreviewed English source file. Probes exist only in an owned detached fixture and are removed afterward. |
| English guides and links | Passed | 22 Markdown files, 196 local links/anchors and 16 guide section checks before this dashboard evidence update. |
| Upstream language reconciliation | **3,300/3,300 entries covered in each language; 0 missing/mismatched** | 4,674 canonical English-source keys per catalog; 105 reviewed collisions and 202 authoritative overrides, including the native Command/Ctrl confirmation tooltip. Compatibility aliases/provenance are retained, not silently discarded. |
| English desktop UI | **17 scenes; 121 English, 88 safety and 42 quit checks; 0 failures** | Actual isolated Avalonia windows and fake providers. The added saved-page reopen regression failed before the production fix and passed afterward. `safety` includes the quit scenario; `quit` is not a standalone scene name. |
| Spanish and Simplified Chinese desktop UI | **7 main pages each; 0 known untranslated candidates** | Final screenshots generated after terminology fixes. Chinese Settings/Trading and Spanish Live were visually inspected, including the auto-resume explanation, partial-position reduction and emergency-close labels. A heuristic is not a natural-language proof. |
| Generators and report paths | Passed in the English scenario and three-language export smoke | Five strategies, two indicator sources and three complete guide examples compile; backtest/optimization/walk-forward/portfolio prose and self-contained chart HTML are exercised. Actual English/Spanish/Chinese HTML export localizes prose while preserving inline C# and numeric data. Generated C# identifiers remain English in every UI language. |
| Tradovate credential replacement | Passed after a real service-path failure was fixed | Fake HTTP/WebSocket traffic proves old contexts can cancel/close and receive acknowledgments while fresh credentials service new exposure. No real credentials or orders. |
| Tradovate historical packet handoff | **Forced failure before, passing after** | Native Windows exposed 10/12 ticks. A controlled producer/registration interleaving then reproduced history completion with 0 buffered data packets before the fix and 1/1 delivered afterward. Route publication and orphan append/removal now share an atomic gate; callbacks stay outside that gate. The public regression still requires all 12 ticks and unchanged prices, ordering, aggressor sides and cancellation. |
| Spot position recovery | Passed after failing-before regressions | Signed fake account snapshots use free+locked base-asset balances; delayed/failing snapshots stay unknown; close is limited to the resumed strategy's owned quantity. No invented aggregate cost basis or PnL. |
| Restart process handoff | Passed after an observed early-launch failure | Child processes test predecessor exit and exact argument preservation, including Unicode/quotes/newlines. Actual packaged LaunchServices restarts also exited the predecessor, created exactly one replacement and preserved all ten explicit fixture environment values; both English and Spanish surfaces rendered. Cancel/protection cases remain covered by the fake-account quit scenario, not a real brokerage claim. |
| Windows packaging | **Both installers built; native x64 and ARM64 install/open/close/uninstall passed** | Successful run 41502632224 builds both architectures on both Windows hosts, executes each host's native installer and preserves the fixture-data hash through uninstall. Captures target the owned application HWND, not the desktop. Installers remain unsigned review artifacts, not a published signed release. |
| macOS packaging and installation | **Ad-hoc 1.2.0 staged and installed strict/deep signatures verified** | The initial normal-profile install restored the English workspace and custom indicator after the user approved Documents access. Installation had exposed stripped managed-assembly signing attributes; the copy now preserves them and verification remains strict. The corrected startup package additionally passed isolated native lifecycle testing. |
| macOS tray startup and native lifecycle | **Failing before; corrected native restart/tray/quit passed** | A controlled one-chart simulator fixture became two charts after native close/reopen before the fix. The corrected signed package retained exactly one chart across three native close/tray-open cycles; native menu Quit exited without force termination. Startup screen fit, workspace restore and delayed auto-resume now run once per main-window lifetime. |
| Packaged native MCP | **Passed on the final rebuilt bundle** | Actual Apple Silicon apphost initialized, answered ping and listed 16 tools with exit 0 and empty stderr; legacy settings bytes were unchanged. Receipt: `packaged-mcp/final-result.json`. This is not a GUI lifecycle test. |
| Catalog reproduction | **Byte-for-byte reproducible** | Actual `rebase.py` CLI using the pinned checkpoint/safety/upstream refs reproduced both catalogs, provenance and the collision manifest with identical SHA-256 digests. Maintainer dependency versions and the exact command are documented in the sync guide. |
| Upstream tracker and script syntax | **13 tracker tests passed; Bash/zsh syntax passed** | Synthetic histories/fake issue API only; no checkpoint advancement or notifications. |
| Native Windows regression suites | **Latest x64 passed; ARM64 exposed one safety failure** | Revision `dd1d9b8`, [run 41604359692](https://github.com/mehmetcanbudak/trading-hub/actions/runs/41604359692): x64 completed all gates; ARM64 had 357 passed/1 failed in the partial-stop/user-limit ownership case. Earlier run 41502632224 passed 358/358 on both hosts, but does not certify this newer source. The repaired Rithmic code must pass a new matrix without skipped assertions or longer waits. |
| Native Windows x64 and ARM64 desktop | **Latest x64 complete; prior ARM64 complete** | Run 41604359692 completed x64 English/source/safety/UI and native installer gates. ARM64 stopped at its unit/safety failure and is not a fresh desktop pass. Earlier run 41502632224 verified both native owned-window captures, installers, open/close/uninstall and fixture-data preservation. Installers remain unsigned review artifacts. |
| Screenshot bitmap ownership | **61 owned captures corrected; complete native rerun passed** | Run 41498553556 faulted with `0xC0000005` in Skia's native PNG encoder during x64 theme capture. Every capture now retains its bitmap through encoding and disposes deterministically. All 17 local scenes plus three extra theme passes and the complete x64/ARM64/macOS run 41502632224 pass afterward, without disabling a renderer, scene or assertion. The precise native fault mechanism is not proven; this is observed non-recurrence plus an ownership correction, not a guarantee about all third-party native code. |
| Hosted macOS ARM64 | **Complete installation-follow-up job passed** | Revision `dd1d9b8`, run 41604359692: Release build, 351 passed/7 Windows-only skipped, English/source/catalog audits, all 17 English scenes, seven Spanish/Chinese pages each and the real self-contained build/install/verify step passed. This is distinct from the subsequent Rithmic concurrency repair awaiting its own hosted matrix. |

Current local evidence is under `/Volumes/TradingHub-upstream-a861ab10ae/evidence/integration`; failing-before receipts are under the sibling `evidence/safety-before` directory. Automated-test artifacts are isolated. Personal-profile native captures stay privately local and are not published. These receipts are not a public app release; the workflow uploads only its own clean screenshots, reports, TRX results and Windows installers.

### Installation and native lifecycle follow-up — 2026-11-03

The real `./build-app.sh --install` path initially failed strict signature verification on `System.Xml.Linq.dll`. Its bytes matched the verified staged library, but the installation copy had removed `com.apple.cs.CodeDirectory`, `CodeRequirements` and `CodeSignature`. The installer now uses the metadata-preserving `ditto` copy already used for packaged output; no signature check is weakened. The macOS workflow now executes the real build/install/verify command so this consumer path is gated, not merely shell-parsed. A previous bundle and twelve state/script locations were privately backed up outside Git; market-history files and Keychain were not exported or reset.

Native tray isolation needs more than `HOME`: the real macOS known-folder getters still selected the personal Library/Documents paths in that probe. The accepted fixture pre-created standard Library/Documents folders under an isolated home, supplied both `HOME` and `CFFIXED_USER_HOME`, verified the actual `Product` getters, overrode the other stores including a fake Keychain, disabled the sleep guard and confirmed open state files were confined to the fixture. `TRADINGHUB_DATA_DIR` was intentionally absent only for this native tray fixture because the application otherwise disables tray installation in tools. No real-order control was used.

Release evidence is under `evidence/integration/release-install-session`: the signature diagnosis, normal-profile owned-window capture, native one-chart-to-two-chart failure, repeated corrected native cycles, packaged restart/quit receipt, full TRX and UI output remain distinct. The additional 166 scoped provider-recovery tests used fake exchanges; they do not constitute authenticated provider-demo testing. The user selected Binance Futures testnet for the separate authorized provider check and deferred physical sleep/lid checks until port jobs finish. Neither authenticated execution nor physical-power certification is inferred from public API pings, source review or fixture tests.

### Windows ARM64 Rithmic publication follow-up — 2026-11-03

The installation-follow-up matrix passed macOS and Windows x64 but failed one Windows ARM64 safety assertion: after a partial stop fill, the broker's active-order view contained only the remaining manual limit instead of the acknowledged replacement stop. This was not a compiler warning and is not treated as a harmless retry.

Source inspection found that overlapping Rithmic callbacks captured the session order list before acquiring the broker gate and published it after releasing that gate. A controlled older callback, delayed at exactly that boundary, deterministically hid a subsequently acknowledged stop. The repair captures, applies and publishes the order view and position under the same existing gate, publishes the list reference with volatile semantics and invokes notifications only after releasing the gate. It does not change protocol fields, order IDs, quantity math, manual-order ownership or exchange-native protection limitations.

The controlled broker/protocol smoke failed before the repair and passed afterward; the complete local suite then passed 351 cases with 7 Windows-only skips. Receipts are `rithmic-publication-probe-red/stale-publication-red.trx`, `rithmic-publication-probe-green/stale-publication-green.trx` and `unit-after-rithmic-fix/rithmic-publication-fixed.trx` under `release-install-session`. The scheduling hook and standalone probe are temporary isolated-checkout instrumentation, not a production API or a weaker permanent test. The existing partial-fill/manual-order cases retain every assertion. A fresh hosted matrix remains required for this repair.



### Release gates still open

- Release approval must review the native Windows x64/ARM64 and macOS checks, actual application captures and explicit safety requirements. The installer/startup follow-up passed hosted macOS and x64; its ARM64 safety failure is visible above. The Rithmic repair has failing-before/passing-after local evidence but still needs a fresh complete hosted matrix. Making `memoshi` canonical or installing it is not permission to route real orders.
- Packaged macOS GUI restart/tray/quit and the corrected repeated-reopen path have been exercised with isolated stores in the authorized desktop session. Review the evidence and the newly added hosted macOS install gate before stronger release claims; the personal profile is not an automated fixture.
- Real broker/provider licensing, native cancel/fill races, disconnect/reconnect behavior and account-side risk settings require an authorized simulated/provider test environment. Fake providers do not certify brokerage execution.
- Physical Windows/macOS sleep/lid/thermal behavior, actual paid-model availability/output and arbitrary third-party extensions are not exercised. The user explicitly deferred physical Mac checks until active port jobs finish. No privileged power operation or paid call was made.
- Preserve all required safety behavior above. The rolling upstream checkpoint records reviewed source integration after the established native/English/safety gates, not real-money, signed-release or physical-power certification. Keep those release limits visible after checkpoint advancement.

### Ports' pinned-reference decision

`memoshi` is the canonical C# base for future Rust/Swift work. This installation/validation task does not modify either port worktree, branch or reference pin. Their agents must deliberately adopt an exact reviewed `memoshi` SHA, preserve ownership/replay/cancellation/quit semantics and one-time startup behavior with native tests, and reconcile English-source multilingual behavior. C# Windows credential, tray, power and CLI APIs are not portable implementations and do not justify silently repinning either port.

## Historical evidence — 2026-10-19

| Check | Result | Evidence |
|---|---|---|
| Release application/solution build | Passed | .NET SDK 10.0.402, Apple Silicon macOS; two pre-existing warnings noted below. |
| Unit/regression suite | **222 passed on macOS and Windows** | Original 192 cases, 29 localization/compatibility cases and one forced busy-port fixture regression. Hosted run 39238567184 passes both platforms. |
| Fake Tradovate request-log race | Fixed in test fixture only | ConcurrentQueue replaces a concurrently read/written List; previously failing test passed **20/20** repeated runs. |
| Source localization audit | Passed | **184 files / 11,531 text tokens**, zero unresolved candidates; **129** explicit compatibility/language-code exceptions. |
| Non-text C# review guard | Passed | Baseline token fingerprints plus 36 reviewed display/compatibility/sample files; raw strategy/indicator template C# is recursively fingerprinted. |
| English guides and links | Passed | **22 Markdown files**, **173 local links/anchors**, **16 guide section-coverage checks**. Section counts supplement, not replace, semantic review. |
| English UI scenario | **120 checks, zero failures** | Seven main pages; nine secondary windows; chart-setting tabs; technical-source badge; actual safety dialogs; reports and compiler output. |
| Safety cancellation | Passed | Cancel real-account launch, paid Databento download, data deletion and emergency stop; no runner launched, paid data requested or history deleted. |
| Built-in/example compilation | Passed | Five strategy templates, the initial custom indicator, the New indicator editor template and all three complete guide examples compile with English code identifiers. The New indicator regression was observed failing before its fix. |
| Existing sample migration | Passed locally | Five original files backed up and hash-verified outside watched folders. Explicitly authorized English filenames/classes/parameters/private/local names; no saved references existed. All non-text tokens match after the identifier map; four old/new strategies retain identical trades/equity with default and customized parameters (143–589 trades per replay). Indicator threshold default and series ID unchanged. |
| Claude bridge, without paid usage | Passed | Fake CLI exercises actual bridge success/session parsing, authentication recognition and empty-response fallback. Both new-code prompts request English identifiers. No real model call or session was used. |
| Closed-lid helper, without power changes | Passed | Fake RunAdminScript captures the English administrator explanation and verifies assertion/flag state and cleanup. No privileged helper or pmset change executed. |
| Windows | Passed for shared runtime | Hosted run 39238567184 builds the solution, passes all 222 regression tests plus 13 tracker tests and validates English source/documentation. Unix-only test-fixture assumptions and CRLF-sensitive fingerprints were repaired without changing production locks or recovery assertions; numeric/raw-code mutation checks remain active. Native Windows desktop/system integration remains outside this evidence. |
| Report generation/export | Passed | Backtest, optimization, walk-forward and portfolio prose; parameter display names; fallback verdict metadata; self-contained English HTML with embedded charts. |
| Test isolation guards | Passed | Direct production-data root and a symlink alias both refused before application startup. Sleep guard disabled; fake Keychain/providers used. |
| Installer syntax | Passed | `zsh -n build-app.sh`; only comments/messages translated, packaging behavior unchanged. |
| Broader scenarios / narrow-window visual review | Passed | Final **16-scenario** run exited 0 with **65 screenshots**; fresh **1100×760** run exited 0 with 120 English checks. Main pages, secondary windows and safety dialogs visually inspected. |
| Upstream tracker unit tests | **13 passed on Linux, macOS and Windows** | Temporary Git histories and fake issue API cover advancement/rewrite detection, checkpoint validation, doc mappings, note preservation, retry-safe deduplication, bounded output and fork-only writes. Live setup and quiet manual rerun are verified below. |
| Negative audit gates | Passed | Unaccented Spanish, accented Spanish and new unreviewed English source were rejected. Temporary probes removed. |
| Hosted GitHub workflow | Passed | [Workflow](../../.github/workflows/english-translation-validation.yml), [run 39238567184](https://github.com/mehmetcanbudak/trading-hub/actions/runs/39238567184) validates commit `bd02d9b`: Windows shared build/222 regressions/13 tracker tests/source-doc audit; macOS Release build/222 regressions/13 tracker tests/source-doc audit and all 16 UI scenarios, including 120 English checks. Isolated screenshots and HTML reports uploaded as `tradinghub-english-ui`. This is this fork's evidence, not older TradeDock CI. |
| Installed English app | Passed | Updated bundle installed and strict signature verification passed. Native Backtesting shows compiled DeltaAbsorption.cs with English code, and Custom indicator editor shows compiled DeltaPercentageExample.cs with English code. All five files migrated with permission; no saved references needed rewriting. Previous bundle/source/state rollback remains outside watched folders, and real-order permissions remain disabled. |

## Documentation follow-up — 2026-10-22

Preserved the original dirty English worktree's documentation-only safety guidance without editing that worktree: indicator fixed-history/forming-candle/look-ahead/state validation, partial-fill and pending-cancellation checks, and live incident/reconciliation checks. Its historical audit covered 177 links/anchors and 16 guide section checks; it did not claim a new app, UI, broker or installation run. The integrated guides retain that guidance.

## Upstream maintenance

The [upstream sync guide](upstream-sync.md), [checkpoint ledger](../../.github/upstream-english.json) and [daily/manual monitor](../../.github/workflows/upstream-english-tracking.yml) track the original repository separately from `memoshi`. `integrated_commit` advances to `c28900e03ba473ee21f3983e65c1adf980ae0077` for this user-authorized consolidation: the unchanged runtime passed [all three native platform jobs at `617259d`](https://github.com/mehmetcanbudak/trading-hub/actions/runs/41506213481), and fresh `memoshi` configuration, tracker, compatibility, English, catalog and UI checks pass. The checkpoint records reviewed upstream source, not app installation or brokerage/physical-power certification. The historical C# audit baseline stays at `9ee74a4c527ac43120423acd41e006ec9d7877ad`.

Local tracker tests cover synthetic upstream additions, history rewrites, invalid checkpoints, English doc mappings, preserved issue notes/checklists, bounded output, notification deduplication/retry and fork-only writes. The monitor never merges, auto-translates, advances the checkpoint, installs an app or touches trading data. Historical setup runs 39237527539 and 39237638444 maintained exactly one tracker issue without duplicate update comments; their then-current “Up to date” reports are not evidence for the present tree. Daily/manual monitoring now checks canonical `memoshi`, with Issues enabled and the schedule at 06:37 UTC. Future integration/review targets this fork's `memoshi`, never the original author's repository, historical main or either port.

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
- Shared Windows validation exposed two fixture lifetime/locking assumptions; tests now model historical damage offline and dispose every acquired lock. Production locking was not weakened.
- Source-review fingerprints now ignore LF/CRLF differences, with an observed red/green raw-string regression and unchanged numeric/code-mutation gates.

## Limits and deliberate exclusions

Two existing warnings remain: CS0414 in CumulativeDeltaCandles and CS0108 for ChartTraderPanel.Template. They are not introduced language failures.

No real-money orders, real provider credentials, actual paid Claude calls or privileged closed-lid operation are used as translation tests. Existing fake broker scenarios, the fake Claude bridge and captured power-helper script test built-in English paths safely. Windows cross-publishing and hosted shared-runtime build/tests/source auditing pass, but a complete Windows desktop port is not certified. Real brokerage connectivity/fills, provider licensing/model availability, actual paid-model output, physical closed-lid/thermal behavior and arbitrary extensions remain unverified. Raw vendor/model/system responses can have their own language; diagnostics are not silently rewritten or discarded.

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
TRADINGHUB_BACKTESTS_DIR="$fixture/backtests" \
TRADINGHUB_LIVE_DIR="$fixture/live" \
TRADINGHUB_KEYCHAIN_FILE="$fixture/fake-keychain.json" \
TRADINGHUB_SLEEP_GUARD=off \
dotnet run -c Release --project tools/TradingHub.UiSnapshot -- \
  "$fixture/screenshots" 2 \
  trading settings connections data strategies accounts live english \
  panel footprint mercado conexiones data+rithmic data+import trading+strategy theme safety
```

For a narrow-window check, use a fresh fixture with the same six isolation variables and disabled sleep guard, then run the seven main pages plus `english --size 1100x760`. For Spanish/Chinese page captures, use a fresh fixture for each language and set `TRADINGHUB_LANG=es` or `TRADINGHUB_LANG=zh-CN`. Scene identifiers such as `mercado` and `conexiones` remain stable test contracts.

Final follow-up evidence: `/tmp/tradinghub-samples-final.L3HCDr/screenshots` and `/tmp/tradinghub-samples-narrow.40OfLU/screenshots`. The original five-file migration checks/replays are in `/tmp/tradinghub-sample-migration.R7Ns8W/check.log`; the local sample backup is `~/Library/Application Support/Trading Hub Backups/english-samples-20261019-002254/`. Native installed-app captures are kept locally, not published with personal profile data. The rollback copy is under `~/Library/Application Support/Trading Hub Backups/english-20261018-231314/`. A new ad-hoc signature can require macOS Documents permission again; the user approved that prompt during this installation. Temporary evidence is not a permanent release artifact; CI uploads isolated screenshots and HTML reports when that workflow runs.
