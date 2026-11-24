# Charte graphique du poste

Règle permanente : **tout écran, nouveau ou modifié, respecte cette charte.** On ne s'en écarte que sur demande explicite du
titulaire du projet, et l'écart est alors noté ici. Le modèle de référence est l'Administration : titre, sous-titre, panneaux
et notes explicatives discrètes.

Tout est défini dans `src/Pharma.Desktop/wwwroot/css/app.css`. Un écran ne redéfinit ni taille, ni couleur, ni police : il
utilise les classes ci-dessous. Pas de `style="font-size: …"`, `style="color: …"` ni `style="height: …"` dans un écran.

## Logo

**Logo Tracodex** (gélule verte et ambre, croissant et serpent blancs), adopté le 2026-11-18 à la place de la croix verte :
fichiers sources, couleurs, tailles minimales et règles dans [branding/logo.md](branding/logo.md). Dans le poste, l'icône seule
(`wwwroot/img/logo.svg`) est posée sur une vignette blanche arrondie de 40 px dans l'en-tête vert (`.logo`) et sans fond sur l'écran
de connexion (`.logo-connexion`, 64 px). Le logo n'est jamais redessiné, recoloré, déformé ni ombré, et son croissant n'est jamais
rouge. Depuis le 2026-11-18, le vert de l'interface est celui du logo (« Fonds » ci-dessous). Vérifié le 2026-11-18 à
1 366 × 768, clair et sombre (connexion, en-tête).

## En-tête et menu

