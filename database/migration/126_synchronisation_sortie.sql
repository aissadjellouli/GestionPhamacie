-- =====================================================================
-- Synchronisation officine -> serveur central, lot S1 (2026-11-16,
-- docs/conception-synchronisation.md § 3 et § 10, inventaire :
-- docs/synchronisation-inventaire.md). Côté officine seulement : aucun
-- envoi, aucune adresse de serveur central.
--   * sync_parametre  : réglage « synchronisation active » (DÉSACTIVÉ par
--     défaut), identifiant de l'officine (vide tant que l'officine n'est pas
--     inscrite), époque, version du contrat, bornes des lots, dernier lot
--     constitué, dernier lot accusé (prévu, rempli au lot S3).
--   * sync_table      : inventaire de CHAQUE table du schéma public (sens
--     montante / descendante / bidirectionnelle / jamais), clé stable,
--     colonnes exclues, colonnes remplacées par le code d'un patient,
--     pseudonyme du porteur. sync_tables_non_classees() liste les oubliées.
--   * sync_pseudonyme : correspondance locale personne <-> code aléatoire
--     stable (patient : id ; porteur : empreinte du nom et de la pièce).
--     JAMAIS envoyée.
--   * sync_sortie     : file des modifications (outbox), ajout seul tant que
--     non acquittée ; alimentée dans la MÊME transaction que l'opération par
--     le déclencheur zz_sync_sortie des tables montantes et bidirectionnelles,
--     données déjà pseudonymisées (aucun nom, n° d'assuré ni téléphone).
--   * sync_lot        : lots constitués, numérotés sans trou par époque au
--     moment de la constitution (pas les lignes : voir la conception).
-- Désactivée, la synchronisation n'a AUCUN déclencheur posé : rien n'est
-- inscrit, aucun coût sur les ventes. sync_definir_active(true|false) pose
-- ou retire les déclencheurs. Purge de la file : seulement après accusé du
-- lot (prévue, non lancée). Aucun chiffrement (lot 9 non lancé).
-- Idempotent ; aucune donnée existante modifiée.
-- =====================================================================

CREATE TABLE IF NOT EXISTS sync_parametre (
    id               int         PRIMARY KEY DEFAULT 1 CHECK (id = 1),
    active           boolean     NOT NULL DEFAULT false,
    officine_id      uuid,
    epoque           int         NOT NULL DEFAULT 1 CHECK (epoque >= 1),
    version_contrat  text        NOT NULL DEFAULT 'sync-v1' CHECK (version_contrat ~ '^sync-v[0-9]+$'),
    lot_max_lignes   int         NOT NULL DEFAULT 500 CHECK (lot_max_lignes BETWEEN 1 AND 10000),
    lot_max_octets   int         NOT NULL DEFAULT 1048576 CHECK (lot_max_octets BETWEEN 1024 AND 16777216),
    dernier_lot      bigint      NOT NULL DEFAULT 0 CHECK (dernier_lot >= 0),
    dernier_accuse   bigint      NOT NULL DEFAULT 0 CHECK (dernier_accuse >= 0 AND dernier_accuse <= dernier_lot),
    modifie_le       timestamptz NOT NULL DEFAULT now()
);
INSERT INTO sync_parametre DEFAULT VALUES ON CONFLICT (id) DO NOTHING;

CREATE TABLE IF NOT EXISTS sync_table (
    nom_table               text    PRIMARY KEY,
    sens                    text    NOT NULL CHECK (sens IN ('montante', 'descendante', 'bidirectionnelle', 'jamais')),
    cle_colonnes            text[]  NOT NULL DEFAULT '{id}',
    colonnes_exclues        text[]  NOT NULL DEFAULT '{}',
    colonnes_patient        text[]  NOT NULL DEFAULT '{}',
    porteur                 boolean NOT NULL DEFAULT false,
    motif                   text
);

CREATE TABLE IF NOT EXISTS sync_pseudonyme (
    nature      text        NOT NULL CHECK (nature IN ('patient', 'porteur')),
    cle_locale  text        NOT NULL,
    code        text        NOT NULL UNIQUE CHECK (code ~ '^[PR]-[0-9a-f]{24}$'),
    cree_le     timestamptz NOT NULL DEFAULT now(),
    PRIMARY KEY (nature, cle_locale)
);

CREATE TABLE IF NOT EXISTS sync_lot (
    epoque           int         NOT NULL,
    numero           bigint      NOT NULL CHECK (numero >= 1),
    version_contrat  text        NOT NULL,
    nb_lignes        int         NOT NULL CHECK (nb_lignes >= 1),
    octets           int         NOT NULL CHECK (octets >= 0),
    empreinte        text        NOT NULL CHECK (empreinte ~ '^[0-9a-f]{64}$'),
    constitue_le     timestamptz NOT NULL DEFAULT now(),
    accuse_le        timestamptz,
    PRIMARY KEY (epoque, numero)
);

CREATE TABLE IF NOT EXISTS sync_sortie (
    id               bigint      GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    nom_table        text        NOT NULL,
    id_local         text        NOT NULL,
    operation        text        NOT NULL CHECK (operation IN ('creation', 'modification', 'suppression')),
    donnees          jsonb,
    version_contrat  text        NOT NULL,
    epoque           int         NOT NULL,
    survenu_le       timestamptz NOT NULL DEFAULT clock_timestamp(),
    lot_numero       bigint,
    CONSTRAINT fk_sync_sortie_lot FOREIGN KEY (epoque, lot_numero) REFERENCES sync_lot (epoque, numero),
    CONSTRAINT ck_sync_sortie_donnees CHECK ((operation = 'suppression') = (donnees IS NULL))
);
CREATE INDEX IF NOT EXISTS ix_sync_sortie_en_attente ON sync_sortie (id) WHERE lot_numero IS NULL;
CREATE INDEX IF NOT EXISTS ix_sync_sortie_lot ON sync_sortie (epoque, lot_numero) WHERE lot_numero IS NOT NULL;

-- ---------------------------------------------------------------------
-- Inventaire : une ligne par table du schéma public. Une nouvelle table
-- doit être classée ici (ou par un script suivant) : test d'architecture
-- SynchronisationInventaireTests et sync_tables_non_classees().
-- Colonnes : (table, sens, colonnes exclues, colonnes « patient », porteur)
-- ---------------------------------------------------------------------
INSERT INTO sync_table (nom_table, sens, colonnes_exclues, colonnes_patient, porteur, motif) VALUES
    -- Ventes, paiements, fiscal (montante seule, le local fait foi)
    ('vente', 'montante', '{remis_a_nom,pour_nom,chifa_qr}', '{patient_id,remis_a_patient_id,pour_patient_id}', false, NULL),
    ('ligne_vente', 'montante', '{}', '{}', false, NULL),
    ('paiement', 'montante', '{}', '{patient_id}', false, NULL),
    ('annulation_vente', 'montante', '{}', '{}', false, NULL),
    ('retour_client', 'montante', '{}', '{}', false, NULL),
    ('retour_client_ligne', 'montante', '{}', '{}', false, NULL),
    ('avoir_mouvement', 'montante', '{}', '{patient_id}', false, NULL),
    ('regularisation_part_organisme', 'montante', '{}', '{}', false, NULL),
    ('vente_chifa_facture_conflit', 'montante', '{}', '{}', false, NULL),
    ('journal_vente', 'montante', '{}', '{}', false, 'Journal chaîné, envoyé avec rang et empreinte'),
    ('cloture_fiscale', 'montante', '{}', '{}', false, NULL),
    ('sceau_integrite', 'montante', '{}', '{}', false, 'Sceaux recopiés tels quels ; la clé ne part jamais'),
    ('verification_integrite', 'montante', '{}', '{}', false, NULL),
    -- Registre des psychotropes (porteur et patient sous code)
    ('registre_psychotrope', 'montante', '{porteur_nom,porteur_adresse,porteur_piece}', '{patient_id}', true, NULL),
    ('journal_registre_psychotrope', 'montante', '{porteur_nom,porteur_adresse,porteur_piece}', '{patient_id}', true,
        'Journal chaîné : colonnes du porteur retirées, le serveur central ne peut pas recalculer l''empreinte (lot S4)'),
    ('annulation_registre', 'montante', '{}', '{}', false, NULL),
    ('forcage_alerte_psychotrope', 'montante', '{messages}', '{patient_id}', false, NULL),
    -- Stock
    ('lot', 'montante', '{}', '{}', false, NULL),
    ('boite', 'montante', '{}', '{}', false, NULL),
    ('boite_evenement', 'montante', '{}', '{patient_id}', false, NULL),
    ('mouvement_stock', 'montante', '{}', '{}', false, NULL),
    ('correction_lot', 'montante', '{}', '{}', false, NULL),
    ('inventaire', 'montante', '{}', '{}', false, NULL),
    ('ligne_inventaire', 'montante', '{}', '{}', false, NULL),
    ('campagne_inventaire', 'montante', '{}', '{}', false, NULL),
    ('campagne_inventaire_ligne', 'montante', '{}', '{}', false, NULL),
    ('emplacement', 'montante', '{}', '{}', false, NULL),
    ('verification_serialisation', 'montante', '{}', '{}', false, NULL),
    -- Caisse
    ('session_caisse', 'montante', '{}', '{}', false, NULL),
    ('comptage_caisse', 'montante', '{}', '{}', false, NULL),
    ('comptage_intermediaire', 'montante', '{}', '{}', false, NULL),
    ('ecart_caisse_vu', 'montante', '{}', '{}', false, NULL),
    ('evenement_caisse', 'montante', '{}', '{}', false, NULL),
    ('mouvement_tiroir', 'montante', '{}', '{}', false, NULL),
    ('remise_cheques', 'montante', '{}', '{}', false, NULL),
    ('remise_cheque_paiement', 'montante', '{}', '{}', false, NULL),
    -- Journaux d'actions
    ('journal_audit', 'montante', '{details}', '{}', false, 'Détails libres retirés (peuvent contenir des données personnelles)'),
    ('connexion_employe', 'montante', '{}', '{}', false, NULL),
    ('journal_sauvegarde', 'montante', '{}', '{}', false, NULL),
    ('alerte', 'montante', '{}', '{}', false, NULL),
    ('alerte_surveillance', 'montante', '{}', '{}', false, NULL),
    -- Achats et réceptions
    ('fournisseur', 'montante', '{}', '{}', false, NULL),
    ('fournisseur_mouvement', 'montante', '{}', '{}', false, NULL),
    ('reception', 'montante', '{}', '{}', false, NULL),
    ('reception_ligne', 'montante', '{}', '{}', false, NULL),
    ('comptage_reception', 'montante', '{}', '{}', false, NULL),
    ('comptage_reception_ligne', 'montante', '{}', '{}', false, NULL),
    ('retour_fournisseur', 'montante', '{}', '{}', false, NULL),
    ('ligne_retour_fournisseur', 'montante', '{}', '{}', false, NULL),
    ('reglement_fournisseur', 'montante', '{}', '{}', false, NULL),
    ('reglement_facture', 'montante', '{}', '{}', false, NULL),
    ('rupture_grossiste', 'montante', '{}', '{}', false, NULL),
    ('catalogue_grossiste', 'montante', '{}', '{}', false, NULL),
    -- Chifa
    ('bordereau', 'montante', '{}', '{}', false, NULL),
    ('bordereau_vente', 'montante', '{}', '{}', false, NULL),
    ('versement_bordereau', 'montante', '{}', '{}', false, NULL),
    -- Patients, crédits, factures (sous code)
    ('patient', 'montante', '{nom,prenom,date_naissance,telephone,num_assure,email}', '{id,assure_principal_id}', false,
        'Pseudonyme : le code remplace l''identifiant'),
    ('credit_ouverture', 'montante', '{}', '{patient_id}', false, NULL),
    ('credit_mouvement', 'montante', '{}', '{patient_id}', false, NULL),
    ('credit_imputation', 'montante', '{}', '{}', false, NULL),
    ('demande_patient', 'montante', '{sms_erreur}', '{patient_id}', false, NULL),
    ('client_facture', 'montante', '{telephone,email}', '{}', false, NULL),
    ('facture_client', 'montante', '{destinataire_nom,destinataire_infos}', '{patient_id}', false, NULL),
    ('facture_client_ligne', 'montante', '{}', '{}', false, NULL),
    ('facture_client_version', 'montante', '{contenu}', '{}', false, 'Contenu figé retiré (nom du destinataire)'),
    ('reglement_facture_client', 'montante', '{}', '{}', false, NULL),
    -- Trésorerie et financements
    ('compte_tresorerie', 'montante', '{}', '{}', false, NULL),
    ('mouvement_tresorerie', 'montante', '{}', '{}', false, NULL),
    ('compte_carte_periode', 'montante', '{}', '{}', false, NULL),
    ('pret_bancaire', 'montante', '{}', '{}', false, NULL),
    ('echeance_pret', 'montante', '{}', '{}', false, NULL),
    ('paiement_echeance_pret', 'montante', '{}', '{}', false, NULL),
    ('paiement_effet', 'montante', '{}', '{}', false, NULL),
    ('correspondance_echeancier', 'montante', '{}', '{}', false, NULL),
    ('correspondance_echeancier_preteur', 'montante', '{}', '{}', false, NULL),
    ('utilisation_financement', 'montante', '{}', '{}', false, NULL),
    ('remboursement_titulaire', 'montante', '{}', '{}', false, NULL),
    ('pharmacie', 'montante', '{}', '{}', false, NULL),
    -- Bidirectionnelles : ordre descendant appliqué par l'API locale + état montant
    ('produit', 'bidirectionnelle', '{}', '{}', false, 'Prix et marges'),
    ('remise_automatique', 'bidirectionnelle', '{}', '{}', false, NULL),
    ('substitution_liste_blanche', 'bidirectionnelle', '{}', '{}', false, NULL),
    ('commande_fournisseur', 'bidirectionnelle', '{}', '{}', false, 'Commande en brouillon préparée à distance'),
    ('ligne_commande', 'bidirectionnelle', '{}', '{}', false, NULL),
    ('utilisateur', 'bidirectionnelle', '{mot_de_passe_hash,pin_hash,code_comptoir_essais,code_comptoir_bloque_jusqu,telephone,email,jetons_revoques_le}', '{}', false,
        'Jamais de mot de passe ni de code comptoir'),
    ('role', 'bidirectionnelle', '{}', '{}', false, NULL),
    ('role_permission', 'bidirectionnelle', '{}', '{}', false, NULL),
    ('motif_reglable', 'bidirectionnelle', '{}', '{}', false, NULL),
    ('regle_alerte_psychotrope', 'bidirectionnelle', '{}', '{}', false, NULL),
    ('parametre_alerte', 'bidirectionnelle', '{}', '{}', false, NULL),
    ('parametre_caisse', 'bidirectionnelle', '{}', '{}', false, NULL),
    ('parametre_fiscal', 'bidirectionnelle', '{}', '{}', false, NULL),
    ('parametre_formulaire_sms', 'bidirectionnelle', '{}', '{}', false, NULL),
    ('parametre_inventaire', 'bidirectionnelle', '{}', '{}', false, NULL),
    ('parametre_notification', 'bidirectionnelle', '{smtp_identifiant,smtp_mot_de_passe}', '{}', false, 'Jamais le compte de messagerie'),
    ('parametre_rapport', 'bidirectionnelle', '{}', '{}', false, NULL),
    ('parametre_sauvegarde', 'bidirectionnelle', '{}', '{}', false, NULL),
    ('parametre_serialisation', 'bidirectionnelle', '{}', '{}', false, NULL),
    ('parametre_substitution', 'bidirectionnelle', '{}', '{}', false, NULL),
    ('parametre_surveillance', 'bidirectionnelle', '{}', '{}', false, NULL),
    ('parametre_tracabilite', 'bidirectionnelle', '{}', '{}', false, NULL),
    -- Descendantes : référentiels diffusés par le serveur central (lot S5)
    ('nomenclature_produit', 'descendante', '{}', '{}', false, NULL),
    ('nomenclature_version', 'descendante', '{}', '{}', false, NULL),
    ('dci', 'descendante', '{}', '{}', false, NULL),
    ('laboratoire', 'descendante', '{}', '{}', false, NULL),
    ('categorie_produit', 'descendante', '{}', '{}', false, NULL),
    ('indication', 'descendante', '{}', '{}', false, NULL),
    ('produit_indication', 'descendante', '{}', '{}', false, NULL),
    ('classe_suivie', 'descendante', '{}', '{}', false, NULL),
    ('classe_suivie_dci', 'descendante', '{}', '{}', false, NULL),
    ('substance_surveillee', 'descendante', '{}', '{}', false, NULL),
    ('zone_psychotrope_wilaya', 'descendante', '{}', '{}', false, NULL),
    ('wilaya', 'descendante', '{}', '{}', false, NULL),
    ('organisme', 'descendante', '{}', '{}', false, NULL),
    ('centre_payeur', 'descendante', '{}', '{}', false, NULL),
    ('type_prestation', 'descendante', '{}', '{}', false, NULL),
    ('chifa_code_produit', 'descendante', '{}', '{}', false, NULL),
    ('parametre_gs1_ai', 'descendante', '{}', '{}', false, NULL),
    -- Jamais envoyées
    ('ordonnance', 'jamais', '{}', '{}', false, 'Ordonnances et images scannées restent dans l''officine (décision 2)'),
    ('medecin', 'jamais', '{}', '{}', false, 'Coordonnées des prescripteurs : aucun usage central identifié'),
    ('historique_chifa', 'jamais', '{}', '{}', false, 'Reprise : nom et n° d''assuré en clair'),
    ('destinataire_notification', 'jamais', '{}', '{}', false, 'Coordonnées personnelles'),
    ('message_sortant', 'jamais', '{}', '{}', false, 'Contenu et adresse des SMS et courriels'),
    ('alerte_non_envoyee', 'jamais', '{}', '{}', false, 'Technique, local'),
    ('panier_attente', 'jamais', '{}', '{}', false, 'Transitoire'),
    ('reception_attente', 'jamais', '{}', '{}', false, 'Transitoire'),
    ('scan_reception', 'jamais', '{}', '{}', false, 'Transitoire'),
    ('import_catalogue_grossiste', 'jamais', '{}', '{}', false, 'Journal d''import local'),
    ('profil_catalogue_grossiste', 'jamais', '{}', '{}', false, 'Réglage d''import local'),
    ('compteur_ticket', 'jamais', '{}', '{}', false, 'Compteur local'),
    ('facture_compteur', 'jamais', '{}', '{}', false, 'Compteur local'),
    ('permission', 'jamais', '{}', '{}', false, 'Livrée par les scripts du logiciel'),
    ('chaine_integrite', 'jamais', '{}', '{}', false, 'Registre technique local'),
    ('parametre_integrite', 'jamais', '{}', '{}', false, 'Technique, local'),
    ('integrite_essai', 'jamais', '{}', '{}', false, 'Table d''essai interne'),
    ('serialisation_reponse_factice', 'jamais', '{}', '{}', false, 'Essai'),
    ('serveur_officine', 'jamais', '{}', '{}', false, 'Réglages propres à chaque serveur'),
    ('surveillance_replication', 'jamais', '{}', '{}', false, 'Réplication locale'),
    ('sync_parametre', 'jamais', '{}', '{}', false, 'Synchronisation elle-même'),
    ('sync_table', 'jamais', '{}', '{}', false, 'Synchronisation elle-même'),
    ('sync_pseudonyme', 'jamais', '{}', '{}', false, 'Correspondance personne <-> code : ne quitte jamais l''officine'),
    ('sync_sortie', 'jamais', '{}', '{}', false, 'Synchronisation elle-même'),
    ('sync_lot', 'jamais', '{}', '{}', false, 'Synchronisation elle-même')
ON CONFLICT (nom_table) DO NOTHING;

-- Clé stable = clé primaire de la table (constatée dans le catalogue)
UPDATE sync_table s SET cle_colonnes = k.colonnes
FROM (SELECT c.relname AS nom, array_agg(a.attname::text ORDER BY x.n) AS colonnes
      FROM pg_constraint p
      JOIN pg_class c ON c.oid = p.conrelid
      JOIN pg_namespace ns ON ns.oid = c.relnamespace AND ns.nspname = 'public'
      CROSS JOIN LATERAL unnest(p.conkey) WITH ORDINALITY AS x(attnum, n)
      JOIN pg_attribute a ON a.attrelid = c.oid AND a.attnum = x.attnum
      WHERE p.contype = 'p'
      GROUP BY c.relname) k
WHERE k.nom = s.nom_table AND s.cle_colonnes IS DISTINCT FROM k.colonnes;

-- ---------------------------------------------------------------------
-- Fonctions
-- ---------------------------------------------------------------------
CREATE OR REPLACE FUNCTION sync_tables_non_classees() RETURNS SETOF text
LANGUAGE sql STABLE AS $$
    SELECT t.tablename::text FROM pg_tables t
    WHERE t.schemaname = 'public' AND NOT EXISTS (SELECT 1 FROM sync_table s WHERE s.nom_table = t.tablename)
    ORDER BY 1
$$;

-- Code stable d'un patient (créé au premier besoin, jamais réutilisé)
CREATE OR REPLACE FUNCTION sync_code_patient(p_id bigint) RETURNS text
LANGUAGE plpgsql AS $$
DECLARE v_code text;
BEGIN
    IF p_id IS NULL THEN RETURN NULL; END IF;
    SELECT code INTO v_code FROM sync_pseudonyme WHERE nature = 'patient' AND cle_locale = p_id::text;
    IF FOUND THEN RETURN v_code; END IF;
    INSERT INTO sync_pseudonyme (nature, cle_locale, code)
    VALUES ('patient', p_id::text, 'P-' || encode(gen_random_bytes(12), 'hex'))
    ON CONFLICT (nature, cle_locale) DO NOTHING;
    SELECT code INTO v_code FROM sync_pseudonyme WHERE nature = 'patient' AND cle_locale = p_id::text;
    RETURN v_code;
END $$;

-- Code stable d'un porteur : clé locale = empreinte SHA-256 du nom et de la pièce normalisés (le nom n'est pas recopié)
CREATE OR REPLACE FUNCTION sync_code_porteur(p_nom text, p_piece text) RETURNS text
LANGUAGE plpgsql AS $$
DECLARE v_cle text; v_code text;
BEGIN
    IF coalesce(btrim(p_nom), '') = '' AND coalesce(btrim(p_piece), '') = '' THEN RETURN NULL; END IF;
    v_cle := encode(sha256(convert_to(lower(btrim(coalesce(p_nom, ''))) || '|' || lower(btrim(coalesce(p_piece, ''))), 'UTF8')), 'hex');
    SELECT code INTO v_code FROM sync_pseudonyme WHERE nature = 'porteur' AND cle_locale = v_cle;
    IF FOUND THEN RETURN v_code; END IF;
    INSERT INTO sync_pseudonyme (nature, cle_locale, code)
    VALUES ('porteur', v_cle, 'R-' || encode(gen_random_bytes(12), 'hex'))
    ON CONFLICT (nature, cle_locale) DO NOTHING;
    SELECT code INTO v_code FROM sync_pseudonyme WHERE nature = 'porteur' AND cle_locale = v_cle;
    RETURN v_code;
