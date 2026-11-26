-- =====================================================================
-- 168 -- Contrôle interne : réception en attente abandonnée par un autre employé
--   Décision du titulaire du 2026-11-26 (lot 2, point 9) : l'abandon d'une réception mise en attente par un autre employé lève une
--   anomalie du contrôle interne (type « reception_abandonnee », niveau « attention », une par réception abandonnée). Abandonner sa
--   propre saisie reste normal : aucune anomalie.
--   Source : journal des actions (`journal_audit`, action `reception_attente_abandonnee`) ; l'employé qui avait mis la réception en
--   attente y est noté par son identifiant (`details.mise_en_attente_par_id`) depuis ce script. Les abandons notés avant ne portent que
--   son nom : ils ne lèvent aucune anomalie (rien n'est deviné).
--   * contrainte ck_alerte_surveillance_type : valeur « reception_abandonnee » ajoutée (garde : jamais recréée si elle y est déjà) ;
--   * surveillance_candidats_receptions(p_jour) : nouvelle source d'anomalies ;
--   * surveiller(p_jour) : redéfinie à l'identique du script 096, plus cette source.
--   Aucune donnée existante modifiée.
--   Retour arrière : database/migration/retour/168_controle_reception_abandonnee_retour.sql (les anomalies de ce type sont supprimées).
-- Idempotent et rejouable sur une base plus récente.
-- A exécuter après 167_postes_autorises.sql.
-- =====================================================================

-- Fichier en UTF-8 : lu comme tel quel que soit l'encodage du poste qui lance psql.
SET client_encoding = 'UTF8';

DO $garde$ BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'ck_alerte_surveillance_type'
                   AND pg_get_constraintdef(oid) LIKE '%''reception_abandonnee''%') THEN
        ALTER TABLE alerte_surveillance DROP CONSTRAINT IF EXISTS ck_alerte_surveillance_type;
        ALTER TABLE alerte_surveillance ADD CONSTRAINT ck_alerte_surveillance_type CHECK (type IN ('lignes_retirees', 'tiroir', 'attentes_abandonnees',
            'hors_horaires', 'remises', 'remises_patient', 'ajustements', 'casses', 'echantillons', 'boites_fantomes', 'sensibles_en_retard',
            'ecart_reception', 'reception_meme_personne', 'retour_sans_avoir', 'avoir_incomplet',
            'credit_nouveau_patient', 'credits_employe', 'credit_sans_remboursement', 'autorisations_valideur', 'autorisations_employe',
            'postes_simultanes', 'baisses_prix', 'sorties_tiroir', 'reception_abandonnee'));
    END IF;
END $garde$;

CREATE OR REPLACE FUNCTION surveillance_candidats_receptions(p_jour date)
RETURNS TABLE (o_type text, o_niveau text, o_utilisateur_id bigint, o_jour date, o_cle text, o_message text, o_notification text, o_valeur numeric)
LANGUAGE sql STABLE AS $$
    SELECT 'reception_abandonnee', 'attention', j.utilisateur_id, p_jour, j.enregistrement_id::text,
           format('%s a abandonné la réception en attente « %s » (%s ligne(s)) mise en attente par %s, le %s à %s.',
                  u.nom_complet, j.details->>'libelle', COALESCE(j.details->>'lignes', '?'), COALESCE(j.details->>'mise_en_attente_par', '?'),
                  to_char(p_jour, 'DD/MM/YYYY'), to_char(j.cree_le, 'HH24:MI')),
           format('%s : réception « %s » d''un collègue abandonnée le %s.', u.nom_complet, j.details->>'libelle', to_char(p_jour, 'DD/MM')),
           COALESCE((j.details->>'lignes')::numeric, 0)
    FROM journal_audit j
    JOIN utilisateur u ON u.id = j.utilisateur_id
    WHERE j.action = 'reception_attente_abandonnee'
      AND j.cree_le::date = p_jour
      AND j.enregistrement_id IS NOT NULL
      AND j.details ? 'mise_en_attente_par_id'
      AND (j.details->>'mise_en_attente_par_id')::bigint <> j.utilisateur_id;
$$;

COMMENT ON FUNCTION surveillance_candidats_receptions(date) IS
    'Contrôle interne (script 168) : réception en attente abandonnée par un autre employé que celui qui l''avait mise en attente, une anomalie par réception';

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
             UNION ALL SELECT * FROM surveillance_candidats_tiroir(p_jour)
             UNION ALL SELECT * FROM surveillance_candidats_receptions(p_jour) LOOP
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
