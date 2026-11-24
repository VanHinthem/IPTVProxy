# IPTVProxy — ontwikkelafspraken

IPTVProxy is een hobbyproject. Prioriteit: snel een bruikbare eerste versie op de NAS krijgen, daarna verbeteren vanuit werkelijk gebruik. Het functioneel ontwerp is richtinggevend, geen verplicht volledig releasecontract.

- Werk in grotere samenhangende functionele blokken; vermijd micro-issues, micro-PR's, uitgebreide fasegates en onnodige bevestigingsmomenten.
- Kies gangbare, eenvoudige technische oplossingen zelfstandig; bespreek alleen ingrijpende productkeuzes, destructieve wijzigingen of wezenlijke risico's.
- Houd ontwerp en documentatie beknopt; onderhoud README, STATUS en belangrijke besluiten. Geen uitgebreide documenten zonder concrete noodzaak.
- Test risicogestuurd: build/smoke tests, kernflows, authenticatie, secretbeheer, import/databehoud, verbindingslimieten en streaming. Geen uitputtende testmatrix of formele acceptatie voor iedere kleine stap.
- Combineer haalbaarheidsonderzoek met implementatie waar praktisch. Bouw vroeg een end-to-end werkende versie en deploy naar NAS voor praktijkfeedback met geautoriseerde providers en Nuvio + IPTV/Arvio.
- Vermijd overengineering, premature abstractions en featurepariteit met Dispatcharr. Stel niet-essentiële functionaliteit uit.
- Gebruik GitHub en CI/CD pragmatisch; bundel gerelateerde wijzigingen. Geen credentials, gevoelige URLs of persoonlijke kijkgegevens in Git.
- Lees docs/STATUS.md en docs/FUNCTIONAL_DESIGN.md bij aanvang; actualiseer status na betekenisvolle wijzigingen.
