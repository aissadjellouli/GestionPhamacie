-- =====================================================================
-- Base centrale : familles nominatives complétées (2026-11-16, décision du
-- titulaire, docs/conception-synchronisation.md § 14). Même liste que
-- l'officine (script 133) et Pharma.Shared.FamillesNominativesSync.
--   * pharmacie (défaut oui) : + nom, nif, nis, rc, article_imposition.
--   * banque (nouvelle, défaut NON) : compte_tresorerie.numero_compte,
--     pret_bancaire.preteur.
--   * clients_factures (nouvelle, défaut NON) : client_facture.adresse.
-- Le réglage effectif descend aux officines au prochain échange (005) ; les
-- colonnes non autorisées sont effacées à la réception (004/006).
-- Idempotent (un réglage déjà fait n'est jamais écrasé) ; à passer après 006.
-- =====================================================================

INSERT INTO cloud_famille (famille, libelle, envoyee) VALUES
    ('banque',           'Coordonnées bancaires (n° de compte, prêteur)', false),
    ('clients_factures', 'Adresse des clients facturés',                  false)
ON CONFLICT (famille) DO NOTHING;
UPDATE cloud_famille SET libelle = 'Nom, titulaire, identifiants fiscaux et coordonnées de la pharmacie'
WHERE famille = 'pharmacie' AND libelle = 'Titulaire et coordonnées de la pharmacie';

INSERT INTO cloud_famille_colonne (famille, nom_table, colonne) VALUES
    ('pharmacie',        'pharmacie',         'nom'),
    ('pharmacie',        'pharmacie',         'nif'),
    ('pharmacie',        'pharmacie',         'nis'),
    ('pharmacie',        'pharmacie',         'rc'),
    ('pharmacie',        'pharmacie',         'article_imposition'),
    ('banque',           'compte_tresorerie', 'numero_compte'),
    ('banque',           'pret_bancaire',     'preteur'),
    ('clients_factures', 'client_facture',    'adresse')
ON CONFLICT DO NOTHING;
