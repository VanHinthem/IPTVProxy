# Eerste NAS-test (development)

Use a CI-tested immutable image tag from GHCR, not an arbitrary latest tag. Initial publication targets linux/amd64; ARM64 follows when needed. If the package is private, authenticate locally with an authorized read:packages token. Never put the token in Git.

Copy compose.yaml and .env.example into a new NAS directory; copy .env.example to .env, set permissions 600, choose two distinct random hexadecimal passwords (openssl rand -hex 24), and fill in the exact image tag. Keep the new named volume; never delete an existing volume to resolve a migration error.

Initial loopback-only test: keep BIND_ADDRESS=127.0.0.1 and PUBLIC_BASE_URL=http://localhost:5089. Run docker compose pull and docker compose up -d. Check docker compose ps and curl -fsS http://127.0.0.1:5089/health. For admin access use SSH local forwarding of port 5089 to NAS loopback.

For LAN player testing, configure a trusted HTTPS reverse proxy before exposing credentials. Preserve Host, enable WebSocket upgrades, disable proxy buffering for streaming, and disable access logging of credential-bearing URLs (including player_api.php and live paths). Do not expose the service to the public internet. Set PUBLIC_BASE_URL to the exact HTTPS origin the clients use. Only expose the backend to the proxy via a private network or firewall-restricted bind; never use an unrestricted published port. AdminGate validates browser Origin against configured public origin. TLS termination must be at the trusted proxy. Do not forward untrusted traffic to the backend.

Test: sign in to administration, add one authorized HTTPS Xtream provider with the actual subscribed connection limit, import, verify categories/counts, configure player with the IPTV (not admin or provider) credentials, play one channel, stop and verify another starts. Repeat with other provider and player. No real credentials or stream URLs in bug reports. HTTPS upstream only, no redirects or HLS yet. No production readiness claimed. Back up the data volume including keys.
