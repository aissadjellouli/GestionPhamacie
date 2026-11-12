// Fonctions du poste appelées depuis Blazor : raccourci F12 en caisse et suivi de l'inactivité.
window.pharma = {
  _caisse: null,
  _derniereActivite: Date.now(),

  // La page Caisse s'abonne au raccourci F12 (Encaisser) et se désabonne en quittant la page
  ecouterF12: function (reference) { this._caisse = reference; },
  arreterF12: function () { this._caisse = null; },

  // Secondes écoulées depuis la dernière action (clavier, souris, scanner)
  inactiviteSecondes: function () { return Math.floor((Date.now() - this._derniereActivite) / 1000); }
};

document.addEventListener('keydown', function (e) {
  window.pharma._derniereActivite = Date.now();
  if (e.key === 'F12' && window.pharma._caisse) {
    e.preventDefault();
    window.pharma._caisse.invokeMethodAsync('SurF12');
  }
}, true);

['mousedown', 'mousemove', 'wheel', 'touchstart'].forEach(function (evenement) {
  document.addEventListener(evenement, function () { window.pharma._derniereActivite = Date.now(); }, { passive: true });
});

// Caméra au comptoir : photo de l'ordonnance (l'autorisation est accordée par la fenêtre du poste)
window.pharma.camera = {
  _flux: null,
  demarrer: async function (idVideo) {
    const video = document.getElementById(idVideo);
    this._flux = await navigator.mediaDevices.getUserMedia({
      video: { width: { ideal: 1920 }, height: { ideal: 1080 }, facingMode: 'environment' }, audio: false });
    video.srcObject = this._flux;
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
