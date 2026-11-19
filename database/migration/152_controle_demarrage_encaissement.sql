-- =====================================================================
-- 152 : encaissement suspendu au démarrage jusqu'au contrôle du serveur
-- central (2026-11-19, choix « A + B » du titulaire ;
-- docs/conception-synchronisation.md § 31.10).
-- Une base restaurée ne le sait pas d'elle-même : seul le serveur central
-- peut dire qu'il a reçu plus de lots qu'elle n'en a formé. Entre le
-- démarrage de l'API et ce contrôle, une vente reprendrait un n° de ticket
-- déjà reçu par le central. Donc :
--   * sync_noter_demarrage() : appelée au démarrage de l'API ; pose
--     demarrage_le (synchronisation active, adresse réglée, serveur
--     activé ; sinon rien n'est attendu) et efface le contrôle précédent.
--   * sync_noter_controle_demarrage() : le serveur central a répondu (pas
--     de retour en arrière, ou retour en arrière avec compteur avancé).
--   * encaissement_attente_secondes() : secondes avant que l'encaissement
--     soit permis (0 : permis). Attente tant que le contrôle n'est pas
--     fait, au plus controle_demarrage_delai_s après le démarrage ; même
--     plafond après une détection de retour en arrière dont l'avance des
--     n° (compteur des tickets compris) n'est pas encore faite.
--     Délai 0 : jamais d'attente. Au-delà du délai, la vente est permise
--     même sans Internet (les n° repris sont signalés par le central).
--   * prochain_numero_vente() : refuse (P0001, message clair) pendant
--     l'attente ; sinon inchangé (script 112).
-- Garde de rejeu : 112 ne recrée plus prochain_numero_vente si
-- encaissement_attente_secondes existe.
-- Idempotent. Aucune donnée existante modifiée.
-- =====================================================================

-- Fichier en UTF-8 : lu comme tel quel que soit l'encodage du poste qui lance psql.
SET client_encoding = 'UTF8';

-- ---- 1. État du contrôle de démarrage ----
ALTER TABLE sync_parametre ADD COLUMN IF NOT EXISTS demarrage_le timestamptz;
ALTER TABLE sync_parametre ADD COLUMN IF NOT EXISTS controle_demarrage_le timestamptz;
ALTER TABLE sync_parametre ADD COLUMN IF NOT EXISTS controle_demarrage_delai_s int NOT NULL DEFAULT 120;
DO $garde$ BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'sync_parametre_controle_demarrage_delai_check'
                   AND conrelid = 'sync_parametre'::regclass) THEN
        ALTER TABLE sync_parametre ADD CONSTRAINT sync_parametre_controle_demarrage_delai_check
            CHECK (controle_demarrage_delai_s BETWEEN 0 AND 600);
    END IF;
END $garde$;
COMMENT ON COLUMN sync_parametre.demarrage_le IS 'Démarrage de l''API (synchronisation active et serveur activé) : encaissement suspendu jusqu''au contrôle du serveur central, au plus controle_demarrage_delai_s (script 152).';
COMMENT ON COLUMN sync_parametre.controle_demarrage_le IS 'Contrôle de démarrage fait : le serveur central a répondu (pas de retour en arrière, ou compteur des tickets avancé) (script 152).';
COMMENT ON COLUMN sync_parametre.controle_demarrage_delai_s IS 'Attente maximale de l''encaissement au démarrage, en secondes (0 : aucune attente ; 120 par défaut, choix du titulaire le 2026-11-19) (script 152).';

-- ---- 2. Démarrage et contrôle ----
CREATE OR REPLACE FUNCTION sync_noter_demarrage() RETURNS boolean
LANGUAGE plpgsql AS $$
DECLARE v_attendu boolean;
BEGIN
    UPDATE sync_parametre
       SET demarrage_le = CASE WHEN active AND adresse_serveur IS NOT NULL AND inscrite_le IS NOT NULL THEN now() END,
           controle_demarrage_le = NULL
     WHERE id = 1
    RETURNING demarrage_le IS NOT NULL INTO v_attendu;
    RETURN coalesce(v_attendu, false);
