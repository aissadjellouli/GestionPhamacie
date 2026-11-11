using Npgsql;
using Pharma.Application.Tracabilite;
using Pharma.Domain.Securite;

namespace Pharma.Infrastructure.Tracabilite;

/// <summary>
/// Rappel de lot en une transaction : fonction SQL rappel_lot (blocage des lots encore vendables + événements de boîte),
/// puis inscription au journal des actions sensibles. Consultation : fonction SQL ventes_lot, sans rien modifier.
/// </summary>
public sealed class RappelLotSql(NpgsqlDataSource db) : IRappelLot
{
    private const string AnonymeSql = "Vente anonyme";   // libellé renvoyé par ventes_lot

    private sealed record EtatLot(string Produit, int Bloques, int NonBloques, int EnStock);

    public async Task<ResultatRappel?> RappelerAsync(
        long produitId, string numeroLot, string motif, long utilisateurId, CancellationToken ct = default)
    {
        // Les lots sont enregistrés en majuscules sans espaces (018) : la même valeur sert à la recherche, au blocage et au journal
        numeroLot = Pharma.Domain.Stock.RegleLot.Normaliser(numeroLot);
        await using var connexion = await db.OpenConnectionAsync(ct);
        await using var tx = await connexion.BeginTransactionAsync(ct);

        // 1. Lots de ce numéro verrouillés : deux rappels simultanés ne bloquent et ne journalisent qu'une fois
        await using (var cmd = new NpgsqlCommand(
            "SELECT id FROM lot WHERE produit_id = $1 AND numero_lot = $2 ORDER BY id FOR UPDATE", connexion, tx))
        {
            cmd.Parameters.AddWithValue(produitId);
            cmd.Parameters.AddWithValue(numeroLot);
            await cmd.ExecuteNonQueryAsync(ct);
        }
        if (await EtatAsync(connexion, tx, produitId, numeroLot, ct) is not { } etat || etat.Bloques + etat.NonBloques == 0) return null;
        // Déjà bloqué partout : rien n'est écrit (la transaction est abandonnée)
        if (etat.NonBloques == 0) return new ResultatRappel(etat.Produit, numeroLot, 0, etat.EnStock, []);

        // 2. Blocage des lots encore vendables et liste des ventes
        var ventes = new List<VenteARappeler>();
        await using (var cmd = new NpgsqlCommand(
            "SELECT o_patient, o_telephone, o_date_vente, o_ticket, o_boites FROM rappel_lot($1, $2, $3, $4)", connexion, tx))
        {
            cmd.Parameters.AddWithValue(produitId);
            cmd.Parameters.AddWithValue(numeroLot);
            cmd.Parameters.AddWithValue(motif);
            cmd.Parameters.AddWithValue(utilisateurId);
            await LireVentesAsync(cmd, ventes, ct);
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
            cmd.Parameters.AddWithValue(etat.NonBloques);
            cmd.Parameters.AddWithValue(etat.EnStock);
            cmd.Parameters.AddWithValue(ventes.Count);
            cmd.Parameters.AddWithValue(Permissions.StockDeclarerPerime);
            await cmd.ExecuteNonQueryAsync(ct);
        }

        var ou = await EmplacementsAsync(connexion, tx, produitId, ct);
        await tx.CommitAsync(ct);
        return new ResultatRappel(etat.Produit, numeroLot, etat.NonBloques, etat.EnStock, ventes, ou, Motif: motif);
    }

