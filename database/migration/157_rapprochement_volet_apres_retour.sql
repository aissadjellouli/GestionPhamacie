-- =====================================================================
-- 157 : rapprochement d'un volet Chifa après un retour (décision du
-- titulaire du 2026-11-21 : AJUSTER ; suite du script 155).
-- Constat : la comparaison du volet se faisait sur les boîtes VENDUES ;
-- une régularisation faite au rattachement APRÈS un retour retenait une
-- part Chifa, dont v_vente_part_organisme retranchait ENCORE la part déjà
-- rendue (double déduction), et le journal fiscal déplaçait aussi cette
-- part une seconde fois.
--   * regularisation_part_organisme.part_organisme_rendue_avant (>= 0,
--     défaut 0) : part de l'organisme déjà retirée par des retours AVANT
--     le rattachement. part_organisme_chifa = part Chifa des boîtes encore
--     chez le patient (comparaison nette des retours) ; part_organisme_vente
--     reste la part enregistrée à la caisse (avant retours). Lignes
--     existantes : 0 (avant le script 155, aucun retour sur une vente en
--     tiers payant ; depuis, aucune régularisation ne l'écrivait).
--   * v_vente_part_organisme : part retenue = part Chifa + part rendue
--     avant le rattachement (sinon part caisse), moins TOUTES les parts
--     rendues ; ce qui est rendu avant n'est donc déduit qu'une fois.
--   * Journal fiscal de la régularisation : déplacement = part Chifa + part
--     rendue avant − part caisse (= − écart réellement régularisé).
--   * Défense : à l'insertion d'une régularisation, la part rendue avant doit
--     être exactement la somme des parts organisme des retours de la vente.
--   * Protection (098) : la nouvelle colonne ne se modifie pas non plus.
-- Idempotent. Aucune donnée supprimée ni modifiée. Après 155.
-- =====================================================================

-- Fichier en UTF-8 : lu comme tel quel que soit l'encodage du poste qui lance psql.
SET client_encoding = 'UTF8';

-- ---- 1. Part organisme rendue avant le rattachement --------------------------
ALTER TABLE regularisation_part_organisme ADD COLUMN IF NOT EXISTS part_organisme_rendue_avant numeric(12,2) NOT NULL DEFAULT 0;
DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'ck_regularisation_rendue_avant') THEN
        ALTER TABLE regularisation_part_organisme ADD CONSTRAINT ck_regularisation_rendue_avant CHECK (part_organisme_rendue_avant >= 0);
    END IF;
END $$;
COMMENT ON COLUMN regularisation_part_organisme.part_organisme_rendue_avant IS 'Part de l''organisme déjà retirée par des retours avant le rattachement du volet (script 157) ; part_organisme_chifa porte alors sur les seules boîtes encore chez le patient';
COMMENT ON COLUMN regularisation_part_organisme.part_organisme_chifa IS 'Part calculée sur le prix Chifa (hors majoration) des boîtes servies encore chez le patient au rattachement (nettes des retours, script 157)';

-- ---- 2. Défense : part rendue avant = retours de la vente à l'insertion -------
CREATE OR REPLACE FUNCTION verifier_regularisation_rendue_avant() RETURNS trigger
LANGUAGE plpgsql AS $$
DECLARE v_rendu numeric;
BEGIN
    SELECT COALESCE(sum(rl.montant_organisme), 0) INTO v_rendu
    FROM retour_client_ligne rl JOIN ligne_vente l ON l.id = rl.ligne_vente_id
    WHERE l.vente_id = NEW.vente_id;
    IF NEW.part_organisme_rendue_avant IS DISTINCT FROM v_rendu THEN
        RAISE EXCEPTION 'Un retour vient d''être enregistré sur cette vente : rescannez le volet.' USING ERRCODE = 'P0001';
    END IF;
    RETURN NEW;
END;
$$;
COMMENT ON FUNCTION verifier_regularisation_rendue_avant() IS 'Refuse une régularisation dont la part rendue avant le rattachement ne correspond pas aux retours de la vente (script 157)';
DROP TRIGGER IF EXISTS trg_regularisation_rendue_avant ON regularisation_part_organisme;
CREATE TRIGGER trg_regularisation_rendue_avant
    BEFORE INSERT ON regularisation_part_organisme
    FOR EACH ROW EXECUTE FUNCTION verifier_regularisation_rendue_avant();

-- ---- 3. Protection (098) : colonne comprise dans ce qui ne change jamais ------
CREATE OR REPLACE FUNCTION proteger_regularisation_part_organisme() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    IF TG_OP = 'UPDATE' AND OLD.statut = 'active' AND NEW.statut = 'annulee'
       AND (NEW.id, NEW.vente_id, NEW.organisme_id, NEW.part_organisme_vente, NEW.part_organisme_chifa, NEW.ecart, NEW.sort, NEW.montant_patient,
            NEW.montant_dette_diminuee, NEW.montant_a_rendre, NEW.montant_pharmacie, NEW.credit_mouvement_id, NEW.utilisateur_id, NEW.valide_par, NEW.cree_le,
            NEW.part_organisme_rendue_avant)
           IS NOT DISTINCT FROM
           (OLD.id, OLD.vente_id, OLD.organisme_id, OLD.part_organisme_vente, OLD.part_organisme_chifa, OLD.ecart, OLD.sort, OLD.montant_patient,
            OLD.montant_dette_diminuee, OLD.montant_a_rendre, OLD.montant_pharmacie, OLD.credit_mouvement_id, OLD.utilisateur_id, OLD.valide_par, OLD.cree_le,
            OLD.part_organisme_rendue_avant)
    THEN
        RETURN NEW;
    END IF;
    RAISE EXCEPTION 'Une régularisation de part organisme ne se modifie pas et ne se supprime pas (seule l''annulation de sa vente la marque annulée)';
