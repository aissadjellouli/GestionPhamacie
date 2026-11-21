-- =====================================================================
-- Base centrale, 2026-11-21 (go du titulaire, option B) : rôle de LECTURE
-- technique pharma_cloud_etat, pour la page d'état du central en
-- développement (http://127.0.0.1:5092, docs/poste-de-developpement.md)
-- et, plus tard, les écrans S7. Script de la base CENTRALE, passé par le
-- propriétaire, idempotent ; à passer après 028.
--   1. Rôle pharma_cloud_etat créé SANS connexion (NOLOGIN), jamais
--      propriétaire. Une connexion s'y rattache par GRANT (poste de
--      développement : pharma_cloud_etat_vs, scripts/dev/
--      preparer_central_vs.ps1). En production : rôle présent, aucune
--      connexion créée (décidée au déploiement, docs/mise-en-production.md).
--   2. SELECT par COLONNES seulement, sur 7 tables techniques :
--      officine (sans rien de personnel), certificat (sans le motif de
--      révocation), code_activation (sans empreinte_code : le code n'est
--      jamais gardé en clair), journal_securite, lot_recu (sans contenu),
--      officine_epoque, chargement. Aucun droit d'écriture, aucun droit
--      sur les tables miroir, ligne_remplacee, journaux d'administration.
--   3. Cloisonnement inchangé : lot_recu, officine_epoque et chargement
--      ont la RLS forcée (politique « cloisonnement », pour tous les
--      rôles) ; le rôle pose l'officine courante comme l'API
--      (set_config('app.officine_id', …, true)) et ne voit rien sans elle.
--   4. Fonctions SECURITY DEFINER (hors déclencheurs) du schéma public
--      encore exécutables par PUBLIC (dont cloud_activer, cloud_renouveler,
--      cloud_journaliser, qui écrivent) : EXECUTE retiré à PUBLIC et
--      accordé explicitement à pharma_cloud_api. Droits effectifs de
--      l'API et du propriétaire inchangés ; tout autre rôle (dont
--      pharma_cloud_etat) n'écrit plus par ces fonctions.
-- Aucune donnée n'est lue, modifiée ni supprimée par ce script.
-- =====================================================================

SET client_encoding = 'UTF8';

DO $version$ BEGIN
    IF current_setting('server_version_num')::int < 180000 THEN
        RAISE EXCEPTION 'Script central 029 : PostgreSQL 18 ou plus récent exigé (comme les scripts 020 à 028) ; ce serveur est en version %. Rien n''a été modifié.',
            current_setting('server_version');
    END IF;
    IF to_regclass('public.chargement') IS NULL OR NOT EXISTS (SELECT 1 FROM information_schema.columns
            WHERE table_schema = 'public' AND table_name = 'ligne_remplacee' AND column_name = 'colonnes_effacees') THEN
        RAISE EXCEPTION 'Script central 029 : passer d''abord les scripts 001 à 028 (table chargement ou colonne ligne_remplacee.colonnes_effacees absente). Rien n''a été modifié.';
    END IF;
END $version$;

-- ---- 1. Rôle (sans connexion) ----
DO $$ BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'pharma_cloud_etat') THEN
        CREATE ROLE pharma_cloud_etat NOLOGIN NOINHERIT;
    END IF;
END $$;
-- Le propriétaire des objets peut prendre le rôle de lecture (tests en transaction annulée), comme pour pharma_cloud_api (001)
DO $$ BEGIN
    EXECUTE format('GRANT pharma_cloud_etat TO %I WITH SET TRUE', current_user);
EXCEPTION WHEN others THEN
    RAISE NOTICE 'Droit SET sur pharma_cloud_etat non accordé à % : %', current_user, SQLERRM;
END $$;

-- ---- 2. Lecture par colonnes (REVOKE ALL d'abord : rejeu = mêmes droits, droits de colonnes compris) ----
REVOKE ALL ON officine, certificat, code_activation, journal_securite, lot_recu, officine_epoque, chargement FROM pharma_cloud_etat;
GRANT SELECT (id, libelle, cree_le, resiliee_le, conservation_lots_ans, conservation_depart_ans, mutualisation_liberee_le)
    ON officine TO pharma_cloud_etat;
GRANT SELECT (empreinte, officine_id, serveur, numero_serie, emis_le, echeance, revoque_le, remplace_empreinte)
    ON certificat TO pharma_cloud_etat;
GRANT SELECT (officine_id, serveur, cree_le, expire_le, utilise_le, certificat_empreinte)
    ON code_activation TO pharma_cloud_etat;
GRANT SELECT (id, le, officine_id, certificat_empreinte, evenement, detail)
    ON journal_securite TO pharma_cloud_etat;
GRANT SELECT (officine_id, epoque, numero, empreinte, version_contrat, nb_lignes, octets, recu_le, expurge_le, colonnes_effacees)
    ON lot_recu TO pharma_cloud_etat;
GRANT SELECT (officine_id, epoque, dernier_lot, dernier_recu_le)
    ON officine_epoque TO pharma_cloud_etat;
GRANT SELECT (officine_id, numero, epoque, repere, motif, tables, reprendre_apres, lots_abandonnes, dernier_avant, etat, debut_le, fin_le,
              absentes, octets_recus, lignes_remplacees)
    ON chargement TO pharma_cloud_etat;
-- Politique de cloisonnement (fonction appelée par la RLS)
GRANT EXECUTE ON FUNCTION cloud_officine_courante() TO pharma_cloud_etat;

-- ---- 3. Fonctions SECURITY DEFINER : plus d'EXECUTE pour PUBLIC (API inchangée) ----
DO $$
DECLARE f regprocedure;
BEGIN
    FOR f IN SELECT p.oid::regprocedure FROM pg_proc p
             WHERE p.pronamespace = 'public'::regnamespace AND p.prokind = 'f' AND p.prosecdef
               AND p.prorettype <> 'trigger'::regtype
               AND has_function_privilege('public', p.oid, 'EXECUTE')
             ORDER BY 1
    LOOP
        EXECUTE format('GRANT EXECUTE ON FUNCTION %s TO pharma_cloud_api', f);
        EXECUTE format('REVOKE EXECUTE ON FUNCTION %s FROM PUBLIC', f);
        RAISE NOTICE 'Script central 029 : EXECUTE retiré à PUBLIC, accordé à pharma_cloud_api : %', f;
    END LOOP;
END $$;

