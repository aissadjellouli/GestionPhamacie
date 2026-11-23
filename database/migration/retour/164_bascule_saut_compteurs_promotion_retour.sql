-- =====================================================================
-- Retour arrière du script 164 (saut des compteurs à la promotion)
--   Supprime les fonctions bascule_*, les tables bascule_saut et bascule_promotion et leur classement sync_table, et remet les
--   deux contraintes à liste de valeurs comme avant le script 164 (motif seul « restauration » ; évènements du journal fiscal sans
--   « saut_compteur »), SEULEMENT si aucune ligne n'utilise la valeur ajoutée.
--   REFUSÉ après un saut (bascule_promotion.saut) : les n° sautés ne se reprennent jamais (une vente a pu recevoir un n° de la
--   réserve) et leur trace est fiscale (plage dans compteur_ticket_saut, ligne du journal des ventes, journal des actions) ; ces
--   lignes sont en ajout seul et resteraient de toute façon. Avant tout saut (aucune promotion depuis le script), le retour passe.
--   Rejouable sans erreur. A lancer seul, hors de la suite numérotée : ce dossier n'est jamais lu par les outils de mise à jour.
-- =====================================================================

-- Fichier en UTF-8 : lu comme tel quel que soit l'encodage du poste qui lance psql.
SET client_encoding = 'UTF8';

DO $refus$ BEGIN
    IF to_regclass('public.bascule_promotion') IS NOT NULL THEN
        IF EXISTS (SELECT 1 FROM bascule_promotion WHERE saut) THEN
            RAISE EXCEPTION 'Retour du script 164 refusé : un saut de compteurs a déjà été fait à une promotion (trace fiscale à conserver)';
        END IF;
    END IF;
END $refus$;

DELETE FROM sync_table WHERE nom_table IN ('bascule_saut', 'bascule_promotion');
DROP TABLE IF EXISTS bascule_saut;
DROP TABLE IF EXISTS bascule_promotion;
DROP FUNCTION IF EXISTS bascule_saut_promotion(int);
DROP FUNCTION IF EXISTS bascule_noter_saut(bigint, text, text, bigint, bigint);
DROP FUNCTION IF EXISTS bascule_timeline_courante();

DO $contraintes$ BEGIN
    IF NOT EXISTS (SELECT 1 FROM compteur_ticket_saut WHERE motif = 'bascule')
       AND EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'ck_compteur_ticket_saut_motif'
                   AND conrelid = 'compteur_ticket_saut'::regclass AND pg_get_constraintdef(oid) LIKE '%''bascule''%') THEN
        ALTER TABLE compteur_ticket_saut DROP CONSTRAINT ck_compteur_ticket_saut_motif;
        ALTER TABLE compteur_ticket_saut ADD CONSTRAINT ck_compteur_ticket_saut_motif CHECK (motif IN ('restauration'));
    END IF;
    IF NOT EXISTS (SELECT 1 FROM journal_vente WHERE evenement = 'saut_compteur')
       AND EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'ck_journal_vente_evenement'
                   AND conrelid = 'journal_vente'::regclass AND pg_get_constraintdef(oid) LIKE '%saut_compteur%') THEN
        ALTER TABLE journal_vente DROP CONSTRAINT ck_journal_vente_evenement;
        ALTER TABLE journal_vente ADD CONSTRAINT ck_journal_vente_evenement CHECK (evenement IN
            ('ouverture', 'vente', 'annulation', 'retour', 'avoir', 'regularisation', 'annulation_regularisation'));
    END IF;
END $contraintes$;
