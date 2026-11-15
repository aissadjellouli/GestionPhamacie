-- =====================================================================
-- Bordereaux Chifa / MDN, rejets et paiements (décisions du titulaire,
-- 2026-11-15, « go bordereaux complet »)
--   * Le n° de bordereau est celui de CHIFA-Officine : saisi (jamais
--     attribué par le logiciel), unique PAR ORGANISME (avant : unique
--     partout).
--   * Un bordereau par organisme (CNAS, CASNOS, MDN) et par centre payeur
--     (MDN : centre facultatif) ; il contient plusieurs factures Chifa,
--     une par vente : table bordereau_vente.
--   * Statuts : preparation → depose → paye_partiel / paye → solde.
--     Reprise des anciens : ouvert → preparation, envoye → depose,
--     paye → solde, rejet_partiel → paye_partiel.
--   * Rejets (colonnes de bordereau_vente) : motif (liste réglable
--     « rejet_chifa »), montant rejeté, suite : redeposer, perte, patient
--     (dette du patient : mouvement de crédit rattaché à la vente, compté
--     par v_credit_accorde, imputation 087).
--   * Paiement : versement_bordereau (versement d'un organisme imputé à
--     un ou plusieurs bordereaux, en entier ou en partie) ; reprise des
--     versements déjà liés par mouvement_tresorerie.bordereau_id.
--   * Paramètres : délai des ventes à déposer (7 j) et des bordereaux
--     impayés (45 j).
--   * Vues : v_bordereau, v_vente_chifa_a_deposer ; v_suivi_dettes
--     (créance par bordereau, ventes à déposer, rejets à traiter) ;
--     v_credit_accorde (rejets réclamés au patient).
--   * Droit chifa.bordereaux : donné au titulaire et à l'assistant à la
--     première exécution s'il n'est attribué à aucun rôle (modèle 093).
--   Aucune donnée existante supprimée. Idempotent. À exécuter après 109.
-- =====================================================================

-- ---- 1. Bordereau : compléments -------------------------------------------
ALTER TABLE bordereau ADD COLUMN IF NOT EXISTS canal text;
ALTER TABLE bordereau ADD COLUMN IF NOT EXISTS centre_payeur_id bigint REFERENCES centre_payeur(id);
ALTER TABLE bordereau ADD COLUMN IF NOT EXISTS montant_declare numeric(12,2);
ALTER TABLE bordereau ADD COLUMN IF NOT EXISTS cree_le timestamptz NOT NULL DEFAULT now();
ALTER TABLE bordereau ADD COLUMN IF NOT EXISTS cree_par bigint REFERENCES utilisateur(id);
ALTER TABLE bordereau ADD COLUMN IF NOT EXISTS depose_le timestamptz;
ALTER TABLE bordereau ADD COLUMN IF NOT EXISTS depose_par bigint REFERENCES utilisateur(id);

-- Canal des bordereaux existants : MDN si l'organisme a le code « MDN », sinon Chifa
UPDATE bordereau b SET canal = CASE WHEN upper(o.code) = 'MDN' THEN 'mdn' ELSE 'chifa' END
FROM organisme o WHERE o.id = b.organisme_id AND b.canal IS NULL;
ALTER TABLE bordereau ALTER COLUMN canal SET DEFAULT 'chifa';
ALTER TABLE bordereau ALTER COLUMN canal SET NOT NULL;

DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'ck_bordereau_canal') THEN
        ALTER TABLE bordereau ADD CONSTRAINT ck_bordereau_canal CHECK (canal IN ('chifa', 'mdn'));
    END IF;
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'ck_bordereau_montant_declare') THEN
        ALTER TABLE bordereau ADD CONSTRAINT ck_bordereau_montant_declare CHECK (montant_declare IS NULL OR montant_declare >= 0);
    END IF;

    -- Statuts : reprise des anciennes valeurs, puis nouvelle contrainte
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'bordereau_statut_check'
                   AND pg_get_constraintdef(oid) LIKE '%''preparation''%') THEN
        ALTER TABLE bordereau DROP CONSTRAINT IF EXISTS bordereau_statut_check;
        UPDATE bordereau SET statut = CASE statut
            WHEN 'ouvert' THEN 'preparation' WHEN 'envoye' THEN 'depose'
            WHEN 'paye' THEN 'solde' WHEN 'rejet_partiel' THEN 'paye_partiel' ELSE statut END
        WHERE statut IN ('ouvert', 'envoye', 'paye', 'rejet_partiel');
        ALTER TABLE bordereau ALTER COLUMN statut SET DEFAULT 'preparation';
        ALTER TABLE bordereau ADD CONSTRAINT bordereau_statut_check
            CHECK (statut IN ('preparation', 'depose', 'paye_partiel', 'paye', 'solde'));
    END IF;
    -- Déposé (ou au-delà) : date de dépôt connue (date_envoi) ; un ancien bordereau sans date garde sa fin de période
    UPDATE bordereau SET date_envoi = periode_fin WHERE statut <> 'preparation' AND date_envoi IS NULL;
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'ck_bordereau_depot') THEN
        ALTER TABLE bordereau ADD CONSTRAINT ck_bordereau_depot CHECK (statut = 'preparation' OR date_envoi IS NOT NULL);
    END IF;

    -- N° unique par organisme (celui de CHIFA-Officine), non plus dans toute la base
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'uq_bordereau_organisme_numero') THEN
        ALTER TABLE bordereau ADD CONSTRAINT uq_bordereau_organisme_numero UNIQUE (organisme_id, numero);
    END IF;
    IF EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'bordereau_numero_key') THEN
        ALTER TABLE bordereau DROP CONSTRAINT bordereau_numero_key;
    END IF;
END $$;

-- Montant déclaré des anciens bordereaux (sans ventes) : leur montant total
UPDATE bordereau b SET montant_declare = b.montant_total
WHERE b.montant_declare IS NULL AND b.montant_total > 0 AND b.statut <> 'preparation';

COMMENT ON COLUMN bordereau.numero IS 'N° de bordereau de CHIFA-Officine (saisi), unique par organisme (script 110)';
COMMENT ON COLUMN bordereau.date_envoi IS 'Date de dépôt du bordereau à l''organisme (script 110)';
COMMENT ON COLUMN bordereau.montant_total IS 'Montant des anciens bordereaux sans ventes ; attendu des nouveaux : v_bordereau (script 110)';
COMMENT ON COLUMN bordereau.montant_declare IS 'Montant total déclaré par CHIFA-Officine (saisi), comparé à l''attendu (script 110)';
COMMENT ON COLUMN bordereau.canal IS 'chifa (CNAS, CASNOS) ou mdn (script 110)';

-- ---- 2. Ventes d'un bordereau, rejets ---------------------------------------
CREATE TABLE IF NOT EXISTS bordereau_vente (
    id                    bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    bordereau_id          bigint NOT NULL REFERENCES bordereau(id),
    vente_id              bigint NOT NULL REFERENCES vente(id),
    part_organisme        numeric(12,2) NOT NULL,
    ajoute_le             timestamptz NOT NULL DEFAULT now(),
    ajoute_par            bigint NOT NULL REFERENCES utilisateur(id),
    rejete_le             timestamptz,
    rejete_par            bigint REFERENCES utilisateur(id),
    rejet_motif_code      text,
    rejet_texte           text,
    montant_rejete        numeric(12,2) NOT NULL DEFAULT 0,
    suite                 text,
    suite_le              timestamptz,
    suite_par             bigint REFERENCES utilisateur(id),
    credit_mouvement_id   bigint REFERENCES credit_mouvement(id),
    CONSTRAINT uq_bordereau_vente UNIQUE (bordereau_id, vente_id),
    CONSTRAINT ck_bordereau_vente_rejet CHECK (
        (rejete_le IS NULL AND rejete_par IS NULL AND rejet_motif_code IS NULL AND rejet_texte IS NULL AND montant_rejete = 0 AND suite IS NULL)
        OR (rejete_le IS NOT NULL AND rejete_par IS NOT NULL AND rejet_motif_code IS NOT NULL AND montant_rejete > 0
            AND montant_rejete <= part_organisme + 0.01)),
    CONSTRAINT ck_bordereau_vente_suite CHECK (
        (suite IS NULL AND suite_le IS NULL AND suite_par IS NULL AND credit_mouvement_id IS NULL)
        OR (suite IN ('redeposer', 'perte', 'patient') AND suite_le IS NOT NULL AND suite_par IS NOT NULL
            AND (suite = 'patient') = (credit_mouvement_id IS NOT NULL)))
);

