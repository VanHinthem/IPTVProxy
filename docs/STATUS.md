# Projectstatus — 2026-11-25

Werkblok #1 afgerond op 25 november 2026; PR #5 gemerged naar main (7c0b78c). Volgend werkblok: #2. Zie [MVP](MVP.md) en [functioneel ontwerp](FUNCTIONAL_DESIGN.md).

NAS en Nginx HTTPS werken. IPTV-subdomein DNS-only om vertraging via Cloudflare-proxy te vermijden. VLC speelt direct af; Nuvio playback en single-viewer zenderwisseling bevestigd. Twee geautoriseerde providers geïmporteerd (86.514 livekanalen). HTTP/HTTPS upstream en maximaal vijf gecontroleerde redirects ondersteund.

Per-client queues veroorzaakten overflow bij bursty streams van tweede provider. Vervangen door eigen in-memory TS-ringbuffer van circa 32 MiB per actieve upstream, onafhankelijke cursors, uitleesblokken circa 64 KiB en maximaal 1 MiB recente historie bij aansluiten. Achterlopende clients springen naar oudste beschikbare pakketgrens; geen garantie op decoderherstel zonder volgende PSI/keyframe. Geen Redis/FFmpeg vereist. Bufferregressiechecks toegevoegd aan CI. CI en imagepublicatie a341961 groen; gebruiker bevestigt stabiele playback bij beide providers en verbeterde zenderwisseling.

CI en imagepublicatie 568b0f3 groen. NAS-update, behoud catalogus, playback beide providers en externe 5G-playback door gebruiker bevestigd. Open vóór bredere externe gebruikersdistributie: publieke toegangsrestricties, individuele gebruikerssessies en client-IP-rate-limiting; apart opvolgen in #4. Arvio nog niet afzonderlijk gevalideerd. HLS/failover later. Single-viewer switching kan andere kijkers verdringen. Geen credentials in Git.
