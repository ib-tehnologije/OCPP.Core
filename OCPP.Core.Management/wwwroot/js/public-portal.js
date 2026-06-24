(function () {
  const languages = ["hr", "en", "sl", "it", "de", "fr"];
  const translations = {
    hr: {
      "header.help": "Upute",
      "header.support": "Podrška",
      "header.scanQr": "Skeniraj QR",
      "brand.tagline": "Koristite brze punjače s jasnim cijenama i trenutnim pokretanjem sesije.",
      "nav.map": "Karta",
      "nav.help": "Kako radi",
      "nav.contact": "Kontakt",
      "lang.label": "Jezik",
      "qr.title": "Skeniraj QR kod punjača",
      "qr.subtitle": "Skeniraj naljepnicu na punjaču i otvori postojeću stranicu za pokretanje punjenja.",
      "qr.unsupported": "QR kod nije prepoznat. Koristite postojeći /cp/..., /evse/... ili /Public/Start link.",
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
      "map.grace": "tolerancija",
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
      "start.idleFreeWindow": "besplatno",
      "result.newSession": "Pokreni novu sesiju"
    },
    en: {
      "header.help": "Help",
      "header.support": "Support",
      "header.scanQr": "Scan QR",
      "brand.tagline": "Use fast chargers with clear pricing and instant session start.",
      "nav.map": "Map",
      "nav.help": "How it works",
      "nav.contact": "Contact",
      "lang.label": "Language",
      "qr.title": "Scan charger QR code",
      "qr.subtitle": "Use the QR code printed on the charger sticker to open its existing start page.",
      "qr.unsupported": "The scanned QR code is not supported. Use an existing /cp/..., /evse/... or /Public/Start link.",
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
      "start.idleFreeWindow": "free",
      "result.newSession": "Start a new session"
    },
    sl: {
      "header.help": "Pomoč",
      "header.support": "Podpora",
      "header.scanQr": "Skeniraj QR",
      "brand.tagline": "Uporabljajte hitre polnilnice z jasnimi cenami in takojšnjim začetkom seje.",
      "nav.map": "Zemljevid",
      "nav.help": "Kako deluje",
      "nav.contact": "Kontakt",
      "lang.label": "Jezik",
      "qr.title": "Skeniraj QR kodo polnilnice",
      "qr.subtitle": "Skenirajte kodo na polnilnici in odprite obstoječo stran za začetek polnjenja.",
      "qr.unsupported": "Skenirana QR koda ni podprta. Uporabite obstoječo povezavo /cp/..., /evse/... ali /Public/Start.",
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
      "map.grace": "toleranca",
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
      "start.idleFreeWindow": "brezplačno",
      "result.newSession": "Začni novo sejo"
    },
    it: {
      "header.help": "Aiuto",
      "header.support": "Supporto",
      "header.scanQr": "Scansiona QR",
      "brand.tagline": "Usa le colonnine rapide con prezzi chiari e avvio immediato della sessione.",
      "nav.map": "Mappa",
      "nav.help": "Come funziona",
      "nav.contact": "Contatto",
      "lang.label": "Lingua",
      "qr.title": "Scansiona il QR del caricatore",
      "qr.subtitle": "Usa il QR sul caricatore per aprire la pagina esistente di avvio ricarica.",
      "qr.unsupported": "Il QR scansionato non è supportato. Usa un link esistente /cp/..., /evse/... o /Public/Start.",
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
      "map.grace": "tolleranza",
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
      "start.idleFreeWindow": "gratis",
      "result.newSession": "Avvia una nuova sessione"
    },
    de: {
      "header.help": "Hilfe",
      "header.support": "Support",
      "header.scanQr": "QR scannen",
      "brand.tagline": "Nutzen Sie Schnellladepunkte mit transparenten Preisen und sofortigem Sitzungsstart.",
      "nav.map": "Karte",
      "nav.help": "So funktioniert es",
      "nav.contact": "Kontakt",
      "lang.label": "Sprache",
      "qr.title": "QR-Code der Ladestation scannen",
      "qr.subtitle": "Scannen Sie den QR-Code am Ladepunkt, um die vorhandene Startseite zu öffnen.",
      "qr.unsupported": "Der gescannte QR-Code wird nicht unterstützt. Verwenden Sie einen vorhandenen /cp/...-, /evse/...- oder /Public/Start-Link.",
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
      "map.grace": "Kulanz",
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
      "start.idleFreeWindow": "kostenfrei",
      "result.newSession": "Neue Sitzung starten"
    },
    fr: {
      "header.help": "Aide",
      "header.support": "Support",
      "header.scanQr": "Scanner QR",
      "brand.tagline": "Utilisez des bornes rapides avec des tarifs clairs et un démarrage immédiat de la session.",
      "nav.map": "Carte",
      "nav.help": "Comment ça marche",
      "nav.contact": "Contact",
      "lang.label": "Langue",
      "qr.title": "Scanner le QR du chargeur",
      "qr.subtitle": "Scannez le QR imprimé sur la borne pour ouvrir sa page de démarrage existante.",
      "qr.unsupported": "Le QR scanné n'est pas pris en charge. Utilisez un lien /cp/..., /evse/... ou /Public/Start existant.",
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
      "map.grace": "tolérance",
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
      "start.idleFreeWindow": "gratuit",
      "result.newSession": "Démarrer une nouvelle session"
    }
  };

  const startTranslations = {
    hr: {
      "start.step.start": "① Početak",
      "start.step.wait": "② Čekanje",
      "start.step.charge": "③ Punjenje",
      "start.step.done": "④ Gotovo",
      "start.step.error": "⑤ Greška",
      "start.status.available": "Dostupno",
      "start.status.freeCharging": "Besplatno punjenje",
      "start.status.offline": "Offline",
      "start.status.occupied": "Zauzeto",
      "start.status.busy": "Zauzeto",
      "start.status.charging": "Punjenje",
      "start.status.preparing": "Priprema",
      "start.status.reserved": "Rezervirano",
      "start.status.unavailable": "Nedostupno",
      "start.status.faulted": "Greška",
      "start.status.finishing": "Završavanje",
      "start.status.suspendedEv": "Pauzirano od vozila",
      "start.status.suspendedEvse": "Pauzirano od punjača",
      "start.status.waiting": "Čekanje",
      "start.tapToSwitch": "Dodirnite za promjenu",
      "start.connectorSelected": "Odabrani priključak",
      "start.energy": "Energija",
      "start.free": "Besplatno",
      "start.energyUnit": "energija",
      "start.sessionFee": "Naknada sesije",
      "start.idleFee": "Naknada mirovanja",
      "start.chargedAfterChargingEnds": "Naplata nakon završetka punjenja",
      "start.chargedFromSessionStart": "Naplata od početka sesije",
      "start.grace": "tolerancija",
      "start.max": "max",
      "start.maxEnergy": "Maks. energija",
      "start.preAuthorization": "Predautorizacija (proc.)",
      "start.finalAmountNote": "Konačni iznos = energija + naknada sesije + naknada mirovanja (ako postoji). Razlika od predautorizacije automatski se vraća.",
      "start.r1Hint": "Ako trebate R1 račun, unesite podatke tvrtke niže ili ih dodajte kasnije na stranici statusa sesije.",
      "start.r1MetadataHint": "Ove podatke uključujemo u Stripe metadata kako biste mogli izdati R1 račun.",
      "start.recoveryReservation": "Rezervacija",
      "start.recoveryStatus": "status",
      "start.resumePayment": "Nastavi plaćanje",
      "start.cancelPreviousAttempt": "Otkaži prethodni pokušaj"
    },
    en: {
      "start.step.start": "① Start",
      "start.step.wait": "② Wait",
      "start.step.charge": "③ Charge",
      "start.step.done": "④ Done",
      "start.step.error": "⑤ Error",
      "start.status.available": "Available",
      "start.status.freeCharging": "Free charging",
      "start.status.offline": "Offline",
      "start.status.occupied": "Occupied",
      "start.status.busy": "Busy",
      "start.status.charging": "Charging",
      "start.status.preparing": "Preparing",
      "start.status.reserved": "Reserved",
      "start.status.unavailable": "Unavailable",
      "start.status.faulted": "Faulted",
      "start.status.finishing": "Finishing",
      "start.status.suspendedEv": "Suspended by vehicle",
      "start.status.suspendedEvse": "Suspended by charger",
      "start.status.waiting": "Waiting",
      "start.tapToSwitch": "Tap to switch",
      "start.connectorSelected": "Selected connector",
      "start.energy": "Energy",
      "start.free": "Free",
      "start.energyUnit": "energy",
      "start.sessionFee": "Session fee",
      "start.idleFee": "Idle fee",
      "start.chargedAfterChargingEnds": "Charged after charging ends",
      "start.chargedFromSessionStart": "Charged from session start",
      "start.grace": "toleranca",
      "start.max": "max",
      "start.maxEnergy": "Max. energy",
      "start.preAuthorization": "Pre-authorization (est.)",
      "start.finalAmountNote": "Final amount = energy + session fee + idle fee (if any). Difference from pre-auth is refunded automatically.",
      "start.r1Hint": "If you need an R1 invoice, enter your company info below or add it later from the session status page.",
      "start.r1MetadataHint": "We will include this information in Stripe metadata so you can issue an R1 invoice.",
      "start.recoveryReservation": "Reservation",
      "start.recoveryStatus": "status",
      "start.resumePayment": "Resume payment",
      "start.cancelPreviousAttempt": "Cancel previous attempt"
    },
    sl: {
      "start.step.start": "① Začetek",
      "start.step.wait": "② Čakanje",
      "start.step.charge": "③ Polnjenje",
      "start.step.done": "④ Končano",
      "start.step.error": "⑤ Napaka",
      "start.status.available": "Na voljo",
      "start.status.freeCharging": "Brezplačno polnjenje",
      "start.status.offline": "Offline",
      "start.status.occupied": "Zasedeno",
      "start.status.busy": "Zasedeno",
      "start.status.charging": "Polnjenje",
      "start.status.preparing": "Priprava",
      "start.status.reserved": "Rezervirano",
      "start.status.unavailable": "Nedostopno",
      "start.status.faulted": "Napaka",
      "start.status.finishing": "Zaključevanje",
      "start.status.suspendedEv": "Pavza vozila",
      "start.status.suspendedEvse": "Pavza polnilnice",
      "start.status.waiting": "Čakanje",
      "start.tapToSwitch": "Tapnite za zamenjavo",
      "start.connectorSelected": "Izbrani priključek",
      "start.energy": "Energija",
      "start.free": "Brezplačno",
      "start.energyUnit": "energija",
      "start.sessionFee": "Pristojbina seje",
      "start.idleFee": "Pristojbina mirovanja",
      "start.chargedAfterChargingEnds": "Zaračunano po koncu polnjenja",
      "start.chargedFromSessionStart": "Zaračunano od začetka seje",
      "start.grace": "grace",
      "start.max": "max",
      "start.maxEnergy": "Maks. energija",
      "start.preAuthorization": "Predavtorizacija (ocena)",
      "start.finalAmountNote": "Končni znesek = energija + pristojbina seje + pristojbina mirovanja (če obstaja). Razlika od predavtorizacije se samodejno vrne.",
      "start.r1Hint": "Če potrebujete račun za podjetje, vnesite podatke spodaj ali jih dodajte pozneje na strani statusa seje.",
      "start.r1MetadataHint": "Te podatke bomo vključili v Stripe metadata, da lahko izdate R1 račun.",
      "start.recoveryReservation": "Rezervacija",
      "start.recoveryStatus": "status",
      "start.resumePayment": "Nadaljuj plačilo",
      "start.cancelPreviousAttempt": "Prekliči prejšnji poskus"
    },
    it: {
      "start.step.start": "① Avvio",
      "start.step.wait": "② Attesa",
      "start.step.charge": "③ Ricarica",
      "start.step.done": "④ Fine",
      "start.step.error": "⑤ Errore",
      "start.status.available": "Disponibile",
      "start.status.freeCharging": "Ricarica gratuita",
      "start.status.offline": "Offline",
      "start.status.occupied": "Occupato",
      "start.status.busy": "Occupato",
      "start.status.charging": "In ricarica",
      "start.status.preparing": "Preparazione",
      "start.status.reserved": "Riservato",
      "start.status.unavailable": "Non disponibile",
      "start.status.faulted": "Errore",
      "start.status.finishing": "Completamento",
      "start.status.suspendedEv": "Pausa dal veicolo",
      "start.status.suspendedEvse": "Pausa dal caricatore",
      "start.status.waiting": "In attesa",
      "start.tapToSwitch": "Tocca per cambiare",
      "start.connectorSelected": "Connettore selezionato",
      "start.energy": "Energia",
      "start.free": "Gratis",
      "start.energyUnit": "energia",
      "start.sessionFee": "Costo sessione",
      "start.idleFee": "Costo inattività",
      "start.chargedAfterChargingEnds": "Addebitato dopo la fine della ricarica",
      "start.chargedFromSessionStart": "Addebitato dall'inizio sessione",
      "start.grace": "tolleranza",
      "start.max": "max",
      "start.maxEnergy": "Energia max",
      "start.preAuthorization": "Pre-autorizzazione (stim.)",
      "start.finalAmountNote": "Importo finale = energia + costo sessione + costo inattività (se presente). La differenza dalla pre-autorizzazione viene rimborsata automaticamente.",
      "start.r1Hint": "Se ti serve una fattura R1, inserisci i dati aziendali qui sotto o aggiungili più tardi dalla pagina di stato sessione.",
      "start.r1MetadataHint": "Includeremo queste informazioni nei metadati Stripe per permettere l'emissione della fattura R1.",
      "start.recoveryReservation": "Prenotazione",
      "start.recoveryStatus": "stato",
      "start.resumePayment": "Riprendi pagamento",
      "start.cancelPreviousAttempt": "Annulla tentativo precedente"
    },
    de: {
      "start.step.start": "① Start",
      "start.step.wait": "② Warten",
      "start.step.charge": "③ Laden",
      "start.step.done": "④ Fertig",
      "start.step.error": "⑤ Fehler",
      "start.status.available": "Verfügbar",
      "start.status.freeCharging": "Kostenloses Laden",
      "start.status.offline": "Offline",
      "start.status.occupied": "Belegt",
      "start.status.busy": "Belegt",
      "start.status.charging": "Lädt",
      "start.status.preparing": "Vorbereitung",
      "start.status.reserved": "Reserviert",
      "start.status.unavailable": "Nicht verfügbar",
      "start.status.faulted": "Fehler",
      "start.status.finishing": "Wird beendet",
      "start.status.suspendedEv": "Vom Fahrzeug pausiert",
      "start.status.suspendedEvse": "Vom Ladepunkt pausiert",
      "start.status.waiting": "Warten",
      "start.tapToSwitch": "Zum Wechseln tippen",
      "start.connectorSelected": "Ausgewählter Anschluss",
      "start.energy": "Energie",
      "start.free": "Kostenlos",
      "start.energyUnit": "Energie",
      "start.sessionFee": "Sitzungsgebühr",
      "start.idleFee": "Standgebühr",
      "start.chargedAfterChargingEnds": "Berechnung nach Ladeende",
      "start.chargedFromSessionStart": "Berechnung ab Sitzungsstart",
      "start.grace": "Kulanz",
      "start.max": "max",
      "start.maxEnergy": "Max. Energie",
      "start.preAuthorization": "Vorautorisierung (gesch.)",
      "start.finalAmountNote": "Endbetrag = Energie + Sitzungsgebühr + Standgebühr (falls vorhanden). Die Differenz zur Vorautorisierung wird automatisch erstattet.",
      "start.r1Hint": "Wenn Sie eine Firmenrechnung benötigen, geben Sie unten die Firmendaten ein oder ergänzen Sie sie später auf der Sitzungsstatusseite.",
      "start.r1MetadataHint": "Wir übernehmen diese Informationen in die Stripe-Metadaten, damit eine R1-Rechnung ausgestellt werden kann.",
      "start.recoveryReservation": "Reservierung",
      "start.recoveryStatus": "Status",
      "start.resumePayment": "Zahlung fortsetzen",
      "start.cancelPreviousAttempt": "Vorherigen Versuch abbrechen"
    },
    fr: {
      "start.step.start": "① Début",
      "start.step.wait": "② Attente",
      "start.step.charge": "③ Recharge",
      "start.step.done": "④ Terminé",
      "start.step.error": "⑤ Erreur",
      "start.status.available": "Disponible",
      "start.status.freeCharging": "Recharge gratuite",
      "start.status.offline": "Hors ligne",
      "start.status.occupied": "Occupé",
      "start.status.busy": "Occupé",
      "start.status.charging": "Recharge",
      "start.status.preparing": "Préparation",
      "start.status.reserved": "Réservé",
      "start.status.unavailable": "Indisponible",
      "start.status.faulted": "Erreur",
      "start.status.finishing": "Finalisation",
      "start.status.suspendedEv": "Pause véhicule",
      "start.status.suspendedEvse": "Pause borne",
      "start.status.waiting": "Attente",
      "start.tapToSwitch": "Touchez pour changer",
      "start.connectorSelected": "Connecteur sélectionné",
      "start.energy": "Énergie",
      "start.free": "Gratuit",
      "start.energyUnit": "énergie",
      "start.sessionFee": "Frais de session",
      "start.idleFee": "Frais d'inactivité",
      "start.chargedAfterChargingEnds": "Facturé après la fin de recharge",
      "start.chargedFromSessionStart": "Facturé dès le début de session",
      "start.grace": "tolérance",
      "start.max": "max",
      "start.maxEnergy": "Énergie max",
      "start.preAuthorization": "Préautorisation (estim.)",
      "start.finalAmountNote": "Montant final = énergie + frais de session + frais d'inactivité (le cas échéant). La différence avec la préautorisation est remboursée automatiquement.",
      "start.r1Hint": "Si vous avez besoin d'une facture société, saisissez les informations ci-dessous ou ajoutez-les plus tard depuis la page de statut de session.",
      "start.r1MetadataHint": "Nous inclurons ces informations dans les métadonnées Stripe afin que vous puissiez émettre une facture R1.",
      "start.recoveryReservation": "Réservation",
      "start.recoveryStatus": "statut",
      "start.resumePayment": "Reprendre le paiement",
      "start.cancelPreviousAttempt": "Annuler la tentative précédente"
    }
  };

  Object.keys(startTranslations).forEach((lang) => {
    if (translations[lang]) {
      Object.assign(translations[lang], startTranslations[lang]);
    }
  });

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
      "status.label.currentTotal": "Procijenjeni iznos",
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
      "status.hint.maxEnergyStopping": "Dosegnut je konfigurirani limit sesije od {energy} kWh. Zaustavljanje punjenja je zatraženo.",
      "status.hint.maxEnergyReached": "Sesija je završila nakon dosezanja konfiguriranog limita od {energy} kWh.",
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
      "status.label.currentTotal": "Estimated total",
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
      "status.hint.maxEnergyStopping": "The configured session cap of {energy} kWh has been reached. A charging stop was requested.",
      "status.hint.maxEnergyReached": "This session ended after reaching the configured energy cap of {energy} kWh.",
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
    },
    sl: {
      "status.step.start": "① Začetek",
      "status.step.wait": "② Čakanje",
      "status.step.charge": "③ Polnjenje",
      "status.step.done": "④ Končano",
      "status.step.error": "⑤ Napaka",
      "status.badge.waiting": "Čakanje na polnilnico...",
      "status.badge.awaitingPlug": "Priključite kabel...",
      "status.badge.charging": "Polnjenje poteka",
      "status.badge.pausedVehicle": "Pavza vozila / mirovanje",
      "status.badge.pausedCharger": "Pavza polnilnice",
      "status.badge.finishing": "Zaključevanje seje",
      "status.badge.stopPending": "Polnjenje ustavljeno, odklopite vozilo",
      "status.badge.done": "Polnjenje končano",
      "status.badge.error": "Napaka",
      "status.badge.stopFailed": "Seje ni mogoče ustaviti",
      "status.reservation": "Rezervacija",
      "status.label.status": "Stanje",
      "status.label.connector": "Priključek",
      "status.label.authorized": "Avtorizirano",
      "status.label.currentTotal": "Ocenjeni znesek",
      "status.label.sessionDuration": "Trajanje seje",
      "status.section.progress": "Napredek",
      "status.section.costBreakdown": "Razčlenitev stroškov",
      "status.section.costSummary": "Povzetek stroškov",
      "status.section.invoice": "Račun",
      "status.label.energy": "Energija",
      "status.label.sessionFee": "Pristojbina seje",
      "status.label.idleFee": "Pristojbina mirovanja",
      "status.label.chargingStarted": "Polnjenje začeto",
      "status.label.disconnected": "Odklopljeno",
      "status.label.totalCharged": "Skupaj zaračunano",
      "status.label.total": "SKUPAJ",
      "status.label.totalTime": "SKUPNI ČAS",
      "status.label.refundToCard": "Vračilo na kartico",
      "status.label.invoiceStatus": "Stanje",
      "status.label.invoiceNumber": "Številka računa",
      "status.label.updated": "Posodobljeno",
      "status.label.paymentStatus": "Stanje plačila",
      "status.label.lastStatus": "Zadnje stanje",
      "status.hint.waitDefault": "Preverite, ali je kabel priključen v vozilo. Polnilnica bi morala kmalu začeti sejo.",
      "status.wait.starting": "Zaganjanje...",
      "status.hint.awaitingPlug": "Za nadaljevanje priključite kabel v vozilo.",
      "status.hint.awaitingPlugTimed": "Za nadaljevanje priključite kabel v vozilo. Preostali čas: {minutes} min.",
      "status.hint.starting": "Polnilnica začenja sejo, prosimo počakajte.",
      "status.hint.pausedVehicle": "Polnjenje je začasno ustavljeno, ker vozilo ne porablja več energije. Pristojbina mirovanja se lahko začne po obdobju tolerance.",
      "status.hint.pausedVehicleQuiet": "Polnjenje je začasno ustavljeno, obračun mirovanja pa je trenutno ustavljen zaradi mirnega obdobja.",
      "status.hint.pausedCharger": "Polnjenje je začasno ustavila polnilnica ali postaja. Lahko počakate ali ustavite sejo.",
      "status.hint.stopPending": "Polnjenje je ustavljeno. Seja se zaključi šele, ko odklopite vozilo.",
      "status.hint.stopPendingQuiet": "Polnjenje je ustavljeno. Seja se zaključi po odklopu, obračun mirovanja pa je trenutno ustavljen zaradi mirnega obdobja.",
      "status.hint.notReady": "Polnilnica še ni pripravljena. Preverite kabel in poskusite znova.",
      "status.hint.stopFailed": "Polnilnica še ni potrdila zahteve za ustavitev. Počakajte trenutek in poskusite znova.",
      "status.hint.maxEnergyStopping": "Dosežena je nastavljena omejitev seje {energy} kWh. Zahtevana je ustavitev polnjenja.",
      "status.hint.maxEnergyReached": "Seja se je končala po dosegu nastavljene omejitve {energy} kWh.",
      "status.hint.errorDefault": "Polnilnica je trenutno nedosegljiva ali zasedena. Preverite kabel in poskusite znova.",
      "status.idlePausedByWindow": "Obračun zasedenosti je med mirnim obdobjem ustavljen.",
      "status.done.thankYou": "Hvala!",
      "status.done.completed": "Seja je bila uspešno končana",
      "status.error.title": "Polnilnica ni uspela začeti seje",
      "status.error.noFunds": "Sredstva niso bila zaračunana",
      "status.action.cancelSession": "Prekliči sejo",
      "status.action.stopCharging": "Ustavi polnjenje",
      "status.action.stopping": "Ustavljanje...",
      "status.action.backToMap": "Nazaj na zemljevid",
      "status.action.openInvoice": "Odpri račun",
      "status.action.tryAgain": "Poskusi znova",
      "status.action.contactSupport": "Kontaktiraj podporo",
      "status.action.saveR1": "Shrani R1 podatke",
      "status.action.savingR1": "Shranjevanje R1 podatkov...",
      "status.footer.autoRefresh": "Stran se osvežuje samodejno",
      "status.footer.dataRefreshes": "Podatki se samodejno osvežujejo vsakih 5 sekund",
      "status.footer.poweredBy": "Omogoča",
      "status.footer.needHelp": "Potrebujete pomoč?",
      "status.r1.title": "R1 račun (podjetje)",
      "status.r1.subtitle": "Potrebujete R1 račun? Podatke podjetja lahko pošljete zdaj ali pozneje prek varne povezave do seje.",
      "status.r1.companyName": "Naziv podjetja (neobvezno)",
      "status.r1.oib": "Davčna številka (obvezno za R1)",
      "status.r1.oibHelp": "Davčna številka se preveri pred shranjevanjem zahteve.",
      "status.r1.invalidOib": "Vnesite veljavno davčno številko (11 številk).",
      "status.r1.saved": "R1 podatki so uspešno shranjeni.",
      "status.r1.failed": "R1 podatkov trenutno ni mogoče shraniti. Poskusite znova.",
      "status.refundCalc": "Predavtorizacija {authorized} - zaračunano {captured} = vračilo {refund}"
    },
    it: {
      "status.step.start": "① Avvio",
      "status.step.wait": "② Attesa",
      "status.step.charge": "③ Ricarica",
      "status.step.done": "④ Fine",
      "status.step.error": "⑤ Errore",
      "status.badge.waiting": "In attesa del caricatore...",
      "status.badge.awaitingPlug": "Collega il cavo...",
      "status.badge.charging": "Ricarica in corso",
      "status.badge.pausedVehicle": "Pausa dal veicolo / inattivo",
      "status.badge.pausedCharger": "Pausa dal caricatore",
      "status.badge.finishing": "Chiusura sessione",
      "status.badge.stopPending": "Ricarica fermata, scollega il veicolo",
      "status.badge.done": "Ricarica completata",
      "status.badge.error": "Errore",
      "status.badge.stopFailed": "Impossibile fermare la sessione",
      "status.reservation": "Prenotazione",
      "status.label.status": "Stato",
      "status.label.connector": "Connettore",
      "status.label.authorized": "Autorizzato",
      "status.label.currentTotal": "Importo stimato",
      "status.label.sessionDuration": "Durata sessione",
      "status.section.progress": "Avanzamento",
      "status.section.costBreakdown": "Dettaglio costi",
      "status.section.costSummary": "Riepilogo costi",
      "status.section.invoice": "Fattura",
      "status.label.energy": "Energia",
      "status.label.sessionFee": "Costo sessione",
      "status.label.idleFee": "Costo inattività",
      "status.label.chargingStarted": "Ricarica iniziata",
      "status.label.disconnected": "Scollegato",
      "status.label.totalCharged": "Totale addebitato",
      "status.label.total": "TOTALE",
      "status.label.totalTime": "TEMPO TOTALE",
      "status.label.refundToCard": "Rimborso su carta",
      "status.label.invoiceStatus": "Stato",
      "status.label.invoiceNumber": "Numero fattura",
      "status.label.updated": "Aggiornato",
      "status.label.paymentStatus": "Stato pagamento",
      "status.label.lastStatus": "Ultimo stato",
      "status.hint.waitDefault": "Assicurati che il cavo sia collegato al veicolo. Il caricatore dovrebbe avviare la sessione a breve.",
      "status.wait.starting": "Avvio...",
      "status.hint.awaitingPlug": "Collega il cavo al veicolo per continuare.",
      "status.hint.awaitingPlugTimed": "Collega il cavo al veicolo per continuare. Tempo rimanente: {minutes} min.",
      "status.hint.starting": "Il caricatore sta avviando la sessione, attendi.",
      "status.hint.pausedVehicle": "La ricarica è in pausa perché il veicolo non assorbe più energia. I costi di inattività possono iniziare dopo il periodo di tolleranza.",
      "status.hint.pausedVehicleQuiet": "La ricarica è in pausa, ma l'addebito di inattività è temporaneamente sospeso per la fascia di quiete.",
      "status.hint.pausedCharger": "La ricarica è stata messa in pausa dal caricatore o dalla stazione. Puoi attendere o fermare la sessione.",
      "status.hint.stopPending": "La ricarica è stata fermata. La sessione termina solo dopo aver scollegato il veicolo.",
      "status.hint.stopPendingQuiet": "La ricarica è stata fermata. La sessione termina dopo lo scollegamento e l'addebito di inattività è temporaneamente sospeso per la fascia di quiete.",
      "status.hint.notReady": "Il caricatore non è ancora pronto. Controlla il cavo e riprova.",
      "status.hint.stopFailed": "Il caricatore non ha ancora confermato la richiesta di stop. Attendi un momento e riprova.",
      "status.hint.maxEnergyStopping": "È stato raggiunto il limite configurato di {energy} kWh. È stato richiesto lo stop della ricarica.",
      "status.hint.maxEnergyReached": "Questa sessione è terminata dopo il raggiungimento del limite configurato di {energy} kWh.",
      "status.hint.errorDefault": "Il caricatore è attualmente offline o occupato. Controlla il cavo e riprova.",
      "status.idlePausedByWindow": "L'addebito di occupazione è sospeso durante la fascia di quiete.",
      "status.done.thankYou": "Grazie!",
      "status.done.completed": "Sessione completata con successo",
      "status.error.title": "Il caricatore non è riuscito ad avviare la sessione",
      "status.error.noFunds": "Nessun importo è stato addebitato",
      "status.action.cancelSession": "Annulla sessione",
      "status.action.stopCharging": "Ferma ricarica",
      "status.action.stopping": "Arresto...",
      "status.action.backToMap": "Torna alla mappa",
      "status.action.openInvoice": "Apri fattura",
      "status.action.tryAgain": "Riprova",
      "status.action.contactSupport": "Contatta supporto",
      "status.action.saveR1": "Salva dati R1",
      "status.action.savingR1": "Salvataggio dati R1...",
      "status.footer.autoRefresh": "La pagina si aggiorna automaticamente",
      "status.footer.dataRefreshes": "I dati si aggiornano automaticamente ogni 5 secondi",
      "status.footer.poweredBy": "Fornito da",
      "status.footer.needHelp": "Serve aiuto?",
      "status.r1.title": "Fattura R1 (azienda)",
      "status.r1.subtitle": "Ti serve una fattura R1? Puoi inviare i dati aziendali ora o più tardi usando questo link sicuro della sessione.",
      "status.r1.companyName": "Nome azienda (opzionale)",
      "status.r1.oib": "OIB (richiesto per R1)",
      "status.r1.oibHelp": "L'OIB viene verificato prima di salvare la richiesta.",
      "status.r1.invalidOib": "Inserisci un OIB valido (11 cifre).",
      "status.r1.saved": "Dati R1 salvati correttamente.",
      "status.r1.failed": "Impossibile salvare i dati R1 in questo momento. Riprova.",
      "status.refundCalc": "Pre-autorizzazione {authorized} - addebitato {captured} = rimborso {refund}"
    },
    de: {
      "status.step.start": "① Start",
      "status.step.wait": "② Warten",
      "status.step.charge": "③ Laden",
      "status.step.done": "④ Fertig",
      "status.step.error": "⑤ Fehler",
      "status.badge.waiting": "Warten auf Ladepunkt...",
      "status.badge.awaitingPlug": "Kabel anschließen...",
      "status.badge.charging": "Ladevorgang läuft",
      "status.badge.pausedVehicle": "Vom Fahrzeug pausiert / Leerlauf",
      "status.badge.pausedCharger": "Vom Ladepunkt pausiert",
      "status.badge.finishing": "Sitzung wird beendet",
      "status.badge.stopPending": "Laden gestoppt, Fahrzeug abstecken",
      "status.badge.done": "Laden abgeschlossen",
      "status.badge.error": "Fehler",
      "status.badge.stopFailed": "Sitzung konnte nicht gestoppt werden",
      "status.reservation": "Reservierung",
      "status.label.status": "Status",
      "status.label.connector": "Anschluss",
      "status.label.authorized": "Autorisiert",
      "status.label.currentTotal": "Geschätzter Betrag",
      "status.label.sessionDuration": "Sitzungsdauer",
      "status.section.progress": "Fortschritt",
      "status.section.costBreakdown": "Kostenaufschlüsselung",
      "status.section.costSummary": "Kostenzusammenfassung",
      "status.section.invoice": "Rechnung",
      "status.label.energy": "Energie",
      "status.label.sessionFee": "Sitzungsgebühr",
      "status.label.idleFee": "Standgebühr",
      "status.label.chargingStarted": "Laden gestartet",
      "status.label.disconnected": "Abgesteckt",
      "status.label.totalCharged": "Gesamt berechnet",
      "status.label.total": "GESAMT",
      "status.label.totalTime": "GESAMTZEIT",
      "status.label.refundToCard": "Rückerstattung auf Karte",
      "status.label.invoiceStatus": "Status",
      "status.label.invoiceNumber": "Rechnungsnummer",
      "status.label.updated": "Aktualisiert",
      "status.label.paymentStatus": "Zahlungsstatus",
      "status.label.lastStatus": "Letzter Status",
      "status.hint.waitDefault": "Stellen Sie sicher, dass das Kabel mit dem Fahrzeug verbunden ist. Der Ladepunkt sollte die Sitzung in Kürze starten.",
      "status.wait.starting": "Startet...",
      "status.hint.awaitingPlug": "Schließen Sie das Kabel an das Fahrzeug an, um fortzufahren.",
      "status.hint.awaitingPlugTimed": "Schließen Sie das Kabel an das Fahrzeug an, um fortzufahren. Verbleibende Zeit: {minutes} Min.",
      "status.hint.starting": "Der Ladepunkt startet die Sitzung, bitte warten.",
      "status.hint.pausedVehicle": "Der Ladevorgang pausiert, weil das Fahrzeug keine Leistung mehr abnimmt. Standgebühren können nach der Kulanzzeit beginnen.",
      "status.hint.pausedVehicleQuiet": "Der Ladevorgang pausiert, aber die Standgebühr ist wegen des Ruhezeitfensters derzeit pausiert.",
      "status.hint.pausedCharger": "Der Ladevorgang wurde vom Ladepunkt oder der Station pausiert. Sie können warten oder die Sitzung stoppen.",
      "status.hint.stopPending": "Der Ladevorgang wurde gestoppt. Die Sitzung endet erst, wenn Sie das Fahrzeug abstecken.",
      "status.hint.stopPendingQuiet": "Der Ladevorgang wurde gestoppt. Die Sitzung endet nach dem Abstecken, und die Standgebühr ist wegen des Ruhezeitfensters derzeit pausiert.",
      "status.hint.notReady": "Der Ladepunkt ist noch nicht bereit. Prüfen Sie das Kabel und versuchen Sie es erneut.",
      "status.hint.stopFailed": "Der Ladepunkt hat die Stopp-Anforderung noch nicht bestätigt. Warten Sie kurz und versuchen Sie es erneut.",
      "status.hint.maxEnergyStopping": "Das konfigurierte Sitzungslimit von {energy} kWh wurde erreicht. Ein Ladestopp wurde angefordert.",
      "status.hint.maxEnergyReached": "Diese Sitzung wurde nach Erreichen des konfigurierten Limits von {energy} kWh beendet.",
      "status.hint.errorDefault": "Der Ladepunkt ist derzeit offline oder belegt. Prüfen Sie das Kabel und versuchen Sie es erneut.",
      "status.idlePausedByWindow": "Die Belegungsgebühr ist während des Ruhezeitfensters pausiert.",
      "status.done.thankYou": "Danke!",
      "status.done.completed": "Sitzung erfolgreich abgeschlossen",
      "status.error.title": "Der Ladepunkt konnte die Sitzung nicht starten",
      "status.error.noFunds": "Es wurden keine Beträge belastet",
      "status.action.cancelSession": "Sitzung abbrechen",
      "status.action.stopCharging": "Laden stoppen",
      "status.action.stopping": "Stoppt...",
      "status.action.backToMap": "Zurück zur Karte",
      "status.action.openInvoice": "Rechnung öffnen",
      "status.action.tryAgain": "Erneut versuchen",
      "status.action.contactSupport": "Support kontaktieren",
      "status.action.saveR1": "R1-Daten speichern",
      "status.action.savingR1": "R1-Daten werden gespeichert...",
      "status.footer.autoRefresh": "Seite aktualisiert automatisch",
      "status.footer.dataRefreshes": "Daten werden alle 5 Sekunden automatisch aktualisiert",
      "status.footer.poweredBy": "Bereitgestellt von",
      "status.footer.needHelp": "Brauchen Sie Hilfe?",
      "status.r1.title": "R1-Rechnung (Firma)",
      "status.r1.subtitle": "Benötigen Sie eine R1-Rechnung? Sie können Firmendaten jetzt oder später über diesen sicheren Sitzungslink senden.",
      "status.r1.companyName": "Firmenname (optional)",
      "status.r1.oib": "OIB (für R1 erforderlich)",
      "status.r1.oibHelp": "Die OIB wird vor dem Speichern der Anfrage geprüft.",
      "status.r1.invalidOib": "Bitte geben Sie eine gültige OIB ein (11 Ziffern).",
      "status.r1.saved": "R1-Daten erfolgreich gespeichert.",
      "status.r1.failed": "R1-Daten können derzeit nicht gespeichert werden. Bitte versuchen Sie es erneut.",
      "status.refundCalc": "Vorautorisierung {authorized} - belastet {captured} = Rückerstattung {refund}"
    },
    fr: {
      "status.step.start": "① Début",
      "status.step.wait": "② Attente",
      "status.step.charge": "③ Recharge",
      "status.step.done": "④ Terminé",
      "status.step.error": "⑤ Erreur",
      "status.badge.waiting": "En attente de la borne...",
      "status.badge.awaitingPlug": "Branchez le câble...",
      "status.badge.charging": "Recharge en cours",
      "status.badge.pausedVehicle": "Pause véhicule / inactif",
      "status.badge.pausedCharger": "Pause borne",
      "status.badge.finishing": "Fin de session",
      "status.badge.stopPending": "Recharge arrêtée, débranchez le véhicule",
      "status.badge.done": "Recharge terminée",
      "status.badge.error": "Erreur",
      "status.badge.stopFailed": "Impossible d'arrêter la session",
      "status.reservation": "Réservation",
      "status.label.status": "Statut",
      "status.label.connector": "Connecteur",
      "status.label.authorized": "Autorisé",
      "status.label.currentTotal": "Montant estimé",
      "status.label.sessionDuration": "Durée de session",
      "status.section.progress": "Progression",
      "status.section.costBreakdown": "Détail des coûts",
      "status.section.costSummary": "Résumé des coûts",
      "status.section.invoice": "Facture",
      "status.label.energy": "Énergie",
      "status.label.sessionFee": "Frais de session",
      "status.label.idleFee": "Frais d'inactivité",
      "status.label.chargingStarted": "Recharge démarrée",
      "status.label.disconnected": "Débranché",
      "status.label.totalCharged": "Total facturé",
      "status.label.total": "TOTAL",
      "status.label.totalTime": "TEMPS TOTAL",
      "status.label.refundToCard": "Remboursement sur carte",
      "status.label.invoiceStatus": "Statut",
      "status.label.invoiceNumber": "Numéro de facture",
      "status.label.updated": "Mis à jour",
      "status.label.paymentStatus": "Statut du paiement",
      "status.label.lastStatus": "Dernier statut",
      "status.hint.waitDefault": "Vérifiez que le câble est branché au véhicule. La borne devrait démarrer la session bientôt.",
      "status.wait.starting": "Démarrage...",
      "status.hint.awaitingPlug": "Branchez le câble au véhicule pour continuer.",
      "status.hint.awaitingPlugTimed": "Branchez le câble au véhicule pour continuer. Temps restant : {minutes} min.",
      "status.hint.starting": "La borne démarre la session, veuillez patienter.",
      "status.hint.pausedVehicle": "La recharge est en pause car le véhicule ne consomme plus d'énergie. Les frais d'inactivité peuvent commencer après la période de tolérance.",
      "status.hint.pausedVehicleQuiet": "La recharge est en pause, mais la facturation d'inactivité est temporairement suspendue en raison de la plage calme.",
      "status.hint.pausedCharger": "La recharge est mise en pause par la borne ou la station. Vous pouvez attendre ou arrêter la session.",
      "status.hint.stopPending": "La recharge est arrêtée. La session se termine seulement après le débranchement du véhicule.",
      "status.hint.stopPendingQuiet": "La recharge est arrêtée. La session se termine après le débranchement, et la facturation d'inactivité est temporairement suspendue en raison de la plage calme.",
      "status.hint.notReady": "La borne n'est pas encore prête. Vérifiez le câble et réessayez.",
      "status.hint.stopFailed": "La borne n'a pas encore confirmé la demande d'arrêt. Patientez un instant puis réessayez.",
      "status.hint.maxEnergyStopping": "La limite de session configurée de {energy} kWh a été atteinte. Un arrêt de recharge a été demandé.",
      "status.hint.maxEnergyReached": "Cette session s'est terminée après avoir atteint la limite configurée de {energy} kWh.",
      "status.hint.errorDefault": "La borne est actuellement hors ligne ou occupée. Vérifiez le câble et réessayez.",
      "status.idlePausedByWindow": "La facturation d'occupation est suspendue pendant la plage calme.",
      "status.done.thankYou": "Merci !",
      "status.done.completed": "Session terminée avec succès",
      "status.error.title": "La borne n'a pas réussi à démarrer la session",
      "status.error.noFunds": "Aucun montant n'a été débité",
      "status.action.cancelSession": "Annuler la session",
      "status.action.stopCharging": "Arrêter la recharge",
      "status.action.stopping": "Arrêt...",
      "status.action.backToMap": "Retour à la carte",
      "status.action.openInvoice": "Ouvrir la facture",
      "status.action.tryAgain": "Réessayer",
      "status.action.contactSupport": "Contacter le support",
      "status.action.saveR1": "Enregistrer les données R1",
      "status.action.savingR1": "Enregistrement des données R1...",
      "status.footer.autoRefresh": "La page s'actualise automatiquement",
      "status.footer.dataRefreshes": "Les données s'actualisent automatiquement toutes les 5 secondes",
      "status.footer.poweredBy": "Propulsé par",
      "status.footer.needHelp": "Besoin d'aide ?",
      "status.r1.title": "Facture R1 (entreprise)",
      "status.r1.subtitle": "Besoin d'une facture R1 ? Vous pouvez envoyer les informations de l'entreprise maintenant ou plus tard avec ce lien de session sécurisé.",
      "status.r1.companyName": "Nom de l'entreprise (optionnel)",
      "status.r1.oib": "OIB (requis pour R1)",
      "status.r1.oibHelp": "L'OIB est vérifié avant l'enregistrement de la demande.",
      "status.r1.invalidOib": "Veuillez saisir un OIB valide (11 chiffres).",
      "status.r1.saved": "Données R1 enregistrées avec succès.",
      "status.r1.failed": "Impossible d'enregistrer les données R1 pour le moment. Veuillez réessayer.",
      "status.refundCalc": "Préautorisation {authorized} - facturé {captured} = remboursement {refund}"
    }
  };

  Object.keys(statusTranslations).forEach((lang) => {
    if (translations[lang]) {
      Object.assign(translations[lang], statusTranslations[lang]);
    }
  });

  const dynamicTranslations = {
    hr: {
      "result.paymentAuthorized": "Plaćanje autorizirano",
      "result.paymentStatus": "Status plaćanja",
      "result.chargingWillStart": "Sesija punjenja uskoro će započeti.",
      "result.somethingWentWrong": "Nešto je pošlo po zlu.",
      "map.availableCount": "{available}/{total} dostupno",
      "map.unnamedStation": "(Neimenovana postaja)",
      "map.noUsableCoordinates": "Nisu pronađene upotrebljive koordinate za oznake na karti.",
      "map.shrink": "Smanji",
      "map.clearSearch": "Očisti pretragu",
      "map.centerOnLocation": "Centriraj na svoju lokaciju",
      "map.expandTitle": "Proširi kartu",
      "status.connectorFallback": "Priključak {id}",
      "message.chargePointMissing": "Nedostaje punjač.",
      "message.chargePointNotFound": "Punjač nije pronađen.",
      "message.connectorUnavailableForChargePoint": "Odabrani priključak nije dostupan za ovaj punjač. Odaberite drugi priključak.",
      "message.r1OibRequired": "Za R1 račun unesite OIB (11 znamenki).",
      "message.r1OibInvalid": "Uneseni OIB nije valjan. Provjerite 11 znamenki i pokušajte ponovno.",
      "message.connectorOfflineTryAvailable": "Ovaj priključak je offline. Pokušajte ponovno kada bude dostupan.",
      "message.unableStart": "Nije moguće pokrenuti transakciju.",
      "message.activeReservation": "Ovaj priključak je privremeno rezerviran tijekom checkouta. Ako je to vaša sesija, nastavite u istom pregledniku ili odaberite drugi priključak.",
      "message.connectorInUse": "Ovaj priključak je trenutno u uporabi. Najprije zaustavite aktivnu sesiju ili odaberite drugi priključak.",
      "message.connectorCurrentlyOffline": "Ovaj priključak je trenutno offline. Pokušajte kasnije ili odaberite drugi priključak.",
      "message.connectorNotReady": "Ovaj priključak trenutno nije spreman. Odaberite drugi priključak.",
      "message.connectorCurrentlyUnavailable": "Ovaj priključak trenutno nije dostupan. Odaberite drugi priključak ili pokušajte za trenutak.",
      "message.recoveryUnfinished": "Već imate nedovršen checkout za ovaj priključak. Nastavite plaćanje ili ga otkažite prije pokretanja nove sesije.",
      "message.recoveryUnavailable": "Ovaj checkout se više ne može nastaviti. Otkažite prethodni pokušaj kako biste otključali priključak.",
      "message.paymentCancelled": "Plaćanje je otkazano.",
      "message.reservationIdRequired": "Potreban je ID rezervacije.",
      "message.unexpectedStopResponse": "Neočekivan odgovor za zaustavljanje.",
      "message.upstreamStatusUnavailable": "Status servera trenutno nije dostupan.",
      "message.upstreamStopUnavailable": "Zaustavljanje preko servera trenutno nije dostupno."
    },
    en: {
      "result.paymentAuthorized": "Payment authorized",
      "result.paymentStatus": "Payment status",
      "result.chargingWillStart": "Charging session will start shortly.",
      "result.somethingWentWrong": "Something went wrong.",
      "map.availableCount": "{available}/{total} available",
      "map.unnamedStation": "(Unnamed station)",
      "map.noUsableCoordinates": "No usable coordinates were found for map markers.",
      "map.shrink": "Shrink",
      "map.clearSearch": "Clear search",
      "map.centerOnLocation": "Center on your location",
      "map.expandTitle": "Expand map",
      "status.connectorFallback": "Connector {id}",
      "message.chargePointMissing": "Charge point is missing.",
      "message.chargePointNotFound": "Charge point not found.",
      "message.connectorUnavailableForChargePoint": "Selected connector is not available for this charge point. Please choose another connector.",
      "message.r1OibRequired": "For an R1 (company) invoice, please enter your OIB (11 digits).",
      "message.r1OibInvalid": "The OIB you entered is not valid. Please check the 11 digits and try again.",
      "message.connectorOfflineTryAvailable": "This connector is offline. Please try again when it is available.",
      "message.unableStart": "Unable to start the transaction.",
      "message.activeReservation": "This connector is temporarily reserved during checkout. If it is your session, continue in the same browser or choose another connector.",
      "message.connectorInUse": "This connector is currently in use. Please stop the active session first or choose another connector.",
      "message.connectorCurrentlyOffline": "This connector is currently offline. Please try again later or choose another connector.",
      "message.connectorNotReady": "This connector is not ready right now. Please choose another connector.",
      "message.connectorCurrentlyUnavailable": "This connector is currently unavailable. Please choose another connector or try again in a moment.",
      "message.recoveryUnfinished": "You already have an unfinished checkout for this connector. Continue payment or cancel it before starting a new session.",
      "message.recoveryUnavailable": "This checkout can no longer be resumed. Cancel the previous attempt to unlock this connector.",
      "message.paymentCancelled": "Payment cancelled.",
      "message.reservationIdRequired": "Reservation id is required.",
      "message.unexpectedStopResponse": "Unexpected upstream stop response.",
      "message.upstreamStatusUnavailable": "Upstream status unavailable.",
      "message.upstreamStopUnavailable": "Upstream stop unavailable."
    },
    sl: {
      "result.paymentAuthorized": "Plačilo avtorizirano",
      "result.paymentStatus": "Stanje plačila",
      "result.chargingWillStart": "Seja polnjenja se bo kmalu začela.",
      "result.somethingWentWrong": "Nekaj je šlo narobe.",
      "map.availableCount": "{available}/{total} na voljo",
      "map.unnamedStation": "(Neimenovana postaja)",
      "map.noUsableCoordinates": "Za oznake na zemljevidu ni bilo najdenih uporabnih koordinat.",
      "map.shrink": "Zmanjšaj",
      "map.clearSearch": "Počisti iskanje",
      "map.centerOnLocation": "Centriraj na svojo lokacijo",
      "map.expandTitle": "Razširi zemljevid",
      "status.connectorFallback": "Priključek {id}",
      "message.chargePointMissing": "Manjka polnilna točka.",
      "message.chargePointNotFound": "Polnilna točka ni najdena.",
      "message.connectorUnavailableForChargePoint": "Izbrani priključek ni na voljo za to polnilno točko. Izberite drug priključek.",
      "message.r1OibRequired": "Za R1 račun vnesite davčno številko (11 številk).",
      "message.r1OibInvalid": "Vnesena davčna številka ni veljavna. Preverite 11 številk in poskusite znova.",
      "message.connectorOfflineTryAvailable": "Ta priključek je offline. Poskusite znova, ko bo na voljo.",
      "message.unableStart": "Transakcije ni mogoče začeti.",
      "message.activeReservation": "Ta priključek je med checkoutom začasno rezerviran. Če je to vaša seja, nadaljujte v istem brskalniku ali izberite drug priključek.",
      "message.connectorInUse": "Ta priključek je trenutno v uporabi. Najprej ustavite aktivno sejo ali izberite drug priključek.",
      "message.connectorCurrentlyOffline": "Ta priključek je trenutno offline. Poskusite pozneje ali izberite drug priključek.",
      "message.connectorNotReady": "Ta priključek trenutno ni pripravljen. Izberite drug priključek.",
      "message.connectorCurrentlyUnavailable": "Ta priključek trenutno ni na voljo. Izberite drug priključek ali poskusite čez trenutek.",
      "message.recoveryUnfinished": "Za ta priključek že imate nedokončan checkout. Nadaljujte plačilo ali ga prekličite pred začetkom nove seje.",
      "message.recoveryUnavailable": "Tega checkouta ni več mogoče nadaljevati. Prekličite prejšnji poskus, da odklenete priključek.",
      "message.paymentCancelled": "Plačilo je preklicano.",
      "message.reservationIdRequired": "ID rezervacije je obvezen.",
      "message.unexpectedStopResponse": "Nepričakovan odgovor za ustavitev.",
      "message.upstreamStatusUnavailable": "Stanje strežnika trenutno ni na voljo.",
      "message.upstreamStopUnavailable": "Ustavitev prek strežnika trenutno ni na voljo."
    },
    it: {
      "result.paymentAuthorized": "Pagamento autorizzato",
      "result.paymentStatus": "Stato pagamento",
      "result.chargingWillStart": "La sessione di ricarica inizierà a breve.",
      "result.somethingWentWrong": "Qualcosa è andato storto.",
      "map.availableCount": "{available}/{total} disponibili",
      "map.unnamedStation": "(Stazione senza nome)",
      "map.noUsableCoordinates": "Non sono state trovate coordinate utilizzabili per i marker della mappa.",
      "map.shrink": "Riduci",
      "map.clearSearch": "Cancella ricerca",
      "map.centerOnLocation": "Centra sulla tua posizione",
      "map.expandTitle": "Espandi mappa",
      "status.connectorFallback": "Connettore {id}",
      "message.chargePointMissing": "Punto di ricarica mancante.",
      "message.chargePointNotFound": "Punto di ricarica non trovato.",
      "message.connectorUnavailableForChargePoint": "Il connettore selezionato non è disponibile per questo punto di ricarica. Scegli un altro connettore.",
      "message.r1OibRequired": "Per una fattura R1 (azienda), inserisci il tuo OIB (11 cifre).",
      "message.r1OibInvalid": "L'OIB inserito non è valido. Controlla le 11 cifre e riprova.",
      "message.connectorOfflineTryAvailable": "Questo connettore è offline. Riprova quando sarà disponibile.",
      "message.unableStart": "Impossibile avviare la transazione.",
      "message.activeReservation": "Questo connettore è temporaneamente riservato durante il checkout. Se è la tua sessione, continua nello stesso browser o scegli un altro connettore.",
      "message.connectorInUse": "Questo connettore è attualmente in uso. Ferma prima la sessione attiva o scegli un altro connettore.",
      "message.connectorCurrentlyOffline": "Questo connettore è attualmente offline. Riprova più tardi o scegli un altro connettore.",
      "message.connectorNotReady": "Questo connettore non è pronto al momento. Scegli un altro connettore.",
      "message.connectorCurrentlyUnavailable": "Questo connettore è attualmente non disponibile. Scegli un altro connettore o riprova tra poco.",
      "message.recoveryUnfinished": "Hai già un checkout non completato per questo connettore. Continua il pagamento o annullalo prima di avviare una nuova sessione.",
      "message.recoveryUnavailable": "Questo checkout non può più essere ripreso. Annulla il tentativo precedente per sbloccare questo connettore.",
      "message.paymentCancelled": "Pagamento annullato.",
      "message.reservationIdRequired": "ID prenotazione richiesto.",
      "message.unexpectedStopResponse": "Risposta di stop dal server non prevista.",
      "message.upstreamStatusUnavailable": "Stato del server non disponibile.",
      "message.upstreamStopUnavailable": "Stop del server non disponibile."
    },
    de: {
      "result.paymentAuthorized": "Zahlung autorisiert",
      "result.paymentStatus": "Zahlungsstatus",
      "result.chargingWillStart": "Die Ladesitzung startet in Kürze.",
      "result.somethingWentWrong": "Etwas ist schiefgelaufen.",
      "map.availableCount": "{available}/{total} verfügbar",
      "map.unnamedStation": "(Unbenannte Station)",
      "map.noUsableCoordinates": "Es wurden keine nutzbaren Koordinaten für Kartenmarker gefunden.",
      "map.shrink": "Verkleinern",
      "map.clearSearch": "Suche löschen",
      "map.centerOnLocation": "Auf Ihren Standort zentrieren",
      "map.expandTitle": "Karte vergrößern",
      "status.connectorFallback": "Anschluss {id}",
      "message.chargePointMissing": "Ladepunkt fehlt.",
      "message.chargePointNotFound": "Ladepunkt nicht gefunden.",
      "message.connectorUnavailableForChargePoint": "Der ausgewählte Anschluss ist für diesen Ladepunkt nicht verfügbar. Bitte wählen Sie einen anderen Anschluss.",
      "message.r1OibRequired": "Für eine R1-Firmenrechnung geben Sie bitte Ihre OIB ein (11 Ziffern).",
      "message.r1OibInvalid": "Die eingegebene OIB ist ungültig. Prüfen Sie die 11 Ziffern und versuchen Sie es erneut.",
      "message.connectorOfflineTryAvailable": "Dieser Anschluss ist offline. Bitte versuchen Sie es erneut, wenn er verfügbar ist.",
      "message.unableStart": "Die Transaktion konnte nicht gestartet werden.",
      "message.activeReservation": "Dieser Anschluss ist während des Checkouts vorübergehend reserviert. Wenn dies Ihre Sitzung ist, fahren Sie im selben Browser fort oder wählen Sie einen anderen Anschluss.",
      "message.connectorInUse": "Dieser Anschluss ist derzeit in Verwendung. Stoppen Sie zuerst die aktive Sitzung oder wählen Sie einen anderen Anschluss.",
      "message.connectorCurrentlyOffline": "Dieser Anschluss ist derzeit offline. Bitte versuchen Sie es später erneut oder wählen Sie einen anderen Anschluss.",
      "message.connectorNotReady": "Dieser Anschluss ist derzeit nicht bereit. Bitte wählen Sie einen anderen Anschluss.",
      "message.connectorCurrentlyUnavailable": "Dieser Anschluss ist derzeit nicht verfügbar. Bitte wählen Sie einen anderen Anschluss oder versuchen Sie es gleich erneut.",
      "message.recoveryUnfinished": "Für diesen Anschluss gibt es bereits einen unvollständigen Checkout. Setzen Sie die Zahlung fort oder brechen Sie sie ab, bevor Sie eine neue Sitzung starten.",
      "message.recoveryUnavailable": "Dieser Checkout kann nicht mehr fortgesetzt werden. Brechen Sie den vorherigen Versuch ab, um den Anschluss freizugeben.",
      "message.paymentCancelled": "Zahlung abgebrochen.",
      "message.reservationIdRequired": "Reservierungs-ID ist erforderlich.",
      "message.unexpectedStopResponse": "Unerwartete Antwort beim Stoppen.",
      "message.upstreamStatusUnavailable": "Serverstatus ist derzeit nicht verfügbar.",
      "message.upstreamStopUnavailable": "Server-Stopp ist derzeit nicht verfügbar."
    },
    fr: {
      "result.paymentAuthorized": "Paiement autorisé",
      "result.paymentStatus": "Statut du paiement",
      "result.chargingWillStart": "La session de recharge démarrera bientôt.",
      "result.somethingWentWrong": "Une erreur est survenue.",
      "map.availableCount": "{available}/{total} disponibles",
      "map.unnamedStation": "(Station sans nom)",
      "map.noUsableCoordinates": "Aucune coordonnée utilisable n'a été trouvée pour les marqueurs de carte.",
      "map.shrink": "Réduire",
      "map.clearSearch": "Effacer la recherche",
      "map.centerOnLocation": "Centrer sur votre position",
      "map.expandTitle": "Agrandir la carte",
      "status.connectorFallback": "Connecteur {id}",
      "message.chargePointMissing": "Point de charge manquant.",
      "message.chargePointNotFound": "Point de charge introuvable.",
      "message.connectorUnavailableForChargePoint": "Le connecteur sélectionné n'est pas disponible pour ce point de charge. Veuillez choisir un autre connecteur.",
      "message.r1OibRequired": "Pour une facture R1 (entreprise), veuillez saisir votre OIB (11 chiffres).",
      "message.r1OibInvalid": "L'OIB saisi n'est pas valide. Vérifiez les 11 chiffres et réessayez.",
      "message.connectorOfflineTryAvailable": "Ce connecteur est hors ligne. Veuillez réessayer lorsqu'il sera disponible.",
      "message.unableStart": "Impossible de démarrer la transaction.",
      "message.activeReservation": "Ce connecteur est temporairement réservé pendant le checkout. Si c'est votre session, continuez dans le même navigateur ou choisissez un autre connecteur.",
      "message.connectorInUse": "Ce connecteur est actuellement utilisé. Arrêtez d'abord la session active ou choisissez un autre connecteur.",
      "message.connectorCurrentlyOffline": "Ce connecteur est actuellement hors ligne. Réessayez plus tard ou choisissez un autre connecteur.",
      "message.connectorNotReady": "Ce connecteur n'est pas prêt pour le moment. Veuillez choisir un autre connecteur.",
      "message.connectorCurrentlyUnavailable": "Ce connecteur est actuellement indisponible. Choisissez un autre connecteur ou réessayez dans un instant.",
      "message.recoveryUnfinished": "Vous avez déjà un checkout inachevé pour ce connecteur. Continuez le paiement ou annulez-le avant de démarrer une nouvelle session.",
      "message.recoveryUnavailable": "Ce checkout ne peut plus être repris. Annulez la tentative précédente pour déverrouiller ce connecteur.",
      "message.paymentCancelled": "Paiement annulé.",
      "message.reservationIdRequired": "ID de réservation requis.",
      "message.unexpectedStopResponse": "Réponse d'arrêt serveur inattendue.",
      "message.upstreamStatusUnavailable": "Statut serveur indisponible.",
      "message.upstreamStopUnavailable": "Arrêt serveur indisponible."
    }
  };

  Object.keys(dynamicTranslations).forEach((lang) => {
    if (translations[lang]) {
      Object.assign(translations[lang], dynamicTranslations[lang]);
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

  function formatTextFor(lang, key, values) {
    let text = textFor(lang, key);
    Object.entries(values || {}).forEach(([name, value]) => {
      text = text.replaceAll(`{${name}}`, value);
    });
    return text;
  }

  function messageTranslationKey(value) {
    const normalized = String(value || "").trim();
    const keys = {
      "Charge point is missing.": "message.chargePointMissing",
      "Charge point not found.": "message.chargePointNotFound",
      "Selected connector is not available for this charge point. Please choose another connector.": "message.connectorUnavailableForChargePoint",
      "For an R1 (company) invoice, please enter your OIB (11 digits).": "message.r1OibRequired",
      "The OIB you entered is not valid. Please check the 11 digits and try again.": "message.r1OibInvalid",
      "This connector is offline. Please try again when it is available.": "message.connectorOfflineTryAvailable",
      "Unable to start the transaction.": "message.unableStart",
      "Payment authorized. Charging session will start shortly.": "result.chargingWillStart",
      "Charging session will start shortly.": "result.chargingWillStart",
      "Something went wrong.": "result.somethingWentWrong",
      "This connector is temporarily reserved during checkout. If it is your session, continue in the same browser or choose another connector.": "message.activeReservation",
      "This connector is currently in use. Please stop the active session first or choose another connector.": "message.connectorInUse",
      "This connector is currently offline. Please try again later or choose another connector.": "message.connectorCurrentlyOffline",
      "This connector is not ready right now. Please choose another connector.": "message.connectorNotReady",
      "This connector is currently unavailable. Please choose another connector or try again in a moment.": "message.connectorCurrentlyUnavailable",
      "You already have an unfinished checkout for this connector. Continue payment or cancel it before starting a new session.": "message.recoveryUnfinished",
      "This checkout can no longer be resumed. Cancel the previous attempt to unlock this connector.": "message.recoveryUnavailable",
      "Payment cancelled.": "message.paymentCancelled",
      "Reservation id is required.": "message.reservationIdRequired",
      "reservationId required": "message.reservationIdRequired",
      "Unexpected upstream stop response.": "message.unexpectedStopResponse",
      "Upstream status unavailable.": "message.upstreamStatusUnavailable",
      "Upstream stop unavailable.": "message.upstreamStopUnavailable",
      "R1 invoice details were saved successfully.": "status.r1.saved",
      "Unable to save R1 invoice request.": "status.r1.failed",
      "Valid OIB (11 digits) is required.": "status.r1.invalidOib",
      "Valid OIB (11 digits) is required for R1 invoice.": "status.r1.invalidOib"
    };

    if (keys[normalized]) {
      return keys[normalized];
    }

    if (normalized.endsWith(" Cancel the previous attempt to unlock this connector.")) {
      return "message.recoveryUnavailable";
    }

    return null;
  }

  function messageTextFor(lang, value) {
    const key = messageTranslationKey(value);
    return key ? textFor(lang, key) : value;
  }

  function statusTranslationKey(value) {
    const normalized = (value || "").trim().toLowerCase().replace(/[\s_-]+/g, "");
    const keys = {
      available: "start.status.available",
      freecharging: "start.status.freeCharging",
      offline: "start.status.offline",
      occupied: "start.status.occupied",
      busy: "start.status.busy",
      charging: "start.status.charging",
      preparing: "start.status.preparing",
      reserved: "start.status.reserved",
      unavailable: "start.status.unavailable",
      faulted: "start.status.faulted",
      finishing: "start.status.finishing",
      suspendedev: "start.status.suspendedEv",
      suspendedevse: "start.status.suspendedEvse",
      waiting: "start.status.waiting"
    };

    return keys[normalized] || null;
  }

  function statusTextFor(lang, value) {
    const key = statusTranslationKey(value);
    return key ? textFor(lang, key) : value;
  }

  function applyOptionStateText(element, lang) {
    const defaultKey = element.getAttribute("data-i18n-default-text");
    const selectedKey = element.getAttribute("data-i18n-selected-text");
    if (defaultKey) {
      element.dataset.defaultText = textFor(lang, defaultKey);
    }
    if (selectedKey) {
      element.dataset.selectedText = textFor(lang, selectedKey);
    }

    const state = element.getAttribute("data-i18n-option-state");
    if (state === "selected") {
      element.textContent = element.dataset.selectedText || element.textContent;
    } else if (state === "default") {
      element.textContent = element.dataset.defaultText || element.textContent;
    }
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

    document.querySelectorAll("[data-i18n-title]").forEach((element) => {
      const key = element.getAttribute("data-i18n-title");
      const value = textFor(lang, key);
      if (value) {
        element.setAttribute("title", value);
      }
    });

    document.querySelectorAll("[data-i18n-aria-label]").forEach((element) => {
      const key = element.getAttribute("data-i18n-aria-label");
      const value = textFor(lang, key);
      if (value) {
        element.setAttribute("aria-label", value);
      }
    });

    document.querySelectorAll("[data-i18n-status]").forEach((element) => {
      const value = element.getAttribute("data-i18n-status") || element.textContent;
      element.textContent = statusTextFor(lang, value);
    });

    document.querySelectorAll("[data-i18n-message]").forEach((element) => {
      const value = element.getAttribute("data-i18n-message") || element.textContent;
      element.textContent = messageTextFor(lang, value);
    });

    document.querySelectorAll("[data-i18n-template]").forEach((element) => {
      const key = element.getAttribute("data-i18n-template");
      const values = {};
      Array.from(element.attributes).forEach((attribute) => {
        if (attribute.name.startsWith("data-i18n-param-")) {
          values[attribute.name.replace("data-i18n-param-", "")] = attribute.value;
        }
      });
      element.textContent = formatTextFor(lang, key, values);
    });

    document.querySelectorAll("[data-i18n-default-text], [data-i18n-selected-text]").forEach((element) => {
      applyOptionStateText(element, lang);
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
      const cpOrEvseMatch = path.match(/^\/(cp|evse)\/([^/]+)(?:\/(\d+))?\/?$/i);
      const isLegacyStart = /^\/Public\/Start$/i.test(path) && !!url.searchParams.get("cp");
      if (!cpOrEvseMatch && !isLegacyStart) {
        return null;
      }

      if (cpOrEvseMatch) {
        const id = cpOrEvseMatch[2];
        const connectorId = cpOrEvseMatch[3];
        url.pathname = connectorId ? `/cp/${id}/${connectorId}` : `/cp/${id}`;
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
    formatTextFor,
    messageTextFor,
    statusTextFor,
    applyTranslations
  };
})();
