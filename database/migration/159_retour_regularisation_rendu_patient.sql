-- =====================================================================
-- 159 : retour d'une vente dont la part organisme a été régularisée
-- « rendu au patient » au rattachement du volet (décision du titulaire du
-- 2026-11-21 : même modèle que le script 158 ; suite des scripts 155, 157
-- et 158).
-- Constat : quand l'organisme a couvert plus que prévu, le rattachement du
-- volet a rendu au patient la différence (sort avoir_patient : dette
-- diminuée, puis « à rendre » par le tiroir). Un retour suivant lui rendait
-- ensuite toute sa part des boîtes rendues : la part de l'écart de ces
-- boîtes, déjà rendue, lui restait acquise.
--   * retour_client_ligne.montant_avoir_retenu (>= 0, défaut 0) : part de la
--     régularisation « rendu au patient » des boîtes rendues (même prorata
--     que le script 158), RETENUE sur ce que le retour lui rend. Lignes
--     existantes : 0. montant reste la part payée des boîtes rendues (brute).
--   * retour_client.dette_avoir_retenu (>= 0, défaut 0) : part de cette
--     retenue que le retour ne couvrait pas, ajoutée à la dette du patient
--     (relevé : mouvement « correction » rattaché à la vente). Portée par la
--     première écriture du retour (celle qui porte les lignes).
--     retour_client.montant_rembourse = part du patient des boîtes rendues
--     − (retenue − dette ajoutée).
--   * Défense : une retenue exige une régularisation « rendu au patient »
--     active sur la vente, la somme retenue ne dépasse jamais son montant ;
--     une dette ajoutée exige une vente avec patient.
--   * v_credit_accorde : la dette ajoutée compte comme crédit accordé sur la
--     vente, y compris après l'annulation de la vente (l'annulation ne
--     revient pas sur les boîtes déjà rendues). Mêmes colonnes.
--   * Journal fiscal : la ligne « retour » et l'annulation après retour
--     déduisent la retenue de la part du patient et du TTC (la
--     régularisation avait déjà déplacé cette part vers l'organisme).
--     [À VÉRIFIER avec le comptable]
-- Idempotent. Aucune donnée supprimée ni modifiée. Après 158.
-- =====================================================================

-- Fichier en UTF-8 : lu comme tel quel que soit l'encodage du poste qui lance psql.
SET client_encoding = 'UTF8';

-- ---- 1. Retenue par ligne, dette ajoutée par retour ---------------------------
ALTER TABLE retour_client_ligne ADD COLUMN IF NOT EXISTS montant_avoir_retenu numeric(12,2) NOT NULL DEFAULT 0;
ALTER TABLE retour_client ADD COLUMN IF NOT EXISTS dette_avoir_retenu numeric(12,2) NOT NULL DEFAULT 0;
DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'ck_retour_client_ligne_montant_avoir_retenu') THEN
        ALTER TABLE retour_client_ligne ADD CONSTRAINT ck_retour_client_ligne_montant_avoir_retenu CHECK (montant_avoir_retenu >= 0);
    END IF;
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'ck_retour_client_dette_avoir_retenu') THEN
        ALTER TABLE retour_client ADD CONSTRAINT ck_retour_client_dette_avoir_retenu CHECK (dette_avoir_retenu >= 0);
    END IF;
END $$;
COMMENT ON COLUMN retour_client_ligne.montant_avoir_retenu IS 'Part de la régularisation « rendu au patient » (rattachement du volet) des boîtes rendues, déjà rendue au patient et retenue sur ce que le retour lui rend (script 159)';
COMMENT ON COLUMN retour_client.dette_avoir_retenu IS 'Part de la retenue (régularisation « rendu au patient ») que le retour ne couvrait pas, ajoutée à la dette du patient (script 159)';

-- ---- 2. Défense : régularisation « rendu au patient » active, jamais plus que son montant
CREATE OR REPLACE FUNCTION verifier_retour_avoir_retenu() RETURNS trigger
LANGUAGE plpgsql AS $$
DECLARE
    v_vente   bigint;
    v_montant numeric;
    v_retenu  numeric;
