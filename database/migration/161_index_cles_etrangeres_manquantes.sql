-- =====================================================================
-- 161 -- Index des clés étrangères restées sans index de tête (revue de la base du 2026-11-23)
--   Sans index sur la colonne référencée, chaque lecture « lignes d'une commande / d'un retour / d'une session… » parcourt toute la
--   table, et chaque suppression ou changement de clé dans la table parente parcourt la table fille entière (contrôle de la clé
--   étrangère). Vérifié avant : aucun index (ni clé primaire, ni contrainte unique) ne commence par ces colonnes.
--   1. ix_ligne_commande_commande            : ligne_commande (commande_id)
--   2. ix_produit_dci                        : produit (dci_id)           (substitution, alternatives : produits d'une même DCI)
--   3. ix_facture_client_patient             : facture_client (patient_id)
--   4. ix_commande_fournisseur_fournisseur   : commande_fournisseur (fournisseur_id)
--   5. ix_journal_vente_session_caisse       : journal_vente (session_caisse_id)
--   6. ix_evenement_caisse_session           : evenement_caisse (session_caisse_id)
--   7. ix_ligne_retour_fournisseur_retour    : ligne_retour_fournisseur (retour_id)
--   8. ix_sync_ordre_recu_message_ferme_par  : sync_ordre_recu (message_ferme_par), partiel (colonne vide sauf message fermé, script 160)
--   Index ordinaires (pas CONCURRENTLY) : le script se rejoue dans la transaction des scripts 034 et suivants (docs/scripts-base-de-donnees.md,
--   3.3) ; la création bloque les écritures de chaque table le temps de la lire (journal_vente et ligne_commande : à lancer API arrêtée,
--   comme toute mise à jour). Retour arrière : DROP INDEX IF EXISTS pour chacun des huit index. Aucune donnée modifiée.
-- Idempotent et rejouable sur une base plus récente.
-- A exécuter après 160_message_central_postes.sql.
-- =====================================================================

-- Fichier en UTF-8 : lu comme tel quel que soit l'encodage du poste qui lance psql.
SET client_encoding = 'UTF8';

CREATE INDEX IF NOT EXISTS ix_ligne_commande_commande
    ON ligne_commande (commande_id);

CREATE INDEX IF NOT EXISTS ix_produit_dci
    ON produit (dci_id);

CREATE INDEX IF NOT EXISTS ix_facture_client_patient
    ON facture_client (patient_id);

CREATE INDEX IF NOT EXISTS ix_commande_fournisseur_fournisseur
    ON commande_fournisseur (fournisseur_id);

CREATE INDEX IF NOT EXISTS ix_journal_vente_session_caisse
    ON journal_vente (session_caisse_id);

CREATE INDEX IF NOT EXISTS ix_evenement_caisse_session
    ON evenement_caisse (session_caisse_id);

CREATE INDEX IF NOT EXISTS ix_ligne_retour_fournisseur_retour
    ON ligne_retour_fournisseur (retour_id);

CREATE INDEX IF NOT EXISTS ix_sync_ordre_recu_message_ferme_par
    ON sync_ordre_recu (message_ferme_par) WHERE message_ferme_par IS NOT NULL;
