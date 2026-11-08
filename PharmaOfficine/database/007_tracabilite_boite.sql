-- =====================================================================
-- Traçabilité de chaque boîte : grossiste -> réception -> stock -> patient
-- A exécuter après codes_barres_internes.sql et droits_utilisateurs.sql
-- =====================================================================

-- ---------------------------------------------------------------------
-- 1. Identité de chaque boîte
--    soit son Data Matrix (GTIN + numéro de série),
--    soit une étiquette interne unique collée à la réception
-- ---------------------------------------------------------------------
ALTER TABLE boite ALTER COLUMN gtin         DROP NOT NULL;
ALTER TABLE boite ALTER COLUMN numero_serie DROP NOT NULL;
ALTER TABLE boite ADD COLUMN code_etiquette text UNIQUE;
ALTER TABLE boite ADD CONSTRAINT ck_boite_identifiant CHECK (
    (gtin IS NOT NULL AND numero_serie IS NOT NULL) OR code_etiquette IS NOT NULL
);

-- Blocage d'un lot (rappel, suspicion de contrefaçon)
ALTER TABLE lot ADD COLUMN bloque        boolean NOT NULL DEFAULT false;
ALTER TABLE lot ADD COLUMN motif_blocage text;

-- Numéro de la facture Chifa_Officine correspondant à la vente
ALTER TABLE vente ADD COLUMN num_facture_chifa text;

-- ---------------------------------------------------------------------
-- 2. Réglages de traçabilité (activés une fois le stock étiqueté)
-- ---------------------------------------------------------------------
CREATE TABLE parametre_tracabilite (
    id                  int PRIMARY KEY DEFAULT 1 CHECK (id = 1),
    boite_obligatoire   boolean NOT NULL DEFAULT false,   -- chaque boîte vendue doit être scannée
    patient_obligatoire text    NOT NULL DEFAULT 'ordonnance'
                        CHECK (patient_obligatoire IN ('jamais', 'psychotrope', 'ordonnance', 'toujours'))
);
INSERT INTO parametre_tracabilite DEFAULT VALUES;

-- ---------------------------------------------------------------------
-- 3. Historique de chaque boîte (non modifiable)
-- ---------------------------------------------------------------------
CREATE TABLE boite_evenement (
    id                      bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    boite_id                bigint NOT NULL REFERENCES boite(id),
    type                    text   NOT NULL CHECK (type IN (
                                'reception', 'vente', 'retour_client', 'retour_fournisseur',
                                'destruction', 'blocage', 'correction')),
    reception_id            bigint REFERENCES reception(id),
    ligne_vente_id          bigint REFERENCES ligne_vente(id),
    retour_fournisseur_id   bigint REFERENCES retour_fournisseur(id),
    patient_id              bigint REFERENCES patient(id),
    utilisateur_id          bigint REFERENCES utilisateur(id),
    commentaire             text,
    cree_le                 timestamptz NOT NULL DEFAULT now()
);
CREATE INDEX idx_boite_evenement ON boite_evenement (boite_id, cree_le);

CREATE OR REPLACE FUNCTION interdire_modification_historique() RETURNS trigger
LANGUAGE plpgsql AS $$
BEGIN
    RAISE EXCEPTION 'L''historique de traçabilité ne peut être ni modifié ni supprimé';
END;
$$;

CREATE TRIGGER trg_boite_evenement_immuable
    BEFORE UPDATE OR DELETE ON boite_evenement
    FOR EACH ROW EXECUTE FUNCTION interdire_modification_historique();

-- Toute boîte enregistrée entre dans l'historique avec sa réception
CREATE OR REPLACE FUNCTION journaliser_entree_boite() RETURNS trigger
LANGUAGE plpgsql AS $$
BEGIN
    INSERT INTO boite_evenement (boite_id, type, reception_id)
    SELECT NEW.id, 'reception', l.reception_id FROM lot l WHERE l.id = NEW.lot_id;
    RETURN NULL;
END;
$$;

CREATE TRIGGER trg_boite_entree
    AFTER INSERT ON boite
    FOR EACH ROW EXECUTE FUNCTION journaliser_entree_boite();

