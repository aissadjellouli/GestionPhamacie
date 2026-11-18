using System.Globalization;
using System.Text.Json;
using Npgsql;

namespace Pharma.Cloud.Api.Integrite;

/// <summary>
/// Lot S4 (script central 012) : commandes d'administration de la vérification d'intégrité des données reçues.
///   --verifier-integrite [--complete] [--officine &lt;code&gt;]   passage incrémental (tâche planifiée, toutes les 15 min) ou complet
///                                                         (depuis le rang 1, chaque semaine) ; résultat publié vers chaque officine
///   --anomalies-integrite [--officine &lt;code&gt;] [--nombre &lt;n&gt;]   anomalies constatées, les plus récentes d'abord
/// Connexion du propriétaire (PHARMA_CLOUD_ADMIN_CONNEXION) ; l'officine est posée dans chaque transaction (cloisonnement). Une
/// officine en erreur n'arrête pas les autres. Aucune donnée de patient n'est affichée (les données reçues sont déjà sous code).
/// </summary>
public static class VerificationIntegriteCentrale
{
    public const int NombreParDefaut = 50;

    /// <returns>0 si toutes les officines ont été vérifiées, 1 si au moins une a échoué.</returns>
    public static async Task<int> VerifierAsync(NpgsqlDataSource db, bool complete, Guid? officine, TextWriter sortie)
    {
        var echecs = 0;
        foreach (var (id, libelle) in await OfficinesAsync(db, officine))
        {
            try
            {
                await using var cnx = await db.OpenConnectionAsync();
                await using var tx = await cnx.BeginTransactionAsync();
                await PoserOfficineAsync(cnx, tx, id);
                await using var cmd = new NpgsqlCommand("SELECT cloud_verifier_integrite($1)::text", cnx, tx);
                cmd.Parameters.AddWithValue(complete);
                var resume = (string)(await cmd.ExecuteScalarAsync())!;
                await tx.CommitAsync();
                sortie.WriteLine($"{id}  {libelle}  {Resume(resume)}");
            }
            catch (Exception e) when (e is NpgsqlException or InvalidOperationException or JsonException)
            {
                echecs++;
                sortie.WriteLine($"{id}  {libelle}  ÉCHEC de la vérification : {e.GetType().Name}");
            }
        }
        return echecs == 0 ? 0 : 1;
    }

    public static async Task<int> AnomaliesAsync(NpgsqlDataSource db, Guid? officine, int nombre, TextWriter sortie)
    {
        if (nombre is < 1 or > 1000) throw new ArgumentException("--nombre : entre 1 et 1 000.");
        foreach (var (id, libelle) in await OfficinesAsync(db, officine))
        {
            await using var cnx = await db.OpenConnectionAsync();
            await using var tx = await cnx.BeginTransactionAsync();
            await PoserOfficineAsync(cnx, tx, id);
            await using var cmd = new NpgsqlCommand("""
                SELECT a.id, a.detectee_le, a.nature, a.journal, a.cle_chaine, a.rang, a.cle_ligne, a.detail
                FROM cloud_anomalie_integrite a ORDER BY a.id DESC LIMIT $1
                """, cnx, tx);
            cmd.Parameters.AddWithValue(nombre);
            await using var r = await cmd.ExecuteReaderAsync();
            var lignes = new List<string>();
            while (await r.ReadAsync())
                lignes.Add($"  n° {r.GetInt64(0)}  {r.GetDateTime(1).ToUniversalTime():yyyy-MM-dd HH:mm} UTC  {r.GetString(2)}  {r.GetString(3)}"
                    + (r.IsDBNull(4) ? "" : $" ({r.GetString(4)})") + (r.IsDBNull(5) ? "" : $" rang {r.GetInt64(5)}")
                    + (r.IsDBNull(6) ? "" : $" [{r.GetString(6)}]") + (r.IsDBNull(7) ? "" : $" : {r.GetString(7)}"));
            sortie.WriteLine($"{id}  {libelle}  {(lignes.Count == 0 ? "aucune anomalie" : $"{lignes.Count} anomalie(s) affichée(s)")}");
            foreach (var l in lignes) sortie.WriteLine(l);
        }
        return 0;
    }

