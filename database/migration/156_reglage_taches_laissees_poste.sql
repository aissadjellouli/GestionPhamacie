-- =====================================================================
-- 156 : réglage de l'officine « facture et retour fournisseur laissés au
-- changement d'utilisateur » (décision du titulaire, 2026-11-21)
--   * parametre_caisse.taches_laissees_poste : comment chaque poste garde
--     la facture en préparation et le retour fournisseur laissés au
--     « Changer d'utilisateur » :
--       - 'chiffre'   (défaut) : fichier du poste chiffré AES-256 par le
--                     compte Windows (DPAPI), relu au redémarrage ;
--       - 'desactive' : en mémoire seulement, perdus à la fermeture du
--                     poste ; le fichier du poste est supprimé à la
--                     prochaine connexion sur ce poste.
--     Jamais d'écriture en clair. Réglé par le titulaire seul
--     (Administration › Caisse et contrôle › Règles), inscrit au journal
--     des actions (avant / après).
--   * Table locale à l'officine (groupe 3 : jamais mutualisée) ; la valeur
--     part au serveur central avec la ligne (table miroir, lecture seule).
-- Idempotent ; réversible (voir « Retour arrière » en fin de fichier).
-- =====================================================================

ALTER TABLE parametre_caisse ADD COLUMN IF NOT EXISTS taches_laissees_poste text NOT NULL DEFAULT 'chiffre';

DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_constraint
                   WHERE conname = 'ck_parametre_caisse_taches_laissees_poste' AND conrelid = 'parametre_caisse'::regclass) THEN
        ALTER TABLE parametre_caisse ADD CONSTRAINT ck_parametre_caisse_taches_laissees_poste
            CHECK (taches_laissees_poste IN ('chiffre', 'desactive'));
    END IF;
END $$;

COMMENT ON COLUMN parametre_caisse.taches_laissees_poste IS
    'Facture et retour fournisseur laissés au changement d''utilisateur : chiffre (fichier du poste chiffré AES-256, compte Windows, défaut) ou desactive (mémoire seule) ; titulaire seul (156)';

-- ---- Retour arrière (manuel, à exécuter seulement sur décision) ------------------------------
-- À faire avec le retour à une version du logiciel qui ne lit pas ce réglage.
-- ALTER TABLE parametre_caisse DROP CONSTRAINT IF EXISTS ck_parametre_caisse_taches_laissees_poste;
-- ALTER TABLE parametre_caisse DROP COLUMN IF EXISTS taches_laissees_poste;
