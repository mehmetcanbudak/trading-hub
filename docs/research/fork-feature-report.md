# Trading Hub — contributing-fork feature report

## Scope and evidence

This report covers additional published work in the public fork network of **ralphtradesz/trading-hub**, not unchanged copies, private work, deleted branches or unpushed Rust/Swift worktrees. It is a source and contribution inventory, not a release approval, security audit or recommendation to merge a fork wholesale.

The original baseline is [`1d5a774d3d1bf2baf9259597234d227daad915a9`](https://github.com/ralphtradesz/trading-hub/commit/1d5a774d3d1bf2baf9259597234d227daad915a9). Our comparison reference is **memoshi@17dec28f0edbe4f1204b9c9a5d99ea1a6840342c**. The principal closing observation was **2026-11-03 18:27 UTC**. KVP advanced during preparation; its final inspected pin is **50220ba91613161685f84384825d855cb4dbeb76**, including five commits observed after the initially inspected `121dcbb`.

| Coverage | Observed |
|---|---:|
| Public forks enumerated | 52 |
| Published fork branches enumerated | 83 |
| Published fork tags enumerated | 24 |
| Distinct current branch tips compared | 38 |
| Forks with additional committed content | 7 |
| Unchanged/behind-only forks excluded | 45 |

All paginated branch/tag inventories and exact-tip comparisons succeeded. Every listed fork reported zero child forks. Source inspection covered complete contribution families and representative implementations; it did not execute fork code, builds, tests, installers, OAuth sessions, broker requests, remote MCP tools or model requests. Authored tests, README claims, release assets and workflow definitions are distinguished from executed evidence.

Ahead/behind counts below are relative to the exact original baseline. They include merges, inherited translation commits and squashed histories; they are **not independent feature totals** and must not be summed across branches or forks. All source links are pinned. Branches may advance after this snapshot.

Primary discovery source: [paginated original-forks API](https://api.github.com/repos/ralphtradesz/trading-hub/forks?per_page=100&sort=newest), followed by every fork's branches/tags endpoints and exact-SHA comparison APIs.

## Contribution map

| Fork and inspected branch | Pin | Ahead / behind original | Main specialization and placement |
|---|---|---:|---|
| [mehmetcanbudak/trading-hub — memoshi](https://github.com/mehmetcanbudak/trading-hub/tree/17dec28f0edbe4f1204b9c9a5d99ea1a6840342c) | `17dec28` | 81 / 0 | English-first compatibility, reviewed order/ledger/lifecycle safety and validation; canonical branch |
| [gouqizi2214730-source/trading-hub — main, branded KVP](https://github.com/gouqizi2214730-source/trading-hub/tree/50220ba91613161685f84384825d855cb4dbeb76) | `50220ba` | 37 / 0 | Broad execution/data/analysis/chart/editor/alerts/replay product expansion; newest features are post-release source |
| [PANASV/trading-hub — main](https://github.com/PANASV/trading-hub/tree/cdb84ea16eee7ac8d6c1b74136d2eeacedadf834) | `cdb84ea` | 22 / 2 | QuanTAlib, Camarilla, IB market data, Codex, TradingView MCP and instrument discovery; all feature tips integrated |
| [jjuangutierrez/trading-hub — fix-zoom-limit](https://github.com/jjuangutierrez/trading-hub/tree/8ceaf4c43e50edb29e96bfc17ae5c8f19b7e17d9) | `8ceaf4c` | 2 / 3 | Open interest, deeper zoom and specialized flow/profile studies; feature-branch-only |
| [dhamnaik-lab/trading-hub — main](https://github.com/dhamnaik-lab/trading-hub/tree/875fea8465fa6c6bda4fffc6fb628ca7fea25805) | `875fea8` | 14 / 2 | Independent English localization and evidence/installer tooling; one additional phase-9 variant remains feature-only |
| [byfor11/trading-hub — main](https://github.com/byfor11/trading-hub/tree/86d46108c2217793d5e77c301e2bf7437b8a74e6) | `86d4610` | 1 / 3 | Architecture-selectable Mac packaging; feature and main tips have identical complete trees |
| [indigiti/tradedock — translation/english-documentation-ui](https://github.com/indigiti/tradedock/tree/65ae0229bc0b36ba44156cfaaa530607c382ec25) | `65ae022` | 28 / 3 | Partial community English translation and guarded CI; already inherited by ours |

## 1. Our fork: canonical English-first compatibility and safety

Our additional work is not principally a new indicator pack or new broker family. It extends the original application while preserving application/storage identities and consolidating reviewed English, safety and upstream histories.

### Whole contribution catalog

- **English-first presentation and documentation:** broad primary/secondary UI, chart/DOM/footprint controls, safety dialogs, reports, compiler/provider diagnostics, assistant/risk prompts, SDK display defaults and full English guides.
- **Compatibility-aware translation:** legacy account, drawing, feed, ledger and saved-example identities remain recognizable. Generated strategy/indicator code uses English names without casually renaming persistent identities or arbitrary user scripts.
- **Runtime multilingual reconciliation:** English source is canonical; Spanish and Simplified Chinese catalogs retain upstream coverage and reviewed overrides. This is a different architecture from PANASV's Spanish-keyed wrappers and from English-only replacement forks.
- **Order permissions and ownership:** send-time real-order permission checks, reduction/cancellation handling after permission revocation, stable order identity, replacement credential/transport contexts and owned-quantity close boundaries.
- **Broker corrections:** Binance futures conditional-order API support and tracking; Rithmic tag/publication/replacement handling; Tradovate placement ownership, disconnected amendment ordering and acknowledgment handling.
- **Bracket and restart lifecycle:** coordinated protective orders track late fills, cancellation/replacement obligations and recovered ownership rather than treating missing snapshots or command receipts as definitive completion.
- **Process-crash ledger recovery:** checksummed, generation-bound append intents and assigned-byte reconciliation; cross-process leases/deletion generations; explicit pending-fill recovery; unavailable history remains unknown instead of false zero/flat.
- **Desktop lifecycle:** protected Quit/restart behavior, preservation of background strategies, one-time startup across tray reopen, process handoff and argument preservation.
- **Packaging and performance repairs:** stricter Mac signature/copy verification and metadata-preserving installation; historical session-profile replay avoids rebuilding expensive drawing snapshots for every historical bar without removing the indicator.
- **Validation/maintenance:** English/source-identity/catalog gates, isolated UI/safety/quit scenes, fake broker regressions, native platform packaging workflows, original-upstream tracker and reviewed checkpoint ledger.

### Latest delta and evidence boundary

The latest published commit, `17dec28`, changes only the installation-acceptance dashboard relative to `c27e5aa`; it introduces no new runtime feature. PR #5 had already merged the reviewed runtime at `9d5791a`. The maintained installation record now says source `c27e5aa` was installed and accepted on the personal Mac; the previous report's older-installed-runtime statement is superseded by that record. This research did not launch or reinstall it.

There are no public releases in our fork. Authenticated provider, physical-power, arbitrary-extension and successful real-model-output guarantees remain separate. No integrated adapter provides atomic broker-native OCO. Append recovery is process-crash consistency, not power-loss rollback protection or broker-event exactly-once delivery; live ledger prices remain market-price estimates without commissions.

**Sources:** [canonical dashboard](https://github.com/mehmetcanbudak/trading-hub/blob/17dec28f0edbe4f1204b9c9a5d99ea1a6840342c/docs/en/TRANSLATION_STATUS.md), [reviewed safety integration](https://github.com/mehmetcanbudak/trading-hub/pull/5), [ledger transaction/replay implementation](https://github.com/mehmetcanbudak/trading-hub/blob/9d5791a79e9ebfa9641c30d151ffdf21639cc22b/src/TradingHub.Strategies/LivePortfolio.cs#L292-L755), [bracket reconciliation](https://github.com/mehmetcanbudak/trading-hub/blob/9d5791a79e9ebfa9641c30d151ffdf21639cc22b/src/TradingHub.Core/Trading/Brackets.cs#L726-L931).

## 2. KVP: broad product expansion

**Current inspected main:** `50220ba`. **Latest published release:** [v1.2.0.21](https://github.com/gouqizi2214730-source/trading-hub/releases/tag/v1.2.0.21), targeting `68d6359`, with Windows x64/ARM64 installers. Main contains **six later commits**: the Longbridge/aggregate-history commit and five liquidity/structure/keyboard/GPU/documentation commits. These later features must not be attributed to the `.21` installer.

### Capabilities already in v1.2.0.21

| Area | Added implementation |
|---|---|
| Bybit execution | Private V5 unified-account client, demo/testnet/real environments, signed requests/time synchronization, balances/positions/open orders, market/GTC limit/conditional stops, amendment/cancellation, flatten/reverse and reduce-only exits |
| Historical data | Enlarged footprint windows, compressed older downloaded ticks, selectable 1/5/15/60-minute compact history, memory-sensitive raw/compact caps, kline fallback and retention of available history through provider failures |
| Profiles and caches | Profile-row/POC/VA styling, completed-profile disk persistence, reusable candle-series cache, parallel initial aggregation, recent-window price-step suggestion and offline instrument catalog |
| VP Ultimate | Period profiles, buyer/seller POC and value-area controls; multi-period, anchored, previous and closing VWAP levels; delta/absorption bubbles; sessions, key levels, rolling VWAP, robust-volume coloring and double-inside-bar analysis; later naked daily/weekly/monthly/yearly POCs and session VWAP |
| CVD | Line/area/candles/histogram/steps, reset periods, moving average and price/CVD divergence marks |
| Chart navigation/drawings | Rectangle zoom, autoscale/session-line controls, clock/timezone selection, reset view, account/risk-based long/short drawings, anchored VWAP/deviation bands and fixed-range volume/delta profiles |
| Indicator authoring | View embedded built-in source, copy to editable user indicators, rename/delete/recompile; save Pine source and request Claude conversion |
| Notifications | Unified price/indicator/strategy/risk/execution alerts through toast/status/sound/log and optional JSON webhook/Telegram; `Strategy.Alert(message)` |
| Practice replay | Random downloaded instrument/start selection, blind instrument/date hiding, reveal and simulated execution/PnL report |
| Operations/distribution | Relocatable market/app/script storage, proxy controls, connection/startup fixes, Windows updater/installers/release channel and KVP migration/branding |
| Rithmic maintenance | Brief pending-price stabilization around amendment and stale incoming state; this is not our acknowledgment-safe replacement/ledger implementation |

**Entry points:** [Bybit account](https://github.com/gouqizi2214730-source/trading-hub/blob/50220ba91613161685f84384825d855cb4dbeb76/src/TradingHub.Core/Feeds/Bybit/BybitAccount.cs), [compact history](https://github.com/gouqizi2214730-source/trading-hub/blob/50220ba91613161685f84384825d855cb4dbeb76/src/TradingHub.Core/Data/CompactHistory.cs), [VP Ultimate sources](https://github.com/gouqizi2214730-source/trading-hub/tree/50220ba91613161685f84384825d855cb4dbeb76/src/TradingHub.Indicators.Native/VpUltimate), [CVD](https://github.com/gouqizi2214730-source/trading-hub/blob/50220ba91613161685f84384825d855cb4dbeb76/src/TradingHub.Indicators.Native/Cvd.cs), [indicator sources](https://github.com/gouqizi2214730-source/trading-hub/blob/50220ba91613161685f84384825d855cb4dbeb76/src/TradingHub.App/Services/IndicatorSources.cs), [alerts](https://github.com/gouqizi2214730-source/trading-hub/blob/50220ba91613161685f84384825d855cb4dbeb76/src/TradingHub.App/Services/AlertCenter.cs), [replay](https://github.com/gouqizi2214730-source/trading-hub/blob/50220ba91613161685f84384825d855cb4dbeb76/src/TradingHub.App/Views/TradingView.Replay.cs).

### Main-only data expansion after v1.2.0.21

1. **Longbridge stocks/ETFs:** configured HK/US/mainland-China/Singapore symbols; static instrument metadata, quotes, candle history, recent day trades and trade/depth pushes. OAuth registration/PKCE, region/client configuration, token refresh and existing secret-store integration are included. It implements market-data/history/session interfaces, **not an execution account**. Real authorization/entitlements were not exercised. Recent tape is limited to the latest 1,000 day trades; older coverage uses candles.
2. **Aggregated crypto market data:** Binance Futures + Bybit by default, with supported alternate source identities including Binance Spot. It merges trades and kline volume, regenerates IDs and combines price extremes. It is **not consolidated execution**. The displayed DOM is the first venue's book, not a merged order book; partial secondary-source failure can reduce coverage.
3. **Automatic candle history:** incremental current-month cache plus `KlineKeeper` background maintenance. Maintenance is disabled by default; optional opened-instrument tracking is bounded. Requested multi-year lookbacks use progressively coarser bars and do not prove the provider supplies the full range.

**Sources:** [Longbridge feed](https://github.com/gouqizi2214730-source/trading-hub/blob/50220ba91613161685f84384825d855cb4dbeb76/src/TradingHub.Core/Feeds/Longbridge/LongbridgeFeed.cs), [OAuth](https://github.com/gouqizi2214730-source/trading-hub/blob/50220ba91613161685f84384825d855cb4dbeb76/src/TradingHub.Core/Feeds/Longbridge/LongbridgeOAuth.cs), [aggregate feed](https://github.com/gouqizi2214730-source/trading-hub/blob/50220ba91613161685f84384825d855cb4dbeb76/src/TradingHub.Core/Feeds/AggregateFeed.cs), [automatic history](https://github.com/gouqizi2214730-source/trading-hub/blob/50220ba91613161685f84384825d855cb4dbeb76/src/TradingHub.App/Services/KlineKeeper.cs).

### Additional main-only work observed during finalization

| Area | Added implementation | Boundary |
|---|---|---|
| Recorded depth | Per-instrument sampled book history, daily `.liq` files under `Liquidez`, recent-memory reload and SDK depth samples | Received depth snapshots, not every order-book event; default minimum sample spacing is three seconds, default 100 buckets/side and 12-hour memory window |
| Liquidity heatmap | Optional layer behind candles, opacity/pixel-time grouping and relative color intensity | Resting-book size, not traded/aggressor volume; maximum-per-cell display, not time-weighted mean |
| Orderbook Delta | Bid-minus-ask, percentage imbalance, separate sides or bar-to-bar book change; average/last/max samples and distance-from-best control | Different from trade CVD; bars without snapshots carry forward prior bid/ask values |
| SMC/ICT structure | Swings, BOS/CHoCH, order blocks/breakers, FVG/mitigation, equal-high/low groups/sweeps and CISD; higher-timeframe candles/FVG/PO3/T-Spot/STDV projections | OHLC-derived, revisable pattern analysis; inferred structure is not measured book liquidity or proven non-repainting trade signals |
| Additional studies | Triple mapped-timeframe Bollinger bands and VWAP Claire rolling/anchored lines | Claire uses chart-bar typical-price/volume and available day buckets, not raw-trade VWAP or a strict calendar-duration window |
| External VPFM launcher | Finds/launches a separate Windows VPFM executable or opens its release page | Not embedded VPFM; external derivatives/options/GEX modules are not thereby implemented in KVP |
| Keyboard chart entry | Typed symbol/period popup, Enter/Escape behavior, current/other-feed search and shortcuts sheet | Chart navigation, not keyboard order execution; discovery depends on available catalogs |
| GPU price binning | ILGPU 1.5.3, CUDA-first/OpenCL-second selection, device/status display, off setting and CPU fallback | Production callsite is fixed-range-profile drawing price binning, not all indicators/rendering/backtests; no Metal backend or Apple Silicon GPU proof |
| Documentation/localization | English/Chinese entries and guides covering the new data, heatmap, structure, keyboard and GPU features | Source/docs presence is not executed readiness evidence |

**Sources:** [late exact delta](https://github.com/gouqizi2214730-source/trading-hub/compare/121dcbbc5aa253ce52c95e3e6b72eadaa5b290ad...50220ba91613161685f84384825d855cb4dbeb76), [liquidity history](https://github.com/gouqizi2214730-source/trading-hub/blob/50220ba91613161685f84384825d855cb4dbeb76/src/TradingHub.Core/Data/LiquidityHistory.cs), [heatmap renderer](https://github.com/gouqizi2214730-source/trading-hub/blob/50220ba91613161685f84384825d855cb4dbeb76/src/TradingHub.Charting/Rendering/LiquidityHeatmapRenderer.cs), [book delta](https://github.com/gouqizi2214730-source/trading-hub/blob/50220ba91613161685f84384825d855cb4dbeb76/src/TradingHub.Indicators.Native/OrderbookDelta.cs), [structure studies](https://github.com/gouqizi2214730-source/trading-hub/tree/50220ba91613161685f84384825d855cb4dbeb76/src/TradingHub.Indicators.Native/Structure), [GPU implementation](https://github.com/gouqizi2214730-source/trading-hub/blob/50220ba91613161685f84384825d855cb4dbeb76/src/TradingHub.Core/Compute/GpuCompute.cs), [keyboard entry](https://github.com/gouqizi2214730-source/trading-hub/blob/50220ba91613161685f84384825d855cb4dbeb76/src/TradingHub.App/Views/TradingView.QuickEntry.cs).

### Integration risks and important distinctions

- **Bybit scope/protection:** the client is linear/UTA and one-way (`positionIdx=0`), advertised for USDT, but the public instrument filter does not explicitly enforce USDT settlement. Reduce-only `Exit` does not make every live-strategy protective order reduce-only; local sibling cancellation is not atomic OCO. Do not substitute this older execution path for our reviewed acknowledgment/ledger logic.
- **Raw, compact and synthetic history differ:** compact rows lose original internal timing/price detail; kline backfill creates synthetic trades. Missing buy volume is estimated from candle shape by default. Aggregate klines can mark estimated buy volume as available, weakening provenance distinctions. CVD/profile/VWAP labels do not recover missing microstructure.
- **KVP migration is not cosmetic:** startup moves old `Trading Hub` application-data/Documents directories to `KVP` when the destination is absent; it does not merge two existing trees and swallows failures. Installer/uninstall/executable compatibility identities can also collide. [Migration source](https://github.com/gouqizi2214730-source/trading-hub/blob/50220ba91613161685f84384825d855cb4dbeb76/src/TradingHub.Core/Common/Product.cs#L7-L36).
- **Book recording adds continuing I/O:** enabled independently of heatmap visibility. Persistence requires live recording; snapshots are sampled/coarsened and recent loading is relative to current UTC. Arbitrary historical-day depth replay is not added. There is memory retention but no disk-age pruning in this class; write failures are swallowed. Aggregate-feed heatmaps still see only the first venue's DOM.
- **Three meanings of liquidity must not be conflated:** trade CVD may contain inferred/estimated aggressors; book delta is sampled resting depth; SMC liquidity pools are OHLC-pattern inferences.
- **GPU exactness/performance is not proven:** volumes move from decimal accumulation to double GPU/parallel sums and back. Dense arrays cover the entire tick span; over 50 million ticks returns no profile. Large allocations remain possible below that cap. Authored backend tests are not benchmarks or proof of decimal-exact equivalence on the user's hardware.
- **AI/external effects:** Pine import requests conversion, not a Pine runtime/transpiler. Webhook/Telegram alerts disclose configured event data and use best-effort delivery, not a durable acknowledged queue. Optional custom webhook headers are ordinary settings data, unlike the separately protected Telegram token.
- **Replay is not isolated run accounting:** result PnL is a difference in shared portfolio realized PnL, so concurrent simulated activity can contaminate it. Blind mode is UI hiding, not an information-security boundary.
- **Structure outputs can revise:** confirmed pivots require future neighboring bars; calculations rebuild a bounded recent window and include forming bars. HTF alignment and 30-day month mappings differ from exchange-calendar sessions. Do not generalize closed-HTF Bollinger behavior into a blanket non-repainting claim.
- **Release trust:** the updater targets this fork's Windows release channel and checks SHA-256 only when GitHub supplies the digest. Release files were not downloaded or executed here. Provider entitlements, OAuth, GPU compatibility and actual delivery remain unverified.

**Relationship to ours:** Bybit execution, compact history, the specialized analysis/drawings/editor/replay/alerts/cache features and new provider/depth/GPU work are genuine capabilities to evaluate. Common English UI, Windows support, basic profiles and credential/protection concerns overlap conceptually. None of that proves KVP adopted our safety implementation. Keep our identity, storage and order-control boundaries; do not wholesale-import KVP product/installer/data migration or its older broker/strategy engine.

## 3. PANASV: indicators, IB data and alternative assistant engines

All 11 published feature tips occur in `main@cdb84ea`; there is no additional unmerged feature-only tip. No PANASV branch/tag changed during this refresh. Its older eight-commit advance is not newly delivered in this snapshot.

### Whole contribution catalog

| Area | Added implementation |
|---|---|
| QuanTAlib | Package 0.8.12 integration; chart wrappers for SMA/EMA/WMA/HMA/ALMA/JMA/KAMA/T3/ZLEMA/DEMA/TEMA/VIDYA/Ehlers SuperSmoother; configurable three-line ribbon; forming-bar/new-bar adapter |
| Camarilla | Custom prior-session PP and H1–H6/L1–L6; chart/custom/CME session selection and per-session line breaks; separately implemented chart/strategy calculations |
| Strategy API | `Qt(...)`, underlying indicator/readiness access, `Camarilla()` series/level access and compiler `Q = QuanTAlib` alias |
| IB CSV/offline history | Folder/CLI import, CME metadata/session/backtest integration, sorting/deduplication, zero-volume filtering and reusable stored-history feed |
| IB live/history | Custom TWS/Gateway TCP client; supported CME index-futures contracts; historical TRADES bars, local-history reuse/backfill, tick-by-tick trades/BBO, depth fallback and reconnect/error handling |
| Localization | Independent Spanish-keyed English/Chinese layer, user dictionary overrides, automatic macOS language detection, Chinese-capable rendering/font and number/date/report/template localization; maintenance tooling |
| Default chart presentation | Candles, TradingView-style colors, default EMA9/EMA60/WMA200 ribbon, initial stored MNQ one-minute chart where available and simulator fallback |
| Claude/Codex | Shared assistant bridge, engine/model/reasoning selection, custom model IDs, remembered sessions/engine histories, CLI discovery/streamed events/cancellation; local laboratory integration |
| TradingView MCP | Optional remote server configuration/authentication helpers for both assistants, tool activity display and research-oriented prompts |
| Instrument discovery | When a selected connection has no matches, show suggestions from other enabled data profiles and allow switching the picker connection |

Feature branches cover `quantalib`, `ib-import`, `i18n`, `lang-auto`, `default-candles`, `default-mnq`, `ai-engines`, `ib-probe-i18n`, `ib-live`, `tradingview-mcp` and `picker-search`. They are cumulative checkpoints, not additive feature totals.

### Dependencies and limits

- **IB is market data, not order execution.** It needs authenticated TWS/Gateway, socket API, client-ID separation and appropriate market/depth entitlements. The custom protocol negotiates versions 100–187; actual server-version interoperability was not exercised. Live aggressor side is inferred; historical bars become synthetic trades. Contract selection follows calendar rolls, not volume-based discovery.
- **Import can overwrite detail:** importing writes complete affected UTC days with replacement. Reimporting OHLCV may replace finer recorded ticks. CSV parsing is simple rather than general quoted CSV; continuous-contract price adjustment is not supplied.
- **QuanTAlib is not an all-indicator GUI port:** the chart catalog wraps the MA family plus custom Camarilla; strategy adapters are broader but single-value-oriented. Decimal/double conversions, warm-up behavior and separate Camarilla formulas need parity review. The source's draw-readiness condition is broader than documentation's `IsHot` description.
- **Codex is a real separate engine seam, not universal AI replacement:** it uses the user's installed/authenticated CLI, workspace-write and never-ask approval configuration. Risk-agent execution remains Claude-only. Suggested model names do not establish entitlement; remote MCP authority is separate from shell sandbox intent.
- **TradingView is not enforced read-only:** server-namespace access is configured, while mutation restrictions are prompt prose. It is neither a TradingView chart feed nor an execution route. Fork-stated plan/bar/rate limits were not independently verified against the service.
- **Localization is an alternative mechanism:** Spanish keys and `zh` differ from our English-canonical/`zh-CN` catalogs. Entire strategy/HTML templates are wrapped by its formatter, so code-generation/export syntax and stable identifier compatibility need explicit review. Default workspace changes should not overwrite saved user preferences.
- **Search is bounded discovery:** only enabled market-data profiles, cached/limited-time retrieval and no-match fallback; not global broker discovery or automatic account routing.

**Relationship to ours:** IB market data/import, QuanTAlib/Camarilla adapters, Codex, remote TradingView MCP and cross-profile suggestions are genuinely additional mechanisms. Three-language UI, OS-language choice, Claude chat and the laboratory already exist generally in ours; replacing their architecture is not required to obtain the new features.

**Sources:** [complete original comparison](https://github.com/PANASV/trading-hub/compare/1d5a774d3d1bf2baf9259597234d227daad915a9...cdb84ea16eee7ac8d6c1b74136d2eeacedadf834), [QuanTAlib/Camarilla](https://github.com/PANASV/trading-hub/blob/cdb84ea16eee7ac8d6c1b74136d2eeacedadf834/src/TradingHub.Indicators.Native/QuanTAlib/QtIndicators.cs), [strategy adapters](https://github.com/PANASV/trading-hub/blob/cdb84ea16eee7ac8d6c1b74136d2eeacedadf834/src/TradingHub.Strategies/QuanTAlibSeries.cs), [IB importer](https://github.com/PANASV/trading-hub/blob/cdb84ea16eee7ac8d6c1b74136d2eeacedadf834/src/TradingHub.Core/Data/IbBarImport.cs), [IB feed](https://github.com/PANASV/trading-hub/blob/cdb84ea16eee7ac8d6c1b74136d2eeacedadf834/src/TradingHub.Core/Feeds/InteractiveBrokers/IbFeed.cs), [Codex bridge](https://github.com/PANASV/trading-hub/blob/cdb84ea16eee7ac8d6c1b74136d2eeacedadf834/src/TradingHub.App/Services/CodexBridge.cs), [MCP permissions](https://github.com/PANASV/trading-hub/blob/cdb84ea16eee7ac8d6c1b74136d2eeacedadf834/src/TradingHub.App/Services/ClaudeCode.cs#L456-L540), [picker](https://github.com/PANASV/trading-hub/blob/cdb84ea16eee7ac8d6c1b74136d2eeacedadf834/src/TradingHub.App/Controls/InstrumentPicker.cs#L275-L350).

## 4. jjuangutierrez: OI and specialized flow/profile studies

All added work is on `fix-zoom-limit@8ceaf4c`; its default main contains no additional work. Both contribution commits and all 13 changed files were accounted for. No head changed during this refresh.

### Whole contribution catalog

- **Live Binance Futures OI:** periodic REST polling and a new sink/data/chart propagation path.
- **Historical Open Interest indicator:** total OI line or signed-change histogram, Binance five-minute historical samples and current live OI.
- **Deeper zoom-out:** minimum candle width changes from 1.5 to 0.1, without a corresponding aggregation/performance redesign.
- **Anchored Flow VWAP:** manual time, confirmed swing or volume-spike anchors; volume/delta/price-volume weighting, deviation bands, fills and flow bubbles.
- **Refill/absorption zones:** high-volume buckets classified using aggression, candle position and close; neutral/mitigated zone controls and projections. Optional estimated footprint fallback.
- **Multi-session VP:** day/week/month profiles, value-area controls, optional AMT-style composite merging, projected prior untested POCs and tested-POC display controls.
- **Restricted-folder resilience:** catches selected directory/example/enumeration failures, nullable watcher and compiler-read retry behavior; adds indicator documentation.

### What is actually new versus ours

OI, specialized anchored Flow VWAP, refill heuristics, month/composite profile behavior and the larger zoom range are additional. Basic session/week/visible volume profiles, volume/delta/bid-ask modes, value area and POC-until-touch already exist in ours. Several watcher/read-failure protections also overlap; the initial directory-creation catches are an additional narrow difference.

KVP's VWAP/profile/zoom families overlap semantically with this fork, not necessarily through identical code or shared ancestry. They should be compared as alternative implementations, not counted twice as independent missing capability categories.

### Limits

- OI event delivery mutates the chart path directly from the feed callback; the anonymous chart subscription has no corresponding added removal. **Potential thread/lifetime problems are source-based inferences, not reproduced failures here.**
- Historical OI is Binance-specific, fetched independently by the indicator, with swallowed errors and finite coverage. Candles before the first sample receive that first sample rather than an unavailable value, risking future information appearing on older history.
- Confirmed-swing anchoring rewrites values from the pivot; document retrospective calculation rather than treating it as an available-at-the-time trading signal.
- Refill labels are footprint/OHLC heuristics, not observed order-book replenishment or proven iceberg activity. Estimated fallback uniformly distributes candle values.
- VP calendar grouping differs from our session-aware implementation; performance under deeper visible history was not measured.
- Spanish labels need English-canonical localization without casually renaming saved indicator types/properties/series.

**Sources:** [complete comparison](https://github.com/jjuangutierrez/trading-hub/compare/1d5a774d3d1bf2baf9259597234d227daad915a9...8ceaf4c43e50edb29e96bfc17ae5c8f19b7e17d9), [OI](https://github.com/jjuangutierrez/trading-hub/blob/8ceaf4c43e50edb29e96bfc17ae5c8f19b7e17d9/src/TradingHub.Indicators.Native/OpenInterest.cs), [Flow VWAP](https://github.com/jjuangutierrez/trading-hub/blob/8ceaf4c43e50edb29e96bfc17ae5c8f19b7e17d9/src/TradingHub.Indicators.Native/FlowVwapAnclada.cs), [refill zones](https://github.com/jjuangutierrez/trading-hub/blob/8ceaf4c43e50edb29e96bfc17ae5c8f19b7e17d9/src/TradingHub.Indicators.Native/ClusterSearchRefill.cs), [VP](https://github.com/jjuangutierrez/trading-hub/blob/8ceaf4c43e50edb29e96bfc17ae5c8f19b7e17d9/src/TradingHub.Indicators.Native/VP.cs).

## 5. dhamnaik-lab: independent English localization and evidence tooling

Main `875fea8` contains its consolidated localization, release-validation variant and warning maintenance. All 12 published branches were inspected; no head changed during this refresh. Histories overlap and contain squash integrations, so their commit counts are not additive.

### Whole contribution catalog

- English primary README/onboarding, translated existing documentation paths and technical/setup/privacy/Windows guides.
- Primary/secondary UI, shared controls/dialogs, real-account/data-cost warnings, chart/DOM/footprint/drawing text and provider guidance.
- English analytical-report headings/explanations/chart labels and report UI; not a new analytics engine.
- SDK default display overrides preserving generated resource keys and custom override precedence; native-indicator/property metadata.
- English templates/generated display text with selected legacy example identities and footprint presets preserved; CSV/export identities retained.
- Claude/laboratory/risk-agent prose and diagnostics preserving schema/tool/role/order-comment identities; not a new assistant provider.
- Localization/compatibility tests, Windows fake-listener fixture maintenance, Markdown path/anchor checks, residual-Spanish inventory and synthetic UI/footprint/DOM captures.
- English NSIS/PowerShell packaging text and x64/ARM64 installer-candidate workflow with hash/signature/source manifest and disposable x64 silent install/uninstall/data-preservation checks.
- Workflow-action upgrades and two warning fixes; not new trading/indicator algorithms.

### Feature-only phase-9 variant

`feat/english-localization-phase9@e0a48f3` is distinct from merged `feat/english-release-validation-phase9@eaebd0d`:

- Seven replacement English documentation screenshots and synthetic Claude conversations.
- Further Claude labels, subscription/help/login/compilation text and human-approved sizing prompt.
- A screenshot generator that commits/pushes generated images back to the internal feature branch, requiring `contents: write`.
- An alternative installer workflow/checklist.

Main instead retains historical screenshot provenance and the more conservative installer-validation workflow. Do not call both phase-9 variants fully merged.

### Relationship to ours and limits

This is independent Git history, but English UI/docs/reports/SDK/generators/prompts and native-platform validation **mostly overlap capabilities we already have**. Distinct useful artifacts are its categorized Spanish inventory, candidate hash/signature manifests and curated screenshot workflow. They are evidence/documentation differences, not missing brokerage functions.

Its README/checklist leaves translation/runtime release work open. The Spanish inventory is heuristic, not exhaustive or a strict source-identity guard. Main's installer smoke runs x64 on an x64 host; it does not prove ARM64 GUI, macOS or broker behavior. The branch-writing screenshot workflow needs a permission/branch-policy review. Replacing our localization wholesale would regress our canonical catalogs, safety integration and stricter compatibility review.

**Sources:** [main comparison](https://github.com/dhamnaik-lab/trading-hub/compare/1d5a774d3d1bf2baf9259597234d227daad915a9...875fea8465fa6c6bda4fffc6fb628ca7fea25805), [SDK override implementation](https://github.com/dhamnaik-lab/trading-hub/blob/875fea8465fa6c6bda4fffc6fb628ca7fea25805/src/TradingHub.Sdk/Localization/Strings.cs), [residual inventory](https://github.com/dhamnaik-lab/trading-hub/blob/875fea8465fa6c6bda4fffc6fb628ca7fea25805/tools/audit_spanish_literals.py), [main installer validation](https://github.com/dhamnaik-lab/trading-hub/blob/875fea8465fa6c6bda4fffc6fb628ca7fea25805/.github/workflows/english-windows-installer-validation.yml), [main-to-feature phase-9 difference](https://github.com/dhamnaik-lab/trading-hub/compare/875fea8465fa6c6bda4fffc6fb628ca7fea25805..e0a48f3cb9a14897f82e32d481bdc67635139d7b), [feature screenshot writer](https://github.com/dhamnaik-lab/trading-hub/blob/e0a48f3cb9a14897f82e32d481bdc67635139d7b/.github/workflows/english-doc-screenshots.yml).

## 6. byfor11: Intel-selectable Mac packaging

Main `86d4610` and retained feature tip `ae626b4` have identical complete tree `7eb4dd23abbc71ef84181e2d5ae4af35d642f83c`. This is one contribution, not two. No head changed during the refresh.

### Whole contribution catalog

- Detects `arm64` and selects `osx-arm64`; every other detected architecture falls back to `osx-x64`.
- Allows caller `RID` override, then local build-environment override, then automatic selection.
- Publishes one self-contained Mac architecture per invocation and prints the target.
- Updates installation/development/Claude-assisted guidance to mention Intel as well as Apple Silicon.
- Keeps the same bundle identity, Mac application layout and declared macOS 12 minimum. Adds no Windows implementation.

**Relationship to ours:** selecting a Mac Intel RID is additional; our Mac packager still targets Apple Silicon. Existing self-contained packaging is not new. This is not a universal binary, cross-host packaging system or demonstrated Intel/minimum-OS compatibility.

Do not replace our repaired installer script with its older full script: ours has version derivation and stricter signing/copy verification/metadata preservation. The old script retains swallowed signing/copy failures and destructive replacement behavior inherited from its baseline. Only the bounded architecture-selection idea is new.

**Sources:** [complete comparison](https://github.com/byfor11/trading-hub/compare/1d5a774d3d1bf2baf9259597234d227daad915a9...86d46108c2217793d5e77c301e2bf7437b8a74e6), [build script](https://github.com/byfor11/trading-hub/blob/86d46108c2217793d5e77c301e2bf7437b8a74e6/build-app.sh), [identical-tree comparison](https://api.github.com/repos/byfor11/trading-hub/compare/86d46108c2217793d5e77c301e2bf7437b8a74e6...ae626b4c0ceb9598cdfed0e14c00bec49ba4a231).

## 7. indigiti/TradeDock: incorporated translation ancestor

All additional work remains on `translation/english-documentation-ui@65ae022`, not its unchanged main. No branch/tag changed during the refresh.

### Whole contribution catalog

- English README/documentation entry points while retaining Spanish originals.
- Seventeen condensed guide/index files under `docs/en`.
- Partial literal translation across 20 application C# UI files, backed by a 604-entry glossary.
- English safety/provider/live-resume wording without deliberately adding order controls or changing routes.
- Reviewed-literal source-substitution guard, English-link checks and incomplete residual-language reporting.
- macOS .NET build/regression/headless-scene workflow with screenshot artifacts.
- Dashboard-first review/draft release discipline.

Its own dashboard remains explicitly partial/blocked: dynamic labels, SDK language, full guide parity and manual/runtime/provider acceptance remain unfinished. This branch does not supply our later SDK/report/generator/risk-prompt completion or native Windows acceptance infrastructure.

**Already incorporated:** all 28 translation commits are ancestors of our canonical branch. Exact `65ae022...17dec28` comparison is ahead 56 / behind 0, merge base `65ae022`. This is actual lineage, not semantic similarity. There is no newly missing feature to re-import; retain attribution and distinguish its historical blocked status from our later state.

**Sources:** [complete contribution comparison](https://github.com/indigiti/tradedock/compare/9ee74a4c527ac43120423acd41e006ec9d7877ad...65ae0229bc0b36ba44156cfaaa530607c382ec25), [blocked dashboard](https://github.com/indigiti/tradedock/blob/65ae0229bc0b36ba44156cfaaa530607c382ec25/docs/en/TRANSLATION_STATUS.md), [checker](https://github.com/indigiti/tradedock/blob/65ae0229bc0b36ba44156cfaaa530607c382ec25/tools/check_english_translation.py), [ancestry proof](https://github.com/mehmetcanbudak/trading-hub/compare/65ae0229bc0b36ba44156cfaaa530607c382ec25...17dec28f0edbe4f1204b9c9a5d99ea1a6840342c).

## Cross-fork interpretation

| Classification | Findings |
|---|---|
| Actual inherited contribution | TradeDock's complete 28-commit translation is in ours |
| Alternative implementations of existing capabilities | Dham's English localization; PANASV's three-language system/Claude/laboratory concepts; portions of KVP/Jjuan profiles and packaging |
| Additional data/execution capabilities to evaluate | KVP Bybit execution; PANASV IB market data/import; KVP Longbridge/aggregate data and automatic history |
| Additional analysis/navigation capabilities to evaluate | KVP VP/CVD/drawings/replay/heatmap/book/structure/keyboard tools; PANASV QuanTAlib/Camarilla; Jjuan OI and specialized flow/profile studies |
| Additional assistant/automation mechanisms | PANASV Codex/remote MCP; KVP source/Pine workflow and external alerts |
| Bounded platform/tooling differences | Byfor Mac-RID selection; Dham evidence manifests/screenshots/inventory; KVP cache/GPU changes with explicit fidelity/platform limits |

For the selected work heads of KVP, PANASV, Jjuan, Dham and Byfor, none of their original-relative extra commit IDs is shared with ours. This establishes independent contribution history, **not absence of semantically equivalent code or cherry-picked logic**. Duplicate feature concepts and identical/squashed trees must not be counted as separate missing product capabilities.

The report identifies candidates and boundaries, not an authorization to merge, migrate data, enable real orders, expose external tools or run arbitrary extensions. Any later integration should preserve canonical persistent identities, English-source catalogs, protected order ownership, crash/recovery semantics and current safety confirmations. No application/source change or integration was performed by this research; this Markdown report is the sole repository artifact created.

## Later selected adoption — external worktree only, 2026-11-04

The preceding report remains the original source-only research snapshot, not a statement that canonical or installed software was changed. A read-only copy was brought into the separate `feat/non-kvp-adoption` execution worktree at base `17dec28f0edbe4f1204b9c9a5d99ea1a6840342c`; the canonical user-owned report is unchanged. That later work is uncommitted, unmerged and not installed. Selected PANASV/jjuangutierrez adaptations cover instrument search/deep zoom, QuanTAlib/Camarilla, flow/profile/OI studies, IB **data-only** history/import and optional Codex/approved TradingView research. Source was adapted to preserve existing identities, order controls and explicit fidelity boundaries, not imported wholesale or certified equivalent.

TradeDock is already inherited; Dham runtime and Intel packaging imports are not selected. KVP remains explicitly deferred/last. The [validation dashboard](../en/TRANSLATION_STATUS.md#unmerged-non-kvp-adoption--2026-11-04) records the final zero-warning/error Release build, exact-catalog complete suite (663 discovered: 656 passed, zero failed, seven existing Windows-only skips), actual fake-provider UI/CLI, real English/Spanish study-settings consumers and both 3,300-entry provenance/current-candidate catalog gates. Actual build and publish contain all ten license/index/notice files byte-identical to source; the published App assembly matches too. These are unmerged C#-only worktree results, not installation, signing/notarization, distribution approval, paid-model success, native/real-broker or OAuth interoperability.

Every prior failed checkpoint remains historical evidence: initial seven full-suite failures, both durable-ledger writer-checkpoint timeouts, external APFS-image/HDD watchdog failure, temp-only cold-compilation timeout, initial SSD missing-repository-context failure, initial catalog extraction timeout and Spanish settings' incidental English-name pins. Controlled byte-identical owned-SSD execution with exact read-only repository/provenance context passed without relaxing watchdogs, concurrency, financial assertions or existing platform skips; the original ledger/Binance timing cause remains unproven. Research's 250 ms content monitor and owned shutdown/drain have actual RED/GREEN subprocess evidence plus two permanent regression facts. [Third-party notices](../../THIRD_PARTY_NOTICES.md) retain both community source pins and full applicable texts for all five restored dependency families. English, Spanish and Chinese each passed the same actual 185-check six-study settings-consumer scene. The final full English checker passed (22 documents, 228 local links, 16 guide-section checks; 216 source files, 13,165 tokens, 111 compatibility exceptions, zero failures; `english-source-docs-final.receipt.json`). Owned SSD/fake fixtures were owner-verified and removed after proof; external receipts/TRX/PNGs/publish remain. Real provider/platform/power-loss/exactly-once and arbitrary-code sandbox boundaries remain outside these finite proofs; this is not an installed or release-certified application.

## Canonical integration supersession — 2026-11-04

The original research snapshot through “Cross-fork interpretation” above remains intact. Its eight-line external-worktree adoption appendix is also retained as a historical checkpoint, not a current unmerged-state claim. The original user report and advisor plans were preserved in `7593166284551a8b29c9b0d5148f6e85f6040473`; selected non-KVP feature commit `041b4a886e9232f9126bd913522b27fc0c97bd1d` is merged into canonical `memoshi` at `b730d4c144b69f4a2695b5bdefd572dab6014369` and published to `origin/memoshi`. Fresh canonical gates passed: zero-warning/error Release build, complete 656/0/7 suite (663 total), eight actual isolated UI processes, full English/source/docs checker, both catalog gates and actual publish/ten-file notice identity. See the dashboard for exact receipts and boundaries. A later separately user-authorized personal Mac installation accepted source `7d990e8cac15da3038d238a15225f39ae3e546ca`; its [actual installed-app proof](../en/TRANSLATION_STATUS.md#personal-mac-installation--2026-11-04) is separate from the canonical fake-fixture gates.

Selected PANASV/Jjuan adaptations are the scope enumerated in that appendix and the [dashboard](../en/TRANSLATION_STATUS.md#canonical-non-kvp-integration--2026-11-04). TradeDock's complete 28-commit translation is actual inherited lineage, not a pending import. Dham's optional screenshots and hash/signature/source manifests are documentation/evidence artifacts, not missing runtime; no Dham runtime or Intel packaging was imported. No KVP code was imported; the [later source-only KVP review](kvp-adoption-review.md) supersedes the original `.21` metadata without approving adoption.

Historical external passes/failures remain distinct from fresh canonical results. Canonical notice normalization preserves all copyright/license prose and internal blanks while normalizing CRLF and one extra terminal blank; the earlier CRLF-only external receipt is historical. Fresh canonical source/build/publish notice copies are byte-identical. Authorized obsolete C# local/remote branches and auxiliary worktrees were pruned after verified 28-ref preservation; historical main, Rust/Swift worktrees/branches/pins, ledger and user stores remain unchanged. See the dashboard for canonical proof and the [port handoff](non-kvp-canonical-handoff.md) for exact-SHA review without automatic rebasing, pin changes or resumed implementation.
