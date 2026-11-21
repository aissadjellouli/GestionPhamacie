-- =====================================================================
-- 158 : retour d'une vente dont la part organisme a été régularisée « à la
-- charge du patient », et dettes du patient au retour (décision du
-- titulaire du 2026-11-21 ; suite des scripts 155 et 157).
-- Constat : une régularisation « à la charge du patient » faite au
-- rattachement du volet AVANT un retour ajoutait l'écart à sa dette pour
-- toutes les boîtes ; le retour ne lui rendait pas la part de l'écart des
-- boîtes rendues. Par ailleurs, v_credit_accorde retranchait de la dette
-- d'une vente PAYÉE (régularisation ou rejet de bordereau à la charge du
-- patient) tout l'argent rendu par ses retours, part du patient comprise.
--   * retour_client_ligne.montant_regularisation (>= 0, défaut 0) : part de
--     la régularisation « à la charge du patient » des boîtes rendues,
--     rendue au patient (dette diminuée pour la part impayée, argent ou
--     imputation sur ses dettes pour la part payée). Lignes existantes : 0.
--     Comptée dans retour_client.montant_rembourse (écritures du retour).
--   * Défense : une part de régularisation rendue exige une régularisation
--     « à la charge du patient » active sur la vente, et la somme rendue
--     par les retours ne dépasse jamais son montant.
--   * v_credit_accorde : vente à crédit, inchangée (tout ce que les retours
--     rendent est retranché) ; vente payée, seule la part de régularisation
--     rendue est retranchée. Mêmes colonnes.
--   * Journal fiscal : la ligne « retour » et l'annulation après retour
--     comptent la part de régularisation rendue dans le TTC et la part du
--     patient (la régularisation l'y avait déplacée).
--     [À VÉRIFIER avec le comptable]
-- Idempotent. Aucune donnée supprimée ni modifiée. Après 157.
-- =====================================================================

-- Fichier en UTF-8 : lu comme tel quel que soit l'encodage du poste qui lance psql.
SET client_encoding = 'UTF8';

-- ---- 1. Part de la régularisation des boîtes rendues -------------------------
ALTER TABLE retour_client_ligne ADD COLUMN IF NOT EXISTS montant_regularisation numeric(12,2) NOT NULL DEFAULT 0;
DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'ck_retour_client_ligne_montant_regularisation') THEN
        ALTER TABLE retour_client_ligne ADD CONSTRAINT ck_retour_client_ligne_montant_regularisation CHECK (montant_regularisation >= 0);
    END IF;
END $$;
COMMENT ON COLUMN retour_client_ligne.montant_regularisation IS 'Part de la régularisation « à la charge du patient » (rattachement du volet) des boîtes rendues, rendue au patient ; comprise dans retour_client.montant_rembourse (script 158)';

-- ---- 2. Défense : régularisation active, jamais plus que son montant ----------
CREATE OR REPLACE FUNCTION verifier_retour_regularisation() RETURNS trigger
LANGUAGE plpgsql AS $$
DECLARE
    v_vente   bigint;
    v_montant numeric;
    v_rendu   numeric;
BEGIN
    IF NEW.montant_regularisation = 0 OR (TG_OP = 'UPDATE' AND NEW.montant_regularisation <= OLD.montant_regularisation) THEN
        RETURN NEW;
    END IF;
    SELECT l.vente_id INTO v_vente FROM ligne_vente l WHERE l.id = NEW.ligne_vente_id;
    SELECT g.montant_patient INTO v_montant FROM regularisation_part_organisme g
    WHERE g.vente_id = v_vente AND g.statut = 'active' AND g.sort = 'charge_patient';
    IF v_montant IS NULL THEN
        RAISE EXCEPTION 'Cette vente n''a pas de régularisation à la charge du patient : rien à lui rendre à ce titre.' USING ERRCODE = 'P0001';
    END IF;
    SELECT COALESCE(sum(rl.montant_regularisation), 0) INTO v_rendu
    FROM retour_client_ligne rl JOIN ligne_vente l ON l.id = rl.ligne_vente_id
    WHERE l.vente_id = v_vente AND rl.id IS DISTINCT FROM NEW.id;
    IF v_rendu + NEW.montant_regularisation > v_montant THEN
        RAISE EXCEPTION 'La régularisation de part organisme de cette vente vient de changer : rechargez la vente.' USING ERRCODE = 'P0001';
    END IF;
    RETURN NEW;
