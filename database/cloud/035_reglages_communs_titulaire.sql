-- =====================================================================
-- Base centrale, lot S7.4 (2026-11-21, go du titulaire, recommandations
-- S7 acceptées) : RÉGLAGES COMMUNS proposés par le titulaire de plusieurs
-- officines (docs/ecrans-central.md § 3 quinquies, docs/conception-
-- synchronisation.md § 32.4). Script de la base CENTRALE, passé par le
-- propriétaire, idempotent ; à passer après 034.
--   1. Liste FERMÉE et bornes (cloud_reglage_commun_bornes) : seuils
--      d'alerte de stock et de péremption du réglage général de
--      l'officine (parametre_alerte) ; jamais un prix, une TVA, un droit,
--      un compte, rien de fiscal ni de nominatif. Contrôle complet d'un
--      jeu (cloud_reglages_communs_controle) : format reglages-v1, au
--      moins un réglage, clés de la liste, entiers dans les bornes, rouge
--      plus court que l'orange. Mêmes bornes que Pharma.Domain
--      (ReglagesCommuns) ; le poste revérifie tout à la réception.
--   2. ordre_central : type « reglages » (origine titulaire SEULEMENT),
--      données contrôlées par la contrainte elle-même, 24 h comme les
--      autres ordres du titulaire ; colonne envoi_id (un envoi vers
--      plusieurs officines = un ordre par officine, même envoi_id).
--   3. Écrans : envoi d'un jeu à une ou plusieurs officines (droit
--      « ordres » exigé sur CHACUNE, tout ou rien, contrôles avant le
--      code de confirmation, 5 envois par heure, un seul jeu ouvert par
--      officine) ; liste de mes envois (officine, réglages, dates, statut :
--      jamais le motif ni l'auteur d'une décision locale).
-- L'officine décide sur son poste (titulaire, mot de passe ressaisi) :
-- rien ne s'applique sans son acceptation ; le résultat remonte par la
-- voie existante (sync_ordre_resultat → statut de l'ordre).
-- Aucune donnée existante n'est lue, modifiée ni supprimée (colonne
-- ajoutée, contraintes élargies).
-- =====================================================================

SET client_encoding = 'UTF8';

DO $version$ BEGIN
    IF to_regprocedure('public.cloud_ecran_titulaire_creer_ordre(text,uuid,text,text)') IS NULL
       OR NOT EXISTS (SELECT 1 FROM pg_attribute WHERE attrelid = to_regclass('public.ordre_central') AND attname = 'origine' AND NOT attisdropped) THEN
        RAISE EXCEPTION 'Script central 035 : passer d''abord le script 034. Rien n''a été modifié.';
    END IF;
END $version$;

-- ---- 1. Liste fermée et contrôle d'un jeu ----

-- Bornes d'un réglage commun (NULL, NULL : hors de la liste fermée). Même liste que Pharma.Domain.Reglages.ReglagesCommuns.
CREATE OR REPLACE FUNCTION cloud_reglage_commun_bornes(p_cle text, OUT minimum int, OUT maximum int)
LANGUAGE sql IMMUTABLE SET search_path = public, pg_temp AS $$
    SELECT b.mi, b.ma FROM (VALUES ('jours_orange', 1, 1095), ('jours_rouge', 1, 1095), ('horizon_risque_jours', 1, 1095),
                                   ('delai_fournisseur_jours', 0, 90), ('jours_securite', 0, 180), ('jours_cible_commande', 1, 365),
                                   ('jours_dormant', 1, 1095), ('seuil_lot_delaisse', 1, 1000), ('seuil_lot_delaisse_collectif', 1, 1000))
           b(cle, mi, ma)
    WHERE b.cle = p_cle
$$;

