| `062_vues_parametres_nommes.sql` | Vues `v_etat_stock` et `v_risque_peremption` : réglages lus par colonnes nommées au lieu de `SELECT * FROM parametre_alerte` (le `*` figé à la création faisait différer une base rejouée de `init/schema.sql`) ; `v_mouvement_tresorerie` : libellé nommé sur les deux lignes du `UNION`. Mêmes colonnes et mêmes résultats. Une base neuve (`init/`) et une base mise à jour (migrations) ont désormais exactement la même structure. |
| `063_revocation_connexions.sql` | Révocation des connexions : `utilisateur.jetons_revoques_le`, posée par le déclencheur `trg_revoquer_connexions` quand le mot de passe change, que le compte est désactivé ou remis en « mot de passe à changer », ou que le code comptoir est effacé ; posée pour tous les comptes après une restauration. L'API refuse toute connexion ouverte avant. |
| `064_caisse_cloturee.sql` | Aucune écriture d'argent sur une caisse clôturée : déclencheur `trg_caisse_ouverte` (fonction `verifier_caisse_ouverte`) sur `vente`, `paiement`, `annulation_vente`, `retour_client`, `reglement_fournisseur` (part du tiroir) et `mouvement_tresorerie` (pendant la journée). |
| `065_stock_vendable_hors_bloque.sql` | Stock vendable sans les lots bloqués : `v_etat_stock` (mêmes colonnes) et `lot_fefo` ignorent les lots bloqués (rappel). |
| `066_alertes_peremption_claires.sql` | Alertes de péremption utiles seulement : `generer_alertes` ne crée plus d'alerte pour un lot dont toutes les boîtes seront vendues à temps ; messages qui disent combien de boîtes, quelle valeur et quoi faire. |
| `067_controle_interne.sql` | « Surveillance » devient « Contrôle interne » dans les messages d'anomalie (`surveillance_candidats`, `surveillance_candidats_comptes`) ; noms techniques inchangés. |
| `068_verification_destinataire.sql` | Vérification d'un destinataire des notifications par un code : `destinataire_notification.code_verification_hash` (haché), `code_verification_expire` (15 minutes), `code_verification_essais` (5 au plus). |
| `069_receptions_attente.sql` | Réceptions en attente : table `reception_attente` (saisie de l'écran Réceptions en JSON, mode `reception` / `facture` / `comptage` / `recompte`, scans du mobile repris) ; mise en attente par bouton, en quittant l'écran ou en se déconnectant, reprise depuis n'importe quel poste. |
| `070_comptage_intermediaire.sql` | Comptage intermédiaire de la caisse : table `comptage_intermediaire` (tiroir compté sans clôturer : compté, attendu et écart au moment du comptage, détail des coupures) ; attendu et écart réservés au droit de voir les écarts. |
| `061_tva_sorties.sql` | TVA à reverser sur les pertes et dons : table `parametre_rapport` (une ligne : `tva_reversee_perime`, `tva_reversee_casse`, `tva_reversee_don`, « non » par défaut), réglée par le titulaire selon l'avis de son comptable ; les rapports en tirent la marge après pertes et dons. |
# Base de données

PostgreSQL 17+. Le schéma est défini par les scripts du dossier `database/`, exécutés dans l’ordre des numéros. EF Core lit et écrit les données mais ne crée pas le schéma.

## Ordre des scripts

| Script | Contenu |
| --- | --- |
| `001_schema.sql` | Tables de base : utilisateurs, catalogue, fournisseurs, lots, ventes, patients, bordereaux, registre des psychotropes, journal |
| `002_alertes_stock.sql` | Paramètres, consommation, risque de péremption, état du stock, alternatives, FEFO, blocage du périmé, table et génération des alertes |
| `003_provenance.sql` | Réceptions, grossiste de chaque lot, boîtes sérialisées, retours fournisseurs |
| `004_codes_barres_internes.sql` | Codes EAN-13 internes (préfixe 20), recherche au scan |
| `005_seuils_peremption.sql` | Seuils de péremption par pharmacie, grossiste et produit |
| `006_droits_utilisateurs.sql` | Rôles, permissions, journal non modifiable |
| `007_tracabilite_boite.sql` | Identité de chaque boîte, historique, vente boîte par boîte, rappel de lot |
| `008_reprise_donnees.sql` | Schéma `import` pour la reprise de l’ancien logiciel |
| `009_caisse.sql` | Numéros de ticket, sessions de caisse, comptage de clôture, paramètres de caisse, remise |
| `010_administration.sql` | Identité de la pharmacie, délai de reprise des grossistes, contrôles des seuils, protection du rôle titulaire |
| `011_ordonnance_scannee.sql` | Image et texte lu de l'ordonnance scannée, date de création |
| `012_achats.sql` | Numéros de commande (C-000001) et de retour (R-000001), suivi des retours (envoi, avoir attendu et reçu), montant TTC des réceptions, recherche au scan par étiquette de boîte |
| `013_notifications.sql` | Réglages des notifications (SMTP, modem GSM, heure du rapport, seuils SMS), destinataires vérifiés, file des messages sortants avec filet contre les adresses non vérifiées |
| `014_sauvegardes.sql` | Réglages et journal non modifiable des sauvegardes automatiques |
| `015_catalogue.sql` | Droit « catalogue.creer » (titulaire et assistant) et unicité nom, dosage et forme des produits |
| `016_emplacements.sql` | Emplacements de rangement (rayon, étagère, tiroir ; zone de vente ou réserve), emplacement de vente et réserve de chaque produit |
| `017_inventaire.sql` | Inventaire tournant : seuils de correction automatique, inventaires par rayon et lignes par lot (comptage à l'aveugle, écart, décision), type de notification « écart d'inventaire » |
| `018_lot_unique.sql` | Lot unique par produit, n° de lot et grossiste (n° normalisé en majuscules) ; réception et prix d'achat gardés sur chaque entrée en stock (`mouvement_stock`) et réception propre à chaque boîte (`boite.reception_id`) ; `creer_boites_internes` accepte la réception |
| `019_mobile.sql` | Scanner mobile : boîtes scannées au déballage (`scan_reception`), en attente d'import dans la réception du poste ; une même boîte sérialisée en attente une seule fois |
| `020_seuils_stock.sql` | Seuil d'alerte de stock par produit : `produit.stock_minimum` devient le seuil saisi (NULL = automatique, 0 = pas d'alerte) et remplace le calcul automatique dans `v_etat_stock` ; la commande suggérée remonte au moins au seuil |
| `021_prix_boite.sql` | Prix d'achat réel par boîte (`boite.prix_achat_ht`, celui de sa livraison) ; plus de moyenne sur le lot ; vue `v_valeur_lot` (valeur réelle d'un lot) utilisée pour la valeur du stock, la valeur à risque et les écarts d'inventaire ; un lot = produit + n° + grossiste + péremption (`ux_lot_fournisseur_date`) |
| `data/900_donnees_demo.sql` | Données fictives (développement et tests seulement) |
| `data/901_complement_sintrom.sql` | Complément fictif : Sintrom 4 mg (acénocoumarol) et un lot en stock, pour tester les ordonnances manuscrites ; ignoré sans données de démonstration |
| `data/902_emplacements_demo.sql` | Emplacements fictifs (rayons A à E, réfrigérateur F-01, armoire des psychotropes P-01, réserve R-01 et R-02) et rangement des produits de démonstration , et un lot « date à vérifier » (AM55) pour tester l'inventaire |
| `data/903_factures_demo.sql` | Factures clients fictives, une par état du suivi : à encaisser récente, à encaisser ancienne (alerte), avec avoir partiel, rejetée par l'organisme, réglée en partie par un versement en banque, soldée, retirée du suivi. Lignes libres : aucune vente n'est modifiée |

