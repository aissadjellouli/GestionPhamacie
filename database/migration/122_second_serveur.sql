-- =====================================================================
-- 2e serveur dans l'officine (2026-11-16, lot 5, docs/mise-en-production.md
-- « 2e serveur »)
--   * serveur_officine : un serveur de l'officine (principal ou 2e serveur),
--     identifié par le nom du PC. Réglages propres à chaque serveur (dossier
--     d'échange CHIFA-Officine, dossier du logiciel MDN), cases cochées par
--     le titulaire (CHIFA-Officine installé, lecteur de carte et Token,
--     logiciel MDN installé, douchette branchée et testée), relevés (version de l'API, clé de scellement,
--     imprimantes, dossiers joignables). La table est répliquée : les deux
--     lignes se lisent des deux côtés ; à la bascule, l'API du 2e serveur
--     prend les dossiers de SA ligne.
--   * surveillance_replication : seuils d'alerte de retard de la
--     réplication (titulaire) et dernière alerte envoyée.
-- La réplication elle-même (rôle, pg_hba.conf, slot) se règle par les
-- scripts d'administration scripts/second-serveur (superutilisateur), pas
-- ici. Idempotent ; aucune donnée existante modifiée.
-- =====================================================================

CREATE TABLE IF NOT EXISTS serveur_officine (
    nom                      text        PRIMARY KEY CHECK (nom <> '' AND nom = upper(btrim(nom))),
    role                     text        NOT NULL DEFAULT 'second' CHECK (role IN ('principal', 'second')),
    dossier_chifa            text,
    dossier_mdn              text,
    chifa_installe           boolean     NOT NULL DEFAULT false,
    lecteur_token            boolean     NOT NULL DEFAULT false,
    mdn_installe             boolean     NOT NULL DEFAULT false,
    douchette_testee         boolean     NOT NULL DEFAULT false,
    version_api              text,
    cle_presente             boolean,
    empreinte_cle            text        CHECK (empreinte_cle IS NULL OR empreinte_cle ~ '^[0-9a-f]{16}$'),
    imprimantes              text,
    dossier_chifa_joignable  boolean,
    dossier_mdn_joignable    boolean,
    releve_le                timestamptz,
    modifie_le               timestamptz NOT NULL DEFAULT now(),
    modifie_par              bigint      REFERENCES utilisateur(id)
);
-- Un seul serveur principal à la fois
CREATE UNIQUE INDEX IF NOT EXISTS uq_serveur_officine_principal ON serveur_officine (role) WHERE role = 'principal';

CREATE TABLE IF NOT EXISTS surveillance_replication (
    id                   int         PRIMARY KEY DEFAULT 1 CHECK (id = 1),
    seuil_secondes       int         NOT NULL DEFAULT 300 CHECK (seuil_secondes BETWEEN 10 AND 86400),
    seuil_octets         bigint      NOT NULL DEFAULT 67108864 CHECK (seuil_octets > 0),
    derniere_alerte_le   timestamptz,
    modifie_le           timestamptz NOT NULL DEFAULT now()
);
INSERT INTO surveillance_replication DEFAULT VALUES ON CONFLICT (id) DO NOTHING;

COMMENT ON TABLE serveur_officine IS 'Serveurs de l''officine (principal, 2e serveur) : réglages propres à chacun, cases du titulaire, relevés (script 122).';
COMMENT ON TABLE surveillance_replication IS 'Seuils d''alerte de retard de la réplication vers le 2e serveur (script 122).';
