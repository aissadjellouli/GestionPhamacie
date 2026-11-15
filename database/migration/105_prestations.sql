-- =====================================================================
-- Prestations sans stock (modèle Pharma, 2026-11-15, lot 6)
--   type_prestation : prestations vendues à la caisse (prise de tension,
--   glycémie, injection…) : code unique, libellé, prix TTC, taux de TVA,
--   actif. Gérées dans Administration › Prestations avec le droit
--   existant « catalogue.creer » (D-9) ; jamais supprimées (désactivées).
--   ligne_vente : une ligne est SOIT un produit (produit_id + lot_id),
--   SOIT une prestation (type_prestation_id, libellé et taux de TVA figés
--   à la vente, sans lot, sans produit, sans SHP ni part organisme).
--   lot_id et produit_id deviennent facultatifs ; la contrainte
--   ck_ligne_vente_produit_ou_prestation interdit toute ligne produit
--   sans lot. Les lignes existantes (toutes produit + lot) restent
--   valides : aucune donnée existante n'est modifiée.
--   Prestation : jamais de stock, de boîte, de registre, de consommation ;
--   pas de retour client (D-12 : annulation de la vente ou avoir) ; pas
--   de prise en charge par l'organisme.
--   Vues et fonctions revues : v_ligne_vente_facturable (LEFT JOIN : la
--   prestation reste facturable, TVA de la ligne), v_consommation et
--   v_etat_stock (lignes produit seulement, résultat inchangé),
--   verifier_lot_vendable, vendre_boite, verifier_tracabilite_vente
--   (lignes produit seulement) ; v_parcours_boite, v_registre_psychotrope,
--   ventes_lot, lots_delaisses : inchangées (passent par lot/boîte).
-- Idempotent. À exécuter après 104.
-- =====================================================================

-- ---- Types de prestation -------------------------------------------
CREATE TABLE IF NOT EXISTS type_prestation (
    id        bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    code      text NOT NULL,
    libelle   text NOT NULL,
    prix      numeric(12,2) NOT NULL DEFAULT 0,
    taux_tva  numeric(5,2) NOT NULL DEFAULT 0,
    actif     boolean NOT NULL DEFAULT true,
    cree_le   timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT ck_type_prestation_code CHECK (length(btrim(code)) BETWEEN 1 AND 20),
    CONSTRAINT ck_type_prestation_libelle CHECK (length(btrim(libelle)) BETWEEN 1 AND 120),
    CONSTRAINT ck_type_prestation_prix CHECK (prix >= 0),
    CONSTRAINT ck_type_prestation_tva CHECK (taux_tva >= 0 AND taux_tva <= 100)
);
CREATE UNIQUE INDEX IF NOT EXISTS ux_type_prestation_code ON type_prestation (upper(btrim(code)));
COMMENT ON TABLE type_prestation IS 'Prestations vendues à la caisse, sans stock (script 105) ; droit catalogue.creer ; jamais supprimées';
COMMENT ON COLUMN type_prestation.prix IS 'Prix TTC proposé à la caisse (0 : à fixer par le titulaire, vente refusée tant qu''il vaut 0)';

-- ---- Lignes de vente : produit OU prestation -----------------------
ALTER TABLE ligne_vente ADD COLUMN IF NOT EXISTS type_prestation_id bigint;
ALTER TABLE ligne_vente ADD COLUMN IF NOT EXISTS libelle text;
ALTER TABLE ligne_vente ADD COLUMN IF NOT EXISTS taux_tva numeric(5,2);
COMMENT ON COLUMN ligne_vente.type_prestation_id IS 'Prestation vendue (script 105) ; NULL pour une ligne produit';
COMMENT ON COLUMN ligne_vente.libelle IS 'Libellé de la prestation figé à la vente (script 105) ; NULL pour une ligne produit';
COMMENT ON COLUMN ligne_vente.taux_tva IS 'Taux de TVA de la prestation figé à la vente (script 105) ; NULL pour une ligne produit (TVA du produit)';

DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'fk_ligne_vente_type_prestation') THEN
        ALTER TABLE ligne_vente ADD CONSTRAINT fk_ligne_vente_type_prestation
            FOREIGN KEY (type_prestation_id) REFERENCES type_prestation(id);
    END IF;
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'ck_ligne_vente_produit_ou_prestation') THEN
        -- Posée avant de rendre lot_id / produit_id facultatifs : les lignes existantes (produit + lot) la respectent
        ALTER TABLE ligne_vente ADD CONSTRAINT ck_ligne_vente_produit_ou_prestation CHECK (
            (produit_id IS NOT NULL AND lot_id IS NOT NULL AND type_prestation_id IS NULL AND libelle IS NULL AND taux_tva IS NULL)
            OR (type_prestation_id IS NOT NULL AND produit_id IS NULL AND lot_id IS NULL
                AND libelle IS NOT NULL AND taux_tva IS NOT NULL AND taux_tva >= 0 AND taux_tva <= 100
                AND shp = 0 AND montant_organisme = 0 AND prix_chifa IS NULL AND raison_lot IS NULL));
    END IF;
END $$;

ALTER TABLE ligne_vente ALTER COLUMN lot_id DROP NOT NULL;
ALTER TABLE ligne_vente ALTER COLUMN produit_id DROP NOT NULL;
CREATE INDEX IF NOT EXISTS idx_ligne_vente_type_prestation ON ligne_vente (type_prestation_id) WHERE type_prestation_id IS NOT NULL;

-- ---- Déclencheurs et fonctions : lignes produit seulement ----------
CREATE OR REPLACE FUNCTION verifier_lot_vendable() RETURNS trigger
    LANGUAGE plpgsql AS
$$
DECLARE
    l record;
BEGIN
    -- Prestation (script 105) : pas de lot
    IF NEW.lot_id IS NULL THEN
        RETURN NEW;
    END IF;
    SELECT date_peremption, bloque, motif_blocage INTO l FROM lot WHERE id = NEW.lot_id;
    IF l.bloque THEN
        RAISE EXCEPTION 'Lot bloqué (%), vente interdite', COALESCE(l.motif_blocage, 'sans motif');
    END IF;
    IF l.date_peremption <= current_date THEN
        RAISE EXCEPTION 'Lot périmé depuis le %, vente interdite', to_char(l.date_peremption, 'DD/MM/YYYY');
    END IF;
    RETURN NEW;
END;
$$;

CREATE OR REPLACE FUNCTION vendre_boite(p_boite_id bigint, p_ligne_vente_id bigint, p_utilisateur_id bigint) RETURNS void
    LANGUAGE plpgsql AS
$$
DECLARE
    b        record;
    lv       record;
    v_deja   int;
BEGIN
    SELECT * INTO b  FROM boite       WHERE id = p_boite_id FOR UPDATE;
    SELECT lv2.*, v.patient_id INTO lv
    FROM ligne_vente lv2 JOIN vente v ON v.id = lv2.vente_id
    WHERE lv2.id = p_ligne_vente_id;

    IF b.statut <> 'en_stock' THEN
        RAISE EXCEPTION 'Boîte déjà %, vente impossible : contrefaçon ou double scan possible', b.statut;
    END IF;
    -- Prestation (script 105) : aucune boîte
    IF lv.lot_id IS NULL OR b.lot_id <> lv.lot_id THEN
        RAISE EXCEPTION 'Cette boîte n''appartient pas au lot de la ligne de vente';
    END IF;
    SELECT count(*) INTO v_deja FROM boite WHERE ligne_vente_id = p_ligne_vente_id;
    IF v_deja >= lv.quantite THEN
        RAISE EXCEPTION 'Toutes les boîtes de cette ligne sont déjà scannées';
    END IF;

    UPDATE boite SET statut = 'vendue', ligne_vente_id = p_ligne_vente_id WHERE id = p_boite_id;
    INSERT INTO boite_evenement (boite_id, type, ligne_vente_id, patient_id, utilisateur_id)
    VALUES (p_boite_id, 'vente', p_ligne_vente_id, lv.patient_id, p_utilisateur_id);
END;
$$;

CREATE OR REPLACE FUNCTION verifier_tracabilite_vente() RETURNS trigger
    LANGUAGE plpgsql AS
$$
DECLARE
    prm record;
