-- =====================================================================
-- Base centrale : mutualisation des réglages (2026-11-16, lot M3,
-- docs/conception-synchronisation.md § 16 bis ; décisions D1 à D14 acceptées).
-- Script de la base CENTRALE. Idempotent ; à passer après 009 (et après
-- 002 régénéré : il contient l'état montant reglage_mutualise des officines).
--   * cloud_reglage_table : liste fermée des 11 tables mutualisables (mode de
--     départ du § 16, pour information).
--   * cloud_reglage_modele : un modèle par (périmètre, table). D1 : un seul
--     périmètre « tous » pour l'instant (la clé prévoit des groupes plus tard).
--     Créé SANS valeur pour les 11 tables (D12 : rien n'est publié
--     automatiquement ; valeur NULL = rien ne descend, l'officine reste
--     maîtresse de la table). Mode propose / impose, valeur au format mutu-v1,
--     version du modèle (+1 à chaque publication ou changement de mode d'une
--     table publiée), auteur.
--   * cloud_journal_reglage : journal en AJOUT SEUL (qui, quand, avant / après).
--   * officine.mutualisation_liberee_le : officine sortie de la mutualisation (D3).
--   * Fonctions d'administration, sans EXECUTE pour PUBLIC ni pour
--     pharma_cloud_api (seul l'outil d'administration, propriétaire de la base,
--     publie) : cloud_publier_reglage, cloud_regler_mode_reglage,
--     cloud_liberer_reglages, cloud_retablir_reglages ; lecture de l'état des
--     officines : cloud_etat_reglages.
--   * cloud_descendants remplacée : logique du script 009 conservée à
--     l'identique, plus un élément « mutu_<table> » par table PUBLIÉE du
--     périmètre de l'officine (nouvelle version descendante seulement si
--     l'élément change). Officine libérée : élément de mode « local ».
--   * Sécurité : pharma_cloud_api ne fait que LIRE les modèles (sécurité par
--     ligne : politique de lecture seule) ; aucun droit sur le journal.
-- =====================================================================

-- ---- Liste fermée des 11 tables ----
CREATE OR REPLACE FUNCTION cloud_reglage_tables() RETURNS text[]
LANGUAGE sql IMMUTABLE AS $$
    SELECT ARRAY['motif_reglable', 'parametre_alerte', 'parametre_fiscal', 'parametre_formulaire_sms', 'parametre_inventaire',
                 'parametre_serialisation', 'parametre_substitution', 'parametre_surveillance', 'parametre_tracabilite',
                 'regle_alerte_psychotrope', 'remise_automatique']::text[]
$$;

CREATE TABLE IF NOT EXISTS cloud_reglage_table (
    nom_table   text PRIMARY KEY CHECK (nom_table = ANY (cloud_reglage_tables())),
    libelle     text NOT NULL CHECK (length(btrim(libelle)) BETWEEN 1 AND 100),
    mode_depart text NOT NULL CHECK (mode_depart IN ('propose', 'impose'))
);
-- Valeurs de départ proposées au § 16 (information seulement : rien n'est publié)
INSERT INTO cloud_reglage_table (nom_table, libelle, mode_depart) VALUES
    ('parametre_fiscal', 'Conservation et export fiscal', 'impose'),
    ('regle_alerte_psychotrope', 'Règles d''alerte des psychotropes', 'propose'),
    ('parametre_tracabilite', 'Traçabilité des boîtes', 'impose'),
    ('parametre_serialisation', 'Sérialisation', 'impose'),
    ('parametre_substitution', 'Substitution', 'propose'),
    ('motif_reglable', 'Motifs réglables', 'propose'),
    ('parametre_formulaire_sms', 'Formulaire d''accord SMS', 'propose'),
    ('parametre_surveillance', 'Contrôle interne (seuils)', 'propose'),
    ('parametre_alerte', 'Seuils d''alerte de péremption et de stock', 'propose'),
    ('parametre_inventaire', 'Inventaire (seuils)', 'propose'),
    ('remise_automatique', 'Remises automatiques par catégorie', 'propose')
ON CONFLICT (nom_table) DO NOTHING;

CREATE TABLE IF NOT EXISTS cloud_reglage_modele (
    perimetre      text        NOT NULL DEFAULT 'tous' CHECK (perimetre ~ '^[a-z0-9_-]{1,40}$'),
    nom_table      text        NOT NULL REFERENCES cloud_reglage_table (nom_table),
    mode           text        NOT NULL CHECK (mode IN ('propose', 'impose')),
    valeur         jsonb       CHECK (valeur IS NULL OR (jsonb_typeof(valeur) = 'object' AND octet_length(valeur::text) <= 60000)),
    version_modele int         NOT NULL DEFAULT 0 CHECK (version_modele >= 0),
    ajouts_locaux  boolean     NOT NULL DEFAULT true,
    modifie_le     timestamptz NOT NULL DEFAULT now(),
    modifie_par    text        NOT NULL CHECK (length(btrim(modifie_par)) BETWEEN 1 AND 100),
    PRIMARY KEY (perimetre, nom_table),
    -- Pas de valeur : jamais publié (version 0) ; valeur : au moins la version 1
    CONSTRAINT ck_reglage_modele_version CHECK ((valeur IS NULL) = (version_modele = 0))
);
CREATE INDEX IF NOT EXISTS ix_reglage_modele_table ON cloud_reglage_modele (nom_table);

-- D12 : les 11 modèles existent sans valeur, dans le mode de départ ; rien ne descend
INSERT INTO cloud_reglage_modele (perimetre, nom_table, mode, modifie_par)
SELECT 'tous', t.nom_table, t.mode_depart, 'script 010' FROM cloud_reglage_table t
ON CONFLICT (perimetre, nom_table) DO NOTHING;

CREATE TABLE IF NOT EXISTS cloud_journal_reglage (
    id              bigint      GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    le              timestamptz NOT NULL DEFAULT now(),
    perimetre       text        NOT NULL DEFAULT 'tous',
    officine_id     uuid,
    nom_table       text        CHECK (nom_table IS NULL OR nom_table = ANY (cloud_reglage_tables())),
    action          text        NOT NULL CHECK (action IN ('publication', 'mode', 'liberation', 'retour')),
    mode_avant      text        CHECK (mode_avant IS NULL OR mode_avant IN ('local', 'propose', 'impose')),
    mode_apres      text        CHECK (mode_apres IS NULL OR mode_apres IN ('local', 'propose', 'impose')),
    version_modele  int,
    valeur_avant    jsonb,
    valeur_apres    jsonb,
    auteur          text        NOT NULL CHECK (length(btrim(auteur)) BETWEEN 1 AND 100),
    compte_systeme  text        CHECK (length(compte_systeme) <= 200),
    poste           text        CHECK (length(poste) <= 200),
    motif           text        CHECK (length(motif) <= 500)
);
CREATE INDEX IF NOT EXISTS ix_journal_reglage_table ON cloud_journal_reglage (nom_table, le);
CREATE INDEX IF NOT EXISTS ix_journal_reglage_officine ON cloud_journal_reglage (officine_id, le) WHERE officine_id IS NOT NULL;

-- Ajout seul (fonction cloud_ajout_seul du script 001)
DROP TRIGGER IF EXISTS trg_journal_reglage_ajout_seul ON cloud_journal_reglage;
CREATE TRIGGER trg_journal_reglage_ajout_seul BEFORE UPDATE OR DELETE ON cloud_journal_reglage FOR EACH ROW EXECUTE FUNCTION cloud_ajout_seul();
DROP TRIGGER IF EXISTS trg_journal_reglage_non_vidable ON cloud_journal_reglage;
CREATE TRIGGER trg_journal_reglage_non_vidable BEFORE TRUNCATE ON cloud_journal_reglage FOR EACH STATEMENT EXECUTE FUNCTION cloud_ajout_seul();

-- Officine sortie de la mutualisation (D3) : ses tables publiées descendent en mode « local »
ALTER TABLE officine ADD COLUMN IF NOT EXISTS mutualisation_liberee_le timestamptz;

-- ---- Droits et sécurité par ligne : l'API ne fait que LIRE les modèles ----
ALTER TABLE cloud_reglage_table ENABLE ROW LEVEL SECURITY;
ALTER TABLE cloud_reglage_modele ENABLE ROW LEVEL SECURITY;
ALTER TABLE cloud_journal_reglage ENABLE ROW LEVEL SECURITY;   -- aucune politique : invisible pour tout rôle autre que le propriétaire
DROP POLICY IF EXISTS lecture_api ON cloud_reglage_table;
CREATE POLICY lecture_api ON cloud_reglage_table FOR SELECT TO pharma_cloud_api USING (true);
DROP POLICY IF EXISTS lecture_api ON cloud_reglage_modele;
CREATE POLICY lecture_api ON cloud_reglage_modele FOR SELECT TO pharma_cloud_api USING (true);
REVOKE ALL ON cloud_reglage_table, cloud_reglage_modele, cloud_journal_reglage FROM PUBLIC;
REVOKE INSERT, UPDATE, DELETE, TRUNCATE, REFERENCES, TRIGGER ON cloud_reglage_table, cloud_reglage_modele FROM pharma_cloud_api;
REVOKE ALL ON cloud_journal_reglage FROM pharma_cloud_api;
GRANT SELECT ON cloud_reglage_table, cloud_reglage_modele TO pharma_cloud_api;

-- ---- Fonctions d'administration (outil d'administration seulement) ----
-- Contrôles communs : auteur obligatoire (D10), table de la liste fermée
CREATE OR REPLACE FUNCTION cloud_reglage_controle(p_table text, p_auteur text) RETURNS void
LANGUAGE plpgsql IMMUTABLE AS $$
BEGIN
    IF p_auteur IS NULL OR length(btrim(p_auteur)) NOT BETWEEN 1 AND 100 THEN
        RAISE EXCEPTION 'Mutualisation : l''auteur est obligatoire (1 à 100 caractères)' USING ERRCODE = 'P0001';
    END IF;
    IF p_table IS NULL OR NOT p_table = ANY (cloud_reglage_tables()) THEN
        RAISE EXCEPTION 'Mutualisation : table non mutualisable : % (attendu : %)', p_table, array_to_string(cloud_reglage_tables(), ', ') USING ERRCODE = 'P0001';
    END IF;
END $$;

-- Publie la valeur d'une table pour le périmètre ; p_mode / p_ajouts_locaux NULL : valeurs en vigueur. Renvoie la version du modèle
-- (inchangée, sans journal, si ni la valeur ni le mode ni les ajouts locaux ne changent).
CREATE OR REPLACE FUNCTION cloud_publier_reglage(p_table text, p_valeur jsonb, p_auteur text, p_mode text DEFAULT NULL,
    p_ajouts_locaux boolean DEFAULT NULL, p_compte text DEFAULT NULL, p_poste text DEFAULT NULL, p_motif text DEFAULT NULL,
    p_perimetre text DEFAULT 'tous') RETURNS int
LANGUAGE plpgsql AS $$
DECLARE m cloud_reglage_modele; v_mode text; v_ajouts boolean; v_version int;
BEGIN
    PERFORM cloud_reglage_controle(p_table, p_auteur);
    IF p_valeur IS NULL OR jsonb_typeof(p_valeur) <> 'object' THEN
        RAISE EXCEPTION 'Mutualisation : la valeur doit être un objet JSON' USING ERRCODE = 'P0001';
    END IF;
    IF octet_length(p_valeur::text) > 60000 THEN
        RAISE EXCEPTION 'Mutualisation : valeur trop volumineuse (60000 octets au plus, l''élément descendant est limité à 64 Kio)' USING ERRCODE = 'P0001';
    END IF;
    IF p_mode IS NOT NULL AND p_mode NOT IN ('propose', 'impose') THEN
        RAISE EXCEPTION 'Mutualisation : mode inconnu : % (propose ou impose)', p_mode USING ERRCODE = 'P0001';
    END IF;
    SELECT * INTO m FROM cloud_reglage_modele WHERE perimetre = p_perimetre AND nom_table = p_table FOR UPDATE;
    IF NOT FOUND THEN RAISE EXCEPTION 'Mutualisation : périmètre inconnu : %', p_perimetre USING ERRCODE = 'P0001'; END IF;
    v_mode := coalesce(p_mode, m.mode);
    v_ajouts := coalesce(p_ajouts_locaux, m.ajouts_locaux);
    IF m.valeur IS NOT DISTINCT FROM p_valeur AND m.mode = v_mode AND m.ajouts_locaux = v_ajouts THEN RETURN m.version_modele; END IF;
    v_version := m.version_modele + 1;
    UPDATE cloud_reglage_modele SET valeur = p_valeur, mode = v_mode, ajouts_locaux = v_ajouts, version_modele = v_version,
                                    modifie_le = now(), modifie_par = btrim(p_auteur)
    WHERE perimetre = p_perimetre AND nom_table = p_table;
    INSERT INTO cloud_journal_reglage (perimetre, nom_table, action, mode_avant, mode_apres, version_modele, valeur_avant, valeur_apres, auteur, compte_systeme, poste, motif)
    VALUES (p_perimetre, p_table, 'publication', m.mode, v_mode, v_version, m.valeur, p_valeur, btrim(p_auteur), p_compte, p_poste, p_motif);
    RETURN v_version;
END $$;

-- Change le mode (propose / impose) d'une table. Table publiée : nouvelle version du modèle ; sinon le mode est seulement noté.
CREATE OR REPLACE FUNCTION cloud_regler_mode_reglage(p_table text, p_mode text, p_auteur text, p_compte text DEFAULT NULL,
    p_poste text DEFAULT NULL, p_motif text DEFAULT NULL, p_perimetre text DEFAULT 'tous') RETURNS int
LANGUAGE plpgsql AS $$
DECLARE m cloud_reglage_modele; v_version int;
BEGIN
    PERFORM cloud_reglage_controle(p_table, p_auteur);
    IF p_mode IS NULL OR p_mode NOT IN ('propose', 'impose') THEN
        RAISE EXCEPTION 'Mutualisation : mode inconnu : % (propose ou impose)', p_mode USING ERRCODE = 'P0001';
    END IF;
    SELECT * INTO m FROM cloud_reglage_modele WHERE perimetre = p_perimetre AND nom_table = p_table FOR UPDATE;
    IF NOT FOUND THEN RAISE EXCEPTION 'Mutualisation : périmètre inconnu : %', p_perimetre USING ERRCODE = 'P0001'; END IF;
    IF m.mode = p_mode THEN RETURN m.version_modele; END IF;
    v_version := CASE WHEN m.valeur IS NULL THEN 0 ELSE m.version_modele + 1 END;
    UPDATE cloud_reglage_modele SET mode = p_mode, version_modele = v_version, modifie_le = now(), modifie_par = btrim(p_auteur)
    WHERE perimetre = p_perimetre AND nom_table = p_table;
    INSERT INTO cloud_journal_reglage (perimetre, nom_table, action, mode_avant, mode_apres, version_modele, valeur_avant, valeur_apres, auteur, compte_systeme, poste, motif)
    VALUES (p_perimetre, p_table, 'mode', m.mode, p_mode, v_version, m.valeur, m.valeur, btrim(p_auteur), p_compte, p_poste, p_motif);
    RETURN v_version;
END $$;

-- Libère une officine (départ, D3) : ses tables publiées descendent en mode « local ». Renvoie le nombre de tables concernées.
CREATE OR REPLACE FUNCTION cloud_liberer_reglages(p_officine uuid, p_auteur text, p_compte text DEFAULT NULL, p_poste text DEFAULT NULL,
    p_motif text DEFAULT NULL, p_perimetre text DEFAULT 'tous') RETURNS int
LANGUAGE plpgsql AS $$
DECLARE n int := 0; m record;
BEGIN
    PERFORM cloud_reglage_controle((cloud_reglage_tables())[1], p_auteur);
    IF NOT EXISTS (SELECT 1 FROM officine WHERE id = p_officine) THEN RAISE EXCEPTION 'Officine inconnue : %', p_officine USING ERRCODE = 'P0001'; END IF;
    IF EXISTS (SELECT 1 FROM officine WHERE id = p_officine AND mutualisation_liberee_le IS NOT NULL) THEN RETURN 0; END IF;
    UPDATE officine SET mutualisation_liberee_le = now() WHERE id = p_officine;
    FOR m IN SELECT * FROM cloud_reglage_modele WHERE perimetre = p_perimetre AND valeur IS NOT NULL ORDER BY nom_table LOOP
        INSERT INTO cloud_journal_reglage (perimetre, officine_id, nom_table, action, mode_avant, mode_apres, version_modele, auteur, compte_systeme, poste, motif)
        VALUES (p_perimetre, p_officine, m.nom_table, 'liberation', m.mode, 'local', m.version_modele, btrim(p_auteur), p_compte, p_poste, p_motif);
        n := n + 1;
    END LOOP;
    IF n = 0 THEN
        INSERT INTO cloud_journal_reglage (perimetre, officine_id, action, mode_apres, auteur, compte_systeme, poste, motif)
        VALUES (p_perimetre, p_officine, 'liberation', 'local', btrim(p_auteur), p_compte, p_poste, p_motif);
    END IF;
    RETURN n;
END $$;

-- Annule la libération : les modèles publiés redescendent comme pour toute officine
CREATE OR REPLACE FUNCTION cloud_retablir_reglages(p_officine uuid, p_auteur text, p_compte text DEFAULT NULL, p_poste text DEFAULT NULL,
    p_motif text DEFAULT NULL, p_perimetre text DEFAULT 'tous') RETURNS int
LANGUAGE plpgsql AS $$
DECLARE n int := 0; m record;
BEGIN
    PERFORM cloud_reglage_controle((cloud_reglage_tables())[1], p_auteur);
    IF NOT EXISTS (SELECT 1 FROM officine WHERE id = p_officine) THEN RAISE EXCEPTION 'Officine inconnue : %', p_officine USING ERRCODE = 'P0001'; END IF;
    IF EXISTS (SELECT 1 FROM officine WHERE id = p_officine AND mutualisation_liberee_le IS NULL) THEN RETURN 0; END IF;
    UPDATE officine SET mutualisation_liberee_le = NULL WHERE id = p_officine;
    FOR m IN SELECT * FROM cloud_reglage_modele WHERE perimetre = p_perimetre AND valeur IS NOT NULL ORDER BY nom_table LOOP
        INSERT INTO cloud_journal_reglage (perimetre, officine_id, nom_table, action, mode_avant, mode_apres, version_modele, auteur, compte_systeme, poste, motif)
        VALUES (p_perimetre, p_officine, m.nom_table, 'retour', 'local', m.mode, m.version_modele, btrim(p_auteur), p_compte, p_poste, p_motif);
        n := n + 1;
    END LOOP;
    IF n = 0 THEN
        INSERT INTO cloud_journal_reglage (perimetre, officine_id, action, mode_avant, auteur, compte_systeme, poste, motif)
        VALUES (p_perimetre, p_officine, 'retour', 'local', btrim(p_auteur), p_compte, p_poste, p_motif);
    END IF;
    RETURN n;
END $$;

-- État de la mutualisation de chaque officine (miroir reglage_mutualise, sécurité par ligne forcée : l'officine est posée une par une)
CREATE OR REPLACE FUNCTION cloud_etat_reglages(p_table text DEFAULT NULL)
RETURNS TABLE (officine_id uuid, libelle text, nom_table text, mode text, mode_recu text, version_modele_recue bigint,
               version_modele_appliquee bigint, etat text, motif_rejet text, origine text, recu_le timestamptz, applique_le timestamptz,
               liberee_le timestamptz)
LANGUAGE plpgsql AS $$
DECLARE o record; v_avant text := current_setting('app.officine_id', true);
BEGIN
    IF p_table IS NOT NULL AND NOT p_table = ANY (cloud_reglage_tables()) THEN
        RAISE EXCEPTION 'Mutualisation : table non mutualisable : %', p_table USING ERRCODE = 'P0001';
    END IF;
    FOR o IN SELECT x.id, x.libelle AS lib, x.mutualisation_liberee_le AS liberee FROM officine x WHERE x.resiliee_le IS NULL ORDER BY x.libelle, x.id LOOP
        PERFORM set_config('app.officine_id', o.id::text, true);
        RETURN QUERY SELECT o.id, o.lib, r.nom_table, r.mode, r.mode_recu, r.version_modele_recue, r.version_modele_appliquee, r.etat,
                            r.motif_rejet, r.origine, r.recu_le, r.applique_le, o.liberee
                     FROM reglage_mutualise r WHERE r.officine_id = o.id AND (p_table IS NULL OR r.nom_table = p_table) ORDER BY r.nom_table;
    END LOOP;
    PERFORM set_config('app.officine_id', coalesce(v_avant, ''), true);
END $$;

REVOKE EXECUTE ON FUNCTION cloud_reglage_controle(text, text), cloud_publier_reglage(text, jsonb, text, text, boolean, text, text, text, text),
    cloud_regler_mode_reglage(text, text, text, text, text, text, text), cloud_liberer_reglages(uuid, text, text, text, text, text),
    cloud_retablir_reglages(uuid, text, text, text, text, text), cloud_etat_reglages(text), cloud_reglage_tables() FROM PUBLIC;
REVOKE EXECUTE ON FUNCTION cloud_reglage_controle(text, text), cloud_publier_reglage(text, jsonb, text, text, boolean, text, text, text, text),
    cloud_regler_mode_reglage(text, text, text, text, text, text, text), cloud_liberer_reglages(uuid, text, text, text, text, text),
    cloud_retablir_reglages(uuid, text, text, text, text, text), cloud_etat_reglages(text) FROM pharma_cloud_api;
-- cloud_reglage_tables() (liste fermée, sans donnée) est lue par la contrainte de la table : l'API la lit aussi
GRANT EXECUTE ON FUNCTION cloud_reglage_tables() TO pharma_cloud_api;

-- ---- Voie descendante : cloud_descendants remplace la version du script 009 ----
-- Logique de 009 conservée à l'identique (familles, central restauré, acquittement) ; ajout : un élément « mutu_<table> »
-- par table publiée du périmètre « tous », publié seulement s'il a changé (cloud_publier_descendant).
CREATE OR REPLACE FUNCTION cloud_descendants(p_apres bigint)
RETURNS TABLE (nom_table text, cle text, version bigint, donnees jsonb, version_max bigint)
LANGUAGE plpgsql SECURITY DEFINER SET search_path = public, pg_temp AS $$
DECLARE v_off uuid := cloud_officine_courante(); v_max bigint; v_n bigint; v_liberee boolean; m record;
BEGIN
    IF v_off IS NULL THEN RAISE EXCEPTION 'Officine courante non posée (app.officine_id)'; END IF;
    PERFORM cloud_publier_descendant('reglage_central', 'familles_envoyees',
        jsonb_build_object('familles', to_jsonb(cloud_familles_envoyees(v_off))));
    -- Modèles de réglages publiés (D12 : rien tant que l'administrateur n'a pas publié) ; officine libérée : mode « local »
    SELECT x.mutualisation_liberee_le IS NOT NULL INTO v_liberee FROM officine x WHERE x.id = v_off;
    FOR m IN SELECT r.* FROM cloud_reglage_modele r WHERE r.perimetre = 'tous' AND r.valeur IS NOT NULL ORDER BY r.nom_table LOOP
        PERFORM cloud_publier_descendant('reglage_central', 'mutu_' || m.nom_table,
            CASE WHEN coalesce(v_liberee, false)
                 THEN jsonb_build_object('format', 'mutu-v1', 'mode', 'local', 'version_modele', m.version_modele)
                 ELSE jsonb_build_object('format', 'mutu-v1', 'mode', m.mode, 'version_modele', m.version_modele,
                                         'ajouts_locaux', m.ajouts_locaux,
                                         'publie_le', to_char(m.modifie_le AT TIME ZONE 'UTC', 'YYYY-MM-DD"T"HH24:MI:SS"Z"'),
                                         'auteur', m.modifie_par, 'valeur', m.valeur) END);
    END LOOP;
    SELECT o.version_max INTO v_max FROM officine_descendant o WHERE o.officine_id = v_off;
    IF p_apres IS NOT NULL AND p_apres > v_max AND p_apres <= 1000000000000 THEN
        -- Serveur central restauré : l'officine a vu des versions que ce serveur ne connaît plus. Tout est republié au-dessus.
        WITH o AS (SELECT x.nom_table AS t, x.cle AS c, row_number() OVER (ORDER BY x.version, x.nom_table, x.cle) AS rn
                   FROM descendant x WHERE x.officine_id = v_off)
        UPDATE descendant d SET version = p_apres + o.rn, modifie_le = now() FROM o
        WHERE d.officine_id = v_off AND d.nom_table = o.t AND d.cle = o.c;
        GET DIAGNOSTICS v_n = ROW_COUNT;
        UPDATE officine_descendant o SET version_max = p_apres + v_n WHERE o.officine_id = v_off;
        PERFORM cloud_journaliser(v_off, NULL, 'descendant_remonte', 'version descendante de l''officine supérieure à celle du serveur central : versions remontées');
        v_max := p_apres + v_n;
    END IF;
    IF p_apres IS NOT NULL AND p_apres BETWEEN 0 AND v_max THEN
        UPDATE officine_descendant o SET version_acquittee = greatest(o.version_acquittee, p_apres), acquittee_le = now()
        WHERE o.officine_id = v_off AND o.version_acquittee < p_apres;
    END IF;
    RETURN QUERY SELECT d.nom_table, d.cle, d.version, d.donnees, v_max FROM descendant d
                 WHERE d.officine_id = v_off AND d.version > coalesce(p_apres, 0) ORDER BY d.version;
END $$;
REVOKE EXECUTE ON FUNCTION cloud_descendants(bigint) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION cloud_descendants(bigint) TO pharma_cloud_api;

COMMENT ON TABLE cloud_reglage_table IS 'Serveur central : les 11 tables de réglages mutualisables et leur mode de départ (information, rien n''est publié) (script 010).';
COMMENT ON TABLE cloud_reglage_modele IS 'Serveur central : modèle de réglage par (périmètre, table) ; valeur NULL = rien publié, rien ne descend (D12) ; écrit seulement par l''outil d''administration (script 010).';
COMMENT ON TABLE cloud_journal_reglage IS 'Serveur central : journal des actions de mutualisation (qui, quand, avant / après), en ajout seul (script 010).';
COMMENT ON COLUMN officine.mutualisation_liberee_le IS 'Officine sortie de la mutualisation : ses modèles publiés descendent en mode local (script 010).';
