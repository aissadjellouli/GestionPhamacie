-- =====================================================================
-- 164 -- Bascule du 2e serveur : saut des compteurs à la promotion (lot 7)
--   Plan : docs/taches/2026-11-23-bascule-2e-serveur/plan.md (§ 3 et décision Q13 du titulaire, 2026-11-23).
--   Quand le 2e serveur est promu, l'ancien principal peut continuer d'écrire de son côté (coupure du réseau, faux arrêt) : pour
--   qu'aucun n° visible ne puisse exister sur les deux serveurs, chaque compteur SAUTE une réserve avant toute écriture du nouveau
--   principal. Réserves (décision Q13) : tickets « T- » +100 000 ; factures +1 000 ; avoirs +1 000 ; bons de commande (« C- ») +1 000.
--   Les n° de bordereau Chifa sont SAISIS (jamais attribués par le logiciel, script 110) : aucun compteur à sauter.
--   * bascule_promotion (ajout seul) : une ligne par timeline notée = « le saut de cette promotion est fait » (clé unique : une seule
--     fois par promotion, même après un redémarrage de l'API). Première note sans saut (mise en service du suivi).
--   * bascule_saut (ajout seul) : chaque saut (compteur, ancienne et nouvelle valeur, réserve).
--   * compteur_ticket_saut : motif « bascule » admis (contrainte gardée, rejouable) ; reconnu par journal_vente_continuite (script 151).
--   * journal_vente : évènement « saut_compteur » admis (contrainte gardée) ; le saut des tickets y est inscrit, chaîné, sans montant.
--   * journal_audit : chaque saut (tickets, factures, avoirs, commandes) y est inscrit, motif « bascule ».
--   * bascule_saut_promotion(p_timeline) : fonction appelée au démarrage de l'API (avant toute écriture) ; refusée en lecture seule.
--   Aucune donnée existante modifiée à l'exécution du script ; les compteurs ne bougent qu'à l'appel de la fonction, au démarrage
--   d'une API sur une base dont la timeline est plus haute que la dernière notée.
--   Retour arrière : database/migration/retour/164_bascule_saut_compteurs_promotion_retour.sql (refusé après un saut : les n° sautés
--   ne se reprennent jamais et leur trace est fiscale).
-- Idempotent et rejouable sur une base plus récente.
-- A exécuter après 163_serveur_ecarte_lecture_seule.sql.
-- =====================================================================

-- Fichier en UTF-8 : lu comme tel quel que soit l'encodage du poste qui lance psql.
SET client_encoding = 'UTF8';

-- ---- 1. Motif « bascule » des plages de tickets sautées (contrainte à liste de valeurs, gardée) ----
DO $garde$ BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'ck_compteur_ticket_saut_motif'
                   AND conrelid = 'compteur_ticket_saut'::regclass AND pg_get_constraintdef(oid) LIKE '%''bascule''%') THEN
        ALTER TABLE compteur_ticket_saut DROP CONSTRAINT IF EXISTS ck_compteur_ticket_saut_motif;
        ALTER TABLE compteur_ticket_saut ADD CONSTRAINT ck_compteur_ticket_saut_motif CHECK (motif IN ('restauration', 'bascule'));
    END IF;
END $garde$;

-- ---- 2. Évènement « saut_compteur » du journal fiscal (contrainte à liste de valeurs, gardée) ----
DO $garde$ BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'ck_journal_vente_evenement'
                   AND conrelid = 'journal_vente'::regclass AND pg_get_constraintdef(oid) LIKE '%saut_compteur%') THEN
        ALTER TABLE journal_vente DROP CONSTRAINT IF EXISTS ck_journal_vente_evenement;
        ALTER TABLE journal_vente ADD CONSTRAINT ck_journal_vente_evenement CHECK (evenement IN
            ('ouverture', 'vente', 'annulation', 'retour', 'avoir', 'regularisation', 'annulation_regularisation', 'saut_compteur'));
    END IF;
END $garde$;

