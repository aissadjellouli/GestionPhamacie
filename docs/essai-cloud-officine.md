# Essayer l'officine et le serveur central ensemble, sur votre PC

Ce guide permet de tester vous-même, sur votre PC, une officine et un serveur central **reliés entre eux**, en une commande, **sans risque pour vos bases de démonstration**.

> **Outil de développement, jamais de production.** Il crée des bases jetables, un mot de passe d'essai et une autorité de certification d'essai. La vraie mise en production suit [mise-en-production.md](mise-en-production.md) (parcours E) et les scripts de `scripts\mep`.

## 1. Ce que fait l'outil

Le script est `scripts\dev\essai_cloud_officine.ps1`. Il :

- crée une **base centrale jetable** `pharmacie_cloud_essai` ;
- crée une **copie jetable** de `pharmacie_dev`, nommée `pharmacie_essai_officine` (`pharmacie_dev` n'est que **lue**, jamais modifiée) ;
- crée une autorité d'essai et ses clés dans `%TEMP%\pharma-essai-cloud` (hors du dépôt), avec un mot de passe d'essai tiré au hasard et gardé seulement dans ce dossier ;
- lance le **serveur central** d'essai (port **5190**, HTTPS) et l'**API de l'officine** d'essai (port **5191**, HTTP), en arrière-plan ;
- crée l'officine au central et **l'active** (code d'activation et certificat), comme le ferait l'écran, sans clic.

Ce qu'il ne fait jamais : toucher `pharmacie_dev` (sauf lecture), `pharmacie_test`, `pharmacie_cloud_dev`, le port 5080, `C:\ProgramData\PharmaOfficine` ; utiliser le compte `postgres` ; envoyer quoi que ce soit sur Internet (toutes les adresses sont `127.0.0.1`) ; toucher le magasin de certificats de Windows.

## 2. Prérequis

- PostgreSQL 18 installé, service démarré, et les bases de démonstration créées (voir [poste-de-developpement.md](poste-de-developpement.md)). `psql`, `pg_dump` et `pg_restore` sont cherchés dans `C:\Program Files\PostgreSQL`.
- Le SDK .NET installé (`dotnet --version`).
- Le compte `pharma_app` (celui des bases de démonstration), avec ses droits habituels de développement : créer des bases et des rôles. Si un droit manque, le script s'arrête en disant lequel ; il ne s'élève jamais en `postgres`.
- Deux ports libres : **5190** et **5191** (changer avec `-PortCentral` et `-PortApi`). Sont refusés : 5080, 5180, 9333, 5186, 9446, 5432 et tout port déjà occupé.
- Les deux mots de passe suivants (saisie masquée, jamais écrits dans le dépôt) :
  - celui de `pharma_app` : voir [poste-de-developpement.md](poste-de-developpement.md) ;
  - celui du compte `titulaire` de démonstration : même document, « Comptes de démonstration ».
  Pour ne pas les retaper : `$env:PGPASSWORD` et `$env:PHARMA_ESSAI_MDP_TITULAIRE` dans la fenêtre PowerShell (valables pour cette fenêtre seulement).

## 3. Démarrer

Dans PowerShell, à la racine du dépôt :

```powershell
# Voir ce qui serait fait, sans rien créer
.\scripts\dev\essai_cloud_officine.ps1 -Demarrer -WhatIf

# Démarrer (la première fois : compilation, quelques minutes)
.\scripts\dev\essai_cloud_officine.ps1 -Demarrer
```

Si PowerShell refuse le script : `powershell -ExecutionPolicy Bypass -File .\scripts\dev\essai_cloud_officine.ps1 -Demarrer`.

À la fin, le résumé donne les adresses et les commandes utiles. Relancer `-Demarrer` ne casse rien : il reprend ce qui manque (par exemple le serveur central arrêté).

**Le chargement complet des données part tout seul après l'activation.** Pour la copie de démonstration (environ 38 000 lignes), compter **7 minutes** environ. Suivre avec `-Etat` : « chargement ... termine ».

## 4. Ouvrir un poste sur l'officine d'essai

Le poste se branche sur l'API d'essai avec la variable `PHARMA_API` (fenêtre PowerShell à part) :

```powershell
$env:PHARMA_API = "http://127.0.0.1:5191/"
dotnet run --project src\Pharma.Desktop
```

Connectez-vous avec les identifiants de démonstration de [poste-de-developpement.md](poste-de-developpement.md). Vérification : l'en-tête du poste ne doit montrer aucune erreur de serveur, et **Administration › Synchronisation › État** doit dire « activé (certificat) ».

Ce poste ne parle qu'à l'API d'essai (5191). Votre API habituelle (5080) n'est pas touchée. Le poste garde ses réglages habituels de PC (par exemple imprimantes) et range ses PDF dans `Documents\Pharma Officine`, comme d'habitude.

## 5. Lire ce que le central a reçu

Trois commandes, sans mot de passe supplémentaire :

```powershell
.\scripts\dev\essai_cloud_officine.ps1 -Etat                                   # tout d'un coup
.\scripts\dev\essai_cloud_officine.ps1 -Requete "SELECT ..." -Sur central      # lecture dans la base centrale
.\scripts\dev\essai_cloud_officine.ps1 -Requete "SELECT ..."                   # lecture dans la copie de l'officine
```

Les requêtes sont lancées **en lecture seule**. Avec `psql` directement : hôte `localhost`, compte `pharma_app`, base `pharmacie_cloud_essai` ou `pharmacie_essai_officine`. Dans la base centrale, la sécurité au niveau des lignes cache tout tant qu'on n'a pas dit quelle officine on regarde : commencer par `SET app.officine_id = '<code de l'officine>';` (le code est `SELECT officine_id FROM sync_parametre` dans la copie de l'officine). Le script `-Requete ... -Sur central` le fait pour vous.

L'envoi se fait **chaque minute**. Attendre une à deux minutes après une action, ou cliquer sur **Synchroniser maintenant** (Administration › Synchronisation).

## 6. Scénarios à vérifier à la main

### 6.1 Une vente est envoyée et retrouvée au central

- **Faire** : au poste, une vente simple à la Caisse (espèces). Noter le numéro du ticket.
- **Attendre** une à deux minutes.
- **Voir** (central) :
  ```sql
  SELECT id, numero, montant_total FROM vente ORDER BY id DESC LIMIT 3;
  SELECT count(*) AS lots, max(numero) AS dernier_lot, max(recu_le) AS dernier_recu FROM lot_recu;
  ```
- **On doit voir** : la vente avec le numéro du ticket et le même montant ; le nombre de lots et le dernier lot qui ont augmenté d'un cran.
- Côté officine : `-Etat` montre « non_accuses = 0 » (rien en attente).

### 6.2 Rien de nominatif ne part par défaut

- **Voir** (central) :
  ```sql
  SELECT famille, envoyee FROM cloud_famille ORDER BY famille;
  SELECT id, identifiant, nom_complet FROM utilisateur ORDER BY id;
  SELECT id, est_chronique, plafond_credit FROM patient LIMIT 3;
  SELECT column_name FROM information_schema.columns
   WHERE table_name = 'patient' AND column_name IN ('nom', 'prenom', 'telephone', 'email', 'date_naissance');
  ```
- **On doit voir** : `employes`, `banque` et `clients_factures` à `f` (non envoyées) ; les noms et identifiants du personnel **vides** ; les patients sous des codes `P-...` ; **aucune ligne** pour la dernière requête (les colonnes de nom, téléphone, courriel et naissance n'existent pas au central).

### 6.3 Activer puis désactiver une famille au central

Exemple avec les noms du personnel (famille `employes`).

- **Activer** :
  ```powershell
  .\scripts\dev\essai_cloud_officine.ps1 -Central --regler-famille employes oui
  ```
  Le réglage **descend** dans l'officine en une minute environ. Visible au poste : Administration › Synchronisation › Réglages (« Données nominatives envoyées »).
- **Important** : cocher une famille **ne renvoie pas** les valeurs d'avant. Elles repartent à la prochaine modification de la ligne, ou par un chargement complet (6.4). Pour un essai rapide, modifier un employé au poste (par exemple son nom, puis le remettre), ou, avec `psql` sur la **copie jetable** seulement (`-Requete` ne sert qu'à lire) :
  ```sql
  -- base pharmacie_essai_officine : provoque une « modification » du vendeur
  UPDATE utilisateur SET nom_complet = nom_complet WHERE identifiant = 'vendeur';
  ```
- **Voir** (central) : `SELECT id, identifiant, nom_complet FROM utilisateur WHERE nom_complet IS NOT NULL;`
- **On doit voir** : la ligne du vendeur avec son nom.
- **Désactiver** :
  ```powershell
  .\scripts\dev\essai_cloud_officine.ps1 -Central --regler-famille employes non
  ```
  Attendre la descente (une minute), puis refaire une modification du vendeur. **On doit voir** : son nom et son identifiant **vides** de nouveau au central. Ce qui avait été reçu avant n'est effacé que par `--effacer-famille` (simulation d'abord, puis confirmation : c'est une suppression de données, hors de cet essai).
- Pour voir tous les réglages : `-Central --reglages`.

### 6.4 Chargement complet

- **Faire** : Administration › Synchronisation › État › **Recharger toutes les données**, confirmer.
- **Voir** (central, pendant puis après) :
  ```sql
  SELECT numero, motif, etat, debut_le, fin_le, octets_recus FROM chargement ORDER BY numero DESC LIMIT 3;
  SELECT evenement, detail FROM journal_securite ORDER BY id DESC LIMIT 5;
  ```
- **On doit voir** : un nouveau chargement de motif `demande`, d'état `en_cours` puis `termine` (environ 7 minutes pour la copie de démonstration ; l'écran de l'officine montre la progression : constitution, prêt, annoncé, terminé) ; le journal de sécurité note `chargement_debut` puis `chargement_termine`. Les ventes continuent d'être possibles pendant ce temps.

### 6.5 Bandeau « en attente » quand le central est arrêté

- **Régler** : Administration › Synchronisation › **Supervision et purge** : « Bandeau sur les postes après » = **1** minute, Enregistrer. (Par défaut 60 minutes.)
- **Arrêter le central seul** :
  ```powershell
  .\scripts\dev\essai_cloud_officine.ps1 -ArreterCentral
  ```
- **Faire** une vente au poste, puis attendre **2 à 3 minutes**.
- **On doit voir** : en bas de la fenêtre du poste (compte titulaire) « Synchronisation en attente depuis hh:mm (n opérations) » ; la vente n'est **pas** bloquée. Côté officine : `-Requete "SELECT derniere_erreur, erreur_depuis_le FROM sync_parametre"` donne « Serveur central injoignable... ».
- Le bandeau disparaît quand le central revient (6.6).

### 6.6 Reprise au redémarrage

- **Avec le central arrêté** (6.5), faire deux ou trois ventes. Puis arrêter aussi l'API et tout relancer, en gardant les bases :
  ```powershell
  .\scripts\dev\essai_cloud_officine.ps1 -Arreter -GarderBases
  .\scripts\dev\essai_cloud_officine.ps1 -Demarrer
  ```
  (`-Demarrer` ne redemande aucun mot de passe de démonstration : l'officine est déjà activée.)
- **Voir** (central, après une à deux minutes) :
  ```sql
  SELECT numero, nb_lignes, recu_le FROM lot_recu ORDER BY numero DESC LIMIT 5;
  ```
- **On doit voir** : les lots en attente arrivés **dans l'ordre**, sans trou ni doublon ; `-Etat` indique `non_accuses = 0` ; le bandeau a disparu.

### 6.7 Purge de la file d'envoi

- **Ce que c'est** : l'officine retire de sa file (`sync_sortie`) les lots **accusés depuis plus longtemps que la durée de conservation** (30 jours par défaut, **1 jour au minimum**).
- **Voir** : Administration › Synchronisation › Supervision et purge : « Purger les lots accusés » coché, « dernière purge », « lots purgés », « taille de la file ». Ou :
  ```sql
  SELECT purge_active, purge_jours, derniere_purge_le, derniere_purge_lignes, purge_dernier_lot, derniere_purge_erreur FROM sync_parametre;
  ```
- **On doit voir** : purge active, aucune erreur ; la tâche passe 5 minutes après le démarrage de l'API puis toutes les heures. **Rien n'est purgé au premier jour** (les lots sont récents) : c'est normal. La garde de la base interdit de modifier la date d'accusé d'un lot déjà accusé : on ne peut donc pas « vieillir » les lots à la main. Pour voir une vraie purge : régler la conservation à 1 jour, **mettre l'essai en pause** (`-Arreter -GarderBases`, bases et dossier gardés), reprendre le lendemain (`-Demarrer`) et attendre une heure.

### 6.8 Alerte

- **Régler** : Supervision et purge : « Alerte d'erreur après » = **1** minute, Enregistrer.
- **Arrêter le central** (`-ArreterCentral`), attendre **2 à 3 minutes**.
- **Voir** (officine) :
  ```sql
  SELECT type, objet, raison, cree_le FROM alerte_non_envoyee ORDER BY id DESC LIMIT 3;
  SELECT erreur_nature, erreur_depuis_le, alerte_erreur_le FROM sync_parametre;
  ```
- **On doit voir** : `alerte_erreur_le` renseigné (au plus **une alerte par heure**) ; comme l'essai n'a ni serveur de courriel ni destinataire vérifié, l'alerte apparaît en « alerte non envoyée » (« Aucun destinataire vérifié et actif ne reçoit ce type d'alerte ») sur l'accueil du titulaire. Aucun message ne sort du PC.
- Relancer le central avec `-Demarrer` : l'envoi reprend, l'erreur disparaît.

## 7. Arrêt et nettoyage

```powershell
# Tout supprimer : processus, deux bases jetables, rôle d'essai, dossier d'essai
.\scripts\dev\essai_cloud_officine.ps1 -Arreter

# Seulement arrêter, en gardant les bases et le dossier (pause) ; -Demarrer reprend
.\scripts\dev\essai_cloud_officine.ps1 -Arreter -GarderBases

# Voir sans rien faire
.\scripts\dev\essai_cloud_officine.ps1 -Arreter -WhatIf
```

`-Arreter` n'arrête que les deux processus lancés par le script (retrouvés par leur numéro enregistré dans le dossier d'essai) et ne supprime que `pharmacie_cloud_essai`, `pharmacie_essai_officine`, le rôle `pharma_cloud_essai` et `%TEMP%\pharma-essai-cloud`. Sans essai en cours, il ne fait rien. Pour vérifier qu'il ne reste rien :

```powershell
Test-Path "$env:TEMP\pharma-essai-cloud"          # False attendu
```
```sql
SELECT datname FROM pg_database WHERE datname IN ('pharmacie_cloud_essai', 'pharmacie_essai_officine');   -- 0 ligne
```

## 8. Dépannage

| Message ou symptôme | Cause probable et remède |
| --- | --- |
| « port 5080 interdit » (ou 5180, 9333, 5186, 9446) | Ports réservés à vos autres outils : choisir d'autres valeurs pour `-PortCentral` et `-PortApi`. |
| « Le port 5190 est déjà occupé » | Autre programme (ou essai oublié) : `-Etat`, `-Arreter`, ou autre port. |
| « Connexion à PostgreSQL impossible avec pharma_app » | Mauvais mot de passe ou service arrêté. Vérifier avec `psql -U pharma_app -h localhost -d postgres`. |
| « pharma_app n'a pas le droit CREATEDB » ou « CREATEROLE » ou « option ADMIN sur pharma_cloud_api » | Droit à donner **par l'administrateur PostgreSQL** (le script ne prend jamais le compte `postgres`). Le message cite la commande. |
| « Compilation ... en échec » | Lire les lignes affichées. Un poste ou l'API habituelle qui tourne ne gêne pas (compilation dans `%TEMP%\pharma-essai-cloud\artefacts`). `-Recompiler` pour refaire. |
| « Connexion du compte titulaire de démonstration refusée » | Mot de passe de démonstration incorrect (voir [poste-de-developpement.md](poste-de-developpement.md)). Relancer `-Demarrer`. |
| « Activation refusée » | Le code a servi ou expiré : relancer `-Demarrer` (un code neuf est demandé pour la même officine). |
| « reste d'un ancien essai » | La base centrale existe mais le dossier d'essai a disparu : `-Arreter`, puis `-Demarrer`. |
| L'officine n'envoie rien | `-Etat` : processus arrêtés ? `derniere_erreur` ? Journaux dans `%TEMP%\pharma-essai-cloud` (`central.out.log`, `api.out.log`, `*.err.log`). |
| Rien au central après une vente | Attendre une à deux minutes, ou « Synchroniser maintenant ». Après une panne du central, l'envoi réessaie à 1, 2, 4 minutes... jusqu'à 30 minutes : « Synchroniser maintenant » force un passage. |
| Rien de nominatif visible après avoir activé une famille | Normal : modifier la ligne (ou recharger toutes les données) pour qu'elle reparte (6.3). |

Les journaux du serveur central et de l'API d'essai sont dans `%TEMP%\pharma-essai-cloud` ; ils sont supprimés avec l'essai.

## 9. Limites connues de l'outil

- Les PDF produits par le poste et ses réglages d'imprimante restent ceux de votre PC (hors du dossier d'essai).
- L'API d'essai redirige vers le dossier d'essai ses dossiers de documents, de journaux, de clés et de synchronisation. Deux dossiers de l'API ne sont pas réglables par configuration (archives fiscales mensuelles et copie de secours) : ne pas lancer de clôture fiscale mensuelle pendant l'essai.
- La clé de l'API (`Jwt`) est tirée au hasard à chaque lancement de l'API : les connexions ouvertes avant un redémarrage sont à refaire.
- Le serveur central d'essai tourne en `Debug`, sans service Windows : c'est un essai fonctionnel, pas une mesure de performance.
