using Npgsql;
using Pharma.Application.Caisse;

namespace Pharma.Infrastructure.Caisse;

public sealed class TicketSql(NpgsqlDataSource db) : ITicketLecture
{
    public async Task<TicketVente?> LireAsync(long venteId, CancellationToken ct = default)
    {
        TicketVente? t;
        await using (var cmd = db.CreateCommand($"""
            SELECT v.numero, v.date_vente, u.nom_complet, s.poste, v.statut, o.code, CASE WHEN v.reste_saisi THEN NULL ELSE v.taux_tiers_payant END,
                   v.montant_total + v.montant_remise + v.montant_don, v.montant_remise, v.part_organisme, v.part_patient,
                   (SELECT p.mode FROM paiement p WHERE p.vente_id = v.id AND p.mode <> 'avoir' ORDER BY p.id LIMIT 1), v.montant_don,
                   -- Écart Chifa payé par le patient : notre prix gardé au-dessus du prix du volet
                   (SELECT COALESCE(SUM((l.prix_unitaire - l.prix_chifa) * l.quantite), 0) FROM ligne_vente l
                    WHERE l.vente_id = v.id AND l.prix_chifa IS NOT NULL AND l.prix_unitaire > l.prix_chifa),
                   {RemiseVenteSql.Titulaire}, {RemiseVenteSql.RemisA}, {RemiseVenteSql.Pour},
                   -- Majoration Chifa (script 100, D-3) : à la charge de l'organisme, ajoutée à sa part
                   v.chifa_majoration_montant,
                   -- Part payée par l'avoir du patient (script 106)
                   (SELECT COALESCE(SUM(p.montant), 0) FROM paiement p WHERE p.vente_id = v.id AND p.mode = 'avoir')
            FROM vente v
            JOIN utilisateur u ON u.id = v.utilisateur_id
            LEFT JOIN session_caisse s ON s.id = v.session_caisse_id
            LEFT JOIN organisme o ON o.id = v.organisme_id
            WHERE v.id = $1
            """))
        {
            cmd.Parameters.AddWithValue(venteId);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            if (!await r.ReadAsync(ct)) return null;
            t = new TicketVente(r.GetString(0), r.GetDateTime(1), r.GetString(2), r.IsDBNull(3) ? null : r.GetString(3), r.GetString(4),
                r.IsDBNull(5) ? null : r.GetString(5), r.IsDBNull(6) ? null : r.GetDecimal(6), [],
                r.GetDecimal(7), r.GetDecimal(8), r.GetDecimal(9), r.GetDecimal(10), r.IsDBNull(11) ? null : r.GetString(11),
                null, null, null, null, null, r.GetDecimal(12), r.GetDecimal(13),
                // « Remis à » / « Pour » (script 091) : seul cas où un nom figure sur le ticket
                Pharma.Application.Caisse.RemiseVente.Complement(S(r, 15), S(r, 16)) is null ? null : Pharma.Application.Caisse.RemiseVente.Libelle(S(r, 14), S(r, 15), S(r, 16)),
                r.GetDecimal(17), r.GetDecimal(18));
            static string? S(NpgsqlDataReader r, int i) => r.IsDBNull(i) ? null : r.GetString(i);
        }

        var lignes = new List<LigneTicket>();
        await using (var cmd = db.CreateCommand("""
            SELECT COALESCE(p.nom_commercial, l.libelle), l.quantite, l.prix_unitaire, l.prix_unitaire * l.quantite, l.montant_organisme, l.shp,
                   -- Prestation (script 105) : hors médicaments
                   CASE WHEN l.produit_id IS NULL THEN false
                        ELSE COALESCE((SELECT c.medicament FROM categorie_produit c WHERE c.code = p.categorie), true) END,
                   l.reduction_auto + l.reduction_manuelle,
                   CASE WHEN l.reduction_manuelle > 0 THEN LEAST(l.remise_auto_pct + COALESCE(NULLIF(v.remise_pct, 0), 0), 100) ELSE l.remise_auto_pct END,
                   l.don_partiel,
                   -- Prestation : taux de TVA figé, imprimé sur sa ligne
                   l.taux_tva
            FROM ligne_vente l LEFT JOIN produit p ON p.id = l.produit_id JOIN vente v ON v.id = l.vente_id
            WHERE l.vente_id = $1 ORDER BY l.id
            """))
        {
            cmd.Parameters.AddWithValue(venteId);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
                lignes.Add(new LigneTicket(r.GetString(0), r.GetInt32(1), r.GetDecimal(2), r.GetDecimal(3), r.GetDecimal(4), r.GetDecimal(5), r.GetBoolean(6),
                    r.GetDecimal(7), r.GetDecimal(8), r.GetBoolean(9), r.IsDBNull(10) ? null : r.GetDecimal(10)));
        }
        // Retours clients déjà enregistrés sur cette vente : rappelés sur le ticket réimprimé
        var retours = new List<string>();
        await using (var cmd = db.CreateCommand("""
            -- Un retour partagé entre dette et espèces est écrit en deux lignes (même instant) : regroupées ici
            SELECT rc.cree_le,
                   (SELECT string_agg(p.nom_commercial || ' × ' || rl.quantite, ', ' ORDER BY rl.id)
                    FROM retour_client r2
                    JOIN retour_client_ligne rl ON rl.retour_id = r2.id
                    JOIN ligne_vente l ON l.id = rl.ligne_vente_id
                    JOIN produit p ON p.id = l.produit_id
                    WHERE r2.vente_id = v.id AND r2.cree_le = rc.cree_le),
                   COALESCE(SUM(rc.montant_rembourse) FILTER (WHERE rc.mode IS DISTINCT FROM 'credit'), 0),
                   COALESCE(SUM(rc.montant_rembourse) FILTER (WHERE rc.mode = 'credit'), 0)
            FROM retour_client rc
            JOIN vente v ON v.id = rc.vente_id
            WHERE v.numero = $1
            GROUP BY v.id, rc.cree_le
            ORDER BY rc.cree_le
            """))
        {
            cmd.Parameters.AddWithValue(t.Numero);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                var (rendu, deduit) = (r.GetDecimal(2), r.GetDecimal(3));
                retours.Add($"Retour du {r.GetDateTime(0).ToLocalTime():dd/MM/yyyy HH:mm} : {(r.IsDBNull(1) ? "produits" : r.GetString(1))}"
                    + (rendu > 0 || deduit == 0 ? $" · rendu {rendu:N2} DA" : "") + (deduit > 0 ? $" · déduit de la dette {deduit:N2} DA" : ""));
            }
        }
        return t with { Lignes = lignes, Retours = retours };
    }
}
