-- =====================================================================
-- Réception à l'aveugle et retours sans avoir (lutte contre le vol)
--   * réception en deux étapes (réglage parametre_surveillance.reception_aveugle,
--     actif par défaut) : comptage des boîtes sans voir le bon de livraison
--     (comptage_reception), puis saisie de la facture par une autre personne ;
--   * écart entre le compté et la facture : réception bloquée jusqu'à la
--     validation du titulaire ou d'un employé qui a le droit
--     « achats.valider_ecart » ; le stock prend le compté, la dette suit la
--     facture : boîtes manquantes sorties du stock (mouvement et boîte
--     « manquant_reception » / « manquante », à réclamer au grossiste) ;
--   * anomalies au titulaire : écart de réception, comptage et facture faits
--     par la même personne, retour envoyé sans avoir après
--     parametre_surveillance.jours_avoir_retour jours (30), avoir inférieur
--     au montant attendu.
-- Idempotent. A exécuter après 056_droit_don.sql.
-- =====================================================================

-- 1. Réglages
ALTER TABLE parametre_surveillance ADD COLUMN IF NOT EXISTS reception_aveugle boolean NOT NULL DEFAULT true;
ALTER TABLE parametre_surveillance ADD COLUMN IF NOT EXISTS jours_avoir_retour integer NOT NULL DEFAULT 30;
DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'ck_parametre_surveillance_achats') THEN
        ALTER TABLE parametre_surveillance ADD CONSTRAINT ck_parametre_surveillance_achats CHECK (jours_avoir_retour BETWEEN 1 AND 365);
    END IF;
END $$;
COMMENT ON COLUMN parametre_surveillance.reception_aveugle IS 'Réception en deux étapes : comptage sans voir le BL, puis facture saisie et comparée';
COMMENT ON COLUMN parametre_surveillance.jours_avoir_retour IS 'Retour fournisseur envoyé sans avoir au-delà de ce délai (jours) : anomalie';

-- 2. Comptage d'une livraison (première étape)
CREATE TABLE IF NOT EXISTS comptage_reception (
    id                  bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    fournisseur_id      bigint      NOT NULL REFERENCES fournisseur (id),
    numero_bl           text        NOT NULL,
    statut              text        NOT NULL DEFAULT 'compte'
                        CHECK (statut IN ('compte', 'ecart', 'receptionne', 'a_recompter', 'annule')),
    compte_par          bigint      NOT NULL REFERENCES utilisateur (id),
    compte_le           timestamptz NOT NULL DEFAULT now(),
    facture_par         bigint      REFERENCES utilisateur (id),
    facture_le          timestamptz,
    saisie              jsonb,
    ecarts              jsonb,
    boites_manquantes   integer     NOT NULL DEFAULT 0 CHECK (boites_manquantes >= 0),
    boites_en_trop      integer     NOT NULL DEFAULT 0 CHECK (boites_en_trop >= 0),
    valeur_manquante    numeric(12,2) NOT NULL DEFAULT 0 CHECK (valeur_manquante >= 0),
    decide_par          bigint      REFERENCES utilisateur (id),
    decide_le           timestamptz,
    reception_id        bigint      REFERENCES reception (id)
);
COMMENT ON TABLE comptage_reception IS 'Réception à l''aveugle : boîtes comptées sans voir le BL ; la facture saisie ensuite est comparée au compté';
COMMENT ON COLUMN comptage_reception.saisie IS 'Facture saisie en attente de validation (écart) ; jamais de code comptoir';
COMMENT ON COLUMN comptage_reception.ecarts IS 'Lignes en écart : produit, lot, facturé, compté';
CREATE INDEX IF NOT EXISTS ix_comptage_reception_statut ON comptage_reception (statut);

CREATE TABLE IF NOT EXISTS comptage_reception_ligne (
    id              bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    comptage_id     bigint  NOT NULL REFERENCES comptage_reception (id) ON DELETE CASCADE,
    produit_id      bigint  NOT NULL REFERENCES produit (id),
    numero_lot      text    NOT NULL,
    date_peremption date    NOT NULL,
    gtin            text,
    quantite        integer NOT NULL CHECK (quantite BETWEEN 1 AND 10000),
    numeros_serie   text[]  NOT NULL DEFAULT '{}',
    scans_mobile    bigint[] NOT NULL DEFAULT '{}'
);
CREATE INDEX IF NOT EXISTS ix_comptage_reception_ligne ON comptage_reception_ligne (comptage_id);

-- 3. Boîtes manquantes à la réception (facturées, non livrées)
ALTER TABLE boite DROP CONSTRAINT IF EXISTS boite_statut_check;
ALTER TABLE boite ADD CONSTRAINT boite_statut_check CHECK (statut = ANY (ARRAY['en_stock', 'vendue', 'retournee_fournisseur', 'detruite',
    'retour_client', 'donnee', 'manquante']));

ALTER TABLE boite_evenement DROP CONSTRAINT IF EXISTS boite_evenement_type_check;
ALTER TABLE boite_evenement ADD CONSTRAINT boite_evenement_type_check CHECK (type = ANY (ARRAY['reception', 'vente', 'retour_client',
    'retour_fournisseur', 'destruction', 'blocage', 'correction', 'inventaire', 'echantillon', 'manquant_reception']));

