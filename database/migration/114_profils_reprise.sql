-- =====================================================================
-- 114 — Reprise des logiciels concurrents : profils de reprise et
-- historique des ventes Chifa (2026-11-16, lot 4, M4a)
--
--   * import.profil_reprise : correspondance de colonnes ENREGISTRÉE et
--     réutilisable pour un logiciel concurrent (nom, version, feuille ou
--     table → colonne de chaque champ de reprise) et transformations
--     simples (séparateur décimal, format de date, codes de classe).
--     Les formats réels de Chifa Plus, XpertPharm, PharmaPro et
--     Profficine sont INCONNUS : quatre modèles VIDES sont livrés,
--     statut « a_completer », à compléter avec un export réel depuis
--     Administration › Profils de reprise. Rien n'est supposé.
--   * import.historique_chifa_src : transit de l'historique des ventes
--     Chifa de l'ancien logiciel (n° de facture, date, assuré, montants),
--     tout en texte comme les autres tables de transit.
--   * historique_chifa : historique EN LECTURE SEULE après chargement
--     (aucune vente, aucun stock, aucune dette recréés). Modification et
--     suppression refusées par déclencheur.
--   * import.controler_historique_chifa : avertissements (date ou montant
--     illisible, total différent de la somme des parts) ; jamais bloquant.
--   * import.charger_historique_chifa : copie des lignes à date lisible,
--     appelée par l'application dans la transaction du chargement.
-- Aucune donnée existante modifiée ni supprimée. Idempotent.
-- À exécuter après 111_integrite_hachage.sql.
-- =====================================================================

-- ---- 1. Profils de reprise ------------------------------------------------
CREATE TABLE IF NOT EXISTS import.profil_reprise (
    id                  bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    logiciel            text        NOT NULL,
    version             text        NOT NULL DEFAULT '',
    statut              text        NOT NULL DEFAULT 'a_completer' CHECK (statut IN ('a_completer', 'verifie')),
    remarque            text,
    -- [{"cible":"produits","table":"Feuil1","colonnes":{"ref_source":"CODE",...}}, ...]
    correspondances     jsonb       NOT NULL DEFAULT '[]'::jsonb,
    separateur_decimal  text        NOT NULL DEFAULT 'auto' CHECK (separateur_decimal IN ('auto', 'virgule', 'point')),
    format_date         text,
    -- {"P":"psychotrope","L1":"liste_i",...}
    codes_classe        jsonb       NOT NULL DEFAULT '{}'::jsonb,
    cree_le             timestamptz NOT NULL DEFAULT now(),
    modifie_le          timestamptz NOT NULL DEFAULT now(),
    modifie_par         text,
    CONSTRAINT profil_reprise_correspondances_tableau CHECK (jsonb_typeof(correspondances) = 'array'),
    CONSTRAINT profil_reprise_codes_objet CHECK (jsonb_typeof(codes_classe) = 'object')
);
CREATE UNIQUE INDEX IF NOT EXISTS ux_profil_reprise_logiciel_version ON import.profil_reprise (lower(logiciel), lower(version));
COMMENT ON TABLE import.profil_reprise IS 'Profil de reprise d''un logiciel concurrent (114) : correspondance de colonnes et transformations ; a_completer = modèle vide, format réel inconnu';

-- Modèles vides : formats réels inconnus, à compléter avec un export réel
INSERT INTO import.profil_reprise (logiciel, version, statut, remarque)
SELECT v.logiciel, '', 'a_completer',
       'Modèle vide livré avec Pharma Officine : le format d''export de ce logiciel n''est pas connu. À compléter avec un export réel (fichier exemple), puis passer le statut à « vérifié ».'
FROM (VALUES ('Chifa Plus'), ('XpertPharm'), ('PharmaPro'), ('Profficine')) v(logiciel)
WHERE NOT EXISTS (SELECT 1 FROM import.profil_reprise p WHERE lower(p.logiciel) = lower(v.logiciel) AND p.version = '');

-- ---- 2. Historique des ventes Chifa : transit ---------------------------------
CREATE TABLE IF NOT EXISTS import.historique_chifa_src (
    session_id     bigint NOT NULL REFERENCES import.session(id),
    num_facture    text   NOT NULL,
    date_facture   text   NOT NULL,
    num_assure     text,
    nom_assure     text,
    montant_total  text,
    part_chifa     text,
    part_assure    text,
    PRIMARY KEY (session_id, num_facture, date_facture)
);

