-- =====================================================================
-- 163 -- Bascule du 2e serveur : état « serveur écarté » (lecture seule), gardé en base
--   Plan : docs/taches/2026-11-23-bascule-2e-serveur/plan.md (lot 2). Un serveur qui découvre en cours de marche qu'un autre
--   serveur est devenu principal (timeline plus grande) passe en lecture seule ; cet état doit survivre à un redémarrage de l'API,
--   même si l'autre serveur est injoignable. Une ligne sans levée (leve_le NULL) = serveur écarté ; au plus une ligne active.
--   Ajout seul : une ligne ne se supprime jamais ; seule la levée (leve_le, leve_par, leve_motif, posés une seule fois) la modifie.
--   Table classée « jamais » dans sync_table : l'état d'un serveur n'est jamais envoyé au serveur central.
--   Aucune donnée existante modifiée. Le script n'écrit rien dans la table.
--   Retour arrière : database/migration/retour/163_serveur_ecarte_lecture_seule_retour.sql (supprime la table, sa fonction et
--   son classement ; les lignes éventuelles de la table sont perdues, c'est voulu : le script les avait créées).
-- Idempotent et rejouable sur une base plus récente.
-- A exécuter après 162_deconnexion_inactivite_defaut_15_minutes.sql.
-- =====================================================================

-- Fichier en UTF-8 : lu comme tel quel que soit l'encodage du poste qui lance psql.
SET client_encoding = 'UTF8';

CREATE TABLE IF NOT EXISTS serveur_ecarte (
    id                    bigint       GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    ecarte_le             timestamptz  NOT NULL DEFAULT now(),
    serveur_retenu        varchar(200) NOT NULL,
    timeline_locale       int          NOT NULL CHECK (timeline_locale >= 1),
    timeline_retenue      int          NOT NULL,
    identifiant_systeme   varchar(40)  NOT NULL,
    motif                 varchar(500) NOT NULL,
    leve_le               timestamptz,
    leve_par              varchar(200),
    leve_motif            varchar(500),
    cree_le               timestamptz  NOT NULL DEFAULT now(),
    CONSTRAINT ck_serveur_ecarte_timelines CHECK (timeline_retenue > timeline_locale),
    CONSTRAINT ck_serveur_ecarte_levee CHECK ((leve_le IS NULL AND leve_par IS NULL AND leve_motif IS NULL)
                                              OR (leve_le IS NOT NULL AND leve_par IS NOT NULL AND leve_motif IS NOT NULL))
);
-- Au plus une ligne active (non levée) ; lecture de l'état au démarrage : index partiel.
CREATE UNIQUE INDEX IF NOT EXISTS ux_serveur_ecarte_active ON serveur_ecarte ((true)) WHERE leve_le IS NULL;
CREATE INDEX IF NOT EXISTS ix_serveur_ecarte_ecarte_le ON serveur_ecarte (ecarte_le);
COMMENT ON TABLE serveur_ecarte IS 'État « serveur écarté » (lecture seule) après une bascule vers un autre serveur principal ; ajout seul, seule la levée modifie une ligne ; jamais envoyée au central (script 163).';

CREATE OR REPLACE FUNCTION serveur_ecarte_proteger() RETURNS trigger
LANGUAGE plpgsql AS $$
BEGIN
    -- Seule la levée d'une ligne encore active est permise : les colonnes de l'écart restent inchangées.
    IF TG_OP = 'UPDATE' AND OLD.leve_le IS NULL AND NEW.leve_le IS NOT NULL
       AND (to_jsonb(NEW) - 'leve_le' - 'leve_par' - 'leve_motif') = (to_jsonb(OLD) - 'leve_le' - 'leve_par' - 'leve_motif') THEN
        RETURN NEW;
    END IF;
    RAISE EXCEPTION 'serveur_ecarte : ajout seul (seule la levée d''un écart actif est permise)';
END $$;
DROP TRIGGER IF EXISTS trg_serveur_ecarte_ajout_seul ON serveur_ecarte;
CREATE TRIGGER trg_serveur_ecarte_ajout_seul BEFORE UPDATE OR DELETE ON serveur_ecarte FOR EACH ROW EXECUTE FUNCTION serveur_ecarte_proteger();
DROP TRIGGER IF EXISTS trg_serveur_ecarte_non_vidable ON serveur_ecarte;
CREATE TRIGGER trg_serveur_ecarte_non_vidable BEFORE TRUNCATE ON serveur_ecarte FOR EACH STATEMENT EXECUTE FUNCTION serveur_ecarte_proteger();

-- Classement pour la synchronisation : jamais envoyée au serveur central
INSERT INTO sync_table (nom_table, sens, cle_colonnes, colonnes_exclues, colonnes_patient, colonnes_texte, porteur, motif) VALUES
    ('serveur_ecarte', 'jamais', '{id}', '{}', '{}', '{}', false, 'État local « serveur écarté » (lecture seule après bascule), script 163')
ON CONFLICT (nom_table) DO NOTHING;