ALTER TABLE mouvement_stock DROP CONSTRAINT IF EXISTS mouvement_stock_type_check;
ALTER TABLE mouvement_stock ADD CONSTRAINT mouvement_stock_type_check CHECK (type = ANY (ARRAY['entree', 'vente', 'retour_client',
    'retour_fournisseur', 'perime', 'casse', 'ajustement_inventaire', 'destruction', 'echantillon', 'manquant_reception']));

-- Registre des psychotropes : une boîte facturée mais non livrée est un écart en moins
CREATE OR REPLACE FUNCTION inscrire_mouvement_registre() RETURNS trigger
LANGUAGE plpgsql AS $$
DECLARE
    v_type  text;
    v_solde int;
BEGIN
    IF NEW.type = 'vente' OR NEW.quantite = 0 OR NOT coalesce(est_sous_registre(NEW.lot_id), false) THEN
        RETURN NEW;
    END IF;
    IF NEW.utilisateur_id IS NULL THEN
        RAISE EXCEPTION 'Mouvement d''un produit sous registre sans employé : inscription au registre impossible';
    END IF;
    v_type := CASE NEW.type
        WHEN 'entree'                THEN 'entree'
        WHEN 'retour_client'         THEN 'retour_client'
        WHEN 'retour_fournisseur'    THEN CASE WHEN NEW.quantite < 0 THEN 'retour_fournisseur' ELSE 'retour_refuse' END
        WHEN 'perime'                THEN 'destruction'
        WHEN 'destruction'           THEN 'destruction'
        WHEN 'casse'                 THEN 'casse'
        WHEN 'ajustement_inventaire' THEN CASE WHEN NEW.quantite < 0 THEN 'ecart_moins' ELSE 'ecart_plus' END
        WHEN 'manquant_reception'    THEN 'ecart_moins'
    END;
    IF v_type IS NULL THEN
        RAISE EXCEPTION 'Type de mouvement % non prévu au registre des psychotropes', NEW.type;
    END IF;
    SELECT greatest(quantite_disponible, 0) INTO v_solde FROM lot WHERE id = NEW.lot_id;
    INSERT INTO registre_psychotrope (lot_id, mouvement_id, type_mouvement, quantite, solde_apres, utilisateur_id, reference, cree_le)
    VALUES (NEW.lot_id, NEW.id, v_type, abs(NEW.quantite), coalesce(v_solde, 0), NEW.utilisateur_id, NEW.reference, NEW.cree_le);
    RETURN NEW;
END $$;

-- 4. Droit de valider un écart de réception (posé à la première exécution seulement)
DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM permission WHERE code = 'achats.valider_ecart') THEN
        INSERT INTO permission (code, module, libelle, sensible)
        VALUES ('achats.valider_ecart', 'Achats', 'Valider un écart entre le comptage et la facture d''une livraison', true);

        ALTER TABLE role_permission DISABLE TRIGGER trg_proteger_role_systeme;
        INSERT INTO role_permission (role_id, permission_code)
        SELECT r.id, 'achats.valider_ecart' FROM role r WHERE r.code IN ('titulaire', 'assistant')
        ON CONFLICT DO NOTHING;
        ALTER TABLE role_permission ENABLE TRIGGER trg_proteger_role_systeme;
    END IF;
END $$;

-- 5. Types d'anomalie
ALTER TABLE alerte_surveillance DROP CONSTRAINT IF EXISTS ck_alerte_surveillance_type;
ALTER TABLE alerte_surveillance ADD CONSTRAINT ck_alerte_surveillance_type CHECK (type IN ('lignes_retirees', 'tiroir', 'attentes_abandonnees',
    'hors_horaires', 'remises', 'remises_patient', 'ajustements', 'casses', 'echantillons', 'boites_fantomes', 'sensibles_en_retard',
    'ecart_reception', 'reception_meme_personne', 'retour_sans_avoir', 'avoir_incomplet'));

