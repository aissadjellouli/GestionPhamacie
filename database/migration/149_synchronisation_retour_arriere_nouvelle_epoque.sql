-- =====================================================================
-- 149 — Synchronisation, lot S6.2 : base de l'officine revenue en arrière (restauration d'une sauvegarde, bascule sur le
--   2e serveur avec retard de réplication) et « Repartir sur une nouvelle époque » (2026-11-18, « go S6 » du titulaire,
--   docs/conception-synchronisation.md § 30).
--   1. sync_parametre : détection notée (retour_arriere_le, retour_arriere_central = dernier lot reçu par le serveur
--      central pour l'époque en cours), avance des n° faite (retour_arriere_sequences_le), alerte envoyée au titulaire
--      (retour_arriere_alerte_le : une alerte par détection). Garde du CHECK posée une seule fois.
--   2. sync_noter_retour_arriere(dernier lot du central) : pose la détection si le serveur central a reçu PLUS de lots que
--      cette base n'en a formé pour l'époque en cours ; renvoie true à la première détection seulement.
--   3. sync_avancer_sequences(jsonb { table : plus grand id reçu par le central }) : avance la séquence de la colonne id
--      des tables envoyées (sens effectif montante ou bidirectionnelle) dont la clé est {id} ; jamais en arrière ; table
--      inconnue, non envoyée, sans séquence ou valeur invalide : ignorée. Les ventes faites ensuite ne reprennent plus
--      les n° perdus par la restauration.
--   4. sync_noter_alerte_retour_arriere(date) : alerte envoyée pour la détection en cours.
--   5. sync_nouvelle_epoque(titulaire) : refusée sans détection ; époque + 1, dernier lot et dernier accusé à 0, repère
--      de purge remis à zéro, détection effacée ; lots non accusés et lignes non envoyées de l'ancienne époque GARDÉS
--      (signalés « autres époques », jamais renvoyés automatiquement) ; chargement en cours de l'ancienne époque remplacé,
--      puis chargement complet demandé (mécanisme du § 13, motif « demande »).
--   Corrections après revue et essai (2026-11-18, conception § 30.8) :
--   6. sync_noter_retour_arriere_refus(dernier lot du central) : variante qui ne compare pas à dernier_lot, posée quand un
--      lot jamais reformé est refusé « empreinte différente » alors que le central a reçu au moins ce n°.
--   7. sync_avancer_sequences : table verrouillée (SHARE ROW EXCLUSIVE) avant la lecture et setval, l'avance ne recule
--      jamais même avec des ventes simultanées ; un échec par table (bloc EXCEPTION) est rendu dans la colonne « erreur »,
--      les autres tables sont avancées ; l'avance n'est notée faite que si aucune table n'a échoué (nouvel essai sinon).
--   8. sync_noter_alerte_retour_arriere : greatest(date, détection) (horloges du poste et de la base différentes).
--   9. sync_nouvelle_epoque(titulaire, plus grande époque connue du central, époque attendue) : refus si l'époque a changé ;
--      nouvelle époque = greatest(époque locale, époque du central) + 1. L'ancienne signature (bigint) est retirée.
-- Idempotent et rejouable sur une base plus récente ; aucune donnée existante modifiée (colonnes ajoutées vides).
-- Ordre de mise en production : APRÈS le script central 020 (archive des lignes remplacées, route des maximums).
-- A exécuter après 148_sauvegarde_alerte_sans_disque_externe.sql.
-- =====================================================================
SET client_encoding = 'UTF8';

-- ---- 1. Colonnes et garde ----
ALTER TABLE sync_parametre ADD COLUMN IF NOT EXISTS retour_arriere_le timestamptz;
ALTER TABLE sync_parametre ADD COLUMN IF NOT EXISTS retour_arriere_central bigint;
ALTER TABLE sync_parametre ADD COLUMN IF NOT EXISTS retour_arriere_sequences_le timestamptz;
ALTER TABLE sync_parametre ADD COLUMN IF NOT EXISTS retour_arriere_alerte_le timestamptz;

DO $garde$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'sync_parametre_retour_arriere_check'
                   AND conrelid = 'sync_parametre'::regclass) THEN
        ALTER TABLE sync_parametre ADD CONSTRAINT sync_parametre_retour_arriere_check CHECK (
            (retour_arriere_le IS NULL) = (retour_arriere_central IS NULL)
            AND (retour_arriere_central IS NULL OR retour_arriere_central >= 1)
            AND (retour_arriere_sequences_le IS NULL OR retour_arriere_le IS NOT NULL));
    END IF;
