-- =====================================================================
-- 101b — Suppression de l'ancienne colonne vente.num_facture_chifa
-- (2026-11-16, modèle Pharma, lot 2, partie B du script 101).
-- SCRIPT ÉCRIT, NON EXÉCUTÉ : l'exécution sur une base réelle est une
-- opération DESTRUCTIVE soumise à la validation explicite du titulaire
-- (voir docs/mise-en-production.md, parcours B, étape 8).
--
-- À LIRE AVANT DE LANCER
--   * SAUVEGARDE OBLIGATOIRE avant (pg_dump complet de la base) : la
--     suppression est IRRÉVERSIBLE, sauf restauration de la sauvegarde.
--   * API arrêtée (la suppression prend un verrou exclusif sur vente).
--   * Prérequis : script 101 exécuté et TOUS les conflits de n° de
--     facture Chifa tranchés (écran « Conflits de n° de facture Chifa »).
--   * Compte à utiliser : celui qui POSSÈDE la table vente.
--       - avant le script 111b : pharma_app ;
--       - après le script 111b : un compte membre de pharma_proprietaire,
--         avec « SET ROLE pharma_proprietaire; » en tête de session
--         (jamais pharma_app, qui ne possède plus la table).
--     Le script vérifie ce point et refuse sinon.
--   * Lancement (exclu du dossier publié de l'API, motif *b_*.sql) :
--       psql -U <compte> -d <base> -v ON_ERROR_STOP=1 -f 101b_suppression_num_facture_chifa.sql
--
-- Ce que fait le script (une seule transaction implicite : un bloc DO ;
-- tout refus ne modifie RIEN) :
--   1. colonne déjà absente : ne fait rien et le dit (idempotent) ;
--   2. REFUSE (RAISE EXCEPTION, aucune modification) si :
--        - le compte n'est pas propriétaire de vente ;
--        - un conflit de vente_chifa_facture_conflit n'est pas tranché ;
--        - une vente a num_facture_chifa renseigné et chifa_facture vide
--          (valeur qui serait perdue) ;
--        - une valeur de num_facture_chifa diffère de chifa_facture sans
--          conflit tranché ;
--        - un objet dépend encore de la colonne : vue, règle, index,
--          contrainte, déclencheur, statistique (catalogue pg_depend),
--          fonction dont le texte la cite, catalogue de synchronisation
--          (sync_table, sync_famille_colonne), chaîne d'intégrité
--          (chaine_integrite.colonnes) ;
--   3. supprime la colonne (sans CASCADE), journalise l'opération dans
--      journal_audit (aucune donnée personnelle : seulement des comptes)
--      et affiche un résumé.
--
-- Ce qui ne change pas : vente_chifa_facture_conflit garde sa propre
-- copie num_facture_chifa (historique des valeurs avant tranchage) ;
-- sync_sortie / sync_lot / journal_audit conservent leurs lignes passées
-- (un lot déjà constitué reste renvoyable à l'identique : son contenu et
-- sa liste de colonnes retirées sont figés, voir script 136).
-- Idempotent : relancé, il ne fait rien.
-- =====================================================================

DO $$
DECLARE
    v_attnum      int;
    v_proprio     oid;
    n_total       bigint;
    n_renseignees bigint;
    n_a_trancher  bigint := 0;
    n_perdues     bigint := 0;
    n_divergentes bigint := 0;
    n_dep         int;
    n_hist        bigint := 0;
    n_lots        bigint := 0;
    n_audit       bigint := 0;
    v_liste       text;
    v_erreurs     text[] := '{}';
BEGIN
    PERFORM set_config('lock_timeout', '15s', true);

    -- 1. Idempotence : colonne déjà supprimée
    SELECT a.attnum INTO v_attnum
    FROM pg_attribute a
    WHERE a.attrelid = 'public.vente'::regclass AND a.attname = 'num_facture_chifa' AND NOT a.attisdropped;
    IF v_attnum IS NULL THEN
        RAISE NOTICE '101b : la colonne vente.num_facture_chifa n''existe déjà plus, rien à faire';
        RETURN;
    END IF;

    -- 2a. Propriétaire de la table
    SELECT c.relowner INTO v_proprio FROM pg_class c WHERE c.oid = 'public.vente'::regclass;
    IF NOT pg_has_role(current_user, v_proprio, 'USAGE') THEN
        RAISE EXCEPTION '101b REFUSÉ : le compte % ne possède pas la table vente (propriétaire : %). Avant 111b, utiliser pharma_app ; après 111b, utiliser un membre de pharma_proprietaire avec SET ROLE pharma_proprietaire. Aucune modification faite.',
            current_user, v_proprio::regrole;
    END IF;

    -- 2b. Script 101 exécuté
    IF to_regclass('public.vente_chifa_facture_conflit') IS NULL THEN
        RAISE EXCEPTION '101b REFUSÉ : la table vente_chifa_facture_conflit est absente (script 101 non exécuté). Aucune modification faite.';
    END IF;

    SELECT count(*), count(*) FILTER (WHERE NULLIF(btrim(num_facture_chifa), '') IS NOT NULL)
    INTO n_total, n_renseignees FROM vente;

    -- 2c. Conflits non tranchés
    SELECT count(*) INTO n_a_trancher FROM vente_chifa_facture_conflit WHERE tranche_le IS NULL;
    IF n_a_trancher > 0 THEN
        v_erreurs := v_erreurs || format('%s conflit(s) de n° de facture Chifa non tranché(s) (écran « Conflits de n° de facture Chifa »)', n_a_trancher);
    END IF;

    -- 2d. Valeur qui serait perdue : num_facture_chifa renseigné, chifa_facture vide
    SELECT count(*) INTO n_perdues FROM vente
    WHERE NULLIF(btrim(num_facture_chifa), '') IS NOT NULL AND NULLIF(btrim(chifa_facture), '') IS NULL;
    IF n_perdues > 0 THEN
        v_erreurs := v_erreurs || format('%s vente(s) ont num_facture_chifa renseigné et chifa_facture vide (valeur perdue à la suppression ; relancer 101 puis trancher les conflits)', n_perdues);
    END IF;

    -- 2e. Valeurs différentes sans conflit tranché
    SELECT count(*) INTO n_divergentes FROM vente v
    WHERE NULLIF(btrim(v.num_facture_chifa), '') IS NOT NULL AND NULLIF(btrim(v.chifa_facture), '') IS NOT NULL
      AND btrim(v.num_facture_chifa) <> btrim(v.chifa_facture)
      AND NOT EXISTS (SELECT 1 FROM vente_chifa_facture_conflit c WHERE c.vente_id = v.id AND c.tranche_le IS NOT NULL);
    IF n_divergentes > 0 THEN
        v_erreurs := v_erreurs || format('%s vente(s) ont num_facture_chifa différent de chifa_facture sans conflit tranché', n_divergentes);
    END IF;

    -- 2f. Objets qui dépendent de la colonne (catalogue). Le défaut de colonne
    --     (pg_attrdef) disparaît avec elle ; tout le reste bloque.
    SELECT count(*), string_agg(pg_describe_object(d.classid, d.objid, d.objsubid), ' ; ' ORDER BY pg_describe_object(d.classid, d.objid, d.objsubid))
    INTO n_dep, v_liste
    FROM pg_depend d
    WHERE d.refclassid = 'pg_class'::regclass AND d.refobjid = 'public.vente'::regclass AND d.refobjsubid = v_attnum
      AND d.deptype IN ('n', 'a') AND d.classid <> 'pg_attrdef'::regclass;
    IF n_dep > 0 THEN
        v_erreurs := v_erreurs || format('%s objet(s) dépendent de la colonne (vue, règle, index, contrainte, déclencheur ou statistique) : %s', n_dep, v_liste);
    END IF;

    --     Déclencheurs de vente dont la définition cite la colonne (clause WHEN / UPDATE OF)
    SELECT count(*), string_agg(t.tgname, ', ') INTO n_dep, v_liste
    FROM pg_trigger t
    WHERE t.tgrelid = 'public.vente'::regclass AND NOT t.tgisinternal
      AND pg_get_triggerdef(t.oid) ILIKE '%num_facture_chifa%';
    IF n_dep > 0 THEN
        v_erreurs := v_erreurs || format('déclencheur(s) de vente citant la colonne : %s', v_liste);
    END IF;

    --     Fonctions, vues et vues matérialisées dont le texte cite la colonne
    --     (hors dépendance suivie par le catalogue : corps de fonction)
    SELECT count(*), string_agg(p.oid::regprocedure::text, ' ; ' ORDER BY p.oid::regprocedure::text) INTO n_dep, v_liste
    FROM pg_proc p JOIN pg_namespace n ON n.oid = p.pronamespace
    WHERE n.nspname = 'public' AND p.prosrc ILIKE '%num_facture_chifa%';
    IF n_dep > 0 THEN
        v_erreurs := v_erreurs || format('%s fonction(s) citent num_facture_chifa (vérifier s''il s''agit de vente ou de la table des conflits) : %s', n_dep, v_liste);
    END IF;
    SELECT count(*), string_agg(viewname, ', ') INTO n_dep, v_liste
    FROM pg_views WHERE schemaname = 'public' AND definition ILIKE '%num_facture_chifa%';
    IF n_dep > 0 THEN
        v_erreurs := v_erreurs || format('vue(s) citant num_facture_chifa : %s', v_liste);
    END IF;
    SELECT count(*), string_agg(matviewname, ', ') INTO n_dep, v_liste
    FROM pg_matviews WHERE schemaname = 'public' AND definition ILIKE '%num_facture_chifa%';
    IF n_dep > 0 THEN
        v_erreurs := v_erreurs || format('vue(s) matérialisée(s) citant num_facture_chifa : %s', v_liste);
    END IF;

    -- 2g. Catalogue de synchronisation (filtres et familles par colonne)
    IF to_regclass('public.sync_table') IS NOT NULL THEN
        SELECT count(*) INTO n_dep FROM sync_table
        WHERE nom_table = 'vente'
          AND ('num_facture_chifa' = ANY (colonnes_exclues) OR 'num_facture_chifa' = ANY (colonnes_patient) OR 'num_facture_chifa' = ANY (cle_colonnes));
        IF n_dep > 0 THEN
            v_erreurs := v_erreurs || 'sync_table (vente) cite la colonne dans colonnes_exclues, colonnes_patient ou cle_colonnes';
        END IF;
    END IF;
    IF to_regclass('public.sync_famille_colonne') IS NOT NULL THEN
        SELECT count(*) INTO n_dep FROM sync_famille_colonne WHERE nom_table = 'vente' AND colonne = 'num_facture_chifa';
        IF n_dep > 0 THEN
            v_erreurs := v_erreurs || 'sync_famille_colonne cite vente.num_facture_chifa';
        END IF;
    END IF;

    -- 2h. Chaîne d'intégrité (empreintes des lignes scellées)
    IF to_regclass('public.chaine_integrite') IS NOT NULL THEN
        SELECT count(*), string_agg(journal, ', ') INTO n_dep, v_liste
        FROM chaine_integrite WHERE 'num_facture_chifa' = ANY (colonnes);
        IF n_dep > 0 THEN
            v_erreurs := v_erreurs || format('chaîne d''intégrité : la colonne entre dans l''empreinte de %s (suppression impossible sans casser les sceaux)', v_liste);
        END IF;
    END IF;

    IF cardinality(v_erreurs) > 0 THEN
        RAISE EXCEPTION E'101b REFUSÉ (aucune modification faite) :\n  - %', array_to_string(v_erreurs, E'\n  - ');
    END IF;

    -- Historique conservé tel quel (information seulement)
    IF to_regclass('public.sync_sortie') IS NOT NULL THEN
        SELECT count(*) INTO n_hist FROM sync_sortie WHERE donnees ? 'num_facture_chifa' AND nom_table = 'vente';
    END IF;
    IF to_regclass('public.sync_lot') IS NOT NULL AND EXISTS (
           SELECT 1 FROM information_schema.columns WHERE table_schema = 'public' AND table_name = 'sync_lot' AND column_name = 'colonnes_retirees') THEN
        SELECT count(*) INTO n_lots FROM sync_lot WHERE 'vente.num_facture_chifa' = ANY (colonnes_retirees);
    END IF;
    SELECT count(*) INTO n_audit FROM journal_audit WHERE table_cible = 'vente' AND details::text ILIKE '%num_facture_chifa%';

    -- 3. Suppression (sans CASCADE : tout objet oublié ferait échouer et annuler l'opération)
    ALTER TABLE public.vente DROP COLUMN num_facture_chifa;

    INSERT INTO journal_audit (utilisateur_id, action, table_cible, enregistrement_id, details)
    VALUES (NULL, 'colonne_supprimee', 'vente', NULL,
            jsonb_build_object('colonne', 'num_facture_chifa', 'script', '101b',
                               'ventes', n_total, 'valeurs_renseignees_supprimees', n_renseignees,
                               'motif', 'fusion du n° de facture Chifa dans chifa_facture (scripts 101 et 101b)'));

    RAISE NOTICE '101b : colonne vente.num_facture_chifa supprimée (% vente(s), dont % avec une valeur, toutes déjà portées par chifa_facture)', n_total, n_renseignees;
    RAISE NOTICE '101b : historique conservé : % ligne(s) de sync_sortie contenant la colonne, % lot(s) l''ayant retirée, % ligne(s) de journal_audit la citant', n_hist, n_lots, n_audit;
    RAISE NOTICE '101b : la table vente_chifa_facture_conflit garde sa copie des valeurs ; opération journalisée (journal_audit, action colonne_supprimee)';
END $$;
