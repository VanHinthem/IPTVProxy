# IPTVProxy

Self-hosted IPTV-manager en streamingproxy voor geautoriseerde IPTV-bronnen. Het doel is aanzienlijk eenvoudiger configuratie en onderhoud dan Dispatcharr, met betrouwbare playback via bestaande spelers.

**Status:** Werkblokken 1–3 afgerond en gemerged; werkblok 4 (betrouwbaarheid, gebruikers en playlists) gestart.

## Documentatie
- [MVP en architectuur](docs/MVP.md)
- [Productvisie en functioneel ontwerp](docs/FUNCTIONAL_DESIGN.md)
- [Projectstatus en vervolgstappen](docs/STATUS.md)
- [NAS-installatie](deploy/README.md)

## Catalogusbeheer
- Live TV: logische kanalen, providergroepen, EPG en kanaallogo's.
- Films/series: volledige import, selectieve publicatie, eigen categorieën, titelzoeker, native Range-playback.
- Cataloguswijzigingen: per provider/type nieuwe, teruggekeerde en verdwenen bronnen, titels en providercategorieën; beschikbaarheid van logische kanalen.
- Importplanning: Live TV standaard 6 uur, films/series 24 uur, per provider/type instelbaar (0 = uit). Historie begint bij invoering van wijzigingsregistratie, niet retrospectief.

Xtream Codes is de primaire interface; Nuvio + IPTV is gevalideerd, Arvio nog niet afzonderlijk. Docker is de primaire deploymentmethode.

Gebruik uitsluitend bronnen waarvoor toegang en distributie zijn toegestaan. Geen providercredentials, echte stream-URL's of persoonlijke kijkgegevens in Git.
