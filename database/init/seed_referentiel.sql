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
--   * synchronisation (126 à 131, 141) : réglage par défaut (désactivée), classement
--     sync_table, familles nominatives (sync_famille_colonne) ;
--   * données techniques des scripts 111, 112, 116, 117, 120, 122, 123 et 143 (section 8) : paramètres de l'intégrité, inscription des
--     trois journaux chaînés dans chaine_integrite (integrite_activer), lignes d'ouverture du journal des ventes et du registre des
--     psychotropes, compteur des tickets, paramètres fiscaux, de substitution, de sérialisation, du formulaire SMS, de la réplication,
--     identifiants GS1, classe « antibiotiques » ;
--   * compte « titulaire » avec le mot de passe provisoire « Provisoire-2026 » :
--     changement obligatoire à la première connexion (rien d'autre n'est
--     possible avant).
-- Réexécutable : rien n'est dupliqué ni écrasé (ON CONFLICT / NOT EXISTS).
-- La nomenclature nationale s'importe ensuite dans Administration › Nomenclature
-- (fichier database/data/NOMENCLATURE NATIONALE - VERSION ….xlsx).
-- Valeurs identiques à celles posées par les scripts 001 à 165 (165 : classement sync_table de presence_poste (jamais) ; 164 : classement sync_table de bascule_promotion et bascule_saut (jamais) ; 163 : classement sync_table de serveur_ecarte (jamais) ; 162 : déconnexion après inactivité : défaut de colonne 15 minutes, posé par INSERT … DEFAULT VALUES ; 161 : aucune valeur de référentiel, index des clés étrangères ; 160 : aucune valeur de référentiel, message du titulaire à distance affiché sur les postes ; 159 : aucune valeur de référentiel, retour d'une vente régularisée « rendu au patient » ; 158 : aucune valeur de référentiel, retour d'une vente régularisée à la charge du patient et dettes du patient ; 157 : aucune valeur de référentiel, rapprochement d'un volet après un retour ; 156 : aucune valeur de référentiel, réglage des tâches laissées au poste : défaut de colonne « chiffre », posé par INSERT … DEFAULT VALUES ; 155 : aucune valeur de référentiel, retour partiel en tiers payant ; 154 : catégorie « parapharmacie » « Parapharmacie (à préciser) » ; 153 : règle d'alerte « traitement_en_cours » ; valeurs de départ
-- des autres règles changées seulement sur une base où elles existent et n'ont jamais été modifiées, l'init n'en sème pas ;
-- 152 n'ajoute aucune donnée de référence ; 151 : classement sync_table de integrite_prise_acte (montante), compteur_ticket_saut et sync_declaration_epoque (jamais) ; 095, 098, 099, 132, 134, 137, 138, 139, 142, 143, 148, 149 et 150 n'ajoutent aucune donnée de référence ; 141 : les 11 lignes de reglage_mutualise (mode local, aucun modèle) et le classement sync_table de reglage_mutualise ; 144 : classement sync_table de compteur_ticket (montante) ; 145 : classement de sync_ordre_recu (jamais) et sync_ordre_resultat (montante) ; 146 : classement de commande_ordre_central (jamais) ; 147 : chifa_code_produit et produit_indication passées montantes ; 140 : droit « sync.suivre » (titulaire) ; 096 : motifs du tiroir avec effet
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
    ('paramedical', 'Paramédical (pansements, accessoires de soin)', false, 80),
    ('parapharmacie', 'Parapharmacie (à préciser)', false, 90)
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
    ('sync.suivre', 'Administration', 'Voir le bandeau de synchronisation', false),
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
    ('chifa.exclure', 'Chifa', 'Exclure une vente Chifa', true),
    ('credit.accorder', 'Crédits', 'Mettre une vente en crédit', false),
    ('credit.depasser_plafond', 'Crédits', 'Dépasser le plafond de crédit d''un client', true),
    ('factures.gerer', 'Factures', 'Émettre et gérer les factures clients', true),
    ('psychotropes.delivrer', 'Psychotropes', 'Délivrer un psychotrope', true),
    ('psychotropes.registre', 'Psychotropes', 'Consulter le registre', false),
    ('psychotropes.forcer_alerte', 'Psychotropes', 'Forcer une alerte du registre (valideur)', true),
    ('rapports.marges', 'Rapports', 'Voir les prix d''achat et les marges', true),
    ('rapports.ventes', 'Rapports', 'Voir les rapports de ventes', false),
    ('catalogue.creer', 'Stock', 'Créer un produit au catalogue', true),
    ('prix.modifier', 'Stock', 'Modifier un prix de vente', true),
    ('produits.bloquer', 'Stock', 'Bloquer un produit à l''achat ou à la vente', true),
    ('stock.ajuster', 'Stock', 'Corriger le stock (inventaire)', true),
    ('stock.consulter', 'Stock', 'Consulter le stock', false),
    ('stock.declarer_perime', 'Stock', 'Déclarer un produit périmé ou cassé', true),
    ('stock.donner', 'Stock', 'Donner un échantillon ou une gratuité (don à un nécessiteux)', true),
    ('stock.corriger_entree', 'Stock', 'Corriger une entrée de stock', true),
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
    ('titulaire', 'sync.suivre'),
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
    ('titulaire', 'chifa.exclure'),
    ('titulaire', 'credit.accorder'),
    ('titulaire', 'credit.depasser_plafond'),
    ('titulaire', 'factures.gerer'),
    ('titulaire', 'prix.modifier'),
    ('titulaire', 'psychotropes.delivrer'),
    ('titulaire', 'psychotropes.registre'),
    ('titulaire', 'psychotropes.forcer_alerte'),
    ('titulaire', 'rapports.marges'),
    ('titulaire', 'rapports.ventes'),
    ('titulaire', 'stock.ajuster'),
    ('titulaire', 'stock.consulter'),
    ('titulaire', 'stock.declarer_perime'),
    ('titulaire', 'stock.donner'),
    ('titulaire', 'stock.corriger_entree'),
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
    ('assistant', 'chifa.exclure'),
    ('assistant', 'credit.accorder'),
    ('assistant', 'credit.depasser_plafond'),
    ('assistant', 'psychotropes.delivrer'),
    ('assistant', 'psychotropes.registre'),
    ('assistant', 'psychotropes.forcer_alerte'),
    ('assistant', 'rapports.ventes'),
    ('assistant', 'stock.ajuster'),
    ('assistant', 'stock.consulter'),
    ('assistant', 'stock.declarer_perime'),
    ('assistant', 'stock.corriger_entree'),
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

-- Exclusion d'une vente Chifa du traitement (motifs de départ, D-7) et types de majoration Chifa (100)
INSERT INTO motif_reglable (liste, code, libelle, systeme, ordre) VALUES
    ('chifa_exclusion', 'deja_traitee',       'Déjà traitée autrement', false, 10),
    ('chifa_exclusion', 'ordonnance_annulee', 'Ordonnance annulée',     false, 20),
    ('chifa_exclusion', 'erreur_saisie',      'Erreur de saisie',       false, 30),
    ('chifa_exclusion', 'autre',              'Autre (préciser)',       true,  999),
    ('majoration_chifa', 'garde',             'Garde',                  false, 10)
ON CONFLICT (liste, code) DO NOTHING;

-- Motifs du rejet d'une facture Chifa par l'organisme (110) ; « Autre » demande un texte et ne se désactive pas
INSERT INTO motif_reglable (liste, code, libelle, systeme, ordre) VALUES
    ('rejet_chifa', 'droits_expires',           'Droits expirés',           false, 10),
    ('rejet_chifa', 'ordonnance_non_conforme',  'Ordonnance non conforme',  false, 20),
    ('rejet_chifa', 'produit_non_remboursable', 'Produit non remboursable', false, 30),
    ('rejet_chifa', 'erreur_saisie',            'Erreur de saisie',         false, 40),
    ('rejet_chifa', 'autre',                    'Autre (préciser)',         true,  999)
ON CONFLICT (liste, code) DO NOTHING;

-- Motifs du retour client (104) ; « Autre » demande un texte et ne se désactive pas
INSERT INTO motif_reglable (liste, code, libelle, systeme, ordre) VALUES
    ('retour_client', 'erreur_delivrance', 'Erreur de délivrance', false, 10),
    ('retour_client', 'non_utilise',       'Produit non utilisé',  false, 20),
    ('retour_client', 'effet_indesirable', 'Effet indésirable',    false, 30),
    ('retour_client', 'autre',             'Autre (préciser)',     true,  999)
ON CONFLICT (liste, code) DO NOTHING;

-- Indications thérapeutiques de départ (104), à valider par le titulaire (Administration › Indications thérapeutiques) ;
-- ajoutées seulement si le libellé est absent
INSERT INTO indication (libelle)
SELECT x.libelle
FROM (VALUES ('Toux'), ('Fièvre'), ('Douleur'), ('Rhume'), ('Mal de gorge'), ('Allergie'),
             ('Diarrhée'), ('Constipation'), ('Brûlures d''estomac'), ('Nausées')) AS x(libelle)
WHERE NOT EXISTS (SELECT 1 FROM indication i WHERE lower(btrim(i.libelle)) = lower(btrim(x.libelle)));

-- Prestations de départ (105) : prix et taux de TVA à fixer par le titulaire (Administration › Prestations) ;
-- à 0 DA, une prestation ne peut pas être vendue
INSERT INTO type_prestation (code, libelle, prix, taux_tva)
SELECT x.code, x.libelle, 0, 0
FROM (VALUES ('TENSION', 'Prise de tension'), ('GLYCEMIE', 'Mesure de la glycémie'), ('INJECTION', 'Injection')) AS x(code, libelle)
WHERE NOT EXISTS (SELECT 1 FROM type_prestation t WHERE upper(btrim(t.code)) = x.code);

-- Wilayas d'Algérie (118) : 48 historiques + 10 de la loi 19-12 (49 à 58)
INSERT INTO wilaya (code, nom) VALUES
    ('01', 'Adrar'), ('02', 'Chlef'), ('03', 'Laghouat'), ('04', 'Oum El Bouaghi'), ('05', 'Batna'),
    ('06', 'Béjaïa'), ('07', 'Biskra'), ('08', 'Béchar'), ('09', 'Blida'), ('10', 'Bouira'),
    ('11', 'Tamanrasset'), ('12', 'Tébessa'), ('13', 'Tlemcen'), ('14', 'Tiaret'), ('15', 'Tizi Ouzou'),
    ('16', 'Alger'), ('17', 'Djelfa'), ('18', 'Jijel'), ('19', 'Sétif'), ('20', 'Saïda'),
    ('21', 'Skikda'), ('22', 'Sidi Bel Abbès'), ('23', 'Annaba'), ('24', 'Guelma'), ('25', 'Constantine'),
    ('26', 'Médéa'), ('27', 'Mostaganem'), ('28', 'M''Sila'), ('29', 'Mascara'), ('30', 'Ouargla'),
    ('31', 'Oran'), ('32', 'El Bayadh'), ('33', 'Illizi'), ('34', 'Bordj Bou Arréridj'), ('35', 'Boumerdès'),
    ('36', 'El Tarf'), ('37', 'Tindouf'), ('38', 'Tissemsilt'), ('39', 'El Oued'), ('40', 'Khenchela'),
    ('41', 'Souk Ahras'), ('42', 'Tipaza'), ('43', 'Mila'), ('44', 'Aïn Defla'), ('45', 'Naâma'),
    ('46', 'Aïn Témouchent'), ('47', 'Ghardaïa'), ('48', 'Relizane'),
    ('49', 'Timimoun'), ('50', 'Bordj Badji Mokhtar'), ('51', 'Ouled Djellal'), ('52', 'Béni Abbès'), ('53', 'In Salah'),
    ('54', 'In Guezzam'), ('55', 'Touggourt'), ('56', 'Djanet'), ('57', 'El M''Ghair'), ('58', 'El Meniaa')
ON CONFLICT (code) DO NOTHING;

-- Wilayas 59 à 69 (125) : loi 26-06 et décret présidentiel 26-206 [À VÉRIFIER au Journal officiel] ; wilaya d'origine renseignée
INSERT INTO wilaya (code, nom, wilaya_origine) VALUES
    ('59', 'Aflou', '03'), ('60', 'Barika', '05'), ('61', 'El Kantara', '07'), ('62', 'Bir El Ater', '12'),
    ('63', 'El Aricha', '13'), ('64', 'Ksar Chellala', '14'), ('65', 'Aïn Oussera', '17'), ('66', 'Messaad', '17'),
    ('67', 'Ksar El Boukhari', '26'), ('68', 'Bou Saâda', '28'), ('69', 'El Abiodh Sidi Cheikh', '32')
ON CONFLICT (code) DO NOTHING;

-- Alertes du registre des psychotropes (117), avec les valeurs de départ du script 153 (décision du titulaire du 2026-11-19) :
-- même patient désactivée, ordonnance réutilisée sans limite, plus de 2 boîtes ou 28 jours, prescripteur inhabituel désactivée.
-- Avant le 2026-11-20, l'init ne les semait pas (base neuve sans ces quatre règles tant qu'aucun modèle central n'était appliqué).
INSERT INTO regle_alerte_psychotrope (code, libelle, active, seuil, periode_jours, ordre) VALUES
    ('patient_repete', 'Même patient servi plusieurs fois', false, 2, 30, 1),
    ('ordonnance_reutilisee', 'Même n° d''ordonnance et même prescripteur déjà utilisés', true, 1, NULL, 2),
    ('quantite_anormale', 'Quantité anormale (boîtes par délivrance, durée de traitement)', true, 2, 28, 3),
    ('prescripteur_inhabituel', 'Prescripteur inhabituel', false, 1, 365, 4)
ON CONFLICT (code) DO NOTHING;

-- Alerte « prescripteur hors zone » du registre des psychotropes (118) : sans effet tant que la zone n'est pas réglée
INSERT INTO regle_alerte_psychotrope (code, libelle, seuil, periode_jours, ordre) VALUES
    ('prescripteur_hors_zone', 'Prescripteur hors zone (wilaya du médecin hors de la zone de l''officine)', 1, 1, 5)
ON CONFLICT (code) DO NOTHING;
-- Alerte « patient encore sous traitement » (153) : active, valideur exigé, période sans objet
INSERT INTO regle_alerte_psychotrope (code, libelle, active, seuil, periode_jours, exiger_valideur, ordre) VALUES
    ('traitement_en_cours', 'Patient encore sous traitement (psychotrope déjà délivré, durée prescrite non écoulée)', true, 1, NULL, true, 6)
ON CONFLICT (code) DO NOTHING;

-- Synchronisation (126 à 131) : ligne de réglage (désactivée, code officine tiré au sort une fois) et classement de chaque
-- table (sens, clé, colonnes exclues, sous code, textes filtrés). Sans ces lignes, une installation neuve depuis init ne
-- pourrait pas activer la synchronisation (sync_tables_non_classees). Test : SynchronisationInventaireTests (Le_seed_contient_le_classement_de_synchronisation_des_scripts).
INSERT INTO sync_parametre DEFAULT VALUES ON CONFLICT (id) DO NOTHING;
INSERT INTO sync_table (nom_table, sens, cle_colonnes, colonnes_exclues, colonnes_patient, colonnes_texte, porteur, motif) VALUES
    ('alerte', 'montante', '{id}', '{}', '{}', '{action_prise,message}', false, NULL),
    ('alerte_non_envoyee', 'jamais', '{id}', '{}', '{}', '{}', false, 'Technique, local'),
    ('alerte_surveillance', 'montante', '{id}', '{}', '{}', '{action_prise,message}', false, NULL),
    ('annulation_registre', 'montante', '{id}', '{}', '{}', '{motif}', false, NULL),
    ('annulation_vente', 'montante', '{id}', '{}', '{}', '{motif}', false, NULL),
    ('avoir_mouvement', 'montante', '{id}', '{}', '{patient_id}', '{motif}', false, NULL),
    ('bascule_promotion', 'jamais', '{id}', '{}', '{}', '{}', false, 'Promotions notées : saut des compteurs fait une seule fois par timeline, script 164'),
    ('bascule_saut', 'jamais', '{id}', '{}', '{}', '{}', false, 'Sauts de compteurs faits à une promotion (ancienne et nouvelle valeur), script 164'),
    ('boite', 'montante', '{id}', '{}', '{}', '{}', false, NULL),
    ('boite_evenement', 'montante', '{id}', '{}', '{patient_id}', '{commentaire}', false, NULL),
    ('bordereau', 'montante', '{id}', '{}', '{}', '{}', false, NULL),
    ('bordereau_vente', 'montante', '{id}', '{}', '{}', '{rejet_texte}', false, NULL),
    ('campagne_inventaire', 'montante', '{id}', '{}', '{}', '{commentaire}', false, NULL),
    ('campagne_inventaire_ligne', 'montante', '{campagne_id,lot_id}', '{}', '{}', '{}', false, NULL),
    ('catalogue_grossiste', 'montante', '{fournisseur_id,produit_id}', '{}', '{}', '{}', false, NULL),
    ('categorie_produit', 'descendante', '{code}', '{}', '{}', '{}', false, NULL),
    ('centre_payeur', 'descendante', '{id}', '{}', '{}', '{}', false, NULL),
    ('chaine_integrite', 'jamais', '{journal}', '{}', '{}', '{}', false, 'Registre technique local'),
    ('chifa_code_produit', 'montante', '{code}', '{}', '{}', '{}', false, 'Code CHIFA associé à un produit dans l''officine (n° de produit local), script 147'),
    ('classe_suivie', 'descendante', '{code}', '{}', '{}', '{}', false, NULL),
    ('classe_suivie_dci', 'descendante', '{classe_code,dci_id}', '{}', '{}', '{}', false, NULL),
    ('client_facture', 'montante', '{id}', '{telephone,email}', '{}', '{}', false, NULL),
    ('cloture_fiscale', 'montante', '{id}', '{}', '{}', '{}', false, NULL),
    ('commande_fournisseur', 'bidirectionnelle', '{id}', '{}', '{}', '{}', false, 'Commande en brouillon préparée à distance'),
    ('commande_ordre_central', 'jamais', '{commande_id}', '{}', '{}', '{}', false, 'Lien commande / ordre du serveur central (local), script 146'),
    ('comptage_caisse', 'montante', '{session_id,coupure}', '{}', '{}', '{}', false, NULL),
    ('comptage_intermediaire', 'montante', '{id}', '{}', '{}', '{}', false, NULL),
    ('comptage_reception', 'montante', '{id}', '{}', '{}', '{}', false, NULL),
    ('comptage_reception_ligne', 'montante', '{id}', '{}', '{}', '{}', false, NULL),
    ('compte_carte_periode', 'montante', '{id}', '{}', '{}', '{}', false, NULL),
    ('compte_tresorerie', 'montante', '{id}', '{}', '{}', '{}', false, NULL),
    ('compteur_ticket', 'montante', '{id}', '{}', '{}', '{}', false, 'Compteur des tickets : continuité vérifiée au serveur central (lot S4, script 144)'),
    ('compteur_ticket_saut', 'jamais', '{id}', '{}', '{}', '{}', false, 'Plages de n° de tickets sautées après un retour en arrière, script 151'),
    ('connexion_employe', 'montante', '{id}', '{}', '{}', '{}', false, NULL),
    ('correction_lot', 'montante', '{id}', '{}', '{}', '{motif}', false, NULL),
    ('correspondance_echeancier', 'montante', '{compte_id}', '{}', '{}', '{}', false, NULL),
    ('correspondance_echeancier_preteur', 'montante', '{preteur}', '{}', '{}', '{}', false, NULL),
    ('credit_imputation', 'montante', '{id}', '{}', '{}', '{}', false, NULL),
    ('credit_mouvement', 'montante', '{id}', '{}', '{patient_id}', '{}', false, NULL),
    ('credit_ouverture', 'montante', '{patient_id}', '{}', '{patient_id}', '{}', false, NULL),
    ('dci', 'descendante', '{id}', '{}', '{}', '{}', false, NULL),
    ('demande_patient', 'montante', '{id}', '{sms_erreur}', '{patient_id}', '{}', false, NULL),
    ('destinataire_notification', 'jamais', '{id}', '{}', '{}', '{}', false, 'Coordonnées personnelles'),
    ('ecart_caisse_vu', 'montante', '{session_id}', '{}', '{}', '{}', false, NULL),
    ('echeance_pret', 'montante', '{id}', '{}', '{}', '{}', false, NULL),
    ('emplacement', 'montante', '{id}', '{}', '{}', '{}', false, NULL),
    ('evenement_caisse', 'montante', '{id}', '{}', '{}', '{detail}', false, NULL),
    ('facture_client', 'montante', '{id}', '{destinataire_nom,destinataire_infos}', '{patient_id}', '{commentaire,destinataire,motif,motif_hors_suivi,motif_rejet}', false, NULL),
    ('facture_client_ligne', 'montante', '{id}', '{}', '{}', '{}', false, NULL),
    ('facture_client_version', 'montante', '{facture_id,version}', '{contenu}', '{}', '{motif}', false, 'Contenu figé retiré (nom du destinataire)'),
    ('facture_compteur', 'jamais', '{type,annee}', '{}', '{}', '{}', false, 'Compteur local'),
    ('forcage_alerte_psychotrope', 'montante', '{id}', '{messages}', '{patient_id}', '{motif}', false, NULL),
    ('fournisseur', 'montante', '{id}', '{}', '{}', '{}', false, NULL),
    ('fournisseur_mouvement', 'montante', '{id}', '{}', '{}', '{}', false, NULL),
    ('historique_chifa', 'jamais', '{id}', '{}', '{}', '{}', false, 'Reprise : nom et n° d''assuré en clair'),
    ('import_catalogue_grossiste', 'jamais', '{id}', '{}', '{}', '{}', false, 'Journal d''import local'),
    ('indication', 'descendante', '{id}', '{}', '{}', '{}', false, NULL),
    ('integrite_essai', 'jamais', '{id}', '{}', '{}', '{}', false, 'Table d''essai interne'),
    ('integrite_prise_acte', 'montante', '{id}', '{}', '{}', '{cause}', false, 'Prise d''acte du titulaire d''une restauration constatée par le serveur central, script 151'),
    ('inventaire', 'montante', '{id}', '{}', '{}', '{}', false, NULL),
    ('journal_audit', 'montante', '{id}', '{details}', '{}', '{}', false, 'Détails libres retirés (peuvent contenir des données personnelles)'),
    ('journal_registre_psychotrope', 'montante', '{id}', '{porteur_nom,porteur_adresse,porteur_piece}', '{patient_id}', '{motif}', true, 'Journal chaîné : colonnes du porteur retirées, le serveur central ne peut pas recalculer l''empreinte (lot S4)'),
    ('journal_sauvegarde', 'montante', '{id}', '{}', '{}', '{}', false, NULL),
    ('journal_vente', 'montante', '{id}', '{}', '{}', '{motif}', false, 'Journal chaîné, envoyé avec rang et empreinte'),
    ('laboratoire', 'descendante', '{id}', '{}', '{}', '{}', false, NULL),
    ('ligne_commande', 'bidirectionnelle', '{id}', '{}', '{}', '{}', false, NULL),
    ('ligne_inventaire', 'montante', '{id}', '{}', '{}', '{}', false, NULL),
    ('ligne_retour_fournisseur', 'montante', '{id}', '{}', '{}', '{}', false, NULL),
    ('ligne_vente', 'montante', '{id}', '{}', '{}', '{raison_lot}', false, NULL),
    ('lot', 'montante', '{id}', '{}', '{}', '{motif_blocage}', false, NULL),
    ('medecin', 'jamais', '{id}', '{telephone,adresse,email,numero_ordre,code_prestataire}', '{}', '{}', false, 'Envoi en option (sync_parametre.envoyer_medecins, défaut non) : nom, spécialité, wilaya'),
    ('message_sortant', 'jamais', '{id}', '{}', '{}', '{}', false, 'Contenu et adresse des SMS et courriels'),
    ('motif_reglable', 'bidirectionnelle', '{id}', '{}', '{}', '{}', false, NULL),
    ('mouvement_stock', 'montante', '{id}', '{}', '{}', '{}', false, NULL),
    ('mouvement_tiroir', 'montante', '{id}', '{}', '{}', '{beneficiaire,detail,libelle,motif_correction,raison}', false, NULL),
    ('mouvement_tresorerie', 'montante', '{id}', '{}', '{}', '{libelle}', false, NULL),
    ('nomenclature_produit', 'descendante', '{id}', '{}', '{}', '{}', false, NULL),
    ('nomenclature_version', 'descendante', '{id}', '{}', '{}', '{}', false, NULL),
    ('ordonnance', 'jamais', '{id}', '{}', '{}', '{}', false, 'Ordonnances et images scannées restent dans l''officine (décision 2)'),
    ('organisme', 'descendante', '{id}', '{}', '{}', '{}', false, NULL),
    ('paiement', 'montante', '{id}', '{}', '{patient_id}', '{}', false, NULL),
    ('paiement_echeance_pret', 'montante', '{id}', '{}', '{}', '{}', false, NULL),
    ('paiement_effet', 'montante', '{id}', '{}', '{}', '{}', false, NULL),
    ('panier_attente', 'jamais', '{id}', '{}', '{}', '{}', false, 'Transitoire'),
    ('parametre_alerte', 'bidirectionnelle', '{id}', '{}', '{}', '{}', false, NULL),
    ('parametre_caisse', 'bidirectionnelle', '{id}', '{}', '{}', '{}', false, NULL),
    ('parametre_fiscal', 'bidirectionnelle', '{id}', '{}', '{}', '{}', false, NULL),
    ('parametre_formulaire_sms', 'bidirectionnelle', '{id}', '{}', '{}', '{}', false, NULL),
    ('parametre_gs1_ai', 'descendante', '{code}', '{}', '{}', '{}', false, NULL),
    ('parametre_integrite', 'jamais', '{id}', '{}', '{}', '{}', false, 'Technique, local'),
    ('parametre_inventaire', 'bidirectionnelle', '{id}', '{}', '{}', '{}', false, NULL),
    ('parametre_notification', 'bidirectionnelle', '{id}', '{smtp_identifiant,smtp_mot_de_passe}', '{}', '{}', false, 'Jamais le compte de messagerie'),
    ('parametre_rapport', 'bidirectionnelle', '{id}', '{}', '{}', '{}', false, NULL),
    ('parametre_sauvegarde', 'bidirectionnelle', '{id}', '{}', '{}', '{}', false, NULL),
    ('parametre_serialisation', 'bidirectionnelle', '{id}', '{}', '{}', '{}', false, NULL),
    ('parametre_substitution', 'bidirectionnelle', '{id}', '{}', '{}', '{}', false, NULL),
    ('parametre_surveillance', 'bidirectionnelle', '{id}', '{}', '{}', '{}', false, NULL),
    ('parametre_tracabilite', 'bidirectionnelle', '{id}', '{}', '{}', '{}', false, NULL),
    ('patient', 'montante', '{id}', '{nom,prenom,date_naissance,telephone,num_assure,email}', '{id,assure_principal_id}', '{lien}', false, 'Pseudonyme : le code remplace l''identifiant'),
    ('permission', 'jamais', '{code}', '{}', '{}', '{}', false, 'Livrée par les scripts du logiciel'),
    ('pharmacie', 'montante', '{id}', '{}', '{}', '{}', false, NULL),
    ('presence_poste', 'jamais', '{poste}', '{}', '{}', '{}', false, 'Présence des postes vue par le serveur principal (règle de majorité du 2e serveur), script 165'),
    ('pret_bancaire', 'montante', '{id}', '{}', '{}', '{objet}', false, NULL),
    ('produit', 'bidirectionnelle', '{id}', '{}', '{}', '{motif_blocage}', false, 'Prix et marges'),
    ('produit_indication', 'montante', '{produit_id,indication_id}', '{}', '{}', '{}', false, 'Indications associées à un produit dans l''officine (n° de produit local), script 147'),
    ('profil_catalogue_grossiste', 'jamais', '{fournisseur_id}', '{}', '{}', '{}', false, 'Réglage d''import local'),
    ('reception', 'montante', '{id}', '{}', '{}', '{}', false, NULL),
    ('reception_attente', 'jamais', '{id}', '{}', '{}', '{}', false, 'Transitoire'),
    ('reception_ligne', 'montante', '{id}', '{}', '{}', '{}', false, NULL),
    ('registre_psychotrope', 'montante', '{id}', '{porteur_nom,porteur_adresse,porteur_piece}', '{patient_id}', '{}', true, NULL),
    ('reglage_central', 'descendante', '{cle}', '{}', '{}', '{}', false, 'Réglages décidés au serveur central (familles nominatives…), script 131'),
    ('reglage_mutualise', 'montante', '{nom_table}', '{modele}', '{}', '{motif_rejet}', false, 'État de la mutualisation des réglages (mode, version appliquée, rejet), script 141'),
    ('regle_alerte_psychotrope', 'bidirectionnelle', '{code}', '{}', '{}', '{}', false, NULL),
    ('reglement_facture', 'montante', '{reglement_id,reception_id}', '{}', '{}', '{}', false, NULL),
    ('reglement_facture_client', 'montante', '{mouvement_id,facture_id}', '{}', '{}', '{}', false, NULL),
    ('reglement_fournisseur', 'montante', '{id}', '{}', '{}', '{commentaire,motif_annulation}', false, NULL),
    ('regularisation_part_organisme', 'montante', '{id}', '{}', '{}', '{motif_annulation}', false, NULL),
    ('remboursement_titulaire', 'montante', '{id}', '{}', '{}', '{libelle}', false, NULL),
    ('remise_automatique', 'bidirectionnelle', '{id}', '{}', '{}', '{motif}', false, NULL),
    ('remise_cheque_paiement', 'montante', '{remise_id,paiement_id}', '{}', '{}', '{}', false, NULL),
    ('remise_cheques', 'montante', '{id}', '{}', '{}', '{}', false, NULL),
    ('retour_client', 'montante', '{id}', '{}', '{}', '{motif,motif_texte}', false, NULL),
    ('retour_client_ligne', 'montante', '{id}', '{}', '{}', '{}', false, NULL),
    ('retour_fournisseur', 'montante', '{id}', '{}', '{}', '{motif}', false, NULL),
    ('role', 'bidirectionnelle', '{id}', '{}', '{}', '{}', false, NULL),
    ('role_permission', 'bidirectionnelle', '{role_id,permission_code}', '{}', '{}', '{}', false, NULL),
    ('rupture_grossiste', 'montante', '{id}', '{}', '{}', '{}', false, NULL),
    ('scan_reception', 'jamais', '{id}', '{}', '{}', '{}', false, 'Transitoire'),
    ('sceau_integrite', 'montante', '{id}', '{}', '{}', '{}', false, 'Sceaux recopiés tels quels ; la clé ne part jamais'),
    ('serialisation_reponse_factice', 'jamais', '{id}', '{}', '{}', '{}', false, 'Essai'),
    ('serveur_ecarte', 'jamais', '{id}', '{}', '{}', '{}', false, 'État local « serveur écarté » (lecture seule après bascule), script 163'),
    ('serveur_officine', 'jamais', '{nom}', '{}', '{}', '{}', false, 'Réglages propres à chaque serveur'),
    ('session_caisse', 'montante', '{id}', '{}', '{}', '{}', false, NULL),
    ('substance_surveillee', 'descendante', '{motif}', '{}', '{}', '{}', false, NULL),
    ('substitution_liste_blanche', 'bidirectionnelle', '{produit_id,remplacant_id}', '{}', '{}', '{}', false, NULL),
    ('surveillance_replication', 'jamais', '{id}', '{}', '{}', '{}', false, 'Réplication locale'),
    ('sync_chargement', 'jamais', '{numero}', '{}', '{}', '{}', false, 'Synchronisation elle-même'),
    ('sync_declaration_epoque', 'jamais', '{nouvelle_epoque}', '{}', '{}', '{}', false, 'Déclaration du changement d''époque envoyée par la route du serveur central, script 151'),
    ('sync_famille_colonne', 'jamais', '{famille,nom_table,colonne}', '{}', '{}', '{}', false, 'Synchronisation elle-même'),
    ('sync_journal_descendant', 'jamais', '{id}', '{}', '{}', '{}', false, 'Synchronisation elle-même (journal de la voie descendante, script 136)'),
    ('sync_lot', 'jamais', '{epoque,numero}', '{}', '{}', '{}', false, 'Synchronisation elle-même'),
    ('sync_ordre_recu', 'jamais', '{id}', '{}', '{}', '{}', false, 'Ordres reçus du serveur central (données d''employés possibles), script 145'),
    ('sync_ordre_resultat', 'montante', '{ordre_id}', '{}', '{}', '{motif}', false, 'Résultat des ordres du serveur central, script 145'),
    ('sync_parametre', 'jamais', '{id}', '{}', '{}', '{}', false, 'Synchronisation elle-même'),
    ('sync_pseudonyme', 'jamais', '{nature,cle_locale}', '{}', '{}', '{}', false, 'Correspondance personne <-> code : ne quitte jamais l''officine'),
    ('sync_sortie', 'jamais', '{id}', '{}', '{}', '{}', false, 'Synchronisation elle-même'),
    ('sync_table', 'jamais', '{nom_table}', '{}', '{}', '{}', false, 'Synchronisation elle-même'),
    ('type_prestation', 'descendante', '{id}', '{}', '{}', '{}', false, NULL),
    ('utilisateur', 'bidirectionnelle', '{id}', '{mot_de_passe_hash,pin_hash,code_comptoir_essais,code_comptoir_bloque_jusqu,telephone,email,jetons_revoques_le}', '{}', '{}', false, 'Jamais de mot de passe ni de code comptoir'),
    ('utilisation_financement', 'montante', '{id}', '{}', '{}', '{libelle}', false, NULL),
    ('vente', 'montante', '{id}', '{remis_a_nom,pour_nom,chifa_qr}', '{patient_id,remis_a_patient_id,pour_patient_id}', '{chifa_attente_motif_texte,chifa_ecart_detail,chifa_ecart_motif,chifa_exclusion_texte}', false, NULL),
    ('vente_chifa_facture_conflit', 'montante', '{vente_id}', '{}', '{}', '{}', false, NULL),
    ('verification_integrite', 'montante', '{id}', '{}', '{}', '{}', false, NULL),
    ('verification_serialisation', 'montante', '{id}', '{}', '{}', '{message}', false, NULL),
    ('versement_bordereau', 'montante', '{id}', '{}', '{}', '{}', false, NULL),
    ('wilaya', 'descendante', '{code}', '{}', '{}', '{}', false, NULL),
    ('zone_psychotrope_wilaya', 'descendante', '{wilaya}', '{}', '{}', '{}', false, NULL)
ON CONFLICT (nom_table) DO NOTHING;

-- Mutualisation des réglages (141) : une ligne d'état par table mutualisable, mode « local », aucun modèle reçu (rien n'est imposé ni publié)
INSERT INTO reglage_mutualise (nom_table)
SELECT t FROM unnest(reglage_tables_mutualisables()) AS t
ON CONFLICT (nom_table) DO NOTHING;

