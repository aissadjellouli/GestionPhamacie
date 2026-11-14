-- =====================================================================
-- Alertes qui n'ont pas pu partir ; doublons selon les majuscules
--   * alerte_non_envoyee : alerte au titulaire qui n'a pas pu partir
--     (mise en file impossible, aucun destinataire vérifié et actif ne
--     reçoit ce type, ou envoi abandonné après une semaine d'essais).
--     L'opération n'est jamais bloquée ; l'erreur est aussi inscrite au
--     journal du serveur. Le titulaire voit un bandeau à l'Accueil
--     (« N alerte(s) n'ont pas pu être envoyées ») et les marque vues
--     (vue_le, vue_par). Aucune donnée de patient : objet de l'alerte et
--     raison de l'échec seulement ;
--   * client facturé et compte de trésorerie : nom unique sans tenir
--     compte des majuscules ni des espaces autour (lower(btrim())). Le
--     logiciel refuse le doublon avec un message clair ; l'index couvre
--     deux postes simultanés. Index créé seulement si la base n'a aucun
--     doublon (sinon un avis est affiché et rien n'est créé : doublons à
--     corriger d'abord, puis rejouer ce script).
-- Idempotent. A exécuter après 076_….sql.
-- =====================================================================

-- ---- Alertes non envoyées (bandeau du titulaire) --------------------------------
CREATE TABLE IF NOT EXISTS alerte_non_envoyee (
    id        bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    type      text        NOT NULL,          -- type de notification (ecart_caisse, lot_bloque, sauvegarde…)
    objet     text        NOT NULL,
    raison    text        NOT NULL,
    cree_le   timestamptz NOT NULL DEFAULT now(),
    vue_le    timestamptz,
    vue_par   bigint      REFERENCES utilisateur(id),
    CHECK ((vue_le IS NULL) = (vue_par IS NULL))
);
COMMENT ON TABLE alerte_non_envoyee IS 'Alerte au titulaire qui n''a pas pu partir (mise en file impossible, aucun destinataire, envoi abandonné) : bandeau de l''Accueil du titulaire jusqu''à ce qu''il la marque vue';
CREATE INDEX IF NOT EXISTS idx_alerte_non_envoyee_non_vue ON alerte_non_envoyee (id) WHERE vue_le IS NULL;

-- ---- Noms uniques sans tenir compte des majuscules -------------------------------
DO $$
BEGIN
    IF to_regclass('ux_client_facture_raison_sociale_ci') IS NULL THEN
        IF EXISTS (SELECT 1 FROM client_facture GROUP BY lower(btrim(raison_sociale)) HAVING count(*) > 1) THEN
            RAISE NOTICE 'client_facture : raisons sociales en double (majuscules ou espaces) : index ux_client_facture_raison_sociale_ci non créé. Corrigez les doublons puis rejouez ce script.';
        ELSE
            CREATE UNIQUE INDEX ux_client_facture_raison_sociale_ci ON client_facture (lower(btrim(raison_sociale)));
        END IF;
    END IF;

    IF to_regclass('ux_compte_tresorerie_nom_ci') IS NULL THEN
        IF EXISTS (SELECT 1 FROM compte_tresorerie GROUP BY lower(btrim(nom)) HAVING count(*) > 1) THEN
            RAISE NOTICE 'compte_tresorerie : noms en double (majuscules ou espaces) : index ux_compte_tresorerie_nom_ci non créé. Corrigez les doublons puis rejouez ce script.';
        ELSE
            CREATE UNIQUE INDEX ux_compte_tresorerie_nom_ci ON compte_tresorerie (lower(btrim(nom)));
        END IF;
    END IF;
END $$;
