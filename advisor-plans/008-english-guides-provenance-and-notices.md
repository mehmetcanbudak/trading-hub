# Plan 008: Document verified non-KVP capabilities and dependency notices

## Execution contract

- Planned at base `17dec28f0edbe4f1204b9c9a5d99ea1a6840342c`; prepared 2026-11-04 for the same uncommitted integration on `/Volumes/TradingHub-upstream-a861ab10ae/non-kvp-adoption-17dec28-streamed`, branch `feat/non-kvp-adoption`.
- **Dispatch prerequisite:** parent must first observe a successful combined build and actual fake-provider UI/CLI smoke. Do not execute while the current source freeze/build is running. Parent supplies exact receipts and current reviewed source paths at dispatch.
- Modify ONLY the execution worktree, never canonical `/Users/mehmetcanbudak/Projects/trading-hub`, installed app, other worktrees, user data, ports or Git refs. No commits/merges/push/install. GPT6.1Sol inherited; no nested agents.
- Read repo dashboard/sync rules. English canonical; persistent IDs/VTK1/paths/default workspace/orders/ledgers/ACK/brackets/quit protections remain unchanged. KVP, Intel, live accounts/providers/model/OAuth and CI dispatch remain excluded.
- Use specialized read/glob/grep/edit/write; C# LSP absent, targeted reference search fallback; no source-text shell edits. Context7 quota unavailable: primary pinned source/docs only, no retry/key request.
- No builds/tests/lint/formatters during the editing wave. Prebuilt I18n extraction/merge programs may run with all six isolated stores/fake Keychain/sleep guard off in parent-owned external fixture locations; never implicit `dotnet run` builds. Parent reruns gates after both documentation/catalog owners finish.
- Evidence only: do not claim runtime/native/provider passes from source review, do not overwrite old failure history, do not claim installed application contains this unmerged work. Parent maintains advisor-plans index; no speculative docs or unrelated cleanup.

## Why and current state

The existing guides still describe IB as connectivity-only and strategy chat as Claude-only. `docs/en/conexiones.md:38` currently says `Interactive Brokers | Connectivity probe only`; lines127–129 say no data/orders. `datos.md:16` says synthetic data is not actual execution; preserve that invariant. `graficos.md:27` describes instrument search/explicit Enter and79–83 describes focal zoom/pan. `indicadores.md:20` documents existing Session volume profile, which remains unchanged. `asistente-claude.md:25–30` documents old chat process permissions; the new two-engine and research route need exact updated semantics, not stronger sandbox claims.

`TRANSLATION_STATUS.md` header describes canonical memoshi and installed c27e5aa; keep those historical/installed facts. Add an explicitly unmerged/not-installed feature-worktree section with observed receipts, not a false canonical runtime SHA. The historical audit baseline9ee74a4 and rolling upstream integrated checkpoint are separate and unchanged.

## Scope

Own relevant sections of README.md, docs/en/{README,conexiones,datos,graficos,indicadores,estrategias,asistente-claude,privacidad,desarrollo,backtesting,TRANSLATION_STATUS}.md; existing docs/research/fork-feature-report.md copied into execution worktree from canonical read-only report if absent; new required THIRD_PARTY_NOTICES.md and bundled license text(s); App csproj CONTENT-COPY entries only after TV owner releases ownership. No business logic, tests, locale catalogs, source review JSON/checker, upstream ledger or version bump. Catalog/Audit owner handles those other files. Preserve links/section completeness and existing order safety explanations.

## Required accurate documentation

