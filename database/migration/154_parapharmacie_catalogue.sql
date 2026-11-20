-- =====================================================================
-- 154 : parapharmacie distinguée dans le catalogue (décision du titulaire
-- du 2026-11-20).
--   * Parapharmacie = toute catégorie non médicament (categorie_produit.
--     medicament = false), matériel médical et paramédical compris ; les 8
--     catégories existantes sont gardées et un code générique est ajouté :
--     « parapharmacie », « Parapharmacie (à préciser) ».
--   * parapharmacie_certaine(...) : règle STRICTE qui seule permet de classer
--     un produit en parapharmacie sans regard humain : n° d'enregistrement
--     vide ou absent de la nomenclature nationale (toutes versions), aucune
--     DCI, classe « libre », non remboursable, sans tarif de référence, et
--     aucune substance surveillée dont le motif figure dans son nom (ou sa
--     DCI). Hors nomenclature seul ne suffit jamais.
--   * import.charger (reprise) : n° trouvé dans la nomenclature, médicament ;
--     sinon parapharmacie si la règle stricte est remplie ; sinon médicament
--     (à classer par le titulaire). Classe et « à vérifier » inchangés.
--   * Rattrapage : produits encore en catégorie « medicament » qui remplissent
--     la règle stricte passés en « parapharmacie » (nombre affiché). Les
--     autres produits hors nomenclature restent médicaments et se classent
--     à la main (Administration › Nomenclature, « Produits à classer »).
-- Idempotent : rejoué, la catégorie existe déjà et aucun produit ne reste à
-- rattraper (NOTICE « 0 produit »). Aucune donnée supprimée.
-- Après 045 (categorie_produit), 092 (import.charger) et 153.
-- =====================================================================

-- Fichier en UTF-8 : lu comme tel quel que soit l'encodage du poste qui lance psql.
SET client_encoding = 'UTF8';

-- ---- 1. Catégorie générique « Parapharmacie (à préciser) »
INSERT INTO categorie_produit (code, libelle, medicament, ordre) VALUES
    ('parapharmacie', 'Parapharmacie (à préciser)', false, 90)
ON CONFLICT (code) DO NOTHING;
COMMENT ON TABLE categorie_produit IS 'Catégories de produits ; medicament : compté dans les totaux « médicaments », sinon parapharmacie (« autres produits » au ticket)';

-- ---- 2. Règle stricte de la parapharmacie certaine
CREATE OR REPLACE FUNCTION parapharmacie_certaine(p_num text, p_dci_id bigint, p_classe text, p_remboursable boolean,
                                                  p_tarif_reference numeric, p_nom text, p_dci text DEFAULT NULL) RETURNS boolean
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
COMMENT ON FUNCTION parapharmacie_certaine(text, bigint, text, boolean, numeric, text, text) IS 'Règle stricte (154) : produit classable en parapharmacie sans regard humain (hors nomenclature, sans DCI, classe libre, non remboursable, sans tarif de référence, aucune substance surveillée dans le nom)';

-- ---- 3. Reprise : catégorie du produit créé
CREATE OR REPLACE FUNCTION import.charger(p_session bigint, p_utilisateur_id bigint) RETURNS void
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

-- ---- 4. Rattrapage des produits existants : règle stricte seulement
DO $$
DECLARE
    n integer;
BEGIN
    UPDATE produit p SET categorie = 'parapharmacie'
    WHERE p.categorie = 'medicament'
      AND parapharmacie_certaine(p.num_enregistrement, p.dci_id, p.classe, p.remboursable, p.tarif_reference, p.nom_commercial);
    GET DIAGNOSTICS n = ROW_COUNT;
    RAISE NOTICE '154 : % produit(s) passé(s) de « medicament » à « parapharmacie » (règle stricte)', n;
END $$;
