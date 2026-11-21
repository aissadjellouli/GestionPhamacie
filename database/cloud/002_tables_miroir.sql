-- =====================================================================
-- Base centrale : tables miroir des tables envoyées par les officines.
-- FICHIER GÉNÉRÉ, ne pas modifier à la main :
--   Pharma.Cloud.Api.exe --generer-miroir <racine du dépôt>
-- Source : database/init/schema.sql (schéma local) et classement
-- sync_table (database/migration 126 et suivants). Test d'alignement :
-- tests/Pharma.Cloud.Tests/SchemaMiroirTests.cs.
-- Mêmes tables et colonnes que la base locale (dont officine_id), sauf
-- colonnes jamais envoyées (exclues, porteur remplacé par porteur_code,
-- patients sous code P-… donc en texte) ; clé = (officine_id, clé
-- locale) ; colonnes sync_epoque, sync_numero, sync_recu_le (état final
-- = ligne de plus grand n°), sync_chargement et sync_absente_le
-- (rechargement complet, script 003). RLS forcée par officine. Idempotent ; une
-- colonne ajoutée localement est ajoutée ici (ADD COLUMN IF NOT EXISTS) ;
-- un changement de type se traite par un script central à part.
-- =====================================================================

CREATE TABLE IF NOT EXISTS public."alerte" (
    officine_id uuid NOT NULL,
    "id" bigint NOT NULL,
    sync_epoque integer NOT NULL,
    sync_numero bigint NOT NULL,
    sync_recu_le timestamp with time zone DEFAULT now() NOT NULL,
    PRIMARY KEY (officine_id, "id")
);
ALTER TABLE public."alerte" ADD COLUMN IF NOT EXISTS "type" text;
ALTER TABLE public."alerte" ADD COLUMN IF NOT EXISTS "niveau" text;
ALTER TABLE public."alerte" ADD COLUMN IF NOT EXISTS "produit_id" bigint;
ALTER TABLE public."alerte" ADD COLUMN IF NOT EXISTS "lot_id" bigint;
ALTER TABLE public."alerte" ADD COLUMN IF NOT EXISTS "message" text;
ALTER TABLE public."alerte" ADD COLUMN IF NOT EXISTS "valeur_dzd" numeric(12,2);
ALTER TABLE public."alerte" ADD COLUMN IF NOT EXISTS "statut" text;
ALTER TABLE public."alerte" ADD COLUMN IF NOT EXISTS "reportee_jusqu_au" date;
ALTER TABLE public."alerte" ADD COLUMN IF NOT EXISTS "action_prise" text;
ALTER TABLE public."alerte" ADD COLUMN IF NOT EXISTS "cree_le" timestamp with time zone;
ALTER TABLE public."alerte" ADD COLUMN IF NOT EXISTS "mise_a_jour_le" timestamp with time zone;
ALTER TABLE public."alerte" ADD COLUMN IF NOT EXISTS "traitee_par" bigint;
ALTER TABLE public."alerte" ADD COLUMN IF NOT EXISTS "traitee_le" timestamp with time zone;
ALTER TABLE public."alerte" ADD COLUMN IF NOT EXISTS sync_chargement integer;
ALTER TABLE public."alerte" ADD COLUMN IF NOT EXISTS sync_absente_le timestamp with time zone;
ALTER TABLE public."alerte" ENABLE ROW LEVEL SECURITY;
ALTER TABLE public."alerte" FORCE ROW LEVEL SECURITY;
DROP POLICY IF EXISTS cloisonnement ON public."alerte";
CREATE POLICY cloisonnement ON public."alerte" USING (officine_id = cloud_officine_courante()) WITH CHECK (officine_id = cloud_officine_courante());
GRANT SELECT, INSERT, UPDATE, DELETE ON public."alerte" TO pharma_cloud_api;
INSERT INTO cloud_table_miroir (nom_table, cle, colonnes) VALUES ('alerte', '{id}', '{id,type,niveau,produit_id,lot_id,message,valeur_dzd,statut,reportee_jusqu_au,action_prise,cree_le,mise_a_jour_le,traitee_par,traitee_le,officine_id}')
    ON CONFLICT (nom_table) DO UPDATE SET cle = EXCLUDED.cle, colonnes = EXCLUDED.colonnes;

CREATE TABLE IF NOT EXISTS public."alerte_surveillance" (
    officine_id uuid NOT NULL,
    "id" bigint NOT NULL,
    sync_epoque integer NOT NULL,
    sync_numero bigint NOT NULL,
    sync_recu_le timestamp with time zone DEFAULT now() NOT NULL,
    PRIMARY KEY (officine_id, "id")
);
ALTER TABLE public."alerte_surveillance" ADD COLUMN IF NOT EXISTS "type" text;
ALTER TABLE public."alerte_surveillance" ADD COLUMN IF NOT EXISTS "niveau" text;
ALTER TABLE public."alerte_surveillance" ADD COLUMN IF NOT EXISTS "utilisateur_id" bigint;
ALTER TABLE public."alerte_surveillance" ADD COLUMN IF NOT EXISTS "jour" date;
ALTER TABLE public."alerte_surveillance" ADD COLUMN IF NOT EXISTS "cle" text;
ALTER TABLE public."alerte_surveillance" ADD COLUMN IF NOT EXISTS "message" text;
ALTER TABLE public."alerte_surveillance" ADD COLUMN IF NOT EXISTS "valeur" numeric(12,2);
ALTER TABLE public."alerte_surveillance" ADD COLUMN IF NOT EXISTS "statut" text;
ALTER TABLE public."alerte_surveillance" ADD COLUMN IF NOT EXISTS "action_prise" text;
ALTER TABLE public."alerte_surveillance" ADD COLUMN IF NOT EXISTS "cree_le" timestamp with time zone;
ALTER TABLE public."alerte_surveillance" ADD COLUMN IF NOT EXISTS "mise_a_jour_le" timestamp with time zone;
ALTER TABLE public."alerte_surveillance" ADD COLUMN IF NOT EXISTS "traitee_par" bigint;
ALTER TABLE public."alerte_surveillance" ADD COLUMN IF NOT EXISTS "traitee_le" timestamp with time zone;
ALTER TABLE public."alerte_surveillance" ADD COLUMN IF NOT EXISTS sync_chargement integer;
ALTER TABLE public."alerte_surveillance" ADD COLUMN IF NOT EXISTS sync_absente_le timestamp with time zone;
ALTER TABLE public."alerte_surveillance" ENABLE ROW LEVEL SECURITY;
ALTER TABLE public."alerte_surveillance" FORCE ROW LEVEL SECURITY;
DROP POLICY IF EXISTS cloisonnement ON public."alerte_surveillance";
CREATE POLICY cloisonnement ON public."alerte_surveillance" USING (officine_id = cloud_officine_courante()) WITH CHECK (officine_id = cloud_officine_courante());
GRANT SELECT, INSERT, UPDATE, DELETE ON public."alerte_surveillance" TO pharma_cloud_api;
INSERT INTO cloud_table_miroir (nom_table, cle, colonnes) VALUES ('alerte_surveillance', '{id}', '{id,type,niveau,utilisateur_id,jour,cle,message,valeur,statut,action_prise,cree_le,mise_a_jour_le,traitee_par,traitee_le,officine_id}')
    ON CONFLICT (nom_table) DO UPDATE SET cle = EXCLUDED.cle, colonnes = EXCLUDED.colonnes;

CREATE TABLE IF NOT EXISTS public."annulation_registre" (
    officine_id uuid NOT NULL,
    "id" bigint NOT NULL,
    sync_epoque integer NOT NULL,
    sync_numero bigint NOT NULL,
    sync_recu_le timestamp with time zone DEFAULT now() NOT NULL,
    PRIMARY KEY (officine_id, "id")
);
ALTER TABLE public."annulation_registre" ADD COLUMN IF NOT EXISTS "registre_id" bigint;
ALTER TABLE public."annulation_registre" ADD COLUMN IF NOT EXISTS "motif" text;
ALTER TABLE public."annulation_registre" ADD COLUMN IF NOT EXISTS "utilisateur_id" bigint;
ALTER TABLE public."annulation_registre" ADD COLUMN IF NOT EXISTS "cree_le" timestamp with time zone;
ALTER TABLE public."annulation_registre" ADD COLUMN IF NOT EXISTS sync_chargement integer;
ALTER TABLE public."annulation_registre" ADD COLUMN IF NOT EXISTS sync_absente_le timestamp with time zone;
ALTER TABLE public."annulation_registre" ENABLE ROW LEVEL SECURITY;
ALTER TABLE public."annulation_registre" FORCE ROW LEVEL SECURITY;
DROP POLICY IF EXISTS cloisonnement ON public."annulation_registre";
CREATE POLICY cloisonnement ON public."annulation_registre" USING (officine_id = cloud_officine_courante()) WITH CHECK (officine_id = cloud_officine_courante());
GRANT SELECT, INSERT, UPDATE, DELETE ON public."annulation_registre" TO pharma_cloud_api;
INSERT INTO cloud_table_miroir (nom_table, cle, colonnes) VALUES ('annulation_registre', '{id}', '{id,registre_id,motif,utilisateur_id,cree_le,officine_id}')
    ON CONFLICT (nom_table) DO UPDATE SET cle = EXCLUDED.cle, colonnes = EXCLUDED.colonnes;

CREATE TABLE IF NOT EXISTS public."annulation_vente" (
    officine_id uuid NOT NULL,
    "id" bigint NOT NULL,
    sync_epoque integer NOT NULL,
    sync_numero bigint NOT NULL,
    sync_recu_le timestamp with time zone DEFAULT now() NOT NULL,
    PRIMARY KEY (officine_id, "id")
);
ALTER TABLE public."annulation_vente" ADD COLUMN IF NOT EXISTS "vente_id" bigint;
ALTER TABLE public."annulation_vente" ADD COLUMN IF NOT EXISTS "motif" text;
ALTER TABLE public."annulation_vente" ADD COLUMN IF NOT EXISTS "utilisateur_id" bigint;
ALTER TABLE public."annulation_vente" ADD COLUMN IF NOT EXISTS "valide_par" bigint;
ALTER TABLE public."annulation_vente" ADD COLUMN IF NOT EXISTS "ancienne" boolean;
ALTER TABLE public."annulation_vente" ADD COLUMN IF NOT EXISTS "mode" text;
ALTER TABLE public."annulation_vente" ADD COLUMN IF NOT EXISTS "montant_rembourse" numeric(12,2);
ALTER TABLE public."annulation_vente" ADD COLUMN IF NOT EXISTS "session_caisse_id" bigint;
ALTER TABLE public."annulation_vente" ADD COLUMN IF NOT EXISTS "poste" text;
ALTER TABLE public."annulation_vente" ADD COLUMN IF NOT EXISTS "cree_le" timestamp with time zone;
ALTER TABLE public."annulation_vente" ADD COLUMN IF NOT EXISTS sync_chargement integer;
ALTER TABLE public."annulation_vente" ADD COLUMN IF NOT EXISTS sync_absente_le timestamp with time zone;
ALTER TABLE public."annulation_vente" ENABLE ROW LEVEL SECURITY;
ALTER TABLE public."annulation_vente" FORCE ROW LEVEL SECURITY;
DROP POLICY IF EXISTS cloisonnement ON public."annulation_vente";
CREATE POLICY cloisonnement ON public."annulation_vente" USING (officine_id = cloud_officine_courante()) WITH CHECK (officine_id = cloud_officine_courante());
GRANT SELECT, INSERT, UPDATE, DELETE ON public."annulation_vente" TO pharma_cloud_api;
INSERT INTO cloud_table_miroir (nom_table, cle, colonnes) VALUES ('annulation_vente', '{id}', '{id,vente_id,motif,utilisateur_id,valide_par,ancienne,mode,montant_rembourse,session_caisse_id,poste,cree_le,officine_id}')
    ON CONFLICT (nom_table) DO UPDATE SET cle = EXCLUDED.cle, colonnes = EXCLUDED.colonnes;

CREATE TABLE IF NOT EXISTS public."avoir_mouvement" (
    officine_id uuid NOT NULL,
    "id" bigint NOT NULL,
    sync_epoque integer NOT NULL,
    sync_numero bigint NOT NULL,
    sync_recu_le timestamp with time zone DEFAULT now() NOT NULL,
    PRIMARY KEY (officine_id, "id")
);
ALTER TABLE public."avoir_mouvement" ADD COLUMN IF NOT EXISTS "patient_id" text;
ALTER TABLE public."avoir_mouvement" ADD COLUMN IF NOT EXISTS "sens" text;
ALTER TABLE public."avoir_mouvement" ADD COLUMN IF NOT EXISTS "montant" numeric(12,2);
ALTER TABLE public."avoir_mouvement" ADD COLUMN IF NOT EXISTS "origine" text;
ALTER TABLE public."avoir_mouvement" ADD COLUMN IF NOT EXISTS "vente_id" bigint;
ALTER TABLE public."avoir_mouvement" ADD COLUMN IF NOT EXISTS "paiement_id" bigint;
ALTER TABLE public."avoir_mouvement" ADD COLUMN IF NOT EXISTS "mouvement_tiroir_id" bigint;
ALTER TABLE public."avoir_mouvement" ADD COLUMN IF NOT EXISTS "motif" text;
ALTER TABLE public."avoir_mouvement" ADD COLUMN IF NOT EXISTS "utilisateur_id" bigint;
ALTER TABLE public."avoir_mouvement" ADD COLUMN IF NOT EXISTS "cree_le" timestamp with time zone;
ALTER TABLE public."avoir_mouvement" ADD COLUMN IF NOT EXISTS sync_chargement integer;
ALTER TABLE public."avoir_mouvement" ADD COLUMN IF NOT EXISTS sync_absente_le timestamp with time zone;
ALTER TABLE public."avoir_mouvement" ENABLE ROW LEVEL SECURITY;
ALTER TABLE public."avoir_mouvement" FORCE ROW LEVEL SECURITY;
DROP POLICY IF EXISTS cloisonnement ON public."avoir_mouvement";
CREATE POLICY cloisonnement ON public."avoir_mouvement" USING (officine_id = cloud_officine_courante()) WITH CHECK (officine_id = cloud_officine_courante());
GRANT SELECT, INSERT, UPDATE, DELETE ON public."avoir_mouvement" TO pharma_cloud_api;
INSERT INTO cloud_table_miroir (nom_table, cle, colonnes) VALUES ('avoir_mouvement', '{id}', '{id,patient_id,sens,montant,origine,vente_id,paiement_id,mouvement_tiroir_id,motif,utilisateur_id,cree_le,officine_id}')
    ON CONFLICT (nom_table) DO UPDATE SET cle = EXCLUDED.cle, colonnes = EXCLUDED.colonnes;

CREATE TABLE IF NOT EXISTS public."boite" (
    officine_id uuid NOT NULL,
    "id" bigint NOT NULL,
    sync_epoque integer NOT NULL,
    sync_numero bigint NOT NULL,
    sync_recu_le timestamp with time zone DEFAULT now() NOT NULL,
    PRIMARY KEY (officine_id, "id")
);
ALTER TABLE public."boite" ADD COLUMN IF NOT EXISTS "lot_id" bigint;
ALTER TABLE public."boite" ADD COLUMN IF NOT EXISTS "gtin" text;
ALTER TABLE public."boite" ADD COLUMN IF NOT EXISTS "numero_serie" text;
ALTER TABLE public."boite" ADD COLUMN IF NOT EXISTS "statut" text;
ALTER TABLE public."boite" ADD COLUMN IF NOT EXISTS "ligne_vente_id" bigint;
ALTER TABLE public."boite" ADD COLUMN IF NOT EXISTS "scannee_le" timestamp with time zone;
ALTER TABLE public."boite" ADD COLUMN IF NOT EXISTS "code_etiquette" text;
ALTER TABLE public."boite" ADD COLUMN IF NOT EXISTS "reception_id" bigint;
ALTER TABLE public."boite" ADD COLUMN IF NOT EXISTS "prix_achat_ht" numeric(12,2);
ALTER TABLE public."boite" ADD COLUMN IF NOT EXISTS sync_chargement integer;
ALTER TABLE public."boite" ADD COLUMN IF NOT EXISTS sync_absente_le timestamp with time zone;
ALTER TABLE public."boite" ENABLE ROW LEVEL SECURITY;
ALTER TABLE public."boite" FORCE ROW LEVEL SECURITY;
DROP POLICY IF EXISTS cloisonnement ON public."boite";
CREATE POLICY cloisonnement ON public."boite" USING (officine_id = cloud_officine_courante()) WITH CHECK (officine_id = cloud_officine_courante());
GRANT SELECT, INSERT, UPDATE, DELETE ON public."boite" TO pharma_cloud_api;
INSERT INTO cloud_table_miroir (nom_table, cle, colonnes) VALUES ('boite', '{id}', '{id,lot_id,gtin,numero_serie,statut,ligne_vente_id,scannee_le,code_etiquette,reception_id,prix_achat_ht,officine_id}')
    ON CONFLICT (nom_table) DO UPDATE SET cle = EXCLUDED.cle, colonnes = EXCLUDED.colonnes;

CREATE TABLE IF NOT EXISTS public."boite_evenement" (
    officine_id uuid NOT NULL,
    "id" bigint NOT NULL,
    sync_epoque integer NOT NULL,
    sync_numero bigint NOT NULL,
    sync_recu_le timestamp with time zone DEFAULT now() NOT NULL,
    PRIMARY KEY (officine_id, "id")
);
ALTER TABLE public."boite_evenement" ADD COLUMN IF NOT EXISTS "boite_id" bigint;
ALTER TABLE public."boite_evenement" ADD COLUMN IF NOT EXISTS "type" text;
ALTER TABLE public."boite_evenement" ADD COLUMN IF NOT EXISTS "reception_id" bigint;
ALTER TABLE public."boite_evenement" ADD COLUMN IF NOT EXISTS "ligne_vente_id" bigint;
ALTER TABLE public."boite_evenement" ADD COLUMN IF NOT EXISTS "retour_fournisseur_id" bigint;
ALTER TABLE public."boite_evenement" ADD COLUMN IF NOT EXISTS "patient_id" text;
ALTER TABLE public."boite_evenement" ADD COLUMN IF NOT EXISTS "utilisateur_id" bigint;
ALTER TABLE public."boite_evenement" ADD COLUMN IF NOT EXISTS "commentaire" text;
ALTER TABLE public."boite_evenement" ADD COLUMN IF NOT EXISTS "cree_le" timestamp with time zone;
ALTER TABLE public."boite_evenement" ADD COLUMN IF NOT EXISTS "inventaire_id" bigint;
ALTER TABLE public."boite_evenement" ADD COLUMN IF NOT EXISTS sync_chargement integer;
ALTER TABLE public."boite_evenement" ADD COLUMN IF NOT EXISTS sync_absente_le timestamp with time zone;
ALTER TABLE public."boite_evenement" ENABLE ROW LEVEL SECURITY;
ALTER TABLE public."boite_evenement" FORCE ROW LEVEL SECURITY;
DROP POLICY IF EXISTS cloisonnement ON public."boite_evenement";
CREATE POLICY cloisonnement ON public."boite_evenement" USING (officine_id = cloud_officine_courante()) WITH CHECK (officine_id = cloud_officine_courante());
GRANT SELECT, INSERT, UPDATE, DELETE ON public."boite_evenement" TO pharma_cloud_api;
INSERT INTO cloud_table_miroir (nom_table, cle, colonnes) VALUES ('boite_evenement', '{id}', '{id,boite_id,type,reception_id,ligne_vente_id,retour_fournisseur_id,patient_id,utilisateur_id,commentaire,cree_le,inventaire_id,officine_id}')
    ON CONFLICT (nom_table) DO UPDATE SET cle = EXCLUDED.cle, colonnes = EXCLUDED.colonnes;

CREATE TABLE IF NOT EXISTS public."bordereau" (
    officine_id uuid NOT NULL,
    "id" bigint NOT NULL,
    sync_epoque integer NOT NULL,
    sync_numero bigint NOT NULL,
    sync_recu_le timestamp with time zone DEFAULT now() NOT NULL,
    PRIMARY KEY (officine_id, "id")
);
ALTER TABLE public."bordereau" ADD COLUMN IF NOT EXISTS "organisme_id" bigint;
ALTER TABLE public."bordereau" ADD COLUMN IF NOT EXISTS "numero" text;
ALTER TABLE public."bordereau" ADD COLUMN IF NOT EXISTS "periode_debut" date;
ALTER TABLE public."bordereau" ADD COLUMN IF NOT EXISTS "periode_fin" date;
ALTER TABLE public."bordereau" ADD COLUMN IF NOT EXISTS "montant_total" numeric(12,2);
ALTER TABLE public."bordereau" ADD COLUMN IF NOT EXISTS "statut" text;
ALTER TABLE public."bordereau" ADD COLUMN IF NOT EXISTS "date_envoi" date;
ALTER TABLE public."bordereau" ADD COLUMN IF NOT EXISTS "canal" text;
ALTER TABLE public."bordereau" ADD COLUMN IF NOT EXISTS "centre_payeur_id" bigint;
ALTER TABLE public."bordereau" ADD COLUMN IF NOT EXISTS "montant_declare" numeric(12,2);
ALTER TABLE public."bordereau" ADD COLUMN IF NOT EXISTS "cree_le" timestamp with time zone;
ALTER TABLE public."bordereau" ADD COLUMN IF NOT EXISTS "cree_par" bigint;
ALTER TABLE public."bordereau" ADD COLUMN IF NOT EXISTS "depose_le" timestamp with time zone;
ALTER TABLE public."bordereau" ADD COLUMN IF NOT EXISTS "depose_par" bigint;
ALTER TABLE public."bordereau" ADD COLUMN IF NOT EXISTS sync_chargement integer;
ALTER TABLE public."bordereau" ADD COLUMN IF NOT EXISTS sync_absente_le timestamp with time zone;
ALTER TABLE public."bordereau" ENABLE ROW LEVEL SECURITY;
ALTER TABLE public."bordereau" FORCE ROW LEVEL SECURITY;
DROP POLICY IF EXISTS cloisonnement ON public."bordereau";
CREATE POLICY cloisonnement ON public."bordereau" USING (officine_id = cloud_officine_courante()) WITH CHECK (officine_id = cloud_officine_courante());
GRANT SELECT, INSERT, UPDATE, DELETE ON public."bordereau" TO pharma_cloud_api;
INSERT INTO cloud_table_miroir (nom_table, cle, colonnes) VALUES ('bordereau', '{id}', '{id,organisme_id,numero,periode_debut,periode_fin,montant_total,statut,date_envoi,canal,centre_payeur_id,montant_declare,cree_le,cree_par,depose_le,depose_par,officine_id}')
    ON CONFLICT (nom_table) DO UPDATE SET cle = EXCLUDED.cle, colonnes = EXCLUDED.colonnes;

CREATE TABLE IF NOT EXISTS public."bordereau_vente" (
    officine_id uuid NOT NULL,
    "id" bigint NOT NULL,
    sync_epoque integer NOT NULL,
    sync_numero bigint NOT NULL,
    sync_recu_le timestamp with time zone DEFAULT now() NOT NULL,
    PRIMARY KEY (officine_id, "id")
);
ALTER TABLE public."bordereau_vente" ADD COLUMN IF NOT EXISTS "bordereau_id" bigint;
ALTER TABLE public."bordereau_vente" ADD COLUMN IF NOT EXISTS "vente_id" bigint;
ALTER TABLE public."bordereau_vente" ADD COLUMN IF NOT EXISTS "part_organisme" numeric(12,2);
ALTER TABLE public."bordereau_vente" ADD COLUMN IF NOT EXISTS "ajoute_le" timestamp with time zone;
ALTER TABLE public."bordereau_vente" ADD COLUMN IF NOT EXISTS "ajoute_par" bigint;
ALTER TABLE public."bordereau_vente" ADD COLUMN IF NOT EXISTS "rejete_le" timestamp with time zone;
ALTER TABLE public."bordereau_vente" ADD COLUMN IF NOT EXISTS "rejete_par" bigint;
ALTER TABLE public."bordereau_vente" ADD COLUMN IF NOT EXISTS "rejet_motif_code" text;
ALTER TABLE public."bordereau_vente" ADD COLUMN IF NOT EXISTS "rejet_texte" text;
ALTER TABLE public."bordereau_vente" ADD COLUMN IF NOT EXISTS "montant_rejete" numeric(12,2);
ALTER TABLE public."bordereau_vente" ADD COLUMN IF NOT EXISTS "suite" text;
ALTER TABLE public."bordereau_vente" ADD COLUMN IF NOT EXISTS "suite_le" timestamp with time zone;
ALTER TABLE public."bordereau_vente" ADD COLUMN IF NOT EXISTS "suite_par" bigint;
ALTER TABLE public."bordereau_vente" ADD COLUMN IF NOT EXISTS "credit_mouvement_id" bigint;
ALTER TABLE public."bordereau_vente" ADD COLUMN IF NOT EXISTS sync_chargement integer;
ALTER TABLE public."bordereau_vente" ADD COLUMN IF NOT EXISTS sync_absente_le timestamp with time zone;
ALTER TABLE public."bordereau_vente" ENABLE ROW LEVEL SECURITY;
ALTER TABLE public."bordereau_vente" FORCE ROW LEVEL SECURITY;
DROP POLICY IF EXISTS cloisonnement ON public."bordereau_vente";
CREATE POLICY cloisonnement ON public."bordereau_vente" USING (officine_id = cloud_officine_courante()) WITH CHECK (officine_id = cloud_officine_courante());
GRANT SELECT, INSERT, UPDATE, DELETE ON public."bordereau_vente" TO pharma_cloud_api;
INSERT INTO cloud_table_miroir (nom_table, cle, colonnes) VALUES ('bordereau_vente', '{id}', '{id,bordereau_id,vente_id,part_organisme,ajoute_le,ajoute_par,rejete_le,rejete_par,rejet_motif_code,rejet_texte,montant_rejete,suite,suite_le,suite_par,credit_mouvement_id,officine_id}')
    ON CONFLICT (nom_table) DO UPDATE SET cle = EXCLUDED.cle, colonnes = EXCLUDED.colonnes;

CREATE TABLE IF NOT EXISTS public."campagne_inventaire" (
    officine_id uuid NOT NULL,
    "id" bigint NOT NULL,
    sync_epoque integer NOT NULL,
    sync_numero bigint NOT NULL,
    sync_recu_le timestamp with time zone DEFAULT now() NOT NULL,
    PRIMARY KEY (officine_id, "id")
);
ALTER TABLE public."campagne_inventaire" ADD COLUMN IF NOT EXISTS "mode" text;
ALTER TABLE public."campagne_inventaire" ADD COLUMN IF NOT EXISTS "statut" text;
ALTER TABLE public."campagne_inventaire" ADD COLUMN IF NOT EXISTS "commence_le" timestamp with time zone;
ALTER TABLE public."campagne_inventaire" ADD COLUMN IF NOT EXISTS "commence_par" bigint;
ALTER TABLE public."campagne_inventaire" ADD COLUMN IF NOT EXISTS "cloture_le" timestamp with time zone;
ALTER TABLE public."campagne_inventaire" ADD COLUMN IF NOT EXISTS "cloture_par" bigint;
ALTER TABLE public."campagne_inventaire" ADD COLUMN IF NOT EXISTS "nombre_lots" integer;
ALTER TABLE public."campagne_inventaire" ADD COLUMN IF NOT EXISTS "nombre_boites" integer;
ALTER TABLE public."campagne_inventaire" ADD COLUMN IF NOT EXISTS "valeur_stock" numeric(14,2);
ALTER TABLE public."campagne_inventaire" ADD COLUMN IF NOT EXISTS "commentaire" text;
ALTER TABLE public."campagne_inventaire" ADD COLUMN IF NOT EXISTS sync_chargement integer;
ALTER TABLE public."campagne_inventaire" ADD COLUMN IF NOT EXISTS sync_absente_le timestamp with time zone;
ALTER TABLE public."campagne_inventaire" ENABLE ROW LEVEL SECURITY;
ALTER TABLE public."campagne_inventaire" FORCE ROW LEVEL SECURITY;
DROP POLICY IF EXISTS cloisonnement ON public."campagne_inventaire";
CREATE POLICY cloisonnement ON public."campagne_inventaire" USING (officine_id = cloud_officine_courante()) WITH CHECK (officine_id = cloud_officine_courante());
GRANT SELECT, INSERT, UPDATE, DELETE ON public."campagne_inventaire" TO pharma_cloud_api;
INSERT INTO cloud_table_miroir (nom_table, cle, colonnes) VALUES ('campagne_inventaire', '{id}', '{id,mode,statut,commence_le,commence_par,cloture_le,cloture_par,nombre_lots,nombre_boites,valeur_stock,commentaire,officine_id}')
    ON CONFLICT (nom_table) DO UPDATE SET cle = EXCLUDED.cle, colonnes = EXCLUDED.colonnes;

CREATE TABLE IF NOT EXISTS public."campagne_inventaire_ligne" (
    officine_id uuid NOT NULL,
    "campagne_id" bigint NOT NULL,
    "lot_id" bigint NOT NULL,
    sync_epoque integer NOT NULL,
    sync_numero bigint NOT NULL,
    sync_recu_le timestamp with time zone DEFAULT now() NOT NULL,
    PRIMARY KEY (officine_id, "campagne_id", "lot_id")
);
ALTER TABLE public."campagne_inventaire_ligne" ADD COLUMN IF NOT EXISTS "produit" text;
ALTER TABLE public."campagne_inventaire_ligne" ADD COLUMN IF NOT EXISTS "numero_lot" text;
ALTER TABLE public."campagne_inventaire_ligne" ADD COLUMN IF NOT EXISTS "date_peremption" date;
ALTER TABLE public."campagne_inventaire_ligne" ADD COLUMN IF NOT EXISTS "rayon" text;
ALTER TABLE public."campagne_inventaire_ligne" ADD COLUMN IF NOT EXISTS "quantite" integer;
ALTER TABLE public."campagne_inventaire_ligne" ADD COLUMN IF NOT EXISTS "prix_achat_ht" numeric(12,2);
ALTER TABLE public."campagne_inventaire_ligne" ADD COLUMN IF NOT EXISTS "valeur" numeric(14,2);
ALTER TABLE public."campagne_inventaire_ligne" ADD COLUMN IF NOT EXISTS sync_chargement integer;
ALTER TABLE public."campagne_inventaire_ligne" ADD COLUMN IF NOT EXISTS sync_absente_le timestamp with time zone;
ALTER TABLE public."campagne_inventaire_ligne" ENABLE ROW LEVEL SECURITY;
ALTER TABLE public."campagne_inventaire_ligne" FORCE ROW LEVEL SECURITY;
DROP POLICY IF EXISTS cloisonnement ON public."campagne_inventaire_ligne";
CREATE POLICY cloisonnement ON public."campagne_inventaire_ligne" USING (officine_id = cloud_officine_courante()) WITH CHECK (officine_id = cloud_officine_courante());
GRANT SELECT, INSERT, UPDATE, DELETE ON public."campagne_inventaire_ligne" TO pharma_cloud_api;
INSERT INTO cloud_table_miroir (nom_table, cle, colonnes) VALUES ('campagne_inventaire_ligne', '{campagne_id,lot_id}', '{campagne_id,lot_id,produit,numero_lot,date_peremption,rayon,quantite,prix_achat_ht,valeur,officine_id}')
    ON CONFLICT (nom_table) DO UPDATE SET cle = EXCLUDED.cle, colonnes = EXCLUDED.colonnes;

CREATE TABLE IF NOT EXISTS public."catalogue_grossiste" (
    officine_id uuid NOT NULL,
    "fournisseur_id" bigint NOT NULL,
    "produit_id" bigint NOT NULL,
    sync_epoque integer NOT NULL,
    sync_numero bigint NOT NULL,
    sync_recu_le timestamp with time zone DEFAULT now() NOT NULL,
    PRIMARY KEY (officine_id, "fournisseur_id", "produit_id")
);
ALTER TABLE public."catalogue_grossiste" ADD COLUMN IF NOT EXISTS "code_grossiste" text;
ALTER TABLE public."catalogue_grossiste" ADD COLUMN IF NOT EXISTS "designation" text;
ALTER TABLE public."catalogue_grossiste" ADD COLUMN IF NOT EXISTS "prix_ht" numeric(12,2);
ALTER TABLE public."catalogue_grossiste" ADD COLUMN IF NOT EXISTS "disponible" boolean;
ALTER TABLE public."catalogue_grossiste" ADD COLUMN IF NOT EXISTS "quantite_disponible" integer;
ALTER TABLE public."catalogue_grossiste" ADD COLUMN IF NOT EXISTS "delai_jours" integer;
ALTER TABLE public."catalogue_grossiste" ADD COLUMN IF NOT EXISTS "import_id" bigint;
ALTER TABLE public."catalogue_grossiste" ADD COLUMN IF NOT EXISTS "maj_le" timestamp with time zone;
ALTER TABLE public."catalogue_grossiste" ADD COLUMN IF NOT EXISTS sync_chargement integer;
ALTER TABLE public."catalogue_grossiste" ADD COLUMN IF NOT EXISTS sync_absente_le timestamp with time zone;
ALTER TABLE public."catalogue_grossiste" ENABLE ROW LEVEL SECURITY;
ALTER TABLE public."catalogue_grossiste" FORCE ROW LEVEL SECURITY;
DROP POLICY IF EXISTS cloisonnement ON public."catalogue_grossiste";
CREATE POLICY cloisonnement ON public."catalogue_grossiste" USING (officine_id = cloud_officine_courante()) WITH CHECK (officine_id = cloud_officine_courante());
GRANT SELECT, INSERT, UPDATE, DELETE ON public."catalogue_grossiste" TO pharma_cloud_api;
INSERT INTO cloud_table_miroir (nom_table, cle, colonnes) VALUES ('catalogue_grossiste', '{fournisseur_id,produit_id}', '{fournisseur_id,produit_id,code_grossiste,designation,prix_ht,disponible,quantite_disponible,delai_jours,import_id,maj_le,officine_id}')
    ON CONFLICT (nom_table) DO UPDATE SET cle = EXCLUDED.cle, colonnes = EXCLUDED.colonnes;

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

CREATE TABLE IF NOT EXISTS public."client_facture" (
    officine_id uuid NOT NULL,
    "id" bigint NOT NULL,
    sync_epoque integer NOT NULL,
    sync_numero bigint NOT NULL,
    sync_recu_le timestamp with time zone DEFAULT now() NOT NULL,
    PRIMARY KEY (officine_id, "id")
);
ALTER TABLE public."client_facture" ADD COLUMN IF NOT EXISTS "raison_sociale" text;
ALTER TABLE public."client_facture" ADD COLUMN IF NOT EXISTS "adresse" text;
ALTER TABLE public."client_facture" ADD COLUMN IF NOT EXISTS "nif" text;
ALTER TABLE public."client_facture" ADD COLUMN IF NOT EXISTS "rc" text;
ALTER TABLE public."client_facture" ADD COLUMN IF NOT EXISTS "nis" text;
ALTER TABLE public."client_facture" ADD COLUMN IF NOT EXISTS "article_imposition" text;
ALTER TABLE public."client_facture" ADD COLUMN IF NOT EXISTS "actif" boolean;
ALTER TABLE public."client_facture" ADD COLUMN IF NOT EXISTS "cree_le" timestamp with time zone;
ALTER TABLE public."client_facture" ADD COLUMN IF NOT EXISTS "delai_paiement_jours" integer;
ALTER TABLE public."client_facture" ADD COLUMN IF NOT EXISTS sync_chargement integer;
ALTER TABLE public."client_facture" ADD COLUMN IF NOT EXISTS sync_absente_le timestamp with time zone;
ALTER TABLE public."client_facture" ENABLE ROW LEVEL SECURITY;
ALTER TABLE public."client_facture" FORCE ROW LEVEL SECURITY;
DROP POLICY IF EXISTS cloisonnement ON public."client_facture";
CREATE POLICY cloisonnement ON public."client_facture" USING (officine_id = cloud_officine_courante()) WITH CHECK (officine_id = cloud_officine_courante());
GRANT SELECT, INSERT, UPDATE, DELETE ON public."client_facture" TO pharma_cloud_api;
INSERT INTO cloud_table_miroir (nom_table, cle, colonnes) VALUES ('client_facture', '{id}', '{id,raison_sociale,adresse,nif,rc,nis,article_imposition,actif,cree_le,delai_paiement_jours,officine_id}')
    ON CONFLICT (nom_table) DO UPDATE SET cle = EXCLUDED.cle, colonnes = EXCLUDED.colonnes;