END $$;

-- Ligne minimisée : porteur sous code, colonnes exclues retirées, patients sous code
CREATE OR REPLACE FUNCTION sync_minimiser(p_table sync_table, p_ligne jsonb) RETURNS jsonb
LANGUAGE plpgsql AS $$
DECLARE j jsonb := p_ligne; c text;
BEGIN
    IF p_table.porteur THEN
        j := j || jsonb_build_object('porteur_code', sync_code_porteur(j ->> 'porteur_nom', j ->> 'porteur_piece'));
        j := j - ARRAY['porteur_nom', 'porteur_adresse', 'porteur_piece'];
    END IF;
    j := j - p_table.colonnes_exclues;
    FOREACH c IN ARRAY p_table.colonnes_patient LOOP
        IF jsonb_typeof(j -> c) = 'number' THEN
            j := jsonb_set(j, ARRAY[c], to_jsonb(sync_code_patient((j ->> c)::bigint)));
        END IF;
    END LOOP;
    RETURN j;
END $$;

-- Déclencheur des tables montantes et bidirectionnelles : une ligne de file par modification, même transaction
CREATE OR REPLACE FUNCTION sync_inscrire() RETURNS trigger
LANGUAGE plpgsql AS $$
DECLARE
    t       sync_table;
    p       sync_parametre;
    j       jsonb;
    v_id    text;
