-- =====================================================================
-- Base centrale : nomenclature nationale envoyée aux officines (lot S5.3,
-- 2026-11-18 ; docs/conception-synchronisation.md § 26). Script de la
-- base CENTRALE, à passer après 014 par le propriétaire.
--   * referentiel_fichier : fichier officiel (classeur du MIPH) gardé UNE
--     fois pour toutes les officines : libellé, nom, contenu, empreinte
--     SHA-256, taille (20 Mo au plus), auteur ; un même contenu n'est
--     gardé qu'une fois. Pas de cloisonnement : référentiel commun.
--   * Type d'ordre « nomenclature » : désigne un fichier ; l'officine le
--     télécharge à la confirmation du titulaire, vérifie l'empreinte et
--     l'importe comme un import manuel (décisions S5.3-D1 à D5).
--   * cloud_referentiel_contenu : contenu remis à l'API seulement pour une
--     officine (celle de la transaction) qui a reçu un ordre désignant ce
--     fichier.
--   * cloud_creer_ordre redéfinie (version 014 + branche nomenclature) ;
--     contrainte des types élargie avec garde.
-- Idempotent. Aucune donnée existante modifiée.
-- =====================================================================

SET client_encoding = 'UTF8';

CREATE TABLE IF NOT EXISTS referentiel_fichier (
    id         bigint      GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    nature     text        NOT NULL CHECK (nature IN ('nomenclature')),
    libelle    text        NOT NULL CHECK (length(btrim(libelle)) BETWEEN 1 AND 120),
    fichier    text        NOT NULL CHECK (length(fichier) BETWEEN 1 AND 200),
    contenu    bytea       NOT NULL,
    empreinte  text        NOT NULL UNIQUE CHECK (empreinte ~ '^[0-9a-f]{64}$'),
    octets     integer     NOT NULL CHECK (octets BETWEEN 1 AND 20971520),
    auteur     text        NOT NULL CHECK (length(btrim(auteur)) BETWEEN 1 AND 100),
    importe_le timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT ck_referentiel_fichier_taille CHECK (octets = length(contenu))
);
COMMENT ON TABLE referentiel_fichier IS 'Fichiers officiels (nomenclature) gardés une fois et téléchargés par les officines, script 015.';

-- Enregistre un fichier (même contenu : renvoie le n° existant) ; contrôle minimal : classeur .xlsx (archive ZIP), 20 Mo au plus
CREATE OR REPLACE FUNCTION cloud_importer_referentiel(p_nature text, p_libelle text, p_fichier text, p_contenu bytea, p_auteur text) RETURNS bigint
LANGUAGE plpgsql AS $$
DECLARE v_empreinte text := encode(sha256(p_contenu), 'hex'); v_id bigint;
BEGIN
    IF p_auteur IS NULL OR length(btrim(p_auteur)) NOT BETWEEN 1 AND 100 THEN RAISE EXCEPTION 'Auteur obligatoire.'; END IF;
    IF p_contenu IS NULL OR length(p_contenu) NOT BETWEEN 1 AND 20971520 THEN RAISE EXCEPTION 'Fichier vide ou de plus de 20 Mo.'; END IF;
    IF substring(p_contenu FROM 1 FOR 4) <> '\x504b0304'::bytea THEN RAISE EXCEPTION 'Ce n''est pas un classeur Excel (.xlsx).'; END IF;
    SELECT id INTO v_id FROM referentiel_fichier WHERE empreinte = v_empreinte;
    IF v_id IS NOT NULL THEN RETURN v_id; END IF;
    INSERT INTO referentiel_fichier (nature, libelle, fichier, contenu, empreinte, octets, auteur)
    VALUES (p_nature, btrim(p_libelle), p_fichier, p_contenu, v_empreinte, length(p_contenu), btrim(p_auteur))
    RETURNING id INTO v_id;
    RETURN v_id;
END $$;

-- Contenu d'un fichier pour l'officine courante, seulement si un de ses ordres le désigne (sinon NULL)
CREATE OR REPLACE FUNCTION cloud_referentiel_contenu(p_id bigint) RETURNS bytea
LANGUAGE sql STABLE SECURITY DEFINER SET search_path = public, pg_temp AS $$
    SELECT f.contenu FROM referentiel_fichier f
    WHERE f.id = p_id
      AND EXISTS (SELECT 1 FROM ordre_central o WHERE o.officine_id = cloud_officine_courante() AND o.type = 'nomenclature'
                    AND (o.donnees ->> 'fichier')::bigint = p_id)