CREATE TABLE IF NOT EXISTS public."cloture_fiscale" (
    officine_id uuid NOT NULL,
    "id" bigint NOT NULL,
    sync_epoque integer NOT NULL,
    sync_numero bigint NOT NULL,
    sync_recu_le timestamp with time zone DEFAULT now() NOT NULL,
    PRIMARY KEY (officine_id, "id")
);
ALTER TABLE public."cloture_fiscale" ADD COLUMN IF NOT EXISTS "type" text;
ALTER TABLE public."cloture_fiscale" ADD COLUMN IF NOT EXISTS "session_caisse_id" bigint;
ALTER TABLE public."cloture_fiscale" ADD COLUMN IF NOT EXISTS "periode_du" date;
ALTER TABLE public."cloture_fiscale" ADD COLUMN IF NOT EXISTS "periode_au" date;
ALTER TABLE public."cloture_fiscale" ADD COLUMN IF NOT EXISTS "sceau_id" bigint;
ALTER TABLE public."cloture_fiscale" ADD COLUMN IF NOT EXISTS "evenements" bigint;
ALTER TABLE public."cloture_fiscale" ADD COLUMN IF NOT EXISTS "montant_ttc" numeric(14,2);
ALTER TABLE public."cloture_fiscale" ADD COLUMN IF NOT EXISTS "montant_ht" numeric(14,2);
ALTER TABLE public."cloture_fiscale" ADD COLUMN IF NOT EXISTS "montant_tva" numeric(14,2);
ALTER TABLE public."cloture_fiscale" ADD COLUMN IF NOT EXISTS "fichier_archive" text;
ALTER TABLE public."cloture_fiscale" ADD COLUMN IF NOT EXISTS "empreinte_archive" bytea;
ALTER TABLE public."cloture_fiscale" ADD COLUMN IF NOT EXISTS "cree_le" timestamp with time zone;
ALTER TABLE public."cloture_fiscale" ADD COLUMN IF NOT EXISTS sync_chargement integer;
ALTER TABLE public."cloture_fiscale" ADD COLUMN IF NOT EXISTS sync_absente_le timestamp with time zone;
ALTER TABLE public."cloture_fiscale" ENABLE ROW LEVEL SECURITY;
ALTER TABLE public."cloture_fiscale" FORCE ROW LEVEL SECURITY;
DROP POLICY IF EXISTS cloisonnement ON public."cloture_fiscale";
CREATE POLICY cloisonnement ON public."cloture_fiscale" USING (officine_id = cloud_officine_courante()) WITH CHECK (officine_id = cloud_officine_courante());
GRANT SELECT, INSERT, UPDATE, DELETE ON public."cloture_fiscale" TO pharma_cloud_api;
INSERT INTO cloud_table_miroir (nom_table, cle, colonnes) VALUES ('cloture_fiscale', '{id}', '{id,type,session_caisse_id,periode_du,periode_au,sceau_id,evenements,montant_ttc,montant_ht,montant_tva,fichier_archive,empreinte_archive,cree_le,officine_id}')
    ON CONFLICT (nom_table) DO UPDATE SET cle = EXCLUDED.cle, colonnes = EXCLUDED.colonnes;

CREATE TABLE IF NOT EXISTS public."commande_fournisseur" (
    officine_id uuid NOT NULL,
    "id" bigint NOT NULL,
    sync_epoque integer NOT NULL,
    sync_numero bigint NOT NULL,
    sync_recu_le timestamp with time zone DEFAULT now() NOT NULL,
    PRIMARY KEY (officine_id, "id")
);
ALTER TABLE public."commande_fournisseur" ADD COLUMN IF NOT EXISTS "fournisseur_id" bigint;
ALTER TABLE public."commande_fournisseur" ADD COLUMN IF NOT EXISTS "numero" text;
ALTER TABLE public."commande_fournisseur" ADD COLUMN IF NOT EXISTS "date_commande" timestamp with time zone;
ALTER TABLE public."commande_fournisseur" ADD COLUMN IF NOT EXISTS "statut" text;
ALTER TABLE public."commande_fournisseur" ADD COLUMN IF NOT EXISTS "montant_total" numeric(12,2);
ALTER TABLE public."commande_fournisseur" ADD COLUMN IF NOT EXISTS "utilisateur_id" bigint;
ALTER TABLE public."commande_fournisseur" ADD COLUMN IF NOT EXISTS "envoyee_email_le" timestamp with time zone;
ALTER TABLE public."commande_fournisseur" ADD COLUMN IF NOT EXISTS "envoyee_email_a" text;
ALTER TABLE public."commande_fournisseur" ADD COLUMN IF NOT EXISTS sync_chargement integer;
ALTER TABLE public."commande_fournisseur" ADD COLUMN IF NOT EXISTS sync_absente_le timestamp with time zone;
ALTER TABLE public."commande_fournisseur" ENABLE ROW LEVEL SECURITY;
ALTER TABLE public."commande_fournisseur" FORCE ROW LEVEL SECURITY;
DROP POLICY IF EXISTS cloisonnement ON public."commande_fournisseur";
CREATE POLICY cloisonnement ON public."commande_fournisseur" USING (officine_id = cloud_officine_courante()) WITH CHECK (officine_id = cloud_officine_courante());
GRANT SELECT, INSERT, UPDATE, DELETE ON public."commande_fournisseur" TO pharma_cloud_api;
INSERT INTO cloud_table_miroir (nom_table, cle, colonnes) VALUES ('commande_fournisseur', '{id}', '{id,fournisseur_id,numero,date_commande,statut,montant_total,utilisateur_id,envoyee_email_le,envoyee_email_a,officine_id}')
    ON CONFLICT (nom_table) DO UPDATE SET cle = EXCLUDED.cle, colonnes = EXCLUDED.colonnes;

CREATE TABLE IF NOT EXISTS public."comptage_caisse" (
    officine_id uuid NOT NULL,
    "session_id" bigint NOT NULL,
    "coupure" text NOT NULL,
    sync_epoque integer NOT NULL,
    sync_numero bigint NOT NULL,
    sync_recu_le timestamp with time zone DEFAULT now() NOT NULL,
    PRIMARY KEY (officine_id, "session_id", "coupure")
);
ALTER TABLE public."comptage_caisse" ADD COLUMN IF NOT EXISTS "nombre" integer;
ALTER TABLE public."comptage_caisse" ADD COLUMN IF NOT EXISTS "montant" numeric(12,2);
ALTER TABLE public."comptage_caisse" ADD COLUMN IF NOT EXISTS sync_chargement integer;
ALTER TABLE public."comptage_caisse" ADD COLUMN IF NOT EXISTS sync_absente_le timestamp with time zone;
ALTER TABLE public."comptage_caisse" ENABLE ROW LEVEL SECURITY;
ALTER TABLE public."comptage_caisse" FORCE ROW LEVEL SECURITY;
DROP POLICY IF EXISTS cloisonnement ON public."comptage_caisse";
CREATE POLICY cloisonnement ON public."comptage_caisse" USING (officine_id = cloud_officine_courante()) WITH CHECK (officine_id = cloud_officine_courante());
GRANT SELECT, INSERT, UPDATE, DELETE ON public."comptage_caisse" TO pharma_cloud_api;
INSERT INTO cloud_table_miroir (nom_table, cle, colonnes) VALUES ('comptage_caisse', '{session_id,coupure}', '{session_id,coupure,nombre,montant,officine_id}')
    ON CONFLICT (nom_table) DO UPDATE SET cle = EXCLUDED.cle, colonnes = EXCLUDED.colonnes;

CREATE TABLE IF NOT EXISTS public."comptage_intermediaire" (
    officine_id uuid NOT NULL,
    "id" bigint NOT NULL,
    sync_epoque integer NOT NULL,
    sync_numero bigint NOT NULL,
    sync_recu_le timestamp with time zone DEFAULT now() NOT NULL,
    PRIMARY KEY (officine_id, "id")
);
ALTER TABLE public."comptage_intermediaire" ADD COLUMN IF NOT EXISTS "session_caisse_id" bigint;
ALTER TABLE public."comptage_intermediaire" ADD COLUMN IF NOT EXISTS "utilisateur_id" bigint;
ALTER TABLE public."comptage_intermediaire" ADD COLUMN IF NOT EXISTS "compte" numeric(12,2);
ALTER TABLE public."comptage_intermediaire" ADD COLUMN IF NOT EXISTS "attendu" numeric(12,2);
ALTER TABLE public."comptage_intermediaire" ADD COLUMN IF NOT EXISTS "ecart" numeric(12,2);
ALTER TABLE public."comptage_intermediaire" ADD COLUMN IF NOT EXISTS "comptage" jsonb;
ALTER TABLE public."comptage_intermediaire" ADD COLUMN IF NOT EXISTS "cree_le" timestamp with time zone;
ALTER TABLE public."comptage_intermediaire" ADD COLUMN IF NOT EXISTS sync_chargement integer;
ALTER TABLE public."comptage_intermediaire" ADD COLUMN IF NOT EXISTS sync_absente_le timestamp with time zone;
ALTER TABLE public."comptage_intermediaire" ENABLE ROW LEVEL SECURITY;
ALTER TABLE public."comptage_intermediaire" FORCE ROW LEVEL SECURITY;
DROP POLICY IF EXISTS cloisonnement ON public."comptage_intermediaire";
CREATE POLICY cloisonnement ON public."comptage_intermediaire" USING (officine_id = cloud_officine_courante()) WITH CHECK (officine_id = cloud_officine_courante());
GRANT SELECT, INSERT, UPDATE, DELETE ON public."comptage_intermediaire" TO pharma_cloud_api;
INSERT INTO cloud_table_miroir (nom_table, cle, colonnes) VALUES ('comptage_intermediaire', '{id}', '{id,session_caisse_id,utilisateur_id,compte,attendu,ecart,comptage,cree_le,officine_id}')
    ON CONFLICT (nom_table) DO UPDATE SET cle = EXCLUDED.cle, colonnes = EXCLUDED.colonnes;

CREATE TABLE IF NOT EXISTS public."comptage_reception" (
    officine_id uuid NOT NULL,
    "id" bigint NOT NULL,
    sync_epoque integer NOT NULL,
    sync_numero bigint NOT NULL,
    sync_recu_le timestamp with time zone DEFAULT now() NOT NULL,
    PRIMARY KEY (officine_id, "id")
);
ALTER TABLE public."comptage_reception" ADD COLUMN IF NOT EXISTS "fournisseur_id" bigint;
ALTER TABLE public."comptage_reception" ADD COLUMN IF NOT EXISTS "numero_bl" text;
ALTER TABLE public."comptage_reception" ADD COLUMN IF NOT EXISTS "statut" text;
ALTER TABLE public."comptage_reception" ADD COLUMN IF NOT EXISTS "compte_par" bigint;
ALTER TABLE public."comptage_reception" ADD COLUMN IF NOT EXISTS "compte_le" timestamp with time zone;
ALTER TABLE public."comptage_reception" ADD COLUMN IF NOT EXISTS "facture_par" bigint;
ALTER TABLE public."comptage_reception" ADD COLUMN IF NOT EXISTS "facture_le" timestamp with time zone;
ALTER TABLE public."comptage_reception" ADD COLUMN IF NOT EXISTS "saisie" jsonb;
ALTER TABLE public."comptage_reception" ADD COLUMN IF NOT EXISTS "ecarts" jsonb;
ALTER TABLE public."comptage_reception" ADD COLUMN IF NOT EXISTS "boites_manquantes" integer;
ALTER TABLE public."comptage_reception" ADD COLUMN IF NOT EXISTS "boites_en_trop" integer;
ALTER TABLE public."comptage_reception" ADD COLUMN IF NOT EXISTS "valeur_manquante" numeric(12,2);
ALTER TABLE public."comptage_reception" ADD COLUMN IF NOT EXISTS "decide_par" bigint;
ALTER TABLE public."comptage_reception" ADD COLUMN IF NOT EXISTS "decide_le" timestamp with time zone;
ALTER TABLE public."comptage_reception" ADD COLUMN IF NOT EXISTS "reception_id" bigint;
ALTER TABLE public."comptage_reception" ADD COLUMN IF NOT EXISTS sync_chargement integer;
ALTER TABLE public."comptage_reception" ADD COLUMN IF NOT EXISTS sync_absente_le timestamp with time zone;
ALTER TABLE public."comptage_reception" ENABLE ROW LEVEL SECURITY;
ALTER TABLE public."comptage_reception" FORCE ROW LEVEL SECURITY;
DROP POLICY IF EXISTS cloisonnement ON public."comptage_reception";
CREATE POLICY cloisonnement ON public."comptage_reception" USING (officine_id = cloud_officine_courante()) WITH CHECK (officine_id = cloud_officine_courante());
GRANT SELECT, INSERT, UPDATE, DELETE ON public."comptage_reception" TO pharma_cloud_api;
INSERT INTO cloud_table_miroir (nom_table, cle, colonnes) VALUES ('comptage_reception', '{id}', '{id,fournisseur_id,numero_bl,statut,compte_par,compte_le,facture_par,facture_le,saisie,ecarts,boites_manquantes,boites_en_trop,valeur_manquante,decide_par,decide_le,reception_id,officine_id}')
    ON CONFLICT (nom_table) DO UPDATE SET cle = EXCLUDED.cle, colonnes = EXCLUDED.colonnes;

CREATE TABLE IF NOT EXISTS public."comptage_reception_ligne" (
    officine_id uuid NOT NULL,
    "id" bigint NOT NULL,
    sync_epoque integer NOT NULL,
    sync_numero bigint NOT NULL,
    sync_recu_le timestamp with time zone DEFAULT now() NOT NULL,
    PRIMARY KEY (officine_id, "id")
);
ALTER TABLE public."comptage_reception_ligne" ADD COLUMN IF NOT EXISTS "comptage_id" bigint;
ALTER TABLE public."comptage_reception_ligne" ADD COLUMN IF NOT EXISTS "produit_id" bigint;
ALTER TABLE public."comptage_reception_ligne" ADD COLUMN IF NOT EXISTS "numero_lot" text;
ALTER TABLE public."comptage_reception_ligne" ADD COLUMN IF NOT EXISTS "date_peremption" date;
ALTER TABLE public."comptage_reception_ligne" ADD COLUMN IF NOT EXISTS "gtin" text;
ALTER TABLE public."comptage_reception_ligne" ADD COLUMN IF NOT EXISTS "quantite" integer;
ALTER TABLE public."comptage_reception_ligne" ADD COLUMN IF NOT EXISTS "numeros_serie" text[];
ALTER TABLE public."comptage_reception_ligne" ADD COLUMN IF NOT EXISTS "scans_mobile" bigint[];
ALTER TABLE public."comptage_reception_ligne" ADD COLUMN IF NOT EXISTS sync_chargement integer;
ALTER TABLE public."comptage_reception_ligne" ADD COLUMN IF NOT EXISTS sync_absente_le timestamp with time zone;
ALTER TABLE public."comptage_reception_ligne" ENABLE ROW LEVEL SECURITY;
ALTER TABLE public."comptage_reception_ligne" FORCE ROW LEVEL SECURITY;
DROP POLICY IF EXISTS cloisonnement ON public."comptage_reception_ligne";
CREATE POLICY cloisonnement ON public."comptage_reception_ligne" USING (officine_id = cloud_officine_courante()) WITH CHECK (officine_id = cloud_officine_courante());
GRANT SELECT, INSERT, UPDATE, DELETE ON public."comptage_reception_ligne" TO pharma_cloud_api;
INSERT INTO cloud_table_miroir (nom_table, cle, colonnes) VALUES ('comptage_reception_ligne', '{id}', '{id,comptage_id,produit_id,numero_lot,date_peremption,gtin,quantite,numeros_serie,scans_mobile,officine_id}')
    ON CONFLICT (nom_table) DO UPDATE SET cle = EXCLUDED.cle, colonnes = EXCLUDED.colonnes;

CREATE TABLE IF NOT EXISTS public."compte_carte_periode" (
    officine_id uuid NOT NULL,
    "id" bigint NOT NULL,
    sync_epoque integer NOT NULL,
    sync_numero bigint NOT NULL,
    sync_recu_le timestamp with time zone DEFAULT now() NOT NULL,
    PRIMARY KEY (officine_id, "id")
);
ALTER TABLE public."compte_carte_periode" ADD COLUMN IF NOT EXISTS "compte_id" bigint;
ALTER TABLE public."compte_carte_periode" ADD COLUMN IF NOT EXISTS "du" date;
ALTER TABLE public."compte_carte_periode" ADD COLUMN IF NOT EXISTS "au" date;
ALTER TABLE public."compte_carte_periode" ADD COLUMN IF NOT EXISTS sync_chargement integer;
ALTER TABLE public."compte_carte_periode" ADD COLUMN IF NOT EXISTS sync_absente_le timestamp with time zone;
ALTER TABLE public."compte_carte_periode" ENABLE ROW LEVEL SECURITY;
ALTER TABLE public."compte_carte_periode" FORCE ROW LEVEL SECURITY;
DROP POLICY IF EXISTS cloisonnement ON public."compte_carte_periode";
CREATE POLICY cloisonnement ON public."compte_carte_periode" USING (officine_id = cloud_officine_courante()) WITH CHECK (officine_id = cloud_officine_courante());
GRANT SELECT, INSERT, UPDATE, DELETE ON public."compte_carte_periode" TO pharma_cloud_api;
INSERT INTO cloud_table_miroir (nom_table, cle, colonnes) VALUES ('compte_carte_periode', '{id}', '{id,compte_id,du,au,officine_id}')
    ON CONFLICT (nom_table) DO UPDATE SET cle = EXCLUDED.cle, colonnes = EXCLUDED.colonnes;

CREATE TABLE IF NOT EXISTS public."compte_tresorerie" (
    officine_id uuid NOT NULL,
    "id" bigint NOT NULL,
    sync_epoque integer NOT NULL,
    sync_numero bigint NOT NULL,
    sync_recu_le timestamp with time zone DEFAULT now() NOT NULL,
    PRIMARY KEY (officine_id, "id")
);
ALTER TABLE public."compte_tresorerie" ADD COLUMN IF NOT EXISTS "type" text;
ALTER TABLE public."compte_tresorerie" ADD COLUMN IF NOT EXISTS "nom" text;
ALTER TABLE public."compte_tresorerie" ADD COLUMN IF NOT EXISTS "banque" text;
ALTER TABLE public."compte_tresorerie" ADD COLUMN IF NOT EXISTS "numero_compte" text;
ALTER TABLE public."compte_tresorerie" ADD COLUMN IF NOT EXISTS "recoit_carte" boolean;
ALTER TABLE public."compte_tresorerie" ADD COLUMN IF NOT EXISTS "solde_initial" numeric(12,2);
ALTER TABLE public."compte_tresorerie" ADD COLUMN IF NOT EXISTS "date_initiale" date;
ALTER TABLE public."compte_tresorerie" ADD COLUMN IF NOT EXISTS "actif" boolean;
ALTER TABLE public."compte_tresorerie" ADD COLUMN IF NOT EXISTS "cree_le" timestamp with time zone;
ALTER TABLE public."compte_tresorerie" ADD COLUMN IF NOT EXISTS sync_chargement integer;
ALTER TABLE public."compte_tresorerie" ADD COLUMN IF NOT EXISTS sync_absente_le timestamp with time zone;
ALTER TABLE public."compte_tresorerie" ENABLE ROW LEVEL SECURITY;
ALTER TABLE public."compte_tresorerie" FORCE ROW LEVEL SECURITY;
DROP POLICY IF EXISTS cloisonnement ON public."compte_tresorerie";
CREATE POLICY cloisonnement ON public."compte_tresorerie" USING (officine_id = cloud_officine_courante()) WITH CHECK (officine_id = cloud_officine_courante());
GRANT SELECT, INSERT, UPDATE, DELETE ON public."compte_tresorerie" TO pharma_cloud_api;
INSERT INTO cloud_table_miroir (nom_table, cle, colonnes) VALUES ('compte_tresorerie', '{id}', '{id,type,nom,banque,numero_compte,recoit_carte,solde_initial,date_initiale,actif,cree_le,officine_id}')
    ON CONFLICT (nom_table) DO UPDATE SET cle = EXCLUDED.cle, colonnes = EXCLUDED.colonnes;

CREATE TABLE IF NOT EXISTS public."compteur_ticket" (
    officine_id uuid NOT NULL,
    "id" smallint NOT NULL,
    sync_epoque integer NOT NULL,
    sync_numero bigint NOT NULL,
    sync_recu_le timestamp with time zone DEFAULT now() NOT NULL,
    PRIMARY KEY (officine_id, "id")
);
ALTER TABLE public."compteur_ticket" ADD COLUMN IF NOT EXISTS "prefixe" text;
ALTER TABLE public."compteur_ticket" ADD COLUMN IF NOT EXISTS "dernier_numero" bigint;
ALTER TABLE public."compteur_ticket" ADD COLUMN IF NOT EXISTS "premier_numero_continu" bigint;
ALTER TABLE public."compteur_ticket" ADD COLUMN IF NOT EXISTS "mis_en_service_le" timestamp with time zone;
ALTER TABLE public."compteur_ticket" ADD COLUMN IF NOT EXISTS sync_chargement integer;
ALTER TABLE public."compteur_ticket" ADD COLUMN IF NOT EXISTS sync_absente_le timestamp with time zone;
ALTER TABLE public."compteur_ticket" ENABLE ROW LEVEL SECURITY;
ALTER TABLE public."compteur_ticket" FORCE ROW LEVEL SECURITY;
DROP POLICY IF EXISTS cloisonnement ON public."compteur_ticket";
CREATE POLICY cloisonnement ON public."compteur_ticket" USING (officine_id = cloud_officine_courante()) WITH CHECK (officine_id = cloud_officine_courante());
GRANT SELECT, INSERT, UPDATE, DELETE ON public."compteur_ticket" TO pharma_cloud_api;
INSERT INTO cloud_table_miroir (nom_table, cle, colonnes) VALUES ('compteur_ticket', '{id}', '{id,prefixe,dernier_numero,premier_numero_continu,mis_en_service_le,officine_id}')
    ON CONFLICT (nom_table) DO UPDATE SET cle = EXCLUDED.cle, colonnes = EXCLUDED.colonnes;

CREATE TABLE IF NOT EXISTS public."connexion_employe" (
    officine_id uuid NOT NULL,
    "id" bigint NOT NULL,
    sync_epoque integer NOT NULL,
    sync_numero bigint NOT NULL,
    sync_recu_le timestamp with time zone DEFAULT now() NOT NULL,
    PRIMARY KEY (officine_id, "id")
);
ALTER TABLE public."connexion_employe" ADD COLUMN IF NOT EXISTS "utilisateur_id" bigint;
ALTER TABLE public."connexion_employe" ADD COLUMN IF NOT EXISTS "poste" text;
ALTER TABLE public."connexion_employe" ADD COLUMN IF NOT EXISTS "methode" text;
ALTER TABLE public."connexion_employe" ADD COLUMN IF NOT EXISTS "cree_le" timestamp with time zone;
ALTER TABLE public."connexion_employe" ADD COLUMN IF NOT EXISTS sync_chargement integer;
ALTER TABLE public."connexion_employe" ADD COLUMN IF NOT EXISTS sync_absente_le timestamp with time zone;
ALTER TABLE public."connexion_employe" ENABLE ROW LEVEL SECURITY;
ALTER TABLE public."connexion_employe" FORCE ROW LEVEL SECURITY;
DROP POLICY IF EXISTS cloisonnement ON public."connexion_employe";
CREATE POLICY cloisonnement ON public."connexion_employe" USING (officine_id = cloud_officine_courante()) WITH CHECK (officine_id = cloud_officine_courante());
GRANT SELECT, INSERT, UPDATE, DELETE ON public."connexion_employe" TO pharma_cloud_api;
INSERT INTO cloud_table_miroir (nom_table, cle, colonnes) VALUES ('connexion_employe', '{id}', '{id,utilisateur_id,poste,methode,cree_le,officine_id}')
    ON CONFLICT (nom_table) DO UPDATE SET cle = EXCLUDED.cle, colonnes = EXCLUDED.colonnes;

CREATE TABLE IF NOT EXISTS public."correction_lot" (
    officine_id uuid NOT NULL,
    "id" bigint NOT NULL,
    sync_epoque integer NOT NULL,
    sync_numero bigint NOT NULL,
    sync_recu_le timestamp with time zone DEFAULT now() NOT NULL,
    PRIMARY KEY (officine_id, "id")
);
ALTER TABLE public."correction_lot" ADD COLUMN IF NOT EXISTS "lot_id" bigint;
ALTER TABLE public."correction_lot" ADD COLUMN IF NOT EXISTS "champ" text;
ALTER TABLE public."correction_lot" ADD COLUMN IF NOT EXISTS "ancienne_valeur" text;
ALTER TABLE public."correction_lot" ADD COLUMN IF NOT EXISTS "nouvelle_valeur" text;
ALTER TABLE public."correction_lot" ADD COLUMN IF NOT EXISTS "motif" text;
ALTER TABLE public."correction_lot" ADD COLUMN IF NOT EXISTS "mouvement_id" bigint;
ALTER TABLE public."correction_lot" ADD COLUMN IF NOT EXISTS "utilisateur_id" bigint;
ALTER TABLE public."correction_lot" ADD COLUMN IF NOT EXISTS "corrige_le" timestamp with time zone;
ALTER TABLE public."correction_lot" ADD COLUMN IF NOT EXISTS sync_chargement integer;
ALTER TABLE public."correction_lot" ADD COLUMN IF NOT EXISTS sync_absente_le timestamp with time zone;
ALTER TABLE public."correction_lot" ENABLE ROW LEVEL SECURITY;
ALTER TABLE public."correction_lot" FORCE ROW LEVEL SECURITY;
DROP POLICY IF EXISTS cloisonnement ON public."correction_lot";
CREATE POLICY cloisonnement ON public."correction_lot" USING (officine_id = cloud_officine_courante()) WITH CHECK (officine_id = cloud_officine_courante());
GRANT SELECT, INSERT, UPDATE, DELETE ON public."correction_lot" TO pharma_cloud_api;
INSERT INTO cloud_table_miroir (nom_table, cle, colonnes) VALUES ('correction_lot', '{id}', '{id,lot_id,champ,ancienne_valeur,nouvelle_valeur,motif,mouvement_id,utilisateur_id,corrige_le,officine_id}')
    ON CONFLICT (nom_table) DO UPDATE SET cle = EXCLUDED.cle, colonnes = EXCLUDED.colonnes;

CREATE TABLE IF NOT EXISTS public."correspondance_echeancier" (
    officine_id uuid NOT NULL,
    "compte_id" bigint NOT NULL,
    sync_epoque integer NOT NULL,
    sync_numero bigint NOT NULL,
    sync_recu_le timestamp with time zone DEFAULT now() NOT NULL,
    PRIMARY KEY (officine_id, "compte_id")
);
ALTER TABLE public."correspondance_echeancier" ADD COLUMN IF NOT EXISTS "col_date" integer;
ALTER TABLE public."correspondance_echeancier" ADD COLUMN IF NOT EXISTS "col_capital" integer;
ALTER TABLE public."correspondance_echeancier" ADD COLUMN IF NOT EXISTS "col_interets" integer;
ALTER TABLE public."correspondance_echeancier" ADD COLUMN IF NOT EXISTS "col_tva" integer;
ALTER TABLE public."correspondance_echeancier" ADD COLUMN IF NOT EXISTS "col_marge" integer;
ALTER TABLE public."correspondance_echeancier" ADD COLUMN IF NOT EXISTS "col_montant" integer;
ALTER TABLE public."correspondance_echeancier" ADD COLUMN IF NOT EXISTS "col_encours" integer;
ALTER TABLE public."correspondance_echeancier" ADD COLUMN IF NOT EXISTS "lignes_ignorees" integer;
ALTER TABLE public."correspondance_echeancier" ADD COLUMN IF NOT EXISTS "utilisateur_id" bigint;
ALTER TABLE public."correspondance_echeancier" ADD COLUMN IF NOT EXISTS "modifie_le" timestamp with time zone;
ALTER TABLE public."correspondance_echeancier" ADD COLUMN IF NOT EXISTS "col_assurance" integer;
ALTER TABLE public."correspondance_echeancier" ADD COLUMN IF NOT EXISTS sync_chargement integer;
ALTER TABLE public."correspondance_echeancier" ADD COLUMN IF NOT EXISTS sync_absente_le timestamp with time zone;
ALTER TABLE public."correspondance_echeancier" ENABLE ROW LEVEL SECURITY;
ALTER TABLE public."correspondance_echeancier" FORCE ROW LEVEL SECURITY;
DROP POLICY IF EXISTS cloisonnement ON public."correspondance_echeancier";
CREATE POLICY cloisonnement ON public."correspondance_echeancier" USING (officine_id = cloud_officine_courante()) WITH CHECK (officine_id = cloud_officine_courante());
GRANT SELECT, INSERT, UPDATE, DELETE ON public."correspondance_echeancier" TO pharma_cloud_api;
INSERT INTO cloud_table_miroir (nom_table, cle, colonnes) VALUES ('correspondance_echeancier', '{compte_id}', '{compte_id,col_date,col_capital,col_interets,col_tva,col_marge,col_montant,col_encours,lignes_ignorees,utilisateur_id,modifie_le,col_assurance,officine_id}')
    ON CONFLICT (nom_table) DO UPDATE SET cle = EXCLUDED.cle, colonnes = EXCLUDED.colonnes;

CREATE TABLE IF NOT EXISTS public."correspondance_echeancier_preteur" (
    officine_id uuid NOT NULL,
    "preteur" text NOT NULL,
    sync_epoque integer NOT NULL,
    sync_numero bigint NOT NULL,
    sync_recu_le timestamp with time zone DEFAULT now() NOT NULL,
    PRIMARY KEY (officine_id, "preteur")
);
ALTER TABLE public."correspondance_echeancier_preteur" ADD COLUMN IF NOT EXISTS "col_date" integer;
ALTER TABLE public."correspondance_echeancier_preteur" ADD COLUMN IF NOT EXISTS "col_capital" integer;
ALTER TABLE public."correspondance_echeancier_preteur" ADD COLUMN IF NOT EXISTS "col_interets" integer;
ALTER TABLE public."correspondance_echeancier_preteur" ADD COLUMN IF NOT EXISTS "col_tva" integer;
ALTER TABLE public."correspondance_echeancier_preteur" ADD COLUMN IF NOT EXISTS "col_marge" integer;
ALTER TABLE public."correspondance_echeancier_preteur" ADD COLUMN IF NOT EXISTS "col_montant" integer;
ALTER TABLE public."correspondance_echeancier_preteur" ADD COLUMN IF NOT EXISTS "col_encours" integer;
ALTER TABLE public."correspondance_echeancier_preteur" ADD COLUMN IF NOT EXISTS "lignes_ignorees" integer;
ALTER TABLE public."correspondance_echeancier_preteur" ADD COLUMN IF NOT EXISTS "utilisateur_id" bigint;
ALTER TABLE public."correspondance_echeancier_preteur" ADD COLUMN IF NOT EXISTS "modifie_le" timestamp with time zone;
ALTER TABLE public."correspondance_echeancier_preteur" ADD COLUMN IF NOT EXISTS "col_assurance" integer;
ALTER TABLE public."correspondance_echeancier_preteur" ADD COLUMN IF NOT EXISTS sync_chargement integer;
ALTER TABLE public."correspondance_echeancier_preteur" ADD COLUMN IF NOT EXISTS sync_absente_le timestamp with time zone;
ALTER TABLE public."correspondance_echeancier_preteur" ENABLE ROW LEVEL SECURITY;
ALTER TABLE public."correspondance_echeancier_preteur" FORCE ROW LEVEL SECURITY;
DROP POLICY IF EXISTS cloisonnement ON public."correspondance_echeancier_preteur";
CREATE POLICY cloisonnement ON public."correspondance_echeancier_preteur" USING (officine_id = cloud_officine_courante()) WITH CHECK (officine_id = cloud_officine_courante());
GRANT SELECT, INSERT, UPDATE, DELETE ON public."correspondance_echeancier_preteur" TO pharma_cloud_api;
INSERT INTO cloud_table_miroir (nom_table, cle, colonnes) VALUES ('correspondance_echeancier_preteur', '{preteur}', '{preteur,col_date,col_capital,col_interets,col_tva,col_marge,col_montant,col_encours,lignes_ignorees,utilisateur_id,modifie_le,col_assurance,officine_id}')
    ON CONFLICT (nom_table) DO UPDATE SET cle = EXCLUDED.cle, colonnes = EXCLUDED.colonnes;

CREATE TABLE IF NOT EXISTS public."credit_imputation" (
    officine_id uuid NOT NULL,
    "id" bigint NOT NULL,
    sync_epoque integer NOT NULL,
    sync_numero bigint NOT NULL,
    sync_recu_le timestamp with time zone DEFAULT now() NOT NULL,
    PRIMARY KEY (officine_id, "id")
);
ALTER TABLE public."credit_imputation" ADD COLUMN IF NOT EXISTS "credit_mouvement_id" bigint;
ALTER TABLE public."credit_imputation" ADD COLUMN IF NOT EXISTS "vente_id" bigint;
ALTER TABLE public."credit_imputation" ADD COLUMN IF NOT EXISTS "montant" numeric(12,2);
ALTER TABLE public."credit_imputation" ADD COLUMN IF NOT EXISTS "cree_le" timestamp with time zone;
ALTER TABLE public."credit_imputation" ADD COLUMN IF NOT EXISTS sync_chargement integer;
ALTER TABLE public."credit_imputation" ADD COLUMN IF NOT EXISTS sync_absente_le timestamp with time zone;
ALTER TABLE public."credit_imputation" ENABLE ROW LEVEL SECURITY;
ALTER TABLE public."credit_imputation" FORCE ROW LEVEL SECURITY;
DROP POLICY IF EXISTS cloisonnement ON public."credit_imputation";
CREATE POLICY cloisonnement ON public."credit_imputation" USING (officine_id = cloud_officine_courante()) WITH CHECK (officine_id = cloud_officine_courante());
GRANT SELECT, INSERT, UPDATE, DELETE ON public."credit_imputation" TO pharma_cloud_api;
INSERT INTO cloud_table_miroir (nom_table, cle, colonnes) VALUES ('credit_imputation', '{id}', '{id,credit_mouvement_id,vente_id,montant,cree_le,officine_id}')
    ON CONFLICT (nom_table) DO UPDATE SET cle = EXCLUDED.cle, colonnes = EXCLUDED.colonnes;

