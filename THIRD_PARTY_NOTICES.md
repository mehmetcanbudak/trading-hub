# Third-party notices and selected adoption provenance

Trading Hub is source-available under [MIT with the Commons Clause](LICENSE), copyright © 2026 ralphtradesz and Trading Hub contributors. This does not relicense separately licensed libraries under the Commons Clause. Retain the full application license and the bundled texts below when distributing these selected additions. This index supplements, not replaces, existing dependency notices.

## Community source adaptations

Selected chart/search, indicators, Interactive Brokers data/import and assistant/research adaptations originate from [PANASV/trading-hub](https://github.com/PANASV/trading-hub/tree/cdb84ea16eee7ac8d6c1b74136d2eeacedadf834), pin `cdb84ea16eee7ac8d6c1b74136d2eeacedadf834`, and [jjuangutierrez/trading-hub](https://github.com/jjuangutierrez/trading-hub/tree/8ceaf4c43e50edb29e96bfc17ae5c8f19b7e17d9), pin `8ceaf4c43e50edb29e96bfc17ae5c8f19b7e17d9`. Credit PANASV, jjuangutierrez, ralphtradesz and Trading Hub contributors. Their MIT plus Commons Clause application-source attribution is retained; adaptations are not claims of exact behavioral equivalence. TradeDock translation provenance is already inherited. KVP remains deferred; Dham runtime and Intel imports are not selected.

Pinned community license texts are retained verbatim for [PANASV](licenses/PANASV-TradingHub-LICENSE.txt) and [jjuangutierrez](licenses/jjuangutierrez-TradingHub-LICENSE.txt), including the original copyright and Commons Clause condition.

## Restored library families

| Package | Version | Copyright / license | Pinned source and bundled terms |
|---|---|---|---|
| QuanTAlib | 0.8.12 | Copyright 2024 Miha Kralj; Apache-2.0 | `mihakralj/QuanTAlib` at `d1a5d96d2ed912604c32f8dcaca896720d33f1b7`; [full license](licenses/Apache-2.0-QuanTAlib.txt) |
| ModelContextProtocol.Core | 2.3.1 | © Model Context Protocol a Series of LF Projects, LLC.; Apache-2.0 | `modelcontextprotocol/csharp-sdk` at `61420937cbef3eab895cafc8a89418811fb71f54`; [full license](licenses/Apache-2.0-MCP.txt), [source notices](licenses/MCP-THIRD-PARTY-NOTICES.txt) |
| Microsoft.Extensions.AI.Abstractions | 10.11.0 | © Microsoft Corporation. All rights reserved.; MIT | `dotnet/extensions` at `25f07afa51034e37b334210642d7dc5f191c142d`; [full license](licenses/Microsoft-MIT.txt), [source notices](licenses/Microsoft-Extensions-THIRD-PARTY-NOTICES.txt) |
| Microsoft.Extensions.Logging.Abstractions | 10.0.13 | © Microsoft Corporation. All rights reserved.; MIT | `dotnet/dotnet` at `4099f83977f4d29604a9aeb971fb0a7bc1d5f699`; [full license](licenses/Microsoft-MIT.txt), [full restored notices](licenses/Microsoft-Logging-DI-THIRD-PARTY-NOTICES.txt) |
| Microsoft.Extensions.DependencyInjection.Abstractions | 10.0.13 | © Microsoft Corporation. All rights reserved.; MIT | Same dotnet pin and [full license](licenses/Microsoft-MIT.txt); [full restored notices](licenses/Microsoft-Logging-DI-THIRD-PARTY-NOTICES.txt) also apply to this Logging transitive dependency |

Authority is each exact-version NuGet nuspec and the pinned source distribution, not a moving project homepage. QuanTAlib's pinned recursive source inventory contains LICENSE and no NOTICE; MCP includes the bundled third-party notice; the extensions root includes the bundled notice. Logging and DI restore identical complete notice texts (SHA-256 `6d15e10a101c6bfff2ab4429ed061bf76c456fc4b23ad6b03e0d0f8377148a21`), shared here verbatim rather than summarized. Apache section 4 obligations and upstream copyright notices remain applicable.

The App project copies this index, the application LICENSE and `licenses/` to build and publish output. Actual output/publish inspection remains a release gate in the [validation dashboard](docs/en/TRANSLATION_STATUS.md); content declarations alone are not packaging evidence.
