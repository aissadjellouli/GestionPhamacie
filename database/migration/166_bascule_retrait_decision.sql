-- =====================================================================
-- 166 -- Bascule du 2e serveur, lot V2-4 : mise hors jeu du serveur 1 (retrait) et décisions de promotion
--   Plan : docs/taches/2026-11-23-bascule-2e-serveur/plan-vague-2.md § 3 (cas 2, promotion automatique) et § 10.
--   1. serveur_ecarte.nature : « bascule » (écart constaté après la promotion de l'autre serveur, script 163) ou « retrait » (mise hors
--      jeu demandée par le 2e serveur AVANT sa promotion). Pour un retrait, la timeline du serveur retenu n'est pas encore connue :
--      timeline_retenue devient facultative pour cette seule nature (la contrainte est remplacée, jamais relâchée pour « bascule »).
--   2. bascule_decision : une ligne par promotion automatique (ajout seul), écrite par le serveur promu à son redémarrage en principal :
--      cas, timelines, LSN, dernière transaction reçue, rapports des postes et sondes (noms de postes seulement, aucune donnée patient).
--   Les deux tables sont classées « jamais » (jamais envoyées au serveur central). Aucune donnée existante modifiée.
--   Retour arrière : database/migration/retour/166_bascule_retrait_decision_retour.sql (refusé si un retrait ou une décision existe).
-- Idempotent et rejouable sur une base plus récente.
-- A exécuter après 165_presence_postes.sql.
-- =====================================================================

-- Fichier en UTF-8 : lu comme tel quel que soit l'encodage du poste qui lance psql.
SET client_encoding = 'UTF8';

-- ---- 1. Nature d'un écart ---------------------------------------------------------------------------------------------------
ALTER TABLE serveur_ecarte ADD COLUMN IF NOT EXISTS nature varchar(20) NOT NULL DEFAULT 'bascule';
DO $garde$ BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'ck_serveur_ecarte_nature' AND conrelid = 'serveur_ecarte'::regclass) THEN
        ALTER TABLE serveur_ecarte ADD CONSTRAINT ck_serveur_ecarte_nature CHECK (nature::text = ANY (ARRAY['bascule'::text, 'retrait'::text]));
    END IF;
END $garde$;

-- (listes de valeurs écrites en text explicite : même forme dans la suite et dans database/init après pg_dump)
-- timeline_retenue facultative pour un retrait seulement (contrainte remplacée une seule fois : garde sur sa définition)
ALTER TABLE serveur_ecarte ALTER COLUMN timeline_retenue DROP NOT NULL;
DO $garde$ BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'ck_serveur_ecarte_timelines' AND conrelid = 'serveur_ecarte'::regclass
                   AND pg_get_constraintdef(oid) LIKE '%retrait%') THEN
        ALTER TABLE serveur_ecarte DROP CONSTRAINT IF EXISTS ck_serveur_ecarte_timelines;
        ALTER TABLE serveur_ecarte ADD CONSTRAINT ck_serveur_ecarte_timelines CHECK (
            (nature = 'bascule' AND timeline_retenue IS NOT NULL AND timeline_retenue > timeline_locale)
            OR (nature = 'retrait' AND (timeline_retenue IS NULL OR timeline_retenue > timeline_locale)));
    END IF;
END $garde$;
COMMENT ON COLUMN serveur_ecarte.nature IS 'bascule : écart constaté après la promotion de l''autre serveur (163) ; retrait : mise hors jeu demandée par le 2e serveur avant sa promotion (166)';

-- ---- 2. Décisions de promotion (ajout seul) -----------------------------------------------------------------------------------
CREATE TABLE IF NOT EXISTS bascule_decision (
    id                     bigint       GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    decidee_le             timestamptz  NOT NULL,
    cas                    varchar(30)  NOT NULL,
    timeline_avant         int,
    timeline_apres         int,
    lsn_final              varchar(40),
    derniere_donnee_recue  timestamptz,
    rapports               jsonb        NOT NULL DEFAULT '[]'::jsonb,
    sondes                 jsonb        NOT NULL DEFAULT '{}'::jsonb,
    motif                  varchar(500) NOT NULL,
    cree_le                timestamptz  NOT NULL DEFAULT now(),
    CONSTRAINT ck_bascule_decision_cas CHECK (cas::text = ANY (ARRAY['promotion'::text, 'retrait_promotion'::text]))
);
CREATE INDEX IF NOT EXISTS ix_bascule_decision_decidee_le ON bascule_decision (decidee_le);
COMMENT ON TABLE bascule_decision IS 'Promotions automatiques du 2e serveur (cas, timelines, LSN, dernière transaction reçue, rapports des postes et sondes ; noms de postes seulement) ; ajout seul ; jamais envoyée au central (script 166).';

CREATE OR REPLACE FUNCTION bascule_decision_proteger() RETURNS trigger
LANGUAGE plpgsql AS $$
BEGIN
    RAISE EXCEPTION 'bascule_decision : ajout seul';
END $$;
DROP TRIGGER IF EXISTS trg_bascule_decision_ajout_seul ON bascule_decision;
CREATE TRIGGER trg_bascule_decision_ajout_seul BEFORE UPDATE OR DELETE ON bascule_decision FOR EACH ROW EXECUTE FUNCTION bascule_decision_proteger();
DROP TRIGGER IF EXISTS trg_bascule_decision_non_vidable ON bascule_decision;
CREATE TRIGGER trg_bascule_decision_non_vidable BEFORE TRUNCATE ON bascule_decision FOR EACH STATEMENT EXECUTE FUNCTION bascule_decision_proteger();

-- Classement pour la synchronisation : jamais envoyée au serveur central
INSERT INTO sync_table (nom_table, sens, cle_colonnes, colonnes_exclues, colonnes_patient, colonnes_texte, porteur, motif) VALUES
    ('bascule_decision', 'jamais', '{id}', '{}', '{}', '{}', false, 'Décisions de promotion automatique du 2e serveur, script 166')
ON CONFLICT (nom_table) DO NOTHING;