CREATE TABLE IF NOT EXISTS public."credit_mouvement" (
    officine_id uuid NOT NULL,
    "id" bigint NOT NULL,
    sync_epoque integer NOT NULL,
    sync_numero bigint NOT NULL,
    sync_recu_le timestamp with time zone DEFAULT now() NOT NULL,
    PRIMARY KEY (officine_id, "id")
);
ALTER TABLE public."credit_mouvement" ADD COLUMN IF NOT EXISTS "patient_id" text;
ALTER TABLE public."credit_mouvement" ADD COLUMN IF NOT EXISTS "type" text;
ALTER TABLE public."credit_mouvement" ADD COLUMN IF NOT EXISTS "montant" numeric(12,2);
ALTER TABLE public."credit_mouvement" ADD COLUMN IF NOT EXISTS "solde_apres" numeric(12,2);
ALTER TABLE public."credit_mouvement" ADD COLUMN IF NOT EXISTS "vente_id" bigint;
ALTER TABLE public."credit_mouvement" ADD COLUMN IF NOT EXISTS "utilisateur_id" bigint;
ALTER TABLE public."credit_mouvement" ADD COLUMN IF NOT EXISTS "poste" text;
ALTER TABLE public."credit_mouvement" ADD COLUMN IF NOT EXISTS "reference" text;
ALTER TABLE public."credit_mouvement" ADD COLUMN IF NOT EXISTS "cree_le" timestamp with time zone;
ALTER TABLE public."credit_mouvement" ADD COLUMN IF NOT EXISTS sync_chargement integer;
ALTER TABLE public."credit_mouvement" ADD COLUMN IF NOT EXISTS sync_absente_le timestamp with time zone;
ALTER TABLE public."credit_mouvement" ENABLE ROW LEVEL SECURITY;
ALTER TABLE public."credit_mouvement" FORCE ROW LEVEL SECURITY;
DROP POLICY IF EXISTS cloisonnement ON public."credit_mouvement";
CREATE POLICY cloisonnement ON public."credit_mouvement" USING (officine_id = cloud_officine_courante()) WITH CHECK (officine_id = cloud_officine_courante());
GRANT SELECT, INSERT, UPDATE, DELETE ON public."credit_mouvement" TO pharma_cloud_api;
INSERT INTO cloud_table_miroir (nom_table, cle, colonnes) VALUES ('credit_mouvement', '{id}', '{id,patient_id,type,montant,solde_apres,vente_id,utilisateur_id,poste,reference,cree_le,officine_id}')
    ON CONFLICT (nom_table) DO UPDATE SET cle = EXCLUDED.cle, colonnes = EXCLUDED.colonnes;

CREATE TABLE IF NOT EXISTS public."credit_ouverture" (
    officine_id uuid NOT NULL,
    "patient_id" text NOT NULL,
    sync_epoque integer NOT NULL,
    sync_numero bigint NOT NULL,
    sync_recu_le timestamp with time zone DEFAULT now() NOT NULL,
    PRIMARY KEY (officine_id, "patient_id")
);
ALTER TABLE public."credit_ouverture" ADD COLUMN IF NOT EXISTS "montant" numeric(12,2);
ALTER TABLE public."credit_ouverture" ADD COLUMN IF NOT EXISTS sync_chargement integer;
ALTER TABLE public."credit_ouverture" ADD COLUMN IF NOT EXISTS sync_absente_le timestamp with time zone;
ALTER TABLE public."credit_ouverture" ENABLE ROW LEVEL SECURITY;
ALTER TABLE public."credit_ouverture" FORCE ROW LEVEL SECURITY;
DROP POLICY IF EXISTS cloisonnement ON public."credit_ouverture";
CREATE POLICY cloisonnement ON public."credit_ouverture" USING (officine_id = cloud_officine_courante()) WITH CHECK (officine_id = cloud_officine_courante());
GRANT SELECT, INSERT, UPDATE, DELETE ON public."credit_ouverture" TO pharma_cloud_api;
INSERT INTO cloud_table_miroir (nom_table, cle, colonnes) VALUES ('credit_ouverture', '{patient_id}', '{patient_id,montant,officine_id}')
    ON CONFLICT (nom_table) DO UPDATE SET cle = EXCLUDED.cle, colonnes = EXCLUDED.colonnes;

CREATE TABLE IF NOT EXISTS public."demande_patient" (
    officine_id uuid NOT NULL,
    "id" bigint NOT NULL,
    sync_epoque integer NOT NULL,
    sync_numero bigint NOT NULL,
    sync_recu_le timestamp with time zone DEFAULT now() NOT NULL,
    PRIMARY KEY (officine_id, "id")
);
ALTER TABLE public."demande_patient" ADD COLUMN IF NOT EXISTS "patient_id" text;
ALTER TABLE public."demande_patient" ADD COLUMN IF NOT EXISTS "produit_id" bigint;
ALTER TABLE public."demande_patient" ADD COLUMN IF NOT EXISTS "quantite" integer;
ALTER TABLE public."demande_patient" ADD COLUMN IF NOT EXISTS "cree_le" timestamp with time zone;
ALTER TABLE public."demande_patient" ADD COLUMN IF NOT EXISTS "cree_par" bigint;
ALTER TABLE public."demande_patient" ADD COLUMN IF NOT EXISTS "statut" text;
ALTER TABLE public."demande_patient" ADD COLUMN IF NOT EXISTS "prevenu_le" timestamp with time zone;
ALTER TABLE public."demande_patient" ADD COLUMN IF NOT EXISTS "prevenu_par" bigint;
ALTER TABLE public."demande_patient" ADD COLUMN IF NOT EXISTS "prevenu_canal" text;
ALTER TABLE public."demande_patient" ADD COLUMN IF NOT EXISTS "cloture_le" timestamp with time zone;
ALTER TABLE public."demande_patient" ADD COLUMN IF NOT EXISTS "cloture_par" bigint;
ALTER TABLE public."demande_patient" ADD COLUMN IF NOT EXISTS sync_chargement integer;
ALTER TABLE public."demande_patient" ADD COLUMN IF NOT EXISTS sync_absente_le timestamp with time zone;
ALTER TABLE public."demande_patient" ENABLE ROW LEVEL SECURITY;
ALTER TABLE public."demande_patient" FORCE ROW LEVEL SECURITY;
DROP POLICY IF EXISTS cloisonnement ON public."demande_patient";
CREATE POLICY cloisonnement ON public."demande_patient" USING (officine_id = cloud_officine_courante()) WITH CHECK (officine_id = cloud_officine_courante());
GRANT SELECT, INSERT, UPDATE, DELETE ON public."demande_patient" TO pharma_cloud_api;
INSERT INTO cloud_table_miroir (nom_table, cle, colonnes) VALUES ('demande_patient', '{id}', '{id,patient_id,produit_id,quantite,cree_le,cree_par,statut,prevenu_le,prevenu_par,prevenu_canal,cloture_le,cloture_par,officine_id}')
    ON CONFLICT (nom_table) DO UPDATE SET cle = EXCLUDED.cle, colonnes = EXCLUDED.colonnes;

CREATE TABLE IF NOT EXISTS public."ecart_caisse_vu" (
    officine_id uuid NOT NULL,
    "session_id" bigint NOT NULL,
    sync_epoque integer NOT NULL,
    sync_numero bigint NOT NULL,
    sync_recu_le timestamp with time zone DEFAULT now() NOT NULL,
    PRIMARY KEY (officine_id, "session_id")
);
ALTER TABLE public."ecart_caisse_vu" ADD COLUMN IF NOT EXISTS "vu_le" timestamp with time zone;
ALTER TABLE public."ecart_caisse_vu" ADD COLUMN IF NOT EXISTS "vu_par" bigint;
ALTER TABLE public."ecart_caisse_vu" ADD COLUMN IF NOT EXISTS sync_chargement integer;
ALTER TABLE public."ecart_caisse_vu" ADD COLUMN IF NOT EXISTS sync_absente_le timestamp with time zone;
ALTER TABLE public."ecart_caisse_vu" ENABLE ROW LEVEL SECURITY;
ALTER TABLE public."ecart_caisse_vu" FORCE ROW LEVEL SECURITY;
DROP POLICY IF EXISTS cloisonnement ON public."ecart_caisse_vu";
CREATE POLICY cloisonnement ON public."ecart_caisse_vu" USING (officine_id = cloud_officine_courante()) WITH CHECK (officine_id = cloud_officine_courante());
GRANT SELECT, INSERT, UPDATE, DELETE ON public."ecart_caisse_vu" TO pharma_cloud_api;
INSERT INTO cloud_table_miroir (nom_table, cle, colonnes) VALUES ('ecart_caisse_vu', '{session_id}', '{session_id,vu_le,vu_par,officine_id}')
    ON CONFLICT (nom_table) DO UPDATE SET cle = EXCLUDED.cle, colonnes = EXCLUDED.colonnes;

CREATE TABLE IF NOT EXISTS public."echeance_pret" (
    officine_id uuid NOT NULL,
    "id" bigint NOT NULL,
    sync_epoque integer NOT NULL,
    sync_numero bigint NOT NULL,
    sync_recu_le timestamp with time zone DEFAULT now() NOT NULL,
    PRIMARY KEY (officine_id, "id")
);
ALTER TABLE public."echeance_pret" ADD COLUMN IF NOT EXISTS "pret_id" bigint;
ALTER TABLE public."echeance_pret" ADD COLUMN IF NOT EXISTS "rang" integer;
ALTER TABLE public."echeance_pret" ADD COLUMN IF NOT EXISTS "date_echeance" date;
ALTER TABLE public."echeance_pret" ADD COLUMN IF NOT EXISTS "capital" numeric(12,2);
ALTER TABLE public."echeance_pret" ADD COLUMN IF NOT EXISTS "interets" numeric(12,2);
ALTER TABLE public."echeance_pret" ADD COLUMN IF NOT EXISTS "tva" numeric(12,2);
ALTER TABLE public."echeance_pret" ADD COLUMN IF NOT EXISTS "marge" numeric(12,2);
ALTER TABLE public."echeance_pret" ADD COLUMN IF NOT EXISTS "montant" numeric(12,2);
ALTER TABLE public."echeance_pret" ADD COLUMN IF NOT EXISTS "capital_restant" numeric(12,2);
ALTER TABLE public."echeance_pret" ADD COLUMN IF NOT EXISTS "assurance" numeric(12,2);
ALTER TABLE public."echeance_pret" ADD COLUMN IF NOT EXISTS sync_chargement integer;
ALTER TABLE public."echeance_pret" ADD COLUMN IF NOT EXISTS sync_absente_le timestamp with time zone;
ALTER TABLE public."echeance_pret" ENABLE ROW LEVEL SECURITY;
ALTER TABLE public."echeance_pret" FORCE ROW LEVEL SECURITY;
DROP POLICY IF EXISTS cloisonnement ON public."echeance_pret";
CREATE POLICY cloisonnement ON public."echeance_pret" USING (officine_id = cloud_officine_courante()) WITH CHECK (officine_id = cloud_officine_courante());
GRANT SELECT, INSERT, UPDATE, DELETE ON public."echeance_pret" TO pharma_cloud_api;
INSERT INTO cloud_table_miroir (nom_table, cle, colonnes) VALUES ('echeance_pret', '{id}', '{id,pret_id,rang,date_echeance,capital,interets,tva,marge,montant,capital_restant,assurance,officine_id}')
    ON CONFLICT (nom_table) DO UPDATE SET cle = EXCLUDED.cle, colonnes = EXCLUDED.colonnes;

CREATE TABLE IF NOT EXISTS public."emplacement" (
    officine_id uuid NOT NULL,
    "id" bigint NOT NULL,
    sync_epoque integer NOT NULL,
    sync_numero bigint NOT NULL,
    sync_recu_le timestamp with time zone DEFAULT now() NOT NULL,
    PRIMARY KEY (officine_id, "id")
);
ALTER TABLE public."emplacement" ADD COLUMN IF NOT EXISTS "code" text;
ALTER TABLE public."emplacement" ADD COLUMN IF NOT EXISTS "rayon" text;
ALTER TABLE public."emplacement" ADD COLUMN IF NOT EXISTS "etagere" text;
ALTER TABLE public."emplacement" ADD COLUMN IF NOT EXISTS "tiroir" text;
ALTER TABLE public."emplacement" ADD COLUMN IF NOT EXISTS "libelle" text;
ALTER TABLE public."emplacement" ADD COLUMN IF NOT EXISTS "zone" text;
ALTER TABLE public."emplacement" ADD COLUMN IF NOT EXISTS "actif" boolean;
ALTER TABLE public."emplacement" ADD COLUMN IF NOT EXISTS "cree_le" timestamp with time zone;
ALTER TABLE public."emplacement" ADD COLUMN IF NOT EXISTS sync_chargement integer;
ALTER TABLE public."emplacement" ADD COLUMN IF NOT EXISTS sync_absente_le timestamp with time zone;
ALTER TABLE public."emplacement" ENABLE ROW LEVEL SECURITY;
ALTER TABLE public."emplacement" FORCE ROW LEVEL SECURITY;
DROP POLICY IF EXISTS cloisonnement ON public."emplacement";
CREATE POLICY cloisonnement ON public."emplacement" USING (officine_id = cloud_officine_courante()) WITH CHECK (officine_id = cloud_officine_courante());
GRANT SELECT, INSERT, UPDATE, DELETE ON public."emplacement" TO pharma_cloud_api;
INSERT INTO cloud_table_miroir (nom_table, cle, colonnes) VALUES ('emplacement', '{id}', '{id,code,rayon,etagere,tiroir,libelle,zone,actif,cree_le,officine_id}')
    ON CONFLICT (nom_table) DO UPDATE SET cle = EXCLUDED.cle, colonnes = EXCLUDED.colonnes;

CREATE TABLE IF NOT EXISTS public."evenement_caisse" (
    officine_id uuid NOT NULL,
    "id" bigint NOT NULL,
    sync_epoque integer NOT NULL,
    sync_numero bigint NOT NULL,
    sync_recu_le timestamp with time zone DEFAULT now() NOT NULL,
    PRIMARY KEY (officine_id, "id")
);
ALTER TABLE public."evenement_caisse" ADD COLUMN IF NOT EXISTS "type" text;
ALTER TABLE public."evenement_caisse" ADD COLUMN IF NOT EXISTS "utilisateur_id" bigint;
ALTER TABLE public."evenement_caisse" ADD COLUMN IF NOT EXISTS "poste" text;
ALTER TABLE public."evenement_caisse" ADD COLUMN IF NOT EXISTS "session_caisse_id" bigint;
ALTER TABLE public."evenement_caisse" ADD COLUMN IF NOT EXISTS "produit_id" bigint;
ALTER TABLE public."evenement_caisse" ADD COLUMN IF NOT EXISTS "lot_id" bigint;
ALTER TABLE public."evenement_caisse" ADD COLUMN IF NOT EXISTS "quantite" integer;
ALTER TABLE public."evenement_caisse" ADD COLUMN IF NOT EXISTS "montant" numeric(12,2);
ALTER TABLE public."evenement_caisse" ADD COLUMN IF NOT EXISTS "detail" text;
ALTER TABLE public."evenement_caisse" ADD COLUMN IF NOT EXISTS "cree_le" timestamp with time zone;
ALTER TABLE public."evenement_caisse" ADD COLUMN IF NOT EXISTS sync_chargement integer;
ALTER TABLE public."evenement_caisse" ADD COLUMN IF NOT EXISTS sync_absente_le timestamp with time zone;
ALTER TABLE public."evenement_caisse" ENABLE ROW LEVEL SECURITY;
ALTER TABLE public."evenement_caisse" FORCE ROW LEVEL SECURITY;
DROP POLICY IF EXISTS cloisonnement ON public."evenement_caisse";
CREATE POLICY cloisonnement ON public."evenement_caisse" USING (officine_id = cloud_officine_courante()) WITH CHECK (officine_id = cloud_officine_courante());
GRANT SELECT, INSERT, UPDATE, DELETE ON public."evenement_caisse" TO pharma_cloud_api;
INSERT INTO cloud_table_miroir (nom_table, cle, colonnes) VALUES ('evenement_caisse', '{id}', '{id,type,utilisateur_id,poste,session_caisse_id,produit_id,lot_id,quantite,montant,detail,cree_le,officine_id}')
    ON CONFLICT (nom_table) DO UPDATE SET cle = EXCLUDED.cle, colonnes = EXCLUDED.colonnes;

CREATE TABLE IF NOT EXISTS public."facture_client" (
    officine_id uuid NOT NULL,
    "id" bigint NOT NULL,
    sync_epoque integer NOT NULL,
    sync_numero bigint NOT NULL,
    sync_recu_le timestamp with time zone DEFAULT now() NOT NULL,
    PRIMARY KEY (officine_id, "id")
);
ALTER TABLE public."facture_client" ADD COLUMN IF NOT EXISTS "type" text;
ALTER TABLE public."facture_client" ADD COLUMN IF NOT EXISTS "numero" text;
ALTER TABLE public."facture_client" ADD COLUMN IF NOT EXISTS "date_facture" date;
ALTER TABLE public."facture_client" ADD COLUMN IF NOT EXISTS "destinataire" text;
ALTER TABLE public."facture_client" ADD COLUMN IF NOT EXISTS "patient_id" text;
ALTER TABLE public."facture_client" ADD COLUMN IF NOT EXISTS "client_id" bigint;
ALTER TABLE public."facture_client" ADD COLUMN IF NOT EXISTS "organisme_id" bigint;
ALTER TABLE public."facture_client" ADD COLUMN IF NOT EXISTS "periode_du" date;
ALTER TABLE public."facture_client" ADD COLUMN IF NOT EXISTS "periode_au" date;
ALTER TABLE public."facture_client" ADD COLUMN IF NOT EXISTS "montant_ht" numeric(12,2);
ALTER TABLE public."facture_client" ADD COLUMN IF NOT EXISTS "montant_tva" numeric(12,2);
ALTER TABLE public."facture_client" ADD COLUMN IF NOT EXISTS "montant_ttc" numeric(12,2);
ALTER TABLE public."facture_client" ADD COLUMN IF NOT EXISTS "deja_paye" numeric(12,2);
ALTER TABLE public."facture_client" ADD COLUMN IF NOT EXISTS "statut" text;
ALTER TABLE public."facture_client" ADD COLUMN IF NOT EXISTS "version" integer;
ALTER TABLE public."facture_client" ADD COLUMN IF NOT EXISTS "motif_rejet" text;
ALTER TABLE public."facture_client" ADD COLUMN IF NOT EXISTS "remplace_id" bigint;
ALTER TABLE public."facture_client" ADD COLUMN IF NOT EXISTS "remplacee_par" bigint;
ALTER TABLE public."facture_client" ADD COLUMN IF NOT EXISTS "facture_origine_id" bigint;
ALTER TABLE public."facture_client" ADD COLUMN IF NOT EXISTS "motif" text;
ALTER TABLE public."facture_client" ADD COLUMN IF NOT EXISTS "hors_suivi" boolean;
ALTER TABLE public."facture_client" ADD COLUMN IF NOT EXISTS "motif_hors_suivi" text;
ALTER TABLE public."facture_client" ADD COLUMN IF NOT EXISTS "commentaire" text;
ALTER TABLE public."facture_client" ADD COLUMN IF NOT EXISTS "utilisateur_id" bigint;
ALTER TABLE public."facture_client" ADD COLUMN IF NOT EXISTS "cree_le" timestamp with time zone;
ALTER TABLE public."facture_client" ADD COLUMN IF NOT EXISTS "date_echeance" date;
ALTER TABLE public."facture_client" ADD COLUMN IF NOT EXISTS "part_credit" numeric(12,2);
ALTER TABLE public."facture_client" ADD COLUMN IF NOT EXISTS sync_chargement integer;
ALTER TABLE public."facture_client" ADD COLUMN IF NOT EXISTS sync_absente_le timestamp with time zone;
ALTER TABLE public."facture_client" ENABLE ROW LEVEL SECURITY;
ALTER TABLE public."facture_client" FORCE ROW LEVEL SECURITY;
DROP POLICY IF EXISTS cloisonnement ON public."facture_client";
CREATE POLICY cloisonnement ON public."facture_client" USING (officine_id = cloud_officine_courante()) WITH CHECK (officine_id = cloud_officine_courante());
GRANT SELECT, INSERT, UPDATE, DELETE ON public."facture_client" TO pharma_cloud_api;
INSERT INTO cloud_table_miroir (nom_table, cle, colonnes) VALUES ('facture_client', '{id}', '{id,type,numero,date_facture,destinataire,patient_id,client_id,organisme_id,periode_du,periode_au,montant_ht,montant_tva,montant_ttc,deja_paye,statut,version,motif_rejet,remplace_id,remplacee_par,facture_origine_id,motif,hors_suivi,motif_hors_suivi,commentaire,utilisateur_id,cree_le,date_echeance,part_credit,officine_id}')
    ON CONFLICT (nom_table) DO UPDATE SET cle = EXCLUDED.cle, colonnes = EXCLUDED.colonnes;

CREATE TABLE IF NOT EXISTS public."facture_client_ligne" (
    officine_id uuid NOT NULL,
    "id" bigint NOT NULL,
    sync_epoque integer NOT NULL,
    sync_numero bigint NOT NULL,
    sync_recu_le timestamp with time zone DEFAULT now() NOT NULL,
    PRIMARY KEY (officine_id, "id")
);
ALTER TABLE public."facture_client_ligne" ADD COLUMN IF NOT EXISTS "facture_id" bigint;
ALTER TABLE public."facture_client_ligne" ADD COLUMN IF NOT EXISTS "ordre" integer;
ALTER TABLE public."facture_client_ligne" ADD COLUMN IF NOT EXISTS "vente_id" bigint;
ALTER TABLE public."facture_client_ligne" ADD COLUMN IF NOT EXISTS "ligne_vente_id" bigint;
ALTER TABLE public."facture_client_ligne" ADD COLUMN IF NOT EXISTS "part" text;
ALTER TABLE public."facture_client_ligne" ADD COLUMN IF NOT EXISTS "designation" text;
ALTER TABLE public."facture_client_ligne" ADD COLUMN IF NOT EXISTS "quantite" numeric(10,2);
ALTER TABLE public."facture_client_ligne" ADD COLUMN IF NOT EXISTS "prix_unitaire_ttc" numeric(12,2);
ALTER TABLE public."facture_client_ligne" ADD COLUMN IF NOT EXISTS "taux_tva" numeric(5,2);
ALTER TABLE public."facture_client_ligne" ADD COLUMN IF NOT EXISTS "montant_ttc" numeric(12,2);
ALTER TABLE public."facture_client_ligne" ADD COLUMN IF NOT EXISTS sync_chargement integer;
ALTER TABLE public."facture_client_ligne" ADD COLUMN IF NOT EXISTS sync_absente_le timestamp with time zone;
ALTER TABLE public."facture_client_ligne" ENABLE ROW LEVEL SECURITY;
ALTER TABLE public."facture_client_ligne" FORCE ROW LEVEL SECURITY;
DROP POLICY IF EXISTS cloisonnement ON public."facture_client_ligne";
CREATE POLICY cloisonnement ON public."facture_client_ligne" USING (officine_id = cloud_officine_courante()) WITH CHECK (officine_id = cloud_officine_courante());
GRANT SELECT, INSERT, UPDATE, DELETE ON public."facture_client_ligne" TO pharma_cloud_api;
INSERT INTO cloud_table_miroir (nom_table, cle, colonnes) VALUES ('facture_client_ligne', '{id}', '{id,facture_id,ordre,vente_id,ligne_vente_id,part,designation,quantite,prix_unitaire_ttc,taux_tva,montant_ttc,officine_id}')
    ON CONFLICT (nom_table) DO UPDATE SET cle = EXCLUDED.cle, colonnes = EXCLUDED.colonnes;

CREATE TABLE IF NOT EXISTS public."facture_client_version" (
    officine_id uuid NOT NULL,
    "facture_id" bigint NOT NULL,
    "version" integer NOT NULL,
    sync_epoque integer NOT NULL,
    sync_numero bigint NOT NULL,
    sync_recu_le timestamp with time zone DEFAULT now() NOT NULL,
    PRIMARY KEY (officine_id, "facture_id", "version")
);
ALTER TABLE public."facture_client_version" ADD COLUMN IF NOT EXISTS "motif" text;
ALTER TABLE public."facture_client_version" ADD COLUMN IF NOT EXISTS "utilisateur_id" bigint;
ALTER TABLE public."facture_client_version" ADD COLUMN IF NOT EXISTS "cree_le" timestamp with time zone;
ALTER TABLE public."facture_client_version" ADD COLUMN IF NOT EXISTS sync_chargement integer;
ALTER TABLE public."facture_client_version" ADD COLUMN IF NOT EXISTS sync_absente_le timestamp with time zone;
ALTER TABLE public."facture_client_version" ENABLE ROW LEVEL SECURITY;
ALTER TABLE public."facture_client_version" FORCE ROW LEVEL SECURITY;
DROP POLICY IF EXISTS cloisonnement ON public."facture_client_version";
CREATE POLICY cloisonnement ON public."facture_client_version" USING (officine_id = cloud_officine_courante()) WITH CHECK (officine_id = cloud_officine_courante());
GRANT SELECT, INSERT, UPDATE, DELETE ON public."facture_client_version" TO pharma_cloud_api;
INSERT INTO cloud_table_miroir (nom_table, cle, colonnes) VALUES ('facture_client_version', '{facture_id,version}', '{facture_id,version,motif,utilisateur_id,cree_le,officine_id}')
    ON CONFLICT (nom_table) DO UPDATE SET cle = EXCLUDED.cle, colonnes = EXCLUDED.colonnes;

CREATE TABLE IF NOT EXISTS public."forcage_alerte_psychotrope" (
    officine_id uuid NOT NULL,
    "id" bigint NOT NULL,
    sync_epoque integer NOT NULL,
    sync_numero bigint NOT NULL,
    sync_recu_le timestamp with time zone DEFAULT now() NOT NULL,
    PRIMARY KEY (officine_id, "id")
);
ALTER TABLE public."forcage_alerte_psychotrope" ADD COLUMN IF NOT EXISTS "cree_le" timestamp with time zone;
ALTER TABLE public."forcage_alerte_psychotrope" ADD COLUMN IF NOT EXISTS "utilisateur_id" bigint;
ALTER TABLE public."forcage_alerte_psychotrope" ADD COLUMN IF NOT EXISTS "valide_par" bigint;
ALTER TABLE public."forcage_alerte_psychotrope" ADD COLUMN IF NOT EXISTS "codes" text[];
ALTER TABLE public."forcage_alerte_psychotrope" ADD COLUMN IF NOT EXISTS "motif" text;
ALTER TABLE public."forcage_alerte_psychotrope" ADD COLUMN IF NOT EXISTS "patient_id" text;
ALTER TABLE public."forcage_alerte_psychotrope" ADD COLUMN IF NOT EXISTS "medecin_id" bigint;
ALTER TABLE public."forcage_alerte_psychotrope" ADD COLUMN IF NOT EXISTS "numero_ordonnance" text;
ALTER TABLE public."forcage_alerte_psychotrope" ADD COLUMN IF NOT EXISTS "produits" bigint[];
ALTER TABLE public."forcage_alerte_psychotrope" ADD COLUMN IF NOT EXISTS "poste" text;
ALTER TABLE public."forcage_alerte_psychotrope" ADD COLUMN IF NOT EXISTS sync_chargement integer;
ALTER TABLE public."forcage_alerte_psychotrope" ADD COLUMN IF NOT EXISTS sync_absente_le timestamp with time zone;
ALTER TABLE public."forcage_alerte_psychotrope" ENABLE ROW LEVEL SECURITY;
ALTER TABLE public."forcage_alerte_psychotrope" FORCE ROW LEVEL SECURITY;
DROP POLICY IF EXISTS cloisonnement ON public."forcage_alerte_psychotrope";
CREATE POLICY cloisonnement ON public."forcage_alerte_psychotrope" USING (officine_id = cloud_officine_courante()) WITH CHECK (officine_id = cloud_officine_courante());
GRANT SELECT, INSERT, UPDATE, DELETE ON public."forcage_alerte_psychotrope" TO pharma_cloud_api;
INSERT INTO cloud_table_miroir (nom_table, cle, colonnes) VALUES ('forcage_alerte_psychotrope', '{id}', '{id,cree_le,utilisateur_id,valide_par,codes,motif,patient_id,medecin_id,numero_ordonnance,produits,poste,officine_id}')
    ON CONFLICT (nom_table) DO UPDATE SET cle = EXCLUDED.cle, colonnes = EXCLUDED.colonnes;

CREATE TABLE IF NOT EXISTS public."fournisseur" (
    officine_id uuid NOT NULL,
    "id" bigint NOT NULL,
    sync_epoque integer NOT NULL,
    sync_numero bigint NOT NULL,
    sync_recu_le timestamp with time zone DEFAULT now() NOT NULL,
    PRIMARY KEY (officine_id, "id")
);
ALTER TABLE public."fournisseur" ADD COLUMN IF NOT EXISTS "raison_sociale" text;
ALTER TABLE public."fournisseur" ADD COLUMN IF NOT EXISTS "nif" text;
ALTER TABLE public."fournisseur" ADD COLUMN IF NOT EXISTS "rc" text;
ALTER TABLE public."fournisseur" ADD COLUMN IF NOT EXISTS "telephone" text;
ALTER TABLE public."fournisseur" ADD COLUMN IF NOT EXISTS "adresse" text;
ALTER TABLE public."fournisseur" ADD COLUMN IF NOT EXISTS "solde" numeric(12,2);
ALTER TABLE public."fournisseur" ADD COLUMN IF NOT EXISTS "jours_orange" integer;
ALTER TABLE public."fournisseur" ADD COLUMN IF NOT EXISTS "jours_rouge" integer;
ALTER TABLE public."fournisseur" ADD COLUMN IF NOT EXISTS "delai_reprise_jours" integer;
ALTER TABLE public."fournisseur" ADD COLUMN IF NOT EXISTS "email" text;
ALTER TABLE public."fournisseur" ADD COLUMN IF NOT EXISTS "actif" boolean;
ALTER TABLE public."fournisseur" ADD COLUMN IF NOT EXISTS sync_chargement integer;
ALTER TABLE public."fournisseur" ADD COLUMN IF NOT EXISTS sync_absente_le timestamp with time zone;
ALTER TABLE public."fournisseur" ENABLE ROW LEVEL SECURITY;
ALTER TABLE public."fournisseur" FORCE ROW LEVEL SECURITY;
DROP POLICY IF EXISTS cloisonnement ON public."fournisseur";
CREATE POLICY cloisonnement ON public."fournisseur" USING (officine_id = cloud_officine_courante()) WITH CHECK (officine_id = cloud_officine_courante());
GRANT SELECT, INSERT, UPDATE, DELETE ON public."fournisseur" TO pharma_cloud_api;
INSERT INTO cloud_table_miroir (nom_table, cle, colonnes) VALUES ('fournisseur', '{id}', '{id,raison_sociale,nif,rc,telephone,adresse,solde,jours_orange,jours_rouge,delai_reprise_jours,email,actif,officine_id}')
    ON CONFLICT (nom_table) DO UPDATE SET cle = EXCLUDED.cle, colonnes = EXCLUDED.colonnes;

CREATE TABLE IF NOT EXISTS public."fournisseur_mouvement" (
    officine_id uuid NOT NULL,
    "id" bigint NOT NULL,
    sync_epoque integer NOT NULL,
    sync_numero bigint NOT NULL,
    sync_recu_le timestamp with time zone DEFAULT now() NOT NULL,
    PRIMARY KEY (officine_id, "id")
);
ALTER TABLE public."fournisseur_mouvement" ADD COLUMN IF NOT EXISTS "fournisseur_id" bigint;
ALTER TABLE public."fournisseur_mouvement" ADD COLUMN IF NOT EXISTS "type" text;
ALTER TABLE public."fournisseur_mouvement" ADD COLUMN IF NOT EXISTS "montant" numeric(12,2);
ALTER TABLE public."fournisseur_mouvement" ADD COLUMN IF NOT EXISTS "solde_apres" numeric(12,2);
ALTER TABLE public."fournisseur_mouvement" ADD COLUMN IF NOT EXISTS "utilisateur_id" bigint;
ALTER TABLE public."fournisseur_mouvement" ADD COLUMN IF NOT EXISTS "reference" text;
ALTER TABLE public."fournisseur_mouvement" ADD COLUMN IF NOT EXISTS "cree_le" timestamp with time zone;
ALTER TABLE public."fournisseur_mouvement" ADD COLUMN IF NOT EXISTS sync_chargement integer;
ALTER TABLE public."fournisseur_mouvement" ADD COLUMN IF NOT EXISTS sync_absente_le timestamp with time zone;
ALTER TABLE public."fournisseur_mouvement" ENABLE ROW LEVEL SECURITY;
ALTER TABLE public."fournisseur_mouvement" FORCE ROW LEVEL SECURITY;
DROP POLICY IF EXISTS cloisonnement ON public."fournisseur_mouvement";
CREATE POLICY cloisonnement ON public."fournisseur_mouvement" USING (officine_id = cloud_officine_courante()) WITH CHECK (officine_id = cloud_officine_courante());
GRANT SELECT, INSERT, UPDATE, DELETE ON public."fournisseur_mouvement" TO pharma_cloud_api;
INSERT INTO cloud_table_miroir (nom_table, cle, colonnes) VALUES ('fournisseur_mouvement', '{id}', '{id,fournisseur_id,type,montant,solde_apres,utilisateur_id,reference,cree_le,officine_id}')
    ON CONFLICT (nom_table) DO UPDATE SET cle = EXCLUDED.cle, colonnes = EXCLUDED.colonnes;

CREATE TABLE IF NOT EXISTS public."integrite_prise_acte" (
    officine_id uuid NOT NULL,
    "id" bigint NOT NULL,
    sync_epoque integer NOT NULL,
    sync_numero bigint NOT NULL,
    sync_recu_le timestamp with time zone DEFAULT now() NOT NULL,
    PRIMARY KEY (officine_id, "id")
);
ALTER TABLE public."integrite_prise_acte" ADD COLUMN IF NOT EXISTS "nouvelle_epoque" integer;
ALTER TABLE public."integrite_prise_acte" ADD COLUMN IF NOT EXISTS "cause" text;
ALTER TABLE public."integrite_prise_acte" ADD COLUMN IF NOT EXISTS "utilisateur_id" bigint;
ALTER TABLE public."integrite_prise_acte" ADD COLUMN IF NOT EXISTS "cree_le" timestamp with time zone;
ALTER TABLE public."integrite_prise_acte" ADD COLUMN IF NOT EXISTS sync_chargement integer;
ALTER TABLE public."integrite_prise_acte" ADD COLUMN IF NOT EXISTS sync_absente_le timestamp with time zone;
ALTER TABLE public."integrite_prise_acte" ENABLE ROW LEVEL SECURITY;
ALTER TABLE public."integrite_prise_acte" FORCE ROW LEVEL SECURITY;
DROP POLICY IF EXISTS cloisonnement ON public."integrite_prise_acte";
CREATE POLICY cloisonnement ON public."integrite_prise_acte" USING (officine_id = cloud_officine_courante()) WITH CHECK (officine_id = cloud_officine_courante());
GRANT SELECT, INSERT, UPDATE, DELETE ON public."integrite_prise_acte" TO pharma_cloud_api;
INSERT INTO cloud_table_miroir (nom_table, cle, colonnes) VALUES ('integrite_prise_acte', '{id}', '{id,nouvelle_epoque,cause,utilisateur_id,cree_le,officine_id}')
    ON CONFLICT (nom_table) DO UPDATE SET cle = EXCLUDED.cle, colonnes = EXCLUDED.colonnes;

CREATE TABLE IF NOT EXISTS public."inventaire" (
    officine_id uuid NOT NULL,
    "id" bigint NOT NULL,
    sync_epoque integer NOT NULL,
    sync_numero bigint NOT NULL,
    sync_recu_le timestamp with time zone DEFAULT now() NOT NULL,
    PRIMARY KEY (officine_id, "id")
);
ALTER TABLE public."inventaire" ADD COLUMN IF NOT EXISTS "rayon" text;
ALTER TABLE public."inventaire" ADD COLUMN IF NOT EXISTS "statut" text;
ALTER TABLE public."inventaire" ADD COLUMN IF NOT EXISTS "commence_le" timestamp with time zone;
ALTER TABLE public."inventaire" ADD COLUMN IF NOT EXISTS "commence_par" bigint;
ALTER TABLE public."inventaire" ADD COLUMN IF NOT EXISTS "termine_le" timestamp with time zone;
ALTER TABLE public."inventaire" ADD COLUMN IF NOT EXISTS "termine_par" bigint;
ALTER TABLE public."inventaire" ADD COLUMN IF NOT EXISTS "campagne_id" bigint;
ALTER TABLE public."inventaire" ADD COLUMN IF NOT EXISTS "produit_id" bigint;
ALTER TABLE public."inventaire" ADD COLUMN IF NOT EXISTS sync_chargement integer;
ALTER TABLE public."inventaire" ADD COLUMN IF NOT EXISTS sync_absente_le timestamp with time zone;
ALTER TABLE public."inventaire" ENABLE ROW LEVEL SECURITY;
ALTER TABLE public."inventaire" FORCE ROW LEVEL SECURITY;
DROP POLICY IF EXISTS cloisonnement ON public."inventaire";
CREATE POLICY cloisonnement ON public."inventaire" USING (officine_id = cloud_officine_courante()) WITH CHECK (officine_id = cloud_officine_courante());
GRANT SELECT, INSERT, UPDATE, DELETE ON public."inventaire" TO pharma_cloud_api;
INSERT INTO cloud_table_miroir (nom_table, cle, colonnes) VALUES ('inventaire', '{id}', '{id,rayon,statut,commence_le,commence_par,termine_le,termine_par,campagne_id,produit_id,officine_id}')
    ON CONFLICT (nom_table) DO UPDATE SET cle = EXCLUDED.cle, colonnes = EXCLUDED.colonnes;

