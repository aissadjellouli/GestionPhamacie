-- =====================================================================
-- 136 — Corrections après revue et audit de sécurité (2026-11-16,
-- docs/conception-synchronisation.md § 14 et § 15).
--   1. Lots : colonnes retirées FIGÉES à la constitution
--      (sync_lot.colonnes_retirees, « table.colonne ») : le renvoi à
--      l'identique ne dépend plus du catalogue des familles (une colonne
--      ajoutée ensuite à une famille ne rend plus un lot non renvoyable) ;
--      une reforme ne fait que retirer davantage (garde de sync_lot).
--      sync_constituer_lot, sync_reformer_lot_colonnes,
--      sync_colonnes_interdites, sync_colonnes_hors_familles,
--      sync_retirer_colonnes. Lots antérieurs (colonne NULL) : traités par
--      l'application de façon sûre (reconstitués ou reformés).
--   2. Voie descendante : sync_recevoir_descendant (renvoie un statut, ne
--      lève plus pour une table inconnue) ; refuse une version au-delà de la
--      version maximale annoncée ou un saut anormal
--      (sync_parametre.descendant_saut_max, 1000) ; journal local
--      sync_journal_descendant (statut, versions, familles avant/après, sans
--      donnée personnelle) ; version acquittée notée à part
--      (sync_noter_version_descendante).
--   3. Contenance (script 134) : motif sans dépendance à la locale (classes
--      accentuées écrites en entier), fonction contenance_depuis_conditionnement ;
--      nouveau passage pour les seuls produits encore sans contenance
--      (journal_audit comme 134).
-- Idempotent ; aucune donnée existante perdue ; synchronisation toujours
-- désactivée par défaut.
-- =====================================================================

-- ---- 1. Colonnes retirées figées ----
ALTER TABLE sync_lot ADD COLUMN IF NOT EXISTS colonnes_retirees text[];

CREATE OR REPLACE FUNCTION sync_colonnes_hors_familles(p_familles text[]) RETURNS text[]
LANGUAGE sql STABLE AS $$
    SELECT coalesce(array_agg(f.nom_table || '.' || f.colonne ORDER BY f.nom_table || '.' || f.colonne COLLATE "C"), '{}'::text[])
    FROM sync_famille_colonne f WHERE NOT f.famille = ANY (coalesce(p_familles, '{}'::text[]))
$$;

-- Colonnes à retirer maintenant (familles non autorisées par le réglage reçu)
CREATE OR REPLACE FUNCTION sync_colonnes_interdites() RETURNS text[]
LANGUAGE sql STABLE AS $$ SELECT sync_colonnes_hors_familles(sync_familles_effectives()) $$;

-- Données sans les colonnes listées (« table.colonne ») ; p_colonnes NULL : aucun retrait
CREATE OR REPLACE FUNCTION sync_retirer_colonnes(p_table text, p_donnees jsonb, p_colonnes text[]) RETURNS jsonb
LANGUAGE sql IMMUTABLE AS $$
    SELECT CASE WHEN p_donnees IS NULL OR p_colonnes IS NULL THEN p_donnees
                ELSE p_donnees - ARRAY(SELECT split_part(c, '.', 2) FROM unnest(p_colonnes) c WHERE split_part(c, '.', 1) = p_table) END
$$;

-- Lots : accusé, reforme par familles (130), ou reforme par colonnes (ne retire que davantage)
CREATE OR REPLACE FUNCTION sync_lot_garde() RETURNS trigger
LANGUAGE plpgsql AS $$
BEGIN
    IF TG_OP = 'UPDATE' AND OLD.accuse_le IS NULL AND NEW.accuse_le IS NOT NULL
       AND (to_jsonb(NEW) - 'accuse_le') = (to_jsonb(OLD) - 'accuse_le') THEN
        RETURN NEW;
    END IF;
    IF TG_OP = 'UPDATE' AND OLD.accuse_le IS NULL AND NEW.accuse_le IS NULL AND NEW.familles IS NOT NULL
       AND (OLD.familles IS NULL OR (NEW.familles <@ OLD.familles AND NOT OLD.familles <@ NEW.familles))
       AND (to_jsonb(NEW) - ARRAY['empreinte', 'octets', 'familles', 'reforme_le'])
         = (to_jsonb(OLD) - ARRAY['empreinte', 'octets', 'familles', 'reforme_le']) THEN
        RETURN NEW;
    END IF;
    IF TG_OP = 'UPDATE' AND OLD.accuse_le IS NULL AND NEW.accuse_le IS NULL AND NEW.colonnes_retirees IS NOT NULL
       AND (OLD.colonnes_retirees IS NULL OR (OLD.colonnes_retirees <@ NEW.colonnes_retirees AND NOT NEW.colonnes_retirees <@ OLD.colonnes_retirees))
       AND (to_jsonb(NEW) - ARRAY['empreinte', 'octets', 'colonnes_retirees', 'reforme_le'])
         = (to_jsonb(OLD) - ARRAY['empreinte', 'octets', 'colonnes_retirees', 'reforme_le']) THEN
        RETURN NEW;
    END IF;
    RAISE EXCEPTION 'sync_lot : lot figé (seul l''accusé peut être enregistré)';
