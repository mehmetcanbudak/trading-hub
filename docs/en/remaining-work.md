[← Documentation index](./README.md)

# Remaining work

The single list of everything still open in the Trading Hub project: the canonical C# app (`memoshi`), its validation, release, upstream maintenance and the Rust/Swift ports. Verified results live in the [validation dashboard](./TRANSLATION_STATUS.md); checks that need accounts, networks or hardware are detailed in the [external acceptance checklist](./external-acceptance.md). This page says what is left, why, and what would close it.

**Snapshot (2026-11-19):** `memoshi` is at `52cf2b3`: [PR #17](https://github.com/mehmetcanbudak/trading-hub/pull/17), the deep review of the past week (about 150 findings fixed), is merged with all three platforms green and installed on this Mac. Upstream (`ralphtradesz/trading-hub`) has **0 pending commits** since the reviewed `1d5a774`. Both ports are 281 commits behind `memoshi` and paused.

Priorities: **P0** blocks safe use of the current code, **P1** should come next, **P2** is improvement or deferred work.

## 1. Decisions needed from the owner

| # | Decision | Context | Options |
|---|---|---|---|
| D1 | Trading date of evening sessions | Since `3bbedbb` a session starting at or after 12:00 (CME Sunday 18:00) belongs to the next trading date. Strategies that filter by weekday see Monday instead of Sunday; daily-loss-limit days are unchanged. | **Decided 2026-11-19: keep.** Already documented in [Strategies](./estrategias.md) and pinned by `SessionTradingDateTests`. |
| D2 | Explicit strategy reprice of a triggered stop | A strategy's own reprice may still cancel a triggered stop's executing MARKET child and send a replacement (by design); only the automatic bracket path is now blocked. | **Decided 2026-11-19: refuse every amendment of an executing child** (implemented on `fix/d2-d4-order-path`: Binance refuses a price update of a MARKET order). |
| D3 | Binance Reverse confirmation | Reverse is refused when Binance's position `updateTime` is older than the earlier orders' last update, even by 1 ms. | **Decided 2026-11-19: keep strict.** |
| D4 | Tradovate unreported amendment | An entry amendment of an order the venue never reports holds the account's send turn until it is cancelled or the session closes. | **Decided 2026-11-19: give it a bound** (implemented on `fix/d2-d4-order-path`: 30 s, then the unsent amendment is refused and its turn released). |
| D5 | Bybit saves on Windows | During a persistent sharing conflict each save, including the one before a reduce-only order, waits up to about 1 s. | **Decided 2026-11-19: keep.** |
| D6 | Lab runner P&L freshness | Open P&L-only runner saves are written at most once a minute, so the headless lab's estimate can be a minute old. | **Decided 2026-11-19: keep.** |
| D7 | Pull requests | #16 only edits the dashboard header that #17 supersedes. | **Decided:** close #16; take #17 out of draft and merge once all three platforms are green on one tree. |
| D8 | Install | The Mac runs `a03e15e`. | **Decided:** install the merged build after a byte-verified backup. |
| D9 | Old handoff note | `FORK-PARITY-HANDOFF-2026-11-16.md` is untracked in the repository root and superseded by this page. | **Decided 2026-11-19: deleted.** |
| D10 | KVS forming-bar semantics | After the review, a forming bar starts each live update fresh: a value the update does not assign is `na`, matching a full recalculation (Pine `var`-style carry-over is not implemented). | **Decided 2026-11-19: keep** (a carry-over keyword is a separate language-design item). |

## 2. Ship the current work (P0)

Done 2026-11-19: all three platforms green on `bf8b4ff` (run 44409663151; the ARM64 Tradovate failure was a test-fixture race, fixed), #17 merged as `52cf2b3`, #16 closed, both branches deleted, `52cf2b3` installed after a byte-verified backup (receipt in the [dashboard](./TRANSLATION_STATUS.md)).

Still open:
1. Watch the first real sessions for the new behaviour that touches saved data: hourly candle caches written by older builds are detected and refetched (expect some extra downloads); chart alerts are now parked instead of deleted on symbol switches.
2. Compare CPU and memory with the previous build: the installed app showed about one busy core (UI thread) and a stable ~7 GB working set with a live footprint chart on 757,020 bars. There is no baseline, so decide whether it is normal or a regression (P1).
3. D2 (refuse every amendment of an executing child) and D4 (bound the Tradovate unreported-amendment send turn): order-path changes on their own branch and PR, each with tests (P1).

## 3. C# app — engineering follow-ups

### Order path and durability
| P | Item |
|---|---|
| P1 | Rithmic residual: a new-exposure order whose admission stays unresolved past the 10 s bound lets the reducing close send, and can be admitted after the close completed (same bounded residual as Tradovate/Binance; documented in [Trading](./operar.md)). Decide whether to refuse such late admissions. |
| P2 | Simulated/replay accounts and the live strategy runner still write durable state on the UI thread (~70–90 ms per order); moving it off needs a strategy-execution redesign (deliberate earlier decision). |
| P2 | `Partial_native_close…` flake: proven durable-write latency under an fsync storm; only a longer fixture bound would cure it (needs approval). |
| P2 | Two order-path fixes have Tradovate-only tests; Binance shares the code (parity tests are defensive). |

### Data and feeds
| P | Item |
|---|---|
| P1 | IB now requests 60-minute bars (`1 hour`); verify pacing and session-aligned hours against a real TWS/Gateway session. |
| P1 | Hyperliquid months entirely outside its 5,000-candle window return no bars and are not flagged; needs a cache-layer "unavailable" marker. |
| P1 | `KlineHistory` refreshes closed months by replacement, so candles that aged out of a short-window venue can be lost on refresh; merge instead of replace. |
| P2 | Legacy caches holding only sparse finer bars cannot be told apart from real hourly bars (rare; refetch on demand). |

### Charts, alerts and UI
| P | Item |
|---|---|
| P2 | A bar close that lands in the same UI tick as a history rebuild is not checked by alerts. |
| P2 | Parked alerts are not updated when an indicator is removed while the chart shows another instrument. |
| P2 | Parked/unreadable-drawings status messages can be overwritten by the "ready" message when history finishes loading. |
| P2 | Workspace save cache keeps the full text of every saved drawings file for the session (bounded; a hash would do). |
| P2 | `BybitFeed` pings from a fire-and-forget task; when the socket closes mid-send the fault is unobserved and lands in `crash.log` (harmless, seen once on `52cf2b3`). Observe it and log it quietly. |

### Studies
| P | Item |
|---|---|
| P2 | Each closed-bar library study keeps its own copy of the candle history (SS-12). |
| P2 | Remaining helper duplication needs shared helpers in `StudyMath` (SS-13). |
| P2 | `FlowByExchange`'s 24-hour ranking still loops its window each frame (memory is bounded). |
| P2 | "Line break continue/reverse" semantics in the sweep studies depend on the original author's intent; confirm before changing. |

### KVS scripting
| P | Item |
|---|---|
| P1 | The ATAS namespace `Preprocess` still text-replaces across generated KVS code, including user strings; scope it to identifiers. |
| P2 | One-line `if (a) { x = 1 } else { x = 2 }` is rejected; only the multi-line form works (undocumented until fixed). |
| P2 | The Windows directory watcher added for edited copies has not run on Windows hands-on. |

### Localization tooling
| P | Item |
|---|---|
| P1 | The I18n extractor misses single words with a colon (`Usage:`) and conditional string concatenation, which hid untranslated text; extend `IsText`/`Concat`. |
| P2 | Wire URLs and JSON templates are carried as identity "translations" in `overrides.json`; teach the extractor to skip URL/JSON-shaped literals and drop those entries. |

## 4. Tests, CI and tooling

| P | Item |
|---|---|
| P1 | macOS hosted runners are often unavailable (zero-step cancellations). Consider a self-hosted Apple Silicon runner or a scheduled re-run policy. |
| P1 | Watch `LocalApiSettingsTests.A_port_change_restarts_on_the_new_port_and_the_old_port_refuses`: failed once under full-suite load locally, passes alone; and the Windows `vstest` testhost-connect timeout seen once on x64 (runner slowness). |
| P1 | The macOS safety-scene Tradovate fixture failure now prints diagnostics and the contract-lookup retry is fixed; confirm it stays green. |
| P2 | Liquidation-stream tests poll a condition with a 10 s deadline because `DerivativesData` exposes no event to await. |
| P2 | Disk headroom on this Mac swings between 3 and 21 GB (shared with other projects); one full-suite run hit `ENOSPC`. Keep evidence on the external volume and consider `TMPDIR` there for long runs. |

## 5. External acceptance (needs accounts, network, hardware or presence)

Details and procedures: [External acceptance checklist](./external-acceptance.md). None of these is needed to use the app in simulation; each closes a gap the dashboard lists as unverified.

| Area | Checks |
|---|---|
| Broker demo/testnet | Binance USDⓈ-M demo ([plan](./binance-demo-session.md)), including dragging a partly filled website order; Bybit demo/testnet; Tradovate demo with opt-in exchange-side OCO ([checklist](./native-protection-demo-checklist.md)); Rithmic/AMP paper; IB market data. |
| Accounts and sign-ins | Real Claude Code and Codex requests; TradingView research sign-in; Longbridge OAuth; webhook and Telegram alerts; options-data and ETF-flow connections with your keys. |
| Network | Deribit and Polymarket from an unrestricted network. |
| Hardware and presence | Sleep/lid/wake while connected; sustained load and heat; physical power loss; a real Intel Mac; hands-on Windows 10/11 (installer, Credential Manager, tray, DPI, brokerage, the new rename retry and IPv6 port probe). |
| Packaged app lifecycle | Native tray hide/reopen and automatic language restart on a freshly packaged build (isolated runs disable the tray by design); position-bearing Quit on the installed build. |
| Extensions | Any additional third-party strategy or indicator needs its own selected source/version and isolated replay. |
| Translations | Native-speaker review of the machine-translated Spanish and Simplified Chinese text (about 10.5k entries each; recent additions unreviewed). |

## 6. Release and distribution (deferred by choice)

| Item | State |
|---|---|
| Rebrand (name different from KVP's) | Planned later; `Product.Name` holds the display name, storage identities must stay. |
| Developer ID signing, notarization, public release artifacts | Deferred; builds are ad-hoc signed for personal use. |
| Windows beyond CI | Deferred; CI builds, tests, packages, installs and launches x64 and ARM64. |
| Real-money certification | Never implied by any gate; real accounts stay behind REAL confirmations, per-connection permissions and caps. |

## 7. Upstream and forks

| Item | State |
|---|---|
| Upstream monitor | Tracker issue #2 open, 0 pending commits (checked 2026-11-18). Incorporate future upstream changes through a sync branch and reviewed PR per [Upstream synchronization](./upstream-sync.md). |
| Fork survey | Last full survey 2026-11-15 (53 forks; only KVP had new work, adopted through `97a8521`). Repeat periodically; byfor11's Intel packaging idea was adopted. |

## 8. Rust and Swift ports (paused; resume only on explicit instruction)

Both worktrees are untouched by C# work, 281 commits behind `memoshi`, and contain their own agents' uncommitted changes. Each must deliberately adopt an exact reviewed `memoshi` SHA before continuing; C# Windows credential, tray, power and CLI APIs are not portable implementations.

| Port | Where it stopped | Next step per its own plan |
|---|---|---|
| Rust (`rust_port`, last commit 2026-10-23) | M0 incomplete: P1 verified; core-only P2 blocked (exports mismatch on a nested HTTP exception; deadline-contract correction unbuilt); GPUI production gate and macOS 12 runtime blocked; M1–M9 not started. See `plans/README.md` in the worktree. | Restore launch disk reserve, build and verify the deadline amendment, compare two new exports, finish P2 regression, then request P3. |
| Swift (`swift_port`, last commit 2026-10-19) | Implementation paused after a manual preview; all 778 production-native rows and 27 correction scenarios open; native GUI startup and offline-research fixes authored, unverified. See `plans/STATUS.md` in the worktree. | Verify the AppKit entry and recorder fixes, rerun the full native smoke. |
