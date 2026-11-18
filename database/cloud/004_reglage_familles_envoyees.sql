-- =====================================================================
-- Base centrale : réglages du serveur central (2026-11-16,
-- docs/conception-synchronisation.md § 14). Script de la base CENTRALE.
--   1. Familles de données nominatives envoyées par les officines :
--      cloud_famille (valeur globale, défauts du titulaire : employes NON,
--      pharmacie, fournisseurs, courriel_commandes OUI) et officine_famille
--      (surcharge par officine). Colonnes : cloud_famille_colonne (même
--      liste que l'officine, script 130, et Pharma.Shared).
--      * L'officine lit son réglage effectif (route GET /api/v1/reglages,
--        cloud_familles_officine_courante) et filtre AVANT l'envoi.
--      * Double sécurité : cloud_appliquer_lot efface à la réception toute
--        colonne d'une famille non autorisée (tables miroir ET lot brut
--        gardé, marqué expurge_le / colonnes_effacees), journal
--        « familles_effacees_reception » une fois par jour et par officine.
--      * Décocher ne supprime rien de ce qui a déjà été reçu : effacement
--        par commande explicite (cloud_effacer_famille, simulation par
--        défaut, confirmation exigée), journalisé.
--   2. Conservation des lots bruts (lot_recu) : durée globale
--      (cloud_parametre.conservation_lots_ans) et par officine
--      (officine.conservation_lots_ans), 6 ans par défaut, 6 ans MINIMUM
--      (refus en dessous), pas de maximum. Effacement des lots trop anciens
--      codé (cloud_purger_lots) mais DÉSACTIVÉ par défaut
--      (cloud_parametre.purge_lots_active = false) ; activable seulement
--      par la commande d'administration ; chaque effacement journalisé
--      (officine, nombre de lots, période). lot_recu reste en ajout seul
--      pour tout autre chemin : le déclencheur n'autorise une suppression
--      que pour un lot dont la durée de son officine est dépassée ET
--      effacement activé ET opération posée par la fonction ; une
--      modification que pour retirer des colonnes nominatives (contenu
--      recontrôlé). Partitions mensuelles communes à toutes les officines :
--      lignes effacées une à une selon la durée de CHAQUE officine ; une
--      partition n'est supprimée qu'une fois vide.
-- Idempotent ; à passer après 001, 002 et 003 (remplace cloud_appliquer_lot
-- de 003 et cloud_journaliser de 001). Aucune donnée existante modifiée.
-- =====================================================================

-- ---------------------------------------------------------------------
-- 1. Familles nominatives (tables d'administration : aucun accès direct du rôle applicatif)
-- ---------------------------------------------------------------------
CREATE TABLE IF NOT EXISTS cloud_famille (
    famille    text        PRIMARY KEY CHECK (famille ~ '^[a-z_]{1,40}$'),
    libelle    text        NOT NULL,
    envoyee    boolean     NOT NULL,
    modifie_le timestamptz NOT NULL DEFAULT now()
);
-- Défauts validés par le titulaire (2026-11-16) ; un réglage déjà fait n'est jamais écrasé
INSERT INTO cloud_famille (famille, libelle, envoyee) VALUES
    ('employes',           'Noms et identifiants du personnel',        false),
    ('pharmacie',          'Titulaire et coordonnées de la pharmacie', true),
    ('fournisseurs',       'Coordonnées des fournisseurs',             true),
    ('courriel_commandes', 'Adresse courriel des commandes envoyées',  true)
ON CONFLICT (famille) DO NOTHING;

CREATE TABLE IF NOT EXISTS cloud_famille_colonne (
    famille    text NOT NULL REFERENCES cloud_famille (famille),
    nom_table  text NOT NULL,
    colonne    text NOT NULL,
    PRIMARY KEY (famille, nom_table, colonne)
);
INSERT INTO cloud_famille_colonne (famille, nom_table, colonne) VALUES
    ('employes',           'utilisateur',            'nom_complet'),
    ('employes',           'utilisateur',            'identifiant'),
    ('pharmacie',          'pharmacie',              'titulaire'),
    ('pharmacie',          'pharmacie',              'adresse'),
    ('pharmacie',          'pharmacie',              'telephone'),
    ('pharmacie',          'parametre_notification', 'smtp_expediteur'),
    ('fournisseurs',       'fournisseur',            'telephone'),
    ('fournisseurs',       'fournisseur',            'email'),
    ('fournisseurs',       'fournisseur',            'adresse'),
    ('courriel_commandes', 'commande_fournisseur',   'envoyee_email_a')
ON CONFLICT DO NOTHING;

CREATE TABLE IF NOT EXISTS officine_famille (
    officine_id uuid        NOT NULL REFERENCES officine (id) ON UPDATE CASCADE ON DELETE CASCADE,
    famille     text        NOT NULL REFERENCES cloud_famille (famille),
    envoyee     boolean     NOT NULL,
    modifie_le  timestamptz NOT NULL DEFAULT now(),
    PRIMARY KEY (officine_id, famille)
);

-- ---------------------------------------------------------------------
-- 2. Conservation des lots bruts
-- ---------------------------------------------------------------------
ALTER TABLE cloud_parametre ADD COLUMN IF NOT EXISTS conservation_lots_ans int NOT NULL DEFAULT 6;
ALTER TABLE cloud_parametre ADD COLUMN IF NOT EXISTS purge_lots_active boolean NOT NULL DEFAULT false;
ALTER TABLE officine ADD COLUMN IF NOT EXISTS conservation_lots_ans int;
DO $$ BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'cloud_parametre_conservation_lots_ans_check') THEN
        ALTER TABLE cloud_parametre ADD CONSTRAINT cloud_parametre_conservation_lots_ans_check CHECK (conservation_lots_ans >= 6);
    END IF;
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'officine_conservation_lots_ans_check') THEN
        ALTER TABLE officine ADD CONSTRAINT officine_conservation_lots_ans_check CHECK (conservation_lots_ans IS NULL OR conservation_lots_ans >= 6);
    END IF;
