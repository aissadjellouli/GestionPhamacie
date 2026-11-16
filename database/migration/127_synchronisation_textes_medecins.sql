-- =====================================================================
-- 127 — Synchronisation : textes libres filtrés, envoi des médecins en option
-- ---------------------------------------------------------------------
-- Décisions du titulaire (2026-11-16) :
--  1. Textes libres (motifs, commentaires, messages, libellés saisis) :
--     avant envoi, les données sensibles sont masquées
--       - adresses courriel            -> [COURRIEL]
--       - numéros de téléphone         -> [TÉL]
--       - suites de 9 chiffres et plus  -> [N°] (assuré, NIN, pièce)
--       - nom ou prénom d'un patient   -> son code P-… (un seul patient)
--                                         ou [NOM] (plusieurs patients)
--       - nom d'un porteur du registre -> [NOM]
--     Option sync_parametre.textes_libres : 'filtres' (défaut) ou
--     'retires' (texte non envoyé, valeur NULL).
--     Limite : un nom inconnu de la base ou mal orthographié n'est pas repéré.
--  2. Médecins : option sync_parametre.envoyer_medecins (défaut : non).
--     Activée : nom, spécialité et wilaya envoyés (téléphone, adresse,
--     courriel, n° d'ordre et code prestataire retirés). Les ordonnances
--     restent dans l'officine.
-- Idempotent ; aucune donnée existante modifiée ; synchronisation toujours
-- désactivée par défaut.
-- =====================================================================

ALTER TABLE sync_parametre ADD COLUMN IF NOT EXISTS textes_libres text NOT NULL DEFAULT 'filtres';
ALTER TABLE sync_parametre ADD COLUMN IF NOT EXISTS envoyer_medecins boolean NOT NULL DEFAULT false;
DO $$ BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'sync_parametre_textes_libres_check') THEN
        ALTER TABLE sync_parametre ADD CONSTRAINT sync_parametre_textes_libres_check CHECK (textes_libres IN ('filtres', 'retires'));
    END IF;
END $$;

ALTER TABLE sync_table ADD COLUMN IF NOT EXISTS colonnes_texte text[] NOT NULL DEFAULT '{}';

-- Colonnes de texte saisi librement (tables envoyées)
UPDATE sync_table s SET colonnes_texte = v.cols
FROM (VALUES
    ('alerte',                        '{action_prise,message}'::text[]),
    ('alerte_surveillance',           '{action_prise,message}'),
    ('annulation_registre',           '{motif}'),
    ('annulation_vente',              '{motif}'),
    ('avoir_mouvement',               '{motif}'),
    ('boite_evenement',               '{commentaire}'),
    ('bordereau_vente',               '{rejet_texte}'),
    ('campagne_inventaire',           '{commentaire}'),
    ('correction_lot',                '{motif}'),
    ('evenement_caisse',              '{detail}'),
    ('facture_client',                '{commentaire,destinataire,motif,motif_hors_suivi,motif_rejet}'),
    ('facture_client_version',        '{motif}'),
    ('forcage_alerte_psychotrope',    '{motif}'),
    ('journal_registre_psychotrope',  '{motif}'),
    ('journal_vente',                 '{motif}'),
    ('ligne_vente',                   '{raison_lot}'),
    ('lot',                           '{motif_blocage}'),
    ('mouvement_tiroir',              '{beneficiaire,detail,libelle,motif_correction,raison}'),
    ('mouvement_tresorerie',          '{libelle}'),
    ('patient',                       '{lien}'),
    ('pret_bancaire',                 '{objet}'),
    ('produit',                       '{motif_blocage}'),
    ('reglement_fournisseur',         '{commentaire,motif_annulation}'),
    ('regularisation_part_organisme', '{motif_annulation}'),
    ('remboursement_titulaire',       '{libelle}'),
    ('remise_automatique',            '{motif}'),
    ('retour_client',                 '{motif,motif_texte}'),
    ('retour_fournisseur',            '{motif}'),
    ('utilisation_financement',       '{libelle}'),
    ('vente',                         '{chifa_attente_motif_texte,chifa_ecart_detail,chifa_ecart_motif,chifa_exclusion_texte}'),
    ('verification_serialisation',    '{message}')
) AS v(nom_table, cols)
WHERE s.nom_table = v.nom_table;

