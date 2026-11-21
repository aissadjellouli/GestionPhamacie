-- =====================================================================
-- Base centrale, lot S7.2a (2026-11-21, go du titulaire, recommandations
-- S7 retenues) : écrans du TITULAIRE, côté serveur central seulement
-- (docs/ecrans-central.md). Script de la base CENTRALE, passé par le
-- propriétaire, idempotent ; à passer après 031.
--   1. compte_officine : colonne « droits » (consulter, journal_acces).
--   2. code_rattachement : code à usage unique qui rattache UNE officine à
--      UN compte titulaire ; seule l'empreinte SHA-256 du code est gardée
--      (jamais le code) ; 30 minutes ; créé par la console du central en
--      attendant S7.2b (rubrique « Accès à distance » du poste).
--   3. cloud_ecran_rattacher : session titulaire, code (empreinte, compte
--      visé, non expiré, non utilisé), 5 refus au plus par quart d'heure ;
--      notée au journal des accès ET au journal de sécurité.
--   4. Lecture du titulaire : ses officines, le tableau de bord d'une
--      officine (AGRÉGATS seulement, aucune donnée nominative, aucune
--      ligne de détail), le journal des accès de ses officines. Les tables
--      miroir sont lues avec l'officine posée PAR LA BASE
--      (cloud_ecran_ouvrir, RLS forcée). Une donnée qui n'est pas montée au
--      central (table ou colonne absente du miroir, colonne d'une famille
--      non envoyée, aucun chargement complet terminé comprenant la table)
--      est rendue NULL (« non disponible » à l'écran), jamais inventée.
--   5. L'éditeur n'a aucune de ces fonctions (rôle « titulaire » exigé) ;
--      le titulaire n'a aucune fonction de l'éditeur (031 : rôle
--      « editeur » exigé).
-- Aucune donnée existante n'est lue, modifiée ni supprimée par ce script.
-- =====================================================================

SET client_encoding = 'UTF8';

DO $version$ BEGIN
    IF to_regprocedure('public.cloud_ecran_editeur(text)') IS NULL OR to_regprocedure('public.cloud_ecran_ouvrir(text,uuid)') IS NULL
       OR to_regclass('public.compte_officine') IS NULL THEN
        RAISE EXCEPTION 'Script central 032 : passer d''abord les scripts 030 et 031 (écrans du central). Rien n''a été modifié.';
    END IF;
END $version$;

-- ---- 1. Droits d'un compte titulaire sur une officine ----
ALTER TABLE compte_officine ADD COLUMN IF NOT EXISTS droits text[] NOT NULL DEFAULT '{consulter,journal_acces}';
DO $$ BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'ck_compte_officine_droits' AND conrelid = 'compte_officine'::regclass) THEN
        ALTER TABLE compte_officine ADD CONSTRAINT ck_compte_officine_droits
            CHECK (cardinality(droits) >= 1 AND droits <@ ARRAY['consulter', 'journal_acces']::text[]);
    END IF;
END $$;

-- ---- 2. Codes de rattachement (empreinte seulement, 30 min, usage unique) ----
CREATE TABLE IF NOT EXISTS code_rattachement (
    id              bigint      GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    empreinte_code  text        NOT NULL UNIQUE CHECK (empreinte_code ~ '^[0-9a-f]{64}$'),
    compte_id       uuid        NOT NULL REFERENCES compte_distant (id),
    officine_id     uuid        NOT NULL REFERENCES officine (id) ON UPDATE CASCADE,
    cree_le         timestamptz NOT NULL DEFAULT now(),
    expire_le       timestamptz NOT NULL,
    cree_par        text        NOT NULL CHECK (length(btrim(cree_par)) BETWEEN 1 AND 100),
    utilise_le      timestamptz,
    annule_le       timestamptz,
    CONSTRAINT ck_code_rattachement_duree CHECK (expire_le > cree_le AND expire_le <= cree_le + interval '30 minutes')
);
CREATE INDEX IF NOT EXISTS ix_code_rattachement_en_cours ON code_rattachement (compte_id, officine_id) WHERE utilise_le IS NULL AND annule_le IS NULL;

