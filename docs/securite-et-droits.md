# Sécurité et droits

## Comptes

- Un compte par employé, jamais de compte partagé. Mot de passe haché en bcrypt ; code court (PIN) prévu pour la caisse.
- Connexion : `POST /api/auth/connexion` renvoie un jeton JWT (12 h par défaut).
- Chaque connexion réussie (mot de passe ou code comptoir) est tracée avec le poste (table `connexion_employe`, script 058) : la surveillance signale un employé actif sur deux postes à la fois.
- La clé `Jwt:Cle` de `appsettings.json` est une valeur de développement : la remplacer en production par une clé aléatoire de 64 caractères au moins, propre à chaque pharmacie.

## Droits

- 34 permissions (table `permission`, constantes `Pharma.Domain.Securite.Permissions`, `database/init/seed_referentiel.sql`) ; la liste complète, avec les droits de départ de chaque rôle, est dans `docs/regles-metier.md` (Sécurité et droits, Droits par défaut). Ajouts : « stock.donner » (échantillon, gratuité, don ; script 056), « achats.valider_ecart » (écart de réception à l'aveugle ; script 057) et « caisse.remises_auto » (remises automatiques ; scripts 071-072).
- **Droits réglés par personne** (script 071) : 3 des 34 droits ne se règlent plus par rôle mais sur chaque employé, par le titulaire seul (fiche de l'employé, Utilisateurs et droits) : « caisse.remise » (remise maximale en %, 0 = pas de remise), « stock.donner » (peut faire un don) et « caisse.remises_auto » (peut gérer les remises automatiques). Ils sont absents de la matrice des rôles. Remise au-delà du maximum : la fenêtre « Faire autoriser » ne propose que les employés dont le maximum couvre la remise demandée. Le droit « rapports.marges » montre aussi la valeur d'achat et la TVA des pertes et dons, et la marge après pertes et dons.
- Rôles de départ : titulaire (tout, non modifiable), pharmacien assistant, vendeur, stagiaire. Le titulaire ajuste les droits depuis l’administration.
- Chaque point d’accès déclare sa permission : `.ExigerPermission(Permissions.StockConsulter)`. La vérification se fait en base à chaque appel, donc un droit retiré s’applique immédiatement.
- Certaines actions sont **réservées au titulaire** quel que soit le réglage des droits : `.ExigerTitulaire()` côté API et `ExigerTitulaireAsync` dans les cas d'usage (rôle système). C'est le cas des notifications : réglages, destinataires, messages de test et file d'attente ; et du réglage de la TVA à reverser sur les pertes et dons (Rapports, script 061, inscrit au journal `tva_sorties`) ; et du réglage « facture et retour fournisseur laissés » sur les postes (Caisse et contrôle › Règles, script 156, journal `taches_laissees_poste`, voir « Fichier local du poste » plus bas). Le poste reçoit le marqueur `role.titulaire` dans la liste des droits pour afficher ces écrans. Ce marqueur n'est **pas un droit du catalogue** : une action réservée au titulaire s'inscrit au journal (`journal_audit`) **sans code de droit**, car la clé étrangère `permission_code` n'accepte que les droits de la table `permission` (sinon erreur technique 500 ; corrigé le 2026-11-17 pour les réglages des serveurs, les seuils de réplication et l'export de la clé de scellement).
- **Nouvelle époque de synchronisation** (Administration › Synchronisation, après un retour en arrière de la base) : titulaire seul, mot de passe ressaisi (même vérification que « Changer mon mot de passe », `VerificationMotDePasseActuel`), aucun code SMS ni e-mail (décision du 2026-11-19) ; journal `synchronisation_nouvelle_epoque` avec `validation = mot_de_passe`, jamais le mot de passe. — `src/Pharma.Application/Synchronisation/RetourArriere.cs`
- **Patients › Patients en attente** (écran `patients/attente`, fenêtre et lien de la Caisse) : `caisse.vendre`, comme ses routes (`api/caisse/demandes-patients*`, `api/patients/{id}/consentement-sms`, `formulaire-sms*`). Recherche du produit (`ChoixProduit`, 2026-11-20) : `GET /api/produits` avec `stock.consulter`, sinon `GET /api/caisse/produits` avec `caisse.vendre`, sinon aucun produit ; `GET /api/produits` n'est **pas** ouvert à `caisse.vendre`.
- **Finances › Chifa › « Retours à corriger dans CHIFA-Officine »** (script 155) : `caisse.vendre` pour la liste (`GET api/chifa/retours-a-corriger`) comme pour « Corrigé dans CHIFA-Officine » (`POST api/chifa/retours-a-corriger/{id}/corrige`), à l'API (`ExigerPermission`) et dans le cas d'usage (`ConsulterVentesChifa`, `EnregistrerInfosChifa`) ; décision du titulaire du 2026-11-21 : droit gardé, comme les autres listes Chifa.
- Action sensible non autorisée : le poste demande le code d'un employé qui a le droit ; le journal enregistre qui a agi et qui a autorisé, et l'opération (annulation ancienne, retour) garde ce collègue comme validateur. Jamais par code : la délivrance d'un psychotrope (un employé habilité reprend la vente), et, sur une connexion par code comptoir, un droit hors du comptoir.
- Mot de passe provisoire : aucun droit, ni par mot de passe ni par code comptoir, tant qu'il n'est pas changé. Les routes sans droit particulier (`ExigerConnexion`) refusent aussi le provisoire et le super admin, sauf ce qui leur reste permis.
- Connexions révoquées (script 063) : mot de passe changé ou réinitialisé, compte désactivé, code comptoir effacé ou base restaurée → les connexions déjà ouvertes sont refusées à l'appel suivant (l'API relit l'état du compte à chaque appel).
- Réseau : chiffrer les échanges en HTTPS à la mise en production (parcours A, étape 4.6 de [mise-en-production.md](mise-en-production.md)).

## Garanties en base

- Le titulaire garde tous ses droits, et il reste toujours au moins un titulaire actif.
- `journal_audit`, `registre_psychotrope` et `boite_evenement` ne peuvent être ni modifiés ni supprimés.
- PostgreSQL n’accepte que les connexions locales de l’API (à régler dans `pg_hba.conf` à l’installation).
- Journaux chaînés (script 111, 2026-11-16) : chaque ligne porte l’empreinte SHA-256 de son contenu et de la ligne précédente ; ni modification, ni suppression, ni vidage (déclencheurs). Vérification chaque nuit et à la demande du titulaire.

## Intégrité des journaux : clé de scellement et rôles (2026-11-16)

- **Clé de scellement** : 32 octets aléatoires créés au premier démarrage de l’API dans `C:\ProgramData\PharmaOfficine\cle-integrite.dat` (réglable par `Integrite:FichierCle`), chiffrés par DPAPI en portée machine (illisibles sur un autre PC) ; fichier accessible seulement au système, aux administrateurs et au compte de l’API. Jamais en base, jamais dans `appsettings`, jamais dans un journal : seule son empreinte (16 caractères hexadécimaux) est affichée. Une clé illisible n’est jamais remplacée en silence (erreur). — `src/Pharma.Infrastructure/Securite/ScelleurIntegriteLocal.cs`
- **Clé de scellement : export et import (2026-11-16, 2e serveur, approuvé par le titulaire)** : **export réservé au titulaire** (Administration › 2e serveur › Clé de scellement ; route `POST /api/admin/second-serveur/cle/exporter`, `ExigerTitulaire`) dans un fichier `.pocle` chiffré par un mot de passe qu’il choisit (12 caractères au moins, saisi deux fois) : PBKDF2-SHA256 (600 000 tours, sel aléatoire de 16 octets) puis AES-256-GCM (nonce aléatoire, en-tête authentifié), fonctions intégrées à .NET, sans paquet externe. Le fichier est écrit sur le poste du titulaire, à ranger hors du PC (clé USB au coffre), le mot de passe à part. **Import** sur le 2e serveur (ou le PC de secours) en ligne de commande, l’API de secours ne servant pas tant que sa base est en lecture seule : `Pharma.Api.exe --importer-cle <fichier> [--remplacer]`, le titulaire tape le mot de passe ; la clé est rechiffrée DPAPI machine dans `Integrite:FichierCle`. Mauvais mot de passe ou fichier abîmé : refus, rien n’est écrit. Une autre clé déjà présente n’est remplacée qu’avec `--remplacer` (l’ancienne est gardée à côté, datée). **Jamais la clé ni le mot de passe au journal** : l’export est inscrit au journal des actions (empreinte et nom du fichier), l’import au journal du serveur (empreinte). `Pharma.Api.exe --empreinte-cle` affiche l’empreinte pour comparer les deux serveurs. Ce chiffrement porte sur la **clé seulement** : le chiffrement des données (lot 9) reste interdit sans l’approbation du titulaire. — `src/Pharma.Application/SecondServeur/CleScellementExport.cs`, `src/Pharma.Api/Securite/ConsoleCleScellement.cs`
- **2e serveur** : écran « 2e serveur » réservé au titulaire ; réplication par un rôle PostgreSQL dédié (`pharma_replication`, LOGIN REPLICATION) autorisé dans `pg_hba.conf` pour la seule adresse du 2e serveur (`scram-sha-256`) ; mots de passe demandés par les scripts, jamais écrits dans un fichier. La bascule se fait sur le 2e serveur par un administrateur (script), aucun service réseau n’a le droit de promouvoir la base. **Copie de la GED (2026-11-17)** : la fréquence se règle dans l’écran (titulaire seul, journal avant/après) ; la tâche de copie du 2e serveur la lit dans sa base locale avec le rôle `pharma_ged_lecture` (connexion seulement, sessions en lecture seule, droit de lire une seule colonne, aucun mot de passe stocké) ; connexion par **authentification Windows (SSPI)**, décision du titulaire du 2026-11-17 : le `pg_ident.conf` n'associe à ce rôle que le compte Windows de la tâche de copie sur ce PC (`PharmaGedCopie@<NOM DU PC>`), et le `pg_hba.conf` porte pour ce seul rôle deux lignes `sspi map=pharma_ged_copie` limitées à la boucle locale (127.0.0.1 et ::1), sans autre règle ni ouverture réseau ; un autre programme du PC, sous un autre compte Windows, est refusé ; les anciennes lignes `trust` sont retirées (`autoriser-lecture-copie-ged.ps1`). — [mise-en-production.md](mise-en-production.md), « 2e serveur »
- **Sceau** : HMAC-SHA256 calculé par l’application avec cette clé sur le dernier rang et la dernière empreinte d’un journal : quelqu’un qui a accès à la base seule peut recalculer les empreintes, pas les sceaux. Version cloud : adaptateur `IScelleurIntegrite` à venir (coffre de clés de l’hébergeur).
- **Rôles PostgreSQL** : `pharma_app` est encore propriétaire de la base et peut donc désactiver un déclencheur. La séparation (propriétaire `pharma_proprietaire`, `pharma_app` sans `UPDATE`/`DELETE`/`TRUNCATE` sur les journaux) est écrite dans `database/migration/111b_roles_integrite.sql`, **non exécuté** : à faire par l’administrateur (superutilisateur), voir [mise-en-production.md](mise-en-production.md), « Séparation des rôles ».


## Réglages mutualisés (2026-11-17)

Détail : [conception-synchronisation.md](conception-synchronisation.md) § 16 bis ; règles : [regles-metier.md](regles-metier.md).

- **Seul l'administrateur du serveur central publie** un modèle, change le mode (proposé, imposé), libère ou rétablit une officine : outil `Pharma.Cloud.Api --publier-reglage`, `--mode-reglage`, `--liberer-reglages`, `--retablir-reglages`, avec `--auteur` obligatoire. Les fonctions SQL d'administration (scripts centraux 010 et 011) n'ont pas de droit `EXECUTE` pour `PUBLIC` ni pour `pharma_cloud_api` : l'API centrale ne fait que lire (`cloud_descendants`).
- **Double validation** : à la publication (outil) et à la réception (officine), par les mêmes règles du domaine ; liste fermée de 11 tables ; une valeur invalide est rejetée en bloc pour sa table, journalisée, sans arrêter la synchronisation.
- **Dans l'officine** : la liste des 11 réglages (`GET /api/reglages-mutualises`) exige `admin.parametres` ; consulter une table et adopter sa valeur proposée (« Adopter ») exigent le droit de saisir cette table : `admin.parametres` (motifs, alertes, substitution, formulaire SMS), `caisse.remises_auto` (remises automatiques), sinon le titulaire.
- **Table imposée** : toute modification locale est refusée par le cas d'usage (409, champ `reglage_impose`) **et** par la base (déclencheur `zz_reglage_garde`, script 141). Seules les heures d'ouverture et de fermeture du contrôle interne restent modifiables.
- **Marqueur `pharma.reglage_central`** : il laisse passer l'écriture d'un modèle reçu. Il est posé seulement par `ReglagesMutualisesSql` (dans sa transaction) et par les outils de rejeu et de restauration (`PGOPTIONS`). Il protège contre une erreur de l'application, pas contre le compte de la base de l'API.
- **Traçabilité** : journal du central (`--journal-reglages` : qui, quand, compte Windows, poste, mode et version) ; journal de l'officine (`journal_audit`, valeurs avant et après de chaque application, adoption, libération ou modification locale).
- **Données** : aucun nom de patient ni mot de passe dans un modèle ; l'auteur est l'identifiant de l'administrateur du central.

## Fichier local du poste avec des données personnelles : tâches laissées (2026-11-21)

- **Fichier** : `%LOCALAPPDATA%\PharmaOfficine\taches-laissees.dat` (profil Windows de la session du poste). Il garde la facture et le retour fournisseur laissés au « Changer d'utilisateur » ; la facture peut contenir le **nom, le prénom et le n° d'assuré d'un patient**, le nom de l'employé qui l'a laissée et des textes libres (commentaire, lignes libres).
- **Protection** : contenu JSON chiffré par **DPAPI en portée utilisateur Windows** (`DataProtectionScope.CurrentUser`, complément propre au fichier) : illisible sous un autre compte Windows ou sur un autre PC ; le fichier temporaire d'écriture est chiffré lui aussi : **rien n'est écrit en clair**. Contrôlé par `tests/Pharma.Application.Tests/FichierTachesLaisseesTests.cs` (« Aucune donnée en clair »).
- **Durée** : 7 jours au plus ; tâche reprise retirée du fichier ; fichier supprimé quand il n'y a plus de tâche ; fichier illisible mis de côté puis supprimé après 7 jours.
- **Réglage de l'officine, titulaire seul** (script 156, `parametre_caisse.taches_laissees_poste`) : « Chiffré (AES-256, compte Windows) » (défaut) ou « Désactivé (gardé en mémoire seulement, perdu au redémarrage) » ; aucune valeur « en clair » possible (contrainte en base, contrôle dans `ConservationTachesLaissees.Verifier`). Changement : `PUT /api/admin/reglage-taches-laissees`, **rôle de titulaire exigé** (filtre `ExigerTitulaire` et `ExigerTitulaireAsync` dans le cas d'usage ; le droit `admin.parametres` seul ne suffit pas), inscrit au journal des actions (avant / après). Lecture : `GET /api/reglage-taches-laissees`, **tout employé connecté** (mot de passe provisoire compris), droit minimal car la valeur n'est pas sensible et chaque poste doit la connaître. « Désactivé » : le poste supprime le fichier, son fichier temporaire et ses copies mises de côté à la prochaine connexion (ou à la prochaine écriture) et n'écrit plus rien sur le disque. Avant la première connexion qui suit le démarrage, le poste ne lit, n'écrit ni ne supprime le fichier.
- **Journal** : seuls le nom du fichier et le type d'erreur vont dans `erreurs.log`, jamais le contenu.
- Ce chiffrement local ne relève pas du lot 9 (chiffrement des données en base, des sauvegardes et au central), qui reste soumis au « go chiffrement ».

## Journaux sans données personnelles

Règle (2026-11-16) : les fichiers journaux du serveur (`C:\ProgramData\PharmaOfficine\Journaux\pharma-api-*.log`), du serveur central (`Journaux\Central\central-api-*.log`, `central-ecrans-*.log`, depuis le 2026-11-21) et du poste (`%LOCALAPPDATA%\PharmaOfficine\erreurs.log`) ne contiennent aucune donnée personnelle : ni nom ou prénom de patient, de médecin ou d'employé, ni n° d'assuré, n° de pièce, téléphone, e-mail, adresse, ni texte libre saisi (motifs, observations). Le journal d'audit en base (`journal_audit`) n'est pas concerné.

- **À la source** : on journalise des identifiants techniques (id, code d'action, type d'alerte, code d'erreur), jamais le texte. Refus métier : chemin, type d'exception, champ et statut, jamais le message (qui part au poste seulement). Refus de la base : code SQL, contrainte, table, sans le texte de la base. Requête mal formée : nom du paramètre seulement. Code super admin : action et canal (SMS ou e-mail), jamais le destinataire.
- **Filet de sécurité** : `Pharma.Shared.MasqueurJournal` est appliqué à chaque ligne écrite (message rendu et pile d'exception complète) par `Pharma.Shared.Journalisation.JournalFichierProvider` (serveur de l'officine et serveur central) et par `App.NoterErreur` (poste). Il masque : e-mail → `a***@d***.dz` (1er caractère, 1er caractère du domaine, extension) ; téléphone mobile algérien 05/06/07 ou +213/00213, séparateurs espace, point ou tiret admis → `06******12` ; nombre de 9 à 12 chiffres avec suffixe `/NN` facultatif (n° d'assuré) → `6907******42/80`. Les noms et textes libres ne sont pas reconnaissables : seule la règle « à la source » les protège.
- Contrôlé par `tests/Pharma.Architecture.Tests/JournauxSansDonneesPersonnellesTests.cs`.

## Barre d'état (2026-11-21)

- `GET /api/etat-poste`, `GET /api/etat-poste/compteurs` et `GET /api/synchronisation/etat-poste` : ouvertes à **tout employé
  connecté** (aucun droit d'administration), nombres et états techniques seulement (ni nom, ni montant, ni adresse du central) ;
  chaque compteur n'est rendu qu'à l'employé qui a le droit de son écran (0 sinon). Détail : regles-metier.md « Barre d'état ».

## Comptes distants des écrans du serveur central (lot S7.1, 2026-11-21)

Détail : [ecrans-central.md](ecrans-central.md) § 5. En local seulement à ce jour.

- **Comptes** (`compte_distant`, script central 030) distincts des employés de l'officine : rôle `editeur` (S7.1) ou `titulaire` (S7.2). Créés
  par la console du central (`--creer-compte-editeur`, `--creer-compte-titulaire`) **ou, depuis le 2026-11-21 (décision du titulaire), par
  l'éditeur dans l'écran « Comptes »** (« Ajouter un compte » : éditeur seulement, code de confirmation exigé, script central 039 ; jamais par un
  titulaire) ; le premier éditeur d'une installation est toujours créé par la console. Mot de passe **provisoire** tiré au sort par le central,
  affiché une fois (seule l'empreinte est gardée), à changer à la première connexion ; empreinte BCrypt coût 12 ; second facteur obligatoire à chaque connexion **et** à chaque action sensible.
- **Éditeur** : état technique de toutes les officines, **jamais** de donnée nominative ni de contenu (décision n° 4) ; actions : créer une
  officine, nouveau code d'activation, révoquer un certificat ou un serveur (motif obligatoire, ressaisie, journal des accès et journal de
  sécurité). Départ, effacement, purges, familles et autorité restent en console.
- **Base** : rôle `pharma_cloud_ecrans` sans connexion et **sans aucun droit sur les tables** ; seulement les fonctions `cloud_ecran_*`, qui
  revérifient le jeton de session ; l'officine courante est posée par la base après vérification du droit.
- **Verrouillage** 5 échecs → 15 min, doublé jusqu'à 24 h ; même message pour un identifiant inconnu ou un mot de passe faux ; limites de débit.
- **Journal des accès** au central : ajout seul, chaîné SHA-256, sans texte libre ni donnée nominative ; « Mon journal des accès » pour chaque compte.
- **Titulaire (lot S7.2a, script central 032)** : compte créé par la console (`--creer-compte-titulaire … --auteur`), jamais par un écran ;
  il ne voit que **ses** officines, rattachées par un **code de rattachement** (un compte, une officine, 30 minutes, usage unique, empreinte
  seule en base ; 5 refus par quart d'heure ; code d'un autre compte : même réponse qu'un code inconnu). Droits par officine : `consulter`
  (tableau de bord : **totaux seulement**, aucun patient, aucune vente détaillée) et `journal_acces` (journal des accès de l'officine, y
  compris ceux de l'éditeur, sans identifiant ni nom). Sessions : 30 minutes d'inactivité, 8 heures au plus.
- **Réglages communs du titulaire (lot S7.4, script central 035)** : un titulaire à distance **propose** des seuils d'alerte (liste
  fermée de 9 réglages, bornes) à ses officines où il a le droit `ordres` (**chacune**, sinon rien n'est envoyé) ; code de second facteur
  à chaque envoi, 5 envois par heure ; jamais prix, TVA, droits, comptes ni rien de fiscal (refusé par l'écran, la base et le poste).
  **Rien ne s'applique** sans l'acceptation du titulaire de l'officine sur son poste, **mot de passe ressaisi** (aussi pour refuser) ; un
  autre employé ne décide pas. Le titulaire à distance ne voit que ses propres envois et leur état, jamais le motif d'un refus local.
- **Envoi des codes de second facteur (lot S7.6, script central 037)** : canal (e-mail, SMS, les deux) et serveur d'e-mail réglés dans
  l'écran « Envoi des codes » par l'**éditeur seulement** (titulaire refusé par l'écran et par la base), **code de second facteur** pour
  enregistrer. Le **mot de passe SMTP** est chiffré par l'application (protection des données, clés dans le dossier du central, hors
  dépôt ; DPAPI machine sous Windows) : la base ne garde que le texte chiffré (un texte en clair est refusé par la contrainte), la
  lecture de l'éditeur ne rend que « renseigné oui/non », le champ n'est jamais réaffiché (vide = inchangé), jamais dans une adresse,
  un journal ni la console. Chaque lecture, modification, e-mail d'essai et envoi de code est noté au journal des accès (chaîné) ;
  chaque modification au journal de sécurité, sans mot de passe, identifiant, adresse ni numéro. Contact d'un compte (e-mail ET
  téléphone, au moins un) modifiable par l'éditeur avec un code, affiché masqué. SMS : aucun fournisseur, refus clair, jamais un
  faux succès. Réglage incomplet en production : refus clair (ou variables `PHARMA_CLOUD_SMTP_*` si elles sont complètes).
- **Lignes absentes (lot S7.5, script central 036)** : l'éditeur (fiche d'une officine) et le titulaire (ses officines, droit
  `consulter`) ne voient que des **nombres** : lignes absentes par table, n° du chargement qui les a constatées, date, cause probable ;
  jamais le contenu ni la clé d'une ligne. Les fonctions comptent dans les tables miroir avec l'officine posée par la base (RLS forcée)
  et ne lisent que `officine_id`, `sync_epoque` et `sync_absente_le` ; le regroupement interne n'est accordé à aucun rôle.
- **Multi-officines et ordres du titulaire (lot S7.3, script central 034)** : le droit `ordres` d'un compte distant sur une officine ne
  s'accorde (et ne se retire) que **depuis le poste** de l'officine, par le titulaire, mot de passe ressaisi ; le retrait d'un compte
  coupe l'accès **tout de suite** et annule ses ordres encore ouverts. Ordres du titulaire : liste fermée (essai, sauvegarde, vérification
  d'intégrité ; aucun sur les ventes, stocks, prix ou comptes, revérifiée par la base), code de second facteur à usage unique par ordre,
  24 heures, 10 par heure ; la sauvegarde attend la confirmation du titulaire local. Vue d'ensemble : officines du compte seulement,
  totaux seulement.
- **Accès à distance depuis le poste (lot S7.2b, script central 033)** : seul le **titulaire** de l'officine génère un code de rattachement
  (Administration › Synchronisation › Accès à distance), avec son **mot de passe ressaisi** (essais comptés, blocage) et la synchronisation
  activée. Le poste demande le code au central par `POST /api/v1/rattachement` (certificat client + jeton) : **l'officine est celle du
  certificat**, jamais un champ ; la base revérifie le certificat. Compte inconnu, non titulaire ou désactivé : **même réponse** (aucune
  énumération) ; 5 demandes par heure et par officine. Le code est montré une fois, jamais gardé ni journalisé (empreinte seule au central).
- **Remise à zéro d'un mot de passe (script central 038)** : par l'**éditeur** seulement, jamais sur son propre compte (refus de l'écran
  et de la base), compte actif ; motif, identifiant ressaisi et **code de second facteur**. Le mot de passe provisoire est tiré par les
  écrans, **montré une seule fois à l'éditeur** (mémoire du processus, 2 minutes, même session) et **jamais envoyé par e-mail** (le code
  de connexion part déjà sur ce canal : les deux facteurs ne voyagent jamais ensemble) ; seule l'empreinte BCrypt va en base ; jamais
  dans un journal, une adresse ou la console. Effets : changement obligatoire à la connexion suivante, toutes les sessions du compte
  fermées, codes en cours annulés, verrou levé ; journal des accès (aussi dans le journal du compte remis à zéro) et journal de sécurité
  (avec le motif).
- **Retrait d'un rattachement par le titulaire (script central 038)** : le titulaire retire lui-même une de ses officines (nom ressaisi,
  code de second facteur) ; accès coupé **tout de suite**, ses ordres encore ouverts annulés ; l'éditeur ne retire aucun rattachement.
- **Message à afficher sur les postes (script central 038, script local 160)** : ordre du titulaire à distance (droit `ordres`, code de
  second facteur) ; **texte simple** de 200 caractères au plus, une ligne, sans balise, caractère invisible ni lien, affiché 7 jours au
  plus, contrôlé par l'écran, la base centrale (fonction et contrainte) et le poste ; affiché en texte (jamais interprété ni cliquable)
  à **tous les employés connectés** ; seul le **titulaire de l'officine** peut le fermer (journal des actions, sans le texte). Le texte
  n'est jamais écrit au journal de sécurité du central.
- **Rôles étanches** : le titulaire n'ouvre aucun écran ni aucune action de l'éditeur, l'éditeur aucun écran du titulaire ; refus par les
  écrans (« Accès refusé ») **et** par la base (rôle revérifié dans chaque fonction).
