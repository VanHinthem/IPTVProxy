# Projectstatus — 2026-11-25

Actief: #1, draft PR #5. Zie [MVP](MVP.md) en [functioneel ontwerp](FUNCTIONAL_DESIGN.md).

NAS en Nginx HTTPS werken. IPTV-subdomein DNS-only om vertraging via Cloudflare-proxy te vermijden. VLC speelt direct af; Nuvio playback en single-viewer zenderwisseling bevestigd. Twee geautoriseerde providers geïmporteerd (86.514 livekanalen). HTTP/HTTPS upstream en maximaal vijf gecontroleerde redirects ondersteund.

Per-client queues veroorzaakten overflow bij bursty streams van tweede provider. Vervangen door eigen in-memory TS-ringbuffer van circa 32 MiB per actieve upstream, onafhankelijke cursors, uitleesblokken circa 64 KiB en maximaal 1 MiB recente historie bij aansluiten. Achterlopende clients springen naar oudste beschikbare pakketgrens; geen garantie op decoderherstel zonder volgende PSI/keyframe. Geen Redis/FFmpeg vereist. Bufferregressiechecks toegevoegd aan CI. CI en imagepublicatie a341961 groen; gebruiker bevestigt stabiele playback bij beide providers en verbeterde zenderwisseling.

Open: externe 5G-test, verificatie publieke toegangsrestricties, CI en deployment van bijgewerkte SQLite dependency (3.53.3), merge. NAS-installatie-instructies bijgewerkt. Single-viewer channel switching kan andere kijkers verdringen; onafhankelijke gebruikerssessies later. HLS/failover nog niet geïmplementeerd. Geen credentials in Git.
