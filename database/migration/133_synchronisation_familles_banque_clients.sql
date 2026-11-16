-- =====================================================================
-- 133 — Synchronisation : familles nominatives complétées (2026-11-16,
-- décision du titulaire « ok proposition familles »,
-- docs/conception-synchronisation.md § 14).
--   * pharmacie (défaut oui) : + pharmacie.nom, .nif, .nis, .rc,
--     .article_imposition (identifiants fiscaux de l'officine, du même
--     ordre que NIF/NIS/RC : n° d'article d'imposition).
--   * banque (nouvelle, défaut NON) : compte_tresorerie.numero_compte,
--     pret_bancaire.preteur.
--   * clients_factures (nouvelle, défaut NON) : client_facture.adresse.
--   * parametre_formulaire_sms.texte : inchangé, toujours envoyé (modèle de
--     message, décision du titulaire).
-- Même liste : Pharma.Shared.FamillesNominativesSync et script central 007.
-- Idempotent ; aucune donnée métier modifiée ; synchronisation toujours
-- désactivée par défaut.
-- =====================================================================

ALTER TABLE sync_famille_colonne DROP CONSTRAINT IF EXISTS sync_famille_colonne_famille_check;
ALTER TABLE sync_famille_colonne ADD CONSTRAINT sync_famille_colonne_famille_check
    CHECK (famille IN ('employes', 'pharmacie', 'fournisseurs', 'courriel_commandes', 'banque', 'clients_factures'));

INSERT INTO sync_famille_colonne (famille, nom_table, colonne) VALUES
    ('pharmacie',        'pharmacie',         'nom'),
    ('pharmacie',        'pharmacie',         'nif'),
    ('pharmacie',        'pharmacie',         'nis'),
    ('pharmacie',        'pharmacie',         'rc'),
    ('pharmacie',        'pharmacie',         'article_imposition'),
    ('banque',           'compte_tresorerie', 'numero_compte'),
    ('banque',           'pret_bancaire',     'preteur'),
    ('clients_factures', 'client_facture',    'adresse')
ON CONFLICT DO NOTHING;
