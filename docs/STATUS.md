# Projectstatus — 2026-11-24

MVP en architectuur akkoord; zie [MVP](MVP.md) en [functioneel ontwerp](FUNCTIONAL_DESIGN.md). Actief: #1, draft PR #5.

CI #9 groen inclusief initiële migratie en container-smoketests. GHCR AMD64-publicatieworkflow, NAS Compose, lokale configuratie en installatie-instructies toegevoegd onder deploy/. Browser-origincontrole aangepast aan geconfigureerde publieke HTTPS-origin. Nieuwe CI en daadwerkelijke imagepublicatie nog niet bevestigd. Gebruik een nieuw volume voor de eerste installatie; verwijder geen bestaande data.

Volgende stap: bevestig CI en GHCR-tag, configureer lokale HTTPS-toegang, importeer geautoriseerde providers en test Nuvio + IPTV/Arvio. HTTPS-upstream zonder redirects en TS-only zijn voorlopig beperkingen. Nog geen echte import/playback of NAS-deployment bevestigd.

Werkwijze: zie [AGENTS.md](../AGENTS.md).