-- Aucun droit direct, pour aucun rôle applicatif (rejeu : mêmes droits)
REVOKE ALL ON code_rattachement FROM PUBLIC, pharma_cloud_api, pharma_cloud_etat, pharma_cloud_ecrans;

-- ---------------------------------------------------------------------
-- Contrôles internes (jamais accordés)
-- ---------------------------------------------------------------------

-- Session ouverte d'un compte titulaire ; sinon erreur 42501 (l'éditeur n'a aucun écran du titulaire)
CREATE OR REPLACE FUNCTION cloud_ecran_titulaire(p_empreinte_jeton text, OUT session_id bigint, OUT compte_id uuid)
LANGUAGE plpgsql SECURITY DEFINER SET search_path = public, pg_temp AS $$
DECLARE v record;
BEGIN
    SELECT * INTO v FROM cloud_ecran_session_ouverte(p_empreinte_jeton);
    IF v.role <> 'titulaire' THEN
        RAISE EXCEPTION 'écran réservé au titulaire' USING ERRCODE = '42501';
    END IF;
    session_id := v.session_id; compte_id := v.compte_id;
END $$;

-- Ouverture d'une officine par un titulaire avec un droit : cloud_ecran_ouvrir (030 : session, rattachement non retiré, officine
-- courante posée par la base) puis le droit demandé. Rend false sans lever d'erreur (les refus restent au journal des accès).
CREATE OR REPLACE FUNCTION cloud_ecran_ouvrir_titulaire(p_empreinte_jeton text, p_officine uuid, p_droit text, p_evenement text,
                                                        OUT ouverte boolean, OUT session_id bigint, OUT compte_id uuid)
LANGUAGE plpgsql SECURITY DEFINER SET search_path = public, pg_temp AS $$
DECLARE v record;
BEGIN
    SELECT * INTO v FROM cloud_ecran_titulaire(p_empreinte_jeton);
    session_id := v.session_id; compte_id := v.compte_id; ouverte := false;
    IF NOT cloud_ecran_ouvrir(p_empreinte_jeton, p_officine) THEN RETURN; END IF;
    IF NOT EXISTS (SELECT 1 FROM compte_officine x WHERE x.compte_id = v.compte_id AND x.officine_id = p_officine AND x.retire_le IS NULL
                   AND p_droit = ANY (x.droits)) THEN
        PERFORM set_config('app.officine_id', '', true);
        PERFORM cloud_ecran_journaliser(v.compte_id, v.session_id, p_officine, p_evenement, 'refus', 'droit:' || p_droit);
        RETURN;
    END IF;
    ouverte := true;
END $$;

-- Donnée montée au central pour l'officine courante : table du miroir et colonnes reçues (catalogue cloud_table_miroir et table
-- réelle), aucune colonne d'une famille nominative non envoyée par l'officine, et au moins un chargement complet TERMINÉ
-- comprenant la table (sans lui, une somme serait partielle). Sinon false : l'écran affiche « non disponible ».
CREATE OR REPLACE FUNCTION cloud_ecran_miroir_disponible(p_table text, p_colonnes text[]) RETURNS boolean
LANGUAGE plpgsql STABLE SECURITY DEFINER SET search_path = public, pg_temp AS $$
DECLARE v_off uuid := cloud_officine_courante(); v_rel regclass;
BEGIN
    IF v_off IS NULL OR p_table IS NULL OR p_table !~ '^[a-z_]{1,63}$' THEN RETURN false; END IF;
    v_rel := to_regclass('public.' || quote_ident(p_table));
    IF v_rel IS NULL THEN RETURN false; END IF;
    IF NOT EXISTS (SELECT 1 FROM cloud_table_miroir m WHERE m.nom_table = p_table AND m.colonnes @> p_colonnes) THEN RETURN false; END IF;
    IF (SELECT count(DISTINCT a.attname) FROM pg_attribute a
        WHERE a.attrelid = v_rel AND a.attnum > 0 AND NOT a.attisdropped AND a.attname::text = ANY (p_colonnes))
       <> (SELECT count(DISTINCT x) FROM unnest(p_colonnes) x) THEN
        RETURN false;
    END IF;
    IF EXISTS (SELECT 1 FROM cloud_famille_colonne c WHERE c.nom_table = p_table AND c.colonne = ANY (p_colonnes)
               AND NOT c.famille = ANY (cloud_familles_envoyees(v_off))) THEN
        RETURN false;
    END IF;
    RETURN EXISTS (SELECT 1 FROM chargement c WHERE c.etat = 'termine' AND p_table = ANY (c.tables));