BEGIN
    SELECT * INTO p FROM sync_parametre WHERE id = 1;
    IF NOT FOUND OR NOT p.active THEN RETURN NULL; END IF;
    SELECT * INTO t FROM sync_table WHERE nom_table = TG_TABLE_NAME;
    IF NOT FOUND OR t.sens NOT IN ('montante', 'bidirectionnelle') THEN RETURN NULL; END IF;
    j := sync_minimiser(t, CASE WHEN TG_OP = 'DELETE' THEN to_jsonb(OLD) ELSE to_jsonb(NEW) END);
    SELECT string_agg(coalesce(j ->> k.col, ''), '|' ORDER BY k.n) INTO v_id
    FROM unnest(t.cle_colonnes) WITH ORDINALITY AS k(col, n);
    INSERT INTO sync_sortie (nom_table, id_local, operation, donnees, version_contrat, epoque)
    VALUES (TG_TABLE_NAME, v_id,
            CASE TG_OP WHEN 'INSERT' THEN 'creation' WHEN 'UPDATE' THEN 'modification' ELSE 'suppression' END,
            CASE WHEN TG_OP = 'DELETE' THEN NULL ELSE j END,
            p.version_contrat, p.epoque);
    RETURN NULL;
END $$;

-- Pose (synchro active) ou retire (désactivée) les déclencheurs ; renvoie le nombre de tables suivies
CREATE OR REPLACE FUNCTION sync_installer_declencheurs() RETURNS integer
LANGUAGE plpgsql AS $$
DECLARE r record; n int := 0; v_active boolean;
BEGIN
    SELECT active INTO v_active FROM sync_parametre WHERE id = 1;
    FOR r IN SELECT c.relname FROM pg_trigger tg
             JOIN pg_class c ON c.oid = tg.tgrelid
             JOIN pg_namespace ns ON ns.oid = c.relnamespace AND ns.nspname = 'public'
             WHERE tg.tgname = 'zz_sync_sortie' LOOP
        EXECUTE format('DROP TRIGGER zz_sync_sortie ON public.%I', r.relname);
    END LOOP;
    IF coalesce(v_active, false) THEN
        FOR r IN SELECT s.nom_table FROM sync_table s
                 JOIN pg_tables t ON t.schemaname = 'public' AND t.tablename = s.nom_table
                 WHERE s.sens IN ('montante', 'bidirectionnelle') ORDER BY 1 LOOP
            EXECUTE format('CREATE TRIGGER zz_sync_sortie AFTER INSERT OR UPDATE OR DELETE ON public.%I '
                           'FOR EACH ROW EXECUTE FUNCTION sync_inscrire()', r.nom_table);
            n := n + 1;
        END LOOP;
    END IF;
    RETURN n;
