-- =====================================================================
-- Pharma Officine : CRÉATION COMPLÈTE DU SCHÉMA (installation neuve)
--
-- Équivaut aux scripts database/001 à 035 appliqués dans l'ordre (vérifié :
-- même structure). Pour une base déjà installée, appliquer seulement les
-- nouveaux scripts 0xx_*.sql, jamais ce fichier.
--
-- Base vide uniquement (créée vide, ou après clean.sql). S'arrête sans rien
-- faire si la base contient déjà le schéma.
--
--   psql -h <serveur> -U pharma_app -d <base> -v ON_ERROR_STOP=1 -f database/init/schema.sql
--
-- Puis : database/init/seed_referentiel.sql (référentiel de départ).
-- Fichier produit par pg_dump --schema-only à partir d'une base construite
-- par les scripts 001 à 035 ; à régénérer à chaque nouveau script 0xx.
-- =====================================================================

SELECT to_regclass('public.utilisateur') IS NOT NULL AS schema_deja_present \gset
\if :schema_deja_present
    \echo 'La base contient déjà le schéma : rien n''est fait. Base existante : appliquer seulement les nouveaux scripts 0xx_*.sql.'
    \quit
\endif

--
-- PostgreSQL database dump
--

\restrict 9XH77KG8B6v59htLgCaGmoeu9kRmbcKuQ2TcsCNOph5QQoRNyIVgeEHeqhfipGh


SET statement_timeout = 0;
SET lock_timeout = 0;
SET idle_in_transaction_session_timeout = 0;
SET transaction_timeout = 0;
SET client_encoding = 'UTF8';
SET standard_conforming_strings = on;
SELECT pg_catalog.set_config('search_path', '', false);
SET check_function_bodies = false;
SET xmloption = content;
SET client_min_messages = warning;
SET row_security = off;

--
-- Name: import; Type: SCHEMA; Schema: -; Owner: -
--

CREATE SCHEMA import;


--
-- Name: pg_trgm; Type: EXTENSION; Schema: -; Owner: -
--

CREATE EXTENSION IF NOT EXISTS pg_trgm WITH SCHEMA public;


--
-- Name: pgcrypto; Type: EXTENSION; Schema: -; Owner: -
--

CREATE EXTENSION IF NOT EXISTS pgcrypto WITH SCHEMA public;


--
-- Name: charger(bigint, bigint); Type: FUNCTION; Schema: import; Owner: -
--

CREATE FUNCTION import.charger(p_session bigint, p_utilisateur_id bigint) RETURNS void
    LANGUAGE plpgsql
    AS $$
DECLARE
    v_reprise  bigint;
    v_id       bigint;
    v_ref      text := 'REPRISE-' || p_session;
    r          record;
BEGIN
    IF (SELECT statut FROM import.session WHERE id = p_session) <> 'en_cours' THEN
        RAISE EXCEPTION 'Session % déjà chargée ou annulée', p_session;
    END IF;
    IF import.controler(p_session) > 0 THEN
        RAISE EXCEPTION 'Anomalies bloquantes restantes : chargement refusé';
    END IF;

    -- Produits absents du catalogue, créés à la demande du pharmacien
    FOR r IN
        SELECT s.*
        FROM import.correspondance_produit c
        JOIN import.produit_src s ON s.session_id = c.session_id AND s.ref_source = c.ref_source
        WHERE c.session_id = p_session AND c.methode = 'nouveau' AND c.produit_id IS NULL
    LOOP
        INSERT INTO produit (code_barre, num_enregistrement, nom_commercial, dosage, forme, prix_public)
        VALUES (NULLIF(btrim(r.code_barre), ''), NULLIF(btrim(r.num_enregistrement), ''),
                r.nom, r.dosage, r.forme, COALESCE(import.nombre(r.prix_vente), 0))
        RETURNING id INTO v_id;
        UPDATE import.correspondance_produit SET produit_id = v_id
        WHERE session_id = p_session AND ref_source = r.ref_source;
    END LOOP;

    -- Grossistes et leurs dettes
    FOR r IN SELECT * FROM import.fournisseur_src WHERE session_id = p_session LOOP
        INSERT INTO fournisseur (raison_sociale, telephone, solde)
        VALUES (r.raison_sociale, r.telephone, COALESCE(import.nombre(r.solde), 0))
        RETURNING id INTO v_id;
        UPDATE import.fournisseur_src SET fournisseur_id = v_id
        WHERE session_id = p_session AND ref_source = r.ref_source;
    END LOOP;

    -- Grossiste fictif pour le stock dont la provenance est inconnue
    INSERT INTO fournisseur (raison_sociale) VALUES ('Reprise de stock (ancien logiciel)')
    RETURNING id INTO v_reprise;

    -- Une réception de reprise par grossiste
    DROP TABLE IF EXISTS _rec;
    CREATE TEMP TABLE _rec (fournisseur_id bigint PRIMARY KEY, reception_id bigint) ON COMMIT DROP;

    FOR r IN
        SELECT DISTINCT COALESCE(fs.fournisseur_id, v_reprise) AS fid
        FROM import.stock_src st
        LEFT JOIN import.fournisseur_src fs
               ON fs.session_id = st.session_id AND fs.ref_source = st.fournisseur_ref
        WHERE st.session_id = p_session AND import.nombre(st.quantite) > 0
    LOOP
        INSERT INTO reception (fournisseur_id, numero_bl, utilisateur_id)
        VALUES (r.fid, v_ref, p_utilisateur_id)
        RETURNING id INTO v_id;
        INSERT INTO _rec VALUES (r.fid, v_id);
    END LOOP;

    -- Lots (date inconnue : provisoirement à un an, et signalée à vérifier)
    INSERT INTO lot (produit_id, reception_id, fournisseur_id, numero_lot,
                     date_peremption, quantite_disponible, prix_achat_ht, date_a_verifier)
    SELECT c.produit_id, rc.reception_id, rc.fournisseur_id, x.lot_reprise,
           COALESCE(MIN(x.dte), current_date + 365),
           SUM(x.qte)::int,
           MAX(COALESCE(import.nombre(ps.prix_achat), 0)),
           bool_or(x.dte IS NULL)
    FROM (
        SELECT st.*,
               import.nombre(st.quantite)        AS qte,
               import.date_src(st.date_peremption) AS dte,
               upper(COALESCE(NULLIF(btrim(st.numero_lot), ''),
                        'REPRISE-' || COALESCE(to_char(import.date_src(st.date_peremption), 'YYYYMM'), 'ND')))
                                                 AS lot_reprise   -- normalisé comme à la réception (018)
        FROM import.stock_src st
        WHERE st.session_id = p_session
    ) x
    JOIN import.correspondance_produit c
         ON c.session_id = x.session_id AND c.ref_source = x.ref_produit
    JOIN import.produit_src ps
         ON ps.session_id = x.session_id AND ps.ref_source = x.ref_produit
    LEFT JOIN import.fournisseur_src fs
         ON fs.session_id = x.session_id AND fs.ref_source = x.fournisseur_ref
    JOIN _rec rc ON rc.fournisseur_id = COALESCE(fs.fournisseur_id, v_reprise)
    WHERE x.qte > 0
    GROUP BY c.produit_id, rc.reception_id, rc.fournisseur_id, x.lot_reprise;

    -- Mouvements d'entrée pour la traçabilité
    INSERT INTO mouvement_stock (lot_id, type, quantite, reference, utilisateur_id, reception_id, prix_achat_ht)
    SELECT l.id, 'entree', l.quantite_disponible, v_ref, p_utilisateur_id, l.reception_id, l.prix_achat_ht
    FROM lot l
    WHERE l.reception_id IN (SELECT reception_id FROM _rec);

    -- Solde d'ouverture du registre des psychotropes
    INSERT INTO registre_psychotrope (lot_id, type_mouvement, quantite, solde_apres, utilisateur_id)
    SELECT l.id, 'entree', l.quantite_disponible, l.quantite_disponible, p_utilisateur_id
    FROM lot l
    JOIN produit p ON p.id = l.produit_id
    WHERE l.reception_id IN (SELECT reception_id FROM _rec)
      AND p.classe IN ('psychotrope', 'stupefiant');

    -- Clients et crédits en cours
    FOR r IN SELECT * FROM import.client_src WHERE session_id = p_session LOOP
        INSERT INTO patient (nom, prenom, telephone, num_assure, solde_credit)
        VALUES (r.nom, r.prenom, r.telephone, r.num_assure, COALESCE(import.nombre(r.solde_credit), 0))
        RETURNING id INTO v_id;
        UPDATE import.client_src SET patient_id = v_id
        WHERE session_id = p_session AND ref_source = r.ref_source;
    END LOOP;

    UPDATE import.session SET statut = 'charge' WHERE id = p_session;
END;
$$;


--
-- Name: controle_apres_chargement(bigint); Type: FUNCTION; Schema: import; Owner: -
--

CREATE FUNCTION import.controle_apres_chargement(p_session bigint) RETURNS TABLE(indicateur text, ancien numeric, nouveau numeric, ecart numeric)
    LANGUAGE sql STABLE
    AS $$
    SELECT t.indicateur, t.ancien, t.nouveau, t.nouveau - t.ancien
    FROM (
        SELECT 'Unités en stock'::text AS indicateur,
               (SELECT COALESCE(SUM(import.nombre(st.quantite)), 0)
                FROM import.stock_src st
                WHERE st.session_id = p_session AND import.nombre(st.quantite) > 0) AS ancien,
               (SELECT COALESCE(SUM(l.quantite_disponible), 0)::numeric
                FROM lot l JOIN reception r ON r.id = l.reception_id
                WHERE r.numero_bl = 'REPRISE-' || p_session) AS nouveau
        UNION ALL
        SELECT 'Crédits clients (DA)',
               (SELECT COALESCE(SUM(import.nombre(c.solde_credit)), 0)
                FROM import.client_src c WHERE c.session_id = p_session),
               (SELECT COALESCE(SUM(pt.solde_credit), 0)
                FROM patient pt JOIN import.client_src c ON c.patient_id = pt.id
                WHERE c.session_id = p_session)
        UNION ALL
        SELECT 'Dettes grossistes (DA)',
               (SELECT COALESCE(SUM(import.nombre(f.solde)), 0)
                FROM import.fournisseur_src f WHERE f.session_id = p_session),
               (SELECT COALESCE(SUM(fo.solde), 0)
                FROM fournisseur fo JOIN import.fournisseur_src f ON f.fournisseur_id = fo.id
                WHERE f.session_id = p_session)
        UNION ALL
        SELECT 'Nombre de clients',
               (SELECT count(*) FROM import.client_src c WHERE c.session_id = p_session),
               (SELECT count(*) FROM import.client_src c
                WHERE c.session_id = p_session AND c.patient_id IS NOT NULL)
    ) t;
$$;


--
-- Name: controler(bigint); Type: FUNCTION; Schema: import; Owner: -
--

CREATE FUNCTION import.controler(p_session bigint) RETURNS integer
    LANGUAGE plpgsql
    AS $$
DECLARE
    nb_bloquants integer;
BEGIN
    DELETE FROM import.anomalie WHERE session_id = p_session;

    INSERT INTO import.anomalie (session_id, entite, ref_source, gravite, message)
    SELECT p_session, 'produit', s.ref_source, 'bloquant',
           format('« %s » sans correspondance : à rapprocher à la main ou à créer', s.nom)
    FROM import.produit_src s
    LEFT JOIN import.correspondance_produit c
           ON c.session_id = s.session_id AND c.ref_source = s.ref_source
    WHERE s.session_id = p_session AND c.ref_source IS NULL;

    INSERT INTO import.anomalie (session_id, entite, ref_source, gravite, message)
    SELECT p_session, 'produit', c.ref_source, 'bloquant',
           format('Correspondance par nom à confirmer (ressemblance %s)', c.score)
    FROM import.correspondance_produit c
    WHERE c.session_id = p_session AND NOT c.valide;

    INSERT INTO import.anomalie (session_id, entite, ref_source, gravite, message)
    SELECT p_session, 'stock', st.ref_produit, 'bloquant',
           format('Quantité illisible ou négative : « %s »', st.quantite)
    FROM import.stock_src st
    WHERE st.session_id = p_session
      AND (import.nombre(st.quantite) IS NULL OR import.nombre(st.quantite) < 0);

    INSERT INTO import.anomalie (session_id, entite, ref_source, gravite, message)
    SELECT p_session, 'stock', st.ref_produit, 'bloquant',
           'Ligne de stock d''un produit absent de l''export produits'
    FROM import.stock_src st
    LEFT JOIN import.produit_src s
           ON s.session_id = st.session_id AND s.ref_source = st.ref_produit
    WHERE st.session_id = p_session AND s.ref_source IS NULL;

    INSERT INTO import.anomalie (session_id, entite, ref_source, gravite, message)
    SELECT p_session, 'stock', st.ref_produit, 'avertissement',
           'Date de péremption absente ou illisible : à vérifier lors de l''inventaire de démarrage'
    FROM import.stock_src st
    WHERE st.session_id = p_session
      AND import.nombre(st.quantite) > 0
      AND import.date_src(st.date_peremption) IS NULL;

    INSERT INTO import.anomalie (session_id, entite, ref_source, gravite, message)
    SELECT p_session, 'stock', st.ref_produit, 'avertissement',
           format('Lot déjà périmé (%s) : sera importé et bloqué à la vente', st.date_peremption)
    FROM import.stock_src st
    WHERE st.session_id = p_session
      AND import.nombre(st.quantite) > 0
      AND import.date_src(st.date_peremption) <= current_date;

    INSERT INTO import.anomalie (session_id, entite, ref_source, gravite, message)
    SELECT p_session, 'client', c.ref_source, 'bloquant',
           format('Solde crédit illisible pour %s : « %s »', c.nom, c.solde_credit)
    FROM import.client_src c
    WHERE c.session_id = p_session
      AND c.solde_credit IS NOT NULL AND btrim(c.solde_credit) <> ''
      AND import.nombre(c.solde_credit) IS NULL;

    INSERT INTO import.anomalie (session_id, entite, ref_source, gravite, message)
    SELECT p_session, 'fournisseur', f.ref_source, 'bloquant',
           format('Solde illisible pour %s : « %s »', f.raison_sociale, f.solde)
    FROM import.fournisseur_src f
    WHERE f.session_id = p_session
      AND f.solde IS NOT NULL AND btrim(f.solde) <> ''
      AND import.nombre(f.solde) IS NULL;

    SELECT count(*) INTO nb_bloquants
    FROM import.anomalie WHERE session_id = p_session AND gravite = 'bloquant';
    RETURN nb_bloquants;
END;
$$;


--
-- Name: date_src(text); Type: FUNCTION; Schema: import; Owner: -
--

CREATE FUNCTION import.date_src(t text) RETURNS date
    LANGUAGE plpgsql IMMUTABLE
    AS $_$
DECLARE
    s text := btrim(t);
BEGIN
    IF s IS NULL OR s = '' THEN RETURN NULL; END IF;
    IF s ~ '^\d{1,2}/\d{1,2}/\d{4}$' THEN
        RETURN to_date(s, 'DD/MM/YYYY');
    ELSIF s ~ '^\d{1,2}/\d{4}$' THEN                 -- MM/AAAA : dernier jour du mois
        RETURN (date_trunc('month', to_date(s, 'MM/YYYY')) + interval '1 month - 1 day')::date;
    ELSIF s ~ '^\d{4}-\d{2}-\d{2}' THEN
        RETURN substr(s, 1, 10)::date;
    ELSIF s ~ '^\d{8}$' THEN
        RETURN to_date(s, 'YYYYMMDD');
    END IF;
    RETURN NULL;
EXCEPTION WHEN others THEN
    RETURN NULL;
END;
$_$;


--
-- Name: nombre(text); Type: FUNCTION; Schema: import; Owner: -
--

CREATE FUNCTION import.nombre(t text) RETURNS numeric
    LANGUAGE plpgsql IMMUTABLE
    AS $_$
DECLARE
    s text;
BEGIN
    IF t IS NULL OR btrim(t) = '' THEN RETURN NULL; END IF;
    s := regexp_replace(t, '[\s\u00A0]|DA|DZD', '', 'gi');
    IF s ~ '^-?\d{1,3}(\.\d{3})+,\d+$' THEN          -- 1.234,50
        s := replace(replace(s, '.', ''), ',', '.');
    ELSIF s ~ '^-?\d+,\d+$' THEN                     -- 1234,50
        s := replace(s, ',', '.');
    END IF;
    RETURN s::numeric;
EXCEPTION WHEN others THEN
    RETURN NULL;
END;
$_$;


--
-- Name: rapprocher_produits(bigint); Type: FUNCTION; Schema: import; Owner: -
--

CREATE FUNCTION import.rapprocher_produits(p_session bigint) RETURNS void
    LANGUAGE plpgsql
    AS $$
BEGIN
    -- On garde les choix déjà validés à la main
    DELETE FROM import.correspondance_produit WHERE session_id = p_session AND NOT valide;

    INSERT INTO import.correspondance_produit (session_id, ref_source, produit_id, methode, score, valide)
    SELECT DISTINCT ON (s.ref_source) p_session, s.ref_source, p.id, 'code_barre', 1, true
    FROM import.produit_src s
    JOIN produit p ON p.code_barre = btrim(s.code_barre)
    WHERE s.session_id = p_session
    ORDER BY s.ref_source, p.id
    ON CONFLICT DO NOTHING;

    INSERT INTO import.correspondance_produit (session_id, ref_source, produit_id, methode, score, valide)
    SELECT DISTINCT ON (s.ref_source) p_session, s.ref_source, p.id, 'num_enregistrement', 1, true
    FROM import.produit_src s
    JOIN produit p ON p.num_enregistrement = btrim(s.num_enregistrement)
    WHERE s.session_id = p_session
    ORDER BY s.ref_source, p.id
    ON CONFLICT DO NOTHING;

    INSERT INTO import.correspondance_produit (session_id, ref_source, produit_id, methode, score, valide)
    SELECT DISTINCT ON (s.ref_source) p_session, s.ref_source, p.id, 'nom_approche',
           round(similarity(lower(p.nom_commercial || ' ' || COALESCE(p.dosage, '')),
                            lower(s.nom || ' ' || COALESCE(s.dosage, '')))::numeric, 3),
           false
    FROM import.produit_src s
    JOIN produit p
      ON similarity(lower(p.nom_commercial || ' ' || COALESCE(p.dosage, '')),
                    lower(s.nom || ' ' || COALESCE(s.dosage, ''))) >= 0.5
    WHERE s.session_id = p_session
    ORDER BY s.ref_source,
             similarity(lower(p.nom_commercial || ' ' || COALESCE(p.dosage, '')),
                        lower(s.nom || ' ' || COALESCE(s.dosage, ''))) DESC
    ON CONFLICT DO NOTHING;
END;
$$;


--
-- Name: a_permission(bigint, text); Type: FUNCTION; Schema: public; Owner: -
--

CREATE FUNCTION public.a_permission(p_utilisateur_id bigint, p_code text) RETURNS boolean
    LANGUAGE sql STABLE
    AS $$
    SELECT EXISTS (
        SELECT 1
        FROM utilisateur u
        JOIN role_permission rp ON rp.role_id = u.role_id
        WHERE u.id = p_utilisateur_id
          AND u.actif
          AND rp.permission_code = p_code
    );
$$;


--
-- Name: completer_reception_boite(); Type: FUNCTION; Schema: public; Owner: -
--

CREATE FUNCTION public.completer_reception_boite() RETURNS trigger
    LANGUAGE plpgsql
    AS $$
BEGIN
    IF NEW.reception_id IS NULL THEN
        SELECT reception_id INTO NEW.reception_id FROM lot WHERE id = NEW.lot_id;
    END IF;
    IF NEW.prix_achat_ht IS NULL THEN
        NEW.prix_achat_ht := COALESCE(
            (SELECT m.prix_achat_ht FROM mouvement_stock m
             WHERE m.lot_id = NEW.lot_id AND m.reception_id = NEW.reception_id AND m.type = 'entree' AND m.prix_achat_ht IS NOT NULL
             ORDER BY m.id DESC LIMIT 1),
            (SELECT prix_achat_ht FROM lot WHERE id = NEW.lot_id));
    END IF;
    RETURN NEW;
END;
$$;


--
-- Name: creer_boites_internes(bigint, integer, bigint, numeric); Type: FUNCTION; Schema: public; Owner: -
--

CREATE FUNCTION public.creer_boites_internes(p_lot_id bigint, p_nombre integer, p_reception_id bigint DEFAULT NULL::bigint, p_prix numeric DEFAULT NULL::numeric) RETURNS TABLE(o_boite_id bigint, o_code text)
    LANGUAGE plpgsql
    AS $$
DECLARE
    v_dispo   int;
    v_deja    int;
    v_base    text;
    i         int;
BEGIN
    SELECT quantite_disponible INTO v_dispo FROM lot WHERE id = p_lot_id FOR UPDATE;
    SELECT count(*) INTO v_deja FROM boite WHERE lot_id = p_lot_id AND statut = 'en_stock';
    IF p_nombre < 1 OR v_deja + p_nombre > v_dispo THEN
        RAISE EXCEPTION 'Lot % : % boîtes en stock, % déjà étiquetées, % demandées',
            p_lot_id, v_dispo, v_deja, p_nombre;
    END IF;

    FOR i IN 1..p_nombre LOOP
        v_base := '20' || lpad(nextval('seq_code_interne')::text, 10, '0');
        o_code := v_base || ean13_cle(v_base);
        INSERT INTO boite (lot_id, code_etiquette, reception_id, prix_achat_ht) VALUES (p_lot_id, o_code, p_reception_id, p_prix)
        RETURNING id INTO o_boite_id;
        RETURN NEXT;
    END LOOP;
END;
$$;


--
-- Name: ean13_cle(text); Type: FUNCTION; Schema: public; Owner: -
--

CREATE FUNCTION public.ean13_cle(p12 text) RETURNS character
    LANGUAGE plpgsql IMMUTABLE
    AS $_$
DECLARE
    somme int := 0;
    i     int;
BEGIN
    IF p12 !~ '^[0-9]{12}$' THEN
        RAISE EXCEPTION 'Il faut exactement 12 chiffres : %', p12;
    END IF;
    FOR i IN 1..12 LOOP
        somme := somme + substr(p12, i, 1)::int * CASE WHEN i % 2 = 0 THEN 3 ELSE 1 END;
    END LOOP;
    RETURN ((10 - somme % 10) % 10)::text;
END;
$_$;


--
-- Name: est_sous_registre(bigint); Type: FUNCTION; Schema: public; Owner: -
--

CREATE FUNCTION public.est_sous_registre(p_lot_id bigint) RETURNS boolean
    LANGUAGE sql STABLE
    AS $$
    SELECT p.classe IN ('psychotrope', 'stupefiant') FROM lot l JOIN produit p ON p.id = l.produit_id WHERE l.id = p_lot_id
$$;


--
-- Name: eviter_doublon_registre(); Type: FUNCTION; Schema: public; Owner: -
--

CREATE FUNCTION public.eviter_doublon_registre() RETURNS trigger
    LANGUAGE plpgsql
    AS $$
BEGIN
    IF NEW.type_mouvement = 'entree' AND NEW.mouvement_id IS NULL AND NEW.ligne_vente_id IS NULL
       AND EXISTS (SELECT 1 FROM registre_psychotrope r
                   WHERE r.lot_id = NEW.lot_id AND r.type_mouvement = 'entree' AND r.mouvement_id IS NOT NULL) THEN
        RETURN NULL;
    END IF;
    RETURN NEW;
END $$;


--
-- Name: garder_un_titulaire(); Type: FUNCTION; Schema: public; Owner: -
--

CREATE FUNCTION public.garder_un_titulaire() RETURNS trigger
    LANGUAGE plpgsql
    AS $$
BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM utilisateur u JOIN role r ON r.id = u.role_id
        WHERE r.code = 'titulaire' AND u.actif
    ) THEN
        RAISE EXCEPTION 'Il doit rester au moins un titulaire actif';
    END IF;
    RETURN NULL;
END;
$$;


--
-- Name: generer_alertes(); Type: FUNCTION; Schema: public; Owner: -
--

CREATE FUNCTION public.generer_alertes() RETURNS integer
    LANGUAGE plpgsql
    AS $$
DECLARE
    nb integer;
BEGIN
    -- Les alertes reportées dont le délai est passé redeviennent visibles
    UPDATE alerte SET statut = 'nouvelle', mise_a_jour_le = now()
    WHERE statut = 'reportee' AND reportee_jusqu_au <= current_date;

    DROP TABLE IF EXISTS _candidats;
    CREATE TEMP TABLE _candidats ON COMMIT DROP AS

    -- Péremption
    SELECT CASE r.niveau
               WHEN 'perime'            THEN 'perime'
               WHEN 'risque_ecoulement' THEN 'risque_ecoulement'
               ELSE 'peremption'
           END AS type,
           CASE r.niveau
               WHEN 'perime' THEN 'urgent'
               WHEN 'rouge'  THEN 'urgent'
               ELSE 'attention'
           END AS niveau,
           r.produit_id,
           r.lot_id,
           CASE r.niveau
               WHEN 'perime' THEN format('%s, lot %s : PÉRIMÉ depuis le %s, %s unités à retirer du rayon.',
                                  r.nom_commercial, r.numero_lot,
                                  to_char(r.date_peremption, 'DD/MM/YYYY'), r.quantite_disponible)
               ELSE format('%s, lot %s : expire le %s (%s j). %s unités sur %s risquent de ne pas être vendues.',
                           r.nom_commercial, r.numero_lot,
                           to_char(r.date_peremption, 'DD/MM/YYYY'), r.jours_restants,
                           r.qte_a_risque, r.quantite_disponible)
           END AS message,
           r.valeur_a_risque AS valeur_dzd
    FROM v_risque_peremption r
    WHERE r.niveau <> 'ok'

    UNION ALL

    -- État du stock
    SELECT CASE e.statut
               WHEN 'rupture'  THEN 'rupture'
               WHEN 'critique' THEN 'stock_critique'
               WHEN 'dormant'  THEN 'dormant'
               ELSE 'surstock'
           END,
           CASE e.statut
               WHEN 'rupture'  THEN 'urgent'
               WHEN 'critique' THEN 'attention'
               ELSE 'info'
           END,
           e.produit_id,
           NULL::bigint,
           CASE e.statut
               WHEN 'rupture'  THEN format('%s en rupture.%s Commande suggérée : %s unités.',
                                    e.nom_commercial,
                                    CASE WHEN e.en_tension THEN ' Produit en tension nationale.' ELSE '' END,
                                    e.qte_suggeree)
               WHEN 'critique' THEN format('%s : stock %s, sous le seuil de %s. %s Commande suggérée : %s unités.',
                                    e.nom_commercial, e.stock, e.seuil_alerte,
                                    CASE WHEN e.couverture_jours IS NULL THEN 'Aucune vente récente.'
                                         ELSE format('Couverture : %s jours.', e.couverture_jours) END,
                                    e.qte_suggeree)
               WHEN 'dormant'  THEN format('%s : %s unités sans vente récente.',
                                    e.nom_commercial, e.stock)
               ELSE format('%s : %s jours de stock, surstock.',
                           e.nom_commercial, e.couverture_jours)
           END,
           NULL::numeric
    FROM v_etat_stock e
    WHERE e.statut <> 'ok';

    -- Créer les nouvelles alertes ; mettre à jour celles qui existent.
    -- Si le niveau s'aggrave, l'alerte redevient « nouvelle » même si elle était reportée.
    INSERT INTO alerte AS a (type, niveau, produit_id, lot_id, message, valeur_dzd)
    SELECT type, niveau, produit_id, lot_id, message, valeur_dzd FROM _candidats
    ON CONFLICT (type, produit_id, COALESCE(lot_id, 0))
        WHERE statut IN ('nouvelle', 'vue', 'reportee')
    DO UPDATE SET
        message        = EXCLUDED.message,
        valeur_dzd     = EXCLUDED.valeur_dzd,
        statut         = CASE WHEN a.niveau <> EXCLUDED.niveau THEN 'nouvelle' ELSE a.statut END,
        niveau         = EXCLUDED.niveau,
        mise_a_jour_le = now();

    GET DIAGNOSTICS nb = ROW_COUNT;

    -- Clôture automatique des alertes dont la cause a disparu
    UPDATE alerte a
    SET statut = 'resolue', traitee_le = now(), mise_a_jour_le = now()
    WHERE a.statut IN ('nouvelle', 'vue', 'reportee')
      AND NOT EXISTS (
          SELECT 1 FROM _candidats c
          WHERE c.type = a.type
            AND c.produit_id = a.produit_id
            AND COALESCE(c.lot_id, 0) = COALESCE(a.lot_id, 0)
      );

    RETURN nb;