-- ---- 3. Promotions notées et sauts faits (ajout seul, jamais envoyés au central) ----
CREATE TABLE IF NOT EXISTS bascule_promotion (
    id        bigint       GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    timeline  int          NOT NULL CHECK (timeline >= 1),
    saut      boolean      NOT NULL,
    motif     varchar(300) NOT NULL,
    cree_le   timestamptz  NOT NULL DEFAULT now(),
    CONSTRAINT ux_bascule_promotion_timeline UNIQUE (timeline)
);
COMMENT ON TABLE bascule_promotion IS 'Une ligne par timeline de la base : saut des compteurs fait à cette promotion (saut) ou première note sans saut ; clé unique = un seul saut par promotion ; ajout seul (script 164).';

CREATE TABLE IF NOT EXISTS bascule_saut (
    id            bigint       GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    promotion_id  bigint       NOT NULL REFERENCES bascule_promotion (id),
    compteur      text         NOT NULL,
    detail        varchar(100),
    ancienne      bigint       NOT NULL CHECK (ancienne >= 0),
    nouvelle      bigint       NOT NULL,
    reserve       bigint       NOT NULL CHECK (reserve >= 1),
    cree_le       timestamptz  NOT NULL DEFAULT now(),
    CONSTRAINT ck_bascule_saut_compteur CHECK (compteur IN ('ticket', 'facture', 'avoir', 'commande')),
    CONSTRAINT ck_bascule_saut_valeurs CHECK (nouvelle = ancienne + reserve)
);
CREATE INDEX IF NOT EXISTS ix_bascule_saut_promotion ON bascule_saut (promotion_id);
COMMENT ON TABLE bascule_saut IS 'Chaque saut de compteur fait à une promotion (ancienne et nouvelle valeur, réserve) ; ajout seul (script 164).';

-- integrite_ajout_seul_restauration : fonction du script 151 (ni modification ni suppression, ni vidage)
DROP TRIGGER IF EXISTS trg_bascule_promotion_ajout_seul ON bascule_promotion;
CREATE TRIGGER trg_bascule_promotion_ajout_seul BEFORE UPDATE OR DELETE ON bascule_promotion FOR EACH ROW EXECUTE FUNCTION integrite_ajout_seul_restauration();
DROP TRIGGER IF EXISTS trg_bascule_promotion_non_vidable ON bascule_promotion;
CREATE TRIGGER trg_bascule_promotion_non_vidable BEFORE TRUNCATE ON bascule_promotion FOR EACH STATEMENT EXECUTE FUNCTION integrite_ajout_seul_restauration();
DROP TRIGGER IF EXISTS trg_bascule_saut_ajout_seul ON bascule_saut;
CREATE TRIGGER trg_bascule_saut_ajout_seul BEFORE UPDATE OR DELETE ON bascule_saut FOR EACH ROW EXECUTE FUNCTION integrite_ajout_seul_restauration();
DROP TRIGGER IF EXISTS trg_bascule_saut_non_vidable ON bascule_saut;
CREATE TRIGGER trg_bascule_saut_non_vidable BEFORE TRUNCATE ON bascule_saut FOR EACH STATEMENT EXECUTE FUNCTION integrite_ajout_seul_restauration();

-- Classement pour la synchronisation : jamais envoyées au serveur central
INSERT INTO sync_table (nom_table, sens, cle_colonnes, colonnes_exclues, colonnes_patient, colonnes_texte, porteur, motif) VALUES
    ('bascule_promotion', 'jamais', '{id}', '{}', '{}', '{}', false, 'Promotions notées : saut des compteurs fait une seule fois par timeline, script 164'),
    ('bascule_saut', 'jamais', '{id}', '{}', '{}', '{}', false, 'Sauts de compteurs faits à une promotion (ancienne et nouvelle valeur), script 164')
ON CONFLICT (nom_table) DO NOTHING;

