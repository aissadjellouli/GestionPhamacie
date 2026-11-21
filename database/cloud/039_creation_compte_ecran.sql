-- =====================================================================
-- Base centrale, création d'un compte distant depuis l'écran « Comptes »
-- (2026-11-21, go du titulaire « go création compte écran ») :
-- docs/ecrans-central.md § 3 octies. Script de la base CENTRALE, passé
-- par le propriétaire, idempotent ; à passer après 038.
--   L'ÉDITEUR seul crée un compte éditeur ou titulaire (identifiant, nom,
--   e-mail et/ou téléphone) avec un code de confirmation ; le mot de
--   passe est PROVISOIRE, tiré au sort par l'application (seule son
--   empreinte BCrypt arrive ici), montré UNE fois à l'éditeur, à changer
--   à la première connexion. La console (--creer-compte-editeur,
--   --creer-compte-titulaire) reste possible, notamment pour le premier
--   éditeur à l'installation.
-- Aucune donnée existante n'est lue, modifiée ni supprimée par ce script
-- (fonction ajoutée).
-- =====================================================================

SET client_encoding = 'UTF8';

DO $version$ BEGIN
    IF to_regprocedure('public.cloud_ecran_remise_mot_de_passe(text,text,uuid,text,text,text)') IS NULL THEN
        RAISE EXCEPTION 'Script central 039 : passer d''abord les scripts 030 à 038 (écrans du central). Rien n''a été modifié.';
    END IF;
END $version$;

-- Statuts : ok, identifiant (format : 3 à 60 caractères, minuscules, chiffres, « . _ - »), existe (identifiant déjà pris), nom (1 à
-- 120 caractères), role (editeur ou titulaire), contact (ni e-mail ni téléphone), email, telephone (formes de compte_distant), puis
-- ceux du code (faux, expire, epuise, aucun). Contrôles AVANT le code (une faute de saisie ne consomme pas le code).
-- Journaux : accès (chaîné ; une ligne pour l'éditeur, une pour le compte créé) et sécurité (n° du compte, rôle) ; jamais
-- l'identifiant, le nom, l'e-mail, le téléphone ni le mot de passe.
CREATE OR REPLACE FUNCTION cloud_ecran_compte_creer(p_empreinte_jeton text, p_empreinte_code text, p_identifiant text, p_nom text,
                                                    p_role text, p_telephone text, p_email text, p_empreinte_mdp text)
RETURNS TABLE (statut text, compte uuid)
LANGUAGE plpgsql SECURITY DEFINER SET search_path = public, pg_temp AS $$
DECLARE v record; r text; v_id uuid;
        v_ident text := lower(btrim(coalesce(p_identifiant, '')));
        v_nom text := btrim(coalesce(p_nom, ''));
        v_tel text := nullif(replace(btrim(coalesce(p_telephone, '')), ' ', ''), '');
        v_email text := nullif(btrim(coalesce(p_email, '')), '');
BEGIN
    SELECT * INTO v FROM cloud_ecran_editeur(p_empreinte_jeton);
    IF p_empreinte_mdp IS NULL OR p_empreinte_mdp !~ '^\$2[aby]\$1[2-9]\$[./A-Za-z0-9]{53}$' THEN
        RAISE EXCEPTION 'empreinte BCrypt invalide (coût 12 au moins)' USING ERRCODE = '22023';
    END IF;
    r := CASE WHEN v_ident !~ '^[a-z0-9][a-z0-9._-]{2,59}$' THEN 'identifiant'
              WHEN EXISTS (SELECT 1 FROM compte_distant x WHERE x.identifiant = v_ident) THEN 'existe'
              WHEN length(v_nom) NOT BETWEEN 1 AND 120 THEN 'nom'
              WHEN p_role IS NULL OR p_role NOT IN ('editeur', 'titulaire') THEN 'role'
              WHEN v_tel IS NULL AND v_email IS NULL THEN 'contact'
              WHEN v_email IS NOT NULL AND (length(v_email) > 254 OR v_email !~ '^[^@[:space:]]+@[^@[:space:]]+\.[^@[:space:]]+$') THEN 'email'
              WHEN v_tel IS NOT NULL AND v_tel !~ '^\+?[0-9]{8,15}$' THEN 'telephone' END;
    IF r IS NOT NULL THEN
        PERFORM cloud_ecran_journaliser(v.compte_id, v.session_id, NULL, 'compte_cree', 'refus', r);
        RETURN QUERY SELECT r, NULL::uuid; RETURN;
    END IF;
    r := cloud_ecran_controler_code(v.session_id, 'action', p_empreinte_code);
    IF r <> 'ok' THEN
        PERFORM cloud_ecran_noter_action(v.compte_id, v.session_id, NULL, 'compte_cree', 'refus', r, NULL);
        RETURN QUERY SELECT r, NULL::uuid; RETURN;
    END IF;
    BEGIN
        INSERT INTO compte_distant (identifiant, nom, role, empreinte_mot_de_passe, telephone, email, cree_par)
        VALUES (v_ident, v_nom, p_role, p_empreinte_mdp, v_tel, v_email, 'écran : éditeur n° ' || left(v.compte_id::text, 8))
        RETURNING id INTO v_id;
    EXCEPTION WHEN unique_violation THEN
        -- Créé entre le contrôle et l'insertion (deux éditeurs en même temps) : le code est consommé, rien n'est créé
        RETURN QUERY SELECT 'existe'::text, NULL::uuid; RETURN;
    END;
    PERFORM cloud_ecran_journaliser(v_id, NULL, NULL, 'compte_cree', 'ok', 'editeur:' || p_role);
    PERFORM cloud_ecran_noter_action(v.compte_id, v.session_id, NULL, 'compte_cree', 'ok', 'compte:' || left(v_id::text, 8),
        'compte ' || p_role || ' n° ' || left(v_id::text, 8) || ' créé depuis l''écran (mot de passe provisoire, à changer à la première connexion)');
    RETURN QUERY SELECT 'ok'::text, v_id;
END $$;

REVOKE ALL ON FUNCTION cloud_ecran_compte_creer(text, text, text, text, text, text, text, text) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION cloud_ecran_compte_creer(text, text, text, text, text, text, text, text) TO pharma_cloud_ecrans;

COMMENT ON FUNCTION cloud_ecran_compte_creer(text, text, text, text, text, text, text, text) IS 'Compte distant (éditeur ou titulaire) créé par l''éditeur depuis l''écran « Comptes » (mot de passe provisoire, code de confirmation), script 039.';