END;
$$;
COMMENT ON FUNCTION verifier_retour_regularisation() IS 'Refuse une part de régularisation rendue sans régularisation « à la charge du patient » active, ou au-delà de son montant (script 158)';
DROP TRIGGER IF EXISTS trg_retour_client_ligne_regularisation ON retour_client_ligne;
CREATE TRIGGER trg_retour_client_ligne_regularisation
    BEFORE INSERT OR UPDATE OF montant_regularisation ON retour_client_ligne
    FOR EACH ROW EXECUTE FUNCTION verifier_retour_regularisation();

-- ---- 3. Crédit accordé par vente : vente payée, seule la part de régularisation rendue est retranchée
CREATE OR REPLACE VIEW v_credit_accorde AS
SELECT v.id AS vente_id, v.patient_id, v.numero, v.date_vente,
       x.montant - CASE
           WHEN EXISTS (SELECT 1 FROM paiement p WHERE p.vente_id = v.id AND p.mode = 'credit')
               THEN COALESCE((SELECT sum(rc.montant_rembourse) FROM retour_client rc WHERE rc.vente_id = v.id), 0)
           ELSE COALESCE((SELECT sum(rl.montant_regularisation) FROM retour_client_ligne rl JOIN ligne_vente lr ON lr.id = rl.ligne_vente_id
                          WHERE lr.vente_id = v.id), 0)
       END AS montant
FROM vente v
JOIN LATERAL (SELECT COALESCE((SELECT sum(p.montant) FROM paiement p WHERE p.vente_id = v.id AND p.mode = 'credit'), 0)
                   + COALESCE((SELECT sum(r.montant_patient) FROM regularisation_part_organisme r
                               WHERE r.vente_id = v.id AND r.sort = 'charge_patient' AND r.statut = 'active'), 0)
                   + COALESCE((SELECT sum(bv.montant_rejete) FROM bordereau_vente bv
                               WHERE bv.vente_id = v.id AND bv.suite = 'patient'), 0) AS montant) x ON x.montant > 0
WHERE v.statut = 'validee' AND v.patient_id IS NOT NULL;
COMMENT ON VIEW v_credit_accorde IS 'Crédit accordé par vente : part à crédit, régularisation et rejet de bordereau à la charge du patient ; moins ce que les retours ont rendu (vente à crédit : tout ; vente payée : la part de régularisation rendue, script 158)';

-- ---- 4. Journal fiscal : part de régularisation rendue dans le TTC et la part du patient
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
    FROM (SELECT journal_vente_taux_ligne(rl.ligne_vente_id) AS taux, sum(rl.montant + rl.montant_regularisation + rl.montant_organisme) AS poids,
                 sum(rl.montant + rl.montant_regularisation) AS pat, sum(rl.montant_organisme) AS org
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

    -- Annulation : nouvelle ligne (le ticket garde son numéro), au montant encore acquis (TTC déjà rendu par des retours exclu, part de
    -- régularisation rendue comprise, script 158)
    IF v.statut = 'annulee' AND (TG_OP = 'INSERT' OR OLD.statut IS DISTINCT FROM 'annulee')
       AND NOT EXISTS (SELECT 1 FROM journal_vente WHERE vente_id = v.id AND evenement = 'annulation') THEN
        SELECT * INTO v_an FROM annulation_vente WHERE vente_id = v.id ORDER BY id DESC LIMIT 1;
        SELECT COALESCE(sum(r.montant + r.montant_regularisation), 0), COALESCE(sum(r.montant_organisme), 0) INTO v_ret_pat, v_ret_org
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

-- ---- Retour arrière (manuel, à exécuter seulement sur décision) ------------------------------
-- À faire avec le retour à une version du logiciel qui n'écrit pas montant_regularisation, et seulement si aucune ligne de retour
-- n'en porte (sinon la dette des ventes concernées et le journal changeraient) :
-- SELECT count(*) FROM retour_client_ligne WHERE montant_regularisation > 0;   -- doit valoir 0
-- recréer journal_vente_depuis_retour() et journal_vente_depuis_vente() du script 155 (section 5 seulement : ne pas rejouer tout le
-- script 155, qui remettrait la vue v_vente_part_organisme d'avant le script 157), et v_credit_accorde du script 110, puis :
-- DROP TRIGGER IF EXISTS trg_retour_client_ligne_regularisation ON retour_client_ligne;
-- DROP FUNCTION IF EXISTS verifier_retour_regularisation();
-- ALTER TABLE retour_client_ligne DROP CONSTRAINT IF EXISTS ck_retour_client_ligne_montant_regularisation;
-- ALTER TABLE retour_client_ligne DROP COLUMN IF EXISTS montant_regularisation;
