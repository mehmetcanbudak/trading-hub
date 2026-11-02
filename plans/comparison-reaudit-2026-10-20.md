# Trading Hub / Errata — second-pass audit

Date: 2026-10-20 UTC. **Advisory only; no implementation or release approval.**

## Verdict

The earlier comparison was directionally useful but **not complete**. This pass found additional Trading Hub execution risks, corrected an over-simple reduce-only recommendation, and retired Errata's now-resolved dependency-policy finding.

Neither project can honestly be certified as flawless or universally state of the art. Trading Hub has the more integrated authoring/backtesting/execution workflow; Errata has the stronger recording/replay/provenance and read-only research boundaries. Capability breadth, code volume, test counts and implementation language do not establish correctness or superiority.

**Do not use these passing checks as approval for real-money Trading Hub strategies.** Resolve the execution findings below with deterministic broker-contract tests before claiming safe execution. Errata's next priority is current-source runtime and release acceptance, not indiscriminate feature expansion.

## Scope and source authority

- Trading Hub: `5f7c19e7528f47582f34f14d2c06e71d388b9858`, branch `feat/english-localization`.
- Errata: inspected implementation at `3c1821b19c955237633b8045cf4bbec46faee081`; advanced concurrently to `f967a0a2a0a4729a48abc6b98fb3dc93b027254d`. That intervening commit changes only three generated security inventory JSON files. Its actual inventory freshness gate was then rerun successfully.
- Both trees started clean. Errata's inventory files became dirty and were committed during the audit; they were not edited or reverted by this audit.
- This pass rechecked prior findings and their callers, the intervening changes, test coverage, current project authorities, relevant external contracts, and all nine audit categories: correctness, security, performance, testing, architecture, dependencies, tooling, documentation and direction.
- It is a critical-path and change-focused re-audit, **not a line-by-line proof of the entire repositories**, owned forks or dependencies. No independent reviewer/subagent was used.
- No source, test, configuration or dependency changes; no installs, product-data access, credentials, live orders, paid model calls, commits or pushes. This report is the only repository artifact added by the audit.

Trading Hub authority: [`TRANSLATION_STATUS.md`](../docs/en/TRANSLATION_STATUS.md), [`upstream-sync.md`](../docs/en/upstream-sync.md), and [upstream ledger](../.github/upstream-english.json). Errata authority: [`AGENTS.md`](../../Errata/AGENTS.md), `QUANT_CONTEXT.md`, ADRs, and [`plans/CURRENT_WORK.md`](../../Errata/plans/CURRENT_WORK.md). Errata's separate Agent Control platform remains planned, not implemented. Its deliberate lack of order execution is not a defect.

## Corrections to the previous report

| Earlier statement or implication | Corrected assessment |
|---|---|
| Errata root/desktop dependency policy fails on zbus-family advisories | **Resolved in the inspected source.** All six product/contract graphs now pass offline/locked policy checks and actual-metadata private-crate licence checks. The new OS-store/owned-parent work must still retain its platform acceptance boundaries. |
| Trading Hub has 221 regression cases | Fresh run: **222**, plus **13** independent upstream-tracker tests. |
| Passing incremental build reported zero warnings | This fresh build reports **two existing warnings**, CS0414 and CS0108, zero errors. |
| Route protection through `Exit()` as the fix | Necessary where supported, but **not sufficient**: Rithmic/Tradovate flags are local/emulated, Binance has position-mode restrictions, and the current API maps non-stop exits to limits. Market close needs its own correct contract. |
| Centralize real-order permission at broker acquisition | Acquisition alone is insufficient: cached brokers and permission revocation require enforcement at exposure-increasing submission too. Cancellation and supported reducing exits must remain usable. |
| Binance order routing being present implies complete strategy support | It does not: original submitted orders are not reconciled by the polling implementation, and ambiguous submission outcomes are treated as rejection. These were missed in the first pass. |
| Statistical method names establish stronger quantitative validity | Trading Hub has broader built-in named anti-overfitting methods in the reviewed scope. Their presence is not independent mathematical validation, calibrated fill accuracy or evidence of profitable strategies. |
| Older Errata performance figures represent current application speed | They are historical, source-bound diagnostics. Recent ledger measurements still miss targets, and the latest provenance correction requires its own native runtime evidence. No controlled Trading Hub-versus-Errata benchmark was run. |
| Latest Errata hosted failure was the October 15 run | New October 20 runs are also blocked by billing/spending restrictions. The inspected latest run is `39417051273`; this is not evidence of failing product tests. |

