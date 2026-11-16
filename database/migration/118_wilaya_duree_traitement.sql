-- =====================================================================
-- Wilaya du médecin, zone de l'officine, durée du traitement (2026-11-16, suite du lot 8)
--   1. RÉFÉRENTIEL « wilaya » (code sur deux chiffres, nom) : les 48 wilayas
--      historiques et les 10 wilayas de plein exercice issues de la loi
--      n° 19-12 du 11 décembre 2019 (49 à 58). Liste en données : une wilaya
--      ajoutée plus tard s'insère ici sans changer le code.
--   2. medecin.wilaya : code de wilaya du prescripteur (facultatif, NULL
--      accepté ; les médecins existants restent sans wilaya).
--   3. zone_psychotrope_wilaya : wilayas de la zone de l'officine (la wilaya
--      de l'officine, marquée « officine », et des wilayas voisines au
--      choix), réglée dans Administration › Psychotropes. Zone vide : pas
--      de contrôle « hors zone ».
--   4. Règle d'alerte « prescripteur_hors_zone » (wilaya du médecin connue
--      et hors zone) ; regle_alerte_psychotrope.unites_par_jour : unités par
--      jour au-delà desquelles « quantité anormale » alerte quand la durée du
--      traitement et la contenance sont connues [À VÉRIFIER : valeur, vide
--      par défaut = contrôle inactif].
--   5. registre_psychotrope.duree_traitement_jours (facultatif) et
--      produit.contenance (unités par boîte, facultatif).
--   Aucune donnée existante modifiée ni supprimée. Idempotent. Après 117.
-- =====================================================================

-- ---- 1. Référentiel des wilayas ---------------------------------------------
CREATE TABLE IF NOT EXISTS wilaya (
    code  text PRIMARY KEY,
    nom   text NOT NULL,
    CONSTRAINT ck_wilaya_code CHECK (code ~ '^[0-9]{2}$')
);

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

-- ---- 2. Wilaya du médecin -----------------------------------------------------
ALTER TABLE medecin ADD COLUMN IF NOT EXISTS wilaya text;
DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'fk_medecin_wilaya') THEN
        ALTER TABLE medecin ADD CONSTRAINT fk_medecin_wilaya FOREIGN KEY (wilaya) REFERENCES wilaya(code);
    END IF;
END;
$$;
CREATE INDEX IF NOT EXISTS idx_medecin_sans_wilaya ON medecin (nom) WHERE wilaya IS NULL;

-- ---- 3. Zone de l'officine ----------------------------------------------------
CREATE TABLE IF NOT EXISTS zone_psychotrope_wilaya (
    wilaya       text        PRIMARY KEY REFERENCES wilaya(code),
    officine     boolean     NOT NULL DEFAULT false,
    modifie_le   timestamptz NOT NULL DEFAULT now(),
    modifie_par  bigint      REFERENCES utilisateur(id)
);
CREATE UNIQUE INDEX IF NOT EXISTS ux_zone_psychotrope_officine ON zone_psychotrope_wilaya (officine) WHERE officine;

-- ---- 4. Règles d'alerte -------------------------------------------------------
ALTER TABLE regle_alerte_psychotrope ADD COLUMN IF NOT EXISTS unites_par_jour integer;
DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'ck_regle_alerte_psy_unites') THEN
        ALTER TABLE regle_alerte_psychotrope ADD CONSTRAINT ck_regle_alerte_psy_unites CHECK (unites_par_jour IS NULL OR unites_par_jour BETWEEN 1 AND 100);
    END IF;
    -- Liste des codes : ajout de « prescripteur_hors_zone » (contrainte remplacée, mêmes codes plus celui-ci)
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'ck_regle_alerte_psy_code'
                   AND pg_get_constraintdef(oid) LIKE '%prescripteur_hors_zone%') THEN
        ALTER TABLE regle_alerte_psychotrope DROP CONSTRAINT IF EXISTS ck_regle_alerte_psy_code;
        ALTER TABLE regle_alerte_psychotrope ADD CONSTRAINT ck_regle_alerte_psy_code
            CHECK (code IN ('patient_repete', 'ordonnance_reutilisee', 'quantite_anormale', 'prescripteur_inhabituel', 'prescripteur_hors_zone'));
    END IF;
END;
$$;
-- Seuil et période sans objet pour cette règle (valeurs neutres exigées par les contraintes)
INSERT INTO regle_alerte_psychotrope (code, libelle, seuil, periode_jours, ordre) VALUES
    ('prescripteur_hors_zone', 'Prescripteur hors zone (wilaya du médecin hors de la zone de l''officine)', 1, 1, 5)
ON CONFLICT (code) DO NOTHING;

-- ---- 5. Durée du traitement et contenance -------------------------------------
ALTER TABLE registre_psychotrope ADD COLUMN IF NOT EXISTS duree_traitement_jours integer;
ALTER TABLE produit ADD COLUMN IF NOT EXISTS contenance integer;
DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'ck_registre_psy_duree') THEN
        ALTER TABLE registre_psychotrope ADD CONSTRAINT ck_registre_psy_duree
            CHECK (duree_traitement_jours IS NULL OR duree_traitement_jours BETWEEN 1 AND 3650);
    END IF;
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'ck_produit_contenance') THEN
        ALTER TABLE produit ADD CONSTRAINT ck_produit_contenance CHECK (contenance IS NULL OR contenance BETWEEN 1 AND 10000);
    END IF;
END;
$$;
