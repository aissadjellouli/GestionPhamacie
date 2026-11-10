-- =====================================================================
-- Factures clients
--   * destinataires : patient, entreprise (fiche client_facture : raison
--     sociale, adresse, NIF, RC, NIS, AI) ou organisme (CNAS, CASNOS et
--     autres organismes ajoutés par le titulaire, ex. celui des militaires) ;
--   * contenu : ventes validées (une, plusieurs, par ordonnance ou par
--     période) et lignes libres ; une vente n'est facturée qu'une fois par
--     part (patient / organisme) tant que la facture n'est pas rejetée ;
--   * numéros sans trou, par année : F-2026-0001 (factures), AV-2026-0001
--     (avoirs), attribués sous verrou (facture_compteur) ;
--   * facture rejetée par l'organisme : corrigée sur place (version + 1,
--     versions précédentes gardées dans facture_client_version) ou
--     remplacée par une nouvelle facture (liens remplace / remplacee_par) ;
--   * suivi : reste à encaisser = TTC - déjà payé au comptoir - avoirs -
--     règlements (versements saisis en trésorerie) ; le pharmacien peut
--     retirer une facture du suivi (motif) et l'y remettre ;
--   * mentions de la pharmacie : RC, NIF, NIS, article d'imposition ;
--   * droit « factures.gerer » : titulaire, qui l'accorde à qui il veut.
-- Idempotent. A exécuter après 030_tresorerie.sql.
-- =====================================================================

DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM permission WHERE code = 'factures.gerer') THEN
        INSERT INTO permission (code, module, libelle, sensible) VALUES ('factures.gerer', 'Factures', 'Émettre et gérer les factures clients', true);
        ALTER TABLE role_permission DISABLE TRIGGER trg_proteger_role_systeme;
        INSERT INTO role_permission (role_id, permission_code) SELECT id, 'factures.gerer' FROM role WHERE systeme;
        ALTER TABLE role_permission ENABLE TRIGGER trg_proteger_role_systeme;
    END IF;
END $$;

ALTER TABLE pharmacie ADD COLUMN IF NOT EXISTS rc text;
ALTER TABLE pharmacie ADD COLUMN IF NOT EXISTS nis text;
ALTER TABLE pharmacie ADD COLUMN IF NOT EXISTS article_imposition text;

-- Organismes : adresse et identifiants pour la facture papier
ALTER TABLE organisme ADD COLUMN IF NOT EXISTS adresse text;
ALTER TABLE organisme ADD COLUMN IF NOT EXISTS nif text;
ALTER TABLE organisme ADD COLUMN IF NOT EXISTS actif boolean NOT NULL DEFAULT true;

CREATE TABLE IF NOT EXISTS client_facture (
    id                  bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    raison_sociale      text        NOT NULL UNIQUE,
    adresse             text,
    telephone           text,
    nif                 text,
    rc                  text,
    nis                 text,
    article_imposition  text,
    actif               boolean     NOT NULL DEFAULT true,
    cree_le             timestamptz NOT NULL DEFAULT now()
);

CREATE TABLE IF NOT EXISTS facture_compteur (
    type     text NOT NULL CHECK (type IN ('facture', 'avoir')),
    annee    int  NOT NULL,
    dernier  int  NOT NULL DEFAULT 0,
    PRIMARY KEY (type, annee)
);

CREATE TABLE IF NOT EXISTS facture_client (
    id                   bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    type                 text          NOT NULL CHECK (type IN ('facture', 'avoir')),
    numero               text          NOT NULL UNIQUE,
    date_facture         date          NOT NULL,
    destinataire         text          NOT NULL CHECK (destinataire IN ('patient', 'entreprise', 'organisme')),
    patient_id           bigint        REFERENCES patient(id),
    client_id            bigint        REFERENCES client_facture(id),
    organisme_id         bigint        REFERENCES organisme(id),
    -- Destinataire tel qu'imprimé (nom, adresse, identifiants), figé à l'émission
    destinataire_nom     text          NOT NULL,
    destinataire_infos   text,
    periode_du           date,
    periode_au           date,
    montant_ht           numeric(12,2) NOT NULL,
    montant_tva          numeric(12,2) NOT NULL,
    montant_ttc          numeric(12,2) NOT NULL,
    deja_paye            numeric(12,2) NOT NULL DEFAULT 0,   -- payé au comptoir pour les ventes facturées
    statut               text          NOT NULL DEFAULT 'emise' CHECK (statut IN ('emise', 'rejetee', 'remplacee')),
    version              int           NOT NULL DEFAULT 1,
    motif_rejet          text,
    remplace_id          bigint        REFERENCES facture_client(id),
    remplacee_par        bigint        REFERENCES facture_client(id),
    facture_origine_id   bigint        REFERENCES facture_client(id),  -- avoir : facture corrigée
    motif                text,                                          -- avoir : motif
    hors_suivi           boolean       NOT NULL DEFAULT false,
    motif_hors_suivi     text,
    commentaire          text,
    utilisateur_id       bigint        NOT NULL REFERENCES utilisateur(id),
    cree_le              timestamptz   NOT NULL DEFAULT now(),
    CHECK (destinataire <> 'patient' OR patient_id IS NOT NULL),
    CHECK (destinataire <> 'entreprise' OR client_id IS NOT NULL),
    CHECK (destinataire <> 'organisme' OR organisme_id IS NOT NULL),
    CHECK (type = 'facture' OR facture_origine_id IS NOT NULL),
    CHECK (NOT hors_suivi OR motif_hors_suivi IS NOT NULL)
);
CREATE INDEX IF NOT EXISTS idx_facture_client_date ON facture_client (date_facture);

