-- =====================================================================
-- Base centrale, motif de la déconnexion après inactivité au journal des
-- accès (recette du 2026-11-22). Script de la base CENTRALE, passé par le
-- propriétaire, idempotent ; à passer après 041.
-- Défaut corrigé : quand la page des écrans se ferme d'elle-même à
-- l'échéance d'inactivité, elle envoie « motif=inactivite », mais la
-- base fermait la session comme une déconnexion volontaire (motif_fin
-- « deconnexion », journal « deconnexion ») : le journal des accès ne
-- distinguait pas une sortie volontaire d'une fermeture par inactivité.
-- Règle : nouvelle fonction cloud_ecran_deconnexion(jeton, motif). Le
-- motif « inactivite » n'est retenu que si la BASE le constate (session
-- ouverte dont la dernière activité est plus ancienne que l'inactivité
-- réglée pour le rôle, à 30 s près pour le minuteur de la page) : alors
-- même notation que la fermeture par la base (040) : motif_fin
-- « inactivite », journal « session_expiree / ok / inactivite ». Sinon :
-- déconnexion ordinaire, comme cloud_ecran_deconnexion(jeton) (030),
-- qui reste inchangée. Aucune donnée existante modifiée.
-- =====================================================================

SET client_encoding = 'UTF8';

DO $version$ BEGIN
    IF to_regprocedure('public.cloud_inactivite_session(text)') IS NULL THEN
        RAISE EXCEPTION 'Script central 042 : passer d''abord les scripts 040 et 041 (inactivité des sessions). Rien n''a été modifié.';
    END IF;
END $version$;

-- Tolérance du minuteur de la page (échéance calculée à l'affichage, horloges du navigateur et du serveur) — interne
CREATE OR REPLACE FUNCTION cloud_tolerance_inactivite_page() RETURNS interval
LANGUAGE sql IMMUTABLE AS $$ SELECT interval '30 seconds' $$;

-- Déconnexion avec motif demandé par la page : « inactivite » vérifié par la base, sinon déconnexion ordinaire ; rien si la session
-- était déjà fermée (la base a pu la fermer juste avant, au contrôle d'accès)
CREATE OR REPLACE FUNCTION cloud_ecran_deconnexion(p_empreinte_jeton text, p_motif text) RETURNS void
LANGUAGE plpgsql SECURITY DEFINER SET search_path = public, pg_temp AS $$
DECLARE s session_distante; v_role text;
BEGIN
    IF p_empreinte_jeton IS NULL OR p_empreinte_jeton !~ '^[0-9a-f]{64}$' THEN RETURN; END IF;
    SELECT * INTO s FROM session_distante x WHERE x.empreinte_jeton = p_empreinte_jeton AND x.fin_le IS NULL FOR UPDATE;
    IF NOT FOUND THEN RETURN; END IF;
    SELECT c.role INTO v_role FROM compte_distant c WHERE c.id = s.compte_id;
    IF p_motif = 'inactivite' AND s.etape = 'ouverte'
       AND s.derniere_activite_le <= now() - cloud_inactivite_session(v_role) + cloud_tolerance_inactivite_page() THEN
        UPDATE session_distante SET fin_le = now(), motif_fin = 'inactivite' WHERE id = s.id;
        PERFORM cloud_ecran_journaliser(s.compte_id, s.id, NULL, 'session_expiree', 'ok', 'inactivite');
        RETURN;
    END IF;
    UPDATE session_distante SET fin_le = now(), motif_fin = 'deconnexion' WHERE id = s.id;
    PERFORM cloud_ecran_journaliser(s.compte_id, s.id, NULL, 'deconnexion', 'ok', NULL);
END $$;

REVOKE ALL ON FUNCTION cloud_tolerance_inactivite_page(), cloud_ecran_deconnexion(text, text) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION cloud_ecran_deconnexion(text, text) TO pharma_cloud_ecrans;
