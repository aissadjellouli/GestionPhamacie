// Écrans du central (lot S7.1) : seul script de la page (CSP « script-src 'self' », aucun script en ligne).
// 1. Écran fixe : chaque tableau (.tableau-cadre) prend la hauteur restante de la fenêtre et défile seul dans son cadre
//    (même règle que poste.js du poste, en plus simple : pas de zoom, pas de caisse).
// 2. Bouton « Copier » du code d'activation (data-copier = id de l'élément à copier).
// 3. Choix de l'affichage (clair, sombre, automatique) appliqué dès qu'il change.
// 4. Après une saisie refusée, les champs remplis sont remis (jamais un mot de passe ni un code).
(function () {
  "use strict";
  var MARGE = 16, MINIMUM = 140;

  function dessous(cadre) {
    var total = 0;
    for (var n = cadre; n && !n.classList.contains("page"); n = n.parentElement) {
      for (var s = n.nextElementSibling; s; s = s.nextElementSibling) total += s.offsetHeight + 16;
      var st = n.parentElement ? getComputedStyle(n.parentElement) : null;
      if (st) total += parseFloat(st.paddingBottom) || 0;
    }
    return total;
  }

  // Zone à deux colonnes (data-hauteur-restante, ex. Réglages communs) : hauteur réelle restante de la fenêtre (messages compris),
  // ses panneaux défilent dedans ; rien sous 1 100 px de large (colonnes empilées, hauteur libre)
  function zones() {
    var liste = Array.prototype.slice.call(document.querySelectorAll("[data-hauteur-restante]"));
    liste.forEach(function (z) { z.style.height = ""; });
    if (window.innerWidth <= 1100) return [];
    liste.forEach(function (z) {
      var haut = z.getBoundingClientRect().top + window.scrollY;
      z.style.height = Math.max(260, Math.floor(window.innerHeight - haut - dessous(z) - MARGE)) + "px";
    });
    return liste;
  }

  // Hauteur minimale d'un cadre : MINIMUM, ou data-hauteur-min d'un parent (page chargée en formulaires, ex. Ordres de l'officine)
  function minimum(c) {
    var p = c.closest("[data-hauteur-min]");
    var v = p ? parseInt(p.getAttribute("data-hauteur-min"), 10) : NaN;
    return isNaN(v) ? MINIMUM : Math.max(80, v);
  }

  function ajuster() {
    var zonesFixes = zones();
    var cadres = Array.prototype.slice.call(document.querySelectorAll(".tableau-cadre"));
    cadres.forEach(function (c) { c.style.maxHeight = ""; });
    cadres.forEach(function (c) {
      var haut = c.getBoundingClientRect().top + window.scrollY;
      var h = Math.max(minimum(c), Math.floor(window.innerHeight - haut - dessous(c) - MARGE));
      c.style.maxHeight = h + "px";
    });
    // Reste un débordement (marges, notes) : le plus grand cadre le reprend
    var d = document.scrollingElement;
    for (var essai = 0; essai < 4; essai++) {
      var deborde = Math.ceil(d.scrollHeight - d.clientHeight);
      if (deborde <= 0) break;
      if (zonesFixes.length > 0) {
        var z = zonesFixes[0], hz = parseFloat(z.style.height) || z.clientHeight, nz = Math.max(260, hz - deborde);
        if (nz < hz) { z.style.height = nz + "px"; continue; }
      }
      if (cadres.length === 0) break;
      var grand = cadres.slice().sort(function (a, b) { return b.clientHeight - a.clientHeight; })[0];
      var actuel = parseFloat(grand.style.maxHeight) || grand.clientHeight;
      var h = Math.max(minimum(grand), actuel - deborde);
      if (h >= actuel) break;
      grand.style.maxHeight = h + "px";
    }
    // Espace encore libre sous la page (estimation de « dessous » trop large) : la zone le reprend, sans jamais faire défiler
    zonesFixes.forEach(function (z) {
      var libre = Math.floor(window.innerHeight - document.documentElement.getBoundingClientRect().height);
      if (libre > 2) z.style.height = (parseFloat(z.style.height) + libre - 2) + "px";
    });
  }

  function copier(e) {
    var bouton = e.target.closest("[data-copier]");
    if (!bouton) return;
    var cible = document.getElementById(bouton.getAttribute("data-copier"));
    if (!cible || !navigator.clipboard) return;
    navigator.clipboard.writeText(cible.textContent.trim()).then(function () {
      var fait = document.querySelector('[data-copie-faite="' + bouton.getAttribute("data-copier") + '"]');
      if (fait) fait.hidden = false;
    });
  }

  // 4. Saisie gardée après un refus (« Enregistrer », e-mail d'essai, contact, ordres…) : à l'envoi d'un formulaire, ses valeurs
  //    sont gardées pour l'onglet (sessionStorage, jamais dans l'adresse ni au serveur) ; si la page revient avec « ?erreur= », elles
  //    sont remises dans les champs de même nom ; « ?ok= » les efface. JAMAIS gardés : mot de passe, code de confirmation, champs
  //    cachés (jeton anti-falsification, retour).
  var CLE_SAISIE = "pharma-saisie:";
  function gardable(c) {
    if (!c.name || c.disabled) return false;
    var type = (c.type || "").toLowerCase();
    if (type === "password" || type === "hidden" || type === "submit" || type === "button" || type === "file") return false;
    return !/^(code|__RequestVerificationToken|mot_de_passe|retour|confirmation)$/i.test(c.name);
  }
  function garderSaisie(e) {
    var f = e.target;
    if (!f || f.tagName !== "FORM" || (f.method || "").toLowerCase() !== "post") return;
    var valeurs = {};
    Array.prototype.forEach.call(f.elements, function (c) {
      if (!gardable(c)) return;
      if (c.type === "checkbox" || c.type === "radio") { if (c.checked) valeurs[c.name + "=" + c.value] = true; }
      else valeurs[c.name] = c.value;
    });
    try { sessionStorage.setItem(CLE_SAISIE + location.pathname, JSON.stringify(valeurs)); } catch (x) { /* stockage indisponible */ }
  }
  function remettreSaisie() {
    var cle = CLE_SAISIE + location.pathname, texte = null;
    var params = new URLSearchParams(location.search);
    try {
      if (params.has("ok")) { sessionStorage.removeItem(cle); return; }
      if (!params.has("erreur")) return;
      texte = sessionStorage.getItem(cle);
    } catch (x) { return; }
    if (!texte) return;
    var valeurs;
    try { valeurs = JSON.parse(texte); } catch (x) { return; }
    Array.prototype.forEach.call(document.querySelectorAll("main form[method=post] [name], form[method=post] [name]"), function (c) {
      if (!gardable(c)) return;
      if (c.type === "checkbox" || c.type === "radio") c.checked = !!valeurs[c.name + "=" + c.value];
      else if (Object.prototype.hasOwnProperty.call(valeurs, c.name)) c.value = valeurs[c.name];
    });
  }

  function demarrer() {
    remettreSaisie();
    document.addEventListener("submit", garderSaisie, true);
    ajuster();
    window.addEventListener("resize", ajuster);
    if (document.fonts && document.fonts.ready) document.fonts.ready.then(ajuster);
    document.addEventListener("click", copier);
    // 3. Affichage clair / sombre / automatique : appliqué dès le choix dans la liste (le bouton « Appliquer » reste pour un navigateur sans script)
    document.addEventListener("change", function (e) {
      var choix = e.target.closest ? e.target.closest("select.choix-theme") : null;
      if (choix && choix.form) choix.form.submit();
    });
  }
  if (document.readyState === "loading") document.addEventListener("DOMContentLoaded", demarrer); else demarrer();
})();
