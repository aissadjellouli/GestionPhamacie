# 2e serveur en local (poste de développement)

Journal de l'installation d'un 2e serveur **sur un seul PC de développement** le 2026-11-22, puis de l'essai de bascule.
Pour une officine réelle (deux PC), la procédure de référence reste celle de [mise-en-production.md](mise-en-production.md),
« 2e serveur », avec les scripts de `scripts/second-serveur/`.
Règles métier correspondantes : [regles-metier.md](regles-metier.md) (« Réplication en continu vers le 2e serveur », « 2e serveur facultatif »).

## 1. Principe retenu

| Élément | Principal | 2e serveur (local) |
|---|---|---|
| PostgreSQL | instance de développement, port **5432** (service `postgresql-x64-18`) | seconde instance, port **5433**, dossier `C:\MesAtlassian\pg-second-serveur`, démarrée par `pg_ctl` (aucun service Windows) |
| API | 5080 / 5081 (Visual Studio) | **5082**, lancée par `dotnet Pharma.Api.dll` |
| Données de l'API (GED, journaux, Chifa) | `C:\ProgramData\PharmaOfficine` | `C:\MesAtlassian\pg-second-serveur-api` |
| Secret de réplication | — | `C:\MesAtlassian\pg-second-serveur-secret\pgpass.conf` (réservé au compte Windows) |

Les scripts de `scripts/second-serveur/` n'ont pas été utilisés tels quels : ils attendent deux PC (adresses IPv4, services
Windows, dossier des données lu dans le registre) et le mot de passe du superutilisateur `postgres`, absent du poste
(aucun `pgpass.conf`). Les mêmes étapes ont été faites à la main, sans le compte `postgres`.

La réplication physique copie **toute l'instance** (toutes les bases du 5432 : `pharmacie_dev`, `pharmacie_test`,
`pharmacie_cloud_dev`, bases jetables…), 1,71 Go le 2026-11-22.

## 2. Vérifications préalables (lecture seule)

Faites avec le compte de l'API (`pharma_app`, mot de passe lu dans `src/Pharma.Api/appsettings.Development.json`, jamais affiché) :

```powershell
$psql = 'C:\Program Files\PostgreSQL\18\bin\psql.exe'
$cs = (Get-Content src\Pharma.Api\appsettings.Development.json -Raw | ConvertFrom-Json).ConnectionStrings.Pharmacie
$env:PGPASSWORD = ([regex]::Match($cs,'Password=([^;]*)')).Groups[1].Value
& $psql -h localhost -p 5432 -U pharma_app -d pharmacie_test -w -qtA -c "SELECT name||'='||setting FROM pg_settings WHERE name IN ('wal_level','max_wal_senders','max_replication_slots','hot_standby','listen_addresses','max_slot_wal_keep_size') ORDER BY 1"
Remove-Item Env:\PGPASSWORD
```

Constats du 2026-11-22 :

| Point | Valeur | Conséquence |
|---|---|---|
| PostgreSQL | 18.6 | — |
| `wal_level` | `replica` | **aucun redémarrage** du 5432 |
| `max_wal_senders`, `max_replication_slots` | 10 | suffisant |
| `hot_standby` | `on` | copie lisible |
| `listen_addresses` | `*` | rien à changer |
| `max_slot_wal_keep_size` | `-1` (sans limite) | limite de 20 Go **non posée** (compte `postgres` requis, voir § 7) |
| `pg_hba.conf` | `host replication all 127.0.0.1/32 scram-sha-256` déjà présent | **aucune modification** |
| Slot, rôle de réplication | absents | à créer |
| Ports 5433 et 5082 | libres | — |
| Disque C: | 1 533 Go libres | — |

Constat annexe : sur ce PostgreSQL de développement, `pharma_app` a les attributs **CREATEROLE, CREATEDB et REPLICATION**
(aucun script du dépôt ne les donne ; origine inconnue). Ils ont servi aux étapes 3 et 4 faute du compte `postgres`.
Retrait à décider (par `postgres` : `ALTER ROLE pharma_app NOCREATEROLE NOCREATEDB NOREPLICATION`).

## 3. Rôle de réplication dédié (sur le 5432)

Rôle `pharma_replication` (LOGIN REPLICATION), créé par `pharma_app` (CREATEROLE) ; mot de passe de 28 caractères tiré au
sort, jamais affiché, écrit seulement dans un fichier `pgpass.conf` dont le dossier n'est lisible que par le compte Windows :

```powershell
# mot de passe tiré au sort dans $mdp (RandomNumberGenerator), passé à psql par une variable (-v p=...)
SELECT format('CREATE ROLE pharma_replication LOGIN REPLICATION PASSWORD %L', :'p')
WHERE NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'pharma_replication') \gexec

# fichier du mot de passe (format pgpass : hôte:port:base:compte:mot de passe)
Set-Content C:\MesAtlassian\pg-second-serveur-secret\pgpass.conf "localhost:5432:*:pharma_replication:$mdp" -Encoding ascii -NoNewline
icacls C:\MesAtlassian\pg-second-serveur-secret /inheritance:r /grant:r "$($env:USERNAME):(OI)(CI)F"
```

Ensuite, chaque commande du rôle passe par `$env:PGPASSFILE = 'C:\MesAtlassian\pg-second-serveur-secret\pgpass.conf'`.

## 4. Slot de réplication (sur le 5432)

```sql
SELECT pg_create_physical_replication_slot('second_serveur', true)
WHERE NOT EXISTS (SELECT 1 FROM pg_replication_slots WHERE slot_name = 'second_serveur');
```

Le slot oblige le principal à garder les journaux de modifications que la copie n'a pas encore reçus.

## 5. Copie de la base (port 5433)

```powershell
$bin = 'C:\Program Files\PostgreSQL\18\bin'
$env:PGPASSFILE = 'C:\MesAtlassian\pg-second-serveur-secret\pgpass.conf'
& "$bin\pg_basebackup.exe" -h localhost -p 5432 -U pharma_replication -w -D C:\MesAtlassian\pg-second-serveur -X stream -S second_serveur -R -c fast -P
```

