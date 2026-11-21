// Fonctions du poste appelées depuis Blazor : raccourci F12 en caisse et suivi de l'inactivité.
window.pharma = {
  _caisse: null,
  _derniereActivite: Date.now(),

  // La page Caisse s'abonne au raccourci F12 (Encaisser) et se désabonne en quittant la page
  ecouterF12: function (reference) { this._caisse = reference; },
  arreterF12: function () { this._caisse = null; },
  // Caisse : amène une ligne du panier dans son cadre (produit ajouté ou scanné), sans faire défiler l'écran
  voirLigne: function (index) {
    // Après le calcul de la hauteur du cadre (pharma.tableaux, planifié après chaque changement de l'écran)
    // et encore après un bandeau apparu dans la foulée (lot périmé remplacé…), qui réduit le cadre une seconde fois
    function voir() {
      var ligne = document.querySelector('.panier [data-ligne="' + index + '"]');
      if (ligne) ligne.scrollIntoView({ block: 'nearest' });
    }
    setTimeout(voir, 150);
    setTimeout(voir, 500);
  },
  // Amène une zone ouverte (formulaire sous un tableau) dans son cadre défilant, sans faire défiler l'écran
  voirZone: function (selecteur) {
    function voir() { var z = document.querySelector(selecteur); if (z) z.scrollIntoView({ block: 'nearest' }); }
    setTimeout(voir, 150);
    setTimeout(voir, 500);
  },

  // Liste de propositions (.suggestions, flèches haut et bas) : amène l'option active dans le cadre de la liste, en ne faisant
  // défiler que la liste, jamais l'écran
  voirOption: function (id) {
    var option = document.getElementById(id);
    var liste = option && option.closest('.suggestions');
    if (!liste) return;
    var cadre = liste.getBoundingClientRect(), r = option.getBoundingClientRect();
    if (r.bottom > cadre.bottom) liste.scrollTop += r.bottom - cadre.bottom;
    else if (r.top < cadre.top) liste.scrollTop -= cadre.top - r.top;
  },

  // Menu de gauche de l'Administration : amène le lien affiché (bandeau ambre) dans le cadre du menu, au plus près
  // (comme scrollIntoView block « nearest »), en ne déplaçant que le menu, jamais l'écran
  voirLienActif: function (nav) {
    if (!nav) return;
    var lien = nav.querySelector("a.active");
    if (!lien) return;
    var cadre = nav.getBoundingClientRect(), r = lien.getBoundingClientRect(), marge = 8;
    if (r.bottom > cadre.bottom) nav.scrollTop += r.bottom - cadre.bottom + marge;
    else if (r.top < cadre.top) nav.scrollTop -= cadre.top - r.top + marge;
  },

  // Champ dont la saisie a été corrigée par l'écran (ex. remise : « 05 » → « 5 », champ vidé → « 0 ») : valeur affichée réécrite,
  // curseur en fin de champ
  fixerValeur: function (champ, valeur) {
    if (!champ || champ.value === valeur) return;
    champ.value = valeur;
    try { champ.setSelectionRange(valeur.length, valeur.length); } catch (e) { }
  },

  // Secondes écoulées depuis la dernière action (clavier, souris, scanner)
  inactiviteSecondes: function () { return Math.floor((Date.now() - this._derniereActivite) / 1000); }
};

