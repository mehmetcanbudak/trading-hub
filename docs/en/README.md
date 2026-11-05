# Trading Hub — English documentation

[← Main README](../../README.md) · [Spanish documentation](../README.es.md)

Full English guides for this fork's canonical C# branch, `memoshi`, covering Apple Silicon macOS and implemented Windows 10/11 x64/ARM64 support. English is canonical; runtime UI, reports and assistant prose can use English, Spanish or Simplified Chinese. Generated C#/sample identifiers stay English. Spanish folder names and API/storage identifiers are deliberately preserved; existing personal and third-party content is not rewritten. Automated native Windows installation and UI evidence is recorded in the dashboard; real/demo brokerage execution and physical macOS lifecycle/power validation remain separate release gates.

| Area | Guide |
|---|---|
| Setup | [Installation](instalacion.md), [Getting started](primeros-pasos.md), [Install with Claude](instalar-con-claude-code.md) |
| Markets | [Connections](conexiones.md), [Charts](graficos.md), [Trading](operar.md), [Indicators](indicadores.md), [Data](datos.md) |
| Automation | [Strategies](estrategias.md), [Backtesting](backtesting.md), [Live strategies](en-vivo.md), [Claude](asistente-claude.md) |
| Reference | [Settings](ajustes.md), [Privacy](privacidad.md), [Development](desarrollo.md), [Windows installation/platforms](portar-a-windows.md) |

**Trading risk:** Backtests do not predict future results. Practice in simulation before using a live brokerage account. Source code is publicly available under MIT with the Commons Clause; commercial redistribution and related paid services are restricted.

## Translation progress

See the [localization validation dashboard](TRANSLATION_STATUS.md) for verified checks, compatibility exceptions and untested areas. Use [Upstream synchronization](upstream-sync.md) for daily original-repository monitoring, the reviewed checkpoint and the safe merge/translation checklist.

## Selected non-KVP worktree additions

The [dashboard](TRANSLATION_STATUS.md#canonical-non-kvp-integration--2026-11-04) separates the accepted canonical non-KVP integration, historical candidate receipts and the selected KVP capabilities. Start with [search/zoom](graficos.md), [additional studies](indicadores.md#selected-additional-native-studies), [IB imports and OI fidelity](datos.md#interactive-brokers-bars-and-offline-history), [optional assistant/research](asistente-claude.md#optional-strategy-chat-engine-and-isolation) and [reproduction](desarrollo.md#selected-non-kvp-reproduction). [Third-party notices](../../THIRD_PARTY_NOTICES.md) retain community provenance and full dependency terms.

## Selected KVP capability candidate

The complete selected KVP scope has passed [source/local/fake acceptance](TRANSLATION_STATUS.md#kvp-capability-adoption-candidate--2026-11-04); [canonical promotion and personal Mac installation](TRANSLATION_STATUS.md#canonical-kvp-promotion--2026-11-05) have separate receipts. Read [history/depth fidelity](datos.md#raw-compact-and-candle-history), [expanded studies](indicadores.md), [chart controls and isolated practice](graficos.md), [providers and execution limits](conexiones.md), [opt-in storage/network/alerts](ajustes.md) and [safe reproduction](desarrollo.md#selected-kvp-integration-reproduction). Existing identities and startup protections are retained. KVP branding/migration, Windows distribution/VPFM and unmeasured GPU acceleration are excluded; real-provider/native release gates remain separate.