- Search: only enabled other market-data profiles when current nonempty query has no result/nonfavorites; incomplete failures visible; switch browse profile only, explicit Picked selection; no account/order change.
- Zoom: minimum0.1; dense per-pixel OHLC/close-extrema rendering and deliberate final-visible color policy; analytic bars untouched; normal focal/live/pan behavior preserved. Cite actual observed50k scene evidence only if receipt passes.
- New optional native names/options:13MAs/ribbon/Camarilla; all3flow/refill/composite studies and OpenInterest. Existing MA/SessionVolumeProfile stay available unchanged. IsHot vs warmup display; genuine0/negative vs missing; replay/latest replacement; previous OBSERVED session/partial-first history/CME/customtimezone rules.
- Flow: explicitUTC, retrospective CLOSED-confirmed pivots, preceding-volume spikes, abs-price notional, optional estimated OHLC fallback OFF bydefault, any observed footprint wins but mixed contribution incomplete; refill heuristic is NOT book/iceberg proof. Month/composite uses session boundaries; deterministic rounding/ties and finite60/10POC limits. IB live sides inferred; observed rows don't imply exchange-authoritative aggression.
- OI: Binance USDM Futures only, server observation time, historical5m/max500/latest30days, no lookahead, missing/zero distinct,10minute maxage, close-exclusive time cutoff/currentclock clamp, actual non-time lasttrade, range bounds/adjacent-bar change semantics; observation envelope NOT continuous coverage. No syntheticOI/fallbacksymbol. Recorder busy warning, retained writer lease/drain, no claim of stronger market/ledger durability.
- IB: dataONLY; wire187 negotiatedexclusively,8indexroots, TWS/Gateway/clientID/entitlement prerequisites;15second same-instrument tick requests; inferredside/depthfreshBBO/reconnect/NY18/calendarroll/no priceadjustment. Explicit offline no network, stableprofile/scope/history refresh. Defaultpreserve-real CSV minute fill; explicitcovered-minute replacement/backups/leases/revalidated immutablepreview; separateib-bars synthetic storage, real-minute precedence; multifileNOTatomic, backups/journalrecovery and no automaticpruning. Actual CLI syntax/errors/options from implemented IbImportCommand, not guessed flags. No IBexecution or misleading eligibility.
- Document the exercised IB consumer guarantees, not just labels: actual NY18 fresh-form defaults preserve saved/custom sessions; synthetic imported bars remain non-observed through strategy backtests/footprint aggregation; removed profiles retire retained transports; active-writer committed-prefix reads do not weaken strict import validation or hide quiescent corruption.
- Strategy chat: Claude default, optional own Codex CLI session, engine-bound histories/exactresume/custommodel/effort, missingCLI/mandatoryisolation failures, cancellation, nullableUSD/tokenusage. Riskagent remainsClaude-only. Stable chats/geometry/LabSource=claude IDs retained. Six store/runtime env variables reach local childserver processes. Lab-only command override; research sealed appentry. Existing16Lab tools/proposals doNOTapply trades.
- TradingView: offdefault; Essential+excludestrials; explicit appownedOAuth, dedicatedOSsecretentries, metadata-onlypaginateddiscovery/review/fingerprints,19researchintersection, ALLwatchlists/alerts includingmislabelledget_active_watchlist denied; schema/grantchange/rereview/revocation enforcedbeforecall. CLI sandbox network restrictions doNOTconstrainremoteMCP or in-processC#. No remoteaccount interoperability certification from fake tests. Explain new external requests/storage/privacy without collectingcredentials.

## Source and legal facts

PANASV sourcepin cdb84ea16eee7ac8d6c1b74136d2eeacedadf834; jjuangutierrez pin8ceaf4c43e50edb29e96bfc17ae5c8f19b7e17d9, MIT+CommonsClause attribution retained. NoKVP import. TradeDock alreadyinherited; Dham runtime imports/Intel unselected.

Exact NuGet nuspec authority:
https://api.nuget.org/v3-flatcontainer/quantalib/0.8.12/quantalib.nuspec — Apache2.0, copyrightMihaKralj, source d1a5d96d2ed912604c32f8dcaca896720d33f1b7, native net10/no package dependencies.
https://api.nuget.org/v3-flatcontainer/modelcontextprotocol.core/2.3.1/modelcontextprotocol.core.nuspec — Apache2.0, copyright © Model Context Protocol a Series of LF Projects, LLC.; source61420937cbef3eab895cafc8a89418811fb71f54; net10transitives Microsoft.Extensions.AI.Abstractions10.11.0/Logging.Abstractions10.0.13. Inspect actual restored new transitive notices/LICENSE/NOTICE and retain applicable copyright/license notices. Full Apache terms at https://www.apache.org/licenses/LICENSE-2.0.txt section4 requires recipients get license/NOTICE wherepresent. Include correct license text/notices in source AND build/publish output via minimal Appcsproj ContentCopy entries; no install/build-app redesign. Combined app still MIT+CommonsClause/sourceavailable, not OSI-approvedopen-source claim.

