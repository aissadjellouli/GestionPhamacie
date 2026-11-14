-- =====================================================================
-- Surveillance de la caisse (lutte contre le vol), réservée au titulaire
--   * evenement_caisse : ce qui se passe au comptoir sans laisser de vente :
--     ligne retirée du panier (boîtes, montant), tiroir ouvert sans vente,
--     vente en attente abandonnée ; employé, poste et caisse ;
--   * parametre_surveillance : seuils par employé et par jour, horaires
--     d'ouverture (vides : pas de contrôle des ventes hors horaires) ;
--   * alerte_surveillance : anomalies par employé et par jour (ou par
--     patient pour les remises répétées), visibles du titulaire seul,
--     closes par lui (« traitée ») ; jamais supprimées ;
--   * surveiller(jour) : calcule les anomalies du jour, crée celles qui
--     manquent et renvoie les nouvelles (à notifier) ; appelée après chaque
--     vente et chaque événement de caisse.
--   Horaires : comparés à l'heure locale du serveur PostgreSQL (réglage
--   timezone, à mettre sur l'heure de la pharmacie).
-- Idempotent. A exécuter après 053_lot_delaisse.sql.
-- =====================================================================

-- 1. Événements de caisse
CREATE TABLE IF NOT EXISTS evenement_caisse (
    id                bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    type              text NOT NULL,
    utilisateur_id    bigint NOT NULL REFERENCES utilisateur (id),
    poste             text,
    session_caisse_id bigint REFERENCES session_caisse (id),
    produit_id        bigint REFERENCES produit (id),
    lot_id            bigint REFERENCES lot (id),
    quantite          integer,
    montant           numeric(12,2),
    detail            text,
    cree_le           timestamptz NOT NULL DEFAULT now()
);
DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'ck_evenement_caisse_type') THEN
        ALTER TABLE evenement_caisse ADD CONSTRAINT ck_evenement_caisse_type
            CHECK (type IN ('ligne_retiree', 'tiroir_ouvert', 'attente_abandonnee'));
    END IF;
END $$;
CREATE INDEX IF NOT EXISTS ix_evenement_caisse_employe ON evenement_caisse (utilisateur_id, cree_le);
COMMENT ON TABLE evenement_caisse IS 'Comptoir sans vente : ligne retirée du panier, tiroir ouvert sans vente, vente en attente abandonnée';

-- 2. Réglages
CREATE TABLE IF NOT EXISTS parametre_surveillance (
    id                          integer PRIMARY KEY DEFAULT 1 CHECK (id = 1),
    seuil_lignes_retirees       integer NOT NULL DEFAULT 5 CHECK (seuil_lignes_retirees >= 1),
    seuil_tiroir                integer NOT NULL DEFAULT 3 CHECK (seuil_tiroir >= 1),
    seuil_attentes_abandonnees  integer NOT NULL DEFAULT 3 CHECK (seuil_attentes_abandonnees >= 1),
    seuil_remises               integer NOT NULL DEFAULT 5 CHECK (seuil_remises >= 1),
    seuil_remises_patient       integer NOT NULL DEFAULT 3 CHECK (seuil_remises_patient >= 1),
    heure_ouverture             time,
    heure_fermeture             time,
    CONSTRAINT ck_parametre_surveillance_horaires CHECK ((heure_ouverture IS NULL) = (heure_fermeture IS NULL))
);
INSERT INTO parametre_surveillance DEFAULT VALUES ON CONFLICT (id) DO NOTHING;
COMMENT ON TABLE parametre_surveillance IS 'Seuils de la surveillance de la caisse (par employé et par jour) et horaires d''ouverture (vides : pas de contrôle)';

-- 3. Anomalies (titulaire seul)
CREATE TABLE IF NOT EXISTS alerte_surveillance (
    id              bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    type            text NOT NULL,
    niveau          text NOT NULL CHECK (niveau IN ('attention', 'urgent')),
    utilisateur_id  bigint REFERENCES utilisateur (id),
    jour            date NOT NULL,
    cle             text NOT NULL DEFAULT '',
    message         text NOT NULL,
    valeur          numeric(12,2),
    statut          text NOT NULL DEFAULT 'nouvelle' CHECK (statut IN ('nouvelle', 'traitee')),
    action_prise    text,
    cree_le         timestamptz NOT NULL DEFAULT now(),
    mise_a_jour_le  timestamptz NOT NULL DEFAULT now(),
    traitee_par     bigint REFERENCES utilisateur (id),
    traitee_le      timestamptz
);
DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'ck_alerte_surveillance_type') THEN
        ALTER TABLE alerte_surveillance ADD CONSTRAINT ck_alerte_surveillance_type
            CHECK (type IN ('lignes_retirees', 'tiroir', 'attentes_abandonnees', 'hors_horaires', 'remises', 'remises_patient'));
    END IF;
