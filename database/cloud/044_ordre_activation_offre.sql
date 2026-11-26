-- =====================================================================
-- Base centrale, offre de base et options (décisions du titulaire du 2026-11-26, docs/offre-et-options.md, script local 172). Script
-- de la base CENTRALE, passé par le propriétaire, idempotent ; à passer après 043. Aucune donnée modifiée.
--   * Nouveau type d'ordre « activation_offre » : l'éditeur transmet à une officine un code d'activation qu'IL a signé (outil
--     Pharma.Editeur, clé privée jamais sur le central). Le central ne signe rien et ne peut pas créer d'activation : il contrôle
--     seulement la forme du code ; l'officine contrôle la signature, son code d'officine et son NIF, et refuse tout code faux.
--   * cloud_creer_ordre_activation(officine, code, auteur) : création par la console de l'éditeur (--ordre-activation), mêmes étapes
--     que cloud_creer_ordre (journal de l'ordre, journal de sécurité, publication) ; jamais ouverte aux écrans du titulaire.
--   Retour arrière (à la main, hors suite) : remettre la contrainte ck_ordre_central_type du script 038 et supprimer la fonction ;
--   possible seulement si aucun ordre « activation_offre » n'existe.
-- =====================================================================
SET client_encoding = 'UTF8';

DO $version$ BEGIN
    IF to_regprocedure('public.cloud_ordre_publier(uuid)') IS NULL OR to_regprocedure('public.cloud_journaliser(uuid,text,text,text)') IS NULL THEN
        RAISE EXCEPTION 'Script central 044 : passer d''abord les scripts 001 à 043. Rien n''a été modifié.';
    END IF;
END $version$;

-- Type « activation_offre » permis (garde de rejeu : marqueur dans la définition), origine console seulement (jamais le titulaire :
-- la contrainte ck_ordre_central_titulaire du 038 n'admet pour lui que essai, sauvegarde, vérification, réglages et message)
DO $$ BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'ck_ordre_central_type' AND conrelid = 'ordre_central'::regclass
                   AND pg_get_constraintdef(oid) LIKE '%''activation_offre''%') THEN
        ALTER TABLE ordre_central DROP CONSTRAINT IF EXISTS ck_ordre_central_type;
        ALTER TABLE ordre_central ADD CONSTRAINT ck_ordre_central_type CHECK (type IN ('essai', 'employe_creer', 'employe_modifier',
            'employe_bloquer', 'employe_debloquer', 'droits_role', 'prix_produit', 'nomenclature', 'commande', 'classe_suivie', 'organismes',
            'prestations', 'indications', 'dci', 'sauvegarde', 'verification_integrite', 'reglages', 'message', 'activation_offre'));
    END IF;
END $$;

CREATE OR REPLACE FUNCTION cloud_creer_ordre_activation(p_officine uuid, p_code text, p_auteur text) RETURNS uuid
LANGUAGE plpgsql AS $$
DECLARE v_id uuid; v_code text := btrim(coalesce(p_code, ''));
BEGIN
    IF p_auteur IS NULL OR length(btrim(p_auteur)) NOT BETWEEN 1 AND 100 THEN RAISE EXCEPTION 'Auteur de l''ordre obligatoire (1 à 100 caractères).'; END IF;
    IF NOT EXISTS (SELECT 1 FROM officine WHERE id = p_officine AND resiliee_le IS NULL) THEN RAISE EXCEPTION 'Officine inconnue ou résiliée : %', p_officine; END IF;
    -- Forme seulement (PO1.<contenu>.<signature>, base64url) : la signature est contrôlée par l'officine, jamais ici
    IF length(v_code) NOT BETWEEN 20 AND 4000 OR v_code !~ '^PO1\.[A-Za-z0-9_-]+\.[A-Za-z0-9_-]+$' THEN
        RAISE EXCEPTION 'Code d''activation mal formé (attendu : PO1.<contenu>.<signature>, produit par l''outil de l''éditeur).';
    END IF;
    PERFORM set_config('app.officine_id', p_officine::text, true);
    INSERT INTO ordre_central (officine_id, type, cible, attendu, donnees, auteur, expire_le)
    VALUES (p_officine, 'activation_offre', NULL, '{}'::jsonb, jsonb_build_object('code', v_code), btrim(p_auteur), now() + interval '7 days')
    RETURNING id INTO v_id;
    INSERT INTO ordre_central_journal (officine_id, ordre_id, statut, par) VALUES (p_officine, v_id, 'en_attente', btrim(p_auteur));
    PERFORM cloud_journaliser(p_officine, NULL, 'ordre_cree', left(format('ordre %s (activation_offre) par %s', v_id, btrim(p_auteur)), 500));
    PERFORM cloud_ordre_publier(v_id);
    RETURN v_id;
END $$;
COMMENT ON FUNCTION cloud_creer_ordre_activation(uuid, text, text) IS 'Transmet à une officine un code d''activation signé par l''éditeur (console --ordre-activation, script 044) ; le central ne signe rien';
REVOKE EXECUTE ON FUNCTION cloud_creer_ordre_activation(uuid, text, text) FROM PUBLIC;
