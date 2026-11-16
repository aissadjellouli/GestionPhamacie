-- =====================================================================
-- 124 — Profils de reprise : noms neutres des modèles vides livrés par 114
-- (2026-11-16)
--
--   * Les quatre modèles vides livrés par 114 portent désormais les noms
--     « Modèle de reprise 1 » à « Modèle de reprise 4 ».
--   * Identification sans aucun nom : les lignes créées par l'insertion de
--     114 partagent le même horodatage de création (le plus ancien de la
--     table, une seule instruction) ; elles sont numérotées par id.
--   * Seuls les profils encore intacts sont renommés : statut
--     « a_completer », version vide, aucune correspondance, aucun code de
--     classe, aucun format de date, séparateur « auto », jamais modifiés
--     (modifie_par vide, modifie_le = cree_le). Un profil modifié par
--     l'utilisateur garde son nom.
--   * Aucun renommage si le nouveau nom existe déjà (index unique
--     logiciel + version).
--   * Commentaire de la table mis à jour.
-- Aucune donnée supprimée. Idempotent.
-- À exécuter après 123_formulaire_accord_sms.sql.
-- =====================================================================

WITH modeles AS (
    SELECT p.id, row_number() OVER (ORDER BY p.id) AS rang
    FROM import.profil_reprise p
    WHERE p.cree_le = (SELECT min(cree_le) FROM import.profil_reprise)
)
UPDATE import.profil_reprise p
SET logiciel = 'Modèle de reprise ' || m.rang
FROM modeles m
WHERE p.id = m.id
  AND m.rang <= 4
  AND p.logiciel <> 'Modèle de reprise ' || m.rang
  AND p.statut = 'a_completer'
  AND p.version = ''
  AND p.correspondances = '[]'::jsonb
  AND p.codes_classe = '{}'::jsonb
  AND p.format_date IS NULL
  AND p.separateur_decimal = 'auto'
  AND p.modifie_par IS NULL
  AND p.modifie_le = p.cree_le
  AND NOT EXISTS (SELECT 1 FROM import.profil_reprise a
                  WHERE lower(a.logiciel) = lower('Modèle de reprise ' || m.rang) AND a.version = '');

COMMENT ON TABLE import.profil_reprise IS 'Profil de reprise d''un logiciel précédent (114) : correspondance de colonnes et transformations ; a_completer = modèle vide, format réel inconnu';
