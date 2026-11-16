# Charte graphique du poste

Règle permanente : **tout écran, nouveau ou modifié, respecte cette charte.** On ne s'en écarte que sur demande explicite du
titulaire du projet, et l'écart est alors noté ici. Le modèle de référence est l'Administration : titre, sous-titre, panneaux
et notes explicatives discrètes.

Tout est défini dans `src/Pharma.Desktop/wwwroot/css/app.css`. Un écran ne redéfinit ni taille, ni couleur, ni police : il
utilise les classes ci-dessous. Pas de `style="font-size: …"`, `style="color: …"` ni `style="height: …"` dans un écran.

## En-tête et menu

Une seule ligne, dans cet ordre : logo et nom de la pharmacie (lien vers l'accueil), **« ← *écran précédent* »**, menu des
**domaines** (Caisse, Stock, Achats, Finances, Pilotage ; chacun suivi de son raccourci « ⇧lettre » dans un petit cadre),
**« ⌕ Rechercher… Ctrl K »**, puis outils de l'employé. Pas de menu déroulant : les écrans d'un domaine sont dans le menu de
gauche (docs/navigation.md).
« ← *écran précédent* » (à gauche) et « ⇄ Changer » (à droite) sont des **pastilles blanches à texte vert** : repérables d'un coup
d'œil, et éloignées du menu pour ne pas être confondues avec un écran. Leurs libellés restent courts pour garder l'en-tête sur une ligne.

L'en-tête occupe **toute la largeur de la fenêtre** (validé le 2026-11-13) : logo et nom dans les 240 px de gauche, au-dessus de la
colonne du menu vertical, le reste réparti jusqu'au bord droit. Nom du logiciel et « pharmacie · poste » restent sur une ligne,
coupés par « … » s'ils sont trop longs (texte complet au survol).

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
édition du profil dans une fenêtre `.voile` qui défile dans son cadre ; mesure à 0 px à refaire sur le poste),
`/admin/restauration`, `/admin/sauvegardes`, `/admin/seuils`, `/guide`, `/mon-code`, `/mot-de-passe`.
**Écran ajouté le 2026-11-16 (lot 10, sérialisation), conçu fixe (seul le tableau défile, paginé et filtré par statut) ; mesure
à 1 536 × 890 à faire à l'écran** : `/admin/serialisation`.
**Écran ajouté le 2026-11-16 (lot 5, 2e serveur), conçu fixe 1 536 × 890 (trois sous-rubriques à gauche : État de la réplication,
Réglages par serveur, Clé de scellement ; tableau des serveurs seul défilant) ; mesure à 0 px à faire à l'écran** : `/admin/second-serveur`.
**Écran ajouté le 2026-11-16 (lot S2, synchronisation), conçu fixe 1 536 × 890 (trois sous-rubriques à gauche : État, Réglages,
Activation ; aucun tableau, aucun défilement) ; mesure à 0 px à faire à l'écran** : `/admin/synchronisation`.
**Écran ajouté le 2026-11-16 (lot 11, tableau de bord financier), conçu fixe 1 536 × 890 (sept rubriques à gauche : Synthèse,
Créances, Stock et dormant, Marges, Achats par grossiste, Rejets Chifa, TVA par taux ; tuiles, puis un seul tableau défilant,
paginé ; marges et stock dormant filtrés et paginés par le serveur) ; mesure à 0 px à faire à l'écran** : `/pilotage-financier`.
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
`/admin/reprise` et `/admin/profils-reprise` (super admin) : non mesurés, compte super admin non utilisé.

**Ajouté le 2026-11-16 (lot 2, à mesurer sur le poste)** : Administration › Contrôle interne › Intégrité des journaux
(`/admin/controle-interne?rubrique=integrite`) : titre et bouton « Vérifier maintenant » sur une ligne, un seul tableau en
cadre défilant (`.tableau-cadre`), page attendue à 0 px. Mesure à 1 536 × 890 non faite (poste non lancé) : à vérifier.

**Ajouté le 2026-11-16 (lot 3, à mesurer sur le poste)** : Administration › Contrôle interne › Journal fiscal
(`/admin/controle-interne?rubrique=journal-fiscal`) : titre, période, ticket et boutons d’export sur une ligne ; une ligne d’état
(chaîne, compteur des tickets, conservation) ; à gauche le tableau des lignes en cadre défilant et paginé, à droite le récapitulatif
TVA et les sceaux de la période (cadre défilant). Page attendue à 0 px. Mesure à 1 536 × 890 non faite (poste non lancé) : à vérifier.

