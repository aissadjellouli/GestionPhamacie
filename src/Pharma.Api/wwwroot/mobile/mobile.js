// Scanner mobile Pharma Officine : page servie par l'API du PC serveur, sur le Wi-Fi de la pharmacie.
// Lecture caméra : détecteur du navigateur, sinon ZXing (lib/zxing-0.23.0, Apache-2.0) servi par le serveur.
// Les données reçues sont toujours affichées comme du texte (jamais comme du HTML).
"use strict";

const etat = { jeton: null, permissions: [], onglet: "controle", inventaire: null, emplacements: null };
const $ = id => document.getElementById(id);

// ---- Outils ------------------------------------------------------------------------

/** Crée un élément : el("li", { class: "x" }, "texte", autreElement). */
function el(tag, attrs, ...enfants) {
  const e = document.createElement(tag);
  for (const [k, v] of Object.entries(attrs || {})) {
    if (v === null || v === undefined || v === false) continue;
    if (k.startsWith("on")) e.addEventListener(k.slice(2), v);
    else if (k === "class") e.className = v;
    else e.setAttribute(k, v === true ? "" : v);
  }
  for (const c of enfants.flat()) if (c !== null && c !== undefined && c !== false) e.append(c instanceof Node ? c : String(c));
  return e;
}

const dateFr = iso => iso ? iso.split("-").reverse().join("/") : "—";
const dinars = n => Number(n).toLocaleString("fr-DZ", { minimumFractionDigits: 2, maximumFractionDigits: 2 }) + " DA";
const peut = p => etat.permissions.includes(p);

function statut(message, type) {
  const s = $("statut");
  s.textContent = message || "";
  s.className = "statut" + (type ? " " + type : "");
}

/** Signal sonore et vibration à chaque lecture (le préparateur ne regarde pas toujours l'écran). */
function signal(ok) {
  try { navigator.vibrate?.(ok ? 60 : [80, 60, 80]); } catch { }
  try {
    const ctx = signal.ctx ||= new AudioContext();
    const o = ctx.createOscillator(), g = ctx.createGain();
    o.frequency.value = ok ? 1200 : 300; g.gain.value = 0.08;
    o.connect(g).connect(ctx.destination); o.start(); o.stop(ctx.currentTime + (ok ? 0.08 : 0.25));
  } catch { }
}

async function api(methode, url, corps) {
  const r = await fetch(url, {
    method: methode,
    headers: { "Content-Type": "application/json", ...(etat.jeton ? { Authorization: "Bearer " + etat.jeton } : {}) },
    body: corps === undefined ? undefined : JSON.stringify(corps)
  });
  if (r.status === 401 && etat.jeton) { deconnecter("Session expirée : reconnectez-vous."); throw new Error("Session expirée."); }
  if (r.status === 204) return null;
  const donnees = await r.json().catch(() => null);
  if (!r.ok) throw new Error(donnees?.message || `Erreur ${r.status}`);
  return donnees;
}

// ---- Connexion ---------------------------------------------------------------------

async function connecter(e) {
  e.preventDefault();
  try {
    const s = await api("POST", "/api/auth/connexion", { identifiant: $("identifiant").value.trim(), motDePasse: $("mot-de-passe").value });
    etat.jeton = s.jeton;
    sessionStorage.setItem("jeton", s.jeton);
    sessionStorage.setItem("nom", s.nomComplet);
    $("mot-de-passe").value = "";
    await demarrer();
  } catch (err) { statutConnexion(err.message); }
}

function statutConnexion(m) {
  let p = $("erreur-connexion");
  if (!p) { p = el("p", { id: "erreur-connexion", class: "statut erreur", role: "alert" }); $("form-connexion").append(p); }
  p.textContent = m;
}

function deconnecter(message) {
  arreterCamera();
  etat.jeton = null; etat.permissions = []; etat.inventaire = null;
  sessionStorage.clear();
  $("ecran-app").hidden = true; $("ecran-connexion").hidden = false; $("deconnexion").hidden = true;
  $("qui").textContent = "Scanner mobile";
  if (message) statutConnexion(message);
}

