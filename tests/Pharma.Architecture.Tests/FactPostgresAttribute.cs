using Npgsql;

namespace Pharma.Architecture.Tests;

/// <summary>
/// Test qui exige un vrai PostgreSQL : outils psql et pg_dump trouvés, serveur joignable, compte autorisé à créer une base (CREATEDB).
/// Quand l'un manque, le test est marqué IGNORÉ avec la raison (visible dans le résultat de la suite), au lieu de réussir sans rien vérifier.
/// Mécanisme : sous-classe de <see cref="FactAttribute"/> dont <c>Skip</c> est renseigné (même principe que FactEssaiBase de Pharma.Cloud.Tests).
/// </summary>
internal sealed class FactPostgresAttribute : FactAttribute
{
    private static readonly Lazy<string?> Raison = new(Indisponibilite);

    public FactPostgresAttribute()
    {
        if (Raison.Value is { } raison) Skip = "PostgreSQL indisponible, test ignoré : " + raison;
    }

    /// <summary>Null si tout est disponible ; sinon la raison, en une phrase.</summary>
    private static string? Indisponibilite()
    {
        if (ScriptsRejouablesTests.Outil("psql.exe") is null) return "psql introuvable (installer PostgreSQL ou définir PHARMA_PG_BIN).";
        if (ScriptsRejouablesTests.Outil("pg_dump.exe") is null) return "pg_dump introuvable (installer PostgreSQL ou définir PHARMA_PG_BIN).";
        try
        {
            var cnx = new NpgsqlConnectionStringBuilder(ScriptsRejouablesTests.ChaineConnexion());
            var administration = new NpgsqlConnectionStringBuilder(cnx.ConnectionString) { Database = "postgres", Pooling = false, Timeout = 5 }.ConnectionString;
            using var cx = new NpgsqlConnection(administration);
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