-- Contrôle complet d'un jeu (données d'un ordre « reglages ») : NULL si valide ; sinon format, vide, hors_liste, hors_bornes,
-- incoherent (même ordre de contrôle que ReglagesCommuns.Controler). Utilisée par la contrainte de la table : IMMUTABLE.
CREATE OR REPLACE FUNCTION cloud_reglages_communs_controle(p jsonb) RETURNS text
LANGUAGE plpgsql IMMUTABLE SET search_path = public, pg_temp AS $$
DECLARE r jsonb; e record; b record;
BEGIN
    IF p IS NULL OR jsonb_typeof(p) <> 'object' OR length(p::text) > 2000 OR p ->> 'format' IS DISTINCT FROM 'reglages-v1'
       OR jsonb_typeof(p -> 'reglages') IS DISTINCT FROM 'object'
       OR EXISTS (SELECT 1 FROM jsonb_object_keys(p) k WHERE k NOT IN ('format', 'reglages')) THEN
        RETURN 'format';
    END IF;
    r := p -> 'reglages';
    IF r = '{}'::jsonb THEN RETURN 'vide'; END IF;
    IF EXISTS (SELECT 1 FROM jsonb_object_keys(r) k WHERE (cloud_reglage_commun_bornes(k)).minimum IS NULL) THEN RETURN 'hors_liste'; END IF;
    FOR e IN SELECT key, value FROM jsonb_each(r) LOOP
        b := cloud_reglage_commun_bornes(e.key);
        IF jsonb_typeof(e.value) <> 'number' OR e.value::text !~ '^-?[0-9]{1,9}$' THEN RETURN 'hors_bornes'; END IF;
        IF (e.value::text)::int NOT BETWEEN b.minimum AND b.maximum THEN RETURN 'hors_bornes'; END IF;
    END LOOP;
    IF r ? 'jours_orange' AND r ? 'jours_rouge' AND (r ->> 'jours_rouge')::int >= (r ->> 'jours_orange')::int THEN RETURN 'incoherent'; END IF;
    RETURN NULL;
END $$;

-- ---- 2. Ordres : type « reglages » (titulaire seulement, données contrôlées), envoi ----
ALTER TABLE ordre_central ADD COLUMN IF NOT EXISTS envoi_id uuid;
CREATE INDEX IF NOT EXISTS ix_ordre_central_envoi ON ordre_central (compte_id, envoi_id) WHERE envoi_id IS NOT NULL;
DO $$ BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'ck_ordre_central_type' AND pg_get_constraintdef(oid) LIKE '%''reglages''%') THEN
        ALTER TABLE ordre_central DROP CONSTRAINT IF EXISTS ck_ordre_central_type;
        ALTER TABLE ordre_central ADD CONSTRAINT ck_ordre_central_type CHECK (type IN ('essai', 'employe_creer', 'employe_modifier',
            'employe_bloquer', 'employe_debloquer', 'droits_role', 'prix_produit', 'nomenclature', 'commande', 'classe_suivie', 'organismes',
            'prestations', 'indications', 'dci', 'sauvegarde', 'verification_integrite', 'reglages'));
    END IF;
    -- Liste fermée des ordres du titulaire, revérifiée par la table : sans donnée (essai, sauvegarde, vérification d'intégrité) ou
    -- réglages communs contrôlés ; « reglages » jamais par la console ; envoi_id seulement pour « reglages »
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'ck_ordre_central_titulaire' AND conrelid = 'ordre_central'::regclass
                   AND pg_get_constraintdef(oid) LIKE '%cloud_reglages_communs_controle%') THEN
        ALTER TABLE ordre_central DROP CONSTRAINT IF EXISTS ck_ordre_central_titulaire;
        ALTER TABLE ordre_central ADD CONSTRAINT ck_ordre_central_titulaire
            CHECK ((type <> 'reglages' OR (origine = 'titulaire' AND envoi_id IS NOT NULL))
                   AND (envoi_id IS NULL OR type = 'reglages')
                   AND (origine <> 'titulaire' OR (cible IS NULL AND expire_le <= cree_le + interval '24 hours'
                        AND ((type IN ('essai', 'sauvegarde', 'verification_integrite') AND donnees = '{}'::jsonb)
                             OR (type = 'reglages' AND cloud_reglages_communs_controle(donnees) IS NULL)))));
    END IF;
