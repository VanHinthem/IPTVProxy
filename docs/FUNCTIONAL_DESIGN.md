# IPTVProxy — functioneel ontwerp

Datum: 2026-11-24. Status: functionele baseline akkoord op hoofdlijnen; releaseprioritering en technische haalbaarheid nog open. V1 in eerdere gesprekken betekent gewenste productfunctionaliteit, niet een definitief MVP-contract.

## 1. Productvisie
Een compacte self-hosted IPTV-manager en centrale streamingproxy, geïnspireerd door Dispatcharr maar met zo weinig mogelijk handmatig beheer. Doel: na installatie en selectie van gewenste categorieën snel kunnen kijken, zonder eerst duizenden kanalen te configureren. Bestaande spelers verzorgen de kijkervaring. Eerste doelclients: Nuvio + IPTV en Arvio. Meerdere Xtream-providers, momenteel twee, met veranderbare prioriteit. Live-tv, films en series; huishouden en eventueel geautoriseerde externe gebruikers. Alleen gebruik binnen providerrechten en abonnementsvoorwaarden.

## 2. Bronbeheer
- B01: meerdere providers, Xtream Codes primair, M3U URL/bestand aanvullend, handmatig stream toevoegen.
- B02: volledige catalogus importeren, inclusief live, films, series en originele categorieën; selectie uitsluitend binnen applicatie.
- B03: providerprioriteit wijzigen, provider uitschakelen, aangepaste HTTP-headers en User-Agent.
- B04: accountstatus, verloopdatum, max_connections en active_cons ophalen waar betrouwbaar beschikbaar; handmatige limiet mogelijk; onbekend niet als onbeperkt voorstellen.
- B05: periodieke en handmatige import, status en historie. Proxy/VPN per provider nog open.
- B06: bronidentiteit scheiden van gepubliceerde identiteit; stabiele IDs, behoud van handmatige correcties; verdwenen items markeren, gewijzigde URL bijwerken, nieuwe varianten toevoegen.
- B07: valideer snapshots voor publicatie; behoud laatste geldige gegevens bij fout of verdachte krimp. Geen overlappende imports van dezelfde bron; geen onderbreking van actieve streams.

## 3. Live-tv catalogus en matching
- L01: vier lagen: originele broncatalogus, genormaliseerde catalogus, groepen, profielen.
- L02: automatisch logische kanalen aanmaken en meerdere bronstreams (ook binnen één provider) koppelen. Standaard één kanaal publiceren; aparte kwaliteitsvarianten mogelijk.
- L03: matching via naamnormalisatie, land, taal, categorie, EPG-ID, aliassen, eerder bevestigde koppelingen; regionale zenders niet blind samenvoegen.
- L04: hybride referentiecatalogus voor bekende zenders (eerste kandidaat NL/BE); onbekende zenders automatisch toevoegen. Hoge zekerheid automatisch koppelen, twijfel ter beoordeling, overige apart houden. Bevestigingen en afwijzingen onthouden.
- L05: kwaliteit herkennen (SD/HD/FHD/4K, codec, framerate, bitrate, HDR waar beschikbaar); afgeleide en gemeten eigenschappen onderscheiden. Onbekend blijft onbekend; HD-label is geen bewijs van 720p.
- L06: doorzoekbare catalogus, filters, bulkacties, hernoemen, samenvoegen, splitsen, uitsluiten. Per kanaal naam, logo, land, taal, categorie, EPG, bronkoppelingen, prioriteit, kwaliteit, nummer, actiefstatus; handmatige overrides blijven behouden. Individuele bron uitsluitbaar.

## 4. Groepen en publicatie
- G01: handmatige, dynamische en hybride groepen; hybride = regels plus handmatige toevoegingen/uitsluitingen.
- G02: dynamische regels op land, taal, providercategorie, provider, naam, kwaliteit, codec, framerate, EPG en aantal providers, voor zover metadata betrouwbaar beschikbaar is. Exacte AND/OR-semantiek en omgang met onbekende metadata nog uitwerken.
- G03: minimum/maximumkwaliteit en optionele fallback naar lagere kwaliteit; groep kan eigen streamselectiestrategie hebben. Kanaal kan in meerdere groepen voorkomen met verschillende regels. Alleen geschikte bronvarianten selecteren.
- G04: automatische publicatie nieuwe passende kanalen per groep instelbaar; overzicht recente toevoegingen, handmatige beoordeling mogelijk.
- G05: sorteren handmatig, alfabetisch, provider, referentievolgorde, kwaliteit; vastgezette posities combineren met automatische sortering. Stabiele nummers/IDs waar mogelijk. Identiteit van afzonderlijke publicaties bij meerdere groepen en clientgedrag nog valideren.
- G06: groepen herbruikbaar in meerdere profielen; wijzigingen automatisch doorwerken. Eén standaardprofiel bij installatie.

