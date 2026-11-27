# Clé de l'éditeur : création et utilisation

> Procédure de l'éditeur (2026-11-26). Contexte et décisions : [offre-et-options.md](offre-et-options.md). Règles :
> [regles-metier.md](regles-metier.md), « Offre de base et options : activation signée par l'éditeur ».

## À quoi sert la clé

Les options (packs A à H) et les quotas (postes, scanners mobiles) d'une pharmacie viennent d'un **code d'activation signé** (`PO1.…`).

- La **clé privée** (`cle-editeur.pem`) **signe** les codes. Elle reste chez l'éditeur, chiffrée par un mot de passe. Qui a ce fichier
  et son mot de passe peut se fabriquer n'importe quelle offre.
- La **clé publique** est **intégrée au programme** (`CleEditeur` dans `src/Pharma.Application/Offre/ActivationOffre.cs`). L'API s'en sert
  seulement pour **vérifier** les codes ; elle ne peut pas en créer.
- Il y a **deux clés** : la **principale**, qui signe, et la **clé de secours**, qui ne sert que si la principale est perdue ou volée.
  Un code signé par l'une ou l'autre est accepté.

Une clé ne se régénère **jamais à l'identique** : perdue sans copie, elle est perdue pour de bon.

## Règles de sécurité

| Toujours | Jamais |
|---|---|
| Générer et utiliser les clés sur le **PC de l'éditeur** | Sur le serveur d'une officine |
| Lancer les commandes dans **sa propre fenêtre PowerShell** | Dans un outil d'assistance ou de discussion qui garde la trace des commandes (le mot de passe passerait dans son historique) |
| Deux clés, deux dossiers, **deux mots de passe différents** | Le même mot de passe pour les deux |
| Une **clé USB par clé**, rangées dans deux lieux différents | `cle-editeur.pem` dans un dépôt git, par e-mail ou messagerie |
| Mots de passe notés à part (coffre de mots de passe ou papier rangé) | Donner un mot de passe à qui que ce soit, assistant compris |

L'outil `Pharma.Editeur` n'est **jamais livré** aux pharmacies.

## 1. Création des deux clés (une seule fois)

Fait le 2026-11-26 : clé principale dans `C:\MesAtlassian\cle-editeur`, clé de secours dans `C:\MesAtlassian\cle-secours` ; clés publiques
intégrées au programme le même jour. À refaire seulement pour remplacer une clé (§ 6).

1. Ouvrir PowerShell (pas besoin de droits administrateur) et vérifier l'outil :
   ```powershell
   cd C:\MesAtlassian\pharmaofficine
   dotnet build src\Pharma.Editeur -v q -nologo
   ```
   **Attendu** : « La génération a réussi. »
2. Créer les deux dossiers, hors du dépôt (pas dans un dossier synchronisé dans un nuage) :
   ```powershell
   New-Item -ItemType Directory -Force C:\MesAtlassian\cle-editeur, C:\MesAtlassian\cle-secours
   ```