The six earlier Trading Hub source risks remain present. The key files have no changes relative to the previous audit's `7f37245` baseline; these are pre-existing behavior, not demonstrated localization regressions.

## Vetted execution and reliability findings

Priority P0 here means **block real-money safety claims**, not a CVSS score. Confidence applies to the observed source mechanism; no finding was reproduced on a live broker. Effort includes characterization and regressions: S = hours, M = roughly a day, L = multi-day. Adapter work is not a one-line fix.

| ID / priority | Finding and concrete consequence | Evidence | Effort / fix risk / confidence |
|---|---|---|---|
| TH-01 / P0 / new | **Binance fills do not update strategy-tracked orders.** `Send` returns an `Order` whose success path sets only its exchange ID. Polling replaces `_orders` with new objects, only from open orders. `LiveStrategyRunner.SyncOrders` reads its original `so.Live` object. A completed order disappearing from the open-order list cannot supply a terminal fill to that object; strategy ledger/protection can remain unaware of execution. | `src/TradingHub.Core/Feeds/Binance/BinanceAccount.cs:245–281,302–321`; `src/TradingHub.Strategies/LiveStrategyRunner.cs:571–620` | **L / HIGH / HIGH** |
| TH-02 / P0 / confirmed | **Real-order permission is only applied at selected entry points.** `AccountsFor` filters UI choices, but `AccountFor` returns cached/constructed real brokers without the same check; restored account/strategy IDs reach it. Turning permission off is not a submission boundary. | `src/TradingHub.App/Services/AppServices.cs:496–561`; `src/TradingHub.App/Services/LiveCenter.cs:407,543–555`; `src/TradingHub.App/Views/TradingView.cs:644` | **M–L / HIGH / HIGH** |
| TH-03 / P0 / new | **Unknown submission outcomes become “Rejected.”** All HTTP failures become a generic rejection exception; the broker also marks transport/timeouts as `Failed/Rejected`. The request has no application-generated client order identifier and no single-order reconciliation path. A response can be lost after acceptance, so failure to receive it is not proof that no order exists. | `src/TradingHub.Core/Feeds/Binance/BinanceAccount.cs:76–101,158–178,302–321`; `src/TradingHub.Strategies/LiveStrategyRunner.cs:601–607` | **L / HIGH / HIGH** |
| TH-04 / P0 / confirmed, remedy corrected | **Live strategy protection bypasses the reducing exit path.** `IsProtective` is local strategy metadata; `Place` still calls ordinary `Market/Limit/Stop`. A late or overlapping protective fill can increase/reverse exposure where not constrained by the broker. Local emulated OCO is not atomic exchange OCO. | `src/TradingHub.Strategies/LiveStrategyRunner.cs:665–710,740–760`; `src/TradingHub.Core/Trading/ITradingAccount.cs:39–65`; `src/TradingHub.Core/Feeds/Binance/BinanceAccount.cs:330–331`; `src/TradingHub.Core/Feeds/Rithmic/RithmicBroker.cs:137–190`; `src/TradingHub.Core/Feeds/Tradovate/TradovateTrading.cs:518–569` | **L / HIGH / HIGH** |
| TH-05 / P0 / confirmed | **Binance replacement races cancellation.** `Replace` starts `Cancel` and immediately submits a replacement. Cancellation returns without action if the original exchange ID has not arrived, and cancellation failures do not prevent replacement. `Modify` discards the replacement object, compounding tracking problems. | `src/TradingHub.Core/Feeds/Binance/BinanceAccount.cs:334–364`; `src/TradingHub.Strategies/LiveStrategyRunner.cs:729–735`; `src/TradingHub.Core/Trading/Brackets.cs:263,324` | **L / HIGH / HIGH** |
| TH-06 / P0 compatibility check / new | **Futures conditional-order integration does not follow the current documented Algo route.** The app posts `STOP_MARKET` with `stopPrice` to `/fapi/v1/order` and only polls/cancels ordinary orders. Binance's current official SDK says to use `/fapi/v1/algoOrder` for conditional TP/SL orders, with distinct identifiers, fields and lifecycle operations. Verify and migrate the complete contract, not just the POST URL. | `src/TradingHub.Core/Feeds/Binance/BinanceAccount.cs:148–187`; version-pinned official SDK under External references below | **L / HIGH / HIGH route mismatch; MED production failure consequence** |
| TH-07 / P1 / confirmed | **Recorder I/O failure and shutdown ownership are unsafe.** The background thread catches only `InvalidOperationException`; open/write/flush/dispose can raise I/O errors. `Dispose` releases the instrument lock after a two-second join without proving the writer terminated. | `src/TradingHub.Core/Data/InstrumentData.cs:490–557`; `src/TradingHub.Core/Data/TickFile.cs` writer; .NET threading reference below | **M / MED–HIGH / HIGH** |
| TH-08 / P1 / confirmed | **Unreadable connection state may be overwritten with defaults.** Load errors leave an empty profile list; the constructor seeds and saves defaults over the same file. Saves write directly to the destination. | `src/TradingHub.App/Services/Connections.cs:197–208,222–241` | **S–M / MED / HIGH** |
| TH-09 / P1 / confirmed structural limit | **Session retention and recording backlog are unbounded.** Every accepted live trade is retained in growing arrays, released instruments remain running/retained until shutdown, and the recorder collection has no capacity. This is a long-session/resource risk, not a measured leak or current OOM reproduction. | `src/TradingHub.Core/Data/TradeStore.cs:33–49,92`; `src/TradingHub.Core/Data/InstrumentData.cs:472–478,494,584–587` | **M–L / HIGH / HIGH structure; load consequence unmeasured** |

