# Essayer l'officine et le serveur central ensemble, sur votre PC

Ce guide permet de tester vous-même, sur votre PC, une officine et un serveur central **reliés entre eux**, en une commande, **sans risque pour vos bases de démonstration**.

> **Outil de développement, jamais de production.** Il crée des bases jetables, un mot de passe d'essai et une autorité de certification d'essai. La vraie mise en production suit [mise-en-production.md](mise-en-production.md) (parcours E) et les scripts de `scripts\mep`.

## 1. Ce que fait l'outil

Le script est `scripts\dev\essai_cloud_officine.ps1`. Il :

- crée une **base centrale jetable** `pharmacie_cloud_essai` ;
- crée une **copie jetable** de `pharmacie_dev`, nommée `pharmacie_essai_officine` (`pharmacie_dev` n'est que **lue**, jamais modifiée) ;
- crée une autorité d'essai et ses clés dans `%TEMP%\pharma-essai-cloud` (hors du dépôt), avec un mot de passe d'essai tiré au hasard et gardé seulement dans ce dossier ;
- lance le **serveur central** d'essai (port **5190**, HTTPS) et l'**API de l'officine** d'essai (port **5191**, HTTP), en arrière-plan ;
- crée l'officine au central et **l'active** (code d'activation et certificat), comme le ferait l'écran, sans clic. Pour cela il crée, dans la copie jetable **seulement**, un compte titulaire temporaire `essai_activation` (mot de passe aléatoire, en mémoire seulement), l'utilise, puis le **désactive** : le mot de passe du vrai compte `titulaire` n'est plus nécessaire.

Ce qu'il ne fait jamais : toucher `pharmacie_dev` (sauf lecture), `pharmacie_test`, `pharmacie_cloud_dev`, le port 5080, `C:\ProgramData\PharmaOfficine` ; utiliser le compte `postgres` ; envoyer quoi que ce soit sur Internet (toutes les adresses sont `127.0.0.1`) ; toucher le magasin de certificats de Windows.

Pour relier, au contraire, l'officine `pharmacie_dev` au serveur central de développement `pharmacie_cloud_dev` lancés en débogage
depuis Visual Studio, c'est un autre outil : `scripts\dev\preparer_central_vs.ps1` (voir [poste-de-developpement.md](poste-de-developpement.md),
« Lancer API + Poste + Central dans Visual Studio »). L'outil d'essai décrit ici, lui, ne touche jamais `pharmacie_cloud_dev` ; il
donne à ses deux serveurs leurs propres variables (connexions, dossiers), qui l'emportent sur les variables d'utilisateur posées par
`preparer_central_vs.ps1`.

## 2. Prérequis

- PostgreSQL 18 installé, service démarré, et les bases de démonstration créées (voir [poste-de-developpement.md](poste-de-developpement.md)). `psql`, `pg_dump` et `pg_restore` sont cherchés dans `C:\Program Files\PostgreSQL`.
- Le SDK .NET installé (`dotnet --version`).
- Le compte `pharma_app` (celui des bases de démonstration), avec ses droits habituels de développement : créer des bases et des rôles. Si un droit manque, le script s'arrête en disant lequel ; il ne s'élève jamais en `postgres`.
- Deux ports libres : **5190** et **5191** (changer avec `-PortCentral` et `-PortApi`). Sont refusés : 5080, 5180, 9333, 5186, 9446, 5432 et tout port déjà occupé.
- **Un seul mot de passe** (saisie masquée, jamais écrit dans le dépôt) : celui de `pharma_app`, **`pharma_dev`** (mot de passe de développement des bases locales, voir [poste-de-developpement.md](poste-de-developpement.md) ; jamais utilisé en production). Le script le demande au démarrage (rien ne s'affiche pendant la frappe : taper puis Entrée). Pour ne pas le retaper, le définir avant de lancer, dans la même fenêtre PowerShell (valable pour cette fenêtre seulement) :
  ```powershell
  $env:PGPASSWORD = "pharma_dev"
  ```
- **Aucun mot de passe de titulaire.** Le compte `titulaire` de démonstration n'est ni demandé ni modifié : l'activation de l'officine d'essai passe par le compte temporaire `essai_activation` (section 3 bis). Ancien chemin, gardé pour compatibilité : si la variable `PHARMA_ESSAI_MDP_TITULAIRE` est définie, le script se connecte avec le compte `titulaire` et ce mot de passe, et ne crée aucun compte.

## 3. Démarrer

Dans PowerShell, à la racine du dépôt (`cd C:\MesAtlassian\pharmaofficine`).

**Windows bloque les scripts PowerShell par défaut** (message « l'exécution de scripts est désactivée sur ce système »). Lancer donc
chaque commande du script avec `powershell -ExecutionPolicy Bypass -File` devant : l'autorisation ne vaut que pour cette commande,
rien n'est modifié sur le PC.

```powershell
# Voir ce qui serait fait, sans rien créer
powershell -ExecutionPolicy Bypass -File .\scripts\dev\essai_cloud_officine.ps1 -Demarrer -WhatIf

# Démarrer (la première fois : compilation, quelques minutes)
powershell -ExecutionPolicy Bypass -File .\scripts\dev\essai_cloud_officine.ps1 -Demarrer

# Les autres actions, de la même façon
powershell -ExecutionPolicy Bypass -File .\scripts\dev\essai_cloud_officine.ps1 -Etat
powershell -ExecutionPolicy Bypass -File .\scripts\dev\essai_cloud_officine.ps1 -Arreter
```

Autre possibilité, pour toute la fenêtre PowerShell ouverte (la protection revient à sa fermeture) :
`Set-ExecutionPolicy -Scope Process -ExecutionPolicy Bypass`, puis les commandes courtes `.\scripts\dev\essai_cloud_officine.ps1 -Demarrer`, etc.
Dans la suite de ce guide, les commandes sont écrites en forme courte : ajouter `powershell -ExecutionPolicy Bypass -File` devant si
la fenêtre n'a pas été autorisée.

À la fin, le résumé donne les adresses et les commandes utiles. Relancer `-Demarrer` ne casse rien : il reprend ce qui manque (par exemple le serveur central arrêté).

**Le chargement complet des données part tout seul après l'activation.** Pour la copie de démonstration (environ 38 000 lignes), compter **7 minutes** environ. Suivre avec `-Etat` : « chargement ... termine ».

## 3 bis. Le compte temporaire d'activation

L'activation de l'officine passe par l'API, qui demande une connexion. Au premier démarrage (officine pas encore activée), le script :

1. tire au hasard un mot de passe de 24 caractères, **gardé en mémoire seulement** (jamais écrit dans un fichier, un journal ni à l'écran) ;
2. crée dans `pharmacie_essai_officine` le compte `essai_activation` (rôle titulaire, sans changement de mot de passe obligatoire), avec un hachage bcrypt fait par la bibliothèque de l'application (même facteur de coût) ;
3. se connecte avec ce compte, active l'officine comme l'écran (réglages, code, certificat) ;
4. **désactive** le compte (`actif = false`), même si l'activation a échoué.

Garde-fous : le compte n'est créé que si la base visée est exactement `pharmacie_essai_officine` (refus pour `pharmacie_dev`, `pharmacie_test` et toute autre base, et la base de la connexion est relue avant d'écrire) ; seul le compte `essai_activation` est écrit, jamais le compte `titulaire` ni un autre ; un nouveau `-Demarrer` après un échec réactive le même compte avec un nouveau mot de passe. Le compte disparaît avec la copie jetable (`-Arreter`). Il ne figure pas dans `pharmacie_dev`.

Pour des appels ultérieurs à l'API (6.9), ne pas réactiver ce compte à la main : créer un autre compte de la même façon, dans la copie jetable seulement.

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

## 5 bis. Suivre l'essai en direct

Toutes ces commandes **lisent seulement** : elles peuvent tourner pendant qu'un autre essai (ou un agent) travaille sur l'officine d'essai.
Dans une fenêtre PowerShell, à la racine du dépôt :

```powershell
cd C:\MesAtlassian\pharmaofficine
$env:PGPASSWORD = "pharma_dev"
```

**1. À l'écran (le plus parlant).** Ouvrir un poste sur l'officine d'essai (section 4), se connecter en titulaire, puis :
- **Administration › Synchronisation › Réglages mutualisés** : état des 11 tables (local, proposé, imposé, rejeté) ; « Actualiser » de temps en temps ;
- **Inventaire › Seuils**, **Motifs**, **Remises automatiques**, **Contrôle interne** : bandeau « Proposé par le serveur central » ou « Fixé par le serveur central ».

**2. Côté serveur central.**

```powershell
# État des 11 modèles publiés (mode, version, auteur, officines qui ont appliqué ou rejeté)
powershell -ExecutionPolicy Bypass -File .\scripts\dev\essai_cloud_officine.ps1 -Central --reglages-mutualises

# Journal des publications et changements de mode (qui, quand, avant / après)
powershell -ExecutionPolicy Bypass -File .\scripts\dev\essai_cloud_officine.ps1 -Central --journal-reglages
```

**3. Côté officine d'essai.**

```powershell
# État de la synchronisation (files, lots, dernier envoi, erreurs)
powershell -ExecutionPolicy Bypass -File .\scripts\dev\essai_cloud_officine.ps1 -Etat

# État des 11 réglages dans l'officine
powershell -ExecutionPolicy Bypass -File .\scripts\dev\essai_cloud_officine.ps1 -Requete "select nom_table, mode, etat, version_modele_appliquee, motif_rejet from reglage_mutualise order by 1" -Sur officine

# Ce qui a été appliqué, remplacé, adopté ou rejeté (journal des actions)
powershell -ExecutionPolicy Bypass -File .\scripts\dev\essai_cloud_officine.ps1 -Requete "select cree_le, action, details from journal_audit where action like 'reglage_central%' order by cree_le desc limit 20" -Sur officine
```

**4. Journaux des deux serveurs** (fichiers texte, un par serveur) :

```powershell
explorer "$env:TEMP\pharma-essai-cloud"
```

### Suivre en continu sur trois fenêtres côte à côte

Le serveur central n'a pas encore d'écran (lot S7) : ses vues sont des fenêtres PowerShell qui se rafraîchissent toutes les 15 secondes.
Arrêter une boucle : **Ctrl + C** dans sa fenêtre. Ces boucles ne font que lire.

**Vue 1, le poste** : Administration › Synchronisation › Réglages mutualisés (« Actualiser » de temps en temps), et Inventaire › Seuils
pour voir le bandeau « Proposé par le serveur central » puis « Fixé par le serveur central ».

**Vue 2, le serveur central** :

```powershell
cd C:\MesAtlassian\pharmaofficine
$env:PGPASSWORD = "pharma_dev"
while ($true) {
  Clear-Host
  Write-Host "=== SERVEUR CENTRAL ($(Get-Date -Format HH:mm:ss)) ===" -ForegroundColor Cyan
  powershell -ExecutionPolicy Bypass -File .\scripts\dev\essai_cloud_officine.ps1 -Central --reglages-mutualises
  Write-Host "`n--- Derniers changements ---" -ForegroundColor Cyan
  powershell -ExecutionPolicy Bypass -File .\scripts\dev\essai_cloud_officine.ps1 -Central --journal-reglages --limite 8
  Start-Sleep 15
}
```

**Vue 3, l'officine d'essai** :

```powershell
cd C:\MesAtlassian\pharmaofficine
$env:PGPASSWORD = "pharma_dev"
while ($true) {
  Clear-Host
  Write-Host "=== OFFICINE D'ESSAI ($(Get-Date -Format HH:mm:ss)) ===" -ForegroundColor Green
  powershell -ExecutionPolicy Bypass -File .\scripts\dev\essai_cloud_officine.ps1 -Requete "select nom_table, mode, etat, version_modele_appliquee as version, motif_rejet from reglage_mutualise order by 1" -Sur officine
  Write-Host "`n--- Dernières actions ---" -ForegroundColor Green
  powershell -ExecutionPolicy Bypass -File .\scripts\dev\essai_cloud_officine.ps1 -Requete "select to_char(cree_le,'HH24:MI:SS') as heure, action from journal_audit where action like 'reglage_central%' order by cree_le desc limit 8" -Sur officine
  Start-Sleep 15
}
```

Ce qu'on voit pendant un essai de mutualisation : au central (vue 2), une table passe de la version 0 à « propose » puis « impose », avec
l'auteur au journal ; dans la minute, l'officine (vue 3) change de mode et journalise `reglage_central_applique`, `_remplace`,
`_adopte` ou `_rejete` ; au poste (vue 1), le bandeau apparaît après « Actualiser ». Pendant un scénario de panne, la vue 2 affiche une
erreur de connexion : le central est arrêté volontairement, puis relancé.

**Pendant qu'un essai de scénarios tourne** : regarder seulement. Ne pas modifier les réglages soi-même (cela fausserait les scénarios) et ne
pas lancer `-Arreter` avant la fin.

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
  (`-Demarrer` ne crée aucun compte temporaire : l'officine est déjà activée.)
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

### 6.9 Mutualisation des réglages (lot M8)

Les 11 réglages que le serveur central peut proposer ou imposer (`conception-synchronisation.md`, § 16 bis) se jouent avec les mêmes deux processus. Les commandes du central passent par `-Central` ; l'état de l'officine se lit avec `-Requete`. Une table n'est publiée que par une action explicite : tant qu'elle ne l'est pas, rien ne descend.

**Compte d'essai.** Les appels de l'API officine demandent une connexion. Le compte `essai_activation` du démarrage est désactivé une fois l'officine activée (section 3 bis). Ne pas toucher au compte `titulaire` : créer un compte de rôle titulaire dans la **copie jetable seulement** (hachage BCrypt de l'application, mot de passe tiré au hasard et jamais écrit), s'en servir, puis le désactiver.

**Scénarios** (chacun : faire, attendre une minute au plus, lire). Exemples avec les motifs (`motif_reglable`), qui acceptent un modèle de bout en bout, et les remises (`remise_automatique`) :

1. **Rien n'est publié** : `-Central --reglages-mutualises` (« NON PUBLIÉ » ×11). Officine : `-Requete "SELECT nom_table, mode, etat FROM reglage_mutualise ORDER BY 1"` (11 lignes `local`, `aucun`).
2. **Proposer** : `-Central --modele-depuis-officine <code officine> motif_reglable --fichier <fichier.json> --ecraser`, modifier une valeur dans le fichier, `-Central --publier-reglage motif_reglable --fichier <fichier.json> --auteur <nom> --simulation`, puis la même commande sans `--simulation`. Officine : `-Requete "SELECT nom_table, mode, version_modele_recue, version_modele_appliquee, etat, origine FROM reglage_mutualise WHERE nom_table = 'motif_reglable'"` ; l'API répond par `GET /api/reglages-mutualises` et l'en-tête `X-Reglage-Central` de la route de l'écran. **Attendu** : au premier modèle différent, `non_adopte` (la valeur de l'officine gagne) ; après « Adopter » (`POST /api/reglages-mutualises/motif_reglable/adopter`, corps `{"versionModele": n}`), les versions suivantes sont suivies toutes seules tant que la valeur n'est pas modifiée sur place.
3. **Modifier sur place puis republier** : modification par l'écran, nouvelle version proposée → `non_adopte`, valeur locale gardée ; Adopter → valeur du central, journal `reglage_central_adopte` (`-Requete "SELECT action, details FROM journal_audit WHERE action LIKE 'reglage_central%' ORDER BY id DESC LIMIT 3"`).
4. **Imposer** : `-Central --mode-reglage motif_reglable impose --auteur <nom> --oui`. **Attendu** : valeur locale remplacée (`reglage_central_remplace`, avant et après), message `reglages_centraux` (`-Requete "SELECT type, objet FROM alerte_non_envoyee ORDER BY id DESC LIMIT 3"`), modification de l'écran refusée (409, champ `reglage_impose`), écriture SQL directe refusée (« Réglage fixé par le serveur central »).
5. **Motifs** : motif du central verrouillé, ajout local permis, motif retiré du modèle gardé et redevenu modifiable.
6. **Remises** : créer une remise locale par catégorie, publier un modèle de remises imposé → la remise locale est **arrêtée** (jamais supprimée), `GET /api/remises-automatiques` la montre `arretee`.
7. **Modèle invalide** : un fichier avec une valeur interdite est refusé par `--publier-reglage` (code 1, rien d'écrit). Par la voie descendante, seule une insertion contrôlée dans la base centrale **d'essai** permet de le jouer ; elle doit viser une table jamais publiée, sinon le central réécrit l'élément à l'échange suivant : `SET app.officine_id = '<code>'; SELECT cloud_publier_descendant('reglage_central', 'mutu_<table>', '<élément mutu-v1 invalide>'::jsonb);`. **Attendu** : table `rejete` avec motif, ancienne valeur gardée, une seule ligne `reglage_central_rejete` pour cette version.
8. **Libérer, rétablir** : `-Central --liberer-reglages <code> --auteur <nom>` puis `-Central --retablir-reglages <code> --auteur <nom>`.
9. **Désactiver puis réactiver la synchronisation** (Administration › Synchronisation) : tables locales dès la désactivation, valeurs gardées ; imposition de retour à la réactivation.
10. **Central arrêté** (`-ArreterCentral`) avec une table imposée : l'officine fonctionne et le refus local reste actif ; `-Demarrer` relance le central seul.
11. **Rejeu des scripts** avec une table imposée : copie jetable `pharmacie_jetable_rejeu_...` de la base d'essai (`pg_dump`, puis `database\tests\rejeu_complet.ps1`).
12. **Journal central** : `-Central --journal-reglages [table]` (qui, quand, avant et après, compte Windows et poste).

**État (2026-11-17, lot M8 clos)** : les 13 scénarios ci-dessus (et la première réception d'un modèle proposé, règle D-M8-2) ont été rejoués avec succès sur un essai neuf (central 5192, API 5193). Deux défauts ont été corrigés en route : D-M8-1 (script central `011`, `ajouts_locaux` écrit seulement pour les motifs et les remises ; `-Demarrer` crée la base centrale avec 001 à 011) et D-M8-3 (la libération et le rétablissement par `--liberer-reglages` / `--retablir-reglages` republient la même version : l'officine les applique désormais ; un essai lancé avant ce correctif doit être relancé avec `-Arreter` puis `-Demarrer -Recompiler`). Un essai commencé avant un script (011 central, 142 officine) doit être arrêté puis redémarré. Points d'attention : `--modele-depuis-officine` lit le **miroir du central**, donc une modification locale toute fraîche n'y est pas encore (attendre un envoi) ; la copie `pharmacie_essai_officine` est faite au démarrage, un script de migration ajouté ensuite à `pharmacie_dev` n'y est pas (l'appliquer à la copie jetable de rejeu avant `rejeu_complet.ps1`) ; si `-Arreter` répond « droits insuffisants », relancer la commande. Détail et preuves : `conception-synchronisation.md`, § 16 bis.12 « Lot M8 : résultat final ».

## 7. Arrêt et nettoyage

**Si un essai de scénarios tourne encore** (par exemple un essai lancé par un agent), attendre sa fin : arrêter les serveurs l'interromprait.

Arrêter dans cet ordre :

1. **Les boucles de suivi** (vues 2 et 3 de la section 5 bis) : **Ctrl + C** dans chaque fenêtre, puis la fermer.
2. **Le poste de caisse** : fermer la fenêtre Tracodex Officine. Cela libère aussi `Pharma.Desktop.exe` : tant que le poste est ouvert, `dotnet build` échoue sur « fichier verrouillé » (ce n'est pas une erreur de code).
3. **Les deux serveurs et les bases d'essai** :

```powershell
cd C:\MesAtlassian\pharmaofficine
$env:PGPASSWORD = "pharma_dev"

# Tout supprimer : processus, deux bases jetables, rôle d'essai, dossier d'essai
powershell -ExecutionPolicy Bypass -File .\scripts\dev\essai_cloud_officine.ps1 -Arreter
```

4. **Vérifier qu'il ne reste rien** :

```powershell
Get-Process Pharma* -ErrorAction SilentlyContinue                                        # rien ne doit s'afficher
Get-NetTCPConnection -LocalPort 5190,5191 -State Listen -ErrorAction SilentlyContinue    # rien non plus
```

Ne sont jamais touchés : les bases de démonstration (`pharmacie_dev`, `pharmacie_test`), l'API habituelle (port 5080) et le compte `postgres`.

Autres variantes (forme courte : ajouter `powershell -ExecutionPolicy Bypass -File` devant si la fenêtre n'est pas autorisée) :

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
| « Connexion du compte titulaire de démonstration refusée » | Cela n'arrive plus que si la variable `PHARMA_ESSAI_MDP_TITULAIRE` est définie (ancien chemin) avec un mot de passe refusé, par exemple sur ce PC. Remède : retirer la variable (`Remove-Item Env:\PHARMA_ESSAI_MDP_TITULAIRE`) et relancer `-Demarrer` : le compte temporaire `essai_activation` prend le relais, sans aucun mot de passe de titulaire. |
| « Connexion avec le compte temporaire essai_activation impossible » | L'API d'essai ne répond pas ou refuse la connexion : `-Etat`, journaux `api.err.log`. Relancer `-Demarrer` (le compte est réactivé avec un nouveau mot de passe). |
| « Refusé : le compte temporaire d'essai n'est créé que dans la base pharmacie_essai_officine » | Garde-fou : le script ne crée ce compte que dans la copie jetable. Ne pas modifier les noms de base du script. |
| « Compte temporaire essai_activation non désactivé » | Avertissement : la désactivation a échoué. Le compte disparaît avec `-Arreter`, ou le désactiver à la main dans `pharmacie_essai_officine` (`UPDATE utilisateur SET actif = false WHERE identifiant = 'essai_activation'`). |
| « Activation refusée » | Le code a servi ou expiré : relancer `-Demarrer` (un code neuf est demandé pour la même officine). |
| « n'a pas pu être arrêté (droits insuffisants ?) » | Un serveur d'essai a été lancé avec d'autres droits (fenêtre élevée, autre session) : `-Arreter` ne supprime alors aucune base. Arrêter le processus indiqué soi-même (`Stop-Process -Id <PID> -Force`, dans une fenêtre aux mêmes droits), puis relancer `-Arreter`. |
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
