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
--   * organismes CNAS, CASNOS et MDN ; substances surveillées (arrêté du
--     11 août 2021) ; coffre de la trésorerie ; catégories de produits
--     (médicament, dermo-cosmétique, hygiène…) ; motifs et catégories réglables
--     (tiroir, mise en attente, charges, sorties de stock, retours ; 094 ;
--     vente Chifa sans n° de facture ; 097) ;
--   * compte « titulaire » avec le mot de passe provisoire « Provisoire-2026 » :
--     changement obligatoire à la première connexion (rien d'autre n'est
--     possible avant).
-- Réexécutable : rien n'est dupliqué ni écrasé (ON CONFLICT / NOT EXISTS).
-- La nomenclature nationale s'importe ensuite dans Administration › Nomenclature
-- (fichier database/data/NOMENCLATURE NATIONALE - VERSION ….xlsx).
-- Valeurs identiques à celles posées par les scripts 001 à 099 (095, 098 et 099 n'ajoutent aucune donnée de référence ; 096 : motifs du tiroir avec effet
-- et détail, « Dépôt ou retrait d'espèces » désactivé ; 097 : motifs de la liste
-- « volet_absent », vente Chifa sans n° de facture ; vérifié : permissions, rôles,
-- droits de départ, substances, organismes, coffre, catégories de produits, motifs réglables). Règle : tout script numéroté qui ajoute
-- une donnée de référence la reporte ici dans la même modification (docs/base-de-donnees.md).
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
INSERT INTO parametre_surveillance DEFAULT VALUES ON CONFLICT (id) DO NOTHING;
INSERT INTO parametre_rapport DEFAULT VALUES ON CONFLICT (id) DO NOTHING;