CREATE TABLE IF NOT EXISTS public."journal_audit" (
    officine_id uuid NOT NULL,
    "id" bigint NOT NULL,
    sync_epoque integer NOT NULL,
    sync_numero bigint NOT NULL,
    sync_recu_le timestamp with time zone DEFAULT now() NOT NULL,
    PRIMARY KEY (officine_id, "id")
);
ALTER TABLE public."journal_audit" ADD COLUMN IF NOT EXISTS "utilisateur_id" bigint;
ALTER TABLE public."journal_audit" ADD COLUMN IF NOT EXISTS "action" text;
ALTER TABLE public."journal_audit" ADD COLUMN IF NOT EXISTS "table_cible" text;
ALTER TABLE public."journal_audit" ADD COLUMN IF NOT EXISTS "enregistrement_id" bigint;
ALTER TABLE public."journal_audit" ADD COLUMN IF NOT EXISTS "cree_le" timestamp with time zone;
ALTER TABLE public."journal_audit" ADD COLUMN IF NOT EXISTS "permission_code" text;
ALTER TABLE public."journal_audit" ADD COLUMN IF NOT EXISTS "autorise_par" bigint;
ALTER TABLE public."journal_audit" ADD COLUMN IF NOT EXISTS sync_chargement integer;
ALTER TABLE public."journal_audit" ADD COLUMN IF NOT EXISTS sync_absente_le timestamp with time zone;
ALTER TABLE public."journal_audit" ENABLE ROW LEVEL SECURITY;
ALTER TABLE public."journal_audit" FORCE ROW LEVEL SECURITY;
DROP POLICY IF EXISTS cloisonnement ON public."journal_audit";
CREATE POLICY cloisonnement ON public."journal_audit" USING (officine_id = cloud_officine_courante()) WITH CHECK (officine_id = cloud_officine_courante());
GRANT SELECT, INSERT, UPDATE, DELETE ON public."journal_audit" TO pharma_cloud_api;
INSERT INTO cloud_table_miroir (nom_table, cle, colonnes) VALUES ('journal_audit', '{id}', '{id,utilisateur_id,action,table_cible,enregistrement_id,cree_le,permission_code,autorise_par,officine_id}')
    ON CONFLICT (nom_table) DO UPDATE SET cle = EXCLUDED.cle, colonnes = EXCLUDED.colonnes;

CREATE TABLE IF NOT EXISTS public."journal_registre_psychotrope" (
    officine_id uuid NOT NULL,
    "id" bigint NOT NULL,
    sync_epoque integer NOT NULL,
    sync_numero bigint NOT NULL,
    sync_recu_le timestamp with time zone DEFAULT now() NOT NULL,
    PRIMARY KEY (officine_id, "id")
);
ALTER TABLE public."journal_registre_psychotrope" ADD COLUMN IF NOT EXISTS "nature" text;
ALTER TABLE public."journal_registre_psychotrope" ADD COLUMN IF NOT EXISTS "registre_id" bigint;
ALTER TABLE public."journal_registre_psychotrope" ADD COLUMN IF NOT EXISTS "numero_ordre" bigint;
ALTER TABLE public."journal_registre_psychotrope" ADD COLUMN IF NOT EXISTS "lot_id" bigint;
ALTER TABLE public."journal_registre_psychotrope" ADD COLUMN IF NOT EXISTS "ligne_vente_id" bigint;
ALTER TABLE public."journal_registre_psychotrope" ADD COLUMN IF NOT EXISTS "mouvement_id" bigint;
ALTER TABLE public."journal_registre_psychotrope" ADD COLUMN IF NOT EXISTS "type_mouvement" text;
ALTER TABLE public."journal_registre_psychotrope" ADD COLUMN IF NOT EXISTS "quantite" integer;
ALTER TABLE public."journal_registre_psychotrope" ADD COLUMN IF NOT EXISTS "solde_apres" integer;
ALTER TABLE public."journal_registre_psychotrope" ADD COLUMN IF NOT EXISTS "patient_id" text;
ALTER TABLE public."journal_registre_psychotrope" ADD COLUMN IF NOT EXISTS "medecin_id" bigint;
ALTER TABLE public."journal_registre_psychotrope" ADD COLUMN IF NOT EXISTS "ordonnance_id" bigint;
ALTER TABLE public."journal_registre_psychotrope" ADD COLUMN IF NOT EXISTS "utilisateur_id" bigint;
ALTER TABLE public."journal_registre_psychotrope" ADD COLUMN IF NOT EXISTS "reference" text;
ALTER TABLE public."journal_registre_psychotrope" ADD COLUMN IF NOT EXISTS "inscrit_le" timestamp with time zone;
ALTER TABLE public."journal_registre_psychotrope" ADD COLUMN IF NOT EXISTS "lignes_anterieures" bigint;
ALTER TABLE public."journal_registre_psychotrope" ADD COLUMN IF NOT EXISTS "empreinte_anterieure" bytea;
ALTER TABLE public."journal_registre_psychotrope" ADD COLUMN IF NOT EXISTS "dernier_registre_id" bigint;
ALTER TABLE public."journal_registre_psychotrope" ADD COLUMN IF NOT EXISTS "annulation_id" bigint;
ALTER TABLE public."journal_registre_psychotrope" ADD COLUMN IF NOT EXISTS "motif" text;
ALTER TABLE public."journal_registre_psychotrope" ADD COLUMN IF NOT EXISTS "cree_le" timestamp with time zone;
ALTER TABLE public."journal_registre_psychotrope" ADD COLUMN IF NOT EXISTS "cle_chaine" text;
ALTER TABLE public."journal_registre_psychotrope" ADD COLUMN IF NOT EXISTS "rang" bigint;
ALTER TABLE public."journal_registre_psychotrope" ADD COLUMN IF NOT EXISTS "hash" bytea;
ALTER TABLE public."journal_registre_psychotrope" ADD COLUMN IF NOT EXISTS "hash_precedent" bytea;
ALTER TABLE public."journal_registre_psychotrope" ADD COLUMN IF NOT EXISTS "porteur_code" text;
ALTER TABLE public."journal_registre_psychotrope" ADD COLUMN IF NOT EXISTS sync_chargement integer;
ALTER TABLE public."journal_registre_psychotrope" ADD COLUMN IF NOT EXISTS sync_absente_le timestamp with time zone;
ALTER TABLE public."journal_registre_psychotrope" ENABLE ROW LEVEL SECURITY;
ALTER TABLE public."journal_registre_psychotrope" FORCE ROW LEVEL SECURITY;
DROP POLICY IF EXISTS cloisonnement ON public."journal_registre_psychotrope";
CREATE POLICY cloisonnement ON public."journal_registre_psychotrope" USING (officine_id = cloud_officine_courante()) WITH CHECK (officine_id = cloud_officine_courante());
GRANT SELECT, INSERT, UPDATE, DELETE ON public."journal_registre_psychotrope" TO pharma_cloud_api;
INSERT INTO cloud_table_miroir (nom_table, cle, colonnes) VALUES ('journal_registre_psychotrope', '{id}', '{id,nature,registre_id,numero_ordre,lot_id,ligne_vente_id,mouvement_id,type_mouvement,quantite,solde_apres,patient_id,medecin_id,ordonnance_id,utilisateur_id,reference,inscrit_le,lignes_anterieures,empreinte_anterieure,dernier_registre_id,annulation_id,motif,cree_le,cle_chaine,rang,hash,hash_precedent,officine_id,porteur_code}')
    ON CONFLICT (nom_table) DO UPDATE SET cle = EXCLUDED.cle, colonnes = EXCLUDED.colonnes;

CREATE TABLE IF NOT EXISTS public."journal_sauvegarde" (
    officine_id uuid NOT NULL,
    "id" bigint NOT NULL,
    sync_epoque integer NOT NULL,
    sync_numero bigint NOT NULL,
    sync_recu_le timestamp with time zone DEFAULT now() NOT NULL,
    PRIMARY KEY (officine_id, "id")
);
ALTER TABLE public."journal_sauvegarde" ADD COLUMN IF NOT EXISTS "debut" timestamp with time zone;
ALTER TABLE public."journal_sauvegarde" ADD COLUMN IF NOT EXISTS "fin" timestamp with time zone;
ALTER TABLE public."journal_sauvegarde" ADD COLUMN IF NOT EXISTS "statut" text;
ALTER TABLE public."journal_sauvegarde" ADD COLUMN IF NOT EXISTS "declenchement" text;
ALTER TABLE public."journal_sauvegarde" ADD COLUMN IF NOT EXISTS "fichier" text;
ALTER TABLE public."journal_sauvegarde" ADD COLUMN IF NOT EXISTS "taille_octets" bigint;
ALTER TABLE public."journal_sauvegarde" ADD COLUMN IF NOT EXISTS "message" text;
ALTER TABLE public."journal_sauvegarde" ADD COLUMN IF NOT EXISTS "utilisateur_id" bigint;
ALTER TABLE public."journal_sauvegarde" ADD COLUMN IF NOT EXISTS sync_chargement integer;
ALTER TABLE public."journal_sauvegarde" ADD COLUMN IF NOT EXISTS sync_absente_le timestamp with time zone;
ALTER TABLE public."journal_sauvegarde" ENABLE ROW LEVEL SECURITY;
ALTER TABLE public."journal_sauvegarde" FORCE ROW LEVEL SECURITY;
DROP POLICY IF EXISTS cloisonnement ON public."journal_sauvegarde";
CREATE POLICY cloisonnement ON public."journal_sauvegarde" USING (officine_id = cloud_officine_courante()) WITH CHECK (officine_id = cloud_officine_courante());
GRANT SELECT, INSERT, UPDATE, DELETE ON public."journal_sauvegarde" TO pharma_cloud_api;
INSERT INTO cloud_table_miroir (nom_table, cle, colonnes) VALUES ('journal_sauvegarde', '{id}', '{id,debut,fin,statut,declenchement,fichier,taille_octets,message,utilisateur_id,officine_id}')
    ON CONFLICT (nom_table) DO UPDATE SET cle = EXCLUDED.cle, colonnes = EXCLUDED.colonnes;

CREATE TABLE IF NOT EXISTS public."journal_vente" (
    officine_id uuid NOT NULL,
    "id" bigint NOT NULL,
    sync_epoque integer NOT NULL,
    sync_numero bigint NOT NULL,
    sync_recu_le timestamp with time zone DEFAULT now() NOT NULL,
    PRIMARY KEY (officine_id, "id")
);
ALTER TABLE public."journal_vente" ADD COLUMN IF NOT EXISTS "evenement" text;
ALTER TABLE public."journal_vente" ADD COLUMN IF NOT EXISTS "numero_ticket" text;
ALTER TABLE public."journal_vente" ADD COLUMN IF NOT EXISTS "vente_id" bigint;
ALTER TABLE public."journal_vente" ADD COLUMN IF NOT EXISTS "annulation_id" bigint;
ALTER TABLE public."journal_vente" ADD COLUMN IF NOT EXISTS "retour_client_id" bigint;
ALTER TABLE public."journal_vente" ADD COLUMN IF NOT EXISTS "avoir_mouvement_id" bigint;
ALTER TABLE public."journal_vente" ADD COLUMN IF NOT EXISTS "regularisation_id" bigint;
ALTER TABLE public."journal_vente" ADD COLUMN IF NOT EXISTS "survenu_le" timestamp with time zone;
ALTER TABLE public."journal_vente" ADD COLUMN IF NOT EXISTS "enregistre_le" timestamp with time zone;
ALTER TABLE public."journal_vente" ADD COLUMN IF NOT EXISTS "session_caisse_id" bigint;
ALTER TABLE public."journal_vente" ADD COLUMN IF NOT EXISTS "poste" text;
ALTER TABLE public."journal_vente" ADD COLUMN IF NOT EXISTS "utilisateur_id" bigint;
ALTER TABLE public."journal_vente" ADD COLUMN IF NOT EXISTS "type_vente" text;
ALTER TABLE public."journal_vente" ADD COLUMN IF NOT EXISTS "montant_ttc" numeric(14,2);
ALTER TABLE public."journal_vente" ADD COLUMN IF NOT EXISTS "montant_ht" numeric(14,2);
ALTER TABLE public."journal_vente" ADD COLUMN IF NOT EXISTS "montant_tva" numeric(14,2);
ALTER TABLE public."journal_vente" ADD COLUMN IF NOT EXISTS "detail_tva" text;
ALTER TABLE public."journal_vente" ADD COLUMN IF NOT EXISTS "part_patient" numeric(14,2);
ALTER TABLE public."journal_vente" ADD COLUMN IF NOT EXISTS "part_organisme" numeric(14,2);
ALTER TABLE public."journal_vente" ADD COLUMN IF NOT EXISTS "paiements" text;
ALTER TABLE public."journal_vente" ADD COLUMN IF NOT EXISTS "montant_mouvement" numeric(14,2);
ALTER TABLE public."journal_vente" ADD COLUMN IF NOT EXISTS "motif" text;
ALTER TABLE public."journal_vente" ADD COLUMN IF NOT EXISTS "cle_chaine" text;
ALTER TABLE public."journal_vente" ADD COLUMN IF NOT EXISTS "rang" bigint;
ALTER TABLE public."journal_vente" ADD COLUMN IF NOT EXISTS "hash" bytea;
ALTER TABLE public."journal_vente" ADD COLUMN IF NOT EXISTS "hash_precedent" bytea;
ALTER TABLE public."journal_vente" ADD COLUMN IF NOT EXISTS sync_chargement integer;
ALTER TABLE public."journal_vente" ADD COLUMN IF NOT EXISTS sync_absente_le timestamp with time zone;
ALTER TABLE public."journal_vente" ENABLE ROW LEVEL SECURITY;
ALTER TABLE public."journal_vente" FORCE ROW LEVEL SECURITY;
DROP POLICY IF EXISTS cloisonnement ON public."journal_vente";
CREATE POLICY cloisonnement ON public."journal_vente" USING (officine_id = cloud_officine_courante()) WITH CHECK (officine_id = cloud_officine_courante());
GRANT SELECT, INSERT, UPDATE, DELETE ON public."journal_vente" TO pharma_cloud_api;
INSERT INTO cloud_table_miroir (nom_table, cle, colonnes) VALUES ('journal_vente', '{id}', '{id,evenement,numero_ticket,vente_id,annulation_id,retour_client_id,avoir_mouvement_id,regularisation_id,survenu_le,enregistre_le,session_caisse_id,poste,utilisateur_id,type_vente,montant_ttc,montant_ht,montant_tva,detail_tva,part_patient,part_organisme,paiements,montant_mouvement,motif,cle_chaine,rang,hash,hash_precedent,officine_id}')
    ON CONFLICT (nom_table) DO UPDATE SET cle = EXCLUDED.cle, colonnes = EXCLUDED.colonnes;

CREATE TABLE IF NOT EXISTS public."ligne_commande" (
    officine_id uuid NOT NULL,
    "id" bigint NOT NULL,
    sync_epoque integer NOT NULL,
    sync_numero bigint NOT NULL,
    sync_recu_le timestamp with time zone DEFAULT now() NOT NULL,
    PRIMARY KEY (officine_id, "id")
);
ALTER TABLE public."ligne_commande" ADD COLUMN IF NOT EXISTS "commande_id" bigint;
ALTER TABLE public."ligne_commande" ADD COLUMN IF NOT EXISTS "produit_id" bigint;
ALTER TABLE public."ligne_commande" ADD COLUMN IF NOT EXISTS "qte_commandee" integer;
ALTER TABLE public."ligne_commande" ADD COLUMN IF NOT EXISTS "qte_recue" integer;
ALTER TABLE public."ligne_commande" ADD COLUMN IF NOT EXISTS "prix_achat_ht" numeric(12,2);
ALTER TABLE public."ligne_commande" ADD COLUMN IF NOT EXISTS sync_chargement integer;
ALTER TABLE public."ligne_commande" ADD COLUMN IF NOT EXISTS sync_absente_le timestamp with time zone;
ALTER TABLE public."ligne_commande" ENABLE ROW LEVEL SECURITY;
ALTER TABLE public."ligne_commande" FORCE ROW LEVEL SECURITY;
DROP POLICY IF EXISTS cloisonnement ON public."ligne_commande";
CREATE POLICY cloisonnement ON public."ligne_commande" USING (officine_id = cloud_officine_courante()) WITH CHECK (officine_id = cloud_officine_courante());
GRANT SELECT, INSERT, UPDATE, DELETE ON public."ligne_commande" TO pharma_cloud_api;
INSERT INTO cloud_table_miroir (nom_table, cle, colonnes) VALUES ('ligne_commande', '{id}', '{id,commande_id,produit_id,qte_commandee,qte_recue,prix_achat_ht,officine_id}')
    ON CONFLICT (nom_table) DO UPDATE SET cle = EXCLUDED.cle, colonnes = EXCLUDED.colonnes;

CREATE TABLE IF NOT EXISTS public."ligne_inventaire" (
    officine_id uuid NOT NULL,
    "id" bigint NOT NULL,
    sync_epoque integer NOT NULL,
    sync_numero bigint NOT NULL,
    sync_recu_le timestamp with time zone DEFAULT now() NOT NULL,
    PRIMARY KEY (officine_id, "id")
);
ALTER TABLE public."ligne_inventaire" ADD COLUMN IF NOT EXISTS "inventaire_id" bigint;
ALTER TABLE public."ligne_inventaire" ADD COLUMN IF NOT EXISTS "lot_id" bigint;
ALTER TABLE public."ligne_inventaire" ADD COLUMN IF NOT EXISTS "theorique" integer;
ALTER TABLE public."ligne_inventaire" ADD COLUMN IF NOT EXISTS "compte" integer;
ALTER TABLE public."ligne_inventaire" ADD COLUMN IF NOT EXISTS "date_verifiee" date;
ALTER TABLE public."ligne_inventaire" ADD COLUMN IF NOT EXISTS "compte_par" bigint;
ALTER TABLE public."ligne_inventaire" ADD COLUMN IF NOT EXISTS "compte_le" timestamp with time zone;
ALTER TABLE public."ligne_inventaire" ADD COLUMN IF NOT EXISTS "ecart" integer;
ALTER TABLE public."ligne_inventaire" ADD COLUMN IF NOT EXISTS "valeur_ecart" numeric(12,2);
ALTER TABLE public."ligne_inventaire" ADD COLUMN IF NOT EXISTS "statut" text;
ALTER TABLE public."ligne_inventaire" ADD COLUMN IF NOT EXISTS "decide_par" bigint;
ALTER TABLE public."ligne_inventaire" ADD COLUMN IF NOT EXISTS "decide_le" timestamp with time zone;
ALTER TABLE public."ligne_inventaire" ADD COLUMN IF NOT EXISTS sync_chargement integer;
ALTER TABLE public."ligne_inventaire" ADD COLUMN IF NOT EXISTS sync_absente_le timestamp with time zone;
ALTER TABLE public."ligne_inventaire" ENABLE ROW LEVEL SECURITY;
ALTER TABLE public."ligne_inventaire" FORCE ROW LEVEL SECURITY;
DROP POLICY IF EXISTS cloisonnement ON public."ligne_inventaire";
CREATE POLICY cloisonnement ON public."ligne_inventaire" USING (officine_id = cloud_officine_courante()) WITH CHECK (officine_id = cloud_officine_courante());
GRANT SELECT, INSERT, UPDATE, DELETE ON public."ligne_inventaire" TO pharma_cloud_api;
INSERT INTO cloud_table_miroir (nom_table, cle, colonnes) VALUES ('ligne_inventaire', '{id}', '{id,inventaire_id,lot_id,theorique,compte,date_verifiee,compte_par,compte_le,ecart,valeur_ecart,statut,decide_par,decide_le,officine_id}')
    ON CONFLICT (nom_table) DO UPDATE SET cle = EXCLUDED.cle, colonnes = EXCLUDED.colonnes;

CREATE TABLE IF NOT EXISTS public."ligne_retour_fournisseur" (
    officine_id uuid NOT NULL,
    "id" bigint NOT NULL,
    sync_epoque integer NOT NULL,
    sync_numero bigint NOT NULL,
    sync_recu_le timestamp with time zone DEFAULT now() NOT NULL,
    PRIMARY KEY (officine_id, "id")
);
ALTER TABLE public."ligne_retour_fournisseur" ADD COLUMN IF NOT EXISTS "retour_id" bigint;
ALTER TABLE public."ligne_retour_fournisseur" ADD COLUMN IF NOT EXISTS "lot_id" bigint;
ALTER TABLE public."ligne_retour_fournisseur" ADD COLUMN IF NOT EXISTS "quantite" integer;
ALTER TABLE public."ligne_retour_fournisseur" ADD COLUMN IF NOT EXISTS sync_chargement integer;
ALTER TABLE public."ligne_retour_fournisseur" ADD COLUMN IF NOT EXISTS sync_absente_le timestamp with time zone;
ALTER TABLE public."ligne_retour_fournisseur" ENABLE ROW LEVEL SECURITY;
ALTER TABLE public."ligne_retour_fournisseur" FORCE ROW LEVEL SECURITY;
DROP POLICY IF EXISTS cloisonnement ON public."ligne_retour_fournisseur";
CREATE POLICY cloisonnement ON public."ligne_retour_fournisseur" USING (officine_id = cloud_officine_courante()) WITH CHECK (officine_id = cloud_officine_courante());
GRANT SELECT, INSERT, UPDATE, DELETE ON public."ligne_retour_fournisseur" TO pharma_cloud_api;
INSERT INTO cloud_table_miroir (nom_table, cle, colonnes) VALUES ('ligne_retour_fournisseur', '{id}', '{id,retour_id,lot_id,quantite,officine_id}')
    ON CONFLICT (nom_table) DO UPDATE SET cle = EXCLUDED.cle, colonnes = EXCLUDED.colonnes;

CREATE TABLE IF NOT EXISTS public."ligne_vente" (
    officine_id uuid NOT NULL,
    "id" bigint NOT NULL,
    sync_epoque integer NOT NULL,
    sync_numero bigint NOT NULL,
    sync_recu_le timestamp with time zone DEFAULT now() NOT NULL,
    PRIMARY KEY (officine_id, "id")
);
ALTER TABLE public."ligne_vente" ADD COLUMN IF NOT EXISTS "vente_id" bigint;
ALTER TABLE public."ligne_vente" ADD COLUMN IF NOT EXISTS "produit_id" bigint;
ALTER TABLE public."ligne_vente" ADD COLUMN IF NOT EXISTS "lot_id" bigint;
ALTER TABLE public."ligne_vente" ADD COLUMN IF NOT EXISTS "quantite" integer;
ALTER TABLE public."ligne_vente" ADD COLUMN IF NOT EXISTS "prix_unitaire" numeric(12,2);
ALTER TABLE public."ligne_vente" ADD COLUMN IF NOT EXISTS "base_remboursement" numeric(12,2);
ALTER TABLE public."ligne_vente" ADD COLUMN IF NOT EXISTS "taux_remboursement" numeric(5,2);
ALTER TABLE public."ligne_vente" ADD COLUMN IF NOT EXISTS "montant_organisme" numeric(12,2);
ALTER TABLE public."ligne_vente" ADD COLUMN IF NOT EXISTS "montant_patient" numeric(12,2);
ALTER TABLE public."ligne_vente" ADD COLUMN IF NOT EXISTS "shp" numeric(8,2);
ALTER TABLE public."ligne_vente" ADD COLUMN IF NOT EXISTS "raison_lot" text;
ALTER TABLE public."ligne_vente" ADD COLUMN IF NOT EXISTS "remise_auto_pct" numeric(5,2);
ALTER TABLE public."ligne_vente" ADD COLUMN IF NOT EXISTS "reduction_auto" numeric(12,2);
ALTER TABLE public."ligne_vente" ADD COLUMN IF NOT EXISTS "reduction_manuelle" numeric(12,2);
ALTER TABLE public."ligne_vente" ADD COLUMN IF NOT EXISTS "don_partiel" boolean;
ALTER TABLE public."ligne_vente" ADD COLUMN IF NOT EXISTS "prix_chifa" numeric(12,2);
ALTER TABLE public."ligne_vente" ADD COLUMN IF NOT EXISTS "choix_prix_chifa" text;
ALTER TABLE public."ligne_vente" ADD COLUMN IF NOT EXISTS "type_prestation_id" bigint;
ALTER TABLE public."ligne_vente" ADD COLUMN IF NOT EXISTS "libelle" text;
ALTER TABLE public."ligne_vente" ADD COLUMN IF NOT EXISTS "taux_tva" numeric(5,2);
ALTER TABLE public."ligne_vente" ADD COLUMN IF NOT EXISTS sync_chargement integer;
ALTER TABLE public."ligne_vente" ADD COLUMN IF NOT EXISTS sync_absente_le timestamp with time zone;
ALTER TABLE public."ligne_vente" ENABLE ROW LEVEL SECURITY;
ALTER TABLE public."ligne_vente" FORCE ROW LEVEL SECURITY;
DROP POLICY IF EXISTS cloisonnement ON public."ligne_vente";
CREATE POLICY cloisonnement ON public."ligne_vente" USING (officine_id = cloud_officine_courante()) WITH CHECK (officine_id = cloud_officine_courante());
GRANT SELECT, INSERT, UPDATE, DELETE ON public."ligne_vente" TO pharma_cloud_api;
INSERT INTO cloud_table_miroir (nom_table, cle, colonnes) VALUES ('ligne_vente', '{id}', '{id,vente_id,produit_id,lot_id,quantite,prix_unitaire,base_remboursement,taux_remboursement,montant_organisme,montant_patient,shp,raison_lot,remise_auto_pct,reduction_auto,reduction_manuelle,don_partiel,prix_chifa,choix_prix_chifa,type_prestation_id,libelle,taux_tva,officine_id}')
    ON CONFLICT (nom_table) DO UPDATE SET cle = EXCLUDED.cle, colonnes = EXCLUDED.colonnes;

CREATE TABLE IF NOT EXISTS public."lot" (
    officine_id uuid NOT NULL,
    "id" bigint NOT NULL,
    sync_epoque integer NOT NULL,
    sync_numero bigint NOT NULL,
    sync_recu_le timestamp with time zone DEFAULT now() NOT NULL,
    PRIMARY KEY (officine_id, "id")
);
ALTER TABLE public."lot" ADD COLUMN IF NOT EXISTS "produit_id" bigint;
ALTER TABLE public."lot" ADD COLUMN IF NOT EXISTS "ligne_commande_id" bigint;
ALTER TABLE public."lot" ADD COLUMN IF NOT EXISTS "numero_lot" text;
ALTER TABLE public."lot" ADD COLUMN IF NOT EXISTS "date_peremption" date;
ALTER TABLE public."lot" ADD COLUMN IF NOT EXISTS "quantite_disponible" integer;
ALTER TABLE public."lot" ADD COLUMN IF NOT EXISTS "prix_achat_ht" numeric(12,2);
ALTER TABLE public."lot" ADD COLUMN IF NOT EXISTS "reception_id" bigint;
ALTER TABLE public."lot" ADD COLUMN IF NOT EXISTS "fournisseur_id" bigint;
ALTER TABLE public."lot" ADD COLUMN IF NOT EXISTS "code_interne" text;
ALTER TABLE public."lot" ADD COLUMN IF NOT EXISTS "bloque" boolean;
ALTER TABLE public."lot" ADD COLUMN IF NOT EXISTS "motif_blocage" text;
ALTER TABLE public."lot" ADD COLUMN IF NOT EXISTS "date_a_verifier" boolean;
ALTER TABLE public."lot" ADD COLUMN IF NOT EXISTS "ppa" numeric(12,2);
ALTER TABLE public."lot" ADD COLUMN IF NOT EXISTS "shp" numeric(10,2);
ALTER TABLE public."lot" ADD COLUMN IF NOT EXISTS sync_chargement integer;
ALTER TABLE public."lot" ADD COLUMN IF NOT EXISTS sync_absente_le timestamp with time zone;
ALTER TABLE public."lot" ENABLE ROW LEVEL SECURITY;
ALTER TABLE public."lot" FORCE ROW LEVEL SECURITY;
DROP POLICY IF EXISTS cloisonnement ON public."lot";
CREATE POLICY cloisonnement ON public."lot" USING (officine_id = cloud_officine_courante()) WITH CHECK (officine_id = cloud_officine_courante());
GRANT SELECT, INSERT, UPDATE, DELETE ON public."lot" TO pharma_cloud_api;
INSERT INTO cloud_table_miroir (nom_table, cle, colonnes) VALUES ('lot', '{id}', '{id,produit_id,ligne_commande_id,numero_lot,date_peremption,quantite_disponible,prix_achat_ht,reception_id,fournisseur_id,code_interne,bloque,motif_blocage,date_a_verifier,ppa,shp,officine_id}')
    ON CONFLICT (nom_table) DO UPDATE SET cle = EXCLUDED.cle, colonnes = EXCLUDED.colonnes;

CREATE TABLE IF NOT EXISTS public."medecin" (
    officine_id uuid NOT NULL,
    "id" bigint NOT NULL,
    sync_epoque integer NOT NULL,
    sync_numero bigint NOT NULL,
    sync_recu_le timestamp with time zone DEFAULT now() NOT NULL,
    PRIMARY KEY (officine_id, "id")
);
ALTER TABLE public."medecin" ADD COLUMN IF NOT EXISTS "nom" text;
ALTER TABLE public."medecin" ADD COLUMN IF NOT EXISTS "specialite" text;
ALTER TABLE public."medecin" ADD COLUMN IF NOT EXISTS "wilaya" text;
ALTER TABLE public."medecin" ADD COLUMN IF NOT EXISTS sync_chargement integer;
ALTER TABLE public."medecin" ADD COLUMN IF NOT EXISTS sync_absente_le timestamp with time zone;
ALTER TABLE public."medecin" ENABLE ROW LEVEL SECURITY;
ALTER TABLE public."medecin" FORCE ROW LEVEL SECURITY;
DROP POLICY IF EXISTS cloisonnement ON public."medecin";
CREATE POLICY cloisonnement ON public."medecin" USING (officine_id = cloud_officine_courante()) WITH CHECK (officine_id = cloud_officine_courante());
GRANT SELECT, INSERT, UPDATE, DELETE ON public."medecin" TO pharma_cloud_api;
INSERT INTO cloud_table_miroir (nom_table, cle, colonnes) VALUES ('medecin', '{id}', '{id,nom,specialite,wilaya,officine_id}')
    ON CONFLICT (nom_table) DO UPDATE SET cle = EXCLUDED.cle, colonnes = EXCLUDED.colonnes;

CREATE TABLE IF NOT EXISTS public."motif_reglable" (
    officine_id uuid NOT NULL,
    "id" bigint NOT NULL,
    sync_epoque integer NOT NULL,
    sync_numero bigint NOT NULL,
    sync_recu_le timestamp with time zone DEFAULT now() NOT NULL,
    PRIMARY KEY (officine_id, "id")
);
ALTER TABLE public."motif_reglable" ADD COLUMN IF NOT EXISTS "liste" text;
ALTER TABLE public."motif_reglable" ADD COLUMN IF NOT EXISTS "code" text;
ALTER TABLE public."motif_reglable" ADD COLUMN IF NOT EXISTS "libelle" text;
ALTER TABLE public."motif_reglable" ADD COLUMN IF NOT EXISTS "actif" boolean;
ALTER TABLE public."motif_reglable" ADD COLUMN IF NOT EXISTS "systeme" boolean;
ALTER TABLE public."motif_reglable" ADD COLUMN IF NOT EXISTS "ordre" integer;
ALTER TABLE public."motif_reglable" ADD COLUMN IF NOT EXISTS "cree_le" timestamp with time zone;
ALTER TABLE public."motif_reglable" ADD COLUMN IF NOT EXISTS "modifie_le" timestamp with time zone;
ALTER TABLE public."motif_reglable" ADD COLUMN IF NOT EXISTS "effet" text;
ALTER TABLE public."motif_reglable" ADD COLUMN IF NOT EXISTS "detail" text;
ALTER TABLE public."motif_reglable" ADD COLUMN IF NOT EXISTS "modele_code_central" boolean;
ALTER TABLE public."motif_reglable" ADD COLUMN IF NOT EXISTS sync_chargement integer;
ALTER TABLE public."motif_reglable" ADD COLUMN IF NOT EXISTS sync_absente_le timestamp with time zone;
ALTER TABLE public."motif_reglable" ENABLE ROW LEVEL SECURITY;
ALTER TABLE public."motif_reglable" FORCE ROW LEVEL SECURITY;
DROP POLICY IF EXISTS cloisonnement ON public."motif_reglable";
CREATE POLICY cloisonnement ON public."motif_reglable" USING (officine_id = cloud_officine_courante()) WITH CHECK (officine_id = cloud_officine_courante());
GRANT SELECT, INSERT, UPDATE, DELETE ON public."motif_reglable" TO pharma_cloud_api;
INSERT INTO cloud_table_miroir (nom_table, cle, colonnes) VALUES ('motif_reglable', '{id}', '{id,liste,code,libelle,actif,systeme,ordre,cree_le,modifie_le,effet,detail,officine_id,modele_code_central}')
    ON CONFLICT (nom_table) DO UPDATE SET cle = EXCLUDED.cle, colonnes = EXCLUDED.colonnes;

CREATE TABLE IF NOT EXISTS public."mouvement_stock" (
    officine_id uuid NOT NULL,
    "id" bigint NOT NULL,
    sync_epoque integer NOT NULL,
    sync_numero bigint NOT NULL,
    sync_recu_le timestamp with time zone DEFAULT now() NOT NULL,
    PRIMARY KEY (officine_id, "id")
);
ALTER TABLE public."mouvement_stock" ADD COLUMN IF NOT EXISTS "lot_id" bigint;
ALTER TABLE public."mouvement_stock" ADD COLUMN IF NOT EXISTS "type" text;
ALTER TABLE public."mouvement_stock" ADD COLUMN IF NOT EXISTS "quantite" integer;
ALTER TABLE public."mouvement_stock" ADD COLUMN IF NOT EXISTS "reference" text;
ALTER TABLE public."mouvement_stock" ADD COLUMN IF NOT EXISTS "utilisateur_id" bigint;
ALTER TABLE public."mouvement_stock" ADD COLUMN IF NOT EXISTS "cree_le" timestamp with time zone;
ALTER TABLE public."mouvement_stock" ADD COLUMN IF NOT EXISTS "reception_id" bigint;
ALTER TABLE public."mouvement_stock" ADD COLUMN IF NOT EXISTS "prix_achat_ht" numeric(12,2);
ALTER TABLE public."mouvement_stock" ADD COLUMN IF NOT EXISTS sync_chargement integer;
ALTER TABLE public."mouvement_stock" ADD COLUMN IF NOT EXISTS sync_absente_le timestamp with time zone;
ALTER TABLE public."mouvement_stock" ENABLE ROW LEVEL SECURITY;
ALTER TABLE public."mouvement_stock" FORCE ROW LEVEL SECURITY;
DROP POLICY IF EXISTS cloisonnement ON public."mouvement_stock";
CREATE POLICY cloisonnement ON public."mouvement_stock" USING (officine_id = cloud_officine_courante()) WITH CHECK (officine_id = cloud_officine_courante());
GRANT SELECT, INSERT, UPDATE, DELETE ON public."mouvement_stock" TO pharma_cloud_api;
INSERT INTO cloud_table_miroir (nom_table, cle, colonnes) VALUES ('mouvement_stock', '{id}', '{id,lot_id,type,quantite,reference,utilisateur_id,cree_le,reception_id,prix_achat_ht,officine_id}')
    ON CONFLICT (nom_table) DO UPDATE SET cle = EXCLUDED.cle, colonnes = EXCLUDED.colonnes;

