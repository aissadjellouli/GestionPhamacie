-- =====================================================================
-- 121 : reprise de données, historique des factures d'organisme
-- (décision du titulaire, 2026-11-16)
--   * parametre_caisse.reprise_exiger_produits_historique : option du
--     titulaire « exiger le fichier des produits pour importer
--     l'historique des organismes seul » (Chifa et MDN). Par défaut :
--     exigé (comportement du script 114). Le fichier des produits reste
--     toujours exigé avec le stock, les lots, les ventes, les patients,
--     les crédits ou les grossistes (contrôle dans l'application).
--   * historique_chifa devient l'historique des factures d'organisme :
--     colonne organisme (CHIFA = CNAS ou CASNOS non précisé, CNAS,
--     CASNOS, MDN). Les lignes déjà reprises restent « CHIFA ».
--     Vue historique_organisme (nom générique, part_organisme).
--   * import.historique_chifa_src : colonne organisme facultative
--     (CNAS / CASNOS) ; import.historique_mdn_src : transit des
--     factures MDN (conventions militaires).
--     [À VÉRIFIER] format réel des feuilles MDN des anciens logiciels :
--     aucun modèle dans le dépôt, tout reste réglable par le profil de
--     reprise (correspondance des colonnes, formats).
--   * import.controler_historique_chifa / import.charger_historique_chifa
--     remplacées : traitent aussi les factures MDN.
-- Idempotent ; réversible (voir « Retour arrière » en fin de fichier).
-- =====================================================================

-- ---- 1. Option du titulaire ----------------------------------------------
ALTER TABLE parametre_caisse ADD COLUMN IF NOT EXISTS reprise_exiger_produits_historique boolean NOT NULL DEFAULT true;
COMMENT ON COLUMN parametre_caisse.reprise_exiger_produits_historique IS
    'Reprise : fichier des produits exigé pour importer l''historique des organismes seul (Chifa, MDN) ; toujours exigé avec stock, ventes, patients, crédits (121)';

-- ---- 2. Transit ---------------------------------------------------------------
ALTER TABLE import.historique_chifa_src ADD COLUMN IF NOT EXISTS organisme text;

CREATE TABLE IF NOT EXISTS import.historique_mdn_src (
    session_id     bigint NOT NULL REFERENCES import.session(id),
    num_facture    text   NOT NULL,
    date_facture   text   NOT NULL,
    num_affilie    text,
    nom_affilie    text,
    montant_total  text,
    part_mdn       text,
    part_assure    text,
    PRIMARY KEY (session_id, num_facture, date_facture)
);
COMMENT ON TABLE import.historique_mdn_src IS 'Transit des factures MDN (conventions militaires) de l''ancien logiciel (121) ; format à vérifier, réglable par le profil';

-- ---- 3. Historique en lecture seule : organisme ---------------------------------
ALTER TABLE historique_chifa ADD COLUMN IF NOT EXISTS organisme text NOT NULL DEFAULT 'CHIFA';
DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'ck_historique_chifa_organisme') THEN
        ALTER TABLE historique_chifa ADD CONSTRAINT ck_historique_chifa_organisme CHECK (organisme IN ('CHIFA', 'CNAS', 'CASNOS', 'MDN'));
    END IF;
    -- Un même numéro peut exister chez Chifa et au MDN : unicité par organisme
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'ux_historique_organisme_facture') THEN
        ALTER TABLE historique_chifa ADD CONSTRAINT ux_historique_organisme_facture UNIQUE (session_id, organisme, num_facture, date_facture);
    END IF;
    IF EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'ux_historique_chifa_facture') THEN
        ALTER TABLE historique_chifa DROP CONSTRAINT ux_historique_chifa_facture;
    END IF;
END $$;
CREATE INDEX IF NOT EXISTS ix_historique_chifa_organisme ON historique_chifa (organisme, date_facture DESC);
COMMENT ON COLUMN historique_chifa.organisme IS 'CHIFA (CNAS ou CASNOS non précisé), CNAS, CASNOS ou MDN (121)';
COMMENT ON COLUMN historique_chifa.part_chifa IS 'Part de l''organisme (Chifa ou MDN)';
COMMENT ON TABLE historique_chifa IS 'Factures d''organisme passées de l''ancien logiciel (Chifa, MDN ; 114, 121), en lecture seule : aucune vente recréée';

CREATE OR REPLACE VIEW historique_organisme AS
SELECT id, session_id, logiciel_source, organisme, num_facture, date_facture,
       num_assure, nom_assure, montant_total, part_chifa AS part_organisme, part_assure, importe_le
FROM historique_chifa;
COMMENT ON VIEW historique_organisme IS 'Historique des organismes repris (Chifa, MDN), lecture seule (121)';

-- ---- 4. Contrôles (avertissements, jamais bloquants) -----------------------------------
CREATE OR REPLACE FUNCTION import.controler_historique_chifa(p_session bigint) RETURNS integer
LANGUAGE plpgsql AS $$
DECLARE
    n integer;
