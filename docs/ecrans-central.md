# Écrans du serveur central (lot S7)

> **Statut (2026-11-21)** : lot **S7.1 réalisé en local** (écrans de l'éditeur), go du titulaire, toutes les recommandations de la
> conception retenues. Lot **S7.2a réalisé en local** (écrans du titulaire, **côté serveur central seulement**, go du titulaire,
> recommandations S7 acceptées) : § 3 bis. **Rien n'est en ligne** : la mise en ligne (hébergeur, certificat, SMTP, connexion de production)
> viendra plus tard. Lot **S7.2b réalisé en local** (rubrique « Accès à distance » du poste : le titulaire tire lui-même le code de
> rattachement, go du titulaire, recommandations acceptées) : § 3 ter. Lot **S7.3 réalisé en local** (multi-officines : vue d'ensemble ; droit
> `ordres` accordé depuis le poste, comptes rattachés et retrait ; ordres à distance du titulaire en liste fermée) : § 3 quater. Lot **S7.4
> réalisé en local** (réglages communs proposés par le titulaire à plusieurs officines, chaque officine décide sur son poste) : § 3 quinquies.
> Lot **S7.5 réalisé en local** (lignes absentes : rubrique de la fiche d'une officine pour l'éditeur, compteur du titulaire, nombres
> seulement) : § 3 sexies.

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
| Routes `GET /api/v1/acces-distants`, `POST …/droits`, `POST …/retrait` (S7.3) | API centrale (mTLS) | Comptes rattachés de l'officine du certificat, droits, retrait |
| Route `POST /api/v1/rattachement` (S7.2b) | API centrale (`Pharma.Cloud.Api`, mTLS) | Code de rattachement de l'officine du certificat, montré une fois |
| Rubrique « Accès à distance » (S7.2b) | poste, Administration › Synchronisation | Le titulaire génère le code (mot de passe ressaisi) |
| Commandes de la console du central | `Pharma.Cloud.Api.exe` | `--creer-compte-editeur`, `--creer-compte-titulaire` (S7.2a), `--code-rattachement` (S7.2a), `--comptes-distants`, `--desactiver-compte`, `--deverrouiller-compte` |
| Tests | `tests/Pharma.Cloud.Web.Tests`, `tests/Pharma.Cloud.Tests/ComptesDistantsConsoleTests.cs`, `database/cloud/tests/030_…` à `036_…` | Voir § 7 |

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
central, S5) : la liste la plus prudente est retenue, **sans** le « message à afficher sur les postes » (voir § 8). Aucun ordre ne modifie
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
  la console) à changer à la première connexion ; nouveau mot de passe : 12 à 128 caractères, lettres et chiffres, sans l'identifiant,
  différent de l'actuel. Une vérification BCrypt est toujours faite (empreinte factice si le compte est inconnu ou bloqué) : même durée de réponse.
- **Verrouillage** : 5 échecs → 15 minutes, puis durée **doublée** à chaque nouveau blocage, 24 heures au plus ; remis à zéro par une connexion
  complète ou `--deverrouiller-compte`.
- **Second facteur** : 6 chiffres, empreinte **HMAC-SHA256** (clé tirée au démarrage du processus des écrans, jamais en base : un redémarrage annule
  les codes en cours), 5 minutes, 5 essais, usage unique ; 5 codes au plus par compte et par quart d'heure. Après le bon code, **nouveau jeton**
  (pas de fixation de session).
- **Sessions** : jeton de 32 octets dans un cookie `__Host-pharma-ecrans` (Secure, HttpOnly, SameSite=Strict, Path=/, sans domaine ni date) ; la
  base n'en garde que l'**empreinte SHA-256**. Éditeur : 2 heures ; titulaire (S7.2) : 30 minutes d'inactivité, 8 heures au plus. Déconnexion,
  changement de mot de passe (autres sessions) et désactivation du compte ferment les sessions.
- **HTTP** : HTTPS seulement (TLS 1.2 ou 1.3), jeton anti-falsification sur chaque formulaire (cookie `__Host-pharma-ecrans-af`),
  `Cache-Control: no-store`, CSP stricte (`default-src 'none'; script-src 'self'; style-src 'self'; …; frame-ancestors 'none'`), `X-Frame-Options: DENY`,
  `Referrer-Policy: no-referrer`, HSTS hors développement, hôtes permis `127.0.0.1` et `localhost` (`AllowedHosts`), limites de débit par adresse
  (connexion 10 / 5 min, codes 20 / 5 min, global 300 / min). Page d'erreur fixe, sans détail technique. Aucune route de l'API des officines.
- **Journal des accès** (`journal_acces_distant`) : ajout seul (déclencheurs `cloud_ajout_seul`), **chaîné SHA-256** (format `acces-v1`,
  vérifiable par `SELECT * FROM cloud_ecran_verifier_journal_acces()`), sans texte libre ni donnée nominative : n° du compte, n° de session,
  officine, événement, résultat, objet technique borné. Le code n'est **jamais** écrit dans un journal ni sur la console.
- **Démarrage refusé** : adresse hors boucle locale en développement ; mode « fichier » des codes hors développement ou hors boucle locale ;
  SMS (aucun fournisseur) ; SMTP incomplet en production ; connexion absente ; certificat de l'autorité autre qu'un `.crt` ; port 5090.

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

Mot de passe de `editeur.dev` perdu : aucune commande ne remet un mot de passe en S7.1 ; `--desactiver-compte editeur.dev` puis créer un autre
compte (`--creer-compte-editeur editeur.dev2 --nom "…" --email …`) avec l'outil (`%LOCALAPPDATA%\PharmaOfficine\central-dev\outil`).

Réglages (`appsettings*.json`, jamais de secret) : `Ecrans:Adresse` (127.0.0.1), `Ecrans:Port` (5094), `Ecrans:ModeCodes` (`fichier` en
développement, `courriel` sinon), `Ecrans:CertificatAutorite` (sinon `%PHARMA_CLOUD_DOSSIER%\autorite.crt`), `Ecrans:DossierCodes`.
Variables : `PHARMA_CLOUD_ECRANS_CONNEXION` (obligatoire), `PHARMA_CLOUD_DOSSIER` (certificat public), `PHARMA_CLOUD_SMTP_*` (production).

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

## 8. Reste à faire

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
- **S7.3, non fait** : ordre « message à afficher sur les postes » (surface d'affichage commune aux postes à décider ; la barre d'état
  est livrée). Écrans S7.3 mesurés à 0 px le 2026-11-21 : `/mes-officines/ensemble`, `/mes-officines/{officine}/ordres` sans le droit
  et avec le droit accordé (1 366 × 768, statut « Appliqué »), rubrique « Accès à distance » avec la zone de droits ou de retrait ouverte.
- Reste de S7.2 : SMS (fournisseur à choisir), remise à zéro d'un mot de passe par l'éditeur, retrait d'un rattachement depuis les écrans,
  alertes (refus en série).
- Mesure des écrans à 1 366 × 768 et 1 536 × 890, clair et sombre (règle de l'écran fixe) : **faite le 2026-11-21** pour S7.1
  (19 pages), S7.2a (`/mes-officines`, `/rattacher`, `/mes-officines/{officine}`, `/mes-officines/{officine}/journal`), S7.3, S7.4 et la
  rubrique « Accès à distance » du poste ([charte-graphique.md](charte-graphique.md)).