-- Une vente n'est que sur un seul bordereau à la fois : seule une facture rejetée « à redéposer » libère la vente
CREATE UNIQUE INDEX IF NOT EXISTS ux_bordereau_vente_active ON bordereau_vente (vente_id) WHERE suite IS DISTINCT FROM 'redeposer';
CREATE INDEX IF NOT EXISTS idx_bordereau_vente_vente ON bordereau_vente (vente_id);
CREATE INDEX IF NOT EXISTS idx_bordereau_statut ON bordereau (statut, organisme_id);
-- Historique d'un bordereau (fiche) : entrées du journal par enregistrement visé
CREATE INDEX IF NOT EXISTS idx_audit_cible ON journal_audit (table_cible, enregistrement_id) WHERE table_cible IS NOT NULL;

COMMENT ON TABLE bordereau_vente IS 'Factures Chifa (une par vente) d''un bordereau, et leur rejet éventuel (script 110)';
COMMENT ON COLUMN bordereau_vente.part_organisme IS 'Part organisme retenue au rattachement (information ; l''attendu se lit dans v_vente_part_organisme)';
COMMENT ON COLUMN bordereau_vente.rejet_motif_code IS 'Motif du rejet (motif_reglable, liste rejet_chifa)';
COMMENT ON COLUMN bordereau_vente.suite IS 'Suite du rejet : redeposer (vente à déposer à nouveau), perte, patient (ajoutée à sa dette)';

-- ---- 3. Versements imputés aux bordereaux -------------------------------------
CREATE TABLE IF NOT EXISTS versement_bordereau (
    id            bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    mouvement_id  bigint NOT NULL REFERENCES mouvement_tresorerie(id),
    bordereau_id  bigint NOT NULL REFERENCES bordereau(id),
    montant       numeric(12,2) NOT NULL CHECK (montant > 0),
    CONSTRAINT uq_versement_bordereau UNIQUE (mouvement_id, bordereau_id)
);
CREATE INDEX IF NOT EXISTS idx_versement_bordereau_bordereau ON versement_bordereau (bordereau_id);
COMMENT ON TABLE versement_bordereau IS 'Part d''un versement d''organisme (trésorerie) imputée à un bordereau (script 110)';

-- Reprise : versements déjà liés à un bordereau (mouvement_tresorerie.bordereau_id)
INSERT INTO versement_bordereau (mouvement_id, bordereau_id, montant)
SELECT m.id, m.bordereau_id, m.montant FROM mouvement_tresorerie m
WHERE m.bordereau_id IS NOT NULL AND m.sens = 'entree'
  AND NOT EXISTS (SELECT 1 FROM versement_bordereau vb WHERE vb.mouvement_id = m.id AND vb.bordereau_id = m.bordereau_id);

-- ---- 4. Délais réglables (Administration › Caisse) ---------------------------
ALTER TABLE parametre_caisse ADD COLUMN IF NOT EXISTS chifa_delai_depot_jours integer NOT NULL DEFAULT 7;
ALTER TABLE parametre_caisse ADD COLUMN IF NOT EXISTS chifa_delai_paiement_jours integer NOT NULL DEFAULT 45;
DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'ck_parametre_caisse_chifa_delai_depot') THEN
        ALTER TABLE parametre_caisse ADD CONSTRAINT ck_parametre_caisse_chifa_delai_depot CHECK (chifa_delai_depot_jours BETWEEN 1 AND 365);
    END IF;
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'ck_parametre_caisse_chifa_delai_paiement') THEN
        ALTER TABLE parametre_caisse ADD CONSTRAINT ck_parametre_caisse_chifa_delai_paiement CHECK (chifa_delai_paiement_jours BETWEEN 1 AND 365);
    END IF;
