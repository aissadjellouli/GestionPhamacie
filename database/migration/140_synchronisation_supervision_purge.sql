-- =====================================================================
-- 140 — Synchronisation, lot S3 : supervision (bandeau, alertes au titulaire)
-- et purge de la file d'envoi après accusé (2026-11-16, GO « go S3 »,
-- docs/conception-synchronisation.md § 8 et § 20).
--
--   1. Réglages (sync_parametre) : seuil du bandeau des postes (60 min),
--      seuil d'alerte de retard (24 h), seuil d'erreur persistante (60 min),
--      purge de la file (active, conservation des lots accusés : 30 jours).
--      Valeurs de départ proposées, réglables dans Administration ›
--      Synchronisation › Supervision et purge.
--   2. État de supervision : début et nature de l'erreur d'envoi en cours
--      (sync_noter_passage), dernière alerte envoyée par sujet
--      (sync_noter_alerte : au plus une par heure et par sujet).
--   3. Purge : sync_purger retire les lignes de sync_sortie des lots
--      ACCUSÉS depuis plus longtemps que le délai de conservation, par lots
--      entiers dans l'ordre (jamais de trou), jamais un lot non accusé ni un
--      lot qui contient des lignes d'un chargement complet en cours. Les
--      gardes de sync_sortie (126 : suppression seulement après accusé) et de
--      sync_lot (126, 130, 136 : lot figé, jamais supprimé) ne sont pas
--      touchées : la ligne du lot (numéro, empreinte, colonnes retirées
--      figées) reste. Le plus grand lot purgé est mémorisé
--      (purge_epoque, purge_dernier_lot) : l'application sait qu'un lot
--      accusé ne peut plus être renvoyé et demande alors un chargement complet
--      au lieu de renvoyer un lot incomplet.
--   4. Droit « sync.suivre » (voir le bandeau de synchronisation) : donné au
--      titulaire ; à donner aux rôles choisis dans la matrice des droits.
--   5. Nouveau sujet de notification « synchronisation » (alertes ci-dessus) ;
--      une seule fois, les destinataires déjà abonnés aux alertes de
--      sauvegarde (alertes techniques) y sont abonnés aussi.
-- Idempotent ; aucune donnée existante modifiée, hors l'abonnement et le droit
-- ci-dessus posés à la première exécution seulement.
-- A exécuter après 139_contenance_flacons_seringues_stylos.sql.
-- =====================================================================

-- ---- 1 et 2. Colonnes de sync_parametre ----
ALTER TABLE sync_parametre ADD COLUMN IF NOT EXISTS bandeau_minutes       int         NOT NULL DEFAULT 60;
ALTER TABLE sync_parametre ADD COLUMN IF NOT EXISTS alerte_retard_heures  int         NOT NULL DEFAULT 24;
ALTER TABLE sync_parametre ADD COLUMN IF NOT EXISTS alerte_erreur_minutes int         NOT NULL DEFAULT 60;
ALTER TABLE sync_parametre ADD COLUMN IF NOT EXISTS purge_active          boolean     NOT NULL DEFAULT true;
ALTER TABLE sync_parametre ADD COLUMN IF NOT EXISTS purge_jours           int         NOT NULL DEFAULT 30;
ALTER TABLE sync_parametre ADD COLUMN IF NOT EXISTS purge_epoque          int;
ALTER TABLE sync_parametre ADD COLUMN IF NOT EXISTS purge_dernier_lot     bigint      NOT NULL DEFAULT 0;
ALTER TABLE sync_parametre ADD COLUMN IF NOT EXISTS derniere_purge_le     timestamptz;
ALTER TABLE sync_parametre ADD COLUMN IF NOT EXISTS derniere_purge_lignes bigint      NOT NULL DEFAULT 0;
ALTER TABLE sync_parametre ADD COLUMN IF NOT EXISTS purge_lignes_total    bigint      NOT NULL DEFAULT 0;
ALTER TABLE sync_parametre ADD COLUMN IF NOT EXISTS derniere_purge_erreur text;
ALTER TABLE sync_parametre ADD COLUMN IF NOT EXISTS erreur_depuis_le      timestamptz;
ALTER TABLE sync_parametre ADD COLUMN IF NOT EXISTS erreur_nature         text;
ALTER TABLE sync_parametre ADD COLUMN IF NOT EXISTS alerte_retard_le      timestamptz;
ALTER TABLE sync_parametre ADD COLUMN IF NOT EXISTS alerte_erreur_le      timestamptz;
ALTER TABLE sync_parametre ADD COLUMN IF NOT EXISTS alerte_certificat_le  timestamptz;
ALTER TABLE sync_parametre ADD COLUMN IF NOT EXISTS alerte_quota_le       timestamptz;