END $garde$;

COMMENT ON COLUMN sync_parametre.retour_arriere_le IS 'Base revenue en arrière détectée (le serveur central a reçu plus de lots que cette base n''en a formé) : envoi suspendu jusqu''à une nouvelle époque (script 149).';
COMMENT ON COLUMN sync_parametre.retour_arriere_central IS 'Dernier lot de l''époque en cours reçu par le serveur central, vu à la détection (script 149).';
COMMENT ON COLUMN sync_parametre.retour_arriere_sequences_le IS 'Séquences des tables envoyées avancées au-delà des n° connus du serveur central pour la détection en cours (script 149).';
COMMENT ON COLUMN sync_parametre.retour_arriere_alerte_le IS 'Dernière alerte « base revenue en arrière » envoyée au titulaire : une par détection (script 149).';

-- ---- 2. Détection ----
CREATE OR REPLACE FUNCTION sync_noter_retour_arriere(p_dernier_central bigint) RETURNS boolean
LANGUAGE plpgsql AS $$
DECLARE p sync_parametre;
BEGIN
    SELECT * INTO p FROM sync_parametre WHERE id = 1 FOR UPDATE;
    IF NOT FOUND OR p_dernier_central IS NULL OR p_dernier_central <= p.dernier_lot THEN RETURN false; END IF;
    IF p.retour_arriere_le IS NOT NULL THEN
        UPDATE sync_parametre SET retour_arriere_central = greatest(retour_arriere_central, p_dernier_central) WHERE id = 1;
        RETURN false;
    END IF;
    UPDATE sync_parametre SET retour_arriere_le = now(), retour_arriere_central = p_dernier_central, retour_arriere_sequences_le = NULL
    WHERE id = 1;
    RETURN true;
END $$;

-- Variante (correction 2026-11-18) : un lot jamais reformé refusé « empreinte différente » alors que le central a reçu au moins
-- ce n° prouve que cette base est revenue en arrière, même si le dernier lot formé ici n'est pas plus petit (lots reformés
-- depuis la restauration avec un autre contenu). Aucune comparaison à dernier_lot ; true à la première détection seulement.
CREATE OR REPLACE FUNCTION sync_noter_retour_arriere_refus(p_dernier_central bigint) RETURNS boolean
LANGUAGE plpgsql AS $$
DECLARE p sync_parametre;
BEGIN
    SELECT * INTO p FROM sync_parametre WHERE id = 1 FOR UPDATE;
    IF NOT FOUND OR p_dernier_central IS NULL OR p_dernier_central < 1 THEN RETURN false; END IF;
    IF p.retour_arriere_le IS NOT NULL THEN
        UPDATE sync_parametre SET retour_arriere_central = greatest(retour_arriere_central, p_dernier_central) WHERE id = 1;
        RETURN false;
    END IF;
    UPDATE sync_parametre SET retour_arriere_le = now(), retour_arriere_central = p_dernier_central, retour_arriere_sequences_le = NULL
    WHERE id = 1;
    RETURN true;
END $$;

