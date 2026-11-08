-- =====================================================================
-- Complément de données FICTIVES (développement et tests seulement) :
-- Sintrom 4 mg (acénocoumarol), pour tester les ordonnances manuscrites.
-- Code-barres, prix et lot sont inventés.
-- À exécuter après 900_donnees_demo.sql. Réexécution sans effet.
-- =====================================================================
SELECT NOT EXISTS (SELECT 1 FROM reception WHERE numero_bl = 'BL-DEMO-1') AS sans_demo \gset
\if :sans_demo
\echo 'Données de démonstration absentes : complément ignoré.'
\quit
\endif

INSERT INTO dci (nom) VALUES ('Acénocoumarol') ON CONFLICT (nom) DO NOTHING;

INSERT INTO produit (code_barre, nom_commercial, dci_id, dosage, forme, prix_public, tarif_reference, remboursable, classe)
SELECT '6130000000107', 'Sintrom 4 mg cp', d.id, '4 mg', 'comprimé', 280.00, 280.00, true, 'liste_i'
FROM dci d WHERE d.nom = 'Acénocoumarol'
ON CONFLICT (code_barre) DO NOTHING;

-- Un lot en stock chez le Grossiste A (réception de démonstration)
INSERT INTO lot (produit_id, reception_id, fournisseur_id, numero_lot, date_peremption, quantite_disponible, prix_achat_ht)
SELECT p.id, r.id, r.fournisseur_id, 'SIN4-26', current_date + 420, 25, 200.00
FROM produit p
JOIN reception r ON r.numero_bl = 'BL-DEMO-1'
WHERE p.code_barre = '6130000000107'
ON CONFLICT ON CONSTRAINT ux_lot_reception DO NOTHING;
