-- =====================================================================
-- 141 — Mutualisation des réglages : socle de l'officine (2026-11-16, lot M2,
-- docs/conception-synchronisation.md § 16 bis ; décisions D1 à D14 acceptées).
--   Rien n'est imposé et rien n'est publié par ce script : les 11 tables
--   sont « locales » (aucun modèle reçu), le comportement de l'officine est
--   inchangé. Sans serveur central (synchronisation désactivée ou officine non
--   inscrite), le mode effectif reste « local » pour toutes les tables.
--   1. reglage_mutualise (classée MONTANTE, colonne modele non envoyée) : une
--      ligne par table mutualisable (11), mode en vigueur (local, propose,
--      impose), mode et version du modèle reçus, valeur du modèle reçue, état
--      (aucun, applique, non_adopte, rejete), motif de rejet, origine de la
--      valeur (locale, central), date de la dernière modification locale.
--   2. reglage_tables_mutualisables(), reglage_mode_effectif(table) : liste
--      fermée des 11 tables ; mode effectif = mode en vigueur si la
--      synchronisation est active ET l'officine inscrite, sinon « local ».
--   3. Garde zz_reglage_garde sur les 11 tables : refus d'écriture (P0001) sur
--      une table imposée, sauf pendant l'application d'un modèle (marqueur de
--      session pharma.reglage_central = 'on', posé par SET LOCAL). Cas
--      particuliers : horaires de parametre_surveillance toujours locaux (D7) ;
--      motifs : ajouts locaux permis selon ajouts_locaux, motifs du central
--      verrouillés (D5) ; remises : refus sur une remise du central et sur toute
--      création locale en mode imposé (D6). Hors mode imposé : la modification
--      locale est notée (origine locale, date) dans reglage_mutualise.
--   4. reglage_journaliser(...) : journal d'audit avant / après (actions
--      reglage_central_applique, _remplace, _rejete, _adopte, _libere).
--   5. motif_reglable.modele_code_central ; remise_automatique.modele_cle
--      (mutualisation par catégorie seulement, D6) ; cree_par facultatif
--      seulement pour une remise du central.
--   6. Voie descendante : sync_recevoir_descendant reconnaît les éléments
--      « mutu_<table> » (format mutu-v1) : rangés dans reglage_central et dans
--      reglage_mutualise ; un élément mutu_ d'une table non mutualisable est
--      journalisé (statut « invalide ») et ACQUITTÉ sans bloquer les suivants ;
--      un élément d'une table connue mais mal formé est acquitté et marque la
--      table « rejete ». Les éléments hors mutu_ se comportent comme avant (136),
--      sauf une clé inutilisable, refusée (statut « refuse ») au lieu de lever une
--      erreur qui annulerait tout le passage.
--   7. Sujet de notification « reglages_centraux » (D4).
-- Idempotent et rejouable sur une base plus récente (gardes sur le catalogue) ;
-- aucune valeur de réglage modifiée ; synchronisation inchangée.
-- Règle pour les scripts futurs qui écrivent dans l'une des 11 tables : poser
-- SET LOCAL pharma.reglage_central = 'on' (ou PGOPTIONS="-c pharma.reglage_central=on").
-- A exécuter après 140_synchronisation_supervision_purge.sql.
-- =====================================================================

-- ---- 2a. Liste fermée des 11 tables mutualisables ----
CREATE OR REPLACE FUNCTION reglage_tables_mutualisables() RETURNS text[]
LANGUAGE sql IMMUTABLE AS $$
    SELECT ARRAY['motif_reglable', 'parametre_alerte', 'parametre_fiscal', 'parametre_formulaire_sms', 'parametre_inventaire',
                 'parametre_serialisation', 'parametre_substitution', 'parametre_surveillance', 'parametre_tracabilite',
                 'regle_alerte_psychotrope', 'remise_automatique']::text[]
$$;

