-- =====================================================================
-- Reprise des données de l'ancien logiciel (mise en production)
-- Principe : on charge les exports bruts dans une zone de transit
-- (schéma "import"), on rapproche, on contrôle, puis on charge
-- dans la vraie base en UNE transaction. La base de l'ancien
-- logiciel n'est jamais modifiée.
-- A exécuter après toutes les autres migrations.
-- =====================================================================

CREATE SCHEMA IF NOT EXISTS import;

-- Lots dont la date de péremption n'était pas connue dans l'ancien logiciel
ALTER TABLE lot ADD COLUMN IF NOT EXISTS date_a_verifier boolean NOT NULL DEFAULT false;

-- ---------------------------------------------------------------------
-- 1. Sessions d'import (une répétition = une session, le jour J = une autre)
-- ---------------------------------------------------------------------
CREATE TABLE IF NOT EXISTS import.session (
    id               bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    logiciel_source  text        NOT NULL,           -- nom et version de l'ancien logiciel
    debut            timestamptz NOT NULL DEFAULT now(),
    statut           text        NOT NULL DEFAULT 'en_cours'
                     CHECK (statut IN ('en_cours', 'charge', 'annule')),
    notes            text
);

-- ---------------------------------------------------------------------
-- 2. Tables de transit : tout en texte, tel que lu dans l'export
-- ---------------------------------------------------------------------
CREATE TABLE IF NOT EXISTS import.produit_src (
    session_id          bigint NOT NULL REFERENCES import.session(id),
    ref_source          text   NOT NULL,     -- identifiant dans l'ancien logiciel
    code_barre          text,
    num_enregistrement  text,
    nom                 text   NOT NULL,
    dosage              text,
    forme               text,
    prix_vente          text,
    prix_achat          text,
    PRIMARY KEY (session_id, ref_source)
);

CREATE TABLE IF NOT EXISTS import.fournisseur_src (
    session_id      bigint NOT NULL REFERENCES import.session(id),
    ref_source      text   NOT NULL,
    raison_sociale  text   NOT NULL,
    telephone       text,
    solde           text,                    -- dette envers le grossiste
    fournisseur_id  bigint,                  -- rempli au chargement
    PRIMARY KEY (session_id, ref_source)
);

CREATE TABLE IF NOT EXISTS import.stock_src (
    id                  bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    session_id          bigint NOT NULL REFERENCES import.session(id),
    ref_produit         text   NOT NULL,
    numero_lot          text,
    date_peremption     text,
    quantite            text,
    fournisseur_ref     text
);

CREATE TABLE IF NOT EXISTS import.client_src (
    session_id      bigint NOT NULL REFERENCES import.session(id),
    ref_source      text   NOT NULL,
    nom             text   NOT NULL,
    prenom          text,
    telephone       text,
    num_assure      text,
    solde_credit    text,
    patient_id      bigint,                  -- rempli au chargement
    PRIMARY KEY (session_id, ref_source)
);

-- Rapprochement des produits de l'ancien logiciel avec le catalogue
CREATE TABLE IF NOT EXISTS import.correspondance_produit (
    session_id  bigint  NOT NULL REFERENCES import.session(id),
    ref_source  text    NOT NULL,
    produit_id  bigint  REFERENCES produit(id),
    methode     text    NOT NULL CHECK (methode IN (
                    'code_barre', 'num_enregistrement', 'nom_approche', 'manuel', 'nouveau')),
    score       numeric(4,3),
    valide      boolean NOT NULL DEFAULT false,
    PRIMARY KEY (session_id, ref_source)
);

CREATE TABLE IF NOT EXISTS import.anomalie (
    id          bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    session_id  bigint NOT NULL REFERENCES import.session(id),
    entite      text   NOT NULL,
    ref_source  text,
    gravite     text   NOT NULL CHECK (gravite IN ('bloquant', 'avertissement')),
    message     text   NOT NULL
);

-- ---------------------------------------------------------------------
-- 3. Conversions tolérantes (formats algériens et français courants)
-- ---------------------------------------------------------------------
CREATE OR REPLACE FUNCTION import.nombre(t text) RETURNS numeric
LANGUAGE plpgsql IMMUTABLE AS $$
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
$$;

CREATE OR REPLACE FUNCTION import.date_src(t text) RETURNS date
LANGUAGE plpgsql IMMUTABLE AS $$
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
$$;

-- ---------------------------------------------------------------------
-- 4. Rapprochement automatique des produits
--    code-barres et n° d'enregistrement : validés d'office
--    nom approché : proposé, à confirmer par le pharmacien
-- ---------------------------------------------------------------------
CREATE OR REPLACE FUNCTION import.rapprocher_produits(p_session bigint) RETURNS void
LANGUAGE plpgsql AS $$
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

-- ---------------------------------------------------------------------
-- 5. Contrôles : rien n'est chargé tant qu'il reste une anomalie bloquante
-- ---------------------------------------------------------------------
CREATE OR REPLACE FUNCTION import.controler(p_session bigint) RETURNS integer
LANGUAGE plpgsql AS $$
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

-- ---------------------------------------------------------------------
-- 6. Chargement dans la vraie base, en une seule transaction
-- ---------------------------------------------------------------------
CREATE OR REPLACE FUNCTION import.charger(p_session bigint, p_utilisateur_id bigint) RETURNS void
LANGUAGE plpgsql AS $$
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
    SELECT c.produit_id, rc.reception_id, rc.fournisseur_id, x.numero_lot,
           COALESCE(MIN(x.dte), current_date + 365),
           SUM(x.qte)::int,
           MAX(COALESCE(import.nombre(ps.prix_achat), 0)),
           bool_or(x.dte IS NULL)
    FROM (
        SELECT st.*,
               import.nombre(st.quantite)        AS qte,
               import.date_src(st.date_peremption) AS dte,
               COALESCE(NULLIF(btrim(st.numero_lot), ''),
                        'REPRISE-' || COALESCE(to_char(import.date_src(st.date_peremption), 'YYYYMM'), 'ND'))
                                                 AS numero_lot
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
    GROUP BY c.produit_id, rc.reception_id, rc.fournisseur_id, x.numero_lot;

    -- Mouvements d'entrée pour la traçabilité
    INSERT INTO mouvement_stock (lot_id, type, quantite, reference, utilisateur_id)
    SELECT l.id, 'entree', l.quantite_disponible, v_ref, p_utilisateur_id
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

-- ---------------------------------------------------------------------
-- 7. Contrôle après chargement : les totaux doivent être identiques
-- ---------------------------------------------------------------------
CREATE OR REPLACE FUNCTION import.controle_apres_chargement(p_session bigint)
RETURNS TABLE (indicateur text, ancien numeric, nouveau numeric, ecart numeric)
LANGUAGE sql STABLE AS $$
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

-- Lots à contrôler pendant l'inventaire de démarrage
CREATE OR REPLACE VIEW v_lots_date_a_verifier AS
SELECT l.id AS lot_id, p.nom_commercial, l.numero_lot, l.quantite_disponible, f.raison_sociale AS fournisseur
FROM lot l
JOIN produit p     ON p.id = l.produit_id
JOIN fournisseur f ON f.id = l.fournisseur_id
WHERE l.date_a_verifier AND l.quantite_disponible > 0
ORDER BY p.nom_commercial;