    /// <summary>
    /// Lot S6.5.3 (script 023, S6.5-D8) : restaurations constatées (bifurcations), pour l'éditeur : journal, époques, rang commun, lignes
    /// et période de la suite remplacée, montant TTC (journal des ventes), sceaux perdus, classe. Lecture seule.
    /// </summary>
    public static async Task<int> RestaurationsAsync(NpgsqlDataSource db, Guid? officine, TextWriter sortie)
    {
        foreach (var (id, libelle) in await OfficinesAsync(db, officine))
        {
            await using var cnx = await db.OpenConnectionAsync();
            await using var tx = await cnx.BeginTransactionAsync();
            await PoserOfficineAsync(cnx, tx, id);
            await using var cmd = new NpgsqlCommand("""
                SELECT b.id, b.constatee_le, b.journal, b.ancienne_epoque, b.nouvelle_epoque, b.rang_commun, b.fin_ancienne_rang, b.lignes_ancienne,
                       b.premiere_date, b.derniere_date, b.montant_ttc, b.sceaux_perdus, b.classe
                FROM cloud_restaurations_integrite() b
                """, cnx, tx);
            await using var r = await cmd.ExecuteReaderAsync();
            var lignes = new List<string>();
            while (await r.ReadAsync())
                lignes.Add(FormattableString.Invariant($"  n° {r.GetInt64(0)}  {r.GetDateTime(1).ToUniversalTime():yyyy-MM-dd HH:mm} UTC  {r.GetString(2)}  époque {r.GetInt32(3)} -> {r.GetInt32(4)}")
                    + FormattableString.Invariant($"  rangs {r.GetInt64(5) + 1} à {(r.IsDBNull(6) ? "?" : r.GetInt64(6).ToString(CultureInfo.InvariantCulture))} ({r.GetInt64(7)} ligne(s))")
                    + (r.IsDBNull(8) ? "" : FormattableString.Invariant($" du {r.GetDateTime(8).ToUniversalTime():yyyy-MM-dd} au {r.GetDateTime(9).ToUniversalTime():yyyy-MM-dd}"))
                    + (r.IsDBNull(10) ? "" : FormattableString.Invariant($"  montant TTC {r.GetDecimal(10):0.00}"))
                    + FormattableString.Invariant($"  sceaux perdus {r.GetInt32(11)}  {(r.GetString(12) == "restauration_constatee" ? "remplacée après restauration" : "À JUSTIFIER")}"));
            sortie.WriteLine($"{id}  {libelle}  {(lignes.Count == 0 ? "aucune restauration" : $"{lignes.Count} restauration(s)")}");
            foreach (var l in lignes) sortie.WriteLine(l);
        }
        return 0;
    }

    /// <summary>Ligne de compte rendu d'un passage (résumé integrite-v1 du script 012).</summary>
    public static string Resume(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var r = doc.RootElement;
        var chaines = r.GetProperty("chaines").EnumerateArray()
            .Select(c => $"{c.GetProperty("journal").GetString()}:{c.GetProperty("statut").GetString()}@{c.GetProperty("dernier_rang").GetInt64()}").ToList();
        var nouvelles = r.TryGetProperty("nouvelles", out var n) ? n.GetInt64() : 0;
        return string.Create(CultureInfo.InvariantCulture,
            $"{r.GetProperty("statut").GetString()} ; {r.GetProperty("anomalies_total").GetInt64()} anomalie(s) dont {nouvelles} nouvelle(s) ; "
            + $"chaînes {(chaines.Count == 0 ? "aucune" : string.Join(", ", chaines))}");
    }

    private static async Task<List<(Guid Id, string Libelle)>> OfficinesAsync(NpgsqlDataSource db, Guid? officine)
    {
        await using var cmd = db.CreateCommand("SELECT id, libelle FROM officine WHERE resiliee_le IS NULL AND ($1::uuid IS NULL OR id = $1) ORDER BY cree_le");
        cmd.Parameters.Add(new NpgsqlParameter { Value = (object?)officine ?? DBNull.Value, NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Uuid });
        await using var r = await cmd.ExecuteReaderAsync();
        var liste = new List<(Guid, string)>();
        while (await r.ReadAsync()) liste.Add((r.GetGuid(0), r.GetString(1)));
        if (officine is not null && liste.Count == 0) throw new ArgumentException($"Officine {officine} inconnue ou résiliée.");
        return liste;
    }

    private static async Task PoserOfficineAsync(NpgsqlConnection cnx, NpgsqlTransaction tx, Guid officine)
    {
        await using var cmd = new NpgsqlCommand("SELECT set_config('app.officine_id', $1, true)", cnx, tx);
        cmd.Parameters.AddWithValue(officine.ToString("D"));
        await cmd.ExecuteNonQueryAsync();
    }
}
