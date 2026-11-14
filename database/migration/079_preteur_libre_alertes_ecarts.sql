-- =====================================================================
-- Prêteur hors trésorerie, alertes non parties, écarts de caisse vus,
-- relevés impossibles à vider
--   * pret_bancaire : un prêt peut venir d'un prêteur qui n'est pas un
--     compte de la trésorerie (banque non suivie) : compte_id devient
--     facultatif et preteur (nom saisi librement) s'y ajoute ; exactement
--     l'un des deux (contrainte pret_bancaire_preteur_check, posée avec
--     garde) ; référence unique par prêteur libre (sans tenir compte des
--     majuscules ni des espaces autour du nom). L'argent d'un tel prêt
--     n'entre dans aucun compte suivi : il ne sert que directement
--     (facture, traite, dépense), contrôlé par le logiciel ;
--   * correspondance_echeancier_preteur : colonnes du tableau d'échéances
--     retenues pour un prêteur libre (clé : nom en minuscules, sans
--     espaces autour) ;
--   * v_echeance_pret et v_pret_bancaire : mêmes colonnes, dans le même
--     ordre ; banque = nom du compte, sinon nom du prêteur libre
--     (COALESCE) ; v_dette_financement inchangée (lit v_pret_bancaire) ;
--   * parametre_notification.signaler_sans_destinataire (défaut : oui) :
--     une alerte (ou le rapport du soir) qui ne part faute de destinataire
--     vérifié est signalée au bandeau de l'accueil du titulaire ; les
--     pannes (mise en file impossible, envoi en échec) le sont toujours ;
--   * message_sortant.signale_le : envoi en échec signalé au titulaire
--     (au bout d'un jour d'essais, une seule fois ; les essais continuent
--     jusqu'à 7 jours) ;
--   * ecart_caisse_vu : écart de caisse marqué vu par le titulaire
--     (tuile « Écarts de caisse récents » de son accueil) ;
--   * boite_evenement, credit_mouvement, fournisseur_mouvement : refusaient
--     déjà UPDATE et DELETE ; TRUNCATE est maintenant refusé aussi
--     (déclencheurs d'instruction, fonction interdire_vidage de 076,
--     message propre à chaque table). Aucune procédure du logiciel ne vide
--     ces tables (la restauration remplit une base neuve).
-- Idempotent. A exécuter après 078_financements.sql.
-- =====================================================================

-- ---- Relevés impossibles à vider -------------------------------------------------------------
CREATE OR REPLACE FUNCTION interdire_vidage() RETURNS trigger
LANGUAGE plpgsql AS $$
BEGIN
    RAISE EXCEPTION '% ne peut pas être vidé (TRUNCATE refusé) : ses lignes sont conservées',
        CASE TG_TABLE_NAME
            WHEN 'journal_audit'         THEN 'Le journal des actions'
            WHEN 'registre_psychotrope'  THEN 'Le registre des psychotropes'
            WHEN 'journal_sauvegarde'    THEN 'Le journal des sauvegardes'
            WHEN 'boite_evenement'       THEN 'L''historique des boîtes'
            WHEN 'credit_mouvement'      THEN 'Le relevé de la dette des patients'
            WHEN 'fournisseur_mouvement' THEN 'Le relevé de la dette envers les grossistes'
            ELSE 'La table ' || TG_TABLE_NAME
        END;
END;
$$;

CREATE OR REPLACE TRIGGER trg_boite_evenement_non_vidable
    BEFORE TRUNCATE ON boite_evenement
    FOR EACH STATEMENT EXECUTE FUNCTION interdire_vidage();

CREATE OR REPLACE TRIGGER trg_credit_mouvement_non_vidable
    BEFORE TRUNCATE ON credit_mouvement
    FOR EACH STATEMENT EXECUTE FUNCTION interdire_vidage();

CREATE OR REPLACE TRIGGER trg_fournisseur_mouvement_non_vidable
    BEFORE TRUNCATE ON fournisseur_mouvement
    FOR EACH STATEMENT EXECUTE FUNCTION interdire_vidage();

-- ---- Alertes non parties : option du titulaire, envoi en échec signalé une fois --------------
ALTER TABLE parametre_notification ADD COLUMN IF NOT EXISTS signaler_sans_destinataire boolean NOT NULL DEFAULT true;
COMMENT ON COLUMN parametre_notification.signaler_sans_destinataire IS 'Alerte (ou rapport du soir) partie vers aucun destinataire vérifié : signalée au bandeau de l''accueil du titulaire. Les pannes (mise en file impossible, envoi en échec) le sont toujours';

ALTER TABLE message_sortant ADD COLUMN IF NOT EXISTS signale_le timestamptz;
COMMENT ON COLUMN message_sortant.signale_le IS 'Envoi en échec signalé au titulaire (alerte_non_envoyee) : après un jour d''essais, ou à l''abandon ; une seule fois';

-- ---- Écarts de caisse vus par le titulaire (accueil) -----------------------------------------
CREATE TABLE IF NOT EXISTS ecart_caisse_vu (
    session_id  bigint PRIMARY KEY REFERENCES session_caisse(id),
    vu_le       timestamptz NOT NULL DEFAULT now(),
    vu_par      bigint NOT NULL REFERENCES utilisateur(id)
);
COMMENT ON TABLE ecart_caisse_vu IS 'Écart de caisse (session clôturée avec un écart non nul) marqué vu par le titulaire : il quitte la tuile « Écarts de caisse récents » de son accueil';

-- ---- Prêteur hors trésorerie ------------------------------------------------------------------
ALTER TABLE pret_bancaire ALTER COLUMN compte_id DROP NOT NULL;
ALTER TABLE pret_bancaire ADD COLUMN IF NOT EXISTS preteur text;
COMMENT ON COLUMN pret_bancaire.preteur IS 'Prêteur qui n''est pas un compte de la trésorerie (nom saisi librement) ; exclusif de compte_id';
COMMENT ON TABLE pret_bancaire IS 'Prêt accordé par une banque de la trésorerie (compte_id) ou par un prêteur hors trésorerie (preteur) ; réservé au titulaire';

DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'pret_bancaire_preteur_check' AND conrelid = 'pret_bancaire'::regclass) THEN
        ALTER TABLE pret_bancaire ADD CONSTRAINT pret_bancaire_preteur_check
            CHECK ((compte_id IS NOT NULL AND preteur IS NULL) OR (compte_id IS NULL AND btrim(preteur) <> ''));
    END IF;
