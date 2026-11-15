-- =====================================================================
-- Fuseau horaire de la base : Africa/Algiers (UTC+1 toute l'année, sans
-- heure d'été). Les dates calculées en SQL (jour d'une vente, d'une
-- clôture, rapports « du jour ») sont celles de l'officine, quel que soit
-- le fuseau du serveur PostgreSQL (avant : Europe/Paris en test, une vente
-- de 23 h 30 changeait de jour l'été).
--
-- ALTER DATABASE … SET timezone : réglage de la BASE, pas du schéma. Il
-- n'apparaît PAS dans un dump de schéma (database/init/schema.sql) : sur
-- une installation neuve, lancer aussi ce script après schema.sql
-- (docs/mise-en-production.md). Il vaut pour les connexions ouvertes après
-- son exécution. L'API ajoute de toute façon Timezone=Africa/Algiers à sa
-- chaîne de connexion si celle-ci n'en précise pas.
--
-- Aucune donnée modifiée : les colonnes timestamptz sont stockées en UTC ;
-- seul l'affichage et les conversions en date changent.
-- Idempotent. A exécuter après 082_medecin_coordonnees.sql.
-- =====================================================================

DO $$
BEGIN
    EXECUTE format('ALTER DATABASE %I SET timezone = %L', current_database(), 'Africa/Algiers');
END
$$;
