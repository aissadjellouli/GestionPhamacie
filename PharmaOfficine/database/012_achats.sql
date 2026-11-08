-- =====================================================================
-- Achats : numéros de commande et de retour, suivi des retours,
-- recherche au scan étendue aux étiquettes par boîte.
-- A exécuter après 011_ordonnance_scannee.sql. Idempotent.
-- =====================================================================

-- ---------------------------------------------------------------------
-- 1. Numéros : C-000001 (commandes), R-000001 (retours fournisseurs)
-- ---------------------------------------------------------------------
CREATE SEQUENCE IF NOT EXISTS seq_numero_commande START 1;
CREATE SEQUENCE IF NOT EXISTS seq_numero_retour START 1;

CREATE OR REPLACE FUNCTION prochain_numero_commande() RETURNS text
LANGUAGE sql AS $$ SELECT 'C-' || lpad(nextval('seq_numero_commande')::text, 6, '0'); $$;

CREATE OR REPLACE FUNCTION prochain_numero_retour() RETURNS text
LANGUAGE sql AS $$ SELECT 'R-' || lpad(nextval('seq_numero_retour')::text, 6, '0'); $$;

-- ---------------------------------------------------------------------
-- 2. Retours fournisseurs : dates d'envoi et d'avoir, avoir attendu
-- ---------------------------------------------------------------------
ALTER TABLE retour_fournisseur ADD COLUMN IF NOT EXISTS envoye_le           timestamptz;
ALTER TABLE retour_fournisseur ADD COLUMN IF NOT EXISTS avoir_recu_le       timestamptz;
ALTER TABLE retour_fournisseur ADD COLUMN IF NOT EXISTS montant_attendu     numeric(12,2) CHECK (montant_attendu >= 0);

-- Réception : montant TTC ajouté à la dette du grossiste (traçabilité de fournisseur.solde)
ALTER TABLE reception ADD COLUMN IF NOT EXISTS montant_ttc numeric(12,2) CHECK (montant_ttc >= 0);

-- ---------------------------------------------------------------------
-- 3. Recherche au scan : étiquette par boîte (007), puis code du lot (004),
--    puis code du fabricant (lot FEFO). Remplace la version de 004.
-- ---------------------------------------------------------------------
CREATE OR REPLACE FUNCTION rechercher_par_code(p_code text)
RETURNS TABLE (produit_id bigint, lot_id bigint, source text)
LANGUAGE plpgsql STABLE AS $$
BEGIN
    RETURN QUERY
        SELECT l.produit_id, l.id, 'etiquette_boite'::text
        FROM boite b JOIN lot l ON l.id = b.lot_id
        WHERE b.code_etiquette = p_code;
    IF FOUND THEN RETURN; END IF;

    RETURN QUERY
        SELECT l.produit_id, l.id, 'code_interne'::text
        FROM lot l
        WHERE l.code_interne = p_code;     -- même périmé : la caisse affichera
    IF FOUND THEN RETURN; END IF;          -- le blocage plutôt que « introuvable »

    RETURN QUERY
        SELECT p.id, lot_fefo(p.id), 'code_fabricant'::text
        FROM produit p
        WHERE p.code_barre = p_code AND p.actif;
END;
$$;