## 5. Films en series
- V01: volledige import, eenvoudig categoriebeheer: providercategorieën selecteren, hernoemen, samenvoegen en aan profielen toewijzen. Nieuwe content in geselecteerde categorie automatisch publiceren.
- V02: originele metadata behouden; details op aanvraag ophalen en cachen. Catalogusimport niet laten ontaarden in individuele requests voor alle items/afleveringen.
- V03: films en series proxyen, afzonderlijke afspeelsessies, HTTP Range/seek waar upstream en client dit ondersteunen.
- V04: geen deduplicatie van films/series/afleveringen, automatische kwaliteitsselectie, eigen TMDB-integratie of geavanceerde metadatafilters in eerste beoogde versie. Duplicaten voorlopig behouden. Eventuele kwaliteitsrestricties alleen afdwingen met bekende metadata; beleid voor onbekend nog bepalen.
- V05: onderzoeken welke metadata providers werkelijk aanbieden en hoe doelclients duplicaten en bronnen verwerken.

## 6. EPG
- E01: EPG uit Xtream automatisch configureren; externe XMLTV URL/bestand, gzip, meerdere bronnen, bronprioriteit, periodieke/handmatige sync, tijdzonecorrectie.
- E02: EPG los van streamprovider. Matching: handmatige mapping, referentie-ID, provider-ID, exacte genormaliseerde naam/land, alias, twijfelvoorstel. Geen match betekent kanaal zonder gids.
- E03: per kanaal één actuele geschikte EPG-bron kiezen; prioriteit, actualiteit, dekking, volledigheid, taal; fallback bij verouderde bron. Geen individuele programmamerging.
- E04: preview, problemenoverzicht standaard voor gepubliceerde kanalen, handmatige correcties behouden. XMLTV per profiel en EPG via Xtream API. Geen uitgebreide eigen tv-gids of opnameplanning.

## 7. Streamingengine
- S01: proxy standaard voor live/VOD/series. Native passthrough als kandidaat, FFmpeg stream copy/remux als optionele fallback; geen transcoding. MPEG-TS, HLS en VOD technisch valideren.
- S02: live sharing van dezelfde geschikte upstreamstream voor meerdere clients, indien toegestaan. Clientisolatie, afzonderlijke begrensde buffers, trage client mag anderen niet blokkeren, laatste client beëindigt stream na korte configureerbare grace period; bij capaciteitsdruk direct vrijgeven. Geen VOD sharing.
- S03: strategie provider eerst of kwaliteit eerst configureerbaar, betrouwbaarheid later als zelfstandige strategie; tijdelijke cooldown voor defecte bronnen. Globaal -> profiel -> groep -> kanaal overrides; toegangs- en kwaliteitslimieten blijven harde restricties.
- S04: bronselectie houdt rekening met kwaliteit, prioriteit, capaciteit, betrouwbaarheid, compatibiliteit en overrides; hergebruik geschikte actieve stream waar passend. Exacte rangorde bij conflict nog uitwerken.
- S05: detecteer HTTP-fouten, geen data, offline provider, capaciteit en herhaalde uitval; reconnect/failover naar geschikte bron. Geen onnodige automatische failback. Naadloosheid niet gegarandeerd bij afwijkende codecs/timestamps/formaten. Geen geschikte bron: gecontroleerde fout.
- S06: beperkte configureerbare buffering, korte zaptijd, passieve kwaliteitsmeting tijdens normaal gebruik en handmatige streamtest; geen massale periodieke tests.
- S07: directe streaming optioneel per gebruiker, alleen indien toegestaan en technisch haalbaar; mogelijk zichtbare providercredentials, beperkte monitoring/limieten/failover; niet standaard.
- S08: provider-catch-up doorgeven als provider/client dit ondersteunen; geen eigen opnames of timeshiftopslag. Eigen timeshift niet vastgesteld.
- S09: externe proxyclients gebruiken thuisupload per client; sharing reduceert upstreamverbindingen/download, niet externe upload.

## 8. Gebruikers, profielen en beveiliging
- U01: beheeraccounts gescheiden van IPTV-accounts; account kan persoon, apparaat of huishouden vertegenwoordigen.
- U02: IPTV-account met eigen credentials, profiel, actiefstatus, toegestane contenttypen, max sessies, max kwaliteit, lokale/externe toegang, proxy/direct-modus. Standaard gedeeld profiel, eigen profielen optioneel.
- U03: onderscheid provider-upstreamlimieten, gebruikerssessielimieten en optionele systeemlimiet. Directe streaming niet betrouwbaar meetbaar/afdwingbaar.
- U04: Xtream Codes primaire output, M3U/XMLTV aanvullend; makkelijk kopieerbare configuratie per account.
- U05: authenticatie, brute-force-bescherming, HTTPS via reverse proxy, beheer standaard alleen lokaal, IPTV API optioneel extern. Autorisatie voor iedere contentrequest, account blokkeren en actieve sessies beëindigen. Geen uitgebreid RBAC/OIDC.
- U06: secrets beschermen in opslag, output, backups en logs; nooit volledige credential-URL's loggen. Technisch beveiligingsontwerp vereist, inclusief vertrouwde proxies en veilige URL-fetching. Externe toegang alleen binnen providerrechten.

