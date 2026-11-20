-- =====================================================================
-- Tests du script 154 (parapharmacie distinguée dans le catalogue : catégorie générique, règle stricte, rattrapage,
-- reprise), en transaction ANNULÉE, depuis la racine du dépôt :
--   psql -U pharma_app -d pharmacie_test -v ON_ERROR_STOP=1 -f database/tests/154_parapharmacie_catalogue_tests.sql
-- Le script 154 est rejoué deux fois dans la transaction ; rien n'est gardé (ROLLBACK).
-- =====================================================================
BEGIN;

-- Données de test : un n° de la nomenclature, une substance surveillée propre au test, des produits « medicament »
INSERT INTO nomenclature_version (libelle, fichier, en_vigueur, non_renouveles, retraits, utilisateur_id)
SELECT 'Test 154', 'test154.xlsx', 0, 0, 0, (SELECT min(id) FROM utilisateur);
INSERT INTO nomenclature_produit (version_id, situation, num_enregistrement, dci, marque)
SELECT max(id), 'en_vigueur', 'T154/NOMENCL', 'PARACETAMOL', 'ZZ154 MARQUE' FROM nomenclature_version WHERE libelle = 'Test 154';
-- Second n° de la nomenclature, propre à la reprise (le premier est déjà pris par un produit du catalogue : n° unique)
INSERT INTO nomenclature_produit (version_id, situation, num_enregistrement, dci, marque)
SELECT max(id), 'en_vigueur', 'T154/REPRISE', 'PARACETAMOL', 'ZZ154 MARQUE REPRISE' FROM nomenclature_version WHERE libelle = 'Test 154';
INSERT INTO substance_surveillee (motif, classe, source) VALUES ('ZZPSYCHO154', 'psychotrope', 'test 154');
INSERT INTO dci (nom) VALUES ('ZZ154 DCI') ON CONFLICT DO NOTHING;

INSERT INTO produit (nom_commercial, num_enregistrement, classe, categorie) VALUES
    ('ZZ154 Crème certaine', NULL, 'libre', 'medicament'),                 -- règle stricte remplie
    ('ZZ154 Gel hors nomenclature', 'T154/ABSENT', 'libre', 'medicament'), -- n° absent de la nomenclature : remplie aussi
    ('ZZ154 Produit nomenclature', 'T154/NOMENCL', 'libre', 'medicament'), -- n° dans la nomenclature : jamais touché
    ('ZZ154 ZZPSYCHO154 sirop', NULL, 'libre', 'medicament'),              -- substance surveillée dans le nom : jamais touché
    ('ZZ154 Psychotrope', NULL, 'psychotrope', 'medicament'),              -- classe sous registre : jamais touché
    ('ZZ154 Liste I', NULL, 'liste_i', 'medicament');                      -- classe ≠ libre : jamais touché
INSERT INTO produit (nom_commercial, classe, categorie, remboursable, tarif_reference) VALUES
    ('ZZ154 Remboursable', 'libre', 'medicament', true, 100);             -- remboursable : jamais touché
INSERT INTO produit (nom_commercial, classe, categorie, tarif_reference) VALUES
    ('ZZ154 Avec tarif', 'libre', 'medicament', 100);                     -- tarif de référence : jamais touché
INSERT INTO produit (nom_commercial, classe, categorie, dci_id) VALUES
    ('ZZ154 Avec DCI', 'libre', 'medicament', (SELECT id FROM dci WHERE nom = 'ZZ154 DCI'));   -- DCI : jamais touché
INSERT INTO produit (nom_commercial, classe, categorie) VALUES
    ('ZZ154 Hygiène déjà classée', 'libre', 'hygiene');                  -- autre catégorie : gardée

-- 1. Règle stricte (fonction)
DO $$
BEGIN
    ASSERT parapharmacie_certaine(NULL, NULL, 'libre', false, NULL, 'Crème mains'), 'sans n°, sans DCI, libre : certaine';
    ASSERT parapharmacie_certaine('T154/ABSENT', NULL, 'libre', false, NULL, 'Gel'), 'n° absent de la nomenclature : certaine';
    ASSERT NOT parapharmacie_certaine('T154/NOMENCL', NULL, 'libre', false, NULL, 'X'), 'n° de la nomenclature : jamais';
    ASSERT NOT parapharmacie_certaine(' T154/NOMENCL ', NULL, 'libre', false, NULL, 'X'), 'n° de la nomenclature (espaces) : jamais';
    ASSERT NOT parapharmacie_certaine(NULL, 1, 'libre', false, NULL, 'X'), 'avec DCI : jamais';
    ASSERT NOT parapharmacie_certaine(NULL, NULL, 'libre', false, NULL, 'X', 'Paracétamol'), 'DCI en texte : jamais';
    ASSERT NOT parapharmacie_certaine(NULL, NULL, 'liste_ii', false, NULL, 'X'), 'liste II : jamais';
    ASSERT NOT parapharmacie_certaine(NULL, NULL, NULL, false, NULL, 'X'), 'classe inconnue : jamais';
    ASSERT NOT parapharmacie_certaine(NULL, NULL, 'libre', true, NULL, 'X'), 'remboursable : jamais';
    ASSERT NOT parapharmacie_certaine(NULL, NULL, 'libre', false, 50, 'X'), 'tarif de référence : jamais';
    ASSERT NOT parapharmacie_certaine(NULL, NULL, 'libre', false, NULL, 'zzpsycho154 gouttes'), 'substance surveillée dans le nom : jamais';
