-- =====================================================================
-- Données FICTIVES pour le développement et les tests (jamais en production)
-- Mêmes cas que les maquettes : lot périmé, risque d'écoulement,
-- rupture avec alternative, stock critique, produit dormant, psychotrope.
-- Dates calculées par rapport au jour d'exécution.
-- Mot de passe de tous les comptes : demo
-- =====================================================================
CREATE EXTENSION IF NOT EXISTS pgcrypto;

-- Utilisateurs
INSERT INTO utilisateur (nom_complet, identifiant, mot_de_passe_hash, role_id)
SELECT u.nom, u.ident, crypt('demo', gen_salt('bf', 10)), r.id
FROM (VALUES ('Dr Titulaire Démo', 'titulaire', 'titulaire'),
             ('Assistant Démo',    'assistant', 'assistant'),
             ('Vendeur Démo',      'vendeur',   'vendeur')) AS u(nom, ident, role_code)
JOIN role r ON r.code = u.role_code;

-- Catalogue
INSERT INTO dci (nom) VALUES
    ('Paracétamol'), ('Amoxicilline'), ('Metformine'), ('Amlodipine'),
    ('Oméprazole'), ('Alprazolam'), ('Cholécalciférol'), ('Insuline glargine');

INSERT INTO produit (code_barre, nom_commercial, dci_id, dosage, forme, prix_public, tarif_reference, remboursable, classe)
SELECT p.cb, p.nom, d.id, p.dosage, p.forme, p.prix, p.tarif, p.remb, p.classe
FROM (VALUES
    ('6130000000011', 'Paracétamol 500 mg cp',          'Paracétamol',       '500 mg',    'comprimé', 120.00,  120.00, true,  'libre'),
    ('6130000000028', 'Amoxicilline 1 g cp',            'Amoxicilline',      '1 g',       'comprimé', 350.00,  350.00, true,  'liste_i'),
    ('6130000000035', 'Metformine 850 mg cp (Labo A)',  'Metformine',        '850 mg',    'comprimé', 220.00,  220.00, true,  'liste_i'),
    ('6130000000042', 'Metformine 850 mg cp (Labo B)',  'Metformine',        '850 mg',    'comprimé', 210.00,  210.00, true,  'liste_i'),
    ('6130000000059', 'Amlodipine 5 mg cp',             'Amlodipine',        '5 mg',      'comprimé', 280.00,  280.00, true,  'liste_i'),
    ('6130000000066', 'Oméprazole 20 mg gél',           'Oméprazole',        '20 mg',     'gélule',   350.00,  350.00, true,  'liste_i'),
    ('6130000000073', 'Alprazolam 0,25 mg cp',          'Alprazolam',        '0,25 mg',   'comprimé', 190.00,  190.00, true,  'psychotrope'),
    ('6130000000080', 'Vitamine D3 ampoules',           'Cholécalciférol',   '200 000 UI','ampoule',  240.00,  NULL,   false, 'libre'),
    ('6130000000097', 'Insuline glargine 100 UI/ml',    'Insuline glargine', '100 UI/ml', 'stylo',   3800.00, 3800.00, true,  'liste_i')
) AS p(cb, nom, dci, dosage, forme, prix, tarif, remb, classe)
JOIN dci d ON d.nom = p.dci;

-- Grossistes, avec leurs délais de reprise
INSERT INTO fournisseur (raison_sociale, telephone, solde, jours_orange, jours_rouge) VALUES
    ('Grossiste A (GR-01)', '[téléphone]', 185000.00, 195, NULL),
    ('Grossiste B (GR-02)', '[téléphone]',  64000.00, NULL, NULL),
    ('Grossiste C (GR-03)', '[téléphone]',  23500.00, 105, 60);

-- Une réception par grossiste, il y a 30 jours
INSERT INTO reception (fournisseur_id, numero_bl, date_reception)
SELECT f.id, 'BL-DEMO-' || f.id, now() - interval '30 days'
FROM fournisseur f;

