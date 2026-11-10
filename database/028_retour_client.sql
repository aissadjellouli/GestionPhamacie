-- =====================================================================
-- Retour de médicaments par le patient, après la vente
--   * le pharmacien choisit pour chaque produit : remis en stock (même lot)
--     ou à détruire ; refuser le retour n'enregistre rien ;
--   * retour partiel possible (quantité, boîtes identifiées choisies) ;
--   * droit « caisse.retour_especes », ou validation sur place par le code
--     d'un employé qui l'a ;
--   * l'argent rendu (part payée par le patient) sort de la caisse ouverte
--     du poste ; vente à crédit : la dette du patient diminue ;
--   * psychotropes : le registre inscrit le retour, puis la destruction.
-- Idempotent. A exécuter après 027_annulation_vente.sql.
-- =====================================================================

-- Destruction d'une boîte reprise au patient (stock inchangé : entrée puis sortie)
ALTER TABLE mouvement_stock DROP CONSTRAINT IF EXISTS mouvement_stock_type_check;
ALTER TABLE mouvement_stock ADD CONSTRAINT mouvement_stock_type_check CHECK (type IN (
    'entree', 'vente', 'retour_client', 'retour_fournisseur', 'perime', 'casse', 'ajustement_inventaire', 'destruction'));

CREATE TABLE IF NOT EXISTS retour_client (
    id                 bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    vente_id           bigint        NOT NULL REFERENCES vente(id),
    motif              text          NOT NULL CHECK (length(btrim(motif)) >= 3),
    utilisateur_id     bigint        NOT NULL REFERENCES utilisateur(id),
    valide_par         bigint        NOT NULL REFERENCES utilisateur(id),
    mode               text          CHECK (mode IN ('especes', 'carte', 'cheque', 'credit')),
    montant_rembourse  numeric(12,2) NOT NULL DEFAULT 0 CHECK (montant_rembourse >= 0),
    session_caisse_id  bigint        REFERENCES session_caisse(id),
    poste              text,
    cree_le            timestamptz   NOT NULL DEFAULT now()
);
CREATE INDEX IF NOT EXISTS idx_retour_client_vente ON retour_client (vente_id);
CREATE INDEX IF NOT EXISTS idx_retour_client_session ON retour_client (session_caisse_id);

CREATE TABLE IF NOT EXISTS retour_client_ligne (
    id              bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    retour_id       bigint        NOT NULL REFERENCES retour_client(id),
    ligne_vente_id  bigint        NOT NULL REFERENCES ligne_vente(id),
    quantite        int           NOT NULL CHECK (quantite > 0),
    destination     text          NOT NULL CHECK (destination IN ('stock', 'destruction')),
    montant         numeric(12,2) NOT NULL CHECK (montant >= 0)
);
CREATE INDEX IF NOT EXISTS idx_retour_ligne_vente ON retour_client_ligne (ligne_vente_id);

-- Registre des psychotropes : la destruction d'une boîte reprise est inscrite comme telle
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
        WHEN 'destruction'           THEN 'destruction'
        WHEN 'casse'                 THEN 'casse'
        WHEN 'ajustement_inventaire' THEN CASE WHEN NEW.quantite < 0 THEN 'ecart_moins' ELSE 'ecart_plus' END
    END;
    IF v_type IS NULL THEN
        RAISE EXCEPTION 'Type de mouvement % non prévu au registre des psychotropes', NEW.type;
    END IF;
    SELECT greatest(quantite_disponible, 0) INTO v_solde FROM lot WHERE id = NEW.lot_id;
    INSERT INTO registre_psychotrope (lot_id, mouvement_id, type_mouvement, quantite, solde_apres, utilisateur_id, reference, cree_le)
    VALUES (NEW.lot_id, NEW.id, v_type, abs(NEW.quantite), coalesce(v_solde, 0), NEW.utilisateur_id, NEW.reference, NEW.cree_le);
    RETURN NEW;
END $$;
