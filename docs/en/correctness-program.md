# Memoshi correctness program

The approved work protects order admission, research results and saved work. The candidate is `fix/memoshi-correctness`, based on `1310f0a1fcee0b04ae6aabfbb19bf906699b3366`. Its baseline runtime matches the accepted `5c2583c` installation. This candidate has not replaced that installation or merged into `memoshi`.

The canonical checkout, personal data, credentials, upstream checkpoint, historical English audit baseline and Rust/Swift pins were not changed.

## Delivered work

| Work | Consumer change | Maintainer boundary |
|---|---|---|
| Configured quantity caps | Exposure-increasing strategy and reversal routes use the configured policy. Genuine reductions and cancellation remain available. | Shared admission policy and final native checks. |
| Causal walk-forward | Training consumes only accounting available before its cutoff. Later exits cannot rewrite training profit. | Actual closing fills, boundary contributions and intraday risk remain distinct. |
| History mutation ownership | Cooperating readers and writers coordinate non-IB history changes. | Ownership ends before network, progress and application callbacks. |
| Research cache freshness | Same-metadata corrections, missing days and combined-source changes invalidate research consumers. | Captured content identity belongs to the read that produced the data. |
| Native futures quantities | Invalid fractional, nonpositive and overflowing native quantities are refused before transport. | Virtual strategy sizing retains its separate conversion rules. |
| Accounts' Rithmic Close | Captured routing, current native evidence and latest leased ownership determine admission and settlement. A pending send that the close already waited on in an equivalent session is cancelled if it becomes working at the send slot. | Unknown reducing evidence reserves the original identity. A working order the close did not wait on still refuses without cancellation. Business refusal is not a storage fault. |
| Accounts order identity | ACK and terminal reports update the original row without a stale pending Cancel. | Exact session and native Order references preserve action ownership and row placement. Unknown snapshots are not pruned. |
| Native amendments | Remaining quantity and predecessor fill use one raw observation. Precommit working reports cannot accept an amendment. | Exact durable lineage and a commitment-relative publication revision. No receipt-only acknowledgment. |
| Immutable compilation | Admitted research, chart continuation and same-process restart retain their compiled generation. | Exact source, digest, type and factory travel together. |
| Saved research evidence | Reports retain captured source and input-quality evidence. Optimization summaries describe the measured population. | Unknown legacy evidence is unavailable, not reconstructed as certainty. |
| True-exit ownership | Ordinary work, recording and retired native readers finish before terminal handoff. | Admission closes before the drain snapshot. Final writes and callbacks remain owned. |
| CI and maintained documentation | Current display completeness, compiled scene discovery and standalone selector enforcement are exercised. | Portable test inputs carry the real provenance and guide files. English documentation is canonical. |
| Marked portfolio coverage | Missing coverage and cuts through measured windows are refused. | Common available intervals and exact weighted money remain authoritative. |
| Marked report consumers | Equity and drawdown remain visible without pretending boundary marks are independent trades. | Complete, partial and unavailable research coverage stays explicit through saved and exported reports. |

