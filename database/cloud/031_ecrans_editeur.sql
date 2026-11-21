-- =====================================================================
-- Base centrale, lot S7.1 (2026-11-21, go du titulaire) : écrans de
-- l'ÉDITEUR (docs/ecrans-central.md). Script de la base CENTRALE, passé
-- par le propriétaire, idempotent ; à passer après 030.
--   1. Lecture : fonctions cloud_ecran_* SECURITY DEFINER (search_path
--      fixé, EXECUTE retiré à PUBLIC, accordé à pharma_cloud_ecrans),
--      jeton de session revérifié à chaque appel (rôle « editeur »).
--      Champs TECHNIQUES seulement (décision n° 4 : l'éditeur ne voit
--      jamais de donnée nominative) : jamais ordre_central.donnees ni
--      cible ni motif, jamais cloud_anomalie_integrite.detail / attendu /
--      trouve / cle_ligne, jamais lot_recu.contenu, jamais un code
--      d'activation ni son empreinte, jamais le motif d'une révocation,
--      aucune table miroir.
--   2. Liste des officines : filtre sur le libellé et l'état, page par
--      page ; les éléments cloisonnés (époques, lots, chargements,
--      anomalies) sont lus officine par officine en posant l'officine
--      courante (la RLS forcée s'applique aussi au propriétaire).
--   3. Fiche d'une officine : après cloud_ecran_ouvrir (030), dans la
--      même transaction ; l'officine est celle posée par la base.
--   4. Actions (code de second facteur « action » exigé, motif de 5 à
--      300 caractères, confirmation par ressaisie) : créer une officine
--      et son code d'activation, nouveau code d'activation, révoquer un
--      certificat, révoquer un serveur (cloud_revoquer_serveur du 001).
--      Le code d'activation est tiré par l'application (seule son
--      empreinte arrive ici, comme pour la console). Chaque action est
--      notée au journal des accès ET au journal de sécurité.
-- Aucune donnée existante n'est lue, modifiée ni supprimée par ce script.
-- =====================================================================

SET client_encoding = 'UTF8';

DO $version$ BEGIN
    IF to_regclass('public.journal_acces_distant') IS NULL OR to_regprocedure('public.cloud_ecran_ouvrir(text,uuid)') IS NULL THEN
        RAISE EXCEPTION 'Script central 031 : passer d''abord le script 030 (comptes distants). Rien n''a été modifié.';
    END IF;
END $version$;

-- ---- Contrôles internes (jamais accordés) ----

-- Session ouverte d'un compte éditeur ; sinon erreur 42501
CREATE OR REPLACE FUNCTION cloud_ecran_editeur(p_empreinte_jeton text, OUT session_id bigint, OUT compte_id uuid)
LANGUAGE plpgsql SECURITY DEFINER SET search_path = public, pg_temp AS $$
DECLARE v record;
BEGIN
    SELECT * INTO v FROM cloud_ecran_session_ouverte(p_empreinte_jeton);
    IF v.role <> 'editeur' THEN
        RAISE EXCEPTION 'écran réservé à l''éditeur' USING ERRCODE = '42501';
    END IF;
    session_id := v.session_id; compte_id := v.compte_id;
END $$;

-- Officine ouverte (posée par cloud_ecran_ouvrir dans la transaction) pour un éditeur ; sinon erreur 42501
CREATE OR REPLACE FUNCTION cloud_ecran_officine_editeur(p_empreinte_jeton text, OUT session_id bigint, OUT compte_id uuid, OUT officine_id uuid)
LANGUAGE plpgsql SECURITY DEFINER SET search_path = public, pg_temp AS $$
DECLARE v record; v_officine uuid := cloud_officine_courante();
BEGIN
    SELECT * INTO v FROM cloud_ecran_editeur(p_empreinte_jeton);
    IF v_officine IS NULL OR NOT EXISTS (SELECT 1 FROM officine o WHERE o.id = v_officine) THEN
        RAISE EXCEPTION 'aucune officine ouverte (cloud_ecran_ouvrir)' USING ERRCODE = '42501';
    END IF;
    session_id := v.session_id; compte_id := v.compte_id; officine_id := v_officine;
END $$;

-- État d'une officine (non cloisonné : officine, certificat)
CREATE OR REPLACE FUNCTION cloud_ecran_etat_officine(p_officine uuid) RETURNS text
LANGUAGE sql STABLE SECURITY DEFINER SET search_path = public, pg_temp AS $$
    SELECT CASE
        WHEN o.resiliee_le IS NOT NULL THEN 'partie'
        WHEN EXISTS (SELECT 1 FROM certificat c WHERE c.officine_id = o.id AND c.revoque_le IS NULL AND c.echeance > now()) THEN 'active'
        WHEN NOT EXISTS (SELECT 1 FROM certificat c WHERE c.officine_id = o.id) THEN 'en_attente'
        ELSE 'sans_certificat_valable' END
    FROM officine o WHERE o.id = p_officine
$$;

-- Motif d'une action : 5 à 300 caractères, nettoyé
CREATE OR REPLACE FUNCTION cloud_ecran_motif(p_motif text) RETURNS text
LANGUAGE sql IMMUTABLE SET search_path = public, pg_temp AS $$
    SELECT CASE WHEN length(btrim(coalesce(p_motif, ''))) BETWEEN 5 AND 300 THEN btrim(regexp_replace(p_motif, '[[:cntrl:]]', ' ', 'g')) END
$$;

-- ---------------------------------------------------------------------
-- Lecture
-- ---------------------------------------------------------------------

-- Liste des officines (filtre sur le libellé, état ; page de 10 à 100). Pour chaque officine de la page : dernier contact
-- (dernier lot reçu ou dernier certificat émis), retard (plus de 24 h sans contact alors qu'elle est active), dernier lot,
-- chargement en cours, échéance de certificat valable la plus proche, nombre d'anomalies d'intégrité.
CREATE OR REPLACE FUNCTION cloud_ecran_officines(p_empreinte_jeton text, p_texte text, p_etat text, p_page int, p_taille int)
RETURNS TABLE (total bigint, id uuid, libelle text, etat text, cree_le timestamptz, dernier_contact timestamptz, en_retard boolean,
               dernier_lot bigint, derniere_epoque int, chargement_en_cours boolean, echeance_certificat timestamptz, anomalies bigint)
LANGUAGE plpgsql SECURITY DEFINER SET search_path = public, pg_temp AS $$
DECLARE v record; o record; v_total bigint; v_taille int := least(greatest(coalesce(p_taille, 20), 10), 100);
        v_page int := greatest(coalesce(p_page, 1), 1); v_texte text := nullif(btrim(coalesce(p_texte, '')), '');
        v_etat text := coalesce(nullif(p_etat, ''), 'toutes'); v_avant text := current_setting('app.officine_id', true);
        v_contact timestamptz; v_lot bigint; v_epoque int; v_charg boolean; v_anom bigint;
BEGIN
    SELECT * INTO v FROM cloud_ecran_editeur(p_empreinte_jeton);
    IF v_etat NOT IN ('toutes', 'active', 'en_attente', 'sans_certificat_valable', 'partie') THEN
        RAISE EXCEPTION 'état de filtre inconnu' USING ERRCODE = '22023';
    END IF;
    IF length(v_texte) > 100 THEN v_texte := left(v_texte, 100); END IF;
    SELECT count(*) INTO v_total FROM officine f
    WHERE (v_texte IS NULL OR strpos(lower(f.libelle), lower(v_texte)) > 0) AND (v_etat = 'toutes' OR cloud_ecran_etat_officine(f.id) = v_etat);
    FOR o IN SELECT f.id AS o_id, f.libelle AS o_libelle, cloud_ecran_etat_officine(f.id) AS o_etat, f.cree_le AS o_cree_le FROM officine f
             WHERE (v_texte IS NULL OR strpos(lower(f.libelle), lower(v_texte)) > 0) AND (v_etat = 'toutes' OR cloud_ecran_etat_officine(f.id) = v_etat)
             ORDER BY lower(f.libelle), f.id OFFSET (v_page - 1) * v_taille LIMIT v_taille LOOP
        PERFORM set_config('app.officine_id', o.o_id::text, true);
        SELECT e.dernier_lot, e.epoque INTO v_lot, v_epoque FROM officine_epoque e ORDER BY e.epoque DESC LIMIT 1;
        SELECT greatest((SELECT max(e.dernier_recu_le) FROM officine_epoque e),
                        (SELECT max(c.emis_le) FROM certificat c WHERE c.officine_id = o.o_id)) INTO v_contact;
        v_charg := EXISTS (SELECT 1 FROM chargement c WHERE c.etat = 'en_cours');
        SELECT count(*) INTO v_anom FROM cloud_anomalie_integrite a;
        id := o.o_id; libelle := o.o_libelle; etat := o.o_etat; cree_le := o.o_cree_le; total := v_total;
        dernier_contact := v_contact; en_retard := o.o_etat = 'active' AND (v_contact IS NULL OR v_contact < now() - interval '24 hours');
        dernier_lot := v_lot; derniere_epoque := v_epoque; chargement_en_cours := v_charg; anomalies := v_anom;
        echeance_certificat := (SELECT min(c.echeance) FROM certificat c WHERE c.officine_id = o.o_id AND c.revoque_le IS NULL AND c.echeance > now());
        RETURN NEXT;
        v_lot := NULL; v_epoque := NULL; v_contact := NULL;
    END LOOP;
    PERFORM set_config('app.officine_id', coalesce(v_avant, ''), true);
    PERFORM cloud_ecran_journaliser(v.compte_id, v.session_id, NULL, 'liste_officines', 'ok', NULL);
    IF v_total = 0 OR (v_page - 1) * v_taille >= v_total THEN
        -- Page vide : une ligne sans officine porte quand même le total
        total := v_total; id := NULL; libelle := NULL; etat := NULL; cree_le := NULL; dernier_contact := NULL; en_retard := NULL;
        dernier_lot := NULL; derniere_epoque := NULL; chargement_en_cours := NULL; echeance_certificat := NULL; anomalies := NULL;
        RETURN NEXT;
    END IF;
END $$;

-- Résumé de l'officine ouverte
CREATE OR REPLACE FUNCTION cloud_ecran_fiche(p_empreinte_jeton text)
RETURNS TABLE (id uuid, libelle text, etat text, cree_le timestamptz, resiliee_le timestamptz, dernier_contact timestamptz,
               dernier_lot bigint, derniere_epoque int, chargement_en_cours boolean, echeance_certificat timestamptz, anomalies bigint)
LANGUAGE plpgsql SECURITY DEFINER SET search_path = public, pg_temp AS $$
DECLARE v record;
BEGIN
    SELECT * INTO v FROM cloud_ecran_officine_editeur(p_empreinte_jeton);
    RETURN QUERY
    SELECT o.id, o.libelle, cloud_ecran_etat_officine(o.id), o.cree_le, o.resiliee_le,
           greatest((SELECT max(e.dernier_recu_le) FROM officine_epoque e), (SELECT max(c.emis_le) FROM certificat c WHERE c.officine_id = o.id)),
           (SELECT e.dernier_lot FROM officine_epoque e ORDER BY e.epoque DESC LIMIT 1),
           (SELECT max(e.epoque) FROM officine_epoque e),
           EXISTS (SELECT 1 FROM chargement c WHERE c.etat = 'en_cours'),
           (SELECT min(c.echeance) FROM certificat c WHERE c.officine_id = o.id AND c.revoque_le IS NULL AND c.echeance > now()),
           (SELECT count(*) FROM cloud_anomalie_integrite a)
    FROM officine o WHERE o.id = v.officine_id;
END $$;

-- Certificats (début d'empreinte seulement, jamais le motif de révocation)
CREATE OR REPLACE FUNCTION cloud_ecran_certificats(p_empreinte_jeton text)
RETURNS TABLE (serveur text, empreinte_courte text, emis_le timestamptz, echeance timestamptz, revoque_le timestamptz, remplace_courte text)
LANGUAGE plpgsql SECURITY DEFINER SET search_path = public, pg_temp AS $$
DECLARE v record;
BEGIN
    SELECT * INTO v FROM cloud_ecran_officine_editeur(p_empreinte_jeton);
    RETURN QUERY SELECT c.serveur, left(c.empreinte, 12), c.emis_le, c.echeance, c.revoque_le, left(c.remplace_empreinte, 12)
                 FROM certificat c WHERE c.officine_id = v.officine_id ORDER BY c.emis_le DESC LIMIT 200;
END $$;

-- Codes d'activation (ni le code, ni son empreinte)
CREATE OR REPLACE FUNCTION cloud_ecran_codes_activation(p_empreinte_jeton text)
RETURNS TABLE (serveur text, cree_le timestamptz, expire_le timestamptz, utilise_le timestamptz, certificat_courte text)
LANGUAGE plpgsql SECURITY DEFINER SET search_path = public, pg_temp AS $$
DECLARE v record;
BEGIN
    SELECT * INTO v FROM cloud_ecran_officine_editeur(p_empreinte_jeton);
    RETURN QUERY SELECT a.serveur, a.cree_le, a.expire_le, a.utilise_le, left(a.certificat_empreinte, 12)
                 FROM code_activation a WHERE a.officine_id = v.officine_id ORDER BY a.cree_le DESC LIMIT 200;
END $$;

-- Chargements complets (20 derniers)
CREATE OR REPLACE FUNCTION cloud_ecran_chargements(p_empreinte_jeton text)
RETURNS TABLE (numero int, motif_chargement text, etat text, epoque int, debut_le timestamptz, fin_le timestamptz, lots_abandonnes bigint,
               octets_recus bigint, absentes bigint, lignes_remplacees bigint)
LANGUAGE plpgsql SECURITY DEFINER SET search_path = public, pg_temp AS $$
DECLARE v record;
BEGIN
    SELECT * INTO v FROM cloud_ecran_officine_editeur(p_empreinte_jeton);
    RETURN QUERY SELECT c.numero, c.motif, c.etat, c.epoque, c.debut_le, c.fin_le, c.lots_abandonnes, c.octets_recus::bigint, c.absentes,
                        c.lignes_remplacees::bigint
                 FROM chargement c ORDER BY c.numero DESC LIMIT 20;
END $$;

-- 50 derniers lots reçus (sans contenu)
CREATE OR REPLACE FUNCTION cloud_ecran_lots(p_empreinte_jeton text)
RETURNS TABLE (epoque int, numero bigint, nb_lignes int, octets int, version_contrat text, recu_le timestamptz, expurge_le timestamptz)
LANGUAGE plpgsql SECURITY DEFINER SET search_path = public, pg_temp AS $$
DECLARE v record;
BEGIN
    SELECT * INTO v FROM cloud_ecran_officine_editeur(p_empreinte_jeton);
    RETURN QUERY SELECT l.epoque, l.numero, l.nb_lignes, l.octets, l.version_contrat, l.recu_le, l.expurge_le
                 FROM lot_recu l ORDER BY l.recu_le DESC, l.numero DESC LIMIT 50;
END $$;

-- Époques et changements d'époque déclarés (sans fins de chaîne ni n° d'employé)
CREATE OR REPLACE FUNCTION cloud_ecran_epoques(p_empreinte_jeton text)
RETURNS TABLE (epoque int, dernier_lot bigint, dernier_recu_le timestamptz)
LANGUAGE plpgsql SECURITY DEFINER SET search_path = public, pg_temp AS $$
DECLARE v record;
BEGIN
    SELECT * INTO v FROM cloud_ecran_officine_editeur(p_empreinte_jeton);
    RETURN QUERY SELECT e.epoque, e.dernier_lot, e.dernier_recu_le FROM officine_epoque e ORDER BY e.epoque DESC;
END $$;

CREATE OR REPLACE FUNCTION cloud_ecran_changements_epoque(p_empreinte_jeton text)
RETURNS TABLE (ancienne_epoque int, nouvelle_epoque int, dernier_lot_local bigint, dernier_lot_central bigint, detecte_le timestamptz,
               decide_le timestamptz, avant_premier_lot boolean, recu_le timestamptz)
LANGUAGE plpgsql SECURITY DEFINER SET search_path = public, pg_temp AS $$
DECLARE v record;
BEGIN
    SELECT * INTO v FROM cloud_ecran_officine_editeur(p_empreinte_jeton);
    RETURN QUERY SELECT c.ancienne_epoque, c.nouvelle_epoque, c.dernier_lot_local, c.dernier_lot_central, c.detecte_le, c.decide_le,
                        c.avant_premier_lot, c.recu_le
                 FROM cloud_epoque_changement c ORDER BY c.nouvelle_epoque DESC LIMIT 50;
END $$;

-- Volumes reçus par jour (31 derniers jours)
CREATE OR REPLACE FUNCTION cloud_ecran_volumes(p_empreinte_jeton text)
RETURNS TABLE (jour date, octets bigint)
LANGUAGE plpgsql SECURITY DEFINER SET search_path = public, pg_temp AS $$
DECLARE v record;
BEGIN
    SELECT * INTO v FROM cloud_ecran_officine_editeur(p_empreinte_jeton);
    RETURN QUERY SELECT j.jour, j.octets FROM officine_volume_jour j WHERE j.jour >= current_date - 30 ORDER BY j.jour DESC;
END $$;

-- Intégrité : état des chaînes, et anomalies (nature et rang seulement, jamais détail / attendu / trouvé / clé de ligne)
CREATE OR REPLACE FUNCTION cloud_ecran_integrite_chaines(p_empreinte_jeton text)
RETURNS TABLE (journal text, cle_chaine text, statut text, dernier_rang bigint, rang_rupture bigint, non_verifiables bigint,
               verifie_le timestamptz, verification_complete_le timestamptz)
LANGUAGE plpgsql SECURITY DEFINER SET search_path = public, pg_temp AS $$
DECLARE v record;
BEGIN
    SELECT * INTO v FROM cloud_ecran_officine_editeur(p_empreinte_jeton);
    RETURN QUERY SELECT e.journal, e.cle_chaine, e.statut, e.dernier_rang, e.rang_rupture, e.non_verifiables, e.verifie_le, e.verification_complete_le
                 FROM cloud_integrite_etat e ORDER BY e.journal, e.cle_chaine;
END $$;

CREATE OR REPLACE FUNCTION cloud_ecran_anomalies(p_empreinte_jeton text, p_page int, p_taille int)
RETURNS TABLE (total bigint, id bigint, journal text, cle_chaine text, rang bigint, nature text, detectee_le timestamptz, classe text)
LANGUAGE plpgsql SECURITY DEFINER SET search_path = public, pg_temp AS $$
DECLARE v record; v_taille int := least(greatest(coalesce(p_taille, 20), 10), 100); v_page int := greatest(coalesce(p_page, 1), 1);
BEGIN
    SELECT * INTO v FROM cloud_ecran_officine_editeur(p_empreinte_jeton);
    RETURN QUERY SELECT count(*) OVER (), a.id, a.journal, a.cle_chaine, a.rang, a.nature, a.detectee_le, k.classe
                 FROM cloud_anomalie_integrite a LEFT JOIN cloud_anomalie_classement k ON k.anomalie_id = a.id
                 ORDER BY a.id DESC OFFSET (v_page - 1) * v_taille LIMIT v_taille;
END $$;

-- Restaurations constatées (sans montant)
CREATE OR REPLACE FUNCTION cloud_ecran_restaurations(p_empreinte_jeton text)
RETURNS TABLE (journal text, cle_chaine text, ancienne_epoque int, nouvelle_epoque int, rang_commun bigint, lignes_ancienne bigint,
               sceaux_perdus int, classe text, premiere_date timestamptz, derniere_date timestamptz, constatee_le timestamptz)
LANGUAGE plpgsql SECURITY DEFINER SET search_path = public, pg_temp AS $$
DECLARE v record;
BEGIN
    SELECT * INTO v FROM cloud_ecran_officine_editeur(p_empreinte_jeton);
    RETURN QUERY SELECT b.journal, b.cle_chaine, b.ancienne_epoque, b.nouvelle_epoque, b.rang_commun, b.lignes_ancienne, b.sceaux_perdus,
                        b.classe, b.premiere_date, b.derniere_date, b.constatee_le
                 FROM cloud_integrite_bifurcation b ORDER BY b.constatee_le DESC LIMIT 50;
END $$;

-- Ordres envoyés à l'officine (type et statut seulement : ni données, ni cible, ni auteur, ni motif)
CREATE OR REPLACE FUNCTION cloud_ecran_ordres(p_empreinte_jeton text, p_page int, p_taille int)
RETURNS TABLE (total bigint, id uuid, type text, statut text, cree_le timestamptz, expire_le timestamptz, statut_le timestamptz)
LANGUAGE plpgsql SECURITY DEFINER SET search_path = public, pg_temp AS $$
DECLARE v record; v_taille int := least(greatest(coalesce(p_taille, 20), 10), 100); v_page int := greatest(coalesce(p_page, 1), 1);
BEGIN
    SELECT * INTO v FROM cloud_ecran_officine_editeur(p_empreinte_jeton);
    RETURN QUERY SELECT count(*) OVER (), o.id, o.type, o.statut, o.cree_le, o.expire_le, o.statut_le
                 FROM ordre_central o ORDER BY o.cree_le DESC, o.id OFFSET (v_page - 1) * v_taille LIMIT v_taille;
END $$;

-- Journal de sécurité de l'officine (événements techniques ; début d'empreinte du certificat)
CREATE OR REPLACE FUNCTION cloud_ecran_journal_securite(p_empreinte_jeton text, p_page int, p_taille int)
RETURNS TABLE (total bigint, id bigint, le timestamptz, evenement text, certificat_courte text, detail text)
LANGUAGE plpgsql SECURITY DEFINER SET search_path = public, pg_temp AS $$
DECLARE v record; v_taille int := least(greatest(coalesce(p_taille, 20), 10), 100); v_page int := greatest(coalesce(p_page, 1), 1);
BEGIN
    SELECT * INTO v FROM cloud_ecran_officine_editeur(p_empreinte_jeton);
    RETURN QUERY SELECT count(*) OVER (), j.id, j.le, j.evenement, left(j.certificat_empreinte, 12), j.detail
                 FROM journal_securite j WHERE j.officine_id = v.officine_id ORDER BY j.id DESC OFFSET (v_page - 1) * v_taille LIMIT v_taille;
END $$;

-- Mon journal des accès (compte de la session ; libellé de l'officine pour la lecture)
CREATE OR REPLACE FUNCTION cloud_ecran_mon_journal(p_empreinte_jeton text, p_page int, p_taille int)
RETURNS TABLE (total bigint, id bigint, le timestamptz, evenement text, resultat text, objet text, officine_id uuid, officine text)
LANGUAGE plpgsql SECURITY DEFINER SET search_path = public, pg_temp AS $$
DECLARE v record; v_taille int := least(greatest(coalesce(p_taille, 20), 10), 100); v_page int := greatest(coalesce(p_page, 1), 1);
BEGIN
    SELECT * INTO v FROM cloud_ecran_session_ouverte(p_empreinte_jeton);
    RETURN QUERY SELECT count(*) OVER (), j.id, j.le, j.evenement, j.resultat, j.objet, j.officine_id, o.libelle
                 FROM journal_acces_distant j LEFT JOIN officine o ON o.id = j.officine_id
                 WHERE j.compte_id = v.compte_id ORDER BY j.id DESC OFFSET (v_page - 1) * v_taille LIMIT v_taille;
END $$;

-- ---------------------------------------------------------------------
-- Actions de l'éditeur. Statuts communs : ok, motif, confirmation, serveur, libelle, partie, inconnu, puis ceux du code
-- (faux, expire, epuise, aucun). Les contrôles de saisie passent AVANT le code (un code n'est pas consommé pour une faute de
-- frappe) ; un code faux est compté même si l'action est refusée (la fonction rend un statut, elle ne lève pas d'erreur).
-- ---------------------------------------------------------------------

-- Note l'action aux deux journaux (accès : sans texte libre ; sécurité : avec le motif, comme les révocations de la console)
CREATE OR REPLACE FUNCTION cloud_ecran_noter_action(p_compte uuid, p_session bigint, p_officine uuid, p_evenement text, p_resultat text,
                                                    p_objet text, p_detail text) RETURNS void
LANGUAGE plpgsql SECURITY DEFINER SET search_path = public, pg_temp AS $$
BEGIN
    PERFORM cloud_ecran_journaliser(p_compte, p_session, p_officine, p_evenement, p_resultat, p_objet);
    IF p_resultat = 'ok' THEN
        INSERT INTO journal_securite (officine_id, evenement, detail)
        VALUES (p_officine, p_evenement, left('écran éditeur, compte n° ' || left(p_compte::text, 8) || ' : ' || p_detail, 500));
    END IF;
END $$;

CREATE OR REPLACE FUNCTION cloud_ecran_creer_officine(p_empreinte_jeton text, p_empreinte_code text, p_motif text, p_libelle text,
                                                      p_confirmation text, p_serveur text, p_empreinte_activation text)
RETURNS TABLE (statut text, officine_id uuid, expire_le timestamptz)
LANGUAGE plpgsql SECURITY DEFINER SET search_path = public, pg_temp AS $$
DECLARE v record; v_motif text := cloud_ecran_motif(p_motif); v_libelle text := btrim(coalesce(p_libelle, '')); r text; v_id uuid;
        v_expire timestamptz := now() + interval '24 hours';
BEGIN
    SELECT * INTO v FROM cloud_ecran_editeur(p_empreinte_jeton);
    IF length(v_libelle) NOT BETWEEN 1 AND 200 THEN RETURN QUERY SELECT 'libelle'::text, NULL::uuid, NULL::timestamptz; RETURN; END IF;
    IF v_motif IS NULL THEN RETURN QUERY SELECT 'motif'::text, NULL::uuid, NULL::timestamptz; RETURN; END IF;
    IF btrim(coalesce(p_confirmation, '')) <> v_libelle THEN RETURN QUERY SELECT 'confirmation'::text, NULL::uuid, NULL::timestamptz; RETURN; END IF;
    IF p_serveur NOT IN ('principal', 'second') THEN RETURN QUERY SELECT 'serveur'::text, NULL::uuid, NULL::timestamptz; RETURN; END IF;
    IF p_empreinte_activation IS NULL OR p_empreinte_activation !~ '^[0-9a-f]{64}$' THEN
        RAISE EXCEPTION 'empreinte du code d''activation invalide' USING ERRCODE = '22023';
    END IF;
    r := cloud_ecran_controler_code(v.session_id, 'action', p_empreinte_code);
    IF r <> 'ok' THEN
        PERFORM cloud_ecran_noter_action(v.compte_id, v.session_id, NULL, 'officine_creee_ecran', 'refus', r, NULL);
        RETURN QUERY SELECT r, NULL::uuid, NULL::timestamptz; RETURN;
    END IF;
    INSERT INTO officine (libelle) VALUES (v_libelle) RETURNING id INTO v_id;
    INSERT INTO code_activation (empreinte_code, officine_id, serveur, expire_le) VALUES (p_empreinte_activation, v_id, p_serveur, v_expire);
    PERFORM cloud_ecran_noter_action(v.compte_id, v.session_id, v_id, 'officine_creee_ecran', 'ok', p_serveur,
                                     'officine créée, code d''activation ' || p_serveur || ' (24 h) ; motif : ' || v_motif);
    RETURN QUERY SELECT 'ok'::text, v_id, v_expire;
END $$;

CREATE OR REPLACE FUNCTION cloud_ecran_nouveau_code_activation(p_empreinte_jeton text, p_empreinte_code text, p_motif text,
                                                               p_confirmation text, p_serveur text, p_empreinte_activation text)
RETURNS TABLE (statut text, expire_le timestamptz)
LANGUAGE plpgsql SECURITY DEFINER SET search_path = public, pg_temp AS $$
DECLARE v record; o officine; v_motif text := cloud_ecran_motif(p_motif); r text; v_expire timestamptz := now() + interval '24 hours';
BEGIN
    SELECT * INTO v FROM cloud_ecran_officine_editeur(p_empreinte_jeton);
    SELECT * INTO o FROM officine x WHERE x.id = v.officine_id;
    IF o.resiliee_le IS NOT NULL THEN RETURN QUERY SELECT 'partie'::text, NULL::timestamptz; RETURN; END IF;
    IF v_motif IS NULL THEN RETURN QUERY SELECT 'motif'::text, NULL::timestamptz; RETURN; END IF;
    IF btrim(coalesce(p_confirmation, '')) <> o.libelle THEN RETURN QUERY SELECT 'confirmation'::text, NULL::timestamptz; RETURN; END IF;
    IF p_serveur NOT IN ('principal', 'second') THEN RETURN QUERY SELECT 'serveur'::text, NULL::timestamptz; RETURN; END IF;
    IF p_empreinte_activation IS NULL OR p_empreinte_activation !~ '^[0-9a-f]{64}$' THEN
        RAISE EXCEPTION 'empreinte du code d''activation invalide' USING ERRCODE = '22023';
    END IF;
    r := cloud_ecran_controler_code(v.session_id, 'action', p_empreinte_code);
    IF r <> 'ok' THEN
        PERFORM cloud_ecran_noter_action(v.compte_id, v.session_id, o.id, 'code_activation_ecran', 'refus', r, NULL);
        RETURN QUERY SELECT r, NULL::timestamptz; RETURN;
    END IF;
    INSERT INTO code_activation (empreinte_code, officine_id, serveur, expire_le) VALUES (p_empreinte_activation, o.id, p_serveur, v_expire);
    PERFORM cloud_ecran_noter_action(v.compte_id, v.session_id, o.id, 'code_activation_ecran', 'ok', p_serveur,
                                     'nouveau code d''activation ' || p_serveur || ' (24 h) ; motif : ' || v_motif);
    RETURN QUERY SELECT 'ok'::text, v_expire;
END $$;

CREATE OR REPLACE FUNCTION cloud_ecran_revoquer_certificat(p_empreinte_jeton text, p_empreinte_code text, p_motif text,
                                                           p_empreinte_courte text, p_confirmation text)
RETURNS TABLE (statut text, revoques int)
LANGUAGE plpgsql SECURITY DEFINER SET search_path = public, pg_temp AS $$
DECLARE v record; v_motif text := cloud_ecran_motif(p_motif); r text; v_cert text; n int;
BEGIN
    SELECT * INTO v FROM cloud_ecran_officine_editeur(p_empreinte_jeton);
    IF p_empreinte_courte IS NULL OR p_empreinte_courte !~ '^[0-9a-f]{12}$' THEN RETURN QUERY SELECT 'inconnu'::text, 0; RETURN; END IF;
    IF v_motif IS NULL THEN RETURN QUERY SELECT 'motif'::text, 0; RETURN; END IF;
    IF lower(btrim(coalesce(p_confirmation, ''))) <> p_empreinte_courte THEN RETURN QUERY SELECT 'confirmation'::text, 0; RETURN; END IF;
    IF (SELECT count(*) FROM certificat c WHERE c.officine_id = v.officine_id AND left(c.empreinte, 12) = p_empreinte_courte AND c.revoque_le IS NULL) <> 1 THEN
        RETURN QUERY SELECT 'inconnu'::text, 0; RETURN;
    END IF;
    r := cloud_ecran_controler_code(v.session_id, 'action', p_empreinte_code);
    IF r <> 'ok' THEN
        PERFORM cloud_ecran_noter_action(v.compte_id, v.session_id, v.officine_id, 'revocation_ecran', 'refus', r, NULL);
        RETURN QUERY SELECT r, 0; RETURN;
    END IF;
    UPDATE certificat c SET revoque_le = now(), motif_revocation = left('écran éditeur : ' || v_motif, 500)
    WHERE c.officine_id = v.officine_id AND left(c.empreinte, 12) = p_empreinte_courte AND c.revoque_le IS NULL
    RETURNING c.empreinte INTO v_cert;
    -- Successeurs révoqués en cascade par le déclencheur du 001
    SELECT count(*) INTO n FROM certificat c WHERE c.officine_id = v.officine_id AND c.revoque_le >= now() AND c.revoque_le IS NOT NULL;
    PERFORM cloud_ecran_noter_action(v.compte_id, v.session_id, v.officine_id, 'revocation_ecran', 'ok', p_empreinte_courte,
                                     'certificat ' || p_empreinte_courte || ' révoqué ; motif : ' || v_motif);
    RETURN QUERY SELECT 'ok'::text, n;
END $$;

CREATE OR REPLACE FUNCTION cloud_ecran_revoquer_serveur(p_empreinte_jeton text, p_empreinte_code text, p_motif text, p_serveur text,
                                                        p_confirmation text)
RETURNS TABLE (statut text, revoques int)
LANGUAGE plpgsql SECURITY DEFINER SET search_path = public, pg_temp AS $$
DECLARE v record; o officine; v_motif text := cloud_ecran_motif(p_motif); r text; n int;
BEGIN
    SELECT * INTO v FROM cloud_ecran_officine_editeur(p_empreinte_jeton);
    SELECT * INTO o FROM officine x WHERE x.id = v.officine_id;
    IF p_serveur NOT IN ('principal', 'second') THEN RETURN QUERY SELECT 'serveur'::text, 0; RETURN; END IF;
    IF v_motif IS NULL THEN RETURN QUERY SELECT 'motif'::text, 0; RETURN; END IF;
    IF btrim(coalesce(p_confirmation, '')) <> o.libelle THEN RETURN QUERY SELECT 'confirmation'::text, 0; RETURN; END IF;
    IF NOT EXISTS (SELECT 1 FROM certificat c WHERE c.officine_id = o.id AND c.serveur = p_serveur AND c.revoque_le IS NULL) THEN
        RETURN QUERY SELECT 'inconnu'::text, 0; RETURN;
    END IF;
    r := cloud_ecran_controler_code(v.session_id, 'action', p_empreinte_code);
    IF r <> 'ok' THEN
        PERFORM cloud_ecran_noter_action(v.compte_id, v.session_id, o.id, 'revocation_serveur_ecran', 'refus', r, NULL);
        RETURN QUERY SELECT r, 0; RETURN;
    END IF;
    -- Fonction de la console (001) : révocation et ligne « revocation » au journal de sécurité
    n := cloud_revoquer_serveur(o.id, p_serveur, left('écran éditeur : ' || v_motif, 450));
    PERFORM cloud_ecran_journaliser(v.compte_id, v.session_id, o.id, 'revocation_serveur_ecran', 'ok', p_serveur);
    RETURN QUERY SELECT 'ok'::text, n;
END $$;

-- ---- Droits : rien pour PUBLIC ; les écrans n'exécutent que les fonctions de lecture et d'action ----
DO $droits$
DECLARE f regprocedure;
BEGIN
    FOR f IN SELECT p.oid::regprocedure FROM pg_proc p
             WHERE p.pronamespace = 'public'::regnamespace AND p.proname LIKE 'cloud\_ecran\_%'
    LOOP
        EXECUTE format('REVOKE ALL ON FUNCTION %s FROM PUBLIC', f);
    END LOOP;
END $droits$;
GRANT EXECUTE ON FUNCTION cloud_ecran_officines(text, text, text, int, int) TO pharma_cloud_ecrans;
GRANT EXECUTE ON FUNCTION cloud_ecran_fiche(text) TO pharma_cloud_ecrans;
GRANT EXECUTE ON FUNCTION cloud_ecran_certificats(text) TO pharma_cloud_ecrans;
GRANT EXECUTE ON FUNCTION cloud_ecran_codes_activation(text) TO pharma_cloud_ecrans;
GRANT EXECUTE ON FUNCTION cloud_ecran_chargements(text) TO pharma_cloud_ecrans;
GRANT EXECUTE ON FUNCTION cloud_ecran_lots(text) TO pharma_cloud_ecrans;
GRANT EXECUTE ON FUNCTION cloud_ecran_epoques(text) TO pharma_cloud_ecrans;
GRANT EXECUTE ON FUNCTION cloud_ecran_changements_epoque(text) TO pharma_cloud_ecrans;
GRANT EXECUTE ON FUNCTION cloud_ecran_volumes(text) TO pharma_cloud_ecrans;
GRANT EXECUTE ON FUNCTION cloud_ecran_integrite_chaines(text) TO pharma_cloud_ecrans;
GRANT EXECUTE ON FUNCTION cloud_ecran_anomalies(text, int, int) TO pharma_cloud_ecrans;
GRANT EXECUTE ON FUNCTION cloud_ecran_restaurations(text) TO pharma_cloud_ecrans;
GRANT EXECUTE ON FUNCTION cloud_ecran_ordres(text, int, int) TO pharma_cloud_ecrans;
GRANT EXECUTE ON FUNCTION cloud_ecran_journal_securite(text, int, int) TO pharma_cloud_ecrans;
GRANT EXECUTE ON FUNCTION cloud_ecran_mon_journal(text, int, int) TO pharma_cloud_ecrans;
GRANT EXECUTE ON FUNCTION cloud_ecran_creer_officine(text, text, text, text, text, text, text) TO pharma_cloud_ecrans;
GRANT EXECUTE ON FUNCTION cloud_ecran_nouveau_code_activation(text, text, text, text, text, text) TO pharma_cloud_ecrans;
GRANT EXECUTE ON FUNCTION cloud_ecran_revoquer_certificat(text, text, text, text, text) TO pharma_cloud_ecrans;
GRANT EXECUTE ON FUNCTION cloud_ecran_revoquer_serveur(text, text, text, text, text) TO pharma_cloud_ecrans;
