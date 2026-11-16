using Npgsql;
using Pharma.Cloud.Api.Schema;
using Pharma.Cloud.Api.Securite;
using Pharma.Shared;

namespace Pharma.Cloud.Api;

/// <summary>
/// Commandes d'administration du serveur central (le serveur ne démarre pas). Secrets jamais en argument ni dans appsettings :
///   PHARMA_CLOUD_DOSSIER          dossier HORS dépôt de l'autorité, du certificat HTTPS et de la clé des jetons (ou --dossier)
///   PHARMA_CLOUD_MOT_DE_PASSE     mot de passe des clés de ce dossier (16 caractères au moins)
///   PHARMA_CLOUD_ADMIN_CONNEXION  connexion PostgreSQL du propriétaire de la base centrale (officines, codes, révocations)
/// </summary>
public static class ConsoleAdministration
{
    public static readonly string[] Commandes =
        ["--initialiser-autorite", "--certificat-serveur", "--creer-officine", "--code-activation", "--revoquer", "--revoquer-serveur", "--lister", "--partitions", "--generer-miroir"];

    public static bool Concerne(string[] args) => args.Length > 0 && Commandes.Contains(args[0]);

    public static async Task<int> ExecuterAsync(string[] args)
    {
        try
        {
            switch (args[0])
            {
                case "--generer-miroir":
                    var racine = args.Length > 1 ? args[1] : Directory.GetCurrentDirectory();
                    File.WriteAllText(GenerateurMiroir.CheminScript(racine), GenerateurMiroir.Generer(racine));
                    Console.WriteLine($"Écrit : {GenerateurMiroir.CheminScript(racine)}");
                    return 0;
                case "--initialiser-autorite":
                {
                    var ac = AutoriteCertification.Creer(Dossier(args), MotDePasse(), DateTimeOffset.UtcNow);
                    Console.WriteLine($"Autorité créée. Empreinte SHA-256 : {ac.EmpreinteRacine}");
                    return 0;
                }
                case "--certificat-serveur":
                {
                    var noms = (Option(args, "--noms") ?? "localhost,127.0.0.1").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                    var ac = AutoriteCertification.Charger(Dossier(args), MotDePasse());
                    ac.CreerCertificatServeur(Dossier(args), MotDePasse(), noms, DateTimeOffset.UtcNow);
                    Console.WriteLine($"Certificat HTTPS créé pour {string.Join(", ", noms)} ({AutoriteCertification.FichierServeur}).");
                    return 0;
                }
                case "--creer-officine":
                {
                    var libelle = args.Length > 1 ? args[1] : throw new ArgumentException("Libellé de l'officine attendu.");
                    await using var db = Admin();
                    await using var cmd = db.CreateCommand("INSERT INTO officine (libelle) VALUES ($1) RETURNING id");
                    cmd.Parameters.AddWithValue(libelle);
                    var id = (Guid)(await cmd.ExecuteScalarAsync())!;
                    Console.WriteLine($"Officine créée : {id} (provisoire : remplacé à la première activation par le code de l'officine installée)");
                    await NouveauCodeAsync(db, id, Option(args, "--serveur") ?? "principal", Dossier(args));
                    return 0;
                }
                case "--code-activation":
                {
                    var id = args.Length > 1 && Guid.TryParse(args[1], out var g) ? g : throw new ArgumentException("Code de l'officine attendu.");
                    await using var db = Admin();
                    await NouveauCodeAsync(db, id, Option(args, "--serveur") ?? "principal", Dossier(args));
                    return 0;
                }
                case "--revoquer":
                {
                    var empreinte = args.Length > 1 ? args[1].ToLowerInvariant() : throw new ArgumentException("Empreinte du certificat attendue.");
                    await using var db = Admin();
                    await using var cmd = db.CreateCommand("""
                        WITH r AS (UPDATE certificat SET revoque_le = now(), motif_revocation = $2 WHERE empreinte = $1 AND revoque_le IS NULL
                                   RETURNING officine_id, empreinte)
                        INSERT INTO journal_securite (officine_id, certificat_empreinte, evenement, detail)
                        SELECT officine_id, empreinte, 'revocation', $2 FROM r RETURNING 1
                        """);
                    cmd.Parameters.AddWithValue(empreinte);
                    cmd.Parameters.AddWithValue(Option(args, "--motif") ?? "révocation par l'administrateur");
                    Console.WriteLine(await cmd.ExecuteScalarAsync() is null ? "Certificat inconnu ou déjà révoqué." : "Certificat révoqué (effet au prochain appel).");
                    return 0;
                }
                case "--revoquer-serveur":
                {
                    var off = args.Length > 2 && Guid.TryParse(args[1], out var o) ? o : throw new ArgumentException("Attendu : --revoquer-serveur <code officine> <serveur>.");
                    await using var db = Admin();
                    await using var cmd = db.CreateCommand("SELECT cloud_revoquer_serveur($1, $2, $3)");
                    cmd.Parameters.AddWithValue(off);
                    cmd.Parameters.AddWithValue(args[2]);
                    cmd.Parameters.AddWithValue(Option(args, "--motif") ?? "révocation par l'administrateur");
                    Console.WriteLine($"Certificats révoqués : {await cmd.ExecuteScalarAsync()} (effet au prochain appel).");
                    return 0;
                }
                case "--lister":
                {
                    await using var db = Admin();
                    await using var cmd = db.CreateCommand("""
                        SELECT o.id, o.libelle, c.serveur, left(c.empreinte, 16), c.echeance, c.revoque_le IS NOT NULL
                        FROM officine o LEFT JOIN certificat c ON c.officine_id = o.id ORDER BY o.cree_le, c.emis_le
                        """);
                    await using var r = await cmd.ExecuteReaderAsync();
                    while (await r.ReadAsync())
                        Console.WriteLine(r.IsDBNull(2) ? $"{r.GetGuid(0)}  {r.GetString(1)}  (aucun certificat)"
                            : $"{r.GetGuid(0)}  {r.GetString(1)}  {r.GetString(2)}  {r.GetString(3)}…  échéance {r.GetDateTime(4):yyyy-MM-dd}{(r.GetBoolean(5) ? "  RÉVOQUÉ" : "")}");
                    return 0;
                }
                case "--partitions":
                {
                    await using var db = Admin();
                    await using var cmd = db.CreateCommand("SELECT cloud_creer_partitions(1, 3)");
                    Console.WriteLine($"Partitions mensuelles créées : {await cmd.ExecuteScalarAsync()}");
                    return 0;
                }
            }
        }
        catch (Exception e) when (e is InvalidOperationException or ArgumentException or NpgsqlException or IOException)
        {
            Console.Error.WriteLine(e.Message);
            return 1;
        }
        return 1;
    }

