# Non-KVP canonical source handoff — 2026-11-04

## Current canonical review route

This is a dated non-KVP handoff, not the latest full C# baseline. Start any new port review from the exact accepted source recorded in the [current dashboard](../en/TRANSLATION_STATUS.md). Later accepted work includes selected KVP capabilities, startup and execution/IB hardening. Review their recorded scope and evidence rather than using this document's older integration SHA as a complete inventory.

This route remains review-only. Keep existing port worktrees and oracle pins unchanged until separately authorized adoption names an exact source and its native validation. A correctness candidate is not an accepted replacement for canonical source or the installed app.

## Exact review inputs

- Canonical C# integration: `memoshi@b730d4c144b69f4a2695b5bdefd572dab6014369`.
- Selected feature commit: `041b4a886e9232f9126bd913522b27fc0c97bd1d`, based on `17dec28f0edbe4f1204b9c9a5d99ea1a6840342c`.
- Original report/plans preservation: `7593166284551a8b29c9b0d5148f6e85f6040473`.
- This is the exact tested and published source review input, not the final documentation-only descendant. The final documentation SHA is supplied separately after this bookkeeping is committed/pushed. Before port review, record that exact final canonical commit and compare its runtime input tree to this tested source; never substitute a moving `memoshi` tip.

## Selected scope and exclusions

PANASV adaptations cover cross-connection instrument discovery without account switching; 13 QuanTAlib moving averages/ribbon, Camarilla and strategy helpers; optional own Codex CLI; preservation-first IB CSV/offline/TWS/Gateway **market data only**; and explicitly granted/revocable research-only TradingView MCP. Jjuan adaptations cover timestamp-safe Binance Futures open interest, anchored Flow VWAP with estimates off by default, explicitly heuristic refill zones, bounded session/week/month/composite profiles preserving SessionVolumeProfile, and bounded deeper zoom with a historical 50,000-candle UI scenario.

Review the [dashboard](../en/TRANSLATION_STATUS.md#canonical-non-kvp-integration--2026-11-04), [adoption provenance](./fork-feature-report.md#canonical-integration-supersession--2026-11-04) and [third-party notices](../../THIRD_PARTY_NOTICES.md), rather than importing community trees wholesale. TradeDock's 28-commit translation is already inherited. Dham screenshots/hash/signature/source manifests are optional evidence/documentation tooling, not missing runtime; no Dham runtime or Intel packaging was imported. [KVP review](./kvp-adoption-review.md) is source-only: no KVP code/import is authorized.

## Port coordination contract

Rust/Swift worktrees, branches, dirty work and existing oracle pins remain untouched. This handoff authorizes **review only**, not automatic source rebases, oracle-pin updates, resumed implementation or merging. Each port must inspect the exact canonical SHA, inventory applicable behavior and compatibility differences, retain its current pin, and obtain separate explicit authorization before implementation or adoption. Record any later authorized adopted SHA and port-specific evidence independently.

Preserve persistent IDs/storage paths, account/feed selection boundaries, protected order/ACK/bracket/ledger/lifecycle semantics, explicit research grants and observed-versus-estimated study labels. C# APIs, rendering and dependencies are review material, not evidence of native port equivalence.

## Evidence boundary at this checkpoint

Source `b730d4c144b69f4a2695b5bdefd572dab6014369` is merged and published to `origin/memoshi`. Fresh canonical restore/tracker, zero-warning/error Release build, complete suite (656 passed / zero failed / seven existing Windows-only skipped / 663 total), eight sequential actual isolated UI processes, full English/source/docs checker, catalog gates and actual publish/ten-file notice identity all passed. Exact receipts and finite scenario coverage are in the dashboard. Historical external-worktree passes **and failures** remain separate; neither set is native-port evidence. At consolidation the installed Mac app was accepted source `c27e5aa`. A later separately authorized personal installation accepted source `7d990e8cac15da3038d238a15225f39ae3e546ca`; see the [actual installed-app acceptance](../en/TRANSLATION_STATUS.md#personal-mac-installation--2026-11-04). This does not change existing port pins or authorize adoption.

Authorized C# cleanup removed four auxiliary worktrees and five merged-only local obsolete branches. The atomic source push pruned only the five named obsolete remote C# branches recorded in the dashboard, with explicit old-SHA leases. A verified complete 28-ref pre-cleanup bundle is retained at `/Volumes/TradingHub-upstream-a861ab10ae/evidence/non-kvp-adoption-17dec28/canonical-consolidation-2026-11-04/pre-cleanup-refs.bundle`. Canonical/Rust/Swift are the remaining worktrees; historical main and both port branches/pins remain protected and unchanged.

No C# fake-provider/CLI/UI result certifies a Rust/Swift build, native desktop/lifecycle/installer behavior, actual broker entitlement/order behavior, successful real model/OAuth delivery, power-loss/exactly-once semantics, signing/notarization, distribution or arbitrary-code sandboxing. Those gates remain separate and require their own authorized environments/scenarios. No real broker/model/OAuth application calls or manual CI dispatch are part of this consolidation.