END $$;

-- Lot brut expurgé (colonnes nominatives retirées) : l'empreinte reçue ne couvre plus le contenu gardé
ALTER TABLE lot_recu ADD COLUMN IF NOT EXISTS expurge_le timestamptz;
ALTER TABLE lot_recu ADD COLUMN IF NOT EXISTS colonnes_effacees text[];

-- ---------------------------------------------------------------------
-- Fonctions de lecture
-- ---------------------------------------------------------------------

-- Familles envoyées par une officine (surcharge, sinon valeur globale), triées
CREATE OR REPLACE FUNCTION cloud_familles_envoyees(p_officine uuid) RETURNS text[]
LANGUAGE sql STABLE SECURITY DEFINER SET search_path = public, pg_temp AS $$
    SELECT coalesce(array_agg(f.famille ORDER BY f.famille COLLATE "C"), '{}'::text[])
    FROM cloud_famille f LEFT JOIN officine_famille o ON o.famille = f.famille AND o.officine_id = p_officine
    WHERE coalesce(o.envoyee, f.envoyee)
$$;
REVOKE EXECUTE ON FUNCTION cloud_familles_envoyees(uuid) FROM PUBLIC;

-- Pour l'API : familles de l'officine courante (celle du certificat, posée dans la transaction) ; aucune si non posée
CREATE OR REPLACE FUNCTION cloud_familles_officine_courante() RETURNS text[]
LANGUAGE sql STABLE SECURITY DEFINER SET search_path = public, pg_temp AS $$
    SELECT CASE WHEN cloud_officine_courante() IS NULL THEN '{}'::text[] ELSE cloud_familles_envoyees(cloud_officine_courante()) END
$$;

-- Colonnes (« table.colonne ») des familles NON autorisées pour l'officine courante ; toutes si non posée
CREATE OR REPLACE FUNCTION cloud_colonnes_interdites() RETURNS text[]
LANGUAGE sql STABLE SECURITY DEFINER SET search_path = public, pg_temp AS $$
    SELECT coalesce(array_agg(c.nom_table || '.' || c.colonne ORDER BY 1), '{}'::text[])
    FROM cloud_famille_colonne c
    WHERE cloud_officine_courante() IS NULL OR NOT c.famille = ANY (cloud_familles_envoyees(cloud_officine_courante()))
$$;

-- Colonnes (« table.colonne ») présentes dans les données d'un lot sync-v1
CREATE OR REPLACE FUNCTION cloud_colonnes_presentes(p_lot jsonb, p_colonnes text[]) RETURNS text[]
LANGUAGE sql IMMUTABLE AS $$
    SELECT coalesce(array_agg(c ORDER BY c), '{}'::text[]) FROM unnest(coalesce(p_colonnes, '{}'::text[])) c
    WHERE EXISTS (SELECT 1 FROM jsonb_array_elements(p_lot -> 'lignes') x
                  WHERE x ->> 'table' = split_part(c, '.', 1) AND jsonb_typeof(x -> 'donnees') = 'object'
                    AND (x -> 'donnees') ? split_part(c, '.', 2))
$$;