async function demarrer() {
  etat.permissions = await api("GET", "/api/auth/permissions");
  $("qui").textContent = "Connecté : " + (sessionStorage.getItem("nom") || "");
  $("ecran-connexion").hidden = true; $("ecran-app").hidden = false; $("deconnexion").hidden = false;
  document.querySelector('[data-onglet="reception"]').hidden = !peut("achats.receptionner");
  document.querySelector('[data-onglet="inventaire"]').hidden = !peut("stock.ajuster");
  document.querySelector('[data-onglet="pilotage"]').hidden = !peut("role.titulaire");
  choisirOnglet(peut("role.titulaire") ? "pilotage" : "controle");
}

// ---- Onglets et saisie du code -----------------------------------------------------

function choisirOnglet(nom) {
  etat.onglet = nom;
  for (const b of document.querySelectorAll("[data-onglet]")) b.setAttribute("aria-selected", b.dataset.onglet === nom);
  for (const s of ["pilotage", "controle", "inventaire", "reception"]) $("onglet-" + s).hidden = s !== nom;
  // Le pilotage ne scanne rien
  $("barre-scan").hidden = nom === "pilotage";
  if (nom === "pilotage") { arreterCamera(); statut(""); afficherPilotage(); return; }
  $("libelle-scan").textContent = { controle: "Scanner une boîte à contrôler", inventaire: "Scanner une boîte comptée", reception: "Scanner une boîte reçue" }[nom];
  statut("");
  if (nom === "inventaire") afficherInventaire();
  if (nom === "reception") afficherReception();
  focaliserScan();
}

function focaliserScan() { if (!$("video-cadre").hidden) return; $("scan").focus({ preventScroll: true }); }

// Les douchettes et lasers tapent le code puis Entrée, comme un clavier
async function toucheScan(e) {
  if (e.key !== "Enter") return;
  e.preventDefault();
  const code = $("scan").value.trim();
  $("scan").value = "";
  if (code) await traiterCode(code);
}

async function traiterCode(code) {
  try {
    if (etat.onglet === "controle") await controler(code);
    else if (etat.onglet === "inventaire") await scannerInventaire(code);
    else await scannerReception(code);
  } catch (err) { signal(false); statut(err.message, "erreur"); }
}

// ---- Caméra ------------------------------------------------------------------------

let flux = null, lire = null, dernierScan = 0;
const FORMATS = ["data_matrix", "ean_13", "ean_8", "code_128", "qr_code"];

/**
 * Lecteur de codes : celui du navigateur s'il lit les Data Matrix (Chrome sur Android),
 * sinon la bibliothèque ZXing servie par le serveur de la pharmacie (iPhone, Firefox…).
 * Renvoie une fonction (vidéo) => texte du code ou null.
 */
async function preparerLecteur() {
  if ("BarcodeDetector" in window) {
    const pris = await BarcodeDetector.getSupportedFormats().catch(() => []);
    if (pris.includes("data_matrix")) {
      const detecteur = new BarcodeDetector({ formats: FORMATS.filter(f => pris.includes(f)) });
      return async video => (await detecteur.detect(video))[0]?.rawValue ?? null;
    }
  }
  await chargerZxing();
  const indices = new Map();
  const F = ZXing.BarcodeFormat;
  indices.set(ZXing.DecodeHintType.POSSIBLE_FORMATS, [F.DATA_MATRIX, F.EAN_13, F.EAN_8, F.CODE_128, F.QR_CODE]);
  indices.set(ZXing.DecodeHintType.TRY_HARDER, true);
  const lecteur = new ZXing.MultiFormatReader();
  lecteur.setHints(indices);
  const toile = document.createElement("canvas");
  return video => {
    // Seule la zone du viseur est analysée : plus rapide, et évite de lire une boîte voisine
    const [w, h] = [video.videoWidth, video.videoHeight];
    if (!w || !h) return null;
    const [x, y, lw, lh] = [Math.round(w * 0.14), Math.round(h * 0.22), Math.round(w * 0.72), Math.round(h * 0.56)];
    toile.width = lw; toile.height = lh;
    toile.getContext("2d", { willReadFrequently: true }).drawImage(video, x, y, lw, lh, 0, 0, lw, lh);
    try {
      return lecteur.decodeWithState(new ZXing.BinaryBitmap(new ZXing.HybridBinarizer(new ZXing.HTMLCanvasElementLuminanceSource(toile)))).getText();
    } catch { return null; }   // aucun code dans l'image
    finally { lecteur.reset(); }
  };
}

