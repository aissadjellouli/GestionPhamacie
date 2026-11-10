using Npgsql;
using NpgsqlTypes;
using Pharma.Application.Stock;

namespace Pharma.Infrastructure.Lecture;

/// <summary>Registre des psychotropes (vue v_registre_psychotrope, 024_registre_psychotropes.sql).</summary>
public sealed class RegistreSql(NpgsqlDataSource db) : IRegistrePsychotropes
{
    public async Task<IReadOnlyList<LigneRegistre>> LireAsync(long? produitId, DateOnly du, DateOnly au, CancellationToken ct = default)
    {
        // Jours du poste (heure locale) convertis en instants
        var debut = du.ToDateTime(TimeOnly.MinValue, DateTimeKind.Local).ToUniversalTime();
        var fin = au.AddDays(1).ToDateTime(TimeOnly.MinValue, DateTimeKind.Local).ToUniversalTime();
        await using var cmd = db.CreateCommand("""
            SELECT numero, cree_le, produit_id, produit, numero_lot, type_mouvement, entree, sortie, solde_produit, solde_lot,
                   NULLIF(patient, ''), medecin, ordonnance, fournisseur, reference,
                   porteur_nom, porteur_adresse, porteur_piece, employe
            FROM v_registre_psychotrope
            WHERE cree_le >= $1 AND cree_le < $2 AND ($3::bigint IS NULL OR produit_id = $3)
            ORDER BY numero
            """);
        cmd.Parameters.AddWithValue(debut);
        cmd.Parameters.AddWithValue(fin);
        cmd.Parameters.Add(new NpgsqlParameter { Value = (object?)produitId ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Bigint });
        var liste = new List<LigneRegistre>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        string? S(int i) => r.IsDBNull(i) ? null : r.GetString(i);
        int? N(int i) => r.IsDBNull(i) ? null : r.GetInt32(i);
        while (await r.ReadAsync(ct))
            liste.Add(new LigneRegistre(r.GetInt64(0), r.GetDateTime(1), r.GetInt64(2), r.GetString(3), r.GetString(4), r.GetString(5),
                N(6), N(7), N(8), r.GetInt32(9), S(10), S(11), S(12), S(13), S(14), S(15), S(16), S(17), r.GetString(18)));
        return liste;
    }

    public async Task<IReadOnlyList<ProduitRegistre>> ProduitsAsync(CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand("""
            SELECT p.id, p.nom_commercial FROM produit p
            WHERE p.classe IN ('psychotrope', 'stupefiant')
               OR EXISTS (SELECT 1 FROM registre_psychotrope r JOIN lot l ON l.id = r.lot_id WHERE l.produit_id = p.id)
            ORDER BY p.nom_commercial
            """);
        var liste = new List<ProduitRegistre>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) liste.Add(new ProduitRegistre(r.GetInt64(0), r.GetString(1)));
        return liste;
    }
}