END;
$$;


--
-- Name: generer_code_interne(bigint); Type: FUNCTION; Schema: public; Owner: -
--

CREATE FUNCTION public.generer_code_interne(p_lot_id bigint) RETURNS text
    LANGUAGE plpgsql
    AS $$
DECLARE
    existant text;
    base     text;
    code     text;
BEGIN
    SELECT code_interne INTO existant FROM lot WHERE id = p_lot_id FOR UPDATE;
    IF NOT FOUND THEN
        RAISE EXCEPTION 'Lot % introuvable', p_lot_id;
    END IF;
    IF existant IS NOT NULL THEN
        RETURN existant;
    END IF;

    base := '20' || lpad(nextval('seq_code_interne')::text, 10, '0');
    code := base || ean13_cle(base);
    UPDATE lot SET code_interne = code WHERE id = p_lot_id;
    RETURN code;
END;
$$;


--
-- Name: inscrire_mouvement_registre(); Type: FUNCTION; Schema: public; Owner: -
--

CREATE FUNCTION public.inscrire_mouvement_registre() RETURNS trigger
    LANGUAGE plpgsql
    AS $$
DECLARE
    v_type  text;
    v_solde int;
BEGIN
    IF NEW.type = 'vente' OR NEW.quantite = 0 OR NOT coalesce(est_sous_registre(NEW.lot_id), false) THEN
        RETURN NEW;
    END IF;
    IF NEW.utilisateur_id IS NULL THEN
        RAISE EXCEPTION 'Mouvement d''un produit sous registre sans employé : inscription au registre impossible';
    END IF;
    v_type := CASE NEW.type
        WHEN 'entree'                THEN 'entree'
        WHEN 'retour_client'         THEN 'retour_client'
        WHEN 'retour_fournisseur'    THEN CASE WHEN NEW.quantite < 0 THEN 'retour_fournisseur' ELSE 'retour_refuse' END
        WHEN 'perime'                THEN 'destruction'
        WHEN 'destruction'           THEN 'destruction'
        WHEN 'casse'                 THEN 'casse'
        WHEN 'ajustement_inventaire' THEN CASE WHEN NEW.quantite < 0 THEN 'ecart_moins' ELSE 'ecart_plus' END
    END;
    IF v_type IS NULL THEN
        RAISE EXCEPTION 'Type de mouvement % non prévu au registre des psychotropes', NEW.type;
    END IF;
    SELECT greatest(quantite_disponible, 0) INTO v_solde FROM lot WHERE id = NEW.lot_id;
    INSERT INTO registre_psychotrope (lot_id, mouvement_id, type_mouvement, quantite, solde_apres, utilisateur_id, reference, cree_le)
    VALUES (NEW.lot_id, NEW.id, v_type, abs(NEW.quantite), coalesce(v_solde, 0), NEW.utilisateur_id, NEW.reference, NEW.cree_le);
    RETURN NEW;
END $$;


--
-- Name: interdire_modification(); Type: FUNCTION; Schema: public; Owner: -
--

CREATE FUNCTION public.interdire_modification() RETURNS trigger
    LANGUAGE plpgsql
    AS $$
BEGIN
    RAISE EXCEPTION 'Le registre des psychotropes ne peut être ni modifié ni supprimé';
END;
$$;


--
-- Name: interdire_modification_historique(); Type: FUNCTION; Schema: public; Owner: -
--

CREATE FUNCTION public.interdire_modification_historique() RETURNS trigger
    LANGUAGE plpgsql
    AS $$
BEGIN
    RAISE EXCEPTION 'L''historique de traçabilité ne peut être ni modifié ni supprimé';
END;
$$;


--
-- Name: interdire_modification_journal(); Type: FUNCTION; Schema: public; Owner: -
--

CREATE FUNCTION public.interdire_modification_journal() RETURNS trigger
    LANGUAGE plpgsql
    AS $$
BEGIN
    RAISE EXCEPTION 'Le journal des actions ne peut être ni modifié ni supprimé';
END;
$$;


--
-- Name: journaliser_entree_boite(); Type: FUNCTION; Schema: public; Owner: -
--

CREATE FUNCTION public.journaliser_entree_boite() RETURNS trigger
    LANGUAGE plpgsql
    AS $$
BEGIN
    INSERT INTO boite_evenement (boite_id, type, reception_id) VALUES (NEW.id, 'reception', NEW.reception_id);
    RETURN NULL;
END;
$$;


--
-- Name: lot_fefo(bigint); Type: FUNCTION; Schema: public; Owner: -
--

CREATE FUNCTION public.lot_fefo(p_produit_id bigint) RETURNS bigint
    LANGUAGE sql STABLE
    AS $$
    SELECT id FROM lot
    WHERE produit_id = p_produit_id
      AND quantite_disponible > 0
      AND date_peremption > current_date
    ORDER BY date_peremption, id
    LIMIT 1;
$$;


--
-- Name: majuscule_sans_accent(text); Type: FUNCTION; Schema: public; Owner: -
--

CREATE FUNCTION public.majuscule_sans_accent(t text) RETURNS text
    LANGUAGE sql IMMUTABLE
    AS $$
    SELECT translate(upper(coalesce(t, '')), 'ÀÂÄÁÃÇÉÈÊËÍÎÏÌÑÓÔÖÒÕÚÛÜÙÝŸ', 'AAAAACEEEEIIIINOOOOOUUUUYY')
$$;


--
-- Name: mouvement_boite(bigint, text, bigint, text, bigint); Type: FUNCTION; Schema: public; Owner: -
--

CREATE FUNCTION public.mouvement_boite(p_boite_id bigint, p_type text, p_utilisateur_id bigint, p_commentaire text DEFAULT NULL::text, p_retour_fournisseur_id bigint DEFAULT NULL::bigint) RETURNS void
    LANGUAGE plpgsql
    AS $$
DECLARE
    v_statut text := CASE p_type
        WHEN 'retour_client'      THEN 'retour_client'
        WHEN 'retour_fournisseur' THEN 'retournee_fournisseur'
        WHEN 'destruction'        THEN 'detruite'
    END;
BEGIN
    IF v_statut IS NULL THEN
        RAISE EXCEPTION 'Mouvement inconnu : %', p_type;
    END IF;
    UPDATE boite SET statut = v_statut WHERE id = p_boite_id;
    INSERT INTO boite_evenement (boite_id, type, retour_fournisseur_id, utilisateur_id, commentaire)
    VALUES (p_boite_id, p_type, p_retour_fournisseur_id, p_utilisateur_id, p_commentaire);
END;
$$;


--
-- Name: normaliser_numero_lot(); Type: FUNCTION; Schema: public; Owner: -
--

CREATE FUNCTION public.normaliser_numero_lot() RETURNS trigger
    LANGUAGE plpgsql
    AS $$
BEGIN
    NEW.numero_lot := upper(btrim(NEW.numero_lot));
    IF NEW.numero_lot = '' THEN
        RAISE EXCEPTION 'Numéro de lot vide';
    END IF;
    RETURN NEW;
END;
$$;


--
-- Name: numeroter_registre(); Type: FUNCTION; Schema: public; Owner: -
--

CREATE FUNCTION public.numeroter_registre() RETURNS trigger
    LANGUAGE plpgsql
    AS $$
BEGIN
    PERFORM pg_advisory_xact_lock(hashtext('registre_psychotrope'));
    SELECT coalesce(max(numero_ordre), 0) + 1 INTO NEW.numero_ordre FROM registre_psychotrope;
    RETURN NEW;
END $$;


--
-- Name: prochain_numero_commande(); Type: FUNCTION; Schema: public; Owner: -
--

CREATE FUNCTION public.prochain_numero_commande() RETURNS text
    LANGUAGE sql
    AS $$ SELECT 'C-' || lpad(nextval('seq_numero_commande')::text, 6, '0'); $$;


--
-- Name: prochain_numero_retour(); Type: FUNCTION; Schema: public; Owner: -
--

CREATE FUNCTION public.prochain_numero_retour() RETURNS text
    LANGUAGE sql
    AS $$ SELECT 'R-' || lpad(nextval('seq_numero_retour')::text, 6, '0'); $$;


--
-- Name: prochain_numero_vente(); Type: FUNCTION; Schema: public; Owner: -
--

CREATE FUNCTION public.prochain_numero_vente() RETURNS text
    LANGUAGE sql
    AS $$
    SELECT 'T-' || lpad(nextval('seq_numero_vente')::text, 6, '0');
$$;


--
-- Name: proteger_ligne_role_systeme(); Type: FUNCTION; Schema: public; Owner: -
--

CREATE FUNCTION public.proteger_ligne_role_systeme() RETURNS trigger
    LANGUAGE plpgsql
    AS $$
BEGIN
    IF OLD.systeme THEN
        RAISE EXCEPTION 'Le rôle titulaire ne peut être ni modifié ni supprimé';
    END IF;
    RETURN COALESCE(NEW, OLD);
END;
$$;


--
-- Name: proteger_role_systeme(); Type: FUNCTION; Schema: public; Owner: -
--

CREATE FUNCTION public.proteger_role_systeme() RETURNS trigger
    LANGUAGE plpgsql
    AS $$
DECLARE
    rid bigint := COALESCE(OLD.role_id, NEW.role_id);
BEGIN
    IF (SELECT systeme FROM role WHERE id = rid) THEN
        RAISE EXCEPTION 'Les droits du titulaire ne peuvent pas être modifiés';
    END IF;
    RETURN COALESCE(NEW, OLD);
END;
$$;


--
-- Name: proteger_session_fermee(); Type: FUNCTION; Schema: public; Owner: -
--

CREATE FUNCTION public.proteger_session_fermee() RETURNS trigger
    LANGUAGE plpgsql
    AS $$
BEGIN
    IF OLD.fermee_le IS NOT NULL THEN
        RAISE EXCEPTION 'Session de caisse déjà clôturée : elle ne peut plus être modifiée';
    END IF;
    RETURN NEW;
END;
$$;


--
-- Name: rappel_lot(bigint, text, text, bigint); Type: FUNCTION; Schema: public; Owner: -
--

CREATE FUNCTION public.rappel_lot(p_produit_id bigint, p_numero_lot text, p_motif text, p_utilisateur_id bigint) RETURNS TABLE(o_patient text, o_telephone text, o_date_vente timestamp with time zone, o_ticket text, o_boites bigint)
    LANGUAGE plpgsql
    AS $$
BEGIN
    -- Bloquer le lot chez tous les grossistes
    UPDATE lot SET bloque = true, motif_blocage = p_motif
    WHERE produit_id = p_produit_id AND numero_lot = p_numero_lot;

    INSERT INTO boite_evenement (boite_id, type, utilisateur_id, commentaire)
    SELECT b.id, 'blocage', p_utilisateur_id, p_motif
    FROM boite b JOIN lot l ON l.id = b.lot_id
    WHERE l.produit_id = p_produit_id AND l.numero_lot = p_numero_lot
      AND b.statut = 'en_stock';

    -- Ventes du lot : aussi celles d'avant l'étiquetage par boîte
    RETURN QUERY
    SELECT COALESCE(NULLIF(btrim(COALESCE(pt.nom, '') || ' ' || COALESCE(pt.prenom, '')), ''),
                    'Vente anonyme'),
           pt.telephone, v.date_vente, v.numero, SUM(lv.quantite)::bigint
    FROM ligne_vente lv
    JOIN vente v     ON v.id = lv.vente_id
    JOIN lot l       ON l.id = lv.lot_id
    LEFT JOIN patient pt ON pt.id = v.patient_id
    WHERE l.produit_id = p_produit_id AND l.numero_lot = p_numero_lot
      AND v.statut = 'validee'
    GROUP BY pt.nom, pt.prenom, pt.telephone, v.date_vente, v.numero
    ORDER BY v.date_vente DESC;
END;
$$;


--
-- Name: rechercher_par_code(text); Type: FUNCTION; Schema: public; Owner: -
--

CREATE FUNCTION public.rechercher_par_code(p_code text) RETURNS TABLE(produit_id bigint, lot_id bigint, source text)
    LANGUAGE plpgsql STABLE
    AS $$
BEGIN
    RETURN QUERY
        SELECT l.produit_id, l.id, 'etiquette_boite'::text
        FROM boite b JOIN lot l ON l.id = b.lot_id
        WHERE b.code_etiquette = p_code;
    IF FOUND THEN RETURN; END IF;

    RETURN QUERY
        SELECT l.produit_id, l.id, 'code_interne'::text
        FROM lot l
        WHERE l.code_interne = p_code;     -- même périmé : la caisse affichera
    IF FOUND THEN RETURN; END IF;          -- le blocage plutôt que « introuvable »

    RETURN QUERY
        SELECT p.id, lot_fefo(p.id), 'code_fabricant'::text
        FROM produit p
        WHERE p.code_barre = p_code AND p.actif;
END;
$$;


--
-- Name: sans_accent(text); Type: FUNCTION; Schema: public; Owner: -
--

CREATE FUNCTION public.sans_accent(t text) RETURNS text
    LANGUAGE sql IMMUTABLE STRICT PARALLEL SAFE
    RETURN translate(t, 'àâäáãçéèêëíìîïñóòôöõúùûüÿœÀÂÄÁÃÇÉÈÊËÍÌÎÏÑÓÒÔÖÕÚÙÛÜŸŒ'::text, 'aaaaaceeeeiiiinooooouuuuyoAAAAACEEEEIIIINOOOOOUUUUYO'::text);


--
-- Name: vendre_boite(bigint, bigint, bigint); Type: FUNCTION; Schema: public; Owner: -
--

CREATE FUNCTION public.vendre_boite(p_boite_id bigint, p_ligne_vente_id bigint, p_utilisateur_id bigint) RETURNS void
    LANGUAGE plpgsql
    AS $$
DECLARE
    b        record;
    lv       record;
    v_deja   int;
BEGIN
    SELECT * INTO b  FROM boite       WHERE id = p_boite_id FOR UPDATE;
    SELECT lv2.*, v.patient_id INTO lv
    FROM ligne_vente lv2 JOIN vente v ON v.id = lv2.vente_id
    WHERE lv2.id = p_ligne_vente_id;

    IF b.statut <> 'en_stock' THEN
        RAISE EXCEPTION 'Boîte déjà %, vente impossible : contrefaçon ou double scan possible', b.statut;
    END IF;
    IF b.lot_id <> lv.lot_id THEN
        RAISE EXCEPTION 'Cette boîte n''appartient pas au lot de la ligne de vente';
    END IF;
    SELECT count(*) INTO v_deja FROM boite WHERE ligne_vente_id = p_ligne_vente_id;
    IF v_deja >= lv.quantite THEN
        RAISE EXCEPTION 'Toutes les boîtes de cette ligne sont déjà scannées';
    END IF;

    UPDATE boite SET statut = 'vendue', ligne_vente_id = p_ligne_vente_id WHERE id = p_boite_id;
    INSERT INTO boite_evenement (boite_id, type, ligne_vente_id, patient_id, utilisateur_id)
    VALUES (p_boite_id, 'vente', p_ligne_vente_id, lv.patient_id, p_utilisateur_id);
END;
$$;


--
-- Name: verifier_destinataire_message(); Type: FUNCTION; Schema: public; Owner: -
--

CREATE FUNCTION public.verifier_destinataire_message() RETURNS trigger
    LANGUAGE plpgsql
    AS $$
DECLARE
    d destinataire_notification;
BEGIN
    SELECT * INTO d FROM destinataire_notification WHERE id = NEW.destinataire_id;
    IF NOT d.actif THEN
        RAISE EXCEPTION 'Destinataire désactivé : message refusé';
    END IF;
    IF NOT d.valide AND NEW.type <> 'test' THEN
        RAISE EXCEPTION 'Destinataire non vérifié : message refusé';
    END IF;
    IF (NEW.canal = 'email' AND NEW.adresse IS DISTINCT FROM d.email)
       OR (NEW.canal = 'sms' AND NEW.adresse IS DISTINCT FROM d.telephone) THEN
        RAISE EXCEPTION 'Adresse différente de celle du destinataire : message refusé';
    END IF;
    RETURN NEW;
END;
$$;


--
-- Name: verifier_fournisseur_lot(); Type: FUNCTION; Schema: public; Owner: -
--

CREATE FUNCTION public.verifier_fournisseur_lot() RETURNS trigger
    LANGUAGE plpgsql
    AS $$
BEGIN
    IF NEW.fournisseur_id IS DISTINCT FROM
       (SELECT fournisseur_id FROM reception WHERE id = NEW.reception_id) THEN
        RAISE EXCEPTION 'Le fournisseur du lot ne correspond pas à celui de la réception';
    END IF;
    RETURN NEW;
END;
$$;


--
-- Name: verifier_lot_vendable(); Type: FUNCTION; Schema: public; Owner: -
--

CREATE FUNCTION public.verifier_lot_vendable() RETURNS trigger
    LANGUAGE plpgsql
    AS $$
DECLARE
    d date;
BEGIN
    SELECT date_peremption INTO d FROM lot WHERE id = NEW.lot_id;
    IF d <= current_date THEN
        RAISE EXCEPTION 'Lot périmé depuis le %, vente interdite', to_char(d, 'DD/MM/YYYY');
    END IF;
    RETURN NEW;
END;
$$;


--
-- Name: verifier_retour_bon_fournisseur(); Type: FUNCTION; Schema: public; Owner: -
--

CREATE FUNCTION public.verifier_retour_bon_fournisseur() RETURNS trigger
    LANGUAGE plpgsql
    AS $$
DECLARE
    f_lot    bigint;
    f_retour bigint;
BEGIN
    SELECT fournisseur_id INTO f_lot    FROM lot                WHERE id = NEW.lot_id;
    SELECT fournisseur_id INTO f_retour FROM retour_fournisseur WHERE id = NEW.retour_id;
    IF f_lot <> f_retour THEN
        RAISE EXCEPTION 'Ce lot provient d''un autre grossiste : retour impossible sur ce bon';
    END IF;
    RETURN NEW;
END;
$$;


--
-- Name: verifier_tracabilite_vente(); Type: FUNCTION; Schema: public; Owner: -
--

CREATE FUNCTION public.verifier_tracabilite_vente() RETURNS trigger
    LANGUAGE plpgsql
    AS $$
DECLARE
    prm record;
BEGIN
    IF NEW.statut <> 'validee' OR (TG_OP = 'UPDATE' AND OLD.statut = 'validee') THEN
        RETURN NULL;
    END IF;
    SELECT * INTO prm FROM parametre_tracabilite WHERE id = 1;

    IF prm.boite_obligatoire AND EXISTS (
        SELECT 1 FROM ligne_vente lv
        WHERE lv.vente_id = NEW.id
          AND lv.quantite <> (SELECT count(*) FROM boite b WHERE b.ligne_vente_id = lv.id)
    ) THEN
        RAISE EXCEPTION 'Chaque boîte vendue doit être scannée';
    END IF;

    IF NEW.patient_id IS NULL AND (
           prm.patient_obligatoire = 'toujours'
        OR (prm.patient_obligatoire = 'ordonnance' AND NEW.ordonnance_id IS NOT NULL)
        OR (prm.patient_obligatoire IN ('ordonnance', 'psychotrope') AND EXISTS (
                SELECT 1 FROM ligne_vente lv JOIN produit p ON p.id = lv.produit_id
                WHERE lv.vente_id = NEW.id AND p.classe IN ('psychotrope', 'stupefiant')))
    ) THEN
        RAISE EXCEPTION 'Le patient doit être identifié pour cette vente';
    END IF;
    RETURN NULL;
END;
$$;


--
-- Name: verifier_zones_produit(); Type: FUNCTION; Schema: public; Owner: -
--

CREATE FUNCTION public.verifier_zones_produit() RETURNS trigger
    LANGUAGE plpgsql
    AS $$
BEGIN
    IF NEW.emplacement_id IS NOT NULL
       AND (SELECT zone FROM emplacement WHERE id = NEW.emplacement_id) <> 'vente' THEN
        RAISE EXCEPTION 'L''emplacement de vente doit être un emplacement en zone de vente';
    END IF;
    IF NEW.reserve_id IS NOT NULL
       AND (SELECT zone FROM emplacement WHERE id = NEW.reserve_id) <> 'reserve' THEN
        RAISE EXCEPTION 'L''emplacement de réserve doit être en zone de réserve';
    END IF;
    RETURN NEW;
END;
$$;


SET default_tablespace = '';

SET default_table_access_method = heap;

--
-- Name: anomalie; Type: TABLE; Schema: import; Owner: -
--

CREATE TABLE import.anomalie (
    id bigint NOT NULL,
    session_id bigint NOT NULL,
    entite text NOT NULL,
    ref_source text,
    gravite text NOT NULL,
    message text NOT NULL,
    CONSTRAINT anomalie_gravite_check CHECK ((gravite = ANY (ARRAY['bloquant'::text, 'avertissement'::text])))
);


--
-- Name: anomalie_id_seq; Type: SEQUENCE; Schema: import; Owner: -
--

ALTER TABLE import.anomalie ALTER COLUMN id ADD GENERATED ALWAYS AS IDENTITY (
    SEQUENCE NAME import.anomalie_id_seq
    START WITH 1
    INCREMENT BY 1
    NO MINVALUE
    NO MAXVALUE
    CACHE 1
);


--
-- Name: client_src; Type: TABLE; Schema: import; Owner: -
--

CREATE TABLE import.client_src (
    session_id bigint NOT NULL,
    ref_source text NOT NULL,
    nom text NOT NULL,
    prenom text,
    telephone text,
    num_assure text,
    solde_credit text,
    patient_id bigint
);


--
-- Name: correspondance_produit; Type: TABLE; Schema: import; Owner: -
--

CREATE TABLE import.correspondance_produit (
    session_id bigint NOT NULL,
    ref_source text NOT NULL,
    produit_id bigint,
    methode text NOT NULL,
    score numeric(4,3),
    valide boolean DEFAULT false NOT NULL,
    CONSTRAINT correspondance_produit_methode_check CHECK ((methode = ANY (ARRAY['code_barre'::text, 'num_enregistrement'::text, 'nom_approche'::text, 'manuel'::text, 'nouveau'::text])))
);


--
-- Name: fournisseur_src; Type: TABLE; Schema: import; Owner: -
--

CREATE TABLE import.fournisseur_src (
    session_id bigint NOT NULL,
    ref_source text NOT NULL,
    raison_sociale text NOT NULL,
    telephone text,
    solde text,
    fournisseur_id bigint
);


--
-- Name: produit_src; Type: TABLE; Schema: import; Owner: -
--

CREATE TABLE import.produit_src (
    session_id bigint NOT NULL,
    ref_source text NOT NULL,
    code_barre text,
    num_enregistrement text,
    nom text NOT NULL,
    dosage text,
    forme text,
    prix_vente text,
    prix_achat text
);


--
-- Name: session; Type: TABLE; Schema: import; Owner: -
--

CREATE TABLE import.session (
    id bigint NOT NULL,
    logiciel_source text NOT NULL,
    debut timestamp with time zone DEFAULT now() NOT NULL,
    statut text DEFAULT 'en_cours'::text NOT NULL,
    notes text,
    CONSTRAINT session_statut_check CHECK ((statut = ANY (ARRAY['en_cours'::text, 'charge'::text, 'annule'::text])))
);


--
-- Name: session_id_seq; Type: SEQUENCE; Schema: import; Owner: -
--

ALTER TABLE import.session ALTER COLUMN id ADD GENERATED ALWAYS AS IDENTITY (
    SEQUENCE NAME import.session_id_seq
    START WITH 1
    INCREMENT BY 1
    NO MINVALUE
    NO MAXVALUE
    CACHE 1
);


--
-- Name: stock_src; Type: TABLE; Schema: import; Owner: -
--

CREATE TABLE import.stock_src (
    id bigint NOT NULL,
    session_id bigint NOT NULL,
    ref_produit text NOT NULL,
    numero_lot text,
    date_peremption text,
    quantite text,
    fournisseur_ref text
);


--
-- Name: stock_src_id_seq; Type: SEQUENCE; Schema: import; Owner: -
--

ALTER TABLE import.stock_src ALTER COLUMN id ADD GENERATED ALWAYS AS IDENTITY (
    SEQUENCE NAME import.stock_src_id_seq
    START WITH 1
    INCREMENT BY 1
    NO MINVALUE
    NO MAXVALUE
    CACHE 1
);


--
-- Name: alerte; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public.alerte (
    id bigint NOT NULL,
    type text NOT NULL,
    niveau text NOT NULL,
    produit_id bigint NOT NULL,
    lot_id bigint,
    message text NOT NULL,
    valeur_dzd numeric(12,2),
    statut text DEFAULT 'nouvelle'::text NOT NULL,
    reportee_jusqu_au date,
    action_prise text,
    cree_le timestamp with time zone DEFAULT now() NOT NULL,
    mise_a_jour_le timestamp with time zone DEFAULT now() NOT NULL,
    traitee_par bigint,
    traitee_le timestamp with time zone,
    CONSTRAINT alerte_niveau_check CHECK ((niveau = ANY (ARRAY['info'::text, 'attention'::text, 'urgent'::text]))),
    CONSTRAINT alerte_statut_check CHECK ((statut = ANY (ARRAY['nouvelle'::text, 'vue'::text, 'reportee'::text, 'traitee'::text, 'resolue'::text]))),
    CONSTRAINT alerte_type_check CHECK ((type = ANY (ARRAY['peremption'::text, 'perime'::text, 'risque_ecoulement'::text, 'rupture'::text, 'stock_critique'::text, 'dormant'::text, 'surstock'::text])))
);


--
-- Name: alerte_id_seq; Type: SEQUENCE; Schema: public; Owner: -
--

ALTER TABLE public.alerte ALTER COLUMN id ADD GENERATED ALWAYS AS IDENTITY (
    SEQUENCE NAME public.alerte_id_seq
    START WITH 1
    INCREMENT BY 1
    NO MINVALUE
    NO MAXVALUE
    CACHE 1
);


--
-- Name: annulation_vente; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public.annulation_vente (
    id bigint NOT NULL,
    vente_id bigint NOT NULL,
    motif text NOT NULL,
    utilisateur_id bigint NOT NULL,
    valide_par bigint,
    ancienne boolean NOT NULL,
    mode text,
    montant_rembourse numeric(12,2) DEFAULT 0 NOT NULL,
    session_caisse_id bigint,
    poste text,
    cree_le timestamp with time zone DEFAULT now() NOT NULL,
    CONSTRAINT annulation_vente_check CHECK (((NOT ancienne) OR (valide_par IS NOT NULL))),
    CONSTRAINT annulation_vente_mode_check CHECK ((mode = ANY (ARRAY['especes'::text, 'carte'::text, 'cheque'::text, 'credit'::text]))),
    CONSTRAINT annulation_vente_montant_rembourse_check CHECK ((montant_rembourse >= (0)::numeric)),
    CONSTRAINT annulation_vente_motif_check CHECK ((length(btrim(motif)) >= 3))
);


--
-- Name: annulation_vente_id_seq; Type: SEQUENCE; Schema: public; Owner: -
--