END $$;
CREATE UNIQUE INDEX IF NOT EXISTS ux_alerte_surveillance ON alerte_surveillance (type, COALESCE(utilisateur_id, 0), jour, cle);
COMMENT ON TABLE alerte_surveillance IS 'Anomalies de caisse par employé et par jour, visibles du titulaire seul ; closes par lui, jamais supprimées';

-- 4. Notification au titulaire
-- Rejouable sur une base plus récente : la liste n'est refaite que si elle n'a pas encore « surveillance »
-- Les destinataires des alertes de lot délaissé reçoivent aussi celle-ci : une seule fois, quand le type est ajouté
-- (un rejeu ne recoche pas le type retiré ensuite par le titulaire)
DO $garde$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'destinataire_notification_types_check' AND pg_get_constraintdef(oid) LIKE '%''surveillance''%') THEN
        ALTER TABLE destinataire_notification DROP CONSTRAINT IF EXISTS destinataire_notification_types_check;
        ALTER TABLE destinataire_notification ADD CONSTRAINT destinataire_notification_types_check
            CHECK (types <@ ARRAY['rapport_quotidien', 'ecart_caisse', 'lot_bloque', 'sauvegarde', 'ecart_inventaire', 'lot_delaisse', 'surveillance']);
        UPDATE destinataire_notification SET types = types || 'surveillance'::text
        WHERE 'lot_delaisse' = ANY (types) AND NOT 'surveillance' = ANY (types);
    END IF;
END $garde$;
-- Rejouable sur une base plus récente : la liste n'est refaite que si elle n'a pas encore « surveillance »
DO $garde$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'message_sortant_type_check' AND pg_get_constraintdef(oid) LIKE '%''surveillance''%') THEN
        ALTER TABLE message_sortant DROP CONSTRAINT IF EXISTS message_sortant_type_check;
        ALTER TABLE message_sortant ADD CONSTRAINT message_sortant_type_check
            CHECK (type = ANY (ARRAY['rapport_quotidien', 'ecart_caisse', 'lot_bloque', 'sauvegarde', 'ecart_inventaire', 'lot_delaisse', 'surveillance', 'test']));
    END IF;
END $garde$;