-- ---------------------------------------------------------------------
-- 2. Organismes payeurs CNAS, CASNOS et MDN (les autres s'ajoutent dans Administration › Pharmacie)
-- ---------------------------------------------------------------------
INSERT INTO organisme (code, nom) VALUES
    ('CNAS',   'Caisse nationale des assurances sociales des travailleurs salariés'),
    ('CASNOS', 'Caisse nationale de sécurité sociale des non-salariés')
ON CONFLICT (code) DO NOTHING;
-- MDN (militaires, script 090) : ajouté seulement s'il n'existe pas déjà (quelle que soit la casse du code)
INSERT INTO organisme (code, nom)
SELECT 'MDN', 'Ministère de la Défense nationale (militaires)'
WHERE NOT EXISTS (SELECT 1 FROM organisme WHERE upper(code) = 'MDN');

-- Catégories de produits (045) : « médicament » ou autre produit, pour les totaux de la caisse et des rapports
INSERT INTO categorie_produit (code, libelle, medicament, ordre) VALUES
    ('medicament',  'Médicament', true, 10),
    ('dermo_cosmetique', 'Dermo-cosmétique', false, 20),
    ('hygiene', 'Hygiène', false, 30),
    ('bebe_maman', 'Bébé et maman', false, 40),
    ('complement_alimentaire', 'Compléments alimentaires et diététique', false, 50),
    ('phyto_aroma', 'Phytothérapie et aromathérapie', false, 60),
    ('materiel_medical', 'Matériel médical et orthopédie', false, 70),
    ('paramedical', 'Paramédical (pansements, accessoires de soin)', false, 80)
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
    ('achats.valider_ecart', 'Achats', 'Valider un écart entre le comptage et la facture d''une livraison', true),
    ('admin.parametres', 'Administration', 'Modifier les paramètres', true),
    ('admin.sauvegardes', 'Administration', 'Gérer les sauvegardes', true),
    ('admin.utilisateurs', 'Administration', 'Gérer les utilisateurs et les droits', true),
    ('reprise.doublons', 'Administration', 'Valider les doublons d''import', true),
    ('caisse.annuler_vente', 'Caisse', 'Annuler une vente validée', true),
    ('caisse.cloturer', 'Caisse', 'Faire la relève et clôturer la caisse', true),
    ('caisse.ouvrir_tiroir', 'Caisse', 'Ouvrir le tiroir sans vente', true),
    ('caisse.remise', 'Caisse', 'Accorder une remise', true),
    ('caisse.remises_auto', 'Caisse', 'Gérer les remises automatiques', true),
    ('caisse.retour_especes', 'Caisse', 'Rembourser un retour en espèces', true),
    ('caisse.supprimer_ligne', 'Caisse', 'Supprimer une ligne après scan', true),
    ('caisse.vendre', 'Caisse', 'Encaisser une vente', false),
    ('caisse.vendre_sans_ordonnance', 'Caisse', 'Vendre sans ordonnance un médicament de liste I ou II', true),
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
    ('produits.bloquer', 'Stock', 'Bloquer un produit à l''achat ou à la vente', true),
    ('stock.ajuster', 'Stock', 'Corriger le stock (inventaire)', true),
    ('stock.consulter', 'Stock', 'Consulter le stock', false),
    ('stock.declarer_perime', 'Stock', 'Déclarer un produit périmé ou cassé', true),
    ('stock.donner', 'Stock', 'Donner un échantillon ou une gratuité (don à un nécessiteux)', true),
    ('tresorerie.gerer', 'Trésorerie', 'Gérer la trésorerie (coffre, banques, dépenses)', true)

ON CONFLICT (code) DO NOTHING;

-- Droits réglés sur chaque employé par le titulaire (071) : don, remise maximale, remises automatiques
UPDATE permission SET par_personne = true
WHERE code IN ('caisse.remise', 'stock.donner', 'caisse.remises_auto') AND NOT par_personne;

-- Les droits du rôle système (titulaire) sont protégés par un déclencheur : levé le temps de l'insertion
ALTER TABLE role_permission DISABLE TRIGGER trg_proteger_role_systeme;
INSERT INTO role_permission (role_id, permission_code)
SELECT r.id, d.permission FROM (VALUES
    ('titulaire', 'achats.commander'),
    ('titulaire', 'achats.receptionner'),
    ('titulaire', 'achats.regler'),
    ('titulaire', 'achats.retour_fournisseur'),
    ('titulaire', 'achats.valider_ecart'),
    ('titulaire', 'admin.parametres'),
    ('titulaire', 'admin.sauvegardes'),
    ('titulaire', 'admin.utilisateurs'),
    ('titulaire', 'reprise.doublons'),
    ('titulaire', 'caisse.annuler_vente'),
    ('titulaire', 'caisse.cloturer'),
    ('titulaire', 'caisse.ouvrir_tiroir'),
    ('titulaire', 'caisse.remise'),
    ('titulaire', 'caisse.remises_auto'),
    ('titulaire', 'caisse.retour_especes'),
    ('titulaire', 'caisse.supprimer_ligne'),
    ('titulaire', 'caisse.vendre'),
    ('titulaire', 'caisse.vendre_sans_ordonnance'),
    ('titulaire', 'caisse.voir_ecart'),
    ('titulaire', 'catalogue.creer'),
    ('titulaire', 'produits.bloquer'),
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
    ('titulaire', 'stock.donner'),
    ('titulaire', 'tresorerie.gerer'),
    ('assistant', 'achats.commander'),
    ('assistant', 'achats.receptionner'),
    ('assistant', 'achats.regler'),
    ('assistant', 'achats.retour_fournisseur'),
    ('assistant', 'achats.valider_ecart'),
    ('assistant', 'caisse.annuler_vente'),
    ('assistant', 'caisse.cloturer'),
    ('assistant', 'reprise.doublons'),
    ('assistant', 'caisse.ouvrir_tiroir'),
    ('assistant', 'caisse.retour_especes'),
    ('assistant', 'caisse.supprimer_ligne'),
    ('assistant', 'caisse.vendre'),
    ('assistant', 'caisse.vendre_sans_ordonnance'),
    ('assistant', 'catalogue.creer'),
    ('assistant', 'produits.bloquer'),
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
INSERT INTO utilisateur (nom_complet, identifiant, mot_de_passe_hash, role_id, doit_changer_mot_de_passe, peut_donner, remise_max_pct, gere_remises_auto)
SELECT 'Pharmacien titulaire', 'titulaire', crypt('Provisoire-2026', gen_salt('bf', 10)), r.id, true, true, 100, true
FROM role r
WHERE r.code = 'titulaire' AND NOT EXISTS (SELECT 1 FROM utilisateur WHERE identifiant = 'titulaire');

-- ---------------------------------------------------------------------
-- 7. Motifs et catégories réglables par le titulaire (094) : valeurs de départ, insérées si absentes
-- ---------------------------------------------------------------------
-- Tiroir (094, 096) : effet sur l'argent de la caisse et détail exigé ; « Dépôt ou retrait d'espèces » désactivé par 096
INSERT INTO motif_reglable (liste, code, libelle, systeme, ordre, effet, detail, actif) VALUES
    ('tiroir', 'monnaie',              'Faire de la monnaie',                       false, 10,  'aucun',  'aucun',       true),
    ('tiroir', 'erreur_rendu',         'Erreur de rendu de monnaie',                false, 20,  'aucun',  'aucun',       true),
    ('tiroir', 'depot_especes',        'Dépôt ou retrait d''espèces',               false, 30,  'aucun',  'aucun',       false),
    ('tiroir', 'verification',         'Vérification du contenu',                   false, 40,  'aucun',  'aucun',       true),
    ('tiroir', 'remise_fond',          'Remise d''un fonds de caisse',              false, 50,  'entree', 'fond',        true),
    ('tiroir', 'retrait_coffre',       'Retrait vers le coffre',                    false, 60,  'sortie', 'coffre',      true),
    ('tiroir', 'paiement_fournisseur', 'Paiement d''un livreur ou d''un fournisseur', false, 70, 'sortie', 'fournisseur', true),
    ('tiroir', 'petite_depense',       'Petite dépense',                            false, 80,  'sortie', 'depense',     true),
    ('tiroir', 'remboursement_client', 'Remboursement d''un client',                false, 90,  'sortie', 'patient',     true),
    ('tiroir', 'recuperation_objet',   'Récupération d''un objet',                  false, 100, 'aucun',  'aucun',       true),
    ('tiroir', 'autre',                'Autre (préciser)',                          true,  999, 'aucun',  'aucun',       true)
ON CONFLICT (liste, code) DO NOTHING;

INSERT INTO motif_reglable (liste, code, libelle, systeme, ordre) VALUES
    ('attente', 'produit_manquant',  'Produit manquant à commander',          false, 10),
    ('attente', 'patient_revient',   'Patient revient payer',                 false, 20),
    ('attente', 'attente_ordonnance','Attente de l''ordonnance',              false, 30),
    ('attente', 'autre',             'Autre (préciser)',                      true,  999),
    ('charge', 'loyer',              'Loyer et charges',                      false, 10),
    ('charge', 'energie',            'Électricité, eau, gaz',                 false, 20),
    ('charge', 'salaires',           'Salaires et cotisations',               false, 30),
    ('charge', 'impots',             'Impôts et taxes',                       false, 40),
    ('charge', 'fournitures',        'Fournitures et consommables',           false, 50),
    ('charge', 'entretien',          'Entretien et réparations',              false, 60),
    ('charge', 'charges_financieres','Charges financières (intérêts d’emprunt)', true, 70),
    ('charge', 'autre',              'Autre dépense',                         true,  999),
    ('sortie_stock', 'perime',       'Périmé',                                true,  10),
    ('sortie_stock', 'casse',        'Cassé ou abîmé',                        true,  20),
    ('sortie_stock', 'echantillon',  'Échantillon, gratuité ou don',          true,  30),
    ('retour_fournisseur', 'peremption_proche', 'Péremption proche',          false, 10),
    ('retour_fournisseur', 'perime',            'Périmé',                     true,  20),
    ('retour_fournisseur', 'defaut',            'Défaut',                     false, 30),
    ('retour_fournisseur', 'erreur_livraison',  'Erreur de livraison',        false, 40),
    ('retour_fournisseur', 'rappel_lot',        'Rappel de lot',              true,  50)
ON CONFLICT (liste, code) DO NOTHING;

-- Vente « Facturée par Chifa » encaissée sans n° de facture Chifa : motif obligatoire (097)
INSERT INTO motif_reglable (liste, code, libelle, systeme, ordre) VALUES
    ('volet_absent', 'carte_absente',      'Pas de carte Chifa présentée',  false, 10),
    ('volet_absent', 'chifa_indisponible', 'CHIFA-Officine indisponible',   false, 20),
    ('volet_absent', 'volet_plus_tard',    'Volet à éditer plus tard',      false, 30),
    ('volet_absent', 'autre',              'Autre (préciser)',              true,  999)
ON CONFLICT (liste, code) DO NOTHING;

COMMIT;

\echo 'Référentiel chargé. Connexion : titulaire / Provisoire-2026 (à changer à la première connexion).'
