-- =====================================================================
-- Tests du script 141 (mutualisation des réglages, socle de l'officine), en transaction ANNULÉE :
--   psql -U pharma_app -d pharmacie_test -v ON_ERROR_STOP=1 -f database/tests/141_mutualisation_reglages_tests.sql
-- =====================================================================
BEGIN;

-- 1. État initial : 11 tables, toutes locales, aucun modèle, comportement inchangé
DO $$
BEGIN
    ASSERT cardinality(reglage_tables_mutualisables()) = 11, '11 tables';
    ASSERT (SELECT count(*) FROM reglage_mutualise) = 11, '11 lignes';
    ASSERT NOT EXISTS (SELECT 1 FROM unnest(reglage_tables_mutualisables()) t WHERE NOT EXISTS (SELECT 1 FROM reglage_mutualise r WHERE r.nom_table = t)), 'une ligne par table';
    ASSERT NOT EXISTS (SELECT 1 FROM reglage_mutualise WHERE mode <> 'local' OR etat <> 'aucun' OR modele IS NOT NULL OR mode_recu IS NOT NULL
                       OR version_modele_recue IS NOT NULL OR version_modele_appliquee IS NOT NULL OR origine <> 'locale'),
           'toutes locales, sans modèle, sans état';
    ASSERT NOT EXISTS (SELECT 1 FROM unnest(reglage_tables_mutualisables()) t WHERE NOT EXISTS (SELECT 1 FROM sync_table s WHERE s.nom_table = t AND s.sens = 'bidirectionnelle')),
           'les 11 tables sont bidirectionnelles';
    ASSERT (SELECT sens FROM sync_table WHERE nom_table = 'reglage_mutualise') = 'montante', 'classée montante';
    ASSERT (SELECT colonnes_exclues FROM sync_table WHERE nom_table = 'reglage_mutualise') = '{modele}', 'modele non envoyé';
    ASSERT (SELECT cle_colonnes FROM sync_table WHERE nom_table = 'reglage_mutualise') = '{nom_table}', 'clé';
    ASSERT NOT EXISTS (SELECT sync_tables_non_classees()), 'aucune table non classée';
    ASSERT (SELECT count(*) FROM pg_trigger WHERE tgname = 'zz_reglage_garde' AND NOT tgisinternal) = 11, 'garde posée sur les 11 tables';
    ASSERT has_table_privilege('pharma_app', 'public.reglage_mutualise', 'SELECT, INSERT, UPDATE, DELETE'), 'droits de pharma_app';
    ASSERT (SELECT officine_id FROM reglage_mutualise LIMIT 1) = officine_courante(), 'code officine';
    -- Table hors liste refusée
    BEGIN
        INSERT INTO reglage_mutualise (nom_table) VALUES ('parametre_caisse');
        RAISE EXCEPTION 'table du groupe 3 acceptée';
    EXCEPTION WHEN check_violation THEN NULL;
    END;
    -- Sujet de notification
    ASSERT (SELECT pg_get_constraintdef(oid) FROM pg_constraint WHERE conname = 'destinataire_notification_types_check') LIKE '%reglages_centraux%', 'sujet destinataires';
    ASSERT (SELECT pg_get_constraintdef(oid) FROM pg_constraint WHERE conname = 'message_sortant_type_check') LIKE '%reglages_centraux%', 'sujet messages';
END $$;

-- 2. Mode effectif : « local » tant que la synchronisation n'est pas active et l'officine inscrite
UPDATE reglage_mutualise SET mode = 'impose' WHERE nom_table = 'parametre_substitution';
DO $$
BEGIN
    ASSERT (SELECT NOT active FROM sync_parametre), 'essai sur une base dont la synchronisation est désactivée';
    ASSERT reglage_mode_effectif('parametre_substitution') = 'local', 'désactivée : local malgré mode imposé (valeurs gardées)';
    UPDATE parametre_substitution SET equivalent_exact_seulement = NOT equivalent_exact_seulement;   -- permis
    UPDATE sync_parametre SET active = true, inscrite_le = NULL WHERE id = 1;
    ASSERT reglage_mode_effectif('parametre_substitution') = 'local', 'active mais non inscrite : local';
    UPDATE sync_parametre SET inscrite_le = now() WHERE id = 1;
    ASSERT reglage_mode_effectif('parametre_substitution') = 'impose', 'active et inscrite : mode en vigueur';
    ASSERT reglage_mode_effectif('parametre_fiscal') = 'local', 'autre table : local';
END $$;

-- 3. Table imposée : refus partout (sauf cas particuliers), message en français
UPDATE reglage_mutualise SET mode = 'impose';
DO $$
DECLARE t text;
BEGIN
    FOREACH t IN ARRAY ARRAY['parametre_alerte', 'parametre_fiscal', 'parametre_formulaire_sms', 'parametre_inventaire', 'parametre_serialisation',
                             'parametre_substitution', 'parametre_surveillance', 'parametre_tracabilite', 'regle_alerte_psychotrope'] LOOP
        ASSERT EXISTS (SELECT 1 FROM pg_class WHERE relname = t AND relkind = 'r'), t;
        BEGIN
            EXECUTE format('DELETE FROM %I', t);
            RAISE EXCEPTION 'suppression acceptée sur % imposée', t;
        EXCEPTION WHEN raise_exception THEN ASSERT SQLERRM LIKE 'Réglage fixé par le serveur central : %' AND SQLERRM LIKE '%' || t || '%', t || ' : ' || SQLERRM;
        END;
        BEGIN
            EXECUTE format('INSERT INTO %I DEFAULT VALUES', t);
            RAISE EXCEPTION 'insertion acceptée sur % imposée', t;
        EXCEPTION WHEN raise_exception THEN ASSERT SQLERRM LIKE 'Réglage fixé par le serveur central : %', t || ' : ' || SQLERRM;
        END;
    END LOOP;
    BEGIN
        UPDATE parametre_substitution SET equivalent_exact_seulement = NOT equivalent_exact_seulement;
        RAISE EXCEPTION 'modification acceptée sur table imposée';
    EXCEPTION WHEN raise_exception THEN ASSERT SQLERRM LIKE 'Réglage fixé par le serveur central : %', SQLERRM;
    END;
    BEGIN
        UPDATE regle_alerte_psychotrope SET seuil = seuil + 1 WHERE code = 'patient_repete';
        RAISE EXCEPTION 'modification acceptée sur table imposée';
    EXCEPTION WHEN raise_exception THEN ASSERT SQLERRM LIKE 'Réglage fixé par le serveur central : %', SQLERRM;
    END;
    -- Écriture sans effet : permise
    UPDATE parametre_substitution SET equivalent_exact_seulement = equivalent_exact_seulement;
END $$;

-- 3 bis. Application d'un modèle : le marqueur de session laisse tout passer (transaction courante seulement)
DO $$
DECLARE v boolean;
BEGIN
    PERFORM set_config('pharma.reglage_central', 'on', true);
    UPDATE parametre_substitution SET equivalent_exact_seulement = NOT equivalent_exact_seulement RETURNING equivalent_exact_seulement INTO v;
    UPDATE regle_alerte_psychotrope SET seuil = seuil + 1 WHERE code = 'patient_repete';
    PERFORM set_config('pharma.reglage_central', 'off', true);
    BEGIN
        UPDATE parametre_substitution SET equivalent_exact_seulement = NOT equivalent_exact_seulement;
        RAISE EXCEPTION 'marqueur « off » ignoré';
    EXCEPTION WHEN raise_exception THEN ASSERT SQLERRM LIKE 'Réglage fixé par le serveur central : %', SQLERRM;
    END;
    PERFORM set_config('pharma.reglage_central', '', true);
    BEGIN
        UPDATE parametre_substitution SET equivalent_exact_seulement = NOT equivalent_exact_seulement;
        RAISE EXCEPTION 'marqueur vide ignoré';
    EXCEPTION WHEN raise_exception THEN ASSERT SQLERRM LIKE 'Réglage fixé par le serveur central : %', SQLERRM;
    END;
END $$;

-- 4. D7 : horaires du contrôle interne toujours locaux, même imposé
DO $$
BEGIN
    UPDATE parametre_surveillance SET heure_ouverture = '08:00', heure_fermeture = '20:00';
    ASSERT (SELECT heure_ouverture FROM parametre_surveillance) = '08:00', 'horaires modifiés malgré le mode imposé';
    UPDATE parametre_surveillance SET heure_ouverture = NULL, heure_fermeture = NULL;
    BEGIN
        UPDATE parametre_surveillance SET seuil_tiroir = seuil_tiroir + 1;
        RAISE EXCEPTION 'seuil modifié malgré le mode imposé';
    EXCEPTION WHEN raise_exception THEN ASSERT SQLERRM LIKE 'Réglage fixé par le serveur central : %', SQLERRM;
    END;
    BEGIN
        UPDATE parametre_surveillance SET heure_ouverture = '08:00', heure_fermeture = '20:00', seuil_tiroir = seuil_tiroir + 1;
        RAISE EXCEPTION 'horaires et seuil modifiés ensemble';
    EXCEPTION WHEN raise_exception THEN ASSERT SQLERRM LIKE 'Réglage fixé par le serveur central : %', SQLERRM;
    END;
END $$;

-- 5. D5 : motifs. Ajouts locaux permis, motifs du central verrouillés, repérage jamais posé hors application d'un modèle
DO $$
DECLARE v_central bigint; v_local bigint;
BEGIN
    INSERT INTO motif_reglable (liste, code, libelle) VALUES ('sortie_stock', 'essai_local_141', 'Essai local 141') RETURNING id INTO v_local;
    ASSERT v_local IS NOT NULL, 'ajout local permis en mode imposé (ajouts_locaux vrai par défaut)';
    UPDATE motif_reglable SET libelle = 'Essai local 141 b' WHERE id = v_local;   -- motif local : modifiable
    BEGIN
        INSERT INTO motif_reglable (liste, code, libelle, modele_code_central) VALUES ('sortie_stock', 'c_essai_141', 'Essai central 141', true);
        RAISE EXCEPTION 'repérage posé hors application';
    EXCEPTION WHEN raise_exception THEN ASSERT SQLERRM LIKE 'Réglage fixé par le serveur central : %', SQLERRM;
    END;
    BEGIN
        UPDATE motif_reglable SET modele_code_central = true WHERE id = v_local;
        RAISE EXCEPTION 'repérage modifié hors application';
    EXCEPTION WHEN raise_exception THEN ASSERT SQLERRM LIKE 'Réglage fixé par le serveur central : %', SQLERRM;
    END;
    PERFORM set_config('pharma.reglage_central', 'on', true);
    INSERT INTO motif_reglable (liste, code, libelle, modele_code_central) VALUES ('sortie_stock', 'c_essai_141', 'Essai central 141', true) RETURNING id INTO v_central;
    PERFORM set_config('pharma.reglage_central', 'off', true);
    BEGIN
        UPDATE motif_reglable SET libelle = 'Changé ici' WHERE id = v_central;
        RAISE EXCEPTION 'motif du central modifié';
    EXCEPTION WHEN raise_exception THEN ASSERT SQLERRM LIKE 'Réglage fixé par le serveur central : %', SQLERRM;
    END;
    BEGIN
        DELETE FROM motif_reglable WHERE id = v_central;
        RAISE EXCEPTION 'motif du central supprimé';
    EXCEPTION WHEN raise_exception THEN ASSERT SQLERRM LIKE 'Réglage fixé par le serveur central : %', SQLERRM;
    END;
    -- Ajouts locaux refusés quand le modèle les interdit
    UPDATE reglage_mutualise SET ajouts_locaux = false WHERE nom_table = 'motif_reglable';
    BEGIN
        INSERT INTO motif_reglable (liste, code, libelle) VALUES ('sortie_stock', 'essai_local_141b', 'Essai local 141 bis');
        RAISE EXCEPTION 'ajout local accepté malgré ajouts_locaux = false';
    EXCEPTION WHEN raise_exception THEN ASSERT SQLERRM LIKE 'Réglage fixé par le serveur central : %', SQLERRM;
    END;
    -- Modèle retiré : le motif redevient local (repérage retiré par l'application) puis se modifie comme un motif local
    PERFORM set_config('pharma.reglage_central', 'on', true);
    UPDATE motif_reglable SET modele_code_central = false WHERE id = v_central;
    PERFORM set_config('pharma.reglage_central', 'off', true);
    UPDATE motif_reglable SET libelle = 'Redevenu local' WHERE id = v_central;
END $$;

-- 6. D6 : remises. Aucune création locale en mode imposé ; une remise du central (catégorie, sans auteur) est verrouillée
DO $$
DECLARE v_central bigint; v_local bigint; v_user bigint := (SELECT min(id) FROM utilisateur);
BEGIN
    BEGIN
        INSERT INTO remise_automatique (categorie, pourcentage, date_debut, date_fin, cree_par) VALUES ('dermo_cosmetique', 10, current_date, current_date + 10, v_user);
        RAISE EXCEPTION 'création locale acceptée en mode imposé';
    EXCEPTION WHEN raise_exception THEN ASSERT SQLERRM LIKE 'Réglage fixé par le serveur central : %', SQLERRM;
    END;
    PERFORM set_config('pharma.reglage_central', 'on', true);
    INSERT INTO remise_automatique (categorie, pourcentage, date_debut, date_fin, cree_par) VALUES ('dermo_cosmetique', 10, current_date, current_date + 10, v_user) RETURNING id INTO v_local;
    INSERT INTO remise_automatique (categorie, pourcentage, date_debut, date_fin, modele_cle) VALUES ('hygiene', 5, current_date, current_date + 10, 'c_abcdef123456') RETURNING id INTO v_central;
    PERFORM set_config('pharma.reglage_central', 'off', true);
    ASSERT (SELECT cree_par IS NULL FROM remise_automatique WHERE id = v_central), 'remise du central sans auteur local';
    -- remise locale existante : se modifie (arrêt) ; remise du central : verrouillée
    UPDATE remise_automatique SET arretee_le = now() WHERE id = v_local;
    BEGIN
        UPDATE remise_automatique SET arretee_le = now() WHERE id = v_central;
        RAISE EXCEPTION 'remise du central modifiée';
    EXCEPTION WHEN raise_exception THEN ASSERT SQLERRM LIKE 'Réglage fixé par le serveur central : %', SQLERRM;
    END;
    BEGIN
        UPDATE remise_automatique SET modele_cle = NULL WHERE id = v_central;
        RAISE EXCEPTION 'repérage de la remise modifié hors application';
    EXCEPTION WHEN raise_exception THEN ASSERT SQLERRM LIKE 'Réglage fixé par le serveur central : %', SQLERRM;
    END;
    -- Contraintes : auteur obligatoire pour une remise locale ; remise du central par catégorie seulement, clé « c_ » + 12 caractères
    PERFORM set_config('pharma.reglage_central', 'on', true);
    BEGIN
        INSERT INTO remise_automatique (categorie, pourcentage, date_debut, date_fin) VALUES ('hygiene', 5, current_date, current_date + 10);
        RAISE EXCEPTION 'remise locale sans auteur acceptée';
    EXCEPTION WHEN check_violation THEN NULL;
    END;
    BEGIN
        INSERT INTO remise_automatique (produit_id, pourcentage, date_debut, date_fin, modele_cle) VALUES ((SELECT min(id) FROM produit), 5, current_date, current_date + 10, 'c_abcdef123457');
        RAISE EXCEPTION 'remise du central sur un produit acceptée';
    EXCEPTION WHEN check_violation OR not_null_violation THEN NULL;
    END;
    BEGIN
        INSERT INTO remise_automatique (categorie, pourcentage, date_debut, date_fin, modele_cle) VALUES ('hygiene', 5, current_date, current_date + 10, 'mauvaise');
        RAISE EXCEPTION 'clé de remise invalide acceptée';
    EXCEPTION WHEN check_violation THEN NULL;
    END;
    BEGIN
        INSERT INTO remise_automatique (categorie, pourcentage, date_debut, date_fin, modele_cle) VALUES ('hygiene', 6, current_date, current_date + 10, 'c_abcdef123456');
        RAISE EXCEPTION 'clé de remise en double acceptée';
    EXCEPTION WHEN unique_violation THEN NULL;
    END;
    PERFORM set_config('pharma.reglage_central', 'off', true);
END $$;

-- 7. Hors mode imposé : modification locale permise et notée (origine locale, date)
UPDATE reglage_mutualise SET mode = 'propose', origine = 'central', modifie_localement_le = NULL WHERE nom_table = 'parametre_tracabilite';
UPDATE reglage_mutualise SET mode = 'local', origine = 'central', modifie_localement_le = NULL WHERE nom_table = 'parametre_inventaire';
UPDATE reglage_mutualise SET mode = 'local', origine = 'locale', modifie_localement_le = NULL WHERE nom_table = 'parametre_fiscal';
DO $$
BEGIN
    UPDATE parametre_tracabilite SET boite_obligatoire = NOT boite_obligatoire;
    ASSERT (SELECT origine = 'locale' AND modifie_localement_le IS NOT NULL FROM reglage_mutualise WHERE nom_table = 'parametre_tracabilite'), 'proposé : modification locale notée';
    UPDATE parametre_inventaire SET seuil_boites = seuil_boites + 1;
    ASSERT (SELECT origine = 'locale' AND modifie_localement_le IS NOT NULL FROM reglage_mutualise WHERE nom_table = 'parametre_inventaire'), 'valeur du central modifiée après libération : notée';
    UPDATE parametre_fiscal SET conservation_annees = conservation_annees + 1;
    ASSERT (SELECT origine = 'locale' AND modifie_localement_le IS NOT NULL FROM reglage_mutualise WHERE nom_table = 'parametre_fiscal'), 'local sans modèle : modification notée aussi (script 142)';
END $$;

-- 8. Journal avant / après
DO $$
DECLARE v_id bigint;
BEGIN
    v_id := reglage_journaliser('reglage_central_remplace', 'parametre_alerte', 'propose', 'impose', 3, 'admin-central-1',
                                '{"seuil_rouge": 7}'::jsonb, '{"seuil_rouge": 14}'::jsonb);
    ASSERT (SELECT details #>> '{valeur,avant,seuil_rouge}' = '7' AND details #>> '{valeur,apres,seuil_rouge}' = '14'
                   AND details #>> '{mode,avant}' = 'propose' AND details #>> '{mode,apres}' = 'impose' AND (details ->> 'version_modele')::int = 3
                   AND utilisateur_id IS NULL AND table_cible = 'parametre_alerte'
            FROM journal_audit WHERE id = v_id), 'avant / après journalisés';
    BEGIN
        PERFORM reglage_journaliser('autre_action', 'parametre_alerte', NULL, NULL, 1, NULL, NULL, NULL);
        RAISE EXCEPTION 'action inconnue acceptée';
    EXCEPTION WHEN raise_exception THEN ASSERT SQLERRM LIKE '%action de journal inconnue%', SQLERRM;
    END;
    BEGIN
        PERFORM reglage_journaliser('reglage_central_applique', 'parametre_caisse', NULL, NULL, 1, NULL, NULL, NULL);
        RAISE EXCEPTION 'table du groupe 3 acceptée';
    EXCEPTION WHEN raise_exception THEN ASSERT SQLERRM LIKE '%non mutualisable%', SQLERRM;
    END;
END $$;

-- 9. Voie descendante : éléments « mutu_<table> »
DO $$
DECLARE
    v_el jsonb := '{"format":"mutu-v1","mode":"propose","version_modele":4,"ajouts_locaux":true,"auteur":"admin-central-1","valeur":{"boite_obligatoire":true}}';
    r text;
BEGIN
    DELETE FROM reglage_central;
    UPDATE sync_parametre SET descendant_version = 0, descendant_saut_max = 1000 WHERE id = 1;
    UPDATE reglage_mutualise SET etat = 'aucun', motif_rejet = NULL, mode_recu = NULL, version_modele_recue = NULL, modele = NULL, version_modele_rejetee = NULL;

    -- Élément valide : rangé, état de la table renseigné, mode en vigueur INCHANGÉ (l'application le décide), journal local avant / après
    ASSERT sync_recevoir_descendant('reglage_central', 'mutu_parametre_tracabilite', 1, v_el, 5) = 'applique', 'élément valide appliqué';
    ASSERT (SELECT version FROM reglage_central WHERE cle = 'mutu_parametre_tracabilite') = 1, 'rangé dans reglage_central';
    ASSERT (SELECT mode_recu = 'propose' AND version_modele_recue = 4 AND recu_le IS NOT NULL AND modele = v_el AND mode = 'propose' AND etat = 'aucun'
            FROM reglage_mutualise WHERE nom_table = 'parametre_tracabilite'), 'état de la table : modèle reçu rangé';
    ASSERT (SELECT apres ->> 'version_modele' = '4' AND apres ->> 'mode' = 'propose' FROM sync_journal_descendant
            WHERE cle = 'mutu_parametre_tracabilite' AND statut = 'applique'), 'journal : version de modèle et mode';
    ASSERT sync_recevoir_descendant('reglage_central', 'mutu_parametre_tracabilite', 1, v_el, 5) = 'ancien', 'même version : ignorée';
    ASSERT sync_recevoir_descendant('reglage_central', 'mutu_parametre_tracabilite', 2, v_el || '{"version_modele":5,"mode":"impose"}', 5) = 'applique', 'version plus récente';
    ASSERT (SELECT mode_recu = 'impose' AND version_modele_recue = 5 AND mode = 'propose' FROM reglage_mutualise WHERE nom_table = 'parametre_tracabilite'), 'nouvelle version reçue';
    ASSERT (SELECT avant ->> 'version_modele' = '4' AND apres ->> 'version_modele' = '5' FROM sync_journal_descendant
            WHERE cle = 'mutu_parametre_tracabilite' AND version = 2), 'journal : avant 4, après 5';

    -- Table non mutualisable : journalisée « invalide », rien rangé, ACQUITTÉE sans bloquer les éléments suivants
    ASSERT sync_recevoir_descendant('reglage_central', 'mutu_parametre_caisse', 3, v_el, 5) = 'invalide', 'groupe 3 : invalide';
    ASSERT sync_recevoir_descendant('reglage_central', 'mutu_parametre_caisse', 3, v_el, 5) = 'invalide', 'rejouée : toujours invalide';
    ASSERT (SELECT count(*) FROM sync_journal_descendant WHERE cle = 'mutu_parametre_caisse' AND statut = 'invalide') = 1, 'une seule ligne de journal';
    ASSERT NOT EXISTS (SELECT 1 FROM reglage_central WHERE cle = 'mutu_parametre_caisse'), 'rien rangé';
    ASSERT sync_recevoir_descendant('reglage_central', 'mutu_', 4, v_el, 5) = 'invalide', 'clé sans table : invalide';
    ASSERT sync_recevoir_descendant('reglage_central', 'mutu_parametre_fiscal', 5,
                                    '{"format":"mutu-v1","mode":"impose","version_modele":1,"valeur":{"conservation_ans":10}}', 5) = 'applique', 'l''élément suivant est traité';
    ASSERT (SELECT mode_recu = 'impose' FROM reglage_mutualise WHERE nom_table = 'parametre_fiscal'), 'suivant rangé';
    ASSERT sync_noter_version_descendante(5) = 5, 'acquittement jusqu''à 5 possible (aucun refus bloquant)';

    -- Élément d'une table connue mais mal formé : acquitté, table marquée « rejetée », rien d'autre ne change
    ASSERT sync_recevoir_descendant('reglage_central', 'mutu_parametre_alerte', 6, '{"format":"mutu-v9","mode":"impose","version_modele":2,"valeur":{}}', 9) = 'applique', 'format inconnu';
    ASSERT (SELECT etat = 'rejete' AND motif_rejet LIKE '%format non pris en charge%' AND mode = 'impose' AND mode_recu IS NULL AND version_modele_rejetee = 2
            FROM reglage_mutualise WHERE nom_table = 'parametre_alerte'), 'format inconnu : rejetée, mode précédent gardé';
    ASSERT sync_recevoir_descendant('reglage_central', 'mutu_parametre_inventaire', 7, '{"format":"mutu-v1","mode":"obligatoire","version_modele":2,"valeur":{}}', 9) = 'applique', 'mode inconnu';
    ASSERT (SELECT etat = 'rejete' AND motif_rejet LIKE '%mode inconnu%' FROM reglage_mutualise WHERE nom_table = 'parametre_inventaire'), 'mode inconnu rejeté';
    ASSERT sync_recevoir_descendant('reglage_central', 'mutu_parametre_serialisation', 8, '{"format":"mutu-v1","mode":"impose","version_modele":"x","valeur":{}}', 9) = 'applique', 'version invalide';
    ASSERT (SELECT etat = 'rejete' AND version_modele_rejetee IS NULL FROM reglage_mutualise WHERE nom_table = 'parametre_serialisation'), 'version invalide rejetée';
    ASSERT sync_recevoir_descendant('reglage_central', 'mutu_parametre_substitution', 9, '{"format":"mutu-v1","mode":"impose","version_modele":1}', 9) = 'applique', 'valeur absente';
    ASSERT (SELECT etat = 'rejete' AND motif_rejet LIKE '%valeur du modèle absente%' FROM reglage_mutualise WHERE nom_table = 'parametre_substitution'), 'valeur absente rejetée';
    -- Mode local (libération) : sans valeur
    ASSERT sync_recevoir_descendant('reglage_central', 'mutu_parametre_formulaire_sms', 10, '{"format":"mutu-v1","mode":"local","version_modele":3}', 10) = 'applique', 'libération';
    ASSERT (SELECT mode_recu = 'local' AND etat = 'aucun' FROM reglage_mutualise WHERE nom_table = 'parametre_formulaire_sms'), 'libération rangée';

    -- Protections de transport inchangées : version au-delà du maximum, saut anormal, trop gros, clé inutilisable
    ASSERT sync_recevoir_descendant('reglage_central', 'mutu_parametre_fiscal', 11, v_el, 10) = 'refuse', 'au-delà de la version maximale';
    ASSERT sync_recevoir_descendant('reglage_central', 'mutu_parametre_fiscal', 5000, v_el, 6000) = 'refuse', 'saut anormal';
    ASSERT sync_recevoir_descendant('reglage_central', 'mutu_parametre_fiscal', 12, jsonb_build_object('format', 'mutu-v1', 'mode', 'impose', 'version_modele', 9,
                                    'valeur', jsonb_build_object('x', repeat('a', 70000))), 12) = 'refuse', 'plus de 64 Kio';
    ASSERT sync_recevoir_descendant('reglage_central', 'Mauvaise-Cle', 12, v_el, 12) = 'refuse', 'clé inutilisable : refusée sans erreur';
    ASSERT sync_recevoir_descendant('vente', '1', 12, v_el, 12) = 'table_inconnue', 'table descendante inconnue';

    -- Les éléments qui ne sont pas « mutu_ » se comportent comme avant (script 136)
    ASSERT sync_recevoir_descendant('reglage_central', 'familles_envoyees', 13, '{"familles":["pharmacie"]}', 13) = 'applique', 'familles envoyées';
    ASSERT sync_familles_effectives() = '{pharmacie}', 'familles lues localement';
    ASSERT (SELECT avant IS NULL AND apres = '["pharmacie"]'::jsonb FROM sync_journal_descendant WHERE cle = 'familles_envoyees' AND version = 13), 'journal des familles inchangé';
END $$;

\echo 'Tests du script 141 : tous réussis (transaction annulée).'
ROLLBACK;
