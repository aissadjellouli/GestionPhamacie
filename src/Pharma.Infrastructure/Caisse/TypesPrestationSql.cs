using Npgsql;
using NpgsqlTypes;
using Pharma.Application.Caisse;
using Pharma.Application.Commun;
using Pharma.Domain.Commun;
using Pharma.Domain.Ventes;

namespace Pharma.Infrastructure.Caisse;

/// <summary>Types de prestation (script 105) : jamais supprimés ; code unique sans tenir compte de la casse (ux_type_prestation_code).</summary>
public sealed class TypesPrestationSql(NpgsqlDataSource db) : ITypesPrestation
{
    private const string Colonnes = "t.id, t.code, t.libelle, t.prix, t.taux_tva, t.actif, t.remboursable, t.tarif_reference FROM type_prestation t";

    public async Task<Page<TypePrestation>> ListerAsync(bool? actif, string? recherche, DemandePage page, CancellationToken ct = default)
    {
        const string Filtre = """
            ($1::boolean IS NULL OR t.actif = $1)
            AND ($2::text IS NULL OR t.code ILIKE '%' || $2 || '%' OR t.libelle ILIKE '%' || $2 || '%')
            """;
        await using var cx = await db.OpenConnectionAsync(ct);
        void Parametres(NpgsqlCommand c)
        {
            c.Parameters.Add(new NpgsqlParameter { Value = (object?)actif ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Boolean });
            c.Parameters.Add(new NpgsqlParameter { Value = (object?)recherche ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Text });
        }
        int total;
        await using (var compte = new NpgsqlCommand($"SELECT count(*) FROM type_prestation t WHERE {Filtre}", cx))
        {
            Parametres(compte);
            total = (int)(long)(await compte.ExecuteScalarAsync(ct))!;
        }
        await using var cmd = new NpgsqlCommand($"SELECT {Colonnes} WHERE {Filtre} ORDER BY t.libelle, t.code LIMIT $3 OFFSET $4", cx);
        Parametres(cmd);
        cmd.Parameters.AddWithValue(page.Taille);
        cmd.Parameters.AddWithValue(page.Decalage);
        var liste = new List<TypePrestation>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) liste.Add(Lire(r));
        return new Page<TypePrestation>(liste, total, page.Page, page.Taille);
    }

    public async Task<TypePrestation?> LireAsync(long id, CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand($"SELECT {Colonnes} WHERE t.id = $1");
        cmd.Parameters.AddWithValue(id);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        return await r.ReadAsync(ct) ? Lire(r) : null;
    }

    /// <summary>Types de prestation d'identifiants donnés (vente : actifs ou non, la vente contrôle).</summary>
    public static async Task<IReadOnlyDictionary<long, TypePrestation>> ParIdsAsync(NpgsqlDataSource db, IEnumerable<long> ids, CancellationToken ct)
    {
        await using var cmd = db.CreateCommand($"SELECT {Colonnes} WHERE t.id = ANY($1)");
        cmd.Parameters.AddWithValue(ids.ToArray());
        var types = new Dictionary<long, TypePrestation>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) { var t = Lire(r); types[t.Id] = t; }
        return types;
    }

    public async Task<long> EnregistrerAsync(long? id, string code, string libelle, decimal prix, decimal tauxTva, bool actif,
        bool remboursable, decimal? tarifReference, CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand(id is null
            ? "INSERT INTO type_prestation (code, libelle, prix, taux_tva, actif, remboursable, tarif_reference) VALUES ($1, $2, $3, $4, $5, $6, $7) RETURNING id"
            : "UPDATE type_prestation SET code = $1, libelle = $2, prix = $3, taux_tva = $4, actif = $5, remboursable = $6, tarif_reference = $7 WHERE id = $8 RETURNING id");
        cmd.Parameters.AddWithValue(code);
        cmd.Parameters.AddWithValue(libelle);
        cmd.Parameters.AddWithValue(prix);
        cmd.Parameters.AddWithValue(tauxTva);
        cmd.Parameters.AddWithValue(actif);
        cmd.Parameters.AddWithValue(remboursable);
        cmd.Parameters.Add(new NpgsqlParameter { Value = (object?)tarifReference ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Numeric });
        if (id is { } i) cmd.Parameters.AddWithValue(i);
        try
        {
            return await cmd.ExecuteScalarAsync(ct) is long nouvel ? nouvel : throw new RegleMetierException("Prestation introuvable.", "prestation");
        }
        catch (PostgresException e) when (e.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            throw new RegleMetierException($"Le code de prestation « {code} » existe déjà.", "code");
        }
    }

    private static TypePrestation Lire(NpgsqlDataReader r) =>
        new(r.GetInt64(0), r.GetString(1), r.GetString(2), r.GetDecimal(3), r.GetDecimal(4), r.GetBoolean(5),
            r.GetBoolean(6), r.IsDBNull(7) ? null : r.GetDecimal(7));
}