END $$;
COMMENT ON COLUMN parametre_caisse.chifa_delai_depot_jours IS 'Vente Chifa/MDN à déposer signalée en retard au-delà (jours, script 110)';
COMMENT ON COLUMN parametre_caisse.chifa_delai_paiement_jours IS 'Bordereau déposé non soldé signalé impayé au-delà (jours après le dépôt, script 110)';

-- ---- 5. Motifs de rejet (liste réglable rejet_chifa) -------------------------
DO $$
DECLARE
    c        text;
    def      text;
    valeurs  text[];
BEGIN
    SELECT conname, pg_get_constraintdef(oid) INTO c, def FROM pg_constraint
    WHERE conrelid = 'motif_reglable'::regclass AND contype = 'c' AND conname = 'ck_motif_reglable_liste';
    IF def IS NOT NULL AND def LIKE '%''rejet_chifa''%' THEN
        RETURN;
    END IF;
    SELECT array_agg(DISTINCT v ORDER BY v) INTO valeurs FROM (
        SELECT (regexp_matches(COALESCE(def, ''), '''([a-z0-9_]+)''', 'g'))[1] AS v
        UNION SELECT liste FROM motif_reglable
        UNION SELECT 'rejet_chifa'
    ) x;
    IF c IS NOT NULL THEN
        EXECUTE format('ALTER TABLE motif_reglable DROP CONSTRAINT %I', c);
    END IF;
    EXECUTE format('ALTER TABLE motif_reglable ADD CONSTRAINT ck_motif_reglable_liste CHECK (liste = ANY (%L::text[]))', valeurs);
END $$;

INSERT INTO motif_reglable (liste, code, libelle, systeme, ordre) VALUES
    ('rejet_chifa', 'droits_expires',     'Droits expirés',            false, 10),
    ('rejet_chifa', 'ordonnance_non_conforme', 'Ordonnance non conforme', false, 20),
    ('rejet_chifa', 'produit_non_remboursable', 'Produit non remboursable', false, 30),
    ('rejet_chifa', 'erreur_saisie',      'Erreur de saisie',          false, 40),
    ('rejet_chifa', 'autre',              'Autre (préciser)',          true,  999)
ON CONFLICT (liste, code) DO NOTHING;

-- ---- 6. Droit chifa.bordereaux ----------------------------------------------
-- Posé seulement s'il n'est attribué à aucun rôle (première exécution) : une réexécution
-- n'annule jamais un choix fait ensuite par le titulaire.
DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM permission WHERE code = 'chifa.bordereaux') THEN
        INSERT INTO permission (code, module, libelle, sensible)
        VALUES ('chifa.bordereaux', 'Chifa', 'Gérer les bordereaux et rejets', false)
        ON CONFLICT DO NOTHING;
    END IF;
    IF NOT EXISTS (SELECT 1 FROM role_permission WHERE permission_code = 'chifa.bordereaux') THEN
        ALTER TABLE role_permission DISABLE TRIGGER trg_proteger_role_systeme;
        INSERT INTO role_permission (role_id, permission_code)
        SELECT r.id, 'chifa.bordereaux' FROM role r WHERE r.code IN ('titulaire', 'assistant')
        ON CONFLICT DO NOTHING;
        ALTER TABLE role_permission ENABLE TRIGGER trg_proteger_role_systeme;
    END IF;
END $$;

-- ---- 7. Vues --------------------------------------------------------------------
-- Bordereau et ses montants : attendu (parts organisme retenues des ventes ; ancien bordereau sans ventes :
-- son montant total), rejeté, versé, reste à recevoir
CREATE OR REPLACE VIEW v_bordereau AS
SELECT b.id, b.organisme_id, o.code AS organisme, b.canal, b.centre_payeur_id, b.numero, b.periode_debut, b.periode_fin,
       b.date_envoi AS date_depot, b.statut, b.montant_declare,
       COALESCE(x.ventes, 0)::integer AS ventes,
       (CASE WHEN COALESCE(x.ventes, 0) > 0 THEN x.attendu ELSE b.montant_total END)::numeric(12,2) AS attendu,
       COALESCE(x.rejete, 0)::numeric(12,2) AS rejete,
       COALESCE(x.rejets_a_traiter, 0)::integer AS rejets_a_traiter,
       COALESCE(y.verse, 0)::numeric(12,2) AS verse,
       ((CASE WHEN COALESCE(x.ventes, 0) > 0 THEN x.attendu ELSE b.montant_total END) - COALESCE(x.rejete, 0) - COALESCE(y.verse, 0))::numeric(12,2) AS reste
