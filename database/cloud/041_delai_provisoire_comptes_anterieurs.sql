-- =====================================================================
-- Base centrale, délai de grâce des mots de passe provisoires créés AVANT
-- le contrôle d'expiration du script 040 (revue de code du 2026-11-22).
-- Script de la base CENTRALE, passé par le propriétaire, idempotent ; à
-- passer après 040.
-- Défaut corrigé : le 040 refuse un mot de passe provisoire non utilisé
-- dont coalesce(mot_de_passe_change_le, cree_le) + durée est passé, y
-- compris pour un compte créé AVANT son passage : un compte créé il y a
-- 10 jours, jamais utilisé, était refusé dès la mise en production (et
-- si c'était le seul éditeur, plus personne ne pouvait régénérer depuis
-- les écrans).
-- Règle : la durée de vie d'un mot de passe provisoire part de son
-- dernier changement (remise à zéro) ou de la création du compte, et AU
-- PLUS TÔT du passage de ce script (colonne
-- parametre_envoi_codes.provisoire_controle_depuis, posée UNE fois au
-- premier passage, jamais déplacée par un rejeu). Un compte provisoire
-- antérieur dispose donc d'une durée complète après le déploiement ; un
-- compte créé ou remis à zéro ensuite n'est pas concerné.
-- cloud_compte_provisoire_echeance (040) est REMPLACÉE : même signature,
-- appelants inchangés. Aucune donnée existante n'est modifiée ni
-- supprimée (une colonne ajoutée avec sa valeur par défaut).
-- =====================================================================

SET client_encoding = 'UTF8';

DO $version$ BEGIN
    IF to_regprocedure('public.cloud_compte_provisoire_echeance(timestamptz,timestamptz)') IS NULL THEN
        RAISE EXCEPTION 'Script central 041 : passer d''abord le script 040 (durée de vie des mots de passe provisoires). Rien n''a été modifié.';
    END IF;
END $version$;

-- Date de départ du contrôle : l'heure du PREMIER passage (valeur par défaut évaluée à l'ajout de la colonne ; IF NOT EXISTS : un
-- rejeu ne la déplace pas)
ALTER TABLE parametre_envoi_codes ADD COLUMN IF NOT EXISTS provisoire_controle_depuis timestamptz NOT NULL DEFAULT now();
COMMENT ON COLUMN parametre_envoi_codes.provisoire_controle_depuis IS
    'Script 041 : départ au plus tôt de la durée de vie d''un mot de passe provisoire (premier passage du script) ; délai de grâce des comptes antérieurs au contrôle du 040.';

-- Échéance d'un mot de passe provisoire : dernier changement (remise à zéro) ou, à défaut, création, et au plus tôt le départ du
-- contrôle (041), plus la durée — interne. greatest ignore un départ absent (aucune ligne de réglage).
CREATE OR REPLACE FUNCTION cloud_compte_provisoire_echeance(p_change_le timestamptz, p_cree_le timestamptz) RETURNS timestamptz
LANGUAGE sql STABLE SET search_path = public, pg_temp AS $$
    SELECT greatest(coalesce(p_change_le, p_cree_le), (SELECT p.provisoire_controle_depuis FROM parametre_envoi_codes p WHERE p.id = 1))
           + cloud_duree_provisoire()
$$;
-- Interne, comme au 040 : rien pour PUBLIC (appelée par les fonctions SECURITY DEFINER du propriétaire)
REVOKE ALL ON FUNCTION cloud_compte_provisoire_echeance(timestamptz, timestamptz) FROM PUBLIC;
