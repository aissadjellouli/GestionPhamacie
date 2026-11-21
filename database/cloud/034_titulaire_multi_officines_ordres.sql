-- =====================================================================
-- Base centrale, lot S7.3 (2026-11-21, go du titulaire, recommandations
-- S7 acceptées) : multi-officines et ordres à distance du TITULAIRE
-- (docs/ecrans-central.md § 3 quater). Script de la base CENTRALE, passé
-- par le propriétaire, idempotent ; à passer après 033.
--   1. compte_officine.droits : nouveau droit « ordres ». Il n'est JAMAIS
--      accordé par les écrans ni par le rattachement (qui donne
--      consulter + journal_acces) : seulement par le poste de l'officine
--      (titulaire local, mot de passe ressaisi), fonction 6 ci-dessous.
--   2. ordre_central : origine (console | titulaire) et compte distant
--      auteur ; deux types de plus, « sauvegarde » et
--      « verification_integrite ». Liste FERMÉE des ordres du titulaire :
--      essai, sauvegarde, verification_integrite (aucun ne modifie une
--      vente, un stock, un prix ni un compte). Ordre du titulaire : 24 h.
--   3. cloud_expirer_ordres : motif selon le délai réel (24 h ou 7 jours).
--   4. Vue d'ensemble du titulaire (cloud_ecran_titulaire_ensemble) : une
--      ligne d'agrégats par officine du compte (droit « consulter »),
--      lue par cloud_ecran_titulaire_tableau (officine posée par la base,
--      RLS forcée) ; le total du groupe est calculé par l'écran, jamais
--      avec un « non disponible » compté comme 0.
--   5. Ordres du titulaire : liste (type, statut, dates ; jamais les
--      données, le motif ni l'auteur) et création (droit « ordres »,
--      liste fermée, un seul ordre ouvert par type et par officine,
--      10 ordres par heure et par compte, code de confirmation à usage
--      unique, journaux des accès (chaîné) et de sécurité, journal des
--      ordres, publication par la voie descendante existante ordre-v1).
--   6. Poste de l'officine (rôle pharma_cloud_api, certificat revérifié
--      comme en 033) : liste des comptes rattachés, droits, retrait. Le
--      retrait (ou le retrait du droit « ordres ») annule les ordres
--      encore ouverts de ce compte pour l'officine.
-- Aucune donnée existante n'est lue, modifiée ni supprimée par ce script
-- (colonnes ajoutées avec une valeur par défaut, contraintes élargies).
-- =====================================================================

SET client_encoding = 'UTF8';

DO $version$ BEGIN
    IF to_regprocedure('public.cloud_poste_code_rattachement(text,text,text)') IS NULL
       OR to_regprocedure('public.cloud_ecran_titulaire_tableau(text,uuid)') IS NULL
       OR to_regclass('public.ordre_central') IS NULL THEN
        RAISE EXCEPTION 'Script central 034 : passer d''abord les scripts 013 et 030 à 033. Rien n''a été modifié.';
    END IF;
END $version$;

-- ---- 1. Droit « ordres » ----
DO $$ BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'ck_compte_officine_droits' AND conrelid = 'compte_officine'::regclass
                   AND pg_get_constraintdef(oid) LIKE '%''ordres''%') THEN
        ALTER TABLE compte_officine DROP CONSTRAINT IF EXISTS ck_compte_officine_droits;
        ALTER TABLE compte_officine ADD CONSTRAINT ck_compte_officine_droits
            CHECK (cardinality(droits) >= 1 AND droits <@ ARRAY['consulter', 'journal_acces', 'ordres']::text[]);
    END IF;
END $$;