-- Lot sync-v1 sans les colonnes données (« table.colonne »), lignes dans le même ordre
CREATE OR REPLACE FUNCTION cloud_lot_sans_colonnes(p_lot jsonb, p_colonnes text[]) RETURNS jsonb
LANGUAGE sql IMMUTABLE AS $$
    SELECT CASE WHEN coalesce(cardinality(p_colonnes), 0) = 0 THEN p_lot ELSE
        jsonb_set(p_lot, '{lignes}', coalesce((
            SELECT jsonb_agg(CASE WHEN jsonb_typeof(x -> 'donnees') = 'object'
                                  THEN jsonb_set(x, '{donnees}', (x -> 'donnees') - ARRAY(
                                       SELECT split_part(c, '.', 2) FROM unnest(p_colonnes) c WHERE split_part(c, '.', 1) = x ->> 'table'))
                                  ELSE x END ORDER BY o)
            FROM jsonb_array_elements(p_lot -> 'lignes') WITH ORDINALITY AS e(x, o)), '[]'::jsonb))
    END
$$;

-- Durée de conservation des lots bruts d'une officine (surcharge, sinon globale ; jamais moins de 6 ans)
CREATE OR REPLACE FUNCTION cloud_conservation_ans(p_officine uuid) RETURNS int
LANGUAGE sql STABLE SECURITY DEFINER SET search_path = public, pg_temp AS $$
    SELECT greatest(6, coalesce((SELECT conservation_lots_ans FROM officine WHERE id = p_officine),
                                (SELECT conservation_lots_ans FROM cloud_parametre WHERE id = 1), 6))
$$;

-- Journal : refus répétés et effacements à la réception limités (une ligne par heure, ou par jour pour les effacements)
CREATE OR REPLACE FUNCTION cloud_journaliser(p_officine uuid, p_certificat text, p_evenement text, p_detail text)
RETURNS void
LANGUAGE plpgsql SECURITY DEFINER SET search_path = public, pg_temp AS $$
BEGIN
    IF p_evenement IN ('certificat_refuse', 'lot_refuse', 'quota_depasse', 'activation_refusee', 'renouvellement_refuse', 'familles_effacees_reception')
       AND EXISTS (SELECT 1 FROM journal_securite j
                   WHERE j.evenement = p_evenement
                     AND j.le > now() - CASE WHEN p_evenement = 'familles_effacees_reception' THEN interval '1 day' ELSE interval '1 hour' END
                     AND j.officine_id IS NOT DISTINCT FROM p_officine AND j.certificat_empreinte IS NOT DISTINCT FROM p_certificat) THEN
        RETURN;
    END IF;
    INSERT INTO journal_securite (officine_id, certificat_empreinte, evenement, detail)
    VALUES (p_officine, p_certificat, p_evenement, left(p_detail, 500));
END $$;

-- ---------------------------------------------------------------------
-- lot_recu : ajout seul, sauf deux chemins contrôlés
-- ---------------------------------------------------------------------
-- Garde de rejeu (2026-11-18) : définition remplacée par le script 021 ; rejouer ce script après 021 ne la
-- ramène pas en arrière (marqueur : table journal_depart_officine, posé par 021).
DO $rejeu$ BEGIN
    IF to_regclass('public.journal_depart_officine') IS NOT NULL THEN
        RAISE NOTICE 'cloud_lot_recu_garde : définition plus récente (script 021) gardée';
    ELSE
        EXECUTE $def$
CREATE OR REPLACE FUNCTION cloud_lot_recu_garde() RETURNS trigger
LANGUAGE plpgsql AS $$
DECLARE v_op text := coalesce(current_setting('cloud.lot_recu_operation', true), '');
BEGIN
    IF TG_OP = 'DELETE' AND v_op = 'purge'
       AND (SELECT purge_lots_active FROM cloud_parametre WHERE id = 1)
       AND OLD.recu_le < now() - make_interval(years => cloud_conservation_ans(OLD.officine_id)) THEN
        RETURN OLD;
    END IF;
    IF TG_OP = 'UPDATE' AND v_op = 'expurger' AND NEW.expurge_le IS NOT NULL
       AND coalesce(OLD.colonnes_effacees, '{}') <@ coalesce(NEW.colonnes_effacees, '{}')
       AND (to_jsonb(NEW) - ARRAY['contenu', 'expurge_le', 'colonnes_effacees'])
         = (to_jsonb(OLD) - ARRAY['contenu', 'expurge_le', 'colonnes_effacees'])
       AND convert_from(NEW.contenu, 'UTF8')::jsonb
         = cloud_lot_sans_colonnes(convert_from(OLD.contenu, 'UTF8')::jsonb, NEW.colonnes_effacees) THEN
        RETURN NEW;
    END IF;
    RAISE EXCEPTION '% : ajout seul', TG_TABLE_NAME;
END $$;
$def$;
    END IF;
END $rejeu$;
DROP TRIGGER IF EXISTS trg_lot_recu_ajout_seul ON lot_recu;
CREATE TRIGGER trg_lot_recu_ajout_seul BEFORE UPDATE OR DELETE ON lot_recu FOR EACH ROW EXECUTE FUNCTION cloud_lot_recu_garde();

