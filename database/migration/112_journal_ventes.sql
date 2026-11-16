-- =====================================================================
-- Journal fiscal des ventes (2026-11-16, lot 3, M6b, conformité 2027)
--   * journal_vente : journal en AJOUT SEUL, chaîné par la brique du
--     script 111 (integrite_activer). Une ligne par événement fiscal :
--     vente encaissée, annulation, retour client, avoir, régularisation
--     Chifa (et son annulation), plus une ligne d'ouverture à la mise en
--     service. La table vente n'est PAS figée (mises à jour Chifa
--     conservées, analyse-ecarts § 5 risque 2) : le journal est séparé.
--   * Les lignes sont écrites par des déclencheurs DIFFÉRÉS (contrainte
--     DEFERRABLE INITIALLY DEFERRED) : dans la MÊME transaction que
--     l'opération, juste avant sa validation, quand lignes et paiements
--     sont tous écrits. Une opération ne peut donc pas être enregistrée
--     sans sa ligne (si la ligne échoue, toute l'opération est annulée).
--   * Numérotation SANS TROU des tickets « T- » : compteur_ticket (une
--     ligne verrouillée par UPDATE jusqu'à la fin de la transaction) au
--     lieu de la séquence seq_numero_vente (laissée en place, plus
--     utilisée). Une transaction annulée rend son numéro. Les tickets
--     existants gardent leur numéro ; la continuité s'applique à partir
--     de compteur_ticket.premier_numero_continu (mise en service).
--   * Montants (TTC, HT, TVA) ventilés par taux de TVA (taux de la
--     ligne, sinon du produit ou de la prestation, sinon 0) ;
--     detail_tva = « taux:ttc:ht:tva » par taux, séparés par « ; ».
--     Signe : vente positive, annulation et retour négatifs ; avoir et
--     régularisation : TTC nul (ni chiffre d'affaires ni TVA), montant
--     dans montant_mouvement / parts. [À VÉRIFIER] art. 51 bis (TVA
--     médicaments 0 %, parapharmacie et prestations 19 %).
--   * cloture_fiscale : clôture journalière (une par session de caisse
--     clôturée) ou mensuelle, reliée à son sceau (sceau_integrite,
--     HMAC calculé par l'application), en ajout seul.
--   * parametre_fiscal [À VÉRIFIER] : durée de conservation (6 ans au
--     moins), format d'export DGI, fuseau des périodes.
--   * Conservation : journal, sceaux et clôtures ne se suppriment
--     jamais ; ventes, paiements, annulations et retours de moins de
--     parametre_fiscal.conservation_annees ne peuvent pas être supprimés.
--   Aucune donnée existante supprimée ni modifiée ; pas de reprise des
--   ventes passées dans le journal (ligne d'ouverture seulement).
--   Idempotent. À exécuter après 111.
-- =====================================================================

-- ---- 1. Paramètres [À VÉRIFIER] ----------------------------------------------
CREATE TABLE IF NOT EXISTS parametre_fiscal (
    id                   smallint    PRIMARY KEY DEFAULT 1 CHECK (id = 1),
    conservation_annees  smallint    NOT NULL DEFAULT 6,
    format_export_dgi    text        NOT NULL DEFAULT 'csv-v1',
    separateur_csv       text        NOT NULL DEFAULT ';',
    fuseau               text        NOT NULL DEFAULT 'Africa/Algiers',
    CONSTRAINT ck_parametre_fiscal_conservation CHECK (conservation_annees BETWEEN 6 AND 30),
    CONSTRAINT ck_parametre_fiscal_format CHECK (format_export_dgi IN ('csv-v1')),
    CONSTRAINT ck_parametre_fiscal_separateur CHECK (separateur_csv IN (';', ',', E'\t'))
);
INSERT INTO parametre_fiscal (id) VALUES (1) ON CONFLICT (id) DO NOTHING;

-- ---- 2. Compteur des tickets (sans trou) -------------------------------------
CREATE TABLE IF NOT EXISTS compteur_ticket (
    id                      smallint    PRIMARY KEY DEFAULT 1 CHECK (id = 1),
    prefixe                 text        NOT NULL DEFAULT 'T-',
    dernier_numero          bigint      NOT NULL,
    premier_numero_continu  bigint      NOT NULL,
    mis_en_service_le       timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT ck_compteur_ticket_numero CHECK (dernier_numero >= 0 AND premier_numero_continu >= 1)
);
-- Départ : plus grand n° « T-nnnnnn » déjà attribué (les n° de démonstration « T-DEMO-… » ne comptent pas)
INSERT INTO compteur_ticket (id, dernier_numero, premier_numero_continu)
SELECT 1, m, m + 1
FROM (SELECT COALESCE(max(substring(numero FROM '^T-([0-9]+)$')::bigint), 0) AS m FROM vente) x
ON CONFLICT (id) DO NOTHING;

-- Le compteur n'avance que d'une unité à la fois, ne recule jamais et ne se supprime pas
CREATE OR REPLACE FUNCTION compteur_ticket_proteger() RETURNS trigger
LANGUAGE plpgsql AS $$
BEGIN
    IF TG_OP = 'UPDATE' AND NEW.dernier_numero = OLD.dernier_numero + 1 AND NEW.prefixe = OLD.prefixe
       AND NEW.premier_numero_continu = OLD.premier_numero_continu AND NEW.mis_en_service_le = OLD.mis_en_service_le THEN
        RETURN NEW;
    END IF;
    RAISE EXCEPTION 'Le compteur des tickets avance d''une unité à la fois ; il ne se modifie pas et ne se supprime pas';
END;
$$;
CREATE OR REPLACE TRIGGER trg_compteur_ticket_proteger
    BEFORE UPDATE OR DELETE ON compteur_ticket
    FOR EACH ROW EXECUTE FUNCTION compteur_ticket_proteger();

-- N° du prochain ticket : la ligne du compteur reste verrouillée jusqu'à la fin de la transaction (deux caisses attendent
-- l'une l'autre) ; une transaction annulée rend son numéro. Au moins 6 chiffres (au-delà de 999 999 : 7 chiffres, jamais tronqué).
CREATE OR REPLACE FUNCTION prochain_numero_vente() RETURNS text
LANGUAGE plpgsql AS $$
DECLARE
    v_numero  bigint;
    v_prefixe text;
BEGIN
    UPDATE compteur_ticket SET dernier_numero = dernier_numero + 1 WHERE id = 1 RETURNING dernier_numero, prefixe INTO v_numero, v_prefixe;
    IF NOT FOUND THEN
        RAISE EXCEPTION 'Compteur des tickets absent (script 112)';
    END IF;
    RETURN v_prefixe || lpad(v_numero::text, GREATEST(6, char_length(v_numero::text)), '0');
END;
$$;

-- ---- 3. Journal des ventes ---------------------------------------------------
CREATE TABLE IF NOT EXISTS journal_vente (
    id                  bigint        GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    evenement           text          NOT NULL,
    numero_ticket       text,
    vente_id            bigint        REFERENCES vente (id),
    annulation_id       bigint        REFERENCES annulation_vente (id),
    retour_client_id    bigint        REFERENCES retour_client (id),
    avoir_mouvement_id  bigint        REFERENCES avoir_mouvement (id),
    regularisation_id   bigint        REFERENCES regularisation_part_organisme (id),
    survenu_le          timestamptz   NOT NULL,
    enregistre_le       timestamptz   NOT NULL DEFAULT clock_timestamp(),
    session_caisse_id   bigint        REFERENCES session_caisse (id),
    poste               text,
    utilisateur_id      bigint        REFERENCES utilisateur (id),
    type_vente          text,
    montant_ttc         numeric(14,2) NOT NULL DEFAULT 0,
    montant_ht          numeric(14,2) NOT NULL DEFAULT 0,
    montant_tva         numeric(14,2) NOT NULL DEFAULT 0,
    detail_tva          text          NOT NULL DEFAULT '',
    part_patient        numeric(14,2) NOT NULL DEFAULT 0,
    part_organisme      numeric(14,2) NOT NULL DEFAULT 0,
    paiements           text          NOT NULL DEFAULT '',
    montant_mouvement   numeric(14,2) NOT NULL DEFAULT 0,
    motif               text,
    CONSTRAINT ck_journal_vente_evenement CHECK (evenement IN
        ('ouverture', 'vente', 'annulation', 'retour', 'avoir', 'regularisation', 'annulation_regularisation')),
    CONSTRAINT ck_journal_vente_montants CHECK (montant_ht + montant_tva = montant_ttc)
);
CREATE INDEX IF NOT EXISTS idx_journal_vente_survenu ON journal_vente (survenu_le);
CREATE INDEX IF NOT EXISTS idx_journal_vente_ticket ON journal_vente (numero_ticket);
CREATE UNIQUE INDEX IF NOT EXISTS ux_journal_vente_vente ON journal_vente (vente_id) WHERE evenement = 'vente';
CREATE UNIQUE INDEX IF NOT EXISTS ux_journal_vente_annulation ON journal_vente (vente_id) WHERE evenement = 'annulation';
CREATE UNIQUE INDEX IF NOT EXISTS ux_journal_vente_retour ON journal_vente (retour_client_id) WHERE retour_client_id IS NOT NULL;
CREATE UNIQUE INDEX IF NOT EXISTS ux_journal_vente_avoir ON journal_vente (avoir_mouvement_id) WHERE avoir_mouvement_id IS NOT NULL;
CREATE UNIQUE INDEX IF NOT EXISTS ux_journal_vente_regularisation ON journal_vente (regularisation_id, evenement) WHERE regularisation_id IS NOT NULL;

SELECT integrite_activer('journal_vente', 'Journal des ventes (fiscal)', ARRAY[
    'id', 'evenement', 'numero_ticket', 'vente_id', 'annulation_id', 'retour_client_id', 'avoir_mouvement_id', 'regularisation_id',
    'survenu_le', 'enregistre_le', 'session_caisse_id', 'poste', 'utilisateur_id', 'type_vente',
    'montant_ttc', 'montant_ht', 'montant_tva', 'detail_tva', 'part_patient', 'part_organisme', 'paiements', 'montant_mouvement', 'motif']);

-- Ligne d'ouverture (une seule, journal vide) : mise en service, premier n° continu, ventes passées non reprises
INSERT INTO journal_vente (evenement, survenu_le, motif)
SELECT 'ouverture', c.mis_en_service_le,
       format('Mise en service du journal fiscal : premier ticket continu %s%s ; ventes antérieures non reprises (%s vente(s) en base)',
              c.prefixe, lpad(c.premier_numero_continu::text, GREATEST(6, char_length(c.premier_numero_continu::text)), '0'),
              (SELECT count(*) FROM vente))
FROM compteur_ticket c
WHERE c.id = 1 AND NOT EXISTS (SELECT 1 FROM journal_vente);

-- ---- 4. Ventilation par taux de TVA ------------------------------------------
-- p_taux (triés, distincts) et p_poids (montant TTC des lignes par taux) : le TTC est réparti au prorata, l'arrondi va au taux le
-- plus lourd ; HT = TTC / (1 + taux/100) arrondi au centime, TVA = TTC - HT. Sans ligne : tout au taux 0.
CREATE OR REPLACE FUNCTION journal_vente_ventiler(p_taux numeric[], p_poids numeric[], p_ttc numeric)
RETURNS TABLE (detail text, ht numeric, tva numeric)
LANGUAGE plpgsql IMMUTABLE AS $$
DECLARE
    v_n      int := COALESCE(cardinality(p_taux), 0);
    v_total  numeric := 0;
    v_reste  numeric;
    v_max    int := 1;
    v_ttc    numeric[] := '{}';
    v_h      numeric;
    v_parts  text[] := '{}';
BEGIN
    IF v_n = 0 THEN
        p_taux := ARRAY[0::numeric]; p_poids := ARRAY[1::numeric]; v_n := 1;
    END IF;
    FOR i IN 1..v_n LOOP
        v_total := v_total + abs(COALESCE(p_poids[i], 0));
        IF abs(COALESCE(p_poids[i], 0)) > abs(COALESCE(p_poids[v_max], 0)) THEN v_max := i; END IF;
    END LOOP;
    v_reste := p_ttc;
    FOR i IN 1..v_n LOOP
        v_ttc[i] := CASE WHEN v_total = 0 THEN 0 ELSE round(p_ttc * abs(COALESCE(p_poids[i], 0)) / v_total, 2) END;
        v_reste := v_reste - v_ttc[i];
    END LOOP;
    v_ttc[v_max] := v_ttc[v_max] + v_reste;
    ht := 0; tva := 0;
    FOR i IN 1..v_n LOOP
        v_h := round(v_ttc[i] / (1 + COALESCE(p_taux[i], 0) / 100), 2);
        ht := ht + v_h;
        tva := tva + (v_ttc[i] - v_h);
        v_parts := v_parts || format('%s:%s:%s:%s', to_char(COALESCE(p_taux[i], 0), 'FM990.00'),
                                     v_ttc[i]::numeric(14,2), v_h::numeric(14,2), (v_ttc[i] - v_h)::numeric(14,2));
    END LOOP;
    detail := array_to_string(v_parts, ';');
    RETURN NEXT;
END;
$$;

-- Taux de TVA d'une ligne de vente : celui de la ligne, sinon du produit ou de la prestation, sinon 0
CREATE OR REPLACE FUNCTION journal_vente_taux_ligne(p_ligne_id bigint) RETURNS numeric
LANGUAGE sql STABLE AS $$
    SELECT COALESCE(l.taux_tva, p.taux_tva, tp.taux_tva, 0)::numeric(5,2)
    FROM ligne_vente l LEFT JOIN produit p ON p.id = l.produit_id LEFT JOIN type_prestation tp ON tp.id = l.type_prestation_id
    WHERE l.id = p_ligne_id
$$;

-- Paiements d'une vente par mode (« mode:montant » triés par mode, séparés par « ; »), signe appliqué
CREATE OR REPLACE FUNCTION journal_vente_paiements(p_vente_id bigint, p_signe int) RETURNS text
LANGUAGE sql STABLE AS $$
    SELECT COALESCE(string_agg(mode || ':' || (total * p_signe)::numeric(14,2), ';' ORDER BY mode), '')
    FROM (SELECT mode, sum(montant) AS total FROM paiement WHERE vente_id = p_vente_id GROUP BY mode) x
$$;

-- ---- 5. Écriture des lignes (déclencheurs différés) --------------------------
-- Vente encaissée (passage à « validee ») et annulation (passage à « annulee ») : relit la vente à la validation de la transaction
CREATE OR REPLACE FUNCTION journal_vente_depuis_vente() RETURNS trigger
LANGUAGE plpgsql AS $$
DECLARE
    v        vente;
    v_poste  text;
    v_taux   numeric[];
    v_poids  numeric[];
    v_vent   record;
    v_an     annulation_vente;
    v_retour numeric;
BEGIN
    SELECT * INTO v FROM vente WHERE id = NEW.id;
    IF NOT FOUND THEN RETURN NULL; END IF;
    SELECT array_agg(taux ORDER BY taux), array_agg(poids ORDER BY taux) INTO v_taux, v_poids
    FROM (SELECT journal_vente_taux_ligne(l.id) AS taux, sum(l.montant_organisme + l.montant_patient) AS poids
          FROM ligne_vente l WHERE l.vente_id = v.id GROUP BY 1) g;

    -- Vente encaissée : insérée validée, ou passée de « en cours » à validée (ou annulée dans la même transaction)
    IF v.statut IN ('validee', 'annulee') AND (TG_OP = 'INSERT' OR OLD.statut = 'en_cours')
       AND NOT EXISTS (SELECT 1 FROM journal_vente WHERE vente_id = v.id AND evenement = 'vente') THEN
        SELECT poste INTO v_poste FROM session_caisse WHERE id = v.session_caisse_id;
        SELECT * INTO v_vent FROM journal_vente_ventiler(v_taux, v_poids, v.montant_total);
        INSERT INTO journal_vente (evenement, numero_ticket, vente_id, survenu_le, session_caisse_id, poste, utilisateur_id, type_vente,
                                   montant_ttc, montant_ht, montant_tva, detail_tva, part_patient, part_organisme, paiements)
        VALUES ('vente', v.numero, v.id, v.date_vente, v.session_caisse_id, v_poste, v.utilisateur_id, v.type,
                v.montant_total, v_vent.ht, v_vent.tva, v_vent.detail, v.part_patient, v.part_organisme, journal_vente_paiements(v.id, 1));
    END IF;

    -- Annulation : nouvelle ligne (le ticket garde son numéro), au montant encore acquis (déjà rendu par des retours exclu)
    IF v.statut = 'annulee' AND (TG_OP = 'INSERT' OR OLD.statut IS DISTINCT FROM 'annulee')
       AND NOT EXISTS (SELECT 1 FROM journal_vente WHERE vente_id = v.id AND evenement = 'annulation') THEN
        SELECT * INTO v_an FROM annulation_vente WHERE vente_id = v.id ORDER BY id DESC LIMIT 1;
        SELECT COALESCE(sum(r.montant), 0) INTO v_retour
        FROM retour_client_ligne r JOIN ligne_vente l ON l.id = r.ligne_vente_id WHERE l.vente_id = v.id;
        SELECT * INTO v_vent FROM journal_vente_ventiler(v_taux, v_poids, -(v.montant_total - v_retour));
        INSERT INTO journal_vente (evenement, numero_ticket, vente_id, annulation_id, survenu_le, session_caisse_id, poste, utilisateur_id,
                                   type_vente, montant_ttc, montant_ht, montant_tva, detail_tva, part_patient, part_organisme, paiements, motif)
        VALUES ('annulation', v.numero, v.id, v_an.id, COALESCE(v_an.cree_le, now()), v_an.session_caisse_id, v_an.poste,
                v_an.utilisateur_id, v.type, -(v.montant_total - v_retour), v_vent.ht, v_vent.tva, v_vent.detail,
                -(v.part_patient - v_retour), -v.part_organisme,
                CASE WHEN v_an.mode IS NOT NULL AND v_an.montant_rembourse > 0
                     THEN v_an.mode || ':' || (-v_an.montant_rembourse)::numeric(14,2) ELSE '' END,
                left(v_an.motif, 500));
    END IF;
    RETURN NULL;
END;
$$;

DROP TRIGGER IF EXISTS trg_journal_vente_insertion ON vente;
CREATE CONSTRAINT TRIGGER trg_journal_vente_insertion
    AFTER INSERT ON vente DEFERRABLE INITIALLY DEFERRED
    FOR EACH ROW WHEN (NEW.statut <> 'en_cours') EXECUTE FUNCTION journal_vente_depuis_vente();
DROP TRIGGER IF EXISTS trg_journal_vente_statut ON vente;
CREATE CONSTRAINT TRIGGER trg_journal_vente_statut
    AFTER UPDATE OF statut ON vente DEFERRABLE INITIALLY DEFERRED
    FOR EACH ROW WHEN (OLD.statut IS DISTINCT FROM NEW.statut) EXECUTE FUNCTION journal_vente_depuis_vente();

-- Retour client : montant des boîtes rendues (négatif), ventilé selon le taux des lignes rendues
CREATE OR REPLACE FUNCTION journal_vente_depuis_retour() RETURNS trigger
LANGUAGE plpgsql AS $$
DECLARE
    r       retour_client;
    v       vente;
    v_taux  numeric[];
    v_poids numeric[];
    v_total numeric;
    v_vent  record;
BEGIN
    SELECT * INTO r FROM retour_client WHERE id = NEW.id;
    IF NOT FOUND OR EXISTS (SELECT 1 FROM journal_vente WHERE retour_client_id = r.id) THEN RETURN NULL; END IF;
    SELECT * INTO v FROM vente WHERE id = r.vente_id;
    SELECT array_agg(taux ORDER BY taux), array_agg(poids ORDER BY taux), COALESCE(sum(poids), 0) INTO v_taux, v_poids, v_total
    FROM (SELECT journal_vente_taux_ligne(rl.ligne_vente_id) AS taux, sum(rl.montant) AS poids
          FROM retour_client_ligne rl WHERE rl.retour_id = r.id GROUP BY 1) g;
    SELECT * INTO v_vent FROM journal_vente_ventiler(v_taux, v_poids, -v_total);
    INSERT INTO journal_vente (evenement, numero_ticket, vente_id, retour_client_id, survenu_le, session_caisse_id, poste, utilisateur_id,
                               type_vente, montant_ttc, montant_ht, montant_tva, detail_tva, part_patient, part_organisme, paiements, motif)
    VALUES ('retour', v.numero, v.id, r.id, r.cree_le, r.session_caisse_id, r.poste, r.utilisateur_id, v.type,
            -v_total, v_vent.ht, v_vent.tva, v_vent.detail, -v_total, 0,
            CASE WHEN r.mode IS NOT NULL AND r.montant_rembourse > 0 THEN r.mode || ':' || (-r.montant_rembourse)::numeric(14,2) ELSE '' END,
            left(COALESCE(r.motif_code || ' ', '') || COALESCE(r.motif, ''), 500));
    RETURN NULL;
END;
$$;
DROP TRIGGER IF EXISTS trg_journal_vente_retour ON retour_client;
CREATE CONSTRAINT TRIGGER trg_journal_vente_retour
    AFTER INSERT ON retour_client DEFERRABLE INITIALLY DEFERRED
    FOR EACH ROW EXECUTE FUNCTION journal_vente_depuis_retour();

-- Avoir émis à la main, sur trop-payé de régularisation, ou remboursé en espèces : TTC nul, montant dans montant_mouvement
-- (crédit positif, débit négatif). L'avoir né d'une annulation ou d'un retour, et l'avoir utilisé en paiement, figurent déjà
-- dans la ligne de l'annulation, du retour ou de la vente.
CREATE OR REPLACE FUNCTION journal_vente_depuis_avoir() RETURNS trigger
LANGUAGE plpgsql AS $$
DECLARE
    v_numero text;
BEGIN
    IF EXISTS (SELECT 1 FROM journal_vente WHERE avoir_mouvement_id = NEW.id) THEN RETURN NULL; END IF;
    SELECT numero INTO v_numero FROM vente WHERE id = NEW.vente_id;
    INSERT INTO journal_vente (evenement, numero_ticket, vente_id, avoir_mouvement_id, survenu_le, utilisateur_id, montant_mouvement, motif)
    VALUES ('avoir', v_numero, NEW.vente_id, NEW.id, NEW.cree_le, NEW.utilisateur_id,
            CASE WHEN NEW.sens = 'credit' THEN NEW.montant ELSE -NEW.montant END, NEW.origine);
    RETURN NULL;
END;
$$;
DROP TRIGGER IF EXISTS trg_journal_vente_avoir ON avoir_mouvement;
CREATE CONSTRAINT TRIGGER trg_journal_vente_avoir
    AFTER INSERT ON avoir_mouvement DEFERRABLE INITIALLY DEFERRED
    FOR EACH ROW WHEN (NEW.origine IN ('saisie', 'trop_paye_regularisation', 'remboursement'))
    EXECUTE FUNCTION journal_vente_depuis_avoir();

-- Régularisation de la part de l'organisme (Chifa) et son annulation : TTC nul, déplacement entre part organisme et part patient
CREATE OR REPLACE FUNCTION journal_vente_depuis_regularisation() RETURNS trigger
LANGUAGE plpgsql AS $$
DECLARE
    g        regularisation_part_organisme;
    v        vente;
    v_evt    text;
    v_signe  int;
    v_delta  numeric;
BEGIN
    SELECT * INTO g FROM regularisation_part_organisme WHERE id = NEW.id;
    IF NOT FOUND THEN RETURN NULL; END IF;
    v_evt := CASE WHEN TG_OP = 'INSERT' THEN 'regularisation' ELSE 'annulation_regularisation' END;
    IF v_evt = 'annulation_regularisation' AND g.statut = 'active' THEN RETURN NULL; END IF;
    IF EXISTS (SELECT 1 FROM journal_vente WHERE regularisation_id = g.id AND evenement = v_evt) THEN RETURN NULL; END IF;
    SELECT * INTO v FROM vente WHERE id = g.vente_id;
    v_signe := CASE WHEN v_evt = 'regularisation' THEN 1 ELSE -1 END;
    v_delta := (g.part_organisme_chifa - g.part_organisme_vente) * v_signe;
    INSERT INTO journal_vente (evenement, numero_ticket, vente_id, regularisation_id, survenu_le, utilisateur_id, type_vente,
                               part_patient, part_organisme, montant_mouvement, motif)
    VALUES (v_evt, v.numero, v.id, g.id, CASE WHEN v_evt = 'regularisation' THEN g.cree_le ELSE COALESCE(g.annulee_le, now()) END,
            CASE WHEN v_evt = 'regularisation' THEN g.utilisateur_id ELSE g.annulee_par END, v.type,
            -v_delta, v_delta, COALESCE(g.montant_a_rendre, 0) * v_signe,
            left(CASE WHEN v_evt = 'regularisation' THEN g.sort ELSE g.motif_annulation END, 500));
    RETURN NULL;
END;
$$;
DROP TRIGGER IF EXISTS trg_journal_vente_regularisation ON regularisation_part_organisme;
CREATE CONSTRAINT TRIGGER trg_journal_vente_regularisation
    AFTER INSERT ON regularisation_part_organisme DEFERRABLE INITIALLY DEFERRED
    FOR EACH ROW EXECUTE FUNCTION journal_vente_depuis_regularisation();
DROP TRIGGER IF EXISTS trg_journal_vente_regularisation_annulee ON regularisation_part_organisme;
CREATE CONSTRAINT TRIGGER trg_journal_vente_regularisation_annulee
    AFTER UPDATE OF statut ON regularisation_part_organisme DEFERRABLE INITIALLY DEFERRED
    FOR EACH ROW WHEN (OLD.statut IS DISTINCT FROM NEW.statut) EXECUTE FUNCTION journal_vente_depuis_regularisation();

-- ---- 6. Clôtures scellées ----------------------------------------------------
CREATE TABLE IF NOT EXISTS cloture_fiscale (
    id                  bigint        GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    type                text          NOT NULL,
    session_caisse_id   bigint        REFERENCES session_caisse (id),
    periode_du          date          NOT NULL,
    periode_au          date          NOT NULL,
    sceau_id            bigint        NOT NULL REFERENCES sceau_integrite (id),
    evenements          bigint        NOT NULL DEFAULT 0,
    montant_ttc         numeric(14,2) NOT NULL DEFAULT 0,
    montant_ht          numeric(14,2) NOT NULL DEFAULT 0,
    montant_tva         numeric(14,2) NOT NULL DEFAULT 0,
    fichier_archive     text,
    empreinte_archive   bytea,
    cree_le             timestamptz   NOT NULL DEFAULT now(),
    CONSTRAINT ck_cloture_fiscale_type CHECK (type IN ('journaliere', 'mensuelle')),
    CONSTRAINT ck_cloture_fiscale_periode CHECK (periode_au >= periode_du),
    CONSTRAINT ck_cloture_fiscale_session CHECK ((type = 'journaliere') = (session_caisse_id IS NOT NULL))
);
CREATE UNIQUE INDEX IF NOT EXISTS ux_cloture_fiscale_session ON cloture_fiscale (session_caisse_id) WHERE type = 'journaliere';
CREATE UNIQUE INDEX IF NOT EXISTS ux_cloture_fiscale_mois ON cloture_fiscale (periode_du) WHERE type = 'mensuelle';
CREATE INDEX IF NOT EXISTS idx_cloture_fiscale_periode ON cloture_fiscale (periode_du, periode_au);

CREATE OR REPLACE FUNCTION cloture_fiscale_immuable() RETURNS trigger
LANGUAGE plpgsql AS $$
BEGIN
    RAISE EXCEPTION 'Une clôture fiscale ne peut être ni modifiée, ni supprimée, ni vidée';
END;
$$;
CREATE OR REPLACE TRIGGER trg_cloture_fiscale_immuable
    BEFORE UPDATE OR DELETE ON cloture_fiscale FOR EACH ROW EXECUTE FUNCTION cloture_fiscale_immuable();
CREATE OR REPLACE TRIGGER trg_cloture_fiscale_non_vidable
    BEFORE TRUNCATE ON cloture_fiscale FOR EACH STATEMENT EXECUTE FUNCTION cloture_fiscale_immuable();

-- ---- 7. Conservation (6 ans au moins) ----------------------------------------
-- Pièces des ventes de moins de parametre_fiscal.conservation_annees : suppression refusée (journal, sceaux et clôtures : jamais)
CREATE OR REPLACE FUNCTION conservation_fiscale_proteger() RETURNS trigger
LANGUAGE plpgsql AS $$
DECLARE
    v_annees int := COALESCE((SELECT conservation_annees FROM parametre_fiscal WHERE id = 1), 6);
    v_date   timestamptz;
BEGIN
    v_date := CASE TG_TABLE_NAME
        WHEN 'vente' THEN OLD.date_vente
        ELSE (to_jsonb(OLD) ->> 'cree_le')::timestamptz
    END;
    IF v_date IS NULL OR v_date > now() - make_interval(years => v_annees) THEN
        RAISE EXCEPTION 'Conservation fiscale : % de moins de % ans, suppression interdite', TG_TABLE_NAME, v_annees;
    END IF;
    RETURN OLD;
END;
$$;
CREATE OR REPLACE TRIGGER trg_conservation_fiscale BEFORE DELETE ON vente FOR EACH ROW EXECUTE FUNCTION conservation_fiscale_proteger();
CREATE OR REPLACE TRIGGER trg_conservation_fiscale BEFORE DELETE ON paiement FOR EACH ROW EXECUTE FUNCTION conservation_fiscale_proteger();
CREATE OR REPLACE TRIGGER trg_conservation_fiscale BEFORE DELETE ON annulation_vente FOR EACH ROW EXECUTE FUNCTION conservation_fiscale_proteger();
CREATE OR REPLACE TRIGGER trg_conservation_fiscale BEFORE DELETE ON retour_client FOR EACH ROW EXECUTE FUNCTION conservation_fiscale_proteger();

-- ---- 8. Contrôles et récapitulatif -------------------------------------------
-- Continuité depuis la mise en service : n° manquant, n° au-delà du compteur, vente ou annulation sans sa ligne au journal
CREATE OR REPLACE FUNCTION journal_vente_continuite()
RETURNS TABLE (anomalie text, numero text, vente_id bigint)
LANGUAGE sql STABLE AS $$
    WITH c AS (SELECT * FROM compteur_ticket WHERE id = 1),
         v AS (SELECT x.id, x.numero, x.statut, substring(x.numero FROM '^' || (SELECT prefixe FROM c) || '([0-9]+)$')::bigint AS n
               FROM vente x)
    SELECT 'numero_manquant', c.prefixe || lpad(s.n::text, GREATEST(6, char_length(s.n::text)), '0'), NULL::bigint
    FROM c, generate_series(c.premier_numero_continu, c.dernier_numero) AS s(n)
    WHERE NOT EXISTS (SELECT 1 FROM v WHERE v.n = s.n)
    UNION ALL
    SELECT 'numero_hors_compteur', v.numero, v.id FROM v, c WHERE v.n > c.dernier_numero
    UNION ALL
    SELECT 'vente_sans_ligne', v.numero, v.id FROM v, c
    WHERE v.n >= c.premier_numero_continu AND v.statut IN ('validee', 'annulee')
      AND NOT EXISTS (SELECT 1 FROM journal_vente j WHERE j.vente_id = v.id AND j.evenement = 'vente')
    UNION ALL
    SELECT 'annulation_sans_ligne', v.numero, v.id FROM v
    WHERE v.statut = 'annulee' AND EXISTS (SELECT 1 FROM annulation_vente a, compteur_ticket c WHERE a.vente_id = v.id AND a.cree_le >= c.mis_en_service_le)
      AND NOT EXISTS (SELECT 1 FROM journal_vente j WHERE j.vente_id = v.id AND j.evenement = 'annulation')
    ORDER BY 1, 2
$$;

-- Trous de l'historique (avant la mise en service) : signalés, jamais corrigés
CREATE OR REPLACE FUNCTION journal_vente_trous_historique()
RETURNS TABLE (du bigint, au bigint, manquants bigint)
LANGUAGE sql STABLE AS $$
    WITH n AS (SELECT DISTINCT substring(numero FROM '^T-([0-9]+)$')::bigint AS n FROM vente WHERE numero ~ '^T-[0-9]+$'),
         s AS (SELECT n, lag(n) OVER (ORDER BY n) AS p FROM n
               WHERE n < (SELECT premier_numero_continu FROM compteur_ticket WHERE id = 1))
    SELECT COALESCE(p, 0) + 1, n - 1, n - COALESCE(p, 0) - 1 FROM s WHERE n - COALESCE(p, 0) > 1 ORDER BY 1
$$;

-- Récapitulatif TVA d'une période (jours locaux, parametre_fiscal.fuseau) : par taux, TTC, HT, TVA et nombre d'événements
CREATE OR REPLACE FUNCTION journal_vente_recap_tva(p_du date, p_au date)
RETURNS TABLE (taux numeric, montant_ttc numeric, montant_ht numeric, montant_tva numeric, evenements bigint)
LANGUAGE sql STABLE AS $$
    SELECT split_part(d, ':', 1)::numeric(5,2), sum(split_part(d, ':', 2)::numeric), sum(split_part(d, ':', 3)::numeric),
           sum(split_part(d, ':', 4)::numeric), count(DISTINCT j.id)
    FROM journal_vente j CROSS JOIN LATERAL string_to_table(NULLIF(j.detail_tva, ''), ';') AS d
    WHERE (j.survenu_le AT TIME ZONE (SELECT fuseau FROM parametre_fiscal WHERE id = 1))::date BETWEEN p_du AND p_au
    GROUP BY 1 ORDER BY 1
$$;