CREATE TABLE IF NOT EXISTS public."mouvement_tiroir" (
    officine_id uuid NOT NULL,
    "id" bigint NOT NULL,
    sync_epoque integer NOT NULL,
    sync_numero bigint NOT NULL,
    sync_recu_le timestamp with time zone DEFAULT now() NOT NULL,
    PRIMARY KEY (officine_id, "id")
);
ALTER TABLE public."mouvement_tiroir" ADD COLUMN IF NOT EXISTS "numero" text;
ALTER TABLE public."mouvement_tiroir" ADD COLUMN IF NOT EXISTS "evenement_caisse_id" bigint;
ALTER TABLE public."mouvement_tiroir" ADD COLUMN IF NOT EXISTS "session_caisse_id" bigint;
ALTER TABLE public."mouvement_tiroir" ADD COLUMN IF NOT EXISTS "utilisateur_id" bigint;
ALTER TABLE public."mouvement_tiroir" ADD COLUMN IF NOT EXISTS "poste" text;
ALTER TABLE public."mouvement_tiroir" ADD COLUMN IF NOT EXISTS "motif_code" text;
ALTER TABLE public."mouvement_tiroir" ADD COLUMN IF NOT EXISTS "motif_libelle" text;
ALTER TABLE public."mouvement_tiroir" ADD COLUMN IF NOT EXISTS "sens" text;
ALTER TABLE public."mouvement_tiroir" ADD COLUMN IF NOT EXISTS "detail" text;
ALTER TABLE public."mouvement_tiroir" ADD COLUMN IF NOT EXISTS "montant" numeric(12,2);
ALTER TABLE public."mouvement_tiroir" ADD COLUMN IF NOT EXISTS "categorie" text;
ALTER TABLE public."mouvement_tiroir" ADD COLUMN IF NOT EXISTS "libelle" text;
ALTER TABLE public."mouvement_tiroir" ADD COLUMN IF NOT EXISTS "fournisseur_id" bigint;
ALTER TABLE public."mouvement_tiroir" ADD COLUMN IF NOT EXISTS "beneficiaire" text;
ALTER TABLE public."mouvement_tiroir" ADD COLUMN IF NOT EXISTS "reference" text;
ALTER TABLE public."mouvement_tiroir" ADD COLUMN IF NOT EXISTS "patient_id" bigint;
ALTER TABLE public."mouvement_tiroir" ADD COLUMN IF NOT EXISTS "raison" text;
ALTER TABLE public."mouvement_tiroir" ADD COLUMN IF NOT EXISTS "reglement_fournisseur_id" bigint;
ALTER TABLE public."mouvement_tiroir" ADD COLUMN IF NOT EXISTS "mouvement_tresorerie_id" bigint;
ALTER TABLE public."mouvement_tiroir" ADD COLUMN IF NOT EXISTS "valide_par" bigint;
ALTER TABLE public."mouvement_tiroir" ADD COLUMN IF NOT EXISTS "corrige_id" bigint;
ALTER TABLE public."mouvement_tiroir" ADD COLUMN IF NOT EXISTS "motif_correction" text;
ALTER TABLE public."mouvement_tiroir" ADD COLUMN IF NOT EXISTS "cree_le" timestamp with time zone;
ALTER TABLE public."mouvement_tiroir" ADD COLUMN IF NOT EXISTS "regularisation_id" bigint;
ALTER TABLE public."mouvement_tiroir" ADD COLUMN IF NOT EXISTS sync_chargement integer;
ALTER TABLE public."mouvement_tiroir" ADD COLUMN IF NOT EXISTS sync_absente_le timestamp with time zone;
ALTER TABLE public."mouvement_tiroir" ENABLE ROW LEVEL SECURITY;
ALTER TABLE public."mouvement_tiroir" FORCE ROW LEVEL SECURITY;
DROP POLICY IF EXISTS cloisonnement ON public."mouvement_tiroir";
CREATE POLICY cloisonnement ON public."mouvement_tiroir" USING (officine_id = cloud_officine_courante()) WITH CHECK (officine_id = cloud_officine_courante());
GRANT SELECT, INSERT, UPDATE, DELETE ON public."mouvement_tiroir" TO pharma_cloud_api;
INSERT INTO cloud_table_miroir (nom_table, cle, colonnes) VALUES ('mouvement_tiroir', '{id}', '{id,numero,evenement_caisse_id,session_caisse_id,utilisateur_id,poste,motif_code,motif_libelle,sens,detail,montant,categorie,libelle,fournisseur_id,beneficiaire,reference,patient_id,raison,reglement_fournisseur_id,mouvement_tresorerie_id,valide_par,corrige_id,motif_correction,cree_le,regularisation_id,officine_id}')
    ON CONFLICT (nom_table) DO UPDATE SET cle = EXCLUDED.cle, colonnes = EXCLUDED.colonnes;

CREATE TABLE IF NOT EXISTS public."mouvement_tresorerie" (
    officine_id uuid NOT NULL,
    "id" bigint NOT NULL,
    sync_epoque integer NOT NULL,
    sync_numero bigint NOT NULL,
    sync_recu_le timestamp with time zone DEFAULT now() NOT NULL,
    PRIMARY KEY (officine_id, "id")
);
ALTER TABLE public."mouvement_tresorerie" ADD COLUMN IF NOT EXISTS "compte_id" bigint;
ALTER TABLE public."mouvement_tresorerie" ADD COLUMN IF NOT EXISTS "date_mouvement" date;
ALTER TABLE public."mouvement_tresorerie" ADD COLUMN IF NOT EXISTS "sens" text;
ALTER TABLE public."mouvement_tresorerie" ADD COLUMN IF NOT EXISTS "montant" numeric(12,2);
ALTER TABLE public."mouvement_tresorerie" ADD COLUMN IF NOT EXISTS "nature" text;
ALTER TABLE public."mouvement_tresorerie" ADD COLUMN IF NOT EXISTS "categorie" text;
ALTER TABLE public."mouvement_tresorerie" ADD COLUMN IF NOT EXISTS "libelle" text;
ALTER TABLE public."mouvement_tresorerie" ADD COLUMN IF NOT EXISTS "reference" text;
ALTER TABLE public."mouvement_tresorerie" ADD COLUMN IF NOT EXISTS "organisme_id" bigint;
ALTER TABLE public."mouvement_tresorerie" ADD COLUMN IF NOT EXISTS "bordereau_id" bigint;
ALTER TABLE public."mouvement_tresorerie" ADD COLUMN IF NOT EXISTS "session_caisse_id" bigint;
ALTER TABLE public."mouvement_tresorerie" ADD COLUMN IF NOT EXISTS "pendant_session" boolean;
ALTER TABLE public."mouvement_tresorerie" ADD COLUMN IF NOT EXISTS "automatique" boolean;
ALTER TABLE public."mouvement_tresorerie" ADD COLUMN IF NOT EXISTS "transfert_de" bigint;
ALTER TABLE public."mouvement_tresorerie" ADD COLUMN IF NOT EXISTS "utilisateur_id" bigint;
ALTER TABLE public."mouvement_tresorerie" ADD COLUMN IF NOT EXISTS "cree_le" timestamp with time zone;
ALTER TABLE public."mouvement_tresorerie" ADD COLUMN IF NOT EXISTS sync_chargement integer;
ALTER TABLE public."mouvement_tresorerie" ADD COLUMN IF NOT EXISTS sync_absente_le timestamp with time zone;
ALTER TABLE public."mouvement_tresorerie" ENABLE ROW LEVEL SECURITY;
ALTER TABLE public."mouvement_tresorerie" FORCE ROW LEVEL SECURITY;
DROP POLICY IF EXISTS cloisonnement ON public."mouvement_tresorerie";
CREATE POLICY cloisonnement ON public."mouvement_tresorerie" USING (officine_id = cloud_officine_courante()) WITH CHECK (officine_id = cloud_officine_courante());
GRANT SELECT, INSERT, UPDATE, DELETE ON public."mouvement_tresorerie" TO pharma_cloud_api;
INSERT INTO cloud_table_miroir (nom_table, cle, colonnes) VALUES ('mouvement_tresorerie', '{id}', '{id,compte_id,date_mouvement,sens,montant,nature,categorie,libelle,reference,organisme_id,bordereau_id,session_caisse_id,pendant_session,automatique,transfert_de,utilisateur_id,cree_le,officine_id}')
    ON CONFLICT (nom_table) DO UPDATE SET cle = EXCLUDED.cle, colonnes = EXCLUDED.colonnes;

CREATE TABLE IF NOT EXISTS public."paiement" (
    officine_id uuid NOT NULL,
    "id" bigint NOT NULL,
    sync_epoque integer NOT NULL,
    sync_numero bigint NOT NULL,
    sync_recu_le timestamp with time zone DEFAULT now() NOT NULL,
    PRIMARY KEY (officine_id, "id")
);
ALTER TABLE public."paiement" ADD COLUMN IF NOT EXISTS "vente_id" bigint;
ALTER TABLE public."paiement" ADD COLUMN IF NOT EXISTS "patient_id" text;
ALTER TABLE public."paiement" ADD COLUMN IF NOT EXISTS "mode" text;
ALTER TABLE public."paiement" ADD COLUMN IF NOT EXISTS "montant" numeric(12,2);
ALTER TABLE public."paiement" ADD COLUMN IF NOT EXISTS "utilisateur_id" bigint;
ALTER TABLE public."paiement" ADD COLUMN IF NOT EXISTS "cree_le" timestamp with time zone;
ALTER TABLE public."paiement" ADD COLUMN IF NOT EXISTS "session_caisse_id" bigint;
ALTER TABLE public."paiement" ADD COLUMN IF NOT EXISTS "numero_recu" text;
ALTER TABLE public."paiement" ADD COLUMN IF NOT EXISTS sync_chargement integer;
ALTER TABLE public."paiement" ADD COLUMN IF NOT EXISTS sync_absente_le timestamp with time zone;
ALTER TABLE public."paiement" ENABLE ROW LEVEL SECURITY;
ALTER TABLE public."paiement" FORCE ROW LEVEL SECURITY;
DROP POLICY IF EXISTS cloisonnement ON public."paiement";
CREATE POLICY cloisonnement ON public."paiement" USING (officine_id = cloud_officine_courante()) WITH CHECK (officine_id = cloud_officine_courante());
GRANT SELECT, INSERT, UPDATE, DELETE ON public."paiement" TO pharma_cloud_api;
INSERT INTO cloud_table_miroir (nom_table, cle, colonnes) VALUES ('paiement', '{id}', '{id,vente_id,patient_id,mode,montant,utilisateur_id,cree_le,session_caisse_id,numero_recu,officine_id}')
    ON CONFLICT (nom_table) DO UPDATE SET cle = EXCLUDED.cle, colonnes = EXCLUDED.colonnes;

CREATE TABLE IF NOT EXISTS public."paiement_echeance_pret" (
    officine_id uuid NOT NULL,
    "id" bigint NOT NULL,
    sync_epoque integer NOT NULL,
    sync_numero bigint NOT NULL,
    sync_recu_le timestamp with time zone DEFAULT now() NOT NULL,
    PRIMARY KEY (officine_id, "id")
);
ALTER TABLE public."paiement_echeance_pret" ADD COLUMN IF NOT EXISTS "echeance_id" bigint;
ALTER TABLE public."paiement_echeance_pret" ADD COLUMN IF NOT EXISTS "date_paiement" date;
ALTER TABLE public."paiement_echeance_pret" ADD COLUMN IF NOT EXISTS "moyen" text;
ALTER TABLE public."paiement_echeance_pret" ADD COLUMN IF NOT EXISTS "compte_id" bigint;
ALTER TABLE public."paiement_echeance_pret" ADD COLUMN IF NOT EXISTS "reference" text;
ALTER TABLE public."paiement_echeance_pret" ADD COLUMN IF NOT EXISTS "part_tiroir" numeric(12,2);
ALTER TABLE public."paiement_echeance_pret" ADD COLUMN IF NOT EXISTS "versement_id" bigint;
ALTER TABLE public."paiement_echeance_pret" ADD COLUMN IF NOT EXISTS "mouvement_capital_id" bigint;
ALTER TABLE public."paiement_echeance_pret" ADD COLUMN IF NOT EXISTS "mouvement_charges_id" bigint;
ALTER TABLE public."paiement_echeance_pret" ADD COLUMN IF NOT EXISTS "utilisateur_id" bigint;
ALTER TABLE public."paiement_echeance_pret" ADD COLUMN IF NOT EXISTS "cree_le" timestamp with time zone;
ALTER TABLE public."paiement_echeance_pret" ADD COLUMN IF NOT EXISTS sync_chargement integer;
ALTER TABLE public."paiement_echeance_pret" ADD COLUMN IF NOT EXISTS sync_absente_le timestamp with time zone;
ALTER TABLE public."paiement_echeance_pret" ENABLE ROW LEVEL SECURITY;
ALTER TABLE public."paiement_echeance_pret" FORCE ROW LEVEL SECURITY;
DROP POLICY IF EXISTS cloisonnement ON public."paiement_echeance_pret";
CREATE POLICY cloisonnement ON public."paiement_echeance_pret" USING (officine_id = cloud_officine_courante()) WITH CHECK (officine_id = cloud_officine_courante());
GRANT SELECT, INSERT, UPDATE, DELETE ON public."paiement_echeance_pret" TO pharma_cloud_api;
INSERT INTO cloud_table_miroir (nom_table, cle, colonnes) VALUES ('paiement_echeance_pret', '{id}', '{id,echeance_id,date_paiement,moyen,compte_id,reference,part_tiroir,versement_id,mouvement_capital_id,mouvement_charges_id,utilisateur_id,cree_le,officine_id}')
    ON CONFLICT (nom_table) DO UPDATE SET cle = EXCLUDED.cle, colonnes = EXCLUDED.colonnes;

CREATE TABLE IF NOT EXISTS public."paiement_effet" (
    officine_id uuid NOT NULL,
    "id" bigint NOT NULL,
    sync_epoque integer NOT NULL,
    sync_numero bigint NOT NULL,
    sync_recu_le timestamp with time zone DEFAULT now() NOT NULL,
    PRIMARY KEY (officine_id, "id")
);
ALTER TABLE public."paiement_effet" ADD COLUMN IF NOT EXISTS "reglement_id" bigint;
ALTER TABLE public."paiement_effet" ADD COLUMN IF NOT EXISTS "date_paiement" date;
ALTER TABLE public."paiement_effet" ADD COLUMN IF NOT EXISTS "moyen" text;
ALTER TABLE public."paiement_effet" ADD COLUMN IF NOT EXISTS "compte_id" bigint;
ALTER TABLE public."paiement_effet" ADD COLUMN IF NOT EXISTS "reference" text;
ALTER TABLE public."paiement_effet" ADD COLUMN IF NOT EXISTS "part_tiroir" numeric(12,2);
ALTER TABLE public."paiement_effet" ADD COLUMN IF NOT EXISTS "versement_id" bigint;
ALTER TABLE public."paiement_effet" ADD COLUMN IF NOT EXISTS "utilisateur_id" bigint;
ALTER TABLE public."paiement_effet" ADD COLUMN IF NOT EXISTS "cree_le" timestamp with time zone;
ALTER TABLE public."paiement_effet" ADD COLUMN IF NOT EXISTS sync_chargement integer;
ALTER TABLE public."paiement_effet" ADD COLUMN IF NOT EXISTS sync_absente_le timestamp with time zone;
ALTER TABLE public."paiement_effet" ENABLE ROW LEVEL SECURITY;
ALTER TABLE public."paiement_effet" FORCE ROW LEVEL SECURITY;
DROP POLICY IF EXISTS cloisonnement ON public."paiement_effet";
CREATE POLICY cloisonnement ON public."paiement_effet" USING (officine_id = cloud_officine_courante()) WITH CHECK (officine_id = cloud_officine_courante());
GRANT SELECT, INSERT, UPDATE, DELETE ON public."paiement_effet" TO pharma_cloud_api;
INSERT INTO cloud_table_miroir (nom_table, cle, colonnes) VALUES ('paiement_effet', '{id}', '{id,reglement_id,date_paiement,moyen,compte_id,reference,part_tiroir,versement_id,utilisateur_id,cree_le,officine_id}')
    ON CONFLICT (nom_table) DO UPDATE SET cle = EXCLUDED.cle, colonnes = EXCLUDED.colonnes;

CREATE TABLE IF NOT EXISTS public."parametre_alerte" (
    officine_id uuid NOT NULL,
    "id" integer NOT NULL,
    sync_epoque integer NOT NULL,
    sync_numero bigint NOT NULL,
    sync_recu_le timestamp with time zone DEFAULT now() NOT NULL,
    PRIMARY KEY (officine_id, "id")
);
ALTER TABLE public."parametre_alerte" ADD COLUMN IF NOT EXISTS "jours_orange" integer;
ALTER TABLE public."parametre_alerte" ADD COLUMN IF NOT EXISTS "jours_rouge" integer;
ALTER TABLE public."parametre_alerte" ADD COLUMN IF NOT EXISTS "horizon_risque_jours" integer;
ALTER TABLE public."parametre_alerte" ADD COLUMN IF NOT EXISTS "fenetre_conso_jours" integer;
ALTER TABLE public."parametre_alerte" ADD COLUMN IF NOT EXISTS "delai_fournisseur_jours" integer;
ALTER TABLE public."parametre_alerte" ADD COLUMN IF NOT EXISTS "jours_securite" integer;
ALTER TABLE public."parametre_alerte" ADD COLUMN IF NOT EXISTS "jours_cible_commande" integer;
ALTER TABLE public."parametre_alerte" ADD COLUMN IF NOT EXISTS "jours_dormant" integer;
ALTER TABLE public."parametre_alerte" ADD COLUMN IF NOT EXISTS "jours_surstock" integer;
ALTER TABLE public."parametre_alerte" ADD COLUMN IF NOT EXISTS "seuil_lot_delaisse" integer;
ALTER TABLE public."parametre_alerte" ADD COLUMN IF NOT EXISTS "seuil_lot_delaisse_collectif" integer;
ALTER TABLE public."parametre_alerte" ADD COLUMN IF NOT EXISTS sync_chargement integer;
ALTER TABLE public."parametre_alerte" ADD COLUMN IF NOT EXISTS sync_absente_le timestamp with time zone;
ALTER TABLE public."parametre_alerte" ENABLE ROW LEVEL SECURITY;
ALTER TABLE public."parametre_alerte" FORCE ROW LEVEL SECURITY;
DROP POLICY IF EXISTS cloisonnement ON public."parametre_alerte";
CREATE POLICY cloisonnement ON public."parametre_alerte" USING (officine_id = cloud_officine_courante()) WITH CHECK (officine_id = cloud_officine_courante());
GRANT SELECT, INSERT, UPDATE, DELETE ON public."parametre_alerte" TO pharma_cloud_api;
INSERT INTO cloud_table_miroir (nom_table, cle, colonnes) VALUES ('parametre_alerte', '{id}', '{id,jours_orange,jours_rouge,horizon_risque_jours,fenetre_conso_jours,delai_fournisseur_jours,jours_securite,jours_cible_commande,jours_dormant,jours_surstock,seuil_lot_delaisse,seuil_lot_delaisse_collectif,officine_id}')
    ON CONFLICT (nom_table) DO UPDATE SET cle = EXCLUDED.cle, colonnes = EXCLUDED.colonnes;

CREATE TABLE IF NOT EXISTS public."parametre_caisse" (
    officine_id uuid NOT NULL,
    "id" integer NOT NULL,
    sync_epoque integer NOT NULL,
    sync_numero bigint NOT NULL,
    sync_recu_le timestamp with time zone DEFAULT now() NOT NULL,
    PRIMARY KEY (officine_id, "id")
);
ALTER TABLE public."parametre_caisse" ADD COLUMN IF NOT EXISTS "comptage_aveugle" boolean;
ALTER TABLE public."parametre_caisse" ADD COLUMN IF NOT EXISTS "fond_caisse_defaut" numeric(12,2);
ALTER TABLE public."parametre_caisse" ADD COLUMN IF NOT EXISTS "seuil_ecart" numeric(12,2);
ALTER TABLE public."parametre_caisse" ADD COLUMN IF NOT EXISTS "deconnexion_minutes" integer;
ALTER TABLE public."parametre_caisse" ADD COLUMN IF NOT EXISTS "versement_cloture" text;
ALTER TABLE public."parametre_caisse" ADD COLUMN IF NOT EXISTS "delai_alerte_dette_jours" integer;
ALTER TABLE public."parametre_caisse" ADD COLUMN IF NOT EXISTS "plafond_credit_defaut" numeric(12,2);
ALTER TABLE public."parametre_caisse" ADD COLUMN IF NOT EXISTS "seuil_annulations" integer;
ALTER TABLE public."parametre_caisse" ADD COLUMN IF NOT EXISTS "autorisation_par_code" boolean;
ALTER TABLE public."parametre_caisse" ADD COLUMN IF NOT EXISTS "seuil_ecart_chifa" numeric(12,2);
ALTER TABLE public."parametre_caisse" ADD COLUMN IF NOT EXISTS "seuil_sortie_tiroir" numeric(12,2);
ALTER TABLE public."parametre_caisse" ADD COLUMN IF NOT EXISTS "delai_volet_chifa_jours" integer;
ALTER TABLE public."parametre_caisse" ADD COLUMN IF NOT EXISTS "jours_alerte_fin_droit" integer;
ALTER TABLE public."parametre_caisse" ADD COLUMN IF NOT EXISTS "chifa_delai_depot_jours" integer;
ALTER TABLE public."parametre_caisse" ADD COLUMN IF NOT EXISTS "chifa_delai_paiement_jours" integer;
ALTER TABLE public."parametre_caisse" ADD COLUMN IF NOT EXISTS "reprise_exiger_produits_historique" boolean;
ALTER TABLE public."parametre_caisse" ADD COLUMN IF NOT EXISTS "taches_laissees_poste" text;
ALTER TABLE public."parametre_caisse" ADD COLUMN IF NOT EXISTS sync_chargement integer;
ALTER TABLE public."parametre_caisse" ADD COLUMN IF NOT EXISTS sync_absente_le timestamp with time zone;
ALTER TABLE public."parametre_caisse" ENABLE ROW LEVEL SECURITY;
ALTER TABLE public."parametre_caisse" FORCE ROW LEVEL SECURITY;
DROP POLICY IF EXISTS cloisonnement ON public."parametre_caisse";
CREATE POLICY cloisonnement ON public."parametre_caisse" USING (officine_id = cloud_officine_courante()) WITH CHECK (officine_id = cloud_officine_courante());
GRANT SELECT, INSERT, UPDATE, DELETE ON public."parametre_caisse" TO pharma_cloud_api;
INSERT INTO cloud_table_miroir (nom_table, cle, colonnes) VALUES ('parametre_caisse', '{id}', '{id,comptage_aveugle,fond_caisse_defaut,seuil_ecart,deconnexion_minutes,versement_cloture,delai_alerte_dette_jours,plafond_credit_defaut,seuil_annulations,autorisation_par_code,seuil_ecart_chifa,seuil_sortie_tiroir,delai_volet_chifa_jours,jours_alerte_fin_droit,chifa_delai_depot_jours,chifa_delai_paiement_jours,reprise_exiger_produits_historique,officine_id,taches_laissees_poste}')
    ON CONFLICT (nom_table) DO UPDATE SET cle = EXCLUDED.cle, colonnes = EXCLUDED.colonnes;

CREATE TABLE IF NOT EXISTS public."parametre_fiscal" (
    officine_id uuid NOT NULL,
    "id" smallint NOT NULL,
    sync_epoque integer NOT NULL,
    sync_numero bigint NOT NULL,
    sync_recu_le timestamp with time zone DEFAULT now() NOT NULL,
    PRIMARY KEY (officine_id, "id")
);
ALTER TABLE public."parametre_fiscal" ADD COLUMN IF NOT EXISTS "conservation_annees" smallint;
ALTER TABLE public."parametre_fiscal" ADD COLUMN IF NOT EXISTS "format_export_dgi" text;
ALTER TABLE public."parametre_fiscal" ADD COLUMN IF NOT EXISTS "separateur_csv" text;
ALTER TABLE public."parametre_fiscal" ADD COLUMN IF NOT EXISTS "fuseau" text;
ALTER TABLE public."parametre_fiscal" ADD COLUMN IF NOT EXISTS sync_chargement integer;
ALTER TABLE public."parametre_fiscal" ADD COLUMN IF NOT EXISTS sync_absente_le timestamp with time zone;
ALTER TABLE public."parametre_fiscal" ENABLE ROW LEVEL SECURITY;
ALTER TABLE public."parametre_fiscal" FORCE ROW LEVEL SECURITY;
DROP POLICY IF EXISTS cloisonnement ON public."parametre_fiscal";
CREATE POLICY cloisonnement ON public."parametre_fiscal" USING (officine_id = cloud_officine_courante()) WITH CHECK (officine_id = cloud_officine_courante());
GRANT SELECT, INSERT, UPDATE, DELETE ON public."parametre_fiscal" TO pharma_cloud_api;
INSERT INTO cloud_table_miroir (nom_table, cle, colonnes) VALUES ('parametre_fiscal', '{id}', '{id,conservation_annees,format_export_dgi,separateur_csv,fuseau,officine_id}')
    ON CONFLICT (nom_table) DO UPDATE SET cle = EXCLUDED.cle, colonnes = EXCLUDED.colonnes;

CREATE TABLE IF NOT EXISTS public."parametre_formulaire_sms" (
    officine_id uuid NOT NULL,
    "id" integer NOT NULL,
    sync_epoque integer NOT NULL,
    sync_numero bigint NOT NULL,
    sync_recu_le timestamp with time zone DEFAULT now() NOT NULL,
    PRIMARY KEY (officine_id, "id")
);
ALTER TABLE public."parametre_formulaire_sms" ADD COLUMN IF NOT EXISTS "texte" text;
ALTER TABLE public."parametre_formulaire_sms" ADD COLUMN IF NOT EXISTS "modifie_le" timestamp with time zone;
ALTER TABLE public."parametre_formulaire_sms" ADD COLUMN IF NOT EXISTS "modifie_par" bigint;
ALTER TABLE public."parametre_formulaire_sms" ADD COLUMN IF NOT EXISTS sync_chargement integer;
ALTER TABLE public."parametre_formulaire_sms" ADD COLUMN IF NOT EXISTS sync_absente_le timestamp with time zone;
ALTER TABLE public."parametre_formulaire_sms" ENABLE ROW LEVEL SECURITY;
ALTER TABLE public."parametre_formulaire_sms" FORCE ROW LEVEL SECURITY;
DROP POLICY IF EXISTS cloisonnement ON public."parametre_formulaire_sms";
CREATE POLICY cloisonnement ON public."parametre_formulaire_sms" USING (officine_id = cloud_officine_courante()) WITH CHECK (officine_id = cloud_officine_courante());
GRANT SELECT, INSERT, UPDATE, DELETE ON public."parametre_formulaire_sms" TO pharma_cloud_api;
INSERT INTO cloud_table_miroir (nom_table, cle, colonnes) VALUES ('parametre_formulaire_sms', '{id}', '{id,texte,modifie_le,modifie_par,officine_id}')
    ON CONFLICT (nom_table) DO UPDATE SET cle = EXCLUDED.cle, colonnes = EXCLUDED.colonnes;

CREATE TABLE IF NOT EXISTS public."parametre_inventaire" (
    officine_id uuid NOT NULL,
    "id" integer NOT NULL,
    sync_epoque integer NOT NULL,
    sync_numero bigint NOT NULL,
    sync_recu_le timestamp with time zone DEFAULT now() NOT NULL,
    PRIMARY KEY (officine_id, "id")
);
ALTER TABLE public."parametre_inventaire" ADD COLUMN IF NOT EXISTS "seuil_boites" integer;
ALTER TABLE public."parametre_inventaire" ADD COLUMN IF NOT EXISTS "seuil_montant" numeric(12,2);
ALTER TABLE public."parametre_inventaire" ADD COLUMN IF NOT EXISTS sync_chargement integer;
ALTER TABLE public."parametre_inventaire" ADD COLUMN IF NOT EXISTS sync_absente_le timestamp with time zone;
ALTER TABLE public."parametre_inventaire" ENABLE ROW LEVEL SECURITY;
ALTER TABLE public."parametre_inventaire" FORCE ROW LEVEL SECURITY;
DROP POLICY IF EXISTS cloisonnement ON public."parametre_inventaire";
CREATE POLICY cloisonnement ON public."parametre_inventaire" USING (officine_id = cloud_officine_courante()) WITH CHECK (officine_id = cloud_officine_courante());
GRANT SELECT, INSERT, UPDATE, DELETE ON public."parametre_inventaire" TO pharma_cloud_api;
INSERT INTO cloud_table_miroir (nom_table, cle, colonnes) VALUES ('parametre_inventaire', '{id}', '{id,seuil_boites,seuil_montant,officine_id}')
    ON CONFLICT (nom_table) DO UPDATE SET cle = EXCLUDED.cle, colonnes = EXCLUDED.colonnes;

CREATE TABLE IF NOT EXISTS public."parametre_notification" (
    officine_id uuid NOT NULL,
    "id" integer NOT NULL,
    sync_epoque integer NOT NULL,
    sync_numero bigint NOT NULL,
    sync_recu_le timestamp with time zone DEFAULT now() NOT NULL,
    PRIMARY KEY (officine_id, "id")
);
ALTER TABLE public."parametre_notification" ADD COLUMN IF NOT EXISTS "smtp_serveur" text;
ALTER TABLE public."parametre_notification" ADD COLUMN IF NOT EXISTS "smtp_port" integer;
ALTER TABLE public."parametre_notification" ADD COLUMN IF NOT EXISTS "smtp_tls" boolean;
ALTER TABLE public."parametre_notification" ADD COLUMN IF NOT EXISTS "smtp_expediteur" text;
ALTER TABLE public."parametre_notification" ADD COLUMN IF NOT EXISTS "modem_port" text;
ALTER TABLE public."parametre_notification" ADD COLUMN IF NOT EXISTS "heure_rapport" time without time zone;
ALTER TABLE public."parametre_notification" ADD COLUMN IF NOT EXISTS "dernier_rapport" date;
ALTER TABLE public."parametre_notification" ADD COLUMN IF NOT EXISTS "sms_seuil_ecart" numeric(12,2);
ALTER TABLE public."parametre_notification" ADD COLUMN IF NOT EXISTS "sms_lot_bloque" boolean;
ALTER TABLE public."parametre_notification" ADD COLUMN IF NOT EXISTS "sms_sauvegarde" boolean;
ALTER TABLE public."parametre_notification" ADD COLUMN IF NOT EXISTS "signaler_sans_destinataire" boolean;
ALTER TABLE public."parametre_notification" ADD COLUMN IF NOT EXISTS sync_chargement integer;
ALTER TABLE public."parametre_notification" ADD COLUMN IF NOT EXISTS sync_absente_le timestamp with time zone;
ALTER TABLE public."parametre_notification" ENABLE ROW LEVEL SECURITY;
ALTER TABLE public."parametre_notification" FORCE ROW LEVEL SECURITY;
DROP POLICY IF EXISTS cloisonnement ON public."parametre_notification";
CREATE POLICY cloisonnement ON public."parametre_notification" USING (officine_id = cloud_officine_courante()) WITH CHECK (officine_id = cloud_officine_courante());
GRANT SELECT, INSERT, UPDATE, DELETE ON public."parametre_notification" TO pharma_cloud_api;
INSERT INTO cloud_table_miroir (nom_table, cle, colonnes) VALUES ('parametre_notification', '{id}', '{id,smtp_serveur,smtp_port,smtp_tls,smtp_expediteur,modem_port,heure_rapport,dernier_rapport,sms_seuil_ecart,sms_lot_bloque,sms_sauvegarde,signaler_sans_destinataire,officine_id}')
    ON CONFLICT (nom_table) DO UPDATE SET cle = EXCLUDED.cle, colonnes = EXCLUDED.colonnes;

CREATE TABLE IF NOT EXISTS public."parametre_rapport" (
    officine_id uuid NOT NULL,
    "id" integer NOT NULL,
    sync_epoque integer NOT NULL,
    sync_numero bigint NOT NULL,
    sync_recu_le timestamp with time zone DEFAULT now() NOT NULL,
    PRIMARY KEY (officine_id, "id")
);
ALTER TABLE public."parametre_rapport" ADD COLUMN IF NOT EXISTS "tva_reversee_perime" boolean;
ALTER TABLE public."parametre_rapport" ADD COLUMN IF NOT EXISTS "tva_reversee_casse" boolean;
ALTER TABLE public."parametre_rapport" ADD COLUMN IF NOT EXISTS "tva_reversee_don" boolean;
ALTER TABLE public."parametre_rapport" ADD COLUMN IF NOT EXISTS sync_chargement integer;
ALTER TABLE public."parametre_rapport" ADD COLUMN IF NOT EXISTS sync_absente_le timestamp with time zone;
ALTER TABLE public."parametre_rapport" ENABLE ROW LEVEL SECURITY;
ALTER TABLE public."parametre_rapport" FORCE ROW LEVEL SECURITY;
DROP POLICY IF EXISTS cloisonnement ON public."parametre_rapport";
CREATE POLICY cloisonnement ON public."parametre_rapport" USING (officine_id = cloud_officine_courante()) WITH CHECK (officine_id = cloud_officine_courante());
GRANT SELECT, INSERT, UPDATE, DELETE ON public."parametre_rapport" TO pharma_cloud_api;
INSERT INTO cloud_table_miroir (nom_table, cle, colonnes) VALUES ('parametre_rapport', '{id}', '{id,tva_reversee_perime,tva_reversee_casse,tva_reversee_don,officine_id}')
    ON CONFLICT (nom_table) DO UPDATE SET cle = EXCLUDED.cle, colonnes = EXCLUDED.colonnes;

CREATE TABLE IF NOT EXISTS public."parametre_sauvegarde" (
    officine_id uuid NOT NULL,
    "id" integer NOT NULL,
    sync_epoque integer NOT NULL,
    sync_numero bigint NOT NULL,
    sync_recu_le timestamp with time zone DEFAULT now() NOT NULL,
    PRIMARY KEY (officine_id, "id")
);
ALTER TABLE public."parametre_sauvegarde" ADD COLUMN IF NOT EXISTS "actif" boolean;
ALTER TABLE public."parametre_sauvegarde" ADD COLUMN IF NOT EXISTS "dossier" text;
ALTER TABLE public."parametre_sauvegarde" ADD COLUMN IF NOT EXISTS "heure" time without time zone;
ALTER TABLE public."parametre_sauvegarde" ADD COLUMN IF NOT EXISTS "conservation" integer;
ALTER TABLE public."parametre_sauvegarde" ADD COLUMN IF NOT EXISTS "derniere_execution" date;
ALTER TABLE public."parametre_sauvegarde" ADD COLUMN IF NOT EXISTS "retention_ged_jours" integer;
ALTER TABLE public."parametre_sauvegarde" ADD COLUMN IF NOT EXISTS "nettoyage_ged_annees" integer;
ALTER TABLE public."parametre_sauvegarde" ADD COLUMN IF NOT EXISTS "alerte_sans_disque_jours" integer;
ALTER TABLE public."parametre_sauvegarde" ADD COLUMN IF NOT EXISTS "suivi_disque_depuis" timestamp with time zone;
ALTER TABLE public."parametre_sauvegarde" ADD COLUMN IF NOT EXISTS "derniere_alerte_sans_disque" timestamp with time zone;
ALTER TABLE public."parametre_sauvegarde" ADD COLUMN IF NOT EXISTS sync_chargement integer;
ALTER TABLE public."parametre_sauvegarde" ADD COLUMN IF NOT EXISTS sync_absente_le timestamp with time zone;
ALTER TABLE public."parametre_sauvegarde" ENABLE ROW LEVEL SECURITY;
ALTER TABLE public."parametre_sauvegarde" FORCE ROW LEVEL SECURITY;
DROP POLICY IF EXISTS cloisonnement ON public."parametre_sauvegarde";
CREATE POLICY cloisonnement ON public."parametre_sauvegarde" USING (officine_id = cloud_officine_courante()) WITH CHECK (officine_id = cloud_officine_courante());
GRANT SELECT, INSERT, UPDATE, DELETE ON public."parametre_sauvegarde" TO pharma_cloud_api;
INSERT INTO cloud_table_miroir (nom_table, cle, colonnes) VALUES ('parametre_sauvegarde', '{id}', '{id,actif,dossier,heure,conservation,derniere_execution,retention_ged_jours,nettoyage_ged_annees,officine_id,alerte_sans_disque_jours,suivi_disque_depuis,derniere_alerte_sans_disque}')
    ON CONFLICT (nom_table) DO UPDATE SET cle = EXCLUDED.cle, colonnes = EXCLUDED.colonnes;

