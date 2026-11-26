-- =====================================================================
-- Retour arrière du script 168 (réception en attente abandonnée par un autre employé)
--   Remet surveiller(p_jour) dans sa version du script 096, supprime surveillance_candidats_receptions(p_jour) et retire la valeur
--   « reception_abandonnee » de la contrainte ck_alerte_surveillance_type, comme avant le script 168.
--   ATTENTION : les anomalies de ce type déjà levées sont SUPPRIMÉES (la contrainte ne peut pas revenir sans cela).
--   Rejouable sans erreur. A lancer seul, hors de la suite numérotée : ce dossier n'est jamais lu par les outils de mise à jour.
-- =====================================================================

-- Fichier en UTF-8 : lu comme tel quel que soit l'encodage du poste qui lance psql.
SET client_encoding = 'UTF8';

-- surveiller : version du script 096
CREATE OR REPLACE FUNCTION surveiller(p_jour date) RETURNS TABLE(o_id bigint, o_niveau text, o_notification text)
LANGUAGE plpgsql AS $$
DECLARE
    c        record;
    v_id     bigint;
    v_ancien text;
BEGIN
    FOR c IN SELECT * FROM surveillance_candidats(p_jour)
             UNION ALL SELECT * FROM surveillance_candidats_achats(p_jour)
             UNION ALL SELECT * FROM surveillance_candidats_comptes(p_jour)
             UNION ALL SELECT * FROM surveillance_candidats_tiroir(p_jour) LOOP
        v_id := NULL;
        SELECT a.id, a.niveau INTO v_id, v_ancien FROM alerte_surveillance a
        WHERE a.type = c.o_type AND COALESCE(a.utilisateur_id, 0) = COALESCE(c.o_utilisateur_id, 0) AND a.jour = c.o_jour AND a.cle = c.o_cle;
        IF v_id IS NULL THEN
            INSERT INTO alerte_surveillance (type, niveau, utilisateur_id, jour, cle, message, valeur)
            VALUES (c.o_type, c.o_niveau, c.o_utilisateur_id, c.o_jour, c.o_cle, c.o_message, c.o_valeur)
            RETURNING id INTO v_id;
            o_id := v_id; o_niveau := c.o_niveau; o_notification := c.o_notification;
            RETURN NEXT;
        ELSE
            -- Une anomalie traitée reste traitée ; son message suit les chiffres du jour
            -- Une anomalie traitée qui devient urgente revient « à examiner » (la notification renvoie à l'écran)
            UPDATE alerte_surveillance SET message = c.o_message, valeur = c.o_valeur, niveau = c.o_niveau, mise_a_jour_le = now(),
                   statut = CASE WHEN v_ancien <> c.o_niveau AND c.o_niveau = 'urgent' THEN 'nouvelle' ELSE statut END,
                   action_prise = CASE WHEN v_ancien <> c.o_niveau AND c.o_niveau = 'urgent' AND statut = 'traitee'
                                       THEN concat('Traitée le ', to_char(traitee_le, 'DD/MM/YYYY HH24:MI'), COALESCE(' : ' || action_prise, ''),
                                                   ' ; redevenue urgente.')
                                       ELSE action_prise END,
                   traitee_par = CASE WHEN v_ancien <> c.o_niveau AND c.o_niveau = 'urgent' THEN NULL ELSE traitee_par END,
                   traitee_le = CASE WHEN v_ancien <> c.o_niveau AND c.o_niveau = 'urgent' THEN NULL ELSE traitee_le END
            WHERE id = v_id;
            IF v_ancien <> c.o_niveau AND c.o_niveau = 'urgent' THEN
                o_id := v_id; o_niveau := c.o_niveau; o_notification := c.o_notification;
                RETURN NEXT;
            END IF;
        END IF;
    END LOOP;
END;
$$;

DROP FUNCTION IF EXISTS surveillance_candidats_receptions(date);

DO $garde$ BEGIN
    IF EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'ck_alerte_surveillance_type'
               AND pg_get_constraintdef(oid) LIKE '%''reception_abandonnee''%') THEN
        DELETE FROM alerte_surveillance WHERE type = 'reception_abandonnee';
        ALTER TABLE alerte_surveillance DROP CONSTRAINT ck_alerte_surveillance_type;
        ALTER TABLE alerte_surveillance ADD CONSTRAINT ck_alerte_surveillance_type CHECK (type IN ('lignes_retirees', 'tiroir', 'attentes_abandonnees',
            'hors_horaires', 'remises', 'remises_patient', 'ajustements', 'casses', 'echantillons', 'boites_fantomes', 'sensibles_en_retard',
            'ecart_reception', 'reception_meme_personne', 'retour_sans_avoir', 'avoir_incomplet',
            'credit_nouveau_patient', 'credits_employe', 'credit_sans_remboursement', 'autorisations_valideur', 'autorisations_employe',
            'postes_simultanes', 'baisses_prix', 'sorties_tiroir'));
    END IF;
END $garde$;
