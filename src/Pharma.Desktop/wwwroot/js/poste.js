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
