# Projectstatus — 2026-11-24

Actief: #1, draft PR #5. Zie [MVP](MVP.md) en [functioneel ontwerp](FUNCTIONAL_DESIGN.md).

NAS-container gestart met HTTP 200 health; lokale HTTPS-toegang via Nginx Proxy Manager werkt. Blazor assets gecorrigeerd in Docker-build; gebruiker bevestigt dat knopactie nu een validatiemelding geeft. HTTP-provideradres werd afgewezen door HTTPS-only validatie; HTTP/HTTPS toegestaan met waarschuwing en specifiekere invoerfouten. Nieuwe CI/image nog te bevestigen.

Open: echte providerimport en playback met Nuvio/Arvio, redirect/HLS indien nodig, SQLite dependency-waarschuwing, duidelijkere accountvalidatie. Geen credentials in Git. HTTP-upstream is onversleuteld; beveiligde lokale toegang blijft vereist. Geen bewezen playback.