function chargerZxing() {
  if (window.ZXing) return Promise.resolve();
  return new Promise((ok, ko) => {
    const s = document.createElement("script");
    s.src = "lib/zxing-0.23.0/zxing.min.js";
    s.onload = ok; s.onerror = () => ko(new Error("bibliothèque de lecture introuvable sur le serveur"));
    document.head.append(s);
  });
}

async function demarrerCamera() {
  const info = $("info-camera");
  if (!window.isSecureContext) { info.hidden = false; info.textContent = "Caméra : ouvrez la page en https:// (voir le guide d’installation). En attendant, utilisez la douchette ou le clavier."; return; }
  try {
    lire ||= await preparerLecteur();
    flux = await navigator.mediaDevices.getUserMedia({ video: { facingMode: "environment" }, audio: false });
    const v = $("video");
    v.srcObject = flux; await v.play();
    $("video-cadre").hidden = false; info.hidden = true;
    boucleCamera();
  } catch (err) { info.hidden = false; info.textContent = "Caméra indisponible : " + err.message + ". Utilisez la douchette, le laser ou le clavier."; arreterCamera(); }
}

async function boucleCamera() {
  if (!flux) return;
  try {
    const code = await lire($("video"));
    // Une lecture toutes les 1,5 s : deux boîtes identiques à la suite restent comptées deux fois
    if (code && Date.now() - dernierScan > 1500) { dernierScan = Date.now(); await traiterCode(code); }
  } catch { }
  setTimeout(boucleCamera, 200);
}

function arreterCamera() {
  flux?.getTracks().forEach(t => t.stop());
  flux = null;
  $("video-cadre").hidden = true;
}

// ---- Contrôle en rayon -------------------------------------------------------------

const NIVEAUX = {
  perime: ["Périmé", "urgent"], rouge: ["Péremption proche", "urgent"], orange: ["À surveiller", "attention"],
  risque_ecoulement: ["Risque d’invendu", "attention"], ok: ["OK", "ok"]
};

async function controler(code) {
  const f = await api("POST", "/api/mobile/code", { code });
  etat.dernierCode = code;
  signal(f.alertes.length === 0);
  statut(f.produit.nom + (f.type === "produit" ? " : code du fabricant, lot non identifié." : ""), f.alertes.length ? "erreur" : "ok");
  afficherFiche(f);
}

function afficherFiche(f) {
  const p = f.produit;
  const zone = $("fiche");
  zone.replaceChildren(el("article", { class: "carte" },
    el("h2", {}, p.nom),
    el("p", { class: "detail" }, [p.dci, p.dosage, p.forme].filter(Boolean).join(" · ")),
    f.alertes.length ? el("ul", { class: "alertes", role: "alert" }, f.alertes.map(a => el("li", {}, a))) : null,
    el("dl", { class: "grille" },
      el("dt", {}, "Stock"), el("dd", {}, `${p.stock} boîte(s)`),
      el("dt", {}, "Prix public"), el("dd", {}, dinars(p.prixPublic)),
      el("dt", {}, "Rayon"), el("dd", {}, p.emplacement ? el("span", { class: "code" }, p.emplacement) : "non rangé"),
      el("dt", {}, "Réserve"), el("dd", {}, p.reserve ? el("span", { class: "code" }, p.reserve) : "—")),
    el("h2", {}, "Lots en stock"),
    p.lots.length ? el("ul", { class: "lots" }, p.lots.map(l => ligneLot(l, l.id === f.lotId))) : el("p", { class: "detail" }, "Aucun lot en stock."),
    peut("stock.ajuster") ? blocRangement(p) : null));
}

