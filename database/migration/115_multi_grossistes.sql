-- =====================================================================
-- Multi-grossistes (lot 6, M3a ; go du titulaire du 2026-11-16)
--   * profil_catalogue_grossiste : correspondance des colonnes du fichier
--     catalogue / disponibilité de CHAQUE grossiste (formats des grossistes
--     inconnus [À VÉRIFIER] : rien n'est supposé, le profil se règle à
--     l'écran, comme pour la reprise).
--   * import_catalogue_grossiste : trace de chaque fichier importé (date,
--     auteur, lignes lues, rapprochées, non rapprochées).
--   * catalogue_grossiste : dernier prix HT, disponibilité et délai connus
--     par produit et par grossiste, avec la date de l'import qui les a
--     donnés. Un nouvel import remplace la ligne du même produit chez le
--     même grossiste (les autres lignes restent).
--   * commande_fournisseur.envoyee_email_le / envoyee_email_a : dernier
--     envoi du bon par e-mail (SMTP de la pharmacie).
--   Aucune donnée existante supprimée ni modifiée. Idempotent.
--   À exécuter après 111 (ne dépend pas des scripts 112 à 114). Si 111b a
--   été appliqué : lancer avec « SET ROLE pharma_proprietaire; » (les
--   droits de pharma_app suivent par les privilèges par défaut).
-- =====================================================================

-- ---- 1. Profil de colonnes par grossiste ---------------------------------
CREATE TABLE IF NOT EXISTS profil_catalogue_grossiste (
    fournisseur_id        bigint PRIMARY KEY REFERENCES fournisseur(id),
    colonne_code          text,          -- code produit chez le grossiste
    colonne_code_barre    text,          -- code-barres (EAN-13)
    colonne_designation   text,
    colonne_prix_ht       text,
    colonne_disponibilite text,          -- quantité disponible ou texte
    colonne_delai         text,          -- délai de livraison en jours
    -- Valeurs texte de la colonne disponibilité qui veulent dire « indisponible »
    -- (séparées par « ; ») ; une quantité 0 l'est toujours
    valeurs_indisponible  text,
    -- Valeurs texte qui veulent dire « disponible » (séparées par « ; ») ;
    -- une valeur reconnue dans aucune des deux listes reste « inconnue »
    valeurs_disponible    text,
    modifie_le            timestamptz NOT NULL DEFAULT now(),
    modifie_par           bigint REFERENCES utilisateur(id)
);

-- Base où une première version du script a déjà tourné
ALTER TABLE profil_catalogue_grossiste ADD COLUMN IF NOT EXISTS valeurs_disponible text;

-- ---- 2. Imports de fichiers catalogue -------------------------------------
CREATE TABLE IF NOT EXISTS import_catalogue_grossiste (
    id                bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    fournisseur_id    bigint NOT NULL REFERENCES fournisseur(id),
    nom_fichier       text NOT NULL,
    importe_le        timestamptz NOT NULL DEFAULT now(),
    utilisateur_id    bigint REFERENCES utilisateur(id),
    lignes_lues       integer NOT NULL DEFAULT 0 CHECK (lignes_lues >= 0),
    lignes_rapprochees integer NOT NULL DEFAULT 0 CHECK (lignes_rapprochees >= 0),
    lignes_ignorees   integer NOT NULL DEFAULT 0 CHECK (lignes_ignorees >= 0)
);
CREATE INDEX IF NOT EXISTS idx_import_catalogue_grossiste
    ON import_catalogue_grossiste (fournisseur_id, importe_le DESC);

-- ---- 3. Dernier état connu par produit et grossiste ------------------------
CREATE TABLE IF NOT EXISTS catalogue_grossiste (
    fournisseur_id      bigint NOT NULL REFERENCES fournisseur(id),
    produit_id          bigint NOT NULL REFERENCES produit(id),
    code_grossiste      text,
    designation         text,
    prix_ht             numeric(12,2) CHECK (prix_ht IS NULL OR prix_ht >= 0),
    disponible          boolean,         -- null : inconnu (colonne absente du profil)
    quantite_disponible integer CHECK (quantite_disponible IS NULL OR quantite_disponible >= 0),
    delai_jours         integer CHECK (delai_jours IS NULL OR delai_jours >= 0),
    import_id           bigint REFERENCES import_catalogue_grossiste(id),
    maj_le              timestamptz NOT NULL DEFAULT now(),
    PRIMARY KEY (fournisseur_id, produit_id)
);
CREATE INDEX IF NOT EXISTS idx_catalogue_grossiste_produit ON catalogue_grossiste (produit_id);
-- Rapprochement d'un import suivant par le code produit du grossiste
CREATE INDEX IF NOT EXISTS idx_catalogue_grossiste_code
    ON catalogue_grossiste (fournisseur_id, code_grossiste) WHERE code_grossiste IS NOT NULL;

-- ---- 4. Envoi du bon par e-mail --------------------------------------------
ALTER TABLE commande_fournisseur ADD COLUMN IF NOT EXISTS envoyee_email_le timestamptz;
ALTER TABLE commande_fournisseur ADD COLUMN IF NOT EXISTS envoyee_email_a text;
