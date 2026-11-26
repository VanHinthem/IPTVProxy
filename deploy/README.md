# NAS deployment (IPTVProxy MVP)

Use a CI-tested immutable GHCR image tag. Copy compose.yaml and .env.example into a NAS directory; copy .env.example to .env, chmod 600, use distinct random hexadecimal passwords (openssl rand -hex 24), and fill in the image tag. Keep the named volume and back it up together with its data-protection keys. Never delete the volume to fix an upgrade.

Initial test: bind to loopback, set PUBLIC_BASE_URL=http://localhost:5089, run docker compose pull and docker compose up -d, and check /health. Access administration via SSH port forwarding.

For HTTPS use a trusted reverse proxy, preserve Host, enable WebSockets and disable proxy buffering/cache for live streams. Disable access logging for credential-bearing URLs throughout the proxy chain. Set PUBLIC_BASE_URL to the actual HTTPS origin. Restrict the backend port to the proxy by private Docker networking or firewall rules; never forward the backend directly from the internet.

For remote playback without VPN, use a public trusted TLS certificate at the reverse proxy and forward only the required HTTPS port. A DNS-only record can avoid buffering via a CDN; this exposes the origin IP and removes CDN protection. Restrict administration to trusted networks using proxy rules covering the admin UI, static assets and Blazor negotiation endpoints. Do not expose NAS management ports. Verify access restrictions from outside the LAN before enabling remote users. The application currently uses separate bootstrap admin and IPTV accounts, a basic origin check and rate limits; it is not a hardened multi-user service. Rate limits currently see the proxy IP rather than individual remote clients.

Add only authorized providers with their actual subscription connection limits. HTTP and HTTPS upstreams are accepted; HTTP exposes provider credentials and data in transit. Live MPEG-TS only, with bounded validated redirects and a shared in-memory ring buffer per active channel. Single-viewer channel switching can displace other viewers on providers with a single allowed connection; disable it for independent simultaneous users (Playback__SingleViewerChannelSwitch=false via compose environment).

Test provider import, catalog visibility, playback, switching and stability; confirm external access separately. Do not share real credentials or stream URLs in bug reports.

## Werkblok 2 — eerste praktijktest
Gebruik uitsluitend een immutable `sha-...` image waarvan CI én Development image geslaagd zijn. Maak eerst een consistente backup van het persistente volume inclusief database, WAL en dataprotectiesleutels (stop container voor bestandsbackup). Na upgrade: controleer health en bestaande playback in originele modus. Open Kanaalbeheer en Matchingdiagnostiek; noteer aantallen, selecteer categorievoorbeelden en importeer beide providers opnieuw voor EPG-metadata. Maak een kleine groep, bijvoorbeeld NL met minimum FHD en onbekende fallback; controleer preview. Activeer samengestelde publicatie pas daarna, vernieuw de catalogus in Nuvio en test playback/zappen/EPG. Zet bij problemen de originele publicatiemodus terug. Oude en nieuwe Xtream IDs zijn gescheiden; bestaande favorieten op ruwe IDs migreren niet automatisch. Voor downgrade herstel de consistente backup; verwijder nooit het volume.

## EPG en afbeeldingen
XMLTV wordt lokaal gecachet en als volledig bestand aangeboden. `Epg__RefreshHours` (standaard 6, 0 uit) stuurt import; `Catalog__RefreshHours` stuurt catalogusimport. `Artwork__CacheMegabytes` standaard 256; cache op persistent volume. Bewaar ook `artwork-signing.key` en data-protectiesleutels bij backup. Artwork-routes zijn ondertekende/beschermde bearer-URLs; geen providercredentials in clientcatalogus. Laat `/artwork/` bereikbaar voor IPTV-clients, maar beperk beheer via reverse proxy. Geen upstream SVG; lokale placeholder mogelijk. Controleer logo's en EPG na clientrefresh. Programma-afbeeldingen alleen indien aangeleverd en ondersteund door client; geen externe verrijking in MVP 2.


## Werkblok 3 en 4
Films en series gebruiken native HTTP Range-playback. Importintervallen worden per provider/type via Updates beheerd (0 = uit). Werkblok 4 introduceert accounts, playlists en onafhankelijke sessies; deze zijn nog niet geaccepteerd. Gebruik de huidige single-viewer configuratie niet voor onafhankelijke externe kijkers. Controleer bij upgrade behoud van IDs, account, catalogus en volume.