-- ---- 1. État de la mutualisation, par table ----
CREATE TABLE IF NOT EXISTS reglage_mutualise (
    nom_table                text        PRIMARY KEY CHECK (nom_table = ANY (reglage_tables_mutualisables())),
    mode                     text        NOT NULL DEFAULT 'local' CHECK (mode IN ('local', 'propose', 'impose')),
    ajouts_locaux            boolean     NOT NULL DEFAULT true,
    mode_recu                text        CHECK (mode_recu IS NULL OR mode_recu IN ('local', 'propose', 'impose')),
    version_modele_recue     bigint      CHECK (version_modele_recue IS NULL OR version_modele_recue >= 1),
    recu_le                  timestamptz,
    modele                   jsonb       CHECK (modele IS NULL OR jsonb_typeof(modele) = 'object'),
    version_modele_appliquee bigint      CHECK (version_modele_appliquee IS NULL OR version_modele_appliquee >= 1),
    applique_le              timestamptz,
    etat                     text        NOT NULL DEFAULT 'aucun' CHECK (etat IN ('aucun', 'applique', 'non_adopte', 'rejete')),
    motif_rejet              text        CHECK (motif_rejet IS NULL OR length(motif_rejet) <= 500),
    version_modele_rejetee   bigint      CHECK (version_modele_rejetee IS NULL OR version_modele_rejetee >= 1),
    origine                  text        NOT NULL DEFAULT 'locale' CHECK (origine IN ('locale', 'central')),
    modifie_localement_le    timestamptz,
    officine_id              uuid        NOT NULL DEFAULT officine_courante()
);

-- Classement de synchronisation : état MONTANT (le central connaît l'état de chaque officine) ; la valeur du modèle reçue
-- (modele) ne repart pas ; le motif de rejet est un texte libre filtré comme les autres
INSERT INTO sync_table (nom_table, sens, colonnes_exclues, colonnes_patient, porteur, motif) VALUES
    ('reglage_mutualise', 'montante', '{modele}', '{}', false, 'État de la mutualisation des réglages (mode, version appliquée, rejet), script 141')
ON CONFLICT (nom_table) DO NOTHING;
UPDATE sync_table SET colonnes_exclues = '{modele}', cle_colonnes = '{nom_table}', colonnes_texte = '{motif_rejet}'
WHERE nom_table = 'reglage_mutualise'
  AND (colonnes_exclues, cle_colonnes, colonnes_texte) IS DISTINCT FROM ('{modele}'::text[], '{nom_table}'::text[], '{motif_rejet}'::text[]);

-- Droits de pharma_app (propriétaire en développement ; après la séparation des rôles, les droits par défaut de 111b s'appliquent)
DO $droits$
BEGIN
    IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'pharma_app')
       AND NOT has_table_privilege('pharma_app', 'public.reglage_mutualise', 'SELECT, INSERT, UPDATE, DELETE') THEN
        GRANT SELECT, INSERT, UPDATE, DELETE ON reglage_mutualise TO pharma_app;
    END IF;
END $droits$;

-- ---- 5. Colonnes de repérage des valeurs venues du modèle central ----
ALTER TABLE motif_reglable ADD COLUMN IF NOT EXISTS modele_code_central boolean NOT NULL DEFAULT false;

ALTER TABLE remise_automatique ADD COLUMN IF NOT EXISTS modele_cle text;
-- cree_par : obligatoire pour une remise locale, facultatif pour une remise du central (aucun auteur local)
ALTER TABLE remise_automatique ALTER COLUMN cree_par DROP NOT NULL;
DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'ck_remise_auto_createur') THEN
        ALTER TABLE remise_automatique ADD CONSTRAINT ck_remise_auto_createur CHECK (cree_par IS NOT NULL OR modele_cle IS NOT NULL);
    END IF;
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'ck_remise_auto_modele') THEN
        -- D6 : une remise du central ne vise qu'une catégorie ; clé stable du central « c_ » + 12 caractères
        ALTER TABLE remise_automatique ADD CONSTRAINT ck_remise_auto_modele CHECK (modele_cle IS NULL OR (categorie IS NOT NULL AND modele_cle ~ '^c_[A-Za-z0-9]{12}$'));
    END IF;
