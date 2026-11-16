-- =====================================================================
-- 131 — Synchronisation : voie descendante (2026-11-16, première brique
-- de S3, docs/conception-synchronisation.md § 15).
--   * reglage_central (classée DESCENDANTE) : réglages décidés au serveur
--     central et descendus par la synchronisation (clé, version, valeur).
--     Premier réglage : familles_envoyees (familles nominatives autorisées,
--     script 130). L'API locale le lit UNIQUEMENT ici : aucun appel au
--     serveur central pour l'obtenir, aucun blocage s'il est en panne
--     (dernière version appliquée).
--   * sync_appliquer_descendant : écriture idempotente d'un élément reçu ;
--     jamais écrasé par une version plus ancienne ou égale ; table non
--     prévue refusée. sync_parametre.descendant_version = plus grande
--     version appliquée (redonnée au serveur central : acquittement).
--   * sync_familles_effectives() lit reglage_central ; rien reçu : aucune
--     famille (rien de nominatif ne part, comme avant).
--   * Remplace le cache du script 130 (sync_parametre.familles_envoyees,
--     familles_lues_le, sync_noter_familles), retirés : simple cache,
--     jamais rempli sur les bases existantes.
-- Idempotent ; aucune donnée métier modifiée ; synchronisation toujours
-- désactivée par défaut.
-- =====================================================================

CREATE TABLE IF NOT EXISTS reglage_central (
    cle          text        PRIMARY KEY CHECK (cle ~ '^[a-z_]{1,63}$'),
    version      bigint      NOT NULL CHECK (version >= 1),
    valeur       jsonb       NOT NULL CHECK (jsonb_typeof(valeur) = 'object'),
    recu_le      timestamptz NOT NULL DEFAULT now(),
    officine_id  uuid        NOT NULL DEFAULT officine_courante()
);

INSERT INTO sync_table (nom_table, sens, colonnes_exclues, colonnes_patient, porteur, motif) VALUES
    ('reglage_central', 'descendante', '{}', '{}', false, 'Réglages décidés au serveur central (familles nominatives…), script 131')
ON CONFLICT (nom_table) DO NOTHING;
UPDATE sync_table SET cle_colonnes = '{cle}' WHERE nom_table = 'reglage_central' AND cle_colonnes IS DISTINCT FROM '{cle}';

ALTER TABLE sync_parametre ADD COLUMN IF NOT EXISTS descendant_version bigint NOT NULL DEFAULT 0;
DROP FUNCTION IF EXISTS sync_noter_familles(text[]);
ALTER TABLE sync_parametre DROP COLUMN IF EXISTS familles_envoyees;
ALTER TABLE sync_parametre DROP COLUMN IF EXISTS familles_lues_le;

-- Élément descendant reçu : appliqué si sa version est plus grande que celle déjà là ; renvoie true s'il a été appliqué
CREATE OR REPLACE FUNCTION sync_appliquer_descendant(p_table text, p_cle text, p_version bigint, p_donnees jsonb) RETURNS boolean
LANGUAGE plpgsql AS $$
DECLARE n int;
BEGIN
    IF p_version IS NULL OR p_version < 1 OR p_donnees IS NULL OR jsonb_typeof(p_donnees) <> 'object' THEN
        RAISE EXCEPTION 'Synchronisation : élément descendant mal formé (%/%)', p_table, p_cle;
    END IF;
    IF p_table = 'reglage_central' THEN
        INSERT INTO reglage_central AS r (cle, version, valeur) VALUES (p_cle, p_version, p_donnees)
        ON CONFLICT (cle) DO UPDATE SET version = EXCLUDED.version, valeur = EXCLUDED.valeur, recu_le = now()
        WHERE r.version < EXCLUDED.version;
        GET DIAGNOSTICS n = ROW_COUNT;
    ELSE
        RAISE EXCEPTION 'Synchronisation : table descendante non prévue : %', p_table;
    END IF;
    UPDATE sync_parametre SET descendant_version = greatest(descendant_version, p_version) WHERE id = 1;
    RETURN n > 0;
END $$;

-- Familles envoyées maintenant (triées), lues dans la base locale ; rien reçu : aucune
CREATE OR REPLACE FUNCTION sync_familles_effectives() RETURNS text[]
LANGUAGE sql STABLE AS $$
    SELECT coalesce((SELECT array_agg(x ORDER BY x COLLATE "C")
                     FROM reglage_central r, jsonb_array_elements_text(r.valeur -> 'familles') x
                     WHERE r.cle = 'familles_envoyees' AND EXISTS (SELECT 1 FROM sync_famille_colonne f WHERE f.famille = x)), '{}'::text[])
$$;

-- Constitution d'un lot (remplace la version de 130) : familles comparées au réglage local reçu
CREATE OR REPLACE FUNCTION sync_enregistrer_lot(p_numero bigint, p_lignes bigint[], p_octets int, p_empreinte text, p_familles text[])
RETURNS bigint LANGUAGE plpgsql AS $$
DECLARE p sync_parametre; n int; v_familles text[] := sync_familles_effectives();
BEGIN
    SELECT * INTO p FROM sync_parametre WHERE id = 1 FOR UPDATE;
    IF p.dernier_lot + 1 <> p_numero OR coalesce(array_length(p_lignes, 1), 0) = 0 THEN RETURN NULL; END IF;
    IF EXISTS (SELECT 1 FROM sync_chargement WHERE etat = 'constitution') THEN RETURN NULL; END IF;
    IF p_familles IS NOT NULL AND ARRAY(SELECT x FROM unnest(p_familles) x ORDER BY x COLLATE "C") IS DISTINCT FROM v_familles THEN
        RETURN NULL;
    END IF;
    INSERT INTO sync_lot (epoque, numero, version_contrat, nb_lignes, octets, empreinte, familles)
    VALUES (p.epoque, p_numero, p.version_contrat, array_length(p_lignes, 1), p_octets, p_empreinte,
            CASE WHEN p_familles IS NULL THEN NULL ELSE v_familles END);
    UPDATE sync_sortie SET lot_numero = p_numero
    WHERE id = ANY (p_lignes) AND lot_numero IS NULL AND epoque = p.epoque;
    GET DIAGNOSTICS n = ROW_COUNT;
    IF n <> array_length(p_lignes, 1) THEN
        RAISE EXCEPTION 'Synchronisation : lot % incohérent (% lignes rattachées sur %)', p_numero, n, array_length(p_lignes, 1);
    END IF;
    UPDATE sync_parametre SET dernier_lot = p_numero WHERE id = 1;
    RETURN p_numero;
END $$;

COMMENT ON TABLE reglage_central IS 'Synchronisation : réglages décidés au serveur central, descendus par la synchronisation, jamais écrasés par une version plus ancienne (script 131).';
COMMENT ON COLUMN sync_parametre.descendant_version IS 'Plus grande version descendante appliquée (acquittement redonné au serveur central) (script 131).';
