-- =====================================================================
-- 135 — Synchronisation : famille « banque » élargie (2026-11-16,
-- décision du titulaire « ok banque », docs/conception-synchronisation.md § 14).
-- Défaut NON (inchangé, réglé au serveur central). Ajouts :
--   compte_tresorerie.banque, reglement_fournisseur.banque,
--   reglement_fournisseur.reference, paiement_effet.reference,
--   remise_cheques.reference, pret_bancaire.reference.
-- Vérifié : aucune de ces colonnes n'est dans la clé de sa table (clé = id)
-- ni utilisée par le serveur central pour une jointure ou un contrôle.
-- NON ajoutée : correspondance_echeancier_preteur.preteur, clé de sa ligne
-- (la retirer rendrait la ligne non identifiable au central).
-- Même liste : Pharma.Shared.FamillesNominativesSync et script central 008.
-- Idempotent ; aucune donnée métier modifiée.
-- =====================================================================

INSERT INTO sync_famille_colonne (famille, nom_table, colonne) VALUES
    ('banque', 'compte_tresorerie',     'banque'),
    ('banque', 'reglement_fournisseur', 'banque'),
    ('banque', 'reglement_fournisseur', 'reference'),
    ('banque', 'paiement_effet',        'reference'),
    ('banque', 'remise_cheques',        'reference'),
    ('banque', 'pret_bancaire',         'reference')
ON CONFLICT DO NOTHING;
