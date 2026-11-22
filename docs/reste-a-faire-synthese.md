# Reste à faire : synthèse

Point d'avancement du 2026-11-22 : **tout est commité** jusqu'à `d429b90` (branche `develop`, rien poussé depuis `f399e6d` ou avant :
l'utilisateur pousse lui-même). Scripts centraux **037 à 040** passés sur `pharmacie_cloud_dev` (sauvegardes dans
`C:\MesAtlassian\sauvegardes-bd` avant 037, après 038, avant 039 et avant 040) ; script local **160** passé sur `pharmacie_dev` et
`pharmacie_test`. Détail : [a-faire.md](a-faire.md), [fonctionnalites.md](fonctionnalites.md) (inventaire complet) et documents cités.
À mettre à jour à chaque point d'avancement.

## 1. En cours

Rien en cours de développement. Démonstration cloud : étapes 1 et 2 écrites, premier démarrage réel en attente de Docker (section 3).

## 2. Fait et commité les 2026-11-21 (soir) et 2026-11-22

- `c3b8532`, `98fbae7` : tuiles navigables du poste et du central, aide fermée au clic extérieur, thème du central sans bouton,
  Reprise étape 5 sans défilement.
- `076df06` : **codes de second facteur par Gmail** (S7.6, script central 037) : écran « Envoi des codes », « SMS » seul refusé sans
  fournisseur, e-mail d'essai (mot de passe enregistré envoyé seulement au serveur enregistré, serveur contrôlé, refus 535 reconnu),
  console `--envoi-codes` (amorçage en production) et `--contact-compte` ; en développement, code dans le fichier ET par e-mail.
  **Reste S7.2/S7.3** (038, local 160) : remise à zéro d'un mot de passe par l'éditeur, retrait d'un rattachement par le titulaire,
  message aux postes (un nouveau remplace le précédent). **Création d'un compte à l'écran** (039). Saisie gardée après un refus
  ([charte-graphique.md](charte-graphique.md) « Erreurs de saisie »). Journal en fichiers partagé (API locale, API et écrans du
  central ; section `Journaux` de chaque `appsettings.json`). Envoi réel par Gmail essayé avec succès (e-mail d'essai et codes).
- `2902442` : tableau des comptes élargi et pleine hauteur (`ecrans.js` ne retranche plus les colonnes voisines).
- `3a79e77` : **script central 040** : import CSV de comptes (écran et console `--creer-comptes`, un seul code, tout ou rien,
  200 lignes), identifiant envoyé par e-mail et mot de passe jamais par e-mail (SMS si fournisseur, sinon à transmettre à la main),
  durée de vie des mots de passe provisoires réglable (1 à 30 jours) et « Régénérer le mot de passe », déconnexion après inactivité
  réglable par rôle (un utilisateur actif n'est jamais déconnecté, bandeau « Rester connecté »), jeton anti-falsification exigé sur
  toutes les actions, envoi indépendant du navigateur (délai propre de 30 s), navigateur ouvert sur la page de connexion depuis
  Visual Studio.
- Tests au dernier passage : Domain 718, Application 1 816, Architecture 386, Cloud 145 (+ 1 ignoré), Cloud.Web 52 ; SQL 030 à 040
  et 160 réussis.

## 2 ter. À faire en premier (point du 2026-11-22, 03:30)

Commités depuis `3a79e77` : `141f058` (7 points de la revue du central, adresses `.invalid` jamais envoyées, tableau des comptes et
aperçu d'import, wilaya du médecin à la caisse) et `d429b90` (montant reçu obligatoire en espèces). **À pousser** par l'utilisateur :
`d429b90`, `141f058`, `c617380`, `3a79e77`, `2902442`, `076df06`, `98fbae7`, `c3b8532`.

**Mesures et essais non faits**
- Page « Ordres de l'officine » (titulaire) après la revue (« Durée d'affichage » sans heure) : non mesurée (connexion titulaire =
  code envoyé dans la boîte du titulaire ; prévoir un compte titulaire de mesure en `@exemple.invalid`, comme `editeur.mesure`).
- Bandeau « Rester connecté » et message « Session fermée après inactivité » (lot 040) : jamais vus dans un navigateur (régler
  l'inactivité sur 5 min pour l'essai).
- Liste des mots de passe provisoires après un vrai import CSV (`/comptes/mot-de-passe-affiche` à plusieurs comptes) : non vue.
- Caisse : ligne « Durée du traitement » collée à son libellé (psychotrope au panier) non vue ; enregistrement réel d'une wilaya
  depuis la caisse non essayé (aucun médecin de `pharmacie_test` n'a de wilaya).
- Formulaire « contact » de « Comptes » : vérifier que l'e-mail et le téléphone saisis reviennent après un refus (`ecrans.js` les
  garde dans l'onglet ; l'agent de la revue a dit le contraire sans essai).

**À corriger ou à décider**
- Test instable `TicketRouleauTests.Hauteur_selon_le_contenu_sans_saut_de_page` : 1 échec en suite complète le 2026-11-22, réussi
  seul et à la relance (sensible au parallélisme ?) ; à stabiliser.
- Montant reçu obligatoire : exigé par l'écran de la caisse seulement ; le serveur (`Reglement`) accepte toujours un montant absent
  comme appoint exact. Décider s'il faut l'exiger aussi côté API.
- Caractère NUL dans un message aux postes : refusé par l'écran et le poste ; refus par PostgreSQL (038) supposé, non vérifié.
- 2e serveur en local : plan proposé le 2026-11-22 (seconde instance PostgreSQL 5433 répliquée, seconde API 5082, slot
  `second_serveur`, redémarrage du PostgreSQL de développement, retour arrière) ; attend « go 2e serveur local ».
- Bases jetables à supprimer par l'utilisateur (suppression refusée à l'assistant) : `pharmacie_jetable_m4_*` (3),
  `pharmacie_jetable_rejeu_*` (3), `pharmacie_jetable_sugg_*` (2) ; à décider : `pharmacie_essai_restauration*`,
  `pharmacie_recette_*`.
- SMS : aucun fournisseur (codes, mots de passe provisoires) ; alertes de refus de connexion en série.
- Rappel : le script central 038 a été passé sans sauvegarde juste avant (sauvegarde « après 038 » faite ensuite).

**Comptes de développement du central** (`pharmacie_cloud_dev`, jamais livrés) : `editeur.dev`, `titulaire.dev` (e-mail du
titulaire), `editeur.mesure` (`mesure@exemple.invalid`, codes dans le fichier seulement, pour les mesures), `01540`, `12345`.

## 2 bis. Fait et commité le 2026-11-21 (`f71a011` à `20c1ecf`)

- **Retour d'une vente régularisée « rendu au patient »** (script 159, décision du titulaire « Régularisation ok ») : retenue au prorata
  de ce qui a déjà été rendu au patient pour les boîtes rapportées, prise sur le remboursement ; si elle dépasse, le reste devient
  une dette (relevé, journal, alerte « Patient prévenu ») ; puis la règle 158 sur ce qui reste à rendre. Script 159 passé deux fois
  sur `pharmacie_dev` et `pharmacie_test` après sauvegarde, test SQL réussi sur les deux ; `002_tables_miroir.sql` repassé sur
  `pharmacie_cloud_dev`. Écran mesuré (vente T-005928 de `pharmacie_test`) : 0 px à 1 366 × 768 et 1 536 × 890, clair et sombre.
  À confirmer par le titulaire : annulation après un retour avec dette ajoutée (dette laissée due) ; « à rendre » pas encore remis
  par le tiroir (retenue sur tout le montant) ; journal fiscal à montrer au comptable.
- **Écrans du central (S7.1)** : défaut corrigé — la CSP `base-uri 'none'` faisait ignorer `<base href="/">` (feuille de style,
  script et logo en 404 sur les pages `/officines/…`, rubrique « Lots reçus » défilant de 532 px) → `base-uri 'self'`, test ajouté ;
  19 pages mesurées à 0 px aux deux tailles, clair et sombre (compte `editeur.dev`, mot de passe changé le 2026-11-21, transmis au
  titulaire dans la conversation).
- **Écrans du titulaire au central (S7.2a, côté central seulement ; le poste ne change pas)** : script central **032**
  (`compte_officine.droits`, `code_rattachement`, rattachement, tableau de bord en totaux, journal des accès de l'officine), console
  `--creer-compte-titulaire` / `--code-rattachement`, écrans `/mes-officines`, `/rattacher`, `/mes-officines/{officine}`,
  `/mes-officines/{officine}/journal`, compte `titulaire.dev` dans `preparer_central_vs.ps1`. Script central 032 passé sur
  `pharmacie_cloud_dev` ; quatre écrans mesurés à 0 px (1 366 × 768 et 1 536 × 890, clair et sombre).
  Tests sur bases jetables (supprimées) : SQL 030 à 032, Pharma.Cloud.Web.Tests, ComptesDistantsConsoleTests, rejeu des scripts centraux.
- **Barre d'état : liaisons avec le serveur central et le 2e serveur** (demande du titulaire « 7 quand le cloud est déployé sinon
  rien », « pareil pour 2e serveur ») : un élément seulement en anomalie (« Central : en attente (2 h 05) », « Central : erreur
  d'envoi », « 2e serveur : décroché »…), rien si la liaison n'est pas en place ou à jour ; route `GET /api/synchronisation/etat-poste`
  (tout employé connecté, sans donnée sensible, gardée 30 s) ; lien vers l'écran pour qui y a droit. Aucun script. Barre mesurée dans
  le cas le plus chargé (5 éléments) : une ligne à 1 366 × 768 et 1 536 × 890, clair et sombre (page de mesure, pas l'application lancée).
  À voir sur un poste relié à un vrai central et avec un vrai 2e serveur ; à confirmer par le titulaire : seuils repris de la supervision
  (attention au seuil du bandeau, anormal au seuil d'alerte de retard).
- **Barre d'état : éléments fixes et pastilles** (maquette validée « ok maquette », 2026-11-21) : fixes (Connecté, poste · vendeur,
  date et heure, version en infobulle) ; pastilles seulement s'il y a une information, cliquables selon les droits ; regroupement
  « À traiter (N) ▾ » dès qu'une anomalie s'affiche (au plus 4 anomalies visibles) ; route `GET /api/etat-poste/compteurs` (tout
  employé connecté, compteurs filtrés par les droits, gardée 30 s). Aucun script. Mesurée sur page de mesure (vrai app.css, Poppins) :
  une ligne à 1 366 × 768 et 1 536 × 890, clair et sombre. **Non faites** (pas de source fiable) : « Mise à jour disponible » ;
  « Imprimante injoignable » en direct (seul l'échec d'envoi d'un ticket est signalé). Mesurée aussi dans l'application lancée
  (`pharmacie_test`, titulaire et vendeur) : 0 px ; à confirmer
  par le titulaire : regroupement dès une seule anomalie même pour 1 pastille grise, seuil de 4 anomalies visibles, déconnexion
  gardée quand le serveur est injoignable, libellés « 5 volets Chifa » et « 12 Chifa à déposer » en deux pastilles.
- **Mesures** (charte, « Mesures du 2026-11-21 ») : fenêtre « Changement d'utilisateur impossible » de la Caisse (serveur arrêté),
  0 px, panier gardé par « Rester sur la vente ».
- Compilation : 0 erreur, 0 avertissement ; tests : 2 936 réussis, 1 ignoré (déjà ignoré), 0 échec (avant S7.4).

## 3. Ensuite

- [x] **Push** des commits locaux par le titulaire : fait (`develop` = `origin/develop`).
- [ ] **Port 5432 du poste de développement** (par le titulaire, PowerShell administrateur) : `listen_addresses = 'localhost'` dans
      `C:\Program Files\PostgreSQL\18\data\postgresql.conf` (copie `postgresql.conf.avant-localhost` gardée), puis `Restart-Service postgresql-x64-18`,
      Visual Studio arrêté avant. Constat du 2026-11-18 : écoute sur toutes les adresses, `pg_hba.conf` limité à 127.0.0.1 et ::1, aucune règle de pare-feu.
- [ ] **Décisions du titulaire du 2026-11-19, à réaliser** (sur « go ») :
  - [x] **vérifié le 2026-11-20 : conforme (mot de passe seul), doc alignée** — nouvelle époque de synchronisation : **ressaisie du
    mot de passe du titulaire seule**, pas de code SMS ni e-mail ;
  - [x] **fait le 2026-11-20 (script central 026 : `cloud_table_miroir.archive_remplacee` faux pour les deux tables)** — archive du central (`ligne_remplacee`) : **exclure** `alerte` et `alerte_surveillance` (tables recalculées au démarrage) ;
  - [x] **fait le 2026-11-20 (gardes dans 010 et 013 à 017 ; test `RejeuScriptsCentrauxTests`)** — rejeu des scripts du central : **protéger** `cloud_descendants` (010 → 011) et `cloud_creer_ordre` (013 → 018) comme les autres fonctions ;
  - [x] **fait le 2026-11-20 (produit choisi par la recherche du comptoir sans `stock.consulter`, lien dans la colonne de la Caisse ;
    captures de la Caisse faites le 2026-11-20 : 0 px à 1 366 × 768 et 1 536 × 890, clair et sombre ; essai dans le poste le 2026-11-20 avec un compte qui n'a que `caisse.vendre`,
    sur une copie jetable supprimée ensuite : menu Patients et lien de la Caisse visibles, produit trouvé, `GET /api/produits` refusé)** — **Patients en attente dans le menu** pour un employé qui n'a que `caisse.vendre` (en plus de la fenêtre de la Caisse) ;
  - [x] **fait le 2026-11-20 (script central 027, F8 étape 4)** — départ d'une officine : **effacer aussi les éléments techniques** (époques, chargements, éléments descendants, ordres,
    anomalies d'intégrité), dans le même effacement que les données, donc **jamais avant la fin de la conservation de 6 ans**
    après le départ (étape 7.8 : 6 ans minimum, refus en dessous ; précision du titulaire du 2026-11-19) ; **garder** la fiche,
    les certificats révoqués et les journaux (preuves) ;
  - [x] **fait le 2026-11-20 (script central 028)** — `cloud_effacer_famille` **étendu à `ligne_remplacee`** (famille décochée retirée aussi des lignes archivées) ;
  - [x] **fait le 2026-11-20 (zip chiffré AES-256 au format WinZip AE-2 par la bibliothèque SharpZipLib 1.4.2, licence MIT ;
    mot de passe de 24 caractères tiré au sort, affiché une seule fois, écrit nulle part ; aucune copie en clair sur le disque ;
    empreinte SHA-256 de l'archive chiffrée au journal des départs et sur l'accusé de réception ; F8 à jour ; tests
    `DepartArchiveChiffreeTests` ; ouverture avec 7-Zip sur un poste réel à constater)** — zip chiffré de l'archive de départ ;
  - [x] **fait le 2026-11-20 (option `--email` de `--depart-officine`, compte SMTP de l'éditeur en variables `PHARMA_CLOUD_SMTP_*`,
    TLS obligatoire, MailKit ; adresse non enregistrée, masquée au journal des départs ; échec non bloquant ; affichage unique gardé ;
    tests `DepartEnvoiMotDePasseTests` ; compte SMTP de l'éditeur à créer et premier envoi réel à essayer)** — mot de passe de
    l'archive de départ envoyé par e-mail au titulaire ; règle des deux canaux : si un jour l'archive part par un lien envoyé par
    e-mail, le mot de passe ne part plus par e-mail ;
  - [ ] **restant : mot de passe de l'archive de départ par SMS** (aucun fournisseur SMS choisi pour le serveur central) ;
  - [ ] **restant : lien de téléchargement à durée limitée** (dans aucun lot ; d'ici là, remise en main propre sur clé USB) —
    remise de l'archive de départ **au choix du titulaire** : clé USB en main propre (possible) ou lien à durée limitée
    avec mot de passe envoyé par un autre canal ; **accusé de réception portant l'empreinte SHA-256** dans les deux cas (fait, F8) ;
  - [x] **fait le 2026-11-20 (voir la ligne « suggestions de saisie (2026-11-20) »)** — suggestions de saisie : **ne plus proposer les libellés de trésorerie saisis à la main** (risque de nom de client) ;
  - [x] **fait le 2026-11-20 (script 154 et init ; règle stricte et rattrapage, reprise, « Produits à classer » avec reclassement groupé,
    catégorie dans « Modifier un produit », DCI/dosage/forme facultatifs, TVA 19 % proposée (confirmée le 2026-11-20 ; reprise gardée telle quelle, confirmé le 2026-11-20), pastille Stock, rapports
    parapharmacie / prestations ; script 154 passé deux fois sur `pharmacie_dev` et `pharmacie_test` après sauvegarde
    vérifiée (0 produit rattrapé : la parapharmacie de démo était déjà classée), tests SQL 154 réussis sur les deux ; règle stricte
    corrigée pour une classe NULL ; mesures 0 px à 1 366 × 768 et 1 536 × 890, clair et sombre, captures regardées : Produits à
    classer, Version et catégories, Stock › Produits (pastille), Nouveau produit (catégorie parapharmacie), Rapports Jour, Semaine
    et Mois (répartition et retours clients passés dans leurs propres tuiles : la vue Mois défilait de 102 px à 1 366 × 768) ;
    Modifier un produit, fiche ouverte : 127 px à 1 366 × 768 (formulaire ouvert, toléré ; la catégorie partage la ligne de la
    contenance), 0 px à 1 536 × 890)** — parapharmacie distinguée dans le catalogue (V1) ;
  - démo : **démonstration cloud** (Dockerfile, docker-compose local, mode démo) — détail : [demo-cloud.md](demo-cloud.md) ;
    - [x] **étape 0 faite le 2026-11-20 : API utilisable sous Linux** (secrets locaux : DPAPI inchangé sous Windows, DataProtection
      ailleurs ; outils PostgreSQL sans `.exe` et cherchés dans le PATH ; racine des données `Donnees:Racine` ; polices PDF
      Liberation Sans embarquées hors Windows, licence OFL ; en-têtes de proxy seulement pour `ProxysConnus` ; build et tests
      réussis sous Windows, tests `ApiSousLinuxTests` ; **aucun démarrage réel sous Linux encore**) ;
    - [ ] étape 1 : Dockerfile de l'API et docker-compose local (API + PostgreSQL + volume des données, client PostgreSQL,
      ICU, `TZ=Africa/Algiers`) — **fichiers écrits le 2026-11-20** (`src/Pharma.Api/Dockerfile`, `.dockerignore`,
      `deploy/demo/` : compose, `.env.example`, script d'init ; tests `DemoCloudConteneurTests`, build et tests d'architecture
      réussis sous Windows) ; **reste : premier démarrage réel** après installation de Docker Desktop + WSL 2 (liste « À vérifier
      au premier démarrage » de [demo-cloud.md](demo-cloud.md)) ; comptes de démo : **choix A du titulaire le 2026-11-20**
      (base de démo construite comme les bases de développement : migrations puis données fictives ; séquence validée sur une
      base jetable, défaut du script 149 sur base neuve corrigé et repassé sur dev et test) ;
    - [ ] étape 2 : mode démo — **code et fichiers écrits le 2026-11-21** (réglage `Demo:Actif` faux par défaut, refus de démarrer
      sans le marqueur `base_demonstration` posé par le seul script d'init, mention « Démonstration — données fictives » au poste
      et sur la page mobile, filigrane des PDF, fonctions coupées en 422, adaptateurs SMS/e-mail/Chifa factices, comptes de démo
      au mot de passe figé, jeton 2 h, gabarit et service `reinit` de remise à zéro quotidienne, nomenclature importée par l'API
      puis 905 ; tests `ModeDemoTests` ; build et tests réussis sous Windows) ; **reste : premier démarrage réel** (Docker absent
      du poste ; liste « À vérifier au premier démarrage » de [demo-cloud.md](demo-cloud.md), étape 2) et validation par le
      titulaire des décisions listées dans [demo-cloud.md](demo-cloud.md) (droit de redistribuer la nomenclature si démo publique) ;
    - [ ] étape 3 : tests (démarrage du conteneur, PDF, sauvegarde et restauration dans le conteneur) ;
    - [ ] étape 4 : documentation d'exploitation de la démo ;
  - majorations Chifa : **« Garde » seule** pour l'instant ;
  - [x] **fait le 2026-11-20 (script 153 et seed de l'init : les six règles semées pour une base neuve ; commité le 2026-11-20 ; captures
    faites le 2026-11-20 : Administration › Psychotropes 0 px à 1536 × 890 et 1366 × 768, clair et sombre ; Caisse avec
    psychotrope 0 px à 1536 × 890 et à 1366 × 768, clair et sombre, bloc « Client et ordonnance » replié de lui-même ; corrigé :
    la bulle de la durée ne vient plus qu'à F12 (elle plaçait le curseur dans le bloc et empêchait le repli), durée listée dans
    « à compléter »)** — alertes psychotropes : règle 1 remplacée (patient servi alors qu'un psychotrope est encore en cours de traitement ; durée
    prescrite obligatoire), ordonnance réutilisée 1 fois sans limite de durée, plus de 2 boîtes ou plus de 28 jours,
    prescripteur inhabituel désactivé au départ (détail : [a-faire.md](a-faire.md), « Écrans de vente ») ; précisions du
    2026-11-20 : règle 1 sous un **nouveau code** `traitement_en_cours` (ancienne règle `patient_repete` gardée, désactivée),
    **valideur exigé** ; règle 2 : période réglable **ou « sans limite »** (valeur de départ : sans limite) ; bases existantes :
    nouvelles valeurs posées **seulement sur les règles jamais modifiées** par le titulaire ;
  - [x] **fait le 2026-11-20 (scripts centraux 026 à 028, gardes de rejeu de 004 à 022, F8 ; passés deux fois sur
    `pharmacie_cloud_dev` après sauvegarde vérifiée ; commité le 2026-11-20 ; zip chiffré fait le 2026-11-20, voir plus haut)** — central, précisions du 2026-11-20 : tables sans archive par une colonne réglable
    `cloud_table_miroir.archive_remplacee` ; alertes déjà archivées **gardées** ; au départ, `ordre_central_journal` **gardé**
    (lien technique vers les ordres retiré) ; effacés aussi : volume journalier, état et sceaux d'intégrité ; gardés : familles
    envoyées et codes d'activation ; archive de départ remise par lien : **zip chiffré** par mot de passe ;
  - [x] **fait le 2026-11-20 (Trésorerie : versement d'organisme seul proposé, libellés mis par le logiciel ; dépense et encaissement
    sans propositions ; transferts et versements de caisse sans libellé à taper ; rayons : 30 au plus ; Seuils par produit : 8 lignes
    à 1 366 × 768, 10 à 1 536 × 890, filtres à gauche, écart noté dans la charte ; 0 px, clair et sombre, captures regardées)** —
    suggestions de saisie (2026-11-20) : **proposer aussi** les libellés de versement d'organisme, transferts et versements de
    caisse ; **tous les rayons** proposés dès l'entrée dans le champ ; écran **Seuils par produit à revoir** (8 à 10 lignes
    visibles à 1366 × 768) ;
  - [x] **fait le 2026-11-20 (26 bases supprimées, aucune connexion ouverte ; `pharmacie_jetable_sugg_7086638f`, reste d'un essai des
    tests de suggestions, supprimée ensuite sur go du titulaire)** — bases : **supprimer les bases jetables seules** (`pharmacie_cloud_jetable_m11_1d7bbebb`, `pharmacie_jetable_m4_*`,
    `pharmacie_jetable_rejeu_*`), liste exacte montrée et validée avant suppression ; bases de recette gardées.
- [x] **S6.5, intégrité après restauration** : **terminé** le 2026-11-19 (S6.5.0 à S6.5.6 ; essai de bout en bout réussi). Restent
      hors lot : avis du comptable (§ 6) ; n° repris avant la détection : traité le 2026-11-19 (§ 31.10). Synthèse :
      [conception-synchronisation.md](conception-synchronisation.md) § 31.9.

## 4. Défauts d'écran

- [x] **Écrans qui défilent à 1 366 × 768** (mesure du 2026-11-20 : 17 écrans de 1 à 195 px) : **corrigé le 2026-11-20, commité
      `d4dbe40`**, remesuré à 0 px à 1 366 × 768 (tous les écrans, toutes rubriques) et à 1 536 × 890, captures clair et sombre.
      Détail des corrections : charte graphique, « Campagne 1 366 × 768 du 2026-11-20 ». Nouvelles rubriques : Caisse et contrôle
      › « Chifa : délais et seuils », Notifications › « E-mail » et « SMS ».
- [x] **Fiche d'un prêt ouvert** (Financements › Prêts bancaires) : défilait de 77 à 226 px à 1 366 × 768. **Corrigé le 2026-11-21
      (« go 3 », commité `e93dc22`)** : cinq tuiles compactes sur une rangée, tableaux à 140 px au moins, onglets « Prêt et document »,
      « Employer l'argent » et saisie de l'échéancier dans un cadre défilant ; rien n'est retiré. Mesurée le 2026-11-21 : 0 px sur les
      quatre onglets, clair et sombre (charte graphique).
- [x] **Vente au changement d'utilisateur, serveur injoignable** : **corrigé le 2026-11-21 (commité `e93dc22`)** : plus aucun panier perdu
      en silence ; fenêtre bloquante « Réessayer » / « Rester sur la vente » (regles-metier.md « Aucun panier perdu en silence »).
      Essai serveur arrêté, mesure (0 px aux deux tailles) et captures clair et sombre faits le 2026-11-21.
- [x] **Facture et retour fournisseur laissés au changement d'utilisateur** : **fait le 2026-11-21 (commité `e93dc22`)**, décision du
      titulaire « fichier du poste » : `%LOCALAPPDATA%\PharmaOfficine\taches-laissees.dat`, chiffré DPAPI utilisateur, relu au
      démarrage, 7 jours au plus (regles-metier.md « Facture et retour fournisseur laissés »). **Reste : essai sur le poste**
      (facture en préparation, « ⇄ Changer », fermer et relancer le logiciel, le suivant reprend la facture).
- [x] Reprise de données et profils de reprise (super admin) : mesurés le 2026-11-21 (API lancée avec `SuperAdmin__Fichier`,
      compte `super.essai`) ; Reprise corrigée (55 px → 0 px à 1 366 × 768), commité `f399e6d`.
- [x] **Bulle « À renseigner » trop tôt** (2026-11-20, corrigé, commité `d4dbe40`, vérifié à l'écran) : porteur de l'ordonnance (curseur dans le nom à la coche),
      n° de facture et « motif sans n° » Chifa, motif du rattachement d'un volet, confirmation du mot de passe (Synchronisation) ;
      règle ajoutée à la charte graphique (« Bulle seulement après une tentative »).
- [x] **Aide de la caisse recouverte par l'écran** (2026-11-20, corrigé, commité `d4dbe40`, vérifié à l'écran) : `app.css`, colonne du menu au-dessus du
      contenu quand l'aide est ouverte. Seul cas : les autres aides sont dans le titre de l'écran.

### Défauts d'écran constatés le 2026-11-18

- [x] Caisse en tiers payant, avec produit et bandeau de lot, à 1366 × 768 : défilait de 164 px → 0 (bloc client replié de lui-même).
- [x] Liste de recherche de la caisse à 1366 × 768 : barre horizontale → liste sur toute la largeur de la ligne.
- [x] Surveillance à 1366 × 768 : menu de gauche 37 px → 0 (tiroir et produits sensibles dans « Pertes et vigilance »).
- [x] Emplacements à 1366 × 768 : « Zone » sous la liste des rayons → les cinq champs sur une ligne.
- [x] Contrôle interne › Pertes et vigilance › Par employé à 1366 × 768 : 33 px et barre horizontale → 0 (corrigé le 2026-11-18).
- [ ] **Données d'essai dans `pharmacie_test`** (2026-11-18, soir) : ventes en attente de 23:00 (1 produit, 2 412,50 DA) et 23:05
      (1 produit, 1 930,00 DA), créées par mes essais ; vente en attente de 22:12 (3 produits, 1 417,50 DA, titulaire),
      créée par mon essai en quittant la Caisse panier rempli ; une autre de 22:13 (2 produits, 782,50 DA, motif « Autre : test »),
      pas de mon fait (poste de Visual Studio encore ouvert sur mon API ?). Abandon sur accord seulement (l'abandon crée une anomalie).
- Rappel de la règle (précision du titulaire, 2026-11-18) : seuls les **écrans principaux** ne défilent jamais ; un formulaire ouvert peut faire défiler la page.

## 5. Décisions attendues du titulaire

- [x] **Rapprochement d'un volet après un retour en tiers payant** (décidé le 2026-11-21 : ajuster ; fait, script 157, commité `e93dc22`) : déduire les boîtes rendues dans la comparaison, ou refuser la régularisation d'une vente déjà rendue en partie (risque de double déduction : [a-faire.md](a-faire.md)).

- [ ] **Compte `postgres`** : fichier `pgpass.conf` (hors dépôt) pour essayer le script 111b et la méthode `SET ROLE` sur une copie.
- [ ] **Écrans de vente** : liste des DCI d'antibiotiques, format de l'export pour les impôts et taux de TVA.
- [ ] Autres points ouverts : voir [a-faire.md](a-faire.md), « Décisions en attente du titulaire » et « Mise en production : à décider ».

## 6. Démarches du titulaire

- [ ] Hébergeur en Algérie agréé ARPCE et nom de domaine `.dz` (préalable au serveur central réel).
- [ ] Déclaration à l'ANPDP ; texte du consentement SMS ; test d'intrusion avant la mise en service du central.
- [ ] Lettre à la CNAS (format du QR Chifa, export, volets ; procédure pour une vente déjà déposée et retour d'un médicament remboursé : questions dans [chifa.md](chifa.md)).
- [ ] Marque Tracodex : recherche d'antériorité à l'INAPI (classes 9, 42 et 5) ; avis sur le croissant du logo (emblème
      national, ordonnance 03-06) avant le dépôt ([branding/logo.md](branding/logo.md)).
- [ ] Wilayas 59 à 69 à vérifier au Journal officiel.
- [ ] Exports de l'ancien logiciel, catalogues des grossistes, tableaux d'amortissement des banques, modèle de feuille CVM/MDN.
- [ ] Questions au comptable : TVA des pertes et dons, don partiel, intérêts et marge des prêts (mourabaha) ; n° de tickets sautés après une restauration (S6.5-D6) ; journal fiscal d'un retour en tiers payant au TTC complet (part organisme négative, script 155).
- [ ] Questions à un juriste (loi 18-07 et loi 25-11, avant la mise en service du central) : autorisation ou déclaration ANPDP, consentement ou information des patients, secret professionnel et hébergement chez l'éditeur, hébergement en Algérie obligatoire ou non, réponse à une autorité sans le titulaire et tiers de confiance (décision DC8) : [chiffrement-data.md](chiffrement-data.md) § 12.3 ; durée de conservation au central après le départ d'une officine (6 ans par défaut) ; obligations de conservation des ordonnances.

## 7. Essais sur le terrain

- [ ] HTTPS : script `certificat_https.ps1` en réel, autorité commune aux serveurs, postes et téléphones.
- [ ] Copie de la GED entre deux vrais serveurs (partage SMB hors domaine, droit du compte de tâche, durée de la première copie), lecture de la fréquence par authentification Windows (SSPI, nom du compte présenté par Windows).
- [ ] Matériel (modem GSM, SMTP, imprimantes, disque de sauvegarde, iPhone) ; douchette dans Achats › Commandes (Entrée sur un code scanné).
- [ ] Chifa (double saisie, suffixe du n° d'assuré), reprise de données réelle, étiquetage du stock.
- [ ] Synchronisation avec un vrai serveur central (restauration, bascule, départ en réel).

## 8. Plus tard (sur accord explicite du titulaire)


- [x] **Synchronisation S7.1** (2026-11-21, en local) : écrans de l'éditeur du serveur central, comptes distants, second facteur, journal des accès chaîné ([ecrans-central.md](ecrans-central.md)). Scripts centraux **030** et **031** passés sur `pharmacie_cloud_dev`, `preparer_central_vs.ps1 -Preparer` fait, 19 pages mesurées à 0 px (1 366 × 768 et 1 536 × 890, clair et sombre).
- [x] **Synchronisation S7.2a** (2026-11-21, en local, côté serveur central) : écrans du titulaire (mes officines, rattachement par code, tableau de bord en totaux seulement, journal des accès de l'officine), console `--creer-compte-titulaire` et `--code-rattachement` ([ecrans-central.md](ecrans-central.md) § 3 bis). Script central **032** passé sur `pharmacie_cloud_dev`, compte `titulaire.dev` créé, quatre écrans mesurés à 0 px (1 366 × 768 et 1 536 × 890, clair et sombre).
- [x] **Synchronisation S7.2b** (2026-11-21, en local) : rubrique « Accès à distance » du poste, le titulaire génère le code de rattachement
  (route centrale `POST /api/v1/rattachement`, script central **033**) ([ecrans-central.md](ecrans-central.md) § 3 ter). Script central **033**
  passé sur `pharmacie_cloud_dev` ; rubrique « Accès à distance » mesurée à 0 px (commit `20c1ecf`).
- [x] **Synchronisation S7.2b (reste)** : voir et retirer depuis le poste les comptes distants rattachés (fait avec S7.3).
- [x] **Synchronisation S7.3** (2026-11-21, en local) : vue d’ensemble du titulaire, droit « ordres » accordé depuis le poste, comptes
  rattachés et retrait, ordres du titulaire (essai, sauvegarde, vérification d’intégrité) ([ecrans-central.md](ecrans-central.md) § 3 quater).
  Script central **034** passé sur `pharmacie_cloud_dev` ; mesurés à 0 px : `/mes-officines/ensemble`, `/mes-officines/{officine}/ordres`
  (sans le droit, puis avec le droit accordé à 1 366 × 768, statut « Appliqué ») et la rubrique « Accès à distance ».
- [x] **Synchronisation S7.4** (2026-11-21, en local) : réglages communs proposés par le titulaire à plusieurs officines, chaque
  officine décide sur son poste ([ecrans-central.md](ecrans-central.md) § 3 quinquies). Script central **035** passé deux fois sur
  `pharmacie_cloud_dev` après sauvegarde, tests SQL 034 et 035 réussis ; `/mes-officines/reglages` et Réglages mutualisés du poste
  mesurés à 0 px ; essai de bout en bout réussi avec `pharmacie_test` ; commité `4a0570f`.
- [x] **Synchronisation S7.5** (2026-11-21, en local) : lignes absentes vues au central, nombres seulement
  ([ecrans-central.md](ecrans-central.md) § 3 sexies). Script central 036 passé, écrans mesurés, commité `f399e6d`.
- [x] **Synchronisation S7.3 (reste)** (2026-11-21, en local) : ordre « message à afficher sur les postes » : pastille « Message » de la
  barre d'état, fenêtre, fermeture par le titulaire local ([ecrans-central.md](ecrans-central.md) § 3 nonies). Scripts central **038** et
  local **160** à passer (après relecture et sauvegarde) ; écrans à mesurer ; essai de bout en bout poste ↔ central à faire.
- [x] **Synchronisation S7.6** (2026-11-21, en local) : envoi des codes de second facteur réglé dans l'administration du site
  (canal, serveur d'e-mail, mot de passe chiffré, contacts des comptes) ; script 037 à passer, écrans à mesurer, essai Gmail réel à faire.
- [x] **Synchronisation S7.2 (reste)** (2026-11-21, en local) : remise à zéro d'un mot de passe par l'éditeur (montré une fois, jamais par
  e-mail), retrait d'un rattachement par le titulaire depuis les écrans (script central 038 à passer, écrans à mesurer).
- [x] **Comptes du central** (2026-11-21/22, `076df06`, `3a79e77`) : création à l'écran (039), import CSV (040), durée de vie et
  régénération des mots de passe provisoires, identifiant par e-mail, déconnexion après inactivité réglable par rôle.
- [ ] **Synchronisation S7.2 (reste)** : SMS (fournisseur), alertes de refus en série.
- [ ] **Mise en ligne des écrans du central** (après le choix de l'hébergeur) : domaine et certificat, proxy inverse (adresse réelle pour les limites de débit), connexion de production au rôle `pharma_cloud_ecrans`, SMTP, clé HMAC commune si plusieurs instances, comptes éditeur réels, test d'intrusion.
- [ ] Découpage par officine des grandes tables du central.
- [ ] Script 101b (suppression de `vente.num_facture_chifa`) : à exécuter à la mise à jour de l'officine, conflits tranchés.
- [ ] Sauvegarde chiffrée de la GED hors de l'officine (après le lot 9 et l'hébergeur).
- [ ] **Lot 9, chiffrement** : uniquement sur « go chiffrement ». Option C (ouverture à deux) conçue, décisions DC1 à DC7 acceptées le 2026-11-17 : [chiffrement-data.md](chiffrement-data.md), lots C1 à C5.