document.addEventListener('keydown', function (e) {
  window.pharma._derniereActivite = Date.now();
  if (e.key === 'F12' && window.pharma._caisse) {
    e.preventDefault();
    // Fenêtre ouverte par-dessus la caisse (confirmation, validation, ordonnance agrandie…) : F12 n'encaisse pas derrière elle
    if (!document.querySelector('.voile, [aria-modal="true"]')) window.pharma._caisse.invokeMethodAsync('SurF12');
  }
  // Caisse : F7 place le curseur dans « Scanner ou rechercher un produit », d'où qu'on soit sur l'écran de vente
  if (e.key === 'F7' && window.pharma._caisse) {
    var scan = document.getElementById('scan');
    if (scan) { e.preventDefault(); scan.focus(); scan.select(); }
  }
  // Caisse (2026-11-15) : F6 patient, F5 médecin prescripteur (jamais le rechargement de la page : il viderait le panier),
  // F8 ouvrir le tiroir (la confirmation du menu s'affiche). Un lien « Ajouter un patient » déplie d'abord le bloc client.
  if ((e.key === 'F5' || e.key === 'F6' || e.key === 'F8') && window.pharma._caisse) {
    e.preventDefault();
    if (document.querySelector('.voile, [aria-modal="true"]')) return;
    var nom = e.key === 'F6' ? 'patient' : e.key === 'F5' ? 'medecin' : 'tiroir';
    var cible = document.querySelector('[data-raccourci="' + nom + '"]');
    // Bloc client résumé en une ligne (2026-11-18) : F6 et F5 le rouvrent (« Modifier ») puis placent le curseur sur le champ
    if (!cible && nom !== 'tiroir') cible = document.querySelector('[data-raccourci="client"]');
    if (!cible) return;
    if (cible.tagName === 'INPUT') { cible.focus(); cible.select(); return; }
    cible.click();
    if (nom !== 'tiroir') setTimeout(function () { var champ = document.querySelector('input[data-raccourci="' + nom + '"]'); if (champ) champ.focus(); }, 150);
  }
  // Navigation (docs/navigation.md) : Ctrl + K ouvre la recherche partout ; Ctrl + Maj + lettre ouvre le domaine qui l'affiche
  // dans le menu du haut (data-touche ; un domaine sans droit n'y est pas, le raccourci ne fait alors rien)
  if (e.ctrlKey && !e.altKey && !e.shiftKey && e.key.toLowerCase() === 'k') {
    var bouton = document.querySelector('.entete .bouton-recherche');
    if (bouton) { e.preventDefault(); bouton.click(); }
  } else if (e.ctrlKey && e.shiftKey && !e.altKey && /^[a-z]$/i.test(e.key)) {
    var domaine = document.querySelector('.entete [data-touche="' + e.key.toUpperCase() + '"]');
    if (domaine) { e.preventDefault(); domaine.click(); }
  }
}, true);

['mousedown', 'mousemove', 'wheel', 'touchstart'].forEach(function (evenement) {
  document.addEventListener(evenement, function () { window.pharma._derniereActivite = Date.now(); }, { passive: true });
});

// Clavier cohérent (docs/navigation.md) : à l'ouverture d'un écran ou d'une rubrique, le curseur va dans sa première recherche
// (on tape directement) ; jamais s'il est déjà dans un champ. Échap efface la recherche où l'on est, sauf si une liste de
// propositions est ouverte (Échap la ferme) ou dans une fenêtre (Échap la ferme).
(function () {
  var dernier = null, prevu = false;
  function placer() {
    prevu = false;
    var ecran = location.pathname + location.search;
    if (ecran === dernier) return;
    var actif = document.activeElement;
    var libre = !actif || actif === document.body || actif.tagName === 'A' || actif.tagName === 'BUTTON'
      || (actif.closest && actif.closest('.entete, .nav-laterale, .admin-nav'));
    if (!libre) { dernier = ecran; return; }
    var champs = document.querySelectorAll('.page input[type="search"]');
    for (var i = 0; i < champs.length; i++) {
      var c = champs[i];
      if (c.offsetParent !== null && !c.disabled && !c.readOnly && !c.closest('.voile')) { dernier = ecran; c.focus(); return; }
    }
  }
  new MutationObserver(function () { if (!prevu) { prevu = true; requestAnimationFrame(placer); } })
    .observe(document.documentElement, { childList: true, subtree: true });

  // Échap dans un champ dont la liste de propositions est ouverte (2026-11-18) : la liste se ferme (Blazor) et le texte tapé reste
  // (sans cela, un champ « search » se vide de lui-même) ; un second Échap, liste fermée, efface la recherche comme ci-dessous
  document.addEventListener('keydown', function (e) {
    if (e.key !== 'Escape') return;
    var c = document.activeElement;
    var cadre = c && c.closest && c.closest('.recherche');
    if (cadre && cadre.querySelector('.suggestions')) e.preventDefault();
  }, true);

  document.addEventListener('keydown', function (e) {
    if (e.key !== 'Escape' || e.defaultPrevented) return;
    var c = document.activeElement;
    if (!c || !c.matches || !c.matches('.page input[type="search"]') || !c.value || c.closest('.voile')) return;
    if (document.querySelector('.suggestions')) return;
    e.preventDefault();
    c.value = '';
    c.dispatchEvent(new Event('input', { bubbles: true }));
    c.dispatchEvent(new Event('change', { bubbles: true }));
  });
})();

