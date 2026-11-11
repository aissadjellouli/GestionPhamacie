-- =====================================================================
-- Reprise de données : corrections issues du test de reprise du 11/11/2026
--   * import.produit_src.classe : classe réglementaire de l'ancien logiciel
--     (facultative), reconnue par import.classe_src ;
--   * classe des produits créés : la plus stricte entre la nomenclature
--     nationale (par n° d'enregistrement) et l'ancien logiciel. Produit
--     absent de la nomenclature (ou sans n° d'enregistrement) : créé
--     « à vérifier », donc refusé en caisse jusqu'à la confirmation de sa
--     classe par le titulaire (avant : seuls les produits sans n° étaient
--     à vérifier, un psychotrope repris avec son n° était vendu « libre ») ;
--   * import.stock_src.prix_achat : prix d'achat de chaque lot (sinon celui
--     du produit) ; contrôle « valeur du stock au prix d'achat » ajouté ;
--   * deux lignes de même n° de lot et de dates différentes restent deux
--     lots (avant : fusionnées à la date la plus proche) ;
--   * grossiste « Reprise de stock (ancien logiciel) » créé seulement si
--     une ligne de stock n'a pas de grossiste connu.
-- Redéfinit import.controler, import.charger et
-- import.controle_apres_chargement (définis dans 008) : les scripts se
-- rejouent tous, dans l'ordre, sur une base déjà installée.
-- Idempotent. A exécuter après 047_recette_corrections.sql.
-- =====================================================================

ALTER TABLE import.produit_src ADD COLUMN IF NOT EXISTS classe text;
ALTER TABLE import.stock_src   ADD COLUMN IF NOT EXISTS prix_achat text;

-- Classe écrite par l'ancien logiciel → classe de Pharma Officine (NULL : vide ou non reconnue)
CREATE OR REPLACE FUNCTION import.classe_src(t text) RETURNS text
LANGUAGE sql STABLE AS $$
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

-- Rang de sévérité : la classe la plus stricte l'emporte
CREATE OR REPLACE FUNCTION import.rang_classe(c text) RETURNS integer
LANGUAGE sql IMMUTABLE AS $$
    SELECT CASE c WHEN 'stupefiant' THEN 4 WHEN 'psychotrope' THEN 3 WHEN 'liste_i' THEN 2 WHEN 'liste_ii' THEN 1 WHEN 'libre' THEN 0 END;
$$;

-- Classe proposée par la nomenclature nationale pour un n° d'enregistrement (NULL : absent ou non importée)
CREATE OR REPLACE FUNCTION import.classe_nomenclature(p_num text) RETURNS text
LANGUAGE sql STABLE AS $$
    SELECT v.classe_proposee
    FROM v_nomenclature v
    WHERE v.num_enregistrement = NULLIF(btrim(p_num), '')
    ORDER BY (v.situation = 'en_vigueur') DESC
    LIMIT 1;
$$;

CREATE OR REPLACE FUNCTION import.controler(p_session bigint) RETURNS integer
LANGUAGE plpgsql AS $$
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

    SELECT count(*) INTO nb_bloquants
    FROM import.anomalie WHERE session_id = p_session AND gravite = 'bloquant';
    RETURN nb_bloquants;
END;
$$;

CREATE OR REPLACE FUNCTION import.charger(p_session bigint, p_utilisateur_id bigint) RETURNS void
LANGUAGE plpgsql AS $$
DECLARE
    v_reprise  bigint;
    v_id       bigint;
    v_ref      text := 'REPRISE-' || p_session;
    v_nomencl  text;
    v_ancien   text;
    v_classe   text;
    r          record;
BEGIN
    IF (SELECT statut FROM import.session WHERE id = p_session) <> 'en_cours' THEN
        RAISE EXCEPTION 'Session % déjà chargée ou annulée', p_session;
    END IF;
    IF import.controler(p_session) > 0 THEN
        RAISE EXCEPTION 'Anomalies bloquantes restantes : chargement refusé';
    END IF;

    -- Produits absents du catalogue, créés à la demande du pharmacien. Classe : la plus stricte entre la nomenclature
    -- et l'ancien logiciel. Absent de la nomenclature : à vérifier par le titulaire, non vendable d'ici là (037)
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
        INSERT INTO produit (code_barre, num_enregistrement, nom_commercial, dosage, forme, prix_public, classe, a_verifier)
        VALUES (NULLIF(btrim(r.code_barre), ''), NULLIF(btrim(r.num_enregistrement), ''),
                r.nom, r.dosage, r.forme, COALESCE(import.nombre(r.prix_vente), 0),
                v_classe, v_nomencl IS NULL)
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

-- Contrôle après chargement : les totaux doivent être identiques (valeur du stock au prix d'achat ajoutée)
CREATE OR REPLACE FUNCTION import.controle_apres_chargement(p_session bigint)
RETURNS TABLE (indicateur text, ancien numeric, nouveau numeric, ecart numeric)
LANGUAGE sql STABLE AS $$
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
    ) t
    ORDER BY t.ordre;
$$;
