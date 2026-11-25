# MVP en architectuur — akkoord 24 november 2026

Modulaire monoliet: .NET 10 / ASP.NET Core, Blazor Web App Interactive Server, EF Core + SQLite (WAL, lokaal persistent volume), BackgroundService, in-memory cache, één Docker-container, GitHub Actions/GHCR. Native HTTP streaming met gedeelde in-memory MPEG-TS-ringbuffer per actieve upstream; FFmpeg alleen indien nodig voor remux, geen transcoding.

## Roadmap
- [#1 Eerste kijkbare live-tv](https://github.com/VanHinthem/IPTVProxy/issues/1): basis, beheeraccount, IPTV-account, Xtream-bronnen, live-import, stabiele IDs, native proxy, vroege NAS-test.
- [#2 Automatisch kanaalbeheer](https://github.com/VanHinthem/IPTVProxy/issues/2): conservatieve naam-matching, groepen met categorie/minimumkwaliteit, handmatige toevoeging/uitsluiting, EPG, periodieke imports.
- [#3 Films en series](https://github.com/VanHinthem/IPTVProxy/issues/3): categorie-selectie, metadata on demand, Range/seek.
- [#4 Betrouwbaarheid](https://github.com/VanHinthem/IPTVProxy/issues/4): live sharing, failover, providerlimieten, monitoring. Providerprioriteit eerst; HLS eerder indien nodig.

Status 25 november 2026: #1 gemerged; #2 gemerged (PR #6); #3 volgende. Live-catalogus/EPG-verfijningen: https://github.com/VanHinthem/IPTVProxy/issues/7.

Alle vier samen vormen de MVP. V1: uitgebreidere matching, dynamische groepen, externe XMLTV, meerdere accounts/profielen, strategieën, historie, backup/herstel. Later: overige bronnen, uitgebreide analyses, metadata-verrijking en complexe regels. Zie FUNCTIONAL_DESIGN.md voor productvisie.

## Randvoorwaarden
Gescheiden beheer/IPTV-authenticatie, versleutelde providercredentials, geen credential-URLs in logs, gecontroleerde hosts/redirects, lokale beheerinterface, HTTPS voor externe toegang. Alleen geautoriseerde bronnen binnen providerrechten. Onbekende capaciteit conservatief behandelen. Transactionele snapshots, behoud IDs en laatste geldige catalogus. MPEG-TS eerst, HLS apart behandelen; failover hoeft niet naadloos te zijn. Praktijktests met twee providers en Nuvio + IPTV, inclusief 5G, geaccepteerd op 25 november 2026. Arvio en publieke toegangsrestricties nog afzonderlijk valideren.