// Listes de propositions (.suggestions, 2026-11-18) : jamais au-delà du bas de la fenêtre (écran sans défilement, la barre d'état
// comprise). Si la place manque sous le champ (moins de 160 px) et qu'il y en a plus au-dessus, la liste s'ouvre vers le haut ; sinon elle est
// raccourcie et défile seule. Recalculé à chaque changement de l'écran et à chaque redimensionnement.
(function () {
  var prevu = false;
  function placer() {
    prevu = false;
    var zoom = parseFloat(document.documentElement.style.zoom) || 1;
    var entete = document.querySelector('.entete');
    var haut = entete ? entete.getBoundingClientRect().bottom : 0;
    document.querySelectorAll('.suggestions').forEach(function (l) {
      var cadre = l.parentElement;
      if (!cadre) return;
      var c = cadre.getBoundingClientRect();
      // Cadre qui défile autour du champ (relevé dans « Encaisser une dette »…) : la liste ne doit pas non plus en sortir, elle y serait coupée
      var bas = window.innerHeight - 40 * zoom, plafond = haut;
      for (var e = cadre.parentElement; e && e !== document.body; e = e.parentElement) {
        var oy = getComputedStyle(e).overflowY;
        if (oy === 'auto' || oy === 'scroll' || oy === 'hidden') {
          var r = e.getBoundingClientRect();
          bas = Math.min(bas, r.bottom); plafond = Math.max(plafond, r.top);
          break;
        }
      }
      // Place libre sous et au-dessus du champ, marge et bord de la liste déduits (12 px)
      var dessous = Math.floor((bas - c.bottom) / zoom) - 4 - 12;
      var dessus = Math.floor((c.top - plafond) / zoom) - 4 - 12 - 8;
      var naturel = Math.min(360, l.scrollHeight);
      // Vers le bas tant qu'il y a la place pour quelques propositions (160 px) ; vers le haut seulement s'il y en a plus là
      if (naturel <= dessous || dessous >= 160 || dessous >= dessus) {
        l.style.top = ''; l.style.bottom = '';
        l.style.maxHeight = naturel > dessous ? Math.max(dessous, 80) + 'px' : '';
      } else {
        l.style.top = 'auto'; l.style.bottom = 'calc(100% + 4px)';
        l.style.maxHeight = Math.min(360, dessus) + 'px';
      }
    });
  }
  function planifier() { if (!prevu) { prevu = true; requestAnimationFrame(placer); } }
  new MutationObserver(function (changes) {
    // Nos propres changements de style ne relancent rien (attributs non observés) : seulement les ajouts et retraits d'éléments
    if (document.querySelector('.suggestions')) planifier();
  }).observe(document.documentElement, { childList: true, subtree: true });
  window.addEventListener('resize', planifier);
})();

// Caméra au comptoir : photo de l'ordonnance (l'autorisation est accordée par la fenêtre du poste)
window.pharma.camera = {
  _flux: null,
  // Numéro de la dernière demande : un arrêt demandé pendant l'attente de la caméra rend la demande en cours caduque
  _demande: 0,
  demarrer: async function (idVideo) {
    const demande = ++this._demande;
    const flux = await navigator.mediaDevices.getUserMedia({
      video: { width: { ideal: 1920 }, height: { ideal: 1080 }, facingMode: 'environment' }, audio: false });
    const video = document.getElementById(idVideo);
    // Caméra fermée (ou écran quitté) pendant l'attente : les pistes sont coupées aussitôt, la caméra ne reste pas allumée
    if (demande !== this._demande || !video) {
      flux.getTracks().forEach(function (t) { t.stop(); });
      return;
    }
    this._flux = flux;
    video.srcObject = flux;
    await video.play();
  },
  // Image JPEG en base64 (sans l'en-tête data:)
  capturer: function (idVideo) {
    const video = document.getElementById(idVideo);
    const toile = document.createElement('canvas');
    toile.width = video.videoWidth; toile.height = video.videoHeight;
    toile.getContext('2d').drawImage(video, 0, 0);
    return toile.toDataURL('image/jpeg', 0.92).split(',')[1];
  },
  arreter: function () {
    this._demande++;
    if (this._flux) this._flux.getTracks().forEach(function (t) { t.stop(); });
    this._flux = null;
  }
};