BEGIN
    IF NEW.montant_avoir_retenu = 0 OR (TG_OP = 'UPDATE' AND NEW.montant_avoir_retenu <= OLD.montant_avoir_retenu) THEN
        RETURN NEW;
    END IF;
    SELECT l.vente_id INTO v_vente FROM ligne_vente l WHERE l.id = NEW.ligne_vente_id;
    SELECT g.montant_patient INTO v_montant FROM regularisation_part_organisme g
    WHERE g.vente_id = v_vente AND g.statut = 'active' AND g.sort = 'avoir_patient';
    IF v_montant IS NULL THEN
        RAISE EXCEPTION 'Cette vente n''a pas de régularisation « rendu au patient » : rien à retenir à ce titre.' USING ERRCODE = 'P0001';
    END IF;
    SELECT COALESCE(sum(rl.montant_avoir_retenu), 0) INTO v_retenu
    FROM retour_client_ligne rl JOIN ligne_vente l ON l.id = rl.ligne_vente_id
    WHERE l.vente_id = v_vente AND rl.id IS DISTINCT FROM NEW.id;
    IF v_retenu + NEW.montant_avoir_retenu > v_montant THEN
        RAISE EXCEPTION 'La régularisation de part organisme de cette vente vient de changer : rechargez la vente.' USING ERRCODE = 'P0001';
    END IF;
    RETURN NEW;
END;
$$;
COMMENT ON FUNCTION verifier_retour_avoir_retenu() IS 'Refuse une retenue sans régularisation « rendu au patient » active, ou au-delà de son montant (script 159)';
DROP TRIGGER IF EXISTS trg_retour_client_ligne_avoir_retenu ON retour_client_ligne;
CREATE TRIGGER trg_retour_client_ligne_avoir_retenu
    BEFORE INSERT OR UPDATE OF montant_avoir_retenu ON retour_client_ligne
    FOR EACH ROW EXECUTE FUNCTION verifier_retour_avoir_retenu();

CREATE OR REPLACE FUNCTION verifier_retour_dette_avoir() RETURNS trigger
LANGUAGE plpgsql AS $$
BEGIN
    IF NEW.dette_avoir_retenu = 0 OR (TG_OP = 'UPDATE' AND NEW.dette_avoir_retenu <= OLD.dette_avoir_retenu) THEN
        RETURN NEW;
    END IF;
    IF NOT EXISTS (SELECT 1 FROM vente v JOIN regularisation_part_organisme g ON g.vente_id = v.id AND g.statut = 'active' AND g.sort = 'avoir_patient'
                   WHERE v.id = NEW.vente_id AND v.patient_id IS NOT NULL) THEN
        RAISE EXCEPTION 'Vente sans patient ou sans régularisation « rendu au patient » : aucune dette ne peut être ajoutée par ce retour.' USING ERRCODE = 'P0001';
    END IF;
    RETURN NEW;
END;
$$;
COMMENT ON FUNCTION verifier_retour_dette_avoir() IS 'Refuse une dette ajoutée par un retour sans patient ou sans régularisation « rendu au patient » active (script 159)';
DROP TRIGGER IF EXISTS trg_retour_client_dette_avoir ON retour_client;
CREATE TRIGGER trg_retour_client_dette_avoir
    BEFORE INSERT OR UPDATE OF dette_avoir_retenu ON retour_client
    FOR EACH ROW EXECUTE FUNCTION verifier_retour_dette_avoir();

-- ---- 3. Crédit accordé par vente : dette ajoutée par la retenue, même après annulation de la vente
CREATE OR REPLACE VIEW v_credit_accorde AS
SELECT v.id AS vente_id, v.patient_id, v.numero, v.date_vente,
       CASE WHEN v.statut = 'validee' THEN
           x.montant - CASE
               WHEN EXISTS (SELECT 1 FROM paiement p WHERE p.vente_id = v.id AND p.mode = 'credit')
                   THEN COALESCE((SELECT sum(rc.montant_rembourse) FROM retour_client rc WHERE rc.vente_id = v.id), 0)
               ELSE COALESCE((SELECT sum(rl.montant_regularisation) FROM retour_client_ligne rl JOIN ligne_vente lr ON lr.id = rl.ligne_vente_id
                              WHERE lr.vente_id = v.id), 0)
           END
       -- Vente annulée : seule la dette ajoutée par la retenue de ses retours reste due (script 159)
       ELSE x.dette_retenue END AS montant
