-- =====================================================================
-- Trésorerie, règlements des grossistes et factures clients
--   * reglement_fournisseur.moyen : nouveau moyen « prelevement »
--     (prélèvement bancaire). Chèque, virement et prélèvement sont
--     débités d'une banque obligatoire : contrôle fait par le logiciel
--     (pas de contrainte en base : des règlements anciens par chèque
--     ont été enregistrés « hors trésorerie », sans compte) ;
--   * paiement_effet : paiement d'une traite (effet) à son échéance,
--     confirmé par le titulaire : espèces (coffre, complété au besoin
--     par l'argent du tiroir porté au coffre), chèque, virement ou
--     prélèvement (banque obligatoire). Une traite remise avec une
--     banque reste débitée à l'échéance sans action : sa confirmation
--     est notée, sans second débit ;
--   * compte_carte_periode : banque qui reçoit les paiements par carte,
--     période par période. Changer de banque « carte » ne fait plus
--     changer de banque les paiements passés ; le changement vaut à
--     partir du jour même. Reprise : la banque actuelle vaut depuis
--     toujours ;
--   * reglement_facture_client.part_credit : part d'un encaissement de
--     facture client qui solde la dette du patient pour les ventes à
--     crédit de la facture (déjà déduite du reste de la facture par la
--     dette réglée : pas de double compte) ;
--   * vues recréées à l'identique (mêmes colonnes, même ordre) :
--     v_solde_tresorerie et v_mouvement_tresorerie (cartes par période,
--     traites payées), v_facture_client (part_credit), v_suivi_dettes
--     (vente à crédit facturée comptée une fois, dans la facture ;
--     traite échue non payée signalée).
-- Idempotent. A exécuter après 073_cle_encaissement.sql.
-- =====================================================================

-- ---- Moyen « prélèvement » ----------------------------------------------------
DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_constraint
                   WHERE conname = 'reglement_fournisseur_moyen_check'
                     AND pg_get_constraintdef(oid) LIKE '%''prelevement''%') THEN
        ALTER TABLE reglement_fournisseur DROP CONSTRAINT IF EXISTS reglement_fournisseur_moyen_check;
        ALTER TABLE reglement_fournisseur ADD CONSTRAINT reglement_fournisseur_moyen_check
            CHECK (moyen IN ('especes', 'cheque', 'virement', 'effet', 'prelevement'));
    END IF;
END $$;

-- ---- Paiement des traites à l'échéance ------------------------------------------
CREATE TABLE IF NOT EXISTS paiement_effet (
    id              bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    reglement_id    bigint NOT NULL UNIQUE REFERENCES reglement_fournisseur(id),
    date_paiement   date NOT NULL,
    moyen           text NOT NULL CHECK (moyen IN ('especes', 'cheque', 'virement', 'prelevement')),
    compte_id       bigint REFERENCES compte_tresorerie(id),
    reference       text,
    part_tiroir     numeric(12,2) NOT NULL DEFAULT 0 CHECK (part_tiroir >= 0),
    versement_id    bigint REFERENCES mouvement_tresorerie(id),
    utilisateur_id  bigint NOT NULL REFERENCES utilisateur(id),
    cree_le         timestamptz NOT NULL DEFAULT now(),
    CHECK (moyen = 'especes' OR compte_id IS NOT NULL),
    CHECK (part_tiroir = 0 OR (moyen = 'especes' AND versement_id IS NOT NULL))
);
COMMENT ON TABLE paiement_effet IS 'Traite payée au grossiste à son échéance, confirmée par le titulaire ; débitée du coffre (espèces) ou de la banque choisie, sauf traite remise avec une banque (déjà débitée à l''échéance)';
COMMENT ON COLUMN paiement_effet.part_tiroir IS 'Espèces : complément pris dans le tiroir de la caisse ouverte et porté au coffre (versement_id) avant le paiement';