Observed restored `App/obj/project.assets.json` resolves exactly these new families: QuanTAlib0.8.12; ModelContextProtocol.Core2.3.1; Microsoft.Extensions.AI.Abstractions10.11.0; Microsoft.Extensions.Logging.Abstractions10.0.13; Microsoft.Extensions.DependencyInjection.Abstractions10.0.13. The DI abstraction is an additional Logging transitive and must not be omitted from notices.

- All three Microsoft nuspecs specify MIT and `© Microsoft Corporation. All rights reserved.` AI sourcepin: dotnet/extensions `25f07afa51034e37b334210642d7dc5f191c142d`; Logging/DI sourcepin: dotnet/dotnet `4099f83977f4d29604a9aeb971fb0a7bc1d5f699`.
- Restored Logging and DI packages both include `THIRD-PARTY-NOTICES.TXT`, 76.2KB/1,418 lines, with identical SHA256 `6d15e10a101c6bfff2ab4429ed061bf76c456fc4b23ad6b03e0d0f8377148a21`. One verbatim shared copy may cover both if the notice index explicitly identifies both packages; preserve the full applicable text, not an invented summary.
- Package roots are under the parent evidence root's `nuget-packages/<lowercase-package>/<version>/`. QuanTAlib, MCP Core and AI root inventories contain no standalone LICENSE/NOTICE file; retain their declared license/copyright and inspect pinned upstream source for any applicable NOTICE rather than assuming the NuGet root inventory settles source notice duties.

## Evidence, done and STOP

Parent provides exact build/test/UI/CLI paths/results. Record only observed selected counts/warnings/errors/limits; keep first failures and fixes visible. No personal fixture/captures/secrets published. New paragraph in report distinguishes adopted/notselected/deferredKVP and unmerged/notinstalled state. Update guide crosslinks and development commands/fixture overrides/newscenes; no realquota retry recommendation.

Done after parent docs/source/catalog checks and relevant scenes pass; parent must separately verify bundled notice files actually appear in output/publish. STOP if receipts missing, docs imply native/realbroker guarantees not exercised, source semantics conflict with claims, or scope requires businesslogic/locale edits. Message other owner for crossfile coordination.

## Documentation handoff

The executor handed off the scoped English guides, execution-only fork-report supplement, third-party notice index, full pinned license/notice files and App content-copy declarations. No checks ran during editing. Documentation is frozen pending final parent receipts; the dashboard retains the unmerged/not-installed state, initial failures and the two unresolved durable-ledger writer-checkpoint timeouts. Build/publish notice-copy proof, corrected visuals, subprocess/cancellation, complete tests and localization/safety gates remain pending. This handoff does not close release acceptance.

## Final acceptance

The pending statements above describe the original handoff. Final complete tests passed656/0/7 Windows-only skips out of663; the actual English/safety/Quit and selected multilingual UI gates passed. English/Spanish/Chinese six-study settings each passed185 actual consumer checks. Ten license/notice files byte-match source in actual build and publish output, and the published App assembly matches the final build. The complete English checker passed22 documents,228 links,16 guides and216 source files /13,165 tokens /111 exceptions with zero failures; final receipt-only bookkeeping passed the same documentation/self-check functions.

Evidence relative to `/Volumes/TradingHub-upstream-a861ab10ae/evidence/non-kvp-adoption-17dec28`: `tests/full-catalog-final/receipt.json`, `publish-notice-final.receipt.json`, `english-source-docs-final.receipt.json`, `final-doc-bookkeeping.receipt.json`, and the final UI receipts indexed by the dashboard. Earlier failures remain visible. This closes selected unmerged C# adoption documentation, not real-provider/model/OAuth, native-Windows, distribution or installed-app acceptance.