$$;
REVOKE EXECUTE ON FUNCTION cloud_referentiel_contenu(bigint) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION cloud_referentiel_contenu(bigint) TO pharma_cloud_api;
REVOKE EXECUTE ON FUNCTION cloud_importer_referentiel(text, text, text, bytea, text) FROM PUBLIC;

DO $garde$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'ck_ordre_central_type' AND pg_get_constraintdef(oid) LIKE '%''nomenclature''%') THEN
        ALTER TABLE ordre_central DROP CONSTRAINT IF EXISTS ck_ordre_central_type;
        ALTER TABLE ordre_central ADD CONSTRAINT ck_ordre_central_type CHECK (type IN ('essai', 'employe_creer', 'employe_modifier',
            'employe_bloquer', 'employe_debloquer', 'droits_role', 'prix_produit', 'nomenclature'));
    END IF;
END $garde$;

-- Création d'un ordre : contrôles, valeur attendue lue dans les tables miroir, journal, publication
-- Garde de rejeu (2026-11-20) : définition remplacée par le script 016 ; rejouer ce script après 016 ne la
-- ramène pas en arrière (marqueur : contrainte ck_ordre_central_type contenant 'commande', posée par 016).
DO $rejeu$ BEGIN
    IF EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'ck_ordre_central_type' AND pg_get_constraintdef(oid) LIKE '%''commande''%') THEN
        RAISE NOTICE 'cloud_creer_ordre : définition plus récente (script 016) gardée';
    ELSE
        EXECUTE $def$
CREATE OR REPLACE FUNCTION cloud_creer_ordre(p_officine uuid, p_type text, p_cible text, p_donnees jsonb, p_auteur text) RETURNS uuid
LANGUAGE plpgsql AS $$
DECLARE
    v_id      uuid;
    v_attendu jsonb := '{}'::jsonb;
    v_donnees jsonb := coalesce(p_donnees, '{}'::jsonb);
    v_cible   text := nullif(btrim(coalesce(p_cible, '')), '');
    u         record;
    r         record;
    v_perms   jsonb;
