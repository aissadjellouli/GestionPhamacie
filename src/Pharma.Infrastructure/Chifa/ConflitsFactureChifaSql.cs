using Npgsql;
using Pharma.Application.Chifa;

namespace Pharma.Infrastructure.Chifa;

/// <summary>Conflits de n° de facture Chifa (table vente_chifa_facture_conflit, script 101) ; seule chifa_facture est écrite.</summary>
public sealed class ConflitsFactureChifaSql(NpgsqlDataSource db) : IConflitsFactureChifa
{
    private const string Requete = """
        SELECT c.vente_id, v.numero, v.date_vente, c.num_facture_chifa, c.chifa_facture, c.valeur_retenue, c.tranche_le, u.nom_complet
        FROM vente_chifa_facture_conflit c
        JOIN vente v ON v.id = c.vente_id
        LEFT JOIN utilisateur u ON u.id = c.tranche_par
        """;

    public async Task<IReadOnlyList<ConflitFactureChifa>> ListerAsync(bool aTrancher, CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand(Requete + " WHERE NOT $1 OR c.tranche_le IS NULL ORDER BY c.constate_le, c.vente_id");
        cmd.Parameters.AddWithValue(aTrancher);
        return await LireAsync(cmd, ct);
    }

    public async Task<ConflitFactureChifa?> LireAsync(long venteId, CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand(Requete + " WHERE c.vente_id = $1");
        cmd.Parameters.AddWithValue(venteId);
        return (await LireAsync(cmd, ct)).FirstOrDefault();
    }

    public async Task<IssueTrancheFacture> TrancherAsync(long venteId, string valeurRetenue, long utilisateurId, CancellationToken ct = default)
    {
        await using var cx = await db.OpenConnectionAsync(ct);
        await using var tx = await cx.BeginTransactionAsync(ct);
        await using (var verrou = new NpgsqlCommand("SELECT tranche_le IS NOT NULL FROM vente_chifa_facture_conflit WHERE vente_id = $1 FOR UPDATE", cx, tx))
        {
            verrou.Parameters.AddWithValue(venteId);
            var tranche = await verrou.ExecuteScalarAsync(ct);
            if (tranche is null) return IssueTrancheFacture.Introuvable;
            if ((bool)tranche) return IssueTrancheFacture.DejaTranche;
        }
        await using (var pris = new NpgsqlCommand("SELECT 1 FROM vente WHERE chifa_facture = $1 AND id <> $2 AND statut <> 'annulee' LIMIT 1", cx, tx))
        {
            pris.Parameters.AddWithValue(valeurRetenue);
            pris.Parameters.AddWithValue(venteId);
            if (await pris.ExecuteScalarAsync(ct) is not null) return IssueTrancheFacture.NumeroDejaPris;
        }
        await using (var maj = new NpgsqlCommand("UPDATE vente SET chifa_facture = $2 WHERE id = $1", cx, tx))
        {
            maj.Parameters.AddWithValue(venteId);
            maj.Parameters.AddWithValue(valeurRetenue);
            try { await maj.ExecuteNonQueryAsync(ct); }
            catch (PostgresException e) when (e.SqlState == PostgresErrorCodes.UniqueViolation) { return IssueTrancheFacture.NumeroDejaPris; }
        }
        await using (var marque = new NpgsqlCommand(
            "UPDATE vente_chifa_facture_conflit SET tranche_le = now(), tranche_par = $2, valeur_retenue = $3 WHERE vente_id = $1", cx, tx))
        {
            marque.Parameters.AddWithValue(venteId);
            marque.Parameters.AddWithValue(utilisateurId);
            marque.Parameters.AddWithValue(valeurRetenue);
            await marque.ExecuteNonQueryAsync(ct);
        }
        await tx.CommitAsync(ct);
        return IssueTrancheFacture.Ecrit;
    }

    private static async Task<IReadOnlyList<ConflitFactureChifa>> LireAsync(NpgsqlCommand cmd, CancellationToken ct)
    {
        var liste = new List<ConflitFactureChifa>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
            liste.Add(new ConflitFactureChifa(r.GetInt64(0), r.GetString(1), r.GetDateTime(2), r.GetString(3),
                r.IsDBNull(4) ? null : r.GetString(4), r.IsDBNull(5) ? null : r.GetString(5),
                r.IsDBNull(6) ? null : r.GetDateTime(6), r.IsDBNull(7) ? null : r.GetString(7)));
        return liste;
    }
}
