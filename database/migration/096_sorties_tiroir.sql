-- =====================================================================
-- Sorties et entrées d'argent par le tiroir (2026-11-15, règle du titulaire)
--   Toute sortie ou remise d'argent par le tiroir (F8) est tracée et impacte
--   l'attendu de la caisse (relève, clôture) et la trésorerie.
--   * motif_reglable : effet (aucun, sortie, entree) et détail exigé
--     (aucun, depense, fournisseur, patient, coffre, fond) ; les motifs
--     ajoutés par le titulaire peuvent aussi avoir un effet.
--   * nouveaux motifs du tiroir (insérés s'ils sont absents) ; « Dépôt ou
--     retrait d'espèces » désactivé une seule fois, à la première exécution
--     (jamais supprimé ; une réactivation par le titulaire est respectée).
--   * mouvement_tiroir : chaque sortie ou entrée, rattachée à l'événement
--     d'ouverture du tiroir, à la session de caisse et à l'employé ; numéro
--     de bon de sortie (BS-000001) ; jamais supprimée : correction par
--     écriture inverse (corrige_id, une seule par mouvement).
--     L'attendu compte les lignes sans autre écriture (dépense, patient,
--     fournisseur sans grossiste) ; le règlement d'un grossiste (part_tiroir)
--     et les échanges avec le coffre (mouvement_tresorerie pendant la
--     session, signé) sont comptés par leur propre table.
--   * parametre_caisse.seuil_sortie_tiroir (défaut 2 000 DA) : au-delà,
--     validation d'un ayant droit (caisse.cloturer, code comptoir possible).
--   * parametre_surveillance.seuil_sorties_tiroir (défaut 5) : alerte
--     « sorties_tiroir » par employé et par jour (surveiller()).
-- Aucune donnée supprimée. Idempotent. A exécuter après 095_rapprochement_volet.sql.
-- =====================================================================

-- 1. Effet et détail des motifs
ALTER TABLE motif_reglable ADD COLUMN IF NOT EXISTS effet  text NOT NULL DEFAULT 'aucun';
ALTER TABLE motif_reglable ADD COLUMN IF NOT EXISTS detail text NOT NULL DEFAULT 'aucun';
DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'ck_motif_reglable_effet') THEN
        ALTER TABLE motif_reglable ADD CONSTRAINT ck_motif_reglable_effet CHECK (
            (effet = 'aucun' AND detail = 'aucun')
            OR (effet = 'sortie' AND detail IN ('depense', 'fournisseur', 'patient', 'coffre'))
            OR (effet = 'entree' AND detail = 'fond'));
    END IF;
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'ck_motif_reglable_effet_tiroir') THEN
        ALTER TABLE motif_reglable ADD CONSTRAINT ck_motif_reglable_effet_tiroir CHECK (effet = 'aucun' OR liste = 'tiroir');
    END IF;
END $$;
COMMENT ON COLUMN motif_reglable.effet IS 'Tiroir : aucun, sortie ou entree d''argent (montant obligatoire hors « aucun »)';
COMMENT ON COLUMN motif_reglable.detail IS 'Tiroir : détail exigé : depense (catégorie de charge), fournisseur (grossiste ou nom), patient, coffre (retrait vers le coffre), fond (remise d''un fonds de caisse)';

-- 2. Motifs du tiroir (la première exécution désactive « Dépôt ou retrait d'espèces », remplacé par des motifs à effet)
DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM motif_reglable WHERE liste = 'tiroir' AND code = 'retrait_coffre') THEN
        UPDATE motif_reglable SET actif = false, modifie_le = now() WHERE liste = 'tiroir' AND code = 'depot_especes' AND NOT systeme;
    END IF;