ALTER TABLE public.annulation_vente ALTER COLUMN id ADD GENERATED ALWAYS AS IDENTITY (
    SEQUENCE NAME public.annulation_vente_id_seq
    START WITH 1
    INCREMENT BY 1
    NO MINVALUE
    NO MAXVALUE
    CACHE 1
);


--
-- Name: boite; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public.boite (
    id bigint NOT NULL,
    lot_id bigint NOT NULL,
    gtin text,
    numero_serie text,
    statut text DEFAULT 'en_stock'::text NOT NULL,
    ligne_vente_id bigint,
    scannee_le timestamp with time zone DEFAULT now() NOT NULL,
    code_etiquette text,
    reception_id bigint NOT NULL,
    prix_achat_ht numeric(12,2) NOT NULL,
    CONSTRAINT boite_prix_achat_ht_check CHECK ((prix_achat_ht >= (0)::numeric)),
    CONSTRAINT boite_statut_check CHECK ((statut = ANY (ARRAY['en_stock'::text, 'vendue'::text, 'retournee_fournisseur'::text, 'detruite'::text, 'retour_client'::text]))),
    CONSTRAINT ck_boite_identifiant CHECK ((((gtin IS NOT NULL) AND (numero_serie IS NOT NULL)) OR (code_etiquette IS NOT NULL)))
);


--
-- Name: boite_evenement; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public.boite_evenement (
    id bigint NOT NULL,
    boite_id bigint NOT NULL,
    type text NOT NULL,
    reception_id bigint,
    ligne_vente_id bigint,
    retour_fournisseur_id bigint,
    patient_id bigint,
    utilisateur_id bigint,
    commentaire text,
    cree_le timestamp with time zone DEFAULT now() NOT NULL,
    CONSTRAINT boite_evenement_type_check CHECK ((type = ANY (ARRAY['reception'::text, 'vente'::text, 'retour_client'::text, 'retour_fournisseur'::text, 'destruction'::text, 'blocage'::text, 'correction'::text])))
);


--
-- Name: boite_evenement_id_seq; Type: SEQUENCE; Schema: public; Owner: -
--

ALTER TABLE public.boite_evenement ALTER COLUMN id ADD GENERATED ALWAYS AS IDENTITY (
    SEQUENCE NAME public.boite_evenement_id_seq
    START WITH 1
    INCREMENT BY 1
    NO MINVALUE
    NO MAXVALUE
    CACHE 1
);


--
-- Name: boite_id_seq; Type: SEQUENCE; Schema: public; Owner: -
--

ALTER TABLE public.boite ALTER COLUMN id ADD GENERATED ALWAYS AS IDENTITY (
    SEQUENCE NAME public.boite_id_seq
    START WITH 1
    INCREMENT BY 1
    NO MINVALUE
    NO MAXVALUE
    CACHE 1
);


--
-- Name: bordereau; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public.bordereau (
    id bigint NOT NULL,
    organisme_id bigint NOT NULL,
    numero text NOT NULL,
    periode_debut date NOT NULL,
    periode_fin date NOT NULL,
    montant_total numeric(12,2) DEFAULT 0 NOT NULL,
    statut text DEFAULT 'ouvert'::text NOT NULL,
    date_envoi date,
    CONSTRAINT bordereau_check CHECK ((periode_fin >= periode_debut)),
    CONSTRAINT bordereau_statut_check CHECK ((statut = ANY (ARRAY['ouvert'::text, 'envoye'::text, 'paye'::text, 'rejet_partiel'::text])))
);


--
-- Name: bordereau_id_seq; Type: SEQUENCE; Schema: public; Owner: -
--

ALTER TABLE public.bordereau ALTER COLUMN id ADD GENERATED ALWAYS AS IDENTITY (
    SEQUENCE NAME public.bordereau_id_seq
    START WITH 1
    INCREMENT BY 1
    NO MINVALUE
    NO MAXVALUE
    CACHE 1
);


--
-- Name: campagne_inventaire; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public.campagne_inventaire (
    id bigint NOT NULL,
    mode text NOT NULL,
    statut text DEFAULT 'en_cours'::text NOT NULL,
    commence_le timestamp with time zone DEFAULT now() NOT NULL,
    commence_par bigint NOT NULL,
    cloture_le timestamp with time zone,
    cloture_par bigint,
    nombre_lots integer,
    nombre_boites integer,
    valeur_stock numeric(14,2),
    commentaire text,
    CONSTRAINT campagne_inventaire_mode_check CHECK ((mode = ANY (ARRAY['ouverte'::text, 'fermee'::text]))),
    CONSTRAINT campagne_inventaire_statut_check CHECK ((statut = ANY (ARRAY['en_cours'::text, 'cloturee'::text, 'annulee'::text])))
);


--
-- Name: campagne_inventaire_id_seq; Type: SEQUENCE; Schema: public; Owner: -
--

ALTER TABLE public.campagne_inventaire ALTER COLUMN id ADD GENERATED ALWAYS AS IDENTITY (
    SEQUENCE NAME public.campagne_inventaire_id_seq
    START WITH 1
    INCREMENT BY 1
    NO MINVALUE
    NO MAXVALUE
    CACHE 1
);


--
-- Name: campagne_inventaire_ligne; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public.campagne_inventaire_ligne (
    campagne_id bigint NOT NULL,
    lot_id bigint NOT NULL,
    produit text NOT NULL,
    numero_lot text NOT NULL,
    date_peremption date NOT NULL,
    rayon text NOT NULL,
    quantite integer NOT NULL,
    prix_achat_ht numeric(12,2) NOT NULL,
    valeur numeric(14,2) NOT NULL
);


--
-- Name: client_facture; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public.client_facture (
    id bigint NOT NULL,
    raison_sociale text NOT NULL,
    adresse text,
    telephone text,
    nif text,
    rc text,
    nis text,
    article_imposition text,
    actif boolean DEFAULT true NOT NULL,
    cree_le timestamp with time zone DEFAULT now() NOT NULL
);


--
-- Name: client_facture_id_seq; Type: SEQUENCE; Schema: public; Owner: -
--

ALTER TABLE public.client_facture ALTER COLUMN id ADD GENERATED ALWAYS AS IDENTITY (
    SEQUENCE NAME public.client_facture_id_seq
    START WITH 1
    INCREMENT BY 1
    NO MINVALUE
    NO MAXVALUE
    CACHE 1
);


--
-- Name: commande_fournisseur; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public.commande_fournisseur (
    id bigint NOT NULL,
    fournisseur_id bigint NOT NULL,
    numero text NOT NULL,
    date_commande timestamp with time zone DEFAULT now() NOT NULL,
    statut text DEFAULT 'brouillon'::text NOT NULL,
    montant_total numeric(12,2) DEFAULT 0 NOT NULL,
    utilisateur_id bigint,
    CONSTRAINT commande_fournisseur_statut_check CHECK ((statut = ANY (ARRAY['brouillon'::text, 'envoyee'::text, 'recue_partielle'::text, 'recue'::text, 'annulee'::text])))
);


--
-- Name: commande_fournisseur_id_seq; Type: SEQUENCE; Schema: public; Owner: -
--

ALTER TABLE public.commande_fournisseur ALTER COLUMN id ADD GENERATED ALWAYS AS IDENTITY (
    SEQUENCE NAME public.commande_fournisseur_id_seq
    START WITH 1
    INCREMENT BY 1
    NO MINVALUE
    NO MAXVALUE
    CACHE 1
);


--
-- Name: comptage_caisse; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public.comptage_caisse (
    session_id bigint NOT NULL,
    coupure text NOT NULL,
    nombre integer NOT NULL,
    montant numeric(12,2) NOT NULL,
    CONSTRAINT comptage_caisse_coupure_check CHECK ((coupure = ANY (ARRAY['b2000'::text, 'b1000'::text, 'b500'::text, 'b200'::text, 'p100'::text, 'autres'::text]))),
    CONSTRAINT comptage_caisse_montant_check CHECK ((montant >= (0)::numeric)),
    CONSTRAINT comptage_caisse_nombre_check CHECK ((nombre >= 0))
);


--
-- Name: compte_tresorerie; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public.compte_tresorerie (
    id bigint NOT NULL,
    type text NOT NULL,
    nom text NOT NULL,
    banque text,
    numero_compte text,
    recoit_carte boolean DEFAULT false NOT NULL,
    solde_initial numeric(12,2) DEFAULT 0 NOT NULL,
    date_initiale date DEFAULT CURRENT_DATE NOT NULL,
    actif boolean DEFAULT true NOT NULL,
    cree_le timestamp with time zone DEFAULT now() NOT NULL,
    CONSTRAINT compte_tresorerie_check CHECK (((type = 'banque'::text) OR (NOT recoit_carte))),
    CONSTRAINT compte_tresorerie_type_check CHECK ((type = ANY (ARRAY['coffre'::text, 'banque'::text])))
);


--
-- Name: compte_tresorerie_id_seq; Type: SEQUENCE; Schema: public; Owner: -
--

ALTER TABLE public.compte_tresorerie ALTER COLUMN id ADD GENERATED ALWAYS AS IDENTITY (
    SEQUENCE NAME public.compte_tresorerie_id_seq
    START WITH 1
    INCREMENT BY 1
    NO MINVALUE
    NO MAXVALUE
    CACHE 1
);


--
-- Name: dci; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public.dci (
    id bigint NOT NULL,
    nom text NOT NULL
);


--
-- Name: dci_id_seq; Type: SEQUENCE; Schema: public; Owner: -
--

ALTER TABLE public.dci ALTER COLUMN id ADD GENERATED ALWAYS AS IDENTITY (
    SEQUENCE NAME public.dci_id_seq
    START WITH 1
    INCREMENT BY 1
    NO MINVALUE
    NO MAXVALUE
    CACHE 1
);


--
-- Name: destinataire_notification; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public.destinataire_notification (
    id bigint NOT NULL,
    nom text NOT NULL,
    email text,
    telephone text,
    canal_email boolean DEFAULT false NOT NULL,
    canal_sms boolean DEFAULT false NOT NULL,
    types text[] DEFAULT ARRAY['rapport_quotidien'::text, 'ecart_caisse'::text, 'lot_bloque'::text, 'sauvegarde'::text] NOT NULL,
    valide boolean DEFAULT false NOT NULL,
    actif boolean DEFAULT true NOT NULL,
    cree_le timestamp with time zone DEFAULT now() NOT NULL,
    CONSTRAINT destinataire_notification_check CHECK (((NOT canal_email) OR (email IS NOT NULL))),
    CONSTRAINT destinataire_notification_check1 CHECK (((NOT canal_sms) OR (telephone IS NOT NULL))),
    CONSTRAINT destinataire_notification_types_check CHECK ((types <@ ARRAY['rapport_quotidien'::text, 'ecart_caisse'::text, 'lot_bloque'::text, 'sauvegarde'::text, 'ecart_inventaire'::text]))
);


--
-- Name: destinataire_notification_id_seq; Type: SEQUENCE; Schema: public; Owner: -
--

ALTER TABLE public.destinataire_notification ALTER COLUMN id ADD GENERATED ALWAYS AS IDENTITY (
    SEQUENCE NAME public.destinataire_notification_id_seq
    START WITH 1
    INCREMENT BY 1
    NO MINVALUE
    NO MAXVALUE
    CACHE 1
);


--
-- Name: emplacement; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public.emplacement (
    id bigint NOT NULL,
    code text NOT NULL,
    rayon text NOT NULL,
    etagere text,
    tiroir text,
    libelle text,
    zone text DEFAULT 'vente'::text NOT NULL,
    actif boolean DEFAULT true NOT NULL,
    cree_le timestamp with time zone DEFAULT now() NOT NULL,
    CONSTRAINT emplacement_check CHECK (((tiroir IS NULL) OR (etagere IS NOT NULL))),
    CONSTRAINT emplacement_code_check CHECK ((code = upper(code))),
    CONSTRAINT emplacement_zone_check CHECK ((zone = ANY (ARRAY['vente'::text, 'reserve'::text])))
);


--
-- Name: emplacement_id_seq; Type: SEQUENCE; Schema: public; Owner: -
--

ALTER TABLE public.emplacement ALTER COLUMN id ADD GENERATED ALWAYS AS IDENTITY (
    SEQUENCE NAME public.emplacement_id_seq
    START WITH 1
    INCREMENT BY 1
    NO MINVALUE
    NO MAXVALUE
    CACHE 1
);


--
-- Name: facture_client; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public.facture_client (
    id bigint NOT NULL,
    type text NOT NULL,
    numero text NOT NULL,
    date_facture date NOT NULL,
    destinataire text NOT NULL,
    patient_id bigint,
    client_id bigint,
    organisme_id bigint,
    destinataire_nom text NOT NULL,
    destinataire_infos text,
    periode_du date,
    periode_au date,
    montant_ht numeric(12,2) NOT NULL,
    montant_tva numeric(12,2) NOT NULL,
    montant_ttc numeric(12,2) NOT NULL,
    deja_paye numeric(12,2) DEFAULT 0 NOT NULL,
    statut text DEFAULT 'emise'::text NOT NULL,
    version integer DEFAULT 1 NOT NULL,
    motif_rejet text,
    remplace_id bigint,
    remplacee_par bigint,
    facture_origine_id bigint,
    motif text,
    hors_suivi boolean DEFAULT false NOT NULL,
    motif_hors_suivi text,
    commentaire text,
    utilisateur_id bigint NOT NULL,
    cree_le timestamp with time zone DEFAULT now() NOT NULL,
    CONSTRAINT facture_client_check CHECK (((destinataire <> 'patient'::text) OR (patient_id IS NOT NULL))),
    CONSTRAINT facture_client_check1 CHECK (((destinataire <> 'entreprise'::text) OR (client_id IS NOT NULL))),
    CONSTRAINT facture_client_check2 CHECK (((destinataire <> 'organisme'::text) OR (organisme_id IS NOT NULL))),
    CONSTRAINT facture_client_check3 CHECK (((type = 'facture'::text) OR (facture_origine_id IS NOT NULL))),
    CONSTRAINT facture_client_check4 CHECK (((NOT hors_suivi) OR (motif_hors_suivi IS NOT NULL))),
    CONSTRAINT facture_client_destinataire_check CHECK ((destinataire = ANY (ARRAY['patient'::text, 'entreprise'::text, 'organisme'::text]))),
    CONSTRAINT facture_client_statut_check CHECK ((statut = ANY (ARRAY['emise'::text, 'rejetee'::text, 'remplacee'::text]))),
    CONSTRAINT facture_client_type_check CHECK ((type = ANY (ARRAY['facture'::text, 'avoir'::text])))
);


--
-- Name: facture_client_id_seq; Type: SEQUENCE; Schema: public; Owner: -
--

ALTER TABLE public.facture_client ALTER COLUMN id ADD GENERATED ALWAYS AS IDENTITY (
    SEQUENCE NAME public.facture_client_id_seq
    START WITH 1
    INCREMENT BY 1
    NO MINVALUE
    NO MAXVALUE
    CACHE 1
);


--
-- Name: facture_client_ligne; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public.facture_client_ligne (
    id bigint NOT NULL,
    facture_id bigint NOT NULL,
    ordre integer NOT NULL,
    vente_id bigint,
    ligne_vente_id bigint,
    part text NOT NULL,
    designation text NOT NULL,
    quantite numeric(10,2) NOT NULL,
    prix_unitaire_ttc numeric(12,2) NOT NULL,
    taux_tva numeric(5,2) DEFAULT 0 NOT NULL,
    montant_ttc numeric(12,2) NOT NULL,
    CONSTRAINT facture_client_ligne_check CHECK (((part = 'libre'::text) OR (vente_id IS NOT NULL))),
    CONSTRAINT facture_client_ligne_part_check CHECK ((part = ANY (ARRAY['patient'::text, 'organisme'::text, 'libre'::text])))
);


--
-- Name: facture_client_ligne_id_seq; Type: SEQUENCE; Schema: public; Owner: -
--

ALTER TABLE public.facture_client_ligne ALTER COLUMN id ADD GENERATED ALWAYS AS IDENTITY (
    SEQUENCE NAME public.facture_client_ligne_id_seq
    START WITH 1
    INCREMENT BY 1
    NO MINVALUE
    NO MAXVALUE
    CACHE 1
);


--
-- Name: facture_client_version; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public.facture_client_version (
    facture_id bigint NOT NULL,
    version integer NOT NULL,
    contenu jsonb NOT NULL,
    motif text,
    utilisateur_id bigint NOT NULL,
    cree_le timestamp with time zone DEFAULT now() NOT NULL
);


--
-- Name: facture_compteur; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public.facture_compteur (
    type text NOT NULL,
    annee integer NOT NULL,
    dernier integer DEFAULT 0 NOT NULL,
    CONSTRAINT facture_compteur_type_check CHECK ((type = ANY (ARRAY['facture'::text, 'avoir'::text])))
);


--
-- Name: fournisseur; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public.fournisseur (
    id bigint NOT NULL,
    raison_sociale text NOT NULL,
    nif text,
    rc text,
    telephone text,
    adresse text,
    solde numeric(12,2) DEFAULT 0 NOT NULL,
    jours_orange integer,
    jours_rouge integer,
    delai_reprise_jours integer,
    CONSTRAINT ck_seuils_fournisseur CHECK (((jours_rouge IS NULL) OR (jours_orange IS NULL) OR (jours_rouge < jours_orange))),
    CONSTRAINT fournisseur_delai_reprise_jours_check CHECK ((delai_reprise_jours > 0)),
    CONSTRAINT fournisseur_jours_orange_check CHECK ((jours_orange > 0)),
    CONSTRAINT fournisseur_jours_rouge_check CHECK ((jours_rouge > 0))
);


--
-- Name: fournisseur_id_seq; Type: SEQUENCE; Schema: public; Owner: -
--

ALTER TABLE public.fournisseur ALTER COLUMN id ADD GENERATED ALWAYS AS IDENTITY (
    SEQUENCE NAME public.fournisseur_id_seq
    START WITH 1
    INCREMENT BY 1
    NO MINVALUE
    NO MAXVALUE
    CACHE 1
);


--
-- Name: inventaire; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public.inventaire (
    id bigint NOT NULL,
    rayon text NOT NULL,
    statut text DEFAULT 'en_cours'::text NOT NULL,
    commence_le timestamp with time zone DEFAULT now() NOT NULL,
    commence_par bigint NOT NULL,
    termine_le timestamp with time zone,
    termine_par bigint,
    campagne_id bigint,
    CONSTRAINT inventaire_statut_check CHECK ((statut = ANY (ARRAY['en_cours'::text, 'termine'::text, 'annule'::text])))
);


--
-- Name: inventaire_id_seq; Type: SEQUENCE; Schema: public; Owner: -
--

ALTER TABLE public.inventaire ALTER COLUMN id ADD GENERATED ALWAYS AS IDENTITY (
    SEQUENCE NAME public.inventaire_id_seq
    START WITH 1
    INCREMENT BY 1
    NO MINVALUE
    NO MAXVALUE
    CACHE 1
);


--
-- Name: journal_audit; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public.journal_audit (
    id bigint NOT NULL,
    utilisateur_id bigint,
    action text NOT NULL,
    table_cible text,
    enregistrement_id bigint,
    details jsonb,
    cree_le timestamp with time zone DEFAULT now() NOT NULL,
    permission_code text,
    autorise_par bigint
);


--
-- Name: journal_audit_id_seq; Type: SEQUENCE; Schema: public; Owner: -
--

ALTER TABLE public.journal_audit ALTER COLUMN id ADD GENERATED ALWAYS AS IDENTITY (
    SEQUENCE NAME public.journal_audit_id_seq
    START WITH 1
    INCREMENT BY 1
    NO MINVALUE
    NO MAXVALUE
    CACHE 1
);


--
-- Name: journal_sauvegarde; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public.journal_sauvegarde (
    id bigint NOT NULL,
    debut timestamp with time zone DEFAULT now() NOT NULL,
    fin timestamp with time zone,
    statut text NOT NULL,
    declenchement text NOT NULL,
    fichier text,
    taille_octets bigint,
    message text,
    utilisateur_id bigint,
    CONSTRAINT journal_sauvegarde_declenchement_check CHECK ((declenchement = ANY (ARRAY['automatique'::text, 'manuel'::text]))),
    CONSTRAINT journal_sauvegarde_statut_check CHECK ((statut = ANY (ARRAY['reussie'::text, 'echouee'::text, 'disque_absent'::text])))
);


--
-- Name: journal_sauvegarde_id_seq; Type: SEQUENCE; Schema: public; Owner: -
--

ALTER TABLE public.journal_sauvegarde ALTER COLUMN id ADD GENERATED ALWAYS AS IDENTITY (
    SEQUENCE NAME public.journal_sauvegarde_id_seq
    START WITH 1
    INCREMENT BY 1
    NO MINVALUE
    NO MAXVALUE
    CACHE 1
);


--
-- Name: laboratoire; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public.laboratoire (
    id bigint NOT NULL,
    nom text NOT NULL,
    pays text
);


--
-- Name: laboratoire_id_seq; Type: SEQUENCE; Schema: public; Owner: -
--

ALTER TABLE public.laboratoire ALTER COLUMN id ADD GENERATED ALWAYS AS IDENTITY (
    SEQUENCE NAME public.laboratoire_id_seq
    START WITH 1
    INCREMENT BY 1
    NO MINVALUE
    NO MAXVALUE
    CACHE 1
);


--
-- Name: ligne_commande; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public.ligne_commande (
    id bigint NOT NULL,
    commande_id bigint NOT NULL,
    produit_id bigint NOT NULL,
    qte_commandee integer NOT NULL,
    qte_recue integer DEFAULT 0 NOT NULL,
    prix_achat_ht numeric(12,2) NOT NULL,
    CONSTRAINT ligne_commande_prix_achat_ht_check CHECK ((prix_achat_ht >= (0)::numeric)),
    CONSTRAINT ligne_commande_qte_commandee_check CHECK ((qte_commandee > 0)),
    CONSTRAINT ligne_commande_qte_recue_check CHECK ((qte_recue >= 0))
);


--
-- Name: ligne_commande_id_seq; Type: SEQUENCE; Schema: public; Owner: -
--

ALTER TABLE public.ligne_commande ALTER COLUMN id ADD GENERATED ALWAYS AS IDENTITY (
    SEQUENCE NAME public.ligne_commande_id_seq
    START WITH 1
    INCREMENT BY 1
    NO MINVALUE
    NO MAXVALUE
    CACHE 1
);


--
-- Name: ligne_inventaire; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public.ligne_inventaire (
    id bigint NOT NULL,
    inventaire_id bigint NOT NULL,
    lot_id bigint NOT NULL,
    theorique integer,
    compte integer,
    date_verifiee date,
    compte_par bigint,
    compte_le timestamp with time zone,
    ecart integer,
    valeur_ecart numeric(12,2),
    statut text DEFAULT 'a_compter'::text NOT NULL,
    decide_par bigint,
    decide_le timestamp with time zone,
    CONSTRAINT ligne_inventaire_compte_check CHECK ((compte >= 0)),
    CONSTRAINT ligne_inventaire_statut_check CHECK ((statut = ANY (ARRAY['a_compter'::text, 'compte'::text, 'conforme'::text, 'ajuste'::text, 'a_valider'::text, 'valide'::text, 'refuse'::text])))
);


--
-- Name: ligne_inventaire_id_seq; Type: SEQUENCE; Schema: public; Owner: -
--

ALTER TABLE public.ligne_inventaire ALTER COLUMN id ADD GENERATED ALWAYS AS IDENTITY (
    SEQUENCE NAME public.ligne_inventaire_id_seq
    START WITH 1
    INCREMENT BY 1
    NO MINVALUE
    NO MAXVALUE
    CACHE 1
);


--
-- Name: ligne_retour_fournisseur; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public.ligne_retour_fournisseur (
    id bigint NOT NULL,
    retour_id bigint NOT NULL,
    lot_id bigint NOT NULL,
    quantite integer NOT NULL,
    CONSTRAINT ligne_retour_fournisseur_quantite_check CHECK ((quantite > 0))
);


--
-- Name: ligne_retour_fournisseur_id_seq; Type: SEQUENCE; Schema: public; Owner: -
--

ALTER TABLE public.ligne_retour_fournisseur ALTER COLUMN id ADD GENERATED ALWAYS AS IDENTITY (
    SEQUENCE NAME public.ligne_retour_fournisseur_id_seq
    START WITH 1
    INCREMENT BY 1
    NO MINVALUE
    NO MAXVALUE
    CACHE 1
);


--
-- Name: ligne_vente; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public.ligne_vente (
    id bigint NOT NULL,
    vente_id bigint NOT NULL,
    produit_id bigint NOT NULL,
    lot_id bigint NOT NULL,
    quantite integer NOT NULL,
    prix_unitaire numeric(12,2) NOT NULL,
    base_remboursement numeric(12,2) DEFAULT 0 NOT NULL,
    taux_remboursement numeric(5,2) DEFAULT 0 NOT NULL,
    montant_organisme numeric(12,2) DEFAULT 0 NOT NULL,
    montant_patient numeric(12,2) DEFAULT 0 NOT NULL,
    CONSTRAINT ligne_vente_quantite_check CHECK ((quantite > 0)),
    CONSTRAINT ligne_vente_taux_remboursement_check CHECK (((taux_remboursement >= (0)::numeric) AND (taux_remboursement <= (100)::numeric)))
);


--
-- Name: ligne_vente_id_seq; Type: SEQUENCE; Schema: public; Owner: -
--

ALTER TABLE public.ligne_vente ALTER COLUMN id ADD GENERATED ALWAYS AS IDENTITY (
    SEQUENCE NAME public.ligne_vente_id_seq
    START WITH 1
    INCREMENT BY 1
    NO MINVALUE
    NO MAXVALUE
    CACHE 1
);


--
-- Name: lot; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public.lot (
    id bigint NOT NULL,
    produit_id bigint NOT NULL,
    ligne_commande_id bigint,
    numero_lot text NOT NULL,
    date_peremption date NOT NULL,
    quantite_disponible integer DEFAULT 0 NOT NULL,
    prix_achat_ht numeric(12,2) DEFAULT 0 NOT NULL,
    reception_id bigint NOT NULL,
    fournisseur_id bigint NOT NULL,
    code_interne text,
    bloque boolean DEFAULT false NOT NULL,
    motif_blocage text,
    date_a_verifier boolean DEFAULT false NOT NULL,
    CONSTRAINT lot_quantite_disponible_check CHECK ((quantite_disponible >= 0))
);


--
-- Name: lot_id_seq; Type: SEQUENCE; Schema: public; Owner: -
--

ALTER TABLE public.lot ALTER COLUMN id ADD GENERATED ALWAYS AS IDENTITY (
    SEQUENCE NAME public.lot_id_seq
    START WITH 1
    INCREMENT BY 1
    NO MINVALUE
    NO MAXVALUE
    CACHE 1
);


--
-- Name: medecin; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public.medecin (
    id bigint NOT NULL,
    nom text NOT NULL,
    specialite text,
    code_prestataire text
);


--
-- Name: medecin_id_seq; Type: SEQUENCE; Schema: public; Owner: -
--

ALTER TABLE public.medecin ALTER COLUMN id ADD GENERATED ALWAYS AS IDENTITY (
    SEQUENCE NAME public.medecin_id_seq
    START WITH 1
    INCREMENT BY 1
    NO MINVALUE
    NO MAXVALUE
    CACHE 1
);


--
-- Name: message_sortant; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public.message_sortant (
    id bigint NOT NULL,
    canal text NOT NULL,
    destinataire_id bigint NOT NULL,
    adresse text NOT NULL,
    type text NOT NULL,
    objet text,
    corps text NOT NULL,
    corps_html text,
    statut text DEFAULT 'en_attente'::text NOT NULL,
    tentatives integer DEFAULT 0 NOT NULL,
    prochaine_tentative timestamp with time zone,
    derniere_erreur text,
    cree_le timestamp with time zone DEFAULT now() NOT NULL,
    envoye_le timestamp with time zone,
    CONSTRAINT message_sortant_canal_check CHECK ((canal = ANY (ARRAY['email'::text, 'sms'::text]))),
    CONSTRAINT message_sortant_statut_check CHECK ((statut = ANY (ARRAY['en_attente'::text, 'envoye'::text, 'echec'::text]))),
    CONSTRAINT message_sortant_type_check CHECK ((type = ANY (ARRAY['rapport_quotidien'::text, 'ecart_caisse'::text, 'lot_bloque'::text, 'sauvegarde'::text, 'ecart_inventaire'::text, 'test'::text])))
);