-- ---- Banque des cartes, période par période -----------------------------------------
CREATE TABLE IF NOT EXISTS compte_carte_periode (
    id         bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    compte_id  bigint NOT NULL REFERENCES compte_tresorerie(id),
    du         date NOT NULL,
    au         date,
    CHECK (au IS NULL OR au >= du)
);
COMMENT ON TABLE compte_carte_periode IS 'Banque qui reçoit les paiements par carte, du … au … (au vide : en cours). Tenue par le déclencheur de compte_tresorerie.recoit_carte';
CREATE INDEX IF NOT EXISTS idx_compte_carte_periode ON compte_carte_periode (compte_id, du);

-- Reprise : la banque « carte » actuelle vaut depuis toujours
INSERT INTO compte_carte_periode (compte_id, du)
SELECT c.id, '-infinity'::date FROM compte_tresorerie c
WHERE c.recoit_carte AND NOT EXISTS (SELECT 1 FROM compte_carte_periode)
ORDER BY c.id LIMIT 1;

-- Changement de banque « carte » : la période de l'ancienne s'arrête la veille, celle de la nouvelle commence le jour même
CREATE OR REPLACE FUNCTION suivre_compte_carte() RETURNS trigger
    LANGUAGE plpgsql
    AS $$
BEGIN
    IF TG_OP = 'UPDATE' AND OLD.recoit_carte AND NOT NEW.recoit_carte THEN
        UPDATE compte_carte_periode SET au = current_date - 1 WHERE compte_id = NEW.id AND au IS NULL AND du < current_date;
        -- Période ouverte le jour même : elle n'a duré aucun jour
        DELETE FROM compte_carte_periode WHERE compte_id = NEW.id AND au IS NULL AND du >= current_date;
    ELSIF NEW.recoit_carte AND (TG_OP = 'INSERT' OR NOT OLD.recoit_carte) THEN
        -- Une seule banque à la fois : une autre période encore ouverte est close (le logiciel retire d'abord la case à l'ancienne)
        UPDATE compte_carte_periode SET au = current_date - 1 WHERE compte_id <> NEW.id AND au IS NULL AND du < current_date;
        DELETE FROM compte_carte_periode WHERE compte_id <> NEW.id AND au IS NULL AND du >= current_date;
        INSERT INTO compte_carte_periode (compte_id, du) VALUES (NEW.id, current_date);
    END IF;
    RETURN NEW;
END $$;

DROP TRIGGER IF EXISTS trg_compte_carte ON compte_tresorerie;
CREATE TRIGGER trg_compte_carte AFTER INSERT OR UPDATE OF recoit_carte ON compte_tresorerie
    FOR EACH ROW EXECUTE FUNCTION suivre_compte_carte();

-- ---- Facture client encaissée : part qui solde la dette du patient --------------------
ALTER TABLE reglement_facture_client ADD COLUMN IF NOT EXISTS part_credit numeric(12,2) NOT NULL DEFAULT 0
    CHECK (part_credit >= 0 AND part_credit <= montant);
COMMENT ON COLUMN reglement_facture_client.part_credit IS 'Part de l''encaissement qui a soldé la dette du patient pour les ventes à crédit de la facture (comptée dans la dette réglée, pas une seconde fois dans le reste)';

