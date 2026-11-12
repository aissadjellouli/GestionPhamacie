SET client_encoding = 'UTF8';
-- =====================================================================
-- Réceptions en attente : saisie d'une livraison mise de côté (bouton
-- « Mettre en attente », ou automatiquement en quittant l'écran ou en se
-- déconnectant), gardée sur le serveur et reprise depuis n'importe quel
-- poste. Le contenu est celui de l'écran Réceptions (lignes, BL, facture,
-- grossiste, commande) ; rien n'entre en stock avant la validation.
-- Mode : reception, facture (facture d'un BL compté), comptage ou recompte
-- (réception à l'aveugle : reprise par le seul employé qui a compté).
-- Scans : scans du mobile repris dans la saisie, remis en attente d'import
-- si la réception en attente est abandonnée.
-- Idempotent. A exécuter après 068_verification_destinataire.sql.
-- =====================================================================

CREATE TABLE IF NOT EXISTS reception_attente (
    id              bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    poste           text          NOT NULL DEFAULT '',
    utilisateur_id  bigint        NOT NULL REFERENCES utilisateur(id),
    libelle         text          NOT NULL,
    mode            text          NOT NULL DEFAULT 'reception',
    lignes          int           NOT NULL CHECK (lignes >= 0),
    contenu         jsonb         NOT NULL,
    scans           bigint[]      NOT NULL DEFAULT '{}',
    cree_le         timestamptz   NOT NULL DEFAULT now()
);

-- Liste de modes : posée une seule fois (jamais recréée sans garde)
DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'reception_attente_mode_check') THEN
        ALTER TABLE reception_attente ADD CONSTRAINT reception_attente_mode_check
            CHECK (mode IN ('reception', 'facture', 'comptage', 'recompte'));
    END IF;
END $$;

CREATE INDEX IF NOT EXISTS idx_reception_attente_cree ON reception_attente (cree_le);
COMMENT ON TABLE reception_attente IS 'Réceptions de livraison mises en attente (contenu de l''écran Réceptions, repris puis supprimé)';