CREATE TABLE IF NOT EXISTS public."parametre_serialisation" (
    officine_id uuid NOT NULL,
    "id" integer NOT NULL,
    sync_epoque integer NOT NULL,
    sync_numero bigint NOT NULL,
    sync_recu_le timestamp with time zone DEFAULT now() NOT NULL,
    PRIMARY KEY (officine_id, "id")
);
ALTER TABLE public."parametre_serialisation" ADD COLUMN IF NOT EXISTS "actif" boolean;
ALTER TABLE public."parametre_serialisation" ADD COLUMN IF NOT EXISTS "connecteur" text;
ALTER TABLE public."parametre_serialisation" ADD COLUMN IF NOT EXISTS "factice_hors_ligne" boolean;
ALTER TABLE public."parametre_serialisation" ADD COLUMN IF NOT EXISTS "modifie_le" timestamp with time zone;
ALTER TABLE public."parametre_serialisation" ADD COLUMN IF NOT EXISTS sync_chargement integer;
ALTER TABLE public."parametre_serialisation" ADD COLUMN IF NOT EXISTS sync_absente_le timestamp with time zone;
ALTER TABLE public."parametre_serialisation" ENABLE ROW LEVEL SECURITY;
ALTER TABLE public."parametre_serialisation" FORCE ROW LEVEL SECURITY;
DROP POLICY IF EXISTS cloisonnement ON public."parametre_serialisation";
CREATE POLICY cloisonnement ON public."parametre_serialisation" USING (officine_id = cloud_officine_courante()) WITH CHECK (officine_id = cloud_officine_courante());
GRANT SELECT, INSERT, UPDATE, DELETE ON public."parametre_serialisation" TO pharma_cloud_api;
INSERT INTO cloud_table_miroir (nom_table, cle, colonnes) VALUES ('parametre_serialisation', '{id}', '{id,actif,connecteur,factice_hors_ligne,modifie_le,officine_id}')
    ON CONFLICT (nom_table) DO UPDATE SET cle = EXCLUDED.cle, colonnes = EXCLUDED.colonnes;

CREATE TABLE IF NOT EXISTS public."parametre_substitution" (
    officine_id uuid NOT NULL,
    "id" integer NOT NULL,
    sync_epoque integer NOT NULL,
    sync_numero bigint NOT NULL,
    sync_recu_le timestamp with time zone DEFAULT now() NOT NULL,
    PRIMARY KEY (officine_id, "id")
);
ALTER TABLE public."parametre_substitution" ADD COLUMN IF NOT EXISTS "equivalent_exact_seulement" boolean;
ALTER TABLE public."parametre_substitution" ADD COLUMN IF NOT EXISTS "modifie_le" timestamp with time zone;
ALTER TABLE public."parametre_substitution" ADD COLUMN IF NOT EXISTS "modifie_par" bigint;
ALTER TABLE public."parametre_substitution" ADD COLUMN IF NOT EXISTS sync_chargement integer;
ALTER TABLE public."parametre_substitution" ADD COLUMN IF NOT EXISTS sync_absente_le timestamp with time zone;
ALTER TABLE public."parametre_substitution" ENABLE ROW LEVEL SECURITY;
ALTER TABLE public."parametre_substitution" FORCE ROW LEVEL SECURITY;
DROP POLICY IF EXISTS cloisonnement ON public."parametre_substitution";
CREATE POLICY cloisonnement ON public."parametre_substitution" USING (officine_id = cloud_officine_courante()) WITH CHECK (officine_id = cloud_officine_courante());
GRANT SELECT, INSERT, UPDATE, DELETE ON public."parametre_substitution" TO pharma_cloud_api;
INSERT INTO cloud_table_miroir (nom_table, cle, colonnes) VALUES ('parametre_substitution', '{id}', '{id,equivalent_exact_seulement,modifie_le,modifie_par,officine_id}')
    ON CONFLICT (nom_table) DO UPDATE SET cle = EXCLUDED.cle, colonnes = EXCLUDED.colonnes;

CREATE TABLE IF NOT EXISTS public."parametre_surveillance" (
    officine_id uuid NOT NULL,
    "id" integer NOT NULL,
    sync_epoque integer NOT NULL,
    sync_numero bigint NOT NULL,
    sync_recu_le timestamp with time zone DEFAULT now() NOT NULL,
    PRIMARY KEY (officine_id, "id")
);
ALTER TABLE public."parametre_surveillance" ADD COLUMN IF NOT EXISTS "seuil_lignes_retirees" integer;
ALTER TABLE public."parametre_surveillance" ADD COLUMN IF NOT EXISTS "seuil_tiroir" integer;
ALTER TABLE public."parametre_surveillance" ADD COLUMN IF NOT EXISTS "seuil_attentes_abandonnees" integer;
ALTER TABLE public."parametre_surveillance" ADD COLUMN IF NOT EXISTS "seuil_remises" integer;
ALTER TABLE public."parametre_surveillance" ADD COLUMN IF NOT EXISTS "seuil_remises_patient" integer;
ALTER TABLE public."parametre_surveillance" ADD COLUMN IF NOT EXISTS "heure_ouverture" time without time zone;
ALTER TABLE public."parametre_surveillance" ADD COLUMN IF NOT EXISTS "heure_fermeture" time without time zone;
ALTER TABLE public."parametre_surveillance" ADD COLUMN IF NOT EXISTS "seuil_ajustements" integer;
ALTER TABLE public."parametre_surveillance" ADD COLUMN IF NOT EXISTS "seuil_casses" integer;
ALTER TABLE public."parametre_surveillance" ADD COLUMN IF NOT EXISTS "seuil_echantillons" integer;
ALTER TABLE public."parametre_surveillance" ADD COLUMN IF NOT EXISTS "jours_comptage_sensibles" integer;
ALTER TABLE public."parametre_surveillance" ADD COLUMN IF NOT EXISTS "reception_aveugle" boolean;
ALTER TABLE public."parametre_surveillance" ADD COLUMN IF NOT EXISTS "jours_avoir_retour" integer;
ALTER TABLE public."parametre_surveillance" ADD COLUMN IF NOT EXISTS "seuil_credit_nouveau_patient" numeric(12,2);
ALTER TABLE public."parametre_surveillance" ADD COLUMN IF NOT EXISTS "jours_patient_recent" integer;
ALTER TABLE public."parametre_surveillance" ADD COLUMN IF NOT EXISTS "seuil_credits_jour" numeric(12,2);
ALTER TABLE public."parametre_surveillance" ADD COLUMN IF NOT EXISTS "jours_credit_sans_remboursement" integer;
ALTER TABLE public."parametre_surveillance" ADD COLUMN IF NOT EXISTS "seuil_autorisations_valideur" integer;
ALTER TABLE public."parametre_surveillance" ADD COLUMN IF NOT EXISTS "seuil_autorisations_employe" integer;
ALTER TABLE public."parametre_surveillance" ADD COLUMN IF NOT EXISTS "minutes_postes_simultanes" integer;
ALTER TABLE public."parametre_surveillance" ADD COLUMN IF NOT EXISTS "seuil_changements_poste" integer;
ALTER TABLE public."parametre_surveillance" ADD COLUMN IF NOT EXISTS "seuil_baisses_prix" integer;
ALTER TABLE public."parametre_surveillance" ADD COLUMN IF NOT EXISTS "seuil_sorties_tiroir" integer;
ALTER TABLE public."parametre_surveillance" ADD COLUMN IF NOT EXISTS sync_chargement integer;
ALTER TABLE public."parametre_surveillance" ADD COLUMN IF NOT EXISTS sync_absente_le timestamp with time zone;
ALTER TABLE public."parametre_surveillance" ENABLE ROW LEVEL SECURITY;
ALTER TABLE public."parametre_surveillance" FORCE ROW LEVEL SECURITY;
DROP POLICY IF EXISTS cloisonnement ON public."parametre_surveillance";
CREATE POLICY cloisonnement ON public."parametre_surveillance" USING (officine_id = cloud_officine_courante()) WITH CHECK (officine_id = cloud_officine_courante());
GRANT SELECT, INSERT, UPDATE, DELETE ON public."parametre_surveillance" TO pharma_cloud_api;
INSERT INTO cloud_table_miroir (nom_table, cle, colonnes) VALUES ('parametre_surveillance', '{id}', '{id,seuil_lignes_retirees,seuil_tiroir,seuil_attentes_abandonnees,seuil_remises,seuil_remises_patient,heure_ouverture,heure_fermeture,seuil_ajustements,seuil_casses,seuil_echantillons,jours_comptage_sensibles,reception_aveugle,jours_avoir_retour,seuil_credit_nouveau_patient,jours_patient_recent,seuil_credits_jour,jours_credit_sans_remboursement,seuil_autorisations_valideur,seuil_autorisations_employe,minutes_postes_simultanes,seuil_changements_poste,seuil_baisses_prix,seuil_sorties_tiroir,officine_id}')
    ON CONFLICT (nom_table) DO UPDATE SET cle = EXCLUDED.cle, colonnes = EXCLUDED.colonnes;

CREATE TABLE IF NOT EXISTS public."parametre_tracabilite" (
    officine_id uuid NOT NULL,
    "id" integer NOT NULL,
    sync_epoque integer NOT NULL,
    sync_numero bigint NOT NULL,
    sync_recu_le timestamp with time zone DEFAULT now() NOT NULL,
    PRIMARY KEY (officine_id, "id")
);
ALTER TABLE public."parametre_tracabilite" ADD COLUMN IF NOT EXISTS "boite_obligatoire" boolean;
ALTER TABLE public."parametre_tracabilite" ADD COLUMN IF NOT EXISTS "patient_obligatoire" text;
ALTER TABLE public."parametre_tracabilite" ADD COLUMN IF NOT EXISTS sync_chargement integer;
ALTER TABLE public."parametre_tracabilite" ADD COLUMN IF NOT EXISTS sync_absente_le timestamp with time zone;
ALTER TABLE public."parametre_tracabilite" ENABLE ROW LEVEL SECURITY;
ALTER TABLE public."parametre_tracabilite" FORCE ROW LEVEL SECURITY;
DROP POLICY IF EXISTS cloisonnement ON public."parametre_tracabilite";
CREATE POLICY cloisonnement ON public."parametre_tracabilite" USING (officine_id = cloud_officine_courante()) WITH CHECK (officine_id = cloud_officine_courante());
GRANT SELECT, INSERT, UPDATE, DELETE ON public."parametre_tracabilite" TO pharma_cloud_api;
INSERT INTO cloud_table_miroir (nom_table, cle, colonnes) VALUES ('parametre_tracabilite', '{id}', '{id,boite_obligatoire,patient_obligatoire,officine_id}')
    ON CONFLICT (nom_table) DO UPDATE SET cle = EXCLUDED.cle, colonnes = EXCLUDED.colonnes;

CREATE TABLE IF NOT EXISTS public."patient" (
    officine_id uuid NOT NULL,
    "id" text NOT NULL,
    sync_epoque integer NOT NULL,
    sync_numero bigint NOT NULL,
    sync_recu_le timestamp with time zone DEFAULT now() NOT NULL,
    PRIMARY KEY (officine_id, "id")
);
ALTER TABLE public."patient" ADD COLUMN IF NOT EXISTS "organisme_id" bigint;
ALTER TABLE public."patient" ADD COLUMN IF NOT EXISTS "est_chronique" boolean;
ALTER TABLE public."patient" ADD COLUMN IF NOT EXISTS "plafond_credit" numeric(12,2);
ALTER TABLE public."patient" ADD COLUMN IF NOT EXISTS "solde_credit" numeric(12,2);
ALTER TABLE public."patient" ADD COLUMN IF NOT EXISTS "cree_le" timestamp with time zone;
ALTER TABLE public."patient" ADD COLUMN IF NOT EXISTS "cree_par" bigint;
ALTER TABLE public."patient" ADD COLUMN IF NOT EXISTS "lien" text;
ALTER TABLE public."patient" ADD COLUMN IF NOT EXISTS "assure_principal_id" text;
ALTER TABLE public."patient" ADD COLUMN IF NOT EXISTS "date_fin_droit" date;
ALTER TABLE public."patient" ADD COLUMN IF NOT EXISTS "centre_payeur_id" bigint;
ALTER TABLE public."patient" ADD COLUMN IF NOT EXISTS "consentement_sms" boolean;
ALTER TABLE public."patient" ADD COLUMN IF NOT EXISTS "consentement_sms_le" timestamp with time zone;
ALTER TABLE public."patient" ADD COLUMN IF NOT EXISTS "formulaire_sms_recu" boolean;
ALTER TABLE public."patient" ADD COLUMN IF NOT EXISTS "formulaire_sms_recu_le" timestamp with time zone;
ALTER TABLE public."patient" ADD COLUMN IF NOT EXISTS sync_chargement integer;
ALTER TABLE public."patient" ADD COLUMN IF NOT EXISTS sync_absente_le timestamp with time zone;
ALTER TABLE public."patient" ENABLE ROW LEVEL SECURITY;
ALTER TABLE public."patient" FORCE ROW LEVEL SECURITY;
DROP POLICY IF EXISTS cloisonnement ON public."patient";
CREATE POLICY cloisonnement ON public."patient" USING (officine_id = cloud_officine_courante()) WITH CHECK (officine_id = cloud_officine_courante());
GRANT SELECT, INSERT, UPDATE, DELETE ON public."patient" TO pharma_cloud_api;
INSERT INTO cloud_table_miroir (nom_table, cle, colonnes) VALUES ('patient', '{id}', '{id,organisme_id,est_chronique,plafond_credit,solde_credit,cree_le,cree_par,lien,assure_principal_id,date_fin_droit,centre_payeur_id,consentement_sms,consentement_sms_le,formulaire_sms_recu,formulaire_sms_recu_le,officine_id}')
    ON CONFLICT (nom_table) DO UPDATE SET cle = EXCLUDED.cle, colonnes = EXCLUDED.colonnes;

CREATE TABLE IF NOT EXISTS public."pharmacie" (
    officine_id uuid NOT NULL,
    "id" integer NOT NULL,
    sync_epoque integer NOT NULL,
    sync_numero bigint NOT NULL,
    sync_recu_le timestamp with time zone DEFAULT now() NOT NULL,
    PRIMARY KEY (officine_id, "id")
);
ALTER TABLE public."pharmacie" ADD COLUMN IF NOT EXISTS "nom" text;
ALTER TABLE public."pharmacie" ADD COLUMN IF NOT EXISTS "titulaire" text;
ALTER TABLE public."pharmacie" ADD COLUMN IF NOT EXISTS "adresse" text;
ALTER TABLE public."pharmacie" ADD COLUMN IF NOT EXISTS "telephone" text;
ALTER TABLE public."pharmacie" ADD COLUMN IF NOT EXISTS "nif" text;
ALTER TABLE public."pharmacie" ADD COLUMN IF NOT EXISTS "chifa_officine" text;
ALTER TABLE public."pharmacie" ADD COLUMN IF NOT EXISTS "langue_defaut" text;
ALTER TABLE public."pharmacie" ADD COLUMN IF NOT EXISTS "rc" text;
ALTER TABLE public."pharmacie" ADD COLUMN IF NOT EXISTS "nis" text;
ALTER TABLE public."pharmacie" ADD COLUMN IF NOT EXISTS "article_imposition" text;
ALTER TABLE public."pharmacie" ADD COLUMN IF NOT EXISTS sync_chargement integer;
ALTER TABLE public."pharmacie" ADD COLUMN IF NOT EXISTS sync_absente_le timestamp with time zone;
ALTER TABLE public."pharmacie" ENABLE ROW LEVEL SECURITY;
ALTER TABLE public."pharmacie" FORCE ROW LEVEL SECURITY;
DROP POLICY IF EXISTS cloisonnement ON public."pharmacie";
CREATE POLICY cloisonnement ON public."pharmacie" USING (officine_id = cloud_officine_courante()) WITH CHECK (officine_id = cloud_officine_courante());
GRANT SELECT, INSERT, UPDATE, DELETE ON public."pharmacie" TO pharma_cloud_api;
INSERT INTO cloud_table_miroir (nom_table, cle, colonnes) VALUES ('pharmacie', '{id}', '{id,nom,titulaire,adresse,telephone,nif,chifa_officine,langue_defaut,rc,nis,article_imposition,officine_id}')
    ON CONFLICT (nom_table) DO UPDATE SET cle = EXCLUDED.cle, colonnes = EXCLUDED.colonnes;

CREATE TABLE IF NOT EXISTS public."pret_bancaire" (
    officine_id uuid NOT NULL,
    "id" bigint NOT NULL,
    sync_epoque integer NOT NULL,
    sync_numero bigint NOT NULL,
    sync_recu_le timestamp with time zone DEFAULT now() NOT NULL,
    PRIMARY KEY (officine_id, "id")
);
ALTER TABLE public."pret_bancaire" ADD COLUMN IF NOT EXISTS "compte_id" bigint;
ALTER TABLE public."pret_bancaire" ADD COLUMN IF NOT EXISTS "reference" text;
ALTER TABLE public."pret_bancaire" ADD COLUMN IF NOT EXISTS "objet" text;
ALTER TABLE public."pret_bancaire" ADD COLUMN IF NOT EXISTS "montant" numeric(12,2);
ALTER TABLE public."pret_bancaire" ADD COLUMN IF NOT EXISTS "date_pret" date;
ALTER TABLE public."pret_bancaire" ADD COLUMN IF NOT EXISTS "document" text;
ALTER TABLE public."pret_bancaire" ADD COLUMN IF NOT EXISTS "utilisateur_id" bigint;
ALTER TABLE public."pret_bancaire" ADD COLUMN IF NOT EXISTS "cree_le" timestamp with time zone;
ALTER TABLE public."pret_bancaire" ADD COLUMN IF NOT EXISTS "preteur" text;
ALTER TABLE public."pret_bancaire" ADD COLUMN IF NOT EXISTS "taux" numeric(7,4);
ALTER TABLE public."pret_bancaire" ADD COLUMN IF NOT EXISTS "duree_mois" integer;
ALTER TABLE public."pret_bancaire" ADD COLUMN IF NOT EXISTS sync_chargement integer;
ALTER TABLE public."pret_bancaire" ADD COLUMN IF NOT EXISTS sync_absente_le timestamp with time zone;
ALTER TABLE public."pret_bancaire" ENABLE ROW LEVEL SECURITY;
ALTER TABLE public."pret_bancaire" FORCE ROW LEVEL SECURITY;
DROP POLICY IF EXISTS cloisonnement ON public."pret_bancaire";
CREATE POLICY cloisonnement ON public."pret_bancaire" USING (officine_id = cloud_officine_courante()) WITH CHECK (officine_id = cloud_officine_courante());
GRANT SELECT, INSERT, UPDATE, DELETE ON public."pret_bancaire" TO pharma_cloud_api;
INSERT INTO cloud_table_miroir (nom_table, cle, colonnes) VALUES ('pret_bancaire', '{id}', '{id,compte_id,reference,objet,montant,date_pret,document,utilisateur_id,cree_le,preteur,taux,duree_mois,officine_id}')
    ON CONFLICT (nom_table) DO UPDATE SET cle = EXCLUDED.cle, colonnes = EXCLUDED.colonnes;

CREATE TABLE IF NOT EXISTS public."produit" (
    officine_id uuid NOT NULL,
    "id" bigint NOT NULL,
    sync_epoque integer NOT NULL,
    sync_numero bigint NOT NULL,
    sync_recu_le timestamp with time zone DEFAULT now() NOT NULL,
    PRIMARY KEY (officine_id, "id")
);
ALTER TABLE public."produit" ADD COLUMN IF NOT EXISTS "num_enregistrement" text;
ALTER TABLE public."produit" ADD COLUMN IF NOT EXISTS "code_barre" text;
ALTER TABLE public."produit" ADD COLUMN IF NOT EXISTS "nom_commercial" text;
ALTER TABLE public."produit" ADD COLUMN IF NOT EXISTS "dci_id" bigint;
ALTER TABLE public."produit" ADD COLUMN IF NOT EXISTS "laboratoire_id" bigint;
ALTER TABLE public."produit" ADD COLUMN IF NOT EXISTS "dosage" text;
ALTER TABLE public."produit" ADD COLUMN IF NOT EXISTS "forme" text;
ALTER TABLE public."produit" ADD COLUMN IF NOT EXISTS "conditionnement" text;
ALTER TABLE public."produit" ADD COLUMN IF NOT EXISTS "prix_public" numeric(12,2);
ALTER TABLE public."produit" ADD COLUMN IF NOT EXISTS "tarif_reference" numeric(12,2);
ALTER TABLE public."produit" ADD COLUMN IF NOT EXISTS "remboursable" boolean;
ALTER TABLE public."produit" ADD COLUMN IF NOT EXISTS "taux_tva" numeric(5,2);
ALTER TABLE public."produit" ADD COLUMN IF NOT EXISTS "classe" text;
ALTER TABLE public."produit" ADD COLUMN IF NOT EXISTS "est_essentiel" boolean;
ALTER TABLE public."produit" ADD COLUMN IF NOT EXISTS "stock_minimum" integer;
ALTER TABLE public."produit" ADD COLUMN IF NOT EXISTS "actif" boolean;
ALTER TABLE public."produit" ADD COLUMN IF NOT EXISTS "cree_le" timestamp with time zone;
ALTER TABLE public."produit" ADD COLUMN IF NOT EXISTS "en_tension" boolean;
ALTER TABLE public."produit" ADD COLUMN IF NOT EXISTS "jours_orange" integer;
ALTER TABLE public."produit" ADD COLUMN IF NOT EXISTS "jours_rouge" integer;
ALTER TABLE public."produit" ADD COLUMN IF NOT EXISTS "emplacement_id" bigint;
ALTER TABLE public."produit" ADD COLUMN IF NOT EXISTS "reserve_id" bigint;
ALTER TABLE public."produit" ADD COLUMN IF NOT EXISTS "a_verifier" boolean;
ALTER TABLE public."produit" ADD COLUMN IF NOT EXISTS "liste_psychotrope" text;
ALTER TABLE public."produit" ADD COLUMN IF NOT EXISTS "categorie" text;
ALTER TABLE public."produit" ADD COLUMN IF NOT EXISTS "bloque_achat" boolean;
ALTER TABLE public."produit" ADD COLUMN IF NOT EXISTS "bloque_vente" boolean;
ALTER TABLE public."produit" ADD COLUMN IF NOT EXISTS "motif_blocage" text;
ALTER TABLE public."produit" ADD COLUMN IF NOT EXISTS "sensible" boolean;
ALTER TABLE public."produit" ADD COLUMN IF NOT EXISTS "classe_confirmee_par" bigint;
ALTER TABLE public."produit" ADD COLUMN IF NOT EXISTS "classe_confirmee_le" timestamp with time zone;
ALTER TABLE public."produit" ADD COLUMN IF NOT EXISTS "fabrication_locale" boolean;
ALTER TABLE public."produit" ADD COLUMN IF NOT EXISTS "contenance" integer;
ALTER TABLE public."produit" ADD COLUMN IF NOT EXISTS sync_chargement integer;
ALTER TABLE public."produit" ADD COLUMN IF NOT EXISTS sync_absente_le timestamp with time zone;
ALTER TABLE public."produit" ENABLE ROW LEVEL SECURITY;
ALTER TABLE public."produit" FORCE ROW LEVEL SECURITY;
DROP POLICY IF EXISTS cloisonnement ON public."produit";
CREATE POLICY cloisonnement ON public."produit" USING (officine_id = cloud_officine_courante()) WITH CHECK (officine_id = cloud_officine_courante());
GRANT SELECT, INSERT, UPDATE, DELETE ON public."produit" TO pharma_cloud_api;
INSERT INTO cloud_table_miroir (nom_table, cle, colonnes) VALUES ('produit', '{id}', '{id,num_enregistrement,code_barre,nom_commercial,dci_id,laboratoire_id,dosage,forme,conditionnement,prix_public,tarif_reference,remboursable,taux_tva,classe,est_essentiel,stock_minimum,actif,cree_le,en_tension,jours_orange,jours_rouge,emplacement_id,reserve_id,a_verifier,liste_psychotrope,categorie,bloque_achat,bloque_vente,motif_blocage,sensible,classe_confirmee_par,classe_confirmee_le,fabrication_locale,contenance,officine_id}')
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

CREATE TABLE IF NOT EXISTS public."reception" (
    officine_id uuid NOT NULL,
    "id" bigint NOT NULL,
    sync_epoque integer NOT NULL,
    sync_numero bigint NOT NULL,
    sync_recu_le timestamp with time zone DEFAULT now() NOT NULL,
    PRIMARY KEY (officine_id, "id")
);
ALTER TABLE public."reception" ADD COLUMN IF NOT EXISTS "fournisseur_id" bigint;
ALTER TABLE public."reception" ADD COLUMN IF NOT EXISTS "commande_id" bigint;
ALTER TABLE public."reception" ADD COLUMN IF NOT EXISTS "numero_bl" text;
ALTER TABLE public."reception" ADD COLUMN IF NOT EXISTS "numero_facture" text;
ALTER TABLE public."reception" ADD COLUMN IF NOT EXISTS "date_reception" timestamp with time zone;
ALTER TABLE public."reception" ADD COLUMN IF NOT EXISTS "utilisateur_id" bigint;
ALTER TABLE public."reception" ADD COLUMN IF NOT EXISTS "montant_ttc" numeric(12,2);
ALTER TABLE public."reception" ADD COLUMN IF NOT EXISTS "montant_ht" numeric(12,2);
ALTER TABLE public."reception" ADD COLUMN IF NOT EXISTS "remise" numeric(12,2);
ALTER TABLE public."reception" ADD COLUMN IF NOT EXISTS "montant_tva" numeric(12,2);
ALTER TABLE public."reception" ADD COLUMN IF NOT EXISTS "timbre" numeric(12,2);
ALTER TABLE public."reception" ADD COLUMN IF NOT EXISTS "shp_total" numeric(12,2);
ALTER TABLE public."reception" ADD COLUMN IF NOT EXISTS "date_facture" date;
ALTER TABLE public."reception" ADD COLUMN IF NOT EXISTS sync_chargement integer;
ALTER TABLE public."reception" ADD COLUMN IF NOT EXISTS sync_absente_le timestamp with time zone;
ALTER TABLE public."reception" ENABLE ROW LEVEL SECURITY;
ALTER TABLE public."reception" FORCE ROW LEVEL SECURITY;
DROP POLICY IF EXISTS cloisonnement ON public."reception";
CREATE POLICY cloisonnement ON public."reception" USING (officine_id = cloud_officine_courante()) WITH CHECK (officine_id = cloud_officine_courante());
GRANT SELECT, INSERT, UPDATE, DELETE ON public."reception" TO pharma_cloud_api;
INSERT INTO cloud_table_miroir (nom_table, cle, colonnes) VALUES ('reception', '{id}', '{id,fournisseur_id,commande_id,numero_bl,numero_facture,date_reception,utilisateur_id,montant_ttc,montant_ht,remise,montant_tva,timbre,shp_total,date_facture,officine_id}')
    ON CONFLICT (nom_table) DO UPDATE SET cle = EXCLUDED.cle, colonnes = EXCLUDED.colonnes;

CREATE TABLE IF NOT EXISTS public."reception_ligne" (
    officine_id uuid NOT NULL,
    "id" bigint NOT NULL,
    sync_epoque integer NOT NULL,
    sync_numero bigint NOT NULL,
    sync_recu_le timestamp with time zone DEFAULT now() NOT NULL,
    PRIMARY KEY (officine_id, "id")
);
ALTER TABLE public."reception_ligne" ADD COLUMN IF NOT EXISTS "reception_id" bigint;
ALTER TABLE public."reception_ligne" ADD COLUMN IF NOT EXISTS "lot_id" bigint;
ALTER TABLE public."reception_ligne" ADD COLUMN IF NOT EXISTS "produit_id" bigint;
ALTER TABLE public."reception_ligne" ADD COLUMN IF NOT EXISTS "quantite" integer;
ALTER TABLE public."reception_ligne" ADD COLUMN IF NOT EXISTS "prix_achat_ht" numeric(12,2);
ALTER TABLE public."reception_ligne" ADD COLUMN IF NOT EXISTS "remise_pct" numeric(5,2);
ALTER TABLE public."reception_ligne" ADD COLUMN IF NOT EXISTS "prix_net_ht" numeric(12,2);
ALTER TABLE public."reception_ligne" ADD COLUMN IF NOT EXISTS "ppa" numeric(12,2);
ALTER TABLE public."reception_ligne" ADD COLUMN IF NOT EXISTS "shp" numeric(8,2);
ALTER TABLE public."reception_ligne" ADD COLUMN IF NOT EXISTS "taux_tva" numeric(5,2);
ALTER TABLE public."reception_ligne" ADD COLUMN IF NOT EXISTS "ug" integer;
ALTER TABLE public."reception_ligne" ADD COLUMN IF NOT EXISTS "ug_cout" text;
ALTER TABLE public."reception_ligne" ADD COLUMN IF NOT EXISTS "prix_catalogue_remplace" boolean;
ALTER TABLE public."reception_ligne" ADD COLUMN IF NOT EXISTS sync_chargement integer;
ALTER TABLE public."reception_ligne" ADD COLUMN IF NOT EXISTS sync_absente_le timestamp with time zone;
ALTER TABLE public."reception_ligne" ENABLE ROW LEVEL SECURITY;
ALTER TABLE public."reception_ligne" FORCE ROW LEVEL SECURITY;
DROP POLICY IF EXISTS cloisonnement ON public."reception_ligne";
CREATE POLICY cloisonnement ON public."reception_ligne" USING (officine_id = cloud_officine_courante()) WITH CHECK (officine_id = cloud_officine_courante());
GRANT SELECT, INSERT, UPDATE, DELETE ON public."reception_ligne" TO pharma_cloud_api;
INSERT INTO cloud_table_miroir (nom_table, cle, colonnes) VALUES ('reception_ligne', '{id}', '{id,reception_id,lot_id,produit_id,quantite,prix_achat_ht,remise_pct,prix_net_ht,ppa,shp,taux_tva,ug,ug_cout,prix_catalogue_remplace,officine_id}')
    ON CONFLICT (nom_table) DO UPDATE SET cle = EXCLUDED.cle, colonnes = EXCLUDED.colonnes;

CREATE TABLE IF NOT EXISTS public."registre_psychotrope" (
    officine_id uuid NOT NULL,
    "id" bigint NOT NULL,
    sync_epoque integer NOT NULL,
    sync_numero bigint NOT NULL,
    sync_recu_le timestamp with time zone DEFAULT now() NOT NULL,
    PRIMARY KEY (officine_id, "id")
);
ALTER TABLE public."registre_psychotrope" ADD COLUMN IF NOT EXISTS "lot_id" bigint;
ALTER TABLE public."registre_psychotrope" ADD COLUMN IF NOT EXISTS "ligne_vente_id" bigint;
ALTER TABLE public."registre_psychotrope" ADD COLUMN IF NOT EXISTS "type_mouvement" text;
ALTER TABLE public."registre_psychotrope" ADD COLUMN IF NOT EXISTS "quantite" integer;
ALTER TABLE public."registre_psychotrope" ADD COLUMN IF NOT EXISTS "solde_apres" integer;
ALTER TABLE public."registre_psychotrope" ADD COLUMN IF NOT EXISTS "patient_id" text;
ALTER TABLE public."registre_psychotrope" ADD COLUMN IF NOT EXISTS "medecin_id" bigint;
ALTER TABLE public."registre_psychotrope" ADD COLUMN IF NOT EXISTS "ordonnance_id" bigint;
ALTER TABLE public."registre_psychotrope" ADD COLUMN IF NOT EXISTS "utilisateur_id" bigint;
ALTER TABLE public."registre_psychotrope" ADD COLUMN IF NOT EXISTS "cree_le" timestamp with time zone;
ALTER TABLE public."registre_psychotrope" ADD COLUMN IF NOT EXISTS "mouvement_id" bigint;
ALTER TABLE public."registre_psychotrope" ADD COLUMN IF NOT EXISTS "reference" text;
ALTER TABLE public."registre_psychotrope" ADD COLUMN IF NOT EXISTS "numero_ordre" bigint;
ALTER TABLE public."registre_psychotrope" ADD COLUMN IF NOT EXISTS "duree_traitement_jours" integer;
ALTER TABLE public."registre_psychotrope" ADD COLUMN IF NOT EXISTS "porteur_code" text;
ALTER TABLE public."registre_psychotrope" ADD COLUMN IF NOT EXISTS sync_chargement integer;
ALTER TABLE public."registre_psychotrope" ADD COLUMN IF NOT EXISTS sync_absente_le timestamp with time zone;
ALTER TABLE public."registre_psychotrope" ENABLE ROW LEVEL SECURITY;
ALTER TABLE public."registre_psychotrope" FORCE ROW LEVEL SECURITY;
DROP POLICY IF EXISTS cloisonnement ON public."registre_psychotrope";
CREATE POLICY cloisonnement ON public."registre_psychotrope" USING (officine_id = cloud_officine_courante()) WITH CHECK (officine_id = cloud_officine_courante());
GRANT SELECT, INSERT, UPDATE, DELETE ON public."registre_psychotrope" TO pharma_cloud_api;
INSERT INTO cloud_table_miroir (nom_table, cle, colonnes) VALUES ('registre_psychotrope', '{id}', '{id,lot_id,ligne_vente_id,type_mouvement,quantite,solde_apres,patient_id,medecin_id,ordonnance_id,utilisateur_id,cree_le,mouvement_id,reference,numero_ordre,duree_traitement_jours,officine_id,porteur_code}')
    ON CONFLICT (nom_table) DO UPDATE SET cle = EXCLUDED.cle, colonnes = EXCLUDED.colonnes;

CREATE TABLE IF NOT EXISTS public."reglage_mutualise" (
    officine_id uuid NOT NULL,
    "nom_table" text NOT NULL,
    sync_epoque integer NOT NULL,
    sync_numero bigint NOT NULL,
    sync_recu_le timestamp with time zone DEFAULT now() NOT NULL,
    PRIMARY KEY (officine_id, "nom_table")
);
ALTER TABLE public."reglage_mutualise" ADD COLUMN IF NOT EXISTS "mode" text;
ALTER TABLE public."reglage_mutualise" ADD COLUMN IF NOT EXISTS "ajouts_locaux" boolean;
ALTER TABLE public."reglage_mutualise" ADD COLUMN IF NOT EXISTS "mode_recu" text;
ALTER TABLE public."reglage_mutualise" ADD COLUMN IF NOT EXISTS "version_modele_recue" bigint;
ALTER TABLE public."reglage_mutualise" ADD COLUMN IF NOT EXISTS "recu_le" timestamp with time zone;
ALTER TABLE public."reglage_mutualise" ADD COLUMN IF NOT EXISTS "version_modele_appliquee" bigint;
ALTER TABLE public."reglage_mutualise" ADD COLUMN IF NOT EXISTS "applique_le" timestamp with time zone;
ALTER TABLE public."reglage_mutualise" ADD COLUMN IF NOT EXISTS "etat" text;
ALTER TABLE public."reglage_mutualise" ADD COLUMN IF NOT EXISTS "motif_rejet" text;
ALTER TABLE public."reglage_mutualise" ADD COLUMN IF NOT EXISTS "version_modele_rejetee" bigint;
ALTER TABLE public."reglage_mutualise" ADD COLUMN IF NOT EXISTS "origine" text;
ALTER TABLE public."reglage_mutualise" ADD COLUMN IF NOT EXISTS "modifie_localement_le" timestamp with time zone;
ALTER TABLE public."reglage_mutualise" ADD COLUMN IF NOT EXISTS sync_chargement integer;
ALTER TABLE public."reglage_mutualise" ADD COLUMN IF NOT EXISTS sync_absente_le timestamp with time zone;
ALTER TABLE public."reglage_mutualise" ENABLE ROW LEVEL SECURITY;
ALTER TABLE public."reglage_mutualise" FORCE ROW LEVEL SECURITY;
DROP POLICY IF EXISTS cloisonnement ON public."reglage_mutualise";
CREATE POLICY cloisonnement ON public."reglage_mutualise" USING (officine_id = cloud_officine_courante()) WITH CHECK (officine_id = cloud_officine_courante());
GRANT SELECT, INSERT, UPDATE, DELETE ON public."reglage_mutualise" TO pharma_cloud_api;
INSERT INTO cloud_table_miroir (nom_table, cle, colonnes) VALUES ('reglage_mutualise', '{nom_table}', '{nom_table,mode,ajouts_locaux,mode_recu,version_modele_recue,recu_le,version_modele_appliquee,applique_le,etat,motif_rejet,version_modele_rejetee,origine,modifie_localement_le,officine_id}')
    ON CONFLICT (nom_table) DO UPDATE SET cle = EXCLUDED.cle, colonnes = EXCLUDED.colonnes;