END $$;

-- ---- 4. Créance de l'organisme : chaque retour déduit une seule fois ----------
CREATE OR REPLACE VIEW v_vente_part_organisme AS
SELECT v.id AS vente_id, v.organisme_id, v.part_organisme AS part_organisme_caisse,
       (COALESCE(r.part_organisme_chifa + r.part_organisme_rendue_avant, v.part_organisme) - COALESCE(rt.organisme, 0)
        + CASE WHEN rt.plus_rien_rembourse THEN 0 ELSE v.chifa_majoration_montant END)::numeric(12,2) AS part_organisme_retenue,
       (CASE WHEN rt.plus_rien_rembourse THEN 0 ELSE v.chifa_majoration_montant END)::numeric(12,2) AS majoration_chifa
FROM vente v
LEFT JOIN regularisation_part_organisme r ON r.vente_id = v.id AND r.statut = 'active'
LEFT JOIN LATERAL (
    SELECT sum(rl.montant_organisme) AS organisme,
           -- Majoration retirée seulement quand toutes les lignes à part organisme sont entièrement rendues
           sum(rl.montant_organisme) > 0 AND NOT EXISTS (
               SELECT 1 FROM ligne_vente l2
               WHERE l2.vente_id = v.id AND l2.montant_organisme > 0
                 AND l2.quantite > COALESCE((SELECT sum(x.quantite) FROM retour_client_ligne x WHERE x.ligne_vente_id = l2.id), 0)) AS plus_rien_rembourse
    FROM retour_client_ligne rl JOIN ligne_vente l ON l.id = rl.ligne_vente_id
    WHERE l.vente_id = v.id) rt ON true;
COMMENT ON VIEW v_vente_part_organisme IS 'Créance de l''organisme par vente : part Chifa (+ part déjà rendue avant le rattachement, script 157) si l''écart a été régularisé (script 097), sinon part enregistrée à la caisse ; moins la part des boîtes rendues, majoration retirée quand plus aucune boîte remboursée ne reste (script 155)';

-- ---- 5. Journal fiscal de la régularisation : déplacement réel ---------------
CREATE OR REPLACE FUNCTION journal_vente_depuis_regularisation() RETURNS trigger
LANGUAGE plpgsql AS $$
DECLARE
    g        regularisation_part_organisme;
    v        vente;
    v_evt    text;
    v_signe  int;
    v_delta  numeric;
BEGIN
    SELECT * INTO g FROM regularisation_part_organisme WHERE id = NEW.id;
    IF NOT FOUND THEN RETURN NULL; END IF;
    v_evt := CASE WHEN TG_OP = 'INSERT' THEN 'regularisation' ELSE 'annulation_regularisation' END;
    IF v_evt = 'annulation_regularisation' AND g.statut = 'active' THEN RETURN NULL; END IF;
    IF EXISTS (SELECT 1 FROM journal_vente WHERE regularisation_id = g.id AND evenement = v_evt) THEN RETURN NULL; END IF;
    SELECT * INTO v FROM vente WHERE id = g.vente_id;
    v_signe := CASE WHEN v_evt = 'regularisation' THEN 1 ELSE -1 END;
    -- Part déjà rendue avant le rattachement : déjà sortie du journal par la ligne « retour » (script 155), jamais déplacée deux fois
    v_delta := (g.part_organisme_chifa + g.part_organisme_rendue_avant - g.part_organisme_vente) * v_signe;
    INSERT INTO journal_vente (evenement, numero_ticket, vente_id, regularisation_id, survenu_le, utilisateur_id, type_vente,
                               part_patient, part_organisme, montant_mouvement, motif)
    VALUES (v_evt, v.numero, v.id, g.id, CASE WHEN v_evt = 'regularisation' THEN g.cree_le ELSE COALESCE(g.annulee_le, now()) END,
            CASE WHEN v_evt = 'regularisation' THEN g.utilisateur_id ELSE g.annulee_par END, v.type,
            -v_delta, v_delta, COALESCE(g.montant_a_rendre, 0) * v_signe,
            left(CASE WHEN v_evt = 'regularisation' THEN g.sort ELSE g.motif_annulation END, 500));
    RETURN NULL;
END;
$$;

-- ---- Retour arrière (manuel, à exécuter seulement sur décision) ------------------------------
-- À faire avec le retour à une version du logiciel qui n'écrit pas part_organisme_rendue_avant, et seulement si aucune
-- régularisation n'a part_organisme_rendue_avant > 0 (sinon la créance de ces ventes changerait) :
-- SELECT count(*) FROM regularisation_part_organisme WHERE part_organisme_rendue_avant > 0;   -- doit valoir 0
-- \ir 155_retour_partiel_tiers_payant.sql   -- vue v_vente_part_organisme du script 155
-- puis recréer journal_vente_depuis_regularisation() et proteger_regularisation_part_organisme() du script 112 / 098
-- (sections correspondantes), puis :
-- DROP TRIGGER IF EXISTS trg_regularisation_rendue_avant ON regularisation_part_organisme;
-- DROP FUNCTION IF EXISTS verifier_regularisation_rendue_avant();
-- ALTER TABLE regularisation_part_organisme DROP CONSTRAINT IF EXISTS ck_regularisation_rendue_avant;
-- ALTER TABLE regularisation_part_organisme DROP COLUMN IF EXISTS part_organisme_rendue_avant;