- `-R` écrit `standby.signal` et `primary_conninfo` dans `postgresql.auto.conf` ; le mot de passe n'y figure pas
  (`passfile=` renvoie au fichier protégé) ; `primary_slot_name = 'second_serveur'`.
- Volume copié : 1,8 Go (1 823 258 kB).

Réglages propres à la copie, ajoutés à la fin de `C:\MesAtlassian\pg-second-serveur\postgresql.auto.conf` :

```
port = '5433'
listen_addresses = 'localhost'
```

Démarrage (sous le compte Windows, sans service) :

```powershell
& "$bin\pg_ctl.exe" start -D C:\MesAtlassian\pg-second-serveur -l C:\MesAtlassian\pg-second-serveur-secret\demarrage.log -w -t 60
```

Remarque : lancée depuis un outil qui attend la fin de la sortie, la commande ne rend pas la main (les processus de PostgreSQL
gardent la sortie ouverte) ; le serveur est pourtant démarré. Vérifier par `netstat -ano | Select-String ":5433"`.

Journal de démarrage constaté : « entre en mode standby », « le système de bases de données est prêt pour accepter les connexions
en lecture seule », « démarré le flux des journaux depuis le principal ».

## 6. Vérification de la réplication

| Contrôle | 5432 | 5433 |
|---|---|---|
| `SELECT pg_is_in_recovery()` | `false` | `true` (lecture seule) |
| Ventes de `pharmacie_test` (`count(*)`, `max(id)`) | 3 335 / 3307262 | 3 335 / 3307262 |
| Slot `second_serveur` | actif | — |

Le détail de `pg_stat_replication` (retard en octets) n'est pas lisible sans `pg_monitor` (voir § 7).

## 7. Non fait faute du compte `postgres`

Commandes prêtes, à lancer par le titulaire avec `psql -U postgres` (terminal normal, deux mots de passe demandés) :

```sql
ALTER SYSTEM SET max_slot_wal_keep_size = '20GB';
SELECT pg_reload_conf();
GRANT pg_monitor TO pharma_app;
```

- **Limite de 20 Go** : sans elle, si la copie est arrêtée et oubliée alors que le slot existe, les journaux s'accumulent sans
  fin dans `C:\Program Files\PostgreSQL\18\data\pg_wal` jusqu'à remplir le disque, ce qui arrête le PostgreSQL principal
  (toutes les bases). Avec elle, au-delà de 20 Go le principal libère ces journaux et la copie est à refaire.
  Règle tant que la limite n'est pas posée : **copie arrêtée durablement ⇒ slot supprimé**.
- **`pg_monitor`** : nécessaire pour que l'écran « 2e serveur » affiche le retard de la réplication.

## 8. Bascule (2026-11-22, « go bascule »)

### 8.1 Promotion de la copie

```powershell
& "$bin\pg_ctl.exe" promote -D C:\MesAtlassian\pg-second-serveur -w -t 60
```

- Dernière transaction reçue avant la promotion : 22/11/2026 14:24:33.
- Après : `pg_is_in_recovery()` = `false`, timeline **2** (le 5432 reste en timeline 1).

**Écart avec la procédure réelle** : `basculer-second-serveur.ps1` refuse de basculer tant que l'ancien principal répond. Ici
l'ancien principal (5432) est l'instance de développement qui porte toutes les bases : il n'a **pas** été arrêté. Les deux
bases divergent, sans risque : dossiers séparés et aucun poste relié à l'API 5082.

### 8.2 Slot supprimé (sur le 5432)

Après la promotion, la copie ne lit plus le slot : le garder ferait accumuler les journaux sur le 5432.

```sql
SELECT pg_drop_replication_slot('second_serveur') FROM pg_replication_slots WHERE slot_name = 'second_serveur' AND NOT active;
```

### 8.3 Coupures dans la copie seulement (base `pharmacie_test` du 5433)

Pour que la seconde API n'envoie rien en double depuis une base divergente :

```sql
UPDATE sync_parametre SET active = false WHERE id = 1;                       -- envoi au serveur central coupé
UPDATE parametre_notification SET smtp_serveur = NULL, modem_port = NULL;    -- e-mails et SMS coupés (2 destinataires réglés)
```

Vérifié sur le 5432 après coup : envoi au central actif, SMTP et modem toujours réglés (rien changé au principal).

### 8.4 API sur le port 5082

Lancée hors Visual Studio, depuis **une copie** de la sortie de compilation (`C:\MesAtlassian\pg-second-serveur-api\bin`),
avec des variables d'environnement propres au processus :

```powershell
$r = 'C:\MesAtlassian\pg-second-serveur-api'   # sous-dossiers GED, Journaux, Chifa créés avant
dotnet build src\Pharma.Api\Pharma.Api.csproj -v q -nologo
robocopy 'src\Pharma.Api\bin\Debug\net10.0' "$r\bin" /MIR /NFL /NDL /NJH /NJS /NP   # code 1 = fichiers copiés
$out = "$r\bin"
$cs = (Get-Content src\Pharma.Api\appsettings.Development.json -Raw | ConvertFrom-Json).ConnectionStrings.Pharmacie
$env:ConnectionStrings__Pharmacie = $cs -replace 'Port=5432', 'Port=5433'
$env:ASPNETCORE_ENVIRONMENT = 'Development'; $env:Urls = 'http://localhost:5082'
$env:Donnees__Racine = $r; $env:Ged__Dossier = "$r\GED"; $env:Journaux__Dossier = "$r\Journaux"; $env:Chifa__DossierExport = "$r\Chifa"
Start-Process dotnet -ArgumentList 'Pharma.Api.dll' -WorkingDirectory (Resolve-Path $out) -WindowStyle Hidden `
    -RedirectStandardOutput "$r\console.log" -RedirectStandardError "$r\console-erreurs.log"
