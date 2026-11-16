-- =====================================================================
-- Base centrale : famille « banque » élargie (2026-11-16, décision du
-- titulaire). Même liste que l'officine (script 135). Défaut NON inchangé.
-- correspondance_echeancier_preteur.preteur non ajoutée : clé de la ligne.
-- Idempotent ; à passer après 007.
-- =====================================================================

UPDATE cloud_famille SET libelle = 'Coordonnées bancaires (n° de compte, banque, références, prêteur)'
WHERE famille = 'banque' AND libelle = 'Coordonnées bancaires (n° de compte, prêteur)';

INSERT INTO cloud_famille_colonne (famille, nom_table, colonne) VALUES
    ('banque', 'compte_tresorerie',     'banque'),
    ('banque', 'reglement_fournisseur', 'banque'),
    ('banque', 'reglement_fournisseur', 'reference'),
    ('banque', 'paiement_effet',        'reference'),
    ('banque', 'remise_cheques',        'reference'),
    ('banque', 'pret_bancaire',         'reference')
ON CONFLICT DO NOTHING;
