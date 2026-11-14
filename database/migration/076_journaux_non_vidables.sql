-- =====================================================================
-- Journaux et registre impossibles à vider
--   * le journal des actions (journal_audit), le registre des psychotropes
--     (registre_psychotrope) et le journal des sauvegardes
--     (journal_sauvegarde) refusaient déjà UPDATE et DELETE ligne par
--     ligne, mais pas TRUNCATE, qui vide la table sans passer par les
--     déclencheurs de ligne ;
--   * déclencheur d'instruction BEFORE TRUNCATE sur chacune des trois
--     tables : TRUNCATE est refusé avec un message clair (aussi quand la
--     table serait vidée par un TRUNCATE … CASCADE d'une autre table).
-- La restauration d'une sauvegarde n'est pas concernée : elle remplit une
-- base neuve (pg_restore) et ne vide aucune table.
-- Limite : le propriétaire des tables peut désactiver un déclencheur
-- (ALTER TABLE … DISABLE TRIGGER) ; la protection vaut contre une erreur
-- ou un outil, pas contre le propriétaire de la base.
-- Idempotent. A exécuter après 075.
-- =====================================================================

CREATE OR REPLACE FUNCTION interdire_vidage() RETURNS trigger
LANGUAGE plpgsql AS $$
BEGIN
    RAISE EXCEPTION '% ne peut pas être vidé (TRUNCATE refusé) : ses lignes sont conservées',
        CASE TG_TABLE_NAME
            WHEN 'journal_audit'        THEN 'Le journal des actions'
            WHEN 'registre_psychotrope' THEN 'Le registre des psychotropes'
            WHEN 'journal_sauvegarde'   THEN 'Le journal des sauvegardes'
            ELSE 'La table ' || TG_TABLE_NAME
        END;
END;
$$;

CREATE OR REPLACE TRIGGER trg_journal_non_vidable
    BEFORE TRUNCATE ON journal_audit
    FOR EACH STATEMENT EXECUTE FUNCTION interdire_vidage();

CREATE OR REPLACE TRIGGER trg_registre_non_vidable
    BEFORE TRUNCATE ON registre_psychotrope
    FOR EACH STATEMENT EXECUTE FUNCTION interdire_vidage();

CREATE OR REPLACE TRIGGER trg_journal_sauvegarde_non_vidable
    BEFORE TRUNCATE ON journal_sauvegarde
    FOR EACH STATEMENT EXECUTE FUNCTION interdire_vidage();