// Affichage choisi par l'employé : clair, sombre, ou auto (suit le réglage de Windows, même s'il change en cours de journée)
window.pharma.affichage = {
  _poste: null,
  _requete: window.matchMedia('(prefers-color-scheme: dark)'),
  appliquer: function (mode) {
    if (mode === 'clair' || mode === 'sombre') document.documentElement.dataset.theme = mode;
    else delete document.documentElement.dataset.theme;
    return this.estSombre();
  },
  estSombre: function () {
    const force = document.documentElement.dataset.theme;
    return force ? force === 'sombre' : this._requete.matches;
  },
  // Le poste est prévenu quand Windows passe en clair ou en sombre (pour la barre de titre)
  suivre: function (reference) { this._poste = reference; }
};
window.pharma.affichage._requete.addEventListener('change', function () {
  const a = window.pharma.affichage;
  if (a._poste) a._poste.invokeMethodAsync('SurChangementAffichage', a.estSombre());
});

// Retour à l'écran précédent : Alt + ← et bouton « précédent » de la souris passent par l'historique du poste (qui
// rouvre l'écran avec son état), jamais par le retour du navigateur intégré.
window.pharma.retour = {
  _poste: null,
  suivre: function (reference) { this._poste = reference; },
  _demander: function (e) {
    e.preventDefault();
    if (window.pharma.retour._poste) window.pharma.retour._poste.invokeMethodAsync('Retour');
  }
};
document.addEventListener('keydown', function (e) {
  if (e.altKey && e.key === 'ArrowLeft') window.pharma.retour._demander(e);
});
// Bouton 3 = précédent : empêché à l'appui et au relâché (sinon le navigateur intégré reviendrait seul)
document.addEventListener('mousedown', function (e) { if (e.button === 3) e.preventDefault(); });
document.addEventListener('mouseup', function (e) { if (e.button === 3) window.pharma.retour._demander(e); });

// ---- Accord automatique des « (s) » dans tout le texte affiché (écrans, messages du serveur et de la base) ----
/**
 * Accord automatique des « (s) » selon le nombre qui précède : 0 ou 1 (et tout nombre inférieur à 2) → singulier, 2 et plus →
 * pluriel. « 3 lot(s) » → « 3 lots », « 1 boîte(s) ajoutée(s) » → « 1 boîte ajoutée ». Le nombre vaut jusqu'à la prochaine
 * ponctuation ou le prochain nombre ; un « (s) » sans nombre devant reste tel quel. Même règle que Pharma.Domain.Commun.Accord.
 */
function accorder(texte, nombre) {
  if (!texte) return { texte: texte ?? "", nombre };
  let sortie = "", i = 0;
  const chiffre = (c) => c >= "0" && c <= "9";
  const lettre = (c) => !!c && c.toLowerCase() !== c.toUpperCase();
  while (i < texte.length) {
    const c = texte[i];
    if (chiffre(c)) {
      const debut = i; let valeur = ""; let decimale = false;
      while (i < texte.length) {
        const d = texte[i], suivant = texte[i + 1];
        if (chiffre(d)) { valeur += d; i++; }
        else if ((d === " " || d === " " || d === " ") && chiffre(suivant) && valeur) i++;
        else if ((d === "," || d === ".") && chiffre(suivant) && !decimale) { valeur += "."; decimale = true; i++; }
        else break;
      }
      sortie += texte.slice(debut, i);
      nombre = parseFloat(valeur);
      continue;
    }
    if (texte.startsWith("(s)", i) && i > 0 && lettre(texte[i - 1])) {
      sortie += nombre === null || nombre === undefined || isNaN(nombre) ? "(s)" : nombre >= 2 ? "s" : "";
      i += 3;
      continue;
    }
    if (",;:.·()\n!?".includes(c)) nombre = null;
    sortie += c;
    i++;
  }
  return { texte: sortie, nombre };
}
const accord = (texte) => accorder(texte, null).texte;