CREATE TABLE IF NOT EXISTS public."regle_alerte_psychotrope" (
    officine_id uuid NOT NULL,
    "code" text NOT NULL,
    sync_epoque integer NOT NULL,
    sync_numero bigint NOT NULL,
    sync_recu_le timestamp with time zone DEFAULT now() NOT NULL,
    PRIMARY KEY (officine_id, "code")
);
ALTER TABLE public."regle_alerte_psychotrope" ADD COLUMN IF NOT EXISTS "libelle" text;
ALTER TABLE public."regle_alerte_psychotrope" ADD COLUMN IF NOT EXISTS "active" boolean;
ALTER TABLE public."regle_alerte_psychotrope" ADD COLUMN IF NOT EXISTS "seuil" integer;
ALTER TABLE public."regle_alerte_psychotrope" ADD COLUMN IF NOT EXISTS "periode_jours" integer;
ALTER TABLE public."regle_alerte_psychotrope" ADD COLUMN IF NOT EXISTS "exiger_valideur" boolean;
ALTER TABLE public."regle_alerte_psychotrope" ADD COLUMN IF NOT EXISTS "ordre" smallint;
ALTER TABLE public."regle_alerte_psychotrope" ADD COLUMN IF NOT EXISTS "modifie_le" timestamp with time zone;
ALTER TABLE public."regle_alerte_psychotrope" ADD COLUMN IF NOT EXISTS "modifie_par" bigint;
ALTER TABLE public."regle_alerte_psychotrope" ADD COLUMN IF NOT EXISTS "unites_par_jour" integer;
ALTER TABLE public."regle_alerte_psychotrope" ADD COLUMN IF NOT EXISTS sync_chargement integer;
ALTER TABLE public."regle_alerte_psychotrope" ADD COLUMN IF NOT EXISTS sync_absente_le timestamp with time zone;
ALTER TABLE public."regle_alerte_psychotrope" ENABLE ROW LEVEL SECURITY;
ALTER TABLE public."regle_alerte_psychotrope" FORCE ROW LEVEL SECURITY;
DROP POLICY IF EXISTS cloisonnement ON public."regle_alerte_psychotrope";
CREATE POLICY cloisonnement ON public."regle_alerte_psychotrope" USING (officine_id = cloud_officine_courante()) WITH CHECK (officine_id = cloud_officine_courante());
GRANT SELECT, INSERT, UPDATE, DELETE ON public."regle_alerte_psychotrope" TO pharma_cloud_api;
INSERT INTO cloud_table_miroir (nom_table, cle, colonnes) VALUES ('regle_alerte_psychotrope', '{code}', '{code,libelle,active,seuil,periode_jours,exiger_valideur,ordre,modifie_le,modifie_par,unites_par_jour,officine_id}')
    ON CONFLICT (nom_table) DO UPDATE SET cle = EXCLUDED.cle, colonnes = EXCLUDED.colonnes;

CREATE TABLE IF NOT EXISTS public."reglement_facture" (
    officine_id uuid NOT NULL,
    "reglement_id" bigint NOT NULL,
    "reception_id" bigint NOT NULL,
    sync_epoque integer NOT NULL,
    sync_numero bigint NOT NULL,
    sync_recu_le timestamp with time zone DEFAULT now() NOT NULL,
    PRIMARY KEY (officine_id, "reglement_id", "reception_id")
);
ALTER TABLE public."reglement_facture" ADD COLUMN IF NOT EXISTS "montant" numeric(12,2);
ALTER TABLE public."reglement_facture" ADD COLUMN IF NOT EXISTS sync_chargement integer;
ALTER TABLE public."reglement_facture" ADD COLUMN IF NOT EXISTS sync_absente_le timestamp with time zone;
ALTER TABLE public."reglement_facture" ENABLE ROW LEVEL SECURITY;
ALTER TABLE public."reglement_facture" FORCE ROW LEVEL SECURITY;
DROP POLICY IF EXISTS cloisonnement ON public."reglement_facture";
CREATE POLICY cloisonnement ON public."reglement_facture" USING (officine_id = cloud_officine_courante()) WITH CHECK (officine_id = cloud_officine_courante());
GRANT SELECT, INSERT, UPDATE, DELETE ON public."reglement_facture" TO pharma_cloud_api;
INSERT INTO cloud_table_miroir (nom_table, cle, colonnes) VALUES ('reglement_facture', '{reglement_id,reception_id}', '{reglement_id,reception_id,montant,officine_id}')
    ON CONFLICT (nom_table) DO UPDATE SET cle = EXCLUDED.cle, colonnes = EXCLUDED.colonnes;

CREATE TABLE IF NOT EXISTS public."reglement_facture_client" (
    officine_id uuid NOT NULL,
    "mouvement_id" bigint NOT NULL,
    "facture_id" bigint NOT NULL,
    sync_epoque integer NOT NULL,
    sync_numero bigint NOT NULL,
    sync_recu_le timestamp with time zone DEFAULT now() NOT NULL,
    PRIMARY KEY (officine_id, "mouvement_id", "facture_id")
);
ALTER TABLE public."reglement_facture_client" ADD COLUMN IF NOT EXISTS "montant" numeric(12,2);
ALTER TABLE public."reglement_facture_client" ADD COLUMN IF NOT EXISTS "part_credit" numeric(12,2);
ALTER TABLE public."reglement_facture_client" ADD COLUMN IF NOT EXISTS sync_chargement integer;
ALTER TABLE public."reglement_facture_client" ADD COLUMN IF NOT EXISTS sync_absente_le timestamp with time zone;
ALTER TABLE public."reglement_facture_client" ENABLE ROW LEVEL SECURITY;
ALTER TABLE public."reglement_facture_client" FORCE ROW LEVEL SECURITY;
DROP POLICY IF EXISTS cloisonnement ON public."reglement_facture_client";
CREATE POLICY cloisonnement ON public."reglement_facture_client" USING (officine_id = cloud_officine_courante()) WITH CHECK (officine_id = cloud_officine_courante());
GRANT SELECT, INSERT, UPDATE, DELETE ON public."reglement_facture_client" TO pharma_cloud_api;
INSERT INTO cloud_table_miroir (nom_table, cle, colonnes) VALUES ('reglement_facture_client', '{mouvement_id,facture_id}', '{mouvement_id,facture_id,montant,part_credit,officine_id}')
    ON CONFLICT (nom_table) DO UPDATE SET cle = EXCLUDED.cle, colonnes = EXCLUDED.colonnes;

CREATE TABLE IF NOT EXISTS public."reglement_fournisseur" (
    officine_id uuid NOT NULL,
    "id" bigint NOT NULL,
    sync_epoque integer NOT NULL,
    sync_numero bigint NOT NULL,
    sync_recu_le timestamp with time zone DEFAULT now() NOT NULL,
    PRIMARY KEY (officine_id, "id")
);
ALTER TABLE public."reglement_fournisseur" ADD COLUMN IF NOT EXISTS "fournisseur_id" bigint;
ALTER TABLE public."reglement_fournisseur" ADD COLUMN IF NOT EXISTS "date_reglement" date;
ALTER TABLE public."reglement_fournisseur" ADD COLUMN IF NOT EXISTS "montant" numeric(12,2);
ALTER TABLE public."reglement_fournisseur" ADD COLUMN IF NOT EXISTS "moyen" text;
ALTER TABLE public."reglement_fournisseur" ADD COLUMN IF NOT EXISTS "reference" text;
ALTER TABLE public."reglement_fournisseur" ADD COLUMN IF NOT EXISTS "banque" text;
ALTER TABLE public."reglement_fournisseur" ADD COLUMN IF NOT EXISTS "echeance" date;
ALTER TABLE public."reglement_fournisseur" ADD COLUMN IF NOT EXISTS "part_tiroir" numeric(12,2);
ALTER TABLE public."reglement_fournisseur" ADD COLUMN IF NOT EXISTS "session_caisse_id" bigint;
ALTER TABLE public."reglement_fournisseur" ADD COLUMN IF NOT EXISTS "utilisateur_id" bigint;
ALTER TABLE public."reglement_fournisseur" ADD COLUMN IF NOT EXISTS "commentaire" text;
ALTER TABLE public."reglement_fournisseur" ADD COLUMN IF NOT EXISTS "cree_le" timestamp with time zone;
ALTER TABLE public."reglement_fournisseur" ADD COLUMN IF NOT EXISTS "compte_id" bigint;
ALTER TABLE public."reglement_fournisseur" ADD COLUMN IF NOT EXISTS "numero" text;
ALTER TABLE public."reglement_fournisseur" ADD COLUMN IF NOT EXISTS "statut" text;
ALTER TABLE public."reglement_fournisseur" ADD COLUMN IF NOT EXISTS "annule_le" timestamp with time zone;
ALTER TABLE public."reglement_fournisseur" ADD COLUMN IF NOT EXISTS "annule_par" bigint;
ALTER TABLE public."reglement_fournisseur" ADD COLUMN IF NOT EXISTS "motif_annulation" text;
ALTER TABLE public."reglement_fournisseur" ADD COLUMN IF NOT EXISTS "annulation_valide_par" bigint;
ALTER TABLE public."reglement_fournisseur" ADD COLUMN IF NOT EXISTS sync_chargement integer;
ALTER TABLE public."reglement_fournisseur" ADD COLUMN IF NOT EXISTS sync_absente_le timestamp with time zone;
ALTER TABLE public."reglement_fournisseur" ENABLE ROW LEVEL SECURITY;
ALTER TABLE public."reglement_fournisseur" FORCE ROW LEVEL SECURITY;
DROP POLICY IF EXISTS cloisonnement ON public."reglement_fournisseur";
CREATE POLICY cloisonnement ON public."reglement_fournisseur" USING (officine_id = cloud_officine_courante()) WITH CHECK (officine_id = cloud_officine_courante());
GRANT SELECT, INSERT, UPDATE, DELETE ON public."reglement_fournisseur" TO pharma_cloud_api;
INSERT INTO cloud_table_miroir (nom_table, cle, colonnes) VALUES ('reglement_fournisseur', '{id}', '{id,fournisseur_id,date_reglement,montant,moyen,reference,banque,echeance,part_tiroir,session_caisse_id,utilisateur_id,commentaire,cree_le,compte_id,numero,statut,annule_le,annule_par,motif_annulation,annulation_valide_par,officine_id}')
    ON CONFLICT (nom_table) DO UPDATE SET cle = EXCLUDED.cle, colonnes = EXCLUDED.colonnes;

CREATE TABLE IF NOT EXISTS public."regularisation_part_organisme" (
    officine_id uuid NOT NULL,
    "id" bigint NOT NULL,
    sync_epoque integer NOT NULL,
    sync_numero bigint NOT NULL,
    sync_recu_le timestamp with time zone DEFAULT now() NOT NULL,
    PRIMARY KEY (officine_id, "id")
);
ALTER TABLE public."regularisation_part_organisme" ADD COLUMN IF NOT EXISTS "vente_id" bigint;
ALTER TABLE public."regularisation_part_organisme" ADD COLUMN IF NOT EXISTS "organisme_id" bigint;
ALTER TABLE public."regularisation_part_organisme" ADD COLUMN IF NOT EXISTS "part_organisme_vente" numeric(12,2);
ALTER TABLE public."regularisation_part_organisme" ADD COLUMN IF NOT EXISTS "part_organisme_chifa" numeric(12,2);
ALTER TABLE public."regularisation_part_organisme" ADD COLUMN IF NOT EXISTS "ecart" numeric(12,2);
ALTER TABLE public."regularisation_part_organisme" ADD COLUMN IF NOT EXISTS "sort" text;
ALTER TABLE public."regularisation_part_organisme" ADD COLUMN IF NOT EXISTS "montant_patient" numeric(12,2);
ALTER TABLE public."regularisation_part_organisme" ADD COLUMN IF NOT EXISTS "montant_dette_diminuee" numeric(12,2);
ALTER TABLE public."regularisation_part_organisme" ADD COLUMN IF NOT EXISTS "montant_a_rendre" numeric(12,2);
ALTER TABLE public."regularisation_part_organisme" ADD COLUMN IF NOT EXISTS "montant_pharmacie" numeric(12,2);
ALTER TABLE public."regularisation_part_organisme" ADD COLUMN IF NOT EXISTS "credit_mouvement_id" bigint;
ALTER TABLE public."regularisation_part_organisme" ADD COLUMN IF NOT EXISTS "utilisateur_id" bigint;
ALTER TABLE public."regularisation_part_organisme" ADD COLUMN IF NOT EXISTS "valide_par" bigint;
ALTER TABLE public."regularisation_part_organisme" ADD COLUMN IF NOT EXISTS "cree_le" timestamp with time zone;
ALTER TABLE public."regularisation_part_organisme" ADD COLUMN IF NOT EXISTS "statut" text;
ALTER TABLE public."regularisation_part_organisme" ADD COLUMN IF NOT EXISTS "annulee_le" timestamp with time zone;
ALTER TABLE public."regularisation_part_organisme" ADD COLUMN IF NOT EXISTS "annulee_par" bigint;
ALTER TABLE public."regularisation_part_organisme" ADD COLUMN IF NOT EXISTS "motif_annulation" text;
ALTER TABLE public."regularisation_part_organisme" ADD COLUMN IF NOT EXISTS "annulation_credit_mouvement_id" bigint;
ALTER TABLE public."regularisation_part_organisme" ADD COLUMN IF NOT EXISTS "annulation_dette_annulee" numeric(12,2);
ALTER TABLE public."regularisation_part_organisme" ADD COLUMN IF NOT EXISTS "annulation_trop_paye_impute" numeric(12,2);
ALTER TABLE public."regularisation_part_organisme" ADD COLUMN IF NOT EXISTS "annulation_rembourse" numeric(12,2);
ALTER TABLE public."regularisation_part_organisme" ADD COLUMN IF NOT EXISTS "annulation_mouvement_tiroir_id" bigint;
ALTER TABLE public."regularisation_part_organisme" ADD COLUMN IF NOT EXISTS "part_organisme_rendue_avant" numeric(12,2);
ALTER TABLE public."regularisation_part_organisme" ADD COLUMN IF NOT EXISTS sync_chargement integer;
ALTER TABLE public."regularisation_part_organisme" ADD COLUMN IF NOT EXISTS sync_absente_le timestamp with time zone;
ALTER TABLE public."regularisation_part_organisme" ENABLE ROW LEVEL SECURITY;
ALTER TABLE public."regularisation_part_organisme" FORCE ROW LEVEL SECURITY;
DROP POLICY IF EXISTS cloisonnement ON public."regularisation_part_organisme";
CREATE POLICY cloisonnement ON public."regularisation_part_organisme" USING (officine_id = cloud_officine_courante()) WITH CHECK (officine_id = cloud_officine_courante());
GRANT SELECT, INSERT, UPDATE, DELETE ON public."regularisation_part_organisme" TO pharma_cloud_api;
INSERT INTO cloud_table_miroir (nom_table, cle, colonnes) VALUES ('regularisation_part_organisme', '{id}', '{id,vente_id,organisme_id,part_organisme_vente,part_organisme_chifa,ecart,sort,montant_patient,montant_dette_diminuee,montant_a_rendre,montant_pharmacie,credit_mouvement_id,utilisateur_id,valide_par,cree_le,statut,annulee_le,annulee_par,motif_annulation,annulation_credit_mouvement_id,annulation_dette_annulee,annulation_trop_paye_impute,annulation_rembourse,annulation_mouvement_tiroir_id,officine_id,part_organisme_rendue_avant}')
    ON CONFLICT (nom_table) DO UPDATE SET cle = EXCLUDED.cle, colonnes = EXCLUDED.colonnes;

CREATE TABLE IF NOT EXISTS public."remboursement_titulaire" (
    officine_id uuid NOT NULL,
    "id" bigint NOT NULL,
    sync_epoque integer NOT NULL,
    sync_numero bigint NOT NULL,
    sync_recu_le timestamp with time zone DEFAULT now() NOT NULL,
    PRIMARY KEY (officine_id, "id")
);
ALTER TABLE public."remboursement_titulaire" ADD COLUMN IF NOT EXISTS "date_remboursement" date;
ALTER TABLE public."remboursement_titulaire" ADD COLUMN IF NOT EXISTS "montant" numeric(12,2);
ALTER TABLE public."remboursement_titulaire" ADD COLUMN IF NOT EXISTS "compte_id" bigint;
ALTER TABLE public."remboursement_titulaire" ADD COLUMN IF NOT EXISTS "mouvement_id" bigint;
ALTER TABLE public."remboursement_titulaire" ADD COLUMN IF NOT EXISTS "libelle" text;
ALTER TABLE public."remboursement_titulaire" ADD COLUMN IF NOT EXISTS "reference" text;
ALTER TABLE public."remboursement_titulaire" ADD COLUMN IF NOT EXISTS "utilisateur_id" bigint;
ALTER TABLE public."remboursement_titulaire" ADD COLUMN IF NOT EXISTS "cree_le" timestamp with time zone;
ALTER TABLE public."remboursement_titulaire" ADD COLUMN IF NOT EXISTS sync_chargement integer;
ALTER TABLE public."remboursement_titulaire" ADD COLUMN IF NOT EXISTS sync_absente_le timestamp with time zone;
ALTER TABLE public."remboursement_titulaire" ENABLE ROW LEVEL SECURITY;
ALTER TABLE public."remboursement_titulaire" FORCE ROW LEVEL SECURITY;
DROP POLICY IF EXISTS cloisonnement ON public."remboursement_titulaire";
CREATE POLICY cloisonnement ON public."remboursement_titulaire" USING (officine_id = cloud_officine_courante()) WITH CHECK (officine_id = cloud_officine_courante());
GRANT SELECT, INSERT, UPDATE, DELETE ON public."remboursement_titulaire" TO pharma_cloud_api;
INSERT INTO cloud_table_miroir (nom_table, cle, colonnes) VALUES ('remboursement_titulaire', '{id}', '{id,date_remboursement,montant,compte_id,mouvement_id,libelle,reference,utilisateur_id,cree_le,officine_id}')
    ON CONFLICT (nom_table) DO UPDATE SET cle = EXCLUDED.cle, colonnes = EXCLUDED.colonnes;

CREATE TABLE IF NOT EXISTS public."remise_automatique" (
    officine_id uuid NOT NULL,
    "id" bigint NOT NULL,
    sync_epoque integer NOT NULL,
    sync_numero bigint NOT NULL,
    sync_recu_le timestamp with time zone DEFAULT now() NOT NULL,
    PRIMARY KEY (officine_id, "id")
);
ALTER TABLE public."remise_automatique" ADD COLUMN IF NOT EXISTS "produit_id" bigint;
ALTER TABLE public."remise_automatique" ADD COLUMN IF NOT EXISTS "categorie" text;
ALTER TABLE public."remise_automatique" ADD COLUMN IF NOT EXISTS "pourcentage" numeric(5,2);
ALTER TABLE public."remise_automatique" ADD COLUMN IF NOT EXISTS "date_debut" date;
ALTER TABLE public."remise_automatique" ADD COLUMN IF NOT EXISTS "date_fin" date;
ALTER TABLE public."remise_automatique" ADD COLUMN IF NOT EXISTS "motif" text;
ALTER TABLE public."remise_automatique" ADD COLUMN IF NOT EXISTS "cree_par" bigint;
ALTER TABLE public."remise_automatique" ADD COLUMN IF NOT EXISTS "cree_le" timestamp with time zone;
ALTER TABLE public."remise_automatique" ADD COLUMN IF NOT EXISTS "modifiee_par" bigint;
ALTER TABLE public."remise_automatique" ADD COLUMN IF NOT EXISTS "modifiee_le" timestamp with time zone;
ALTER TABLE public."remise_automatique" ADD COLUMN IF NOT EXISTS "arretee_par" bigint;
ALTER TABLE public."remise_automatique" ADD COLUMN IF NOT EXISTS "arretee_le" timestamp with time zone;
ALTER TABLE public."remise_automatique" ADD COLUMN IF NOT EXISTS "modele_cle" text;
ALTER TABLE public."remise_automatique" ADD COLUMN IF NOT EXISTS sync_chargement integer;
ALTER TABLE public."remise_automatique" ADD COLUMN IF NOT EXISTS sync_absente_le timestamp with time zone;
ALTER TABLE public."remise_automatique" ENABLE ROW LEVEL SECURITY;
ALTER TABLE public."remise_automatique" FORCE ROW LEVEL SECURITY;
DROP POLICY IF EXISTS cloisonnement ON public."remise_automatique";
CREATE POLICY cloisonnement ON public."remise_automatique" USING (officine_id = cloud_officine_courante()) WITH CHECK (officine_id = cloud_officine_courante());
GRANT SELECT, INSERT, UPDATE, DELETE ON public."remise_automatique" TO pharma_cloud_api;
INSERT INTO cloud_table_miroir (nom_table, cle, colonnes) VALUES ('remise_automatique', '{id}', '{id,produit_id,categorie,pourcentage,date_debut,date_fin,motif,cree_par,cree_le,modifiee_par,modifiee_le,arretee_par,arretee_le,officine_id,modele_cle}')
    ON CONFLICT (nom_table) DO UPDATE SET cle = EXCLUDED.cle, colonnes = EXCLUDED.colonnes;

CREATE TABLE IF NOT EXISTS public."remise_cheque_paiement" (
    officine_id uuid NOT NULL,
    "remise_id" bigint NOT NULL,
    "paiement_id" bigint NOT NULL,
    sync_epoque integer NOT NULL,
    sync_numero bigint NOT NULL,
    sync_recu_le timestamp with time zone DEFAULT now() NOT NULL,
    PRIMARY KEY (officine_id, "remise_id", "paiement_id")
);
ALTER TABLE public."remise_cheque_paiement" ADD COLUMN IF NOT EXISTS sync_chargement integer;
ALTER TABLE public."remise_cheque_paiement" ADD COLUMN IF NOT EXISTS sync_absente_le timestamp with time zone;
ALTER TABLE public."remise_cheque_paiement" ENABLE ROW LEVEL SECURITY;
ALTER TABLE public."remise_cheque_paiement" FORCE ROW LEVEL SECURITY;
DROP POLICY IF EXISTS cloisonnement ON public."remise_cheque_paiement";
CREATE POLICY cloisonnement ON public."remise_cheque_paiement" USING (officine_id = cloud_officine_courante()) WITH CHECK (officine_id = cloud_officine_courante());
GRANT SELECT, INSERT, UPDATE, DELETE ON public."remise_cheque_paiement" TO pharma_cloud_api;
INSERT INTO cloud_table_miroir (nom_table, cle, colonnes) VALUES ('remise_cheque_paiement', '{remise_id,paiement_id}', '{remise_id,paiement_id,officine_id}')
    ON CONFLICT (nom_table) DO UPDATE SET cle = EXCLUDED.cle, colonnes = EXCLUDED.colonnes;

CREATE TABLE IF NOT EXISTS public."remise_cheques" (
    officine_id uuid NOT NULL,
    "id" bigint NOT NULL,
    sync_epoque integer NOT NULL,
    sync_numero bigint NOT NULL,
    sync_recu_le timestamp with time zone DEFAULT now() NOT NULL,
    PRIMARY KEY (officine_id, "id")
);
ALTER TABLE public."remise_cheques" ADD COLUMN IF NOT EXISTS "compte_id" bigint;
ALTER TABLE public."remise_cheques" ADD COLUMN IF NOT EXISTS "date_remise" date;
ALTER TABLE public."remise_cheques" ADD COLUMN IF NOT EXISTS "reference" text;
ALTER TABLE public."remise_cheques" ADD COLUMN IF NOT EXISTS "utilisateur_id" bigint;
ALTER TABLE public."remise_cheques" ADD COLUMN IF NOT EXISTS "cree_le" timestamp with time zone;
ALTER TABLE public."remise_cheques" ADD COLUMN IF NOT EXISTS sync_chargement integer;
ALTER TABLE public."remise_cheques" ADD COLUMN IF NOT EXISTS sync_absente_le timestamp with time zone;
ALTER TABLE public."remise_cheques" ENABLE ROW LEVEL SECURITY;
ALTER TABLE public."remise_cheques" FORCE ROW LEVEL SECURITY;
DROP POLICY IF EXISTS cloisonnement ON public."remise_cheques";
CREATE POLICY cloisonnement ON public."remise_cheques" USING (officine_id = cloud_officine_courante()) WITH CHECK (officine_id = cloud_officine_courante());
GRANT SELECT, INSERT, UPDATE, DELETE ON public."remise_cheques" TO pharma_cloud_api;
INSERT INTO cloud_table_miroir (nom_table, cle, colonnes) VALUES ('remise_cheques', '{id}', '{id,compte_id,date_remise,reference,utilisateur_id,cree_le,officine_id}')
    ON CONFLICT (nom_table) DO UPDATE SET cle = EXCLUDED.cle, colonnes = EXCLUDED.colonnes;

CREATE TABLE IF NOT EXISTS public."retour_client" (
    officine_id uuid NOT NULL,
    "id" bigint NOT NULL,
    sync_epoque integer NOT NULL,
    sync_numero bigint NOT NULL,
    sync_recu_le timestamp with time zone DEFAULT now() NOT NULL,
    PRIMARY KEY (officine_id, "id")
);
ALTER TABLE public."retour_client" ADD COLUMN IF NOT EXISTS "vente_id" bigint;
ALTER TABLE public."retour_client" ADD COLUMN IF NOT EXISTS "motif" text;
ALTER TABLE public."retour_client" ADD COLUMN IF NOT EXISTS "utilisateur_id" bigint;
ALTER TABLE public."retour_client" ADD COLUMN IF NOT EXISTS "valide_par" bigint;
ALTER TABLE public."retour_client" ADD COLUMN IF NOT EXISTS "mode" text;
ALTER TABLE public."retour_client" ADD COLUMN IF NOT EXISTS "montant_rembourse" numeric(12,2);
ALTER TABLE public."retour_client" ADD COLUMN IF NOT EXISTS "session_caisse_id" bigint;
ALTER TABLE public."retour_client" ADD COLUMN IF NOT EXISTS "poste" text;
ALTER TABLE public."retour_client" ADD COLUMN IF NOT EXISTS "cree_le" timestamp with time zone;
ALTER TABLE public."retour_client" ADD COLUMN IF NOT EXISTS "motif_code" text;
ALTER TABLE public."retour_client" ADD COLUMN IF NOT EXISTS "motif_texte" text;
ALTER TABLE public."retour_client" ADD COLUMN IF NOT EXISTS "chifa_corrige_le" timestamp with time zone;
ALTER TABLE public."retour_client" ADD COLUMN IF NOT EXISTS "chifa_corrige_par" bigint;
ALTER TABLE public."retour_client" ADD COLUMN IF NOT EXISTS sync_chargement integer;
ALTER TABLE public."retour_client" ADD COLUMN IF NOT EXISTS sync_absente_le timestamp with time zone;
ALTER TABLE public."retour_client" ENABLE ROW LEVEL SECURITY;
ALTER TABLE public."retour_client" FORCE ROW LEVEL SECURITY;
DROP POLICY IF EXISTS cloisonnement ON public."retour_client";
CREATE POLICY cloisonnement ON public."retour_client" USING (officine_id = cloud_officine_courante()) WITH CHECK (officine_id = cloud_officine_courante());
GRANT SELECT, INSERT, UPDATE, DELETE ON public."retour_client" TO pharma_cloud_api;
INSERT INTO cloud_table_miroir (nom_table, cle, colonnes) VALUES ('retour_client', '{id}', '{id,vente_id,motif,utilisateur_id,valide_par,mode,montant_rembourse,session_caisse_id,poste,cree_le,motif_code,motif_texte,officine_id,chifa_corrige_le,chifa_corrige_par}')
    ON CONFLICT (nom_table) DO UPDATE SET cle = EXCLUDED.cle, colonnes = EXCLUDED.colonnes;

CREATE TABLE IF NOT EXISTS public."retour_client_ligne" (
    officine_id uuid NOT NULL,
    "id" bigint NOT NULL,
    sync_epoque integer NOT NULL,
    sync_numero bigint NOT NULL,
    sync_recu_le timestamp with time zone DEFAULT now() NOT NULL,
    PRIMARY KEY (officine_id, "id")
);
ALTER TABLE public."retour_client_ligne" ADD COLUMN IF NOT EXISTS "retour_id" bigint;
ALTER TABLE public."retour_client_ligne" ADD COLUMN IF NOT EXISTS "ligne_vente_id" bigint;
ALTER TABLE public."retour_client_ligne" ADD COLUMN IF NOT EXISTS "quantite" integer;
ALTER TABLE public."retour_client_ligne" ADD COLUMN IF NOT EXISTS "destination" text;
ALTER TABLE public."retour_client_ligne" ADD COLUMN IF NOT EXISTS "montant" numeric(12,2);
ALTER TABLE public."retour_client_ligne" ADD COLUMN IF NOT EXISTS "montant_organisme" numeric(12,2);
ALTER TABLE public."retour_client_ligne" ADD COLUMN IF NOT EXISTS "montant_regularisation" numeric(12,2);
ALTER TABLE public."retour_client_ligne" ADD COLUMN IF NOT EXISTS sync_chargement integer;
ALTER TABLE public."retour_client_ligne" ADD COLUMN IF NOT EXISTS sync_absente_le timestamp with time zone;
ALTER TABLE public."retour_client_ligne" ENABLE ROW LEVEL SECURITY;
ALTER TABLE public."retour_client_ligne" FORCE ROW LEVEL SECURITY;
DROP POLICY IF EXISTS cloisonnement ON public."retour_client_ligne";
CREATE POLICY cloisonnement ON public."retour_client_ligne" USING (officine_id = cloud_officine_courante()) WITH CHECK (officine_id = cloud_officine_courante());
GRANT SELECT, INSERT, UPDATE, DELETE ON public."retour_client_ligne" TO pharma_cloud_api;
INSERT INTO cloud_table_miroir (nom_table, cle, colonnes) VALUES ('retour_client_ligne', '{id}', '{id,retour_id,ligne_vente_id,quantite,destination,montant,officine_id,montant_organisme,montant_regularisation}')
    ON CONFLICT (nom_table) DO UPDATE SET cle = EXCLUDED.cle, colonnes = EXCLUDED.colonnes;

CREATE TABLE IF NOT EXISTS public."retour_fournisseur" (
    officine_id uuid NOT NULL,
    "id" bigint NOT NULL,
    sync_epoque integer NOT NULL,
    sync_numero bigint NOT NULL,
    sync_recu_le timestamp with time zone DEFAULT now() NOT NULL,
    PRIMARY KEY (officine_id, "id")
);
ALTER TABLE public."retour_fournisseur" ADD COLUMN IF NOT EXISTS "fournisseur_id" bigint;
ALTER TABLE public."retour_fournisseur" ADD COLUMN IF NOT EXISTS "numero" text;
ALTER TABLE public."retour_fournisseur" ADD COLUMN IF NOT EXISTS "motif" text;
ALTER TABLE public."retour_fournisseur" ADD COLUMN IF NOT EXISTS "statut" text;
ALTER TABLE public."retour_fournisseur" ADD COLUMN IF NOT EXISTS "montant_avoir" numeric(12,2);
ALTER TABLE public."retour_fournisseur" ADD COLUMN IF NOT EXISTS "cree_le" timestamp with time zone;
ALTER TABLE public."retour_fournisseur" ADD COLUMN IF NOT EXISTS "utilisateur_id" bigint;
ALTER TABLE public."retour_fournisseur" ADD COLUMN IF NOT EXISTS "envoye_le" timestamp with time zone;
ALTER TABLE public."retour_fournisseur" ADD COLUMN IF NOT EXISTS "avoir_recu_le" timestamp with time zone;
ALTER TABLE public."retour_fournisseur" ADD COLUMN IF NOT EXISTS "montant_attendu" numeric(12,2);
ALTER TABLE public."retour_fournisseur" ADD COLUMN IF NOT EXISTS sync_chargement integer;
ALTER TABLE public."retour_fournisseur" ADD COLUMN IF NOT EXISTS sync_absente_le timestamp with time zone;
ALTER TABLE public."retour_fournisseur" ENABLE ROW LEVEL SECURITY;
ALTER TABLE public."retour_fournisseur" FORCE ROW LEVEL SECURITY;
DROP POLICY IF EXISTS cloisonnement ON public."retour_fournisseur";
CREATE POLICY cloisonnement ON public."retour_fournisseur" USING (officine_id = cloud_officine_courante()) WITH CHECK (officine_id = cloud_officine_courante());
GRANT SELECT, INSERT, UPDATE, DELETE ON public."retour_fournisseur" TO pharma_cloud_api;
INSERT INTO cloud_table_miroir (nom_table, cle, colonnes) VALUES ('retour_fournisseur', '{id}', '{id,fournisseur_id,numero,motif,statut,montant_avoir,cree_le,utilisateur_id,envoye_le,avoir_recu_le,montant_attendu,officine_id}')
    ON CONFLICT (nom_table) DO UPDATE SET cle = EXCLUDED.cle, colonnes = EXCLUDED.colonnes;

CREATE TABLE IF NOT EXISTS public."role" (
    officine_id uuid NOT NULL,
    "id" bigint NOT NULL,
    sync_epoque integer NOT NULL,
    sync_numero bigint NOT NULL,
    sync_recu_le timestamp with time zone DEFAULT now() NOT NULL,
    PRIMARY KEY (officine_id, "id")
);
ALTER TABLE public."role" ADD COLUMN IF NOT EXISTS "code" text;
ALTER TABLE public."role" ADD COLUMN IF NOT EXISTS "nom" text;
ALTER TABLE public."role" ADD COLUMN IF NOT EXISTS "systeme" boolean;
ALTER TABLE public."role" ADD COLUMN IF NOT EXISTS "remise_max_pct" numeric(5,2);
ALTER TABLE public."role" ADD COLUMN IF NOT EXISTS sync_chargement integer;
ALTER TABLE public."role" ADD COLUMN IF NOT EXISTS sync_absente_le timestamp with time zone;
ALTER TABLE public."role" ENABLE ROW LEVEL SECURITY;
ALTER TABLE public."role" FORCE ROW LEVEL SECURITY;
DROP POLICY IF EXISTS cloisonnement ON public."role";
CREATE POLICY cloisonnement ON public."role" USING (officine_id = cloud_officine_courante()) WITH CHECK (officine_id = cloud_officine_courante());
GRANT SELECT, INSERT, UPDATE, DELETE ON public."role" TO pharma_cloud_api;
INSERT INTO cloud_table_miroir (nom_table, cle, colonnes) VALUES ('role', '{id}', '{id,code,nom,systeme,remise_max_pct,officine_id}')
    ON CONFLICT (nom_table) DO UPDATE SET cle = EXCLUDED.cle, colonnes = EXCLUDED.colonnes;

