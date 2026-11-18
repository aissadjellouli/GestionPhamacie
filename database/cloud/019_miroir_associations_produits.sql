-- =====================================================================
-- Base centrale : copies des associations faites dans l'officine sur ses
-- produits (2026-11-18, « go reclassement » ; docs/conception-
-- synchronisation.md § 29). Script de la base CENTRALE, à passer après 018
-- par le propriétaire, et AVANT le script 147 des officines (un lot qui
-- contient une table inconnue du central est refusé en entier).
--   * chifa_code_produit (clé code) et produit_indication (clé produit_id,
--     indication_id) : tables miroir cloisonnées par officine, comme celles
--     du script 002 ; reçues désormais en voie montante (copie de
--     sauvegarde pour la restauration d'une officine, vues multi-officines).
-- Idempotent. Aucune donnée existante modifiée.
-- =====================================================================

SET client_encoding = 'UTF8';

CREATE TABLE IF NOT EXISTS public."chifa_code_produit" (
    officine_id uuid NOT NULL,
    "code" text NOT NULL,
    sync_epoque integer NOT NULL,
    sync_numero bigint NOT NULL,
    sync_recu_le timestamp with time zone DEFAULT now() NOT NULL,
    PRIMARY KEY (officine_id, "code")
);
ALTER TABLE public."chifa_code_produit" ADD COLUMN IF NOT EXISTS "produit_id" bigint;
ALTER TABLE public."chifa_code_produit" ADD COLUMN IF NOT EXISTS "associe_par" bigint;
ALTER TABLE public."chifa_code_produit" ADD COLUMN IF NOT EXISTS "associe_le" timestamp with time zone;
ALTER TABLE public."chifa_code_produit" ADD COLUMN IF NOT EXISTS sync_chargement integer;
ALTER TABLE public."chifa_code_produit" ADD COLUMN IF NOT EXISTS sync_absente_le timestamp with time zone;
ALTER TABLE public."chifa_code_produit" ENABLE ROW LEVEL SECURITY;
ALTER TABLE public."chifa_code_produit" FORCE ROW LEVEL SECURITY;
DROP POLICY IF EXISTS cloisonnement ON public."chifa_code_produit";
CREATE POLICY cloisonnement ON public."chifa_code_produit" USING (officine_id = cloud_officine_courante()) WITH CHECK (officine_id = cloud_officine_courante());
GRANT SELECT, INSERT, UPDATE, DELETE ON public."chifa_code_produit" TO pharma_cloud_api;
INSERT INTO cloud_table_miroir (nom_table, cle, colonnes) VALUES ('chifa_code_produit', '{code}', '{code,produit_id,associe_par,associe_le,officine_id}')
    ON CONFLICT (nom_table) DO UPDATE SET cle = EXCLUDED.cle, colonnes = EXCLUDED.colonnes;

CREATE TABLE IF NOT EXISTS public."produit_indication" (
    officine_id uuid NOT NULL,
    "produit_id" bigint NOT NULL,
    "indication_id" bigint NOT NULL,
    sync_epoque integer NOT NULL,
    sync_numero bigint NOT NULL,
    sync_recu_le timestamp with time zone DEFAULT now() NOT NULL,
    PRIMARY KEY (officine_id, "produit_id", "indication_id")
);
ALTER TABLE public."produit_indication" ADD COLUMN IF NOT EXISTS "cree_le" timestamp with time zone;
ALTER TABLE public."produit_indication" ADD COLUMN IF NOT EXISTS sync_chargement integer;
ALTER TABLE public."produit_indication" ADD COLUMN IF NOT EXISTS sync_absente_le timestamp with time zone;
ALTER TABLE public."produit_indication" ENABLE ROW LEVEL SECURITY;
ALTER TABLE public."produit_indication" FORCE ROW LEVEL SECURITY;
DROP POLICY IF EXISTS cloisonnement ON public."produit_indication";
CREATE POLICY cloisonnement ON public."produit_indication" USING (officine_id = cloud_officine_courante()) WITH CHECK (officine_id = cloud_officine_courante());
GRANT SELECT, INSERT, UPDATE, DELETE ON public."produit_indication" TO pharma_cloud_api;
INSERT INTO cloud_table_miroir (nom_table, cle, colonnes) VALUES ('produit_indication', '{produit_id,indication_id}', '{produit_id,indication_id,cree_le,officine_id}')
    ON CONFLICT (nom_table) DO UPDATE SET cle = EXCLUDED.cle, colonnes = EXCLUDED.colonnes;
