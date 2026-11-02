# Trading Hub public-fork comparison — 2026-10-22

## Result

**32 public forks, 36 public branches, 3 forks with additional published commits, 29 forks matching upstream on every published branch.** There are four branches with extra work: one feature branch, one community English branch, our completed English branch, and our separate image-optimization branch.

Snapshot: **2026-10-22 20:52 UTC**. Baseline: upstream `ralphtradesz/trading-hub:main` at `9ee74a4c527ac43120423acd41e006ec9d7877ad` (2026-10-17). Every fork's published `main` points to that exact commit. None of the additional-work branches is behind current upstream.

The noteworthy newly discovered product work is **jjuangutierrez's open-interest/zoom branch**. TradeDock's English work is already incorporated in our English branch. The remaining independent change is ImgBot's image compression in our own fork.

## Method and limits

- Fully paginated the upstream forks endpoint and every fork's public branches endpoint. GitHub's counter and the API inventory both returned 32 forks; all 32 branch enumerations succeeded.
- Compared each distinct non-upstream branch tip against the exact upstream SHA. All commit lists were complete: maximum 38 commits and 199 changed files, below the requested commit page size and GitHub comparison file cap. Identical SHA values were deduplicated, not inferred from repository names, stars, timestamps or descriptions.
- Inspected the seven-file open-interest patch and relevant complete source files; inspected TradeDock's published dashboard, draft PR and CI results; inspected our branch history and CI. Downloaded and decoded the eight image changes for pixel comparison.
- **Did not execute, build, install or merge fork C# code.** Existing CI results are reported as evidence from GitHub, not tests rerun during this audit. This is a change inventory with targeted source inspection, not a comprehensive security/correctness audit.
- Public, currently published branches only. Private/unpushed/local/deleted branches, tags not used as branches, and work existing only in external PR heads are not covered. “No additional published commits” does not mean the owner has done no private/local work.
- Our local Rust/Swift worktrees are not published branches in this public snapshot; no public Rust/Swift implementation is established by this inventory.

Primary inventory sources: https://api.github.com/repos/ralphtradesz/trading-hub/forks?per_page=100&sort=newest and the public branch API for each fork. Baseline: https://github.com/ralphtradesz/trading-hub/commit/9ee74a4c527ac43120423acd41e006ec9d7877ad

## Forks with additional work

### 1. jjuangutierrez/trading-hub — new open-interest feature and deeper zoom

Repository: https://github.com/jjuangutierrez/trading-hub
Work branch: https://github.com/jjuangutierrez/trading-hub/tree/fix-zoom-limit
Pinned commit: https://github.com/jjuangutierrez/trading-hub/commit/eb571a5e87605ec1ccbaf1b045febb36ea5860b0

**1 commit ahead, 0 behind; 7 changed source files.** Commit `eb571a5e87`, titled “OI added,” was committed on 2026-10-22 at 01:37 UTC. Its default `main` remains unchanged; the work is easy to miss if only main is checked.

Actual changes:

- Reduces `ChartViewport.MinBarWidth` from **1.5 to 0.1**, allowing much further zoom-out.
- Adds Binance futures live open-interest polling through the existing feed HTTP helper, at roughly **3-second intervals**.
- Adds an optional/default `IMarketDataSink.OnOpenInterest` callback and propagates OI through `InstrumentData`, `CandleSeries` and the chart model.
- Adds a **212-line OpenInterest indicator** with total-line and positive/negative change histogram modes. The indicator separately downloads approximately **14 days of 5-minute Binance futures OI history**.
- No test files or CI workflow changes are included in this commit. At inspection, GitHub returned **zero Actions runs and zero check runs for this SHA**, and the fork had no PRs. That is absence of published automated evidence, not proof the author did no manual testing.

**Worth evaluating, but not a drop-in merge recommendation.** Targeted source inspection found concrete review concerns:

1. `InstrumentData.OnOpenInterest` invokes its event directly from the background polling path; the chart handler mutates the candle series immediately. Review UI-thread dispatch and thread safety rather than treating this like the existing queued UI-pump updates.
2. The anonymous `OpenInterestChanged` chart subscription is not removed in `ChartModel.Dispose`, unlike the existing named data-event subscriptions. Review lifecycle/leaks and callbacks after chart disposal.
3. `OpenInterest.Buscar` returns the **first available sample when the requested bar predates all downloaded samples**. That can display a future OI value on older bars; missing history should not be mistaken for known historical data.
4. The indicator's history loader calls Binance futures directly without checking the chart's provider; non-Binance instruments and incomplete history/error handling need review. Its display labels, code identifiers and comments are largely Spanish and would need English work.

Pinned source evidence:

- https://github.com/jjuangutierrez/trading-hub/blob/eb571a5e87605ec1ccbaf1b045febb36ea5860b0/src/TradingHub.Core/Data/InstrumentData.cs#L394-L399
- https://github.com/jjuangutierrez/trading-hub/blob/eb571a5e87605ec1ccbaf1b045febb36ea5860b0/src/TradingHub.Charting/ChartModel.cs#L162-L166
- https://github.com/jjuangutierrez/trading-hub/blob/eb571a5e87605ec1ccbaf1b045febb36ea5860b0/src/TradingHub.Charting/ChartModel.cs#L828-L840
- https://github.com/jjuangutierrez/trading-hub/blob/eb571a5e87605ec1ccbaf1b045febb36ea5860b0/src/TradingHub.Indicators.Native/OpenInterest.cs#L87-L160
- https://api.github.com/repos/jjuangutierrez/trading-hub/commits/eb571a5e87605ec1ccbaf1b045febb36ea5860b0/check-runs

### 2. indigiti/tradedock — community English draft, already incorporated by us

Repository: https://github.com/indigiti/tradedock
Work branch: https://github.com/indigiti/tradedock/tree/translation/english-documentation-ui
Pinned tip: https://github.com/indigiti/tradedock/commit/65ae0229bc0b36ba44156cfaaa530607c382ec25

**28 commits ahead, 0 behind; 47 changed files.** All work commits are dated 2026-10-18. Its default `main` remains identical to upstream.

Actual work includes English README/project instructions, retained Spanish originals, 17 English documentation files including the index, a **604-entry UI glossary**, partial labels/safety text across **20 application C# files**, and translation validation/headless screenshot CI.

Status is important:

- PR #1 is **open and draft**, not merged: https://github.com/indigiti/tradedock/pull/1
- Its own pinned dashboard explicitly states **“BLOCKED — do not merge”**, partial UI translation, condensed English guides and outstanding visual/live-provider checks: https://github.com/indigiti/tradedock/blob/65ae0229bc0b36ba44156cfaaa530607c382ec25/docs/en/TRANSLATION_STATUS.md
- GitHub shows successful CI at this exact tip, for example https://github.com/indigiti/tradedock/actions/runs/39140931062 . Green CI does not override the dashboard's incomplete release scope.
- **All 28 commits are already ancestors of our English branch.** This is provenance we reused, not another missing feature to import again.

### 3. mehmetcanbudak/trading-hub — our completed English work and maintenance

Repository: https://github.com/mehmetcanbudak/trading-hub
Default/work branch: https://github.com/mehmetcanbudak/trading-hub/tree/feat/english-localization
Pinned tip: https://github.com/mehmetcanbudak/trading-hub/commit/5f7c19e7528f47582f34f14d2c06e71d388b9858

**38 commits ahead, 0 behind; 199 changed files versus original upstream.** This includes **28 inherited TradeDock commits plus 10 additional commits in our fork**, not 38 independently authored changes.

Additional work verified in public history/diffs includes:

- Broader English UI, SDK display defaults, reports, prompts and complete English guides, with compatibility-aware display helpers rather than blindly changing stored identities.
- English strategy/indicator sample filenames and identifiers, including the separate New indicator generator; compatibility regression coverage and safer source/structure audits.
- Isolated UI/safety-dialog checks, English screenshots, fake Claude/power tests and portable test-fixture fixes.
- Windows shared-runtime build/tests/source auditing in addition to macOS UI validation; this is not a completed Windows desktop release.
- A daily/manual **original-upstream** monitor, reviewed checkpoint ledger and safe English update guide.

Public validation at this exact tip is successful: https://github.com/mehmetcanbudak/trading-hub/actions/runs/39239369636 . The published dashboard records **222 regression tests**, **13 tracker tests**, and **120 macOS English UI checks**, with real brokerage/paid-model/physical-power/Windows desktop limitations: https://github.com/mehmetcanbudak/trading-hub/blob/5f7c19e7528f47582f34f14d2c06e71d388b9858/docs/en/TRANSLATION_STATUS.md

This branch does **not** contain the newly discovered `jjuangutierrez` OI commit. The existing automatic tracker monitors original upstream main, **not all fork feature branches**; this audit did not change its scope.

#### Separate imgbot branch — small, verified image-only change

Branch: https://github.com/mehmetcanbudak/trading-hub/tree/imgbot
Commit: https://github.com/mehmetcanbudak/trading-hub/commit/3344a566f9f3c3c61ee7d0a535e2034cb400a07b
Open PR: https://github.com/mehmetcanbudak/trading-hub/pull/1

**1 commit ahead of original upstream; 8 PNG files only.** This is a separate branch based on original main, not a 39th commit on our English branch. PR #1 targets **our main**, not the English branch.

