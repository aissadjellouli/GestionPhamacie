-- =====================================================================
-- Registre des psychotropes renforcé (2026-11-16, lot 8, « go lot 8 »)
--   1. CHAÎNAGE DU REGISTRE EXISTANT. La brique du lot 2 (111) refuse de
--      chaîner une table qui a déjà des lignes (integrite_activer), et
--      ajouter rang/hash à registre_psychotrope laisserait les lignes
--      existantes hors chaîne. Choix retenu (aucune ligne existante
--      modifiée ; la table registre_psychotrope n'est pas modifiée) :
--      JOURNAL CHAÎNÉ SÉPARÉ « journal_registre_psychotrope », créé vide
--      puis inscrit par integrite_activer, dont la PREMIÈRE ligne
--      (nature « ouverture », rang 1, datée) porte le SCEAU INITIAL des
--      lignes antérieures : leur nombre, le dernier id, et leur empreinte
--      globale
--        sha256( UTF-8( canonique(l1) || E'\n' || canonique(l2) … ) )
--      (lignes dans l'ordre des id ; format : registre_canonique).
--      Ensuite chaque ligne ajoutée au registre (déclencheur AFTER INSERT)
--      et chaque annulation d'écriture sont recopiées dans le journal
--      chaîné, dans la même transaction.
--      registre_concordance() vérifie que le registre et son journal
--      disent la même chose (lignes antérieures : même nombre et même
--      empreinte ; lignes recopiées identiques ; aucune ligne hors
--      journal ; annulations identiques). La vérification nocturne du
--      lot 2 l'appelle après integrite_verifier.
--   2. CORRECTIONS : annulation_registre (ajout seul). Une écriture n'est
--      jamais modifiée : elle est annulée avec un motif (le mouvement de
--      stock inverse passe par les opérations existantes : retour, écart
--      d'inventaire), et l'annulation est recopiée dans le journal chaîné.
--   3. ALERTES PARAMÉTRABLES : regle_alerte_psychotrope (seuils, périodes,
--      valideur exigé) [À VÉRIFIER : valeurs par défaut] ;
--      forcage_alerte_psychotrope (ajout seul) : employé, valideur par
--      code, motif ; permission psychotropes.forcer_alerte.
--   4. ANTIBIOTIQUES : classe_suivie, classe_suivie_dci (liste en
--      données) ; amorçage « antibiotiques » par motifs de DCI
--      [À VÉRIFIER : nomenclature], seulement si la classe est vide.
--   Aucune donnée existante supprimée ni modifiée. Idempotent.
--   À exécuter après 111.
-- =====================================================================

-- ---- 0. Ajout seul (tables de ce script) -----------------------------------
CREATE OR REPLACE FUNCTION ajout_seul_refuser() RETURNS trigger
LANGUAGE plpgsql AS $$
BEGIN
    RAISE EXCEPTION 'Table % en ajout seul : ni modification, ni suppression, ni vidage', TG_TABLE_NAME;
END;
$$;

-- ---- 1. Représentation canonique d'une ligne du registre (format registre-v1) ----
-- Champs nom=longueur:valeur (nom=~ si NULL, integrite_champ du script 111), séparés par « | », dans cet ordre ;
-- horodatage en UTC AAAA-MM-JJTHH:MI:SS.microsecondesZ
CREATE OR REPLACE FUNCTION registre_canonique(r registre_psychotrope) RETURNS text
LANGUAGE sql STABLE AS $$
    SELECT concat_ws('|', 'registre-v1',
        integrite_champ('id', r.id::text),
        integrite_champ('numero_ordre', r.numero_ordre::text),
        integrite_champ('lot_id', r.lot_id::text),
        integrite_champ('ligne_vente_id', r.ligne_vente_id::text),
        integrite_champ('mouvement_id', r.mouvement_id::text),
        integrite_champ('type_mouvement', r.type_mouvement),
        integrite_champ('quantite', r.quantite::text),
        integrite_champ('solde_apres', r.solde_apres::text),
        integrite_champ('patient_id', r.patient_id::text),
        integrite_champ('medecin_id', r.medecin_id::text),
        integrite_champ('ordonnance_id', r.ordonnance_id::text),
        integrite_champ('utilisateur_id', r.utilisateur_id::text),
        integrite_champ('reference', r.reference),
        integrite_champ('porteur_nom', r.porteur_nom),
        integrite_champ('porteur_adresse', r.porteur_adresse),
        integrite_champ('porteur_piece', r.porteur_piece),
        integrite_champ('cree_le', to_char(r.cree_le AT TIME ZONE 'UTC', 'YYYY-MM-DD"T"HH24:MI:SS.US"Z"')))
$$;

-- ---- 2. Journal chaîné ----------------------------------------------------------
CREATE TABLE IF NOT EXISTS journal_registre_psychotrope (
    id                    bigint      GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    nature                text        NOT NULL,
    registre_id           bigint      REFERENCES registre_psychotrope(id),
    numero_ordre          bigint,
    lot_id                bigint,
    ligne_vente_id        bigint,
    mouvement_id          bigint,
    type_mouvement        text,
    quantite              integer,
    solde_apres           integer,
    patient_id            bigint,
    medecin_id            bigint,
    ordonnance_id         bigint,
    utilisateur_id        bigint,
    reference             text,
    porteur_nom           text,
    porteur_adresse       text,
    porteur_piece         text,
    inscrit_le            timestamptz,
    lignes_anterieures    bigint,
    empreinte_anterieure  bytea,
    dernier_registre_id   bigint,
    annulation_id         bigint,
    motif                 text,
    cree_le               timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT ck_journal_registre_nature CHECK (nature IN ('ouverture', 'inscription', 'annulation')),
    CONSTRAINT ck_journal_registre_ouverture CHECK (nature <> 'ouverture' OR (lignes_anterieures IS NOT NULL AND empreinte_anterieure IS NOT NULL)),
    CONSTRAINT ck_journal_registre_ligne CHECK (nature = 'ouverture' OR registre_id IS NOT NULL)
);

SELECT integrite_activer('journal_registre_psychotrope', 'Registre des psychotropes',
    ARRAY['id', 'nature', 'registre_id', 'numero_ordre', 'lot_id', 'ligne_vente_id', 'mouvement_id', 'type_mouvement', 'quantite',
          'solde_apres', 'patient_id', 'medecin_id', 'ordonnance_id', 'utilisateur_id', 'reference', 'porteur_nom', 'porteur_adresse',
          'porteur_piece', 'inscrit_le', 'lignes_anterieures', 'empreinte_anterieure', 'dernier_registre_id', 'annulation_id', 'motif',
          'cree_le']);

CREATE UNIQUE INDEX IF NOT EXISTS ux_journal_registre_ouverture ON journal_registre_psychotrope (cle_chaine) WHERE nature = 'ouverture';
CREATE UNIQUE INDEX IF NOT EXISTS ux_journal_registre_inscription ON journal_registre_psychotrope (registre_id) WHERE nature = 'inscription';
CREATE UNIQUE INDEX IF NOT EXISTS ux_journal_registre_annulation ON journal_registre_psychotrope (annulation_id) WHERE nature = 'annulation';

-- Lignes du registre absentes du journal (antérieures à l'ouverture) : nombre, dernier id, empreinte globale
CREATE OR REPLACE FUNCTION registre_empreinte_anterieure()
RETURNS TABLE (lignes bigint, dernier_id bigint, empreinte bytea)
LANGUAGE sql STABLE AS $$
    SELECT count(*), max(r.id),
           sha256(convert_to(COALESCE(string_agg(registre_canonique(r), E'\n' ORDER BY r.id), ''), 'UTF8'))
    FROM registre_psychotrope r
    WHERE NOT EXISTS (SELECT 1 FROM journal_registre_psychotrope j WHERE j.nature = 'inscription' AND j.registre_id = r.id)
$$;

-- ---- 3. Annulation d'une écriture (ajout seul) ----------------------------------
CREATE TABLE IF NOT EXISTS annulation_registre (
    id              bigint      GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    registre_id     bigint      NOT NULL UNIQUE REFERENCES registre_psychotrope(id),
    motif           text        NOT NULL,
    utilisateur_id  bigint      NOT NULL REFERENCES utilisateur(id),
    cree_le         timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT ck_annulation_registre_motif CHECK (char_length(btrim(motif)) BETWEEN 5 AND 500)
);
CREATE OR REPLACE TRIGGER trg_annulation_registre_immuable
    BEFORE UPDATE OR DELETE ON annulation_registre FOR EACH ROW EXECUTE FUNCTION ajout_seul_refuser();
CREATE OR REPLACE TRIGGER trg_annulation_registre_non_vidable
    BEFORE TRUNCATE ON annulation_registre FOR EACH STATEMENT EXECUTE FUNCTION ajout_seul_refuser();

-- ---- 4. Recopie dans le journal chaîné ------------------------------------------
CREATE OR REPLACE FUNCTION registre_vers_journal() RETURNS trigger
LANGUAGE plpgsql AS $$
BEGIN
    INSERT INTO journal_registre_psychotrope (nature, registre_id, numero_ordre, lot_id, ligne_vente_id, mouvement_id, type_mouvement,
        quantite, solde_apres, patient_id, medecin_id, ordonnance_id, utilisateur_id, reference, porteur_nom, porteur_adresse,
        porteur_piece, inscrit_le)
    VALUES ('inscription', NEW.id, NEW.numero_ordre, NEW.lot_id, NEW.ligne_vente_id, NEW.mouvement_id, NEW.type_mouvement,
        NEW.quantite, NEW.solde_apres, NEW.patient_id, NEW.medecin_id, NEW.ordonnance_id, NEW.utilisateur_id, NEW.reference,
        NEW.porteur_nom, NEW.porteur_adresse, NEW.porteur_piece, NEW.cree_le);
    RETURN NULL;
END;
$$;

CREATE OR REPLACE FUNCTION annulation_registre_vers_journal() RETURNS trigger
LANGUAGE plpgsql AS $$
BEGIN
    INSERT INTO journal_registre_psychotrope (nature, registre_id, annulation_id, motif, utilisateur_id, inscrit_le)
    VALUES ('annulation', NEW.registre_id, NEW.id, NEW.motif, NEW.utilisateur_id, NEW.cree_le);
    RETURN NULL;
END;
$$;

-- Ligne d'ouverture (sceau initial des lignes antérieures) puis recopie : dans la même transaction, registre verrouillé
-- en écriture (aucune ligne ne peut s'intercaler entre l'empreinte et le déclencheur)
DO $$
DECLARE
    v record;
BEGIN
    LOCK TABLE registre_psychotrope IN SHARE ROW EXCLUSIVE MODE;
    IF NOT EXISTS (SELECT 1 FROM journal_registre_psychotrope WHERE nature = 'ouverture') THEN
        SELECT * INTO v FROM registre_empreinte_anterieure();
        INSERT INTO journal_registre_psychotrope (nature, lignes_anterieures, dernier_registre_id, empreinte_anterieure, inscrit_le, motif)
        VALUES ('ouverture', v.lignes, v.dernier_id, v.empreinte, now(),
                'Ouverture du chaînage : ' || v.lignes || ' ligne(s) antérieure(s) scellée(s) par leur empreinte globale');
    END IF;
    CREATE OR REPLACE TRIGGER zz_registre_journal_chaine
        AFTER INSERT ON registre_psychotrope FOR EACH ROW EXECUTE FUNCTION registre_vers_journal();
    CREATE OR REPLACE TRIGGER zz_annulation_registre_journal
        AFTER INSERT ON annulation_registre FOR EACH ROW EXECUTE FUNCTION annulation_registre_vers_journal();
END;
$$;

-- ---- 5. Concordance registre / journal chaîné -----------------------------------
-- Une ligne : nombre de lignes du registre, et la première discordance (motif vide si tout concorde)
CREATE OR REPLACE FUNCTION registre_concordance()
RETURNS TABLE (lignes bigint, motif text, registre_id bigint, attendu text, trouve text)
LANGUAGE plpgsql STABLE AS $$
#variable_conflict use_variable
DECLARE
    v_ouv  journal_registre_psychotrope;
    v_ant  record;
    v_id   bigint;
BEGIN
    SELECT count(*) INTO lignes FROM registre_psychotrope;
    motif := NULL; registre_id := NULL; attendu := NULL; trouve := NULL;

    SELECT * INTO v_ouv FROM journal_registre_psychotrope j WHERE j.nature = 'ouverture' ORDER BY j.rang LIMIT 1;
    IF NOT FOUND THEN
        motif := 'ligne d''ouverture du journal absente'; RETURN NEXT; RETURN;
    END IF;

    -- Lignes antérieures (absentes du journal) : même nombre et même empreinte qu'à l'ouverture
    SELECT * INTO v_ant FROM registre_empreinte_anterieure();
    IF v_ant.lignes <> v_ouv.lignes_anterieures OR v_ant.empreinte <> v_ouv.empreinte_anterieure THEN
        motif := 'lignes antérieures à l''ouverture modifiées, supprimées, ou ligne ajoutée hors journal';
        attendu := v_ouv.lignes_anterieures || ' ligne(s), ' || encode(v_ouv.empreinte_anterieure, 'hex');
        trouve := v_ant.lignes || ' ligne(s), ' || encode(v_ant.empreinte, 'hex');
        RETURN NEXT; RETURN;
    END IF;

    -- Ligne recopiée disparue du registre
    SELECT j.registre_id INTO v_id FROM journal_registre_psychotrope j
    WHERE j.nature = 'inscription' AND NOT EXISTS (SELECT 1 FROM registre_psychotrope r WHERE r.id = j.registre_id)
    ORDER BY j.rang LIMIT 1;
    IF FOUND THEN
        motif := 'ligne du registre supprimée'; registre_id := v_id; RETURN NEXT; RETURN;
    END IF;

    -- Ligne du registre différente de sa copie chaînée
    SELECT r.id INTO v_id FROM registre_psychotrope r JOIN journal_registre_psychotrope j ON j.nature = 'inscription' AND j.registre_id = r.id
    WHERE (r.numero_ordre, r.lot_id, r.ligne_vente_id, r.mouvement_id, r.type_mouvement, r.quantite, r.solde_apres, r.patient_id,
           r.medecin_id, r.ordonnance_id, r.utilisateur_id, r.reference, r.porteur_nom, r.porteur_adresse, r.porteur_piece, r.cree_le)
          IS DISTINCT FROM
          (j.numero_ordre, j.lot_id, j.ligne_vente_id, j.mouvement_id, j.type_mouvement, j.quantite, j.solde_apres, j.patient_id,
           j.medecin_id, j.ordonnance_id, j.utilisateur_id, j.reference, j.porteur_nom, j.porteur_adresse, j.porteur_piece, j.inscrit_le)
    ORDER BY r.id LIMIT 1;
    IF FOUND THEN
        motif := 'ligne du registre modifiée (différente de sa copie chaînée)'; registre_id := v_id; RETURN NEXT; RETURN;
    END IF;

    -- Annulations : chacune recopiée à l'identique, aucune disparue
    SELECT a.registre_id INTO v_id FROM annulation_registre a
    LEFT JOIN journal_registre_psychotrope j ON j.nature = 'annulation' AND j.annulation_id = a.id
    WHERE j.id IS NULL OR (a.registre_id, a.motif, a.utilisateur_id, a.cree_le) IS DISTINCT FROM (j.registre_id, j.motif, j.utilisateur_id, j.inscrit_le)
    ORDER BY a.id LIMIT 1;
    IF FOUND THEN
        motif := 'annulation d''écriture modifiée ou hors journal'; registre_id := v_id; RETURN NEXT; RETURN;
    END IF;
    SELECT j.registre_id INTO v_id FROM journal_registre_psychotrope j
    WHERE j.nature = 'annulation' AND NOT EXISTS (SELECT 1 FROM annulation_registre a WHERE a.id = j.annulation_id)
    ORDER BY j.rang LIMIT 1;
    IF FOUND THEN
        motif := 'annulation d''écriture supprimée'; registre_id := v_id; RETURN NEXT; RETURN;
    END IF;

    RETURN NEXT;
END;
$$;

-- ---- 6. Alertes paramétrables [À VÉRIFIER : valeurs par défaut] ----------------
--   patient_repete          : alerte si le patient a déjà reçu au moins « seuil » délivrances sous registre sur « periode_jours »
--   ordonnance_reutilisee   : alerte si le même n° d'ordonnance du même prescripteur a déjà servi « seuil » fois sur la période
--   quantite_anormale       : alerte au-delà de « seuil » boîtes d'un même produit par délivrance ; et si la durée de traitement est
--                             connue, au-delà de « periode_jours » jours de traitement
--   prescripteur_inhabituel : alerte si le prescripteur a moins de « seuil » délivrances sous registre à l'officine sur la période
--                             (la zone d'exercice du médecin n'est pas connue : pas de contrôle « hors zone »)
CREATE TABLE IF NOT EXISTS regle_alerte_psychotrope (
    code             text        PRIMARY KEY,
    libelle          text        NOT NULL,
    active           boolean     NOT NULL DEFAULT true,
    seuil            integer     NOT NULL,
    periode_jours    integer     NOT NULL,
    exiger_valideur  boolean     NOT NULL DEFAULT false,
    ordre            smallint    NOT NULL DEFAULT 0,
    modifie_le       timestamptz,
    modifie_par      bigint      REFERENCES utilisateur(id),
    CONSTRAINT ck_regle_alerte_psy_code CHECK (code IN ('patient_repete', 'ordonnance_reutilisee', 'quantite_anormale', 'prescripteur_inhabituel')),
    CONSTRAINT ck_regle_alerte_psy_seuil CHECK (seuil BETWEEN 1 AND 1000),
    CONSTRAINT ck_regle_alerte_psy_periode CHECK (periode_jours BETWEEN 1 AND 3650)
);
INSERT INTO regle_alerte_psychotrope (code, libelle, seuil, periode_jours, ordre) VALUES
    ('patient_repete', 'Même patient servi plusieurs fois', 2, 30, 1),
    ('ordonnance_reutilisee', 'Même n° d''ordonnance et même prescripteur déjà utilisés', 1, 365, 2),
    ('quantite_anormale', 'Quantité anormale (boîtes par délivrance, durée de traitement)', 3, 28, 3),
    ('prescripteur_inhabituel', 'Prescripteur inhabituel', 1, 365, 4)
ON CONFLICT (code) DO NOTHING;

CREATE TABLE IF NOT EXISTS forcage_alerte_psychotrope (
    id                 bigint      GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    cree_le            timestamptz NOT NULL DEFAULT now(),
    utilisateur_id     bigint      NOT NULL REFERENCES utilisateur(id),
    valide_par         bigint      REFERENCES utilisateur(id),
    codes              text[]      NOT NULL,
    messages           text        NOT NULL,
    motif              text        NOT NULL,
    patient_id         bigint      REFERENCES patient(id),
    medecin_id         bigint      REFERENCES medecin(id),
    numero_ordonnance  text,
    produits           bigint[]    NOT NULL,
    poste              text,
    CONSTRAINT ck_forcage_psy_motif CHECK (char_length(btrim(motif)) BETWEEN 5 AND 500),
    CONSTRAINT ck_forcage_psy_codes CHECK (cardinality(codes) > 0)
);
CREATE INDEX IF NOT EXISTS idx_forcage_psy_date ON forcage_alerte_psychotrope (cree_le DESC);
CREATE INDEX IF NOT EXISTS idx_forcage_psy_utilisateur ON forcage_alerte_psychotrope (utilisateur_id, cree_le DESC);
CREATE OR REPLACE TRIGGER trg_forcage_psy_immuable
    BEFORE UPDATE OR DELETE ON forcage_alerte_psychotrope FOR EACH ROW EXECUTE FUNCTION ajout_seul_refuser();
CREATE OR REPLACE TRIGGER trg_forcage_psy_non_vidable
    BEFORE TRUNCATE ON forcage_alerte_psychotrope FOR EACH STATEMENT EXECUTE FUNCTION ajout_seul_refuser();

-- Index utiles aux contrôles (patient, ordonnance, prescripteur)
CREATE INDEX IF NOT EXISTS idx_registre_patient ON registre_psychotrope (patient_id, cree_le) WHERE patient_id IS NOT NULL;
CREATE INDEX IF NOT EXISTS idx_registre_medecin ON registre_psychotrope (medecin_id, cree_le) WHERE medecin_id IS NOT NULL;

-- Permission : forcer une alerte psychotrope dont la règle exige un valideur (titulaire et assistant ; un vendeur la fait
-- accorder par le code comptoir d'un ayant droit)
DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM permission WHERE code = 'psychotropes.forcer_alerte') THEN
        INSERT INTO permission (code, module, libelle, sensible)
        VALUES ('psychotropes.forcer_alerte', 'Psychotropes', 'Forcer une alerte du registre (valideur)', true)
        ON CONFLICT DO NOTHING;
    END IF;
    IF NOT EXISTS (SELECT 1 FROM role_permission WHERE permission_code = 'psychotropes.forcer_alerte') THEN
        ALTER TABLE role_permission DISABLE TRIGGER trg_proteger_role_systeme;
        INSERT INTO role_permission (role_id, permission_code)
        SELECT r.id, 'psychotropes.forcer_alerte' FROM role r WHERE r.code IN ('titulaire', 'assistant')
        ON CONFLICT DO NOTHING;
        ALTER TABLE role_permission ENABLE TRIGGER trg_proteger_role_systeme;
    END IF;
END $$;

-- ---- 7. Classes suivies (antibiotiques) [À VÉRIFIER : nomenclature] ------------
CREATE TABLE IF NOT EXISTS classe_suivie (
    code     text    PRIMARY KEY,
    libelle  text    NOT NULL,
    active   boolean NOT NULL DEFAULT true,
    CONSTRAINT ck_classe_suivie_code CHECK (code ~ '^[a-z0-9_]{2,40}$')
);
CREATE TABLE IF NOT EXISTS classe_suivie_dci (
    classe_code  text        NOT NULL REFERENCES classe_suivie(code),
    dci_id       bigint      NOT NULL REFERENCES dci(id),
    ajoute_le    timestamptz NOT NULL DEFAULT now(),
    ajoute_par   bigint      REFERENCES utilisateur(id),
    PRIMARY KEY (classe_code, dci_id)
);
INSERT INTO classe_suivie (code, libelle) VALUES ('antibiotiques', 'Antibiotiques') ON CONFLICT (code) DO NOTHING;
-- Amorçage par motifs de DCI, seulement si la classe est vide (n'annule jamais un choix du titulaire)
INSERT INTO classe_suivie_dci (classe_code, dci_id)
SELECT 'antibiotiques', d.id FROM dci d
WHERE NOT EXISTS (SELECT 1 FROM classe_suivie_dci c WHERE c.classe_code = 'antibiotiques')
  AND lower(d.nom) ~ '(cillin|cef|ceph|floxacin|thromycin|spiramycin|josamycin|pristinamycin|fosfomycin|lincomycin|clindamycin|vancomycin|streptomycin|gentamicin|amikacin|tobramycin|cycline|metronidazol|ornidazol|tinidazol|secnidazol|sulfamethoxazol|trimethoprim|nitrofuran|rifampicin|colistin|penem|aztreonam)'
ON CONFLICT DO NOTHING;
