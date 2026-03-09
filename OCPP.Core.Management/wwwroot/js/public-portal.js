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
      "map.search": "Pretraži po nazivu, lokaciji ili ID-u punjača...",
      "map.stationsOnMap": "Punjači na karti",
      "map.noStations": "Još nema konfiguriranih punjača.",
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
      "map.search": "Search by name, location, or charge point id...",
      "map.stationsOnMap": "Stations on map",
      "map.noStations": "No stations configured yet.",
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
      "map.search": "Išči po imenu, lokaciji ali ID-ju polnilnice...",
      "map.stationsOnMap": "Postaje na zemljevidu",
      "map.noStations": "Še ni nastavljenih postaj.",
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
      "map.search": "Cerca per nome, località o ID del punto di ricarica...",
      "map.stationsOnMap": "Stazioni sulla mappa",
      "map.noStations": "Nessuna stazione configurata.",
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
      "map.search": "Nach Name, Ort oder Ladepunkt-ID suchen...",
      "map.stationsOnMap": "Stationen auf der Karte",
      "map.noStations": "Noch keine Stationen konfiguriert.",
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
      "map.search": "Rechercher par nom, lieu ou identifiant de borne...",
      "map.stationsOnMap": "Bornes sur la carte",
      "map.noStations": "Aucune borne configurée pour le moment.",
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
})();
