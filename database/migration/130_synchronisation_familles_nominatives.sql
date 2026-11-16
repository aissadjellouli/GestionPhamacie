-- =====================================================================
-- 130 — Synchronisation : familles de données nominatives réglées au
-- serveur central (2026-11-16, docs/conception-synchronisation.md § 14).
--   * sync_famille_colonne : colonnes de chaque famille (même liste que
--     Pharma.Shared.FamillesNominativesSync et que la base centrale,
--     script central 004) :
--       employes            utilisateur.nom_complet, utilisateur.identifiant
--       pharmacie           pharmacie.titulaire, .adresse, .telephone,
--                           parametre_notification.smtp_expediteur
--       fournisseurs        fournisseur.telephone, .email, .adresse
--       courriel_commandes  commande_fournisseur.envoyee_email_a
--   * sync_parametre.familles_envoyees : réglage effectif lu auprès du
--     serveur central à chaque passage d'envoi (cache). NULL = jamais lu :
--     AUCUNE famille n'est envoyée tant que le réglage n'est pas connu.
--   * Filtre à la CONSTITUTION des lots (sync_retirer_familles), pas dans
--     la file : la file garde la ligne complète, dans l'officine ; une
--     ligne déjà en file quand une famille est décochée est donc filtrée
--     elle aussi (sync_sortie est en ajout seul, elle ne se réécrit pas).
--     Chaque lot garde les familles appliquées (sync_lot.familles) pour
--     être renvoyé à l'identique ; si une famille a été décochée depuis et
--     que le lot n'est pas accusé, il est reformé SANS elle
--     (sync_reformer_lot : seul changement permis en plus de l'accusé,
--     familles en réduction stricte seulement).
-- Idempotent ; aucune donnée existante modifiée ; synchronisation
-- toujours désactivée par défaut.
-- Garde (ajoutée le 2026-11-16 après revue) : rejoué seul sur une base où
-- le script 131 est déjà passé, ce script ne fait RIEN (il réinstallerait
-- l'ancien cache sync_parametre.familles_envoyees et d'anciennes fonctions).
-- =====================================================================

SELECT to_regclass('public.reglage_central') IS NOT NULL AS sync_131_deja_passe \gset
\if :sync_131_deja_passe
\echo 'Script 130 : déjà remplacé par le script 131 (voie descendante), rien à faire.'
\quit
\endif

CREATE TABLE IF NOT EXISTS sync_famille_colonne (
    famille    text NOT NULL CHECK (famille IN ('employes', 'pharmacie', 'fournisseurs', 'courriel_commandes')),
    nom_table  text NOT NULL,
    colonne    text NOT NULL,
    PRIMARY KEY (famille, nom_table, colonne)
);
CREATE INDEX IF NOT EXISTS ix_sync_famille_colonne_table ON sync_famille_colonne (nom_table);

INSERT INTO sync_famille_colonne (famille, nom_table, colonne) VALUES
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

INSERT INTO sync_table (nom_table, sens, colonnes_exclues, colonnes_patient, porteur, motif) VALUES
    ('sync_famille_colonne', 'jamais', '{}', '{}', false, 'Synchronisation elle-même')
ON CONFLICT (nom_table) DO NOTHING;
UPDATE sync_table SET cle_colonnes = '{famille,nom_table,colonne}'
WHERE nom_table = 'sync_famille_colonne' AND cle_colonnes IS DISTINCT FROM '{famille,nom_table,colonne}';

ALTER TABLE sync_parametre ADD COLUMN IF NOT EXISTS familles_envoyees text[];
ALTER TABLE sync_parametre ADD COLUMN IF NOT EXISTS familles_lues_le timestamptz;
ALTER TABLE sync_lot ADD COLUMN IF NOT EXISTS familles text[];
ALTER TABLE sync_lot ADD COLUMN IF NOT EXISTS reforme_le timestamptz;

-- ---------------------------------------------------------------------
-- Fonctions
-- ---------------------------------------------------------------------

-- Familles envoyées maintenant (triées) ; réglage jamais lu : aucune
CREATE OR REPLACE FUNCTION sync_familles_effectives() RETURNS text[]
LANGUAGE sql STABLE AS $$
    SELECT coalesce((SELECT familles_envoyees FROM sync_parametre WHERE id = 1), '{}'::text[])
$$;