--
-- Name: message_sortant_id_seq; Type: SEQUENCE; Schema: public; Owner: -
--

ALTER TABLE public.message_sortant ALTER COLUMN id ADD GENERATED ALWAYS AS IDENTITY (
    SEQUENCE NAME public.message_sortant_id_seq
    START WITH 1
    INCREMENT BY 1
    NO MINVALUE
    NO MAXVALUE
    CACHE 1
);


--
-- Name: mouvement_stock; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public.mouvement_stock (
    id bigint NOT NULL,
    lot_id bigint NOT NULL,
    type text NOT NULL,
    quantite integer NOT NULL,
    reference text,
    utilisateur_id bigint,
    cree_le timestamp with time zone DEFAULT now() NOT NULL,
    reception_id bigint,
    prix_achat_ht numeric(12,2),
    CONSTRAINT mouvement_stock_type_check CHECK ((type = ANY (ARRAY['entree'::text, 'vente'::text, 'retour_client'::text, 'retour_fournisseur'::text, 'perime'::text, 'casse'::text, 'ajustement_inventaire'::text, 'destruction'::text])))
);


--
-- Name: mouvement_stock_id_seq; Type: SEQUENCE; Schema: public; Owner: -
--

ALTER TABLE public.mouvement_stock ALTER COLUMN id ADD GENERATED ALWAYS AS IDENTITY (
    SEQUENCE NAME public.mouvement_stock_id_seq
    START WITH 1
    INCREMENT BY 1
    NO MINVALUE
    NO MAXVALUE
    CACHE 1
);


--
-- Name: mouvement_tresorerie; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public.mouvement_tresorerie (
    id bigint NOT NULL,
    compte_id bigint NOT NULL,
    date_mouvement date NOT NULL,
    sens text NOT NULL,
    montant numeric(12,2) NOT NULL,
    nature text NOT NULL,
    categorie text,
    libelle text NOT NULL,
    reference text,
    organisme_id bigint,
    bordereau_id bigint,
    session_caisse_id bigint,
    pendant_session boolean DEFAULT false NOT NULL,
    automatique boolean DEFAULT false NOT NULL,
    transfert_de bigint,
    utilisateur_id bigint NOT NULL,
    cree_le timestamp with time zone DEFAULT now() NOT NULL,
    CONSTRAINT mouvement_tresorerie_check CHECK (((nature <> 'versement_organisme'::text) OR (organisme_id IS NOT NULL))),
    CONSTRAINT mouvement_tresorerie_check1 CHECK (((nature <> 'versement_caisse'::text) OR ((session_caisse_id IS NOT NULL) AND (sens = 'entree'::text)))),
    CONSTRAINT mouvement_tresorerie_check2 CHECK (((nature <> 'depense'::text) OR ((sens = 'sortie'::text) AND (categorie IS NOT NULL)))),
    CONSTRAINT mouvement_tresorerie_montant_check CHECK ((montant > (0)::numeric)),
    CONSTRAINT mouvement_tresorerie_nature_check CHECK ((nature = ANY (ARRAY['depense'::text, 'encaissement'::text, 'versement_organisme'::text, 'transfert'::text, 'versement_caisse'::text]))),
    CONSTRAINT mouvement_tresorerie_sens_check CHECK ((sens = ANY (ARRAY['entree'::text, 'sortie'::text])))
);


--
-- Name: mouvement_tresorerie_id_seq; Type: SEQUENCE; Schema: public; Owner: -
--

ALTER TABLE public.mouvement_tresorerie ALTER COLUMN id ADD GENERATED ALWAYS AS IDENTITY (
    SEQUENCE NAME public.mouvement_tresorerie_id_seq
    START WITH 1
    INCREMENT BY 1
    NO MINVALUE
    NO MAXVALUE
    CACHE 1
);


--
-- Name: nomenclature_produit; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public.nomenclature_produit (
    id bigint NOT NULL,
    version_id bigint NOT NULL,
    situation text NOT NULL,
    num_enregistrement text,
    code text,
    dci text,
    marque text,
    forme text,
    dosage text,
    conditionnement text,
    liste text,
    p1 text,
    p2 text,
    obs text,
    laboratoire text,
    pays text,
    date_initiale date,
    date_finale date,
    type text,
    statut text,
    stabilite text,
    CONSTRAINT nomenclature_produit_situation_check CHECK ((situation = ANY (ARRAY['en_vigueur'::text, 'non_renouvele'::text, 'retire'::text])))
);


--
-- Name: nomenclature_produit_id_seq; Type: SEQUENCE; Schema: public; Owner: -
--

ALTER TABLE public.nomenclature_produit ALTER COLUMN id ADD GENERATED ALWAYS AS IDENTITY (
    SEQUENCE NAME public.nomenclature_produit_id_seq
    START WITH 1
    INCREMENT BY 1
    NO MINVALUE
    NO MAXVALUE
    CACHE 1
);


--
-- Name: nomenclature_version; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public.nomenclature_version (
    id bigint NOT NULL,
    libelle text NOT NULL,
    fichier text NOT NULL,
    en_vigueur integer NOT NULL,
    non_renouveles integer NOT NULL,
    retraits integer NOT NULL,
    utilisateur_id bigint NOT NULL,
    importee_le timestamp with time zone DEFAULT now() NOT NULL
);


--
-- Name: nomenclature_version_id_seq; Type: SEQUENCE; Schema: public; Owner: -
--

ALTER TABLE public.nomenclature_version ALTER COLUMN id ADD GENERATED ALWAYS AS IDENTITY (
    SEQUENCE NAME public.nomenclature_version_id_seq
    START WITH 1
    INCREMENT BY 1
    NO MINVALUE
    NO MAXVALUE
    CACHE 1
);


--
-- Name: ordonnance; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public.ordonnance (
    id bigint NOT NULL,
    patient_id bigint NOT NULL,
    medecin_id bigint,
    date_prescription date NOT NULL,
    numero text,
    chemin_scan text,
    cree_le timestamp with time zone DEFAULT now() NOT NULL,
    texte_reconnu text
);


--
-- Name: ordonnance_id_seq; Type: SEQUENCE; Schema: public; Owner: -
--

ALTER TABLE public.ordonnance ALTER COLUMN id ADD GENERATED ALWAYS AS IDENTITY (
    SEQUENCE NAME public.ordonnance_id_seq
    START WITH 1
    INCREMENT BY 1
    NO MINVALUE
    NO MAXVALUE
    CACHE 1
);


--
-- Name: organisme; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public.organisme (
    id bigint NOT NULL,
    code text NOT NULL,
    nom text NOT NULL,
    adresse text,
    nif text,
    actif boolean DEFAULT true NOT NULL
);


--
-- Name: organisme_id_seq; Type: SEQUENCE; Schema: public; Owner: -
--

ALTER TABLE public.organisme ALTER COLUMN id ADD GENERATED ALWAYS AS IDENTITY (
    SEQUENCE NAME public.organisme_id_seq
    START WITH 1
    INCREMENT BY 1
    NO MINVALUE
    NO MAXVALUE
    CACHE 1
);


--
-- Name: paiement; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public.paiement (
    id bigint NOT NULL,
    vente_id bigint,
    patient_id bigint,
    mode text NOT NULL,
    montant numeric(12,2) NOT NULL,
    utilisateur_id bigint,
    cree_le timestamp with time zone DEFAULT now() NOT NULL,
    session_caisse_id bigint,
    CONSTRAINT paiement_check CHECK (((vente_id IS NOT NULL) OR (patient_id IS NOT NULL))),
    CONSTRAINT paiement_mode_check CHECK ((mode = ANY (ARRAY['especes'::text, 'carte'::text, 'cheque'::text, 'credit'::text, 'remboursement_credit'::text]))),
    CONSTRAINT paiement_montant_check CHECK ((montant > (0)::numeric))
);


--
-- Name: paiement_id_seq; Type: SEQUENCE; Schema: public; Owner: -
--

ALTER TABLE public.paiement ALTER COLUMN id ADD GENERATED ALWAYS AS IDENTITY (
    SEQUENCE NAME public.paiement_id_seq
    START WITH 1
    INCREMENT BY 1
    NO MINVALUE
    NO MAXVALUE
    CACHE 1
);


--
-- Name: panier_attente; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public.panier_attente (
    id bigint NOT NULL,
    poste text NOT NULL,
    utilisateur_id bigint NOT NULL,
    libelle text NOT NULL,
    lignes integer NOT NULL,
    total numeric(12,2) DEFAULT 0 NOT NULL,
    contenu jsonb NOT NULL,
    cree_le timestamp with time zone DEFAULT now() NOT NULL,
    CONSTRAINT panier_attente_lignes_check CHECK ((lignes > 0))
);


--
-- Name: panier_attente_id_seq; Type: SEQUENCE; Schema: public; Owner: -
--

ALTER TABLE public.panier_attente ALTER COLUMN id ADD GENERATED ALWAYS AS IDENTITY (
    SEQUENCE NAME public.panier_attente_id_seq
    START WITH 1
    INCREMENT BY 1
    NO MINVALUE
    NO MAXVALUE
    CACHE 1
);


--
-- Name: parametre_alerte; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public.parametre_alerte (
    id integer DEFAULT 1 NOT NULL,
    jours_orange integer DEFAULT 180 NOT NULL,
    jours_rouge integer DEFAULT 90 NOT NULL,
    horizon_risque_jours integer DEFAULT 365 NOT NULL,
    fenetre_conso_jours integer DEFAULT 90 NOT NULL,
    delai_fournisseur_jours integer DEFAULT 2 NOT NULL,
    jours_securite integer DEFAULT 7 NOT NULL,
    jours_cible_commande integer DEFAULT 30 NOT NULL,
    jours_dormant integer DEFAULT 90 NOT NULL,
    jours_surstock integer DEFAULT 180 NOT NULL,
    CONSTRAINT ck_parametre_alerte_positifs CHECK (((jours_orange > 0) AND (jours_rouge > 0) AND (horizon_risque_jours > 0) AND (fenetre_conso_jours > 0) AND (delai_fournisseur_jours >= 0) AND (jours_securite >= 0) AND (jours_cible_commande > 0) AND (jours_dormant > 0) AND (jours_surstock > 0))),
    CONSTRAINT ck_seuils_general CHECK ((jours_rouge < jours_orange)),
    CONSTRAINT parametre_alerte_id_check CHECK ((id = 1))
);


--
-- Name: parametre_caisse; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public.parametre_caisse (
    id integer DEFAULT 1 NOT NULL,
    comptage_aveugle boolean DEFAULT true NOT NULL,
    fond_caisse_defaut numeric(12,2) DEFAULT 5000 NOT NULL,
    seuil_ecart numeric(12,2) DEFAULT 500 NOT NULL,
    deconnexion_minutes integer DEFAULT 0 NOT NULL,
    versement_cloture text DEFAULT 'manuel'::text NOT NULL,
    CONSTRAINT ck_versement_cloture CHECK ((versement_cloture = ANY (ARRAY['manuel'::text, 'automatique'::text]))),
    CONSTRAINT parametre_caisse_deconnexion_minutes_check CHECK ((deconnexion_minutes >= 0)),
    CONSTRAINT parametre_caisse_fond_caisse_defaut_check CHECK ((fond_caisse_defaut >= (0)::numeric)),
    CONSTRAINT parametre_caisse_id_check CHECK ((id = 1)),
    CONSTRAINT parametre_caisse_seuil_ecart_check CHECK ((seuil_ecart >= (0)::numeric))
);


--
-- Name: parametre_inventaire; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public.parametre_inventaire (
    id integer DEFAULT 1 NOT NULL,
    seuil_boites integer DEFAULT 2 NOT NULL,
    seuil_montant numeric(12,2) DEFAULT 2000 NOT NULL,
    CONSTRAINT parametre_inventaire_id_check CHECK ((id = 1)),
    CONSTRAINT parametre_inventaire_seuil_boites_check CHECK ((seuil_boites >= 0)),
    CONSTRAINT parametre_inventaire_seuil_montant_check CHECK ((seuil_montant >= (0)::numeric))
);


--
-- Name: parametre_notification; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public.parametre_notification (
    id integer DEFAULT 1 NOT NULL,
    smtp_serveur text,
    smtp_port integer DEFAULT 587 NOT NULL,
    smtp_tls boolean DEFAULT true NOT NULL,
    smtp_expediteur text,
    smtp_identifiant text,
    smtp_mot_de_passe bytea,
    modem_port text,
    heure_rapport time without time zone DEFAULT '21:00:00'::time without time zone NOT NULL,
    dernier_rapport date,
    sms_seuil_ecart numeric(12,2),
    sms_lot_bloque boolean DEFAULT true NOT NULL,
    sms_sauvegarde boolean DEFAULT true NOT NULL,
    CONSTRAINT parametre_notification_id_check CHECK ((id = 1)),
    CONSTRAINT parametre_notification_sms_seuil_ecart_check CHECK ((sms_seuil_ecart >= (0)::numeric)),
    CONSTRAINT parametre_notification_smtp_port_check CHECK (((smtp_port >= 1) AND (smtp_port <= 65535)))
);


--
-- Name: parametre_sauvegarde; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public.parametre_sauvegarde (
    id integer DEFAULT 1 NOT NULL,
    actif boolean DEFAULT false NOT NULL,
    dossier text,
    heure time without time zone DEFAULT '22:00:00'::time without time zone NOT NULL,
    conservation integer DEFAULT 14 NOT NULL,
    derniere_execution date,
    CONSTRAINT parametre_sauvegarde_conservation_check CHECK (((conservation >= 1) AND (conservation <= 365))),
    CONSTRAINT parametre_sauvegarde_id_check CHECK ((id = 1))
);


--
-- Name: parametre_tracabilite; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public.parametre_tracabilite (
    id integer DEFAULT 1 NOT NULL,
    boite_obligatoire boolean DEFAULT false NOT NULL,
    patient_obligatoire text DEFAULT 'ordonnance'::text NOT NULL,
    CONSTRAINT parametre_tracabilite_id_check CHECK ((id = 1)),
    CONSTRAINT parametre_tracabilite_patient_obligatoire_check CHECK ((patient_obligatoire = ANY (ARRAY['jamais'::text, 'psychotrope'::text, 'ordonnance'::text, 'toujours'::text])))
);


--
-- Name: patient; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public.patient (
    id bigint NOT NULL,
    nom text NOT NULL,
    prenom text,
    date_naissance date,
    telephone text,
    num_assure text,
    organisme_id bigint,
    est_chronique boolean DEFAULT false NOT NULL,
    plafond_credit numeric(12,2) DEFAULT 0 NOT NULL,
    solde_credit numeric(12,2) DEFAULT 0 NOT NULL,
    cree_le timestamp with time zone DEFAULT now() NOT NULL
);


--
-- Name: patient_id_seq; Type: SEQUENCE; Schema: public; Owner: -
--

ALTER TABLE public.patient ALTER COLUMN id ADD GENERATED ALWAYS AS IDENTITY (
    SEQUENCE NAME public.patient_id_seq
    START WITH 1
    INCREMENT BY 1
    NO MINVALUE
    NO MAXVALUE
    CACHE 1
);


--
-- Name: permission; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public.permission (
    code text NOT NULL,
    module text NOT NULL,
    libelle text NOT NULL,
    sensible boolean DEFAULT false NOT NULL
);


--
-- Name: pharmacie; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public.pharmacie (
    id integer DEFAULT 1 NOT NULL,
    nom text,
    titulaire text,
    adresse text,
    telephone text,
    nif text,
    chifa_officine text,
    langue_defaut text DEFAULT 'fr'::text NOT NULL,
    rc text,
    nis text,
    article_imposition text,
    CONSTRAINT pharmacie_id_check CHECK ((id = 1)),
    CONSTRAINT pharmacie_langue_defaut_check CHECK ((langue_defaut = ANY (ARRAY['ar'::text, 'fr'::text, 'en'::text])))
);


--
-- Name: produit; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public.produit (
    id bigint NOT NULL,
    num_enregistrement text,
    code_barre text,
    nom_commercial text NOT NULL,
    dci_id bigint,
    laboratoire_id bigint,
    dosage text,
    forme text,
    conditionnement text,
    prix_public numeric(12,2) DEFAULT 0 NOT NULL,
    tarif_reference numeric(12,2),
    remboursable boolean DEFAULT false NOT NULL,
    taux_tva numeric(5,2) DEFAULT 0 NOT NULL,
    classe text DEFAULT 'libre'::text NOT NULL,
    est_essentiel boolean DEFAULT false NOT NULL,
    stock_minimum integer,
    actif boolean DEFAULT true NOT NULL,
    cree_le timestamp with time zone DEFAULT now() NOT NULL,
    en_tension boolean DEFAULT false NOT NULL,
    jours_orange integer,
    jours_rouge integer,
    emplacement_id bigint,
    reserve_id bigint,
    CONSTRAINT ck_seuils_produit CHECK (((jours_rouge IS NULL) OR (jours_orange IS NULL) OR (jours_rouge < jours_orange))),
    CONSTRAINT produit_classe_check CHECK ((classe = ANY (ARRAY['libre'::text, 'liste_i'::text, 'liste_ii'::text, 'psychotrope'::text, 'stupefiant'::text]))),
    CONSTRAINT produit_jours_orange_check CHECK ((jours_orange > 0)),
    CONSTRAINT produit_jours_rouge_check CHECK ((jours_rouge > 0)),
    CONSTRAINT produit_prix_public_check CHECK ((prix_public >= (0)::numeric)),
    CONSTRAINT produit_stock_minimum_positif CHECK (((stock_minimum IS NULL) OR ((stock_minimum >= 0) AND (stock_minimum <= 100000)))),
    CONSTRAINT produit_tarif_reference_check CHECK ((tarif_reference >= (0)::numeric))
);


--
-- Name: produit_id_seq; Type: SEQUENCE; Schema: public; Owner: -
--

ALTER TABLE public.produit ALTER COLUMN id ADD GENERATED ALWAYS AS IDENTITY (
    SEQUENCE NAME public.produit_id_seq
    START WITH 1
    INCREMENT BY 1
    NO MINVALUE
    NO MAXVALUE
    CACHE 1
);


--
-- Name: reception; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public.reception (
    id bigint NOT NULL,
    fournisseur_id bigint NOT NULL,
    commande_id bigint,
    numero_bl text,
    numero_facture text,
    date_reception timestamp with time zone DEFAULT now() NOT NULL,
    utilisateur_id bigint,
    montant_ttc numeric(12,2),
    CONSTRAINT reception_montant_ttc_check CHECK ((montant_ttc >= (0)::numeric))
);


--
-- Name: reception_id_seq; Type: SEQUENCE; Schema: public; Owner: -
--

ALTER TABLE public.reception ALTER COLUMN id ADD GENERATED ALWAYS AS IDENTITY (
    SEQUENCE NAME public.reception_id_seq
    START WITH 1
    INCREMENT BY 1
    NO MINVALUE
    NO MAXVALUE
    CACHE 1
);


--
-- Name: registre_psychotrope; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public.registre_psychotrope (
    id bigint NOT NULL,
    lot_id bigint NOT NULL,
    ligne_vente_id bigint,
    type_mouvement text NOT NULL,
    quantite integer NOT NULL,
    solde_apres integer NOT NULL,
    patient_id bigint,
    medecin_id bigint,
    ordonnance_id bigint,
    utilisateur_id bigint NOT NULL,
    cree_le timestamp with time zone DEFAULT now() NOT NULL,
    mouvement_id bigint,
    reference text,
    porteur_nom text,
    porteur_adresse text,
    porteur_piece text,
    numero_ordre bigint,
    CONSTRAINT ck_registre_porteur_complet CHECK (((porteur_nom IS NULL) OR ((porteur_adresse IS NOT NULL) AND (porteur_piece IS NOT NULL)))),
    CONSTRAINT registre_psychotrope_check CHECK (((type_mouvement <> 'sortie'::text) OR (ordonnance_id IS NOT NULL))),
    CONSTRAINT registre_psychotrope_quantite_check CHECK ((quantite > 0)),
    CONSTRAINT registre_psychotrope_solde_apres_check CHECK ((solde_apres >= 0)),
    CONSTRAINT registre_psychotrope_type_mouvement_check CHECK ((type_mouvement = ANY (ARRAY['ouverture'::text, 'entree'::text, 'retour_client'::text, 'retour_refuse'::text, 'ecart_plus'::text, 'sortie'::text, 'retour_fournisseur'::text, 'destruction'::text, 'casse'::text, 'ecart_moins'::text])))
);


--
-- Name: registre_psychotrope_id_seq; Type: SEQUENCE; Schema: public; Owner: -
--

ALTER TABLE public.registre_psychotrope ALTER COLUMN id ADD GENERATED ALWAYS AS IDENTITY (
    SEQUENCE NAME public.registre_psychotrope_id_seq
    START WITH 1
    INCREMENT BY 1
    NO MINVALUE
    NO MAXVALUE
    CACHE 1
);


--
-- Name: reglement_facture; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public.reglement_facture (
    reglement_id bigint NOT NULL,
    reception_id bigint NOT NULL,
    montant numeric(12,2) NOT NULL,
    CONSTRAINT reglement_facture_montant_check CHECK ((montant > (0)::numeric))
);


--
-- Name: reglement_facture_client; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public.reglement_facture_client (
    mouvement_id bigint NOT NULL,
    facture_id bigint NOT NULL,
    montant numeric(12,2) NOT NULL,
    CONSTRAINT reglement_facture_client_montant_check CHECK ((montant > (0)::numeric))
);


--
-- Name: reglement_fournisseur; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public.reglement_fournisseur (
    id bigint NOT NULL,
    fournisseur_id bigint NOT NULL,
    date_reglement date NOT NULL,
    montant numeric(12,2) NOT NULL,
    moyen text NOT NULL,
    reference text,
    banque text,
    echeance date,
    part_tiroir numeric(12,2) DEFAULT 0 NOT NULL,
    session_caisse_id bigint,
    utilisateur_id bigint NOT NULL,
    commentaire text,
    cree_le timestamp with time zone DEFAULT now() NOT NULL,
    compte_id bigint,
    CONSTRAINT reglement_fournisseur_check CHECK ((part_tiroir <= montant)),
    CONSTRAINT reglement_fournisseur_check1 CHECK (((part_tiroir = (0)::numeric) OR ((moyen = 'especes'::text) AND (session_caisse_id IS NOT NULL)))),
    CONSTRAINT reglement_fournisseur_check2 CHECK (((moyen <> 'effet'::text) OR (echeance IS NOT NULL))),
    CONSTRAINT reglement_fournisseur_montant_check CHECK ((montant > (0)::numeric)),
    CONSTRAINT reglement_fournisseur_moyen_check CHECK ((moyen = ANY (ARRAY['especes'::text, 'cheque'::text, 'virement'::text, 'effet'::text]))),
    CONSTRAINT reglement_fournisseur_part_tiroir_check CHECK ((part_tiroir >= (0)::numeric))
);


--
-- Name: reglement_fournisseur_id_seq; Type: SEQUENCE; Schema: public; Owner: -
--

ALTER TABLE public.reglement_fournisseur ALTER COLUMN id ADD GENERATED ALWAYS AS IDENTITY (
    SEQUENCE NAME public.reglement_fournisseur_id_seq
    START WITH 1
    INCREMENT BY 1
    NO MINVALUE
    NO MAXVALUE
    CACHE 1
);


--
-- Name: remise_cheque_paiement; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public.remise_cheque_paiement (
    remise_id bigint NOT NULL,
    paiement_id bigint NOT NULL
);


--
-- Name: remise_cheques; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public.remise_cheques (
    id bigint NOT NULL,
    compte_id bigint NOT NULL,
    date_remise date NOT NULL,
    reference text,
    utilisateur_id bigint NOT NULL,
    cree_le timestamp with time zone DEFAULT now() NOT NULL
);


--
-- Name: remise_cheques_id_seq; Type: SEQUENCE; Schema: public; Owner: -
--

ALTER TABLE public.remise_cheques ALTER COLUMN id ADD GENERATED ALWAYS AS IDENTITY (
    SEQUENCE NAME public.remise_cheques_id_seq
    START WITH 1
    INCREMENT BY 1
    NO MINVALUE
    NO MAXVALUE
    CACHE 1
);


--
-- Name: retour_client; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public.retour_client (
    id bigint NOT NULL,
    vente_id bigint NOT NULL,
    motif text NOT NULL,
    utilisateur_id bigint NOT NULL,
    valide_par bigint NOT NULL,
    mode text,
    montant_rembourse numeric(12,2) DEFAULT 0 NOT NULL,
    session_caisse_id bigint,
    poste text,
    cree_le timestamp with time zone DEFAULT now() NOT NULL,
    CONSTRAINT retour_client_mode_check CHECK ((mode = ANY (ARRAY['especes'::text, 'carte'::text, 'cheque'::text, 'credit'::text]))),
    CONSTRAINT retour_client_montant_rembourse_check CHECK ((montant_rembourse >= (0)::numeric)),
    CONSTRAINT retour_client_motif_check CHECK ((length(btrim(motif)) >= 3))
);


--
-- Name: retour_client_id_seq; Type: SEQUENCE; Schema: public; Owner: -
--

ALTER TABLE public.retour_client ALTER COLUMN id ADD GENERATED ALWAYS AS IDENTITY (
    SEQUENCE NAME public.retour_client_id_seq
    START WITH 1
    INCREMENT BY 1
    NO MINVALUE
    NO MAXVALUE
    CACHE 1
);


--
-- Name: retour_client_ligne; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public.retour_client_ligne (
    id bigint NOT NULL,
    retour_id bigint NOT NULL,
    ligne_vente_id bigint NOT NULL,
    quantite integer NOT NULL,
    destination text NOT NULL,
    montant numeric(12,2) NOT NULL,
    CONSTRAINT retour_client_ligne_destination_check CHECK ((destination = ANY (ARRAY['stock'::text, 'destruction'::text]))),
    CONSTRAINT retour_client_ligne_montant_check CHECK ((montant >= (0)::numeric)),
    CONSTRAINT retour_client_ligne_quantite_check CHECK ((quantite > 0))
);


--
-- Name: retour_client_ligne_id_seq; Type: SEQUENCE; Schema: public; Owner: -
--

ALTER TABLE public.retour_client_ligne ALTER COLUMN id ADD GENERATED ALWAYS AS IDENTITY (
    SEQUENCE NAME public.retour_client_ligne_id_seq
    START WITH 1
    INCREMENT BY 1
    NO MINVALUE
    NO MAXVALUE
    CACHE 1
);


--
-- Name: retour_fournisseur; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public.retour_fournisseur (
    id bigint NOT NULL,
    fournisseur_id bigint NOT NULL,
    numero text NOT NULL,
    motif text NOT NULL,
    statut text DEFAULT 'brouillon'::text NOT NULL,
    montant_avoir numeric(12,2),
    cree_le timestamp with time zone DEFAULT now() NOT NULL,
    utilisateur_id bigint,
    envoye_le timestamp with time zone,
    avoir_recu_le timestamp with time zone,
    montant_attendu numeric(12,2),
    CONSTRAINT retour_fournisseur_montant_attendu_check CHECK ((montant_attendu >= (0)::numeric)),
    CONSTRAINT retour_fournisseur_motif_check CHECK ((motif = ANY (ARRAY['peremption_proche'::text, 'perime'::text, 'defaut'::text, 'erreur_livraison'::text, 'rappel_lot'::text]))),
    CONSTRAINT retour_fournisseur_statut_check CHECK ((statut = ANY (ARRAY['brouillon'::text, 'envoye'::text, 'avoir_recu'::text, 'refuse'::text])))
);


