-- =====================================================================
-- 138 — Synchronisation : noms du personnel dans les textes libres
-- (2026-11-16, constat de l'essai de bout en bout, docs/conception-synchronisation.md § 14 et § 19).
--   Constat : les messages d'alerte (alerte_surveillance.message, alerte.message)
--   citent le nom complet d'un employé (« accordées par <nom> »). Le filtre des
--   textes libres (sync_filtrer_texte, script 127) ne masquait que les patients et
--   les porteurs : ce nom partait au serveur central même quand la famille
--   « employes » n'est pas autorisée.
--   Correction : tant que la famille « employes » n'est pas envoyée
--   (sync_familles_effectives), tout nom complet d'utilisateur présent dans un
--   texte libre est remplacé par [PERSONNEL] avant l'inscription dans la file.
--   Famille autorisée : texte inchangé. Le masquage a lieu avant celui des
--   patients (un prénom commun à un patient et à un employé ne casse pas le nom).
-- Idempotent (CREATE OR REPLACE) ; aucune donnée existante modifiée : seules les
-- nouvelles lignes de file sont concernées (la file est en ajout seul).
-- =====================================================================
CREATE OR REPLACE FUNCTION sync_filtrer_texte(p_texte text) RETURNS text
LANGUAGE plpgsql AS $$
DECLARE
    t       text := p_texte;
    m       text;
    v_ids   bigint[];
    v_rempl text;
    v_sortie text;
    v_reste text;
    v_pos   int;
BEGIN
    IF t IS NULL OR btrim(t) = '' THEN RETURN t; END IF;
    t := regexp_replace(t, '[[:alnum:]._%+-]+@[[:alnum:].-]+\.[[:alpha:]]{2,}', '[COURRIEL]', 'g');
    t := regexp_replace(t, '(\+|00)?213[ .-]?[0-9]([ .-]?[0-9]){7,8}', '[TÉL]', 'g');
    t := regexp_replace(t, '\m0[2-7]([ .-]?[0-9]){8}\M', '[TÉL]', 'g');
    t := regexp_replace(t, '[0-9]([ .-]?[0-9]){8,}', '[N°]', 'g');
    -- Noms du personnel : masqués tant que la famille « employes » n'est pas envoyée (les plus longs d'abord, sans tenir compte de la casse)
    IF NOT ('employes' = ANY (sync_familles_effectives())) THEN
        FOR m IN SELECT btrim(u.nom_complet) FROM utilisateur u
                 WHERE length(btrim(u.nom_complet)) >= 3
                 ORDER BY length(btrim(u.nom_complet)) DESC, 1 LOOP
            v_sortie := '';
            v_reste := t;
            LOOP
                v_pos := strpos(lower(v_reste), lower(m));
                EXIT WHEN v_pos = 0;
                v_sortie := v_sortie || left(v_reste, v_pos - 1) || '[PERSONNEL]';
                v_reste := substr(v_reste, v_pos + length(m));
            END LOOP;
            t := v_sortie || v_reste;
        END LOOP;
    END IF;
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