-- Médecins : colonnes retirées si l'option est activée
UPDATE sync_table
SET colonnes_exclues = '{telephone,adresse,email,numero_ordre,code_prestataire}',
    motif = 'Envoi en option (sync_parametre.envoyer_medecins, défaut non) : nom, spécialité, wilaya'
WHERE nom_table = 'medecin';

-- Recherche des noms de patients par mot (filtre des textes)
CREATE INDEX IF NOT EXISTS ix_patient_nom_minuscule ON patient (lower(nom));
CREATE INDEX IF NOT EXISTS ix_patient_prenom_minuscule ON patient (lower(prenom));

-- ---------------------------------------------------------------------
-- Filtre d'un texte libre
-- ---------------------------------------------------------------------
CREATE OR REPLACE FUNCTION sync_filtrer_texte(p_texte text) RETURNS text
LANGUAGE plpgsql AS $$
DECLARE
    t       text := p_texte;
    m       text;
    v_ids   bigint[];
    v_rempl text;
BEGIN
    IF t IS NULL OR btrim(t) = '' THEN RETURN t; END IF;
    t := regexp_replace(t, '[[:alnum:]._%+-]+@[[:alnum:].-]+\.[[:alpha:]]{2,}', '[COURRIEL]', 'g');
    t := regexp_replace(t, '(\+|00)?213[ .-]?[0-9]([ .-]?[0-9]){7,8}', '[TÉL]', 'g');
    t := regexp_replace(t, '\m0[2-7]([ .-]?[0-9]){8}\M', '[TÉL]', 'g');
    t := regexp_replace(t, '[0-9]([ .-]?[0-9]){8,}', '[N°]', 'g');
    FOR m IN SELECT DISTINCT lower(x[1]) FROM regexp_matches(t, '([[:alpha:]][[:alpha:]''-]{2,})', 'g') AS x LOOP
        SELECT array_agg(DISTINCT id) INTO v_ids FROM patient WHERE lower(nom) = m OR lower(prenom) = m;
        IF v_ids IS NOT NULL THEN
            v_rempl := CASE WHEN array_length(v_ids, 1) = 1 THEN sync_code_patient(v_ids[1]) ELSE '[NOM]' END;
        ELSIF EXISTS (SELECT 1 FROM registre_psychotrope WHERE lower(porteur_nom) ~ ('\m' || m || '\M')) THEN
            v_rempl := '[NOM]';
        ELSE
            CONTINUE;
        END IF;
        t := regexp_replace(t, '\m' || m || '\M', v_rempl, 'gi');
    END LOOP;
    RETURN t;
END $$;

-- Sens effectif d'une table (médecins : selon l'option)
CREATE OR REPLACE FUNCTION sync_sens_effectif(p_table sync_table, p_param sync_parametre) RETURNS text
LANGUAGE sql STABLE AS $$
    SELECT CASE WHEN p_table.nom_table = 'medecin' AND p_param.envoyer_medecins THEN 'montante' ELSE p_table.sens END
$$;

-- Ligne minimisée : porteur sous code, colonnes exclues retirées, patients sous code, textes filtrés ou retirés
CREATE OR REPLACE FUNCTION sync_minimiser(p_table sync_table, p_ligne jsonb) RETURNS jsonb
LANGUAGE plpgsql AS $$
DECLARE j jsonb := p_ligne; c text; v_mode text;
BEGIN
    IF p_table.porteur THEN
        j := j || jsonb_build_object('porteur_code', sync_code_porteur(j ->> 'porteur_nom', j ->> 'porteur_piece'));
        j := j - ARRAY['porteur_nom', 'porteur_adresse', 'porteur_piece'];
    END IF;
    j := j - p_table.colonnes_exclues;
    FOREACH c IN ARRAY p_table.colonnes_patient LOOP
        IF jsonb_typeof(j -> c) = 'number' THEN
            j := jsonb_set(j, ARRAY[c], to_jsonb(sync_code_patient((j ->> c)::bigint)));
        END IF;
    END LOOP;
    IF cardinality(p_table.colonnes_texte) > 0 THEN
        SELECT textes_libres INTO v_mode FROM sync_parametre WHERE id = 1;
        FOREACH c IN ARRAY p_table.colonnes_texte LOOP
            IF jsonb_typeof(j -> c) = 'string' THEN
                j := jsonb_set(j, ARRAY[c], CASE WHEN v_mode = 'retires' THEN 'null'::jsonb
                                                 ELSE to_jsonb(sync_filtrer_texte(j ->> c)) END);
            END IF;
        END LOOP;
    END IF;
    RETURN j;
