-- =====================================================================
-- Historique des factures clients : rien ne s'efface, rien ne se réécrit
--   * facture_client : aucune facture ni aucun avoir ne peut être supprimé ;
--     une facture « remplacée » ne peut plus être modifiée (elle reste
--     telle qu'elle a été remplacée, consultable et imprimable) ;
--   * facture_client_ligne : les lignes d'une facture remplacée ne peuvent
--     être ni modifiées ni supprimées (une facture rejetée peut être
--     corrigée : ses anciennes lignes partent d'abord dans
--     facture_client_version) ;
--   * facture_client_version : une version archivée ne peut être ni
--     modifiée ni supprimée.
-- Le logiciel n'a jamais fait ces opérations : ce script les rend
-- impossibles aussi en dehors de lui (requête SQL directe).
-- Une remise à zéro complète reste possible avec init/clean.sql, qui
-- supprime le schéma entier.
-- Idempotent. A exécuter après 042_email_tiers.sql.
-- =====================================================================

CREATE OR REPLACE FUNCTION proteger_historique_facture() RETURNS trigger
LANGUAGE plpgsql AS $$
BEGIN
    IF TG_TABLE_NAME = 'facture_client_version' THEN
        RAISE EXCEPTION 'Historique des factures : la version % de la facture % ne peut être ni modifiée ni supprimée.',
            OLD.version, OLD.facture_id USING ERRCODE = 'P0001';
    END IF;
    IF TG_TABLE_NAME = 'facture_client' THEN
        IF TG_OP = 'DELETE' THEN
            RAISE EXCEPTION 'Historique des factures : la facture % ne peut pas être supprimée (corrigez-la, remplacez-la ou émettez un avoir).',
                OLD.numero USING ERRCODE = 'P0001';
        END IF;
        IF OLD.statut = 'remplacee' THEN
            RAISE EXCEPTION 'Historique des factures : la facture % a été remplacée et ne peut plus être modifiée.', OLD.numero USING ERRCODE = 'P0001';
        END IF;
        RETURN NEW;
    END IF;
    -- facture_client_ligne : lignes figées dès que leur facture est remplacée
    IF EXISTS (SELECT 1 FROM facture_client f WHERE f.id = OLD.facture_id AND f.statut = 'remplacee') THEN
        RAISE EXCEPTION 'Historique des factures : les lignes d''une facture remplacée ne peuvent plus être modifiées.' USING ERRCODE = 'P0001';
    END IF;
    RETURN CASE WHEN TG_OP = 'DELETE' THEN OLD ELSE NEW END;
END $$;

DROP TRIGGER IF EXISTS trg_historique_facture_version ON facture_client_version;
CREATE TRIGGER trg_historique_facture_version BEFORE UPDATE OR DELETE ON facture_client_version
    FOR EACH ROW EXECUTE FUNCTION proteger_historique_facture();

DROP TRIGGER IF EXISTS trg_historique_facture ON facture_client;
CREATE TRIGGER trg_historique_facture BEFORE UPDATE OR DELETE ON facture_client
    FOR EACH ROW EXECUTE FUNCTION proteger_historique_facture();

DROP TRIGGER IF EXISTS trg_historique_facture_ligne ON facture_client_ligne;
CREATE TRIGGER trg_historique_facture_ligne BEFORE UPDATE OR DELETE ON facture_client_ligne
    FOR EACH ROW EXECUTE FUNCTION proteger_historique_facture();