-- 5. Anomalies d'un jour (candidates). o_notification : texte sans donnée de patient (SMS, e-mail)
CREATE OR REPLACE FUNCTION surveillance_candidats(p_jour date)
RETURNS TABLE (o_type text, o_niveau text, o_utilisateur_id bigint, o_jour date, o_cle text, o_message text, o_notification text, o_valeur numeric)
LANGUAGE sql STABLE AS $$
    WITH p AS (SELECT * FROM parametre_surveillance WHERE id = 1),
    ev AS (
        SELECT e.* FROM evenement_caisse e
        WHERE e.cree_le >= p_jour AND e.cree_le < p_jour + 1
    ),
    ventes AS (
        SELECT v.* FROM vente v
        WHERE v.statut = 'validee' AND v.date_vente >= p_jour AND v.date_vente < p_jour + 1
    ),
    -- Boîtes retirées du panier avant le paiement
    retraits AS (
        SELECT ev.utilisateur_id, sum(COALESCE(ev.quantite, 1))::int AS boites, sum(COALESCE(ev.montant, 0)) AS montant, count(*) AS lignes
        FROM ev WHERE ev.type = 'ligne_retiree' GROUP BY ev.utilisateur_id
    ),
    tiroirs AS (
        SELECT ev.utilisateur_id, count(*)::int AS n FROM ev WHERE ev.type = 'tiroir_ouvert' GROUP BY ev.utilisateur_id
    ),
    attentes AS (
        SELECT ev.utilisateur_id, count(*)::int AS n, sum(COALESCE(ev.montant, 0)) AS montant
        FROM ev WHERE ev.type = 'attente_abandonnee' GROUP BY ev.utilisateur_id
    ),
    hors AS (
        SELECT v.utilisateur_id, count(*)::int AS n, sum(v.montant_total) AS montant,
               min(v.date_vente)::time(0) AS premiere, max(v.date_vente)::time(0) AS derniere
        FROM ventes v, p
        WHERE p.heure_ouverture IS NOT NULL
          AND (v.date_vente::time < p.heure_ouverture OR v.date_vente::time >= p.heure_fermeture)
        GROUP BY v.utilisateur_id
    ),
    remises AS (
        SELECT v.utilisateur_id, count(*)::int AS n, sum(v.montant_remise) AS montant
        FROM ventes v WHERE v.remise_pct > 0 GROUP BY v.utilisateur_id
    ),
    -- Remises au même patient sur les 30 jours qui précèdent (une anomalie par patient et par mois)
    remises_patient AS (
        SELECT v.patient_id, count(*)::int AS n, sum(v.montant_remise) AS montant,
               string_agg(DISTINCT u.nom_complet, ', ') AS vendeurs
        FROM vente v JOIN utilisateur u ON u.id = v.utilisateur_id
        WHERE v.statut = 'validee' AND v.remise_pct > 0 AND v.patient_id IS NOT NULL
          AND v.date_vente >= p_jour - 29 AND v.date_vente < p_jour + 1
        GROUP BY v.patient_id
    )
    SELECT 'lignes_retirees', CASE WHEN r.boites >= 2 * p.seuil_lignes_retirees THEN 'urgent' ELSE 'attention' END,
           r.utilisateur_id, p_jour, '',
           format('%s a retiré du panier %s boîte(s) (%s ligne(s), %s DA) avant le paiement le %s.', u.nom_complet, r.boites, r.lignes,
                  to_char(r.montant, 'FM999G999G990D00'), to_char(p_jour, 'DD/MM/YYYY')),
           format('%s : %s boite(s) retiree(s) du panier avant paiement le %s.', u.nom_complet, r.boites, to_char(p_jour, 'DD/MM')),
           r.montant
    FROM retraits r JOIN utilisateur u ON u.id = r.utilisateur_id, p WHERE r.boites >= p.seuil_lignes_retirees
    UNION ALL
    SELECT 'tiroir', CASE WHEN t.n >= 2 * p.seuil_tiroir THEN 'urgent' ELSE 'attention' END, t.utilisateur_id, p_jour, '',
           format('%s a ouvert le tiroir-caisse sans vente %s fois le %s.', u.nom_complet, t.n, to_char(p_jour, 'DD/MM/YYYY')),
           format('%s : tiroir ouvert sans vente %s fois le %s.', u.nom_complet, t.n, to_char(p_jour, 'DD/MM')),
           t.n
    FROM tiroirs t JOIN utilisateur u ON u.id = t.utilisateur_id, p WHERE t.n >= p.seuil_tiroir
    UNION ALL
    SELECT 'attentes_abandonnees', CASE WHEN a.n >= 2 * p.seuil_attentes_abandonnees THEN 'urgent' ELSE 'attention' END, a.utilisateur_id, p_jour, '',
           format('%s a abandonné %s vente(s) en attente (%s DA) le %s.', u.nom_complet, a.n, to_char(a.montant, 'FM999G999G990D00'), to_char(p_jour, 'DD/MM/YYYY')),
           format('%s : %s vente(s) en attente abandonnee(s) le %s.', u.nom_complet, a.n, to_char(p_jour, 'DD/MM')),
           a.montant
    FROM attentes a JOIN utilisateur u ON u.id = a.utilisateur_id, p WHERE a.n >= p.seuil_attentes_abandonnees
    UNION ALL
    SELECT 'hors_horaires', 'urgent', h.utilisateur_id, p_jour, '',
           format('%s a encaissé %s vente(s) (%s DA) hors des horaires d''ouverture le %s (de %s à %s).', u.nom_complet, h.n,
                  to_char(h.montant, 'FM999G999G990D00'), to_char(p_jour, 'DD/MM/YYYY'), h.premiere, h.derniere),
           format('%s : %s vente(s) hors horaires le %s.', u.nom_complet, h.n, to_char(p_jour, 'DD/MM')),
           h.montant
    FROM hors h JOIN utilisateur u ON u.id = h.utilisateur_id
    UNION ALL
    SELECT 'remises', CASE WHEN r.n >= 2 * p.seuil_remises THEN 'urgent' ELSE 'attention' END, r.utilisateur_id, p_jour, '',
           format('%s a accordé une remise sur %s vente(s) (%s DA de remise) le %s.', u.nom_complet, r.n, to_char(r.montant, 'FM999G999G990D00'), to_char(p_jour, 'DD/MM/YYYY')),
           format('%s : remise sur %s vente(s) le %s.', u.nom_complet, r.n, to_char(p_jour, 'DD/MM')),
           r.montant
    FROM remises r JOIN utilisateur u ON u.id = r.utilisateur_id, p WHERE r.n >= p.seuil_remises
    UNION ALL
    SELECT 'remises_patient', CASE WHEN rp.n >= 2 * p.seuil_remises_patient THEN 'urgent' ELSE 'attention' END, NULL::bigint,
           date_trunc('month', p_jour)::date, 'patient:' || rp.patient_id,
           format('%s %s : %s vente(s) avec remise en 30 jours (%s DA de remise), accordées par %s.',
                  pt.nom, COALESCE(pt.prenom, ''), rp.n, to_char(rp.montant, 'FM999G999G990D00'), rp.vendeurs),
           format('Remises repetees au meme patient : %s ventes en 30 jours (detail dans Surveillance).', rp.n),
           rp.montant
    FROM remises_patient rp JOIN patient pt ON pt.id = rp.patient_id, p WHERE rp.n >= p.seuil_remises_patient;
$$;

-- 6. Calcul du jour : nouvelles anomalies créées (et renvoyées pour notification), existantes mises à jour
CREATE OR REPLACE FUNCTION surveiller(p_jour date)
RETURNS TABLE (o_id bigint, o_niveau text, o_notification text)
LANGUAGE plpgsql AS $$
DECLARE
    c        record;
    v_id     bigint;
    v_ancien text;
BEGIN
    FOR c IN SELECT * FROM surveillance_candidats(p_jour) LOOP
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
