// Erreurs mises sous les yeux de l'employé, sur tous les écrans, sans code dans chaque page.
// Après un clic ou une touche, si un message d'erreur (.erreur) apparaît ou si un champ passe en rouge (aria-invalid="true") :
//   * un champ en rouge : l'écran défile jusqu'à lui, le curseur s'y place, et une bulle à côté répète le message ;
//   * sinon : l'écran défile jusqu'au message, qui clignote brièvement.
// La bulle est posée hors des composants Blazor (dans <body>) : elle ne gêne pas leur rendu. Elle disparaît dès que le champ
// change ou redevient valide.
(function () {
  const DELAI_APRES_ACTION = 4000;   // une erreur qui arrive plus tard (rafraîchissement) ne fait pas sauter l'écran
  let derniereAction = 0;
  let vus = new WeakSet();           // messages et champs déjà signalés
  let bulle = null, champBulle = null;
  let messageServeur = null;         // message d'un refus du serveur qui a désigné son champ

  ['click', 'keydown', 'submit'].forEach(function (e) {
    document.addEventListener(e, function () { derniereAction = Date.now(); }, true);
  });

  const visible = (e) => e && e.isConnected && e.offsetParent !== null;

  function retirerBulle() {
    if (bulle) bulle.remove();
    bulle = null;
    if (champBulle) champBulle.removeEventListener('input', retirerBulle);
    champBulle = null;
  }

  function placerBulle() {
    if (!bulle || !visible(champBulle)) { retirerBulle(); return; }
    const r = champBulle.getBoundingClientRect();
    // Message d'erreur ou explication posé juste sous le champ (recette 2026-11-15) : la bulle passe dessous, sans le cacher
    let bas = r.bottom;
    const zone = champBulle.closest('.champ') || champBulle.parentElement;
    for (let s = zone && zone.nextElementSibling, n = 0; s && n < 2; s = s.nextElementSibling, n++) {
      if (!visible(s) || !s.matches('.erreur, .detail, [role="status"], [role="alert"]')) break;
      const rs = s.getBoundingClientRect();
      if (rs.top - bas > 40) break;
      bas = Math.max(bas, rs.bottom);
    }
    bulle.style.top = (window.scrollY + bas + 6) + 'px';
    bulle.style.left = (window.scrollX + Math.max(8, Math.min(r.left, window.innerWidth - bulle.offsetWidth - 8))) + 'px';
  }

  // Message à côté du champ : celui de l'erreur affichée, sinon le libellé du champ
  function messagePour(champ, erreur) {
    if (messageServeur) { const m = messageServeur; messageServeur = null; return m; }
    if (erreur) return erreur.innerText.trim();
    const libelle = champ.id ? document.querySelector('label[for="' + champ.id + '"]') : champ.closest('label');
    const nom = (libelle ? libelle.innerText : champ.getAttribute('aria-label') || champ.placeholder || '').replace(/\*/g, '').trim();
    return nom ? 'À renseigner : ' + nom + '.' : 'Ce champ est à renseigner.';
  }

  function signaler() {
    const recente = Date.now() - derniereAction < DELAI_APRES_ACTION;
    const nouveauxChamps = [...document.querySelectorAll('[aria-invalid="true"]')].filter(e => visible(e) && !vus.has(e));
    const nouvellesErreurs = [...document.querySelectorAll('.erreur:not([role="status"])')].filter(e => visible(e) && !vus.has(e));
    nouveauxChamps.forEach(e => vus.add(e));
    nouvellesErreurs.forEach(e => vus.add(e));
    // Bulle orpheline : champ redevenu valide, ou disparu de l'écran (changement d'écran, fenêtre fermée)
    if (bulle && (!visible(champBulle) || champBulle.getAttribute('aria-invalid') !== 'true')) retirerBulle();
    if (!recente || (nouveauxChamps.length === 0 && nouvellesErreurs.length === 0)) return;

    // Le champ en rouge le plus haut dans la page (ordre du document), sinon le premier message
    const champ = [...document.querySelectorAll('[aria-invalid="true"]')].find(visible);
    const erreur = nouvellesErreurs[0] || null;
    if (champ) {
      champ.scrollIntoView({ behavior: 'smooth', block: 'center' });
      try { champ.focus({ preventScroll: true }); } catch (e) { }
      retirerBulle();
      bulle = document.createElement('div');
      bulle.className = 'bulle-erreur';
      bulle.setAttribute('role', 'alert');
      bulle.textContent = messagePour(champ, erreur);
      document.body.appendChild(bulle);
      champBulle = champ;
      champ.addEventListener('input', retirerBulle);
      placerBulle();
      setTimeout(placerBulle, 450);   // après le défilement
    } else if (erreur) {
      erreur.scrollIntoView({ behavior: 'smooth', block: 'center' });
      erreur.animate([{ boxShadow: '0 0 0 0 rgba(180,35,24,0)' }, { boxShadow: '0 0 0 6px rgba(180,35,24,0.35)' }, { boxShadow: '0 0 0 0 rgba(180,35,24,0)' }],
        { duration: 900, iterations: 2 });
    }
  }

  let attente = null;
  new MutationObserver(function () {
    if (attente) return;
    attente = setTimeout(function () { attente = null; signaler(); }, 60);
  }).observe(document.documentElement, { childList: true, subtree: true, attributes: true, attributeFilter: ['aria-invalid', 'class'] });

  // Refus du serveur qui désigne un champ (ErreurDto.Champ) : le champ data-champ de ce nom passe en rouge. On cherche
  // d'abord dans la fenêtre ouverte au premier plan, puis dans la page.
  window.pharma = window.pharma || {};
  window.pharma.erreurs = {
    marquer: function (nom, message) {
      const zones = [...document.querySelectorAll('.voile')].reverse().concat([document.querySelector('main') || document.body]);
      for (const zone of zones) {
        const champ = [...zone.querySelectorAll('[data-champ]')].find(e => visible(e) && e.dataset.champ.split(' ').includes(nom));
        if (!champ) continue;
        champ.setAttribute('aria-invalid', 'true');
        champ.dataset.invalideServeur = '1';
        champ.addEventListener('input', function lever() {
          if (champ.dataset.invalideServeur) { champ.removeAttribute('aria-invalid'); delete champ.dataset.invalideServeur; }
          champ.removeEventListener('input', lever);
        });
        vus.delete(champ);
        messageServeur = message;
        derniereAction = Date.now();
        return true;
      }
      return false;
    }
  };

  window.addEventListener('scroll', placerBulle, true);
  window.addEventListener('resize', placerBulle);
  // Changement d'écran (le routeur Blazor passe par history.pushState, le retour arrière par popstate) : on repart de zéro
  function nouvelEcran() { retirerBulle(); messageServeur = null; vus = new WeakSet(); }
  window.addEventListener('popstate', nouvelEcran);
  ['pushState', 'replaceState'].forEach(function (nom) {
    const origine = history[nom];
    history[nom] = function () { const r = origine.apply(this, arguments); nouvelEcran(); return r; };
  });
})();