-- ---- Vues (mêmes colonnes, même ordre) ---------------------------------------------------
CREATE OR REPLACE VIEW v_solde_tresorerie AS
SELECT c.id, c.type, c.nom, c.banque, c.numero_compte, c.recoit_carte, c.actif, c.solde_initial, c.date_initiale,
       c.solde_initial
       + COALESCE((SELECT sum(CASE WHEN m.sens = 'entree' THEN m.montant ELSE -m.montant END)
                   FROM mouvement_tresorerie m
                   WHERE m.compte_id = c.id AND m.date_mouvement >= c.date_initiale), 0)
       -- Cartes : à la banque qui les recevait le jour du paiement
       + COALESCE((SELECT sum(p.montant)
                   FROM paiement p
                   JOIN compte_carte_periode h ON h.compte_id = c.id AND p.cree_le::date >= h.du AND (h.au IS NULL OR p.cree_le::date <= h.au)
                   WHERE p.mode = 'carte' AND p.cree_le >= c.date_initiale
                     AND (p.vente_id IS NULL OR EXISTS (SELECT 1 FROM vente v WHERE v.id = p.vente_id AND v.statut IN ('validee', 'annulee')))), 0)
       + COALESCE((SELECT sum(p.montant)
                   FROM remise_cheques r
                   JOIN remise_cheque_paiement rp ON rp.remise_id = r.id
                   JOIN paiement p ON p.id = rp.paiement_id
                   WHERE r.compte_id = c.id AND r.date_remise >= c.date_initiale), 0)
       - COALESCE((SELECT sum(CASE WHEN g.moyen = 'especes' THEN g.montant - g.part_tiroir ELSE g.montant END)
                   FROM reglement_fournisseur g
                   WHERE g.montant > g.part_tiroir
                     AND (g.moyen = 'especes' AND c.type = 'coffre' OR g.moyen <> 'especes' AND g.compte_id = c.id)
                     AND CASE WHEN g.moyen = 'effet' THEN g.echeance ELSE g.date_reglement END >= c.date_initiale
                     AND (g.moyen <> 'effet' OR g.echeance <= current_date)), 0)
       -- Traites remises sans banque, payées à l'échéance (coffre ou banque choisie)
       - COALESCE((SELECT sum(g.montant)
                   FROM paiement_effet pe
                   JOIN reglement_fournisseur g ON g.id = pe.reglement_id
                   WHERE g.compte_id IS NULL
                     AND (pe.moyen = 'especes' AND c.type = 'coffre' OR pe.moyen <> 'especes' AND pe.compte_id = c.id)
                     AND pe.date_paiement >= c.date_initiale), 0)
       - COALESCE((SELECT sum(x.montant_rembourse)
                   FROM (SELECT a.cree_le, a.montant_rembourse FROM annulation_vente a WHERE a.mode = 'carte' AND a.montant_rembourse > 0
                         UNION ALL
                         SELECT rc.cree_le, rc.montant_rembourse FROM retour_client rc WHERE rc.mode = 'carte' AND rc.montant_rembourse > 0) x
                   JOIN compte_carte_periode h ON h.compte_id = c.id AND x.cree_le::date >= h.du AND (h.au IS NULL OR x.cree_le::date <= h.au)
                   WHERE x.cree_le >= c.date_initiale), 0) AS solde
FROM compte_tresorerie c;

CREATE OR REPLACE VIEW v_mouvement_tresorerie AS
SELECT m.compte_id, m.date_mouvement AS date, m.sens, m.montant, m.nature, m.categorie, m.libelle, m.reference,
       u.nom_complet AS employe, 'saisi'::text AS origine, m.id AS source_id, m.cree_le
FROM mouvement_tresorerie m
JOIN utilisateur u ON u.id = m.utilisateur_id
UNION ALL
SELECT h.compte_id, p.cree_le::date AS date, 'entree'::text AS sens, sum(p.montant) AS montant, 'carte'::text AS nature,
       NULL::text AS categorie, 'Paiements par carte du jour'::text AS libelle, NULL::text AS reference, NULL::text AS employe,
       'calcule'::text AS origine, NULL::bigint AS source_id, max(p.cree_le) AS cree_le
FROM paiement p
JOIN compte_carte_periode h ON p.cree_le::date >= h.du AND (h.au IS NULL OR p.cree_le::date <= h.au)
LEFT JOIN vente v ON v.id = p.vente_id
WHERE p.mode = 'carte' AND (p.vente_id IS NULL OR v.statut IN ('validee', 'annulee'))
GROUP BY h.compte_id, p.cree_le::date
UNION ALL
SELECT r.compte_id, r.date_remise AS date, 'entree'::text AS sens, sum(p.montant) AS montant, 'remise_cheques'::text AS nature,
       NULL::text AS categorie, format('Remise de %s chèque(s)', count(*)) AS libelle, r.reference, u.nom_complet AS employe,
       'calcule'::text AS origine, r.id AS source_id, r.cree_le
