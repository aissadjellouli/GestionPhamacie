-- =====================================================================
-- Onze nouvelles wilayas : passage de 58 à 69 (2026-11-16)
--   Références : loi n° 26-06 du 4 avril 2026 (JO n° 25 du 5 avril 2026) ;
--   décret présidentiel n° 26-206 du 25 mai 2026 (JO n° 40) fixant les
--   numéros 59 à 69. [À VÉRIFIER : références relevées sur des sources
--   secondaires, à confirmer au Journal officiel]
--   1. wilaya.wilaya_origine (facultatif) : code de la wilaya dont la
--      nouvelle wilaya est issue ; rempli pour 59 à 69 seulement.
--   2. Insertion des wilayas 59 à 69 (les codes 01 à 58 sont inchangés).
--   Aucune contrainte CHECK ne bornait les codes à 58 (ck_wilaya_code : deux
--   chiffres) : rien à élargir. Aucune donnée existante modifiée (médecins,
--   zone de l'officine inchangés). Idempotent. Après 124.
-- =====================================================================

ALTER TABLE wilaya ADD COLUMN IF NOT EXISTS wilaya_origine text;
DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'fk_wilaya_origine') THEN
        ALTER TABLE wilaya ADD CONSTRAINT fk_wilaya_origine FOREIGN KEY (wilaya_origine) REFERENCES wilaya(code);
    END IF;
END;
$$;

INSERT INTO wilaya (code, nom, wilaya_origine) VALUES
    ('59', 'Aflou', '03'), ('60', 'Barika', '05'), ('61', 'El Kantara', '07'), ('62', 'Bir El Ater', '12'),
    ('63', 'El Aricha', '13'), ('64', 'Ksar Chellala', '14'), ('65', 'Aïn Oussera', '17'), ('66', 'Messaad', '17'),
    ('67', 'Ksar El Boukhari', '26'), ('68', 'Bou Saâda', '28'), ('69', 'El Abiodh Sidi Cheikh', '32')
ON CONFLICT (code) DO NOTHING;
