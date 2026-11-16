-- =====================================================================
-- Formulaire d'accord SMS des patients (décision du titulaire du 2026-11-16)
--   * patient.formulaire_sms_recu / formulaire_sms_recu_le : formulaire
--     d'accord SMS signé par le patient reçu au comptoir, avec la date
--     (date de réception, ou du retrait de la case).
--   * parametre_formulaire_sms : texte du formulaire réglé par le
--     titulaire (une seule ligne ; texte null = texte par défaut fourni
--     par l'application).
--   Aucune donnée existante supprimée ni modifiée. Idempotent.
--   À exécuter après 116. Si 111b a été appliqué : lancer avec
--   « SET ROLE pharma_proprietaire; ».
-- =====================================================================

ALTER TABLE patient ADD COLUMN IF NOT EXISTS formulaire_sms_recu boolean NOT NULL DEFAULT false;
ALTER TABLE patient ADD COLUMN IF NOT EXISTS formulaire_sms_recu_le timestamptz;

CREATE TABLE IF NOT EXISTS parametre_formulaire_sms (
    id           integer PRIMARY KEY DEFAULT 1 CHECK (id = 1),
    texte        text CHECK (texte IS NULL OR char_length(texte) BETWEEN 1 AND 4000),
    modifie_le   timestamptz NOT NULL DEFAULT now(),
    modifie_par  bigint REFERENCES utilisateur(id)
);
INSERT INTO parametre_formulaire_sms (id) VALUES (1) ON CONFLICT (id) DO NOTHING;
