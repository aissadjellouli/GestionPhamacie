-- =====================================================================
-- Tests du script 150 (index de la recherche pendant la frappe), en transaction ANNULÉE :
--   psql -U pharma_app -d pharmacie_test -v ON_ERROR_STOP=1 -f database/tests/150_index_recherche_suggestions_tests.sql
-- =====================================================================
BEGIN;

-- 1. Index présents, valides, une seule fois
DO $$
BEGIN
    ASSERT (SELECT count(*) FROM pg_indexes WHERE indexname = 'ix_patient_nom_prenom_sans_accent' AND tablename = 'patient'
            AND indexdef LIKE '%USING gin%sans_accent%gin_trgm_ops%') = 1, 'index patient (trigrammes, sans_accent)';
    ASSERT (SELECT count(*) FROM pg_indexes WHERE indexname = 'ix_medecin_nom_sans_accent' AND tablename = 'medecin'
            AND indexdef LIKE '%USING gin%sans_accent%gin_trgm_ops%') = 1, 'index médecin (trigrammes, sans_accent)';
    ASSERT (SELECT count(*) FROM pg_indexes WHERE indexname = 'ix_lot_produit_numero' AND tablename = 'lot'
            AND indexdef LIKE '%USING btree (produit_id, numero_lot)') = 1, 'index lot (produit, numéro)';
    ASSERT NOT EXISTS (SELECT 1 FROM pg_index i JOIN pg_class c ON c.oid = i.indexrelid
                       WHERE c.relname IN ('ix_patient_nom_prenom_sans_accent', 'ix_medecin_nom_sans_accent', 'ix_lot_produit_numero')
                         AND NOT i.indisvalid), 'index valides';
END $$;

-- 2. Recherche sans accents ni majuscules (même expression que PatientsSql) : « eloise » trouve « Éloïse », « BENALI » trouve « Bénali »
INSERT INTO patient (nom, prenom) VALUES ('Bénali', 'Éloise'), ('Zzessai150', NULL);
INSERT INTO medecin (nom, specialite) VALUES ('Dr Hélène Essai150', 'Pédiatrie');
DO $$
BEGIN
    ASSERT (SELECT count(*) FROM patient WHERE sans_accent(nom || ' ' || COALESCE(prenom, '')) ILIKE '%' || sans_accent('eloise') || '%'
            AND nom = 'Bénali') = 1, 'prénom sans accent';
    ASSERT (SELECT count(*) FROM patient WHERE sans_accent(nom || ' ' || COALESCE(prenom, '')) ILIKE '%' || sans_accent('BENALI') || '%'
            AND nom = 'Bénali') = 1, 'nom en majuscules sans accent';
    ASSERT (SELECT count(*) FROM patient WHERE sans_accent(nom || ' ' || COALESCE(prenom, '')) ILIKE '%' || sans_accent('bénali élo') || '%'
            AND nom = 'Bénali') = 1, '« nom prénom »';
    ASSERT (SELECT count(*) FROM patient WHERE sans_accent(nom || ' ' || COALESCE(prenom, '')) ILIKE '%' || sans_accent('zzessai150') || '%') = 1,
        'patient sans prénom';
    ASSERT (SELECT count(*) FROM medecin WHERE sans_accent(nom) ILIKE '%' || sans_accent('helene essai150') || '%') = 1, 'médecin sans accent';
END $$;

-- 3. L'index sert la recherche (lecture complète interdite pour la vérification)
SET LOCAL enable_seqscan = off;
DO $$
DECLARE ligne text; trouve boolean := false;
BEGIN
    FOR ligne IN EXECUTE $q$EXPLAIN SELECT id FROM medecin WHERE sans_accent(nom) ILIKE '%' || sans_accent('ben') || '%'$q$ LOOP
        trouve := trouve OR ligne LIKE '%ix_medecin_nom_sans_accent%';
    END LOOP;
    ASSERT trouve, 'index médecin non utilisé';
END $$;
DO $$
DECLARE ligne text; trouve boolean := false;
BEGIN
    FOR ligne IN EXECUTE $q$EXPLAIN SELECT id FROM patient WHERE sans_accent(nom || ' ' || COALESCE(prenom, '')) ILIKE '%' || sans_accent('ami') || '%'$q$ LOOP
        trouve := trouve OR ligne LIKE '%ix_patient_nom_prenom_sans_accent%';
    END LOOP;
    ASSERT trouve, 'index patient non utilisé';
END $$;
-- Numéros de lot d'un produit proposés au rappel (même requête que LotsRappelablesSql) : l'index (produit, numéro) sert la lecture
DO $$
DECLARE ligne text; trouve boolean := false;
BEGIN
    FOR ligne IN EXECUTE $q$EXPLAIN SELECT l.numero_lot, min(l.date_peremption), COALESCE(sum(l.quantite_disponible), 0)::int, bool_and(l.bloque)
            FROM lot l WHERE l.produit_id = 1 AND btrim(l.numero_lot) <> '' AND sans_accent(l.numero_lot) ILIKE '%' || sans_accent('b1') || '%' ESCAPE '\'
            GROUP BY l.numero_lot ORDER BY 1 LIMIT 10$q$ LOOP
        trouve := trouve OR ligne LIKE '%ix_lot_produit_numero%';
    END LOOP;
    ASSERT trouve, 'index lot (produit, numéro) non utilisé';
END $$;
RESET enable_seqscan;

-- 4. Saisie échappée (\, %, _ pris à la lettre, ESCAPE '\') : « __ » ou « % » ne renvoient pas tout
DO $$
BEGIN
    ASSERT (SELECT count(*) FROM medecin WHERE sans_accent(nom) ILIKE '%' || sans_accent('\_\_') || '%' ESCAPE '\'
            AND nom = 'Dr Hélène Essai150') = 0, '« __ » ne renvoie pas tout';
    ASSERT (SELECT count(*) FROM medecin WHERE sans_accent(nom) ILIKE '%' || sans_accent('\%') || '%' ESCAPE '\'
            AND nom = 'Dr Hélène Essai150') = 0, '« % » ne renvoie pas tout';
    ASSERT (SELECT count(*) FROM medecin WHERE sans_accent(nom) ILIKE '%' || sans_accent('helene') || '%' ESCAPE '\'
            AND nom = 'Dr Hélène Essai150') = 1, 'saisie ordinaire inchangée';
END $$;

ROLLBACK;