END $$;

-- ---------------------------------------------------------------------
-- Lecture du titulaire
-- ---------------------------------------------------------------------

-- Mes officines : celles de compte_officine non retirées, avec l'état technique (dernier contact, retard), sans aucune donnée de vente
CREATE OR REPLACE FUNCTION cloud_ecran_titulaire_officines(p_empreinte_jeton text)
RETURNS TABLE (id uuid, libelle text, etat text, rattachee_le timestamptz, droits text, dernier_contact timestamptz, en_retard boolean)
LANGUAGE plpgsql SECURITY DEFINER SET search_path = public, pg_temp AS $$
DECLARE v record; o record; v_avant text := current_setting('app.officine_id', true); v_contact timestamptz;
BEGIN
    SELECT * INTO v FROM cloud_ecran_titulaire(p_empreinte_jeton);
    FOR o IN SELECT f.id AS o_id, f.libelle AS o_libelle, cloud_ecran_etat_officine(f.id) AS o_etat, x.accorde_le AS o_le, x.droits AS o_droits
             FROM compte_officine x JOIN officine f ON f.id = x.officine_id
             WHERE x.compte_id = v.compte_id AND x.retire_le IS NULL
             ORDER BY lower(f.libelle), f.id LIMIT 200 LOOP
        PERFORM set_config('app.officine_id', o.o_id::text, true);
        SELECT greatest((SELECT max(e.dernier_recu_le) FROM officine_epoque e),
                        (SELECT max(c.emis_le) FROM certificat c WHERE c.officine_id = o.o_id)) INTO v_contact;
        id := o.o_id; libelle := o.o_libelle; etat := o.o_etat; rattachee_le := o.o_le;
        droits := array_to_string(o.o_droits, ','); dernier_contact := v_contact;
        en_retard := o.o_etat = 'active' AND (v_contact IS NULL OR v_contact < now() - interval '24 hours');
        RETURN NEXT;
    END LOOP;
    PERFORM set_config('app.officine_id', coalesce(v_avant, ''), true);
    PERFORM cloud_ecran_journaliser(v.compte_id, v.session_id, NULL, 'liste_officines_titulaire', 'ok', NULL);
END $$;

-- Tableau de bord d'une officine du titulaire : UNE ligne d'agrégats (aucune ligne vide si l'ouverture est refusée : refus au
-- journal des accès). Journée et mois au fuseau de l'officine (parametre_fiscal.fuseau s'il est monté et connu du serveur, sinon
-- Africa/Algiers, réglage de la base locale, migration 083). Mêmes définitions que le tableau de bord local (PilotageSql) :
-- chiffre d'affaires = ventes validées moins retours clients du jour (ou du mois) ; crédits en cours = somme des soldes de crédit
-- positifs (comme le pilotage financier). Lignes marquées absentes au dernier chargement (sync_absente_le) exclues.
CREATE OR REPLACE FUNCTION cloud_ecran_titulaire_tableau(p_empreinte_jeton text, p_officine uuid)
RETURNS TABLE (id uuid, libelle text, etat text, fuseau text, dernier_contact timestamptz, en_retard boolean, dernier_lot bigint,
               chargement_complet_le timestamptz, chargement_en_cours boolean,
               ca_jour numeric, ventes_jour int, ca_mois numeric, ventes_mois int, ca_mois_precedent numeric, part_chifa_mois numeric,
               credits_en_cours numeric, alertes_stock int, alertes_peremption int, anomalies_ouvertes bigint, ordres_en_attente bigint)
LANGUAGE plpgsql SECURITY DEFINER SET search_path = public, pg_temp AS $$
DECLARE v record; o officine; v_fuseau text := 'Africa/Algiers'; v_f text; v_jour date;
        v_debut_jour timestamptz; v_fin_jour timestamptz; v_debut_mois timestamptz; v_debut_prec timestamptz;
        v_ventes boolean; v_retours boolean; r_jour numeric; r_mois numeric; r_prec numeric;
