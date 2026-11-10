-- =====================================================================
-- Nomenclature nationale des produits pharmaceutiques (MIPH)
--   * le fichier Excel officiel est importé par le titulaire ; seule la
--     dernière version est gardée (l'historique des imports est conservé) ;
--   * trois situations : en vigueur, non renouvelé, retiré (3 feuilles) ;
--   * substances surveillées : psychotropes à risque avéré d'abus
--     (arrêté interministériel du 11 août 2021, JO n° 61), reconnues par
--     leur DCI pour proposer la classe « psychotrope » ;
--   * le produit du catalogue est relié par son n° d'enregistrement.
-- Idempotent. A exécuter après 022_caisse_partagee.sql.
-- =====================================================================

CREATE TABLE IF NOT EXISTS nomenclature_version (
    id              bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    libelle         text        NOT NULL,          -- ex. « Nomenclature Juin 2026 » (nom de la feuille)
    fichier         text        NOT NULL,
    en_vigueur      int         NOT NULL,
    non_renouveles  int         NOT NULL,
    retraits        int         NOT NULL,
    utilisateur_id  bigint      NOT NULL REFERENCES utilisateur(id),
    importee_le     timestamptz NOT NULL DEFAULT now()
);

CREATE TABLE IF NOT EXISTS nomenclature_produit (
    id                  bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    version_id          bigint NOT NULL REFERENCES nomenclature_version(id),
    situation           text   NOT NULL CHECK (situation IN ('en_vigueur', 'non_renouvele', 'retire')),
    num_enregistrement  text,
    code                text,          -- ex. « 15 A 065 » (même code que les listes officielles)
    dci                 text,
    marque              text,
    forme               text,
    dosage              text,
    conditionnement     text,
    liste               text,          -- « LISTE I », « LISTE II » ou vide
    p1                  text,
    p2                  text,
    obs                 text,
    laboratoire         text,
    pays                text,
    date_initiale       date,
    date_finale         date,
    type                text,          -- GE (générique), RE (référence), BIO…
    statut              text,          -- F (fabriqué en Algérie), I (importé)
    stabilite           text
);
CREATE INDEX IF NOT EXISTS idx_nomenclature_num ON nomenclature_produit (num_enregistrement);
CREATE INDEX IF NOT EXISTS idx_nomenclature_marque ON nomenclature_produit USING gin (marque gin_trgm_ops);
CREATE INDEX IF NOT EXISTS idx_nomenclature_dci ON nomenclature_produit USING gin (dci gin_trgm_ops);

CREATE TABLE IF NOT EXISTS substance_surveillee (
    motif   text PRIMARY KEY,        -- recherché dans la DCI (majuscules, sans accent)
    classe  text NOT NULL CHECK (classe IN ('psychotrope', 'stupefiant')),
    source  text NOT NULL
);
INSERT INTO substance_surveillee (motif, classe, source)
SELECT m, 'psychotrope', 'Arrêté interministériel du 11 août 2021 (JO n° 61) : psychotropes à risque avéré d''abus'
FROM unnest(ARRAY['BUPRENORPHINE', 'TRAMADOL', 'CLONAZEPAM', 'PREGABALINE', 'TRIHEXYPHENIDYLE',
                  'CLORAZEPATE', 'MIDAZOLAM', 'ZOLPIDEM']) AS m
ON CONFLICT (motif) DO NOTHING;

-- Comparaison des DCI : majuscules sans accents (sans l'extension unaccent)
CREATE OR REPLACE FUNCTION majuscule_sans_accent(t text) RETURNS text
LANGUAGE sql IMMUTABLE AS $$
    SELECT translate(upper(coalesce(t, '')), 'ÀÂÄÁÃÇÉÈÊËÍÎÏÌÑÓÔÖÒÕÚÛÜÙÝŸ', 'AAAAACEEEEIIIINOOOOOUUUUYY')
$$;

-- Classe proposée : substance surveillée, sinon la liste de la nomenclature
CREATE OR REPLACE VIEW v_nomenclature AS
SELECT n.*,
       COALESCE(
           (SELECT s.classe FROM substance_surveillee s
            WHERE majuscule_sans_accent(n.dci) LIKE '%' || s.motif || '%'
            ORDER BY s.classe DESC LIMIT 1),
           CASE WHEN upper(btrim(n.liste)) IN ('LISTE I', 'LISTE 1') THEN 'liste_i'
                WHEN upper(btrim(n.liste)) IN ('LISTE II', 'LISTE 2') THEN 'liste_ii'
                ELSE 'libre' END) AS classe_proposee
FROM nomenclature_produit n;

-- Produits du catalogue à vérifier : substance surveillée non classée comme telle,
-- ou produit retiré / non renouvelé dans la dernière nomenclature
CREATE OR REPLACE VIEW v_produit_a_verifier AS
SELECT p.id AS produit_id, p.nom_commercial, p.dosage, p.forme, p.classe,
       d.nom AS dci, p.num_enregistrement,
       COALESCE(sd.classe, n.classe_proposee) AS classe_proposee,
       n.situation,
       CASE WHEN COALESCE(sd.classe, n.classe_proposee) IN ('psychotrope', 'stupefiant')
                 AND p.classe NOT IN ('psychotrope', 'stupefiant') THEN 'classe'
            ELSE 'situation' END AS motif
FROM produit p
LEFT JOIN dci d ON d.id = p.dci_id
LEFT JOIN LATERAL (SELECT s.classe FROM substance_surveillee s
                   WHERE majuscule_sans_accent(d.nom) LIKE '%' || s.motif || '%'
                   ORDER BY s.classe DESC LIMIT 1) sd ON true
LEFT JOIN LATERAL (SELECT v.classe_proposee, v.situation FROM v_nomenclature v
                   WHERE v.num_enregistrement = p.num_enregistrement
                   ORDER BY v.situation = 'en_vigueur' DESC LIMIT 1) n ON true
WHERE p.actif
  AND ((COALESCE(sd.classe, n.classe_proposee) IN ('psychotrope', 'stupefiant')
        AND p.classe NOT IN ('psychotrope', 'stupefiant'))
       OR n.situation IN ('retire', 'non_renouvele'));
