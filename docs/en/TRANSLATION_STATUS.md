# Trading Hub — English localization validation

**Reviewed:** 2026-11-03
**Branch:** `memoshi`; **reviewed runtime:** `9d5791a`; **PR #5 merge:** `265d60f`.
**Implementation:** canonical English-first C# reference with the consolidated English, reviewed safety, Windows v1.1 and multilingual v1.2 histories. The approved integration includes latest upstream `1d5a774`, process-crash-consistent ledger replay and broker-acknowledged protection hardening. Existing English installations, persistent identities and real-account controls remain intact. Rust/Swift branches/worktrees/pins and the installed personal-profile runtime `6363664` are not changed by this follow-up. Public releases remain explicitly deferred.

**Review:** [PR #5](https://github.com/mehmetcanbudak/trading-hub/pull/5) merged at `265d60f93f7a99aaa287c459f9fcf5d8dc827627` after the user approved integration for personal Mac use with documented release/provider/native-Windows limits. [Integration PR #4](https://github.com/mehmetcanbudak/trading-hub/pull/4) and safety PR #3 retain the earlier history; the original safety source was corrected, not accepted unchanged. This approval does not publish a release, replace the installed app or authorize a port rebase.

## Scope and provenance

This fork reuses the 28-commit community translation at `65ae0229bc0b36ba44156cfaaa530607c382ec25` from [indigiti/TradeDock](https://github.com/indigiti/tradedock/tree/translation/english-documentation-ui). Its original [draft PR](https://github.com/indigiti/tradedock/pull/1) remains a separate upstream review, not a release approval for this fork. Original application baseline: `9ee74a4c527ac43120423acd41e006ec9d7877ad`.

Completed coverage includes navigation, controls/tooltips, secondary windows, safety dialogs, chart/DOM/footprint labels, native indicator/SDK settings, compiler diagnostics, live/backtest status/errors, reports, built-in examples and English Claude/risk-agent instructions. All 16 detailed feature/setup/porting guides now cover their original sections, API examples and safety rules rather than being condensed summaries.

## Current upstream and safety follow-up — 2026-11-03

The reviewed upstream input is `1d5a774d3d1bf2baf9259597234d227daad915a9`, merged at `f9bf505`. It adds Mac-download guidance only; it introduces no application, provider or strategy feature. English, Spanish and Chinese instructions distinguish this fork's source build from the original author's binary, which does not include fork fixes. The English primary README remains canonical; the obsolete duplicate English README is not restored.

The documentation merge's `src`, tests, tools, installer, solution and build inputs are identical to native-validated runtime `6363664` (`git diff --quiet` returned 0). This supports review of the **upstream documentation checkpoint**, not native certification of the additional fork runtime changes below. GitHub CI is unavailable by user report; no hosted run was manually dispatched or retried and no gate was disabled. A normal source push can still trigger the repository's existing automatic workflows; none is used here as fresh validation.

### Observed local follow-up gates

| Gate | Observed result | Boundary |
|---|---|---|
| Complete Release build | **0 warnings, 0 errors** | .NET 10.0.402 on Apple Silicon. CS0108 and CS0414 were removed without suppressions; new test-analyzer warnings were corrected too. |
| Final compatibility/safety suite | **468 passed, 7 Windows-only skipped, 475 total** | `final-exact-suite/results/final-exact.trx`; the entire suite was selected, with seven native-only cases skipped on macOS. The earlier full-run Binance market-cancel failure is retained, not treated as a harmless retry. |
| Controlled broker regressions | **64 Binance cases passed; full suite passed** | Deferred market cancel distinguishes already-filled, partially working and failed-lookup cases. Tradovate's forced disconnect-queue ordering regression failed before the production fix and passed afterward. |
| Fresh-process ledger consumer | **Failing before, passing after** | A killed writer left partial bytes after failed rollback. The old reader lost a closing fill and reported the wrong position/PnL; the corrected fresh reader recovered three fills once, then a normal append produced four fills, flat exposure and realized PnL 10. |
| English source/review guard | **191 files, 12,122 text tokens, 111 documented exceptions; 0 failures** | 97 individually reviewed structural fingerprints; historical baseline `9ee74a4` and the existing numeric/raw-template algorithm remain unchanged. |
| English guides | **22 files, 200 local links/anchors, 16 guide section checks passed** | Full `check_english_translation.py`, not just the source-only audit. |
| Upstream language coverage | **3,300/3,300 covered per language; 0 missing/mismatched** | 4,721 canonical keys per catalog, 249 authoritative overrides and 105 reviewed collisions. New recovery/acknowledgment prose is included; protocol markers and original vendor responses are not translated as application text. |
| Actual English UI | **17 scenes; 121 English, 96 safety and 42 quit checks; 0 failures** | Fake providers and six isolated stores. New history-fault captures show unknown position/PnL, visible diagnostics, blocked unsafe service actions and same-length history-cache invalidation. |
| Spanish/Chinese UI | **7 main pages each; 0 known untranslated candidates** | Rendered Chinese Settings and Spanish Live were visually inspected; this is finite surface coverage, not a natural-language proof. |
| Generators/reports | **Passed in the English scenario** | Five strategy templates, two indicator sources and three complete guide examples compile; existing backtest/report/export paths remain covered. |
| Private Mac package | **Self-contained ARM64; both staged/copied strict/deep signatures passed** | Ad-hoc signing only; `build-app.sh` ran without `--install`. Native MCP initialized, answered ping and listed 16 tools with exit 0/empty stderr. |
| Actual packaged Mac GUI | **Opened; Settings → Live responded; native close exited 0** | PID-scoped accessibility and owned-window captures only; no forced termination. This follow-up did not repeat native tray/restart acceptance or replace the installed app. |
| Windows platform outputs | **Self-contained x64 and ARM64 published; PE architectures verified** | No native Windows host, PowerShell or NSIS compiler was available here. No fresh Windows desktop, Credential Manager, IPC, power or installer result is claimed. |
| Tracker/shell checks | **13 tracker tests passed; Bash/zsh syntax passed** | Temporary histories/fake issue API; no broker credentials or trading data. |
| Actions maintenance | **checkout v7, setup-dotnet v6, upload-artifact v7** | Official stable action manifests use Node 24. This is source/manifest review, not a new hosted-run result. |
| ImgBot PR #1 | **Reviewed and closed without merging** | All eight optimized images retained identical RGBA pixels but changed color/EXIF metadata for only 136,614 bytes saved. Existing assets remain unchanged. |
| Actual Claude session/request | **Authenticated request attempted; account quota blocked output** | Real CLI 2.1.316 through production `ClaudeAgent.RunAsync`, default `claude-opus-5-5`, supported US$0.25 cap, restricted/safe mode, no tools/MCP/session persistence. The service reported its weekly limit; reported cost US$0.00, zero tools. |

The temporary direct-apphost GUI launch also emitted `sandbox_extension_issue_file_to_process ... Operation not permitted` for its owned temporary bundle. The window rendered, both native navigation actions succeeded and close exited 0; the diagnostic is retained in `mac-native/packaged-gui.json`. No privacy setting, signature check or system security control was weakened to suppress it. A normal installed-app update/acceptance remains separate.

After explicit user authorization, one minimal nonsensitive JSON request exercised the real production Claude bridge, not the fake CLI. The reply was **“You've hit your weekly limit · resets 8am (Europe/Istanbul)”** after about 6.46 seconds; no requested model output was produced and `NeedsLogin` was false. `actual-claude/ACTUAL_CLAUDE_RESULT.json` retains the exact result. No retry, alternate account, API-key fallback or quota workaround was attempted. Successful generation/model availability still requires restored quota and a later authorized retest.

### Reviewed safety changes and remaining guarantees

- A checksummed, generation-bound append intent is flushed before acknowledgment. Fresh readers combine the committed prefix with that exact intent once; the next save finishes its assigned bytes. Cross-process leases and deletion generations prevent concurrent replay/resurrection. Explicit Resume retries pending fills without a dummy execution and remains paused on storage failure.
- Storage faults block new/reversing exposure and fabricated financial totals, not late-fill accounting or existing protection/reductions. Unavailable history stays visible and prevents unsafe launch/restart, analysis and risk changes. [Live strategies](en-vivo.md) documents recovery and downgrade constraints.
- Unknown placement is retained under exact client identity rather than blindly retried. Delayed cancel/modify acknowledgments keep their owned handle. Actual working reports acknowledge amendments; latest queued prices supersede older disconnect moves. Bracket actions no longer depend on a notification subscriber, expired timeout or disappearing order.
- **No integrated live adapter currently submits native OCO groups.** Binance Futures' supported native `reduceOnly` is account-wide, not strategy-isolated OCO. Rithmic/Tradovate simple orders and Spot do not gain exchange enforcement from a local flag. Coordinated sibling protection is safer, not atomic; see [Trading](operar.md#stops-and-targets).
- Ledger recovery now covers **process-crash consistency**, not power loss/filesystem rollback, events lost before a durable intent, broker-event exactly-once delivery or authoritative execution-price accounting. Live-ledger prices remain last-market-price estimates without commissions. Unknown submission attribution remains process/session-local.

### Still open or deliberately deferred

1. Authenticated Binance Futures testnet: a usable dedicated key/secret must be entered privately in the isolated demo profile before authorized lifecycle, partial-fill, cancel/replace, reconnect, snapshot and late-fill scenarios. The previous one-time secret was never revealed; no secret is requested in chat or extracted from Keychain. Other providers need their own authorized scenarios.
2. Physical Mac sleep/wake/lid/thermal tests require user participation and a safe pause in the other agents' work. Native Windows physical behavior and fresh native regression/desktop/installer gates need a Windows host; cross-publishing is not a substitute.
3. Successful real Claude output is blocked by the account's weekly quota. The authorized capped request and error propagation were exercised; login is valid, but no model output or universal model availability is certified. Wait for quota reset and obtain later retest authorization rather than bypassing the limit.
4. Additional third-party strategies/indicators require a specifically selected source/version and isolated replay. Built-ins/examples and the previously authorized custom delta are not universal extension certification.
5. Human integration approval and PR #5 merging are complete. A later normal installation remains separate: the installed runtime is still `6363664`; fresh native tray/restart/personal-profile acceptance of this runtime was not claimed.
6. Public release publication, distribution signing/notarization and downloadable fork artifacts are **deferred by request**, not current personal-use work.

Local receipts are under `/var/folders/xm/66q9p6h91vs2w2l6fdl2j4z00000gn/T/memoshi-followup-fg6f_56e`. Focused TRX, captures, journal consumer stores and validation receipts are retained privately at `/Volumes/TradingHub-upstream-a861ab10ae/evidence/integration/memoshi-1d5a774-9d5791a`; the timed-out bulk copy was replaced by an explicit receipt/capture copy, not treated as successful. No personal-profile capture or credential was published.

## Consolidation baseline — 2026-11-02

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
3. **Order ownership and recovery:** stable IDs, legacy ledger identities and manual holdings remain intact. Unknown snapshots are not flat; complete position/order replay precedes adoption. Concurrent order/PnL callbacks must not overwrite newer protection with an older snapshot. Partial fills resize/cancel siblings without closing unrelated holdings. Cancellation acknowledgments and late fills remain tracked after stop. Published append intents must replay once across process restart; failures must not invent flat/zero history or reopen new exposure.
4. **Permission and connection boundaries:** permission is checked at order send; revoked permission blocks new exposure while allowing reductions. Replaced credential/transport contexts reject new exposure but remain available for cancellation and close acknowledgments until confirmed idle. Closing an owned Spot quantity must not liquidate manual or unrelated-asset balances.

Sibling protection remains emulated, not atomic broker-native OCO. The current follow-up replaces the old process-local append boundary with process-crash-consistent journaling, without claiming power-loss durability or broker-event exactly-once delivery. Passing fake providers must not hide those limits.

### Consolidation and installation baseline evidence

| Gate | Observed result | Scope / remaining limit |
|---|---|---|
| Complete Release solution build | Passed; 0 errors | Historical installation baseline on .NET 10.0.402. Its clean packaging/hosted builds still reported CS0108 and CS0414; both are removed in the current follow-up above, without suppressions. |
| Full compatibility/safety suite | **352 passed, 7 Windows-only skipped, 359 total** | Full local suite after the historical profile replay repair: `release-install-session/profile-replay/profile-replay-fixed.trx`. The new allocation regression failed before the fix. Existing partial-stop/user-limit ownership and sibling assertions are unchanged; earlier runtime `393bbb1` already passed both native Windows jobs in run 41613876096. |
| Failing-before safety regressions | Confirmed | Pinned safety source: ledger suite 3 failed/8 passed; corrected runner/replay/close suite 24 failed/112 passed. The integrated full suite passes. |
| English source/review guard | **191 files, 11,979 text tokens, 111 documented exceptions; 0 failures** | Full source/docs audit after the main-window startup, Rithmic publication and native profile replay reviews; 93 explicit fingerprints. Original `9ee74a4` audit baseline and numeric/template checks are unchanged. |
| Canonical branch consolidation | **Local configuration and runtime gates passed** | At consolidation, `memoshi` used the `617259d` runtime already verified by all three hosted native jobs. That cutover passed Release build, 351 tests/7 Windows-only skips, 13 tracker tests, source/docs/catalog audits and 120 English/88 safety/42 quit UI checks. The installation/startup repair below is an additional reviewed runtime change with its own new regression and native evidence. Original dirty English state is backed up, and this task leaves port refs/worktrees/pins untouched. |
| Binance replacement acknowledgment fixture | **Corrected after an observed CI timing failure; local full suite passes** | Initial `memoshi` run 41511774403 failed the regular case on macOS and conditional case on Windows ARM64: a fixed three-second reply timer expired before the observer asserted the old ID. Windows x64 completed all gates. The fixture now holds the actual HTTP acknowledgment asynchronously and waits for a successful completed poll, retaining every ID/price/quantity/single-handle assertion. A smoke using the real `BinanceBroker` implementation against fake HTTP holds replies for 4.2 seconds across 16 polls for each order type, then verifies exact exchange values and handle identity after release. Both cases and 351 full-suite tests pass locally; 7 Windows-only skips remain. No production routing/source change. |
| Hosted macOS runner acquisition | **Resolved for the installation follow-up; complete native job passed** | [Run 41604359692](https://github.com/mehmetcanbudak/trading-hub/actions/runs/41604359692), revision `dd1d9b8`, obtained `macos-26` ARM64 and passed every step, including the newly added real build/install/strict-signature gate. The earlier `macos-15` allocation failures remain infrastructure history, not product-test passes. No gate was disabled. |
| Rithmic working-order publication | **Controlled failure before; passing after; complete native matrix passed** | Holding an older sync before the broker gate reproduced a newly acknowledged stop disappearing from `ActiveOrders`. Capturing, applying and publishing under the same existing gate prevents that forced stale-snapshot interleaving; lock-free list readers use volatile publication and notifications remain outside the gate. Revision `393bbb1` passes all three native jobs in run 41613876096 without weakening the existing safety cases. |
| Historical session-profile replay | **Red/green allocation, exact semantics and native normal startup passed** | A real normal-profile GUI stall was traced to `SessionVolumeProfile.OnCalculate`. The repaired runtime `6363664` passes the complete native matrix and restores the four-tab English workspace, custom delta indicator and session profile. Settings → Trading navigation responds and public Binance data advances. No indicator, saved setting or history was discarded to obtain this result. |
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
| Native Windows regression suites | **Both x64 and ARM64 passed on the corrected runtime** | Revision `393bbb1`, [run 41613876096](https://github.com/mehmetcanbudak/trading-hub/actions/runs/41613876096): both native hosts completed every compatibility/safety/platform gate. The preceding `dd1d9b8` ARM64 partial-stop failure remains documented below; it was corrected in production code, not hidden by a retry, skipped assertion or longer wait. |
| Native Windows x64 and ARM64 desktop | **Both complete native jobs passed** | Run 41613876096 passed English/source/safety/UI, both architecture builds and each host's actual native install/open/capture/close/uninstall plus fixture-data hash preservation. Captures target only the owned fixture HWND. Installers remain unsigned review artifacts, not a published signed release. |
| Screenshot bitmap ownership | **61 owned captures corrected; complete native rerun passed** | Run 41498553556 faulted with `0xC0000005` in Skia's native PNG encoder during x64 theme capture. Every capture now retains its bitmap through encoding and disposes deterministically. All 17 local scenes plus three extra theme passes and the complete x64/ARM64/macOS run 41502632224 pass afterward, without disabling a renderer, scene or assertion. The precise native fault mechanism is not proven; this is observed non-recurrence plus an ownership correction, not a guarantee about all third-party native code. |
| Hosted macOS ARM64 | **Complete corrected-runtime job passed** | Revision `393bbb1`, run 41613876096: Release build, compatibility/safety regressions, English/source/catalog audits, all English scenes, seven Spanish/Chinese pages each and the real self-contained build/install/strict-verify step passed. Earlier runner-allocation and ARM64 test failures remain explicit historical evidence, not current release passes. |

Current local evidence is under `/Volumes/TradingHub-upstream-a861ab10ae/evidence/integration`; failing-before receipts are under the sibling `evidence/safety-before` directory. Automated-test artifacts are isolated. Personal-profile native captures stay privately local and are not published. These receipts are not a public app release; the workflow uploads only its own clean screenshots, reports, TRX results and Windows installers.

### Installation and native lifecycle follow-up — 2026-11-03

The real `./build-app.sh --install` path initially failed strict signature verification on `System.Xml.Linq.dll`. Its bytes matched the verified staged library, but the installation copy had removed `com.apple.cs.CodeDirectory`, `CodeRequirements` and `CodeSignature`. The installer now uses the metadata-preserving `ditto` copy already used for packaged output; no signature check is weakened. The macOS workflow now executes the real build/install/verify command so this consumer path is gated, not merely shell-parsed. A previous bundle and twelve state/script locations were privately backed up outside Git; market-history files and Keychain were not exported or reset.

Native tray isolation needs more than `HOME`: the real macOS known-folder getters still selected the personal Library/Documents paths in that probe. The accepted fixture pre-created standard Library/Documents folders under an isolated home, supplied both `HOME` and `CFFIXED_USER_HOME`, verified the actual `Product` getters, overrode the other stores including a fake Keychain, disabled the sleep guard and confirmed open state files were confined to the fixture. `TRADINGHUB_DATA_DIR` was intentionally absent only for this native tray fixture because the application otherwise disables tray installation in tools. No real-order control was used.

Release evidence is under `evidence/integration/release-install-session`: the signature diagnosis, normal-profile owned-window capture, native one-chart-to-two-chart failure, repeated corrected native cycles, packaged restart/quit receipt, full TRX and UI output remain distinct. The additional 166 scoped provider-recovery tests used fake exchanges; they do not constitute authenticated provider-demo testing. After the Binance website failed to reveal a newly created one-time secret, the user explicitly deferred authenticated demo testing and chose to finish installation without credentials. Physical sleep/lid checks are also deferred until port jobs finish. Neither authenticated execution nor physical-power certification is inferred from public API pings, source review or fixture tests.

### Windows ARM64 Rithmic publication follow-up — 2026-11-03

The installation-follow-up matrix passed macOS and Windows x64 but failed one Windows ARM64 safety assertion: after a partial stop fill, the broker's active-order view contained only the remaining manual limit instead of the acknowledged replacement stop. This was not a compiler warning and is not treated as a harmless retry.

Source inspection found that overlapping Rithmic callbacks captured the session order list before acquiring the broker gate and published it after releasing that gate. A controlled older callback, delayed at exactly that boundary, deterministically hid a subsequently acknowledged stop. The repair captures, applies and publishes the order view and position under the same existing gate, publishes the list reference with volatile semantics and invokes notifications only after releasing the gate. It does not change protocol fields, order IDs, quantity math, manual-order ownership or exchange-native protection limitations.

The controlled broker/protocol smoke failed before the repair and passed afterward; the complete local suite then passed 351 cases with 7 Windows-only skips. Receipts are `rithmic-publication-probe-red/stale-publication-red.trx`, `rithmic-publication-probe-green/stale-publication-green.trx` and `unit-after-rithmic-fix/rithmic-publication-fixed.trx` under `release-install-session`. The temporary scheduling hook, standalone probe and path/fingerprint console project have been removed. The existing partial-fill/manual-order cases retain every assertion. Corrected runtime `393bbb1b7feea0bae694021312e061fe4bf5a75b` subsequently passed the complete macOS ARM64, Windows x64 and Windows ARM64 jobs in [run 41613876096](https://github.com/mehmetcanbudak/trading-hub/actions/runs/41613876096); `hosted-rithmic-repair-result.json` records the exact head SHA and three successful conclusions.

### Normal-profile historical replay follow-up — 2026-11-03

The fully signed `1016fbf` app and its native MCP smoke passed, as did all three hosted jobs in run 41617507288, but its real normal-profile GUI then stalled before a content window appeared. A native sample measured an 11.4 GiB footprint with a 22.1 GiB peak. Managed stacks identified `SessionVolumeProfile.OnCalculate` inside `Indicator.HostRecalculate`, reached by the normal UI timer after history loading. The user authorized stopping only that newly launched stuck process; no port process was stopped and no saved configuration or history file was deleted or reset.

The draw-only profile cloned accumulated price levels, rebuilt a sorted dictionary and recomputed value area for every historical bar. The repair still commits each closed bar exactly once, but materializes drawing snapshots only at completed session/week boundaries and the current bar. All price aggregation, identifiers, period values, POC/value-area math, colors and rendering remain unchanged. It does not disable the indicator or discard saved user settings.

| Synthetic 1,024-bar replay | Observed managed allocation |
|---|---:|
| Before repair; permanent allocation regression fails | 110,129,120 bytes |
| After repair; isolated runtime smoke passes | 760,648 bytes |

The permanent regression enforces a generous 16 MiB UI-thread allocation budget rather than a machine-dependent elapsed-time threshold. Twelve isolated before/after state scenarios are identical across Session, Week and Visible modes, including price levels, bid/ask totals, POC/VAH/VAL, forming-bar corrections, daily/weekly rollover, repeated-update idempotence and live-versus-replay equivalence. `profile-replay/` holds the red/full-green TRX, semantic receipts and the private native startup diagnostic. Personal history is not a synthetic fixture. Native normal-profile rendering and the complete hosted matrix are explicit acceptance gates for this repair.

Corrected runtime `636366419f17ae09b1d10d4a67aea0cfc79d1c8f` passed all three native jobs in [run 41627826982](https://github.com/mehmetcanbudak/trading-hub/actions/runs/41627826982), then passed strict/deep signature verification after installation in `/Applications/Trading Hub.app`. All four protected startup files were byte-identical across replacement. The actual normal profile rendered its four chart tabs, custom delta indicator and retained session volume profile; safe Settings → Trading navigation responded, English settings remained intact and live public Binance data advanced. No order control was used. `profile-replay/normal-profile-acceptance.json` records the exact binaries, owned window and privately retained captures. The port agents received the exact reviewed runtime SHA without changing their branches or pins; Swift remains explicitly paused.

### Release gates still open

- Release approval must review the actual application captures and explicit safety requirements alongside the corrected runtime's successful native Windows x64/ARM64 and macOS matrix. The earlier ARM64 failure and controlled red/green correction remain visible above. Passing these gates, making `memoshi` canonical or installing it is not permission to route real orders or proof of authenticated-provider or physical-power behavior.
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

The [upstream sync guide](upstream-sync.md), [checkpoint ledger](../../.github/upstream-english.json) and [daily/manual monitor](../../.github/workflows/upstream-english-tracking.yml) track the original repository separately from `memoshi`. Consolidation advanced to `c28900e` after [all three native jobs at `617259d`](https://github.com/mehmetcanbudak/trading-hub/actions/runs/41506213481). Approved PR #5 advances canonical `memoshi` to reviewed upstream `1d5a774`; its documentation merge retains the runtime/build inputs already validated natively at `6363664`, while the additional fork runtime has the fresh local evidence and limits above. The canonical monitor reports **Up to date**, zero pending commits; tracker issue #2 was updated locally without hosted CI. Checkpoint acceptance does not certify fresh native Windows execution, brokerage behavior, power-loss durability or physical hardware. The historical C# audit baseline stays at `9ee74a4c527ac43120423acd41e006ec9d7877ad`.

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

The earlier CS0414 (`CumulativeDeltaCandles._base`) and CS0108 (`ChartTraderPanel.Template`) are fixed: dead state/its empty hook were removed, and the private template accessor is now `GetBracketTemplate`. Current full Release builds report zero warnings and errors; neither diagnostic is suppressed.

No real-money orders, broker credentials or privileged closed-lid operation are fixtures. The separately authorized no-tool Claude request hit the account's weekly limit and reported zero cost; successful model output remains unverified. Historical runtime `6363664` passed native Mac/x64/ARM64 jobs; this follow-up adds fresh local shared-runtime tests, three-language UI, signed Mac-package/native GUI/MCP evidence and both Windows cross-publishes, **not a fresh native Windows run**. Authenticated provider execution, physical sleep/lid/thermal behavior and selected additional extensions remain separate. Raw vendor/model/system responses and the temporary native GUI sandbox-extension warning are retained, not suppressed.

A language heuristic and passing tests cannot prove arbitrary runtime content is English or certify trading safety. Completion claims apply to the reviewed built-in surfaces and full English guides, with the explicit compatibility/content exclusions above.

## Reproduce

```bash
export PATH="$HOME/.dotnet:$PATH"
fixture="$(mktemp -d /tmp/tradinghub-english.XXXXXX)"
export TRADINGHUB_DATA_DIR="$fixture/data"
export TRADINGHUB_STRATEGIES_DIR="$fixture/strategies"
export TRADINGHUB_INDICATORS_DIR="$fixture/indicators"
export TRADINGHUB_BACKTESTS_DIR="$fixture/backtests"
export TRADINGHUB_LIVE_DIR="$fixture/live"
export TRADINGHUB_KEYCHAIN_FILE="$fixture/fake-keychain.json"
export TRADINGHUB_SLEEP_GUARD=off
dotnet build TradingHub.slnx -c Release
dotnet test tests/TradingHub.Tests -c Release
python3 tools/check_english_translation.py
dotnet run -c Release --project tools/TradingHub.UiSnapshot -- \
  "$fixture/screenshots" 2 \
  trading settings connections data strategies accounts live english \
  panel footprint mercado conexiones data+rithmic data+import trading+strategy theme safety
```

For a narrow-window check, use a fresh fixture with the same six isolation variables and disabled sleep guard, then run the seven main pages plus `english --size 1100x760`. For Spanish/Chinese page captures, use a fresh fixture for each language and set `TRADINGHUB_LANG=es` or `TRADINGHUB_LANG=zh-CN`. Scene identifiers such as `mercado` and `conexiones` remain stable test contracts.

Final follow-up evidence: `/tmp/tradinghub-samples-final.L3HCDr/screenshots` and `/tmp/tradinghub-samples-narrow.40OfLU/screenshots`. The original five-file migration checks/replays are in `/tmp/tradinghub-sample-migration.R7Ns8W/check.log`; the local sample backup is `~/Library/Application Support/Trading Hub Backups/english-samples-20261019-002254/`. Native installed-app captures are kept locally, not published with personal profile data. The rollback copy is under `~/Library/Application Support/Trading Hub Backups/english-20261018-231314/`. A new ad-hoc signature can require macOS Documents permission again; the user approved that prompt during this installation. Temporary evidence is not a permanent release artifact; CI uploads isolated screenshots and HTML reports when that workflow runs.