-- ---- 3. Historique des ventes Chifa : lecture seule -------------------------
CREATE TABLE IF NOT EXISTS historique_chifa (
    id               bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    session_id       bigint      NOT NULL REFERENCES import.session(id),
    logiciel_source  text        NOT NULL,
    num_facture      text        NOT NULL,
    date_facture     date        NOT NULL,
    num_assure       text,
    nom_assure       text,
    montant_total    numeric(14,2),
    part_chifa       numeric(14,2),
    part_assure      numeric(14,2),
    importe_le       timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT ux_historique_chifa_facture UNIQUE (session_id, num_facture, date_facture)
);
CREATE INDEX IF NOT EXISTS ix_historique_chifa_date ON historique_chifa (date_facture DESC);
CREATE INDEX IF NOT EXISTS ix_historique_chifa_assure ON historique_chifa (num_assure);
COMMENT ON TABLE historique_chifa IS 'Ventes Chifa passées de l''ancien logiciel (114), en lecture seule : aucune vente recréée';

CREATE OR REPLACE FUNCTION historique_chifa_lecture_seule() RETURNS trigger
LANGUAGE plpgsql AS $$
BEGIN
    RAISE EXCEPTION 'Historique Chifa repris : lecture seule (ni modification ni suppression)';
END;
$$;

DROP TRIGGER IF EXISTS trg_historique_chifa_lecture_seule ON historique_chifa;
CREATE TRIGGER trg_historique_chifa_lecture_seule BEFORE UPDATE OR DELETE ON historique_chifa
    FOR EACH ROW EXECUTE FUNCTION historique_chifa_lecture_seule();

-- ---- 4. Contrôles (après import.controler, qui vide les anomalies de la session) ----
CREATE OR REPLACE FUNCTION import.controler_historique_chifa(p_session bigint) RETURNS integer
LANGUAGE plpgsql AS $$
DECLARE
    n integer;
BEGIN
    INSERT INTO import.anomalie (session_id, entite, ref_source, gravite, message)
    SELECT p_session, 'historique_chifa', h.num_facture, 'avertissement', m.message
    FROM import.historique_chifa_src h
    CROSS JOIN LATERAL (
        SELECT 'Date de facture illisible (« ' || h.date_facture || ' ») : facture non reprise dans l''historique' AS message
        WHERE import.date_src(h.date_facture) IS NULL
        UNION ALL
        SELECT 'Montant ambigu (« ' || x.v || ' ») : point des milliers ou point décimal ? Choisissez le séparateur décimal du profil'
        FROM unnest(ARRAY[h.montant_total, h.part_chifa, h.part_assure]) x(v) WHERE import.nombre_ambigu(x.v)
        UNION ALL
        SELECT 'Montant illisible (« ' || x.v || ' ») : repris vide'
        FROM unnest(ARRAY[h.montant_total, h.part_chifa, h.part_assure]) x(v)
        WHERE NULLIF(btrim(x.v), '') IS NOT NULL AND NOT import.nombre_ambigu(x.v) AND import.nombre(x.v) IS NULL
        UNION ALL
        SELECT 'Total (' || import.nombre(h.montant_total) || ') différent de part Chifa + part assuré ('
               || (import.nombre(h.part_chifa) + import.nombre(h.part_assure)) || ')'
        WHERE abs(import.nombre(h.montant_total) - import.nombre(h.part_chifa) - import.nombre(h.part_assure)) > 0.01
    ) m
    WHERE h.session_id = p_session;
    GET DIAGNOSTICS n = ROW_COUNT;
    RETURN n;
END;
$$;

-- ---- 5. Chargement (dans la transaction de l'application, après import.charger) ----
CREATE OR REPLACE FUNCTION import.charger_historique_chifa(p_session bigint) RETURNS integer
LANGUAGE plpgsql AS $$
DECLARE
    n integer;
BEGIN
    INSERT INTO historique_chifa (session_id, logiciel_source, num_facture, date_facture, num_assure, nom_assure,
                                  montant_total, part_chifa, part_assure)
    SELECT h.session_id, s.logiciel_source, btrim(h.num_facture), import.date_src(h.date_facture),
           NULLIF(btrim(h.num_assure), ''), NULLIF(btrim(h.nom_assure), ''),
           round(import.nombre(h.montant_total), 2), round(import.nombre(h.part_chifa), 2), round(import.nombre(h.part_assure), 2)
    FROM import.historique_chifa_src h
    JOIN import.session s ON s.id = h.session_id
    WHERE h.session_id = p_session AND import.date_src(h.date_facture) IS NOT NULL
    ON CONFLICT ON CONSTRAINT ux_historique_chifa_facture DO NOTHING;
    GET DIAGNOSTICS n = ROW_COUNT;
    RETURN n;
END;
$$;
