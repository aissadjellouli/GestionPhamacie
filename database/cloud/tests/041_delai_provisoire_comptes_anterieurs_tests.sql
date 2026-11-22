-- =====================================================================
-- Tests du script 041 (délai de grâce des mots de passe provisoires antérieurs au contrôle du 040), en transaction ANNULÉE :
--   psql -U <propriétaire> -d <base centrale de test> -v ON_ERROR_STOP=1 -f database/cloud/tests/041_delai_provisoire_comptes_anterieurs_tests.sql
-- Départ du contrôle posé ; compte provisoire créé avant le départ : durée complète à partir du départ (accepté), refusé une fois
-- cette durée passée ; compte créé ou remis à zéro après le départ : règle du 040 inchangée ; fonction interne sans droit pour PUBLIC.
-- =====================================================================
SET client_encoding = 'UTF8';
BEGIN;

DO $$ BEGIN
    ASSERT (SELECT provisoire_controle_depuis FROM parametre_envoi_codes WHERE id = 1) <= now(), 'départ du contrôle posé';
    ASSERT NOT has_function_privilege('public', 'cloud_compte_provisoire_echeance(timestamptz, timestamptz)', 'EXECUTE'), 'interne';
    ASSERT NOT has_function_privilege('pharma_cloud_ecrans', 'cloud_compte_provisoire_echeance(timestamptz, timestamptz)', 'EXECUTE'), 'interne';
END $$;

-- Durée 7 jours ; contrôle en place depuis 2 jours
UPDATE parametre_envoi_codes SET duree_provisoire_jours = 7, provisoire_controle_depuis = now() - interval '2 days' WHERE id = 1;
SELECT cloud_compte_distant_creer('ancien.g410', 'Ancien 0410', 'titulaire', '$2a$12$' || repeat('a', 53), NULL, 'g410@exemple.invalid', 'tests 041') \g /dev/null
SELECT cloud_compte_distant_creer('recent.g410', 'Recent 0410', 'titulaire', '$2a$12$' || repeat('a', 53), NULL, 'r410@exemple.invalid', 'tests 041') \g /dev/null
SELECT cloud_compte_distant_creer('remis.g410', 'Remis 0410', 'titulaire', '$2a$12$' || repeat('a', 53), NULL, 'm410@exemple.invalid', 'tests 041') \g /dev/null
-- ancien : créé il y a 30 jours, jamais utilisé ; recent : créé il y a 1 jour (après le départ) ; remis : créé il y a 60 jours,
-- remis à zéro il y a 10 jours (avant le départ)
UPDATE compte_distant SET cree_le = now() - interval '30 days' WHERE identifiant = 'ancien.g410';
UPDATE compte_distant SET cree_le = now() - interval '1 day' WHERE identifiant = 'recent.g410';
UPDATE compte_distant SET cree_le = now() - interval '60 days', mot_de_passe_change_le = now() - interval '10 days' WHERE identifiant = 'remis.g410';

DO $$ DECLARE c record; BEGIN
    -- Antérieurs au contrôle : échéance = départ + 7 jours (dans 5 jours), plus « expiré dès la mise en production »
    FOR c IN SELECT * FROM compte_distant WHERE identifiant IN ('ancien.g410', 'remis.g410') LOOP
        ASSERT cloud_compte_provisoire_echeance(c.mot_de_passe_change_le, c.cree_le) BETWEEN now() + interval '4 days 23 hours' AND now() + interval '5 days 1 hour',
            'délai de grâce : ' || c.identifiant;
    END LOOP;
    -- Postérieur : règle du 040 (création + 7 jours)
    SELECT * INTO c FROM compte_distant WHERE identifiant = 'recent.g410';
    ASSERT cloud_compte_provisoire_echeance(c.mot_de_passe_change_le, c.cree_le) = c.cree_le + interval '7 days', 'règle du 040 après le départ';
END $$;

-- Connexion à mot de passe juste : l'ancien compte est accepté pendant le délai de grâce
SET LOCAL ROLE pharma_cloud_ecrans;
DO $$ DECLARE id_ancien uuid; BEGIN
    SELECT compte_id INTO id_ancien FROM cloud_ecran_debut_connexion('ancien.g410');
    ASSERT (SELECT statut FROM cloud_ecran_fin_connexion(id_ancien, true, repeat('9', 64))) = 'code_attendu', 'ancien compte accepté pendant la grâce';
END $$;
RESET ROLE;

-- Délai de grâce passé (contrôle en place depuis 8 jours) : l'ancien compte est refusé comme au 040
UPDATE parametre_envoi_codes SET provisoire_controle_depuis = now() - interval '8 days' WHERE id = 1;
SET LOCAL ROLE pharma_cloud_ecrans;
DO $$ DECLARE id_ancien uuid; BEGIN
    SELECT compte_id INTO id_ancien FROM cloud_ecran_debut_connexion('ancien.g410');
    ASSERT (SELECT statut FROM cloud_ecran_fin_connexion(id_ancien, true, repeat('8', 64))) = 'provisoire_expire', 'grâce passée : refusé';
END $$;
RESET ROLE;

-- Rejeu du 041 : le départ du contrôle ne bouge pas
DO $$ DECLARE avant timestamptz; BEGIN
    SELECT provisoire_controle_depuis INTO avant FROM parametre_envoi_codes WHERE id = 1;
    ALTER TABLE parametre_envoi_codes ADD COLUMN IF NOT EXISTS provisoire_controle_depuis timestamptz NOT NULL DEFAULT now();
    ASSERT (SELECT provisoire_controle_depuis FROM parametre_envoi_codes WHERE id = 1) = avant, 'rejeu : départ inchangé';
END $$;

ROLLBACK;