FROM bordereau b
JOIN organisme o ON o.id = b.organisme_id
LEFT JOIN LATERAL (
    SELECT count(*) AS ventes, sum(po.part_organisme_retenue) AS attendu, sum(bv.montant_rejete) AS rejete,
           count(*) FILTER (WHERE bv.rejete_le IS NOT NULL AND bv.suite IS NULL) AS rejets_a_traiter
    FROM bordereau_vente bv JOIN v_vente_part_organisme po ON po.vente_id = bv.vente_id
    WHERE bv.bordereau_id = b.id) x ON true
LEFT JOIN LATERAL (SELECT sum(vb.montant) AS verse FROM versement_bordereau vb WHERE vb.bordereau_id = b.id) y ON true;

-- Ventes Chifa / MDN à déposer : encaissées, non exclues, sur aucun bordereau, avec leur n° de facture Chifa (canal chifa),
-- part organisme à facturer ; une facture rejetée « à redéposer » y revient, pas une rejetée passée en perte ou réclamée au patient
CREATE OR REPLACE VIEW v_vente_chifa_a_deposer AS
SELECT v.id AS vente_id, v.numero, v.date_vente, v.organisme_id, v.canal_organisme AS canal, v.centre_payeur_id, v.patient_id,
       v.chifa_facture, po.part_organisme_retenue AS part_organisme,
       EXISTS (SELECT 1 FROM bordereau_vente bv WHERE bv.vente_id = v.id AND bv.suite = 'redeposer') AS redeposee
FROM vente v
JOIN v_vente_part_organisme po ON po.vente_id = v.id
WHERE v.canal_organisme IN ('chifa', 'mdn') AND v.statut = 'validee' AND v.bordereau_id IS NULL AND v.chifa_exclue_le IS NULL
  AND (v.canal_organisme = 'mdn' OR v.chifa_facture IS NOT NULL)
  AND po.part_organisme_retenue > 0
  AND NOT EXISTS (SELECT 1 FROM bordereau_vente bv WHERE bv.vente_id = v.id AND bv.suite IS DISTINCT FROM 'redeposer');

-- Crédit accordé : paiements à crédit, régularisations à la charge du patient (097) et rejets Chifa réclamés au patient (110)
CREATE OR REPLACE VIEW v_credit_accorde AS
 SELECT v.id AS vente_id,
    v.patient_id,
    v.numero,
    v.date_vente,
    (x.montant - COALESCE(( SELECT sum(rc.montant_rembourse) AS sum
           FROM retour_client rc
          WHERE (rc.vente_id = v.id)), (0)::numeric)) AS montant
   FROM (vente v
     JOIN LATERAL ( SELECT ((COALESCE(( SELECT sum(p.montant) AS sum
                   FROM paiement p
                  WHERE ((p.vente_id = v.id) AND (p.mode = 'credit'::text))), (0)::numeric) + COALESCE(( SELECT sum(r.montant_patient) AS sum
                   FROM regularisation_part_organisme r
                  WHERE ((r.vente_id = v.id) AND (r.sort = 'charge_patient'::text) AND (r.statut = 'active'::text))), (0)::numeric))
                  + COALESCE(( SELECT sum(bv.montant_rejete) AS sum
                   FROM bordereau_vente bv
                  WHERE ((bv.vente_id = v.id) AND (bv.suite = 'patient'::text))), (0)::numeric)) AS montant) x ON ((x.montant > (0)::numeric)))
  WHERE ((v.statut = 'validee'::text) AND (v.patient_id IS NOT NULL));