CREATE TABLE IF NOT EXISTS public."role_permission" (
    officine_id uuid NOT NULL,
    "role_id" bigint NOT NULL,
    "permission_code" text NOT NULL,
    sync_epoque integer NOT NULL,
    sync_numero bigint NOT NULL,
    sync_recu_le timestamp with time zone DEFAULT now() NOT NULL,
    PRIMARY KEY (officine_id, "role_id", "permission_code")
);
ALTER TABLE public."role_permission" ADD COLUMN IF NOT EXISTS sync_chargement integer;
ALTER TABLE public."role_permission" ADD COLUMN IF NOT EXISTS sync_absente_le timestamp with time zone;
ALTER TABLE public."role_permission" ENABLE ROW LEVEL SECURITY;
ALTER TABLE public."role_permission" FORCE ROW LEVEL SECURITY;
DROP POLICY IF EXISTS cloisonnement ON public."role_permission";
CREATE POLICY cloisonnement ON public."role_permission" USING (officine_id = cloud_officine_courante()) WITH CHECK (officine_id = cloud_officine_courante());
GRANT SELECT, INSERT, UPDATE, DELETE ON public."role_permission" TO pharma_cloud_api;
INSERT INTO cloud_table_miroir (nom_table, cle, colonnes) VALUES ('role_permission', '{role_id,permission_code}', '{role_id,permission_code,officine_id}')
    ON CONFLICT (nom_table) DO UPDATE SET cle = EXCLUDED.cle, colonnes = EXCLUDED.colonnes;

CREATE TABLE IF NOT EXISTS public."rupture_grossiste" (
    officine_id uuid NOT NULL,
    "id" bigint NOT NULL,
    sync_epoque integer NOT NULL,
    sync_numero bigint NOT NULL,
    sync_recu_le timestamp with time zone DEFAULT now() NOT NULL,
    PRIMARY KEY (officine_id, "id")
);
ALTER TABLE public."rupture_grossiste" ADD COLUMN IF NOT EXISTS "fournisseur_id" bigint;
ALTER TABLE public."rupture_grossiste" ADD COLUMN IF NOT EXISTS "produit_id" bigint;
ALTER TABLE public."rupture_grossiste" ADD COLUMN IF NOT EXISTS "debut" timestamp with time zone;
ALTER TABLE public."rupture_grossiste" ADD COLUMN IF NOT EXISTS "import_debut_id" bigint;
ALTER TABLE public."rupture_grossiste" ADD COLUMN IF NOT EXISTS "fin" timestamp with time zone;
ALTER TABLE public."rupture_grossiste" ADD COLUMN IF NOT EXISTS "import_fin_id" bigint;
ALTER TABLE public."rupture_grossiste" ADD COLUMN IF NOT EXISTS sync_chargement integer;
ALTER TABLE public."rupture_grossiste" ADD COLUMN IF NOT EXISTS sync_absente_le timestamp with time zone;
ALTER TABLE public."rupture_grossiste" ENABLE ROW LEVEL SECURITY;
ALTER TABLE public."rupture_grossiste" FORCE ROW LEVEL SECURITY;
DROP POLICY IF EXISTS cloisonnement ON public."rupture_grossiste";
CREATE POLICY cloisonnement ON public."rupture_grossiste" USING (officine_id = cloud_officine_courante()) WITH CHECK (officine_id = cloud_officine_courante());
GRANT SELECT, INSERT, UPDATE, DELETE ON public."rupture_grossiste" TO pharma_cloud_api;
INSERT INTO cloud_table_miroir (nom_table, cle, colonnes) VALUES ('rupture_grossiste', '{id}', '{id,fournisseur_id,produit_id,debut,import_debut_id,fin,import_fin_id,officine_id}')
    ON CONFLICT (nom_table) DO UPDATE SET cle = EXCLUDED.cle, colonnes = EXCLUDED.colonnes;

CREATE TABLE IF NOT EXISTS public."sceau_integrite" (
    officine_id uuid NOT NULL,
    "id" bigint NOT NULL,
    sync_epoque integer NOT NULL,
    sync_numero bigint NOT NULL,
    sync_recu_le timestamp with time zone DEFAULT now() NOT NULL,
    PRIMARY KEY (officine_id, "id")
);
ALTER TABLE public."sceau_integrite" ADD COLUMN IF NOT EXISTS "journal" text;
ALTER TABLE public."sceau_integrite" ADD COLUMN IF NOT EXISTS "cle_chaine" text;
ALTER TABLE public."sceau_integrite" ADD COLUMN IF NOT EXISTS "motif" text;
ALTER TABLE public."sceau_integrite" ADD COLUMN IF NOT EXISTS "periode_du" date;
ALTER TABLE public."sceau_integrite" ADD COLUMN IF NOT EXISTS "periode_au" date;
ALTER TABLE public."sceau_integrite" ADD COLUMN IF NOT EXISTS "scelle_le" timestamp with time zone;
ALTER TABLE public."sceau_integrite" ADD COLUMN IF NOT EXISTS "dernier_rang" bigint;
ALTER TABLE public."sceau_integrite" ADD COLUMN IF NOT EXISTS "dernier_id" bigint;
ALTER TABLE public."sceau_integrite" ADD COLUMN IF NOT EXISTS "dernier_hash" bytea;
ALTER TABLE public."sceau_integrite" ADD COLUMN IF NOT EXISTS "algorithme" text;
ALTER TABLE public."sceau_integrite" ADD COLUMN IF NOT EXISTS "empreinte_cle" text;
ALTER TABLE public."sceau_integrite" ADD COLUMN IF NOT EXISTS "sceau" bytea;
ALTER TABLE public."sceau_integrite" ADD COLUMN IF NOT EXISTS "auteur_id" bigint;
ALTER TABLE public."sceau_integrite" ADD COLUMN IF NOT EXISTS sync_chargement integer;
ALTER TABLE public."sceau_integrite" ADD COLUMN IF NOT EXISTS sync_absente_le timestamp with time zone;
ALTER TABLE public."sceau_integrite" ENABLE ROW LEVEL SECURITY;
ALTER TABLE public."sceau_integrite" FORCE ROW LEVEL SECURITY;
DROP POLICY IF EXISTS cloisonnement ON public."sceau_integrite";
CREATE POLICY cloisonnement ON public."sceau_integrite" USING (officine_id = cloud_officine_courante()) WITH CHECK (officine_id = cloud_officine_courante());
GRANT SELECT, INSERT, UPDATE, DELETE ON public."sceau_integrite" TO pharma_cloud_api;
INSERT INTO cloud_table_miroir (nom_table, cle, colonnes) VALUES ('sceau_integrite', '{id}', '{id,journal,cle_chaine,motif,periode_du,periode_au,scelle_le,dernier_rang,dernier_id,dernier_hash,algorithme,empreinte_cle,sceau,auteur_id,officine_id}')
    ON CONFLICT (nom_table) DO UPDATE SET cle = EXCLUDED.cle, colonnes = EXCLUDED.colonnes;

CREATE TABLE IF NOT EXISTS public."session_caisse" (
    officine_id uuid NOT NULL,
    "id" bigint NOT NULL,
    sync_epoque integer NOT NULL,
    sync_numero bigint NOT NULL,
    sync_recu_le timestamp with time zone DEFAULT now() NOT NULL,
    PRIMARY KEY (officine_id, "id")
);
ALTER TABLE public."session_caisse" ADD COLUMN IF NOT EXISTS "utilisateur_id" bigint;
ALTER TABLE public."session_caisse" ADD COLUMN IF NOT EXISTS "poste" text;
ALTER TABLE public."session_caisse" ADD COLUMN IF NOT EXISTS "ouverte_le" timestamp with time zone;
ALTER TABLE public."session_caisse" ADD COLUMN IF NOT EXISTS "fond_caisse" numeric(12,2);
ALTER TABLE public."session_caisse" ADD COLUMN IF NOT EXISTS "fermee_le" timestamp with time zone;
ALTER TABLE public."session_caisse" ADD COLUMN IF NOT EXISTS "ventes_especes" numeric(12,2);
ALTER TABLE public."session_caisse" ADD COLUMN IF NOT EXISTS "montant_attendu" numeric(12,2);
ALTER TABLE public."session_caisse" ADD COLUMN IF NOT EXISTS "montant_compte" numeric(12,2);
ALTER TABLE public."session_caisse" ADD COLUMN IF NOT EXISTS "ecart" numeric(12,2);
ALTER TABLE public."session_caisse" ADD COLUMN IF NOT EXISTS "releve_de" bigint;
ALTER TABLE public."session_caisse" ADD COLUMN IF NOT EXISTS sync_chargement integer;
ALTER TABLE public."session_caisse" ADD COLUMN IF NOT EXISTS sync_absente_le timestamp with time zone;
ALTER TABLE public."session_caisse" ENABLE ROW LEVEL SECURITY;
ALTER TABLE public."session_caisse" FORCE ROW LEVEL SECURITY;
DROP POLICY IF EXISTS cloisonnement ON public."session_caisse";
CREATE POLICY cloisonnement ON public."session_caisse" USING (officine_id = cloud_officine_courante()) WITH CHECK (officine_id = cloud_officine_courante());
GRANT SELECT, INSERT, UPDATE, DELETE ON public."session_caisse" TO pharma_cloud_api;
INSERT INTO cloud_table_miroir (nom_table, cle, colonnes) VALUES ('session_caisse', '{id}', '{id,utilisateur_id,poste,ouverte_le,fond_caisse,fermee_le,ventes_especes,montant_attendu,montant_compte,ecart,releve_de,officine_id}')
    ON CONFLICT (nom_table) DO UPDATE SET cle = EXCLUDED.cle, colonnes = EXCLUDED.colonnes;

CREATE TABLE IF NOT EXISTS public."substitution_liste_blanche" (
    officine_id uuid NOT NULL,
    "produit_id" bigint NOT NULL,
    "remplacant_id" bigint NOT NULL,
    sync_epoque integer NOT NULL,
    sync_numero bigint NOT NULL,
    sync_recu_le timestamp with time zone DEFAULT now() NOT NULL,
    PRIMARY KEY (officine_id, "produit_id", "remplacant_id")
);
ALTER TABLE public."substitution_liste_blanche" ADD COLUMN IF NOT EXISTS "cree_le" timestamp with time zone;
ALTER TABLE public."substitution_liste_blanche" ADD COLUMN IF NOT EXISTS "cree_par" bigint;
ALTER TABLE public."substitution_liste_blanche" ADD COLUMN IF NOT EXISTS sync_chargement integer;
ALTER TABLE public."substitution_liste_blanche" ADD COLUMN IF NOT EXISTS sync_absente_le timestamp with time zone;
ALTER TABLE public."substitution_liste_blanche" ENABLE ROW LEVEL SECURITY;
ALTER TABLE public."substitution_liste_blanche" FORCE ROW LEVEL SECURITY;
DROP POLICY IF EXISTS cloisonnement ON public."substitution_liste_blanche";
CREATE POLICY cloisonnement ON public."substitution_liste_blanche" USING (officine_id = cloud_officine_courante()) WITH CHECK (officine_id = cloud_officine_courante());
GRANT SELECT, INSERT, UPDATE, DELETE ON public."substitution_liste_blanche" TO pharma_cloud_api;
INSERT INTO cloud_table_miroir (nom_table, cle, colonnes) VALUES ('substitution_liste_blanche', '{produit_id,remplacant_id}', '{produit_id,remplacant_id,cree_le,cree_par,officine_id}')
    ON CONFLICT (nom_table) DO UPDATE SET cle = EXCLUDED.cle, colonnes = EXCLUDED.colonnes;

CREATE TABLE IF NOT EXISTS public."sync_ordre_resultat" (
    officine_id uuid NOT NULL,
    "ordre_id" uuid NOT NULL,
    sync_epoque integer NOT NULL,
    sync_numero bigint NOT NULL,
    sync_recu_le timestamp with time zone DEFAULT now() NOT NULL,
    PRIMARY KEY (officine_id, "ordre_id")
);
ALTER TABLE public."sync_ordre_resultat" ADD COLUMN IF NOT EXISTS "statut" text;
ALTER TABLE public."sync_ordre_resultat" ADD COLUMN IF NOT EXISTS "motif" text;
ALTER TABLE public."sync_ordre_resultat" ADD COLUMN IF NOT EXISTS "traite_le" timestamp with time zone;
ALTER TABLE public."sync_ordre_resultat" ADD COLUMN IF NOT EXISTS sync_chargement integer;
ALTER TABLE public."sync_ordre_resultat" ADD COLUMN IF NOT EXISTS sync_absente_le timestamp with time zone;
ALTER TABLE public."sync_ordre_resultat" ENABLE ROW LEVEL SECURITY;
ALTER TABLE public."sync_ordre_resultat" FORCE ROW LEVEL SECURITY;
DROP POLICY IF EXISTS cloisonnement ON public."sync_ordre_resultat";
CREATE POLICY cloisonnement ON public."sync_ordre_resultat" USING (officine_id = cloud_officine_courante()) WITH CHECK (officine_id = cloud_officine_courante());
GRANT SELECT, INSERT, UPDATE, DELETE ON public."sync_ordre_resultat" TO pharma_cloud_api;
INSERT INTO cloud_table_miroir (nom_table, cle, colonnes) VALUES ('sync_ordre_resultat', '{ordre_id}', '{ordre_id,statut,motif,traite_le,officine_id}')
    ON CONFLICT (nom_table) DO UPDATE SET cle = EXCLUDED.cle, colonnes = EXCLUDED.colonnes;

CREATE TABLE IF NOT EXISTS public."utilisateur" (
    officine_id uuid NOT NULL,
    "id" bigint NOT NULL,
    sync_epoque integer NOT NULL,
    sync_numero bigint NOT NULL,
    sync_recu_le timestamp with time zone DEFAULT now() NOT NULL,
    PRIMARY KEY (officine_id, "id")
);
ALTER TABLE public."utilisateur" ADD COLUMN IF NOT EXISTS "nom_complet" text;
ALTER TABLE public."utilisateur" ADD COLUMN IF NOT EXISTS "identifiant" text;
ALTER TABLE public."utilisateur" ADD COLUMN IF NOT EXISTS "actif" boolean;
ALTER TABLE public."utilisateur" ADD COLUMN IF NOT EXISTS "cree_le" timestamp with time zone;
ALTER TABLE public."utilisateur" ADD COLUMN IF NOT EXISTS "role_id" bigint;
ALTER TABLE public."utilisateur" ADD COLUMN IF NOT EXISTS "derniere_connexion" timestamp with time zone;
ALTER TABLE public."utilisateur" ADD COLUMN IF NOT EXISTS "affichage" text;
ALTER TABLE public."utilisateur" ADD COLUMN IF NOT EXISTS "doit_changer_mot_de_passe" boolean;
ALTER TABLE public."utilisateur" ADD COLUMN IF NOT EXISTS "peut_donner" boolean;
ALTER TABLE public."utilisateur" ADD COLUMN IF NOT EXISTS "remise_max_pct" numeric(5,2);
ALTER TABLE public."utilisateur" ADD COLUMN IF NOT EXISTS "gere_remises_auto" boolean;
ALTER TABLE public."utilisateur" ADD COLUMN IF NOT EXISTS sync_chargement integer;
ALTER TABLE public."utilisateur" ADD COLUMN IF NOT EXISTS sync_absente_le timestamp with time zone;
ALTER TABLE public."utilisateur" ENABLE ROW LEVEL SECURITY;
ALTER TABLE public."utilisateur" FORCE ROW LEVEL SECURITY;
DROP POLICY IF EXISTS cloisonnement ON public."utilisateur";
CREATE POLICY cloisonnement ON public."utilisateur" USING (officine_id = cloud_officine_courante()) WITH CHECK (officine_id = cloud_officine_courante());
GRANT SELECT, INSERT, UPDATE, DELETE ON public."utilisateur" TO pharma_cloud_api;
INSERT INTO cloud_table_miroir (nom_table, cle, colonnes) VALUES ('utilisateur', '{id}', '{id,nom_complet,identifiant,actif,cree_le,role_id,derniere_connexion,affichage,doit_changer_mot_de_passe,peut_donner,remise_max_pct,gere_remises_auto,officine_id}')
    ON CONFLICT (nom_table) DO UPDATE SET cle = EXCLUDED.cle, colonnes = EXCLUDED.colonnes;

CREATE TABLE IF NOT EXISTS public."utilisation_financement" (
    officine_id uuid NOT NULL,
    "id" bigint NOT NULL,
    sync_epoque integer NOT NULL,
    sync_numero bigint NOT NULL,
    sync_recu_le timestamp with time zone DEFAULT now() NOT NULL,
    PRIMARY KEY (officine_id, "id")
);
ALTER TABLE public."utilisation_financement" ADD COLUMN IF NOT EXISTS "source" text;
ALTER TABLE public."utilisation_financement" ADD COLUMN IF NOT EXISTS "pret_id" bigint;
ALTER TABLE public."utilisation_financement" ADD COLUMN IF NOT EXISTS "usage" text;
ALTER TABLE public."utilisation_financement" ADD COLUMN IF NOT EXISTS "date_usage" date;
ALTER TABLE public."utilisation_financement" ADD COLUMN IF NOT EXISTS "montant" numeric(12,2);
ALTER TABLE public."utilisation_financement" ADD COLUMN IF NOT EXISTS "libelle" text;
ALTER TABLE public."utilisation_financement" ADD COLUMN IF NOT EXISTS "mouvement_id" bigint;
ALTER TABLE public."utilisation_financement" ADD COLUMN IF NOT EXISTS "depense_id" bigint;
ALTER TABLE public."utilisation_financement" ADD COLUMN IF NOT EXISTS "reglement_id" bigint;
ALTER TABLE public."utilisation_financement" ADD COLUMN IF NOT EXISTS "paiement_effet_id" bigint;
ALTER TABLE public."utilisation_financement" ADD COLUMN IF NOT EXISTS "utilisateur_id" bigint;
ALTER TABLE public."utilisation_financement" ADD COLUMN IF NOT EXISTS "cree_le" timestamp with time zone;
ALTER TABLE public."utilisation_financement" ADD COLUMN IF NOT EXISTS sync_chargement integer;
ALTER TABLE public."utilisation_financement" ADD COLUMN IF NOT EXISTS sync_absente_le timestamp with time zone;
ALTER TABLE public."utilisation_financement" ENABLE ROW LEVEL SECURITY;
ALTER TABLE public."utilisation_financement" FORCE ROW LEVEL SECURITY;
DROP POLICY IF EXISTS cloisonnement ON public."utilisation_financement";
CREATE POLICY cloisonnement ON public."utilisation_financement" USING (officine_id = cloud_officine_courante()) WITH CHECK (officine_id = cloud_officine_courante());
GRANT SELECT, INSERT, UPDATE, DELETE ON public."utilisation_financement" TO pharma_cloud_api;
INSERT INTO cloud_table_miroir (nom_table, cle, colonnes) VALUES ('utilisation_financement', '{id}', '{id,source,pret_id,usage,date_usage,montant,libelle,mouvement_id,depense_id,reglement_id,paiement_effet_id,utilisateur_id,cree_le,officine_id}')
    ON CONFLICT (nom_table) DO UPDATE SET cle = EXCLUDED.cle, colonnes = EXCLUDED.colonnes;

CREATE TABLE IF NOT EXISTS public."vente" (
    officine_id uuid NOT NULL,
    "id" bigint NOT NULL,
    sync_epoque integer NOT NULL,
    sync_numero bigint NOT NULL,
    sync_recu_le timestamp with time zone DEFAULT now() NOT NULL,
    PRIMARY KEY (officine_id, "id")
);
ALTER TABLE public."vente" ADD COLUMN IF NOT EXISTS "numero" text;
ALTER TABLE public."vente" ADD COLUMN IF NOT EXISTS "date_vente" timestamp with time zone;
ALTER TABLE public."vente" ADD COLUMN IF NOT EXISTS "utilisateur_id" bigint;
ALTER TABLE public."vente" ADD COLUMN IF NOT EXISTS "patient_id" text;
ALTER TABLE public."vente" ADD COLUMN IF NOT EXISTS "ordonnance_id" bigint;
ALTER TABLE public."vente" ADD COLUMN IF NOT EXISTS "type" text;
ALTER TABLE public."vente" ADD COLUMN IF NOT EXISTS "organisme_id" bigint;
ALTER TABLE public."vente" ADD COLUMN IF NOT EXISTS "bordereau_id" bigint;
ALTER TABLE public."vente" ADD COLUMN IF NOT EXISTS "montant_total" numeric(12,2);
ALTER TABLE public."vente" ADD COLUMN IF NOT EXISTS "part_organisme" numeric(12,2);
ALTER TABLE public."vente" ADD COLUMN IF NOT EXISTS "part_patient" numeric(12,2);
ALTER TABLE public."vente" ADD COLUMN IF NOT EXISTS "statut" text;
ALTER TABLE public."vente" ADD COLUMN IF NOT EXISTS "num_facture_chifa" text;
ALTER TABLE public."vente" ADD COLUMN IF NOT EXISTS "session_caisse_id" bigint;
ALTER TABLE public."vente" ADD COLUMN IF NOT EXISTS "remise_pct" numeric(5,2);
ALTER TABLE public."vente" ADD COLUMN IF NOT EXISTS "montant_remise" numeric(12,2);
ALTER TABLE public."vente" ADD COLUMN IF NOT EXISTS "taux_tiers_payant" numeric(5,2);
ALTER TABLE public."vente" ADD COLUMN IF NOT EXISTS "reste_saisi" boolean;
ALTER TABLE public."vente" ADD COLUMN IF NOT EXISTS "chifa_facture" text;
ALTER TABLE public."vente" ADD COLUMN IF NOT EXISTS "chifa_date" date;
ALTER TABLE public."vente" ADD COLUMN IF NOT EXISTS "remise_additionnee" boolean;
ALTER TABLE public."vente" ADD COLUMN IF NOT EXISTS "montant_don" numeric(12,2);
ALTER TABLE public."vente" ADD COLUMN IF NOT EXISTS "cle_encaissement" uuid;
ALTER TABLE public."vente" ADD COLUMN IF NOT EXISTS "canal_organisme" text;
ALTER TABLE public."vente" ADD COLUMN IF NOT EXISTS "remis_a_patient_id" text;
ALTER TABLE public."vente" ADD COLUMN IF NOT EXISTS "pour_patient_id" text;
ALTER TABLE public."vente" ADD COLUMN IF NOT EXISTS "chifa_date_ordonnance" date;
ALTER TABLE public."vente" ADD COLUMN IF NOT EXISTS "chifa_rattache_le" timestamp with time zone;
ALTER TABLE public."vente" ADD COLUMN IF NOT EXISTS "chifa_rattache_par" bigint;
ALTER TABLE public."vente" ADD COLUMN IF NOT EXISTS "chifa_ecart_motif" text;
ALTER TABLE public."vente" ADD COLUMN IF NOT EXISTS "chifa_ecart_detail" text;
ALTER TABLE public."vente" ADD COLUMN IF NOT EXISTS "chifa_ecart_part_organisme" numeric(12,2);
ALTER TABLE public."vente" ADD COLUMN IF NOT EXISTS "chifa_attente_motif_code" text;
ALTER TABLE public."vente" ADD COLUMN IF NOT EXISTS "chifa_attente_motif_texte" text;
ALTER TABLE public."vente" ADD COLUMN IF NOT EXISTS "centre_payeur_id" bigint;
ALTER TABLE public."vente" ADD COLUMN IF NOT EXISTS "chifa_date_soins" date;
ALTER TABLE public."vente" ADD COLUMN IF NOT EXISTS "chifa_saisi_par" bigint;
ALTER TABLE public."vente" ADD COLUMN IF NOT EXISTS "chifa_majoration_code" text;
ALTER TABLE public."vente" ADD COLUMN IF NOT EXISTS "chifa_majoration_montant" numeric(12,2);
ALTER TABLE public."vente" ADD COLUMN IF NOT EXISTS "chifa_exclue_le" timestamp with time zone;
ALTER TABLE public."vente" ADD COLUMN IF NOT EXISTS "chifa_exclue_par" bigint;
ALTER TABLE public."vente" ADD COLUMN IF NOT EXISTS "chifa_exclusion_motif_code" text;
ALTER TABLE public."vente" ADD COLUMN IF NOT EXISTS "chifa_exclusion_texte" text;
ALTER TABLE public."vente" ADD COLUMN IF NOT EXISTS sync_chargement integer;
ALTER TABLE public."vente" ADD COLUMN IF NOT EXISTS sync_absente_le timestamp with time zone;
ALTER TABLE public."vente" ENABLE ROW LEVEL SECURITY;
ALTER TABLE public."vente" FORCE ROW LEVEL SECURITY;
DROP POLICY IF EXISTS cloisonnement ON public."vente";
CREATE POLICY cloisonnement ON public."vente" USING (officine_id = cloud_officine_courante()) WITH CHECK (officine_id = cloud_officine_courante());
GRANT SELECT, INSERT, UPDATE, DELETE ON public."vente" TO pharma_cloud_api;
INSERT INTO cloud_table_miroir (nom_table, cle, colonnes) VALUES ('vente', '{id}', '{id,numero,date_vente,utilisateur_id,patient_id,ordonnance_id,type,organisme_id,bordereau_id,montant_total,part_organisme,part_patient,statut,num_facture_chifa,session_caisse_id,remise_pct,montant_remise,taux_tiers_payant,reste_saisi,chifa_facture,chifa_date,remise_additionnee,montant_don,cle_encaissement,canal_organisme,remis_a_patient_id,pour_patient_id,chifa_date_ordonnance,chifa_rattache_le,chifa_rattache_par,chifa_ecart_motif,chifa_ecart_detail,chifa_ecart_part_organisme,chifa_attente_motif_code,chifa_attente_motif_texte,centre_payeur_id,chifa_date_soins,chifa_saisi_par,chifa_majoration_code,chifa_majoration_montant,chifa_exclue_le,chifa_exclue_par,chifa_exclusion_motif_code,chifa_exclusion_texte,officine_id}')
    ON CONFLICT (nom_table) DO UPDATE SET cle = EXCLUDED.cle, colonnes = EXCLUDED.colonnes;

CREATE TABLE IF NOT EXISTS public."vente_chifa_facture_conflit" (
    officine_id uuid NOT NULL,
    "vente_id" bigint NOT NULL,
    sync_epoque integer NOT NULL,
    sync_numero bigint NOT NULL,
    sync_recu_le timestamp with time zone DEFAULT now() NOT NULL,
    PRIMARY KEY (officine_id, "vente_id")
);
ALTER TABLE public."vente_chifa_facture_conflit" ADD COLUMN IF NOT EXISTS "num_facture_chifa" text;
ALTER TABLE public."vente_chifa_facture_conflit" ADD COLUMN IF NOT EXISTS "chifa_facture" text;
ALTER TABLE public."vente_chifa_facture_conflit" ADD COLUMN IF NOT EXISTS "constate_le" timestamp with time zone;
ALTER TABLE public."vente_chifa_facture_conflit" ADD COLUMN IF NOT EXISTS "tranche_le" timestamp with time zone;
ALTER TABLE public."vente_chifa_facture_conflit" ADD COLUMN IF NOT EXISTS "tranche_par" bigint;
ALTER TABLE public."vente_chifa_facture_conflit" ADD COLUMN IF NOT EXISTS "valeur_retenue" text;
ALTER TABLE public."vente_chifa_facture_conflit" ADD COLUMN IF NOT EXISTS sync_chargement integer;
ALTER TABLE public."vente_chifa_facture_conflit" ADD COLUMN IF NOT EXISTS sync_absente_le timestamp with time zone;
ALTER TABLE public."vente_chifa_facture_conflit" ENABLE ROW LEVEL SECURITY;
ALTER TABLE public."vente_chifa_facture_conflit" FORCE ROW LEVEL SECURITY;
DROP POLICY IF EXISTS cloisonnement ON public."vente_chifa_facture_conflit";
CREATE POLICY cloisonnement ON public."vente_chifa_facture_conflit" USING (officine_id = cloud_officine_courante()) WITH CHECK (officine_id = cloud_officine_courante());
GRANT SELECT, INSERT, UPDATE, DELETE ON public."vente_chifa_facture_conflit" TO pharma_cloud_api;
INSERT INTO cloud_table_miroir (nom_table, cle, colonnes) VALUES ('vente_chifa_facture_conflit', '{vente_id}', '{vente_id,num_facture_chifa,chifa_facture,constate_le,tranche_le,tranche_par,valeur_retenue,officine_id}')
    ON CONFLICT (nom_table) DO UPDATE SET cle = EXCLUDED.cle, colonnes = EXCLUDED.colonnes;

CREATE TABLE IF NOT EXISTS public."verification_integrite" (
    officine_id uuid NOT NULL,
    "id" bigint NOT NULL,
    sync_epoque integer NOT NULL,
    sync_numero bigint NOT NULL,
    sync_recu_le timestamp with time zone DEFAULT now() NOT NULL,
    PRIMARY KEY (officine_id, "id")
);
ALTER TABLE public."verification_integrite" ADD COLUMN IF NOT EXISTS "verifie_le" timestamp with time zone;
ALTER TABLE public."verification_integrite" ADD COLUMN IF NOT EXISTS "journal" text;
ALTER TABLE public."verification_integrite" ADD COLUMN IF NOT EXISTS "cle_chaine" text;
ALTER TABLE public."verification_integrite" ADD COLUMN IF NOT EXISTS "lignes" bigint;
ALTER TABLE public."verification_integrite" ADD COLUMN IF NOT EXISTS "sceaux" integer;
ALTER TABLE public."verification_integrite" ADD COLUMN IF NOT EXISTS "statut" text;
ALTER TABLE public."verification_integrite" ADD COLUMN IF NOT EXISTS "rang_rupture" bigint;
ALTER TABLE public."verification_integrite" ADD COLUMN IF NOT EXISTS "motif" text;
ALTER TABLE public."verification_integrite" ADD COLUMN IF NOT EXISTS "attendu" text;
ALTER TABLE public."verification_integrite" ADD COLUMN IF NOT EXISTS "trouve" text;
ALTER TABLE public."verification_integrite" ADD COLUMN IF NOT EXISTS "declenchee_par" bigint;
ALTER TABLE public."verification_integrite" ADD COLUMN IF NOT EXISTS "dernier_rang" bigint;
ALTER TABLE public."verification_integrite" ADD COLUMN IF NOT EXISTS "dernier_hash" bytea;
ALTER TABLE public."verification_integrite" ADD COLUMN IF NOT EXISTS sync_chargement integer;
ALTER TABLE public."verification_integrite" ADD COLUMN IF NOT EXISTS sync_absente_le timestamp with time zone;
ALTER TABLE public."verification_integrite" ENABLE ROW LEVEL SECURITY;
ALTER TABLE public."verification_integrite" FORCE ROW LEVEL SECURITY;
DROP POLICY IF EXISTS cloisonnement ON public."verification_integrite";
CREATE POLICY cloisonnement ON public."verification_integrite" USING (officine_id = cloud_officine_courante()) WITH CHECK (officine_id = cloud_officine_courante());
GRANT SELECT, INSERT, UPDATE, DELETE ON public."verification_integrite" TO pharma_cloud_api;
INSERT INTO cloud_table_miroir (nom_table, cle, colonnes) VALUES ('verification_integrite', '{id}', '{id,verifie_le,journal,cle_chaine,lignes,sceaux,statut,rang_rupture,motif,attendu,trouve,declenchee_par,officine_id,dernier_rang,dernier_hash}')
    ON CONFLICT (nom_table) DO UPDATE SET cle = EXCLUDED.cle, colonnes = EXCLUDED.colonnes;

CREATE TABLE IF NOT EXISTS public."verification_serialisation" (
    officine_id uuid NOT NULL,
    "id" bigint NOT NULL,
    sync_epoque integer NOT NULL,
    sync_numero bigint NOT NULL,
    sync_recu_le timestamp with time zone DEFAULT now() NOT NULL,
    PRIMARY KEY (officine_id, "id")
);
ALTER TABLE public."verification_serialisation" ADD COLUMN IF NOT EXISTS "boite_id" bigint;
ALTER TABLE public."verification_serialisation" ADD COLUMN IF NOT EXISTS "operation" text;
ALTER TABLE public."verification_serialisation" ADD COLUMN IF NOT EXISTS "statut" text;
ALTER TABLE public."verification_serialisation" ADD COLUMN IF NOT EXISTS "resultat" text;
ALTER TABLE public."verification_serialisation" ADD COLUMN IF NOT EXISTS "message" text;
ALTER TABLE public."verification_serialisation" ADD COLUMN IF NOT EXISTS "essais" integer;
ALTER TABLE public."verification_serialisation" ADD COLUMN IF NOT EXISTS "alerte_titulaire" boolean;
ALTER TABLE public."verification_serialisation" ADD COLUMN IF NOT EXISTS "cree_le" timestamp with time zone;
ALTER TABLE public."verification_serialisation" ADD COLUMN IF NOT EXISTS "prochain_essai_le" timestamp with time zone;
ALTER TABLE public."verification_serialisation" ADD COLUMN IF NOT EXISTS "dernier_essai_le" timestamp with time zone;
ALTER TABLE public."verification_serialisation" ADD COLUMN IF NOT EXISTS "traite_le" timestamp with time zone;
ALTER TABLE public."verification_serialisation" ADD COLUMN IF NOT EXISTS sync_chargement integer;
ALTER TABLE public."verification_serialisation" ADD COLUMN IF NOT EXISTS sync_absente_le timestamp with time zone;
ALTER TABLE public."verification_serialisation" ENABLE ROW LEVEL SECURITY;
ALTER TABLE public."verification_serialisation" FORCE ROW LEVEL SECURITY;
DROP POLICY IF EXISTS cloisonnement ON public."verification_serialisation";
CREATE POLICY cloisonnement ON public."verification_serialisation" USING (officine_id = cloud_officine_courante()) WITH CHECK (officine_id = cloud_officine_courante());
GRANT SELECT, INSERT, UPDATE, DELETE ON public."verification_serialisation" TO pharma_cloud_api;
INSERT INTO cloud_table_miroir (nom_table, cle, colonnes) VALUES ('verification_serialisation', '{id}', '{id,boite_id,operation,statut,resultat,message,essais,alerte_titulaire,cree_le,prochain_essai_le,dernier_essai_le,traite_le,officine_id}')
    ON CONFLICT (nom_table) DO UPDATE SET cle = EXCLUDED.cle, colonnes = EXCLUDED.colonnes;

CREATE TABLE IF NOT EXISTS public."versement_bordereau" (
    officine_id uuid NOT NULL,
    "id" bigint NOT NULL,
    sync_epoque integer NOT NULL,
    sync_numero bigint NOT NULL,
    sync_recu_le timestamp with time zone DEFAULT now() NOT NULL,
    PRIMARY KEY (officine_id, "id")
);
ALTER TABLE public."versement_bordereau" ADD COLUMN IF NOT EXISTS "mouvement_id" bigint;
ALTER TABLE public."versement_bordereau" ADD COLUMN IF NOT EXISTS "bordereau_id" bigint;
ALTER TABLE public."versement_bordereau" ADD COLUMN IF NOT EXISTS "montant" numeric(12,2);
ALTER TABLE public."versement_bordereau" ADD COLUMN IF NOT EXISTS sync_chargement integer;
ALTER TABLE public."versement_bordereau" ADD COLUMN IF NOT EXISTS sync_absente_le timestamp with time zone;
ALTER TABLE public."versement_bordereau" ENABLE ROW LEVEL SECURITY;
ALTER TABLE public."versement_bordereau" FORCE ROW LEVEL SECURITY;
DROP POLICY IF EXISTS cloisonnement ON public."versement_bordereau";
CREATE POLICY cloisonnement ON public."versement_bordereau" USING (officine_id = cloud_officine_courante()) WITH CHECK (officine_id = cloud_officine_courante());
GRANT SELECT, INSERT, UPDATE, DELETE ON public."versement_bordereau" TO pharma_cloud_api;
INSERT INTO cloud_table_miroir (nom_table, cle, colonnes) VALUES ('versement_bordereau', '{id}', '{id,mouvement_id,bordereau_id,montant,officine_id}')
    ON CONFLICT (nom_table) DO UPDATE SET cle = EXCLUDED.cle, colonnes = EXCLUDED.colonnes;
