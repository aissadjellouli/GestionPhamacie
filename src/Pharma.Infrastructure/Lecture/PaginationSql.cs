using Npgsql;
using Pharma.Application.Commun;

namespace Pharma.Infrastructure.Lecture;

/// <summary>
/// Lecture d'une page : la requête reçoit « count(*) OVER () » en dernière colonne (total des lignes qui répondent aux
/// filtres), puis LIMIT et OFFSET. Une page au-delà de la fin renvoie une liste vide et un total à zéro : l'écran revient
/// alors à la première page.
/// </summary>
internal static class PaginationSql
{
    /// <summary>À ajouter en dernière colonne du SELECT.</summary>
    public const string Total = "count(*) OVER ()::int AS total_lignes";

    /// <summary>Ajoute LIMIT et OFFSET en paramètres suivants ; <paramref name="premier"/> est le numéro du premier paramètre libre.</summary>
    public static string Limite(int premier) => $"LIMIT ${premier} OFFSET ${premier + 1}";

    public static void Parametres(NpgsqlCommand cmd, DemandePage page)
    {
        cmd.Parameters.AddWithValue(page.Taille);
        cmd.Parameters.AddWithValue(page.Decalage);
    }

    /// <summary>Lit les lignes ; la colonne <paramref name="colonneTotal"/> porte le total.</summary>
    public static async Task<Page<T>> LireAsync<T>(NpgsqlCommand cmd, DemandePage page, int colonneTotal, Func<NpgsqlDataReader, T> lire, CancellationToken ct)
    {
        var lignes = new List<T>();
        var total = 0;
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            total = r.GetInt32(colonneTotal);
            lignes.Add(lire(r));
        }
        return new Page<T>(lignes, total, page.Page, page.Taille);
    }
}
