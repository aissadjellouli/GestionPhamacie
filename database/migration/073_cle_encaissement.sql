-- =====================================================================
-- Clé d'encaissement : une même vente n'est jamais enregistrée deux fois
--   * vente.cle_encaissement : identifiant créé par le poste pour chaque
--     panier (renouvelé après une vente réussie, un panier vidé ou mis en
--     attente) et envoyé avec l'encaissement. Si le poste renvoie le même
--     panier (réponse du serveur arrivée après le délai d'attente du poste,
--     nouvel essai), le serveur rend la vente déjà enregistrée au lieu d'en
--     créer une seconde ;
--   * index unique partiel : deux encaissements simultanés du même panier ne
--     peuvent pas créer deux ventes. Les ventes anciennes (sans clé) ne sont
--     pas concernées.
-- Idempotent. A exécuter après 072_remises_automatiques.sql.
-- =====================================================================

ALTER TABLE vente ADD COLUMN IF NOT EXISTS cle_encaissement uuid;
COMMENT ON COLUMN vente.cle_encaissement IS 'Clé du panier envoyée par le poste : un nouvel essai du même encaissement rend la vente déjà enregistrée (pas de doublon)';

CREATE UNIQUE INDEX IF NOT EXISTS ux_vente_cle_encaissement ON vente (cle_encaissement) WHERE cle_encaissement IS NOT NULL;
