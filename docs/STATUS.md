# Projectstatus — 2026-11-25

Actief: #1, draft PR #5. Zie [MVP](MVP.md) en [functioneel ontwerp](FUNCTIONAL_DESIGN.md).

NAS en Nginx HTTPS werken. IPTV-subdomein DNS-only om vertraging via Cloudflare-proxy te vermijden. VLC speelt direct af; Nuvio playback en single-viewer zenderwisseling bevestigd. Twee geautoriseerde providers geïmporteerd (86.514 livekanalen). HTTP/HTTPS upstream en maximaal vijf gecontroleerde redirects ondersteund.

Per-client queues veroorzaakten overflow bij bursty streams van tweede provider. Vervangen door eigen in-memory TS-ringbuffer van circa 32 MiB per actieve upstream, onafhankelijke cursors, uitleesblokken circa 64 KiB en maximaal 1 MiB recente historie bij aansluiten. Achterlopende clients springen naar oudste beschikbare pakketgrens; geen garantie op decoderherstel zonder volgende PSI/keyframe. Geen Redis/FFmpeg vereist. Bufferregressiechecks toegevoegd aan CI. Nieuwe build en praktijkstabiliteit nog niet bevestigd.

Open: stabiele playback beide providers en zappen, externe 5G-test, publieke toegangsbeveiliging, SQLite dependency-waarschuwing, installatie-instructies en merge. Single-viewer channel switching kan andere kijkers verdringen; onafhankelijke gebruikerssessies later. HLS/failover nog niet geïmplementeerd. Geen credentials in Git.
