-- =====================================================================
-- Base centrale, import de comptes distants, durée de vie des mots de
-- passe provisoires et envoi de l'identifiant par e-mail (2026-11-21, go
-- du titulaire) : docs/ecrans-central.md § 3 decies. Script de la base
-- CENTRALE, passé par le propriétaire, idempotent ; à passer après 039.
--   1. Durée de vie d'un mot de passe PROVISOIRE non utilisé : colonne
--      parametre_envoi_codes.duree_provisoire_jours (1 à 30 jours, 7 par
--      défaut), réglée par l'éditeur dans l'écran « Envoi des codes » avec
--      un code de confirmation. À la connexion, un compte au mot de passe
--      provisoire dont coalesce(mot_de_passe_change_le, cree_le) + durée
--      est passé est REFUSÉ (statut provisoire_expire, noté au journal
--      des accès) : l'éditeur régénère un mot de passe (remise à zéro du
--      038, qui repart pour une durée complète). S'applique à tout compte
--      provisoire : écran, import, console, remise à zéro.
--      cloud_ecran_fin_connexion (030) est REMPLACÉE : corps du 030 repris
--      à l'identique, seul le contrôle d'expiration est ajouté (après la
--      vérification réussie du mot de passe : un mot de passe faux reste
--      « identifiant ou mot de passe incorrect »).
--   2. Import de comptes (fichier CSV lu par les écrans ou la console) :
--      contrôle des lignes (forme, doublon dans le fichier, identifiant
--      déjà pris, nom, rôle, contact) SANS code ; création en lot TOUT OU
--      RIEN avec UN SEUL code de confirmation pour tout le fichier (écrans)
--      ou par la console (propriétaire, sans code). Mots de passe
--      provisoires tirés par l'application : seules leurs empreintes
--      BCrypt arrivent ici.
--   3. Liste des comptes de l'éditeur avec l'état du mot de passe
--      (provisoire jusqu'au…, provisoire expiré), contact d'un compte TOUT
--      JUSTE remis à zéro (envoi de l'identifiant) et note des envois de
--      l'identifiant (e-mail) et du mot de passe (SMS) au journal des
--      accès (résultat seulement, jamais l'adresse, le numéro ni le mot
--      de passe).
--   4. Déconnexion après INACTIVITÉ, réglée par l'éditeur par rôle (écran
--      « Envoi des codes », 5 à 60 min ; éditeur 15, titulaire 30 par
--      défaut), code de confirmation. Décision du titulaire du 2026-11-21 :
--      un utilisateur actif n'est jamais déconnecté : plus de durée
--      maximale pour une session OUVERTE (2 h / 8 h abandonnées), échéance
--      glissante repoussée à chaque requête ; étapes code et mot de passe :
--      échéance courte inchangée. cloud_ecran_session_valable (030) est
--      REMPLACÉE (corps repris, seuls ces deux points changent) ; motif de
--      fin « inactivite » ajouté à la contrainte de session_distante.
-- Journaux : jamais l'identifiant, le nom, l'e-mail, le téléphone, le mot
-- de passe ni son empreinte. Aucune donnée existante n'est modifiée ni
-- supprimée (colonnes ajoutées avec leur valeur par défaut, contrainte
-- élargie, fonctions ajoutées, deux fonctions remplacées).
-- =====================================================================

SET client_encoding = 'UTF8';

DO $version$ BEGIN
    IF to_regprocedure('public.cloud_ecran_compte_creer(text,text,text,text,text,text,text,text)') IS NULL THEN
        RAISE EXCEPTION 'Script central 040 : passer d''abord les scripts 030 à 039 (écrans du central). Rien n''a été modifié.';
    END IF;
END $version$;

-- ---------------------------------------------------------------------
-- 1. Durée de vie d'un mot de passe provisoire non utilisé
-- ---------------------------------------------------------------------
ALTER TABLE parametre_envoi_codes ADD COLUMN IF NOT EXISTS duree_provisoire_jours int NOT NULL DEFAULT 7
    CONSTRAINT ck_parametre_envoi_codes_duree_provisoire CHECK (duree_provisoire_jours BETWEEN 1 AND 30);
COMMENT ON COLUMN parametre_envoi_codes.duree_provisoire_jours IS 'Script 040 : durée de vie (jours) d''un mot de passe provisoire non utilisé, 1 à 30, 7 par défaut.';

-- Durée réglée (7 jours si le réglage manque) — interne, jamais accordée
CREATE OR REPLACE FUNCTION cloud_duree_provisoire() RETURNS interval
LANGUAGE sql STABLE SET search_path = public, pg_temp AS $$
    SELECT make_interval(days => coalesce((SELECT p.duree_provisoire_jours FROM parametre_envoi_codes p WHERE p.id = 1), 7))
$$;

-- Échéance d'un mot de passe provisoire : dernier changement (remise à zéro) ou, à défaut, création, plus la durée — interne
CREATE OR REPLACE FUNCTION cloud_compte_provisoire_echeance(p_change_le timestamptz, p_cree_le timestamptz) RETURNS timestamptz
LANGUAGE sql STABLE SET search_path = public, pg_temp AS $$
    SELECT coalesce(p_change_le, p_cree_le) + cloud_duree_provisoire()
$$;

-- Connexion, 2e partie (corps du script 030 repris À L'IDENTIQUE ; seul ajout : le bloc « Script 040 » après la vérification réussie).
-- Statuts : code_attendu, refuse, verrouille, provisoire_expire (mot de passe juste mais provisoire non utilisé dans sa durée de vie :
-- aucun échec compté, aucune session). p_compte NULL : identifiant inconnu (journalisé sans l'identifiant).
CREATE OR REPLACE FUNCTION cloud_ecran_fin_connexion(p_compte uuid, p_reussi boolean, p_empreinte_jeton text)
RETURNS TABLE (statut text, verrouille_jusqu_au timestamptz)
LANGUAGE plpgsql SECURITY DEFINER SET search_path = public, pg_temp AS $$
DECLARE c compte_distant; v_session bigint;
BEGIN
    IF p_compte IS NULL THEN
        PERFORM cloud_ecran_journaliser(NULL, NULL, NULL, 'connexion', 'refus', 'identifiant_inconnu');
        RETURN QUERY SELECT 'refuse'::text, NULL::timestamptz; RETURN;
    END IF;
    SELECT * INTO c FROM compte_distant x WHERE x.id = p_compte FOR UPDATE;
    IF NOT FOUND OR NOT c.actif THEN
        PERFORM cloud_ecran_journaliser(p_compte, NULL, NULL, 'connexion', 'refus', 'compte_inactif');
        RETURN QUERY SELECT 'refuse'::text, NULL::timestamptz; RETURN;
    END IF;
    IF c.verrouille_jusqu_au > now() THEN
        PERFORM cloud_ecran_journaliser(c.id, NULL, NULL, 'connexion', 'refus', 'verrouille');
        RETURN QUERY SELECT 'verrouille'::text, c.verrouille_jusqu_au; RETURN;
    END IF;
    IF NOT coalesce(p_reussi, false) THEN
        IF c.echecs + 1 >= 5 THEN
            UPDATE compte_distant x SET echecs = 0, verrouillages = x.verrouillages + 1,
                   verrouille_jusqu_au = now() + least(interval '15 minutes' * power(2, least(x.verrouillages, 10))::int, interval '24 hours')
            WHERE x.id = c.id RETURNING * INTO c;
            PERFORM cloud_ecran_journaliser(c.id, NULL, NULL, 'verrouillage', 'refus', 'echecs_5');
            RETURN QUERY SELECT 'verrouille'::text, c.verrouille_jusqu_au; RETURN;
        END IF;
        UPDATE compte_distant x SET echecs = x.echecs + 1 WHERE x.id = c.id;
        PERFORM cloud_ecran_journaliser(c.id, NULL, NULL, 'connexion', 'refus', 'mot_de_passe');
        RETURN QUERY SELECT 'refuse'::text, NULL::timestamptz; RETURN;
    END IF;
    -- Script 040 : mot de passe provisoire non utilisé dans sa durée de vie (réglage « Envoi des codes ») : refusé, l'éditeur régénère
    IF c.mot_de_passe_provisoire AND cloud_compte_provisoire_echeance(c.mot_de_passe_change_le, c.cree_le) < now() THEN
        PERFORM cloud_ecran_journaliser(c.id, NULL, NULL, 'connexion', 'refus', 'provisoire_expire');
        RETURN QUERY SELECT 'provisoire_expire'::text, NULL::timestamptz; RETURN;
    END IF;
    IF p_empreinte_jeton IS NULL OR p_empreinte_jeton !~ '^[0-9a-f]{64}$' THEN
        RAISE EXCEPTION 'empreinte de jeton invalide' USING ERRCODE = '22023';
    END IF;
    UPDATE compte_distant x SET echecs = 0 WHERE x.id = c.id;
    INSERT INTO session_distante (empreinte_jeton, compte_id, etape, expire_le)
    VALUES (p_empreinte_jeton, c.id, 'code', now() + interval '10 minutes') RETURNING id INTO v_session;
    PERFORM cloud_ecran_journaliser(c.id, v_session, NULL, 'mot_de_passe_verifie', 'ok', NULL);
    RETURN QUERY SELECT 'code_attendu'::text, NULL::timestamptz;
END $$;

-- Durée lue par l'éditeur (écran « Envoi des codes »)
CREATE OR REPLACE FUNCTION cloud_ecran_provisoire_duree_lire(p_empreinte_jeton text) RETURNS int
LANGUAGE plpgsql SECURITY DEFINER SET search_path = public, pg_temp AS $$
DECLARE v record;
BEGIN
    SELECT * INTO v FROM cloud_ecran_editeur(p_empreinte_jeton);
    RETURN coalesce((SELECT p.duree_provisoire_jours FROM parametre_envoi_codes p WHERE p.id = 1), 7);
END $$;

-- Durée enregistrée par l'éditeur (code de confirmation « action »). Statuts : ok, duree (1 à 30 jours), puis ceux du code (faux,
-- expire, epuise, aucun). Contrôle AVANT le code. Journaux : accès (chaîné) et sécurité (ancienne et nouvelle durée).
CREATE OR REPLACE FUNCTION cloud_ecran_provisoire_duree_enregistrer(p_empreinte_jeton text, p_empreinte_code text, p_jours int)
RETURNS TABLE (statut text)
LANGUAGE plpgsql SECURITY DEFINER SET search_path = public, pg_temp AS $$
DECLARE v record; r text; v_avant int;
BEGIN
    SELECT * INTO v FROM cloud_ecran_editeur(p_empreinte_jeton);
    IF p_jours IS NULL OR p_jours NOT BETWEEN 1 AND 30 THEN
        PERFORM cloud_ecran_journaliser(v.compte_id, v.session_id, NULL, 'duree_provisoire_modifiee', 'refus', 'duree');
        RETURN QUERY SELECT 'duree'::text; RETURN;
    END IF;
    r := cloud_ecran_controler_code(v.session_id, 'action', p_empreinte_code);
    IF r <> 'ok' THEN
        PERFORM cloud_ecran_noter_action(v.compte_id, v.session_id, NULL, 'duree_provisoire_modifiee', 'refus', r, NULL);
        RETURN QUERY SELECT r; RETURN;
    END IF;
    SELECT p.duree_provisoire_jours INTO v_avant FROM parametre_envoi_codes p WHERE p.id = 1 FOR UPDATE;
    UPDATE parametre_envoi_codes x SET duree_provisoire_jours = p_jours, modifie_par = v.compte_id, modifie_le = now() WHERE x.id = 1;
    PERFORM cloud_ecran_noter_action(v.compte_id, v.session_id, NULL, 'duree_provisoire_modifiee', 'ok', 'jours:' || p_jours,
        'durée de vie d''un mot de passe provisoire non utilisé : ' || p_jours || ' jour(s) (avant : ' || coalesce(v_avant::text, '7') || ')');
    RETURN QUERY SELECT 'ok'::text;
END $$;

-- ---------------------------------------------------------------------
-- 2. Import de comptes (lignes d'un fichier CSV lues par l'application)
-- ---------------------------------------------------------------------

-- Contrôle des lignes (interne, jamais accordé). p_lignes : tableau JSON de 1 à 200 objets {ligne, role, identifiant, nom, email,
-- telephone[, empreinte]} (ligne = n° dans le fichier). Rend chaque ligne normalisée et son statut, dans l'ordre de 039 : forme (pas un
-- objet), identifiant, doublon (même identifiant plus haut dans le fichier), existe (déjà pris), nom, role, contact, email, telephone ;
-- ok sinon. Aucune écriture.
CREATE OR REPLACE FUNCTION cloud_comptes_import_controle(p_lignes jsonb)
RETURNS TABLE (ligne int, statut text, identifiant text, nom text, role text, telephone text, email text, empreinte text)
LANGUAGE plpgsql STABLE SET search_path = public, pg_temp AS $$
#variable_conflict use_column
BEGIN
    IF p_lignes IS NULL OR jsonb_typeof(p_lignes) <> 'array' OR jsonb_array_length(p_lignes) NOT BETWEEN 1 AND 200 THEN
        RAISE EXCEPTION 'lignes du fichier : tableau de 1 à 200 lignes attendu' USING ERRCODE = '22023';
    END IF;
    RETURN QUERY
    WITH brut AS (
        SELECT x.n::int AS n, x.e, jsonb_typeof(x.e) = 'object' AS objet FROM jsonb_array_elements(p_lignes) WITH ORDINALITY AS x(e, n)
    ), l AS (
        SELECT CASE WHEN b.objet AND b.e->>'ligne' ~ '^[0-9]{1,6}$' THEN (b.e->>'ligne')::int ELSE b.n END AS l_ligne, b.n AS l_n, b.objet AS l_objet,
               CASE WHEN b.objet THEN lower(btrim(coalesce(b.e->>'identifiant', ''))) ELSE '' END AS l_ident,
               CASE WHEN b.objet THEN btrim(coalesce(b.e->>'nom', '')) ELSE '' END AS l_nom,
               CASE WHEN b.objet THEN lower(btrim(coalesce(b.e->>'role', ''))) ELSE '' END AS l_role,
               CASE WHEN b.objet THEN nullif(replace(btrim(coalesce(b.e->>'telephone', '')), ' ', ''), '') END AS l_tel,
               CASE WHEN b.objet THEN nullif(btrim(coalesce(b.e->>'email', '')), '') END AS l_email,
               CASE WHEN b.objet THEN b.e->>'empreinte' END AS l_empreinte
        FROM brut b
    ), c AS (
        SELECT l.*, row_number() OVER (PARTITION BY l.l_ident ORDER BY l.l_n) AS l_rang FROM l
    )
    SELECT c.l_ligne,
           CASE WHEN NOT c.l_objet THEN 'forme'
                WHEN c.l_ident !~ '^[a-z0-9][a-z0-9._-]{2,59}$' THEN 'identifiant'
                WHEN c.l_rang > 1 THEN 'doublon'
                WHEN EXISTS (SELECT 1 FROM compte_distant x WHERE x.identifiant = c.l_ident) THEN 'existe'
                WHEN length(c.l_nom) NOT BETWEEN 1 AND 120 THEN 'nom'
                WHEN c.l_role NOT IN ('editeur', 'titulaire') THEN 'role'
                WHEN c.l_tel IS NULL AND c.l_email IS NULL THEN 'contact'
                WHEN c.l_email IS NOT NULL AND (length(c.l_email) > 254 OR c.l_email !~ '^[^@[:space:]]+@[^@[:space:]]+\.[^@[:space:]]+$') THEN 'email'
                WHEN c.l_tel IS NOT NULL AND c.l_tel !~ '^\+?[0-9]{8,15}$' THEN 'telephone'
                ELSE 'ok' END,
           c.l_ident, c.l_nom, c.l_role, c.l_tel, c.l_email, c.l_empreinte
    FROM c ORDER BY c.l_n;
END $$;

-- Aperçu par l'éditeur : statut de chaque ligne (numero = n° de ligne du fichier ; « ligne » est un nom de colonne interdit aux
-- écrans, test 031), SANS code (rien n'est créé ni consommé). Journal des accès : nombre de lignes et
-- d'erreurs seulement.
CREATE OR REPLACE FUNCTION cloud_ecran_comptes_apercu(p_empreinte_jeton text, p_lignes jsonb)
RETURNS TABLE (numero int, statut text)
LANGUAGE plpgsql SECURITY DEFINER SET search_path = public, pg_temp AS $$
#variable_conflict use_column
DECLARE v record; v_n int; v_err int;
BEGIN
    SELECT * INTO v FROM cloud_ecran_editeur(p_empreinte_jeton);
    SELECT count(*), count(*) FILTER (WHERE x.statut <> 'ok') INTO v_n, v_err FROM cloud_comptes_import_controle(p_lignes) x;
    PERFORM cloud_ecran_journaliser(v.compte_id, v.session_id, NULL, 'comptes_import_apercu', 'ok', 'lignes:' || v_n || ':erreurs:' || v_err);
    RETURN QUERY SELECT x.ligne, x.statut FROM cloud_comptes_import_controle(p_lignes) x;
END $$;

-- Création en lot par l'éditeur : TOUT OU RIEN, UN SEUL code de confirmation pour tout le fichier. Chaque ligne porte l'empreinte BCrypt
-- de son mot de passe provisoire (coût 12 au moins). Contrôles des lignes AVANT le code : une ligne invalide → rien n'est créé, le code
-- n'est pas consommé, chaque ligne rend son statut. Code faux, expiré… : une ligne (ligne NULL) avec le statut du code. Réussite : une
-- ligne « ok » par compte créé (n° de ligne, n° du compte). Identifiant pris entre le contrôle et l'insertion : rien n'est créé
-- (ligne NULL, statut existe ; le code est consommé). Journaux : accès (une ligne par compte créé, une pour l'éditeur) et sécurité
-- (nombre de comptes par rôle) ; jamais l'identifiant, le nom ni le contact.
CREATE OR REPLACE FUNCTION cloud_ecran_comptes_importer(p_empreinte_jeton text, p_empreinte_code text, p_lignes jsonb)
RETURNS TABLE (numero int, statut text, compte uuid)
LANGUAGE plpgsql SECURITY DEFINER SET search_path = public, pg_temp AS $$
#variable_conflict use_column
DECLARE v record; r text; k record; v_id uuid; v_lignes int[] := '{}'; v_ids uuid[] := '{}'; v_ed int := 0; v_ti int := 0;
BEGIN
    SELECT * INTO v FROM cloud_ecran_editeur(p_empreinte_jeton);
    IF EXISTS (SELECT 1 FROM cloud_comptes_import_controle(p_lignes) x WHERE x.statut <> 'ok') THEN
        PERFORM cloud_ecran_journaliser(v.compte_id, v.session_id, NULL, 'comptes_importes', 'refus', 'lignes');
        RETURN QUERY SELECT x.ligne, x.statut, NULL::uuid FROM cloud_comptes_import_controle(p_lignes) x;
        RETURN;
    END IF;
    IF EXISTS (SELECT 1 FROM cloud_comptes_import_controle(p_lignes) x
               WHERE x.empreinte IS NULL OR x.empreinte !~ '^\$2[aby]\$1[2-9]\$[./A-Za-z0-9]{53}$') THEN
        RAISE EXCEPTION 'empreinte BCrypt invalide (coût 12 au moins)' USING ERRCODE = '22023';
    END IF;
    r := cloud_ecran_controler_code(v.session_id, 'action', p_empreinte_code);
    IF r <> 'ok' THEN
        PERFORM cloud_ecran_noter_action(v.compte_id, v.session_id, NULL, 'comptes_importes', 'refus', r, NULL);
        RETURN QUERY SELECT NULL::int, r, NULL::uuid; RETURN;
    END IF;
    BEGIN
        FOR k IN SELECT * FROM cloud_comptes_import_controle(p_lignes) LOOP
            INSERT INTO compte_distant (identifiant, nom, role, empreinte_mot_de_passe, telephone, email, cree_par)
            VALUES (k.identifiant, k.nom, k.role, k.empreinte, k.telephone, k.email, 'écran : import, éditeur n° ' || left(v.compte_id::text, 8))
            RETURNING id INTO v_id;
            PERFORM cloud_ecran_journaliser(v_id, NULL, NULL, 'compte_cree', 'ok', 'import:' || k.role);
            v_lignes := v_lignes || k.ligne; v_ids := v_ids || v_id;
            IF k.role = 'editeur' THEN v_ed := v_ed + 1; ELSE v_ti := v_ti + 1; END IF;
        END LOOP;
    EXCEPTION WHEN unique_violation THEN
        -- Un identifiant pris entre le contrôle et l'insertion (deux éditeurs en même temps) : tout est annulé, le code est consommé
        RETURN QUERY SELECT NULL::int, 'existe'::text, NULL::uuid; RETURN;
    END;
    PERFORM cloud_ecran_noter_action(v.compte_id, v.session_id, NULL, 'comptes_importes', 'ok', 'comptes:' || (v_ed + v_ti),
        'import de ' || (v_ed + v_ti) || ' compte(s) depuis l''écran (' || v_ed || ' éditeur(s), ' || v_ti
        || ' titulaire(s)) ; mots de passe provisoires, à changer à la première connexion');
    RETURN QUERY SELECT u.l, 'ok'::text, u.i FROM unnest(v_lignes, v_ids) AS u(l, i);
END $$;

-- Création en lot par la CONSOLE du central (propriétaire, aucun code ; jamais accordée aux écrans) : mêmes contrôles, tout ou rien.
-- Ligne invalide : rien n'est créé, chaque ligne rend son statut. Réussite : une ligne « ok » par compte créé.
CREATE OR REPLACE FUNCTION cloud_compte_distant_creer_lot(p_lignes jsonb, p_auteur text)
RETURNS TABLE (numero int, statut text, compte uuid)
LANGUAGE plpgsql SET search_path = public, pg_temp AS $$
#variable_conflict use_column
DECLARE k record; v_id uuid; v_lignes int[] := '{}'; v_ids uuid[] := '{}'; v_ed int := 0; v_ti int := 0;
        v_auteur text := left(btrim(coalesce(p_auteur, '')), 100);
BEGIN
    IF length(v_auteur) < 1 THEN RAISE EXCEPTION 'auteur obligatoire' USING ERRCODE = '22023'; END IF;
    IF EXISTS (SELECT 1 FROM cloud_comptes_import_controle(p_lignes) x WHERE x.statut <> 'ok') THEN
        RETURN QUERY SELECT x.ligne, x.statut, NULL::uuid FROM cloud_comptes_import_controle(p_lignes) x;
        RETURN;
    END IF;
    IF EXISTS (SELECT 1 FROM cloud_comptes_import_controle(p_lignes) x
               WHERE x.empreinte IS NULL OR x.empreinte !~ '^\$2[aby]\$1[2-9]\$[./A-Za-z0-9]{53}$') THEN
        RAISE EXCEPTION 'empreinte BCrypt invalide (coût 12 au moins)' USING ERRCODE = '22023';
    END IF;
    FOR k IN SELECT * FROM cloud_comptes_import_controle(p_lignes) LOOP
        INSERT INTO compte_distant (identifiant, nom, role, empreinte_mot_de_passe, telephone, email, cree_par)
        VALUES (k.identifiant, k.nom, k.role, k.empreinte, k.telephone, k.email, v_auteur)
        RETURNING id INTO v_id;
        PERFORM cloud_ecran_journaliser(v_id, NULL, NULL, 'compte_cree', 'ok', 'console:import:' || k.role);
        v_lignes := v_lignes || k.ligne; v_ids := v_ids || v_id;
        IF k.role = 'editeur' THEN v_ed := v_ed + 1; ELSE v_ti := v_ti + 1; END IF;
    END LOOP;
    INSERT INTO journal_securite (evenement, detail)
    VALUES ('comptes_importes', left('console (' || v_auteur || ') : import de ' || (v_ed + v_ti) || ' compte(s) distant(s) ('
        || v_ed || ' éditeur(s), ' || v_ti || ' titulaire(s)) ; mots de passe provisoires, à changer à la première connexion', 500));
    RETURN QUERY SELECT u.l, 'ok'::text, u.i FROM unnest(v_lignes, v_ids) AS u(l, i);
END $$;

-- ---------------------------------------------------------------------
-- 3. Liste des comptes (état du mot de passe) et note des envois
-- ---------------------------------------------------------------------

-- Comptes vus par l'éditeur : colonnes de cloud_ecran_facteur_comptes (037 ; contact MASQUÉ) et état du mot de passe : NULL (choisi),
-- provisoire (avec son échéance) ou provisoire_expire. Page de 10 à 100.
CREATE OR REPLACE FUNCTION cloud_ecran_comptes_liste(p_empreinte_jeton text, p_page int, p_taille int)
RETURNS TABLE (total bigint, id uuid, identifiant text, role text, actif boolean, email_masque text, telephone_masque text,
               derniere_connexion_le timestamptz, mot_de_passe_etat text, provisoire_expire_le timestamptz)
LANGUAGE plpgsql SECURITY DEFINER SET search_path = public, pg_temp AS $$
DECLARE v record; v_taille int := least(greatest(coalesce(p_taille, 20), 10), 100); v_page int := greatest(coalesce(p_page, 1), 1);
BEGIN
    SELECT * INTO v FROM cloud_ecran_editeur(p_empreinte_jeton);
    PERFORM cloud_ecran_journaliser(v.compte_id, v.session_id, NULL, 'liste_comptes', 'ok', NULL);
    RETURN QUERY SELECT count(*) OVER (), c.id, c.identifiant, c.role, c.actif,
                        CASE WHEN c.email IS NOT NULL THEN left(c.email, 1) || '***' || substr(c.email, strpos(c.email, '@')) END,
                        CASE WHEN c.telephone IS NOT NULL THEN '***' || right(c.telephone, 2) END,
                        c.derniere_connexion_le,
                        CASE WHEN NOT c.mot_de_passe_provisoire THEN NULL
                             WHEN cloud_compte_provisoire_echeance(c.mot_de_passe_change_le, c.cree_le) < now() THEN 'provisoire_expire'
                             ELSE 'provisoire' END,
                        CASE WHEN c.mot_de_passe_provisoire THEN cloud_compte_provisoire_echeance(c.mot_de_passe_change_le, c.cree_le) END
                 FROM compte_distant c ORDER BY c.role, c.identifiant OFFSET (v_page - 1) * v_taille LIMIT v_taille;
END $$;

-- Résultat de l'envoi de l'identifiant (e-mail, evenement identifiant_envoi) ou du mot de passe provisoire (SMS, evenement
-- provisoire_envoi) d'un compte, au journal des accès du compte (liste fermée canal:résultat ; jamais l'adresse, le numéro, ni le mot
-- de passe). Éditeur seulement.
CREATE OR REPLACE FUNCTION cloud_ecran_comptes_noter_envoi(p_empreinte_jeton text, p_compte uuid, p_evenement text, p_objet text) RETURNS void
LANGUAGE plpgsql SECURITY DEFINER SET search_path = public, pg_temp AS $$
DECLARE v record;
BEGIN
    SELECT * INTO v FROM cloud_ecran_editeur(p_empreinte_jeton);
    IF p_evenement IS NULL OR p_evenement NOT IN ('identifiant_envoi', 'provisoire_envoi') OR p_objet IS NULL
       OR (p_evenement = 'identifiant_envoi' AND p_objet !~ '^email:(ok|echec)$')
       OR (p_evenement = 'provisoire_envoi' AND p_objet !~ '^sms:(ok|echec|indisponible)$') THEN
        RAISE EXCEPTION 'résultat d''envoi invalide' USING ERRCODE = '22023';
    END IF;
    IF NOT EXISTS (SELECT 1 FROM compte_distant c WHERE c.id = p_compte) THEN
        RAISE EXCEPTION 'compte inconnu' USING ERRCODE = '22023';
    END IF;
    PERFORM cloud_ecran_journaliser(p_compte, NULL, NULL, p_evenement, CASE WHEN p_objet LIKE '%:ok' THEN 'ok' ELSE 'refus' END, p_objet);
END $$;

-- Destinataire d'un compte TOUT JUSTE remis à zéro (038, il y a 2 minutes au plus, mot de passe encore provisoire) : identifiant, rôle,
-- e-mail et téléphone, pour l'envoi de l'identifiant (e-mail) et du mot de passe (SMS) par l'application. Aucune ligne sinon (jamais
-- le contact d'un compte quelconque). Éditeur seulement.
CREATE OR REPLACE FUNCTION cloud_ecran_comptes_destinataire_remise(p_empreinte_jeton text, p_compte uuid)
RETURNS TABLE (identifiant text, role text, email text, telephone text)
LANGUAGE plpgsql SECURITY DEFINER SET search_path = public, pg_temp AS $$
DECLARE v record;
BEGIN
    SELECT * INTO v FROM cloud_ecran_editeur(p_empreinte_jeton);
    RETURN QUERY SELECT c.identifiant, c.role, c.email, c.telephone FROM compte_distant c
                 WHERE c.id = p_compte AND c.id <> v.compte_id AND c.actif AND c.mot_de_passe_provisoire
                   AND c.mot_de_passe_change_le > now() - interval '2 minutes';
END $$;

-- ---------------------------------------------------------------------
-- 4. Déconnexion après inactivité, réglée par l'éditeur (par rôle)
-- ---------------------------------------------------------------------
-- Décision du titulaire (2026-11-21) : un utilisateur actif n'est jamais déconnecté. Une session OUVERTE n'a plus de durée maximale
-- (éditeur 2 h, titulaire 8 h : abandonnées) : seule l'inactivité la ferme (éditeur 15 min, titulaire 30 min par défaut, 5 à 60 min,
-- réglées dans l'écran « Envoi des codes ») ; chaque requête de l'utilisateur repousse son échéance (expire_le glissante). Les étapes
-- intermédiaires (code : 10 min ; mot de passe à changer : 10 min) gardent leur courte échéance.
ALTER TABLE parametre_envoi_codes ADD COLUMN IF NOT EXISTS inactivite_editeur_minutes int NOT NULL DEFAULT 15
    CONSTRAINT ck_parametre_envoi_codes_inactivite_editeur CHECK (inactivite_editeur_minutes BETWEEN 5 AND 60);
ALTER TABLE parametre_envoi_codes ADD COLUMN IF NOT EXISTS inactivite_titulaire_minutes int NOT NULL DEFAULT 30
    CONSTRAINT ck_parametre_envoi_codes_inactivite_titulaire CHECK (inactivite_titulaire_minutes BETWEEN 5 AND 60);
COMMENT ON COLUMN parametre_envoi_codes.inactivite_editeur_minutes IS 'Script 040 : session éditeur fermée après cette inactivité (minutes, 5 à 60, 15 par défaut).';
COMMENT ON COLUMN parametre_envoi_codes.inactivite_titulaire_minutes IS 'Script 040 : session titulaire fermée après cette inactivité (minutes, 5 à 60, 30 par défaut).';

-- Motif de fin « inactivite » permis (contrainte élargie, aucune ligne modifiée)
DO $$ BEGIN
    IF EXISTS (SELECT 1 FROM pg_constraint WHERE conrelid = 'public.session_distante'::regclass AND conname = 'session_distante_motif_fin_check') THEN
        ALTER TABLE session_distante DROP CONSTRAINT session_distante_motif_fin_check;
    END IF;
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conrelid = 'public.session_distante'::regclass AND conname = 'ck_session_distante_motif_fin') THEN
        ALTER TABLE session_distante ADD CONSTRAINT ck_session_distante_motif_fin
            CHECK (motif_fin IN ('deconnexion', 'expiree', 'revoquee', 'remplacee', 'compte_desactive', 'codes_epuises', 'inactivite'));
    END IF;