-- part : patient (part payée par le patient), organisme (part prise en charge), libre (ligne saisie)
CREATE TABLE IF NOT EXISTS facture_client_ligne (
    id                  bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    facture_id          bigint        NOT NULL REFERENCES facture_client(id),
    ordre               int           NOT NULL,
    vente_id            bigint        REFERENCES vente(id),
    ligne_vente_id      bigint        REFERENCES ligne_vente(id),
    part                text          NOT NULL CHECK (part IN ('patient', 'organisme', 'libre')),
    designation         text          NOT NULL,
    quantite            numeric(10,2) NOT NULL,
    prix_unitaire_ttc   numeric(12,2) NOT NULL,
    taux_tva            numeric(5,2)  NOT NULL DEFAULT 0,
    montant_ttc         numeric(12,2) NOT NULL,
    CHECK (part = 'libre' OR vente_id IS NOT NULL)
);
CREATE INDEX IF NOT EXISTS idx_facture_ligne_facture ON facture_client_ligne (facture_id);
CREATE INDEX IF NOT EXISTS idx_facture_ligne_vente ON facture_client_ligne (vente_id, part);

-- Versions précédentes d'une facture corrigée après rejet (contenu complet, réimprimable)
CREATE TABLE IF NOT EXISTS facture_client_version (
    facture_id      bigint      NOT NULL REFERENCES facture_client(id),
    version         int         NOT NULL,
    contenu         jsonb       NOT NULL,
    motif           text,
    utilisateur_id  bigint      NOT NULL REFERENCES utilisateur(id),
    cree_le         timestamptz NOT NULL DEFAULT now(),
    PRIMARY KEY (facture_id, version)
);

-- Règlement de factures par un mouvement de trésorerie (versement d'organisme, encaissement)
CREATE TABLE IF NOT EXISTS reglement_facture_client (
    mouvement_id  bigint        NOT NULL REFERENCES mouvement_tresorerie(id),
    facture_id    bigint        NOT NULL REFERENCES facture_client(id),
    montant       numeric(12,2) NOT NULL CHECK (montant > 0),
    PRIMARY KEY (mouvement_id, facture_id)
);

-- Suivi : reste à encaisser (factures émises ou rejetées ; une facture remplacée ne compte plus)
CREATE OR REPLACE VIEW v_facture_client AS
SELECT f.*,
       COALESCE((SELECT SUM(a.montant_ttc) FROM facture_client a WHERE a.facture_origine_id = f.id AND a.type = 'avoir'), 0) AS avoirs,
       COALESCE((SELECT SUM(r.montant) FROM reglement_facture_client r WHERE r.facture_id = f.id), 0) AS regle,
       CASE WHEN f.type = 'avoir' OR f.statut = 'remplacee' THEN 0
            ELSE GREATEST(f.montant_ttc - f.deja_paye
                 - COALESCE((SELECT SUM(a.montant_ttc) FROM facture_client a WHERE a.facture_origine_id = f.id AND a.type = 'avoir'), 0)
                 - COALESCE((SELECT SUM(r.montant) FROM reglement_facture_client r WHERE r.facture_id = f.id), 0), 0) END AS reste,
       u.nom_complet AS employe,
       (SELECT numero FROM facture_client x WHERE x.id = f.remplace_id) AS remplace_numero,
       (SELECT numero FROM facture_client x WHERE x.id = f.remplacee_par) AS remplacee_par_numero,
       (SELECT numero FROM facture_client x WHERE x.id = f.facture_origine_id) AS origine_numero
FROM facture_client f JOIN utilisateur u ON u.id = f.utilisateur_id;

-- Lignes de vente facturables : quantités nettes des retours clients, parts patient et organisme au prorata
CREATE OR REPLACE VIEW v_ligne_vente_facturable AS
SELECT v.id AS vente_id, v.numero, v.date_vente, v.patient_id, v.organisme_id, v.ordonnance_id, o.numero AS ordonnance_numero,
       l.id AS ligne_vente_id, p.nom_commercial AS designation, p.taux_tva, l.prix_unitaire, l.quantite,
       l.quantite - COALESCE(rt.quantite, 0) AS quantite_nette,
       round(l.montant_patient * (l.quantite - COALESCE(rt.quantite, 0)) / l.quantite, 2) AS montant_patient,
       round(l.montant_organisme * (l.quantite - COALESCE(rt.quantite, 0)) / l.quantite, 2) AS montant_organisme,
       v.montant_remise,
       EXISTS (SELECT 1 FROM facture_client_ligne fl JOIN facture_client f ON f.id = fl.facture_id
               WHERE fl.ligne_vente_id = l.id AND fl.part = 'patient' AND f.statut IN ('emise', 'rejetee') AND f.type = 'facture') AS facturee_patient,
       EXISTS (SELECT 1 FROM facture_client_ligne fl JOIN facture_client f ON f.id = fl.facture_id
               WHERE fl.ligne_vente_id = l.id AND fl.part = 'organisme' AND f.statut IN ('emise', 'rejetee') AND f.type = 'facture') AS facturee_organisme
FROM vente v
JOIN ligne_vente l ON l.vente_id = v.id
JOIN produit p ON p.id = l.produit_id
LEFT JOIN ordonnance o ON o.id = v.ordonnance_id
LEFT JOIN (SELECT ligne_vente_id, SUM(quantite) AS quantite FROM retour_client_ligne GROUP BY ligne_vente_id) rt ON rt.ligne_vente_id = l.id
WHERE v.statut = 'validee' AND l.quantite > 0;