BEGIN
    IF p_auteur IS NULL OR length(btrim(p_auteur)) NOT BETWEEN 1 AND 100 THEN RAISE EXCEPTION 'Auteur de l''ordre obligatoire (1 à 100 caractères).'; END IF;
    IF NOT EXISTS (SELECT 1 FROM officine WHERE id = p_officine AND resiliee_le IS NULL) THEN RAISE EXCEPTION 'Officine inconnue ou résiliée : %', p_officine; END IF;
    IF jsonb_typeof(v_donnees) <> 'object' THEN RAISE EXCEPTION 'Données de l''ordre : objet JSON attendu.'; END IF;
    PERFORM set_config('app.officine_id', p_officine::text, true);

    IF p_type = 'essai' THEN
        v_cible := NULL; v_donnees := '{}'::jsonb;
    ELSIF p_type = 'employe_creer' THEN
        IF coalesce(btrim(v_donnees ->> 'nom'), '') = '' OR length(v_donnees ->> 'nom') > 120 THEN RAISE EXCEPTION 'Nom de l''employé obligatoire (120 caractères au plus).'; END IF;
        IF coalesce(v_donnees ->> 'identifiant', '') !~ '^[A-Za-z0-9._-]{2,40}$' THEN RAISE EXCEPTION 'Identifiant de connexion invalide (lettres, chiffres, point, tiret, 2 à 40 caractères).'; END IF;
        SELECT * INTO r FROM cloud_ordre_role(v_donnees ->> 'role');
        IF r.id IS NULL THEN RAISE EXCEPTION 'Rôle inconnu dans cette officine : %', v_donnees ->> 'role'; END IF;
        IF r.systeme THEN RAISE EXCEPTION 'Le rôle de pharmacien titulaire ne se donne jamais à distance.'; END IF;
        v_cible := NULL;
        v_donnees := jsonb_build_object('nom', btrim(v_donnees ->> 'nom'), 'identifiant', v_donnees ->> 'identifiant', 'role', v_donnees ->> 'role');
    ELSIF p_type IN ('employe_modifier', 'employe_bloquer', 'employe_debloquer') THEN
        IF v_cible IS NULL OR v_cible !~ '^[0-9]{1,18}$' THEN RAISE EXCEPTION 'N° de l''employé attendu.'; END IF;
        SELECT x.id, x.nom_complet, x.actif, rr.code AS role, coalesce(rr.systeme, false) AS systeme INTO u
        FROM utilisateur x LEFT JOIN role rr ON rr.officine_id = x.officine_id AND rr.id = x.role_id
        WHERE x.officine_id = p_officine AND x.id = v_cible::bigint;
        IF u.id IS NULL THEN RAISE EXCEPTION 'Employé n° % inconnu au serveur central pour cette officine.', v_cible; END IF;
        IF u.systeme THEN RAISE EXCEPTION 'Le compte d''un pharmacien titulaire ne se modifie jamais à distance.'; END IF;
        v_attendu := jsonb_strip_nulls(jsonb_build_object('role', u.role, 'actif', u.actif, 'nom', u.nom_complet));
        IF p_type = 'employe_modifier' THEN
            IF v_donnees ? 'role' THEN
                SELECT * INTO r FROM cloud_ordre_role(v_donnees ->> 'role');
                IF r.id IS NULL THEN RAISE EXCEPTION 'Rôle inconnu dans cette officine : %', v_donnees ->> 'role'; END IF;
                IF r.systeme THEN RAISE EXCEPTION 'Le rôle de pharmacien titulaire ne se donne jamais à distance.'; END IF;
            END IF;
            IF v_donnees ? 'nom' AND (coalesce(btrim(v_donnees ->> 'nom'), '') = '' OR length(v_donnees ->> 'nom') > 120) THEN
                RAISE EXCEPTION 'Nom de l''employé invalide (1 à 120 caractères).';
            END IF;
            v_donnees := jsonb_strip_nulls(jsonb_build_object('nom', btrim(v_donnees ->> 'nom'), 'role', v_donnees ->> 'role'));
            IF v_donnees = '{}'::jsonb THEN RAISE EXCEPTION 'Rien à modifier : nom ou rôle attendu.'; END IF;
        ELSE
            IF p_type = 'employe_bloquer' AND u.actif IS FALSE THEN RAISE EXCEPTION 'Employé déjà bloqué.'; END IF;
            IF p_type = 'employe_debloquer' AND u.actif IS TRUE THEN RAISE EXCEPTION 'Employé déjà actif.'; END IF;
            v_donnees := '{}'::jsonb;
        END IF;
    ELSIF p_type = 'droits_role' THEN
        SELECT * INTO r FROM cloud_ordre_role(v_cible);
        IF r.id IS NULL THEN RAISE EXCEPTION 'Rôle inconnu dans cette officine : %', v_cible; END IF;
        IF r.systeme THEN RAISE EXCEPTION 'Les droits du pharmacien titulaire ne se modifient jamais.'; END IF;
        IF jsonb_typeof(v_donnees -> 'permissions') IS DISTINCT FROM 'array'
           OR EXISTS (SELECT 1 FROM jsonb_array_elements(v_donnees -> 'permissions') e WHERE jsonb_typeof(e) <> 'string' OR e #>> '{}' !~ '^[a-z0-9_.]{1,60}$') THEN
            RAISE EXCEPTION 'Liste des droits attendue (codes de permission).';
        END IF;
        SELECT coalesce(jsonb_agg(p.permission_code ORDER BY p.permission_code), '[]'::jsonb) INTO v_perms
        FROM role_permission p WHERE p.officine_id = p_officine AND p.role_id = r.id;
        v_attendu := jsonb_build_object('permissions', v_perms);
        v_donnees := jsonb_build_object('permissions', (SELECT coalesce(jsonb_agg(DISTINCT e #>> '{}' ORDER BY e #>> '{}'), '[]'::jsonb)
                                                       FROM jsonb_array_elements(v_donnees -> 'permissions') e));
    ELSIF p_type = 'prix_produit' THEN
        IF v_cible IS NULL OR v_cible !~ '^[0-9]{1,18}$' THEN RAISE EXCEPTION 'N° du produit attendu.'; END IF;
        SELECT x.id, x.nom_commercial, x.prix_public, x.tarif_reference INTO u
        FROM produit x WHERE x.officine_id = p_officine AND x.id = v_cible::bigint;
        IF u.id IS NULL THEN RAISE EXCEPTION 'Produit n° % inconnu au serveur central pour cette officine.', v_cible; END IF;
        IF NOT (v_donnees ? 'prix_public' OR v_donnees ? 'tarif_reference') THEN RAISE EXCEPTION 'Rien à modifier : prix public ou tarif de référence attendu.'; END IF;
        IF (v_donnees ? 'prix_public' AND (jsonb_typeof(v_donnees -> 'prix_public') <> 'number' OR (v_donnees ->> 'prix_public')::numeric <= 0
                                           OR (v_donnees ->> 'prix_public')::numeric <> round((v_donnees ->> 'prix_public')::numeric, 2)))
           OR (v_donnees ? 'tarif_reference' AND (jsonb_typeof(v_donnees -> 'tarif_reference') <> 'number' OR (v_donnees ->> 'tarif_reference')::numeric <= 0
                                                  OR (v_donnees ->> 'tarif_reference')::numeric <> round((v_donnees ->> 'tarif_reference')::numeric, 2))) THEN
            RAISE EXCEPTION 'Prix et tarif : montants en DA supérieurs à 0, deux décimales au plus.';
        END IF;
        -- Une clé absente de l'ordre vaut « inchangé »
        IF (NOT v_donnees ? 'prix_public' OR (v_donnees ->> 'prix_public')::numeric = u.prix_public)
           AND (NOT v_donnees ? 'tarif_reference' OR (v_donnees ->> 'tarif_reference')::numeric IS NOT DISTINCT FROM u.tarif_reference) THEN
            RAISE EXCEPTION 'Rien ne change : prix et tarif identiques à ceux de l''officine.';
        END IF;
        v_attendu := jsonb_build_object('prix_public', u.prix_public, 'tarif_reference', u.tarif_reference);
        v_donnees := jsonb_strip_nulls(jsonb_build_object('nom', u.nom_commercial,
            'prix_public', (v_donnees ->> 'prix_public')::numeric, 'tarif_reference', (v_donnees ->> 'tarif_reference')::numeric));
    ELSIF p_type = 'nomenclature' THEN
        -- Lot S5.3 : désigne un fichier de référentiel du serveur central (téléchargé par l'officine à la confirmation)
        IF jsonb_typeof(v_donnees -> 'fichier') IS DISTINCT FROM 'number' THEN RAISE EXCEPTION 'N° du fichier de nomenclature attendu.'; END IF;
        SELECT f.id, f.libelle, f.fichier, f.empreinte, f.octets INTO u
        FROM referentiel_fichier f WHERE f.id = (v_donnees ->> 'fichier')::bigint AND f.nature = 'nomenclature';
        IF u.id IS NULL THEN RAISE EXCEPTION 'Fichier de nomenclature n° % inconnu.', v_donnees ->> 'fichier'; END IF;
        v_cible := NULL;
        v_donnees := jsonb_build_object('fichier', u.id, 'libelle', u.libelle, 'nom_fichier', u.fichier, 'empreinte', u.empreinte, 'octets', u.octets);
    ELSE
        RAISE EXCEPTION 'Type d''ordre inconnu : %', p_type;
    END IF;

    INSERT INTO ordre_central (officine_id, type, cible, attendu, donnees, auteur, expire_le)
    VALUES (p_officine, p_type, v_cible, v_attendu, v_donnees, btrim(p_auteur), now() + interval '7 days')
    RETURNING id INTO v_id;
    INSERT INTO ordre_central_journal (officine_id, ordre_id, statut, par) VALUES (p_officine, v_id, 'en_attente', btrim(p_auteur));
    PERFORM cloud_journaliser(p_officine, NULL, 'ordre_cree', left(format('ordre %s (%s%s) par %s', v_id, p_type,
        CASE WHEN v_cible IS NULL THEN '' ELSE ' ' || v_cible END, btrim(p_auteur)), 500));
    PERFORM cloud_ordre_publier(v_id);
    RETURN v_id;
END $$;
$def$;
    END IF;
END $rejeu$;

REVOKE EXECUTE ON FUNCTION cloud_creer_ordre(uuid, text, text, jsonb, text) FROM PUBLIC;