FROM remise_cheques r
JOIN remise_cheque_paiement rp ON rp.remise_id = r.id
JOIN paiement p ON p.id = rp.paiement_id
JOIN utilisateur u ON u.id = r.utilisateur_id
GROUP BY r.id, u.nom_complet
UNION ALL
SELECT CASE WHEN g.moyen = 'especes' THEN (SELECT compte_tresorerie.id FROM compte_tresorerie WHERE compte_tresorerie.type = 'coffre') ELSE g.compte_id END AS compte_id,
       CASE WHEN g.moyen = 'effet' THEN g.echeance ELSE g.date_reglement END AS date,
       'sortie'::text AS sens,
       CASE WHEN g.moyen = 'especes' THEN g.montant - g.part_tiroir ELSE g.montant END AS montant,
       'reglement_grossiste'::text AS nature, NULL::text AS categorie, 'Règlement '::text || f.raison_sociale AS libelle, g.reference,
       u.nom_complet AS employe, 'calcule'::text AS origine, g.id AS source_id, g.cree_le
FROM reglement_fournisseur g
JOIN fournisseur f ON f.id = g.fournisseur_id
JOIN utilisateur u ON u.id = g.utilisateur_id
WHERE g.montant > g.part_tiroir AND (g.moyen = 'especes' OR g.compte_id IS NOT NULL)
UNION ALL
SELECT CASE WHEN pe.moyen = 'especes' THEN (SELECT compte_tresorerie.id FROM compte_tresorerie WHERE compte_tresorerie.type = 'coffre') ELSE pe.compte_id END AS compte_id,
       pe.date_paiement AS date, 'sortie'::text AS sens, g.montant, 'reglement_grossiste'::text AS nature, NULL::text AS categorie,
       'Traite '::text || COALESCE(g.reference || ' ', '') || 'payée · ' || f.raison_sociale AS libelle, pe.reference,
       u.nom_complet AS employe, 'calcule'::text AS origine, pe.id AS source_id, pe.cree_le
FROM paiement_effet pe
JOIN reglement_fournisseur g ON g.id = pe.reglement_id
JOIN fournisseur f ON f.id = g.fournisseur_id
JOIN utilisateur u ON u.id = pe.utilisateur_id
WHERE g.compte_id IS NULL
UNION ALL
SELECT h.compte_id, x.cree_le::date AS date, 'sortie'::text AS sens, x.montant_rembourse AS montant, 'remboursement_carte'::text AS nature,
       NULL::text AS categorie, x.libelle, NULL::text AS reference, NULL::text AS employe, 'calcule'::text AS origine,
       NULL::bigint AS source_id, x.cree_le
FROM (SELECT annulation_vente.cree_le, annulation_vente.montant_rembourse, 'Remboursement carte (annulation)'::text AS libelle
      FROM annulation_vente WHERE annulation_vente.mode = 'carte' AND annulation_vente.montant_rembourse > 0
      UNION ALL
      SELECT retour_client.cree_le, retour_client.montant_rembourse, 'Remboursement carte (retour)'::text AS libelle
      FROM retour_client WHERE retour_client.mode = 'carte' AND retour_client.montant_rembourse > 0) x
JOIN compte_carte_periode h ON x.cree_le::date >= h.du AND (h.au IS NULL OR x.cree_le::date <= h.au);