END $$;
CREATE UNIQUE INDEX IF NOT EXISTS uq_remise_auto_modele_cle ON remise_automatique (modele_cle) WHERE modele_cle IS NOT NULL;

-- ---- 2b. Mode effectif ----
-- Mode en vigueur si la synchronisation est active et l'officine inscrite, sinon « local » (libération sans rien effacer, D3)
CREATE OR REPLACE FUNCTION reglage_mode_effectif(p_table text) RETURNS text
LANGUAGE sql STABLE AS $$
    SELECT CASE WHEN EXISTS (SELECT 1 FROM sync_parametre p WHERE p.id = 1 AND p.active AND p.inscrite_le IS NOT NULL)
                THEN coalesce((SELECT r.mode FROM reglage_mutualise r WHERE r.nom_table = p_table), 'local')
                ELSE 'local' END
$$;

-- ---- 3. Garde des 11 tables ----
CREATE OR REPLACE FUNCTION reglage_garde() RETURNS trigger
LANGUAGE plpgsql AS $$
DECLARE
    v_mode   text;
    v_autre  boolean := false;
    v_refus  text := 'Réglage fixé par le serveur central : ' || TG_TABLE_NAME || ' ne peut pas être modifié dans l''officine.';
BEGIN
    -- Application d'un modèle (poste le marqueur par SET LOCAL dans sa transaction) : tout passe
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

    -- Modification locale permise : notée (origine locale), dans la même transaction ; la ligne est verrouillée jusqu'à la fin,
    -- ce qui sérialise avec l'application d'un modèle (SELECT … FOR UPDATE). Sans serveur central et sans modèle : rien à noter.
    UPDATE reglage_mutualise SET origine = 'locale', modifie_localement_le = now()
    WHERE nom_table = TG_TABLE_NAME AND (mode <> 'local' OR origine = 'central');
    IF TG_OP = 'DELETE' THEN RETURN OLD; END IF;
    RETURN NEW;
END $$;

DO $$
DECLARE t text;
BEGIN
    FOREACH t IN ARRAY reglage_tables_mutualisables() LOOP
        EXECUTE format('DROP TRIGGER IF EXISTS zz_reglage_garde ON public.%I', t);
        EXECUTE format('CREATE TRIGGER zz_reglage_garde BEFORE INSERT OR UPDATE OR DELETE ON public.%I '
                       'FOR EACH ROW EXECUTE FUNCTION reglage_garde()', t);
    END LOOP;
END $$;

-- Si la synchronisation est active, la nouvelle table reçoit son déclencheur d'envoi (sans effet sinon)
SELECT sync_installer_declencheurs();

-- Une ligne par table, en mode « local », aucun modèle : le comportement de l'officine ne change pas (D12)
INSERT INTO reglage_mutualise (nom_table)
SELECT t FROM unnest(reglage_tables_mutualisables()) AS t
ON CONFLICT (nom_table) DO NOTHING;

-- ---- 4. Journal avant / après ----
-- Une ligne du journal d'audit (sans employé : p_utilisateur vide pour une action du serveur central). Les valeurs avant et après
-- sont des objets JSON choisis par l'appelant (pas de donnée personnelle).
CREATE OR REPLACE FUNCTION reglage_journaliser(p_action text, p_table text, p_mode_avant text, p_mode_apres text, p_version_modele bigint,
                                               p_auteur text, p_valeur_avant jsonb, p_valeur_apres jsonb, p_utilisateur bigint DEFAULT NULL)
RETURNS bigint LANGUAGE plpgsql AS $$
DECLARE v_id bigint;
BEGIN
    IF p_action IS NULL OR p_action NOT IN ('reglage_central_applique', 'reglage_central_remplace', 'reglage_central_rejete',
                                            'reglage_central_adopte', 'reglage_central_libere') THEN
        RAISE EXCEPTION 'Mutualisation : action de journal inconnue (%)', p_action;
    END IF;
    IF p_table IS NULL OR NOT p_table = ANY (reglage_tables_mutualisables()) THEN
        RAISE EXCEPTION 'Mutualisation : table non mutualisable (%)', p_table;
    END IF;
    INSERT INTO journal_audit (utilisateur_id, action, table_cible, details)
    VALUES (p_utilisateur, p_action, p_table,
            jsonb_build_object('table', p_table,
                               'mode', jsonb_build_object('avant', p_mode_avant, 'apres', p_mode_apres),
                               'version_modele', p_version_modele,
                               'auteur_central', left(p_auteur, 100),
                               'valeur', jsonb_build_object('avant', p_valeur_avant, 'apres', p_valeur_apres)))
    RETURNING id INTO v_id;
    RETURN v_id;