END $$;

-- Activer / désactiver : refuse l'activation tant qu'une table n'est pas classée
CREATE OR REPLACE FUNCTION sync_definir_active(p_active boolean) RETURNS integer
LANGUAGE plpgsql AS $$
DECLARE v_oubliees text;
BEGIN
    IF p_active THEN
        SELECT string_agg(x, ', ') INTO v_oubliees FROM sync_tables_non_classees() AS x;
        IF v_oubliees IS NOT NULL THEN
            RAISE EXCEPTION 'Synchronisation : tables non classées (sync_table) : %', v_oubliees;
        END IF;
    END IF;
    UPDATE sync_parametre SET active = p_active, modifie_le = now() WHERE id = 1;
    RETURN sync_installer_declencheurs();
END $$;

-- File en ajout seul : seul le rattachement à un lot (NULL -> n°) est permis ; suppression après accusé seulement
CREATE OR REPLACE FUNCTION sync_sortie_garde() RETURNS trigger
LANGUAGE plpgsql AS $$
BEGIN
    IF TG_OP = 'UPDATE' THEN
        IF OLD.lot_numero IS NULL AND NEW.lot_numero IS NOT NULL
           AND (to_jsonb(NEW) - 'lot_numero') = (to_jsonb(OLD) - 'lot_numero') THEN
            RETURN NEW;
        END IF;
        RAISE EXCEPTION 'sync_sortie : ajout seul (seul le rattachement à un lot est permis)';
    ELSIF TG_OP = 'DELETE' THEN
        IF EXISTS (SELECT 1 FROM sync_lot l WHERE l.epoque = OLD.epoque AND l.numero = OLD.lot_numero AND l.accuse_le IS NOT NULL) THEN
            RETURN OLD;
        END IF;
        RAISE EXCEPTION 'sync_sortie : suppression permise seulement après accusé du lot';
    END IF;
    RAISE EXCEPTION 'sync_sortie : vidage interdit';
