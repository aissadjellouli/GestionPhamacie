-- =====================================================================
-- Base centrale, lot S7.5 (2026-11-21, go du titulaire, maquette validée) :
-- LIGNES ABSENTES vues par les écrans du central (docs/ecrans-central.md
-- § 3 sexies, docs/conception-synchronisation.md § 13.3 et § 32.5).
-- Script de la base CENTRALE, passé par le propriétaire, idempotent ; à
-- passer après 035. LECTURE SEULEMENT : fonctions et index, aucune table
-- ni colonne créée, aucune donnée lue hors des fonctions, modifiée ni
-- supprimée.
--   1. Index partiels (officine_id, sync_absente_le) WHERE sync_absente_le
--      IS NOT NULL sur chaque table miroir existante : le compte des lignes
--      absentes ne parcourt que ces lignes. Une table miroir ajoutée plus
--      tard (002 régénéré) n'a pas l'index tant que ce script n'est pas
--      repassé (le compte reste juste, seulement plus lent).
--   2. Regroupement interne (jamais accordé) : lignes marquées absentes
--      (sync_absente_le, posée par cloud_fin_chargement, 003 puis 020) de
--      l'officine courante, par table, chargement qui les a constatées
--      (fin_le du chargement terminé = sync_absente_le : même transaction)
--      et CAUSE PROBABLE, dérivée seulement de ce que le central sait :
--        restauration     : ligne d'une époque antérieure à celle du
--                           chargement ET restauration constatée par la
--                           vérification d'intégrité pour cette époque
--                           (cloud_integrite_bifurcation, 022/023) ;
--        nouvelle_epoque  : ligne d'une époque antérieure à celle du
--                           chargement (officine repartie sur une nouvelle
--                           époque : restauration ou bascule, S6-D1), sans
--                           restauration constatée ;
--        non_expliquee    : tout le reste (même époque, chargement non
--                           retrouvé). AUCUNE anomalie n'est ouverte
--                           (décision n° 8 : signaler et garder).
--      Aucune purge des données de l'officine n'est connue du central
--      (la purge du lot S3 ne vide que la file locale sync_sortie) :
--      cause « purge » non dérivable, donc jamais rendue.
--      NOMBRES SEULEMENT : aucune colonne métier n'est lue (seulement
--      officine_id, sync_epoque, sync_absente_le et count(*)).
--   3. Écrans de l'ÉDITEUR (fiche d'une officine, rubrique « Lignes
--      absentes ») : tableau paginé et résumé (4 tuiles), après
--      cloud_ecran_ouvrir (officine posée par la base).
--   4. Écrans du TITULAIRE : nombre de lignes absentes et de tables
--      concernées par officine (droit « consulter », officine ouverte par
--      la base, RLS forcée) ; NULL (« non disponible ») tant qu'aucun
--      chargement complet n'est terminé.
-- Droits : EXECUTE retiré à PUBLIC ; les trois fonctions des écrans
-- accordées à pharma_cloud_ecrans seulement ; les deux internes à
-- personne.
-- =====================================================================

SET client_encoding = 'UTF8';

DO $version$ BEGIN
    IF to_regprocedure('public.cloud_ecran_titulaire_reglages_envois(text,integer,integer)') IS NULL
       OR to_regprocedure('public.cloud_ecran_officine_editeur(text)') IS NULL
       OR to_regclass('public.cloud_integrite_bifurcation') IS NULL OR to_regclass('public.chargement') IS NULL THEN
        RAISE EXCEPTION 'Script central 036 : passer d''abord les scripts 001 à 035. Rien n''a été modifié.';
    END IF;
END $version$;