END $$;

-- ---- 6. Voie descendante : éléments « mutu_<table> » ----
DO $garde$
BEGIN
    -- Statut « invalide » du journal local (élément mutu_ d'une table non mutualisable : ignoré et acquitté)
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'sync_journal_descendant_statut_check' AND pg_get_constraintdef(oid) LIKE '%''invalide''%') THEN
        ALTER TABLE sync_journal_descendant DROP CONSTRAINT IF EXISTS sync_journal_descendant_statut_check;
        ALTER TABLE sync_journal_descendant ADD CONSTRAINT sync_journal_descendant_statut_check
            CHECK (statut IN ('applique', 'table_inconnue', 'refuse', 'invalide'));
    END IF;
END $garde$;

-- Remplace la version de 136 (reprise telle quelle pour tout ce qui n'est pas « mutu_ »). Statuts : 'applique', 'ancien',
-- 'table_inconnue', 'refuse' (bloquent l'acquittement) et 'invalide' (acquitté : n'arrête pas les éléments suivants).
CREATE OR REPLACE FUNCTION sync_recevoir_descendant(p_table text, p_cle text, p_version bigint, p_donnees jsonb, p_version_max bigint)
RETURNS text LANGUAGE plpgsql AS $$
DECLARE
    p        sync_parametre;
    v_avant  jsonb;
    n        int;
    v_table  text;
    v_mode   text;
    v_motif  text;
    v_modele bigint;
BEGIN
    SELECT * INTO p FROM sync_parametre WHERE id = 1;
    IF p_version IS NULL OR p_version < 1 OR p_donnees IS NULL OR jsonb_typeof(p_donnees) <> 'object'
       OR p_version > coalesce(p_version_max, 0) OR p_version > p.descendant_version + p.descendant_saut_max
       OR length(p_donnees::text) > 65536 THEN
        -- Une ligne par élément refusé (renvoyé à chaque passage tant qu'il n'est pas acquitté : pas de doublon)
        INSERT INTO sync_journal_descendant (nom_table, cle, version, statut, motif)
        SELECT left(coalesce(p_table, '?'), 63), left(coalesce(p_cle, '?'), 200), p_version, 'refuse',
               CASE WHEN p_version > coalesce(p_version_max, 0) THEN 'version au-delà de la version maximale annoncée'
                    WHEN p_version > p.descendant_version + p.descendant_saut_max THEN 'saut de version anormal'
                    WHEN length(p_donnees::text) > 65536 THEN 'données trop volumineuses (plus de 64 Kio)'
                    ELSE 'élément mal formé' END
        WHERE NOT EXISTS (SELECT 1 FROM sync_journal_descendant j WHERE j.statut = 'refuse' AND j.nom_table = left(coalesce(p_table, '?'), 63)
                          AND j.cle = left(coalesce(p_cle, '?'), 200) AND j.version IS NOT DISTINCT FROM p_version);
        RETURN 'refuse';
    END IF;
    IF p_table IS DISTINCT FROM 'reglage_central' THEN
        INSERT INTO sync_journal_descendant (nom_table, cle, version, statut, motif)
        SELECT left(coalesce(p_table, '?'), 63), left(coalesce(p_cle, '?'), 200), p_version, 'table_inconnue', 'table descendante non prévue ici : ignorée'
        WHERE NOT EXISTS (SELECT 1 FROM sync_journal_descendant j WHERE j.statut = 'table_inconnue' AND j.nom_table = left(coalesce(p_table, '?'), 63)
                          AND j.cle = left(coalesce(p_cle, '?'), 200) AND j.version IS NOT DISTINCT FROM p_version);
        RETURN 'table_inconnue';
    END IF;
    IF p_cle IS NULL OR p_cle !~ '^[a-z_]{1,63}$' THEN
        -- Clé inutilisable (la table reglage_central la refuserait) : même traitement qu'un élément mal formé
        INSERT INTO sync_journal_descendant (nom_table, cle, version, statut, motif)
        SELECT left(p_table, 63), left(coalesce(p_cle, '?'), 200), p_version, 'refuse', 'élément mal formé'
        WHERE NOT EXISTS (SELECT 1 FROM sync_journal_descendant j WHERE j.statut = 'refuse' AND j.nom_table = left(p_table, 63)
                          AND j.cle = left(coalesce(p_cle, '?'), 200) AND j.version IS NOT DISTINCT FROM p_version);
        RETURN 'refuse';
    END IF;
    IF p_cle LIKE 'mutu\_%' THEN
        v_table := substr(p_cle, 6);
        IF NOT v_table = ANY (reglage_tables_mutualisables()) THEN
            -- Élément de mutualisation d'une table qui n'est pas mutualisable : journalisé, ignoré, ACQUITTÉ (ne bloque pas les suivants)
            INSERT INTO sync_journal_descendant (nom_table, cle, version, statut, motif)
            SELECT p_table, left(p_cle, 200), p_version, 'invalide', 'table non mutualisable : élément ignoré'
            WHERE NOT EXISTS (SELECT 1 FROM sync_journal_descendant j WHERE j.statut = 'invalide' AND j.nom_table = p_table
                              AND j.cle = left(p_cle, 200) AND j.version IS NOT DISTINCT FROM p_version);
            RETURN 'invalide';
        END IF;
    END IF;
    SELECT valeur INTO v_avant FROM reglage_central WHERE cle = p_cle;
    INSERT INTO reglage_central AS r (cle, version, valeur) VALUES (p_cle, p_version, p_donnees)
    ON CONFLICT (cle) DO UPDATE SET version = EXCLUDED.version, valeur = EXCLUDED.valeur, recu_le = now()
    WHERE r.version < EXCLUDED.version;
    GET DIAGNOSTICS n = ROW_COUNT;
    IF n = 0 THEN RETURN 'ancien'; END IF;

    IF v_table IS NOT NULL THEN
        -- État de la table : modèle reçu rangé (appliqué ensuite par l'application) ; élément mal formé : table « rejetée », rien d'autre ne change
        v_mode := p_donnees ->> 'mode';
        v_motif := CASE
            WHEN p_donnees ->> 'format' IS DISTINCT FROM 'mutu-v1' THEN 'format non pris en charge'
            WHEN v_mode IS NULL OR v_mode NOT IN ('local', 'propose', 'impose') THEN 'mode inconnu'
            WHEN jsonb_typeof(p_donnees -> 'version_modele') IS DISTINCT FROM 'number' OR (p_donnees ->> 'version_modele') !~ '^[0-9]{1,15}$'
                 THEN 'version du modèle absente ou invalide'
            WHEN (p_donnees ->> 'version_modele')::bigint < 1 THEN 'version du modèle absente ou invalide'
            WHEN v_mode <> 'local' AND jsonb_typeof(p_donnees -> 'valeur') IS DISTINCT FROM 'object' THEN 'valeur du modèle absente'
            WHEN p_donnees ? 'ajouts_locaux' AND jsonb_typeof(p_donnees -> 'ajouts_locaux') IS DISTINCT FROM 'boolean' THEN 'indicateur des ajouts locaux invalide'
            ELSE NULL END;
        v_modele := CASE WHEN jsonb_typeof(p_donnees -> 'version_modele') = 'number' AND (p_donnees ->> 'version_modele') ~ '^[0-9]{1,15}$'
                              AND (p_donnees ->> 'version_modele')::bigint >= 1
                         THEN (p_donnees ->> 'version_modele')::bigint END;
        INSERT INTO reglage_mutualise (nom_table) VALUES (v_table) ON CONFLICT (nom_table) DO NOTHING;
        IF v_motif IS NULL THEN
            UPDATE reglage_mutualise SET mode_recu = v_mode, version_modele_recue = v_modele, recu_le = now(), modele = p_donnees
            WHERE nom_table = v_table;
        ELSE
            UPDATE reglage_mutualise SET etat = 'rejete', motif_rejet = left('Élément du serveur central mal formé : ' || v_motif, 500),
                                         version_modele_rejetee = v_modele
            WHERE nom_table = v_table;
        END IF;
    END IF;

    INSERT INTO sync_journal_descendant (nom_table, cle, version, statut, avant, apres)
    VALUES (p_table, p_cle, p_version, 'applique',
            CASE WHEN p_cle = 'familles_envoyees' THEN v_avant -> 'familles'
                 WHEN v_table IS NOT NULL AND v_avant IS NOT NULL
                      THEN jsonb_build_object('version_modele', v_avant -> 'version_modele', 'mode', v_avant -> 'mode') END,
            CASE WHEN p_cle = 'familles_envoyees' THEN p_donnees -> 'familles'
                 WHEN v_table IS NOT NULL
                      THEN jsonb_build_object('version_modele', p_donnees -> 'version_modele', 'mode', p_donnees -> 'mode') END);
    RETURN 'applique';
END $$;

-- ---- 7. Sujet de notification « reglages_centraux » ----
-- Rejouable : la liste n'est refaite que si elle n'a pas encore le sujet ; l'abonnement n'est posé qu'à ce moment-là
DO $garde$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'destinataire_notification_types_check' AND pg_get_constraintdef(oid) LIKE '%''reglages_centraux''%') THEN
        ALTER TABLE destinataire_notification DROP CONSTRAINT IF EXISTS destinataire_notification_types_check;
        ALTER TABLE destinataire_notification ADD CONSTRAINT destinataire_notification_types_check
            CHECK (types <@ ARRAY['rapport_quotidien', 'ecart_caisse', 'lot_bloque', 'sauvegarde', 'ecart_inventaire', 'lot_delaisse', 'surveillance', 'synchronisation', 'reglages_centraux']);
        UPDATE destinataire_notification SET types = types || 'reglages_centraux'::text
        WHERE 'synchronisation' = ANY (types) AND NOT 'reglages_centraux' = ANY (types);
    END IF;
END $garde$;

DO $garde$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'message_sortant_type_check' AND pg_get_constraintdef(oid) LIKE '%''reglages_centraux''%') THEN
        ALTER TABLE message_sortant DROP CONSTRAINT IF EXISTS message_sortant_type_check;
        ALTER TABLE message_sortant ADD CONSTRAINT message_sortant_type_check
            CHECK (type = ANY (ARRAY['rapport_quotidien', 'ecart_caisse', 'lot_bloque', 'sauvegarde', 'ecart_inventaire', 'lot_delaisse', 'surveillance', 'synchronisation', 'reglages_centraux', 'test']));
    END IF;
END $garde$;

COMMENT ON TABLE reglage_mutualise IS 'Mutualisation des réglages : état de chacune des 11 tables (mode en vigueur, modèle reçu, version appliquée, rejet, origine de la valeur) ; montante, la valeur du modèle ne repart pas (script 141).';
COMMENT ON FUNCTION reglage_mode_effectif(text) IS 'Mode en vigueur d''une table mutualisable ; « local » tant que la synchronisation est désactivée ou l''officine non inscrite (script 141).';
COMMENT ON FUNCTION reglage_garde() IS 'Refus d''écriture sur une table imposée par le serveur central, sauf pendant l''application d''un modèle (pharma.reglage_central = on) (script 141).';
COMMENT ON COLUMN motif_reglable.modele_code_central IS 'Vrai pour un motif créé ou réglé par le modèle du serveur central : verrouillé en mode imposé (script 141).';
COMMENT ON COLUMN remise_automatique.modele_cle IS 'Clé stable (c_ + 12 caractères) d''une remise du serveur central, par catégorie ; NULL pour une remise locale (script 141).';
