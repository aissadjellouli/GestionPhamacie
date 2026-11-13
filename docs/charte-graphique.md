# Charte graphique du poste

Règle permanente : **tout écran, nouveau ou modifié, respecte cette charte.** On ne s'en écarte que sur demande explicite du
titulaire du projet, et l'écart est alors noté ici. Le modèle de référence est l'Administration : titre, sous-titre, panneaux
et notes explicatives discrètes.

Tout est défini dans `src/Pharma.Desktop/wwwroot/css/app.css`. Un écran ne redéfinit ni taille, ni couleur, ni police : il
utilise les classes ci-dessous. Pas de `style="font-size: …"`, `style="color: …"` ni `style="height: …"` dans un écran.

## En-tête et menu

Une seule ligne, dans cet ordre : logo et nom de la pharmacie, **« ← *écran précédent* »**, menu, puis outils de l'employé.
« ← *écran précédent* » (à gauche) et « ⇄ Changer » (à droite) sont des **pastilles blanches à texte vert** : repérables d'un coup
d'œil, et éloignées du menu pour ne pas être confondues avec un écran. Leurs libellés restent courts pour garder l'en-tête sur une ligne.

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

### Écran trop chargé : menu vertical de rubriques

Un écran qui empile trop de panneaux est découpé en **rubriques**, avec le menu vertical de l'Administration (même rendu :
liens à gauche, rubrique active surlignée avec le filet ambre). Utilisé par l'Administration (`.admin`, `.admin-nav`,
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
  d'un document (lignes d'une facture), réglages (seuils par grossiste, droits par rôle), rayons, sauvegardes gardées
  (nombre limité par le réglage), synthèses (échéancier, tableau par jour d'un rapport). Un test d'architecture
  (`ListesTests`) liste ces exceptions avec leur raison et refuse tout nouveau tableau qui n'y est pas et n'est pas paginé.
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
