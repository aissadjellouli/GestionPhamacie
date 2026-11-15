-- =====================================================================
-- Ventes Chifa et MDN jamais refacturées ; un seul patient par n° d'assuré
-- (2026-11-15, docs/taches/2026-11-15-recette-complete/conception-chifa-mdn.md §3.1)
--
-- A. vente.canal_organisme : qui facture la part de l'organisme
--      'logiciel' : facture d'organisme de Pharma Officine (cas habituel) ;
--      'chifa'    : facturée par le logiciel Chifa (volet scanné, ou case
--                   « facturée par Chifa » avec le n° de facture Chifa) ;
--      'mdn'      : vente à l'organisme militaire (code « MDN »).
--    Reprise : ventes avec chifa_facture => 'chifa' ; ventes dont
--    l'organisme a le code « MDN » => 'mdn' (seules lignes modifiées,
--    nombre affiché). v_ligne_vente_facturable (mêmes colonnes, même
--    ordre) : part organisme à 0 pour les canaux 'chifa' et 'mdn' ; la
--    part patient reste facturable. Factures déjà émises : inchangées
--    (celles qui portent la part organisme d'une vente Chifa/MDN sont
--    listées par un avis).
-- B. Unicité du n° d'assuré : index unique partiel sur le n° réduit à ses
--    chiffres (n° + suffixe de l'ayant droit : « 690755003842/80 » et
--    « 690755003842/01 » restent deux patients). Créé SEULEMENT s'il n'y a
--    aucun doublon ; sinon les doublons sont listés et rien n'est fait
--    (aucune fusion, aucune suppression).
-- Idempotent. A exécuter après 088_libelle_encaissement_dette.sql.
-- =====================================================================

-- ---------------------------------------------------------------------
-- A. Canal de facturation de la part organisme
-- ---------------------------------------------------------------------
ALTER TABLE vente ADD COLUMN IF NOT EXISTS canal_organisme text NOT NULL DEFAULT 'logiciel';
DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'ck_vente_canal_organisme') THEN
        ALTER TABLE vente ADD CONSTRAINT ck_vente_canal_organisme CHECK (canal_organisme IN ('logiciel', 'chifa', 'mdn'));
    END IF;
END $$;
COMMENT ON COLUMN vente.canal_organisme IS 'Qui facture la part organisme : logiciel (facture Pharma Officine), chifa (logiciel Chifa), mdn (organisme militaire) ; chifa et mdn ne sont jamais refacturées';

DO $$
DECLARE
    n_chifa integer;
    n_mdn   integer;
BEGIN
    UPDATE vente SET canal_organisme = 'chifa'
    WHERE chifa_facture IS NOT NULL AND canal_organisme <> 'chifa';
    GET DIAGNOSTICS n_chifa = ROW_COUNT;

    UPDATE vente v SET canal_organisme = 'mdn'
    FROM organisme o
    WHERE o.id = v.organisme_id AND upper(o.code) = 'MDN' AND v.chifa_facture IS NULL AND v.canal_organisme = 'logiciel';
    GET DIAGNOSTICS n_mdn = ROW_COUNT;

    RAISE NOTICE 'Reprise du canal : % vente(s) passée(s) en chifa, % en mdn.', n_chifa, n_mdn;
    IF NOT EXISTS (SELECT 1 FROM organisme WHERE upper(code) = 'MDN') THEN
        RAISE NOTICE 'Aucun organisme de code MDN : aucune vente reprise en mdn.';
    END IF;
END $$;