The [development guide](./desarrollo.md#native-execution-ownership), [backtesting guide](./backtesting.md), [strategy guide](./estrategias.md#files-and-compilation) and [data guide](./datos.md#interactive-brokers-bars-and-offline-history) describe the resulting contracts.

## Current verification

| Gate | Observed result |
|---|---|
| Release solution | All 15 projects. Zero warnings and errors. |
| Complete unfiltered suite | 1,733 total. 1,726 passed, zero failed and seven existing Windows-only skips on the final isolated-fake source. |
| Native consumers | All 19 new finality, outcome, quantity-observation and commitment-ordering cases and all 63 Accounts Close consumers pass within that complete run. |
| English UI and real guide code | 122 checks passed. All three complete C# guide examples compiled. |
| Startup compiler and disposal | All eight actual CLI consumers passed. Injected invalid and unreadable source warnings remained visible. |
| Catalogs | Spanish and Chinese each contain 7,868 translations. Current extraction has 5,958 texts and 1,319 templates, with zero completeness or template defects. All 3,300 original provenance entries remain covered. |
| Source and documentation guard | 312 source files, 17,644 text tokens and 111 existing compatibility exceptions. Zero source or maintained-documentation failures. |
| Individual source decisions | Exactly 58 reviewed source hashes and reasons changed. The 213 unrelated entries in each approval map, historical baseline, literal inventory and deletion reviews stayed unchanged during publication. |
| Current functional registry | All 50 compiled Darwin handlers pass on the final linked fixture. The 49 nonperformance consumers ran separately from the resource/fidelity consumer. Nine runtime DLLs and both real guide inputs remained unchanged. The provider scene passed all 360 checks. |
| Multilingual and report surfaces | Fresh Spanish and Chinese safety, research, generation and seven-page groups pass. All seven English core pages pass. Settings and Accounts were visually inspected in all three languages. All four final saved HTML exports were rendered and inspected with loaded inline charts and no horizontal overflow. |
| CLI selector boundaries | All five actual invalid or mixed standalone selectors exited before capture, store or keychain creation. |

These are source, fake-provider, durable-consumer and isolated Avalonia results. They are not private-venue, installed-package or native-Windows certification.

Local receipts, before runtimes, failed runs and exact manifests are preserved under `/Volumes/Elements/memoshi-correctness-evidence-e8bb16b7cb`. The [validation dashboard](./TRANSLATION_STATUS.md#correctness-program-candidate) remains the release-status authority.

The [decision trail](correctness-decisions.tsv) preserves the append-only implementation and verification history. Later publication receipts remain in the external evidence directory.

## Attention

- The pending-peer Accounts Close timeout was a production race. The close loop could declare cancellations complete just before an equivalent session's awaited pending send was acknowledged, and the send slot then refused the close. CI captured that refusal with isolated fake URIs. When nothing has been committed or sent, the close now returns to cancellation within its existing 15-second bound. The captured-finality refusal for a reappearing replay-absent order is unchanged.
- The other two earlier timeouts most likely came from shared fake-venue authority. Each fake server used a random loopback port without a path, so independent fakes on one port looked like one Rithmic account. Stopped sessions with open orders stay registered by design. An instrumented root-URI suite counted up to 43 such authorities and an expected 0.18 colliding fake starts per run, mostly in Accounts Close tests. A collision leaves a foreign working order that can never become terminal, so the close waits past the 8-second test deadline. Real-session controls reproduce the interference, and per-instance fake URIs remove it: an instrumented isolated run had zero inherited sessions across 116 fakes.
- Retained stopped sessions have a production consequence that needs an owner decision. If a working order ends at the exchange while a connection is stopped, and the same account reconnects in the same process, Accounts Close for that contract keeps refusing until restart. It never sends a wrong order, but it blocks the emergency close.
- The journal consumer now samples corrected data immediately and observes the genuine timer until Statistics displays the same result. A controlled run observed 500 then 400 without reselection. No production timer or financial assertion changed.
- Earlier independent reviews used separate GPT 6.1 Sol contexts. A later Claude review read the native close, admission, durable close journal, quantity caps and live launch code and found only the retained-session consequence above. Remaining files were not re-reviewed across families.
- The `kvp-performance` scene ran three alternating times on the base `1310f0a` and the candidate builds. Every scope's in-process warmed medians overlap, for example a cold chart frame of 41 to 48 ms in both builds, so no regression or speedup is claimed. Wall time was not compared because the base binaries ran from an external disk image. Resource checks are not a physical-disk throughput or GPU claim.
- Automated language-candidate detection is not a natural-language proof. Browser inspection found and fixed one hard-coded Spanish heading after the automated gate passed. All four final English exports retain their loaded charts, and the three ordinary-containing reports now show the English profitability heading.
- Marksman's earlier four warnings most likely came from the editor integration, which indexes the session's main checkout and rejects this worktree as a workspace. Run directly on this tree, it reported 157 ambiguous links and 2 broken links. The Spanish and English guides share file names, and Marksman matches bare names across folders. Same-folder links now use `./`, and the two links into a separate Errata repository are plain text. Marksman now reports zero findings, with a deliberate broken-link control still detected.
- Windows CI exposed four recurring and two intermittent failures, four of which also fail on `memoshi`. Fixture cleanup deleted directory junctions with recursive delete, which .NET routes through `DeleteVolumeMountPoint`. Tick tests read a live day with a stricter share mode than production readers. Ledger fixtures deleted storage while execution projection still held it open. Without `.gitattributes`, Windows checkouts compiled C# raw strings with CRLF, which also breaks catalog lookup of multi-line text; C# sources are now LF everywhere. A macOS CI chart scene read the strategy overlay before its timer redraw.
- A captured-finality test held native admission inside its permission barrier while waiting for a late fill. The close polls every 20 ms and can enter that barrier before the fake's trailing cancel notification is handled. That handler then waits on the held gate, and the fill queues behind it on the same connection until the barrier expires. Delaying the trailing notification into the barrier window reproduced it 3 of 3 times. The test now settles the cancellation before the close, and the same forced interleaving passes 3 of 3 with every original assertion unchanged. Production holds this gate only for the in-memory admission check.
- An ARM64 cleanup failure came from the Rithmic read loop. Its last order notification commits the strategy ledger synchronously, sometimes within 10 ms of the test returning, so Windows refused the temporary-directory delete. A ledger-access probe recorded the same 11 writes before and after the fix. The same race produced macOS "Directory not empty" failures in `RithmicTests`. The closing-quantity test and `NativeOrderTestFixture` now join each Rithmic session with `StopAsync` before deleting storage, which covers every broker the fixture creates.
- Two Binance tests failed once each on CI and are not attributed. `Triggered_partial_market_stop_remains_current_while_sibling_capacity_reconciles` also fails on `memoshi` in 2 of the last 6 runs. `Unknown_submission_replay_books_each_owned_fill_and_realized_profit_once` failed once on ARM64 and passed 25 of 25 stressed local runs. This branch does not change Binance code or either test.
- The bulk ledger recovery timeout is a test budget, not a hang or regression. Run directly with data on the internal disk, the child recovers 512 heads in 19.4 to 23.9 s on the base and 16.2 to 17.4 s on the candidate, mostly durable admission and reset recovery. Suite load on this Mac and hosted ARM64 runners pushed it past the shared 30-second watchdog. Only that test's snapshot phase now allows 120 seconds; startup, exit and pipe-drain keep the 30-second deadlock watchdog.
- Native Rithmic evidence does not establish exchange-atomic position/fill inclusion, venue-command acknowledgment correlation, server-enforced reduce-only, native OCO or exactly-once delivery. The in-process amendment revision is not a persisted cross-process clock.
- Real local storage failures and a failed archive command remain in the evidence. All eight temporary generated roots were restored as verified current real directories. English, safety and saved-report consumers passed from those paths. Only the owned RAM device was retired after exact runtime and fixture preservation. No personal stores or installed app were used to recover space.

## Remaining operator gates

| Next action | Pstack workflow and boundary |
|---|---|
| Review and integrate the candidate | Use the review and Babysit workflows on the draft PR. Keep it draft until required CI and unresolved-risk decisions are explicit. Do not merge from green status alone. |
| Validate private providers | Use one bounded Bug fix or Investigation run per dedicated paper/demo environment. Enter credentials locally and authorize finite orders separately. Fake-provider proof is not venue conformance. |
| Validate Windows and native Mac behavior | Run native platform scenarios and separately authorize any packaged Mac replacement. This branch does not certify installation, restart, power/lid or private credential-manager behavior. |
| Improve measured performance | Use Perf issue for a concrete regression or Hillclimb for one metric. Establish a real workload, limiter and reproducible baseline before claiming a win. |

## Pstack choices that changed the work

| Principle | Specific choice |
|---|---|
| Model the Domain | Kept compiled generations, unknown evidence, marked coverage, native row identity and prepared versus committed amendments as distinct data. |
| Foundational Thinking | Named the accounting and ownership shapes before changing their consumers. |
| Boundary Discipline | Kept real provider admission and durable ownership checks at their consuming boundaries. Separated business refusal from actual I/O failure. |
| Type System Discipline | Used explicit nullable evidence and one captured amendment quantity pair instead of interpreting unknown as zero. |
| Laziness Protocol | Reused existing gates and records. Removed the preparation-relative revision and redundant commitment Boolean instead of adding another manager. |
| Build the Lever | Kept rerunnable native regressions, exact source-fingerprint export, strict catalog checks and compiled-registry consumers. |
| Separate Before Serializing Shared State | Used isolated worktrees, fresh stores and one stable authority per fake venue instead of clearing shared execution state. |
| Make Operations Idempotent | Retained durable uncertainty and original identities. Unknown submissions are not automatically resent. |
| Migrate Callers Then Delete Legacy APIs | Migrated consumers to the selected contracts without retaining weaker aliases or alternate dispatch paths. |
| Exhaust the Design Space | Compared independent close-settlement and amendment-provenance designs before selecting the smaller local model. |
| Attack the Premise | Reproduced both precommit publication schedules rather than assuming a committed Boolean or a later callback was sufficient. |
| Fix Root Causes | Fixed causal accounting, captured compilation and leased ownership at their producers and consumers, not with output guards. |
| Test Behavior, Not Implementation | Required real frames, exact persisted money and lineage, later usable operations and restart. Removed diagnostic-word and forwarding pins. |
| Prove It Works | Exercised actual consumers. Builds, source reviews and older green runs did not substitute for final behavior. |
| Sequence Work into Verifiable Units | Kept before failures, individual repairs and complete integration gates separately evidenced. |
| Explain the Number | Kept measured resource results separate from an unestablished comparative benchmark. |
| Guard the Context Window | Partitioned source and evidence reviews while retaining one integration owner and exact file decisions. |

For future work, give pstack one outcome, its consumer-visible acceptance and explicit safety boundaries. Use `how` for ownership walkthroughs, bounded `swarm` audits for independent read sets, `architect` before cross-boundary changes, the Bug fix playbook for executable defects and `show-me-your-work` for resumable evidence. Keep concurrent code owners away from the same execution state.
