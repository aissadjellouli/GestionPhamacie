-- =====================================================================
-- Caisse : numéros de ticket, sessions de caisse, clôture en comptage
-- aveugle, paramètres de caisse, remise.
-- A exécuter après 008_reprise_donnees.sql
-- =====================================================================

-- ---------------------------------------------------------------------
-- 1. Numéro de ticket : T-000001, T-000002... (jamais remis à zéro)
-- ---------------------------------------------------------------------
CREATE SEQUENCE IF NOT EXISTS seq_numero_vente START 1;

CREATE OR REPLACE FUNCTION prochain_numero_vente() RETURNS text
LANGUAGE sql AS $$
    SELECT 'T-' || lpad(nextval('seq_numero_vente')::text, 6, '0');
$$;

-- ---------------------------------------------------------------------
-- 2. Paramètres de caisse (une seule ligne, réglés par le titulaire)
-- ---------------------------------------------------------------------
CREATE TABLE IF NOT EXISTS parametre_caisse (
    id                      int PRIMARY KEY DEFAULT 1 CHECK (id = 1),
    comptage_aveugle        boolean       NOT NULL DEFAULT true,   -- attendu masqué avant le comptage
    fond_caisse_defaut      numeric(12,2) NOT NULL DEFAULT 5000 CHECK (fond_caisse_defaut >= 0),
    seuil_ecart             numeric(12,2) NOT NULL DEFAULT 500  CHECK (seuil_ecart >= 0),
    deconnexion_minutes     int           NOT NULL DEFAULT 0    CHECK (deconnexion_minutes >= 0)  -- 0 = jamais
);
INSERT INTO parametre_caisse DEFAULT VALUES ON CONFLICT (id) DO NOTHING;

-- ---------------------------------------------------------------------
-- 3. Sessions de caisse : une seule session ouverte par employé
-- ---------------------------------------------------------------------
CREATE TABLE IF NOT EXISTS session_caisse (
    id                  bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    utilisateur_id      bigint        NOT NULL REFERENCES utilisateur(id),
    poste               text          NOT NULL,
    ouverte_le          timestamptz   NOT NULL DEFAULT now(),
    fond_caisse         numeric(12,2) NOT NULL CHECK (fond_caisse >= 0),
    fermee_le           timestamptz,
    ventes_especes      numeric(12,2),
    montant_attendu     numeric(12,2),
    montant_compte      numeric(12,2),
    ecart               numeric(12,2),
    CHECK ((fermee_le IS NULL) = (montant_compte IS NULL))
);
CREATE UNIQUE INDEX IF NOT EXISTS ux_session_ouverte ON session_caisse (utilisateur_id) WHERE fermee_le IS NULL;
CREATE INDEX IF NOT EXISTS idx_session_caisse_date ON session_caisse (ouverte_le);

-- Une session clôturée ne se modifie plus
CREATE OR REPLACE FUNCTION proteger_session_fermee() RETURNS trigger
LANGUAGE plpgsql AS $$
BEGIN
    IF OLD.fermee_le IS NOT NULL THEN
        RAISE EXCEPTION 'Session de caisse déjà clôturée : elle ne peut plus être modifiée';
    END IF;
    RETURN NEW;
END;
$$;

CREATE OR REPLACE TRIGGER trg_session_fermee
    BEFORE UPDATE OR DELETE ON session_caisse
    FOR EACH ROW EXECUTE FUNCTION proteger_session_fermee();

-- Détail du comptage par coupure (billets de 2000, 1000, 500, 200 ; pièces de 100 ; autres pièces)
CREATE TABLE IF NOT EXISTS comptage_caisse (
    session_id  bigint        NOT NULL REFERENCES session_caisse(id),
    coupure     text          NOT NULL CHECK (coupure IN ('b2000', 'b1000', 'b500', 'b200', 'p100', 'autres')),
    nombre      int           NOT NULL CHECK (nombre >= 0),   -- pour « autres » : 1
    montant     numeric(12,2) NOT NULL CHECK (montant >= 0),
    PRIMARY KEY (session_id, coupure)
);

-- ---------------------------------------------------------------------
-- 4. Ventes et paiements rattachés à la session ; remise
-- ---------------------------------------------------------------------
ALTER TABLE vente    ADD COLUMN IF NOT EXISTS session_caisse_id bigint REFERENCES session_caisse(id);
ALTER TABLE paiement ADD COLUMN IF NOT EXISTS session_caisse_id bigint REFERENCES session_caisse(id);
CREATE INDEX IF NOT EXISTS idx_vente_session    ON vente (session_caisse_id);
CREATE INDEX IF NOT EXISTS idx_paiement_session ON paiement (session_caisse_id);

-- Remise accordée sur la part du patient (plafonnée par role.remise_max_pct côté application)
ALTER TABLE vente ADD COLUMN IF NOT EXISTS remise_pct    numeric(5,2)  NOT NULL DEFAULT 0 CHECK (remise_pct BETWEEN 0 AND 100);
ALTER TABLE vente ADD COLUMN IF NOT EXISTS montant_remise numeric(12,2) NOT NULL DEFAULT 0 CHECK (montant_remise >= 0);

-- Taux de tiers payant choisi au comptoir (80 ou 100 %), pour mémoire
ALTER TABLE vente ADD COLUMN IF NOT EXISTS taux_tiers_payant numeric(5,2) CHECK (taux_tiers_payant BETWEEN 0 AND 100);
