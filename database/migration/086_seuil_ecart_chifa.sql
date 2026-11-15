-- =====================================================================
-- Écart de prix d'un volet Chifa (2026-11-15)
--   * parametre_caisse.seuil_ecart_chifa : écart par boîte (DA) sous
--     lequel une ligne du volet est alignée d'office sur le prix Chifa
--     (5 DA par défaut, réglable dans Administration › Caisse) ;
--   * ligne_vente.prix_chifa : prix unitaire lu sur le volet Chifa
--     (NULL : ligne hors volet) ; la part de l'organisme est calculée
--     sur ce prix ;
--   * ligne_vente.choix_prix_chifa : prix retenu quand il diffère du
--     nôtre : aligne_auto (sous le seuil), aligne (s'aligner sur Chifa),
--     garde (garder notre prix, écart payé par le patient),
--     plus_eleve (vendeur sans le droit prix.modifier : le plus élevé
--     des deux d'office).
-- Le catalogue n'est jamais modifié. Aucune donnée supprimée.
-- Idempotent. A exécuter après 085_droit_cloture_caisse.sql.
-- =====================================================================

ALTER TABLE parametre_caisse ADD COLUMN IF NOT EXISTS seuil_ecart_chifa numeric(12,2) NOT NULL DEFAULT 5;

DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'ck_seuil_ecart_chifa') THEN
        ALTER TABLE parametre_caisse ADD CONSTRAINT ck_seuil_ecart_chifa CHECK (seuil_ecart_chifa >= 0);
    END IF;
END $$;

COMMENT ON COLUMN parametre_caisse.seuil_ecart_chifa IS 'Écart de prix par boîte (DA) sous lequel une ligne d''un volet Chifa est alignée d''office sur le prix Chifa';

ALTER TABLE ligne_vente ADD COLUMN IF NOT EXISTS prix_chifa numeric(12,2);
ALTER TABLE ligne_vente ADD COLUMN IF NOT EXISTS choix_prix_chifa text;

DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'ck_ligne_vente_prix_chifa') THEN
        ALTER TABLE ligne_vente ADD CONSTRAINT ck_ligne_vente_prix_chifa CHECK (prix_chifa IS NULL OR prix_chifa >= 0);
    END IF;
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'ck_ligne_vente_choix_prix_chifa') THEN
        ALTER TABLE ligne_vente ADD CONSTRAINT ck_ligne_vente_choix_prix_chifa CHECK (
            choix_prix_chifa IS NULL
            OR (prix_chifa IS NOT NULL AND choix_prix_chifa IN ('aligne_auto', 'aligne', 'garde', 'plus_eleve')));
    END IF;
END $$;

COMMENT ON COLUMN ligne_vente.prix_chifa IS 'Prix unitaire lu sur le volet Chifa (NULL : ligne hors volet)';
COMMENT ON COLUMN ligne_vente.choix_prix_chifa IS 'Prix retenu quand il diffère du prix Chifa : aligne_auto, aligne, garde ou plus_eleve';