3. Générer la clé principale, puis la clé de secours (un mot de passe **différent** pour chacune, 12 caractères au moins, demandé deux fois,
   rien ne s'affiche pendant la frappe) :
   ```powershell
   dotnet run --project src\Pharma.Editeur -- generer-cle --dossier C:\MesAtlassian\cle-editeur
   dotnet run --project src\Pharma.Editeur -- generer-cle --dossier C:\MesAtlassian\cle-secours
   ```
   **Attendu** : « Clé créée : … » puis la clé publique (`MFkw…`).
   **Refus possibles** (rien n'est écrit) : « Mot de passe trop court », « Les deux mots de passe diffèrent », « Une clé existe déjà »
   (une clé n'est jamais écrasée).
4. Vérifier que chaque mot de passe ouvre sa clé :
   ```powershell
   dotnet run --project src\Pharma.Editeur -- cle-publique --cle C:\MesAtlassian\cle-editeur\cle-editeur.pem
   dotnet run --project src\Pharma.Editeur -- cle-publique --cle C:\MesAtlassian\cle-secours\cle-editeur.pem
   ```
   **Attendu** : la même ligne `MFkw…` que dans `cle-editeur-publique.txt` du dossier.
5. Copier chaque `cle-editeur.pem` sur **sa propre clé USB**, rangées dans deux lieux différents ; noter les deux mots de passe à part.

## 2. Intégration des clés publiques au programme (une seule fois)

Dans `src/Pharma.Application/Offre/ActivationOffre.cs`, classe `CleEditeur` :

- `PrincipaleBase64` = contenu de `C:\MesAtlassian\cle-editeur\cle-editeur-publique.txt` ;
- `SecoursBase64` = contenu de `C:\MesAtlassian\cle-secours\cle-editeur-publique.txt`.

Puis tests (`OffreTests` vérifie que les deux clés sont lisibles et différentes), commit et livraison de cette version à toutes les
pharmacies. **Tant que ces constantes sont vides, aucun code n'est accepté** : offre de base, 2 postes, écrans des packs fermés.

## 3. Signer un code pour une pharmacie

> Pas à pas détaillé (chaque paramètre, saisie dans Administration › Mon offre, messages de refus) : [signer-code-activation.md](signer-code-activation.md).

1. Relever, dans l'écran **Administration › Mon offre** de la pharmacie (titulaire) : le **code de l'officine**, le **NIF** et
   l'**empreinte de ce serveur** (boutons « Copier »). Le NIF doit être saisi avant (Administration › Langue et pharmacie) : un code est lié
   aux deux. Empreintes du 2e serveur et du PC de secours : sur chacun, `Pharma.Api.exe --empreinte-serveur` (après
   `Pharma.Api.exe --creer-cle-serveur`, en administrateur, à l'installation ; voir [mise-en-production.md](mise-en-production.md)).
2. Signer avec la **clé principale** (le mot de passe est demandé) :
   ```powershell
   cd C:\MesAtlassian\pharmaofficine
   dotnet run --project src\Pharma.Editeur -- signer --cle C:\MesAtlassian\cle-editeur\cle-editeur.pem `
       --officine <code de l'officine> --nif <NIF> --postes <n> --serveur <empreinte> [--serveur <empreinte> …] `
       [--packs A,D | --packs tous] [--scanners <n>] [--fin AAAA-MM-JJ] [--transition]
   ```

   | Option | Sens |
   |---|---|
   | `--postes <n>` | Nombre de postes autorisés (1 à 200), **obligatoire** |
   | `--serveur <empreinte>` | Empreinte d'un serveur de l'officine (principal, 2e serveur, PC de secours), **obligatoire**, répétable 1 à 3 fois ; tirets et casse indifférents. L'outil ne signe plus que des codes version 2 (liés aux serveurs, décision du 2026-11-27) |
   | `--packs A,D` | Packs vendus (A à H, séparés par des virgules) ; `tous` = les 8 ; absent = aucun pack |
   | `--scanners <n>` | Scanners mobiles (0 par défaut) |
   | `--fin AAAA-MM-JJ` | Dernier jour de l'offre (abonnement) ; absent = définitive. Rappels au titulaire (SMS ou e-mail) 90, 60, 30, 15, 7 et 1 jour(s) avant la fin, puis au début des 15 jours de grâce |
   | `--transition` | Code « tout compris » de la mise en place ou de la mise à jour : les 8 packs, `--fin` obligatoire |

   **Attendu** : une ligne de résumé (pharmacie, NIF, postes, packs, fin), la liste des serveurs autorisés, puis le code `PO1.…`. Le code
   n'est pas secret : il ne vaut que pour cette pharmacie (code de l'officine et NIF) et sur les serveurs déclarés.
3. Relire un code avant de l'envoyer (contenu, serveurs autorisés et signature) :
   ```powershell
   dotnet run --project src\Pharma.Editeur -- lire --code <PO1.…> --cle-publique (Get-Content C:\MesAtlassian\cle-editeur\cle-editeur-publique.txt)
   ```

### Codes de transition des bases de l'éditeur

| Base | Code de l'officine | NIF |
|---|---|---|
| `pharmacie_dev` | `1ea51897-2885-4e89-bc0c-89765d819402` | `000016000000000` |
| `pharmacie_test` | `142d3c7d-c7a3-4e40-84dc-04d4440af2dc` | `000016000000000` |

```powershell
dotnet run --project src\Pharma.Editeur -- signer --cle C:\MesAtlassian\cle-editeur\cle-editeur.pem --officine 1ea51897-2885-4e89-bc0c-89765d819402 --nif 000016000000000 --postes 20 --serveur <empreinte du PC de développement> --transition --fin 2027-12-31
dotnet run --project src\Pharma.Editeur -- signer --cle C:\MesAtlassian\cle-editeur\cle-editeur.pem --officine 142d3c7d-c7a3-4e40-84dc-04d4440af2dc --nif 000016000000000 --postes 20 --serveur <empreinte du PC de développement> --transition --fin 2027-12-31
```

Les codes version 1 déjà signés pour ces bases restent acceptés (Mon offre : « Code non lié au serveur ») ; pour les remplacer, créer la clé du
PC de développement (`Pharma.Api.exe --creer-cle-serveur`, en administrateur), lire son empreinte (`--empreinte-serveur`) et signer ci-dessus.

## 4. Faire parvenir le code à la pharmacie

- **Sur place** : Administration › **Mon offre**, avec le compte du titulaire (par le titulaire, ou par l'éditeur à ses côtés : la saisie
  est réservée au titulaire), coller le code dans « Code d'activation », **Activer**.
  **Attendu** : « Code accepté : Offre activée (ou Période de transition), N postes » ; les écrans des packs se rouvrent (au plus 2 minutes
  sur les autres postes).
- **Par le serveur central** (pharmacie synchronisée), sur le serveur central :
  ```powershell
  Pharma.Cloud.Api.exe --ordre-activation --officine <code de l'officine> --code <PO1.…> --auteur <nom>
  ```
  Le central transmet sans rien signer ; l'officine contrôle et applique au prochain échange.

**Refus possibles à la saisie** : autre pharmacie (code de l'officine), autre NIF, signature fausse, **serveur non déclaré dans le code**
(« Ce serveur n'est pas déclaré dans le code de l'offre : demandez un nouveau code à l'éditeur avec l'empreinte … »), code échu (délai de
grâce passé, compté au jour effectif : reculer l'horloge n'y change rien), code plus ancien que celui en place, code déjà enregistré. Rien ne
change dans ces cas.

## 5. Vie de l'offre

- **Vente d'une option, ajout de postes, renouvellement** : signer un **nouveau** code avec tout le contenu voulu (le code ne s'ajoute pas
  au précédent : le plus récent remplace l'offre).
- **Fin d'abonnement** : alerte au titulaire par SMS ou e-mail (mêmes destinataires que les alertes techniques) 90, 60, 30, 15, 7 et 1 jour(s) avant la fin,
  puis au début des 15 jours de grâce, une seule fois chacune pour une date de fin donnée (seule l'étape la plus proche est envoyée ;
  `RappelsOffre`, `src/Pharma.Application/Offre/Offre.cs`) ; l'écran Mon offre affiche son message dès 90 jours ; après la grâce, offre de base ; aucune donnée effacée ; un nouveau code
  rouvre tout.
- **Changement de NIF** de la pharmacie : l'offre retombe à la base ; signer un nouveau code avec le nouveau NIF.
- **Remplacement d'un serveur** (PC neuf, Windows réinstallé, restauration sur un autre PC, nouveau 2e serveur ou PC de secours) : sa clé
  change, donc signer un **nouveau code** avec les empreintes à jour ; en attendant, 15 jours de grâce depuis la première constatation, puis
  offre de base. Déclarer le 2e serveur et le PC de secours **dès le premier code**.

## 6. Perte ou vol d'une clé

| Situation | Que faire |
|---|---|
| **Clé principale perdue** (fichier ou mot de passe) | Signer avec la **clé de secours** (`--cle C:\MesAtlassian\cle-secours\cle-editeur.pem`) : aucune nouvelle version, aucune pharmacie ne retombe à la base. À la version suivante : générer une nouvelle clé (§ 1), elle devient la principale ou la secours. |
| **Clé principale volée** | Signer avec la clé de secours ; à la version suivante, **retirer la principale** de `CleEditeur` (ses codes ne valent plus : nouveaux codes, signés avec la clé de secours, pour les pharmacies qui en avaient un) et générer une nouvelle clé de secours. |
| **Clé de secours perdue ou volée** | Rien ne change pour les pharmacies ; à la version suivante, la remplacer par une nouvelle clé. |
| **Les deux perdues** | Générer deux nouvelles clés, livrer une nouvelle version, signer un nouveau code pour **chaque** pharmacie (sinon elles retombent à l'offre de base). |
