# Plan 009: Preserve English audit strength and translate new surfaces

## Execution contract

- Planned at base `17dec28f0edbe4f1204b9c9a5d99ea1a6840342c`; prepared 2026-11-04 for the same uncommitted integration on `/Volumes/TradingHub-upstream-a861ab10ae/non-kvp-adoption-17dec28-streamed`, branch `feat/non-kvp-adoption`.
- **Dispatch prerequisite:** parent must first observe a successful combined build and actual fake-provider UI/CLI smoke. Do not execute while the current source freeze/build is running. Parent supplies exact receipts and current reviewed source paths at dispatch.
- Modify ONLY the execution worktree, never canonical `/Users/mehmetcanbudak/Projects/trading-hub`, installed app, other worktrees, user data, ports or Git refs. No commits/merges/push/install. GPT6.1Sol inherited; no nested agents.
- Read repo dashboard/sync rules. English canonical; persistent IDs/VTK1/paths/default workspace/orders/ledgers/ACK/brackets/quit protections remain unchanged. KVP, Intel, live accounts/providers/model/OAuth and CI dispatch remain excluded.
- Use specialized read/glob/grep/edit/write; C# LSP absent, targeted reference search fallback; no source-text shell edits. Context7 quota unavailable: primary pinned source/docs only, no retry/key request.
- No builds/tests/lint/formatters during the editing wave. Prebuilt I18n extraction/merge programs may run with all six isolated stores/fake Keychain/sleep guard off in parent-owned external fixture locations; never implicit `dotnet run` builds. Parent reruns gates after both documentation/catalog owners finish.
- Evidence only: do not claim runtime/native/provider passes from source review, do not overwrite old failure history, do not claim installed application contains this unmerged work. Parent maintains advisor-plans index; no speculative docs or unrelated cleanup.

## Current state and invariants

`tools/TradingHub.UiSnapshot/EnglishLocalizationAudit.cs:14` pins historical baseline9ee74a4c527ac43120423acd41e006ec9d7877ad. `Structure`35–50 fingerprints all nontext tokens and recursively parsed raw C#templates with SHA256; **do not change that algorithm, baseline, numeric checks or Spanish candidates**. Run134–148 audits tracked+untracked current source. At140 EVERYmissing baselinepath currently fails `source deletion requires explicit review`.

The real clean cutover removed Core/Feeds/InteractiveBrokers/IbProbe.cs after migrating its sole caller to the full IBclient. Parent observed `git cat-file -e 9ee...:src/TradingHub.Core/Feeds/InteractiveBrokers/IbProbe.cs` EXIT0, so this is a real baseline deletion needing explicit review—not a reason to keep a stub or advance baseline.

`tools/english-localization-review.json` stores exact approvedStructure and perpath reviewedFiles reasons, not blanket folder approvals. New feature source and modified structures need individually reviewed fingerprints after code review/runtime proof. All existing broker/ledger review notes remain.

I18n Program supports `extract`, `pending`, `merge`, `check ... --require-complete`, `coverage provenance language`. Extraction is inclusive; identifiers/paths/protocolkeys/usertext must not be translated accidentally. Currentprovenance3300upstream entries/overrides/collisions remain intact. Englishsource is canonical, es/zh-CN catalog keys are English.

## Scope and implementation

Own tools/english-localization-review.json; the smallest EnglishLocalizationAudit.cs deletion-review extension; src/TradingHub.App/Localization/{es,zh-CN}.json; tools/TradingHub.I18n/overrides.json only when needed to preserve authored future translations. No appbusinesslogic, guide/changelog/NOTICE/Appcsproj, sourcebaseline changes, upstream ledger or arbitrary C# rewrites. Doc owner owns guides/evidence/licenses. Report any actual non-English new source to its original code owner, not your broad edit.

