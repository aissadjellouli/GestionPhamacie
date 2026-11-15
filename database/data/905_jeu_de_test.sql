-- =====================================================================
-- Jeu de test FICTIF de grande taille (développement et tests seulement,
-- jamais en production) : de quoi remplir les rapports, l'Accueil, les
-- relevés de crédit, les dettes envers les grossistes et les traites.
--
--   * environ 200 patients (assurés CNAS et CASNOS, non assurés, certains
--     avec crédit et plafond) et 40 médecins ;
--   * environ 155 médicaments REELS repris de la nomenclature nationale
--     importée (table nomenclature_produit : marque, DCI, dosage, forme,
--     conditionnement, n° d'enregistrement, classe) et 35 produits de
--     parapharmacie fictifs. Seuls les prix, le tarif de référence, le
--     caractère remboursable, les lots et les quantités sont fictifs ;
--   * 1 à 4 lots par produit : lots normaux, proches de la péremption,
--     périmés à retirer, ruptures, surstocks ;
--   * environ 3 000 ventes sur les 90 derniers jours (deux caisses par jour,
--     de 8 h à 20 h) : vente libre, tiers payant (CNAS, CASNOS), crédit,
--     remises et dons partiels, annulations, retours, ordonnances, registre
--     des psychotropes ; sessions de caisse clôturées (attendu, compté, écart) ;
--   * 5 grossistes fictifs : bons de commande, réceptions (BL, factures),
--     règlements, traites en attente, échues, payées ; dette et relevés.
--
-- Cohérence : stock décrémenté par lot (mouvement_stock), part de
-- l'organisme, remise, don, SHP et totaux calculés comme le logiciel, relevés
-- de crédit et de dette inscrits avec leurs vraies dates.
-- Les déclencheurs des relevés (patient, grossiste) sont suspendus le temps
-- de la transaction pour écrire les mouvements aux dates de l'historique, puis
-- réactivés avant la validation (aucune modification de schéma).
--
-- Ajouts seulement : aucune donnée existante n'est modifiée ni supprimée.
-- Un seul passage : si la caisse « Caisse démo » existe déjà, le script
-- s'arrête sans rien faire. Tout se passe dans une transaction.
-- A exécuter après les scripts 0xx et 900 à 904, nomenclature nationale
-- importée. Dates calculées par rapport au jour d'exécution.
-- =====================================================================
SELECT EXISTS (SELECT 1 FROM session_caisse WHERE poste = 'Caisse démo') AS jeu_deja_charge \gset
\if :jeu_deja_charge
\echo 'Jeu de test 905 déjà présent : rien à faire.'
\quit
\endif

SELECT NOT (
        (SELECT count(*) FROM utilisateur WHERE identifiant IN ('titulaire', 'assistant', 'vendeur')) = 3
    AND (SELECT count(*) FROM organisme WHERE code IN ('CNAS', 'CASNOS')) = 2
    AND EXISTS (SELECT 1 FROM compte_tresorerie WHERE type = 'coffre')
    AND EXISTS (SELECT 1 FROM compte_tresorerie WHERE type = 'banque' AND actif)
    AND EXISTS (SELECT 1 FROM emplacement WHERE zone = 'vente' AND actif)
    AND (SELECT count(*) FROM nomenclature_produit) > 1000
) AS prerequis_manquants \gset
\if :prerequis_manquants
\echo 'Prérequis absents (comptes titulaire/assistant/vendeur, CNAS, CASNOS, coffre, banque, emplacements, nomenclature) : jeu de test ignoré.'
\quit
\endif

BEGIN;
SET LOCAL lock_timeout = '30s';
SELECT setseed(0.905);

-- Relevés tenus par déclencheur : suspendus pour écrire les mouvements datés (réactivés en fin de script)
ALTER TABLE patient DISABLE TRIGGER trg_patient_credit;
ALTER TABLE fournisseur DISABLE TRIGGER trg_fournisseur_dette;

CREATE FUNCTION pg_temp.h905(t text, m int) RETURNS int LANGUAGE sql IMMUTABLE AS
$$ SELECT (('x' || substr(md5(t), 1, 7))::bit(28)::int) % m $$;

-- ---------------------------------------------------------------------
-- 1. Médicaments réels : choix dans la nomenclature nationale
--    (prefixe de DCI, nombre de produits, prix de base fictif, poids de la demande)
-- ---------------------------------------------------------------------
CREATE TEMP TABLE d905_choix (prefixe text, n int, prix numeric, poids int) ON COMMIT DROP;
INSERT INTO d905_choix
SELECT split_part(l, '|', 1), split_part(l, '|', 2)::int, split_part(l, '|', 3)::numeric, split_part(l, '|', 4)::int
FROM unnest(ARRAY[
'PARACETAMOL|4|150|9','IBUPROFENE|3|240|8','ACIDE ACETYLSALICYLIQUE|1|120|4','DICLOFENAC SODIQUE|2|300|6','DICLOFENAC DIETHYLAMINE|1|450|4',
'KETOPROFENE|1|350|4','PIROXICAM|1|260|3','CELECOXIB|1|650|3','TRAMADOL CHLORHYDRATE|2|420|4','AMOXICILLINE TRIHYDRATE|3|380|8',
'AZITHROMYCINE|2|700|5','CLARITHROMYCINE|1|850|3','CEFIXIME|1|950|3','CEFUROXIME|1|1000|3','CEFALEXINE|1|450|3',
'CIPROFLOXACINE|1|520|4','LEVOFLOXACINE|1|800|3','NORFLOXACINE|1|420|2','METRONIDAZOLE|2|240|4','SPIRAMYCINE|1|480|3',
'FLUCONAZOLE|1|480|3','ACICLOVIR|1|520|3','CLINDAMYCINE|1|800|2','METFORMINE CHLORHYDRATE|3|220|8','GLICLAZIDE|1|360|6',
'GLIMEPIRIDE|2|450|5','SITAGLIPTINE|1|2200|3','REPAGLINIDE|1|900|2','INSULINE GLARGINE|1|3300|2','AMLODIPINE BESILATE EXPRIME EN AMLODIPINE|2|330|6',
'BISOPROLOL|2|380|6','RAMIPRIL|1|480|5','VALSARTAN|1|900|4','IRBESARTAN|1|850|3','LOSARTAN|1|620|4','TELMISARTAN|1|800|3',
'CANDESARTAN|1|800|3','FUROSEMIDE|1|160|4','ATENOLOL|1|200|3','SUCCINATE DE METOPROLOL|1|420|3','ATORVASTATINE|2|1100|5',
'ROSUVASTATINE|1|1200|4','SIMVASTATINE|1|600|3','RIVAROXABAN|1|4500|2','APIXABAN|1|4200|2','ACENOCOUMAROL|1|320|3',
'OMEPRAZOLE|2|520|6','ESOMEPRAZOLE|1|800|4','LANSOPRAZOLE|1|650|3','DOMPERIDONE|1|280|5','TRIMEBUTINE|1|420|4',
'PHLOROGLUCINOL|2|300|5','MEBEVERINE|1|480|3','LOPERAMIDE|1|230|4','DIOSMECTITE|1|340|4','LACTULOSE|1|420|3',
'RACECADOTRIL|1|600|3','SALBUTAMOL|1|420|5','BUDESONIDE|1|1500|3','MONTELUKAST|1|1350|3','CETIRIZINE|2|260|7',
'LORATADINE|1|230|5','DESLORATADINE|1|400|3','FEXOFENADINE|1|520|3','CARBOCISTEINE|1|330|5','CHLORHYDRATE D''AMBROXOL|1|290|4',
'DEXTROMETHORPHANE|1|300|3','PREDNISOLONE SOUS FORME METASULFOBENZOATE|1|420|4','PREDNISONE|1|160|3','METHYLPREDNISOLONE|1|500|2','BETAMETHASONE|1|350|3',
'ACIDE FUSIDIQUE|1|450|3','MICONAZOLE|1|350|2','ECONAZOLE NITRATE|1|380|2','TERBINAFINE|1|600|2','KETOCONAZOLE|1|400|2',
'LEVOTHYROXINE SODIQUE|2|230|6','ALENDRONATE|1|900|2','ALLOPURINOL|1|220|3','CHOLECALCIFEROL|1|280|3','ACIDE FOLIQUE|1|130|3',
'ACIDE ASCORBIQUE|1|250|3','CYANOCOBALAMINE|1|300|2','HYDROXYDE FERRIQUE POLYMALTOSE|1|650|3','CALCIUM CARBONATE|1|380|2','BROMAZEPAM|1|260|3',
'CLONAZEPAM|1|300|2','CLORAZEPATE|1|330|2','LORAZEPAM|1|280|3','ZOLPIDEM|1|550|2','PREGABALINE|2|1100|3',
'GABAPENTINE|1|800|2','CARBAMAZEPINE|1|300|2','LEVETIRACETAM|1|1100|2','VALPROATE|1|480|2','LAMOTRIGINE|1|700|2',
'SERTRALINE|1|900|3','PAROXETINE|1|800|3','ESCITALOPRAM|1|900|3','AMITRIPTYLINE|1|280|3','VENLAFAXINE|1|900|2',
'OLANZAPINE|1|1200|2','RISPERIDONE|1|700|2','QUETIAPINE|1|1100|2','HALOPERIDOL|1|250|2','TRIHEXYPHENIDYLE|1|300|2',
'HYDROXYZINE|1|380|3','TAMSULOSINE|1|700|3','FINASTERIDE|1|650|2','SILDENAFIL|1|1500|2','TADALAFIL|1|1700|2',
'SUCCINATE DE SOLIFENACINE|1|1500|2','OXYBUTYNINE|1|400|2','LATANOPROST|1|1300|2','TIMOLOL|1|650|2','FLUBENDAZOLE|1|220|3',
'ALBENDAZOLE|1|300|2','POLYVIDONE IODEE|1|380|3','ONDANSETRON|1|600|2','BETAHISTINE|1|450|3','MOLSIDOMINE|1|420|2',
'TRIMETAZIDINE|1|480|2','FEBUXOSTAT|1|1200|2','ENOXAPARINE|1|2200|2','DEXCHLORPHENIRAMINE|1|200|3','OXOMEMAZINE|1|260|3',
'ETAMSYLATE|1|350|2','HYDROCORTISONE|1|300|2','DONEPEZIL|1|1300|1','ACIDE URSODESOXYCHOLIQUE|1|1400|1','DAPAGLIFLOZINE|1|2600|2',
'BACLOFENE|1|300|1','PRAZEPAM|1|300|2'
]) l;

-- Lignes réelles de la nomenclature (version la plus récente, vendues en officine, en vigueur),
-- jamais déjà présentes au catalogue (même n° d'enregistrement, ou même nom, dosage et forme)
CREATE TEMP TABLE d905_nom ON COMMIT DROP AS
SELECT x.* FROM d905_choix c
CROSS JOIN LATERAL (
  SELECT * FROM (
    SELECT DISTINCT ON (n.num_enregistrement)
           n.id, n.num_enregistrement, btrim(n.marque) AS marque, btrim(n.dci) AS dci, btrim(n.forme) AS forme, btrim(n.dosage) AS dosage,
           btrim(n.conditionnement) AS cond, btrim(n.liste) AS liste, n.type, n.classe_proposee, c.prix, c.poids
    FROM v_nomenclature n
    WHERE n.situation = 'en_vigueur' AND n.p2 ILIKE 'OFF%' AND n.num_enregistrement IS NOT NULL AND btrim(n.marque) <> ''
      AND length(n.marque) <= 60 AND length(coalesce(n.dosage, '')) <= 40 AND length(coalesce(n.forme, '')) <= 60
      AND n.dci IS NOT NULL AND btrim(n.dci) LIKE c.prefixe || '%' AND n.dci !~ '/'
      AND n.version_id = (SELECT max(id) FROM nomenclature_version)
      AND NOT EXISTS (SELECT 1 FROM produit p WHERE p.num_enregistrement = n.num_enregistrement
                      OR (lower(p.nom_commercial) = lower(btrim(n.marque)) AND lower(coalesce(p.dosage, '')) = lower(coalesce(btrim(n.dosage), ''))
                          AND lower(coalesce(p.forme, '')) = lower(coalesce(btrim(n.forme), ''))))
    ORDER BY n.num_enregistrement
  ) q
  ORDER BY (q.forme !~* '^(COMPRIME|GELULE|SIROP|SUSPENSION|SACHET|CREME|GEL|POMMADE|COLLYRE|SOLUTION BUVABLE|SUPPOSITOIRE|SPRAY|AEROSOL)'), md5(q.num_enregistrement)
  LIMIT c.n
) x;

-- Catalogue à créer : médicaments de la nomenclature, puis parapharmacie fictive
CREATE TEMP TABLE d905_cat ON COMMIT DROP AS
SELECT s.*,
       -- remboursable : fictif (la nomenclature ne le précise pas) ; classe hors « libre », sauf confort ; quelques exceptions
       (s.categorie = 'medicament'
        AND ((s.classe <> 'libre' AND coalesce(s.dci, '') !~ '^(SILDENAFIL|TADALAFIL)')
             OR coalesce(s.dci, '') ~ '^(PARACETAMOL|ACIDE ACETYLSALICYLIQUE|ACIDE FOLIQUE)$')
        AND pg_temp.h905(coalesce(s.num_enreg, s.nom), 100) >= 3) AS remb
FROM (
  SELECT r.rang, r.src, r.nom, r.dci, r.dosage, r.forme, r.conditionnement, r.num_enreg, r.classe, r.categorie, r.poids, r.liste_psy, r.type, r.tva,
         CASE WHEN r.src = 1
              THEN greatest(50, round(r.base * (0.75 + pg_temp.h905(r.num_enreg, 61) / 100.0) * CASE WHEN r.type = 'RE' THEN 1.4 ELSE 1 END / 5) * 5)
              ELSE r.base END AS prix
  FROM (
    SELECT row_number() OVER (ORDER BY m.src, m.ord) AS rang, m.*
    FROM (
      SELECT 1 AS src, n.id::bigint AS ord, n.marque AS nom, n.dci, n.dosage, n.forme, n.cond AS conditionnement, n.num_enregistrement AS num_enreg,
             n.classe_proposee AS classe, 'medicament'::text AS categorie, n.poids,
             CASE WHEN n.classe_proposee = 'psychotrope' THEN CASE WHEN upper(n.liste) LIKE '%II%' THEN 'liste_ii' ELSE 'liste_i' END END AS liste_psy,
             n.type, n.prix AS base, 0::numeric AS tva
      FROM d905_nom n
      UNION ALL
      SELECT 2, t.ord, split_part(t.l, '|', 1), NULL, NULL, NULL, NULLIF(split_part(t.l, '|', 2), ''), NULL, 'libre', split_part(t.l, '|', 4),
             split_part(t.l, '|', 5)::int, NULL, NULL, split_part(t.l, '|', 3)::numeric, 19
      FROM unnest(ARRAY[
        'Crème hydratante visage|50 ml|1450|dermo_cosmetique|4','Écran solaire SPF 50+|50 ml|1900|dermo_cosmetique|3','Lait corporel nourrissant|400 ml|1200|dermo_cosmetique|3',
        'Shampooing antipelliculaire|200 ml|950|dermo_cosmetique|3','Gel lavant dermatologique|500 ml|1100|dermo_cosmetique|3','Baume à lèvres réparateur|4 g|450|dermo_cosmetique|4',
        'Eau thermale en spray|150 ml|900|dermo_cosmetique|2','Crème pour les mains|75 ml|600|dermo_cosmetique|3',
        'Savon liquide antiseptique|250 ml|480|hygiene|4','Gel hydroalcoolique|100 ml|250|hygiene|5','Dentifrice fluoré|75 ml|350|hygiene|5',
        'Bain de bouche sans alcool|250 ml|520|hygiene|3','Coton hydrophile|100 g|220|hygiene|4','Serviettes hygiéniques|16 pièces|380|hygiene|4',
        'Lait infantile 1er âge|400 g|1650|bebe_maman|4','Lait infantile 2e âge|400 g|1600|bebe_maman|4','Couches taille 3|40 pièces|1850|bebe_maman|4',
        'Lingettes bébé|72 pièces|520|bebe_maman|3','Crème change|100 g|690|bebe_maman|3',
        'Oméga 3 capsules|60 capsules|2400|complement_alimentaire|2','Multivitamines adulte|30 comprimés|1700|complement_alimentaire|3',
        'Magnésium marin|60 gélules|1500|complement_alimentaire|3','Probiotiques|20 gélules|2200|complement_alimentaire|2','Mélatonine 1,9 mg|30 comprimés|1300|complement_alimentaire|2',
        'Tisane sommeil|20 sachets|480|phyto_aroma|3','Huile essentielle de lavande|10 ml|950|phyto_aroma|2','Huile essentielle eucalyptus|10 ml|850|phyto_aroma|2',
        'Tensiomètre brassard automatique||7500|materiel_medical|1','Thermomètre digital||650|materiel_medical|3','Lecteur de glycémie||3900|materiel_medical|1',
        'Bandelettes de glycémie|50 bandelettes|2600|materiel_medical|3','Masques chirurgicaux|50 pièces|450|materiel_medical|4','Gants nitrile|100 pièces|1200|materiel_medical|3',
        'Attelle de poignet||2600|paramedical|1','Genouillère élastique||2200|paramedical|1'
      ]) WITH ORDINALITY AS t(l, ord)
    ) m
  ) r
) s;

-- Reste de la nomenclature (une ligne par n° d'enregistrement, version la plus récente) : produits du catalogue sans stock.
-- Prix et tarif fictifs (la nomenclature ne les donne pas) ; classe la plus stricte (substance surveillée) déjà dans classe_proposee.
CREATE TEMP TABLE d905_rest ON COMMIT DROP AS
SELECT s.*,
       (s.classe <> 'libre' AND coalesce(s.dci, '') !~ '^(SILDENAFIL|TADALAFIL)' OR coalesce(s.dci, '') ~ '^(PARACETAMOL|ACIDE ACETYLSALICYLIQUE|ACIDE FOLIQUE)$')
         AND pg_temp.h905(s.num_enreg, 100) >= 3 AS remb
FROM (
  SELECT q.num_enreg, q.nom, q.dci, q.dosage, q.forme, q.conditionnement, q.classe, q.type, q.situation,
         CASE WHEN q.classe = 'psychotrope' THEN CASE WHEN upper(q.liste) LIKE '%II%' THEN 'liste_ii' ELSE 'liste_i' END END AS liste_psy,
         greatest(50, round(CASE q.classe WHEN 'libre' THEN 120 + pg_temp.h905(q.num_enreg, 500) ELSE 250 + pg_temp.h905(q.num_enreg, 2200) END
                            * CASE WHEN q.type = 'RE' THEN 1.4 ELSE 1 END
                            * CASE WHEN q.forme ~* 'INJECT|PERFUS' THEN 2 ELSE 1 END / 5) * 5) AS prix
  FROM (
    SELECT DISTINCT ON (n.num_enregistrement)
           btrim(n.num_enregistrement) AS num_enreg, btrim(n.marque) AS nom, NULLIF(btrim(n.dci), '') AS dci, NULLIF(btrim(n.dosage), '') AS dosage,
           NULLIF(btrim(n.forme), '') AS forme, NULLIF(btrim(n.conditionnement), '') AS conditionnement, n.classe_proposee AS classe,
           btrim(n.liste) AS liste, upper(btrim(n.type)) AS type, n.situation
    FROM v_nomenclature n
    WHERE n.version_id = (SELECT max(id) FROM nomenclature_version) AND btrim(coalesce(n.num_enregistrement, '')) <> '' AND btrim(coalesce(n.marque, '')) <> ''
    ORDER BY n.num_enregistrement, (n.situation = 'en_vigueur') DESC, n.id
  ) q
  WHERE NOT EXISTS (SELECT 1 FROM d905_nom x WHERE x.num_enregistrement = q.num_enreg)
    AND NOT EXISTS (SELECT 1 FROM produit p WHERE p.num_enregistrement = q.num_enreg)
) s;

-- DCI manquantes (rapprochement sans casse ni accents avec les DCI existantes)
INSERT INTO dci (nom)
SELECT DISTINCT u.dci FROM (SELECT dci FROM d905_cat UNION ALL SELECT dci FROM d905_rest) u
WHERE u.dci IS NOT NULL AND NOT EXISTS (SELECT 1 FROM dci d WHERE majuscule_sans_accent(d.nom) = majuscule_sans_accent(u.dci))
ON CONFLICT (nom) DO NOTHING;
CREATE TEMP TABLE d905_dcimap ON COMMIT DROP AS
SELECT DISTINCT ON (majuscule_sans_accent(nom)) majuscule_sans_accent(nom) AS k, id FROM dci ORDER BY majuscule_sans_accent(nom), id;
CREATE UNIQUE INDEX ON d905_dcimap (k);

-- Produits (rattachés à la nomenclature par leur n° d'enregistrement, comme le logiciel)
INSERT INTO produit (code_barre, num_enregistrement, nom_commercial, dci_id, dosage, forme, conditionnement, prix_public, tarif_reference,
                     remboursable, taux_tva, classe, categorie, liste_psychotrope, est_essentiel, emplacement_id, reserve_id, cree_le)
SELECT code.ean, c.num_enreg, c.nom, d.id, c.dosage, c.forme, c.conditionnement, c.prix,
       CASE WHEN c.remb THEN least(c.prix, greatest(5, round(c.prix * CASE WHEN c.type = 'RE' THEN 0.7 ELSE 0.8 + pg_temp.h905(c.num_enreg || 't', 21) / 100.0 END / 5) * 5)) END,
       c.remb, c.tva, c.classe, c.categorie, c.liste_psy, c.poids >= 8,
       (SELECT array_agg(e.id ORDER BY e.id) FROM emplacement e WHERE e.zone = 'vente' AND e.actif)[1 + c.rang % (SELECT count(*) FROM emplacement e WHERE e.zone = 'vente' AND e.actif)],
       CASE WHEN c.rang % 3 = 0 THEN (SELECT array_agg(e.id ORDER BY e.id) FROM emplacement e WHERE e.zone = 'reserve' AND e.actif)[1 + c.rang % GREATEST((SELECT count(*) FROM emplacement e WHERE e.zone = 'reserve' AND e.actif), 1)] END,
       now() - interval '150 days'
FROM d905_cat c
CROSS JOIN LATERAL (SELECT '613905' || lpad(c.rang::text, 6, '0') AS p12) b
CROSS JOIN LATERAL (SELECT b.p12 || ean13_cle(b.p12) AS ean) code
LEFT JOIN d905_dcimap d ON d.k = majuscule_sans_accent(c.dci)
ON CONFLICT DO NOTHING;

-- Tout le reste de la nomenclature : une requête ensembliste, sans stock (actif seulement si « en vigueur »)
CREATE TEMP TABLE d905_nouveaux (produit_id bigint) ON COMMIT DROP;
WITH ins AS (
  INSERT INTO produit (num_enregistrement, nom_commercial, dci_id, dosage, forme, conditionnement, prix_public, tarif_reference,
                       remboursable, taux_tva, classe, categorie, liste_psychotrope, actif, cree_le)
  SELECT r.num_enreg, r.nom, d.id, r.dosage, r.forme, r.conditionnement, r.prix,
         CASE WHEN r.remb THEN least(r.prix, greatest(5, round(r.prix * CASE WHEN r.type = 'RE' THEN 0.7 ELSE 0.8 + pg_temp.h905(r.num_enreg || 't', 21) / 100.0 END / 5) * 5)) END,
         r.remb, 0, r.classe, 'medicament', r.liste_psy, r.situation = 'en_vigueur', now() - interval '150 days'
  FROM d905_rest r
  LEFT JOIN d905_dcimap d ON d.k = majuscule_sans_accent(r.dci)
  ON CONFLICT DO NOTHING
  RETURNING id
)
INSERT INTO d905_nouveaux SELECT id FROM ins;

CREATE TEMP TABLE d905_prod ON COMMIT DROP AS
SELECT p.id AS produit_id, c.rang, c.nom, c.poids, c.prix, c.tva, c.classe, c.categorie, c.remb, p.tarif_reference AS tarif,
       CASE c.classe WHEN 'liste_i' THEN 2.50 WHEN 'liste_ii' THEN 1.50 WHEN 'stupefiant' THEN 2.50
            WHEN 'psychotrope' THEN CASE c.liste_psy WHEN 'liste_i' THEN 2.50 WHEN 'liste_ii' THEN 1.50 ELSE 0 END ELSE 0 END::numeric AS shp,
       CASE WHEN c.classe = 'psychotrope' THEN 'N' WHEN c.rang % 11 = 3 THEN 'R' WHEN c.rang % 13 = 5 THEN 'P'
            WHEN c.rang % 9 = 4 THEN 'X' WHEN c.rang % 17 = 7 THEN 'S' ELSE 'N' END AS motif,
       0 AS e
FROM d905_cat c
JOIN produit p ON p.code_barre = '613905' || lpad(c.rang::text, 6, '0') || ean13_cle('613905' || lpad(c.rang::text, 6, '0'));
UPDATE d905_prod SET remb = (tarif IS NOT NULL);
CREATE INDEX ON d905_prod (produit_id);

-- ---------------------------------------------------------------------
-- 2. Médecins (40), patients (200), grossistes (5)
-- ---------------------------------------------------------------------
CREATE TEMP TABLE d905_nom_fam ON COMMIT DROP AS
SELECT row_number() OVER () AS idx, n FROM unnest(ARRAY['Benali','Mansouri','Haddad','Bouzid','Cherif','Belkacem','Amrani','Saidi','Merabet','Khelifi','Hamidi','Boudiaf',
  'Ziani','Larbi','Meziane','Brahimi','Kaci','Slimani','Ouali','Bensaid','Taleb','Rahmani','Djebbar','Laribi','Toumi','Boukhalfa','Messaoud','Guendouz','Yahiaoui',
  'Benyoucef','Hadjadj','Salhi','Makhlouf','Zerrouki','Bouras','Fellah','Aoun','Chaib','Dahmani','Ferhat','Gacem','Hamel','Idir','Lakhdar','Moussaoui','Nacer',
  'Oukaci','Rezig','Sebti','Tabet','Terki','Belhadj','Bensalem','Abdi','Azzouz','Bekkouche','Chouaib','Derradji']) AS n;
CREATE TEMP TABLE d905_nom_pre ON COMMIT DROP AS
SELECT row_number() OVER () AS idx, n FROM unnest(ARRAY['Karim','Samir','Yacine','Nadia','Amel','Farid','Lynda','Rachid','Sofiane','Meriem','Walid','Salima','Hakim','Nawel',
  'Mourad','Soraya','Lotfi','Amina','Redouane','Fatima','Abdelkader','Khadidja','Mohamed','Aicha','Ahmed','Zohra','Omar','Houria','Ali','Samia','Nabil','Leila',
  'Bilal','Yasmine','Ismail','Imane','Mustapha','Sabrina','Djamel','Wafa','Toufik','Ines','Hichem','Malika','Said']) AS n;

CREATE TEMP TABLE d905_med (medecin_id bigint) ON COMMIT DROP;
WITH g AS (
  SELECT i, f.n AS fam, p.n AS pre,
         (ARRAY['Médecine générale','Cardiologie','Pédiatrie','Endocrinologie-diabétologie','Gynécologie-obstétrique','Pneumologie','Gastro-entérologie',
                'Dermatologie','Neurologie','Psychiatrie','Rhumatologie','Ophtalmologie','ORL','Urologie'])[CASE WHEN i % 3 = 0 THEN 1 ELSE 1 + (i * 5) % 14 END] AS spe,
         (ARRAY['Alger','Blida','Oran','Constantine','Tizi Ouzou','Sétif','Annaba','Batna','Béjaïa','Tlemcen'])[1 + i % 10] AS ville
  FROM generate_series(1, 40) i
  JOIN d905_nom_fam f ON f.idx = 1 + (i * 13) % 58
  JOIN d905_nom_pre p ON p.idx = 1 + (i * 5) % 45
), ins AS (
  INSERT INTO medecin (nom, specialite, code_prestataire, telephone, adresse, email, numero_ordre)
  SELECT 'Dr ' || pre || ' ' || fam, spe, '16' || lpad((i * 7919 % 99999999)::text, 8, '0'), '0770 ' || lpad((100000 + i * 20011 % 899999)::text, 6, '0'),
         (1 + i * 3 % 90) || ' rue des Médecins, ' || ville, lower('dr.' || fam || '.' || i) || '@exemple.dz', 'OM-16-' || lpad((i * 13)::text, 5, '0')
  FROM g RETURNING id
)
INSERT INTO d905_med SELECT id FROM ins;

CREATE TEMP TABLE d905_pat (i int, patient_id bigint, email text, kind text, organisme_id bigint, plafond numeric, solde numeric DEFAULT 0) ON COMMIT DROP;
INSERT INTO d905_pat (i, email, kind, plafond)
SELECT i, lower(p.n || '.' || f.n || '.' || i) || '@exemple.dz',
       CASE WHEN i % 100 < 45 THEN 'cnas' WHEN i % 100 < 62 THEN 'casnos' ELSE 'non' END,
       CASE WHEN i % 5 = 0 THEN (5 + (i * 3) % 26) * 1000 ELSE 0 END
FROM generate_series(1, 200) i
JOIN d905_nom_fam f ON f.idx = 1 + (i * 7) % 58
JOIN d905_nom_pre p ON p.idx = 1 + (i * 11) % 45;
UPDATE d905_pat SET organisme_id = (SELECT id FROM organisme WHERE code = CASE d905_pat.kind WHEN 'cnas' THEN 'CNAS' ELSE 'CASNOS' END) WHERE kind <> 'non';

INSERT INTO patient (nom, prenom, date_naissance, telephone, num_assure, organisme_id, est_chronique, plafond_credit, solde_credit, cree_le, email, cree_par)
SELECT f.n, p.n, date '1945-01-01' + (d.i * 977 % 24000), '0660 ' || substr(lpad((100000 + d.i * 4213 % 899999)::text, 6, '0'), 1, 2) || ' '
       || substr(lpad((100000 + d.i * 4213 % 899999)::text, 6, '0'), 3, 2) || ' ' || substr(lpad((100000 + d.i * 4213 % 899999)::text, 6, '0'), 5, 2),
       CASE d.kind WHEN 'cnas' THEN '905' || lpad(d.i::text, 9, '0') WHEN 'casnos' THEN '906' || lpad(d.i::text, 9, '0') END,
       d.organisme_id, d.i % 7 = 0, d.plafond, 0, now() - ((d.i * 53) % 400 + 30) * interval '1 day', d.email,
       (SELECT id FROM utilisateur WHERE identifiant = 'titulaire')
FROM d905_pat d
JOIN d905_nom_fam f ON f.idx = 1 + (d.i * 7) % 58
JOIN d905_nom_pre p ON p.idx = 1 + (d.i * 11) % 45;
UPDATE d905_pat d SET patient_id = p.id FROM patient p WHERE p.email = d.email;
CREATE INDEX ON d905_pat (patient_id);

CREATE TEMP TABLE d905_four (idx int, fournisseur_id bigint) ON COMMIT DROP;
WITH ins AS (
  INSERT INTO fournisseur (raison_sociale, nif, rc, telephone, adresse, solde, jours_orange, jours_rouge, delai_reprise_jours, email, actif)
  SELECT t.nom, '0001' || lpad((t.ord * 1234567)::text, 11, '0'), '16/00-' || lpad((t.ord * 7654)::text, 7, '0') || 'B' || lpad(t.ord::text, 2, '0'),
         '0550 ' || lpad((t.ord * 11)::text, 2, '0') || ' 02 ' || lpad((t.ord * 7)::text, 2, '0'), (t.ord * 4) || ' zone industrielle, ' || t.ville,
         0, CASE WHEN t.ord IN (1, 3) THEN 180 END, CASE WHEN t.ord = 3 THEN 60 END, CASE WHEN t.ord IN (2, 4) THEN 90 END,
         'commandes' || t.ord || '@exemple.dz', true
  FROM unnest(ARRAY['Atlas Pharma Distribution (fictif)|Alger','Sud Négoce Pharmaceutique (fictif)|Ouargla','Oranie Médic Grossiste (fictif)|Oran',
                    'Centre Pharma Gros (fictif)|Blida','Est Distribution Santé (fictif)|Constantine']) WITH ORDINALITY AS u(l, ord),
       LATERAL (SELECT split_part(u.l, '|', 1) AS nom, split_part(u.l, '|', 2) AS ville, u.ord) t
  RETURNING id, raison_sociale
)
INSERT INTO d905_four SELECT (ord - 1)::int, ins.id
FROM ins JOIN unnest(ARRAY['Atlas Pharma Distribution (fictif)','Sud Négoce Pharmaceutique (fictif)','Oranie Médic Grossiste (fictif)',
                           'Centre Pharma Gros (fictif)','Est Distribution Santé (fictif)']) WITH ORDINALITY AS u(nom, ord) ON u.nom = ins.raison_sociale;

-- ---------------------------------------------------------------------
-- 3. Plan des lots : 1 à 4 par produit, selon le profil de stock
--    N normal, R rupture, P périmé à retirer, X proche de la péremption, S surstock
-- ---------------------------------------------------------------------
CREATE TEMP TABLE d905_lotplan (lot_idx serial, produit_id bigint, fournisseur_idx int, arrivee date, qty int, perem_cible date, pa numeric,
                                remise numeric, numero_lot text, lot_id bigint) ON COMMIT DROP;
DO $plan$
DECLARE
    p record;
    v_totw numeric;
    v_e int; v_nl int; v_total int; v_q int; v_j int; v_arr int; v_perem date; v_ago int; v_fi int; v_slot int; v_tva numeric;
BEGIN
    SELECT sum(poids) INTO v_totw FROM d905_prod;
    FOR p IN SELECT * FROM d905_prod ORDER BY rang LOOP
        v_e := greatest(8, round(8000.0 * p.poids / v_totw))::int;
        UPDATE d905_prod SET e = v_e WHERE produit_id = p.produit_id;
        v_nl := CASE WHEN p.motif = 'P' THEN 2 + (random() < 0.4)::int
                     ELSE 1 + (random() < 0.7)::int + (random() < 0.4)::int + (random() < 0.15)::int END;
        IF p.motif = 'R' THEN v_nl := least(v_nl, 2); END IF;
        v_total := CASE p.motif
                     WHEN 'R' THEN ceil(v_e * (0.25 + random() * 0.35))
                     WHEN 'S' THEN ceil(v_e * (3 + random() * 2)) + 40
                     WHEN 'X' THEN ceil(v_e * (0.9 + random() * 0.5)) + 12
                     ELSE ceil(v_e * (1.0 + random() * 0.6)) + 6 END;
        v_arr := 98 + (random() * 20)::int;
        FOR v_j IN 1..v_nl LOOP
            IF v_j > 1 THEN v_arr := greatest(v_arr - (14 + (random() * 28)::int), 3); END IF;
            v_q := greatest(3, ceil(v_total::numeric / v_nl * (0.7 + random() * 0.6))::int);
            v_perem := current_date + (150 + random() * 750)::int;
            IF p.motif = 'X' AND v_j = 1 THEN
                v_perem := current_date + (18 + random() * 95)::int;
                v_q := ceil(v_e * 1.8)::int + 30 + (random() * 30)::int;
            ELSIF p.motif = 'P' AND v_j = 1 THEN
                v_ago := 3 + (random() * 55)::int;
                v_perem := current_date - v_ago;
                v_q := ceil(v_e * (90 - v_ago) / 90.0)::int + 10 + (random() * 20)::int;
            END IF;
            v_fi := floor(random() * 5)::int;
            v_slot := 4 + v_fi + 7 * round((v_arr - 4 - v_fi) / 7.0)::int;
            IF v_slot < 3 THEN v_slot := 4 + v_fi; END IF;
            INSERT INTO d905_lotplan (produit_id, fournisseur_idx, arrivee, qty, perem_cible, pa, remise, numero_lot)
            VALUES (p.produit_id, v_fi, current_date - v_slot, v_q, v_perem,
                    round((p.prix * (0.66 + random() * 0.10) / (1 + p.tva / 100) * (0.97 + random() * 0.06))::numeric, 2),
                    (ARRAY[0, 0, 2, 3, 5])[1 + floor(random() * 5)::int],
                    'D' || p.rang || chr(64 + v_j));
        END LOOP;
    END LOOP;
END
$plan$;

-- Suivi des lots pendant la simulation
CREATE TEMP TABLE d905_lot (lot_id bigint PRIMARY KEY, produit_id bigint, arrivee date, perem_cible date, restant int) ON COMMIT DROP;
CREATE INDEX ON d905_lot (produit_id);
-- Règlements à passer (un par facture, à la date prévue)
CREATE TEMP TABLE d905_pay (id serial, pay_date date, fournisseur_id bigint, reception_id bigint, montant numeric, moyen text, reference text,
                            echeance date, avec_banque boolean, effet_paye_le date, tiroir numeric, numero_bl text) ON COMMIT DROP;

-- ---------------------------------------------------------------------
-- 4. Simulation jour par jour : achats, règlements, ventes, caisses
-- ---------------------------------------------------------------------
DO $sim$
#variable_conflict use_column
DECLARE
    v_today date := current_date;
    v_now timestamptz := now();
    u_titu bigint; u_assi bigint; u_vend bigint;
    v_banque bigint; v_banque_nom text; v_coffre bigint;
    d int; v_date date; sess int;
    v_open timestamptz; v_close timestamptz; v_sess_id bigint; v_sess_user bigint; v_fond numeric; v_releve bigint;
    v_prev_compte numeric := 5000; v_prev_sess bigint;
    v_dow numeric; v_n int; v_times timestamptz[]; v_t timestamptz;
    -- achats
    rr record; l record; p record; pr record;
    v_ts timestamptz; v_fid bigint; v_cmd bigint; v_rec bigint; v_lot bigint; v_lc bigint; v_recseq int := 0; v_refseq int := 0; v_bl text; v_fa text;
    v_ht numeric; v_rem numeric; v_tva numeric; v_shp numeric; v_ttc numeric; v_net numeric; v_u_rec bigint; v_solde numeric;
    v_age int; v_r1 numeric; v_r2 numeric; v_kind text; v_paydate date; v_moy text; v_mont numeric; v_ech date; v_banque_ok boolean; v_effet date; v_tiroir numeric; v_ref text;
    v_reg bigint; v_numreg text;
    -- ventes
    v_tp boolean; v_pat bigint; v_org bigint; v_taux numeric; v_ord bigint; v_med bigint; v_user bigint; v_nl int; v_rem_pct numeric; v_only_remb boolean;
    a_prod bigint[]; a_lot bigint[]; a_q int[]; a_raison text[]; a_lg bigint[]; a_net numeric[];
    v_prod bigint; v_lotid bigint; v_rest int; v_q int; v_raison text; v_need_ord boolean; v_has_list boolean; v_with_ord boolean;
    v_numero text; v_vente bigint; v_pu numeric; v_base numeric; v_taux_l numeric; v_org_l numeric; v_pp numeric; v_red numeric; v_is_med boolean; v_lg bigint;
    v_brut numeric; v_orgt numeric; v_remise numeric; v_don numeric; v_total numeric; v_ppat numeric; v_mode text; v_r numeric;
    v_plaf numeric; v_psolde numeric; v_ordseq int := 0; i int; k int; v_annulee boolean;
    v_ret_montant numeric; v_ret_id bigint; v_dest text;
    -- caisses
    v_net_esp numeric; v_att numeric; v_ecart numeric; v_compte numeric; v_vers numeric; v_rem_c numeric;
    c2000 int; c1000 int; c500 int; c200 int; p100 int; v_p bigint; v_s numeric; v_m numeric; v_t2 timestamptz; v_rc text;
    v_tir_session bigint;
BEGIN
    SELECT id INTO u_titu FROM utilisateur WHERE identifiant = 'titulaire';
    SELECT id INTO u_assi FROM utilisateur WHERE identifiant = 'assistant';
    SELECT id INTO u_vend FROM utilisateur WHERE identifiant = 'vendeur';
    SELECT id, nom INTO v_banque, v_banque_nom FROM compte_tresorerie WHERE type = 'banque' AND actif ORDER BY recoit_carte DESC, id LIMIT 1;
    SELECT id INTO v_coffre FROM compte_tresorerie WHERE type = 'coffre' ORDER BY id LIMIT 1;

    FOR d IN -118..0 LOOP
        v_date := v_today + d;
        v_sess_id := NULL;
        v_prev_sess := NULL;
        v_prev_compte := 5000;

        FOR sess IN 1..2 LOOP
            v_sess_id := NULL;
            v_open := (v_date + CASE sess WHEN 1 THEN time '08:00' ELSE time '14:00' END)::timestamptz;
            v_close := least((v_date + CASE sess WHEN 1 THEN time '14:00' ELSE time '20:00' END)::timestamptz, v_now);
            IF d >= -90 AND v_close - v_open >= interval '15 minutes' THEN
                v_sess_user := CASE sess WHEN 1 THEN u_assi ELSE u_vend END;
                v_fond := CASE WHEN sess = 2 AND v_prev_sess IS NOT NULL THEN v_prev_compte ELSE 5000 END;
                v_releve := CASE WHEN sess = 2 THEN v_prev_sess END;
                INSERT INTO session_caisse (utilisateur_id, poste, ouverte_le, fond_caisse, releve_de)
                VALUES (v_sess_user, 'Caisse démo', v_open, v_fond, v_releve) RETURNING id INTO v_sess_id;
            END IF;

            -- ===== Achats du jour (une fois par jour, avec la caisse du matin ouverte s'il y en a une)
            IF sess = 1 THEN
                v_recseq := v_recseq;  -- compteur de réceptions conservé d'un jour à l'autre
                FOR rr IN SELECT fournisseur_idx AS fidx FROM d905_lotplan WHERE arrivee = v_date GROUP BY fournisseur_idx ORDER BY 1 LOOP
                    v_recseq := v_recseq + 1;
                    SELECT fournisseur_id INTO v_fid FROM d905_four WHERE idx = rr.fidx;
                    v_ts := least((v_date + time '09:30')::timestamptz + rr.fidx * interval '20 minutes', v_now);
                    v_u_rec := CASE WHEN v_recseq % 3 = 0 THEN u_titu ELSE u_assi END;
                    SELECT sum(round(x.pa * x.qty, 2)),
                           sum(round(x.pa * x.qty, 2) - round(round(x.pa * (1 - x.remise / 100), 2) * x.qty, 2)),
                           sum(round(round(round(x.pa * (1 - x.remise / 100), 2) * x.qty, 2) * pp.tva / 100, 2)),
                           sum(x.qty * pp.shp)
                      INTO v_ht, v_rem, v_tva, v_shp
                    FROM d905_lotplan x JOIN d905_prod pp ON pp.produit_id = x.produit_id
                    WHERE x.arrivee = v_date AND x.fournisseur_idx = rr.fidx;
                    v_ttc := v_ht - v_rem + v_tva;
                    v_bl := 'BL-' || (rr.fidx + 1) || '-' || lpad(v_recseq::text, 4, '0');
                    v_fa := CASE WHEN random() < 0.88 THEN 'FA-' || (rr.fidx + 1) || '-' || to_char(v_date, 'YYMM') || '-' || lpad(v_recseq::text, 4, '0') END;

                    v_cmd := NULL;
                    IF random() < 0.45 THEN
                        INSERT INTO commande_fournisseur (fournisseur_id, numero, date_commande, statut, montant_total, utilisateur_id)
                        VALUES (v_fid, prochain_numero_commande(), v_ts - interval '2 days', 'recue', v_ht, u_titu) RETURNING id INTO v_cmd;
                        INSERT INTO ligne_commande (commande_id, produit_id, qte_commandee, qte_recue, prix_achat_ht)
                        SELECT v_cmd, produit_id, sum(qty), sum(qty), round(avg(pa), 2)
                        FROM d905_lotplan WHERE arrivee = v_date AND fournisseur_idx = rr.fidx GROUP BY produit_id;
                    END IF;

                    INSERT INTO reception (fournisseur_id, commande_id, numero_bl, numero_facture, date_reception, utilisateur_id, montant_ttc, montant_ht,
                                           remise, montant_tva, timbre, shp_total)
                    VALUES (v_fid, v_cmd, v_bl, v_fa, v_ts, v_u_rec, v_ttc, v_ht, v_rem, v_tva, 0, v_shp) RETURNING id INTO v_rec;

                    FOR l IN SELECT x.*, pp.prix, pp.shp, pp.tva FROM d905_lotplan x JOIN d905_prod pp ON pp.produit_id = x.produit_id
                             WHERE x.arrivee = v_date AND x.fournisseur_idx = rr.fidx ORDER BY x.lot_idx LOOP
                        v_net := round(l.pa * (1 - l.remise / 100), 2);
                        v_lc := NULL;
                        IF v_cmd IS NOT NULL THEN SELECT id INTO v_lc FROM ligne_commande WHERE commande_id = v_cmd AND produit_id = l.produit_id; END IF;
                        INSERT INTO lot (produit_id, ligne_commande_id, numero_lot, date_peremption, quantite_disponible, prix_achat_ht, reception_id, fournisseur_id)
                        VALUES (l.produit_id, v_lc, l.numero_lot, CASE WHEN l.perem_cible > v_today THEN l.perem_cible ELSE v_today + 30 END, l.qty, v_net, v_rec, v_fid)
                        RETURNING id INTO v_lot;
                        UPDATE d905_lotplan SET lot_id = v_lot WHERE lot_idx = l.lot_idx;
                        INSERT INTO d905_lot VALUES (v_lot, l.produit_id, l.arrivee, l.perem_cible, l.qty);
                        INSERT INTO mouvement_stock (lot_id, type, quantite, reference, utilisateur_id, cree_le, reception_id, prix_achat_ht)
                        VALUES (v_lot, 'entree', l.qty, 'BL ' || v_bl, v_u_rec, v_ts, v_rec, v_net);
                        INSERT INTO reception_ligne (reception_id, lot_id, produit_id, quantite, prix_achat_ht, remise_pct, prix_net_ht, ppa, shp, taux_tva, ug, ug_cout, prix_catalogue_remplace)
                        VALUES (v_rec, v_lot, l.produit_id, l.qty, l.pa, l.remise, v_net, l.prix, l.shp, l.tva, 0, 'zero', false);
                    END LOOP;

                    UPDATE fournisseur SET solde = solde + v_ttc WHERE id = v_fid RETURNING solde INTO v_solde;
                    INSERT INTO fournisseur_mouvement (fournisseur_id, type, montant, solde_apres, utilisateur_id, reference, cree_le)
                    VALUES (v_fid, 'reception', v_ttc, v_solde, v_u_rec, 'Réception BL ' || v_bl || COALESCE(', facture ' || v_fa, ''), v_ts);

                    -- Règlement prévu pour cette facture, selon son âge
                    v_age := v_today - v_date; v_r1 := random(); v_kind := NULL; v_paydate := NULL; v_tiroir := 0; v_ech := NULL; v_effet := NULL; v_banque_ok := false;
                    IF v_age > 70 THEN v_kind := CASE WHEN v_r1 < 0.65 THEN 'full' WHEN v_r1 < 0.77 THEN 'partial' WHEN v_r1 < 0.90 THEN 'traite' END;
                    ELSIF v_age > 40 THEN v_kind := CASE WHEN v_r1 < 0.45 THEN 'full' WHEN v_r1 < 0.70 THEN 'traite' END;
                    ELSIF v_age > 12 THEN v_kind := CASE WHEN v_r1 < 0.20 THEN 'traite' END;
                    END IF;
                    IF v_kind IN ('full', 'partial') THEN
                        v_paydate := v_date + 12 + (random() * 26)::int;
                        v_r2 := random();
                        v_moy := CASE WHEN v_r2 < 0.12 THEN 'especes' WHEN v_r2 < 0.50 THEN 'cheque' WHEN v_r2 < 0.95 THEN 'virement' ELSE 'prelevement' END;
                        v_mont := CASE WHEN v_kind = 'partial' THEN round(v_ttc * 0.5, 2) ELSE v_ttc END;
                        IF v_moy = 'especes' AND v_paydate BETWEEN v_today - 85 AND v_today AND random() < 0.4 THEN
                            v_mont := least(v_ttc, (3 + floor(random() * 10)) * 1000);
                            v_tiroir := v_mont;
                        END IF;
                        v_banque_ok := v_moy <> 'especes';
                    ELSIF v_kind = 'traite' THEN
                        v_paydate := v_date + 5 + (random() * 20)::int;
                        v_ech := v_paydate + (ARRAY[30, 60, 90])[1 + floor(random() * 3)::int];
                        v_moy := 'effet'; v_mont := v_ttc; v_banque_ok := random() < 0.5;
                        IF NOT v_banque_ok AND v_ech + 3 <= v_today AND random() < 0.65 THEN v_effet := v_ech + (random() * 3)::int; END IF;
                    END IF;
                    IF v_paydate IS NOT NULL AND v_paydate <= v_today AND v_mont > 0 THEN
                        v_refseq := v_refseq + 1;
                        v_ref := CASE v_moy WHEN 'cheque' THEN 'CHQ-' || lpad((810000 + v_refseq)::text, 7, '0') WHEN 'virement' THEN 'VIR-905-' || lpad(v_refseq::text, 5, '0')
                                            WHEN 'prelevement' THEN 'PRL-905-' || lpad(v_refseq::text, 5, '0') WHEN 'effet' THEN 'TR-905-' || lpad(v_refseq::text, 5, '0') END;
                        INSERT INTO d905_pay (pay_date, fournisseur_id, reception_id, montant, moyen, reference, echeance, avec_banque, effet_paye_le, tiroir, numero_bl)
                        VALUES (v_paydate, v_fid, v_rec, v_mont, v_moy, v_ref, v_ech, v_banque_ok, v_effet, v_tiroir, v_bl);
                    END IF;
                END LOOP;

                -- Règlements du jour
                FOR p IN SELECT * FROM d905_pay WHERE pay_date = v_date ORDER BY id LOOP
                    v_ts := least((v_date + time '15:00')::timestamptz, v_now);
                    v_tir_session := CASE WHEN p.tiroir > 0 THEN v_sess_id END;
                    v_numreg := 'RG-' || lpad(nextval('seq_numero_reglement_fournisseur')::text, 6, '0');
                    INSERT INTO reglement_fournisseur (fournisseur_id, date_reglement, montant, moyen, reference, banque, echeance, part_tiroir,
                                                       session_caisse_id, utilisateur_id, commentaire, cree_le, compte_id, numero)
                    VALUES (p.fournisseur_id, v_date, p.montant, p.moyen, p.reference, CASE WHEN p.avec_banque THEN v_banque_nom END, p.echeance,
                            CASE WHEN v_tir_session IS NOT NULL THEN p.tiroir ELSE 0 END, v_tir_session,
                            CASE WHEN p.moyen = 'effet' THEN u_titu ELSE u_assi END, 'Facture BL ' || p.numero_bl, v_ts,
                            CASE WHEN p.avec_banque THEN v_banque END, v_numreg)
                    RETURNING id INTO v_reg;
                    INSERT INTO reglement_facture (reglement_id, reception_id, montant) VALUES (v_reg, p.reception_id, p.montant);
                    UPDATE fournisseur SET solde = solde - p.montant WHERE id = p.fournisseur_id RETURNING solde INTO v_solde;
                    INSERT INTO fournisseur_mouvement (fournisseur_id, type, montant, solde_apres, utilisateur_id, reference, cree_le)
                    VALUES (p.fournisseur_id, 'reglement', -p.montant, v_solde, CASE WHEN p.moyen = 'effet' THEN u_titu ELSE u_assi END,
                            'Règlement ' || v_numreg || ' (' || CASE p.moyen WHEN 'especes' THEN 'Espèces' WHEN 'cheque' THEN 'Chèque' WHEN 'virement' THEN 'Virement'
                                WHEN 'effet' THEN 'Traite' ELSE 'Prélèvement' END || COALESCE(' ' || p.reference, '') || ')', v_ts);
                    IF p.effet_paye_le IS NOT NULL THEN
                        INSERT INTO paiement_effet (reglement_id, date_paiement, moyen, compte_id, reference, part_tiroir, utilisateur_id, cree_le)
                        VALUES (v_reg, p.effet_paye_le, 'virement', v_banque, 'VIR-ECH-' || lpad(p.id::text, 5, '0'), 0, u_titu,
                                least((p.effet_paye_le + time '10:00')::timestamptz, v_now));
                    END IF;
                END LOOP;
            END IF;

            -- ===== Ventes de la session
            IF v_sess_id IS NOT NULL THEN
                v_dow := CASE extract(isodow FROM v_date)::int WHEN 5 THEN 0.55 WHEN 7 THEN 1.1 ELSE 1.0 END;
                v_n := round(34 * v_dow * CASE sess WHEN 1 THEN 0.45 ELSE 0.55 END * (0.8 + random() * 0.4)
                             * least(extract(epoch FROM v_close - v_open) / 21600.0, 1.0))::int;
                SELECT array_agg(t ORDER BY t) INTO v_times FROM (
                    SELECT v_open + interval '1 minute' + random() * (v_close - v_open - interval '3 minutes') AS t FROM generate_series(1, greatest(v_n, 0))) s;

                FOREACH v_t IN ARRAY coalesce(v_times, '{}'::timestamptz[]) LOOP
                    v_tp := false; v_pat := NULL; v_org := NULL; v_taux := NULL; v_ord := NULL; v_med := NULL;
                    v_r := random();
                    IF v_r < 0.34 THEN
                        SELECT patient_id, organisme_id INTO v_pat, v_org FROM d905_pat WHERE organisme_id IS NOT NULL ORDER BY random() LIMIT 1;
                        v_tp := true; v_taux := CASE WHEN random() < 0.7 THEN 80 ELSE 100 END;
                    ELSIF v_r < 0.46 THEN
                        SELECT patient_id INTO v_pat FROM d905_pat ORDER BY random() LIMIT 1;
                    ELSIF v_r < 0.56 THEN
                        SELECT patient_id INTO v_pat FROM d905_pat WHERE plafond > 0 ORDER BY random() LIMIT 1;
                    END IF;
                    v_user := (ARRAY[v_sess_user, v_sess_user, v_sess_user, u_titu, u_vend])[1 + floor(random() * 5)::int];

                    -- Lignes : produits choisis selon la demande, premier périmé premier sorti
                    v_nl := 1 + (random() < 0.55)::int + (random() < 0.22)::int + (random() < 0.07)::int;
                    v_only_remb := v_tp AND random() < 0.85;
                    a_prod := '{}'; a_lot := '{}'; a_q := '{}'; a_raison := '{}'; a_lg := '{}'; a_net := '{}';
                    FOR i IN 1..v_nl LOOP
                        v_prod := NULL;
                        SELECT pp.produit_id INTO v_prod FROM d905_prod pp
                        WHERE NOT (pp.produit_id = ANY (a_prod)) AND (NOT v_only_remb OR pp.remb)
                          AND EXISTS (SELECT 1 FROM d905_lot l WHERE l.produit_id = pp.produit_id AND l.arrivee < v_date AND l.perem_cible > v_date AND l.restant > 0)
                        ORDER BY -ln(1.0 - random()) / pp.poids LIMIT 1;
                        CONTINUE WHEN v_prod IS NULL;
                        SELECT l.lot_id, l.restant INTO v_lotid, v_rest FROM d905_lot l
                        WHERE l.produit_id = v_prod AND l.arrivee < v_date AND l.perem_cible > v_date AND l.restant > 0
                        ORDER BY l.perem_cible, l.lot_id LIMIT 1;
                        v_q := least(v_rest, 1 + (random() < 0.30)::int + (random() < 0.10)::int + (random() < 0.04)::int * 2);
                        v_raison := NULL;
                        IF random() < 0.03 THEN
                            SELECT l.lot_id, l.restant INTO v_lotid, v_rest FROM d905_lot l
                            WHERE l.produit_id = v_prod AND l.arrivee < v_date AND l.perem_cible > v_date AND l.restant > 0
                            ORDER BY l.perem_cible, l.lot_id OFFSET 1 LIMIT 1;
                            IF FOUND THEN v_raison := 'demande_client'; v_q := least(v_q, v_rest);
                            ELSE
                                SELECT l.lot_id, l.restant INTO v_lotid, v_rest FROM d905_lot l
                                WHERE l.produit_id = v_prod AND l.arrivee < v_date AND l.perem_cible > v_date AND l.restant > 0 ORDER BY l.perem_cible, l.lot_id LIMIT 1;
                            END IF;
                        END IF;
                        a_prod := a_prod || v_prod; a_lot := a_lot || v_lotid; a_q := a_q || v_q; a_raison := a_raison || v_raison;
                    END LOOP;
                    CONTINUE WHEN coalesce(array_length(a_prod, 1), 0) = 0;

                    SELECT coalesce(bool_or(classe IN ('psychotrope', 'stupefiant')), false), coalesce(bool_or(classe IN ('liste_i', 'liste_ii')), false)
                      INTO v_need_ord, v_has_list FROM d905_prod WHERE produit_id = ANY (a_prod);
                    v_with_ord := v_tp OR v_need_ord OR (v_has_list AND random() < 0.5);
                    IF v_with_ord AND v_pat IS NULL THEN
                        SELECT patient_id INTO v_pat FROM d905_pat ORDER BY random() LIMIT 1;
                    END IF;
                    IF v_with_ord THEN
                        v_ordseq := v_ordseq + 1;
                        SELECT medecin_id INTO v_med FROM d905_med ORDER BY random() LIMIT 1;
                        INSERT INTO ordonnance (patient_id, medecin_id, date_prescription, numero, cree_le)
                        VALUES (v_pat, v_med, v_t::date - (random() * 4)::int, 'ORD-905-' || lpad(v_ordseq::text, 6, '0'), v_t) RETURNING id INTO v_ord;
                    END IF;
                    v_rem_pct := CASE WHEN random() < 0.12 THEN (ARRAY[5, 10, 10, 15, 20])[1 + floor(random() * 5)::int] ELSE 0 END;

                    SELECT prochain_numero_vente() INTO v_numero;
                    INSERT INTO vente (numero, date_vente, utilisateur_id, patient_id, ordonnance_id, type, organisme_id, statut, session_caisse_id,
                                       remise_pct, taux_tiers_payant, remise_additionnee)
                    VALUES (v_numero, v_t, v_user, v_pat, v_ord, CASE WHEN v_tp THEN 'tiers_payant' ELSE 'libre' END, v_org, 'en_cours', v_sess_id,
                            v_rem_pct, v_taux, false) RETURNING id INTO v_vente;

                    v_brut := 0; v_orgt := 0; v_remise := 0; v_don := 0;
                    FOR i IN 1..array_length(a_prod, 1) LOOP
                        SELECT * INTO pr FROM d905_prod WHERE produit_id = a_prod[i];
                        v_pu := pr.prix + pr.shp;
                        v_base := 0; v_taux_l := 0; v_org_l := 0;
                        IF v_tp AND pr.remb AND pr.tarif IS NOT NULL THEN
                            v_base := pr.tarif; v_taux_l := v_taux;
                            v_org_l := round(least(pr.tarif, v_pu) * a_q[i] * v_taux / 100, 2);
                        END IF;
                        v_pp := v_pu * a_q[i] - v_org_l;
                        v_red := round(greatest(v_pp - least(pr.shp * a_q[i], v_pp), 0) * v_rem_pct / 100, 2);
                        v_is_med := pr.categorie = 'medicament';
                        INSERT INTO ligne_vente (vente_id, produit_id, lot_id, quantite, prix_unitaire, base_remboursement, taux_remboursement, montant_organisme,
                                                 montant_patient, shp, raison_lot, remise_auto_pct, reduction_auto, reduction_manuelle, don_partiel)
                        VALUES (v_vente, a_prod[i], a_lot[i], a_q[i], v_pu, v_base, v_taux_l, v_org_l, v_pp, pr.shp, a_raison[i], 0, 0, v_red, v_is_med AND v_red > 0)
                        RETURNING id INTO v_lg;
                        UPDATE lot SET quantite_disponible = quantite_disponible - a_q[i] WHERE id = a_lot[i] RETURNING quantite_disponible INTO v_rest;
                        UPDATE d905_lot SET restant = v_rest WHERE lot_id = a_lot[i];
                        INSERT INTO mouvement_stock (lot_id, type, quantite, reference, utilisateur_id, cree_le)
                        VALUES (a_lot[i], 'vente', -a_q[i], v_numero, v_user, v_t);
                        IF pr.classe IN ('psychotrope', 'stupefiant') THEN
                            INSERT INTO registre_psychotrope (lot_id, ligne_vente_id, type_mouvement, quantite, solde_apres, patient_id, medecin_id, ordonnance_id, utilisateur_id, cree_le)
                            VALUES (a_lot[i], v_lg, 'sortie', a_q[i], v_rest, v_pat, v_med, v_ord, v_user, v_t);
                        END IF;
                        v_brut := v_brut + v_pu * a_q[i]; v_orgt := v_orgt + v_org_l;
                        IF v_is_med THEN v_don := v_don + v_red; ELSE v_remise := v_remise + v_red; END IF;
                        a_lg := a_lg || v_lg; a_net := a_net || (v_pp - v_red);
                    END LOOP;

                    v_total := v_brut - v_remise - v_don;
                    v_ppat := v_brut - v_orgt - v_remise - v_don;
                    UPDATE vente SET montant_total = v_total, part_organisme = v_orgt, part_patient = v_ppat, montant_remise = v_remise, montant_don = v_don,
                                     statut = 'validee' WHERE id = v_vente;

                    -- Paiement de la part du patient
                    v_mode := NULL;
                    IF v_ppat > 0 THEN
                        v_r := random();
                        v_mode := CASE WHEN v_r < 0.70 THEN 'especes' WHEN v_r < 0.88 THEN 'carte' WHEN v_r < 0.93 THEN 'cheque' ELSE 'credit' END;
                        IF v_pat IS NOT NULL AND random() < 0.30 THEN v_mode := 'credit'; END IF;
                        IF v_mode = 'credit' THEN
                            SELECT plafond, solde INTO v_plaf, v_psolde FROM d905_pat WHERE patient_id = v_pat;
                            IF v_pat IS NULL OR coalesce(v_plaf, 0) <= 0 OR v_psolde + v_ppat > v_plaf THEN v_mode := 'especes'; END IF;
                        END IF;
                        INSERT INTO paiement (vente_id, patient_id, mode, montant, utilisateur_id, cree_le, session_caisse_id)
                        VALUES (v_vente, v_pat, v_mode, v_ppat, v_user, v_t, v_sess_id);
                        IF v_mode = 'credit' THEN
                            UPDATE patient SET solde_credit = solde_credit + v_ppat WHERE id = v_pat RETURNING solde_credit INTO v_solde;
                            INSERT INTO credit_mouvement (patient_id, type, montant, solde_apres, vente_id, utilisateur_id, poste, reference, cree_le)
                            VALUES (v_pat, 'vente_credit', v_ppat, v_solde, v_vente, v_user, 'Caisse démo', 'Vente ' || v_numero, v_t);
                            UPDATE d905_pat SET solde = v_solde WHERE patient_id = v_pat;
                        END IF;
                    END IF;

                    -- Annulation (quelques ventes, hors psychotropes et crédit)
                    v_annulee := false;
                    IF NOT v_need_ord AND v_mode IS DISTINCT FROM 'credit' AND random() < 0.013 THEN
                        v_annulee := true;
                        UPDATE vente SET statut = 'annulee' WHERE id = v_vente;
                        FOR i IN 1..array_length(a_prod, 1) LOOP
                            UPDATE lot SET quantite_disponible = quantite_disponible + a_q[i] WHERE id = a_lot[i] RETURNING quantite_disponible INTO v_rest;
                            UPDATE d905_lot SET restant = v_rest WHERE lot_id = a_lot[i];
                            INSERT INTO mouvement_stock (lot_id, type, quantite, reference, utilisateur_id, cree_le)
                            VALUES (a_lot[i], 'retour_client', a_q[i], 'Annulation ' || v_numero, v_user, least(v_t + interval '6 minutes', v_close));
                        END LOOP;
                        INSERT INTO annulation_vente (vente_id, motif, utilisateur_id, valide_par, ancienne, mode, montant_rembourse, session_caisse_id, poste, cree_le)
                        VALUES (v_vente, (ARRAY['Erreur de saisie du caissier', 'Le client change d''avis', 'Mauvais produit scanné', 'Ordonnance refusée'])[1 + floor(random() * 4)::int],
                                v_user, NULL, false, v_mode, v_ppat, v_sess_id, 'Caisse démo', least(v_t + interval '6 minutes', v_close));
                    END IF;

                    -- Retour d'une boîte (quelques ventes, hors psychotropes et crédit)
                    IF NOT v_annulee AND NOT v_need_ord AND v_mode IN ('especes', 'carte', 'cheque') AND a_net[1] > 0 AND random() < 0.012
                       AND v_t + interval '40 minutes' < v_close THEN
                        v_ret_montant := round(a_net[1] / a_q[1], 2);
                        v_dest := CASE WHEN random() < 0.3 THEN 'destruction' ELSE 'stock' END;
                        INSERT INTO retour_client (vente_id, motif, utilisateur_id, valide_par, mode, montant_rembourse, session_caisse_id, poste, cree_le)
                        VALUES (v_vente, (ARRAY['Boîte non utilisée', 'Produit non adapté', 'Erreur de délivrance'])[1 + floor(random() * 3)::int],
                                v_user, u_titu, v_mode, v_ret_montant, v_sess_id, 'Caisse démo', v_t + interval '40 minutes') RETURNING id INTO v_ret_id;
                        INSERT INTO retour_client_ligne (retour_id, ligne_vente_id, quantite, destination, montant) VALUES (v_ret_id, a_lg[1], 1, v_dest, v_ret_montant);
                        UPDATE lot SET quantite_disponible = quantite_disponible + 1 WHERE id = a_lot[1];
                        INSERT INTO mouvement_stock (lot_id, type, quantite, reference, utilisateur_id, cree_le)
                        VALUES (a_lot[1], 'retour_client', 1, 'Retour client ' || v_numero, v_user, v_t + interval '40 minutes');
                        IF v_dest = 'destruction' THEN
                            UPDATE lot SET quantite_disponible = quantite_disponible - 1 WHERE id = a_lot[1];
                            INSERT INTO mouvement_stock (lot_id, type, quantite, reference, utilisateur_id, cree_le)
                            VALUES (a_lot[1], 'destruction', -1, 'Retour client ' || v_numero || ' : à détruire', v_user, v_t + interval '40 minutes');
                        ELSE
                            UPDATE d905_lot SET restant = restant + 1 WHERE lot_id = a_lot[1];
                        END IF;
                    END IF;
                END LOOP;

                -- Remboursements de dette au comptoir (espèces), en fin de session
                FOR k IN 1..((random() < 0.45)::int + (random() < 0.10)::int) LOOP
                    SELECT patient_id, solde INTO v_p, v_s FROM d905_pat WHERE solde > 0 ORDER BY random() LIMIT 1;
                    CONTINUE WHEN v_p IS NULL;
                    v_m := least(v_s, greatest(500, round(v_s * (0.3 + random() * 0.7) / 100) * 100));
                    v_t2 := v_close - interval '80 seconds' + k * interval '10 seconds';
                    INSERT INTO paiement (patient_id, mode, montant, utilisateur_id, cree_le, session_caisse_id, numero_recu)
                    VALUES (v_p, 'especes', v_m, v_sess_user, v_t2, v_sess_id, 'RC-' || lpad(nextval('seq_numero_recu')::text, 6, '0'))
                    RETURNING numero_recu INTO v_rc;
                    UPDATE patient SET solde_credit = solde_credit - v_m WHERE id = v_p RETURNING solde_credit INTO v_solde;
                    INSERT INTO credit_mouvement (patient_id, type, montant, solde_apres, utilisateur_id, poste, reference, cree_le)
                    VALUES (v_p, 'remboursement', -v_m, v_solde, v_sess_user, 'Caisse démo', 'Remboursement ' || v_rc, v_t2);
                    UPDATE d905_pat SET solde = v_solde WHERE patient_id = v_p;
                END LOOP;

                -- Argent porté au coffre pendant la journée (de temps en temps, caisse du matin)
                SELECT COALESCE((SELECT SUM(montant) FROM paiement WHERE session_caisse_id = v_sess_id AND mode = 'especes'), 0)
                     - COALESCE((SELECT SUM(montant_rembourse) FROM annulation_vente WHERE session_caisse_id = v_sess_id AND mode = 'especes'), 0)
                     - COALESCE((SELECT SUM(montant_rembourse) FROM retour_client WHERE session_caisse_id = v_sess_id AND mode = 'especes'), 0)
                     - COALESCE((SELECT SUM(part_tiroir) FROM reglement_fournisseur WHERE session_caisse_id = v_sess_id), 0)
                     - COALESCE((SELECT SUM(montant) FROM mouvement_tresorerie WHERE session_caisse_id = v_sess_id AND pendant_session), 0)
                  INTO v_net_esp;
                IF sess = 1 AND random() < 0.06 AND v_fond + v_net_esp >= 15000 THEN
                    v_vers := floor((v_fond + v_net_esp - 8000) / 1000) * 1000;
                    INSERT INTO mouvement_tresorerie (compte_id, date_mouvement, sens, montant, nature, libelle, session_caisse_id, pendant_session, automatique, utilisateur_id, cree_le)
                    VALUES (v_coffre, v_date, 'entree', v_vers, 'versement_caisse', 'Argent de la caisse porté au coffre (Caisse démo)', v_sess_id, true, false, v_sess_user,
                            v_close - interval '30 minutes');
                    v_net_esp := v_net_esp - v_vers;
                END IF;

                -- Clôture : attendu, compté, écart plausible
                v_att := v_fond + v_net_esp;
                v_r := random();
                v_ecart := CASE WHEN v_r < 0.62 THEN 0
                                WHEN v_r < 0.74 THEN -50 * (1 + floor(random() * 4))
                                WHEN v_r < 0.84 THEN 50 * (1 + floor(random() * 4))
                                WHEN v_r < 0.92 THEN -(250 + 50 * floor(random() * 12))
                                WHEN v_r < 0.96 THEN 250 + 50 * floor(random() * 8)
                                ELSE -(1000 + 100 * floor(random() * 16)) END;
                v_compte := greatest(v_att + v_ecart, 0);
                v_rem_c := v_compte;
                c2000 := floor(v_rem_c * 0.40 / 2000)::int; v_rem_c := v_rem_c - c2000 * 2000;
                c1000 := floor(v_rem_c * 0.50 / 1000)::int; v_rem_c := v_rem_c - c1000 * 1000;
                c500  := floor(v_rem_c * 0.50 / 500)::int;  v_rem_c := v_rem_c - c500 * 500;
                c200  := floor(v_rem_c * 0.60 / 200)::int;  v_rem_c := v_rem_c - c200 * 200;
                p100  := floor(v_rem_c / 100)::int;         v_rem_c := v_rem_c - p100 * 100;
                INSERT INTO comptage_caisse (session_id, coupure, nombre, montant) VALUES
                    (v_sess_id, 'b2000', c2000, c2000 * 2000), (v_sess_id, 'b1000', c1000, c1000 * 1000), (v_sess_id, 'b500', c500, c500 * 500),
                    (v_sess_id, 'b200', c200, c200 * 200), (v_sess_id, 'p100', p100, p100 * 100), (v_sess_id, 'autres', 0, v_rem_c);
                UPDATE session_caisse SET fermee_le = v_close, ventes_especes = v_net_esp, montant_attendu = v_att, montant_compte = v_compte, ecart = v_compte - v_att
                WHERE id = v_sess_id;

                -- Versement au coffre à la clôture du soir (le tiroir garde 5 000 DA pour le lendemain)
                IF sess = 2 AND v_compte - 5000 >= 1000 THEN
                    v_vers := floor((v_compte - 5000) / 100) * 100;
                    INSERT INTO mouvement_tresorerie (compte_id, date_mouvement, sens, montant, nature, libelle, session_caisse_id, pendant_session, automatique, utilisateur_id, cree_le)
                    VALUES (v_coffre, v_date, 'entree', v_vers, 'versement_caisse', 'Versement de la caisse clôturée (Caisse démo)', v_sess_id, false, false, v_sess_user, v_close);
                END IF;
                v_prev_sess := v_sess_id; v_prev_compte := v_compte;
            END IF;
        END LOOP;

        -- Casse de temps en temps (hors produits sous registre)
        IF d >= -90 AND random() < 0.12 THEN
            SELECT l.lot_id, l.restant INTO v_lotid, v_rest FROM d905_lot l JOIN d905_prod pp ON pp.produit_id = l.produit_id
            WHERE l.restant >= 5 AND l.arrivee < v_date AND pp.classe NOT IN ('psychotrope', 'stupefiant') ORDER BY random() LIMIT 1;
            IF FOUND THEN
                v_q := 1 + (random() < 0.4)::int;
                UPDATE lot SET quantite_disponible = quantite_disponible - v_q WHERE id = v_lotid;
                UPDATE d905_lot SET restant = restant - v_q WHERE lot_id = v_lotid;
                INSERT INTO mouvement_stock (lot_id, type, quantite, reference, utilisateur_id, cree_le)
                VALUES (v_lotid, 'casse', -v_q, 'Casse constatée au rayon', u_assi, least((v_date + time '16:30')::timestamptz, v_now));
            END IF;
        END IF;
    END LOOP;
END
$sim$;

-- ---------------------------------------------------------------------
-- 5. Fin : péremptions réelles, seuils, commandes en cours, trésorerie
-- ---------------------------------------------------------------------
-- Lots périmés à retirer : la date réelle est posée maintenant (la vente d'un lot périmé est refusée par la base)
UPDATE lot SET date_peremption = lp.perem_cible FROM d905_lotplan lp WHERE lot.id = lp.lot_id AND lp.perem_cible <= current_date;
-- Quelques dates de péremption à vérifier
UPDATE lot SET date_a_verifier = true WHERE id IN (SELECT lot_id FROM d905_lotplan WHERE lot_id IS NOT NULL ORDER BY md5(lot_idx::text) LIMIT 6);
-- Seuil d'alerte : environ dix jours de ventes, pour la plupart des produits
UPDATE produit SET stock_minimum = greatest(3, ceil(pp.e / 9.0)::int) FROM d905_prod pp WHERE produit.id = pp.produit_id AND pp.rang % 3 <> 0;

-- Bons de commande en cours (produits en rupture ou presque)
DO $cmd$
DECLARE
    v_k int; v_cmd bigint; v_fid bigint; v_tot numeric;
BEGIN
    FOR v_k IN 0..3 LOOP
        SELECT fournisseur_id INTO v_fid FROM d905_four WHERE idx = v_k;
        INSERT INTO commande_fournisseur (fournisseur_id, numero, date_commande, statut, montant_total, utilisateur_id)
        VALUES (v_fid, prochain_numero_commande(), now() - (ARRAY[1, 3, 6, 12])[v_k + 1] * interval '1 day',
                (ARRAY['brouillon', 'envoyee', 'envoyee', 'annulee'])[v_k + 1], 0, (SELECT id FROM utilisateur WHERE identifiant = 'titulaire'))
        RETURNING id INTO v_cmd;
        INSERT INTO ligne_commande (commande_id, produit_id, qte_commandee, prix_achat_ht)
        SELECT v_cmd, pp.produit_id, greatest(10, pp.e / 2),
               round(pp.prix * 0.72 / (1 + pp.tva / 100), 2)
        FROM d905_prod pp WHERE pp.motif IN ('R', 'N') ORDER BY md5(pp.rang::text || v_k::text) LIMIT 4;
        SELECT sum(qte_commandee * prix_achat_ht) INTO v_tot FROM ligne_commande WHERE commande_id = v_cmd;
        UPDATE commande_fournisseur SET montant_total = coalesce(v_tot, 0) WHERE id = v_cmd;
    END LOOP;
END
$cmd$;

-- Trésorerie : versements des organismes (en banque), dépenses du coffre et de la banque
INSERT INTO mouvement_tresorerie (compte_id, date_mouvement, sens, montant, nature, libelle, reference, organisme_id, utilisateur_id, cree_le)
SELECT (SELECT id FROM compte_tresorerie WHERE type = 'banque' AND actif ORDER BY recoit_carte DESC, id LIMIT 1),
       current_date - t.jours, 'entree', t.montant, 'versement_organisme', 'Versement ' || o.code || ' (jeu de test)',
       'VIR-905-' || o.code || '-' || t.rang, o.id, (SELECT id FROM utilisateur WHERE identifiant = 'titulaire'), (current_date - t.jours + time '11:00')::timestamptz
FROM organisme o
CROSS JOIN LATERAL (
    SELECT s.rang, s.jours, round(coalesce(sum(v.part_organisme), 0) * 0.95, -2) AS montant
    FROM (VALUES (1, 40, 90, 65), (2, 6, 65, 35)) AS s(rang, jours, de, a)
    LEFT JOIN vente v ON v.organisme_id = o.id AND v.statut = 'validee'
         AND v.session_caisse_id IN (SELECT id FROM session_caisse WHERE poste = 'Caisse démo')
         AND v.date_vente >= current_date - s.de AND v.date_vente < current_date - s.a
    GROUP BY s.rang, s.jours
) t
WHERE o.code IN ('CNAS', 'CASNOS') AND t.montant > 0;

INSERT INTO mouvement_tresorerie (compte_id, date_mouvement, sens, montant, nature, categorie, libelle, utilisateur_id, cree_le)
SELECT CASE d.compte WHEN 'banque' THEN (SELECT id FROM compte_tresorerie WHERE type = 'banque' AND actif ORDER BY recoit_carte DESC, id LIMIT 1)
                     ELSE (SELECT id FROM compte_tresorerie WHERE type = 'coffre' ORDER BY id LIMIT 1) END,
       current_date - d.jours, 'sortie', d.montant, 'depense', d.categorie, d.libelle, (SELECT id FROM utilisateur WHERE identifiant = 'titulaire'),
       (current_date - d.jours + time '09:00')::timestamptz
FROM (VALUES (80, 'banque', 90000, 'loyer', 'Loyer de l''officine'), (50, 'banque', 90000, 'loyer', 'Loyer de l''officine'), (20, 'banque', 90000, 'loyer', 'Loyer de l''officine'),
             (75, 'banque', 14500, 'charges', 'Électricité et eau'), (45, 'banque', 16200, 'charges', 'Électricité et eau'), (15, 'banque', 15100, 'charges', 'Électricité et eau'),
             (70, 'coffre', 3500, 'fournitures', 'Fournitures de bureau'), (55, 'coffre', 2000, 'entretien', 'Entretien du local'),
             (33, 'coffre', 4200, 'fournitures', 'Rouleaux de tickets et sacs'), (12, 'coffre', 2500, 'entretien', 'Nettoyage'),
             (5, 'coffre', 6000, 'entretien', 'Réparation du climatiseur')) AS d(jours, compte, montant, categorie, libelle);

-- Relevés tenus par déclencheur : réactivés avant la validation
ALTER TABLE patient ENABLE TRIGGER trg_patient_credit;
ALTER TABLE fournisseur ENABLE TRIGGER trg_fournisseur_dette;

-- Bilan de la reprise de la nomenclature
SELECT (SELECT count(*) FROM nomenclature_produit WHERE version_id = (SELECT max(id) FROM nomenclature_version)) AS lignes_nomenclature,
       (SELECT count(*) FROM d905_prod WHERE categorie = 'medicament') + (SELECT count(*) FROM d905_nouveaux) AS produits_crees_depuis_nomenclature,
       (SELECT count(*) FROM d905_prod WHERE categorie = 'medicament') AS dont_avec_stock,
       (SELECT count(*) FROM d905_rest) - (SELECT count(*) FROM d905_nouveaux) AS ignores_doublon_de_nom,
       (SELECT count(*) FROM d905_prod WHERE categorie <> 'medicament') AS parapharmacie_creee;
COMMIT;
