-- =====================================================================
-- Pharma Officine : RÉFÉRENTIEL DE DÉPART (mise en production)
--
-- À lancer une fois, juste après schema.sql, sur la base de la pharmacie :
--
--   psql -h <serveur> -U pharma_app -d <base> -v ON_ERROR_STOP=1 -f database/init/seed_referentiel.sql
--
-- Contenu (aucune donnée fictive, aucune donnée de patient) :
--   * identité de la pharmacie et paramètres par défaut (caisse, alertes,
--     traçabilité, inventaire, notifications, sauvegardes) ;
--   * rôles (titulaire, assistant, vendeur, stagiaire), droits et droits
--     de départ de chaque rôle ;
--   * organismes CNAS et CASNOS ; substances surveillées (arrêté du
--     11 août 2021) ; coffre de la trésorerie ;
--   * compte « titulaire » avec le mot de passe provisoire « Provisoire-2026 » :
--     changement obligatoire à la première connexion (rien d'autre n'est
--     possible avant).
-- Réexécutable : rien n'est dupliqué ni écrasé (ON CONFLICT / NOT EXISTS).
-- La nomenclature nationale s'importe ensuite dans Administration › Nomenclature
-- (fichier database/data/NOMENCLATURE NATIONALE - VERSION ….xlsx).
-- Valeurs identiques à celles posées par les scripts 001 à 035.
-- =====================================================================

BEGIN;

-- ---------------------------------------------------------------------
-- 1. Pharmacie et paramètres (une ligne chacun, valeurs par défaut du schéma)
-- ---------------------------------------------------------------------
INSERT INTO pharmacie DEFAULT VALUES ON CONFLICT (id) DO NOTHING;
INSERT INTO parametre_alerte DEFAULT VALUES ON CONFLICT (id) DO NOTHING;
INSERT INTO parametre_tracabilite DEFAULT VALUES ON CONFLICT (id) DO NOTHING;
INSERT INTO parametre_caisse DEFAULT VALUES ON CONFLICT (id) DO NOTHING;
INSERT INTO parametre_notification DEFAULT VALUES ON CONFLICT (id) DO NOTHING;
INSERT INTO parametre_sauvegarde DEFAULT VALUES ON CONFLICT (id) DO NOTHING;
INSERT INTO parametre_inventaire DEFAULT VALUES ON CONFLICT (id) DO NOTHING;