--
-- Name: retour_fournisseur_id_seq; Type: SEQUENCE; Schema: public; Owner: -
--

ALTER TABLE public.retour_fournisseur ALTER COLUMN id ADD GENERATED ALWAYS AS IDENTITY (
    SEQUENCE NAME public.retour_fournisseur_id_seq
    START WITH 1
    INCREMENT BY 1
    NO MINVALUE
    NO MAXVALUE
    CACHE 1
);


--
-- Name: role; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public.role (
    id bigint NOT NULL,
    code text NOT NULL,
    nom text NOT NULL,
    systeme boolean DEFAULT false NOT NULL,
    remise_max_pct numeric(5,2) DEFAULT 0 NOT NULL,
    CONSTRAINT role_remise_max_pct_check CHECK (((remise_max_pct >= (0)::numeric) AND (remise_max_pct <= (100)::numeric)))
);


--
-- Name: role_id_seq; Type: SEQUENCE; Schema: public; Owner: -
--

ALTER TABLE public.role ALTER COLUMN id ADD GENERATED ALWAYS AS IDENTITY (
    SEQUENCE NAME public.role_id_seq
    START WITH 1
    INCREMENT BY 1
    NO MINVALUE
    NO MAXVALUE
    CACHE 1
);


--
-- Name: role_permission; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public.role_permission (
    role_id bigint NOT NULL,
    permission_code text NOT NULL
);


--
-- Name: scan_reception; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public.scan_reception (
    id bigint NOT NULL,
    utilisateur_id bigint NOT NULL,
    produit_id bigint NOT NULL,
    code_brut text NOT NULL,
    gtin text,
    numero_lot text,
    date_peremption date,
    numero_serie text,
    scanne_le timestamp with time zone DEFAULT now() NOT NULL,
    importe_le timestamp with time zone
);


--
-- Name: scan_reception_id_seq; Type: SEQUENCE; Schema: public; Owner: -
--

ALTER TABLE public.scan_reception ALTER COLUMN id ADD GENERATED ALWAYS AS IDENTITY (
    SEQUENCE NAME public.scan_reception_id_seq
    START WITH 1
    INCREMENT BY 1
    NO MINVALUE
    NO MAXVALUE
    CACHE 1
);


--
-- Name: seq_code_interne; Type: SEQUENCE; Schema: public; Owner: -
--

CREATE SEQUENCE public.seq_code_interne
    START WITH 1
    INCREMENT BY 1
    NO MINVALUE
    NO MAXVALUE
    CACHE 1;


--
-- Name: seq_numero_commande; Type: SEQUENCE; Schema: public; Owner: -
--

CREATE SEQUENCE public.seq_numero_commande
    START WITH 1
    INCREMENT BY 1
    NO MINVALUE
    NO MAXVALUE
    CACHE 1;


--
-- Name: seq_numero_retour; Type: SEQUENCE; Schema: public; Owner: -
--

CREATE SEQUENCE public.seq_numero_retour
    START WITH 1
    INCREMENT BY 1
    NO MINVALUE
    NO MAXVALUE
    CACHE 1;


--
-- Name: seq_numero_vente; Type: SEQUENCE; Schema: public; Owner: -
--

CREATE SEQUENCE public.seq_numero_vente
    START WITH 1
    INCREMENT BY 1
    NO MINVALUE
    NO MAXVALUE
    CACHE 1;


--
-- Name: session_caisse; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public.session_caisse (
    id bigint NOT NULL,
    utilisateur_id bigint NOT NULL,
    poste text NOT NULL,
    ouverte_le timestamp with time zone DEFAULT now() NOT NULL,
    fond_caisse numeric(12,2) NOT NULL,
    fermee_le timestamp with time zone,
    ventes_especes numeric(12,2),
    montant_attendu numeric(12,2),
    montant_compte numeric(12,2),
    ecart numeric(12,2),
    releve_de bigint,
    CONSTRAINT session_caisse_check CHECK (((fermee_le IS NULL) = (montant_compte IS NULL))),
    CONSTRAINT session_caisse_fond_caisse_check CHECK ((fond_caisse >= (0)::numeric))
);


--
-- Name: session_caisse_id_seq; Type: SEQUENCE; Schema: public; Owner: -
--

ALTER TABLE public.session_caisse ALTER COLUMN id ADD GENERATED ALWAYS AS IDENTITY (
    SEQUENCE NAME public.session_caisse_id_seq
    START WITH 1
    INCREMENT BY 1
    NO MINVALUE
    NO MAXVALUE
    CACHE 1
);


--
-- Name: substance_surveillee; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public.substance_surveillee (
    motif text NOT NULL,
    classe text NOT NULL,
    source text NOT NULL,
    CONSTRAINT substance_surveillee_classe_check CHECK ((classe = ANY (ARRAY['psychotrope'::text, 'stupefiant'::text])))
);


--
-- Name: utilisateur; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public.utilisateur (
    id bigint NOT NULL,
    nom_complet text NOT NULL,
    identifiant text NOT NULL,
    mot_de_passe_hash text NOT NULL,
    actif boolean DEFAULT true NOT NULL,
    cree_le timestamp with time zone DEFAULT now() NOT NULL,
    role_id bigint NOT NULL,
    pin_hash text,
    derniere_connexion timestamp with time zone,
    code_comptoir_essais integer DEFAULT 0 NOT NULL,
    code_comptoir_bloque_jusqu timestamp with time zone,
    affichage text DEFAULT 'auto'::text NOT NULL,
    doit_changer_mot_de_passe boolean DEFAULT false NOT NULL,
    CONSTRAINT ck_utilisateur_affichage CHECK ((affichage = ANY (ARRAY['auto'::text, 'clair'::text, 'sombre'::text])))
);


--
-- Name: utilisateur_id_seq; Type: SEQUENCE; Schema: public; Owner: -
--

ALTER TABLE public.utilisateur ALTER COLUMN id ADD GENERATED ALWAYS AS IDENTITY (
    SEQUENCE NAME public.utilisateur_id_seq
    START WITH 1
    INCREMENT BY 1
    NO MINVALUE
    NO MAXVALUE
    CACHE 1
);


--
-- Name: vente; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public.vente (
    id bigint NOT NULL,
    numero text NOT NULL,
    date_vente timestamp with time zone DEFAULT now() NOT NULL,
    utilisateur_id bigint NOT NULL,
    patient_id bigint,
    ordonnance_id bigint,
    type text NOT NULL,
    organisme_id bigint,
    bordereau_id bigint,
    montant_total numeric(12,2) DEFAULT 0 NOT NULL,
    part_organisme numeric(12,2) DEFAULT 0 NOT NULL,
    part_patient numeric(12,2) DEFAULT 0 NOT NULL,
    statut text DEFAULT 'en_cours'::text NOT NULL,
    num_facture_chifa text,
    session_caisse_id bigint,
    remise_pct numeric(5,2) DEFAULT 0 NOT NULL,
    montant_remise numeric(12,2) DEFAULT 0 NOT NULL,
    taux_tiers_payant numeric(5,2),
    CONSTRAINT vente_check CHECK (((type = 'libre'::text) OR ((organisme_id IS NOT NULL) AND (patient_id IS NOT NULL)))),
    CONSTRAINT vente_montant_remise_check CHECK ((montant_remise >= (0)::numeric)),
    CONSTRAINT vente_remise_pct_check CHECK (((remise_pct >= (0)::numeric) AND (remise_pct <= (100)::numeric))),
    CONSTRAINT vente_statut_check CHECK ((statut = ANY (ARRAY['en_cours'::text, 'validee'::text, 'annulee'::text]))),
    CONSTRAINT vente_taux_tiers_payant_check CHECK (((taux_tiers_payant >= (0)::numeric) AND (taux_tiers_payant <= (100)::numeric))),
    CONSTRAINT vente_type_check CHECK ((type = ANY (ARRAY['libre'::text, 'tiers_payant'::text])))
);


--
-- Name: v_consommation; Type: VIEW; Schema: public; Owner: -
--

CREATE VIEW public.v_consommation AS
 WITH prm AS (
         SELECT parametre_alerte.fenetre_conso_jours AS f
           FROM public.parametre_alerte
          WHERE (parametre_alerte.id = 1)
        ), ventes AS (
         SELECT lv.produit_id,
            sum(lv.quantite) AS qte
           FROM ((public.ligne_vente lv
             JOIN public.vente v ON ((v.id = lv.vente_id)))
             CROSS JOIN prm prm_1)
          WHERE ((v.statut = 'validee'::text) AND (v.date_vente >= (now() - make_interval(days => prm_1.f))))
          GROUP BY lv.produit_id
        )
 SELECT p.id AS produit_id,
    ((COALESCE(ventes.qte, (0)::bigint))::numeric / (prm.f)::numeric) AS conso_jour
   FROM ((public.produit p
     CROSS JOIN prm)
     LEFT JOIN ventes ON ((ventes.produit_id = p.id)));


--
-- Name: v_etat_stock; Type: VIEW; Schema: public; Owner: -
--

CREATE VIEW public.v_etat_stock AS
 WITH prm AS (
         SELECT parametre_alerte.id,
            parametre_alerte.jours_orange,
            parametre_alerte.jours_rouge,
            parametre_alerte.horizon_risque_jours,
            parametre_alerte.fenetre_conso_jours,
            parametre_alerte.delai_fournisseur_jours,
            parametre_alerte.jours_securite,
            parametre_alerte.jours_cible_commande,
            parametre_alerte.jours_dormant,
            parametre_alerte.jours_surstock
           FROM public.parametre_alerte
          WHERE (parametre_alerte.id = 1)
        ), stock AS (
         SELECT lot.produit_id,
            sum(lot.quantite_disponible) AS qte
           FROM public.lot
          WHERE (lot.date_peremption > CURRENT_DATE)
          GROUP BY lot.produit_id
        ), derniere AS (
         SELECT lv.produit_id,
            max(v.date_vente) AS derniere_vente
           FROM (public.ligne_vente lv
             JOIN public.vente v ON ((v.id = lv.vente_id)))
          WHERE (v.statut = 'validee'::text)
          GROUP BY lv.produit_id
        ), base AS (
         SELECT p.id AS produit_id,
            p.nom_commercial,
            p.en_tension,
            COALESCE(s.qte, (0)::bigint) AS stock,
            c.conso_jour,
            d.derniere_vente,
            (COALESCE((p.stock_minimum)::numeric, ceil((c.conso_jour * ((prm.delai_fournisseur_jours + prm.jours_securite))::numeric))))::integer AS seuil_alerte,
            (ceil((c.conso_jour * (prm.jours_cible_commande)::numeric)))::integer AS stock_cible,
            prm.jours_dormant,
            prm.jours_surstock
           FROM ((((public.produit p
             CROSS JOIN prm)
             JOIN public.v_consommation c ON ((c.produit_id = p.id)))
             LEFT JOIN stock s ON ((s.produit_id = p.id)))
             LEFT JOIN derniere d ON ((d.produit_id = p.id)))
          WHERE p.actif
        )
 SELECT produit_id,
    nom_commercial,
    en_tension,
    stock,
    round(conso_jour, 2) AS conso_jour,
        CASE
            WHEN (conso_jour > (0)::numeric) THEN round(((stock)::numeric / conso_jour))
            ELSE NULL::numeric
        END AS couverture_jours,
    seuil_alerte,
    derniere_vente,
        CASE
            WHEN ((stock = 0) AND (seuil_alerte > 0)) THEN 'rupture'::text
            WHEN (stock < seuil_alerte) THEN 'critique'::text
            WHEN ((stock > 0) AND ((derniere_vente IS NULL) OR (derniere_vente < (now() - make_interval(days => jours_dormant))))) THEN 'dormant'::text
            WHEN ((conso_jour > (0)::numeric) AND (((stock)::numeric / conso_jour) > (jours_surstock)::numeric)) THEN 'surstock'::text
            ELSE 'ok'::text
        END AS statut,
    GREATEST((0)::bigint, (GREATEST(stock_cible, seuil_alerte) - stock)) AS qte_suggeree
   FROM base;


--
-- Name: v_alternatives; Type: VIEW; Schema: public; Owner: -
--

CREATE VIEW public.v_alternatives AS
 SELECT p.id AS produit_id,
    alt.id AS alternative_id,
    alt.nom_commercial AS alternative,
    e.stock AS stock_alternative
   FROM ((public.produit p
     JOIN public.produit alt ON (((alt.dci_id = p.dci_id) AND (NOT (alt.dosage IS DISTINCT FROM p.dosage)) AND (NOT (alt.forme IS DISTINCT FROM p.forme)) AND (alt.id <> p.id) AND alt.actif)))
     JOIN public.v_etat_stock e ON ((e.produit_id = alt.id)))
  WHERE (e.stock > 0);


--
-- Name: v_caisse_a_verser; Type: VIEW; Schema: public; Owner: -
--

CREATE VIEW public.v_caisse_a_verser AS
 SELECT s.id AS session_caisse_id,
    s.poste,
    s.fermee_le,
    u.nom_complet AS ouverte_par,
    COALESCE(s.montant_compte, s.montant_attendu) AS compte,
    GREATEST((COALESCE(s.montant_compte, s.montant_attendu) - ( SELECT parametre_caisse.fond_caisse_defaut
           FROM public.parametre_caisse
          WHERE (parametre_caisse.id = 1))), (0)::numeric) AS propose
   FROM (public.session_caisse s
     JOIN public.utilisateur u ON ((u.id = s.utilisateur_id)))
  WHERE ((s.fermee_le IS NOT NULL) AND ((s.fermee_le)::date >= ( SELECT compte_tresorerie.date_initiale
           FROM public.compte_tresorerie
          WHERE (compte_tresorerie.type = 'coffre'::text))) AND (NOT (EXISTS ( SELECT 1
           FROM public.session_caisse n
          WHERE (n.releve_de = s.id)))) AND (NOT (EXISTS ( SELECT 1
           FROM public.mouvement_tresorerie m
          WHERE ((m.session_caisse_id = s.id) AND (NOT m.pendant_session))))));


--
-- Name: v_cheque_a_remettre; Type: VIEW; Schema: public; Owner: -
--

CREATE VIEW public.v_cheque_a_remettre AS
 SELECT p.id AS paiement_id,
    p.cree_le,
    p.montant,
    COALESCE(v.numero, 'Règlement de crédit'::text) AS origine,
    NULLIF(TRIM(BOTH FROM concat_ws(' '::text, pa.nom, pa.prenom)), ''::text) AS patient
   FROM ((public.paiement p
     LEFT JOIN public.vente v ON ((v.id = p.vente_id)))
     LEFT JOIN public.patient pa ON ((pa.id = COALESCE(v.patient_id, p.patient_id))))
  WHERE ((p.mode = 'cheque'::text) AND ((p.vente_id IS NULL) OR (v.statut = 'validee'::text)) AND (NOT (EXISTS ( SELECT 1
           FROM public.remise_cheque_paiement r
          WHERE (r.paiement_id = p.id)))));


--
-- Name: v_facture_client; Type: VIEW; Schema: public; Owner: -
--

CREATE VIEW public.v_facture_client AS
 SELECT f.id,
    f.type,
    f.numero,
    f.date_facture,
    f.destinataire,
    f.patient_id,
    f.client_id,
    f.organisme_id,
    f.destinataire_nom,
    f.destinataire_infos,
    f.periode_du,
    f.periode_au,
    f.montant_ht,
    f.montant_tva,
    f.montant_ttc,
    f.deja_paye,
    f.statut,
    f.version,
    f.motif_rejet,
    f.remplace_id,
    f.remplacee_par,
    f.facture_origine_id,
    f.motif,
    f.hors_suivi,
    f.motif_hors_suivi,
    f.commentaire,
    f.utilisateur_id,
    f.cree_le,
    COALESCE(( SELECT sum(a.montant_ttc) AS sum
           FROM public.facture_client a
          WHERE ((a.facture_origine_id = f.id) AND (a.type = 'avoir'::text))), (0)::numeric) AS avoirs,
    COALESCE(( SELECT sum(r.montant) AS sum
           FROM public.reglement_facture_client r
          WHERE (r.facture_id = f.id)), (0)::numeric) AS regle,
        CASE
            WHEN ((f.type = 'avoir'::text) OR (f.statut = 'remplacee'::text)) THEN (0)::numeric
            ELSE GREATEST((((f.montant_ttc - f.deja_paye) - COALESCE(( SELECT sum(a.montant_ttc) AS sum
               FROM public.facture_client a
              WHERE ((a.facture_origine_id = f.id) AND (a.type = 'avoir'::text))), (0)::numeric)) - COALESCE(( SELECT sum(r.montant) AS sum
               FROM public.reglement_facture_client r
              WHERE (r.facture_id = f.id)), (0)::numeric)), (0)::numeric)
        END AS reste,
    u.nom_complet AS employe,
    ( SELECT x.numero
           FROM public.facture_client x
          WHERE (x.id = f.remplace_id)) AS remplace_numero,
    ( SELECT x.numero
           FROM public.facture_client x
          WHERE (x.id = f.remplacee_par)) AS remplacee_par_numero,
    ( SELECT x.numero
           FROM public.facture_client x
          WHERE (x.id = f.facture_origine_id)) AS origine_numero
   FROM (public.facture_client f
     JOIN public.utilisateur u ON ((u.id = f.utilisateur_id)));


--
-- Name: v_facture_fournisseur; Type: VIEW; Schema: public; Owner: -
--

CREATE VIEW public.v_facture_fournisseur AS
 SELECT r.id AS reception_id,
    r.fournisseur_id,
    f.raison_sociale AS fournisseur,
    r.numero_facture,
    r.numero_bl,
    r.date_reception,
    COALESCE(r.montant_ttc, (0)::numeric) AS montant,
    COALESCE(( SELECT sum(x.montant) AS sum
           FROM public.reglement_facture x
          WHERE (x.reception_id = r.id)), (0)::numeric) AS regle,
    (COALESCE(r.montant_ttc, (0)::numeric) - COALESCE(( SELECT sum(x.montant) AS sum
           FROM public.reglement_facture x
          WHERE (x.reception_id = r.id)), (0)::numeric)) AS reste
   FROM (public.reception r
     JOIN public.fournisseur f ON ((f.id = r.fournisseur_id)));


--
-- Name: v_ligne_vente_facturable; Type: VIEW; Schema: public; Owner: -
--

CREATE VIEW public.v_ligne_vente_facturable AS
 SELECT v.id AS vente_id,
    v.numero,
    v.date_vente,
    v.patient_id,
    v.organisme_id,
    v.ordonnance_id,
    o.numero AS ordonnance_numero,
    l.id AS ligne_vente_id,
    p.nom_commercial AS designation,
    p.taux_tva,
    l.prix_unitaire,
    l.quantite,
    (l.quantite - COALESCE(rt.quantite, (0)::bigint)) AS quantite_nette,
    round(((l.montant_patient * ((l.quantite - COALESCE(rt.quantite, (0)::bigint)))::numeric) / (l.quantite)::numeric), 2) AS montant_patient,
    round(((l.montant_organisme * ((l.quantite - COALESCE(rt.quantite, (0)::bigint)))::numeric) / (l.quantite)::numeric), 2) AS montant_organisme,
    v.montant_remise,
    (EXISTS ( SELECT 1
           FROM (public.facture_client_ligne fl
             JOIN public.facture_client f ON ((f.id = fl.facture_id)))
          WHERE ((fl.ligne_vente_id = l.id) AND (fl.part = 'patient'::text) AND (f.statut = ANY (ARRAY['emise'::text, 'rejetee'::text])) AND (f.type = 'facture'::text)))) AS facturee_patient,
    (EXISTS ( SELECT 1
           FROM (public.facture_client_ligne fl
             JOIN public.facture_client f ON ((f.id = fl.facture_id)))
          WHERE ((fl.ligne_vente_id = l.id) AND (fl.part = 'organisme'::text) AND (f.statut = ANY (ARRAY['emise'::text, 'rejetee'::text])) AND (f.type = 'facture'::text)))) AS facturee_organisme
   FROM ((((public.vente v
     JOIN public.ligne_vente l ON ((l.vente_id = v.id)))
     JOIN public.produit p ON ((p.id = l.produit_id)))
     LEFT JOIN public.ordonnance o ON ((o.id = v.ordonnance_id)))
     LEFT JOIN LATERAL ( SELECT sum(r.quantite) AS quantite
           FROM public.retour_client_ligne r
          WHERE (r.ligne_vente_id = l.id)) rt ON (true))
  WHERE ((v.statut = 'validee'::text) AND (l.quantite > 0));


--
-- Name: v_lots_a_etiqueter; Type: VIEW; Schema: public; Owner: -
--

CREATE VIEW public.v_lots_a_etiqueter AS
 SELECT l.id AS lot_id,
    p.nom_commercial,
    l.numero_lot,
    l.quantite_disponible,
    (l.quantite_disponible - count(b.id) FILTER (WHERE (b.statut = 'en_stock'::text))) AS boites_sans_identite
   FROM ((public.lot l
     JOIN public.produit p ON ((p.id = l.produit_id)))
     LEFT JOIN public.boite b ON ((b.lot_id = l.id)))
  WHERE (l.quantite_disponible > 0)
  GROUP BY l.id, p.nom_commercial, l.numero_lot, l.quantite_disponible
 HAVING ((l.quantite_disponible - count(b.id) FILTER (WHERE (b.statut = 'en_stock'::text))) > 0);


--
-- Name: v_lots_date_a_verifier; Type: VIEW; Schema: public; Owner: -
--

CREATE VIEW public.v_lots_date_a_verifier AS
 SELECT l.id AS lot_id,
    p.nom_commercial,
    l.numero_lot,
    l.quantite_disponible,
    f.raison_sociale AS fournisseur
   FROM ((public.lot l
     JOIN public.produit p ON ((p.id = l.produit_id)))
     JOIN public.fournisseur f ON ((f.id = l.fournisseur_id)))
  WHERE (l.date_a_verifier AND (l.quantite_disponible > 0))
  ORDER BY p.nom_commercial;


--
-- Name: v_mouvement_tresorerie; Type: VIEW; Schema: public; Owner: -
--

CREATE VIEW public.v_mouvement_tresorerie AS
SELECT
    NULL::bigint AS compte_id,
    NULL::date AS date,
    NULL::text AS sens,
    NULL::numeric AS montant,
    NULL::text AS nature,
    NULL::text AS categorie,
    NULL::text AS libelle,
    NULL::text AS reference,
    NULL::text AS employe,
    NULL::text AS origine,
    NULL::bigint AS source_id,
    NULL::timestamp with time zone AS cree_le;


--
-- Name: v_nomenclature; Type: VIEW; Schema: public; Owner: -
--

CREATE VIEW public.v_nomenclature AS
 SELECT id,
    version_id,
    situation,
    num_enregistrement,
    code,
    dci,
    marque,
    forme,
    dosage,
    conditionnement,
    liste,
    p1,
    p2,
    obs,
    laboratoire,
    pays,
    date_initiale,
    date_finale,
    type,
    statut,
    stabilite,
    COALESCE(( SELECT s.classe
           FROM public.substance_surveillee s
          WHERE (public.majuscule_sans_accent(n.dci) ~~ (('%'::text || s.motif) || '%'::text))
          ORDER BY s.classe DESC
         LIMIT 1),
        CASE
            WHEN (upper(btrim(liste)) = ANY (ARRAY['LISTE I'::text, 'LISTE 1'::text])) THEN 'liste_i'::text
            WHEN (upper(btrim(liste)) = ANY (ARRAY['LISTE II'::text, 'LISTE 2'::text])) THEN 'liste_ii'::text
            ELSE 'libre'::text
        END) AS classe_proposee
   FROM public.nomenclature_produit n;


--
-- Name: v_parcours_boite; Type: VIEW; Schema: public; Owner: -
--

CREATE VIEW public.v_parcours_boite AS
 SELECT b.id AS boite_id,
    COALESCE(b.code_etiquette, ((b.gtin || ' / '::text) || b.numero_serie)) AS identifiant,
    p.nom_commercial,
    l.numero_lot,
    l.date_peremption,
    f.raison_sociale AS fournisseur,
    r.numero_bl,
    r.date_reception,
    b.statut,
    l.bloque,
    v.numero AS ticket,
    v.date_vente,
    v.num_facture_chifa,
    NULLIF(btrim(((COALESCE(pt.nom, ''::text) || ' '::text) || COALESCE(pt.prenom, ''::text))), ''::text) AS patient,
    pt.telephone
   FROM (((((((public.boite b
     JOIN public.lot l ON ((l.id = b.lot_id)))
     JOIN public.produit p ON ((p.id = l.produit_id)))
     JOIN public.fournisseur f ON ((f.id = l.fournisseur_id)))
     JOIN public.reception r ON ((r.id = b.reception_id)))
     LEFT JOIN public.ligne_vente lv ON ((lv.id = b.ligne_vente_id)))
     LEFT JOIN public.vente v ON ((v.id = lv.vente_id)))
     LEFT JOIN public.patient pt ON ((pt.id = v.patient_id)));


--
-- Name: v_produit_a_verifier; Type: VIEW; Schema: public; Owner: -
--

CREATE VIEW public.v_produit_a_verifier AS
 SELECT p.id AS produit_id,
    p.nom_commercial,
    p.dosage,
    p.forme,
    p.classe,
    d.nom AS dci,
    p.num_enregistrement,
    COALESCE(sd.classe, n.classe_proposee) AS classe_proposee,
    n.situation,
        CASE
            WHEN ((COALESCE(sd.classe, n.classe_proposee) = ANY (ARRAY['psychotrope'::text, 'stupefiant'::text])) AND (p.classe <> ALL (ARRAY['psychotrope'::text, 'stupefiant'::text]))) THEN 'classe'::text
            WHEN (n.situation IS NULL) THEN 'absent'::text
            ELSE 'situation'::text
        END AS motif
   FROM (((public.produit p
     LEFT JOIN public.dci d ON ((d.id = p.dci_id)))
     LEFT JOIN LATERAL ( SELECT s.classe
           FROM public.substance_surveillee s
          WHERE (public.majuscule_sans_accent(d.nom) ~~ (('%'::text || s.motif) || '%'::text))
          ORDER BY s.classe DESC
         LIMIT 1) sd ON (true))
     LEFT JOIN LATERAL ( SELECT v.classe_proposee,
            v.situation
           FROM public.v_nomenclature v
          WHERE (v.num_enregistrement = p.num_enregistrement)
          ORDER BY (v.situation = 'en_vigueur'::text) DESC
         LIMIT 1) n ON (true))
  WHERE (p.actif AND (((COALESCE(sd.classe, n.classe_proposee) = ANY (ARRAY['psychotrope'::text, 'stupefiant'::text])) AND (p.classe <> ALL (ARRAY['psychotrope'::text, 'stupefiant'::text]))) OR (n.situation = ANY (ARRAY['retire'::text, 'non_renouvele'::text])) OR ((n.situation IS NULL) AND (NULLIF(TRIM(BOTH FROM p.num_enregistrement), ''::text) IS NOT NULL) AND (EXISTS ( SELECT 1
           FROM public.nomenclature_produit)))));


--
-- Name: v_registre_psychotrope; Type: VIEW; Schema: public; Owner: -
--