-- ---------------------------------------------------------------------
-- 4. Étiquettes internes : un code unique PAR BOÎTE
-- ---------------------------------------------------------------------
CREATE OR REPLACE FUNCTION creer_boites_internes(p_lot_id bigint, p_nombre int)
RETURNS TABLE (o_boite_id bigint, o_code text)
LANGUAGE plpgsql AS $$
DECLARE
    v_dispo   int;
    v_deja    int;
    v_base    text;
    i         int;
BEGIN
    SELECT quantite_disponible INTO v_dispo FROM lot WHERE id = p_lot_id FOR UPDATE;
    SELECT count(*) INTO v_deja FROM boite WHERE lot_id = p_lot_id AND statut = 'en_stock';
    IF p_nombre < 1 OR v_deja + p_nombre > v_dispo THEN
        RAISE EXCEPTION 'Lot % : % boîtes en stock, % déjà étiquetées, % demandées',
            p_lot_id, v_dispo, v_deja, p_nombre;
    END IF;

    FOR i IN 1..p_nombre LOOP
        v_base := '20' || lpad(nextval('seq_code_interne')::text, 10, '0');
        o_code := v_base || ean13_cle(v_base);
        INSERT INTO boite (lot_id, code_etiquette) VALUES (p_lot_id, o_code)
        RETURNING id INTO o_boite_id;
        RETURN NEXT;
    END LOOP;
END;
$$;

-- ---------------------------------------------------------------------
-- 5. Vente d'une boîte précise (appelée à chaque scan en caisse)
-- ---------------------------------------------------------------------
CREATE OR REPLACE FUNCTION vendre_boite(p_boite_id bigint, p_ligne_vente_id bigint, p_utilisateur_id bigint)
RETURNS void LANGUAGE plpgsql AS $$
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
    IF b.lot_id <> lv.lot_id THEN
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

-- Retour client, retour fournisseur, destruction : toujours tracés
CREATE OR REPLACE FUNCTION mouvement_boite(
    p_boite_id bigint, p_type text, p_utilisateur_id bigint,
    p_commentaire text DEFAULT NULL, p_retour_fournisseur_id bigint DEFAULT NULL)
RETURNS void LANGUAGE plpgsql AS $$
DECLARE
    v_statut text := CASE p_type
        WHEN 'retour_client'      THEN 'retour_client'
        WHEN 'retour_fournisseur' THEN 'retournee_fournisseur'
        WHEN 'destruction'        THEN 'detruite'
    END;
BEGIN
    IF v_statut IS NULL THEN
        RAISE EXCEPTION 'Mouvement inconnu : %', p_type;
    END IF;
    UPDATE boite SET statut = v_statut WHERE id = p_boite_id;
    INSERT INTO boite_evenement (boite_id, type, retour_fournisseur_id, utilisateur_id, commentaire)
    VALUES (p_boite_id, p_type, p_retour_fournisseur_id, p_utilisateur_id, p_commentaire);
END;
$$;

-- ---------------------------------------------------------------------
-- 6. Contrôle à la validation d'une vente
-- ---------------------------------------------------------------------
CREATE OR REPLACE FUNCTION verifier_tracabilite_vente() RETURNS trigger
LANGUAGE plpgsql AS $$
DECLARE
    prm record;
