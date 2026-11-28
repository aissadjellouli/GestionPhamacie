-- =====================================================================
-- Tests du script 175 (la remise sur un médicament compte de nouveau au contrôle interne) et de son retour arrière, en transaction
-- ANNULÉE, depuis la racine du dépôt, après 174 :
--   psql -U pharma_app -d <base jetable> -v ON_ERROR_STOP=1 -f database/tests/175_remise_medicament_au_controle_tests.sql
-- Les deux comptages (remises par employé, remises_patient) exigent et additionnent montant_remise + montant_don ; rejeu sans erreur ;
-- aller, retour, aller. Sur une base qui a des ventes validées : une vente passée en « remise sur médicament » (taux positif,
-- montant_remise à 0, montant_don positif) est comptée après 175 et ne l'est pas après le retour.
-- =====================================================================
BEGIN;

\ir ../migration/175_remise_medicament_au_controle.sql
\ir ../migration/175_remise_medicament_au_controle.sql

SELECT (SELECT count(*) FROM regexp_matches(pg_get_functiondef('surveillance_candidats(date)'::regprocedure), 'montant_remise \+ v\.montant_don > 0', 'g')) = 2
   AND (SELECT count(*) FROM regexp_matches(pg_get_functiondef('surveillance_candidats(date)'::regprocedure), 'sum\(v\.montant_remise \+ v\.montant_don\)', 'g')) = 2 AS ok \gset
\if :ok
\else
    DO $$ BEGIN RAISE EXCEPTION 'les deux comptages de remises doivent exiger et additionner montant_remise + montant_don'; END $$;
\endif
-- La fonction s'exécute (aucune erreur de syntaxe ni de colonne)
SELECT count(*) >= 0 AS ok FROM surveillance_candidats(current_date) \gset
\if :ok
\else
    DO $$ BEGIN RAISE EXCEPTION 'surveillance_candidats en échec'; END $$;
\endif

-- Comportement, sur une base qui a au moins une vente validée d'un employé : seuil à 1, une vente passée en remise sur médicament
SELECT id AS vente_id, utilisateur_id AS employe, date_vente::date AS jour FROM vente WHERE statut = 'validee' ORDER BY id DESC LIMIT 1 \gset
SELECT :{?vente_id} AS a_des_ventes \gset
\if :a_des_ventes
    UPDATE vente SET remise_pct = 0, montant_remise = 0, montant_don = 0 WHERE date_vente::date = DATE :'jour';
    UPDATE vente SET remise_pct = 50, montant_remise = 0, montant_don = 120 WHERE id = :vente_id;
    UPDATE parametre_surveillance SET seuil_remises = 1 WHERE id = 1;
    SELECT count(*) = 1 AND max(o_valeur) = 120 AS ok FROM surveillance_candidats(DATE :'jour') WHERE o_type = 'remises' AND o_utilisateur_id = :employe \gset
    \if :ok
    \else
        DO $$ BEGIN RAISE EXCEPTION 'une remise sur médicament (montant_don) doit être comptée après 175'; END $$;
    \endif
    -- Décision du 2026-11-27 tenue : taux positif sans aucune réduction, non comptée
    UPDATE vente SET montant_don = 0 WHERE id = :vente_id;
    SELECT count(*) = 0 AS ok FROM surveillance_candidats(DATE :'jour') WHERE o_type = 'remises' \gset
    \if :ok
    \else
        DO $$ BEGIN RAISE EXCEPTION 'une remise de 0 DA ne doit pas être comptée'; END $$;
    \endif
    UPDATE vente SET montant_don = 120 WHERE id = :vente_id;
\else
    \echo '175 : aucune vente validée dans cette base, essai de comportement non fait'
\endif

\ir ../migration/retour/175_remise_medicament_au_controle_retour.sql
\ir ../migration/retour/175_remise_medicament_au_controle_retour.sql
SELECT position('montant_don > 0' in pg_get_functiondef('surveillance_candidats(date)'::regprocedure)) = 0
   AND (SELECT count(*) FROM regexp_matches(pg_get_functiondef('surveillance_candidats(date)'::regprocedure), 'montant_remise > 0', 'g')) = 2 AS ok \gset
\if :ok
\else
    DO $$ BEGIN RAISE EXCEPTION 'retour incomplet'; END $$;
\endif
\if :a_des_ventes
    SELECT count(*) = 0 AS ok FROM surveillance_candidats(DATE :'jour') WHERE o_type = 'remises' \gset
    \if :ok
    \else
        DO $$ BEGIN RAISE EXCEPTION 'après le retour, la remise sur médicament ne doit plus être comptée (état du script 173)'; END $$;
    \endif
\endif
\ir ../migration/175_remise_medicament_au_controle.sql
SELECT position('montant_don > 0' in pg_get_functiondef('surveillance_candidats(date)'::regprocedure)) > 0 AS ok \gset
\if :ok
\else
    DO $$ BEGIN RAISE EXCEPTION 'aller après retour en échec'; END $$;
\endif

ROLLBACK;
\echo '175_remise_medicament_au_controle_tests : OK'
