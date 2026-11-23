-- =====================================================================
-- Tests du script 043 (règles de rattachement et verrou par officine), en transaction ANNULÉE :
--   psql -U <propriétaire> -d <base centrale de test> -v ON_ERROR_STOP=1 -f database/cloud/tests/043_regles_rattachement_ordres_central_tests.sql
-- Rattachement d'un compte déjà rattaché avec le droit « ordres » : droit perdu et ordres ouverts annulés (journal de sécurité) ;
-- sans ce droit : rien d'annulé ; essais bloqués « trop_d_essais » non comptés dans la fenêtre ; verrou par officine posé par la
-- création d'un ordre et l'envoi d'un message ; droits inchangés.
-- =====================================================================
SET client_encoding = 'UTF8';
BEGIN;

DO $$ BEGIN
    ASSERT has_function_privilege('pharma_cloud_ecrans', 'cloud_ecran_rattacher(text, text)', 'EXECUTE'), 'rattacher : écrans';
    ASSERT has_function_privilege('pharma_cloud_ecrans', 'cloud_ecran_titulaire_creer_ordre(text, uuid, text, text)', 'EXECUTE'), 'ordre : écrans';
    ASSERT has_function_privilege('pharma_cloud_ecrans', 'cloud_ecran_titulaire_envoyer_message(text, uuid, text, int, text)', 'EXECUTE'), 'message : écrans';
    ASSERT NOT has_function_privilege('public', 'cloud_ecran_rattacher(text, text)', 'EXECUTE'), 'rattacher : pas pour PUBLIC';
    ASSERT NOT has_function_privilege('pharma_cloud_api', 'cloud_ecran_rattacher(text, text)', 'EXECUTE'), 'rattacher : pas pour l''API';
    -- Verrou par officine posé avant la recherche d'un ordre ouvert, en plus du verrou par compte
    ASSERT pg_get_functiondef('cloud_ecran_titulaire_creer_ordre(text, uuid, text, text)'::regprocedure) LIKE '%ordre_officine:%', 'verrou par officine (ordre)';
    ASSERT pg_get_functiondef('cloud_ecran_titulaire_envoyer_message(text, uuid, text, int, text)'::regprocedure) LIKE '%ordre_officine:%', 'verrou par officine (message)';
    ASSERT pg_get_functiondef('cloud_ecran_titulaire_creer_ordre(text, uuid, text, text)'::regprocedure) LIKE '%ordre_titulaire:%', 'verrou par compte gardé (ordre)';
    ASSERT pg_get_functiondef('cloud_ecran_rattacher(text, text)'::regprocedure) LIKE '%IS DISTINCT FROM ''trop_d_essais''%', 'essais bloqués non comptés';
END $$;

INSERT INTO officine (id, libelle) VALUES ('11111111-0000-0000-0000-000000000430', 'Essai 0430 O1'), ('22222222-0000-0000-0000-000000000430', 'Essai 0430 O2');
SELECT cloud_compte_distant_creer('titulaire.a430', 'Titulaire A 0430', 'titulaire', '$2a$12$' || repeat('a', 53), NULL, 'a430@exemple.invalid', 'tests 043') \g /dev/null
UPDATE compte_distant SET mot_de_passe_provisoire = false WHERE identifiant = 'titulaire.a430';
INSERT INTO session_distante (empreinte_jeton, compte_id, etape, expire_le)
SELECT repeat('4', 64), c.id, 'ouverte', now() + interval '2 hours' FROM compte_distant c WHERE c.identifiant = 'titulaire.a430';
-- A rattaché à O1 avec le droit « ordres » et un ordre ouvert ; rattaché à O2 sans ce droit
INSERT INTO compte_officine (compte_id, officine_id, accorde_par, droits)
SELECT c.id, o.id, 'tests 043', o.droits FROM compte_distant c,
    (VALUES ('11111111-0000-0000-0000-000000000430'::uuid, ARRAY['consulter', 'journal_acces', 'ordres']),
            ('22222222-0000-0000-0000-000000000430'::uuid, ARRAY['consulter', 'journal_acces'])) o(id, droits)