-- Lots
INSERT INTO lot (produit_id, reception_id, fournisseur_id, numero_lot, date_peremption, quantite_disponible, prix_achat_ht)
SELECT p.id, r.id, f.id, l.lot, current_date + l.jours, l.qte, l.achat
FROM (VALUES
    ('Paracétamol 500 mg cp',         'Grossiste A (GR-01)', 'P8831',  900, 312,   80.00),
    ('Amoxicilline 1 g cp',           'Grossiste A (GR-01)', 'A2391',   -8,  14,  250.00),
    ('Amoxicilline 1 g cp',           'Grossiste A (GR-01)', 'B1182',  140,  20,  250.00),
    ('Metformine 850 mg cp (Labo A)', 'Grossiste B (GR-02)', 'MA01',   400,   0,  160.00),
    ('Metformine 850 mg cp (Labo B)', 'Grossiste B (GR-02)', 'MB07',   500,  34,  150.00),
    ('Amlodipine 5 mg cp',            'Grossiste A (GR-01)', 'AM55',   365,   8,  200.00),
    ('Oméprazole 20 mg gél',          'Grossiste C (GR-03)', 'OM552',  265, 100,  210.00),
    ('Alprazolam 0,25 mg cp',         'Grossiste A (GR-01)', 'AL25',   330,  14,  130.00),
    ('Vitamine D3 ampoules',          'Grossiste B (GR-02)', 'VD3',    450,  40,  240.00),
    ('Insuline glargine 100 UI/ml',   'Grossiste A (GR-01)', 'G7710',   84,  12, 3000.00)
) AS l(produit, grossiste, lot, jours, qte, achat)
JOIN produit p     ON p.nom_commercial = l.produit
JOIN fournisseur f ON f.raison_sociale = l.grossiste
JOIN reception r   ON r.fournisseur_id = f.id;

-- Patients fictifs
INSERT INTO patient (nom, prenom, telephone, num_assure, organisme_id, plafond_credit, solde_credit)
SELECT 'Patient fictif', n::text, '[téléphone]', 'ASSURE-DEMO-' || n, o.id, 10000, (n * 500) % 3000
FROM generate_series(1, 7) n
JOIN organisme o ON o.code = 'CNAS';

-- Historique de 300 ventes sur environ 90 jours (pour les ventes moyennes)
INSERT INTO vente (numero, date_vente, utilisateur_id, patient_id, type, statut)
SELECT 'T-DEMO-' || lpad(g::text, 5, '0'),
       now() - make_interval(hours => g * 7),
       (SELECT id FROM utilisateur WHERE identifiant = 'vendeur'),
       CASE WHEN g % 20 = 19 THEN (SELECT id FROM patient WHERE prenom = (1 + g % 7)::text) END,
       'libre', 'validee'
FROM generate_series(1, 300) g;

INSERT INTO ligne_vente (vente_id, produit_id, lot_id, quantite, prix_unitaire, montant_patient)
SELECT v.id, l.produit_id, l.id, m.qte, p.prix_public, p.prix_public * m.qte
FROM vente v
CROSS JOIN LATERAL (SELECT substr(v.numero, 8)::int AS g) n
CROSS JOIN LATERAL (
    SELECT CASE
             WHEN n.g % 40 = 18                THEN 'G7710'
             WHEN n.g % 20 BETWEEN 0 AND 7     THEN 'P8831'
             WHEN n.g % 20 BETWEEN 8 AND 11    THEN 'B1182'
             WHEN n.g % 20 BETWEEN 12 AND 14   THEN 'AM55'
             WHEN n.g % 20 = 15                THEN 'MB07'
             WHEN n.g % 20 = 16                THEN 'MA01'
             WHEN n.g % 20 = 17                THEN 'OM552'
             WHEN n.g % 20 = 19                THEN 'AL25'
             ELSE 'P8831'
           END AS lot,
           CASE WHEN n.g % 20 BETWEEN 0 AND 7 OR n.g % 20 IN (15, 16) THEN 2 ELSE 1 END AS qte
) m
JOIN lot l     ON l.numero_lot = m.lot
JOIN produit p ON p.id = l.produit_id
WHERE v.numero LIKE 'T-DEMO-%';

UPDATE vente v
SET montant_total = s.total, part_patient = s.total
FROM (SELECT vente_id, SUM(quantite * prix_unitaire) AS total FROM ligne_vente GROUP BY vente_id) s
WHERE s.vente_id = v.id;

-- Quelques boîtes étiquetées pour tester la traçabilité (lot G7710)
SELECT * FROM creer_boites_internes((SELECT id FROM lot WHERE numero_lot = 'G7710'), 3);

-- Première génération des alertes
SELECT generer_alertes() AS alertes_creees;
