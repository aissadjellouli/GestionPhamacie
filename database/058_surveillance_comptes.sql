-- =====================================================================
-- Surveillance des comptes (lutte contre le vol), réservée au titulaire
--   * crédits suspects : patient récent avec une grosse dette
--     (patient.cree_par : qui l'a créé), crédits accordés par un employé
--     au-delà d'un montant par jour, patient qui n'a jamais rien remboursé ;
--   * codes comptoir : autorisations sur place trop fréquentes (par celui
--     qui autorise, par celui qui en profite) ;
--   * même employé actif sur deux postes à la fois (connexion_employe :
--     chaque connexion avec le poste et la méthode, ventes par poste) ;
--   * baisses de prix faites par un autre que le titulaire.
-- Idempotent. A exécuter après 057_reception_aveugle.sql.
-- =====================================================================

-- 1. Qui a créé le patient
ALTER TABLE patient ADD COLUMN IF NOT EXISTS cree_par bigint REFERENCES utilisateur (id);
COMMENT ON COLUMN patient.cree_par IS 'Employé qui a créé le patient (vide pour les patients antérieurs ou repris)';

-- 2. Connexions des employés (poste et méthode)
CREATE TABLE IF NOT EXISTS connexion_employe (
    id              bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    utilisateur_id  bigint      NOT NULL REFERENCES utilisateur (id),
    poste           text,
    methode         text        NOT NULL CHECK (methode IN ('mot_de_passe', 'code')),
    cree_le         timestamptz NOT NULL DEFAULT now()
);
COMMENT ON TABLE connexion_employe IS 'Connexions réussies des employés (poste, mot de passe ou code comptoir) : postes simultanés';
CREATE INDEX IF NOT EXISTS ix_connexion_employe ON connexion_employe (utilisateur_id, cree_le);

-- 3. Réglages
ALTER TABLE parametre_surveillance ADD COLUMN IF NOT EXISTS seuil_credit_nouveau_patient numeric(12,2) NOT NULL DEFAULT 5000;
ALTER TABLE parametre_surveillance ADD COLUMN IF NOT EXISTS jours_patient_recent integer NOT NULL DEFAULT 7;
ALTER TABLE parametre_surveillance ADD COLUMN IF NOT EXISTS seuil_credits_jour numeric(12,2) NOT NULL DEFAULT 20000;
ALTER TABLE parametre_surveillance ADD COLUMN IF NOT EXISTS jours_credit_sans_remboursement integer NOT NULL DEFAULT 60;
ALTER TABLE parametre_surveillance ADD COLUMN IF NOT EXISTS seuil_autorisations_valideur integer NOT NULL DEFAULT 10;
ALTER TABLE parametre_surveillance ADD COLUMN IF NOT EXISTS seuil_autorisations_employe integer NOT NULL DEFAULT 5;
ALTER TABLE parametre_surveillance ADD COLUMN IF NOT EXISTS minutes_postes_simultanes integer NOT NULL DEFAULT 10;
ALTER TABLE parametre_surveillance ADD COLUMN IF NOT EXISTS seuil_changements_poste integer NOT NULL DEFAULT 3;
ALTER TABLE parametre_surveillance ADD COLUMN IF NOT EXISTS seuil_baisses_prix integer NOT NULL DEFAULT 1;
DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'ck_parametre_surveillance_comptes') THEN
        ALTER TABLE parametre_surveillance ADD CONSTRAINT ck_parametre_surveillance_comptes CHECK (
            seuil_credit_nouveau_patient > 0 AND jours_patient_recent BETWEEN 1 AND 90 AND seuil_credits_jour > 0
            AND jours_credit_sans_remboursement BETWEEN 1 AND 365 AND seuil_autorisations_valideur >= 1 AND seuil_autorisations_employe >= 1
            AND minutes_postes_simultanes BETWEEN 1 AND 120 AND seuil_changements_poste >= 1 AND seuil_baisses_prix >= 1);
    END IF;
END $$;

