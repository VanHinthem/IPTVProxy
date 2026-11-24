# Projectstatus — 2026-11-24

Fase: functionele discovery afgerond op hoofdlijnen. Zie [functioneel ontwerp](FUNCTIONAL_DESIGN.md). Nog geen applicatiecode, gekozen stack, gevalideerde providerintegratie of definitieve releaseplanning.

## Vervolg
1. MVP afbakenen en prioriteren tegenover V1 en latere uitbreiding; expliciet vastleggen wat niet in MVP zit.
2. Technische proof of concept: twee geautoriseerde providers, Nuvio + IPTV en Arvio, XC-contract, EPG, MPEG-TS/HLS, VOD seek, sharing, failover en limieten. Metingen en acceptatiecriteria vastleggen; geen echte credentials in Git.
3. Architectuur en datamodel kiezen op basis van resultaten. .NET en één Docker-container zijn kandidaten, geen besluiten.
4. Roadmap, werkpakketten, tests, CI/CD en projectworkflow vastleggen.
5. Pas daarna gefaseerd implementeren.

## Werkwijze
Functionele baseline is de bron voor scopebesluiten; wijzigingen expliciet documenteren. Eenvoud en betrouwbaarheid gaan boven featurepariteit met Dispatcharr. Geen implementatie starten vóór afbakening en technische haalbaarheid. Voorlopige instellingen en technische aannames zijn niet gevalideerd.
