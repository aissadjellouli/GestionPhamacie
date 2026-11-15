-- =====================================================================
-- Reprise des patients : doublons de n° d'assuré tolérés, à valider
-- (2026-11-15, règle du titulaire, choix « a »)
--
-- Depuis 089, un seul patient par n° d'assuré (index ux_patient_num_assure,
-- n° + suffixe réduits aux chiffres). Avant ce script, un export de l'ancien
-- logiciel qui contenait deux fois le même n° (ou un n° déjà en base) faisait
-- échouer tout le chargement.
--
-- Désormais :
--  - import.client_src.ligne : rang de la ligne dans la table source (lecture
--    de l'ancien logiciel), pour désigner « le premier » et les citer ;
--  - import.controler : un avertissement par doublon (compte rendu avant
--    chargement), sans bloquer ; les autres contrôles sont inchangés ;
--  - import.charger : le premier patient d'un n° est repris ; les suivants
--    (même n° dans le fichier, ou n° déjà en base) ne sont PAS créés et vont
--    dans import.doublon_patient (statut « a_valider »), avec leurs données
--    et leur dette reprise (jamais ajoutée en silence à une autre fiche) ;
--  - import.decider_doublon_patient : décision du titulaire ou d'un ayant
--    droit (garder la fiche existante, remplacer par les données importées,
--    créer comme ayant droit distinct avec un autre suffixe ; dette rattachée
--    ou abandonnée) ; journalisée par l'application ;
--  - import.controle_apres_chargement : deux indicateurs de plus (doublons,
--    dettes des doublons).
-- Idempotent ; aucune donnée existante modifiée. A exécuter après
-- 091_vente_remis_a_pour.sql.
-- =====================================================================

ALTER TABLE import.client_src ADD COLUMN IF NOT EXISTS ligne integer;
COMMENT ON COLUMN import.client_src.ligne IS 'Rang de la ligne dans la table de l''ancien logiciel (1 = première lue) ; NULL pour une extraction antérieure à 092';

CREATE TABLE IF NOT EXISTS import.doublon_patient (
    id                  bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    session_id          bigint NOT NULL REFERENCES import.session(id),
    ref_source          text NOT NULL,
    ligne               integer,
    nom                 text NOT NULL,
    prenom              text,
    telephone           text,
    num_assure          text NOT NULL,
    solde_credit        numeric(12,2) NOT NULL DEFAULT 0,
    patient_existant_id bigint NOT NULL REFERENCES public.patient(id),
    origine             text NOT NULL,
    statut              text NOT NULL DEFAULT 'a_valider',
    dette               text,
    patient_cree_id     bigint REFERENCES public.patient(id),
    decide_par          bigint REFERENCES public.utilisateur(id),
    decide_le           timestamptz,
    cree_le             timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT ux_doublon_patient_source UNIQUE (session_id, ref_source),
    CONSTRAINT ck_doublon_patient_origine CHECK (origine IN ('fichier', 'base')),
    CONSTRAINT ck_doublon_patient_statut CHECK (statut IN ('a_valider', 'garde_existant', 'remplace', 'ayant_droit')),
    CONSTRAINT ck_doublon_patient_dette CHECK (dette IS NULL OR dette IN ('rattachee', 'abandonnee')),
    CONSTRAINT ck_doublon_patient_decision CHECK ((statut = 'a_valider') = (decide_le IS NULL))
);
CREATE INDEX IF NOT EXISTS ix_doublon_patient_statut ON import.doublon_patient (statut, id);
COMMENT ON TABLE import.doublon_patient IS 'Patients de la reprise non créés (n° d''assuré déjà pris), à valider par le titulaire ou un ayant droit (092)';
COMMENT ON COLUMN import.doublon_patient.origine IS 'fichier : n° déjà repris d''une ligne précédente du même export ; base : n° d''un patient déjà en base';
COMMENT ON COLUMN import.doublon_patient.dette IS 'Dette reprise du patient ignoré : rattachee (ajoutée à la fiche gardée ou créée) ou abandonnee ; NULL sans dette ou avant décision';

-- ---------------------------------------------------------------------
-- Patients de l'export qui ne seront pas créés : n° d'assuré (chiffres seuls, comme ux_patient_num_assure) déjà
-- porté par un patient en base, ou par une ligne précédente du même export (ligne, puis ref_source)
-- ---------------------------------------------------------------------
CREATE OR REPLACE FUNCTION import.doublons_clients(p_session bigint)
RETURNS TABLE(ref_source text, ligne integer, nom text, prenom text, telephone text, num_assure text, solde_credit numeric,
              origine text, existant_patient_id bigint, existant_ref text, existant_ligne integer, existant_nom text)
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

CREATE OR REPLACE FUNCTION import.controler(p_session bigint) RETURNS integer
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


CREATE OR REPLACE FUNCTION import.controle_apres_chargement(p_session bigint) RETURNS TABLE(indicateur text, ancien numeric, nouveau numeric, ecart numeric)
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

-- ---------------------------------------------------------------------
-- Décision du titulaire (ou d'un ayant droit, contrôlé par l'application) sur un doublon en attente :
--   garder_existant : patient importé écarté ; dette rattachee (ajoutée à la fiche gardée) ou abandonnee ;
--   remplacer       : nom, prénom, téléphone et n° de la fiche existante remplacés par l'import ; dette idem ;
--   ayant_droit     : patient créé avec le n° saisi (autre suffixe : chiffres différents, n° libre) ; sa dette
--                     reprise va sur cette nouvelle fiche (dette = rattachee) ou est abandonnée.
-- Dette rattachée : mouvement « reprise » au relevé, au nom de p_utilisateur_id. Renvoie la fiche concernée.
-- ---------------------------------------------------------------------
CREATE OR REPLACE FUNCTION import.decider_doublon_patient(p_id bigint, p_decision text, p_dette text, p_num_assure text,
                                                          p_utilisateur_id bigint) RETURNS bigint
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
