-- =====================================================================
-- Tests du script 142 (mutualisation des réglages : « jamais modifiée sur place depuis l'installation »), en transaction ANNULÉE.
-- Le test rejoue le script 142 (ir, chemin relatif à ce fichier) :
--   psql -U pharma_app -d pharmacie_test -v ON_ERROR_STOP=1 -f database/tests/142_mutualisation_reglages_modification_locale_tests.sql
-- =====================================================================
BEGIN;

-- État d'installation simulé : aucune table reçue du central, rien de modifié
UPDATE reglage_mutualise SET mode = 'local', ajouts_locaux = true, mode_recu = NULL, version_modele_recue = NULL, recu_le = NULL, modele = NULL,
       version_modele_appliquee = NULL, applique_le = NULL, etat = 'aucun', motif_rejet = NULL, version_modele_rejetee = NULL,
       origine = 'locale', modifie_localement_le = NULL;

-- 1. Une écriture sans changement de valeur, ou qui ne touche que modifie_le / modifie_par, n'est pas une modification
DO $$
BEGIN
    UPDATE parametre_fiscal SET conservation_annees = conservation_annees;
    UPDATE parametre_substitution SET modifie_le = now() + interval '1 second', modifie_par = NULL;
    UPDATE regle_alerte_psychotrope SET modifie_le = now() + interval '1 second';
    UPDATE parametre_surveillance SET heure_ouverture = heure_ouverture, heure_fermeture = heure_fermeture;
    ASSERT NOT EXISTS (SELECT 1 FROM reglage_mutualise WHERE modifie_localement_le IS NOT NULL), 'aucune modification réelle : rien de noté';
END $$;

-- 2. Les horaires d'ouverture restent locaux (D7) : ne comptent pas ; un seuil du modèle compte
DO $$
BEGIN
    UPDATE parametre_surveillance SET heure_ouverture = heure_ouverture + interval '1 hour';
    ASSERT (SELECT modifie_localement_le IS NULL FROM reglage_mutualise WHERE nom_table = 'parametre_surveillance'), 'horaires seuls : pas une modification';
    UPDATE parametre_surveillance SET seuil_tiroir = seuil_tiroir + 1;
    ASSERT (SELECT origine = 'locale' AND modifie_localement_le IS NOT NULL FROM reglage_mutualise WHERE nom_table = 'parametre_surveillance'), 'seuil modifié : noté';
END $$;

-- 3. Modification réelle sans modèle reçu, mode « local » : notée (une ligne par table touchée, les autres intactes)
DO $$
BEGIN
    UPDATE parametre_inventaire SET seuil_boites = seuil_boites + 1;
    ASSERT (SELECT origine = 'locale' AND modifie_localement_le IS NOT NULL FROM reglage_mutualise WHERE nom_table = 'parametre_inventaire'), 'inventaire : noté';
    UPDATE parametre_tracabilite SET boite_obligatoire = NOT boite_obligatoire;
    ASSERT (SELECT modifie_localement_le IS NOT NULL FROM reglage_mutualise WHERE nom_table = 'parametre_tracabilite'), 'traçabilité : noté';
    ASSERT (SELECT modifie_localement_le IS NULL FROM reglage_mutualise WHERE nom_table = 'parametre_fiscal'), 'table non touchée : intacte';
    -- Un ajout compte aussi
    INSERT INTO motif_reglable (liste, code, libelle) VALUES ('sortie_stock', 'essai_local_142', 'Essai local 142');
    ASSERT (SELECT modifie_localement_le IS NOT NULL FROM reglage_mutualise WHERE nom_table = 'motif_reglable'), 'ajout de motif : noté';
END $$;

-- 4. Valeurs d'installation : écrites avec le marqueur (scripts, init, rejeu), jamais notées
DO $$
BEGIN
    PERFORM set_config('pharma.reglage_central', 'on', true);
    UPDATE parametre_fiscal SET conservation_annees = conservation_annees + 1;
    PERFORM set_config('pharma.reglage_central', 'off', true);
    ASSERT (SELECT modifie_localement_le IS NULL FROM reglage_mutualise WHERE nom_table = 'parametre_fiscal'), 'marqueur : valeur d''installation non notée';
END $$;

-- 5. Reprise de l'existant par le journal d'audit (modifications faites avant le script 141 ou sans être notées), en rejouant le script
UPDATE reglage_mutualise SET origine = 'locale', modifie_localement_le = NULL;
INSERT INTO journal_audit (utilisateur_id, action, table_cible, details, cree_le) VALUES
    (NULL, 'seuils_alerte', 'parametre_alerte', '{}'::jsonb, '2090-11-10 09:00+00'),
    (NULL, 'seuils_alerte', 'parametre_alerte', '{}'::jsonb, '2090-11-12 09:00+00'),
    (NULL, 'remise_automatique_arretee', 'remise_automatique', '{}'::jsonb, '2090-11-13 09:00+00'),
    (NULL, 'reglages_surveillance', 'parametre_surveillance', '{"horaires": {"avant": "08:00", "apres": "09:00"}}'::jsonb, '2090-11-14 09:00+00'),
    (NULL, 'serialisation_parametres', 'parametre_serialisation', '{}'::jsonb, '2090-11-15 09:00+00');
-- Table déjà reçue du central : jamais reprise
UPDATE reglage_mutualise SET mode_recu = 'propose', version_modele_recue = 1 WHERE nom_table = 'parametre_serialisation';
\ir ../migration/142_mutualisation_reglages_modification_locale.sql
DO $$
BEGIN
    ASSERT (SELECT modifie_localement_le = '2090-11-12 09:00+00' FROM reglage_mutualise WHERE nom_table = 'parametre_alerte'), 'dernière écriture du journal reprise';
    ASSERT (SELECT modifie_localement_le = '2090-11-13 09:00+00' FROM reglage_mutualise WHERE nom_table = 'remise_automatique'), 'remises : actions créée / modifiée / arrêtée';
    ASSERT (SELECT modifie_localement_le IS NULL FROM reglage_mutualise WHERE nom_table = 'parametre_surveillance'), 'horaires seuls au journal : pas une modification';
    ASSERT (SELECT modifie_localement_le IS NULL FROM reglage_mutualise WHERE nom_table = 'parametre_serialisation'), 'table déjà reçue : non reprise';
    ASSERT (SELECT modifie_localement_le IS NULL FROM reglage_mutualise WHERE nom_table = 'parametre_fiscal'), 'sans trace au journal : jamais modifiée';
END $$;
-- Rejouable : une date déjà posée n'est pas écrasée
\ir ../migration/142_mutualisation_reglages_modification_locale.sql
DO $$
BEGIN
    ASSERT (SELECT modifie_localement_le = '2090-11-12 09:00+00' FROM reglage_mutualise WHERE nom_table = 'parametre_alerte'), 'rejeu : date inchangée';
END $$;

\echo 'Tests du script 142 : tous réussis (transaction annulée).'
ROLLBACK;