-- ---------------------------------------------------------------------
-- Dépôt d'un lot (remplace la version de 003) : en plus, colonnes des familles non autorisées pour l'officine effacées
-- AVANT toute écriture (tables miroir et lot brut gardé) et journalisées (une fois par jour)
-- ---------------------------------------------------------------------
-- Garde de rejeu (2026-11-18) : définition remplacée par le script 006 ; rejouer ce script après 006 ne la
-- ramène pas en arrière (marqueur : colonne cloud_parametre.quota_chargement_octets, posé par 006).
DO $rejeu$ BEGIN
    IF EXISTS (SELECT 1 FROM pg_attribute WHERE attrelid = 'public.cloud_parametre'::regclass AND attname = 'quota_chargement_octets' AND NOT attisdropped) THEN
        RAISE NOTICE 'cloud_appliquer_lot : définition plus récente (script 006) gardée';
    ELSE
        EXECUTE $def$
CREATE OR REPLACE FUNCTION cloud_appliquer_lot(p_epoque int, p_numero bigint, p_empreinte text, p_version text, p_contenu bytea)
RETURNS TABLE (statut text, dernier bigint)
LANGUAGE plpgsql AS $$
DECLARE
    v_off      uuid := cloud_officine_courante();
    e          officine_epoque;
    v_lot      jsonb;
    r          record;
    m          cloud_table_miroir;
    v_ch       chargement;
    v_cle      text;
    v_objet    text;
    v_match    text;
    v_garde    text;
    v_set      text;
    v_nb       int;
    v_quota    bigint;
    v_jour     bigint;
    v_effacees text[];
    v_contenu  bytea := p_contenu;
