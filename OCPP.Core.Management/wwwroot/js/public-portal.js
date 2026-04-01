(function () {
  const languages = ["hr", "en", "sl", "it", "de", "fr"];
  const translations = {
    hr: {
      "header.help": "Upute",
      "header.support": "Podrška",
      "header.scanQr": "Skeniraj QR",
      "nav.map": "Karta",
      "nav.help": "Kako radi",
      "nav.contact": "Kontakt",
      "lang.label": "Jezik",
      "qr.title": "Skeniraj QR kod punjača",
      "qr.subtitle": "Skeniraj naljepnicu na punjaču i otvori postojeću stranicu za pokretanje punjenja.",
      "qr.unsupported": "QR kod nije prepoznat. Koristite postojeći /cp/... ili /Public/Start link.",
      "qr.camera": "Kamera nije dostupna ili dozvola nije odobrena.",
      "map.title": "Karta punjača",
      "map.stations": "punjača",
      "map.gps": "GPS",
      "map.expand": "Proširi",
      "map.noCoords": "Karta nije dostupna jer nedostaju koordinate.",
      "map.available": "Dostupno",
      "map.busy": "Zauzeto",
      "map.offline": "Nedostupno",
      "map.search": "Pretraži po nazivu, lokaciji ili javnom kodu punjača...",
      "map.stationsOnMap": "Punjači na karti",
      "map.noStations": "Još nema konfiguriranih punjača.",
      "map.noVisibleStations": "Nema punjača u trenutačno vidljivom području karte.",
      "map.connectors": "priključaka",
      "map.freeEnergy": "Besplatna energija",
      "map.sessionFee": "Naknada sesije",
      "map.idleFee": "Naknada mirovanja",
      "map.grace": "grace",
      "map.minutes": "min",
      "map.chooseConnector": "Odaberi priključak",
      "map.startSession": "Pokreni sesiju",
      "start.chooseConnector": "Odaberi priključak",
      "start.prices": "Cijene i naknade",
      "start.companyInvoice": "R1 račun",
      "start.companyName": "Naziv tvrtke (opcionalno)",
      "start.companyNamePlaceholder": "Naziv tvrtke / organizacije",
      "start.oib": "OIB (obavezno za R1)",
      "start.oibPlaceholder": "11 znamenki",
      "start.startFree": "Pokreni besplatnu sesiju",
      "start.startCharging": "Pokreni punjenje",
      "start.backToMap": "Natrag na kartu",
      "start.finishPreviousCheckout": "Dovršite ili otkažite prethodni checkout prije pokretanja nove sesije na ovom priključku.",
      "start.freeInfo": "Besplatno punjenje je uključeno. Naplata se neće izvršiti.",
      "start.secureInfo": "Sigurno plaćanje putem Stripea. Naplaćuje se samo stvarni trošak.",
      "result.newSession": "Pokreni novu sesiju"
    },
    en: {
      "header.help": "Help",
      "header.support": "Support",
      "header.scanQr": "Scan QR",
      "nav.map": "Map",
      "nav.help": "How it works",
      "nav.contact": "Contact",
      "lang.label": "Language",
      "qr.title": "Scan charger QR code",
      "qr.subtitle": "Use the QR code printed on the charger sticker to open its existing start page.",
      "qr.unsupported": "The scanned QR code is not supported. Use an existing /cp/... or /Public/Start link.",
      "qr.camera": "Camera is unavailable or permission was denied.",
      "map.title": "Charging map",
      "map.stations": "station(s)",
      "map.gps": "GPS",
      "map.expand": "Expand",
      "map.noCoords": "No map data available (missing coordinates).",
      "map.available": "Available",
      "map.busy": "Busy",
      "map.offline": "Offline",
      "map.search": "Search by name, location, or public charger code...",
      "map.stationsOnMap": "Stations on map",
      "map.noStations": "No stations configured yet.",
      "map.noVisibleStations": "No stations match the current map view.",
      "map.connectors": "connector(s)",
      "map.freeEnergy": "Free energy",
      "map.sessionFee": "Session fee",
      "map.idleFee": "Idle fee",
      "map.grace": "grace",
      "map.minutes": "min",
      "map.chooseConnector": "Choose connector",
      "map.startSession": "Start session",
      "start.chooseConnector": "Choose connector",
      "start.prices": "Prices & fees",
      "start.companyInvoice": "Company invoice",
      "start.companyName": "Company name (optional)",
      "start.companyNamePlaceholder": "Company / organization name",
      "start.oib": "OIB (required for R1)",
      "start.oibPlaceholder": "11 digits",
      "start.startFree": "Start free session",
      "start.startCharging": "Start charging",
      "start.backToMap": "Back to map",
      "start.finishPreviousCheckout": "Finish or cancel the previous checkout before starting a new session on this connector.",
      "start.freeInfo": "Free charging enabled. No payment will be charged.",
      "start.secureInfo": "Secure payment via Stripe. Only the actual cost is charged.",
      "result.newSession": "Start a new session"
    },
    sl: {
      "header.help": "Pomoč",
      "header.support": "Podpora",
      "header.scanQr": "Skeniraj QR",
      "nav.map": "Zemljevid",
      "nav.help": "Kako deluje",
      "nav.contact": "Kontakt",
      "lang.label": "Jezik",
      "qr.title": "Skeniraj QR kodo polnilnice",
      "qr.subtitle": "Skenirajte kodo na polnilnici in odprite obstoječo stran za začetek polnjenja.",
      "qr.unsupported": "Skenirana QR koda ni podprta. Uporabite obstoječo povezavo /cp/... ali /Public/Start.",
      "qr.camera": "Kamera ni na voljo ali dovoljenje ni bilo odobreno.",
      "map.title": "Zemljevid polnilnic",
      "map.stations": "postaj",
      "map.gps": "GPS",
      "map.expand": "Razširi",
      "map.noCoords": "Zemljevid ni na voljo, ker manjkajo koordinate.",
      "map.available": "Na voljo",
      "map.busy": "Zasedeno",
      "map.offline": "Nedosegljivo",
      "map.search": "Išči po imenu, lokaciji ali javni kodi polnilnice...",
      "map.stationsOnMap": "Postaje na zemljevidu",
      "map.noStations": "Še ni nastavljenih postaj.",
      "map.noVisibleStations": "V trenutno vidnem delu zemljevida ni postaj.",
      "map.connectors": "priključkov",
      "map.freeEnergy": "Brezplačna energija",
      "map.sessionFee": "Pristojbina seje",
      "map.idleFee": "Pristojbina mirovanja",
      "map.grace": "grace",
      "map.minutes": "min",
      "map.chooseConnector": "Izberi priključek",
      "map.startSession": "Začni sejo",
      "start.chooseConnector": "Izberi priključek",
      "start.prices": "Cene in pristojbine",
      "start.companyInvoice": "Račun za podjetje",
      "start.companyName": "Naziv podjetja (neobvezno)",
      "start.companyNamePlaceholder": "Naziv podjetja / organizacije",
      "start.oib": "Davčna številka (obvezno za R1)",
      "start.oibPlaceholder": "11 številk",
      "start.startFree": "Začni brezplačno sejo",
      "start.startCharging": "Začni polnjenje",
      "start.backToMap": "Nazaj na zemljevid",
      "start.finishPreviousCheckout": "Dokončajte ali prekličite prejšnji checkout, preden začnete novo sejo na tem priključku.",
      "start.freeInfo": "Brezplačno polnjenje je omogočeno. Plačilo ne bo izvedeno.",
      "start.secureInfo": "Varno plačilo prek Stripe. Zaračuna se samo dejanski strošek.",
      "result.newSession": "Začni novo sejo"
    },
    it: {
      "header.help": "Aiuto",
      "header.support": "Supporto",
      "header.scanQr": "Scansiona QR",
      "nav.map": "Mappa",
      "nav.help": "Come funziona",
      "nav.contact": "Contatto",
      "lang.label": "Lingua",
      "qr.title": "Scansiona il QR del caricatore",
      "qr.subtitle": "Usa il QR sul caricatore per aprire la pagina esistente di avvio ricarica.",
      "qr.unsupported": "Il QR scansionato non è supportato. Usa un link esistente /cp/... o /Public/Start.",
      "qr.camera": "La fotocamera non è disponibile o il permesso è stato negato.",
      "map.title": "Mappa di ricarica",
      "map.stations": "stazioni",
      "map.gps": "GPS",
      "map.expand": "Espandi",
      "map.noCoords": "Nessun dato mappa disponibile (coordinate mancanti).",
      "map.available": "Disponibile",
      "map.busy": "Occupato",
      "map.offline": "Offline",
      "map.search": "Cerca per nome, località o codice pubblico della colonnina...",
      "map.stationsOnMap": "Stazioni sulla mappa",
      "map.noStations": "Nessuna stazione configurata.",
      "map.noVisibleStations": "Nessuna stazione corrisponde all'area visibile della mappa.",
      "map.connectors": "connettori",
      "map.freeEnergy": "Energia gratuita",
      "map.sessionFee": "Costo sessione",
      "map.idleFee": "Costo inattività",
      "map.grace": "grace",
      "map.minutes": "min",
      "map.chooseConnector": "Scegli connettore",
      "map.startSession": "Avvia sessione",
      "start.chooseConnector": "Scegli connettore",
      "start.prices": "Prezzi e costi",
      "start.companyInvoice": "Fattura aziendale",
      "start.companyName": "Nome azienda (opzionale)",
      "start.companyNamePlaceholder": "Nome azienda / organizzazione",
      "start.oib": "OIB (richiesto per R1)",
      "start.oibPlaceholder": "11 cifre",
      "start.startFree": "Avvia sessione gratuita",
      "start.startCharging": "Avvia ricarica",
      "start.backToMap": "Torna alla mappa",
      "start.finishPreviousCheckout": "Completa o annulla il checkout precedente prima di avviare una nuova sessione su questo connettore.",
      "start.freeInfo": "Ricarica gratuita abilitata. Nessun pagamento verrà addebitato.",
      "start.secureInfo": "Pagamento sicuro tramite Stripe. Viene addebitato solo il costo effettivo.",
      "result.newSession": "Avvia una nuova sessione"
    },
    de: {
      "header.help": "Hilfe",
      "header.support": "Support",
      "header.scanQr": "QR scannen",
      "nav.map": "Karte",
      "nav.help": "So funktioniert es",
      "nav.contact": "Kontakt",
      "lang.label": "Sprache",
      "qr.title": "QR-Code der Ladestation scannen",
      "qr.subtitle": "Scannen Sie den QR-Code am Ladepunkt, um die vorhandene Startseite zu öffnen.",
      "qr.unsupported": "Der gescannte QR-Code wird nicht unterstützt. Verwenden Sie einen vorhandenen /cp/...- oder /Public/Start-Link.",
      "qr.camera": "Kamera nicht verfügbar oder Berechtigung verweigert.",
      "map.title": "Ladekarte",
      "map.stations": "Stationen",
      "map.gps": "GPS",
      "map.expand": "Vergrößern",
      "map.noCoords": "Keine Kartendaten verfügbar (fehlende Koordinaten).",
      "map.available": "Verfügbar",
      "map.busy": "Belegt",
      "map.offline": "Offline",
      "map.search": "Nach Name, Ort oder öffentlichem Ladecode suchen...",
      "map.stationsOnMap": "Stationen auf der Karte",
      "map.noStations": "Noch keine Stationen konfiguriert.",
      "map.noVisibleStations": "Keine Stationen entsprechen dem aktuell sichtbaren Kartenausschnitt.",
      "map.connectors": "Anschlüsse",
      "map.freeEnergy": "Kostenlose Energie",
      "map.sessionFee": "Sitzungsgebühr",
      "map.idleFee": "Standgebühr",
      "map.grace": "grace",
      "map.minutes": "Min",
      "map.chooseConnector": "Anschluss wählen",
      "map.startSession": "Sitzung starten",
      "start.chooseConnector": "Anschluss wählen",
      "start.prices": "Preise und Gebühren",
      "start.companyInvoice": "Firmenrechnung",
      "start.companyName": "Firmenname (optional)",
      "start.companyNamePlaceholder": "Firma / Organisation",
      "start.oib": "OIB (für R1 erforderlich)",
      "start.oibPlaceholder": "11 Ziffern",
      "start.startFree": "Kostenlose Sitzung starten",
      "start.startCharging": "Ladevorgang starten",
      "start.backToMap": "Zurück zur Karte",
      "start.finishPreviousCheckout": "Schließen Sie den vorherigen Checkout ab oder stornieren Sie ihn, bevor Sie auf diesem Anschluss eine neue Sitzung starten.",
      "start.freeInfo": "Kostenloses Laden ist aktiviert. Es wird nichts belastet.",
      "start.secureInfo": "Sichere Zahlung über Stripe. Es werden nur die tatsächlichen Kosten berechnet.",
      "result.newSession": "Neue Sitzung starten"
    },
    fr: {
      "header.help": "Aide",
      "header.support": "Support",
      "header.scanQr": "Scanner QR",
      "nav.map": "Carte",
      "nav.help": "Comment ça marche",
      "nav.contact": "Contact",
      "lang.label": "Langue",
      "qr.title": "Scanner le QR du chargeur",
      "qr.subtitle": "Scannez le QR imprimé sur la borne pour ouvrir sa page de démarrage existante.",
      "qr.unsupported": "Le QR scanné n'est pas pris en charge. Utilisez un lien /cp/... ou /Public/Start existant.",
      "qr.camera": "La caméra n'est pas disponible ou l'autorisation a été refusée.",
      "map.title": "Carte de recharge",
      "map.stations": "stations",
      "map.gps": "GPS",
      "map.expand": "Agrandir",
      "map.noCoords": "Aucune donnée de carte disponible (coordonnées manquantes).",
      "map.available": "Disponible",
      "map.busy": "Occupé",
      "map.offline": "Hors ligne",
      "map.search": "Rechercher par nom, lieu ou code public de borne...",
      "map.stationsOnMap": "Bornes sur la carte",
      "map.noStations": "Aucune borne configurée pour le moment.",
      "map.noVisibleStations": "Aucune borne ne correspond à la zone actuellement visible sur la carte.",
      "map.connectors": "connecteurs",
      "map.freeEnergy": "Énergie gratuite",
      "map.sessionFee": "Frais de session",
      "map.idleFee": "Frais d'inactivité",
      "map.grace": "grace",
      "map.minutes": "min",
      "map.chooseConnector": "Choisir un connecteur",
      "map.startSession": "Démarrer la session",
      "start.chooseConnector": "Choisir un connecteur",
      "start.prices": "Tarifs et frais",
      "start.companyInvoice": "Facture société",
      "start.companyName": "Nom de l'entreprise (optionnel)",
      "start.companyNamePlaceholder": "Entreprise / organisation",
      "start.oib": "OIB (requis pour R1)",
      "start.oibPlaceholder": "11 chiffres",
      "start.startFree": "Démarrer une session gratuite",
      "start.startCharging": "Démarrer la recharge",
      "start.backToMap": "Retour à la carte",
      "start.finishPreviousCheckout": "Terminez ou annulez le checkout précédent avant de démarrer une nouvelle session sur ce connecteur.",
      "start.freeInfo": "Recharge gratuite activée. Aucun paiement ne sera débité.",
      "start.secureInfo": "Paiement sécurisé via Stripe. Seul le coût réel est débité.",
      "result.newSession": "Démarrer une nouvelle session"
    }
  };

  const statusTranslations = {
    hr: {
      "status.step.start": "① Početak",
      "status.step.wait": "② Čekaj",
      "status.step.charge": "③ Punjenje",
      "status.step.done": "④ Gotovo",
      "status.step.error": "⑤ Greška",
      "status.badge.waiting": "Čekanje na punjač...",
      "status.badge.awaitingPlug": "Spojite kabel...",
      "status.badge.charging": "Punjenje u tijeku",
      "status.badge.pausedVehicle": "Pauzirano od vozila / idle",
      "status.badge.pausedCharger": "Pauzirano od punjača",
      "status.badge.finishing": "Završavanje sesije",
      "status.badge.stopPending": "Punjenje zaustavljeno, ištekajte vozilo",
      "status.badge.done": "Punjenje završeno",
      "status.badge.error": "Greška",
      "status.badge.stopFailed": "Zaustavljanje nije uspjelo",
      "status.reservation": "Rezervacija",
      "status.label.status": "Status",
      "status.label.connector": "Priključak",
      "status.label.authorized": "Autorizirano",
      "status.label.currentTotal": "Potrošnja",
      "status.label.sessionDuration": "Trajanje sesije",
      "status.section.progress": "Napredak",
      "status.section.costBreakdown": "Pregled troškova",
      "status.section.costSummary": "Sažetak troškova",
      "status.section.invoice": "Račun",
      "status.label.energy": "Energija",
      "status.label.sessionFee": "Naknada sesije",
      "status.label.idleFee": "Idle naknada",
      "status.label.chargingStarted": "Punjenje započelo",
      "status.label.disconnected": "Ištekanje",
      "status.label.totalCharged": "Ukupno naplaćeno",
      "status.label.total": "UKUPNO",
      "status.label.totalTime": "UKUPNO VRIJEME",
      "status.label.refundToCard": "Povrat na karticu",
      "status.label.invoiceStatus": "Status",
      "status.label.invoiceNumber": "Broj računa",
      "status.label.updated": "Ažurirano",
      "status.label.paymentStatus": "Status plaćanja",
      "status.label.lastStatus": "Zadnji status",
      "status.hint.waitDefault": "Provjerite je li kabel spojen na vozilo. Punjač bi uskoro trebao pokrenuti sesiju.",
      "status.wait.starting": "Pokretanje...",
      "status.hint.awaitingPlug": "Spojite kabel na vozilo za nastavak.",
      "status.hint.awaitingPlugTimed": "Spojite kabel na vozilo za nastavak. Preostalo vrijeme: {minutes} min.",
      "status.hint.starting": "Punjač pokreće sesiju, pričekajte trenutak.",
      "status.hint.pausedVehicle": "Punjenje je pauzirano jer vozilo više ne vuče snagu. Idle naknada može krenuti nakon grace perioda.",
      "status.hint.pausedVehicleQuiet": "Punjenje je pauzirano, ali je idle naplata trenutno pauzirana zbog mirnog razdoblja.",
      "status.hint.pausedCharger": "Punjenje je pauzirano od strane punjača ili postaje. Možete pričekati ili zaustaviti sesiju.",
      "status.hint.stopPending": "Punjenje je zaustavljeno. Sesija će se završiti tek kada ištekate vozilo.",
      "status.hint.stopPendingQuiet": "Punjenje je zaustavljeno. Sesija završava nakon ištekavanja, a idle naplata je trenutno pauzirana zbog mirnog razdoblja.",
      "status.hint.notReady": "Punjač još nije spreman. Provjerite kabel i pokušajte ponovno.",
      "status.hint.stopFailed": "Punjač još nije potvrdio zahtjev za zaustavljanje. Pričekajte trenutak i pokušajte ponovno.",
      "status.hint.errorDefault": "Punjač je trenutno nedostupan ili zauzet. Provjerite kabel i pokušajte ponovno.",
      "status.idlePausedByWindow": "Naplata zauzeća je pauzirana tijekom mirnog razdoblja.",
      "status.done.thankYou": "Hvala!",
      "status.done.completed": "Sesija je uspješno završena",
      "status.error.title": "Punjač nije uspio pokrenuti sesiju",
      "status.error.noFunds": "Sredstva nisu naplaćena",
      "status.action.cancelSession": "Otkaži sesiju",
      "status.action.stopCharging": "Zaustavi punjenje",
      "status.action.stopping": "Zaustavljanje...",
      "status.action.backToMap": "Natrag na kartu",
      "status.action.openInvoice": "Otvori račun",
      "status.action.tryAgain": "Pokušaj ponovno",
      "status.action.contactSupport": "Kontaktiraj podršku",
      "status.action.saveR1": "Spremi R1 podatke",
      "status.action.savingR1": "Spremanje R1 podataka...",
      "status.footer.autoRefresh": "Stranica se osvježava automatski",
      "status.footer.dataRefreshes": "Podaci se osvježavaju svakih 5 sekundi",
      "status.footer.poweredBy": "Pokreće",
      "status.footer.needHelp": "Trebate pomoć?",
      "status.r1.title": "R1 račun (tvrtka)",
      "status.r1.subtitle": "Trebate R1 račun? Podatke tvrtke možete poslati sada ili kasnije putem ove sigurne poveznice na sesiju.",
      "status.r1.companyName": "Naziv tvrtke (opcionalno)",
      "status.r1.oib": "OIB (obavezno za R1)",
      "status.r1.oibHelp": "OIB se provjerava prije spremanja zahtjeva.",
      "status.r1.invalidOib": "Unesite ispravan OIB (11 znamenki).",
      "status.r1.saved": "R1 podaci su uspješno spremljeni.",
      "status.r1.failed": "R1 podatke trenutno nije moguće spremiti. Pokušajte ponovno.",
      "status.refundCalc": "Autorizacija {authorized} - naplaćeno {captured} = povrat {refund}"
    },
    en: {
      "status.step.start": "① Start",
      "status.step.wait": "② Wait",
      "status.step.charge": "③ Charge",
      "status.step.done": "④ Done",
      "status.step.error": "⑤ Error",
      "status.badge.waiting": "Waiting for charger...",
      "status.badge.awaitingPlug": "Plug in cable...",
      "status.badge.charging": "Charging in progress",
      "status.badge.pausedVehicle": "Paused by vehicle / idle",
      "status.badge.pausedCharger": "Paused by charger",
      "status.badge.finishing": "Finishing session",
      "status.badge.stopPending": "Charging stopped, unplug vehicle",
      "status.badge.done": "Charging complete",
      "status.badge.error": "Error",
      "status.badge.stopFailed": "Unable to stop session",
      "status.reservation": "Reservation",
      "status.label.status": "Status",
      "status.label.connector": "Connector",
      "status.label.authorized": "Authorized",
      "status.label.currentTotal": "Consumption",
      "status.label.sessionDuration": "Session duration",
      "status.section.progress": "Progress",
      "status.section.costBreakdown": "Cost breakdown",
      "status.section.costSummary": "Cost summary",
      "status.section.invoice": "Invoice",
      "status.label.energy": "Energy",
      "status.label.sessionFee": "Session fee",
      "status.label.idleFee": "Idle fee",
      "status.label.chargingStarted": "Charging started",
      "status.label.disconnected": "Disconnected",
      "status.label.totalCharged": "Total charged",
      "status.label.total": "TOTAL",
      "status.label.totalTime": "TOTAL TIME",
      "status.label.refundToCard": "Refund to card",
      "status.label.invoiceStatus": "Status",
      "status.label.invoiceNumber": "Invoice number",
      "status.label.updated": "Updated",
      "status.label.paymentStatus": "Payment status",
      "status.label.lastStatus": "Last status",
      "status.hint.waitDefault": "Make sure the cable is plugged into your vehicle. The charger should start the session shortly.",
      "status.wait.starting": "Starting...",
      "status.hint.awaitingPlug": "Connect the cable to your vehicle to continue.",
      "status.hint.awaitingPlugTimed": "Connect the cable to your vehicle to continue. Time remaining: {minutes} min.",
      "status.hint.starting": "Charger is starting the session, please wait.",
      "status.hint.pausedVehicle": "Charging has paused because the vehicle stopped drawing power. Idle fees may continue after the grace period.",
      "status.hint.pausedVehicleQuiet": "Charging is paused, but idle billing is currently paused because of the quiet-hours window.",
      "status.hint.pausedCharger": "Charging is paused by the charger or station controls. You can wait or stop the session.",
      "status.hint.stopPending": "Charging has stopped. The session finishes only after you unplug the vehicle.",
      "status.hint.stopPendingQuiet": "Charging has stopped. The session finishes after unplug, and idle billing is currently paused because of the quiet-hours window.",
      "status.hint.notReady": "Charger is not ready yet. Please check the cable and try again.",
      "status.hint.stopFailed": "The charger did not confirm the stop request yet. Please wait a moment and try again.",
      "status.hint.errorDefault": "The charger is currently offline or occupied. Check the cable and try again.",
      "status.idlePausedByWindow": "Occupancy billing is paused during the quiet-hours window.",
      "status.done.thankYou": "Thank you!",
      "status.done.completed": "Session completed successfully",
      "status.error.title": "Charger failed to start the session",
      "status.error.noFunds": "No funds were charged",
      "status.action.cancelSession": "Cancel session",
      "status.action.stopCharging": "Stop charging",
      "status.action.stopping": "Stopping...",
      "status.action.backToMap": "Back to map",
      "status.action.openInvoice": "Open invoice",
      "status.action.tryAgain": "Try again",
      "status.action.contactSupport": "Contact support",
      "status.action.saveR1": "Save R1 details",
      "status.action.savingR1": "Saving R1 details...",
      "status.footer.autoRefresh": "Page refreshes automatically",
      "status.footer.dataRefreshes": "Data refreshes automatically every 5 seconds",
      "status.footer.poweredBy": "Powered by",
      "status.footer.needHelp": "Need help?",
      "status.r1.title": "R1 invoice (company)",
      "status.r1.subtitle": "Need an R1 invoice? You can submit company details now or later using this secure session link.",
      "status.r1.companyName": "Company name (optional)",
      "status.r1.oib": "OIB (required for R1)",
      "status.r1.oibHelp": "OIB is validated before the request is saved.",
      "status.r1.invalidOib": "Please enter a valid OIB (11 digits).",
      "status.r1.saved": "R1 details saved successfully.",
      "status.r1.failed": "Unable to save R1 details right now. Please try again.",
      "status.refundCalc": "Pre-auth {authorized} - charged {captured} = refund {refund}"
    }
  };

  Object.keys(statusTranslations).forEach((lang) => {
    if (translations[lang]) {
      Object.assign(translations[lang], statusTranslations[lang]);
    }
  });

  let qrScanner = null;

  function currentLanguage() {
    const params = new URLSearchParams(window.location.search);
    const fromQuery = params.get("lang");
    if (fromQuery && languages.includes(fromQuery)) {
      localStorage.setItem("publicPortalLang", fromQuery);
      return fromQuery;
    }

    const stored = localStorage.getItem("publicPortalLang");
    return languages.includes(stored) ? stored : "en";
  }

  function textFor(lang, key) {
    return translations[lang]?.[key] || translations.en[key] || key;
  }

  function applyTranslations(lang) {
    document.documentElement.lang = lang;
    document.querySelectorAll("[data-i18n]").forEach((element) => {
      const key = element.getAttribute("data-i18n");
      const value = textFor(lang, key);
      if (value) {
        element.textContent = value;
      }
    });

    document.querySelectorAll("[data-i18n-placeholder]").forEach((element) => {
      const key = element.getAttribute("data-i18n-placeholder");
      const value = textFor(lang, key);
      if (value) {
        element.setAttribute("placeholder", value);
      }
    });

    document.querySelectorAll("[data-public-lang]").forEach((button) => {
      button.classList.toggle("is-active", button.getAttribute("data-public-lang") === lang);
    });
  }

  function buildNavigationTarget(rawValue) {
    if (!rawValue) {
      return null;
    }

    const trimmed = rawValue.trim();
    if (!trimmed) {
      return null;
    }

    try {
      const url = trimmed.startsWith("http://") || trimmed.startsWith("https://")
        ? new URL(trimmed)
        : new URL(trimmed, window.location.origin);

      const path = url.pathname || "";
      const isCpPath = /^\/cp\/[^/]+(?:\/\d+)?\/?$/i.test(path);
      const isLegacyStart = /^\/Public\/Start$/i.test(path) && !!url.searchParams.get("cp");
      if (!isCpPath && !isLegacyStart) {
        return null;
      }

      return url.toString();
    } catch (_error) {
      return null;
    }
  }

  async function stopScanner() {
    if (!qrScanner) {
      return;
    }

    try {
      await qrScanner.stop();
    } catch (_error) {
      // ignore
    }

    try {
      await qrScanner.clear();
    } catch (_error) {
      // ignore
    }

    qrScanner = null;
  }

  function setQrError(message) {
    const error = document.getElementById("publicQrError");
    if (!error) {
      return;
    }

    if (!message) {
      error.hidden = true;
      error.textContent = "";
      return;
    }

    error.hidden = false;
    error.textContent = message;
  }

  async function openQrModal() {
    const modal = document.getElementById("publicQrModal");
    const reader = document.getElementById("publicQrReader");
    if (!modal || !reader) {
      return;
    }

    modal.hidden = false;
    setQrError(null);

    if (typeof Html5Qrcode === "undefined") {
      setQrError(textFor(currentLanguage(), "qr.camera"));
      return;
    }

    qrScanner = new Html5Qrcode("publicQrReader");
    try {
      await qrScanner.start(
        { facingMode: "environment" },
        { fps: 10, qrbox: { width: 240, height: 240 } },
        (decodedText) => {
          const target = buildNavigationTarget(decodedText);
          if (!target) {
            setQrError(textFor(currentLanguage(), "qr.unsupported"));
            return;
          }

          window.location.href = target;
        },
        () => {}
      );
    } catch (_error) {
      setQrError(textFor(currentLanguage(), "qr.camera"));
    }
  }

  async function closeQrModal() {
    const modal = document.getElementById("publicQrModal");
    if (modal) {
      modal.hidden = true;
    }
    setQrError(null);
    await stopScanner();
  }

  function wireQrModal() {
    const openButton = document.getElementById("publicQrOpen");
    const closeButton = document.getElementById("publicQrClose");
    const modal = document.getElementById("publicQrModal");

    if (openButton) {
      openButton.addEventListener("click", () => {
        void openQrModal();
      });
    }

    if (closeButton) {
      closeButton.addEventListener("click", () => {
        void closeQrModal();
      });
    }

    if (modal) {
      modal.addEventListener("click", (event) => {
        if (event.target === modal) {
          void closeQrModal();
        }
      });
    }
  }

  function wireLanguageButtons() {
    document.querySelectorAll("[data-public-lang]").forEach((button) => {
      button.addEventListener("click", () => {
        const lang = button.getAttribute("data-public-lang");
        if (!languages.includes(lang)) {
          return;
        }

        localStorage.setItem("publicPortalLang", lang);
        applyTranslations(lang);
      });
    });
  }

  document.addEventListener("DOMContentLoaded", () => {
    const lang = currentLanguage();
    applyTranslations(lang);
    wireLanguageButtons();
    wireQrModal();
  });

  window.publicPortalI18n = {
    currentLanguage,
    textFor,
    applyTranslations
  };
})();