END $$;

-- ---------------------------------------------------------------------
-- 3. Écrans du titulaire
-- ---------------------------------------------------------------------

-- Envoi d'un jeu de réglages communs à une ou plusieurs officines. TOUT OU RIEN : chaque officine doit être rattachée au compte avec
-- le droit « ordres » (accordé seulement depuis son poste), présente et sans jeu encore ouvert ; sinon rien n'est créé et l'officine
-- en cause est rendue (refuse, partie, deja_en_cours). Statuts : ok (ordres = nombre d'officines), trop_d_envois (5 par heure),
-- format, vide, hors_liste, hors_bornes, incoherent, aucune_officine, trop_d_officines (50), refuse, partie, deja_en_cours, puis ceux
-- du code de confirmation (faux, expire, epuise, aucun). Les contrôles passent AVANT le code (une erreur ne le consomme pas) ;
-- chaque refus est noté au journal des accès (chaîné). Un ordre par officine (origine titulaire, 24 h, même envoi_id), publié par
-- la voie descendante existante (ordre-v1) ; l'officine décide sur son poste.
CREATE OR REPLACE FUNCTION cloud_ecran_titulaire_envoyer_reglages(p_empreinte_jeton text, p_officines uuid[], p_reglages jsonb, p_empreinte_code text)
RETURNS TABLE (statut text, officine_id uuid, ordres int)
LANGUAGE plpgsql SECURITY DEFINER SET search_path = public, pg_temp AS $$
DECLARE v record; c compte_distant; o officine; r text; v_off uuid; v_offs uuid[]; v_envoi uuid := gen_random_uuid(); v_id uuid; v_auteur text;
        n int := 0; v_avant text := current_setting('app.officine_id', true);
BEGIN
    SELECT * INTO v FROM cloud_ecran_titulaire(p_empreinte_jeton);
    -- Deux envois simultanés du même compte : l'un après l'autre (même verrou que les autres ordres du titulaire)
    PERFORM pg_advisory_xact_lock(hashtextextended('ordre_titulaire:' || v.compte_id::text, 0));
    IF (SELECT count(*) FROM journal_acces_distant j WHERE j.compte_id = v.compte_id AND j.evenement = 'envoi_reglages_titulaire'
        AND j.resultat = 'ok' AND j.le > now() - interval '1 hour') >= 5 THEN
        r := 'trop_d_envois';
    END IF;
    r := coalesce(r, cloud_reglages_communs_controle(p_reglages));
    IF r IS NULL THEN
        SELECT coalesce(array_agg(DISTINCT x ORDER BY x), '{}') INTO v_offs FROM unnest(coalesce(p_officines, '{}'::uuid[])) x WHERE x IS NOT NULL;
        r := CASE WHEN cardinality(v_offs) = 0 THEN 'aucune_officine' WHEN cardinality(v_offs) > 50 THEN 'trop_d_officines' END;
    END IF;
    IF r IS NOT NULL THEN
        PERFORM cloud_ecran_journaliser(v.compte_id, v.session_id, NULL, 'envoi_reglages_titulaire', 'refus', r);
        RETURN QUERY SELECT r, NULL::uuid, 0; RETURN;
    END IF;
    -- Chaque officine : rattachement non retiré et droit « ordres » (refus noté par l'ouverture), officine posée par la base
    FOREACH v_off IN ARRAY v_offs LOOP
        IF NOT (SELECT x.ouverte FROM cloud_ecran_ouvrir_titulaire(p_empreinte_jeton, v_off, 'ordres', 'reglages_titulaire') x) THEN
            r := 'refuse';
        ELSE
            SELECT * INTO o FROM officine x WHERE x.id = v_off;
            r := CASE WHEN o.resiliee_le IS NOT NULL THEN 'partie'
                      WHEN EXISTS (SELECT 1 FROM ordre_central x WHERE x.officine_id = v_off AND x.origine = 'titulaire' AND x.type = 'reglages'
                                   AND x.statut IN ('en_attente', 'a_confirmer') AND x.expire_le > now()) THEN 'deja_en_cours' END;
            IF r IS NOT NULL THEN
                PERFORM cloud_ecran_journaliser(v.compte_id, v.session_id, v_off, 'reglages_titulaire', 'refus', r);
            END IF;
        END IF;
        IF r IS NOT NULL THEN
            PERFORM set_config('app.officine_id', coalesce(v_avant, ''), true);
            PERFORM cloud_ecran_journaliser(v.compte_id, v.session_id, NULL, 'envoi_reglages_titulaire', 'refus', r);
            RETURN QUERY SELECT r, v_off, 0; RETURN;
        END IF;
    END LOOP;
    r := nullif(cloud_ecran_controler_code(v.session_id, 'action', p_empreinte_code), 'ok');
    IF r IS NOT NULL THEN
        PERFORM set_config('app.officine_id', coalesce(v_avant, ''), true);
        PERFORM cloud_ecran_journaliser(v.compte_id, v.session_id, NULL, 'envoi_reglages_titulaire', 'refus', 'code:' || r);
        RETURN QUERY SELECT r, NULL::uuid, 0; RETURN;
    END IF;
    SELECT * INTO c FROM compte_distant x WHERE x.id = v.compte_id;
    v_auteur := left('titulaire à distance (' || c.identifiant || ')', 100);
    FOREACH v_off IN ARRAY v_offs LOOP
        PERFORM set_config('app.officine_id', v_off::text, true);
        INSERT INTO ordre_central (officine_id, type, cible, attendu, donnees, auteur, expire_le, origine, compte_id, envoi_id)
        VALUES (v_off, 'reglages', NULL, '{}'::jsonb, p_reglages, v_auteur, now() + interval '24 hours', 'titulaire', v.compte_id, v_envoi)
        RETURNING id INTO v_id;
        INSERT INTO ordre_central_journal (officine_id, ordre_id, statut, par) VALUES (v_off, v_id, 'en_attente', v_auteur);
        PERFORM cloud_ecran_journaliser(v.compte_id, v.session_id, v_off, 'reglages_titulaire', 'ok', 'reglages');
        PERFORM cloud_journaliser(v_off, NULL, 'ordre_cree',
            left(format('ordre %s (reglages : %s) par l''écran du titulaire, compte n° %s, envoi %s, échéance 24 h', v_id,
                        (SELECT string_agg(k, ', ' ORDER BY k) FROM jsonb_object_keys(p_reglages -> 'reglages') k),
                        left(v.compte_id::text, 8), left(v_envoi::text, 8)), 500));
        PERFORM cloud_ordre_publier(v_id);
        n := n + 1;
    END LOOP;
    PERFORM set_config('app.officine_id', coalesce(v_avant, ''), true);
    PERFORM cloud_ecran_journaliser(v.compte_id, v.session_id, NULL, 'envoi_reglages_titulaire', 'ok', 'officines:' || n);
    RETURN QUERY SELECT 'ok'::text, NULL::uuid, n;
END $$;

-- Mes envois de réglages communs : un ordre par ligne (officine, réglages envoyés, dates, statut), pour les officines encore
-- rattachées au compte avec le droit « ordres » (chacune ouverte par la base, ouverture notée au journal des accès ; 50 au plus).
-- Seulement les envois de CE compte ; jamais le motif, l'auteur ni le résultat d'une décision locale. Un ordre ouvert échu est montré
-- « expire ». Page de 5 à 100 lignes, plus récentes d'abord.
CREATE OR REPLACE FUNCTION cloud_ecran_titulaire_reglages_envois(p_empreinte_jeton text, p_page int, p_taille int)
RETURNS TABLE (total bigint, id uuid, officine text, cree_le timestamptz, expire_le timestamptz, statut text, statut_le timestamptz, reglages jsonb)
LANGUAGE plpgsql SECURITY DEFINER SET search_path = public, pg_temp AS $$
DECLARE v record; f record; v_lignes jsonb := '[]'::jsonb; v_avant text := current_setting('app.officine_id', true);
        v_taille int := least(greatest(coalesce(p_taille, 10), 5), 100); v_page int := greatest(coalesce(p_page, 1), 1);
BEGIN
    SELECT * INTO v FROM cloud_ecran_titulaire(p_empreinte_jeton);
    FOR f IN SELECT x.officine_id AS o_id, g.libelle AS o_libelle FROM compte_officine x JOIN officine g ON g.id = x.officine_id
             WHERE x.compte_id = v.compte_id AND x.retire_le IS NULL AND 'ordres' = ANY (x.droits)
             ORDER BY lower(g.libelle), g.id LIMIT 50 LOOP
        IF (SELECT y.ouverte FROM cloud_ecran_ouvrir_titulaire(p_empreinte_jeton, f.o_id, 'ordres', 'reglages_envois') y) THEN
            v_lignes := v_lignes || coalesce((
                SELECT jsonb_agg(jsonb_build_object('id', o.id, 'officine', f.o_libelle, 'cree_le', o.cree_le, 'expire_le', o.expire_le,
                           'statut', CASE WHEN o.statut IN ('en_attente', 'a_confirmer') AND o.expire_le <= now() THEN 'expire' ELSE o.statut END,
                           'statut_le', o.statut_le, 'reglages', o.donnees -> 'reglages'))
                FROM (SELECT * FROM ordre_central z WHERE z.officine_id = f.o_id AND z.origine = 'titulaire' AND z.type = 'reglages'
                      AND z.compte_id = v.compte_id ORDER BY z.cree_le DESC LIMIT 200) o), '[]'::jsonb);
        END IF;
    END LOOP;
    PERFORM set_config('app.officine_id', coalesce(v_avant, ''), true);
    RETURN QUERY SELECT count(*) OVER (), (e ->> 'id')::uuid, e ->> 'officine', (e ->> 'cree_le')::timestamptz, (e ->> 'expire_le')::timestamptz,
                        e ->> 'statut', (e ->> 'statut_le')::timestamptz, e -> 'reglages'
                 FROM jsonb_array_elements(v_lignes) e
                 ORDER BY (e ->> 'cree_le')::timestamptz DESC, e ->> 'officine', e ->> 'id'
                 OFFSET (v_page - 1) * v_taille LIMIT v_taille;
END $$;

-- ---- Droits : écrans : envoi et liste ; rien pour PUBLIC. Les deux contrôles (purs, sans lecture de table) restent exécutables
-- par tous : la contrainte de ordre_central les appelle avec le rôle qui écrit la ligne (par exemple le retour du résultat d'un
-- ordre par l'API centrale) ----
REVOKE ALL ON FUNCTION cloud_ecran_titulaire_envoyer_reglages(text, uuid[], jsonb, text), cloud_ecran_titulaire_reglages_envois(text, int, int) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION cloud_ecran_titulaire_envoyer_reglages(text, uuid[], jsonb, text) TO pharma_cloud_ecrans;
GRANT EXECUTE ON FUNCTION cloud_ecran_titulaire_reglages_envois(text, int, int) TO pharma_cloud_ecrans;

COMMENT ON COLUMN ordre_central.envoi_id IS 'Envoi de réglages communs du titulaire (un ordre « reglages » par officine, même envoi), script 035.';
COMMENT ON FUNCTION cloud_reglages_communs_controle(jsonb) IS 'Lot S7.4 : jeu de réglages communs (reglages-v1, liste fermée, bornes) ; NULL si valide.';
