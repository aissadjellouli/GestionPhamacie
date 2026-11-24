-- =====================================================================
-- Pharma Officine : CRÉATION COMPLÈTE DU SCHÉMA (installation neuve)
--
-- Équivaut aux scripts database/migration/001 à 167 appliqués dans l'ordre (vérifié :
-- même structure). Pour une base déjà installée, rejouer TOUTE la suite
-- database/migration depuis le script 001 (mettre_a_jour_base_officine.ps1), jamais ce fichier ni un script isolé.
--
-- Base vide uniquement (créée vide, ou après clean.sql). S'arrête sans rien
-- faire si la base contient déjà le schéma.
--
--   psql -h <serveur> -U pharma_app -d <base> -v ON_ERROR_STOP=1 -f database/init/schema.sql
--
-- Puis : database/init/seed_referentiel.sql (référentiel de départ).
-- Fichier produit par pg_dump --schema-only à partir d'une base construite
-- par les scripts 001 à 167 ; à régénérer à chaque nouveau script.
-- =====================================================================

SELECT to_regclass('public.utilisateur') IS NOT NULL AS schema_deja_present \gset
\if :schema_deja_present
    \echo 'La base contient déjà le schéma : rien n''est fait. Base existante : rejouer toute la suite database/migration depuis 001.'
    \quit
\endif
--
-- PostgreSQL database dump
--

\restrict bYJAwJgfijFwLe244M93hWc8IXyQJ3Sqz9zg8n98StjZOpakXtshPD3oqhUvNQz

-- Dumped from database version 18.6
-- Dumped by pg_dump version 18.6

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
-- Name: SCHEMA public; Type: COMMENT; Schema: -; Owner: -
--

COMMENT ON SCHEMA public IS '';


--
-- Name: pg_trgm; Type: EXTENSION; Schema: -; Owner: -
--

CREATE EXTENSION IF NOT EXISTS pg_trgm WITH SCHEMA public;


--
-- Name: EXTENSION pg_trgm; Type: COMMENT; Schema: -; Owner: -
--

COMMENT ON EXTENSION pg_trgm IS 'text similarity measurement and index searching based on trigrams';


--
-- Name: pgcrypto; Type: EXTENSION; Schema: -; Owner: -
--

CREATE EXTENSION IF NOT EXISTS pgcrypto WITH SCHEMA public;


--
-- Name: EXTENSION pgcrypto; Type: COMMENT; Schema: -; Owner: -
--

COMMENT ON EXTENSION pgcrypto IS 'cryptographic functions';


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
    v_nomencl  text;
    v_ancien   text;
    v_classe   text;
    v_categorie text;
    v_existant bigint;
    r          record;
BEGIN
    IF (SELECT statut FROM import.session WHERE id = p_session) <> 'en_cours' THEN
        RAISE EXCEPTION 'Session % déjà chargée ou annulée', p_session;
    END IF;
    IF import.controler(p_session) > 0 THEN
        RAISE EXCEPTION 'Anomalies bloquantes restantes : chargement refusé';
    END IF;

    -- Produits absents du catalogue, créés à la demande du pharmacien. Classe : la plus stricte entre la nomenclature
    -- et l'ancien logiciel. Absent de la nomenclature : à vérifier par le titulaire, non vendable d'ici là (037).
    -- Catégorie (154) : n° trouvé dans la nomenclature, médicament ; sinon parapharmacie seulement si la règle stricte
    -- (parapharmacie_certaine) est remplie ; sinon médicament, à classer par le titulaire (Administration › Nomenclature)
    FOR r IN
        SELECT s.*
        FROM import.correspondance_produit c
        JOIN import.produit_src s ON s.session_id = c.session_id AND s.ref_source = c.ref_source
        WHERE c.session_id = p_session AND c.methode = 'nouveau' AND c.produit_id IS NULL
    LOOP
        v_nomencl := import.classe_nomenclature(r.num_enregistrement);
        v_ancien  := import.classe_src(r.classe);
        v_classe  := CASE WHEN COALESCE(import.rang_classe(v_ancien), -1) > COALESCE(import.rang_classe(v_nomencl), -1)
                          THEN v_ancien ELSE COALESCE(v_nomencl, 'libre') END;
        v_categorie := CASE WHEN v_nomencl IS NULL
                                 AND parapharmacie_certaine(r.num_enregistrement, NULL, v_classe, false, NULL, r.nom)
                            THEN 'parapharmacie' ELSE 'medicament' END;
        INSERT INTO produit (code_barre, num_enregistrement, nom_commercial, dosage, forme, prix_public, classe, a_verifier, categorie)
        VALUES (NULLIF(btrim(r.code_barre), ''), NULLIF(btrim(r.num_enregistrement), ''),
                r.nom, r.dosage, r.forme, COALESCE(import.nombre(r.prix_vente), 0),
                v_classe, v_nomencl IS NULL, v_categorie)
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

    -- Grossiste fictif, seulement pour du stock dont la provenance est inconnue
    IF EXISTS (SELECT 1
               FROM import.stock_src st
               LEFT JOIN import.fournisseur_src fs
                      ON fs.session_id = st.session_id AND fs.ref_source = st.fournisseur_ref
               WHERE st.session_id = p_session AND import.nombre(st.quantite) > 0 AND fs.fournisseur_id IS NULL) THEN
        INSERT INTO fournisseur (raison_sociale) VALUES ('Reprise de stock (ancien logiciel)')
        RETURNING id INTO v_reprise;
    END IF;

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

    -- Lots : un par produit, grossiste, n° de lot et date (date inconnue : provisoirement à un an, et signalée à vérifier).
    -- Prix d'achat : celui du lot, sinon celui du produit ; moyenne pondérée si plusieurs lignes forment le même lot
    INSERT INTO lot (produit_id, reception_id, fournisseur_id, numero_lot,
                     date_peremption, quantite_disponible, prix_achat_ht, date_a_verifier)
    SELECT c.produit_id, rc.reception_id, rc.fournisseur_id, x.lot_reprise,
           COALESCE(x.dte, current_date + 365),
           SUM(x.qte)::int,
           round(SUM(x.qte * x.pa) / SUM(x.qte), 2),
           x.dte IS NULL
    FROM (
        SELECT st.*,
               import.nombre(st.quantite)        AS qte,
               import.date_src(st.date_peremption) AS dte,
               COALESCE(import.nombre(st.prix_achat), import.nombre(ps.prix_achat), 0) AS pa,
               upper(COALESCE(NULLIF(btrim(st.numero_lot), ''),
                        'REPRISE-' || COALESCE(to_char(import.date_src(st.date_peremption), 'YYYYMM'), 'ND')))
                                                 AS lot_reprise   -- normalisé comme à la réception (018)
        FROM import.stock_src st
        JOIN import.produit_src ps
             ON ps.session_id = st.session_id AND ps.ref_source = st.ref_produit
        WHERE st.session_id = p_session
    ) x
    JOIN import.correspondance_produit c
         ON c.session_id = x.session_id AND c.ref_source = x.ref_produit
    LEFT JOIN import.fournisseur_src fs
         ON fs.session_id = x.session_id AND fs.ref_source = x.fournisseur_ref
    JOIN _rec rc ON rc.fournisseur_id = COALESCE(fs.fournisseur_id, v_reprise)
    WHERE x.qte > 0
    GROUP BY c.produit_id, rc.reception_id, rc.fournisseur_id, x.lot_reprise, x.dte;

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

    -- Clients et crédits en cours, dans l'ordre de l'export. N° d'assuré déjà pris (patient en base, ou ligne précédente
    -- de l'export) : patient non créé, mis en attente de validation avec sa dette (092) ; le reste de l'import continue
    FOR r IN SELECT * FROM import.client_src WHERE session_id = p_session ORDER BY ligne NULLS LAST, ref_source LOOP
        v_existant := NULL;
        IF r.num_assure IS NOT NULL AND regexp_replace(r.num_assure, '\D', '', 'g') <> '' THEN
            SELECT pt.id INTO v_existant FROM patient pt
            WHERE pt.num_assure IS NOT NULL AND regexp_replace(pt.num_assure, '\D', '', 'g') <> ''
              AND regexp_replace(pt.num_assure, '\D', '', 'g') = regexp_replace(r.num_assure, '\D', '', 'g')
            ORDER BY pt.id LIMIT 1;
        END IF;
        IF v_existant IS NOT NULL THEN
            INSERT INTO import.doublon_patient (session_id, ref_source, ligne, nom, prenom, telephone, num_assure, solde_credit,
                                                patient_existant_id, origine)
            VALUES (p_session, r.ref_source, r.ligne, r.nom, r.prenom, r.telephone, r.num_assure,
                    COALESCE(import.nombre(r.solde_credit), 0), v_existant,
                    CASE WHEN EXISTS (SELECT 1 FROM import.client_src x WHERE x.session_id = p_session AND x.patient_id = v_existant)
                         THEN 'fichier' ELSE 'base' END)
            ON CONFLICT (session_id, ref_source) DO NOTHING;
            CONTINUE;
        END IF;
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
-- Name: charger_historique_chifa(bigint); Type: FUNCTION; Schema: import; Owner: -
--

CREATE FUNCTION import.charger_historique_chifa(p_session bigint) RETURNS integer
    LANGUAGE plpgsql
    AS $$
DECLARE
    n integer;
    m integer;
BEGIN
    INSERT INTO historique_chifa (session_id, logiciel_source, organisme, num_facture, date_facture, num_assure, nom_assure,
                                  montant_total, part_chifa, part_assure)
    SELECT h.session_id, s.logiciel_source,
           CASE WHEN upper(btrim(h.organisme)) IN ('CNAS', 'CASNOS') THEN upper(btrim(h.organisme)) ELSE 'CHIFA' END,
           btrim(h.num_facture), import.date_src(h.date_facture),
           NULLIF(btrim(h.num_assure), ''), NULLIF(btrim(h.nom_assure), ''),
           round(import.nombre(h.montant_total), 2), round(import.nombre(h.part_chifa), 2), round(import.nombre(h.part_assure), 2)
    FROM import.historique_chifa_src h
    JOIN import.session s ON s.id = h.session_id
    WHERE h.session_id = p_session AND import.date_src(h.date_facture) IS NOT NULL
    ON CONFLICT ON CONSTRAINT ux_historique_organisme_facture DO NOTHING;
    GET DIAGNOSTICS n = ROW_COUNT;

    INSERT INTO historique_chifa (session_id, logiciel_source, organisme, num_facture, date_facture, num_assure, nom_assure,
                                  montant_total, part_chifa, part_assure)
    SELECT h.session_id, s.logiciel_source, 'MDN', btrim(h.num_facture), import.date_src(h.date_facture),
           NULLIF(btrim(h.num_affilie), ''), NULLIF(btrim(h.nom_affilie), ''),
           round(import.nombre(h.montant_total), 2), round(import.nombre(h.part_mdn), 2), round(import.nombre(h.part_assure), 2)
    FROM import.historique_mdn_src h
    JOIN import.session s ON s.id = h.session_id
    WHERE h.session_id = p_session AND import.date_src(h.date_facture) IS NOT NULL
    ON CONFLICT ON CONSTRAINT ux_historique_organisme_facture DO NOTHING;
    GET DIAGNOSTICS m = ROW_COUNT;
    RETURN n + m;
END;
$$;


--
-- Name: classe_nomenclature(text); Type: FUNCTION; Schema: import; Owner: -
--

CREATE FUNCTION import.classe_nomenclature(p_num text) RETURNS text
    LANGUAGE sql STABLE
    AS $$
    SELECT v.classe_proposee
    FROM v_nomenclature v
    WHERE v.num_enregistrement = NULLIF(btrim(p_num), '')
    ORDER BY (v.situation = 'en_vigueur') DESC
    LIMIT 1;
$$;


--
-- Name: classe_src(text); Type: FUNCTION; Schema: import; Owner: -
--

CREATE FUNCTION import.classe_src(t text) RETURNS text
    LANGUAGE sql STABLE
    AS $$
    SELECT CASE
        WHEN v = '' THEN NULL
        WHEN v ~ '\mSTUP' OR v ~ '\mTABLEAU\s*B\M' THEN 'stupefiant'
        WHEN v ~ '\mPSY' THEN 'psychotrope'
        WHEN v ~ '\mLISTE?\s*(II|2)\M' OR v ~ '\mTABLEAU\s*C\M' THEN 'liste_ii'
        WHEN v ~ '\mLISTE?\s*(I|1)\M' OR v ~ '\mTABLEAU\s*A\M' THEN 'liste_i'
        WHEN v ~ '\m(LIBRE|NON LISTE|HORS LISTE|AUCUNE|NEANT|OTC)\M' THEN 'libre'
    END
    FROM (SELECT btrim(regexp_replace(majuscule_sans_accent(COALESCE(t, '')), '[_.-]+', ' ', 'g')) AS v) x;
$$;


--
-- Name: controle_apres_chargement(bigint); Type: FUNCTION; Schema: import; Owner: -
--

CREATE FUNCTION import.controle_apres_chargement(p_session bigint) RETURNS TABLE(indicateur text, ancien numeric, nouveau numeric, ecart numeric)
    LANGUAGE sql STABLE
    AS $$
    SELECT t.indicateur, t.ancien, t.nouveau, t.nouveau - t.ancien
    FROM (
        SELECT 1 AS ordre, 'Unités en stock'::text AS indicateur,
               (SELECT COALESCE(SUM(import.nombre(st.quantite)), 0)
                FROM import.stock_src st
                WHERE st.session_id = p_session AND import.nombre(st.quantite) > 0) AS ancien,
               (SELECT COALESCE(SUM(l.quantite_disponible), 0)::numeric
                FROM lot l JOIN reception r ON r.id = l.reception_id
                WHERE r.numero_bl = 'REPRISE-' || p_session) AS nouveau
        UNION ALL
        SELECT 2, 'Valeur du stock au prix d''achat (DA)',
               (SELECT COALESCE(round(SUM(import.nombre(st.quantite)
                                          * COALESCE(import.nombre(st.prix_achat), import.nombre(ps.prix_achat), 0)), 2), 0)
                FROM import.stock_src st
                JOIN import.produit_src ps ON ps.session_id = st.session_id AND ps.ref_source = st.ref_produit
                WHERE st.session_id = p_session AND import.nombre(st.quantite) > 0),
               (SELECT COALESCE(SUM(l.quantite_disponible * l.prix_achat_ht), 0)
                FROM lot l JOIN reception r ON r.id = l.reception_id
                WHERE r.numero_bl = 'REPRISE-' || p_session)
        UNION ALL
        SELECT 3, 'Crédits clients (DA)',
               (SELECT COALESCE(SUM(import.nombre(c.solde_credit)), 0)
                FROM import.client_src c WHERE c.session_id = p_session),
               (SELECT COALESCE(SUM(pt.solde_credit), 0)
                FROM patient pt JOIN import.client_src c ON c.patient_id = pt.id
                WHERE c.session_id = p_session)
        UNION ALL
        SELECT 4, 'Dettes grossistes (DA)',
               (SELECT COALESCE(SUM(import.nombre(f.solde)), 0)
                FROM import.fournisseur_src f WHERE f.session_id = p_session),
               (SELECT COALESCE(SUM(fo.solde), 0)
                FROM fournisseur fo JOIN import.fournisseur_src f ON f.fournisseur_id = fo.id
                WHERE f.session_id = p_session)
        UNION ALL
        SELECT 5, 'Nombre de clients',
               (SELECT count(*) FROM import.client_src c WHERE c.session_id = p_session),
               (SELECT count(*) FROM import.client_src c
                WHERE c.session_id = p_session AND c.patient_id IS NOT NULL)
        UNION ALL
        -- 092 : patients non créés (n° d'assuré déjà pris) ; nouveau = déjà décidés par le titulaire
        SELECT 6, 'Doublons de n° d''assuré (décidés)',
               (SELECT count(*) FROM import.doublon_patient d WHERE d.session_id = p_session),
               (SELECT count(*) FROM import.doublon_patient d WHERE d.session_id = p_session AND d.statut <> 'a_valider')
        UNION ALL
        SELECT 7, 'Dettes des doublons (DA, reprises)',
               (SELECT COALESCE(SUM(d.solde_credit), 0) FROM import.doublon_patient d WHERE d.session_id = p_session),
               (SELECT COALESCE(SUM(d.solde_credit), 0) FROM import.doublon_patient d WHERE d.session_id = p_session AND d.dette = 'rattachee')
    ) t
    ORDER BY t.ordre;
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

    -- Nombres ambigus (« 12.500 ») : jamais devinés, à corriger dans l'export puis à recharger
    INSERT INTO import.anomalie (session_id, entite, ref_source, gravite, message)
    SELECT p_session, a.entite, a.ref_source, 'bloquant',
           format('%s : valeur ambiguë « %s » (douze mille cinq cents, ou douze et demi ?). Écrivez-la sans point de milliers (12500) ou avec une virgule décimale (12,5) dans le fichier, puis rechargez-le', a.champ, a.valeur)
    FROM (SELECT 'stock' AS entite, st.ref_produit AS ref_source, 'Quantité en stock' AS champ, st.quantite AS valeur
          FROM import.stock_src st WHERE st.session_id = p_session
          UNION ALL SELECT 'produit', s.ref_source, 'Prix de vente', s.prix_vente FROM import.produit_src s WHERE s.session_id = p_session
          UNION ALL SELECT 'produit', s.ref_source, 'Prix d''achat', s.prix_achat FROM import.produit_src s WHERE s.session_id = p_session
          UNION ALL SELECT 'stock', st.ref_produit, 'Prix d''achat du lot', st.prix_achat FROM import.stock_src st WHERE st.session_id = p_session
          UNION ALL SELECT 'client', c.ref_source, 'Solde crédit de ' || c.nom, c.solde_credit FROM import.client_src c WHERE c.session_id = p_session
          UNION ALL SELECT 'fournisseur', f.ref_source, 'Solde de ' || f.raison_sociale, f.solde FROM import.fournisseur_src f WHERE f.session_id = p_session) a
    WHERE import.nombre_ambigu(a.valeur);

    -- Produits à créer : même nom, dosage et forme qu'un produit du catalogue ou qu'un autre produit à créer
    -- (la base refuse ce doublon : sans ce contrôle, le chargement échouerait sans explication)
    INSERT INTO import.anomalie (session_id, entite, ref_source, gravite, message)
    SELECT p_session, 'produit', n.ref_source, 'bloquant',
           format('« %s %s %s » à créer : un produit de même nom, dosage et forme existe déjà (catalogue ou autre ligne de l''export). Rapprochez-le de ce produit, ou distinguez son nom', n.nom, COALESCE(n.dosage, ''), COALESCE(n.forme, ''))
    FROM (SELECT s.ref_source, s.nom, s.dosage, s.forme,
                 count(*) OVER (PARTITION BY lower(s.nom), lower(COALESCE(s.dosage, '')), lower(COALESCE(s.forme, ''))) AS memes
          FROM import.correspondance_produit c
          JOIN import.produit_src s ON s.session_id = c.session_id AND s.ref_source = c.ref_source
          WHERE c.session_id = p_session AND c.methode = 'nouveau' AND c.produit_id IS NULL) n
    WHERE n.memes > 1
       OR EXISTS (SELECT 1 FROM produit p WHERE lower(p.nom_commercial) = lower(n.nom)
                  AND lower(COALESCE(p.dosage, '')) = lower(COALESCE(n.dosage, '')) AND lower(COALESCE(p.forme, '')) = lower(COALESCE(n.forme, '')));

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
      AND NOT import.nombre_ambigu(st.quantite)
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
      AND NOT import.nombre_ambigu(c.solde_credit)
      AND import.nombre(c.solde_credit) IS NULL;

    INSERT INTO import.anomalie (session_id, entite, ref_source, gravite, message)
    SELECT p_session, 'fournisseur', f.ref_source, 'bloquant',
           format('Solde illisible pour %s : « %s »', f.raison_sociale, f.solde)
    FROM import.fournisseur_src f
    WHERE f.session_id = p_session
      AND f.solde IS NOT NULL AND btrim(f.solde) <> ''
      AND NOT import.nombre_ambigu(f.solde)
      AND import.nombre(f.solde) IS NULL;

    INSERT INTO import.anomalie (session_id, entite, ref_source, gravite, message)
    SELECT p_session, 'stock', st.ref_produit, 'bloquant',
           format('Prix d''achat du lot illisible ou négatif : « %s »', st.prix_achat)
    FROM import.stock_src st
    WHERE st.session_id = p_session
      AND st.prix_achat IS NOT NULL AND btrim(st.prix_achat) <> ''
      AND NOT import.nombre_ambigu(st.prix_achat)
      AND (import.nombre(st.prix_achat) IS NULL OR import.nombre(st.prix_achat) < 0);

    -- Classe réglementaire de l'ancien logiciel non reconnue : la nomenclature décide, sinon le produit sera à vérifier
    INSERT INTO import.anomalie (session_id, entite, ref_source, gravite, message)
    SELECT p_session, 'produit', s.ref_source, 'avertissement',
           format('« %s » : classe « %s » non reconnue (attendu : libre, liste I, liste II, psychotrope, stupéfiant). Classe de la nomenclature appliquée, sinon produit à vérifier', s.nom, s.classe)
    FROM import.produit_src s
    WHERE s.session_id = p_session
      AND s.classe IS NOT NULL AND btrim(s.classe) <> ''
      AND import.classe_src(s.classe) IS NULL;

    -- Produits à créer sans classe connue de la nomenclature : créés « à vérifier », non vendables avant confirmation
    IF NOT EXISTS (SELECT 1 FROM v_nomenclature) THEN
        INSERT INTO import.anomalie (session_id, entite, ref_source, gravite, message)
        VALUES (p_session, 'produit', NULL, 'avertissement',
                'Nomenclature nationale non importée : tous les produits créés seront à vérifier avant la vente. Importez-la d''abord (Administration › Nomenclature nationale), puis relancez la reprise');
    ELSE
        INSERT INTO import.anomalie (session_id, entite, ref_source, gravite, message)
        SELECT p_session, 'produit', NULL, 'avertissement',
               format('%s produit(s) à créer absent(s) de la nomenclature nationale ou sans n° d''enregistrement : créés « à vérifier », non vendables avant que le titulaire confirme leur classe', count(*))
        FROM import.correspondance_produit c
        JOIN import.produit_src s ON s.session_id = c.session_id AND s.ref_source = c.ref_source
        WHERE c.session_id = p_session AND c.methode = 'nouveau' AND c.produit_id IS NULL
          AND import.classe_nomenclature(s.num_enregistrement) IS NULL
        HAVING count(*) > 0;
    END IF;

    -- Doublons de n° d'assuré (092) : jamais bloquants ; le premier patient du n° est repris, les suivants sont
    -- mis en attente de validation au chargement (import.doublon_patient)
    INSERT INTO import.anomalie (session_id, entite, ref_source, gravite, message)
    SELECT p_session, 'client', NULL, 'avertissement',
           format('%s doublon(s) de n° d''assuré ignoré(s) : le premier patient de chaque n° est repris, les suivants ne sont pas créés et attendent la validation du titulaire (Administration › Doublons d''import), avec leur dette éventuelle', count(*))
    FROM import.doublons_clients(p_session)
    HAVING count(*) > 0;

    INSERT INTO import.anomalie (session_id, entite, ref_source, gravite, message)
    SELECT p_session, 'client', d.ref_source, 'avertissement',
           format('Ligne %s — %s — n° %s (déjà : %s%s)%s',
                  COALESCE(d.ligne::text, '?'), btrim(d.nom || ' ' || COALESCE(d.prenom, '')), d.num_assure,
                  d.existant_nom, CASE d.origine WHEN 'base' THEN ', patient déjà en base' ELSE ', ligne ' || COALESCE(d.existant_ligne::text, '?') || ' du fichier' END,
                  CASE WHEN d.solde_credit <> 0
                       THEN format(' ; dette reprise %s DA mise en attente (rattachée à la fiche gardée ou abandonnée à la validation)', d.solde_credit)
                       ELSE '' END)
    FROM import.doublons_clients(p_session) d
    ORDER BY d.ligne NULLS LAST, d.ref_source;

    SELECT count(*) INTO nb_bloquants
    FROM import.anomalie WHERE session_id = p_session AND gravite = 'bloquant';
    RETURN nb_bloquants;
END;
$$;


--
-- Name: controler_historique_chifa(bigint); Type: FUNCTION; Schema: import; Owner: -
--

CREATE FUNCTION import.controler_historique_chifa(p_session bigint) RETURNS integer
    LANGUAGE plpgsql
    AS $$
DECLARE
    n integer;
BEGIN
    INSERT INTO import.anomalie (session_id, entite, ref_source, gravite, message)
    SELECT p_session, h.entite, h.num_facture, 'avertissement', m.message
    FROM (
        SELECT 'historique_chifa' AS entite, 'Chifa' AS libelle, num_facture, date_facture, montant_total, part_chifa AS part_org, part_assure, organisme
        FROM import.historique_chifa_src WHERE session_id = p_session
        UNION ALL
        SELECT 'historique_mdn', 'MDN', num_facture, date_facture, montant_total, part_mdn, part_assure, NULL
        FROM import.historique_mdn_src WHERE session_id = p_session
    ) h
    CROSS JOIN LATERAL (
        SELECT 'Date de facture illisible (« ' || h.date_facture || ' ») : facture non reprise dans l''historique' AS message
        WHERE import.date_src(h.date_facture) IS NULL
        UNION ALL
        SELECT 'Organisme « ' || h.organisme || ' » inconnu (CNAS ou CASNOS attendu) : repris comme Chifa'
        WHERE NULLIF(btrim(h.organisme), '') IS NOT NULL AND upper(btrim(h.organisme)) NOT IN ('CNAS', 'CASNOS', 'CHIFA')
        UNION ALL
        SELECT 'Montant ambigu (« ' || x.v || ' ») : point des milliers ou point décimal ? Choisissez le séparateur décimal du profil'
        FROM unnest(ARRAY[h.montant_total, h.part_org, h.part_assure]) x(v) WHERE import.nombre_ambigu(x.v)
        UNION ALL
        SELECT 'Montant illisible (« ' || x.v || ' ») : repris vide'
        FROM unnest(ARRAY[h.montant_total, h.part_org, h.part_assure]) x(v)
        WHERE NULLIF(btrim(x.v), '') IS NOT NULL AND NOT import.nombre_ambigu(x.v) AND import.nombre(x.v) IS NULL
        UNION ALL
        SELECT 'Total (' || import.nombre(h.montant_total) || ') différent de part ' || h.libelle || ' + part assuré ('
               || (import.nombre(h.part_org) + import.nombre(h.part_assure)) || ')'
        WHERE abs(import.nombre(h.montant_total) - import.nombre(h.part_org) - import.nombre(h.part_assure)) > 0.01
    ) m;
    GET DIAGNOSTICS n = ROW_COUNT;
    RETURN n;
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
-- Name: decider_doublon_patient(bigint, text, text, text, bigint); Type: FUNCTION; Schema: import; Owner: -
--

CREATE FUNCTION import.decider_doublon_patient(p_id bigint, p_decision text, p_dette text, p_num_assure text, p_utilisateur_id bigint) RETURNS bigint
    LANGUAGE plpgsql
    AS $$
DECLARE
    d       import.doublon_patient%ROWTYPE;
    v_cible bigint;
    v_cle   text := regexp_replace(COALESCE(p_num_assure, ''), '\D', '', 'g');
BEGIN
    SELECT * INTO d FROM import.doublon_patient WHERE id = p_id FOR UPDATE;
    IF NOT FOUND THEN
        RAISE EXCEPTION 'Doublon d''import introuvable.';
    END IF;
    IF d.statut <> 'a_valider' THEN
        RAISE EXCEPTION 'Ce doublon a déjà été traité : rien n''a été modifié.';
    END IF;
    IF p_decision NOT IN ('garder_existant', 'remplacer', 'ayant_droit') THEN
        RAISE EXCEPTION 'Décision inconnue : %', p_decision;
    END IF;
    IF d.solde_credit <> 0 AND COALESCE(p_dette, '') NOT IN ('rattachee', 'abandonnee') THEN
        RAISE EXCEPTION 'Dette reprise de % DA : choisissez de la rattacher ou de l''abandonner.', d.solde_credit;
    END IF;
    IF d.solde_credit = 0 THEN
        p_dette := NULL;
    END IF;

    IF p_decision = 'ayant_droit' THEN
        IF v_cle = '' THEN
            RAISE EXCEPTION 'Saisissez le n° d''assuré de l''ayant droit (avec son suffixe).';
        END IF;
        IF v_cle = regexp_replace(d.num_assure, '\D', '', 'g') THEN
            RAISE EXCEPTION 'Même n° que la fiche existante : changez le suffixe de l''ayant droit.';
        END IF;
        IF EXISTS (SELECT 1 FROM patient pt WHERE pt.num_assure IS NOT NULL AND regexp_replace(pt.num_assure, '\D', '', 'g') <> ''
                   AND regexp_replace(pt.num_assure, '\D', '', 'g') = v_cle) THEN
            RAISE EXCEPTION 'Le n° % est déjà celui d''un autre patient.', btrim(p_num_assure);
        END IF;
        INSERT INTO patient (nom, prenom, telephone, num_assure, cree_par)
        VALUES (d.nom, d.prenom, d.telephone, btrim(p_num_assure), p_utilisateur_id)
        RETURNING id INTO v_cible;
    ELSE
        v_cible := d.patient_existant_id;
        IF p_decision = 'remplacer' THEN
            UPDATE patient SET nom = d.nom, prenom = d.prenom, telephone = d.telephone, num_assure = d.num_assure
            WHERE id = v_cible;
        END IF;
    END IF;

    IF p_dette = 'rattachee' THEN
        PERFORM credit_contexte('reprise', NULL, p_utilisateur_id, NULL, 'REPRISE-' || d.session_id || ' ' || d.ref_source);
        UPDATE patient SET solde_credit = solde_credit + d.solde_credit WHERE id = v_cible;
    END IF;

    UPDATE import.doublon_patient
    SET statut = CASE p_decision WHEN 'garder_existant' THEN 'garde_existant' WHEN 'remplacer' THEN 'remplace' ELSE 'ayant_droit' END,
        dette = p_dette,
        patient_cree_id = CASE WHEN p_decision = 'ayant_droit' THEN v_cible END,
        decide_par = p_utilisateur_id, decide_le = now()
    WHERE id = p_id;
    UPDATE import.client_src SET patient_id = v_cible
    WHERE session_id = d.session_id AND ref_source = d.ref_source AND p_decision = 'ayant_droit';
    RETURN v_cible;
END;
$$;


--
-- Name: doublons_clients(bigint); Type: FUNCTION; Schema: import; Owner: -
--

CREATE FUNCTION import.doublons_clients(p_session bigint) RETURNS TABLE(ref_source text, ligne integer, nom text, prenom text, telephone text, num_assure text, solde_credit numeric, origine text, existant_patient_id bigint, existant_ref text, existant_ligne integer, existant_nom text)
    LANGUAGE sql STABLE
    AS $$
    WITH c AS (
        SELECT s.*, regexp_replace(s.num_assure, '\D', '', 'g') AS cle,
               row_number() OVER (PARTITION BY regexp_replace(s.num_assure, '\D', '', 'g')
                                  ORDER BY s.ligne NULLS LAST, s.ref_source) AS rang
        FROM import.client_src s
        WHERE s.session_id = p_session AND s.num_assure IS NOT NULL AND regexp_replace(s.num_assure, '\D', '', 'g') <> ''
    )
    SELECT c.ref_source, c.ligne, c.nom, c.prenom, c.telephone, c.num_assure,
           COALESCE(import.nombre(c.solde_credit), 0),
           CASE WHEN b.id IS NOT NULL THEN 'base' ELSE 'fichier' END,
           b.id, p.ref_source, p.ligne,
           COALESCE(btrim(b.nom || ' ' || COALESCE(b.prenom, '')), btrim(p.nom || ' ' || COALESCE(p.prenom, '')))
    FROM c
    JOIN c p ON p.cle = c.cle AND p.rang = 1
    LEFT JOIN LATERAL (
        SELECT pt.id, pt.nom, pt.prenom FROM patient pt
        WHERE pt.num_assure IS NOT NULL AND regexp_replace(pt.num_assure, '\D', '', 'g') <> ''
          AND regexp_replace(pt.num_assure, '\D', '', 'g') = c.cle
          AND pt.id IS DISTINCT FROM c.patient_id
        ORDER BY pt.id LIMIT 1) b ON true
    WHERE c.patient_id IS NULL AND (b.id IS NOT NULL OR c.rang > 1);
$$;


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
    IF s ~ '^-?\d{1,3}\.\d{3}$' THEN                 -- 12.500 : ambigu, vérification manuelle (import.nombre_ambigu)
        RETURN NULL;
    ELSIF s ~ '^-?\d{1,3}(\.\d{3}){2,}$' THEN        -- 1.234.567 : points des milliers
        s := replace(s, '.', '');
    ELSIF s ~ '^-?\d{1,3}(\.\d{3})+,\d+$' THEN       -- 1.234,50
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
-- Name: nombre_ambigu(text); Type: FUNCTION; Schema: import; Owner: -
--

CREATE FUNCTION import.nombre_ambigu(t text) RETURNS boolean
    LANGUAGE sql IMMUTABLE
    AS $_$
    SELECT t IS NOT NULL AND regexp_replace(t, '[\s\u00A0]|DA|DZD', '', 'gi') ~ '^-?\d{1,3}\.\d{3}$';
$_$;


--
-- Name: rang_classe(text); Type: FUNCTION; Schema: import; Owner: -
--

CREATE FUNCTION import.rang_classe(c text) RETURNS integer
    LANGUAGE sql IMMUTABLE
    AS $$
    SELECT CASE c WHEN 'stupefiant' THEN 4 WHEN 'psychotrope' THEN 3 WHEN 'liste_i' THEN 2 WHEN 'liste_ii' THEN 1 WHEN 'libre' THEN 0 END;
$$;


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
    SELECT CASE
        WHEN p_code IN ('stock.donner', 'caisse.remise', 'caisse.remises_auto') THEN EXISTS (
            SELECT 1
            FROM utilisateur u
            JOIN role r ON r.id = u.role_id
            WHERE u.id = p_utilisateur_id
              AND u.actif
              AND (r.systeme
                   OR (p_code = 'stock.donner' AND u.peut_donner)
                   OR (p_code = 'caisse.remise' AND u.remise_max_pct > 0)
                   OR (p_code = 'caisse.remises_auto' AND u.gere_remises_auto)))
        ELSE EXISTS (
            SELECT 1
            FROM utilisateur u
            JOIN role_permission rp ON rp.role_id = u.role_id
            WHERE u.id = p_utilisateur_id
              AND u.actif
              AND rp.permission_code = p_code)
    END;
$$;


--
-- Name: ajout_seul_refuser(); Type: FUNCTION; Schema: public; Owner: -
--

CREATE FUNCTION public.ajout_seul_refuser() RETURNS trigger
    LANGUAGE plpgsql
    AS $$
BEGIN
    RAISE EXCEPTION 'Table % en ajout seul : ni modification, ni suppression, ni vidage', TG_TABLE_NAME;
END;
$$;


--
-- Name: alerter_lots_delaisses(bigint[]); Type: FUNCTION; Schema: public; Owner: -
--

CREATE FUNCTION public.alerter_lots_delaisses(p_produits bigint[]) RETURNS TABLE(o_alerte_id bigint, o_niveau text, o_message text)
    LANGUAGE plpgsql
    AS $$
DECLARE
    c        record;
    v_id     bigint;
    v_ancien text;
BEGIN
    FOR c IN SELECT d.* FROM unnest(p_produits) AS u(pid), LATERAL lots_delaisses(u.pid) d LOOP
        v_id := NULL;
        SELECT a.id, a.niveau INTO v_id, v_ancien FROM alerte a
        WHERE a.type = c.o_type AND a.produit_id = c.o_produit_id AND a.lot_id = c.o_lot_id
          AND a.statut IN ('nouvelle', 'vue', 'reportee');
        IF v_id IS NULL THEN
            INSERT INTO alerte (type, niveau, produit_id, lot_id, message, valeur_dzd)
            VALUES (c.o_type, c.o_niveau, c.o_produit_id, c.o_lot_id, c.o_message, c.o_valeur_dzd)
            RETURNING id INTO v_id;
            o_alerte_id := v_id; o_niveau := c.o_niveau; o_message := c.o_message;
            RETURN NEXT;
        ELSE
            UPDATE alerte SET message = c.o_message, valeur_dzd = c.o_valeur_dzd, niveau = c.o_niveau,
                              statut = CASE WHEN alerte.niveau <> c.o_niveau THEN 'nouvelle' ELSE alerte.statut END,
                              mise_a_jour_le = now()
            WHERE alerte.id = v_id;
            IF v_ancien <> c.o_niveau AND c.o_niveau = 'urgent' THEN
                o_alerte_id := v_id; o_niveau := c.o_niveau; o_message := c.o_message;
                RETURN NEXT;
            END IF;
        END IF;
    END LOOP;
END;
$$;


--
-- Name: annulation_registre_vers_journal(); Type: FUNCTION; Schema: public; Owner: -
--

CREATE FUNCTION public.annulation_registre_vers_journal() RETURNS trigger
    LANGUAGE plpgsql
    AS $$
BEGIN
    INSERT INTO journal_registre_psychotrope (nature, registre_id, annulation_id, motif, utilisateur_id, inscrit_le)
    VALUES ('annulation', NEW.registre_id, NEW.id, NEW.motif, NEW.utilisateur_id, NEW.cree_le);
    RETURN NULL;
END;
$$;


--
-- Name: avoir_mouvement_controle_solde(); Type: FUNCTION; Schema: public; Owner: -
--

CREATE FUNCTION public.avoir_mouvement_controle_solde() RETURNS trigger
    LANGUAGE plpgsql
    AS $$
DECLARE
    v_solde numeric(12,2);
BEGIN
    PERFORM 1 FROM patient WHERE id = NEW.patient_id FOR UPDATE;
    IF NEW.sens = 'debit' THEN
        SELECT COALESCE(SUM(CASE WHEN sens = 'credit' THEN montant ELSE -montant END), 0) INTO v_solde
        FROM avoir_mouvement WHERE patient_id = NEW.patient_id;
        IF v_solde < NEW.montant THEN
            RAISE EXCEPTION 'Avoir insuffisant (solde : % DA)', v_solde USING ERRCODE = 'P0001';
        END IF;
    END IF;
    RETURN NEW;
END $$;


--
-- Name: avoir_mouvement_immuable(); Type: FUNCTION; Schema: public; Owner: -
--

CREATE FUNCTION public.avoir_mouvement_immuable() RETURNS trigger
    LANGUAGE plpgsql
    AS $$
BEGIN
    RAISE EXCEPTION 'Un mouvement d''avoir ne se modifie pas et ne se supprime pas.';
END $$;


--
-- Name: bascule_decision_proteger(); Type: FUNCTION; Schema: public; Owner: -
--

CREATE FUNCTION public.bascule_decision_proteger() RETURNS trigger
    LANGUAGE plpgsql
    AS $$
BEGIN
    RAISE EXCEPTION 'bascule_decision : ajout seul';
END $$;


--
-- Name: bascule_noter_saut(bigint, text, text, bigint, bigint); Type: FUNCTION; Schema: public; Owner: -
--

CREATE FUNCTION public.bascule_noter_saut(p_promotion bigint, p_compteur text, p_detail text, p_ancienne bigint, p_nouvelle bigint) RETURNS void
    LANGUAGE plpgsql
    AS $$
BEGIN
    INSERT INTO bascule_saut (promotion_id, compteur, detail, ancienne, nouvelle, reserve)
    VALUES (p_promotion, p_compteur, p_detail, p_ancienne, p_nouvelle, p_nouvelle - p_ancienne);
    INSERT INTO journal_audit (utilisateur_id, action, table_cible, details)
    VALUES (NULL, 'saut_compteur_bascule', 'bascule_saut',
            jsonb_build_object('motif', 'bascule', 'compteur', p_compteur, 'detail', p_detail,
                               'ancienne_valeur', p_ancienne, 'nouvelle_valeur', p_nouvelle, 'reserve', p_nouvelle - p_ancienne));
END $$;


--
-- Name: bascule_saut_promotion(integer); Type: FUNCTION; Schema: public; Owner: -
--

CREATE FUNCTION public.bascule_saut_promotion(p_timeline integer DEFAULT NULL::integer) RETURNS TABLE(timeline_notee integer, saut_fait boolean, compteurs_sautes integer)
    LANGUAGE plpgsql
    AS $$
#variable_conflict use_column
DECLARE
    c_reserve_tickets   constant bigint := 100000;
    c_reserve_factures  constant bigint := 1000;
    c_reserve_avoirs    constant bigint := 1000;
    c_reserve_commandes constant bigint := 1000;
    v_tl        int;
    v_max       int;
    v_promotion bigint;
    v_n         int := 0;
    v_compteur  compteur_ticket;
    v_ancienne  bigint;
    v_nouvelle  bigint;
    v_annee     int := extract(year FROM (now() AT TIME ZONE 'Africa/Algiers'))::int;
    r           record;
BEGIN
    IF pg_is_in_recovery() THEN
        RAISE EXCEPTION 'Saut des compteurs refusé : la base est en lecture seule (2e serveur pas encore promu)' USING ERRCODE = 'read_only_sql_transaction';
    END IF;
    v_tl := COALESCE(p_timeline, bascule_timeline_courante());
    IF v_tl IS NULL OR v_tl < 1 THEN
        RAISE EXCEPTION 'Saut des compteurs refusé : timeline invalide (%)', v_tl USING ERRCODE = 'check_violation';
    END IF;
    -- Deux démarrages en même temps : le second attend la fin du premier, puis ne refait rien
    PERFORM pg_advisory_xact_lock(hashtextextended('bascule_saut_promotion', 0));
    SELECT max(b.timeline) INTO v_max FROM bascule_promotion b;

    IF v_max IS NULL THEN
        INSERT INTO bascule_promotion (timeline, saut, motif)
        VALUES (v_tl, false, 'Première note : timeline courante gardée sans saut (mise en service du suivi des promotions)');
        timeline_notee := v_tl; saut_fait := false; compteurs_sautes := 0;
        RETURN NEXT;
        RETURN;
    END IF;
    IF v_tl <= v_max THEN
        timeline_notee := v_tl; saut_fait := false; compteurs_sautes := 0;
        RETURN NEXT;
        RETURN;
    END IF;

    INSERT INTO bascule_promotion (timeline, saut, motif)
    VALUES (v_tl, true, format('Promotion : timeline %s après la timeline %s ; compteurs sautés (tickets +%s, factures +%s, avoirs +%s, commandes +%s)',
                               v_tl, v_max, c_reserve_tickets, c_reserve_factures, c_reserve_avoirs, c_reserve_commandes))
    RETURNING id INTO v_promotion;

    -- Tickets « T- » : plage sautée notée (motif « bascule »), compteur avancé sous la garde du script 151, journal fiscal
    SELECT * INTO v_compteur FROM compteur_ticket WHERE id = 1 FOR UPDATE;
    IF FOUND THEN
        v_ancienne := v_compteur.dernier_numero;
        v_nouvelle := v_ancienne + c_reserve_tickets;
        INSERT INTO compteur_ticket_saut (du, au, motif) VALUES (v_ancienne + 1, v_nouvelle, 'bascule');
        PERFORM set_config('pharma.saut_ticket', v_nouvelle::text, true);
        UPDATE compteur_ticket SET dernier_numero = v_nouvelle WHERE id = 1;
        PERFORM set_config('pharma.saut_ticket', '', true);
        PERFORM bascule_noter_saut(v_promotion, 'ticket', v_compteur.prefixe, v_ancienne, v_nouvelle);
        INSERT INTO journal_vente (evenement, survenu_le, motif)
        VALUES ('saut_compteur', now(),
                format('Bascule du 2e serveur (timeline %s) : compteur des tickets sauté de %s%s à %s%s (réserve de %s n°)',
                       v_tl, v_compteur.prefixe, lpad(v_ancienne::text, GREATEST(6, char_length(v_ancienne::text)), '0'),
                       v_compteur.prefixe, lpad(v_nouvelle::text, GREATEST(6, char_length(v_nouvelle::text)), '0'), c_reserve_tickets));
        v_n := v_n + 1;
    END IF;

    -- Factures et avoirs : l'année en cours (créée si absente) et toute année déjà ouverte
    INSERT INTO facture_compteur (type, annee, dernier) VALUES ('facture', v_annee, 0), ('avoir', v_annee, 0) ON CONFLICT (type, annee) DO NOTHING;
    FOR r IN SELECT f.type, f.annee, f.dernier FROM facture_compteur f ORDER BY f.type, f.annee FOR UPDATE LOOP
        v_nouvelle := r.dernier + CASE WHEN r.type = 'avoir' THEN c_reserve_avoirs ELSE c_reserve_factures END;
        UPDATE facture_compteur SET dernier = v_nouvelle WHERE type = r.type AND annee = r.annee;
        PERFORM bascule_noter_saut(v_promotion, r.type, r.annee::text, r.dernier, v_nouvelle);
        v_n := v_n + 1;
    END LOOP;

    -- Bons de commande « C- » (séquence) : en dernier, un setval ne s'annule pas avec la transaction
    IF to_regclass('public.seq_numero_commande') IS NOT NULL THEN
        SELECT CASE WHEN s.is_called THEN s.last_value ELSE s.last_value - 1 END INTO v_ancienne FROM seq_numero_commande s;
        v_nouvelle := v_ancienne + c_reserve_commandes;
        PERFORM bascule_noter_saut(v_promotion, 'commande', 'C-', v_ancienne, v_nouvelle);
        PERFORM setval('seq_numero_commande', v_nouvelle, true);
        v_n := v_n + 1;
    END IF;

    timeline_notee := v_tl; saut_fait := true; compteurs_sautes := v_n;
    RETURN NEXT;
END $$;


--
-- Name: FUNCTION bascule_saut_promotion(p_timeline integer); Type: COMMENT; Schema: public; Owner: -
--

COMMENT ON FUNCTION public.bascule_saut_promotion(p_timeline integer) IS 'Saut des compteurs à la promotion du 2e serveur, une seule fois par timeline, avant toute écriture (script 164, décision Q13).';


--
-- Name: bascule_timeline_courante(); Type: FUNCTION; Schema: public; Owner: -
--

CREATE FUNCTION public.bascule_timeline_courante() RETURNS integer
    LANGUAGE sql
    AS $$
    SELECT ('x' || substr(pg_walfile_name(pg_current_wal_lsn()), 1, 8))::bit(32)::int
$$;


--
-- Name: cloture_fiscale_immuable(); Type: FUNCTION; Schema: public; Owner: -
--

CREATE FUNCTION public.cloture_fiscale_immuable() RETURNS trigger
    LANGUAGE plpgsql
    AS $$
BEGIN
    RAISE EXCEPTION 'Une clôture fiscale ne peut être ni modifiée, ni supprimée, ni vidée';
END;
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
-- Name: compteur_ticket_proteger(); Type: FUNCTION; Schema: public; Owner: -
--

CREATE FUNCTION public.compteur_ticket_proteger() RETURNS trigger
    LANGUAGE plpgsql
    AS $$
BEGIN
    IF TG_OP = 'UPDATE' AND NEW.dernier_numero = OLD.dernier_numero + 1 AND NEW.prefixe = OLD.prefixe
       AND NEW.premier_numero_continu = OLD.premier_numero_continu AND NEW.mis_en_service_le = OLD.mis_en_service_le THEN
        RETURN NEW;
    END IF;
    IF TG_OP = 'UPDATE' AND NEW.dernier_numero > OLD.dernier_numero + 1
       AND coalesce(current_setting('pharma.saut_ticket', true), '') = NEW.dernier_numero::text
       AND (to_jsonb(NEW) - 'dernier_numero') = (to_jsonb(OLD) - 'dernier_numero')
       AND EXISTS (SELECT 1 FROM compteur_ticket_saut s WHERE s.du = OLD.dernier_numero + 1 AND s.au = NEW.dernier_numero) THEN
        RETURN NEW;
    END IF;
    RAISE EXCEPTION 'Le compteur des tickets avance d''une unité à la fois ; il ne se modifie pas et ne se supprime pas';
END;
$$;


--
-- Name: conservation_fiscale_proteger(); Type: FUNCTION; Schema: public; Owner: -
--

CREATE FUNCTION public.conservation_fiscale_proteger() RETURNS trigger
    LANGUAGE plpgsql
    AS $$
DECLARE
    v_annees int := COALESCE((SELECT conservation_annees FROM parametre_fiscal WHERE id = 1), 6);
    v_date   timestamptz;
BEGIN
    v_date := CASE TG_TABLE_NAME
        WHEN 'vente' THEN OLD.date_vente
        ELSE (to_jsonb(OLD) ->> 'cree_le')::timestamptz
    END;
    IF v_date IS NULL OR v_date > now() - make_interval(years => v_annees) THEN
        RAISE EXCEPTION 'Conservation fiscale : % de moins de % ans, suppression interdite', TG_TABLE_NAME, v_annees;
    END IF;
    RETURN OLD;
END;
$$;


--
-- Name: contenance_depuis_conditionnement(text); Type: FUNCTION; Schema: public; Owner: -
--

CREATE FUNCTION public.contenance_depuis_conditionnement(p_conditionnement text) RETURNS integer
    LANGUAGE sql IMMUTABLE
    AS $_$
    -- motif de ContenanceProduit.Boite(), recopié tel quel (test ContenanceDepuisNomenclatureTests)
    SELECT substring(p_conditionnement FROM '(?i)^\s*(?:B|BTE|BO[IÎiî]TE)\s*(?:/|\s+DE\s+)\s*([0-9]{1,5})(?:\s*(?:SACHETS?(?:(?:\s+|-)DOSES?)?|SACH|SCH|AMPOULES?|AMPS?|AMP\.?\s+BUV|CPR?S?|CP\.?\s+EFF|COMPRIM[EÉeé]S?(?:\s+EFFERVESCENTS?)?|G[EÉeé]LULES?|SUPPOSITOIRES?|SUPPO|OVULES?|CAPSULES?|PATCH(?:E?S)?|DISPOSITIFS?\s+TRANSDERMIQUES?|UNIDOSES?|R[EÉeé]CIPIENTS\s+UNIDOSES|FLACONS?|FLC|FL|(?:SERINGUES?|SERING\.?|SERIN|SER)(?:\s*PR[EÉeé][\s-]*REMPL[A-Za-zÉéÈè]*\.?)?|STYLOS?(?:\s*PR[EÉeé][\s-]*REMPL[A-Za-zÉéÈè]*\.?)?|CART(?:OUCHES?)?|POCHES?|TUBES?)\.?(?:\s*(?:DE\s*|/\s*)?[0-9]+(?:[.,][0-9]+)?\s*(?:ML|MG|G|L)|\s*\(\s*[0-9]+(?:[.,][0-9]+)?\s*(?:ML|MG|G|L)\s*\))?)?\s*$')::int
$_$;


--
-- Name: credit_contexte(text, bigint, bigint, text, text); Type: FUNCTION; Schema: public; Owner: -
--

CREATE FUNCTION public.credit_contexte(p_type text, p_vente_id bigint, p_utilisateur_id bigint, p_poste text, p_reference text) RETURNS void
    LANGUAGE sql
    AS $$
    SELECT set_config('pharma.credit_type', COALESCE(p_type, ''), true),
           set_config('pharma.credit_vente', COALESCE(p_vente_id::text, ''), true),
           set_config('pharma.credit_utilisateur', COALESCE(p_utilisateur_id::text, ''), true),
           set_config('pharma.credit_poste', COALESCE(p_poste, ''), true),
           set_config('pharma.credit_reference', COALESCE(p_reference, ''), true);
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
-- Name: dette_fournisseur_contexte(text, bigint, text); Type: FUNCTION; Schema: public; Owner: -
--

CREATE FUNCTION public.dette_fournisseur_contexte(p_type text, p_utilisateur_id bigint, p_reference text) RETURNS void
    LANGUAGE sql
    AS $$
    SELECT set_config('pharma.dette_type', COALESCE(p_type, ''), true),
           set_config('pharma.dette_utilisateur', COALESCE(p_utilisateur_id::text, ''), true),
           set_config('pharma.dette_reference', COALESCE(p_reference, ''), true);
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
-- Name: encaissement_attente_secondes(); Type: FUNCTION; Schema: public; Owner: -
--

CREATE FUNCTION public.encaissement_attente_secondes() RETURNS integer
    LANGUAGE plpgsql STABLE
    AS $$
DECLARE
    p        sync_parametre;
    v_delai  interval;
    v_fin    timestamptz;
BEGIN
    SELECT * INTO p FROM sync_parametre WHERE id = 1;
    IF NOT FOUND OR NOT p.active OR p.controle_demarrage_delai_s = 0 THEN
        RETURN 0;
    END IF;
    v_delai := make_interval(secs => p.controle_demarrage_delai_s);
    IF p.demarrage_le IS NOT NULL AND p.controle_demarrage_le IS NULL THEN
        v_fin := p.demarrage_le + v_delai;
    END IF;
    -- Retour en arrière détecté, avance des n° (compteur des tickets compris, script 151) pas encore faite
    IF p.retour_arriere_le IS NOT NULL AND p.retour_arriere_sequences_le IS NULL THEN
        v_fin := greatest(v_fin, p.retour_arriere_le + v_delai);
    END IF;
    IF v_fin IS NULL OR v_fin <= now() THEN
        RETURN 0;
    END IF;
    RETURN greatest(1, ceil(extract(epoch FROM v_fin - now()))::int);
END $$;


--
-- Name: FUNCTION encaissement_attente_secondes(); Type: COMMENT; Schema: public; Owner: -
--

COMMENT ON FUNCTION public.encaissement_attente_secondes() IS 'Secondes avant que l''encaissement soit permis (0 : permis) : contrôle de démarrage du serveur central attendu, au plus le délai réglé (script 152).';


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

    -- Péremption : une alerte seulement s'il y a quelque chose à faire (lot périmé, boîtes qui ne seront pas vendues à temps) ;
    -- le message dit pourquoi et quoi faire. Lot dont toutes les boîtes seront vendues à temps : aucune alerte
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
           CASE
               WHEN r.niveau = 'perime' THEN format('%s, lot %s : PÉRIMÉ depuis le %s. %s boîte(s) à retirer du rayon.',
                                  r.nom_commercial, r.numero_lot,
                                  to_char(r.date_peremption, 'DD/MM/YYYY'), r.quantite_disponible)
               ELSE format('%s, lot %s : expire le %s (dans %s j). Au rythme des ventes, %s boîte(s) sur %s ne seront pas vendues à temps (%s DA au prix d''achat). À faire : les mettre en avant, les retourner au grossiste ou les proposer à la place d''un équivalent.',
                           r.nom_commercial, r.numero_lot,
                           to_char(r.date_peremption, 'DD/MM/YYYY'), r.jours_restants,
                           r.qte_a_risque, r.quantite_disponible, to_char(r.valeur_a_risque, 'FM999G999G990D00'))
           END AS message,
           r.valeur_a_risque AS valeur_dzd
    FROM v_risque_peremption r
    WHERE r.niveau = 'perime' OR (r.niveau <> 'ok' AND r.qte_a_risque > 0)

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
    WHERE e.statut <> 'ok'

    UNION ALL

    -- Ancien lot délaissé au profit d'un lot plus récent (alerte réservée au titulaire)
    SELECT * FROM lots_delaisses(NULL);

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
-- Name: historique_chifa_lecture_seule(); Type: FUNCTION; Schema: public; Owner: -
--

CREATE FUNCTION public.historique_chifa_lecture_seule() RETURNS trigger
    LANGUAGE plpgsql
    AS $$
BEGIN
    RAISE EXCEPTION 'Historique Chifa repris : lecture seule (ni modification ni suppression)';
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
        WHEN 'manquant_reception'    THEN 'ecart_moins'
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
-- Name: integrite_activer(text, text, text[], text); Type: FUNCTION; Schema: public; Owner: -
--

CREATE FUNCTION public.integrite_activer(p_journal text, p_libelle text, p_colonnes text[], p_algorithme text DEFAULT NULL::text) RETURNS void
    LANGUAGE plpgsql
    AS $$
DECLARE
    v_table   regclass := to_regclass(quote_ident(p_journal));
    v_existe  chaine_integrite;
    v_algo    text := COALESCE(p_algorithme, (SELECT algorithme_defaut FROM parametre_integrite WHERE id = 1), 'sha256');
    v_lignes  boolean;
    v_flottant text;
BEGIN
    IF v_table IS NULL THEN
        RAISE EXCEPTION 'Table % introuvable', p_journal;
    END IF;
    SELECT * INTO v_existe FROM chaine_integrite WHERE journal = p_journal;
    IF FOUND THEN
        IF v_existe.colonnes <> p_colonnes OR v_existe.algorithme <> v_algo THEN
            RAISE EXCEPTION 'Journal % déjà chaîné avec d''autres colonnes ou un autre algorithme', p_journal;
        END IF;
    ELSE
        EXECUTE format('SELECT EXISTS (SELECT 1 FROM %I)', p_journal) INTO v_lignes;
        IF v_lignes THEN
            RAISE EXCEPTION 'Table % : elle a déjà des lignes, elle ne peut pas être chaînée après coup', p_journal;
        END IF;
        SELECT string_agg(a.attname, ', ') INTO v_flottant
        FROM pg_attribute a JOIN pg_type t ON t.oid = a.atttypid
        WHERE a.attrelid = v_table AND a.attname = ANY (p_colonnes) AND t.typname IN ('float4', 'float8');
        IF v_flottant IS NOT NULL THEN
            RAISE EXCEPTION 'Table % : colonnes en réel flottant refusées (représentation instable) : %', p_journal, v_flottant;
        END IF;
    END IF;

    EXECUTE format('ALTER TABLE %I ADD COLUMN IF NOT EXISTS cle_chaine text NOT NULL DEFAULT %L', p_journal, 'officine');
    EXECUTE format('ALTER TABLE %I ADD COLUMN IF NOT EXISTS rang bigint', p_journal);
    EXECUTE format('ALTER TABLE %I ADD COLUMN IF NOT EXISTS hash bytea', p_journal);
    EXECUTE format('ALTER TABLE %I ADD COLUMN IF NOT EXISTS hash_precedent bytea', p_journal);
    EXECUTE format('CREATE UNIQUE INDEX IF NOT EXISTS %I ON %I (cle_chaine, rang)', 'ux_' || p_journal || '_chaine', p_journal);

    IF v_existe.journal IS NULL THEN
        INSERT INTO chaine_integrite (journal, libelle, colonnes, algorithme) VALUES (p_journal, p_libelle, p_colonnes, v_algo);
        -- Vérifie tout de suite que les colonnes déclarées existent
        PERFORM integrite_expression(p_journal);
    END IF;

    EXECUTE format('CREATE OR REPLACE TRIGGER zz_integrite_chainer BEFORE INSERT ON %I FOR EACH ROW EXECUTE FUNCTION integrite_chainer()', p_journal);
    EXECUTE format('CREATE OR REPLACE TRIGGER trg_integrite_immuable BEFORE UPDATE OR DELETE ON %I FOR EACH ROW EXECUTE FUNCTION integrite_interdire_modification()', p_journal);
    EXECUTE format('CREATE OR REPLACE TRIGGER trg_integrite_non_vidable BEFORE TRUNCATE ON %I FOR EACH STATEMENT EXECUTE FUNCTION integrite_interdire_modification()', p_journal);
END;
$$;


--
-- Name: integrite_ajout_seul_restauration(); Type: FUNCTION; Schema: public; Owner: -
--

CREATE FUNCTION public.integrite_ajout_seul_restauration() RETURNS trigger
    LANGUAGE plpgsql
    AS $$
BEGIN
    RAISE EXCEPTION '% : ajout seul (ni modification ni suppression)', TG_TABLE_NAME;
END $$;


--
-- Name: integrite_chainer(); Type: FUNCTION; Schema: public; Owner: -
--

CREATE FUNCTION public.integrite_chainer() RETURNS trigger
    LANGUAGE plpgsql
    AS $_$
DECLARE
    v_ci        chaine_integrite;
    v_rang      bigint;
    v_prec      bytea;
    v_canonique text;
BEGIN
    SELECT * INTO v_ci FROM chaine_integrite WHERE journal = TG_TABLE_NAME;
    IF NOT FOUND THEN
        RAISE EXCEPTION 'Table % chaînée mais absente de chaine_integrite', TG_TABLE_NAME;
    END IF;
    -- En lecture répétable, la ligne écrite par une autre caisse juste avant ne serait pas vue : la chaîne se casserait
    IF current_setting('transaction_isolation') <> 'read committed' THEN
        RAISE EXCEPTION 'Journal % : écriture seulement en isolation « read committed »', TG_TABLE_NAME;
    END IF;
    NEW.cle_chaine := COALESCE(NEW.cle_chaine, 'officine');
    -- Écriture sérialisée par journal et clé de chaîne (plusieurs caisses), verrou rendu à la fin de la transaction
    PERFORM pg_advisory_xact_lock(hashtextextended('integrite|' || TG_TABLE_NAME || '|' || NEW.cle_chaine, 0));
    EXECUTE format('SELECT rang, hash FROM %I WHERE cle_chaine = $1 ORDER BY rang DESC LIMIT 1', TG_TABLE_NAME)
        INTO v_rang, v_prec USING NEW.cle_chaine;
    NEW.rang := COALESCE(v_rang, 0) + 1;
    NEW.hash_precedent := COALESCE(v_prec, integrite_origine(v_ci.algorithme));
    EXECUTE format('SELECT %s FROM (SELECT ($1).*) r', integrite_expression(TG_TABLE_NAME)) INTO v_canonique USING NEW;
    NEW.hash := integrite_hacher(v_ci.algorithme, v_canonique, NEW.hash_precedent);
    RETURN NEW;
END;
$_$;


--
-- Name: integrite_champ(text, text); Type: FUNCTION; Schema: public; Owner: -
--

CREATE FUNCTION public.integrite_champ(p_nom text, p_valeur text) RETURNS text
    LANGUAGE sql IMMUTABLE PARALLEL SAFE
    AS $$
    SELECT p_nom || '=' || CASE WHEN p_valeur IS NULL THEN '~' ELSE char_length(p_valeur)::text || ':' || p_valeur END
$$;


--
-- Name: integrite_expression(text); Type: FUNCTION; Schema: public; Owner: -
--

CREATE FUNCTION public.integrite_expression(p_journal text) RETURNS text
    LANGUAGE plpgsql STABLE
    AS $$
DECLARE
    v_ci     chaine_integrite;
    v_col    record;
    v_val    text;
    v_parts  text[];
    v_trouve int := 0;
BEGIN
    SELECT * INTO v_ci FROM chaine_integrite WHERE journal = p_journal;
    IF NOT FOUND THEN
        RAISE EXCEPTION 'Journal % non inscrit dans chaine_integrite', p_journal;
    END IF;
    v_parts := ARRAY[
        quote_literal('v' || v_ci.version_format || '|' || v_ci.journal),
        'integrite_champ(''cle_chaine'', r.cle_chaine)',
        'integrite_champ(''rang'', r.rang::text)'];
    FOR v_col IN
        SELECT u.nom, t.typname
        FROM unnest(v_ci.colonnes) WITH ORDINALITY AS u(nom, ord)
        JOIN pg_attribute a ON a.attrelid = to_regclass(quote_ident(v_ci.journal)) AND a.attname = u.nom AND a.attnum > 0 AND NOT a.attisdropped
        JOIN pg_type t ON t.oid = a.atttypid
        ORDER BY u.ord
    LOOP
        v_trouve := v_trouve + 1;
        v_val := CASE v_col.typname
            WHEN 'timestamptz' THEN format('to_char(r.%I AT TIME ZONE ''UTC'', ''YYYY-MM-DD"T"HH24:MI:SS.US"Z"'')', v_col.nom)
            WHEN 'timestamp'   THEN format('to_char(r.%I, ''YYYY-MM-DD"T"HH24:MI:SS.US'')', v_col.nom)
            WHEN 'date'        THEN format('to_char(r.%I, ''YYYY-MM-DD'')', v_col.nom)
            WHEN 'bool'        THEN format('CASE WHEN r.%I IS NULL THEN NULL WHEN r.%I THEN ''true'' ELSE ''false'' END', v_col.nom, v_col.nom)
            WHEN 'bytea'       THEN format('encode(r.%I, ''hex'')', v_col.nom)
            ELSE format('r.%I::text', v_col.nom)
        END;
        v_parts := v_parts || format('integrite_champ(%L, %s)', v_col.nom, v_val);
    END LOOP;
    IF v_trouve <> cardinality(v_ci.colonnes) THEN
        RAISE EXCEPTION 'Journal % : colonne déclarée absente de la table', p_journal;
    END IF;
    RETURN 'concat_ws(''|'', ' || array_to_string(v_parts, ', ') || ')';
END;
$$;


--
-- Name: integrite_hacher(text, text, bytea); Type: FUNCTION; Schema: public; Owner: -
--

CREATE FUNCTION public.integrite_hacher(p_algorithme text, p_canonique text, p_precedent bytea) RETURNS bytea
    LANGUAGE sql IMMUTABLE PARALLEL SAFE
    AS $$
    SELECT CASE p_algorithme
               WHEN 'sha512' THEN sha512(convert_to(p_canonique, 'UTF8') || p_precedent)
               ELSE sha256(convert_to(p_canonique, 'UTF8') || p_precedent)
           END
$$;


--
-- Name: integrite_interdire_modification(); Type: FUNCTION; Schema: public; Owner: -
--

CREATE FUNCTION public.integrite_interdire_modification() RETURNS trigger
    LANGUAGE plpgsql
    AS $$
BEGIN
    RAISE EXCEPTION 'Journal % chaîné : il ne peut être ni modifié, ni supprimé, ni vidé', TG_TABLE_NAME;
END;
$$;


--
-- Name: integrite_interdire_modification_trace(); Type: FUNCTION; Schema: public; Owner: -
--

CREATE FUNCTION public.integrite_interdire_modification_trace() RETURNS trigger
    LANGUAGE plpgsql
    AS $$
BEGIN
    RAISE EXCEPTION 'Les sceaux et vérifications d''intégrité ne peuvent être ni modifiés, ni supprimés, ni vidés';
END;
$$;


--
-- Name: integrite_origine(text); Type: FUNCTION; Schema: public; Owner: -
--

CREATE FUNCTION public.integrite_origine(p_algorithme text) RETURNS bytea
    LANGUAGE sql IMMUTABLE PARALLEL SAFE
    AS $$
    SELECT decode(repeat('00', CASE p_algorithme WHEN 'sha512' THEN 64 ELSE 32 END), 'hex')
$$;


--
-- Name: integrite_registre_immuable(); Type: FUNCTION; Schema: public; Owner: -
--

CREATE FUNCTION public.integrite_registre_immuable() RETURNS trigger
    LANGUAGE plpgsql
    AS $$
BEGIN
    IF TG_OP = 'UPDATE' AND NEW.journal = OLD.journal AND NEW.colonnes = OLD.colonnes AND NEW.algorithme = OLD.algorithme
       AND NEW.version_format = OLD.version_format AND NEW.active_le = OLD.active_le THEN
        RETURN NEW;
    END IF;
    RAISE EXCEPTION 'Le registre des journaux chaînés ne peut être ni modifié (sauf le libellé) ni supprimé';
END;
$$;


--
-- Name: integrite_verifier(text, text); Type: FUNCTION; Schema: public; Owner: -
--

CREATE FUNCTION public.integrite_verifier(p_journal text, p_cle text DEFAULT NULL::text) RETURNS TABLE(cle_chaine text, lignes bigint, dernier_rang bigint, dernier_hash bytea, rang_rupture bigint, id_rupture bigint, motif text, attendu text, trouve text)
    LANGUAGE plpgsql STABLE
    AS $_$
#variable_conflict use_variable
DECLARE
    v_ci    chaine_integrite;
    v_cle   text;
    v_ligne record;
    v_prec  bytea;
    v_rang  bigint;
    v_calc  bytea;
BEGIN
    SELECT * INTO v_ci FROM chaine_integrite c WHERE c.journal = p_journal;
    IF NOT FOUND THEN
        RAISE EXCEPTION 'Journal % non inscrit dans chaine_integrite', p_journal;
    END IF;
    FOR v_cle IN EXECUTE format('SELECT DISTINCT t.cle_chaine FROM %I t WHERE $1 IS NULL OR t.cle_chaine = $1 ORDER BY 1', p_journal) USING p_cle
    LOOP
        cle_chaine := v_cle; lignes := 0; dernier_rang := 0; dernier_hash := NULL;
        rang_rupture := NULL; id_rupture := NULL; motif := NULL; attendu := NULL; trouve := NULL;
        v_prec := integrite_origine(v_ci.algorithme);
        v_rang := 0;
        FOR v_ligne IN EXECUTE format('SELECT r.id, r.rang, r.hash, r.hash_precedent, %s AS canonique FROM %I r WHERE r.cle_chaine = $1 ORDER BY r.rang',
                                      integrite_expression(p_journal), p_journal) USING v_cle
        LOOP
            lignes := lignes + 1;
            IF v_ligne.rang IS DISTINCT FROM v_rang + 1 THEN
                rang_rupture := v_rang + 1; id_rupture := v_ligne.id; motif := 'ligne manquante ou hors chaîne';
                attendu := 'rang ' || (v_rang + 1); trouve := 'rang ' || COALESCE(v_ligne.rang::text, 'vide');
                EXIT;
            END IF;
            IF v_ligne.hash_precedent IS DISTINCT FROM v_prec THEN
                rang_rupture := v_ligne.rang; id_rupture := v_ligne.id; motif := 'chaînage rompu (empreinte précédente)';
                attendu := encode(v_prec, 'hex'); trouve := COALESCE(encode(v_ligne.hash_precedent, 'hex'), 'vide');
                EXIT;
            END IF;
            v_calc := integrite_hacher(v_ci.algorithme, v_ligne.canonique, v_prec);
            IF v_ligne.hash IS DISTINCT FROM v_calc THEN
                rang_rupture := v_ligne.rang; id_rupture := v_ligne.id; motif := 'ligne modifiée (empreinte recalculée différente)';
                attendu := encode(v_calc, 'hex'); trouve := COALESCE(encode(v_ligne.hash, 'hex'), 'vide');
                EXIT;
            END IF;
            v_prec := v_ligne.hash;
            v_rang := v_ligne.rang;
            dernier_rang := v_ligne.rang;
            dernier_hash := v_ligne.hash;
        END LOOP;
        RETURN NEXT;
    END LOOP;
END;
$_$;


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
-- Name: interdire_vidage(); Type: FUNCTION; Schema: public; Owner: -
--

CREATE FUNCTION public.interdire_vidage() RETURNS trigger
    LANGUAGE plpgsql
    AS $$
BEGIN
    RAISE EXCEPTION '% ne peut pas être vidé (TRUNCATE refusé) : ses lignes sont conservées',
        CASE TG_TABLE_NAME
            WHEN 'journal_audit'         THEN 'Le journal des actions'
            WHEN 'registre_psychotrope'  THEN 'Le registre des psychotropes'
            WHEN 'journal_sauvegarde'    THEN 'Le journal des sauvegardes'
            WHEN 'boite_evenement'       THEN 'L''historique des boîtes'
            WHEN 'credit_mouvement'      THEN 'Le relevé de la dette des patients'
            WHEN 'fournisseur_mouvement' THEN 'Le relevé de la dette envers les grossistes'
            ELSE 'La table ' || TG_TABLE_NAME
        END;
END;
$$;


--
-- Name: journal_vente_continuite(); Type: FUNCTION; Schema: public; Owner: -
--

CREATE FUNCTION public.journal_vente_continuite() RETURNS TABLE(anomalie text, numero text, vente_id bigint)
    LANGUAGE sql STABLE
    AS $_$
    WITH c AS (SELECT * FROM compteur_ticket WHERE id = 1),
         v AS (SELECT x.id, x.numero, x.statut, substring(x.numero FROM '^' || (SELECT prefixe FROM c) || '([0-9]+)$')::bigint AS n
               FROM vente x)
    SELECT 'numero_manquant', c.prefixe || lpad(s.n::text, GREATEST(6, char_length(s.n::text)), '0'), NULL::bigint
    FROM c, generate_series(c.premier_numero_continu, c.dernier_numero) AS s(n)
    WHERE NOT EXISTS (SELECT 1 FROM v WHERE v.n = s.n)
      AND NOT EXISTS (SELECT 1 FROM compteur_ticket_saut z WHERE s.n BETWEEN z.du AND z.au)
    UNION ALL
    SELECT 'numero_hors_compteur', v.numero, v.id FROM v, c WHERE v.n > c.dernier_numero
    UNION ALL
    SELECT 'vente_sans_ligne', v.numero, v.id FROM v, c
    WHERE v.n >= c.premier_numero_continu AND v.statut IN ('validee', 'annulee')
      AND NOT EXISTS (SELECT 1 FROM journal_vente j WHERE j.vente_id = v.id AND j.evenement = 'vente')
    UNION ALL
    SELECT 'annulation_sans_ligne', v.numero, v.id FROM v
    WHERE v.statut = 'annulee' AND EXISTS (SELECT 1 FROM annulation_vente a, compteur_ticket c WHERE a.vente_id = v.id AND a.cree_le >= c.mis_en_service_le)
      AND NOT EXISTS (SELECT 1 FROM journal_vente j WHERE j.vente_id = v.id AND j.evenement = 'annulation')
    ORDER BY 1, 2
$_$;


--
-- Name: journal_vente_depuis_avoir(); Type: FUNCTION; Schema: public; Owner: -
--

CREATE FUNCTION public.journal_vente_depuis_avoir() RETURNS trigger
    LANGUAGE plpgsql
    AS $$
DECLARE
    v_numero text;
BEGIN
    IF EXISTS (SELECT 1 FROM journal_vente WHERE avoir_mouvement_id = NEW.id) THEN RETURN NULL; END IF;
    SELECT numero INTO v_numero FROM vente WHERE id = NEW.vente_id;
    INSERT INTO journal_vente (evenement, numero_ticket, vente_id, avoir_mouvement_id, survenu_le, utilisateur_id, montant_mouvement, motif)
    VALUES ('avoir', v_numero, NEW.vente_id, NEW.id, NEW.cree_le, NEW.utilisateur_id,
            CASE WHEN NEW.sens = 'credit' THEN NEW.montant ELSE -NEW.montant END, NEW.origine);
    RETURN NULL;
END;
$$;


--
-- Name: journal_vente_depuis_regularisation(); Type: FUNCTION; Schema: public; Owner: -
--

CREATE FUNCTION public.journal_vente_depuis_regularisation() RETURNS trigger
    LANGUAGE plpgsql
    AS $$
DECLARE
    g        regularisation_part_organisme;
    v        vente;
    v_evt    text;
    v_signe  int;
    v_delta  numeric;
BEGIN
    SELECT * INTO g FROM regularisation_part_organisme WHERE id = NEW.id;
    IF NOT FOUND THEN RETURN NULL; END IF;
    v_evt := CASE WHEN TG_OP = 'INSERT' THEN 'regularisation' ELSE 'annulation_regularisation' END;
    IF v_evt = 'annulation_regularisation' AND g.statut = 'active' THEN RETURN NULL; END IF;
    IF EXISTS (SELECT 1 FROM journal_vente WHERE regularisation_id = g.id AND evenement = v_evt) THEN RETURN NULL; END IF;
    SELECT * INTO v FROM vente WHERE id = g.vente_id;
    v_signe := CASE WHEN v_evt = 'regularisation' THEN 1 ELSE -1 END;
    -- Part déjà rendue avant le rattachement : déjà sortie du journal par la ligne « retour » (script 155), jamais déplacée deux fois
    v_delta := (g.part_organisme_chifa + g.part_organisme_rendue_avant - g.part_organisme_vente) * v_signe;
    INSERT INTO journal_vente (evenement, numero_ticket, vente_id, regularisation_id, survenu_le, utilisateur_id, type_vente,
                               part_patient, part_organisme, montant_mouvement, motif)
    VALUES (v_evt, v.numero, v.id, g.id, CASE WHEN v_evt = 'regularisation' THEN g.cree_le ELSE COALESCE(g.annulee_le, now()) END,
            CASE WHEN v_evt = 'regularisation' THEN g.utilisateur_id ELSE g.annulee_par END, v.type,
            -v_delta, v_delta, COALESCE(g.montant_a_rendre, 0) * v_signe,
            left(CASE WHEN v_evt = 'regularisation' THEN g.sort ELSE g.motif_annulation END, 500));
    RETURN NULL;
END;
$$;


--
-- Name: journal_vente_depuis_retour(); Type: FUNCTION; Schema: public; Owner: -
--

CREATE FUNCTION public.journal_vente_depuis_retour() RETURNS trigger
    LANGUAGE plpgsql
    AS $$
DECLARE
    r       retour_client;
    v       vente;
    v_taux  numeric[];
    v_poids numeric[];
    v_total numeric;
    v_pat   numeric;
    v_org   numeric;
    v_vent  record;
BEGIN
    SELECT * INTO r FROM retour_client WHERE id = NEW.id;
    IF NOT FOUND OR EXISTS (SELECT 1 FROM journal_vente WHERE retour_client_id = r.id) THEN RETURN NULL; END IF;
    SELECT * INTO v FROM vente WHERE id = r.vente_id;
    SELECT array_agg(taux ORDER BY taux), array_agg(poids ORDER BY taux), COALESCE(sum(poids), 0), COALESCE(sum(pat), 0), COALESCE(sum(org), 0)
    INTO v_taux, v_poids, v_total, v_pat, v_org
    FROM (SELECT journal_vente_taux_ligne(rl.ligne_vente_id) AS taux,
                 sum(rl.montant + rl.montant_regularisation - rl.montant_avoir_retenu + rl.montant_organisme) AS poids,
                 sum(rl.montant + rl.montant_regularisation - rl.montant_avoir_retenu) AS pat, sum(rl.montant_organisme) AS org
          FROM retour_client_ligne rl WHERE rl.retour_id = r.id GROUP BY 1) g;
    SELECT * INTO v_vent FROM journal_vente_ventiler(v_taux, v_poids, -v_total);
    INSERT INTO journal_vente (evenement, numero_ticket, vente_id, retour_client_id, survenu_le, session_caisse_id, poste, utilisateur_id,
                               type_vente, montant_ttc, montant_ht, montant_tva, detail_tva, part_patient, part_organisme, paiements, motif)
    VALUES ('retour', v.numero, v.id, r.id, r.cree_le, r.session_caisse_id, r.poste, r.utilisateur_id, v.type,
            -v_total, v_vent.ht, v_vent.tva, v_vent.detail, -v_pat, -v_org,
            CASE WHEN r.mode IS NOT NULL AND r.montant_rembourse > 0 THEN r.mode || ':' || (-r.montant_rembourse)::numeric(14,2) ELSE '' END,
            left(COALESCE(r.motif_code || ' ', '') || COALESCE(r.motif, ''), 500));
    RETURN NULL;
END;
$$;


--
-- Name: journal_vente_depuis_vente(); Type: FUNCTION; Schema: public; Owner: -
--

CREATE FUNCTION public.journal_vente_depuis_vente() RETURNS trigger
    LANGUAGE plpgsql
    AS $$
DECLARE
    v         vente;
    v_poste   text;
    v_taux    numeric[];
    v_poids   numeric[];
    v_vent    record;
    v_an      annulation_vente;
    v_retour  numeric;
    v_ret_pat numeric;
    v_ret_org numeric;
BEGIN
    SELECT * INTO v FROM vente WHERE id = NEW.id;
    IF NOT FOUND THEN RETURN NULL; END IF;
    SELECT array_agg(taux ORDER BY taux), array_agg(poids ORDER BY taux) INTO v_taux, v_poids
    FROM (SELECT journal_vente_taux_ligne(l.id) AS taux, sum(l.montant_organisme + l.montant_patient) AS poids
          FROM ligne_vente l WHERE l.vente_id = v.id GROUP BY 1) g;

    -- Vente encaissée : insérée validée, ou passée de « en cours » à validée (ou annulée dans la même transaction)
    IF v.statut IN ('validee', 'annulee') AND (TG_OP = 'INSERT' OR OLD.statut = 'en_cours')
       AND NOT EXISTS (SELECT 1 FROM journal_vente WHERE vente_id = v.id AND evenement = 'vente') THEN
        SELECT poste INTO v_poste FROM session_caisse WHERE id = v.session_caisse_id;
        SELECT * INTO v_vent FROM journal_vente_ventiler(v_taux, v_poids, v.montant_total);
        INSERT INTO journal_vente (evenement, numero_ticket, vente_id, survenu_le, session_caisse_id, poste, utilisateur_id, type_vente,
                                   montant_ttc, montant_ht, montant_tva, detail_tva, part_patient, part_organisme, paiements)
        VALUES ('vente', v.numero, v.id, v.date_vente, v.session_caisse_id, v_poste, v.utilisateur_id, v.type,
                v.montant_total, v_vent.ht, v_vent.tva, v_vent.detail, v.part_patient, v.part_organisme, journal_vente_paiements(v.id, 1));
    END IF;

    -- Annulation : nouvelle ligne (le ticket garde son numéro), au montant encore acquis (TTC déjà rendu par des retours exclu, part de
    -- régularisation rendue comprise, script 158 ; retenue de la régularisation « rendu au patient » déduite, script 159)
    IF v.statut = 'annulee' AND (TG_OP = 'INSERT' OR OLD.statut IS DISTINCT FROM 'annulee')
       AND NOT EXISTS (SELECT 1 FROM journal_vente WHERE vente_id = v.id AND evenement = 'annulation') THEN
        SELECT * INTO v_an FROM annulation_vente WHERE vente_id = v.id ORDER BY id DESC LIMIT 1;
        SELECT COALESCE(sum(r.montant + r.montant_regularisation - r.montant_avoir_retenu), 0), COALESCE(sum(r.montant_organisme), 0)
        INTO v_ret_pat, v_ret_org
        FROM retour_client_ligne r JOIN ligne_vente l ON l.id = r.ligne_vente_id WHERE l.vente_id = v.id;
        v_retour := v_ret_pat + v_ret_org;
        SELECT * INTO v_vent FROM journal_vente_ventiler(v_taux, v_poids, -(v.montant_total - v_retour));
        INSERT INTO journal_vente (evenement, numero_ticket, vente_id, annulation_id, survenu_le, session_caisse_id, poste, utilisateur_id,
                                   type_vente, montant_ttc, montant_ht, montant_tva, detail_tva, part_patient, part_organisme, paiements, motif)
        VALUES ('annulation', v.numero, v.id, v_an.id, COALESCE(v_an.cree_le, now()), v_an.session_caisse_id, v_an.poste,
                v_an.utilisateur_id, v.type, -(v.montant_total - v_retour), v_vent.ht, v_vent.tva, v_vent.detail,
                -(v.part_patient - v_ret_pat), -(v.part_organisme - v_ret_org),
                CASE WHEN v_an.mode IS NOT NULL AND v_an.montant_rembourse > 0
                     THEN v_an.mode || ':' || (-v_an.montant_rembourse)::numeric(14,2) ELSE '' END,
                left(v_an.motif, 500));
    END IF;
    RETURN NULL;
END;
$$;


--
-- Name: journal_vente_paiements(bigint, integer); Type: FUNCTION; Schema: public; Owner: -
--

CREATE FUNCTION public.journal_vente_paiements(p_vente_id bigint, p_signe integer) RETURNS text
    LANGUAGE sql STABLE
    AS $$
    SELECT COALESCE(string_agg(mode || ':' || (total * p_signe)::numeric(14,2), ';' ORDER BY mode), '')
    FROM (SELECT mode, sum(montant) AS total FROM paiement WHERE vente_id = p_vente_id GROUP BY mode) x
$$;


--
-- Name: journal_vente_recap_tva(date, date); Type: FUNCTION; Schema: public; Owner: -
--

CREATE FUNCTION public.journal_vente_recap_tva(p_du date, p_au date) RETURNS TABLE(taux numeric, montant_ttc numeric, montant_ht numeric, montant_tva numeric, evenements bigint)
    LANGUAGE sql STABLE
    AS $$
    SELECT split_part(d, ':', 1)::numeric(5,2), sum(split_part(d, ':', 2)::numeric), sum(split_part(d, ':', 3)::numeric),
           sum(split_part(d, ':', 4)::numeric), count(DISTINCT j.id)
    FROM journal_vente j CROSS JOIN LATERAL string_to_table(NULLIF(j.detail_tva, ''), ';') AS d
    WHERE (j.survenu_le AT TIME ZONE (SELECT fuseau FROM parametre_fiscal WHERE id = 1))::date BETWEEN p_du AND p_au
    GROUP BY 1 ORDER BY 1
$$;


--
-- Name: journal_vente_taux_ligne(bigint); Type: FUNCTION; Schema: public; Owner: -
--

CREATE FUNCTION public.journal_vente_taux_ligne(p_ligne_id bigint) RETURNS numeric
    LANGUAGE sql STABLE
    AS $$
    SELECT COALESCE(l.taux_tva, p.taux_tva, tp.taux_tva, 0)::numeric(5,2)
    FROM ligne_vente l LEFT JOIN produit p ON p.id = l.produit_id LEFT JOIN type_prestation tp ON tp.id = l.type_prestation_id
    WHERE l.id = p_ligne_id
$$;


--
-- Name: journal_vente_trous_historique(); Type: FUNCTION; Schema: public; Owner: -
--

CREATE FUNCTION public.journal_vente_trous_historique() RETURNS TABLE(du bigint, au bigint, manquants bigint)
    LANGUAGE sql STABLE
    AS $_$
    WITH n AS (SELECT DISTINCT substring(numero FROM '^T-([0-9]+)$')::bigint AS n FROM vente WHERE numero ~ '^T-[0-9]+$'),
         s AS (SELECT n, lag(n) OVER (ORDER BY n) AS p FROM n
               WHERE n < (SELECT premier_numero_continu FROM compteur_ticket WHERE id = 1))
    SELECT COALESCE(p, 0) + 1, n - 1, n - COALESCE(p, 0) - 1 FROM s WHERE n - COALESCE(p, 0) > 1 ORDER BY 1
$_$;


--
-- Name: journal_vente_ventiler(numeric[], numeric[], numeric); Type: FUNCTION; Schema: public; Owner: -
--

CREATE FUNCTION public.journal_vente_ventiler(p_taux numeric[], p_poids numeric[], p_ttc numeric) RETURNS TABLE(detail text, ht numeric, tva numeric)
    LANGUAGE plpgsql IMMUTABLE
    AS $$
DECLARE
    v_n      int := COALESCE(cardinality(p_taux), 0);
    v_total  numeric := 0;
    v_reste  numeric;
    v_max    int := 1;
    v_ttc    numeric[] := '{}';
    v_h      numeric;
    v_parts  text[] := '{}';
BEGIN
    IF v_n = 0 THEN
        p_taux := ARRAY[0::numeric]; p_poids := ARRAY[1::numeric]; v_n := 1;
    END IF;
    FOR i IN 1..v_n LOOP
        v_total := v_total + abs(COALESCE(p_poids[i], 0));
        IF abs(COALESCE(p_poids[i], 0)) > abs(COALESCE(p_poids[v_max], 0)) THEN v_max := i; END IF;
    END LOOP;
    v_reste := p_ttc;
    FOR i IN 1..v_n LOOP
        v_ttc[i] := CASE WHEN v_total = 0 THEN 0 ELSE round(p_ttc * abs(COALESCE(p_poids[i], 0)) / v_total, 2) END;
        v_reste := v_reste - v_ttc[i];
    END LOOP;
    v_ttc[v_max] := v_ttc[v_max] + v_reste;
    ht := 0; tva := 0;
    FOR i IN 1..v_n LOOP
        v_h := round(v_ttc[i] / (1 + COALESCE(p_taux[i], 0) / 100), 2);
        ht := ht + v_h;
        tva := tva + (v_ttc[i] - v_h);
        v_parts := v_parts || format('%s:%s:%s:%s', to_char(COALESCE(p_taux[i], 0), 'FM990.00'),
                                     v_ttc[i]::numeric(14,2), v_h::numeric(14,2), (v_ttc[i] - v_h)::numeric(14,2));
    END LOOP;
    detail := array_to_string(v_parts, ';');
    RETURN NEXT;
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
      AND NOT bloque
    ORDER BY date_peremption, id
    LIMIT 1;
$$;


--
-- Name: lots_delaisses(bigint); Type: FUNCTION; Schema: public; Owner: -
--

CREATE FUNCTION public.lots_delaisses(p_produit_id bigint DEFAULT NULL::bigint) RETURNS TABLE(o_type text, o_niveau text, o_produit_id bigint, o_lot_id bigint, o_message text, o_valeur_dzd numeric)
    LANGUAGE sql STABLE
    AS $$
    WITH p AS (SELECT seuil_lot_delaisse AS seuil, seuil_lot_delaisse_collectif AS collectif, jours_rouge FROM parametre_alerte WHERE id = 1),
    lots AS (
        SELECT l.id, l.produit_id, l.numero_lot, l.date_peremption, l.quantite_disponible, l.prix_achat_ht, l.bloque, r.date_reception
        FROM lot l JOIN reception r ON r.id = l.reception_id
        WHERE p_produit_id IS NULL OR l.produit_id = p_produit_id
    ),
    -- Ancien lot A, encore en stock et vendable ; dernière vente validée d'une de ses boîtes
    delaisses AS (
        SELECT a.*,
               (SELECT max(t.traitee_le) FROM alerte t
                WHERE t.type = 'lot_delaisse' AND t.lot_id = a.id AND t.statut = 'traitee') AS traitee_le,
               (SELECT max(va.date_vente) FROM ligne_vente la JOIN vente va ON va.id = la.vente_id
                WHERE la.lot_id = a.id AND va.statut = 'validee') AS derniere_vente
        FROM lots a
        WHERE a.quantite_disponible > 0 AND a.date_peremption > current_date AND NOT a.bloque
    ),
    -- Lot B plus récent (reçu après, ou périmant après) qui ne périme pas avant A ; compté depuis l'arrivée
    -- des deux lots, la dernière alerte traitée par le titulaire ou la dernière vente d'une boîte de A
    paires AS (
        SELECT a.id AS a_id, b.id AS b_id,
               greatest(a.date_reception, b.date_reception, a.traitee_le, a.derniere_vente) AS depuis,
               a.derniere_vente
        FROM delaisses a
        JOIN lots b ON b.produit_id = a.produit_id AND b.id <> a.id
        WHERE (b.date_reception > a.date_reception OR b.date_peremption > a.date_peremption)
          AND b.date_peremption >= a.date_peremption
    ),
    ventes AS (
        SELECT pa.a_id, v.utilisateur_id, sum(lv.quantite)::int AS boites, bool_or(lv.raison_lot = 'introuvable') AS introuvable
        FROM paires pa
        JOIN ligne_vente lv ON lv.lot_id = pa.b_id
        JOIN vente v ON v.id = lv.vente_id AND v.statut = 'validee' AND v.date_vente >= pa.depuis
        WHERE lv.raison_lot IS DISTINCT FROM 'peremption'
          -- Strictement après la dernière vente de A (une vente qui contient aussi une boîte de A ne compte pas)
          AND (pa.derniere_vente IS NULL OR v.date_vente > pa.derniere_vente)
        GROUP BY pa.a_id, v.utilisateur_id
    ),
    -- Par ancien lot : seuil d'un vendeur atteint, ou seuil collectif atteint par plusieurs vendeurs
    par_lot AS (
        SELECT ve.a_id, max(ve.boites) AS max_vendeur, sum(ve.boites) AS total, count(*) AS vendeurs, bool_or(ve.introuvable) AS introuvable
        FROM ventes ve GROUP BY ve.a_id
    ),
    retenus AS (
        SELECT pl.*, (pl.vendeurs >= 2 AND pl.total >= p.collectif) AS collectif
        FROM par_lot pl, p
        WHERE pl.max_vendeur >= p.seuil OR (pl.vendeurs >= 2 AND pl.total >= p.collectif)
    )
    SELECT 'lot_delaisse'::text,
           CASE WHEN rt.collectif OR rt.introuvable OR a.date_peremption <= current_date + (SELECT jours_rouge FROM p) THEN 'urgent' ELSE 'attention' END,
           a.produit_id,
           a.id,
           format('%s, lot %s : %s boîte(s) en stock, délaissées : un lot plus récent est vendu à leur place. %s%s. Risque de péremption ou de perte des boîtes du lot %s : faites l''inventaire de ce produit.',
                  pr.nom_commercial, a.numero_lot, a.quantite_disponible,
                  CASE WHEN rt.collectif THEN format('%s vendeurs ont vendu ensemble %s boîte(s) d''un lot plus récent : ', rt.vendeurs, rt.total) ELSE '' END,
                  (SELECT string_agg(format('%s a vendu %s boîte(s) d''un lot plus récent%s', u.nom_complet, ve.boites,
                                            CASE WHEN ve.introuvable THEN ' (dont « boîte de l''ancien lot introuvable »)' ELSE '' END),
                                     ' ; ' ORDER BY ve.boites DESC)
                   FROM ventes ve JOIN utilisateur u ON u.id = ve.utilisateur_id WHERE ve.a_id = a.id),
                  a.numero_lot),
           round(a.quantite_disponible * a.prix_achat_ht, 2)
    FROM retenus rt
    JOIN delaisses a ON a.id = rt.a_id
    JOIN produit pr ON pr.id = a.produit_id;
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
-- Name: mettre_en_file_serialisation(); Type: FUNCTION; Schema: public; Owner: -
--

CREATE FUNCTION public.mettre_en_file_serialisation() RETURNS trigger
    LANGUAGE plpgsql
    AS $$
DECLARE
    v_operation text;
BEGIN
    IF NEW.gtin IS NULL OR NEW.numero_serie IS NULL THEN
        RETURN NULL;   -- étiquette interne : pas de sérialisation nationale
    END IF;
    IF NOT COALESCE((SELECT actif FROM parametre_serialisation WHERE id = 1), false) THEN
        RETURN NULL;
    END IF;
    IF TG_OP = 'INSERT' THEN
        v_operation := 'verifier';
    ELSIF OLD.statut = 'en_stock' AND NEW.statut = 'vendue' THEN
        v_operation := 'decommissionner';
    ELSIF OLD.statut = 'vendue' AND NEW.statut = 'en_stock' THEN
        v_operation := 'reactiver';
    ELSE
        RETURN NULL;
    END IF;
    INSERT INTO verification_serialisation (boite_id, operation) VALUES (NEW.id, v_operation);
    RETURN NULL;
END;
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
-- Name: noter_credit_ouverture(); Type: FUNCTION; Schema: public; Owner: -
--

CREATE FUNCTION public.noter_credit_ouverture() RETURNS trigger
    LANGUAGE plpgsql
    AS $$
BEGIN
    IF NEW.type = 'reprise' AND NEW.montant > 0 THEN
        INSERT INTO credit_ouverture (patient_id, montant) VALUES (NEW.patient_id, NEW.montant)
        ON CONFLICT (patient_id) DO UPDATE SET montant = credit_ouverture.montant + EXCLUDED.montant;
    END IF;
    RETURN NEW;
END $$;


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
-- Name: officine_courante(); Type: FUNCTION; Schema: public; Owner: -
--

CREATE FUNCTION public.officine_courante() RETURNS uuid
    LANGUAGE plpgsql
    AS $$
DECLARE v uuid;
BEGIN
    SELECT officine_id INTO v FROM sync_parametre WHERE id = 1;
    IF v IS NOT NULL THEN RETURN v; END IF;
    INSERT INTO sync_parametre (id, officine_id) VALUES (1, gen_random_uuid())
    ON CONFLICT (id) DO UPDATE SET officine_id = coalesce(sync_parametre.officine_id, EXCLUDED.officine_id);
    SELECT officine_id INTO v FROM sync_parametre WHERE id = 1;
    RETURN v;
END $$;


--
-- Name: FUNCTION officine_courante(); Type: COMMENT; Schema: public; Owner: -
--

COMMENT ON FUNCTION public.officine_courante() IS 'Code de l''officine installée (sync_parametre.officine_id), défaut de la colonne officine_id des tables synchronisées (script 128).';


--
-- Name: parapharmacie_certaine(text, bigint, text, boolean, numeric, text, text); Type: FUNCTION; Schema: public; Owner: -
--

CREATE FUNCTION public.parapharmacie_certaine(p_num text, p_dci_id bigint, p_classe text, p_remboursable boolean, p_tarif_reference numeric, p_nom text, p_dci text DEFAULT NULL::text) RETURNS boolean
    LANGUAGE sql STABLE
    AS $$
    SELECT p_dci_id IS NULL
       AND NULLIF(btrim(COALESCE(p_dci, '')), '') IS NULL
       AND COALESCE(p_classe, '') = 'libre'   -- classe inconnue (NULL) : jamais (résultat false, pas NULL)
       AND NOT COALESCE(p_remboursable, false)
       AND p_tarif_reference IS NULL
       AND (NULLIF(btrim(p_num), '') IS NULL
            OR NOT EXISTS (SELECT 1 FROM nomenclature_produit n WHERE n.num_enregistrement = btrim(p_num)))
       AND NOT EXISTS (SELECT 1 FROM substance_surveillee s
                       WHERE majuscule_sans_accent(COALESCE(p_nom, '') || ' ' || COALESCE(p_dci, '')) LIKE '%' || s.motif || '%')
$$;


--
-- Name: FUNCTION parapharmacie_certaine(p_num text, p_dci_id bigint, p_classe text, p_remboursable boolean, p_tarif_reference numeric, p_nom text, p_dci text); Type: COMMENT; Schema: public; Owner: -
--

COMMENT ON FUNCTION public.parapharmacie_certaine(p_num text, p_dci_id bigint, p_classe text, p_remboursable boolean, p_tarif_reference numeric, p_nom text, p_dci text) IS 'Règle stricte (154) : produit classable en parapharmacie sans regard humain (hors nomenclature, sans DCI, classe libre, non remboursable, sans tarif de référence, aucune substance surveillée dans le nom)';


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
    LANGUAGE plpgsql
    AS $$
DECLARE
    v_numero  bigint;
    v_prefixe text;
    v_attente int;
BEGIN
    v_attente := encaissement_attente_secondes();
    IF v_attente > 0 THEN
        RAISE EXCEPTION 'Encaissement en attente du contrôle de démarrage avec le serveur central (n° de tickets vérifiés après une restauration) : réessayez dans % s au plus.', v_attente;
    END IF;
    UPDATE compteur_ticket SET dernier_numero = dernier_numero + 1 WHERE id = 1 RETURNING dernier_numero, prefixe INTO v_numero, v_prefixe;
    IF NOT FOUND THEN
        RAISE EXCEPTION 'Compteur des tickets absent (script 112)';
    END IF;
    RETURN v_prefixe || lpad(v_numero::text, GREATEST(6, char_length(v_numero::text)), '0');
END;
$$;


--
-- Name: proteger_credit_mouvement(); Type: FUNCTION; Schema: public; Owner: -
--

CREATE FUNCTION public.proteger_credit_mouvement() RETURNS trigger
    LANGUAGE plpgsql
    AS $$
BEGIN
    RAISE EXCEPTION 'Le relevé des dettes ne se modifie pas : corrigez par un nouveau mouvement';
END $$;


--
-- Name: proteger_fournisseur_mouvement(); Type: FUNCTION; Schema: public; Owner: -
--

CREATE FUNCTION public.proteger_fournisseur_mouvement() RETURNS trigger
    LANGUAGE plpgsql
    AS $$
BEGIN
    RAISE EXCEPTION 'Le relevé des dettes ne se modifie pas : corrigez par un nouveau mouvement';
END $$;


--
-- Name: proteger_historique_facture(); Type: FUNCTION; Schema: public; Owner: -
--

CREATE FUNCTION public.proteger_historique_facture() RETURNS trigger
    LANGUAGE plpgsql
    AS $$
BEGIN
    IF TG_TABLE_NAME = 'facture_client_version' THEN
        RAISE EXCEPTION 'Historique des factures : la version % de la facture % ne peut être ni modifiée ni supprimée.',
            OLD.version, OLD.facture_id USING ERRCODE = 'P0001';
    END IF;
    IF TG_TABLE_NAME = 'facture_client' THEN
        IF TG_OP = 'DELETE' THEN
            RAISE EXCEPTION 'Historique des factures : la facture % ne peut pas être supprimée (corrigez-la, remplacez-la ou émettez un avoir).',
                OLD.numero USING ERRCODE = 'P0001';
        END IF;
        IF OLD.statut = 'remplacee' THEN
            RAISE EXCEPTION 'Historique des factures : la facture % a été remplacée et ne peut plus être modifiée.', OLD.numero USING ERRCODE = 'P0001';
        END IF;
        RETURN NEW;
    END IF;
    -- facture_client_ligne : lignes figées dès que leur facture est remplacée
    IF EXISTS (SELECT 1 FROM facture_client f WHERE f.id = OLD.facture_id AND f.statut = 'remplacee') THEN
        RAISE EXCEPTION 'Historique des factures : les lignes d''une facture remplacée ne peuvent plus être modifiées.' USING ERRCODE = 'P0001';
    END IF;
    RETURN CASE WHEN TG_OP = 'DELETE' THEN OLD ELSE NEW END;
END $$;


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
-- Name: proteger_regularisation_part_organisme(); Type: FUNCTION; Schema: public; Owner: -
--

CREATE FUNCTION public.proteger_regularisation_part_organisme() RETURNS trigger
    LANGUAGE plpgsql
    AS $$
BEGIN
    IF TG_OP = 'UPDATE' AND OLD.statut = 'active' AND NEW.statut = 'annulee'
       AND (NEW.id, NEW.vente_id, NEW.organisme_id, NEW.part_organisme_vente, NEW.part_organisme_chifa, NEW.ecart, NEW.sort, NEW.montant_patient,
            NEW.montant_dette_diminuee, NEW.montant_a_rendre, NEW.montant_pharmacie, NEW.credit_mouvement_id, NEW.utilisateur_id, NEW.valide_par, NEW.cree_le,
            NEW.part_organisme_rendue_avant)
           IS NOT DISTINCT FROM
           (OLD.id, OLD.vente_id, OLD.organisme_id, OLD.part_organisme_vente, OLD.part_organisme_chifa, OLD.ecart, OLD.sort, OLD.montant_patient,
            OLD.montant_dette_diminuee, OLD.montant_a_rendre, OLD.montant_pharmacie, OLD.credit_mouvement_id, OLD.utilisateur_id, OLD.valide_par, OLD.cree_le,
            OLD.part_organisme_rendue_avant)
    THEN
        RETURN NEW;
    END IF;
    RAISE EXCEPTION 'Une régularisation de part organisme ne se modifie pas et ne se supprime pas (seule l''annulation de sa vente la marque annulée)';
END $$;


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
-- Name: rang_assure(text); Type: FUNCTION; Schema: public; Owner: -
--

CREATE FUNCTION public.rang_assure(p_num text) RETURNS text
    LANGUAGE sql IMMUTABLE PARALLEL SAFE
    AS $_$ SELECT substring(p_num FROM '/\s*([0-9]{2})\s*$') $_$;


--
-- Name: FUNCTION rang_assure(p_num text); Type: COMMENT; Schema: public; Owner: -
--

COMMENT ON FUNCTION public.rang_assure(p_num text) IS 'Rang de l''assuré = 2 chiffres du suffixe « /NN » du n° d''assuré ; NULL si absent ou format atypique (script 102)';


--
-- Name: rappel_lot(bigint, text, text, bigint); Type: FUNCTION; Schema: public; Owner: -
--

CREATE FUNCTION public.rappel_lot(p_produit_id bigint, p_numero_lot text, p_motif text, p_utilisateur_id bigint) RETURNS TABLE(o_patient text, o_telephone text, o_date_vente timestamp with time zone, o_ticket text, o_boites bigint)
    LANGUAGE plpgsql
    AS $$
BEGIN
    -- Bloquer le lot chez tous les grossistes où il est encore vendable,
    -- et tracer le blocage des boîtes en stock de ces lots
    WITH bloques AS (
        UPDATE lot SET bloque = true, motif_blocage = p_motif
        WHERE produit_id = p_produit_id AND numero_lot = p_numero_lot AND NOT bloque
        RETURNING id
    )
    INSERT INTO boite_evenement (boite_id, type, utilisateur_id, commentaire)
    SELECT b.id, 'blocage', p_utilisateur_id, p_motif
    FROM boite b JOIN bloques l ON l.id = b.lot_id
    WHERE b.statut = 'en_stock';

    RETURN QUERY SELECT * FROM ventes_lot(p_produit_id, p_numero_lot);
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
-- Name: refuser_retour_organisme_depose(); Type: FUNCTION; Schema: public; Owner: -
--

CREATE FUNCTION public.refuser_retour_organisme_depose() RETURNS trigger
    LANGUAGE plpgsql
    AS $$
BEGIN
    IF NEW.montant_organisme > 0 AND EXISTS (
        SELECT 1 FROM ligne_vente l
        JOIN bordereau_vente bv ON bv.vente_id = l.vente_id
        JOIN bordereau b ON b.id = bv.bordereau_id
        WHERE l.id = NEW.ligne_vente_id AND b.statut <> 'preparation') THEN
        RAISE EXCEPTION 'Vente déposée à la CNAS : retour impossible dans le logiciel tant que la procédure de la CNAS n’est pas connue'
            USING ERRCODE = 'P0001';
    END IF;
    RETURN NEW;
END;
$$;


--
-- Name: FUNCTION refuser_retour_organisme_depose(); Type: COMMENT; Schema: public; Owner: -
--

COMMENT ON FUNCTION public.refuser_retour_organisme_depose() IS 'Refuse une part organisme rendue (retour_client_ligne.montant_organisme > 0) sur une vente portée sur un bordereau déposé (script 155)';


--
-- Name: refuser_retour_prestation(); Type: FUNCTION; Schema: public; Owner: -
--

CREATE FUNCTION public.refuser_retour_prestation() RETURNS trigger
    LANGUAGE plpgsql
    AS $$
BEGIN
    IF EXISTS (SELECT 1 FROM ligne_vente WHERE id = NEW.ligne_vente_id AND type_prestation_id IS NOT NULL) THEN
        RAISE EXCEPTION 'Une prestation ne se rend pas : annulez la vente ou faites un avoir';
    END IF;
    RETURN NEW;
END;
$$;


SET default_tablespace = '';

SET default_table_access_method = heap;

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
    duree_traitement_jours integer,
    officine_id uuid DEFAULT public.officine_courante() NOT NULL,
    CONSTRAINT ck_registre_porteur_complet CHECK (((porteur_nom IS NULL) OR ((porteur_adresse IS NOT NULL) AND (porteur_piece IS NOT NULL)))),
    CONSTRAINT ck_registre_psy_duree CHECK (((duree_traitement_jours IS NULL) OR ((duree_traitement_jours >= 1) AND (duree_traitement_jours <= 3650)))),
    CONSTRAINT registre_psychotrope_check CHECK (((type_mouvement <> 'sortie'::text) OR (ordonnance_id IS NOT NULL))),
    CONSTRAINT registre_psychotrope_quantite_check CHECK ((quantite > 0)),
    CONSTRAINT registre_psychotrope_solde_apres_check CHECK ((solde_apres >= 0)),
    CONSTRAINT registre_psychotrope_type_mouvement_check CHECK ((type_mouvement = ANY (ARRAY['ouverture'::text, 'entree'::text, 'retour_client'::text, 'retour_refuse'::text, 'ecart_plus'::text, 'sortie'::text, 'retour_fournisseur'::text, 'destruction'::text, 'casse'::text, 'ecart_moins'::text])))
);


--
-- Name: COLUMN registre_psychotrope.porteur_nom; Type: COMMENT; Schema: public; Owner: -
--

COMMENT ON COLUMN public.registre_psychotrope.porteur_nom IS 'Porteur de l''ordonnance quand ce n''est pas le patient (avec adresse et pièce d''identité)';


--
-- Name: registre_canonique(public.registre_psychotrope); Type: FUNCTION; Schema: public; Owner: -
--

CREATE FUNCTION public.registre_canonique(r public.registre_psychotrope) RETURNS text
    LANGUAGE sql STABLE
    AS $$
    SELECT concat_ws('|', 'registre-v1',
        integrite_champ('id', r.id::text),
        integrite_champ('numero_ordre', r.numero_ordre::text),
        integrite_champ('lot_id', r.lot_id::text),
        integrite_champ('ligne_vente_id', r.ligne_vente_id::text),
        integrite_champ('mouvement_id', r.mouvement_id::text),
        integrite_champ('type_mouvement', r.type_mouvement),
        integrite_champ('quantite', r.quantite::text),
        integrite_champ('solde_apres', r.solde_apres::text),
        integrite_champ('patient_id', r.patient_id::text),
        integrite_champ('medecin_id', r.medecin_id::text),
        integrite_champ('ordonnance_id', r.ordonnance_id::text),
        integrite_champ('utilisateur_id', r.utilisateur_id::text),
        integrite_champ('reference', r.reference),
        integrite_champ('porteur_nom', r.porteur_nom),
        integrite_champ('porteur_adresse', r.porteur_adresse),
        integrite_champ('porteur_piece', r.porteur_piece),
        integrite_champ('cree_le', to_char(r.cree_le AT TIME ZONE 'UTC', 'YYYY-MM-DD"T"HH24:MI:SS.US"Z"')))
$$;


--
-- Name: registre_concordance(); Type: FUNCTION; Schema: public; Owner: -
--

CREATE FUNCTION public.registre_concordance() RETURNS TABLE(lignes bigint, motif text, registre_id bigint, attendu text, trouve text)
    LANGUAGE plpgsql STABLE
    AS $$
#variable_conflict use_variable
DECLARE
    v_ouv  journal_registre_psychotrope;
    v_ant  record;
    v_id   bigint;
BEGIN
    SELECT count(*) INTO lignes FROM registre_psychotrope;
    motif := NULL; registre_id := NULL; attendu := NULL; trouve := NULL;

    SELECT * INTO v_ouv FROM journal_registre_psychotrope j WHERE j.nature = 'ouverture' ORDER BY j.rang LIMIT 1;
    IF NOT FOUND THEN
        motif := 'ligne d''ouverture du journal absente'; RETURN NEXT; RETURN;
    END IF;

    -- Lignes antérieures (absentes du journal) : même nombre et même empreinte qu'à l'ouverture
    SELECT * INTO v_ant FROM registre_empreinte_anterieure();
    IF v_ant.lignes <> v_ouv.lignes_anterieures OR v_ant.empreinte <> v_ouv.empreinte_anterieure THEN
        motif := 'lignes antérieures à l''ouverture modifiées, supprimées, ou ligne ajoutée hors journal';
        attendu := v_ouv.lignes_anterieures || ' ligne(s), ' || encode(v_ouv.empreinte_anterieure, 'hex');
        trouve := v_ant.lignes || ' ligne(s), ' || encode(v_ant.empreinte, 'hex');
        RETURN NEXT; RETURN;
    END IF;

    -- Ligne recopiée disparue du registre
    SELECT j.registre_id INTO v_id FROM journal_registre_psychotrope j
    WHERE j.nature = 'inscription' AND NOT EXISTS (SELECT 1 FROM registre_psychotrope r WHERE r.id = j.registre_id)
    ORDER BY j.rang LIMIT 1;
    IF FOUND THEN
        motif := 'ligne du registre supprimée'; registre_id := v_id; RETURN NEXT; RETURN;
    END IF;

    -- Ligne du registre différente de sa copie chaînée
    SELECT r.id INTO v_id FROM registre_psychotrope r JOIN journal_registre_psychotrope j ON j.nature = 'inscription' AND j.registre_id = r.id
    WHERE (r.numero_ordre, r.lot_id, r.ligne_vente_id, r.mouvement_id, r.type_mouvement, r.quantite, r.solde_apres, r.patient_id,
           r.medecin_id, r.ordonnance_id, r.utilisateur_id, r.reference, r.porteur_nom, r.porteur_adresse, r.porteur_piece, r.cree_le)
          IS DISTINCT FROM
          (j.numero_ordre, j.lot_id, j.ligne_vente_id, j.mouvement_id, j.type_mouvement, j.quantite, j.solde_apres, j.patient_id,
           j.medecin_id, j.ordonnance_id, j.utilisateur_id, j.reference, j.porteur_nom, j.porteur_adresse, j.porteur_piece, j.inscrit_le)
    ORDER BY r.id LIMIT 1;
    IF FOUND THEN
        motif := 'ligne du registre modifiée (différente de sa copie chaînée)'; registre_id := v_id; RETURN NEXT; RETURN;
    END IF;

    -- Annulations : chacune recopiée à l'identique, aucune disparue
    SELECT a.registre_id INTO v_id FROM annulation_registre a
    LEFT JOIN journal_registre_psychotrope j ON j.nature = 'annulation' AND j.annulation_id = a.id
    WHERE j.id IS NULL OR (a.registre_id, a.motif, a.utilisateur_id, a.cree_le) IS DISTINCT FROM (j.registre_id, j.motif, j.utilisateur_id, j.inscrit_le)
    ORDER BY a.id LIMIT 1;
    IF FOUND THEN
        motif := 'annulation d''écriture modifiée ou hors journal'; registre_id := v_id; RETURN NEXT; RETURN;
    END IF;
    SELECT j.registre_id INTO v_id FROM journal_registre_psychotrope j
    WHERE j.nature = 'annulation' AND NOT EXISTS (SELECT 1 FROM annulation_registre a WHERE a.id = j.annulation_id)
    ORDER BY j.rang LIMIT 1;
    IF FOUND THEN
        motif := 'annulation d''écriture supprimée'; registre_id := v_id; RETURN NEXT; RETURN;
    END IF;

    RETURN NEXT;
END;
$$;


--
-- Name: registre_empreinte_anterieure(); Type: FUNCTION; Schema: public; Owner: -
--

CREATE FUNCTION public.registre_empreinte_anterieure() RETURNS TABLE(lignes bigint, dernier_id bigint, empreinte bytea)
    LANGUAGE sql STABLE
    AS $$
    SELECT count(*), max(r.id),
           sha256(convert_to(COALESCE(string_agg(registre_canonique(r), E'\n' ORDER BY r.id), ''), 'UTF8'))
    FROM registre_psychotrope r
    WHERE NOT EXISTS (SELECT 1 FROM journal_registre_psychotrope j WHERE j.nature = 'inscription' AND j.registre_id = r.id)
$$;


--
-- Name: registre_vers_journal(); Type: FUNCTION; Schema: public; Owner: -
--

CREATE FUNCTION public.registre_vers_journal() RETURNS trigger
    LANGUAGE plpgsql
    AS $$
BEGIN
    INSERT INTO journal_registre_psychotrope (nature, registre_id, numero_ordre, lot_id, ligne_vente_id, mouvement_id, type_mouvement,
        quantite, solde_apres, patient_id, medecin_id, ordonnance_id, utilisateur_id, reference, porteur_nom, porteur_adresse,
        porteur_piece, inscrit_le)
    VALUES ('inscription', NEW.id, NEW.numero_ordre, NEW.lot_id, NEW.ligne_vente_id, NEW.mouvement_id, NEW.type_mouvement,
        NEW.quantite, NEW.solde_apres, NEW.patient_id, NEW.medecin_id, NEW.ordonnance_id, NEW.utilisateur_id, NEW.reference,
        NEW.porteur_nom, NEW.porteur_adresse, NEW.porteur_piece, NEW.cree_le);
    RETURN NULL;
END;
$$;


--
-- Name: reglage_garde(); Type: FUNCTION; Schema: public; Owner: -
--

CREATE FUNCTION public.reglage_garde() RETURNS trigger
    LANGUAGE plpgsql
    AS $$
DECLARE
    v_mode      text;
    v_autre     boolean := false;
    v_refus     text := 'Réglage fixé par le serveur central : ' || TG_TABLE_NAME || ' ne peut pas être modifié dans l''officine.';
    v_ignorees  text[] := ARRAY['modifie_le', 'modifie_par', 'modifiee_le', 'modifiee_par'];
BEGIN
    -- Application d'un modèle, scripts, init, rejeu (marqueur posé par SET LOCAL ou PGOPTIONS) : tout passe, rien n'est noté
    IF coalesce(current_setting('pharma.reglage_central', true), '') = 'on' THEN
        IF TG_OP = 'DELETE' THEN RETURN OLD; END IF;
        RETURN NEW;
    END IF;

    -- Le repérage « venu du modèle » n'est jamais posé ni changé hors application d'un modèle
    IF TG_TABLE_NAME = 'motif_reglable' AND TG_OP <> 'DELETE' THEN
        IF TG_OP = 'INSERT' THEN v_autre := NEW.modele_code_central; ELSE v_autre := NEW.modele_code_central IS DISTINCT FROM OLD.modele_code_central; END IF;
        IF v_autre THEN
            RAISE EXCEPTION 'Réglage fixé par le serveur central : le repérage des motifs du serveur central ne se modifie pas dans l''officine.';
        END IF;
    ELSIF TG_TABLE_NAME = 'remise_automatique' AND TG_OP <> 'DELETE' THEN
        IF TG_OP = 'INSERT' THEN v_autre := NEW.modele_cle IS NOT NULL; ELSE v_autre := NEW.modele_cle IS DISTINCT FROM OLD.modele_cle; END IF;
        IF v_autre THEN
            RAISE EXCEPTION 'Réglage fixé par le serveur central : le repérage des remises du serveur central ne se modifie pas dans l''officine.';
        END IF;
    END IF;
    v_autre := false;

    v_mode := reglage_mode_effectif(TG_TABLE_NAME);
    IF v_mode = 'impose' THEN
        -- Écriture sans effet : permise
        IF TG_OP = 'UPDATE' AND NEW IS NOT DISTINCT FROM OLD THEN RETURN NEW; END IF;
        IF TG_TABLE_NAME = 'parametre_surveillance' THEN
            -- D7 : les horaires d'ouverture restent locaux ; toute autre colonne est fixée
            IF TG_OP = 'UPDATE' AND (to_jsonb(NEW) - ARRAY['heure_ouverture', 'heure_fermeture'])
                                  = (to_jsonb(OLD) - ARRAY['heure_ouverture', 'heure_fermeture']) THEN
                v_autre := true;
            END IF;
        ELSIF TG_TABLE_NAME = 'motif_reglable' THEN
            -- D5 : motifs du central verrouillés ; ajout local permis si le modèle l'accorde (ajouts_locaux)
            IF TG_OP = 'INSERT' THEN
                v_autre := coalesce((SELECT r.ajouts_locaux FROM reglage_mutualise r WHERE r.nom_table = 'motif_reglable'), true);
                v_refus := 'Réglage fixé par le serveur central : l''ajout de motifs n''est pas permis dans l''officine.';
            ELSE
                v_autre := NOT OLD.modele_code_central;
            END IF;
        ELSIF TG_TABLE_NAME = 'remise_automatique' THEN
            -- D6 : aucune création locale ; une remise locale existante reste modifiable (arrêt) ; une remise du central est verrouillée
            IF TG_OP <> 'INSERT' THEN v_autre := OLD.modele_cle IS NULL; END IF;
        END IF;
        IF NOT v_autre THEN RAISE EXCEPTION '%', v_refus; END IF;
    END IF;

    -- Modification locale permise : notée (origine locale, date), dans la même transaction ; la ligne est verrouillée jusqu'à la fin,
    -- ce qui sérialise avec l'application d'un modèle (SELECT … FOR UPDATE). Notée même sans modèle reçu et en mode « local » :
    -- c'est ce qui distingue « jamais modifiée depuis l'installation » de « modifiée sur place » (script 142).
    -- Une mise à jour qui ne change aucune valeur de réglage n'est pas une modification.
    IF TG_OP = 'UPDATE' THEN
        IF TG_TABLE_NAME = 'parametre_surveillance' THEN v_ignorees := v_ignorees || ARRAY['heure_ouverture', 'heure_fermeture']; END IF;
        IF (to_jsonb(NEW) - v_ignorees) IS NOT DISTINCT FROM (to_jsonb(OLD) - v_ignorees) THEN RETURN NEW; END IF;
    END IF;
    UPDATE reglage_mutualise SET origine = 'locale', modifie_localement_le = now() WHERE nom_table = TG_TABLE_NAME;
    IF TG_OP = 'DELETE' THEN RETURN OLD; END IF;
    RETURN NEW;
END $$;


--
-- Name: FUNCTION reglage_garde(); Type: COMMENT; Schema: public; Owner: -
--

COMMENT ON FUNCTION public.reglage_garde() IS 'Refus d''écriture sur une table imposée par le serveur central, sauf pendant l''application d''un modèle (pharma.reglage_central = on) ; toute modification locale permise est notée dans reglage_mutualise (scripts 141 et 142).';


--
-- Name: reglage_journaliser(text, text, text, text, bigint, text, jsonb, jsonb, bigint); Type: FUNCTION; Schema: public; Owner: -
--

CREATE FUNCTION public.reglage_journaliser(p_action text, p_table text, p_mode_avant text, p_mode_apres text, p_version_modele bigint, p_auteur text, p_valeur_avant jsonb, p_valeur_apres jsonb, p_utilisateur bigint DEFAULT NULL::bigint) RETURNS bigint
    LANGUAGE plpgsql
    AS $$
DECLARE v_id bigint;
BEGIN
    IF p_action IS NULL OR p_action NOT IN ('reglage_central_applique', 'reglage_central_remplace', 'reglage_central_rejete',
                                            'reglage_central_adopte', 'reglage_central_libere') THEN
        RAISE EXCEPTION 'Mutualisation : action de journal inconnue (%)', p_action;
    END IF;
    IF p_table IS NULL OR NOT p_table = ANY (reglage_tables_mutualisables()) THEN
        RAISE EXCEPTION 'Mutualisation : table non mutualisable (%)', p_table;
    END IF;
    INSERT INTO journal_audit (utilisateur_id, action, table_cible, details)
    VALUES (p_utilisateur, p_action, p_table,
            jsonb_build_object('table', p_table,
                               'mode', jsonb_build_object('avant', p_mode_avant, 'apres', p_mode_apres),
                               'version_modele', p_version_modele,
                               'auteur_central', left(p_auteur, 100),
                               'valeur', jsonb_build_object('avant', p_valeur_avant, 'apres', p_valeur_apres)))
    RETURNING id INTO v_id;
    RETURN v_id;
END $$;


--
-- Name: reglage_mode_effectif(text); Type: FUNCTION; Schema: public; Owner: -
--

CREATE FUNCTION public.reglage_mode_effectif(p_table text) RETURNS text
    LANGUAGE sql STABLE
    AS $$
    SELECT CASE WHEN EXISTS (SELECT 1 FROM sync_parametre p WHERE p.id = 1 AND p.active AND p.inscrite_le IS NOT NULL)
                THEN coalesce((SELECT r.mode FROM reglage_mutualise r WHERE r.nom_table = p_table), 'local')
                ELSE 'local' END
$$;


--
-- Name: FUNCTION reglage_mode_effectif(p_table text); Type: COMMENT; Schema: public; Owner: -
--

COMMENT ON FUNCTION public.reglage_mode_effectif(p_table text) IS 'Mode en vigueur d''une table mutualisable ; « local » tant que la synchronisation est désactivée ou l''officine non inscrite (script 141).';


--
-- Name: reglage_tables_mutualisables(); Type: FUNCTION; Schema: public; Owner: -
--

CREATE FUNCTION public.reglage_tables_mutualisables() RETURNS text[]
    LANGUAGE sql IMMUTABLE
    AS $$
    SELECT ARRAY['motif_reglable', 'parametre_alerte', 'parametre_fiscal', 'parametre_formulaire_sms', 'parametre_inventaire',
                 'parametre_serialisation', 'parametre_substitution', 'parametre_surveillance', 'parametre_tracabilite',
                 'regle_alerte_psychotrope', 'remise_automatique']::text[]
$$;


--
-- Name: revoquer_connexions_utilisateur(); Type: FUNCTION; Schema: public; Owner: -
--

CREATE FUNCTION public.revoquer_connexions_utilisateur() RETURNS trigger
    LANGUAGE plpgsql
    AS $$
BEGIN
    IF NEW.mot_de_passe_hash IS DISTINCT FROM OLD.mot_de_passe_hash
       OR (OLD.actif AND NOT NEW.actif)
       OR (NEW.doit_changer_mot_de_passe AND NOT OLD.doit_changer_mot_de_passe)
       OR (OLD.pin_hash IS NOT NULL AND NEW.pin_hash IS NULL)
    THEN
        NEW.jetons_revoques_le := now();
    END IF;
    RETURN NEW;
END;
$$;


--
-- Name: sans_accent(text); Type: FUNCTION; Schema: public; Owner: -
--

CREATE FUNCTION public.sans_accent(t text) RETURNS text
    LANGUAGE sql IMMUTABLE STRICT PARALLEL SAFE
    RETURN translate(t, 'àâäáãçéèêëíìîïñóòôöõúùûüÿœÀÂÄÁÃÇÉÈÊËÍÌÎÏÑÓÒÔÖÕÚÙÛÜŸŒ'::text, 'aaaaaceeeeiiiinooooouuuuyoAAAAACEEEEIIIINOOOOOUUUUYO'::text);


--
-- Name: serveur_ecarte_proteger(); Type: FUNCTION; Schema: public; Owner: -
--

CREATE FUNCTION public.serveur_ecarte_proteger() RETURNS trigger
    LANGUAGE plpgsql
    AS $$
BEGIN
    -- Seule la levée d'une ligne encore active est permise : les colonnes de l'écart restent inchangées.
    IF TG_OP = 'UPDATE' AND OLD.leve_le IS NULL AND NEW.leve_le IS NOT NULL
       AND (to_jsonb(NEW) - 'leve_le' - 'leve_par' - 'leve_motif') = (to_jsonb(OLD) - 'leve_le' - 'leve_par' - 'leve_motif') THEN
        RETURN NEW;
    END IF;
    RAISE EXCEPTION 'serveur_ecarte : ajout seul (seule la levée d''un écart actif est permise)';
END $$;


--
-- Name: situation_lot(bigint); Type: FUNCTION; Schema: public; Owner: -
--

CREATE FUNCTION public.situation_lot(p_lot_id bigint) RETURNS TABLE(o_situation text, o_lot_delaisse_id bigint)
    LANGUAGE sql STABLE
    AS $$
    WITH l AS (
        SELECT lt.id, lt.produit_id, lt.date_peremption, lt.date_a_verifier, r.date_reception
        FROM lot lt JOIN reception r ON r.id = lt.reception_id
        WHERE lt.id = p_lot_id
    ),
    -- Autres lots vendables du produit, reçus avant ou périmant avant le lot servi
    autres AS (
        SELECT a.id, a.date_peremption, a.date_a_verifier
        FROM l
        JOIN lot a ON a.produit_id = l.produit_id AND a.id <> l.id
        JOIN reception ra ON ra.id = a.reception_id
        WHERE NOT a.bloque AND a.quantite_disponible > 0 AND a.date_peremption > current_date
          AND (ra.date_reception < l.date_reception OR a.date_peremption < l.date_peremption)
    )
    SELECT CASE
               WHEN NOT EXISTS (SELECT 1 FROM autres) THEN 'aucune'
               -- Raison vérifiée sur les dates enregistrées : le lot servi périme avant tous les autres
               WHEN NOT l.date_a_verifier
                    AND NOT EXISTS (SELECT 1 FROM autres WHERE date_a_verifier OR date_peremption <= l.date_peremption) THEN 'peremption'
               ELSE 'obligatoire'
           END,
           (SELECT id FROM autres ORDER BY date_peremption, id LIMIT 1)
    FROM l;
$$;


--
-- Name: suivre_compte_carte(); Type: FUNCTION; Schema: public; Owner: -
--

CREATE FUNCTION public.suivre_compte_carte() RETURNS trigger
    LANGUAGE plpgsql
    AS $$
BEGIN
    IF TG_OP = 'UPDATE' AND OLD.recoit_carte AND NOT NEW.recoit_carte THEN
        UPDATE compte_carte_periode SET au = current_date - 1 WHERE compte_id = NEW.id AND au IS NULL AND du < current_date;
        -- Période ouverte le jour même : elle n'a duré aucun jour
        DELETE FROM compte_carte_periode WHERE compte_id = NEW.id AND au IS NULL AND du >= current_date;
    ELSIF NEW.recoit_carte AND (TG_OP = 'INSERT' OR NOT OLD.recoit_carte) THEN
        -- Une seule banque à la fois : une autre période encore ouverte est close (le logiciel retire d'abord la case à l'ancienne)
        UPDATE compte_carte_periode SET au = current_date - 1 WHERE compte_id <> NEW.id AND au IS NULL AND du < current_date;
        DELETE FROM compte_carte_periode WHERE compte_id <> NEW.id AND au IS NULL AND du >= current_date;
        INSERT INTO compte_carte_periode (compte_id, du) VALUES (NEW.id, current_date);
    END IF;
    RETURN NEW;
END $$;


--
-- Name: surveillance_candidats(date); Type: FUNCTION; Schema: public; Owner: -
--

CREATE FUNCTION public.surveillance_candidats(p_jour date) RETURNS TABLE(o_type text, o_niveau text, o_utilisateur_id bigint, o_jour date, o_cle text, o_message text, o_notification text, o_valeur numeric)
    LANGUAGE sql STABLE
    AS $$
    WITH p AS (SELECT * FROM parametre_surveillance WHERE id = 1),
    ev AS (
        SELECT e.* FROM evenement_caisse e
        WHERE e.cree_le >= p_jour AND e.cree_le < p_jour + 1
    ),
    ventes AS (
        SELECT v.* FROM vente v
        WHERE v.statut = 'validee' AND v.date_vente >= p_jour AND v.date_vente < p_jour + 1
    ),
    -- Boîtes retirées du panier avant le paiement
    retraits AS (
        SELECT ev.utilisateur_id, sum(COALESCE(ev.quantite, 1))::int AS boites, sum(COALESCE(ev.montant, 0)) AS montant, count(*) AS lignes
        FROM ev WHERE ev.type = 'ligne_retiree' GROUP BY ev.utilisateur_id
    ),
    tiroirs AS (
        SELECT ev.utilisateur_id, count(*)::int AS n FROM ev WHERE ev.type = 'tiroir_ouvert' GROUP BY ev.utilisateur_id
    ),
    attentes AS (
        SELECT ev.utilisateur_id, count(*)::int AS n, sum(COALESCE(ev.montant, 0)) AS montant
        FROM ev WHERE ev.type = 'attente_abandonnee' GROUP BY ev.utilisateur_id
    ),
    hors AS (
        SELECT v.utilisateur_id, count(*)::int AS n, sum(v.montant_total) AS montant,
               min(v.date_vente)::time(0) AS premiere, max(v.date_vente)::time(0) AS derniere
        FROM ventes v, p
        WHERE p.heure_ouverture IS NOT NULL
          -- Plage normale (08:00-20:00) ou passant minuit (08:00-00:30)
          AND CASE WHEN p.heure_ouverture < p.heure_fermeture
                   THEN v.date_vente::time < p.heure_ouverture OR v.date_vente::time >= p.heure_fermeture
                   ELSE v.date_vente::time >= p.heure_fermeture AND v.date_vente::time < p.heure_ouverture END
        GROUP BY v.utilisateur_id
    ),
    remises AS (
        SELECT v.utilisateur_id, count(*)::int AS n, sum(v.montant_remise) AS montant
        FROM ventes v WHERE v.remise_pct > 0 GROUP BY v.utilisateur_id
    ),
    -- Remises au même patient sur les 30 jours qui précèdent (une anomalie par patient et par mois)
    remises_patient AS (
        SELECT v.patient_id, count(*)::int AS n, sum(v.montant_remise) AS montant,
               string_agg(DISTINCT u.nom_complet, ', ') AS vendeurs
        FROM vente v JOIN utilisateur u ON u.id = v.utilisateur_id
        WHERE v.statut = 'validee' AND v.remise_pct > 0 AND v.patient_id IS NOT NULL
          AND v.date_vente >= p_jour - 29 AND v.date_vente < p_jour + 1
        GROUP BY v.patient_id
    )
    ,
    -- Inventaire : boîtes manquantes ajustées (stock diminué), par employé qui a terminé ou validé
    ajustements AS (
        SELECT m.utilisateur_id, sum(-m.quantite)::int AS boites, sum(-m.quantite * l.prix_achat_ht) AS valeur
        FROM mouvement_stock m JOIN lot l ON l.id = m.lot_id
        WHERE m.type = 'ajustement_inventaire' AND m.quantite < 0 AND m.cree_le >= p_jour AND m.cree_le < p_jour + 1
        GROUP BY m.utilisateur_id
    ),
    -- Boîtes déclarées cassées, et sorties en échantillon ou gratuité
    sorties AS (
        SELECT m.utilisateur_id, m.type, sum(-m.quantite)::int AS boites, sum(-m.quantite * l.prix_achat_ht) AS valeur
        FROM mouvement_stock m JOIN lot l ON l.id = m.lot_id
        WHERE m.type IN ('casse', 'echantillon') AND m.cree_le >= p_jour AND m.cree_le < p_jour + 1
        GROUP BY m.utilisateur_id, m.type
    ),
    -- Boîtes fantômes : inventaire terminé ce jour, lot dont au moins une boîte a été scannée, boîtes du lot en stock
    -- (entrées avant le comptage) jamais scannées pendant cet inventaire
    inv AS (
        SELECT i.id, i.rayon, i.commence_le FROM inventaire i
        WHERE i.statut = 'termine' AND i.termine_le >= p_jour AND i.termine_le < p_jour + 1
    ),
    -- Lot compté uniquement en scannant ses boîtes identifiées (boîtes vues = boîtes comptées) : un lot compté à la main,
    -- ou par un code qui ne désigne pas une boîte, n'a pas de boîtes fantômes
    lots_scannes AS (
        SELECT e.inventaire_id, b.lot_id
        FROM boite_evenement e JOIN boite b ON b.id = e.boite_id
        JOIN ligne_inventaire li ON li.inventaire_id = e.inventaire_id AND li.lot_id = b.lot_id
        WHERE e.type = 'inventaire' AND e.inventaire_id IN (SELECT id FROM inv)
        GROUP BY e.inventaire_id, b.lot_id, li.compte
        HAVING count(*) = COALESCE(li.compte, 0)
    ),
    fantomes AS (
        SELECT ls.inventaire_id, b.lot_id, count(*)::int AS n
        FROM lots_scannes ls
        JOIN inv ON inv.id = ls.inventaire_id
        JOIN boite b ON b.lot_id = ls.lot_id AND b.statut = 'en_stock' AND b.scannee_le < inv.commence_le
        WHERE NOT EXISTS (SELECT 1 FROM boite_evenement e WHERE e.boite_id = b.id AND e.type = 'inventaire' AND e.inventaire_id = ls.inventaire_id)
        GROUP BY ls.inventaire_id, b.lot_id
    ),
    fantomes_inv AS (
        SELECT f.inventaire_id, sum(f.n)::int AS n, sum(f.n * l.prix_achat_ht) AS valeur,
               string_agg(format('%s lot %s (%s)', pr.nom_commercial, l.numero_lot, f.n), ', ' ORDER BY f.n DESC) AS detail
        FROM fantomes f JOIN lot l ON l.id = f.lot_id JOIN produit pr ON pr.id = l.produit_id
        GROUP BY f.inventaire_id
    ),
    -- Produits sensibles : dernier comptage dépassé
    sensibles AS (
        SELECT (SELECT max(i.termine_le) FROM inventaire i WHERE i.rayon = 'Produits sensibles' AND i.statut = 'termine') AS dernier,
               (SELECT count(*) FROM produit pr WHERE pr.sensible AND pr.actif) AS produits
    )
    SELECT 'lignes_retirees', CASE WHEN r.boites >= 2 * p.seuil_lignes_retirees THEN 'urgent' ELSE 'attention' END,
           r.utilisateur_id, p_jour, '',
           format('%s a retiré du panier %s boîte(s) (%s ligne(s), %s DA) avant le paiement le %s.', u.nom_complet, r.boites, r.lignes,
                  to_char(r.montant, 'FM999G999G990D00'), to_char(p_jour, 'DD/MM/YYYY')),
           format('%s : %s boite(s) retiree(s) du panier avant paiement le %s.', u.nom_complet, r.boites, to_char(p_jour, 'DD/MM')),
           r.montant
    FROM retraits r JOIN utilisateur u ON u.id = r.utilisateur_id, p WHERE r.boites >= p.seuil_lignes_retirees
    UNION ALL
    SELECT 'tiroir', CASE WHEN t.n >= 2 * p.seuil_tiroir THEN 'urgent' ELSE 'attention' END, t.utilisateur_id, p_jour, '',
           format('%s a ouvert le tiroir-caisse sans vente %s fois le %s.', u.nom_complet, t.n, to_char(p_jour, 'DD/MM/YYYY')),
           format('%s : tiroir ouvert sans vente %s fois le %s.', u.nom_complet, t.n, to_char(p_jour, 'DD/MM')),
           t.n
    FROM tiroirs t JOIN utilisateur u ON u.id = t.utilisateur_id, p WHERE t.n >= p.seuil_tiroir
    UNION ALL
    SELECT 'attentes_abandonnees', CASE WHEN a.n >= 2 * p.seuil_attentes_abandonnees THEN 'urgent' ELSE 'attention' END, a.utilisateur_id, p_jour, '',
           format('%s a abandonné %s vente(s) en attente (%s DA) le %s.', u.nom_complet, a.n, to_char(a.montant, 'FM999G999G990D00'), to_char(p_jour, 'DD/MM/YYYY')),
           format('%s : %s vente(s) en attente abandonnee(s) le %s.', u.nom_complet, a.n, to_char(p_jour, 'DD/MM')),
           a.montant
    FROM attentes a JOIN utilisateur u ON u.id = a.utilisateur_id, p WHERE a.n >= p.seuil_attentes_abandonnees
    UNION ALL
    SELECT 'hors_horaires', 'urgent', h.utilisateur_id, p_jour, '',
           format('%s a encaissé %s vente(s) (%s DA) hors des horaires d''ouverture le %s (de %s à %s).', u.nom_complet, h.n,
                  to_char(h.montant, 'FM999G999G990D00'), to_char(p_jour, 'DD/MM/YYYY'), h.premiere, h.derniere),
           format('%s : %s vente(s) hors horaires le %s.', u.nom_complet, h.n, to_char(p_jour, 'DD/MM')),
           h.montant
    FROM hors h JOIN utilisateur u ON u.id = h.utilisateur_id
    UNION ALL
    SELECT 'remises', CASE WHEN r.n >= 2 * p.seuil_remises THEN 'urgent' ELSE 'attention' END, r.utilisateur_id, p_jour, '',
           format('%s a accordé une remise sur %s vente(s) (%s DA de remise) le %s.', u.nom_complet, r.n, to_char(r.montant, 'FM999G999G990D00'), to_char(p_jour, 'DD/MM/YYYY')),
           format('%s : remise sur %s vente(s) le %s.', u.nom_complet, r.n, to_char(p_jour, 'DD/MM')),
           r.montant
    FROM remises r JOIN utilisateur u ON u.id = r.utilisateur_id, p WHERE r.n >= p.seuil_remises
    UNION ALL
    SELECT 'remises_patient', CASE WHEN rp.n >= 2 * p.seuil_remises_patient THEN 'urgent' ELSE 'attention' END, NULL::bigint,
           date_trunc('month', p_jour)::date, 'patient:' || rp.patient_id,
           format('%s %s : %s vente(s) avec remise en 30 jours (%s DA de remise), accordées par %s.',
                  pt.nom, COALESCE(pt.prenom, ''), rp.n, to_char(rp.montant, 'FM999G999G990D00'), rp.vendeurs),
           format('Remises repetees au meme patient : %s ventes en 30 jours (detail dans Controle interne).', rp.n),
           rp.montant
    FROM remises_patient rp JOIN patient pt ON pt.id = rp.patient_id, p WHERE rp.n >= p.seuil_remises_patient
      -- Une série de remises à cheval sur deux mois ne crée pas une seconde anomalie
      AND NOT EXISTS (SELECT 1 FROM alerte_surveillance a WHERE a.type = 'remises_patient' AND a.cle = 'patient:' || rp.patient_id
                      AND a.jour < date_trunc('month', p_jour)::date AND a.cree_le >= p_jour - 29)
    UNION ALL
    SELECT 'ajustements', CASE WHEN a.boites >= 2 * p.seuil_ajustements THEN 'urgent' ELSE 'attention' END, a.utilisateur_id, p_jour, '',
           format('Inventaires terminés ou validés par %s le %s : %s boîte(s) manquante(s) ajustée(s) (%s DA au prix d''achat).', u.nom_complet,
                  to_char(p_jour, 'DD/MM/YYYY'), a.boites, to_char(a.valeur, 'FM999G999G990D00')),
           format('%s : %s boite(s) manquante(s) ajustee(s) en inventaire le %s.', u.nom_complet, a.boites, to_char(p_jour, 'DD/MM')),
           a.valeur
    FROM ajustements a JOIN utilisateur u ON u.id = a.utilisateur_id, p WHERE a.boites >= p.seuil_ajustements
    UNION ALL
    SELECT CASE s.type WHEN 'casse' THEN 'casses' ELSE 'echantillons' END,
           CASE WHEN s.boites >= 2 * CASE s.type WHEN 'casse' THEN p.seuil_casses ELSE p.seuil_echantillons END THEN 'urgent' ELSE 'attention' END,
           s.utilisateur_id, p_jour, '',
           format('%s a déclaré %s boîte(s) %s le %s (%s DA au prix d''achat).', u.nom_complet, s.boites,
                  CASE s.type WHEN 'casse' THEN 'cassée(s)' ELSE 'sortie(s) en échantillon ou gratuité' END,
                  to_char(p_jour, 'DD/MM/YYYY'), to_char(s.valeur, 'FM999G999G990D00')),
           format('%s : %s boite(s) %s le %s.', u.nom_complet, s.boites, CASE s.type WHEN 'casse' THEN 'cassee(s)' ELSE 'en echantillon' END, to_char(p_jour, 'DD/MM')),
           s.valeur
    FROM sorties s JOIN utilisateur u ON u.id = s.utilisateur_id, p
    WHERE s.boites >= CASE s.type WHEN 'casse' THEN p.seuil_casses ELSE p.seuil_echantillons END
    UNION ALL
    SELECT 'boites_fantomes', CASE WHEN fi.n >= 3 THEN 'urgent' ELSE 'attention' END, NULL::bigint, p_jour, 'inventaire:' || fi.inventaire_id,
           format('Inventaire « %s » : %s boîte(s) identifiée(s) en stock non retrouvée(s) au comptage : %s. Elles ont peut-être quitté la pharmacie : recherchez-les, puis faites leur sortie.',
                  inv.rayon, fi.n, fi.detail),
           format('Inventaire %s : %s boite(s) identifiee(s) non retrouvee(s) au comptage.', inv.rayon, fi.n),
           fi.valeur
    FROM fantomes_inv fi JOIN inv ON inv.id = fi.inventaire_id
    UNION ALL
    SELECT 'sensibles_en_retard', 'attention', NULL::bigint, COALESCE(se.dernier::date, DATE '2000-01-01'), 'sensibles',
           format('Comptage des produits sensibles (%s produit(s)) %s : à faire dans Stock, Inventaire, « Produits sensibles ».', se.produits,
                  CASE WHEN se.dernier IS NULL THEN 'jamais fait' ELSE 'pas fait depuis le ' || to_char(se.dernier, 'DD/MM/YYYY') END),
           'Comptage des produits sensibles en retard.',
           NULL::numeric
    FROM sensibles se, p
    WHERE se.produits > 0 AND (se.dernier IS NULL OR se.dernier < p_jour - p.jours_comptage_sensibles + 1);
$$;


--
-- Name: surveillance_candidats_achats(date); Type: FUNCTION; Schema: public; Owner: -
--

CREATE FUNCTION public.surveillance_candidats_achats(p_jour date) RETURNS TABLE(o_type text, o_niveau text, o_utilisateur_id bigint, o_jour date, o_cle text, o_message text, o_notification text, o_valeur numeric)
    LANGUAGE sql STABLE
    AS $$
    WITH p AS (SELECT * FROM parametre_surveillance WHERE id = 1),
    -- Écart entre le comptage et la facture, constaté ce jour : l'anomalie nomme celui qui a compté
    ecarts AS (
        SELECT c.*, f.raison_sociale, uc.nom_complet AS compteur, uf.nom_complet AS factureur
        FROM comptage_reception c
        JOIN fournisseur f ON f.id = c.fournisseur_id
        JOIN utilisateur uc ON uc.id = c.compte_par
        JOIN utilisateur uf ON uf.id = c.facture_par
        WHERE c.ecarts IS NOT NULL AND c.facture_le >= p_jour AND c.facture_le < p_jour + 1
    ),
    -- Même personne au comptage et à la saisie de la facture
    meme AS (
        SELECT c.compte_par AS utilisateur_id, count(*)::int AS n, string_agg(c.numero_bl, ', ' ORDER BY c.id) AS bls
        FROM comptage_reception c
        WHERE c.facture_par = c.compte_par AND c.facture_le >= p_jour AND c.facture_le < p_jour + 1
        GROUP BY c.compte_par
    ),
    -- Retour envoyé sans avoir au-delà du délai
    sans_avoir AS (
        SELECT r.*, f.raison_sociale, (p_jour - r.envoye_le::date) AS jours
        FROM retour_fournisseur r JOIN fournisseur f ON f.id = r.fournisseur_id, p
        WHERE r.statut = 'envoye' AND r.envoye_le IS NOT NULL AND r.envoye_le::date <= p_jour - p.jours_avoir_retour
    ),
    -- Avoir reçu ce jour, inférieur au montant attendu
    incomplets AS (
        SELECT r.*, f.raison_sociale
        FROM retour_fournisseur r JOIN fournisseur f ON f.id = r.fournisseur_id
        WHERE r.statut = 'avoir_recu' AND r.avoir_recu_le >= p_jour AND r.avoir_recu_le < p_jour + 1
          AND r.montant_attendu IS NOT NULL AND COALESCE(r.montant_avoir, 0) < r.montant_attendu - 0.01
    )
    SELECT 'ecart_reception', CASE WHEN e.boites_manquantes > 0 THEN 'urgent' ELSE 'attention' END, e.compte_par, e.facture_le::date,
           'comptage:' || e.id,
           format('Livraison BL %s de %s : écart entre le comptage de %s et la facture saisie par %s : %s boîte(s) manquante(s) (%s DA facturés), %s boîte(s) en trop. Réception bloquée jusqu''à validation (Achats, Réceptions).',
                  e.numero_bl, e.raison_sociale, e.compteur, e.factureur, e.boites_manquantes,
                  to_char(e.valeur_manquante, 'FM999G999G990D00'), e.boites_en_trop),
           format('Ecart de reception BL %s (%s) : %s boite(s) manquante(s), %s en trop, comptage de %s.', e.numero_bl, e.raison_sociale,
                  e.boites_manquantes, e.boites_en_trop, e.compteur),
           e.valeur_manquante
    FROM ecarts e
    UNION ALL
    SELECT 'reception_meme_personne', 'attention', m.utilisateur_id, p_jour, '',
           format('%s a compté les boîtes puis saisi la facture de %s livraison(s) le %s (BL %s) : la réception à l''aveugle demande deux personnes.',
                  u.nom_complet, m.n, to_char(p_jour, 'DD/MM/YYYY'), m.bls),
           format('%s : comptage et facture de %s livraison(s) faits seul le %s.', u.nom_complet, m.n, to_char(p_jour, 'DD/MM')),
           m.n
    FROM meme m JOIN utilisateur u ON u.id = m.utilisateur_id
    UNION ALL
    SELECT 'retour_sans_avoir', CASE WHEN s.jours >= 2 * p.jours_avoir_retour THEN 'urgent' ELSE 'attention' END,
           s.utilisateur_id, s.envoye_le::date, 'retour:' || s.id,
           format('Retour %s envoyé à %s le %s (%s DA attendus) : avoir toujours pas reçu après %s jours. Relancez le grossiste.',
                  s.numero, s.raison_sociale, to_char(s.envoye_le, 'DD/MM/YYYY'), to_char(COALESCE(s.montant_attendu, 0), 'FM999G999G990D00'), s.jours),
           format('Retour %s (%s) sans avoir depuis %s jours.', s.numero, s.raison_sociale, s.jours),
           s.montant_attendu
    FROM sans_avoir s, p
    UNION ALL
    SELECT 'avoir_incomplet', 'attention', i.utilisateur_id, i.avoir_recu_le::date, 'retour:' || i.id,
           format('Retour %s à %s : avoir de %s DA pour %s DA attendus (%s DA manquants).', i.numero, i.raison_sociale,
                  to_char(COALESCE(i.montant_avoir, 0), 'FM999G999G990D00'), to_char(i.montant_attendu, 'FM999G999G990D00'),
                  to_char(i.montant_attendu - COALESCE(i.montant_avoir, 0), 'FM999G999G990D00')),
           format('Avoir incomplet pour le retour %s (%s).', i.numero, i.raison_sociale),
           i.montant_attendu - COALESCE(i.montant_avoir, 0)
    FROM incomplets i;
$$;


--
-- Name: surveillance_candidats_comptes(date); Type: FUNCTION; Schema: public; Owner: -
--

CREATE FUNCTION public.surveillance_candidats_comptes(p_jour date) RETURNS TABLE(o_type text, o_niveau text, o_utilisateur_id bigint, o_jour date, o_cle text, o_message text, o_notification text, o_valeur numeric)
    LANGUAGE sql STABLE
    AS $$
    WITH p AS (SELECT * FROM parametre_surveillance WHERE id = 1),
    -- Crédits accordés (ventes à crédit), par patient et par employé
    credits AS (
        SELECT m.patient_id, m.utilisateur_id, m.montant, m.cree_le FROM credit_mouvement m WHERE m.type = 'vente_credit'
    ),
    -- Patient récent dont la dette atteint le seuil, crédit accordé ce jour
    nouveaux AS (
        SELECT pt.id, pt.nom, pt.prenom, pt.solde_credit, pt.cree_le, pt.cree_par,
               (SELECT string_agg(DISTINCT u.nom_complet, ', ') FROM credits c JOIN utilisateur u ON u.id = c.utilisateur_id WHERE c.patient_id = pt.id) AS vendeurs
        FROM patient pt, p
        WHERE pt.cree_le >= p_jour - p.jours_patient_recent + 1 AND pt.solde_credit >= p.seuil_credit_nouveau_patient
          AND EXISTS (SELECT 1 FROM credits c WHERE c.patient_id = pt.id AND c.cree_le >= p_jour AND c.cree_le < p_jour + 1)
    ),
    -- Crédits accordés par un employé ce jour
    par_employe AS (
        SELECT c.utilisateur_id, count(*)::int AS n, sum(c.montant) AS montant, count(DISTINCT c.patient_id)::int AS patients
        FROM credits c WHERE c.utilisateur_id IS NOT NULL AND c.cree_le >= p_jour AND c.cree_le < p_jour + 1
        GROUP BY c.utilisateur_id
    ),
    -- Patient endetté qui n'a jamais rien remboursé depuis son premier crédit
    jamais AS (
        SELECT pt.id, pt.nom, pt.prenom, pt.solde_credit, min(c.cree_le) AS premier,
               string_agg(DISTINCT u.nom_complet, ', ') AS vendeurs
        FROM patient pt JOIN credits c ON c.patient_id = pt.id LEFT JOIN utilisateur u ON u.id = c.utilisateur_id, p
        WHERE pt.solde_credit > 0
          AND NOT EXISTS (SELECT 1 FROM credit_mouvement r WHERE r.patient_id = pt.id AND r.type = 'remboursement')
        GROUP BY pt.id, pt.nom, pt.prenom, pt.solde_credit, p.jours_credit_sans_remboursement
        HAVING min(c.cree_le)::date <= p_jour - p.jours_credit_sans_remboursement
    ),
    -- Autorisations sur place par code comptoir ce jour
    autorisations AS (
        SELECT j.utilisateur_id, j.autorise_par, j.details->>'droit' AS droit
        FROM journal_audit j
        WHERE j.action = 'autorisation_code' AND j.autorise_par IS NOT NULL AND j.cree_le >= p_jour AND j.cree_le < p_jour + 1
    ),
    par_valideur AS (
        SELECT a.autorise_par AS utilisateur_id, count(*)::int AS n, string_agg(DISTINCT u.nom_complet, ', ') AS employes
        FROM autorisations a JOIN utilisateur u ON u.id = a.utilisateur_id GROUP BY a.autorise_par
    ),
    par_beneficiaire AS (
        SELECT a.utilisateur_id, count(*)::int AS n, string_agg(DISTINCT v.nom_complet, ', ') AS valideurs, string_agg(DISTINCT a.droit, ', ') AS droits
        FROM autorisations a JOIN utilisateur v ON v.id = a.autorise_par GROUP BY a.utilisateur_id
    ),
    -- Activité par poste ce jour (connexions et ventes) : changements de poste rapprochés
    activite AS (
        SELECT c.utilisateur_id, c.poste, c.cree_le AS t FROM connexion_employe c
        WHERE c.poste IS NOT NULL AND c.cree_le >= p_jour AND c.cree_le < p_jour + 1
        UNION ALL
        SELECT v.utilisateur_id, s.poste, v.date_vente FROM vente v JOIN session_caisse s ON s.id = v.session_caisse_id
        WHERE v.date_vente >= p_jour AND v.date_vente < p_jour + 1
    ),
    suite AS (
        SELECT a.utilisateur_id, a.poste, a.t,
               lag(a.poste) OVER w AS poste_avant, lag(a.t) OVER w AS t_avant
        FROM activite a WINDOW w AS (PARTITION BY a.utilisateur_id ORDER BY a.t)
    ),
    postes AS (
        SELECT s.utilisateur_id, count(*)::int AS n, string_agg(DISTINCT s.poste_avant || ' / ' || s.poste, ', ') AS paires
        FROM suite s, p
        WHERE s.poste_avant IS NOT NULL AND s.poste_avant <> s.poste AND s.t - s.t_avant <= make_interval(mins => p.minutes_postes_simultanes)
        GROUP BY s.utilisateur_id
    ),
    -- Baisses de prix par un autre que le titulaire
    baisses AS (
        SELECT j.utilisateur_id, count(*)::int AS n,
               string_agg(format('%s : %s → %s DA', j.details->>'produit', j.details->>'avant', j.details->>'apres'), ', ' ORDER BY j.id) AS detail,
               sum((j.details->>'avant')::numeric - (j.details->>'apres')::numeric) AS baisse
        FROM journal_audit j JOIN utilisateur u ON u.id = j.utilisateur_id JOIN role r ON r.id = u.role_id
        WHERE j.action = 'prix_modifie' AND j.cree_le >= p_jour AND j.cree_le < p_jour + 1 AND r.code <> 'titulaire'
          AND (j.details->>'apres')::numeric < (j.details->>'avant')::numeric
        GROUP BY j.utilisateur_id
    )
    SELECT 'credit_nouveau_patient', CASE WHEN n.solde_credit >= 2 * p.seuil_credit_nouveau_patient THEN 'urgent' ELSE 'attention' END,
           n.cree_par, n.cree_le::date, 'patient:' || n.id,
           format('%s %s, patient créé le %s%s : dette de %s DA dès ses premiers jours (crédits accordés par %s).', n.nom, COALESCE(n.prenom, ''),
                  to_char(n.cree_le, 'DD/MM/YYYY'), COALESCE(' par ' || (SELECT nom_complet FROM utilisateur WHERE id = n.cree_par), ''),
                  to_char(n.solde_credit, 'FM999G999G990D00'), COALESCE(n.vendeurs, '?')),
           format('Patient cree recemment avec une dette de %s DA (detail dans Controle interne).', to_char(n.solde_credit, 'FM999G999G990')),
           n.solde_credit
    FROM nouveaux n, p
    UNION ALL
    SELECT 'credits_employe', CASE WHEN e.montant >= 2 * p.seuil_credits_jour THEN 'urgent' ELSE 'attention' END, e.utilisateur_id, p_jour, '',
           format('%s a accordé %s vente(s) à crédit le %s à %s patient(s), pour %s DA.', u.nom_complet, e.n, to_char(p_jour, 'DD/MM/YYYY'),
                  e.patients, to_char(e.montant, 'FM999G999G990D00')),
           format('%s : %s DA de credits accordes le %s.', u.nom_complet, to_char(e.montant, 'FM999G999G990'), to_char(p_jour, 'DD/MM')),
           e.montant
    FROM par_employe e JOIN utilisateur u ON u.id = e.utilisateur_id, p WHERE e.montant >= p.seuil_credits_jour
    UNION ALL
    SELECT 'credit_sans_remboursement', 'attention', NULL::bigint, j.premier::date, 'patient:' || j.id,
           format('%s %s doit %s DA et n''a jamais rien remboursé depuis son premier crédit du %s (crédits accordés par %s).',
                  j.nom, COALESCE(j.prenom, ''), to_char(j.solde_credit, 'FM999G999G990D00'), to_char(j.premier, 'DD/MM/YYYY'), COALESCE(j.vendeurs, '?')),
           format('Patient endette sans aucun remboursement depuis le %s (detail dans Controle interne).', to_char(j.premier, 'DD/MM/YYYY')),
           j.solde_credit
    FROM jamais j
    UNION ALL
    SELECT 'autorisations_valideur', CASE WHEN v.n >= 2 * p.seuil_autorisations_valideur THEN 'urgent' ELSE 'attention' END, v.utilisateur_id, p_jour, '',
           format('%s a autorisé %s action(s) par son code comptoir le %s (pour %s).', u.nom_complet, v.n, to_char(p_jour, 'DD/MM/YYYY'), v.employes),
           format('%s : %s autorisations par code comptoir le %s.', u.nom_complet, v.n, to_char(p_jour, 'DD/MM')),
           v.n
    FROM par_valideur v JOIN utilisateur u ON u.id = v.utilisateur_id, p WHERE v.n >= p.seuil_autorisations_valideur
    UNION ALL
    SELECT 'autorisations_employe', CASE WHEN b.n >= 2 * p.seuil_autorisations_employe THEN 'urgent' ELSE 'attention' END, b.utilisateur_id, p_jour, '',
           format('%s a eu besoin de %s autorisation(s) par code comptoir le %s (droits : %s ; autorisé par %s).', u.nom_complet, b.n,
                  to_char(p_jour, 'DD/MM/YYYY'), b.droits, b.valideurs),
           format('%s : %s autorisations par code demandees le %s.', u.nom_complet, b.n, to_char(p_jour, 'DD/MM')),
           b.n
    FROM par_beneficiaire b JOIN utilisateur u ON u.id = b.utilisateur_id, p WHERE b.n >= p.seuil_autorisations_employe
    UNION ALL
    SELECT 'postes_simultanes', CASE WHEN s.n >= 2 * p.seuil_changements_poste THEN 'urgent' ELSE 'attention' END, s.utilisateur_id, p_jour, '',
           format('%s a travaillé sur deux postes à la fois le %s : %s passage(s) d''un poste à l''autre en moins de %s minutes (%s). Son identifiant ou son code est peut-être utilisé par quelqu''un d''autre.',
                  u.nom_complet, to_char(p_jour, 'DD/MM/YYYY'), s.n, p.minutes_postes_simultanes, s.paires),
           format('%s : actif sur deux postes a la fois le %s.', u.nom_complet, to_char(p_jour, 'DD/MM')),
           s.n
    FROM postes s JOIN utilisateur u ON u.id = s.utilisateur_id, p WHERE s.n >= p.seuil_changements_poste
    UNION ALL
    SELECT 'baisses_prix', CASE WHEN b.n >= 2 * p.seuil_baisses_prix THEN 'urgent' ELSE 'attention' END, b.utilisateur_id, p_jour, '',
           format('%s a baissé le prix de vente de %s produit(s) le %s : %s.', u.nom_complet, b.n, to_char(p_jour, 'DD/MM/YYYY'), b.detail),
           format('%s : %s baisse(s) de prix le %s.', u.nom_complet, b.n, to_char(p_jour, 'DD/MM')),
           b.baisse
    FROM baisses b JOIN utilisateur u ON u.id = b.utilisateur_id, p WHERE b.n >= p.seuil_baisses_prix;
$$;


--
-- Name: surveillance_candidats_tiroir(date); Type: FUNCTION; Schema: public; Owner: -
--

CREATE FUNCTION public.surveillance_candidats_tiroir(p_jour date) RETURNS TABLE(o_type text, o_niveau text, o_utilisateur_id bigint, o_jour date, o_cle text, o_message text, o_notification text, o_valeur numeric)
    LANGUAGE sql STABLE
    AS $$
    WITH p AS (SELECT seuil_sorties_tiroir FROM parametre_surveillance WHERE id = 1),
    sorties AS (
        SELECT m.utilisateur_id, count(*) AS n, sum(m.montant) AS total,
               string_agg(DISTINCT m.motif_libelle, ', ') AS motifs
        FROM mouvement_tiroir m
        WHERE m.sens = 'sortie' AND m.corrige_id IS NULL AND m.cree_le::date = p_jour
        GROUP BY m.utilisateur_id)
    SELECT 'sorties_tiroir', CASE WHEN s.n >= 2 * p.seuil_sorties_tiroir THEN 'urgent' ELSE 'attention' END, s.utilisateur_id, p_jour, '',
           format('%s a fait %s sortie(s) d''argent par le tiroir le %s, pour %s DA (%s).', u.nom_complet, s.n, to_char(p_jour, 'DD/MM/YYYY'),
                  to_char(s.total, 'FM999G999G990D00'), s.motifs),
           format('%s : %s sorties du tiroir le %s.', u.nom_complet, s.n, to_char(p_jour, 'DD/MM')),
           s.n
    FROM sorties s JOIN utilisateur u ON u.id = s.utilisateur_id, p WHERE s.n >= p.seuil_sorties_tiroir;
$$;


--
-- Name: surveiller(date); Type: FUNCTION; Schema: public; Owner: -
--

CREATE FUNCTION public.surveiller(p_jour date) RETURNS TABLE(o_id bigint, o_niveau text, o_notification text)
    LANGUAGE plpgsql
    AS $$
DECLARE
    c        record;
    v_id     bigint;
    v_ancien text;
BEGIN
    FOR c IN SELECT * FROM surveillance_candidats(p_jour)
             UNION ALL SELECT * FROM surveillance_candidats_achats(p_jour)
             UNION ALL SELECT * FROM surveillance_candidats_comptes(p_jour)
             UNION ALL SELECT * FROM surveillance_candidats_tiroir(p_jour) LOOP
        v_id := NULL;
        SELECT a.id, a.niveau INTO v_id, v_ancien FROM alerte_surveillance a
        WHERE a.type = c.o_type AND COALESCE(a.utilisateur_id, 0) = COALESCE(c.o_utilisateur_id, 0) AND a.jour = c.o_jour AND a.cle = c.o_cle;
        IF v_id IS NULL THEN
            INSERT INTO alerte_surveillance (type, niveau, utilisateur_id, jour, cle, message, valeur)
            VALUES (c.o_type, c.o_niveau, c.o_utilisateur_id, c.o_jour, c.o_cle, c.o_message, c.o_valeur)
            RETURNING id INTO v_id;
            o_id := v_id; o_niveau := c.o_niveau; o_notification := c.o_notification;
            RETURN NEXT;
        ELSE
            -- Une anomalie traitée reste traitée ; son message suit les chiffres du jour
            -- Une anomalie traitée qui devient urgente revient « à examiner » (la notification renvoie à l'écran)
            UPDATE alerte_surveillance SET message = c.o_message, valeur = c.o_valeur, niveau = c.o_niveau, mise_a_jour_le = now(),
                   statut = CASE WHEN v_ancien <> c.o_niveau AND c.o_niveau = 'urgent' THEN 'nouvelle' ELSE statut END,
                   action_prise = CASE WHEN v_ancien <> c.o_niveau AND c.o_niveau = 'urgent' AND statut = 'traitee'
                                       THEN concat('Traitée le ', to_char(traitee_le, 'DD/MM/YYYY HH24:MI'), COALESCE(' : ' || action_prise, ''),
                                                   ' ; redevenue urgente.')
                                       ELSE action_prise END,
                   traitee_par = CASE WHEN v_ancien <> c.o_niveau AND c.o_niveau = 'urgent' THEN NULL ELSE traitee_par END,
                   traitee_le = CASE WHEN v_ancien <> c.o_niveau AND c.o_niveau = 'urgent' THEN NULL ELSE traitee_le END
            WHERE id = v_id;
            IF v_ancien <> c.o_niveau AND c.o_niveau = 'urgent' THEN
                o_id := v_id; o_niveau := c.o_niveau; o_notification := c.o_notification;
                RETURN NEXT;
            END IF;
        END IF;
    END LOOP;
END;
$$;


--
-- Name: sync_appliquer_descendant(text, text, bigint, jsonb); Type: FUNCTION; Schema: public; Owner: -
--

CREATE FUNCTION public.sync_appliquer_descendant(p_table text, p_cle text, p_version bigint, p_donnees jsonb) RETURNS boolean
    LANGUAGE plpgsql
    AS $$
DECLARE n int;
BEGIN
    IF p_version IS NULL OR p_version < 1 OR p_donnees IS NULL OR jsonb_typeof(p_donnees) <> 'object' THEN
        RAISE EXCEPTION 'Synchronisation : élément descendant mal formé (%/%)', p_table, p_cle;
    END IF;
    IF p_table = 'reglage_central' THEN
        INSERT INTO reglage_central AS r (cle, version, valeur) VALUES (p_cle, p_version, p_donnees)
        ON CONFLICT (cle) DO UPDATE SET version = EXCLUDED.version, valeur = EXCLUDED.valeur, recu_le = now()
        WHERE r.version < EXCLUDED.version;
        GET DIAGNOSTICS n = ROW_COUNT;
    ELSE
        RAISE EXCEPTION 'Synchronisation : table descendante non prévue : %', p_table;
    END IF;
    UPDATE sync_parametre SET descendant_version = greatest(descendant_version, p_version) WHERE id = 1;
    RETURN n > 0;
END $$;


--
-- Name: sync_avancer_compteur_ticket(bigint, integer); Type: FUNCTION; Schema: public; Owner: -
--

CREATE FUNCTION public.sync_avancer_compteur_ticket(p_numero bigint, p_epoque integer DEFAULT NULL::integer) RETURNS bigint
    LANGUAGE plpgsql
    AS $$
DECLARE c compteur_ticket;
BEGIN
    IF p_numero IS NULL OR p_numero < 1 THEN RETURN 0; END IF;
    SELECT * INTO c FROM compteur_ticket WHERE id = 1 FOR UPDATE;
    IF NOT FOUND OR p_numero <= c.dernier_numero THEN RETURN 0; END IF;
    IF p_numero - c.dernier_numero > 10000000 THEN
        RAISE EXCEPTION 'Compteur des tickets : saut de % n° refusé (plus de 10 millions)', p_numero - c.dernier_numero USING ERRCODE = 'check_violation';
    END IF;
    INSERT INTO compteur_ticket_saut (du, au, motif, epoque) VALUES (c.dernier_numero + 1, p_numero, 'restauration', p_epoque);
    PERFORM set_config('pharma.saut_ticket', p_numero::text, true);
    UPDATE compteur_ticket SET dernier_numero = p_numero WHERE id = 1;
    PERFORM set_config('pharma.saut_ticket', '', true);
    RETURN p_numero - c.dernier_numero;
END $$;


--
-- Name: FUNCTION sync_avancer_compteur_ticket(p_numero bigint, p_epoque integer); Type: COMMENT; Schema: public; Owner: -
--

COMMENT ON FUNCTION public.sync_avancer_compteur_ticket(p_numero bigint, p_epoque integer) IS 'Après un retour en arrière : compteur des tickets avancé au-delà du plus grand n° reçu par le serveur central, plage notée (script 151, S6.5-D6 a).';


--
-- Name: sync_avancer_sequences(jsonb); Type: FUNCTION; Schema: public; Owner: -
--

CREATE FUNCTION public.sync_avancer_sequences(p_maximums jsonb) RETURNS TABLE(nom_table text, avant bigint, apres bigint, erreur text)
    LANGUAGE plpgsql
    AS $_$
#variable_conflict use_column
DECLARE
    p          sync_parametre;
    r          record;
    v_seq      text;
    v_max      bigint;
    v_dernier  bigint;
    v_appele   boolean;
    v_actuel   bigint;
    v_echecs   int := 0;
BEGIN
    IF pg_is_in_recovery() THEN
        RAISE EXCEPTION 'Synchronisation : avance des n° impossible sur une base en lecture seule';
    END IF;
    IF p_maximums IS NULL OR jsonb_typeof(p_maximums) <> 'object' THEN
        RAISE EXCEPTION 'Synchronisation : maximums attendus sous la forme { table : n° }';
    END IF;
    SELECT * INTO p FROM sync_parametre WHERE id = 1 FOR UPDATE;
    PERFORM set_config('lock_timeout', '5s', true);
    FOR r IN
        SELECT s.nom_table AS t, m.value AS v
        FROM jsonb_each(p_maximums) m
        JOIN sync_table s ON s.nom_table = m.key
        JOIN pg_tables pt ON pt.schemaname = 'public' AND pt.tablename = s.nom_table
        WHERE sync_sens_effectif(s, p) IN ('montante', 'bidirectionnelle') AND s.cle_colonnes = '{id}'
        ORDER BY s.nom_table
    LOOP
        CONTINUE WHEN jsonb_typeof(r.v) <> 'number' OR (r.v #>> '{}') !~ '^[0-9]{1,18}$';
        v_max := (r.v #>> '{}')::bigint;
        CONTINUE WHEN v_max < 1;
        v_seq := pg_get_serial_sequence(format('public.%I', r.t), 'id');
        CONTINUE WHEN v_seq IS NULL;
        nom_table := r.t; avant := NULL; apres := NULL; erreur := NULL;
        BEGIN
            EXECUTE format('LOCK TABLE public.%I IN SHARE ROW EXCLUSIVE MODE', r.t);
            EXECUTE format('SELECT last_value, is_called FROM %s', v_seq) INTO v_dernier, v_appele;
            -- n° déjà attribué au plus : last_value si la séquence a servi, sinon last_value - 1
            v_actuel := CASE WHEN v_appele THEN v_dernier ELSE v_dernier - 1 END;
            avant := v_actuel;
            IF v_max > v_actuel THEN
                PERFORM setval(v_seq, v_max, true);
                apres := v_max;
            ELSE
                apres := v_actuel;
            END IF;
        EXCEPTION WHEN others THEN
            -- Sous-transaction annulée (verrou relâché, séquence inchangée) : la table est signalée, les suivantes sont traitées
            v_echecs := v_echecs + 1;
            avant := NULL; apres := NULL;
            erreur := SQLSTATE || ' ' || left(SQLERRM, 200);
        END;
        RETURN NEXT;
    END LOOP;
    IF p.retour_arriere_le IS NOT NULL AND v_echecs = 0 THEN
        UPDATE sync_parametre SET retour_arriere_sequences_le = now() WHERE id = 1;
    END IF;
END $_$;


--
-- Name: FUNCTION sync_avancer_sequences(p_maximums jsonb); Type: COMMENT; Schema: public; Owner: -
--

COMMENT ON FUNCTION public.sync_avancer_sequences(p_maximums jsonb) IS 'Avance (jamais en arrière, table verrouillée) les séquences id des tables envoyées au-delà des n° connus du serveur central ; échec rendu par table (script 149).';


--
-- Name: sync_chargement_accuse(integer); Type: FUNCTION; Schema: public; Owner: -
--

CREATE FUNCTION public.sync_chargement_accuse(p_numero integer) RETURNS boolean
    LANGUAGE sql STABLE
    AS $$
    SELECT NOT EXISTS (SELECT 1 FROM sync_sortie s
                       LEFT JOIN sync_lot l ON l.epoque = s.epoque AND l.numero = s.lot_numero
                       WHERE s.chargement = p_numero AND (s.lot_numero IS NULL OR l.accuse_le IS NULL))
$$;


--
-- Name: sync_chargement_annonce(integer, bigint); Type: FUNCTION; Schema: public; Owner: -
--

CREATE FUNCTION public.sync_chargement_annonce(p_numero integer, p_reprendre_apres bigint) RETURNS void
    LANGUAGE plpgsql
    AS $$
BEGIN
    UPDATE sync_chargement SET etat = 'annonce', annonce_le = coalesce(annonce_le, now()), reprendre_apres = p_reprendre_apres
    WHERE numero = p_numero AND etat IN ('pret', 'annonce');
    IF NOT FOUND THEN RAISE EXCEPTION 'Synchronisation : chargement % non prêt', p_numero; END IF;
END $$;


--
-- Name: sync_chargement_preparer(integer); Type: FUNCTION; Schema: public; Owner: -
--

CREATE FUNCTION public.sync_chargement_preparer(p_numero integer) RETURNS integer
    LANGUAGE plpgsql
    AS $$
DECLARE n int;
BEGIN
    UPDATE sync_chargement SET etat = 'constitution' WHERE numero = p_numero AND etat IN ('demande', 'constitution');
    IF NOT FOUND THEN RAISE EXCEPTION 'Synchronisation : chargement % absent ou déjà constitué', p_numero; END IF;
    PERFORM sync_code_patient(id) FROM patient;
    PERFORM sync_code_porteur(porteur_nom, porteur_piece) FROM registre_psychotrope;
    PERFORM sync_code_porteur(porteur_nom, porteur_piece) FROM journal_registre_psychotrope;
    SELECT count(*) INTO n FROM sync_tables_envoyees();
    RETURN n;
END $$;


--
-- Name: sync_chargement_pret(integer, bigint, jsonb); Type: FUNCTION; Schema: public; Owner: -
--

CREATE FUNCTION public.sync_chargement_pret(p_numero integer, p_repere bigint, p_lignes jsonb) RETURNS void
    LANGUAGE plpgsql
    AS $$
BEGIN
    UPDATE sync_chargement
    SET etat = 'pret', repere = p_repere, epoque = (SELECT epoque FROM sync_parametre WHERE id = 1),
        tables = ARRAY(SELECT jsonb_object_keys(p_lignes) ORDER BY 1), lignes = p_lignes,
        lignes_total = (SELECT coalesce(sum(v::bigint), 0) FROM jsonb_each_text(p_lignes) AS e(k, v)),
        constitue_le = now()
    WHERE numero = p_numero AND etat = 'constitution';
    IF NOT FOUND THEN RAISE EXCEPTION 'Synchronisation : chargement % n''est pas en constitution', p_numero; END IF;
END $$;


--
-- Name: sync_chargement_progression(integer); Type: FUNCTION; Schema: public; Owner: -
--

CREATE FUNCTION public.sync_chargement_progression(p_numero integer) RETURNS TABLE(inscrites bigint, en_lot bigint, accusees bigint)
    LANGUAGE sql STABLE
    AS $$
    SELECT count(*), count(*) FILTER (WHERE s.lot_numero IS NOT NULL), count(*) FILTER (WHERE l.accuse_le IS NOT NULL)
    FROM sync_sortie s LEFT JOIN sync_lot l ON l.epoque = s.epoque AND l.numero = s.lot_numero
    WHERE s.chargement = p_numero
$$;


--
-- Name: sync_chargement_terminer(integer, bigint, jsonb); Type: FUNCTION; Schema: public; Owner: -
--

CREATE FUNCTION public.sync_chargement_terminer(p_numero integer, p_absentes bigint, p_detail jsonb) RETURNS void
    LANGUAGE plpgsql
    AS $$
BEGIN
    UPDATE sync_chargement SET etat = 'termine', termine_le = now(), absentes_central = p_absentes, absentes_detail = p_detail
    WHERE numero = p_numero AND etat = 'annonce';
    IF NOT FOUND THEN RAISE EXCEPTION 'Synchronisation : chargement % non annoncé', p_numero; END IF;
END $$;


--
-- Name: sync_code_patient(bigint); Type: FUNCTION; Schema: public; Owner: -
--

CREATE FUNCTION public.sync_code_patient(p_id bigint) RETURNS text
    LANGUAGE plpgsql
    AS $$
DECLARE v_code text;
BEGIN
    IF p_id IS NULL THEN RETURN NULL; END IF;
    SELECT code INTO v_code FROM sync_pseudonyme WHERE nature = 'patient' AND cle_locale = p_id::text;
    IF FOUND THEN RETURN v_code; END IF;
    INSERT INTO sync_pseudonyme (nature, cle_locale, code)
    VALUES ('patient', p_id::text, 'P-' || encode(gen_random_bytes(12), 'hex'))
    ON CONFLICT (nature, cle_locale) DO NOTHING;
    SELECT code INTO v_code FROM sync_pseudonyme WHERE nature = 'patient' AND cle_locale = p_id::text;
    RETURN v_code;
END $$;


--
-- Name: sync_code_porteur(text, text); Type: FUNCTION; Schema: public; Owner: -
--

CREATE FUNCTION public.sync_code_porteur(p_nom text, p_piece text) RETURNS text
    LANGUAGE plpgsql
    AS $$
DECLARE v_cle text; v_code text;
BEGIN
    IF coalesce(btrim(p_nom), '') = '' AND coalesce(btrim(p_piece), '') = '' THEN RETURN NULL; END IF;
    v_cle := encode(sha256(convert_to(lower(btrim(coalesce(p_nom, ''))) || '|' || lower(btrim(coalesce(p_piece, ''))), 'UTF8')), 'hex');
    SELECT code INTO v_code FROM sync_pseudonyme WHERE nature = 'porteur' AND cle_locale = v_cle;
    IF FOUND THEN RETURN v_code; END IF;
    INSERT INTO sync_pseudonyme (nature, cle_locale, code)
    VALUES ('porteur', v_cle, 'R-' || encode(gen_random_bytes(12), 'hex'))
    ON CONFLICT (nature, cle_locale) DO NOTHING;
    SELECT code INTO v_code FROM sync_pseudonyme WHERE nature = 'porteur' AND cle_locale = v_cle;
    RETURN v_code;
END $$;


--
-- Name: sync_colonnes_hors_familles(text[]); Type: FUNCTION; Schema: public; Owner: -
--

CREATE FUNCTION public.sync_colonnes_hors_familles(p_familles text[]) RETURNS text[]
    LANGUAGE sql STABLE
    AS $$
    SELECT coalesce(array_agg(f.nom_table || '.' || f.colonne ORDER BY f.nom_table || '.' || f.colonne COLLATE "C"), '{}'::text[])
    FROM sync_famille_colonne f WHERE NOT f.famille = ANY (coalesce(p_familles, '{}'::text[]))
$$;


--
-- Name: sync_colonnes_interdites(); Type: FUNCTION; Schema: public; Owner: -
--

CREATE FUNCTION public.sync_colonnes_interdites() RETURNS text[]
    LANGUAGE sql STABLE
    AS $$ SELECT sync_colonnes_hors_familles(sync_familles_effectives()) $$;


--
-- Name: sync_constituer_lot(bigint, bigint[], integer, text, text[]); Type: FUNCTION; Schema: public; Owner: -
--

CREATE FUNCTION public.sync_constituer_lot(p_numero bigint, p_lignes bigint[], p_octets integer, p_empreinte text, p_colonnes text[]) RETURNS bigint
    LANGUAGE plpgsql
    AS $$
DECLARE p sync_parametre; n int; v_cols text[] := sync_colonnes_interdites();
BEGIN
    SELECT * INTO p FROM sync_parametre WHERE id = 1 FOR UPDATE;
    IF p.dernier_lot + 1 <> p_numero OR coalesce(array_length(p_lignes, 1), 0) = 0 THEN RETURN NULL; END IF;
    IF EXISTS (SELECT 1 FROM sync_chargement WHERE etat = 'constitution') THEN RETURN NULL; END IF;
    IF ARRAY(SELECT x FROM unnest(coalesce(p_colonnes, '{}'::text[])) x ORDER BY x COLLATE "C") IS DISTINCT FROM v_cols THEN RETURN NULL; END IF;
    INSERT INTO sync_lot (epoque, numero, version_contrat, nb_lignes, octets, empreinte, familles, colonnes_retirees)
    VALUES (p.epoque, p_numero, p.version_contrat, array_length(p_lignes, 1), p_octets, p_empreinte, sync_familles_effectives(), v_cols);
    UPDATE sync_sortie SET lot_numero = p_numero
    WHERE id = ANY (p_lignes) AND lot_numero IS NULL AND epoque = p.epoque;
    GET DIAGNOSTICS n = ROW_COUNT;
    IF n <> array_length(p_lignes, 1) THEN
        RAISE EXCEPTION 'Synchronisation : lot % incohérent (% lignes rattachées sur %)', p_numero, n, array_length(p_lignes, 1);
    END IF;
    UPDATE sync_parametre SET dernier_lot = p_numero WHERE id = 1;
    RETURN p_numero;
END $$;


--
-- Name: sync_definir_active(boolean); Type: FUNCTION; Schema: public; Owner: -
--

CREATE FUNCTION public.sync_definir_active(p_active boolean) RETURNS integer
    LANGUAGE plpgsql
    AS $$
DECLARE v_oubliees text;
BEGIN
    IF p_active THEN
        SELECT string_agg(x, ', ') INTO v_oubliees FROM sync_tables_non_classees() AS x;
        IF v_oubliees IS NOT NULL THEN
            RAISE EXCEPTION 'Synchronisation : tables non classées (sync_table) : %', v_oubliees;
        END IF;
    END IF;
    UPDATE sync_parametre SET active = p_active, modifie_le = now() WHERE id = 1;
    RETURN sync_installer_declencheurs();
END $$;


--
-- Name: sync_definir_options(text, boolean); Type: FUNCTION; Schema: public; Owner: -
--

CREATE FUNCTION public.sync_definir_options(p_textes_libres text, p_envoyer_medecins boolean) RETURNS integer
    LANGUAGE plpgsql
    AS $$
BEGIN
    IF p_textes_libres NOT IN ('filtres', 'retires') THEN
        RAISE EXCEPTION 'Synchronisation : textes_libres doit valoir filtres ou retires';
    END IF;
    UPDATE sync_parametre SET textes_libres = p_textes_libres,
                              envoyer_medecins = coalesce(p_envoyer_medecins, false),
                              modifie_le = now()
    WHERE id = 1;
    RETURN sync_installer_declencheurs();
END $$;


--
-- Name: sync_demander_chargement(text, bigint); Type: FUNCTION; Schema: public; Owner: -
--

CREATE FUNCTION public.sync_demander_chargement(p_motif text, p_utilisateur bigint) RETURNS integer
    LANGUAGE plpgsql
    AS $$
DECLARE c sync_chargement; n int;
BEGIN
    IF p_motif NOT IN ('activation', 'demande', 'reactivation', 'lots_manquants') THEN
        RAISE EXCEPTION 'Synchronisation : motif de chargement inconnu : %', p_motif;
    END IF;
    PERFORM 1 FROM sync_parametre WHERE id = 1 FOR UPDATE;
    SELECT * INTO c FROM sync_chargement WHERE etat NOT IN ('termine', 'remplace');
    IF FOUND THEN
        IF c.etat IN ('demande', 'constitution') THEN RETURN c.numero; END IF;
        IF p_motif IN ('demande', 'activation') THEN RETURN NULL; END IF;
        UPDATE sync_chargement SET etat = 'remplace' WHERE numero = c.numero;
    END IF;
    INSERT INTO sync_chargement (motif, demande_par) VALUES (p_motif, p_utilisateur) RETURNING numero INTO n;
    RETURN n;
END $$;


--
-- Name: sync_enregistrer_accuse(integer, bigint); Type: FUNCTION; Schema: public; Owner: -
--

CREATE FUNCTION public.sync_enregistrer_accuse(p_epoque integer, p_numero bigint) RETURNS bigint
    LANGUAGE plpgsql
    AS $$
DECLARE p sync_parametre;
BEGIN
    SELECT * INTO p FROM sync_parametre WHERE id = 1 FOR UPDATE;
    IF p_epoque <> p.epoque OR p_numero > p.dernier_lot THEN
        RAISE EXCEPTION 'Synchronisation : accusé % de l''époque % incohérent (dernier lot constitué %, époque %)', p_numero, p_epoque, p.dernier_lot, p.epoque;
    END IF;
    UPDATE sync_lot SET accuse_le = now() WHERE epoque = p_epoque AND numero <= p_numero AND accuse_le IS NULL;
    UPDATE sync_parametre SET dernier_accuse = greatest(dernier_accuse, p_numero), dernier_accuse_le = now(),
                              dernier_envoi_le = now(), derniere_erreur = NULL, derniere_erreur_le = NULL
    WHERE id = 1;
    RETURN greatest(p.dernier_accuse, p_numero);
END $$;


--
-- Name: sync_enregistrer_inscription(uuid); Type: FUNCTION; Schema: public; Owner: -
--

CREATE FUNCTION public.sync_enregistrer_inscription(p_officine uuid) RETURNS void
    LANGUAGE plpgsql
    AS $$
BEGIN
    IF p_officine IS DISTINCT FROM officine_courante() THEN
        RAISE EXCEPTION 'Synchronisation : le serveur central a répondu pour une autre officine';
    END IF;
    UPDATE sync_parametre SET inscrite_le = coalesce(inscrite_le, now()), derniere_erreur = NULL, derniere_erreur_le = NULL WHERE id = 1;
END $$;


--
-- Name: sync_enregistrer_lot(bigint, bigint[], integer, text); Type: FUNCTION; Schema: public; Owner: -
--

CREATE FUNCTION public.sync_enregistrer_lot(p_numero bigint, p_lignes bigint[], p_octets integer, p_empreinte text) RETURNS bigint
    LANGUAGE sql
    AS $$
    SELECT sync_enregistrer_lot(p_numero, p_lignes, p_octets, p_empreinte, NULL::text[])
$$;


--
-- Name: sync_enregistrer_lot(bigint, bigint[], integer, text, text[]); Type: FUNCTION; Schema: public; Owner: -
--

CREATE FUNCTION public.sync_enregistrer_lot(p_numero bigint, p_lignes bigint[], p_octets integer, p_empreinte text, p_familles text[]) RETURNS bigint
    LANGUAGE plpgsql
    AS $$
DECLARE p sync_parametre; n int; v_familles text[] := sync_familles_effectives();
BEGIN
    SELECT * INTO p FROM sync_parametre WHERE id = 1 FOR UPDATE;
    IF p.dernier_lot + 1 <> p_numero OR coalesce(array_length(p_lignes, 1), 0) = 0 THEN RETURN NULL; END IF;
    IF EXISTS (SELECT 1 FROM sync_chargement WHERE etat = 'constitution') THEN RETURN NULL; END IF;
    IF p_familles IS NOT NULL AND ARRAY(SELECT x FROM unnest(p_familles) x ORDER BY x COLLATE "C") IS DISTINCT FROM v_familles THEN
        RETURN NULL;
    END IF;
    INSERT INTO sync_lot (epoque, numero, version_contrat, nb_lignes, octets, empreinte, familles)
    VALUES (p.epoque, p_numero, p.version_contrat, array_length(p_lignes, 1), p_octets, p_empreinte,
            CASE WHEN p_familles IS NULL THEN NULL ELSE v_familles END);
    UPDATE sync_sortie SET lot_numero = p_numero
    WHERE id = ANY (p_lignes) AND lot_numero IS NULL AND epoque = p.epoque;
    GET DIAGNOSTICS n = ROW_COUNT;
    IF n <> array_length(p_lignes, 1) THEN
        RAISE EXCEPTION 'Synchronisation : lot % incohérent (% lignes rattachées sur %)', p_numero, n, array_length(p_lignes, 1);
    END IF;
    UPDATE sync_parametre SET dernier_lot = p_numero WHERE id = 1;
    RETURN p_numero;
END $$;


--
-- Name: sync_familles_effectives(); Type: FUNCTION; Schema: public; Owner: -
--

CREATE FUNCTION public.sync_familles_effectives() RETURNS text[]
    LANGUAGE sql STABLE
    AS $$
    SELECT coalesce((SELECT array_agg(x ORDER BY x COLLATE "C")
                     FROM reglage_central r, jsonb_array_elements_text(r.valeur -> 'familles') x
                     WHERE r.cle = 'familles_envoyees' AND EXISTS (SELECT 1 FROM sync_famille_colonne f WHERE f.famille = x)), '{}'::text[])
$$;


--
-- Name: sync_filtrer_texte(text); Type: FUNCTION; Schema: public; Owner: -
--

CREATE FUNCTION public.sync_filtrer_texte(p_texte text) RETURNS text
    LANGUAGE plpgsql
    AS $$
DECLARE
    t       text := p_texte;
    m       text;
    v_ids   bigint[];
    v_rempl text;
    v_sortie text;
    v_reste text;
    v_pos   int;
BEGIN
    IF t IS NULL OR btrim(t) = '' THEN RETURN t; END IF;
    t := regexp_replace(t, '[[:alnum:]._%+-]+@[[:alnum:].-]+\.[[:alpha:]]{2,}', '[COURRIEL]', 'g');
    t := regexp_replace(t, '(\+|00)?213[ .-]?[0-9]([ .-]?[0-9]){7,8}', '[TÉL]', 'g');
    t := regexp_replace(t, '\m0[2-7]([ .-]?[0-9]){8}\M', '[TÉL]', 'g');
    t := regexp_replace(t, '[0-9]([ .-]?[0-9]){8,}', '[N°]', 'g');
    -- Noms du personnel : masqués tant que la famille « employes » n'est pas envoyée (les plus longs d'abord, sans tenir compte de la casse)
    IF NOT ('employes' = ANY (sync_familles_effectives())) THEN
        FOR m IN SELECT btrim(u.nom_complet) FROM utilisateur u
                 WHERE length(btrim(u.nom_complet)) >= 3
                 ORDER BY length(btrim(u.nom_complet)) DESC, 1 LOOP
            v_sortie := '';
            v_reste := t;
            LOOP
                v_pos := strpos(lower(v_reste), lower(m));
                EXIT WHEN v_pos = 0;
                v_sortie := v_sortie || left(v_reste, v_pos - 1) || '[PERSONNEL]';
                v_reste := substr(v_reste, v_pos + length(m));
            END LOOP;
            t := v_sortie || v_reste;
        END LOOP;
    END IF;
    FOR m IN SELECT DISTINCT lower(x[1]) FROM regexp_matches(t, '([[:alpha:]][[:alpha:]''-]{2,})', 'g') AS x LOOP
        SELECT array_agg(DISTINCT id) INTO v_ids FROM patient WHERE lower(nom) = m OR lower(prenom) = m;
        IF v_ids IS NOT NULL THEN
            v_rempl := CASE WHEN array_length(v_ids, 1) = 1 THEN sync_code_patient(v_ids[1]) ELSE '[NOM]' END;
        ELSIF EXISTS (SELECT 1 FROM registre_psychotrope WHERE lower(porteur_nom) ~ ('\m' || m || '\M')) THEN
            v_rempl := '[NOM]';
        ELSE
            CONTINUE;
        END IF;
        t := regexp_replace(t, '\m' || m || '\M', v_rempl, 'gi');
    END LOOP;
    RETURN t;
END $$;


--
-- Name: sync_fins_chaine(); Type: FUNCTION; Schema: public; Owner: -
--

CREATE FUNCTION public.sync_fins_chaine() RETURNS jsonb
    LANGUAGE plpgsql STABLE
    AS $$
DECLARE r record; v jsonb := '[]'; l record; n int;
BEGIN
    -- Seulement les journaux envoyés au central (essai S6.5.5, 2026-11-19 : la table interne integrite_essai, jamais envoyée, faisait
    -- refuser toute la déclaration par le central, qui ne connaît que les journaux reçus)
    FOR r IN SELECT c.journal FROM chaine_integrite c
             JOIN sync_table t ON t.nom_table = c.journal AND t.sens IN ('montante', 'bidirectionnelle')
             ORDER BY c.journal LOOP
        IF to_regclass('public.' || quote_ident(r.journal)) IS NULL THEN CONTINUE; END IF;
        n := 0;
        FOR l IN EXECUTE format(
            'SELECT DISTINCT ON (cle_chaine) cle_chaine, rang, id::text AS id, encode(hash, ''hex'') AS hash FROM public.%I
             WHERE cle_chaine IS NOT NULL ORDER BY cle_chaine, rang DESC', r.journal) LOOP
            v := v || jsonb_build_array(jsonb_build_object('journal', r.journal, 'cle_chaine', l.cle_chaine, 'rang', l.rang, 'id', l.id, 'hash', l.hash));
            n := n + 1;
        END LOOP;
        IF n = 0 THEN
            v := v || jsonb_build_array(jsonb_build_object('journal', r.journal, 'cle_chaine', 'officine', 'rang', 0, 'id', NULL, 'hash', NULL));
        END IF;
    END LOOP;
    RETURN v;
END $$;


--
-- Name: sync_inscrire(); Type: FUNCTION; Schema: public; Owner: -
--

CREATE FUNCTION public.sync_inscrire() RETURNS trigger
    LANGUAGE plpgsql
    AS $$
DECLARE
    t       sync_table;
    p       sync_parametre;
    j       jsonb;
    v_id    text;
BEGIN
    SELECT * INTO p FROM sync_parametre WHERE id = 1;
    IF NOT FOUND OR NOT p.active THEN RETURN NULL; END IF;
    SELECT * INTO t FROM sync_table WHERE nom_table = TG_TABLE_NAME;
    IF NOT FOUND OR sync_sens_effectif(t, p) NOT IN ('montante', 'bidirectionnelle') THEN RETURN NULL; END IF;
    j := sync_minimiser(t, CASE WHEN TG_OP = 'DELETE' THEN to_jsonb(OLD) ELSE to_jsonb(NEW) END);
    SELECT string_agg(coalesce(j ->> k.col, ''), '|' ORDER BY k.n) INTO v_id
    FROM unnest(t.cle_colonnes) WITH ORDINALITY AS k(col, n);
    INSERT INTO sync_sortie (nom_table, id_local, operation, donnees, version_contrat, epoque)
    VALUES (TG_TABLE_NAME, v_id,
            CASE TG_OP WHEN 'INSERT' THEN 'creation' WHEN 'UPDATE' THEN 'modification' ELSE 'suppression' END,
            CASE WHEN TG_OP = 'DELETE' THEN NULL ELSE j END,
            p.version_contrat, p.epoque);
    RETURN NULL;
END $$;


--
-- Name: sync_installer_declencheurs(); Type: FUNCTION; Schema: public; Owner: -
--

CREATE FUNCTION public.sync_installer_declencheurs() RETURNS integer
    LANGUAGE plpgsql
    AS $$
DECLARE r record; n int := 0; p sync_parametre;
BEGIN
    SELECT * INTO p FROM sync_parametre WHERE id = 1;
    FOR r IN SELECT c.relname FROM pg_trigger tg
             JOIN pg_class c ON c.oid = tg.tgrelid
             JOIN pg_namespace ns ON ns.oid = c.relnamespace AND ns.nspname = 'public'
             WHERE tg.tgname = 'zz_sync_sortie' LOOP
        EXECUTE format('DROP TRIGGER zz_sync_sortie ON public.%I', r.relname);
    END LOOP;
    IF coalesce(p.active, false) THEN
        FOR r IN SELECT s.nom_table FROM sync_table s
                 JOIN pg_tables t ON t.schemaname = 'public' AND t.tablename = s.nom_table
                 WHERE sync_sens_effectif(s, p) IN ('montante', 'bidirectionnelle') ORDER BY 1 LOOP
            EXECUTE format('CREATE TRIGGER zz_sync_sortie AFTER INSERT OR UPDATE OR DELETE ON public.%I '
                           'FOR EACH ROW EXECUTE FUNCTION sync_inscrire()', r.nom_table);
            n := n + 1;
        END LOOP;
    END IF;
    RETURN n;
END $$;


--
-- Name: sync_instantane_table(integer, text, bigint); Type: FUNCTION; Schema: public; Owner: -
--

CREATE FUNCTION public.sync_instantane_table(p_chargement integer, p_table text, p_repere bigint) RETURNS bigint
    LANGUAGE plpgsql
    AS $_$
DECLARE t sync_table; p sync_parametre; v_ordre text; n bigint;
BEGIN
    SELECT * INTO p FROM sync_parametre WHERE id = 1;
    SELECT * INTO t FROM sync_table WHERE nom_table = p_table;
    IF NOT FOUND OR sync_sens_effectif(t, p) NOT IN ('montante', 'bidirectionnelle')
       OR NOT EXISTS (SELECT 1 FROM pg_tables WHERE schemaname = 'public' AND tablename = p_table) THEN
        RAISE EXCEPTION 'Synchronisation : table % non envoyée', p_table;
    END IF;
    SELECT string_agg(format('x.%I', c), ', ' ORDER BY k) INTO v_ordre FROM unnest(t.cle_colonnes) WITH ORDINALITY u(c, k);
    EXECUTE format($f$
        INSERT INTO sync_sortie (nom_table, id_local, operation, donnees, version_contrat, epoque, ordre, chargement)
        SELECT %L,
               (SELECT string_agg(coalesce(m.j ->> k.col, ''), '|' ORDER BY k.n) FROM unnest($1::text[]) WITH ORDINALITY AS k(col, n)),
               'instantane', m.j, $2, $3, $4, $5
        FROM (SELECT sync_minimiser($6, to_jsonb(x)) AS j FROM public.%I x ORDER BY %s) m$f$, p_table, p_table, v_ordre)
    USING t.cle_colonnes, p.version_contrat, p.epoque, p_repere, p_chargement, t;
    GET DIAGNOSTICS n = ROW_COUNT;
    RETURN n;
END $_$;


--
-- Name: sync_lot_garde(); Type: FUNCTION; Schema: public; Owner: -
--

CREATE FUNCTION public.sync_lot_garde() RETURNS trigger
    LANGUAGE plpgsql
    AS $$
BEGIN
    IF TG_OP = 'UPDATE' AND OLD.accuse_le IS NULL AND NEW.accuse_le IS NOT NULL
       AND (to_jsonb(NEW) - 'accuse_le') = (to_jsonb(OLD) - 'accuse_le') THEN
        RETURN NEW;
    END IF;
    IF TG_OP = 'UPDATE' AND OLD.accuse_le IS NULL AND NEW.accuse_le IS NULL AND NEW.familles IS NOT NULL
       AND (OLD.familles IS NULL OR (NEW.familles <@ OLD.familles AND NOT OLD.familles <@ NEW.familles))
       AND (to_jsonb(NEW) - ARRAY['empreinte', 'octets', 'familles', 'reforme_le'])
         = (to_jsonb(OLD) - ARRAY['empreinte', 'octets', 'familles', 'reforme_le']) THEN
        RETURN NEW;
    END IF;
    IF TG_OP = 'UPDATE' AND OLD.accuse_le IS NULL AND NEW.accuse_le IS NULL AND NEW.colonnes_retirees IS NOT NULL
       AND (OLD.colonnes_retirees IS NULL OR (OLD.colonnes_retirees <@ NEW.colonnes_retirees AND NOT NEW.colonnes_retirees <@ OLD.colonnes_retirees))
       AND (to_jsonb(NEW) - ARRAY['empreinte', 'octets', 'colonnes_retirees', 'reforme_le'])
         = (to_jsonb(OLD) - ARRAY['empreinte', 'octets', 'colonnes_retirees', 'reforme_le']) THEN
        RETURN NEW;
    END IF;
    RAISE EXCEPTION 'sync_lot : lot figé (seul l''accusé peut être enregistré)';
END $$;


--
-- Name: sync_message_central_fermer(uuid, bigint); Type: FUNCTION; Schema: public; Owner: -
--

CREATE FUNCTION public.sync_message_central_fermer(p_id uuid, p_par bigint) RETURNS boolean
    LANGUAGE plpgsql
    AS $$
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


--
-- Name: FUNCTION sync_message_central_fermer(p_id uuid, p_par bigint); Type: COMMENT; Schema: public; Owner: -
--

COMMENT ON FUNCTION public.sync_message_central_fermer(p_id uuid, p_par bigint) IS 'Ferme un message du titulaire à distance affiché sur les postes (journal des actions), script 160.';


--
-- Name: sync_table; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public.sync_table (
    nom_table text NOT NULL,
    sens text NOT NULL,
    cle_colonnes text[] DEFAULT '{id}'::text[] NOT NULL,
    colonnes_exclues text[] DEFAULT '{}'::text[] NOT NULL,
    colonnes_patient text[] DEFAULT '{}'::text[] NOT NULL,
    porteur boolean DEFAULT false NOT NULL,
    motif text,
    colonnes_texte text[] DEFAULT '{}'::text[] NOT NULL,
    CONSTRAINT sync_table_sens_check CHECK ((sens = ANY (ARRAY['montante'::text, 'descendante'::text, 'bidirectionnelle'::text, 'jamais'::text])))
);


--
-- Name: TABLE sync_table; Type: COMMENT; Schema: public; Owner: -
--

COMMENT ON TABLE public.sync_table IS 'Synchronisation : inventaire de chaque table du schéma public (sens, clé, colonnes exclues ou sous code) (script 126).';


--
-- Name: sync_minimiser(public.sync_table, jsonb); Type: FUNCTION; Schema: public; Owner: -
--

CREATE FUNCTION public.sync_minimiser(p_table public.sync_table, p_ligne jsonb) RETURNS jsonb
    LANGUAGE plpgsql
    AS $$
DECLARE j jsonb := p_ligne; c text; v_mode text;
BEGIN
    IF p_table.porteur THEN
        j := j || jsonb_build_object('porteur_code', sync_code_porteur(j ->> 'porteur_nom', j ->> 'porteur_piece'));
        j := j - ARRAY['porteur_nom', 'porteur_adresse', 'porteur_piece'];
    END IF;
    j := j - p_table.colonnes_exclues;
    FOREACH c IN ARRAY p_table.colonnes_patient LOOP
        IF jsonb_typeof(j -> c) = 'number' THEN
            j := jsonb_set(j, ARRAY[c], to_jsonb(sync_code_patient((j ->> c)::bigint)));
        END IF;
    END LOOP;
    IF cardinality(p_table.colonnes_texte) > 0 THEN
        SELECT textes_libres INTO v_mode FROM sync_parametre WHERE id = 1;
        -- Journal chaîné : jamais vidé (l'empreinte reste recalculable au serveur central), toujours filtré
        IF EXISTS (SELECT 1 FROM chaine_integrite ci WHERE ci.journal = p_table.nom_table) THEN
            v_mode := 'filtres';
        END IF;
        FOREACH c IN ARRAY p_table.colonnes_texte LOOP
            IF jsonb_typeof(j -> c) = 'string' THEN
                j := jsonb_set(j, ARRAY[c], CASE WHEN v_mode = 'retires' THEN 'null'::jsonb
                                                 ELSE to_jsonb(sync_filtrer_texte(j ->> c)) END);
            END IF;
        END LOOP;
    END IF;
    RETURN j;
END $$;


--
-- Name: sync_noter_alerte(text, timestamp with time zone); Type: FUNCTION; Schema: public; Owner: -
--

CREATE FUNCTION public.sync_noter_alerte(p_nature text, p_quand timestamp with time zone) RETURNS void
    LANGUAGE plpgsql
    AS $$
BEGIN
    IF p_nature NOT IN ('retard', 'erreur', 'certificat', 'quota') THEN
        RAISE EXCEPTION 'Synchronisation : sujet d''alerte inconnu (%)', p_nature;
    END IF;
    UPDATE sync_parametre SET
        alerte_retard_le     = CASE WHEN p_nature = 'retard'     THEN p_quand ELSE alerte_retard_le END,
        alerte_erreur_le     = CASE WHEN p_nature = 'erreur'     THEN p_quand ELSE alerte_erreur_le END,
        alerte_certificat_le = CASE WHEN p_nature = 'certificat' THEN p_quand ELSE alerte_certificat_le END,
        alerte_quota_le      = CASE WHEN p_nature = 'quota'      THEN p_quand ELSE alerte_quota_le END
    WHERE id = 1;
END $$;


--
-- Name: sync_noter_alerte_retour_arriere(timestamp with time zone); Type: FUNCTION; Schema: public; Owner: -
--

CREATE FUNCTION public.sync_noter_alerte_retour_arriere(p_quand timestamp with time zone) RETURNS void
    LANGUAGE sql
    AS $$
    -- greatest : jamais avant la détection (horloge du poste en retard sur celle de la base : sinon une alerte par minute)
    UPDATE sync_parametre SET retour_arriere_alerte_le = greatest(p_quand, retour_arriere_le) WHERE id = 1 AND retour_arriere_le IS NOT NULL
$$;


--
-- Name: sync_noter_controle_demarrage(); Type: FUNCTION; Schema: public; Owner: -
--

CREATE FUNCTION public.sync_noter_controle_demarrage() RETURNS void
    LANGUAGE sql
    AS $$
    UPDATE sync_parametre SET controle_demarrage_le = now()
     WHERE id = 1 AND demarrage_le IS NOT NULL AND controle_demarrage_le IS NULL;
$$;


--
-- Name: FUNCTION sync_noter_controle_demarrage(); Type: COMMENT; Schema: public; Owner: -
--

COMMENT ON FUNCTION public.sync_noter_controle_demarrage() IS 'Le serveur central a répondu au contrôle de démarrage : encaissement permis (script 152).';


--
-- Name: sync_noter_declaration(integer, boolean, text); Type: FUNCTION; Schema: public; Owner: -
--

CREATE FUNCTION public.sync_noter_declaration(p_nouvelle integer, p_avant_premier_lot boolean, p_resultat text) RETURNS void
    LANGUAGE sql
    AS $$
    UPDATE sync_declaration_epoque SET envoyee_le = now(), avant_premier_lot = p_avant_premier_lot, resultat = left(p_resultat, 500)
    WHERE nouvelle_epoque = p_nouvelle AND envoyee_le IS NULL
$$;


--
-- Name: sync_noter_demarrage(); Type: FUNCTION; Schema: public; Owner: -
--

CREATE FUNCTION public.sync_noter_demarrage() RETURNS boolean
    LANGUAGE plpgsql
    AS $$
DECLARE v_attendu boolean;
BEGIN
    UPDATE sync_parametre
       SET demarrage_le = CASE WHEN active AND adresse_serveur IS NOT NULL AND inscrite_le IS NOT NULL THEN now() END,
           controle_demarrage_le = NULL
     WHERE id = 1
    RETURNING demarrage_le IS NOT NULL INTO v_attendu;
    RETURN coalesce(v_attendu, false);
END $$;


--
-- Name: FUNCTION sync_noter_demarrage(); Type: COMMENT; Schema: public; Owner: -
--

COMMENT ON FUNCTION public.sync_noter_demarrage() IS 'Démarrage de l''API : contrôle du serveur central attendu avant tout encaissement (true), ou rien à attendre (script 152).';


--
-- Name: sync_noter_envoi(text); Type: FUNCTION; Schema: public; Owner: -
--

CREATE FUNCTION public.sync_noter_envoi(p_erreur text) RETURNS void
    LANGUAGE sql
    AS $$
    UPDATE sync_parametre SET dernier_envoi_le = now(), derniere_erreur = left(p_erreur, 500),
                              derniere_erreur_le = CASE WHEN p_erreur IS NULL THEN NULL ELSE now() END
    WHERE id = 1
$$;


--
-- Name: sync_noter_integrite_signalee(bigint); Type: FUNCTION; Schema: public; Owner: -
--

CREATE FUNCTION public.sync_noter_integrite_signalee(p_id bigint) RETURNS void
    LANGUAGE sql
    AS $$
    UPDATE sync_parametre SET integrite_anomalie_signalee = greatest(integrite_anomalie_signalee, coalesce(p_id, 0)) WHERE id = 1
$$;


--
-- Name: sync_noter_passage(text); Type: FUNCTION; Schema: public; Owner: -
--

CREATE FUNCTION public.sync_noter_passage(p_nature text) RETURNS void
    LANGUAGE plpgsql
    AS $$
BEGIN
    IF p_nature IS NOT NULL AND p_nature NOT IN ('autre', 'quota') THEN
        RAISE EXCEPTION 'Synchronisation : nature d''erreur inconnue (%)', p_nature;
    END IF;
    UPDATE sync_parametre
    SET erreur_depuis_le = CASE WHEN p_nature IS NULL THEN NULL ELSE coalesce(erreur_depuis_le, now()) END,
        erreur_nature = p_nature
    WHERE id = 1;
END $$;


--
-- Name: sync_noter_purge_erreur(text); Type: FUNCTION; Schema: public; Owner: -
--

CREATE FUNCTION public.sync_noter_purge_erreur(p_erreur text) RETURNS void
    LANGUAGE sql
    AS $$
    UPDATE sync_parametre SET derniere_purge_erreur = left(p_erreur, 500) WHERE id = 1
$$;


--
-- Name: sync_noter_retour_arriere(bigint); Type: FUNCTION; Schema: public; Owner: -
--

CREATE FUNCTION public.sync_noter_retour_arriere(p_dernier_central bigint) RETURNS boolean
    LANGUAGE plpgsql
    AS $$
DECLARE p sync_parametre;
BEGIN
    SELECT * INTO p FROM sync_parametre WHERE id = 1 FOR UPDATE;
    IF NOT FOUND OR p_dernier_central IS NULL OR p_dernier_central <= p.dernier_lot THEN RETURN false; END IF;
    IF p.retour_arriere_le IS NOT NULL THEN
        UPDATE sync_parametre SET retour_arriere_central = greatest(retour_arriere_central, p_dernier_central) WHERE id = 1;
        RETURN false;
    END IF;
    UPDATE sync_parametre SET retour_arriere_le = now(), retour_arriere_central = p_dernier_central, retour_arriere_sequences_le = NULL
    WHERE id = 1;
    RETURN true;
END $$;


--
-- Name: FUNCTION sync_noter_retour_arriere(p_dernier_central bigint); Type: COMMENT; Schema: public; Owner: -
--

COMMENT ON FUNCTION public.sync_noter_retour_arriere(p_dernier_central bigint) IS 'Pose la détection « base revenue en arrière » (serveur central en avance sur cette base pour l''époque en cours) ; true à la première détection (script 149).';


--
-- Name: sync_noter_retour_arriere_refus(bigint); Type: FUNCTION; Schema: public; Owner: -
--

CREATE FUNCTION public.sync_noter_retour_arriere_refus(p_dernier_central bigint) RETURNS boolean
    LANGUAGE plpgsql
    AS $$
DECLARE p sync_parametre;
BEGIN
    SELECT * INTO p FROM sync_parametre WHERE id = 1 FOR UPDATE;
    IF NOT FOUND OR p_dernier_central IS NULL OR p_dernier_central < 1 THEN RETURN false; END IF;
    IF p.retour_arriere_le IS NOT NULL THEN
        UPDATE sync_parametre SET retour_arriere_central = greatest(retour_arriere_central, p_dernier_central) WHERE id = 1;
        RETURN false;
    END IF;
    UPDATE sync_parametre SET retour_arriere_le = now(), retour_arriere_central = p_dernier_central, retour_arriere_sequences_le = NULL
    WHERE id = 1;
    RETURN true;
END $$;


--
-- Name: FUNCTION sync_noter_retour_arriere_refus(p_dernier_central bigint); Type: COMMENT; Schema: public; Owner: -
--

COMMENT ON FUNCTION public.sync_noter_retour_arriere_refus(p_dernier_central bigint) IS 'Pose la détection « base revenue en arrière » sur refus « empreinte différente » d''un lot jamais reformé déjà reçu par le central (sans comparaison à dernier_lot) ; true à la première détection (script 149).';


--
-- Name: sync_noter_version_descendante(bigint); Type: FUNCTION; Schema: public; Owner: -
--

CREATE FUNCTION public.sync_noter_version_descendante(p_version bigint) RETURNS bigint
    LANGUAGE sql
    AS $$
    UPDATE sync_parametre SET descendant_version = greatest(descendant_version, coalesce(p_version, 0)) WHERE id = 1 RETURNING descendant_version
$$;


--
-- Name: sync_nouvelle_epoque(bigint, integer, integer); Type: FUNCTION; Schema: public; Owner: -
--

CREATE FUNCTION public.sync_nouvelle_epoque(p_utilisateur bigint, p_epoque_central integer, p_epoque_attendue integer) RETURNS TABLE(epoque integer, ancienne_epoque integer, chargement integer)
    LANGUAGE plpgsql
    AS $$
#variable_conflict use_column
DECLARE p sync_parametre; n int; v_nouvelle int;
BEGIN
    IF pg_is_in_recovery() THEN
        RAISE EXCEPTION 'Synchronisation : nouvelle époque impossible sur une base en lecture seule';
    END IF;
    SELECT * INTO p FROM sync_parametre WHERE id = 1 FOR UPDATE;
    IF NOT FOUND THEN RAISE EXCEPTION 'Synchronisation : réglage absent (script 126)'; END IF;
    IF p.retour_arriere_le IS NULL THEN
        RAISE EXCEPTION 'Synchronisation : aucun retour en arrière détecté, nouvelle époque refusée'
            USING ERRCODE = 'check_violation';
    END IF;
    IF p_epoque_attendue IS NULL OR p_epoque_attendue <> p.epoque THEN
        RAISE EXCEPTION 'Synchronisation : l''époque a changé (attendue %, actuelle %), nouvelle époque refusée', p_epoque_attendue, p.epoque
            USING ERRCODE = 'check_violation';
    END IF;
    IF p_epoque_central IS NOT NULL AND (p_epoque_central < 0 OR p_epoque_central >= 2147483647) THEN
        RAISE EXCEPTION 'Synchronisation : époque du serveur central invalide (%)', p_epoque_central USING ERRCODE = 'check_violation';
    END IF;
    -- Jamais une époque déjà connue du central (deux restaurations successives, 2e serveur) : au-delà des deux
    v_nouvelle := greatest(p.epoque, coalesce(p_epoque_central, 0)) + 1;
    -- Script 151 : déclaration à envoyer au serveur central AVANT tout lot de la nouvelle époque (ancienne époque, lots, dates, fins de chaîne)
    INSERT INTO sync_declaration_epoque (nouvelle_epoque, ancienne_epoque, dernier_lot_local, dernier_lot_central, detecte_le, titulaire_id, fins_chaine)
    VALUES (v_nouvelle, p.epoque, p.dernier_lot, p.retour_arriere_central, p.retour_arriere_le, p_utilisateur, sync_fins_chaine())
    ON CONFLICT (nouvelle_epoque) DO NOTHING;
    -- Chargement en cours de l'ancienne époque : son instantané (lignes de l'ancienne époque) ne partira jamais
    UPDATE sync_chargement c SET etat = 'remplace' WHERE c.etat NOT IN ('termine', 'remplace');
    UPDATE sync_parametre SET epoque = v_nouvelle, dernier_lot = 0, dernier_accuse = 0,
                              purge_epoque = NULL, purge_dernier_lot = 0,
                              retour_arriere_le = NULL, retour_arriere_central = NULL, retour_arriere_sequences_le = NULL,
                              derniere_erreur = NULL, derniere_erreur_le = NULL, modifie_le = now()
    WHERE id = 1;
    n := sync_demander_chargement('demande', p_utilisateur);
    epoque := v_nouvelle; ancienne_epoque := p.epoque; chargement := n;
    RETURN NEXT;
END $$;


--
-- Name: FUNCTION sync_nouvelle_epoque(p_utilisateur bigint, p_epoque_central integer, p_epoque_attendue integer); Type: COMMENT; Schema: public; Owner: -
--

COMMENT ON FUNCTION public.sync_nouvelle_epoque(p_utilisateur bigint, p_epoque_central integer, p_epoque_attendue integer) IS 'Titulaire : nouvelle époque (au-delà de l''époque locale et de la plus grande connue du central) après un retour en arrière détecté, époque attendue contrôlée, puis chargement complet demandé (script 149).';


--
-- Name: sync_ordre_noter(uuid, text, text, bigint); Type: FUNCTION; Schema: public; Owner: -
--

CREATE FUNCTION public.sync_ordre_noter(p_id uuid, p_statut text, p_motif text, p_par bigint) RETURNS void
    LANGUAGE plpgsql
    AS $$
DECLARE o sync_ordre_recu;
BEGIN
    IF p_statut NOT IN ('a_confirmer', 'applique', 'refuse', 'expire', 'annule') THEN
        RAISE EXCEPTION 'Ordre du serveur central : état inconnu (%)', p_statut;
    END IF;
    SELECT * INTO o FROM sync_ordre_recu WHERE id = p_id FOR UPDATE;
    IF NOT FOUND THEN RAISE EXCEPTION 'Ordre du serveur central inconnu : %', p_id; END IF;
    IF o.statut IN ('applique', 'refuse', 'expire', 'annule') THEN
        RAISE EXCEPTION 'Ordre du serveur central déjà traité (%)', o.statut;
    END IF;
    UPDATE sync_ordre_recu SET statut = p_statut, motif = left(p_motif, 500),
           traite_le = CASE WHEN p_statut = 'a_confirmer' THEN traite_le ELSE now() END,
           traite_par = CASE WHEN p_statut = 'a_confirmer' THEN traite_par ELSE p_par END
    WHERE id = p_id;
    INSERT INTO sync_ordre_resultat AS r (ordre_id, statut, motif, traite_le) VALUES (p_id, p_statut, left(p_motif, 500), now())
    ON CONFLICT (ordre_id) DO UPDATE SET statut = EXCLUDED.statut, motif = EXCLUDED.motif, traite_le = EXCLUDED.traite_le;
    INSERT INTO journal_audit (utilisateur_id, action, table_cible, details)
    VALUES (p_par, 'ordre_central_' || p_statut, 'sync_ordre_recu',
            jsonb_build_object('ordre', p_id, 'type', o.type, 'cible', o.cible, 'auteur_central', o.auteur, 'motif', left(p_motif, 500),
                               'attendu', o.attendu, 'donnees', o.donnees - 'mot_de_passe'));
END $$;


--
-- Name: sync_parametre_garde_officine(); Type: FUNCTION; Schema: public; Owner: -
--

CREATE FUNCTION public.sync_parametre_garde_officine() RETURNS trigger
    LANGUAGE plpgsql
    AS $$
BEGIN
    IF OLD.officine_id IS NOT NULL AND NEW.officine_id IS DISTINCT FROM OLD.officine_id THEN
        RAISE EXCEPTION 'Synchronisation : le code de l''officine ne se modifie pas';
    END IF;
    RETURN NEW;
END $$;


--
-- Name: sync_purger(integer, integer); Type: FUNCTION; Schema: public; Owner: -
--

CREATE FUNCTION public.sync_purger(p_delai_jours integer, p_max_lots integer DEFAULT 20) RETURNS TABLE(lots integer, lignes bigint, octets bigint, jusqu_au_lot bigint)
    LANGUAGE plpgsql
    AS $$
DECLARE
    p          sync_parametre;
    v_depart   bigint;
    v_dernier  bigint;
    v_lot      record;
    v_n        bigint;
    v_lots     int := 0;
    v_lignes   bigint := 0;
    v_octets   bigint := 0;
BEGIN
    IF p_delai_jours IS NULL OR p_delai_jours < 1 THEN
        RAISE EXCEPTION 'Synchronisation : délai de conservation invalide (1 jour au moins)';
    END IF;
    IF p_max_lots IS NULL OR p_max_lots < 1 OR p_max_lots > 1000 THEN
        RAISE EXCEPTION 'Synchronisation : nombre de lots à purger invalide (1 à 1000)';
    END IF;
    IF pg_is_in_recovery() THEN
        RAISE EXCEPTION 'Synchronisation : purge impossible sur une base en lecture seule';
    END IF;
    PERFORM pg_advisory_xact_lock(hashtext('sync_purger'));
    SELECT * INTO p FROM sync_parametre WHERE id = 1;
    v_depart := CASE WHEN p.purge_epoque = p.epoque THEN p.purge_dernier_lot ELSE 0 END;
    v_dernier := v_depart;
    FOR v_lot IN
        SELECT l.numero AS numero, l.octets AS taille FROM sync_lot l
        WHERE l.epoque = p.epoque AND l.numero > v_depart AND l.accuse_le IS NOT NULL
          AND l.accuse_le < now() - make_interval(days => p_delai_jours)
        ORDER BY l.numero LIMIT p_max_lots
    LOOP
        EXIT WHEN v_lot.numero <> v_dernier + 1;
        EXIT WHEN EXISTS (SELECT 1 FROM sync_sortie s JOIN sync_chargement c ON c.numero = s.chargement
                          WHERE s.epoque = p.epoque AND s.lot_numero = v_lot.numero AND c.etat IN ('demande', 'constitution', 'pret', 'annonce'));
        DELETE FROM sync_sortie s WHERE s.epoque = p.epoque AND s.lot_numero = v_lot.numero;   -- garde : lot accusé seulement
        GET DIAGNOSTICS v_n = ROW_COUNT;
        v_lots := v_lots + 1;
        v_lignes := v_lignes + v_n;
        v_octets := v_octets + v_lot.taille;
        v_dernier := v_lot.numero;
    END LOOP;
    IF v_lots > 0 THEN
        UPDATE sync_parametre SET purge_epoque = p.epoque, purge_dernier_lot = v_dernier, derniere_purge_le = now(),
                                  derniere_purge_lignes = v_lignes, purge_lignes_total = purge_lignes_total + v_lignes,
                                  derniere_purge_erreur = NULL
        WHERE id = 1;
    END IF;
    RETURN QUERY SELECT v_lots, v_lignes, v_octets, CASE WHEN v_lots > 0 THEN v_dernier ELSE 0::bigint END;
END $$;


--
-- Name: FUNCTION sync_purger(p_delai_jours integer, p_max_lots integer); Type: COMMENT; Schema: public; Owner: -
--

COMMENT ON FUNCTION public.sync_purger(p_delai_jours integer, p_max_lots integer) IS 'Purge de sync_sortie : lignes des lots accusés depuis plus de p_delai_jours jours, jamais un lot non accusé ni un chargement en cours (script 140).';


--
-- Name: sync_recevoir_descendant(text, text, bigint, jsonb, bigint); Type: FUNCTION; Schema: public; Owner: -
--

CREATE FUNCTION public.sync_recevoir_descendant(p_table text, p_cle text, p_version bigint, p_donnees jsonb, p_version_max bigint) RETURNS text
    LANGUAGE plpgsql
    AS $_$
DECLARE
    p        sync_parametre;
    v_motif  text;
    v_id     uuid;
    v_statut text;
    o        sync_ordre_recu;
BEGIN
    IF p_table IS DISTINCT FROM 'ordre_central' THEN
        RETURN sync_recevoir_descendant_reglages(p_table, p_cle, p_version, p_donnees, p_version_max);
    END IF;
    SELECT * INTO p FROM sync_parametre WHERE id = 1;
    v_motif := CASE
        WHEN p_version IS NULL OR p_version < 1 OR p_donnees IS NULL OR jsonb_typeof(p_donnees) <> 'object' THEN 'élément mal formé'
        WHEN p_version > coalesce(p_version_max, 0) THEN 'version au-delà de la version maximale annoncée'
        WHEN p_version > p.descendant_version + p.descendant_saut_max THEN 'saut de version anormal'
        WHEN length(p_donnees::text) > 65536 THEN 'données trop volumineuses (plus de 64 Kio)'
        WHEN p_donnees ->> 'format' IS DISTINCT FROM 'ordre-v1' THEN 'format d''ordre non pris en charge'
        WHEN p_cle IS NULL OR p_cle !~ '^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$'
             OR p_donnees ->> 'id' IS DISTINCT FROM p_cle THEN 'identifiant d''ordre invalide'
        WHEN coalesce(p_donnees ->> 'type', '') !~ '^[a-z_]{1,40}$' THEN 'type d''ordre invalide'
        WHEN coalesce(p_donnees ->> 'auteur', '') = '' OR length(p_donnees ->> 'auteur') > 100 THEN 'auteur de l''ordre absent'
        WHEN jsonb_typeof(p_donnees -> 'attendu') IS DISTINCT FROM 'object' OR jsonb_typeof(p_donnees -> 'donnees') IS DISTINCT FROM 'object'
             THEN 'contenu de l''ordre invalide'
        WHEN coalesce(p_donnees ->> 'statut', '') NOT IN ('en_attente', 'a_confirmer', 'applique', 'refuse', 'expire', 'annule')
             THEN 'état de l''ordre invalide'
        ELSE NULL END;
    IF v_motif IS NULL THEN
        BEGIN
            PERFORM (p_donnees ->> 'cree_le')::timestamptz, (p_donnees ->> 'expire_le')::timestamptz;
        EXCEPTION WHEN others THEN v_motif := 'dates de l''ordre invalides';
        END;
    END IF;
    IF v_motif IS NOT NULL THEN
        INSERT INTO sync_journal_descendant (nom_table, cle, version, statut, motif)
        SELECT 'ordre_central', left(coalesce(p_cle, '?'), 200), p_version, 'refuse', v_motif
        WHERE NOT EXISTS (SELECT 1 FROM sync_journal_descendant j WHERE j.statut = 'refuse' AND j.nom_table = 'ordre_central'
                          AND j.cle = left(coalesce(p_cle, '?'), 200) AND j.version IS NOT DISTINCT FROM p_version);
        RETURN 'refuse';
    END IF;

    v_id := p_cle::uuid;
    v_statut := p_donnees ->> 'statut';
    SELECT * INTO o FROM sync_ordre_recu WHERE id = v_id FOR UPDATE;
    IF NOT FOUND THEN
        INSERT INTO sync_ordre_recu (id, type, cible, attendu, donnees, auteur, cree_le, expire_le, version, statut)
        VALUES (v_id, p_donnees ->> 'type', left(p_donnees ->> 'cible', 100), p_donnees -> 'attendu', p_donnees -> 'donnees',
                p_donnees ->> 'auteur', (p_donnees ->> 'cree_le')::timestamptz, (p_donnees ->> 'expire_le')::timestamptz, p_version,
                'a_appliquer');
        -- Annulé ou expiré au central avant d'arriver : jamais appliqué
        IF v_statut IN ('annule', 'expire') THEN
            PERFORM sync_ordre_noter(v_id, v_statut, CASE v_statut WHEN 'annule' THEN 'annulé au serveur central' ELSE 'expiré au serveur central' END, NULL);
        END IF;
    ELSIF o.version >= p_version THEN
        RETURN 'ancien';
    ELSE
        UPDATE sync_ordre_recu SET version = p_version WHERE id = v_id;
        -- Seule une annulation ou une expiration décidée au central change un ordre pas encore traité ; le reste est ignoré
        IF v_statut IN ('annule', 'expire') AND o.statut IN ('a_appliquer', 'a_confirmer') THEN
            PERFORM sync_ordre_noter(v_id, v_statut, CASE v_statut WHEN 'annule' THEN 'annulé au serveur central' ELSE 'expiré au serveur central' END, NULL);
        END IF;
    END IF;
    INSERT INTO sync_journal_descendant (nom_table, cle, version, statut, avant, apres)
    VALUES ('ordre_central', p_cle, p_version, 'applique', NULL, jsonb_build_object('type', p_donnees ->> 'type', 'statut', v_statut));
    RETURN 'applique';
END $_$;


--
-- Name: sync_recevoir_descendant_reglages(text, text, bigint, jsonb, bigint); Type: FUNCTION; Schema: public; Owner: -
--

CREATE FUNCTION public.sync_recevoir_descendant_reglages(p_table text, p_cle text, p_version bigint, p_donnees jsonb, p_version_max bigint) RETURNS text
    LANGUAGE plpgsql
    AS $_$
DECLARE
    p        sync_parametre;
    v_avant  jsonb;
    n        int;
    v_table  text;
    v_mode   text;
    v_motif  text;
    v_modele bigint;
BEGIN
    SELECT * INTO p FROM sync_parametre WHERE id = 1;
    IF p_version IS NULL OR p_version < 1 OR p_donnees IS NULL OR jsonb_typeof(p_donnees) <> 'object'
       OR p_version > coalesce(p_version_max, 0) OR p_version > p.descendant_version + p.descendant_saut_max
       OR length(p_donnees::text) > 65536 THEN
        -- Une ligne par élément refusé (renvoyé à chaque passage tant qu'il n'est pas acquitté : pas de doublon)
        INSERT INTO sync_journal_descendant (nom_table, cle, version, statut, motif)
        SELECT left(coalesce(p_table, '?'), 63), left(coalesce(p_cle, '?'), 200), p_version, 'refuse',
               CASE WHEN p_version > coalesce(p_version_max, 0) THEN 'version au-delà de la version maximale annoncée'
                    WHEN p_version > p.descendant_version + p.descendant_saut_max THEN 'saut de version anormal'
                    WHEN length(p_donnees::text) > 65536 THEN 'données trop volumineuses (plus de 64 Kio)'
                    ELSE 'élément mal formé' END
        WHERE NOT EXISTS (SELECT 1 FROM sync_journal_descendant j WHERE j.statut = 'refuse' AND j.nom_table = left(coalesce(p_table, '?'), 63)
                          AND j.cle = left(coalesce(p_cle, '?'), 200) AND j.version IS NOT DISTINCT FROM p_version);
        RETURN 'refuse';
    END IF;
    IF p_table IS DISTINCT FROM 'reglage_central' THEN
        INSERT INTO sync_journal_descendant (nom_table, cle, version, statut, motif)
        SELECT left(coalesce(p_table, '?'), 63), left(coalesce(p_cle, '?'), 200), p_version, 'table_inconnue', 'table descendante non prévue ici : ignorée'
        WHERE NOT EXISTS (SELECT 1 FROM sync_journal_descendant j WHERE j.statut = 'table_inconnue' AND j.nom_table = left(coalesce(p_table, '?'), 63)
                          AND j.cle = left(coalesce(p_cle, '?'), 200) AND j.version IS NOT DISTINCT FROM p_version);
        RETURN 'table_inconnue';
    END IF;
    IF p_cle IS NULL OR p_cle !~ '^[a-z_]{1,63}$' THEN
        -- Clé inutilisable (la table reglage_central la refuserait) : même traitement qu'un élément mal formé
        INSERT INTO sync_journal_descendant (nom_table, cle, version, statut, motif)
        SELECT left(p_table, 63), left(coalesce(p_cle, '?'), 200), p_version, 'refuse', 'élément mal formé'
        WHERE NOT EXISTS (SELECT 1 FROM sync_journal_descendant j WHERE j.statut = 'refuse' AND j.nom_table = left(p_table, 63)
                          AND j.cle = left(coalesce(p_cle, '?'), 200) AND j.version IS NOT DISTINCT FROM p_version);
        RETURN 'refuse';
    END IF;
    IF p_cle LIKE 'mutu\_%' THEN
        v_table := substr(p_cle, 6);
        IF NOT v_table = ANY (reglage_tables_mutualisables()) THEN
            -- Élément de mutualisation d'une table qui n'est pas mutualisable : journalisé, ignoré, ACQUITTÉ (ne bloque pas les suivants)
            INSERT INTO sync_journal_descendant (nom_table, cle, version, statut, motif)
            SELECT p_table, left(p_cle, 200), p_version, 'invalide', 'table non mutualisable : élément ignoré'
            WHERE NOT EXISTS (SELECT 1 FROM sync_journal_descendant j WHERE j.statut = 'invalide' AND j.nom_table = p_table
                              AND j.cle = left(p_cle, 200) AND j.version IS NOT DISTINCT FROM p_version);
            RETURN 'invalide';
        END IF;
    END IF;
    SELECT valeur INTO v_avant FROM reglage_central WHERE cle = p_cle;
    INSERT INTO reglage_central AS r (cle, version, valeur) VALUES (p_cle, p_version, p_donnees)
    ON CONFLICT (cle) DO UPDATE SET version = EXCLUDED.version, valeur = EXCLUDED.valeur, recu_le = now()
    WHERE r.version < EXCLUDED.version;
    GET DIAGNOSTICS n = ROW_COUNT;
    IF n = 0 THEN RETURN 'ancien'; END IF;

    IF v_table IS NOT NULL THEN
        -- État de la table : modèle reçu rangé (appliqué ensuite par l'application) ; élément mal formé : table « rejetée », rien d'autre ne change
        v_mode := p_donnees ->> 'mode';
        v_motif := CASE
            WHEN p_donnees ->> 'format' IS DISTINCT FROM 'mutu-v1' THEN 'format non pris en charge'
            WHEN v_mode IS NULL OR v_mode NOT IN ('local', 'propose', 'impose') THEN 'mode inconnu'
            WHEN jsonb_typeof(p_donnees -> 'version_modele') IS DISTINCT FROM 'number' OR (p_donnees ->> 'version_modele') !~ '^[0-9]{1,15}$'
                 THEN 'version du modèle absente ou invalide'
            WHEN (p_donnees ->> 'version_modele')::bigint < 1 THEN 'version du modèle absente ou invalide'
            WHEN v_mode <> 'local' AND jsonb_typeof(p_donnees -> 'valeur') IS DISTINCT FROM 'object' THEN 'valeur du modèle absente'
            WHEN p_donnees ? 'ajouts_locaux' AND jsonb_typeof(p_donnees -> 'ajouts_locaux') IS DISTINCT FROM 'boolean' THEN 'indicateur des ajouts locaux invalide'
            ELSE NULL END;
        v_modele := CASE WHEN jsonb_typeof(p_donnees -> 'version_modele') = 'number' AND (p_donnees ->> 'version_modele') ~ '^[0-9]{1,15}$'
                              AND (p_donnees ->> 'version_modele')::bigint >= 1
                         THEN (p_donnees ->> 'version_modele')::bigint END;
        INSERT INTO reglage_mutualise (nom_table) VALUES (v_table) ON CONFLICT (nom_table) DO NOTHING;
        IF v_motif IS NULL THEN
            UPDATE reglage_mutualise SET mode_recu = v_mode, version_modele_recue = v_modele, recu_le = now(), modele = p_donnees
            WHERE nom_table = v_table;
        ELSE
            UPDATE reglage_mutualise SET etat = 'rejete', motif_rejet = left('Élément du serveur central mal formé : ' || v_motif, 500),
                                         version_modele_rejetee = v_modele
            WHERE nom_table = v_table;
        END IF;
    END IF;

    INSERT INTO sync_journal_descendant (nom_table, cle, version, statut, avant, apres)
    VALUES (p_table, p_cle, p_version, 'applique',
            CASE WHEN p_cle = 'familles_envoyees' THEN v_avant -> 'familles'
                 WHEN v_table IS NOT NULL AND v_avant IS NOT NULL
                      THEN jsonb_build_object('version_modele', v_avant -> 'version_modele', 'mode', v_avant -> 'mode') END,
            CASE WHEN p_cle = 'familles_envoyees' THEN p_donnees -> 'familles'
                 WHEN v_table IS NOT NULL
                      THEN jsonb_build_object('version_modele', p_donnees -> 'version_modele', 'mode', p_donnees -> 'mode') END);
    RETURN 'applique';
END $_$;


--
-- Name: sync_reformer_lot(integer, bigint, text, integer, text[]); Type: FUNCTION; Schema: public; Owner: -
--

CREATE FUNCTION public.sync_reformer_lot(p_epoque integer, p_numero bigint, p_empreinte text, p_octets integer, p_familles text[]) RETURNS void
    LANGUAGE plpgsql
    AS $$
BEGIN
    UPDATE sync_lot SET empreinte = p_empreinte, octets = p_octets,
                        familles = ARRAY(SELECT x FROM unnest(p_familles) x ORDER BY x COLLATE "C"), reforme_le = now()
    WHERE epoque = p_epoque AND numero = p_numero AND accuse_le IS NULL;
    IF NOT FOUND THEN RAISE EXCEPTION 'Synchronisation : lot %/% absent ou déjà accusé', p_epoque, p_numero; END IF;
END $$;


--
-- Name: sync_reformer_lot_colonnes(integer, bigint, text, integer, text[]); Type: FUNCTION; Schema: public; Owner: -
--

CREATE FUNCTION public.sync_reformer_lot_colonnes(p_epoque integer, p_numero bigint, p_empreinte text, p_octets integer, p_colonnes text[]) RETURNS void
    LANGUAGE plpgsql
    AS $$
BEGIN
    UPDATE sync_lot SET empreinte = p_empreinte, octets = p_octets, reforme_le = now(),
                        colonnes_retirees = ARRAY(SELECT DISTINCT x COLLATE "C" FROM unnest(p_colonnes) x ORDER BY 1)
    WHERE epoque = p_epoque AND numero = p_numero AND accuse_le IS NULL;
    IF NOT FOUND THEN RAISE EXCEPTION 'Synchronisation : lot %/% absent ou déjà accusé', p_epoque, p_numero; END IF;
END $$;


--
-- Name: sync_regler_adresse(text); Type: FUNCTION; Schema: public; Owner: -
--

CREATE FUNCTION public.sync_regler_adresse(p_adresse text) RETURNS void
    LANGUAGE sql
    AS $$
    UPDATE sync_parametre SET adresse_serveur = nullif(btrim(p_adresse), ''), modifie_le = now() WHERE id = 1
$$;


--
-- Name: sync_regler_controle_demarrage(integer); Type: FUNCTION; Schema: public; Owner: -
--

CREATE FUNCTION public.sync_regler_controle_demarrage(p_delai_s integer) RETURNS void
    LANGUAGE plpgsql
    AS $$
BEGIN
    IF p_delai_s IS NULL OR p_delai_s NOT BETWEEN 0 AND 600 THEN
        RAISE EXCEPTION 'Attente au démarrage : entre 0 et 600 secondes.';
    END IF;
    UPDATE sync_parametre SET controle_demarrage_delai_s = p_delai_s WHERE id = 1;
END $$;


--
-- Name: sync_regler_supervision(integer, integer, integer, boolean, integer); Type: FUNCTION; Schema: public; Owner: -
--

CREATE FUNCTION public.sync_regler_supervision(p_bandeau_minutes integer, p_retard_heures integer, p_erreur_minutes integer, p_purge_active boolean, p_purge_jours integer) RETURNS void
    LANGUAGE sql
    AS $$
    UPDATE sync_parametre SET bandeau_minutes = p_bandeau_minutes, alerte_retard_heures = p_retard_heures,
                              alerte_erreur_minutes = p_erreur_minutes, purge_active = p_purge_active, purge_jours = p_purge_jours,
                              modifie_le = now()
    WHERE id = 1
$$;


--
-- Name: sync_retirer_colonnes(text, jsonb, text[]); Type: FUNCTION; Schema: public; Owner: -
--

CREATE FUNCTION public.sync_retirer_colonnes(p_table text, p_donnees jsonb, p_colonnes text[]) RETURNS jsonb
    LANGUAGE sql IMMUTABLE
    AS $$
    SELECT CASE WHEN p_donnees IS NULL OR p_colonnes IS NULL THEN p_donnees
                ELSE p_donnees - ARRAY(SELECT split_part(c, '.', 2) FROM unnest(p_colonnes) c WHERE split_part(c, '.', 1) = p_table) END
$$;


--
-- Name: sync_retirer_familles(text, jsonb, text[]); Type: FUNCTION; Schema: public; Owner: -
--

CREATE FUNCTION public.sync_retirer_familles(p_table text, p_donnees jsonb, p_familles text[]) RETURNS jsonb
    LANGUAGE sql STABLE
    AS $$
    SELECT CASE WHEN p_donnees IS NULL OR p_familles IS NULL THEN p_donnees
                ELSE p_donnees - coalesce((SELECT array_agg(f.colonne) FROM sync_famille_colonne f
                                           WHERE f.nom_table = p_table AND NOT f.famille = ANY (p_familles)), '{}'::text[])
           END
$$;


--
-- Name: sync_parametre; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public.sync_parametre (
    id integer DEFAULT 1 NOT NULL,
    active boolean DEFAULT false NOT NULL,
    officine_id uuid DEFAULT gen_random_uuid(),
    epoque integer DEFAULT 1 NOT NULL,
    version_contrat text DEFAULT 'sync-v1'::text NOT NULL,
    lot_max_lignes integer DEFAULT 500 NOT NULL,
    lot_max_octets integer DEFAULT 1048576 NOT NULL,
    dernier_lot bigint DEFAULT 0 NOT NULL,
    dernier_accuse bigint DEFAULT 0 NOT NULL,
    modifie_le timestamp with time zone DEFAULT now() NOT NULL,
    textes_libres text DEFAULT 'filtres'::text NOT NULL,
    envoyer_medecins boolean DEFAULT false NOT NULL,
    adresse_serveur text,
    inscrite_le timestamp with time zone,
    dernier_envoi_le timestamp with time zone,
    dernier_accuse_le timestamp with time zone,
    derniere_erreur text,
    derniere_erreur_le timestamp with time zone,
    descendant_version bigint DEFAULT 0 NOT NULL,
    descendant_saut_max bigint DEFAULT 1000 NOT NULL,
    bandeau_minutes integer DEFAULT 60 NOT NULL,
    alerte_retard_heures integer DEFAULT 24 NOT NULL,
    alerte_erreur_minutes integer DEFAULT 60 NOT NULL,
    purge_active boolean DEFAULT true NOT NULL,
    purge_jours integer DEFAULT 30 NOT NULL,
    purge_epoque integer,
    purge_dernier_lot bigint DEFAULT 0 NOT NULL,
    derniere_purge_le timestamp with time zone,
    derniere_purge_lignes bigint DEFAULT 0 NOT NULL,
    purge_lignes_total bigint DEFAULT 0 NOT NULL,
    derniere_purge_erreur text,
    erreur_depuis_le timestamp with time zone,
    erreur_nature text,
    alerte_retard_le timestamp with time zone,
    alerte_erreur_le timestamp with time zone,
    alerte_certificat_le timestamp with time zone,
    alerte_quota_le timestamp with time zone,
    integrite_anomalie_signalee bigint DEFAULT 0 NOT NULL,
    retour_arriere_le timestamp with time zone,
    retour_arriere_central bigint,
    retour_arriere_sequences_le timestamp with time zone,
    retour_arriere_alerte_le timestamp with time zone,
    demarrage_le timestamp with time zone,
    controle_demarrage_le timestamp with time zone,
    controle_demarrage_delai_s integer DEFAULT 120 NOT NULL,
    CONSTRAINT sync_parametre_adresse_serveur_check CHECK (((adresse_serveur IS NULL) OR (adresse_serveur ~ '^https://[A-Za-z0-9.-]+(:[0-9]{1,5})?/?$'::text))),
    CONSTRAINT sync_parametre_check CHECK (((dernier_accuse >= 0) AND (dernier_accuse <= dernier_lot))),
    CONSTRAINT sync_parametre_controle_demarrage_delai_check CHECK (((controle_demarrage_delai_s >= 0) AND (controle_demarrage_delai_s <= 600))),
    CONSTRAINT sync_parametre_dernier_lot_check CHECK ((dernier_lot >= 0)),
    CONSTRAINT sync_parametre_descendant_saut_max_check CHECK ((descendant_saut_max >= 1)),
    CONSTRAINT sync_parametre_epoque_check CHECK ((epoque >= 1)),
    CONSTRAINT sync_parametre_erreur_check CHECK ((((erreur_nature IS NULL) = (erreur_depuis_le IS NULL)) AND ((erreur_nature IS NULL) OR (erreur_nature = ANY (ARRAY['autre'::text, 'quota'::text]))))),
    CONSTRAINT sync_parametre_id_check CHECK ((id = 1)),
    CONSTRAINT sync_parametre_lot_max_lignes_check CHECK (((lot_max_lignes >= 1) AND (lot_max_lignes <= 10000))),
    CONSTRAINT sync_parametre_lot_max_octets_check CHECK (((lot_max_octets >= 1024) AND (lot_max_octets <= 16777216))),
    CONSTRAINT sync_parametre_purge_check CHECK (((purge_dernier_lot >= 0) AND (purge_dernier_lot <= dernier_accuse) AND (derniere_purge_lignes >= 0) AND (purge_lignes_total >= 0))),
    CONSTRAINT sync_parametre_retour_arriere_check CHECK ((((retour_arriere_le IS NULL) = (retour_arriere_central IS NULL)) AND ((retour_arriere_central IS NULL) OR (retour_arriere_central >= 1)) AND ((retour_arriere_sequences_le IS NULL) OR (retour_arriere_le IS NOT NULL)))),
    CONSTRAINT sync_parametre_supervision_check CHECK ((((bandeau_minutes >= 1) AND (bandeau_minutes <= 10080)) AND ((alerte_retard_heures >= 1) AND (alerte_retard_heures <= 720)) AND ((alerte_erreur_minutes >= 1) AND (alerte_erreur_minutes <= 10080)) AND ((purge_jours >= 1) AND (purge_jours <= 365)))),
    CONSTRAINT sync_parametre_textes_libres_check CHECK ((textes_libres = ANY (ARRAY['filtres'::text, 'retires'::text]))),
    CONSTRAINT sync_parametre_version_contrat_check CHECK ((version_contrat ~ '^sync-v[0-9]+$'::text))
);


--
-- Name: TABLE sync_parametre; Type: COMMENT; Schema: public; Owner: -
--

COMMENT ON TABLE public.sync_parametre IS 'Synchronisation : réglage (désactivée par défaut), officine, époque, contrat, bornes des lots (script 126).';


--
-- Name: COLUMN sync_parametre.descendant_version; Type: COMMENT; Schema: public; Owner: -
--

COMMENT ON COLUMN public.sync_parametre.descendant_version IS 'Plus grande version descendante appliquée (acquittement redonné au serveur central) (script 131).';


--
-- Name: COLUMN sync_parametre.integrite_anomalie_signalee; Type: COMMENT; Schema: public; Owner: -
--

COMMENT ON COLUMN public.sync_parametre.integrite_anomalie_signalee IS 'N° de la dernière anomalie d''intégrité du serveur central signalée au titulaire (script 144).';


--
-- Name: COLUMN sync_parametre.retour_arriere_le; Type: COMMENT; Schema: public; Owner: -
--

COMMENT ON COLUMN public.sync_parametre.retour_arriere_le IS 'Base revenue en arrière détectée (le serveur central a reçu plus de lots que cette base n''en a formé) : envoi suspendu jusqu''à une nouvelle époque (script 149).';


--
-- Name: COLUMN sync_parametre.retour_arriere_central; Type: COMMENT; Schema: public; Owner: -
--

COMMENT ON COLUMN public.sync_parametre.retour_arriere_central IS 'Dernier lot de l''époque en cours reçu par le serveur central, vu à la détection (script 149).';


--
-- Name: COLUMN sync_parametre.retour_arriere_sequences_le; Type: COMMENT; Schema: public; Owner: -
--

COMMENT ON COLUMN public.sync_parametre.retour_arriere_sequences_le IS 'Séquences des tables envoyées avancées au-delà des n° connus du serveur central pour la détection en cours (script 149).';


--
-- Name: COLUMN sync_parametre.retour_arriere_alerte_le; Type: COMMENT; Schema: public; Owner: -
--

COMMENT ON COLUMN public.sync_parametre.retour_arriere_alerte_le IS 'Dernière alerte « base revenue en arrière » envoyée au titulaire : une par détection (script 149).';


--
-- Name: COLUMN sync_parametre.demarrage_le; Type: COMMENT; Schema: public; Owner: -
--

COMMENT ON COLUMN public.sync_parametre.demarrage_le IS 'Démarrage de l''API (synchronisation active et serveur activé) : encaissement suspendu jusqu''au contrôle du serveur central, au plus controle_demarrage_delai_s (script 152).';


--
-- Name: COLUMN sync_parametre.controle_demarrage_le; Type: COMMENT; Schema: public; Owner: -
--

COMMENT ON COLUMN public.sync_parametre.controle_demarrage_le IS 'Contrôle de démarrage fait : le serveur central a répondu (pas de retour en arrière, ou compteur des tickets avancé) (script 152).';


--
-- Name: COLUMN sync_parametre.controle_demarrage_delai_s; Type: COMMENT; Schema: public; Owner: -
--

COMMENT ON COLUMN public.sync_parametre.controle_demarrage_delai_s IS 'Attente maximale de l''encaissement au démarrage, en secondes (0 : aucune attente ; 120 par défaut, choix du titulaire le 2026-11-19) (script 152).';


--
-- Name: sync_sens_effectif(public.sync_table, public.sync_parametre); Type: FUNCTION; Schema: public; Owner: -
--

CREATE FUNCTION public.sync_sens_effectif(p_table public.sync_table, p_param public.sync_parametre) RETURNS text
    LANGUAGE sql STABLE
    AS $$
    SELECT CASE WHEN p_table.nom_table = 'medecin' AND p_param.envoyer_medecins THEN 'montante' ELSE p_table.sens END
$$;


--
-- Name: sync_sortie_garde(); Type: FUNCTION; Schema: public; Owner: -
--

CREATE FUNCTION public.sync_sortie_garde() RETURNS trigger
    LANGUAGE plpgsql
    AS $$
BEGIN
    IF TG_OP = 'UPDATE' THEN
        IF OLD.lot_numero IS NULL AND NEW.lot_numero IS NOT NULL
           AND (to_jsonb(NEW) - 'lot_numero') = (to_jsonb(OLD) - 'lot_numero') THEN
            RETURN NEW;
        END IF;
        RAISE EXCEPTION 'sync_sortie : ajout seul (seul le rattachement à un lot est permis)';
    ELSIF TG_OP = 'DELETE' THEN
        IF EXISTS (SELECT 1 FROM sync_lot l WHERE l.epoque = OLD.epoque AND l.numero = OLD.lot_numero AND l.accuse_le IS NOT NULL) THEN
            RETURN OLD;
        END IF;
        RAISE EXCEPTION 'sync_sortie : suppression permise seulement après accusé du lot';
    END IF;
    RAISE EXCEPTION 'sync_sortie : vidage interdit';
END $$;


--
-- Name: sync_tables_envoyees(); Type: FUNCTION; Schema: public; Owner: -
--

CREATE FUNCTION public.sync_tables_envoyees() RETURNS SETOF text
    LANGUAGE sql STABLE
    AS $$
    SELECT s.nom_table FROM sync_table s
    JOIN pg_tables t ON t.schemaname = 'public' AND t.tablename = s.nom_table
    JOIN sync_parametre p ON p.id = 1
    WHERE sync_sens_effectif(s, p) IN ('montante', 'bidirectionnelle')
    ORDER BY 1
$$;


--
-- Name: sync_tables_non_classees(); Type: FUNCTION; Schema: public; Owner: -
--

CREATE FUNCTION public.sync_tables_non_classees() RETURNS SETOF text
    LANGUAGE sql STABLE
    AS $$
    SELECT t.tablename::text FROM pg_tables t
    WHERE t.schemaname = 'public' AND NOT EXISTS (SELECT 1 FROM sync_table s WHERE s.nom_table = t.tablename)
    ORDER BY 1
$$;


--
-- Name: tracer_credit_patient(); Type: FUNCTION; Schema: public; Owner: -
--

CREATE FUNCTION public.tracer_credit_patient() RETURNS trigger
    LANGUAGE plpgsql
    AS $$
DECLARE
    v_delta numeric(12,2);
    v_type  text := NULLIF(current_setting('pharma.credit_type', true), '');
BEGIN
    v_delta := NEW.solde_credit - CASE WHEN TG_OP = 'INSERT' THEN 0 ELSE OLD.solde_credit END;
    IF v_delta = 0 THEN
        RETURN NEW;
    END IF;
    INSERT INTO credit_mouvement (patient_id, type, montant, solde_apres, vente_id, utilisateur_id, poste, reference)
    VALUES (NEW.id,
            -- Sans contexte : patient créé avec une dette (reprise de l'ancien logiciel), sinon correction hors logiciel
            COALESCE(v_type, CASE WHEN TG_OP = 'INSERT' THEN 'reprise' ELSE 'correction' END),
            v_delta, NEW.solde_credit,
            NULLIF(current_setting('pharma.credit_vente', true), '')::bigint,
            NULLIF(current_setting('pharma.credit_utilisateur', true), '')::bigint,
            NULLIF(current_setting('pharma.credit_poste', true), ''),
            NULLIF(current_setting('pharma.credit_reference', true), ''));
    -- Le contexte ne vaut que pour un mouvement
    PERFORM credit_contexte(NULL, NULL, NULL, NULL, NULL);
    RETURN NEW;
END $$;


--
-- Name: tracer_dette_fournisseur(); Type: FUNCTION; Schema: public; Owner: -
--

CREATE FUNCTION public.tracer_dette_fournisseur() RETURNS trigger
    LANGUAGE plpgsql
    AS $$
DECLARE
    v_delta numeric(12,2);
BEGIN
    v_delta := NEW.solde - CASE WHEN TG_OP = 'INSERT' THEN 0 ELSE OLD.solde END;
    IF v_delta = 0 THEN
        RETURN NEW;
    END IF;
    INSERT INTO fournisseur_mouvement (fournisseur_id, type, montant, solde_apres, utilisateur_id, reference)
    VALUES (NEW.id,
            -- Sans contexte : grossiste créé avec une dette (reprise), sinon correction hors logiciel
            COALESCE(NULLIF(current_setting('pharma.dette_type', true), ''), CASE WHEN TG_OP = 'INSERT' THEN 'reprise' ELSE 'correction' END),
            v_delta, NEW.solde,
            NULLIF(current_setting('pharma.dette_utilisateur', true), '')::bigint,
            NULLIF(current_setting('pharma.dette_reference', true), ''));
    PERFORM dette_fournisseur_contexte(NULL, NULL, NULL);
    RETURN NEW;
END $$;


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
    -- Prestation (script 105) : aucune boîte
    IF lv.lot_id IS NULL OR b.lot_id <> lv.lot_id THEN
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
-- Name: ventes_lot(bigint, text); Type: FUNCTION; Schema: public; Owner: -
--

CREATE FUNCTION public.ventes_lot(p_produit_id bigint, p_numero_lot text) RETURNS TABLE(o_patient text, o_telephone text, o_date_vente timestamp with time zone, o_ticket text, o_boites bigint)
    LANGUAGE sql STABLE
    AS $$
    -- Aussi les ventes d'avant l'étiquetage par boîte ; une vente dont toutes les boîtes du lot ont été rendues est masquée
    SELECT COALESCE(NULLIF(btrim(COALESCE(pt.nom, '') || ' ' || COALESCE(pt.prenom, '')), ''),
                    'Vente anonyme'),
           pt.telephone, v.date_vente, v.numero, SUM(greatest(lv.quantite - COALESCE(rd.rendues, 0), 0))::bigint
    FROM ligne_vente lv
    JOIN vente v     ON v.id = lv.vente_id
    JOIN lot l       ON l.id = lv.lot_id
    LEFT JOIN patient pt ON pt.id = v.patient_id
    LEFT JOIN LATERAL (SELECT sum(rl.quantite) AS rendues
                       FROM retour_client_ligne rl WHERE rl.ligne_vente_id = lv.id) rd ON true
    WHERE l.produit_id = p_produit_id AND l.numero_lot = p_numero_lot
      AND v.statut = 'validee'
    GROUP BY pt.nom, pt.prenom, pt.telephone, v.date_vente, v.numero
    HAVING SUM(greatest(lv.quantite - COALESCE(rd.rendues, 0), 0)) > 0
    ORDER BY v.date_vente DESC;
$$;


--
-- Name: FUNCTION ventes_lot(p_produit_id bigint, p_numero_lot text); Type: COMMENT; Schema: public; Owner: -
--

COMMENT ON FUNCTION public.ventes_lot(p_produit_id bigint, p_numero_lot text) IS 'Ventes validées d''un lot (patients à contacter), boîtes rendues déduites : rappel et consultation d''un lot rappelé';


--
-- Name: verifier_caisse_ouverte(); Type: FUNCTION; Schema: public; Owner: -
--

CREATE FUNCTION public.verifier_caisse_ouverte() RETURNS trigger
    LANGUAGE plpgsql
    AS $$
DECLARE
    v_fermee timestamptz;
BEGIN
    IF NEW.session_caisse_id IS NULL THEN
        RETURN NEW;
    END IF;
    -- Seulement l'argent du tiroir : part du tiroir d'un règlement grossiste, versement au coffre pendant la journée.
    -- Champs propres à une table : lus seulement pour cette table (IF imbriqués), sinon l'expression échoue sur les autres
    IF TG_TABLE_NAME = 'reglement_fournisseur' THEN
        IF NEW.part_tiroir = 0 THEN
            RETURN NEW;
        END IF;
    ELSIF TG_TABLE_NAME = 'mouvement_tresorerie' THEN
        IF NOT NEW.pendant_session THEN
            RETURN NEW;
        END IF;
    END IF;
    SELECT fermee_le INTO v_fermee FROM session_caisse WHERE id = NEW.session_caisse_id FOR SHARE;
    IF v_fermee IS NOT NULL THEN
        RAISE EXCEPTION 'Cette caisse vient d''être clôturée : ouvrez la caisse du poste et recommencez.'
            USING ERRCODE = 'P0001';
    END IF;
    RETURN NEW;
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
    l record;
BEGIN
    -- Prestation (script 105) : pas de lot
    IF NEW.lot_id IS NULL THEN
        RETURN NEW;
    END IF;
    SELECT date_peremption, bloque, motif_blocage INTO l FROM lot WHERE id = NEW.lot_id;
    IF l.bloque THEN
        RAISE EXCEPTION 'Lot bloqué (%), vente interdite', COALESCE(l.motif_blocage, 'sans motif');
    END IF;
    IF l.date_peremption <= current_date THEN
        RAISE EXCEPTION 'Lot périmé depuis le %, vente interdite', to_char(l.date_peremption, 'DD/MM/YYYY');
    END IF;
    RETURN NEW;
END;
$$;


--
-- Name: verifier_regularisation_rendue_avant(); Type: FUNCTION; Schema: public; Owner: -
--

CREATE FUNCTION public.verifier_regularisation_rendue_avant() RETURNS trigger
    LANGUAGE plpgsql
    AS $$
DECLARE v_rendu numeric;
BEGIN
    SELECT COALESCE(sum(rl.montant_organisme), 0) INTO v_rendu
    FROM retour_client_ligne rl JOIN ligne_vente l ON l.id = rl.ligne_vente_id
    WHERE l.vente_id = NEW.vente_id;
    IF NEW.part_organisme_rendue_avant IS DISTINCT FROM v_rendu THEN
        RAISE EXCEPTION 'Un retour vient d''être enregistré sur cette vente : rescannez le volet.' USING ERRCODE = 'P0001';
    END IF;
    RETURN NEW;
END;
$$;


--
-- Name: FUNCTION verifier_regularisation_rendue_avant(); Type: COMMENT; Schema: public; Owner: -
--

COMMENT ON FUNCTION public.verifier_regularisation_rendue_avant() IS 'Refuse une régularisation dont la part rendue avant le rattachement ne correspond pas aux retours de la vente (script 157)';


--
-- Name: verifier_retour_avoir_retenu(); Type: FUNCTION; Schema: public; Owner: -
--

CREATE FUNCTION public.verifier_retour_avoir_retenu() RETURNS trigger
    LANGUAGE plpgsql
    AS $$
DECLARE
    v_vente   bigint;
    v_montant numeric;
    v_retenu  numeric;
BEGIN
    IF NEW.montant_avoir_retenu = 0 OR (TG_OP = 'UPDATE' AND NEW.montant_avoir_retenu <= OLD.montant_avoir_retenu) THEN
        RETURN NEW;
    END IF;
    SELECT l.vente_id INTO v_vente FROM ligne_vente l WHERE l.id = NEW.ligne_vente_id;
    SELECT g.montant_patient INTO v_montant FROM regularisation_part_organisme g
    WHERE g.vente_id = v_vente AND g.statut = 'active' AND g.sort = 'avoir_patient';
    IF v_montant IS NULL THEN
        RAISE EXCEPTION 'Cette vente n''a pas de régularisation « rendu au patient » : rien à retenir à ce titre.' USING ERRCODE = 'P0001';
    END IF;
    SELECT COALESCE(sum(rl.montant_avoir_retenu), 0) INTO v_retenu
    FROM retour_client_ligne rl JOIN ligne_vente l ON l.id = rl.ligne_vente_id
    WHERE l.vente_id = v_vente AND rl.id IS DISTINCT FROM NEW.id;
    IF v_retenu + NEW.montant_avoir_retenu > v_montant THEN
        RAISE EXCEPTION 'La régularisation de part organisme de cette vente vient de changer : rechargez la vente.' USING ERRCODE = 'P0001';
    END IF;
    RETURN NEW;
END;
$$;


--
-- Name: FUNCTION verifier_retour_avoir_retenu(); Type: COMMENT; Schema: public; Owner: -
--

COMMENT ON FUNCTION public.verifier_retour_avoir_retenu() IS 'Refuse une retenue sans régularisation « rendu au patient » active, ou au-delà de son montant (script 159)';


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
-- Name: verifier_retour_dette_avoir(); Type: FUNCTION; Schema: public; Owner: -
--

CREATE FUNCTION public.verifier_retour_dette_avoir() RETURNS trigger
    LANGUAGE plpgsql
    AS $$
BEGIN
    IF NEW.dette_avoir_retenu = 0 OR (TG_OP = 'UPDATE' AND NEW.dette_avoir_retenu <= OLD.dette_avoir_retenu) THEN
        RETURN NEW;
    END IF;
    IF NOT EXISTS (SELECT 1 FROM vente v JOIN regularisation_part_organisme g ON g.vente_id = v.id AND g.statut = 'active' AND g.sort = 'avoir_patient'
                   WHERE v.id = NEW.vente_id AND v.patient_id IS NOT NULL) THEN
        RAISE EXCEPTION 'Vente sans patient ou sans régularisation « rendu au patient » : aucune dette ne peut être ajoutée par ce retour.' USING ERRCODE = 'P0001';
    END IF;
    RETURN NEW;
END;
$$;


--
-- Name: FUNCTION verifier_retour_dette_avoir(); Type: COMMENT; Schema: public; Owner: -
--

COMMENT ON FUNCTION public.verifier_retour_dette_avoir() IS 'Refuse une dette ajoutée par un retour sans patient ou sans régularisation « rendu au patient » active (script 159)';


--
-- Name: verifier_retour_regularisation(); Type: FUNCTION; Schema: public; Owner: -
--

CREATE FUNCTION public.verifier_retour_regularisation() RETURNS trigger
    LANGUAGE plpgsql
    AS $$
DECLARE
    v_vente   bigint;
    v_montant numeric;
    v_rendu   numeric;
BEGIN
    IF NEW.montant_regularisation = 0 OR (TG_OP = 'UPDATE' AND NEW.montant_regularisation <= OLD.montant_regularisation) THEN
        RETURN NEW;
    END IF;
    SELECT l.vente_id INTO v_vente FROM ligne_vente l WHERE l.id = NEW.ligne_vente_id;
    SELECT g.montant_patient INTO v_montant FROM regularisation_part_organisme g
    WHERE g.vente_id = v_vente AND g.statut = 'active' AND g.sort = 'charge_patient';
    IF v_montant IS NULL THEN
        RAISE EXCEPTION 'Cette vente n''a pas de régularisation à la charge du patient : rien à lui rendre à ce titre.' USING ERRCODE = 'P0001';
    END IF;
    SELECT COALESCE(sum(rl.montant_regularisation), 0) INTO v_rendu
    FROM retour_client_ligne rl JOIN ligne_vente l ON l.id = rl.ligne_vente_id
    WHERE l.vente_id = v_vente AND rl.id IS DISTINCT FROM NEW.id;
    IF v_rendu + NEW.montant_regularisation > v_montant THEN
        RAISE EXCEPTION 'La régularisation de part organisme de cette vente vient de changer : rechargez la vente.' USING ERRCODE = 'P0001';
    END IF;
    RETURN NEW;
END;
$$;


--
-- Name: FUNCTION verifier_retour_regularisation(); Type: COMMENT; Schema: public; Owner: -
--

COMMENT ON FUNCTION public.verifier_retour_regularisation() IS 'Refuse une part de régularisation rendue sans régularisation « à la charge du patient » active, ou au-delà de son montant (script 158)';


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

    -- Boîtes scannées : lignes produit seulement (une prestation n'a pas de boîte, script 105)
    IF prm.boite_obligatoire AND EXISTS (
        SELECT 1 FROM ligne_vente lv
        WHERE lv.vente_id = NEW.id AND lv.lot_id IS NOT NULL
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
    patient_id bigint,
    ligne integer
);


--
-- Name: COLUMN client_src.ligne; Type: COMMENT; Schema: import; Owner: -
--

COMMENT ON COLUMN import.client_src.ligne IS 'Rang de la ligne dans la table de l''ancien logiciel (1 = première lue) ; NULL pour une extraction antérieure à 092';


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
-- Name: doublon_patient; Type: TABLE; Schema: import; Owner: -
--

CREATE TABLE import.doublon_patient (
    id bigint NOT NULL,
    session_id bigint NOT NULL,
    ref_source text NOT NULL,
    ligne integer,
    nom text NOT NULL,
    prenom text,
    telephone text,
    num_assure text NOT NULL,
    solde_credit numeric(12,2) DEFAULT 0 NOT NULL,
    patient_existant_id bigint NOT NULL,
    origine text NOT NULL,
    statut text DEFAULT 'a_valider'::text NOT NULL,
    dette text,
    patient_cree_id bigint,
    decide_par bigint,
    decide_le timestamp with time zone,
    cree_le timestamp with time zone DEFAULT now() NOT NULL,
    CONSTRAINT ck_doublon_patient_decision CHECK (((statut = 'a_valider'::text) = (decide_le IS NULL))),
    CONSTRAINT ck_doublon_patient_dette CHECK (((dette IS NULL) OR (dette = ANY (ARRAY['rattachee'::text, 'abandonnee'::text])))),
    CONSTRAINT ck_doublon_patient_origine CHECK ((origine = ANY (ARRAY['fichier'::text, 'base'::text]))),
    CONSTRAINT ck_doublon_patient_statut CHECK ((statut = ANY (ARRAY['a_valider'::text, 'garde_existant'::text, 'remplace'::text, 'ayant_droit'::text])))
);


--
-- Name: TABLE doublon_patient; Type: COMMENT; Schema: import; Owner: -
--

COMMENT ON TABLE import.doublon_patient IS 'Patients de la reprise non créés (n° d''assuré déjà pris), à valider par le titulaire ou un ayant droit (092)';


--
-- Name: COLUMN doublon_patient.origine; Type: COMMENT; Schema: import; Owner: -
--

COMMENT ON COLUMN import.doublon_patient.origine IS 'fichier : n° déjà repris d''une ligne précédente du même export ; base : n° d''un patient déjà en base';


--
-- Name: COLUMN doublon_patient.dette; Type: COMMENT; Schema: import; Owner: -
--

COMMENT ON COLUMN import.doublon_patient.dette IS 'Dette reprise du patient ignoré : rattachee (ajoutée à la fiche gardée ou créée) ou abandonnee ; NULL sans dette ou avant décision';


--
-- Name: doublon_patient_id_seq; Type: SEQUENCE; Schema: import; Owner: -
--

ALTER TABLE import.doublon_patient ALTER COLUMN id ADD GENERATED ALWAYS AS IDENTITY (
    SEQUENCE NAME import.doublon_patient_id_seq
    START WITH 1
    INCREMENT BY 1
    NO MINVALUE
    NO MAXVALUE
    CACHE 1
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
-- Name: historique_chifa_src; Type: TABLE; Schema: import; Owner: -
--

CREATE TABLE import.historique_chifa_src (
    session_id bigint NOT NULL,
    num_facture text NOT NULL,
    date_facture text NOT NULL,
    num_assure text,
    nom_assure text,
    montant_total text,
    part_chifa text,
    part_assure text,
    organisme text
);


--
-- Name: historique_mdn_src; Type: TABLE; Schema: import; Owner: -
--

CREATE TABLE import.historique_mdn_src (
    session_id bigint NOT NULL,
    num_facture text NOT NULL,
    date_facture text NOT NULL,
    num_affilie text,
    nom_affilie text,
    montant_total text,
    part_mdn text,
    part_assure text
);


--
-- Name: TABLE historique_mdn_src; Type: COMMENT; Schema: import; Owner: -
--

COMMENT ON TABLE import.historique_mdn_src IS 'Transit des factures MDN (conventions militaires) de l''ancien logiciel (121) ; format à vérifier, réglable par le profil';


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
    prix_achat text,
    classe text
);


--
-- Name: profil_reprise; Type: TABLE; Schema: import; Owner: -
--

CREATE TABLE import.profil_reprise (
    id bigint NOT NULL,
    logiciel text NOT NULL,
    version text DEFAULT ''::text NOT NULL,
    statut text DEFAULT 'a_completer'::text NOT NULL,
    remarque text,
    correspondances jsonb DEFAULT '[]'::jsonb NOT NULL,
    separateur_decimal text DEFAULT 'auto'::text NOT NULL,
    format_date text,
    codes_classe jsonb DEFAULT '{}'::jsonb NOT NULL,
    cree_le timestamp with time zone DEFAULT now() NOT NULL,
    modifie_le timestamp with time zone DEFAULT now() NOT NULL,
    modifie_par text,
    CONSTRAINT profil_reprise_codes_objet CHECK ((jsonb_typeof(codes_classe) = 'object'::text)),
    CONSTRAINT profil_reprise_correspondances_tableau CHECK ((jsonb_typeof(correspondances) = 'array'::text)),
    CONSTRAINT profil_reprise_separateur_decimal_check CHECK ((separateur_decimal = ANY (ARRAY['auto'::text, 'virgule'::text, 'point'::text]))),
    CONSTRAINT profil_reprise_statut_check CHECK ((statut = ANY (ARRAY['a_completer'::text, 'verifie'::text])))
);


--
-- Name: TABLE profil_reprise; Type: COMMENT; Schema: import; Owner: -
--

COMMENT ON TABLE import.profil_reprise IS 'Profil de reprise d''un logiciel précédent (114) : correspondance de colonnes et transformations ; a_completer = modèle vide, format réel inconnu';


--
-- Name: profil_reprise_id_seq; Type: SEQUENCE; Schema: import; Owner: -
--

ALTER TABLE import.profil_reprise ALTER COLUMN id ADD GENERATED ALWAYS AS IDENTITY (
    SEQUENCE NAME import.profil_reprise_id_seq
    START WITH 1
    INCREMENT BY 1
    NO MINVALUE
    NO MAXVALUE
    CACHE 1
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
    lance_par text,
    charge_le timestamp with time zone,
    charge_par text,
    CONSTRAINT session_statut_check CHECK ((statut = ANY (ARRAY['en_cours'::text, 'charge'::text, 'annule'::text])))
);


--
-- Name: COLUMN session.lance_par; Type: COMMENT; Schema: import; Owner: -
--

COMMENT ON COLUMN import.session.lance_par IS 'Qui a lancé l''extraction (ex. super admin et son identifiant)';


--
-- Name: COLUMN session.charge_le; Type: COMMENT; Schema: import; Owner: -
--

COMMENT ON COLUMN import.session.charge_le IS 'Date du chargement définitif dans la base';


--
-- Name: COLUMN session.charge_par; Type: COMMENT; Schema: import; Owner: -
--

COMMENT ON COLUMN import.session.charge_par IS 'Qui a lancé le chargement définitif';


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
    fournisseur_ref text,
    prix_achat text
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
    officine_id uuid DEFAULT public.officine_courante() NOT NULL,
    CONSTRAINT alerte_niveau_check CHECK ((niveau = ANY (ARRAY['info'::text, 'attention'::text, 'urgent'::text]))),
    CONSTRAINT alerte_statut_check CHECK ((statut = ANY (ARRAY['nouvelle'::text, 'vue'::text, 'reportee'::text, 'traitee'::text, 'resolue'::text]))),
    CONSTRAINT alerte_type_check CHECK ((type = ANY (ARRAY['peremption'::text, 'perime'::text, 'risque_ecoulement'::text, 'rupture'::text, 'stock_critique'::text, 'dormant'::text, 'surstock'::text, 'lot_delaisse'::text])))
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
-- Name: alerte_non_envoyee; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public.alerte_non_envoyee (
    id bigint NOT NULL,
    type text NOT NULL,
    objet text NOT NULL,
    raison text NOT NULL,
    cree_le timestamp with time zone DEFAULT now() NOT NULL,
    vue_le timestamp with time zone,
    vue_par bigint,
    CONSTRAINT alerte_non_envoyee_check CHECK (((vue_le IS NULL) = (vue_par IS NULL)))
);


--
-- Name: TABLE alerte_non_envoyee; Type: COMMENT; Schema: public; Owner: -
--

COMMENT ON TABLE public.alerte_non_envoyee IS 'Alerte au titulaire qui n''a pas pu partir (mise en file impossible, aucun destinataire, envoi abandonné) : bandeau de l''Accueil du titulaire jusqu''à ce qu''il la marque vue';


--
-- Name: alerte_non_envoyee_id_seq; Type: SEQUENCE; Schema: public; Owner: -
--

ALTER TABLE public.alerte_non_envoyee ALTER COLUMN id ADD GENERATED ALWAYS AS IDENTITY (
    SEQUENCE NAME public.alerte_non_envoyee_id_seq
    START WITH 1
    INCREMENT BY 1
    NO MINVALUE
    NO MAXVALUE
    CACHE 1
);


--
-- Name: alerte_surveillance; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public.alerte_surveillance (
    id bigint NOT NULL,
    type text NOT NULL,
    niveau text NOT NULL,
    utilisateur_id bigint,
    jour date NOT NULL,
    cle text DEFAULT ''::text NOT NULL,
    message text NOT NULL,
    valeur numeric(12,2),
    statut text DEFAULT 'nouvelle'::text NOT NULL,
    action_prise text,
    cree_le timestamp with time zone DEFAULT now() NOT NULL,
    mise_a_jour_le timestamp with time zone DEFAULT now() NOT NULL,
    traitee_par bigint,
    traitee_le timestamp with time zone,
    officine_id uuid DEFAULT public.officine_courante() NOT NULL,
    CONSTRAINT alerte_surveillance_niveau_check CHECK ((niveau = ANY (ARRAY['attention'::text, 'urgent'::text]))),
    CONSTRAINT alerte_surveillance_statut_check CHECK ((statut = ANY (ARRAY['nouvelle'::text, 'traitee'::text]))),
    CONSTRAINT ck_alerte_surveillance_type CHECK ((type = ANY (ARRAY['lignes_retirees'::text, 'tiroir'::text, 'attentes_abandonnees'::text, 'hors_horaires'::text, 'remises'::text, 'remises_patient'::text, 'ajustements'::text, 'casses'::text, 'echantillons'::text, 'boites_fantomes'::text, 'sensibles_en_retard'::text, 'ecart_reception'::text, 'reception_meme_personne'::text, 'retour_sans_avoir'::text, 'avoir_incomplet'::text, 'credit_nouveau_patient'::text, 'credits_employe'::text, 'credit_sans_remboursement'::text, 'autorisations_valideur'::text, 'autorisations_employe'::text, 'postes_simultanes'::text, 'baisses_prix'::text, 'sorties_tiroir'::text])))
);


--
-- Name: TABLE alerte_surveillance; Type: COMMENT; Schema: public; Owner: -
--

COMMENT ON TABLE public.alerte_surveillance IS 'Anomalies de caisse par employé et par jour, visibles du titulaire seul ; closes par lui, jamais supprimées';


--
-- Name: alerte_surveillance_id_seq; Type: SEQUENCE; Schema: public; Owner: -
--

ALTER TABLE public.alerte_surveillance ALTER COLUMN id ADD GENERATED ALWAYS AS IDENTITY (
    SEQUENCE NAME public.alerte_surveillance_id_seq
    START WITH 1
    INCREMENT BY 1
    NO MINVALUE
    NO MAXVALUE
    CACHE 1
);


--
-- Name: annulation_registre; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public.annulation_registre (
    id bigint NOT NULL,
    registre_id bigint NOT NULL,
    motif text NOT NULL,
    utilisateur_id bigint NOT NULL,
    cree_le timestamp with time zone DEFAULT now() NOT NULL,
    officine_id uuid DEFAULT public.officine_courante() NOT NULL,
    CONSTRAINT ck_annulation_registre_motif CHECK (((char_length(btrim(motif)) >= 5) AND (char_length(btrim(motif)) <= 500)))
);


--
-- Name: annulation_registre_id_seq; Type: SEQUENCE; Schema: public; Owner: -
--

ALTER TABLE public.annulation_registre ALTER COLUMN id ADD GENERATED ALWAYS AS IDENTITY (
    SEQUENCE NAME public.annulation_registre_id_seq
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
    officine_id uuid DEFAULT public.officine_courante() NOT NULL,
    CONSTRAINT annulation_vente_check CHECK (((NOT ancienne) OR (valide_par IS NOT NULL))),
    CONSTRAINT annulation_vente_mode_check CHECK ((mode = ANY (ARRAY['especes'::text, 'carte'::text, 'cheque'::text, 'credit'::text]))),
    CONSTRAINT annulation_vente_montant_rembourse_check CHECK ((montant_rembourse >= (0)::numeric)),
    CONSTRAINT annulation_vente_motif_check CHECK ((length(btrim(motif)) >= 3))
);


--
-- Name: TABLE annulation_vente; Type: COMMENT; Schema: public; Owner: -
--

COMMENT ON TABLE public.annulation_vente IS 'Ventes annulées : motif, employé, validateur (vente ancienne), argent rendu et caisse concernée';


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
-- Name: avoir_mouvement; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public.avoir_mouvement (
    id bigint NOT NULL,
    patient_id bigint NOT NULL,
    sens text NOT NULL,
    montant numeric(12,2) NOT NULL,
    origine text NOT NULL,
    vente_id bigint,
    paiement_id bigint,
    mouvement_tiroir_id bigint,
    motif text,
    utilisateur_id bigint NOT NULL,
    cree_le timestamp with time zone DEFAULT now() NOT NULL,
    officine_id uuid DEFAULT public.officine_courante() NOT NULL,
    CONSTRAINT ck_avoir_mouvement_liens CHECK ((((origine <> ALL (ARRAY['annulation_vente'::text, 'trop_paye_regularisation'::text, 'retour_client'::text])) OR (vente_id IS NOT NULL)) AND ((origine <> 'paiement'::text) OR (paiement_id IS NOT NULL)) AND ((origine <> 'remboursement'::text) OR (mouvement_tiroir_id IS NOT NULL)))),
    CONSTRAINT ck_avoir_mouvement_montant CHECK ((montant > (0)::numeric)),
    CONSTRAINT ck_avoir_mouvement_origine CHECK ((origine = ANY (ARRAY['annulation_vente'::text, 'trop_paye_regularisation'::text, 'saisie'::text, 'paiement'::text, 'remboursement'::text, 'retour_client'::text]))),
    CONSTRAINT ck_avoir_mouvement_sens CHECK ((sens = ANY (ARRAY['credit'::text, 'debit'::text]))),
    CONSTRAINT ck_avoir_mouvement_sens_origine CHECK ((((origine = ANY (ARRAY['annulation_vente'::text, 'trop_paye_regularisation'::text, 'saisie'::text, 'retour_client'::text])) AND (sens = 'credit'::text)) OR ((origine = ANY (ARRAY['paiement'::text, 'remboursement'::text])) AND (sens = 'debit'::text))))
);


--
-- Name: TABLE avoir_mouvement; Type: COMMENT; Schema: public; Owner: -
--

COMMENT ON TABLE public.avoir_mouvement IS 'Avoir du patient (argent dû par la pharmacie), séparé de sa dette ; solde : v_avoir_patient';


--
-- Name: avoir_mouvement_id_seq; Type: SEQUENCE; Schema: public; Owner: -
--

ALTER TABLE public.avoir_mouvement ALTER COLUMN id ADD GENERATED ALWAYS AS IDENTITY (
    SEQUENCE NAME public.avoir_mouvement_id_seq
    START WITH 1
    INCREMENT BY 1
    NO MINVALUE
    NO MAXVALUE
    CACHE 1
);


--
-- Name: bascule_decision; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public.bascule_decision (
    id bigint NOT NULL,
    decidee_le timestamp with time zone NOT NULL,
    cas character varying(30) NOT NULL,
    timeline_avant integer,
    timeline_apres integer,
    lsn_final character varying(40),
    derniere_donnee_recue timestamp with time zone,
    rapports jsonb DEFAULT '[]'::jsonb NOT NULL,
    sondes jsonb DEFAULT '{}'::jsonb NOT NULL,
    motif character varying(500) NOT NULL,
    cree_le timestamp with time zone DEFAULT now() NOT NULL,
    CONSTRAINT ck_bascule_decision_cas CHECK (((cas)::text = ANY (ARRAY['promotion'::text, 'retrait_promotion'::text])))
);


--
-- Name: TABLE bascule_decision; Type: COMMENT; Schema: public; Owner: -
--

COMMENT ON TABLE public.bascule_decision IS 'Promotions automatiques du 2e serveur (cas, timelines, LSN, dernière transaction reçue, rapports des postes et sondes ; noms de postes seulement) ; ajout seul ; jamais envoyée au central (script 166).';


--
-- Name: bascule_decision_id_seq; Type: SEQUENCE; Schema: public; Owner: -
--

ALTER TABLE public.bascule_decision ALTER COLUMN id ADD GENERATED ALWAYS AS IDENTITY (
    SEQUENCE NAME public.bascule_decision_id_seq
    START WITH 1
    INCREMENT BY 1
    NO MINVALUE
    NO MAXVALUE
    CACHE 1
);


--
-- Name: bascule_promotion; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public.bascule_promotion (
    id bigint NOT NULL,
    timeline integer NOT NULL,
    saut boolean NOT NULL,
    motif character varying(300) NOT NULL,
    cree_le timestamp with time zone DEFAULT now() NOT NULL,
    CONSTRAINT bascule_promotion_timeline_check CHECK ((timeline >= 1))
);


--
-- Name: TABLE bascule_promotion; Type: COMMENT; Schema: public; Owner: -
--

COMMENT ON TABLE public.bascule_promotion IS 'Une ligne par timeline de la base : saut des compteurs fait à cette promotion (saut) ou première note sans saut ; clé unique = un seul saut par promotion ; ajout seul (script 164).';


--
-- Name: bascule_promotion_id_seq; Type: SEQUENCE; Schema: public; Owner: -
--

ALTER TABLE public.bascule_promotion ALTER COLUMN id ADD GENERATED ALWAYS AS IDENTITY (
    SEQUENCE NAME public.bascule_promotion_id_seq
    START WITH 1
    INCREMENT BY 1
    NO MINVALUE
    NO MAXVALUE
    CACHE 1
);


--
-- Name: bascule_saut; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public.bascule_saut (
    id bigint NOT NULL,
    promotion_id bigint NOT NULL,
    compteur text NOT NULL,
    detail character varying(100),
    ancienne bigint NOT NULL,
    nouvelle bigint NOT NULL,
    reserve bigint NOT NULL,
    cree_le timestamp with time zone DEFAULT now() NOT NULL,
    CONSTRAINT bascule_saut_ancienne_check CHECK ((ancienne >= 0)),
    CONSTRAINT bascule_saut_reserve_check CHECK ((reserve >= 1)),
    CONSTRAINT ck_bascule_saut_compteur CHECK ((compteur = ANY (ARRAY['ticket'::text, 'facture'::text, 'avoir'::text, 'commande'::text]))),
    CONSTRAINT ck_bascule_saut_valeurs CHECK ((nouvelle = (ancienne + reserve)))
);


--
-- Name: TABLE bascule_saut; Type: COMMENT; Schema: public; Owner: -
--

COMMENT ON TABLE public.bascule_saut IS 'Chaque saut de compteur fait à une promotion (ancienne et nouvelle valeur, réserve) ; ajout seul (script 164).';


--
-- Name: bascule_saut_id_seq; Type: SEQUENCE; Schema: public; Owner: -
--

ALTER TABLE public.bascule_saut ALTER COLUMN id ADD GENERATED ALWAYS AS IDENTITY (
    SEQUENCE NAME public.bascule_saut_id_seq
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
    officine_id uuid DEFAULT public.officine_courante() NOT NULL,
    CONSTRAINT boite_prix_achat_ht_check CHECK ((prix_achat_ht >= (0)::numeric)),
    CONSTRAINT boite_statut_check CHECK ((statut = ANY (ARRAY['en_stock'::text, 'vendue'::text, 'retournee_fournisseur'::text, 'detruite'::text, 'retour_client'::text, 'donnee'::text, 'manquante'::text]))),
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
    inventaire_id bigint,
    officine_id uuid DEFAULT public.officine_courante() NOT NULL,
    CONSTRAINT boite_evenement_type_check CHECK ((type = ANY (ARRAY['reception'::text, 'vente'::text, 'retour_client'::text, 'retour_fournisseur'::text, 'destruction'::text, 'blocage'::text, 'correction'::text, 'inventaire'::text, 'echantillon'::text, 'manquant_reception'::text])))
);


--
-- Name: COLUMN boite_evenement.inventaire_id; Type: COMMENT; Schema: public; Owner: -
--

COMMENT ON COLUMN public.boite_evenement.inventaire_id IS 'Inventaire où la boîte a été scannée (type inventaire) : boîtes fantômes';


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
    statut text DEFAULT 'preparation'::text NOT NULL,
    date_envoi date,
    canal text DEFAULT 'chifa'::text NOT NULL,
    centre_payeur_id bigint,
    montant_declare numeric(12,2),
    cree_le timestamp with time zone DEFAULT now() NOT NULL,
    cree_par bigint,
    depose_le timestamp with time zone,
    depose_par bigint,
    officine_id uuid DEFAULT public.officine_courante() NOT NULL,
    CONSTRAINT bordereau_check CHECK ((periode_fin >= periode_debut)),
    CONSTRAINT bordereau_statut_check CHECK ((statut = ANY (ARRAY['preparation'::text, 'depose'::text, 'paye_partiel'::text, 'paye'::text, 'solde'::text]))),
    CONSTRAINT ck_bordereau_canal CHECK ((canal = ANY (ARRAY['chifa'::text, 'mdn'::text]))),
    CONSTRAINT ck_bordereau_depot CHECK (((statut = 'preparation'::text) OR (date_envoi IS NOT NULL))),
    CONSTRAINT ck_bordereau_montant_declare CHECK (((montant_declare IS NULL) OR (montant_declare >= (0)::numeric)))
);


--
-- Name: COLUMN bordereau.numero; Type: COMMENT; Schema: public; Owner: -
--

COMMENT ON COLUMN public.bordereau.numero IS 'N° de bordereau de CHIFA-Officine (saisi), unique par organisme (script 110)';


--
-- Name: COLUMN bordereau.montant_total; Type: COMMENT; Schema: public; Owner: -
--

COMMENT ON COLUMN public.bordereau.montant_total IS 'Montant des anciens bordereaux sans ventes ; attendu des nouveaux : v_bordereau (script 110)';


--
-- Name: COLUMN bordereau.date_envoi; Type: COMMENT; Schema: public; Owner: -
--

COMMENT ON COLUMN public.bordereau.date_envoi IS 'Date de dépôt du bordereau à l''organisme (script 110)';


--
-- Name: COLUMN bordereau.canal; Type: COMMENT; Schema: public; Owner: -
--

COMMENT ON COLUMN public.bordereau.canal IS 'chifa (CNAS, CASNOS) ou mdn (script 110)';


--
-- Name: COLUMN bordereau.montant_declare; Type: COMMENT; Schema: public; Owner: -
--

COMMENT ON COLUMN public.bordereau.montant_declare IS 'Montant total déclaré par CHIFA-Officine (saisi), comparé à l''attendu (script 110)';


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
-- Name: bordereau_vente; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public.bordereau_vente (
    id bigint NOT NULL,
    bordereau_id bigint NOT NULL,
    vente_id bigint NOT NULL,
    part_organisme numeric(12,2) NOT NULL,
    ajoute_le timestamp with time zone DEFAULT now() NOT NULL,
    ajoute_par bigint NOT NULL,
    rejete_le timestamp with time zone,
    rejete_par bigint,
    rejet_motif_code text,
    rejet_texte text,
    montant_rejete numeric(12,2) DEFAULT 0 NOT NULL,
    suite text,
    suite_le timestamp with time zone,
    suite_par bigint,
    credit_mouvement_id bigint,
    officine_id uuid DEFAULT public.officine_courante() NOT NULL,
    CONSTRAINT ck_bordereau_vente_rejet CHECK ((((rejete_le IS NULL) AND (rejete_par IS NULL) AND (rejet_motif_code IS NULL) AND (rejet_texte IS NULL) AND (montant_rejete = (0)::numeric) AND (suite IS NULL)) OR ((rejete_le IS NOT NULL) AND (rejete_par IS NOT NULL) AND (rejet_motif_code IS NOT NULL) AND (montant_rejete > (0)::numeric) AND (montant_rejete <= (part_organisme + 0.01))))),
    CONSTRAINT ck_bordereau_vente_suite CHECK ((((suite IS NULL) AND (suite_le IS NULL) AND (suite_par IS NULL) AND (credit_mouvement_id IS NULL)) OR ((suite = ANY (ARRAY['redeposer'::text, 'perte'::text, 'patient'::text])) AND (suite_le IS NOT NULL) AND (suite_par IS NOT NULL) AND ((suite = 'patient'::text) = (credit_mouvement_id IS NOT NULL)))))
);


--
-- Name: TABLE bordereau_vente; Type: COMMENT; Schema: public; Owner: -
--

COMMENT ON TABLE public.bordereau_vente IS 'Factures Chifa (une par vente) d''un bordereau, et leur rejet éventuel (script 110)';


--
-- Name: COLUMN bordereau_vente.part_organisme; Type: COMMENT; Schema: public; Owner: -
--

COMMENT ON COLUMN public.bordereau_vente.part_organisme IS 'Part organisme retenue au rattachement (information ; l''attendu se lit dans v_vente_part_organisme)';


--
-- Name: COLUMN bordereau_vente.rejet_motif_code; Type: COMMENT; Schema: public; Owner: -
--

COMMENT ON COLUMN public.bordereau_vente.rejet_motif_code IS 'Motif du rejet (motif_reglable, liste rejet_chifa)';


--
-- Name: COLUMN bordereau_vente.suite; Type: COMMENT; Schema: public; Owner: -
--

COMMENT ON COLUMN public.bordereau_vente.suite IS 'Suite du rejet : redeposer (vente à déposer à nouveau), perte, patient (ajoutée à sa dette)';


--
-- Name: bordereau_vente_id_seq; Type: SEQUENCE; Schema: public; Owner: -
--

ALTER TABLE public.bordereau_vente ALTER COLUMN id ADD GENERATED ALWAYS AS IDENTITY (
    SEQUENCE NAME public.bordereau_vente_id_seq
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
    officine_id uuid DEFAULT public.officine_courante() NOT NULL,
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
    valeur numeric(14,2) NOT NULL,
    officine_id uuid DEFAULT public.officine_courante() NOT NULL
);


--
-- Name: catalogue_grossiste; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public.catalogue_grossiste (
    fournisseur_id bigint NOT NULL,
    produit_id bigint NOT NULL,
    code_grossiste text,
    designation text,
    prix_ht numeric(12,2),
    disponible boolean,
    quantite_disponible integer,
    delai_jours integer,
    import_id bigint,
    maj_le timestamp with time zone DEFAULT now() NOT NULL,
    officine_id uuid DEFAULT public.officine_courante() NOT NULL,
    CONSTRAINT catalogue_grossiste_delai_jours_check CHECK (((delai_jours IS NULL) OR (delai_jours >= 0))),
    CONSTRAINT catalogue_grossiste_prix_ht_check CHECK (((prix_ht IS NULL) OR (prix_ht >= (0)::numeric))),
    CONSTRAINT catalogue_grossiste_quantite_disponible_check CHECK (((quantite_disponible IS NULL) OR (quantite_disponible >= 0)))
);


--
-- Name: categorie_produit; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public.categorie_produit (
    code text NOT NULL,
    libelle text NOT NULL,
    medicament boolean DEFAULT false NOT NULL,
    ordre integer DEFAULT 0 NOT NULL,
    actif boolean DEFAULT true NOT NULL,
    officine_id uuid DEFAULT public.officine_courante() NOT NULL
);


--
-- Name: TABLE categorie_produit; Type: COMMENT; Schema: public; Owner: -
--

COMMENT ON TABLE public.categorie_produit IS 'Catégories de produits ; medicament : compté dans les totaux « médicaments », sinon parapharmacie (« autres produits » au ticket)';


--
-- Name: centre_payeur; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public.centre_payeur (
    id bigint NOT NULL,
    organisme_id bigint NOT NULL,
    code text NOT NULL,
    nom text NOT NULL,
    actif boolean DEFAULT true NOT NULL,
    cree_le timestamp with time zone DEFAULT now() NOT NULL,
    officine_id uuid DEFAULT public.officine_courante() NOT NULL,
    CONSTRAINT ck_centre_payeur_code CHECK (((code = btrim(code)) AND ((char_length(code) >= 1) AND (char_length(code) <= 10)))),
    CONSTRAINT ck_centre_payeur_nom CHECK (((char_length(btrim(nom)) >= 1) AND (char_length(btrim(nom)) <= 120)))
);


--
-- Name: TABLE centre_payeur; Type: COMMENT; Schema: public; Owner: -
--

COMMENT ON TABLE public.centre_payeur IS 'Centre payeur d''un organisme Chifa (CNAS, CASNOS) ; code libre 1 à 10 caractères (D-2) ; jamais supprimé, désactivé (script 100)';


--
-- Name: centre_payeur_id_seq; Type: SEQUENCE; Schema: public; Owner: -
--

ALTER TABLE public.centre_payeur ALTER COLUMN id ADD GENERATED ALWAYS AS IDENTITY (
    SEQUENCE NAME public.centre_payeur_id_seq
    START WITH 1
    INCREMENT BY 1
    NO MINVALUE
    NO MAXVALUE
    CACHE 1
);


--
-- Name: chaine_integrite; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public.chaine_integrite (
    journal text NOT NULL,
    libelle text NOT NULL,
    colonnes text[] NOT NULL,
    algorithme text DEFAULT 'sha256'::text NOT NULL,
    version_format smallint DEFAULT 1 NOT NULL,
    active_le timestamp with time zone DEFAULT now() NOT NULL,
    CONSTRAINT ck_chaine_integrite_algorithme CHECK ((algorithme = ANY (ARRAY['sha256'::text, 'sha512'::text]))),
    CONSTRAINT ck_chaine_integrite_colonnes CHECK ((cardinality(colonnes) > 0))
);


--
-- Name: chifa_code_produit; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public.chifa_code_produit (
    code text NOT NULL,
    produit_id bigint NOT NULL,
    associe_par bigint,
    associe_le timestamp with time zone DEFAULT now() NOT NULL,
    officine_id uuid DEFAULT public.officine_courante() NOT NULL
);


--
-- Name: TABLE chifa_code_produit; Type: COMMENT; Schema: public; Owner: -
--

COMMENT ON TABLE public.chifa_code_produit IS 'Code produit du QR des volets Chifa associé à un produit du catalogue (fait une fois par le pharmacien)';


--
-- Name: classe_suivie; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public.classe_suivie (
    code text NOT NULL,
    libelle text NOT NULL,
    active boolean DEFAULT true NOT NULL,
    officine_id uuid DEFAULT public.officine_courante() NOT NULL,
    CONSTRAINT ck_classe_suivie_code CHECK ((code ~ '^[a-z0-9_]{2,40}$'::text))
);


--
-- Name: classe_suivie_dci; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public.classe_suivie_dci (
    classe_code text NOT NULL,
    dci_id bigint NOT NULL,
    ajoute_le timestamp with time zone DEFAULT now() NOT NULL,
    ajoute_par bigint,
    officine_id uuid DEFAULT public.officine_courante() NOT NULL
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
    cree_le timestamp with time zone DEFAULT now() NOT NULL,
    email text,
    delai_paiement_jours integer DEFAULT 30 NOT NULL,
    officine_id uuid DEFAULT public.officine_courante() NOT NULL,
    CONSTRAINT ck_client_facture_delai_paiement CHECK (((delai_paiement_jours >= 1) AND (delai_paiement_jours <= 365)))
);


--
-- Name: COLUMN client_facture.email; Type: COMMENT; Schema: public; Owner: -
--

COMMENT ON COLUMN public.client_facture.email IS 'Adresse e-mail de l''entreprise cliente (facultative)';


--
-- Name: COLUMN client_facture.delai_paiement_jours; Type: COMMENT; Schema: public; Owner: -
--

COMMENT ON COLUMN public.client_facture.delai_paiement_jours IS 'Délai de paiement des factures du client : échéance proposée (jours)';


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
-- Name: cloture_fiscale; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public.cloture_fiscale (
    id bigint NOT NULL,
    type text NOT NULL,
    session_caisse_id bigint,
    periode_du date NOT NULL,
    periode_au date NOT NULL,
    sceau_id bigint NOT NULL,
    evenements bigint DEFAULT 0 NOT NULL,
    montant_ttc numeric(14,2) DEFAULT 0 NOT NULL,
    montant_ht numeric(14,2) DEFAULT 0 NOT NULL,
    montant_tva numeric(14,2) DEFAULT 0 NOT NULL,
    fichier_archive text,
    empreinte_archive bytea,
    cree_le timestamp with time zone DEFAULT now() NOT NULL,
    officine_id uuid DEFAULT public.officine_courante() NOT NULL,
    CONSTRAINT ck_cloture_fiscale_periode CHECK ((periode_au >= periode_du)),
    CONSTRAINT ck_cloture_fiscale_session CHECK (((type = 'journaliere'::text) = (session_caisse_id IS NOT NULL))),
    CONSTRAINT ck_cloture_fiscale_type CHECK ((type = ANY (ARRAY['journaliere'::text, 'mensuelle'::text])))
);


--
-- Name: cloture_fiscale_id_seq; Type: SEQUENCE; Schema: public; Owner: -
--

ALTER TABLE public.cloture_fiscale ALTER COLUMN id ADD GENERATED ALWAYS AS IDENTITY (
    SEQUENCE NAME public.cloture_fiscale_id_seq
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
    envoyee_email_le timestamp with time zone,
    envoyee_email_a text,
    officine_id uuid DEFAULT public.officine_courante() NOT NULL,
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
-- Name: commande_ordre_central; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public.commande_ordre_central (
    commande_id bigint NOT NULL,
    ordre_id uuid NOT NULL,
    auteur text NOT NULL,
    cree_le timestamp with time zone DEFAULT now() NOT NULL,
    CONSTRAINT commande_ordre_central_auteur_check CHECK (((length(auteur) >= 1) AND (length(auteur) <= 100)))
);


--
-- Name: TABLE commande_ordre_central; Type: COMMENT; Schema: public; Owner: -
--

COMMENT ON TABLE public.commande_ordre_central IS 'Commandes créées en brouillon par un ordre du serveur central (lot S5.4) ; jamais envoyée (script 146).';


--
-- Name: comptage_caisse; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public.comptage_caisse (
    session_id bigint NOT NULL,
    coupure text NOT NULL,
    nombre integer NOT NULL,
    montant numeric(12,2) NOT NULL,
    officine_id uuid DEFAULT public.officine_courante() NOT NULL,
    CONSTRAINT comptage_caisse_coupure_check CHECK ((coupure = ANY (ARRAY['b2000'::text, 'b1000'::text, 'b500'::text, 'b200'::text, 'p100'::text, 'autres'::text]))),
    CONSTRAINT comptage_caisse_montant_check CHECK ((montant >= (0)::numeric)),
    CONSTRAINT comptage_caisse_nombre_check CHECK ((nombre >= 0))
);


--
-- Name: comptage_intermediaire; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public.comptage_intermediaire (
    id bigint NOT NULL,
    session_caisse_id bigint NOT NULL,
    utilisateur_id bigint NOT NULL,
    compte numeric(12,2) NOT NULL,
    attendu numeric(12,2) NOT NULL,
    ecart numeric(12,2) NOT NULL,
    comptage jsonb NOT NULL,
    cree_le timestamp with time zone DEFAULT now() NOT NULL,
    officine_id uuid DEFAULT public.officine_courante() NOT NULL,
    CONSTRAINT comptage_intermediaire_compte_check CHECK ((compte >= (0)::numeric))
);


--
-- Name: TABLE comptage_intermediaire; Type: COMMENT; Schema: public; Owner: -
--

COMMENT ON TABLE public.comptage_intermediaire IS 'Comptages du tiroir en cours de journée, sans clôture (attendu et écart réservés au droit de voir les écarts)';


--
-- Name: comptage_intermediaire_id_seq; Type: SEQUENCE; Schema: public; Owner: -
--

ALTER TABLE public.comptage_intermediaire ALTER COLUMN id ADD GENERATED ALWAYS AS IDENTITY (
    SEQUENCE NAME public.comptage_intermediaire_id_seq
    START WITH 1
    INCREMENT BY 1
    NO MINVALUE
    NO MAXVALUE
    CACHE 1
);


--
-- Name: comptage_reception; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public.comptage_reception (
    id bigint NOT NULL,
    fournisseur_id bigint NOT NULL,
    numero_bl text NOT NULL,
    statut text DEFAULT 'compte'::text NOT NULL,
    compte_par bigint NOT NULL,
    compte_le timestamp with time zone DEFAULT now() NOT NULL,
    facture_par bigint,
    facture_le timestamp with time zone,
    saisie jsonb,
    ecarts jsonb,
    boites_manquantes integer DEFAULT 0 NOT NULL,
    boites_en_trop integer DEFAULT 0 NOT NULL,
    valeur_manquante numeric(12,2) DEFAULT 0 NOT NULL,
    decide_par bigint,
    decide_le timestamp with time zone,
    reception_id bigint,
    officine_id uuid DEFAULT public.officine_courante() NOT NULL,
    CONSTRAINT comptage_reception_boites_en_trop_check CHECK ((boites_en_trop >= 0)),
    CONSTRAINT comptage_reception_boites_manquantes_check CHECK ((boites_manquantes >= 0)),
    CONSTRAINT comptage_reception_statut_check CHECK ((statut = ANY (ARRAY['compte'::text, 'ecart'::text, 'receptionne'::text, 'a_recompter'::text, 'annule'::text]))),
    CONSTRAINT comptage_reception_valeur_manquante_check CHECK ((valeur_manquante >= (0)::numeric))
);


--
-- Name: TABLE comptage_reception; Type: COMMENT; Schema: public; Owner: -
--

COMMENT ON TABLE public.comptage_reception IS 'Réception à l''aveugle : boîtes comptées sans voir le BL ; la facture saisie ensuite est comparée au compté';


--
-- Name: COLUMN comptage_reception.saisie; Type: COMMENT; Schema: public; Owner: -
--

COMMENT ON COLUMN public.comptage_reception.saisie IS 'Facture saisie en attente de validation (écart) ; jamais de code comptoir';


--
-- Name: COLUMN comptage_reception.ecarts; Type: COMMENT; Schema: public; Owner: -
--

COMMENT ON COLUMN public.comptage_reception.ecarts IS 'Lignes en écart : produit, lot, facturé, compté';


--
-- Name: comptage_reception_id_seq; Type: SEQUENCE; Schema: public; Owner: -
--

ALTER TABLE public.comptage_reception ALTER COLUMN id ADD GENERATED ALWAYS AS IDENTITY (
    SEQUENCE NAME public.comptage_reception_id_seq
    START WITH 1
    INCREMENT BY 1
    NO MINVALUE
    NO MAXVALUE
    CACHE 1
);


--
-- Name: comptage_reception_ligne; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public.comptage_reception_ligne (
    id bigint NOT NULL,
    comptage_id bigint NOT NULL,
    produit_id bigint NOT NULL,
    numero_lot text NOT NULL,
    date_peremption date NOT NULL,
    gtin text,
    quantite integer NOT NULL,
    numeros_serie text[] DEFAULT '{}'::text[] NOT NULL,
    scans_mobile bigint[] DEFAULT '{}'::bigint[] NOT NULL,
    officine_id uuid DEFAULT public.officine_courante() NOT NULL,
    CONSTRAINT comptage_reception_ligne_quantite_check CHECK (((quantite >= 1) AND (quantite <= 10000)))
);


--
-- Name: comptage_reception_ligne_id_seq; Type: SEQUENCE; Schema: public; Owner: -
--

ALTER TABLE public.comptage_reception_ligne ALTER COLUMN id ADD GENERATED ALWAYS AS IDENTITY (
    SEQUENCE NAME public.comptage_reception_ligne_id_seq
    START WITH 1
    INCREMENT BY 1
    NO MINVALUE
    NO MAXVALUE
    CACHE 1
);


--
-- Name: compte_carte_periode; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public.compte_carte_periode (
    id bigint NOT NULL,
    compte_id bigint NOT NULL,
    du date NOT NULL,
    au date,
    officine_id uuid DEFAULT public.officine_courante() NOT NULL,
    CONSTRAINT compte_carte_periode_check CHECK (((au IS NULL) OR (au >= du)))
);


--
-- Name: TABLE compte_carte_periode; Type: COMMENT; Schema: public; Owner: -
--

COMMENT ON TABLE public.compte_carte_periode IS 'Banque qui reçoit les paiements par carte, du … au … (au vide : en cours). Tenue par le déclencheur de compte_tresorerie.recoit_carte';


--
-- Name: compte_carte_periode_id_seq; Type: SEQUENCE; Schema: public; Owner: -
--

ALTER TABLE public.compte_carte_periode ALTER COLUMN id ADD GENERATED ALWAYS AS IDENTITY (
    SEQUENCE NAME public.compte_carte_periode_id_seq
    START WITH 1
    INCREMENT BY 1
    NO MINVALUE
    NO MAXVALUE
    CACHE 1
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
    officine_id uuid DEFAULT public.officine_courante() NOT NULL,
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
-- Name: compteur_ticket; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public.compteur_ticket (
    id smallint DEFAULT 1 NOT NULL,
    prefixe text DEFAULT 'T-'::text NOT NULL,
    dernier_numero bigint NOT NULL,
    premier_numero_continu bigint NOT NULL,
    mis_en_service_le timestamp with time zone DEFAULT now() NOT NULL,
    officine_id uuid DEFAULT public.officine_courante() NOT NULL,
    CONSTRAINT ck_compteur_ticket_numero CHECK (((dernier_numero >= 0) AND (premier_numero_continu >= 1))),
    CONSTRAINT compteur_ticket_id_check CHECK ((id = 1))
);


--
-- Name: compteur_ticket_saut; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public.compteur_ticket_saut (
    id bigint NOT NULL,
    du bigint NOT NULL,
    au bigint NOT NULL,
    motif text NOT NULL,
    epoque integer,
    cree_le timestamp with time zone DEFAULT now() NOT NULL,
    CONSTRAINT ck_compteur_ticket_saut_motif CHECK ((motif = ANY (ARRAY['restauration'::text, 'bascule'::text]))),
    CONSTRAINT ck_compteur_ticket_saut_plage CHECK ((au >= du)),
    CONSTRAINT compteur_ticket_saut_du_check CHECK ((du >= 1))
);


--
-- Name: TABLE compteur_ticket_saut; Type: COMMENT; Schema: public; Owner: -
--

COMMENT ON TABLE public.compteur_ticket_saut IS 'Plages de n° de tickets sautées après un retour en arrière de la base (restauration, bascule) : n° déjà attribués à des ventes reçues par le serveur central ; ajout seul (script 151, décision S6.5-D6 a).';


--
-- Name: compteur_ticket_saut_id_seq; Type: SEQUENCE; Schema: public; Owner: -
--

ALTER TABLE public.compteur_ticket_saut ALTER COLUMN id ADD GENERATED ALWAYS AS IDENTITY (
    SEQUENCE NAME public.compteur_ticket_saut_id_seq
    START WITH 1
    INCREMENT BY 1
    NO MINVALUE
    NO MAXVALUE
    CACHE 1
);


--
-- Name: connexion_employe; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public.connexion_employe (
    id bigint NOT NULL,
    utilisateur_id bigint NOT NULL,
    poste text,
    methode text NOT NULL,
    cree_le timestamp with time zone DEFAULT now() NOT NULL,
    officine_id uuid DEFAULT public.officine_courante() NOT NULL,
    CONSTRAINT connexion_employe_methode_check CHECK ((methode = ANY (ARRAY['mot_de_passe'::text, 'code'::text])))
);


--
-- Name: TABLE connexion_employe; Type: COMMENT; Schema: public; Owner: -
--

COMMENT ON TABLE public.connexion_employe IS 'Connexions réussies des employés (poste, mot de passe ou code comptoir) : postes simultanés';


--
-- Name: connexion_employe_id_seq; Type: SEQUENCE; Schema: public; Owner: -
--

ALTER TABLE public.connexion_employe ALTER COLUMN id ADD GENERATED ALWAYS AS IDENTITY (
    SEQUENCE NAME public.connexion_employe_id_seq
    START WITH 1
    INCREMENT BY 1
    NO MINVALUE
    NO MAXVALUE
    CACHE 1
);


--
-- Name: correction_lot; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public.correction_lot (
    id bigint NOT NULL,
    lot_id bigint NOT NULL,
    champ text NOT NULL,
    ancienne_valeur text,
    nouvelle_valeur text,
    motif text NOT NULL,
    mouvement_id bigint,
    utilisateur_id bigint NOT NULL,
    corrige_le timestamp with time zone DEFAULT now() NOT NULL,
    officine_id uuid DEFAULT public.officine_courante() NOT NULL,
    CONSTRAINT ck_correction_lot_champ CHECK ((champ = ANY (ARRAY['ppa'::text, 'numero_lot'::text, 'date_peremption'::text, 'shp'::text, 'quantite'::text]))),
    CONSTRAINT ck_correction_lot_motif CHECK ((length(btrim(motif)) > 0))
);


--
-- Name: TABLE correction_lot; Type: COMMENT; Schema: public; Owner: -
--

COMMENT ON TABLE public.correction_lot IS 'Corrections d''une entrée de stock (script 103) : une ligne par champ corrigé, ancienne → nouvelle valeur, motif ; jamais modifiée ni supprimée';


--
-- Name: COLUMN correction_lot.mouvement_id; Type: COMMENT; Schema: public; Owner: -
--

COMMENT ON COLUMN public.correction_lot.mouvement_id IS 'Correction de quantité : mouvement de stock (ajustement) qui l''a portée';


--
-- Name: correction_lot_id_seq; Type: SEQUENCE; Schema: public; Owner: -
--

ALTER TABLE public.correction_lot ALTER COLUMN id ADD GENERATED ALWAYS AS IDENTITY (
    SEQUENCE NAME public.correction_lot_id_seq
    START WITH 1
    INCREMENT BY 1
    NO MINVALUE
    NO MAXVALUE
    CACHE 1
);


--
-- Name: correspondance_echeancier; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public.correspondance_echeancier (
    compte_id bigint NOT NULL,
    col_date integer NOT NULL,
    col_capital integer,
    col_interets integer,
    col_tva integer,
    col_marge integer,
    col_montant integer,
    col_encours integer,
    lignes_ignorees integer DEFAULT 0 NOT NULL,
    utilisateur_id bigint NOT NULL,
    modifie_le timestamp with time zone DEFAULT now() NOT NULL,
    col_assurance integer,
    officine_id uuid DEFAULT public.officine_courante() NOT NULL,
    CONSTRAINT correspondance_echeancier_col_assurance_check CHECK ((col_assurance >= 0)),
    CONSTRAINT correspondance_echeancier_col_capital_check CHECK ((col_capital >= 0)),
    CONSTRAINT correspondance_echeancier_col_date_check CHECK ((col_date >= 0)),
    CONSTRAINT correspondance_echeancier_col_encours_check CHECK ((col_encours >= 0)),
    CONSTRAINT correspondance_echeancier_col_interets_check CHECK ((col_interets >= 0)),
    CONSTRAINT correspondance_echeancier_col_marge_check CHECK ((col_marge >= 0)),
    CONSTRAINT correspondance_echeancier_col_montant_check CHECK ((col_montant >= 0)),
    CONSTRAINT correspondance_echeancier_col_tva_check CHECK ((col_tva >= 0)),
    CONSTRAINT correspondance_echeancier_lignes_ignorees_check CHECK ((lignes_ignorees >= 0))
);


--
-- Name: TABLE correspondance_echeancier; Type: COMMENT; Schema: public; Owner: -
--

COMMENT ON TABLE public.correspondance_echeancier IS 'Colonnes (à partir de 0) du tableau d''échéances fourni par cette banque, et lignes d''en-tête à ignorer ; proposées au prochain import';


--
-- Name: correspondance_echeancier_preteur; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public.correspondance_echeancier_preteur (
    preteur text NOT NULL,
    col_date integer NOT NULL,
    col_capital integer,
    col_interets integer,
    col_tva integer,
    col_marge integer,
    col_montant integer,
    col_encours integer,
    lignes_ignorees integer DEFAULT 0 NOT NULL,
    utilisateur_id bigint NOT NULL,
    modifie_le timestamp with time zone DEFAULT now() NOT NULL,
    col_assurance integer,
    officine_id uuid DEFAULT public.officine_courante() NOT NULL,
    CONSTRAINT correspondance_echeancier_preteur_col_assurance_check CHECK ((col_assurance >= 0)),
    CONSTRAINT correspondance_echeancier_preteur_col_capital_check CHECK ((col_capital >= 0)),
    CONSTRAINT correspondance_echeancier_preteur_col_date_check CHECK ((col_date >= 0)),
    CONSTRAINT correspondance_echeancier_preteur_col_encours_check CHECK ((col_encours >= 0)),
    CONSTRAINT correspondance_echeancier_preteur_col_interets_check CHECK ((col_interets >= 0)),
    CONSTRAINT correspondance_echeancier_preteur_col_marge_check CHECK ((col_marge >= 0)),
    CONSTRAINT correspondance_echeancier_preteur_col_montant_check CHECK ((col_montant >= 0)),
    CONSTRAINT correspondance_echeancier_preteur_col_tva_check CHECK ((col_tva >= 0)),
    CONSTRAINT correspondance_echeancier_preteur_lignes_ignorees_check CHECK ((lignes_ignorees >= 0)),
    CONSTRAINT correspondance_echeancier_preteur_preteur_check CHECK (((preteur = lower(btrim(preteur))) AND (preteur <> ''::text)))
);


--
-- Name: TABLE correspondance_echeancier_preteur; Type: COMMENT; Schema: public; Owner: -
--

COMMENT ON TABLE public.correspondance_echeancier_preteur IS 'Colonnes (à partir de 0) du tableau d''échéances d''un prêteur hors trésorerie (nom en minuscules, sans espaces autour), proposées au prochain import';


--
-- Name: credit_imputation; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public.credit_imputation (
    id bigint NOT NULL,
    credit_mouvement_id bigint NOT NULL,
    vente_id bigint NOT NULL,
    montant numeric(12,2) NOT NULL,
    cree_le timestamp with time zone DEFAULT now() NOT NULL,
    officine_id uuid DEFAULT public.officine_courante() NOT NULL,
    CONSTRAINT ck_credit_imputation_montant CHECK ((montant > (0)::numeric))
);


--
-- Name: credit_imputation_id_seq; Type: SEQUENCE; Schema: public; Owner: -
--

ALTER TABLE public.credit_imputation ALTER COLUMN id ADD GENERATED ALWAYS AS IDENTITY (
    SEQUENCE NAME public.credit_imputation_id_seq
    START WITH 1
    INCREMENT BY 1
    NO MINVALUE
    NO MAXVALUE
    CACHE 1
);


--
-- Name: credit_mouvement; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public.credit_mouvement (
    id bigint NOT NULL,
    patient_id bigint NOT NULL,
    type text NOT NULL,
    montant numeric(12,2) NOT NULL,
    solde_apres numeric(12,2) NOT NULL,
    vente_id bigint,
    utilisateur_id bigint,
    poste text,
    reference text,
    cree_le timestamp with time zone DEFAULT now() NOT NULL,
    officine_id uuid DEFAULT public.officine_courante() NOT NULL,
    CONSTRAINT credit_mouvement_montant_check CHECK ((montant <> (0)::numeric)),
    CONSTRAINT credit_mouvement_type_check CHECK ((type = ANY (ARRAY['ouverture'::text, 'reprise'::text, 'vente_credit'::text, 'remboursement'::text, 'retour'::text, 'annulation'::text, 'correction'::text])))
);


--
-- Name: TABLE credit_mouvement; Type: COMMENT; Schema: public; Owner: -
--

COMMENT ON TABLE public.credit_mouvement IS 'Relevé de la dette des patients : une ligne par mouvement de patient.solde_credit (déclencheur trg_patient_credit)';


--
-- Name: credit_mouvement_id_seq; Type: SEQUENCE; Schema: public; Owner: -
--

ALTER TABLE public.credit_mouvement ALTER COLUMN id ADD GENERATED ALWAYS AS IDENTITY (
    SEQUENCE NAME public.credit_mouvement_id_seq
    START WITH 1
    INCREMENT BY 1
    NO MINVALUE
    NO MAXVALUE
    CACHE 1
);


--
-- Name: credit_ouverture; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public.credit_ouverture (
    patient_id bigint NOT NULL,
    montant numeric(12,2) NOT NULL,
    officine_id uuid DEFAULT public.officine_courante() NOT NULL,
    CONSTRAINT credit_ouverture_montant_check CHECK ((montant >= (0)::numeric))
);


--
-- Name: TABLE credit_ouverture; Type: COMMENT; Schema: public; Owner: -
--

COMMENT ON TABLE public.credit_ouverture IS 'Dette antérieure aux ventes à crédit du logiciel (reprise) : soldée en premier par les remboursements';


--
-- Name: dci; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public.dci (
    id bigint NOT NULL,
    nom text NOT NULL,
    officine_id uuid DEFAULT public.officine_courante() NOT NULL
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
-- Name: demande_patient; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public.demande_patient (
    id bigint NOT NULL,
    patient_id bigint NOT NULL,
    produit_id bigint NOT NULL,
    quantite integer DEFAULT 1 NOT NULL,
    cree_le timestamp with time zone DEFAULT now() NOT NULL,
    cree_par bigint,
    statut text DEFAULT 'en_attente'::text NOT NULL,
    prevenu_le timestamp with time zone,
    prevenu_par bigint,
    prevenu_canal text,
    sms_erreur text,
    cloture_le timestamp with time zone,
    cloture_par bigint,
    officine_id uuid DEFAULT public.officine_courante() NOT NULL,
    CONSTRAINT demande_patient_prevenu_canal_check CHECK (((prevenu_canal IS NULL) OR (prevenu_canal = ANY (ARRAY['comptoir'::text, 'sms'::text])))),
    CONSTRAINT demande_patient_quantite_check CHECK (((quantite >= 1) AND (quantite <= 1000))),
    CONSTRAINT demande_patient_statut_check CHECK ((statut = ANY (ARRAY['en_attente'::text, 'prevenu'::text, 'servie'::text, 'annulee'::text])))
);


--
-- Name: demande_patient_id_seq; Type: SEQUENCE; Schema: public; Owner: -
--

ALTER TABLE public.demande_patient ALTER COLUMN id ADD GENERATED ALWAYS AS IDENTITY (
    SEQUENCE NAME public.demande_patient_id_seq
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
    code_verification_hash text,
    code_verification_expire timestamp with time zone,
    code_verification_essais integer DEFAULT 0 NOT NULL,
    CONSTRAINT destinataire_notification_check CHECK (((NOT canal_email) OR (email IS NOT NULL))),
    CONSTRAINT destinataire_notification_check1 CHECK (((NOT canal_sms) OR (telephone IS NOT NULL))),
    CONSTRAINT destinataire_notification_types_check CHECK ((types <@ ARRAY['rapport_quotidien'::text, 'ecart_caisse'::text, 'lot_bloque'::text, 'sauvegarde'::text, 'ecart_inventaire'::text, 'lot_delaisse'::text, 'surveillance'::text, 'synchronisation'::text, 'reglages_centraux'::text]))
);


--
-- Name: COLUMN destinataire_notification.code_verification_hash; Type: COMMENT; Schema: public; Owner: -
--

COMMENT ON COLUMN public.destinataire_notification.code_verification_hash IS 'Code de vérification envoyé au destinataire (haché), effacé une fois vérifié';


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
-- Name: ecart_caisse_vu; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public.ecart_caisse_vu (
    session_id bigint NOT NULL,
    vu_le timestamp with time zone DEFAULT now() NOT NULL,
    vu_par bigint NOT NULL,
    officine_id uuid DEFAULT public.officine_courante() NOT NULL
);


--
-- Name: TABLE ecart_caisse_vu; Type: COMMENT; Schema: public; Owner: -
--

COMMENT ON TABLE public.ecart_caisse_vu IS 'Écart de caisse (session clôturée avec un écart non nul) marqué vu par le titulaire : il quitte la tuile « Écarts de caisse récents » de son accueil';


--
-- Name: echeance_pret; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public.echeance_pret (
    id bigint NOT NULL,
    pret_id bigint NOT NULL,
    rang integer NOT NULL,
    date_echeance date NOT NULL,
    capital numeric(12,2) NOT NULL,
    interets numeric(12,2) DEFAULT 0 NOT NULL,
    tva numeric(12,2) DEFAULT 0 NOT NULL,
    marge numeric(12,2) DEFAULT 0 NOT NULL,
    montant numeric(12,2) NOT NULL,
    capital_restant numeric(12,2) NOT NULL,
    assurance numeric(12,2) DEFAULT 0 NOT NULL,
    officine_id uuid DEFAULT public.officine_courante() NOT NULL,
    CONSTRAINT echeance_pret_assurance_check CHECK ((assurance >= (0)::numeric)),
    CONSTRAINT echeance_pret_capital_check CHECK ((capital >= (0)::numeric)),
    CONSTRAINT echeance_pret_capital_restant_check CHECK ((capital_restant >= (0)::numeric)),
    CONSTRAINT echeance_pret_check CHECK ((((((capital + interets) + tva) + marge) + assurance) = montant)),
    CONSTRAINT echeance_pret_interets_check CHECK ((interets >= (0)::numeric)),
    CONSTRAINT echeance_pret_marge_check CHECK ((marge >= (0)::numeric)),
    CONSTRAINT echeance_pret_montant_check CHECK ((montant > (0)::numeric)),
    CONSTRAINT echeance_pret_rang_check CHECK ((rang > 0)),
    CONSTRAINT echeance_pret_tva_check CHECK ((tva >= (0)::numeric))
);


--
-- Name: TABLE echeance_pret; Type: COMMENT; Schema: public; Owner: -
--

COMMENT ON TABLE public.echeance_pret IS 'Échéancier d''un prêt ; une échéance payée n''est plus modifiée';


--
-- Name: COLUMN echeance_pret.marge; Type: COMMENT; Schema: public; Owner: -
--

COMMENT ON COLUMN public.echeance_pret.marge IS 'Marge du financement islamique (mourabaha), facultative';


--
-- Name: COLUMN echeance_pret.assurance; Type: COMMENT; Schema: public; Owner: -
--

COMMENT ON COLUMN public.echeance_pret.assurance IS 'Assurance comprise dans l''échéance, facultative ; payée avec les intérêts en charges financières';


--
-- Name: echeance_pret_id_seq; Type: SEQUENCE; Schema: public; Owner: -
--

ALTER TABLE public.echeance_pret ALTER COLUMN id ADD GENERATED ALWAYS AS IDENTITY (
    SEQUENCE NAME public.echeance_pret_id_seq
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
    officine_id uuid DEFAULT public.officine_courante() NOT NULL,
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
-- Name: evenement_caisse; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public.evenement_caisse (
    id bigint NOT NULL,
    type text NOT NULL,
    utilisateur_id bigint NOT NULL,
    poste text,
    session_caisse_id bigint,
    produit_id bigint,
    lot_id bigint,
    quantite integer,
    montant numeric(12,2),
    detail text,
    cree_le timestamp with time zone DEFAULT now() NOT NULL,
    officine_id uuid DEFAULT public.officine_courante() NOT NULL,
    CONSTRAINT ck_evenement_caisse_type CHECK ((type = ANY (ARRAY['ligne_retiree'::text, 'tiroir_ouvert'::text, 'attente_abandonnee'::text])))
);


--
-- Name: TABLE evenement_caisse; Type: COMMENT; Schema: public; Owner: -
--

COMMENT ON TABLE public.evenement_caisse IS 'Comptoir sans vente : ligne retirée du panier, tiroir ouvert sans vente, vente en attente abandonnée';


--
-- Name: evenement_caisse_id_seq; Type: SEQUENCE; Schema: public; Owner: -
--

ALTER TABLE public.evenement_caisse ALTER COLUMN id ADD GENERATED ALWAYS AS IDENTITY (
    SEQUENCE NAME public.evenement_caisse_id_seq
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
    date_echeance date,
    part_credit numeric(12,2) DEFAULT 0 NOT NULL,
    officine_id uuid DEFAULT public.officine_courante() NOT NULL,
    CONSTRAINT ck_facture_client_part_credit CHECK (((part_credit >= (0)::numeric) AND ((part_credit = (0)::numeric) OR ((type = 'avoir'::text) AND (part_credit <= montant_ttc))))),
    CONSTRAINT ck_facture_echeance CHECK ((((type = 'facture'::text) AND (date_echeance IS NOT NULL) AND (date_echeance >= date_facture)) OR ((type = 'avoir'::text) AND (date_echeance IS NULL)))),
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
-- Name: COLUMN facture_client.date_facture; Type: COMMENT; Schema: public; Owner: -
--

COMMENT ON COLUMN public.facture_client.date_facture IS 'Date d''établissement de la facture';


--
-- Name: COLUMN facture_client.date_echeance; Type: COMMENT; Schema: public; Owner: -
--

COMMENT ON COLUMN public.facture_client.date_echeance IS 'Date d''échéance : la facture doit être payée à cette date (pas d''échéance pour un avoir)';


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
    officine_id uuid DEFAULT public.officine_courante() NOT NULL,
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
    cree_le timestamp with time zone DEFAULT now() NOT NULL,
    officine_id uuid DEFAULT public.officine_courante() NOT NULL
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
-- Name: forcage_alerte_psychotrope; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public.forcage_alerte_psychotrope (
    id bigint NOT NULL,
    cree_le timestamp with time zone DEFAULT now() NOT NULL,
    utilisateur_id bigint NOT NULL,
    valide_par bigint,
    codes text[] NOT NULL,
    messages text NOT NULL,
    motif text NOT NULL,
    patient_id bigint,
    medecin_id bigint,
    numero_ordonnance text,
    produits bigint[] NOT NULL,
    poste text,
    officine_id uuid DEFAULT public.officine_courante() NOT NULL,
    CONSTRAINT ck_forcage_psy_codes CHECK ((cardinality(codes) > 0)),
    CONSTRAINT ck_forcage_psy_motif CHECK (((char_length(btrim(motif)) >= 5) AND (char_length(btrim(motif)) <= 500)))
);


--
-- Name: forcage_alerte_psychotrope_id_seq; Type: SEQUENCE; Schema: public; Owner: -
--

ALTER TABLE public.forcage_alerte_psychotrope ALTER COLUMN id ADD GENERATED ALWAYS AS IDENTITY (
    SEQUENCE NAME public.forcage_alerte_psychotrope_id_seq
    START WITH 1
    INCREMENT BY 1
    NO MINVALUE
    NO MAXVALUE
    CACHE 1
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
    email text,
    actif boolean DEFAULT true NOT NULL,
    officine_id uuid DEFAULT public.officine_courante() NOT NULL,
    CONSTRAINT ck_seuils_fournisseur CHECK (((jours_rouge IS NULL) OR (jours_orange IS NULL) OR (jours_rouge < jours_orange))),
    CONSTRAINT fournisseur_delai_reprise_jours_check CHECK ((delai_reprise_jours > 0)),
    CONSTRAINT fournisseur_jours_orange_check CHECK ((jours_orange > 0)),
    CONSTRAINT fournisseur_jours_rouge_check CHECK ((jours_rouge > 0))
);


--
-- Name: COLUMN fournisseur.email; Type: COMMENT; Schema: public; Owner: -
--

COMMENT ON COLUMN public.fournisseur.email IS 'Adresse e-mail du grossiste (facultative)';


--
-- Name: COLUMN fournisseur.actif; Type: COMMENT; Schema: public; Owner: -
--

COMMENT ON COLUMN public.fournisseur.actif IS 'Faux : grossiste désactivé, plus proposé aux commandes ni aux réceptions (historique conservé)';


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
-- Name: fournisseur_mouvement; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public.fournisseur_mouvement (
    id bigint NOT NULL,
    fournisseur_id bigint NOT NULL,
    type text NOT NULL,
    montant numeric(12,2) NOT NULL,
    solde_apres numeric(12,2) NOT NULL,
    utilisateur_id bigint,
    reference text,
    cree_le timestamp with time zone DEFAULT now() NOT NULL,
    officine_id uuid DEFAULT public.officine_courante() NOT NULL,
    CONSTRAINT fournisseur_mouvement_montant_check CHECK ((montant <> (0)::numeric)),
    CONSTRAINT fournisseur_mouvement_type_check CHECK ((type = ANY (ARRAY['ouverture'::text, 'reprise'::text, 'reception'::text, 'reglement'::text, 'avoir'::text, 'correction'::text, 'annulation_reglement'::text])))
);


--
-- Name: TABLE fournisseur_mouvement; Type: COMMENT; Schema: public; Owner: -
--

COMMENT ON TABLE public.fournisseur_mouvement IS 'Relevé de la dette envers les grossistes : une ligne par mouvement de fournisseur.solde (déclencheur trg_fournisseur_dette)';


--
-- Name: fournisseur_mouvement_id_seq; Type: SEQUENCE; Schema: public; Owner: -
--

ALTER TABLE public.fournisseur_mouvement ALTER COLUMN id ADD GENERATED ALWAYS AS IDENTITY (
    SEQUENCE NAME public.fournisseur_mouvement_id_seq
    START WITH 1
    INCREMENT BY 1
    NO MINVALUE
    NO MAXVALUE
    CACHE 1
);


--
-- Name: historique_chifa; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public.historique_chifa (
    id bigint NOT NULL,
    session_id bigint NOT NULL,
    logiciel_source text NOT NULL,
    num_facture text NOT NULL,
    date_facture date NOT NULL,
    num_assure text,
    nom_assure text,
    montant_total numeric(14,2),
    part_chifa numeric(14,2),
    part_assure numeric(14,2),
    importe_le timestamp with time zone DEFAULT now() NOT NULL,
    organisme text DEFAULT 'CHIFA'::text NOT NULL,
    CONSTRAINT ck_historique_chifa_organisme CHECK ((organisme = ANY (ARRAY['CHIFA'::text, 'CNAS'::text, 'CASNOS'::text, 'MDN'::text])))
);


--
-- Name: TABLE historique_chifa; Type: COMMENT; Schema: public; Owner: -
--

COMMENT ON TABLE public.historique_chifa IS 'Factures d''organisme passées de l''ancien logiciel (Chifa, MDN ; 114, 121), en lecture seule : aucune vente recréée';


--
-- Name: COLUMN historique_chifa.part_chifa; Type: COMMENT; Schema: public; Owner: -
--

COMMENT ON COLUMN public.historique_chifa.part_chifa IS 'Part de l''organisme (Chifa ou MDN)';


--
-- Name: COLUMN historique_chifa.organisme; Type: COMMENT; Schema: public; Owner: -
--

COMMENT ON COLUMN public.historique_chifa.organisme IS 'CHIFA (CNAS ou CASNOS non précisé), CNAS, CASNOS ou MDN (121)';


--
-- Name: historique_chifa_id_seq; Type: SEQUENCE; Schema: public; Owner: -
--

ALTER TABLE public.historique_chifa ALTER COLUMN id ADD GENERATED ALWAYS AS IDENTITY (
    SEQUENCE NAME public.historique_chifa_id_seq
    START WITH 1
    INCREMENT BY 1
    NO MINVALUE
    NO MAXVALUE
    CACHE 1
);


--
-- Name: historique_organisme; Type: VIEW; Schema: public; Owner: -
--

CREATE VIEW public.historique_organisme AS
 SELECT id,
    session_id,
    logiciel_source,
    organisme,
    num_facture,
    date_facture,
    num_assure,
    nom_assure,
    montant_total,
    part_chifa AS part_organisme,
    part_assure,
    importe_le
   FROM public.historique_chifa;


--
-- Name: VIEW historique_organisme; Type: COMMENT; Schema: public; Owner: -
--

COMMENT ON VIEW public.historique_organisme IS 'Historique des organismes repris (Chifa, MDN), lecture seule (121)';


--
-- Name: import_catalogue_grossiste; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public.import_catalogue_grossiste (
    id bigint NOT NULL,
    fournisseur_id bigint NOT NULL,
    nom_fichier text NOT NULL,
    importe_le timestamp with time zone DEFAULT now() NOT NULL,
    utilisateur_id bigint,
    lignes_lues integer DEFAULT 0 NOT NULL,
    lignes_rapprochees integer DEFAULT 0 NOT NULL,
    lignes_ignorees integer DEFAULT 0 NOT NULL,
    CONSTRAINT import_catalogue_grossiste_lignes_ignorees_check CHECK ((lignes_ignorees >= 0)),
    CONSTRAINT import_catalogue_grossiste_lignes_lues_check CHECK ((lignes_lues >= 0)),
    CONSTRAINT import_catalogue_grossiste_lignes_rapprochees_check CHECK ((lignes_rapprochees >= 0))
);


--
-- Name: import_catalogue_grossiste_id_seq; Type: SEQUENCE; Schema: public; Owner: -
--

ALTER TABLE public.import_catalogue_grossiste ALTER COLUMN id ADD GENERATED ALWAYS AS IDENTITY (
    SEQUENCE NAME public.import_catalogue_grossiste_id_seq
    START WITH 1
    INCREMENT BY 1
    NO MINVALUE
    NO MAXVALUE
    CACHE 1
);


--
-- Name: indication; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public.indication (
    id bigint NOT NULL,
    libelle text NOT NULL,
    actif boolean DEFAULT true NOT NULL,
    cree_le timestamp with time zone DEFAULT now() NOT NULL,
    officine_id uuid DEFAULT public.officine_courante() NOT NULL,
    CONSTRAINT ck_indication_libelle CHECK (((length(btrim(libelle)) >= 1) AND (length(btrim(libelle)) <= 80)))
);


--
-- Name: indication_id_seq; Type: SEQUENCE; Schema: public; Owner: -
--

ALTER TABLE public.indication ALTER COLUMN id ADD GENERATED ALWAYS AS IDENTITY (
    SEQUENCE NAME public.indication_id_seq
    START WITH 1
    INCREMENT BY 1
    NO MINVALUE
    NO MAXVALUE
    CACHE 1
);


--
-- Name: integrite_essai; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public.integrite_essai (
    id bigint NOT NULL,
    libelle text NOT NULL,
    montant numeric(12,2),
    cree_le timestamp with time zone DEFAULT now() NOT NULL,
    cle_chaine text DEFAULT 'officine'::text NOT NULL,
    rang bigint,
    hash bytea,
    hash_precedent bytea
);


--
-- Name: integrite_essai_id_seq; Type: SEQUENCE; Schema: public; Owner: -
--

ALTER TABLE public.integrite_essai ALTER COLUMN id ADD GENERATED ALWAYS AS IDENTITY (
    SEQUENCE NAME public.integrite_essai_id_seq
    START WITH 1
    INCREMENT BY 1
    NO MINVALUE
    NO MAXVALUE
    CACHE 1
);


--
-- Name: integrite_prise_acte; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public.integrite_prise_acte (
    id bigint NOT NULL,
    nouvelle_epoque integer NOT NULL,
    cause text NOT NULL,
    utilisateur_id bigint NOT NULL,
    cree_le timestamp with time zone DEFAULT now() NOT NULL,
    officine_id uuid DEFAULT public.officine_courante() NOT NULL,
    CONSTRAINT integrite_prise_acte_cause_check CHECK (((length(btrim(cause)) >= 3) AND (length(btrim(cause)) <= 500))),
    CONSTRAINT integrite_prise_acte_nouvelle_epoque_check CHECK ((nouvelle_epoque >= 2))
);


--
-- Name: TABLE integrite_prise_acte; Type: COMMENT; Schema: public; Owner: -
--

COMMENT ON TABLE public.integrite_prise_acte IS 'Prise d''acte du titulaire d''une restauration constatée par le serveur central (cause), envoyée au central ; ajout seul (script 151, S6.5-D4).';


--
-- Name: integrite_prise_acte_id_seq; Type: SEQUENCE; Schema: public; Owner: -
--

ALTER TABLE public.integrite_prise_acte ALTER COLUMN id ADD GENERATED ALWAYS AS IDENTITY (
    SEQUENCE NAME public.integrite_prise_acte_id_seq
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
    produit_id bigint,
    officine_id uuid DEFAULT public.officine_courante() NOT NULL,
    CONSTRAINT inventaire_statut_check CHECK ((statut = ANY (ARRAY['en_cours'::text, 'termine'::text, 'annule'::text])))
);


--
-- Name: COLUMN inventaire.produit_id; Type: COMMENT; Schema: public; Owner: -
--

COMMENT ON COLUMN public.inventaire.produit_id IS 'Inventaire d''un seul produit (tous ses lots), demandé par une alerte ; NULL : inventaire d''un rayon';


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
    autorise_par bigint,
    officine_id uuid DEFAULT public.officine_courante() NOT NULL
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
-- Name: journal_registre_psychotrope; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public.journal_registre_psychotrope (
    id bigint NOT NULL,
    nature text NOT NULL,
    registre_id bigint,
    numero_ordre bigint,
    lot_id bigint,
    ligne_vente_id bigint,
    mouvement_id bigint,
    type_mouvement text,
    quantite integer,
    solde_apres integer,
    patient_id bigint,
    medecin_id bigint,
    ordonnance_id bigint,
    utilisateur_id bigint,
    reference text,
    porteur_nom text,
    porteur_adresse text,
    porteur_piece text,
    inscrit_le timestamp with time zone,
    lignes_anterieures bigint,
    empreinte_anterieure bytea,
    dernier_registre_id bigint,
    annulation_id bigint,
    motif text,
    cree_le timestamp with time zone DEFAULT now() NOT NULL,
    cle_chaine text DEFAULT 'officine'::text NOT NULL,
    rang bigint,
    hash bytea,
    hash_precedent bytea,
    officine_id uuid DEFAULT public.officine_courante() NOT NULL,
    CONSTRAINT ck_journal_registre_ligne CHECK (((nature = 'ouverture'::text) OR (registre_id IS NOT NULL))),
    CONSTRAINT ck_journal_registre_nature CHECK ((nature = ANY (ARRAY['ouverture'::text, 'inscription'::text, 'annulation'::text]))),
    CONSTRAINT ck_journal_registre_ouverture CHECK (((nature <> 'ouverture'::text) OR ((lignes_anterieures IS NOT NULL) AND (empreinte_anterieure IS NOT NULL))))
);


--
-- Name: journal_registre_psychotrope_id_seq; Type: SEQUENCE; Schema: public; Owner: -
--

ALTER TABLE public.journal_registre_psychotrope ALTER COLUMN id ADD GENERATED ALWAYS AS IDENTITY (
    SEQUENCE NAME public.journal_registre_psychotrope_id_seq
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
    officine_id uuid DEFAULT public.officine_courante() NOT NULL,
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
-- Name: journal_vente; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public.journal_vente (
    id bigint NOT NULL,
    evenement text NOT NULL,
    numero_ticket text,
    vente_id bigint,
    annulation_id bigint,
    retour_client_id bigint,
    avoir_mouvement_id bigint,
    regularisation_id bigint,
    survenu_le timestamp with time zone NOT NULL,
    enregistre_le timestamp with time zone DEFAULT clock_timestamp() NOT NULL,
    session_caisse_id bigint,
    poste text,
    utilisateur_id bigint,
    type_vente text,
    montant_ttc numeric(14,2) DEFAULT 0 NOT NULL,
    montant_ht numeric(14,2) DEFAULT 0 NOT NULL,
    montant_tva numeric(14,2) DEFAULT 0 NOT NULL,
    detail_tva text DEFAULT ''::text NOT NULL,
    part_patient numeric(14,2) DEFAULT 0 NOT NULL,
    part_organisme numeric(14,2) DEFAULT 0 NOT NULL,
    paiements text DEFAULT ''::text NOT NULL,
    montant_mouvement numeric(14,2) DEFAULT 0 NOT NULL,
    motif text,
    cle_chaine text DEFAULT 'officine'::text NOT NULL,
    rang bigint,
    hash bytea,
    hash_precedent bytea,
    officine_id uuid DEFAULT public.officine_courante() NOT NULL,
    CONSTRAINT ck_journal_vente_evenement CHECK ((evenement = ANY (ARRAY['ouverture'::text, 'vente'::text, 'annulation'::text, 'retour'::text, 'avoir'::text, 'regularisation'::text, 'annulation_regularisation'::text, 'saut_compteur'::text]))),
    CONSTRAINT ck_journal_vente_montants CHECK (((montant_ht + montant_tva) = montant_ttc))
);


--
-- Name: journal_vente_id_seq; Type: SEQUENCE; Schema: public; Owner: -
--

ALTER TABLE public.journal_vente ALTER COLUMN id ADD GENERATED ALWAYS AS IDENTITY (
    SEQUENCE NAME public.journal_vente_id_seq
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
    pays text,
    officine_id uuid DEFAULT public.officine_courante() NOT NULL
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
    officine_id uuid DEFAULT public.officine_courante() NOT NULL,
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
    officine_id uuid DEFAULT public.officine_courante() NOT NULL,
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
    officine_id uuid DEFAULT public.officine_courante() NOT NULL,
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
    produit_id bigint,
    lot_id bigint,
    quantite integer NOT NULL,
    prix_unitaire numeric(12,2) NOT NULL,
    base_remboursement numeric(12,2) DEFAULT 0 NOT NULL,
    taux_remboursement numeric(5,2) DEFAULT 0 NOT NULL,
    montant_organisme numeric(12,2) DEFAULT 0 NOT NULL,
    montant_patient numeric(12,2) DEFAULT 0 NOT NULL,
    shp numeric(8,2) DEFAULT 0 NOT NULL,
    raison_lot text,
    remise_auto_pct numeric(5,2) DEFAULT 0 NOT NULL,
    reduction_auto numeric(12,2) DEFAULT 0 NOT NULL,
    reduction_manuelle numeric(12,2) DEFAULT 0 NOT NULL,
    don_partiel boolean DEFAULT false NOT NULL,
    prix_chifa numeric(12,2),
    choix_prix_chifa text,
    type_prestation_id bigint,
    libelle text,
    taux_tva numeric(5,2),
    officine_id uuid DEFAULT public.officine_courante() NOT NULL,
    CONSTRAINT ck_ligne_vente_choix_prix_chifa CHECK (((choix_prix_chifa IS NULL) OR ((prix_chifa IS NOT NULL) AND (choix_prix_chifa = ANY (ARRAY['aligne_auto'::text, 'aligne'::text, 'garde'::text, 'plus_eleve'::text]))))),
    CONSTRAINT ck_ligne_vente_prix_chifa CHECK (((prix_chifa IS NULL) OR (prix_chifa >= (0)::numeric))),
    CONSTRAINT ck_ligne_vente_produit_ou_prestation CHECK ((((produit_id IS NOT NULL) AND (lot_id IS NOT NULL) AND (type_prestation_id IS NULL) AND (libelle IS NULL) AND (taux_tva IS NULL)) OR ((type_prestation_id IS NOT NULL) AND (produit_id IS NULL) AND (lot_id IS NULL) AND (libelle IS NOT NULL) AND (taux_tva IS NOT NULL) AND (taux_tva >= (0)::numeric) AND (taux_tva <= (100)::numeric) AND (shp = (0)::numeric) AND (prix_chifa IS NULL) AND (raison_lot IS NULL)))),
    CONSTRAINT ck_ligne_vente_raison_lot CHECK (((raison_lot IS NULL) OR (raison_lot = ANY (ARRAY['peremption'::text, 'introuvable'::text, 'abimee'::text, 'demande_client'::text])))),
    CONSTRAINT ck_ligne_vente_reduction_auto CHECK ((reduction_auto >= (0)::numeric)),
    CONSTRAINT ck_ligne_vente_reduction_manuelle CHECK ((reduction_manuelle >= (0)::numeric)),
    CONSTRAINT ck_ligne_vente_remise_auto CHECK (((remise_auto_pct >= (0)::numeric) AND (remise_auto_pct <= (100)::numeric))),
    CONSTRAINT ck_ligne_vente_shp CHECK ((shp >= (0)::numeric)),
    CONSTRAINT ligne_vente_quantite_check CHECK ((quantite > 0)),
    CONSTRAINT ligne_vente_taux_remboursement_check CHECK (((taux_remboursement >= (0)::numeric) AND (taux_remboursement <= (100)::numeric)))
);


--
-- Name: COLUMN ligne_vente.shp; Type: COMMENT; Schema: public; Owner: -
--

COMMENT ON COLUMN public.ligne_vente.shp IS 'SHP par boîte, compris dans prix_unitaire';


--
-- Name: COLUMN ligne_vente.raison_lot; Type: COMMENT; Schema: public; Owner: -
--

COMMENT ON COLUMN public.ligne_vente.raison_lot IS 'Lot plus récent servi alors qu''un lot plus ancien est en stock : peremption (vérifiée), introuvable, abimee, demande_client';


--
-- Name: COLUMN ligne_vente.remise_auto_pct; Type: COMMENT; Schema: public; Owner: -
--

COMMENT ON COLUMN public.ligne_vente.remise_auto_pct IS 'Remise automatique appliquée à la ligne (%)';


--
-- Name: COLUMN ligne_vente.reduction_auto; Type: COMMENT; Schema: public; Owner: -
--

COMMENT ON COLUMN public.ligne_vente.reduction_auto IS 'Réduction due à la remise automatique (DA), sur la part du patient hors SHP';


--
-- Name: COLUMN ligne_vente.reduction_manuelle; Type: COMMENT; Schema: public; Owner: -
--

COMMENT ON COLUMN public.ligne_vente.reduction_manuelle IS 'Réduction due à la remise du vendeur (DA), sur la part du patient hors SHP';


--
-- Name: COLUMN ligne_vente.don_partiel; Type: COMMENT; Schema: public; Owner: -
--

COMMENT ON COLUMN public.ligne_vente.don_partiel IS 'Médicament : les réductions de la ligne sont un don partiel du reste à charge, pas une remise';


--
-- Name: COLUMN ligne_vente.prix_chifa; Type: COMMENT; Schema: public; Owner: -
--

COMMENT ON COLUMN public.ligne_vente.prix_chifa IS 'Prix unitaire lu sur le volet Chifa (NULL : ligne hors volet)';


--
-- Name: COLUMN ligne_vente.choix_prix_chifa; Type: COMMENT; Schema: public; Owner: -
--

COMMENT ON COLUMN public.ligne_vente.choix_prix_chifa IS 'Prix retenu quand il diffère du prix Chifa : aligne_auto, aligne, garde ou plus_eleve';


--
-- Name: COLUMN ligne_vente.type_prestation_id; Type: COMMENT; Schema: public; Owner: -
--

COMMENT ON COLUMN public.ligne_vente.type_prestation_id IS 'Prestation vendue (script 105) ; NULL pour une ligne produit';


--
-- Name: COLUMN ligne_vente.libelle; Type: COMMENT; Schema: public; Owner: -
--

COMMENT ON COLUMN public.ligne_vente.libelle IS 'Libellé de la prestation figé à la vente (script 105) ; NULL pour une ligne produit';


--
-- Name: COLUMN ligne_vente.taux_tva; Type: COMMENT; Schema: public; Owner: -
--

COMMENT ON COLUMN public.ligne_vente.taux_tva IS 'Taux de TVA de la prestation figé à la vente (script 105) ; NULL pour une ligne produit (TVA du produit)';


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
    ppa numeric(12,2),
    shp numeric(10,2) DEFAULT 0 NOT NULL,
    officine_id uuid DEFAULT public.officine_courante() NOT NULL,
    CONSTRAINT ck_lot_ppa CHECK (((ppa IS NULL) OR (ppa >= (0)::numeric))),
    CONSTRAINT ck_lot_shp CHECK ((shp >= (0)::numeric)),
    CONSTRAINT lot_quantite_disponible_check CHECK ((quantite_disponible >= 0))
);


--
-- Name: COLUMN lot.prix_achat_ht; Type: COMMENT; Schema: public; Owner: -
--

COMMENT ON COLUMN public.lot.prix_achat_ht IS 'Prix de la première livraison du lot ; chaque boîte identifiée garde le sien (boite.prix_achat_ht)';


--
-- Name: COLUMN lot.reception_id; Type: COMMENT; Schema: public; Owner: -
--

COMMENT ON COLUMN public.lot.reception_id IS 'Première réception du lot ; chaque boîte et chaque entrée gardent la leur';


--
-- Name: COLUMN lot.ppa; Type: COMMENT; Schema: public; Owner: -
--

COMMENT ON COLUMN public.lot.ppa IS 'Prix public facturé du lot (vignette) ; null : prix public du catalogue';


--
-- Name: COLUMN lot.shp; Type: COMMENT; Schema: public; Owner: -
--

COMMENT ON COLUMN public.lot.shp IS 'SHP par boîte du lot (script 103) : repris des lignes de réception, 0 sans ligne ; mis à jour à chaque réception du lot';


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
    code_prestataire text,
    telephone text,
    adresse text,
    email text,
    numero_ordre text,
    wilaya text,
    officine_id uuid DEFAULT public.officine_courante() NOT NULL,
    CONSTRAINT ck_medecin_coordonnees CHECK ((((telephone IS NULL) OR (length(telephone) <= 30)) AND ((adresse IS NULL) OR (length(adresse) <= 300)) AND ((email IS NULL) OR (length(email) <= 120)) AND ((numero_ordre IS NULL) OR (length(numero_ordre) <= 30)) AND ((code_prestataire IS NULL) OR (length(code_prestataire) <= 30))))
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
    signale_le timestamp with time zone,
    CONSTRAINT message_sortant_canal_check CHECK ((canal = ANY (ARRAY['email'::text, 'sms'::text]))),
    CONSTRAINT message_sortant_statut_check CHECK ((statut = ANY (ARRAY['en_attente'::text, 'envoye'::text, 'echec'::text]))),
    CONSTRAINT message_sortant_type_check CHECK ((type = ANY (ARRAY['rapport_quotidien'::text, 'ecart_caisse'::text, 'lot_bloque'::text, 'sauvegarde'::text, 'ecart_inventaire'::text, 'lot_delaisse'::text, 'surveillance'::text, 'synchronisation'::text, 'reglages_centraux'::text, 'test'::text])))
);


--
-- Name: COLUMN message_sortant.signale_le; Type: COMMENT; Schema: public; Owner: -
--

COMMENT ON COLUMN public.message_sortant.signale_le IS 'Envoi en échec signalé au titulaire (alerte_non_envoyee) : après un jour d''essais, ou à l''abandon ; une seule fois';


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
-- Name: motif_reglable; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public.motif_reglable (
    id bigint NOT NULL,
    liste text NOT NULL,
    code text NOT NULL,
    libelle text NOT NULL,
    actif boolean DEFAULT true NOT NULL,
    systeme boolean DEFAULT false NOT NULL,
    ordre integer DEFAULT 100 NOT NULL,
    cree_le timestamp with time zone DEFAULT now() NOT NULL,
    modifie_le timestamp with time zone DEFAULT now() NOT NULL,
    effet text DEFAULT 'aucun'::text NOT NULL,
    detail text DEFAULT 'aucun'::text NOT NULL,
    officine_id uuid DEFAULT public.officine_courante() NOT NULL,
    modele_code_central boolean DEFAULT false NOT NULL,
    CONSTRAINT ck_motif_reglable_effet CHECK ((((effet = 'aucun'::text) AND (detail = 'aucun'::text)) OR ((effet = 'sortie'::text) AND (detail = ANY (ARRAY['depense'::text, 'fournisseur'::text, 'patient'::text, 'coffre'::text]))) OR ((effet = 'entree'::text) AND (detail = 'fond'::text)))),
    CONSTRAINT ck_motif_reglable_effet_tiroir CHECK (((effet = 'aucun'::text) OR (liste = 'tiroir'::text))),
    CONSTRAINT ck_motif_reglable_liste CHECK ((liste = ANY ('{attente,charge,chifa_exclusion,majoration_chifa,rejet_chifa,retour_client,retour_fournisseur,sortie_stock,tiroir,volet_absent}'::text[]))),
    CONSTRAINT ck_motif_reglable_systeme_actif CHECK ((actif OR (NOT systeme))),
    CONSTRAINT motif_reglable_code_check CHECK ((code ~ '^[a-z0-9_]{1,40}$'::text)),
    CONSTRAINT motif_reglable_libelle_check CHECK (((length(btrim(libelle)) >= 1) AND (length(btrim(libelle)) <= 80)))
);


--
-- Name: TABLE motif_reglable; Type: COMMENT; Schema: public; Owner: -
--

COMMENT ON TABLE public.motif_reglable IS 'Motifs et catégories réglables par le titulaire (tiroir, mise en attente, charges, sorties de stock, retours) ; désactivés, jamais supprimés';


--
-- Name: COLUMN motif_reglable.effet; Type: COMMENT; Schema: public; Owner: -
--

COMMENT ON COLUMN public.motif_reglable.effet IS 'Tiroir : aucun, sortie ou entree d''argent (montant obligatoire hors « aucun »)';


--
-- Name: COLUMN motif_reglable.detail; Type: COMMENT; Schema: public; Owner: -
--

COMMENT ON COLUMN public.motif_reglable.detail IS 'Tiroir : détail exigé : depense (catégorie de charge), fournisseur (grossiste ou nom), patient, coffre (retrait vers le coffre), fond (remise d''un fonds de caisse)';


--
-- Name: COLUMN motif_reglable.modele_code_central; Type: COMMENT; Schema: public; Owner: -
--

COMMENT ON COLUMN public.motif_reglable.modele_code_central IS 'Vrai pour un motif créé ou réglé par le modèle du serveur central : verrouillé en mode imposé (script 141).';


--
-- Name: motif_reglable_id_seq; Type: SEQUENCE; Schema: public; Owner: -
--

ALTER TABLE public.motif_reglable ALTER COLUMN id ADD GENERATED ALWAYS AS IDENTITY (
    SEQUENCE NAME public.motif_reglable_id_seq
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
    officine_id uuid DEFAULT public.officine_courante() NOT NULL,
    CONSTRAINT mouvement_stock_type_check CHECK ((type = ANY (ARRAY['entree'::text, 'vente'::text, 'retour_client'::text, 'retour_fournisseur'::text, 'perime'::text, 'casse'::text, 'ajustement_inventaire'::text, 'destruction'::text, 'echantillon'::text, 'manquant_reception'::text])))
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
-- Name: mouvement_tiroir; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public.mouvement_tiroir (
    id bigint NOT NULL,
    numero text NOT NULL,
    evenement_caisse_id bigint,
    session_caisse_id bigint NOT NULL,
    utilisateur_id bigint NOT NULL,
    poste text,
    motif_code text NOT NULL,
    motif_libelle text NOT NULL,
    sens text NOT NULL,
    detail text NOT NULL,
    montant numeric(12,2) NOT NULL,
    categorie text,
    libelle text,
    fournisseur_id bigint,
    beneficiaire text,
    reference text,
    patient_id bigint,
    raison text,
    reglement_fournisseur_id bigint,
    mouvement_tresorerie_id bigint,
    valide_par bigint,
    corrige_id bigint,
    motif_correction text,
    cree_le timestamp with time zone DEFAULT now() NOT NULL,
    regularisation_id bigint,
    officine_id uuid DEFAULT public.officine_courante() NOT NULL,
    CONSTRAINT ck_mouvement_tiroir_correction CHECK (((corrige_id IS NULL) = (motif_correction IS NULL))),
    CONSTRAINT ck_mouvement_tiroir_depense CHECK (((detail <> 'depense'::text) OR (corrige_id IS NOT NULL) OR ((categorie IS NOT NULL) AND (libelle IS NOT NULL)))),
    CONSTRAINT ck_mouvement_tiroir_fournisseur CHECK (((detail <> 'fournisseur'::text) OR (corrige_id IS NOT NULL) OR (fournisseur_id IS NOT NULL) OR (beneficiaire IS NOT NULL))),
    CONSTRAINT ck_mouvement_tiroir_patient CHECK (((detail <> 'patient'::text) OR (corrige_id IS NOT NULL) OR ((patient_id IS NOT NULL) AND (raison IS NOT NULL)))),
    CONSTRAINT ck_mouvement_tiroir_regularisation CHECK (((regularisation_id IS NULL) OR ((detail = 'patient'::text) AND (sens = 'sortie'::text) AND (corrige_id IS NULL)))),
    CONSTRAINT mouvement_tiroir_detail_check CHECK ((detail = ANY (ARRAY['depense'::text, 'fournisseur'::text, 'patient'::text, 'coffre'::text, 'fond'::text]))),
    CONSTRAINT mouvement_tiroir_montant_check CHECK ((montant > (0)::numeric)),
    CONSTRAINT mouvement_tiroir_sens_check CHECK ((sens = ANY (ARRAY['sortie'::text, 'entree'::text])))
);


--
-- Name: TABLE mouvement_tiroir; Type: COMMENT; Schema: public; Owner: -
--

COMMENT ON TABLE public.mouvement_tiroir IS 'Sorties et entrées d''argent par le tiroir (F8, motif à effet) ; jamais supprimées : correction par écriture inverse (corrige_id)';


--
-- Name: COLUMN mouvement_tiroir.reglement_fournisseur_id; Type: COMMENT; Schema: public; Owner: -
--

COMMENT ON COLUMN public.mouvement_tiroir.reglement_fournisseur_id IS 'Paiement d''un grossiste : règlement en espèces pris dans le tiroir (part_tiroir), compté dans l''attendu par reglement_fournisseur';


--
-- Name: COLUMN mouvement_tiroir.mouvement_tresorerie_id; Type: COMMENT; Schema: public; Owner: -
--

COMMENT ON COLUMN public.mouvement_tiroir.mouvement_tresorerie_id IS 'Retrait vers le coffre ou remise d''un fonds : mouvement du coffre pendant la session, compté dans l''attendu par mouvement_tresorerie';


--
-- Name: COLUMN mouvement_tiroir.regularisation_id; Type: COMMENT; Schema: public; Owner: -
--

COMMENT ON COLUMN public.mouvement_tiroir.regularisation_id IS 'Remboursement du trop-payé « à rendre » de cette régularisation Chifa (script 099) ; rendu tant que le bon n''est pas corrigé';


--
-- Name: mouvement_tiroir_id_seq; Type: SEQUENCE; Schema: public; Owner: -
--

ALTER TABLE public.mouvement_tiroir ALTER COLUMN id ADD GENERATED ALWAYS AS IDENTITY (
    SEQUENCE NAME public.mouvement_tiroir_id_seq
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
    officine_id uuid DEFAULT public.officine_courante() NOT NULL,
    CONSTRAINT mouvement_tresorerie_check CHECK (((nature <> 'versement_organisme'::text) OR (organisme_id IS NOT NULL))),
    CONSTRAINT mouvement_tresorerie_check1 CHECK (((nature <> 'versement_caisse'::text) OR ((session_caisse_id IS NOT NULL) AND (sens = 'entree'::text)))),
    CONSTRAINT mouvement_tresorerie_check2 CHECK (((nature <> 'depense'::text) OR ((sens = 'sortie'::text) AND (categorie IS NOT NULL)))),
    CONSTRAINT mouvement_tresorerie_montant_check CHECK ((montant > (0)::numeric)),
    CONSTRAINT mouvement_tresorerie_nature_check CHECK ((nature = ANY (ARRAY['depense'::text, 'encaissement'::text, 'versement_organisme'::text, 'transfert'::text, 'versement_caisse'::text, 'financement'::text, 'remboursement_financement'::text]))),
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
    officine_id uuid DEFAULT public.officine_courante() NOT NULL,
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
    importee_le timestamp with time zone DEFAULT now() NOT NULL,
    officine_id uuid DEFAULT public.officine_courante() NOT NULL
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
    actif boolean DEFAULT true NOT NULL,
    telephone text,
    email text,
    delai_paiement_jours integer DEFAULT 30 NOT NULL,
    officine_id uuid DEFAULT public.officine_courante() NOT NULL,
    CONSTRAINT ck_organisme_delai_paiement CHECK (((delai_paiement_jours >= 1) AND (delai_paiement_jours <= 365)))
);


--
-- Name: COLUMN organisme.telephone; Type: COMMENT; Schema: public; Owner: -
--

COMMENT ON COLUMN public.organisme.telephone IS 'Téléphone de l''organisme (facultatif)';


--
-- Name: COLUMN organisme.email; Type: COMMENT; Schema: public; Owner: -
--

COMMENT ON COLUMN public.organisme.email IS 'Adresse e-mail de l''organisme (facultative)';


--
-- Name: COLUMN organisme.delai_paiement_jours; Type: COMMENT; Schema: public; Owner: -
--

COMMENT ON COLUMN public.organisme.delai_paiement_jours IS 'Délai de paiement des factures de l''organisme : échéance proposée (jours)';


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
    numero_recu text,
    officine_id uuid DEFAULT public.officine_courante() NOT NULL,
    CONSTRAINT paiement_check CHECK (((vente_id IS NOT NULL) OR (patient_id IS NOT NULL))),
    CONSTRAINT paiement_mode_check CHECK ((mode = ANY (ARRAY['especes'::text, 'carte'::text, 'cheque'::text, 'credit'::text, 'remboursement_credit'::text, 'avoir'::text]))),
    CONSTRAINT paiement_montant_check CHECK ((montant > (0)::numeric))
);


--
-- Name: COLUMN paiement.numero_recu; Type: COMMENT; Schema: public; Owner: -
--

COMMENT ON COLUMN public.paiement.numero_recu IS 'Numéro du reçu de règlement de crédit (RC-000001), null pour un paiement de vente';


--
-- Name: paiement_echeance_pret; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public.paiement_echeance_pret (
    id bigint NOT NULL,
    echeance_id bigint NOT NULL,
    date_paiement date NOT NULL,
    moyen text NOT NULL,
    compte_id bigint,
    reference text,
    part_tiroir numeric(12,2) DEFAULT 0 NOT NULL,
    versement_id bigint,
    mouvement_capital_id bigint,
    mouvement_charges_id bigint,
    utilisateur_id bigint NOT NULL,
    cree_le timestamp with time zone DEFAULT now() NOT NULL,
    officine_id uuid DEFAULT public.officine_courante() NOT NULL,
    CONSTRAINT paiement_echeance_pret_check CHECK (((moyen = 'especes'::text) OR ((compte_id IS NOT NULL) AND (reference IS NOT NULL)))),
    CONSTRAINT paiement_echeance_pret_check1 CHECK (((part_tiroir = (0)::numeric) OR ((moyen = 'especes'::text) AND (versement_id IS NOT NULL)))),
    CONSTRAINT paiement_echeance_pret_moyen_check CHECK ((moyen = ANY (ARRAY['especes'::text, 'cheque'::text, 'virement'::text, 'prelevement'::text]))),
    CONSTRAINT paiement_echeance_pret_part_tiroir_check CHECK ((part_tiroir >= (0)::numeric))
);


--
-- Name: TABLE paiement_echeance_pret; Type: COMMENT; Schema: public; Owner: -
--

COMMENT ON TABLE public.paiement_echeance_pret IS 'Échéance de prêt payée, confirmée par le titulaire : capital (remboursement_financement) et charges financières (intérêts, assurance, TVA, marge : dépense) sortis du coffre ou de la banque choisie';


--
-- Name: paiement_echeance_pret_id_seq; Type: SEQUENCE; Schema: public; Owner: -
--

ALTER TABLE public.paiement_echeance_pret ALTER COLUMN id ADD GENERATED ALWAYS AS IDENTITY (
    SEQUENCE NAME public.paiement_echeance_pret_id_seq
    START WITH 1
    INCREMENT BY 1
    NO MINVALUE
    NO MAXVALUE
    CACHE 1
);


--
-- Name: paiement_effet; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public.paiement_effet (
    id bigint NOT NULL,
    reglement_id bigint NOT NULL,
    date_paiement date NOT NULL,
    moyen text NOT NULL,
    compte_id bigint,
    reference text,
    part_tiroir numeric(12,2) DEFAULT 0 NOT NULL,
    versement_id bigint,
    utilisateur_id bigint NOT NULL,
    cree_le timestamp with time zone DEFAULT now() NOT NULL,
    officine_id uuid DEFAULT public.officine_courante() NOT NULL,
    CONSTRAINT paiement_effet_check CHECK (((moyen = ANY (ARRAY['especes'::text, 'financement'::text])) OR (compte_id IS NOT NULL))),
    CONSTRAINT paiement_effet_check1 CHECK (((part_tiroir = (0)::numeric) OR ((moyen = 'especes'::text) AND (versement_id IS NOT NULL)))),
    CONSTRAINT paiement_effet_moyen_check CHECK ((moyen = ANY (ARRAY['especes'::text, 'cheque'::text, 'virement'::text, 'prelevement'::text, 'financement'::text]))),
    CONSTRAINT paiement_effet_part_tiroir_check CHECK ((part_tiroir >= (0)::numeric))
);


--
-- Name: TABLE paiement_effet; Type: COMMENT; Schema: public; Owner: -
--

COMMENT ON TABLE public.paiement_effet IS 'Traite payée au grossiste à son échéance, confirmée par le titulaire ; débitée du coffre (espèces) ou de la banque choisie, sauf traite remise avec une banque (déjà débitée à l''échéance)';


--
-- Name: COLUMN paiement_effet.part_tiroir; Type: COMMENT; Schema: public; Owner: -
--

COMMENT ON COLUMN public.paiement_effet.part_tiroir IS 'Espèces : complément pris dans le tiroir de la caisse ouverte et porté au coffre (versement_id) avant le paiement';


--
-- Name: paiement_effet_id_seq; Type: SEQUENCE; Schema: public; Owner: -
--

ALTER TABLE public.paiement_effet ALTER COLUMN id ADD GENERATED ALWAYS AS IDENTITY (
    SEQUENCE NAME public.paiement_effet_id_seq
    START WITH 1
    INCREMENT BY 1
    NO MINVALUE
    NO MAXVALUE
    CACHE 1
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
    motif_code text,
    motif_texte text,
    produits_dus jsonb,
    CONSTRAINT panier_attente_lignes_check CHECK ((lignes > 0))
);


--
-- Name: TABLE panier_attente; Type: COMMENT; Schema: public; Owner: -
--

COMMENT ON TABLE public.panier_attente IS 'Ventes en attente de la caisse d''un poste (contenu du panier, repris puis supprimé)';


--
-- Name: COLUMN panier_attente.motif_code; Type: COMMENT; Schema: public; Owner: -
--

COMMENT ON COLUMN public.panier_attente.motif_code IS 'Motif de la mise en attente (motif_reglable, liste attente) ; null : mise en attente automatique (départ de la caisse, changement d''utilisateur) ou antérieure à 094';


--
-- Name: COLUMN panier_attente.produits_dus; Type: COMMENT; Schema: public; Owner: -
--

COMMENT ON COLUMN public.panier_attente.produits_dus IS 'Produits dus au patient (motif produit_manquant) : [{"ProduitId","Nom","Quantite"}] ; aucune commande créée';


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
    seuil_lot_delaisse integer DEFAULT 5 NOT NULL,
    seuil_lot_delaisse_collectif integer DEFAULT 10 NOT NULL,
    officine_id uuid DEFAULT public.officine_courante() NOT NULL,
    CONSTRAINT ck_parametre_alerte_lot_delaisse CHECK ((seuil_lot_delaisse >= 1)),
    CONSTRAINT ck_parametre_alerte_lot_delaisse_collectif CHECK ((seuil_lot_delaisse_collectif >= 1)),
    CONSTRAINT ck_parametre_alerte_positifs CHECK (((jours_orange > 0) AND (jours_rouge > 0) AND (horizon_risque_jours > 0) AND (fenetre_conso_jours > 0) AND (delai_fournisseur_jours >= 0) AND (jours_securite >= 0) AND (jours_cible_commande > 0) AND (jours_dormant > 0) AND (jours_surstock > 0))),
    CONSTRAINT ck_seuils_general CHECK ((jours_rouge < jours_orange)),
    CONSTRAINT parametre_alerte_id_check CHECK ((id = 1))
);


--
-- Name: COLUMN parametre_alerte.seuil_lot_delaisse; Type: COMMENT; Schema: public; Owner: -
--

COMMENT ON COLUMN public.parametre_alerte.seuil_lot_delaisse IS 'Boîtes d''un lot plus récent vendues par un même vendeur, l''ancien lot n''étant plus vendu, avant l''alerte lot_delaisse';


--
-- Name: COLUMN parametre_alerte.seuil_lot_delaisse_collectif; Type: COMMENT; Schema: public; Owner: -
--

COMMENT ON COLUMN public.parametre_alerte.seuil_lot_delaisse_collectif IS 'Boîtes d''un lot plus récent vendues par plusieurs vendeurs ensemble, l''ancien lot n''étant plus vendu, avant l''alerte lot_delaisse urgente';


--
-- Name: parametre_caisse; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public.parametre_caisse (
    id integer DEFAULT 1 NOT NULL,
    comptage_aveugle boolean DEFAULT true NOT NULL,
    fond_caisse_defaut numeric(12,2) DEFAULT 5000 NOT NULL,
    seuil_ecart numeric(12,2) DEFAULT 500 NOT NULL,
    deconnexion_minutes integer DEFAULT 15 NOT NULL,
    versement_cloture text DEFAULT 'manuel'::text NOT NULL,
    delai_alerte_dette_jours integer DEFAULT 30 NOT NULL,
    plafond_credit_defaut numeric(12,2) DEFAULT 0 NOT NULL,
    seuil_annulations integer DEFAULT 0 NOT NULL,
    autorisation_par_code boolean DEFAULT true NOT NULL,
    seuil_ecart_chifa numeric(12,2) DEFAULT 5 NOT NULL,
    seuil_sortie_tiroir numeric(12,2) DEFAULT 2000 NOT NULL,
    delai_volet_chifa_jours integer DEFAULT 7 NOT NULL,
    jours_alerte_fin_droit integer DEFAULT 30 NOT NULL,
    chifa_delai_depot_jours integer DEFAULT 7 NOT NULL,
    chifa_delai_paiement_jours integer DEFAULT 45 NOT NULL,
    reprise_exiger_produits_historique boolean DEFAULT true NOT NULL,
    officine_id uuid DEFAULT public.officine_courante() NOT NULL,
    taches_laissees_poste text DEFAULT 'chiffre'::text NOT NULL,
    CONSTRAINT ck_delai_alerte_dette CHECK (((delai_alerte_dette_jours >= 1) AND (delai_alerte_dette_jours <= 365))),
    CONSTRAINT ck_parametre_caisse_chifa_delai_depot CHECK (((chifa_delai_depot_jours >= 1) AND (chifa_delai_depot_jours <= 365))),
    CONSTRAINT ck_parametre_caisse_chifa_delai_paiement CHECK (((chifa_delai_paiement_jours >= 1) AND (chifa_delai_paiement_jours <= 365))),
    CONSTRAINT ck_parametre_caisse_delai_volet_chifa CHECK (((delai_volet_chifa_jours >= 1) AND (delai_volet_chifa_jours <= 365))),
    CONSTRAINT ck_parametre_caisse_jours_alerte_fin_droit CHECK (((jours_alerte_fin_droit >= 0) AND (jours_alerte_fin_droit <= 365))),
    CONSTRAINT ck_parametre_caisse_seuil_sortie_tiroir CHECK ((seuil_sortie_tiroir >= (0)::numeric)),
    CONSTRAINT ck_parametre_caisse_taches_laissees_poste CHECK ((taches_laissees_poste = ANY (ARRAY['chiffre'::text, 'desactive'::text]))),
    CONSTRAINT ck_plafond_credit_defaut CHECK ((plafond_credit_defaut >= (0)::numeric)),
    CONSTRAINT ck_seuil_annulations CHECK (((seuil_annulations >= 0) AND (seuil_annulations <= 100))),
    CONSTRAINT ck_seuil_ecart_chifa CHECK ((seuil_ecart_chifa >= (0)::numeric)),
    CONSTRAINT ck_versement_cloture CHECK ((versement_cloture = ANY (ARRAY['manuel'::text, 'automatique'::text]))),
    CONSTRAINT parametre_caisse_deconnexion_minutes_check CHECK ((deconnexion_minutes >= 0)),
    CONSTRAINT parametre_caisse_fond_caisse_defaut_check CHECK ((fond_caisse_defaut >= (0)::numeric)),
    CONSTRAINT parametre_caisse_id_check CHECK ((id = 1)),
    CONSTRAINT parametre_caisse_seuil_ecart_check CHECK ((seuil_ecart >= (0)::numeric))
);


--
-- Name: COLUMN parametre_caisse.delai_alerte_dette_jours; Type: COMMENT; Schema: public; Owner: -
--

COMMENT ON COLUMN public.parametre_caisse.delai_alerte_dette_jours IS 'Ancienneté (jours) à partir de laquelle une dette ou une créance est signalée';


--
-- Name: COLUMN parametre_caisse.plafond_credit_defaut; Type: COMMENT; Schema: public; Owner: -
--

COMMENT ON COLUMN public.parametre_caisse.plafond_credit_defaut IS 'Plafond de crédit proposé à la création d''un patient (DA)';


--
-- Name: COLUMN parametre_caisse.seuil_annulations; Type: COMMENT; Schema: public; Owner: -
--

COMMENT ON COLUMN public.parametre_caisse.seuil_annulations IS 'Alerte au-delà de ce nombre d''annulations de vente par session (0 : pas d''alerte)';


--
-- Name: COLUMN parametre_caisse.autorisation_par_code; Type: COMMENT; Schema: public; Owner: -
--

COMMENT ON COLUMN public.parametre_caisse.autorisation_par_code IS 'Une action refusée faute de droit peut être autorisée par le code comptoir d''un employé qui a ce droit';


--
-- Name: COLUMN parametre_caisse.seuil_ecart_chifa; Type: COMMENT; Schema: public; Owner: -
--

COMMENT ON COLUMN public.parametre_caisse.seuil_ecart_chifa IS 'Écart de prix par boîte (DA) sous lequel une ligne d''un volet Chifa est alignée d''office sur le prix Chifa';


--
-- Name: COLUMN parametre_caisse.seuil_sortie_tiroir; Type: COMMENT; Schema: public; Owner: -
--

COMMENT ON COLUMN public.parametre_caisse.seuil_sortie_tiroir IS 'Sortie d''argent par le tiroir au-delà de ce montant : validation d''un ayant droit (caisse.cloturer, code comptoir possible)';


--
-- Name: COLUMN parametre_caisse.delai_volet_chifa_jours; Type: COMMENT; Schema: public; Owner: -
--

COMMENT ON COLUMN public.parametre_caisse.delai_volet_chifa_jours IS 'Ventes Chifa en attente de volet proposées en priorité (jours) ; au-delà : « en retard », toujours rattachables';


--
-- Name: COLUMN parametre_caisse.jours_alerte_fin_droit; Type: COMMENT; Schema: public; Owner: -
--

COMMENT ON COLUMN public.parametre_caisse.jours_alerte_fin_droit IS 'Jours avant la fin des droits Chifa à partir desquels la caisse prévient (orange) ; 0 à 365, défaut 30 (script 107)';


--
-- Name: COLUMN parametre_caisse.chifa_delai_depot_jours; Type: COMMENT; Schema: public; Owner: -
--

COMMENT ON COLUMN public.parametre_caisse.chifa_delai_depot_jours IS 'Vente Chifa/MDN à déposer signalée en retard au-delà (jours, script 110)';


--
-- Name: COLUMN parametre_caisse.chifa_delai_paiement_jours; Type: COMMENT; Schema: public; Owner: -
--

COMMENT ON COLUMN public.parametre_caisse.chifa_delai_paiement_jours IS 'Bordereau déposé non soldé signalé impayé au-delà (jours après le dépôt, script 110)';


--
-- Name: COLUMN parametre_caisse.reprise_exiger_produits_historique; Type: COMMENT; Schema: public; Owner: -
--

COMMENT ON COLUMN public.parametre_caisse.reprise_exiger_produits_historique IS 'Reprise : fichier des produits exigé pour importer l''historique des organismes seul (Chifa, MDN) ; toujours exigé avec stock, ventes, patients, crédits (121)';


--
-- Name: COLUMN parametre_caisse.taches_laissees_poste; Type: COMMENT; Schema: public; Owner: -
--

COMMENT ON COLUMN public.parametre_caisse.taches_laissees_poste IS 'Facture et retour fournisseur laissés au changement d''utilisateur : chiffre (fichier du poste chiffré AES-256, compte Windows, défaut) ou desactive (mémoire seule) ; titulaire seul (156)';


--
-- Name: parametre_fiscal; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public.parametre_fiscal (
    id smallint DEFAULT 1 NOT NULL,
    conservation_annees smallint DEFAULT 6 NOT NULL,
    format_export_dgi text DEFAULT 'csv-v1'::text NOT NULL,
    separateur_csv text DEFAULT ';'::text NOT NULL,
    fuseau text DEFAULT 'Africa/Algiers'::text NOT NULL,
    officine_id uuid DEFAULT public.officine_courante() NOT NULL,
    CONSTRAINT ck_parametre_fiscal_conservation CHECK (((conservation_annees >= 6) AND (conservation_annees <= 30))),
    CONSTRAINT ck_parametre_fiscal_format CHECK ((format_export_dgi = 'csv-v1'::text)),
    CONSTRAINT ck_parametre_fiscal_separateur CHECK ((separateur_csv = ANY (ARRAY[';'::text, ','::text, '	'::text]))),
    CONSTRAINT parametre_fiscal_id_check CHECK ((id = 1))
);


--
-- Name: parametre_formulaire_sms; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public.parametre_formulaire_sms (
    id integer DEFAULT 1 NOT NULL,
    texte text,
    modifie_le timestamp with time zone DEFAULT now() NOT NULL,
    modifie_par bigint,
    officine_id uuid DEFAULT public.officine_courante() NOT NULL,
    CONSTRAINT parametre_formulaire_sms_id_check CHECK ((id = 1)),
    CONSTRAINT parametre_formulaire_sms_texte_check CHECK (((texte IS NULL) OR ((char_length(texte) >= 1) AND (char_length(texte) <= 4000))))
);


--
-- Name: parametre_gs1_ai; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public.parametre_gs1_ai (
    code text NOT NULL,
    champ text NOT NULL,
    longueur_fixe integer,
    longueur_max integer,
    libelle text NOT NULL,
    actif boolean DEFAULT true NOT NULL,
    officine_id uuid DEFAULT public.officine_courante() NOT NULL,
    CONSTRAINT parametre_gs1_ai_champ_check CHECK ((champ = ANY (ARRAY['gtin'::text, 'peremption'::text, 'lot'::text, 'serie'::text]))),
    CONSTRAINT parametre_gs1_ai_code_check CHECK ((code ~ '^[0-9]{2,4}$'::text)),
    CONSTRAINT parametre_gs1_ai_longueur_fixe_check CHECK ((longueur_fixe > 0)),
    CONSTRAINT parametre_gs1_ai_longueur_max_check CHECK ((longueur_max > 0))
);


--
-- Name: parametre_integrite; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public.parametre_integrite (
    id smallint DEFAULT 1 NOT NULL,
    algorithme_defaut text DEFAULT 'sha256'::text NOT NULL,
    heure_verification smallint DEFAULT 3 NOT NULL,
    format_export_sceau text DEFAULT 'hex'::text NOT NULL,
    CONSTRAINT ck_parametre_integrite_algorithme CHECK ((algorithme_defaut = ANY (ARRAY['sha256'::text, 'sha512'::text]))),
    CONSTRAINT ck_parametre_integrite_format CHECK ((format_export_sceau = ANY (ARRAY['hex'::text, 'base64'::text]))),
    CONSTRAINT ck_parametre_integrite_heure CHECK (((heure_verification >= 0) AND (heure_verification <= 23))),
    CONSTRAINT parametre_integrite_id_check CHECK ((id = 1))
);


--
-- Name: parametre_inventaire; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public.parametre_inventaire (
    id integer DEFAULT 1 NOT NULL,
    seuil_boites integer DEFAULT 2 NOT NULL,
    seuil_montant numeric(12,2) DEFAULT 2000 NOT NULL,
    officine_id uuid DEFAULT public.officine_courante() NOT NULL,
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
    signaler_sans_destinataire boolean DEFAULT true NOT NULL,
    officine_id uuid DEFAULT public.officine_courante() NOT NULL,
    CONSTRAINT parametre_notification_id_check CHECK ((id = 1)),
    CONSTRAINT parametre_notification_sms_seuil_ecart_check CHECK ((sms_seuil_ecart >= (0)::numeric)),
    CONSTRAINT parametre_notification_smtp_port_check CHECK (((smtp_port >= 1) AND (smtp_port <= 65535)))
);


--
-- Name: COLUMN parametre_notification.signaler_sans_destinataire; Type: COMMENT; Schema: public; Owner: -
--

COMMENT ON COLUMN public.parametre_notification.signaler_sans_destinataire IS 'Alerte (ou rapport du soir) partie vers aucun destinataire vérifié : signalée au bandeau de l''accueil du titulaire. Les pannes (mise en file impossible, envoi en échec) le sont toujours';


--
-- Name: parametre_postes; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public.parametre_postes (
    id integer DEFAULT 1 NOT NULL,
    mode text DEFAULT 'transition'::text NOT NULL,
    maximum_postes integer DEFAULT 20 NOT NULL,
    duree_code_minutes integer DEFAULT 15 NOT NULL,
    CONSTRAINT ck_parametre_postes_duree CHECK (((duree_code_minutes >= 5) AND (duree_code_minutes <= 60))),
    CONSTRAINT ck_parametre_postes_maximum CHECK (((maximum_postes >= 1) AND (maximum_postes <= 200))),
    CONSTRAINT ck_parametre_postes_mode CHECK ((mode = ANY (ARRAY['inactif'::text, 'transition'::text, 'exige'::text]))),
    CONSTRAINT parametre_postes_id_check CHECK ((id = 1))
);


--
-- Name: TABLE parametre_postes; Type: COMMENT; Schema: public; Owner: -
--

COMMENT ON TABLE public.parametre_postes IS 'Réglage de la liste des postes autorisés (mode inactif / transition / exige, maximum, durée du code) ; jamais envoyée au central (script 167).';


--
-- Name: parametre_rapport; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public.parametre_rapport (
    id integer DEFAULT 1 NOT NULL,
    tva_reversee_perime boolean DEFAULT false NOT NULL,
    tva_reversee_casse boolean DEFAULT false NOT NULL,
    tva_reversee_don boolean DEFAULT false NOT NULL,
    officine_id uuid DEFAULT public.officine_courante() NOT NULL,
    CONSTRAINT parametre_rapport_id_check CHECK ((id = 1))
);


--
-- Name: TABLE parametre_rapport; Type: COMMENT; Schema: public; Owner: -
--

COMMENT ON TABLE public.parametre_rapport IS 'Réglages des rapports : TVA d''achat à reverser selon le motif de la sortie de stock sans vente';


--
-- Name: COLUMN parametre_rapport.tva_reversee_perime; Type: COMMENT; Schema: public; Owner: -
--

COMMENT ON COLUMN public.parametre_rapport.tva_reversee_perime IS 'TVA d''achat reversée sur les boîtes périmées détruites';


--
-- Name: COLUMN parametre_rapport.tva_reversee_casse; Type: COMMENT; Schema: public; Owner: -
--

COMMENT ON COLUMN public.parametre_rapport.tva_reversee_casse IS 'TVA d''achat reversée sur les boîtes cassées';


--
-- Name: COLUMN parametre_rapport.tva_reversee_don; Type: COMMENT; Schema: public; Owner: -
--

COMMENT ON COLUMN public.parametre_rapport.tva_reversee_don IS 'TVA d''achat reversée sur les boîtes données ou remises en échantillon';


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
    retention_ged_jours integer,
    nettoyage_ged_annees integer,
    officine_id uuid DEFAULT public.officine_courante() NOT NULL,
    alerte_sans_disque_jours integer DEFAULT 7 NOT NULL,
    suivi_disque_depuis timestamp with time zone DEFAULT now() NOT NULL,
    derniere_alerte_sans_disque timestamp with time zone,
    CONSTRAINT ck_sauvegarde_alerte_sans_disque CHECK (((alerte_sans_disque_jours >= 1) AND (alerte_sans_disque_jours <= 90))),
    CONSTRAINT ck_sauvegarde_nettoyage_ged CHECK (((nettoyage_ged_annees IS NULL) OR ((nettoyage_ged_annees >= 1) AND (nettoyage_ged_annees <= 100)))),
    CONSTRAINT ck_sauvegarde_retention_ged CHECK (((retention_ged_jours IS NULL) OR ((retention_ged_jours >= 1) AND (retention_ged_jours <= 365)))),
    CONSTRAINT parametre_sauvegarde_conservation_check CHECK (((conservation >= 1) AND (conservation <= 365))),
    CONSTRAINT parametre_sauvegarde_id_check CHECK ((id = 1))
);


--
-- Name: COLUMN parametre_sauvegarde.retention_ged_jours; Type: COMMENT; Schema: public; Owner: -
--

COMMENT ON COLUMN public.parametre_sauvegarde.retention_ged_jours IS 'Jours de conservation de la copie de la base dans la GED (NULL : réglage du serveur, 7 par défaut)';


--
-- Name: COLUMN parametre_sauvegarde.nettoyage_ged_annees; Type: COMMENT; Schema: public; Owner: -
--

COMMENT ON COLUMN public.parametre_sauvegarde.nettoyage_ged_annees IS 'Documents de la GED supprimés au-delà de ce nombre d''années (NULL : jamais)';


--
-- Name: COLUMN parametre_sauvegarde.alerte_sans_disque_jours; Type: COMMENT; Schema: public; Owner: -
--

COMMENT ON COLUMN public.parametre_sauvegarde.alerte_sans_disque_jours IS 'Titulaire prévenu si aucune sauvegarde réussie sur le disque externe depuis N jours (1 à 90, défaut 7) (script 148).';


--
-- Name: COLUMN parametre_sauvegarde.suivi_disque_depuis; Type: COMMENT; Schema: public; Owner: -
--

COMMENT ON COLUMN public.parametre_sauvegarde.suivi_disque_depuis IS 'Point de départ du délai d''alerte tant qu''aucune sauvegarde n''a réussi (script 148).';


--
-- Name: COLUMN parametre_sauvegarde.derniere_alerte_sans_disque; Type: COMMENT; Schema: public; Owner: -
--

COMMENT ON COLUMN public.parametre_sauvegarde.derniere_alerte_sans_disque IS 'Dernière alerte « aucune sauvegarde sur disque externe » envoyée : une par période (script 148).';


--
-- Name: parametre_serialisation; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public.parametre_serialisation (
    id integer DEFAULT 1 NOT NULL,
    actif boolean DEFAULT true NOT NULL,
    connecteur text DEFAULT 'factice'::text NOT NULL,
    factice_hors_ligne boolean DEFAULT false NOT NULL,
    modifie_le timestamp with time zone DEFAULT now() NOT NULL,
    officine_id uuid DEFAULT public.officine_courante() NOT NULL,
    CONSTRAINT parametre_serialisation_connecteur_check CHECK ((connecteur = 'factice'::text)),
    CONSTRAINT parametre_serialisation_id_check CHECK ((id = 1))
);


--
-- Name: parametre_substitution; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public.parametre_substitution (
    id integer DEFAULT 1 NOT NULL,
    equivalent_exact_seulement boolean DEFAULT true NOT NULL,
    modifie_le timestamp with time zone DEFAULT now() NOT NULL,
    modifie_par bigint,
    officine_id uuid DEFAULT public.officine_courante() NOT NULL,
    CONSTRAINT parametre_substitution_id_check CHECK ((id = 1))
);


--
-- Name: parametre_surveillance; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public.parametre_surveillance (
    id integer DEFAULT 1 NOT NULL,
    seuil_lignes_retirees integer DEFAULT 5 NOT NULL,
    seuil_tiroir integer DEFAULT 3 NOT NULL,
    seuil_attentes_abandonnees integer DEFAULT 3 NOT NULL,
    seuil_remises integer DEFAULT 5 NOT NULL,
    seuil_remises_patient integer DEFAULT 3 NOT NULL,
    heure_ouverture time without time zone,
    heure_fermeture time without time zone,
    seuil_ajustements integer DEFAULT 5 NOT NULL,
    seuil_casses integer DEFAULT 3 NOT NULL,
    seuil_echantillons integer DEFAULT 3 NOT NULL,
    jours_comptage_sensibles integer DEFAULT 7 NOT NULL,
    reception_aveugle boolean DEFAULT true NOT NULL,
    jours_avoir_retour integer DEFAULT 30 NOT NULL,
    seuil_credit_nouveau_patient numeric(12,2) DEFAULT 5000 NOT NULL,
    jours_patient_recent integer DEFAULT 7 NOT NULL,
    seuil_credits_jour numeric(12,2) DEFAULT 20000 NOT NULL,
    jours_credit_sans_remboursement integer DEFAULT 60 NOT NULL,
    seuil_autorisations_valideur integer DEFAULT 10 NOT NULL,
    seuil_autorisations_employe integer DEFAULT 5 NOT NULL,
    minutes_postes_simultanes integer DEFAULT 10 NOT NULL,
    seuil_changements_poste integer DEFAULT 3 NOT NULL,
    seuil_baisses_prix integer DEFAULT 1 NOT NULL,
    seuil_sorties_tiroir integer DEFAULT 5 NOT NULL,
    officine_id uuid DEFAULT public.officine_courante() NOT NULL,
    CONSTRAINT ck_parametre_surveillance_achats CHECK (((jours_avoir_retour >= 1) AND (jours_avoir_retour <= 365))),
    CONSTRAINT ck_parametre_surveillance_comptes CHECK (((seuil_credit_nouveau_patient > (0)::numeric) AND ((jours_patient_recent >= 1) AND (jours_patient_recent <= 90)) AND (seuil_credits_jour > (0)::numeric) AND ((jours_credit_sans_remboursement >= 1) AND (jours_credit_sans_remboursement <= 365)) AND (seuil_autorisations_valideur >= 1) AND (seuil_autorisations_employe >= 1) AND ((minutes_postes_simultanes >= 1) AND (minutes_postes_simultanes <= 120)) AND (seuil_changements_poste >= 1) AND (seuil_baisses_prix >= 1))),
    CONSTRAINT ck_parametre_surveillance_horaires CHECK (((heure_ouverture IS NULL) = (heure_fermeture IS NULL))),
    CONSTRAINT ck_parametre_surveillance_sorties_tiroir CHECK ((seuil_sorties_tiroir >= 1)),
    CONSTRAINT ck_parametre_surveillance_stock CHECK (((seuil_ajustements >= 1) AND (seuil_casses >= 1) AND (seuil_echantillons >= 1) AND ((jours_comptage_sensibles >= 1) AND (jours_comptage_sensibles <= 90)))),
    CONSTRAINT parametre_surveillance_id_check CHECK ((id = 1)),
    CONSTRAINT parametre_surveillance_seuil_attentes_abandonnees_check CHECK ((seuil_attentes_abandonnees >= 1)),
    CONSTRAINT parametre_surveillance_seuil_lignes_retirees_check CHECK ((seuil_lignes_retirees >= 1)),
    CONSTRAINT parametre_surveillance_seuil_remises_check CHECK ((seuil_remises >= 1)),
    CONSTRAINT parametre_surveillance_seuil_remises_patient_check CHECK ((seuil_remises_patient >= 1)),
    CONSTRAINT parametre_surveillance_seuil_tiroir_check CHECK ((seuil_tiroir >= 1))
);


--
-- Name: TABLE parametre_surveillance; Type: COMMENT; Schema: public; Owner: -
--

COMMENT ON TABLE public.parametre_surveillance IS 'Seuils de la surveillance de la caisse (par employé et par jour) et horaires d''ouverture (vides : pas de contrôle)';


--
-- Name: COLUMN parametre_surveillance.reception_aveugle; Type: COMMENT; Schema: public; Owner: -
--

COMMENT ON COLUMN public.parametre_surveillance.reception_aveugle IS 'Réception en deux étapes : comptage sans voir le BL, puis facture saisie et comparée';


--
-- Name: COLUMN parametre_surveillance.jours_avoir_retour; Type: COMMENT; Schema: public; Owner: -
--

COMMENT ON COLUMN public.parametre_surveillance.jours_avoir_retour IS 'Retour fournisseur envoyé sans avoir au-delà de ce délai (jours) : anomalie';


--
-- Name: parametre_tracabilite; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public.parametre_tracabilite (
    id integer DEFAULT 1 NOT NULL,
    boite_obligatoire boolean DEFAULT false NOT NULL,
    patient_obligatoire text DEFAULT 'ordonnance'::text NOT NULL,
    officine_id uuid DEFAULT public.officine_courante() NOT NULL,
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
    cree_le timestamp with time zone DEFAULT now() NOT NULL,
    email text,
    cree_par bigint,
    lien text,
    assure_principal_id bigint,
    date_fin_droit date,
    centre_payeur_id bigint,
    consentement_sms boolean DEFAULT false NOT NULL,
    consentement_sms_le timestamp with time zone,
    formulaire_sms_recu boolean DEFAULT false NOT NULL,
    formulaire_sms_recu_le timestamp with time zone,
    officine_id uuid DEFAULT public.officine_courante() NOT NULL,
    CONSTRAINT ck_patient_assure_principal CHECK ((assure_principal_id <> id)),
    CONSTRAINT ck_patient_lien CHECK ((lien = ANY (ARRAY['assure'::text, 'conjoint'::text, 'enfant'::text, 'ascendant'::text, 'autre'::text])))
);


--
-- Name: COLUMN patient.email; Type: COMMENT; Schema: public; Owner: -
--

COMMENT ON COLUMN public.patient.email IS 'Adresse e-mail du patient (facultative)';


--
-- Name: COLUMN patient.cree_par; Type: COMMENT; Schema: public; Owner: -
--

COMMENT ON COLUMN public.patient.cree_par IS 'Employé qui a créé le patient (vide pour les patients antérieurs ou repris)';


--
-- Name: COLUMN patient.lien; Type: COMMENT; Schema: public; Owner: -
--

COMMENT ON COLUMN public.patient.lien IS 'Lien avec l''assuré : assure, conjoint, enfant, ascendant, autre ; NULL : non renseigné';


--
-- Name: COLUMN patient.assure_principal_id; Type: COMMENT; Schema: public; Owner: -
--

COMMENT ON COLUMN public.patient.assure_principal_id IS 'Fiche de l''assuré principal (NULL pour l''assuré lui-même) ; jamais déduit du suffixe du n° d''assuré';


--
-- Name: COLUMN patient.date_fin_droit; Type: COMMENT; Schema: public; Owner: -
--

COMMENT ON COLUMN public.patient.date_fin_droit IS 'Fin des droits Chifa (saisie manuelle) ; dépassée : avertissement à la vente CNAS/CASNOS, sans blocage (script 102)';


--
-- Name: COLUMN patient.centre_payeur_id; Type: COMMENT; Schema: public; Owner: -
--

COMMENT ON COLUMN public.patient.centre_payeur_id IS 'Centre payeur habituel, proposé aux ventes Chifa du patient (script 100)';


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
    sensible boolean DEFAULT false NOT NULL,
    par_personne boolean DEFAULT false NOT NULL
);


--
-- Name: COLUMN permission.par_personne; Type: COMMENT; Schema: public; Owner: -
--

COMMENT ON COLUMN public.permission.par_personne IS 'Droit réglé sur chaque employé (utilisateur), pas dans la matrice des rôles';


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
    officine_id uuid DEFAULT public.officine_courante() NOT NULL,
    CONSTRAINT pharmacie_id_check CHECK ((id = 1)),
    CONSTRAINT pharmacie_langue_defaut_check CHECK ((langue_defaut = ANY (ARRAY['ar'::text, 'fr'::text, 'en'::text])))
);


--
-- Name: poste_autorise; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public.poste_autorise (
    id bigint NOT NULL,
    nom character varying(100) NOT NULL,
    statut text DEFAULT 'en_attente'::text NOT NULL,
    cle_publique bytea,
    empreinte_cle character(16),
    generation integer DEFAULT 1 NOT NULL,
    code_hache bytea,
    code_expire_le timestamp with time zone,
    code_essais smallint DEFAULT 0 NOT NULL,
    ordinateur character varying(100),
    cree_le timestamp with time zone DEFAULT now() NOT NULL,
    cree_par bigint,
    enrole_le timestamp with time zone,
    derniere_preuve_le timestamp with time zone,
    revoque_le timestamp with time zone,
    revoque_par bigint,
    motif_revocation character varying(200),
    CONSTRAINT ck_poste_autorise_actif_cle CHECK (((statut <> 'actif'::text) OR ((cle_publique IS NOT NULL) AND (empreinte_cle IS NOT NULL) AND (enrole_le IS NOT NULL)))),
    CONSTRAINT ck_poste_autorise_attente_code CHECK (((statut <> 'en_attente'::text) OR ((code_hache IS NOT NULL) AND (code_expire_le IS NOT NULL)))),
    CONSTRAINT ck_poste_autorise_essais CHECK ((code_essais >= 0)),
    CONSTRAINT ck_poste_autorise_generation CHECK ((generation >= 1)),
    CONSTRAINT ck_poste_autorise_nom CHECK ((length(btrim((nom)::text)) > 0)),
    CONSTRAINT ck_poste_autorise_revocation CHECK (((statut = 'revoque'::text) = (revoque_le IS NOT NULL))),
    CONSTRAINT ck_poste_autorise_statut CHECK ((statut = ANY (ARRAY['en_attente'::text, 'actif'::text, 'revoque'::text])))
);


--
-- Name: TABLE poste_autorise; Type: COMMENT; Schema: public; Owner: -
--

COMMENT ON TABLE public.poste_autorise IS 'Postes autorisés par le titulaire : enrôlement par code à usage unique, clé publique ECDSA P-256 du poste (la clé privée reste sur le poste), révocation ; jamais envoyée au central (script 167).';


--
-- Name: poste_autorise_id_seq; Type: SEQUENCE; Schema: public; Owner: -
--

ALTER TABLE public.poste_autorise ALTER COLUMN id ADD GENERATED ALWAYS AS IDENTITY (
    SEQUENCE NAME public.poste_autorise_id_seq
    START WITH 1
    INCREMENT BY 1
    NO MINVALUE
    NO MAXVALUE
    CACHE 1
);


--
-- Name: presence_poste; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public.presence_poste (
    poste character varying(100) NOT NULL,
    vu_le timestamp with time zone DEFAULT now() NOT NULL,
    poste_id bigint,
    CONSTRAINT ck_presence_poste_nom CHECK ((length(btrim((poste)::text)) > 0))
);


--
-- Name: TABLE presence_poste; Type: COMMENT; Schema: public; Owner: -
--

COMMENT ON TABLE public.presence_poste IS 'Présence des postes : dernier instant où le serveur principal a vu chaque poste (en-tête X-Pharma-Poste), recopiée sur le 2e serveur pour la règle de majorité ; jamais envoyée au central (script 165).';


--
-- Name: pret_bancaire; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public.pret_bancaire (
    id bigint NOT NULL,
    compte_id bigint,
    reference text NOT NULL,
    objet text,
    montant numeric(12,2) NOT NULL,
    date_pret date NOT NULL,
    document text,
    utilisateur_id bigint NOT NULL,
    cree_le timestamp with time zone DEFAULT now() NOT NULL,
    preteur text,
    taux numeric(7,4),
    duree_mois integer,
    officine_id uuid DEFAULT public.officine_courante() NOT NULL,
    CONSTRAINT pret_bancaire_duree_mois_check CHECK (((duree_mois >= 1) AND (duree_mois <= 600))),
    CONSTRAINT pret_bancaire_montant_check CHECK ((montant > (0)::numeric)),
    CONSTRAINT pret_bancaire_preteur_check CHECK ((((compte_id IS NOT NULL) AND (preteur IS NULL)) OR ((compte_id IS NULL) AND (btrim(preteur) <> ''::text)))),
    CONSTRAINT pret_bancaire_taux_check CHECK (((taux >= (0)::numeric) AND (taux <= (100)::numeric)))
);


--
-- Name: TABLE pret_bancaire; Type: COMMENT; Schema: public; Owner: -
--

COMMENT ON TABLE public.pret_bancaire IS 'Prêt accordé par une banque de la trésorerie (compte_id) ou par un prêteur hors trésorerie (preteur) ; réservé au titulaire';


--
-- Name: COLUMN pret_bancaire.document; Type: COMMENT; Schema: public; Owner: -
--

COMMENT ON COLUMN public.pret_bancaire.document IS 'Document joint (contrat, tableau d''amortissement) : chemin « Type\nom » dans la GED du serveur';


--
-- Name: COLUMN pret_bancaire.preteur; Type: COMMENT; Schema: public; Owner: -
--

COMMENT ON COLUMN public.pret_bancaire.preteur IS 'Prêteur qui n''est pas un compte de la trésorerie (nom saisi librement) ; exclusif de compte_id';


--
-- Name: COLUMN pret_bancaire.taux; Type: COMMENT; Schema: public; Owner: -
--

COMMENT ON COLUMN public.pret_bancaire.taux IS 'Taux annuel du contrat, en % (0 à 100, 4 décimales au plus) ; facultatif ; saisi, jamais recalculé';


--
-- Name: COLUMN pret_bancaire.duree_mois; Type: COMMENT; Schema: public; Owner: -
--

COMMENT ON COLUMN public.pret_bancaire.duree_mois IS 'Durée du contrat en mois (1 à 600) ; facultative ; saisie, jamais recalculée';


--
-- Name: pret_bancaire_id_seq; Type: SEQUENCE; Schema: public; Owner: -
--

ALTER TABLE public.pret_bancaire ALTER COLUMN id ADD GENERATED ALWAYS AS IDENTITY (
    SEQUENCE NAME public.pret_bancaire_id_seq
    START WITH 1
    INCREMENT BY 1
    NO MINVALUE
    NO MAXVALUE
    CACHE 1
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
    a_verifier boolean DEFAULT false NOT NULL,
    liste_psychotrope text,
    categorie text DEFAULT 'medicament'::text NOT NULL,
    bloque_achat boolean DEFAULT false NOT NULL,
    bloque_vente boolean DEFAULT false NOT NULL,
    motif_blocage text,
    sensible boolean DEFAULT false NOT NULL,
    classe_confirmee_par bigint,
    classe_confirmee_le timestamp with time zone,
    fabrication_locale boolean,
    contenance integer,
    officine_id uuid DEFAULT public.officine_courante() NOT NULL,
    CONSTRAINT ck_produit_contenance CHECK (((contenance IS NULL) OR ((contenance >= 1) AND (contenance <= 10000)))),
    CONSTRAINT ck_produit_liste_psychotrope CHECK (((liste_psychotrope IS NULL) OR (liste_psychotrope = ANY (ARRAY['liste_i'::text, 'liste_ii'::text])))),
    CONSTRAINT ck_seuils_produit CHECK (((jours_rouge IS NULL) OR (jours_orange IS NULL) OR (jours_rouge < jours_orange))),
    CONSTRAINT produit_classe_check CHECK ((classe = ANY (ARRAY['libre'::text, 'liste_i'::text, 'liste_ii'::text, 'psychotrope'::text, 'stupefiant'::text]))),
    CONSTRAINT produit_classe_confirmee_check CHECK (((classe_confirmee_par IS NULL) = (classe_confirmee_le IS NULL))),
    CONSTRAINT produit_jours_orange_check CHECK ((jours_orange > 0)),
    CONSTRAINT produit_jours_rouge_check CHECK ((jours_rouge > 0)),
    CONSTRAINT produit_prix_public_check CHECK ((prix_public >= (0)::numeric)),
    CONSTRAINT produit_stock_minimum_positif CHECK (((stock_minimum IS NULL) OR ((stock_minimum >= 0) AND (stock_minimum <= 100000)))),
    CONSTRAINT produit_tarif_reference_check CHECK ((tarif_reference >= (0)::numeric))
);


--
-- Name: COLUMN produit.stock_minimum; Type: COMMENT; Schema: public; Owner: -
--

COMMENT ON COLUMN public.produit.stock_minimum IS 'Seuil d''alerte saisi par le pharmacien (boîtes). NULL : seuil automatique. 0 : pas d''alerte de stock';


--
-- Name: COLUMN produit.a_verifier; Type: COMMENT; Schema: public; Owner: -
--

COMMENT ON COLUMN public.produit.a_verifier IS 'Classe à confirmer par le titulaire, vente refusée d''ici là : produit repris de l''ancien logiciel sans n° d''enregistrement, ou créé (ou passé à une DCI de substance surveillée) par un autre que le titulaire en classe hors registre, classé d''office psychotrope ou stupéfiant';


--
-- Name: COLUMN produit.liste_psychotrope; Type: COMMENT; Schema: public; Owner: -
--

COMMENT ON COLUMN public.produit.liste_psychotrope IS 'Liste I ou II d''un psychotrope, pour son SHP (null : à renseigner, SHP 0)';


--
-- Name: COLUMN produit.categorie; Type: COMMENT; Schema: public; Owner: -
--

COMMENT ON COLUMN public.produit.categorie IS 'Catégorie du produit (categorie_produit) : médicament ou autre produit';


--
-- Name: COLUMN produit.bloque_achat; Type: COMMENT; Schema: public; Owner: -
--

COMMENT ON COLUMN public.produit.bloque_achat IS 'Produit bloqué à l''achat : ni commande, ni réception, ni suggestion';


--
-- Name: COLUMN produit.bloque_vente; Type: COMMENT; Schema: public; Owner: -
--

COMMENT ON COLUMN public.produit.bloque_vente IS 'Produit bloqué à la vente : la caisse refuse de le vendre';


--
-- Name: COLUMN produit.motif_blocage; Type: COMMENT; Schema: public; Owner: -
--

COMMENT ON COLUMN public.produit.motif_blocage IS 'Raison du blocage à l''achat ou à la vente';


--
-- Name: COLUMN produit.sensible; Type: COMMENT; Schema: public; Owner: -
--

COMMENT ON COLUMN public.produit.sensible IS 'Produit sensible (cher, très demandé, souvent volé) : compté à part, régulièrement (Surveillance)';


--
-- Name: COLUMN produit.classe_confirmee_par; Type: COMMENT; Schema: public; Owner: -
--

COMMENT ON COLUMN public.produit.classe_confirmee_par IS 'Titulaire qui a confirmé une classe moins stricte que celle de la DCI (substance surveillée) ou de la nomenclature : un employé ne la change plus ; retirée si la DCI change';


--
-- Name: COLUMN produit.classe_confirmee_le; Type: COMMENT; Schema: public; Owner: -
--

COMMENT ON COLUMN public.produit.classe_confirmee_le IS 'Date de la confirmation de classe par le titulaire (classe_confirmee_par)';


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
-- Name: produit_indication; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public.produit_indication (
    produit_id bigint NOT NULL,
    indication_id bigint NOT NULL,
    cree_le timestamp with time zone DEFAULT now() NOT NULL,
    officine_id uuid DEFAULT public.officine_courante() NOT NULL
);


--
-- Name: profil_catalogue_grossiste; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public.profil_catalogue_grossiste (
    fournisseur_id bigint NOT NULL,
    colonne_code text,
    colonne_code_barre text,
    colonne_designation text,
    colonne_prix_ht text,
    colonne_disponibilite text,
    colonne_delai text,
    valeurs_indisponible text,
    modifie_le timestamp with time zone DEFAULT now() NOT NULL,
    modifie_par bigint,
    valeurs_disponible text
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
    montant_ht numeric(12,2),
    remise numeric(12,2) DEFAULT 0 NOT NULL,
    montant_tva numeric(12,2),
    timbre numeric(12,2) DEFAULT 0 NOT NULL,
    shp_total numeric(12,2) DEFAULT 0 NOT NULL,
    date_facture date,
    officine_id uuid DEFAULT public.officine_courante() NOT NULL,
    CONSTRAINT ck_reception_montants CHECK (((remise >= (0)::numeric) AND (timbre >= (0)::numeric) AND (shp_total >= (0)::numeric) AND ((montant_ht IS NULL) OR (montant_ht >= (0)::numeric)) AND ((montant_tva IS NULL) OR (montant_tva >= (0)::numeric)))),
    CONSTRAINT reception_montant_ttc_check CHECK ((montant_ttc >= (0)::numeric))
);


--
-- Name: COLUMN reception.montant_ttc; Type: COMMENT; Schema: public; Owner: -
--

COMMENT ON COLUMN public.reception.montant_ttc IS 'Net à payer au grossiste : net HT + TVA + timbre';


--
-- Name: COLUMN reception.montant_ht; Type: COMMENT; Schema: public; Owner: -
--

COMMENT ON COLUMN public.reception.montant_ht IS 'Total HT avant remise (null : réception enregistrée avant le détail)';


--
-- Name: COLUMN reception.remise; Type: COMMENT; Schema: public; Owner: -
--

COMMENT ON COLUMN public.reception.remise IS 'Remise accordée par le grossiste sur la facture (DA)';


--
-- Name: COLUMN reception.montant_tva; Type: COMMENT; Schema: public; Owner: -
--

COMMENT ON COLUMN public.reception.montant_tva IS 'TVA de la facture (null : réception enregistrée avant le détail)';


--
-- Name: COLUMN reception.timbre; Type: COMMENT; Schema: public; Owner: -
--

COMMENT ON COLUMN public.reception.timbre IS 'Droit de timbre de la facture (DA)';


--
-- Name: COLUMN reception.shp_total; Type: COMMENT; Schema: public; Owner: -
--

COMMENT ON COLUMN public.reception.shp_total IS 'Total du supplément d''honoraire du pharmacien (SHP) indiqué sur la facture ; non dû au grossiste';


--
-- Name: COLUMN reception.date_facture; Type: COMMENT; Schema: public; Owner: -
--

COMMENT ON COLUMN public.reception.date_facture IS 'Date de la facture du grossiste (script 103), facultative ; ni dans le futur, ni plus de 90 jours avant la livraison (contrôle applicatif)';


--
-- Name: reception_attente; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public.reception_attente (
    id bigint NOT NULL,
    poste text DEFAULT ''::text NOT NULL,
    utilisateur_id bigint NOT NULL,
    libelle text NOT NULL,
    mode text DEFAULT 'reception'::text NOT NULL,
    lignes integer NOT NULL,
    contenu jsonb NOT NULL,
    scans bigint[] DEFAULT '{}'::bigint[] NOT NULL,
    cree_le timestamp with time zone DEFAULT now() NOT NULL,
    CONSTRAINT reception_attente_lignes_check CHECK ((lignes >= 0)),
    CONSTRAINT reception_attente_mode_check CHECK ((mode = ANY (ARRAY['reception'::text, 'facture'::text, 'comptage'::text, 'recompte'::text])))
);


--
-- Name: TABLE reception_attente; Type: COMMENT; Schema: public; Owner: -
--

COMMENT ON TABLE public.reception_attente IS 'Réceptions de livraison mises en attente (contenu de l''écran Réceptions, repris puis supprimé)';


--
-- Name: reception_attente_id_seq; Type: SEQUENCE; Schema: public; Owner: -
--

ALTER TABLE public.reception_attente ALTER COLUMN id ADD GENERATED ALWAYS AS IDENTITY (
    SEQUENCE NAME public.reception_attente_id_seq
    START WITH 1
    INCREMENT BY 1
    NO MINVALUE
    NO MAXVALUE
    CACHE 1
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
-- Name: reception_ligne; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public.reception_ligne (
    id bigint NOT NULL,
    reception_id bigint NOT NULL,
    lot_id bigint NOT NULL,
    produit_id bigint NOT NULL,
    quantite integer NOT NULL,
    prix_achat_ht numeric(12,2) NOT NULL,
    remise_pct numeric(5,2) DEFAULT 0 NOT NULL,
    prix_net_ht numeric(12,2) NOT NULL,
    ppa numeric(12,2),
    shp numeric(8,2) DEFAULT 0 NOT NULL,
    taux_tva numeric(5,2) DEFAULT 0 NOT NULL,
    ug integer DEFAULT 0 NOT NULL,
    ug_cout text DEFAULT 'zero'::text NOT NULL,
    prix_catalogue_remplace boolean DEFAULT false NOT NULL,
    officine_id uuid DEFAULT public.officine_courante() NOT NULL,
    CONSTRAINT ck_reception_ligne_quantites CHECK (((quantite >= 0) AND (ug >= 0) AND ((quantite + ug) > 0) AND (ug_cout = ANY (ARRAY['zero'::text, 'reparti'::text])))),
    CONSTRAINT reception_ligne_ppa_check CHECK (((ppa IS NULL) OR (ppa >= (0)::numeric))),
    CONSTRAINT reception_ligne_prix_achat_ht_check CHECK ((prix_achat_ht >= (0)::numeric)),
    CONSTRAINT reception_ligne_prix_net_ht_check CHECK ((prix_net_ht >= (0)::numeric)),
    CONSTRAINT reception_ligne_remise_pct_check CHECK (((remise_pct >= (0)::numeric) AND (remise_pct <= (100)::numeric))),
    CONSTRAINT reception_ligne_shp_check CHECK ((shp >= (0)::numeric))
);


--
-- Name: TABLE reception_ligne; Type: COMMENT; Schema: public; Owner: -
--

COMMENT ON TABLE public.reception_ligne IS 'Lignes d''une réception telles qu''écrites sur la facture du grossiste (044)';


--
-- Name: COLUMN reception_ligne.quantite; Type: COMMENT; Schema: public; Owner: -
--

COMMENT ON COLUMN public.reception_ligne.quantite IS 'Unités facturées';


--
-- Name: COLUMN reception_ligne.ug; Type: COMMENT; Schema: public; Owner: -
--

COMMENT ON COLUMN public.reception_ligne.ug IS 'Unités gratuites livrées en plus';


--
-- Name: COLUMN reception_ligne.ug_cout; Type: COMMENT; Schema: public; Owner: -
--

COMMENT ON COLUMN public.reception_ligne.ug_cout IS 'Coût des UG : zero (boîtes gratuites à 0 DA) ou reparti (montant facturé ÷ toutes les boîtes)';


--
-- Name: reception_ligne_id_seq; Type: SEQUENCE; Schema: public; Owner: -
--

CREATE SEQUENCE public.reception_ligne_id_seq
    START WITH 1
    INCREMENT BY 1
    NO MINVALUE
    NO MAXVALUE
    CACHE 1;


--
-- Name: reception_ligne_id_seq; Type: SEQUENCE OWNED BY; Schema: public; Owner: -
--

ALTER SEQUENCE public.reception_ligne_id_seq OWNED BY public.reception_ligne.id;


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
-- Name: reglage_central; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public.reglage_central (
    cle text NOT NULL,
    version bigint NOT NULL,
    valeur jsonb NOT NULL,
    recu_le timestamp with time zone DEFAULT now() NOT NULL,
    officine_id uuid DEFAULT public.officine_courante() NOT NULL,
    CONSTRAINT reglage_central_cle_check CHECK ((cle ~ '^[a-z_]{1,63}$'::text)),
    CONSTRAINT reglage_central_valeur_check CHECK ((jsonb_typeof(valeur) = 'object'::text)),
    CONSTRAINT reglage_central_version_check CHECK ((version >= 1))
);


--
-- Name: TABLE reglage_central; Type: COMMENT; Schema: public; Owner: -
--

COMMENT ON TABLE public.reglage_central IS 'Synchronisation : réglages décidés au serveur central, descendus par la synchronisation, jamais écrasés par une version plus ancienne (script 131).';


--
-- Name: reglage_mutualise; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public.reglage_mutualise (
    nom_table text NOT NULL,
    mode text DEFAULT 'local'::text NOT NULL,
    ajouts_locaux boolean DEFAULT true NOT NULL,
    mode_recu text,
    version_modele_recue bigint,
    recu_le timestamp with time zone,
    modele jsonb,
    version_modele_appliquee bigint,
    applique_le timestamp with time zone,
    etat text DEFAULT 'aucun'::text NOT NULL,
    motif_rejet text,
    version_modele_rejetee bigint,
    origine text DEFAULT 'locale'::text NOT NULL,
    modifie_localement_le timestamp with time zone,
    officine_id uuid DEFAULT public.officine_courante() NOT NULL,
    CONSTRAINT reglage_mutualise_etat_check CHECK ((etat = ANY (ARRAY['aucun'::text, 'applique'::text, 'non_adopte'::text, 'rejete'::text]))),
    CONSTRAINT reglage_mutualise_mode_check CHECK ((mode = ANY (ARRAY['local'::text, 'propose'::text, 'impose'::text]))),
    CONSTRAINT reglage_mutualise_mode_recu_check CHECK (((mode_recu IS NULL) OR (mode_recu = ANY (ARRAY['local'::text, 'propose'::text, 'impose'::text])))),
    CONSTRAINT reglage_mutualise_modele_check CHECK (((modele IS NULL) OR (jsonb_typeof(modele) = 'object'::text))),
    CONSTRAINT reglage_mutualise_motif_rejet_check CHECK (((motif_rejet IS NULL) OR (length(motif_rejet) <= 500))),
    CONSTRAINT reglage_mutualise_nom_table_check CHECK ((nom_table = ANY (public.reglage_tables_mutualisables()))),
    CONSTRAINT reglage_mutualise_origine_check CHECK ((origine = ANY (ARRAY['locale'::text, 'central'::text]))),
    CONSTRAINT reglage_mutualise_version_modele_appliquee_check CHECK (((version_modele_appliquee IS NULL) OR (version_modele_appliquee >= 1))),
    CONSTRAINT reglage_mutualise_version_modele_recue_check CHECK (((version_modele_recue IS NULL) OR (version_modele_recue >= 1))),
    CONSTRAINT reglage_mutualise_version_modele_rejetee_check CHECK (((version_modele_rejetee IS NULL) OR (version_modele_rejetee >= 1)))
);


--
-- Name: TABLE reglage_mutualise; Type: COMMENT; Schema: public; Owner: -
--

COMMENT ON TABLE public.reglage_mutualise IS 'Mutualisation des réglages : état de chacune des 11 tables (mode en vigueur, modèle reçu, version appliquée, rejet, origine de la valeur) ; montante, la valeur du modèle ne repart pas (script 141).';


--
-- Name: regle_alerte_psychotrope; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public.regle_alerte_psychotrope (
    code text NOT NULL,
    libelle text NOT NULL,
    active boolean DEFAULT true NOT NULL,
    seuil integer NOT NULL,
    periode_jours integer,
    exiger_valideur boolean DEFAULT false NOT NULL,
    ordre smallint DEFAULT 0 NOT NULL,
    modifie_le timestamp with time zone,
    modifie_par bigint,
    unites_par_jour integer,
    officine_id uuid DEFAULT public.officine_courante() NOT NULL,
    CONSTRAINT ck_regle_alerte_psy_code CHECK ((code = ANY (ARRAY['patient_repete'::text, 'ordonnance_reutilisee'::text, 'quantite_anormale'::text, 'prescripteur_inhabituel'::text, 'prescripteur_hors_zone'::text, 'traitement_en_cours'::text]))),
    CONSTRAINT ck_regle_alerte_psy_periode CHECK (((periode_jours >= 1) AND (periode_jours <= 3650))),
    CONSTRAINT ck_regle_alerte_psy_periode_vide CHECK (((periode_jours IS NOT NULL) OR (code = ANY (ARRAY['ordonnance_reutilisee'::text, 'traitement_en_cours'::text])))),
    CONSTRAINT ck_regle_alerte_psy_seuil CHECK (((seuil >= 1) AND (seuil <= 1000))),
    CONSTRAINT ck_regle_alerte_psy_unites CHECK (((unites_par_jour IS NULL) OR ((unites_par_jour >= 1) AND (unites_par_jour <= 100))))
);


--
-- Name: COLUMN regle_alerte_psychotrope.periode_jours; Type: COMMENT; Schema: public; Owner: -
--

COMMENT ON COLUMN public.regle_alerte_psychotrope.periode_jours IS 'Période en jours (1 à 3650) ; vide : sans limite (ordonnance_reutilisee) ou sans objet (traitement_en_cours), script 153.';


--
-- Name: reglement_facture; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public.reglement_facture (
    reglement_id bigint NOT NULL,
    reception_id bigint NOT NULL,
    montant numeric(12,2) NOT NULL,
    officine_id uuid DEFAULT public.officine_courante() NOT NULL,
    CONSTRAINT reglement_facture_montant_check CHECK ((montant > (0)::numeric))
);


--
-- Name: reglement_facture_client; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public.reglement_facture_client (
    mouvement_id bigint NOT NULL,
    facture_id bigint NOT NULL,
    montant numeric(12,2) NOT NULL,
    part_credit numeric(12,2) DEFAULT 0 NOT NULL,
    officine_id uuid DEFAULT public.officine_courante() NOT NULL,
    CONSTRAINT reglement_facture_client_check CHECK (((part_credit >= (0)::numeric) AND (part_credit <= montant))),
    CONSTRAINT reglement_facture_client_montant_check CHECK ((montant > (0)::numeric))
);


--
-- Name: COLUMN reglement_facture_client.part_credit; Type: COMMENT; Schema: public; Owner: -
--

COMMENT ON COLUMN public.reglement_facture_client.part_credit IS 'Part de l''encaissement qui a soldé la dette du patient pour les ventes à crédit de la facture (comptée dans la dette réglée, pas une seconde fois dans le reste)';


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
    numero text,
    statut text DEFAULT 'valide'::text NOT NULL,
    annule_le timestamp with time zone,
    annule_par bigint,
    motif_annulation text,
    annulation_valide_par bigint,
    officine_id uuid DEFAULT public.officine_courante() NOT NULL,
    CONSTRAINT ck_reglement_fournisseur_statut CHECK ((((statut = 'valide'::text) AND (annule_le IS NULL) AND (annule_par IS NULL) AND (motif_annulation IS NULL)) OR ((statut = 'annule'::text) AND (annule_le IS NOT NULL) AND (annule_par IS NOT NULL) AND ((char_length(btrim(motif_annulation)) >= 3) AND (char_length(btrim(motif_annulation)) <= 300)) AND (moyen = 'especes'::text) AND (part_tiroir = montant)))),
    CONSTRAINT reglement_fournisseur_check CHECK ((part_tiroir <= montant)),
    CONSTRAINT reglement_fournisseur_check1 CHECK (((part_tiroir = (0)::numeric) OR ((moyen = 'especes'::text) AND (session_caisse_id IS NOT NULL)))),
    CONSTRAINT reglement_fournisseur_check2 CHECK (((moyen <> 'effet'::text) OR (echeance IS NOT NULL))),
    CONSTRAINT reglement_fournisseur_montant_check CHECK ((montant > (0)::numeric)),
    CONSTRAINT reglement_fournisseur_moyen_check CHECK ((moyen = ANY (ARRAY['especes'::text, 'cheque'::text, 'virement'::text, 'effet'::text, 'prelevement'::text, 'financement'::text]))),
    CONSTRAINT reglement_fournisseur_part_tiroir_check CHECK ((part_tiroir >= (0)::numeric))
);


--
-- Name: COLUMN reglement_fournisseur.numero; Type: COMMENT; Schema: public; Owner: -
--

COMMENT ON COLUMN public.reglement_fournisseur.numero IS 'Numéro du règlement au grossiste (RG-000001)';


--
-- Name: COLUMN reglement_fournisseur.statut; Type: COMMENT; Schema: public; Owner: -
--

COMMENT ON COLUMN public.reglement_fournisseur.statut IS 'valide ; annule : règlement en espèces payé depuis le tiroir et annulé (script 098), jamais supprimé';


--
-- Name: COLUMN reglement_fournisseur.annulation_valide_par; Type: COMMENT; Schema: public; Owner: -
--

COMMENT ON COLUMN public.reglement_fournisseur.annulation_valide_par IS 'Ayant droit achats.regler qui a autorisé l''annulation par son code comptoir (null : droit propre)';


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
-- Name: regularisation_part_organisme; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public.regularisation_part_organisme (
    id bigint NOT NULL,
    vente_id bigint NOT NULL,
    organisme_id bigint,
    part_organisme_vente numeric(12,2) NOT NULL,
    part_organisme_chifa numeric(12,2) NOT NULL,
    ecart numeric(12,2) NOT NULL,
    sort text NOT NULL,
    montant_patient numeric(12,2) DEFAULT 0 NOT NULL,
    montant_dette_diminuee numeric(12,2) DEFAULT 0 NOT NULL,
    montant_a_rendre numeric(12,2) DEFAULT 0 NOT NULL,
    montant_pharmacie numeric(12,2) DEFAULT 0 NOT NULL,
    credit_mouvement_id bigint,
    utilisateur_id bigint NOT NULL,
    valide_par bigint,
    cree_le timestamp with time zone DEFAULT now() NOT NULL,
    statut text DEFAULT 'active'::text NOT NULL,
    annulee_le timestamp with time zone,
    annulee_par bigint,
    motif_annulation text,
    annulation_credit_mouvement_id bigint,
    annulation_dette_annulee numeric(12,2) DEFAULT 0 NOT NULL,
    annulation_trop_paye_impute numeric(12,2) DEFAULT 0 CONSTRAINT regularisation_part_organis_annulation_trop_paye_imput_not_null NOT NULL,
    annulation_rembourse numeric(12,2) DEFAULT 0 NOT NULL,
    annulation_mouvement_tiroir_id bigint,
    officine_id uuid DEFAULT public.officine_courante() NOT NULL,
    part_organisme_rendue_avant numeric(12,2) DEFAULT 0 NOT NULL,
    CONSTRAINT ck_regularisation_annulation_montants CHECK (((annulation_dette_annulee >= (0)::numeric) AND (annulation_trop_paye_impute >= (0)::numeric) AND (annulation_rembourse >= (0)::numeric) AND (((annulation_dette_annulee + annulation_trop_paye_impute) + annulation_rembourse) <= montant_patient))),
    CONSTRAINT ck_regularisation_avoir CHECK (((sort <> 'avoir_patient'::text) OR ((montant_patient > (0)::numeric) AND (montant_patient = (montant_dette_diminuee + montant_a_rendre))))),
    CONSTRAINT ck_regularisation_patient CHECK (((sort <> 'charge_patient'::text) OR ((montant_patient = ecart) AND (credit_mouvement_id IS NOT NULL)))),
    CONSTRAINT ck_regularisation_rendue_avant CHECK ((part_organisme_rendue_avant >= (0)::numeric)),
    CONSTRAINT ck_regularisation_sens CHECK ((((ecart > (0)::numeric) AND (sort = ANY (ARRAY['charge_patient'::text, 'perte_pharmacie'::text]))) OR ((ecart < (0)::numeric) AND (sort = ANY (ARRAY['avoir_patient'::text, 'gain_organisme'::text]))))),
    CONSTRAINT ck_regularisation_statut CHECK ((((statut = 'active'::text) AND (annulee_le IS NULL) AND (annulee_par IS NULL)) OR ((statut = 'annulee'::text) AND (annulee_le IS NOT NULL) AND (annulee_par IS NOT NULL)))),
    CONSTRAINT regularisation_part_organisme_ecart_check CHECK ((ecart <> (0)::numeric)),
    CONSTRAINT regularisation_part_organisme_montant_a_rendre_check CHECK ((montant_a_rendre >= (0)::numeric)),
    CONSTRAINT regularisation_part_organisme_montant_dette_diminuee_check CHECK ((montant_dette_diminuee >= (0)::numeric)),
    CONSTRAINT regularisation_part_organisme_montant_patient_check CHECK ((montant_patient >= (0)::numeric)),
    CONSTRAINT regularisation_part_organisme_part_organisme_chifa_check CHECK ((part_organisme_chifa >= (0)::numeric)),
    CONSTRAINT regularisation_part_organisme_sort_check CHECK ((sort = ANY (ARRAY['charge_patient'::text, 'perte_pharmacie'::text, 'avoir_patient'::text, 'gain_organisme'::text])))
);


--
-- Name: TABLE regularisation_part_organisme; Type: COMMENT; Schema: public; Owner: -
--

COMMENT ON TABLE public.regularisation_part_organisme IS 'Écart de part organisme au rattachement du volet Chifa : créance ramenée à la part Chifa, différence à la charge du patient, perte pharmacie, avoir patient ou gain organisme ; jamais modifiée ni supprimée';


--
-- Name: COLUMN regularisation_part_organisme.part_organisme_chifa; Type: COMMENT; Schema: public; Owner: -
--

COMMENT ON COLUMN public.regularisation_part_organisme.part_organisme_chifa IS 'Part calculée sur le prix Chifa (hors majoration) des boîtes servies encore chez le patient au rattachement (nettes des retours, script 157)';


--
-- Name: COLUMN regularisation_part_organisme.ecart; Type: COMMENT; Schema: public; Owner: -
--

COMMENT ON COLUMN public.regularisation_part_organisme.ecart IS 'Part organisme enregistrée à la caisse moins part calculée sur le prix Chifa : > 0 la caisse paiera moins, < 0 elle paiera plus';


--
-- Name: COLUMN regularisation_part_organisme.montant_pharmacie; Type: COMMENT; Schema: public; Owner: -
--

COMMENT ON COLUMN public.regularisation_part_organisme.montant_pharmacie IS 'Effet pour la pharmacie : perte (négatif, perte_pharmacie) ou gain (positif, gain_organisme et part non rendue)';


--
-- Name: COLUMN regularisation_part_organisme.statut; Type: COMMENT; Schema: public; Owner: -
--

COMMENT ON COLUMN public.regularisation_part_organisme.statut IS 'active ; annulee : vente annulée (script 098), jamais supprimée';


--
-- Name: COLUMN regularisation_part_organisme.annulation_dette_annulee; Type: COMMENT; Schema: public; Owner: -
--

COMMENT ON COLUMN public.regularisation_part_organisme.annulation_dette_annulee IS 'Part encore due annulée par écriture inverse (annulation_credit_mouvement_id)';


--
-- Name: COLUMN regularisation_part_organisme.annulation_trop_paye_impute; Type: COMMENT; Schema: public; Owner: -
--

COMMENT ON COLUMN public.regularisation_part_organisme.annulation_trop_paye_impute IS 'Trop-payé déduit des autres dettes du patient, à son choix';


--
-- Name: COLUMN regularisation_part_organisme.annulation_rembourse; Type: COMMENT; Schema: public; Owner: -
--

COMMENT ON COLUMN public.regularisation_part_organisme.annulation_rembourse IS 'Trop-payé rendu par le tiroir (annulation_mouvement_tiroir_id)';


--
-- Name: COLUMN regularisation_part_organisme.part_organisme_rendue_avant; Type: COMMENT; Schema: public; Owner: -
--

COMMENT ON COLUMN public.regularisation_part_organisme.part_organisme_rendue_avant IS 'Part de l''organisme déjà retirée par des retours avant le rattachement du volet (script 157) ; part_organisme_chifa porte alors sur les seules boîtes encore chez le patient';


--
-- Name: regularisation_part_organisme_id_seq; Type: SEQUENCE; Schema: public; Owner: -
--

ALTER TABLE public.regularisation_part_organisme ALTER COLUMN id ADD GENERATED ALWAYS AS IDENTITY (
    SEQUENCE NAME public.regularisation_part_organisme_id_seq
    START WITH 1
    INCREMENT BY 1
    NO MINVALUE
    NO MAXVALUE
    CACHE 1
);


--
-- Name: remboursement_titulaire; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public.remboursement_titulaire (
    id bigint NOT NULL,
    date_remboursement date NOT NULL,
    montant numeric(12,2) NOT NULL,
    compte_id bigint NOT NULL,
    mouvement_id bigint NOT NULL,
    libelle text NOT NULL,
    reference text,
    utilisateur_id bigint NOT NULL,
    cree_le timestamp with time zone DEFAULT now() NOT NULL,
    officine_id uuid DEFAULT public.officine_courante() NOT NULL,
    CONSTRAINT remboursement_titulaire_montant_check CHECK ((montant > (0)::numeric))
);


--
-- Name: TABLE remboursement_titulaire; Type: COMMENT; Schema: public; Owner: -
--

COMMENT ON TABLE public.remboursement_titulaire IS 'Le titulaire se rembourse ses apports : sortie du coffre ou d''une banque (mouvement remboursement_financement)';


--
-- Name: remboursement_titulaire_id_seq; Type: SEQUENCE; Schema: public; Owner: -
--

ALTER TABLE public.remboursement_titulaire ALTER COLUMN id ADD GENERATED ALWAYS AS IDENTITY (
    SEQUENCE NAME public.remboursement_titulaire_id_seq
    START WITH 1
    INCREMENT BY 1
    NO MINVALUE
    NO MAXVALUE
    CACHE 1
);


--
-- Name: remise_automatique; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public.remise_automatique (
    id bigint NOT NULL,
    produit_id bigint,
    categorie text,
    pourcentage numeric(5,2) NOT NULL,
    date_debut date NOT NULL,
    date_fin date NOT NULL,
    motif text,
    cree_par bigint,
    cree_le timestamp with time zone DEFAULT now() NOT NULL,
    modifiee_par bigint,
    modifiee_le timestamp with time zone,
    arretee_par bigint,
    arretee_le timestamp with time zone,
    officine_id uuid DEFAULT public.officine_courante() NOT NULL,
    modele_cle text,
    CONSTRAINT ck_remise_auto_cible CHECK (((produit_id IS NULL) <> (categorie IS NULL))),
    CONSTRAINT ck_remise_auto_createur CHECK (((cree_par IS NOT NULL) OR (modele_cle IS NOT NULL))),
    CONSTRAINT ck_remise_auto_dates CHECK ((date_fin >= date_debut)),
    CONSTRAINT ck_remise_auto_modele CHECK (((modele_cle IS NULL) OR ((categorie IS NOT NULL) AND (modele_cle ~ '^c_[A-Za-z0-9]{12}$'::text)))),
    CONSTRAINT ck_remise_auto_motif CHECK (((motif IS NULL) OR (length(motif) <= 200))),
    CONSTRAINT ck_remise_auto_pct CHECK (((pourcentage > (0)::numeric) AND (pourcentage <= (100)::numeric) AND (pourcentage = trunc(pourcentage))))
);


--
-- Name: TABLE remise_automatique; Type: COMMENT; Schema: public; Owner: -
--

COMMENT ON TABLE public.remise_automatique IS 'Remises automatiques (%) par produit ou par catégorie, appliquées par la caisse en vente libre (jamais sur un produit remboursable)';


--
-- Name: COLUMN remise_automatique.modele_cle; Type: COMMENT; Schema: public; Owner: -
--

COMMENT ON COLUMN public.remise_automatique.modele_cle IS 'Clé stable (c_ + 12 caractères) d''une remise du serveur central, par catégorie ; NULL pour une remise locale (script 141).';


--
-- Name: remise_automatique_id_seq; Type: SEQUENCE; Schema: public; Owner: -
--

ALTER TABLE public.remise_automatique ALTER COLUMN id ADD GENERATED ALWAYS AS IDENTITY (
    SEQUENCE NAME public.remise_automatique_id_seq
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
    paiement_id bigint NOT NULL,
    officine_id uuid DEFAULT public.officine_courante() NOT NULL
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
    cree_le timestamp with time zone DEFAULT now() NOT NULL,
    officine_id uuid DEFAULT public.officine_courante() NOT NULL
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
    motif_code text,
    motif_texte text,
    officine_id uuid DEFAULT public.officine_courante() NOT NULL,
    chifa_corrige_le timestamp with time zone,
    chifa_corrige_par bigint,
    dette_avoir_retenu numeric(12,2) DEFAULT 0 NOT NULL,
    CONSTRAINT ck_retour_client_chifa_corrige CHECK (((chifa_corrige_le IS NULL) = (chifa_corrige_par IS NULL))),
    CONSTRAINT ck_retour_client_dette_avoir_retenu CHECK ((dette_avoir_retenu >= (0)::numeric)),
    CONSTRAINT ck_retour_client_motif_texte CHECK (((motif_texte IS NULL) OR ((motif_code IS NOT NULL) AND ((length(btrim(motif_texte)) >= 1) AND (length(btrim(motif_texte)) <= 200))))),
    CONSTRAINT retour_client_mode_check CHECK ((mode = ANY (ARRAY['especes'::text, 'carte'::text, 'cheque'::text, 'credit'::text, 'avoir'::text]))),
    CONSTRAINT retour_client_montant_rembourse_check CHECK ((montant_rembourse >= (0)::numeric)),
    CONSTRAINT retour_client_motif_check CHECK ((length(btrim(motif)) >= 3))
);


--
-- Name: COLUMN retour_client.chifa_corrige_le; Type: COMMENT; Schema: public; Owner: -
--

COMMENT ON COLUMN public.retour_client.chifa_corrige_le IS 'Retour avec part organisme d''une vente Chifa : facture corrigée dans CHIFA-Officine le (liste « Retours à corriger », script 155)';


--
-- Name: COLUMN retour_client.chifa_corrige_par; Type: COMMENT; Schema: public; Owner: -
--

COMMENT ON COLUMN public.retour_client.chifa_corrige_par IS 'Employé qui a marqué la facture corrigée dans CHIFA-Officine (script 155)';


--
-- Name: COLUMN retour_client.dette_avoir_retenu; Type: COMMENT; Schema: public; Owner: -
--

COMMENT ON COLUMN public.retour_client.dette_avoir_retenu IS 'Part de la retenue (régularisation « rendu au patient ») que le retour ne couvrait pas, ajoutée à la dette du patient (script 159)';


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
    officine_id uuid DEFAULT public.officine_courante() NOT NULL,
    montant_organisme numeric(12,2) DEFAULT 0 NOT NULL,
    montant_regularisation numeric(12,2) DEFAULT 0 NOT NULL,
    montant_avoir_retenu numeric(12,2) DEFAULT 0 NOT NULL,
    CONSTRAINT ck_retour_client_ligne_montant_avoir_retenu CHECK ((montant_avoir_retenu >= (0)::numeric)),
    CONSTRAINT ck_retour_client_ligne_montant_organisme CHECK ((montant_organisme >= (0)::numeric)),
    CONSTRAINT ck_retour_client_ligne_montant_regularisation CHECK ((montant_regularisation >= (0)::numeric)),
    CONSTRAINT retour_client_ligne_destination_check CHECK ((destination = ANY (ARRAY['stock'::text, 'destruction'::text]))),
    CONSTRAINT retour_client_ligne_montant_check CHECK ((montant >= (0)::numeric)),
    CONSTRAINT retour_client_ligne_quantite_check CHECK ((quantite > 0))
);


--
-- Name: COLUMN retour_client_ligne.montant_organisme; Type: COMMENT; Schema: public; Owner: -
--

COMMENT ON COLUMN public.retour_client_ligne.montant_organisme IS 'Part de l''organisme des boîtes rendues, retirée de la créance (v_vente_part_organisme) : prorata de la part retenue (Chifa si régularisée), majoration non comprise (script 155)';


--
-- Name: COLUMN retour_client_ligne.montant_regularisation; Type: COMMENT; Schema: public; Owner: -
--

COMMENT ON COLUMN public.retour_client_ligne.montant_regularisation IS 'Part de la régularisation « à la charge du patient » (rattachement du volet) des boîtes rendues, rendue au patient ; comprise dans retour_client.montant_rembourse (script 158)';


--
-- Name: COLUMN retour_client_ligne.montant_avoir_retenu; Type: COMMENT; Schema: public; Owner: -
--

COMMENT ON COLUMN public.retour_client_ligne.montant_avoir_retenu IS 'Part de la régularisation « rendu au patient » (rattachement du volet) des boîtes rendues, déjà rendue au patient et retenue sur ce que le retour lui rend (script 159)';


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
    officine_id uuid DEFAULT public.officine_courante() NOT NULL,
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
    officine_id uuid DEFAULT public.officine_courante() NOT NULL,
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
    permission_code text NOT NULL,
    officine_id uuid DEFAULT public.officine_courante() NOT NULL
);


--
-- Name: rupture_grossiste; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public.rupture_grossiste (
    id bigint NOT NULL,
    fournisseur_id bigint NOT NULL,
    produit_id bigint NOT NULL,
    debut timestamp with time zone DEFAULT now() NOT NULL,
    import_debut_id bigint,
    fin timestamp with time zone,
    import_fin_id bigint,
    officine_id uuid DEFAULT public.officine_courante() NOT NULL,
    CONSTRAINT ck_rupture_grossiste_periode CHECK (((fin IS NULL) OR (fin >= debut)))
);


--
-- Name: rupture_grossiste_id_seq; Type: SEQUENCE; Schema: public; Owner: -
--

ALTER TABLE public.rupture_grossiste ALTER COLUMN id ADD GENERATED ALWAYS AS IDENTITY (
    SEQUENCE NAME public.rupture_grossiste_id_seq
    START WITH 1
    INCREMENT BY 1
    NO MINVALUE
    NO MAXVALUE
    CACHE 1
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
-- Name: TABLE scan_reception; Type: COMMENT; Schema: public; Owner: -
--

COMMENT ON TABLE public.scan_reception IS 'Boîtes scannées sur le mobile au déballage, en attente d''import dans une réception du poste';


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
-- Name: sceau_integrite; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public.sceau_integrite (
    id bigint NOT NULL,
    journal text NOT NULL,
    cle_chaine text DEFAULT 'officine'::text NOT NULL,
    motif text NOT NULL,
    periode_du date NOT NULL,
    periode_au date NOT NULL,
    scelle_le timestamp with time zone NOT NULL,
    dernier_rang bigint NOT NULL,
    dernier_id bigint,
    dernier_hash bytea NOT NULL,
    algorithme text DEFAULT 'HMAC-SHA256'::text NOT NULL,
    empreinte_cle text NOT NULL,
    sceau bytea NOT NULL,
    auteur_id bigint,
    officine_id uuid DEFAULT public.officine_courante() NOT NULL,
    CONSTRAINT ck_sceau_integrite_motif CHECK ((motif = ANY (ARRAY['cloture_journaliere'::text, 'cloture_mensuelle'::text, 'manuel'::text]))),
    CONSTRAINT ck_sceau_integrite_periode CHECK ((periode_au >= periode_du)),
    CONSTRAINT ck_sceau_integrite_rang CHECK ((dernier_rang >= 0))
);


--
-- Name: sceau_integrite_id_seq; Type: SEQUENCE; Schema: public; Owner: -
--

ALTER TABLE public.sceau_integrite ALTER COLUMN id ADD GENERATED ALWAYS AS IDENTITY (
    SEQUENCE NAME public.sceau_integrite_id_seq
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
-- Name: seq_numero_bon_sortie; Type: SEQUENCE; Schema: public; Owner: -
--

CREATE SEQUENCE public.seq_numero_bon_sortie
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
-- Name: seq_numero_recu; Type: SEQUENCE; Schema: public; Owner: -
--

CREATE SEQUENCE public.seq_numero_recu
    START WITH 1
    INCREMENT BY 1
    NO MINVALUE
    NO MAXVALUE
    CACHE 1;


--
-- Name: seq_numero_reglement_fournisseur; Type: SEQUENCE; Schema: public; Owner: -
--

CREATE SEQUENCE public.seq_numero_reglement_fournisseur
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
-- Name: serialisation_reponse_factice; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public.serialisation_reponse_factice (
    id bigint NOT NULL,
    gtin text,
    numero_serie text,
    numero_lot text,
    operation text,
    resultat text NOT NULL,
    commentaire text,
    CONSTRAINT serialisation_reponse_factice_operation_check CHECK ((operation = ANY (ARRAY['verifier'::text, 'decommissionner'::text, 'reactiver'::text]))),
    CONSTRAINT serialisation_reponse_factice_resultat_check CHECK ((resultat = ANY (ARRAY['authentique'::text, 'inconnue'::text, 'deja_decommissionnee'::text, 'rappelee'::text, 'expiree'::text])))
);


--
-- Name: serialisation_reponse_factice_id_seq; Type: SEQUENCE; Schema: public; Owner: -
--

ALTER TABLE public.serialisation_reponse_factice ALTER COLUMN id ADD GENERATED ALWAYS AS IDENTITY (
    SEQUENCE NAME public.serialisation_reponse_factice_id_seq
    START WITH 1
    INCREMENT BY 1
    NO MINVALUE
    NO MAXVALUE
    CACHE 1
);


--
-- Name: serveur_ecarte; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public.serveur_ecarte (
    id bigint NOT NULL,
    ecarte_le timestamp with time zone DEFAULT now() NOT NULL,
    serveur_retenu character varying(200) NOT NULL,
    timeline_locale integer NOT NULL,
    timeline_retenue integer,
    identifiant_systeme character varying(40) NOT NULL,
    motif character varying(500) NOT NULL,
    leve_le timestamp with time zone,
    leve_par character varying(200),
    leve_motif character varying(500),
    cree_le timestamp with time zone DEFAULT now() NOT NULL,
    nature character varying(20) DEFAULT 'bascule'::character varying NOT NULL,
    CONSTRAINT ck_serveur_ecarte_levee CHECK ((((leve_le IS NULL) AND (leve_par IS NULL) AND (leve_motif IS NULL)) OR ((leve_le IS NOT NULL) AND (leve_par IS NOT NULL) AND (leve_motif IS NOT NULL)))),
    CONSTRAINT ck_serveur_ecarte_nature CHECK (((nature)::text = ANY (ARRAY['bascule'::text, 'retrait'::text]))),
    CONSTRAINT ck_serveur_ecarte_timelines CHECK (((((nature)::text = 'bascule'::text) AND (timeline_retenue IS NOT NULL) AND (timeline_retenue > timeline_locale)) OR (((nature)::text = 'retrait'::text) AND ((timeline_retenue IS NULL) OR (timeline_retenue > timeline_locale))))),
    CONSTRAINT serveur_ecarte_timeline_locale_check CHECK ((timeline_locale >= 1))
);


--
-- Name: TABLE serveur_ecarte; Type: COMMENT; Schema: public; Owner: -
--

COMMENT ON TABLE public.serveur_ecarte IS 'État « serveur écarté » (lecture seule) après une bascule vers un autre serveur principal ; ajout seul, seule la levée modifie une ligne ; jamais envoyée au central (script 163).';


--
-- Name: COLUMN serveur_ecarte.nature; Type: COMMENT; Schema: public; Owner: -
--

COMMENT ON COLUMN public.serveur_ecarte.nature IS 'bascule : écart constaté après la promotion de l''autre serveur (163) ; retrait : mise hors jeu demandée par le 2e serveur avant sa promotion (166)';


--
-- Name: serveur_ecarte_id_seq; Type: SEQUENCE; Schema: public; Owner: -
--

ALTER TABLE public.serveur_ecarte ALTER COLUMN id ADD GENERATED ALWAYS AS IDENTITY (
    SEQUENCE NAME public.serveur_ecarte_id_seq
    START WITH 1
    INCREMENT BY 1
    NO MINVALUE
    NO MAXVALUE
    CACHE 1
);


--
-- Name: serveur_officine; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public.serveur_officine (
    nom text NOT NULL,
    role text DEFAULT 'second'::text NOT NULL,
    dossier_chifa text,
    dossier_mdn text,
    chifa_installe boolean DEFAULT false NOT NULL,
    lecteur_token boolean DEFAULT false NOT NULL,
    mdn_installe boolean DEFAULT false NOT NULL,
    douchette_testee boolean DEFAULT false NOT NULL,
    version_api text,
    cle_presente boolean,
    empreinte_cle text,
    imprimantes text,
    dossier_chifa_joignable boolean,
    dossier_mdn_joignable boolean,
    releve_le timestamp with time zone,
    modifie_le timestamp with time zone DEFAULT now() NOT NULL,
    modifie_par bigint,
    CONSTRAINT serveur_officine_empreinte_cle_check CHECK (((empreinte_cle IS NULL) OR (empreinte_cle ~ '^[0-9a-f]{16}$'::text))),
    CONSTRAINT serveur_officine_nom_check CHECK (((nom <> ''::text) AND (nom = upper(btrim(nom))))),
    CONSTRAINT serveur_officine_role_check CHECK ((role = ANY (ARRAY['principal'::text, 'second'::text])))
);


--
-- Name: TABLE serveur_officine; Type: COMMENT; Schema: public; Owner: -
--

COMMENT ON TABLE public.serveur_officine IS 'Serveurs de l''officine (principal, 2e serveur) : réglages propres à chacun, cases du titulaire, relevés (script 122).';


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
    officine_id uuid DEFAULT public.officine_courante() NOT NULL,
    CONSTRAINT session_caisse_check CHECK (((fermee_le IS NULL) = (montant_compte IS NULL))),
    CONSTRAINT session_caisse_fond_caisse_check CHECK ((fond_caisse >= (0)::numeric))
);


--
-- Name: COLUMN session_caisse.utilisateur_id; Type: COMMENT; Schema: public; Owner: -
--

COMMENT ON COLUMN public.session_caisse.utilisateur_id IS 'Employé qui a ouvert la caisse du poste (les ventes gardent leur vendeur)';


--
-- Name: COLUMN session_caisse.releve_de; Type: COMMENT; Schema: public; Owner: -
--

COMMENT ON COLUMN public.session_caisse.releve_de IS 'Session clôturée par une relève, juste avant celle-ci';


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
    officine_id uuid DEFAULT public.officine_courante() NOT NULL,
    CONSTRAINT substance_surveillee_classe_check CHECK ((classe = ANY (ARRAY['psychotrope'::text, 'stupefiant'::text])))
);


--
-- Name: substitution_liste_blanche; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public.substitution_liste_blanche (
    produit_id bigint NOT NULL,
    remplacant_id bigint NOT NULL,
    cree_le timestamp with time zone DEFAULT now() NOT NULL,
    cree_par bigint,
    officine_id uuid DEFAULT public.officine_courante() NOT NULL,
    CONSTRAINT ck_substitution_liste_blanche_distincts CHECK ((produit_id <> remplacant_id))
);


--
-- Name: surveillance_replication; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public.surveillance_replication (
    id integer DEFAULT 1 NOT NULL,
    seuil_secondes integer DEFAULT 300 NOT NULL,
    seuil_octets bigint DEFAULT 67108864 NOT NULL,
    derniere_alerte_le timestamp with time zone,
    modifie_le timestamp with time zone DEFAULT now() NOT NULL,
    ged_copie_minutes integer DEFAULT 5 NOT NULL,
    CONSTRAINT ck_surveillance_replication_ged_copie_minutes CHECK (((ged_copie_minutes >= 1) AND (ged_copie_minutes <= 60))),
    CONSTRAINT surveillance_replication_id_check CHECK ((id = 1)),
    CONSTRAINT surveillance_replication_seuil_octets_check CHECK ((seuil_octets > 0)),
    CONSTRAINT surveillance_replication_seuil_secondes_check CHECK (((seuil_secondes >= 10) AND (seuil_secondes <= 86400)))
);


--
-- Name: TABLE surveillance_replication; Type: COMMENT; Schema: public; Owner: -
--

COMMENT ON TABLE public.surveillance_replication IS 'Seuils d''alerte de retard de la réplication vers le 2e serveur (script 122).';


--
-- Name: COLUMN surveillance_replication.ged_copie_minutes; Type: COMMENT; Schema: public; Owner: -
--

COMMENT ON COLUMN public.surveillance_replication.ged_copie_minutes IS 'Copie de la GED vers le 2e serveur : toutes les N minutes (1 à 60, défaut 5), réglé dans Administration › 2e serveur, lu par scripts/second-serveur/copier-ged.ps1 dans la base locale du 2e serveur (script 143).';


--
-- Name: sync_chargement; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public.sync_chargement (
    numero integer NOT NULL,
    motif text NOT NULL,
    etat text DEFAULT 'demande'::text NOT NULL,
    demande_le timestamp with time zone DEFAULT now() NOT NULL,
    demande_par bigint,
    epoque integer,
    repere bigint,
    tables text[],
    lignes jsonb,
    lignes_total bigint DEFAULT 0 NOT NULL,
    constitue_le timestamp with time zone,
    reprendre_apres bigint,
    annonce_le timestamp with time zone,
    termine_le timestamp with time zone,
    absentes_central bigint,
    absentes_detail jsonb,
    CONSTRAINT ck_sync_chargement_pret CHECK (((etat = ANY (ARRAY['demande'::text, 'constitution'::text, 'remplace'::text])) OR ((repere IS NOT NULL) AND (epoque IS NOT NULL)))),
    CONSTRAINT sync_chargement_etat_check CHECK ((etat = ANY (ARRAY['demande'::text, 'constitution'::text, 'pret'::text, 'annonce'::text, 'termine'::text, 'remplace'::text]))),
    CONSTRAINT sync_chargement_lignes_total_check CHECK ((lignes_total >= 0)),
    CONSTRAINT sync_chargement_motif_check CHECK ((motif = ANY (ARRAY['activation'::text, 'demande'::text, 'reactivation'::text, 'lots_manquants'::text]))),
    CONSTRAINT sync_chargement_repere_check CHECK ((repere >= 0))
);


--
-- Name: TABLE sync_chargement; Type: COMMENT; Schema: public; Owner: -
--

COMMENT ON TABLE public.sync_chargement IS 'Synchronisation : chargements complets (activation, demande, réactivation, lots manquants), un seul en cours (script 129).';


--
-- Name: sync_chargement_numero_seq; Type: SEQUENCE; Schema: public; Owner: -
--

ALTER TABLE public.sync_chargement ALTER COLUMN numero ADD GENERATED ALWAYS AS IDENTITY (
    SEQUENCE NAME public.sync_chargement_numero_seq
    START WITH 1
    INCREMENT BY 1
    NO MINVALUE
    NO MAXVALUE
    CACHE 1
);


--
-- Name: sync_declaration_epoque; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public.sync_declaration_epoque (
    nouvelle_epoque integer NOT NULL,
    ancienne_epoque integer NOT NULL,
    dernier_lot_local bigint NOT NULL,
    dernier_lot_central bigint,
    detecte_le timestamp with time zone,
    decide_le timestamp with time zone DEFAULT now() NOT NULL,
    titulaire_id bigint,
    fins_chaine jsonb DEFAULT '[]'::jsonb NOT NULL,
    envoyee_le timestamp with time zone,
    avant_premier_lot boolean,
    resultat text,
    CONSTRAINT ck_sync_declaration_epoque_ordre CHECK ((nouvelle_epoque > ancienne_epoque)),
    CONSTRAINT sync_declaration_epoque_resultat_check CHECK ((length(resultat) <= 500))
);


--
-- Name: TABLE sync_declaration_epoque; Type: COMMENT; Schema: public; Owner: -
--

COMMENT ON TABLE public.sync_declaration_epoque IS 'Changement d''époque à déclarer au serveur central avant tout lot de la nouvelle époque (script 151, S6.5-D1).';


--
-- Name: sync_famille_colonne; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public.sync_famille_colonne (
    famille text NOT NULL,
    nom_table text NOT NULL,
    colonne text NOT NULL,
    CONSTRAINT sync_famille_colonne_famille_check CHECK ((famille = ANY (ARRAY['employes'::text, 'pharmacie'::text, 'fournisseurs'::text, 'courriel_commandes'::text, 'banque'::text, 'clients_factures'::text])))
);


--
-- Name: TABLE sync_famille_colonne; Type: COMMENT; Schema: public; Owner: -
--

COMMENT ON TABLE public.sync_famille_colonne IS 'Synchronisation : colonnes des familles de données nominatives réglées au serveur central (script 130).';


--
-- Name: sync_journal_descendant; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public.sync_journal_descendant (
    id bigint NOT NULL,
    le timestamp with time zone DEFAULT now() NOT NULL,
    nom_table text NOT NULL,
    cle text NOT NULL,
    version bigint,
    statut text NOT NULL,
    motif text,
    avant jsonb,
    apres jsonb,
    CONSTRAINT sync_journal_descendant_statut_check CHECK ((statut = ANY (ARRAY['applique'::text, 'table_inconnue'::text, 'refuse'::text, 'invalide'::text])))
);


--
-- Name: TABLE sync_journal_descendant; Type: COMMENT; Schema: public; Owner: -
--

COMMENT ON TABLE public.sync_journal_descendant IS 'Synchronisation : journal local de la voie descendante (appliqué, ignoré, refusé), sans donnée personnelle (script 136).';


--
-- Name: sync_journal_descendant_id_seq; Type: SEQUENCE; Schema: public; Owner: -
--

ALTER TABLE public.sync_journal_descendant ALTER COLUMN id ADD GENERATED ALWAYS AS IDENTITY (
    SEQUENCE NAME public.sync_journal_descendant_id_seq
    START WITH 1
    INCREMENT BY 1
    NO MINVALUE
    NO MAXVALUE
    CACHE 1
);


--
-- Name: sync_lot; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public.sync_lot (
    epoque integer NOT NULL,
    numero bigint NOT NULL,
    version_contrat text NOT NULL,
    nb_lignes integer NOT NULL,
    octets integer NOT NULL,
    empreinte text NOT NULL,
    constitue_le timestamp with time zone DEFAULT now() NOT NULL,
    accuse_le timestamp with time zone,
    familles text[],
    reforme_le timestamp with time zone,
    colonnes_retirees text[],
    CONSTRAINT sync_lot_empreinte_check CHECK ((empreinte ~ '^[0-9a-f]{64}$'::text)),
    CONSTRAINT sync_lot_nb_lignes_check CHECK ((nb_lignes >= 1)),
    CONSTRAINT sync_lot_numero_check CHECK ((numero >= 1)),
    CONSTRAINT sync_lot_octets_check CHECK ((octets >= 0))
);


--
-- Name: TABLE sync_lot; Type: COMMENT; Schema: public; Owner: -
--

COMMENT ON TABLE public.sync_lot IS 'Synchronisation : lots constitués, numérotés sans trou par époque à la constitution (script 126).';


--
-- Name: COLUMN sync_lot.familles; Type: COMMENT; Schema: public; Owner: -
--

COMMENT ON COLUMN public.sync_lot.familles IS 'Familles nominatives appliquées à la constitution (NULL : lot antérieur au script 130, sans retrait) (script 130).';


--
-- Name: COLUMN sync_lot.colonnes_retirees; Type: COMMENT; Schema: public; Owner: -
--

COMMENT ON COLUMN public.sync_lot.colonnes_retirees IS 'Colonnes (« table.colonne ») retirées à la constitution, figées pour le renvoi à l''identique ; NULL : lot antérieur au script 136 (script 136).';


--
-- Name: sync_ordre_recu; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public.sync_ordre_recu (
    id uuid NOT NULL,
    type text NOT NULL,
    cible text,
    attendu jsonb DEFAULT '{}'::jsonb NOT NULL,
    donnees jsonb DEFAULT '{}'::jsonb NOT NULL,
    auteur text NOT NULL,
    cree_le timestamp with time zone NOT NULL,
    expire_le timestamp with time zone NOT NULL,
    version bigint NOT NULL,
    statut text DEFAULT 'a_appliquer'::text NOT NULL,
    motif text,
    recu_le timestamp with time zone DEFAULT now() NOT NULL,
    traite_le timestamp with time zone,
    traite_par bigint,
    message_ferme_le timestamp with time zone,
    message_ferme_par bigint,
    CONSTRAINT ck_sync_ordre_recu_message_ferme CHECK (((message_ferme_le IS NULL) OR ((type = 'message'::text) AND (statut = 'applique'::text) AND (message_ferme_par IS NOT NULL)))),
    CONSTRAINT sync_ordre_recu_attendu_check CHECK ((jsonb_typeof(attendu) = 'object'::text)),
    CONSTRAINT sync_ordre_recu_auteur_check CHECK (((length(auteur) >= 1) AND (length(auteur) <= 100))),
    CONSTRAINT sync_ordre_recu_cible_check CHECK ((length(cible) <= 100)),
    CONSTRAINT sync_ordre_recu_donnees_check CHECK ((jsonb_typeof(donnees) = 'object'::text)),
    CONSTRAINT sync_ordre_recu_motif_check CHECK ((length(motif) <= 500)),
    CONSTRAINT sync_ordre_recu_statut_check CHECK ((statut = ANY (ARRAY['a_appliquer'::text, 'a_confirmer'::text, 'applique'::text, 'refuse'::text, 'expire'::text, 'annule'::text]))),
    CONSTRAINT sync_ordre_recu_type_check CHECK ((type ~ '^[a-z_]{1,40}$'::text)),
    CONSTRAINT sync_ordre_recu_version_check CHECK ((version >= 1))
);


--
-- Name: TABLE sync_ordre_recu; Type: COMMENT; Schema: public; Owner: -
--

COMMENT ON TABLE public.sync_ordre_recu IS 'Ordres reçus du serveur central (format ordre-v1), appliqués par l''API locale ; jamais envoyée (script 145).';


--
-- Name: COLUMN sync_ordre_recu.message_ferme_le; Type: COMMENT; Schema: public; Owner: -
--

COMMENT ON COLUMN public.sync_ordre_recu.message_ferme_le IS 'Message du titulaire à distance fermé par le titulaire de l''officine (plus affiché sur les postes), script 160.';


--
-- Name: COLUMN sync_ordre_recu.message_ferme_par; Type: COMMENT; Schema: public; Owner: -
--

COMMENT ON COLUMN public.sync_ordre_recu.message_ferme_par IS 'Titulaire de l''officine qui a fermé le message, script 160.';


--
-- Name: sync_ordre_resultat; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public.sync_ordre_resultat (
    ordre_id uuid NOT NULL,
    statut text NOT NULL,
    motif text,
    traite_le timestamp with time zone DEFAULT now() NOT NULL,
    officine_id uuid DEFAULT public.officine_courante() NOT NULL,
    CONSTRAINT sync_ordre_resultat_motif_check CHECK ((length(motif) <= 500)),
    CONSTRAINT sync_ordre_resultat_statut_check CHECK ((statut = ANY (ARRAY['a_confirmer'::text, 'applique'::text, 'refuse'::text, 'expire'::text, 'annule'::text])))
);


--
-- Name: TABLE sync_ordre_resultat; Type: COMMENT; Schema: public; Owner: -
--

COMMENT ON TABLE public.sync_ordre_resultat IS 'Résultat de chaque ordre du serveur central, envoyé au central (script 145).';


--
-- Name: sync_pseudonyme; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public.sync_pseudonyme (
    nature text NOT NULL,
    cle_locale text NOT NULL,
    code text NOT NULL,
    cree_le timestamp with time zone DEFAULT now() NOT NULL,
    CONSTRAINT sync_pseudonyme_code_check CHECK ((code ~ '^[PR]-[0-9a-f]{24}$'::text)),
    CONSTRAINT sync_pseudonyme_nature_check CHECK ((nature = ANY (ARRAY['patient'::text, 'porteur'::text])))
);


--
-- Name: TABLE sync_pseudonyme; Type: COMMENT; Schema: public; Owner: -
--

COMMENT ON TABLE public.sync_pseudonyme IS 'Synchronisation : correspondance locale personne <-> code aléatoire stable ; jamais envoyée (script 126).';


--
-- Name: sync_sortie; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public.sync_sortie (
    id bigint NOT NULL,
    nom_table text NOT NULL,
    id_local text NOT NULL,
    operation text NOT NULL,
    donnees jsonb,
    version_contrat text NOT NULL,
    epoque integer NOT NULL,
    survenu_le timestamp with time zone DEFAULT clock_timestamp() NOT NULL,
    lot_numero bigint,
    ordre bigint,
    chargement integer,
    CONSTRAINT ck_sync_sortie_donnees CHECK (((operation = 'suppression'::text) = (donnees IS NULL))),
    CONSTRAINT ck_sync_sortie_instantane CHECK (((operation = 'instantane'::text) = ((chargement IS NOT NULL) AND (ordre IS NOT NULL)))),
    CONSTRAINT sync_sortie_operation_check CHECK ((operation = ANY (ARRAY['creation'::text, 'modification'::text, 'suppression'::text, 'instantane'::text])))
);


--
-- Name: TABLE sync_sortie; Type: COMMENT; Schema: public; Owner: -
--

COMMENT ON TABLE public.sync_sortie IS 'Synchronisation : file des modifications à envoyer (outbox), ajout seul tant que non acquittée (script 126).';


--
-- Name: COLUMN sync_sortie.ordre; Type: COMMENT; Schema: public; Owner: -
--

COMMENT ON COLUMN public.sync_sortie.ordre IS 'Ordre d''envoi d''une ligne d''instantané (repère du chargement) ; NULL = n° de la ligne (script 129).';


--
-- Name: sync_sortie_id_seq; Type: SEQUENCE; Schema: public; Owner: -
--

ALTER TABLE public.sync_sortie ALTER COLUMN id ADD GENERATED ALWAYS AS IDENTITY (
    SEQUENCE NAME public.sync_sortie_id_seq
    START WITH 1
    INCREMENT BY 1
    NO MINVALUE
    NO MAXVALUE
    CACHE 1
);


--
-- Name: type_prestation; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public.type_prestation (
    id bigint NOT NULL,
    code text NOT NULL,
    libelle text NOT NULL,
    prix numeric(12,2) DEFAULT 0 NOT NULL,
    taux_tva numeric(5,2) DEFAULT 0 NOT NULL,
    actif boolean DEFAULT true NOT NULL,
    cree_le timestamp with time zone DEFAULT now() NOT NULL,
    remboursable boolean DEFAULT false NOT NULL,
    tarif_reference numeric(12,2),
    officine_id uuid DEFAULT public.officine_courante() NOT NULL,
    CONSTRAINT ck_type_prestation_code CHECK (((length(btrim(code)) >= 1) AND (length(btrim(code)) <= 20))),
    CONSTRAINT ck_type_prestation_libelle CHECK (((length(btrim(libelle)) >= 1) AND (length(btrim(libelle)) <= 120))),
    CONSTRAINT ck_type_prestation_prix CHECK ((prix >= (0)::numeric)),
    CONSTRAINT ck_type_prestation_remboursable CHECK (((remboursable AND (tarif_reference IS NOT NULL) AND (tarif_reference > (0)::numeric)) OR ((NOT remboursable) AND (tarif_reference IS NULL)))),
    CONSTRAINT ck_type_prestation_tva CHECK (((taux_tva >= (0)::numeric) AND (taux_tva <= (100)::numeric)))
);


--
-- Name: TABLE type_prestation; Type: COMMENT; Schema: public; Owner: -
--

COMMENT ON TABLE public.type_prestation IS 'Prestations vendues à la caisse, sans stock (script 105) ; droit catalogue.creer ; jamais supprimées';


--
-- Name: COLUMN type_prestation.prix; Type: COMMENT; Schema: public; Owner: -
--

COMMENT ON COLUMN public.type_prestation.prix IS 'Prix TTC proposé à la caisse (0 : à fixer par le titulaire, vente refusée tant qu''il vaut 0)';


--
-- Name: COLUMN type_prestation.remboursable; Type: COMMENT; Schema: public; Owner: -
--

COMMENT ON COLUMN public.type_prestation.remboursable IS 'Prestation prise en charge par l''organisme en tiers payant (script 108) : taux × min(prix, tarif de référence)';


--
-- Name: COLUMN type_prestation.tarif_reference; Type: COMMENT; Schema: public; Owner: -
--

COMMENT ON COLUMN public.type_prestation.tarif_reference IS 'Tarif de référence du remboursement (script 108) ; obligatoire (> 0) si remboursable, NULL sinon';


--
-- Name: type_prestation_id_seq; Type: SEQUENCE; Schema: public; Owner: -
--

ALTER TABLE public.type_prestation ALTER COLUMN id ADD GENERATED ALWAYS AS IDENTITY (
    SEQUENCE NAME public.type_prestation_id_seq
    START WITH 1
    INCREMENT BY 1
    NO MINVALUE
    NO MAXVALUE
    CACHE 1
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
    telephone text,
    email text,
    jetons_revoques_le timestamp with time zone,
    peut_donner boolean DEFAULT false NOT NULL,
    remise_max_pct numeric(5,2) DEFAULT 0 NOT NULL,
    gere_remises_auto boolean DEFAULT false NOT NULL,
    officine_id uuid DEFAULT public.officine_courante() NOT NULL,
    CONSTRAINT ck_utilisateur_affichage CHECK ((affichage = ANY (ARRAY['auto'::text, 'clair'::text, 'sombre'::text]))),
    CONSTRAINT ck_utilisateur_remise_max CHECK (((remise_max_pct >= (0)::numeric) AND (remise_max_pct <= (100)::numeric) AND (remise_max_pct = trunc(remise_max_pct))))
);


--
-- Name: COLUMN utilisateur.pin_hash; Type: COMMENT; Schema: public; Owner: -
--

COMMENT ON COLUMN public.utilisateur.pin_hash IS 'Code comptoir (haché) pour reprendre la caisse du poste ; accès limité au comptoir';


--
-- Name: COLUMN utilisateur.affichage; Type: COMMENT; Schema: public; Owner: -
--

COMMENT ON COLUMN public.utilisateur.affichage IS 'Affichage du poste : auto (suit Windows), clair ou sombre';


--
-- Name: COLUMN utilisateur.telephone; Type: COMMENT; Schema: public; Owner: -
--

COMMENT ON COLUMN public.utilisateur.telephone IS 'Téléphone de l''employé (facultatif)';


--
-- Name: COLUMN utilisateur.email; Type: COMMENT; Schema: public; Owner: -
--

COMMENT ON COLUMN public.utilisateur.email IS 'Adresse e-mail de l''employé (facultative)';


--
-- Name: COLUMN utilisateur.jetons_revoques_le; Type: COMMENT; Schema: public; Owner: -
--

COMMENT ON COLUMN public.utilisateur.jetons_revoques_le IS 'Connexions ouvertes avant cette date refusées (mot de passe changé, compte désactivé, code effacé, restauration)';


--
-- Name: COLUMN utilisateur.peut_donner; Type: COMMENT; Schema: public; Owner: -
--

COMMENT ON COLUMN public.utilisateur.peut_donner IS 'Peut faire un don (boîtes données, don partiel du reste à charge) sans autorisation ; réglé par le titulaire';


--
-- Name: COLUMN utilisateur.remise_max_pct; Type: COMMENT; Schema: public; Owner: -
--

COMMENT ON COLUMN public.utilisateur.remise_max_pct IS 'Remise maximale (%, entier) accordée seul ; au-delà, code d''un employé dont le maximum le permet ; réglé par le titulaire';


--
-- Name: COLUMN utilisateur.gere_remises_auto; Type: COMMENT; Schema: public; Owner: -
--

COMMENT ON COLUMN public.utilisateur.gere_remises_auto IS 'Peut créer, modifier et arrêter les remises automatiques ; réglé par le titulaire';


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
-- Name: utilisation_financement; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public.utilisation_financement (
    id bigint NOT NULL,
    source text NOT NULL,
    pret_id bigint,
    usage text NOT NULL,
    date_usage date NOT NULL,
    montant numeric(12,2) NOT NULL,
    libelle text NOT NULL,
    mouvement_id bigint,
    depense_id bigint,
    reglement_id bigint,
    paiement_effet_id bigint,
    utilisateur_id bigint NOT NULL,
    cree_le timestamp with time zone DEFAULT now() NOT NULL,
    officine_id uuid DEFAULT public.officine_courante() NOT NULL,
    CONSTRAINT utilisation_financement_check CHECK (((source = 'pret'::text) = (pret_id IS NOT NULL))),
    CONSTRAINT utilisation_financement_check1 CHECK (((usage <> 'tresorerie'::text) OR (mouvement_id IS NOT NULL))),
    CONSTRAINT utilisation_financement_check2 CHECK (((usage <> 'depense'::text) OR ((mouvement_id IS NOT NULL) AND (depense_id IS NOT NULL)))),
    CONSTRAINT utilisation_financement_check3 CHECK (((usage <> 'reglement'::text) OR (reglement_id IS NOT NULL))),
    CONSTRAINT utilisation_financement_check4 CHECK (((usage <> 'traite'::text) OR (paiement_effet_id IS NOT NULL))),
    CONSTRAINT utilisation_financement_montant_check CHECK ((montant > (0)::numeric)),
    CONSTRAINT utilisation_financement_source_check CHECK ((source = ANY (ARRAY['pret'::text, 'titulaire'::text]))),
    CONSTRAINT utilisation_financement_usage_check CHECK ((usage = ANY (ARRAY['tresorerie'::text, 'depense'::text, 'reglement'::text, 'traite'::text])))
);


--
-- Name: TABLE utilisation_financement; Type: COMMENT; Schema: public; Owner: -
--

COMMENT ON TABLE public.utilisation_financement IS 'Usage d''un prêt ou d''un apport du titulaire : versé au coffre ou en banque, dépense payée, facture ou traite d''un grossiste payée hors trésorerie. Un apport du titulaire est une somme que la pharmacie lui doit';


--
-- Name: COLUMN utilisation_financement.mouvement_id; Type: COMMENT; Schema: public; Owner: -
--

COMMENT ON COLUMN public.utilisation_financement.mouvement_id IS 'Entrée « financement » sur le compte (versement, ou passage de la dépense payée)';


--
-- Name: COLUMN utilisation_financement.depense_id; Type: COMMENT; Schema: public; Owner: -
--

COMMENT ON COLUMN public.utilisation_financement.depense_id IS 'Dépense payée par la source, inscrite sur le même compte que l''entrée (solde du compte inchangé)';


--
-- Name: utilisation_financement_id_seq; Type: SEQUENCE; Schema: public; Owner: -
--

ALTER TABLE public.utilisation_financement ALTER COLUMN id ADD GENERATED ALWAYS AS IDENTITY (
    SEQUENCE NAME public.utilisation_financement_id_seq
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
    reste_saisi boolean DEFAULT false NOT NULL,
    chifa_facture text,
    chifa_date date,
    chifa_qr text,
    remise_additionnee boolean DEFAULT false NOT NULL,
    montant_don numeric(12,2) DEFAULT 0 NOT NULL,
    cle_encaissement uuid,
    canal_organisme text DEFAULT 'logiciel'::text NOT NULL,
    remis_a_patient_id bigint,
    remis_a_nom text,
    pour_patient_id bigint,
    pour_nom text,
    chifa_date_ordonnance date,
    chifa_rattache_le timestamp with time zone,
    chifa_rattache_par bigint,
    chifa_ecart_motif text,
    chifa_ecart_detail text,
    chifa_ecart_part_organisme numeric(12,2),
    chifa_attente_motif_code text,
    chifa_attente_motif_texte text,
    centre_payeur_id bigint,
    chifa_date_soins date,
    chifa_saisi_par bigint,
    chifa_majoration_code text,
    chifa_majoration_montant numeric(12,2) DEFAULT 0 NOT NULL,
    chifa_exclue_le timestamp with time zone,
    chifa_exclue_par bigint,
    chifa_exclusion_motif_code text,
    chifa_exclusion_texte text,
    officine_id uuid DEFAULT public.officine_courante() NOT NULL,
    CONSTRAINT ck_vente_canal_organisme CHECK ((canal_organisme = ANY (ARRAY['logiciel'::text, 'chifa'::text, 'mdn'::text]))),
    CONSTRAINT ck_vente_chifa_attente_motif_texte CHECK (((chifa_attente_motif_texte IS NULL) OR ((char_length(btrim(chifa_attente_motif_texte)) >= 1) AND (char_length(btrim(chifa_attente_motif_texte)) <= 200)))),
    CONSTRAINT ck_vente_chifa_ecart_motif CHECK (((chifa_ecart_motif IS NULL) OR ((char_length(btrim(chifa_ecart_motif)) >= 1) AND (char_length(btrim(chifa_ecart_motif)) <= 500)))),
    CONSTRAINT ck_vente_chifa_exclusion CHECK ((((chifa_exclue_le IS NULL) AND (chifa_exclue_par IS NULL) AND (chifa_exclusion_motif_code IS NULL) AND (chifa_exclusion_texte IS NULL)) OR ((chifa_exclue_le IS NOT NULL) AND (chifa_exclue_par IS NOT NULL) AND (chifa_exclusion_motif_code IS NOT NULL)))),
    CONSTRAINT ck_vente_chifa_exclusion_texte CHECK (((chifa_exclusion_texte IS NULL) OR ((char_length(btrim(chifa_exclusion_texte)) >= 1) AND (char_length(btrim(chifa_exclusion_texte)) <= 200)))),
    CONSTRAINT ck_vente_chifa_facture_format CHECK (((chifa_facture IS NULL) OR (chifa_facture ~ '^[0-9]{8}$'::text))),
    CONSTRAINT ck_vente_chifa_majoration CHECK (((chifa_majoration_montant >= (0)::numeric) AND ((chifa_majoration_montant = (0)::numeric) OR (chifa_majoration_code IS NOT NULL)))),
    CONSTRAINT ck_vente_montant_don CHECK ((montant_don >= (0)::numeric)),
    CONSTRAINT ck_vente_pour_nom CHECK (((pour_nom IS NULL) OR ((char_length(btrim(pour_nom)) >= 1) AND (char_length(btrim(pour_nom)) <= 200)))),
    CONSTRAINT ck_vente_pour_un_seul CHECK (((pour_patient_id IS NULL) OR (pour_nom IS NULL))),
    CONSTRAINT ck_vente_remis_a_nom CHECK (((remis_a_nom IS NULL) OR ((char_length(btrim(remis_a_nom)) >= 1) AND (char_length(btrim(remis_a_nom)) <= 200)))),
    CONSTRAINT ck_vente_remis_a_un_seul CHECK (((remis_a_patient_id IS NULL) OR (remis_a_nom IS NULL))),
    CONSTRAINT vente_check CHECK (((type = 'libre'::text) OR ((organisme_id IS NOT NULL) AND (patient_id IS NOT NULL)))),
    CONSTRAINT vente_montant_remise_check CHECK ((montant_remise >= (0)::numeric)),
    CONSTRAINT vente_remise_pct_check CHECK (((remise_pct >= (0)::numeric) AND (remise_pct <= (100)::numeric))),
    CONSTRAINT vente_statut_check CHECK ((statut = ANY (ARRAY['en_cours'::text, 'validee'::text, 'annulee'::text]))),
    CONSTRAINT vente_taux_tiers_payant_check CHECK (((taux_tiers_payant >= (0)::numeric) AND (taux_tiers_payant <= (100)::numeric))),
    CONSTRAINT vente_type_check CHECK ((type = ANY (ARRAY['libre'::text, 'tiers_payant'::text])))
);


--
-- Name: COLUMN vente.num_facture_chifa; Type: COMMENT; Schema: public; Owner: -
--

COMMENT ON COLUMN public.vente.num_facture_chifa IS 'OBSOLÈTE (script 101, 2026-11-15) : remplacée par chifa_facture, plus lue ni écrite ; suppression prévue par le script 101b après validation du titulaire';


--
-- Name: COLUMN vente.reste_saisi; Type: COMMENT; Schema: public; Owner: -
--

COMMENT ON COLUMN public.vente.reste_saisi IS 'Tiers payant : reste à charge du patient saisi par le caissier (taux non appliqué)';


--
-- Name: COLUMN vente.chifa_facture; Type: COMMENT; Schema: public; Owner: -
--

COMMENT ON COLUMN public.vente.chifa_facture IS 'N° de la facture Chifa (8 chiffres) : seule colonne du n° de facture Chifa depuis le script 101 (volet repris, rattaché ou saisi)';


--
-- Name: COLUMN vente.chifa_qr; Type: COMMENT; Schema: public; Owner: -
--

COMMENT ON COLUMN public.vente.chifa_qr IS 'Contenu brut du QR du volet Chifa (format à confirmer avec l''éditeur de Chifa)';


--
-- Name: COLUMN vente.remise_additionnee; Type: COMMENT; Schema: public; Owner: -
--

COMMENT ON COLUMN public.vente.remise_additionnee IS 'La remise du vendeur s''ajoute aux remises automatiques (sinon elle ne vaut que pour les lignes sans remise automatique)';


--
-- Name: COLUMN vente.montant_don; Type: COMMENT; Schema: public; Owner: -
--

COMMENT ON COLUMN public.vente.montant_don IS 'Don partiel du reste à charge (réductions sur les médicaments), en DA ; montant_remise : remises hors médicaments';


--
-- Name: COLUMN vente.cle_encaissement; Type: COMMENT; Schema: public; Owner: -
--

COMMENT ON COLUMN public.vente.cle_encaissement IS 'Clé du panier envoyée par le poste : un nouvel essai du même encaissement rend la vente déjà enregistrée (pas de doublon)';


--
-- Name: COLUMN vente.canal_organisme; Type: COMMENT; Schema: public; Owner: -
--

COMMENT ON COLUMN public.vente.canal_organisme IS 'Qui facture la part organisme : logiciel (facture Pharma Officine), chifa (logiciel Chifa), mdn (organisme militaire) ; chifa et mdn ne sont jamais refacturées';


--
-- Name: COLUMN vente.remis_a_patient_id; Type: COMMENT; Schema: public; Owner: -
--

COMMENT ON COLUMN public.vente.remis_a_patient_id IS 'Vente avec ordonnance : membre de la famille qui récupère les médicaments (fiche patient)';


--
-- Name: COLUMN vente.remis_a_nom; Type: COMMENT; Schema: public; Owner: -
--

COMMENT ON COLUMN public.vente.remis_a_nom IS 'Vente avec ordonnance : personne qui récupère les médicaments (nom libre, hors fiche)';


--
-- Name: COLUMN vente.pour_patient_id; Type: COMMENT; Schema: public; Owner: -
--

COMMENT ON COLUMN public.vente.pour_patient_id IS 'Vente sans ordonnance : membre de la famille concerné (fiche patient)';


--
-- Name: COLUMN vente.pour_nom; Type: COMMENT; Schema: public; Owner: -
--

COMMENT ON COLUMN public.vente.pour_nom IS 'Vente sans ordonnance : personne concernée (nom libre, hors fiche)';


--
-- Name: COLUMN vente.chifa_date_ordonnance; Type: COMMENT; Schema: public; Owner: -
--

COMMENT ON COLUMN public.vente.chifa_date_ordonnance IS 'Date de l''ordonnance lue dans le QR du volet Chifa (7e zone, à confirmer avec l''éditeur de Chifa)';


--
-- Name: COLUMN vente.chifa_rattache_le; Type: COMMENT; Schema: public; Owner: -
--

COMMENT ON COLUMN public.vente.chifa_rattache_le IS 'Volet Chifa rattaché à la vente déjà encaissée (circuit « la caisse d''abord ») : date du rattachement';


--
-- Name: COLUMN vente.chifa_rattache_par; Type: COMMENT; Schema: public; Owner: -
--

COMMENT ON COLUMN public.vente.chifa_rattache_par IS 'Employé qui a rattaché le volet Chifa à la vente déjà encaissée';


--
-- Name: COLUMN vente.chifa_ecart_motif; Type: COMMENT; Schema: public; Owner: -
--

COMMENT ON COLUMN public.vente.chifa_ecart_motif IS 'Volet rattaché malgré des différences avec la vente : motif obligatoire (droit prix.modifier)';


--
-- Name: COLUMN vente.chifa_ecart_detail; Type: COMMENT; Schema: public; Owner: -
--

COMMENT ON COLUMN public.vente.chifa_ecart_detail IS 'Différences volet / vente au rattachement (produits, quantités, prix, part organisme)';


--
-- Name: COLUMN vente.chifa_ecart_part_organisme; Type: COMMENT; Schema: public; Owner: -
--

COMMENT ON COLUMN public.vente.chifa_ecart_part_organisme IS 'Part organisme de la vente moins part calculée sur le prix Chifa du volet (non recalculée)';


--
-- Name: COLUMN vente.chifa_attente_motif_code; Type: COMMENT; Schema: public; Owner: -
--

COMMENT ON COLUMN public.vente.chifa_attente_motif_code IS 'Vente « Facturée par Chifa » encaissée sans n° de facture : motif (motif_reglable, liste volet_absent) ; null avant 097';


--
-- Name: COLUMN vente.chifa_attente_motif_texte; Type: COMMENT; Schema: public; Owner: -
--

COMMENT ON COLUMN public.vente.chifa_attente_motif_texte IS 'Texte du motif « Autre » de la vente encaissée sans n° de facture Chifa';


--
-- Name: COLUMN vente.centre_payeur_id; Type: COMMENT; Schema: public; Owner: -
--

COMMENT ON COLUMN public.vente.centre_payeur_id IS 'Centre payeur de la vente Chifa (script 100)';


--
-- Name: COLUMN vente.chifa_date_soins; Type: COMMENT; Schema: public; Owner: -
--

COMMENT ON COLUMN public.vente.chifa_date_soins IS 'Date des soins de la vente Chifa, saisie (la 2e date du QR n''y est pas rangée automatiquement, D-1 ; script 100)';


--
-- Name: COLUMN vente.chifa_saisi_par; Type: COMMENT; Schema: public; Owner: -
--

COMMENT ON COLUMN public.vente.chifa_saisi_par IS 'Employé qui a saisi la vente dans CHIFA-Officine (défaut : le vendeur ; script 100)';


--
-- Name: COLUMN vente.chifa_majoration_code; Type: COMMENT; Schema: public; Owner: -
--

COMMENT ON COLUMN public.vente.chifa_majoration_code IS 'Type de majoration Chifa (motif_reglable, liste majoration_chifa ; script 100)';


--
-- Name: COLUMN vente.chifa_majoration_montant; Type: COMMENT; Schema: public; Owner: -
--

COMMENT ON COLUMN public.vente.chifa_majoration_montant IS 'Majoration Chifa, ajoutée à la part organisme (D-3 ; v_vente_part_organisme ; script 100)';


--
-- Name: COLUMN vente.chifa_exclue_le; Type: COMMENT; Schema: public; Owner: -
--

COMMENT ON COLUMN public.vente.chifa_exclue_le IS 'Vente Chifa exclue du traitement (hors attente de volet et bordereaux) ; null : non exclue (script 100)';


--
-- Name: COLUMN vente.chifa_exclusion_motif_code; Type: COMMENT; Schema: public; Owner: -
--

COMMENT ON COLUMN public.vente.chifa_exclusion_motif_code IS 'Motif d''exclusion (motif_reglable, liste chifa_exclusion ; script 100)';


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
          WHERE ((v.statut = 'validee'::text) AND (v.date_vente >= (now() - make_interval(days => prm_1.f))) AND (lv.produit_id IS NOT NULL))
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
         SELECT parametre_alerte.delai_fournisseur_jours,
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
          WHERE ((lot.date_peremption > CURRENT_DATE) AND (NOT lot.bloque))
          GROUP BY lot.produit_id
        ), derniere AS (
         SELECT lv.produit_id,
            max(v.date_vente) AS derniere_vente
           FROM (public.ligne_vente lv
             JOIN public.vente v ON ((v.id = lv.vente_id)))
          WHERE ((v.statut = 'validee'::text) AND (lv.produit_id IS NOT NULL))
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
-- Name: v_avoir_patient; Type: VIEW; Schema: public; Owner: -
--

CREATE VIEW public.v_avoir_patient AS
 SELECT patient_id,
    (sum(
        CASE
            WHEN (sens = 'credit'::text) THEN montant
            ELSE (- montant)
        END))::numeric(12,2) AS solde,
    max(cree_le) AS dernier_mouvement
   FROM public.avoir_mouvement
  GROUP BY patient_id;


--
-- Name: v_vente_part_organisme; Type: VIEW; Schema: public; Owner: -
--

CREATE VIEW public.v_vente_part_organisme AS
 SELECT v.id AS vente_id,
    v.organisme_id,
    v.part_organisme AS part_organisme_caisse,
    (((COALESCE((r.part_organisme_chifa + r.part_organisme_rendue_avant), v.part_organisme) - COALESCE(rt.organisme, (0)::numeric)) +
        CASE
            WHEN rt.plus_rien_rembourse THEN (0)::numeric
            ELSE v.chifa_majoration_montant
        END))::numeric(12,2) AS part_organisme_retenue,
    (
        CASE
            WHEN rt.plus_rien_rembourse THEN (0)::numeric
            ELSE v.chifa_majoration_montant
        END)::numeric(12,2) AS majoration_chifa
   FROM ((public.vente v
     LEFT JOIN public.regularisation_part_organisme r ON (((r.vente_id = v.id) AND (r.statut = 'active'::text))))
     LEFT JOIN LATERAL ( SELECT sum(rl.montant_organisme) AS organisme,
            ((sum(rl.montant_organisme) > (0)::numeric) AND (NOT (EXISTS ( SELECT 1
                   FROM public.ligne_vente l2
                  WHERE ((l2.vente_id = v.id) AND (l2.montant_organisme > (0)::numeric) AND (l2.quantite > COALESCE(( SELECT sum(x.quantite) AS sum
                           FROM public.retour_client_ligne x
                          WHERE (x.ligne_vente_id = l2.id)), (0)::bigint))))))) AS plus_rien_rembourse
           FROM (public.retour_client_ligne rl
             JOIN public.ligne_vente l ON ((l.id = rl.ligne_vente_id)))
          WHERE (l.vente_id = v.id)) rt ON (true));


--
-- Name: VIEW v_vente_part_organisme; Type: COMMENT; Schema: public; Owner: -
--

COMMENT ON VIEW public.v_vente_part_organisme IS 'Créance de l''organisme par vente : part Chifa (+ part déjà rendue avant le rattachement, script 157) si l''écart a été régularisé (script 097), sinon part enregistrée à la caisse ; moins la part des boîtes rendues, majoration retirée quand plus aucune boîte remboursée ne reste (script 155)';


--
-- Name: versement_bordereau; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public.versement_bordereau (
    id bigint NOT NULL,
    mouvement_id bigint NOT NULL,
    bordereau_id bigint NOT NULL,
    montant numeric(12,2) NOT NULL,
    officine_id uuid DEFAULT public.officine_courante() NOT NULL,
    CONSTRAINT versement_bordereau_montant_check CHECK ((montant > (0)::numeric))
);


--
-- Name: TABLE versement_bordereau; Type: COMMENT; Schema: public; Owner: -
--

COMMENT ON TABLE public.versement_bordereau IS 'Part d''un versement d''organisme (trésorerie) imputée à un bordereau (script 110)';


--
-- Name: v_bordereau; Type: VIEW; Schema: public; Owner: -
--

CREATE VIEW public.v_bordereau AS
 SELECT b.id,
    b.organisme_id,
    o.code AS organisme,
    b.canal,
    b.centre_payeur_id,
    b.numero,
    b.periode_debut,
    b.periode_fin,
    b.date_envoi AS date_depot,
    b.statut,
    b.montant_declare,
    (COALESCE(x.ventes, (0)::bigint))::integer AS ventes,
    (
        CASE
            WHEN (COALESCE(x.ventes, (0)::bigint) > 0) THEN x.attendu
            ELSE b.montant_total
        END)::numeric(12,2) AS attendu,
    (COALESCE(x.rejete, (0)::numeric))::numeric(12,2) AS rejete,
    (COALESCE(x.rejets_a_traiter, (0)::bigint))::integer AS rejets_a_traiter,
    (COALESCE(y.verse, (0)::numeric))::numeric(12,2) AS verse,
    (((
        CASE
            WHEN (COALESCE(x.ventes, (0)::bigint) > 0) THEN x.attendu
            ELSE b.montant_total
        END - COALESCE(x.rejete, (0)::numeric)) - COALESCE(y.verse, (0)::numeric)))::numeric(12,2) AS reste
   FROM (((public.bordereau b
     JOIN public.organisme o ON ((o.id = b.organisme_id)))
     LEFT JOIN LATERAL ( SELECT count(*) AS ventes,
            sum(po.part_organisme_retenue) AS attendu,
            sum(bv.montant_rejete) AS rejete,
            count(*) FILTER (WHERE ((bv.rejete_le IS NOT NULL) AND (bv.suite IS NULL))) AS rejets_a_traiter
           FROM (public.bordereau_vente bv
             JOIN public.v_vente_part_organisme po ON ((po.vente_id = bv.vente_id)))
          WHERE (bv.bordereau_id = b.id)) x ON (true))
     LEFT JOIN LATERAL ( SELECT sum(vb.montant) AS verse
           FROM public.versement_bordereau vb
          WHERE (vb.bordereau_id = b.id)) y ON (true));


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
    COALESCE(v.numero, 'Encaissement de dette'::text) AS origine,
    NULLIF(TRIM(BOTH FROM concat_ws(' '::text, pa.nom, pa.prenom)), ''::text) AS patient
   FROM ((public.paiement p
     LEFT JOIN public.vente v ON ((v.id = p.vente_id)))
     LEFT JOIN public.patient pa ON ((pa.id = COALESCE(v.patient_id, p.patient_id))))
  WHERE ((p.mode = 'cheque'::text) AND ((p.vente_id IS NULL) OR (v.statut = 'validee'::text)) AND (NOT (EXISTS ( SELECT 1
           FROM public.remise_cheque_paiement r
          WHERE (r.paiement_id = p.id)))));


--
-- Name: v_compte_titulaire; Type: VIEW; Schema: public; Owner: -
--

CREATE VIEW public.v_compte_titulaire AS
 SELECT 'apport'::text AS sens,
    f.date_usage AS date,
    f.montant,
    f.usage,
    f.libelle,
    f.id AS source_id,
    u.nom_complet AS employe,
    f.cree_le
   FROM (public.utilisation_financement f
     JOIN public.utilisateur u ON ((u.id = f.utilisateur_id)))
  WHERE (f.source = 'titulaire'::text)
UNION ALL
 SELECT 'remboursement'::text AS sens,
    r.date_remboursement AS date,
    r.montant,
    'remboursement'::text AS usage,
    (((r.libelle || ' ('::text) || c.nom) || ')'::text) AS libelle,
    r.id AS source_id,
    u.nom_complet AS employe,
    r.cree_le
   FROM ((public.remboursement_titulaire r
     JOIN public.compte_tresorerie c ON ((c.id = r.compte_id)))
     JOIN public.utilisateur u ON ((u.id = r.utilisateur_id)));


--
-- Name: v_credit_accorde; Type: VIEW; Schema: public; Owner: -
--

CREATE VIEW public.v_credit_accorde AS
 SELECT v.id AS vente_id,
    v.patient_id,
    v.numero,
    v.date_vente,
        CASE
            WHEN (v.statut = 'validee'::text) THEN (x.montant -
            CASE
                WHEN (EXISTS ( SELECT 1
                   FROM public.paiement p
                  WHERE ((p.vente_id = v.id) AND (p.mode = 'credit'::text)))) THEN COALESCE(( SELECT sum(rc.montant_rembourse) AS sum
                   FROM public.retour_client rc
                  WHERE (rc.vente_id = v.id)), (0)::numeric)
                ELSE COALESCE(( SELECT sum(rl.montant_regularisation) AS sum
                   FROM (public.retour_client_ligne rl
                     JOIN public.ligne_vente lr ON ((lr.id = rl.ligne_vente_id)))
                  WHERE (lr.vente_id = v.id)), (0)::numeric)
            END)
            ELSE x.dette_retenue
        END AS montant
   FROM (public.vente v
     JOIN LATERAL ( SELECT (((COALESCE(( SELECT sum(p.montant) AS sum
                   FROM public.paiement p
                  WHERE ((p.vente_id = v.id) AND (p.mode = 'credit'::text))), (0)::numeric) + COALESCE(( SELECT sum(r.montant_patient) AS sum
                   FROM public.regularisation_part_organisme r
                  WHERE ((r.vente_id = v.id) AND (r.sort = 'charge_patient'::text) AND (r.statut = 'active'::text))), (0)::numeric)) + COALESCE(( SELECT sum(bv.montant_rejete) AS sum
                   FROM public.bordereau_vente bv
                  WHERE ((bv.vente_id = v.id) AND (bv.suite = 'patient'::text))), (0)::numeric)) + COALESCE(( SELECT sum(rc.dette_avoir_retenu) AS sum
                   FROM public.retour_client rc
                  WHERE (rc.vente_id = v.id)), (0)::numeric)) AS montant,
            COALESCE(( SELECT sum(rc.dette_avoir_retenu) AS sum
                   FROM public.retour_client rc
                  WHERE (rc.vente_id = v.id)), (0)::numeric) AS dette_retenue) x ON ((((v.statut = 'validee'::text) AND (x.montant > (0)::numeric)) OR ((v.statut = 'annulee'::text) AND (x.dette_retenue > (0)::numeric)))))
  WHERE (v.patient_id IS NOT NULL);


--
-- Name: VIEW v_credit_accorde; Type: COMMENT; Schema: public; Owner: -
--

COMMENT ON VIEW public.v_credit_accorde IS 'Crédit accordé par vente : part à crédit, régularisation et rejet de bordereau à la charge du patient, dette ajoutée par la retenue d''une régularisation « rendu au patient » (script 159, gardée après annulation) ; moins ce que les retours ont rendu (vente à crédit : tout ; vente payée : la part de régularisation rendue, script 158)';


--
-- Name: v_echeance_pret; Type: VIEW; Schema: public; Owner: -
--

CREATE VIEW public.v_echeance_pret AS
 SELECT e.id,
    e.pret_id,
    p.reference AS pret,
    p.compte_id AS banque_id,
    COALESCE(c.nom, p.preteur) AS banque,
    e.rang,
    e.date_echeance,
    e.capital,
    e.interets,
    e.tva,
    e.marge,
    e.montant,
    e.capital_restant,
    pe.date_paiement,
    pe.moyen,
    pe.compte_id,
    cp.nom AS compte,
    pe.reference,
    pe.part_tiroir,
    u.nom_complet AS employe
   FROM (((((public.echeance_pret e
     JOIN public.pret_bancaire p ON ((p.id = e.pret_id)))
     LEFT JOIN public.compte_tresorerie c ON ((c.id = p.compte_id)))
     LEFT JOIN public.paiement_echeance_pret pe ON ((pe.echeance_id = e.id)))
     LEFT JOIN public.compte_tresorerie cp ON ((cp.id = pe.compte_id)))
     LEFT JOIN public.utilisateur u ON ((u.id = pe.utilisateur_id)));


--
-- Name: v_pret_bancaire; Type: VIEW; Schema: public; Owner: -
--

CREATE VIEW public.v_pret_bancaire AS
 SELECT p.id,
    p.compte_id,
    COALESCE(c.nom, p.preteur) AS banque,
    p.reference,
    p.objet,
    p.montant,
    p.date_pret,
    p.document,
    COALESCE(e.capital_paye, (0)::numeric) AS capital_rembourse,
    (p.montant - COALESCE(e.capital_paye, (0)::numeric)) AS reste_du,
    COALESCE(( SELECT sum(x.montant) AS sum
           FROM public.utilisation_financement x
          WHERE (x.pret_id = p.id)), (0)::numeric) AS utilise,
    (COALESCE(e.nombre, (0)::bigint))::integer AS echeances,
    (COALESCE(e.payees, (0)::bigint))::integer AS echeances_payees,
    (COALESCE(e.echues, (0)::bigint))::integer AS echues_non_payees,
    ( SELECT v.date_echeance
           FROM public.v_echeance_pret v
          WHERE ((v.pret_id = p.id) AND (v.date_paiement IS NULL))
          ORDER BY v.rang
         LIMIT 1) AS prochaine_echeance,
    ( SELECT v.montant
           FROM public.v_echeance_pret v
          WHERE ((v.pret_id = p.id) AND (v.date_paiement IS NULL))
          ORDER BY v.rang
         LIMIT 1) AS prochain_montant,
    u.nom_complet AS employe,
    p.cree_le
   FROM (((public.pret_bancaire p
     LEFT JOIN public.compte_tresorerie c ON ((c.id = p.compte_id)))
     JOIN public.utilisateur u ON ((u.id = p.utilisateur_id)))
     LEFT JOIN LATERAL ( SELECT count(*) AS nombre,
            count(v.date_paiement) AS payees,
            sum(v.capital) FILTER (WHERE (v.date_paiement IS NOT NULL)) AS capital_paye,
            count(*) FILTER (WHERE ((v.date_paiement IS NULL) AND (v.date_echeance < CURRENT_DATE))) AS echues
           FROM public.v_echeance_pret v
          WHERE (v.pret_id = p.id)) e ON (true));


--
-- Name: v_dette_financement; Type: VIEW; Schema: public; Owner: -
--

CREATE VIEW public.v_dette_financement AS
 SELECT 'dette'::text AS sens,
    'pret'::text AS categorie,
    NULL::bigint AS tiers_id,
    p.banque AS tiers,
    ((('Prêt '::text || p.reference) || COALESCE((' · '::text || p.objet), ''::text)) ||
        CASE
            WHEN (p.echues_non_payees > 0) THEN ' · échéance échue, non payée'::text
            ELSE ''::text
        END) AS libelle,
    p.date_pret AS date_reference,
    p.prochaine_echeance AS echeance,
    p.reste_du AS montant,
    false AS rejetee
   FROM public.v_pret_bancaire p
  WHERE (p.reste_du > (0)::numeric)
UNION ALL
 SELECT 'dette'::text AS sens,
    'titulaire'::text AS categorie,
    NULL::bigint AS tiers_id,
    'Titulaire'::text AS tiers,
    'Apports du titulaire non remboursés'::text AS libelle,
    t.depuis AS date_reference,
    NULL::date AS echeance,
    t.solde AS montant,
    false AS rejetee
   FROM ( SELECT COALESCE(sum(
                CASE
                    WHEN (v_compte_titulaire.sens = 'apport'::text) THEN v_compte_titulaire.montant
                    ELSE (- v_compte_titulaire.montant)
                END), (0)::numeric) AS solde,
            min(v_compte_titulaire.date) FILTER (WHERE (v_compte_titulaire.sens = 'apport'::text)) AS depuis
           FROM public.v_compte_titulaire) t
  WHERE (t.solde > (0)::numeric);


--
-- Name: v_echeance_pret_complete; Type: VIEW; Schema: public; Owner: -
--

CREATE VIEW public.v_echeance_pret_complete AS
 SELECT v.id,
    v.pret_id,
    v.pret,
    v.banque_id,
    v.banque,
    v.rang,
    v.date_echeance,
    v.capital,
    v.interets,
    v.tva,
    v.marge,
    v.montant,
    v.capital_restant,
    v.date_paiement,
    v.moyen,
    v.compte_id,
    v.compte,
    v.reference,
    v.part_tiroir,
    v.employe,
    e.assurance
   FROM (public.v_echeance_pret v
     JOIN public.echeance_pret e ON ((e.id = v.id)));


--
-- Name: v_vente_credit; Type: VIEW; Schema: public; Owner: -
--

CREATE VIEW public.v_vente_credit AS
 SELECT vente_id,
    patient_id,
    numero,
    date_vente,
    montant,
    LEAST((impute + LEAST(GREATEST((libre - ((ouverture + cumul) - capacite)), (0)::numeric), capacite)), montant) AS regle,
    (montant - LEAST((impute + LEAST(GREATEST((libre - ((ouverture + cumul) - capacite)), (0)::numeric), capacite)), montant)) AS reste
   FROM ( SELECT a.vente_id,
            a.patient_id,
            a.numero,
            a.date_vente,
            a.montant,
            LEAST(COALESCE(i.impute, (0)::numeric), a.montant) AS impute,
            (a.montant - LEAST(COALESCE(i.impute, (0)::numeric), a.montant)) AS capacite,
            COALESCE(o.montant, (0)::numeric) AS ouverture,
            sum((a.montant - LEAST(COALESCE(i.impute, (0)::numeric), a.montant))) OVER (PARTITION BY a.patient_id ORDER BY a.date_vente, a.vente_id) AS cumul,
            GREATEST((((COALESCE(o.montant, (0)::numeric) + sum(a.montant) OVER (PARTITION BY a.patient_id)) - p.solde_credit) - sum(LEAST(COALESCE(i.impute, (0)::numeric), a.montant)) OVER (PARTITION BY a.patient_id)), (0)::numeric) AS libre
           FROM (((public.v_credit_accorde a
             JOIN public.patient p ON ((p.id = a.patient_id)))
             LEFT JOIN public.credit_ouverture o ON ((o.patient_id = a.patient_id)))
             LEFT JOIN ( SELECT ci.vente_id,
                    sum(ci.montant) AS impute
                   FROM public.credit_imputation ci
                  GROUP BY ci.vente_id) i ON ((i.vente_id = a.vente_id)))
          WHERE (a.montant > (0)::numeric)) t;


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
            ELSE GREATEST(((((f.montant_ttc - f.deja_paye) - COALESCE(( SELECT sum((a.montant_ttc - a.part_credit)) AS sum
               FROM public.facture_client a
              WHERE ((a.facture_origine_id = f.id) AND (a.type = 'avoir'::text))), (0)::numeric)) - COALESCE(( SELECT sum((r.montant - r.part_credit)) AS sum
               FROM public.reglement_facture_client r
              WHERE (r.facture_id = f.id)), (0)::numeric)) - COALESCE(( SELECT sum(c.regle) AS sum
               FROM public.v_vente_credit c
              WHERE (c.vente_id IN ( SELECT fl.vente_id
                       FROM public.facture_client_ligne fl
                      WHERE ((fl.facture_id = f.id) AND (fl.part = 'patient'::text) AND (fl.vente_id IS NOT NULL))))), (0)::numeric)), (0)::numeric)
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
           FROM (public.reglement_facture x
             JOIN public.reglement_fournisseur g ON ((g.id = x.reglement_id)))
          WHERE ((x.reception_id = r.id) AND (g.statut = 'valide'::text))), (0)::numeric) AS regle,
    (COALESCE(r.montant_ttc, (0)::numeric) - COALESCE(( SELECT sum(x.montant) AS sum
           FROM (public.reglement_facture x
             JOIN public.reglement_fournisseur g ON ((g.id = x.reglement_id)))
          WHERE ((x.reception_id = r.id) AND (g.statut = 'valide'::text))), (0)::numeric)) AS reste
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
    COALESCE(p.nom_commercial, l.libelle) AS designation,
    COALESCE(p.taux_tva, l.taux_tva) AS taux_tva,
    l.prix_unitaire,
    l.quantite,
    (l.quantite - COALESCE(rt.quantite, (0)::bigint)) AS quantite_nette,
    round(((l.montant_patient * ((l.quantite - COALESCE(rt.quantite, (0)::bigint)))::numeric) / (l.quantite)::numeric), 2) AS montant_patient,
        CASE
            WHEN (v.canal_organisme = 'logiciel'::text) THEN round(((l.montant_organisme * ((l.quantite - COALESCE(rt.quantite, (0)::bigint)))::numeric) / (l.quantite)::numeric), 2)
            ELSE (0)::numeric
        END AS montant_organisme,
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
     LEFT JOIN public.produit p ON ((p.id = l.produit_id)))
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
    v.chifa_facture,
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
-- Name: v_pret_bancaire_complet; Type: VIEW; Schema: public; Owner: -
--

CREATE VIEW public.v_pret_bancaire_complet AS
 SELECT v.id,
    v.compte_id,
    v.banque,
    v.reference,
    v.objet,
    v.montant,
    v.date_pret,
    v.document,
    v.capital_rembourse,
    v.reste_du,
    v.utilise,
    v.echeances,
    v.echeances_payees,
    v.echues_non_payees,
    v.prochaine_echeance,
    v.prochain_montant,
    v.employe,
    v.cree_le,
    p.taux,
    p.duree_mois,
    ( SELECT e.assurance
           FROM public.echeance_pret e
          WHERE ((e.pret_id = v.id) AND (NOT (EXISTS ( SELECT 1
                   FROM public.paiement_echeance_pret x
                  WHERE (x.echeance_id = e.id)))))
          ORDER BY e.rang
         LIMIT 1) AS prochaine_assurance
   FROM (public.v_pret_bancaire v
     JOIN public.pret_bancaire p ON ((p.id = v.id)));


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
    cp.classe AS classe_proposee,
    n.situation,
        CASE
            WHEN ((p.classe_confirmee_par IS NULL) AND (((cp.classe = ANY (ARRAY['psychotrope'::text, 'stupefiant'::text])) AND (p.classe <> ALL (ARRAY['psychotrope'::text, 'stupefiant'::text]))) OR ((cp.classe = 'stupefiant'::text) AND (p.classe = 'psychotrope'::text)))) THEN 'classe'::text
            WHEN p.a_verifier THEN 'repris'::text
            WHEN (n.situation IS NULL) THEN 'absent'::text
            ELSE 'situation'::text
        END AS motif
   FROM ((((public.produit p
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
     CROSS JOIN LATERAL ( SELECT
                CASE
                    WHEN ((sd.classe = 'stupefiant'::text) OR (n.classe_proposee = 'stupefiant'::text)) THEN 'stupefiant'::text
                    ELSE COALESCE(sd.classe, n.classe_proposee)
                END AS classe) cp)
  WHERE (p.actif AND (((p.classe_confirmee_par IS NULL) AND (((cp.classe = ANY (ARRAY['psychotrope'::text, 'stupefiant'::text])) AND (p.classe <> ALL (ARRAY['psychotrope'::text, 'stupefiant'::text]))) OR ((cp.classe = 'stupefiant'::text) AND (p.classe = 'psychotrope'::text)))) OR p.a_verifier OR (n.situation = ANY (ARRAY['retire'::text, 'non_renouvele'::text])) OR ((n.situation IS NULL) AND (NULLIF(TRIM(BOTH FROM p.num_enregistrement), ''::text) IS NOT NULL) AND (EXISTS ( SELECT 1
           FROM public.nomenclature_produit)))));


--
-- Name: v_produit_fabrication_locale; Type: VIEW; Schema: public; Owner: -
--

CREATE VIEW public.v_produit_fabrication_locale AS
 SELECT p.id AS produit_id,
    COALESCE(p.fabrication_locale, n.locale) AS fabrication_locale,
        CASE
            WHEN (p.fabrication_locale IS NOT NULL) THEN 'saisie'::text
            WHEN (n.locale IS NOT NULL) THEN 'nomenclature'::text
            ELSE NULL::text
        END AS source
   FROM (public.produit p
     LEFT JOIN LATERAL ( SELECT (upper(btrim(np.pays)) = ANY (ARRAY['ALGERIE'::text, 'ALGÉRIE'::text])) AS locale
           FROM public.nomenclature_produit np
          WHERE ((p.num_enregistrement IS NOT NULL) AND (np.num_enregistrement = p.num_enregistrement) AND (np.pays IS NOT NULL) AND (btrim(np.pays) <> ''::text))
          ORDER BY np.version_id DESC
         LIMIT 1) n ON (true));


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
         SELECT parametre_alerte.jours_orange,
            parametre_alerte.jours_rouge,
            parametre_alerte.horizon_risque_jours
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
    ((((((solde_initial + COALESCE(( SELECT sum(
                CASE
                    WHEN (m.sens = 'entree'::text) THEN m.montant
                    ELSE (- m.montant)
                END) AS sum
           FROM public.mouvement_tresorerie m
          WHERE ((m.compte_id = c.id) AND (m.date_mouvement >= c.date_initiale))), (0)::numeric)) + COALESCE(( SELECT sum(p.montant) AS sum
           FROM (public.paiement p
             JOIN public.compte_carte_periode h ON (((h.compte_id = c.id) AND ((p.cree_le)::date >= h.du) AND ((h.au IS NULL) OR ((p.cree_le)::date <= h.au)))))
          WHERE ((p.mode = 'carte'::text) AND (p.cree_le >= c.date_initiale) AND ((p.vente_id IS NULL) OR (EXISTS ( SELECT 1
                   FROM public.vente v
                  WHERE ((v.id = p.vente_id) AND (v.statut = ANY (ARRAY['validee'::text, 'annulee'::text])))))))), (0)::numeric)) + COALESCE(( SELECT sum(p.montant) AS sum
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
                END >= c.date_initiale) AND ((g.moyen <> 'effet'::text) OR (g.echeance <= CURRENT_DATE)))), (0)::numeric)) - COALESCE(( SELECT sum(g.montant) AS sum
           FROM (public.paiement_effet pe
             JOIN public.reglement_fournisseur g ON ((g.id = pe.reglement_id)))
          WHERE ((g.compte_id IS NULL) AND (((pe.moyen = 'especes'::text) AND (c.type = 'coffre'::text)) OR ((pe.moyen <> 'especes'::text) AND (pe.compte_id = c.id))) AND (pe.date_paiement >= c.date_initiale))), (0)::numeric)) - COALESCE(( SELECT sum(x.montant_rembourse) AS sum
           FROM (( SELECT a.cree_le,
                    a.montant_rembourse
                   FROM public.annulation_vente a
                  WHERE ((a.mode = 'carte'::text) AND (a.montant_rembourse > (0)::numeric))
                UNION ALL
                 SELECT rc.cree_le,
                    rc.montant_rembourse
                   FROM public.retour_client rc
                  WHERE ((rc.mode = 'carte'::text) AND (rc.montant_rembourse > (0)::numeric))) x
             JOIN public.compte_carte_periode h ON (((h.compte_id = c.id) AND ((x.cree_le)::date >= h.du) AND ((h.au IS NULL) OR ((x.cree_le)::date <= h.au)))))
          WHERE (x.cree_le >= c.date_initiale)), (0)::numeric)) AS solde
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
-- Name: v_vente_chifa_a_deposer; Type: VIEW; Schema: public; Owner: -
--

CREATE VIEW public.v_vente_chifa_a_deposer AS
 SELECT v.id AS vente_id,
    v.numero,
    v.date_vente,
    v.organisme_id,
    v.canal_organisme AS canal,
    v.centre_payeur_id,
    v.patient_id,
    v.chifa_facture,
    po.part_organisme_retenue AS part_organisme,
    (EXISTS ( SELECT 1
           FROM public.bordereau_vente bv
          WHERE ((bv.vente_id = v.id) AND (bv.suite = 'redeposer'::text)))) AS redeposee
   FROM (public.vente v
     JOIN public.v_vente_part_organisme po ON ((po.vente_id = v.id)))
  WHERE ((v.canal_organisme = ANY (ARRAY['chifa'::text, 'mdn'::text])) AND (v.statut = 'validee'::text) AND (v.bordereau_id IS NULL) AND (v.chifa_exclue_le IS NULL) AND ((v.canal_organisme = 'mdn'::text) OR (v.chifa_facture IS NOT NULL)) AND (po.part_organisme_retenue > (0)::numeric) AND (NOT (EXISTS ( SELECT 1
           FROM public.bordereau_vente bv
          WHERE ((bv.vente_id = v.id) AND (bv.suite IS DISTINCT FROM 'redeposer'::text))))));


--
-- Name: v_suivi_dettes; Type: VIEW; Schema: public; Owner: -
--

CREATE VIEW public.v_suivi_dettes AS
 SELECT 'creance'::text AS sens,
    'patient'::text AS categorie,
    c.patient_id AS tiers_id,
    TRIM(BOTH FROM concat_ws(' '::text, p.nom, p.prenom)) AS tiers,
    ('Vente à crédit '::text || c.numero) AS libelle,
    (c.date_vente)::date AS date_reference,
    NULL::date AS echeance,
    c.reste AS montant,
    false AS rejetee
   FROM (public.v_vente_credit c
     JOIN public.patient p ON ((p.id = c.patient_id)))
  WHERE ((c.reste > (0)::numeric) AND (NOT (EXISTS ( SELECT 1
           FROM (public.facture_client_ligne fl
             JOIN public.v_facture_client vf ON ((vf.id = fl.facture_id)))
          WHERE ((fl.vente_id = c.vente_id) AND (fl.part = 'patient'::text) AND (vf.type = 'facture'::text) AND (vf.statut <> 'remplacee'::text) AND (NOT vf.hors_suivi) AND (vf.reste > (0)::numeric))))))
UNION ALL
 SELECT 'creance'::text AS sens,
    'patient'::text AS categorie,
    p.id AS tiers_id,
    TRIM(BOTH FROM concat_ws(' '::text, p.nom, p.prenom)) AS tiers,
    'Dette antérieure (ancien logiciel ou mise en place du relevé)'::text AS libelle,
    (COALESCE(( SELECT min(m.cree_le) AS min
           FROM public.credit_mouvement m
          WHERE (m.patient_id = p.id)), now()))::date AS date_reference,
    NULL::date AS echeance,
    (p.solde_credit - COALESCE(( SELECT sum(c.reste) AS sum
           FROM public.v_vente_credit c
          WHERE (c.patient_id = p.id)), (0)::numeric)) AS montant,
    false AS rejetee
   FROM public.patient p
  WHERE ((p.solde_credit - COALESCE(( SELECT sum(c.reste) AS sum
           FROM public.v_vente_credit c
          WHERE (c.patient_id = p.id)), (0)::numeric)) > (0)::numeric)
UNION ALL
 SELECT 'creance'::text AS sens,
        CASE f.destinataire
            WHEN 'organisme'::text THEN 'facture_organisme'::text
            WHEN 'entreprise'::text THEN 'facture_entreprise'::text
            ELSE 'facture_patient'::text
        END AS categorie,
    COALESCE(f.organisme_id, f.client_id, f.patient_id) AS tiers_id,
    f.destinataire_nom AS tiers,
    ('Facture '::text || f.numero) AS libelle,
    f.date_facture AS date_reference,
    fc.date_echeance AS echeance,
    f.reste AS montant,
    (f.statut = 'rejetee'::text) AS rejetee
   FROM (public.v_facture_client f
     JOIN public.facture_client fc ON ((fc.id = f.id)))
  WHERE ((f.type = 'facture'::text) AND (f.statut <> 'remplacee'::text) AND (NOT f.hors_suivi) AND (f.reste > (0)::numeric))
UNION ALL
 SELECT 'creance'::text AS sens,
    'bordereau'::text AS categorie,
    b.organisme_id AS tiers_id,
    o.nom AS tiers,
    ('Bordereau '::text || b.numero) AS libelle,
    COALESCE(b.date_depot, b.periode_fin) AS date_reference,
    (COALESCE(b.date_depot, b.periode_fin) + COALESCE(( SELECT pc.chifa_delai_paiement_jours
           FROM public.parametre_caisse pc
          WHERE (pc.id = 1)), 45)) AS echeance,
    b.reste AS montant,
    (b.rejets_a_traiter > 0) AS rejetee
   FROM (public.v_bordereau b
     JOIN public.organisme o ON ((o.id = b.organisme_id)))
  WHERE ((b.statut = ANY (ARRAY['depose'::text, 'paye_partiel'::text])) AND (b.reste > (0)::numeric))
UNION ALL
 SELECT 'creance'::text AS sens,
    'bordereau'::text AS categorie,
    b.organisme_id AS tiers_id,
    o.nom AS tiers,
    ((('Rejet à traiter · vente '::text || v.numero) || ' · bordereau '::text) || b.numero) AS libelle,
    (bv.rejete_le)::date AS date_reference,
    NULL::date AS echeance,
    bv.montant_rejete AS montant,
    true AS rejetee
   FROM (((public.bordereau_vente bv
     JOIN public.bordereau b ON ((b.id = bv.bordereau_id)))
     JOIN public.organisme o ON ((o.id = b.organisme_id)))
     JOIN public.vente v ON ((v.id = bv.vente_id)))
  WHERE ((bv.rejete_le IS NOT NULL) AND (bv.suite IS NULL))
UNION ALL
 SELECT 'creance'::text AS sens,
    'chifa_a_deposer'::text AS categorie,
    a.organisme_id AS tiers_id,
    o.nom AS tiers,
    ((('Vente '::text || a.numero) || ' à déposer'::text) || COALESCE((' · facture Chifa '::text || a.chifa_facture), ''::text)) AS libelle,
    (a.date_vente)::date AS date_reference,
    ((a.date_vente)::date + COALESCE(( SELECT pc.chifa_delai_depot_jours
           FROM public.parametre_caisse pc
          WHERE (pc.id = 1)), 7)) AS echeance,
    a.part_organisme AS montant,
    false AS rejetee
   FROM (public.v_vente_chifa_a_deposer a
     JOIN public.organisme o ON ((o.id = a.organisme_id)))
UNION ALL
 SELECT 'dette'::text AS sens,
    'grossiste'::text AS categorie,
    ff.fournisseur_id AS tiers_id,
    ff.fournisseur AS tiers,
    COALESCE(('Facture '::text || ff.numero_facture), ('BL '::text || ff.numero_bl), 'Réception'::text) AS libelle,
    (ff.date_reception)::date AS date_reference,
    NULL::date AS echeance,
    ff.reste AS montant,
    false AS rejetee
   FROM public.v_facture_fournisseur ff
  WHERE (ff.reste > (0)::numeric)
UNION ALL
 SELECT 'dette'::text AS sens,
    'grossiste'::text AS categorie,
    f.id AS tiers_id,
    f.raison_sociale AS tiers,
    'Ancien solde (hors factures du logiciel)'::text AS libelle,
    (COALESCE(( SELECT min(m.cree_le) AS min
           FROM public.fournisseur_mouvement m
          WHERE (m.fournisseur_id = f.id)), now()))::date AS date_reference,
    NULL::date AS echeance,
    (f.solde - COALESCE(( SELECT sum(ff.reste) AS sum
           FROM public.v_facture_fournisseur ff
          WHERE ((ff.fournisseur_id = f.id) AND (ff.reste > (0)::numeric))), (0)::numeric)) AS montant,
    false AS rejetee
   FROM public.fournisseur f
  WHERE ((f.solde - COALESCE(( SELECT sum(ff.reste) AS sum
           FROM public.v_facture_fournisseur ff
          WHERE ((ff.fournisseur_id = f.id) AND (ff.reste > (0)::numeric))), (0)::numeric)) > (0)::numeric)
UNION ALL
 SELECT 'dette'::text AS sens,
    'effet'::text AS categorie,
    g.fournisseur_id AS tiers_id,
    f.raison_sociale AS tiers,
    ((('Traite '::text || COALESCE(g.reference, ''::text)) || COALESCE(((' ('::text || g.banque) || ')'::text), ''::text)) ||
        CASE
            WHEN (g.echeance < CURRENT_DATE) THEN ' · échue, non payée'::text
            ELSE ''::text
        END) AS libelle,
    g.date_reglement AS date_reference,
    g.echeance,
    g.montant,
    false AS rejetee
   FROM (public.reglement_fournisseur g
     JOIN public.fournisseur f ON ((f.id = g.fournisseur_id)))
  WHERE ((g.moyen = 'effet'::text) AND (NOT (EXISTS ( SELECT 1
           FROM public.paiement_effet pe
          WHERE (pe.reglement_id = g.id)))) AND ((g.echeance >= CURRENT_DATE) OR (g.compte_id IS NULL)));


--
-- Name: vente_chifa_facture_conflit; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public.vente_chifa_facture_conflit (
    vente_id bigint NOT NULL,
    num_facture_chifa text NOT NULL,
    chifa_facture text,
    constate_le timestamp with time zone DEFAULT now() NOT NULL,
    tranche_le timestamp with time zone,
    tranche_par bigint,
    valeur_retenue text,
    officine_id uuid DEFAULT public.officine_courante() NOT NULL,
    CONSTRAINT ck_vente_chifa_facture_conflit_tranche CHECK ((((tranche_le IS NULL) AND (valeur_retenue IS NULL)) OR ((tranche_le IS NOT NULL) AND (valeur_retenue IS NOT NULL))))
);


--
-- Name: TABLE vente_chifa_facture_conflit; Type: COMMENT; Schema: public; Owner: -
--

COMMENT ON TABLE public.vente_chifa_facture_conflit IS 'Fusion du n° de facture Chifa (script 101) : ventes dont num_facture_chifa et chifa_facture diffèrent, tranchées par le titulaire';


--
-- Name: COLUMN vente_chifa_facture_conflit.num_facture_chifa; Type: COMMENT; Schema: public; Owner: -
--

COMMENT ON COLUMN public.vente_chifa_facture_conflit.num_facture_chifa IS 'Valeur de l''ancienne colonne vente.num_facture_chifa au moment du constat';


--
-- Name: COLUMN vente_chifa_facture_conflit.chifa_facture; Type: COMMENT; Schema: public; Owner: -
--

COMMENT ON COLUMN public.vente_chifa_facture_conflit.chifa_facture IS 'Valeur de vente.chifa_facture au moment du constat (null : recopie impossible, n° déjà porté par une autre vente)';


--
-- Name: COLUMN vente_chifa_facture_conflit.valeur_retenue; Type: COMMENT; Schema: public; Owner: -
--

COMMENT ON COLUMN public.vente_chifa_facture_conflit.valeur_retenue IS 'N° retenu par le titulaire (8 chiffres), écrit dans vente.chifa_facture';


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
-- Name: verification_integrite; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public.verification_integrite (
    id bigint NOT NULL,
    verifie_le timestamp with time zone DEFAULT now() NOT NULL,
    journal text NOT NULL,
    cle_chaine text,
    lignes bigint DEFAULT 0 NOT NULL,
    sceaux integer DEFAULT 0 NOT NULL,
    statut text NOT NULL,
    rang_rupture bigint,
    motif text,
    attendu text,
    trouve text,
    declenchee_par bigint,
    officine_id uuid DEFAULT public.officine_courante() NOT NULL,
    dernier_rang bigint,
    dernier_hash bytea,
    CONSTRAINT ck_verification_integrite_dernier_rang CHECK (((dernier_rang IS NULL) OR (dernier_rang >= 0))),
    CONSTRAINT ck_verification_integrite_statut CHECK ((statut = ANY (ARRAY['ok'::text, 'rupture'::text, 'erreur'::text])))
);


--
-- Name: COLUMN verification_integrite.dernier_rang; Type: COMMENT; Schema: public; Owner: -
--

COMMENT ON COLUMN public.verification_integrite.dernier_rang IS 'Dernier rang de la chaîne constaté par la vérification (NULL : chaîne rompue ou contrôle sans chaîne) ; envoyé au serveur central (script 144).';


--
-- Name: COLUMN verification_integrite.dernier_hash; Type: COMMENT; Schema: public; Owner: -
--

COMMENT ON COLUMN public.verification_integrite.dernier_hash IS 'Empreinte de la ligne au dernier rang constaté ; le serveur central la compare à la ligne reçue (script 144).';


--
-- Name: verification_integrite_id_seq; Type: SEQUENCE; Schema: public; Owner: -
--

ALTER TABLE public.verification_integrite ALTER COLUMN id ADD GENERATED ALWAYS AS IDENTITY (
    SEQUENCE NAME public.verification_integrite_id_seq
    START WITH 1
    INCREMENT BY 1
    NO MINVALUE
    NO MAXVALUE
    CACHE 1
);


--
-- Name: verification_serialisation; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public.verification_serialisation (
    id bigint NOT NULL,
    boite_id bigint NOT NULL,
    operation text NOT NULL,
    statut text DEFAULT 'en_attente'::text NOT NULL,
    resultat text,
    message text,
    essais integer DEFAULT 0 NOT NULL,
    alerte_titulaire boolean DEFAULT false NOT NULL,
    cree_le timestamp with time zone DEFAULT now() NOT NULL,
    prochain_essai_le timestamp with time zone DEFAULT now() NOT NULL,
    dernier_essai_le timestamp with time zone,
    traite_le timestamp with time zone,
    officine_id uuid DEFAULT public.officine_courante() NOT NULL,
    CONSTRAINT verification_serialisation_essais_check CHECK ((essais >= 0)),
    CONSTRAINT verification_serialisation_operation_check CHECK ((operation = ANY (ARRAY['verifier'::text, 'decommissionner'::text, 'reactiver'::text]))),
    CONSTRAINT verification_serialisation_resultat_check CHECK ((resultat = ANY (ARRAY['authentique'::text, 'inconnue'::text, 'deja_decommissionnee'::text, 'rappelee'::text, 'expiree'::text]))),
    CONSTRAINT verification_serialisation_statut_check CHECK ((statut = ANY (ARRAY['en_attente'::text, 'reussie'::text, 'refusee'::text])))
);


--
-- Name: verification_serialisation_id_seq; Type: SEQUENCE; Schema: public; Owner: -
--

ALTER TABLE public.verification_serialisation ALTER COLUMN id ADD GENERATED ALWAYS AS IDENTITY (
    SEQUENCE NAME public.verification_serialisation_id_seq
    START WITH 1
    INCREMENT BY 1
    NO MINVALUE
    NO MAXVALUE
    CACHE 1
);


--
-- Name: versement_bordereau_id_seq; Type: SEQUENCE; Schema: public; Owner: -
--

ALTER TABLE public.versement_bordereau ALTER COLUMN id ADD GENERATED ALWAYS AS IDENTITY (
    SEQUENCE NAME public.versement_bordereau_id_seq
    START WITH 1
    INCREMENT BY 1
    NO MINVALUE
    NO MAXVALUE
    CACHE 1
);


--
-- Name: wilaya; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public.wilaya (
    code text NOT NULL,
    nom text NOT NULL,
    wilaya_origine text,
    officine_id uuid DEFAULT public.officine_courante() NOT NULL,
    CONSTRAINT ck_wilaya_code CHECK ((code ~ '^[0-9]{2}$'::text))
);


--
-- Name: zone_psychotrope_wilaya; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public.zone_psychotrope_wilaya (
    wilaya text NOT NULL,
    officine boolean DEFAULT false NOT NULL,
    modifie_le timestamp with time zone DEFAULT now() NOT NULL,
    modifie_par bigint,
    officine_id uuid DEFAULT public.officine_courante() NOT NULL
);


--
-- Name: reception_ligne id; Type: DEFAULT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.reception_ligne ALTER COLUMN id SET DEFAULT nextval('public.reception_ligne_id_seq'::regclass);


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
-- Name: doublon_patient doublon_patient_pkey; Type: CONSTRAINT; Schema: import; Owner: -
--

ALTER TABLE ONLY import.doublon_patient
    ADD CONSTRAINT doublon_patient_pkey PRIMARY KEY (id);


--
-- Name: fournisseur_src fournisseur_src_pkey; Type: CONSTRAINT; Schema: import; Owner: -
--

ALTER TABLE ONLY import.fournisseur_src
    ADD CONSTRAINT fournisseur_src_pkey PRIMARY KEY (session_id, ref_source);


--
-- Name: historique_chifa_src historique_chifa_src_pkey; Type: CONSTRAINT; Schema: import; Owner: -
--

ALTER TABLE ONLY import.historique_chifa_src
    ADD CONSTRAINT historique_chifa_src_pkey PRIMARY KEY (session_id, num_facture, date_facture);


--
-- Name: historique_mdn_src historique_mdn_src_pkey; Type: CONSTRAINT; Schema: import; Owner: -
--

ALTER TABLE ONLY import.historique_mdn_src
    ADD CONSTRAINT historique_mdn_src_pkey PRIMARY KEY (session_id, num_facture, date_facture);


--
-- Name: produit_src produit_src_pkey; Type: CONSTRAINT; Schema: import; Owner: -
--

ALTER TABLE ONLY import.produit_src
    ADD CONSTRAINT produit_src_pkey PRIMARY KEY (session_id, ref_source);


--
-- Name: profil_reprise profil_reprise_pkey; Type: CONSTRAINT; Schema: import; Owner: -
--

ALTER TABLE ONLY import.profil_reprise
    ADD CONSTRAINT profil_reprise_pkey PRIMARY KEY (id);


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
-- Name: doublon_patient ux_doublon_patient_source; Type: CONSTRAINT; Schema: import; Owner: -
--

ALTER TABLE ONLY import.doublon_patient
    ADD CONSTRAINT ux_doublon_patient_source UNIQUE (session_id, ref_source);


--
-- Name: alerte_non_envoyee alerte_non_envoyee_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.alerte_non_envoyee
    ADD CONSTRAINT alerte_non_envoyee_pkey PRIMARY KEY (id);


--
-- Name: alerte alerte_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.alerte
    ADD CONSTRAINT alerte_pkey PRIMARY KEY (id);


--
-- Name: alerte_surveillance alerte_surveillance_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.alerte_surveillance
    ADD CONSTRAINT alerte_surveillance_pkey PRIMARY KEY (id);


--
-- Name: annulation_registre annulation_registre_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.annulation_registre
    ADD CONSTRAINT annulation_registre_pkey PRIMARY KEY (id);


--
-- Name: annulation_registre annulation_registre_registre_id_key; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.annulation_registre
    ADD CONSTRAINT annulation_registre_registre_id_key UNIQUE (registre_id);


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
-- Name: avoir_mouvement avoir_mouvement_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.avoir_mouvement
    ADD CONSTRAINT avoir_mouvement_pkey PRIMARY KEY (id);


--
-- Name: bascule_decision bascule_decision_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.bascule_decision
    ADD CONSTRAINT bascule_decision_pkey PRIMARY KEY (id);


--
-- Name: bascule_promotion bascule_promotion_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.bascule_promotion
    ADD CONSTRAINT bascule_promotion_pkey PRIMARY KEY (id);


--
-- Name: bascule_saut bascule_saut_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.bascule_saut
    ADD CONSTRAINT bascule_saut_pkey PRIMARY KEY (id);


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
-- Name: bordereau bordereau_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.bordereau
    ADD CONSTRAINT bordereau_pkey PRIMARY KEY (id);


--
-- Name: bordereau_vente bordereau_vente_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.bordereau_vente
    ADD CONSTRAINT bordereau_vente_pkey PRIMARY KEY (id);


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
-- Name: catalogue_grossiste catalogue_grossiste_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.catalogue_grossiste
    ADD CONSTRAINT catalogue_grossiste_pkey PRIMARY KEY (fournisseur_id, produit_id);


--
-- Name: categorie_produit categorie_produit_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.categorie_produit
    ADD CONSTRAINT categorie_produit_pkey PRIMARY KEY (code);


--
-- Name: centre_payeur centre_payeur_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.centre_payeur
    ADD CONSTRAINT centre_payeur_pkey PRIMARY KEY (id);


--
-- Name: chaine_integrite chaine_integrite_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.chaine_integrite
    ADD CONSTRAINT chaine_integrite_pkey PRIMARY KEY (journal);


--
-- Name: chifa_code_produit chifa_code_produit_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.chifa_code_produit
    ADD CONSTRAINT chifa_code_produit_pkey PRIMARY KEY (code);


--
-- Name: classe_suivie_dci classe_suivie_dci_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.classe_suivie_dci
    ADD CONSTRAINT classe_suivie_dci_pkey PRIMARY KEY (classe_code, dci_id);


--
-- Name: classe_suivie classe_suivie_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.classe_suivie
    ADD CONSTRAINT classe_suivie_pkey PRIMARY KEY (code);


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
-- Name: cloture_fiscale cloture_fiscale_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.cloture_fiscale
    ADD CONSTRAINT cloture_fiscale_pkey PRIMARY KEY (id);


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
-- Name: commande_ordre_central commande_ordre_central_ordre_id_key; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.commande_ordre_central
    ADD CONSTRAINT commande_ordre_central_ordre_id_key UNIQUE (ordre_id);


--
-- Name: commande_ordre_central commande_ordre_central_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.commande_ordre_central
    ADD CONSTRAINT commande_ordre_central_pkey PRIMARY KEY (commande_id);


--
-- Name: comptage_caisse comptage_caisse_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.comptage_caisse
    ADD CONSTRAINT comptage_caisse_pkey PRIMARY KEY (session_id, coupure);


--
-- Name: comptage_intermediaire comptage_intermediaire_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.comptage_intermediaire
    ADD CONSTRAINT comptage_intermediaire_pkey PRIMARY KEY (id);


--
-- Name: comptage_reception_ligne comptage_reception_ligne_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.comptage_reception_ligne
    ADD CONSTRAINT comptage_reception_ligne_pkey PRIMARY KEY (id);


--
-- Name: comptage_reception comptage_reception_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.comptage_reception
    ADD CONSTRAINT comptage_reception_pkey PRIMARY KEY (id);


--
-- Name: compte_carte_periode compte_carte_periode_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.compte_carte_periode
    ADD CONSTRAINT compte_carte_periode_pkey PRIMARY KEY (id);


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
-- Name: compteur_ticket compteur_ticket_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.compteur_ticket
    ADD CONSTRAINT compteur_ticket_pkey PRIMARY KEY (id);


--
-- Name: compteur_ticket_saut compteur_ticket_saut_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.compteur_ticket_saut
    ADD CONSTRAINT compteur_ticket_saut_pkey PRIMARY KEY (id);


--
-- Name: connexion_employe connexion_employe_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.connexion_employe
    ADD CONSTRAINT connexion_employe_pkey PRIMARY KEY (id);


--
-- Name: correction_lot correction_lot_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.correction_lot
    ADD CONSTRAINT correction_lot_pkey PRIMARY KEY (id);


--
-- Name: correspondance_echeancier correspondance_echeancier_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.correspondance_echeancier
    ADD CONSTRAINT correspondance_echeancier_pkey PRIMARY KEY (compte_id);


--
-- Name: correspondance_echeancier_preteur correspondance_echeancier_preteur_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.correspondance_echeancier_preteur
    ADD CONSTRAINT correspondance_echeancier_preteur_pkey PRIMARY KEY (preteur);


--
-- Name: credit_imputation credit_imputation_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.credit_imputation
    ADD CONSTRAINT credit_imputation_pkey PRIMARY KEY (id);


--
-- Name: credit_mouvement credit_mouvement_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.credit_mouvement
    ADD CONSTRAINT credit_mouvement_pkey PRIMARY KEY (id);


--
-- Name: credit_ouverture credit_ouverture_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.credit_ouverture
    ADD CONSTRAINT credit_ouverture_pkey PRIMARY KEY (patient_id);


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
-- Name: demande_patient demande_patient_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.demande_patient
    ADD CONSTRAINT demande_patient_pkey PRIMARY KEY (id);


--
-- Name: destinataire_notification destinataire_notification_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.destinataire_notification
    ADD CONSTRAINT destinataire_notification_pkey PRIMARY KEY (id);


--
-- Name: ecart_caisse_vu ecart_caisse_vu_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.ecart_caisse_vu
    ADD CONSTRAINT ecart_caisse_vu_pkey PRIMARY KEY (session_id);


--
-- Name: echeance_pret echeance_pret_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.echeance_pret
    ADD CONSTRAINT echeance_pret_pkey PRIMARY KEY (id);


--
-- Name: echeance_pret echeance_pret_pret_id_rang_key; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.echeance_pret
    ADD CONSTRAINT echeance_pret_pret_id_rang_key UNIQUE (pret_id, rang);


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
-- Name: evenement_caisse evenement_caisse_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.evenement_caisse
    ADD CONSTRAINT evenement_caisse_pkey PRIMARY KEY (id);


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
-- Name: forcage_alerte_psychotrope forcage_alerte_psychotrope_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.forcage_alerte_psychotrope
    ADD CONSTRAINT forcage_alerte_psychotrope_pkey PRIMARY KEY (id);


--
-- Name: fournisseur_mouvement fournisseur_mouvement_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.fournisseur_mouvement
    ADD CONSTRAINT fournisseur_mouvement_pkey PRIMARY KEY (id);


--
-- Name: fournisseur fournisseur_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.fournisseur
    ADD CONSTRAINT fournisseur_pkey PRIMARY KEY (id);


--
-- Name: historique_chifa historique_chifa_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.historique_chifa
    ADD CONSTRAINT historique_chifa_pkey PRIMARY KEY (id);


--
-- Name: import_catalogue_grossiste import_catalogue_grossiste_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.import_catalogue_grossiste
    ADD CONSTRAINT import_catalogue_grossiste_pkey PRIMARY KEY (id);


--
-- Name: indication indication_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.indication
    ADD CONSTRAINT indication_pkey PRIMARY KEY (id);


--
-- Name: integrite_essai integrite_essai_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.integrite_essai
    ADD CONSTRAINT integrite_essai_pkey PRIMARY KEY (id);


--
-- Name: integrite_prise_acte integrite_prise_acte_nouvelle_epoque_key; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.integrite_prise_acte
    ADD CONSTRAINT integrite_prise_acte_nouvelle_epoque_key UNIQUE (nouvelle_epoque);


--
-- Name: integrite_prise_acte integrite_prise_acte_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.integrite_prise_acte
    ADD CONSTRAINT integrite_prise_acte_pkey PRIMARY KEY (id);


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
-- Name: journal_registre_psychotrope journal_registre_psychotrope_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.journal_registre_psychotrope
    ADD CONSTRAINT journal_registre_psychotrope_pkey PRIMARY KEY (id);


--
-- Name: journal_sauvegarde journal_sauvegarde_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.journal_sauvegarde
    ADD CONSTRAINT journal_sauvegarde_pkey PRIMARY KEY (id);


--
-- Name: journal_vente journal_vente_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.journal_vente
    ADD CONSTRAINT journal_vente_pkey PRIMARY KEY (id);


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
-- Name: motif_reglable motif_reglable_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.motif_reglable
    ADD CONSTRAINT motif_reglable_pkey PRIMARY KEY (id);


--
-- Name: mouvement_stock mouvement_stock_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.mouvement_stock
    ADD CONSTRAINT mouvement_stock_pkey PRIMARY KEY (id);


--
-- Name: mouvement_tiroir mouvement_tiroir_corrige_id_key; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.mouvement_tiroir
    ADD CONSTRAINT mouvement_tiroir_corrige_id_key UNIQUE (corrige_id);


--
-- Name: mouvement_tiroir mouvement_tiroir_numero_key; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.mouvement_tiroir
    ADD CONSTRAINT mouvement_tiroir_numero_key UNIQUE (numero);


--
-- Name: mouvement_tiroir mouvement_tiroir_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.mouvement_tiroir
    ADD CONSTRAINT mouvement_tiroir_pkey PRIMARY KEY (id);


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
-- Name: paiement_echeance_pret paiement_echeance_pret_echeance_id_key; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.paiement_echeance_pret
    ADD CONSTRAINT paiement_echeance_pret_echeance_id_key UNIQUE (echeance_id);


--
-- Name: paiement_echeance_pret paiement_echeance_pret_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.paiement_echeance_pret
    ADD CONSTRAINT paiement_echeance_pret_pkey PRIMARY KEY (id);


--
-- Name: paiement_effet paiement_effet_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.paiement_effet
    ADD CONSTRAINT paiement_effet_pkey PRIMARY KEY (id);


--
-- Name: paiement_effet paiement_effet_reglement_id_key; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.paiement_effet
    ADD CONSTRAINT paiement_effet_reglement_id_key UNIQUE (reglement_id);


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
-- Name: parametre_fiscal parametre_fiscal_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.parametre_fiscal
    ADD CONSTRAINT parametre_fiscal_pkey PRIMARY KEY (id);


--
-- Name: parametre_formulaire_sms parametre_formulaire_sms_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.parametre_formulaire_sms
    ADD CONSTRAINT parametre_formulaire_sms_pkey PRIMARY KEY (id);


--
-- Name: parametre_gs1_ai parametre_gs1_ai_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.parametre_gs1_ai
    ADD CONSTRAINT parametre_gs1_ai_pkey PRIMARY KEY (code);


--
-- Name: parametre_integrite parametre_integrite_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.parametre_integrite
    ADD CONSTRAINT parametre_integrite_pkey PRIMARY KEY (id);


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
-- Name: parametre_postes parametre_postes_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.parametre_postes
    ADD CONSTRAINT parametre_postes_pkey PRIMARY KEY (id);


--
-- Name: parametre_rapport parametre_rapport_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.parametre_rapport
    ADD CONSTRAINT parametre_rapport_pkey PRIMARY KEY (id);


--
-- Name: parametre_sauvegarde parametre_sauvegarde_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.parametre_sauvegarde
    ADD CONSTRAINT parametre_sauvegarde_pkey PRIMARY KEY (id);


--
-- Name: parametre_serialisation parametre_serialisation_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.parametre_serialisation
    ADD CONSTRAINT parametre_serialisation_pkey PRIMARY KEY (id);


--
-- Name: parametre_substitution parametre_substitution_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.parametre_substitution
    ADD CONSTRAINT parametre_substitution_pkey PRIMARY KEY (id);


--
-- Name: parametre_surveillance parametre_surveillance_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.parametre_surveillance
    ADD CONSTRAINT parametre_surveillance_pkey PRIMARY KEY (id);


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
-- Name: poste_autorise poste_autorise_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.poste_autorise
    ADD CONSTRAINT poste_autorise_pkey PRIMARY KEY (id);


--
-- Name: presence_poste presence_poste_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.presence_poste
    ADD CONSTRAINT presence_poste_pkey PRIMARY KEY (poste);


--
-- Name: pret_bancaire pret_bancaire_compte_id_reference_key; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.pret_bancaire
    ADD CONSTRAINT pret_bancaire_compte_id_reference_key UNIQUE (compte_id, reference);


--
-- Name: pret_bancaire pret_bancaire_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.pret_bancaire
    ADD CONSTRAINT pret_bancaire_pkey PRIMARY KEY (id);


--
-- Name: produit produit_code_barre_key; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.produit
    ADD CONSTRAINT produit_code_barre_key UNIQUE (code_barre);


--
-- Name: produit_indication produit_indication_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.produit_indication
    ADD CONSTRAINT produit_indication_pkey PRIMARY KEY (produit_id, indication_id);


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
-- Name: profil_catalogue_grossiste profil_catalogue_grossiste_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.profil_catalogue_grossiste
    ADD CONSTRAINT profil_catalogue_grossiste_pkey PRIMARY KEY (fournisseur_id);


--
-- Name: reception_attente reception_attente_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.reception_attente
    ADD CONSTRAINT reception_attente_pkey PRIMARY KEY (id);


--
-- Name: reception_ligne reception_ligne_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.reception_ligne
    ADD CONSTRAINT reception_ligne_pkey PRIMARY KEY (id);


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
-- Name: reglage_central reglage_central_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.reglage_central
    ADD CONSTRAINT reglage_central_pkey PRIMARY KEY (cle);


--
-- Name: reglage_mutualise reglage_mutualise_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.reglage_mutualise
    ADD CONSTRAINT reglage_mutualise_pkey PRIMARY KEY (nom_table);


--
-- Name: regle_alerte_psychotrope regle_alerte_psychotrope_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.regle_alerte_psychotrope
    ADD CONSTRAINT regle_alerte_psychotrope_pkey PRIMARY KEY (code);


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
-- Name: regularisation_part_organisme regularisation_part_organisme_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.regularisation_part_organisme
    ADD CONSTRAINT regularisation_part_organisme_pkey PRIMARY KEY (id);


--
-- Name: remboursement_titulaire remboursement_titulaire_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.remboursement_titulaire
    ADD CONSTRAINT remboursement_titulaire_pkey PRIMARY KEY (id);


--
-- Name: remise_automatique remise_automatique_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.remise_automatique
    ADD CONSTRAINT remise_automatique_pkey PRIMARY KEY (id);


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
-- Name: rupture_grossiste rupture_grossiste_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.rupture_grossiste
    ADD CONSTRAINT rupture_grossiste_pkey PRIMARY KEY (id);


--
-- Name: scan_reception scan_reception_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.scan_reception
    ADD CONSTRAINT scan_reception_pkey PRIMARY KEY (id);


--
-- Name: sceau_integrite sceau_integrite_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.sceau_integrite
    ADD CONSTRAINT sceau_integrite_pkey PRIMARY KEY (id);


--
-- Name: serialisation_reponse_factice serialisation_reponse_factice_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.serialisation_reponse_factice
    ADD CONSTRAINT serialisation_reponse_factice_pkey PRIMARY KEY (id);


--
-- Name: serveur_ecarte serveur_ecarte_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.serveur_ecarte
    ADD CONSTRAINT serveur_ecarte_pkey PRIMARY KEY (id);


--
-- Name: serveur_officine serveur_officine_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.serveur_officine
    ADD CONSTRAINT serveur_officine_pkey PRIMARY KEY (nom);


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
-- Name: substitution_liste_blanche substitution_liste_blanche_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.substitution_liste_blanche
    ADD CONSTRAINT substitution_liste_blanche_pkey PRIMARY KEY (produit_id, remplacant_id);


--
-- Name: surveillance_replication surveillance_replication_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.surveillance_replication
    ADD CONSTRAINT surveillance_replication_pkey PRIMARY KEY (id);


--
-- Name: sync_chargement sync_chargement_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.sync_chargement
    ADD CONSTRAINT sync_chargement_pkey PRIMARY KEY (numero);


--
-- Name: sync_declaration_epoque sync_declaration_epoque_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.sync_declaration_epoque
    ADD CONSTRAINT sync_declaration_epoque_pkey PRIMARY KEY (nouvelle_epoque);


--
-- Name: sync_famille_colonne sync_famille_colonne_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.sync_famille_colonne
    ADD CONSTRAINT sync_famille_colonne_pkey PRIMARY KEY (famille, nom_table, colonne);


--
-- Name: sync_journal_descendant sync_journal_descendant_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.sync_journal_descendant
    ADD CONSTRAINT sync_journal_descendant_pkey PRIMARY KEY (id);


--
-- Name: sync_lot sync_lot_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.sync_lot
    ADD CONSTRAINT sync_lot_pkey PRIMARY KEY (epoque, numero);


--
-- Name: sync_ordre_recu sync_ordre_recu_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.sync_ordre_recu
    ADD CONSTRAINT sync_ordre_recu_pkey PRIMARY KEY (id);


--
-- Name: sync_ordre_resultat sync_ordre_resultat_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.sync_ordre_resultat
    ADD CONSTRAINT sync_ordre_resultat_pkey PRIMARY KEY (ordre_id);


--
-- Name: sync_parametre sync_parametre_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.sync_parametre
    ADD CONSTRAINT sync_parametre_pkey PRIMARY KEY (id);


--
-- Name: sync_pseudonyme sync_pseudonyme_code_key; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.sync_pseudonyme
    ADD CONSTRAINT sync_pseudonyme_code_key UNIQUE (code);


--
-- Name: sync_pseudonyme sync_pseudonyme_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.sync_pseudonyme
    ADD CONSTRAINT sync_pseudonyme_pkey PRIMARY KEY (nature, cle_locale);


--
-- Name: sync_sortie sync_sortie_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.sync_sortie
    ADD CONSTRAINT sync_sortie_pkey PRIMARY KEY (id);


--
-- Name: sync_table sync_table_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.sync_table
    ADD CONSTRAINT sync_table_pkey PRIMARY KEY (nom_table);


--
-- Name: type_prestation type_prestation_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.type_prestation
    ADD CONSTRAINT type_prestation_pkey PRIMARY KEY (id);


--
-- Name: bordereau uq_bordereau_organisme_numero; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.bordereau
    ADD CONSTRAINT uq_bordereau_organisme_numero UNIQUE (organisme_id, numero);


--
-- Name: bordereau_vente uq_bordereau_vente; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.bordereau_vente
    ADD CONSTRAINT uq_bordereau_vente UNIQUE (bordereau_id, vente_id);


--
-- Name: centre_payeur uq_centre_payeur_code; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.centre_payeur
    ADD CONSTRAINT uq_centre_payeur_code UNIQUE (organisme_id, code);


--
-- Name: motif_reglable uq_motif_reglable_code; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.motif_reglable
    ADD CONSTRAINT uq_motif_reglable_code UNIQUE (liste, code);


--
-- Name: versement_bordereau uq_versement_bordereau; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.versement_bordereau
    ADD CONSTRAINT uq_versement_bordereau UNIQUE (mouvement_id, bordereau_id);


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
-- Name: utilisation_financement utilisation_financement_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.utilisation_financement
    ADD CONSTRAINT utilisation_financement_pkey PRIMARY KEY (id);


--
-- Name: bascule_promotion ux_bascule_promotion_timeline; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.bascule_promotion
    ADD CONSTRAINT ux_bascule_promotion_timeline UNIQUE (timeline);


--
-- Name: historique_chifa ux_historique_organisme_facture; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.historique_chifa
    ADD CONSTRAINT ux_historique_organisme_facture UNIQUE (session_id, organisme, num_facture, date_facture);


--
-- Name: lot ux_lot_fournisseur_date; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.lot
    ADD CONSTRAINT ux_lot_fournisseur_date UNIQUE (produit_id, fournisseur_id, numero_lot, date_peremption);


--
-- Name: regularisation_part_organisme ux_regularisation_part_organisme_vente; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.regularisation_part_organisme
    ADD CONSTRAINT ux_regularisation_part_organisme_vente UNIQUE (vente_id);


--
-- Name: vente_chifa_facture_conflit vente_chifa_facture_conflit_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.vente_chifa_facture_conflit
    ADD CONSTRAINT vente_chifa_facture_conflit_pkey PRIMARY KEY (vente_id);


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
-- Name: verification_integrite verification_integrite_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.verification_integrite
    ADD CONSTRAINT verification_integrite_pkey PRIMARY KEY (id);


--
-- Name: verification_serialisation verification_serialisation_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.verification_serialisation
    ADD CONSTRAINT verification_serialisation_pkey PRIMARY KEY (id);


--
-- Name: versement_bordereau versement_bordereau_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.versement_bordereau
    ADD CONSTRAINT versement_bordereau_pkey PRIMARY KEY (id);


--
-- Name: wilaya wilaya_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.wilaya
    ADD CONSTRAINT wilaya_pkey PRIMARY KEY (code);


--
-- Name: zone_psychotrope_wilaya zone_psychotrope_wilaya_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.zone_psychotrope_wilaya
    ADD CONSTRAINT zone_psychotrope_wilaya_pkey PRIMARY KEY (wilaya);


--
-- Name: ix_doublon_patient_statut; Type: INDEX; Schema: import; Owner: -
--

CREATE INDEX ix_doublon_patient_statut ON import.doublon_patient USING btree (statut, id);


--
-- Name: ux_profil_reprise_logiciel_version; Type: INDEX; Schema: import; Owner: -
--

CREATE UNIQUE INDEX ux_profil_reprise_logiciel_version ON import.profil_reprise USING btree (lower(logiciel), lower(version));


--
-- Name: idx_alerte_non_envoyee_non_vue; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX idx_alerte_non_envoyee_non_vue ON public.alerte_non_envoyee USING btree (id) WHERE (vue_le IS NULL);


--
-- Name: idx_alerte_ouverte_tri; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX idx_alerte_ouverte_tri ON public.alerte USING btree (niveau, valeur_dzd DESC) WHERE (statut = ANY (ARRAY['nouvelle'::text, 'vue'::text]));


--
-- Name: idx_annulation_session; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX idx_annulation_session ON public.annulation_vente USING btree (session_caisse_id);


--
-- Name: idx_audit_cible; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX idx_audit_cible ON public.journal_audit USING btree (table_cible, enregistrement_id) WHERE (table_cible IS NOT NULL);


--
-- Name: idx_audit_date; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX idx_audit_date ON public.journal_audit USING btree (cree_le);


--
-- Name: idx_avoir_mouvement_patient; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX idx_avoir_mouvement_patient ON public.avoir_mouvement USING btree (patient_id, cree_le);


--
-- Name: idx_avoir_mouvement_vente; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX idx_avoir_mouvement_vente ON public.avoir_mouvement USING btree (vente_id) WHERE (vente_id IS NOT NULL);


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
-- Name: idx_bordereau_statut; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX idx_bordereau_statut ON public.bordereau USING btree (statut, organisme_id);


--
-- Name: idx_bordereau_vente_vente; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX idx_bordereau_vente_vente ON public.bordereau_vente USING btree (vente_id);


--
-- Name: idx_catalogue_grossiste_code; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX idx_catalogue_grossiste_code ON public.catalogue_grossiste USING btree (fournisseur_id, code_grossiste) WHERE (code_grossiste IS NOT NULL);


--
-- Name: idx_catalogue_grossiste_produit; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX idx_catalogue_grossiste_produit ON public.catalogue_grossiste USING btree (produit_id);


--
-- Name: idx_cloture_fiscale_periode; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX idx_cloture_fiscale_periode ON public.cloture_fiscale USING btree (periode_du, periode_au);


--
-- Name: idx_comptage_intermediaire_session; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX idx_comptage_intermediaire_session ON public.comptage_intermediaire USING btree (session_caisse_id, cree_le);


--
-- Name: idx_compte_carte_periode; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX idx_compte_carte_periode ON public.compte_carte_periode USING btree (compte_id, du);


--
-- Name: idx_credit_mouvement_patient; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX idx_credit_mouvement_patient ON public.credit_mouvement USING btree (patient_id, id);


--
-- Name: idx_credit_mouvement_vente; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX idx_credit_mouvement_vente ON public.credit_mouvement USING btree (vente_id) WHERE (vente_id IS NOT NULL);


--
-- Name: idx_dci_nom_sans_accent; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX idx_dci_nom_sans_accent ON public.dci USING gin (public.sans_accent(nom) public.gin_trgm_ops);


--
-- Name: idx_dci_nom_trgm; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX idx_dci_nom_trgm ON public.dci USING gin (nom public.gin_trgm_ops);


--
-- Name: idx_demande_patient_ouverte; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX idx_demande_patient_ouverte ON public.demande_patient USING btree (produit_id) WHERE (statut = ANY (ARRAY['en_attente'::text, 'prevenu'::text]));


--
-- Name: idx_demande_patient_patient; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX idx_demande_patient_patient ON public.demande_patient USING btree (patient_id, cree_le DESC);


--
-- Name: idx_echeance_pret_date; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX idx_echeance_pret_date ON public.echeance_pret USING btree (date_echeance);


--
-- Name: idx_emplacement_rayon; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX idx_emplacement_rayon ON public.emplacement USING btree (rayon, code);


--
-- Name: idx_facture_client_date; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX idx_facture_client_date ON public.facture_client USING btree (date_facture);


--
-- Name: idx_facture_client_echeance; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX idx_facture_client_echeance ON public.facture_client USING btree (date_echeance) WHERE (type = 'facture'::text);


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
-- Name: idx_forcage_psy_date; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX idx_forcage_psy_date ON public.forcage_alerte_psychotrope USING btree (cree_le DESC);


--
-- Name: idx_forcage_psy_utilisateur; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX idx_forcage_psy_utilisateur ON public.forcage_alerte_psychotrope USING btree (utilisateur_id, cree_le DESC);


--
-- Name: idx_fournisseur_mouvement; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX idx_fournisseur_mouvement ON public.fournisseur_mouvement USING btree (fournisseur_id, id);


--
-- Name: idx_import_catalogue_grossiste; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX idx_import_catalogue_grossiste ON public.import_catalogue_grossiste USING btree (fournisseur_id, importe_le DESC);


--
-- Name: idx_inventaire_campagne; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX idx_inventaire_campagne ON public.inventaire USING btree (campagne_id);


--
-- Name: idx_journal_sauvegarde; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX idx_journal_sauvegarde ON public.journal_sauvegarde USING btree (debut DESC);


--
-- Name: idx_journal_vente_survenu; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX idx_journal_vente_survenu ON public.journal_vente USING btree (survenu_le);


--
-- Name: idx_journal_vente_ticket; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX idx_journal_vente_ticket ON public.journal_vente USING btree (numero_ticket);


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
-- Name: idx_ligne_vente_type_prestation; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX idx_ligne_vente_type_prestation ON public.ligne_vente USING btree (type_prestation_id) WHERE (type_prestation_id IS NOT NULL);


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
-- Name: idx_medecin_sans_wilaya; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX idx_medecin_sans_wilaya ON public.medecin USING btree (nom) WHERE (wilaya IS NULL);


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
-- Name: idx_produit_indication_indication; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX idx_produit_indication_indication ON public.produit_indication USING btree (indication_id);


--
-- Name: idx_produit_nom_sans_accent; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX idx_produit_nom_sans_accent ON public.produit USING gin (public.sans_accent(nom_commercial) public.gin_trgm_ops);


--
-- Name: idx_produit_nom_trgm; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX idx_produit_nom_trgm ON public.produit USING gin (nom_commercial public.gin_trgm_ops);


--
-- Name: idx_reception_attente_cree; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX idx_reception_attente_cree ON public.reception_attente USING btree (cree_le);


--
-- Name: idx_reception_fournisseur; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX idx_reception_fournisseur ON public.reception USING btree (fournisseur_id, date_reception);


--
-- Name: idx_reception_ligne_lot; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX idx_reception_ligne_lot ON public.reception_ligne USING btree (lot_id);


--
-- Name: idx_reception_ligne_reception; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX idx_reception_ligne_reception ON public.reception_ligne USING btree (reception_id);


--
-- Name: idx_registre_date; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX idx_registre_date ON public.registre_psychotrope USING btree (cree_le);


--
-- Name: idx_registre_lot; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX idx_registre_lot ON public.registre_psychotrope USING btree (lot_id);


--
-- Name: idx_registre_medecin; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX idx_registre_medecin ON public.registre_psychotrope USING btree (medecin_id, cree_le) WHERE (medecin_id IS NOT NULL);


--
-- Name: idx_registre_patient; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX idx_registre_patient ON public.registre_psychotrope USING btree (patient_id, cree_le) WHERE (patient_id IS NOT NULL);


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
-- Name: idx_rupture_grossiste_produit; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX idx_rupture_grossiste_produit ON public.rupture_grossiste USING btree (produit_id, debut DESC);


--
-- Name: idx_scan_reception_attente; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX idx_scan_reception_attente ON public.scan_reception USING btree (utilisateur_id) WHERE (importe_le IS NULL);


--
-- Name: idx_sceau_integrite_journal; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX idx_sceau_integrite_journal ON public.sceau_integrite USING btree (journal, cle_chaine, scelle_le);


--
-- Name: idx_session_caisse_date; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX idx_session_caisse_date ON public.session_caisse USING btree (ouverte_le);


--
-- Name: idx_session_fermee; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX idx_session_fermee ON public.session_caisse USING btree (fermee_le) WHERE (fermee_le IS NOT NULL);


--
-- Name: idx_substitution_liste_blanche_remplacant; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX idx_substitution_liste_blanche_remplacant ON public.substitution_liste_blanche USING btree (remplacant_id);


--
-- Name: idx_utilisation_financement_pret; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX idx_utilisation_financement_pret ON public.utilisation_financement USING btree (pret_id);


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
-- Name: idx_verification_integrite_journal; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX idx_verification_integrite_journal ON public.verification_integrite USING btree (journal, verifie_le DESC);


--
-- Name: idx_verification_serialisation_attente; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX idx_verification_serialisation_attente ON public.verification_serialisation USING btree (prochain_essai_le, id) WHERE (statut = 'en_attente'::text);


--
-- Name: idx_verification_serialisation_boite; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX idx_verification_serialisation_boite ON public.verification_serialisation USING btree (boite_id);


--
-- Name: idx_verification_serialisation_statut; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX idx_verification_serialisation_statut ON public.verification_serialisation USING btree (statut, id);


--
-- Name: idx_versement_bordereau_bordereau; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX idx_versement_bordereau_bordereau ON public.versement_bordereau USING btree (bordereau_id);


--
-- Name: ix_bascule_decision_decidee_le; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX ix_bascule_decision_decidee_le ON public.bascule_decision USING btree (decidee_le);


--
-- Name: ix_bascule_saut_promotion; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX ix_bascule_saut_promotion ON public.bascule_saut USING btree (promotion_id);


--
-- Name: ix_commande_fournisseur_fournisseur; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX ix_commande_fournisseur_fournisseur ON public.commande_fournisseur USING btree (fournisseur_id);


--
-- Name: ix_comptage_reception_ligne; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX ix_comptage_reception_ligne ON public.comptage_reception_ligne USING btree (comptage_id);


--
-- Name: ix_comptage_reception_statut; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX ix_comptage_reception_statut ON public.comptage_reception USING btree (statut);


--
-- Name: ix_connexion_employe; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX ix_connexion_employe ON public.connexion_employe USING btree (utilisateur_id, cree_le);


--
-- Name: ix_correction_lot_lot; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX ix_correction_lot_lot ON public.correction_lot USING btree (lot_id, corrige_le);


--
-- Name: ix_credit_imputation_mouvement; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX ix_credit_imputation_mouvement ON public.credit_imputation USING btree (credit_mouvement_id);


--
-- Name: ix_credit_imputation_vente; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX ix_credit_imputation_vente ON public.credit_imputation USING btree (vente_id);


--
-- Name: ix_evenement_caisse_employe; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX ix_evenement_caisse_employe ON public.evenement_caisse USING btree (utilisateur_id, cree_le);


--
-- Name: ix_evenement_caisse_session; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX ix_evenement_caisse_session ON public.evenement_caisse USING btree (session_caisse_id);


--
-- Name: ix_facture_client_patient; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX ix_facture_client_patient ON public.facture_client USING btree (patient_id);


--
-- Name: ix_historique_chifa_assure; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX ix_historique_chifa_assure ON public.historique_chifa USING btree (num_assure);


--
-- Name: ix_historique_chifa_date; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX ix_historique_chifa_date ON public.historique_chifa USING btree (date_facture DESC);


--
-- Name: ix_historique_chifa_organisme; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX ix_historique_chifa_organisme ON public.historique_chifa USING btree (organisme, date_facture DESC);


--
-- Name: ix_journal_vente_session_caisse; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX ix_journal_vente_session_caisse ON public.journal_vente USING btree (session_caisse_id);


--
-- Name: ix_ligne_commande_commande; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX ix_ligne_commande_commande ON public.ligne_commande USING btree (commande_id);


--
-- Name: ix_ligne_retour_fournisseur_retour; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX ix_ligne_retour_fournisseur_retour ON public.ligne_retour_fournisseur USING btree (retour_id);


--
-- Name: ix_lot_produit_numero; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX ix_lot_produit_numero ON public.lot USING btree (produit_id, numero_lot);


--
-- Name: INDEX ix_lot_produit_numero; Type: COMMENT; Schema: public; Owner: -
--

COMMENT ON INDEX public.ix_lot_produit_numero IS 'Numéros de lot d''un produit proposés au rappel, épuisés compris (script 150).';


--
-- Name: ix_medecin_nom_sans_accent; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX ix_medecin_nom_sans_accent ON public.medecin USING gin (public.sans_accent(nom) public.gin_trgm_ops);


--
-- Name: INDEX ix_medecin_nom_sans_accent; Type: COMMENT; Schema: public; Owner: -
--

COMMENT ON INDEX public.ix_medecin_nom_sans_accent IS 'Recherche d''un médecin sans accents ni majuscules (script 150).';


--
-- Name: ix_mouvement_tiroir_employe; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX ix_mouvement_tiroir_employe ON public.mouvement_tiroir USING btree (utilisateur_id, cree_le);


--
-- Name: ix_mouvement_tiroir_regularisation; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX ix_mouvement_tiroir_regularisation ON public.mouvement_tiroir USING btree (regularisation_id) WHERE (regularisation_id IS NOT NULL);


--
-- Name: ix_mouvement_tiroir_session; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX ix_mouvement_tiroir_session ON public.mouvement_tiroir USING btree (session_caisse_id);


--
-- Name: ix_patient_assure_principal; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX ix_patient_assure_principal ON public.patient USING btree (assure_principal_id) WHERE (assure_principal_id IS NOT NULL);


--
-- Name: ix_patient_nom_minuscule; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX ix_patient_nom_minuscule ON public.patient USING btree (lower(nom));


--
-- Name: ix_patient_nom_prenom_sans_accent; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX ix_patient_nom_prenom_sans_accent ON public.patient USING gin (public.sans_accent(((nom || ' '::text) || COALESCE(prenom, ''::text))) public.gin_trgm_ops);


--
-- Name: INDEX ix_patient_nom_prenom_sans_accent; Type: COMMENT; Schema: public; Owner: -
--

COMMENT ON INDEX public.ix_patient_nom_prenom_sans_accent IS 'Recherche d''un patient sans accents ni majuscules (nom, prénom, « nom prénom ») (script 150).';


--
-- Name: ix_patient_num_assure_base; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX ix_patient_num_assure_base ON public.patient USING btree ("left"(regexp_replace(num_assure, '\D'::text, ''::text, 'g'::text), 12)) WHERE (num_assure IS NOT NULL);


--
-- Name: ix_patient_prenom_minuscule; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX ix_patient_prenom_minuscule ON public.patient USING btree (lower(prenom));


--
-- Name: ix_poste_autorise_cree_par; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX ix_poste_autorise_cree_par ON public.poste_autorise USING btree (cree_par);


--
-- Name: ix_poste_autorise_revoque_par; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX ix_poste_autorise_revoque_par ON public.poste_autorise USING btree (revoque_par);


--
-- Name: ix_presence_poste_poste_id; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX ix_presence_poste_poste_id ON public.presence_poste USING btree (poste_id);


--
-- Name: ix_presence_poste_vu_le; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX ix_presence_poste_vu_le ON public.presence_poste USING btree (vu_le);


--
-- Name: ix_produit_dci; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX ix_produit_dci ON public.produit USING btree (dci_id);


--
-- Name: ix_remise_auto_categorie; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX ix_remise_auto_categorie ON public.remise_automatique USING btree (categorie) WHERE (categorie IS NOT NULL);


--
-- Name: ix_remise_auto_produit; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX ix_remise_auto_produit ON public.remise_automatique USING btree (produit_id) WHERE (produit_id IS NOT NULL);


--
-- Name: ix_serveur_ecarte_ecarte_le; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX ix_serveur_ecarte_ecarte_le ON public.serveur_ecarte USING btree (ecarte_le);


--
-- Name: ix_sync_famille_colonne_table; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX ix_sync_famille_colonne_table ON public.sync_famille_colonne USING btree (nom_table);


--
-- Name: ix_sync_ordre_recu_message; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX ix_sync_ordre_recu_message ON public.sync_ordre_recu USING btree (cree_le DESC) WHERE ((type = 'message'::text) AND (statut = 'applique'::text) AND (message_ferme_le IS NULL));


--
-- Name: ix_sync_ordre_recu_message_ferme_par; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX ix_sync_ordre_recu_message_ferme_par ON public.sync_ordre_recu USING btree (message_ferme_par) WHERE (message_ferme_par IS NOT NULL);


--
-- Name: ix_sync_ordre_recu_statut; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX ix_sync_ordre_recu_statut ON public.sync_ordre_recu USING btree (statut, recu_le);


--
-- Name: ix_sync_sortie_chargement; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX ix_sync_sortie_chargement ON public.sync_sortie USING btree (chargement) WHERE (chargement IS NOT NULL);


--
-- Name: ix_sync_sortie_en_attente; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX ix_sync_sortie_en_attente ON public.sync_sortie USING btree (id) WHERE (lot_numero IS NULL);


--
-- Name: ix_sync_sortie_en_attente_ordre; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX ix_sync_sortie_en_attente_ordre ON public.sync_sortie USING btree (COALESCE(ordre, id), id) WHERE (lot_numero IS NULL);


--
-- Name: ix_sync_sortie_lot; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX ix_sync_sortie_lot ON public.sync_sortie USING btree (epoque, lot_numero) WHERE (lot_numero IS NOT NULL);


--
-- Name: ix_vente_centre_payeur; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX ix_vente_centre_payeur ON public.vente USING btree (centre_payeur_id) WHERE (centre_payeur_id IS NOT NULL);


--
-- Name: ix_vente_chifa_attente; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX ix_vente_chifa_attente ON public.vente USING btree (date_vente) WHERE ((canal_organisme = 'chifa'::text) AND (chifa_facture IS NULL) AND (statut = 'validee'::text));


--
-- Name: ix_vente_chifa_ecart; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX ix_vente_chifa_ecart ON public.vente USING btree (chifa_rattache_le) WHERE (chifa_ecart_motif IS NOT NULL);


--
-- Name: ix_vente_chifa_exclue; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX ix_vente_chifa_exclue ON public.vente USING btree (chifa_exclue_le DESC) WHERE (chifa_exclue_le IS NOT NULL);


--
-- Name: ix_vente_chifa_facture_conflit_a_trancher; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX ix_vente_chifa_facture_conflit_a_trancher ON public.vente_chifa_facture_conflit USING btree (constate_le) WHERE (tranche_le IS NULL);


--
-- Name: ix_vente_pour_patient; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX ix_vente_pour_patient ON public.vente USING btree (pour_patient_id) WHERE (pour_patient_id IS NOT NULL);


--
-- Name: ix_vente_remis_a_patient; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX ix_vente_remis_a_patient ON public.vente USING btree (remis_a_patient_id) WHERE (remis_a_patient_id IS NOT NULL);


--
-- Name: uq_remise_auto_modele_cle; Type: INDEX; Schema: public; Owner: -
--

CREATE UNIQUE INDEX uq_remise_auto_modele_cle ON public.remise_automatique USING btree (modele_cle) WHERE (modele_cle IS NOT NULL);


--
-- Name: uq_rupture_grossiste_ouverte; Type: INDEX; Schema: public; Owner: -
--

CREATE UNIQUE INDEX uq_rupture_grossiste_ouverte ON public.rupture_grossiste USING btree (fournisseur_id, produit_id) WHERE (fin IS NULL);


--
-- Name: uq_serveur_officine_principal; Type: INDEX; Schema: public; Owner: -
--

CREATE UNIQUE INDEX uq_serveur_officine_principal ON public.serveur_officine USING btree (role) WHERE (role = 'principal'::text);


--
-- Name: ux_alerte_ouverte; Type: INDEX; Schema: public; Owner: -
--

CREATE UNIQUE INDEX ux_alerte_ouverte ON public.alerte USING btree (type, produit_id, COALESCE(lot_id, (0)::bigint)) WHERE (statut = ANY (ARRAY['nouvelle'::text, 'vue'::text, 'reportee'::text]));


--
-- Name: ux_alerte_surveillance; Type: INDEX; Schema: public; Owner: -
--

CREATE UNIQUE INDEX ux_alerte_surveillance ON public.alerte_surveillance USING btree (type, COALESCE(utilisateur_id, (0)::bigint), jour, cle);


--
-- Name: ux_boite_vue_inventaire; Type: INDEX; Schema: public; Owner: -
--

CREATE UNIQUE INDEX ux_boite_vue_inventaire ON public.boite_evenement USING btree (boite_id, inventaire_id) WHERE (type = 'inventaire'::text);


--
-- Name: ux_bordereau_vente_active; Type: INDEX; Schema: public; Owner: -
--

CREATE UNIQUE INDEX ux_bordereau_vente_active ON public.bordereau_vente USING btree (vente_id) WHERE (suite IS DISTINCT FROM 'redeposer'::text);


--
-- Name: ux_campagne_en_cours; Type: INDEX; Schema: public; Owner: -
--

CREATE UNIQUE INDEX ux_campagne_en_cours ON public.campagne_inventaire USING btree ((true)) WHERE (statut = 'en_cours'::text);


--
-- Name: ux_client_facture_raison_sociale_ci; Type: INDEX; Schema: public; Owner: -
--

CREATE UNIQUE INDEX ux_client_facture_raison_sociale_ci ON public.client_facture USING btree (lower(btrim(raison_sociale)));


--
-- Name: ux_cloture_fiscale_mois; Type: INDEX; Schema: public; Owner: -
--

CREATE UNIQUE INDEX ux_cloture_fiscale_mois ON public.cloture_fiscale USING btree (periode_du) WHERE (type = 'mensuelle'::text);


--
-- Name: ux_cloture_fiscale_session; Type: INDEX; Schema: public; Owner: -
--

CREATE UNIQUE INDEX ux_cloture_fiscale_session ON public.cloture_fiscale USING btree (session_caisse_id) WHERE (type = 'journaliere'::text);


--
-- Name: ux_compte_tresorerie_nom_ci; Type: INDEX; Schema: public; Owner: -
--

CREATE UNIQUE INDEX ux_compte_tresorerie_nom_ci ON public.compte_tresorerie USING btree (lower(btrim(nom)));


--
-- Name: ux_indication_libelle; Type: INDEX; Schema: public; Owner: -
--

CREATE UNIQUE INDEX ux_indication_libelle ON public.indication USING btree (lower(btrim(libelle)));


--
-- Name: ux_integrite_essai_chaine; Type: INDEX; Schema: public; Owner: -
--

CREATE UNIQUE INDEX ux_integrite_essai_chaine ON public.integrite_essai USING btree (cle_chaine, rang);


--
-- Name: ux_inventaire_en_cours; Type: INDEX; Schema: public; Owner: -
--

CREATE UNIQUE INDEX ux_inventaire_en_cours ON public.inventaire USING btree (rayon) WHERE (statut = 'en_cours'::text);


--
-- Name: ux_journal_registre_annulation; Type: INDEX; Schema: public; Owner: -
--

CREATE UNIQUE INDEX ux_journal_registre_annulation ON public.journal_registre_psychotrope USING btree (annulation_id) WHERE (nature = 'annulation'::text);


--
-- Name: ux_journal_registre_inscription; Type: INDEX; Schema: public; Owner: -
--

CREATE UNIQUE INDEX ux_journal_registre_inscription ON public.journal_registre_psychotrope USING btree (registre_id) WHERE (nature = 'inscription'::text);


--
-- Name: ux_journal_registre_ouverture; Type: INDEX; Schema: public; Owner: -
--

CREATE UNIQUE INDEX ux_journal_registre_ouverture ON public.journal_registre_psychotrope USING btree (cle_chaine) WHERE (nature = 'ouverture'::text);


--
-- Name: ux_journal_registre_psychotrope_chaine; Type: INDEX; Schema: public; Owner: -
--

CREATE UNIQUE INDEX ux_journal_registre_psychotrope_chaine ON public.journal_registre_psychotrope USING btree (cle_chaine, rang);


--
-- Name: ux_journal_vente_annulation; Type: INDEX; Schema: public; Owner: -
--

CREATE UNIQUE INDEX ux_journal_vente_annulation ON public.journal_vente USING btree (vente_id) WHERE (evenement = 'annulation'::text);


--
-- Name: ux_journal_vente_avoir; Type: INDEX; Schema: public; Owner: -
--

CREATE UNIQUE INDEX ux_journal_vente_avoir ON public.journal_vente USING btree (avoir_mouvement_id) WHERE (avoir_mouvement_id IS NOT NULL);


--
-- Name: ux_journal_vente_chaine; Type: INDEX; Schema: public; Owner: -
--

CREATE UNIQUE INDEX ux_journal_vente_chaine ON public.journal_vente USING btree (cle_chaine, rang);


--
-- Name: ux_journal_vente_regularisation; Type: INDEX; Schema: public; Owner: -
--

CREATE UNIQUE INDEX ux_journal_vente_regularisation ON public.journal_vente USING btree (regularisation_id, evenement) WHERE (regularisation_id IS NOT NULL);


--
-- Name: ux_journal_vente_retour; Type: INDEX; Schema: public; Owner: -
--

CREATE UNIQUE INDEX ux_journal_vente_retour ON public.journal_vente USING btree (retour_client_id) WHERE (retour_client_id IS NOT NULL);


--
-- Name: ux_journal_vente_vente; Type: INDEX; Schema: public; Owner: -
--

CREATE UNIQUE INDEX ux_journal_vente_vente ON public.journal_vente USING btree (vente_id) WHERE (evenement = 'vente'::text);


--
-- Name: ux_ordonnance_chemin_scan; Type: INDEX; Schema: public; Owner: -
--

CREATE UNIQUE INDEX ux_ordonnance_chemin_scan ON public.ordonnance USING btree (chemin_scan) WHERE (chemin_scan IS NOT NULL);


--
-- Name: ux_paiement_numero_recu; Type: INDEX; Schema: public; Owner: -
--

CREATE UNIQUE INDEX ux_paiement_numero_recu ON public.paiement USING btree (numero_recu) WHERE (numero_recu IS NOT NULL);


--
-- Name: ux_patient_num_assure; Type: INDEX; Schema: public; Owner: -
--

CREATE UNIQUE INDEX ux_patient_num_assure ON public.patient USING btree (regexp_replace(num_assure, '\D'::text, ''::text, 'g'::text)) WHERE ((num_assure IS NOT NULL) AND (regexp_replace(num_assure, '\D'::text, ''::text, 'g'::text) <> ''::text));


--
-- Name: ux_poste_autorise_code; Type: INDEX; Schema: public; Owner: -
--

CREATE UNIQUE INDEX ux_poste_autorise_code ON public.poste_autorise USING btree (code_hache) WHERE (statut = 'en_attente'::text);


--
-- Name: ux_poste_autorise_nom_actif; Type: INDEX; Schema: public; Owner: -
--

CREATE UNIQUE INDEX ux_poste_autorise_nom_actif ON public.poste_autorise USING btree (lower((nom)::text)) WHERE (statut <> 'revoque'::text);


--
-- Name: ux_pret_bancaire_preteur_reference; Type: INDEX; Schema: public; Owner: -
--

CREATE UNIQUE INDEX ux_pret_bancaire_preteur_reference ON public.pret_bancaire USING btree (lower(btrim(preteur)), reference) WHERE (preteur IS NOT NULL);


--
-- Name: ux_produit_nom_dosage_forme; Type: INDEX; Schema: public; Owner: -
--

CREATE UNIQUE INDEX ux_produit_nom_dosage_forme ON public.produit USING btree (lower(nom_commercial), lower(COALESCE(dosage, ''::text)), lower(COALESCE(forme, ''::text)));


--
-- Name: ux_registre_numero_ordre; Type: INDEX; Schema: public; Owner: -
--

CREATE UNIQUE INDEX ux_registre_numero_ordre ON public.registre_psychotrope USING btree (numero_ordre);


--
-- Name: ux_reglement_fournisseur_numero; Type: INDEX; Schema: public; Owner: -
--

CREATE UNIQUE INDEX ux_reglement_fournisseur_numero ON public.reglement_fournisseur USING btree (numero) WHERE (numero IS NOT NULL);


--
-- Name: ux_scan_reception_serie; Type: INDEX; Schema: public; Owner: -
--

CREATE UNIQUE INDEX ux_scan_reception_serie ON public.scan_reception USING btree (gtin, numero_serie) WHERE ((importe_le IS NULL) AND (numero_serie IS NOT NULL));


--
-- Name: ux_serveur_ecarte_active; Type: INDEX; Schema: public; Owner: -
--

CREATE UNIQUE INDEX ux_serveur_ecarte_active ON public.serveur_ecarte USING btree ((true)) WHERE (leve_le IS NULL);


--
-- Name: ux_session_poste_ouverte; Type: INDEX; Schema: public; Owner: -
--

CREATE UNIQUE INDEX ux_session_poste_ouverte ON public.session_caisse USING btree (poste) WHERE (fermee_le IS NULL);


--
-- Name: ux_sync_chargement_en_cours; Type: INDEX; Schema: public; Owner: -
--

CREATE UNIQUE INDEX ux_sync_chargement_en_cours ON public.sync_chargement USING btree ((true)) WHERE (etat <> ALL (ARRAY['termine'::text, 'remplace'::text]));


--
-- Name: ux_type_prestation_code; Type: INDEX; Schema: public; Owner: -
--

CREATE UNIQUE INDEX ux_type_prestation_code ON public.type_prestation USING btree (upper(btrim(code)));


--
-- Name: ux_un_coffre; Type: INDEX; Schema: public; Owner: -
--

CREATE UNIQUE INDEX ux_un_coffre ON public.compte_tresorerie USING btree (type) WHERE (type = 'coffre'::text);


--
-- Name: ux_une_banque_carte; Type: INDEX; Schema: public; Owner: -
--

CREATE UNIQUE INDEX ux_une_banque_carte ON public.compte_tresorerie USING btree (recoit_carte) WHERE recoit_carte;


--
-- Name: ux_vente_chifa_facture; Type: INDEX; Schema: public; Owner: -
--

CREATE UNIQUE INDEX ux_vente_chifa_facture ON public.vente USING btree (chifa_facture) WHERE ((chifa_facture IS NOT NULL) AND (statut <> 'annulee'::text));


--
-- Name: ux_vente_cle_encaissement; Type: INDEX; Schema: public; Owner: -
--

CREATE UNIQUE INDEX ux_vente_cle_encaissement ON public.vente USING btree (cle_encaissement) WHERE (cle_encaissement IS NOT NULL);


--
-- Name: ux_zone_psychotrope_officine; Type: INDEX; Schema: public; Owner: -
--

CREATE UNIQUE INDEX ux_zone_psychotrope_officine ON public.zone_psychotrope_wilaya USING btree (officine) WHERE officine;


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
 SELECT h.compte_id,
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
     JOIN public.compte_carte_periode h ON ((((p.cree_le)::date >= h.du) AND ((h.au IS NULL) OR ((p.cree_le)::date <= h.au)))))
     LEFT JOIN public.vente v ON ((v.id = p.vente_id)))
  WHERE ((p.mode = 'carte'::text) AND ((p.vente_id IS NULL) OR (v.statut = ANY (ARRAY['validee'::text, 'annulee'::text]))))
  GROUP BY h.compte_id, ((p.cree_le)::date)
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
 SELECT
        CASE
            WHEN (pe.moyen = 'especes'::text) THEN ( SELECT compte_tresorerie.id
               FROM public.compte_tresorerie
              WHERE (compte_tresorerie.type = 'coffre'::text))
            ELSE pe.compte_id
        END AS compte_id,
    pe.date_paiement AS date,
    'sortie'::text AS sens,
    g.montant,
    'reglement_grossiste'::text AS nature,
    NULL::text AS categorie,
    ((('Traite '::text || COALESCE((g.reference || ' '::text), ''::text)) || 'payée · '::text) || f.raison_sociale) AS libelle,
    pe.reference,
    u.nom_complet AS employe,
    'calcule'::text AS origine,
    pe.id AS source_id,
    pe.cree_le
   FROM (((public.paiement_effet pe
     JOIN public.reglement_fournisseur g ON ((g.id = pe.reglement_id)))
     JOIN public.fournisseur f ON ((f.id = g.fournisseur_id)))
     JOIN public.utilisateur u ON ((u.id = pe.utilisateur_id)))
  WHERE (g.compte_id IS NULL)
UNION ALL
 SELECT h.compte_id,
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
            'Remboursement carte (retour)'::text AS libelle
           FROM public.retour_client
          WHERE ((retour_client.mode = 'carte'::text) AND (retour_client.montant_rembourse > (0)::numeric))) x
     JOIN public.compte_carte_periode h ON ((((x.cree_le)::date >= h.du) AND ((h.au IS NULL) OR ((x.cree_le)::date <= h.au)))));


--
-- Name: avoir_mouvement tg_avoir_mouvement_immuable; Type: TRIGGER; Schema: public; Owner: -
--

CREATE TRIGGER tg_avoir_mouvement_immuable BEFORE DELETE OR UPDATE ON public.avoir_mouvement FOR EACH ROW EXECUTE FUNCTION public.avoir_mouvement_immuable();


--
-- Name: avoir_mouvement tg_avoir_mouvement_solde; Type: TRIGGER; Schema: public; Owner: -
--

CREATE TRIGGER tg_avoir_mouvement_solde BEFORE INSERT ON public.avoir_mouvement FOR EACH ROW EXECUTE FUNCTION public.avoir_mouvement_controle_solde();


--
-- Name: annulation_registre trg_annulation_registre_immuable; Type: TRIGGER; Schema: public; Owner: -
--

CREATE TRIGGER trg_annulation_registre_immuable BEFORE DELETE OR UPDATE ON public.annulation_registre FOR EACH ROW EXECUTE FUNCTION public.ajout_seul_refuser();


--
-- Name: annulation_registre trg_annulation_registre_non_vidable; Type: TRIGGER; Schema: public; Owner: -
--

CREATE TRIGGER trg_annulation_registre_non_vidable BEFORE TRUNCATE ON public.annulation_registre FOR EACH STATEMENT EXECUTE FUNCTION public.ajout_seul_refuser();


--
-- Name: bascule_decision trg_bascule_decision_ajout_seul; Type: TRIGGER; Schema: public; Owner: -
--

CREATE TRIGGER trg_bascule_decision_ajout_seul BEFORE DELETE OR UPDATE ON public.bascule_decision FOR EACH ROW EXECUTE FUNCTION public.bascule_decision_proteger();


--
-- Name: bascule_decision trg_bascule_decision_non_vidable; Type: TRIGGER; Schema: public; Owner: -
--

CREATE TRIGGER trg_bascule_decision_non_vidable BEFORE TRUNCATE ON public.bascule_decision FOR EACH STATEMENT EXECUTE FUNCTION public.bascule_decision_proteger();


--
-- Name: bascule_promotion trg_bascule_promotion_ajout_seul; Type: TRIGGER; Schema: public; Owner: -
--

CREATE TRIGGER trg_bascule_promotion_ajout_seul BEFORE DELETE OR UPDATE ON public.bascule_promotion FOR EACH ROW EXECUTE FUNCTION public.integrite_ajout_seul_restauration();


--
-- Name: bascule_promotion trg_bascule_promotion_non_vidable; Type: TRIGGER; Schema: public; Owner: -
--

CREATE TRIGGER trg_bascule_promotion_non_vidable BEFORE TRUNCATE ON public.bascule_promotion FOR EACH STATEMENT EXECUTE FUNCTION public.integrite_ajout_seul_restauration();


--
-- Name: bascule_saut trg_bascule_saut_ajout_seul; Type: TRIGGER; Schema: public; Owner: -
--

CREATE TRIGGER trg_bascule_saut_ajout_seul BEFORE DELETE OR UPDATE ON public.bascule_saut FOR EACH ROW EXECUTE FUNCTION public.integrite_ajout_seul_restauration();


--
-- Name: bascule_saut trg_bascule_saut_non_vidable; Type: TRIGGER; Schema: public; Owner: -
--

CREATE TRIGGER trg_bascule_saut_non_vidable BEFORE TRUNCATE ON public.bascule_saut FOR EACH STATEMENT EXECUTE FUNCTION public.integrite_ajout_seul_restauration();


--
-- Name: boite trg_boite_entree; Type: TRIGGER; Schema: public; Owner: -
--

CREATE TRIGGER trg_boite_entree AFTER INSERT ON public.boite FOR EACH ROW EXECUTE FUNCTION public.journaliser_entree_boite();


--
-- Name: boite_evenement trg_boite_evenement_immuable; Type: TRIGGER; Schema: public; Owner: -
--

CREATE TRIGGER trg_boite_evenement_immuable BEFORE DELETE OR UPDATE ON public.boite_evenement FOR EACH ROW EXECUTE FUNCTION public.interdire_modification_historique();


--
-- Name: boite_evenement trg_boite_evenement_non_vidable; Type: TRIGGER; Schema: public; Owner: -
--

CREATE TRIGGER trg_boite_evenement_non_vidable BEFORE TRUNCATE ON public.boite_evenement FOR EACH STATEMENT EXECUTE FUNCTION public.interdire_vidage();


--
-- Name: boite trg_boite_reception; Type: TRIGGER; Schema: public; Owner: -
--

CREATE TRIGGER trg_boite_reception BEFORE INSERT ON public.boite FOR EACH ROW EXECUTE FUNCTION public.completer_reception_boite();


--
-- Name: boite trg_boite_serialisation; Type: TRIGGER; Schema: public; Owner: -
--

CREATE TRIGGER trg_boite_serialisation AFTER INSERT OR UPDATE OF statut ON public.boite FOR EACH ROW EXECUTE FUNCTION public.mettre_en_file_serialisation();


--
-- Name: annulation_vente trg_caisse_ouverte; Type: TRIGGER; Schema: public; Owner: -
--

CREATE TRIGGER trg_caisse_ouverte BEFORE INSERT ON public.annulation_vente FOR EACH ROW EXECUTE FUNCTION public.verifier_caisse_ouverte();


--
-- Name: mouvement_tiroir trg_caisse_ouverte; Type: TRIGGER; Schema: public; Owner: -
--

CREATE TRIGGER trg_caisse_ouverte BEFORE INSERT ON public.mouvement_tiroir FOR EACH ROW EXECUTE FUNCTION public.verifier_caisse_ouverte();


--
-- Name: mouvement_tresorerie trg_caisse_ouverte; Type: TRIGGER; Schema: public; Owner: -
--

CREATE TRIGGER trg_caisse_ouverte BEFORE INSERT ON public.mouvement_tresorerie FOR EACH ROW EXECUTE FUNCTION public.verifier_caisse_ouverte();


--
-- Name: paiement trg_caisse_ouverte; Type: TRIGGER; Schema: public; Owner: -
--

CREATE TRIGGER trg_caisse_ouverte BEFORE INSERT ON public.paiement FOR EACH ROW EXECUTE FUNCTION public.verifier_caisse_ouverte();


--
-- Name: reglement_fournisseur trg_caisse_ouverte; Type: TRIGGER; Schema: public; Owner: -
--

CREATE TRIGGER trg_caisse_ouverte BEFORE INSERT ON public.reglement_fournisseur FOR EACH ROW EXECUTE FUNCTION public.verifier_caisse_ouverte();


--
-- Name: retour_client trg_caisse_ouverte; Type: TRIGGER; Schema: public; Owner: -
--

CREATE TRIGGER trg_caisse_ouverte BEFORE INSERT ON public.retour_client FOR EACH ROW EXECUTE FUNCTION public.verifier_caisse_ouverte();


--
-- Name: vente trg_caisse_ouverte; Type: TRIGGER; Schema: public; Owner: -
--

CREATE TRIGGER trg_caisse_ouverte BEFORE INSERT ON public.vente FOR EACH ROW EXECUTE FUNCTION public.verifier_caisse_ouverte();


--
-- Name: chaine_integrite trg_chaine_integrite_immuable; Type: TRIGGER; Schema: public; Owner: -
--

CREATE TRIGGER trg_chaine_integrite_immuable BEFORE DELETE OR UPDATE ON public.chaine_integrite FOR EACH ROW EXECUTE FUNCTION public.integrite_registre_immuable();


--
-- Name: cloture_fiscale trg_cloture_fiscale_immuable; Type: TRIGGER; Schema: public; Owner: -
--

CREATE TRIGGER trg_cloture_fiscale_immuable BEFORE DELETE OR UPDATE ON public.cloture_fiscale FOR EACH ROW EXECUTE FUNCTION public.cloture_fiscale_immuable();


--
-- Name: cloture_fiscale trg_cloture_fiscale_non_vidable; Type: TRIGGER; Schema: public; Owner: -
--

CREATE TRIGGER trg_cloture_fiscale_non_vidable BEFORE TRUNCATE ON public.cloture_fiscale FOR EACH STATEMENT EXECUTE FUNCTION public.cloture_fiscale_immuable();


--
-- Name: compte_tresorerie trg_compte_carte; Type: TRIGGER; Schema: public; Owner: -
--

CREATE TRIGGER trg_compte_carte AFTER INSERT OR UPDATE OF recoit_carte ON public.compte_tresorerie FOR EACH ROW EXECUTE FUNCTION public.suivre_compte_carte();


--
-- Name: compteur_ticket trg_compteur_ticket_proteger; Type: TRIGGER; Schema: public; Owner: -
--

CREATE TRIGGER trg_compteur_ticket_proteger BEFORE DELETE OR UPDATE ON public.compteur_ticket FOR EACH ROW EXECUTE FUNCTION public.compteur_ticket_proteger();


--
-- Name: compteur_ticket_saut trg_compteur_ticket_saut_ajout_seul; Type: TRIGGER; Schema: public; Owner: -
--

CREATE TRIGGER trg_compteur_ticket_saut_ajout_seul BEFORE DELETE OR UPDATE ON public.compteur_ticket_saut FOR EACH ROW EXECUTE FUNCTION public.integrite_ajout_seul_restauration();


--
-- Name: compteur_ticket_saut trg_compteur_ticket_saut_non_vidable; Type: TRIGGER; Schema: public; Owner: -
--

CREATE TRIGGER trg_compteur_ticket_saut_non_vidable BEFORE TRUNCATE ON public.compteur_ticket_saut FOR EACH STATEMENT EXECUTE FUNCTION public.integrite_ajout_seul_restauration();


--
-- Name: annulation_vente trg_conservation_fiscale; Type: TRIGGER; Schema: public; Owner: -
--

CREATE TRIGGER trg_conservation_fiscale BEFORE DELETE ON public.annulation_vente FOR EACH ROW EXECUTE FUNCTION public.conservation_fiscale_proteger();


--
-- Name: paiement trg_conservation_fiscale; Type: TRIGGER; Schema: public; Owner: -
--

CREATE TRIGGER trg_conservation_fiscale BEFORE DELETE ON public.paiement FOR EACH ROW EXECUTE FUNCTION public.conservation_fiscale_proteger();


--
-- Name: retour_client trg_conservation_fiscale; Type: TRIGGER; Schema: public; Owner: -
--

CREATE TRIGGER trg_conservation_fiscale BEFORE DELETE ON public.retour_client FOR EACH ROW EXECUTE FUNCTION public.conservation_fiscale_proteger();


--
-- Name: vente trg_conservation_fiscale; Type: TRIGGER; Schema: public; Owner: -
--

CREATE TRIGGER trg_conservation_fiscale BEFORE DELETE ON public.vente FOR EACH ROW EXECUTE FUNCTION public.conservation_fiscale_proteger();


--
-- Name: correction_lot trg_correction_lot_immuable; Type: TRIGGER; Schema: public; Owner: -
--

CREATE TRIGGER trg_correction_lot_immuable BEFORE DELETE OR UPDATE ON public.correction_lot FOR EACH ROW EXECUTE FUNCTION public.interdire_modification();


--
-- Name: credit_mouvement trg_credit_mouvement_immuable; Type: TRIGGER; Schema: public; Owner: -
--

CREATE TRIGGER trg_credit_mouvement_immuable BEFORE DELETE OR UPDATE ON public.credit_mouvement FOR EACH ROW EXECUTE FUNCTION public.proteger_credit_mouvement();


--
-- Name: credit_mouvement trg_credit_mouvement_non_vidable; Type: TRIGGER; Schema: public; Owner: -
--

CREATE TRIGGER trg_credit_mouvement_non_vidable BEFORE TRUNCATE ON public.credit_mouvement FOR EACH STATEMENT EXECUTE FUNCTION public.interdire_vidage();


--
-- Name: credit_mouvement trg_credit_ouverture; Type: TRIGGER; Schema: public; Owner: -
--

CREATE TRIGGER trg_credit_ouverture AFTER INSERT ON public.credit_mouvement FOR EACH ROW EXECUTE FUNCTION public.noter_credit_ouverture();


--
-- Name: forcage_alerte_psychotrope trg_forcage_psy_immuable; Type: TRIGGER; Schema: public; Owner: -
--

CREATE TRIGGER trg_forcage_psy_immuable BEFORE DELETE OR UPDATE ON public.forcage_alerte_psychotrope FOR EACH ROW EXECUTE FUNCTION public.ajout_seul_refuser();


--
-- Name: forcage_alerte_psychotrope trg_forcage_psy_non_vidable; Type: TRIGGER; Schema: public; Owner: -
--

CREATE TRIGGER trg_forcage_psy_non_vidable BEFORE TRUNCATE ON public.forcage_alerte_psychotrope FOR EACH STATEMENT EXECUTE FUNCTION public.ajout_seul_refuser();


--
-- Name: fournisseur trg_fournisseur_dette; Type: TRIGGER; Schema: public; Owner: -
--

CREATE TRIGGER trg_fournisseur_dette AFTER INSERT OR UPDATE OF solde ON public.fournisseur FOR EACH ROW EXECUTE FUNCTION public.tracer_dette_fournisseur();


--
-- Name: fournisseur_mouvement trg_fournisseur_mouvement_immuable; Type: TRIGGER; Schema: public; Owner: -
--

CREATE TRIGGER trg_fournisseur_mouvement_immuable BEFORE DELETE OR UPDATE ON public.fournisseur_mouvement FOR EACH ROW EXECUTE FUNCTION public.proteger_fournisseur_mouvement();


--
-- Name: fournisseur_mouvement trg_fournisseur_mouvement_non_vidable; Type: TRIGGER; Schema: public; Owner: -
--

CREATE TRIGGER trg_fournisseur_mouvement_non_vidable BEFORE TRUNCATE ON public.fournisseur_mouvement FOR EACH STATEMENT EXECUTE FUNCTION public.interdire_vidage();


--
-- Name: utilisateur trg_garder_un_titulaire; Type: TRIGGER; Schema: public; Owner: -
--

CREATE CONSTRAINT TRIGGER trg_garder_un_titulaire AFTER DELETE OR UPDATE ON public.utilisateur DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION public.garder_un_titulaire();


--
-- Name: historique_chifa trg_historique_chifa_lecture_seule; Type: TRIGGER; Schema: public; Owner: -
--

CREATE TRIGGER trg_historique_chifa_lecture_seule BEFORE DELETE OR UPDATE ON public.historique_chifa FOR EACH ROW EXECUTE FUNCTION public.historique_chifa_lecture_seule();


--
-- Name: facture_client trg_historique_facture; Type: TRIGGER; Schema: public; Owner: -
--

CREATE TRIGGER trg_historique_facture BEFORE DELETE OR UPDATE ON public.facture_client FOR EACH ROW EXECUTE FUNCTION public.proteger_historique_facture();


--
-- Name: facture_client_ligne trg_historique_facture_ligne; Type: TRIGGER; Schema: public; Owner: -
--

CREATE TRIGGER trg_historique_facture_ligne BEFORE DELETE OR UPDATE ON public.facture_client_ligne FOR EACH ROW EXECUTE FUNCTION public.proteger_historique_facture();


--
-- Name: facture_client_version trg_historique_facture_version; Type: TRIGGER; Schema: public; Owner: -
--

CREATE TRIGGER trg_historique_facture_version BEFORE DELETE OR UPDATE ON public.facture_client_version FOR EACH ROW EXECUTE FUNCTION public.proteger_historique_facture();


--
-- Name: integrite_essai trg_integrite_immuable; Type: TRIGGER; Schema: public; Owner: -
--

CREATE TRIGGER trg_integrite_immuable BEFORE DELETE OR UPDATE ON public.integrite_essai FOR EACH ROW EXECUTE FUNCTION public.integrite_interdire_modification();


--
-- Name: journal_registre_psychotrope trg_integrite_immuable; Type: TRIGGER; Schema: public; Owner: -
--

CREATE TRIGGER trg_integrite_immuable BEFORE DELETE OR UPDATE ON public.journal_registre_psychotrope FOR EACH ROW EXECUTE FUNCTION public.integrite_interdire_modification();


--
-- Name: journal_vente trg_integrite_immuable; Type: TRIGGER; Schema: public; Owner: -
--

CREATE TRIGGER trg_integrite_immuable BEFORE DELETE OR UPDATE ON public.journal_vente FOR EACH ROW EXECUTE FUNCTION public.integrite_interdire_modification();


--
-- Name: integrite_essai trg_integrite_non_vidable; Type: TRIGGER; Schema: public; Owner: -
--

CREATE TRIGGER trg_integrite_non_vidable BEFORE TRUNCATE ON public.integrite_essai FOR EACH STATEMENT EXECUTE FUNCTION public.integrite_interdire_modification();


--
-- Name: journal_registre_psychotrope trg_integrite_non_vidable; Type: TRIGGER; Schema: public; Owner: -
--

CREATE TRIGGER trg_integrite_non_vidable BEFORE TRUNCATE ON public.journal_registre_psychotrope FOR EACH STATEMENT EXECUTE FUNCTION public.integrite_interdire_modification();


--
-- Name: journal_vente trg_integrite_non_vidable; Type: TRIGGER; Schema: public; Owner: -
--

CREATE TRIGGER trg_integrite_non_vidable BEFORE TRUNCATE ON public.journal_vente FOR EACH STATEMENT EXECUTE FUNCTION public.integrite_interdire_modification();


--
-- Name: integrite_prise_acte trg_integrite_prise_acte_ajout_seul; Type: TRIGGER; Schema: public; Owner: -
--

CREATE TRIGGER trg_integrite_prise_acte_ajout_seul BEFORE DELETE OR UPDATE ON public.integrite_prise_acte FOR EACH ROW EXECUTE FUNCTION public.integrite_ajout_seul_restauration();


--
-- Name: journal_audit trg_journal_immuable; Type: TRIGGER; Schema: public; Owner: -
--

CREATE TRIGGER trg_journal_immuable BEFORE DELETE OR UPDATE ON public.journal_audit FOR EACH ROW EXECUTE FUNCTION public.interdire_modification_journal();


--
-- Name: journal_audit trg_journal_non_vidable; Type: TRIGGER; Schema: public; Owner: -
--

CREATE TRIGGER trg_journal_non_vidable BEFORE TRUNCATE ON public.journal_audit FOR EACH STATEMENT EXECUTE FUNCTION public.interdire_vidage();


--
-- Name: journal_sauvegarde trg_journal_sauvegarde_immuable; Type: TRIGGER; Schema: public; Owner: -
--

CREATE TRIGGER trg_journal_sauvegarde_immuable BEFORE DELETE OR UPDATE ON public.journal_sauvegarde FOR EACH ROW EXECUTE FUNCTION public.interdire_modification_historique();


--
-- Name: journal_sauvegarde trg_journal_sauvegarde_non_vidable; Type: TRIGGER; Schema: public; Owner: -
--

CREATE TRIGGER trg_journal_sauvegarde_non_vidable BEFORE TRUNCATE ON public.journal_sauvegarde FOR EACH STATEMENT EXECUTE FUNCTION public.interdire_vidage();


--
-- Name: avoir_mouvement trg_journal_vente_avoir; Type: TRIGGER; Schema: public; Owner: -
--

CREATE CONSTRAINT TRIGGER trg_journal_vente_avoir AFTER INSERT ON public.avoir_mouvement DEFERRABLE INITIALLY DEFERRED FOR EACH ROW WHEN ((new.origine = ANY (ARRAY['saisie'::text, 'trop_paye_regularisation'::text, 'remboursement'::text]))) EXECUTE FUNCTION public.journal_vente_depuis_avoir();


--
-- Name: vente trg_journal_vente_insertion; Type: TRIGGER; Schema: public; Owner: -
--

CREATE CONSTRAINT TRIGGER trg_journal_vente_insertion AFTER INSERT ON public.vente DEFERRABLE INITIALLY DEFERRED FOR EACH ROW WHEN ((new.statut <> 'en_cours'::text)) EXECUTE FUNCTION public.journal_vente_depuis_vente();


--
-- Name: regularisation_part_organisme trg_journal_vente_regularisation; Type: TRIGGER; Schema: public; Owner: -
--

CREATE CONSTRAINT TRIGGER trg_journal_vente_regularisation AFTER INSERT ON public.regularisation_part_organisme DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION public.journal_vente_depuis_regularisation();


--
-- Name: regularisation_part_organisme trg_journal_vente_regularisation_annulee; Type: TRIGGER; Schema: public; Owner: -
--

CREATE CONSTRAINT TRIGGER trg_journal_vente_regularisation_annulee AFTER UPDATE OF statut ON public.regularisation_part_organisme DEFERRABLE INITIALLY DEFERRED FOR EACH ROW WHEN ((old.statut IS DISTINCT FROM new.statut)) EXECUTE FUNCTION public.journal_vente_depuis_regularisation();


--
-- Name: retour_client trg_journal_vente_retour; Type: TRIGGER; Schema: public; Owner: -
--

CREATE CONSTRAINT TRIGGER trg_journal_vente_retour AFTER INSERT ON public.retour_client DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION public.journal_vente_depuis_retour();


--
-- Name: vente trg_journal_vente_statut; Type: TRIGGER; Schema: public; Owner: -
--

CREATE CONSTRAINT TRIGGER trg_journal_vente_statut AFTER UPDATE OF statut ON public.vente DEFERRABLE INITIALLY DEFERRED FOR EACH ROW WHEN ((old.statut IS DISTINCT FROM new.statut)) EXECUTE FUNCTION public.journal_vente_depuis_vente();


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
-- Name: patient trg_patient_credit; Type: TRIGGER; Schema: public; Owner: -
--

CREATE TRIGGER trg_patient_credit AFTER INSERT OR UPDATE OF solde_credit ON public.patient FOR EACH ROW EXECUTE FUNCTION public.tracer_credit_patient();


--
-- Name: role_permission trg_proteger_role_systeme; Type: TRIGGER; Schema: public; Owner: -
--

CREATE TRIGGER trg_proteger_role_systeme BEFORE INSERT OR DELETE ON public.role_permission FOR EACH ROW EXECUTE FUNCTION public.proteger_role_systeme();


--
-- Name: registre_psychotrope trg_registre_immuable; Type: TRIGGER; Schema: public; Owner: -
--

CREATE TRIGGER trg_registre_immuable BEFORE DELETE OR UPDATE ON public.registre_psychotrope FOR EACH ROW EXECUTE FUNCTION public.interdire_modification();


--
-- Name: registre_psychotrope trg_registre_non_vidable; Type: TRIGGER; Schema: public; Owner: -
--

CREATE TRIGGER trg_registre_non_vidable BEFORE TRUNCATE ON public.registre_psychotrope FOR EACH STATEMENT EXECUTE FUNCTION public.interdire_vidage();


--
-- Name: registre_psychotrope trg_registre_numero; Type: TRIGGER; Schema: public; Owner: -
--

CREATE TRIGGER trg_registre_numero BEFORE INSERT ON public.registre_psychotrope FOR EACH ROW EXECUTE FUNCTION public.numeroter_registre();


--
-- Name: registre_psychotrope trg_registre_sans_doublon; Type: TRIGGER; Schema: public; Owner: -
--

CREATE TRIGGER trg_registre_sans_doublon BEFORE INSERT ON public.registre_psychotrope FOR EACH ROW EXECUTE FUNCTION public.eviter_doublon_registre();


--
-- Name: regularisation_part_organisme trg_regularisation_part_organisme_immuable; Type: TRIGGER; Schema: public; Owner: -
--

CREATE TRIGGER trg_regularisation_part_organisme_immuable BEFORE DELETE OR UPDATE ON public.regularisation_part_organisme FOR EACH ROW EXECUTE FUNCTION public.proteger_regularisation_part_organisme();


--
-- Name: regularisation_part_organisme trg_regularisation_rendue_avant; Type: TRIGGER; Schema: public; Owner: -
--

CREATE TRIGGER trg_regularisation_rendue_avant BEFORE INSERT ON public.regularisation_part_organisme FOR EACH ROW EXECUTE FUNCTION public.verifier_regularisation_rendue_avant();


--
-- Name: ligne_retour_fournisseur trg_retour_bon_fournisseur; Type: TRIGGER; Schema: public; Owner: -
--

CREATE TRIGGER trg_retour_bon_fournisseur BEFORE INSERT OR UPDATE ON public.ligne_retour_fournisseur FOR EACH ROW EXECUTE FUNCTION public.verifier_retour_bon_fournisseur();


--
-- Name: retour_client trg_retour_client_dette_avoir; Type: TRIGGER; Schema: public; Owner: -
--

CREATE TRIGGER trg_retour_client_dette_avoir BEFORE INSERT OR UPDATE OF dette_avoir_retenu ON public.retour_client FOR EACH ROW EXECUTE FUNCTION public.verifier_retour_dette_avoir();


--
-- Name: retour_client_ligne trg_retour_client_ligne_avoir_retenu; Type: TRIGGER; Schema: public; Owner: -
--

CREATE TRIGGER trg_retour_client_ligne_avoir_retenu BEFORE INSERT OR UPDATE OF montant_avoir_retenu ON public.retour_client_ligne FOR EACH ROW EXECUTE FUNCTION public.verifier_retour_avoir_retenu();


--
-- Name: retour_client_ligne trg_retour_client_ligne_organisme_depose; Type: TRIGGER; Schema: public; Owner: -
--

CREATE TRIGGER trg_retour_client_ligne_organisme_depose BEFORE INSERT OR UPDATE OF montant_organisme ON public.retour_client_ligne FOR EACH ROW EXECUTE FUNCTION public.refuser_retour_organisme_depose();


--
-- Name: retour_client_ligne trg_retour_client_ligne_prestation; Type: TRIGGER; Schema: public; Owner: -
--

CREATE TRIGGER trg_retour_client_ligne_prestation BEFORE INSERT ON public.retour_client_ligne FOR EACH ROW EXECUTE FUNCTION public.refuser_retour_prestation();


--
-- Name: retour_client_ligne trg_retour_client_ligne_regularisation; Type: TRIGGER; Schema: public; Owner: -
--

CREATE TRIGGER trg_retour_client_ligne_regularisation BEFORE INSERT OR UPDATE OF montant_regularisation ON public.retour_client_ligne FOR EACH ROW EXECUTE FUNCTION public.verifier_retour_regularisation();


--
-- Name: utilisateur trg_revoquer_connexions; Type: TRIGGER; Schema: public; Owner: -
--

CREATE TRIGGER trg_revoquer_connexions BEFORE UPDATE ON public.utilisateur FOR EACH ROW EXECUTE FUNCTION public.revoquer_connexions_utilisateur();


--
-- Name: role trg_role_systeme; Type: TRIGGER; Schema: public; Owner: -
--

CREATE TRIGGER trg_role_systeme BEFORE DELETE OR UPDATE ON public.role FOR EACH ROW EXECUTE FUNCTION public.proteger_ligne_role_systeme();


--
-- Name: sceau_integrite trg_sceau_integrite_immuable; Type: TRIGGER; Schema: public; Owner: -
--

CREATE TRIGGER trg_sceau_integrite_immuable BEFORE DELETE OR UPDATE ON public.sceau_integrite FOR EACH ROW EXECUTE FUNCTION public.integrite_interdire_modification_trace();


--
-- Name: sceau_integrite trg_sceau_integrite_non_vidable; Type: TRIGGER; Schema: public; Owner: -
--

CREATE TRIGGER trg_sceau_integrite_non_vidable BEFORE TRUNCATE ON public.sceau_integrite FOR EACH STATEMENT EXECUTE FUNCTION public.integrite_interdire_modification_trace();


--
-- Name: serveur_ecarte trg_serveur_ecarte_ajout_seul; Type: TRIGGER; Schema: public; Owner: -
--

CREATE TRIGGER trg_serveur_ecarte_ajout_seul BEFORE DELETE OR UPDATE ON public.serveur_ecarte FOR EACH ROW EXECUTE FUNCTION public.serveur_ecarte_proteger();


--
-- Name: serveur_ecarte trg_serveur_ecarte_non_vidable; Type: TRIGGER; Schema: public; Owner: -
--

CREATE TRIGGER trg_serveur_ecarte_non_vidable BEFORE TRUNCATE ON public.serveur_ecarte FOR EACH STATEMENT EXECUTE FUNCTION public.serveur_ecarte_proteger();


--
-- Name: session_caisse trg_session_fermee; Type: TRIGGER; Schema: public; Owner: -
--

CREATE TRIGGER trg_session_fermee BEFORE DELETE OR UPDATE ON public.session_caisse FOR EACH ROW EXECUTE FUNCTION public.proteger_session_fermee();


--
-- Name: sync_lot trg_sync_lot_garde; Type: TRIGGER; Schema: public; Owner: -
--

CREATE TRIGGER trg_sync_lot_garde BEFORE DELETE OR UPDATE ON public.sync_lot FOR EACH ROW EXECUTE FUNCTION public.sync_lot_garde();


--
-- Name: sync_lot trg_sync_lot_non_vidable; Type: TRIGGER; Schema: public; Owner: -
--

CREATE TRIGGER trg_sync_lot_non_vidable BEFORE TRUNCATE ON public.sync_lot FOR EACH STATEMENT EXECUTE FUNCTION public.sync_lot_garde();


--
-- Name: sync_parametre trg_sync_parametre_garde_officine; Type: TRIGGER; Schema: public; Owner: -
--

CREATE TRIGGER trg_sync_parametre_garde_officine BEFORE UPDATE ON public.sync_parametre FOR EACH ROW EXECUTE FUNCTION public.sync_parametre_garde_officine();


--
-- Name: sync_sortie trg_sync_sortie_garde; Type: TRIGGER; Schema: public; Owner: -
--

CREATE TRIGGER trg_sync_sortie_garde BEFORE DELETE OR UPDATE ON public.sync_sortie FOR EACH ROW EXECUTE FUNCTION public.sync_sortie_garde();


--
-- Name: sync_sortie trg_sync_sortie_non_vidable; Type: TRIGGER; Schema: public; Owner: -
--

CREATE TRIGGER trg_sync_sortie_non_vidable BEFORE TRUNCATE ON public.sync_sortie FOR EACH STATEMENT EXECUTE FUNCTION public.sync_sortie_garde();


--
-- Name: vente trg_tracabilite_vente; Type: TRIGGER; Schema: public; Owner: -
--

CREATE CONSTRAINT TRIGGER trg_tracabilite_vente AFTER INSERT OR UPDATE OF statut ON public.vente DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION public.verifier_tracabilite_vente();


--
-- Name: verification_integrite trg_verification_integrite_immuable; Type: TRIGGER; Schema: public; Owner: -
--

CREATE TRIGGER trg_verification_integrite_immuable BEFORE DELETE OR UPDATE ON public.verification_integrite FOR EACH ROW EXECUTE FUNCTION public.integrite_interdire_modification_trace();


--
-- Name: verification_integrite trg_verification_integrite_non_vidable; Type: TRIGGER; Schema: public; Owner: -
--

CREATE TRIGGER trg_verification_integrite_non_vidable BEFORE TRUNCATE ON public.verification_integrite FOR EACH STATEMENT EXECUTE FUNCTION public.integrite_interdire_modification_trace();


--
-- Name: produit trg_zones_produit; Type: TRIGGER; Schema: public; Owner: -
--

CREATE TRIGGER trg_zones_produit BEFORE INSERT OR UPDATE OF emplacement_id, reserve_id ON public.produit FOR EACH ROW EXECUTE FUNCTION public.verifier_zones_produit();


--
-- Name: annulation_registre zz_annulation_registre_journal; Type: TRIGGER; Schema: public; Owner: -
--

CREATE TRIGGER zz_annulation_registre_journal AFTER INSERT ON public.annulation_registre FOR EACH ROW EXECUTE FUNCTION public.annulation_registre_vers_journal();


--
-- Name: integrite_essai zz_integrite_chainer; Type: TRIGGER; Schema: public; Owner: -
--

CREATE TRIGGER zz_integrite_chainer BEFORE INSERT ON public.integrite_essai FOR EACH ROW EXECUTE FUNCTION public.integrite_chainer();


--
-- Name: journal_registre_psychotrope zz_integrite_chainer; Type: TRIGGER; Schema: public; Owner: -
--

CREATE TRIGGER zz_integrite_chainer BEFORE INSERT ON public.journal_registre_psychotrope FOR EACH ROW EXECUTE FUNCTION public.integrite_chainer();


--
-- Name: journal_vente zz_integrite_chainer; Type: TRIGGER; Schema: public; Owner: -
--

CREATE TRIGGER zz_integrite_chainer BEFORE INSERT ON public.journal_vente FOR EACH ROW EXECUTE FUNCTION public.integrite_chainer();


--
-- Name: registre_psychotrope zz_registre_journal_chaine; Type: TRIGGER; Schema: public; Owner: -
--

CREATE TRIGGER zz_registre_journal_chaine AFTER INSERT ON public.registre_psychotrope FOR EACH ROW EXECUTE FUNCTION public.registre_vers_journal();


--
-- Name: motif_reglable zz_reglage_garde; Type: TRIGGER; Schema: public; Owner: -
--

CREATE TRIGGER zz_reglage_garde BEFORE INSERT OR DELETE OR UPDATE ON public.motif_reglable FOR EACH ROW EXECUTE FUNCTION public.reglage_garde();


--
-- Name: parametre_alerte zz_reglage_garde; Type: TRIGGER; Schema: public; Owner: -
--

CREATE TRIGGER zz_reglage_garde BEFORE INSERT OR DELETE OR UPDATE ON public.parametre_alerte FOR EACH ROW EXECUTE FUNCTION public.reglage_garde();


--
-- Name: parametre_fiscal zz_reglage_garde; Type: TRIGGER; Schema: public; Owner: -
--

CREATE TRIGGER zz_reglage_garde BEFORE INSERT OR DELETE OR UPDATE ON public.parametre_fiscal FOR EACH ROW EXECUTE FUNCTION public.reglage_garde();


--
-- Name: parametre_formulaire_sms zz_reglage_garde; Type: TRIGGER; Schema: public; Owner: -
--

CREATE TRIGGER zz_reglage_garde BEFORE INSERT OR DELETE OR UPDATE ON public.parametre_formulaire_sms FOR EACH ROW EXECUTE FUNCTION public.reglage_garde();


--
-- Name: parametre_inventaire zz_reglage_garde; Type: TRIGGER; Schema: public; Owner: -
--

CREATE TRIGGER zz_reglage_garde BEFORE INSERT OR DELETE OR UPDATE ON public.parametre_inventaire FOR EACH ROW EXECUTE FUNCTION public.reglage_garde();


--
-- Name: parametre_serialisation zz_reglage_garde; Type: TRIGGER; Schema: public; Owner: -
--

CREATE TRIGGER zz_reglage_garde BEFORE INSERT OR DELETE OR UPDATE ON public.parametre_serialisation FOR EACH ROW EXECUTE FUNCTION public.reglage_garde();


--
-- Name: parametre_substitution zz_reglage_garde; Type: TRIGGER; Schema: public; Owner: -
--

CREATE TRIGGER zz_reglage_garde BEFORE INSERT OR DELETE OR UPDATE ON public.parametre_substitution FOR EACH ROW EXECUTE FUNCTION public.reglage_garde();


--
-- Name: parametre_surveillance zz_reglage_garde; Type: TRIGGER; Schema: public; Owner: -
--

CREATE TRIGGER zz_reglage_garde BEFORE INSERT OR DELETE OR UPDATE ON public.parametre_surveillance FOR EACH ROW EXECUTE FUNCTION public.reglage_garde();


--
-- Name: parametre_tracabilite zz_reglage_garde; Type: TRIGGER; Schema: public; Owner: -
--

CREATE TRIGGER zz_reglage_garde BEFORE INSERT OR DELETE OR UPDATE ON public.parametre_tracabilite FOR EACH ROW EXECUTE FUNCTION public.reglage_garde();


--
-- Name: regle_alerte_psychotrope zz_reglage_garde; Type: TRIGGER; Schema: public; Owner: -
--

CREATE TRIGGER zz_reglage_garde BEFORE INSERT OR DELETE OR UPDATE ON public.regle_alerte_psychotrope FOR EACH ROW EXECUTE FUNCTION public.reglage_garde();


--
-- Name: remise_automatique zz_reglage_garde; Type: TRIGGER; Schema: public; Owner: -
--

CREATE TRIGGER zz_reglage_garde BEFORE INSERT OR DELETE OR UPDATE ON public.remise_automatique FOR EACH ROW EXECUTE FUNCTION public.reglage_garde();


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
-- Name: doublon_patient doublon_patient_decide_par_fkey; Type: FK CONSTRAINT; Schema: import; Owner: -
--

ALTER TABLE ONLY import.doublon_patient
    ADD CONSTRAINT doublon_patient_decide_par_fkey FOREIGN KEY (decide_par) REFERENCES public.utilisateur(id);


--
-- Name: doublon_patient doublon_patient_patient_cree_id_fkey; Type: FK CONSTRAINT; Schema: import; Owner: -
--

ALTER TABLE ONLY import.doublon_patient
    ADD CONSTRAINT doublon_patient_patient_cree_id_fkey FOREIGN KEY (patient_cree_id) REFERENCES public.patient(id);


--
-- Name: doublon_patient doublon_patient_patient_existant_id_fkey; Type: FK CONSTRAINT; Schema: import; Owner: -
--

ALTER TABLE ONLY import.doublon_patient
    ADD CONSTRAINT doublon_patient_patient_existant_id_fkey FOREIGN KEY (patient_existant_id) REFERENCES public.patient(id);


--
-- Name: doublon_patient doublon_patient_session_id_fkey; Type: FK CONSTRAINT; Schema: import; Owner: -
--

ALTER TABLE ONLY import.doublon_patient
    ADD CONSTRAINT doublon_patient_session_id_fkey FOREIGN KEY (session_id) REFERENCES import.session(id);


--
-- Name: fournisseur_src fournisseur_src_session_id_fkey; Type: FK CONSTRAINT; Schema: import; Owner: -
--

ALTER TABLE ONLY import.fournisseur_src
    ADD CONSTRAINT fournisseur_src_session_id_fkey FOREIGN KEY (session_id) REFERENCES import.session(id);


--
-- Name: historique_chifa_src historique_chifa_src_session_id_fkey; Type: FK CONSTRAINT; Schema: import; Owner: -
--

ALTER TABLE ONLY import.historique_chifa_src
    ADD CONSTRAINT historique_chifa_src_session_id_fkey FOREIGN KEY (session_id) REFERENCES import.session(id);


--
-- Name: historique_mdn_src historique_mdn_src_session_id_fkey; Type: FK CONSTRAINT; Schema: import; Owner: -
--

ALTER TABLE ONLY import.historique_mdn_src
    ADD CONSTRAINT historique_mdn_src_session_id_fkey FOREIGN KEY (session_id) REFERENCES import.session(id);


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
-- Name: alerte_non_envoyee alerte_non_envoyee_vue_par_fkey; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.alerte_non_envoyee
    ADD CONSTRAINT alerte_non_envoyee_vue_par_fkey FOREIGN KEY (vue_par) REFERENCES public.utilisateur(id);


--
-- Name: alerte alerte_produit_id_fkey; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.alerte
    ADD CONSTRAINT alerte_produit_id_fkey FOREIGN KEY (produit_id) REFERENCES public.produit(id);


--
-- Name: alerte_surveillance alerte_surveillance_traitee_par_fkey; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.alerte_surveillance
    ADD CONSTRAINT alerte_surveillance_traitee_par_fkey FOREIGN KEY (traitee_par) REFERENCES public.utilisateur(id);


--
-- Name: alerte_surveillance alerte_surveillance_utilisateur_id_fkey; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.alerte_surveillance
    ADD CONSTRAINT alerte_surveillance_utilisateur_id_fkey FOREIGN KEY (utilisateur_id) REFERENCES public.utilisateur(id);


--
-- Name: alerte alerte_traitee_par_fkey; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.alerte
    ADD CONSTRAINT alerte_traitee_par_fkey FOREIGN KEY (traitee_par) REFERENCES public.utilisateur(id);


--
-- Name: annulation_registre annulation_registre_registre_id_fkey; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.annulation_registre
    ADD CONSTRAINT annulation_registre_registre_id_fkey FOREIGN KEY (registre_id) REFERENCES public.registre_psychotrope(id);


--
-- Name: annulation_registre annulation_registre_utilisateur_id_fkey; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.annulation_registre
    ADD CONSTRAINT annulation_registre_utilisateur_id_fkey FOREIGN KEY (utilisateur_id) REFERENCES public.utilisateur(id);


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
-- Name: avoir_mouvement avoir_mouvement_mouvement_tiroir_id_fkey; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.avoir_mouvement
    ADD CONSTRAINT avoir_mouvement_mouvement_tiroir_id_fkey FOREIGN KEY (mouvement_tiroir_id) REFERENCES public.mouvement_tiroir(id);


--
-- Name: avoir_mouvement avoir_mouvement_paiement_id_fkey; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.avoir_mouvement
    ADD CONSTRAINT avoir_mouvement_paiement_id_fkey FOREIGN KEY (paiement_id) REFERENCES public.paiement(id);


--
-- Name: avoir_mouvement avoir_mouvement_patient_id_fkey; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.avoir_mouvement
    ADD CONSTRAINT avoir_mouvement_patient_id_fkey FOREIGN KEY (patient_id) REFERENCES public.patient(id);


--
-- Name: avoir_mouvement avoir_mouvement_utilisateur_id_fkey; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.avoir_mouvement
    ADD CONSTRAINT avoir_mouvement_utilisateur_id_fkey FOREIGN KEY (utilisateur_id) REFERENCES public.utilisateur(id);


--
-- Name: avoir_mouvement avoir_mouvement_vente_id_fkey; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.avoir_mouvement
    ADD CONSTRAINT avoir_mouvement_vente_id_fkey FOREIGN KEY (vente_id) REFERENCES public.vente(id);


--
-- Name: bascule_saut bascule_saut_promotion_id_fkey; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.bascule_saut
    ADD CONSTRAINT bascule_saut_promotion_id_fkey FOREIGN KEY (promotion_id) REFERENCES public.bascule_promotion(id);


--
-- Name: boite_evenement boite_evenement_boite_id_fkey; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.boite_evenement
    ADD CONSTRAINT boite_evenement_boite_id_fkey FOREIGN KEY (boite_id) REFERENCES public.boite(id);


--
-- Name: boite_evenement boite_evenement_inventaire_id_fkey; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.boite_evenement
    ADD CONSTRAINT boite_evenement_inventaire_id_fkey FOREIGN KEY (inventaire_id) REFERENCES public.inventaire(id);


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
-- Name: bordereau bordereau_centre_payeur_id_fkey; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.bordereau
    ADD CONSTRAINT bordereau_centre_payeur_id_fkey FOREIGN KEY (centre_payeur_id) REFERENCES public.centre_payeur(id);


--
-- Name: bordereau bordereau_cree_par_fkey; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.bordereau
    ADD CONSTRAINT bordereau_cree_par_fkey FOREIGN KEY (cree_par) REFERENCES public.utilisateur(id);


--
-- Name: bordereau bordereau_depose_par_fkey; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.bordereau
    ADD CONSTRAINT bordereau_depose_par_fkey FOREIGN KEY (depose_par) REFERENCES public.utilisateur(id);


--
-- Name: bordereau bordereau_organisme_id_fkey; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.bordereau
    ADD CONSTRAINT bordereau_organisme_id_fkey FOREIGN KEY (organisme_id) REFERENCES public.organisme(id);


--
-- Name: bordereau_vente bordereau_vente_ajoute_par_fkey; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.bordereau_vente
    ADD CONSTRAINT bordereau_vente_ajoute_par_fkey FOREIGN KEY (ajoute_par) REFERENCES public.utilisateur(id);


--
-- Name: bordereau_vente bordereau_vente_bordereau_id_fkey; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.bordereau_vente
    ADD CONSTRAINT bordereau_vente_bordereau_id_fkey FOREIGN KEY (bordereau_id) REFERENCES public.bordereau(id);


--
-- Name: bordereau_vente bordereau_vente_credit_mouvement_id_fkey; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.bordereau_vente
    ADD CONSTRAINT bordereau_vente_credit_mouvement_id_fkey FOREIGN KEY (credit_mouvement_id) REFERENCES public.credit_mouvement(id);


--
-- Name: bordereau_vente bordereau_vente_rejete_par_fkey; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.bordereau_vente
    ADD CONSTRAINT bordereau_vente_rejete_par_fkey FOREIGN KEY (rejete_par) REFERENCES public.utilisateur(id);


--
-- Name: bordereau_vente bordereau_vente_suite_par_fkey; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.bordereau_vente
    ADD CONSTRAINT bordereau_vente_suite_par_fkey FOREIGN KEY (suite_par) REFERENCES public.utilisateur(id);


--
-- Name: bordereau_vente bordereau_vente_vente_id_fkey; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.bordereau_vente
    ADD CONSTRAINT bordereau_vente_vente_id_fkey FOREIGN KEY (vente_id) REFERENCES public.vente(id);


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
-- Name: catalogue_grossiste catalogue_grossiste_fournisseur_id_fkey; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.catalogue_grossiste
    ADD CONSTRAINT catalogue_grossiste_fournisseur_id_fkey FOREIGN KEY (fournisseur_id) REFERENCES public.fournisseur(id);


--
-- Name: catalogue_grossiste catalogue_grossiste_import_id_fkey; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.catalogue_grossiste
    ADD CONSTRAINT catalogue_grossiste_import_id_fkey FOREIGN KEY (import_id) REFERENCES public.import_catalogue_grossiste(id);


--
-- Name: catalogue_grossiste catalogue_grossiste_produit_id_fkey; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.catalogue_grossiste
    ADD CONSTRAINT catalogue_grossiste_produit_id_fkey FOREIGN KEY (produit_id) REFERENCES public.produit(id);


--
-- Name: centre_payeur centre_payeur_organisme_id_fkey; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.centre_payeur
    ADD CONSTRAINT centre_payeur_organisme_id_fkey FOREIGN KEY (organisme_id) REFERENCES public.organisme(id);


--
-- Name: chifa_code_produit chifa_code_produit_associe_par_fkey; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.chifa_code_produit
    ADD CONSTRAINT chifa_code_produit_associe_par_fkey FOREIGN KEY (associe_par) REFERENCES public.utilisateur(id);


--
-- Name: chifa_code_produit chifa_code_produit_produit_id_fkey; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.chifa_code_produit
    ADD CONSTRAINT chifa_code_produit_produit_id_fkey FOREIGN KEY (produit_id) REFERENCES public.produit(id);


--
-- Name: classe_suivie_dci classe_suivie_dci_ajoute_par_fkey; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.classe_suivie_dci
    ADD CONSTRAINT classe_suivie_dci_ajoute_par_fkey FOREIGN KEY (ajoute_par) REFERENCES public.utilisateur(id);


--
-- Name: classe_suivie_dci classe_suivie_dci_classe_code_fkey; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.classe_suivie_dci
    ADD CONSTRAINT classe_suivie_dci_classe_code_fkey FOREIGN KEY (classe_code) REFERENCES public.classe_suivie(code);


--
-- Name: classe_suivie_dci classe_suivie_dci_dci_id_fkey; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.classe_suivie_dci
    ADD CONSTRAINT classe_suivie_dci_dci_id_fkey FOREIGN KEY (dci_id) REFERENCES public.dci(id);


--
-- Name: cloture_fiscale cloture_fiscale_sceau_id_fkey; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.cloture_fiscale
    ADD CONSTRAINT cloture_fiscale_sceau_id_fkey FOREIGN KEY (sceau_id) REFERENCES public.sceau_integrite(id);


--
-- Name: cloture_fiscale cloture_fiscale_session_caisse_id_fkey; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.cloture_fiscale
    ADD CONSTRAINT cloture_fiscale_session_caisse_id_fkey FOREIGN KEY (session_caisse_id) REFERENCES public.session_caisse(id);


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
-- Name: commande_ordre_central commande_ordre_central_commande_id_fkey; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.commande_ordre_central
    ADD CONSTRAINT commande_ordre_central_commande_id_fkey FOREIGN KEY (commande_id) REFERENCES public.commande_fournisseur(id);


--
-- Name: commande_ordre_central commande_ordre_central_ordre_id_fkey; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.commande_ordre_central
    ADD CONSTRAINT commande_ordre_central_ordre_id_fkey FOREIGN KEY (ordre_id) REFERENCES public.sync_ordre_recu(id);


--
-- Name: comptage_caisse comptage_caisse_session_id_fkey; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.comptage_caisse
    ADD CONSTRAINT comptage_caisse_session_id_fkey FOREIGN KEY (session_id) REFERENCES public.session_caisse(id);


--
-- Name: comptage_intermediaire comptage_intermediaire_session_caisse_id_fkey; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.comptage_intermediaire
    ADD CONSTRAINT comptage_intermediaire_session_caisse_id_fkey FOREIGN KEY (session_caisse_id) REFERENCES public.session_caisse(id);


--
-- Name: comptage_intermediaire comptage_intermediaire_utilisateur_id_fkey; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.comptage_intermediaire
    ADD CONSTRAINT comptage_intermediaire_utilisateur_id_fkey FOREIGN KEY (utilisateur_id) REFERENCES public.utilisateur(id);


--
-- Name: comptage_reception comptage_reception_compte_par_fkey; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.comptage_reception
    ADD CONSTRAINT comptage_reception_compte_par_fkey FOREIGN KEY (compte_par) REFERENCES public.utilisateur(id);


--
-- Name: comptage_reception comptage_reception_decide_par_fkey; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.comptage_reception
    ADD CONSTRAINT comptage_reception_decide_par_fkey FOREIGN KEY (decide_par) REFERENCES public.utilisateur(id);


--
-- Name: comptage_reception comptage_reception_facture_par_fkey; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.comptage_reception
    ADD CONSTRAINT comptage_reception_facture_par_fkey FOREIGN KEY (facture_par) REFERENCES public.utilisateur(id);


--
-- Name: comptage_reception comptage_reception_fournisseur_id_fkey; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.comptage_reception
    ADD CONSTRAINT comptage_reception_fournisseur_id_fkey FOREIGN KEY (fournisseur_id) REFERENCES public.fournisseur(id);


--
-- Name: comptage_reception_ligne comptage_reception_ligne_comptage_id_fkey; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.comptage_reception_ligne
    ADD CONSTRAINT comptage_reception_ligne_comptage_id_fkey FOREIGN KEY (comptage_id) REFERENCES public.comptage_reception(id) ON DELETE CASCADE;


--
-- Name: comptage_reception_ligne comptage_reception_ligne_produit_id_fkey; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.comptage_reception_ligne
    ADD CONSTRAINT comptage_reception_ligne_produit_id_fkey FOREIGN KEY (produit_id) REFERENCES public.produit(id);


--
-- Name: comptage_reception comptage_reception_reception_id_fkey; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.comptage_reception
    ADD CONSTRAINT comptage_reception_reception_id_fkey FOREIGN KEY (reception_id) REFERENCES public.reception(id);


--
-- Name: compte_carte_periode compte_carte_periode_compte_id_fkey; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.compte_carte_periode
    ADD CONSTRAINT compte_carte_periode_compte_id_fkey FOREIGN KEY (compte_id) REFERENCES public.compte_tresorerie(id);


--
-- Name: connexion_employe connexion_employe_utilisateur_id_fkey; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.connexion_employe
    ADD CONSTRAINT connexion_employe_utilisateur_id_fkey FOREIGN KEY (utilisateur_id) REFERENCES public.utilisateur(id);


--
-- Name: correction_lot correction_lot_lot_id_fkey; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.correction_lot
    ADD CONSTRAINT correction_lot_lot_id_fkey FOREIGN KEY (lot_id) REFERENCES public.lot(id);


--
-- Name: correction_lot correction_lot_mouvement_id_fkey; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.correction_lot
    ADD CONSTRAINT correction_lot_mouvement_id_fkey FOREIGN KEY (mouvement_id) REFERENCES public.mouvement_stock(id);


--
-- Name: correction_lot correction_lot_utilisateur_id_fkey; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.correction_lot
    ADD CONSTRAINT correction_lot_utilisateur_id_fkey FOREIGN KEY (utilisateur_id) REFERENCES public.utilisateur(id);


--
-- Name: correspondance_echeancier correspondance_echeancier_compte_id_fkey; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.correspondance_echeancier
    ADD CONSTRAINT correspondance_echeancier_compte_id_fkey FOREIGN KEY (compte_id) REFERENCES public.compte_tresorerie(id);


--
-- Name: correspondance_echeancier_preteur correspondance_echeancier_preteur_utilisateur_id_fkey; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.correspondance_echeancier_preteur
    ADD CONSTRAINT correspondance_echeancier_preteur_utilisateur_id_fkey FOREIGN KEY (utilisateur_id) REFERENCES public.utilisateur(id);


--
-- Name: correspondance_echeancier correspondance_echeancier_utilisateur_id_fkey; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.correspondance_echeancier
    ADD CONSTRAINT correspondance_echeancier_utilisateur_id_fkey FOREIGN KEY (utilisateur_id) REFERENCES public.utilisateur(id);


--
-- Name: credit_imputation credit_imputation_credit_mouvement_id_fkey; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.credit_imputation
    ADD CONSTRAINT credit_imputation_credit_mouvement_id_fkey FOREIGN KEY (credit_mouvement_id) REFERENCES public.credit_mouvement(id);


--
-- Name: credit_imputation credit_imputation_vente_id_fkey; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.credit_imputation
    ADD CONSTRAINT credit_imputation_vente_id_fkey FOREIGN KEY (vente_id) REFERENCES public.vente(id);


--
-- Name: credit_mouvement credit_mouvement_patient_id_fkey; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.credit_mouvement
    ADD CONSTRAINT credit_mouvement_patient_id_fkey FOREIGN KEY (patient_id) REFERENCES public.patient(id);


--
-- Name: credit_mouvement credit_mouvement_utilisateur_id_fkey; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.credit_mouvement
    ADD CONSTRAINT credit_mouvement_utilisateur_id_fkey FOREIGN KEY (utilisateur_id) REFERENCES public.utilisateur(id);


--
-- Name: credit_mouvement credit_mouvement_vente_id_fkey; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.credit_mouvement
    ADD CONSTRAINT credit_mouvement_vente_id_fkey FOREIGN KEY (vente_id) REFERENCES public.vente(id);


--
-- Name: credit_ouverture credit_ouverture_patient_id_fkey; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.credit_ouverture
    ADD CONSTRAINT credit_ouverture_patient_id_fkey FOREIGN KEY (patient_id) REFERENCES public.patient(id);


--
-- Name: demande_patient demande_patient_cloture_par_fkey; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.demande_patient
    ADD CONSTRAINT demande_patient_cloture_par_fkey FOREIGN KEY (cloture_par) REFERENCES public.utilisateur(id);


--
-- Name: demande_patient demande_patient_cree_par_fkey; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.demande_patient
    ADD CONSTRAINT demande_patient_cree_par_fkey FOREIGN KEY (cree_par) REFERENCES public.utilisateur(id);


--
-- Name: demande_patient demande_patient_patient_id_fkey; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.demande_patient
    ADD CONSTRAINT demande_patient_patient_id_fkey FOREIGN KEY (patient_id) REFERENCES public.patient(id);


--
-- Name: demande_patient demande_patient_prevenu_par_fkey; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.demande_patient
    ADD CONSTRAINT demande_patient_prevenu_par_fkey FOREIGN KEY (prevenu_par) REFERENCES public.utilisateur(id);


--
-- Name: demande_patient demande_patient_produit_id_fkey; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.demande_patient
    ADD CONSTRAINT demande_patient_produit_id_fkey FOREIGN KEY (produit_id) REFERENCES public.produit(id);


--
-- Name: ecart_caisse_vu ecart_caisse_vu_session_id_fkey; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.ecart_caisse_vu
    ADD CONSTRAINT ecart_caisse_vu_session_id_fkey FOREIGN KEY (session_id) REFERENCES public.session_caisse(id);


--
-- Name: ecart_caisse_vu ecart_caisse_vu_vu_par_fkey; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.ecart_caisse_vu
    ADD CONSTRAINT ecart_caisse_vu_vu_par_fkey FOREIGN KEY (vu_par) REFERENCES public.utilisateur(id);


--
-- Name: echeance_pret echeance_pret_pret_id_fkey; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.echeance_pret
    ADD CONSTRAINT echeance_pret_pret_id_fkey FOREIGN KEY (pret_id) REFERENCES public.pret_bancaire(id);


--
-- Name: evenement_caisse evenement_caisse_lot_id_fkey; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.evenement_caisse
    ADD CONSTRAINT evenement_caisse_lot_id_fkey FOREIGN KEY (lot_id) REFERENCES public.lot(id);


--
-- Name: evenement_caisse evenement_caisse_produit_id_fkey; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.evenement_caisse
    ADD CONSTRAINT evenement_caisse_produit_id_fkey FOREIGN KEY (produit_id) REFERENCES public.produit(id);


--
-- Name: evenement_caisse evenement_caisse_session_caisse_id_fkey; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.evenement_caisse
    ADD CONSTRAINT evenement_caisse_session_caisse_id_fkey FOREIGN KEY (session_caisse_id) REFERENCES public.session_caisse(id);


--
-- Name: evenement_caisse evenement_caisse_utilisateur_id_fkey; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.evenement_caisse
    ADD CONSTRAINT evenement_caisse_utilisateur_id_fkey FOREIGN KEY (utilisateur_id) REFERENCES public.utilisateur(id);


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
-- Name: ligne_vente fk_ligne_vente_type_prestation; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.ligne_vente
    ADD CONSTRAINT fk_ligne_vente_type_prestation FOREIGN KEY (type_prestation_id) REFERENCES public.type_prestation(id);


--
-- Name: medecin fk_medecin_wilaya; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.medecin
    ADD CONSTRAINT fk_medecin_wilaya FOREIGN KEY (wilaya) REFERENCES public.wilaya(code);


--
-- Name: patient fk_patient_assure_principal; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.patient
    ADD CONSTRAINT fk_patient_assure_principal FOREIGN KEY (assure_principal_id) REFERENCES public.patient(id) ON DELETE SET NULL;


--
-- Name: presence_poste fk_presence_poste_poste_autorise; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.presence_poste
    ADD CONSTRAINT fk_presence_poste_poste_autorise FOREIGN KEY (poste_id) REFERENCES public.poste_autorise(id) ON DELETE SET NULL;


--
-- Name: produit fk_produit_categorie; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.produit
    ADD CONSTRAINT fk_produit_categorie FOREIGN KEY (categorie) REFERENCES public.categorie_produit(code);


--
-- Name: retour_client fk_retour_client_chifa_corrige_par; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.retour_client
    ADD CONSTRAINT fk_retour_client_chifa_corrige_par FOREIGN KEY (chifa_corrige_par) REFERENCES public.utilisateur(id);


--
-- Name: sync_sortie fk_sync_sortie_lot; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.sync_sortie
    ADD CONSTRAINT fk_sync_sortie_lot FOREIGN KEY (epoque, lot_numero) REFERENCES public.sync_lot(epoque, numero);


--
-- Name: vente fk_vente_pour_patient; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.vente
    ADD CONSTRAINT fk_vente_pour_patient FOREIGN KEY (pour_patient_id) REFERENCES public.patient(id) ON DELETE SET NULL;


--
-- Name: vente fk_vente_remis_a_patient; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.vente
    ADD CONSTRAINT fk_vente_remis_a_patient FOREIGN KEY (remis_a_patient_id) REFERENCES public.patient(id) ON DELETE SET NULL;


--
-- Name: wilaya fk_wilaya_origine; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.wilaya
    ADD CONSTRAINT fk_wilaya_origine FOREIGN KEY (wilaya_origine) REFERENCES public.wilaya(code);


--
-- Name: forcage_alerte_psychotrope forcage_alerte_psychotrope_medecin_id_fkey; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.forcage_alerte_psychotrope
    ADD CONSTRAINT forcage_alerte_psychotrope_medecin_id_fkey FOREIGN KEY (medecin_id) REFERENCES public.medecin(id);


--
-- Name: forcage_alerte_psychotrope forcage_alerte_psychotrope_patient_id_fkey; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.forcage_alerte_psychotrope
    ADD CONSTRAINT forcage_alerte_psychotrope_patient_id_fkey FOREIGN KEY (patient_id) REFERENCES public.patient(id);


--
-- Name: forcage_alerte_psychotrope forcage_alerte_psychotrope_utilisateur_id_fkey; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.forcage_alerte_psychotrope
    ADD CONSTRAINT forcage_alerte_psychotrope_utilisateur_id_fkey FOREIGN KEY (utilisateur_id) REFERENCES public.utilisateur(id);


--
-- Name: forcage_alerte_psychotrope forcage_alerte_psychotrope_valide_par_fkey; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.forcage_alerte_psychotrope
    ADD CONSTRAINT forcage_alerte_psychotrope_valide_par_fkey FOREIGN KEY (valide_par) REFERENCES public.utilisateur(id);


--
-- Name: fournisseur_mouvement fournisseur_mouvement_fournisseur_id_fkey; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.fournisseur_mouvement
    ADD CONSTRAINT fournisseur_mouvement_fournisseur_id_fkey FOREIGN KEY (fournisseur_id) REFERENCES public.fournisseur(id);


--
-- Name: fournisseur_mouvement fournisseur_mouvement_utilisateur_id_fkey; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.fournisseur_mouvement
    ADD CONSTRAINT fournisseur_mouvement_utilisateur_id_fkey FOREIGN KEY (utilisateur_id) REFERENCES public.utilisateur(id);


--
-- Name: historique_chifa historique_chifa_session_id_fkey; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.historique_chifa
    ADD CONSTRAINT historique_chifa_session_id_fkey FOREIGN KEY (session_id) REFERENCES import.session(id);


--
-- Name: import_catalogue_grossiste import_catalogue_grossiste_fournisseur_id_fkey; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.import_catalogue_grossiste
    ADD CONSTRAINT import_catalogue_grossiste_fournisseur_id_fkey FOREIGN KEY (fournisseur_id) REFERENCES public.fournisseur(id);


--
-- Name: import_catalogue_grossiste import_catalogue_grossiste_utilisateur_id_fkey; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.import_catalogue_grossiste
    ADD CONSTRAINT import_catalogue_grossiste_utilisateur_id_fkey FOREIGN KEY (utilisateur_id) REFERENCES public.utilisateur(id);


--
-- Name: integrite_prise_acte integrite_prise_acte_utilisateur_id_fkey; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.integrite_prise_acte
    ADD CONSTRAINT integrite_prise_acte_utilisateur_id_fkey FOREIGN KEY (utilisateur_id) REFERENCES public.utilisateur(id);


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
-- Name: inventaire inventaire_produit_id_fkey; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.inventaire
    ADD CONSTRAINT inventaire_produit_id_fkey FOREIGN KEY (produit_id) REFERENCES public.produit(id);


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
-- Name: journal_registre_psychotrope journal_registre_psychotrope_registre_id_fkey; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.journal_registre_psychotrope
    ADD CONSTRAINT journal_registre_psychotrope_registre_id_fkey FOREIGN KEY (registre_id) REFERENCES public.registre_psychotrope(id);


--
-- Name: journal_sauvegarde journal_sauvegarde_utilisateur_id_fkey; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.journal_sauvegarde
    ADD CONSTRAINT journal_sauvegarde_utilisateur_id_fkey FOREIGN KEY (utilisateur_id) REFERENCES public.utilisateur(id);


--
-- Name: journal_vente journal_vente_annulation_id_fkey; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.journal_vente
    ADD CONSTRAINT journal_vente_annulation_id_fkey FOREIGN KEY (annulation_id) REFERENCES public.annulation_vente(id);


--
-- Name: journal_vente journal_vente_avoir_mouvement_id_fkey; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.journal_vente
    ADD CONSTRAINT journal_vente_avoir_mouvement_id_fkey FOREIGN KEY (avoir_mouvement_id) REFERENCES public.avoir_mouvement(id);


--
-- Name: journal_vente journal_vente_regularisation_id_fkey; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.journal_vente
    ADD CONSTRAINT journal_vente_regularisation_id_fkey FOREIGN KEY (regularisation_id) REFERENCES public.regularisation_part_organisme(id);


--
-- Name: journal_vente journal_vente_retour_client_id_fkey; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.journal_vente
    ADD CONSTRAINT journal_vente_retour_client_id_fkey FOREIGN KEY (retour_client_id) REFERENCES public.retour_client(id);


--
-- Name: journal_vente journal_vente_session_caisse_id_fkey; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.journal_vente
    ADD CONSTRAINT journal_vente_session_caisse_id_fkey FOREIGN KEY (session_caisse_id) REFERENCES public.session_caisse(id);


--
-- Name: journal_vente journal_vente_utilisateur_id_fkey; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.journal_vente
    ADD CONSTRAINT journal_vente_utilisateur_id_fkey FOREIGN KEY (utilisateur_id) REFERENCES public.utilisateur(id);


--
-- Name: journal_vente journal_vente_vente_id_fkey; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.journal_vente
    ADD CONSTRAINT journal_vente_vente_id_fkey FOREIGN KEY (vente_id) REFERENCES public.vente(id);


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
-- Name: mouvement_tiroir mouvement_tiroir_corrige_id_fkey; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.mouvement_tiroir
    ADD CONSTRAINT mouvement_tiroir_corrige_id_fkey FOREIGN KEY (corrige_id) REFERENCES public.mouvement_tiroir(id);


--
-- Name: mouvement_tiroir mouvement_tiroir_evenement_caisse_id_fkey; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.mouvement_tiroir
    ADD CONSTRAINT mouvement_tiroir_evenement_caisse_id_fkey FOREIGN KEY (evenement_caisse_id) REFERENCES public.evenement_caisse(id);


--
-- Name: mouvement_tiroir mouvement_tiroir_fournisseur_id_fkey; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.mouvement_tiroir
    ADD CONSTRAINT mouvement_tiroir_fournisseur_id_fkey FOREIGN KEY (fournisseur_id) REFERENCES public.fournisseur(id);


--
-- Name: mouvement_tiroir mouvement_tiroir_mouvement_tresorerie_id_fkey; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.mouvement_tiroir
    ADD CONSTRAINT mouvement_tiroir_mouvement_tresorerie_id_fkey FOREIGN KEY (mouvement_tresorerie_id) REFERENCES public.mouvement_tresorerie(id);


--
-- Name: mouvement_tiroir mouvement_tiroir_patient_id_fkey; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.mouvement_tiroir
    ADD CONSTRAINT mouvement_tiroir_patient_id_fkey FOREIGN KEY (patient_id) REFERENCES public.patient(id);


--
-- Name: mouvement_tiroir mouvement_tiroir_reglement_fournisseur_id_fkey; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.mouvement_tiroir
    ADD CONSTRAINT mouvement_tiroir_reglement_fournisseur_id_fkey FOREIGN KEY (reglement_fournisseur_id) REFERENCES public.reglement_fournisseur(id);


--
-- Name: mouvement_tiroir mouvement_tiroir_regularisation_id_fkey; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.mouvement_tiroir
    ADD CONSTRAINT mouvement_tiroir_regularisation_id_fkey FOREIGN KEY (regularisation_id) REFERENCES public.regularisation_part_organisme(id);


--
-- Name: mouvement_tiroir mouvement_tiroir_session_caisse_id_fkey; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.mouvement_tiroir
    ADD CONSTRAINT mouvement_tiroir_session_caisse_id_fkey FOREIGN KEY (session_caisse_id) REFERENCES public.session_caisse(id);


--
-- Name: mouvement_tiroir mouvement_tiroir_utilisateur_id_fkey; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.mouvement_tiroir
    ADD CONSTRAINT mouvement_tiroir_utilisateur_id_fkey FOREIGN KEY (utilisateur_id) REFERENCES public.utilisateur(id);


--
-- Name: mouvement_tiroir mouvement_tiroir_valide_par_fkey; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.mouvement_tiroir
    ADD CONSTRAINT mouvement_tiroir_valide_par_fkey FOREIGN KEY (valide_par) REFERENCES public.utilisateur(id);


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
-- Name: paiement_echeance_pret paiement_echeance_pret_compte_id_fkey; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.paiement_echeance_pret
    ADD CONSTRAINT paiement_echeance_pret_compte_id_fkey FOREIGN KEY (compte_id) REFERENCES public.compte_tresorerie(id);


--
-- Name: paiement_echeance_pret paiement_echeance_pret_echeance_id_fkey; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.paiement_echeance_pret
    ADD CONSTRAINT paiement_echeance_pret_echeance_id_fkey FOREIGN KEY (echeance_id) REFERENCES public.echeance_pret(id);


--
-- Name: paiement_echeance_pret paiement_echeance_pret_mouvement_capital_id_fkey; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.paiement_echeance_pret
    ADD CONSTRAINT paiement_echeance_pret_mouvement_capital_id_fkey FOREIGN KEY (mouvement_capital_id) REFERENCES public.mouvement_tresorerie(id);


--
-- Name: paiement_echeance_pret paiement_echeance_pret_mouvement_charges_id_fkey; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.paiement_echeance_pret
    ADD CONSTRAINT paiement_echeance_pret_mouvement_charges_id_fkey FOREIGN KEY (mouvement_charges_id) REFERENCES public.mouvement_tresorerie(id);


--
-- Name: paiement_echeance_pret paiement_echeance_pret_utilisateur_id_fkey; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.paiement_echeance_pret
    ADD CONSTRAINT paiement_echeance_pret_utilisateur_id_fkey FOREIGN KEY (utilisateur_id) REFERENCES public.utilisateur(id);


--
-- Name: paiement_echeance_pret paiement_echeance_pret_versement_id_fkey; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.paiement_echeance_pret
    ADD CONSTRAINT paiement_echeance_pret_versement_id_fkey FOREIGN KEY (versement_id) REFERENCES public.mouvement_tresorerie(id);


--
-- Name: paiement_effet paiement_effet_compte_id_fkey; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.paiement_effet
    ADD CONSTRAINT paiement_effet_compte_id_fkey FOREIGN KEY (compte_id) REFERENCES public.compte_tresorerie(id);


--
-- Name: paiement_effet paiement_effet_reglement_id_fkey; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.paiement_effet
    ADD CONSTRAINT paiement_effet_reglement_id_fkey FOREIGN KEY (reglement_id) REFERENCES public.reglement_fournisseur(id);


--
-- Name: paiement_effet paiement_effet_utilisateur_id_fkey; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.paiement_effet
    ADD CONSTRAINT paiement_effet_utilisateur_id_fkey FOREIGN KEY (utilisateur_id) REFERENCES public.utilisateur(id);


--
-- Name: paiement_effet paiement_effet_versement_id_fkey; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.paiement_effet
    ADD CONSTRAINT paiement_effet_versement_id_fkey FOREIGN KEY (versement_id) REFERENCES public.mouvement_tresorerie(id);


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
-- Name: parametre_formulaire_sms parametre_formulaire_sms_modifie_par_fkey; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.parametre_formulaire_sms
    ADD CONSTRAINT parametre_formulaire_sms_modifie_par_fkey FOREIGN KEY (modifie_par) REFERENCES public.utilisateur(id);


--
-- Name: parametre_substitution parametre_substitution_modifie_par_fkey; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.parametre_substitution
    ADD CONSTRAINT parametre_substitution_modifie_par_fkey FOREIGN KEY (modifie_par) REFERENCES public.utilisateur(id);


--
-- Name: patient patient_centre_payeur_id_fkey; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.patient
    ADD CONSTRAINT patient_centre_payeur_id_fkey FOREIGN KEY (centre_payeur_id) REFERENCES public.centre_payeur(id);


--
-- Name: patient patient_cree_par_fkey; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.patient
    ADD CONSTRAINT patient_cree_par_fkey FOREIGN KEY (cree_par) REFERENCES public.utilisateur(id);


--
-- Name: patient patient_organisme_id_fkey; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.patient
    ADD CONSTRAINT patient_organisme_id_fkey FOREIGN KEY (organisme_id) REFERENCES public.organisme(id);


--
-- Name: poste_autorise poste_autorise_cree_par_fkey; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.poste_autorise
    ADD CONSTRAINT poste_autorise_cree_par_fkey FOREIGN KEY (cree_par) REFERENCES public.utilisateur(id);


--
-- Name: poste_autorise poste_autorise_revoque_par_fkey; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.poste_autorise
    ADD CONSTRAINT poste_autorise_revoque_par_fkey FOREIGN KEY (revoque_par) REFERENCES public.utilisateur(id);


--
-- Name: pret_bancaire pret_bancaire_compte_id_fkey; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.pret_bancaire
    ADD CONSTRAINT pret_bancaire_compte_id_fkey FOREIGN KEY (compte_id) REFERENCES public.compte_tresorerie(id);


--
-- Name: pret_bancaire pret_bancaire_utilisateur_id_fkey; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.pret_bancaire
    ADD CONSTRAINT pret_bancaire_utilisateur_id_fkey FOREIGN KEY (utilisateur_id) REFERENCES public.utilisateur(id);


--
-- Name: produit produit_classe_confirmee_par_fkey; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.produit
    ADD CONSTRAINT produit_classe_confirmee_par_fkey FOREIGN KEY (classe_confirmee_par) REFERENCES public.utilisateur(id);


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
-- Name: produit_indication produit_indication_indication_id_fkey; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.produit_indication
    ADD CONSTRAINT produit_indication_indication_id_fkey FOREIGN KEY (indication_id) REFERENCES public.indication(id);


--
-- Name: produit_indication produit_indication_produit_id_fkey; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.produit_indication
    ADD CONSTRAINT produit_indication_produit_id_fkey FOREIGN KEY (produit_id) REFERENCES public.produit(id);


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
-- Name: profil_catalogue_grossiste profil_catalogue_grossiste_fournisseur_id_fkey; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.profil_catalogue_grossiste
    ADD CONSTRAINT profil_catalogue_grossiste_fournisseur_id_fkey FOREIGN KEY (fournisseur_id) REFERENCES public.fournisseur(id);


--
-- Name: profil_catalogue_grossiste profil_catalogue_grossiste_modifie_par_fkey; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.profil_catalogue_grossiste
    ADD CONSTRAINT profil_catalogue_grossiste_modifie_par_fkey FOREIGN KEY (modifie_par) REFERENCES public.utilisateur(id);


--
-- Name: reception_attente reception_attente_utilisateur_id_fkey; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.reception_attente
    ADD CONSTRAINT reception_attente_utilisateur_id_fkey FOREIGN KEY (utilisateur_id) REFERENCES public.utilisateur(id);


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
-- Name: reception_ligne reception_ligne_lot_id_fkey; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.reception_ligne
    ADD CONSTRAINT reception_ligne_lot_id_fkey FOREIGN KEY (lot_id) REFERENCES public.lot(id);


--
-- Name: reception_ligne reception_ligne_produit_id_fkey; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.reception_ligne
    ADD CONSTRAINT reception_ligne_produit_id_fkey FOREIGN KEY (produit_id) REFERENCES public.produit(id);


--
-- Name: reception_ligne reception_ligne_reception_id_fkey; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.reception_ligne
    ADD CONSTRAINT reception_ligne_reception_id_fkey FOREIGN KEY (reception_id) REFERENCES public.reception(id);


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
-- Name: regle_alerte_psychotrope regle_alerte_psychotrope_modifie_par_fkey; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.regle_alerte_psychotrope
    ADD CONSTRAINT regle_alerte_psychotrope_modifie_par_fkey FOREIGN KEY (modifie_par) REFERENCES public.utilisateur(id);


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
-- Name: reglement_fournisseur reglement_fournisseur_annulation_valide_par_fkey; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.reglement_fournisseur
    ADD CONSTRAINT reglement_fournisseur_annulation_valide_par_fkey FOREIGN KEY (annulation_valide_par) REFERENCES public.utilisateur(id);


--
-- Name: reglement_fournisseur reglement_fournisseur_annule_par_fkey; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.reglement_fournisseur
    ADD CONSTRAINT reglement_fournisseur_annule_par_fkey FOREIGN KEY (annule_par) REFERENCES public.utilisateur(id);


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
-- Name: regularisation_part_organisme regularisation_part_organisme_annulation_credit_mouvement__fkey; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.regularisation_part_organisme
    ADD CONSTRAINT regularisation_part_organisme_annulation_credit_mouvement__fkey FOREIGN KEY (annulation_credit_mouvement_id) REFERENCES public.credit_mouvement(id);


--
-- Name: regularisation_part_organisme regularisation_part_organisme_annulation_mouvement_tiroir__fkey; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.regularisation_part_organisme
    ADD CONSTRAINT regularisation_part_organisme_annulation_mouvement_tiroir__fkey FOREIGN KEY (annulation_mouvement_tiroir_id) REFERENCES public.mouvement_tiroir(id);


--
-- Name: regularisation_part_organisme regularisation_part_organisme_annulee_par_fkey; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.regularisation_part_organisme
    ADD CONSTRAINT regularisation_part_organisme_annulee_par_fkey FOREIGN KEY (annulee_par) REFERENCES public.utilisateur(id);


--
-- Name: regularisation_part_organisme regularisation_part_organisme_credit_mouvement_id_fkey; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.regularisation_part_organisme
    ADD CONSTRAINT regularisation_part_organisme_credit_mouvement_id_fkey FOREIGN KEY (credit_mouvement_id) REFERENCES public.credit_mouvement(id);


--
-- Name: regularisation_part_organisme regularisation_part_organisme_organisme_id_fkey; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.regularisation_part_organisme
    ADD CONSTRAINT regularisation_part_organisme_organisme_id_fkey FOREIGN KEY (organisme_id) REFERENCES public.organisme(id);


--
-- Name: regularisation_part_organisme regularisation_part_organisme_utilisateur_id_fkey; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.regularisation_part_organisme
    ADD CONSTRAINT regularisation_part_organisme_utilisateur_id_fkey FOREIGN KEY (utilisateur_id) REFERENCES public.utilisateur(id);


--
-- Name: regularisation_part_organisme regularisation_part_organisme_valide_par_fkey; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.regularisation_part_organisme
    ADD CONSTRAINT regularisation_part_organisme_valide_par_fkey FOREIGN KEY (valide_par) REFERENCES public.utilisateur(id);


--
-- Name: regularisation_part_organisme regularisation_part_organisme_vente_id_fkey; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.regularisation_part_organisme
    ADD CONSTRAINT regularisation_part_organisme_vente_id_fkey FOREIGN KEY (vente_id) REFERENCES public.vente(id);


--
-- Name: remboursement_titulaire remboursement_titulaire_compte_id_fkey; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.remboursement_titulaire
    ADD CONSTRAINT remboursement_titulaire_compte_id_fkey FOREIGN KEY (compte_id) REFERENCES public.compte_tresorerie(id);


--
-- Name: remboursement_titulaire remboursement_titulaire_mouvement_id_fkey; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.remboursement_titulaire
    ADD CONSTRAINT remboursement_titulaire_mouvement_id_fkey FOREIGN KEY (mouvement_id) REFERENCES public.mouvement_tresorerie(id);


--
-- Name: remboursement_titulaire remboursement_titulaire_utilisateur_id_fkey; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.remboursement_titulaire
    ADD CONSTRAINT remboursement_titulaire_utilisateur_id_fkey FOREIGN KEY (utilisateur_id) REFERENCES public.utilisateur(id);


--
-- Name: remise_automatique remise_automatique_arretee_par_fkey; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.remise_automatique
    ADD CONSTRAINT remise_automatique_arretee_par_fkey FOREIGN KEY (arretee_par) REFERENCES public.utilisateur(id);


--
-- Name: remise_automatique remise_automatique_categorie_fkey; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.remise_automatique
    ADD CONSTRAINT remise_automatique_categorie_fkey FOREIGN KEY (categorie) REFERENCES public.categorie_produit(code);


--
-- Name: remise_automatique remise_automatique_cree_par_fkey; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.remise_automatique
    ADD CONSTRAINT remise_automatique_cree_par_fkey FOREIGN KEY (cree_par) REFERENCES public.utilisateur(id);


--
-- Name: remise_automatique remise_automatique_modifiee_par_fkey; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.remise_automatique
    ADD CONSTRAINT remise_automatique_modifiee_par_fkey FOREIGN KEY (modifiee_par) REFERENCES public.utilisateur(id);


--
-- Name: remise_automatique remise_automatique_produit_id_fkey; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.remise_automatique
    ADD CONSTRAINT remise_automatique_produit_id_fkey FOREIGN KEY (produit_id) REFERENCES public.produit(id);


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
-- Name: rupture_grossiste rupture_grossiste_fournisseur_id_fkey; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.rupture_grossiste
    ADD CONSTRAINT rupture_grossiste_fournisseur_id_fkey FOREIGN KEY (fournisseur_id) REFERENCES public.fournisseur(id);


--
-- Name: rupture_grossiste rupture_grossiste_import_debut_id_fkey; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.rupture_grossiste
    ADD CONSTRAINT rupture_grossiste_import_debut_id_fkey FOREIGN KEY (import_debut_id) REFERENCES public.import_catalogue_grossiste(id);


--
-- Name: rupture_grossiste rupture_grossiste_import_fin_id_fkey; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.rupture_grossiste
    ADD CONSTRAINT rupture_grossiste_import_fin_id_fkey FOREIGN KEY (import_fin_id) REFERENCES public.import_catalogue_grossiste(id);


--
-- Name: rupture_grossiste rupture_grossiste_produit_id_fkey; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.rupture_grossiste
    ADD CONSTRAINT rupture_grossiste_produit_id_fkey FOREIGN KEY (produit_id) REFERENCES public.produit(id);


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
-- Name: sceau_integrite sceau_integrite_auteur_id_fkey; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.sceau_integrite
    ADD CONSTRAINT sceau_integrite_auteur_id_fkey FOREIGN KEY (auteur_id) REFERENCES public.utilisateur(id);


--
-- Name: sceau_integrite sceau_integrite_journal_fkey; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.sceau_integrite
    ADD CONSTRAINT sceau_integrite_journal_fkey FOREIGN KEY (journal) REFERENCES public.chaine_integrite(journal);


--
-- Name: serveur_officine serveur_officine_modifie_par_fkey; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.serveur_officine
    ADD CONSTRAINT serveur_officine_modifie_par_fkey FOREIGN KEY (modifie_par) REFERENCES public.utilisateur(id);


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
-- Name: substitution_liste_blanche substitution_liste_blanche_cree_par_fkey; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.substitution_liste_blanche
    ADD CONSTRAINT substitution_liste_blanche_cree_par_fkey FOREIGN KEY (cree_par) REFERENCES public.utilisateur(id);


--
-- Name: substitution_liste_blanche substitution_liste_blanche_produit_id_fkey; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.substitution_liste_blanche
    ADD CONSTRAINT substitution_liste_blanche_produit_id_fkey FOREIGN KEY (produit_id) REFERENCES public.produit(id);


--
-- Name: substitution_liste_blanche substitution_liste_blanche_remplacant_id_fkey; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.substitution_liste_blanche
    ADD CONSTRAINT substitution_liste_blanche_remplacant_id_fkey FOREIGN KEY (remplacant_id) REFERENCES public.produit(id);


--
-- Name: sync_ordre_recu sync_ordre_recu_message_ferme_par_fkey; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.sync_ordre_recu
    ADD CONSTRAINT sync_ordre_recu_message_ferme_par_fkey FOREIGN KEY (message_ferme_par) REFERENCES public.utilisateur(id);


--
-- Name: sync_ordre_recu sync_ordre_recu_traite_par_fkey; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.sync_ordre_recu
    ADD CONSTRAINT sync_ordre_recu_traite_par_fkey FOREIGN KEY (traite_par) REFERENCES public.utilisateur(id);


--
-- Name: sync_ordre_resultat sync_ordre_resultat_ordre_id_fkey; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.sync_ordre_resultat
    ADD CONSTRAINT sync_ordre_resultat_ordre_id_fkey FOREIGN KEY (ordre_id) REFERENCES public.sync_ordre_recu(id);


--
-- Name: utilisateur utilisateur_role_id_fkey; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.utilisateur
    ADD CONSTRAINT utilisateur_role_id_fkey FOREIGN KEY (role_id) REFERENCES public.role(id);


--
-- Name: utilisation_financement utilisation_financement_depense_id_fkey; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.utilisation_financement
    ADD CONSTRAINT utilisation_financement_depense_id_fkey FOREIGN KEY (depense_id) REFERENCES public.mouvement_tresorerie(id);


--
-- Name: utilisation_financement utilisation_financement_mouvement_id_fkey; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.utilisation_financement
    ADD CONSTRAINT utilisation_financement_mouvement_id_fkey FOREIGN KEY (mouvement_id) REFERENCES public.mouvement_tresorerie(id);


--
-- Name: utilisation_financement utilisation_financement_paiement_effet_id_fkey; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.utilisation_financement
    ADD CONSTRAINT utilisation_financement_paiement_effet_id_fkey FOREIGN KEY (paiement_effet_id) REFERENCES public.paiement_effet(id);


--
-- Name: utilisation_financement utilisation_financement_pret_id_fkey; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.utilisation_financement
    ADD CONSTRAINT utilisation_financement_pret_id_fkey FOREIGN KEY (pret_id) REFERENCES public.pret_bancaire(id);


--
-- Name: utilisation_financement utilisation_financement_reglement_id_fkey; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.utilisation_financement
    ADD CONSTRAINT utilisation_financement_reglement_id_fkey FOREIGN KEY (reglement_id) REFERENCES public.reglement_fournisseur(id);


--
-- Name: utilisation_financement utilisation_financement_utilisateur_id_fkey; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.utilisation_financement
    ADD CONSTRAINT utilisation_financement_utilisateur_id_fkey FOREIGN KEY (utilisateur_id) REFERENCES public.utilisateur(id);


--
-- Name: vente vente_bordereau_id_fkey; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.vente
    ADD CONSTRAINT vente_bordereau_id_fkey FOREIGN KEY (bordereau_id) REFERENCES public.bordereau(id);


--
-- Name: vente vente_centre_payeur_id_fkey; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.vente
    ADD CONSTRAINT vente_centre_payeur_id_fkey FOREIGN KEY (centre_payeur_id) REFERENCES public.centre_payeur(id);


--
-- Name: vente vente_chifa_exclue_par_fkey; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.vente
    ADD CONSTRAINT vente_chifa_exclue_par_fkey FOREIGN KEY (chifa_exclue_par) REFERENCES public.utilisateur(id);


--
-- Name: vente_chifa_facture_conflit vente_chifa_facture_conflit_tranche_par_fkey; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.vente_chifa_facture_conflit
    ADD CONSTRAINT vente_chifa_facture_conflit_tranche_par_fkey FOREIGN KEY (tranche_par) REFERENCES public.utilisateur(id);


--
-- Name: vente_chifa_facture_conflit vente_chifa_facture_conflit_vente_id_fkey; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.vente_chifa_facture_conflit
    ADD CONSTRAINT vente_chifa_facture_conflit_vente_id_fkey FOREIGN KEY (vente_id) REFERENCES public.vente(id);


--
-- Name: vente vente_chifa_rattache_par_fkey; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.vente
    ADD CONSTRAINT vente_chifa_rattache_par_fkey FOREIGN KEY (chifa_rattache_par) REFERENCES public.utilisateur(id);


--
-- Name: vente vente_chifa_saisi_par_fkey; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.vente
    ADD CONSTRAINT vente_chifa_saisi_par_fkey FOREIGN KEY (chifa_saisi_par) REFERENCES public.utilisateur(id);


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
-- Name: verification_integrite verification_integrite_declenchee_par_fkey; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.verification_integrite
    ADD CONSTRAINT verification_integrite_declenchee_par_fkey FOREIGN KEY (declenchee_par) REFERENCES public.utilisateur(id);


--
-- Name: verification_integrite verification_integrite_journal_fkey; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.verification_integrite
    ADD CONSTRAINT verification_integrite_journal_fkey FOREIGN KEY (journal) REFERENCES public.chaine_integrite(journal);


--
-- Name: verification_serialisation verification_serialisation_boite_id_fkey; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.verification_serialisation
    ADD CONSTRAINT verification_serialisation_boite_id_fkey FOREIGN KEY (boite_id) REFERENCES public.boite(id);


--
-- Name: versement_bordereau versement_bordereau_bordereau_id_fkey; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.versement_bordereau
    ADD CONSTRAINT versement_bordereau_bordereau_id_fkey FOREIGN KEY (bordereau_id) REFERENCES public.bordereau(id);


--
-- Name: versement_bordereau versement_bordereau_mouvement_id_fkey; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.versement_bordereau
    ADD CONSTRAINT versement_bordereau_mouvement_id_fkey FOREIGN KEY (mouvement_id) REFERENCES public.mouvement_tresorerie(id);


--
-- Name: zone_psychotrope_wilaya zone_psychotrope_wilaya_modifie_par_fkey; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.zone_psychotrope_wilaya
    ADD CONSTRAINT zone_psychotrope_wilaya_modifie_par_fkey FOREIGN KEY (modifie_par) REFERENCES public.utilisateur(id);


--
-- Name: zone_psychotrope_wilaya zone_psychotrope_wilaya_wilaya_fkey; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.zone_psychotrope_wilaya
    ADD CONSTRAINT zone_psychotrope_wilaya_wilaya_fkey FOREIGN KEY (wilaya) REFERENCES public.wilaya(code);


--
-- PostgreSQL database dump complete
--

\unrestrict bYJAwJgfijFwLe244M93hWc8IXyQJ3Sqz9zg8n98StjZOpakXtshPD3oqhUvNQz

