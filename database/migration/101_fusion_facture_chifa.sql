-- =====================================================================
-- Fusion du n° de facture Chifa (2026-11-15, modèle Wasfa, lot 2, partie A)
-- Deux colonnes portaient la même notion : vente.num_facture_chifa (script
-- 007, jamais alimentée par le logiciel) et vente.chifa_facture (script
-- 060, volet Chifa). chifa_facture devient la seule colonne lue et écrite.
--   * table vente_chifa_facture_conflit : ventes dont les deux colonnes
--     sont remplies et différentes (ou dont la recopie créerait un doublon
--     de n° de facture) ; tranchées par le titulaire (écran « Conflits de
--     n° de facture Chifa ») ;
--   * recopie num_facture_chifa -> chifa_facture quand chifa_facture est
--     vide (et sans conflit) ;
--   * vue v_parcours_boite : expose chifa_facture au lieu de
--     num_facture_chifa (vue recréée, aucune donnée touchée) ;
--   * CHECK ck_vente_chifa_facture_format (8 chiffres) posé SEULEMENT si
--     toutes les valeurs le respectent ; sinon les valeurs fautives sont
--     affichées (NOTICE) et la contrainte n'est pas posée.
-- num_facture_chifa est CONSERVÉE (obsolète, plus utilisée) : sa
-- suppression (partie B, script 101b) est destructive et sera validée à
-- part par le titulaire.
-- Aucune donnée supprimée. Idempotent. À exécuter après 100.
-- =====================================================================

CREATE TABLE IF NOT EXISTS vente_chifa_facture_conflit (
    vente_id          bigint PRIMARY KEY REFERENCES vente (id),
    num_facture_chifa text NOT NULL,
    chifa_facture     text,
    constate_le       timestamptz NOT NULL DEFAULT now(),
    tranche_le        timestamptz,
    tranche_par       bigint REFERENCES utilisateur (id),
    valeur_retenue    text,
    CONSTRAINT ck_vente_chifa_facture_conflit_tranche
        CHECK ((tranche_le IS NULL AND valeur_retenue IS NULL) OR (tranche_le IS NOT NULL AND valeur_retenue IS NOT NULL))
);

COMMENT ON TABLE vente_chifa_facture_conflit IS 'Fusion du n° de facture Chifa (script 101) : ventes dont num_facture_chifa et chifa_facture diffèrent, tranchées par le titulaire';
COMMENT ON COLUMN vente_chifa_facture_conflit.num_facture_chifa IS 'Valeur de l''ancienne colonne vente.num_facture_chifa au moment du constat';
COMMENT ON COLUMN vente_chifa_facture_conflit.chifa_facture IS 'Valeur de vente.chifa_facture au moment du constat (null : recopie impossible, n° déjà porté par une autre vente)';
COMMENT ON COLUMN vente_chifa_facture_conflit.valeur_retenue IS 'N° retenu par le titulaire (8 chiffres), écrit dans vente.chifa_facture';

CREATE INDEX IF NOT EXISTS ix_vente_chifa_facture_conflit_a_trancher
    ON vente_chifa_facture_conflit (constate_le) WHERE tranche_le IS NULL;

DO $$
DECLARE
    n_conflits int;
    n_doublons int;
    n_copiees  int;
    n_identiques int;
BEGIN
    -- 1. Conflits : les deux colonnes remplies et différentes
    INSERT INTO vente_chifa_facture_conflit (vente_id, num_facture_chifa, chifa_facture)
    SELECT v.id, btrim(v.num_facture_chifa), btrim(v.chifa_facture)
    FROM vente v
    WHERE NULLIF(btrim(v.num_facture_chifa), '') IS NOT NULL
      AND NULLIF(btrim(v.chifa_facture), '') IS NOT NULL
      AND btrim(v.num_facture_chifa) <> btrim(v.chifa_facture)
    ON CONFLICT (vente_id) DO NOTHING;
    GET DIAGNOSTICS n_conflits = ROW_COUNT;

    -- 2. Recopie impossible : le n° est déjà porté par une autre vente non annulée (index ux_vente_chifa_facture)
    INSERT INTO vente_chifa_facture_conflit (vente_id, num_facture_chifa, chifa_facture)
    SELECT v.id, btrim(v.num_facture_chifa), NULL
    FROM vente v
    WHERE NULLIF(btrim(v.num_facture_chifa), '') IS NOT NULL
      AND v.chifa_facture IS NULL
      AND v.statut <> 'annulee'
      AND EXISTS (SELECT 1 FROM vente a WHERE a.id <> v.id AND a.statut <> 'annulee' AND a.chifa_facture = btrim(v.num_facture_chifa))
    ON CONFLICT (vente_id) DO NOTHING;
    GET DIAGNOSTICS n_doublons = ROW_COUNT;

    -- 3. Recopie quand chifa_facture est vide (hors ventes en conflit)
    UPDATE vente v SET chifa_facture = btrim(v.num_facture_chifa)
    WHERE NULLIF(btrim(v.num_facture_chifa), '') IS NOT NULL
      AND NULLIF(btrim(v.chifa_facture), '') IS NULL
      AND NOT EXISTS (SELECT 1 FROM vente_chifa_facture_conflit c WHERE c.vente_id = v.id);
    GET DIAGNOSTICS n_copiees = ROW_COUNT;

    SELECT count(*) INTO n_identiques FROM vente
    WHERE num_facture_chifa IS NOT NULL AND btrim(num_facture_chifa) = btrim(chifa_facture);

    RAISE NOTICE '101 fusion n° facture Chifa : % recopiée(s), % identique(s), % nouveau(x) conflit(s) de valeurs, % nouveau(x) conflit(s) de doublon, % conflit(s) à trancher au total',
        n_copiees, n_identiques, n_conflits, n_doublons,
        (SELECT count(*) FROM vente_chifa_facture_conflit WHERE tranche_le IS NULL);