-- ---- 4. Fonctions ----
-- Timeline de la base en écriture : 8 premiers caractères hexadécimaux du nom du fichier WAL courant (même lecture que l'API)
CREATE OR REPLACE FUNCTION bascule_timeline_courante() RETURNS int
LANGUAGE sql VOLATILE AS $$
    SELECT ('x' || substr(pg_walfile_name(pg_current_wal_lsn()), 1, 8))::bit(32)::int
$$;

-- Note un saut (une ligne de bascule_saut + le journal des actions) ; usage interne de bascule_saut_promotion
CREATE OR REPLACE FUNCTION bascule_noter_saut(p_promotion bigint, p_compteur text, p_detail text, p_ancienne bigint, p_nouvelle bigint) RETURNS void
LANGUAGE plpgsql AS $$
BEGIN
    INSERT INTO bascule_saut (promotion_id, compteur, detail, ancienne, nouvelle, reserve)
    VALUES (p_promotion, p_compteur, p_detail, p_ancienne, p_nouvelle, p_nouvelle - p_ancienne);
    INSERT INTO journal_audit (utilisateur_id, action, table_cible, details)
    VALUES (NULL, 'saut_compteur_bascule', 'bascule_saut',
            jsonb_build_object('motif', 'bascule', 'compteur', p_compteur, 'detail', p_detail,
                               'ancienne_valeur', p_ancienne, 'nouvelle_valeur', p_nouvelle, 'reserve', p_nouvelle - p_ancienne));
END $$;

-- Saut des compteurs à la promotion : une seule fois par timeline (ligne unique dans bascule_promotion), avant toute écriture.
--   * base en lecture seule : refusée (le 2e serveur n'est pas promu) ;
--   * aucune ligne notée : première mise en service du suivi, la timeline est notée SANS saut ;
--   * timeline plus haute que la dernière notée : saut (tickets +100 000 ; factures, avoirs, commandes +1 000), une transaction ;
--   * sinon : rien. p_timeline sert aux tests ; l'API ne le passe pas (timeline lue dans la base).
CREATE OR REPLACE FUNCTION bascule_saut_promotion(p_timeline int DEFAULT NULL)
RETURNS TABLE (timeline_notee int, saut_fait boolean, compteurs_sautes int)
LANGUAGE plpgsql AS $$
#variable_conflict use_column
DECLARE
    c_reserve_tickets   constant bigint := 100000;
    c_reserve_factures  constant bigint := 1000;
    c_reserve_avoirs    constant bigint := 1000;
    c_reserve_commandes constant bigint := 1000;
    v_tl        int;
    v_max       int;
    v_promotion bigint;
    v_n         int := 0;
    v_compteur  compteur_ticket;
    v_ancienne  bigint;
    v_nouvelle  bigint;
    v_annee     int := extract(year FROM (now() AT TIME ZONE 'Africa/Algiers'))::int;
    r           record;
BEGIN
    IF pg_is_in_recovery() THEN
        RAISE EXCEPTION 'Saut des compteurs refusé : la base est en lecture seule (2e serveur pas encore promu)' USING ERRCODE = 'read_only_sql_transaction';
    END IF;
    v_tl := COALESCE(p_timeline, bascule_timeline_courante());
    IF v_tl IS NULL OR v_tl < 1 THEN
        RAISE EXCEPTION 'Saut des compteurs refusé : timeline invalide (%)', v_tl USING ERRCODE = 'check_violation';
    END IF;
    -- Deux démarrages en même temps : le second attend la fin du premier, puis ne refait rien
    PERFORM pg_advisory_xact_lock(hashtextextended('bascule_saut_promotion', 0));
    SELECT max(b.timeline) INTO v_max FROM bascule_promotion b;

    IF v_max IS NULL THEN
        INSERT INTO bascule_promotion (timeline, saut, motif)
        VALUES (v_tl, false, 'Première note : timeline courante gardée sans saut (mise en service du suivi des promotions)');
        timeline_notee := v_tl; saut_fait := false; compteurs_sautes := 0;
        RETURN NEXT;
        RETURN;
    END IF;
    IF v_tl <= v_max THEN
        timeline_notee := v_tl; saut_fait := false; compteurs_sautes := 0;
        RETURN NEXT;
        RETURN;
    END IF;

    INSERT INTO bascule_promotion (timeline, saut, motif)
    VALUES (v_tl, true, format('Promotion : timeline %s après la timeline %s ; compteurs sautés (tickets +%s, factures +%s, avoirs +%s, commandes +%s)',
                               v_tl, v_max, c_reserve_tickets, c_reserve_factures, c_reserve_avoirs, c_reserve_commandes))
    RETURNING id INTO v_promotion;

    -- Tickets « T- » : plage sautée notée (motif « bascule »), compteur avancé sous la garde du script 151, journal fiscal
    SELECT * INTO v_compteur FROM compteur_ticket WHERE id = 1 FOR UPDATE;
    IF FOUND THEN
        v_ancienne := v_compteur.dernier_numero;
        v_nouvelle := v_ancienne + c_reserve_tickets;
        INSERT INTO compteur_ticket_saut (du, au, motif) VALUES (v_ancienne + 1, v_nouvelle, 'bascule');
        PERFORM set_config('pharma.saut_ticket', v_nouvelle::text, true);
        UPDATE compteur_ticket SET dernier_numero = v_nouvelle WHERE id = 1;
        PERFORM set_config('pharma.saut_ticket', '', true);
        PERFORM bascule_noter_saut(v_promotion, 'ticket', v_compteur.prefixe, v_ancienne, v_nouvelle);
        INSERT INTO journal_vente (evenement, survenu_le, motif)
        VALUES ('saut_compteur', now(),
                format('Bascule du 2e serveur (timeline %s) : compteur des tickets sauté de %s%s à %s%s (réserve de %s n°)',
                       v_tl, v_compteur.prefixe, lpad(v_ancienne::text, GREATEST(6, char_length(v_ancienne::text)), '0'),
                       v_compteur.prefixe, lpad(v_nouvelle::text, GREATEST(6, char_length(v_nouvelle::text)), '0'), c_reserve_tickets));
        v_n := v_n + 1;
    END IF;

    -- Factures et avoirs : l'année en cours (créée si absente) et toute année déjà ouverte
    INSERT INTO facture_compteur (type, annee, dernier) VALUES ('facture', v_annee, 0), ('avoir', v_annee, 0) ON CONFLICT (type, annee) DO NOTHING;
    FOR r IN SELECT f.type, f.annee, f.dernier FROM facture_compteur f ORDER BY f.type, f.annee FOR UPDATE LOOP
        v_nouvelle := r.dernier + CASE WHEN r.type = 'avoir' THEN c_reserve_avoirs ELSE c_reserve_factures END;
        UPDATE facture_compteur SET dernier = v_nouvelle WHERE type = r.type AND annee = r.annee;
        PERFORM bascule_noter_saut(v_promotion, r.type, r.annee::text, r.dernier, v_nouvelle);
        v_n := v_n + 1;
    END LOOP;

    -- Bons de commande « C- » (séquence) : en dernier, un setval ne s'annule pas avec la transaction
    IF to_regclass('public.seq_numero_commande') IS NOT NULL THEN
        SELECT CASE WHEN s.is_called THEN s.last_value ELSE s.last_value - 1 END INTO v_ancienne FROM seq_numero_commande s;
        v_nouvelle := v_ancienne + c_reserve_commandes;
        PERFORM bascule_noter_saut(v_promotion, 'commande', 'C-', v_ancienne, v_nouvelle);
        PERFORM setval('seq_numero_commande', v_nouvelle, true);
        v_n := v_n + 1;
    END IF;

    timeline_notee := v_tl; saut_fait := true; compteurs_sautes := v_n;
    RETURN NEXT;
END $$;
COMMENT ON FUNCTION bascule_saut_promotion(int) IS 'Saut des compteurs à la promotion du 2e serveur, une seule fois par timeline, avant toute écriture (script 164, décision Q13).';
