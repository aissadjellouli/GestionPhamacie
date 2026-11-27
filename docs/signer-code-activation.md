# Signer un code d'activation (ou de transition) et le saisir dans l'Administration

> Procédure de l'éditeur, pas à pas (2026-11-27). Création et garde des clés, perte ou vol : [procedure-cle-editeur.md](procedure-cle-editeur.md).
> Décisions : [offre-et-options.md](offre-et-options.md). Règle : [regles-metier.md](regles-metier.md), « Offre de base et options ».

Un **code d'activation** (`PO1.…`) fixe l'offre d'une pharmacie : packs A à H, nombre de postes, scanners mobiles, date de fin. Un **code de
transition** est un code « tout compris » (les 8 packs), toujours limité dans le temps, saisi lors de la mise en place ou de la mise à jour,
le temps de choisir l'offre définitive. Les deux se signent avec la même commande.

**Qui fait quoi** : la **pharmacie** lit son code d'officine et son NIF dans l'Administration ; l'**éditeur** signe le code **sur son PC**
(jamais sur le serveur de l'officine) ; le code est **saisi dans l'Administration** avec le compte du titulaire.

---

## Étape 1 — Relever le code de l'officine et le NIF (dans l'Administration de la pharmacie)

1. Se connecter au logiciel avec le compte **titulaire**.
2. **Le NIF doit être saisi d'abord** : **Administration › Langue et pharmacie**, onglet « Pharmacie et langue », champ **NIF**, puis
   enregistrer. Sans NIF, aucun code ne peut être activé (« NIF de la pharmacie non saisi »).
3. Ouvrir **Administration › Mon offre**. Dans le cadre **« Saisir un code d'activation »**, la phrase « Pour obtenir un code, donnez à
   l'éditeur ces deux informations » affiche :
   - le **code de l'officine** (forme `1ea51897-2885-4e89-bc0c-89765d819402`) ;
   - le **NIF** (affiché tel qu'il est saisi ; il est comparé sans espaces, points ni tirets).
4. Les transmettre à l'éditeur (ils ne sont pas secrets).

## Étape 2 — Signer le code (sur le PC de l'éditeur)

Dans **sa propre fenêtre PowerShell** (le mot de passe de la clé ne doit passer par aucun outil qui garde l'historique) :

```powershell
cd C:\MesAtlassian\pharmaofficine
dotnet run --project src\Pharma.Editeur -- signer --cle <fichier de la clé> --officine <code de l'officine> --nif <NIF> --postes <n> [options]
```

Le mot de passe de la clé est demandé (rien ne s'affiche pendant la frappe).

### Les paramètres, un par un

| Paramètre | Obligatoire | Valeurs | Effet | Exemple |
|---|---|---|---|---|
| `--cle <fichier>` | **Oui** | Chemin de la clé privée | Clé qui signe. **Clé principale** en temps normal ; **clé de secours** seulement si la principale est perdue ou volée | `--cle C:\MesAtlassian\cle-editeur\cle-editeur.pem` |
| `--officine <code>` | **Oui** | Code de l'officine (étape 1) | Le code ne vaut que pour cette pharmacie | `--officine 1ea51897-2885-4e89-bc0c-89765d819402` |
| `--nif <NIF>` | **Oui** | 5 à 30 lettres ou chiffres ; espaces, points et tirets retirés | Le code ne vaut que pour ce NIF ; si la pharmacie change de NIF, il faut un nouveau code | `--nif 000016000000000` |
| `--postes <n>` | **Oui** | 1 à 200 | Nombre maximum de postes autorisés (caisses et PC du titulaire ; ni les serveurs ni les téléphones) | `--postes 5` |
| `--packs <liste>` | Non | `A,B,…,H` séparés par des virgules, ou `tous` | Packs ouverts ; absent = **aucun** pack (offre de base + postes) | `--packs A,D` |
| `--scanners <n>` | Non (0) | 0 à 200 | Scanners mobiles prévus (pas encore compté par le programme) | `--scanners 2` |
| `--fin AAAA-MM-JJ` | Non ; **obligatoire avec `--transition`** | Date | Dernier jour de l'offre (inclus) ; absent = offre **définitive** | `--fin 2027-12-31` |
| `--transition` | Non | (sans valeur) | Code de transition : **les 8 packs** quel que soit `--packs` ; `--fin` obligatoire | `--transition` |

**Packs** : A Contrôle et anti-fraude · B Inventaire et traçabilité · C Achats avancés · D Gestion financière · E Pilotage et notifications ·
F Continuité de service · G Multi-officines et accès à distance · H Confort du comptoir. Détail : [offre-et-options.md](offre-et-options.md).

**Date de fin** : jusqu'au jour de fin inclus tout fonctionne ; le titulaire est prévenu par SMS ou e-mail **90, 60, 30, 15, 7 et 1 jour(s)
avant**, puis au début des **15 jours de grâce** ; ensuite retour à l'offre de base (2 postes, packs fermés), **aucune donnée effacée**.
Conseils : abonnement au dernier jour d'un mois (date anniversaire) ; transition de 3 à 6 mois.

**Nombre de postes** : compter les caisses en service + le PC du titulaire s'il utilise le logiciel + **1 de réserve** (remplacer un PC en
panne sans attendre un code). Une offre plus petite que les postes déjà autorisés n'en retire aucun : elle refuse seulement les nouveaux.

### Ce qui s'affiche

```
Pharmacie 1ea51897-… · NIF 000016000000000 · 20 poste(s) · packs A,B,C,D,E,F,G,H · scanners 0 · jusqu'au 31/12/2027 · transition
PO1.eyJ2IjoxLCJvZmZpY2luZSI6…
```

La **2e ligne** (elle commence par `PO1.`) est le **code** à transmettre. Il n'est **pas secret** : il ne vaut que pour cette pharmacie.
Le copier **en entier**, sans espace ni retour à la ligne.

### Refus possibles de l'outil (rien n'est signé)

| Message | Cause |
|---|---|
| « Option --postes obligatoire. » (ou --officine, --nif) | Paramètre oublié |
| « Code de l'officine invalide » | Code de l'officine mal copié |
| « NIF absent ou mal formé » | NIF de moins de 5 ou de plus de 30 caractères |
| « Pack inconnu ou en double (A à H). » | Lettre hors A à H, ou répétée |
| « Un code de transition a toujours une date de fin. » | `--transition` sans `--fin` |
| « Date de fin invalide (AAAA-MM-JJ). » | Date mal écrite (ex. `31/12/2027` au lieu de `2027-12-31`) |
| « Mot de passe faux ou clé illisible. » | Mauvais mot de passe, ou mauvais fichier de clé |

### Contrôler un code avant de l'envoyer (facultatif, sans mot de passe)

```powershell
dotnet run --project src\Pharma.Editeur -- lire --code <PO1.…> --cle-publique (Get-Content C:\MesAtlassian\cle-editeur\cle-editeur-publique.txt)
```

**Attendu** : le contenu (officine, NIF, postes, packs, fin) et « Signature contrôlée : émise par l'éditeur. »

## Étape 3 — Saisir le code dans l'Administration

1. Se connecter avec le compte **titulaire** (le titulaire lui-même, ou l'éditeur à ses côtés). Le logiciel doit être d'une version qui
   contient les clés de l'éditeur (depuis le 2026-11-26).
2. Ouvrir **Administration › Mon offre**.
3. Dans le cadre **« Saisir un code d'activation »**, coller le code dans le champ **« Code d'activation »**.
4. Cliquer sur **« Activer »**.
5. **Attendu** :
   - message vert **« Code accepté : Période de transition (tout compris), 20 poste(s). »** (ou « Offre activée » pour un code ordinaire) ;
   - à gauche, **Offre en vigueur** : état, « N autorisés sur M » postes, « Valable jusqu'au … », packs **inclus** ;
   - en bas à droite, **Codes enregistrés** : une ligne de plus, état « contrôlé » ;
   - les bandeaux « Option non incluse » disparaissent (au plus 2 minutes après sur les autres postes).

**Par le serveur central** (pharmacie synchronisée), à la place de l'étape 3 :
`Pharma.Cloud.Api.exe --ordre-activation --officine <code de l'officine> --code <PO1.…> --auteur <nom>` ; le code est contrôlé et appliqué
dans l'officine au prochain échange.

### Refus possibles à la saisie (rien ne change)

| Message | Que faire |
|---|---|
| « Ce code a été émis pour une autre pharmacie » | Code signé pour un autre code d'officine : refaire l'étape 2 avec le bon |
| « Ce code a été émis pour un autre NIF » | NIF différent de celui saisi dans Langue et pharmacie : corriger le NIF ou re-signer |
| « NIF de la pharmacie non saisi » | Saisir le NIF (étape 1.2) |
| « Signature fausse » | Code mal copié (caractère en trop ou en moins) ; recoller le code entier |
| « Clé de l'éditeur absente de ce programme » | Version du logiciel trop ancienne : mettre à jour |
| « Code plus ancien que l'activation en place » | Un code plus récent est déjà saisi : signer un nouveau code |
| « Code échu depuis le … » | Date de fin et grâce passées : signer un nouveau code |
| « Ce code est déjà enregistré. » | Rien à faire : il est déjà en place |

## Exemples prêts à l'emploi

```powershell
# Transition 6 mois, tout compris, 10 postes
dotnet run --project src\Pharma.Editeur -- signer --cle C:\MesAtlassian\cle-editeur\cle-editeur.pem --officine <code> --nif <NIF> --postes 10 --transition --fin 2027-05-31

# Abonnement d'un an : contrôle (A) + finances (D), 5 postes
dotnet run --project src\Pharma.Editeur -- signer --cle C:\MesAtlassian\cle-editeur\cle-editeur.pem --officine <code> --nif <NIF> --postes 5 --packs A,D --fin 2027-11-30

# Offre définitive : tous les packs, 8 postes, 2 scanners
dotnet run --project src\Pharma.Editeur -- signer --cle C:\MesAtlassian\cle-editeur\cle-editeur.pem --officine <code> --nif <NIF> --postes 8 --packs tous --scanners 2
```

### Bases de l'éditeur

| Base | Code de l'officine | NIF | Code de transition |
|---|---|---|---|
| `pharmacie_dev` | `1ea51897-2885-4e89-bc0c-89765d819402` | `000016000000000` | Saisi le 2026-11-26 : 20 postes, packs A à H, jusqu'au 31/12/2027 |
| `pharmacie_test` | `142d3c7d-c7a3-4e40-84dc-04d4440af2dc` | `000016000000000` | À signer et saisir |

## Changer une offre

Signer un **nouveau code** avec **tout** le contenu voulu (packs, postes, date) : il **remplace** le précédent, il ne s'y ajoute pas. Le
plus récent (date de signature) l'emporte ; un code plus ancien est refusé.
