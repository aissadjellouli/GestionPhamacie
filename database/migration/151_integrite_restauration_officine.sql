-- =====================================================================
-- 151 : intégrité après restauration, côté officine (lot S6.5.4,
-- 2026-11-19, « go S6.5.4 » ; docs/conception-synchronisation.md § 31,
-- décisions S6.5-D1, D4 et D6 (a) validées par le titulaire).
--   * Tickets « T- » (S6.5-D6 a, choix du titulaire le 2026-11-19) : à la
--     détection d'un retour en arrière, le compteur est avancé au-delà du
--     plus grand n° reçu par le serveur central (aucun n° réattribué). La
--     plage sautée est notée dans compteur_ticket_saut (ajout seul) et
--     reconnue par le contrôle de continuité (journal_vente_continuite).
--     Seule exception à la règle « +1 à la fois » du compteur (script 112).
--   * Déclaration du changement d'époque (S6.5-D1) : « Repartir sur une
--     nouvelle époque » note dans sync_declaration_epoque ce que le
--     serveur central doit recevoir AVANT tout lot de la nouvelle époque
--     (époques, lots, dates, titulaire, fins de chaîne des journaux).
--   * Prise d'acte du titulaire (S6.5-D4) : integrite_prise_acte (cause,
--     ajout seul), envoyée au serveur central (texte libre filtré).
-- Gardes de rejeu : 112 ne recrée plus compteur_ticket_proteger ni
-- journal_vente_continuite, 149 ne recrée plus sync_nouvelle_epoque si
-- la table compteur_ticket_saut (resp. sync_declaration_epoque) existe.
-- PRÉREQUIS : scripts centraux 022 à 024 passés AVANT (un lot contenant
-- integrite_prise_acte serait refusé par un central sans sa table miroir).
-- Idempotent. Aucune donnée existante modifiée.
-- =====================================================================

-- Fichier en UTF-8 : lu comme tel quel que soit l'encodage du poste qui lance psql.
SET client_encoding = 'UTF8';

-- ---- 1. Plages de n° de tickets sautées (ajout seul, jamais envoyées) ----
CREATE TABLE IF NOT EXISTS compteur_ticket_saut (
    id        bigint      GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    du        bigint      NOT NULL CHECK (du >= 1),
    au        bigint      NOT NULL,
    motif     text        NOT NULL,
    epoque    int,
    cree_le   timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT ck_compteur_ticket_saut_plage CHECK (au >= du)
);
DO $garde$ BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'ck_compteur_ticket_saut_motif'
                   AND pg_get_constraintdef(oid) LIKE '%''restauration''%') THEN
        ALTER TABLE compteur_ticket_saut DROP CONSTRAINT IF EXISTS ck_compteur_ticket_saut_motif;
        ALTER TABLE compteur_ticket_saut ADD CONSTRAINT ck_compteur_ticket_saut_motif CHECK (motif IN ('restauration'));
    END IF;
END $garde$;
COMMENT ON TABLE compteur_ticket_saut IS 'Plages de n° de tickets sautées après un retour en arrière de la base (restauration, bascule) : n° déjà attribués à des ventes reçues par le serveur central ; ajout seul (script 151, décision S6.5-D6 a).';

CREATE OR REPLACE FUNCTION integrite_ajout_seul_restauration() RETURNS trigger
LANGUAGE plpgsql AS $$
BEGIN
    RAISE EXCEPTION '% : ajout seul (ni modification ni suppression)', TG_TABLE_NAME;
END $$;
DROP TRIGGER IF EXISTS trg_compteur_ticket_saut_ajout_seul ON compteur_ticket_saut;
CREATE TRIGGER trg_compteur_ticket_saut_ajout_seul BEFORE UPDATE OR DELETE ON compteur_ticket_saut FOR EACH ROW EXECUTE FUNCTION integrite_ajout_seul_restauration();
DROP TRIGGER IF EXISTS trg_compteur_ticket_saut_non_vidable ON compteur_ticket_saut;
CREATE TRIGGER trg_compteur_ticket_saut_non_vidable BEFORE TRUNCATE ON compteur_ticket_saut FOR EACH STATEMENT EXECUTE FUNCTION integrite_ajout_seul_restauration();

