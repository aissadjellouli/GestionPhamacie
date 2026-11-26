-- =====================================================================
-- Tests du script 044 (ordre « activation de l'offre »), en transaction ANNULÉE :
--   psql -U <propriétaire> -d <base centrale de test> -v ON_ERROR_STOP=1 -f database/cloud/tests/044_ordre_activation_offre_tests.sql
-- Ordre créé avec le seul code, origine console, journal de l'ordre ; code mal formé, officine inconnue et auteur vide refusés ;
-- fonction fermée à PUBLIC et aux écrans ; le titulaire ne peut pas créer ce type d'ordre.
-- =====================================================================
SET client_encoding = 'UTF8';
BEGIN;

DO $$ BEGIN
    ASSERT NOT has_function_privilege('public', 'cloud_creer_ordre_activation(uuid, text, text)', 'EXECUTE'), 'fermée à PUBLIC';
    ASSERT NOT has_function_privilege('pharma_cloud_ecrans', 'cloud_creer_ordre_activation(uuid, text, text)', 'EXECUTE'), 'fermée aux écrans';
END $$;

INSERT INTO officine (id, libelle) VALUES ('11111111-0000-0000-0000-000000000440', 'Essai 0440');
DO $$
DECLARE v uuid; o ordre_central; refuse boolean;
BEGIN
    v := cloud_creer_ordre_activation('11111111-0000-0000-0000-000000000440', '  PO1.ZXNzYWktMDQ0.c2lnbmF0dXJl  ', 'editeur 044');
    SELECT * INTO o FROM ordre_central WHERE id = v;
    ASSERT o.type = 'activation_offre' AND o.donnees = '{"code": "PO1.ZXNzYWktMDQ0.c2lnbmF0dXJl"}'::jsonb AND o.origine = 'console'
           AND o.cible IS NULL AND o.expire_le > now() + interval '6 days', 'ordre créé avec le seul code';
    ASSERT EXISTS (SELECT 1 FROM ordre_central_journal WHERE ordre_id = v AND statut = 'en_attente'), 'journal de l''ordre';

    refuse := false;
    BEGIN PERFORM cloud_creer_ordre_activation('11111111-0000-0000-0000-000000000440', 'PO1.pas une signature', 'editeur 044');
    EXCEPTION WHEN raise_exception THEN refuse := true; END;
    ASSERT refuse, 'code mal formé accepté';

    refuse := false;
    BEGIN PERFORM cloud_creer_ordre_activation(gen_random_uuid(), 'PO1.ZXNzYWktMDQ0.c2lnbmF0dXJl', 'editeur 044');
    EXCEPTION WHEN raise_exception THEN refuse := true; END;
    ASSERT refuse, 'officine inconnue acceptée';

    refuse := false;
    BEGIN PERFORM cloud_creer_ordre_activation('11111111-0000-0000-0000-000000000440', 'PO1.ZXNzYWktMDQ0.c2lnbmF0dXJl', ' ');
    EXCEPTION WHEN raise_exception THEN refuse := true; END;
    ASSERT refuse, 'auteur vide accepté';

    -- Jamais au nom du titulaire (contrainte du 038 : liste fermée de ses types)
    refuse := false;
    BEGIN
        INSERT INTO ordre_central (officine_id, type, donnees, auteur, expire_le, origine)
        VALUES ('11111111-0000-0000-0000-000000000440', 'activation_offre', '{"code": "x"}', 'titulaire', now() + interval '1 hour', 'titulaire');
    EXCEPTION WHEN check_violation OR not_null_violation THEN refuse := true;
    END;
    ASSERT refuse, 'activation par le titulaire acceptée';
END $$;

ROLLBACK;
\echo '044_ordre_activation_offre_tests : OK'
