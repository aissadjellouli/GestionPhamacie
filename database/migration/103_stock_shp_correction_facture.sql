-- =====================================================================
-- Stock : SHP par lot, correction tracée d'une entrée de stock,
-- date de la facture fournisseur (2026-11-15, modèle Wasfa, lot 4)
--   * lot.shp : SHP par boîte du lot (numeric(10,2), 0 par défaut), repris
--     à l'ajout de la colonne depuis les lignes de réception du lot (aucun
--     lot n'a de SHP différente d'une ligne à l'autre : constat du lot 0) ;
--     un lot sans ligne de réception (ouverture, reprise) reste à 0 (nombre
--     annoncé par un NOTICE) ; renseigné ensuite à chaque réception ;
--   * reception.date_facture : date de la facture du grossiste (facultative ;
--     contrôles par l'application : pas dans le futur, pas plus de 90 jours
--     avant la livraison) ;
--   * correction_lot : journal des corrections d'une entrée de stock (PPA,
--     n° de lot, péremption, SHP, quantité), ancienne → nouvelle valeur,
--     motif obligatoire ; jamais modifié ni supprimé (déclencheur) ;
--     une correction de quantité est liée à son mouvement de stock ;
--   * nouveau droit « stock.corriger_entree » (« Corriger une entrée de
--     stock », module Stock, sensible) : titulaire et pharmacien assistant
--     par défaut, posé à la première exécution seulement ; jamais autorisable
--     par le code comptoir d'un collègue.
-- Aucune donnée supprimée. Idempotent.
-- A exécuter après 102 (aucune dépendance aux scripts 100 à 102).
-- =====================================================================

-- ---- SHP par lot ------------------------------------------------------
DO $$
DECLARE
    v_sans_ligne int;
BEGIN
    IF NOT EXISTS (SELECT 1 FROM information_schema.columns
                   WHERE table_schema = 'public' AND table_name = 'lot' AND column_name = 'shp') THEN
        ALTER TABLE lot ADD COLUMN shp numeric(10,2) NOT NULL DEFAULT 0;
        -- Reprise : SHP de la ligne de réception du lot (la plus récente si plusieurs, toutes égales à ce jour)
        UPDATE lot l SET shp = x.shp
        FROM (SELECT DISTINCT ON (lot_id) lot_id, shp FROM reception_ligne ORDER BY lot_id, id DESC) x
        WHERE x.lot_id = l.id AND x.shp <> 0;
        SELECT count(*) INTO v_sans_ligne FROM lot l
        WHERE NOT EXISTS (SELECT 1 FROM reception_ligne rl WHERE rl.lot_id = l.id);
        RAISE NOTICE 'lot.shp repris des lignes de réception ; % lot(s) sans ligne de réception laissé(s) à 0', v_sans_ligne;
    END IF;
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'ck_lot_shp') THEN
        ALTER TABLE lot ADD CONSTRAINT ck_lot_shp CHECK (shp >= 0);
    END IF;
END $$;
COMMENT ON COLUMN lot.shp IS 'SHP par boîte du lot (script 103) : repris des lignes de réception, 0 sans ligne ; mis à jour à chaque réception du lot';

-- ---- Date de la facture du grossiste -----------------------------------
ALTER TABLE reception ADD COLUMN IF NOT EXISTS date_facture date;
COMMENT ON COLUMN reception.date_facture IS 'Date de la facture du grossiste (script 103), facultative ; ni dans le futur, ni plus de 90 jours avant la livraison (contrôle applicatif)';

-- ---- Journal des corrections d'une entrée de stock ---------------------
CREATE TABLE IF NOT EXISTS correction_lot (
    id              bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    lot_id          bigint NOT NULL REFERENCES lot (id),
    champ           text NOT NULL CONSTRAINT ck_correction_lot_champ
                        CHECK (champ IN ('ppa', 'numero_lot', 'date_peremption', 'shp', 'quantite')),
    ancienne_valeur text,
    nouvelle_valeur text,
    motif           text NOT NULL CONSTRAINT ck_correction_lot_motif CHECK (length(btrim(motif)) > 0),
    mouvement_id    bigint REFERENCES mouvement_stock (id),
    utilisateur_id  bigint NOT NULL REFERENCES utilisateur (id),
    corrige_le      timestamptz NOT NULL DEFAULT now()
);
COMMENT ON TABLE correction_lot IS 'Corrections d''une entrée de stock (script 103) : une ligne par champ corrigé, ancienne → nouvelle valeur, motif ; jamais modifiée ni supprimée';
COMMENT ON COLUMN correction_lot.mouvement_id IS 'Correction de quantité : mouvement de stock (ajustement) qui l''a portée';
CREATE INDEX IF NOT EXISTS ix_correction_lot_lot ON correction_lot (lot_id, corrige_le);

DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_trigger WHERE tgname = 'trg_correction_lot_immuable') THEN
        CREATE TRIGGER trg_correction_lot_immuable BEFORE DELETE OR UPDATE ON correction_lot
            FOR EACH ROW EXECUTE FUNCTION interdire_modification();
    END IF;
END $$;

-- ---- Droit « Corriger une entrée de stock » ----------------------------
-- Le droit et ses attributions de départ ne sont posés qu'à la première exécution :
-- une réexécution n'annule jamais un choix fait ensuite par le titulaire.
DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM permission WHERE code = 'stock.corriger_entree') THEN
        INSERT INTO permission (code, module, libelle, sensible)
        VALUES ('stock.corriger_entree', 'Stock', 'Corriger une entrée de stock', true)
        ON CONFLICT DO NOTHING;

        -- Les droits du titulaire sont protégés par un déclencheur : levé le temps de cette transaction
        ALTER TABLE role_permission DISABLE TRIGGER trg_proteger_role_systeme;
        INSERT INTO role_permission (role_id, permission_code)
        SELECT r.id, 'stock.corriger_entree' FROM role r WHERE r.code IN ('titulaire', 'assistant')
        ON CONFLICT DO NOTHING;
        ALTER TABLE role_permission ENABLE TRIGGER trg_proteger_role_systeme;
    END IF;
END $$;