1. Read parent two-axis findings/corrections and observed gates; review every affected src C#path before recording hash. Use the existing compiled Structure function from UiSnapshot via disposable external helper if needed; don't reimplement normalization, approve wholefolders or generateblind approvals. Each exact changed/new sourcehash gets an individual behavior/provenance/verificationreason; no claim language-only equivalence for new functionality.
2. Add explicit hash-bound deletion review: an exact path approval with original baseline structural hash and nonempty reason. Allow the missing path ONLY if path exists in fixedbaseline, its baseline hash matches reviewedrecord and reason present. Reject unapproved deletions, stale/recreatedpath approvals, unknownbaselinepaths, mismatchedhashes and emptyreasons. Approve ONLY obsolete IbProbe deletion with precise replacement/caller evidence. Do not weaken addedfiles/numeric/raw-template/Spanish gates; no wildcard exclusions or skippedaudit. Preserve originalbaseline and all unrelated approvals. Expose no new productionapp API for this tool check.
3. Prove deletion guard behavior through actual checker consumer or focused tool selfcheck: the intended reviewed deletion passes, removing approval fails, wrong baseline hash fails and a restored/recreated path makes approvalstale. No source-text assertion against checker implementation; source is tool INPUT, not incidentalimplementation test. Keep any disposablehelper out of product/repo after proof.
4. Run prebuilt I18n DLL extraction against current src into owned external evidence, inspect pending entries and add accurate Spanish/SimplifiedChinese translations of new application surfaces. Preserve placeholders/accelerators/protocol/units/paths and don't translate dynamicvendor/model output. Retain existing canonical keys/collisions/provenance; no deletion of inherited translations or Englishidentity fallback pretending complete.
5. Add authored overrides only in existing convention if regeneration would otherwise erase selected new translations. Parent reruns both original upstream coverage and completeness/template checks for actual extracted candidates. Coordinate any candidate exclusions strictly perexisting actual-identifier rules, not tohideuntranslatedUI.

## Commands and outcomes

Use /Users/mehmetcanbudak/.dotnet/dotnet with parent-owned external CLIhome/TMPDIR/NuGet and all six fixture store overrides. Prebuilt DLL expected at tools/TradingHub.I18n/bin/Release/net10.0/TradingHub.I18n.dll (confirm after build). Extraction/merge are operationaldata generation, NOT build/test; do not run implicit dotnetrun or checks whileother owner edits. Parent afterwave:
- python3 tools/check_english_translation.py -> source+docs pass, unchangedbaseline/algorithm, every newstructure reviewed.
- both I18n coverage provenance commands ->0missing/mismatch.
- I18n check extracted catalog es/zh-CN --require-complete ->0missing/mismatched templates for validcandidates.
- actual English/newfeature scenes plus selectedSpanishChinese newfeature screenshots -> no knownuntranslated candidates; finitecoverage not linguisticproof.

## Done and STOP

All edited/new/deleted source has precise review metadata without silent exceptions; no stale exceptions. Current audit was actuallyrun byparent and passes; unresolved fail remains visible. NewUItranslated correctly, placeholders intact, oldprovenancepreserved. No source/asset identity changes, no realprovider/model/OAuth; packagecopyrightliteral exceptions only exactreviewedcases. STOP if asked toadvance baseline/suppressguard, no evidence of source review, deletion approval is broader thanexactpath+hash, or translationrequireschangingstorage/APIIDs. Report exact blockers and finish independentreachabletranslations.

## Final acceptance

Both catalogs contain5,534 entries, with813 new authored overrides and3,892 valid display candidates. Both original3,300-entry provenance coverage checks and current candidate/template checks passed with zero missing/untranslated/mismatched entries. The app-owned composed My indicators caption translates in actual Spanish/Chinese settings; persistent Source/Category IDs and custom names are unchanged. All three language study scenes passed185 real settings/calculation/renderer checks each; English/safety/Quit and seven-page Spanish/Chinese scenarios also passed.

The original baseline, numeric/raw-template Structure algorithm,111 compatibility exceptions and precise deletion guard remain unchanged. Seven actual full-audit deletion-guard cases passed. Individually reviewed final source hashes include the research shutdown/monitor and authoritative chart-tooltip cleanup corrections. The final full English checker passed216 source files /13,165 tokens with zero failures, and all663 tests ran with656 passed, zero failed and seven existing Windows-only skips.

Evidence: `catalog-audit/i18n-catalog-gates-caption-final.receipt.json`, `catalog-audit/deletion-guard-cases-final.receipt.json`, `catalog-audit/source-hashes-final-corrections.json`, `english-source-docs-final.receipt.json`, `tests/full-catalog-final/receipt.json`, and final locale UI receipts under the external evidence root. No source identity, rolling checkpoint or historical audit baseline was advanced.