END $$;

-- Inactivité réglée pour un rôle (15 / 30 min si le réglage manque) — interne, jamais accordée
CREATE OR REPLACE FUNCTION cloud_inactivite_session(p_role text) RETURNS interval
LANGUAGE sql STABLE SET search_path = public, pg_temp AS $$
    SELECT make_interval(mins => coalesce((SELECT CASE p_role WHEN 'editeur' THEN p.inactivite_editeur_minutes ELSE p.inactivite_titulaire_minutes END
                                           FROM parametre_envoi_codes p WHERE p.id = 1), CASE p_role WHEN 'editeur' THEN 15 ELSE 30 END))
$$;

-- Session valable (corps du script 030 repris ; script 040 : session OUVERTE fermée seulement après l'inactivité réglée pour son rôle,
-- motif « inactivite », et échéance glissante repoussée à chaque requête ; étapes code et mot de passe : échéance courte inchangée).
-- Rend la ligne de session et le rôle du compte, ou rien. L'activité est notée (chaque requête de l'utilisateur).
CREATE OR REPLACE FUNCTION cloud_ecran_session_valable(p_empreinte_jeton text, OUT session_id bigint, OUT compte_id uuid, OUT role text, OUT etape text)
LANGUAGE plpgsql SECURITY DEFINER SET search_path = public, pg_temp AS $$
DECLARE s session_distante; c compte_distant;
BEGIN
    IF p_empreinte_jeton IS NULL OR p_empreinte_jeton !~ '^[0-9a-f]{64}$' THEN RETURN; END IF;
    SELECT * INTO s FROM session_distante x WHERE x.empreinte_jeton = p_empreinte_jeton AND x.fin_le IS NULL FOR UPDATE;
    IF NOT FOUND THEN RETURN; END IF;
    SELECT * INTO c FROM compte_distant x WHERE x.id = s.compte_id;
    IF NOT c.actif THEN
        UPDATE session_distante SET fin_le = now(), motif_fin = 'compte_desactive' WHERE id = s.id;
        RETURN;
    END IF;
    -- Script 040 : session ouverte : seule l'inactivité réglée pour le rôle la ferme (plus de durée maximale)
    IF s.etape = 'ouverte' AND s.derniere_activite_le <= now() - cloud_inactivite_session(c.role) THEN
        UPDATE session_distante SET fin_le = now(), motif_fin = 'inactivite' WHERE id = s.id;
        PERFORM cloud_ecran_journaliser(c.id, s.id, NULL, 'session_expiree', 'ok', 'inactivite');
        RETURN;
    END IF;
    IF s.etape <> 'ouverte' AND s.expire_le <= now() THEN
        UPDATE session_distante SET fin_le = now(), motif_fin = 'expiree' WHERE id = s.id;
        PERFORM cloud_ecran_journaliser(c.id, s.id, NULL, 'session_expiree', 'ok', NULL);
        RETURN;
    END IF;
    UPDATE session_distante SET derniere_activite_le = now(),
           expire_le = CASE WHEN s.etape = 'ouverte' THEN now() + cloud_inactivite_session(c.role) ELSE s.expire_le END
    WHERE id = s.id;
    session_id := s.id; compte_id := c.id; role := c.role; etape := s.etape;