END $$;

-- Constitution d'un lot : colonnes retirées = colonnes interdites au moment de la lecture des lignes (sinon NULL : reprise)
CREATE OR REPLACE FUNCTION sync_constituer_lot(p_numero bigint, p_lignes bigint[], p_octets int, p_empreinte text, p_colonnes text[])
RETURNS bigint LANGUAGE plpgsql AS $$
DECLARE p sync_parametre; n int; v_cols text[] := sync_colonnes_interdites();
BEGIN
    SELECT * INTO p FROM sync_parametre WHERE id = 1 FOR UPDATE;
    IF p.dernier_lot + 1 <> p_numero OR coalesce(array_length(p_lignes, 1), 0) = 0 THEN RETURN NULL; END IF;
    IF EXISTS (SELECT 1 FROM sync_chargement WHERE etat = 'constitution') THEN RETURN NULL; END IF;
    IF ARRAY(SELECT x FROM unnest(coalesce(p_colonnes, '{}'::text[])) x ORDER BY x COLLATE "C") IS DISTINCT FROM v_cols THEN RETURN NULL; END IF;
    INSERT INTO sync_lot (epoque, numero, version_contrat, nb_lignes, octets, empreinte, familles, colonnes_retirees)
    VALUES (p.epoque, p_numero, p.version_contrat, array_length(p_lignes, 1), p_octets, p_empreinte, sync_familles_effectives(), v_cols);
    UPDATE sync_sortie SET lot_numero = p_numero
    WHERE id = ANY (p_lignes) AND lot_numero IS NULL AND epoque = p.epoque;
    GET DIAGNOSTICS n = ROW_COUNT;
    IF n <> array_length(p_lignes, 1) THEN
        RAISE EXCEPTION 'Synchronisation : lot % incohérent (% lignes rattachées sur %)', p_numero, n, array_length(p_lignes, 1);
    END IF;
    UPDATE sync_parametre SET dernier_lot = p_numero WHERE id = 1;
    RETURN p_numero;
END $$;

-- Reforme d'un lot non accusé : colonnes retirées en plus (jamais moins)
CREATE OR REPLACE FUNCTION sync_reformer_lot_colonnes(p_epoque int, p_numero bigint, p_empreinte text, p_octets int, p_colonnes text[])
RETURNS void LANGUAGE plpgsql AS $$
BEGIN
    UPDATE sync_lot SET empreinte = p_empreinte, octets = p_octets, reforme_le = now(),
                        colonnes_retirees = ARRAY(SELECT DISTINCT x COLLATE "C" FROM unnest(p_colonnes) x ORDER BY 1)
    WHERE epoque = p_epoque AND numero = p_numero AND accuse_le IS NULL;
    IF NOT FOUND THEN RAISE EXCEPTION 'Synchronisation : lot %/% absent ou déjà accusé', p_epoque, p_numero; END IF;
END $$;

-- ---- 2. Voie descendante ----
ALTER TABLE sync_parametre ADD COLUMN IF NOT EXISTS descendant_saut_max bigint NOT NULL DEFAULT 1000;
DO $$ BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'sync_parametre_descendant_saut_max_check') THEN
        ALTER TABLE sync_parametre ADD CONSTRAINT sync_parametre_descendant_saut_max_check CHECK (descendant_saut_max >= 1);
    END IF;
END $$;

CREATE TABLE IF NOT EXISTS sync_journal_descendant (
    id          bigint      GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    le          timestamptz NOT NULL DEFAULT now(),
    nom_table   text        NOT NULL,
    cle         text        NOT NULL,
    version     bigint,
    statut      text        NOT NULL CHECK (statut IN ('applique', 'table_inconnue', 'refuse')),
    motif       text,
    avant       jsonb,
    apres       jsonb
);
INSERT INTO sync_table (nom_table, sens, colonnes_exclues, colonnes_patient, porteur, motif) VALUES
    ('sync_journal_descendant', 'jamais', '{}', '{}', false, 'Synchronisation elle-même (journal de la voie descendante, script 136)')
ON CONFLICT (nom_table) DO NOTHING;

