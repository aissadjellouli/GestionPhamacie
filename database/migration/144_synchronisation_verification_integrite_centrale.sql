-- =====================================================================
-- 144 : synchronisation, vérification d'intégrité au serveur central
-- (lot S4, option A, 2026-11-17 ; docs/conception-synchronisation.md § 22).
--   * compteur_ticket passe en MONTANTE (aucune donnée nominative) : le
--     serveur central contrôle la continuité des tickets « T- » comme
--     journal_vente_continuite() le fait ici (S4-D1).
--   * verification_integrite : colonnes dernier_rang et dernier_hash (fin
--     de chaîne constatée par la vérification de l'officine), remplies par
--     l'application ; elles partent avec la ligne (table montante) : le
--     serveur central compare cette fin de chaîne à ce qu'il a reçu
--     (vérification croisée : réécriture après envoi, lignes perdues).
--   * sync_minimiser : les textes libres des journaux chaînés
--     (chaine_integrite) sont TOUJOURS filtrés, jamais vidés, même avec
--     l'option « textes libres retirés » (décision du titulaire du
--     2026-11-17) : un texte vide au central est vraiment vide, ce qui
--     garde le recalcul des empreintes possible ; un texte filtré porte
--     un marqueur ([TÉL], [NOM], code P-…) et la ligne est comptée
--     « non vérifiable » au central, sans fausse alerte.
-- Idempotent. Aucune donnée existante modifiée.
-- =====================================================================

-- Fichier en UTF-8 : lu comme tel quel que soit l'encodage du poste qui lance psql (sous Windows, une sortie
-- redirigée fait lire le fichier en WIN1252 et abîme les accents des textes et des motifs).
SET client_encoding = 'UTF8';

-- ---- 1. compteur_ticket : montante ----
INSERT INTO sync_table (nom_table, sens, colonnes_exclues, colonnes_patient, porteur, motif) VALUES
    ('compteur_ticket', 'montante', '{}', '{}', false, 'Compteur des tickets : continuité vérifiée au serveur central (lot S4, script 144)')
ON CONFLICT (nom_table) DO UPDATE SET sens = EXCLUDED.sens, motif = EXCLUDED.motif
WHERE sync_table.sens IS DISTINCT FROM EXCLUDED.sens OR sync_table.motif IS DISTINCT FROM EXCLUDED.motif;

-- Code officine sur compteur_ticket (comme le script 128 pour les tables envoyées) : ADD COLUMN ... DEFAULT <constante>
-- remplit la ligne existante sans UPDATE ni déclencheur (la garde « +1 seulement » n'est pas sollicitée), puis le
-- défaut devient officine_courante()
DO $officine$
DECLARE v_code uuid := officine_courante();
BEGIN
    IF NOT EXISTS (SELECT 1 FROM information_schema.columns c
                   WHERE c.table_schema = 'public' AND c.table_name = 'compteur_ticket' AND c.column_name = 'officine_id') THEN
        EXECUTE format('ALTER TABLE public.compteur_ticket ADD COLUMN officine_id uuid NOT NULL DEFAULT %L::uuid', v_code);
    END IF;
    ALTER TABLE public.compteur_ticket ALTER COLUMN officine_id SET DEFAULT officine_courante();
END $officine$;

-- ---- 2. verification_integrite : fin de chaîne constatée ----
ALTER TABLE verification_integrite ADD COLUMN IF NOT EXISTS dernier_rang bigint;
ALTER TABLE verification_integrite ADD COLUMN IF NOT EXISTS dernier_hash bytea;
DO $garde$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'ck_verification_integrite_dernier_rang') THEN
        ALTER TABLE verification_integrite ADD CONSTRAINT ck_verification_integrite_dernier_rang
            CHECK (dernier_rang IS NULL OR dernier_rang >= 0);
    END IF;
END $garde$;
COMMENT ON COLUMN verification_integrite.dernier_rang IS 'Dernier rang de la chaîne constaté par la vérification (NULL : chaîne rompue ou contrôle sans chaîne) ; envoyé au serveur central (script 144).';
COMMENT ON COLUMN verification_integrite.dernier_hash IS 'Empreinte de la ligne au dernier rang constaté ; le serveur central la compare à la ligne reçue (script 144).';

-- ---- 2 bis. Alerte au titulaire : dernière anomalie du serveur central déjà signalée (une alerte par nouvelle anomalie) ----
-- Le résumé de la vérification arrive par la voie descendante : reglage_central, clé « integrite_centrale » (format integrite-v1).
ALTER TABLE sync_parametre ADD COLUMN IF NOT EXISTS integrite_anomalie_signalee bigint NOT NULL DEFAULT 0;
COMMENT ON COLUMN sync_parametre.integrite_anomalie_signalee IS 'N° de la dernière anomalie d''intégrité du serveur central signalée au titulaire (script 144).';
CREATE OR REPLACE FUNCTION sync_noter_integrite_signalee(p_id bigint) RETURNS void
LANGUAGE sql AS $$
    UPDATE sync_parametre SET integrite_anomalie_signalee = greatest(integrite_anomalie_signalee, coalesce(p_id, 0)) WHERE id = 1
$$;

-- ---- 3. Textes libres des journaux chaînés : toujours filtrés, jamais vidés (remplace la version du script 127) ----
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
        -- Journal chaîné : jamais vidé (l'empreinte reste recalculable au serveur central), toujours filtré
        IF EXISTS (SELECT 1 FROM chaine_integrite ci WHERE ci.journal = p_table.nom_table) THEN
            v_mode := 'filtres';
        END IF;
        FOREACH c IN ARRAY p_table.colonnes_texte LOOP
            IF jsonb_typeof(j -> c) = 'string' THEN
                j := jsonb_set(j, ARRAY[c], CASE WHEN v_mode = 'retires' THEN 'null'::jsonb
                                                 ELSE to_jsonb(sync_filtrer_texte(j ->> c)) END);
            END IF;
        END LOOP;
    END IF;
    RETURN j;
END $$;

-- ---- 4. Déclencheurs de la file : posés sur compteur_ticket si la synchronisation est déjà active ----
DO $declencheurs$
BEGIN
    IF EXISTS (SELECT 1 FROM sync_parametre WHERE id = 1 AND active) THEN
        PERFORM sync_installer_declencheurs();
    END IF;
END $declencheurs$;
