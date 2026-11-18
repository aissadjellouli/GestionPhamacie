-- =====================================================================
-- 146 : synchronisation, commandes en brouillon préparées par le serveur
-- central (lot S5.4, 2026-11-18 ; docs/conception-synchronisation.md § 27).
--   * commande_ordre_central (jamais envoyée) : lien entre une commande
--     fournisseur créée en brouillon par un ordre « commande » et cet
--     ordre (n° d'ordre unique : un même ordre ne crée jamais deux
--     commandes), auteur au central. Sert à l'affichage « préparé par le
--     serveur central » et à garder ces brouillons à part : un produit
--     ajouté depuis l'écran ne va jamais dans un brouillon du central.
--   * commande_fournisseur n'est pas modifiée (la remontée des commandes
--     au central est inchangée).
-- Idempotent. Aucune donnée existante modifiée.
-- =====================================================================

-- Fichier en UTF-8 : lu comme tel quel que soit l'encodage du poste qui lance psql.
SET client_encoding = 'UTF8';

CREATE TABLE IF NOT EXISTS commande_ordre_central (
    commande_id  bigint      PRIMARY KEY REFERENCES commande_fournisseur (id),
    ordre_id     uuid        NOT NULL UNIQUE REFERENCES sync_ordre_recu (id),
    auteur       text        NOT NULL CHECK (length(auteur) BETWEEN 1 AND 100),
    cree_le      timestamptz NOT NULL DEFAULT now()
);
COMMENT ON TABLE commande_ordre_central IS 'Commandes créées en brouillon par un ordre du serveur central (lot S5.4) ; jamais envoyée (script 146).';

INSERT INTO sync_table (nom_table, sens, colonnes_exclues, colonnes_patient, porteur, motif) VALUES
    ('commande_ordre_central', 'jamais', '{}', '{}', false, 'Lien commande / ordre du serveur central (local), script 146')
ON CONFLICT (nom_table) DO NOTHING;
UPDATE sync_table SET cle_colonnes = '{commande_id}'
WHERE nom_table = 'commande_ordre_central' AND cle_colonnes IS DISTINCT FROM '{commande_id}'::text[];

-- Droits de pharma_app (propriétaire en développement ; après la séparation des rôles, droits par défaut de 111b)
DO $droits$
BEGIN
    IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'pharma_app')
       AND NOT has_table_privilege('pharma_app', 'public.commande_ordre_central', 'SELECT, INSERT') THEN
        GRANT SELECT, INSERT ON commande_ordre_central TO pharma_app;
    END IF;
END $droits$;
