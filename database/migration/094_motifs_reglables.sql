-- =====================================================================
-- Motifs réglables par le titulaire (2026-11-15)
--   * motif_reglable : une table pour plusieurs listes, réglées dans
--     Administration › Motifs et catégories (droit admin.parametres) :
--       - tiroir             : ouverture du tiroir-caisse sans vente (F8) ;
--       - attente            : mise en attente d'une vente ;
--       - charge             : catégories de dépenses de la trésorerie
--                              (reprend les 8 codes jusqu'ici fixes) ;
--       - sortie_stock       : sorties diverses (périmé, cassé, don) ;
--       - retour_fournisseur : motifs de retour au grossiste.
--     Les codes des sorties et des retours restent ceux des contraintes
--     existantes (mouvement_stock, retour_fournisseur) : seuls le libellé
--     et l'activation sont réglables, aucun ajout.
--   * systeme = true : motif indispensable (« Autre », périmé…), jamais
--     désactivé ; aucune suppression d'un motif (désactivation seulement).
--   * panier_attente : motif de la mise en attente (code, texte de « Autre »)
--     et produits dus au patient (motif « produit manquant »).
--   * le motif de l'ouverture du tiroir est écrit dans evenement_caisse.detail
--     (colonne existante) et au journal.
-- Valeurs initiales insérées seulement si absentes ; une réexécution ne
-- modifie jamais un libellé ou une activation réglés ensuite.
-- Aucune donnée supprimée. Idempotent. A exécuter après 093_droit_doublons_import.sql.
-- =====================================================================

CREATE TABLE IF NOT EXISTS motif_reglable (
    id          bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    liste       text        NOT NULL CHECK (liste IN ('tiroir', 'attente', 'charge', 'sortie_stock', 'retour_fournisseur')),
    code        text        NOT NULL CHECK (code ~ '^[a-z0-9_]{1,40}$'),
    libelle     text        NOT NULL CHECK (length(btrim(libelle)) BETWEEN 1 AND 80),
    actif       boolean     NOT NULL DEFAULT true,
    systeme     boolean     NOT NULL DEFAULT false,
    ordre       integer     NOT NULL DEFAULT 100,
    cree_le     timestamptz NOT NULL DEFAULT now(),
    modifie_le  timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT uq_motif_reglable_code UNIQUE (liste, code),
    CONSTRAINT ck_motif_reglable_systeme_actif CHECK (actif OR NOT systeme)
);
COMMENT ON TABLE motif_reglable IS 'Motifs et catégories réglables par le titulaire (tiroir, mise en attente, charges, sorties de stock, retours) ; désactivés, jamais supprimés';

INSERT INTO motif_reglable (liste, code, libelle, systeme, ordre) VALUES
    ('tiroir', 'monnaie',            'Faire de la monnaie',                   false, 10),
    ('tiroir', 'erreur_rendu',       'Erreur de rendu de monnaie',            false, 20),
    ('tiroir', 'depot_especes',      'Dépôt ou retrait d''espèces',           false, 30),
    ('tiroir', 'verification',       'Vérification du contenu',               false, 40),
    ('tiroir', 'autre',              'Autre (préciser)',                      true,  999),
    ('attente', 'produit_manquant',  'Produit manquant à commander',          false, 10),
    ('attente', 'patient_revient',   'Patient revient payer',                 false, 20),
    ('attente', 'attente_ordonnance','Attente de l''ordonnance',              false, 30),
    ('attente', 'autre',             'Autre (préciser)',                      true,  999),
    ('charge', 'loyer',              'Loyer et charges',                      false, 10),
    ('charge', 'energie',            'Électricité, eau, gaz',                 false, 20),
    ('charge', 'salaires',           'Salaires et cotisations',               false, 30),
    ('charge', 'impots',             'Impôts et taxes',                       false, 40),
    ('charge', 'fournitures',        'Fournitures et consommables',           false, 50),
    ('charge', 'entretien',          'Entretien et réparations',              false, 60),
    ('charge', 'charges_financieres','Charges financières (intérêts d’emprunt)', true, 70),
    ('charge', 'autre',              'Autre dépense',                         true,  999),
    ('sortie_stock', 'perime',       'Périmé',                                true,  10),
    ('sortie_stock', 'casse',        'Cassé ou abîmé',                        true,  20),
    ('sortie_stock', 'echantillon',  'Échantillon, gratuité ou don',          true,  30),
    ('retour_fournisseur', 'peremption_proche', 'Péremption proche',          false, 10),
    ('retour_fournisseur', 'perime',            'Périmé',                     true,  20),
    ('retour_fournisseur', 'defaut',            'Défaut',                     false, 30),
    ('retour_fournisseur', 'erreur_livraison',  'Erreur de livraison',        false, 40),
    ('retour_fournisseur', 'rappel_lot',        'Rappel de lot',              true,  50)
ON CONFLICT (liste, code) DO NOTHING;

-- Motif de mise en attente et produits dus (anciennes ventes en attente : sans motif)
ALTER TABLE panier_attente ADD COLUMN IF NOT EXISTS motif_code   text;
ALTER TABLE panier_attente ADD COLUMN IF NOT EXISTS motif_texte  text;
ALTER TABLE panier_attente ADD COLUMN IF NOT EXISTS produits_dus jsonb;
COMMENT ON COLUMN panier_attente.motif_code IS 'Motif de la mise en attente (motif_reglable, liste attente) ; null : mise en attente automatique (départ de la caisse, changement d''utilisateur) ou antérieure à 094';
COMMENT ON COLUMN panier_attente.produits_dus IS 'Produits dus au patient (motif produit_manquant) : [{"ProduitId","Nom","Quantite"}] ; aucune commande créée';