-- ---- 2. Ordres : origine, compte auteur, types du titulaire ----
ALTER TABLE ordre_central ADD COLUMN IF NOT EXISTS origine text NOT NULL DEFAULT 'console';
ALTER TABLE ordre_central ADD COLUMN IF NOT EXISTS compte_id uuid REFERENCES compte_distant (id);
DO $$ BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'ck_ordre_central_origine' AND conrelid = 'ordre_central'::regclass) THEN
        ALTER TABLE ordre_central ADD CONSTRAINT ck_ordre_central_origine
            CHECK (origine IN ('console', 'titulaire') AND (origine = 'console') = (compte_id IS NULL));
    END IF;
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'ck_ordre_central_type' AND pg_get_constraintdef(oid) LIKE '%''sauvegarde''%') THEN
        ALTER TABLE ordre_central DROP CONSTRAINT IF EXISTS ck_ordre_central_type;
        ALTER TABLE ordre_central ADD CONSTRAINT ck_ordre_central_type CHECK (type IN ('essai', 'employe_creer', 'employe_modifier',
            'employe_bloquer', 'employe_debloquer', 'droits_role', 'prix_produit', 'nomenclature', 'commande', 'classe_suivie', 'organismes',
            'prestations', 'indications', 'dci', 'sauvegarde', 'verification_integrite'));
    END IF;
    -- Liste fermée des ordres du titulaire, revérifiée par la table elle-même
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'ck_ordre_central_titulaire' AND conrelid = 'ordre_central'::regclass) THEN
        ALTER TABLE ordre_central ADD CONSTRAINT ck_ordre_central_titulaire
            CHECK (origine <> 'titulaire' OR (type IN ('essai', 'sauvegarde', 'verification_integrite') AND donnees = '{}'::jsonb
                                              AND cible IS NULL AND expire_le <= cree_le + interval '24 hours'));
    END IF;
END $$;
CREATE INDEX IF NOT EXISTS ix_ordre_central_compte ON ordre_central (compte_id, cree_le) WHERE compte_id IS NOT NULL;

-- ---- 3. Expiration : motif selon le délai réel (remplace la version de 013 ; garde de rejeu posée dans 013) ----
CREATE OR REPLACE FUNCTION cloud_expirer_ordres() RETURNS integer
LANGUAGE plpgsql AS $$
DECLARE f record; o record; n int := 0; v_motif text;
BEGIN
    FOR f IN SELECT id FROM officine ORDER BY cree_le LOOP
        PERFORM set_config('app.officine_id', f.id::text, true);
        FOR o IN SELECT id, origine FROM ordre_central WHERE statut IN ('en_attente', 'a_confirmer') AND expire_le <= now() FOR UPDATE LOOP
            v_motif := CASE o.origine WHEN 'titulaire' THEN 'non traité dans les 24 heures' ELSE 'non traité dans les 7 jours' END;
            UPDATE ordre_central SET statut = 'expire', motif = v_motif, statut_le = now() WHERE id = o.id;
            INSERT INTO ordre_central_journal (officine_id, ordre_id, statut, motif, par) VALUES (f.id, o.id, 'expire', v_motif, 'serveur central');
            PERFORM cloud_ordre_publier(o.id);
            n := n + 1;
        END LOOP;
    END LOOP;
    PERFORM set_config('app.officine_id', '', true);
    RETURN n;
END $$;
REVOKE EXECUTE ON FUNCTION cloud_expirer_ordres() FROM PUBLIC;

-- ---------------------------------------------------------------------
-- 4. Vue d'ensemble du titulaire (écrans)
-- ---------------------------------------------------------------------

-- Une ligne d'agrégats par officine du compte (droit « consulter », rattachement non retiré ; 50 officines au plus, par nom) :
-- chaque ligne vient de cloud_ecran_titulaire_tableau (officine ouverte PAR LA BASE, RLS forcée ; ouverture notée au journal de
-- chaque officine). NULL = non disponible (jamais 0). Le total du groupe est fait par l'écran (partiel si une valeur manque).
CREATE OR REPLACE FUNCTION cloud_ecran_titulaire_ensemble(p_empreinte_jeton text)
RETURNS TABLE (id uuid, libelle text, etat text, fuseau text, dernier_contact timestamptz, en_retard boolean, dernier_lot bigint,
               chargement_complet_le timestamptz, chargement_en_cours boolean,
               ca_jour numeric, ventes_jour int, ca_mois numeric, ventes_mois int, ca_mois_precedent numeric, part_chifa_mois numeric,
               credits_en_cours numeric, alertes_stock int, alertes_peremption int, anomalies_ouvertes bigint, ordres_en_attente bigint)