CREATE OR REPLACE VIEW v_facture_client AS
SELECT f.id, f.type, f.numero, f.date_facture, f.destinataire, f.patient_id, f.client_id, f.organisme_id, f.destinataire_nom,
       f.destinataire_infos, f.periode_du, f.periode_au, f.montant_ht, f.montant_tva, f.montant_ttc, f.deja_paye, f.statut,
       f.version, f.motif_rejet, f.remplace_id, f.remplacee_par, f.facture_origine_id, f.motif, f.hors_suivi, f.motif_hors_suivi,
       f.commentaire, f.utilisateur_id, f.cree_le,
       COALESCE((SELECT sum(a.montant_ttc) FROM facture_client a WHERE a.facture_origine_id = f.id AND a.type = 'avoir'), 0) AS avoirs,
       COALESCE((SELECT sum(r.montant) FROM reglement_facture_client r WHERE r.facture_id = f.id), 0) AS regle,
       CASE
           WHEN f.type = 'avoir' OR f.statut = 'remplacee' THEN 0::numeric
           -- La part d'un encaissement qui a soldé la dette du patient est déjà comptée dans la dette réglée des ventes
           ELSE GREATEST(f.montant_ttc - f.deja_paye
                - COALESCE((SELECT sum(a.montant_ttc) FROM facture_client a WHERE a.facture_origine_id = f.id AND a.type = 'avoir'), 0)
                - COALESCE((SELECT sum(r.montant - r.part_credit) FROM reglement_facture_client r WHERE r.facture_id = f.id), 0)
                - COALESCE((SELECT sum(c.regle) FROM v_vente_credit c
                            WHERE c.vente_id IN (SELECT fl.vente_id FROM facture_client_ligne fl
                                                 WHERE fl.facture_id = f.id AND fl.part = 'patient' AND fl.vente_id IS NOT NULL)), 0), 0)
       END AS reste,
       u.nom_complet AS employe,
       (SELECT x.numero FROM facture_client x WHERE x.id = f.remplace_id) AS remplace_numero,
       (SELECT x.numero FROM facture_client x WHERE x.id = f.remplacee_par) AS remplacee_par_numero,
       (SELECT x.numero FROM facture_client x WHERE x.id = f.facture_origine_id) AS origine_numero
FROM facture_client f
JOIN utilisateur u ON u.id = f.utilisateur_id;

CREATE OR REPLACE VIEW v_suivi_dettes AS
-- Vente à crédit : sauf si elle figure dans une facture suivie qui reste à encaisser (la dette y est déjà comptée)
SELECT 'creance'::text AS sens, 'patient'::text AS categorie, c.patient_id AS tiers_id,
       TRIM(BOTH FROM concat_ws(' ', p.nom, p.prenom)) AS tiers, 'Vente à crédit '::text || c.numero AS libelle,
       c.date_vente::date AS date_reference, NULL::date AS echeance, c.reste AS montant, false AS rejetee
FROM v_vente_credit c
JOIN patient p ON p.id = c.patient_id
WHERE c.reste > 0
  AND NOT EXISTS (SELECT 1 FROM facture_client_ligne fl JOIN v_facture_client vf ON vf.id = fl.facture_id
                  WHERE fl.vente_id = c.vente_id AND fl.part = 'patient' AND vf.type = 'facture' AND vf.statut <> 'remplacee'
                    AND NOT vf.hors_suivi AND vf.reste > 0)
UNION ALL
SELECT 'creance'::text AS sens, 'patient'::text AS categorie, p.id AS tiers_id,
       TRIM(BOTH FROM concat_ws(' ', p.nom, p.prenom)) AS tiers,
       'Dette antérieure (ancien logiciel ou mise en place du relevé)'::text AS libelle,
       COALESCE((SELECT min(m.cree_le) FROM credit_mouvement m WHERE m.patient_id = p.id), now())::date AS date_reference,
       NULL::date AS echeance,
       p.solde_credit - COALESCE((SELECT sum(c.reste) FROM v_vente_credit c WHERE c.patient_id = p.id), 0) AS montant,
       false AS rejetee
