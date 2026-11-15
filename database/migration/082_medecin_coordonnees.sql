-- =====================================================================
-- Coordonnées d'un médecin prescripteur
--   * medecin.telephone, medecin.adresse, medecin.email,
--     medecin.numero_ordre (n° d'inscription au tableau de l'Ordre des
--     médecins) : nouveaux ; medecin.code_prestataire (code CNAS / Chifa,
--     colonne existante depuis 001) devient saisissable. Tous
--     facultatifs, saisis à la création du médecin à la caisse ou corrigés
--     dans Administration › Langue et pharmacie ›
--     Médecins prescripteurs. Le n° d'ordre et le code CNAS ne
--     sont pas uniques et leur format n'est pas imposé (aucune règle
--     demandée). Formats contrôlés par l'application
--     (téléphone, e-mail comme pour les patients ; adresse 300 caractères,
--     n° d'ordre et code CNAS 30 caractères au plus) ; bornes de longueur aussi en base.
-- Idempotent. A exécuter après 081_conditions_pret_assurance.sql.
-- =====================================================================

ALTER TABLE medecin ADD COLUMN IF NOT EXISTS telephone text;
ALTER TABLE medecin ADD COLUMN IF NOT EXISTS adresse text;
ALTER TABLE medecin ADD COLUMN IF NOT EXISTS email text;
ALTER TABLE medecin ADD COLUMN IF NOT EXISTS numero_ordre text;

DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'ck_medecin_coordonnees' AND conrelid = 'medecin'::regclass) THEN
        ALTER TABLE medecin ADD CONSTRAINT ck_medecin_coordonnees CHECK (
            (telephone IS NULL OR length(telephone) <= 30)
            AND (adresse IS NULL OR length(adresse) <= 300)
            AND (email IS NULL OR length(email) <= 120)
            AND (numero_ordre IS NULL OR length(numero_ordre) <= 30)
            AND (code_prestataire IS NULL OR length(code_prestataire) <= 30));
    END IF;
END $$;
