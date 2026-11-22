-- =====================================================================
-- Tests du script 042 (motif de la déconnexion après inactivité), en transaction ANNULÉE :
--   psql -U <propriétaire> -d <base centrale de test> -v ON_ERROR_STOP=1 -f database/cloud/tests/042_deconnexion_motif_inactivite_tests.sql
-- Droits (écrans seulement) ; « inactivite » retenu seulement si la base constate l'inactivité (tolérance de 30 s) ; sinon
-- déconnexion ordinaire ; sans motif : déconnexion ordinaire ; session déjà fermée : rien ; ancienne fonction inchangée.
-- =====================================================================
SET client_encoding = 'UTF8';
BEGIN;

DO $$ BEGIN
    ASSERT has_function_privilege('pharma_cloud_ecrans', 'cloud_ecran_deconnexion(text, text)', 'EXECUTE'), 'écrans : permis';
    ASSERT NOT has_function_privilege('public', 'cloud_ecran_deconnexion(text, text)', 'EXECUTE'), 'pas pour PUBLIC';
    ASSERT NOT has_function_privilege('pharma_cloud_api', 'cloud_ecran_deconnexion(text, text)', 'EXECUTE'), 'API : rien';
    ASSERT has_function_privilege('pharma_cloud_ecrans', 'cloud_ecran_deconnexion(text)', 'EXECUTE'), 'ancienne fonction gardée';
    ASSERT (SELECT p.prosecdef FROM pg_proc p WHERE p.oid = 'cloud_ecran_deconnexion(text, text)'::regprocedure), 'SECURITY DEFINER';
END $$;

-- Éditeur (inactivité réglée 15 min par défaut) et quatre sessions ouvertes
SELECT cloud_compte_distant_creer('editeur.e420', 'Éditeur 0420', 'editeur', '$2a$12$' || repeat('a', 53), NULL, 'e420@exemple.invalid', 'tests 042') \g /dev/null
UPDATE parametre_envoi_codes SET inactivite_editeur_minutes = 15 WHERE id = 1;
INSERT INTO session_distante (empreinte_jeton, compte_id, etape, expire_le, derniere_activite_le)
SELECT repeat(x.j, 64), c.id, 'ouverte', now() + interval '1 hour', now() - x.age
FROM (VALUES ('a', interval '20 minutes'),        -- inactive depuis plus que la durée : inactivité constatée
             ('b', interval '14 minutes 45 seconds'), -- 15 s avant l'échéance : dans la tolérance du minuteur de la page
             ('c', interval '2 minutes'),         -- active : la page ne peut pas prétendre à l'inactivité
             ('d', interval '20 minutes')) x(j, age)  -- sans motif : déconnexion ordinaire
JOIN compte_distant c ON c.identifiant = 'editeur.e420';

SET LOCAL ROLE pharma_cloud_ecrans;
SELECT cloud_ecran_deconnexion(repeat('a', 64), 'inactivite') \g /dev/null
SELECT cloud_ecran_deconnexion(repeat('b', 64), 'inactivite') \g /dev/null
SELECT cloud_ecran_deconnexion(repeat('c', 64), 'inactivite') \g /dev/null
SELECT cloud_ecran_deconnexion(repeat('d', 64), NULL) \g /dev/null
-- Session déjà fermée, puis jeton mal formé : rien
SELECT cloud_ecran_deconnexion(repeat('a', 64), 'inactivite') \g /dev/null
SELECT cloud_ecran_deconnexion('pas-un-jeton', 'inactivite') \g /dev/null
RESET ROLE;

DO $$ BEGIN
    ASSERT (SELECT string_agg(left(empreinte_jeton, 1) || ':' || motif_fin, ',' ORDER BY empreinte_jeton) FROM session_distante
            WHERE empreinte_jeton IN (repeat('a', 64), repeat('b', 64), repeat('c', 64), repeat('d', 64)))
           = 'a:inactivite,b:inactivite,c:deconnexion,d:deconnexion', 'motifs de fin';
    ASSERT (SELECT count(*) FROM journal_acces_distant j JOIN compte_distant c ON c.id = j.compte_id
            WHERE c.identifiant = 'editeur.e420' AND j.evenement = 'session_expiree' AND j.objet = 'inactivite') = 2, 'inactivité journalisée';
    ASSERT (SELECT count(*) FROM journal_acces_distant j JOIN compte_distant c ON c.id = j.compte_id
            WHERE c.identifiant = 'editeur.e420' AND j.evenement = 'deconnexion') = 2, 'déconnexions ordinaires journalisées';
    ASSERT cloud_tolerance_inactivite_page() = interval '30 seconds', 'tolérance';
END $$;

ROLLBACK;