```

**Pourquoi une copie** : au premier lancement (depuis `src\Pharma.Api\bin\Debug\net10.0`), l'API 5082 verrouillait les DLL
de la sortie de compilation ; toute compilation de l'API (Visual Studio, `dotnet build`, `dotnet test` qui recompile) échouait
avec « Le fichier est verrouillé par .NET Host ». Corrigé le 2026-11-22 : API arrêtée, sortie copiée, API relancée depuis la
copie ; compilation complète de l'API vérifiée pendant qu'elle tourne. Conséquence : la copie ne suit pas le code ; pour que
l'API 5082 prenne une nouvelle version, l'arrêter, recompiler, refaire le `robocopy`, la relancer.

Pourquoi des dossiers séparés : avec le dossier GED partagé, la purge des sauvegardes (7 jours) de la seconde API toucherait
celles de l'API principale ; le journal en fichiers serait mélangé.

`SecondServeur:AutreServeur` laissé vide : ce réglage ne change que l'adresse et garde le port de la chaîne de connexion ;
avec `localhost`, l'API verrait sa propre base comme « l'autre principal » et refuserait de démarrer. Le contrôle « jamais deux
principaux » n'est donc pas essayable sur un seul PC.

Rappel : une API branchée sur une copie **encore en lecture seule** refuse de démarrer (« base en lecture seule », `SecondServeur.cs`) ;
l'API 5082 ne peut tourner qu'après la promotion.

Constats :

- `GET http://localhost:5082/api/healthcheck` : 200 ;
- journal : « Serveur ADH déclaré principal (API 2026.11) », « Alertes générées ou mises à jour : 114 », aucun avertissement ni erreur ;
- journal de l'API principale (`C:\ProgramData\PharmaOfficine\Journaux`) non touché.

## 9. Pas encore essayé

- Bascule d'un poste par le bouton « Basculer sur le 2e serveur » du bandeau « Serveur injoignable » (`AdresseApiSecours`).
- Connexion, lecture et vente d'essai sur l'API 5082 ; écran « 2e serveur ».
- Retour de l'ancien principal en 2e serveur (`remettre-en-second.ps1`).

## 10. Retrait complet

1. Arrêter l'API 5082 (processus `dotnet Pharma.Api.dll` lancé depuis `C:\MesAtlassian\pg-second-serveur-api\bin`).
2. `pg_ctl stop -D C:\MesAtlassian\pg-second-serveur -m fast`.
3. Vérifier qu'aucun slot ne reste sur le 5432 : `SELECT slot_name FROM pg_replication_slots;` (sinon `pg_drop_replication_slot`).
4. Supprimer le rôle : `DROP ROLE pharma_replication;` (par `pharma_app` CREATEROLE ou `postgres`).
5. Supprimer les dossiers `C:\MesAtlassian\pg-second-serveur`, `C:\MesAtlassian\pg-second-serveur-api` et
   `C:\MesAtlassian\pg-second-serveur-secret`.

Aucune modification à défaire sur le 5432 en dehors du rôle : ni `postgresql.conf`, ni `pg_hba.conf`, ni redémarrage.

## 11. État au 2026-11-22 (après-midi) : retiré

Retrait fait sur demande du titulaire (« go retrait 2e serveur »), sans essai du poste sur l'API 5082 :