**Ajouté le 2026-11-16 (lot 6, à mesurer sur le poste)** : Achats › Commander (multi-grossistes)
(`/achats?onglet=commander`) : filtres sur une ligne, un seul tableau en cadre défilant (`.tableau-cadre`), pagination,
barre de répartition sur une ligne ; Achats › Catalogues des grossistes (`/achats?onglet=catalogues`) : profil et import
dans un panneau défilant à gauche, imports en tableau paginé à droite (`.achats-grille`). Page attendue à 0 px ; mesure à
1 536 × 890 non faite (poste non lancé) : à vérifier.

**Ajouté le 2026-11-16 (lot 7, à mesurer sur le poste)** : Achats › Ruptures chez les grossistes (`/achats?onglet=ruptures`) :
filtres sur une ligne, un seul tableau en cadre défilant, pagination ; Achats › Patients en attente
(`/achats?onglet=patients-attente`) et Achats › Substitution (`/achats?onglet=substitution`) : saisie et réglages dans un panneau
défilant à gauche, liste paginée et filtrée à droite (`.achats-grille`) ; bandeau « Produit redevenu disponible » de la Caisse
(`.info-bandeau`, une ligne). Page attendue à 0 px ; mesure à 1 536 × 890 non faite (poste non lancé) : à vérifier.

**Ajouté le 2026-11-16 (lot 8, à mesurer sur le poste)** : Administration › Psychotropes et antibiotiques
(`/admin/psychotropes`, rubriques « Alertes du registre » : un tableau de 4 règles ; « Suivi des antibiotiques » : deux
panneaux côte à côte, chaque tableau en cadre défilant et paginé). Registre : nouvelle rubrique « Alertes forcées »
(`/stock/registre?rubrique=forcages`, liste paginée et filtrée par le serveur) ; ligne des filtres des inscriptions avec
« Prescripteur (export) » et trois boutons PDF, Excel, CSV (à vérifier : tient-elle sur une ligne ?). Caisse : fenêtre
« Alerte du registre des psychotropes » (voile). Mesure à 1 536 × 890 non faite (poste non lancé) : à vérifier.

### Écran trop chargé : menu vertical de rubriques

Un écran qui empile trop de panneaux est découpé en **rubriques**, avec le menu vertical de l'Administration (même rendu :
liens à gauche, rubrique active surlignée avec le filet ambre).

**Le menu de gauche liste les écrans du domaine** (composant `NavDomaine`, docs/navigation.md) : titre du domaine (gris,
majuscules), puis ses écrans ; l'écran affiché est écrit en vert, ses rubriques en retrait dessous (13 px), la rubrique affichée
avec le bandeau ambre. Un écran garde ses rubriques et son résumé (`nav-resume`) en les passant à `NavDomaine` ; il ne liste
jamais lui-même les autres écrans. Accueil : tuiles cliquables (`.tuile.tuile-lien` : toute la carte ouvre l'écran, bordure verte au
survol, seul « … → » souligné).

Compléments (2026-11-13) : **compteurs** du travail en attente dans le menu du haut (fond blanc, chiffre vert ; rouge si urgent)
et à gauche (`.compteur.neutre`, ou `.compteur` si urgent) ; le menu du haut reste sur une ligne (`nowrap`, 8 px de marge par
domaine). **Étape suivante** sous une liste vide : un seul bouton principal « … → » (`.action-suivante`), centré dans une cellule
de tableau. **Produit ou lot cliquable** : rendu des noms de contact (souligné en pointillé). **Aide** : rond « ? » de 30 px à
côté du titre, panneau de 380 px à droite (titre vert 17 px, liste courte, raccourcis en gris, « Guide complet » et « Fermer »).

