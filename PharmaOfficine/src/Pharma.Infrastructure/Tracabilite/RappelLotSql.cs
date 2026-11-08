using Npgsql;
using Pharma.Application.Tracabilite;
using Pharma.Domain.Securite;

namespace Pharma.Infrastructure.Tracabilite;

/// <summary>
/// Rappel de lot en une transaction : fonction SQL rappel_lot (blocage + événements de boîte),
/// puis inscription au journal des actions sensibles.
/// </summary>
public sealed class RappelLotSql(NpgsqlDataSource db) : IRappelLot
{
    private const string AnonymeSql = "Vente anonyme";   // libellé renvoyé par rappel_lot

    public async Task<ResultatRappel?> RappelerAsync(
        long produitId, string numeroLot, string motif, long utilisateurId, CancellationToken ct = default)
    {
        await using var connexion = await db.OpenConnectionAsync(ct);
        await using var tx = await connexion.BeginTransactionAsync(ct);

        // 1. Le lot existe-t-il (chez au moins un grossiste) ?
        string produit;
        int lots, enStock;
        await using (var cmd = new NpgsqlCommand("""
            SELECT p.nom_commercial, count(l.id)::int, COALESCE(SUM(l.quantite_disponible), 0)::int
            FROM produit p
            LEFT JOIN lot l ON l.produit_id = p.id AND l.numero_lot = $2
            WHERE p.id = $1
            GROUP BY p.nom_commercial
            """, connexion, tx))
        {
            cmd.Parameters.AddWithValue(produitId);
            cmd.Parameters.AddWithValue(numeroLot);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            if (!await r.ReadAsync(ct)) return null;
            (produit, lots, enStock) = (r.GetString(0), r.GetInt32(1), r.GetInt32(2));
        }
        if (lots == 0) return null;

        // 2. Blocage et liste des ventes
        var ventes = new List<VenteARappeler>();
        await using (var cmd = new NpgsqlCommand(
            "SELECT o_patient, o_telephone, o_date_vente, o_ticket, o_boites FROM rappel_lot($1, $2, $3, $4)", connexion, tx))
        {
            cmd.Parameters.AddWithValue(produitId);
            cmd.Parameters.AddWithValue(numeroLot);
            cmd.Parameters.AddWithValue(motif);
            cmd.Parameters.AddWithValue(utilisateurId);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                var patient = r.GetString(0);
                ventes.Add(new VenteARappeler(
                    patient, r.IsDBNull(1) ? null : r.GetString(1), r.GetDateTime(2), r.GetString(3),
                    (int)r.GetInt64(4), patient == AnonymeSql));
            }
        }

        // 3. Journal des actions sensibles (non modifiable)
        await using (var cmd = new NpgsqlCommand("""
            INSERT INTO journal_audit (utilisateur_id, action, table_cible, details, permission_code)
            VALUES ($1, 'rappel_lot', 'lot',
                    jsonb_build_object('produit_id', $2, 'numero_lot', $3, 'motif', $4,
                                       'lots_bloques', $5, 'boites_en_stock', $6, 'ventes', $7),
                    $8)
            """, connexion, tx))
        {
            cmd.Parameters.AddWithValue(utilisateurId);
            cmd.Parameters.AddWithValue(produitId);
            cmd.Parameters.AddWithValue(numeroLot);
            cmd.Parameters.AddWithValue(motif);
            cmd.Parameters.AddWithValue(lots);
            cmd.Parameters.AddWithValue(enStock);
            cmd.Parameters.AddWithValue(ventes.Count);
            cmd.Parameters.AddWithValue(Permissions.StockDeclarerPerime);
            await cmd.ExecuteNonQueryAsync(ct);
        }

        // Où retirer les boîtes : emplacement de vente et réserve du produit
        string? ou;
        await using (var cmd = new NpgsqlCommand("""
            SELECT concat_ws(', réserve ', ev.code || COALESCE(' (' || ev.libelle || ')', ''), er.code)
            FROM produit p LEFT JOIN emplacement ev ON ev.id = p.emplacement_id LEFT JOIN emplacement er ON er.id = p.reserve_id
            WHERE p.id = $1
            """, connexion, tx))
        {
            cmd.Parameters.AddWithValue(produitId);
            ou = await cmd.ExecuteScalarAsync(ct) as string;
        }

        await tx.CommitAsync(ct);
        return new ResultatRappel(produit, numeroLot, lots, enStock, ventes, string.IsNullOrEmpty(ou) ? null : ou);
    }
}
