using Npgsql;
using Pharma.Application.Demo;

namespace Pharma.Infrastructure.Demo;

/// <summary>
/// Marqueur de la base de démonstration : table <see cref="ModeDemo.TableMarqueur"/> avec sa ligne, créée par le seul script
/// d'init de la démo (deploy/demo/init/10_base_officine.sh), jamais par une migration ni un seed. Lu au démarrage de l'API.
/// </summary>
public static class MarqueurDemo
{
    /// <summary>Vrai : marqueur présent ; faux : absent ; null : base injoignable ou chaîne vide (rien n'est levé).</summary>
    public static async Task<bool?> LireAsync(string chaineConnexion, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(chaineConnexion)) return null;
        try
        {
            var b = new NpgsqlConnectionStringBuilder(chaineConnexion) { Timeout = 5 };
            await using var c = new NpgsqlConnection(b.ConnectionString);
            await c.OpenAsync(ct);
            await using (var cmd = new NpgsqlCommand("SELECT to_regclass($1) IS NOT NULL", c))
            {
                cmd.Parameters.AddWithValue("public." + ModeDemo.TableMarqueur);
                if (!(bool)(await cmd.ExecuteScalarAsync(ct))!) return false;
            }
            // Nom de table constant (pas de saisie) : la table existe, sa ligne doit exister aussi
            await using (var cmd = new NpgsqlCommand($"SELECT EXISTS (SELECT 1 FROM public.{ModeDemo.TableMarqueur})", c))
                return (bool)(await cmd.ExecuteScalarAsync(ct))!;
        }
        catch (Exception e) when (e is NpgsqlException or InvalidOperationException or ArgumentException or TimeoutException)
        {
            return null;
        }
    }
}
