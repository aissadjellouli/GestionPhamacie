// Écrans du central (lot S7.1) : seul script de la page (CSP « script-src 'self' », aucun script en ligne).
// 1. Écran fixe : chaque tableau (.tableau-cadre) prend la hauteur restante de la fenêtre et défile seul dans son cadre
//    (même règle que poste.js du poste, en plus simple : pas de zoom, pas de caisse).
// 2. Bouton « Copier » du code d'activation (data-copier = id de l'élément à copier).
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

  function ajuster() {
    var zonesFixes = zones();
    var cadres = Array.prototype.slice.call(document.querySelectorAll(".tableau-cadre"));
    cadres.forEach(function (c) { c.style.maxHeight = ""; });
    cadres.forEach(function (c) {
      var haut = c.getBoundingClientRect().top + window.scrollY;
      var h = Math.max(MINIMUM, Math.floor(window.innerHeight - haut - dessous(c) - MARGE));
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
      var h = Math.max(MINIMUM, actuel - deborde);
      if (h >= actuel) break;
      grand.style.maxHeight = h + "px";
    }
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

  function demarrer() {
    ajuster();
    window.addEventListener("resize", ajuster);
    if (document.fonts && document.fonts.ready) document.fonts.ready.then(ajuster);
    document.addEventListener("click", copier);
  }
  if (document.readyState === "loading") document.addEventListener("DOMContentLoaded", demarrer); else demarrer();
})();
