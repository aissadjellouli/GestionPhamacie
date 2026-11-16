-- =====================================================================
-- Ruptures et substitution (lot 7, M3b ; go du titulaire du 2026-11-16)
--   * rupture_grossiste : historique des passages disponible ⇄ indisponible
--     par produit et grossiste, noté à chaque import de catalogue (lot 6,
--     script 115). Une ligne = une période d'indisponibilité : « debut »
--     (import qui a dit indisponible), « fin » (import qui a dit de
--     nouveau disponible ; null = toujours en rupture). Une disponibilité
--     inconnue (colonne absente ou texte non reconnu) ne change rien.
--   * demande_patient : file des patients en attente d'un produit (saisie
--     « Prévenir quand disponible » au comptoir). Les produits dus des
--     ventes en attente (script 094) sont lus directement dans
--     panier_attente.produits_dus, sans copie.
--   * patient.consentement_sms / consentement_sms_le : accord du patient
--     pour recevoir un SMS « commande disponible » (aucun SMS sans lui).
--   * parametre_substitution : « équivalent exact seulement » (même DCI,
--     dosage et forme), ACTIVÉ par défaut (critère d'acceptation M3).
--   * produit.fabrication_locale : « fabriqué en Algérie » saisi (null =
--     non saisi : la vue v_produit_fabrication_locale reprend alors le
--     pays de la nomenclature nationale importée, s'il est connu).
--   * substitution_liste_blanche : produits autorisés par le pharmacien
--     en remplacement d'un produit donné.
--   Aucune donnée existante supprimée ni modifiée. Idempotent.
--   À exécuter après 115. Si 111b a été appliqué : lancer avec
--   « SET ROLE pharma_proprietaire; ».
-- =====================================================================

-- ---- 1. Historique des ruptures chez les grossistes ------------------------
CREATE TABLE IF NOT EXISTS rupture_grossiste (
    id               bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    fournisseur_id   bigint NOT NULL REFERENCES fournisseur(id),
    produit_id       bigint NOT NULL REFERENCES produit(id),
    debut            timestamptz NOT NULL DEFAULT now(),
    import_debut_id  bigint REFERENCES import_catalogue_grossiste(id),
    fin              timestamptz,
    import_fin_id    bigint REFERENCES import_catalogue_grossiste(id),
    CONSTRAINT ck_rupture_grossiste_periode CHECK (fin IS NULL OR fin >= debut)
);
-- Une seule période ouverte par produit et grossiste
CREATE UNIQUE INDEX IF NOT EXISTS uq_rupture_grossiste_ouverte
    ON rupture_grossiste (fournisseur_id, produit_id) WHERE fin IS NULL;
CREATE INDEX IF NOT EXISTS idx_rupture_grossiste_produit ON rupture_grossiste (produit_id, debut DESC);

-- ---- 2. Patients en attente d'un produit ------------------------------------
CREATE TABLE IF NOT EXISTS demande_patient (
    id            bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    patient_id    bigint NOT NULL REFERENCES patient(id),
    produit_id    bigint NOT NULL REFERENCES produit(id),
    quantite      integer NOT NULL DEFAULT 1 CHECK (quantite BETWEEN 1 AND 1000),
    cree_le       timestamptz NOT NULL DEFAULT now(),
    cree_par      bigint REFERENCES utilisateur(id),
    -- en_attente → prevenu → servie ; annulee à tout moment avant « servie »
    statut        text NOT NULL DEFAULT 'en_attente'
                  CHECK (statut IN ('en_attente', 'prevenu', 'servie', 'annulee')),
    prevenu_le    timestamptz,
    prevenu_par   bigint REFERENCES utilisateur(id),
    -- comptoir : prévenu de vive voix ou par téléphone ; sms : SMS par le modem local
    prevenu_canal text CHECK (prevenu_canal IS NULL OR prevenu_canal IN ('comptoir', 'sms')),
    sms_erreur    text,
    cloture_le    timestamptz,
    cloture_par   bigint REFERENCES utilisateur(id)
);
CREATE INDEX IF NOT EXISTS idx_demande_patient_ouverte
    ON demande_patient (produit_id) WHERE statut IN ('en_attente', 'prevenu');
CREATE INDEX IF NOT EXISTS idx_demande_patient_patient ON demande_patient (patient_id, cree_le DESC);

-- ---- 3. Consentement SMS du patient -----------------------------------------
ALTER TABLE patient ADD COLUMN IF NOT EXISTS consentement_sms boolean NOT NULL DEFAULT false;
ALTER TABLE patient ADD COLUMN IF NOT EXISTS consentement_sms_le timestamptz;

-- ---- 4. Paramètre de substitution -------------------------------------------
CREATE TABLE IF NOT EXISTS parametre_substitution (
    id                           integer PRIMARY KEY DEFAULT 1 CHECK (id = 1),
    equivalent_exact_seulement   boolean NOT NULL DEFAULT true,
    modifie_le                   timestamptz NOT NULL DEFAULT now(),
    modifie_par                  bigint REFERENCES utilisateur(id)
);
INSERT INTO parametre_substitution (id) VALUES (1) ON CONFLICT (id) DO NOTHING;

-- ---- 5. Fabriqué en Algérie -------------------------------------------------
ALTER TABLE produit ADD COLUMN IF NOT EXISTS fabrication_locale boolean;

-- Valeur retenue : la saisie, sinon le pays de la dernière version de la
-- nomenclature qui connaît le n° d'enregistrement (null si inconnu)
CREATE OR REPLACE VIEW v_produit_fabrication_locale AS
SELECT p.id AS produit_id,
       COALESCE(p.fabrication_locale, n.locale) AS fabrication_locale,
       CASE WHEN p.fabrication_locale IS NOT NULL THEN 'saisie'
            WHEN n.locale IS NOT NULL THEN 'nomenclature' END AS source
FROM produit p
LEFT JOIN LATERAL (
    SELECT upper(btrim(np.pays)) IN ('ALGERIE', 'ALGÉRIE') AS locale
    FROM nomenclature_produit np
    WHERE p.num_enregistrement IS NOT NULL AND np.num_enregistrement = p.num_enregistrement
      AND np.pays IS NOT NULL AND btrim(np.pays) <> ''
    ORDER BY np.version_id DESC
    LIMIT 1
) n ON true;

-- ---- 6. Liste blanche de substitution ---------------------------------------
CREATE TABLE IF NOT EXISTS substitution_liste_blanche (
    produit_id      bigint NOT NULL REFERENCES produit(id),
    remplacant_id   bigint NOT NULL REFERENCES produit(id),
    cree_le         timestamptz NOT NULL DEFAULT now(),
    cree_par        bigint REFERENCES utilisateur(id),
    PRIMARY KEY (produit_id, remplacant_id),
    CONSTRAINT ck_substitution_liste_blanche_distincts CHECK (produit_id <> remplacant_id)
);
CREATE INDEX IF NOT EXISTS idx_substitution_liste_blanche_remplacant ON substitution_liste_blanche (remplacant_id);
