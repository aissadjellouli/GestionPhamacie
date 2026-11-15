-- =====================================================================
-- Patient : rang de l'assuré et date de fin de droits (modèle Wasfa,
-- 2026-11-15, lot 3)
--   rang_assure(num_assure) : les 2 chiffres du suffixe « /NN » du n°
--   d'assuré (« 690755008888/02 » -> « 02 ») ; NULL sans erreur pour un
--   n° vide, sans suffixe ou de format atypique (« AAAAAA-AAAA-9 »).
--   Pas de nouvelle colonne pour le rang. Sens du suffixe = rang de
--   l'ayant droit : à confirmer sur cartes réelles (docs/chifa.md).
--   patient.date_fin_droit : date de fin des droits Chifa, saisie sur la
--   fiche patient ; dépassée, la vente en tiers payant CNAS/CASNOS est
--   seulement signalée (avertissement journalisé, pas de blocage, D-4).
-- Aucune donnée supprimée ni modifiée (colonne ajoutée, vide).
-- Idempotent. A exécuter après 099_remboursement_regularisation.sql.
-- =====================================================================

CREATE OR REPLACE FUNCTION rang_assure(p_num text) RETURNS text
    LANGUAGE sql IMMUTABLE PARALLEL SAFE AS
$$ SELECT substring(p_num FROM '/\s*([0-9]{2})\s*$') $$;
COMMENT ON FUNCTION rang_assure(text) IS 'Rang de l''assuré = 2 chiffres du suffixe « /NN » du n° d''assuré ; NULL si absent ou format atypique (script 102)';

ALTER TABLE patient ADD COLUMN IF NOT EXISTS date_fin_droit date;
COMMENT ON COLUMN patient.date_fin_droit IS 'Fin des droits Chifa (saisie manuelle) ; dépassée : avertissement à la vente CNAS/CASNOS, sans blocage (script 102)';