END $$;
COMMENT ON FUNCTION sync_noter_demarrage() IS 'Démarrage de l''API : contrôle du serveur central attendu avant tout encaissement (true), ou rien à attendre (script 152).';

CREATE OR REPLACE FUNCTION sync_noter_controle_demarrage() RETURNS void
LANGUAGE sql AS $$
    UPDATE sync_parametre SET controle_demarrage_le = now()
     WHERE id = 1 AND demarrage_le IS NOT NULL AND controle_demarrage_le IS NULL;
$$;
COMMENT ON FUNCTION sync_noter_controle_demarrage() IS 'Le serveur central a répondu au contrôle de démarrage : encaissement permis (script 152).';

CREATE OR REPLACE FUNCTION sync_regler_controle_demarrage(p_delai_s int) RETURNS void
LANGUAGE plpgsql AS $$
BEGIN
    IF p_delai_s IS NULL OR p_delai_s NOT BETWEEN 0 AND 600 THEN
        RAISE EXCEPTION 'Attente au démarrage : entre 0 et 600 secondes.';
    END IF;
    UPDATE sync_parametre SET controle_demarrage_delai_s = p_delai_s WHERE id = 1;
END $$;

-- ---- 3. Attente de l'encaissement ----
CREATE OR REPLACE FUNCTION encaissement_attente_secondes() RETURNS int
LANGUAGE plpgsql STABLE AS $$
DECLARE
    p        sync_parametre;
    v_delai  interval;
    v_fin    timestamptz;
BEGIN
    SELECT * INTO p FROM sync_parametre WHERE id = 1;
    IF NOT FOUND OR NOT p.active OR p.controle_demarrage_delai_s = 0 THEN
        RETURN 0;
    END IF;
    v_delai := make_interval(secs => p.controle_demarrage_delai_s);
    IF p.demarrage_le IS NOT NULL AND p.controle_demarrage_le IS NULL THEN
        v_fin := p.demarrage_le + v_delai;
    END IF;
    -- Retour en arrière détecté, avance des n° (compteur des tickets compris, script 151) pas encore faite
    IF p.retour_arriere_le IS NOT NULL AND p.retour_arriere_sequences_le IS NULL THEN
        v_fin := greatest(v_fin, p.retour_arriere_le + v_delai);
    END IF;
    IF v_fin IS NULL OR v_fin <= now() THEN
        RETURN 0;
    END IF;
    RETURN greatest(1, ceil(extract(epoch FROM v_fin - now()))::int);
END $$;
COMMENT ON FUNCTION encaissement_attente_secondes() IS 'Secondes avant que l''encaissement soit permis (0 : permis) : contrôle de démarrage du serveur central attendu, au plus le délai réglé (script 152).';

-- ---- 4. N° du prochain ticket (remplace la version du script 112) ----
-- Inchangé (compteur verrouillé jusqu'à la fin de la transaction, une transaction annulée rend son n°), refusé pendant l'attente.
CREATE OR REPLACE FUNCTION prochain_numero_vente() RETURNS text
LANGUAGE plpgsql AS $$
DECLARE
    v_numero  bigint;
    v_prefixe text;
    v_attente int;
BEGIN
    v_attente := encaissement_attente_secondes();
    IF v_attente > 0 THEN
        RAISE EXCEPTION 'Encaissement en attente du contrôle de démarrage avec le serveur central (n° de tickets vérifiés après une restauration) : réessayez dans % s au plus.', v_attente;
    END IF;
    UPDATE compteur_ticket SET dernier_numero = dernier_numero + 1 WHERE id = 1 RETURNING dernier_numero, prefixe INTO v_numero, v_prefixe;
    IF NOT FOUND THEN
        RAISE EXCEPTION 'Compteur des tickets absent (script 112)';
    END IF;
    RETURN v_prefixe || lpad(v_numero::text, GREATEST(6, char_length(v_numero::text)), '0');
END;
$$;