END $$;
INSERT INTO motif_reglable (liste, code, libelle, systeme, ordre, effet, detail) VALUES
    ('tiroir', 'monnaie',              'Faire de la monnaie',                       false, 10,  'aucun',  'aucun'),
    ('tiroir', 'erreur_rendu',         'Erreur de rendu de monnaie',                false, 20,  'aucun',  'aucun'),
    ('tiroir', 'verification',         'Vérification du contenu',                   false, 40,  'aucun',  'aucun'),
    ('tiroir', 'remise_fond',          'Remise d''un fonds de caisse',              false, 50,  'entree', 'fond'),
    ('tiroir', 'retrait_coffre',       'Retrait vers le coffre',                    false, 60,  'sortie', 'coffre'),
    ('tiroir', 'paiement_fournisseur', 'Paiement d''un livreur ou d''un fournisseur', false, 70, 'sortie', 'fournisseur'),
    ('tiroir', 'petite_depense',       'Petite dépense',                            false, 80,  'sortie', 'depense'),
    ('tiroir', 'remboursement_client', 'Remboursement d''un client',                false, 90,  'sortie', 'patient'),
    ('tiroir', 'recuperation_objet',   'Récupération d''un objet',                  false, 100, 'aucun',  'aucun'),
    ('tiroir', 'autre',                'Autre (préciser)',                          true,  999, 'aucun',  'aucun')
ON CONFLICT (liste, code) DO NOTHING;

-- 3. Session de caisse : seuil de validation des sorties
ALTER TABLE parametre_caisse ADD COLUMN IF NOT EXISTS seuil_sortie_tiroir numeric(12,2) NOT NULL DEFAULT 2000;
DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'ck_parametre_caisse_seuil_sortie_tiroir') THEN
        ALTER TABLE parametre_caisse ADD CONSTRAINT ck_parametre_caisse_seuil_sortie_tiroir CHECK (seuil_sortie_tiroir >= 0);
    END IF;
END $$;
COMMENT ON COLUMN parametre_caisse.seuil_sortie_tiroir IS 'Sortie d''argent par le tiroir au-delà de ce montant : validation d''un ayant droit (caisse.cloturer, code comptoir possible)';

-- 4. Sorties et entrées du tiroir
CREATE SEQUENCE IF NOT EXISTS seq_numero_bon_sortie;
CREATE TABLE IF NOT EXISTS mouvement_tiroir (
    id                       bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    numero                   text          NOT NULL UNIQUE,
    evenement_caisse_id      bigint        REFERENCES evenement_caisse(id),
    session_caisse_id        bigint        NOT NULL REFERENCES session_caisse(id),
    utilisateur_id           bigint        NOT NULL REFERENCES utilisateur(id),
    poste                    text,
    motif_code               text          NOT NULL,
    motif_libelle            text          NOT NULL,
    sens                     text          NOT NULL CHECK (sens IN ('sortie', 'entree')),
    detail                   text          NOT NULL CHECK (detail IN ('depense', 'fournisseur', 'patient', 'coffre', 'fond')),
    montant                  numeric(12,2) NOT NULL CHECK (montant > 0),
    categorie                text,
    libelle                  text,
    fournisseur_id           bigint        REFERENCES fournisseur(id),
    beneficiaire             text,
    reference                text,
    patient_id               bigint        REFERENCES patient(id),
    raison                   text,
    reglement_fournisseur_id bigint        REFERENCES reglement_fournisseur(id),
    mouvement_tresorerie_id  bigint        REFERENCES mouvement_tresorerie(id),
    valide_par               bigint        REFERENCES utilisateur(id),
    corrige_id               bigint        UNIQUE REFERENCES mouvement_tiroir(id),
    motif_correction         text,
    cree_le                  timestamptz   NOT NULL DEFAULT now(),
    CONSTRAINT ck_mouvement_tiroir_correction CHECK ((corrige_id IS NULL) = (motif_correction IS NULL)),
    CONSTRAINT ck_mouvement_tiroir_depense CHECK (detail <> 'depense' OR corrige_id IS NOT NULL OR (categorie IS NOT NULL AND libelle IS NOT NULL)),
    CONSTRAINT ck_mouvement_tiroir_patient CHECK (detail <> 'patient' OR corrige_id IS NOT NULL OR (patient_id IS NOT NULL AND raison IS NOT NULL)),
    CONSTRAINT ck_mouvement_tiroir_fournisseur CHECK (detail <> 'fournisseur' OR corrige_id IS NOT NULL OR fournisseur_id IS NOT NULL OR beneficiaire IS NOT NULL)
);
CREATE INDEX IF NOT EXISTS ix_mouvement_tiroir_session ON mouvement_tiroir (session_caisse_id);
CREATE INDEX IF NOT EXISTS ix_mouvement_tiroir_employe ON mouvement_tiroir (utilisateur_id, cree_le);
COMMENT ON TABLE mouvement_tiroir IS 'Sorties et entrées d''argent par le tiroir (F8, motif à effet) ; jamais supprimées : correction par écriture inverse (corrige_id)';
COMMENT ON COLUMN mouvement_tiroir.reglement_fournisseur_id IS 'Paiement d''un grossiste : règlement en espèces pris dans le tiroir (part_tiroir), compté dans l''attendu par reglement_fournisseur';
COMMENT ON COLUMN mouvement_tiroir.mouvement_tresorerie_id IS 'Retrait vers le coffre ou remise d''un fonds : mouvement du coffre pendant la session, compté dans l''attendu par mouvement_tresorerie';