END $$;

CREATE UNIQUE INDEX IF NOT EXISTS ux_pret_bancaire_preteur_reference ON pret_bancaire (lower(btrim(preteur)), reference) WHERE preteur IS NOT NULL;

CREATE TABLE IF NOT EXISTS correspondance_echeancier_preteur (
    preteur          text PRIMARY KEY CHECK (preteur = lower(btrim(preteur)) AND preteur <> ''),
    col_date         int NOT NULL CHECK (col_date >= 0),
    col_capital      int CHECK (col_capital >= 0),
    col_interets     int CHECK (col_interets >= 0),
    col_tva          int CHECK (col_tva >= 0),
    col_marge        int CHECK (col_marge >= 0),
    col_montant      int CHECK (col_montant >= 0),
    col_encours      int CHECK (col_encours >= 0),
    lignes_ignorees  int NOT NULL DEFAULT 0 CHECK (lignes_ignorees >= 0),
    utilisateur_id   bigint NOT NULL REFERENCES utilisateur(id),
    modifie_le       timestamptz NOT NULL DEFAULT now()
);
COMMENT ON TABLE correspondance_echeancier_preteur IS 'Colonnes (à partir de 0) du tableau d''échéances d''un prêteur hors trésorerie (nom en minuscules, sans espaces autour), proposées au prochain import';

-- Vues : mêmes colonnes, dans le même ordre ; la banque est le compte de la trésorerie, sinon le prêteur libre
CREATE OR REPLACE VIEW v_echeance_pret AS
SELECT e.id, e.pret_id, p.reference AS pret, p.compte_id AS banque_id, COALESCE(c.nom, p.preteur) AS banque, e.rang, e.date_echeance, e.capital,
       e.interets, e.tva, e.marge, e.montant, e.capital_restant, pe.date_paiement, pe.moyen, pe.compte_id, cp.nom AS compte, pe.reference,
       pe.part_tiroir, u.nom_complet AS employe
FROM echeance_pret e
JOIN pret_bancaire p ON p.id = e.pret_id
LEFT JOIN compte_tresorerie c ON c.id = p.compte_id
LEFT JOIN paiement_echeance_pret pe ON pe.echeance_id = e.id
LEFT JOIN compte_tresorerie cp ON cp.id = pe.compte_id
LEFT JOIN utilisateur u ON u.id = pe.utilisateur_id;

CREATE OR REPLACE VIEW v_pret_bancaire AS
SELECT p.id, p.compte_id, COALESCE(c.nom, p.preteur) AS banque, p.reference, p.objet, p.montant, p.date_pret, p.document,
       COALESCE(e.capital_paye, 0) AS capital_rembourse,
       p.montant - COALESCE(e.capital_paye, 0) AS reste_du,
       COALESCE((SELECT sum(x.montant) FROM utilisation_financement x WHERE x.pret_id = p.id), 0) AS utilise,
       COALESCE(e.nombre, 0)::int AS echeances,
       COALESCE(e.payees, 0)::int AS echeances_payees,
       COALESCE(e.echues, 0)::int AS echues_non_payees,
       (SELECT v.date_echeance FROM v_echeance_pret v WHERE v.pret_id = p.id AND v.date_paiement IS NULL ORDER BY v.rang LIMIT 1) AS prochaine_echeance,
       (SELECT v.montant FROM v_echeance_pret v WHERE v.pret_id = p.id AND v.date_paiement IS NULL ORDER BY v.rang LIMIT 1) AS prochain_montant,
       u.nom_complet AS employe, p.cree_le
FROM pret_bancaire p
LEFT JOIN compte_tresorerie c ON c.id = p.compte_id
JOIN utilisateur u ON u.id = p.utilisateur_id
LEFT JOIN LATERAL (
    SELECT count(*) AS nombre, count(v.date_paiement) AS payees,
           sum(v.capital) FILTER (WHERE v.date_paiement IS NOT NULL) AS capital_paye,
           count(*) FILTER (WHERE v.date_paiement IS NULL AND v.date_echeance < current_date) AS echues
    FROM v_echeance_pret v WHERE v.pret_id = p.id) e ON true;