function ligneLot(l, scanne) {
  const [libelle, classe] = NIVEAUX[l.niveau] || [l.niveau, "info"];
  const li = el("li", { class: scanne ? "scanne" : null },
    el("div", { class: "lot-tete" },
      el("strong", {}, "Lot " + l.numeroLot + (scanne ? " (cette boîte)" : "")),
      el("span", { class: "pastille " + (l.bloque ? "urgent" : classe) }, l.bloque ? "Bloqué" : libelle)),
    el("div", { class: "detail" }, `Péremption ${dateFr(l.datePeremption)} · ${l.quantite} boîte(s) · ${l.fournisseur}`));
  if (l.dateAVerifier) {
    li.append(el("span", { class: "pastille attention" }, "Date à vérifier"));
    if (peut("stock.ajuster")) {
      const saisie = el("input", { type: "date", "aria-label": `Date lue sur la boîte, lot ${l.numeroLot}` });
      li.append(el("div", { class: "ligne-action" }, saisie,
        el("button", { type: "button", class: "btn btn-principal", onclick: async () => {
          if (!saisie.value) { statut("Saisissez la date lue sur la boîte.", "erreur"); return; }
          try {
            await api("PUT", `/api/mobile/lots/${l.id}/peremption`, { date: saisie.value });
            signal(true); statut(`Lot ${l.numeroLot} : date du ${dateFr(saisie.value)} confirmée.`, "ok");
            afficherFiche(await api("POST", "/api/mobile/code", { code: etat.dernierCode }));
          } catch (err) { signal(false); statut(err.message, "erreur"); }
        } }, "Confirmer")));
    }
  }
  return li;
}

function blocRangement(p) {
  const vente = el("select", { "aria-label": "Emplacement de vente" }, el("option", { value: "" }, "Non rangé en rayon"));
  const reserve = el("select", { "aria-label": "Emplacement de réserve" }, el("option", { value: "" }, "Pas de réserve"));
  const bloc = el("details", { class: "rangement" }, el("summary", {}, "Ranger ce produit"),
    el("div", { class: "champs" }, el("label", {}, "Rayon (vente)"), vente, el("label", {}, "Réserve"), reserve,
      el("button", { type: "button", class: "btn btn-principal", onclick: async () => {
        try {
          await api("PUT", `/api/produits/${p.produitId}/emplacement`,
            { emplacementId: vente.value ? Number(vente.value) : null, reserveId: reserve.value ? Number(reserve.value) : null });
          signal(true); statut(`${p.nom} rangé.`, "ok");
          afficherFiche(await api("POST", "/api/mobile/code", { code: etat.dernierCode }));
        } catch (err) { signal(false); statut(err.message, "erreur"); }
      } }, "Enregistrer le rangement")));
  bloc.addEventListener("toggle", async () => {
    if (!bloc.open || vente.options.length > 1) return;
    etat.emplacements ||= (await api("GET", "/api/emplacements")).filter(e => e.actif);
    for (const e of etat.emplacements) {
      const opt = el("option", { value: e.id, selected: e.id === (e.zone === "vente" ? p.emplacementId : p.reserveId) }, e.code + (e.libelle ? " · " + e.libelle : ""));
      (e.zone === "vente" ? vente : reserve).append(opt);
    }
  });
  return el("div", { style: "margin-top:12px" }, bloc);
}

// ---- Inventaire tournant -----------------------------------------------------------