WHERE c.identifiant = 'titulaire.a430';
SELECT set_config('app.officine_id', '11111111-0000-0000-0000-000000000430', true) \g /dev/null
INSERT INTO ordre_central (officine_id, type, auteur, expire_le, origine, compte_id)
SELECT '11111111-0000-0000-0000-000000000430', 'sauvegarde', 'x', now() + interval '1 hour', 'titulaire', c.id FROM compte_distant c WHERE c.identifiant = 'titulaire.a430';
SELECT set_config('app.officine_id', '', true) \g /dev/null
INSERT INTO code_rattachement (empreinte_code, compte_id, officine_id, expire_le, cree_par)
SELECT x.e, c.id, x.o, now() + interval '30 minutes', 'tests 043'
FROM compte_distant c, (VALUES (repeat('a', 64), '11111111-0000-0000-0000-000000000430'::uuid),
                               (repeat('b', 64), '22222222-0000-0000-0000-000000000430'::uuid)) x(e, o)
WHERE c.identifiant = 'titulaire.a430';

SET LOCAL ROLE pharma_cloud_ecrans;
DO $$ BEGIN
    ASSERT (SELECT statut FROM cloud_ecran_rattacher(repeat('4', 64), repeat('a', 64))) = 'ok', 'rattachement O1';
    ASSERT (SELECT statut FROM cloud_ecran_rattacher(repeat('4', 64), repeat('b', 64))) = 'ok', 'rattachement O2';
END $$;
RESET ROLE;
-- ordre_central est cloisonnée (RLS forcée) : lecture sur l'officine O1
SELECT set_config('app.officine_id', '11111111-0000-0000-0000-000000000430', true) \g /dev/null

DO $$ BEGIN
    ASSERT (SELECT x.droits FROM compte_officine x WHERE x.officine_id = '11111111-0000-0000-0000-000000000430') = '{consulter,journal_acces}', 'O1 : droit « ordres » perdu';
    ASSERT (SELECT statut FROM ordre_central WHERE officine_id = '11111111-0000-0000-0000-000000000430') = 'annule', 'O1 : ordre ouvert annulé';
    ASSERT EXISTS (SELECT 1 FROM journal_securite WHERE officine_id = '11111111-0000-0000-0000-000000000430' AND evenement = 'rattachement_titulaire'
                   AND detail LIKE '%1 ordre(s) ouvert(s) annulé(s)%'), 'O1 : annulation notée au journal de sécurité';
    ASSERT EXISTS (SELECT 1 FROM journal_securite WHERE officine_id = '22222222-0000-0000-0000-000000000430' AND evenement = 'rattachement_titulaire'
                   AND detail NOT LIKE '%annulé%'), 'O2 : rien d''annulé';
END $$;

-- Essais : 5 vrais refus, puis des essais bloqués ; les essais bloqués sont notés « trop_d_essais » et ne comptent pas
INSERT INTO code_rattachement (empreinte_code, compte_id, officine_id, expire_le, cree_par)
SELECT repeat('c', 64), c.id, '11111111-0000-0000-0000-000000000430', now() + interval '30 minutes', 'tests 043' FROM compte_distant c WHERE c.identifiant = 'titulaire.a430';
SET LOCAL ROLE pharma_cloud_ecrans;
DO $$ BEGIN
    FOR i IN 1..5 LOOP
        ASSERT (SELECT statut FROM cloud_ecran_rattacher(repeat('4', 64), repeat('0', 64))) = 'inconnu', 'refus ' || i;
    END LOOP;
    FOR i IN 1..3 LOOP
        ASSERT (SELECT statut FROM cloud_ecran_rattacher(repeat('4', 64), repeat('c', 64))) = 'trop_d_essais', 'bloqué ' || i;
    END LOOP;
END $$;
RESET ROLE;
DO $$ BEGIN
    ASSERT (SELECT count(*) FROM journal_acces_distant j JOIN compte_distant c ON c.id = j.compte_id WHERE c.identifiant = 'titulaire.a430'
            AND j.evenement = 'rattachement' AND j.resultat = 'refus' AND j.objet = 'trop_d_essais') = 3, 'trois essais bloqués notés';
    ASSERT (SELECT count(*) FROM journal_acces_distant j JOIN compte_distant c ON c.id = j.compte_id WHERE c.identifiant = 'titulaire.a430'
            AND j.evenement = 'rattachement' AND j.resultat = 'refus' AND j.objet IS DISTINCT FROM 'trop_d_essais') = 5, 'cinq vrais refus seulement comptés';
    ASSERT (SELECT utilise_le IS NULL FROM code_rattachement WHERE empreinte_code = repeat('c', 64)), 'code non consommé pendant le blocage';
END $$;

ROLLBACK;