FROM patient p
WHERE (p.solde_credit - COALESCE((SELECT sum(c.reste) FROM v_vente_credit c WHERE c.patient_id = p.id), 0)) > 0
UNION ALL
SELECT 'creance'::text AS sens,
       CASE f.destinataire WHEN 'organisme' THEN 'facture_organisme'::text WHEN 'entreprise' THEN 'facture_entreprise'::text
                           ELSE 'facture_patient'::text END AS categorie,
       COALESCE(f.organisme_id, f.client_id, f.patient_id) AS tiers_id, f.destinataire_nom AS tiers,
       'Facture '::text || f.numero AS libelle, f.date_facture AS date_reference, fc.date_echeance AS echeance, f.reste AS montant,
       f.statut = 'rejetee' AS rejetee
FROM v_facture_client f
JOIN facture_client fc ON fc.id = f.id
WHERE f.type = 'facture' AND f.statut <> 'remplacee' AND NOT f.hors_suivi AND f.reste > 0
UNION ALL
SELECT 'creance'::text AS sens, 'bordereau'::text AS categorie, b.organisme_id AS tiers_id, o.nom AS tiers,
       'Bordereau '::text || b.numero AS libelle, COALESCE(b.date_envoi, b.periode_fin) AS date_reference, NULL::date AS echeance,
       b.montant_total - COALESCE((SELECT sum(m.montant) FROM mouvement_tresorerie m WHERE m.bordereau_id = b.id AND m.sens = 'entree'), 0) AS montant,
       b.statut = 'rejet_partiel' AS rejetee
FROM bordereau b
JOIN organisme o ON o.id = b.organisme_id
WHERE b.statut IN ('envoye', 'rejet_partiel')
  AND (b.montant_total - COALESCE((SELECT sum(m.montant) FROM mouvement_tresorerie m WHERE m.bordereau_id = b.id AND m.sens = 'entree'), 0)) > 0
UNION ALL
SELECT 'dette'::text AS sens, 'grossiste'::text AS categorie, ff.fournisseur_id AS tiers_id, ff.fournisseur AS tiers,
       COALESCE('Facture '::text || ff.numero_facture, 'BL '::text || ff.numero_bl, 'Réception'::text) AS libelle,
       ff.date_reception::date AS date_reference, NULL::date AS echeance, ff.reste AS montant, false AS rejetee
FROM v_facture_fournisseur ff
WHERE ff.reste > 0
UNION ALL
SELECT 'dette'::text AS sens, 'grossiste'::text AS categorie, f.id AS tiers_id, f.raison_sociale AS tiers,
       'Ancien solde (hors factures du logiciel)'::text AS libelle,
       COALESCE((SELECT min(m.cree_le) FROM fournisseur_mouvement m WHERE m.fournisseur_id = f.id), now())::date AS date_reference,
       NULL::date AS echeance,
       f.solde - COALESCE((SELECT sum(ff.reste) FROM v_facture_fournisseur ff WHERE ff.fournisseur_id = f.id AND ff.reste > 0), 0) AS montant,
       false AS rejetee
FROM fournisseur f
WHERE (f.solde - COALESCE((SELECT sum(ff.reste) FROM v_facture_fournisseur ff WHERE ff.fournisseur_id = f.id AND ff.reste > 0), 0)) > 0
UNION ALL
-- Traite : jusqu'à son échéance ; sans banque, tant qu'elle n'est pas payée (échue : signalée)
SELECT 'dette'::text AS sens, 'effet'::text AS categorie, g.fournisseur_id AS tiers_id, f.raison_sociale AS tiers,
       'Traite '::text || COALESCE(g.reference, '') || COALESCE(' (' || g.banque || ')', '')
       || CASE WHEN g.echeance < current_date THEN ' · échue, non payée' ELSE '' END AS libelle,
       g.date_reglement AS date_reference, g.echeance, g.montant, false AS rejetee
FROM reglement_fournisseur g
JOIN fournisseur f ON f.id = g.fournisseur_id
WHERE g.moyen = 'effet'
  AND NOT EXISTS (SELECT 1 FROM paiement_effet pe WHERE pe.reglement_id = g.id)
  AND (g.echeance >= current_date OR g.compte_id IS NULL);
