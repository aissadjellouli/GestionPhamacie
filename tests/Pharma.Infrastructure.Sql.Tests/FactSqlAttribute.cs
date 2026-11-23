using Npgsql;

namespace Pharma.Infrastructure.Sql.Tests;

/// <summary>
/// Test qui exige un vrai PostgreSQL (psql trouvé, serveur joignable, compte avec CREATEDB). Sinon le test est IGNORÉ avec la raison,
/// jamais vert en silence ; avec PHARMA_EXIGER_BASE=1 il n'est pas ignoré et ÉCHOUE (la fixture lève la raison).
/// </summary>
internal sealed class FactSqlAttribute : FactAttribute
{
    private static readonly Lazy<string?> Raison = new(Calculer);

    public FactSqlAttribute()
    {
        if (Raison.Value is { } raison && !OutilsPostgres.ExigerBase) Skip = "PostgreSQL indisponible, test ignoré : " + raison;
    }

    /// <summary>Null si tout est disponible ; sinon la raison, en une phrase.</summary>
    public static string? Indisponibilite() => Raison.Value;

    private static string? Calculer()
    {
        if (OutilsPostgres.Outil("psql.exe") is null) return "psql introuvable (installer PostgreSQL ou définir PHARMA_PG_BIN).";
        try
        {
            using var cx = new NpgsqlConnection(OutilsPostgres.ChaineAdministration());
            cx.Open();
            using var droit = new NpgsqlCommand("SELECT rolcreatedb OR rolsuper FROM pg_roles WHERE rolname = current_user", cx);
            return droit.ExecuteScalar() is true ? null : "le compte de connexion n'a pas le droit CREATEDB.";
        }
        catch (Exception e) when (e is NpgsqlException or InvalidOperationException or IOException or KeyNotFoundException or System.Text.Json.JsonException or DirectoryNotFoundException)
        {
            return "serveur injoignable ou connexion introuvable (" + e.GetType().Name + ").";
        }
    }
}