END $$;

-- 2. Rattrapage : rejeu du script (deux fois : le second n'a plus rien à faire)
\ir ../migration/154_parapharmacie_catalogue.sql
\ir ../migration/154_parapharmacie_catalogue.sql

DO $$
BEGIN
    ASSERT (SELECT count(*) FROM categorie_produit WHERE code = 'parapharmacie' AND NOT medicament AND actif) = 1,
        'catégorie « parapharmacie » présente une fois, non médicament';
    ASSERT (SELECT libelle FROM categorie_produit WHERE code = 'parapharmacie') = 'Parapharmacie (à préciser)', 'libellé';
    ASSERT (SELECT categorie FROM produit WHERE nom_commercial = 'ZZ154 Crème certaine') = 'parapharmacie', 'règle remplie : rattrapé';
    ASSERT (SELECT categorie FROM produit WHERE nom_commercial = 'ZZ154 Gel hors nomenclature') = 'parapharmacie', 'n° absent : rattrapé';
    ASSERT (SELECT count(*) FROM produit WHERE nom_commercial IN ('ZZ154 Produit nomenclature', 'ZZ154 ZZPSYCHO154 sirop', 'ZZ154 Psychotrope',
            'ZZ154 Liste I', 'ZZ154 Remboursable', 'ZZ154 Avec tarif', 'ZZ154 Avec DCI') AND categorie = 'medicament') = 7,
        'nomenclature, substance surveillée, psychotrope, liste I, remboursable, tarif, DCI : restés médicaments';
    ASSERT (SELECT categorie FROM produit WHERE nom_commercial = 'ZZ154 Hygiène déjà classée') = 'hygiene', 'autre catégorie gardée';
END $$;

-- 3. Reprise : import.charger donne la catégorie au produit créé
DO $$
DECLARE
    v_session bigint;
BEGIN
    INSERT INTO import.session (logiciel_source) VALUES ('test 154') RETURNING id INTO v_session;
    INSERT INTO import.produit_src (session_id, ref_source, nom, num_enregistrement, classe, prix_vente) VALUES
        (v_session, 'A', 'ZZ154 Reprise crème', NULL, NULL, '100'),
        (v_session, 'B', 'ZZ154 Reprise nomenclature', 'T154/REPRISE', NULL, '100'),
        (v_session, 'C', 'ZZ154 Reprise ZZPSYCHO154', NULL, NULL, '100'),
        (v_session, 'D', 'ZZ154 Reprise liste I', NULL, 'liste I', '100');
    INSERT INTO import.correspondance_produit (session_id, ref_source, methode, valide)
    SELECT v_session, ref_source, 'nouveau', true FROM import.produit_src WHERE session_id = v_session;
    PERFORM import.charger(v_session, (SELECT min(id) FROM utilisateur));

    ASSERT (SELECT categorie FROM produit WHERE nom_commercial = 'ZZ154 Reprise crème') = 'parapharmacie', 'reprise : règle stricte, parapharmacie';
    ASSERT (SELECT categorie FROM produit WHERE nom_commercial = 'ZZ154 Reprise nomenclature') = 'medicament', 'reprise : n° de la nomenclature, médicament';
    ASSERT (SELECT categorie FROM produit WHERE nom_commercial = 'ZZ154 Reprise ZZPSYCHO154') = 'medicament', 'reprise : substance surveillée, médicament';
    ASSERT (SELECT categorie = 'medicament' AND classe = 'liste_i' FROM produit WHERE nom_commercial = 'ZZ154 Reprise liste I'), 'reprise : liste I, médicament';
    -- « À vérifier » inchangé : tout produit absent de la nomenclature le reste, quelle que soit sa catégorie
    ASSERT (SELECT a_verifier FROM produit WHERE nom_commercial = 'ZZ154 Reprise crème'), 'reprise : absent de la nomenclature, à vérifier comme avant';
    ASSERT (SELECT NOT a_verifier FROM produit WHERE nom_commercial = 'ZZ154 Reprise nomenclature'), 'reprise : dans la nomenclature, pas à vérifier';
END $$;

ROLLBACK;
\echo '154 : tous réussis'
