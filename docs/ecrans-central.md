# Écrans du serveur central (lot S7)

> **Statut (2026-11-21)** : lot **S7.1 réalisé en local** (écrans de l'éditeur), go du titulaire, toutes les recommandations de la
> conception retenues. Lot **S7.2a réalisé en local** (écrans du titulaire, **côté serveur central seulement**, go du titulaire,
> recommandations S7 acceptées) : § 3 bis. **Rien n'est en ligne** : la mise en ligne (hébergeur, certificat, SMTP, connexion de production)
> viendra plus tard. Lot **S7.2b réalisé en local** (rubrique « Accès à distance » du poste : le titulaire tire lui-même le code de
> rattachement, go du titulaire, recommandations acceptées) : § 3 ter. Lot **S7.3 réalisé en local** (multi-officines : vue d'ensemble ; droit
> `ordres` accordé depuis le poste, comptes rattachés et retrait ; ordres à distance du titulaire en liste fermée) : § 3 quater. Lot **S7.4
> réalisé en local** (réglages communs proposés par le titulaire à plusieurs officines, chaque officine décide sur son poste) : § 3 quinquies.
> Lot **S7.5 réalisé en local** (lignes absentes : rubrique de la fiche d'une officine pour l'éditeur, compteur du titulaire, nombres
> seulement) : § 3 sexies. **Reste de S7.2 et S7.3 réalisé en local** (remise à zéro d'un mot de passe par l'éditeur, retrait d'un
> rattachement par le titulaire, message à afficher sur les postes ; hors SMS, aucun fournisseur) : § 3 nonies.
> **Import de comptes, durée du mot de passe provisoire, identifiant par e-mail et déconnexion après inactivité réalisés en local**
> (script central 040 ; un utilisateur actif n'est jamais déconnecté) : § 3 decies.

Documents liés : [conception-synchronisation.md](conception-synchronisation.md) § 1, § 6, § 6.1, § 6.2 et § 32 ;
[securite-et-droits.md](securite-et-droits.md) ; [charte-graphique.md](charte-graphique.md) ; [database/cloud/README.md](../database/cloud/README.md) ;
[poste-de-developpement.md](poste-de-developpement.md).

## 1. Ce qui existe

| Élément | Où | Rôle |
|---|---|---|
| Projet `src/Pharma.Cloud.Web` | Blazor en **rendu serveur statique** (formulaires classiques, aucun script Blazor, aucune interactivité) | Écrans ; **processus et port distincts** du serveur central (5090) |
| Script central `030_comptes_distants.sql` | base centrale | Rôle `pharma_cloud_ecrans`, comptes, sessions, codes de second facteur, journal des accès |
| Script central `031_ecrans_editeur.sql` | base centrale | Lecture de l'éditeur et actions |
| Script central `032_ecrans_titulaire.sql` (S7.2a) | base centrale | Droits par officine, codes de rattachement, lecture du titulaire (agrégats), journal des accès de ses officines |
| Script central `033_code_rattachement_poste.sql` (S7.2b) | base centrale | Code de rattachement demandé par le poste (`cloud_poste_code_rattachement`, rôle `pharma_cloud_api`), enregistrement commun avec la console, acteur « poste » au journal de l'officine |
| Script central `034_titulaire_multi_officines_ordres.sql` (S7.3) | base centrale | Droit `ordres`, vue d'ensemble, ordres du titulaire (liste fermée, 24 h), comptes rattachés vus, changés et retirés par le poste |
| Script central `035_reglages_communs_titulaire.sql` (S7.4) | base centrale | Liste fermée et bornes des réglages communs, type d'ordre `reglages`, envoi à plusieurs officines, suivi des envois |
| Script central `036_lignes_absentes_ecrans.sql` (S7.5) | base centrale | Lignes absentes par table, chargement et cause probable (nombres seulement), index partiels ; lecture de l'éditeur et compteur du titulaire |
| Script central `037_envoi_codes_reglage.sql` (S7.6) | base centrale | Réglage unique de l'envoi des codes (canal, SMTP, mot de passe **chiffré**), e-mail ET téléphone d'un compte, canal `les_deux` ; fonctions `cloud_ecran_facteur_*` |
| Écrans « Envoi des codes » et « Comptes » (S7.6) | écrans du central, éditeur | Canal et serveur d'e-mail des codes, e-mail d'essai ; contact (e-mail, téléphone) de chaque compte |
| Script central `038_remise_mot_de_passe_retrait_message.sql` (reste S7.2 / S7.3) | base centrale | Remise à zéro du mot de passe d'un compte par l'éditeur, retrait d'un rattachement par le titulaire, ordre « message » (contrôle `cloud_message_poste_controle`) |
| Script local `160_message_central_postes.sql` (reste S7.3) | base de l'officine | Fermeture du message par le titulaire de l'officine (`sync_ordre_recu.message_ferme_le`, `sync_message_central_fermer`) |
| Pastille « Message » de la barre d'état (reste S7.3) | poste, tous les employés connectés | Message du titulaire à distance, petite fenêtre ; « Ne plus afficher » pour le titulaire de l'officine |
| Routes `GET /api/v1/acces-distants`, `POST …/droits`, `POST …/retrait` (S7.3) | API centrale (mTLS) | Comptes rattachés de l'officine du certificat, droits, retrait |
| Route `POST /api/v1/rattachement` (S7.2b) | API centrale (`Pharma.Cloud.Api`, mTLS) | Code de rattachement de l'officine du certificat, montré une fois |
| Rubrique « Accès à distance » (S7.2b) | poste, Administration › Synchronisation | Le titulaire génère le code (mot de passe ressaisi) |
| Commandes de la console du central | `Pharma.Cloud.Api.exe` | `--creer-compte-editeur`, `--creer-compte-titulaire` (S7.2a), `--code-rattachement` (S7.2a), `--comptes-distants`, `--desactiver-compte`, `--deverrouiller-compte`, `--envoi-codes` (compte e-mail des codes réglé sur le serveur, 2026-11-21), `--contact-compte` (e-mail / téléphone d'un compte changé sur le serveur, 2026-11-21) |
| Tests | `tests/Pharma.Cloud.Web.Tests`, `tests/Pharma.Cloud.Tests/ComptesDistantsConsoleTests.cs`, `…/EnvoiCodesReglageTests.cs`, `database/cloud/tests/030_…` à `037_…` | Voir § 7 |

## 2. Rôles

| Rôle | Écrans | Données vues |
|---|---|---|
| **Éditeur** (support, S7.1) | Officines, fiche d'une officine, mon journal des accès, actions techniques | **Jamais de donnée nominative** (décision n° 4) : état technique seulement |
| **Titulaire** (S7.2a) | Mes officines, rattacher une officine, tableau de bord d'une officine, journal des accès d'une officine, mon journal des accès | Ses officines seulement (`compte_officine`, droits `consulter` et `journal_acces`) ; **agrégats** seulement, aucune donnée nominative ni ligne de détail |

Les rôles sont **étanches** : un titulaire n'ouvre aucun écran ni aucune action de l'éditeur (« Accès refusé » ; `/` et `/officines` le
renvoient à « Mes officines ») et l'éditeur n'ouvre aucun écran du titulaire (tableau de bord, journal d'une officine, rattachement).
La base le revérifie dans chaque fonction (`42501` : rôle `editeur` exigé par `031`, rôle `titulaire` par `032`). Écrans communs : mot de
passe, mon journal des accès, déconnexion, thème.

## 3. Écrans S7.1 (éditeur)

| Route | Écran | Contenu |
|---|---|---|
| `/connexion` | Connexion | Identifiant + mot de passe ; **même message** pour un identifiant inconnu, un mot de passe faux ou un compte bloqué |
| `/code` | Code de confirmation | 6 chiffres, 5 minutes, 5 essais ; « Recevoir un nouveau code » ; « Abandonner » |
| `/mot-de-passe` | Mot de passe | Obligatoire tant que le mot de passe est **provisoire** ; ensuite accessible depuis l'en-tête (les autres sessions sont fermées) |
| `/officines` (et `/`) | Officines | Filtres au-dessus (nom, état : liste bornée), tableau paginé (20) : officine, état, créée le, dernier contact, en retard (plus de 24 h sans contact, officine active), dernier lot, époque, chargement en cours, échéance de certificat la plus proche, anomalies |
| `/officines/{code}?rubrique=…` | Fiche d'une officine | Rubriques à gauche : `resume`, `certificats`, `codes` (sans le code), `chargements` (20), `absentes` (lignes absentes, S7.5 : § 3 sexies), `lots` (50 derniers, sans contenu), `epoques`, `changements`, `volumes` (31 jours), `integrite` (chaînes), `anomalies` (nature et rang, sans détail, paginé), `restaurations` (sans montant), `ordres` (type et statut, sans données, paginé), `journal` (journal de sécurité, paginé) |
| `/officines/nouvelle` | Créer une officine | Action (§ 4) ; serveur principal ou second |
| `/officines/{code}/nouveau-code?serveur=principal\|second` | Nouveau code d'activation | Action (§ 4) |
| `/officines/{code}/revoquer-certificat?empreinte=<12 caractères>` | Révoquer un certificat | Action (§ 4), confirmation par le début d'empreinte |
| `/officines/{code}/revoquer-serveur?serveur=principal\|second` | Révoquer un serveur | Action (§ 4), confirmation par le nom de l'officine |
| `/officines/{code}/code-affiche?ref=…` | Code d'activation | Affiché **une seule fois** (mémoire du processus, 2 minutes, même session), bouton « Copier » |
| `/mon-journal` | Mon journal des accès | Accès du compte, paginé (20) |

**Restent en console** (non exposés aux écrans) : départ et effacement d'une officine, purges, familles nominatives, autorité de certification, réglages mutualisés, ordres.

## 3 bis. Écrans S7.2a (titulaire)

| Route | Écran | Contenu |
|---|---|---|
| `/mes-officines` (et `/`, `/officines`) | Mes officines | Officines rattachées au compte (200 au plus, une ligne par officine) : officine, état, rattachée le, droits, dernier contact, en retard ; liens « Tableau de bord » et « Journal des accès » (si le droit `journal_acces` est accordé) |
| `/rattacher` | Rattacher une officine | Saisie du code de rattachement (12 caractères, tirets et espaces ignorés, casse ignorée) ; succès → tableau de bord de l'officine |
| `/mes-officines/{officine}` | Tableau de bord | 14 tuiles (lignes absentes depuis S7.5) (`.tuiles.tuiles-compactes`, une seule grille, deux rangées à 1 366 px) : voir le tableau ci-dessous ; officine d'un autre compte, inconnue ou rattachement retiré : « Officine introuvable ou non rattachée » (refus noté au journal des accès) |
| `/mes-officines/{officine}/journal` | Journal des accès de l'officine | Tous les accès notés pour l'officine (vous, l'éditeur, un autre titulaire, la console), paginé (20) : le, par, événement, résultat, objet ; **jamais** l'identifiant ni le nom d'un compte |
| `/mon-journal` | Mon journal des accès | Comme pour l'éditeur (S7.1) |

**Tableau de bord** (`cloud_ecran_titulaire_tableau`, une ligne d'agrégats ; mêmes définitions que le tableau de bord local, `PilotageSql`) :

| Tuile | Calcul (tables miroir de l'officine, lignes `sync_absente_le` exclues) |
|---|---|
| Chiffre d'affaires du jour / du mois / mois précédent (entier) | `vente.montant_total` des ventes **validées** de la période, moins `retour_client.montant_rembourse` des retours de la période sur une vente validée |
| Ventes du jour (et du mois, en note) | nombre de ventes validées |
| Part Chifa du mois | `vente.part_organisme` des ventes validées du mois avec `canal_organisme = 'chifa'` |
| Crédits en cours | somme des `patient.solde_credit` positifs (comme le pilotage financier) ; aucun patient désigné |
| Alertes de stock / de péremption | nombre d'`alerte` au statut `nouvelle` ou `vue`, types `rupture` et `stock_critique` / `peremption` et `perime` |
| Dernier contact, en retard | dernier lot reçu ou dernier certificat émis ; en retard : officine active sans contact depuis 24 h |
| Dernier lot reçu, chargement complet | `officine_epoque` (époque la plus récente) ; dernier chargement complet terminé, chargement en cours |
| Anomalies d'intégrité | `cloud_anomalie_integrite` non classées « remplacée après restauration » |
| Ordres en attente | `ordre_central` aux statuts `en_attente` et `a_confirmer` |
| Lignes absentes (S7.5) | lignes marquées `sync_absente_le` dans les tables miroir (nombre et tables ; `cloud_ecran_titulaire_lignes_absentes`) ; « non disponible » sans chargement complet terminé |

- **Jour et mois** au fuseau de l'officine : `parametre_fiscal.fuseau` s'il est monté et connu du serveur PostgreSQL, sinon
  `Africa/Algiers` (réglage de la base locale, migration 083).
- **« Non disponible »** (NULL de la base, jamais 0) : table ou colonne absente du catalogue du miroir ou de la base, colonne d'une famille
  nominative non envoyée par l'officine, ou **aucun chargement complet terminé comprenant la table** (sans lui, une somme ne porterait que
  sur les lignes modifiées depuis l'activation). Sans la table `retour_client`, le chiffre d'affaires reste non disponible (il serait faux) ;
  le nombre de ventes est donné.
- Les totaux sont ceux des données **reçues** : une vente faite depuis le dernier contact n'y figure pas encore (rappelé au pied de l'écran).

**Rattachement** (`cloud_ecran_rattacher`) : le code (12 caractères tirés parmi 32 sans ambiguïté, 60 bits ; affiché une fois) ne vaut
que pour **un** compte titulaire et **une** officine, **30 minutes**, **usage unique** ; un nouveau code pour le même compte et la même
officine annule le précédent. La base ne garde que l'empreinte SHA-256 (`rattachement-v1:` + code canonique). Refusés : code inconnu ou
destiné à un autre compte (**même réponse**, le motif réel `autre_compte` reste au journal), expiré, déjà utilisé ou annulé, officine
partie ; au-delà de **5 refus par quart d'heure**, plus aucun essai (« Trop d'essais »). Réussite : `compte_officine` (droits `consulter`,
`journal_acces`, « code de rattachement n° … »), note au journal des accès **et** au journal de sécurité de l'officine. En attendant
S7.2b, le code n'était tiré que par la console du central ; depuis S7.2b, le titulaire le tire du poste (§ 3 ter), la console reste pour
le développement.

## 3 ter. Lot S7.2b : accès à distance depuis le poste (réalisé en local le 2026-11-21)

Le titulaire tire lui-même le code de rattachement de **son** officine dans le poste, puis le saisit dans `/rattacher` (§ 3 bis).

| Maillon | Où | Règle |
|---|---|---|
| Écran | poste, Administration › Synchronisation › **Accès à distance** (`admin/synchronisation?rubrique=acces-distant`) | Identifiant du compte distant, mot de passe du titulaire ressaisi, « Générer le code » → code en grand (`XXXX-XXXX-XXXX`, classe `.code-acces-distant`), échéance, consigne « à saisir dans https://‹adresse des écrans du serveur central›/rattacher dans les 30 minutes ». Code et mot de passe effacés au changement de rubrique ou par « J'ai noté le code » ; jamais gardés (ni base, ni navigateur, ni journal). Synchronisation non activée (ou 2e serveur en veille) : texte d'explication, **aucun bouton** ; employé non titulaire : texte seulement |
| API locale | `POST /api/admin/synchronisation/acces-distant/code` (droit `admin.parametres`) → `GenererCodeAccesDistant` | **Titulaire seulement** (403 sinon), puis synchronisation activée (activée, adresse réglée, serveur activé, certificat non échu ; sinon refus clair), identifiant bien formé, puis **mot de passe du titulaire** (même vérification que la nouvelle époque : essais comptés, blocage, refus journalisés), puis appel au central. Journal des actions `code_acces_distant` (échéance seulement : ni le code ni l'identifiant). Réponse `Cache-Control: no-store`. Coupée en démonstration |
| API centrale | `POST /api/v1/rattachement` (certificat client + jeton, comme `/echange` ; limite de débit « officine ») | Corps : `{ "compte": "…" }` seulement ; **l'officine est celle du certificat**, jamais un champ. Même tirage que la console (`ComptesDistants.TirerCodeRattachement`) ; seule l'empreinte va en base. Réponses : 200 `{ code, expire }` (une seule fois, `no-store`) ; **422 neutre** pour un compte inconnu, non titulaire, désactivé ou un identifiant mal formé (même texte) ; 429 au-delà de **5 demandes par heure et par officine** (délivrées ou refusées : pas d'oracle) |
| Base centrale | `cloud_poste_code_rattachement` (033, SECURITY DEFINER, seul droit donné à `pharma_cloud_api`) | Revérifie que le certificat présenté est valable, non révoqué, **de l'officine posée** (`app.officine_id`), officine non partie (sinon 42501). Enregistrement commun avec la console (`cloud_code_rattachement_enregistrer`) : un compte, une officine, 30 min, usage unique, code précédent du même compte et de la même officine annulé. Journal des accès : objet `poste` (refus sans compte), `poste:limite` ; journal de sécurité : délivrance, ou refus avec le motif réel (sans l'identifiant saisi). Écran du titulaire : acteur « Poste de l'officine » |

Restent (non demandés dans ce lot) : adresse réelle des écrans du central (mise en ligne). Comptes rattachés vus et retirés depuis le poste : lot S7.3 (§ 3 quater).

## 3 quater. Lot S7.3 : multi-officines et ordres du titulaire (réalisé en local le 2026-11-21)

Go du titulaire, recommandations S7 acceptées. Script central **034** ; aucun script local (aucune table synchronisée ne change :
`002_tables_miroir.sql` inchangé).

| Route / maillon | Où | Règle |
|---|---|---|
| `/mes-officines/ensemble` (« Vue d'ensemble ») | écrans du central | Une ligne par officine du compte (droit `consulter`, 50 au plus, paginé par 20) : dernier contact, chiffre d'affaires du jour, du mois et du mois précédent, ventes, part Chifa, crédits en cours, alertes, anomalies, ordres en attente (mêmes calculs que le tableau de bord, `cloud_ecran_titulaire_tableau`), puis **Total du groupe** (pied du tableau). Une valeur « non disponible » n'est **jamais** comptée comme 0 : le total devient « … (partiel : n sur m officines) », ou « non disponible » si aucune officine n'a la valeur. Chaque officine lue est notée à son journal des accès |
| `/mes-officines/{officine}/ordres` | écrans du central | Droit `ordres` exigé (sinon « Ordre refusé : … droit « ordres » non accordé depuis le poste »). Nouvel ordre : liste fermée (**Essai de liaison**, **Demander une sauvegarde**, **Demander une vérification d'intégrité**), « Recevoir un code de confirmation » puis code (second facteur à usage unique), « Envoyer l'ordre ». Tableau paginé (10) : type, créé le, expire le, statut (en attente, à confirmer sur le poste, appliqué, refusé, expiré, annulé), statut le, par (vous / autre titulaire) ; **jamais** les données, le motif ni l'auteur |
| `POST /actions/mes-officines/{officine}/ordre` | écrans du central | `cloud_ecran_titulaire_creer_ordre` : contrôles **avant** le code (10 ordres par heure et par compte, liste fermée, rattachement et droit `ordres`, officine partie, un seul ordre ouvert du même type), puis code. Ordre `origine = titulaire`, sans donnée, **24 heures**, auteur « titulaire à distance (identifiant) » ; journal des ordres, journal des accès (chaîné), journal de sécurité ; publié par la voie descendante existante (`ordre-v1`) |
| Rubrique « Accès à distance » › **Comptes rattachés** | poste, Administration › Synchronisation | Titulaire seulement, synchronisation activée. Tableau paginé (5) : compte, nom, droits, rattaché le ; « Droits » (cases tableau de bord, journal des accès, **ordres à distance**) ou « Retirer », mot de passe du titulaire ressaisi. Le droit `ordres` ne s'accorde **que d'ici** |
| `GET /api/admin/synchronisation/acces-distant/comptes`, `POST …/droits`, `POST …/retrait` | API locale (`ComptesAccesDistant`) | Titulaire (403 sinon), synchronisation activée, puis mot de passe (changements), puis central. Journal des actions `droits_acces_distant`, `retrait_acces_distant` (compte, droits, ordres annulés). Coupées en démonstration (POST) |
| `GET /api/v1/acces-distants`, `POST /api/v1/acces-distants/droits`, `POST /api/v1/acces-distants/retrait` | API centrale (`AccesDistantsPoste`, certificat client + jeton) | Officine = celle du certificat (revérifiée par la base, `cloud_poste_officine`). Retrait : **effet immédiat** (chaque fonction des écrans revérifie le rattachement), ordres encore ouverts du compte **annulés** et republiés ; retrait du droit `ordres` : idem. 422 « compte non rattaché » ou « droits invalides » |
| Application à l'officine | API locale (`ExecuterOrdreCentral`) | `essai` : rien ne change ; `verification_integrite` : appliquée tout de suite (lecture seule, comme la vérification de la nuit), résultat remonté en nombres (« n chaîne(s) vérifiée(s) : … intacte(s), … rupture(s), … en erreur ») ; `sauvegarde` : **à confirmer** par le titulaire local (Ordres du central), lancée à sa confirmation comme « Sauvegarder maintenant » ; disque absent ou échec : l'ordre reste à confirmer. Ordre échu (horloge de l'officine aussi) : expiré, jamais appliqué |

**Liste fermée** : la conception ne définissait pas d'ordres du titulaire (la décision 3 du § 0 vise les ordres du propriétaire du
central, S5) : la liste la plus prudente est retenue ; le « message à afficher sur les postes » s'y ajoute depuis le script 038 (§ 3 nonies). Aucun ordre ne modifie
une vente, un stock, un prix ni un compte ; la base le revérifie (`cloud_ordre_titulaire_permis`, contrainte `ck_ordre_central_titulaire`).

**Signature** : l'ordre suit le mécanisme existant (voie descendante sur HTTPS avec certificat client de l'officine, `ordre-v1`) ; il n'est
pas signé à part (comme les ordres S5).

## 3 quinquies. Lot S7.4 : réglages communs du titulaire (réalisé en local le 2026-11-21)

Go du titulaire, recommandations S7 acceptées. Script central **035** ; **aucun script local** (aucune table synchronisée ne change :
`002_tables_miroir.sql` inchangé) : la proposition voyage comme un ordre du titulaire (`ordre-v1`, S7.3) et son résultat remonte par
`sync_ordre_resultat`.

| Route / maillon | Où | Règle |
|---|---|---|
| `/mes-officines/reglages` (« Réglages communs », bouton de « Mes officines » ; maquette validée du 2026-11-21) | écrans du central | Boutons « Vue d'ensemble » et « Mes officines ». À gauche, panneau « Nouvelle proposition » : **un réglage** choisi dans la liste fermée (bornes dans chaque choix) et sa valeur, officines en cases (seulement celles avec le droit `ordres`, cochées d'office ; les autres grisées avec la raison), code de confirmation et « Recevoir un code », « Proposer aux officines cochées (n possibles) », « Valable 24 h ». À droite, « Propositions envoyées » paginées (5) : Réglage, Valeur, Officine, Statut (**Proposé / Accepté / Refusé / Expiré / Annulé**), Le ; jamais le motif ni l'auteur de la décision locale. Pied : « Aucune donnée de patient ni de vente ». Écran fixe : chaque colonne défile seule. Après un refus, la saisie revient (nombres et identifiants seulement dans l'adresse). La base accepte aussi un jeu de plusieurs réglages (même liste fermée) |
| `POST /actions/mes-officines/reglages` | écrans du central | Contrôle de l'écran (liste fermée, entiers, bornes, rouge < orange), puis `cloud_ecran_titulaire_envoyer_reglages` : **tout ou rien** (droit `ordres` sur **chaque** officine cochée, officine présente, aucune proposition encore ouverte), 5 envois par heure, 50 officines au plus, contrôles **avant** le code ; un ordre `reglages` par officine (même `envoi_id`), **24 heures**, auteur « titulaire à distance (identifiant) » ; journaux des accès (chaîné), de sécurité, des ordres |
| Réception à l'officine | API locale (`AppliquerOrdresCentraux`, `ReglagesCommunsOrdre`) | Liste fermée, format, entiers et bornes **revérifiés** : sinon **refusé** tout de suite (jamais à confirmer) ; sinon « à confirmer », motif = avant → après. **Rien ne s'applique** sans l'acceptation |
| Décision au poste | Administration › Synchronisation › **Réglages mutualisés** (maquette validée) ; aussi dans Ordres du central | Bandeau « n proposition(s) en attente de votre décision (valable jusqu'au …) » ; un panneau par proposition : Réglage / Actuel (barré) / Proposé (couleur d'accent) ; titulaire seulement, **mot de passe du titulaire ressaisi** pour accepter comme pour refuser (motif obligatoire au refus, règle existante des ordres) ; un autre employé voit sans décider. Panneau « Décisions précédentes » paginé : Le, Réglage, Valeur, Décision, Par. Le tout dans un cadre défilant. Accepter : `EnregistrerSeuils` (mêmes règles que l'écran Seuils : rouge plus court que l'orange une fois combiné ; table « fixée par le serveur central » refusée ; alertes recalculées ; journal `seuils_alerte` avant / après au nom du titulaire) ; règle non respectée : **refus définitif** avec son motif. Seuils par grossiste et exceptions par produit jamais touchés |

**Liste fermée** (la conception ne définissait pas S7.4 ; liste la plus prudente) : seuils du réglage général `parametre_alerte` :
alerte orange et rouge de péremption (1 à 1 095 j), risque d'écoulement (1 à 1 095 j), délai de livraison (0 à 90 j), stock de sécurité
(0 à 180 j), stock visé (1 à 365 j), produit dormant (1 à 1 095 j), ancien lot délaissé (1 à 1 000 boîtes, deux seuils). **Jamais** prix,
TVA, droits, comptes, rien de fiscal ni de nominatif ; revérifiée par la contrainte `ck_ordre_central_titulaire` (central) et par le poste.

## 3 sexies. Lot S7.5 : lignes absentes (réalisé en local le 2026-11-21)

Go du titulaire, maquette validée (section 3 des maquettes du central). Script central **036** (lecture seulement : fonctions et
index) ; **aucun script local**, `002_tables_miroir.sql` inchangé. Une **ligne absente** est une ligne reçue par le central que
l'officine n'a plus à la fin d'un chargement complet (`sync_absente_le`, posée par `cloud_fin_chargement`, conception § 13.3) : elle
reste au central, **jamais effacée** (décision n° 8) ; reçue de nouveau, elle perd sa marque et n'est plus comptée.

| Route / maillon | Où | Règle |
|---|---|---|
| `/officines/{officine}?rubrique=absentes` (« Lignes absentes », éditeur) | fiche d'une officine | Quatre tuiles (`.tuiles-absentes`) : lignes absentes au total, tables concernées, dernier chargement complet (n° et date), cause probable la plus fréquente (avec son nombre de lignes et, s'il est connu, « base revenue en arrière le … »). Puis tableau paginé (20) : Table, Lignes absentes, Constatées au chargement (n°), Première constatation, Cause probable ; une ligne par table, chargement et cause (plus nombreuses d'abord). Pied : « Les lignes absentes restent au central (jamais effacées) ; aucune ligne n'est montrée. » Sans chargement complet terminé : « aucune ligne ne peut encore être constatée absente » |
| Tuile « Lignes absentes » (titulaire) | `/mes-officines/{officine}` (14e tuile, même grille) | Nombre de lignes absentes, note « n table(s), gardées au central » ; « non disponible » sans chargement complet terminé |
| Colonne « Absentes » (titulaire) | `/mes-officines/ensemble` | Une valeur par officine, total du groupe (partiel si une officine n'a pas la valeur, jamais 0) |
| Base centrale | `cloud_ecran_lignes_absentes` (paginé), `cloud_ecran_lignes_absentes_resume` (éditeur, après `cloud_ecran_ouvrir`) ; `cloud_ecran_titulaire_lignes_absentes` (titulaire, droit `consulter`, une officine ou toutes celles du compte) | **Nombres seulement** : seules `officine_id`, `sync_epoque`, `sync_absente_le` et `count(*)` sont lues ; jamais une colonne métier ni une clé de ligne. Regroupement interne `cloud_lignes_absentes_groupes` jamais accordé |

**Chargement qui a constaté** : celui dont la fin (`chargement.fin_le`) est la date posée sur la ligne (même transaction) ; une ligne
dont la date ne correspond à aucun chargement terminé est montrée sans n° (« — »), cause « Non expliquée ».

**Cause probable** (jamais inventée : seulement ce que le central sait déjà) :

| Cause | Condition |
|---|---|
| Restauration d'une sauvegarde | ligne d'une époque **antérieure** à celle du chargement qui l'a constatée **et** restauration constatée par la vérification d'intégrité pour cette époque (`cloud_integrite_bifurcation`) ; date du retour en arrière : changement d'époque déclaré (détecté, sinon décidé) |
| Nouvelle époque (retour en arrière ou bascule) | ligne d'une époque antérieure, sans restauration constatée (S6-D1 : nouvelle époque après restauration ou bascule) |
| Non expliquée | tout le reste (même époque, chargement non retrouvé) |

**Écarts avec la maquette** : (1) « Purge locale » **n'est pas** une cause rendue : le central ne connaît aucune purge des données de
l'officine (la purge S3 ne vide que la file locale `sync_sortie`) ; (2) la phrase « Une cause « non expliquée » ouvre une anomalie »
**n'est pas** reprise : la conception (décision n° 8) dit « signaler et garder », sans anomalie ; aucune anomalie n'est créée ;
(3) une table peut occuper plusieurs lignes du tableau (une par chargement et par cause), comme l'annonce le texte de la maquette
(« par table et par chargement »).

**Écart avec S7.1** : l'en-tête du script 031 disait « aucune table miroir » pour l'éditeur ; le 036 lit les tables miroir pour **compter**
(colonnes techniques `sync_*` seulement, aucune valeur rendue) : décision n° 4 respectée (aucune donnée nominative), testée.

**Index** : `ix_abs_…` partiel par table miroir (lignes absentes seulement) ; une table miroir ajoutée plus tard (002 régénéré) n'a
l'index qu'après un nouveau passage du 036 (le compte reste juste, plus lent).

## 3 septies. Tuiles navigables (réalisé en local le 2026-11-21)

Go du titulaire : **toutes les tuiles du central sont des liens** (`<a class="tuile tuile-lien">`, composant `TuileTableau`,
paramètre `Lien`) vers l'écran qui détaille leur chiffre ; à défaut (le titulaire n'a droit qu'aux totaux), vers l'écran permis le
plus proche. L'infobulle (`title`) dit où la tuile mène. Même apparence ; bordure verte au survol, contour de focus au clavier
(clair et sombre). Règle pure `LiensTuiles` (Donnees), testée contre `ControleAcces` : **jamais un écran de l'autre rôle** ; un écran
soumis à un droit (ordres) n'est visé que si le droit est accordé.

| Écran | Tuile | Destination | Rôle |
|---|---|---|---|
| `/mes-officines/{officine}` | Chiffre d'affaires du jour, Ventes du jour, Chiffre d'affaires du mois, Mois précédent, Part Chifa du mois, Crédits en cours | `/mes-officines/ensemble` (même total officine par officine, total du groupe) | titulaire |
| `/mes-officines/{officine}` | Alertes de stock, Alertes de péremption | `/mes-officines/ensemble` (colonnes Stock, Péremption) | titulaire |
| `/mes-officines/{officine}` | Dernier contact, Dernier lot reçu, Chargement complet | `/mes-officines` (état, dernier contact et retard de chaque officine ; aucun écran du titulaire ne montre les lots ni les chargements) | titulaire |
| `/mes-officines/{officine}` | Anomalies d'intégrité | `/mes-officines/ensemble` (colonne Anomalies) | titulaire |
| `/mes-officines/{officine}` | Ordres en attente | `/mes-officines/{officine}/ordres` avec le droit « ordres » ; sinon `/mes-officines/ensemble` (colonne Ordres) | titulaire |
| `/mes-officines/{officine}` | Lignes absentes | `/mes-officines/ensemble` (colonne Absentes ; la rubrique détaillée est réservée à l'éditeur) | titulaire |
| `/officines/{officine}?rubrique=absentes` | Lignes absentes au total, Tables concernées | même rubrique, même page, ancre `#tableau-absentes` (tableau par table) | éditeur |
| `/officines/{officine}?rubrique=absentes` | Dernier chargement complet | `?rubrique=chargements` | éditeur |
| `/officines/{officine}?rubrique=absentes` | Cause probable la plus fréquente | Restauration → `?rubrique=restaurations` ; Nouvelle époque → `?rubrique=changements` (changements d'époque déclarés) ; Non expliquée ou aucune → ancre `#tableau-absentes` | éditeur |

Le journal des accès de l'officine n'est visé par aucune tuile : il dit qui a ouvert l'officine à distance, pas d'où viennent les
chiffres. Pour connaître le droit « ordres », le tableau de bord lit aussi `cloud_ecran_titulaire_officines` (une ligne
« liste_officines_titulaire » de plus au journal des accès du compte à chaque ouverture du tableau de bord, comme l'écran des ordres).

## 3 octies. Lot S7.6 : envoi des codes réglé dans l'administration du site (réalisé en local le 2026-11-21)

Go du titulaire (« go codes gmail ») : le canal et le serveur d'e-mail des codes de second facteur se règlent **dans les écrans du
central** (éditeur), plus dans des variables Windows. Script central **037** (`parametre_envoi_codes`, une seule ligne).

| Écran | Accès | Contenu |
|---|---|---|
| `/envoi-codes` « Envoi des codes » | éditeur seulement (titulaire : « accès refusé », la base revérifie) | **Canal** (liste bornée : E-mail, SMS, E-mail et SMS ; « SMS » seul est grisé et refusé tant qu'aucun fournisseur SMS n'existe, et « E-mail et SMS » envoie alors l'e-mail seul : un canal indisponible ne bloque jamais les connexions), **serveur SMTP**, **port**, **sécurité** (STARTTLS ou TLS direct ; jamais sans chiffrement), **identifiant**, **expéditeur**, **mot de passe** (champ toujours vide : vide = **inchangé**, jamais réaffiché ; « Renseigné » ou « Non renseigné » seulement), **code de confirmation** exigé pour enregistrer ; « Envoyer un e-mail d'essai » à une adresse saisie, avec les valeurs du formulaire (mot de passe saisi, sinon celui déjà enregistré, et seulement si le serveur, le port, la sécurité et l'identifiant n'ont pas changé : sinon le mot de passe est à ressaisir ; rien n'est enregistré, aucun code exigé ; résultat « envoyé » ou « pas parti », sans détail technique). À droite : réglage en service, « Recevoir un code de confirmation », aide Gmail. |
| `/comptes` « Contacts des comptes » | éditeur seulement | Tableau paginé des comptes distants (identifiant, rôle, actif, e-mail et téléphone **masqués** `a***@domaine`, `***12`, dernière connexion) ; « Modifier » : nouvel e-mail, nouveau téléphone (vide = inchangé, « - » = retirer ; **au moins un** reste), code de confirmation. |

**Envoi d'un code** (connexion, action sensible) : le réglage est **relu en base à chaque envoi** (gardé 10 secondes en mémoire, oublié
à chaque enregistrement) : `courriel` → e-mail à l'adresse du compte ; `sms` → SMS, **aucun fournisseur** : refus clair (« Envoi des codes
par SMS non disponible… ») noté au journal des accès (`code_envoi`, `sms:indisponible`) ; `les_deux` → e-mail **et** SMS, le SMS
indisponible n'empêche pas l'e-mail (refus seulement si aucun canal n'est parti). Résultat de chaque canal au journal des accès
(`fichier|email|sms:ok|echec|indisponible|sans_contact`), jamais le code, l'adresse ni le numéro.

**Amorçage (2026-11-21) : `--envoi-codes` sur le serveur.** Avant la première connexion, aucun code ne peut partir (le réglage exige
lui-même un code). L'installateur règle donc le compte e-mail **sur le serveur**, comme on règle le poste devant le PC de l'officine :
`Pharma.Cloud.Api.exe --envoi-codes --serveur smtp.gmail.com --port 587 --tls starttls --identifiant <adresse> [--expediteur <adresse>]
--operateur <nom> [--essai <adresse>] [--dossier-cles <chemin>]` (connexion du propriétaire `PHARMA_CLOUD_ADMIN_CONNEXION`, clés du
dossier du central ; `--dossier-cles` si les écrans utilisent `Ecrans:DossierCles`, voir « Mot de passe SMTP » ci-dessous). Le mot de
passe d'application est **demandé au clavier, sans écho** ; `--essai` envoie d'abord un e-mail d'essai (sans code) et **n'enregistre rien**
s'il échoue (« identifiant ou mot de passe refusé » reconnu, y compris la réponse 535 de Gmail). Canal « E-mail » ; mot de passe chiffré
avec les mêmes clés que les écrans ; journal de sécurité « console (opérateur …) », serveur, port et sécurité seulement. Les écrans
l'utilisent au plus tard 10 secondes après. Le premier code de l'éditeur part alors par e-mail : plus besoin des variables
`PHARMA_CLOUD_SMTP_*` (gardées comme secours) ni du fichier. Compte créé avec son adresse (`--creer-compte-editeur … --email …`) ; adresse à
changer sans pouvoir recevoir de code (perdue, erronée) : `--contact-compte <identifiant> --email <adresse> --auteur <nom>` sur le
serveur (« - » retire, au moins un contact reste ; journaux sans l'adresse).

**Mode fichier** (développement) : `Ecrans:ModeCodes=fichier` (appsettings.Development.json) : chaque code est écrit dans le fichier **et** envoyé par l'e-mail du réglage en base s'il est complet (un e-mail en échec ne bloque pas, le fichier suffit) ; réglage incomplet : fichier seul. Hors développement, le mode fichier reste prioritaire (la base n'est pas lue).
`Ecrans:ModeCodes=reglage` (défaut ; `courriel`, l'ancien nom, vaut `reglage`) : réglage en base. **Réglage incomplet** (canal e-mail sans
SMTP complet, mot de passe illisible, dossier des clés absent) : fichier en développement ; en production, variables `PHARMA_CLOUD_SMTP_*`
si elles sont complètes (amorçage, comportement d'avant S7.6), sinon **refus clair** (« Envoi des codes non réglé… ») noté au journal
(`reglage:incomplet`). Les écrans ne refusent plus de démarrer faute de SMTP.

**Mot de passe SMTP** : chiffré par l'application (protection des données d'ASP.NET Core, usage `PharmaOfficine.Central.EnvoiCodes.MotDePasseSmtp.v1`)
avant d'aller en base ; la base ne garde que le texte chiffré (contrainte : base64url de 40 caractères au moins, un mot de passe en clair
est refusé). Clés dans `<PHARMA_CLOUD_DOSSIER>\cles-protection` (ou `Ecrans:DossierCles`), hors dépôt (refusé dans un dossier qui contient
`.git` ou la solution) ; sous Windows, clés protégées par DPAPI machine. Jamais réaffiché, jamais dans une adresse, un journal ou la
console. Perte du dossier de clés : le mot de passe ne se relit plus → ressaisir le mot de passe dans l'écran.

**Dossier des clés de la console (2026-11-22)** : `--envoi-codes`, `--depart-officine … --email` et `--creer-comptes … --envoyer-identifiants`
prennent les clés dans `--dossier-cles <chemin>` s'il est donné (le **même** dossier que `Ecrans:DossierCles` des écrans), sinon dans
`<--dossier ou PHARMA_CLOUD_DOSSIER>\cles-protection`. En **lecture** (`--depart-officine`, `--creer-comptes`), rien n'est jamais créé :
dossier absent ou sans clé → message clair (« aucune clé dans … (rien n'a été créé ; donnez … --dossier-cles … ») et l'e-mail ne part
pas (mot de passe de l'archive affiché, identifiants à transmettre à la main). En écriture (`--envoi-codes`), un dossier sans clé est
créé avec de nouvelles clés, après un avertissement à la console. **Protection indisponible** (dossier illisible, droits, DPAPI) : le
mot de passe est traité comme illisible (repli ou « Envoi des codes non réglé »), jamais une erreur serveur à chaque connexion ;
l'erreur n'est pas gardée (l'envoi suivant réessaie) ; enregistrement à l'écran refusé (« dossier des clés … introuvable ou illisible »).
Avertissement « réglage incomplet ou illisible » (et « mot de passe non déchiffrable ») au journal des écrans **une fois** par
changement du réglage relu, plus à chaque code envoyé par le repli.

**Comptes** : e-mail ET téléphone possibles depuis le 030 (contrainte `ck_compte_distant_contact` : au moins un) ; le 037 le constate,
ne modifie aucune ligne ; la console accepte `--telephone` et `--email` ensemble.

**Départ d'une officine** (console) : les variables `PHARMA_CLOUD_SMTP_*` restent prioritaires ; absentes, le mot de passe de l'archive
part par le compte SMTP du réglage « Envoi des codes » (mêmes clés du dossier du central).

**Gmail** : voir [poste-de-developpement.md](poste-de-developpement.md) § « Codes du central par Gmail » (mot de passe d'application).

## 3 nonies. Reste de S7.2 et S7.3 : remise à zéro d'un mot de passe, retrait d'un rattachement, message aux postes (réalisé en local le 2026-11-21)

Go du titulaire (« reste de S7.2 et S7.3 », hors SMS : aucun fournisseur). Script central **038** ; script local **160** (fermeture du
message) ; `002_tables_miroir.sql` **inchangé** (`sync_ordre_recu` n'est jamais envoyée). Aucun écran n'est en ligne.

| Route / maillon | Où | Règle |
|---|---|---|
| `/comptes?compte=…&action=mot-de-passe` (bouton « Mot de passe » du compte choisi) | écrans du central, **éditeur** | Motif (5 à 300 caractères), identifiant du compte **ressaisi**, code de confirmation ; « Remettre à zéro le mot de passe ». Son **propre** compte : aucun formulaire (« votre mot de passe se change par « Mot de passe » ») et refus par l'écran et par la base (`soi_meme`). Compte désactivé : refusé |
| `POST /actions/comptes/{compte}/mot-de-passe` | écrans du central | Mot de passe **provisoire** tiré par les écrans (16 caractères, même tirage que la console) ; seule l'empreinte BCrypt (coût 12) part vers `cloud_ecran_remise_mot_de_passe` : contrôles **avant** le code, puis empreinte remplacée, mot de passe **provisoire** (étape « mot de passe » obligatoire à la connexion suivante), échecs et verrouillage remis à zéro, **sessions ouvertes du compte fermées** (`revoquee`), codes en cours annulés ; journal des accès (éditeur, et compte remis à zéro : visible dans « Mon journal des accès ») et journal de sécurité (n° du compte, motif, sessions fermées) ; **jamais** le mot de passe ni son empreinte |
| `/comptes/mot-de-passe-affiche?compte=…&ref=…` | écrans du central, éditeur | Mot de passe provisoire montré **une seule fois** (mémoire du processus, 2 minutes, même session ; « Copier ») ; ensuite « déjà été affiché ». Jamais sur la page du code d'activation |
| `/comptes?action=creer` (« Ajouter un compte ») et `POST /actions/comptes/creer` (script central 039, 2026-11-21) | écrans du central, **éditeur** | Rôle (titulaire ou éditeur), identifiant (3 à 60 caractères, minuscules, chiffres, « . _ - »), nom, e-mail et/ou téléphone (au moins un : les codes du compte y partent), code de confirmation ; contrôles avant le code (identifiant pris, formes). Mot de passe **provisoire** tiré par les écrans, empreinte BCrypt seule en base, montré **une fois** sur `/comptes/mot-de-passe-affiche?…&cree=1` (« Compte créé ») ; changement obligatoire à la première connexion. Journaux : n° du compte et rôle seulement. Un titulaire est ensuite rattaché à son officine par le code tiré du poste. |
| `/mes-officines/{officine}/retirer` (lien « Retirer » de « Mes officines ») et `POST /actions/mes-officines/{officine}/retrait` | écrans du central, **titulaire** | Nom de l'officine **ressaisi** (casse ignorée), code de confirmation ; `cloud_ecran_titulaire_retirer_officine` : officine d'un autre compte, inconnue ou déjà retirée refusée (ouverture par la base), contrôles **avant** le code ; **effet immédiat** (chaque fonction revérifie le rattachement) ; ordres encore ouverts de ce compte pour l'officine **annulés et republiés** (même règle que le retrait depuis le poste, 034 ; auteur « titulaire à distance (rattachement retiré) ») ; journaux des accès et de sécurité. Pour revenir : nouveau code tiré sur le poste. **L'éditeur ne retire aucun rattachement** (la conception ne le prévoit pas : constaté, § 32.3 ne parle que du poste) |
| `/mes-officines/{officine}/ordres` › panneau « Message sur les postes » et `POST /actions/mes-officines/{officine}/message` | écrans du central, titulaire (droit `ordres`) | Message (200 caractères au plus, texte simple sur une ligne, sans lien), « Durée d'affichage » (liste bornée : 1 à 7 jours, sans heure calculée par les écrans, qui n'ont pas le fuseau de l'officine — 2026-11-22 ; la fin exacte, même heure N jours après l'envoi, est fixée par la base à l'envoi et affichée sur les postes à l'heure de l'officine), code de confirmation. Contrôlé par l'écran (`MessagePostes`), par la base (`cloud_message_poste_controle` dans `cloud_ecran_titulaire_envoyer_message` **et** dans la contrainte `ck_ordre_central_titulaire`) puis par le poste. Un seul message en attente par officine, 10 ordres par heure (messages compris), 24 heures pour être relevé ; publié par `ordre-v1`. Journal de sécurité : longueur et fin d'affichage, **jamais le texte** ; après un refus, le texte ne revient pas dans l'adresse |
| Réception au poste | API locale (`ExecuterOrdreCentral`) | Règles revérifiées (`MessagePostes.Lire`) ; fin déjà passée à l'horloge de l'officine : **refusé** ; sinon **appliqué tout de suite** (aucune confirmation) : « Appliqué » au central = **affiché** (résultat « affiché sur les postes jusqu'au … UTC ») |
| Pastille « Message » | poste, barre d'état, **tous les employés connectés** | `GET /api/etat-poste/message-central` (connexion seule) : le plus récent message appliqué, non fermé et pas échu (horloge de l'officine). Pastille grise (information) cliquable → petite fenêtre au-dessus de la barre (texte, « affiché jusqu'au … », reçu le ; « Ordres du central » avec le droit `admin.parametres` ; « Ne plus afficher » pour le **titulaire** de l'officine ; Échap, clic dehors ou « Fermer »). Regroupement « À traiter » respecté (voir regles-metier.md) |
| Fermeture | API locale `POST /api/etat-poste/message-central/{id}/fermer` | Titulaire seulement (403 sinon) ; `sync_message_central_fermer` (script 160) : le message disparaît de **tous** les postes ; journal des actions `message_central_ferme` (sans le texte) ; la fermeture ne remonte pas au central (le statut reste « Appliqué ») |

**Décisions (les plus sûres, prises sans question, go du titulaire)** :

1. **Mot de passe provisoire montré une fois à l'éditeur, jamais envoyé par le canal des codes du compte** : le code de connexion part
   déjà sur ce canal (e-mail) ; y envoyer aussi le mot de passe mettrait les deux facteurs dans la même boîte (une boîte compromise
   suffirait). Montré à l'éditeur seul, il se transmet de vive voix après vérification de l'identité ; l'éditeur ne peut pas s'en servir
   seul (le code part sur le contact du compte). SMS : aucun fournisseur.
2. **Verrou levé** par la remise à zéro (sinon le mot de passe provisoire resterait inutilisable jusqu'à 24 heures) ; compte désactivé :
   refusé (la remise ne réactive pas un compte).
3. Un éditeur peut remettre à zéro le mot de passe d'un **autre** éditeur (comme le contact d'un compte, S7.6), jamais le sien.
4. Message : **durée en liste bornée** (1 à 7 jours) plutôt qu'une date libre ; « lien » = protocole (`http:`, `mailto:`, `javascript:`…),
   `://`, `www.` ou nom de domaine courant (`.com`, `.net`, `.org`, `.fr`, `.dz`, `.info`, `.io`, `.app`, `.biz`, `.me`, `.co`, `.ly`,
   `.gl`, `.link`, `.xyz`) ; un autre domaine n'est pas reconnu, mais le texte est toujours affiché **en texte simple** (jamais cliquable).
5. Un nouveau message **remplace** le précédent sur les postes (le plus récent encore valable est affiché).
6. Libellé court « **Message** » (texte complet dans l'infobulle et la fenêtre) ; **au-delà de 6 pastilles grises** (6 + le message), toutes
   passent dans « À traiter » : la barre garde le cas le plus chargé mesuré le 2026-11-21.

## 3 decies. Import de comptes, durée du mot de passe provisoire, identifiant par e-mail, déconnexion après inactivité (réalisé en local le 2026-11-21)

Go du titulaire. Script central **040** (`040_import_comptes_duree_provisoire.sql`, après 039). Aucun écran n'est en ligne. Script central **041** (`041_delai_provisoire_comptes_anterieurs.sql`, après 040, revue de code du 2026-11-22) : la durée de vie d'un mot de passe provisoire part au plus tôt de son passage (délai de grâce des comptes créés avant le contrôle).
Script central **042** (`042_deconnexion_motif_inactivite.sql`, après 041, recette du 2026-11-22) : la page fermée à l'échéance
d'inactivité est notée au journal des accès comme une expiration par inactivité (motif vérifié par la base, à 30 s près).

| Route / maillon | Où | Règle |
|---|---|---|
| `/comptes?action=importer` (« Importer des comptes ») et `POST /actions/comptes/importer/apercu` | écrans du central, **éditeur** | Fichier CSV (UTF-8 avec ou sans BOM, 128 Ko et **200 comptes** au plus), ligne d'en-tête `role ; identifiant ; nom ; email ; telephone` (ordre libre, « rôle », « téléphone », « e-mail » acceptés ; séparateur « ; » ou « , », celui de l'en-tête ; champs entre guillemets permis). **Aperçu** ligne par ligne dans la colonne de gauche (seul son cadre défile) : forme (colonnes, guillemet), identifiant, **doublon dans le fichier**, **identifiant déjà pris** (base), nom, rôle, aucun contact, e-mail, téléphone. **Aucun code** demandé ni consommé. L'aperçu reste **en mémoire du processus** (15 minutes, même session ; un seul à la fois) : l'adresse ne porte qu'une référence tirée au sort, jamais les lignes. Seul ce dépôt peut dépasser 16 Ko (limite relevée avant la lecture du formulaire) |
| `POST /actions/comptes/importer` | écrans du central, éditeur | Case « Envoyer l'identifiant par e-mail » (**cochée** par défaut), **un seul code** de confirmation pour tout le fichier. Nouvel avis de la base, puis mots de passe provisoires tirés (16 caractères), empreintes BCrypt seules vers `cloud_ecran_comptes_importer` : **tout ou rien** (une ligne devenue invalide : rien n'est créé, l'aperçu montre la ligne). Résultat sur `/comptes/mot-de-passe-affiche?ref=…&import=1` |
| `/comptes/mot-de-passe-affiche` | écrans du central, éditeur | Liste montrée **une fois** (mémoire, 2 minutes, même session) : identifiant, rôle, mot de passe provisoire, « identifiant envoyé par e-mail : oui / non (case décochée) / non (aucune adresse) / échec », « mot de passe : envoyé par SMS / à transmettre par SMS (manuel) / à transmettre (manuel, aucun téléphone) » ; bouton « Copier » (tout le tableau en texte, tabulations). Même page pour la création unitaire (`&cree=1`) et la remise à zéro (un compte : mot de passe en grand). Jamais en base (empreintes seulement), jamais au journal |
| E-mail de l'identifiant | écrans (et console) | Par le compte SMTP du réglage « Envoi des codes ». Contient l'identifiant, le rôle, l'adresse des écrans (`Ecrans:AdressePublique`, https seulement ; sinon « l'adresse vous sera communiquée par l'éditeur ») et la phrase « Le mot de passe provisoire vous sera transmis séparément ». **Le mot de passe ne part jamais par e-mail.** Un échec d'envoi n'annule rien (« échec » dans la liste). Résultat noté au journal des accès du compte (`identifiant_envoi`, `email:ok` / `email:echec`) |
| Mot de passe par SMS | écrans (et console) | Si le compte a un téléphone **et** qu'un fournisseur SMS est en service (`IEnvoiSmsMotDePasse`) : envoi automatique (`provisoire_envoi`, `sms:…`). Aujourd'hui aucun fournisseur (`EnvoiSmsMotDePasseIndisponible`) : « à transmettre par SMS (manuel) » |
| Tableau « Comptes » | écrans du central, éditeur | Colonne « Mot de passe » : « provisoire (expire le …) », « provisoire expiré », rien s'il est choisi (`cloud_ecran_comptes_liste`). Compte provisoire (expiré ou non) : lien « **Régénérer le mot de passe** » vers la remise à zéro (038) avec le motif prérempli « Mot de passe provisoire expiré » (ou « Nouveau mot de passe provisoire »), modifiable, et la case « Renvoyer l'identifiant par e-mail » (cochée) |
| « Ajouter un compte » (039) | écrans du central, éditeur | Case « Envoyer l'identifiant par e-mail » (cochée) ; même transmission que l'import |
| Connexion | écrans du central | Mot de passe **juste** mais provisoire non utilisé depuis plus que la durée réglée (`coalesce(mot_de_passe_change_le, cree_le) + durée`) : refusé avec « Mot de passe provisoire expiré : demandez à l'éditeur une remise à zéro. » (journal des accès : `connexion` / `provisoire_expire`) ; mot de passe faux : message habituel (rien n'est dit sur l'état du compte) |
| « Envoi des codes » › « Mots de passe provisoires » et `POST /actions/envoi-codes/duree` | écrans du central, éditeur | Durée de vie d'un mot de passe provisoire non utilisé : **1 à 30 jours, 7 par défaut** ; code de confirmation ; journal de sécurité (ancienne et nouvelle durée). S'applique aux comptes créés à l'écran, importés, créés par la console et remis à zéro (la remise repart pour une durée complète) |
| « Envoi des codes » › « Sécurité des sessions » et `POST /actions/envoi-codes/inactivite` | écrans du central, éditeur | Déconnexion après inactivité **par rôle** : **5 à 60 minutes**, éditeur **15**, titulaire **30** par défaut ; code de confirmation ; journal de sécurité. Effet dès la requête suivante de chaque session |
| Sessions ouvertes | base (`cloud_ecran_session_valable`, remplacée par 040) | **Un utilisateur actif n'est jamais déconnecté** (décision du titulaire du 2026-11-21) : plus de durée maximale (2 h / 8 h abandonnées) ; chaque requête de l'utilisateur repousse l'échéance (`expire_le` glissante = maintenant + inactivité du rôle) ; seule l'inactivité ferme la session (motif `inactivite`, journal `session_expiree` / `inactivite`). Étapes « code » et « mot de passe à changer » : 10 minutes, inchangées |
| Bandeau « Rester connecté » | page (`ecrans.js`, en-tête `data-session-restant`) | Une minute avant l'échéance : bandeau « Vous allez être déconnecté dans 60 s » (au-dessus de la page, sans changer sa hauteur) et bouton « **Rester connecté** » (`POST /actions/activite`, jeton anti-falsification, rend les secondes restantes). Clic, touche, molette, défilement dans la page : signal d'activité **une fois par minute au plus** ; aucune requête sans action de l'utilisateur. À l'échéance : la page masque ses données et se déconnecte d'elle-même (`POST /actions/deconnexion`, motif `inactivite`, permis sans session) → « Session fermée après inactivité : reconnectez-vous. » |
| Console | `Pharma.Cloud.Api.exe --creer-comptes <fichier.csv> --auteur <nom> [--envoyer-identifiants] [--adresse-ecrans https://…] [--dossier <dossier du central>] [--dossier-cles <dossier des clés des écrans>]` | Connexion du propriétaire, **aucun code** ; mêmes contrôles, tout ou rien (`cloud_compte_distant_creer_lot`) ; liste identifiant + mot de passe provisoire affichée **une fois** ; identifiant par e-mail si demandé (variables `PHARMA_CLOUD_SMTP_*`, sinon réglage « Envoi des codes ») |

**Décisions prises sans question (les plus sûres)** :

1. **Refus d'expiration seulement après un mot de passe juste** : un mot de passe faux garde le message habituel, rien n'est appris sur
   l'état d'un compte ; aucun échec n'est compté pour un provisoire expiré.
2. **Contact pour l'e-mail d'une remise à zéro** : la base ne le rend que pour un compte remis à zéro il y a **2 minutes au plus**
   (`cloud_ecran_comptes_destinataire_remise`) ; jamais le contact complet d'un compte quelconque.
3. **Signal d'activité** = simple passage par le contrôle d'accès (lecture de la session) ; il n'existe aucune requête automatique.
4. **Adresse des écrans** dans l'e-mail : réglage facultatif `Ecrans:AdressePublique` (https seulement), sinon texte générique.
5. **Gardes de rejeu** dans `030` : rejouer `030` après `040` ne ramène pas les anciennes définitions.
6. Limites de codes inchangées (5 codes par compte et par quart d'heure ; 20 actions « code » par adresse et par 5 minutes).

## 4. Actions de l'éditeur

Chaque action : **nouveau code de second facteur** (« Recevoir un code de confirmation », objet `action`, usage unique), **motif obligatoire**
(5 à 300 caractères), **confirmation par ressaisie** (nom de l'officine, ou début d'empreinte du certificat), puis note au **journal des accès**
(sans texte libre) **et** au **journal de sécurité** du central (avec le motif, comme les révocations de la console). Les contrôles de saisie
passent **avant** le code : une faute de frappe ne consomme pas le code ; un code faux est compté (5 essais).

| Action | Fonction SQL | Réutilise |
|---|---|---|
| Créer une officine + code d'activation (24 h) | `cloud_ecran_creer_officine` | même enregistrement que `--creer-officine` |
| Nouveau code d'activation (principal / second) | `cloud_ecran_nouveau_code_activation` | même enregistrement que `--code-activation` ; refusé pour une officine partie |
| Révoquer un certificat (et ses renouvellements) | `cloud_ecran_revoquer_certificat` | déclencheur de cascade du script 001 |
| Révoquer un serveur | `cloud_ecran_revoquer_serveur` | `cloud_revoquer_serveur` (001) |

Le **code d'activation** est tiré par les écrans (`CodeActivationSync`, comme la console) à partir du **certificat public** de l'autorité
(`autorite.crt`) : seule son empreinte va en base. Les écrans ne lisent **jamais** la clé de l'autorité (test d'architecture).

## 5. Sécurité

- **Base** : rôle `pharma_cloud_ecrans` **sans connexion**, sans aucun droit sur les tables ; EXECUTE sur les seules fonctions `cloud_ecran_*`
  (`SECURITY DEFINER`, `search_path` fixé, retirées à PUBLIC). Chaque fonction **revérifie le jeton** (session ouverte, rôle). L'officine courante
  est posée **par la base** dans `cloud_ecran_ouvrir(empreinte du jeton, officine)` après vérification de la session et du droit ; poser
  `app.officine_id` soi-même ne donne rien. Au démarrage, les écrans refusent une connexion superutilisateur, BYPASSRLS, propriétaire d'une table
  ou non membre de `pharma_cloud_ecrans`.
- **Titulaire (S7.2a)** : chaque fonction du titulaire passe par `cloud_ecran_ouvrir` (session, rattachement non retiré), puis vérifie le
  droit (`consulter` pour le tableau de bord, `journal_acces` pour le journal de l'officine) ; un refus rend **zéro ligne** et reste au
  journal des accès (aucune erreur qui l'annulerait). Les tables miroir sont lues avec l'officine posée par la base (RLS forcée) : poser
  `app.officine_id` sur une autre officine ne change rien. Aucune fonction du titulaire ne rend une colonne nominative (liste fermée
  vérifiée par `032_ecrans_titulaire_tests.sql` et `TitulaireEcransTests`).
- **Mots de passe** : BCrypt coût 12, calculé par l'application ; mot de passe **provisoire** (16 caractères tirés au sort, affiché une fois par
  la console, ou par les écrans à l'éditeur qui le remet à zéro, script 038) à changer à la connexion suivante ; nouveau mot de passe : 12 à 128 caractères, lettres et chiffres, sans l'identifiant,
  différent de l'actuel. Une vérification BCrypt est toujours faite (empreinte factice si le compte est inconnu ou bloqué) : même durée de réponse.
- **Verrouillage** : 5 échecs → 15 minutes, puis durée **doublée** à chaque nouveau blocage, 24 heures au plus ; remis à zéro par une connexion
  complète ou `--deverrouiller-compte`.
- **Second facteur** : 6 chiffres, empreinte **HMAC-SHA256** (clé tirée au démarrage du processus des écrans, jamais en base : un redémarrage annule
  les codes en cours), 5 minutes, 5 essais, usage unique ; 5 codes au plus par compte et par quart d'heure. Après le bon code, **nouveau jeton**
  (pas de fixation de session).
- **Sessions** : jeton de 32 octets dans un cookie `__Host-pharma-ecrans` (Secure, HttpOnly, SameSite=Strict, Path=/, sans domaine ni date) ; la
  base n'en garde que l'**empreinte SHA-256**. Depuis le script 040 : fermée après l'**inactivité** réglée par rôle (éditeur 15 min, titulaire 30 min par défaut, 5 à 60), **aucune durée maximale** pour une session active (§ 3 decies). Déconnexion,
  changement de mot de passe (autres sessions), remise à zéro par l'éditeur (toutes les sessions du compte, script 038) et désactivation
  du compte ferment les sessions.
- **HTTP** : HTTPS seulement (TLS 1.2 ou 1.3), jeton anti-falsification sur chaque formulaire (cookie `__Host-pharma-ecrans-af`),
  `Cache-Control: no-cache, no-store` (valeur de l'anti-falsification : rien remplacé, aucun avertissement au journal à chaque page,
  2026-11-22), CSP stricte (`default-src 'none'; script-src 'self'; style-src 'self'; …; frame-ancestors 'none'`), `X-Frame-Options: DENY`,
  `Referrer-Policy: no-referrer`, HSTS hors développement, hôtes permis `127.0.0.1` et `localhost` (`AllowedHosts`), limites de débit par adresse
  (connexion 10 / 5 min, codes 20 / 5 min, global 300 / min). Page d'erreur fixe, sans détail technique. Aucune route de l'API des officines.
- **Journal des accès** (`journal_acces_distant`) : ajout seul (déclencheurs `cloud_ajout_seul`), **chaîné SHA-256** (format `acces-v1`,
  vérifiable par `SELECT * FROM cloud_ecran_verifier_journal_acces()`), sans texte libre ni donnée nominative : n° du compte, n° de session,
  officine, événement, résultat, objet technique borné. Le code n'est **jamais** écrit dans un journal ni sur la console.
- **Démarrage refusé** : adresse hors boucle locale en développement ; mode « fichier » des codes hors développement ou hors boucle locale ;
  `Ecrans:ModeCodes=sms` ; connexion absente ; certificat de l'autorité autre qu'un `.crt` ; port 5090. Depuis S7.6, un SMTP incomplet
  n'empêche plus le démarrage : chaque envoi de code est refusé clairement tant que le réglage « Envoi des codes » n'est pas complet.
- **Envoi des codes (S7.6)** : réglage modifiable par l'**éditeur seulement**, avec un code de confirmation ; noté au journal des accès
  (`envoi_codes_lu`, `envoi_codes_modifie`, `envoi_codes_essai`, `liste_comptes`, `compte_contact_modifie`, `code_envoi`) et, pour une
  modification, au journal de sécurité (canal, serveur, port, sécurité, « mot de passe remplacé / inchangé » ; jamais le mot de passe,
  l'identifiant, l'expéditeur, une adresse ni un numéro). Un éditeur peut changer le contact d'un autre compte, éditeur compris : chaque
  changement est noté (n° du compte seulement).

## 6. Développement local

1. Passer les scripts centraux **030** puis **031** sur `pharmacie_cloud_dev` (propriétaire `pharma_app`, après relecture) :
   ```powershell
   $env:PGCLIENTENCODING = "UTF8"
   & "C:\Program Files\PostgreSQL\18\bin\psql.exe" -U pharma_app -h localhost -d pharmacie_cloud_dev -v ON_ERROR_STOP=1 -f database\cloud\030_comptes_distants.sql
   & "C:\Program Files\PostgreSQL\18\bin\psql.exe" -U pharma_app -h localhost -d pharmacie_cloud_dev -v ON_ERROR_STOP=1 -f database\cloud\031_ecrans_editeur.sql
   ```
2. `.\scripts\dev\preparer_central_vs.ps1 -Preparer -WhatIf`, puis `-Preparer` : rôle `pharma_cloud_ecrans_dev_vs`, variable d'utilisateur
   `PHARMA_CLOUD_ECRANS_CONNEXION`, dossier `%LOCALAPPDATA%\PharmaOfficine\central-dev\codes`, compte **`editeur.dev`** (mot de passe provisoire
   affiché **une fois** : le noter le temps de la première connexion). Sans 030/031, rien n'est fait pour les écrans (avertissement).
3. Redémarrer Visual Studio ; profil **« Central + Écrans »** (ou « API + Poste + Central ») ; F5 : le navigateur s'ouvre sur
   **https://127.0.0.1:5094/** (certificat de développement ASP.NET : `dotnet dev-certs https --trust` ; il couvre `127.0.0.1`, vérifié le 2026-11-21).
4. Connexion `editeur.dev` ; le code est dans `%LOCALAPPDATA%\PharmaOfficine\central-dev\codes\editeur.dev.txt` (remplacé à chaque envoi) ;
   changer le mot de passe provisoire.
5. `-Etat` montre le rôle, la variable, le dossier des codes, le port 5094 et le compte ; `-Retirer` (après `-WhatIf`) retire le rôle
   `pharma_cloud_ecrans_dev_vs` et la variable, le dossier des codes avec `central-dev` ; le compte `editeur.dev` **reste** au central.

**Titulaire (S7.2a)** :

1. Passer le script central **032** sur `pharmacie_cloud_dev` (après 030 et 031, propriétaire `pharma_app`, après relecture) :
   ```powershell
   & "C:\Program Files\PostgreSQL\18\bin\psql.exe" -U pharma_app -h localhost -d pharmacie_cloud_dev -v ON_ERROR_STOP=1 -f database\cloud\032_ecrans_titulaire.sql
   ```
2. `.\scripts\dev\preparer_central_vs.ps1 -Preparer` crée le compte **`titulaire.dev`** s'il manque (mot de passe provisoire affiché
   **une fois**) ; sans 032 : avertissement, rien n'est fait pour lui. L'outil est recompilé d'office (source des comptes plus récente).
3. Code de rattachement par la console (développement), avec l'outil et la connexion du propriétaire (`PHARMA_CLOUD_ADMIN_CONNEXION`) :
   `Pharma.Cloud.Api.exe --code-rattachement titulaire.dev <officine (uuid)> --auteur "<votre nom>"` → code affiché **une fois**, 30 minutes.
   **Ou, depuis S7.2b** (script central **033** passé, après 032) : dans le poste, connecté en **titulaire**, Administration › Synchronisation ›
   Accès à distance, identifiant `titulaire.dev`, mot de passe du titulaire du poste, « Générer le code » (synchronisation activée exigée).
   Un autre compte titulaire : `--creer-compte-titulaire <identifiant> --nom "…" (--telephone … | --email …) --auteur "<votre nom>"`.
4. Connexion `titulaire.dev` sur https://127.0.0.1:5094/ (code dans `%LOCALAPPDATA%\PharmaOfficine\central-dev\codes\titulaire.dev.txt`),
   changer le mot de passe provisoire, puis « Rattacher une officine ».

**Réglages communs (S7.4)** : passer le script central **035** sur `pharmacie_cloud_dev` (après 034, propriétaire `pharma_app`, après
relecture). Voir [poste-de-developpement.md](poste-de-developpement.md).

**Lignes absentes (S7.5)** : passer le script central **036** sur `pharmacie_cloud_dev` (après 035, propriétaire `pharma_app`,
après relecture). Voir [poste-de-developpement.md](poste-de-developpement.md).

**Multi-officines et ordres (S7.3)** : passer le script central **034** sur `pharmacie_cloud_dev` (après 033, propriétaire `pharma_app`,
après relecture). Dans le poste (titulaire), Accès à distance › Comptes rattachés : « Droits », cocher « Ordres à distance », mot de
passe → le lien « Ordres » apparaît dans « Mes officines ». Voir [poste-de-developpement.md](poste-de-developpement.md).

**Reste S7.2 / S7.3** : passer le script central **038** sur la base centrale de développement (après 037, propriétaire `pharma_app`,
après relecture et sauvegarde), et le script local **160** sur les bases de l'officine (après 159). Voir [poste-de-developpement.md](poste-de-developpement.md).

Mot de passe de `editeur.dev` perdu : depuis le script 038, un **autre** éditeur le remet à zéro dans « Comptes » (« Mot de passe ») ; sans
autre éditeur : `--desactiver-compte editeur.dev` puis créer un autre compte (`--creer-compte-editeur editeur.dev2 --nom "…" --email …`)
avec l'outil (`%LOCALAPPDATA%\PharmaOfficine\central-dev\outil`).

Réglages (`appsettings*.json`, jamais de secret) : `Ecrans:Adresse` (127.0.0.1), `Ecrans:Port` (5094), `Ecrans:ModeCodes` (`fichier` en
développement, `reglage` sinon : canal et SMTP de l'écran « Envoi des codes »), `Ecrans:CertificatAutorite` (sinon `%PHARMA_CLOUD_DOSSIER%\autorite.crt`),
`Ecrans:DossierCodes`, `Ecrans:DossierCles` (S7.6, sinon `%PHARMA_CLOUD_DOSSIER%\cles-protection`).
Variables : `PHARMA_CLOUD_ECRANS_CONNEXION` (obligatoire), `PHARMA_CLOUD_DOSSIER` (certificat public, clés du mot de passe SMTP),
`PHARMA_CLOUD_SMTP_*` (facultatives : repli de production si le réglage en base est incomplet ; départ d'une officine).
Passer du mode fichier au Gmail en local : [poste-de-developpement.md](poste-de-developpement.md) § « Codes du central par Gmail ».

## 7. Tests

- SQL (transaction annulée) : `database/cloud/tests/030_comptes_distants_tests.sql`, `031_ecrans_editeur_tests.sql` ; `032_…` à `036_…` : voir S7.2a à S7.5 ci-dessous.
- `tests/Pharma.Cloud.Web.Tests` : réglages (adresse non locale refusée, mode fichier refusé hors développement, SMS, clé de l'autorité),
  second facteur, contrôle d'accès, mots de passe ; architecture (aucun style en ligne, aucune route de l'API des officines, jamais la clé de
  l'autorité, aucun script Blazor ni interactivité, anti-falsification sur chaque formulaire, lecture par `cloud_ecran_*` seulement) ;
  base jetable : verrouillage, code faux / réutilisé / expiré, sessions (déconnexion, expiration), aucune colonne ni valeur interdite ;
  **bout en bout HTTPS** (serveur démarré sur un port libre de 127.0.0.1) : connexion, code, mot de passe provisoire, liste, toutes les
  rubriques, création d'une officine et code affiché une seule fois, déconnexion, chaîne du journal intacte.
- `tests/Pharma.Cloud.Tests/ComptesDistantsConsoleTests.cs` : commandes de la console (dont `--creer-compte-titulaire` et `--code-rattachement` :
  `--auteur` obligatoire, empreinte seule en base, refus pour un éditeur ou une officine inconnue), puis les tests SQL 030 à 032 sur la même base jetable.
- **S7.2a** : `database/cloud/tests/032_ecrans_titulaire_tests.sql` ; `tests/Pharma.Cloud.Web.Tests/TitulaireEcransTests.cs` (dépôt : deux
  titulaires, deux officines, non-fuite, rattachement, agrégats exacts, « non disponible », rôles croisés refusés par la base ; **bout en bout
  HTTPS** : connexion du titulaire, rattachement, tableau de bord, journal, officine d'un autre compte refusée, écrans de l'éditeur refusés au
  titulaire et écrans du titulaire refusés à l'éditeur) ; `ReglesEcransTests` (aiguillage par rôle, code de rattachement, « non disponible »).
- **S7.2b** : `database/cloud/tests/033_code_rattachement_poste_tests.sql` (officine du certificat seulement, réponse neutre, limite,
  mêmes règles que la console, code utilisable par `/rattacher`, acteur « poste ») ; `tests/Pharma.Cloud.Tests/RattachementPosteTests.cs`
  (rôle `pharma_cloud_api` sur base jetable, réponses neutres, route authentifiée comme l'échange) ; `tests/Pharma.Application.Tests/AccesDistantTests.cs`
  (titulaire seulement, mot de passe requis, synchronisation non activée, refus neutre et limite, code jamais journalisé) ;
  `tests/Pharma.Architecture.Tests/AccesDistantEcranTests.cs` (écran, route locale, démonstration).
- **S7.3** : `database/cloud/tests/034_titulaire_multi_officines_ordres_tests.sql` (A rattaché à O1 et O2, B à O2 : non-fuite, droit `ordres`
  par le poste seulement, ordre refusé sans droit / hors liste / déjà en cours / code faux, ordre expiré, retrait immédiat et ordres annulés) ;
  `tests/Pharma.Cloud.Tests/AccesDistantsPosteTests.cs` (rôle `pharma_cloud_api`, autre certificat refusé) ; `TitulaireEcransTests` (bout en
  bout : vue d'ensemble, ordres refusés sans droit, code, ordre envoyé, hors liste, autre officine) ; `ReglesEcransTests` (aiguillage, total
  partiel) ; `tests/Pharma.Application.Tests/OrdresCentrauxTests.cs` et `AccesDistantTests.cs` ; `AccesDistantEcranTests`.

- **S7.4** : `database/cloud/tests/035_reglages_communs_titulaire_tests.sql` (liste fermée et bornes dans la fonction et la table,
  console refusée ; non-fuite : B n'envoie pas vers O1 et ne voit pas les envois de A ; refus sans droit `ordres` sur une officine, rien
  de créé ; hors liste, hors bornes, incohérent, code faux ; rejeu refusé ; accepté / refusé remontés ; expiration) ; `ReglesEcransTests`
  (accès, liste de l'écran = liste de la base, saisie) ; `TitulaireEcransTests` (bout en bout HTTPS : page, hors liste, hors bornes,
  officine d'un autre titulaire, envoi, suivi « Proposé ») ; `tests/Pharma.Application.Tests/OrdresCentrauxTests.cs` (rien sans
  acceptation, mot de passe faux, hors liste et hors bornes refusés à la réception, table fixée par le central, rouge combiné, refus,
  expiration, rejeu) ; `tests/Pharma.Domain.Tests/ReglagesCommunsTests.cs`.

- **S7.5** : `database/cloud/tests/036_lignes_absentes_ecrans_tests.sql` (droits, index, causes, résumé, ligne reçue de nouveau,
  aucune valeur de ligne, non-fuite : B ne voit jamais O1, « non disponible », rôles croisés, journal) ; `ReglesEcransTests` (rubrique,
  libellés, causes de l'écran = causes de la base, aucune « purge », vue d'ensemble rapprochée) ; `TitulaireEcransTests` (dépôt : 1 ligne
  absente, non disponible, non-fuite, rôles croisés ; bout en bout : tuile et colonne) ; `BoutEnBoutEcransTests` (rubrique « Lignes
  absentes » en HTTPS : tuiles, n° du chargement, cause, pied de page, aucune valeur de ligne) ; `031_…` : comptage des fonctions de
  l'éditeur sans celles du 036.
- **Envoi des codes (S7.6)** : `037_envoi_codes_reglage_tests.sql` (droits, une seule ligne, mot de passe en clair refusé, lecture sans
  mot de passe, titulaire refusé, contrôles avant le code, SMTP complet exigé, vide = inchangé, journaux sans secret, `les_deux`,
  contact d'un compte) ; `EnvoiCodesEcransTests` (canal respecté : e-mail, SMS indisponible, les deux ; réglage incomplet : fichier en
  développement, refus en production ; mode fichier prioritaire ; écrans refusés au titulaire ; **bout en bout HTTPS** : e-mail d'essai,
  enregistrement avec code, mot de passe chiffré jamais rendu par la page ni écrit au journal, champ vide = inchangé, contact d'un compte,
  canal relu en base à chaque connexion) ; `EnvoiCodesReglageTests` (migration sans perte des comptes et des codes depuis l'état d'avant
  037, rejeu, réglage relu par la console, protection des données hors dépôt) ; rejeu 001 → 037 (`RejeuScriptsCentrauxTests`).
- **Reste S7.2 / S7.3 (script 038)** : `038_remise_mot_de_passe_retrait_message_tests.sql` (droits ; contrôle du message dans la fonction
  et dans la table ; non-fuite : B n'envoie pas de message à O1 et ne retire pas O1 ; hors bornes refusé avant le code ; retrait : accès
  coupé, ordres annulés et republiés ; remise à zéro : titulaire refusé, jamais son propre compte, motif, confirmation, code, sessions
  fermées, provisoire, verrou levé, compte désactivé, empreinte jamais au journal) ; `ResteS7EcransTests` (accès par rôle, règles du
  message identiques à la base, messages de chaque refus ; **bout en bout HTTPS** : son propre compte refusé, mot de passe provisoire
  montré une fois et jamais au journal, session du titulaire fermée, ancien mot de passe refusé, changement obligatoire ; message hors
  bornes refusé, envoyé avec le code ; B refusé sur O1 ; retrait avec confirmation, accès coupé, message annulé) ;
  `tests/Pharma.Domain.Tests/MessagePostesTests.cs` ; poste : `OrdresCentrauxTests` (message appliqué tout de suite, hors bornes et
  échu refusés), `MessageCentralTests` (plus récent valable, jamais un échu, fermeture par le titulaire seul, pastille pour tous,
  regroupement « À traiter ») ; `database/tests/160_message_central_postes_tests.sql` ; rejeu 001 → 038 (`RejeuScriptsCentrauxTests`).
- **Tuiles navigables** : `TuilesEcransTests` (chaque tuile, pour chaque jeu de droits et chaque cause, mène à une route `@page`
  réelle permise à son rôle et refusée à l'autre par `ControleAcces` ; ordres seulement avec le droit « ordres » ; cause → rubrique ;
  ancre du tableau avec la page gardée ; toutes les `<TuileTableau>` des écrans ont un `Lien` ; rendu HTML lien + infobulle ou bloc).

## 8. Reste à faire

- **Reste S7.2 / S7.3 (script 038, local 160)** : scripts **à passer** (central 038 sur la base centrale de développement, local 160 sur
  les bases de l'officine, après relecture et sauvegarde ; `schema.sql` déjà à jour) ; écrans **à mesurer** (1 366 × 768 et 1 536 × 890,
  clair et sombre) : `/comptes?compte=…&action=mot-de-passe` (avec et sans message, son propre compte), `/comptes/mot-de-passe-affiche`,
  `/mes-officines/{officine}/retirer`, `/mes-officines/{officine}/ordres` (deux panneaux côte à côte et le tableau : défilement à
  surveiller) ; au poste, la barre d'état avec la pastille « Message » (seule, regroupée dans « À traiter », 7 grises) et sa fenêtre
  ouverte (titulaire et vendeur). Essai de bout en bout poste ↔ central **non fait**. Restent : SMS (fournisseur à choisir), alertes
  de refus en série (non demandées dans ce lot).

- **S7.6** : script central 037 **à passer** sur la base centrale de développement (après relecture) ; écrans **à mesurer**
  (1 366 × 768 et 1 536 × 890, clair et sombre) : `/envoi-codes` (sans message et avec un message), `/comptes` et `/comptes?compte=…`
  (formulaire ouvert) ; essai réel avec un compte Gmail (mot de passe d'application) **non fait** ; SMS : aucun fournisseur.
  Hors Windows, les clés de la protection des données ne sont protégées que par les droits du dossier (à décider à la mise en ligne).

- **S7.5** : script central 036 **à passer** sur `pharmacie_cloud_dev` (après relecture) ; écrans **à mesurer** (1 366 × 768 et
  1 536 × 890, clair et sombre) : `/officines/{officine}?rubrique=absentes` (14 rubriques dans le menu de gauche, avec et sans lignes
  absentes), `/mes-officines/{officine}` (14 tuiles), `/mes-officines/ensemble` (11 colonnes : défilement horizontal à surveiller).
  Non fait : cause « purge » (aucune purge connue du central), anomalie pour une cause non expliquée (non prévue par la conception).

- **S7.4** : script central 035 passé deux fois sur `pharmacie_cloud_dev`, essai de bout en bout réussi le 2026-11-21 (proposition
  « Péremption : alerte orange 150 jours » reçue, acceptée sur le poste, « Accepté » au central). Mesurés à 0 px (1 366 × 768 et
  1 536 × 890, clair et sombre) : `/mes-officines/reglages` (avec un message aussi ; défauts corrigés, charte « Mesures du 2026-11-21
  (après-midi) ») et, au poste, la rubrique « Réglages mutualisés » avec une proposition en attente (titulaire). Non mesurés : la
  fenêtre « Accepter / Refuser » des Ordres du central et la rubrique vue par un vendeur. Motifs, catégories et textes de ticket non
  proposés (voir § 3 quinquies).

- **Mise en ligne** (plus tard) : hébergeur, nom de domaine et certificat, proxy inverse (adresse réelle pour les limites de débit), connexion de
  production rattachée à `pharma_cloud_ecrans`, compte SMTP, clé HMAC commune si plusieurs instances, comptes éditeur réels, test d'intrusion.
- **S7.2b** : génération du code par le poste **réalisée en local** (§ 3 ter) ; comptes rattachés vus et retirés depuis le poste : **S7.3**
  (§ 3 quater) ; la commande `--code-rattachement` de la console reste pour le développement.
- **S7.3** : ordre « message à afficher sur les postes » **réalisé en local** (§ 3 nonies : pastille de la barre d'état). Écrans S7.3 mesurés à 0 px le 2026-11-21 : `/mes-officines/ensemble`, `/mes-officines/{officine}/ordres` sans le droit
  et avec le droit accordé (1 366 × 768, statut « Appliqué »), rubrique « Accès à distance » avec la zone de droits ou de retrait ouverte.
- Reste de S7.2 : SMS (fournisseur à choisir), alertes (refus en série). Remise à zéro d'un mot de passe par l'éditeur et retrait d'un
  rattachement depuis les écrans : **réalisés en local** (§ 3 nonies).
- Mesure des écrans à 1 366 × 768 et 1 536 × 890, clair et sombre (règle de l'écran fixe) : **faite le 2026-11-21** pour S7.1
  (19 pages), S7.2a (`/mes-officines`, `/rattacher`, `/mes-officines/{officine}`, `/mes-officines/{officine}/journal`), S7.3, S7.4 et la
  rubrique « Accès à distance » du poste ([charte-graphique.md](charte-graphique.md)).
