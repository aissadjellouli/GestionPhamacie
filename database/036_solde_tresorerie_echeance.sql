-- =====================================================================
-- Solde de trésorerie : un effet ne sort de la banque qu'à son échéance
--   * avant : la vue v_solde_tresorerie (033) retenait l'effet dès que son
--     échéance était postérieure à la date de départ du compte, sans borne
--     haute ; un effet à échéance future diminuait donc le solde dès sa
--     saisie ;
--   * maintenant : un effet n'est compté que lorsque son échéance est
--     atteinte (échéance <= date du jour). Le reste de la vue est inchangé.
-- Idempotent. A exécuter après 035_mot_de_passe_a_changer.sql.
-- =====================================================================

CREATE OR REPLACE VIEW v_solde_tresorerie AS
SELECT c.id, c.type, c.nom, c.banque, c.numero_compte, c.recoit_carte, c.actif, c.solde_initial, c.date_initiale,
       c.solde_initial
       -- Mouvements saisis
       + COALESCE((SELECT SUM(CASE WHEN m.sens = 'entree' THEN m.montant ELSE -m.montant END)
                   FROM mouvement_tresorerie m WHERE m.compte_id = c.id AND m.date_mouvement >= c.date_initiale), 0)
       -- Paiements par carte (ventes encaissées ou annulées, remboursements de crédit)
       + CASE WHEN c.recoit_carte THEN COALESCE((
             SELECT SUM(p.montant) FROM paiement p
             WHERE p.mode = 'carte' AND p.cree_le >= c.date_initiale
               AND (p.vente_id IS NULL OR EXISTS (SELECT 1 FROM vente v WHERE v.id = p.vente_id AND v.statut IN ('validee', 'annulee')))), 0) ELSE 0 END
       -- Remises de chèques
       + COALESCE((SELECT SUM(p.montant) FROM remise_cheques r JOIN remise_cheque_paiement rp ON rp.remise_id = r.id JOIN paiement p ON p.id = rp.paiement_id
                   WHERE r.compte_id = c.id AND r.date_remise >= c.date_initiale), 0)
       -- Règlements des grossistes : espèces du coffre (hors complément du tiroir), autres moyens de la banque choisie ;
       -- un effet seulement une fois son échéance atteinte
       - COALESCE((SELECT SUM(CASE WHEN g.moyen = 'especes' THEN g.montant - g.part_tiroir ELSE g.montant END)
                   FROM reglement_fournisseur g
                   WHERE g.montant > g.part_tiroir
                     AND ((g.moyen = 'especes' AND c.type = 'coffre') OR (g.moyen <> 'especes' AND g.compte_id = c.id))
                     AND CASE WHEN g.moyen = 'effet' THEN g.echeance ELSE g.date_reglement END >= c.date_initiale
                     AND (g.moyen <> 'effet' OR g.echeance <= CURRENT_DATE)), 0)
       -- Remboursements par carte (annulations et retours)
       - CASE WHEN c.recoit_carte THEN
             COALESCE((SELECT SUM(montant_rembourse) FROM annulation_vente WHERE mode = 'carte' AND montant_rembourse > 0 AND cree_le >= c.date_initiale), 0)
           + COALESCE((SELECT SUM(montant_rembourse) FROM retour_client WHERE mode = 'carte' AND montant_rembourse > 0 AND cree_le >= c.date_initiale), 0)
         ELSE 0 END AS solde
FROM compte_tresorerie c;