DO $$ BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'sync_parametre_supervision_check') THEN
        ALTER TABLE sync_parametre ADD CONSTRAINT sync_parametre_supervision_check CHECK (
            bandeau_minutes BETWEEN 1 AND 10080 AND alerte_retard_heures BETWEEN 1 AND 720
            AND alerte_erreur_minutes BETWEEN 1 AND 10080 AND purge_jours BETWEEN 1 AND 365);
    END IF;
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'sync_parametre_purge_check') THEN
        ALTER TABLE sync_parametre ADD CONSTRAINT sync_parametre_purge_check CHECK (
            purge_dernier_lot >= 0 AND purge_dernier_lot <= dernier_accuse AND derniere_purge_lignes >= 0 AND purge_lignes_total >= 0);
    END IF;
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'sync_parametre_erreur_check') THEN
        ALTER TABLE sync_parametre ADD CONSTRAINT sync_parametre_erreur_check CHECK (
            (erreur_nature IS NULL) = (erreur_depuis_le IS NULL) AND (erreur_nature IS NULL OR erreur_nature IN ('autre', 'quota')));
    END IF;
END $$;

-- Résultat d'un passage d'envoi : NULL = sans erreur (efface), sinon nature de l'erreur ('autre' ou 'quota' : quota ou plafond
-- refusé par le serveur central). Le début de l'erreur est gardé tant qu'elle dure.
CREATE OR REPLACE FUNCTION sync_noter_passage(p_nature text) RETURNS void
LANGUAGE plpgsql AS $$
BEGIN
    IF p_nature IS NOT NULL AND p_nature NOT IN ('autre', 'quota') THEN
        RAISE EXCEPTION 'Synchronisation : nature d''erreur inconnue (%)', p_nature;
    END IF;
    UPDATE sync_parametre
    SET erreur_depuis_le = CASE WHEN p_nature IS NULL THEN NULL ELSE coalesce(erreur_depuis_le, now()) END,
        erreur_nature = p_nature
    WHERE id = 1;
END $$;

-- Dernière alerte envoyée au titulaire, par sujet (retard, erreur, certificat, quota) : sert à n'en envoyer qu'une par heure
CREATE OR REPLACE FUNCTION sync_noter_alerte(p_nature text, p_quand timestamptz) RETURNS void
LANGUAGE plpgsql AS $$
BEGIN
    IF p_nature NOT IN ('retard', 'erreur', 'certificat', 'quota') THEN
        RAISE EXCEPTION 'Synchronisation : sujet d''alerte inconnu (%)', p_nature;
    END IF;
    UPDATE sync_parametre SET
        alerte_retard_le     = CASE WHEN p_nature = 'retard'     THEN p_quand ELSE alerte_retard_le END,
        alerte_erreur_le     = CASE WHEN p_nature = 'erreur'     THEN p_quand ELSE alerte_erreur_le END,
        alerte_certificat_le = CASE WHEN p_nature = 'certificat' THEN p_quand ELSE alerte_certificat_le END,
        alerte_quota_le      = CASE WHEN p_nature = 'quota'      THEN p_quand ELSE alerte_quota_le END
    WHERE id = 1;
END $$;

-- Réglages de l'écran (bornes : contrainte sync_parametre_supervision_check)
CREATE OR REPLACE FUNCTION sync_regler_supervision(p_bandeau_minutes int, p_retard_heures int, p_erreur_minutes int,
                                                  p_purge_active boolean, p_purge_jours int) RETURNS void
LANGUAGE sql AS $$
    UPDATE sync_parametre SET bandeau_minutes = p_bandeau_minutes, alerte_retard_heures = p_retard_heures,
                              alerte_erreur_minutes = p_erreur_minutes, purge_active = p_purge_active, purge_jours = p_purge_jours,
                              modifie_le = now()
    WHERE id = 1
$$;

CREATE OR REPLACE FUNCTION sync_noter_purge_erreur(p_erreur text) RETURNS void
LANGUAGE sql AS $$
    UPDATE sync_parametre SET derniere_purge_erreur = left(p_erreur, 500) WHERE id = 1
$$;

