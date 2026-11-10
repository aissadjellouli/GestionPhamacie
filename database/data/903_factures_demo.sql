-- =====================================================================
-- Factures clients FICTIVES (développement et tests seulement, jamais en
-- production), pour voir vivre les écrans « Factures » et « Dettes et
-- créances » : chaque état du suivi a au moins une facture.
--   * à encaisser, récente (dans le délai d'alerte) ;
--   * à encaisser, ancienne (échéance dépassée : remonte en alerte) ;
--   * avec un avoir partiel ;
--   * rejetée par l'organisme (toujours signalée) ;
--   * réglée en partie par un versement en banque ;
--   * soldée (payée au comptoir) ;
--   * retirée du suivi de trésorerie.
-- Lignes « libres » : aucune vente n'est modifiée. Dates calculées par
-- rapport au jour d'exécution. Numéros pris au compteur des factures,
-- comme le logiciel.
-- Établies à des dates passées, échéance 30 jours après l'établissement.
-- A exécuter après les scripts 0xx (040 compris) et 900_donnees_demo.sql.
-- Réexécution sans effet ; ignoré sans données de démonstration.
-- =====================================================================
SELECT NOT EXISTS (SELECT 1 FROM utilisateur WHERE identifiant = 'titulaire')
       OR to_regclass('public.facture_client') IS NULL AS sans_demo \gset
\if :sans_demo
\echo 'Données de démonstration absentes : factures de démonstration ignorées.'
\quit
\endif

SELECT EXISTS (SELECT 1 FROM client_facture WHERE raison_sociale = 'Société Démo SARL (fictif)') AS factures_deja_chargees \gset
\if :factures_deja_chargees
\echo 'Factures de démonstration déjà présentes : rien à faire.'
\quit
\endif

BEGIN;

-- Entreprises clientes fictives
INSERT INTO client_facture (raison_sociale, adresse, telephone, nif, rc, nis, article_imposition) VALUES
    ('Société Démo SARL (fictif)',   '12 rue des Essais, Alger',  '0550000001', '000016000000001', '16/00-0000001B00', '000016000000011', '16000000001'),
    ('Clinique Exemple SPA (fictif)', '4 avenue du Test, Blida',   '0550000002', '000009000000002', '09/00-0000002B00', '000009000000022', '09000000002');

DO $$
DECLARE
    v_titulaire  bigint := (SELECT id FROM utilisateur WHERE identifiant = 'titulaire');
    v_societe    bigint := (SELECT id FROM client_facture WHERE raison_sociale = 'Société Démo SARL (fictif)');
    v_clinique   bigint := (SELECT id FROM client_facture WHERE raison_sociale = 'Clinique Exemple SPA (fictif)');
    v_cnas       bigint := (SELECT id FROM organisme WHERE code = 'CNAS');
    v_casnos     bigint := (SELECT id FROM organisme WHERE code = 'CASNOS');
    v_patient    bigint := (SELECT id FROM patient ORDER BY id LIMIT 1);
    v_banque     bigint := (SELECT id FROM compte_tresorerie WHERE type = 'banque' AND actif ORDER BY id LIMIT 1);
    v_id         bigint;
    v_ancienne   bigint;
    v_mouvement  bigint;
    f            record;
    l            record;
    v_ht         numeric(12,2);
    v_ttc        numeric(12,2);
    v_numero     text;
    v_annee      int;
    v_seq        int;
BEGIN
    -- Factures : destinataire, ancienneté (jours), état, et leurs lignes libres (désignation, quantité, prix TTC, TVA)
    FOR f IN
        SELECT * FROM (VALUES
            ('recente',   'entreprise', v_societe,  NULL::bigint, NULL::bigint, 'Société Démo SARL (fictif)',    '12 rue des Essais, Alger · NIF 000016000000001', 10, 'emise',   NULL::text,                                         false, NULL::text, 0::numeric),
            ('ancienne',  'entreprise', v_societe,  NULL,         NULL,         'Société Démo SARL (fictif)',    '12 rue des Essais, Alger · NIF 000016000000001', 45, 'emise',   NULL,                                               false, NULL,       0),
            ('avoir',     'entreprise', v_clinique, NULL,         NULL,         'Clinique Exemple SPA (fictif)', '4 avenue du Test, Blida · NIF 000009000000002',  25, 'emise',   NULL,                                               false, NULL,       0),
            ('rejetee',   'organisme',  NULL,       v_cnas,       NULL,         'CNAS',                          NULL,                                             70, 'rejetee', 'Numéro d''assuré illisible sur deux ordonnances',  false, NULL,       0),
            ('partielle', 'organisme',  NULL,       v_casnos,     NULL,         'CASNOS',                        NULL,                                             20, 'emise',   NULL,                                               false, NULL,       0),
            ('soldee',    'patient',    NULL,       NULL,         v_patient,    (SELECT trim(concat_ws(' ', nom, prenom)) FROM patient WHERE id = v_patient), NULL, 5, 'emise', NULL,                                              false, NULL,       3200),
            ('horssuivi', 'entreprise', v_clinique, NULL,         NULL,         'Clinique Exemple SPA (fictif)', '4 avenue du Test, Blida · NIF 000009000000002',  90, 'emise',   NULL,                                               true,  'Créance abandonnée (essai)', 0)
        ) AS t(cle, destinataire, client_id, organisme_id, patient_id, nom, infos, jours, statut, motif_rejet, hors_suivi, motif_hors_suivi, deja_paye)
        WHERE (t.destinataire <> 'organisme' OR t.organisme_id IS NOT NULL)
          AND (t.destinataire <> 'patient' OR t.patient_id IS NOT NULL)
        ORDER BY t.jours DESC   -- de la plus ancienne à la plus récente : les numéros suivent les dates
    LOOP
        -- Numéro pris au compteur de l'année de la facture (F-AAAA-0001), comme le logiciel
        v_annee := extract(year FROM current_date - f.jours)::int;
        INSERT INTO facture_compteur (type, annee, dernier) VALUES ('facture', v_annee, 1)
        ON CONFLICT (type, annee) DO UPDATE SET dernier = facture_compteur.dernier + 1
        RETURNING dernier INTO v_seq;
        v_numero := 'F-' || v_annee || '-' || lpad(v_seq::text, 4, '0');

        SELECT COALESCE(SUM(round(x.quantite * x.prix, 2)), 0),
               COALESCE(SUM(round(round(x.quantite * x.prix, 2) / (1 + x.tva / 100), 2)), 0)
        INTO v_ttc, v_ht
        FROM (VALUES
            ('recente',   'Paracétamol 500 mg cp (dotation infirmerie)',         30, 120.00, 0),
            ('recente',   'Gel hydroalcoolique 500 ml',                          10, 290.00, 19),
            ('ancienne',  'Trousses de premiers secours (parapharmacie)',         8, 1500.00, 19),
            ('avoir',     'Compresses stériles, boîte de 50',                    40, 180.00, 19),
            ('avoir',     'Sérum physiologique 500 ml',                          60, 95.00, 0),
            ('rejetee',   'Part organisme, ordonnances de la période (fictif)',   1, 48000.00, 0),
            ('partielle', 'Part organisme, ordonnances de la période (fictif)',   1, 15000.00, 0),
            ('soldee',    'Médicaments délivrés sur ordonnance (fictif)',         1, 3200.00, 0),
            ('horssuivi', 'Pansements et désinfectants',                          1, 900.00, 19)
        ) AS x(cle, designation, quantite, prix, tva)
        WHERE x.cle = f.cle;

        INSERT INTO facture_client (type, numero, date_facture, destinataire, patient_id, client_id, organisme_id, destinataire_nom,
                                    destinataire_infos, periode_du, periode_au, montant_ht, montant_tva, montant_ttc, deja_paye, statut,
                                    motif_rejet, hors_suivi, motif_hors_suivi, commentaire, utilisateur_id, cree_le, date_echeance)
        VALUES ('facture', v_numero, current_date - f.jours, f.destinataire, f.patient_id, f.client_id, f.organisme_id, f.nom,
                f.infos, current_date - f.jours - 30, current_date - f.jours, v_ht, v_ttc - v_ht, v_ttc, f.deja_paye, f.statut,
                f.motif_rejet, f.hors_suivi, f.motif_hors_suivi, 'Facture de démonstration (fictive)', v_titulaire,
                (current_date - f.jours)::timestamptz + interval '10 hours',
                current_date - f.jours + 30)   -- échéance à 30 jours de l'établissement (040)
        RETURNING id INTO v_id;

        INSERT INTO facture_client_ligne (facture_id, ordre, part, designation, quantite, prix_unitaire_ttc, taux_tva, montant_ttc)
        SELECT v_id, row_number() OVER (), 'libre', x.designation, x.quantite, x.prix, x.tva, round(x.quantite * x.prix, 2)
        FROM (VALUES
            ('recente',   'Paracétamol 500 mg cp (dotation infirmerie)',         30, 120.00, 0),
            ('recente',   'Gel hydroalcoolique 500 ml',                          10, 290.00, 19),
            ('ancienne',  'Trousses de premiers secours (parapharmacie)',         8, 1500.00, 19),
            ('avoir',     'Compresses stériles, boîte de 50',                    40, 180.00, 19),
            ('avoir',     'Sérum physiologique 500 ml',                          60, 95.00, 0),
            ('rejetee',   'Part organisme, ordonnances de la période (fictif)',   1, 48000.00, 0),
            ('partielle', 'Part organisme, ordonnances de la période (fictif)',   1, 15000.00, 0),
            ('soldee',    'Médicaments délivrés sur ordonnance (fictif)',         1, 3200.00, 0),
            ('horssuivi', 'Pansements et désinfectants',                          1, 900.00, 19)
        ) AS x(cle, designation, quantite, prix, tva)
        WHERE x.cle = f.cle;

        -- Avoir partiel de 2 000 DA sur la facture de la clinique (retour de marchandise)
        IF f.cle = 'avoir' THEN
            v_ancienne := v_id;
            v_annee := extract(year FROM current_date - 15)::int;
            INSERT INTO facture_compteur (type, annee, dernier) VALUES ('avoir', v_annee, 1)
            ON CONFLICT (type, annee) DO UPDATE SET dernier = facture_compteur.dernier + 1
            RETURNING dernier INTO v_seq;
            INSERT INTO facture_client (type, numero, date_facture, destinataire, client_id, destinataire_nom, destinataire_infos,
                                        montant_ht, montant_tva, montant_ttc, facture_origine_id, motif, commentaire, utilisateur_id, cree_le)
            VALUES ('avoir', 'AV-' || v_annee || '-' || lpad(v_seq::text, 4, '0'), current_date - 15, 'entreprise', f.client_id, f.nom, f.infos,
                    round(2000 / 1.19, 2), 2000 - round(2000 / 1.19, 2), 2000, v_ancienne, 'Retour de 11 boîtes de compresses (essai)',
                    'Avoir de démonstration (fictif)', v_titulaire, (current_date - 15)::timestamptz + interval '11 hours')
            RETURNING id INTO v_id;
            INSERT INTO facture_client_ligne (facture_id, ordre, part, designation, quantite, prix_unitaire_ttc, taux_tva, montant_ttc)
            VALUES (v_id, 1, 'libre', 'Avoir sur compresses stériles', 1, 2000, 19, 2000);
        END IF;

        -- Versement partiel de l'organisme en banque (6 000 DA sur 15 000) : seulement si une banque existe
        IF f.cle = 'partielle' AND v_banque IS NOT NULL THEN
            INSERT INTO mouvement_tresorerie (compte_id, date_mouvement, sens, montant, nature, libelle, reference, organisme_id, utilisateur_id)
            VALUES (v_banque, current_date - 3, 'entree', 6000, 'versement_organisme', 'Versement CASNOS (démonstration)', 'VIR-DEMO-01',
                    f.organisme_id, v_titulaire)
            RETURNING id INTO v_mouvement;
            INSERT INTO reglement_facture_client (mouvement_id, facture_id, montant) VALUES (v_mouvement, v_id, 6000);
        END IF;
    END LOOP;
END $$;

COMMIT;