LANGUAGE plpgsql SECURITY DEFINER SET search_path = public, pg_temp AS $$
DECLARE v record; o record; n int := 0; v_avant text := current_setting('app.officine_id', true);
BEGIN
    SELECT * INTO v FROM cloud_ecran_titulaire(p_empreinte_jeton);
    FOR o IN SELECT x.officine_id AS o_id FROM compte_officine x JOIN officine f ON f.id = x.officine_id
             WHERE x.compte_id = v.compte_id AND x.retire_le IS NULL AND 'consulter' = ANY (x.droits)
             ORDER BY lower(f.libelle), f.id LIMIT 50 LOOP
        RETURN QUERY SELECT * FROM cloud_ecran_titulaire_tableau(p_empreinte_jeton, o.o_id);
        n := n + 1;
    END LOOP;
    PERFORM set_config('app.officine_id', coalesce(v_avant, ''), true);
    PERFORM cloud_ecran_journaliser(v.compte_id, v.session_id, NULL, 'vue_ensemble_titulaire', 'ok', 'officines:' || n);
END $$;

-- ---------------------------------------------------------------------
-- 5. Ordres du titulaire (écrans)
-- ---------------------------------------------------------------------

-- Liste fermée des ordres qu'un titulaire peut envoyer (aucun ne modifie une vente, un stock, un prix ni un compte)
CREATE OR REPLACE FUNCTION cloud_ordre_titulaire_permis(p_type text) RETURNS boolean
LANGUAGE sql IMMUTABLE SET search_path = public, pg_temp AS $$
    SELECT coalesce(p_type IN ('essai', 'sauvegarde', 'verification_integrite'), false)
$$;