-- Synchronisation (130, 133) : colonnes des familles de données nominatives réglées au serveur central (retirées avant envoi
-- si la famille n'est pas autorisée ; sans ces lignes, rien ne serait retiré)
INSERT INTO sync_famille_colonne (famille, nom_table, colonne) VALUES
    ('employes', 'utilisateur', 'nom_complet'), ('employes', 'utilisateur', 'identifiant'),
    ('pharmacie', 'pharmacie', 'titulaire'), ('pharmacie', 'pharmacie', 'adresse'), ('pharmacie', 'pharmacie', 'telephone'),
    ('pharmacie', 'parametre_notification', 'smtp_expediteur'),
    ('fournisseurs', 'fournisseur', 'telephone'), ('fournisseurs', 'fournisseur', 'email'), ('fournisseurs', 'fournisseur', 'adresse'),
    ('courriel_commandes', 'commande_fournisseur', 'envoyee_email_a'),
    -- 133 : identifiants de la pharmacie, banque (défaut non), clients facturés (défaut non)
    ('pharmacie', 'pharmacie', 'nom'), ('pharmacie', 'pharmacie', 'nif'), ('pharmacie', 'pharmacie', 'nis'), ('pharmacie', 'pharmacie', 'rc'),
    ('pharmacie', 'pharmacie', 'article_imposition'),
    ('banque', 'compte_tresorerie', 'numero_compte'), ('banque', 'pret_bancaire', 'preteur'),
    ('clients_factures', 'client_facture', 'adresse'),
    -- 135 : famille banque élargie (défaut non)
    ('banque', 'compte_tresorerie', 'banque'), ('banque', 'reglement_fournisseur', 'banque'), ('banque', 'reglement_fournisseur', 'reference'),
    ('banque', 'paiement_effet', 'reference'), ('banque', 'remise_cheques', 'reference'), ('banque', 'pret_bancaire', 'reference')
ON CONFLICT DO NOTHING;

-- ---------------------------------------------------------------------
-- 8. Données techniques écrites par les scripts 111, 112, 116, 117, 120, 122, 123 et 143 sur une base vide
--    (sans elles, schema.sql seul donne une base dont le registre des psychotropes et le journal des ventes refusent toute écriture :
--    « Table ... chaînée mais absente de chaine_integrite »). Même ordre que les scripts ; chaque ligne n'est posée que si elle manque
--    (réexécutable, y compris après 111b : integrite_activer, qui modifie les tables, n'est rappelée que pour un journal non encore inscrit).
-- ---------------------------------------------------------------------
-- 111 : paramètres de l'intégrité (algorithme par défaut lu par integrite_activer) puis les trois journaux chaînés, colonnes identiques à
-- celles des scripts 111, 112 et 117 (elles ne changent plus ensuite)
INSERT INTO parametre_integrite (id) VALUES (1) ON CONFLICT (id) DO NOTHING;

DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM chaine_integrite WHERE journal = 'integrite_essai') THEN
        PERFORM integrite_activer('integrite_essai', 'Table d''essai de la brique (interne)', ARRAY['id', 'libelle', 'montant', 'cree_le']);
    END IF;
    IF NOT EXISTS (SELECT 1 FROM chaine_integrite WHERE journal = 'journal_vente') THEN
        PERFORM integrite_activer('journal_vente', 'Journal des ventes (fiscal)', ARRAY[
            'id', 'evenement', 'numero_ticket', 'vente_id', 'annulation_id', 'retour_client_id', 'avoir_mouvement_id', 'regularisation_id',
            'survenu_le', 'enregistre_le', 'session_caisse_id', 'poste', 'utilisateur_id', 'type_vente',
            'montant_ttc', 'montant_ht', 'montant_tva', 'detail_tva', 'part_patient', 'part_organisme', 'paiements', 'montant_mouvement', 'motif']);
    END IF;
    IF NOT EXISTS (SELECT 1 FROM chaine_integrite WHERE journal = 'journal_registre_psychotrope') THEN
        PERFORM integrite_activer('journal_registre_psychotrope', 'Registre des psychotropes',
            ARRAY['id', 'nature', 'registre_id', 'numero_ordre', 'lot_id', 'ligne_vente_id', 'mouvement_id', 'type_mouvement', 'quantite',
                  'solde_apres', 'patient_id', 'medecin_id', 'ordonnance_id', 'utilisateur_id', 'reference', 'porteur_nom', 'porteur_adresse',
                  'porteur_piece', 'inscrit_le', 'lignes_anterieures', 'empreinte_anterieure', 'dernier_registre_id', 'annulation_id', 'motif',
                  'cree_le']);
    END IF;
END $$;

-- 112 : paramètres fiscaux, compteur des tickets (base vide : dernier n° 0), puis ligne d'ouverture du journal des ventes
INSERT INTO parametre_fiscal (id) VALUES (1) ON CONFLICT (id) DO NOTHING;

INSERT INTO compteur_ticket (id, dernier_numero, premier_numero_continu)
SELECT 1, m, m + 1
FROM (SELECT COALESCE(max(substring(numero FROM '^T-([0-9]+)$')::bigint), 0) AS m FROM vente) x
ON CONFLICT (id) DO NOTHING;

INSERT INTO journal_vente (evenement, survenu_le, motif)
SELECT 'ouverture', c.mis_en_service_le,
       format('Mise en service du journal fiscal : premier ticket continu %s%s ; ventes antérieures non reprises (%s vente(s) en base)',
              c.prefixe, lpad(c.premier_numero_continu::text, GREATEST(6, char_length(c.premier_numero_continu::text)), '0'),
              (SELECT count(*) FROM vente))
FROM compteur_ticket c
WHERE c.id = 1 AND NOT EXISTS (SELECT 1 FROM journal_vente);

-- 116 : substitution (équivalent exact seulement) ; 123 : texte du formulaire d'accord SMS (vide) ; 122 et 143 : seuils de la réplication
INSERT INTO parametre_substitution (id) VALUES (1) ON CONFLICT (id) DO NOTHING;
INSERT INTO parametre_formulaire_sms (id) VALUES (1) ON CONFLICT (id) DO NOTHING;
INSERT INTO surveillance_replication (id) VALUES (1) ON CONFLICT (id) DO NOTHING;

-- 120 : sérialisation (connecteur factice) et identifiants d'application GS1 reconnus
INSERT INTO parametre_serialisation DEFAULT VALUES ON CONFLICT (id) DO NOTHING;
INSERT INTO parametre_gs1_ai (code, champ, longueur_fixe, longueur_max, libelle) VALUES
    ('01', 'gtin',       14,   NULL, 'GTIN'),
    ('17', 'peremption', 6,    NULL, 'Date de péremption (AAMMJJ)'),
    ('10', 'lot',        NULL, NULL, 'Numéro de lot (variable)'),
    ('21', 'serie',      NULL, NULL, 'Numéro de série (variable)')
ON CONFLICT (code) DO NOTHING;

-- 117 : classe suivie « antibiotiques » (la liste des DCI se remplit à l'import de la nomenclature, pas ici) puis ligne d'ouverture du
-- registre des psychotropes (registre vide : 0 ligne antérieure ; écrite avant toute inscription)
INSERT INTO classe_suivie (code, libelle) VALUES ('antibiotiques', 'Antibiotiques') ON CONFLICT (code) DO NOTHING;

DO $$
DECLARE
    v record;
BEGIN
    LOCK TABLE registre_psychotrope IN SHARE ROW EXCLUSIVE MODE;
    IF NOT EXISTS (SELECT 1 FROM journal_registre_psychotrope WHERE nature = 'ouverture') THEN
        SELECT * INTO v FROM registre_empreinte_anterieure();
        INSERT INTO journal_registre_psychotrope (nature, lignes_anterieures, dernier_registre_id, empreinte_anterieure, inscrit_le, motif)
        VALUES ('ouverture', v.lignes, v.dernier_id, v.empreinte, now(),
                'Ouverture du chaînage : ' || v.lignes || ' ligne(s) antérieure(s) scellée(s) par leur empreinte globale');
    END IF;
END $$;

COMMIT;

\echo 'Référentiel chargé. Connexion : titulaire / Provisoire-2026 (à changer à la première connexion).'