    /// <summary>Nouveau code d'activation à usage unique, valable 24 h ; seule son empreinte est gardée.</summary>
    private static async Task NouveauCodeAsync(NpgsqlDataSource db, Guid officine, string serveur, string dossier)
    {
        using var racine = System.Security.Cryptography.X509Certificates.X509Certificate2.CreateFromPem(
            File.ReadAllText(Path.Combine(dossier, AutoriteCertification.FichierCertificat)));
        var code = CodeActivationSync.Generer(AutoriteCertification.Empreinte(racine));
        var lu = CodeActivationSync.Lire(code)!.Value;
        await using var cmd = db.CreateCommand("INSERT INTO code_activation (empreinte_code, officine_id, serveur, expire_le) VALUES ($1, $2, $3, now() + interval '24 hours')");
        cmd.Parameters.AddWithValue(CodeActivationSync.EmpreinteSecret(lu.Secret));
        cmd.Parameters.AddWithValue(officine);
        cmd.Parameters.AddWithValue(serveur);
        await cmd.ExecuteNonQueryAsync();
        Console.WriteLine($"Code d'activation du serveur « {serveur} » (usage unique, 24 h) :");
        Console.WriteLine(code);
    }

    private static NpgsqlDataSource Admin() =>
        NpgsqlDataSource.Create(Environment.GetEnvironmentVariable("PHARMA_CLOUD_ADMIN_CONNEXION") is { Length: > 0 } c ? c
            : throw new InvalidOperationException("Variable PHARMA_CLOUD_ADMIN_CONNEXION absente."));

    public static string Dossier(string[] args) => Option(args, "--dossier")
        ?? (Environment.GetEnvironmentVariable("PHARMA_CLOUD_DOSSIER") is { Length: > 0 } d ? d
            : throw new InvalidOperationException("Dossier du serveur central absent (--dossier ou PHARMA_CLOUD_DOSSIER)."));

    public static string MotDePasse()
    {
        var m = Environment.GetEnvironmentVariable("PHARMA_CLOUD_MOT_DE_PASSE");
        AutoriteCertification.VerifierMotDePasse(m);
        return m!;
    }

    private static string? Option(string[] args, string nom)
    {
        var i = Array.IndexOf(args, nom);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }
}