-- Dettes et créances : créance par bordereau (déposé, non soldé ; échéance = dépôt + délai de paiement),
-- rejets à traiter, ventes à déposer (échéance = vente + délai de dépôt)
CREATE OR REPLACE VIEW v_suivi_dettes AS
 SELECT 'creance'::text AS sens,
    'patient'::text AS categorie,
    c.patient_id AS tiers_id,
    TRIM(BOTH FROM concat_ws(' '::text, p.nom, p.prenom)) AS tiers,
    ('Vente à crédit '::text || c.numero) AS libelle,
    (c.date_vente)::date AS date_reference,
    NULL::date AS echeance,
    c.reste AS montant,
    false AS rejetee
   FROM (v_vente_credit c
     JOIN patient p ON ((p.id = c.patient_id)))
  WHERE ((c.reste > (0)::numeric) AND (NOT (EXISTS ( SELECT 1
           FROM (facture_client_ligne fl
             JOIN v_facture_client vf ON ((vf.id = fl.facture_id)))
          WHERE ((fl.vente_id = c.vente_id) AND (fl.part = 'patient'::text) AND (vf.type = 'facture'::text) AND (vf.statut <> 'remplacee'::text) AND (NOT vf.hors_suivi) AND (vf.reste > (0)::numeric))))))
UNION ALL
 SELECT 'creance'::text AS sens,
    'patient'::text AS categorie,
    p.id AS tiers_id,
    TRIM(BOTH FROM concat_ws(' '::text, p.nom, p.prenom)) AS tiers,
    'Dette antérieure (ancien logiciel ou mise en place du relevé)'::text AS libelle,
    (COALESCE(( SELECT min(m.cree_le) AS min
           FROM credit_mouvement m
          WHERE (m.patient_id = p.id)), now()))::date AS date_reference,
    NULL::date AS echeance,
    (p.solde_credit - COALESCE(( SELECT sum(c.reste) AS sum
           FROM v_vente_credit c
          WHERE (c.patient_id = p.id)), (0)::numeric)) AS montant,
    false AS rejetee
   FROM patient p
  WHERE ((p.solde_credit - COALESCE(( SELECT sum(c.reste) AS sum
           FROM v_vente_credit c
          WHERE (c.patient_id = p.id)), (0)::numeric)) > (0)::numeric)
UNION ALL
 SELECT 'creance'::text AS sens,
        CASE f.destinataire
            WHEN 'organisme'::text THEN 'facture_organisme'::text
            WHEN 'entreprise'::text THEN 'facture_entreprise'::text
            ELSE 'facture_patient'::text
        END AS categorie,
    COALESCE(f.organisme_id, f.client_id, f.patient_id) AS tiers_id,
    f.destinataire_nom AS tiers,
    ('Facture '::text || f.numero) AS libelle,
    f.date_facture AS date_reference,
    fc.date_echeance AS echeance,
    f.reste AS montant,
    (f.statut = 'rejetee'::text) AS rejetee
   FROM (v_facture_client f
     JOIN facture_client fc ON ((fc.id = f.id)))
  WHERE ((f.type = 'facture'::text) AND (f.statut <> 'remplacee'::text) AND (NOT f.hors_suivi) AND (f.reste > (0)::numeric))
UNION ALL
 SELECT 'creance'::text AS sens,
    'bordereau'::text AS categorie,
    b.organisme_id AS tiers_id,
    o.nom AS tiers,
    ('Bordereau '::text || b.numero) AS libelle,
    COALESCE(b.date_depot, b.periode_fin) AS date_reference,
    (COALESCE(b.date_depot, b.periode_fin) + COALESCE(( SELECT pc.chifa_delai_paiement_jours FROM parametre_caisse pc WHERE (pc.id = 1)), 45)) AS echeance,
    b.reste AS montant,
    (b.rejets_a_traiter > 0) AS rejetee
   FROM (v_bordereau b
     JOIN organisme o ON ((o.id = b.organisme_id)))
  WHERE ((b.statut = ANY (ARRAY['depose'::text, 'paye_partiel'::text])) AND (b.reste > (0)::numeric))