DROP TRIGGER IF EXISTS trg_caisse_ouverte ON mouvement_tiroir;
CREATE TRIGGER trg_caisse_ouverte BEFORE INSERT ON mouvement_tiroir FOR EACH ROW EXECUTE FUNCTION verifier_caisse_ouverte();

-- 5. Surveillance : N sorties du tiroir par employé et par jour
ALTER TABLE parametre_surveillance ADD COLUMN IF NOT EXISTS seuil_sorties_tiroir integer NOT NULL DEFAULT 5;
DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'ck_parametre_surveillance_sorties_tiroir') THEN
        ALTER TABLE parametre_surveillance ADD CONSTRAINT ck_parametre_surveillance_sorties_tiroir CHECK (seuil_sorties_tiroir >= 1);
    END IF;
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'ck_alerte_surveillance_type' AND pg_get_constraintdef(oid) LIKE '%''sorties_tiroir''%') THEN
        ALTER TABLE alerte_surveillance DROP CONSTRAINT IF EXISTS ck_alerte_surveillance_type;
        ALTER TABLE alerte_surveillance ADD CONSTRAINT ck_alerte_surveillance_type CHECK (type IN ('lignes_retirees', 'tiroir', 'attentes_abandonnees',
            'hors_horaires', 'remises', 'remises_patient', 'ajustements', 'casses', 'echantillons', 'boites_fantomes', 'sensibles_en_retard',
            'ecart_reception', 'reception_meme_personne', 'retour_sans_avoir', 'avoir_incomplet',
            'credit_nouveau_patient', 'credits_employe', 'credit_sans_remboursement', 'autorisations_valideur', 'autorisations_employe',
            'postes_simultanes', 'baisses_prix', 'sorties_tiroir'));
    END IF;
END $$;

