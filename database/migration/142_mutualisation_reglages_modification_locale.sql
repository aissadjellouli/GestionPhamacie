-- =====================================================================
-- 142 — Mutualisation des réglages : « jamais modifiée sur place depuis l'installation »
-- (2026-11-17, décision du titulaire, point D-M8-2 de l'essai M8 ; docs/conception-synchronisation.md § 16 bis.12).
--   À la PREMIÈRE réception d'un modèle « proposé », la valeur du central est appliquée toute seule si la table n'a
--   jamais été modifiée sur place ; sinon la valeur locale est gardée et le central se propose (« Adopter »).
--   Jusqu'ici la garde zz_reglage_garde ne notait une modification locale que si la table avait un mode (propose, impose)
--   ou une valeur venue du central : tant qu'aucun modèle n'était reçu, une table modifiée et une table jamais touchée
--   étaient indiscernables (origine « locale » dans les deux cas).
--   1. reglage_garde() : toute modification locale d'une des 11 tables est notée (origine « locale », date), même sans
--      modèle reçu et en mode « local ». Une écriture qui ne change aucune valeur de réglage n'est pas une modification
--      (colonnes modifie_le / modifie_par ignorées ; horaires d'ouverture de parametre_surveillance ignorés : ils restent
--      locaux, D7, et ne font pas partie du modèle). Les valeurs d'installation ne comptent pas : les scripts, l'init et
--      le rejeu écrivent avec le marqueur pharma.reglage_central = 'on', qui n'est jamais noté.
--   2. Reprise de l'existant : pour une table jamais reçue du central et sans modification notée, la date de modification
--      locale est reprise du journal d'audit (dernière écriture du cas d'usage de saisie, avant ou après le script 141).
--      Limite : une modification faite sans passer par l'application (SQL direct) n'a pas de trace et n'est pas reprise.
-- Idempotent et rejouable sur une base plus récente ; aucune valeur de réglage modifiée ; synchronisation inchangée.
-- A exécuter après 141_mutualisation_reglages.sql.
-- =====================================================================

-- ---- 1. Garde : modification locale toujours notée ----
CREATE OR REPLACE FUNCTION reglage_garde() RETURNS trigger
LANGUAGE plpgsql AS $$
DECLARE
    v_mode      text;
    v_autre     boolean := false;
    v_refus     text := 'Réglage fixé par le serveur central : ' || TG_TABLE_NAME || ' ne peut pas être modifié dans l''officine.';
    v_ignorees  text[] := ARRAY['modifie_le', 'modifie_par', 'modifiee_le', 'modifiee_par'];