Verified eight-file totals: **2,463,903 → 2,327,289 bytes**, saving **136,614 bytes (5.54%, approximately 133.4 KiB)**. All **8/8 images have identical dimensions and decoded RGBA pixels** before/after. This confirms the visual-pixel claim, not preservation of every PNG metadata chunk.

Files: six original `docs/img/` screenshots and two application icons (`icon.png`, `icon-512.png`). It does **not** optimize the newer English screenshots under `docs/en/img/`. Lowest-risk optional candidate, but only a small asset-size improvement; no feature or trading behavior changes. Nothing was merged during this audit.

## Complete inventory: all 32 forks

“Extra commits” is relative to upstream `9ee74a4`, by branch. All branches not listed as additional are exact SHA matches, not merely similar working trees.

| # | Fork URL | Published branches / extra commits | Assessment |
|---|---|---|---|
| 1 | https://github.com/jjuangutierrez/trading-hub | main: 0; fix-zoom-limit: 1 | OI + deeper zoom; needs review/testing. |
| 2 | https://github.com/jonaagarcia10/trading-hub | main: 0 | No additional published work. |
| 3 | https://github.com/viratxd/trading-hub | main: 0 | No additional published work. |
| 4 | https://github.com/reza-boy/trading-hub | main: 0 | No additional published work. |
| 5 | https://github.com/dotty377/trading-hub | main: 0 | No additional published work. |
| 6 | https://github.com/dreamChaser4Rex/trading-hub | main: 0 | No additional published work. |
| 7 | https://github.com/ashuai168/trading-hub | main: 0 | No additional published work. |
| 8 | https://github.com/AllenHegmann999/trading-hub | main: 0 | No additional published work. |
| 9 | https://github.com/penniger/trading-hub | main: 0 | No additional published work. |
| 10 | https://github.com/fomosapienc/trading-hub | main: 0 | No additional published work. |
| 11 | https://github.com/indigiti/tradedock | main: 0; translation/english-documentation-ui: 28 | English draft; all 28 commits already in ours. |
| 12 | https://github.com/fairrog/trading-hub | main: 0 | No additional published work. |
| 13 | https://github.com/mehmetcanbudak/trading-hub | main: 0; feat/english-localization: 38; imgbot: 1 | Our English/maintenance work; separate image-only PR. |
| 14 | https://github.com/hangodot/trading-hub | main: 0 | No additional published work. |
| 15 | https://github.com/winxdevop/trading-hub | main: 0 | No additional published work. |
| 16 | https://github.com/denisfeo05/trading-hub | main: 0 | No additional published work. |
| 17 | https://github.com/dethnass/trading-hub | main: 0 | No additional published work. |
| 18 | https://github.com/qqisee/trading-hub | main: 0 | No additional published work. |
| 19 | https://github.com/gaurav8b/trading-hub | main: 0 | No additional published work. |
| 20 | https://github.com/davidgilmoursp/trading-hub | main: 0 | No additional published work. |
| 21 | https://github.com/Traveler200000/trading-hub | main: 0 | No additional published work. |
| 22 | https://github.com/redlion/trading-hub | main: 0 | No additional published work. |
| 23 | https://github.com/MRx007-123/trading-hub | main: 0 | No additional published work. |
| 24 | https://github.com/oralyalcin/trading-hub | main: 0 | No additional published work. |
| 25 | https://github.com/mostafv/trading-hub | main: 0 | No additional published work. |
| 26 | https://github.com/madantv/trading-hub | main: 0 | No additional published work. |
| 27 | https://github.com/SylvanusAdonu/trading-hub | main: 0 | No additional published work. |
| 28 | https://github.com/atulmanjhi2024-cloud/trading-hub | main: 0 | No additional published work. |
| 29 | https://github.com/gouqizi2214730-source/trading-hub | main: 0 | No additional published work. |
| 30 | https://github.com/odirleisp/trading-hub | main: 0 | No additional published work. |
| 31 | https://github.com/patzheng/trading-hub | main: 0 | No additional published work. |
| 32 | https://github.com/drmarmar/trading-hub | main: 0 | No additional published work. |

## Recommended next steps

1. **If you want a new capability:** evaluate the OI/zoom work in a separate review branch/worktree, fixing thread/lifecycle/history handling and English labels, then add fake-feed/history/render tests before considering integration. Do not merge it directly into the installed trading app.
2. **For English improvements:** keep our existing branch; TradeDock's draft is already incorporated and has no newer published commits in this snapshot.
3. **For small cleanup:** optionally review the eight-file image optimization for an English-branch cherry-pick; do not merge the bot PR into main merely because its title says “Optimize images.”
4. The other 29 forks currently offer no extra public-branch commits to import.

No fork code or application changes were made. Only this report was added locally; existing unrelated working-tree files were left alone.
