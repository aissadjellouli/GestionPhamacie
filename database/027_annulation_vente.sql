-- =====================================================================
-- Annulation d'une vente encaissée
--   * vente récente (sa caisse est encore ouverte) : le caissier l'annule ;
--   * vente ancienne (caisse clôturée) : validation sur place par un
--     employé ayant le droit « caisse.annuler_vente » (code comptoir),
--     ou annulation directe par lui ;
--   * l'argent rendu sort de la caisse ouverte du poste qui annule
--     (la clôture d'une caisse passée n'est jamais modifiée) ;
--   * stock remis sur le même lot (mouvement retour_client, inscrit au
--     registre des psychotropes par 024), boîtes remises en stock avec
--     leur historique, crédit du patient diminué.
-- Idempotent. A exécuter après 026_ventes_attente.sql.
-- =====================================================================

CREATE TABLE IF NOT EXISTS annulation_vente (
    id                 bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    vente_id           bigint        NOT NULL UNIQUE REFERENCES vente(id),
    motif              text          NOT NULL CHECK (length(btrim(motif)) >= 3),
    utilisateur_id     bigint        NOT NULL REFERENCES utilisateur(id),   -- employé qui annule
    valide_par         bigint        REFERENCES utilisateur(id),            -- validation d'une vente ancienne
    ancienne           boolean       NOT NULL,                              -- caisse de la vente déjà clôturée
    mode               text          CHECK (mode IN ('especes', 'carte', 'cheque', 'credit')),
    montant_rembourse  numeric(12,2) NOT NULL DEFAULT 0 CHECK (montant_rembourse >= 0),
    session_caisse_id  bigint        REFERENCES session_caisse(id),         -- caisse d'où sort l'argent rendu
    poste              text,
    cree_le            timestamptz   NOT NULL DEFAULT now(),
    CHECK (NOT ancienne OR valide_par IS NOT NULL)
);
CREATE INDEX IF NOT EXISTS idx_annulation_session ON annulation_vente (session_caisse_id);
COMMENT ON TABLE annulation_vente IS 'Ventes annulées : motif, employé, validateur (vente ancienne), argent rendu et caisse concernée';
