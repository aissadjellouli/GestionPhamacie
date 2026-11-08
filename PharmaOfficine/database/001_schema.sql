-- =====================================================================
-- Schéma de base de données : logiciel de gestion d'officine (Algérie)
-- PostgreSQL 17+
-- =====================================================================

CREATE EXTENSION IF NOT EXISTS pg_trgm;

-- ---------------------------------------------------------------------
-- 1. Utilisateurs et sécurité
-- ---------------------------------------------------------------------
CREATE TABLE utilisateur (
    id                  bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    nom_complet         text        NOT NULL,
    identifiant         text        NOT NULL UNIQUE,
    mot_de_passe_hash   text        NOT NULL,
    role                text        NOT NULL CHECK (role IN ('admin', 'pharmacien', 'assistant')),
    actif               boolean     NOT NULL DEFAULT true,
    cree_le             timestamptz NOT NULL DEFAULT now()
);

-- ---------------------------------------------------------------------
-- 2. Catalogue (nomenclature)
-- ---------------------------------------------------------------------
CREATE TABLE dci (
    id      bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    nom     text NOT NULL UNIQUE
);

CREATE TABLE laboratoire (
    id      bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    nom     text NOT NULL,
    pays    text
);

CREATE TABLE produit (
    id                  bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    num_enregistrement  text UNIQUE,                 -- numéro de la nomenclature
    code_barre          text UNIQUE,
    nom_commercial      text          NOT NULL,
    dci_id              bigint        REFERENCES dci(id),
    laboratoire_id      bigint        REFERENCES laboratoire(id),
    dosage              text,
    forme               text,
    conditionnement     text,
    prix_public         numeric(12,2) NOT NULL DEFAULT 0 CHECK (prix_public >= 0),
    tarif_reference     numeric(12,2) CHECK (tarif_reference >= 0),  -- base de remboursement
    remboursable        boolean       NOT NULL DEFAULT false,
    taux_tva            numeric(5,2)  NOT NULL DEFAULT 0,
    classe              text          NOT NULL DEFAULT 'libre'
                        CHECK (classe IN ('libre', 'liste_i', 'liste_ii', 'psychotrope', 'stupefiant')),
    est_essentiel       boolean       NOT NULL DEFAULT false,       -- liste nationale des médicaments essentiels
    stock_minimum       integer       NOT NULL DEFAULT 0,
    actif               boolean       NOT NULL DEFAULT true,
    cree_le             timestamptz   NOT NULL DEFAULT now()
);

-- Recherche rapide et tolérante aux fautes au comptoir
CREATE INDEX idx_produit_nom_trgm ON produit USING gin (nom_commercial gin_trgm_ops);
CREATE INDEX idx_dci_nom_trgm     ON dci     USING gin (nom gin_trgm_ops);

-- ---------------------------------------------------------------------
-- 3. Fournisseurs et achats
-- ---------------------------------------------------------------------
CREATE TABLE fournisseur (
    id              bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    raison_sociale  text          NOT NULL,
    nif             text,
    rc              text,
    telephone       text,
    adresse         text,
    solde           numeric(12,2) NOT NULL DEFAULT 0   -- dette envers le fournisseur
);

CREATE TABLE commande_fournisseur (
    id              bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    fournisseur_id  bigint        NOT NULL REFERENCES fournisseur(id),
    numero          text          NOT NULL UNIQUE,
    date_commande   timestamptz   NOT NULL DEFAULT now(),
    statut          text          NOT NULL DEFAULT 'brouillon'
                    CHECK (statut IN ('brouillon', 'envoyee', 'recue_partielle', 'recue', 'annulee')),
    montant_total   numeric(12,2) NOT NULL DEFAULT 0,
    utilisateur_id  bigint        REFERENCES utilisateur(id)
);

CREATE TABLE ligne_commande (
    id              bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    commande_id     bigint        NOT NULL REFERENCES commande_fournisseur(id) ON DELETE CASCADE,
    produit_id      bigint        NOT NULL REFERENCES produit(id),
    qte_commandee   integer       NOT NULL CHECK (qte_commandee > 0),
    qte_recue       integer       NOT NULL DEFAULT 0 CHECK (qte_recue >= 0),
    prix_achat_ht   numeric(12,2) NOT NULL CHECK (prix_achat_ht >= 0)
);