-- Données d'une ligne de file sans les colonnes des familles non autorisées. p_familles NULL : lot constitué avant ce
-- script (aucun retrait, pour le renvoyer à l'identique).
CREATE OR REPLACE FUNCTION sync_retirer_familles(p_table text, p_donnees jsonb, p_familles text[]) RETURNS jsonb
LANGUAGE sql STABLE AS $$
    SELECT CASE WHEN p_donnees IS NULL OR p_familles IS NULL THEN p_donnees
                ELSE p_donnees - coalesce((SELECT array_agg(f.colonne) FROM sync_famille_colonne f
                                           WHERE f.nom_table = p_table AND NOT f.famille = ANY (p_familles)), '{}'::text[])
           END
$$;

-- Réglage lu auprès du serveur central : familles connues ici seulement, triées ; renvoie les familles retenues
CREATE OR REPLACE FUNCTION sync_noter_familles(p_familles text[]) RETURNS text[]
LANGUAGE plpgsql AS $$
DECLARE v text[];
BEGIN
    SELECT coalesce(array_agg(DISTINCT x COLLATE "C" ORDER BY x COLLATE "C"), '{}'::text[]) INTO v
    FROM unnest(coalesce(p_familles, '{}'::text[])) AS x
    WHERE EXISTS (SELECT 1 FROM sync_famille_colonne f WHERE f.famille = x);
    UPDATE sync_parametre SET familles_envoyees = v, familles_lues_le = now() WHERE id = 1;
    RETURN v;
END $$;

-- Lots : seul l'accusé (NULL -> date) se modifie, ou la REFORME d'un lot non accusé dont une famille a été décochée
-- (empreinte, taille et familles ; familles en réduction stricte, jamais élargies)
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
    RAISE EXCEPTION 'sync_lot : lot figé (seul l''accusé peut être enregistré)';
END $$;

-- Constitution d'un lot (remplace la version de 129) : familles appliquées gardées ; refusée (NULL) pendant la constitution
-- d'un instantané ou si le réglage a changé depuis la lecture des lignes (reprise au passage suivant)
CREATE OR REPLACE FUNCTION sync_enregistrer_lot(p_numero bigint, p_lignes bigint[], p_octets int, p_empreinte text, p_familles text[])
RETURNS bigint LANGUAGE plpgsql AS $$
DECLARE p sync_parametre; n int;
BEGIN
    SELECT * INTO p FROM sync_parametre WHERE id = 1 FOR UPDATE;
    IF p.dernier_lot + 1 <> p_numero OR coalesce(array_length(p_lignes, 1), 0) = 0 THEN RETURN NULL; END IF;
    IF EXISTS (SELECT 1 FROM sync_chargement WHERE etat = 'constitution') THEN RETURN NULL; END IF;
    IF p_familles IS NOT NULL
       AND ARRAY(SELECT x FROM unnest(p_familles) x ORDER BY x COLLATE "C") IS DISTINCT FROM coalesce(p.familles_envoyees, '{}'::text[]) THEN
        RETURN NULL;
    END IF;
    INSERT INTO sync_lot (epoque, numero, version_contrat, nb_lignes, octets, empreinte, familles)
    VALUES (p.epoque, p_numero, p.version_contrat, array_length(p_lignes, 1), p_octets, p_empreinte,
            CASE WHEN p_familles IS NULL THEN NULL ELSE coalesce(p.familles_envoyees, '{}'::text[]) END);
    UPDATE sync_sortie SET lot_numero = p_numero
    WHERE id = ANY (p_lignes) AND lot_numero IS NULL AND epoque = p.epoque;
    GET DIAGNOSTICS n = ROW_COUNT;
    IF n <> array_length(p_lignes, 1) THEN
        RAISE EXCEPTION 'Synchronisation : lot % incohérent (% lignes rattachées sur %)', p_numero, n, array_length(p_lignes, 1);
    END IF;
    UPDATE sync_parametre SET dernier_lot = p_numero WHERE id = 1;
    RETURN p_numero;
END $$;

-- Ancienne signature (scripts 126 à 129) : lot sans retrait de famille (NULL)
CREATE OR REPLACE FUNCTION sync_enregistrer_lot(p_numero bigint, p_lignes bigint[], p_octets int, p_empreinte text)
RETURNS bigint LANGUAGE sql AS $$
    SELECT sync_enregistrer_lot(p_numero, p_lignes, p_octets, p_empreinte, NULL::text[])
$$;

-- Reforme d'un lot non accusé sans les familles décochées depuis sa constitution
CREATE OR REPLACE FUNCTION sync_reformer_lot(p_epoque int, p_numero bigint, p_empreinte text, p_octets int, p_familles text[])
RETURNS void LANGUAGE plpgsql AS $$
BEGIN
    UPDATE sync_lot SET empreinte = p_empreinte, octets = p_octets,
                        familles = ARRAY(SELECT x FROM unnest(p_familles) x ORDER BY x COLLATE "C"), reforme_le = now()
    WHERE epoque = p_epoque AND numero = p_numero AND accuse_le IS NULL;
    IF NOT FOUND THEN RAISE EXCEPTION 'Synchronisation : lot %/% absent ou déjà accusé', p_epoque, p_numero; END IF;
END $$;

COMMENT ON TABLE sync_famille_colonne IS 'Synchronisation : colonnes des familles de données nominatives réglées au serveur central (script 130).';
COMMENT ON COLUMN sync_parametre.familles_envoyees IS 'Familles nominatives autorisées par le serveur central (lu à chaque passage) ; NULL = jamais lu : rien de nominatif (script 130).';
COMMENT ON COLUMN sync_lot.familles IS 'Familles nominatives appliquées à la constitution (NULL : lot antérieur au script 130, sans retrait) (script 130).';
