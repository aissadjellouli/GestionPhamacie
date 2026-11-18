# Logo Tracodex

Nom du logiciel (choix du titulaire, 2026-11-18) : **Tracodex Officine** (logiciel de la pharmacie) et **Tracodex Réseau** (serveur central).
**À refaire par le graphiste** : `pharmaofficine-logo.svg`, `pharmaofficine-logo-blanc.svg` et `logo-880.png` portent encore le texte « PharmaOfficine » (le symbole seul, utilisé dans le logiciel, n'a pas de texte). Les noms de fichiers restent tels quels.

Logo adopté par le titulaire le 2026-11-18 (remplace la croix verte avec coupe d'Hygie). Fichiers sources dans ce dossier, à utiliser
tels quels : ne pas redessiner, déformer, recolorer ni ajouter d'ombre.

## Le logo

Gélule inclinée à 45° : moitié haute vert officine avec un croissant blanc et un serpent blanc en S (tête vers le haut, langue
fourchue), moitié basse ambre.

| Couleur | Hex | Usage dans le logo |
|---|---|---|
| Vert officine | `#0F766E` | moitié haute |
| Vert clair (fond sombre) | `#14B8A6` | moitié haute, version sombre |
| Ambre | `#F59E0B` | moitié basse (jamais pour du texte sur fond blanc) |
| Encre | `#102A33` | nom « Pharma », tuile de l'icône sombre |

Depuis le 2026-11-18, le vert de l'interface est celui du logo : valeurs, variantes pour le texte et contrastes dans
charte-graphique.md (« Fonds »). L'ambre de l'interface reste #E08A1E (contraste sous l'en-tête vert).

## Fichiers

| Fichier | Usage |
|---|---|
| `pharmaofficine-icone.svg` | symbole seul, fond transparent |
| `pharmaofficine-icone-sombre.svg` | icône sur tuile sombre arrondie |
| `pharmaofficine-logo.svg` | symbole + nom, fond clair (le nom utilise la police Space Grotesk, non embarquée) |
| `pharmaofficine-logo-blanc.svg` | symbole + nom, fond sombre |
| `favicon.svg`, `favicon.ico` | petite taille (16 à 48 px) |
| `icone-16` à `icone-512.png`, `icone-app-512.png` | images carrées |
| `logo-880.png` | symbole + nom en image (documents, e-mails) |

## Où il est utilisé dans la solution

| Endroit | Fichier de la solution | Source |
|---|---|---|
| En-tête du poste (vignette blanche 40 px) et écran de connexion | `src/Pharma.Desktop/wwwroot/img/logo.svg` | `pharmaofficine-icone.svg` |
| Fenêtre, barre des tâches, `.exe` | `src/Pharma.Desktop/logo.ico` | fabriqué depuis `icone-512.png` : tailles 16, 24, 32, 48, 64 et 256 (le `favicon.ico` fourni s'arrête à 48) |
| Application mobile (scanner) | `src/Pharma.Api/wwwroot/mobile/logo.svg` | `pharmaofficine-icone.svg` |
| Titre des PDF (bons, factures…) | `src/Pharma.Infrastructure/Impression/Ressources/logo.png` | `icone-192.png` (1 cm de haut) |

## Règles

- Zone de protection autour du logo : au moins la moitié de la largeur de la gélule.
- Taille minimale : 24 px pour l'icône, 120 px de large pour le logo avec le nom ; en dessous de 24 px, `favicon.svg`.
- Ne jamais mettre le croissant en rouge (le croissant rouge est un emblème protégé).

## À vérifier avant de déposer la marque (démarche du titulaire)

- Recherche d'antériorité à l'INAPI, classes 9, 42 et 5.
- Le croissant peut être jugé comme évoquant un emblème national (ordonnance 03-06) : avis de l'INAPI ou d'un conseil en
  propriété industrielle (non vérifié).
