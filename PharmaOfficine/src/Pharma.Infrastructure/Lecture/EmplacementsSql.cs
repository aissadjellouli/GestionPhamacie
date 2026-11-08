using Npgsql;
using NpgsqlTypes;
using Pharma.Application.Stock;
using Pharma.Domain.Stock;

namespace Pharma.Infrastructure.Lecture;

public sealed class EmplacementsSql(NpgsqlDataSource db) : IEmplacements
{
    public async Task<IReadOnlyList<Emplacement>> ListerAsync(CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand("""
            SELECT e.id, e.code, e.rayon, e.etagere, e.tiroir, e.libelle, e.zone, e.actif,
                   (SELECT count(*) FROM produit p WHERE p.emplacement_id = e.id OR p.reserve_id = e.id)::int
            FROM emplacement e
            ORDER BY e.zone DESC, e.rayon, e.code
            """);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        var liste = new List<Emplacement>();
        while (await r.ReadAsync(ct))
            liste.Add(new Emplacement(r.GetInt64(0), r.GetString(1), r.GetString(2), Texte(r, 3), Texte(r, 4), Texte(r, 5),
                RegleEmplacement.ZoneDepuisCode(r.GetString(6)), r.GetBoolean(7), r.GetInt32(8)));
        return liste;
    }

    public async Task<bool> CodeExisteAsync(string code, long? saufId, CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand("SELECT EXISTS (SELECT 1 FROM emplacement WHERE code = $1 AND ($2::bigint IS NULL OR id <> $2))");
        cmd.Parameters.AddWithValue(code);
        cmd.Parameters.Add(Nul(saufId, NpgsqlDbType.Bigint));
        return await cmd.ExecuteScalarAsync(ct) is true;
    }

    public async Task<long> CreerAsync(string code, string rayon, string? etagere, string? tiroir, string? libelle, ZoneEmplacement zone, CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand("""
            INSERT INTO emplacement (code, rayon, etagere, tiroir, libelle, zone) VALUES ($1, $2, $3, $4, $5, $6) RETURNING id
            """);
        Parametres(cmd, code, rayon, etagere, tiroir, libelle, zone);
        return (long)(await cmd.ExecuteScalarAsync(ct))!;
    }

    public async Task<bool> ModifierAsync(long id, string code, string rayon, string? etagere, string? tiroir, string? libelle, ZoneEmplacement zone, bool actif, CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand("""
            UPDATE emplacement SET code = $1, rayon = $2, etagere = $3, tiroir = $4, libelle = $5, zone = $6, actif = $7 WHERE id = $8
            """);
        Parametres(cmd, code, rayon, etagere, tiroir, libelle, zone);
        cmd.Parameters.AddWithValue(actif);
        cmd.Parameters.AddWithValue(id);
        return await cmd.ExecuteNonQueryAsync(ct) > 0;
    }

    public async Task<string?> NomProduitAsync(long produitId, CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand("SELECT nom_commercial FROM produit WHERE id = $1");
        cmd.Parameters.AddWithValue(produitId);
        return await cmd.ExecuteScalarAsync(ct) as string;
    }

    public async Task RangerAsync(long produitId, long? emplacementId, long? reserveId, CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand("UPDATE produit SET emplacement_id = $2, reserve_id = $3 WHERE id = $1");
        cmd.Parameters.AddWithValue(produitId);
        cmd.Parameters.Add(Nul(emplacementId, NpgsqlDbType.Bigint));
        cmd.Parameters.Add(Nul(reserveId, NpgsqlDbType.Bigint));
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static void Parametres(NpgsqlCommand cmd, string code, string rayon, string? etagere, string? tiroir, string? libelle, ZoneEmplacement zone)
    {
        cmd.Parameters.AddWithValue(code);
        cmd.Parameters.AddWithValue(rayon);
        cmd.Parameters.Add(Nul(etagere, NpgsqlDbType.Text));
        cmd.Parameters.Add(Nul(tiroir, NpgsqlDbType.Text));
        cmd.Parameters.Add(Nul(libelle, NpgsqlDbType.Text));
        cmd.Parameters.AddWithValue(zone.Code());
    }

    private static string? Texte(NpgsqlDataReader r, int i) => r.IsDBNull(i) ? null : r.GetString(i);

    private static NpgsqlParameter Nul<T>(T? valeur, NpgsqlDbType type) =>
        new() { Value = (object?)valeur ?? DBNull.Value, NpgsqlDbType = type };
}