// Le texte d'origine (avec « (s) ») de chaque nœud est gardé : quand Blazor change un nombre voisin (« 2 » devient « 1 »),
// l'accord est recalculé depuis l'original. Le nombre peut être dans un nœud voisin (« @n lot(s) » donne deux nœuds de texte).
window.pharma.accord = (function () {
  const origine = new WeakMap(), ecrit = new WeakMap();
  const ignores = new Set(["SCRIPT", "STYLE", "TEXTAREA", "INPUT", "SELECT", "OPTION"]);
  const source = (n) => { if (ecrit.get(n) !== n.data) origine.set(n, n.data); return origine.get(n); };
  const aTraiter = (n) => (origine.get(n) ?? n.data).includes("(s)");

  function parent(el) {
    if (!el || ignores.has(el.nodeName) || el.isContentEditable) return;
    let nombre = null;
    for (const n of el.childNodes) {
      if (n.nodeType === 3) {
        const texte = source(n);
        const r = accorder(texte, nombre);
        nombre = r.nombre;
        if (texte.includes("(s)") && n.data !== r.texte) n.data = r.texte;
        if (texte.includes("(s)")) ecrit.set(n, n.data);
      } else if (n.nodeType === 1) {
        // Élément en ligne (ex. <strong>3</strong> lot(s)) : son texte compte pour le nombre en cours
        nombre = accorder(n.textContent, nombre).nombre;
      }
    }
  }

  function tout(racine) {
    const marcheur = document.createTreeWalker(racine, NodeFilter.SHOW_TEXT);
    const parents = new Set();
    for (let n = marcheur.nextNode(); n; n = marcheur.nextNode()) if (aTraiter(n)) parents.add(n.parentNode);
    parents.forEach(parent);
  }

  const observateur = new MutationObserver(function (mutations) {
    const parents = new Set();
    for (const m of mutations) {
      if (m.type === "characterData") parents.add(m.target.parentNode);
      else {
        parents.add(m.target);
        m.addedNodes.forEach(function (n) { if (n.nodeType === 1) tout(n); else if (n.nodeType === 3) parents.add(n.parentNode); });
      }
    }
    parents.forEach(function (p) {
      if (p && [...p.childNodes].some(function (n) { return n.nodeType === 3 && aTraiter(n); })) parent(p);
    });
  });

  function demarrer() {
    tout(document.body);
    observateur.observe(document.body, { subtree: true, childList: true, characterData: true });
  }
  if (document.body) demarrer(); else document.addEventListener("DOMContentLoaded", demarrer);
  return { accorder: accorder, texte: accord };
})();

// Zoom de tout l'écran (menu à son nom › Zoom), de 80 à 150 %, retenu par le poste (navigateur intégré) et réappliqué au démarrage
window.pharma.zoom = {
  _cle: "pharma.zoom",
  lire: function () {
    let v = 100;
    try { v = parseInt(localStorage.getItem(this._cle) || "100", 10) || 100; } catch (e) { }
    return Math.min(150, Math.max(80, v));
  },
  appliquer: function (v) {
    document.documentElement.style.zoom = v === 100 ? "" : (v / 100).toString();
    try { localStorage.setItem(this._cle, String(v)); } catch (e) { }
    window.pharma.largeur.maj();
  }
};

// Largeur utile de l'écran (fenêtre ÷ zoom) : les règles « @media (max-width) » ne voient pas le zoom (propriété CSS zoom) ;
// ces classes, si : l'en-tête se compacte par étapes (app.css, « Menu des domaines toujours sur une ligne »)
window.pharma.largeur = {
  maj: function () {
    const zoom = parseFloat(document.documentElement.style.zoom) || 1;
    const utile = window.innerWidth / zoom;
    const c = document.documentElement.classList;
    c.toggle("largeur-1850", utile <= 1850);
    c.toggle("largeur-1650", utile <= 1650);
    c.toggle("largeur-1450", utile <= 1450);
    c.toggle("largeur-1250", utile <= 1250);
  }
};
window.addEventListener("resize", function () { window.pharma.largeur.maj(); });
// Fenêtre redimensionnée : le lien affiché du menu de l'Administration reste visible
window.addEventListener("resize", function () { window.pharma.voirLienActif(document.querySelector(".admin-nav")); });
window.pharma.zoom.appliquer(window.pharma.zoom.lire());