### Smallest complete remedy boundaries

- **TH-01/03/05/06:** start with a local fake Binance account API/stream characterization suite. Preserve stable order identity, cumulative execution accounting, final-state lookup, and explicit uncertain outcomes. Reconcile before resubmission/replacement; preserve cancel/reduce operations. Cover acceptance with lost response, immediate fill before first poll, partial fill, late cancellation, rejection, restart and current conditional-order lifecycle. Do not infer exactly-once execution from HTTP retries. In the scoped `src/tests/tools` caller search, the only Binance account-client test found was HMAC signing (`tests/TradingHub.Tests/BinanceSignatureTests.cs`), not these lifecycle scenarios.
- **TH-02:** one enforceable policy shared by manual trading, restored charts, new/resumed strategies and cached brokers. Tests must prove permission revocation blocks increases without blocking cancellation or supported reductions. Do not use a new UI confirmation as the only enforcement.
- **TH-04:** document capabilities per adapter and account mode. Prove reduce-only behavior where the venue supports it; explicitly characterize emulated protection elsewhere. A `ReduceOnly` property set after transmission cannot change the already-submitted broker payload. Do not route a market close through an implementation that turns it into a limit order.
- **TH-07:** expose terminal recorder failure, contain I/O exceptions without pretending recording continues, and retain ownership until the worker has stopped. Test read-only paths/disk failures, queued shutdown and competing writers using temporary data only.
- **TH-08:** distinguish missing, empty and unreadable state; preserve the original on failure and use an interruption-safe replace/recovery scheme. Test truncated JSON, permission errors and interrupted writes.
- **TH-09:** establish budgets and observability before changing retention. Silent tick dropping or evicting strategy-required history would be an unacceptable “performance fix.”