    public async Task<ResultatRappel?> ConsulterAsync(long produitId, string numeroLot, CancellationToken ct = default)
    {
        numeroLot = Pharma.Domain.Stock.RegleLot.Normaliser(numeroLot);
        await using var connexion = await db.OpenConnectionAsync(ct);
        if (await EtatAsync(connexion, null, produitId, numeroLot, ct) is not { } etat || etat.Bloques + etat.NonBloques == 0) return null;

        var ventes = new List<VenteARappeler>();
        await using (var cmd = new NpgsqlCommand(
            "SELECT o_patient, o_telephone, o_date_vente, o_ticket, o_boites FROM ventes_lot($1, $2)", connexion))
        {
            cmd.Parameters.AddWithValue(produitId);
            cmd.Parameters.AddWithValue(numeroLot);
            await LireVentesAsync(cmd, ventes, ct);
        }

        // Premier rappel inscrit au journal ; à défaut (lot entré bloqué à la réception…), motif du blocage du lot
        DateTime? le = null;
        string? motif = null;
        await using (var cmd = new NpgsqlCommand("""
            SELECT j.cree_le, j.details->>'motif'
            FROM journal_audit j
            WHERE j.action = 'rappel_lot' AND j.details->>'produit_id' = $1::text AND j.details->>'numero_lot' = $2
            UNION ALL
            SELECT NULL, l.motif_blocage FROM lot l
            WHERE l.produit_id = $1 AND l.numero_lot = $2 AND l.bloque AND l.motif_blocage IS NOT NULL
            ORDER BY 1 NULLS LAST
            LIMIT 1
            """, connexion))
        {
            cmd.Parameters.AddWithValue(produitId);
            cmd.Parameters.AddWithValue(numeroLot);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            if (await r.ReadAsync(ct))
                (le, motif) = (r.IsDBNull(0) ? null : r.GetDateTime(0), r.IsDBNull(1) ? null : r.GetString(1));
        }

        return new ResultatRappel(etat.Produit, numeroLot, etat.Bloques, etat.EnStock, ventes,
            await EmplacementsAsync(connexion, null, produitId, ct), etat.NonBloques, le, motif);
    }

    /// <summary>Produit, lots de ce numéro bloqués et encore vendables, boîtes en stock. Null : produit inconnu.</summary>
    private static async Task<EtatLot?> EtatAsync(NpgsqlConnection connexion, NpgsqlTransaction? tx, long produitId, string numeroLot, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand("""
            SELECT p.nom_commercial, count(l.id) FILTER (WHERE l.bloque)::int, count(l.id) FILTER (WHERE NOT l.bloque)::int,
                   COALESCE(SUM(l.quantite_disponible), 0)::int
            FROM produit p
            LEFT JOIN lot l ON l.produit_id = p.id AND l.numero_lot = $2
            WHERE p.id = $1
            GROUP BY p.nom_commercial
            """, connexion, tx);
        cmd.Parameters.AddWithValue(produitId);
        cmd.Parameters.AddWithValue(numeroLot);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        return await r.ReadAsync(ct) ? new EtatLot(r.GetString(0), r.GetInt32(1), r.GetInt32(2), r.GetInt32(3)) : null;
    }

    private static async Task LireVentesAsync(NpgsqlCommand cmd, List<VenteARappeler> ventes, CancellationToken ct)
    {
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            var patient = r.GetString(0);
            ventes.Add(new VenteARappeler(
                patient, r.IsDBNull(1) ? null : r.GetString(1), r.GetDateTime(2), r.GetString(3),
                (int)r.GetInt64(4), patient == AnonymeSql));
        }
    }

    /// <summary>Où retirer les boîtes : emplacement de vente et réserve du produit.</summary>
    private static async Task<string?> EmplacementsAsync(NpgsqlConnection connexion, NpgsqlTransaction? tx, long produitId, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand("""
            SELECT concat_ws(', réserve ', ev.code || COALESCE(' (' || ev.libelle || ')', ''), er.code)
            FROM produit p LEFT JOIN emplacement ev ON ev.id = p.emplacement_id LEFT JOIN emplacement er ON er.id = p.reserve_id
            WHERE p.id = $1
            """, connexion, tx);
        cmd.Parameters.AddWithValue(produitId);
        return await cmd.ExecuteScalarAsync(ct) as string is { Length: > 0 } ou ? ou : null;
    }
}
