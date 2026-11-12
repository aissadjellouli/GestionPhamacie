-- =====================================================================
-- Aucune écriture d'argent sur une caisse déjà clôturée
--   * Une vente, un paiement (vente ou règlement de dette), une annulation,
--     un retour client, la part prise dans le tiroir pour un grossiste et
--     l'argent porté du tiroir au coffre pendant la journée sont refusés si
--     leur caisse est clôturée.
--   * La clôture verrouille sa caisse (FOR UPDATE) : une écriture lancée sur
--     un autre poste pendant le comptage attend la fin de la clôture, puis
--     est refusée ; elle ne tombe plus dans une caisse fermée, hors de tout
--     attendu (écart inexpliqué).
-- Idempotent. A exécuter après 063_revocation_connexions.sql.
-- =====================================================================

SET client_encoding = 'UTF8';

CREATE OR REPLACE FUNCTION verifier_caisse_ouverte()
RETURNS trigger LANGUAGE plpgsql AS $$
DECLARE
    v_fermee timestamptz;
BEGIN
    IF NEW.session_caisse_id IS NULL THEN
        RETURN NEW;
    END IF;
    -- Seulement l'argent du tiroir : part du tiroir d'un règlement grossiste, versement au coffre pendant la journée.
    -- Champs propres à une table : lus seulement pour cette table (IF imbriqués), sinon l'expression échoue sur les autres
    IF TG_TABLE_NAME = 'reglement_fournisseur' THEN
        IF NEW.part_tiroir = 0 THEN
            RETURN NEW;
        END IF;
    ELSIF TG_TABLE_NAME = 'mouvement_tresorerie' THEN
        IF NOT NEW.pendant_session THEN
            RETURN NEW;
        END IF;
    END IF;
    SELECT fermee_le INTO v_fermee FROM session_caisse WHERE id = NEW.session_caisse_id FOR SHARE;
    IF v_fermee IS NOT NULL THEN
        RAISE EXCEPTION 'Cette caisse vient d''être clôturée : ouvrez la caisse du poste et recommencez.'
            USING ERRCODE = 'P0001';
    END IF;
    RETURN NEW;
END;
$$;

DROP TRIGGER IF EXISTS trg_caisse_ouverte ON vente;
CREATE TRIGGER trg_caisse_ouverte BEFORE INSERT ON vente FOR EACH ROW EXECUTE FUNCTION verifier_caisse_ouverte();
DROP TRIGGER IF EXISTS trg_caisse_ouverte ON paiement;
CREATE TRIGGER trg_caisse_ouverte BEFORE INSERT ON paiement FOR EACH ROW EXECUTE FUNCTION verifier_caisse_ouverte();
DROP TRIGGER IF EXISTS trg_caisse_ouverte ON annulation_vente;
CREATE TRIGGER trg_caisse_ouverte BEFORE INSERT ON annulation_vente FOR EACH ROW EXECUTE FUNCTION verifier_caisse_ouverte();
DROP TRIGGER IF EXISTS trg_caisse_ouverte ON retour_client;
CREATE TRIGGER trg_caisse_ouverte BEFORE INSERT ON retour_client FOR EACH ROW EXECUTE FUNCTION verifier_caisse_ouverte();
DROP TRIGGER IF EXISTS trg_caisse_ouverte ON reglement_fournisseur;
CREATE TRIGGER trg_caisse_ouverte BEFORE INSERT ON reglement_fournisseur FOR EACH ROW EXECUTE FUNCTION verifier_caisse_ouverte();
DROP TRIGGER IF EXISTS trg_caisse_ouverte ON mouvement_tresorerie;
CREATE TRIGGER trg_caisse_ouverte BEFORE INSERT ON mouvement_tresorerie FOR EACH ROW EXECUTE FUNCTION verifier_caisse_ouverte();