These are remedy sketches, not approved implementation plans. Test characterization must precede the execution-state changes; those changes must precede new real-money safety claims.

## Errata: resolved work versus still-open acceptance

### Verified progress

- Root, standalone shell and standalone desktop dependency graphs now pass, along with the three isolated Metal/wgpu/D-Bus contract graphs; supplemental checks include unpublished local crates that ordinary cargo-deny can skip.
- New Linux credential code preserves primary identity, ambiguity refusal and legacy lookup; no plain-session fallback was found in the reviewed path (`crates/secret-store/src/keychain/linux.rs`). Platform-service execution is separately evidenced by the ledger, not rerun here.
- Concurrent Binance auxiliary reads now use bounded channels, cancellation identities and owned limiter reservations. HTTP cleanup releases reservation ownership on error/unwind; optional requests do not run under the limiter mutex (`crates/market-service/src/binance_usdm_live.rs:208–378,595–678`, `limiter.rs:1084–1123`). This is improvement, not proof that every venue's pacing is correct.
- Cold-seed/history publication has new bounded result handling and held-endpoint tests. The latest source-provenance fix shares verification between desktop and observability build scripts. Its actual compiled-consumer regression was included in the passing 21-case source-bundle suite rerun here.
- Both owned GPUI/component patch trains reproduce their recorded bytes; current SBOM/notice freshness passes. Owned ports still impose maintenance obligations.

### Remaining, not silently waived

`Errata/plans/CURRENT_WORK.md` is explicit about these obligations:

1. **Fresh current-source native observation.** The new provenance correction has compiled regression evidence, but historical application smokes cannot be relabelled as current binary acceptance.
2. **Unmet latency budgets.** The ledger's last `ad93a403` native probe reported a 37.453 ms whole-frame upper-bound p99 and refused comparability because its harness revision was `unknown`. Its 1,999.586 ms first-depth-render metric is not first usable UI or first photons. Earlier comparable-scope diagnostics also did not establish the 8.3 ms p99 / one-second first-usable goals. The corrected binary needs new measurement; no claim of current speed is made here.
3. **Long-duration evidence:** separate uninterrupted seven-day live and synthetic recorder runs, plus a 24-hour native run. No acceptance clock is running. Interrupted receipts cannot be spliced into a pass.
4. **Replay acceptance:** preserved source/selection/cursor/hash fidelity is valuable, but the ledger's 255.906× versus requested 256× GUI run and sub-50,000× pipeline results are not passing their strict requested-rate claims.
5. **Human/physical acceptance:** audible VoiceOver, IME composition, perceptual audio, notifications, real cross-window drag, supported Intel macOS / Windows / Linux hardware and minimum-host thermal/power behavior.
6. **Production release:** signing/notarization, key custody, actual packages/install/update/rollback and physical failure durability. Mock-signing tests are not signed release evidence.
7. **Hosted verification:** billing/spending restrictions still prevent current-source hosted jobs from running. This requires operator/account action, not weakening CI or calling local results hosted passes.
8. **Conditional data/provider scope:** unavailable feeds, lawful historical coverage, Hyperliquid L4 and paid/provider credentials must retain their stated prerequisites. Explicit proprietary/vendor exclusions and the unbuilt agent platform are not newly discovered defects.

Prior recorded evidence is substantial: the ledger reports 4,114 root cases and 1,927 normal desktop cases. I inspected the desktop target-complete receipt (225 integration targets, source-linked initial/continuation traversals, 33 ignores and zero failures); I did not rerun those full Rust matrices. Extra local groups overlap normal targets and must not be added as independent coverage. The receipt's doctest harness has zero cases.

## What “state of the art” should mean here

These are concrete quality criteria, not a marketing score or a claim that all are currently satisfied.

