-- =====================================================================
-- 155 : retour partiel d'une vente en tiers payant (décision du titulaire
-- du 2026-11-21, option A).
--   * retour_client_ligne.montant_organisme (>= 0, défaut 0) : part de
--     l'organisme des boîtes rendues, au prorata de la part RETENUE (part
--     Chifa si la vente a été régularisée après rapprochement de son volet,
--     sinon part enregistrée à la caisse ; majoration non comprise). Lignes
--     existantes : 0 (aucun retour n'était permis sur une vente en tiers
--     payant avant ce script).
--   * retour_client.chifa_corrige_le / chifa_corrige_par : retour dont la
--     facture a été corrigée dans CHIFA-Officine (liste « Retours à
--     corriger dans CHIFA-Officine », Finances › Chifa). Les deux ensemble.
--   * v_vente_part_organisme : part organisme retenue diminuée de la part
--     des boîtes rendues ; majoration (garde) retirée quand plus aucune
--     boîte remboursée ne reste chez le patient (toutes les lignes à part
--     organisme rendues), gardée sinon. Mêmes colonnes qu'avant (les
--     scripts 097, 098 et 100 se rejouent sans erreur).
--   * Défense en base : refus d'une part organisme rendue (montant_organisme
--     > 0) sur une vente déjà déposée (bordereau sorti de « préparation »).
--     L'application refuse aussi la vente encore sur un bordereau en
--     préparation (retirée d'abord), revérifié sous le verrou de la vente.
--   * Journal fiscal : le retour diminue le TTC complet (patient +
--     organisme), part_organisme négative ; l'annulation d'une vente après
--     retour déduit le TTC complet déjà rendu. Les lignes déjà écrites du
--     journal restent telles quelles (seuls les déclencheurs changent).
--     [À VÉRIFIER avec le comptable]
-- Idempotent. Aucune donnée supprimée ni modifiée. Après 112 et 154.
-- =====================================================================

-- Fichier en UTF-8 : lu comme tel quel que soit l'encodage du poste qui lance psql.
SET client_encoding = 'UTF8';

-- ---- 1. Part organisme des boîtes rendues -----------------------------------
ALTER TABLE retour_client_ligne ADD COLUMN IF NOT EXISTS montant_organisme numeric(12,2) NOT NULL DEFAULT 0;
DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'ck_retour_client_ligne_montant_organisme') THEN
        ALTER TABLE retour_client_ligne ADD CONSTRAINT ck_retour_client_ligne_montant_organisme CHECK (montant_organisme >= 0);
    END IF;
END $$;
COMMENT ON COLUMN retour_client_ligne.montant_organisme IS 'Part de l''organisme des boîtes rendues, retirée de la créance (v_vente_part_organisme) : prorata de la part retenue (Chifa si régularisée), majoration non comprise (script 155)';

-- ---- 2. Correction faite dans CHIFA-Officine ---------------------------------
ALTER TABLE retour_client ADD COLUMN IF NOT EXISTS chifa_corrige_le timestamptz;
ALTER TABLE retour_client ADD COLUMN IF NOT EXISTS chifa_corrige_par bigint;
DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'fk_retour_client_chifa_corrige_par') THEN
        ALTER TABLE retour_client ADD CONSTRAINT fk_retour_client_chifa_corrige_par FOREIGN KEY (chifa_corrige_par) REFERENCES utilisateur(id);
    END IF;
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'ck_retour_client_chifa_corrige') THEN
        ALTER TABLE retour_client ADD CONSTRAINT ck_retour_client_chifa_corrige CHECK ((chifa_corrige_le IS NULL) = (chifa_corrige_par IS NULL));
    END IF;
END $$;
COMMENT ON COLUMN retour_client.chifa_corrige_le IS 'Retour avec part organisme d''une vente Chifa : facture corrigée dans CHIFA-Officine le (liste « Retours à corriger », script 155)';
COMMENT ON COLUMN retour_client.chifa_corrige_par IS 'Employé qui a marqué la facture corrigée dans CHIFA-Officine (script 155)';