-- ---- 3. Purge de la file d'envoi ----
-- Retire les lignes des lots accusés depuis plus de p_delai_jours jours, au plus p_max_lots lots par appel, dans l'ordre des
-- numéros et sans trou à partir du dernier lot déjà purgé. S'arrête avant un lot qui contient des lignes d'un chargement complet
-- non terminé (sync_chargement.etat en cours). Une transaction par appel (l'appelant boucle par petits paquets).
-- Renvoie le nombre de lots, de lignes et d'octets (taille des lots) retirés et le plus grand lot purgé par CET appel (0 : aucun).
CREATE OR REPLACE FUNCTION sync_purger(p_delai_jours int, p_max_lots int DEFAULT 20)
RETURNS TABLE (lots int, lignes bigint, octets bigint, jusqu_au_lot bigint)
LANGUAGE plpgsql AS $$
DECLARE
    p          sync_parametre;
    v_depart   bigint;
    v_dernier  bigint;
    v_lot      record;
    v_n        bigint;
    v_lots     int := 0;
    v_lignes   bigint := 0;
    v_octets   bigint := 0;
BEGIN
    IF p_delai_jours IS NULL OR p_delai_jours < 1 THEN
        RAISE EXCEPTION 'Synchronisation : délai de conservation invalide (1 jour au moins)';
    END IF;
    IF p_max_lots IS NULL OR p_max_lots < 1 OR p_max_lots > 1000 THEN
        RAISE EXCEPTION 'Synchronisation : nombre de lots à purger invalide (1 à 1000)';
    END IF;
    IF pg_is_in_recovery() THEN
        RAISE EXCEPTION 'Synchronisation : purge impossible sur une base en lecture seule';
    END IF;
    PERFORM pg_advisory_xact_lock(hashtext('sync_purger'));
    SELECT * INTO p FROM sync_parametre WHERE id = 1;
    v_depart := CASE WHEN p.purge_epoque = p.epoque THEN p.purge_dernier_lot ELSE 0 END;
    v_dernier := v_depart;
    FOR v_lot IN
        SELECT l.numero AS numero, l.octets AS taille FROM sync_lot l
        WHERE l.epoque = p.epoque AND l.numero > v_depart AND l.accuse_le IS NOT NULL
          AND l.accuse_le < now() - make_interval(days => p_delai_jours)
        ORDER BY l.numero LIMIT p_max_lots
    LOOP
        EXIT WHEN v_lot.numero <> v_dernier + 1;
        EXIT WHEN EXISTS (SELECT 1 FROM sync_sortie s JOIN sync_chargement c ON c.numero = s.chargement
                          WHERE s.epoque = p.epoque AND s.lot_numero = v_lot.numero AND c.etat IN ('demande', 'constitution', 'pret', 'annonce'));
        DELETE FROM sync_sortie s WHERE s.epoque = p.epoque AND s.lot_numero = v_lot.numero;   -- garde : lot accusé seulement
        GET DIAGNOSTICS v_n = ROW_COUNT;
        v_lots := v_lots + 1;
        v_lignes := v_lignes + v_n;
        v_octets := v_octets + v_lot.taille;
        v_dernier := v_lot.numero;
    END LOOP;
    IF v_lots > 0 THEN
        UPDATE sync_parametre SET purge_epoque = p.epoque, purge_dernier_lot = v_dernier, derniere_purge_le = now(),
                                  derniere_purge_lignes = v_lignes, purge_lignes_total = purge_lignes_total + v_lignes,
                                  derniere_purge_erreur = NULL
        WHERE id = 1;
    END IF;
    RETURN QUERY SELECT v_lots, v_lignes, v_octets, CASE WHEN v_lots > 0 THEN v_dernier ELSE 0::bigint END;
END $$;

COMMENT ON FUNCTION sync_purger(int, int) IS 'Purge de sync_sortie : lignes des lots accusés depuis plus de p_delai_jours jours, jamais un lot non accusé ni un chargement en cours (script 140).';

-- ---- 4. Droit « sync.suivre » ----
-- Posé à la première exécution seulement : une réexécution n'annule jamais un choix fait ensuite par le titulaire.
DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM permission WHERE code = 'sync.suivre') THEN
        INSERT INTO permission (code, module, libelle, sensible)
        VALUES ('sync.suivre', 'Administration', 'Voir le bandeau de synchronisation', false)
        ON CONFLICT DO NOTHING;
        -- Les droits du titulaire sont protégés par un déclencheur : levé le temps de cette transaction
        ALTER TABLE role_permission DISABLE TRIGGER trg_proteger_role_systeme;
        INSERT INTO role_permission (role_id, permission_code)
        SELECT r.id, 'sync.suivre' FROM role r WHERE r.code = 'titulaire'
        ON CONFLICT DO NOTHING;
        ALTER TABLE role_permission ENABLE TRIGGER trg_proteger_role_systeme;
    END IF;
END $$;

-- ---- 5. Sujet de notification « synchronisation » ----
-- Rejouable : la liste n'est refaite que si elle n'a pas encore « synchronisation » ; l'abonnement n'est posé qu'à ce moment-là
DO $garde$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'destinataire_notification_types_check' AND pg_get_constraintdef(oid) LIKE '%''synchronisation''%') THEN
        ALTER TABLE destinataire_notification DROP CONSTRAINT IF EXISTS destinataire_notification_types_check;
        ALTER TABLE destinataire_notification ADD CONSTRAINT destinataire_notification_types_check
            CHECK (types <@ ARRAY['rapport_quotidien', 'ecart_caisse', 'lot_bloque', 'sauvegarde', 'ecart_inventaire', 'lot_delaisse', 'surveillance', 'synchronisation']);
        UPDATE destinataire_notification SET types = types || 'synchronisation'::text
        WHERE 'sauvegarde' = ANY (types) AND NOT 'synchronisation' = ANY (types);
    END IF;
END $garde$;

DO $garde$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'message_sortant_type_check' AND pg_get_constraintdef(oid) LIKE '%''synchronisation''%') THEN
        ALTER TABLE message_sortant DROP CONSTRAINT IF EXISTS message_sortant_type_check;
        ALTER TABLE message_sortant ADD CONSTRAINT message_sortant_type_check
            CHECK (type = ANY (ARRAY['rapport_quotidien', 'ecart_caisse', 'lot_bloque', 'sauvegarde', 'ecart_inventaire', 'lot_delaisse', 'surveillance', 'synchronisation', 'test']));
    END IF;
END $garde$;
