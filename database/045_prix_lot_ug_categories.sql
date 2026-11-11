-- =====================================================================
-- Prix de vente par lot, SHP à la caisse, unités gratuites, catégories de
-- produits, reste à charge saisi, plafond de crédit et délais de paiement
--   * produit.liste_psychotrope : liste I ou II d'un psychotrope, pour son
--     SHP (supplément d'honoraire du pharmacien) : 2,50 DA liste I,
--     1,50 DA liste II ; non renseignée : SHP 0 jusqu'à ce qu'elle le soit ;
--   * lot.ppa : prix public facturé du lot (prix de la vignette). La caisse
--     vend chaque boîte au PPA de son lot, ou au prix du catalogue si le lot
--     n'en a pas ; le SHP de la boîte s'y ajoute ;
--   * ligne_vente.shp : SHP par boîte, compris dans prix_unitaire ;
--   * vente.reste_saisi : en tiers payant, reste à charge du patient saisi
--     par le caissier (le taux de prise en charge n'est pas appliqué ; la
--     part de l'organisme est la différence) ;
--   * reception_ligne : unités gratuites (ug) et leur coût, choisi à la
--     saisie (ug_cout) : « zero » = les boîtes gratuites entrent à 0 DA,
--     « reparti » = coût de chaque boîte = montant facturé ÷ (facturées + UG) ;
--     prix_catalogue_remplace : le PPA facturé a remplacé le prix public du
--     catalogue. quantite = unités facturées (0 si la ligne n'est que des UG) ;
--   * categorie_produit / produit.categorie : médicament, dermo-cosmétique,
--     hygiène… ; les totaux « médicaments » et « autres produits » de la
--     caisse et des rapports en découlent (categorie_produit.medicament) ;
--   * parametre_caisse.plafond_credit_defaut : plafond proposé à la création
--     d'un patient (0 : pas de crédit tant qu'il n'est pas accordé) ;
--   * organisme / client_facture.delai_paiement_jours : échéance proposée
--     des factures de ce destinataire (30 jours par défaut).
-- Idempotent. A exécuter après 044_reception_detaillee.sql.
-- =====================================================================

-- ---- SHP des psychotropes selon leur liste
ALTER TABLE produit ADD COLUMN IF NOT EXISTS liste_psychotrope text;
DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'ck_produit_liste_psychotrope') THEN
        ALTER TABLE produit ADD CONSTRAINT ck_produit_liste_psychotrope
            CHECK (liste_psychotrope IS NULL OR liste_psychotrope IN ('liste_i', 'liste_ii'));
    END IF;
END $$;
COMMENT ON COLUMN produit.liste_psychotrope IS 'Liste I ou II d''un psychotrope, pour son SHP (null : à renseigner, SHP 0)';

-- ---- Prix public du lot
ALTER TABLE lot ADD COLUMN IF NOT EXISTS ppa numeric(12,2);
DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'ck_lot_ppa') THEN
        ALTER TABLE lot ADD CONSTRAINT ck_lot_ppa CHECK (ppa IS NULL OR ppa >= 0);
    END IF;
END $$;
COMMENT ON COLUMN lot.ppa IS 'Prix public facturé du lot (vignette) ; null : prix public du catalogue';

-- ---- SHP et reste à charge à la caisse
ALTER TABLE ligne_vente ADD COLUMN IF NOT EXISTS shp numeric(8,2) NOT NULL DEFAULT 0;
DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'ck_ligne_vente_shp') THEN
        ALTER TABLE ligne_vente ADD CONSTRAINT ck_ligne_vente_shp CHECK (shp >= 0);
    END IF;
END $$;
COMMENT ON COLUMN ligne_vente.shp IS 'SHP par boîte, compris dans prix_unitaire';
ALTER TABLE vente ADD COLUMN IF NOT EXISTS reste_saisi boolean NOT NULL DEFAULT false;
COMMENT ON COLUMN vente.reste_saisi IS 'Tiers payant : reste à charge du patient saisi par le caissier (taux non appliqué)';