BEGIN
    IF NEW.statut <> 'validee' OR (TG_OP = 'UPDATE' AND OLD.statut = 'validee') THEN
        RETURN NULL;
    END IF;
    SELECT * INTO prm FROM parametre_tracabilite WHERE id = 1;

    -- Boîtes scannées : lignes produit seulement (une prestation n'a pas de boîte, script 105)
    IF prm.boite_obligatoire AND EXISTS (
        SELECT 1 FROM ligne_vente lv
        WHERE lv.vente_id = NEW.id AND lv.lot_id IS NOT NULL
          AND lv.quantite <> (SELECT count(*) FROM boite b WHERE b.ligne_vente_id = lv.id)
    ) THEN
        RAISE EXCEPTION 'Chaque boîte vendue doit être scannée';
    END IF;

    IF NEW.patient_id IS NULL AND (
           prm.patient_obligatoire = 'toujours'
        OR (prm.patient_obligatoire = 'ordonnance' AND NEW.ordonnance_id IS NOT NULL)
        OR (prm.patient_obligatoire IN ('ordonnance', 'psychotrope') AND EXISTS (
                SELECT 1 FROM ligne_vente lv JOIN produit p ON p.id = lv.produit_id
                WHERE lv.vente_id = NEW.id AND p.classe IN ('psychotrope', 'stupefiant')))
    ) THEN
        RAISE EXCEPTION 'Le patient doit être identifié pour cette vente';
    END IF;
    RETURN NULL;
END;
$$;

-- Retour client d'une prestation : refusé (D-12 : annuler la vente ou faire un avoir)
CREATE OR REPLACE FUNCTION refuser_retour_prestation() RETURNS trigger
    LANGUAGE plpgsql AS
$$
BEGIN
    IF EXISTS (SELECT 1 FROM ligne_vente WHERE id = NEW.ligne_vente_id AND type_prestation_id IS NOT NULL) THEN
        RAISE EXCEPTION 'Une prestation ne se rend pas : annulez la vente ou faites un avoir';
    END IF;
    RETURN NEW;
END;
$$;
DROP TRIGGER IF EXISTS trg_retour_client_ligne_prestation ON retour_client_ligne;
CREATE TRIGGER trg_retour_client_ligne_prestation BEFORE INSERT ON retour_client_ligne
    FOR EACH ROW EXECUTE FUNCTION refuser_retour_prestation();

-- ---- Vues ----------------------------------------------------------
-- Consommation : produits seulement (une prestation n'a pas de produit ; résultat inchangé)
CREATE OR REPLACE VIEW v_consommation AS
 WITH prm AS (
         SELECT parametre_alerte.fenetre_conso_jours AS f
           FROM parametre_alerte
          WHERE (parametre_alerte.id = 1)
        ), ventes AS (
         SELECT lv.produit_id,
            sum(lv.quantite) AS qte
           FROM ((ligne_vente lv
             JOIN vente v ON ((v.id = lv.vente_id)))
             CROSS JOIN prm prm_1)
          WHERE ((v.statut = 'validee'::text) AND (v.date_vente >= (now() - make_interval(days => prm_1.f))) AND (lv.produit_id IS NOT NULL))
          GROUP BY lv.produit_id
        )
 SELECT p.id AS produit_id,
    ((COALESCE(ventes.qte, (0)::bigint))::numeric / (prm.f)::numeric) AS conso_jour
   FROM ((produit p
     CROSS JOIN prm)
     LEFT JOIN ventes ON ((ventes.produit_id = p.id)));

CREATE OR REPLACE VIEW v_etat_stock AS
 WITH prm AS (
         SELECT parametre_alerte.delai_fournisseur_jours,
            parametre_alerte.jours_securite,
            parametre_alerte.jours_cible_commande,
            parametre_alerte.jours_dormant,
            parametre_alerte.jours_surstock
           FROM parametre_alerte
          WHERE (parametre_alerte.id = 1)
        ), stock AS (
         SELECT lot.produit_id,
            sum(lot.quantite_disponible) AS qte
           FROM lot
          WHERE ((lot.date_peremption > CURRENT_DATE) AND (NOT lot.bloque))
          GROUP BY lot.produit_id
        ), derniere AS (
         SELECT lv.produit_id,
            max(v.date_vente) AS derniere_vente
           FROM (ligne_vente lv
             JOIN vente v ON ((v.id = lv.vente_id)))
          WHERE ((v.statut = 'validee'::text) AND (lv.produit_id IS NOT NULL))
          GROUP BY lv.produit_id
        ), base AS (
         SELECT p.id AS produit_id,
            p.nom_commercial,
            p.en_tension,
            COALESCE(s.qte, (0)::bigint) AS stock,
            c.conso_jour,
            d.derniere_vente,
            (COALESCE((p.stock_minimum)::numeric, ceil((c.conso_jour * ((prm.delai_fournisseur_jours + prm.jours_securite))::numeric))))::integer AS seuil_alerte,
            (ceil((c.conso_jour * (prm.jours_cible_commande)::numeric)))::integer AS stock_cible,
            prm.jours_dormant,
            prm.jours_surstock
           FROM ((((produit p
             CROSS JOIN prm)
             JOIN v_consommation c ON ((c.produit_id = p.id)))
             LEFT JOIN stock s ON ((s.produit_id = p.id)))
             LEFT JOIN derniere d ON ((d.produit_id = p.id)))
          WHERE p.actif
        )
 SELECT produit_id,
    nom_commercial,
    en_tension,
    stock,
    round(conso_jour, 2) AS conso_jour,
        CASE
            WHEN (conso_jour > (0)::numeric) THEN round(((stock)::numeric / conso_jour))
            ELSE NULL::numeric
        END AS couverture_jours,
    seuil_alerte,
    derniere_vente,
        CASE
            WHEN ((stock = 0) AND (seuil_alerte > 0)) THEN 'rupture'::text
            WHEN (stock < seuil_alerte) THEN 'critique'::text
            WHEN ((stock > 0) AND ((derniere_vente IS NULL) OR (derniere_vente < (now() - make_interval(days => jours_dormant))))) THEN 'dormant'::text
            WHEN ((conso_jour > (0)::numeric) AND (((stock)::numeric / conso_jour) > (jours_surstock)::numeric)) THEN 'surstock'::text
            ELSE 'ok'::text
        END AS statut,
    GREATEST((0)::bigint, (GREATEST(stock_cible, seuil_alerte) - stock)) AS qte_suggeree
   FROM base;

-- Facturable : la prestation reste sur les factures (libellé et TVA figés sur la ligne)
CREATE OR REPLACE VIEW v_ligne_vente_facturable AS
 SELECT v.id AS vente_id,
    v.numero,
    v.date_vente,
    v.patient_id,
    v.organisme_id,
    v.ordonnance_id,
    o.numero AS ordonnance_numero,
    l.id AS ligne_vente_id,
    COALESCE(p.nom_commercial, l.libelle) AS designation,
    COALESCE(p.taux_tva, l.taux_tva) AS taux_tva,
    l.prix_unitaire,
    l.quantite,
    (l.quantite - COALESCE(rt.quantite, (0)::bigint)) AS quantite_nette,
    round(((l.montant_patient * ((l.quantite - COALESCE(rt.quantite, (0)::bigint)))::numeric) / (l.quantite)::numeric), 2) AS montant_patient,
        CASE
            WHEN (v.canal_organisme = 'logiciel'::text) THEN round(((l.montant_organisme * ((l.quantite - COALESCE(rt.quantite, (0)::bigint)))::numeric) / (l.quantite)::numeric), 2)
            ELSE (0)::numeric
        END AS montant_organisme,
    v.montant_remise,
    (EXISTS ( SELECT 1
           FROM (facture_client_ligne fl
             JOIN facture_client f ON ((f.id = fl.facture_id)))
          WHERE ((fl.ligne_vente_id = l.id) AND (fl.part = 'patient'::text) AND (f.statut = ANY (ARRAY['emise'::text, 'rejetee'::text])) AND (f.type = 'facture'::text)))) AS facturee_patient,
    (EXISTS ( SELECT 1
           FROM (facture_client_ligne fl
             JOIN facture_client f ON ((f.id = fl.facture_id)))
          WHERE ((fl.ligne_vente_id = l.id) AND (fl.part = 'organisme'::text) AND (f.statut = ANY (ARRAY['emise'::text, 'rejetee'::text])) AND (f.type = 'facture'::text)))) AS facturee_organisme
   FROM ((((vente v
     JOIN ligne_vente l ON ((l.vente_id = v.id)))
     LEFT JOIN produit p ON ((p.id = l.produit_id)))
     LEFT JOIN ordonnance o ON ((o.id = v.ordonnance_id)))
     LEFT JOIN LATERAL ( SELECT sum(r.quantite) AS quantite
           FROM retour_client_ligne r
          WHERE (r.ligne_vente_id = l.id)) rt ON (true))
  WHERE ((v.statut = 'validee'::text) AND (l.quantite > 0));
