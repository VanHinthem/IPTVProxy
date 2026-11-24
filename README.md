# IPTVProxy

Self-hosted IPTV-manager en streamingproxy voor geautoriseerde IPTV-bronnen. Het doel is aanzienlijk eenvoudiger configuratie en onderhoud dan Dispatcharr, met betrouwbare playback via bestaande spelers.

**Status:** MVP en architectuur akkoord; implementatie van werkblok 1 gestart.

## Documentatie
- [MVP en architectuur](docs/MVP.md)
- [Productvisie en functioneel ontwerp](docs/FUNCTIONAL_DESIGN.md)
- [Projectstatus en vervolgstappen](docs/STATUS.md)

Xtream Codes is de primaire beoogde interface; Nuvio + IPTV en Arvio zijn de eerste te valideren clients. Docker is de primaire deploymentmethode.

Gebruik uitsluitend bronnen waarvoor toegang en distributie zijn toegestaan. Geen providercredentials, echte stream-URL's of persoonlijke kijkgegevens in Git.