END $$;

-- Vue du parcours de la boîte : chifa_facture au lieu de num_facture_chifa (recréée : le nom d'une colonne de vue ne se renomme pas par CREATE OR REPLACE)
DO $$
BEGIN
    IF EXISTS (SELECT 1 FROM information_schema.columns
               WHERE table_schema = current_schema() AND table_name = 'v_parcours_boite' AND column_name = 'num_facture_chifa') THEN
        DROP VIEW v_parcours_boite;
    END IF;
END $$;

CREATE OR REPLACE VIEW v_parcours_boite AS
SELECT b.id AS boite_id,
       COALESCE(b.code_etiquette, b.gtin || ' / ' || b.numero_serie) AS identifiant,
       p.nom_commercial, l.numero_lot, l.date_peremption,
       f.raison_sociale AS fournisseur, r.numero_bl, r.date_reception,
       b.statut, l.bloque,
       v.numero AS ticket, v.date_vente, v.chifa_facture,
       NULLIF(btrim(COALESCE(pt.nom, '') || ' ' || COALESCE(pt.prenom, '')), '') AS patient,
       pt.telephone
FROM boite b
JOIN lot l          ON l.id = b.lot_id
JOIN produit p      ON p.id = l.produit_id
JOIN fournisseur f  ON f.id = l.fournisseur_id
JOIN reception r    ON r.id = b.reception_id
LEFT JOIN ligne_vente lv ON lv.id = b.ligne_vente_id
LEFT JOIN vente v        ON v.id = lv.vente_id
LEFT JOIN patient pt     ON pt.id = v.patient_id;

-- Contrôle « 8 chiffres » : posé seulement si toutes les valeurs existantes le respectent
DO $$
DECLARE
    r record;
    n int;
BEGIN
    IF EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'ck_vente_chifa_facture_format') THEN
        RETURN;
    END IF;
    SELECT count(*) INTO n FROM vente WHERE chifa_facture IS NOT NULL AND chifa_facture !~ '^[0-9]{8}$';
    IF n = 0 THEN
        ALTER TABLE vente ADD CONSTRAINT ck_vente_chifa_facture_format
            CHECK (chifa_facture IS NULL OR chifa_facture ~ '^[0-9]{8}$');
        RAISE NOTICE '101 : contrôle ck_vente_chifa_facture_format (8 chiffres) posé';
    ELSE
        RAISE NOTICE '101 : % n° de facture Chifa non conforme(s) (8 chiffres) : contrôle NON posé, à corriger puis relancer 101', n;
        FOR r IN SELECT id, numero, chifa_facture FROM vente
                 WHERE chifa_facture IS NOT NULL AND chifa_facture !~ '^[0-9]{8}$' ORDER BY id LOOP
            RAISE NOTICE '   vente % (id %) : chifa_facture = « % »', r.numero, r.id, r.chifa_facture;
        END LOOP;
    END IF;
END $$;

COMMENT ON COLUMN vente.chifa_facture IS 'N° de la facture Chifa (8 chiffres) : seule colonne du n° de facture Chifa depuis le script 101 (volet repris, rattaché ou saisi)';
COMMENT ON COLUMN vente.num_facture_chifa IS 'OBSOLÈTE (script 101, 2026-11-15) : remplacée par chifa_facture, plus lue ni écrite ; suppression prévue par le script 101b après validation du titulaire';
