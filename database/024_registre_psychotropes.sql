-- =====================================================================
-- Registre des psychotropes et stupéfiants complet
--   * chaque mouvement de stock d'un produit sous registre (réception,
--     retour, périmé, casse, écart d'inventaire) y écrit sa ligne
--     automatiquement ; la vente l'écrit elle-même (patient, médecin,
--     ordonnance) ;
--   * porteur de l'ordonnance quand ce n'est pas le patient (nom, adresse,
--     pièce d'identité : arrêté du 29 juin 2013, art. 7) ;
--   * solde d'ouverture par lot (stock actuel) pour démarrer un registre juste ;
--   * vue v_registre_psychotrope : numéro d'ordre, entrées, sorties, solde
--     par produit et par lot.
-- Le registre reste non modifiable (trg_registre_immuable).
-- Idempotent. A exécuter après 023_nomenclature.sql.
-- =====================================================================

-- Types de lignes : entrées (+) et sorties (-)
ALTER TABLE registre_psychotrope DROP CONSTRAINT IF EXISTS registre_psychotrope_type_mouvement_check;
ALTER TABLE registre_psychotrope ADD CONSTRAINT registre_psychotrope_type_mouvement_check CHECK (type_mouvement IN (
    'ouverture', 'entree', 'retour_client', 'retour_refuse', 'ecart_plus',                 -- entrées
    'sortie', 'retour_fournisseur', 'destruction', 'casse', 'ecart_moins'));               -- sorties

ALTER TABLE registre_psychotrope ADD COLUMN IF NOT EXISTS mouvement_id    bigint UNIQUE REFERENCES mouvement_stock(id);
ALTER TABLE registre_psychotrope ADD COLUMN IF NOT EXISTS reference       text;
ALTER TABLE registre_psychotrope ADD COLUMN IF NOT EXISTS porteur_nom     text;
ALTER TABLE registre_psychotrope ADD COLUMN IF NOT EXISTS porteur_adresse text;
ALTER TABLE registre_psychotrope ADD COLUMN IF NOT EXISTS porteur_piece   text;
COMMENT ON COLUMN registre_psychotrope.porteur_nom IS 'Porteur de l''ordonnance quand ce n''est pas le patient (avec adresse et pièce d''identité)';

-- Numéro d'ordre sans trou : l'identifiant technique saute un numéro quand une transaction est annulée,
-- le numéro d'ordre est attribué à l'inscription (max + 1, sous verrou)
ALTER TABLE registre_psychotrope ADD COLUMN IF NOT EXISTS numero_ordre bigint;
DO $$
BEGIN
    IF EXISTS (SELECT 1 FROM registre_psychotrope WHERE numero_ordre IS NULL) THEN
        -- Numérotation des lignes déjà inscrites, dans leur ordre ; rien d'autre n'est modifié
        ALTER TABLE registre_psychotrope DISABLE TRIGGER trg_registre_immuable;
        UPDATE registre_psychotrope r SET numero_ordre = n.rang
        FROM (SELECT id, row_number() OVER (ORDER BY id) AS rang FROM registre_psychotrope) n
        WHERE n.id = r.id AND r.numero_ordre IS NULL;
        ALTER TABLE registre_psychotrope ENABLE TRIGGER trg_registre_immuable;
    END IF;
END $$;
CREATE UNIQUE INDEX IF NOT EXISTS ux_registre_numero_ordre ON registre_psychotrope (numero_ordre);

CREATE OR REPLACE FUNCTION numeroter_registre() RETURNS trigger
LANGUAGE plpgsql AS $$
BEGIN
    PERFORM pg_advisory_xact_lock(hashtext('registre_psychotrope'));
    SELECT coalesce(max(numero_ordre), 0) + 1 INTO NEW.numero_ordre FROM registre_psychotrope;
    RETURN NEW;
END $$;
DROP TRIGGER IF EXISTS trg_registre_numero ON registre_psychotrope;
CREATE TRIGGER trg_registre_numero BEFORE INSERT ON registre_psychotrope
    FOR EACH ROW EXECUTE FUNCTION numeroter_registre();

ALTER TABLE registre_psychotrope DROP CONSTRAINT IF EXISTS ck_registre_porteur_complet;
ALTER TABLE registre_psychotrope ADD CONSTRAINT ck_registre_porteur_complet CHECK (
    porteur_nom IS NULL OR (porteur_adresse IS NOT NULL AND porteur_piece IS NOT NULL));

CREATE OR REPLACE FUNCTION est_sous_registre(p_lot_id bigint) RETURNS boolean
LANGUAGE sql STABLE AS $$
    SELECT p.classe IN ('psychotrope', 'stupefiant') FROM lot l JOIN produit p ON p.id = l.produit_id WHERE l.id = p_lot_id
$$;

-- Mouvement de stock → ligne du registre (la vente est inscrite par l'application, avec l'ordonnance)
CREATE OR REPLACE FUNCTION inscrire_mouvement_registre() RETURNS trigger
LANGUAGE plpgsql AS $$
DECLARE
    v_type  text;
    v_solde int;
BEGIN
    IF NEW.type = 'vente' OR NEW.quantite = 0 OR NOT coalesce(est_sous_registre(NEW.lot_id), false) THEN
        RETURN NEW;
    END IF;
    IF NEW.utilisateur_id IS NULL THEN
        RAISE EXCEPTION 'Mouvement d''un produit sous registre sans employé : inscription au registre impossible';
    END IF;
    v_type := CASE NEW.type
        WHEN 'entree'                THEN 'entree'
        WHEN 'retour_client'         THEN 'retour_client'
        WHEN 'retour_fournisseur'    THEN CASE WHEN NEW.quantite < 0 THEN 'retour_fournisseur' ELSE 'retour_refuse' END
        WHEN 'perime'                THEN 'destruction'
        WHEN 'casse'                 THEN 'casse'
        WHEN 'ajustement_inventaire' THEN CASE WHEN NEW.quantite < 0 THEN 'ecart_moins' ELSE 'ecart_plus' END
    END;
    IF v_type IS NULL THEN
        RAISE EXCEPTION 'Type de mouvement % non prévu au registre des psychotropes', NEW.type;
    END IF;
    -- Le lot est déjà à jour quand le mouvement est écrit
    SELECT greatest(quantite_disponible, 0) INTO v_solde FROM lot WHERE id = NEW.lot_id;
    INSERT INTO registre_psychotrope (lot_id, mouvement_id, type_mouvement, quantite, solde_apres, utilisateur_id, reference, cree_le)
    VALUES (NEW.lot_id, NEW.id, v_type, abs(NEW.quantite), coalesce(v_solde, 0), NEW.utilisateur_id, NEW.reference, NEW.cree_le);
    RETURN NEW;
END $$;

DROP TRIGGER IF EXISTS trg_mouvement_registre ON mouvement_stock;
CREATE TRIGGER trg_mouvement_registre AFTER INSERT ON mouvement_stock
    FOR EACH ROW EXECUTE FUNCTION inscrire_mouvement_registre();

-- La reprise (008) inscrit aussi l'entrée elle-même : pas de doublon si le mouvement l'a déjà fait
CREATE OR REPLACE FUNCTION eviter_doublon_registre() RETURNS trigger
LANGUAGE plpgsql AS $$
BEGIN
    IF NEW.type_mouvement = 'entree' AND NEW.mouvement_id IS NULL AND NEW.ligne_vente_id IS NULL
       AND EXISTS (SELECT 1 FROM registre_psychotrope r
                   WHERE r.lot_id = NEW.lot_id AND r.type_mouvement = 'entree' AND r.mouvement_id IS NOT NULL) THEN
        RETURN NULL;
    END IF;
    RETURN NEW;
END $$;

DROP TRIGGER IF EXISTS trg_registre_sans_doublon ON registre_psychotrope;
CREATE TRIGGER trg_registre_sans_doublon BEFORE INSERT ON registre_psychotrope
    FOR EACH ROW EXECUTE FUNCTION eviter_doublon_registre();

-- Solde d'ouverture : stock actuel de chaque lot sous registre, une fois par produit,
-- au nom du pharmacien titulaire (responsable du registre)
INSERT INTO registre_psychotrope (lot_id, type_mouvement, quantite, solde_apres, utilisateur_id, reference)
SELECT l.id, 'ouverture', l.quantite_disponible, l.quantite_disponible, t.id, 'Solde d''ouverture du registre informatisé'
FROM lot l
JOIN produit p ON p.id = l.produit_id
CROSS JOIN LATERAL (SELECT u.id FROM utilisateur u JOIN role r ON r.id = u.role_id
                    WHERE r.code = 'titulaire' AND u.actif ORDER BY u.id LIMIT 1) t
WHERE p.classe IN ('psychotrope', 'stupefiant')
  AND l.quantite_disponible > 0
  AND NOT EXISTS (SELECT 1 FROM registre_psychotrope r JOIN lot l2 ON l2.id = r.lot_id
                  WHERE l2.produit_id = p.id AND r.type_mouvement = 'ouverture');

-- Lecture : numéro d'ordre, entrées, sorties, solde par produit (à partir de l'ouverture) et par lot
CREATE OR REPLACE VIEW v_registre_psychotrope AS
WITH base AS (
    SELECT r.*, l.produit_id, l.numero_lot,
           CASE WHEN r.type_mouvement IN ('ouverture', 'entree', 'retour_client', 'retour_refuse', 'ecart_plus')
                THEN r.quantite ELSE -r.quantite END AS signe,
           min(r.numero_ordre) FILTER (WHERE r.type_mouvement = 'ouverture') OVER (PARTITION BY l.produit_id) AS ouverture_id
    FROM registre_psychotrope r JOIN lot l ON l.id = r.lot_id
)
SELECT b.numero_ordre AS numero, b.cree_le, b.produit_id,
       p.nom_commercial AS produit, p.classe,
       b.lot_id, b.numero_lot, b.type_mouvement,
       CASE WHEN b.signe > 0 THEN b.quantite END AS entree,
       CASE WHEN b.signe < 0 THEN b.quantite END AS sortie,
       CASE WHEN b.numero_ordre >= coalesce(b.ouverture_id, 0)
            THEN sum(CASE WHEN b.numero_ordre >= coalesce(b.ouverture_id, 0) THEN b.signe ELSE 0 END)
                 OVER (PARTITION BY b.produit_id ORDER BY b.numero_ordre) END::int AS solde_produit,
       b.solde_apres AS solde_lot,
       b.patient_id, trim(concat_ws(' ', pa.nom, pa.prenom)) AS patient,
       m.nom AS medecin, o.numero AS ordonnance, o.date_prescription,
       f.raison_sociale AS fournisseur,
       coalesce(v.numero, b.reference) AS reference,
       b.porteur_nom, b.porteur_adresse, b.porteur_piece,
       u.nom_complet AS employe
FROM base b
JOIN produit p ON p.id = b.produit_id
JOIN utilisateur u ON u.id = b.utilisateur_id
LEFT JOIN patient pa ON pa.id = b.patient_id
LEFT JOIN medecin m ON m.id = b.medecin_id
LEFT JOIN ordonnance o ON o.id = b.ordonnance_id
LEFT JOIN ligne_vente lv ON lv.id = b.ligne_vente_id
LEFT JOIN vente v ON v.id = lv.vente_id
LEFT JOIN mouvement_stock ms ON ms.id = b.mouvement_id
LEFT JOIN reception rc ON rc.id = ms.reception_id
LEFT JOIN fournisseur f ON f.id = rc.fournisseur_id;