END $$;

-- Inactivité lue par l'éditeur (écran « Envoi des codes », section « Sécurité des sessions »)
CREATE OR REPLACE FUNCTION cloud_ecran_inactivite_lire(p_empreinte_jeton text)
RETURNS TABLE (inactivite_editeur_minutes int, inactivite_titulaire_minutes int)
LANGUAGE plpgsql SECURITY DEFINER SET search_path = public, pg_temp AS $$
DECLARE v record;
BEGIN
    SELECT * INTO v FROM cloud_ecran_editeur(p_empreinte_jeton);
    RETURN QUERY SELECT coalesce(p.inactivite_editeur_minutes, 15), coalesce(p.inactivite_titulaire_minutes, 30) FROM parametre_envoi_codes p WHERE p.id = 1;
END $$;

-- Inactivité enregistrée par l'éditeur (code de confirmation « action »). Statuts : ok, editeur, titulaire (5 à 60 minutes), puis ceux
-- du code. Contrôles AVANT le code. Journaux : accès (chaîné) et sécurité (anciennes et nouvelles durées). Effet dès la requête suivante
-- de chaque session (sessions ouvertes comprises).
CREATE OR REPLACE FUNCTION cloud_ecran_inactivite_enregistrer(p_empreinte_jeton text, p_empreinte_code text, p_editeur int, p_titulaire int)
RETURNS TABLE (statut text)
LANGUAGE plpgsql SECURITY DEFINER SET search_path = public, pg_temp AS $$
DECLARE v record; r text; p parametre_envoi_codes;
BEGIN
    SELECT * INTO v FROM cloud_ecran_editeur(p_empreinte_jeton);
    r := CASE WHEN p_editeur IS NULL OR p_editeur NOT BETWEEN 5 AND 60 THEN 'editeur'
              WHEN p_titulaire IS NULL OR p_titulaire NOT BETWEEN 5 AND 60 THEN 'titulaire' END;
    IF r IS NOT NULL THEN
        PERFORM cloud_ecran_journaliser(v.compte_id, v.session_id, NULL, 'inactivite_modifiee', 'refus', r);
        RETURN QUERY SELECT r; RETURN;
    END IF;
    r := cloud_ecran_controler_code(v.session_id, 'action', p_empreinte_code);
    IF r <> 'ok' THEN
        PERFORM cloud_ecran_noter_action(v.compte_id, v.session_id, NULL, 'inactivite_modifiee', 'refus', r, NULL);
        RETURN QUERY SELECT r; RETURN;
    END IF;
    SELECT * INTO p FROM parametre_envoi_codes x WHERE x.id = 1 FOR UPDATE;
    UPDATE parametre_envoi_codes x SET inactivite_editeur_minutes = p_editeur, inactivite_titulaire_minutes = p_titulaire,
           modifie_par = v.compte_id, modifie_le = now() WHERE x.id = 1;
    PERFORM cloud_ecran_noter_action(v.compte_id, v.session_id, NULL, 'inactivite_modifiee', 'ok', 'minutes:' || p_editeur || ':' || p_titulaire,
        'déconnexion après inactivité : éditeur ' || p_editeur || ' min, titulaire ' || p_titulaire || ' min (avant : '
        || coalesce(p.inactivite_editeur_minutes, 15) || ' / ' || coalesce(p.inactivite_titulaire_minutes, 30) || ')');
    RETURN QUERY SELECT 'ok'::text;
