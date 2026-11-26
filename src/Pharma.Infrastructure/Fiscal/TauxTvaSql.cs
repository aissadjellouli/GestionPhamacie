using Npgsql;
using NpgsqlTypes;
using Pharma.Application.Fiscal;

namespace Pharma.Infrastructure.Fiscal;

/// <summary>Taux de TVA réglables (script 171) : tables taux_tva et taux_tva_categorie.</summary>
public sealed class TauxTvaSql(NpgsqlDataSource db) : ITauxTva
{
    public async Task<ListeTauxTva> ListerAsync(CancellationToken ct = default)
    {
        var taux = new List<TauxTvaLu>();
        await using (var cmd = db.CreateCommand("""
            SELECT t.id, t.taux, t.libelle, t.actif, (SELECT count(*)::int FROM produit p WHERE p.taux_tva = t.taux)
            FROM taux_tva t ORDER BY t.taux
            """))
        await using (var r = await cmd.ExecuteReaderAsync(ct))
            while (await r.ReadAsync(ct)) taux.Add(new TauxTvaLu(r.GetInt64(0), r.GetDecimal(1), r.GetString(2), r.GetBoolean(3), r.GetInt32(4)));

        var categories = new List<TauxTvaCategorie>();
        await using (var cmd = db.CreateCommand("""
            SELECT c.code, c.libelle, d.taux FROM categorie_produit c LEFT JOIN taux_tva_categorie d ON d.categorie_code = c.code ORDER BY c.ordre, c.code
            """))
        await using (var r = await cmd.ExecuteReaderAsync(ct))
            while (await r.ReadAsync(ct)) categories.Add(new TauxTvaCategorie(r.GetString(0), r.GetString(1), r.IsDBNull(2) ? null : r.GetDecimal(2)));

        // Taux enregistrés sur des produits ou des prestations sans être dans la liste : signalés, jamais modifiés
        var hors = new List<(decimal, int)>();
        await using (var cmd = db.CreateCommand("""
            SELECT x.taux, count(*)::int FROM (SELECT taux_tva AS taux FROM produit UNION ALL SELECT taux_tva FROM type_prestation) x
            WHERE NOT EXISTS (SELECT 1 FROM taux_tva t WHERE t.taux = x.taux) GROUP BY x.taux ORDER BY x.taux
            """))
        await using (var r = await cmd.ExecuteReaderAsync(ct))
            while (await r.ReadAsync(ct)) hors.Add((r.GetDecimal(0), r.GetInt32(1)));
        return new ListeTauxTva(taux, categories, hors);
    }

    public async Task<long?> AjouterAsync(decimal taux, string libelle, CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand("INSERT INTO taux_tva (taux, libelle) VALUES ($1, $2) ON CONFLICT (taux) DO NOTHING RETURNING id");
        cmd.Parameters.AddWithValue(taux);
        cmd.Parameters.AddWithValue(libelle);
        return await cmd.ExecuteScalarAsync(ct) is long id ? id : null;
    }

    public async Task<bool> ModifierAsync(long id, string libelle, bool actif, CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand("UPDATE taux_tva SET libelle = $2, actif = $3 WHERE id = $1");
        cmd.Parameters.AddWithValue(id);
        cmd.Parameters.AddWithValue(libelle);
        cmd.Parameters.AddWithValue(actif);
        return await cmd.ExecuteNonQueryAsync(ct) == 1;
    }

    public async Task<bool> DefinirDefautAsync(string categorie, decimal? taux, CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand(taux is null
            ? "WITH c AS (SELECT code FROM categorie_produit WHERE code = $1), d AS (DELETE FROM taux_tva_categorie WHERE categorie_code = $1) SELECT count(*)::int FROM c"
            : """
              WITH c AS (SELECT code FROM categorie_produit WHERE code = $1),
                   i AS (INSERT INTO taux_tva_categorie (categorie_code, taux) SELECT code, $2 FROM c
                         ON CONFLICT (categorie_code) DO UPDATE SET taux = EXCLUDED.taux, modifie_le = now())
              SELECT count(*)::int FROM c
              """);
        cmd.Parameters.AddWithValue(categorie);
        if (taux is { } t) cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Numeric, Value = t });
        return (int)(await cmd.ExecuteScalarAsync(ct))! == 1;
    }
}