BEGIN
    SELECT * INTO v FROM cloud_ecran_ouvrir_titulaire(p_empreinte_jeton, p_officine, 'consulter', 'tableau_titulaire');
    IF NOT v.ouverte THEN RETURN; END IF;
    SELECT * INTO o FROM officine x WHERE x.id = p_officine;

    IF cloud_ecran_miroir_disponible('parametre_fiscal', ARRAY['fuseau']) THEN
        SELECT p.fuseau INTO v_f FROM parametre_fiscal p WHERE p.sync_absente_le IS NULL ORDER BY p.id LIMIT 1;
        IF v_f IS NOT NULL AND EXISTS (SELECT 1 FROM pg_timezone_names z WHERE z.name = v_f) THEN v_fuseau := v_f; END IF;
    END IF;
    v_jour := (now() AT TIME ZONE v_fuseau)::date;
    v_debut_jour := v_jour::timestamp AT TIME ZONE v_fuseau;
    v_fin_jour := (v_jour + 1)::timestamp AT TIME ZONE v_fuseau;
    v_debut_mois := date_trunc('month', v_jour::timestamp) AT TIME ZONE v_fuseau;
    v_debut_prec := (date_trunc('month', v_jour::timestamp) - interval '1 month') AT TIME ZONE v_fuseau;

    id := o.id; libelle := o.libelle; etat := cloud_ecran_etat_officine(o.id); fuseau := v_fuseau;
    -- Synchronisation (tables du central, toujours disponibles)
    SELECT greatest((SELECT max(e.dernier_recu_le) FROM officine_epoque e), (SELECT max(c.emis_le) FROM certificat c WHERE c.officine_id = o.id))
        INTO dernier_contact;
    en_retard := etat = 'active' AND (dernier_contact IS NULL OR dernier_contact < now() - interval '24 hours');
    SELECT e.dernier_lot INTO dernier_lot FROM officine_epoque e ORDER BY e.epoque DESC LIMIT 1;
    SELECT max(c.fin_le) INTO chargement_complet_le FROM chargement c WHERE c.etat = 'termine';
    chargement_en_cours := EXISTS (SELECT 1 FROM chargement c WHERE c.etat = 'en_cours');
    SELECT count(*) INTO anomalies_ouvertes FROM cloud_anomalie_integrite a LEFT JOIN cloud_anomalie_classement k ON k.anomalie_id = a.id
    WHERE k.classe IS DISTINCT FROM 'remplacee_apres_restauration';
    SELECT count(*) INTO ordres_en_attente FROM ordre_central x WHERE x.statut IN ('en_attente', 'a_confirmer');

    -- Ventes et retours (tables miroir)
    v_ventes := cloud_ecran_miroir_disponible('vente', ARRAY['date_vente', 'montant_total', 'statut']);
    v_retours := cloud_ecran_miroir_disponible('retour_client', ARRAY['vente_id', 'montant_rembourse', 'cree_le']);
    IF v_ventes AND v_retours THEN
        SELECT coalesce(sum(x.montant_total) FILTER (WHERE x.date_vente >= v_debut_jour), 0),
               count(*) FILTER (WHERE x.date_vente >= v_debut_jour)::int,
               coalesce(sum(x.montant_total) FILTER (WHERE x.date_vente >= v_debut_mois), 0),
               count(*) FILTER (WHERE x.date_vente >= v_debut_mois)::int,
               coalesce(sum(x.montant_total) FILTER (WHERE x.date_vente < v_debut_mois), 0)
          INTO ca_jour, ventes_jour, ca_mois, ventes_mois, ca_mois_precedent
          FROM vente x
         WHERE x.statut = 'validee' AND x.sync_absente_le IS NULL AND x.date_vente >= v_debut_prec AND x.date_vente < v_fin_jour;
        SELECT coalesce(sum(rc.montant_rembourse) FILTER (WHERE rc.cree_le >= v_debut_jour), 0),
               coalesce(sum(rc.montant_rembourse) FILTER (WHERE rc.cree_le >= v_debut_mois), 0),
               coalesce(sum(rc.montant_rembourse) FILTER (WHERE rc.cree_le < v_debut_mois), 0)
          INTO r_jour, r_mois, r_prec
          FROM retour_client rc JOIN vente vr ON vr.officine_id = rc.officine_id AND vr.id = rc.vente_id AND vr.statut = 'validee'
         WHERE rc.sync_absente_le IS NULL AND rc.cree_le >= v_debut_prec AND rc.cree_le < v_fin_jour;
        ca_jour := ca_jour - r_jour; ca_mois := ca_mois - r_mois; ca_mois_precedent := ca_mois_precedent - r_prec;
    ELSIF v_ventes THEN
        -- Nombre de ventes sans les retours : le chiffre d'affaires reste « non disponible » (il serait faux)
        SELECT count(*) FILTER (WHERE x.date_vente >= v_debut_jour)::int, count(*) FILTER (WHERE x.date_vente >= v_debut_mois)::int
          INTO ventes_jour, ventes_mois
          FROM vente x WHERE x.statut = 'validee' AND x.sync_absente_le IS NULL AND x.date_vente >= v_debut_mois AND x.date_vente < v_fin_jour;
    END IF;
    IF cloud_ecran_miroir_disponible('vente', ARRAY['date_vente', 'statut', 'part_organisme', 'canal_organisme']) THEN
        SELECT coalesce(sum(x.part_organisme), 0) INTO part_chifa_mois FROM vente x
         WHERE x.statut = 'validee' AND x.sync_absente_le IS NULL AND x.canal_organisme = 'chifa'
           AND x.date_vente >= v_debut_mois AND x.date_vente < v_fin_jour;
    END IF;
    -- Crédits des patients (somme seulement, aucun patient désigné)
    IF cloud_ecran_miroir_disponible('patient', ARRAY['solde_credit']) THEN
        SELECT coalesce(sum(p.solde_credit), 0) INTO credits_en_cours FROM patient p WHERE p.solde_credit > 0 AND p.sync_absente_le IS NULL;
    END IF;
    -- Alertes de stock et de péremption non traitées (nombres seulement)
    IF cloud_ecran_miroir_disponible('alerte', ARRAY['type', 'statut']) THEN
        SELECT count(*) FILTER (WHERE a.type IN ('rupture', 'stock_critique'))::int,
               count(*) FILTER (WHERE a.type IN ('peremption', 'perime'))::int
          INTO alertes_stock, alertes_peremption
          FROM alerte a WHERE a.statut IN ('nouvelle', 'vue') AND a.sync_absente_le IS NULL;
    END IF;
    RETURN NEXT;