END $$;

-- ---- Droits : rien pour PUBLIC ; écrans : les seules fonctions cloud_ecran_* de ce script ; console : propriétaire ----
REVOKE ALL ON FUNCTION cloud_duree_provisoire(), cloud_compte_provisoire_echeance(timestamptz, timestamptz),
    cloud_ecran_fin_connexion(uuid, boolean, text), cloud_ecran_provisoire_duree_lire(text), cloud_ecran_provisoire_duree_enregistrer(text, text, int),
    cloud_comptes_import_controle(jsonb), cloud_ecran_comptes_apercu(text, jsonb), cloud_ecran_comptes_importer(text, text, jsonb),
    cloud_compte_distant_creer_lot(jsonb, text), cloud_ecran_comptes_liste(text, int, int), cloud_ecran_comptes_noter_envoi(text, uuid, text, text),
    cloud_ecran_comptes_destinataire_remise(text, uuid), cloud_inactivite_session(text), cloud_ecran_session_valable(text),
    cloud_ecran_inactivite_lire(text), cloud_ecran_inactivite_enregistrer(text, text, int, int)
    FROM PUBLIC;
GRANT EXECUTE ON FUNCTION cloud_ecran_fin_connexion(uuid, boolean, text) TO pharma_cloud_ecrans;
GRANT EXECUTE ON FUNCTION cloud_ecran_provisoire_duree_lire(text) TO pharma_cloud_ecrans;
GRANT EXECUTE ON FUNCTION cloud_ecran_provisoire_duree_enregistrer(text, text, int) TO pharma_cloud_ecrans;
GRANT EXECUTE ON FUNCTION cloud_ecran_comptes_apercu(text, jsonb) TO pharma_cloud_ecrans;
GRANT EXECUTE ON FUNCTION cloud_ecran_comptes_importer(text, text, jsonb) TO pharma_cloud_ecrans;
GRANT EXECUTE ON FUNCTION cloud_ecran_comptes_liste(text, int, int) TO pharma_cloud_ecrans;
GRANT EXECUTE ON FUNCTION cloud_ecran_comptes_noter_envoi(text, uuid, text, text) TO pharma_cloud_ecrans;
GRANT EXECUTE ON FUNCTION cloud_ecran_comptes_destinataire_remise(text, uuid) TO pharma_cloud_ecrans;
GRANT EXECUTE ON FUNCTION cloud_ecran_inactivite_lire(text) TO pharma_cloud_ecrans;
GRANT EXECUTE ON FUNCTION cloud_ecran_inactivite_enregistrer(text, text, int, int) TO pharma_cloud_ecrans;

