-- =====================================================================
-- Emplacements FICTIFS (développement et tests seulement) et rangement
-- des produits de démonstration. A exécuter après 016_emplacements.sql.
-- Réexécution sans effet ; ignoré sans données de démonstration.
-- =====================================================================
SELECT NOT EXISTS (SELECT 1 FROM reception WHERE numero_bl = 'BL-DEMO-1') AS sans_demo \gset
\if :sans_demo
\echo 'Données de démonstration absentes : emplacements de démonstration ignorés.'
\quit
\endif

INSERT INTO emplacement (code, rayon, etagere, tiroir, libelle, zone) VALUES
    ('A-01-1', 'A', '01', '1', 'Antalgiques',                 'vente'),
    ('B-01-1', 'B', '01', '1', 'Antibiotiques',               'vente'),
    ('C-01-2', 'C', '01', '2', 'Cardiologie',                 'vente'),
    ('C-02-1', 'C', '02', '1', 'Diabète',                     'vente'),
    ('C-03-1', 'C', '03', '1', 'Anticoagulants',              'vente'),
    ('D-01-1', 'D', '01', '1', 'Gastro-entérologie',          'vente'),
    ('E-01-1', 'E', '01', '1', 'Vitamines',                   'vente'),
    ('F-01',   'F', '01', NULL, 'Réfrigérateur (2 à 8 °C)',   'vente'),
    ('P-01',   'P', '01', NULL, 'Armoire fermée (psychotropes)', 'vente'),
    ('R-01',   'R', '01', NULL, 'Réserve, étagère 1',         'reserve'),
    ('R-02',   'R', '02', NULL, 'Réserve, étagère 2',         'reserve')
ON CONFLICT (code) DO NOTHING;

-- Rangement des produits de démonstration (seulement ceux qui n'ont pas encore d'emplacement)
UPDATE produit p SET emplacement_id = e.id
FROM (VALUES
    ('6130000000011', 'A-01-1'), ('6130000000028', 'B-01-1'), ('6130000000035', 'C-02-1'),
    ('6130000000042', 'C-02-1'), ('6130000000059', 'C-01-2'), ('6130000000066', 'D-01-1'),
    ('6130000000073', 'P-01'),   ('6130000000080', 'E-01-1'), ('6130000000097', 'F-01'),
    ('6130000000107', 'C-03-1')
) AS r(code_barre, code)
JOIN emplacement e ON e.code = r.code
WHERE p.code_barre = r.code_barre AND p.emplacement_id IS NULL;

-- Réserve pour les produits à forte rotation
UPDATE produit p SET reserve_id = e.id
FROM (VALUES ('6130000000011', 'R-01'), ('6130000000028', 'R-01'), ('6130000000035', 'R-02')) AS r(code_barre, code)
JOIN emplacement e ON e.code = r.code
WHERE p.code_barre = r.code_barre AND p.reserve_id IS NULL;

-- Un lot « date à vérifier », comme après une reprise de données, pour tester l'inventaire tournant
UPDATE lot SET date_a_verifier = true
WHERE numero_lot = 'AM55' AND NOT date_a_verifier
  AND NOT EXISTS (SELECT 1 FROM lot WHERE date_a_verifier);