| Dimension | Useful acceptance criterion | Current comparison / gap |
|---|---|---|
| Product scope | Clear supported contract and explicit exclusions | Preserve execution workstation versus read-only terminal; do not merge their authority models accidentally. |
| Execution correctness | Identity-preserving order state, actual fills, uncertain-outcome reconciliation, position-mode-aware reduction and revocation tests | Trading Hub has execution functionality but the findings above block a strong safety claim. Errata intentionally does not execute. |
| Market integrity | Detect stale/gapped books; retain provenance, timestamps, instrument revisions and replay equivalence | Errata has stronger explicit contracts; neither test fixtures nor decoder presence certify live provider access. |
| Persistence/recovery | Preserve unreadable originals; test crash, truncation, disk-full, contention and restart | Trading Hub has concrete gaps; Errata's physical power-loss/update acceptance remains open. |
| Quantitative validity | Independent numerical fixtures, controlled out-of-sample selection, complete trial accounting and calibrated fill assumptions | Method breadth is useful but not validation. Errata has richer recorded-book/provenance machinery; Trading Hub has more integrated named optimization/anti-overfitting tools. |
| Latency | Same inputs, hardware, pane layout and build profile; positive displayed-frame evidence; p95/p99 and first-usable definitions | No controlled comparison was run. Headless draw timing is not whole-frame latency, and Rust is not a performance result. |
| Resource efficiency | Bounded queues/retention; long-duration RSS, allocation and power evidence | Trading Hub has unbounded lifetime structures; Errata's stated duration/host acceptance is incomplete. |
| Security and extensions | Trust boundaries enforced by code; credential hygiene; explicit trusted versus confined extension models | Trusted C# is intentionally unsandboxed. Errata's Rhai/research/assistant boundaries are stronger but not universal sandbox or penetration-test certification. |
| AI integration | Deterministic authority/approval checks and provenance, not only prompt wording or tool annotations | Preserve Errata's approved chart proposals and trusted-server boundary. MCP hints alone do not establish safety. Do not let generated strategies inherit implicit trading approval. |
| Accessibility/UX | Keyboard and screen-reader tasks, visible focus, contrast/zoom, non-drag alternatives, real human input tests | Two fresh Trading Hub screenshots inspected; no global visual/accessibility certification. Errata's automation and AccessKit tests do not replace human acceptance. WCAG applies directly to web surfaces, not as automatic certification of a native app. |
| Supply chain/release | Locked graphs, maintained parents, scoped exceptions, inventories, signed artifacts and update recovery | Current checks improve confidence but do not establish absence of vulnerabilities. Errata now passes its policy; Trading Hub's NuGet sources reported no vulnerable packages. |
| Maintainability | Characterization before risky seams/refactors; ownership of patches and release gates | Errata's large desktop modules remain maintenance hotspots; wholesale splitting/rewrite is not automatically beneficial. Trading Hub's small graph does not compensate for untested broker contracts. |
| Localization/compatibility | English built-in surfaces while preserving saved identities and user code | Trading Hub's audit/UI gates pass; existing arbitrary user/provider/system text is outside those claims. Its upstream tracker is notification-only, not automatic integration. |
| Licensing/data rights | Respect source, extension and market-data obligations independently | Trading Hub is MIT plus Commons Clause, not OSI open source. Porting/copying it to Rust does not erase the condition; Errata's first-party MIT/Apache terms do not override copied-code or market-data restrictions. |

The minimal high-value direction remains: characterize and repair execution safety in Trading Hub; close Errata's measured acceptance gaps; evaluate reuse with a licence-aware parity map only after deciding the intended product. No new abstraction, framework migration, execution subsystem in Errata or whole-app Rust rewrite is justified by “SOTA” alone.

## Fresh checks actually run

