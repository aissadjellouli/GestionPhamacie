-- =====================================================================
-- 160 : message du titulaire à distance affiché sur les postes (reste du
-- lot S7.3, 2026-11-21, go du titulaire ; docs/ecrans-central.md
-- § 3 nonies, docs/regles-metier.md « Barre d'état : message du
-- titulaire »).
--   * Le message arrive comme un ordre du serveur central (type
--     « message », format message-v1, script central 038) dans
--     sync_ordre_recu (script 145) ; l'API locale le contrôle (texte
--     simple de 1 à 200 caractères, sans lien, fin d'affichage 1 à 7 jours
--     après l'envoi) et le note « appliqué » (= affiché) ou « refusé ».
--   * sync_ordre_recu.message_ferme_le / message_ferme_par : message
--     fermé par le titulaire de l'officine (il disparaît de la barre
--     d'état de tous les postes) ; seulement un message appliqué.
--   * sync_message_central_fermer : fermeture et journal des actions
--     (« message_central_ferme ») dans la même transaction.
-- sync_ordre_recu n'est jamais envoyée (sens « jamais ») : le miroir du
-- central (002_tables_miroir.sql) ne change pas. Idempotent ; aucune
-- donnée existante modifiée (deux colonnes vides ajoutées).
-- Retour arrière (manuel) : DROP FUNCTION sync_message_central_fermer(uuid, bigint) ;
--   DROP INDEX ix_sync_ordre_recu_message ; ALTER TABLE sync_ordre_recu DROP CONSTRAINT ck_sync_ordre_recu_message_ferme,
--   DROP COLUMN message_ferme_par, DROP COLUMN message_ferme_le.
-- =====================================================================

-- Fichier en UTF-8 : lu comme tel quel que soit l'encodage du poste qui lance psql.
SET client_encoding = 'UTF8';

ALTER TABLE sync_ordre_recu ADD COLUMN IF NOT EXISTS message_ferme_le timestamptz;
ALTER TABLE sync_ordre_recu ADD COLUMN IF NOT EXISTS message_ferme_par bigint REFERENCES utilisateur (id);

DO $contrainte$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'ck_sync_ordre_recu_message_ferme' AND conrelid = 'public.sync_ordre_recu'::regclass) THEN
        ALTER TABLE sync_ordre_recu ADD CONSTRAINT ck_sync_ordre_recu_message_ferme
            CHECK (message_ferme_le IS NULL OR (type = 'message' AND statut = 'applique' AND message_ferme_par IS NOT NULL));
    END IF;
END $contrainte$;

-- Messages encore affichables (les plus récents d'abord) : lecture de la barre d'état, toutes les minutes sur chaque poste
CREATE INDEX IF NOT EXISTS ix_sync_ordre_recu_message ON sync_ordre_recu (cree_le DESC)
    WHERE type = 'message' AND statut = 'applique' AND message_ferme_le IS NULL;

COMMENT ON COLUMN sync_ordre_recu.message_ferme_le IS 'Message du titulaire à distance fermé par le titulaire de l''officine (plus affiché sur les postes), script 160.';
COMMENT ON COLUMN sync_ordre_recu.message_ferme_par IS 'Titulaire de l''officine qui a fermé le message, script 160.';

-- Fermeture d'un message affiché (titulaire vérifié par l'API) : vrai si le message était affichable et vient d'être fermé ; faux
-- sinon (inconnu, pas un message, pas appliqué, déjà fermé). Journal des actions dans la même transaction (jamais le texte).
CREATE OR REPLACE FUNCTION sync_message_central_fermer(p_id uuid, p_par bigint) RETURNS boolean
LANGUAGE plpgsql AS $$
DECLARE o sync_ordre_recu;
BEGIN
    IF p_par IS NULL THEN RAISE EXCEPTION 'Message du serveur central : fermeture sans auteur'; END IF;
    UPDATE sync_ordre_recu SET message_ferme_le = now(), message_ferme_par = p_par
    WHERE id = p_id AND type = 'message' AND statut = 'applique' AND message_ferme_le IS NULL
    RETURNING * INTO o;
    IF NOT FOUND THEN RETURN false; END IF;
    INSERT INTO journal_audit (utilisateur_id, action, table_cible, details)
    VALUES (p_par, 'message_central_ferme', 'sync_ordre_recu',
            jsonb_build_object('ordre', o.id, 'auteur_central', o.auteur, 'jusqu_au', o.donnees ->> 'jusqu_au',
                               'longueur', length(coalesce(o.donnees ->> 'texte', ''))));
    RETURN true;
END $$;

COMMENT ON FUNCTION sync_message_central_fermer(uuid, bigint) IS 'Ferme un message du titulaire à distance affiché sur les postes (journal des actions), script 160.';