END $$;

DROP TRIGGER IF EXISTS trg_sync_sortie_garde ON sync_sortie;
CREATE TRIGGER trg_sync_sortie_garde BEFORE UPDATE OR DELETE ON sync_sortie FOR EACH ROW EXECUTE FUNCTION sync_sortie_garde();
DROP TRIGGER IF EXISTS trg_sync_sortie_non_vidable ON sync_sortie;
CREATE TRIGGER trg_sync_sortie_non_vidable BEFORE TRUNCATE ON sync_sortie FOR EACH STATEMENT EXECUTE FUNCTION sync_sortie_garde();

-- Lots : seul l'accusé (NULL -> date) se modifie ; jamais supprimés en S1
CREATE OR REPLACE FUNCTION sync_lot_garde() RETURNS trigger
LANGUAGE plpgsql AS $$
BEGIN
    IF TG_OP = 'UPDATE' AND OLD.accuse_le IS NULL AND NEW.accuse_le IS NOT NULL
       AND (to_jsonb(NEW) - 'accuse_le') = (to_jsonb(OLD) - 'accuse_le') THEN
        RETURN NEW;
    END IF;
    RAISE EXCEPTION 'sync_lot : lot figé (seul l''accusé peut être enregistré)';
END $$;

DROP TRIGGER IF EXISTS trg_sync_lot_garde ON sync_lot;
CREATE TRIGGER trg_sync_lot_garde BEFORE UPDATE OR DELETE ON sync_lot FOR EACH ROW EXECUTE FUNCTION sync_lot_garde();
DROP TRIGGER IF EXISTS trg_sync_lot_non_vidable ON sync_lot;
CREATE TRIGGER trg_sync_lot_non_vidable BEFORE TRUNCATE ON sync_lot FOR EACH STATEMENT EXECUTE FUNCTION sync_lot_garde();