-- ---- 2. Compteur des tickets (remplace la version du script 112) ----
-- Inchangé (+1 à la fois, jamais en arrière, jamais supprimé), sauf un saut posé par sync_avancer_compteur_ticket : permis seulement
-- si la transaction l'annonce (pharma.saut_ticket = nouveau n°) ET qu'une plage sautée du + 1 au nouveau n° vient d'être notée.
CREATE OR REPLACE FUNCTION compteur_ticket_proteger() RETURNS trigger
LANGUAGE plpgsql AS $$
BEGIN
    IF TG_OP = 'UPDATE' AND NEW.dernier_numero = OLD.dernier_numero + 1 AND NEW.prefixe = OLD.prefixe
       AND NEW.premier_numero_continu = OLD.premier_numero_continu AND NEW.mis_en_service_le = OLD.mis_en_service_le THEN
        RETURN NEW;
    END IF;
    IF TG_OP = 'UPDATE' AND NEW.dernier_numero > OLD.dernier_numero + 1
       AND coalesce(current_setting('pharma.saut_ticket', true), '') = NEW.dernier_numero::text
       AND (to_jsonb(NEW) - 'dernier_numero') = (to_jsonb(OLD) - 'dernier_numero')
       AND EXISTS (SELECT 1 FROM compteur_ticket_saut s WHERE s.du = OLD.dernier_numero + 1 AND s.au = NEW.dernier_numero) THEN
        RETURN NEW;
    END IF;
    RAISE EXCEPTION 'Le compteur des tickets avance d''une unité à la fois ; il ne se modifie pas et ne se supprime pas';
END;
$$;

-- Avance le compteur au-delà du plus grand n° reçu par le serveur central (jamais en arrière) ; rend le nombre de n° sautés.
CREATE OR REPLACE FUNCTION sync_avancer_compteur_ticket(p_numero bigint, p_epoque int DEFAULT NULL) RETURNS bigint
LANGUAGE plpgsql AS $$
DECLARE c compteur_ticket;
BEGIN
    IF p_numero IS NULL OR p_numero < 1 THEN RETURN 0; END IF;
    SELECT * INTO c FROM compteur_ticket WHERE id = 1 FOR UPDATE;
    IF NOT FOUND OR p_numero <= c.dernier_numero THEN RETURN 0; END IF;
    IF p_numero - c.dernier_numero > 10000000 THEN
        RAISE EXCEPTION 'Compteur des tickets : saut de % n° refusé (plus de 10 millions)', p_numero - c.dernier_numero USING ERRCODE = 'check_violation';
    END IF;
    INSERT INTO compteur_ticket_saut (du, au, motif, epoque) VALUES (c.dernier_numero + 1, p_numero, 'restauration', p_epoque);
    PERFORM set_config('pharma.saut_ticket', p_numero::text, true);
    UPDATE compteur_ticket SET dernier_numero = p_numero WHERE id = 1;
    PERFORM set_config('pharma.saut_ticket', '', true);
    RETURN p_numero - c.dernier_numero;
END $$;
COMMENT ON FUNCTION sync_avancer_compteur_ticket(bigint, int) IS 'Après un retour en arrière : compteur des tickets avancé au-delà du plus grand n° reçu par le serveur central, plage notée (script 151, S6.5-D6 a).';

-- Contrôle de continuité (remplace la version du script 112) : une plage sautée notée n'est pas un n° manquant
CREATE OR REPLACE FUNCTION journal_vente_continuite()
RETURNS TABLE (anomalie text, numero text, vente_id bigint)
LANGUAGE sql STABLE AS $$
    WITH c AS (SELECT * FROM compteur_ticket WHERE id = 1),
         v AS (SELECT x.id, x.numero, x.statut, substring(x.numero FROM '^' || (SELECT prefixe FROM c) || '([0-9]+)$')::bigint AS n
               FROM vente x)
    SELECT 'numero_manquant', c.prefixe || lpad(s.n::text, GREATEST(6, char_length(s.n::text)), '0'), NULL::bigint
    FROM c, generate_series(c.premier_numero_continu, c.dernier_numero) AS s(n)
    WHERE NOT EXISTS (SELECT 1 FROM v WHERE v.n = s.n)
      AND NOT EXISTS (SELECT 1 FROM compteur_ticket_saut z WHERE s.n BETWEEN z.du AND z.au)
    UNION ALL
    SELECT 'numero_hors_compteur', v.numero, v.id FROM v, c WHERE v.n > c.dernier_numero
    UNION ALL
    SELECT 'vente_sans_ligne', v.numero, v.id FROM v, c
    WHERE v.n >= c.premier_numero_continu AND v.statut IN ('validee', 'annulee')
      AND NOT EXISTS (SELECT 1 FROM journal_vente j WHERE j.vente_id = v.id AND j.evenement = 'vente')
    UNION ALL
    SELECT 'annulation_sans_ligne', v.numero, v.id FROM v
    WHERE v.statut = 'annulee' AND EXISTS (SELECT 1 FROM annulation_vente a, compteur_ticket c WHERE a.vente_id = v.id AND a.cree_le >= c.mis_en_service_le)
      AND NOT EXISTS (SELECT 1 FROM journal_vente j WHERE j.vente_id = v.id AND j.evenement = 'annulation')
    ORDER BY 1, 2
$$;

