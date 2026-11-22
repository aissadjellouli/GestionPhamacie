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
