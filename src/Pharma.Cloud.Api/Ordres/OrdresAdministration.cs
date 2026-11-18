using System.Text.Json;
using Npgsql;
using NpgsqlTypes;

namespace Pharma.Cloud.Api.Ordres;

/// <summary>
/// Lots S5.1 et S5.5 (script central 013) : ordres envoyés aux officines, en attendant les écrans du serveur central (S7).
/// Auteur obligatoire sur chaque commande d'écriture (décision D10). Connexion du propriétaire ; l'officine est posée par les fonctions.
///   --ordres [--officine &lt;code&gt;] [--nombre &lt;n&gt;]                                  liste, les plus récents d'abord
///   --ordre-essai --officine &lt;code&gt; --auteur &lt;nom&gt;                                ordre sans effet (vérifie la liaison)
///   --ordre-employe-creer --officine &lt;code&gt; --nom &lt;nom&gt; --identifiant &lt;id&gt; --role &lt;code&gt; --auteur &lt;nom&gt;
///   --ordre-employe-modifier --officine &lt;code&gt; --employe &lt;n°&gt; [--nom &lt;nom&gt;] [--role &lt;code&gt;] --auteur &lt;nom&gt;
///   --ordre-employe-bloquer | --ordre-employe-debloquer --officine &lt;code&gt; --employe &lt;n°&gt; --auteur &lt;nom&gt;
///   --ordre-droits-role --officine &lt;code&gt; --role &lt;code&gt; --permissions &lt;a,b,c&gt; --auteur &lt;nom&gt;
///   --annuler-ordre &lt;id&gt; --officine &lt;code&gt; --auteur &lt;nom&gt;
///   --expirer-ordres                                                                  tâche planifiée (chaque heure)
/// </summary>
public static class OrdresAdministration
{
    public static readonly string[] Commandes =
        ["--ordres", "--ordre-essai", "--ordre-employe-creer", "--ordre-employe-modifier", "--ordre-employe-bloquer", "--ordre-employe-debloquer",
         "--ordre-droits-role", "--ordre-prix", "--annuler-ordre", "--expirer-ordres"];

    public static bool Concerne(string commande) => Commandes.Contains(commande);

    /// <summary>Type d'ordre et données d'une commande de création (null : pas une création) ; règles d'arguments pures, testées.</summary>
    public static (string Type, string? Cible, Dictionary<string, object?> Donnees)? Creation(string[] args)
    {
        string? O(string nom) => Option(args, nom);
        string Exiger(string nom) => O(nom) is { Length: > 0 } v ? v : throw new ArgumentException($"Option {nom} obligatoire.");
        return args[0] switch
        {
            "--ordre-essai" => ("essai", null, []),
            "--ordre-employe-creer" => ("employe_creer", null, new() { ["nom"] = Exiger("--nom"), ["identifiant"] = Exiger("--identifiant"), ["role"] = Exiger("--role") }),
            "--ordre-employe-modifier" => ("employe_modifier", Exiger("--employe"),
                O("--nom") is null && O("--role") is null ? throw new ArgumentException("Option --nom ou --role attendue.")
                    : new() { ["nom"] = O("--nom"), ["role"] = O("--role") }),
            "--ordre-employe-bloquer" => ("employe_bloquer", Exiger("--employe"), []),
            "--ordre-employe-debloquer" => ("employe_debloquer", Exiger("--employe"), []),
            "--ordre-droits-role" => ("droits_role", Exiger("--role"), new()
            {
                ["permissions"] = Exiger("--permissions").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            }),
            // Lot S5.2 : --ordre-prix --officine <code> --produit <n°> [--prix <DA>] [--tarif <DA>] --auteur <nom>
            "--ordre-prix" => ("prix_produit", Exiger("--produit"),
                O("--prix") is null && O("--tarif") is null ? throw new ArgumentException("Option --prix ou --tarif attendue.")
                    : new() { ["prix_public"] = Montant(O("--prix")), ["tarif_reference"] = Montant(O("--tarif")) }),
            _ => null
        };
    }

    private static decimal? Montant(string? v) => v is null ? null
        : decimal.TryParse(v.Replace(',', '.'), System.Globalization.NumberStyles.Number, System.Globalization.CultureInfo.InvariantCulture, out var m) ? m
        : throw new ArgumentException($"Montant invalide : {v} (exemple : 380 ou 380.50).");