Une seule ligne, dans cet ordre : logo et nom de la pharmacie (lien vers l'accueil), **« ← *écran précédent* »**, menu des
**domaines** (Caisse, Stock, Achats, Finances, Pilotage ; chacun suivi de son raccourci « ⇧lettre » dans un petit cadre),
**« ⌕ Rechercher… Ctrl K »**, puis outils de l'employé. Pas de menu déroulant : les écrans d'un domaine sont dans le menu de
gauche (docs/navigation.md).
« ← *écran précédent* » (à gauche) et « ⇄ Changer » (à droite) sont des **pastilles blanches à texte vert** : repérables d'un coup
d'œil, et éloignées du menu pour ne pas être confondues avec un écran. Leurs libellés restent courts pour garder l'en-tête sur une ligne.

L'en-tête occupe **toute la largeur de la fenêtre** (validé le 2026-11-13) : logo et nom dans les 240 px de gauche, au-dessus de la
colonne du menu vertical, le reste réparti jusqu'au bord droit. Nom du logiciel et « pharmacie · poste » restent sur une ligne,
coupés par « … » s'ils sont trop longs (texte complet au survol). Quand la fenêtre se resserre (≤ 1 450 px, dont 1 366 × 768), le nom de
la pharmacie est **le premier masqué** (logo seul, nom au survol) : le libellé de « ← *écran précédent* » reste lisible **en entier**
(« Administration », « Dettes et créances », « Tableau de bord financier »…, plafond 200 px) ; si la place manquait vraiment (nom d'employé très
long, zoom), seul ce bouton se coupe par « … » (complet en infobulle et pour les lecteurs d'écran), jamais un retour à la ligne ni un
chevauchement. Règle de vérification d'un changement d'en-tête : 1 366 × 768 et 1 536 × 890, clair et sombre, sans défilement, sans
libellé tronqué, rectangles des éléments sans chevauchement.

- **Premier niveau : les écrans du quotidien** : Caisse, Stock, Achats, Alertes (avec le compteur des alertes urgentes).
- **Groupes déroulants** : **Finances ▾** (Factures, Trésorerie, Dettes et créances, Chifa « à venir ») et **Pilotage ▾**
  (Rapports, Contrôle interne). Le groupe qui contient l'écran ouvert est souligné en ambre ; dans le menu ouvert, l'écran
  ouvert est marqué d'un trait ambre à gauche.
- **Outils à droite**, dans cet ordre : ⚙ Administration (si l'employé y a droit), le **menu à son nom ▾** (l'employé
  connecté : Mon code comptoir, Mot de passe, Affichage, Zoom de l’écran − 80 à 150 % + retenu par le poste, Guide et retours,
  Changer d’utilisateur, Se déconnecter), puis
  **⇄ Changer** (changer d'utilisateur, la caisse du poste reste ouverte), le plus à droite.
- **Un écran sans droit n'apparaît pas** (pas de lien grisé) ; un groupe sans écran permis disparaît. Seul un écran pas
  encore disponible (Chifa) reste visible, grisé, avec « (à venir) ».
- Un menu déroulant s'ouvre au clic et se ferme avec Échap, un clic à côté ou un changement d'écran.
- Un nouvel écran se range dans un groupe existant ; le premier niveau reste limité aux écrans du quotidien.

## Structure d'un écran

```html
<div class="page">
    <div class="titre-page">
        <div>
            <h1>Titre de l'écran</h1>
            <p class="sous-titre">Une phrase : à quoi sert l'écran</p>
        </div>
        <!-- actions principales de l'écran -->
    </div>

    <section class="panneau" aria-labelledby="t-…">
        <h2 id="t-…">Titre du panneau</h2>
        …
    </section>

    <p class="pied-page">Note explicative de bas d'écran.</p>
</div>
```

### Écran fixe, sans défilement (règle valable sur tous les écrans, demandée le 2026-11-13)

**Précision du titulaire (2026-11-18)** : la règle vaut pour les **écrans principaux**, mesurés à l'arrivée, formulaires fermés,
à 1 536 × 890 et à 1 366 × 768. Un formulaire ouvert à la demande (correction d'un médecin, fiche prêt, usage d'un financement…)
peut faire défiler la page s'il est trop haut : on le signale, ce n'est pas un défaut.

**Un écran ne défile jamais** : titre, menu, filtres, boutons et pagination restent en place dans la fenêtre (1 536 × 890 sur le
poste de référence, zoom 100 %). Seul un tableau défile, dans son cadre (règle des listes ci-dessous). Un écran dont le contenu
dépasse la fenêtre est **découpé en rubriques** avec le menu vertical à gauche (ci-dessous) : une rubrique = ce qui tient dans
la fenêtre. Exceptions : les fenêtres au-dessus de l'écran (`.voile`), qui défilent dans leur cadre.

### Vérification de l’écran fixe

**Méthode de mesure** (poste lancé à 1 536 × 890, zoom 100 %, WebView2 ouvert au débogage distant) : sur chaque écran, chaque
rubrique (`?rubrique=`, `?onglet=`, `?vue=`) et les états qui ajoutent du contenu (formulaires « Nouveau … », panneaux
dépliés), la valeur `document.scrollingElement.scrollHeight - innerHeight` doit valoir **0**. Un dépassement se corrige
dans cet ordre : contenu dans un cadre défilant (`.tableau-cadre` ou `.cadre-defilant`, hauteur ajustée par `poste.js` ;
`.cadre-unique` pour un seul cadre englobant), éléments regroupés sur une ligne ou en colonnes, sinon rubriques à gauche.
Aucune information n’est masquée pour gagner de la place. Accueil : les tuiles (ventes du jour, à faire, contrôle) sont
dans un cadre défilant (`.accueil-tuiles`), grille dense (tuiles de 150 px au moins).

Le test `tests/Pharma.Architecture.Tests/EcranFixeTests.cs` exige que chaque écran du poste (`@page`) figure dans la liste
ci-dessous : un nouvel écran est mesuré, puis ajouté ici avec la date.

**Écrans vérifiés le 2026-11-16 (0 px partout ; titulaire, toutes rubriques ; Accueil et Caisse aussi en assistant et
vendeur)** : `/`, `/accueil`, `/caisse`, `/caisse/etat`, `/cloture`, `/stock`, `/stock/etiquettes`, `/stock/registre`,
`/inventaire`, `/tracabilite`, `/achats`, `/factures`, `/dettes`, `/tresorerie`, `/financements`, `/rapports`,
`/surveillance`, `/surveillance/risques`, `/surveillance/tiroir`, `/bordereaux-chifa`, `/chifa`, `/patients/droits-chifa`,
`/admin/caisse`, `/admin/centres-payeurs`, `/admin/doublons-import`, `/admin/conflits-facture-chifa`, `/admin/indications`,
`/admin/motifs`, `/admin/prestations`, `/admin/controle-interne`, `/admin/droits`, `/admin/emplacements`,
`/admin/imprimantes`, `/admin/nomenclature`, `/admin/notifications`, `/admin/pharmacie`, `/admin/reprise`,
`/admin/profils-reprise` et `/admin/historique-chifa` (ajoutés le 2026-11-16, lot 4 : liste paginée et filtrée, seul le tableau défile,
édition du profil dans une fenêtre `.voile` qui défile dans son cadre ; `/admin/historique-chifa` remesuré par la campagne du
2026-11-20 ci-dessous ; reprise et profils de reprise à 1 366 × 768 : non mesurés, voir plus bas),
`/admin/restauration`, `/admin/sauvegardes`, `/admin/seuils`, `/guide`, `/mon-code`, `/mot-de-passe`.
**`/admin/postes` mesuré le 2026-11-24** (titulaire, clair et sombre, 1 536 × 890 et 1 366 × 768) : 0 px de défilement vertical et horizontal ; liste paginée et filtrée (statut, nom ou ordinateur), seul le tableau défile ; autorisation, code, renommage, révocation et passage en « exigé » dans une fenêtre `.voile`. Code d'autorisation affiché en 32 px (`.code-enrolement`, voulu pour être recopié sans erreur). Écran de connexion : bandeau `.info-bandeau` (transition) ou `.bandeau-attention` (exigé, révoqué), carte « Autoriser ce poste » ; 0 px.
**`/admin/seuils?rubrique=produits` corrigé le 2026-11-18** (l'écran défilait de 75 px à 1 366 × 768, défaut déjà présent avant
le filtre avec propositions : le tableau restait à son minimum de 240 px sous le choix du produit) : deux colonnes dans le panneau
(`.colonnes` : « Produit à régler » et la carte du produit à gauche, 340 px ; filtres, tableau et pagination à droite). Mesuré
(titulaire, clair et sombre, 1 536 × 890 et 1 366 × 768) : 0 px liste fermée, liste du produit ouverte, liste du filtre ouverte,
carte d'un produit affichée (seuil automatique ou saisi).
**Campagne 1 366 × 768 du 2026-11-20** (poste compilé à part, titulaire, toutes rubriques des écrans de la liste ci-dessus) :
17 écrans défilaient de 1 à 195 px, corrigés puis remesurés à 0 px à 1 366 × 768 et à 1 536 × 890, captures clair et sombre
regardées. Corrections : rubriques à gauche (Caisse et contrôle : « Chifa : délais et seuils » séparé des « Règles » ;
Notifications : « E-mail » et « SMS » séparés) ; cadres de tableau acceptant 140 px (`data-hauteur-min="140"` : Achats › À
commander, En attente, Grossistes, Notifications › File d'attente, Suivi des antibiotiques) ; titre et boutons sur une ligne
(`titre-actions-cote` : Nomenclature, Sauvegardes, Suivi des antibiotiques) ; histogramme de la Synthèse financière à 90 px
en fenêtre basse (≤ 800 px) ; marge du bas des pages à menu ramenée de 36 à 20 px en fenêtre basse. Reste : la fiche d'un prêt
ouvert (Financements, formulaire ouvert à la demande, toléré) défile de 77 à 226 px.
**Fiche d'un prêt ouvert revue le 2026-11-21** (Financements › Prêts bancaires, `/financements?rubrique=prets&pret=<id>` ; elle
défilait de 77 à 226 px à 1 366 × 768) : les cinq tuiles du résumé en `.tuiles.tuiles-compactes.tuiles-pret` (une seule rangée,
pied en 12 px), affichées sauf pendant la saisie de l'échéancier ; tableaux Échéancier et Usages en `.tableau-cadre`
`data-hauteur-min="140"` ; onglets « Prêt et document… » et « Employer l'argent… » dans un `.cadre-defilant` (160 px au moins) ;
saisie ou import de l'échéancier dans un `.cadre-defilant.cadre-unique` (un seul cadre, ses tableaux sans hauteur propre). Aucune
information retirée. **Mesurée le 2026-11-21** sur les quatre onglets : 0 px, clair et sombre, 1 366 × 768 et 1 536 × 890 (voir
« Caisse et contrôle › Règles » ci-dessous) ; « Modifier ou réimporter l'échéancier… » (saisie et étape « correspondance des
colonnes ») : aucune mesure notée, à vérifier.
**Fenêtre « Changement d'utilisateur impossible » ajoutée le 2026-11-21** (en-tête, « ⇄ Changer » quand la vente de la Caisse n'a
pas pu être mise en attente) : `.voile` + `.panneau.fenetre-releve` (comme la relève), message en `.erreur`, boutons en `.actions`
(« Réessayer » principal, « Rester sur la vente ») ; aucun style en ligne. Mesurée et capturée en clair et en sombre le 2026-11-21 (« Mesures du 2026-11-21 (fin de journée) » ; cas : Caisse panier rempli,
serveur arrêté).
**Rapports › Synthèse revu le 2026-11-20** : la vue Mois défilait de 102 px à 1 366 × 768 (tuile Chiffre d'affaires sur dix
lignes : répartition médicaments · parapharmacie · prestations et retours clients dans son pied) ; tuiles « Parapharmacie »
(répartition) et « Retours clients » à part, affichées seulement quand elles ont une valeur. Mesuré 0 px en Jour, Semaine et Mois,
1 366 × 768 et 1 536 × 890, clair et sombre. Écrans de la parapharmacie (Nomenclature › Produits à classer et Version et
catégories, Stock › Produits avec la pastille Parapharmacie, Nouveau produit) : 0 px aux deux tailles, clair et sombre.
**Caisse et contrôle › Règles, mesuré le 2026-11-21** après l'ajout du réglage « Facture et retour fournisseur laissés »
(script 156) : 36 px de défilement à 1 366 × 768 ; intitulé et deux choix sur une ligne (libellés courts, détail dans la bulle),
puis les deux choix du versement au coffre côte à côte de la même façon : 0 px à 1 366 × 768 et 1 536 × 890, clair et sombre,
captures regardées. Mesurés le même jour, 0 px aux deux tailles, clair et sombre : fiche d'un prêt (quatre onglets), panneau de
retour d'une vente en tiers payant, Chifa › « Retours à corriger dans CHIFA-Officine ».
**Caisse › Annulation ou retour, panneau de retour, mesuré le 2026-11-21** (script 158, retour et dettes du patient) : avec
l'alerte des dettes en bas, le panneau défilait de 218 px à 1 366 × 768 et de 112 px à 1 536 × 890, alerte sous le pli ;
réorganisé : résumé de la vente, alerte des dettes et ses choix juste dessous (toujours visibles), lignes, motif et précision dans
un seul `.cadre-defilant.retour-cadre`, bouton « Enregistrer le retour » sous le cadre. Mesuré 0 px, bouton visible, à
1 366 × 768 et 1 536 × 890, clair et sombre, captures regardées : vente en tiers payant sans dette, patient endetté (« Déduire de
ses dettes »), patient endetté (« Rendre quand même »).
**Mesures du 2026-11-21 (fin de journée)** :
- **Caisse, fenêtre « Changement d'utilisateur impossible »** (serveur arrêté pendant une vente, puis « Changer ») : fenêtre
  entière à l'écran, 0 px de défilement de la page à 1 366 × 768 et 1 536 × 890, clair et sombre ; « Rester sur la vente »
  referme la fenêtre et garde le panier.
- **Panneau de retour, régularisation « rendu au patient »** (script 159) : ligne compacte de la retenue dans la zone des
  alertes, 0 px aux deux tailles, clair et sombre, bouton visible. Les états « boîtes à 100 % » et « patient endetté avec
  retenue » n'ont pas de vente d'exemple dans `pharmacie_test` (couverts par les tests d'écran).
- **Écrans du central (S7.1, `Pharma.Cloud.Web`)**, navigateur sans fenêtre, compte `editeur.dev` : connexion, code, mot de passe,
  liste des officines, fiche (résumé et 12 rubriques), nouvelle officine, nouveau code, révocation de serveur, mon journal,
  accès refusé : 19 pages, 0 px à 1 366 × 768 et 1 536 × 890, thèmes clair et sombre. Défaut trouvé et corrigé : la CSP
  `base-uri 'none'` faisait ignorer `<base href="/">` ; sur les pages imbriquées (`/officines/…`), feuille de style, script et
  logo étaient cherchés sous `/officines/` (404) et la rubrique « Lots reçus » défilait de 532 px (654 px à 1 536 × 890) ;
  `base-uri 'self'`, test `La_balise_base_est_permise…`.
**Seuils par produit revu le 2026-11-20** (Administration › Alertes et seuils › Seuils par produit, demande du titulaire : 8 à 10
lignes visibles à 1 366 × 768 ; avant : 2 lignes entières, lignes de 70 px) : titre du panneau retiré (il répétait celui de la
page), explication en `.detail` dans la colonne de gauche (masquée quand un produit est choisi), tableau `tableau-serre`, cellules
sur une ligne sauf le nom du produit, action de ligne « Automatique » (bulle et nom accessible « Revenir au seuil automatique »).
**Écart à la règle « filtres au-dessus du tableau », imposé par la demande** : les filtres du tableau (Produit, État, Tout afficher)
sont dans la colonne de gauche, sous le choix du produit, avec sa carte, dans un `.cadre-defilant` ; le tableau prend toute la
hauteur de la colonne de droite. Mesuré (poste à part, titulaire, clair et sombre, captures regardées) : 0 px de défilement de la
page et 0 px horizontal, 8 lignes visibles à 1 366 × 768, 10 à 1 536 × 890 ; produit choisi et filtre actif : 0 px, la colonne
de gauche défile dans son cadre. Caisse avec le lien « Patients en attente » (colonne Comptoir) : 0 px à 1 366 × 768 et
1 536 × 890, clair et sombre.
**Écran ajouté le 2026-11-16 (lot 10, sérialisation), conçu fixe (seul le tableau défile, paginé et filtré par statut) ; mesure
à 1 536 × 890 à faire à l'écran** : `/admin/serialisation`. **Mesuré le 2026-11-21** : 0 px (« Mesures du 2026-11-21 (après-midi) »).
**Écran ajouté le 2026-11-16 (lot 5, 2e serveur), conçu fixe 1 536 × 890 (trois sous-rubriques à gauche : État de la réplication,
Réglages par serveur, Clé de scellement ; tableau des serveurs seul défilant) ; mesure à 0 px à faire à l'écran** : `/admin/second-serveur`. **Mesuré le 2026-11-21** : 0 px (« Mesures du 2026-11-21 (après-midi) »).
**Écran ajouté le 2026-11-16 (lot S2, synchronisation), conçu fixe 1 536 × 890 (trois sous-rubriques à gauche : État, Réglages,
Activation ; aucun tableau, aucun défilement) ; mesure à 0 px à faire à l'écran** : `/admin/synchronisation`.
Mesurée le 2026-11-16 (titulaire, clair et sombre, 1 536 × 890 et 1 366 × 768) : 0 px dans les trois rubriques, y compris
synchronisation activée avec bandeaux d'attention, chargement en cours et confirmation « Tout renvoyer » ; rubrique État : bandeaux
au-dessus, panneaux État et Chargement complet dans un `.cadre-defilant` ; titre et boutons sur une ligne (`.titre-actions-cote`).
**Écran ajouté le 2026-11-16 (lot 11, tableau de bord financier), conçu fixe 1 536 × 890 (sept rubriques à gauche : Synthèse,
Créances, Stock et dormant, Marges, Achats par grossiste, Rejets Chifa, TVA par taux ; tuiles, puis un seul tableau défilant,
paginé ; marges et stock dormant filtrés et paginés par le serveur) ; mesure à 0 px à faire à l'écran** : `/pilotage-financier`.
Synthèse mesurée le 2026-11-16 (voir plus bas) ; autres rubriques : couvertes par la campagne 1 366 × 768 du 2026-11-20 (ci-dessus).
Corrigés ce jour : Accueil (27 px), Administration › Contrôle interne (29 px, horaires sous « Stock et réception » sur une
ligne), Trésorerie › Chèques à remettre (4 632 px, liste en cadre défilant) et › Caisses à verser (10 px), Achats ›
Réceptions › En attente (272 px : pastilles dès qu’il y a des livraisons comptées), Achats › À commander avec un produit
choisi (33 px : produit, bouton et note sur une ligne).
**Reprise de données mesurée et corrigée le 2026-11-16** (1 536 × 890, super admin, thèmes clair et sombre, 0 px après
correction, ni vertical ni horizontal) : étape 1 Source (393 px avant : « Où est l’ancienne base ? » à gauche, liste des
sources en `.cadre-defilant` ; à droite « Connexion » en cadre défilant quand un pilote est choisi, puis le tableau des
reprises précédentes, seul à défiler, et sa pagination) ; étapes 3 à 5 avec une reprise ouverte (260 px et 686 px avant :
en-tête sur une ligne, douze compteurs compacts sur deux lignes, contrôles filtrés et paginés à gauche, à confirmer,
chargement et vérification à droite dans un cadre défilant). Étape 2 non mesurée (demande une ancienne base réelle).
**Écrans du super admin corrigés le 2026-11-16** (1 536 × 890, clair et sombre, captures comparées) : en-tête sur une ligne,
« Connecté : … » à droite (il était collé au bord gauche, sur une 2e ligne) ; un écran `.page.admin` sans menu de gauche
(`NavAdmin` vide pour le super admin) n'a plus la colonne vide de 240 px ni le contenu collé au bord : page centrée de
1 280 px au plus, comme les écrans sans menu vertical. Écrans des employés inchangés (Accueil, Administration › Notifications,
captures identiques). Restauration : 104 px → 0 (sauvegardes à gauche, seul le tableau défile ; suivi et GED à droite, 360 px).
Reprise de données : 0 px à toutes les étapes mesurées (filtres des contrôles resserrés). Profils de reprise (ajouté aux
écrans du super admin) : liste 0 px ; fenêtre « Nouveau profil » / « Modifier… » : 77 px de défilement du voile → 0, seule
la fenêtre défile dans son cadre (hauteur 88 vh, marges comprises).
Mesurés et corrigés le 2026-11-16 (1 536 × 890, titulaire, 0 px après correction, ni vertical ni horizontal) :
Contrôle interne › Journal fiscal (505 px en largeur : colonnes `minmax(0, 1fr) 380px` et tableaux de la colonne de droite
en `min-width: 0`), `/pilotage-financier` › Synthèse (248 px : six tuiles compactes sur une ligne, histogramme de 200 px),
Psychotropes › Alertes du registre (206 px : panneau « Wilayas de la zone » en ligne, liste des voisines de 128 px) et
› Suivi des antibiotiques (30 px : deux colonnes égales), Achats › Patients en attente et › Catalogues des grossistes
(défilement horizontal du tableau : file à droite élargie, colonnes égales), Stock › Modifier un produit (DCI et forme
tronquées : champs élargis). Fenêtre de modification de la fiche patient (157 px, boutons cachés) : fenêtre élargie
(`.fenetre-contact-large`, 1 000 px), tout tient à l'écran. `/admin/synchronisation` › État mesuré à 0 px.
`/admin/reprise` et `/admin/profils-reprise` (super admin) : non mesurés à 1 366 × 768 (mesurés à 1 536 × 890 le 2026-11-16, étape 2
exceptée, voir plus haut) ; le 2026-11-21, le mot de passe noté du compte d'essai `super.essai` est refusé : à demander au titulaire.

**Ajouté le 2026-11-16 (lot 2, à mesurer sur le poste)** : Administration › Contrôle interne › Intégrité des journaux
(`/admin/controle-interne?rubrique=integrite`) : titre et bouton « Vérifier maintenant » sur une ligne, un seul tableau en
cadre défilant (`.tableau-cadre`), page attendue à 0 px. **Mesuré le 2026-11-21** : 0 px (« Mesures du 2026-11-21 (après-midi) »).

**Ajouté le 2026-11-16 (lot 3, à mesurer sur le poste)** : Administration › Contrôle interne › Journal fiscal
(`/admin/controle-interne?rubrique=journal-fiscal`) : titre, période, ticket et boutons d’export sur une ligne ; une ligne d’état
(chaîne, compteur des tickets, conservation) ; à gauche le tableau des lignes en cadre défilant et paginé, à droite le récapitulatif
TVA et les sceaux de la période (cadre défilant). Page attendue à 0 px. **Mesuré et corrigé le 2026-11-16** (voir plus haut).

**Ajouté le 2026-11-16 (lot 6, à mesurer sur le poste)** : Achats › Commander (multi-grossistes)
(`/achats?onglet=commander`) : filtres sur une ligne, un seul tableau en cadre défilant (`.tableau-cadre`), pagination,
barre de répartition sur une ligne ; Achats › Catalogues des grossistes (`/achats?onglet=catalogues`) : profil et import
dans un panneau défilant à gauche, imports en tableau paginé à droite (`.achats-grille`). Page attendue à 0 px. **Mesurés le
2026-11-21** : Commander et Catalogues des grossistes, 0 px (« Mesures du 2026-11-21 (après-midi) »).

**Ajouté le 2026-11-16 (lot 7, à mesurer sur le poste)** : Achats › Ruptures chez les grossistes (`/achats?onglet=ruptures`) :
filtres sur une ligne, un seul tableau en cadre défilant, pagination ; Achats › Patients en attente
(`/achats?onglet=patients-attente`) et Achats › Substitution (`/achats?onglet=substitution`) : saisie et réglages dans un panneau
défilant à gauche, liste paginée et filtrée à droite (`.achats-grille`) ; bandeau « Produit redevenu disponible » de la Caisse
(`.info-bandeau`, une ligne). Page attendue à 0 px. **Mesurés le 2026-11-21** : Ruptures et Substitution, 0 px (« Mesures du
2026-11-21 (après-midi) ») ; Patients en attente mesurés avec le domaine Patients le 2026-11-18.
Patients en attente déplacés le 2026-11-18 dans le domaine Patients (`/patients/attente`, voir ci-dessous) ; l'ancienne adresse y renvoie.

**Domaine Patients mesuré le 2026-11-18** (poste lancé sur `pharmacie_test`, titulaire, thèmes sombre et clair, 1 536 × 890 et
1 366 × 768 ; défilement de la page 0 px en vertical et en horizontal, 0 px de défilement horizontal du tableau, captures regardées) :
`/patients` (Fichier des patients : six filtres sur une ligne `.filtres-liste` — à 1 366 px aussi, libellés courts « Non assuré »,
« Expirés ou bientôt expirés » —, un seul tableau en cadre défilant, 8 colonnes, téléphone sur une ligne, pagination en bas) et
`/patients/attente` (Patients en attente : même mise en page que l'ancien onglet d'Achats, saisie à gauche, file paginée à droite).

**Défauts à 1 366 × 768 corrigés et mesurés le 2026-11-18** (poste compilé à part sur `pharmacie_test`, titulaire, thèmes sombre et
clair, 1 366 × 768 et 1 536 × 890, captures regardées) :
- `/caisse` en tiers payant, produit et bandeau de lot (avant : 164 px) → 0 px. En-tête du bloc client sur une rangée
  (`.bloc-client-actions` : « Réduire », Vente libre / Tiers payant), consigne `.indice` seule sur la rangée suivante ; libellé du
  panier sur une ligne ; **repli automatique du bloc client** quand l'écran déborde encore, panier à sa hauteur minimale
  (`data-repli-auto` posé par la page, déclenché par `pharma.tableaux` dans `poste.js`, jamais curseur dans le bloc ; règle dans
  regles-metier.md « Caisse en trois parties ») ; colonne de paiement resserrée sous 800 px de haut (marges 14 px). Bloc rouvert
  (F6, F5, « Modifier ») : la page peut défiler, formulaire ouvert à la demande. 1 536 × 890 : bloc ouvert, 0 px.
- Propositions de la recherche de la caisse : sur toute la largeur de `.ligne-scan` (champ et boutons), 545 px au lieu de 152 px,
  plus de défilement horizontal (produits en rupture, remplaçants).
- `/surveillance` (toutes rubriques) : menu de gauche 37 px trop haut → 0 ; « Sorties et entrées du tiroir » et « Produits
  sensibles » rangés dans le groupe « Pertes et vigilance » (groupes « Caisse » et « Stock » d'une entrée supprimés).
- `/admin/emplacements`, fiche ouverte : Rayon, Étagère et Tiroir gardaient 213 px (largeur naturelle d'un champ texte), la ligne
  passait à la ligne et « Zone » tombait sous la liste ouverte des rayons ; classe **`.achats-ligne.champs-courts`** (champs à leur
  largeur prévue, `min-width: 0`) : les cinq champs tiennent sur une ligne aux deux tailles.
- `/surveillance/risques` (Par employé) : défilait de 33 px à 1 366 × 768, tableau de 11 colonnes avec barre horizontale → 0 px,
  sans barre horizontale : titre du panneau pour les lecteurs d'écran seulement (`.visuellement-cache`, le titre de la page et la
  période le disent déjà), tableau **`.tableau.tableau-serre`** (cellules 10 × 8 px, à réserver aux tableaux d'une dizaine de
  colonnes) avec `min-width: 0`, cadre `data-hauteur-min="200"`. Onglet Jour par jour (49 px → 0) : même titre caché et même cadre de 200 px.
- N° de tickets après une restauration (2026-11-19, scripts 152 et central 025) : **Caisse**, bandeau « Contrôle de démarrage avec le
  serveur central : encaissement possible dans 1 min 58 au plus » (`bandeau-attention`, `data-champ="attente_encaissement"`, relu
  toutes les 5 s, retiré à 0) ; **Synchronisation › Réglages**, champ « Attente au démarrage (s) » sur la première rangée (explication
  dans l'infobulle) ; **Restaurations constatées**, colonne « Tickets réutilisés » (« 2 : T-017618, T-017619 », 5 n° au plus, liste
  complète dans l'infobulle). Mesurés sur l'essai de bout en bout : 0 px, sans défilement horizontal, à 1 536 × 890 et 1 366 × 768,
  clair et sombre, captures regardées (Caisse panier vide). Réglages : 119 px puis 79 px puis 19 px avant correction (champ seul sur
  une rangée, puis sur la rangée des cases, puis phrase ajoutée sous les réglages retirée).
- Administration › Synchronisation (2026-11-19, lot S6.5.4) : nouvelle rubrique **« Restaurations constatées »**
  (`?rubrique=restaurations` : tableau borné par nature, prise d'acte du titulaire au-dessous, champ « cause » avec `data-champ`) ; la
  rubrique « Intégrité au central » porte un lien « n restauration(s) » dans le titre de son panneau, ses champs sont resserrés sur une
  rangée (`.champs-courts`) et le tableau des anomalies descend à 120 px au moins (`data-hauteur-min`). Mesurées avec un résumé de
  démonstration (copie jetable, supprimée) : 0 px, sans défilement horizontal, à 1 536 × 890 et 1 366 × 768, clair et sombre, captures
  regardées (avant correction : 187 et 428 px avec les trois panneaux empilés).
- Administration › Synchronisation (2026-11-19, essai S6.5.5) : le message de réussite d'une rubrique est effacé au changement de
  rubrique (après une prise d'acte, il faisait dépasser « Intégrité au central » de 52 px à 1 366 × 768) ; rubriques mesurées avec de
  vraies données d'essai (restauration constatée, puis « Aucune anomalie ») : 0 px aux deux tailles, clair et sombre.
- Caisse : un champ ne bouge plus pendant la frappe. Les listes de propositions ouvertes sont écartées du calcul des cadres
  (`pharma.tableaux`, `poste.js`) ; « Nouveau médecin » est à droite du libellé, comme « Nouveau patient » (plus de lien qui
  apparaît sous le champ). Règle : **rien n'apparaît ni ne disparaît dans le flux d'un formulaire pendant la frappe** ; ce qui dépend
  du texte tapé va dans la liste de propositions (flottante) ou sur la ligne du libellé.

**Mutualisation des réglages mesurée le 2026-11-16 (lot M7 ; poste lancé sur deux copies jetables de la base de démonstration, titulaire, thèmes clair et sombre, 1 536 × 890 et 1 366 × 768 ; états « fixé » et « proposé » des neuf écrans, puis états mélangés ; 0 px de défilement de la page, aucun élément coupé ni chevauché, dans les 133 vues mesurées)** : écrans ajoutés `/admin/reglage-fiscal` et `/admin/reglage-tracabilite` (un panneau, aucun tableau, bouton Enregistrer à côté du titre) ; Administration › Synchronisation › Réglages mutualisés (`/admin/synchronisation?rubrique=reglages-mutualises` : tableau de 11 lignes dans son cadre, liste fermée). **Bandeau « serveur central »** (composant `ReglageCentral`, `.info-bandeau.compact.bandeau-central`, une ligne : texte à gauche, « Comparer » et « Adopter » en `.lien-bouton` à droite) sous le titre de Alertes et seuils (rubriques Péremption et Stock seulement), Motifs, Remises automatiques, Contrôle interne, Substitution, Inventaire › Seuils, Psychotropes › Alertes, Notifications › Formulaire SMS et Sérialisation ; un panneau fixé est un `fieldset.panneau` désactivé (champs grisés, même aspect). Fenêtre « Comparer » : `.voile` + `.fenetre-facture`, tableau filtré et paginé. Pour tenir avec le bandeau : un cadre défilant peut déclarer `data-hauteur-min` (minimum de hauteur posé par `poste.js` ; 240 px par défaut, inchangé) — Psychotropes (120 px : à 1 366 × 768 le tableau des règles ne montre qu'une ligne et défile) ; Contrôle interne place ses trois panneaux dans un `.cadre-defilant` ; Psychotropes garde son bouton à côté du titre (`.titre-actions-cote`) ; le texte du formulaire SMS passe de 12 à 8 lignes.

**Ajouté le 2026-11-16 (lot 8, à mesurer sur le poste)** : Administration › Psychotropes et antibiotiques
(`/admin/psychotropes`, rubriques « Alertes du registre » : un tableau de 4 règles ; « Suivi des antibiotiques » : deux
panneaux côte à côte, chaque tableau en cadre défilant et paginé). Registre : nouvelle rubrique « Alertes forcées »
(`/stock/registre?rubrique=forcages`, liste paginée et filtrée par le serveur) ; ligne des filtres des inscriptions avec
« Prescripteur (export) » et trois boutons PDF, Excel, CSV (à vérifier : tient-elle sur une ligne ?). Caisse : fenêtre
« Alerte du registre des psychotropes » (voile). Alertes du registre et Suivi des antibiotiques **mesurés et corrigés le 2026-11-16**
(voir plus haut) ; ligne des filtres du registre et fenêtre de la Caisse : à vérifier.

### Écran trop chargé : menu vertical de rubriques

Un écran qui empile trop de panneaux est découpé en **rubriques**, avec le menu vertical de l'Administration (même rendu :
liens à gauche, rubrique active surlignée avec le filet ambre).

**Le menu de gauche liste les écrans du domaine** (composant `NavDomaine`, docs/navigation.md) : titre du domaine (gris,
majuscules), puis ses écrans ; l'écran affiché est écrit en vert, ses rubriques en retrait dessous (13 px), la rubrique affichée
avec le bandeau ambre. Un écran garde ses rubriques et son résumé (`nav-resume`) en les passant à `NavDomaine` ; il ne liste
jamais lui-même les autres écrans. Accueil : tuiles cliquables (`.tuile.tuile-lien` : toute la carte ouvre l'écran, bordure verte au
survol, seul « … → » souligné). Même règle pour les écrans du central : toutes leurs tuiles sont des liens `.tuile.tuile-lien`
(bordure verte au survol, contour de focus au clavier, infobulle qui dit où la tuile mène ; ecrans-central.md § 3 septies).

**Tuiles navigables (règle du 2026-11-21, tout le poste)** : toute tuile est navigable, vers son écran de détail ou l'écran le
plus proche ; infobulle (`title`) qui dit où elle mène ; non cliquable seulement sans le droit d'ouvrir la cible. Trois écritures,
même rendu que l'accueil (bordure verte au survol et au focus clavier, clair et sombre, aucun style en ligne) :
- composant `TuileLien` (`Lien`, `Infobulle`, `Explication`, `Classe`, `DroitsRubrique`) : rend `a.tuile.tuile-lien` si
  `AccesEcrans.PeutOuvrir` (même contrôle que le menu et la garde de la mise en page : droits de l'écran, écrans réservés,
  écrans du super admin) et, s'il y a lieu, l'un des droits de la rubrique visée ; sinon un `div.tuile` (chiffre seul, pas de lien
  mort). Rubrique ou filtre passés dans l'adresse quand l'écran cible en accepte (`?rubrique=`, `?onglet=`) ;
- lien vers une rubrique de l'écran affiché (ex. Alertes « Ruptures » → `./?rubrique=stock`) : le filtre est appliqué ;
- filtre d'un tableau du même écran, sans adresse : `button.tuile.tuile-lien` (`type="button"`, `title`, `aria-pressed` ; filtre
  appliqué : bordure verte). Ex. résultat d'inventaire, compte du titulaire, fiche d'un prêt, stock dormant.
Test d'architecture `TuilesNavigablesTests` : une tuile `class="tuile"` qui n'est ni l'un ni l'autre fait échouer la construction.

Compléments (2026-11-13) : **compteurs** du travail en attente dans le menu du haut (fond blanc, chiffre vert ; rouge si urgent)
et à gauche (`.compteur.neutre`, ou `.compteur` si urgent) ; le menu du haut reste sur une ligne (`nowrap`, 8 px de marge par
domaine). **Étape suivante** sous une liste vide : un seul bouton principal « … → » (`.action-suivante`), centré dans une cellule
de tableau. **Produit ou lot cliquable** : rendu des noms de contact (souligné en pointillé). **Aide** : rond « ? » de 30 px à
côté du titre, panneau de 380 px à droite (titre vert 17 px, liste courte, raccourcis en gris, « Guide complet » et « Fermer »).

**Le menu est collé au bord gauche de la fenêtre** (demandé le 2026-11-13) : colonne de 240 px sur toute la hauteur, de l'en-tête
à la barre d'état, fond `--menu-fond` et filet `--menu-filet` à droite, élément actif `--menu-actif` ; la page n'est plus centrée ni limitée à 1 280 px, le contenu
prend toute la largeur restante (32 px d'écart avec la colonne). Tout vient de `.page.admin` et `.page.avec-menu` dans `app.css` :
aucun écran n'a de style propre pour cela. Les écrans sans menu vertical restent centrés (1 280 px au plus).

**Bandeau ambre : toujours sur l'écran ou la sous-rubrique affichés, et sur eux seuls.** Dans l'Administration, l'écran affiché qui a
des sous-rubriques (Alertes et seuils, Utilisateurs et droits…) devient un **titre de groupe non cliquable** (`.nav-groupe-titre`,
gris, majuscules), comme « COMMANDES » ou « AUTRES ÉCRANS DU STOCK » dans les autres menus ; sa sous-rubrique affichée porte le
bandeau. Les autres écrans de l'Administration restent des liens. Utilisé par l'Administration (`.admin`, `.admin-nav`,
`.admin-contenu`, composant `NavAdmin`), la Trésorerie, Dettes et créances, Stock, Alertes, Registre, Inventaire, Achats, Rapports,
Contrôle interne (composant `NavControleInterne`, commun aux anomalies et aux pertes et vigilance), le Guide et la Caisse (composant
`NavCaisse`, commun à la vente, à l'annulation ou au retour, au règlement d'un crédit, à l'état de la caisse, à la relève et à la clôture ;
une action qui n'est pas un écran, comme ouvrir le tiroir, demande une confirmation dans le menu) (classes génériques ci-dessous).

```html
<div class="page avec-menu">
    <nav class="nav-laterale" aria-label="Rubriques de …">
        <a href="ecran?rubrique=…" class="active" aria-current="page">Rubrique <span class="compteur">2</span></a>
        …
        <div class="nav-resume"><span class="detail">Solde du coffre</span><strong>…</strong></div>
    </nav>
    <div class="contenu-rubrique">
        <div class="titre-page"><div><h1>Écran · Rubrique</h1><p class="sous-titre">…</p></div></div>
        <section class="panneau">…</section>
    </div>
</div>
```

- La rubrique est **dans l'adresse** (`?rubrique=…`) : « Retour » revient à la rubrique précédente (voir `navigation.md`).
- `.compteur` (rouge) à côté d'une rubrique : nombre de choses qui attendent ou sont en alerte (caisses à verser, chèques à remettre,
  alertes), absent à zéro. `.compteur.neutre` (gris) : simple nombre de lignes (patients, grossistes…), affiché même à zéro.
- `.nav-groupe-titre` : intitulé de groupe dans le menu (« À recevoir », « À payer »), en petites capitales grises ; les rubriques
  du groupe sont en retrait (`class="sous-rubrique"`).
- `.nav-resume` sous le menu : un seul chiffre utile partout dans l'écran (ex. solde du coffre), facultatif.
- Le titre reprend l'écran et la rubrique (« Trésorerie · Mouvements ») ; le sous-titre dit à quoi sert la rubrique.
- **Administration : menu à deux niveaux.** Sous l'écran affiché, ses sous-rubriques (`admin/…?rubrique=…`, déclarées dans
  `NavAdmin`, en retrait et plus petites) ; une seule partie à la fois. Le titre devient « Écran · Sous-rubrique ». Un formulaire
  d'ajout ou de modification trop long pour tenir sous sa liste a sa propre sous-rubrique (ex. Notifications › Ajouter ou modifier).
- **La rubrique est lue dès l'ouverture de l'écran** (dans `OnInitialized`, avant tout chargement) : le titre affiché pendant le
  chargement est déjà le bon, il ne change pas sous les yeux.
- **Le lien affiché reste visible dans le menu** : `NavAdmin` fait défiler le menu de gauche jusqu'à lui (au plus près, sans
  faire défiler l'écran) à l'ouverture, au changement de sous-rubrique et au redimensionnement de la fenêtre (`pharma.voirLienActif`).
- Réglages nombreux (seuils) : groupés par thème en colonnes, une ligne par réglage, libellé à gauche et petite valeur à droite
  (`.reglages-groupes`, `.reglage-ligne`), pour tenir sans défilement.
- Valeurs en lecture seule sur une ligne (état d'un service) : `.achats-ligne.ligne-lecture` (étiquettes alignées en haut),
  étiquette `.etiquette-champ`, pastille ajustée à son texte (`.champ > .pastille`, jamais étirée). Titre long avec boutons :
  `.titre-page.titre-actions-cote` garde les boutons à droite, le sous-titre passe à la ligne.

## Hiérarchie du texte

| Rôle | Balise ou classe | Taille | Couleur |
| --- | --- | --- | --- |
| Titre de page | `h1` | 26 px, gras 600 | accent (vert) |
| Sous-titre de page | `.sous-titre` | 14 px | texte secondaire (gris) |
| Titre de panneau | `h2` | 17 px, gras 600 | texte |
| Sous-titre de section, dans un panneau | `h3`, `.sous-titre-panneau` | 15 px, gras 600 | texte |
| Information principale | texte courant, cellules de tableau | 15 px | texte |
| Information secondaire explicative | `.detail` | 13 px | texte secondaire (gris) |
| Note de bas d'écran | `.pied-page` | 13 px, filet au-dessus | texte secondaire (gris) |
| Étiquette de champ | `label` dans `.champ` ; `.etiquette-champ` pour une valeur en lecture seule | 13 px | texte secondaire (gris) |
| En-tête de tableau | `th` dans `.tableau thead` | 12 px, majuscules, gras 600 | texte 3, bandeau `--surface-2` souligné de 2 px |
| Liste vide | `.vide` | 15 px | texte secondaire (gris) |

Une seule police : Poppins. L'information principale n'est jamais en gris ; une explication, une précision ou une aide à la
saisie n'est jamais en blanc ni plus grosse que le texte qu'elle accompagne.

## Fonds

| Élément | Variable | Remarque |
| --- | --- | --- |
| Page | `--fond` | le même sur tous les écrans |
| Panneau, cadre de tableau | `--surface`, bordure `--bordure`, coins 12 px, marge intérieure 20 px | classe `.panneau`, `.tableau-cadre` |
| En-tête de tableau | `--surface-2` | |
| Bandeau d'information | `.info-bandeau` | information à lire avant d'agir |
| Bandeau d'attention | `.bandeau-attention` | mise en garde |
| Erreur, réussite | `.erreur`, `.succes` | résultat d'une action |

Thème clair adouci (2026-11-15) : jamais de blanc pur pour les grandes surfaces, qui fatiguait l'œil. Page `--fond` #EEE9E1 (beige),
panneaux et champs `--surface` #F7F4EE (blanc cassé chaud), en-têtes de tableau `--surface-2` #F0EBE3, bordures `--bordure` #E0D8CB.
Colonne du menu vertical (2026-11-18, choix du titulaire « grès » : le blanc cassé fatiguait l'œil) : `--menu-fond` #E3DBCE, élément
actif `--menu-actif` #CDE4E0 (avec la barre ambre), filet `--menu-filet` #D2C7B6 ; en thème sombre ces variables reprennent
`--surface`, `--accent-clair` et `--bordure` (inchangé).
Seuls le logo, les images (ordonnances) et les pastilles de l'en-tête vert restent en blanc. Le thème sombre ne change pas.

Les couleurs passent toujours par les variables (`var(--…)`) : c'est ce qui fait fonctionner les thèmes clair et sombre.

**Vert de l'interface = vert du logo Tracodex** (décision du titulaire, 2026-11-18 ; avant : vert #0E5A32) :

| Variable | Clair | Sombre | Usage |
|---|---|---|---|
| `--accent` | #0F766E | #0F766E | fond des boutons principaux, en-tête, « À payer » (texte blanc : contraste 5,47) |
| `--accent-fonce` | #0B5D57 | #99F0E0 | survol des boutons ; texte des messages de réussite |
| `--accent-clair` | #E3F3F0 | #123A36 | fonds légers (réussite, survol des menus, élément actif en sombre) |
| `--accent-trait` | #99D3CB | #1F5F59 | bordures vertes |
| `--accent-texte` | #0C655E | #2DD4BF | liens, titres et texte vert (contraste ≥ 5 sur le menu « grès » en clair, 8,7 en sombre) |
| `--menu-actif` | #CDE4E0 | (`--accent-clair`) | élément actif du menu vertical |
| `--graphique` | #0D9488 | #14B8A6 | barres des graphiques |

**Bronze : le rendu de monnaie** (demande du titulaire, 2026-11-18) : `--bronze` #8A5A24 / #E3A869 (sombre), `--bronze-fond`
#F5EADB / #2E2317, `--bronze-trait` #C99A64 / #7A5530. Caisse : dès qu'il y a de la monnaie à rendre, le montant (`.rendu-caisse.a-rendre`,
30 px, cadre de 2 px) et son libellé « Rendu » passent en bronze ; après l'encaissement, « Rendu : … » est rappelé en bronze, 22 px
(`.rendu-vente`). Contrastes : 4,95 en clair, 7,4 en sombre. Le bronze est réservé au rendu.

Le texte vert est un peu plus foncé que le logo : #0F766E ne fait que 3,99 sur le menu « grès ». L'**ambre reste #E08A1E**
(#F0A040 en sombre) : l'ambre du logo (#F59E0B) ne ferait que 2,55 sous l'en-tête vert, contre 3,1. Mêmes valeurs dans l'application
mobile (`mobile.css`, `theme-color`, manifeste), le titre des PDF (#0C655E), le titre des e-mails de notification (#0C655E) ; écran du
client sur fond « encre » #102A33 (texte #CCEEEA). Fonds neutres du thème clair (beige, « grès ») inchangés.
**Thème sombre sur l'« encre » du logo** (2026-11-18 ; avant : gris-vert #121915…) : `--fond` #0D1A20, `--surface` #132329,
`--surface-2` #182B32, `--bordure` #29414A, `--bordure-2` #1F343C, `--bordure-champ` #45616B, `--texte` #E4EEF0, `--texte-2`
#AFC3C8, `--texte-3` #C8D7DA, `--info-fond` #21363E, `--texte-inactif` #7C9097 (mêmes valeurs dans `mobile.css`) ; contrastes
égaux ou meilleurs qu'avant (texte secondaire 8,8, texte vert 8,7, texte inactif 4,8 sur `--surface`). Vérifié le
2026-11-18 à 1 536 × 890, clair et sombre, captures avant et après (Accueil, Caisse, Stock, Achats, Emplacements, Contrôle interne :
0 px de défilement).

## Contrôles

| Contrôle | Classe | Hauteur |
| --- | --- | --- |
| Bouton d'action | `.btn` | 44 px, coins 8 px |
| Action principale de l'écran | `.btn .btn-principal` | 44 px |
| Action irréversible | `.btn .btn-danger` | 44 px |
| Filtre, onglet | `.puce` | 44 px, arrondi complet |
| Action dans une ligne ou une phrase | `.lien-bouton`, `.lien` | texte seul |
| Champ, liste déroulante, date | sans classe, dans `.champ` | 44 px, coins 8 px, texte 15 px |
| Champ dans une cellule de tableau | sans classe | 36 px, coins 6 px |
| Pied de liste | composant `Pagination` | boutons et liste de 36 px |

Seule exception de taille : « Encaisser » à la caisse (`.btn-large`, 52 px).

## Listes et fenêtres

- **Règle des listes, valable sur tous les écrans** (demandée par le titulaire du projet le 2026-11-13) : tout tableau dont le
  nombre de lignes peut grandir avec l'activité (ventes, patients, produits, lots, factures, journaux, historiques…) est
  1. **paginé** avec le composant `Pagination` (10 lignes par défaut, taille réglable en bas, toujours visible) : le nombre
     total de lignes est toujours affiché, et les totaux sont calculés par le serveur sur toutes les pages ;
  2. **filtré** par des filtres au-dessus du tableau (`.filtres-liste`, voir plus bas), appliqués par le serveur ;
  3. **retenu par « Retour »** : filtres et page (`Historique.Suivre` / `Historique.Restaurer`) ;
  4. **défilant dans son cadre** : c'est le tableau qui défile, jamais l'écran. La hauteur du cadre (`.tableau-cadre`) est
     ajustée à la place qui reste dans la fenêtre (`pharma.tableaux` dans `wwwroot/js/poste.js`, automatique) et la ligne
     d'en-tête reste visible pendant le défilement.

  Seuls échappent à la pagination les tableaux **bornés par nature**, dont la taille ne dépend pas de l'activité : contenu
  d'un document (lignes d'une facture, d'un bon, d'un retour), réglages (droits par rôle), rayons, synthèses (échéancier,
  tableau par jour d'un rapport), résultats d'une recherche plafonnée (médecins). Exceptions choisies par le pharmacien
  le 2026-11-13 : **utilisateurs** (court en pratique) ; organismes payeurs et emplacements sont paginés et filtrés
  par le serveur depuis le 2026-11-15 (décision du titulaire). Le test d'architecture
  `ListesTests` (`tests/Pharma.Architecture.Tests/ListesTests.cs`) liste ces exceptions avec leur raison et refuse tout
  nouveau tableau qui n'y est pas et n'est pas paginé.
- **Pagination par le serveur ou à l'écran.** Une liste que le serveur plafonnait (« les 20 derniers ») est paginée par le
  serveur, sans plafond (journal des sauvegardes, historique des inventaires, ventes de la caisse, patients en crédit). Une
  liste que le serveur renvoie déjà complète et de taille modérée est filtrée et paginée à l'écran (produits bloqués, seuils
  saisis, grossistes des seuils, produits à vérifier, écarts et feuille d'inventaire, balance du registre, livraisons
  comptées, sauvegardes à restaurer, contrôles de reprise) ; le filtre de texte ignore majuscules et accents
  (`RechercheTexte` côté poste, seule fonction de comparaison des écrans, `TexteRecherche` côté serveur). Le total vient de la
  même requête que les lignes (`count(*) OVER ()`, `PaginationSql`). Une liste n'est lue que lorsque sa rubrique est affichée.
- **La page reste valide.** Quand la liste raccourcit (ligne traitée, filtre, données changées avant « ← Retour »), le pied de
  liste ramène la page à la dernière qui existe : jamais de tableau vide avec « Page 2 sur 1 ». Changer de produit, de période
  ou de session (registre, reprise) repart de la première page et, pour la reprise, sans filtres ; une relecture de la même
  feuille (comptage d'un lot, scan) garde filtres et page. Un filtre ne porte jamais sur la valeur en cours de saisie dans la
  même ligne (ex. « délai de reprise à renseigner » : valeur enregistrée), pour que la ligne ne disparaisse pas sous le curseur.
- **Règle des listes déroulantes, valable sur tous les écrans** (filtres comme saisies ; demandée le 2026-11-13) :
  - **valeurs peu nombreuses et bornées** (employé, poste, grossiste, organisme, banque, statut, type, rayon, motif…) : une
    **liste préremplie**, jamais une saisie libre ; valeurs prises dans les données (par exemple les employés présents dans le
    journal ou sur la période) ;
  - **valeurs sans fin** (patient, entreprise cliente, produit, médecin, lot, n° de facture ou de BL…) : **jamais une liste de
    toutes les valeurs**, mais un **champ de recherche avec propositions** : propositions dès 2 lettres (dès la 1re pour un
    produit), 10 au plus, sans tenir compte des majuscules ni des accents, flèches haut et bas, Entrée pour choisir, Échap pour
    fermer, message « Aucun … pour « saisie » » quand rien ne répond ; le choix fait s'affiche avec un lien « Changer ». Dans
    un filtre de tableau, le tableau suit la saisie (filtre sur une partie du nom) avant même qu'une proposition soit choisie.
  - **Composant commun (2026-11-18) : `ChampSuggestions`** (`src/Pharma.Desktop/Composants/Mise/ChampSuggestions.razor`), à utiliser pour
    tout nouveau champ avec propositions ; ne pas recopier de liste à la main. Règles qu'il applique : propositions dès `LettresMin`
    lettres (2 ; 1 pour un produit ; 0 en valeur libre seulement, pour un champ à valeurs très courtes dont toutes les valeurs sont
    proposées dès l'entrée dans le champ : rayon d'un emplacement), 250 ms après la dernière touche, réponses périmées ignorées, **10 au plus**, comparaison sans
    accents ni majuscules, partie tapée surlignée (`mark`), flèches haut et bas **circulaires**, Entrée choisit, Échap ferme la liste
    **sans vider le champ** (un second Échap efface la recherche, `poste.js`), message « Aucun … pour « … » », accessibilité combobox
    (`role="combobox"`, `aria-expanded`, `aria-controls`, `aria-activedescendant`, `role="listbox"` / `option`), liste de classe
    **`.suggestions`** (obligatoire : `poste.js` s'en sert pour Échap et pour le calcul des cadres). La liste ne sort jamais du bas
    de la fenêtre ni du cadre qui défile autour du champ : raccourcie, ou ouverte vers le haut s'il reste moins de 160 px dessous
    (`poste.js`). Le composant ne prend jamais le curseur de lui-même (seul `poste.js` met le curseur dans la première recherche de
    l'écran) ; en valeur libre le champ est de type `text`, jamais pris pour la recherche de l'écran.
    - **Deux modes** : `ModeSuggestions.ChoixObligatoire` (une entité ; `Choix` = libellé choisi, affiché avec « Changer » qui
      appelle `OnEfface` ; `ChangerPermis="false"` pour une entité imposée) et `ModeSuggestions.ValeurLibre` (`@bind-Valeur`, valeurs
      déjà saisies, le texte tapé reste accepté, rien de présélectionné, pas de message quand rien ne répond).
    - **Paramètres** : `Id`, `Rechercher` (`Func<string, Task<(List<T>, string?)>>`, signature des méthodes d'`ApiClient`), `TexteDe`,
      `DetailDe` (seconde ligne), `Complement` (gabarit à droite : montant, pastille), `Mode`, `LettresMin`, `LibelleAucun`,
      `ComplementAucun`, `LibelleListe`, `Libelle`, `Requis`, `Invalide` (aria-invalid), `Style`, `Cadre` (faux : seulement le champ
      et sa liste dans un conteneur `.recherche` du parent), `Type`, `TexteInitial`, `OnChoisi`, `OnTexte`, `OnEfface`, `OnFocus` ;
      tout autre attribut (`data-champ`, `data-raccourci`, `aria-label`, `placeholder`, `class`, `maxlength`, `disabled`) est posé
      tel quel sur le champ (le test `ChampsObligatoiresTests` lit les `data-champ` écrits dans l'écran).
    - **Dérivés** : `ChoixPatient` et `ChoixMedecin` (choix obligatoire, 10 propositions, sans accents) ; `ValeursSaisies`
      (valeur libre, `Champ` = clé de la liste blanche du serveur, `GET /api/suggestions/{champ}?q=`, docs/regles-metier.md
      « Suggestions pendant la frappe » ; `ValeursDeDepart` facultatif : valeurs usuelles proposées après celles de la base, sans
      doublon, quand elles contiennent la saisie, ex. les formes d'un produit). Exemple :
      `<ChoixPatient Id="ti-patient" Libelle="Patient remboursé" Requis="true" data-champ="patient" Choix="@(p is null ? null : ChoixPatient.Nom(p))" OnChoisi="x => p = x" OnEfface="() => p = null" />`
      ou `<ValeursSaisies Id="nm-specialite" Champ="specialite_medecin" @bind-Valeur="fiche.Specialite" aria-label="Spécialité" maxlength="120" />`.
    - **Mise en avant** (revue 2026-11-18) : une proposition mise en avant par les flèches ou la souris reste en avant à l'arrivée
      d'une nouvelle réponse si elle y figure encore (même élément, ou même texte et seconde ligne), sinon rien n'est en avant
      (jamais un autre élément) ; chaque frappe et la fermeture du composant (`IDisposable`) annulent l'attente en cours ; le
      nombre de propositions est annoncé aux lecteurs d'écran (`aria-live="polite"`, classe `.visuellement-cache`).
    - `ChoixProduit` reste un composant à part (16 usages) ; paramètre **`PremierSurEntree`** (vrai par défaut) : Entrée pendant
      une recherche en attente prend le premier résultat (douchette) ; **`PremierSurEntree="false"` partout où ce n'est pas un
      scan de comptoir ou de réception** (Entrée affiche la liste à jour sans rien choisir : jamais un produit non vu) ;
      **`PremierSurEntreeSiCode="true"`** sur un écran où l'on tape et où l'on scanne (Achats › Commandes, 2026-11-18) : Entrée
      prend le premier résultat seulement si le texte ressemble à un scan (8 à 14 chiffres, ou Data Matrix GS1 « 01 » + 14 chiffres). Le
      panier de la caisse garde sa propre liste (scan, volet Chifa) avec les flèches, `role="none"` sur ses `<li>`.
    - **Filtre « Produit » d'un tableau : `FiltreProduit`** (2026-11-18, `src/Pharma.Desktop/Composants/Mise/FiltreProduit.razor`) :
      valeur libre (`@bind-Valeur`), champ de type `search` (il reste la recherche de l'écran), propositions du catalogue dès la
      1re lettre ; le tableau suit chaque touche, choisir une proposition met le nom commercial dans le filtre. Propositions selon
      le droit (`stock.consulter` : `api/produits`, sinon `caisse.vendre` : `api/caisse/produits`, sinon aucune). **Pas de
      propositions** pour un filtre qui porte aussi sur un n° de lot, un code-barres ou un laboratoire : saisie libre telle quelle.
  - Exemples : choix du produit (composant `ChoixProduit`), du patient et du médecin à la caisse, du patient et de l'entreprise dans
    l'éditeur de facture, filtre « Tiers » de Dettes et créances (recherche pour patients, factures clients et organismes des
    bordereaux, demandée le 2026-11-13 ; liste pour grossistes et effets).
- **Tout tableau paginé a des filtres au-dessus** (`.filtres-liste` : grossiste ou tiers, statut ou type, période « Du » / « Au »,
  recherche selon le tableau), appliqués par le serveur pour que le total et les pages les suivent, avec un bouton
  « Tout afficher » dès qu'un filtre est actif et un message « Aucun … pour ces filtres » quand rien ne répond.
  Seul écart : Seuils par produit, filtres dans la colonne de gauche (demande du titulaire du 2026-11-20, voir « Seuils par produit revu »).
- **Retour à l'écran précédent** (`docs/navigation.md`) : tout écran avec des filtres ou une pagination déclare son état à
  l'historique (`Historique.Suivre` et `Historique.Restaurer`) ; tout nouvel onglet passe par l'adresse (`?onglet=…`).
- **Bouton mis en évidence au survol et au clavier** (`.btn`, `.puce`, automatique) : bord et texte verts, fond vert clair et
  halo ; bouton principal plus foncé ; bouton rouge plus foncé avec halo rouge. Aucun effet sur un bouton inactif, jamais
  d'agrandissement.
- **Texte cliquable mis en évidence** (liens, `.lien`, `.lien-bouton`, nom de contact) : vert, gras et souligné au repos ; au survol ou au clavier, fond vert clair arrondi et soulignement plein ; inactif : gris sans soulignement. L’en-tête vert garde son texte blanc.
- **Relief des tableaux** (automatique avec `.tableau`, rien à ajouter dans un écran) : bandeau d'en-tête souligné, trait
  vertical léger entre les colonnes, une ligne sur deux teintée, ligne survolée mise en avant (contour vert et ombre ; jamais d'agrandissement, qui ferait apparaître une barre de défilement dans le cadre du tableau), pied de total séparé par un
  trait de 2 px. Valable en thème clair comme en sombre.
- **La taille de page se règle en bas du tableau**, toujours visible même pour une liste courte ou vide : 5, 10, 20, 30, 50,
  100 ou 200 lignes, 10 par défaut (`Pagination.TailleParDefaut`, `DemandePage.TailleParDefaut`).
- Un relevé propre à un tiers (dette d'un patient, dette envers un grossiste) permet de changer de tiers depuis ses filtres,
  sans quitter le relevé ; la période et le type choisis sont gardés.
- Le détail d'un élément d'une liste s'ouvre dans une fenêtre au-dessus de la liste (`.voile` et un panneau), fermée par
  « Fermer », la touche Échap, ou un clic à côté quand aucune saisie n'est en cours.

## Erreurs de saisie

- Un champ obligatoire vide ou invalide passe en rouge (`Obligatoire.Classe` et `aria-invalid="true"`), et le message d'erreur
  s'affiche dans un bloc `.erreur` (`role="alert"`).
- L'erreur est toujours mise sous les yeux, sur tous les écrans, par `wwwroot/js/erreurs.js` : après un clic, l'écran défile
  jusqu'au premier champ en rouge, y place le curseur et affiche le message dans une bulle à côté ; sans champ en rouge, il
  défile jusqu'au message, qui clignote. Un écran n'a rien à ajouter pour en profiter : il lui suffit de marquer ses champs
  avec `aria-invalid` et d'afficher ses messages avec la classe `.erreur`.
- **Bulle seulement après une tentative (2026-11-20)** : `aria-invalid="true"` déclenche la bulle **et place le curseur** ; il ne
  se pose donc qu'après une tentative de validation (clic sur le bouton, F12 à la caisse : drapeau `controle` / `alerter…`), ou sur
  une saisie déjà fausse (caractère interdit, confirmation complète différente), jamais à l'apparition d'un champ ni pendant la
  frappe. Le champ obligatoire vide est en rouge tout de suite (`Obligatoire.Classe`), sans bulle. Un champ dont le bouton reste
  grisé tant qu'il est vide n'a pas de bulle du tout. Le curseur dans un bloc empêche aussi son repli automatique (caisse à
  1 366 × 768).
- **Panneau flottant ouvert depuis le menu de gauche** (aide de la caisse) : la colonne du menu passe au-dessus du contenu
  (`app.css`, `:has(.panneau-aide)`), comme pour une fenêtre `.voile` ; sinon le contenu le recouvre.
- **Erreurs du serveur** : une règle qui vise un champ le désigne (`throw new RegleMetierException(message, "montant")`) ; la réponse
  d'erreur porte ce nom (`ErreurDto.Champ`) et le poste met en rouge le champ qui porte `data-champ="montant"` (dans la fenêtre
  ouverte au premier plan, sinon dans la page). Un contrôle fait sur le poste sans aller au serveur passe par le même chemin :
  `erreur = Api.SignalerChamp("montant", "…")`. Noms en minuscules avec soulignés (`montant`, `email`, `motif`, `nom_patient`…).
- **Tout champ obligatoire** (libellé `requis`) porte `aria-invalid` (contrôle sur le poste) ou `data-champ` (désigné par le
  serveur) ; et tout nom de champ utilisé par le serveur existe en `data-champ` sur un écran. Les deux règles sont vérifiées par
  `tests/Pharma.Architecture.Tests/ChampsObligatoiresTests.cs`.
- **Une saisie refusée ne vide jamais les champs (2026-11-21, demande du titulaire)** : après un refus (« Enregistrer », e-mail
  d'essai, contact, ordre, message…), l'écran revient avec ce qui avait été saisi ; seuls le **mot de passe** et le **code de
  confirmation** sont à ressaisir. Poste : les formulaires Blazor gardent leurs valeurs (ne pas les remettre à zéro sur une erreur).
  Écrans du central (formulaires envoyés puis page rechargée) : `wwwroot/js/ecrans.js` garde les valeurs du formulaire envoyé pour
  l'onglet (`sessionStorage`, jamais dans l'adresse, jamais au serveur ni au journal) et les remet si la page revient avec
  `?erreur=` ; `?ok=` les efface. Jamais gardés : champs `password`, cachés, `code`, `mot_de_passe`, `confirmation`, jeton
  anti-falsification.

## Chargement

- **Écran, panneau ou fenêtre dont les données arrivent** : composant `<Chargement />` (roue qui tourne et texte, par défaut
  « Chargement… » ; `Texte="Calcul du rapport…"` pour préciser). Jamais un simple texte « Chargement… ».
  Il n'apparaît qu'au bout de 0,4 s : un écran qui charge vite passe directement à son contenu, sans éclair du message
  (changement d'écran sans clignotement).
- **Pas de clignotement au changement d'écran** :
  - message « Aucun… » (`.vide`) invisible pendant ses 0,3 premières secondes : si les données arrivent avant, il n'apparaît jamais ;
  - une partie qui se remplit en plusieurs lectures reste cachée (`visibility: hidden`, la place est gardée) jusqu'à la première
    lecture complète, puis apparaît d'un coup (ex. Achats › Bons de commande) ;
  - la barre fine du haut, une fois affichée, reste au moins 0,6 s et ne s'éteint qu'après 0,25 s sans appel.
- **Tout appel au serveur de plus de 0,3 s** (filtre, page suivante, enregistrement…) : barre fine ambre en haut de la fenêtre,
  affichée automatiquement par la mise en page (`BarreChargement`, appels comptés par `AppelsEnCours`). Un écran n'a rien à
  ajouter. Un appel plus rapide n'affiche rien, pour éviter le clignotement ; des appels enchaînés gardent la barre allumée.

## Barre d'état

- Une bande fixe de 32 px en bas de la fenêtre, sur tous les écrans et tous les postes (`Composants/Mise/BarreEtat.razor`, maquette
  validée le 2026-11-21). **Éléments fixes**, toujours affichés, en texte 13 px gris (`.element-etat`) : voyant « Connecté » /
  « Serveur injoignable » à gauche, poste · vendeur connecté (`.identite-etat`, 260 px au plus, « … » au-delà), date et heure à droite
  (`.element-etat.droite`, version dans l'infobulle). Le nom de la base n'y figure pas (Administration › Sauvegardes).
- **Pastilles** (`.pastille` dans `.barre-etat`, zone `.pastilles-etat` qui coupe ce qui dépasse) : seulement s'il y a une
  information ; 22 px de haut, rayon 11 px, 12,5 px. Trois niveaux, jetons de la charte (clair et sombre, aucune couleur écrite en dur) :
  gris « à traiter » (fond `--texte-2` à 12 %, texte `--texte`, 500) ; orange `.attention` « à surveiller » (`--attention-fond`,
  `--attention`, 600) ; rouge `.anormal` « anomalie » (`--urgent-fond`, `--urgent`, 600). Bouton quand l'employé a le droit d'ouvrir
  l'écran (souligné et contour `--accent-texte` au survol et au clavier), texte simple sinon. Règles d'affichage : regles-metier.md
  « Barre d'état : éléments fixes et pastilles ».
- **« À traiter (N) ▾ »** (`.pastille.groupe`, hors de la zone des pastilles) : dès qu'une pastille orange ou rouge s'affiche, les
  grises y sont regroupées ; petite liste (`.liste-etat`, `role="dialog"`, `aria-expanded`, `aria-controls`) posée au-dessus de la
  barre, alignée à droite du bouton, fermée par Échap (le curseur revient sur le bouton), un clic dehors (`.fond-liste-etat`
  transparent sur toute la fenêtre) ou le choix d'un écran. Au plus 4 anomalies visibles ; le bouton prend la couleur de l'anomalie la
  plus grave rangée dans la liste.
- **Pastille « Message »** (reste du lot S7.3, 2026-11-21) : pastille grise, toujours cliquable ; elle ouvre une petite fenêtre
  (`.fenetre-message-etat`, `role="dialog"`) posée au centre au-dessus de la barre, 520 px au plus, fond `--surface`, bordure
  `--bordure`, texte sur plusieurs lignes (`white-space: normal`), boutons de la charte (« Fermer » principal, « Ne plus afficher » et
  « Ordres du central » en contour) ; fermée par Échap (curseur posé sur « Fermer » à l'ouverture), un clic dehors ou « Fermer ». Au-delà
  de 6 pastilles grises, toutes passent dans « À traiter ». **Non mesurée** à ce jour.
- La barre ne passe jamais à la ligne : `white-space: nowrap`, `overflow-x: clip` (la liste peut s'ouvrir au-dessus). Aucun style en
  ligne (taille, couleur, hauteur) dans la barre. **Mesurée le 2026-11-21** (page de mesure avec le vrai app.css et les polices Poppins)
  : une ligne à 1 366 × 768 et 1 536 × 890, clair et sombre, dans le cas le plus chargé de la maquette (4 anomalies et « À traiter (4) » :
  205 px libres à 1 366), avec 4 anomalies rouges et « À traiter (8) » (4 px libres à 1 366) et avec 6 pastilles grises sans regroupement
  et un nom long (17 px libres à 1 366).

## Tiers et fiche contact

- **Tout nom de tiers affiché dans une ligne est cliquable** : patient, grossiste, organisme, entreprise cliente, employé. On
  l'écrit avec le composant `<LienContact Type="…" Id="…" Nom="…" />` (types : `patient`, `grossiste`, `organisme`, `client`, `employe`) ;
  sans identifiant, le nom reste du texte simple. Le lien garde la taille et la couleur du texte qui l'entoure, souligné en
  pointillé.
- Le clic ouvre la **fiche contact**, une fenêtre unique posée dans la mise en page (`FenetreContact`), au-dessus de tout, y
  compris d'une autre fenêtre : coordonnées, situation (dette, factures ouvertes, rôle…) et raccourcis vers les écrans du tiers
  que l'employé a le droit d'ouvrir. Fermeture par « Fermer », Échap ou clic à côté.
- Une liste, une requête ou un échange qui affiche un nom de tiers transporte aussi son identifiant, pour que le nom soit
  cliquable. Ne pas placer un lien de contact à l'intérieur d'un autre bouton.

## Contrôle

Avant de livrer un écran : le comparer à un écran d'Administration (par exemple Sauvegardes), dans les deux thèmes. Le relevé
de styles fait le 10 novembre 2026 sur les 21 écrans donnait, après alignement, les mêmes valeurs partout pour chaque ligne du
tableau « Hiérarchie du texte ».

### Code d'accès à distance (S7.2b, 2026-11-21)

`.code-acces-distant` (Administration › Synchronisation › Accès à distance) : code montré une seule fois, en grand (32 px, chasse fixe,
espacé), couleurs par variables (`--surface-2`, `--accent-texte`, `--bordure`) donc lisible en clair et en sombre ; champs de la ligne
`.achats-ligne` avec `.champ-compte-distant` et `.champ-bouton`. Aucun style en ligne dans la rubrique. **Mesurée le 2026-11-21** (voir « Mesures du
2026-11-21 (Synchronisation › Accès à distance, poste relié au central) »).

### Mesures du 2026-11-21 (S7.2a, écrans du titulaire au central)

Compte `titulaire.dev`, officine de développement rattachée par un code de la console ; Edge sans fenêtre, 1 366 × 768 et 1 536 × 890,
clair et sombre : « Mes officines », « Rattacher une officine », tableau de bord (13 tuiles sur deux rangées), journal des accès de
l'officine : **0 px** partout ; un titulaire renvoyé de `/officines` (écran de l'éditeur) arrive sur « Mes officines ».

### Mesures du 2026-11-21 (barre d'état : éléments fixes et pastilles, application lancée)

Poste lancé sur `pharmacie_test` (API 5081), comptes `titulaire` et `vendeur`, 1 366 × 768 et 1 536 × 890, clair et sombre :
barre de 32 px sur une ligne, **0 px** de débordement, 0 px de défilement de page. Titulaire : « Sauvegarde : disque absent »
(rouge), « Caisse depuis le 18/11 à 20:44 » (orange), « À traiter (4) » (10 ventes en attente, 1 produit revenu, 12 Chifa à
déposer, 2 traites à payer). Vendeur : mêmes alertes, « À traiter (2) » (ventes en attente, produit revenu), sauvegarde en texte
simple (pas de droit sur l'écran). Liste « À traiter » ouverte vers le haut dans la fenêtre ; Échap la ferme et rend la main au bouton.

### Mesures du 2026-11-21 (S7.3, vue d'ensemble et ordres du titulaire au central)

Compte `titulaire.dev`, 1 366 × 768 et 1 536 × 890, clair et sombre : « Mes officines », « Vue d'ensemble », « Ordres à distance »
(état « droit non accordé ») : **0 px**. Défaut corrigé : le tableau de la vue d'ensemble (12 colonnes) défilait horizontalement à
1 366 px, colonne « Ordres en attente » coupée → dernier contact sous le nom de l'officine, en-têtes courts (« CA jour », « Mois préc. »,
« Péremption »…), montants sur une ligne (`.tableau-ensemble`) : 0 px horizontal et vertical. Mesurés ensuite le 2026-11-21 : rubrique
« Accès à distance » du poste (section suivante) et ordres avec le droit accordé (1 366 × 768, essai de bout en bout, statut « Appliqué ») : 0 px.

### Mesures du 2026-11-21 (Synchronisation › Accès à distance, poste relié au central)

`pharmacie_test` reliée au central de développement, compte `titulaire`, 1 366 × 768 et 1 536 × 890, clair et sombre. Défaut
corrigé : la page défilait (205 px à 1 366, 99 px à 1 536) → les deux panneaux (code, comptes rattachés) dans un cadre défilant
(`.cadre-defilant.cadre-empile`, comme les autres sous-rubriques) : **0 px** de page ; la zone « Droits » ou « Retirer », ouverte
sous le tableau, est amenée dans le cadre (`pharma.voirZone`) et reste entièrement visible aux deux tailles.

### Mesures du 2026-11-21 (après-midi)

`pharmacie_test`, compte `titulaire` (et `titulaire.dev` au central), 1 366 × 768 et 1 536 × 890, clair et sombre.

- **Central, `/mes-officines/reglages`** (S7.4) : défilait (35 px à 1 366 × 768, 104 px avec un message ; 62 px à 1 536 × 890 avec un
  message) → hauteur du cadre posée par `ecrans.js` (`data-hauteur-restante`) au lieu de `calc(100vh - 330px)` : **0 px** partout. Tableau
  « Propositions envoyées » : défilement horizontal, colonne Statut cachée → texte qui passe à la ligne : **0 px**.
- **Poste, Réglages mutualisés** avec une proposition en attente : **0 px** aux deux tailles, clair et sombre.
- **Poste, page de Connexion** : défilait de 53 px à 1 366 × 768 avec le message « … a quitté le poste » → marges resserrées
  (`.page.connexion`) : **0 px** dans les trois états (code comptoir, mot de passe, super admin).
- **Achats › Commander** : accord du texte corrigé (« 10 produits cochés chez 4 grossistes »).
- **Mesurés à 0 px de page** : Achats › Commander (le tableau seul défile), Catalogues des grossistes, Ruptures chez les grossistes,
  Substitution, Contrôle interne › Intégrité des journaux, Sérialisation DataMatrix, 2e serveur ; ordres à distance avec le droit
  accordé (1 366 × 768, essai de bout en bout, statut « Appliqué »).
- **Non mesurés** : `/admin/reprise` et `/admin/profils-reprise` à 1 366 × 768 (mot de passe noté du compte `super.essai` refusé ; ne
  pas réessayer, risque de blocage ; à demander au titulaire).

### Mesures du 2026-11-21 (fin d'après-midi : reprise, cadres du poste, S7.5)

- **Reprise de données** (super admin `super.essai`, API lancée avec `SuperAdmin__Fichier`) : défilait de 55 px à 1 366 × 768 →
  en-tête resserré (`.page-reprise`), aide d'une ligne, tableau « Reprises précédentes » à 160 px au plus bas (`data-hauteur-min`) :
  **0 px** ; Profils de reprise : 0 px ; 1 536 × 890 : 0 px ; clair et sombre.
- **`poste.js`, correction des cadres** : quand l'écran déborde encore, la réduction part de la hauteur **réelle** du cadre (et non de
  sa hauteur permise, parfois plus grande que son contenu : la réduction restait sans effet) et passe au cadre suivant si le plus grand
  est à son minimum (8 essais au plus). Contre-mesure sur 20 écrans (accueil, caisse, stock, achats, finances, patients, pilotage,
  synchronisation, sauvegardes, utilisateurs…) aux deux tailles : 0 px, sauf deux défauts anciens corrigés ci-dessous.
- **Caisse › État de la caisse** : la 5e tuile passait seule en 2e ligne (65 px à 1 366) → toutes les tuiles sur une ligne
  (`.tuiles-etat-caisse`) : 0 px.
- **Pilotage financier › Synthèse** : 9 px à 1 366 × 768 → histogramme de 76 px sous 800 px de haut : 0 px.
- **S7.5, lignes absentes** (central) : rubrique de l'éditeur (`?rubrique=absentes`, les deux officines), tableau de bord du titulaire
  (14 tuiles sur deux rangées) : 0 px aux deux tailles, clair et sombre ; vue d'ensemble : le tableau à 11 colonnes défilait de 51 px
  sur le côté → cellules resserrées (`.tableau-ensemble`, 5 px) : 0 px horizontal et vertical. Aucune ligne absente sur les deux
  officines de développement (état « 0 » vu, état avec lignes non provoqué).

### Vérifications du 2026-11-21 (soir) : tuiles de la Reprise et des Rapports

- **Reprise, étape 5 (Vérification)** : 3 px de défilement à 1 366 × 768 (colonne sans cadre) → marge du bas de la page Reprise 6 px :
  0 px aux deux tailles, clair et sombre. La tuile vers `#t-verif` amène la section en haut de son cadre, sans faire défiler la
  page, l'assistant reste sur sa reprise et son étape. À l'étape 4 (section non affichée), les tuiles restent des chiffres.
- **Période gardée** : Rapports en « Mois » → tuile Retours → `?rubrique=retours` toujours en novembre 2026 ; Pilotage financier en
  « Semaine » → tuile Marge → `?rubrique=marges` sur la même semaine.

### Mesures du 2026-11-21 (soir) : envoi des codes et contacts des comptes (central, éditeur)

- `/envoi-codes` : avec un message, la page défilait de 101 px à 1 366 × 768 (colonne des consignes Gmail) → zone à deux colonnes
  dont la hauteur est posée par `ecrans.js` (`data-hauteur-restante`), chaque panneau défilant en lui-même ; `ecrans.js` rend en
  outre à la zone l'espace resté libre sous la page. **0 px** aux deux tailles, clair et sombre, avec et sans message.
- `/comptes` et `/comptes?compte=…` (formulaire ouvert) : 0 px ; en-tête de l'éditeur à 4 liens sur une ligne.
- Contre-mesure de `/mes-officines/reglages` (même mécanisme) : 0 px avec et sans message.