BEGIN
    IF v_off IS NULL THEN RAISE EXCEPTION 'Officine courante non posée (app.officine_id)'; END IF;
    INSERT INTO officine_epoque (officine_id, epoque) VALUES (v_off, p_epoque) ON CONFLICT DO NOTHING;
    SELECT * INTO e FROM officine_epoque WHERE officine_id = v_off AND epoque = p_epoque FOR UPDATE;
    IF p_numero <= e.dernier_lot THEN
        IF EXISTS (SELECT 1 FROM lot_recu l WHERE l.officine_id = v_off AND l.epoque = p_epoque AND l.numero = p_numero AND l.empreinte = p_empreinte) THEN
            RETURN QUERY SELECT 'deja_recu'::text, e.dernier_lot; RETURN;
        END IF;
        RETURN QUERY SELECT 'empreinte_differente'::text, e.dernier_lot; RETURN;
    END IF;
    IF p_numero > e.dernier_lot + 1 THEN
        RETURN QUERY SELECT 'trou'::text, e.dernier_lot; RETURN;
    END IF;
    SELECT quota_octets_jour INTO v_quota FROM cloud_parametre WHERE id = 1;
    SELECT coalesce((SELECT octets FROM officine_volume_jour WHERE officine_id = v_off AND jour = (now() AT TIME ZONE 'UTC')::date), 0) INTO v_jour;
    IF v_jour + length(p_contenu) > coalesce(v_quota, 536870912) THEN
        RETURN QUERY SELECT 'quota_depasse'::text, e.dernier_lot; RETURN;
    END IF;
    SELECT * INTO v_ch FROM chargement WHERE officine_id = v_off AND etat = 'en_cours' AND epoque = p_epoque;

    v_lot := convert_from(p_contenu, 'UTF8')::jsonb;
    -- Double sécurité : familles non autorisées effacées avant toute écriture
    v_effacees := cloud_colonnes_presentes(v_lot, cloud_colonnes_interdites());
    IF cardinality(v_effacees) > 0 THEN
        v_lot := cloud_lot_sans_colonnes(v_lot, v_effacees);
        v_contenu := convert_to(v_lot::text, 'UTF8');
        PERFORM cloud_journaliser(v_off, NULL, 'familles_effacees_reception',
            format('lot %s/%s : colonnes non autorisées effacées à la réception (%s)', p_epoque, p_numero, array_to_string(v_effacees, ', ')));
    END IF;
    v_nb := jsonb_array_length(v_lot -> 'lignes');
    FOR r IN SELECT x ->> 'table' AS nom, jsonb_agg(x) AS lignes
             FROM jsonb_array_elements(v_lot -> 'lignes') x GROUP BY 1 ORDER BY 1 LOOP
        SELECT * INTO m FROM cloud_table_miroir WHERE nom_table = r.nom;
        IF NOT FOUND THEN RAISE EXCEPTION 'Table non reçue par le serveur central : %', r.nom USING ERRCODE = 'P0001'; END IF;
        SELECT string_agg(format('%I', c), ', ' ORDER BY n) INTO v_cle FROM unnest(m.cle) WITH ORDINALITY k(c, n);
        SELECT string_agg(format('%L, split_part(f.x ->> ''id'', ''|'', %s)', c, n), ', ' ORDER BY n),
               string_agg(format('d.%I = k.%I', c, c), ' AND ' ORDER BY n),
               string_agg(format('coalesce(to_jsonb(d.%I) #>> ''{}'', '''')', c), ' || ''|'' || ' ORDER BY n)
            INTO v_objet, v_match, v_garde FROM unnest(m.cle) WITH ORDINALITY k(c, n);
        IF cardinality(m.cle) > 1 THEN v_match := v_match || ' AND (' || v_garde || ') = f.x ->> ''id'''; END IF;
        SELECT string_agg(format('%I = EXCLUDED.%I', c, c), ', ')
            INTO v_set FROM unnest(m.colonnes || ARRAY['sync_epoque', 'sync_numero', 'sync_recu_le', 'sync_absente_le']) c WHERE NOT c = ANY (m.cle);
        -- État final de chaque donnée dans ce lot (plus grand n° de ligne) ; « instantane » vaut création
        EXECUTE format($f$
            INSERT INTO %1$I AS d
            SELECT p.* FROM (SELECT DISTINCT ON (x ->> 'id') x FROM jsonb_array_elements($1) x
                             ORDER BY x ->> 'id', (x ->> 'n')::bigint DESC) f,
                 LATERAL jsonb_populate_record(NULL::%1$I, (f.x -> 'donnees') || jsonb_build_object(
                     'officine_id', $2, 'sync_epoque', $3, 'sync_numero', (f.x ->> 'n')::bigint, 'sync_recu_le', now())) p
            WHERE f.x ->> 'op' <> 'suppression'
            ON CONFLICT (officine_id, %2$s) DO UPDATE SET %3$s
            WHERE (d.sync_epoque, d.sync_numero) < (EXCLUDED.sync_epoque, EXCLUDED.sync_numero)$f$, r.nom, v_cle, v_set)
        USING r.lignes, v_off, p_epoque;
        EXECUTE format($f$
            DELETE FROM %1$I d USING (SELECT DISTINCT ON (x ->> 'id') x FROM jsonb_array_elements($1) x
                                      ORDER BY x ->> 'id', (x ->> 'n')::bigint DESC) f,
                 LATERAL jsonb_populate_record(NULL::%1$I, jsonb_build_object(%2$s)) k
            WHERE f.x ->> 'op' = 'suppression' AND d.officine_id = $2 AND %3$s
              AND (d.sync_epoque, d.sync_numero) < ($3, (f.x ->> 'n')::bigint)$f$, r.nom, v_objet, v_match)
        USING r.lignes, v_off, p_epoque;
        -- Lignes de l'instantané du chargement en cours : vues (même si une version plus récente est déjà là)
        IF v_ch.numero IS NOT NULL THEN
            EXECUTE format($f$
                UPDATE %1$I d SET sync_chargement = $4, sync_absente_le = NULL
                FROM (SELECT x FROM jsonb_array_elements($1) x WHERE x ->> 'op' = 'instantane' AND (x ->> 'n')::bigint = $3) f,
                     LATERAL jsonb_populate_record(NULL::%1$I, jsonb_build_object(%2$s)) k
                WHERE d.officine_id = $2 AND %3$s$f$, r.nom, v_objet, v_match)
            USING r.lignes, v_off, v_ch.repere, v_ch.numero;
        END IF;
    END LOOP;

    INSERT INTO lot_recu (officine_id, epoque, numero, empreinte, version_contrat, nb_lignes, octets, contenu, expurge_le, colonnes_effacees)
    VALUES (v_off, p_epoque, p_numero, p_empreinte, p_version, v_nb, length(p_contenu), v_contenu,
            CASE WHEN cardinality(v_effacees) > 0 THEN now() END, CASE WHEN cardinality(v_effacees) > 0 THEN v_effacees END);
    INSERT INTO officine_volume_jour (officine_id, jour, octets) VALUES (v_off, (now() AT TIME ZONE 'UTC')::date, length(p_contenu))
    ON CONFLICT (officine_id, jour) DO UPDATE SET octets = officine_volume_jour.octets + EXCLUDED.octets;
    UPDATE officine_epoque SET dernier_lot = p_numero, dernier_recu_le = now() WHERE officine_id = v_off AND epoque = p_epoque;
    RETURN QUERY SELECT 'accepte'::text, p_numero;
END $$;
$def$;
    END IF;
END $rejeu$;

-- ---------------------------------------------------------------------
-- Administration (propriétaire de la base, outil d'administration seulement)
-- ---------------------------------------------------------------------

-- Famille envoyée ou non : globale (p_officine NULL) ou pour une officine (p_envoyee NULL : retour à la valeur globale)
CREATE OR REPLACE FUNCTION cloud_regler_famille(p_famille text, p_envoyee boolean, p_officine uuid DEFAULT NULL) RETURNS void
LANGUAGE plpgsql AS $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM cloud_famille WHERE famille = p_famille) THEN
        RAISE EXCEPTION 'Famille inconnue : % (attendu : %)', p_famille, (SELECT string_agg(famille, ', ' ORDER BY famille) FROM cloud_famille);
    END IF;
    IF p_officine IS NULL THEN
        IF p_envoyee IS NULL THEN RAISE EXCEPTION 'Valeur globale : oui ou non attendu'; END IF;
        UPDATE cloud_famille SET envoyee = p_envoyee, modifie_le = now() WHERE famille = p_famille;
    ELSE
        IF NOT EXISTS (SELECT 1 FROM officine WHERE id = p_officine) THEN RAISE EXCEPTION 'Officine inconnue : %', p_officine; END IF;
        IF p_envoyee IS NULL THEN
            DELETE FROM officine_famille WHERE officine_id = p_officine AND famille = p_famille;
        ELSE
            INSERT INTO officine_famille (officine_id, famille, envoyee) VALUES (p_officine, p_famille, p_envoyee)
            ON CONFLICT (officine_id, famille) DO UPDATE SET envoyee = EXCLUDED.envoyee, modifie_le = now();
        END IF;
    END IF;
    INSERT INTO journal_securite (officine_id, evenement, detail)
    VALUES (p_officine, 'reglage_famille', format('famille %s : %s (%s)', p_famille,
            CASE WHEN p_envoyee IS NULL THEN 'valeur globale' WHEN p_envoyee THEN 'envoyée' ELSE 'non envoyée' END,
            CASE WHEN p_officine IS NULL THEN 'global' ELSE 'officine' END));
END $$;

-- Durée de conservation des lots bruts (ans) : 6 minimum, pas de maximum ; p_ans NULL pour une officine = valeur globale
CREATE OR REPLACE FUNCTION cloud_regler_conservation(p_ans int, p_officine uuid DEFAULT NULL) RETURNS void
LANGUAGE plpgsql AS $$
BEGIN
    IF p_ans IS NOT NULL AND p_ans < 6 THEN
        RAISE EXCEPTION 'Durée de conservation des lots bruts : 6 ans minimum (demandé : % an(s))', p_ans USING ERRCODE = 'P0001';
    END IF;
    IF p_officine IS NULL THEN
        IF p_ans IS NULL THEN RAISE EXCEPTION 'Durée globale attendue (6 ans minimum)'; END IF;
        UPDATE cloud_parametre SET conservation_lots_ans = p_ans WHERE id = 1;
    ELSE
        UPDATE officine SET conservation_lots_ans = p_ans WHERE id = p_officine;
        IF NOT FOUND THEN RAISE EXCEPTION 'Officine inconnue : %', p_officine; END IF;
    END IF;
    INSERT INTO journal_securite (officine_id, evenement, detail)
    VALUES (p_officine, 'reglage_conservation', format('conservation des lots bruts : %s', coalesce(p_ans || ' ans', 'valeur globale')));
END $$;

-- Effacement des lots trop anciens : activé ou désactivé (désactivé par défaut)
CREATE OR REPLACE FUNCTION cloud_regler_purge(p_active boolean) RETURNS void
LANGUAGE plpgsql AS $$
BEGIN
    UPDATE cloud_parametre SET purge_lots_active = coalesce(p_active, false) WHERE id = 1;
    INSERT INTO journal_securite (evenement, detail)
    VALUES ('reglage_purge', CASE WHEN p_active THEN 'effacement des lots trop anciens ACTIVÉ' ELSE 'effacement des lots trop anciens désactivé' END);
END $$;

-- Lots bruts dont la durée de conservation de leur officine est dépassée : simulation (compte) ou effacement (seulement si
-- activé). Une ligne par officine concernée ; puis les partitions mensuelles devenues vides et plus anciennes que 6 ans
-- (minimum) sont détachées et supprimées. Chaque effacement est journalisé.
CREATE OR REPLACE FUNCTION cloud_purger_lots(p_simulation boolean DEFAULT true)
RETURNS TABLE (officine_id uuid, conservation_ans int, lots bigint, recus_du timestamptz, recus_au timestamptz)
LANGUAGE plpgsql AS $$
DECLARE o record; v_ans int; v_n bigint; v_du timestamptz; v_au timestamptz; part record; v_haut timestamptz;
BEGIN
    IF NOT p_simulation AND NOT (SELECT purge_lots_active FROM cloud_parametre WHERE id = 1) THEN
        RAISE EXCEPTION 'Effacement des lots trop anciens désactivé (commande --purge-lots activer)' USING ERRCODE = 'P0001';
    END IF;
    FOR o IN SELECT id FROM officine ORDER BY id LOOP
        v_ans := cloud_conservation_ans(o.id);
        PERFORM set_config('app.officine_id', o.id::text, true);
        IF p_simulation THEN
            SELECT count(*), min(l.recu_le), max(l.recu_le) INTO v_n, v_du, v_au FROM lot_recu l
            WHERE l.officine_id = o.id AND l.recu_le < now() - make_interval(years => v_ans);
        ELSE
            PERFORM set_config('cloud.lot_recu_operation', 'purge', true);
            WITH d AS (DELETE FROM lot_recu l WHERE l.officine_id = o.id AND l.recu_le < now() - make_interval(years => v_ans) RETURNING l.recu_le)
            SELECT count(*), min(d.recu_le), max(d.recu_le) INTO v_n, v_du, v_au FROM d;
            PERFORM set_config('cloud.lot_recu_operation', '', true);
            IF v_n > 0 THEN
                INSERT INTO journal_securite (officine_id, evenement, detail)
                VALUES (o.id, 'lots_purges', format('%s lot(s) brut(s) effacé(s), reçus du %s au %s (conservation %s ans)',
                                                    v_n, to_char(v_du, 'YYYY-MM-DD'), to_char(v_au, 'YYYY-MM-DD'), v_ans));
            END IF;
        END IF;
        IF v_n > 0 THEN RETURN QUERY SELECT o.id, v_ans, v_n, v_du, v_au; END IF;
    END LOOP;
    PERFORM set_config('app.officine_id', '', true);
    IF p_simulation THEN RETURN; END IF;
    -- Partitions mensuelles vides entièrement plus anciennes que le minimum de 6 ans
    FOR part IN SELECT c.relname, pg_get_expr(c.relpartbound, c.oid) AS borne
                FROM pg_inherits i JOIN pg_class c ON c.oid = i.inhrelid
                WHERE i.inhparent = 'lot_recu'::regclass AND c.relname ~ '^lot_recu_[0-9]{4}_[0-9]{2}$' ORDER BY 1 LOOP
        v_haut := (substring(part.borne FROM 'TO \(''([^'']+)''\)'))::timestamptz;
        CONTINUE WHEN v_haut IS NULL OR v_haut >= now() - interval '6 years';
        EXECUTE format('SELECT count(*) FROM %I', part.relname) INTO v_n;
        CONTINUE WHEN v_n > 0;
        EXECUTE format('ALTER TABLE lot_recu DETACH PARTITION %I', part.relname);
        EXECUTE format('DROP TABLE %I', part.relname);
        INSERT INTO journal_securite (evenement, detail) VALUES ('partition_supprimee', format('partition vide %s supprimée', part.relname));
    END LOOP;
END $$;

-- Effacement de ce qui a déjà été reçu pour une famille NON autorisée (tables miroir et lots bruts), pour une officine ou
-- pour toutes celles où la famille est décochée. SUPPRESSION DE DONNÉES : simulation (compte) tant que p_confirmer est faux.
-- Refusé pour une officine où la famille est encore envoyée (elle reviendrait au prochain envoi).
CREATE OR REPLACE FUNCTION cloud_effacer_famille(p_famille text, p_officine uuid DEFAULT NULL, p_confirmer boolean DEFAULT false)
RETURNS TABLE (officine_id uuid, nom_table text, colonne text, lignes bigint, lots bigint)
LANGUAGE plpgsql AS $$
DECLARE o record; c record; v_cols text[]; v_n bigint; v_lots bigint; v_total bigint; v_total_lots bigint;
BEGIN
    IF NOT EXISTS (SELECT 1 FROM cloud_famille WHERE famille = p_famille) THEN RAISE EXCEPTION 'Famille inconnue : %', p_famille; END IF;
    IF p_officine IS NOT NULL THEN
        IF NOT EXISTS (SELECT 1 FROM officine WHERE id = p_officine) THEN RAISE EXCEPTION 'Officine inconnue : %', p_officine; END IF;
        IF p_famille = ANY (cloud_familles_envoyees(p_officine)) THEN
            RAISE EXCEPTION 'La famille % est encore envoyée par cette officine : décochez-la d''abord', p_famille USING ERRCODE = 'P0001';
        END IF;
    END IF;
    SELECT array_agg(f.nom_table || '.' || f.colonne ORDER BY 1) INTO v_cols FROM cloud_famille_colonne f WHERE f.famille = p_famille;
    FOR o IN SELECT x.id FROM officine x
             WHERE (p_officine IS NULL OR x.id = p_officine) AND NOT p_famille = ANY (cloud_familles_envoyees(x.id)) ORDER BY x.id LOOP
        PERFORM set_config('app.officine_id', o.id::text, true);
        v_total := 0;
        FOR c IN SELECT f.nom_table, f.colonne FROM cloud_famille_colonne f
                 JOIN cloud_table_miroir m ON m.nom_table = f.nom_table AND f.colonne = ANY (m.colonnes)
                 WHERE f.famille = p_famille ORDER BY 1, 2 LOOP
            IF p_confirmer THEN
                EXECUTE format('WITH u AS (UPDATE %I SET %I = NULL WHERE officine_id = $1 AND %I IS NOT NULL RETURNING 1) SELECT count(*) FROM u',
                               c.nom_table, c.colonne, c.colonne) INTO v_n USING o.id;
            ELSE
                EXECUTE format('SELECT count(*) FROM %I WHERE officine_id = $1 AND %I IS NOT NULL', c.nom_table, c.colonne) INTO v_n USING o.id;
            END IF;
            v_total := v_total + v_n;
            RETURN QUERY SELECT o.id, c.nom_table, c.colonne, v_n, NULL::bigint;
        END LOOP;
        IF p_confirmer THEN
            PERFORM set_config('cloud.lot_recu_operation', 'expurger', true);
            WITH u AS (
                UPDATE lot_recu l
                SET contenu = convert_to(cloud_lot_sans_colonnes(convert_from(l.contenu, 'UTF8')::jsonb, v_cols)::text, 'UTF8'),
                    colonnes_effacees = ARRAY(SELECT DISTINCT x FROM unnest(coalesce(l.colonnes_effacees, '{}') || v_cols) x ORDER BY 1),
                    expurge_le = now()
                WHERE l.officine_id = o.id AND cardinality(cloud_colonnes_presentes(convert_from(l.contenu, 'UTF8')::jsonb, v_cols)) > 0
                RETURNING 1)
            SELECT count(*) INTO v_lots FROM u;
            PERFORM set_config('cloud.lot_recu_operation', '', true);
            INSERT INTO journal_securite (officine_id, evenement, detail)
            VALUES (o.id, 'famille_effacee', format('famille %s : %s valeur(s) effacée(s) des tables, %s lot(s) brut(s) expurgé(s)', p_famille, v_total, v_lots));
        ELSE
            SELECT count(*) INTO v_lots FROM lot_recu l
            WHERE l.officine_id = o.id AND cardinality(cloud_colonnes_presentes(convert_from(l.contenu, 'UTF8')::jsonb, v_cols)) > 0;
        END IF;
        RETURN QUERY SELECT o.id, NULL::text, NULL::text, NULL::bigint, v_lots;
    END LOOP;
    PERFORM set_config('app.officine_id', '', true);
END $$;

REVOKE EXECUTE ON FUNCTION cloud_regler_famille(text, boolean, uuid), cloud_regler_conservation(int, uuid), cloud_regler_purge(boolean),
    cloud_purger_lots(boolean), cloud_effacer_famille(text, uuid, boolean), cloud_conservation_ans(uuid) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION cloud_familles_officine_courante(), cloud_colonnes_interdites(), cloud_colonnes_presentes(jsonb, text[]),
    cloud_lot_sans_colonnes(jsonb, text[]), cloud_appliquer_lot(int, bigint, text, text, bytea) TO pharma_cloud_api;
REVOKE EXECUTE ON FUNCTION cloud_familles_officine_courante(), cloud_colonnes_interdites() FROM PUBLIC;
GRANT EXECUTE ON FUNCTION cloud_familles_officine_courante(), cloud_colonnes_interdites() TO pharma_cloud_api;

COMMENT ON TABLE cloud_famille IS 'Serveur central : familles de données nominatives, valeur globale envoyée ou non (script 004).';
COMMENT ON TABLE officine_famille IS 'Serveur central : surcharge par officine des familles nominatives envoyées (script 004).';
COMMENT ON TABLE cloud_famille_colonne IS 'Serveur central : colonnes de chaque famille nominative (même liste que l''officine, script 130) (script 004).';
COMMENT ON COLUMN cloud_parametre.conservation_lots_ans IS 'Durée de conservation des lots bruts (ans), 6 minimum (script 004).';
COMMENT ON COLUMN cloud_parametre.purge_lots_active IS 'Effacement des lots bruts trop anciens : désactivé par défaut (script 004).';
COMMENT ON COLUMN lot_recu.expurge_le IS 'Colonnes nominatives retirées du lot brut (réception ou effacement) : l''empreinte ne couvre plus le contenu gardé (script 004).';
