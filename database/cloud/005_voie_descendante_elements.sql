-- =====================================================================
-- Base centrale : voie descendante (2026-11-16, première brique de S3,
-- docs/conception-synchronisation.md § 15). Script de la base CENTRALE.
--   * descendant : éléments à faire descendre dans la base d'une officine
--     (table locale, clé, version, données JSON), version MONOTONE par
--     officine (officine_descendant.version_max). Contrat desc-v1 :
--     réutilisable en S3 (prix, réglages, commandes en brouillon).
--   * Premier élément : reglage_central / familles_envoyees (familles
--     nominatives autorisées, script 004). Publié à la demande : à chaque
--     échange, le réglage effectif de l'officine est comparé au dernier
--     publié ; s'il a changé, nouvelle version.
--   * cloud_descendants(p_apres) : pour l'officine courante (certificat),
--     publie puis renvoie les éléments de version > p_apres (version que
--     l'officine a déjà appliquée : acquittement, gardé dans
--     officine_descendant.version_acquittee).
--   * C'est toujours l'officine qui initie (réponse au dépôt d'un lot et
--     échange léger à chaque passage) ; aucune connexion vers l'officine.
-- Idempotent ; à passer après 004.
-- =====================================================================

CREATE TABLE IF NOT EXISTS officine_descendant (
    officine_id        uuid        PRIMARY KEY,
    version_max        bigint      NOT NULL DEFAULT 0 CHECK (version_max >= 0),
    version_acquittee  bigint      NOT NULL DEFAULT 0 CHECK (version_acquittee >= 0),
    acquittee_le       timestamptz
);

CREATE TABLE IF NOT EXISTS descendant (
    officine_id  uuid        NOT NULL,
    nom_table    text        NOT NULL CHECK (nom_table ~ '^[a-z_]{1,63}$'),
    cle          text        NOT NULL CHECK (length(cle) BETWEEN 1 AND 200),
    version      bigint      NOT NULL CHECK (version >= 1),
    donnees      jsonb       NOT NULL CHECK (jsonb_typeof(donnees) = 'object'),
    modifie_le   timestamptz NOT NULL DEFAULT now(),
    PRIMARY KEY (officine_id, nom_table, cle)
);
CREATE INDEX IF NOT EXISTS ix_descendant_version ON descendant (officine_id, version);

ALTER TABLE officine_descendant ENABLE ROW LEVEL SECURITY;
ALTER TABLE officine_descendant FORCE ROW LEVEL SECURITY;
DROP POLICY IF EXISTS cloisonnement ON officine_descendant;
CREATE POLICY cloisonnement ON officine_descendant USING (officine_id = cloud_officine_courante()) WITH CHECK (officine_id = cloud_officine_courante());
ALTER TABLE descendant ENABLE ROW LEVEL SECURITY;
ALTER TABLE descendant FORCE ROW LEVEL SECURITY;
DROP POLICY IF EXISTS cloisonnement ON descendant;
CREATE POLICY cloisonnement ON descendant USING (officine_id = cloud_officine_courante()) WITH CHECK (officine_id = cloud_officine_courante());
GRANT SELECT ON descendant, officine_descendant TO pharma_cloud_api;

-- Publie un élément pour l'officine courante : nouvelle version seulement si les données changent ; renvoie sa version
CREATE OR REPLACE FUNCTION cloud_publier_descendant(p_table text, p_cle text, p_donnees jsonb) RETURNS bigint
LANGUAGE plpgsql AS $$
DECLARE v_off uuid := cloud_officine_courante(); d descendant; v bigint;
BEGIN
    IF v_off IS NULL THEN RAISE EXCEPTION 'Officine courante non posée (app.officine_id)'; END IF;
    SELECT * INTO d FROM descendant WHERE officine_id = v_off AND nom_table = p_table AND cle = p_cle;
    IF FOUND AND d.donnees = p_donnees THEN RETURN d.version; END IF;
    INSERT INTO officine_descendant (officine_id) VALUES (v_off) ON CONFLICT DO NOTHING;
    UPDATE officine_descendant SET version_max = version_max + 1 WHERE officine_id = v_off RETURNING version_max INTO v;
    INSERT INTO descendant (officine_id, nom_table, cle, version, donnees) VALUES (v_off, p_table, p_cle, v, p_donnees)
    ON CONFLICT (officine_id, nom_table, cle) DO UPDATE SET version = EXCLUDED.version, donnees = EXCLUDED.donnees, modifie_le = now();
    RETURN v;
END $$;
REVOKE EXECUTE ON FUNCTION cloud_publier_descendant(text, text, jsonb) FROM PUBLIC;

-- Échange pour l'officine courante (API) : publie le réglage effectif, enregistre l'acquittement, renvoie les éléments plus récents
-- Garde de rejeu (2026-11-18) : définition remplacée par le script 009 ; rejouer ce script après 009 ne la
-- ramène pas en arrière (marqueur : colonne cloud_parametre.quota_chargements_octets_24h, posé par 009).
DO $rejeu$ BEGIN
    IF EXISTS (SELECT 1 FROM pg_attribute WHERE attrelid = 'public.cloud_parametre'::regclass AND attname = 'quota_chargements_octets_24h' AND NOT attisdropped) THEN
        RAISE NOTICE 'cloud_descendants : définition plus récente (script 009) gardée';
    ELSE
        EXECUTE $def$
CREATE OR REPLACE FUNCTION cloud_descendants(p_apres bigint)
RETURNS TABLE (nom_table text, cle text, version bigint, donnees jsonb, version_max bigint)
LANGUAGE plpgsql SECURITY DEFINER SET search_path = public, pg_temp AS $$
DECLARE v_off uuid := cloud_officine_courante(); v_max bigint;
BEGIN
    IF v_off IS NULL THEN RAISE EXCEPTION 'Officine courante non posée (app.officine_id)'; END IF;
    PERFORM cloud_publier_descendant('reglage_central', 'familles_envoyees',
        jsonb_build_object('familles', to_jsonb(cloud_familles_envoyees(v_off))));
    SELECT o.version_max INTO v_max FROM officine_descendant o WHERE o.officine_id = v_off;
    IF p_apres IS NOT NULL AND p_apres BETWEEN 0 AND v_max THEN
        UPDATE officine_descendant o SET version_acquittee = greatest(o.version_acquittee, p_apres), acquittee_le = now()
        WHERE o.officine_id = v_off AND o.version_acquittee < p_apres;
    END IF;
    RETURN QUERY SELECT d.nom_table, d.cle, d.version, d.donnees, v_max FROM descendant d
                 WHERE d.officine_id = v_off AND d.version > coalesce(p_apres, 0) ORDER BY d.version;
END $$;
$def$;
    END IF;
END $rejeu$;
REVOKE EXECUTE ON FUNCTION cloud_descendants(bigint) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION cloud_descendants(bigint) TO pharma_cloud_api;

COMMENT ON TABLE descendant IS 'Serveur central : éléments descendants par officine (contrat desc-v1), version monotone par officine (script 005).';
COMMENT ON TABLE officine_descendant IS 'Serveur central : dernière version descendante publiée et acquittée par officine (script 005).';