END $$;

-- Journal des accès d'une officine du titulaire (tous les comptes, éditeur compris : qui, quoi, quand), sans identifiant ni nom :
-- l'acteur est « vous », « editeur » ou « titulaire » (un autre compte titulaire). Page de 10 à 100 lignes.
CREATE OR REPLACE FUNCTION cloud_ecran_titulaire_journal(p_empreinte_jeton text, p_officine uuid, p_page int, p_taille int)
RETURNS TABLE (total bigint, id bigint, le timestamptz, acteur text, evenement text, resultat text, objet text)
LANGUAGE plpgsql SECURITY DEFINER SET search_path = public, pg_temp AS $$
DECLARE v record; v_taille int := least(greatest(coalesce(p_taille, 20), 10), 100); v_page int := greatest(coalesce(p_page, 1), 1);
BEGIN
    SELECT * INTO v FROM cloud_ecran_ouvrir_titulaire(p_empreinte_jeton, p_officine, 'journal_acces', 'journal_officine_titulaire');
    IF NOT v.ouverte THEN RETURN; END IF;
    RETURN QUERY SELECT count(*) OVER (), j.id, j.le,
                        -- Commande de la console (objet « console ») : notée au compte concerné, mais faite par le propriétaire du central
                        CASE WHEN j.objet = 'console' OR j.compte_id IS NULL THEN 'console' WHEN j.compte_id = v.compte_id THEN 'vous'
                             WHEN c.role = 'editeur' THEN 'editeur' WHEN c.role = 'titulaire' THEN 'titulaire' ELSE 'console' END,
                        j.evenement, j.resultat, j.objet
                 FROM journal_acces_distant j LEFT JOIN compte_distant c ON c.id = j.compte_id
                 WHERE j.officine_id = p_officine ORDER BY j.id DESC OFFSET (v_page - 1) * v_taille LIMIT v_taille;
