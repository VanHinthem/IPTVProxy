# MVP en architectuur — akkoord 24 november 2026

Modulaire monoliet: .NET 10 / ASP.NET Core, Blazor Web App Interactive Server, EF Core + SQLite (WAL, lokaal persistent volume), BackgroundService, in-memory cache, één Docker-container, GitHub Actions/GHCR. Native HTTP streaming met gedeelde in-memory MPEG-TS-ringbuffer per actieve upstream; FFmpeg alleen indien nodig voor remux, geen transcoding.

## Roadmap
- [#1 Eerste kijkbare live-tv](https://github.com/VanHinthem/IPTVProxy/issues/1): basis, beheeraccount, IPTV-account, Xtream-bronnen, live-import, stabiele IDs, native proxy, vroege NAS-test.
- [#2 Automatisch kanaalbeheer](https://github.com/VanHinthem/IPTVProxy/issues/2): conservatieve naam-matching, groepen met categorie/minimumkwaliteit, handmatige toevoeging/uitsluiting, EPG, periodieke imports.
- [#3 Films en series](https://github.com/VanHinthem/IPTVProxy/issues/3): categorie-selectie, metadata on demand, Range/seek.
- [#4 Betrouwbaarheid](https://github.com/VanHinthem/IPTVProxy/issues/4): live sharing, failover, providerlimieten, monitoring. Providerprioriteit eerst; HLS eerder indien nodig.

Status 26 november 2026: #1–#3 gemerged; #4 gestart. Live-catalogus/EPG-verfijningen: https://github.com/VanHinthem/IPTVProxy/issues/7.

Alle vier samen vormen de MVP. V1: uitgebreidere matching, dynamische groepen, externe XMLTV, uitgebreide profielen, strategieën, historie, backup/herstel. Later: overige bronnen, uitgebreide analyses, metadata-verrijking en complexe regels. Zie FUNCTIONAL_DESIGN.md voor productvisie.

## Randvoorwaarden
Gescheiden beheer/IPTV-authenticatie, versleutelde providercredentials, geen credential-URLs in logs, gecontroleerde hosts/redirects, lokale beheerinterface, HTTPS voor externe toegang. Alleen geautoriseerde bronnen binnen providerrechten. Onbekende capaciteit conservatief behandelen. Transactionele snapshots, behoud IDs en laatste geldige catalogus. MPEG-TS eerst, HLS apart behandelen; failover hoeft niet naadloos te zijn. Praktijktests met twee providers en Nuvio + IPTV, inclusief 5G, geaccepteerd op 25 november 2026. Arvio en publieke toegangsrestricties nog afzonderlijk valideren.

## Werkblok 4 — akkoord 26 november 2026
Meerdere IPTV-accounts en herbruikbare playlists met één standaardplaylist per account horen bij het MVP. Zie issue #4. Bestaande publicatie-ID's en configuratie blijven behouden. Geen automatische verdringing van andere kijkers.
