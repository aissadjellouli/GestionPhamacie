-- =====================================================================
-- 172 -- Offre de base et options : activation signée par l'éditeur
--   Décisions du titulaire du 2026-11-26 (docs/offre-et-options.md, « go all lot ») : les options et les limites d'une pharmacie (premier
--   cas : le nombre maximum de postes autorisés) viennent d'une activation SIGNÉE par l'éditeur, contrôlée par l'API avec la clé publique
--   intégrée à son programme. Rien de ce qui est rangé ici ne donne de droit par sa seule présence : une ligne modifiée ou ajoutée à la
--   main ne passe pas la signature et ramène à l'offre de base (2 postes).
--   * activation_offre : codes d'activation saisis (écran « Mon offre ») ou reçus du serveur central (ordre activation_offre) ;
--     la plus récente qui passe le contrôle (signature, code de l'officine, NIF, échéance + 15 jours) fait l'offre en vigueur.
--   * activation_offre_signaler_refus : note une seule fois qu'une activation rangée ne passe plus le contrôle, avec une ligne au journal.
--   * parametre_postes.maximum_postes n'est plus lu (gardé pour les versions précédentes de l'API ; commentaire mis à jour).
--   * activation_offre est classée « jamais » (jamais envoyée au serveur central ; copiée sur le 2e serveur par la réplication).
--   Données : aucune donnée existante modifiée.
--   Retour arrière : database/migration/retour/172_activation_offre_retour.sql (supprime la table : les activations saisies sont perdues,
--   elles se ressaisissent depuis les codes de l'éditeur).
-- Idempotent et rejouable sur une base plus récente.
-- A exécuter après 171_taux_tva_liste.sql.
-- =====================================================================

-- Fichier en UTF-8 : lu comme tel quel que soit l'encodage du poste qui lance psql.
SET client_encoding = 'UTF8';

CREATE TABLE IF NOT EXISTS activation_offre (
    id             bigint       GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    code           text         NOT NULL CHECK (length(code) BETWEEN 20 AND 4000),
    emise_le       timestamptz  NOT NULL,
    source         text         NOT NULL CHECK (source IN ('code', 'central')),
    saisie_le      timestamptz  NOT NULL DEFAULT now(),
    saisie_par     bigint       REFERENCES utilisateur(id),
    ordre_central  uuid,
    refusee_le     timestamptz,
    motif_refus    text         CHECK (length(motif_refus) <= 300),
    CONSTRAINT uq_activation_offre_code UNIQUE (code),
    CONSTRAINT ck_activation_offre_source CHECK ((source = 'code') = (ordre_central IS NULL))
);
COMMENT ON TABLE activation_offre IS 'Activations de l''offre signées par l''éditeur (script 172) : contrôlées par l''API (signature, officine, NIF) ; une ligne seule ne donne aucun droit';
COMMENT ON COLUMN activation_offre.emise_le IS 'Date de signature lue dans le code (pour l''ordre) ; contrôlée à chaque lecture contre le contenu signé';
COMMENT ON COLUMN parametre_postes.maximum_postes IS 'Plus lu depuis le script 172 : le maximum de postes vient de l''activation signée par l''éditeur (Administration › Mon offre)';

CREATE OR REPLACE FUNCTION activation_offre_signaler_refus(p_id bigint, p_motif text) RETURNS boolean
LANGUAGE plpgsql AS $$
DECLARE n int;
BEGIN
    UPDATE activation_offre SET refusee_le = now(), motif_refus = left(p_motif, 300) WHERE id = p_id AND refusee_le IS NULL;
    GET DIAGNOSTICS n = ROW_COUNT;
    IF n = 1 THEN
        INSERT INTO journal_audit (utilisateur_id, action, table_cible, enregistrement_id, details)
        VALUES (NULL, 'offre_activation_refusee', 'activation_offre', p_id, jsonb_build_object('motif', left(p_motif, 300)));
    END IF;
    RETURN n = 1;
END $$;
COMMENT ON FUNCTION activation_offre_signaler_refus(bigint, text) IS 'Note une seule fois qu''une activation rangée ne passe plus le contrôle de l''API, avec une ligne au journal (script 172)';

-- Même forme que le script 126 (lue par le générateur du miroir du central) : jamais envoyée au central
INSERT INTO sync_table (nom_table, sens, colonnes_exclues, colonnes_patient, porteur, motif) VALUES
    ('activation_offre', 'jamais', '{}', '{}', false, 'Activations de l''offre signées par l''éditeur, script 172')
ON CONFLICT (nom_table) DO NOTHING;