- API 5082 arrêtée, instance 5433 arrêtée (`pg_ctl stop -m fast`) ; ports 5082 et 5433 libres ;
- aucun slot sur le 5432, aucune connexion de `pharma_replication` ; rôle supprimé (`DROP ROLE` par `pharma_app`) ;
- dossiers supprimés : `pg-second-serveur` (1,8 Go), `pg-second-serveur-api` (sortie copiée, GED, journaux, et la clé
  `cle-integrite.dat` créée par l'API 5082 pour son propre dossier, jamais utilisée par le principal), `pg-second-serveur-secret`.

Restent sur le 5432 : les droits CREATEROLE, CREATEDB et REPLICATION de `pharma_app` (antérieurs à cet essai, § 2). Pour
réinstaller : reprendre les § 3 à 8, avec la sortie de l'API copiée dès le départ (§ 8.4).

## 12. Réplication synchrone par transaction (lot V2-7) : réglage et procédure d'essai

### 12.1 Ce que fait l'API
- Seules les transactions de **vente** (`VentesEcritureSql.EnregistrerAsync`), de **facture** (`FacturesSql.EmettreAsync`, `CorrigerAsync`) et de
  **registre des psychotropes** (`NomenclatureSql.ChangerClasseAsync`, solde d'ouverture ; les délivrances sont dans la transaction de la vente)
  passent par `ReplicationSynchrone` (`src/Pharma.Infrastructure/SecondServeur/ReplicationSynchrone.cs`).
- Au début de la transaction : une lecture (`pg_stat_replication` non vide **et** `synchronous_standby_names` non vide). Si oui seulement :
  `SET LOCAL synchronous_commit = remote_write` (valable pour cette transaction, retombe à la validation). Sinon : **aucun SET**, rien ne change
  (officine à un seul serveur, ou 2e serveur arrêté : jamais de blocage des ventes).
- Chien de garde : si la validation dure plus de **2 s** (2e serveur décroché pendant l'attente), l'API annule l'attente
  (`pg_cancel_backend` sur son propre processus, seulement s'il est en attente `SyncRep`) : la transaction **reste validée sur ce serveur**,
  la réplication continue en asynchrone. Le compteur `ReplicationSynchrone.Abandons` augmente et l'événement `AttenteAbandonnee` est levé
  (à brancher sur l'alerte au titulaire, lot suivant). Le compte `pharma_app` annule ses propres processus : aucun droit supplémentaire.

### 12.2 Réglage serveur proposé (non appliqué ; à lancer par le titulaire avec `psql -U postgres`)
Sur le principal (5432) :
```sql
ALTER SYSTEM SET synchronous_standby_names = 'second_serveur';
ALTER SYSTEM SET synchronous_commit = 'local';   -- défaut : aucune attente, sauf transaction qui demande remote_write
ALTER SYSTEM SET wal_sender_timeout = '10s';     -- ajouté le 2026-11-25 (essai réel, § 17) : réseau figé, copie écartée en 10 s
SELECT pg_reload_conf();
```
Sur le 2e serveur, `primary_conninfo` doit contenir `application_name=second_serveur` (sinon le principal ne reconnaît pas la copie
comme serveur synchrone : aucune attente, donc aucun effet, mais aucune garantie non plus). `pg_monitor` reste utile pour l'écran « 2e serveur ».
Attention : sur l'instance de développement, le réglage est **global** (toutes les bases du 5432) ; seules les transactions ci-dessus
demandent `remote_write`, les autres restent en `local`. Retour arrière : `ALTER SYSTEM RESET synchronous_standby_names;` puis `pg_reload_conf()`.

### 12.3 Procédure d'essai réel (jouée au lot V2-8, après réinstallation du 2e serveur, § 3 à 8)
Mesures sur une base d'essai (pas `pharmacie_dev`), 100 encaissements espèces d'une ligne par scénario, API en 5080 ; relever la durée
médiane et le 95e centile (journal des requêtes de l'API ou chronomètre du script d'essai).

| # | Scénario | Réglage | Attendu |
|---|---|---|---|
| 1 | Référence | `synchronous_standby_names` vide, 2e serveur arrêté | durée de base D0 |
| 2 | 2e serveur connecté, `local` partout | réglage 12.2 sans remote_write (API d'avant le lot) | proche de D0 |
| 3 | 2e serveur connecté, API du lot (`remote_write`) | réglage 12.2 | D0 + quelques ms ; relever le surcoût |
| 4 | Comparaison | `ALTER SYSTEM SET synchronous_commit = 'on'` temporaire | plus lent que 3 (attente du rejeu) ; remettre `local` |
| 5 | **2e serveur arrêté** (`pg_ctl stop`) | réglage 12.2 | **aucun blocage** : durée ≈ D0, aucun SET (vérifier `pg_stat_replication` vide) |
| 6 | **Coupure pendant l'attente** : lancer des encaissements en boucle, arrêter le 2e serveur (`-m immediate`) ou couper le réseau en cours | réglage 12.2 | au plus une vente retardée d'environ 2 s, vente **validée**, `Abandons` +1, encaissements suivants sans attente |
| 7 | Perte en cas de panne du principal | réglage 12.2, 2e serveur connecté | arrêter brutalement le principal juste après une vente confirmée : la vente est présente sur le 2e serveur promu (zéro vente perdue attendue) |
| 8 | Retour du 2e serveur | réglage 12.2 | rattrapage, puis les transactions repassent en `remote_write` (vérifier `pg_stat_replication.sync_state = 'sync'`) |

Contrôles pendant l'essai : `SELECT application_name, state, sync_state FROM pg_stat_replication;` ;
`SELECT pid, wait_event FROM pg_stat_activity WHERE wait_event = 'SyncRep';` (doit être vide hors scénario 6) ;
`SHOW synchronous_commit;` (reste `local` en dehors des transactions).
Consigner les durées et les constats dans cette section après l'essai. Critères d'arrêt : un blocage de plus de 3 s au scénario 5 ou 6,
ou une vente perdue au scénario 7. **Joué le 2026-11-25 : résultats au § 17.**

## 13. Clé des jetons commune et signature entre serveurs (vague 2, lot V2-2)
- Les deux API doivent avoir la **même** `Jwt__Cle`. Sur un seul PC (deux instances) la variable est naturellement commune ; sur deux PC, `scripts/second-serveur/initialiser-second-serveur.ps1` exige `-EmpreinteJetonPrincipal` (affichée par `Pharma.Api.exe --empreinte-jeton` sur le principal), saisit la clé masquée et refuse de continuer si l'empreinte diffère.
- Les routes `/api/inter-serveurs/*` n'existent que si `SecondServeur:AutreServeur` est réglé (sinon 404). L'appel doit venir de l'adresse déclarée de l'autre serveur (réseau privé) et être signé (HMAC dérivé de `Jwt__Cle`). L'API de l'autre serveur est jointe en `https://<AutreServeur>:5443` ; réglage `SecondServeur:AutreServeurApi` (URL https) pour une autre adresse ou un autre port.
- Fenêtre d'horloge : `SecondServeur:Bascule:FenetreSignatureSecondes` (60 par défaut, 30 à 300). Deux PC dont les horloges diffèrent de plus d'une minute ne se reconnaissent pas : synchroniser l'heure Windows.
- Sur un seul PC, l'essai de la signature se fait en réglant `SecondServeur:AutreServeur` sur `127.0.0.1` (adresse de boucle locale admise) : attention, l'API verrait alors sa propre base comme « l'autre principal » (voir plus haut) ; l'essai complet reste réservé à la vague 2 (lot V2-8).

## 14. Liste des postes autorisés (2026-11-24)

En modes « transition » et « exigé » (script 167), la présence, les postes connus et les votes ne comptent que les **postes prouvés** (signature
vérifiée avec la clé publique de la copie répliquée). Tant qu'aucun poste n'est autorisé, aucun poste n'est connu : pas de promotion
automatique. Essai réel avec deux PostgreSQL : **fait le 2026-11-25 pour le cas 1** (§ 15), puis pour le relais (cas 3, réussi) et le retrait (cas 2, défaut grave) (§ 16).

## 15. Essai réel V2-8 (2026-11-25) : panne complète du serveur 1, promotion automatique, poste prouvé

Décisions du titulaire : postes autorisés réels (demande d'élévation de Windows acceptée), réplication synchrone **non essayée** (compte
`postgres` requis, réglage global), panne simulée sans arrêter le service 5432 (il porte toutes les bases), base jetable `pharmacie_essai_v28`.

**Montage (dossier `C:\MesAtlassian\essai-v28`, hors dépôt)** : copie physique de toute l'instance 5432 (1 254 Mo, rôle `pharma_replication`,
slot `second_serveur`, `application_name=second_serveur`) démarrée en 5433 par `pg_ctl` ; API serveur 1 en `http://localhost:5083` +
`https://localhost:5445` (base 5432), API 2e serveur en `5082` + `5446` (copie 5433), lancées par `lancer-api.ps1` (relance sur le code de sortie 10,
comme le service Windows) ; même `Jwt:Cle` tirée au sort ; `SecondServeur:AutreServeur` = `localhost:<port PostgreSQL>` (le « :port » est
accepté) et `SecondServeur:AutreServeurApi` en https ; `Donnees:Racine` propre à chaque API ; la clé `Urls` d'`appsettings.json` l'emporte sur
`ASPNETCORE_URLS` : variable `Urls`. Poste d'essai : `PHARMA_API=5083`, `poste.json` (`PHARMA_POSTE_REGLAGES`) avec `AdresseApiSecours=5082`,
identité dans `PHARMA_POSTE_IDENTITE`, moteur web propre (`WEBVIEW2_USER_DATA_FOLDER`). Droit `166b` donné par le titulaire dans pgAdmin :
**il est propre à chaque base** (accordé d'abord dans `pharmacie_dev` par erreur d'indication, puis dans `pharmacie_essai_v28`) ; les scripts de
MEP le passent bien sur la base de la pharmacie (`creer_base_officine.ps1`, `-Base`).

**Constats**

| # | Essai | Résultat |
|---|---|---|
| 1 | Autorisation du poste sur un compte Windows standard (D4 option 1) | Demande d'élévation acceptée ; clé créée dans le **TPM** (« Microsoft Platform Crypto Provider ») par le processus élevé, puis ouverte par le compte standard ; poste autorisé en 20 s ; requêtes signées acceptées (connexion du titulaire, accueil) |
| 2 | API du serveur 1 arrêtée, **base 5432 vivante** | Verdict « base vivante, API muette » (alerte `api_serveur1_arretee`) : **aucune promotion** (attendu, jamais deux principaux) ; poste : bandeau « Vérification avec le 2e serveur en cours » |
| 3 | 2e serveur relancé vers une base du serveur 1 jamais lue | Verdict « Promotion », puis **refus** `lignee_serveur1_inconnue` (attendu : jamais de promotion sans avoir lu la lignée du serveur 1) |
| 4 | Poste plus vu par le serveur 1 depuis plus de 3 min | Verdict « Attendre (aucun_poste_connu) » (attendu, `PosteActifMinutes`) |
| 5 | **Cas 1** : API du serveur 1 et accès à sa base coupés ensemble (relais TCP 5439 → 5432 arrêté) | Coupure 01:08:03 ; verdict « Promotion (cas_1_injoignable_majorite) », 1 poste connu, 1 vote prouvé ; **copie promue à 01:09:38 (1 min 35)** ; API arrêtée (code 10) puis relancée en principal : timeline 2, 4 compteurs sautés avant toute écriture, `bascule_decision` enregistrée, titulaire prévenu |
| 6 | Poste après la promotion | Bandeau d'attente à 17 s, puis « Serveur injoignable » + bouton « Basculer sur le 2e serveur » à 106 s ; **aucune bascule automatique** (voir défaut) ; bouton : bascule sans redémarrage, `poste.json` échangé (`AdresseApi` 5082, secours 5083), message « Ce poste est passé sur le 2e serveur… » ; connexion du titulaire sur le serveur promu : preuve du poste acceptée (clé publique répliquée) |
| 7 | Ancien serveur 1 relancé | Démarre en **lecture seule**, « un autre serveur est devenu principal (timeline 1 contre 2) », santé `role=ecarte`, ligne `serveur_ecarte` nature `bascule` |

**Défaut trouvé, corrigé le 2026-11-25 (« go défaut basculer »)** : après une promotion du cas 1, aucun poste ne recevait la consigne `basculer`. Le 2e serveur
en veille répond « attendre » jusqu'à la promotion (`ConsigneDuVerdict` ne rend jamais `basculer`), puis, devenu principal, sa route
`/api/vigie/rapport` répond **404** (contrat § 2.2) : le poste n'a plus de consigne et reste sur le bandeau manuel. Le contrat prévoit pourtant
`basculer` « le 2e serveur est devenu principal ». Le cas 2 n'est pas touché (le serveur 1 écarté répond 503 `serveur_ecarte` motif `bascule`). Correction : un serveur principal (deux serveurs
déclarés, non écarté) répond à la vigie « basculer » au poste qui ne joint plus son serveur (`ConsigneDuVerdict.PourPrincipal`).

**Cas 1 rejoué avec la correction (même montage, copie 5433 refaite, écart de l'ancien serveur 1 levé par `--lever-ecart`)** : coupure 01:48:11,
promotion 01:49:43 (1 min 32), poste titulaire connecté : bandeau d'attente à 17 s, puis **bascule automatique à 101 s** (« Ce poste est passé sur
le 2e serveur, devenu serveur principal. Votre session et votre vente en cours sont conservées »), sans redémarrage ni nouvelle connexion,
`poste.json` échangé ; requêtes signées acceptées par le serveur promu. Captures `cas1e-*` (clair ; accueil sur le serveur promu en sombre).
Montage rangé ensuite (API, poste, copie arrêtés, slot supprimé).

**Non essayé** : réplication synchrone (§ 12.3), panier en cours rejoué sans double (aucun article dans la base jetable), relais (cas 3) et retrait
(cas 2) : voir § 16 ; copie de l'identité du poste sur un autre PC, captures des bandeaux « Vérification… » et « passé sur le 2e serveur » en sombre
(capturés en clair seulement).

**Écritures de l'essai dans `pharmacie_dev` (erreur de montage, 2026-11-25 de 00:22:57 à 00:27 environ)** : les deux premiers lancements
(substitution de la chaîne de connexion ratée sous PowerShell) ont branché les API d'essai sur `pharmacie_dev`. Constaté ensuite (lecture seule) :
`serveur_officine` (ADH) mis à jour (empreinte de clé de l'essai `687160665a9f8ae6`, au lieu de celle de l'API de développement ;
rétablie au prochain démarrage de cette API), une ligne `bascule_promotion` (« Première note : timeline courante gardée sans saut »), une alerte
non envoyée « Aucun serveur ne peut être désigné » (n° 11), alertes régénérées (109, `mise_a_jour_le`), dates de démarrage de `sync_parametre`.
Aucune ligne `serveur_ecarte`, aucun sceau, aucune vente. Le lanceur vérifie désormais que la base est `pharmacie_essai_v28` avant tout démarrage. **Nettoyage fait le 2026-11-25** (« go nettoyage dev », après sauvegarde complète `C:\MesAtlassian\sauvegardes-nettoyage-dev\pharmacie_dev-avant-nettoyage-2026-11-25.dump`) : alerte non envoyée n° 11 supprimée, empreinte de clé ADH remise à `3d9a11be1f399ad4`. Gardés : la ligne `bascule_promotion` (table en ajout seul, protégée par déclencheur ; même note que celle qu'écrit l'API de développement à sa mise en service), les dates de `mise_a_jour_le` des alertes et de démarrage de `sync_parametre` (valeurs d'avant inconnues, réécrites au prochain démarrage), le droit `pg_promote` (script 166b, normal en développement).

**Rangement fait** : API, poste et instance 5433 arrêtés ; slot `second_serveur` supprimé (sinon les journaux de la 5432 grossissent sans fin).
**Restaient** (retirés le 2026-11-25, voir fin du § 16) : base `pharmacie_essai_v28`, rôle `pharma_replication`, dossier `C:\MesAtlassian\essai-v28` (copie promue, secrets
de l'essai), droit `pg_promote` dans `pharmacie_dev` et `pharmacie_essai_v28`, clés TPM des postes d'essai `PharmaOfficine-Poste-d83c33c12993` et
`PharmaOfficine-Poste-9eb4e4bfa028` (suppression en administrateur).

## 16. Essai réel des cas 3 (relais) et 2 (retrait) (2026-11-25, « go cas 2 et 3 »)

**Montage** : celui du § 15, remis à zéro (`reinit.ps1`), avec **deux postes autorisés** (CAISSE-ESSAI, clé existante ; CAISSE-ESSAI-2, nouvelle
clé créée dans le TPM après demande d'élévation acceptée, autorisé par code), titulaire connecté sur les deux. Chaque poste joint l'API du serveur 1
par son propre relais TCP (`relais-tcp.mjs` 5447 et 5448 → 5083) : couper un relais = ce poste seul perd le serveur 1, alors que le 2e serveur joint
toujours le serveur 1 en direct (API 5445, base 5432). Suivi : `suivre2.ps1` (bandeaux des deux postes, verdict), captures `cas3-*`, `cas3r-*`, `cas2-*`.

| # | Essai | Résultat |
|---|---|---|
| 1 | Départ | 2 postes connus, verdict « Rester » |
| 2 | **Cas 3** : relais du poste A coupé (08:46:44) | Verdict « Relais (cas_3_quelques_postes) », 1 vote sur 2 ; poste A : bandeau « Ce poste ne joint plus le serveur principal : il passe par le 2e serveur… » à **24 s**, barre d'état « Connecté · par le 2e serveur (relais) » ; ses requêtes passent par le 2e serveur et sont acceptées par le serveur 1 (« Requêtes reçues via le relais du 2e serveur », en-têtes signés) ; titulaire prévenu (alerte « Un poste ne joint plus le serveur ») ; poste B inchangé |
| 3 | Relais du poste A rétabli (08:50:47) | **Retour à la voie directe à 128 s** (règle des 2 min), bandeau retiré, verdict « Rester » |
| 4 | **Cas 2** : relais des deux postes coupés (08:54:25) | Poste B : relais à 29 s puis attente ; **poste A : aucun vote reçu par le 2e serveur**, bandeau manuel « Serveur injoignable » à 73 s ; verdict « Relais » (1 vote sur 2) pendant 3 min, puis « RetraitPuisPromotion » quand le poste A est sorti des postes connus (connus 1, `PosteActifMinutes`) |
| 5 | Retrait puis promotion (08:57:45) | Retrait signé accepté par le serveur 1 (ligne `serveur_ecarte` nature `retrait`), preuves réunies, copie promue (timeline 2), API relancée en principal |
| 6 | **Après la promotion** | **Les deux serveurs sont écartés** (santé `role=ecarte` sur 5083 et 5082) : l'officine n'a plus de serveur en écriture ; aucun poste ne bascule |

**Défaut grave, corrigé le 2026-11-25 (« go défaut retrait »)** : la ligne de retrait écrite par le serveur 1 (preuve n° 2 : elle doit être visible dans la
copie avant la promotion) est **répliquée** dans la copie ; après la promotion, le serveur promu relit cette ligne active (`EcartsSql.LireActifAsync` :
toute ligne non levée, sans regarder la timeline) et se croit lui-même écarté (`GardienServeurEcarte.ChargerAsync`). La ligne porte pourtant
`serveur_retenu = localhost:5433` (le serveur promu) et `timeline_locale = 1` (timeline d'avant la promotion). Message du journal trompeur en plus :
« l'autre serveur a une timeline plus récente (0 contre 1) » (motif d'une bascule affiché pour un retrait). Le cas 1 n'est pas touché (l'écart y est
écrit par l'ancien serveur 1 dans sa propre base, après la promotion, donc jamais répliqué). Correction : une ligne active dont la timeline est plus
petite que celle de la base locale en écriture est levée en base et jamais restaurée (`GardienServeurEcarte.Perime`, règle métier) ; journal du
retrait corrigé (« mise hors jeu demandée par l'autre serveur »).

**Constat expliqué (« go vérif rapports »)** : pendant le cas 2, le 2e serveur n'a reçu aucun rapport compté du poste A alors que le poste B votait. Les
deux postes tournent sur le même PC (même adresse 127.0.0.1) et, coupés au même instant, envoient leur rapport toutes les 5 s ; la limite « un rapport
par seconde par adresse » (`VigieRapports`, réponse 429 non journalisée) pourrait refuser systématiquement le second. **Hypothèse non prouvée** :
aucune trace côté serveur à ce moment-là. **Confirmé** depuis : trace Debug des refus ajoutée, puis relais espions (`relais-espion.mjs`) qui notent
chaque rapport et chaque réponse. Rejeu raté : **22 rapports du poste A refusés**, aucun du poste B ; essai décisif (deux postes vus depuis
127.0.0.1) : après la coupure, poste A **15 réponses 429 sur 15**, poste B **35 réponses 200 sur 35** ; poste A sur le bandeau manuel, verdict « Relais ».
Les deux postes, coupés au même instant, rapportent au même rythme (~9 s : 5 s d'attente + 4 s de sonde en échec) : le second arrive toujours
moins d'une seconde après le premier, **refus permanent**. Avec une adresse par poste (poste A vu de 127.0.0.1, poste B de ::1) : les deux votent.
**Décision du titulaire (« go limite par poste », 2026-11-25)** : la limite d'un rapport par seconde est **par poste** pour un rapport authentifié
(poste prouvé ou jeton lié au poste), **par adresse** pour un rapport anonyme (`RegistreRapportsPostes.CleLimite`). Rejeu réel dans les conditions
qui échouaient (deux postes vus depuis 127.0.0.1, coupés ensemble, 20:36:05) : **36 réponses 200 sur 36 pour chaque poste**, aucun 429 ; 2 votes sur
2 connus, attente à 32 s, retrait puis promotion, **les deux postes basculés seuls à 114 s** ; serveur 1 écarté, serveur promu principal (captures `cas2p-*`). (Attention : le journal console de l'essai est mal encodé, chercher « Rapport refus ».)

**Cas 2 rejoué avec la correction** :

| # | Essai | Résultat |
|---|---|---|
| 7 | Mêmes adresses (19:59:04) | Poste A refusé (429), poste B vote ; retrait puis promotion à 20:01:14 quand le poste A sort des connus ; au redémarrage : « Écart du … (timeline 1) levé : écrit avant la promotion de cette base » ; **serveur promu en écriture** (timeline 2, 4 compteurs sautés), **serveur 1 écarté** (ligne `retrait` gardée) ; **les deux postes basculent seuls à 146 s** |
| 8 | Une adresse par poste (20:09:12) | **Cas 2 nominal** : 2 votes sur 2 connus, attente à 29 s, retrait puis promotion, **les deux postes basculés à 110 s**, session gardée |
| 9 | Deux postes depuis 127.0.0.1 (20:16:32) | Défaut de vote reproduit à l'identique (429 permanents pour le poste A) |

Captures `cas2r-*`, `cas2e-*`, `cas2d-*`.

**Retrait de l'essai (« go retrait essai », 2026-11-25)** : postes, API, relais et copie 5433 arrêtés ; slot `second_serveur` supprimé ; rôle
`pharma_replication` supprimé ; dossier `C:\MesAtlassian\essai-v28` supprimé (copie, scripts, secrets de l'essai), captures gardées dans
`C:\MesAtlassian\essai-v28-captures` (hors dépôt). **Restent** : base `pharmacie_essai_v28` (deux connexions pgAdmin du compte `postgres` ouvertes :
à fermer, puis `DROP DATABASE pharmacie_essai_v28`), clés TPM `PharmaOfficine-Poste-d83c33c12993` et `PharmaOfficine-Poste-9eb4e4bfa028` (demande
d'élévation annulée : suppression à faire en administrateur), droit `pg_promote` dans `pharmacie_dev` (gardé : script 166b).

## 17. Essai réel de la réplication synchrone et du panier pendant une bascule (2026-11-25, soir)

Décision du titulaire : « paire de copies » (le 5432 porte toutes les bases et ne peut pas être arrêté brutalement).

**Montage (dossier `C:\MesAtlassian\essai-sync`, hors dépôt)** : copie physique de toute l'instance 5432 par `pg_basebackup` avec le compte
`pharma_app` (aucun rôle, aucun slot, aucun réglage créé sur le 5432), démarrée en **principal d'essai sur 5434** ; dans cette copie seulement,
`pg_hba.conf` en `trust` limité à 127.0.0.1 et ::1 (superutilisateur sans le mot de passe `postgres`), script `166b` passé, envois coupés dans la
copie de `pharmacie_test` (§ 8.3). Copie répliquée **5435** faite depuis le 5434 (slot `second_serveur`, `application_name=second_serveur` ajouté
à la main : `pg_basebackup -R` ne l'écrit pas), reliée au 5434 par un relais TCP « gelable » (réseau figé sans coupure franche). API serveur 1 en
5083/5445 (base 5434), API 2e serveur en 5082/5446 (base 5435), relancées sur le code 10 ; poste CAISSE-SYNC autorisé (clé TPM, demande
d'élévation acceptée) joignant le serveur 1 par un relais (5447) dont on peut retenir les seules réponses. Mesures : script d'encaissements
par l'API (`/api/caisse/encaisser`, titulaire de démonstration, une ligne, espèces).

**Réplication synchrone (§ 12.3), 35 ventes par scénario**

| # | Scénario | Résultat |
|---|---|---|
| 1 | Un seul serveur (référence) | médiane 21,6 ms |
| 2 | 2e serveur connecté, `synchronous_standby_names` vide | 22,9 ms |
| 3 | 2e serveur connecté et attendu (`remote_write`) | 30,2 ms (≈ +7 ms) |
| 4 | `synchronous_commit = on` pour tout | 20,8 ms : peu significatif, l'API impose `remote_write` à ces transactions |
| 5 | 2e serveur arrêté, réglage gardé | 21,9 ms, aucune attente `SyncRep` |
| 6a | 2e serveur arrêté net en pleine série (`-m immediate`) | rien de visible (105 ms au plus) : la connexion tombe franchement |
| 6b | **Réseau figé** entre les serveurs | **défaut** : chaque vente à 2 s pendant 60 s (copie vue « sync » jusqu'à `wal_sender_timeout`) ; toutes validées, une seule alerte |
| 7 | **Panne brutale du principal** en pleine série | **92 ventes confirmées sur 92 présentes sur la copie**, dont la dernière (141 ms avant la panne) ; la vente en cours : erreur 500, absente partout |
| 8 | Retour du 2e serveur | `sync` en 1 s, retard nul |

**Défaut 6b corrigé (« go correctif sync A+C »)** : après un abandon, plus d'attente tant que la copie n'a pas écrit jusqu'à la position notée
(regles-metier.md, « Après une attente abandonnée… ») ; `wal_sender_timeout = 10s` (§ 12.2, mise-en-production.md parcours C étape 15).
**Rejoué** (nouvelle copie, réseau figé 25 s) : **une seule vente à 2,1 s**, puis 56 ventes à vitesse normale (médiane 77 ms) ; copie écartée par
PostgreSQL en 10 s ; après le dégel, `sync` en 1 s et l'attente reprend d'elle-même (au 2e gel : de nouveau une seule vente à 2 s).

**Panier pendant une bascule (cas 1)**

| # | Essai | Résultat |
|---|---|---|
| 1 | Panier VALASTAN, « Encaisser » avec la réponse retenue, puis panne complète du serveur 1 (base et API) | vente T-006322 enregistrée et répliquée avant la panne, réponse jamais reçue par le poste |
| 2 | Bascule | promotion à ≈ 109 s, poste basculé seul à ≈ 129 s, session et panier gardés |
| 3 | Renvoi automatique (Q25) | **défaut** : jamais fait. Le poste ne tentait la bascule qu'une fois, aussitôt l'échec (2e serveur encore en veille) |
| 4 | Nouveau clic « Encaisser » (même clé) | « Vente T-006322 encaissée », aucune vente en double (3 713 avant et après) |
| 5 | Nouvelle vente sur le serveur promu | T-106323 : compteur sauté à la promotion (script 164) |

**Défaut 3 corrigé (« go renvoi auto comme recommandé »)** : encaissement mis en suspens, renvoyé une fois dès la bascule ou le retour du serveur,
5 minutes au plus (regles-metier.md, « Encaissement interrompu par la panne »). **Rejoué** : vente T-006040 enregistrée et répliquée, réponse
retenue, panne ; panier figé avec le bandeau dès l'échec ; promotion ≈ 110 s, bascule ≈ 115 s, **vente renvoyée seule à ≈ 119 s**
(« Vente T-006040 encaissée … renvoyée automatiquement après la panne du serveur »), une seule T-006040 sur le serveur promu. Autre chemin :
API du serveur arrêtée, encaissement mis en suspens, API relancée : vente T-106041 partie seule 16 s après, une seule fois. Bandeau mesuré à
0 px à 1 366 × 768 et 1 536 × 890, clair et sombre (captures `suspens-*`, `renvoi-*`, `panier-*` dans `C:\MesAtlassian\essai-sync`, hors dépôt).

**Constat pour la mise en production, corrigé (« go script application_name »)** : `initialiser-second-serveur.ps1` et `remettre-en-second.ps1`
créent la copie avec `pg_basebackup -R`, qui n'écrit pas `application_name=second_serveur` : le principal ne voyait jamais la copie comme
synchrone. Les deux scripts l'ajoutent désormais avant le démarrage de PostgreSQL (`Set-NomCopieSynchrone`, `commun.ps1`, sans BOM, une seule
fois ; test `NomCopieSynchroneTests` sur le format réel). Non rejoué avec les scripts eux-mêmes (administrateur Windows et `postgres` requis).

**Pas de mise en attente pendant la panne (décision du titulaire)** : vérifié en réel sur le serveur promu : suspens puis annulation, bouton
« Attente » désactivé, Caisse quittée puis reprise avec l'API joignable : **aucune vente en attente créée** (10 avant, 10 après), panier rendu
avec sa clé et le message « Vente reprise : son encaissement avait été interrompu… », puis encaissé (T-106042) ; 0 px à 1 366 × 768 en clair.

**Rangement** : poste, API, relais et les PostgreSQL d'essai arrêtés (aucun port d'essai ouvert) ; rien créé sur le 5432. Copies de bases d'essai (`p`, `s`,
`p2`, `s2`) **supprimées** le 2026-11-25 ; restent dans `C:\MesAtlassian\essai-sync` les programmes compilés, les lanceurs et les captures.
**Reste** : la clé TPM du poste `CAISSE-SYNC` (suppression en administrateur).

## 18. Essai réel des cas « égalité » et « isolement » avec deux postes (2026-11-26, « continue l'essai bascule »)

**Montage** : serveur 1 (principal, PostgreSQL 5434, API 5083) et 2e serveur (veille, PostgreSQL 5435 en réplication, API 5082), chaque liaison
passant par un relais coupable (fichier `coupe-<port>`) : réplication 5439, liaisons entre serveurs 5449 à 5452, poste A `CAISSE-SYNC` → serveur 1
par 5447, poste B `CAISSE COMPTOIR 2` → serveur 1 par 5448. Les deux postes enrôlés (clés TPM, consentement Windows accepté) et connectés.
Suivi toutes les 10 s : rôle des deux serveurs (`/api/healthcheck`), verdict de la vigie du 2e serveur, bandeaux et barre d'état des postes.

**Cas égalité** (serveur 1 + poste B d'un côté, 2e serveur + poste A de l'autre : liaisons entre serveurs, réplication et relais du poste A coupés,
4 min) : le 2e serveur reçoit le vote du poste A (`votes 1`) mais **reste en veille** pendant toute la durée, verdict
`Attendre (poste_authentifie_joint_serveur1)` : un poste authentifié joint encore le serveur 1, donc pas de promotion. Le serveur 1 reste principal
et n'est pas écarté (il voit la moitié des postes). Poste A : « Le serveur principal ne répond plus. Vérification… », barre « Serveur injoignable » ;
poste B : « Connecté », sans interruption. Liaisons rétablies : verdict `Rester`, poste A « Connecté » en moins de 30 s.

**Cas isolement** (toutes les liaisons du serveur 1 coupées, postes compris, 5 min) : à +21 s le 2e serveur a les **2 votes** ; à **+72 s** le
serveur 1 s'écarte de lui-même, « Ce serveur est isolé (copie perdue et moins de la moitié des postes vus) : il passe en lecture seule, titulaire
prévenu » (`/api/healthcheck` : `ecarte`) ; à **+103 s** le 2e serveur est **principal** (un relevé « muet » à +92 s pendant la promotion). Les
deux postes affichent « Connecté · 2e serveur » sans intervention. Aucun moment avec deux principaux acceptant des écritures.

**Constat** : les deux cas se comportent comme le prévoient les § 2 à 5 ; aucune correction de code.