-- ---- 1. Index partiels des lignes absentes (tables miroir existantes) ----
DO $index$
DECLARE t text;
BEGIN
    FOR t IN SELECT m.nom_table FROM cloud_table_miroir m
             WHERE m.nom_table ~ '^[a-z_][a-z0-9_]{0,62}$'
               AND EXISTS (SELECT 1 FROM pg_attribute a WHERE a.attrelid = to_regclass('public.' || quote_ident(m.nom_table))
                           AND a.attname = 'sync_absente_le' AND a.attnum > 0 AND NOT a.attisdropped)
               AND EXISTS (SELECT 1 FROM pg_attribute a WHERE a.attrelid = to_regclass('public.' || quote_ident(m.nom_table))
                           AND a.attname = 'officine_id' AND a.attnum > 0 AND NOT a.attisdropped)
             ORDER BY m.nom_table LOOP
        -- Nom borné à 63 caractères : début du nom de la table et empreinte courte (aucune collision de troncature)
        EXECUTE format('CREATE INDEX IF NOT EXISTS %I ON public.%I (officine_id, sync_absente_le) WHERE sync_absente_le IS NOT NULL',
                       'ix_abs_' || left(t, 47) || '_' || left(md5(t), 8), t);
    END LOOP;
END $index$;

-- ---- 2. Regroupement interne (jamais accordé) ----