async function afficherInventaire() {
  const zone = $("inventaire");
  try {
    if (etat.inventaire) return afficherComptage(await api("GET", `/api/inventaires/${etat.inventaire}`));
    const t = await api("GET", "/api/inventaires");
    const choix = el("select", { id: "rayon", "aria-label": "Rayon à compter" },
      t.rayons.map(r => el("option", { value: r.rayon, selected: r.rayon === t.rayonPropose },
        `Rayon ${r.rayon} · ${r.nombreLots} lot(s)${r.enCoursId ? " · en cours" : ""}${r.rayon === t.rayonPropose ? " (proposé)" : ""}`)));
    zone.replaceChildren(el("div", { class: "carte" },
      el("h2", {}, "Rayon à compter"),
      t.rayons.length ? [choix, el("div", { class: "actions" }, el("button", { type: "button", class: "btn btn-principal", onclick: async () => {
        try {
          const r = await api("POST", "/api/inventaires", { rayon: choix.value });
          etat.inventaire = r.id; await afficherInventaire(); focaliserScan();
        } catch (err) { statut(err.message, "erreur"); }
      } }, "Commencer ou reprendre"))] : el("p", { class: "detail" }, "Aucun emplacement : créez les rayons sur le poste."),
      el("p", { class: "detail" }, "Comptage à l’aveugle : le stock du logiciel n’est montré qu’à la fin, sur le poste.")));
  } catch (err) { statut(err.message, "erreur"); }
}

function afficherComptage(d) {
  if (d.statut !== "en_cours") { etat.inventaire = null; return afficherInventaire(); }
  const comptes = d.lignes.filter(l => l.compte !== null).length;
  $("inventaire").replaceChildren(
    el("div", { class: "carte" },
      el("h2", {}, `Rayon ${d.rayon}`),
      el("p", { class: "progression" }, `${comptes} / ${d.lignes.length} lot(s) compté(s)`),
      el("p", { class: "detail" }, "Scannez chaque boîte, ou tapez le nombre ; 0 si le lot est introuvable.")),
    el("ul", { class: "liste" }, d.lignes.map(l => ligneComptage(d.id, l))),
    el("div", { class: "actions" },
      el("button", { type: "button", class: "btn btn-principal", disabled: comptes < d.lignes.length, onclick: async () => {
        try {
          const b = await api("POST", `/api/inventaires/${d.id}/terminer`);
          signal(true);
          statut(`Inventaire terminé : ${b.ajustes} écart(s) corrigé(s), ${b.aValider} en attente du titulaire.`, "ok");
          etat.inventaire = null; await afficherInventaire();
        } catch (err) { signal(false); statut(err.message, "erreur"); }
      } }, "Terminer"),
      el("button", { type: "button", class: "btn", onclick: () => { etat.inventaire = null; afficherInventaire(); } }, "Plus tard")));
}

function ligneComptage(inventaireId, l) {
  const nombre = el("input", { type: "number", min: "0", inputmode: "numeric", value: l.compte ?? "", "aria-label": `Boîtes comptées, ${l.produit} lot ${l.numeroLot}` });
  const date = l.dateAVerifier ? el("input", { type: "date", value: l.dateVerifiee || "", "aria-label": `Date lue sur la boîte, ${l.produit}` }) : null;
  const envoyer = async () => {
    if (nombre.value === "") return;
    try {
      await api("PUT", `/api/inventaires/${inventaireId}/lignes/${l.id}`, { compte: Number(nombre.value), dateVerifiee: date?.value || null });
      afficherComptage(await api("GET", `/api/inventaires/${inventaireId}`));
    } catch (err) { statut(err.message, "erreur"); }
  };
  nombre.addEventListener("change", envoyer);
  date?.addEventListener("change", envoyer);
  return el("li", { class: l.compte !== null ? "compte" : null },
    el("div", {},
      el("div", { class: "nom" }, l.produit),
      el("div", { class: "detail", style: "margin:0" }, `Lot ${l.numeroLot}${l.emplacement ? " · " + l.emplacement : ""}`),
      date ? el("div", {}, el("span", { class: "pastille attention" }, "Date à vérifier"), date) : null),
    nombre);
}

async function scannerInventaire(code) {
  if (!etat.inventaire) { statut("Commencez d’abord le comptage d’un rayon.", "erreur"); signal(false); return; }
  const r = await api("POST", `/api/inventaires/${etat.inventaire}/scan`, { code });
  signal(true);
  statut(`${r.produit} · lot ${r.numeroLot} : ${r.compte} boîte(s).`, "ok");
  afficherComptage(await api("GET", `/api/inventaires/${etat.inventaire}`));
}