// Tableaux : l'écran reste fixe (titre, menu, filtres, pagination), seul le tableau défile dans son cadre
// (règle des listes, docs/charte-graphique.md). La hauteur de chaque cadre est ajustée à la place qui reste dans la fenêtre,
// sous ce qui le précède et au-dessus de ce qui le suit de près (pagination, total, note) et de la barre d'état.
window.pharma.tableaux = (function () {
  // Le panier de la caisse (cadre prioritaire) peut descendre plus bas : le bloc client ouvert passe avant lui, un temps
  const BARRE_ETAT = 32, MARGE = 24, MIN = 240, MIN_PRIORITAIRE = 110;   // 110 (2026-11-15) : volet sur deux lots avec carte « lot plus ancien », page à 0
  // data-hauteur-min : un cadre peut accepter moins de 240 px quand l'écran porte déjà un bandeau (ex. « Fixé par le serveur central », lot M7)
  function minimum(c) { return parseInt(c.dataset.hauteurMin, 10) || (c.classList.contains("cadre-prioritaire") ? MIN_PRIORITAIRE : MIN); }
  // Hauteur d'un cadre : plafonnée ; le cadre prioritaire (panier de la caisse) prend en plus toute cette hauteur, vide ou non
  function poser(c, h) {
    const valeur = h + "px";
    if (c.style.maxHeight !== valeur) c.style.maxHeight = valeur;
    if (c.classList.contains("cadre-prioritaire") && c.style.height !== valeur) c.style.height = valeur;
  }
  function hautDansPage(el) { let y = 0; for (let e = el; e; e = e.offsetParent) y += e.offsetTop; return y; }
  // Ce qui suit le cadre dans son conteneur et dans le conteneur au-dessus (petits éléments seulement : pagination, pied)
  function dessous(cadre) {
    let total = 0, niveau = cadre;
    for (let i = 0; i < 2 && niveau && !niveau.classList.contains("page"); i++, niveau = niveau.parentElement) {
      for (let s = niveau.nextElementSibling; s; s = s.nextElementSibling) {
        const h = s.offsetHeight;
        if (h > 160) return total;          // un autre panneau suit : la page défilera de toute façon jusqu'à lui
        total += h + 16;
      }
      const st = niveau.parentElement ? getComputedStyle(niveau.parentElement) : null;
      if (st) total += parseFloat(st.paddingBottom) || 0;
    }
    return total;
  }
  // Cadre placé dans un « cadre-unique » (2026-11-15, Encaisser une dette) : c'est le cadre englobant qui défile seul,
  // le cadre intérieur n'a pas de hauteur propre (pas de barres de défilement imbriquées)
  function interieur(c) { return !!(c.parentElement && c.parentElement.closest(".cadre-unique")); }
  // Listes de propositions ouvertes (.suggestions) écartées de la mesure (2026-11-18) : flottantes et déjà gardées dans la
  // fenêtre par leur propre placement, elles ne sont pas un débordement de l'écran. Comptées, elles réduisaient le panier le
  // temps de leur placement : le champ patient ou médecin sautait d'environ 130 px pendant la frappe.
  function ajuster() {
    const listes = Array.from(document.querySelectorAll(".suggestions")).filter(function (l) { return l.style.display !== "none"; })
      .map(function (l) { return { l: l, haut: l.scrollTop }; });
    listes.forEach(function (x) { x.l.style.display = "none"; });
    try { ajusterCadres(); }
    finally { listes.forEach(function (x) { x.l.style.display = ""; x.l.scrollTop = x.haut; }); }
  }
  function ajusterCadres() {
    const zoom = parseFloat(document.documentElement.style.zoom) || 1;
    const fenetre = window.innerHeight / zoom;
    document.querySelectorAll(".cadre-unique .tableau-cadre, .cadre-unique .cadre-defilant").forEach(function (c) {
      if (c.style.maxHeight) c.style.maxHeight = "";
      if (c.style.height) c.style.height = "";
    });
    document.querySelectorAll(".tableau-cadre, .cadre-defilant").forEach(function (c) {
      if (c.closest(".voile, .suggestions") || interieur(c)) return;   // fenêtres au-dessus de l'écran : leur propre défilement
      const h = Math.max(minimum(c), Math.floor(fenetre - hautDansPage(c) - dessous(c) - BARRE_ETAT - MARGE));
      poser(c, h);
    });
    // Correction : tant qu'il reste un débordement de l'écran (marges, notes, ligne de filtres ajoutée…), le plus grand cadre le reprend
    const d = document.scrollingElement;
    for (let essai = 0; essai < 8; essai++) {
      // Fractions de pixel comprises : un dépassement de 0,4 px suffit à faire apparaître la barre de défilement de la fenêtre
      const hauteurPage = document.documentElement.getBoundingClientRect().height;
      const deborde = Math.ceil(Math.max(d.scrollHeight - d.clientHeight, hauteurPage - window.innerHeight) / zoom);
      if (deborde <= 0) break;
      const cadres = Array.from(document.querySelectorAll(".tableau-cadre, .cadre-defilant")).filter(function (c) { return !c.closest(".voile, .suggestions") && !interieur(c); });
      // Un cadre marqué « prioritaire » (le panier de la caisse) reprend le débordement avant les autres
      const tries = cadres.sort(function (a, b) {
        const pa = a.classList.contains('cadre-prioritaire') ? 1 : 0, pb = b.classList.contains('cadre-prioritaire') ? 1 : 0;
        return pb - pa || b.clientHeight - a.clientHeight;
      });
      // Le plus grand cadre qui peut encore céder de la place (hauteur réelle au-dessus de son minimum) reprend le débordement ;
      // hauteur réelle, car le contenu peut être plus court que la hauteur permise (sinon la réduction reste sans effet)
      let reduit = false;
      for (const grand of tries) {
        const permise = parseFloat(grand.style.maxHeight) || grand.clientHeight;
        const actuel = Math.min(permise, grand.clientHeight);
        const h = Math.max(minimum(grand), actuel - deborde);
        if (h < actuel) { poser(grand, h); reduit = true; break; }
      }
      if (!reduit) break;   // plus rien à reprendre : l'écran est trop chargé (à découper en rubriques)
    }
    // Panier de la caisse : il descend jusqu'au-dessus de la barre d'état (écran fixe, rien d'autre ne défile)
    const prioritaire = document.querySelector('.cadre-prioritaire');
    const barre = document.querySelector('.barre-etat');
    const colonne = prioritaire && prioritaire.closest('.colonnes');
    if (prioritaire && barre && colonne && !prioritaire.closest('.voile')) {
      const libre = Math.floor((barre.getBoundingClientRect().top - colonne.getBoundingClientRect().bottom) / zoom) - MARGE;
      const actuel = parseFloat(prioritaire.style.height) || prioritaire.clientHeight;
      if (libre > 2) poser(prioritaire, actuel + libre);
      // Marges sous les colonnes : si la fenêtre déborde encore, le panier rend l'excédent
      const reste = Math.ceil(Math.max(d.scrollHeight - d.clientHeight, document.documentElement.getBoundingClientRect().height - window.innerHeight) / zoom);
      if (reste > 0) poser(prioritaire, Math.max(minimum(prioritaire), (parseFloat(prioritaire.style.height) || prioritaire.clientHeight) - reste));
    }
    // Caisse (2026-11-18) : panier à sa hauteur minimale et écran qui déborde encore (1366 × 768, bandeau de lot…) : le bloc client
    // se résume en une ligne, jamais pendant sa saisie (curseur dans le bloc) ; la page Caisse dit si le repli est permis (data-repli-auto)
    const bloc = document.querySelector('.bloc-client[data-repli-auto]');
    if (bloc && window.pharma._caisse && !bloc.contains(document.activeElement)) {
      const encore = Math.ceil(Math.max(d.scrollHeight - d.clientHeight, document.documentElement.getBoundingClientRect().height - window.innerHeight) / zoom);
      if (encore > 0) window.pharma._caisse.invokeMethodAsync('ReplierClientAuto');
    }
  }
  let prevu = false;
  function planifier()
  {
    if (prevu) return;
    prevu = true;
    requestAnimationFrame(function () { prevu = false; ajuster(); });
    // Second passage après l'affichage complet (polices, données arrivées juste après)
    clearTimeout(planifier._t);
    planifier._t = setTimeout(ajuster, 300);
  }
  function demarrer() {
    new MutationObserver(planifier).observe(document.body, { subtree: true, childList: true });
    window.addEventListener("resize", planifier);
    // Dépliage d'un <details> (relevé de la dette, 2026-11-15) : aucun nœud ajouté, l'observateur ne voit rien ; recalcul au dépliage
    document.addEventListener("toggle", planifier, true);
    planifier();
  }
  if (document.body) demarrer(); else document.addEventListener("DOMContentLoaded", demarrer);
  return { ajuster: ajuster };
})();