CREATE OR REPLACE FUNCTION surveillance_candidats_tiroir(p_jour date)
RETURNS TABLE (o_type text, o_niveau text, o_utilisateur_id bigint, o_jour date, o_cle text, o_message text, o_notification text, o_valeur numeric)
LANGUAGE sql STABLE AS $$
    WITH p AS (SELECT seuil_sorties_tiroir FROM parametre_surveillance WHERE id = 1),
    sorties AS (
        SELECT m.utilisateur_id, count(*) AS n, sum(m.montant) AS total,
               string_agg(DISTINCT m.motif_libelle, ', ') AS motifs
        FROM mouvement_tiroir m
        WHERE m.sens = 'sortie' AND m.corrige_id IS NULL AND m.cree_le::date = p_jour
        GROUP BY m.utilisateur_id)
    SELECT 'sorties_tiroir', CASE WHEN s.n >= 2 * p.seuil_sorties_tiroir THEN 'urgent' ELSE 'attention' END, s.utilisateur_id, p_jour, '',
           format('%s a fait %s sortie(s) d''argent par le tiroir le %s, pour %s DA (%s).', u.nom_complet, s.n, to_char(p_jour, 'DD/MM/YYYY'),
                  to_char(s.total, 'FM999G999G990D00'), s.motifs),
           format('%s : %s sorties du tiroir le %s.', u.nom_complet, s.n, to_char(p_jour, 'DD/MM')),
           s.n
    FROM sorties s JOIN utilisateur u ON u.id = s.utilisateur_id, p WHERE s.n >= p.seuil_sorties_tiroir;
$$;

CREATE OR REPLACE FUNCTION surveiller(p_jour date) RETURNS TABLE(o_id bigint, o_niveau text, o_notification text)
LANGUAGE plpgsql AS $$
DECLARE
    c        record;
    v_id     bigint;
    v_ancien text;
BEGIN
    FOR c IN SELECT * FROM surveillance_candidats(p_jour)
             UNION ALL SELECT * FROM surveillance_candidats_achats(p_jour)
             UNION ALL SELECT * FROM surveillance_candidats_comptes(p_jour)
             UNION ALL SELECT * FROM surveillance_candidats_tiroir(p_jour) LOOP
        v_id := NULL;
        SELECT a.id, a.niveau INTO v_id, v_ancien FROM alerte_surveillance a
        WHERE a.type = c.o_type AND COALESCE(a.utilisateur_id, 0) = COALESCE(c.o_utilisateur_id, 0) AND a.jour = c.o_jour AND a.cle = c.o_cle;
        IF v_id IS NULL THEN
            INSERT INTO alerte_surveillance (type, niveau, utilisateur_id, jour, cle, message, valeur)
            VALUES (c.o_type, c.o_niveau, c.o_utilisateur_id, c.o_jour, c.o_cle, c.o_message, c.o_valeur)
            RETURNING id INTO v_id;
            o_id := v_id; o_niveau := c.o_niveau; o_notification := c.o_notification;
            RETURN NEXT;
        ELSE
            -- Une anomalie traitée reste traitée ; son message suit les chiffres du jour
            -- Une anomalie traitée qui devient urgente revient « à examiner » (la notification renvoie à l'écran)
            UPDATE alerte_surveillance SET message = c.o_message, valeur = c.o_valeur, niveau = c.o_niveau, mise_a_jour_le = now(),
                   statut = CASE WHEN v_ancien <> c.o_niveau AND c.o_niveau = 'urgent' THEN 'nouvelle' ELSE statut END,
                   action_prise = CASE WHEN v_ancien <> c.o_niveau AND c.o_niveau = 'urgent' AND statut = 'traitee'
                                       THEN concat('Traitée le ', to_char(traitee_le, 'DD/MM/YYYY HH24:MI'), COALESCE(' : ' || action_prise, ''),
                                                   ' ; redevenue urgente.')
                                       ELSE action_prise END,
                   traitee_par = CASE WHEN v_ancien <> c.o_niveau AND c.o_niveau = 'urgent' THEN NULL ELSE traitee_par END,
                   traitee_le = CASE WHEN v_ancien <> c.o_niveau AND c.o_niveau = 'urgent' THEN NULL ELSE traitee_le END
            WHERE id = v_id;
            IF v_ancien <> c.o_niveau AND c.o_niveau = 'urgent' THEN
                o_id := v_id; o_niveau := c.o_niveau; o_notification := c.o_notification;
                RETURN NEXT;
            END IF;
        END IF;
    END LOOP;
END;
$$;