-- ---- 3. Avance des séquences (jamais en arrière) ----
-- Version d'origine (sans colonne « erreur ») retirée : le type de retour change
DO $retrait$ BEGIN
    IF to_regprocedure('sync_avancer_sequences(jsonb)') IS NOT NULL
       -- to_regprocedure et non ::regprocedure : la conversion d'un nom inconnu échoue même quand la première condition est fausse
       -- (base neuve, rejeu de 001 à la fin : la fonction n'existe pas encore)
       AND pg_get_function_result(to_regprocedure('sync_avancer_sequences(jsonb)')) NOT LIKE '%erreur%' THEN
        DROP FUNCTION sync_avancer_sequences(jsonb);
    END IF;
END $retrait$;

-- Chaque table est verrouillée en SHARE ROW EXCLUSIVE avant la lecture de sa séquence et setval : aucune insertion (donc aucun
-- nextval par la valeur par défaut) ne peut s'intercaler, l'avance ne recule jamais. Aucun nextval explicite sur ces séquences
-- dans l'application (seulement des séquences de numérotation, hors champ). Attente du verrou bornée (lock_timeout 5 s) ;
-- une table en échec (verrou, valeur hors des bornes de la séquence…) est rendue avec son erreur, les autres sont avancées.
CREATE OR REPLACE FUNCTION sync_avancer_sequences(p_maximums jsonb)
RETURNS TABLE (nom_table text, avant bigint, apres bigint, erreur text)
LANGUAGE plpgsql AS $$
#variable_conflict use_column
DECLARE
    p          sync_parametre;
    r          record;
    v_seq      text;
    v_max      bigint;
    v_dernier  bigint;
    v_appele   boolean;
    v_actuel   bigint;
    v_echecs   int := 0;
BEGIN
    IF pg_is_in_recovery() THEN
        RAISE EXCEPTION 'Synchronisation : avance des n° impossible sur une base en lecture seule';
    END IF;
    IF p_maximums IS NULL OR jsonb_typeof(p_maximums) <> 'object' THEN
        RAISE EXCEPTION 'Synchronisation : maximums attendus sous la forme { table : n° }';
    END IF;
    SELECT * INTO p FROM sync_parametre WHERE id = 1 FOR UPDATE;
    PERFORM set_config('lock_timeout', '5s', true);
    FOR r IN
        SELECT s.nom_table AS t, m.value AS v
        FROM jsonb_each(p_maximums) m
        JOIN sync_table s ON s.nom_table = m.key
        JOIN pg_tables pt ON pt.schemaname = 'public' AND pt.tablename = s.nom_table
        WHERE sync_sens_effectif(s, p) IN ('montante', 'bidirectionnelle') AND s.cle_colonnes = '{id}'
        ORDER BY s.nom_table
    LOOP
        CONTINUE WHEN jsonb_typeof(r.v) <> 'number' OR (r.v #>> '{}') !~ '^[0-9]{1,18}$';
        v_max := (r.v #>> '{}')::bigint;
        CONTINUE WHEN v_max < 1;
        v_seq := pg_get_serial_sequence(format('public.%I', r.t), 'id');
        CONTINUE WHEN v_seq IS NULL;
        nom_table := r.t; avant := NULL; apres := NULL; erreur := NULL;
        BEGIN
            EXECUTE format('LOCK TABLE public.%I IN SHARE ROW EXCLUSIVE MODE', r.t);
            EXECUTE format('SELECT last_value, is_called FROM %s', v_seq) INTO v_dernier, v_appele;
            -- n° déjà attribué au plus : last_value si la séquence a servi, sinon last_value - 1
            v_actuel := CASE WHEN v_appele THEN v_dernier ELSE v_dernier - 1 END;
            avant := v_actuel;
            IF v_max > v_actuel THEN
                PERFORM setval(v_seq, v_max, true);
                apres := v_max;
            ELSE
                apres := v_actuel;
            END IF;
        EXCEPTION WHEN others THEN
            -- Sous-transaction annulée (verrou relâché, séquence inchangée) : la table est signalée, les suivantes sont traitées
            v_echecs := v_echecs + 1;
            avant := NULL; apres := NULL;
            erreur := SQLSTATE || ' ' || left(SQLERRM, 200);
        END;
        RETURN NEXT;
    END LOOP;
    IF p.retour_arriere_le IS NOT NULL AND v_echecs = 0 THEN
        UPDATE sync_parametre SET retour_arriere_sequences_le = now() WHERE id = 1;
    END IF;
END $$;

-- ---- 4. Alerte envoyée pour la détection en cours ----
CREATE OR REPLACE FUNCTION sync_noter_alerte_retour_arriere(p_quand timestamptz) RETURNS void
LANGUAGE sql AS $$
    -- greatest : jamais avant la détection (horloge du poste en retard sur celle de la base : sinon une alerte par minute)
    UPDATE sync_parametre SET retour_arriere_alerte_le = greatest(p_quand, retour_arriere_le) WHERE id = 1 AND retour_arriere_le IS NOT NULL
$$;

-- ---- 5. Nouvelle époque (titulaire) ----
-- Ancienne signature (titulaire seul) retirée : l'époque attendue et celle du central sont désormais exigées
DROP FUNCTION IF EXISTS sync_nouvelle_epoque(bigint);

-- p_epoque_central : plus grande époque connue du serveur central pour cette officine (NULL : serveur central sans ce
-- renseignement, époque locale seule) ; p_epoque_attendue : époque affichée à l'écran, refus si elle a changé.
-- Garde de rejeu (2026-11-19, lot S6.5.4) : définition remplacée par le script 151 ; rejouer ce script après 151
-- ne la ramène pas en arrière (marqueur : sync_declaration_epoque).
DO $rejeu$ BEGIN
    IF to_regclass('public.sync_declaration_epoque') IS NOT NULL THEN
        RAISE NOTICE 'sync_nouvelle_epoque : définition plus récente (script 151) gardée';
    ELSE
        EXECUTE $def$
CREATE OR REPLACE FUNCTION sync_nouvelle_epoque(p_utilisateur bigint, p_epoque_central int, p_epoque_attendue int)
RETURNS TABLE (epoque int, ancienne_epoque int, chargement int)
LANGUAGE plpgsql AS $$
#variable_conflict use_column
DECLARE p sync_parametre; n int; v_nouvelle int;
BEGIN
    IF pg_is_in_recovery() THEN
        RAISE EXCEPTION 'Synchronisation : nouvelle époque impossible sur une base en lecture seule';
    END IF;
    SELECT * INTO p FROM sync_parametre WHERE id = 1 FOR UPDATE;
    IF NOT FOUND THEN RAISE EXCEPTION 'Synchronisation : réglage absent (script 126)'; END IF;
    IF p.retour_arriere_le IS NULL THEN
        RAISE EXCEPTION 'Synchronisation : aucun retour en arrière détecté, nouvelle époque refusée'
            USING ERRCODE = 'check_violation';
    END IF;
    IF p_epoque_attendue IS NULL OR p_epoque_attendue <> p.epoque THEN
        RAISE EXCEPTION 'Synchronisation : l''époque a changé (attendue %, actuelle %), nouvelle époque refusée', p_epoque_attendue, p.epoque
            USING ERRCODE = 'check_violation';
    END IF;
    IF p_epoque_central IS NOT NULL AND (p_epoque_central < 0 OR p_epoque_central >= 2147483647) THEN
        RAISE EXCEPTION 'Synchronisation : époque du serveur central invalide (%)', p_epoque_central USING ERRCODE = 'check_violation';
    END IF;
    -- Jamais une époque déjà connue du central (deux restaurations successives, 2e serveur) : au-delà des deux
    v_nouvelle := greatest(p.epoque, coalesce(p_epoque_central, 0)) + 1;
    -- Chargement en cours de l'ancienne époque : son instantané (lignes de l'ancienne époque) ne partira jamais
    UPDATE sync_chargement c SET etat = 'remplace' WHERE c.etat NOT IN ('termine', 'remplace');
    UPDATE sync_parametre SET epoque = v_nouvelle, dernier_lot = 0, dernier_accuse = 0,
                              purge_epoque = NULL, purge_dernier_lot = 0,
                              retour_arriere_le = NULL, retour_arriere_central = NULL, retour_arriere_sequences_le = NULL,
                              derniere_erreur = NULL, derniere_erreur_le = NULL, modifie_le = now()
    WHERE id = 1;
    n := sync_demander_chargement('demande', p_utilisateur);
    epoque := v_nouvelle; ancienne_epoque := p.epoque; chargement := n;
    RETURN NEXT;
END $$;
$def$;
    END IF;
END $rejeu$;

COMMENT ON FUNCTION sync_noter_retour_arriere(bigint) IS 'Pose la détection « base revenue en arrière » (serveur central en avance sur cette base pour l''époque en cours) ; true à la première détection (script 149).';
COMMENT ON FUNCTION sync_avancer_sequences(jsonb) IS 'Avance (jamais en arrière, table verrouillée) les séquences id des tables envoyées au-delà des n° connus du serveur central ; échec rendu par table (script 149).';
COMMENT ON FUNCTION sync_nouvelle_epoque(bigint, int, int) IS 'Titulaire : nouvelle époque (au-delà de l''époque locale et de la plus grande connue du central) après un retour en arrière détecté, époque attendue contrôlée, puis chargement complet demandé (script 149).';
COMMENT ON FUNCTION sync_noter_retour_arriere_refus(bigint) IS 'Pose la détection « base revenue en arrière » sur refus « empreinte différente » d''un lot jamais reformé déjà reçu par le central (sans comparaison à dernier_lot) ; true à la première détection (script 149).';