-- Ordres du titulaire pour une officine (droit « ordres ») : type, statut, dates, auteur « vous » ou « titulaire » (un autre
-- compte) ; jamais les données, le motif, la cible ni l'identifiant. Un ordre ouvert dont l'échéance est passée est montré
-- « expire » (l'officine ne l'applique plus : elle contrôle aussi l'échéance avec sa propre horloge). Page de 10 à 100 lignes.
CREATE OR REPLACE FUNCTION cloud_ecran_titulaire_ordres(p_empreinte_jeton text, p_officine uuid, p_page int, p_taille int)
RETURNS TABLE (total bigint, id uuid, type text, cree_le timestamptz, expire_le timestamptz, statut text, statut_le timestamptz, acteur text)
LANGUAGE plpgsql SECURITY DEFINER SET search_path = public, pg_temp AS $$
DECLARE v record; v_taille int := least(greatest(coalesce(p_taille, 20), 10), 100); v_page int := greatest(coalesce(p_page, 1), 1);
BEGIN
    SELECT * INTO v FROM cloud_ecran_ouvrir_titulaire(p_empreinte_jeton, p_officine, 'ordres', 'ordres_titulaire');
    IF NOT v.ouverte THEN RETURN; END IF;
    RETURN QUERY SELECT count(*) OVER (), o.id, o.type, o.cree_le, o.expire_le,
                        CASE WHEN o.statut IN ('en_attente', 'a_confirmer') AND o.expire_le <= now() THEN 'expire' ELSE o.statut END,
                        o.statut_le, CASE WHEN o.compte_id = v.compte_id THEN 'vous' ELSE 'titulaire' END
                 FROM ordre_central o
                 WHERE o.officine_id = p_officine AND o.origine = 'titulaire'
                 ORDER BY o.cree_le DESC, o.id OFFSET (v_page - 1) * v_taille LIMIT v_taille;
END $$;

-- Création d'un ordre par le titulaire. Statuts : ok, refuse (officine non rattachée ou droit « ordres » absent), hors_liste,
-- partie, deja_en_cours, trop_d_ordres, puis ceux du code de confirmation (faux, expire, epuise, aucun). Les contrôles passent
-- AVANT le code (une erreur ne consomme pas le code) ; chaque refus est noté au journal des accès.
CREATE OR REPLACE FUNCTION cloud_ecran_titulaire_creer_ordre(p_empreinte_jeton text, p_officine uuid, p_type text, p_empreinte_code text)
RETURNS TABLE (statut text, ordre_id uuid)
LANGUAGE plpgsql SECURITY DEFINER SET search_path = public, pg_temp AS $$
DECLARE v record; c compte_distant; o officine; r text; v_id uuid; v_auteur text;
BEGIN
    SELECT * INTO v FROM cloud_ecran_titulaire(p_empreinte_jeton);
    -- Deux demandes simultanées du même compte : comptées l'une après l'autre
    PERFORM pg_advisory_xact_lock(hashtextextended('ordre_titulaire:' || v.compte_id::text, 0));
    IF (SELECT count(*) FROM journal_acces_distant j WHERE j.compte_id = v.compte_id AND j.evenement = 'ordre_titulaire'
        AND j.resultat = 'ok' AND j.le > now() - interval '1 hour') >= 10 THEN
        PERFORM cloud_ecran_journaliser(v.compte_id, v.session_id, NULL, 'ordre_titulaire', 'refus', 'trop_d_ordres');
        RETURN QUERY SELECT 'trop_d_ordres'::text, NULL::uuid; RETURN;
    END IF;
    IF NOT cloud_ordre_titulaire_permis(p_type) THEN
        PERFORM cloud_ecran_journaliser(v.compte_id, v.session_id, NULL, 'ordre_titulaire', 'refus', 'hors_liste');
        RETURN QUERY SELECT 'hors_liste'::text, NULL::uuid; RETURN;
    END IF;
    -- Rattachement non retiré et droit « ordres » (refus noté par l'ouverture), officine posée par la base
    IF NOT (SELECT x.ouverte FROM cloud_ecran_ouvrir_titulaire(p_empreinte_jeton, p_officine, 'ordres', 'ordre_titulaire') x) THEN
        RETURN QUERY SELECT 'refuse'::text, NULL::uuid; RETURN;
    END IF;
    SELECT * INTO o FROM officine x WHERE x.id = p_officine;
    r := CASE WHEN o.resiliee_le IS NOT NULL THEN 'partie'
              WHEN EXISTS (SELECT 1 FROM ordre_central x WHERE x.officine_id = p_officine AND x.origine = 'titulaire' AND x.type = p_type
                           AND x.statut IN ('en_attente', 'a_confirmer') AND x.expire_le > now()) THEN 'deja_en_cours' END;
    IF r IS NULL THEN
        r := nullif(cloud_ecran_controler_code(v.session_id, 'action', p_empreinte_code), 'ok');
    END IF;
    IF r IS NOT NULL THEN
        PERFORM cloud_ecran_journaliser(v.compte_id, v.session_id, p_officine, 'ordre_titulaire', 'refus', p_type || ':' || r);
        RETURN QUERY SELECT r, NULL::uuid; RETURN;
    END IF;
    SELECT * INTO c FROM compte_distant x WHERE x.id = v.compte_id;
    v_auteur := left('titulaire à distance (' || c.identifiant || ')', 100);
    INSERT INTO ordre_central (officine_id, type, cible, attendu, donnees, auteur, expire_le, origine, compte_id)
    VALUES (p_officine, p_type, NULL, '{}'::jsonb, '{}'::jsonb, v_auteur, now() + interval '24 hours', 'titulaire', v.compte_id)
    RETURNING id INTO v_id;
    INSERT INTO ordre_central_journal (officine_id, ordre_id, statut, par) VALUES (p_officine, v_id, 'en_attente', v_auteur);
    PERFORM cloud_ecran_journaliser(v.compte_id, v.session_id, p_officine, 'ordre_titulaire', 'ok', p_type);
    PERFORM cloud_journaliser(p_officine, NULL, 'ordre_cree',
        left(format('ordre %s (%s) par l''écran du titulaire, compte n° %s, échéance 24 h', v_id, p_type, left(v.compte_id::text, 8)), 500));
    PERFORM cloud_ordre_publier(v_id);
    RETURN QUERY SELECT 'ok'::text, v_id;
END $$;

-- ---------------------------------------------------------------------
-- 6. Poste de l'officine : comptes rattachés, droits, retrait (API centrale, rôle pharma_cloud_api)
-- ---------------------------------------------------------------------

-- Officine du certificat présenté (posée par l'API et revérifiée ici : valable, non révoqué, officine non partie) ; sinon 42501
CREATE OR REPLACE FUNCTION cloud_poste_officine(p_certificat text) RETURNS uuid
LANGUAGE plpgsql STABLE SECURITY DEFINER SET search_path = public, pg_temp AS $$
DECLARE v_off uuid := cloud_officine_courante();
BEGIN
    IF v_off IS NULL OR p_certificat IS NULL OR NOT EXISTS (
        SELECT 1 FROM certificat k JOIN officine o ON o.id = k.officine_id
        WHERE k.empreinte = p_certificat AND k.officine_id = v_off AND k.revoque_le IS NULL AND k.echeance > now() AND o.resiliee_le IS NULL) THEN
        RAISE EXCEPTION 'certificat valable de l''officine exigé' USING ERRCODE = '42501';
    END IF;
    RETURN v_off;
END $$;

-- Annule les ordres encore ouverts d'un compte pour l'officine (retrait du compte ou du droit « ordres ») ; rend leur nombre
CREATE OR REPLACE FUNCTION cloud_ordres_compte_annuler(p_officine uuid, p_compte uuid) RETURNS int
LANGUAGE plpgsql SET search_path = public, pg_temp AS $$
DECLARE o record; n int := 0;
BEGIN
    FOR o IN SELECT x.id FROM ordre_central x WHERE x.officine_id = p_officine AND x.compte_id = p_compte
             AND x.statut IN ('en_attente', 'a_confirmer') ORDER BY x.cree_le LOOP
        PERFORM cloud_annuler_ordre(p_officine, o.id, 'poste de l''officine (accès retiré)');
        n := n + 1;
    END LOOP;
    RETURN n;
END $$;

-- Comptes distants rattachés à l'officine du certificat (non retirés) : identifiant, nom, droits, rattaché le, compte actif
CREATE OR REPLACE FUNCTION cloud_poste_comptes_distants(p_certificat text)
RETURNS TABLE (identifiant text, nom text, droits text, rattache_le timestamptz, actif boolean)
LANGUAGE plpgsql SECURITY DEFINER SET search_path = public, pg_temp AS $$
DECLARE v_off uuid := cloud_poste_officine(p_certificat);
BEGIN
    PERFORM cloud_ecran_journaliser(NULL, NULL, v_off, 'comptes_poste', 'ok', 'poste');
    RETURN QUERY SELECT c.identifiant, c.nom, array_to_string(x.droits, ','), x.accorde_le, c.actif
                 FROM compte_officine x JOIN compte_distant c ON c.id = x.compte_id
                 WHERE x.officine_id = v_off AND x.retire_le IS NULL
                 ORDER BY c.identifiant LIMIT 200;
END $$;

-- Droits d'un compte rattaché, décidés par le titulaire au poste (mot de passe vérifié par l'API locale). Statuts : ok, inconnu
-- (identifiant mal formé, compte non rattaché ou retiré), droits_invalides (vide ou hors de consulter, journal_acces, ordres).
-- Le retrait du droit « ordres » annule les ordres encore ouverts de ce compte pour l'officine.
CREATE OR REPLACE FUNCTION cloud_poste_compte_droits(p_certificat text, p_identifiant text, p_droits text[])
RETURNS TABLE (statut text, ordres_annules int)
LANGUAGE plpgsql SECURITY DEFINER SET search_path = public, pg_temp AS $$
DECLARE v_off uuid := cloud_poste_officine(p_certificat); v_ident text := lower(btrim(coalesce(p_identifiant, ''))); c compte_distant;
        v_droits text[]; v_avant text[]; n int := 0;
BEGIN
    SELECT coalesce(array_agg(DISTINCT d ORDER BY d), '{}') INTO v_droits FROM unnest(coalesce(p_droits, '{}')) d;
    IF cardinality(v_droits) = 0 OR NOT v_droits <@ ARRAY['consulter', 'journal_acces', 'ordres']::text[] THEN
        PERFORM cloud_ecran_journaliser(NULL, NULL, v_off, 'droits_compte_poste', 'refus', 'poste:droits');
        RETURN QUERY SELECT 'droits_invalides'::text, 0; RETURN;
    END IF;
    IF v_ident ~ '^[a-z0-9][a-z0-9._-]{2,59}$' THEN
        SELECT * INTO c FROM compte_distant x WHERE x.identifiant = v_ident;
    END IF;
    SELECT x.droits INTO v_avant FROM compte_officine x WHERE x.compte_id = c.id AND x.officine_id = v_off AND x.retire_le IS NULL FOR UPDATE;
    IF c.id IS NULL OR v_avant IS NULL THEN
        PERFORM cloud_ecran_journaliser(NULL, NULL, v_off, 'droits_compte_poste', 'refus', 'poste');
        RETURN QUERY SELECT 'inconnu'::text, 0; RETURN;
    END IF;
    UPDATE compte_officine x SET droits = v_droits WHERE x.compte_id = c.id AND x.officine_id = v_off;
    IF 'ordres' = ANY (v_avant) AND NOT 'ordres' = ANY (v_droits) THEN n := cloud_ordres_compte_annuler(v_off, c.id); END IF;
    PERFORM cloud_ecran_journaliser(c.id, NULL, v_off, 'droits_compte_poste', 'ok', 'poste:' || array_to_string(v_droits, '.'));
    INSERT INTO journal_securite (officine_id, certificat_empreinte, evenement, detail)
    VALUES (v_off, p_certificat, 'droits_compte_distant',
            left('poste de l''officine : droits du compte n° ' || left(c.id::text, 8) || ' : ' || array_to_string(v_avant, ', ') || ' → '
                 || array_to_string(v_droits, ', ') || CASE WHEN n > 0 THEN ' ; ' || n || ' ordre(s) ouvert(s) annulé(s)' ELSE '' END, 500));
    RETURN QUERY SELECT 'ok'::text, n;
END $$;

-- Retrait d'un compte rattaché par le titulaire au poste : effet IMMÉDIAT (chaque fonction des écrans revérifie le rattachement à
-- chaque appel) ; ordres encore ouverts de ce compte annulés. Statuts : ok, inconnu.
CREATE OR REPLACE FUNCTION cloud_poste_compte_retirer(p_certificat text, p_identifiant text)
RETURNS TABLE (statut text, ordres_annules int)
LANGUAGE plpgsql SECURITY DEFINER SET search_path = public, pg_temp AS $$
DECLARE v_off uuid := cloud_poste_officine(p_certificat); v_ident text := lower(btrim(coalesce(p_identifiant, ''))); c compte_distant; n int := 0;
        k int;
BEGIN
    IF v_ident ~ '^[a-z0-9][a-z0-9._-]{2,59}$' THEN
        SELECT * INTO c FROM compte_distant x WHERE x.identifiant = v_ident;
    END IF;
    UPDATE compte_officine x SET retire_le = now() WHERE x.compte_id = c.id AND x.officine_id = v_off AND x.retire_le IS NULL;
    GET DIAGNOSTICS k = ROW_COUNT;
    IF c.id IS NULL OR k = 0 THEN
        PERFORM cloud_ecran_journaliser(NULL, NULL, v_off, 'retrait_compte_poste', 'refus', 'poste');
        RETURN QUERY SELECT 'inconnu'::text, 0; RETURN;
    END IF;
    n := cloud_ordres_compte_annuler(v_off, c.id);
    PERFORM cloud_ecran_journaliser(c.id, NULL, v_off, 'retrait_compte_poste', 'ok', 'poste');
    INSERT INTO journal_securite (officine_id, certificat_empreinte, evenement, detail)
    VALUES (v_off, p_certificat, 'retrait_compte_distant',
            left('poste de l''officine : compte n° ' || left(c.id::text, 8) || ' retiré' || CASE WHEN n > 0 THEN ' ; ' || n || ' ordre(s) ouvert(s) annulé(s)' ELSE '' END, 500));
    RETURN QUERY SELECT 'ok'::text, n;
END $$;

-- ---- Droits : rien pour PUBLIC ; écrans : fonctions du titulaire ; API : fonctions du poste ----
REVOKE ALL ON FUNCTION cloud_ecran_titulaire_ensemble(text), cloud_ecran_titulaire_ordres(text, uuid, int, int),
    cloud_ecran_titulaire_creer_ordre(text, uuid, text, text), cloud_ordre_titulaire_permis(text), cloud_poste_officine(text),
    cloud_ordres_compte_annuler(uuid, uuid), cloud_poste_comptes_distants(text), cloud_poste_compte_droits(text, text, text[]),
    cloud_poste_compte_retirer(text, text) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION cloud_ecran_titulaire_ensemble(text) TO pharma_cloud_ecrans;
GRANT EXECUTE ON FUNCTION cloud_ecran_titulaire_ordres(text, uuid, int, int) TO pharma_cloud_ecrans;
GRANT EXECUTE ON FUNCTION cloud_ecran_titulaire_creer_ordre(text, uuid, text, text) TO pharma_cloud_ecrans;
GRANT EXECUTE ON FUNCTION cloud_poste_comptes_distants(text) TO pharma_cloud_api;
GRANT EXECUTE ON FUNCTION cloud_poste_compte_droits(text, text, text[]) TO pharma_cloud_api;
GRANT EXECUTE ON FUNCTION cloud_poste_compte_retirer(text, text) TO pharma_cloud_api;

COMMENT ON COLUMN ordre_central.origine IS 'console (commandes du propriétaire) ou titulaire (écrans, liste fermée, 24 h), script 034.';
COMMENT ON COLUMN ordre_central.compte_id IS 'Compte distant titulaire auteur d''un ordre des écrans, script 034.';