-- 6. Anomalies des achats
CREATE OR REPLACE FUNCTION surveillance_candidats_achats(p_jour date)
RETURNS TABLE (o_type text, o_niveau text, o_utilisateur_id bigint, o_jour date, o_cle text, o_message text, o_notification text, o_valeur numeric)
LANGUAGE sql STABLE AS $$
    WITH p AS (SELECT * FROM parametre_surveillance WHERE id = 1),
    -- Écart entre le comptage et la facture, constaté ce jour : l'anomalie nomme celui qui a compté
    ecarts AS (
        SELECT c.*, f.raison_sociale, uc.nom_complet AS compteur, uf.nom_complet AS factureur
        FROM comptage_reception c
        JOIN fournisseur f ON f.id = c.fournisseur_id
        JOIN utilisateur uc ON uc.id = c.compte_par
        JOIN utilisateur uf ON uf.id = c.facture_par
        WHERE c.ecarts IS NOT NULL AND c.facture_le >= p_jour AND c.facture_le < p_jour + 1
    ),
    -- Même personne au comptage et à la saisie de la facture
    meme AS (
        SELECT c.compte_par AS utilisateur_id, count(*)::int AS n, string_agg(c.numero_bl, ', ' ORDER BY c.id) AS bls
        FROM comptage_reception c
        WHERE c.facture_par = c.compte_par AND c.facture_le >= p_jour AND c.facture_le < p_jour + 1
        GROUP BY c.compte_par
    ),
    -- Retour envoyé sans avoir au-delà du délai
    sans_avoir AS (
        SELECT r.*, f.raison_sociale, (p_jour - r.envoye_le::date) AS jours
        FROM retour_fournisseur r JOIN fournisseur f ON f.id = r.fournisseur_id, p
        WHERE r.statut = 'envoye' AND r.envoye_le IS NOT NULL AND r.envoye_le::date <= p_jour - p.jours_avoir_retour
    ),
    -- Avoir reçu ce jour, inférieur au montant attendu
    incomplets AS (
        SELECT r.*, f.raison_sociale
        FROM retour_fournisseur r JOIN fournisseur f ON f.id = r.fournisseur_id
        WHERE r.statut = 'avoir_recu' AND r.avoir_recu_le >= p_jour AND r.avoir_recu_le < p_jour + 1
          AND r.montant_attendu IS NOT NULL AND COALESCE(r.montant_avoir, 0) < r.montant_attendu - 0.01
    )
    SELECT 'ecart_reception', CASE WHEN e.boites_manquantes > 0 THEN 'urgent' ELSE 'attention' END, e.compte_par, e.facture_le::date,
           'comptage:' || e.id,
           format('Livraison BL %s de %s : écart entre le comptage de %s et la facture saisie par %s : %s boîte(s) manquante(s) (%s DA facturés), %s boîte(s) en trop. Réception bloquée jusqu''à validation (Achats, Réceptions).',
                  e.numero_bl, e.raison_sociale, e.compteur, e.factureur, e.boites_manquantes,
                  to_char(e.valeur_manquante, 'FM999G999G990D00'), e.boites_en_trop),
           format('Ecart de reception BL %s (%s) : %s boite(s) manquante(s), %s en trop, comptage de %s.', e.numero_bl, e.raison_sociale,
                  e.boites_manquantes, e.boites_en_trop, e.compteur),
           e.valeur_manquante
    FROM ecarts e
    UNION ALL
    SELECT 'reception_meme_personne', 'attention', m.utilisateur_id, p_jour, '',
           format('%s a compté les boîtes puis saisi la facture de %s livraison(s) le %s (BL %s) : la réception à l''aveugle demande deux personnes.',
                  u.nom_complet, m.n, to_char(p_jour, 'DD/MM/YYYY'), m.bls),
           format('%s : comptage et facture de %s livraison(s) faits seul le %s.', u.nom_complet, m.n, to_char(p_jour, 'DD/MM')),
           m.n
    FROM meme m JOIN utilisateur u ON u.id = m.utilisateur_id
    UNION ALL
    SELECT 'retour_sans_avoir', CASE WHEN s.jours >= 2 * p.jours_avoir_retour THEN 'urgent' ELSE 'attention' END,
           s.utilisateur_id, s.envoye_le::date, 'retour:' || s.id,
           format('Retour %s envoyé à %s le %s (%s DA attendus) : avoir toujours pas reçu après %s jours. Relancez le grossiste.',
                  s.numero, s.raison_sociale, to_char(s.envoye_le, 'DD/MM/YYYY'), to_char(COALESCE(s.montant_attendu, 0), 'FM999G999G990D00'), s.jours),
           format('Retour %s (%s) sans avoir depuis %s jours.', s.numero, s.raison_sociale, s.jours),
           s.montant_attendu
    FROM sans_avoir s, p
    UNION ALL
    SELECT 'avoir_incomplet', 'attention', i.utilisateur_id, i.avoir_recu_le::date, 'retour:' || i.id,
           format('Retour %s à %s : avoir de %s DA pour %s DA attendus (%s DA manquants).', i.numero, i.raison_sociale,
                  to_char(COALESCE(i.montant_avoir, 0), 'FM999G999G990D00'), to_char(i.montant_attendu, 'FM999G999G990D00'),
                  to_char(i.montant_attendu - COALESCE(i.montant_avoir, 0), 'FM999G999G990D00')),
           format('Avoir incomplet pour le retour %s (%s).', i.numero, i.raison_sociale),
           i.montant_attendu - COALESCE(i.montant_avoir, 0)
    FROM incomplets i;
$$;

-- 7. Calcul des anomalies : caisse et stock (055), puis achats
CREATE OR REPLACE FUNCTION surveiller(p_jour date) RETURNS TABLE(o_id bigint, o_niveau text, o_notification text)
LANGUAGE plpgsql AS $$
DECLARE
    c        record;
    v_id     bigint;
    v_ancien text;
BEGIN
    FOR c IN SELECT * FROM surveillance_candidats(p_jour) UNION ALL SELECT * FROM surveillance_candidats_achats(p_jour) LOOP
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
