# Préparer le PC d'un nouveau développeur

Liste complète, dans l'ordre, pour un développeur qui arrive avec **seulement Visual Studio et un accès au dépôt git**. Chaque
étape renvoie au guide détaillé quand il existe. Temps total : environ 1 h 30 (dont téléchargements), plus 30 min si Docker.

> Ce document ne contient **aucun mot de passe réel** : seuls figurent les mots de passe de développement déjà publics dans
> la documentation (bases locales fictives). Les accès réels (dépôt, serveurs, comptes de production) se demandent au titulaire
> du projet et ne s'écrivent jamais dans le dépôt.

---

## 0. À demander au titulaire avant de commencer

| Quoi | Pourquoi |
| --- | --- |
| Accès au dépôt git (Bitbucket, équipe du projet) avec droit de lecture et d'écriture | Cloner, créer des branches |
| Branche de travail à utiliser (aujourd'hui : `develop`) | Ne jamais travailler directement sur la branche principale |
| Règles de commit et de revue (qui pousse, qui relit) | Le titulaire pousse et publie lui-même |
| Droits administrateur sur le PC | Installer PostgreSQL, WSL 2, Docker |

Ne pas demander, et ne jamais recevoir sur un poste de développement : mots de passe de production, clés de l'autorité du
serveur central de production, certificats d'officines réelles, données réelles de pharmacie.

---

## 1. Logiciels à installer

| Logiciel | Version | Remarque |
| --- | --- | --- |
| Windows | 10 (2004 ou plus) ou 11, 64 bits | Le poste client (WPF + WebView2) ne tourne que sous Windows |
| **Visual Studio** | Community 2026 (18.x) ou plus récent | Charges de travail : **Développement ASP.NET et web** et **Développement .NET Desktop** |
| **SDK .NET** | 10.0.100 ou plus récent (voir `global.json`) | Installé avec Visual Studio ; vérifier `dotnet --version` |
| **Git** | celui de Visual Studio ou Git pour Windows | Voir § 2 pour les réglages |
| **PostgreSQL** | 18.x (17 accepté) | Serveur + outils en ligne de commande, port 5432 : [poste-de-developpement.md](poste-de-developpement.md) § 2 |
| Runtime WebView2 | celui de Windows à jour | Déjà présent sur Windows 10/11 à jour |
| 7-Zip | dernière version | Facultatif : ouvrir l'archive de départ chiffrée (AES-256) du serveur central |
| Docker Desktop + WSL 2 | dernière version | Facultatif : démonstration en conteneurs, [installation-docker.md](installation-docker.md) |

Aucun autre outil n'est nécessaire (pas de Node.js : le poste client est en Blazor .NET).

---

## 2. Git : réglages et récupération du code

```powershell
git config --global user.name  "Prénom Nom"
git config --global user.email "adresse@exemple.invalid"
git config --global core.autocrlf true
```

`core.autocrlf true` est le réglage du poste de référence. Les fichiers qui doivent rester en fins de ligne Unix (scripts
shell de la démo `deploy/demo/init/*.sh`) sont protégés par un `.gitattributes` dans leur dossier : ne pas le retirer.

Cloner le dépôt (adresse fournie par le titulaire) dans un dossier **sans espace ni accent**, par exemple `C:\MesDev` :

```powershell
cd C:\MesDev
git clone <adresse du dépôt> PharmaOfficine
cd PharmaOfficine
git checkout develop
```

Dans Visual Studio : **Fichier › Ouvrir › Projet/Solution** › `PharmaOfficine.slnx` (ou `PharmaOfficine.sln` : les deux
existent ; en ligne de commande, toujours préciser lequel).

### Paquets NuGet

Le dépôt a son propre `NuGet.Config` : **seulement nuget.org**, sans identifiant. Rien à configurer.
Si la configuration NuGet **globale** du PC contient d'autres sources qui demandent un mot de passe (erreur `NU1301 … 401`),
compiler en ligne de commande avec `--source https://api.nuget.org/v3/index.json`. Ne jamais ajouter de source au
`NuGet.Config` du dépôt.

Premier contrôle :

```powershell
dotnet build PharmaOfficine.slnx -v q -nologo
```

Attendu : `0 Erreur(s)`.

---

## 3. Bases de données locales

Guide détaillé : [poste-de-developpement.md](poste-de-developpement.md) § 3.

```powershell
Set-ExecutionPolicy -Scope Process -ExecutionPolicy Bypass   # pour cette fenêtre seulement
.\scripts\initialiser_base_locale.ps1
```

Le script demande le mot de passe du super-utilisateur `postgres` (choisi à l'installation de PostgreSQL), crée le compte
applicatif **`pharma_app`** (mot de passe de développement **`pharma_dev`**), crée **`pharmacie_dev`** et **`pharmacie_test`**,
passe toutes les migrations `database/migration/NNN_*.sql` dans l'ordre, puis les données fictives `database/data/9xx_*.sql`.

### Connexion de l'API en développement

Créer **une fois** le fichier `src/Pharma.Api/appsettings.Development.json` (ignoré par git, jamais commité) :

```json
{
  "ConnectionStrings": { "Pharmacie": "Host=localhost;Port=5432;Database=pharmacie_test;Username=pharma_app;Password=pharma_dev" }
}
```

Les tests d'intégration lisent ce même fichier (ou la variable `PHARMA_TEST_CNX`) : ils écrivent dans **`pharmacie_test`**.
Pour faire tourner l'API de Visual Studio sur **`pharmacie_dev`** (cas du lancement avec le serveur central, § 6), le script
du § 6 pose la variable d'utilisateur `ConnectionStrings__Pharmacie`, qui l'emporte sur ce fichier.

### Nomenclature nationale et grand jeu de test

Le jeu fictif de grande taille (`905_jeu_de_test.sql` : 200 patients, environ 3 000 ventes sur 90 jours, 155 médicaments
réels, 35 produits de parapharmacie) ne se charge **que si la nomenclature nationale est importée**. Une fois l'API lancée
(§ 4) :

1. se connecter en `titulaire` / `demo` ;
2. **Administration › Nomenclature nationale › Importer le fichier Excel** : `database\data\NOMENCLATURE NATIONALE - VERSION JUIN 2026.xlsx` ;
3. arrêter l'API, puis relancer `.\scripts\initialiser_base_locale.ps1 -MiseAJour` (le jeu 905 se charge alors).

Faire de même pour chaque base (`pharmacie_dev`, `pharmacie_test`).

### Règles sur les bases de démonstration

- **Jamais** supprimer ni restaurer par-dessus les données de démonstration sans l'accord du titulaire.
- Avant de passer un script sur `pharmacie_dev` ou `pharmacie_test` : sauvegarde `pg_dump -Fc`, relue par `pg_restore -l`.
- Les tests créent et suppriment eux-mêmes des bases jetables `pharmacie_jetable_*` : normal. S'il en reste après une
  interruption, elles peuvent être supprimées (ce sont des copies d'essai).

---

## 4. Lancer l'API et le poste dans Visual Studio

1. Liste à côté du bouton ▶ : **« API + Poste client »** (défini dans `PharmaOfficine.slnLaunch`).
2. **F5** : l'API écoute sur http://localhost:5080 (contrôle : http://localhost:5080/api/healthcheck), puis la fenêtre du
   poste s'ouvre.
3. Comptes de démonstration : `titulaire`, `assistant`, `vendeur`, mot de passe `demo`.

Réglages facultatifs du poste (`PHARMA_API`, `PHARMA_PHARMACIE`, `PHARMA_POSTE`, imprimante d'étiquettes) :
[poste-de-developpement.md](poste-de-developpement.md) § 4.

**Une seule API par port** : un deuxième lancement sur 5080 (ligne de commande, démo Docker) échoue
(« address already in use »). Pour une deuxième instance : `dotnet run --project src\Pharma.Api -- --urls http://localhost:5081`
(l'argument de ligne de commande est prioritaire ; la variable `ASPNETCORE_URLS` ne suffit pas, `appsettings.json` règle déjà
`Urls`).

---

## 5. Tests

```powershell
dotnet test PharmaOfficine.slnx -v q --nologo
```

Projets : `Pharma.Domain.Tests`, `Pharma.Application.Tests`, `Pharma.Architecture.Tests` (lit les sources, les scripts et
la documentation ; une partie utilise PostgreSQL), `Pharma.Cloud.Tests`. Avec `PHARMA_EXIGER_BASE=1`, un PostgreSQL
injoignable fait échouer les tests de base au lieu de les sauter.

Arrêter le débogage (**Maj+F5**) avant de compiler : sinon `MSB3027 … fichier verrouillé`.

Tests SQL d'un script : `database/tests/NNN_*_tests.sql` (transaction annulée), par exemple :

```powershell
$env:PGPASSWORD = "pharma_dev"; $env:PGCLIENTENCODING = "UTF8"
& "C:\Program Files\PostgreSQL\18\bin\psql.exe" -h localhost -U pharma_app -d pharmacie_test -v ON_ERROR_STOP=1 -f database\tests\154_parapharmacie_catalogue_tests.sql
Remove-Item Env:\PGPASSWORD
```

---

## 6. Serveur central (facultatif)

Deux façons, au choix :

**a. Essai sans préparation** : `scripts\dev\essai_cloud_officine.ps1 -Demarrer` lance un central (port 5190) et une API
d'officine (port 5191) reliés entre eux, sur des **bases jetables** (les bases de démonstration ne sont pas touchées). Guide :
[essai-cloud-officine.md](essai-cloud-officine.md).

**b. Les trois projets dans Visual Studio, en débogage** (API locale + poste + central), profil
**« API + Poste + Central »** de `PharmaOfficine.slnLaunch`. Préparation **une fois par PC**, par
`scripts\dev\preparer_central_vs.ps1` :

0. **Base centrale de développement** `pharmacie_cloud_dev` (le script ne la crée jamais) : la créer puis y passer tous les
   scripts `database/cloud/001` à la dernière, dans l'ordre, comme indiqué dans
   [database/cloud/README.md](../database/cloud/README.md), « Essai local (poste de développement) ». Le script vérifie la
   présence du dernier script avant de continuer.
   Mot de passe de `pharma_app` : `$env:PGPASSWORD = "pharma_dev"` dans la fenêtre (sinon il est demandé, saisie masquée).
1. `-Preparer -WhatIf` (voir ce qui serait fait), puis `-Preparer` : dossier d'autorité de développement hors du dépôt
   (`%LOCALAPPDATA%\PharmaOfficine\central-dev`), mots de passe tirés au sort, variables **d'utilisateur Windows**
   (`PHARMA_CLOUD_DOSSIER`, `PHARMA_CLOUD_MOT_DE_PASSE`, `PHARMA_CLOUD_CONNEXION`, `ConnectionStrings__Pharmacie` vers
   `pharmacie_dev`, `Synchronisation__Dossier`), rôle de connexion `pharma_cloud_dev_vs`, autorité et certificat du
   serveur, officine « Développement (pharmacie_dev) » créée au central, **code d'activation affiché** (usage unique, 24 h).
   Prérequis : la base centrale `pharmacie_cloud_dev` (scripts `database/cloud/001` à la dernière ; voir
   [mise-en-production.md](mise-en-production.md), parcours du serveur central).
2. **Redémarrer Visual Studio** (il ne voit les nouvelles variables qu'au redémarrage).
3. Profil **« API + Poste + Central »** › **F5**. Le central écoute en HTTPS sur 127.0.0.1:5090.
4. Dans le poste, en `titulaire` : **Administration › Synchronisation** : adresse `https://127.0.0.1:5090/`, synchronisation
   activée, enregistrer, puis coller le **code d'activation** (`-Activer` réaffiche la marche à suivre et un code neuf si
   besoin ; `-Etat` montre où l'on en est).
5. Le premier envoi charge toute la base au central : quelques minutes. Arrêter le débogage interrompt l'envoi, qui reprend
   au lancement suivant.

**Effet à connaître** : tant que les variables sont posées, **toute** API lancée par ce compte Windows (profil
« API + Poste client », `dotnet run` compris) vise `pharmacie_dev` et non plus `pharmacie_test` : les variables
d'environnement passent avant `appsettings.Development.json`. Les tests, eux, ne sont pas concernés (ils lisent
`PHARMA_TEST_CNX` ou le fichier).

Retour en arrière : décocher d'abord « Synchronisation activée » à l'écran, puis `-Retirer -WhatIf` et `-Retirer`
(variables, rôle et dossier ; rien n'est supprimé en base). Détail et risques :
[poste-de-developpement.md](poste-de-developpement.md), « Lancer API + Poste + Central dans Visual Studio ».

Les secrets du central de développement restent **dans les variables d'utilisateur Windows de ce PC** : ne jamais les
recopier dans un fichier du dépôt, un ticket ou un message.

---

## 7. Démonstration en conteneurs (facultatif)

1. Installer WSL 2 et Docker Desktop : [installation-docker.md](installation-docker.md).
2. `Copy-Item deploy\demo\.env.example deploy\demo\.env`, remplacer chaque valeur `A-REMPLACER` (le fichier `.env` n'est
   **jamais** commité).
3. `docker compose -f deploy/demo/docker-compose.yml --env-file deploy/demo/.env up -d --build`.

Détail, état et points restant à vérifier : [demo-cloud.md](demo-cloud.md).

---

## 8. Règles du projet à connaître avant d'écrire du code

À lire en premier :

| Document | Contenu |
| --- | --- |
| [architecture.md](architecture.md) | Couches `Api` → `Application` → `Domain` ← `Infrastructure`, poste Blazor |
| [regles-metier.md](regles-metier.md) | Toutes les règles implémentées ; **toute nouvelle règle y est ajoutée dans la même modification** |
| [fonctionnalites.md](fonctionnalites.md) | Toutes les fonctionnalités (faites / en attente) ; **mis à jour à chaque modification** |
| [charte-graphique.md](charte-graphique.md) | Classes, tailles, couleurs, règle des listes ; écrans sans défilement à 1366 × 768 et 1536 × 890 |
| [base-de-donnees.md](base-de-donnees.md), [scripts-base-de-donnees.md](scripts-base-de-donnees.md) | Schéma, scripts, ordre de passage |
| [reste-a-faire-synthese.md](reste-a-faire-synthese.md) | Ce qui reste à faire, décisions du titulaire |
| [securite-et-droits.md](securite-et-droits.md) | Droits, actions sensibles |

Règles qui reviennent tout le temps :

- **Scripts SQL** : idempotents (rejouables sans effet), nom parlant, numéro suivant ; le **script d'init**
  (`database/init/schema.sql`, `seed_referentiel.sql`) mis à jour **dans la même modification** (un test le vérifie) ;
  passés sur `pharmacie_dev` et `pharmacie_test` après sauvegarde ; SQL toujours paramétré.
- **Écrans** : charte graphique, aucun défilement de page (seul un tableau défile dans son cadre), tableaux paginés avec
  filtres au-dessus, listes déroulantes seulement pour des valeurs bornées ; vérifier le rendu réel, thèmes clair et sombre,
  aux deux tailles.
- **Secrets** : jamais dans un fichier du dépôt (`appsettings.json` versionné a `Password=` vide) ; `appsettings.Development.json`
  et `deploy/demo/.env` restent locaux.
- **Jamais dans git** : vidéos (exclues par `.gitignore`), dossier `docs/taches/` (échanges de travail), sauvegardes de bases,
  fichiers `.env`.
- **Aucun nom de logiciel d'un autre éditeur** dans le code, les docs, les scripts ou les messages de commit (vérifié par le test `AucunNomTiersTests`).
- **Nouveau fichier `.md` ou `.ps1`** : l'ajouter aux **deux** solutions Visual Studio (`PharmaOfficine.slnx` et
  `PharmaOfficine.sln`, dossier correspondant) dans la même modification ; le test `SolutionFichiersTests` le vérifie.
- **Paquets NuGet** : jamais publiés depuis un poste de développement ; une nouvelle dépendance seulement sous licence
  permissive, version gérée dans `Directory.Packages.props`.
- **Port 5080** : c'est celui de l'API lancée dans Visual Studio ; pour un essai à côté, utiliser un autre port et arrêter
  ses propres instances après usage.
- Fin de chaque tâche : build et tests au vert, docs à jour (règles métier, fonctionnalités, reste à faire), liste des
  fichiers modifiés.

---

## 9. Liste de contrôle finale

- [ ] `dotnet --version` ≥ 10.0.100
- [ ] `psql --version` 18.x, service PostgreSQL démarré
- [ ] `dotnet build PharmaOfficine.slnx` : 0 erreur
- [ ] Bases `pharmacie_dev` et `pharmacie_test` créées, `appsettings.Development.json` présent (non commité)
- [ ] Nomenclature importée et jeu 905 chargé (facultatif mais conseillé)
- [ ] Profil « API + Poste client » : connexion en `titulaire` / `demo` réussie
- [ ] `dotnet test PharmaOfficine.slnx` : tout au vert
- [ ] (Facultatif) profil « API + Poste + Central » préparé et officine de développement activée
- [ ] (Facultatif) Docker Desktop installé, démo en conteneurs démarrée
- [ ] Documents du § 8 lus

---

## 10. Problèmes fréquents

Voir [poste-de-developpement.md](poste-de-developpement.md) § 6 (scripts PowerShell bloqués, PostgreSQL introuvable, accents,
WebView2, fichiers verrouillés, NuGet 401, serveur injoignable) et [installation-docker.md](installation-docker.md) § 8 pour
Docker.