-- ---- 3. Déclaration du changement d'époque à envoyer au serveur central (jamais envoyée comme table) ----
CREATE TABLE IF NOT EXISTS sync_declaration_epoque (
    nouvelle_epoque      int         PRIMARY KEY,
    ancienne_epoque      int         NOT NULL,
    dernier_lot_local    bigint      NOT NULL,
    dernier_lot_central  bigint,
    detecte_le           timestamptz,
    decide_le            timestamptz NOT NULL DEFAULT now(),
    titulaire_id         bigint,
    fins_chaine          jsonb       NOT NULL DEFAULT '[]',
    envoyee_le           timestamptz,
    avant_premier_lot    boolean,
    resultat             text        CHECK (length(resultat) <= 500),
    CONSTRAINT ck_sync_declaration_epoque_ordre CHECK (nouvelle_epoque > ancienne_epoque)
);
COMMENT ON TABLE sync_declaration_epoque IS 'Changement d''époque à déclarer au serveur central avant tout lot de la nouvelle époque (script 151, S6.5-D1).';

-- Fins de chaîne des journaux chaînés (rang, n°, empreinte de la dernière ligne ; rang 0 si le journal est vide)
CREATE OR REPLACE FUNCTION sync_fins_chaine() RETURNS jsonb
LANGUAGE plpgsql STABLE AS $$
DECLARE r record; v jsonb := '[]'; l record; n int;
BEGIN
    FOR r IN SELECT journal FROM chaine_integrite ORDER BY journal LOOP
        IF to_regclass('public.' || quote_ident(r.journal)) IS NULL THEN CONTINUE; END IF;
        n := 0;
        FOR l IN EXECUTE format(
            'SELECT DISTINCT ON (cle_chaine) cle_chaine, rang, id::text AS id, encode(hash, ''hex'') AS hash FROM public.%I
             WHERE cle_chaine IS NOT NULL ORDER BY cle_chaine, rang DESC', r.journal) LOOP
            v := v || jsonb_build_array(jsonb_build_object('journal', r.journal, 'cle_chaine', l.cle_chaine, 'rang', l.rang, 'id', l.id, 'hash', l.hash));
            n := n + 1;
        END LOOP;
        IF n = 0 THEN
            v := v || jsonb_build_array(jsonb_build_object('journal', r.journal, 'cle_chaine', 'officine', 'rang', 0, 'id', NULL, 'hash', NULL));
        END IF;
    END LOOP;
    RETURN v;
END $$;

-- Nouvelle époque (remplace la version du script 149) : inchangée, sauf la déclaration notée avant la remise à zéro
CREATE OR REPLACE FUNCTION sync_nouvelle_epoque(p_utilisateur bigint, p_epoque_central int, p_epoque_attendue int)
RETURNS TABLE (epoque int, ancienne_epoque int, chargement int)
LANGUAGE plpgsql AS $$
#variable_conflict use_column
DECLARE p sync_parametre; n int; v_nouvelle int;
BEGIN
    IF pg_is_in_recovery() THEN
        RAISE EXCEPTION 'Synchronisation : nouvelle époque impossible sur une base en lecture seule';
    END IF;
    SELECT * INTO p FROM sync_parametre WHERE id = 1 FOR UPDATE;
    IF NOT FOUND THEN RAISE EXCEPTION 'Synchronisation : réglage absent (script 126)'; END IF;
    IF p.retour_arriere_le IS NULL THEN
        RAISE EXCEPTION 'Synchronisation : aucun retour en arrière détecté, nouvelle époque refusée'
            USING ERRCODE = 'check_violation';
    END IF;
    IF p_epoque_attendue IS NULL OR p_epoque_attendue <> p.epoque THEN
        RAISE EXCEPTION 'Synchronisation : l''époque a changé (attendue %, actuelle %), nouvelle époque refusée', p_epoque_attendue, p.epoque
            USING ERRCODE = 'check_violation';
    END IF;
    IF p_epoque_central IS NOT NULL AND (p_epoque_central < 0 OR p_epoque_central >= 2147483647) THEN
        RAISE EXCEPTION 'Synchronisation : époque du serveur central invalide (%)', p_epoque_central USING ERRCODE = 'check_violation';
    END IF;
    -- Jamais une époque déjà connue du central (deux restaurations successives, 2e serveur) : au-delà des deux
    v_nouvelle := greatest(p.epoque, coalesce(p_epoque_central, 0)) + 1;
    -- Script 151 : déclaration à envoyer au serveur central AVANT tout lot de la nouvelle époque (ancienne époque, lots, dates, fins de chaîne)
    INSERT INTO sync_declaration_epoque (nouvelle_epoque, ancienne_epoque, dernier_lot_local, dernier_lot_central, detecte_le, titulaire_id, fins_chaine)
    VALUES (v_nouvelle, p.epoque, p.dernier_lot, p.retour_arriere_central, p.retour_arriere_le, p_utilisateur, sync_fins_chaine())
    ON CONFLICT (nouvelle_epoque) DO NOTHING;
    -- Chargement en cours de l'ancienne époque : son instantané (lignes de l'ancienne époque) ne partira jamais
    UPDATE sync_chargement c SET etat = 'remplace' WHERE c.etat NOT IN ('termine', 'remplace');
    UPDATE sync_parametre SET epoque = v_nouvelle, dernier_lot = 0, dernier_accuse = 0,
                              purge_epoque = NULL, purge_dernier_lot = 0,
                              retour_arriere_le = NULL, retour_arriere_central = NULL, retour_arriere_sequences_le = NULL,
                              derniere_erreur = NULL, derniere_erreur_le = NULL, modifie_le = now()
    WHERE id = 1;
    n := sync_demander_chargement('demande', p_utilisateur);
    epoque := v_nouvelle; ancienne_epoque := p.epoque; chargement := n;
    RETURN NEXT;