BEGIN
    INSERT INTO import.anomalie (session_id, entite, ref_source, gravite, message)
    SELECT p_session, h.entite, h.num_facture, 'avertissement', m.message
    FROM (
        SELECT 'historique_chifa' AS entite, 'Chifa' AS libelle, num_facture, date_facture, montant_total, part_chifa AS part_org, part_assure, organisme
        FROM import.historique_chifa_src WHERE session_id = p_session
        UNION ALL
        SELECT 'historique_mdn', 'MDN', num_facture, date_facture, montant_total, part_mdn, part_assure, NULL
        FROM import.historique_mdn_src WHERE session_id = p_session
    ) h
    CROSS JOIN LATERAL (
        SELECT 'Date de facture illisible (« ' || h.date_facture || ' ») : facture non reprise dans l''historique' AS message
        WHERE import.date_src(h.date_facture) IS NULL
        UNION ALL
        SELECT 'Organisme « ' || h.organisme || ' » inconnu (CNAS ou CASNOS attendu) : repris comme Chifa'
        WHERE NULLIF(btrim(h.organisme), '') IS NOT NULL AND upper(btrim(h.organisme)) NOT IN ('CNAS', 'CASNOS', 'CHIFA')
        UNION ALL
        SELECT 'Montant ambigu (« ' || x.v || ' ») : point des milliers ou point décimal ? Choisissez le séparateur décimal du profil'
        FROM unnest(ARRAY[h.montant_total, h.part_org, h.part_assure]) x(v) WHERE import.nombre_ambigu(x.v)
        UNION ALL
        SELECT 'Montant illisible (« ' || x.v || ' ») : repris vide'
        FROM unnest(ARRAY[h.montant_total, h.part_org, h.part_assure]) x(v)
        WHERE NULLIF(btrim(x.v), '') IS NOT NULL AND NOT import.nombre_ambigu(x.v) AND import.nombre(x.v) IS NULL
        UNION ALL
        SELECT 'Total (' || import.nombre(h.montant_total) || ') différent de part ' || h.libelle || ' + part assuré ('
               || (import.nombre(h.part_org) + import.nombre(h.part_assure)) || ')'
        WHERE abs(import.nombre(h.montant_total) - import.nombre(h.part_org) - import.nombre(h.part_assure)) > 0.01
    ) m;
    GET DIAGNOSTICS n = ROW_COUNT;
    RETURN n;
END;
$$;

-- ---- 5. Chargement (dans la transaction de l'application, après import.charger) ----------
CREATE OR REPLACE FUNCTION import.charger_historique_chifa(p_session bigint) RETURNS integer
LANGUAGE plpgsql AS $$
DECLARE
    n integer;
    m integer;
BEGIN
    INSERT INTO historique_chifa (session_id, logiciel_source, organisme, num_facture, date_facture, num_assure, nom_assure,
                                  montant_total, part_chifa, part_assure)
    SELECT h.session_id, s.logiciel_source,
           CASE WHEN upper(btrim(h.organisme)) IN ('CNAS', 'CASNOS') THEN upper(btrim(h.organisme)) ELSE 'CHIFA' END,
           btrim(h.num_facture), import.date_src(h.date_facture),
           NULLIF(btrim(h.num_assure), ''), NULLIF(btrim(h.nom_assure), ''),
           round(import.nombre(h.montant_total), 2), round(import.nombre(h.part_chifa), 2), round(import.nombre(h.part_assure), 2)
    FROM import.historique_chifa_src h
    JOIN import.session s ON s.id = h.session_id
    WHERE h.session_id = p_session AND import.date_src(h.date_facture) IS NOT NULL
    ON CONFLICT ON CONSTRAINT ux_historique_organisme_facture DO NOTHING;
    GET DIAGNOSTICS n = ROW_COUNT;

    INSERT INTO historique_chifa (session_id, logiciel_source, organisme, num_facture, date_facture, num_assure, nom_assure,
                                  montant_total, part_chifa, part_assure)
    SELECT h.session_id, s.logiciel_source, 'MDN', btrim(h.num_facture), import.date_src(h.date_facture),
           NULLIF(btrim(h.num_affilie), ''), NULLIF(btrim(h.nom_affilie), ''),
           round(import.nombre(h.montant_total), 2), round(import.nombre(h.part_mdn), 2), round(import.nombre(h.part_assure), 2)
    FROM import.historique_mdn_src h
    JOIN import.session s ON s.id = h.session_id
    WHERE h.session_id = p_session AND import.date_src(h.date_facture) IS NOT NULL
    ON CONFLICT ON CONSTRAINT ux_historique_organisme_facture DO NOTHING;
    GET DIAGNOSTICS m = ROW_COUNT;
    RETURN n + m;
END;
$$;

-- ---- Retour arrière (manuel, à exécuter seulement sur décision) ------------------------------
-- Les factures MDN déjà chargées doivent d'abord être écartées (table en lecture seule : désactiver le déclencheur).
-- DROP VIEW IF EXISTS historique_organisme;
-- ALTER TABLE historique_chifa ADD CONSTRAINT ux_historique_chifa_facture UNIQUE (session_id, num_facture, date_facture);
-- ALTER TABLE historique_chifa DROP CONSTRAINT ux_historique_organisme_facture, DROP CONSTRAINT ck_historique_chifa_organisme, DROP COLUMN organisme;
-- DROP TABLE import.historique_mdn_src; ALTER TABLE import.historique_chifa_src DROP COLUMN organisme;
-- ALTER TABLE parametre_caisse DROP COLUMN reprise_exiger_produits_historique;
-- puis réexécuter les fonctions de 114.
