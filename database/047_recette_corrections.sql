-- =====================================================================
-- Corrections issues de la recette du 11/11/2026
--   * paiement.numero_recu : numéro suivi des reçus de règlement de crédit
--     (RC-000001…), au lieu de l'identifiant technique ; les reçus déjà
--     émis sont numérotés dans l'ordre de leur enregistrement ;
--   * reglement_fournisseur.numero : numéro suivi des règlements aux
--     grossistes (RG-000001…), même principe ;
--   * parametre_caisse.seuil_annulations : alerte quand une session de
--     caisse dépasse ce nombre d'annulations de vente (0 : pas d'alerte) ;
--   * parametre_caisse.autorisation_par_code : une action refusée faute de
--     droit peut être autorisée sur place par le code comptoir d'un employé
--     qui a ce droit (inscrit au journal : autorise_par) ;
--   * fournisseur.actif : grossiste désactivé (plus proposé aux commandes
--     ni aux réceptions), sans perdre son historique.
-- Idempotent. A exécuter après 046_blocage_produit.sql.
-- =====================================================================

-- ---- Numéros des reçus de crédit
CREATE SEQUENCE IF NOT EXISTS seq_numero_recu;
ALTER TABLE paiement ADD COLUMN IF NOT EXISTS numero_recu text;
COMMENT ON COLUMN paiement.numero_recu IS 'Numéro du reçu de règlement de crédit (RC-000001), null pour un paiement de vente';
DO $$
DECLARE r record;
BEGIN
    FOR r IN SELECT id FROM paiement WHERE vente_id IS NULL AND patient_id IS NOT NULL AND numero_recu IS NULL ORDER BY id LOOP
        UPDATE paiement SET numero_recu = 'RC-' || lpad(nextval('seq_numero_recu')::text, 6, '0') WHERE id = r.id;
    END LOOP;
END $$;
CREATE UNIQUE INDEX IF NOT EXISTS ux_paiement_numero_recu ON paiement (numero_recu) WHERE numero_recu IS NOT NULL;

-- ---- Numéros des règlements aux grossistes
CREATE SEQUENCE IF NOT EXISTS seq_numero_reglement_fournisseur;
ALTER TABLE reglement_fournisseur ADD COLUMN IF NOT EXISTS numero text;
COMMENT ON COLUMN reglement_fournisseur.numero IS 'Numéro du règlement au grossiste (RG-000001)';
DO $$
DECLARE r record;
BEGIN
    FOR r IN SELECT id FROM reglement_fournisseur WHERE numero IS NULL ORDER BY id LOOP
        UPDATE reglement_fournisseur SET numero = 'RG-' || lpad(nextval('seq_numero_reglement_fournisseur')::text, 6, '0') WHERE id = r.id;
    END LOOP;
END $$;
CREATE UNIQUE INDEX IF NOT EXISTS ux_reglement_fournisseur_numero ON reglement_fournisseur (numero) WHERE numero IS NOT NULL;

-- ---- Caisse : alerte sur les annulations, autorisation par code
ALTER TABLE parametre_caisse ADD COLUMN IF NOT EXISTS seuil_annulations int NOT NULL DEFAULT 0;
ALTER TABLE parametre_caisse ADD COLUMN IF NOT EXISTS autorisation_par_code boolean NOT NULL DEFAULT true;
DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'ck_seuil_annulations') THEN
        ALTER TABLE parametre_caisse ADD CONSTRAINT ck_seuil_annulations CHECK (seuil_annulations BETWEEN 0 AND 100);
    END IF;
END $$;
COMMENT ON COLUMN parametre_caisse.seuil_annulations IS 'Alerte au-delà de ce nombre d''annulations de vente par session (0 : pas d''alerte)';
COMMENT ON COLUMN parametre_caisse.autorisation_par_code IS 'Une action refusée faute de droit peut être autorisée par le code comptoir d''un employé qui a ce droit';

-- ---- Grossiste actif
ALTER TABLE fournisseur ADD COLUMN IF NOT EXISTS actif boolean NOT NULL DEFAULT true;
COMMENT ON COLUMN fournisseur.actif IS 'Faux : grossiste désactivé, plus proposé aux commandes ni aux réceptions (historique conservé)';
