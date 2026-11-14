-- =====================================================================
-- Remises automatiques et don partiel du reste à charge
--   * remise_automatique : pourcentage entier décidé par le titulaire (ou un
--     employé qui gère les remises automatiques) pour un produit ou une
--     catégorie, du date_debut au date_fin inclus ; arrêtée avant la fin
--     (arretee_le) plutôt que supprimée. Appliquée par la caisse en vente
--     libre, jamais sur un produit remboursable ; celle du produit l'emporte
--     sur celle de sa catégorie ;
--   * ligne_vente : remise automatique appliquée (%), réductions de la ligne
--     (automatique, du vendeur) et don_partiel (médicament : la réduction est
--     un don partiel du reste à charge, pas une remise) ;
--   * vente : remise du vendeur additionnée ou non aux remises automatiques,
--     montant du don partiel (montant_remise : remises hors médicaments).
-- Idempotent. A exécuter après 071_don_remise_par_personne.sql.
-- =====================================================================

CREATE TABLE IF NOT EXISTS remise_automatique (
    id          bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    produit_id  bigint REFERENCES produit (id),
    categorie   text REFERENCES categorie_produit (code),
    pourcentage numeric(5,2) NOT NULL,
    date_debut  date NOT NULL,
    date_fin    date NOT NULL,
    motif       text,
    cree_par    bigint NOT NULL REFERENCES utilisateur (id),
    cree_le     timestamptz NOT NULL DEFAULT now(),
    modifiee_par bigint REFERENCES utilisateur (id),
    modifiee_le timestamptz,
    arretee_par bigint REFERENCES utilisateur (id),
    arretee_le  timestamptz,
    CONSTRAINT ck_remise_auto_cible CHECK ((produit_id IS NULL) <> (categorie IS NULL)),
    CONSTRAINT ck_remise_auto_pct CHECK (pourcentage > 0 AND pourcentage <= 100 AND pourcentage = trunc(pourcentage)),
    CONSTRAINT ck_remise_auto_dates CHECK (date_fin >= date_debut),
    CONSTRAINT ck_remise_auto_motif CHECK (motif IS NULL OR length(motif) <= 200)
);
COMMENT ON TABLE remise_automatique IS 'Remises automatiques (%) par produit ou par catégorie, appliquées par la caisse en vente libre (jamais sur un produit remboursable)';

CREATE INDEX IF NOT EXISTS ix_remise_auto_produit ON remise_automatique (produit_id) WHERE produit_id IS NOT NULL;
CREATE INDEX IF NOT EXISTS ix_remise_auto_categorie ON remise_automatique (categorie) WHERE categorie IS NOT NULL;

-- Réductions de chaque ligne vendue
ALTER TABLE ligne_vente ADD COLUMN IF NOT EXISTS remise_auto_pct numeric(5,2) NOT NULL DEFAULT 0
    CONSTRAINT ck_ligne_vente_remise_auto CHECK (remise_auto_pct >= 0 AND remise_auto_pct <= 100);
ALTER TABLE ligne_vente ADD COLUMN IF NOT EXISTS reduction_auto numeric(12,2) NOT NULL DEFAULT 0
    CONSTRAINT ck_ligne_vente_reduction_auto CHECK (reduction_auto >= 0);
ALTER TABLE ligne_vente ADD COLUMN IF NOT EXISTS reduction_manuelle numeric(12,2) NOT NULL DEFAULT 0
    CONSTRAINT ck_ligne_vente_reduction_manuelle CHECK (reduction_manuelle >= 0);
ALTER TABLE ligne_vente ADD COLUMN IF NOT EXISTS don_partiel boolean NOT NULL DEFAULT false;
COMMENT ON COLUMN ligne_vente.remise_auto_pct IS 'Remise automatique appliquée à la ligne (%)';
COMMENT ON COLUMN ligne_vente.reduction_auto IS 'Réduction due à la remise automatique (DA), sur la part du patient hors SHP';
COMMENT ON COLUMN ligne_vente.reduction_manuelle IS 'Réduction due à la remise du vendeur (DA), sur la part du patient hors SHP';
COMMENT ON COLUMN ligne_vente.don_partiel IS 'Médicament : les réductions de la ligne sont un don partiel du reste à charge, pas une remise';

-- Totaux de la vente
ALTER TABLE vente ADD COLUMN IF NOT EXISTS remise_additionnee boolean NOT NULL DEFAULT false;
ALTER TABLE vente ADD COLUMN IF NOT EXISTS montant_don numeric(12,2) NOT NULL DEFAULT 0
    CONSTRAINT ck_vente_montant_don CHECK (montant_don >= 0);
COMMENT ON COLUMN vente.remise_additionnee IS 'La remise du vendeur s''ajoute aux remises automatiques (sinon elle ne vaut que pour les lignes sans remise automatique)';
COMMENT ON COLUMN vente.montant_don IS 'Don partiel du reste à charge (réductions sur les médicaments), en DA ; montant_remise : remises hors médicaments';