-- 4. Types d'anomalie
ALTER TABLE alerte_surveillance DROP CONSTRAINT IF EXISTS ck_alerte_surveillance_type;
ALTER TABLE alerte_surveillance ADD CONSTRAINT ck_alerte_surveillance_type CHECK (type IN ('lignes_retirees', 'tiroir', 'attentes_abandonnees',
    'hors_horaires', 'remises', 'remises_patient', 'ajustements', 'casses', 'echantillons', 'boites_fantomes', 'sensibles_en_retard',
    'ecart_reception', 'reception_meme_personne', 'retour_sans_avoir', 'avoir_incomplet',
    'credit_nouveau_patient', 'credits_employe', 'credit_sans_remboursement', 'autorisations_valideur', 'autorisations_employe',
    'postes_simultanes', 'baisses_prix'));

-- 5. Anomalies des comptes (le nom du patient n'apparaît que dans l'écran, jamais dans la notification)
CREATE OR REPLACE FUNCTION surveillance_candidats_comptes(p_jour date)
RETURNS TABLE (o_type text, o_niveau text, o_utilisateur_id bigint, o_jour date, o_cle text, o_message text, o_notification text, o_valeur numeric)
LANGUAGE sql STABLE AS $$
    WITH p AS (SELECT * FROM parametre_surveillance WHERE id = 1),
    -- Crédits accordés (ventes à crédit), par patient et par employé
    credits AS (
        SELECT m.patient_id, m.utilisateur_id, m.montant, m.cree_le FROM credit_mouvement m WHERE m.type = 'vente_credit'
    ),
    -- Patient récent dont la dette atteint le seuil, crédit accordé ce jour
    nouveaux AS (
        SELECT pt.id, pt.nom, pt.prenom, pt.solde_credit, pt.cree_le, pt.cree_par,
               (SELECT string_agg(DISTINCT u.nom_complet, ', ') FROM credits c JOIN utilisateur u ON u.id = c.utilisateur_id WHERE c.patient_id = pt.id) AS vendeurs
        FROM patient pt, p
        WHERE pt.cree_le >= p_jour - p.jours_patient_recent + 1 AND pt.solde_credit >= p.seuil_credit_nouveau_patient
          AND EXISTS (SELECT 1 FROM credits c WHERE c.patient_id = pt.id AND c.cree_le >= p_jour AND c.cree_le < p_jour + 1)
    ),
    -- Crédits accordés par un employé ce jour
    par_employe AS (
        SELECT c.utilisateur_id, count(*)::int AS n, sum(c.montant) AS montant, count(DISTINCT c.patient_id)::int AS patients
        FROM credits c WHERE c.utilisateur_id IS NOT NULL AND c.cree_le >= p_jour AND c.cree_le < p_jour + 1
        GROUP BY c.utilisateur_id
    ),
    -- Patient endetté qui n'a jamais rien remboursé depuis son premier crédit
    jamais AS (
        SELECT pt.id, pt.nom, pt.prenom, pt.solde_credit, min(c.cree_le) AS premier,
               string_agg(DISTINCT u.nom_complet, ', ') AS vendeurs
        FROM patient pt JOIN credits c ON c.patient_id = pt.id LEFT JOIN utilisateur u ON u.id = c.utilisateur_id, p
        WHERE pt.solde_credit > 0
          AND NOT EXISTS (SELECT 1 FROM credit_mouvement r WHERE r.patient_id = pt.id AND r.type = 'remboursement')
        GROUP BY pt.id, pt.nom, pt.prenom, pt.solde_credit, p.jours_credit_sans_remboursement
        HAVING min(c.cree_le)::date <= p_jour - p.jours_credit_sans_remboursement
    ),
    -- Autorisations sur place par code comptoir ce jour
    autorisations AS (
        SELECT j.utilisateur_id, j.autorise_par, j.details->>'droit' AS droit
        FROM journal_audit j
        WHERE j.action = 'autorisation_code' AND j.autorise_par IS NOT NULL AND j.cree_le >= p_jour AND j.cree_le < p_jour + 1
    ),
    par_valideur AS (
        SELECT a.autorise_par AS utilisateur_id, count(*)::int AS n, string_agg(DISTINCT u.nom_complet, ', ') AS employes
        FROM autorisations a JOIN utilisateur u ON u.id = a.utilisateur_id GROUP BY a.autorise_par
    ),
    par_beneficiaire AS (
        SELECT a.utilisateur_id, count(*)::int AS n, string_agg(DISTINCT v.nom_complet, ', ') AS valideurs, string_agg(DISTINCT a.droit, ', ') AS droits
        FROM autorisations a JOIN utilisateur v ON v.id = a.autorise_par GROUP BY a.utilisateur_id
    ),
    -- Activité par poste ce jour (connexions et ventes) : changements de poste rapprochés
    activite AS (
        SELECT c.utilisateur_id, c.poste, c.cree_le AS t FROM connexion_employe c
        WHERE c.poste IS NOT NULL AND c.cree_le >= p_jour AND c.cree_le < p_jour + 1
        UNION ALL
        SELECT v.utilisateur_id, s.poste, v.date_vente FROM vente v JOIN session_caisse s ON s.id = v.session_caisse_id
        WHERE v.date_vente >= p_jour AND v.date_vente < p_jour + 1
    ),
    suite AS (
        SELECT a.utilisateur_id, a.poste, a.t,
               lag(a.poste) OVER w AS poste_avant, lag(a.t) OVER w AS t_avant
        FROM activite a WINDOW w AS (PARTITION BY a.utilisateur_id ORDER BY a.t)
    ),
    postes AS (
        SELECT s.utilisateur_id, count(*)::int AS n, string_agg(DISTINCT s.poste_avant || ' / ' || s.poste, ', ') AS paires
        FROM suite s, p
        WHERE s.poste_avant IS NOT NULL AND s.poste_avant <> s.poste AND s.t - s.t_avant <= make_interval(mins => p.minutes_postes_simultanes)
        GROUP BY s.utilisateur_id
    ),
    -- Baisses de prix par un autre que le titulaire
    baisses AS (
        SELECT j.utilisateur_id, count(*)::int AS n,
               string_agg(format('%s : %s → %s DA', j.details->>'produit', j.details->>'avant', j.details->>'apres'), ', ' ORDER BY j.id) AS detail,
               sum((j.details->>'avant')::numeric - (j.details->>'apres')::numeric) AS baisse
        FROM journal_audit j JOIN utilisateur u ON u.id = j.utilisateur_id JOIN role r ON r.id = u.role_id
        WHERE j.action = 'prix_modifie' AND j.cree_le >= p_jour AND j.cree_le < p_jour + 1 AND r.code <> 'titulaire'
          AND (j.details->>'apres')::numeric < (j.details->>'avant')::numeric
        GROUP BY j.utilisateur_id
    )
    SELECT 'credit_nouveau_patient', CASE WHEN n.solde_credit >= 2 * p.seuil_credit_nouveau_patient THEN 'urgent' ELSE 'attention' END,
           n.cree_par, n.cree_le::date, 'patient:' || n.id,
           format('%s %s, patient créé le %s%s : dette de %s DA dès ses premiers jours (crédits accordés par %s).', n.nom, COALESCE(n.prenom, ''),
                  to_char(n.cree_le, 'DD/MM/YYYY'), COALESCE(' par ' || (SELECT nom_complet FROM utilisateur WHERE id = n.cree_par), ''),
                  to_char(n.solde_credit, 'FM999G999G990D00'), COALESCE(n.vendeurs, '?')),
           format('Patient cree recemment avec une dette de %s DA (detail dans Surveillance).', to_char(n.solde_credit, 'FM999G999G990')),
           n.solde_credit
    FROM nouveaux n, p
    UNION ALL
    SELECT 'credits_employe', CASE WHEN e.montant >= 2 * p.seuil_credits_jour THEN 'urgent' ELSE 'attention' END, e.utilisateur_id, p_jour, '',
           format('%s a accordé %s vente(s) à crédit le %s à %s patient(s), pour %s DA.', u.nom_complet, e.n, to_char(p_jour, 'DD/MM/YYYY'),
                  e.patients, to_char(e.montant, 'FM999G999G990D00')),
           format('%s : %s DA de credits accordes le %s.', u.nom_complet, to_char(e.montant, 'FM999G999G990'), to_char(p_jour, 'DD/MM')),
           e.montant
    FROM par_employe e JOIN utilisateur u ON u.id = e.utilisateur_id, p WHERE e.montant >= p.seuil_credits_jour
    UNION ALL
    SELECT 'credit_sans_remboursement', 'attention', NULL::bigint, j.premier::date, 'patient:' || j.id,
           format('%s %s doit %s DA et n''a jamais rien remboursé depuis son premier crédit du %s (crédits accordés par %s).',
                  j.nom, COALESCE(j.prenom, ''), to_char(j.solde_credit, 'FM999G999G990D00'), to_char(j.premier, 'DD/MM/YYYY'), COALESCE(j.vendeurs, '?')),
           format('Patient endette sans aucun remboursement depuis le %s (detail dans Surveillance).', to_char(j.premier, 'DD/MM/YYYY')),
           j.solde_credit
    FROM jamais j
    UNION ALL
    SELECT 'autorisations_valideur', CASE WHEN v.n >= 2 * p.seuil_autorisations_valideur THEN 'urgent' ELSE 'attention' END, v.utilisateur_id, p_jour, '',
           format('%s a autorisé %s action(s) par son code comptoir le %s (pour %s).', u.nom_complet, v.n, to_char(p_jour, 'DD/MM/YYYY'), v.employes),
           format('%s : %s autorisations par code comptoir le %s.', u.nom_complet, v.n, to_char(p_jour, 'DD/MM')),
           v.n
    FROM par_valideur v JOIN utilisateur u ON u.id = v.utilisateur_id, p WHERE v.n >= p.seuil_autorisations_valideur
    UNION ALL
    SELECT 'autorisations_employe', CASE WHEN b.n >= 2 * p.seuil_autorisations_employe THEN 'urgent' ELSE 'attention' END, b.utilisateur_id, p_jour, '',
           format('%s a eu besoin de %s autorisation(s) par code comptoir le %s (droits : %s ; autorisé par %s).', u.nom_complet, b.n,
                  to_char(p_jour, 'DD/MM/YYYY'), b.droits, b.valideurs),
           format('%s : %s autorisations par code demandees le %s.', u.nom_complet, b.n, to_char(p_jour, 'DD/MM')),
           b.n
    FROM par_beneficiaire b JOIN utilisateur u ON u.id = b.utilisateur_id, p WHERE b.n >= p.seuil_autorisations_employe
    UNION ALL
    SELECT 'postes_simultanes', CASE WHEN s.n >= 2 * p.seuil_changements_poste THEN 'urgent' ELSE 'attention' END, s.utilisateur_id, p_jour, '',
           format('%s a travaillé sur deux postes à la fois le %s : %s passage(s) d''un poste à l''autre en moins de %s minutes (%s). Son identifiant ou son code est peut-être utilisé par quelqu''un d''autre.',
                  u.nom_complet, to_char(p_jour, 'DD/MM/YYYY'), s.n, p.minutes_postes_simultanes, s.paires),
           format('%s : actif sur deux postes a la fois le %s.', u.nom_complet, to_char(p_jour, 'DD/MM')),
           s.n
    FROM postes s JOIN utilisateur u ON u.id = s.utilisateur_id, p WHERE s.n >= p.seuil_changements_poste
    UNION ALL
    SELECT 'baisses_prix', CASE WHEN b.n >= 2 * p.seuil_baisses_prix THEN 'urgent' ELSE 'attention' END, b.utilisateur_id, p_jour, '',
           format('%s a baissé le prix de vente de %s produit(s) le %s : %s.', u.nom_complet, b.n, to_char(p_jour, 'DD/MM/YYYY'), b.detail),
           format('%s : %s baisse(s) de prix le %s.', u.nom_complet, b.n, to_char(p_jour, 'DD/MM')),
           b.baisse
    FROM baisses b JOIN utilisateur u ON u.id = b.utilisateur_id, p WHERE b.n >= p.seuil_baisses_prix;
$$;

-- 6. Calcul des anomalies : caisse et stock (055), achats (057), comptes
CREATE OR REPLACE FUNCTION surveiller(p_jour date) RETURNS TABLE(o_id bigint, o_niveau text, o_notification text)
LANGUAGE plpgsql AS $$
DECLARE
    c        record;
    v_id     bigint;
    v_ancien text;
BEGIN
    FOR c IN SELECT * FROM surveillance_candidats(p_jour)
             UNION ALL SELECT * FROM surveillance_candidats_achats(p_jour)
             UNION ALL SELECT * FROM surveillance_candidats_comptes(p_jour) LOOP
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
            UPDATE alerte_surveillance SET message = c.o_message, valeur = c.o_valeur, niveau = c.o_niveau, mise_a_jour_le = now()
            WHERE id = v_id;
            IF v_ancien <> c.o_niveau AND c.o_niveau = 'urgent' THEN
                o_id := v_id; o_niveau := c.o_niveau; o_notification := c.o_notification;
                RETURN NEXT;
            END IF;
        END IF;
    END LOOP;
END;
$$;