| Project / check | Observed result and boundary |
|---|---|
| Trading Hub `dotnet build TradingHub.slnx -c Release --no-restore` | Pass, zero errors, two existing warnings: CS0414 `Basic.cs:285`, CS0108 `ChartTrader.cs:263`. Incremental/cache-backed, not a clean restore build. |
| `dotnet test tests/TradingHub.Tests -c Release --no-build --no-restore` | **222 passed, zero failures/skips**, after the build. |
| `python3 -m unittest discover -s tools/tests -p 'test_*.py' -v` | **13 passed**, temporary Git histories and fake issue API. No live notification writes. |
| `python3 -B tools/check_english_translation.py` | Pass: 184 files, 11,531 tokens, 129 documented exceptions; 22 docs, 173 local links/anchors, 16 guide-section checks. |
| All 16 isolated UI scenarios | Exit 0, **120 English checks**, 65 PNGs. Fake providers/secrets, four temporary data roots, sleep integration disabled. Generated all captures; visually inspected `trading.png` and `strategies.png`, not all 65. |
| `dotnet list TradingHub.slnx package --vulnerable --include-transitive --no-restore` | No vulnerable packages reported by configured NuGet source. Not a source-code security proof. |
| `zsh -n build-app.sh` | Pass; no installation. |
| Errata `scripts/test-*.py` + `python/tests/test_*.py` | **190 passed** across 21 entrypoints: 100 tooling and 90 SDK cases, existing Python 3.13.3 environment. Includes source-label compiled consumer. No package installation. |
| Errata formatting | Exact documented gates pass: root `cargo fmt --all --check`, shell and desktop `cargo fmt --check`. |
| Errata six dependency graphs | `cargo deny --locked --offline ... check`: all six pass. Smaller graphs retain unmatched-policy/exception warnings, not zero-warning claims. |
| Errata six private-licence graphs | Actual `cargo metadata --locked --offline --all-features` piped into `scripts/private-licences.py`, all pass under their existing policies. |
| Errata `scripts/check-readme-data.py` | Pass, 22 examples accepted by the parser. |
| Errata fork verification | `scripts/init-gpui-fork.sh --verify` and `scripts/init-gpui-component.sh --verify` pass: 18 and 3 patches respectively. No initialization or mutation. |
| Errata actual inventory freshness | `CARGO_NET_OFFLINE=true scripts/check-sbom-fresh.sh` passes after concurrent `f967a0a2` inventory commit. |
| Both `git diff --check` | Pass before adding this report. |
| Trading Hub hosted CI | Run [39239369636](https://github.com/mehmetcanbudak/trading-hub/actions/runs/39239369636) succeeds at exact `5f7c19e`, macOS and Windows-shared-runtime jobs. |
| Errata hosted CI | Run [39417051273](https://github.com/mehmetcanbudak/Errata/actions/runs/39417051273) did not start jobs due to billing/spending restrictions; older/different source, not a result for current local HEAD. |

An **initial overbroad check** used `cargo fmt --all --check` in the standalone shell/desktop and failed on formatting in the pinned GPUI dependency tree. The documented standalone checks do not use `--all`; both then passed. This was an audit-command scope discrepancy, not silently repaired source or a claimed pass for the broader sweep. Fork bytes were subsequently verified unchanged.

The offline RustSec database was `b19b2c83c2493961ccb12f9fc4699a00fb2c6c44`, dated `2026-10-20T13:02:37+02:00`; no database/network refresh was represented as performed. Full Rust builds/tests/Clippy were not rerun: the host had about 17 GiB free initially and 14 GiB near completion, with a retained 10 GiB reserve and previously retired debug caches. Cheap tests used temporary fixtures; no other caches or data were deleted.

UI artifacts: `/tmp/tradinghub-reaudit-ui.ytiowdzi/screenshots`. Build/test isolation root: `/tmp/tradinghub-reaudit.jnmwXX`. Temporary artifacts are not permanent release evidence.

## External references and evidence limits

1. **Binance official USD-M SDK**, immutable revision `a54ec0b312c36706dc1d0b2cb33c658d55b72e89`: [trade API](https://github.com/binance/binance-connector-python/blob/a54ec0b312c36706dc1d0b2cb33c658d55b72e89/clients/derivatives_trading_usds_futures/src/binance_sdk_derivatives_trading_usds_futures/rest_api/api/trade_api.py). `new_algo_order` explicitly directs TP/SL/conditional orders to `/fapi/v1/algoOrder`; it documents `reduce_only` as incompatible with Hedge Mode and `closePosition=true`. The generated enum still lists conditional types for both entrypoints, so **do not treat the enum as proof the legacy route works or claim a particular server rejection code was reproduced**. The direct Binance documentation/announcement fetches returned empty HTTP 202 responses; a public article endpoint returned 404. Search summaries suggested the migration but were not treated as a reproduced venue response. TH-06 is a current-contract compatibility finding pending provider characterization.
2. **Microsoft .NET:** [Exceptions in managed threads](https://learn.microsoft.com/en-us/dotnet/standard/threading/exceptions-in-managed-threads), retrieved primary text: most unhandled thread exceptions terminate the application. This supports the recorder failure mechanism, not a claim that a disk-failure experiment was run.
3. **MCP specification:** [Tools, 2025-06-18](https://modelcontextprotocol.io/specification/2025-06-18/server/tools), retrieved primary text: validate inputs/access, sensitive-operation confirmation, and distrust tool annotations unless the server is trusted. A trusted bundled subprocess is different from an arbitrary untrusted remote server.
4. **W3C WCAG 2.2:** [Dragging movements](https://www.w3.org/WAI/WCAG22/Understanding/dragging-movements.html), retrieved primary text: keyboard accessibility and a single-pointer non-drag alternative are separate requirements. Relevant directly to the browser surface and as a useful native usability criterion, not a native compliance verdict.

Context7 was attempted for .NET documentation but refused requests because its monthly quota was exhausted; direct primary documentation was used instead. No search settings, authentication profiles or network protections were changed. No commercial-terminal benchmark or universal SOTA ranking was performed.

## Considered and rejected / not established

- Old zbus-policy failures: fixed and rechecked, not carried as current vulnerabilities.
- Intentional no-execution boundary in Errata: by design, not missing trading functionality to add without authorization.
- Ordinary trusted C# execution, configured proxies and a trusted bundled MCP server: not newly demonstrated vulnerabilities merely because they have authority.
- Missing explicit `AssemblyLoadContext.Unload()` alone: not proof of a leak.
- Test-only localhost TLS private key noted previously: not a newly established production credential leak. No fresh Git-history secrets audit was performed.
- Missing hardware/human/paid-provider results: explicit acceptance gaps, not manufactured evidence that implementations are broken.
- Full rewrite/refactor, new plugin framework or generic abstraction: not justified before higher-risk behavior is characterized.

## Rechecked Trading Hub source digests

SHA-256 values bind the central static findings, independently of changing documentation:

```text
3c30cd4291c27c90a559b0d0ea3df2be9a43e47bb59b0041832a8bfa8da32f29  src/TradingHub.Core/Feeds/Binance/BinanceAccount.cs
32400458cc330b59ee46ba3dc02463f42a7ab6964a6bc139b78c56518c52eece  src/TradingHub.Strategies/LiveStrategyRunner.cs
431c9c8a0b4ff32f70472946914b3f850c4fe7123949f0b9d8d8f9763a6317d6  src/TradingHub.App/Services/AppServices.cs
ca392023d976b9a6b6757df8edf69399cc6ab86c77349c7a483a482a1850498f  src/TradingHub.App/Services/Connections.cs
40a1ac72161bc4886c8b39a33b697b475659a8dcfd14b1508212d104e5828088  src/TradingHub.Core/Data/InstrumentData.cs
```

Not checked: every source line/dependency, full current Rust matrices, live brokerage or paid providers, current Errata native UI, controlled comparative performance, human/physical or sustained-runtime release acceptance.
Risk: these are verified audit observations and bounded tests, not trading-safety, security, accessibility or release certification.
