-- =====================================================================
-- 167 -- Liste des postes autorisés (enrôlement et preuve de chaque poste)
--   Plan : docs/taches/2026-11-24-postes-autorises/plan.md § 8 et contrat-api.md. Le titulaire autorise chaque poste (code à usage
--   unique) ; le poste garde une clé privée non exportable et signe chaque requête ; la base ne garde que la clé publique.
--   * poste_autorise : un poste (nom, statut en_attente / actif / revoque, clé publique, code d'enrôlement haché, révocation).
--     Nom unique sans tenir compte de la casse parmi les postes non révoqués (index unique partiel).
--   * parametre_postes : une ligne (mode inactif / transition / exige, maximum de postes, durée du code). Mode « transition »
--     posé par défaut : rien n'est bloqué le jour de la mise à jour.
--   * presence_poste.poste_id : poste prouvé vu par le serveur principal (règle de majorité du 2e serveur).
--   Tables classées « jamais » dans sync_table : jamais envoyées au serveur central.
--   Aucune donnée existante modifiée.
--   Retour arrière : database/migration/retour/167_postes_autorises_retour.sql (supprime les tables et la colonne : les
--   enrôlements sont PERDUS, chaque poste devra être réautorisé ; validation explicite du titulaire avant tout passage).
-- Idempotent et rejouable sur une base plus récente.
-- A exécuter après 166_bascule_retrait_decision.sql.
-- =====================================================================

-- Fichier en UTF-8 : lu comme tel quel que soit l'encodage du poste qui lance psql.
SET client_encoding = 'UTF8';

CREATE TABLE IF NOT EXISTS poste_autorise (
    id                  bigint       GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    nom                 varchar(100) NOT NULL,
    statut              text         NOT NULL DEFAULT 'en_attente',
    cle_publique        bytea,
    empreinte_cle       char(16),
    generation          int          NOT NULL DEFAULT 1,
    code_hache          bytea,
    code_expire_le      timestamptz,
    code_essais         smallint     NOT NULL DEFAULT 0,
    ordinateur          varchar(100),
    cree_le             timestamptz  NOT NULL DEFAULT now(),
    cree_par            bigint       REFERENCES utilisateur (id),
    enrole_le           timestamptz,
    derniere_preuve_le  timestamptz,
    revoque_le          timestamptz,
    revoque_par         bigint       REFERENCES utilisateur (id),
    motif_revocation    varchar(200),
    CONSTRAINT ck_poste_autorise_statut CHECK (statut IN ('en_attente', 'actif', 'revoque')),
    CONSTRAINT ck_poste_autorise_nom CHECK (length(btrim(nom)) > 0),
    CONSTRAINT ck_poste_autorise_generation CHECK (generation >= 1),
    CONSTRAINT ck_poste_autorise_essais CHECK (code_essais >= 0),
    CONSTRAINT ck_poste_autorise_actif_cle CHECK (statut <> 'actif' OR (cle_publique IS NOT NULL AND empreinte_cle IS NOT NULL AND enrole_le IS NOT NULL)),
    CONSTRAINT ck_poste_autorise_attente_code CHECK (statut <> 'en_attente' OR (code_hache IS NOT NULL AND code_expire_le IS NOT NULL)),
    CONSTRAINT ck_poste_autorise_revocation CHECK ((statut = 'revoque') = (revoque_le IS NOT NULL))
);
-- Nom unique (sans la casse) parmi les postes non révoqués : un nom révoqué redevient libre
CREATE UNIQUE INDEX IF NOT EXISTS ux_poste_autorise_nom_actif ON poste_autorise (lower(nom)) WHERE statut <> 'revoque';
-- Recherche d'un code d'enrôlement (postes en attente seulement)
CREATE UNIQUE INDEX IF NOT EXISTS ux_poste_autorise_code ON poste_autorise (code_hache) WHERE statut = 'en_attente';
CREATE INDEX IF NOT EXISTS ix_poste_autorise_cree_par ON poste_autorise (cree_par);
CREATE INDEX IF NOT EXISTS ix_poste_autorise_revoque_par ON poste_autorise (revoque_par);
COMMENT ON TABLE poste_autorise IS 'Postes autorisés par le titulaire : enrôlement par code à usage unique, clé publique ECDSA P-256 du poste (la clé privée reste sur le poste), révocation ; jamais envoyée au central (script 167).';

CREATE TABLE IF NOT EXISTS parametre_postes (
    id                  int          PRIMARY KEY DEFAULT 1 CHECK (id = 1),
    mode                text         NOT NULL DEFAULT 'transition',
    maximum_postes      int          NOT NULL DEFAULT 20,
    duree_code_minutes  int          NOT NULL DEFAULT 15,
    CONSTRAINT ck_parametre_postes_mode CHECK (mode IN ('inactif', 'transition', 'exige')),
    CONSTRAINT ck_parametre_postes_maximum CHECK (maximum_postes BETWEEN 1 AND 200),
    CONSTRAINT ck_parametre_postes_duree CHECK (duree_code_minutes BETWEEN 5 AND 60)
);
INSERT INTO parametre_postes DEFAULT VALUES ON CONFLICT (id) DO NOTHING;
COMMENT ON TABLE parametre_postes IS 'Réglage de la liste des postes autorisés (mode inactif / transition / exige, maximum, durée du code) ; jamais envoyée au central (script 167).';

-- Présence des postes (script 165) : poste prouvé vu par le serveur principal
ALTER TABLE presence_poste ADD COLUMN IF NOT EXISTS poste_id bigint;
DO $fk$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'fk_presence_poste_poste_autorise') THEN
        ALTER TABLE presence_poste ADD CONSTRAINT fk_presence_poste_poste_autorise
            FOREIGN KEY (poste_id) REFERENCES poste_autorise (id) ON DELETE SET NULL;
    END IF;
END $fk$;
CREATE INDEX IF NOT EXISTS ix_presence_poste_poste_id ON presence_poste (poste_id);

-- Classement pour la synchronisation : jamais envoyées au serveur central
INSERT INTO sync_table (nom_table, sens, cle_colonnes, colonnes_exclues, colonnes_patient, colonnes_texte, porteur, motif) VALUES
    ('poste_autorise', 'jamais', '{id}', '{}', '{}', '{}', false, 'Postes autorisés de l''officine (enrôlement, clés publiques), script 167'),
    ('parametre_postes', 'jamais', '{id}', '{}', '{}', '{}', false, 'Réglage local de la liste des postes autorisés, script 167')
ON CONFLICT (nom_table) DO NOTHING;
