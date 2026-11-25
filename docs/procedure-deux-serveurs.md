# Mise en place de deux serveurs : procédure complète

Procédure de bout en bout pour installer une officine avec **un PC serveur (principal) et un 2e serveur**, puis pour la vie courante
(panne, retour du serveur réparé). C'est l'**ordre à suivre** : chaque étape renvoie au détail (commandes complètes, messages
attendus, cas d'échec) dans [mise-en-production.md](mise-en-production.md). En cas d'écart entre les deux documents,
**mise-en-production.md fait foi**.

Version de référence : 2026-11-25 (bascule automatique, vague 2 ; réplication synchrone ; postes autorisés). Essais réels :
[second-serveur-local.md](second-serveur-local.md) § 15 à 17.

---

## 0. Ce qu'on obtient

- **Une seule base en écriture**, toujours : celle du PC serveur. Le 2e serveur en reçoit une copie **en continu** (réplication
  PostgreSQL), en lecture seule, et son API tourne **en veille**.
- **Bascule automatique** si le PC serveur tombe : le 2e serveur se promeut seul (1 min 30 à 2 min), les postes basculent seuls, sans
  redémarrage ni reconnexion ; une vente interrompue par la panne est renvoyée automatiquement, jamais encaissée deux fois.
- **Aucune vente perdue** si la réplication synchrone est réglée (phase 4) : les ventes, factures et registre attendent que le 2e serveur
  les ait reçus (au plus 2 s, environ +7 ms par vente mesurés).
- **GED** (ordonnances scannées, factures, fichiers Chifa…) copiée vers le 2e serveur toutes les N minutes (5 par défaut).

Cas où la bascule reste **manuelle** (procédure F3) : égalité des postes, base du PC serveur vivante mais API muette, droit `166b`
absent, promotion automatique désactivée.

---

## 1. Prérequis (avant de commencer)

| # | Quoi | Détail |
|---|---|---|
| 1.1 | **PC serveur installé et en service** | Parcours A complet (PostgreSQL, base, API, HTTPS, service, postes) — mise-en-production.md, parcours A |
| 1.2 | **2e serveur** : un PC allumé en permanence, même réseau local, **IP fixe** | Parcours C, étape 1 |
| 1.3 | **Même version majeure de PostgreSQL** et **même version de l'API** sur les deux PC | Parcours A, étapes 2.1 et 4.1 ; API dans `C:\Program Files\PharmaOfficine\Api` sur le 2e serveur |
| 1.4 | Dossier `scripts\second-serveur` copié sur **les deux** PC (avec `commun.ps1`) | Parcours C, étape 2 |
| 1.5 | Comptes nécessaires | Administrateur Windows des deux PC ; mot de passe du superutilisateur PostgreSQL `postgres` ; titulaire dans l'application |
| 1.6 | **Clé USB du coffre** | Pour le certificat HTTPS du 2e serveur et la clé de scellement |
| 1.7 | **Heure** des PC (serveurs et postes) | À moins d'une minute entre les deux serveurs (échanges signés), à moins de 2 minutes entre chaque poste et le serveur |

**Règle pour tous les scripts** : lancer d'abord avec `-Essai` (ou `-WhatIf`), lire ce qui sera fait, puis sans.

---

## 2. Phase 1 : PC serveur (principal)

| # | Action | Où / compte | Détail |
|---|---|---|---|
| 2.1 | **Droits du compte de l'API pour la bascule** : scripts administrateur `163b` (lire l'identifiant de la base) et `166b` (promouvoir la base) | PC serveur, superutilisateur PostgreSQL | Passés par `creer_base_officine.ps1` / `mettre_a_jour_base_officine.ps1` ; **propres à chaque base** (vérifier sur la base de la pharmacie). Sans `163b` aucun serveur n'est jamais écarté ; sans `166b` pas de promotion automatique. Parcours C (intro), scripts-base-de-donnees.md § 3.9 et § 3.10 |
| 2.2 | `preparer-principal.ps1 -AdresseSecond <IP du 2e> -AdressePrincipal <IP du principal>` (`-Essai` d'abord) | PC serveur, admin Windows + `postgres` | Rôle de réplication (**mot de passe au coffre**), réglages de réplication, `pg_hba.conf` limité à l'IP du 2e, slot `second_serveur`, `pg_monitor`. `-Redemarrer` si demandé, à un moment calme. Parcours C, étapes 3 et 4 |
| 2.3 | `ouvrir-port-base.ps1 -AdresseSecond <IP du 2e>` (`-WhatIf` d'abord) | PC serveur, admin Windows | Port 5432 ouvert **au seul 2e serveur**, jamais au réseau. Parcours C, étape 4 bis |
| 2.4 | Déclarer le 2e serveur à l'API : variable machine `SecondServeur__AutreServeur = <IP du 2e>`, puis `Restart-Service "PharmaOfficine API"` | PC serveur, admin Windows | Parcours C, étape 5 |
| 2.5 | **Empreinte de la clé des jetons** : `Pharma.Api.exe --empreinte-jeton` (16 caractères, jamais la clé) | PC serveur | À noter pour la phase 2 (contrôle de la clé commune) |
| 2.6 | **Exporter la clé de scellement** (Administration › 2e serveur › Clé de scellement, mot de passe de 12 caractères au moins) | Titulaire, dans l'application | Fichier `.pocle` sur la clé USB **au coffre**, mot de passe **à part**. Parcours C, étape 6 |

---

## 3. Phase 2 : 2e serveur

| # | Action | Où / compte | Détail |
|---|---|---|---|
| 3.1 | Variables machine : `ConnectionStrings__Pharmacie` (`Host=localhost`, même compte `pharma_app`, même mot de passe), `Chifa__DossierExport` propre au PC ; **ne pas** régler `AutoriserHttpReseau` | 2e serveur, admin Windows | Parcours C, étape 7. La clé des jetons `Jwt__Cle` est posée par l'étape 3.3 |
| 3.2 | **Certificat HTTPS** signé par la **même autorité** que le PC serveur : généré sur le PC serveur (`certificat_https.ps1 -CertificatPourAutrePc …`), importé sur le 2e (`-Importer … -PareFeu`) | PC serveur puis 2e serveur, admin Windows | Nom et IP **exacts** que les postes utiliseront ; phrase de passe au coffre ; rien à réinstaller sur les postes. Parcours C, étapes 8a et 8b |
| 3.3 | `initialiser-second-serveur.ps1 -Principal <IP du principal> -EmpreinteJetonPrincipal <empreinte 2.5> -FichierCle E:\cle-scellement-<…>.pocle` (`-Essai` d'abord) | 2e serveur, admin Windows + mots de passe réplication, `postgres`, clé de scellement (titulaire) | Taper `OUI`. Le script : contrôle la **clé des jetons** (saisie masquée, s'arrête si l'empreinte diffère), règle le service de l'API en démarrage automatique avec relance (arrêté pour l'instant), garde l'ancien dossier PostgreSQL, copie la base (`pg_basebackup`), **ajoute le nom `second_serveur`** à la copie (réplication synchrone), démarre et contrôle « lecture seule », importe la clé de scellement. Parcours C, étape 9 |
| 3.4 | Déclarer le principal et **démarrer l'API en veille** : `SecondServeur__AutreServeur = <IP du principal>`, `Start-Service "PharmaOfficine API"` | 2e serveur, admin Windows | **Attendu** : service `Running` / `Automatic`, `https://<IP du 2e>:5443/api/healthcheck` → `role = veille`. Parcours C, étape 10 |
| 3.5 | **Réglages par serveur** (Administration › 2e serveur › Réglages par serveur) : dossiers CHIFA-Officine et MDN de chaque serveur, cases matériel | Titulaire, sur le PC serveur | Parcours C, étape 11 |
| 3.6 | `verifier-second-serveur.ps1 -Principal <IP du principal> -DossierChifa … -DossierMdn …` puis écran « 2e serveur » | 2e serveur, admin Windows | **Attendu** : tout vert, versions identiques, clé de scellement identique. Parcours C, étape 12 |

---

## 4. Phase 3 : copie de la GED

| # | Action | Où / compte | Détail |
|---|---|---|---|
| 4.1 | `partager-ged.ps1 -AdresseSecond <IP du 2e>` | PC serveur, admin Windows | Compte local `PharmaGedCopie` (**mot de passe au coffre**, 14 caractères au moins), partage `PharmaGED$` en lecture seule, port 445 au seul 2e serveur. Parcours C, étape 12 bis |
| 4.2 | Essai : `copier-ged.ps1 -Source \\<IP du principal>\PharmaGED$ -Essai` | 2e serveur | Parcours C, étape 12 ter |
| 4.3 | `autoriser-lecture-copie-ged.ps1` sur le **principal**, puis sur le **2e serveur** | Les deux, admin Windows + `postgres` | Fréquence de copie lue dans l'application (facultatif : sinon 5 minutes). Parcours C, étape 12 ter bis |
| 4.4 | `copier-ged.ps1 -Source \\<IP du principal>\PharmaGED$ -Installer`, puis contrôle de la tâche et du journal | 2e serveur, admin Windows | Même mot de passe `PharmaGedCopie` que sur le principal ; première copie longue. Parcours C, étape 12 quater |

---

## 5. Phase 4 : réplication synchrone (recommandé : aucune vente perdue)

Parcours C, étape 15.

| # | Action | Où / compte | Détail |
|---|---|---|---|
| 5.1 | Vérifier le **nom de la copie** : `application_name=second_serveur` présent dans `primary_conninfo` (`postgresql.auto.conf` du dossier de données du 2e serveur) | 2e serveur | Ajouté par `initialiser-second-serveur.ps1` (étape 3.3) et `remettre-en-second.ps1`. Copie faite avec une ancienne version des scripts : l'ajouter à la main (fichier **sans BOM**), redémarrer PostgreSQL du 2e serveur |
| 5.2 | Sur le principal (`psql -U postgres`), **les trois réglages ensemble** : `synchronous_standby_names = 'second_serveur'`, `synchronous_commit = 'local'`, `wal_sender_timeout = '10s'`, puis `SELECT pg_reload_conf();` | PC serveur, `postgres` | **`synchronous_commit = 'local'` est obligatoire** : sans lui, toutes les transactions attendraient le 2e serveur et bloqueraient s'il est arrêté. Aucun redémarrage. **Pas répliqués** (fichier de configuration du PC, pas la base) : à refaire sur le nouveau principal après une bascule (phase 8, étape 2) |
| 5.3 | Contrôle : `SELECT application_name, sync_state FROM pg_stat_replication;` | PC serveur | **Attendu** : `second_serveur \| sync` ; `SHOW synchronous_commit;` → `local` |

Retour arrière : `ALTER SYSTEM RESET synchronous_standby_names; SELECT pg_reload_conf();`.

---

## 6. Phase 5 : postes

| # | Action | Où / compte | Détail |
|---|---|---|---|
| 6.1 | **Autoriser chaque poste** : code créé par le titulaire (Administration › Postes autorisés › « Autoriser un poste », ou `Pharma.Api.exe --autoriser-poste "CAISSE-1"` pour le premier), saisi sur le poste (écran de connexion › « Autoriser ce poste ») | Chaque poste, compte Windows habituel ; un administrateur du PC accepte **une fois** la demande de Windows | **Indispensable à la bascule automatique** : seuls les postes autorisés votent ; sans poste autorisé, pas de promotion automatique. Une fois tous autorisés : « Preuve des postes » = « Exigée pour tous ». mise-en-production.md, « Postes autorisés » |
| 6.1 bis | **Nom des postes** : c'est le nom saisi par le titulaire à l'autorisation (« Nom du poste ») qui est affiché et utilisé partout (caisse, ventes en attente, votes de la bascule). À changer par Administration › Postes autorisés › « Renommer » (refusé si le poste a une caisse ouverte, ou des ventes ou réceptions en attente sous son nom) | Titulaire | Le poste renommé affiche son nouveau nom à son prochain écran de connexion (aussitôt s'il est renommé depuis lui-même). Sans autorisation : variable `PHARMA_POSTE` du PC, sinon « Poste » + nom de l'ordinateur |
| 6.2 | **Adresse de secours** : Administration › Imprimantes et étiquettes › **Serveur et PC de secours** = `https://<nom ou IP du 2e>:5443/` | Chaque poste, titulaire | Nom ou IP **présent dans le certificat** du 2e serveur (étape 3.2) ; jamais `http://…:5080/`. Parcours C, étape 13 |
| 6.3 | Case **« Basculer automatiquement sur le 2e serveur (ce poste) »** (même écran) | Chaque poste | Cochée par défaut. Décochée : le poste propose seulement le bouton de bascule (après vérification que le 2e serveur est principal) |

---

## 7. Phase 6 : contrôles de fin d'installation

| # | Contrôle | Attendu |
|---|---|---|
| 7.1 | Écran Administration › 2e serveur › État de la réplication | « 2e serveur connecté », retard faible, slot actif |
| 7.2 | Santé des deux API | Principal : `role = principal` ; 2e serveur : `role = veille` |
| 7.3 | Réplication synchrone (si phase 4 faite) | `second_serveur \| sync` |
| 7.4 | Alertes (seuils de retard, SMS ou e-mail du titulaire) | Parcours C, étape 14 |
| 7.5 | Barre d'état d'un poste | Rien concernant le 2e serveur (l'élément « 2e serveur : … » n'apparaît qu'en anomalie) |
| 7.6 | Postes autorisés | Tous les postes listés comme prouvés (Administration › Postes autorisés) |
| 7.7 | Promotion automatique | Active par défaut (réglage `SecondServeur:Bascule:PromotionAutomatique` de l'API, variable `SecondServeur__Bascule__PromotionAutomatique`) ; `false` = bascule toujours manuelle |

### 7 bis. Essai de bascule réel avant la mise en service (obligatoire)

Décision du titulaire du 2026-11-25. Détail : parcours C, **étape 16**. Avant l'ouverture, sans client, prévoir une heure.

| # | Action | Attendu |
|---|---|---|
| 1 | Deux postes autorisés au moins, un employé connecté sur chacun **depuis plus de 3 minutes** ; un produit au panier sur l'un d'eux, **sans encaisser** | Contrôles 7.1 à 7.6 verts |
| 2 | **Débrancher le câble réseau du PC serveur** (ou arrêter l'API **et** PostgreSQL ensemble ; jamais l'API seule) | Bandeau « Vérification avec le 2e serveur en cours » en 15 à 30 s |
| 3 | Attendre | 2e serveur promu en 1 min 30 à 2 min (`role = principal`), postes basculés seuls, session et panier gardés, alerte au titulaire |
| 4 | Rebrancher l'ancien PC serveur | Il se met seul en lecture seule (`role = ecarte`) |
| 5 | Remettre en place : phase 8 (F4), puis garder cette répartition ou revenir à l'origine (F4, étape 4) | L'ancien PC est 2e serveur, `role = veille`, réplication à jour |

Pas de promotion au bout de 3 minutes : alerte et journal du 2e serveur, bascule manuelle F3, corriger la cause, refaire l'essai.
La promotion **saute les numéros** de tickets et factures (trou voulu) : **[À VÉRIFIER avec le comptable]**. Noter les heures (panne,
promotion, bascule de chaque poste) dans le dossier de l'officine.

---

## 8. Phase 7 : en cas de panne du PC serveur (procédure F3)

1. **Regarder d'abord si la bascule a déjà eu lieu toute seule** : postes avec « Ce poste est passé sur le 2e serveur, devenu serveur
   principal… », barre d'état « Connecté · 2e serveur », alerte de bascule au titulaire. Une vente interrompue par la panne part seule
   (« renvoyée automatiquement après la panne du serveur ») ; un panier simplement en cours reste à l'écran : cliquer sur « Encaisser ».
2. **Bascule automatique faite** : ne lancer **aucun script de bascule**. Faire seulement : isoler l'ancien PC serveur (arrêter ses services
   ou débrancher son câble), dernière copie de la GED si l'ancien est encore joignable, arrêter la copie de la GED sur le nouveau principal
   (`copier-ged.ps1 … -Desinstaller`), vérifier l'écran « 2e serveur », ne jamais remettre l'ancien en service sans la phase 9. F3,
   étapes 2, 2 bis, 3 bis, 5, 6 et 7.
3. **Pas de bascule automatique** (alerte « promotion manuelle nécessaire », égalité, postes restés sur « Serveur injoignable ») : décision
   du titulaire, puis `basculer-second-serveur.ps1 -AncienPrincipal <IP> -Schema https -PortApi 5443` (`-Essai` d'abord ; taper
   `BASCULER`) ; sur un poste resté sur le bandeau : bouton « Basculer sur le 2e serveur ». F3 complète.
4. **Synchronisation avec le serveur central** : si l'envoi se suspend (« Base revenue en arrière… »), le titulaire repart sur une nouvelle
   époque. F3, étape 7.

---

## 9. Phase 8 : retour du serveur réparé (procédure F4)

1. **Ne pas redémarrer l'API de l'ancien principal.**
2. Sur le **nouveau principal** : `preparer-principal.ps1 -AdresseSecond <IP de l'ancien> -AdressePrincipal <IP du nouveau>`,
   `ouvrir-port-base.ps1 -AdresseSecond <IP de l'ancien>`, `partager-ged.ps1 -AdresseSecond <IP de l'ancien>`. Puis les réglages de
   réplication synchrone (phase 4, étape 5.2) sur ce nouveau principal : ils ne suivent pas la réplication (contrôle :
   `SHOW synchronous_standby_names;`, `SHOW synchronous_commit;`).
3. Sur l'**ancien principal** : `remettre-en-second.ps1 -NouveauPrincipal <IP du nouveau>` (taper `OUI`) ; son ancienne base est **gardée**
   à côté (`<dossier>_ancien_principal_<date>`) : à examiner avant toute suppression. Le script ajoute le nom `second_serveur` à la copie.
   Puis `SecondServeur__AutreServeur = <IP du nouveau>` et démarrer l'API (veille).
4. GED : copie du nouveau vers l'ancien et tâche installée sur l'ancien (F4, étape 3 bis) ; documents d'avant la panne (F4, étape 3 ter,
   facultatif).
5. Postes : rien à faire (leur adresse de secours a été échangée à la bascule). Rebascule vers la répartition d'origine : facultative,
   à un moment calme (F4, étape 4).

---

## 10. Au coffre (récapitulatif)

| Secret ou fichier | Créé à | Utilisé à |
|---|---|---|
| Mot de passe du rôle de réplication | 2.2 | 3.3, phase 9 |
| Clé des jetons `Jwt__Cle` (même valeur sur les deux serveurs) | Parcours A | 3.3 (saisie masquée, contrôle d'empreinte) |
| Fichier de clé de scellement `.pocle` + son mot de passe (à part) | 2.6 | 3.3 |
| PFX du certificat du 2e serveur + phrase de passe | 3.2 | Renouvellement (825 jours, F6) |
| Mot de passe du compte `PharmaGedCopie` | 4.1 | 4.4, phase 9 |
| Mot de passe `postgres` | Parcours A | 2.1, 2.2, 3.3, 4.3, 5.2 |

---

## 11. Non encore essayé en réel (à surveiller à la première installation)

- Les scripts `initialiser-second-serveur.ps1` et `remettre-en-second.ps1` **sur deux vrais PC** (essais faits sur un seul PC, montages
  à la main) ; l'ajout du nom `second_serveur` par ces scripts est testé hors PostgreSQL seulement.
- Copie de la GED entre deux vrais serveurs (partage SMB hors domaine, droit du compte de tâche).
- Isolement du serveur principal (moins de la moitié des postes vus), égalité des postes.
- Connexion d'un poste sur un compte Windows standard après l'autorisation (preuve acceptée) : **[À VÉRIFIER]** au premier poste réel.
- CHIFA-Officine, lecteur de carte et Token sur le 2e serveur (question posée à la CNAS).