-- ---- 3. Créance de l'organisme : retours déduits, majoration selon ce qui reste
CREATE OR REPLACE VIEW v_vente_part_organisme AS
SELECT v.id AS vente_id, v.organisme_id, v.part_organisme AS part_organisme_caisse,
       (COALESCE(r.part_organisme_chifa, v.part_organisme) - COALESCE(rt.organisme, 0)
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
COMMENT ON VIEW v_vente_part_organisme IS 'Créance de l''organisme par vente : part Chifa si l''écart a été régularisé (script 097), sinon part enregistrée à la caisse ; moins la part des boîtes rendues, majoration retirée quand plus aucune boîte remboursée ne reste (script 155)';

-- ---- 4. Défense : pas de part organisme rendue sur une vente déjà déposée ----
CREATE OR REPLACE FUNCTION refuser_retour_organisme_depose() RETURNS trigger
LANGUAGE plpgsql AS $$
BEGIN
    IF NEW.montant_organisme > 0 AND EXISTS (
        SELECT 1 FROM ligne_vente l
        JOIN bordereau_vente bv ON bv.vente_id = l.vente_id
        JOIN bordereau b ON b.id = bv.bordereau_id
        WHERE l.id = NEW.ligne_vente_id AND b.statut <> 'preparation') THEN
        RAISE EXCEPTION 'Vente déposée à la CNAS : retour impossible dans le logiciel tant que la procédure de la CNAS n’est pas connue'
            USING ERRCODE = 'P0001';
    END IF;
    RETURN NEW;
END;
$$;
COMMENT ON FUNCTION refuser_retour_organisme_depose() IS 'Refuse une part organisme rendue (retour_client_ligne.montant_organisme > 0) sur une vente portée sur un bordereau déposé (script 155)';
DROP TRIGGER IF EXISTS trg_retour_client_ligne_organisme_depose ON retour_client_ligne;
CREATE TRIGGER trg_retour_client_ligne_organisme_depose
    BEFORE INSERT OR UPDATE OF montant_organisme ON retour_client_ligne
    FOR EACH ROW EXECUTE FUNCTION refuser_retour_organisme_depose();

-- ---- 5. Journal fiscal : retour au TTC complet -------------------------------
-- Retour client : boîtes rendues au TTC complet (part patient + part organisme), négatif, ventilé selon le taux des lignes rendues
CREATE OR REPLACE FUNCTION journal_vente_depuis_retour() RETURNS trigger
LANGUAGE plpgsql AS $$
DECLARE
    r       retour_client;
    v       vente;
    v_taux  numeric[];
    v_poids numeric[];
    v_total numeric;
    v_pat   numeric;
    v_org   numeric;
    v_vent  record;
BEGIN
    SELECT * INTO r FROM retour_client WHERE id = NEW.id;
    IF NOT FOUND OR EXISTS (SELECT 1 FROM journal_vente WHERE retour_client_id = r.id) THEN RETURN NULL; END IF;
    SELECT * INTO v FROM vente WHERE id = r.vente_id;
    SELECT array_agg(taux ORDER BY taux), array_agg(poids ORDER BY taux), COALESCE(sum(poids), 0), COALESCE(sum(pat), 0), COALESCE(sum(org), 0)
    INTO v_taux, v_poids, v_total, v_pat, v_org
    FROM (SELECT journal_vente_taux_ligne(rl.ligne_vente_id) AS taux, sum(rl.montant + rl.montant_organisme) AS poids,
                 sum(rl.montant) AS pat, sum(rl.montant_organisme) AS org
          FROM retour_client_ligne rl WHERE rl.retour_id = r.id GROUP BY 1) g;
    SELECT * INTO v_vent FROM journal_vente_ventiler(v_taux, v_poids, -v_total);
    INSERT INTO journal_vente (evenement, numero_ticket, vente_id, retour_client_id, survenu_le, session_caisse_id, poste, utilisateur_id,
                               type_vente, montant_ttc, montant_ht, montant_tva, detail_tva, part_patient, part_organisme, paiements, motif)
    VALUES ('retour', v.numero, v.id, r.id, r.cree_le, r.session_caisse_id, r.poste, r.utilisateur_id, v.type,
            -v_total, v_vent.ht, v_vent.tva, v_vent.detail, -v_pat, -v_org,
            CASE WHEN r.mode IS NOT NULL AND r.montant_rembourse > 0 THEN r.mode || ':' || (-r.montant_rembourse)::numeric(14,2) ELSE '' END,
            left(COALESCE(r.motif_code || ' ', '') || COALESCE(r.motif, ''), 500));
    RETURN NULL;
END;
$$;

-- Vente encaissée et annulation : l'annulation déduit le TTC complet déjà rendu (part patient et part organisme des retours)
CREATE OR REPLACE FUNCTION journal_vente_depuis_vente() RETURNS trigger
LANGUAGE plpgsql AS $$
DECLARE
    v         vente;
    v_poste   text;
    v_taux    numeric[];
    v_poids   numeric[];
    v_vent    record;
    v_an      annulation_vente;
    v_retour  numeric;
    v_ret_pat numeric;
    v_ret_org numeric;
BEGIN
    SELECT * INTO v FROM vente WHERE id = NEW.id;
    IF NOT FOUND THEN RETURN NULL; END IF;
    SELECT array_agg(taux ORDER BY taux), array_agg(poids ORDER BY taux) INTO v_taux, v_poids
    FROM (SELECT journal_vente_taux_ligne(l.id) AS taux, sum(l.montant_organisme + l.montant_patient) AS poids
          FROM ligne_vente l WHERE l.vente_id = v.id GROUP BY 1) g;

    -- Vente encaissée : insérée validée, ou passée de « en cours » à validée (ou annulée dans la même transaction)
    IF v.statut IN ('validee', 'annulee') AND (TG_OP = 'INSERT' OR OLD.statut = 'en_cours')
       AND NOT EXISTS (SELECT 1 FROM journal_vente WHERE vente_id = v.id AND evenement = 'vente') THEN
        SELECT poste INTO v_poste FROM session_caisse WHERE id = v.session_caisse_id;
        SELECT * INTO v_vent FROM journal_vente_ventiler(v_taux, v_poids, v.montant_total);
        INSERT INTO journal_vente (evenement, numero_ticket, vente_id, survenu_le, session_caisse_id, poste, utilisateur_id, type_vente,
                                   montant_ttc, montant_ht, montant_tva, detail_tva, part_patient, part_organisme, paiements)
        VALUES ('vente', v.numero, v.id, v.date_vente, v.session_caisse_id, v_poste, v.utilisateur_id, v.type,
                v.montant_total, v_vent.ht, v_vent.tva, v_vent.detail, v.part_patient, v.part_organisme, journal_vente_paiements(v.id, 1));
    END IF;

    -- Annulation : nouvelle ligne (le ticket garde son numéro), au montant encore acquis (TTC déjà rendu par des retours exclu)
    IF v.statut = 'annulee' AND (TG_OP = 'INSERT' OR OLD.statut IS DISTINCT FROM 'annulee')
       AND NOT EXISTS (SELECT 1 FROM journal_vente WHERE vente_id = v.id AND evenement = 'annulation') THEN
        SELECT * INTO v_an FROM annulation_vente WHERE vente_id = v.id ORDER BY id DESC LIMIT 1;
        SELECT COALESCE(sum(r.montant), 0), COALESCE(sum(r.montant_organisme), 0) INTO v_ret_pat, v_ret_org
        FROM retour_client_ligne r JOIN ligne_vente l ON l.id = r.ligne_vente_id WHERE l.vente_id = v.id;
        v_retour := v_ret_pat + v_ret_org;
        SELECT * INTO v_vent FROM journal_vente_ventiler(v_taux, v_poids, -(v.montant_total - v_retour));
        INSERT INTO journal_vente (evenement, numero_ticket, vente_id, annulation_id, survenu_le, session_caisse_id, poste, utilisateur_id,
                                   type_vente, montant_ttc, montant_ht, montant_tva, detail_tva, part_patient, part_organisme, paiements, motif)
        VALUES ('annulation', v.numero, v.id, v_an.id, COALESCE(v_an.cree_le, now()), v_an.session_caisse_id, v_an.poste,
                v_an.utilisateur_id, v.type, -(v.montant_total - v_retour), v_vent.ht, v_vent.tva, v_vent.detail,
                -(v.part_patient - v_ret_pat), -(v.part_organisme - v_ret_org),
                CASE WHEN v_an.mode IS NOT NULL AND v_an.montant_rembourse > 0
                     THEN v_an.mode || ':' || (-v_an.montant_rembourse)::numeric(14,2) ELSE '' END,
                left(v_an.motif, 500));
    END IF;
    RETURN NULL;
END;
$$;
