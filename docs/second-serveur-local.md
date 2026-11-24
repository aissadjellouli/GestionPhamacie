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
ou une vente perdue au scénario 7.

## 13. Clé des jetons commune et signature entre serveurs (vague 2, lot V2-2)
- Les deux API doivent avoir la **même** `Jwt__Cle`. Sur un seul PC (deux instances) la variable est naturellement commune ; sur deux PC, `scripts/second-serveur/initialiser-second-serveur.ps1` exige `-EmpreinteJetonPrincipal` (affichée par `Pharma.Api.exe --empreinte-jeton` sur le principal), saisit la clé masquée et refuse de continuer si l'empreinte diffère.
- Les routes `/api/inter-serveurs/*` n'existent que si `SecondServeur:AutreServeur` est réglé (sinon 404). L'appel doit venir de l'adresse déclarée de l'autre serveur (réseau privé) et être signé (HMAC dérivé de `Jwt__Cle`). L'API de l'autre serveur est jointe en `https://<AutreServeur>:5443` ; réglage `SecondServeur:AutreServeurApi` (URL https) pour une autre adresse ou un autre port.
- Fenêtre d'horloge : `SecondServeur:Bascule:FenetreSignatureSecondes` (60 par défaut, 30 à 300). Deux PC dont les horloges diffèrent de plus d'une minute ne se reconnaissent pas : synchroniser l'heure Windows.
- Sur un seul PC, l'essai de la signature se fait en réglant `SecondServeur:AutreServeur` sur `127.0.0.1` (adresse de boucle locale admise) : attention, l'API verrait alors sa propre base comme « l'autre principal » (voir plus haut) ; l'essai complet reste réservé à la vague 2 (lot V2-8).

## 14. Liste des postes autorisés (2026-11-24)

En modes « transition » et « exigé » (script 167), la présence, les postes connus et les votes ne comptent que les **postes prouvés** (signature
vérifiée avec la clé publique de la copie répliquée). Tant qu'aucun poste n'est autorisé, aucun poste n'est connu : pas de promotion
automatique. Essai réel avec deux PostgreSQL (coupure, votes prouvés, relais) : **non fait** (2e serveur local retiré, § 11), à jouer avec V2-8.