CREATE VIEW public.v_registre_psychotrope AS
 WITH base AS (
         SELECT r.id,
            r.lot_id,
            r.ligne_vente_id,
            r.type_mouvement,
            r.quantite,
            r.solde_apres,
            r.patient_id,
            r.medecin_id,
            r.ordonnance_id,
            r.utilisateur_id,
            r.cree_le,
            r.mouvement_id,
            r.reference,
            r.porteur_nom,
            r.porteur_adresse,
            r.porteur_piece,
            r.numero_ordre,
            l.produit_id,
            l.numero_lot,
                CASE
                    WHEN (r.type_mouvement = ANY (ARRAY['ouverture'::text, 'entree'::text, 'retour_client'::text, 'retour_refuse'::text, 'ecart_plus'::text])) THEN r.quantite
                    ELSE (- r.quantite)
                END AS signe,
            min(r.numero_ordre) FILTER (WHERE (r.type_mouvement = 'ouverture'::text)) OVER (PARTITION BY l.produit_id) AS ouverture_id
           FROM (public.registre_psychotrope r
             JOIN public.lot l ON ((l.id = r.lot_id)))
        )
 SELECT b.numero_ordre AS numero,
    b.cree_le,
    b.produit_id,
    p.nom_commercial AS produit,
    p.classe,
    b.lot_id,
    b.numero_lot,
    b.type_mouvement,
        CASE
            WHEN (b.signe > 0) THEN b.quantite
            ELSE NULL::integer
        END AS entree,
        CASE
            WHEN (b.signe < 0) THEN b.quantite
            ELSE NULL::integer
        END AS sortie,
    (
        CASE
            WHEN (b.numero_ordre >= COALESCE(b.ouverture_id, (0)::bigint)) THEN sum(
            CASE
                WHEN (b.numero_ordre >= COALESCE(b.ouverture_id, (0)::bigint)) THEN b.signe
                ELSE 0
            END) OVER (PARTITION BY b.produit_id ORDER BY b.numero_ordre)
            ELSE NULL::bigint
        END)::integer AS solde_produit,
    b.solde_apres AS solde_lot,
    b.patient_id,
    TRIM(BOTH FROM concat_ws(' '::text, pa.nom, pa.prenom)) AS patient,
    m.nom AS medecin,
    o.numero AS ordonnance,
    o.date_prescription,
    f.raison_sociale AS fournisseur,
    COALESCE(v.numero, b.reference) AS reference,
    b.porteur_nom,
    b.porteur_adresse,
    b.porteur_piece,
    u.nom_complet AS employe
   FROM ((((((((((base b
     JOIN public.produit p ON ((p.id = b.produit_id)))
     JOIN public.utilisateur u ON ((u.id = b.utilisateur_id)))
     LEFT JOIN public.patient pa ON ((pa.id = b.patient_id)))
     LEFT JOIN public.medecin m ON ((m.id = b.medecin_id)))
     LEFT JOIN public.ordonnance o ON ((o.id = b.ordonnance_id)))
     LEFT JOIN public.ligne_vente lv ON ((lv.id = b.ligne_vente_id)))
     LEFT JOIN public.vente v ON ((v.id = lv.vente_id)))
     LEFT JOIN public.mouvement_stock ms ON ((ms.id = b.mouvement_id)))
     LEFT JOIN public.reception rc ON ((rc.id = ms.reception_id)))
     LEFT JOIN public.fournisseur f ON ((f.id = rc.fournisseur_id)));


--
-- Name: v_valeur_lot; Type: VIEW; Schema: public; Owner: -
--

CREATE VIEW public.v_valeur_lot AS
 SELECT l.id AS lot_id,
    l.quantite_disponible,
    round(
        CASE
            WHEN (l.quantite_disponible <= 0) THEN (0)::numeric
            WHEN (COALESCE(b.nb, (0)::bigint) >= l.quantite_disponible) THEN (b.prix_moyen * (l.quantite_disponible)::numeric)
            ELSE (COALESCE(b.somme, (0)::numeric) + (((l.quantite_disponible - COALESCE(b.nb, (0)::bigint)))::numeric * l.prix_achat_ht))
        END, 2) AS valeur,
        CASE
            WHEN (l.quantite_disponible > 0) THEN round(
            CASE
                WHEN (COALESCE(b.nb, (0)::bigint) >= l.quantite_disponible) THEN b.prix_moyen
                ELSE ((COALESCE(b.somme, (0)::numeric) + (((l.quantite_disponible - COALESCE(b.nb, (0)::bigint)))::numeric * l.prix_achat_ht)) / (l.quantite_disponible)::numeric)
            END, 2)
            ELSE l.prix_achat_ht
        END AS prix_unitaire
   FROM (public.lot l
     LEFT JOIN ( SELECT boite.lot_id,
            count(*) AS nb,
            sum(boite.prix_achat_ht) AS somme,
            avg(boite.prix_achat_ht) AS prix_moyen
           FROM public.boite
          WHERE (boite.statut = 'en_stock'::text)
          GROUP BY boite.lot_id) b ON ((b.lot_id = l.id)));


--
-- Name: v_risque_peremption; Type: VIEW; Schema: public; Owner: -
--

CREATE VIEW public.v_risque_peremption AS
 WITH prm AS (
         SELECT parametre_alerte.id,
            parametre_alerte.jours_orange,
            parametre_alerte.jours_rouge,
            parametre_alerte.horizon_risque_jours,
            parametre_alerte.fenetre_conso_jours,
            parametre_alerte.delai_fournisseur_jours,
            parametre_alerte.jours_securite,
            parametre_alerte.jours_cible_commande,
            parametre_alerte.jours_dormant,
            parametre_alerte.jours_surstock
           FROM public.parametre_alerte
          WHERE (parametre_alerte.id = 1)
        ), lots AS (
         SELECT l.id AS lot_id,
            l.produit_id,
            p.nom_commercial,
            l.numero_lot,
            l.date_peremption,
            l.quantite_disponible,
            COALESCE(vl.prix_unitaire, l.prix_achat_ht) AS prix_achat_ht,
            c.conso_jour,
            (l.date_peremption - CURRENT_DATE) AS jours_restants,
            (sum(l.quantite_disponible) OVER (PARTITION BY l.produit_id ORDER BY l.date_peremption, l.id) - l.quantite_disponible) AS qte_avant,
            l.fournisseur_id,
            f.raison_sociale AS fournisseur,
            COALESCE(p.jours_orange, f.jours_orange, prm.jours_orange) AS seuil_orange,
            COALESCE(p.jours_rouge, f.jours_rouge, prm.jours_rouge) AS seuil_rouge,
            prm.horizon_risque_jours
           FROM (((((public.lot l
             CROSS JOIN prm)
             JOIN public.produit p ON ((p.id = l.produit_id)))
             JOIN public.fournisseur f ON ((f.id = l.fournisseur_id)))
             JOIN public.v_consommation c ON ((c.produit_id = l.produit_id)))
             LEFT JOIN public.v_valeur_lot vl ON ((vl.lot_id = l.id)))
          WHERE (l.quantite_disponible > 0)
        ), calc AS (
         SELECT lots.lot_id,
            lots.produit_id,
            lots.nom_commercial,
            lots.numero_lot,
            lots.date_peremption,
            lots.quantite_disponible,
            lots.prix_achat_ht,
            lots.conso_jour,
            lots.jours_restants,
            lots.qte_avant,
            lots.fournisseur_id,
            lots.fournisseur,
            lots.seuil_orange,
            lots.seuil_rouge,
            lots.horizon_risque_jours,
            (GREATEST((0)::numeric, LEAST((lots.quantite_disponible)::numeric, (floor((lots.conso_jour * (GREATEST(lots.jours_restants, 0))::numeric)) - (lots.qte_avant)::numeric))))::integer AS qte_vendable
           FROM lots
        )
 SELECT lot_id,
    produit_id,
    nom_commercial,
    numero_lot,
    date_peremption,
    jours_restants,
    quantite_disponible,
    qte_vendable,
    (quantite_disponible - qte_vendable) AS qte_a_risque,
    round((((quantite_disponible - qte_vendable))::numeric * prix_achat_ht), 2) AS valeur_a_risque,
        CASE
            WHEN (jours_restants <= 0) THEN 'perime'::text
            WHEN (jours_restants <= seuil_rouge) THEN 'rouge'::text
            WHEN (jours_restants <= seuil_orange) THEN 'orange'::text
            WHEN ((jours_restants <= horizon_risque_jours) AND ((quantite_disponible - qte_vendable) > 0)) THEN 'risque_ecoulement'::text
            ELSE 'ok'::text
        END AS niveau,
    fournisseur_id,
    fournisseur,
    seuil_orange,
    seuil_rouge
   FROM calc;


--
-- Name: v_solde_tresorerie; Type: VIEW; Schema: public; Owner: -
--

CREATE VIEW public.v_solde_tresorerie AS
 SELECT id,
    type,
    nom,
    banque,
    numero_compte,
    recoit_carte,
    actif,
    solde_initial,
    date_initiale,
    (((((solde_initial + COALESCE(( SELECT sum(
                CASE
                    WHEN (m.sens = 'entree'::text) THEN m.montant
                    ELSE (- m.montant)
                END) AS sum
           FROM public.mouvement_tresorerie m
          WHERE ((m.compte_id = c.id) AND (m.date_mouvement >= c.date_initiale))), (0)::numeric)) +
        CASE
            WHEN recoit_carte THEN COALESCE(( SELECT sum(p.montant) AS sum
               FROM public.paiement p
              WHERE ((p.mode = 'carte'::text) AND (p.cree_le >= c.date_initiale) AND ((p.vente_id IS NULL) OR (EXISTS ( SELECT 1
                       FROM public.vente v
                      WHERE ((v.id = p.vente_id) AND (v.statut = ANY (ARRAY['validee'::text, 'annulee'::text])))))))), (0)::numeric)
            ELSE (0)::numeric
        END) + COALESCE(( SELECT sum(p.montant) AS sum
           FROM ((public.remise_cheques r
             JOIN public.remise_cheque_paiement rp ON ((rp.remise_id = r.id)))
             JOIN public.paiement p ON ((p.id = rp.paiement_id)))
          WHERE ((r.compte_id = c.id) AND (r.date_remise >= c.date_initiale))), (0)::numeric)) - COALESCE(( SELECT sum(
                CASE
                    WHEN (g.moyen = 'especes'::text) THEN (g.montant - g.part_tiroir)
                    ELSE g.montant
                END) AS sum
           FROM public.reglement_fournisseur g
          WHERE ((g.montant > g.part_tiroir) AND (((g.moyen = 'especes'::text) AND (c.type = 'coffre'::text)) OR ((g.moyen <> 'especes'::text) AND (g.compte_id = c.id))) AND (
                CASE
                    WHEN (g.moyen = 'effet'::text) THEN g.echeance
                    ELSE g.date_reglement
                END >= c.date_initiale))), (0)::numeric)) -
        CASE
            WHEN recoit_carte THEN (COALESCE(( SELECT sum(annulation_vente.montant_rembourse) AS sum
               FROM public.annulation_vente
              WHERE ((annulation_vente.mode = 'carte'::text) AND (annulation_vente.montant_rembourse > (0)::numeric) AND (annulation_vente.cree_le >= c.date_initiale))), (0)::numeric) + COALESCE(( SELECT sum(retour_client.montant_rembourse) AS sum
               FROM public.retour_client
              WHERE ((retour_client.mode = 'carte'::text) AND (retour_client.montant_rembourse > (0)::numeric) AND (retour_client.cree_le >= c.date_initiale))), (0)::numeric))
            ELSE (0)::numeric
        END) AS solde
   FROM public.compte_tresorerie c;


--
-- Name: v_stock_par_fournisseur; Type: VIEW; Schema: public; Owner: -
--

CREATE VIEW public.v_stock_par_fournisseur AS
 SELECT l.produit_id,
    p.nom_commercial,
    l.fournisseur_id,
    f.raison_sociale AS fournisseur,
    sum(l.quantite_disponible) AS quantite,
    min(l.date_peremption) AS premiere_peremption,
    round(sum(((l.quantite_disponible)::numeric * l.prix_achat_ht)), 2) AS valeur_achat
   FROM ((public.lot l
     JOIN public.produit p ON ((p.id = l.produit_id)))
     JOIN public.fournisseur f ON ((f.id = l.fournisseur_id)))
  WHERE (l.quantite_disponible > 0)
  GROUP BY l.produit_id, p.nom_commercial, l.fournisseur_id, f.raison_sociale;


--
-- Name: vente_id_seq; Type: SEQUENCE; Schema: public; Owner: -
--

ALTER TABLE public.vente ALTER COLUMN id ADD GENERATED ALWAYS AS IDENTITY (
    SEQUENCE NAME public.vente_id_seq
    START WITH 1
    INCREMENT BY 1
    NO MINVALUE
    NO MAXVALUE
    CACHE 1
);


--
-- Name: anomalie anomalie_pkey; Type: CONSTRAINT; Schema: import; Owner: -
--

ALTER TABLE ONLY import.anomalie
    ADD CONSTRAINT anomalie_pkey PRIMARY KEY (id);


--
-- Name: client_src client_src_pkey; Type: CONSTRAINT; Schema: import; Owner: -
--

ALTER TABLE ONLY import.client_src
    ADD CONSTRAINT client_src_pkey PRIMARY KEY (session_id, ref_source);


--
-- Name: correspondance_produit correspondance_produit_pkey; Type: CONSTRAINT; Schema: import; Owner: -
--

ALTER TABLE ONLY import.correspondance_produit
    ADD CONSTRAINT correspondance_produit_pkey PRIMARY KEY (session_id, ref_source);


--
-- Name: fournisseur_src fournisseur_src_pkey; Type: CONSTRAINT; Schema: import; Owner: -
--

ALTER TABLE ONLY import.fournisseur_src
    ADD CONSTRAINT fournisseur_src_pkey PRIMARY KEY (session_id, ref_source);


--
-- Name: produit_src produit_src_pkey; Type: CONSTRAINT; Schema: import; Owner: -
--

ALTER TABLE ONLY import.produit_src
    ADD CONSTRAINT produit_src_pkey PRIMARY KEY (session_id, ref_source);


--
-- Name: session session_pkey; Type: CONSTRAINT; Schema: import; Owner: -
--

ALTER TABLE ONLY import.session
    ADD CONSTRAINT session_pkey PRIMARY KEY (id);


--
-- Name: stock_src stock_src_pkey; Type: CONSTRAINT; Schema: import; Owner: -
--

ALTER TABLE ONLY import.stock_src
    ADD CONSTRAINT stock_src_pkey PRIMARY KEY (id);


--
-- Name: alerte alerte_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.alerte
    ADD CONSTRAINT alerte_pkey PRIMARY KEY (id);


--
-- Name: annulation_vente annulation_vente_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.annulation_vente
    ADD CONSTRAINT annulation_vente_pkey PRIMARY KEY (id);


--
-- Name: annulation_vente annulation_vente_vente_id_key; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.annulation_vente
    ADD CONSTRAINT annulation_vente_vente_id_key UNIQUE (vente_id);


--
-- Name: boite boite_code_etiquette_key; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.boite
    ADD CONSTRAINT boite_code_etiquette_key UNIQUE (code_etiquette);


--
-- Name: boite_evenement boite_evenement_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.boite_evenement
    ADD CONSTRAINT boite_evenement_pkey PRIMARY KEY (id);


--
-- Name: boite boite_gtin_numero_serie_key; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.boite
    ADD CONSTRAINT boite_gtin_numero_serie_key UNIQUE (gtin, numero_serie);


--
-- Name: boite boite_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.boite
    ADD CONSTRAINT boite_pkey PRIMARY KEY (id);


--
-- Name: bordereau bordereau_numero_key; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.bordereau
    ADD CONSTRAINT bordereau_numero_key UNIQUE (numero);


--
-- Name: bordereau bordereau_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.bordereau
    ADD CONSTRAINT bordereau_pkey PRIMARY KEY (id);


--
-- Name: campagne_inventaire_ligne campagne_inventaire_ligne_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.campagne_inventaire_ligne
    ADD CONSTRAINT campagne_inventaire_ligne_pkey PRIMARY KEY (campagne_id, lot_id);


--
-- Name: campagne_inventaire campagne_inventaire_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.campagne_inventaire
    ADD CONSTRAINT campagne_inventaire_pkey PRIMARY KEY (id);


--
-- Name: client_facture client_facture_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.client_facture
    ADD CONSTRAINT client_facture_pkey PRIMARY KEY (id);


--
-- Name: client_facture client_facture_raison_sociale_key; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.client_facture
    ADD CONSTRAINT client_facture_raison_sociale_key UNIQUE (raison_sociale);


--
-- Name: commande_fournisseur commande_fournisseur_numero_key; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.commande_fournisseur
    ADD CONSTRAINT commande_fournisseur_numero_key UNIQUE (numero);


--
-- Name: commande_fournisseur commande_fournisseur_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.commande_fournisseur
    ADD CONSTRAINT commande_fournisseur_pkey PRIMARY KEY (id);


--
-- Name: comptage_caisse comptage_caisse_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.comptage_caisse
    ADD CONSTRAINT comptage_caisse_pkey PRIMARY KEY (session_id, coupure);


--
-- Name: compte_tresorerie compte_tresorerie_nom_key; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.compte_tresorerie
    ADD CONSTRAINT compte_tresorerie_nom_key UNIQUE (nom);


--
-- Name: compte_tresorerie compte_tresorerie_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.compte_tresorerie
    ADD CONSTRAINT compte_tresorerie_pkey PRIMARY KEY (id);


--
-- Name: dci dci_nom_key; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.dci
    ADD CONSTRAINT dci_nom_key UNIQUE (nom);


--
-- Name: dci dci_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.dci
    ADD CONSTRAINT dci_pkey PRIMARY KEY (id);


--
-- Name: destinataire_notification destinataire_notification_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.destinataire_notification
    ADD CONSTRAINT destinataire_notification_pkey PRIMARY KEY (id);


--
-- Name: emplacement emplacement_code_key; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.emplacement
    ADD CONSTRAINT emplacement_code_key UNIQUE (code);


--
-- Name: emplacement emplacement_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.emplacement
    ADD CONSTRAINT emplacement_pkey PRIMARY KEY (id);


--
-- Name: facture_client_ligne facture_client_ligne_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.facture_client_ligne
    ADD CONSTRAINT facture_client_ligne_pkey PRIMARY KEY (id);


--
-- Name: facture_client facture_client_numero_key; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.facture_client
    ADD CONSTRAINT facture_client_numero_key UNIQUE (numero);


--
-- Name: facture_client facture_client_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.facture_client
    ADD CONSTRAINT facture_client_pkey PRIMARY KEY (id);


--
-- Name: facture_client_version facture_client_version_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.facture_client_version
    ADD CONSTRAINT facture_client_version_pkey PRIMARY KEY (facture_id, version);


--
-- Name: facture_compteur facture_compteur_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.facture_compteur
    ADD CONSTRAINT facture_compteur_pkey PRIMARY KEY (type, annee);


--
-- Name: fournisseur fournisseur_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.fournisseur
    ADD CONSTRAINT fournisseur_pkey PRIMARY KEY (id);


--
-- Name: inventaire inventaire_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.inventaire
    ADD CONSTRAINT inventaire_pkey PRIMARY KEY (id);


--
-- Name: journal_audit journal_audit_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.journal_audit
    ADD CONSTRAINT journal_audit_pkey PRIMARY KEY (id);


--
-- Name: journal_sauvegarde journal_sauvegarde_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.journal_sauvegarde
    ADD CONSTRAINT journal_sauvegarde_pkey PRIMARY KEY (id);


--
-- Name: laboratoire laboratoire_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.laboratoire
    ADD CONSTRAINT laboratoire_pkey PRIMARY KEY (id);


--
-- Name: ligne_commande ligne_commande_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.ligne_commande
    ADD CONSTRAINT ligne_commande_pkey PRIMARY KEY (id);


--
-- Name: ligne_inventaire ligne_inventaire_inventaire_id_lot_id_key; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.ligne_inventaire
    ADD CONSTRAINT ligne_inventaire_inventaire_id_lot_id_key UNIQUE (inventaire_id, lot_id);


--
-- Name: ligne_inventaire ligne_inventaire_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.ligne_inventaire
    ADD CONSTRAINT ligne_inventaire_pkey PRIMARY KEY (id);


--
-- Name: ligne_retour_fournisseur ligne_retour_fournisseur_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.ligne_retour_fournisseur
    ADD CONSTRAINT ligne_retour_fournisseur_pkey PRIMARY KEY (id);


--
-- Name: ligne_vente ligne_vente_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.ligne_vente
    ADD CONSTRAINT ligne_vente_pkey PRIMARY KEY (id);


--
-- Name: lot lot_code_interne_key; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.lot
    ADD CONSTRAINT lot_code_interne_key UNIQUE (code_interne);


--
-- Name: lot lot_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.lot
    ADD CONSTRAINT lot_pkey PRIMARY KEY (id);


--
-- Name: medecin medecin_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.medecin
    ADD CONSTRAINT medecin_pkey PRIMARY KEY (id);


--
-- Name: message_sortant message_sortant_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.message_sortant
    ADD CONSTRAINT message_sortant_pkey PRIMARY KEY (id);


--
-- Name: mouvement_stock mouvement_stock_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.mouvement_stock
    ADD CONSTRAINT mouvement_stock_pkey PRIMARY KEY (id);


--
-- Name: mouvement_tresorerie mouvement_tresorerie_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.mouvement_tresorerie
    ADD CONSTRAINT mouvement_tresorerie_pkey PRIMARY KEY (id);


--
-- Name: nomenclature_produit nomenclature_produit_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.nomenclature_produit
    ADD CONSTRAINT nomenclature_produit_pkey PRIMARY KEY (id);


--
-- Name: nomenclature_version nomenclature_version_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.nomenclature_version
    ADD CONSTRAINT nomenclature_version_pkey PRIMARY KEY (id);


--
-- Name: ordonnance ordonnance_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.ordonnance
    ADD CONSTRAINT ordonnance_pkey PRIMARY KEY (id);


--
-- Name: organisme organisme_code_key; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.organisme
    ADD CONSTRAINT organisme_code_key UNIQUE (code);


--
-- Name: organisme organisme_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.organisme
    ADD CONSTRAINT organisme_pkey PRIMARY KEY (id);


--
-- Name: paiement paiement_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.paiement
    ADD CONSTRAINT paiement_pkey PRIMARY KEY (id);


--
-- Name: panier_attente panier_attente_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.panier_attente
    ADD CONSTRAINT panier_attente_pkey PRIMARY KEY (id);


--
-- Name: parametre_alerte parametre_alerte_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.parametre_alerte
    ADD CONSTRAINT parametre_alerte_pkey PRIMARY KEY (id);


--
-- Name: parametre_caisse parametre_caisse_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.parametre_caisse
    ADD CONSTRAINT parametre_caisse_pkey PRIMARY KEY (id);


--
-- Name: parametre_inventaire parametre_inventaire_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.parametre_inventaire
    ADD CONSTRAINT parametre_inventaire_pkey PRIMARY KEY (id);


--
-- Name: parametre_notification parametre_notification_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.parametre_notification
    ADD CONSTRAINT parametre_notification_pkey PRIMARY KEY (id);


--
-- Name: parametre_sauvegarde parametre_sauvegarde_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.parametre_sauvegarde
    ADD CONSTRAINT parametre_sauvegarde_pkey PRIMARY KEY (id);


--
-- Name: parametre_tracabilite parametre_tracabilite_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.parametre_tracabilite
    ADD CONSTRAINT parametre_tracabilite_pkey PRIMARY KEY (id);


--
-- Name: patient patient_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.patient
    ADD CONSTRAINT patient_pkey PRIMARY KEY (id);


--
-- Name: permission permission_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.permission
    ADD CONSTRAINT permission_pkey PRIMARY KEY (code);


--
-- Name: pharmacie pharmacie_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.pharmacie
    ADD CONSTRAINT pharmacie_pkey PRIMARY KEY (id);


--
-- Name: produit produit_code_barre_key; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.produit
    ADD CONSTRAINT produit_code_barre_key UNIQUE (code_barre);


--
-- Name: produit produit_num_enregistrement_key; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.produit
    ADD CONSTRAINT produit_num_enregistrement_key UNIQUE (num_enregistrement);


--
-- Name: produit produit_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.produit
    ADD CONSTRAINT produit_pkey PRIMARY KEY (id);


--
-- Name: reception reception_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.reception
    ADD CONSTRAINT reception_pkey PRIMARY KEY (id);


--
-- Name: registre_psychotrope registre_psychotrope_ligne_vente_id_key; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.registre_psychotrope
    ADD CONSTRAINT registre_psychotrope_ligne_vente_id_key UNIQUE (ligne_vente_id);


--
-- Name: registre_psychotrope registre_psychotrope_mouvement_id_key; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.registre_psychotrope
    ADD CONSTRAINT registre_psychotrope_mouvement_id_key UNIQUE (mouvement_id);


--
-- Name: registre_psychotrope registre_psychotrope_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.registre_psychotrope
    ADD CONSTRAINT registre_psychotrope_pkey PRIMARY KEY (id);


--
-- Name: reglement_facture_client reglement_facture_client_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.reglement_facture_client
    ADD CONSTRAINT reglement_facture_client_pkey PRIMARY KEY (mouvement_id, facture_id);


--
-- Name: reglement_facture reglement_facture_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.reglement_facture
    ADD CONSTRAINT reglement_facture_pkey PRIMARY KEY (reglement_id, reception_id);


--
-- Name: reglement_fournisseur reglement_fournisseur_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.reglement_fournisseur
    ADD CONSTRAINT reglement_fournisseur_pkey PRIMARY KEY (id);


--
-- Name: remise_cheque_paiement remise_cheque_paiement_paiement_id_key; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.remise_cheque_paiement
    ADD CONSTRAINT remise_cheque_paiement_paiement_id_key UNIQUE (paiement_id);


--
-- Name: remise_cheque_paiement remise_cheque_paiement_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.remise_cheque_paiement
    ADD CONSTRAINT remise_cheque_paiement_pkey PRIMARY KEY (remise_id, paiement_id);


--
-- Name: remise_cheques remise_cheques_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.remise_cheques
    ADD CONSTRAINT remise_cheques_pkey PRIMARY KEY (id);


--
-- Name: retour_client_ligne retour_client_ligne_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.retour_client_ligne
    ADD CONSTRAINT retour_client_ligne_pkey PRIMARY KEY (id);


--
-- Name: retour_client retour_client_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.retour_client
    ADD CONSTRAINT retour_client_pkey PRIMARY KEY (id);


--
-- Name: retour_fournisseur retour_fournisseur_numero_key; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.retour_fournisseur
    ADD CONSTRAINT retour_fournisseur_numero_key UNIQUE (numero);


--
-- Name: retour_fournisseur retour_fournisseur_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.retour_fournisseur
    ADD CONSTRAINT retour_fournisseur_pkey PRIMARY KEY (id);


--
-- Name: role role_code_key; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.role
    ADD CONSTRAINT role_code_key UNIQUE (code);


--
-- Name: role_permission role_permission_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.role_permission
    ADD CONSTRAINT role_permission_pkey PRIMARY KEY (role_id, permission_code);


--
-- Name: role role_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.role
    ADD CONSTRAINT role_pkey PRIMARY KEY (id);


--
-- Name: scan_reception scan_reception_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.scan_reception
    ADD CONSTRAINT scan_reception_pkey PRIMARY KEY (id);


--
-- Name: session_caisse session_caisse_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.session_caisse
    ADD CONSTRAINT session_caisse_pkey PRIMARY KEY (id);


--
-- Name: substance_surveillee substance_surveillee_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.substance_surveillee
    ADD CONSTRAINT substance_surveillee_pkey PRIMARY KEY (motif);


--
-- Name: utilisateur utilisateur_identifiant_key; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.utilisateur
    ADD CONSTRAINT utilisateur_identifiant_key UNIQUE (identifiant);


--
-- Name: utilisateur utilisateur_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.utilisateur
    ADD CONSTRAINT utilisateur_pkey PRIMARY KEY (id);


--
-- Name: lot ux_lot_fournisseur_date; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.lot
    ADD CONSTRAINT ux_lot_fournisseur_date UNIQUE (produit_id, fournisseur_id, numero_lot, date_peremption);


--
-- Name: vente vente_numero_key; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.vente
    ADD CONSTRAINT vente_numero_key UNIQUE (numero);


--
-- Name: vente vente_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.vente
    ADD CONSTRAINT vente_pkey PRIMARY KEY (id);