// ---- Réception ---------------------------------------------------------------------

async function scannerReception(code) {
  const s = await api("POST", "/api/mobile/reception/scans", { code });
  signal(true);
  statut(`${s.produit}${s.numeroLot ? " · lot " + s.numeroLot : ""}${s.peremption ? " · " + dateFr(s.peremption) : ""}`, "ok");
  await afficherReception();
}

async function afficherReception() {
  try {
    const scans = await api("GET", "/api/mobile/reception/scans");
    const parProduit = new Map();
    for (const s of scans) parProduit.set(s.produit, (parProduit.get(s.produit) || 0) + 1);
    $("reception").replaceChildren(
      el("div", { class: "carte" },
        el("h2", {}, `${scans.length} boîte(s) en attente d’import`),
        scans.length ? el("p", { class: "detail" }, [...parProduit].map(([p, n]) => `${p} : ${n}`).join(" · ")) : el("p", { class: "detail" }, "Aucune boîte scannée.")),
      el("ul", { class: "liste" }, scans.map(s => el("li", {},
        el("div", {},
          el("div", { class: "nom" }, s.produit),
          el("div", { class: "detail", style: "margin:0" },
            [s.numeroLot && "lot " + s.numeroLot, s.peremption && dateFr(s.peremption), s.numeroSerie && "série " + s.numeroSerie, s.scannePar].filter(Boolean).join(" · ") || "sans Data Matrix : lot et date à saisir sur le poste")),
        el("button", { type: "button", class: "btn btn-icone", "aria-label": `Retirer ${s.produit}`, onclick: async () => {
          try { await api("DELETE", `/api/mobile/reception/scans/${s.id}`); await afficherReception(); } catch (err) { statut(err.message, "erreur"); }
        } }, "×")))));
  } catch (err) { statut(err.message, "erreur"); }
}

// ---- Démarrage ---------------------------------------------------------------------

$("form-connexion").addEventListener("submit", connecter);
$("deconnexion").addEventListener("click", () => deconnecter());
$("scan").addEventListener("keydown", toucheScan);
$("camera").addEventListener("click", demarrerCamera);
$("arreter-camera").addEventListener("click", () => { arreterCamera(); focaliserScan(); });
$("clavier").addEventListener("click", () => {
  const s = $("scan");
  s.inputMode = s.inputMode === "none" ? "text" : "none";
  s.blur(); s.focus();
});
// ---- Pilotage (titulaire) : journée, caisses, alertes, écarts d'inventaire à décider ----

const heure = iso => new Date(iso).toLocaleTimeString("fr-FR", { hour: "2-digit", minute: "2-digit" });