## 9. Monitoring
- M01: dashboard met actieve kijkers/upstreams, providerstatus/limieten/abonnement, download en externe upload, catalogusaantallen, aandachtspunten.
- M02: realtime monitor per stream en client (bron, kwaliteit, codec waar bekend, bitrate, duur, lokaal/extern, sharing); individuele sessie of upstream stoppen, live alternatieve bron handmatig selecteren.
- M03: historie per provider/gebruiker, lokaal/extern verkeer, gelijktijdige verbindingen, mislukte starts, onderbrekingen, failovers, gemiddelde starttijd. Meet alleen verkeer via proxy, geen onbewezen directe statistieken.
- M04: bronbetrouwbaarheid met aantallen en percentages; lage steekproef markeren. Dagaggregaties; geen kijkanalyses of permanente detailmetingen.
- M05: gebeurtenissenoverzicht filterbaar op provider, kanaal, gebruiker, type en ernst; interne meldingen voor verlopen abonnement, syncfouten, offline bronnen, EPG, capaciteit, review.
- M06: voorlopige retentie: sessies/events/synchistorie 30 dagen, dagelijkse aggregaties 365 dagen, logs 7 dagen; configureerbaar en nog te valideren. Privacy: beperkte toegang, minimale kijkgegevens. Geen externe notificaties.

## 10. Systeembeheer
- O01: self-hosted Docker/Compose, AMD64/ARM64, geïntegreerde webinterface, persistent volume/bind mount, migraties, healthcheck, reverse proxy; voorkeur één container zonder verplichte externe DB/Redis, architectuur nog open.
- O02: setupwizard: beheeraccount, providers, import, groepen/categorieën selecteren, standaardprofiel en IPTV-account, verbindingsgegevens. Geavanceerde opties standaard verborgen.
- O03: configureerbare onafhankelijke jobs: live/films/series 6 uur, EPG 4 uur, accountstatus 1 uur, matching/groepen na wijzigingen, aggregatie/opruimen/back-up dagelijks. Intervallen voorlopig; providerbelasting valideren.
- O04: taakstatus, laatste/volgende run, duur, fout en handmatig starten. Instellingen algemeen, sync, streaming, netwerk, beveiliging, opslag, backup/herstel en systeeminfo.
- O05: configuratiebackup (providers, handmatige mappings, groepen, profielen, gebruikers, instellingen) en volledige backup (ook catalogus, EPG, statistieken/events). Herstel en versleuteling van gevoelige backups.
- O06: GitHub, Actions, GHCR, SemVer, stabiele/dev-images, tests, release notes, versiecontrole. Geen automatische containerupdates.

## 11. Buiten scope
Eigen speler, DVR/opnames, eigen timeshiftopslag, transcoding, HDHomeRun, plugins, Trakt, uitgebreide media-UI, individuele EPG-programma-editor, VOD-deduplicatie en verrijking in eerste versie, externe notificaties. Geen ongeautoriseerde distributie of omzeiling van providerrestricties.

## 12. Openstaande validatie en besluiten
- MVP versus V1 versus later expliciet prioriteren; bovengenoemde wensen zijn geen belofte dat alles in MVP zit.
- Geautoriseerde testdata van beide providers: catalogusomvang, IDs, metadata, limieten, EPG, contentvarianten, refreshgedrag.
- Nuvio + IPTV en Arvio: Xtream endpoints, categorieën, stabiele IDs/favorieten, duplicaten, EPG, live, seek, HLS/TS, catch-up en redirects.
- Streaming PoC: zaptijd, sharing, trage clients, buffer/memory, failover, limieten, disconnects, VOD ranges, reverse proxy en externe bandbreedte.
- Matching: nauwkeurigheid, false positives, referentiecatalogusbron/licentie en uitbreidbaarheid; regels voor ontbrekende metadata.
- Architectuur: taal/framework ( .NET kandidaat), frontend, database, containeropzet, jobmodel, secretbeheer, teststrategie. Geen definitieve technologiekeuze.
- Licentie voor eigen project nog kiezen; Dispatcharr slechts inspiratie, geen broncode overgenomen.
- UI-voorbeelden uit gesprekken zijn illustratief, geen vastgelegde visuele specificatie.
