using Npgsql;
using NpgsqlTypes;
using Pharma.Application.Catalogue;
using Pharma.Application.Commun;
using Pharma.Domain.Catalogue;
using Pharma.Domain.Commun;

namespace Pharma.Infrastructure.Lecture;

/// <summary>Indications thérapeutiques (script 104) : jamais supprimées ; libellé unique sans tenir compte de la casse (ux_indication_libelle).</summary>
public sealed class IndicationsSql(NpgsqlDataSource db) : IIndications
{
    private const string Colonnes = """
        i.id, i.libelle, i.actif, (SELECT count(*) FROM produit_indication pi WHERE pi.indication_id = i.id)::int FROM indication i
        """;

    public async Task<Page<Indication>> ListerAsync(bool? actif, string? recherche, DemandePage page, CancellationToken ct = default)
    {
        const string Filtre = """
            ($1::boolean IS NULL OR i.actif = $1)
            AND ($2::text IS NULL OR sans_accent(i.libelle) ILIKE '%' || sans_accent($2) || '%')
            """;
        await using var cx = await db.OpenConnectionAsync(ct);
        void Parametres(NpgsqlCommand c)
        {
            c.Parameters.Add(new NpgsqlParameter { Value = (object?)actif ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Boolean });
            c.Parameters.Add(new NpgsqlParameter { Value = (object?)recherche ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Text });
        }
        int total;
        await using (var compte = new NpgsqlCommand($"SELECT count(*) FROM indication i WHERE {Filtre}", cx))
        {
            Parametres(compte);
            total = (int)(long)(await compte.ExecuteScalarAsync(ct))!;
        }
        await using var cmd = new NpgsqlCommand($"SELECT {Colonnes} WHERE {Filtre} ORDER BY i.libelle LIMIT $3 OFFSET $4", cx);
        Parametres(cmd);
        cmd.Parameters.AddWithValue(page.Taille);
        cmd.Parameters.AddWithValue(page.Decalage);
        var liste = await LireTout(cmd, ct);
        return new Page<Indication>(liste, total, page.Page, page.Taille);
    }

    public async Task<IReadOnlyList<Indication>> ParIdsAsync(IReadOnlyCollection<long> ids, CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand($"SELECT {Colonnes} WHERE i.id = ANY($1) ORDER BY i.libelle");
        cmd.Parameters.AddWithValue(ids.ToArray());
        return await LireTout(cmd, ct);
    }

    public async Task<long> EnregistrerAsync(long? id, string libelle, bool actif, CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand(id is null
            ? "INSERT INTO indication (libelle, actif) VALUES ($1, $2) RETURNING id"
            : "UPDATE indication SET libelle = $1, actif = $2 WHERE id = $3 RETURNING id");
        cmd.Parameters.AddWithValue(libelle);
        cmd.Parameters.AddWithValue(actif);
        if (id is { } i) cmd.Parameters.AddWithValue(i);
        try
        {
            return await cmd.ExecuteScalarAsync(ct) is long nouvel ? nouvel : throw new RegleMetierException("Indication introuvable.");
        }
        catch (PostgresException e) when (e.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            throw new RegleMetierException($"L’indication « {libelle} » existe déjà.", "libelle");
        }
    }

    public async Task<IReadOnlyList<Indication>> DuProduitAsync(long produitId, CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand($"""
            SELECT {Colonnes} WHERE i.id IN (SELECT indication_id FROM produit_indication WHERE produit_id = $1) ORDER BY i.libelle
            """);
        cmd.Parameters.AddWithValue(produitId);
        return await LireTout(cmd, ct);
    }

    public async Task RemplacerDuProduitAsync(long produitId, IReadOnlyList<long> indicationIds, CancellationToken ct = default)
    {
        await using var cx = await db.OpenConnectionAsync(ct);
        await using var tx = await cx.BeginTransactionAsync(ct);
        await using (var verif = new NpgsqlCommand("SELECT 1 FROM produit WHERE id = $1 FOR UPDATE", cx, tx))
        {
            verif.Parameters.AddWithValue(produitId);
            if (await verif.ExecuteScalarAsync(ct) is null) throw new RegleMetierException("Produit introuvable.");
        }
        await using (var suppr = new NpgsqlCommand("DELETE FROM produit_indication WHERE produit_id = $1 AND NOT (indication_id = ANY($2))", cx, tx))
        {
            suppr.Parameters.AddWithValue(produitId);
            suppr.Parameters.AddWithValue(indicationIds.ToArray());
            await suppr.ExecuteNonQueryAsync(ct);
        }
        await using (var ajout = new NpgsqlCommand("""
            INSERT INTO produit_indication (produit_id, indication_id) SELECT $1, unnest($2::bigint[]) ON CONFLICT DO NOTHING
            """, cx, tx))
        {
            ajout.Parameters.AddWithValue(produitId);
            ajout.Parameters.AddWithValue(indicationIds.ToArray());
            await ajout.ExecuteNonQueryAsync(ct);
        }
        await tx.CommitAsync(ct);
    }

    private static async Task<IReadOnlyList<Indication>> LireTout(NpgsqlCommand cmd, CancellationToken ct)
    {
        var liste = new List<Indication>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) liste.Add(new Indication(r.GetInt64(0), r.GetString(1), r.GetBoolean(2), r.GetInt32(3)));
        return liste;
    }
}
