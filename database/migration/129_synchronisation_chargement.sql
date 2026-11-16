-- =====================================================================
-- 129 — Synchronisation, lot S6 (2026-11-16) : chargement initial et
-- rechargement complet. docs/conception-synchronisation.md § 13.
--   * sync_chargement : un chargement (activation, demande du titulaire,
--     réactivation, lots manquants au serveur central) ; un seul en cours.
--     États : demande -> constitution -> pret -> annonce -> termine
--     (ou remplace : un chargement plus récent l'a remplacé).
--   * Instantané : une ligne « instantane » par ligne existante de chaque
--     table envoyée, minimisée EXACTEMENT comme la file (sync_minimiser :
--     codes, colonnes exclues, textes filtrés), inscrite dans sync_sortie
--     avec ordre = repère (plus grand n° de file visible dans l'instantané)
--     et le n° du chargement.
--   * Ordre garanti (voir la conception) : l'application prend l'instantané
--     dans une transaction REPEATABLE READ dont l'image est exportée par une
--     transaction courte qui tient LOCK sync_sortie IN SHARE MODE : toute
--     modification visible a un n° <= repère, toute modification non visible
--     un n° > repère. Les lots sont formés par (ordre, n°) : les lignes de
--     l'instantané partent après les modifications qu'elles contiennent et
--     avant celles qui les suivent ; au serveur central, « plus grand
--     (époque, n°) gagne ».
--   * Pendant la constitution (état « constitution »), aucun lot n'est formé
--     (sync_enregistrer_lot renvoie NULL).
-- Idempotent ; aucune donnée existante modifiée ; synchronisation toujours
-- désactivée par défaut.
-- =====================================================================

ALTER TABLE sync_sortie ADD COLUMN IF NOT EXISTS ordre bigint;
ALTER TABLE sync_sortie ADD COLUMN IF NOT EXISTS chargement int;

ALTER TABLE sync_sortie DROP CONSTRAINT IF EXISTS sync_sortie_operation_check;
ALTER TABLE sync_sortie ADD CONSTRAINT sync_sortie_operation_check
    CHECK (operation IN ('creation', 'modification', 'suppression', 'instantane'));
ALTER TABLE sync_sortie DROP CONSTRAINT IF EXISTS ck_sync_sortie_instantane;
ALTER TABLE sync_sortie ADD CONSTRAINT ck_sync_sortie_instantane
    CHECK ((operation = 'instantane') = (chargement IS NOT NULL AND ordre IS NOT NULL));

-- Lignes en attente lues par (ordre, n°)
CREATE INDEX IF NOT EXISTS ix_sync_sortie_en_attente_ordre ON sync_sortie ((coalesce(ordre, id)), id) WHERE lot_numero IS NULL;
CREATE INDEX IF NOT EXISTS ix_sync_sortie_chargement ON sync_sortie (chargement) WHERE chargement IS NOT NULL;

CREATE TABLE IF NOT EXISTS sync_chargement (
    numero            int         GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    motif             text        NOT NULL CHECK (motif IN ('activation', 'demande', 'reactivation', 'lots_manquants')),
    etat              text        NOT NULL DEFAULT 'demande'
                                  CHECK (etat IN ('demande', 'constitution', 'pret', 'annonce', 'termine', 'remplace')),
    demande_le        timestamptz NOT NULL DEFAULT now(),
    demande_par       bigint,
    epoque            int,
    repere            bigint      CHECK (repere >= 0),
    tables            text[],
    lignes            jsonb,
    lignes_total      bigint      NOT NULL DEFAULT 0 CHECK (lignes_total >= 0),
    constitue_le      timestamptz,
    reprendre_apres   bigint,
    annonce_le        timestamptz,
    termine_le        timestamptz,
    absentes_central  bigint,
    absentes_detail   jsonb,
    CONSTRAINT ck_sync_chargement_pret CHECK (etat IN ('demande', 'constitution', 'remplace') OR (repere IS NOT NULL AND epoque IS NOT NULL))
);
CREATE UNIQUE INDEX IF NOT EXISTS ux_sync_chargement_en_cours ON sync_chargement ((true)) WHERE etat NOT IN ('termine', 'remplace');

INSERT INTO sync_table (nom_table, sens, colonnes_exclues, colonnes_patient, porteur, motif) VALUES
    ('sync_chargement', 'jamais', '{}', '{}', false, 'Synchronisation elle-même')
ON CONFLICT (nom_table) DO NOTHING;
UPDATE sync_table SET cle_colonnes = '{numero}' WHERE nom_table = 'sync_chargement' AND cle_colonnes IS DISTINCT FROM '{numero}';

-- ---------------------------------------------------------------------
-- Fonctions
-- ---------------------------------------------------------------------

-- Tables envoyées (sens effectif montante ou bidirectionnelle, médecins selon l'option), comme les déclencheurs
CREATE OR REPLACE FUNCTION sync_tables_envoyees() RETURNS SETOF text
LANGUAGE sql STABLE AS $$
    SELECT s.nom_table FROM sync_table s
    JOIN pg_tables t ON t.schemaname = 'public' AND t.tablename = s.nom_table
    JOIN sync_parametre p ON p.id = 1
    WHERE sync_sens_effectif(s, p) IN ('montante', 'bidirectionnelle')
    ORDER BY 1
$$;

-- Demande d'un chargement complet ; renvoie son n°, ou NULL si un chargement demandé par le titulaire est déjà en cours.
-- Un chargement en cours encore sans instantané (demande, constitution) sert la nouvelle demande. Un chargement dont
-- l'instantané est déjà pris est remplacé pour une réactivation ou des lots manquants (l'instantané serait périmé),
-- jamais pour une simple demande.
CREATE OR REPLACE FUNCTION sync_demander_chargement(p_motif text, p_utilisateur bigint) RETURNS int
LANGUAGE plpgsql AS $$
DECLARE c sync_chargement; n int;
BEGIN
    IF p_motif NOT IN ('activation', 'demande', 'reactivation', 'lots_manquants') THEN
        RAISE EXCEPTION 'Synchronisation : motif de chargement inconnu : %', p_motif;
    END IF;
    PERFORM 1 FROM sync_parametre WHERE id = 1 FOR UPDATE;
    SELECT * INTO c FROM sync_chargement WHERE etat NOT IN ('termine', 'remplace');
    IF FOUND THEN
        IF c.etat IN ('demande', 'constitution') THEN RETURN c.numero; END IF;
        IF p_motif IN ('demande', 'activation') THEN RETURN NULL; END IF;
        UPDATE sync_chargement SET etat = 'remplace' WHERE numero = c.numero;
    END IF;
    INSERT INTO sync_chargement (motif, demande_par) VALUES (p_motif, p_utilisateur) RETURNING numero INTO n;
    RETURN n;
END $$;

-- Avant l'instantané (transaction courte, validée) : état « constitution » (aucun lot formé), codes créés d'avance pour
-- tous les patients et porteurs (l'instantané, en REPEATABLE READ, ne crée alors presque jamais de code lui-même).
CREATE OR REPLACE FUNCTION sync_chargement_preparer(p_numero int) RETURNS int
LANGUAGE plpgsql AS $$
DECLARE n int;
BEGIN
    UPDATE sync_chargement SET etat = 'constitution' WHERE numero = p_numero AND etat IN ('demande', 'constitution');
    IF NOT FOUND THEN RAISE EXCEPTION 'Synchronisation : chargement % absent ou déjà constitué', p_numero; END IF;
    PERFORM sync_code_patient(id) FROM patient;
    PERFORM sync_code_porteur(porteur_nom, porteur_piece) FROM registre_psychotrope;
    PERFORM sync_code_porteur(porteur_nom, porteur_piece) FROM journal_registre_psychotrope;
    SELECT count(*) INTO n FROM sync_tables_envoyees();
    RETURN n;
END $$;

-- Instantané d'une table (appelé dans la transaction REPEATABLE READ de l'instantané) : une ligne « instantane » par ligne,
-- minimisée comme par le déclencheur, ordre = repère ; renvoie le nombre de lignes inscrites.
CREATE OR REPLACE FUNCTION sync_instantane_table(p_chargement int, p_table text, p_repere bigint) RETURNS bigint
LANGUAGE plpgsql AS $$
DECLARE t sync_table; p sync_parametre; v_ordre text; n bigint;
BEGIN
    SELECT * INTO p FROM sync_parametre WHERE id = 1;
    SELECT * INTO t FROM sync_table WHERE nom_table = p_table;
    IF NOT FOUND OR sync_sens_effectif(t, p) NOT IN ('montante', 'bidirectionnelle')
       OR NOT EXISTS (SELECT 1 FROM pg_tables WHERE schemaname = 'public' AND tablename = p_table) THEN
        RAISE EXCEPTION 'Synchronisation : table % non envoyée', p_table;
    END IF;
    SELECT string_agg(format('x.%I', c), ', ' ORDER BY k) INTO v_ordre FROM unnest(t.cle_colonnes) WITH ORDINALITY u(c, k);
    EXECUTE format($f$
        INSERT INTO sync_sortie (nom_table, id_local, operation, donnees, version_contrat, epoque, ordre, chargement)
        SELECT %L,
               (SELECT string_agg(coalesce(m.j ->> k.col, ''), '|' ORDER BY k.n) FROM unnest($1::text[]) WITH ORDINALITY AS k(col, n)),
               'instantane', m.j, $2, $3, $4, $5
        FROM (SELECT sync_minimiser($6, to_jsonb(x)) AS j FROM public.%I x ORDER BY %s) m$f$, p_table, p_table, v_ordre)
    USING t.cle_colonnes, p.version_contrat, p.epoque, p_repere, p_chargement, t;
    GET DIAGNOSTICS n = ROW_COUNT;
    RETURN n;
END $$;

-- Fin de l'instantané (même transaction) : état « pret », repère, comptes par table
CREATE OR REPLACE FUNCTION sync_chargement_pret(p_numero int, p_repere bigint, p_lignes jsonb) RETURNS void
LANGUAGE plpgsql AS $$
BEGIN
    UPDATE sync_chargement
    SET etat = 'pret', repere = p_repere, epoque = (SELECT epoque FROM sync_parametre WHERE id = 1),
        tables = ARRAY(SELECT jsonb_object_keys(p_lignes) ORDER BY 1), lignes = p_lignes,
        lignes_total = (SELECT coalesce(sum(v::bigint), 0) FROM jsonb_each_text(p_lignes) AS e(k, v)),
        constitue_le = now()
    WHERE numero = p_numero AND etat = 'constitution';
    IF NOT FOUND THEN RAISE EXCEPTION 'Synchronisation : chargement % n''est pas en constitution', p_numero; END IF;
END $$;

-- Début accusé par le serveur central
CREATE OR REPLACE FUNCTION sync_chargement_annonce(p_numero int, p_reprendre_apres bigint) RETURNS void
LANGUAGE plpgsql AS $$
BEGIN
    UPDATE sync_chargement SET etat = 'annonce', annonce_le = coalesce(annonce_le, now()), reprendre_apres = p_reprendre_apres
    WHERE numero = p_numero AND etat IN ('pret', 'annonce');
    IF NOT FOUND THEN RAISE EXCEPTION 'Synchronisation : chargement % non prêt', p_numero; END IF;
END $$;

-- Toutes les lignes de l'instantané sont dans des lots accusés
CREATE OR REPLACE FUNCTION sync_chargement_accuse(p_numero int) RETURNS boolean
LANGUAGE sql STABLE AS $$
    SELECT NOT EXISTS (SELECT 1 FROM sync_sortie s
                       LEFT JOIN sync_lot l ON l.epoque = s.epoque AND l.numero = s.lot_numero
                       WHERE s.chargement = p_numero AND (s.lot_numero IS NULL OR l.accuse_le IS NULL))
$$;

-- Fin accusée par le serveur central : lignes du serveur central absentes de l'officine (signalées et gardées là-bas)
CREATE OR REPLACE FUNCTION sync_chargement_terminer(p_numero int, p_absentes bigint, p_detail jsonb) RETURNS void
LANGUAGE plpgsql AS $$
BEGIN
    UPDATE sync_chargement SET etat = 'termine', termine_le = now(), absentes_central = p_absentes, absentes_detail = p_detail
    WHERE numero = p_numero AND etat = 'annonce';
    IF NOT FOUND THEN RAISE EXCEPTION 'Synchronisation : chargement % non annoncé', p_numero; END IF;
END $$;

-- Progression : lignes inscrites, rattachées à un lot, dans un lot accusé
CREATE OR REPLACE FUNCTION sync_chargement_progression(p_numero int)
RETURNS TABLE (inscrites bigint, en_lot bigint, accusees bigint)
LANGUAGE sql STABLE AS $$
    SELECT count(*), count(*) FILTER (WHERE s.lot_numero IS NOT NULL), count(*) FILTER (WHERE l.accuse_le IS NOT NULL)
    FROM sync_sortie s LEFT JOIN sync_lot l ON l.epoque = s.epoque AND l.numero = s.lot_numero
    WHERE s.chargement = p_numero
$$;

-- Constitution d'un lot : refusée (NULL) pendant la constitution d'un instantané (sinon une modification postérieure
-- pourrait partir avant les lignes de l'instantané qu'elle suit)
-- Rejouable : sur une base où le script 130 est passé (version à 5 arguments présente), l'ancienne signature est déjà la
-- version finale (appel de la version à 5 arguments) ; la remplacer par cette version ramènerait une définition périmée.
DO $garde$
BEGIN
    IF to_regprocedure('sync_enregistrer_lot(bigint,bigint[],integer,text,text[])') IS NULL THEN
        EXECUTE $f$
CREATE OR REPLACE FUNCTION sync_enregistrer_lot(p_numero bigint, p_lignes bigint[], p_octets int, p_empreinte text)
RETURNS bigint LANGUAGE plpgsql AS $$
DECLARE p sync_parametre; n int;
BEGIN
    SELECT * INTO p FROM sync_parametre WHERE id = 1 FOR UPDATE;
    IF p.dernier_lot + 1 <> p_numero OR coalesce(array_length(p_lignes, 1), 0) = 0 THEN RETURN NULL; END IF;
    IF EXISTS (SELECT 1 FROM sync_chargement WHERE etat = 'constitution') THEN RETURN NULL; END IF;
    INSERT INTO sync_lot (epoque, numero, version_contrat, nb_lignes, octets, empreinte)
    VALUES (p.epoque, p_numero, p.version_contrat, array_length(p_lignes, 1), p_octets, p_empreinte);
    UPDATE sync_sortie SET lot_numero = p_numero
    WHERE id = ANY (p_lignes) AND lot_numero IS NULL AND epoque = p.epoque;
    GET DIAGNOSTICS n = ROW_COUNT;
    IF n <> array_length(p_lignes, 1) THEN
        RAISE EXCEPTION 'Synchronisation : lot % incohérent (% lignes rattachées sur %)', p_numero, n, array_length(p_lignes, 1);
    END IF;
    UPDATE sync_parametre SET dernier_lot = p_numero WHERE id = 1;
    RETURN p_numero;
END $$;
        $f$;
    END IF;
END $garde$;

COMMENT ON TABLE sync_chargement IS 'Synchronisation : chargements complets (activation, demande, réactivation, lots manquants), un seul en cours (script 129).';
COMMENT ON COLUMN sync_sortie.ordre IS 'Ordre d''envoi d''une ligne d''instantané (repère du chargement) ; NULL = n° de la ligne (script 129).';