END $$;

-- ---------------------------------------------------------------------
-- Rattachement d'une officine par code
-- ---------------------------------------------------------------------

-- Statuts : ok, inconnu (code absent, ou destiné à un autre compte : même réponse), utilise, expire, partie, trop_d_essais.
-- Chaque refus est noté au journal des accès (le compte visé par un code d'un autre compte n'est jamais révélé à l'écran).
CREATE OR REPLACE FUNCTION cloud_ecran_rattacher(p_empreinte_jeton text, p_empreinte_code text)
RETURNS TABLE (statut text, officine_id uuid)
LANGUAGE plpgsql SECURITY DEFINER SET search_path = public, pg_temp AS $$
DECLARE v record; k code_rattachement; o officine; r text;
BEGIN
    SELECT * INTO v FROM cloud_ecran_titulaire(p_empreinte_jeton);
    IF (SELECT count(*) FROM journal_acces_distant j WHERE j.compte_id = v.compte_id AND j.evenement = 'rattachement' AND j.resultat = 'refus'
        AND j.le > now() - interval '15 minutes') >= 5 THEN
        PERFORM cloud_ecran_journaliser(v.compte_id, v.session_id, NULL, 'rattachement', 'refus', 'trop_d_essais');
        RETURN QUERY SELECT 'trop_d_essais'::text, NULL::uuid; RETURN;
    END IF;
    IF p_empreinte_code IS NOT NULL AND p_empreinte_code ~ '^[0-9a-f]{64}$' THEN
        SELECT * INTO k FROM code_rattachement x WHERE x.empreinte_code = p_empreinte_code FOR UPDATE;
    END IF;
    r := CASE WHEN k.id IS NULL THEN 'inconnu'
              WHEN k.compte_id <> v.compte_id THEN 'autre_compte'
              WHEN k.utilise_le IS NOT NULL OR k.annule_le IS NOT NULL THEN 'utilise'
              WHEN k.expire_le <= now() THEN 'expire' END;
    IF r IS NULL THEN
        SELECT * INTO o FROM officine x WHERE x.id = k.officine_id;
        IF o.resiliee_le IS NOT NULL THEN r := 'partie'; END IF;
    END IF;
    IF r IS NOT NULL THEN
        -- Le journal garde le motif ; un code d'un autre compte n'est pas noté contre l'officine de ce compte
        PERFORM cloud_ecran_journaliser(v.compte_id, v.session_id, CASE WHEN r IN ('utilise', 'expire', 'partie') THEN k.officine_id END,
                                        'rattachement', 'refus', r);
        RETURN QUERY SELECT CASE WHEN r = 'autre_compte' THEN 'inconnu' ELSE r END, NULL::uuid; RETURN;
    END IF;
    UPDATE code_rattachement SET utilise_le = now() WHERE id = k.id;
    INSERT INTO compte_officine AS x (compte_id, officine_id, accorde_par, droits)
    VALUES (v.compte_id, k.officine_id, 'code de rattachement n° ' || k.id, ARRAY['consulter', 'journal_acces'])
    ON CONFLICT ON CONSTRAINT compte_officine_pkey DO UPDATE SET retire_le = NULL, droits = EXCLUDED.droits, accorde_le = now(), accorde_par = EXCLUDED.accorde_par;
    PERFORM cloud_ecran_journaliser(v.compte_id, v.session_id, k.officine_id, 'rattachement', 'ok', NULL);
    INSERT INTO journal_securite (officine_id, evenement, detail)
    VALUES (k.officine_id, 'rattachement_titulaire',
            left('écran titulaire, compte n° ' || left(v.compte_id::text, 8) || ' : officine rattachée (consulter, journal des accès) par le code n° '
                 || k.id || ' créé par ' || k.cree_par, 500));
    RETURN QUERY SELECT 'ok'::text, k.officine_id;
END $$;

-- ---------------------------------------------------------------------
-- Administration (console du central, connexion du propriétaire ; jamais accordée aux écrans)
-- ---------------------------------------------------------------------

-- Nouveau code de rattachement pour un compte titulaire actif et une officine non partie : les codes en cours du même compte
-- et de la même officine sont annulés. Seule l'empreinte arrive ici (code tiré par l'application, affiché une fois).
-- Statuts : ok, compte_inconnu, pas_titulaire, compte_inactif, officine_inconnue, partie.
CREATE OR REPLACE FUNCTION cloud_compte_distant_code_rattachement(p_identifiant text, p_officine uuid, p_empreinte_code text, p_par text)
RETURNS TABLE (statut text, expire_le timestamptz)
LANGUAGE plpgsql SET search_path = public, pg_temp AS $$
DECLARE c compte_distant; o officine; v_expire timestamptz := now() + interval '30 minutes'; v_id bigint;
BEGIN
    IF p_empreinte_code IS NULL OR p_empreinte_code !~ '^[0-9a-f]{64}$' THEN
        RAISE EXCEPTION 'empreinte du code de rattachement invalide' USING ERRCODE = '22023';
    END IF;
    SELECT * INTO c FROM compte_distant x WHERE x.identifiant = lower(btrim(p_identifiant));
    IF NOT FOUND THEN RETURN QUERY SELECT 'compte_inconnu'::text, NULL::timestamptz; RETURN; END IF;
    IF c.role <> 'titulaire' THEN RETURN QUERY SELECT 'pas_titulaire'::text, NULL::timestamptz; RETURN; END IF;
    IF NOT c.actif THEN RETURN QUERY SELECT 'compte_inactif'::text, NULL::timestamptz; RETURN; END IF;
    SELECT * INTO o FROM officine x WHERE x.id = p_officine;
    IF NOT FOUND THEN RETURN QUERY SELECT 'officine_inconnue'::text, NULL::timestamptz; RETURN; END IF;
    IF o.resiliee_le IS NOT NULL THEN RETURN QUERY SELECT 'partie'::text, NULL::timestamptz; RETURN; END IF;
    UPDATE code_rattachement x SET annule_le = now()
    WHERE x.compte_id = c.id AND x.officine_id = o.id AND x.utilise_le IS NULL AND x.annule_le IS NULL;
    INSERT INTO code_rattachement (empreinte_code, compte_id, officine_id, expire_le, cree_par)
    VALUES (p_empreinte_code, c.id, o.id, v_expire, btrim(p_par)) RETURNING id INTO v_id;
    PERFORM cloud_ecran_journaliser(c.id, NULL, o.id, 'code_rattachement_cree', 'ok', 'console');
    INSERT INTO journal_securite (officine_id, evenement, detail)
    VALUES (o.id, 'code_rattachement_cree',
            left('console : code de rattachement n° ' || v_id || ' (30 min) pour le compte n° ' || left(c.id::text, 8) || ', par ' || btrim(p_par), 500));
    RETURN QUERY SELECT 'ok'::text, v_expire;
END $$;

-- ---- Droits : rien pour PUBLIC ; les écrans n'exécutent que les fonctions du titulaire ci-dessous ----
DO $droits$
DECLARE f regprocedure;
BEGIN
    FOR f IN SELECT p.oid::regprocedure FROM pg_proc p
             WHERE p.pronamespace = 'public'::regnamespace AND (p.proname LIKE 'cloud\_ecran\_%' OR p.proname LIKE 'cloud\_compte\_distant\_%')
    LOOP
        EXECUTE format('REVOKE ALL ON FUNCTION %s FROM PUBLIC', f);
    END LOOP;
END $droits$;
GRANT EXECUTE ON FUNCTION cloud_ecran_titulaire_officines(text) TO pharma_cloud_ecrans;
GRANT EXECUTE ON FUNCTION cloud_ecran_titulaire_tableau(text, uuid) TO pharma_cloud_ecrans;
GRANT EXECUTE ON FUNCTION cloud_ecran_titulaire_journal(text, uuid, int, int) TO pharma_cloud_ecrans;
GRANT EXECUTE ON FUNCTION cloud_ecran_rattacher(text, text) TO pharma_cloud_ecrans;