BEGIN
    IF NEW.statut <> 'validee' OR (TG_OP = 'UPDATE' AND OLD.statut = 'validee') THEN
        RETURN NULL;
    END IF;
    SELECT * INTO prm FROM parametre_tracabilite WHERE id = 1;

    IF prm.boite_obligatoire AND EXISTS (
        SELECT 1 FROM ligne_vente lv
        WHERE lv.vente_id = NEW.id
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

CREATE CONSTRAINT TRIGGER trg_tracabilite_vente
    AFTER INSERT OR UPDATE OF statut ON vente
    DEFERRABLE INITIALLY DEFERRED
    FOR EACH ROW EXECUTE FUNCTION verifier_tracabilite_vente();

-- Un lot bloqué ne se vend plus (remplace la version précédente)
CREATE OR REPLACE FUNCTION verifier_lot_vendable() RETURNS trigger
LANGUAGE plpgsql AS $$
DECLARE
    l record;
BEGIN
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

-- ---------------------------------------------------------------------
-- 7. Parcours complet d'une boîte
-- ---------------------------------------------------------------------
CREATE OR REPLACE VIEW v_parcours_boite AS
SELECT b.id AS boite_id,
       COALESCE(b.code_etiquette, b.gtin || ' / ' || b.numero_serie) AS identifiant,
       p.nom_commercial, l.numero_lot, l.date_peremption,
       f.raison_sociale AS fournisseur, r.numero_bl, r.date_reception,
       b.statut, l.bloque,
       v.numero AS ticket, v.date_vente, v.num_facture_chifa,
       NULLIF(btrim(COALESCE(pt.nom, '') || ' ' || COALESCE(pt.prenom, '')), '') AS patient,
       pt.telephone
FROM boite b
JOIN lot l          ON l.id = b.lot_id
JOIN produit p      ON p.id = l.produit_id
JOIN fournisseur f  ON f.id = l.fournisseur_id
JOIN reception r    ON r.id = l.reception_id
LEFT JOIN ligne_vente lv ON lv.id = b.ligne_vente_id
LEFT JOIN vente v        ON v.id = lv.vente_id
LEFT JOIN patient pt     ON pt.id = v.patient_id;

-- Avancement de l'étiquetage initial (avant d'activer le mode strict)
CREATE OR REPLACE VIEW v_lots_a_etiqueter AS
SELECT l.id AS lot_id, p.nom_commercial, l.numero_lot, l.quantite_disponible,
       l.quantite_disponible - COUNT(b.id) FILTER (WHERE b.statut = 'en_stock') AS boites_sans_identite
FROM lot l
JOIN produit p ON p.id = l.produit_id
LEFT JOIN boite b ON b.lot_id = l.id
WHERE l.quantite_disponible > 0
GROUP BY l.id, p.nom_commercial, l.numero_lot, l.quantite_disponible
HAVING l.quantite_disponible - COUNT(b.id) FILTER (WHERE b.statut = 'en_stock') > 0;

-- ---------------------------------------------------------------------
-- 8. Rappel de lot : blocage immédiat + liste des patients à contacter
-- ---------------------------------------------------------------------
CREATE OR REPLACE FUNCTION rappel_lot(
    p_produit_id bigint, p_numero_lot text, p_motif text, p_utilisateur_id bigint)
RETURNS TABLE (o_patient text, o_telephone text, o_date_vente timestamptz,
               o_ticket text, o_boites bigint)
LANGUAGE plpgsql AS $$
BEGIN
    -- Bloquer le lot chez tous les grossistes
    UPDATE lot SET bloque = true, motif_blocage = p_motif
    WHERE produit_id = p_produit_id AND numero_lot = p_numero_lot;

    INSERT INTO boite_evenement (boite_id, type, utilisateur_id, commentaire)
    SELECT b.id, 'blocage', p_utilisateur_id, p_motif
    FROM boite b JOIN lot l ON l.id = b.lot_id
    WHERE l.produit_id = p_produit_id AND l.numero_lot = p_numero_lot
      AND b.statut = 'en_stock';

    -- Ventes du lot : aussi celles d'avant l'étiquetage par boîte
    RETURN QUERY
    SELECT COALESCE(NULLIF(btrim(COALESCE(pt.nom, '') || ' ' || COALESCE(pt.prenom, '')), ''),
                    'Vente anonyme'),
           pt.telephone, v.date_vente, v.numero, SUM(lv.quantite)::bigint
    FROM ligne_vente lv
    JOIN vente v     ON v.id = lv.vente_id
    JOIN lot l       ON l.id = lv.lot_id
    LEFT JOIN patient pt ON pt.id = v.patient_id
    WHERE l.produit_id = p_produit_id AND l.numero_lot = p_numero_lot
      AND v.statut = 'validee'
    GROUP BY pt.nom, pt.prenom, pt.telephone, v.date_vente, v.numero
    ORDER BY v.date_vente DESC;
END;
$$;