-- Une ligne par (table, époque de la ligne, date de constatation) : nombres seulement, officine courante (RLS forcée)
CREATE OR REPLACE FUNCTION cloud_lignes_absentes_brut()
RETURNS TABLE (nom_table text, ligne_epoque int, absente_le timestamptz, lignes bigint)
LANGUAGE plpgsql STABLE SECURITY DEFINER SET search_path = public, pg_temp AS $$
DECLARE v_off uuid := cloud_officine_courante(); t text;
BEGIN
    IF v_off IS NULL THEN RAISE EXCEPTION 'Officine courante non posée (app.officine_id)'; END IF;
    FOR t IN SELECT m.nom_table FROM cloud_table_miroir m
             WHERE m.nom_table ~ '^[a-z_][a-z0-9_]{0,62}$'
               AND EXISTS (SELECT 1 FROM pg_attribute a WHERE a.attrelid = to_regclass('public.' || quote_ident(m.nom_table))
                           AND a.attname = 'sync_absente_le' AND a.attnum > 0 AND NOT a.attisdropped)
             ORDER BY m.nom_table LOOP
        RETURN QUERY EXECUTE format(
            'SELECT %L::text, x.sync_epoque::int, x.sync_absente_le, count(*)::bigint FROM public.%I x
              WHERE x.officine_id = $1 AND x.sync_absente_le IS NOT NULL GROUP BY 2, 3', t, t) USING v_off;
    END LOOP;
END $$;

-- Groupes affichés : table, chargement qui a constaté (NULL : non retrouvé), date de constatation, époque du chargement, cause
-- probable, nombre de lignes. Date du retour en arrière : changement d'époque déclaré par l'officine (détecté, sinon décidé).
CREATE OR REPLACE FUNCTION cloud_lignes_absentes_groupes()
RETURNS TABLE (nom_table text, chargement int, constatee_le timestamptz, epoque int, cause text, retour_le timestamptz, lignes bigint)
LANGUAGE sql STABLE SECURITY DEFINER SET search_path = public, pg_temp AS $$
    WITH b AS (SELECT * FROM cloud_lignes_absentes_brut()),
    k AS (
        SELECT b.nom_table, c.numero, b.absente_le, c.epoque,
               CASE WHEN c.numero IS NULL OR coalesce(b.ligne_epoque, c.epoque) >= c.epoque THEN 'non_expliquee'
                    WHEN EXISTS (SELECT 1 FROM cloud_integrite_bifurcation f WHERE f.officine_id = cloud_officine_courante()
                                 AND f.nouvelle_epoque = c.epoque) THEN 'restauration'
                    ELSE 'nouvelle_epoque' END AS cause,
               b.lignes
        FROM b LEFT JOIN chargement c ON c.officine_id = cloud_officine_courante() AND c.etat = 'termine' AND c.fin_le = b.absente_le)
    SELECT k.nom_table, k.numero, k.absente_le, k.epoque, k.cause,
           CASE WHEN k.cause <> 'non_expliquee' THEN
               (SELECT coalesce(e.detecte_le, e.decide_le) FROM cloud_epoque_changement e
                 WHERE e.officine_id = cloud_officine_courante() AND e.nouvelle_epoque = k.epoque) END,
           sum(k.lignes)::bigint
    FROM k GROUP BY k.nom_table, k.numero, k.absente_le, k.epoque, k.cause
$$;

-- ---- 3. Écrans de l'éditeur (après cloud_ecran_ouvrir) ----

-- Tableau paginé (10 à 100 lignes) : table, lignes absentes, chargement qui les a constatées, date, cause probable. Plus nombreuses
-- d'abord. Aucun contenu de ligne, aucune clé.
CREATE OR REPLACE FUNCTION cloud_ecran_lignes_absentes(p_empreinte_jeton text, p_page int, p_taille int)
RETURNS TABLE (total bigint, nom_table text, lignes bigint, chargement int, premiere_constatation timestamptz, cause text)
LANGUAGE plpgsql SECURITY DEFINER SET search_path = public, pg_temp AS $$
DECLARE v record; v_taille int := least(greatest(coalesce(p_taille, 20), 10), 100); v_page int := greatest(coalesce(p_page, 1), 1);
BEGIN
    SELECT * INTO v FROM cloud_ecran_officine_editeur(p_empreinte_jeton);
    RETURN QUERY SELECT count(*) OVER (), g.nom_table, g.lignes, g.chargement, g.constatee_le, g.cause
                 FROM cloud_lignes_absentes_groupes() g
                 ORDER BY g.lignes DESC, g.nom_table, g.constatee_le, g.cause
                 OFFSET (v_page - 1) * v_taille LIMIT v_taille;
END $$;

-- Résumé (une ligne) : lignes absentes au total, tables concernées, dernier chargement complet terminé (n° et date), cause probable
-- la plus fréquente (en lignes ; à égalité : restauration, nouvelle époque, non expliquée), ses lignes et la date du retour en
-- arrière le plus récent qui lui est rattaché.
CREATE OR REPLACE FUNCTION cloud_ecran_lignes_absentes_resume(p_empreinte_jeton text)
RETURNS TABLE (lignes bigint, tables int, dernier_chargement int, dernier_chargement_le timestamptz, cause_frequente text,
               cause_lignes bigint, retour_le timestamptz)
LANGUAGE plpgsql SECURITY DEFINER SET search_path = public, pg_temp AS $$
#variable_conflict use_column
DECLARE v record;
BEGIN
    SELECT * INTO v FROM cloud_ecran_officine_editeur(p_empreinte_jeton);
    WITH g AS (SELECT * FROM cloud_lignes_absentes_groupes()),
    c AS (SELECT g.cause, sum(g.lignes)::bigint AS n, max(g.retour_le) AS r FROM g GROUP BY g.cause
          ORDER BY 2 DESC, CASE g.cause WHEN 'restauration' THEN 1 WHEN 'nouvelle_epoque' THEN 2 ELSE 3 END LIMIT 1)
    SELECT (SELECT coalesce(sum(g.lignes), 0)::bigint FROM g), (SELECT count(DISTINCT g.nom_table)::int FROM g), c.cause, c.n, c.r
      INTO lignes, tables, cause_frequente, cause_lignes, retour_le
      FROM (SELECT 1) un LEFT JOIN c ON true;
    SELECT c.numero, c.fin_le INTO dernier_chargement, dernier_chargement_le FROM chargement c
     WHERE c.etat = 'termine' ORDER BY c.fin_le DESC NULLS LAST, c.numero DESC LIMIT 1;
    RETURN NEXT;
END $$;

-- ---- 4. Écrans du titulaire ----

-- Nombre de lignes absentes et de tables concernées : d'une officine (p_officine), ou de toutes les officines du compte avec le droit
-- « consulter » (p_officine NULL, 50 au plus, comme la vue d'ensemble). Chaque officine est ouverte PAR LA BASE (refus : aucune
-- ligne, noté au journal des accès). NULL (« non disponible ») tant qu'aucun chargement complet n'est terminé : sans lui, aucune
-- ligne ne peut avoir été constatée absente.
CREATE OR REPLACE FUNCTION cloud_ecran_titulaire_lignes_absentes(p_empreinte_jeton text, p_officine uuid)
RETURNS TABLE (id uuid, lignes bigint, tables int)
LANGUAGE plpgsql SECURITY DEFINER SET search_path = public, pg_temp AS $$
DECLARE v record; o record; v_avant text := current_setting('app.officine_id', true);
BEGIN
    SELECT * INTO v FROM cloud_ecran_titulaire(p_empreinte_jeton);
    FOR o IN SELECT x.officine_id AS o_id FROM compte_officine x JOIN officine f ON f.id = x.officine_id
             WHERE x.compte_id = v.compte_id AND x.retire_le IS NULL AND (p_officine IS NULL OR x.officine_id = p_officine)
               AND (p_officine IS NOT NULL OR 'consulter' = ANY (x.droits))
             ORDER BY lower(f.libelle), f.id LIMIT 50 LOOP
        IF (SELECT y.ouverte FROM cloud_ecran_ouvrir_titulaire(p_empreinte_jeton, o.o_id, 'consulter', 'lignes_absentes_titulaire') y) THEN
            id := o.o_id; lignes := NULL; tables := NULL;
            IF EXISTS (SELECT 1 FROM chargement c WHERE c.etat = 'termine') THEN
                SELECT coalesce(sum(b.lignes), 0)::bigint, count(DISTINCT b.nom_table)::int INTO lignes, tables FROM cloud_lignes_absentes_brut() b;
            END IF;
            RETURN NEXT;
        END IF;
    END LOOP;
    -- Officine demandée mais non rattachée : refus noté par l'ouverture (aucune ligne rendue)
    IF p_officine IS NOT NULL AND NOT EXISTS (SELECT 1 FROM compte_officine x WHERE x.compte_id = v.compte_id AND x.officine_id = p_officine
                                              AND x.retire_le IS NULL) THEN
        PERFORM cloud_ecran_ouvrir(p_empreinte_jeton, p_officine);
    END IF;
    PERFORM set_config('app.officine_id', coalesce(v_avant, ''), true);
END $$;

-- ---- Droits : rien pour PUBLIC ; écrans : les trois fonctions de lecture ; internes : personne ----
REVOKE ALL ON FUNCTION cloud_lignes_absentes_brut(), cloud_lignes_absentes_groupes(), cloud_ecran_lignes_absentes(text, int, int),
    cloud_ecran_lignes_absentes_resume(text), cloud_ecran_titulaire_lignes_absentes(text, uuid) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION cloud_ecran_lignes_absentes(text, int, int) TO pharma_cloud_ecrans;
GRANT EXECUTE ON FUNCTION cloud_ecran_lignes_absentes_resume(text) TO pharma_cloud_ecrans;
GRANT EXECUTE ON FUNCTION cloud_ecran_titulaire_lignes_absentes(text, uuid) TO pharma_cloud_ecrans;

COMMENT ON FUNCTION cloud_lignes_absentes_groupes() IS 'Lot S7.5 : lignes absentes de l''officine courante par table, chargement et cause probable (nombres seulement), script 036.';
COMMENT ON FUNCTION cloud_ecran_titulaire_lignes_absentes(text, uuid) IS 'Lot S7.5 : nombre de lignes absentes par officine du titulaire (NULL sans chargement complet terminé), script 036.';