async function afficherPilotage() {
  const zone = $("pilotage");
  zone.replaceChildren(el("p", { class: "detail" }, "Chargement…"));
  try {
    const [t, inv] = await Promise.all([api("GET", "/api/pilotage"), api("GET", "/api/inventaires")]);
    const evolution = t.chiffreAffairesHier > 0 ? Math.round((t.chiffreAffaires / t.chiffreAffairesHier - 1) * 100) : null;
    zone.replaceChildren(
      el("div", { class: "carte" },
        el("p", { class: "detail" }, `Mis à jour à ${heure(t.genereLe)}`, " ",
          el("button", { type: "button", class: "lien", onclick: afficherPilotage }, "Actualiser")),
        t.inventaireComplet ? el("p", { class: "pastille attention" }, t.inventaireComplet) : null,
        el("h2", {}, "Aujourd’hui"),
        el("p", { class: "chiffre" }, dinars(t.chiffreAffaires)),
        el("p", { class: "detail" }, `${t.nombreVentes} vente(s)`,
          evolution === null ? "" : ` · ${evolution >= 0 ? "+" : ""}${evolution} % par rapport à hier (${dinars(t.chiffreAffairesHier)})`),
        el("p", { class: "detail" }, `Part organismes à recevoir : ${dinars(t.partOrganismes)} · crédits accordés : ${dinars(t.creditsAccordes)}`)),
      el("div", { class: "carte" },
        el("h2", {}, `Caisses ouvertes (${t.caisses.length})`),
        t.caisses.length === 0 ? el("p", { class: "detail" }, "Aucune caisse ouverte.") : null,
        ...t.caisses.map(c => el("p", {}, el("strong", {}, c.poste), ` · ${c.ouvertePar} depuis ${heure(c.ouverteLe)}`, el("br"),
          el("span", { class: "detail" }, `${c.ventes} vente(s) · espèces attendues ${dinars(c.especesAttendues)} (fond ${dinars(c.fondCaisse)})`))),
        t.clotures.length ? el("h3", {}, "Clôtures du jour") : null,
        ...t.clotures.map(c => el("p", {}, `${c.poste} à ${heure(c.fermeeLe)} · attendu ${dinars(c.attendu)} `,
          c.ecart === null ? " · comptage non fait" : el("span", { class: "pastille " + (c.ecart === 0 ? "ok" : Math.abs(c.ecart) > 0 ? "attention" : "") },
            ` écart ${c.ecart > 0 ? "+" : ""}${dinars(c.ecart)}`)))),
      blocEcarts(inv.ecartsAValider),
      el("div", { class: "carte" },
        el("h2", {}, `Alertes (${t.alertesUrgentes} urgente(s))`),
        t.alertes.length === 0 ? el("p", { class: "detail" }, "Aucune alerte nouvelle.") : null,
        ...t.alertes.map(a => el("p", {}, el("span", { class: "pastille " + (a.niveau === "urgent" ? "urgent" : a.niveau === "attention" ? "attention" : "") }, a.niveau), " ", a.message))),
      el("div", { class: "carte" },
        el("h2", {}, "Argent"),
        ...t.comptes.map(c => el("p", {}, `${c.nom} : `, el("strong", {}, dinars(c.solde)))),
        t.comptes.length === 0 ? el("p", { class: "detail" }, "Aucun compte de trésorerie.") : null,
        el("p", {}, "Factures clients à encaisser : ", el("strong", {}, dinars(t.facturesAEncaisser)))));
  } catch (err) {
    zone.replaceChildren(el("p", { class: "statut erreur", role: "alert" }, err.message));
  }
}

function blocEcarts(ecarts) {
  return el("div", { class: "carte" },
    el("h2", {}, `Écarts d’inventaire à décider (${ecarts.length})`),
    ecarts.length === 0 ? el("p", { class: "detail" }, "Aucun écart en attente.") : null,
    ...ecarts.map(e => el("div", { class: "ligne-ecart" },
      el("p", {}, el("strong", {}, e.produit), ` · lot ${e.numeroLot} · rayon ${e.rayon}`, el("br"),
        el("span", { class: "detail" }, `Théorique ${e.theorique}, compté ${e.compte} (${e.ecart > 0 ? "+" : ""}${e.ecart}) · ${dinars(e.valeurEcart)} · ${e.comptePar}`)),
      el("div", { class: "boutons" },
        el("button", { type: "button", class: "btn btn-principal", onclick: () => deciderEcart(e, true) }, "Valider"),
        el("button", { type: "button", class: "btn", onclick: () => deciderEcart(e, false) }, "Refuser (recompter)")))));
}

async function deciderEcart(e, valider) {
  try {
    await api("POST", `/api/inventaires/ecarts/${e.ligneId}`, { valider });
    statut(valider ? `Écart validé : ${e.produit}, stock corrigé.` : `Écart refusé : ${e.produit}, à recompter.`, "ok");
    signal(true);
    await afficherPilotage();
  } catch (err) { statut(err.message, "erreur"); signal(false); }
}

for (const b of document.querySelectorAll("[data-onglet]")) b.addEventListener("click", () => choisirOnglet(b.dataset.onglet));

// Garde le champ de scan actif pour la douchette, sauf pendant une saisie
document.addEventListener("click", e => { if (!e.target.closest("input, select, button, summary, details")) focaliserScan(); });

etat.jeton = sessionStorage.getItem("jeton");
if (etat.jeton) demarrer().catch(() => deconnecter());