END $$;

CREATE OR REPLACE FUNCTION sync_inscrire() RETURNS trigger
LANGUAGE plpgsql AS $$
DECLARE
    t       sync_table;
    p       sync_parametre;
    j       jsonb;
    v_id    text;
BEGIN
    SELECT * INTO p FROM sync_parametre WHERE id = 1;
    IF NOT FOUND OR NOT p.active THEN RETURN NULL; END IF;
    SELECT * INTO t FROM sync_table WHERE nom_table = TG_TABLE_NAME;
    IF NOT FOUND OR sync_sens_effectif(t, p) NOT IN ('montante', 'bidirectionnelle') THEN RETURN NULL; END IF;
    j := sync_minimiser(t, CASE WHEN TG_OP = 'DELETE' THEN to_jsonb(OLD) ELSE to_jsonb(NEW) END);
    SELECT string_agg(coalesce(j ->> k.col, ''), '|' ORDER BY k.n) INTO v_id
    FROM unnest(t.cle_colonnes) WITH ORDINALITY AS k(col, n);
    INSERT INTO sync_sortie (nom_table, id_local, operation, donnees, version_contrat, epoque)
    VALUES (TG_TABLE_NAME, v_id,
            CASE TG_OP WHEN 'INSERT' THEN 'creation' WHEN 'UPDATE' THEN 'modification' ELSE 'suppression' END,
            CASE WHEN TG_OP = 'DELETE' THEN NULL ELSE j END,
            p.version_contrat, p.epoque);
    RETURN NULL;
END $$;

CREATE OR REPLACE FUNCTION sync_installer_declencheurs() RETURNS integer
LANGUAGE plpgsql AS $$
DECLARE r record; n int := 0; p sync_parametre;
BEGIN
    SELECT * INTO p FROM sync_parametre WHERE id = 1;
    FOR r IN SELECT c.relname FROM pg_trigger tg
             JOIN pg_class c ON c.oid = tg.tgrelid
             JOIN pg_namespace ns ON ns.oid = c.relnamespace AND ns.nspname = 'public'
             WHERE tg.tgname = 'zz_sync_sortie' LOOP
        EXECUTE format('DROP TRIGGER zz_sync_sortie ON public.%I', r.relname);
    END LOOP;
    IF coalesce(p.active, false) THEN
        FOR r IN SELECT s.nom_table FROM sync_table s
                 JOIN pg_tables t ON t.schemaname = 'public' AND t.tablename = s.nom_table
                 WHERE sync_sens_effectif(s, p) IN ('montante', 'bidirectionnelle') ORDER BY 1 LOOP
            EXECUTE format('CREATE TRIGGER zz_sync_sortie AFTER INSERT OR UPDATE OR DELETE ON public.%I '
                           'FOR EACH ROW EXECUTE FUNCTION sync_inscrire()', r.nom_table);
            n := n + 1;
        END LOOP;
    END IF;
    RETURN n;
END $$;

-- Réglage des deux options ; renvoie le nombre de tables suivies
CREATE OR REPLACE FUNCTION sync_definir_options(p_textes_libres text, p_envoyer_medecins boolean) RETURNS integer
LANGUAGE plpgsql AS $$
BEGIN
    IF p_textes_libres NOT IN ('filtres', 'retires') THEN
        RAISE EXCEPTION 'Synchronisation : textes_libres doit valoir filtres ou retires';
    END IF;
    UPDATE sync_parametre SET textes_libres = p_textes_libres,
                              envoyer_medecins = coalesce(p_envoyer_medecins, false),
                              modifie_le = now()
    WHERE id = 1;
    RETURN sync_installer_declencheurs();
END $$;