-- ---------------------------------------------------------------------
-- 4. Stock par lot
-- ---------------------------------------------------------------------
CREATE TABLE lot (
    id                  bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    produit_id          bigint        NOT NULL REFERENCES produit(id),
    ligne_commande_id   bigint        REFERENCES ligne_commande(id),
    numero_lot          text          NOT NULL,
    date_peremption     date          NOT NULL,
    quantite_disponible integer       NOT NULL DEFAULT 0 CHECK (quantite_disponible >= 0),
    prix_achat_ht       numeric(12,2) NOT NULL DEFAULT 0,
    UNIQUE (produit_id, numero_lot)
);

-- Sert la règle FEFO : premier périmé, premier sorti
CREATE INDEX idx_lot_fefo ON lot (produit_id, date_peremption) WHERE quantite_disponible > 0;

CREATE TABLE mouvement_stock (
    id              bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    lot_id          bigint      NOT NULL REFERENCES lot(id),
    type            text        NOT NULL CHECK (type IN (
                        'entree', 'vente', 'retour_client', 'retour_fournisseur',
                        'perime', 'casse', 'ajustement_inventaire')),
    quantite        integer     NOT NULL,          -- positif = entrée, négatif = sortie
    reference       text,                          -- n° de vente, de commande, etc.
    utilisateur_id  bigint      REFERENCES utilisateur(id),
    cree_le         timestamptz NOT NULL DEFAULT now()
);
CREATE INDEX idx_mouvement_lot ON mouvement_stock (lot_id, cree_le);

-- ---------------------------------------------------------------------
-- 5. Patients, prescripteurs, ordonnances
-- ---------------------------------------------------------------------
CREATE TABLE organisme (
    id      bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    code    text NOT NULL UNIQUE,     -- 'CNAS', 'CASNOS', ...
    nom     text NOT NULL
);

CREATE TABLE patient (
    id              bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    nom             text          NOT NULL,
    prenom          text,
    date_naissance  date,
    telephone       text,
    num_assure      text,
    organisme_id    bigint        REFERENCES organisme(id),
    est_chronique   boolean       NOT NULL DEFAULT false,
    plafond_credit  numeric(12,2) NOT NULL DEFAULT 0,
    solde_credit    numeric(12,2) NOT NULL DEFAULT 0,   -- ce que le patient doit à l'officine
    cree_le         timestamptz   NOT NULL DEFAULT now()
);
CREATE INDEX idx_patient_nom_trgm ON patient USING gin (nom gin_trgm_ops);
CREATE INDEX idx_patient_num_assure ON patient (num_assure);

CREATE TABLE medecin (
    id                  bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    nom                 text NOT NULL,
    specialite          text,
    code_prestataire    text
);

CREATE TABLE ordonnance (
    id                  bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    patient_id          bigint NOT NULL REFERENCES patient(id),
    medecin_id          bigint REFERENCES medecin(id),
    date_prescription   date   NOT NULL,
    numero              text,
    chemin_scan         text            -- copie numérisée stockée sur le serveur
);

-- ---------------------------------------------------------------------
-- 6. Tiers payant : bordereaux
-- ---------------------------------------------------------------------
CREATE TABLE bordereau (
    id              bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    organisme_id    bigint        NOT NULL REFERENCES organisme(id),
    numero          text          NOT NULL UNIQUE,
    periode_debut   date          NOT NULL,
    periode_fin     date          NOT NULL,
    montant_total   numeric(12,2) NOT NULL DEFAULT 0,
    statut          text          NOT NULL DEFAULT 'ouvert'
                    CHECK (statut IN ('ouvert', 'envoye', 'paye', 'rejet_partiel')),
    date_envoi      date,
    CHECK (periode_fin >= periode_debut)
);

-- ---------------------------------------------------------------------
-- 7. Ventes
-- ---------------------------------------------------------------------
CREATE TABLE vente (
    id              bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    numero          text          NOT NULL UNIQUE,
    date_vente      timestamptz   NOT NULL DEFAULT now(),
    utilisateur_id  bigint        NOT NULL REFERENCES utilisateur(id),
    patient_id      bigint        REFERENCES patient(id),
    ordonnance_id   bigint        REFERENCES ordonnance(id),
    type            text          NOT NULL CHECK (type IN ('libre', 'tiers_payant')),
    organisme_id    bigint        REFERENCES organisme(id),
    bordereau_id    bigint        REFERENCES bordereau(id),
    montant_total   numeric(12,2) NOT NULL DEFAULT 0,
    part_organisme  numeric(12,2) NOT NULL DEFAULT 0,
    part_patient    numeric(12,2) NOT NULL DEFAULT 0,
    statut          text          NOT NULL DEFAULT 'en_cours'
                    CHECK (statut IN ('en_cours', 'validee', 'annulee')),
    CHECK (type = 'libre' OR (organisme_id IS NOT NULL AND patient_id IS NOT NULL))
);
CREATE INDEX idx_vente_date ON vente (date_vente);
CREATE INDEX idx_vente_bordereau ON vente (bordereau_id);