    public static async Task<int> ExecuterAsync(NpgsqlDataSource db, string[] args, TextWriter sortie)
    {
        try
        {
            if (args[0] == "--expirer-ordres")
            {
                await using var cmd = db.CreateCommand("SELECT cloud_expirer_ordres()");
                sortie.WriteLine($"Ordres expirés : {await cmd.ExecuteScalarAsync()}");
                return 0;
            }
            if (args[0] == "--ordres") return await ListerAsync(db, Officine(args, false), args, sortie);
            if (args[0] == "--annuler-ordre")
            {
                var id = args.Length > 1 && Guid.TryParse(args[1], out var g) ? g : throw new ArgumentException("Identifiant de l'ordre attendu après --annuler-ordre.");
                await using var cmd = db.CreateCommand("SELECT cloud_annuler_ordre($1, $2, $3)");
                cmd.Parameters.AddWithValue(Officine(args, true)!.Value);
                cmd.Parameters.AddWithValue(id);
                cmd.Parameters.AddWithValue(Auteur(args));
                await cmd.ExecuteNonQueryAsync();
                sortie.WriteLine($"Ordre {id} annulé.");
                return 0;
            }
            var (type, cible, donnees) = Creation(args) ?? throw new ArgumentException($"Commande inconnue : {args[0]}");
            await using (var cmd = db.CreateCommand("SELECT cloud_creer_ordre($1, $2, $3, $4::jsonb, $5)"))
            {
                cmd.Parameters.AddWithValue(Officine(args, true)!.Value);
                cmd.Parameters.AddWithValue(type);
                cmd.Parameters.Add(new NpgsqlParameter { Value = (object?)cible ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Text });
                cmd.Parameters.AddWithValue(JsonSerializer.Serialize(donnees.Where(kv => kv.Value is not null).ToDictionary()));
                cmd.Parameters.AddWithValue(Auteur(args));
                sortie.WriteLine($"Ordre créé : {await cmd.ExecuteScalarAsync()} ({type}) ; il descend à l'officine au prochain échange.");
            }
            return 0;
        }
        catch (PostgresException e) when (e.SqlState == "P0001")
        {
            sortie.WriteLine($"Refusé : {e.MessageText}");
            return 1;
        }
    }

    private static async Task<int> ListerAsync(NpgsqlDataSource db, Guid? officine, string[] args, TextWriter sortie)
    {
        var nombre = Option(args, "--nombre") is { } n ? int.TryParse(n, out var v) && v is >= 1 and <= 1000 ? v
            : throw new ArgumentException("--nombre : entre 1 et 1 000.") : 50;
        await using var liste = db.CreateCommand("SELECT id, libelle FROM officine WHERE resiliee_le IS NULL AND ($1::uuid IS NULL OR id = $1) ORDER BY cree_le");
        liste.Parameters.Add(new NpgsqlParameter { Value = (object?)officine ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Uuid });
        var officines = new List<(Guid, string)>();
        await using (var r = await liste.ExecuteReaderAsync())
            while (await r.ReadAsync()) officines.Add((r.GetGuid(0), r.GetString(1)));
        foreach (var (id, libelle) in officines)
        {
            await using var cnx = await db.OpenConnectionAsync();
            await using var tx = await cnx.BeginTransactionAsync();
            await using (var pose = new NpgsqlCommand("SELECT set_config('app.officine_id', $1, true)", cnx, tx))
            {
                pose.Parameters.AddWithValue(id.ToString("D"));
                await pose.ExecuteNonQueryAsync();
            }
            await using var cmd = new NpgsqlCommand("""
                SELECT id, type, coalesce(cible, ''), statut, auteur, cree_le, coalesce(motif, '') FROM ordre_central ORDER BY cree_le DESC LIMIT $1
                """, cnx, tx);
            cmd.Parameters.AddWithValue(nombre);
            await using var lr = await cmd.ExecuteReaderAsync();
            sortie.WriteLine($"{id}  {libelle}");
            while (await lr.ReadAsync())
                sortie.WriteLine($"  {lr.GetGuid(0)}  {lr.GetDateTime(5).ToUniversalTime():yyyy-MM-dd HH:mm} UTC  {lr.GetString(1)} {lr.GetString(2)}  {lr.GetString(3)}"
                    + $"  par {lr.GetString(4)}{(lr.GetString(6).Length > 0 ? " : " + lr.GetString(6) : "")}");
        }
        return 0;
    }

    private static Guid? Officine(string[] args, bool obligatoire) => Option(args, "--officine") is { } o
        ? Guid.TryParse(o, out var g) ? g : throw new ArgumentException("Code officine (uuid) attendu après --officine.")
        : obligatoire ? throw new ArgumentException("Option --officine obligatoire.") : null;

    private static string Auteur(string[] args) => Option(args, "--auteur") is { Length: > 0 } a ? a : throw new ArgumentException("Option --auteur obligatoire.");

    private static string? Option(string[] args, string nom)
    {
        var i = Array.IndexOf(args, nom);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }
}
