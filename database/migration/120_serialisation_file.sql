-- =====================================================================
-- Sérialisation DataMatrix (2026-11-16, lot 10, M1 de docs/analyse-ecarts.md)
--   * parametre_serialisation : file active, connecteur (« factice » en
--     attendant l'API de la base nationale du MIPH [À VÉRIFIER]), panne
--     simulée de l'adaptateur factice.
--   * verification_serialisation : file d'attente hors ligne. Chaque boîte
--     Data Matrix (GTIN + série) reçue, vendue ou remise en stock y inscrit
--     une opération (verifier / decommissionner / reactiver) par le
--     déclencheur trg_boite_serialisation. La vente n'attend jamais la
--     base nationale : une tâche de fond du serveur rejoue la file.
--   * serialisation_reponse_factice : réponses paramétrables (tests, démo).
--   * parametre_gs1_ai : identifiants d'application GS1 lus dans un Data
--     Matrix [À VÉRIFIER IANOR / GS1] ; défauts identiques à l'existant.
-- Idempotent ; aucune donnée existante modifiée ni supprimée (les boîtes
-- déjà en base n'entrent pas dans la file).
-- =====================================================================

CREATE TABLE IF NOT EXISTS parametre_serialisation (
    id                  int         PRIMARY KEY DEFAULT 1 CHECK (id = 1),
    actif               boolean     NOT NULL DEFAULT true,
    connecteur          text        NOT NULL DEFAULT 'factice' CHECK (connecteur IN ('factice')),
    factice_hors_ligne  boolean     NOT NULL DEFAULT false,
    modifie_le          timestamptz NOT NULL DEFAULT now()
);
INSERT INTO parametre_serialisation DEFAULT VALUES ON CONFLICT (id) DO NOTHING;

CREATE TABLE IF NOT EXISTS verification_serialisation (
    id                bigint      GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    boite_id          bigint      NOT NULL REFERENCES boite(id),
    operation         text        NOT NULL CHECK (operation IN ('verifier', 'decommissionner', 'reactiver')),
    statut            text        NOT NULL DEFAULT 'en_attente' CHECK (statut IN ('en_attente', 'reussie', 'refusee')),
    resultat          text        CHECK (resultat IN ('authentique', 'inconnue', 'deja_decommissionnee', 'rappelee', 'expiree')),
    message           text,
    essais            int         NOT NULL DEFAULT 0 CHECK (essais >= 0),
    alerte_titulaire  boolean     NOT NULL DEFAULT false,
    cree_le           timestamptz NOT NULL DEFAULT now(),
    prochain_essai_le timestamptz NOT NULL DEFAULT now(),
    dernier_essai_le  timestamptz,
    traite_le         timestamptz
);
CREATE INDEX IF NOT EXISTS idx_verification_serialisation_attente
    ON verification_serialisation (prochain_essai_le, id) WHERE statut = 'en_attente';
CREATE INDEX IF NOT EXISTS idx_verification_serialisation_statut ON verification_serialisation (statut, id);
CREATE INDEX IF NOT EXISTS idx_verification_serialisation_boite ON verification_serialisation (boite_id);

CREATE TABLE IF NOT EXISTS serialisation_reponse_factice (
    id            bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    gtin          text,
    numero_serie  text,
    numero_lot    text,
    operation     text CHECK (operation IN ('verifier', 'decommissionner', 'reactiver')),
    resultat      text NOT NULL CHECK (resultat IN ('authentique', 'inconnue', 'deja_decommissionnee', 'rappelee', 'expiree')),
    commentaire   text
);

CREATE TABLE IF NOT EXISTS parametre_gs1_ai (
    code           text    PRIMARY KEY CHECK (code ~ '^[0-9]{2,4}$'),
    champ          text    NOT NULL CHECK (champ IN ('gtin', 'peremption', 'lot', 'serie')),
    longueur_fixe  int     CHECK (longueur_fixe > 0),
    longueur_max   int     CHECK (longueur_max > 0),
    libelle        text    NOT NULL,
    actif          boolean NOT NULL DEFAULT true
);
INSERT INTO parametre_gs1_ai (code, champ, longueur_fixe, longueur_max, libelle) VALUES
    ('01', 'gtin',       14,   NULL, 'GTIN'),
    ('17', 'peremption', 6,    NULL, 'Date de péremption (AAMMJJ)'),
    ('10', 'lot',        NULL, NULL, 'Numéro de lot (variable)'),
    ('21', 'serie',      NULL, NULL, 'Numéro de série (variable)')
ON CONFLICT (code) DO NOTHING;

-- Mise en file : boîte Data Matrix reçue (vérifier), vendue (décommissionner), remise en stock après vente (réactiver).
-- Une simple insertion locale dans la même transaction : la vente n'est jamais bloquée par la base nationale.
CREATE OR REPLACE FUNCTION mettre_en_file_serialisation() RETURNS trigger
    LANGUAGE plpgsql AS
$$
DECLARE
    v_operation text;
BEGIN
    IF NEW.gtin IS NULL OR NEW.numero_serie IS NULL THEN
        RETURN NULL;   -- étiquette interne : pas de sérialisation nationale
    END IF;
    IF NOT COALESCE((SELECT actif FROM parametre_serialisation WHERE id = 1), false) THEN
        RETURN NULL;
    END IF;
    IF TG_OP = 'INSERT' THEN
        v_operation := 'verifier';
    ELSIF OLD.statut = 'en_stock' AND NEW.statut = 'vendue' THEN
        v_operation := 'decommissionner';
    ELSIF OLD.statut = 'vendue' AND NEW.statut = 'en_stock' THEN
        v_operation := 'reactiver';
    ELSE
        RETURN NULL;
    END IF;
    INSERT INTO verification_serialisation (boite_id, operation) VALUES (NEW.id, v_operation);
    RETURN NULL;
END;
$$;

CREATE OR REPLACE TRIGGER trg_boite_serialisation
    AFTER INSERT OR UPDATE OF statut ON boite
    FOR EACH ROW EXECUTE FUNCTION mettre_en_file_serialisation();
