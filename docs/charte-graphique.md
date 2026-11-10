# Charte graphique du poste

Règle permanente : **tout écran, nouveau ou modifié, respecte cette charte.** On ne s'en écarte que sur demande explicite du
titulaire du projet, et l'écart est alors noté ici. Le modèle de référence est l'Administration : titre, sous-titre, panneaux
et notes explicatives discrètes.

Tout est défini dans `src/Pharma.Desktop/wwwroot/css/app.css`. Un écran ne redéfinit ni taille, ni couleur, ni police : il
utilise les classes ci-dessous. Pas de `style="font-size: …"`, `style="color: …"` ni `style="height: …"` dans un écran.

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
| En-tête de tableau | `th` dans `.tableau thead` | 12 px, majuscules | texte secondaire (gris) |
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

- Une liste qui peut grandir est paginée avec le composant `Pagination` : le nombre total de lignes est toujours affiché, et
  les totaux sont calculés par le serveur sur toutes les pages.
- **Tout tableau paginé a des filtres au-dessus** (`.filtres-liste` : grossiste ou tiers, statut ou type, période « Du » / « Au »,
  recherche selon le tableau), appliqués par le serveur pour que le total et les pages les suivent, avec un bouton
  « Tout afficher » dès qu'un filtre est actif et un message « Aucun … pour ces filtres » quand rien ne répond.
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