BEGIN
    -- Application d'un modèle, scripts, init, rejeu (marqueur posé par SET LOCAL ou PGOPTIONS) : tout passe, rien n'est noté
    IF coalesce(current_setting('pharma.reglage_central', true), '') = 'on' THEN
        IF TG_OP = 'DELETE' THEN RETURN OLD; END IF;
        RETURN NEW;
    END IF;

    -- Le repérage « venu du modèle » n'est jamais posé ni changé hors application d'un modèle
    IF TG_TABLE_NAME = 'motif_reglable' AND TG_OP <> 'DELETE' THEN
        IF TG_OP = 'INSERT' THEN v_autre := NEW.modele_code_central; ELSE v_autre := NEW.modele_code_central IS DISTINCT FROM OLD.modele_code_central; END IF;
        IF v_autre THEN
            RAISE EXCEPTION 'Réglage fixé par le serveur central : le repérage des motifs du serveur central ne se modifie pas dans l''officine.';
        END IF;
    ELSIF TG_TABLE_NAME = 'remise_automatique' AND TG_OP <> 'DELETE' THEN
        IF TG_OP = 'INSERT' THEN v_autre := NEW.modele_cle IS NOT NULL; ELSE v_autre := NEW.modele_cle IS DISTINCT FROM OLD.modele_cle; END IF;
        IF v_autre THEN
            RAISE EXCEPTION 'Réglage fixé par le serveur central : le repérage des remises du serveur central ne se modifie pas dans l''officine.';
        END IF;
    END IF;
    v_autre := false;

    v_mode := reglage_mode_effectif(TG_TABLE_NAME);
    IF v_mode = 'impose' THEN
        -- Écriture sans effet : permise
        IF TG_OP = 'UPDATE' AND NEW IS NOT DISTINCT FROM OLD THEN RETURN NEW; END IF;
        IF TG_TABLE_NAME = 'parametre_surveillance' THEN
            -- D7 : les horaires d'ouverture restent locaux ; toute autre colonne est fixée
            IF TG_OP = 'UPDATE' AND (to_jsonb(NEW) - ARRAY['heure_ouverture', 'heure_fermeture'])
                                  = (to_jsonb(OLD) - ARRAY['heure_ouverture', 'heure_fermeture']) THEN
                v_autre := true;
            END IF;
        ELSIF TG_TABLE_NAME = 'motif_reglable' THEN
            -- D5 : motifs du central verrouillés ; ajout local permis si le modèle l'accorde (ajouts_locaux)
            IF TG_OP = 'INSERT' THEN
                v_autre := coalesce((SELECT r.ajouts_locaux FROM reglage_mutualise r WHERE r.nom_table = 'motif_reglable'), true);
                v_refus := 'Réglage fixé par le serveur central : l''ajout de motifs n''est pas permis dans l''officine.';
            ELSE
                v_autre := NOT OLD.modele_code_central;
            END IF;
        ELSIF TG_TABLE_NAME = 'remise_automatique' THEN
            -- D6 : aucune création locale ; une remise locale existante reste modifiable (arrêt) ; une remise du central est verrouillée
            IF TG_OP <> 'INSERT' THEN v_autre := OLD.modele_cle IS NULL; END IF;
        END IF;
        IF NOT v_autre THEN RAISE EXCEPTION '%', v_refus; END IF;
    END IF;

    -- Modification locale permise : notée (origine locale, date), dans la même transaction ; la ligne est verrouillée jusqu'à la fin,
    -- ce qui sérialise avec l'application d'un modèle (SELECT … FOR UPDATE). Notée même sans modèle reçu et en mode « local » :
    -- c'est ce qui distingue « jamais modifiée depuis l'installation » de « modifiée sur place » (script 142).
    -- Une mise à jour qui ne change aucune valeur de réglage n'est pas une modification.
    IF TG_OP = 'UPDATE' THEN
        IF TG_TABLE_NAME = 'parametre_surveillance' THEN v_ignorees := v_ignorees || ARRAY['heure_ouverture', 'heure_fermeture']; END IF;
        IF (to_jsonb(NEW) - v_ignorees) IS NOT DISTINCT FROM (to_jsonb(OLD) - v_ignorees) THEN RETURN NEW; END IF;
    END IF;
    UPDATE reglage_mutualise SET origine = 'locale', modifie_localement_le = now() WHERE nom_table = TG_TABLE_NAME;
    IF TG_OP = 'DELETE' THEN RETURN OLD; END IF;
    RETURN NEW;
END $$;

COMMENT ON FUNCTION reglage_garde() IS 'Refus d''écriture sur une table imposée par le serveur central, sauf pendant l''application d''un modèle (pharma.reglage_central = on) ; toute modification locale permise est notée dans reglage_mutualise (scripts 141 et 142).';

-- Le déclencheur zz_reglage_garde (141) appelle la fonction par son nom : il n'est pas à refaire

-- ---- 2. Reprise de l'existant : modifications locales déjà faites, relevées au journal d'audit ----
-- Seulement les tables jamais reçues du central, d'origine locale et sans modification notée (rejouable : une fois la date posée, plus rien à faire)
UPDATE reglage_mutualise r
SET modifie_localement_le = j.derniere
FROM (
    SELECT m.nom_table, max(a.cree_le) AS derniere
    FROM (VALUES
        ('parametre_fiscal',         'parametre_fiscal'),
        ('parametre_tracabilite',    'parametre_tracabilite'),
        ('parametre_alerte',         'seuils_alerte'),
        ('parametre_substitution',   'parametre_substitution'),
        ('parametre_formulaire_sms', 'formulaire_sms_texte_modifie'),
        ('regle_alerte_psychotrope', 'reglages_alertes_psychotropes'),
        ('motif_reglable',           'motif_reglable'),
        ('parametre_inventaire',     'seuils_inventaire'),
        ('parametre_surveillance',   'reglages_surveillance'),
        ('parametre_serialisation',  'serialisation_parametres'),
        ('remise_automatique',       'remise_automatique_creee'),
        ('remise_automatique',       'remise_automatique_modifiee'),
        ('remise_automatique',       'remise_automatique_arretee')
    ) AS m (nom_table, action)
    JOIN journal_audit a ON a.action = m.action
    -- Un changement des seuls horaires d'ouverture n'est pas une modification du réglage (D7)
    WHERE NOT (m.nom_table = 'parametre_surveillance' AND jsonb_typeof(a.details) = 'object'
               AND a.details ? 'horaires' AND (a.details - 'horaires') = '{}'::jsonb)
    GROUP BY m.nom_table
) j
WHERE r.nom_table = j.nom_table
  AND r.mode_recu IS NULL AND r.version_modele_recue IS NULL
  AND r.origine = 'locale' AND r.modifie_localement_le IS NULL;