| `022_caisse_partagee.sql` | Une caisse ouverte par poste (`ux_session_poste_ouverte` remplace `ux_session_ouverte` par employé) ; `session_caisse.releve_de` (relève) ; code comptoir dans `utilisateur.pin_hash` (prévu en 006) avec `code_comptoir_essais` et `code_comptoir_bloque_jusqu`. S'arrête si un poste a déjà deux caisses ouvertes |

| `023_nomenclature.sql` | Nomenclature nationale du MIPH (`nomenclature_version`, `nomenclature_produit` : en vigueur, non renouvelés, retirés ; seule la dernière version est gardée) ; `substance_surveillee` (psychotropes à risque avéré d'abus, arrêté du 11 août 2021) ; vues `v_nomenclature` (classe proposée) et `v_produit_a_verifier` ; fonction `majuscule_sans_accent` |

| `024_registre_psychotropes.sql` | Registre complet : déclencheur `trg_mouvement_registre` (réception, retours, périmé, casse, écarts d'inventaire inscrits automatiquement ; la vente reste inscrite par l'application) ; porteur de l'ordonnance (nom, adresse, pièce) ; numéro d'ordre sans trou (`numero_ordre`, attribué sous verrou) ; solde d'ouverture par lot au nom du titulaire ; vue `v_registre_psychotrope` (soldes produit et lot). Seule modification de lignes existantes : la numérotation initiale (`numero_ordre`), déclencheur d'immuabilité suspendu le temps de cette mise à jour |

| `025_affichage.sql` | Affichage choisi par chaque employé : `utilisateur.affichage` (auto, clair, sombre ; auto par défaut) |

| `026_ventes_attente.sql` | Ventes en attente : `panier_attente` (poste, employé, libellé, nombre de produits, total, contenu du panier en JSON) ; reprise = suppression en une requête (un seul employé la reprend) |

| `027_annulation_vente.sql` | Annulation d’une vente : `annulation_vente` (motif, employé, validateur pour une vente ancienne, mode, argent rendu, caisse d’où il sort) ; l’attendu de cette caisse est diminué de l’argent rendu |

| `028_retour_client.sql` | Retour de médicaments par le patient : `retour_client` (motif, employé, validateur, argent rendu, caisse) et `retour_client_ligne` (quantité, destination stock ou destruction, montant) ; mouvement `destruction` ; le registre des psychotropes inscrit retour puis destruction |

| `029_reglements.sql` | Règlements : droit `achats.regler` ; `reglement_fournisseur` (date, montant, moyen espèces / chèque / virement / effet, référence, banque, échéance, complément pris dans le tiroir et caisse concernée) et `reglement_facture` (imputation par réception) ; vue `v_facture_fournisseur` (montant, réglé, reste) ; un remboursement de crédit client est un `paiement` sans vente rattaché à la caisse du poste |

| `030_tresorerie.sql` | Trésorerie : droit `tresorerie.gerer` (titulaire) ; `parametre_caisse.versement_cloture` (manuel / automatique) ; `compte_tresorerie` (un coffre, des banques, une seule reçoit les cartes, solde et date de départ) ; `mouvement_tresorerie` (dépense par catégorie, encaissement, versement d’organisme avec bordereau, transfert, versement de caisse pendant ou après la session) ; `remise_cheques` ; `reglement_fournisseur.compte_id` ; vues `v_mouvement_tresorerie` (saisis + calculés : cartes, remises, grossistes, remboursements carte), `v_solde_tresorerie`, `v_cheque_a_remettre`, `v_caisse_a_verser` |

| `031_facture_client.sql` | Factures clients : droit `factures.gerer` (titulaire) ; `pharmacie.rc`, `nis`, `article_imposition` ; `organisme.adresse`, `nif`, `actif` ; `client_facture` (entreprises) ; `facture_compteur` (numéros F- et AV- par année, sans trou) ; `facture_client` (destinataire patient / entreprise / organisme figé à l’émission, totaux HT / TVA / TTC, déjà payé au comptoir, statut émise / rejetée / remplacée, version, remplacement, avoir, retrait du suivi) ; `facture_client_ligne` (part patient / organisme / libre) ; `facture_client_version` (versions précédentes après correction) ; `reglement_facture_client` (mouvements de trésorerie qui soldent une facture) ; vues `v_facture_client` (reste à encaisser) et `v_ligne_vente_facturable` (quantités nettes des retours, déjà facturée par part) |

| `032_inventaire_complet.sql` | Inventaire complet : `campagne_inventaire` (mode pharmacie ouverte / fermée, une seule en cours, totaux à la clôture), `inventaire.campagne_id` (rayons comptés dans la campagne), `campagne_inventaire_ligne` (état d’inventaire figé : stock par lot au prix d’achat HT) ; lots sans emplacement comptés dans « (Sans emplacement) » |

| `033_index_performance.sql` | Performance (mesurée sur 5 ans simulés) : index des clés étrangères lues à chaque vente, ticket, retour, crédit, facture (CONCURRENTLY : à lancer hors transaction) ; fonction `sans_accent()` indexée en trigrammes (recherche produit) ; `v_solde_tresorerie` lue depuis la date de départ de chaque compte ; `v_ligne_vente_facturable` avec retours lus ligne par ligne |

| `034_nomenclature_absents.sql` | `v_produit_a_verifier` : signale aussi (motif `absent`) un produit dont le n° d’enregistrement n’est plus dans la nomenclature chargée ; rien n’est modifié automatiquement |

| `035_mot_de_passe_a_changer.sql` | `utilisateur.doit_changer_mot_de_passe` : mot de passe provisoire (compte titulaire de la mise en production) à changer à la première connexion ; aucun droit avant |
| `036_solde_tresorerie_echeance.sql` | `v_solde_tresorerie` : un effet remis à un grossiste ne diminue le solde de la banque qu'une fois son échéance atteinte (avant : dès sa saisie, même à échéance future). |
| `037_produit_a_verifier.sql` | `produit.a_verifier` : produit créé par la reprise sans n° d'enregistrement, non vendable tant que le titulaire n'a pas confirmé sa classe (Administration, Nomenclature) ; `v_produit_a_verifier` le signale (motif `repris`). `import.charger` (008) pose l'indicateur : **rejouer 008 après ce script** sur une base installée. |
| `038_releve_credit.sql` | Relevé de la dette des patients : `credit_mouvement` (une ligne par mouvement de `patient.solde_credit`, écrite par le déclencheur `trg_patient_credit` ; contexte posé par `credit_contexte()` ; non modifiable), `credit_ouverture` (dette antérieure aux ventes du logiciel), vues `v_credit_accorde` et `v_vente_credit` (réglé et reste dû par vente à crédit, dettes les plus anciennes soldées d'abord) ; `v_facture_client` : le reste à encaisser retire ce qui est réglé sur les ventes à crédit facturées (colonne `credit_regle`). |
| `039_suivi_dettes.sql` | Suivi des dettes et créances de tous les tiers : `fournisseur_mouvement` (relevé de la dette envers chaque grossiste, écrit par le déclencheur `trg_fournisseur_dette`, contexte posé par `dette_fournisseur_contexte()`, non modifiable) ; `parametre_caisse.delai_alerte_dette_jours` (ancienneté signalée, 30 jours par défaut) ; vue `v_suivi_dettes` (une ligne par somme due : patients, factures clients, bordereaux d'organismes, factures de grossistes, effets à échéance) |
| `040_facture_echeance.sql` | Factures clients : `facture_client.date_echeance` (date d'échéance, obligatoire pour une facture, jamais avant la date d'établissement `date_facture` ; pas d'échéance pour un avoir ; factures existantes : établissement + 30 jours) ; `v_suivi_dettes` porte l'échéance des factures (une facture est en retard quand son échéance est dépassée). `v_facture_client` n'est pas modifiée : l'échéance se lit dans la table |
| `041_contacts.sql` | Coordonnées : `utilisateur.telephone` et `utilisateur.email` (employé, saisis dans « Utilisateurs et droits ») ; `organisme.telephone` et `organisme.email` (saisis dans « Factures › Organismes »). Colonnes facultatives, affichées dans la fiche contact |
| `042_email_tiers.sql` | E-mail facultatif des grossistes (`fournisseur.email`), des entreprises clientes (`client_facture.email`) et des patients (`patient.email`) : saisi à la création ou depuis la fiche contact, signalé « à compléter » tant qu'il manque |
| `043_historique_factures.sql` | Historique des factures clients intouchable : aucune facture ni aucun avoir ne peut être supprimé, une facture remplacée et ses lignes ne peuvent plus être modifiées, une version archivée (`facture_client_version`) ne peut être ni modifiée ni supprimée (déclencheurs `trg_historique_facture*`) |
| `044_reception_detaillee.sql` | Réception détaillée comme la facture du grossiste : `reception` (montant HT avant remise, remise, TVA, timbre, total SHP ; `montant_ttc` = net à payer) et `reception_ligne` (prix HT avant remise, remise %, prix net = coût du stock, PPA facturé, SHP par boîte, TVA) |
| `045_prix_lot_ug_categories.sql` | Prix de vente par lot (`lot.ppa`, prix de la vignette) et SHP à la caisse (`ligne_vente.shp`, compris dans le prix) ; liste I/II d'un psychotrope pour son SHP (`produit.liste_psychotrope`) ; reste à charge saisi en tiers payant (`vente.reste_saisi`) ; unités gratuites à la réception (`reception_ligne.ug`, coût `zero` ou `reparti`, `prix_catalogue_remplace`) ; catégories de produits (`categorie_produit`, `produit.categorie`) pour les totaux médicaments / autres produits ; plafond de crédit proposé (`parametre_caisse.plafond_credit_defaut`) ; délai de paiement des organismes et des clients (`delai_paiement_jours`) |
| `046_blocage_produit.sql` | Blocage d'un produit à l'achat (`produit.bloque_achat` : ni commande, ni réception, ni suggestion) et/ou à la vente (`produit.bloque_vente` : refusé en caisse), avec son motif ; droit `produits.bloquer` (titulaire et assistant) |
| `047_recette_corrections.sql` | Corrections de la recette : numéros suivis des reçus de crédit (`paiement.numero_recu`, RC-000001) et des règlements aux grossistes (`reglement_fournisseur.numero`, RG-000001) ; alerte au-delà de N annulations par session (`parametre_caisse.seuil_annulations`) ; autorisation sur place par code comptoir (`parametre_caisse.autorisation_par_code`) ; grossiste désactivable (`fournisseur.actif`) |
| `048_reprise_corrections.sql` | Reprise de données : classe réglementaire facultative de l'ancien logiciel (`import.produit_src.classe`) ; classe des produits créés = la plus stricte entre la nomenclature et l'ancien logiciel, produit absent de la nomenclature créé « à vérifier » ; prix d'achat par lot (`import.stock_src.prix_achat`) et contrôle de la valeur du stock ; lots de même n° et de dates différentes gardés séparés ; grossiste fictif seulement si nécessaire. |
| `049_vente_sans_ordonnance.sql` | Droit « Vendre sans ordonnance un médicament de liste I ou II » (`caisse.vendre_sans_ordonnance`) : titulaire et assistant par défaut, réglable dans la matrice des droits ; sans lui, la caisse demande le médecin prescripteur ou l'autorisation par code. Psychotropes et stupéfiants : ordonnance toujours obligatoire. |
| `050_ged.sql` | GED et sauvegardes : durée de conservation de la copie de la base dans la GED (`parametre_sauvegarde.retention_ged_jours`, 7 jours par défaut) et nettoyage des documents de la GED au-delà d'un nombre d'années (`nettoyage_ged_annees`, vide : jamais). |
| `051_rappel_lot_deja_bloque.sql` | Rappel de lot : `ventes_lot(produit, lot)` liste les ventes du lot (consultation d'un lot déjà rappelé) ; `rappel_lot` ne bloque plus que les lots encore vendables de ce numéro (motif du premier blocage gardé, pas de second événement de blocage). |
| `052_reprise_super_admin.sql` | Reprise de données par le super admin : `import.session.lance_par` (extraction), `charge_le` et `charge_par` (chargement définitif). Les données chargées restent attribuées au titulaire ; la validation par code est inscrite au journal. |
| `053_lot_delaisse.sql` | Ancien lot délaissé : `ligne_vente.raison_lot` (lot plus récent servi : péremption vérifiée, introuvable, abîmée, demande du client), `situation_lot(lot)`, `lots_delaisses(produit)` et `alerter_lots_delaisses(produits)` ; seuils `parametre_alerte.seuil_lot_delaisse` (un vendeur, 5) et `seuil_lot_delaisse_collectif` (plusieurs vendeurs, 10, alerte rouge) ; alerte et notification `lot_delaisse` réservées au titulaire, ajoutées à `generer_alertes` ; `inventaire.produit_id` (inventaire d'un produit). |
| `054_surveillance_caisse.sql` | Contrôle interne de la caisse (titulaire seul) : `evenement_caisse` (ligne retirée du panier, tiroir ouvert sans vente, vente en attente abandonnée), `parametre_surveillance` (seuils par employé et par jour, horaires d’ouverture, vides : pas de contrôle), `alerte_surveillance` (anomalies par employé et par jour, ou par patient pour les remises répétées ; closes par le titulaire, jamais supprimées), `surveillance_candidats(jour)` et `surveiller(jour)` ; notification `surveillance`. |
| `055_surveillance_stock.sql` | Contrôle interne du stock : boîte vue à l’inventaire (`boite_evenement` type `inventaire`, `inventaire_id`, une seule fois par inventaire), boîtes fantômes, `produit.sensible` et comptage « Produits sensibles » (`jours_comptage_sensibles`, 7), échantillons et gratuités (mouvement `echantillon`, boîte `donnee`), seuils `seuil_ajustements` (5), `seuil_casses` (3), `seuil_echantillons` (3) ; `surveillance_candidats` remplacée (anomalies ajustements, casses, echantillons, boites_fantomes, sensibles_en_retard). |
| `056_droit_don.sql` | Droit `stock.donner` (« Donner un échantillon ou une gratuité (don à un nécessiteux) »), titulaire et assistant par défaut, à donner à un vendeur dans la matrice des droits ; posé à la première exécution seulement. |
| `057_reception_aveugle.sql` | Réception à l’aveugle : `comptage_reception` et `comptage_reception_ligne` (boîtes comptées sans voir le BL, facture en écart gardée en `saisie` sans code comptoir), réglages `reception_aveugle` (actif) et `jours_avoir_retour` (30), droit `achats.valider_ecart` (titulaire, assistant), boîte `manquante` et mouvement `manquant_reception` (écart en moins au registre des psychotropes), fonction `surveillance_candidats_achats` (anomalies ecart_reception, reception_meme_personne, retour_sans_avoir, avoir_incomplet) et `surveiller` remplacée. |
| `058_surveillance_comptes.sql` | Contrôle interne des comptes : `patient.cree_par`, table `connexion_employe` (poste et méthode de chaque connexion), réglages des crédits suspects (5 000 DA pour un patient de moins de 7 jours, 20 000 DA de crédits par employé et par jour, 60 jours sans remboursement), des autorisations par code (10 données, 5 demandées par jour), des postes simultanés (3 passages en moins de 10 minutes) et des baisses de prix (1) ; fonction `surveillance_candidats_comptes` et `surveiller` remplacée. |
| `059_corrections_surveillance.sql` | Corrections de la surveillance : boîtes fantômes seulement pour un lot compté uniquement par scans de boîtes identifiées, remises au même patient sans seconde anomalie à cheval sur deux mois, horaires passant minuit, anomalie traitée redevenue urgente remise « à examiner » (`surveillance_candidats` et `surveiller` remplacées). |
| `060_volet_chifa.sql` | Reprise d'une vente Chifa par le QR code du volet : table `chifa_code_produit` (code produit Chifa associé une fois à un produit), `vente.chifa_facture`, `chifa_date`, `chifa_qr` (volet repris, contenu brut), index unique `ux_vente_chifa_facture` (un volet repris une seule fois, sauf vente annulée). Format du QR à confirmer avec l'éditeur de Chifa. |

### Règle permanente : toute migration est répliquée dans `init/`

**Toute modification de la base passe par un script numéroté idempotent, et la même modification met à jour les scripts
d'installation :**

1. **Structure** (table, colonne, contrainte, index, vue, fonction, déclencheur) : `init/schema.sql` est régénéré à partir
   d'une base où tous les scripts ont été rejoués dans l'ordre, et son en-tête cite le dernier script couvert (« 001 à NNN »).
2. **Référentiel** (rôles, permissions et droits par défaut, paramètres, organismes de départ, substances surveillées, comptes
   de trésorerie de départ, et toute donnée qu'une pharmacie neuve doit trouver) : `init/seed_referentiel.sql` reçoit les
   mêmes valeurs, réexécutables (`ON CONFLICT`, `NOT EXISTS`), et son en-tête cite le dernier script couvert.
3. Ne vont **pas** dans `init/` : les données de démonstration (`data/9xx`) et les migrations ponctuelles de données existantes
   (reprise d'un ancien solde, recalcul d'une colonne) : une base neuve n'a rien à migrer.

Contrôle avant de livrer : `init/schema.sql` identique (hors en-tête) à un `pg_dump --schema-only` des bases rejouées, et
référentiel des bases (permissions, rôles, droits de départ, substances, organismes de départ, coffre) identique au seed — un
réglage fait ensuite dans l'application (ex. remise maximale changée par le titulaire) n'est pas un écart.

Contrôle automatique : `tests/Pharma.Architecture.Tests/ScriptsInitTests.cs` échoue si les en-têtes de `schema.sql` et du seed ne citent
pas le dernier script, si une table, une vue, une fonction ou une colonne créée par un script manque dans `schema.sql`, ou si
une permission du logiciel (`Pharma.Domain.Securite.Permissions`) manque dans un script numéroté ou dans le seed.

Pour une nouvelle évolution : ajouter un script `0NN_...sql` idempotent, et dans la même modification régénérer `init/schema.sql` (`pg_dump --schema-only --no-owner --no-privileges --no-comments` d'une base où tous les scripts ont été rejoués dans l'ordre) et compléter `init/seed_referentiel.sql` s'il ajoute des données de référence. Les scripts se rejouent **tous, dans l'ordre** : rejouer un ancien script seul peut ramener une fonction ou une vue à une version antérieure. Dans une vue, nommer les colonnes une à une (pas de `table.*`) et ne jamais ajouter de colonne à une vue définie par un script antérieur : le rejeu de l'ancien script échouerait. **Une contrainte CHECK à liste de valeurs** (types, statuts) ne se recrée jamais sans garde : la recréer seulement si sa définition n'a pas encore la valeur apportée par le script (`DO $garde$ … IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = … AND pg_get_constraintdef(oid) LIKE '%''valeur''%') …`) ; sinon le rejeu complet (restauration d'une sauvegarde) échoue dès que la base contient une valeur ajoutée par un script plus récent.

### Deux usages distincts : création à la mise en production, migration d'une base existante

| Dossier | Quand | Ce qu'on lance |
| --- | --- | --- |
| `database/init/` | **Mise en production (MEP)** : création d'une base **neuve et vide** | `schema.sql` (toute la structure en une fois), puis `seed_referentiel.sql` (référentiel de départ) ; `clean.sql` seulement pour remettre une base à vierge |
| `database/migration/` | **Migration** : mise à jour d'une base **déjà en service** (nouvelle version du logiciel, sauvegarde ancienne restaurée) | Les scripts numérotés `0NN_*.sql` **plus récents que la base**, dans l'ordre (tous sont idempotents) |

On ne mélange pas les deux : une base neuve se crée avec `init/`, pas en rejouant les 0NN un à un ; une base en service ne
reçoit jamais `schema.sql` (il s'arrête de lui-même si le schéma existe) ni `clean.sql` (il efface tout).
Les outils qui rejouent les migrations (`scripts/initialiser_base_locale.ps1` pour les bases de développement, l'installateur
du paquet d'essai, la restauration d'une sauvegarde dans l'API) ne prennent que les fichiers `0NN_*.sql` de `database/migration/`.
L'API emporte ces scripts numérotés dans le sous-dossier `database` de son dossier d'installation.

### Création à la mise en production : `database/init/`

| Fichier | Rôle |
| --- | --- |
| `init/schema.sql` | Toute la structure en un fichier (équivaut aux scripts 001 à 070, régénéré à chaque nouveau script) ; base vide uniquement |
| `init/seed_referentiel.sql` | Référentiel de départ pour la mise en production : rôles, droits, paramètres, CNAS et CASNOS, substances surveillées, coffre, compte `titulaire` au mot de passe provisoire `Provisoire-2026` (changement obligatoire) |
| `init/clean.sql` | Remise à vierge (tout est supprimé) ; exige `-v confirmer=<nom de la base>` |

Procédure complète : [mise-en-production.md](mise-en-production.md)., ne jamais modifier le sens d’un script déjà appliqué en production.

## Scripts idempotents

Chaque script peut être rejoué sans erreur et sans effet sur une base déjà à jour. C’est ce qui permet de mettre à jour une base existante en rejouant tous les scripts dans l’ordre.

| Élément | Écriture |
| --- | --- |
| Table, index, séquence, schéma, extension | `CREATE ... IF NOT EXISTS` |
| Colonne | `ALTER TABLE ... ADD COLUMN IF NOT EXISTS` |
| Fonction, vue de même forme, déclencheur | `CREATE OR REPLACE` |
| Vue dont les colonnes changent d’un script à l’autre | `DROP VIEW IF EXISTS` puis `CREATE VIEW` |
| Déclencheur de contrainte (`CREATE CONSTRAINT TRIGGER`) | `DROP TRIGGER IF EXISTS` puis création (`OR REPLACE` n’existe pas pour eux) |
| Contrainte ajoutée à une table existante | bloc `DO` qui vérifie `pg_constraint` avant `ADD CONSTRAINT` |
| Données de référence (organismes, rôles) | `ON CONFLICT DO NOTHING` |
| Catalogue des permissions (fixé par le logiciel) | `ON CONFLICT DO UPDATE` des libellés |
| Paramètres à une ligne (`parametre_*`) | `INSERT ... ON CONFLICT (id) DO NOTHING` : les réglages de la pharmacie sont conservés |
| Droits par défaut des rôles | posés seulement si le rôle n’a encore aucun droit : les choix du titulaire ne sont jamais écrasés |
| Migration de données (ancienne colonne `utilisateur.role`) | bloc `DO` exécuté seulement si l’ancienne colonne existe encore |
| Données fictives (`data/`) | le script s’arrête si elles sont déjà présentes (`\gset` et `\if` de psql) |

Contrôle effectué : sur une base neuve, deux passes complètes (001 à 009 puis démo) donnent une base identique ; sur une base existante, la réexécution ne change ni les lignes, ni les droits, ni les paramètres, ni les contraintes, déclencheurs, index et vues.

## Base locale de développement

```powershell
.\scripts\initialiser_base_locale.ps1            # crée les bases manquantes, met à jour les autres
.\scripts\initialiser_base_locale.ps1 -MiseAJour # met à jour sans le mot de passe postgres
.\scripts\initialiser_base_locale.ps1 -Recreer   # repart de bases vides
```

Le script crée le rôle `pharma_app` et deux bases, `pharmacie_dev` (pour lancer l’API) et `pharmacie_test` (pour les tests d’intégration), applique tous les scripts puis les données fictives. Options : `-PgBin` (dossier de `psql`, par défaut la dernière version installée), `-SansDemo`, `-Bases`.

Les données fictives reproduisent les cas des maquettes : lot d’amoxicilline périmé, insuline proche de la péremption, oméprazole à risque d’écoulement, metformine en rupture avec une alternative, amlodipine en stock critique, vitamine D3 dormante, alprazolam sous registre. Les dates sont calculées à partir du jour d’exécution.

## Conventions

- Noms en minuscules avec soulignés (`ligne_vente`, `date_peremption`). Le `PharmaDbContext` convertit automatiquement les noms C# (`LigneVente`, `DatePeremption`).
- Clés `bigint GENERATED ALWAYS AS IDENTITY`.
- Montants `numeric(12,2)` en dinars ; dates d’horodatage `timestamptz`.
- Les valeurs fermées (statuts, types) sont des `text` avec `CHECK`, en minuscules.
- Tables d’historique non modifiables : `registre_psychotrope`, `journal_audit`, `boite_evenement`, et les sessions de caisse une fois clôturées.

## Sauvegarde

`scripts/sauvegarde_pharmacie.ps1` : `pg_dump` chaque soir, vérification de lisibilité, copie sur disque externe, 30 jours d’historique. Détails dans [mise-en-production.md](mise-en-production.md).

L'API sait aussi sauvegarder elle-même, avec alerte SMS et e-mail au titulaire en cas d'échec ou de disque absent : voir [notifications.md](notifications.md). Choisir l'un ou l'autre.