CREATE OR REPLACE VIEW v_ligne_vente_facturable AS
SELECT v.id AS vente_id, v.numero, v.date_vente, v.patient_id, v.organisme_id, v.ordonnance_id, o.numero AS ordonnance_numero,
       l.id AS ligne_vente_id, p.nom_commercial AS designation, p.taux_tva, l.prix_unitaire, l.quantite,
       l.quantite - COALESCE(rt.quantite, 0) AS quantite_nette,
       round(l.montant_patient * (l.quantite - COALESCE(rt.quantite, 0)) / l.quantite, 2) AS montant_patient,
       -- Part organisme d'une vente facturée par Chifa ou le MDN : jamais facturable par le logiciel
       CASE WHEN v.canal_organisme = 'logiciel'
            THEN round(l.montant_organisme * (l.quantite - COALESCE(rt.quantite, 0)) / l.quantite, 2)
            ELSE 0 END AS montant_organisme,
       v.montant_remise,
       EXISTS (SELECT 1 FROM facture_client_ligne fl JOIN facture_client f ON f.id = fl.facture_id
               WHERE fl.ligne_vente_id = l.id AND fl.part = 'patient' AND f.statut IN ('emise', 'rejetee') AND f.type = 'facture') AS facturee_patient,
       EXISTS (SELECT 1 FROM facture_client_ligne fl JOIN facture_client f ON f.id = fl.facture_id
               WHERE fl.ligne_vente_id = l.id AND fl.part = 'organisme' AND f.statut IN ('emise', 'rejetee') AND f.type = 'facture') AS facturee_organisme
FROM vente v
JOIN ligne_vente l ON l.vente_id = v.id
JOIN produit p ON p.id = l.produit_id
LEFT JOIN ordonnance o ON o.id = v.ordonnance_id
LEFT JOIN LATERAL (SELECT SUM(quantite) AS quantite FROM retour_client_ligne r WHERE r.ligne_vente_id = l.id) rt ON true
WHERE v.statut = 'validee' AND l.quantite > 0;

-- Constat : factures d'organisme déjà émises sur la part organisme d'une vente Chifa / MDN (gardées telles quelles)
DO $$
DECLARE
    r record;
BEGIN
    FOR r IN
        SELECT DISTINCT f.numero AS facture, f.statut, v.numero AS vente, v.canal_organisme
        FROM facture_client f
        JOIN facture_client_ligne fl ON fl.facture_id = f.id AND fl.part = 'organisme'
        JOIN ligne_vente l ON l.id = fl.ligne_vente_id
        JOIN vente v ON v.id = l.vente_id
        WHERE v.canal_organisme <> 'logiciel'
        ORDER BY 1, 3
    LOOP
        RAISE NOTICE 'A vérifier : facture % (%) porte la part organisme de la vente % (canal %).', r.facture, r.statut, r.vente, r.canal_organisme;
    END LOOP;
END $$;

-- ---------------------------------------------------------------------
-- B. Un seul patient par n° d'assuré (n° + suffixe, chiffres seuls)
-- ---------------------------------------------------------------------
DO $$
DECLARE
    r record;
    doublons integer := 0;
BEGIN
    IF to_regclass('public.ux_patient_num_assure') IS NOT NULL THEN
        RAISE NOTICE 'Index ux_patient_num_assure déjà présent.';
        RETURN;
    END IF;
    FOR r IN
        SELECT regexp_replace(num_assure, '\D', '', 'g') AS cle,
               string_agg(id::text || ' ' || trim(nom || ' ' || COALESCE(prenom, '')) || ' [' || num_assure || ']', ' | ' ORDER BY id) AS fiches
        FROM patient
        WHERE num_assure IS NOT NULL AND regexp_replace(num_assure, '\D', '', 'g') <> ''
        GROUP BY 1 HAVING count(*) > 1
        ORDER BY 1
    LOOP
        doublons := doublons + 1;
        RAISE NOTICE 'Doublon de n° d''assuré % : %', r.cle, r.fiches;
    END LOOP;
    IF doublons > 0 THEN
        RAISE WARNING 'Index unique du n° d''assuré NON créé : % n° en double (listés ci-dessus). Corrigez les fiches (sans les supprimer), puis relancez ce script.', doublons;
        RETURN;
    END IF;
    CREATE UNIQUE INDEX ux_patient_num_assure ON patient ((regexp_replace(num_assure, '\D', '', 'g')))
        WHERE num_assure IS NOT NULL AND regexp_replace(num_assure, '\D', '', 'g') <> '';
    RAISE NOTICE 'Index unique ux_patient_num_assure créé.';
END $$;