-- Élément reçu : 'applique', 'ancien' (version déjà là ou plus récente), 'table_inconnue' ou 'refuse' ; jamais d'exception
-- pour un élément mal formé ; la version acquittée n'est PAS modifiée ici (sync_noter_version_descendante)
CREATE OR REPLACE FUNCTION sync_recevoir_descendant(p_table text, p_cle text, p_version bigint, p_donnees jsonb, p_version_max bigint)
RETURNS text LANGUAGE plpgsql AS $$
DECLARE p sync_parametre; v_avant jsonb; n int;
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
    SELECT valeur INTO v_avant FROM reglage_central WHERE cle = p_cle;
    INSERT INTO reglage_central AS r (cle, version, valeur) VALUES (p_cle, p_version, p_donnees)
    ON CONFLICT (cle) DO UPDATE SET version = EXCLUDED.version, valeur = EXCLUDED.valeur, recu_le = now()
    WHERE r.version < EXCLUDED.version;
    GET DIAGNOSTICS n = ROW_COUNT;
    IF n = 0 THEN RETURN 'ancien'; END IF;
    INSERT INTO sync_journal_descendant (nom_table, cle, version, statut, avant, apres)
    VALUES (p_table, p_cle, p_version, 'applique',
            CASE WHEN p_cle = 'familles_envoyees' THEN v_avant -> 'familles' END,
            CASE WHEN p_cle = 'familles_envoyees' THEN p_donnees -> 'familles' END);
    RETURN 'applique';
END $$;

CREATE OR REPLACE FUNCTION sync_noter_version_descendante(p_version bigint) RETURNS bigint
LANGUAGE sql AS $$
    UPDATE sync_parametre SET descendant_version = greatest(descendant_version, coalesce(p_version, 0)) WHERE id = 1 RETURNING descendant_version
$$;

-- ---- 3. Contenance : motif sans dépendance à la locale ----
CREATE OR REPLACE FUNCTION contenance_depuis_conditionnement(p_conditionnement text) RETURNS int
LANGUAGE sql IMMUTABLE AS $$
    -- motif de ContenanceProduit.Boite(), recopié tel quel (test ContenanceDepuisNomenclatureTests)
    SELECT substring(p_conditionnement FROM '(?i)^\s*(?:B|BTE|BO[IÎiî]TE)\s*(?:/|\s+DE\s+)\s*([0-9]{1,5})(?:\s*(?:SACHETS?(?:(?:\s+|-)DOSES?)?|SACH|SCH|AMPOULES?|AMPS?|AMP\.?\s+BUV|CPR?S?|CP\.?\s+EFF|COMPRIM[EÉeé]S?(?:\s+EFFERVESCENTS?)?|G[EÉeé]LULES?|SUPPOSITOIRES?|SUPPO|OVULES?|CAPSULES?|PATCH(?:E?S)?|DISPOSITIFS?\s+TRANSDERMIQUES?|UNIDOSES?|R[EÉeé]CIPIENTS\s+UNIDOSES)\.?)?\s*$')::int
$$;

WITH lignes AS (
    SELECT p.id, contenance_depuis_conditionnement(n.conditionnement) AS nombre
    FROM produit p
    JOIN nomenclature_produit n ON n.num_enregistrement = p.num_enregistrement
     AND n.version_id = (SELECT max(x.version_id) FROM nomenclature_produit x WHERE x.num_enregistrement = p.num_enregistrement)
    WHERE p.contenance IS NULL
), sures AS (
    SELECT id, min(nombre) AS valeur
    FROM lignes
    GROUP BY id
    HAVING bool_and(coalesce(nombre BETWEEN 1 AND 10000, false))
       AND count(DISTINCT nombre) = 1
       AND min(nombre) <= 1000
), maj AS (
    UPDATE produit p SET contenance = s.valeur
    FROM sures s
    WHERE p.id = s.id AND p.contenance IS NULL
    RETURNING p.id, p.nom_commercial, p.contenance, p.officine_id
)
INSERT INTO journal_audit (utilisateur_id, action, table_cible, enregistrement_id, details, officine_id)
SELECT NULL, 'produit_modifie', 'produit', m.id,
       jsonb_build_object('produit', m.nom_commercial,
                          'contenance', jsonb_build_object('avant', NULL, 'apres', m.contenance),
                          'motif', 'contenance depuis la nomenclature, script 136'),
       m.officine_id
FROM maj m;

COMMENT ON COLUMN sync_lot.colonnes_retirees IS 'Colonnes (« table.colonne ») retirées à la constitution, figées pour le renvoi à l''identique ; NULL : lot antérieur au script 136 (script 136).';
COMMENT ON TABLE sync_journal_descendant IS 'Synchronisation : journal local de la voie descendante (appliqué, ignoré, refusé), sans donnée personnelle (script 136).';
