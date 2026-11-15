-- =====================================================================
-- Coordonnées FICTIVES des données de démonstration (développement et
-- tests seulement, jamais en production) : les documents imprimés
-- (ticket, factures, bons de commande, relevés) affichent un vrai format
-- au lieu de « [téléphone] » ou de champs vides.
--   * grossistes et patients de démonstration : le texte « [téléphone] »
--     devient un numéro fictif (0550 00 01 xx pour les grossistes,
--     0660 00 02 xx pour les patients) ;
--   * pharmacie : adresse, téléphone, NIF, NIS, RC, article d'imposition
--     et n° Chifa fictifs, posés seulement s'ils sont vides (une valeur
--     saisie dans Administration n'est jamais écrasée).
-- Idempotent. A exécuter après 900_donnees_demo.sql.
-- =====================================================================

UPDATE fournisseur f
SET telephone = '0550 00 01 ' || lpad(n.rang::text, 2, '0')
FROM (SELECT id, row_number() OVER (ORDER BY id) AS rang FROM fournisseur WHERE telephone = '[téléphone]') n
WHERE f.id = n.id;

UPDATE patient p
SET telephone = '0660 00 02 ' || lpad(n.rang::text, 2, '0')
FROM (SELECT id, row_number() OVER (ORDER BY id) AS rang FROM patient WHERE telephone = '[téléphone]') n
WHERE p.id = n.id;

UPDATE pharmacie SET
    adresse            = COALESCE(NULLIF(adresse, ''), '12 rue de la Recette, 16000 Alger (adresse fictive)'),
    telephone          = COALESCE(NULLIF(telephone, ''), '023 00 00 00'),
    nif                = COALESCE(NULLIF(nif, ''), '000016000000000'),
    nis                = COALESCE(NULLIF(nis, ''), '000016000000000'),
    rc                 = COALESCE(NULLIF(rc, ''), '16/00-0000000 A 00'),
    article_imposition = COALESCE(NULLIF(article_imposition, ''), '16000000000'),
    chifa_officine     = COALESCE(NULLIF(chifa_officine, ''), '16-0000')
WHERE id = 1;
