-- =====================================================================
-- Base centrale : plus grand n° de ticket reçu d'une officine (lot S6.5.4,
-- 2026-11-19 ; docs/conception-synchronisation.md § 31, décision
-- S6.5-D6 (a) choisie par le titulaire le 2026-11-19).
-- Script de la base CENTRALE, à passer après 023 par le propriétaire.
--   * cloud_dernier_ticket() : plus grand n° « <préfixe><chiffres> » des
--     ventes reçues de l'officine courante (toutes époques, ventes absentes
--     du dernier chargement comprises : ce sont justement les ventes
--     perdues par une restauration), préfixe lu dans le compteur reçu
--     (« T- » par défaut). Lu par la route GET /api/v1/maximums (entrée
--     réservée « #ticket ») : l'officine revenue en arrière avance son
--     compteur au-delà, aucun n° n'est réattribué. Aucune donnée de ligne.
-- Idempotent. Aucune donnée existante modifiée.
-- =====================================================================

SET client_encoding = 'UTF8';

CREATE OR REPLACE FUNCTION cloud_dernier_ticket() RETURNS bigint
LANGUAGE plpgsql STABLE SECURITY DEFINER SET search_path = public, pg_temp AS $$
DECLARE v_off uuid := cloud_officine_courante(); v_prefixe text; v bigint;
BEGIN
    IF v_off IS NULL THEN RAISE EXCEPTION 'Officine courante non posée (app.officine_id)'; END IF;
    IF to_regclass('public.vente') IS NULL THEN RETURN NULL; END IF;
    IF to_regclass('public.compteur_ticket') IS NOT NULL THEN
        EXECUTE 'SELECT prefixe FROM public.compteur_ticket WHERE officine_id = $1 AND id = 1' INTO v_prefixe USING v_off;
    END IF;
    v_prefixe := coalesce(nullif(v_prefixe, ''), 'T-');
    -- Préfixe comparé tel quel (aucune expression régulière construite avec lui), puis 1 à 18 chiffres seulement
    EXECUTE 'SELECT max(substr(numero, length($2) + 1)::bigint) FROM public.vente
             WHERE officine_id = $1 AND left(numero, length($2)) = $2 AND substr(numero, length($2) + 1) ~ ''^[0-9]{1,18}$'''
        INTO v USING v_off, v_prefixe;
    RETURN v;
END $$;
REVOKE EXECUTE ON FUNCTION cloud_dernier_ticket() FROM PUBLIC;
GRANT EXECUTE ON FUNCTION cloud_dernier_ticket() TO pharma_cloud_api;
COMMENT ON FUNCTION cloud_dernier_ticket() IS 'Plus grand n° de ticket reçu de l''officine courante (route /maximums, entrée « #ticket ») ; script 024, S6.5-D6 a.';