**Le menu est collé au bord gauche de la fenêtre** (demandé le 2026-11-13) : colonne de 240 px sur toute la hauteur, de l'en-tête
à la barre d'état, fond `--surface` et filet `--bordure` à droite ; la page n'est plus centrée ni limitée à 1 280 px, le contenu
prend toute la largeur restante (32 px d'écart avec la colonne). Tout vient de `.page.admin` et `.page.avec-menu` dans `app.css` :
aucun écran n'a de style propre pour cela. Les écrans sans menu vertical restent centrés (1 280 px au plus).

**Bandeau ambre : toujours sur l'écran ou la sous-rubrique affichés, et sur eux seuls.** Dans l'Administration, l'écran affiché qui a
des sous-rubriques (Alertes et seuils, Utilisateurs et droits…) devient un **titre de groupe non cliquable** (`.nav-groupe-titre`,
gris, majuscules), comme « COMMANDES » ou « AUTRES ÉCRANS DU STOCK » dans les autres menus ; sa sous-rubrique affichée porte le
bandeau. Les autres écrans de l'Administration restent des liens. Utilisé par l'Administration (`.admin`, `.admin-nav`,
`.admin-contenu`, composant `NavAdmin`), la Trésorerie, Dettes et créances, Stock, Alertes, Registre, Inventaire, Achats, Rapports,
Contrôle interne (composant `NavControleInterne`, commun aux anomalies et aux écarts et risques), le Guide et la Caisse (composant
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
- Réglages nombreux (seuils) : groupés par thème en colonnes, une ligne par réglage, libellé à gauche et petite valeur à droite
  (`.reglages-groupes`, `.reglage-ligne`), pour tenir sans défilement.

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
| Étiquette de champ | `label` dans `.champ` | 13 px | texte secondaire (gris) |
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
Seuls le logo, les images (ordonnances) et les pastilles de l'en-tête vert restent en blanc. Le thème sombre ne change pas.

Les couleurs passent toujours par les variables (`var(--…)`) : c'est ce qui fait fonctionner les thèmes clair et sombre.

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
  - Exemples : choix du produit (composant `ChoixProduit`), du patient et du médecin à la caisse, du patient et de l'entreprise dans
    l'éditeur de facture, filtre « Tiers » de Dettes et créances (recherche pour patients, factures clients et organismes des
    bordereaux, demandée le 2026-11-13 ; liste pour grossistes et effets).
- **Tout tableau paginé a des filtres au-dessus** (`.filtres-liste` : grossiste ou tiers, statut ou type, période « Du » / « Au »,
  recherche selon le tableau), appliqués par le serveur pour que le total et les pages les suivent, avec un bouton
  « Tout afficher » dès qu'un filtre est actif et un message « Aucun … pour ces filtres » quand rien ne répond.
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
- **Erreurs du serveur** : une règle qui vise un champ le désigne (`throw new RegleMetierException(message, "montant")`) ; la réponse
  d'erreur porte ce nom (`ErreurDto.Champ`) et le poste met en rouge le champ qui porte `data-champ="montant"` (dans la fenêtre
  ouverte au premier plan, sinon dans la page). Un contrôle fait sur le poste sans aller au serveur passe par le même chemin :
  `erreur = Api.SignalerChamp("montant", "…")`. Noms en minuscules avec soulignés (`montant`, `email`, `motif`, `nom_patient`…).
- **Tout champ obligatoire** (libellé `requis`) porte `aria-invalid` (contrôle sur le poste) ou `data-champ` (désigné par le
  serveur) ; et tout nom de champ utilisé par le serveur existe en `data-champ` sur un écran. Les deux règles sont vérifiées par
  `tests/Pharma.Architecture.Tests/ChampsObligatoiresTests.cs`.

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

- Une bande fixe de 32 px en bas de la fenêtre, sur tous les écrans et tous les postes (`Composants/Mise/BarreEtat.razor`) : liaison
  avec le serveur, caisse du poste, dernière sauvegarde, version, date et heure. Le nom de la base n'y figure pas : il est affiché à
  titre d'information dans Administration › Sauvegardes. Avant la connexion :
  liaison au serveur et version seulement.
- Texte en 13 px gris (information secondaire) ; seuls les états anormaux ressortent : serveur injoignable et sauvegarde échouée
  en rouge, caisse ouverte depuis un autre jour, sauvegarde absente, désactivée ou plus vieille qu'hier en orange.
- Un clic ouvre l'écran correspondant, selon les droits : caisse (ouverte : relève et clôture ; fermée : caisse), sauvegarde
  (Administration › Sauvegardes) ; un clic sur le serveur le revérifie aussitôt.

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
