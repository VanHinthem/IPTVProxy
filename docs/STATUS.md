# Projectstatus — 2026-11-24

MVP en architectuur akkoord; zie [MVP](MVP.md) en [functioneel ontwerp](FUNCTIONAL_DESIGN.md). Actief: #1, eerste kijkbare live-tv, draft PR #5.

CI #7 groen: geïntegreerde Docker-start, authenticatie en lege Xtream-catalogus. Daarna oorspronkelijke providercategorieën met stabiele IDs en initiële EF Core-migratie toegevoegd; nieuwe CI nog niet bevestigd. De eerdere EnsureCreated-ontwikkeldatabase is niet automatisch migreerbaar; gebruik een nieuw leeg volume voor de eerste echte installatie, verwijder geen bestaande gegevens zonder backup.

Open: migratie/startup en reimport testen, veilige lokale HTTPS-toegang, echte provider- en clienttests, eventuele redirect/HLS-ondersteuning. Nog geen NAS-deployment of bewezen playback.

Werkwijze: zie [AGENTS.md](../AGENTS.md).