COMMENT ON FUNCTION cloud_ecran_fin_connexion(uuid, boolean, text) IS 'Connexion, 2e partie (030) ; script 040 : mot de passe provisoire expiré refusé.';
COMMENT ON FUNCTION cloud_ecran_provisoire_duree_enregistrer(text, text, int) IS 'Durée de vie d''un mot de passe provisoire non utilisé, réglée par l''éditeur (code de confirmation), script 040.';
COMMENT ON FUNCTION cloud_ecran_comptes_apercu(text, jsonb) IS 'Aperçu d''un import de comptes (statut de chaque ligne, sans code), script 040.';
COMMENT ON FUNCTION cloud_ecran_comptes_importer(text, text, jsonb) IS 'Import de comptes distants par l''éditeur : tout ou rien, un seul code de confirmation, script 040.';
COMMENT ON FUNCTION cloud_compte_distant_creer_lot(jsonb, text) IS 'Import de comptes distants par la console du central (propriétaire) : tout ou rien, script 040.';
COMMENT ON FUNCTION cloud_ecran_session_valable(text) IS 'Session valable (030) ; script 040 : session ouverte fermée après l''inactivité réglée par rôle, échéance glissante.';
COMMENT ON FUNCTION cloud_ecran_inactivite_enregistrer(text, text, int, int) IS 'Déconnexion après inactivité par rôle, réglée par l''éditeur (code de confirmation), script 040.';
COMMENT ON FUNCTION cloud_ecran_comptes_liste(text, int, int) IS 'Comptes distants vus par l''éditeur avec l''état du mot de passe (provisoire, expiré), script 040.';