--
-- Name: idx_alerte_ouverte_tri; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX idx_alerte_ouverte_tri ON public.alerte USING btree (niveau, valeur_dzd DESC) WHERE (statut = ANY (ARRAY['nouvelle'::text, 'vue'::text]));


--
-- Name: idx_annulation_session; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX idx_annulation_session ON public.annulation_vente USING btree (session_caisse_id);


--
-- Name: idx_audit_date; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX idx_audit_date ON public.journal_audit USING btree (cree_le);


--
-- Name: idx_boite_en_stock; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX idx_boite_en_stock ON public.boite USING btree (lot_id) WHERE (statut = 'en_stock'::text);


--
-- Name: idx_boite_evenement; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX idx_boite_evenement ON public.boite_evenement USING btree (boite_id, cree_le);


--
-- Name: idx_boite_ligne_vente; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX idx_boite_ligne_vente ON public.boite USING btree (ligne_vente_id) WHERE (ligne_vente_id IS NOT NULL);


--
-- Name: idx_boite_lot; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX idx_boite_lot ON public.boite USING btree (lot_id);


--
-- Name: idx_boite_numero_serie; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX idx_boite_numero_serie ON public.boite USING btree (numero_serie);


--
-- Name: idx_boite_reception; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX idx_boite_reception ON public.boite USING btree (reception_id);


--
-- Name: idx_dci_nom_sans_accent; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX idx_dci_nom_sans_accent ON public.dci USING gin (public.sans_accent(nom) public.gin_trgm_ops);


--
-- Name: idx_dci_nom_trgm; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX idx_dci_nom_trgm ON public.dci USING gin (nom public.gin_trgm_ops);


--
-- Name: idx_emplacement_rayon; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX idx_emplacement_rayon ON public.emplacement USING btree (rayon, code);


--
-- Name: idx_facture_client_date; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX idx_facture_client_date ON public.facture_client USING btree (date_facture);


--
-- Name: idx_facture_ligne_facture; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX idx_facture_ligne_facture ON public.facture_client_ligne USING btree (facture_id);


--
-- Name: idx_facture_ligne_ligne_vente; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX idx_facture_ligne_ligne_vente ON public.facture_client_ligne USING btree (ligne_vente_id) WHERE (ligne_vente_id IS NOT NULL);


--
-- Name: idx_facture_ligne_vente; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX idx_facture_ligne_vente ON public.facture_client_ligne USING btree (vente_id, part);


--
-- Name: idx_facture_origine; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX idx_facture_origine ON public.facture_client USING btree (facture_origine_id) WHERE (facture_origine_id IS NOT NULL);


--
-- Name: idx_inventaire_campagne; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX idx_inventaire_campagne ON public.inventaire USING btree (campagne_id);


--
-- Name: idx_journal_sauvegarde; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX idx_journal_sauvegarde ON public.journal_sauvegarde USING btree (debut DESC);


--
-- Name: idx_ligne_inventaire_a_valider; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX idx_ligne_inventaire_a_valider ON public.ligne_inventaire USING btree (statut) WHERE (statut = 'a_valider'::text);


--
-- Name: idx_ligne_inventaire_lot; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX idx_ligne_inventaire_lot ON public.ligne_inventaire USING btree (lot_id);


--
-- Name: idx_ligne_vente_lot; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX idx_ligne_vente_lot ON public.ligne_vente USING btree (lot_id);


--
-- Name: idx_ligne_vente_produit; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX idx_ligne_vente_produit ON public.ligne_vente USING btree (produit_id);


--
-- Name: idx_ligne_vente_vente; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX idx_ligne_vente_vente ON public.ligne_vente USING btree (vente_id);


--
-- Name: idx_lot_fefo; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX idx_lot_fefo ON public.lot USING btree (produit_id, date_peremption) WHERE (quantite_disponible > 0);


--
-- Name: idx_lot_fournisseur; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX idx_lot_fournisseur ON public.lot USING btree (fournisseur_id);


--
-- Name: idx_lot_reception; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX idx_lot_reception ON public.lot USING btree (reception_id);


--
-- Name: idx_message_a_envoyer; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX idx_message_a_envoyer ON public.message_sortant USING btree (prochaine_tentative) WHERE (statut = 'en_attente'::text);


--
-- Name: idx_mouvement_lot; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX idx_mouvement_lot ON public.mouvement_stock USING btree (lot_id, cree_le);


--
-- Name: idx_mouvement_reception; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX idx_mouvement_reception ON public.mouvement_stock USING btree (reception_id) WHERE (reception_id IS NOT NULL);


--
-- Name: idx_mouvement_tresorerie; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX idx_mouvement_tresorerie ON public.mouvement_tresorerie USING btree (compte_id, date_mouvement);


--
-- Name: idx_mouvement_tresorerie_session; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX idx_mouvement_tresorerie_session ON public.mouvement_tresorerie USING btree (session_caisse_id);


--
-- Name: idx_nomenclature_dci; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX idx_nomenclature_dci ON public.nomenclature_produit USING gin (dci public.gin_trgm_ops);


--
-- Name: idx_nomenclature_marque; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX idx_nomenclature_marque ON public.nomenclature_produit USING gin (marque public.gin_trgm_ops);


--
-- Name: idx_nomenclature_num; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX idx_nomenclature_num ON public.nomenclature_produit USING btree (num_enregistrement);


--
-- Name: idx_ordonnance_patient; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX idx_ordonnance_patient ON public.ordonnance USING btree (patient_id);


--
-- Name: idx_paiement_carte; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX idx_paiement_carte ON public.paiement USING btree (cree_le) WHERE (mode = 'carte'::text);


--
-- Name: idx_paiement_cheque; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX idx_paiement_cheque ON public.paiement USING btree (id) WHERE (mode = 'cheque'::text);


--
-- Name: idx_paiement_credit; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX idx_paiement_credit ON public.paiement USING btree (vente_id) WHERE (mode = 'credit'::text);


--
-- Name: idx_paiement_patient; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX idx_paiement_patient ON public.paiement USING btree (patient_id) WHERE (patient_id IS NOT NULL);


--
-- Name: idx_paiement_session; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX idx_paiement_session ON public.paiement USING btree (session_caisse_id);


--
-- Name: idx_paiement_vente; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX idx_paiement_vente ON public.paiement USING btree (vente_id);


--
-- Name: idx_panier_attente_poste; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX idx_panier_attente_poste ON public.panier_attente USING btree (poste, cree_le);


--
-- Name: idx_patient_nom_trgm; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX idx_patient_nom_trgm ON public.patient USING gin (nom public.gin_trgm_ops);


--
-- Name: idx_patient_num_assure; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX idx_patient_num_assure ON public.patient USING btree (num_assure);


--
-- Name: idx_produit_emplacement; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX idx_produit_emplacement ON public.produit USING btree (emplacement_id);


--
-- Name: idx_produit_nom_sans_accent; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX idx_produit_nom_sans_accent ON public.produit USING gin (public.sans_accent(nom_commercial) public.gin_trgm_ops);


--
-- Name: idx_produit_nom_trgm; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX idx_produit_nom_trgm ON public.produit USING gin (nom_commercial public.gin_trgm_ops);


--
-- Name: idx_reception_fournisseur; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX idx_reception_fournisseur ON public.reception USING btree (fournisseur_id, date_reception);


--
-- Name: idx_registre_date; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX idx_registre_date ON public.registre_psychotrope USING btree (cree_le);


--
-- Name: idx_registre_lot; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX idx_registre_lot ON public.registre_psychotrope USING btree (lot_id);


--
-- Name: idx_reglement_facture_client; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX idx_reglement_facture_client ON public.reglement_facture_client USING btree (facture_id);


--
-- Name: idx_reglement_facture_reception; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX idx_reglement_facture_reception ON public.reglement_facture USING btree (reception_id);


--
-- Name: idx_reglement_fournisseur; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX idx_reglement_fournisseur ON public.reglement_fournisseur USING btree (fournisseur_id, date_reglement);


--
-- Name: idx_reglement_session; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX idx_reglement_session ON public.reglement_fournisseur USING btree (session_caisse_id);


--
-- Name: idx_retour_client_session; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX idx_retour_client_session ON public.retour_client USING btree (session_caisse_id);


--
-- Name: idx_retour_client_vente; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX idx_retour_client_vente ON public.retour_client USING btree (vente_id);


--
-- Name: idx_retour_ligne_retour; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX idx_retour_ligne_retour ON public.retour_client_ligne USING btree (retour_id);


--
-- Name: idx_retour_ligne_vente; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX idx_retour_ligne_vente ON public.retour_client_ligne USING btree (ligne_vente_id);


--
-- Name: idx_scan_reception_attente; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX idx_scan_reception_attente ON public.scan_reception USING btree (utilisateur_id) WHERE (importe_le IS NULL);


--
-- Name: idx_session_caisse_date; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX idx_session_caisse_date ON public.session_caisse USING btree (ouverte_le);


--
-- Name: idx_session_fermee; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX idx_session_fermee ON public.session_caisse USING btree (fermee_le) WHERE (fermee_le IS NOT NULL);


--
-- Name: idx_vente_bordereau; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX idx_vente_bordereau ON public.vente USING btree (bordereau_id);


--
-- Name: idx_vente_date; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX idx_vente_date ON public.vente USING btree (date_vente);


--
-- Name: idx_vente_ordonnance; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX idx_vente_ordonnance ON public.vente USING btree (ordonnance_id) WHERE (ordonnance_id IS NOT NULL);


--
-- Name: idx_vente_organisme; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX idx_vente_organisme ON public.vente USING btree (organisme_id, date_vente) WHERE (organisme_id IS NOT NULL);


--
-- Name: idx_vente_patient; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX idx_vente_patient ON public.vente USING btree (patient_id, date_vente) WHERE (patient_id IS NOT NULL);


--
-- Name: idx_vente_session; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX idx_vente_session ON public.vente USING btree (session_caisse_id);


--
-- Name: ux_alerte_ouverte; Type: INDEX; Schema: public; Owner: -
--

CREATE UNIQUE INDEX ux_alerte_ouverte ON public.alerte USING btree (type, produit_id, COALESCE(lot_id, (0)::bigint)) WHERE (statut = ANY (ARRAY['nouvelle'::text, 'vue'::text, 'reportee'::text]));


--
-- Name: ux_campagne_en_cours; Type: INDEX; Schema: public; Owner: -
--

CREATE UNIQUE INDEX ux_campagne_en_cours ON public.campagne_inventaire USING btree ((true)) WHERE (statut = 'en_cours'::text);


--
-- Name: ux_inventaire_en_cours; Type: INDEX; Schema: public; Owner: -
--

CREATE UNIQUE INDEX ux_inventaire_en_cours ON public.inventaire USING btree (rayon) WHERE (statut = 'en_cours'::text);


--
-- Name: ux_ordonnance_chemin_scan; Type: INDEX; Schema: public; Owner: -
--

CREATE UNIQUE INDEX ux_ordonnance_chemin_scan ON public.ordonnance USING btree (chemin_scan) WHERE (chemin_scan IS NOT NULL);


--
-- Name: ux_produit_nom_dosage_forme; Type: INDEX; Schema: public; Owner: -
--

CREATE UNIQUE INDEX ux_produit_nom_dosage_forme ON public.produit USING btree (lower(nom_commercial), lower(COALESCE(dosage, ''::text)), lower(COALESCE(forme, ''::text)));


--
-- Name: ux_registre_numero_ordre; Type: INDEX; Schema: public; Owner: -
--

CREATE UNIQUE INDEX ux_registre_numero_ordre ON public.registre_psychotrope USING btree (numero_ordre);


--
-- Name: ux_scan_reception_serie; Type: INDEX; Schema: public; Owner: -
--

CREATE UNIQUE INDEX ux_scan_reception_serie ON public.scan_reception USING btree (gtin, numero_serie) WHERE ((importe_le IS NULL) AND (numero_serie IS NOT NULL));


--
-- Name: ux_session_poste_ouverte; Type: INDEX; Schema: public; Owner: -
--

CREATE UNIQUE INDEX ux_session_poste_ouverte ON public.session_caisse USING btree (poste) WHERE (fermee_le IS NULL);


--
-- Name: ux_un_coffre; Type: INDEX; Schema: public; Owner: -
--

CREATE UNIQUE INDEX ux_un_coffre ON public.compte_tresorerie USING btree (type) WHERE (type = 'coffre'::text);


--
-- Name: ux_une_banque_carte; Type: INDEX; Schema: public; Owner: -
--

CREATE UNIQUE INDEX ux_une_banque_carte ON public.compte_tresorerie USING btree (recoit_carte) WHERE recoit_carte;


--
-- Name: v_mouvement_tresorerie _RETURN; Type: RULE; Schema: public; Owner: -
--

CREATE OR REPLACE VIEW public.v_mouvement_tresorerie AS
 SELECT m.compte_id,
    m.date_mouvement AS date,
    m.sens,
    m.montant,
    m.nature,
    m.categorie,
    m.libelle,
    m.reference,
    u.nom_complet AS employe,
    'saisi'::text AS origine,
    m.id AS source_id,
    m.cree_le
   FROM (public.mouvement_tresorerie m
     JOIN public.utilisateur u ON ((u.id = m.utilisateur_id)))
UNION ALL
 SELECT c.id AS compte_id,
    (p.cree_le)::date AS date,
    'entree'::text AS sens,
    sum(p.montant) AS montant,
    'carte'::text AS nature,
    NULL::text AS categorie,
    'Paiements par carte du jour'::text AS libelle,
    NULL::text AS reference,
    NULL::text AS employe,
    'calcule'::text AS origine,
    NULL::bigint AS source_id,
    max(p.cree_le) AS cree_le
   FROM ((public.paiement p
     JOIN public.compte_tresorerie c ON (c.recoit_carte))
     LEFT JOIN public.vente v ON ((v.id = p.vente_id)))
  WHERE ((p.mode = 'carte'::text) AND ((p.vente_id IS NULL) OR (v.statut = ANY (ARRAY['validee'::text, 'annulee'::text]))))
  GROUP BY c.id, ((p.cree_le)::date)
UNION ALL
 SELECT r.compte_id,
    r.date_remise AS date,
    'entree'::text AS sens,
    sum(p.montant) AS montant,
    'remise_cheques'::text AS nature,
    NULL::text AS categorie,
    format('Remise de %s chèque(s)'::text, count(*)) AS libelle,
    r.reference,
    u.nom_complet AS employe,
    'calcule'::text AS origine,
    r.id AS source_id,
    r.cree_le
   FROM (((public.remise_cheques r
     JOIN public.remise_cheque_paiement rp ON ((rp.remise_id = r.id)))
     JOIN public.paiement p ON ((p.id = rp.paiement_id)))
     JOIN public.utilisateur u ON ((u.id = r.utilisateur_id)))
  GROUP BY r.id, u.nom_complet
UNION ALL
 SELECT
        CASE
            WHEN (g.moyen = 'especes'::text) THEN ( SELECT compte_tresorerie.id
               FROM public.compte_tresorerie
              WHERE (compte_tresorerie.type = 'coffre'::text))
            ELSE g.compte_id
        END AS compte_id,
        CASE
            WHEN (g.moyen = 'effet'::text) THEN g.echeance
            ELSE g.date_reglement
        END AS date,
    'sortie'::text AS sens,
        CASE
            WHEN (g.moyen = 'especes'::text) THEN (g.montant - g.part_tiroir)
            ELSE g.montant
        END AS montant,
    'reglement_grossiste'::text AS nature,
    NULL::text AS categorie,
    ('Règlement '::text || f.raison_sociale) AS libelle,
    g.reference,
    u.nom_complet AS employe,
    'calcule'::text AS origine,
    g.id AS source_id,
    g.cree_le
   FROM ((public.reglement_fournisseur g
     JOIN public.fournisseur f ON ((f.id = g.fournisseur_id)))
     JOIN public.utilisateur u ON ((u.id = g.utilisateur_id)))
  WHERE ((g.montant > g.part_tiroir) AND ((g.moyen = 'especes'::text) OR (g.compte_id IS NOT NULL)))
UNION ALL
 SELECT c.id AS compte_id,
    (x.cree_le)::date AS date,
    'sortie'::text AS sens,
    x.montant_rembourse AS montant,
    'remboursement_carte'::text AS nature,
    NULL::text AS categorie,
    x.libelle,
    NULL::text AS reference,
    NULL::text AS employe,
    'calcule'::text AS origine,
    NULL::bigint AS source_id,
    x.cree_le
   FROM (( SELECT annulation_vente.cree_le,
            annulation_vente.montant_rembourse,
            'Remboursement carte (annulation)'::text AS libelle
           FROM public.annulation_vente
          WHERE ((annulation_vente.mode = 'carte'::text) AND (annulation_vente.montant_rembourse > (0)::numeric))
        UNION ALL
         SELECT retour_client.cree_le,
            retour_client.montant_rembourse,
            'Remboursement carte (retour)'::text
           FROM public.retour_client
          WHERE ((retour_client.mode = 'carte'::text) AND (retour_client.montant_rembourse > (0)::numeric))) x
     JOIN public.compte_tresorerie c ON (c.recoit_carte));


--
-- Name: boite trg_boite_entree; Type: TRIGGER; Schema: public; Owner: -
--

CREATE TRIGGER trg_boite_entree AFTER INSERT ON public.boite FOR EACH ROW EXECUTE FUNCTION public.journaliser_entree_boite();


--
-- Name: boite_evenement trg_boite_evenement_immuable; Type: TRIGGER; Schema: public; Owner: -
--

CREATE TRIGGER trg_boite_evenement_immuable BEFORE DELETE OR UPDATE ON public.boite_evenement FOR EACH ROW EXECUTE FUNCTION public.interdire_modification_historique();


--
-- Name: boite trg_boite_reception; Type: TRIGGER; Schema: public; Owner: -
--

CREATE TRIGGER trg_boite_reception BEFORE INSERT ON public.boite FOR EACH ROW EXECUTE FUNCTION public.completer_reception_boite();


--
-- Name: utilisateur trg_garder_un_titulaire; Type: TRIGGER; Schema: public; Owner: -
--

CREATE CONSTRAINT TRIGGER trg_garder_un_titulaire AFTER DELETE OR UPDATE ON public.utilisateur DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION public.garder_un_titulaire();


--
-- Name: journal_audit trg_journal_immuable; Type: TRIGGER; Schema: public; Owner: -
--

CREATE TRIGGER trg_journal_immuable BEFORE DELETE OR UPDATE ON public.journal_audit FOR EACH ROW EXECUTE FUNCTION public.interdire_modification_journal();


--
-- Name: journal_sauvegarde trg_journal_sauvegarde_immuable; Type: TRIGGER; Schema: public; Owner: -
--

CREATE TRIGGER trg_journal_sauvegarde_immuable BEFORE DELETE OR UPDATE ON public.journal_sauvegarde FOR EACH ROW EXECUTE FUNCTION public.interdire_modification_historique();


--
-- Name: ligne_vente trg_ligne_vente_peremption; Type: TRIGGER; Schema: public; Owner: -
--

CREATE TRIGGER trg_ligne_vente_peremption BEFORE INSERT ON public.ligne_vente FOR EACH ROW EXECUTE FUNCTION public.verifier_lot_vendable();


--
-- Name: lot trg_lot_fournisseur; Type: TRIGGER; Schema: public; Owner: -
--

CREATE TRIGGER trg_lot_fournisseur BEFORE INSERT OR UPDATE OF fournisseur_id, reception_id ON public.lot FOR EACH ROW EXECUTE FUNCTION public.verifier_fournisseur_lot();


--
-- Name: lot trg_lot_numero; Type: TRIGGER; Schema: public; Owner: -
--

CREATE TRIGGER trg_lot_numero BEFORE INSERT OR UPDATE OF numero_lot ON public.lot FOR EACH ROW EXECUTE FUNCTION public.normaliser_numero_lot();


--
-- Name: message_sortant trg_message_destinataire; Type: TRIGGER; Schema: public; Owner: -
--

CREATE TRIGGER trg_message_destinataire BEFORE INSERT ON public.message_sortant FOR EACH ROW EXECUTE FUNCTION public.verifier_destinataire_message();


--
-- Name: mouvement_stock trg_mouvement_registre; Type: TRIGGER; Schema: public; Owner: -
--

CREATE TRIGGER trg_mouvement_registre AFTER INSERT ON public.mouvement_stock FOR EACH ROW EXECUTE FUNCTION public.inscrire_mouvement_registre();


--
-- Name: role_permission trg_proteger_role_systeme; Type: TRIGGER; Schema: public; Owner: -
--

CREATE TRIGGER trg_proteger_role_systeme BEFORE INSERT OR DELETE ON public.role_permission FOR EACH ROW EXECUTE FUNCTION public.proteger_role_systeme();


--
-- Name: registre_psychotrope trg_registre_immuable; Type: TRIGGER; Schema: public; Owner: -
--

CREATE TRIGGER trg_registre_immuable BEFORE DELETE OR UPDATE ON public.registre_psychotrope FOR EACH ROW EXECUTE FUNCTION public.interdire_modification();


--
-- Name: registre_psychotrope trg_registre_numero; Type: TRIGGER; Schema: public; Owner: -
--

CREATE TRIGGER trg_registre_numero BEFORE INSERT ON public.registre_psychotrope FOR EACH ROW EXECUTE FUNCTION public.numeroter_registre();


--
-- Name: registre_psychotrope trg_registre_sans_doublon; Type: TRIGGER; Schema: public; Owner: -
--

CREATE TRIGGER trg_registre_sans_doublon BEFORE INSERT ON public.registre_psychotrope FOR EACH ROW EXECUTE FUNCTION public.eviter_doublon_registre();


--
-- Name: ligne_retour_fournisseur trg_retour_bon_fournisseur; Type: TRIGGER; Schema: public; Owner: -
--

CREATE TRIGGER trg_retour_bon_fournisseur BEFORE INSERT OR UPDATE ON public.ligne_retour_fournisseur FOR EACH ROW EXECUTE FUNCTION public.verifier_retour_bon_fournisseur();


--
-- Name: role trg_role_systeme; Type: TRIGGER; Schema: public; Owner: -
--

CREATE TRIGGER trg_role_systeme BEFORE DELETE OR UPDATE ON public.role FOR EACH ROW EXECUTE FUNCTION public.proteger_ligne_role_systeme();


--
-- Name: session_caisse trg_session_fermee; Type: TRIGGER; Schema: public; Owner: -
--

CREATE TRIGGER trg_session_fermee BEFORE DELETE OR UPDATE ON public.session_caisse FOR EACH ROW EXECUTE FUNCTION public.proteger_session_fermee();


--
-- Name: vente trg_tracabilite_vente; Type: TRIGGER; Schema: public; Owner: -
--

CREATE CONSTRAINT TRIGGER trg_tracabilite_vente AFTER INSERT OR UPDATE OF statut ON public.vente DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION public.verifier_tracabilite_vente();


--
-- Name: produit trg_zones_produit; Type: TRIGGER; Schema: public; Owner: -
--

CREATE TRIGGER trg_zones_produit BEFORE INSERT OR UPDATE OF emplacement_id, reserve_id ON public.produit FOR EACH ROW EXECUTE FUNCTION public.verifier_zones_produit();


--
-- Name: anomalie anomalie_session_id_fkey; Type: FK CONSTRAINT; Schema: import; Owner: -
--

ALTER TABLE ONLY import.anomalie
    ADD CONSTRAINT anomalie_session_id_fkey FOREIGN KEY (session_id) REFERENCES import.session(id);


--
-- Name: client_src client_src_session_id_fkey; Type: FK CONSTRAINT; Schema: import; Owner: -
--

ALTER TABLE ONLY import.client_src
    ADD CONSTRAINT client_src_session_id_fkey FOREIGN KEY (session_id) REFERENCES import.session(id);


--
-- Name: correspondance_produit correspondance_produit_produit_id_fkey; Type: FK CONSTRAINT; Schema: import; Owner: -
--

ALTER TABLE ONLY import.correspondance_produit
    ADD CONSTRAINT correspondance_produit_produit_id_fkey FOREIGN KEY (produit_id) REFERENCES public.produit(id);


--
-- Name: correspondance_produit correspondance_produit_session_id_fkey; Type: FK CONSTRAINT; Schema: import; Owner: -
--

ALTER TABLE ONLY import.correspondance_produit
    ADD CONSTRAINT correspondance_produit_session_id_fkey FOREIGN KEY (session_id) REFERENCES import.session(id);


--
-- Name: fournisseur_src fournisseur_src_session_id_fkey; Type: FK CONSTRAINT; Schema: import; Owner: -
--

ALTER TABLE ONLY import.fournisseur_src
    ADD CONSTRAINT fournisseur_src_session_id_fkey FOREIGN KEY (session_id) REFERENCES import.session(id);


--
-- Name: produit_src produit_src_session_id_fkey; Type: FK CONSTRAINT; Schema: import; Owner: -
--

ALTER TABLE ONLY import.produit_src
    ADD CONSTRAINT produit_src_session_id_fkey FOREIGN KEY (session_id) REFERENCES import.session(id);


--
-- Name: stock_src stock_src_session_id_fkey; Type: FK CONSTRAINT; Schema: import; Owner: -
--

ALTER TABLE ONLY import.stock_src
    ADD CONSTRAINT stock_src_session_id_fkey FOREIGN KEY (session_id) REFERENCES import.session(id);


--
-- Name: alerte alerte_lot_id_fkey; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.alerte
    ADD CONSTRAINT alerte_lot_id_fkey FOREIGN KEY (lot_id) REFERENCES public.lot(id);


--
-- Name: alerte alerte_produit_id_fkey; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.alerte
    ADD CONSTRAINT alerte_produit_id_fkey FOREIGN KEY (produit_id) REFERENCES public.produit(id);


--
-- Name: alerte alerte_traitee_par_fkey; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.alerte
    ADD CONSTRAINT alerte_traitee_par_fkey FOREIGN KEY (traitee_par) REFERENCES public.utilisateur(id);


--
-- Name: annulation_vente annulation_vente_session_caisse_id_fkey; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.annulation_vente
    ADD CONSTRAINT annulation_vente_session_caisse_id_fkey FOREIGN KEY (session_caisse_id) REFERENCES public.session_caisse(id);


--
-- Name: annulation_vente annulation_vente_utilisateur_id_fkey; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.annulation_vente
    ADD CONSTRAINT annulation_vente_utilisateur_id_fkey FOREIGN KEY (utilisateur_id) REFERENCES public.utilisateur(id);


--
-- Name: annulation_vente annulation_vente_valide_par_fkey; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.annulation_vente
    ADD CONSTRAINT annulation_vente_valide_par_fkey FOREIGN KEY (valide_par) REFERENCES public.utilisateur(id);


--
-- Name: annulation_vente annulation_vente_vente_id_fkey; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.annulation_vente
    ADD CONSTRAINT annulation_vente_vente_id_fkey FOREIGN KEY (vente_id) REFERENCES public.vente(id);


--
-- Name: boite_evenement boite_evenement_boite_id_fkey; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.boite_evenement
    ADD CONSTRAINT boite_evenement_boite_id_fkey FOREIGN KEY (boite_id) REFERENCES public.boite(id);


--
-- Name: boite_evenement boite_evenement_ligne_vente_id_fkey; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.boite_evenement
    ADD CONSTRAINT boite_evenement_ligne_vente_id_fkey FOREIGN KEY (ligne_vente_id) REFERENCES public.ligne_vente(id);


--
-- Name: boite_evenement boite_evenement_patient_id_fkey; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.boite_evenement
    ADD CONSTRAINT boite_evenement_patient_id_fkey FOREIGN KEY (patient_id) REFERENCES public.patient(id);


--
-- Name: boite_evenement boite_evenement_reception_id_fkey; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.boite_evenement
    ADD CONSTRAINT boite_evenement_reception_id_fkey FOREIGN KEY (reception_id) REFERENCES public.reception(id);