-- Constitution d'un lot (appelée par l'application, une transaction) : n° = dernier + 1 sous verrou de la ligne de
-- réglage, lignes rattachées toutes ou aucune. Renvoie le n° du lot, ou NULL si le n° attendu n'est plus le suivant
-- (constitution simultanée) ou si une ligne est déjà rattachée.
-- Rejouable : sur une base où le script 130 est passé (version à 5 arguments présente), l'ancienne signature est déjà la
-- version finale (appel de la version à 5 arguments) ; la remplacer par cette version ramènerait une définition périmée.
DO $garde$
BEGIN
    IF to_regprocedure('sync_enregistrer_lot(bigint,bigint[],integer,text,text[])') IS NULL THEN
        EXECUTE $f$
CREATE OR REPLACE FUNCTION sync_enregistrer_lot(p_numero bigint, p_lignes bigint[], p_octets int, p_empreinte text)
RETURNS bigint LANGUAGE plpgsql AS $$
DECLARE p sync_parametre; n int;
BEGIN
    SELECT * INTO p FROM sync_parametre WHERE id = 1 FOR UPDATE;
    IF p.dernier_lot + 1 <> p_numero OR coalesce(array_length(p_lignes, 1), 0) = 0 THEN RETURN NULL; END IF;
    INSERT INTO sync_lot (epoque, numero, version_contrat, nb_lignes, octets, empreinte)
    VALUES (p.epoque, p_numero, p.version_contrat, array_length(p_lignes, 1), p_octets, p_empreinte);
    UPDATE sync_sortie SET lot_numero = p_numero
    WHERE id = ANY (p_lignes) AND lot_numero IS NULL AND epoque = p.epoque;
    GET DIAGNOSTICS n = ROW_COUNT;
    IF n <> array_length(p_lignes, 1) THEN
        RAISE EXCEPTION 'Synchronisation : lot % incohérent (% lignes rattachées sur %)', p_numero, n, array_length(p_lignes, 1);
    END IF;
    UPDATE sync_parametre SET dernier_lot = p_numero WHERE id = 1;
    RETURN p_numero;
END $$;
        $f$;
    END IF;
END $garde$;

-- Désactivée par défaut : la pose suit le réglage (aucun déclencheur tant que active = false)
SELECT sync_installer_declencheurs();

COMMENT ON TABLE sync_parametre IS 'Synchronisation : réglage (désactivée par défaut), officine, époque, contrat, bornes des lots (script 126).';
COMMENT ON TABLE sync_table IS 'Synchronisation : inventaire de chaque table du schéma public (sens, clé, colonnes exclues ou sous code) (script 126).';
COMMENT ON TABLE sync_pseudonyme IS 'Synchronisation : correspondance locale personne <-> code aléatoire stable ; jamais envoyée (script 126).';
COMMENT ON TABLE sync_sortie IS 'Synchronisation : file des modifications à envoyer (outbox), ajout seul tant que non acquittée (script 126).';
COMMENT ON TABLE sync_lot IS 'Synchronisation : lots constitués, numérotés sans trou par époque à la constitution (script 126).';