END $$;

-- Résultat de l'envoi de la déclaration (envoyée, déjà reçue, ou route absente du central : lots envoyés sans déclaration)
CREATE OR REPLACE FUNCTION sync_noter_declaration(p_nouvelle int, p_avant_premier_lot boolean, p_resultat text) RETURNS void
LANGUAGE sql AS $$
    UPDATE sync_declaration_epoque SET envoyee_le = now(), avant_premier_lot = p_avant_premier_lot, resultat = left(p_resultat, 500)
    WHERE nouvelle_epoque = p_nouvelle AND envoyee_le IS NULL
$$;

-- ---- 4. Prise d'acte du titulaire d'une restauration constatée (montante, ajout seul) ----
CREATE TABLE IF NOT EXISTS integrite_prise_acte (
    id               bigint      GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    nouvelle_epoque  int         NOT NULL UNIQUE CHECK (nouvelle_epoque >= 2),
    cause            text        NOT NULL CHECK (length(btrim(cause)) BETWEEN 3 AND 500),
    utilisateur_id   bigint      NOT NULL REFERENCES utilisateur (id),
    cree_le          timestamptz NOT NULL DEFAULT now()
);
COMMENT ON TABLE integrite_prise_acte IS 'Prise d''acte du titulaire d''une restauration constatée par le serveur central (cause), envoyée au central ; ajout seul (script 151, S6.5-D4).';
DROP TRIGGER IF EXISTS trg_integrite_prise_acte_ajout_seul ON integrite_prise_acte;
CREATE TRIGGER trg_integrite_prise_acte_ajout_seul BEFORE UPDATE OR DELETE ON integrite_prise_acte FOR EACH ROW EXECUTE FUNCTION integrite_ajout_seul_restauration();
DO $officine$
DECLARE v_code uuid := officine_courante();
BEGIN
    IF NOT EXISTS (SELECT 1 FROM information_schema.columns c
                   WHERE c.table_schema = 'public' AND c.table_name = 'integrite_prise_acte' AND c.column_name = 'officine_id') THEN
        EXECUTE format('ALTER TABLE public.integrite_prise_acte ADD COLUMN officine_id uuid NOT NULL DEFAULT %L::uuid', v_code);
    END IF;
    ALTER TABLE public.integrite_prise_acte ALTER COLUMN officine_id SET DEFAULT officine_courante();
END $officine$;

-- Classement pour la synchronisation : la prise d'acte part au central (cause filtrée comme tout texte libre), le reste jamais
INSERT INTO sync_table (nom_table, sens, colonnes_exclues, colonnes_patient, porteur, motif) VALUES
    ('integrite_prise_acte', 'montante', '{}', '{}', false, 'Prise d''acte du titulaire d''une restauration constatée par le serveur central, script 151'),
    ('compteur_ticket_saut', 'jamais', '{}', '{}', false, 'Plages de n° de tickets sautées après un retour en arrière, script 151'),
    ('sync_declaration_epoque', 'jamais', '{}', '{}', false, 'Déclaration du changement d''époque envoyée par la route du serveur central, script 151')
ON CONFLICT (nom_table) DO NOTHING;
UPDATE sync_table SET cle_colonnes = '{id}', colonnes_texte = '{cause}'
WHERE nom_table = 'integrite_prise_acte'
  AND (cle_colonnes, colonnes_texte) IS DISTINCT FROM ('{id}'::text[], '{cause}'::text[]);
UPDATE sync_table SET cle_colonnes = '{nouvelle_epoque}'
WHERE nom_table = 'sync_declaration_epoque' AND cle_colonnes IS DISTINCT FROM '{nouvelle_epoque}'::text[];
DO $declencheurs$ BEGIN
    IF coalesce((SELECT active FROM sync_parametre WHERE id = 1), false) THEN PERFORM sync_installer_declencheurs(); END IF;
END $declencheurs$;
