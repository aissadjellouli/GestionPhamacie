-- =====================================================================
-- 171 -- Taux de TVA réglables dans l'Administration
--   Décision du titulaire du 2026-11-26 (lot 2, point 6, « go TVA ») : la TVA ne se tape plus librement ; elle se choisit dans une liste
--   de taux tenue dans Administration › Réglages fiscaux, avec un taux par défaut par catégorie de produit.
--   * taux_tva : taux (0 à 100, 2 décimales, unique), libellé, actif ; un taux n'est jamais supprimé (désactivé, il n'est plus proposé).
--     Liste de départ : 0 % « Exonéré » et 19 % « Taux normal » (constat du 2026-11-26 : produits à 0 % et à 19 % seulement).
--   * taux_tva_categorie : taux proposé à la création d'un produit de la catégorie. Au départ : Médicament 0 %, Parapharmacie 19 % ;
--     les autres catégories n'ont pas de taux par défaut (choix du titulaire, à faire valider par le comptable).
--   * Les deux tables sont classées « jamais » (réglage local, jamais envoyé au serveur central).
--   Données : aucun taux de produit, de prestation ou de facture n'est modifié.
--   Retour arrière : database/migration/retour/171_taux_tva_liste_retour.sql (supprime les deux tables : la liste et les taux par
--   défaut sont perdus ; les taux des produits restent tels quels).
-- Idempotent et rejouable sur une base plus récente.
-- A exécuter après 170_pret_paiement_partiel_remboursement_anticipe.sql.
-- =====================================================================

-- Fichier en UTF-8 : lu comme tel quel que soit l'encodage du poste qui lance psql.
SET client_encoding = 'UTF8';

CREATE TABLE IF NOT EXISTS taux_tva (
    id       bigint        GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    taux     numeric(5,2)  NOT NULL UNIQUE CHECK (taux >= 0 AND taux <= 100),
    libelle  varchar(60)   NOT NULL CHECK (btrim(libelle) <> ''),
    actif    boolean       NOT NULL DEFAULT true,
    cree_le  timestamptz   NOT NULL DEFAULT now()
);
COMMENT ON TABLE taux_tva IS 'Taux de TVA proposés à la saisie (produit, prestation, ligne libre de facture), réglés par le titulaire ; jamais supprimés, désactivés (script 171)';

INSERT INTO taux_tva (taux, libelle) VALUES (0, 'Exonéré'), (19, 'Taux normal') ON CONFLICT (taux) DO NOTHING;

CREATE TABLE IF NOT EXISTS taux_tva_categorie (
    categorie_code  text          PRIMARY KEY REFERENCES categorie_produit(code),
    taux            numeric(5,2)  NOT NULL REFERENCES taux_tva(taux),
    modifie_le      timestamptz   NOT NULL DEFAULT now()
);
COMMENT ON TABLE taux_tva_categorie IS 'Taux de TVA proposé à la création d''un produit de la catégorie (script 171) ; catégorie absente : taux à choisir';
CREATE INDEX IF NOT EXISTS ix_taux_tva_categorie_taux ON taux_tva_categorie (taux);

INSERT INTO taux_tva_categorie (categorie_code, taux)
SELECT c.code, v.taux FROM (VALUES ('medicament', 0::numeric(5,2)), ('parapharmacie', 19::numeric(5,2))) AS v(code, taux)
JOIN categorie_produit c ON c.code = v.code
ON CONFLICT (categorie_code) DO NOTHING;

-- Même forme que le script 126 (lue par le générateur du miroir du central) : réglage local, jamais envoyé
INSERT INTO sync_table (nom_table, sens, colonnes_exclues, colonnes_patient, porteur, motif) VALUES
    ('taux_tva', 'jamais', '{}', '{}', false, 'Liste des taux de TVA de l''officine, script 171'),
    ('taux_tva_categorie', 'jamais', '{}', '{}', false, 'Taux de TVA par défaut des catégories de produits, script 171')
ON CONFLICT (nom_table) DO NOTHING;