-- ---- Unités gratuites et prix du catalogue à la réception
ALTER TABLE reception_ligne ADD COLUMN IF NOT EXISTS ug int NOT NULL DEFAULT 0;
ALTER TABLE reception_ligne ADD COLUMN IF NOT EXISTS ug_cout text NOT NULL DEFAULT 'zero';
ALTER TABLE reception_ligne ADD COLUMN IF NOT EXISTS prix_catalogue_remplace boolean NOT NULL DEFAULT false;
ALTER TABLE reception_ligne DROP CONSTRAINT IF EXISTS reception_ligne_quantite_check;
DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'ck_reception_ligne_quantites') THEN
        ALTER TABLE reception_ligne ADD CONSTRAINT ck_reception_ligne_quantites
            CHECK (quantite >= 0 AND ug >= 0 AND quantite + ug > 0 AND ug_cout IN ('zero', 'reparti'));
    END IF;
END $$;
COMMENT ON COLUMN reception_ligne.quantite IS 'Unités facturées';
COMMENT ON COLUMN reception_ligne.ug IS 'Unités gratuites livrées en plus';
COMMENT ON COLUMN reception_ligne.ug_cout IS 'Coût des UG : zero (boîtes gratuites à 0 DA) ou reparti (montant facturé ÷ toutes les boîtes)';

-- ---- Catégories de produits
CREATE TABLE IF NOT EXISTS categorie_produit (
    code        text    PRIMARY KEY,
    libelle     text    NOT NULL,
    medicament  boolean NOT NULL DEFAULT false,
    ordre       int     NOT NULL DEFAULT 0,
    actif       boolean NOT NULL DEFAULT true
);
COMMENT ON TABLE categorie_produit IS 'Catégories de produits ; medicament : compté dans les totaux « médicaments », sinon « autres produits »';
INSERT INTO categorie_produit (code, libelle, medicament, ordre) VALUES
    ('medicament',  'Médicament', true, 10),
    ('dermo_cosmetique', 'Dermo-cosmétique', false, 20),
    ('hygiene', 'Hygiène', false, 30),
    ('bebe_maman', 'Bébé et maman', false, 40),
    ('complement_alimentaire', 'Compléments alimentaires et diététique', false, 50),
    ('phyto_aroma', 'Phytothérapie et aromathérapie', false, 60),
    ('materiel_medical', 'Matériel médical et orthopédie', false, 70),
    ('paramedical', 'Paramédical (pansements, accessoires de soin)', false, 80)
ON CONFLICT (code) DO NOTHING;
ALTER TABLE produit ADD COLUMN IF NOT EXISTS categorie text NOT NULL DEFAULT 'medicament';
DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'fk_produit_categorie') THEN
        ALTER TABLE produit ADD CONSTRAINT fk_produit_categorie FOREIGN KEY (categorie) REFERENCES categorie_produit (code);
    END IF;
END $$;
COMMENT ON COLUMN produit.categorie IS 'Catégorie du produit (categorie_produit) : médicament ou autre produit';

-- ---- Plafond de crédit proposé et délais de paiement
ALTER TABLE parametre_caisse ADD COLUMN IF NOT EXISTS plafond_credit_defaut numeric(12,2) NOT NULL DEFAULT 0;
DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'ck_plafond_credit_defaut') THEN
        ALTER TABLE parametre_caisse ADD CONSTRAINT ck_plafond_credit_defaut CHECK (plafond_credit_defaut >= 0);
    END IF;
END $$;
COMMENT ON COLUMN parametre_caisse.plafond_credit_defaut IS 'Plafond de crédit proposé à la création d''un patient (DA)';

ALTER TABLE organisme ADD COLUMN IF NOT EXISTS delai_paiement_jours int NOT NULL DEFAULT 30;
ALTER TABLE client_facture ADD COLUMN IF NOT EXISTS delai_paiement_jours int NOT NULL DEFAULT 30;
DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'ck_organisme_delai_paiement') THEN
        ALTER TABLE organisme ADD CONSTRAINT ck_organisme_delai_paiement CHECK (delai_paiement_jours BETWEEN 1 AND 365);
    END IF;
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'ck_client_facture_delai_paiement') THEN
        ALTER TABLE client_facture ADD CONSTRAINT ck_client_facture_delai_paiement CHECK (delai_paiement_jours BETWEEN 1 AND 365);
    END IF;
END $$;
COMMENT ON COLUMN organisme.delai_paiement_jours IS 'Délai de paiement des factures de l''organisme : échéance proposée (jours)';
COMMENT ON COLUMN client_facture.delai_paiement_jours IS 'Délai de paiement des factures du client : échéance proposée (jours)';
