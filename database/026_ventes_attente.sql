-- =====================================================================
-- Ventes en attente : paniers mis de côté sur la caisse du poste, gardés
-- sur le serveur (coupure, redémarrage) et repris par n'importe quel
-- employé du poste. Le contenu est celui du panier du poste ; les prix et
-- le stock sont recalculés par le serveur à la reprise et à l'encaissement.
-- Idempotent. A exécuter après 025_affichage.sql.
-- =====================================================================

CREATE TABLE IF NOT EXISTS panier_attente (
    id              bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    poste           text          NOT NULL,
    utilisateur_id  bigint        NOT NULL REFERENCES utilisateur(id),
    libelle         text          NOT NULL,
    lignes          int           NOT NULL CHECK (lignes > 0),
    total           numeric(12,2) NOT NULL DEFAULT 0,
    contenu         jsonb         NOT NULL,
    cree_le         timestamptz   NOT NULL DEFAULT now()
);
CREATE INDEX IF NOT EXISTS idx_panier_attente_poste ON panier_attente (poste, cree_le);
COMMENT ON TABLE panier_attente IS 'Ventes en attente de la caisse d''un poste (contenu du panier, repris puis supprimé)';