-- ---------------------------------------------------------------------
-- 2. Organismes payeurs (les autres, ex. militaires, s'ajoutent dans Administration › Pharmacie)
-- ---------------------------------------------------------------------
INSERT INTO organisme (code, nom) VALUES
    ('CNAS',   'Caisse nationale des assurances sociales des travailleurs salariés'),
    ('CASNOS', 'Caisse nationale de sécurité sociale des non-salariés')
ON CONFLICT (code) DO NOTHING;

-- ---------------------------------------------------------------------
-- 3. Substances surveillées (classe proposée de la nomenclature)
-- ---------------------------------------------------------------------
INSERT INTO substance_surveillee (motif, classe, source)
SELECT m, 'psychotrope', 'Arrêté interministériel du 11 août 2021 (JO n° 61) : psychotropes à risque avéré d''abus'
FROM unnest(ARRAY['BUPRENORPHINE', 'TRAMADOL', 'CLONAZEPAM', 'PREGABALINE', 'TRIHEXYPHENIDYLE',
                  'CLORAZEPATE', 'MIDAZOLAM', 'ZOLPIDEM']) AS m
ON CONFLICT (motif) DO NOTHING;

-- ---------------------------------------------------------------------
-- 4. Rôles, droits et droits de départ
-- ---------------------------------------------------------------------
INSERT INTO role (code, nom, systeme, remise_max_pct) VALUES
    ('titulaire',  'Pharmacien titulaire', true,  100),
    ('assistant',  'Pharmacien assistant', false, 10),
    ('vendeur',    'Vendeur',              false, 0),
    ('stagiaire',  'Stagiaire',            false, 0)
ON CONFLICT (code) DO NOTHING;

INSERT INTO permission (code, module, libelle, sensible) VALUES
    ('achats.commander', 'Achats', 'Passer une commande', false),
    ('achats.receptionner', 'Achats', 'Réceptionner une livraison', false),
    ('achats.regler', 'Achats', 'Régler les factures des grossistes', true),
    ('achats.retour_fournisseur', 'Achats', 'Faire un retour fournisseur', false),
    ('admin.parametres', 'Administration', 'Modifier les paramètres', true),
    ('admin.sauvegardes', 'Administration', 'Gérer les sauvegardes', true),
    ('admin.utilisateurs', 'Administration', 'Gérer les utilisateurs et les droits', true),
    ('caisse.annuler_vente', 'Caisse', 'Annuler une vente validée', true),
    ('caisse.ouvrir_tiroir', 'Caisse', 'Ouvrir le tiroir sans vente', true),
    ('caisse.remise', 'Caisse', 'Accorder une remise', true),
    ('caisse.retour_especes', 'Caisse', 'Rembourser un retour en espèces', true),
    ('caisse.supprimer_ligne', 'Caisse', 'Supprimer une ligne après scan', true),
    ('caisse.vendre', 'Caisse', 'Encaisser une vente', false),
    ('caisse.voir_ecart', 'Caisse', 'Voir le montant attendu et les écarts', true),
    ('chifa.bordereaux', 'Chifa', 'Gérer les bordereaux et rejets', false),
    ('chifa.importer', 'Chifa', 'Importer les factures Chifa', false),
    ('credit.accorder', 'Crédits', 'Mettre une vente en crédit', false),
    ('credit.depasser_plafond', 'Crédits', 'Dépasser le plafond de crédit d''un client', true),
    ('factures.gerer', 'Factures', 'Émettre et gérer les factures clients', true),
    ('psychotropes.delivrer', 'Psychotropes', 'Délivrer un psychotrope', true),
    ('psychotropes.registre', 'Psychotropes', 'Consulter le registre', false),
    ('rapports.marges', 'Rapports', 'Voir les prix d''achat et les marges', true),
    ('rapports.ventes', 'Rapports', 'Voir les rapports de ventes', false),
    ('catalogue.creer', 'Stock', 'Créer un produit au catalogue', true),
    ('prix.modifier', 'Stock', 'Modifier un prix de vente', true),
    ('stock.ajuster', 'Stock', 'Corriger le stock (inventaire)', true),
    ('stock.consulter', 'Stock', 'Consulter le stock', false),
    ('stock.declarer_perime', 'Stock', 'Déclarer un produit périmé ou cassé', true),
    ('tresorerie.gerer', 'Trésorerie', 'Gérer la trésorerie (coffre, banques, dépenses)', true)

ON CONFLICT (code) DO NOTHING;

-- Les droits du rôle système (titulaire) sont protégés par un déclencheur : levé le temps de l'insertion
ALTER TABLE role_permission DISABLE TRIGGER trg_proteger_role_systeme;
INSERT INTO role_permission (role_id, permission_code)
SELECT r.id, d.permission FROM (VALUES
    ('titulaire', 'achats.commander'),
    ('titulaire', 'achats.receptionner'),
    ('titulaire', 'achats.regler'),
    ('titulaire', 'achats.retour_fournisseur'),
    ('titulaire', 'admin.parametres'),
    ('titulaire', 'admin.sauvegardes'),
    ('titulaire', 'admin.utilisateurs'),
    ('titulaire', 'caisse.annuler_vente'),
    ('titulaire', 'caisse.ouvrir_tiroir'),
    ('titulaire', 'caisse.remise'),
    ('titulaire', 'caisse.retour_especes'),
    ('titulaire', 'caisse.supprimer_ligne'),
    ('titulaire', 'caisse.vendre'),
    ('titulaire', 'caisse.voir_ecart'),
    ('titulaire', 'catalogue.creer'),
    ('titulaire', 'chifa.bordereaux'),
    ('titulaire', 'chifa.importer'),
    ('titulaire', 'credit.accorder'),
    ('titulaire', 'credit.depasser_plafond'),
    ('titulaire', 'factures.gerer'),
    ('titulaire', 'prix.modifier'),
    ('titulaire', 'psychotropes.delivrer'),
    ('titulaire', 'psychotropes.registre'),
    ('titulaire', 'rapports.marges'),
    ('titulaire', 'rapports.ventes'),
    ('titulaire', 'stock.ajuster'),
    ('titulaire', 'stock.consulter'),
    ('titulaire', 'stock.declarer_perime'),
    ('titulaire', 'tresorerie.gerer'),
    ('assistant', 'achats.commander'),
    ('assistant', 'achats.receptionner'),
    ('assistant', 'achats.regler'),
    ('assistant', 'achats.retour_fournisseur'),
    ('assistant', 'caisse.annuler_vente'),
    ('assistant', 'caisse.ouvrir_tiroir'),
    ('assistant', 'caisse.remise'),
    ('assistant', 'caisse.retour_especes'),
    ('assistant', 'caisse.supprimer_ligne'),
    ('assistant', 'caisse.vendre'),
    ('assistant', 'catalogue.creer'),
    ('assistant', 'chifa.bordereaux'),
    ('assistant', 'chifa.importer'),
    ('assistant', 'credit.accorder'),
    ('assistant', 'credit.depasser_plafond'),
    ('assistant', 'psychotropes.delivrer'),
    ('assistant', 'psychotropes.registre'),
    ('assistant', 'rapports.ventes'),
    ('assistant', 'stock.ajuster'),
    ('assistant', 'stock.consulter'),
    ('assistant', 'stock.declarer_perime'),
    ('vendeur', 'achats.receptionner'),
    ('vendeur', 'caisse.vendre'),
    ('vendeur', 'chifa.importer'),
    ('vendeur', 'credit.accorder'),
    ('vendeur', 'stock.consulter'),
    ('stagiaire', 'caisse.vendre'),
    ('stagiaire', 'stock.consulter')

) AS d (role, permission)
JOIN role r ON r.code = d.role
-- Posés au premier lancement seulement (aucun compte encore créé) : une réexécution ne touche jamais
-- aux droits que le titulaire a modifiés ensuite
WHERE NOT EXISTS (SELECT 1 FROM utilisateur)
ON CONFLICT DO NOTHING;
ALTER TABLE role_permission ENABLE TRIGGER trg_proteger_role_systeme;

-- ---------------------------------------------------------------------
-- 5. Trésorerie : le coffre (les banques se déclarent dans Trésorerie)
-- ---------------------------------------------------------------------
INSERT INTO compte_tresorerie (type, nom) SELECT 'coffre', 'Coffre' WHERE NOT EXISTS (SELECT 1 FROM compte_tresorerie WHERE type = 'coffre');

-- ---------------------------------------------------------------------
-- 6. Compte titulaire initial : mot de passe provisoire, à changer à la première connexion
-- ---------------------------------------------------------------------
INSERT INTO utilisateur (nom_complet, identifiant, mot_de_passe_hash, role_id, doit_changer_mot_de_passe)
SELECT 'Pharmacien titulaire', 'titulaire', crypt('Provisoire-2026', gen_salt('bf', 10)), r.id, true
FROM role r
WHERE r.code = 'titulaire' AND NOT EXISTS (SELECT 1 FROM utilisateur WHERE identifiant = 'titulaire');

COMMIT;

\echo 'Référentiel chargé. Connexion : titulaire / Provisoire-2026 (à changer à la première connexion).'
