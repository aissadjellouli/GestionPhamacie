-- =====================================================================
-- 165 -- Bascule du 2e serveur, lot V2-1b : présence des postes
--   Plan : docs/taches/2026-11-23-bascule-2e-serveur/plan-vague-2.md § 18 et contrat-api.md § 3.1. Une ligne par poste : son nom et
--   l'instant où le serveur principal l'a vu pour la dernière fois (en-tête X-Pharma-Poste de ses requêtes, noté au plus une fois par
--   minute et par poste). La table est recopiée sur le 2e serveur par la réplication PostgreSQL (comme toute la base) : les deux serveurs
--   y lisent la même liste de « postes connus » (vus dans les 3 dernières minutes). connexion_employe ne convenait pas : elle n'a que
--   l'instant de la connexion, jamais mis à jour.
--   Table classée « jamais » dans sync_table : la présence des postes n'est jamais envoyée au serveur central.
--   Aucune donnée existante modifiée. Le script n'écrit rien dans la table.
--   Retour arrière : database/migration/retour/165_presence_postes_retour.sql (supprime la table et son classement ; les lignes sont
--   perdues, c'est voulu : elles se reconstituent en une minute).
-- Idempotent et rejouable sur une base plus récente.
-- A exécuter après 164_bascule_saut_compteurs_promotion.sql.
-- =====================================================================

-- Fichier en UTF-8 : lu comme tel quel que soit l'encodage du poste qui lance psql.
SET client_encoding = 'UTF8';

CREATE TABLE IF NOT EXISTS presence_poste (
    poste   varchar(100) PRIMARY KEY,
    vu_le   timestamptz  NOT NULL DEFAULT now(),
    CONSTRAINT ck_presence_poste_nom CHECK (length(btrim(poste)) > 0)
);
CREATE INDEX IF NOT EXISTS ix_presence_poste_vu_le ON presence_poste (vu_le);
COMMENT ON TABLE presence_poste IS 'Présence des postes : dernier instant où le serveur principal a vu chaque poste (en-tête X-Pharma-Poste), recopiée sur le 2e serveur pour la règle de majorité ; jamais envoyée au central (script 165).';

-- Classement pour la synchronisation : jamais envoyée au serveur central
INSERT INTO sync_table (nom_table, sens, cle_colonnes, colonnes_exclues, colonnes_patient, colonnes_texte, porteur, motif) VALUES
    ('presence_poste', 'jamais', '{poste}', '{}', '{}', '{}', false, 'Présence des postes vue par le serveur principal (règle de majorité du 2e serveur), script 165')
ON CONFLICT (nom_table) DO NOTHING;