--
-- Name: boite_evenement boite_evenement_retour_fournisseur_id_fkey; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.boite_evenement
    ADD CONSTRAINT boite_evenement_retour_fournisseur_id_fkey FOREIGN KEY (retour_fournisseur_id) REFERENCES public.retour_fournisseur(id);


--
-- Name: boite_evenement boite_evenement_utilisateur_id_fkey; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.boite_evenement
    ADD CONSTRAINT boite_evenement_utilisateur_id_fkey FOREIGN KEY (utilisateur_id) REFERENCES public.utilisateur(id);


--
-- Name: boite boite_ligne_vente_id_fkey; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.boite
    ADD CONSTRAINT boite_ligne_vente_id_fkey FOREIGN KEY (ligne_vente_id) REFERENCES public.ligne_vente(id);


--
-- Name: boite boite_lot_id_fkey; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.boite
    ADD CONSTRAINT boite_lot_id_fkey FOREIGN KEY (lot_id) REFERENCES public.lot(id);


--
-- Name: boite boite_reception_id_fkey; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.boite
    ADD CONSTRAINT boite_reception_id_fkey FOREIGN KEY (reception_id) REFERENCES public.reception(id);


--
-- Name: bordereau bordereau_organisme_id_fkey; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.bordereau
    ADD CONSTRAINT bordereau_organisme_id_fkey FOREIGN KEY (organisme_id) REFERENCES public.organisme(id);


--
-- Name: campagne_inventaire campagne_inventaire_cloture_par_fkey; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.campagne_inventaire
    ADD CONSTRAINT campagne_inventaire_cloture_par_fkey FOREIGN KEY (cloture_par) REFERENCES public.utilisateur(id);


--
-- Name: campagne_inventaire campagne_inventaire_commence_par_fkey; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.campagne_inventaire
    ADD CONSTRAINT campagne_inventaire_commence_par_fkey FOREIGN KEY (commence_par) REFERENCES public.utilisateur(id);


--
-- Name: campagne_inventaire_ligne campagne_inventaire_ligne_campagne_id_fkey; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.campagne_inventaire_ligne
    ADD CONSTRAINT campagne_inventaire_ligne_campagne_id_fkey FOREIGN KEY (campagne_id) REFERENCES public.campagne_inventaire(id);


--
-- Name: campagne_inventaire_ligne campagne_inventaire_ligne_lot_id_fkey; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.campagne_inventaire_ligne
    ADD CONSTRAINT campagne_inventaire_ligne_lot_id_fkey FOREIGN KEY (lot_id) REFERENCES public.lot(id);


--
-- Name: commande_fournisseur commande_fournisseur_fournisseur_id_fkey; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.commande_fournisseur
    ADD CONSTRAINT commande_fournisseur_fournisseur_id_fkey FOREIGN KEY (fournisseur_id) REFERENCES public.fournisseur(id);


--
-- Name: commande_fournisseur commande_fournisseur_utilisateur_id_fkey; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.commande_fournisseur
    ADD CONSTRAINT commande_fournisseur_utilisateur_id_fkey FOREIGN KEY (utilisateur_id) REFERENCES public.utilisateur(id);


--
-- Name: comptage_caisse comptage_caisse_session_id_fkey; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.comptage_caisse
    ADD CONSTRAINT comptage_caisse_session_id_fkey FOREIGN KEY (session_id) REFERENCES public.session_caisse(id);


--
-- Name: facture_client facture_client_client_id_fkey; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.facture_client
    ADD CONSTRAINT facture_client_client_id_fkey FOREIGN KEY (client_id) REFERENCES public.client_facture(id);


--
-- Name: facture_client facture_client_facture_origine_id_fkey; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.facture_client
    ADD CONSTRAINT facture_client_facture_origine_id_fkey FOREIGN KEY (facture_origine_id) REFERENCES public.facture_client(id);


--
-- Name: facture_client_ligne facture_client_ligne_facture_id_fkey; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.facture_client_ligne
    ADD CONSTRAINT facture_client_ligne_facture_id_fkey FOREIGN KEY (facture_id) REFERENCES public.facture_client(id);


--
-- Name: facture_client_ligne facture_client_ligne_ligne_vente_id_fkey; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.facture_client_ligne
    ADD CONSTRAINT facture_client_ligne_ligne_vente_id_fkey FOREIGN KEY (ligne_vente_id) REFERENCES public.ligne_vente(id);


--
-- Name: facture_client_ligne facture_client_ligne_vente_id_fkey; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.facture_client_ligne
    ADD CONSTRAINT facture_client_ligne_vente_id_fkey FOREIGN KEY (vente_id) REFERENCES public.vente(id);


--
-- Name: facture_client facture_client_organisme_id_fkey; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.facture_client
    ADD CONSTRAINT facture_client_organisme_id_fkey FOREIGN KEY (organisme_id) REFERENCES public.organisme(id);


--
-- Name: facture_client facture_client_patient_id_fkey; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.facture_client
    ADD CONSTRAINT facture_client_patient_id_fkey FOREIGN KEY (patient_id) REFERENCES public.patient(id);


--
-- Name: facture_client facture_client_remplace_id_fkey; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.facture_client
    ADD CONSTRAINT facture_client_remplace_id_fkey FOREIGN KEY (remplace_id) REFERENCES public.facture_client(id);


--
-- Name: facture_client facture_client_remplacee_par_fkey; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.facture_client
    ADD CONSTRAINT facture_client_remplacee_par_fkey FOREIGN KEY (remplacee_par) REFERENCES public.facture_client(id);


--
-- Name: facture_client facture_client_utilisateur_id_fkey; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.facture_client
    ADD CONSTRAINT facture_client_utilisateur_id_fkey FOREIGN KEY (utilisateur_id) REFERENCES public.utilisateur(id);


--
-- Name: facture_client_version facture_client_version_facture_id_fkey; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.facture_client_version
    ADD CONSTRAINT facture_client_version_facture_id_fkey FOREIGN KEY (facture_id) REFERENCES public.facture_client(id);


--
-- Name: facture_client_version facture_client_version_utilisateur_id_fkey; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.facture_client_version
    ADD CONSTRAINT facture_client_version_utilisateur_id_fkey FOREIGN KEY (utilisateur_id) REFERENCES public.utilisateur(id);


--
-- Name: inventaire inventaire_campagne_id_fkey; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.inventaire
    ADD CONSTRAINT inventaire_campagne_id_fkey FOREIGN KEY (campagne_id) REFERENCES public.campagne_inventaire(id);


--
-- Name: inventaire inventaire_commence_par_fkey; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.inventaire
    ADD CONSTRAINT inventaire_commence_par_fkey FOREIGN KEY (commence_par) REFERENCES public.utilisateur(id);


--
-- Name: inventaire inventaire_termine_par_fkey; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.inventaire
    ADD CONSTRAINT inventaire_termine_par_fkey FOREIGN KEY (termine_par) REFERENCES public.utilisateur(id);


--
-- Name: journal_audit journal_audit_autorise_par_fkey; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.journal_audit
    ADD CONSTRAINT journal_audit_autorise_par_fkey FOREIGN KEY (autorise_par) REFERENCES public.utilisateur(id);


--
-- Name: journal_audit journal_audit_permission_code_fkey; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.journal_audit
    ADD CONSTRAINT journal_audit_permission_code_fkey FOREIGN KEY (permission_code) REFERENCES public.permission(code);


--
-- Name: journal_audit journal_audit_utilisateur_id_fkey; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.journal_audit
    ADD CONSTRAINT journal_audit_utilisateur_id_fkey FOREIGN KEY (utilisateur_id) REFERENCES public.utilisateur(id);


--
-- Name: journal_sauvegarde journal_sauvegarde_utilisateur_id_fkey; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.journal_sauvegarde
    ADD CONSTRAINT journal_sauvegarde_utilisateur_id_fkey FOREIGN KEY (utilisateur_id) REFERENCES public.utilisateur(id);


--
-- Name: ligne_commande ligne_commande_commande_id_fkey; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.ligne_commande
    ADD CONSTRAINT ligne_commande_commande_id_fkey FOREIGN KEY (commande_id) REFERENCES public.commande_fournisseur(id) ON DELETE CASCADE;


--
-- Name: ligne_commande ligne_commande_produit_id_fkey; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.ligne_commande
    ADD CONSTRAINT ligne_commande_produit_id_fkey FOREIGN KEY (produit_id) REFERENCES public.produit(id);


--
-- Name: ligne_inventaire ligne_inventaire_compte_par_fkey; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.ligne_inventaire
    ADD CONSTRAINT ligne_inventaire_compte_par_fkey FOREIGN KEY (compte_par) REFERENCES public.utilisateur(id);


--
-- Name: ligne_inventaire ligne_inventaire_decide_par_fkey; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.ligne_inventaire
    ADD CONSTRAINT ligne_inventaire_decide_par_fkey FOREIGN KEY (decide_par) REFERENCES public.utilisateur(id);


--
-- Name: ligne_inventaire ligne_inventaire_inventaire_id_fkey; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.ligne_inventaire
    ADD CONSTRAINT ligne_inventaire_inventaire_id_fkey FOREIGN KEY (inventaire_id) REFERENCES public.inventaire(id);


--
-- Name: ligne_inventaire ligne_inventaire_lot_id_fkey; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.ligne_inventaire
    ADD CONSTRAINT ligne_inventaire_lot_id_fkey FOREIGN KEY (lot_id) REFERENCES public.lot(id);


--
-- Name: ligne_retour_fournisseur ligne_retour_fournisseur_lot_id_fkey; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.ligne_retour_fournisseur
    ADD CONSTRAINT ligne_retour_fournisseur_lot_id_fkey FOREIGN KEY (lot_id) REFERENCES public.lot(id);


--
-- Name: ligne_retour_fournisseur ligne_retour_fournisseur_retour_id_fkey; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.ligne_retour_fournisseur
    ADD CONSTRAINT ligne_retour_fournisseur_retour_id_fkey FOREIGN KEY (retour_id) REFERENCES public.retour_fournisseur(id) ON DELETE CASCADE;


--
-- Name: ligne_vente ligne_vente_lot_id_fkey; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.ligne_vente
    ADD CONSTRAINT ligne_vente_lot_id_fkey FOREIGN KEY (lot_id) REFERENCES public.lot(id);


--
-- Name: ligne_vente ligne_vente_produit_id_fkey; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.ligne_vente
    ADD CONSTRAINT ligne_vente_produit_id_fkey FOREIGN KEY (produit_id) REFERENCES public.produit(id);


--
-- Name: ligne_vente ligne_vente_vente_id_fkey; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.ligne_vente
    ADD CONSTRAINT ligne_vente_vente_id_fkey FOREIGN KEY (vente_id) REFERENCES public.vente(id) ON DELETE CASCADE;


--
-- Name: lot lot_fournisseur_id_fkey; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.lot
    ADD CONSTRAINT lot_fournisseur_id_fkey FOREIGN KEY (fournisseur_id) REFERENCES public.fournisseur(id);


--
-- Name: lot lot_ligne_commande_id_fkey; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.lot
    ADD CONSTRAINT lot_ligne_commande_id_fkey FOREIGN KEY (ligne_commande_id) REFERENCES public.ligne_commande(id);


--
-- Name: lot lot_produit_id_fkey; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.lot
    ADD CONSTRAINT lot_produit_id_fkey FOREIGN KEY (produit_id) REFERENCES public.produit(id);


--
-- Name: lot lot_reception_id_fkey; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.lot
    ADD CONSTRAINT lot_reception_id_fkey FOREIGN KEY (reception_id) REFERENCES public.reception(id);


--
-- Name: message_sortant message_sortant_destinataire_id_fkey; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.message_sortant
    ADD CONSTRAINT message_sortant_destinataire_id_fkey FOREIGN KEY (destinataire_id) REFERENCES public.destinataire_notification(id);


--
-- Name: mouvement_stock mouvement_stock_lot_id_fkey; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.mouvement_stock
    ADD CONSTRAINT mouvement_stock_lot_id_fkey FOREIGN KEY (lot_id) REFERENCES public.lot(id);


--
-- Name: mouvement_stock mouvement_stock_reception_id_fkey; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.mouvement_stock
    ADD CONSTRAINT mouvement_stock_reception_id_fkey FOREIGN KEY (reception_id) REFERENCES public.reception(id);


--
-- Name: mouvement_stock mouvement_stock_utilisateur_id_fkey; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.mouvement_stock
    ADD CONSTRAINT mouvement_stock_utilisateur_id_fkey FOREIGN KEY (utilisateur_id) REFERENCES public.utilisateur(id);


--
-- Name: mouvement_tresorerie mouvement_tresorerie_bordereau_id_fkey; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.mouvement_tresorerie
    ADD CONSTRAINT mouvement_tresorerie_bordereau_id_fkey FOREIGN KEY (bordereau_id) REFERENCES public.bordereau(id);


--
-- Name: mouvement_tresorerie mouvement_tresorerie_compte_id_fkey; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.mouvement_tresorerie
    ADD CONSTRAINT mouvement_tresorerie_compte_id_fkey FOREIGN KEY (compte_id) REFERENCES public.compte_tresorerie(id);


--
-- Name: mouvement_tresorerie mouvement_tresorerie_organisme_id_fkey; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.mouvement_tresorerie
    ADD CONSTRAINT mouvement_tresorerie_organisme_id_fkey FOREIGN KEY (organisme_id) REFERENCES public.organisme(id);


--
-- Name: mouvement_tresorerie mouvement_tresorerie_session_caisse_id_fkey; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.mouvement_tresorerie
    ADD CONSTRAINT mouvement_tresorerie_session_caisse_id_fkey FOREIGN KEY (session_caisse_id) REFERENCES public.session_caisse(id);


--
-- Name: mouvement_tresorerie mouvement_tresorerie_transfert_de_fkey; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.mouvement_tresorerie
    ADD CONSTRAINT mouvement_tresorerie_transfert_de_fkey FOREIGN KEY (transfert_de) REFERENCES public.mouvement_tresorerie(id);


--
-- Name: mouvement_tresorerie mouvement_tresorerie_utilisateur_id_fkey; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.mouvement_tresorerie
    ADD CONSTRAINT mouvement_tresorerie_utilisateur_id_fkey FOREIGN KEY (utilisateur_id) REFERENCES public.utilisateur(id);


--
-- Name: nomenclature_produit nomenclature_produit_version_id_fkey; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.nomenclature_produit
    ADD CONSTRAINT nomenclature_produit_version_id_fkey FOREIGN KEY (version_id) REFERENCES public.nomenclature_version(id);


--
-- Name: nomenclature_version nomenclature_version_utilisateur_id_fkey; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.nomenclature_version
    ADD CONSTRAINT nomenclature_version_utilisateur_id_fkey FOREIGN KEY (utilisateur_id) REFERENCES public.utilisateur(id);


--
-- Name: ordonnance ordonnance_medecin_id_fkey; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.ordonnance
    ADD CONSTRAINT ordonnance_medecin_id_fkey FOREIGN KEY (medecin_id) REFERENCES public.medecin(id);


--
-- Name: ordonnance ordonnance_patient_id_fkey; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.ordonnance
    ADD CONSTRAINT ordonnance_patient_id_fkey FOREIGN KEY (patient_id) REFERENCES public.patient(id);


--
-- Name: paiement paiement_patient_id_fkey; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.paiement
    ADD CONSTRAINT paiement_patient_id_fkey FOREIGN KEY (patient_id) REFERENCES public.patient(id);


--
-- Name: paiement paiement_session_caisse_id_fkey; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.paiement
    ADD CONSTRAINT paiement_session_caisse_id_fkey FOREIGN KEY (session_caisse_id) REFERENCES public.session_caisse(id);


--
-- Name: paiement paiement_utilisateur_id_fkey; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.paiement
    ADD CONSTRAINT paiement_utilisateur_id_fkey FOREIGN KEY (utilisateur_id) REFERENCES public.utilisateur(id);


--
-- Name: paiement paiement_vente_id_fkey; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.paiement
    ADD CONSTRAINT paiement_vente_id_fkey FOREIGN KEY (vente_id) REFERENCES public.vente(id);


--
-- Name: panier_attente panier_attente_utilisateur_id_fkey; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.panier_attente
    ADD CONSTRAINT panier_attente_utilisateur_id_fkey FOREIGN KEY (utilisateur_id) REFERENCES public.utilisateur(id);


--
-- Name: patient patient_organisme_id_fkey; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.patient
    ADD CONSTRAINT patient_organisme_id_fkey FOREIGN KEY (organisme_id) REFERENCES public.organisme(id);


--
-- Name: produit produit_dci_id_fkey; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.produit
    ADD CONSTRAINT produit_dci_id_fkey FOREIGN KEY (dci_id) REFERENCES public.dci(id);


--
-- Name: produit produit_emplacement_id_fkey; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.produit
    ADD CONSTRAINT produit_emplacement_id_fkey FOREIGN KEY (emplacement_id) REFERENCES public.emplacement(id);


--
-- Name: produit produit_laboratoire_id_fkey; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.produit
    ADD CONSTRAINT produit_laboratoire_id_fkey FOREIGN KEY (laboratoire_id) REFERENCES public.laboratoire(id);


--
-- Name: produit produit_reserve_id_fkey; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.produit
    ADD CONSTRAINT produit_reserve_id_fkey FOREIGN KEY (reserve_id) REFERENCES public.emplacement(id);


--
-- Name: reception reception_commande_id_fkey; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.reception
    ADD CONSTRAINT reception_commande_id_fkey FOREIGN KEY (commande_id) REFERENCES public.commande_fournisseur(id);


--
-- Name: reception reception_fournisseur_id_fkey; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.reception
    ADD CONSTRAINT reception_fournisseur_id_fkey FOREIGN KEY (fournisseur_id) REFERENCES public.fournisseur(id);


--
-- Name: reception reception_utilisateur_id_fkey; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.reception
    ADD CONSTRAINT reception_utilisateur_id_fkey FOREIGN KEY (utilisateur_id) REFERENCES public.utilisateur(id);


--
-- Name: registre_psychotrope registre_psychotrope_ligne_vente_id_fkey; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.registre_psychotrope
    ADD CONSTRAINT registre_psychotrope_ligne_vente_id_fkey FOREIGN KEY (ligne_vente_id) REFERENCES public.ligne_vente(id);


--
-- Name: registre_psychotrope registre_psychotrope_lot_id_fkey; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.registre_psychotrope
    ADD CONSTRAINT registre_psychotrope_lot_id_fkey FOREIGN KEY (lot_id) REFERENCES public.lot(id);


--
-- Name: registre_psychotrope registre_psychotrope_medecin_id_fkey; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.registre_psychotrope
    ADD CONSTRAINT registre_psychotrope_medecin_id_fkey FOREIGN KEY (medecin_id) REFERENCES public.medecin(id);


--
-- Name: registre_psychotrope registre_psychotrope_mouvement_id_fkey; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.registre_psychotrope
    ADD CONSTRAINT registre_psychotrope_mouvement_id_fkey FOREIGN KEY (mouvement_id) REFERENCES public.mouvement_stock(id);


--
-- Name: registre_psychotrope registre_psychotrope_ordonnance_id_fkey; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.registre_psychotrope
    ADD CONSTRAINT registre_psychotrope_ordonnance_id_fkey FOREIGN KEY (ordonnance_id) REFERENCES public.ordonnance(id);


--
-- Name: registre_psychotrope registre_psychotrope_patient_id_fkey; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.registre_psychotrope
    ADD CONSTRAINT registre_psychotrope_patient_id_fkey FOREIGN KEY (patient_id) REFERENCES public.patient(id);


--
-- Name: registre_psychotrope registre_psychotrope_utilisateur_id_fkey; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.registre_psychotrope
    ADD CONSTRAINT registre_psychotrope_utilisateur_id_fkey FOREIGN KEY (utilisateur_id) REFERENCES public.utilisateur(id);


--
-- Name: reglement_facture_client reglement_facture_client_facture_id_fkey; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.reglement_facture_client
    ADD CONSTRAINT reglement_facture_client_facture_id_fkey FOREIGN KEY (facture_id) REFERENCES public.facture_client(id);


--
-- Name: reglement_facture_client reglement_facture_client_mouvement_id_fkey; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.reglement_facture_client
    ADD CONSTRAINT reglement_facture_client_mouvement_id_fkey FOREIGN KEY (mouvement_id) REFERENCES public.mouvement_tresorerie(id);


--
-- Name: reglement_facture reglement_facture_reception_id_fkey; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.reglement_facture
    ADD CONSTRAINT reglement_facture_reception_id_fkey FOREIGN KEY (reception_id) REFERENCES public.reception(id);


--
-- Name: reglement_facture reglement_facture_reglement_id_fkey; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.reglement_facture
    ADD CONSTRAINT reglement_facture_reglement_id_fkey FOREIGN KEY (reglement_id) REFERENCES public.reglement_fournisseur(id);


--
-- Name: reglement_fournisseur reglement_fournisseur_compte_id_fkey; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.reglement_fournisseur
    ADD CONSTRAINT reglement_fournisseur_compte_id_fkey FOREIGN KEY (compte_id) REFERENCES public.compte_tresorerie(id);


--
-- Name: reglement_fournisseur reglement_fournisseur_fournisseur_id_fkey; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.reglement_fournisseur
    ADD CONSTRAINT reglement_fournisseur_fournisseur_id_fkey FOREIGN KEY (fournisseur_id) REFERENCES public.fournisseur(id);


--
-- Name: reglement_fournisseur reglement_fournisseur_session_caisse_id_fkey; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.reglement_fournisseur
    ADD CONSTRAINT reglement_fournisseur_session_caisse_id_fkey FOREIGN KEY (session_caisse_id) REFERENCES public.session_caisse(id);


--
-- Name: reglement_fournisseur reglement_fournisseur_utilisateur_id_fkey; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.reglement_fournisseur
    ADD CONSTRAINT reglement_fournisseur_utilisateur_id_fkey FOREIGN KEY (utilisateur_id) REFERENCES public.utilisateur(id);


--
-- Name: remise_cheque_paiement remise_cheque_paiement_paiement_id_fkey; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.remise_cheque_paiement
    ADD CONSTRAINT remise_cheque_paiement_paiement_id_fkey FOREIGN KEY (paiement_id) REFERENCES public.paiement(id);


--
-- Name: remise_cheque_paiement remise_cheque_paiement_remise_id_fkey; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.remise_cheque_paiement
    ADD CONSTRAINT remise_cheque_paiement_remise_id_fkey FOREIGN KEY (remise_id) REFERENCES public.remise_cheques(id);


--
-- Name: remise_cheques remise_cheques_compte_id_fkey; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.remise_cheques
    ADD CONSTRAINT remise_cheques_compte_id_fkey FOREIGN KEY (compte_id) REFERENCES public.compte_tresorerie(id);


--
-- Name: remise_cheques remise_cheques_utilisateur_id_fkey; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.remise_cheques
    ADD CONSTRAINT remise_cheques_utilisateur_id_fkey FOREIGN KEY (utilisateur_id) REFERENCES public.utilisateur(id);


--
-- Name: retour_client_ligne retour_client_ligne_ligne_vente_id_fkey; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.retour_client_ligne
    ADD CONSTRAINT retour_client_ligne_ligne_vente_id_fkey FOREIGN KEY (ligne_vente_id) REFERENCES public.ligne_vente(id);


--
-- Name: retour_client_ligne retour_client_ligne_retour_id_fkey; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.retour_client_ligne
    ADD CONSTRAINT retour_client_ligne_retour_id_fkey FOREIGN KEY (retour_id) REFERENCES public.retour_client(id);


--
-- Name: retour_client retour_client_session_caisse_id_fkey; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.retour_client
    ADD CONSTRAINT retour_client_session_caisse_id_fkey FOREIGN KEY (session_caisse_id) REFERENCES public.session_caisse(id);


--
-- Name: retour_client retour_client_utilisateur_id_fkey; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.retour_client
    ADD CONSTRAINT retour_client_utilisateur_id_fkey FOREIGN KEY (utilisateur_id) REFERENCES public.utilisateur(id);


--
-- Name: retour_client retour_client_valide_par_fkey; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.retour_client
    ADD CONSTRAINT retour_client_valide_par_fkey FOREIGN KEY (valide_par) REFERENCES public.utilisateur(id);


--
-- Name: retour_client retour_client_vente_id_fkey; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.retour_client
    ADD CONSTRAINT retour_client_vente_id_fkey FOREIGN KEY (vente_id) REFERENCES public.vente(id);


--
-- Name: retour_fournisseur retour_fournisseur_fournisseur_id_fkey; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.retour_fournisseur
    ADD CONSTRAINT retour_fournisseur_fournisseur_id_fkey FOREIGN KEY (fournisseur_id) REFERENCES public.fournisseur(id);


--
-- Name: retour_fournisseur retour_fournisseur_utilisateur_id_fkey; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.retour_fournisseur
    ADD CONSTRAINT retour_fournisseur_utilisateur_id_fkey FOREIGN KEY (utilisateur_id) REFERENCES public.utilisateur(id);


--
-- Name: role_permission role_permission_permission_code_fkey; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.role_permission
    ADD CONSTRAINT role_permission_permission_code_fkey FOREIGN KEY (permission_code) REFERENCES public.permission(code);


--
-- Name: role_permission role_permission_role_id_fkey; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.role_permission
    ADD CONSTRAINT role_permission_role_id_fkey FOREIGN KEY (role_id) REFERENCES public.role(id) ON DELETE CASCADE;


--
-- Name: scan_reception scan_reception_produit_id_fkey; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.scan_reception
    ADD CONSTRAINT scan_reception_produit_id_fkey FOREIGN KEY (produit_id) REFERENCES public.produit(id);


--
-- Name: scan_reception scan_reception_utilisateur_id_fkey; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.scan_reception
    ADD CONSTRAINT scan_reception_utilisateur_id_fkey FOREIGN KEY (utilisateur_id) REFERENCES public.utilisateur(id);


--
-- Name: session_caisse session_caisse_releve_de_fkey; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.session_caisse
    ADD CONSTRAINT session_caisse_releve_de_fkey FOREIGN KEY (releve_de) REFERENCES public.session_caisse(id);


--
-- Name: session_caisse session_caisse_utilisateur_id_fkey; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.session_caisse
    ADD CONSTRAINT session_caisse_utilisateur_id_fkey FOREIGN KEY (utilisateur_id) REFERENCES public.utilisateur(id);


--
-- Name: utilisateur utilisateur_role_id_fkey; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.utilisateur
    ADD CONSTRAINT utilisateur_role_id_fkey FOREIGN KEY (role_id) REFERENCES public.role(id);


--
-- Name: vente vente_bordereau_id_fkey; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.vente
    ADD CONSTRAINT vente_bordereau_id_fkey FOREIGN KEY (bordereau_id) REFERENCES public.bordereau(id);


--
-- Name: vente vente_ordonnance_id_fkey; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.vente
    ADD CONSTRAINT vente_ordonnance_id_fkey FOREIGN KEY (ordonnance_id) REFERENCES public.ordonnance(id);


--
-- Name: vente vente_organisme_id_fkey; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.vente
    ADD CONSTRAINT vente_organisme_id_fkey FOREIGN KEY (organisme_id) REFERENCES public.organisme(id);


--
-- Name: vente vente_patient_id_fkey; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.vente
    ADD CONSTRAINT vente_patient_id_fkey FOREIGN KEY (patient_id) REFERENCES public.patient(id);


--
-- Name: vente vente_session_caisse_id_fkey; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.vente
    ADD CONSTRAINT vente_session_caisse_id_fkey FOREIGN KEY (session_caisse_id) REFERENCES public.session_caisse(id);


--
-- Name: vente vente_utilisateur_id_fkey; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.vente
    ADD CONSTRAINT vente_utilisateur_id_fkey FOREIGN KEY (utilisateur_id) REFERENCES public.utilisateur(id);


--
-- PostgreSQL database dump complete
--

\unrestrict 9XH77KG8B6v59htLgCaGmoeu9kRmbcKuQ2TcsCNOph5QQoRNyIVgeEHeqhfipGh

