-- =====================================================================
-- 148 — Alerte « aucune sauvegarde réussie sur le disque externe depuis N jours » (2026-11-18, décision du titulaire,
--   docs/ged-protection-a-decider.md § 5)
--   Le titulaire est prévenu (SMS et e-mail, comme une sauvegarde en échec) quand la dernière sauvegarde réussie sur le disque
--   externe a plus de N jours, même si la sauvegarde automatique est désactivée ; au plus une alerte par période de N jours.
--   1. parametre_sauvegarde.alerte_sans_disque_jours : N, de 1 à 90, 7 par défaut (Administration › Sauvegardes).
--   2. parametre_sauvegarde.suivi_disque_depuis : point de départ du délai tant qu'aucune sauvegarde n'a réussi
--      (date d'exécution du script, ou de création de la base).
--   3. parametre_sauvegarde.derniere_alerte_sans_disque : date de la dernière alerte envoyée (une par période).
--   4. Garde du CHECK (1 à 90) : posé une seule fois, jamais recréé sur une base à jour.
-- Idempotent et rejouable sur une base plus récente ; aucune donnée modifiée (la ligne existante prend 7 jours et la date du jour).
-- A exécuter après 147_synchronisation_associations_produits_montantes.sql.
-- =====================================================================

-- ---- 1 à 3. Colonnes ----
ALTER TABLE parametre_sauvegarde ADD COLUMN IF NOT EXISTS alerte_sans_disque_jours integer NOT NULL DEFAULT 7;
ALTER TABLE parametre_sauvegarde ADD COLUMN IF NOT EXISTS suivi_disque_depuis timestamptz NOT NULL DEFAULT now();
ALTER TABLE parametre_sauvegarde ADD COLUMN IF NOT EXISTS derniere_alerte_sans_disque timestamptz;

-- ---- 4. Garde du CHECK ----
DO $garde$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'ck_sauvegarde_alerte_sans_disque'
                   AND conrelid = 'parametre_sauvegarde'::regclass) THEN
        ALTER TABLE parametre_sauvegarde ADD CONSTRAINT ck_sauvegarde_alerte_sans_disque
            CHECK (alerte_sans_disque_jours BETWEEN 1 AND 90);
    END IF;
END $garde$;

COMMENT ON COLUMN parametre_sauvegarde.alerte_sans_disque_jours IS 'Titulaire prévenu si aucune sauvegarde réussie sur le disque externe depuis N jours (1 à 90, défaut 7) (script 148).';
COMMENT ON COLUMN parametre_sauvegarde.suivi_disque_depuis IS 'Point de départ du délai d''alerte tant qu''aucune sauvegarde n''a réussi (script 148).';
COMMENT ON COLUMN parametre_sauvegarde.derniere_alerte_sans_disque IS 'Dernière alerte « aucune sauvegarde sur disque externe » envoyée : une par période (script 148).';