CREATE TABLE ligne_vente (
    id                  bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    vente_id            bigint        NOT NULL REFERENCES vente(id) ON DELETE CASCADE,
    produit_id          bigint        NOT NULL REFERENCES produit(id),
    lot_id              bigint        NOT NULL REFERENCES lot(id),
    quantite            integer       NOT NULL CHECK (quantite > 0),
    prix_unitaire       numeric(12,2) NOT NULL,
    base_remboursement  numeric(12,2) NOT NULL DEFAULT 0,
    taux_remboursement  numeric(5,2)  NOT NULL DEFAULT 0 CHECK (taux_remboursement BETWEEN 0 AND 100),
    montant_organisme   numeric(12,2) NOT NULL DEFAULT 0,
    montant_patient     numeric(12,2) NOT NULL DEFAULT 0
);
CREATE INDEX idx_ligne_vente_lot ON ligne_vente (lot_id);

CREATE TABLE paiement (
    id              bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    vente_id        bigint        REFERENCES vente(id),
    patient_id      bigint        REFERENCES patient(id),
    mode            text          NOT NULL CHECK (mode IN (
                        'especes', 'carte', 'cheque', 'credit', 'remboursement_credit')),
    montant         numeric(12,2) NOT NULL CHECK (montant > 0),
    utilisateur_id  bigint        REFERENCES utilisateur(id),
    cree_le         timestamptz   NOT NULL DEFAULT now(),
    CHECK (vente_id IS NOT NULL OR patient_id IS NOT NULL)
);

-- ---------------------------------------------------------------------
-- 8. Registre des psychotropes et stupéfiants (non modifiable)
-- ---------------------------------------------------------------------
CREATE TABLE registre_psychotrope (
    id              bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    lot_id          bigint      NOT NULL REFERENCES lot(id),
    ligne_vente_id  bigint      UNIQUE REFERENCES ligne_vente(id),
    type_mouvement  text        NOT NULL CHECK (type_mouvement IN ('entree', 'sortie', 'destruction')),
    quantite        integer     NOT NULL CHECK (quantite > 0),
    solde_apres     integer     NOT NULL CHECK (solde_apres >= 0),
    patient_id      bigint      REFERENCES patient(id),
    medecin_id      bigint      REFERENCES medecin(id),
    ordonnance_id   bigint      REFERENCES ordonnance(id),
    utilisateur_id  bigint      NOT NULL REFERENCES utilisateur(id),
    cree_le         timestamptz NOT NULL DEFAULT now(),
    CHECK (type_mouvement <> 'sortie' OR ordonnance_id IS NOT NULL)
);

CREATE OR REPLACE FUNCTION interdire_modification() RETURNS trigger AS $$
BEGIN
    RAISE EXCEPTION 'Le registre des psychotropes ne peut être ni modifié ni supprimé';
END;
$$ LANGUAGE plpgsql;

CREATE TRIGGER trg_registre_immuable
    BEFORE UPDATE OR DELETE ON registre_psychotrope
    FOR EACH ROW EXECUTE FUNCTION interdire_modification();

-- ---------------------------------------------------------------------
-- 9. Journal d'audit
-- ---------------------------------------------------------------------
CREATE TABLE journal_audit (
    id                  bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    utilisateur_id      bigint      REFERENCES utilisateur(id),
    action              text        NOT NULL,
    table_cible         text,
    enregistrement_id   bigint,
    details             jsonb,
    cree_le             timestamptz NOT NULL DEFAULT now()
);
CREATE INDEX idx_audit_date ON journal_audit (cree_le);

-- ---------------------------------------------------------------------
-- 10. Données initiales
-- ---------------------------------------------------------------------
INSERT INTO organisme (code, nom) VALUES
    ('CNAS',   'Caisse nationale des assurances sociales des travailleurs salariés'),
    ('CASNOS', 'Caisse nationale de sécurité sociale des non-salariés');