UNION ALL
 SELECT 'creance'::text AS sens,
    'bordereau'::text AS categorie,
    b.organisme_id AS tiers_id,
    o.nom AS tiers,
    ((('Rejet à traiter · vente '::text || v.numero) || ' · bordereau '::text) || b.numero) AS libelle,
    (bv.rejete_le)::date AS date_reference,
    NULL::date AS echeance,
    bv.montant_rejete AS montant,
    true AS rejetee
   FROM (((bordereau_vente bv
     JOIN bordereau b ON ((b.id = bv.bordereau_id)))
     JOIN organisme o ON ((o.id = b.organisme_id)))
     JOIN vente v ON ((v.id = bv.vente_id)))
  WHERE ((bv.rejete_le IS NOT NULL) AND (bv.suite IS NULL))
UNION ALL
 SELECT 'creance'::text AS sens,
    'chifa_a_deposer'::text AS categorie,
    a.organisme_id AS tiers_id,
    o.nom AS tiers,
    ((('Vente '::text || a.numero) || ' à déposer'::text) || COALESCE((' · facture Chifa '::text || a.chifa_facture), ''::text)) AS libelle,
    (a.date_vente)::date AS date_reference,
    ((a.date_vente)::date + COALESCE(( SELECT pc.chifa_delai_depot_jours FROM parametre_caisse pc WHERE (pc.id = 1)), 7)) AS echeance,
    a.part_organisme AS montant,
    false AS rejetee
   FROM (v_vente_chifa_a_deposer a
     JOIN organisme o ON ((o.id = a.organisme_id)))
UNION ALL
 SELECT 'dette'::text AS sens,
    'grossiste'::text AS categorie,
    ff.fournisseur_id AS tiers_id,
    ff.fournisseur AS tiers,
    COALESCE(('Facture '::text || ff.numero_facture), ('BL '::text || ff.numero_bl), 'Réception'::text) AS libelle,
    (ff.date_reception)::date AS date_reference,
    NULL::date AS echeance,
    ff.reste AS montant,
    false AS rejetee
   FROM v_facture_fournisseur ff
  WHERE (ff.reste > (0)::numeric)
UNION ALL
 SELECT 'dette'::text AS sens,
    'grossiste'::text AS categorie,
    f.id AS tiers_id,
    f.raison_sociale AS tiers,
    'Ancien solde (hors factures du logiciel)'::text AS libelle,
    (COALESCE(( SELECT min(m.cree_le) AS min
           FROM fournisseur_mouvement m
          WHERE (m.fournisseur_id = f.id)), now()))::date AS date_reference,
    NULL::date AS echeance,
    (f.solde - COALESCE(( SELECT sum(ff.reste) AS sum
           FROM v_facture_fournisseur ff
          WHERE ((ff.fournisseur_id = f.id) AND (ff.reste > (0)::numeric))), (0)::numeric)) AS montant,
    false AS rejetee
   FROM fournisseur f
  WHERE ((f.solde - COALESCE(( SELECT sum(ff.reste) AS sum
           FROM v_facture_fournisseur ff
          WHERE ((ff.fournisseur_id = f.id) AND (ff.reste > (0)::numeric))), (0)::numeric)) > (0)::numeric)
UNION ALL
 SELECT 'dette'::text AS sens,
    'effet'::text AS categorie,
    g.fournisseur_id AS tiers_id,
    f.raison_sociale AS tiers,
    ((('Traite '::text || COALESCE(g.reference, ''::text)) || COALESCE(((' ('::text || g.banque) || ')'::text), ''::text)) ||
        CASE
            WHEN (g.echeance < CURRENT_DATE) THEN ' · échue, non payée'::text
            ELSE ''::text
        END) AS libelle,
    g.date_reglement AS date_reference,
    g.echeance,
    g.montant,
    false AS rejetee
   FROM (reglement_fournisseur g
     JOIN fournisseur f ON ((f.id = g.fournisseur_id)))
  WHERE ((g.moyen = 'effet'::text) AND (NOT (EXISTS ( SELECT 1
           FROM paiement_effet pe
          WHERE (pe.reglement_id = g.id)))) AND ((g.echeance >= CURRENT_DATE) OR (g.compte_id IS NULL)));