FROM vente v
JOIN LATERAL (SELECT COALESCE((SELECT sum(p.montant) FROM paiement p WHERE p.vente_id = v.id AND p.mode = 'credit'), 0)
                   + COALESCE((SELECT sum(r.montant_patient) FROM regularisation_part_organisme r
                               WHERE r.vente_id = v.id AND r.sort = 'charge_patient' AND r.statut = 'active'), 0)
                   + COALESCE((SELECT sum(bv.montant_rejete) FROM bordereau_vente bv
                               WHERE bv.vente_id = v.id AND bv.suite = 'patient'), 0)
                   + COALESCE((SELECT sum(rc.dette_avoir_retenu) FROM retour_client rc WHERE rc.vente_id = v.id), 0) AS montant,
                     COALESCE((SELECT sum(rc.dette_avoir_retenu) FROM retour_client rc WHERE rc.vente_id = v.id), 0) AS dette_retenue) x
     ON (v.statut = 'validee' AND x.montant > 0) OR (v.statut = 'annulee' AND x.dette_retenue > 0)
WHERE v.patient_id IS NOT NULL;
COMMENT ON VIEW v_credit_accorde IS 'Crédit accordé par vente : part à crédit, régularisation et rejet de bordereau à la charge du patient, dette ajoutée par la retenue d''une régularisation « rendu au patient » (script 159, gardée après annulation) ; moins ce que les retours ont rendu (vente à crédit : tout ; vente payée : la part de régularisation rendue, script 158)';

-- ---- 4. Journal fiscal : retenue déduite de la part du patient et du TTC du retour
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
    FROM (SELECT journal_vente_taux_ligne(rl.ligne_vente_id) AS taux,
                 sum(rl.montant + rl.montant_regularisation - rl.montant_avoir_retenu + rl.montant_organisme) AS poids,
                 sum(rl.montant + rl.montant_regularisation - rl.montant_avoir_retenu) AS pat, sum(rl.montant_organisme) AS org
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
    -- régularisation rendue comprise, script 158 ; retenue de la régularisation « rendu au patient » déduite, script 159)
    IF v.statut = 'annulee' AND (TG_OP = 'INSERT' OR OLD.statut IS DISTINCT FROM 'annulee')
       AND NOT EXISTS (SELECT 1 FROM journal_vente WHERE vente_id = v.id AND evenement = 'annulation') THEN
        SELECT * INTO v_an FROM annulation_vente WHERE vente_id = v.id ORDER BY id DESC LIMIT 1;
        SELECT COALESCE(sum(r.montant + r.montant_regularisation - r.montant_avoir_retenu), 0), COALESCE(sum(r.montant_organisme), 0)
        INTO v_ret_pat, v_ret_org
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
-- À faire avec le retour à une version du logiciel qui n'écrit pas montant_avoir_retenu, et seulement si aucune ligne de retour
-- n'en porte (sinon la dette des ventes concernées et le journal changeraient) :
-- SELECT count(*) FROM retour_client_ligne WHERE montant_avoir_retenu > 0;   -- doit valoir 0
-- SELECT count(*) FROM retour_client WHERE dette_avoir_retenu > 0;           -- doit valoir 0
-- recréer journal_vente_depuis_retour(), journal_vente_depuis_vente() et v_credit_accorde du script 158 (sections 3 et 4
-- seulement), puis :
-- DROP TRIGGER IF EXISTS trg_retour_client_dette_avoir ON retour_client;
-- DROP FUNCTION IF EXISTS verifier_retour_dette_avoir();
-- DROP TRIGGER IF EXISTS trg_retour_client_ligne_avoir_retenu ON retour_client_ligne;
-- DROP FUNCTION IF EXISTS verifier_retour_avoir_retenu();
-- ALTER TABLE retour_client DROP CONSTRAINT IF EXISTS ck_retour_client_dette_avoir_retenu;
-- ALTER TABLE retour_client_ligne DROP CONSTRAINT IF EXISTS ck_retour_client_ligne_montant_avoir_retenu;
-- ALTER TABLE retour_client DROP COLUMN IF EXISTS dette_avoir_retenu;
-- ALTER TABLE retour_client_ligne DROP COLUMN IF EXISTS montant_avoir_retenu;
