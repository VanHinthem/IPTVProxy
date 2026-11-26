# IPTVProxy

Self-hosted IPTV-manager en streamingproxy voor geautoriseerde IPTV-bronnen. Het doel is aanzienlijk eenvoudiger configuratie en onderhoud dan Dispatcharr, met betrouwbare playback via bestaande spelers.

**Status:** Werkblokken 1 en 2 afgerond; werkblok 3 (films en series) in ontwikkeling.

## Documentatie
- [MVP en architectuur](docs/MVP.md)
- [Productvisie en functioneel ontwerp](docs/FUNCTIONAL_DESIGN.md)
- [Projectstatus en vervolgstappen](docs/STATUS.md)
- [NAS-installatie](deploy/README.md)

Xtream Codes is de primaire interface; Nuvio + IPTV is gevalideerd, Arvio nog niet afzonderlijk. Docker is de primaire deploymentmethode.

Gebruik uitsluitend bronnen waarvoor toegang en distributie zijn toegestaan. Geen providercredentials, echte stream-URL's of persoonlijke kijkgegevens in Git.
