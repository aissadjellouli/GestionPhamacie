using Npgsql;
using Pharma.Cloud.Api.Reglages;
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
        ["--initialiser-autorite", "--certificat-serveur", "--creer-officine", "--code-activation", "--revoquer", "--revoquer-serveur", "--lister", "--partitions", "--generer-miroir",
         "--reglages", "--regler-famille", "--effacer-famille", "--conservation", "--purge-lots", "--purger-lots",
         "--reglages-mutualises", "--publier-reglage", "--mode-reglage", "--modele-depuis-officine", "--liberer-reglages", "--retablir-reglages", "--journal-reglages",
         "--verifier-integrite", "--anomalies-integrite", .. Ordres.OrdresAdministration.Commandes];

    public static bool Concerne(string[] args) => args.Length > 0 && Commandes.Contains(args[0]);

    public static async Task<int> ExecuterAsync(string[] args)
    {
        try
        {
            if (Ordres.OrdresAdministration.Concerne(args[0]))
            {
                // Lots S5.1 et S5.5 : ordres envoyés aux officines (script central 013)
                await using var db = Admin();
                return await Ordres.OrdresAdministration.ExecuterAsync(db, args, Console.Out);
            }
            if (ArgumentsReglages.Concerne(args[0]))
                // Réglages mutualisés (lot M6) : fonctions d'administration du script central 010, connexion du propriétaire
                return await AdministrationReglages.PourConsole(Admin).ExecuterAsync(args);
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
                case "--verifier-integrite":
                    {
                        // Lot S4 : tâche planifiée toutes les 15 min ; --complete chaque semaine (depuis le rang 1)
                        await using var db = Admin();
                        return await Integrite.VerificationIntegriteCentrale.VerifierAsync(db, args.Contains("--complete"), OfficineOption(args), Console.Out);
                    }
                case "--anomalies-integrite":
                    {
                        await using var db = Admin();
                        var nombre = Option(args, "--nombre") is { } n
                            ? int.TryParse(n, out var v) ? v : throw new ArgumentException("Nombre entier attendu après --nombre.")
                            : Integrite.VerificationIntegriteCentrale.NombreParDefaut;
                        return await Integrite.VerificationIntegriteCentrale.AnomaliesAsync(db, OfficineOption(args), nombre, Console.Out);
                    }
                case "--partitions":
                    {
                        await using var db = Admin();
                        await using var cmd = db.CreateCommand("SELECT cloud_creer_partitions(1, 3)");
                        Console.WriteLine($"Partitions mensuelles créées : {await cmd.ExecuteScalarAsync()}");
                        return 0;
                    }
                case "--reglages":
                    {
                        await using var db = Admin();
                        await AfficherReglagesAsync(db);
                        return 0;
                    }
                case "--regler-famille":
                    {
                        // --regler-famille <famille> oui|non|global [--officine <code>]
                        var famille = args.Length > 2 ? args[1] : throw new ArgumentException("Attendu : --regler-famille <famille> oui|non|global [--officine <code>].");
                        bool? envoyee = args[2] switch
                        {
                            "oui" => true,
                            "non" => false,
                            "global" => null,
                            _ => throw new ArgumentException("Valeur attendue : oui, non ou global (retour à la valeur globale, avec --officine).")
                        };
                        var officine = OfficineOption(args);
                        await using var db = Admin();
                        await using var cmd = db.CreateCommand("SELECT cloud_regler_famille($1, $2, $3)");
                        cmd.Parameters.AddWithValue(famille);
                        cmd.Parameters.Add(new NpgsqlParameter { Value = (object?)envoyee ?? DBNull.Value, NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Boolean });
                        cmd.Parameters.Add(new NpgsqlParameter { Value = (object?)officine ?? DBNull.Value, NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Uuid });
                        await cmd.ExecuteNonQueryAsync();
                        Console.WriteLine($"Famille « {famille} » : {(envoyee is null ? "valeur globale" : envoyee.Value ? "envoyée" : "non envoyée")}"
                            + $"{(officine is null ? " (toutes les officines sans surcharge)" : $" (officine {officine})")} ; pris en compte au prochain passage d'envoi de l'officine.");
                        if (envoyee == false)
                            Console.WriteLine($"Ce qui a déjà été reçu reste gardé : pour l'effacer, --effacer-famille {famille}{(officine is null ? "" : $" --officine {officine}")} (simulation, puis --confirmer {famille}).");
                        return 0;
                    }
                case "--effacer-famille":
                    {
                        // SUPPRESSION de données : simulation sans --confirmer <famille> (le nom de la famille répété)
                        var famille = args.Length > 1 ? args[1] : throw new ArgumentException("Attendu : --effacer-famille <famille> [--officine <code>] [--confirmer <famille>].");
                        var officine = OfficineOption(args);
                        var confirmee = Option(args, "--confirmer") is { } c && c == famille;
                        if (Option(args, "--confirmer") is not null && !confirmee) throw new ArgumentException("Confirmation refusée : --confirmer doit répéter le nom de la famille.");
                        await using var db = Admin();
                        await using var cmd = db.CreateCommand("SELECT officine_id, nom_table, colonne, lignes, lots FROM cloud_effacer_famille($1, $2, $3)");
                        cmd.Parameters.AddWithValue(famille);
                        cmd.Parameters.Add(new NpgsqlParameter { Value = (object?)officine ?? DBNull.Value, NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Uuid });
                        cmd.Parameters.AddWithValue(confirmee);
                        await using var r = await cmd.ExecuteReaderAsync();
                        var vu = false;
                        while (await r.ReadAsync())
                        {
                            vu = true;
                            Console.WriteLine(r.IsDBNull(1)
                                ? $"{r.GetGuid(0)}  lots bruts contenant la famille : {r.GetInt64(4)}"
                                : $"{r.GetGuid(0)}  {r.GetString(1)}.{r.GetString(2)} : {r.GetInt64(3)} valeur(s)");
                        }
                        if (!vu) Console.WriteLine("Aucune officine où cette famille est décochée.");
                        Console.WriteLine(confirmee ? "Effacement fait et journalisé (famille_effacee)."
                            : $"SIMULATION : rien n'a été effacé. Pour effacer (définitif) : ajoutez --confirmer {famille}.");
                        return 0;
                    }
                case "--conservation":
                    {
                        // --conservation <ans>|global [--officine <code>] : 6 ans minimum, pas de maximum
                        var valeur = args.Length > 1 ? args[1] : throw new ArgumentException("Attendu : --conservation <ans>|global [--officine <code>].");
                        int? ans = valeur == "global" ? null : int.TryParse(valeur, out var a) ? a : throw new ArgumentException("Nombre d'années attendu (6 minimum).");
                        var officine = OfficineOption(args);
                        await using var db = Admin();
                        await using var cmd = db.CreateCommand("SELECT cloud_regler_conservation($1, $2)");
                        cmd.Parameters.Add(new NpgsqlParameter { Value = (object?)ans ?? DBNull.Value, NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Integer });
                        cmd.Parameters.Add(new NpgsqlParameter { Value = (object?)officine ?? DBNull.Value, NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Uuid });
                        await cmd.ExecuteNonQueryAsync();
                        Console.WriteLine($"Conservation des lots bruts : {(ans is null ? "valeur globale" : $"{ans} ans")}{(officine is null ? " (globale)" : $" (officine {officine})")}.");
                        return 0;
                    }
                case "--purge-lots":
                    {
                        var active = args.Length > 1 && args[1] == "activer" ? true
                            : args.Length > 1 && args[1] == "desactiver" ? false
                            : throw new ArgumentException("Attendu : --purge-lots activer|desactiver.");
                        await using var db = Admin();
                        await using var cmd = db.CreateCommand("SELECT cloud_regler_purge($1)");
                        cmd.Parameters.AddWithValue(active);
                        await cmd.ExecuteNonQueryAsync();
                        Console.WriteLine(active
                            ? "Effacement des lots bruts trop anciens ACTIVÉ : chaque --purger-lots (tâche planifiée) effacera les lots dont la durée de conservation est dépassée."
                            : "Effacement des lots bruts trop anciens désactivé.");
                        return 0;
                    }
                case "--purger-lots":
                    {
                        // Tâche planifiée : sans effet tant que l'effacement n'est pas activé ; --simulation : compte seulement
                        var simulation = args.Contains("--simulation");
                        await using var db = Admin();
                        if (!simulation)
                        {
                            await using var etat = db.CreateCommand("SELECT purge_lots_active FROM cloud_parametre WHERE id = 1");
                            if (await etat.ExecuteScalarAsync() is not true)
                            {
                                Console.WriteLine("Effacement des lots bruts désactivé (défaut) : rien n'est effacé. Activation : --purge-lots activer.");
                                return 0;
                            }
                        }
                        await using var cmd = db.CreateCommand("SELECT officine_id, conservation_ans, lots, recus_du, recus_au FROM cloud_purger_lots($1)");
                        cmd.Parameters.AddWithValue(simulation);
                        await using var r = await cmd.ExecuteReaderAsync();
                        long total = 0;
                        while (await r.ReadAsync())
                        {
                            total += r.GetInt64(2);
                            Console.WriteLine($"{r.GetGuid(0)}  {r.GetInt64(2)} lot(s) reçus du {r.GetDateTime(3):yyyy-MM-dd} au {r.GetDateTime(4):yyyy-MM-dd} (conservation {r.GetInt32(1)} ans)");
                        }
                        Console.WriteLine(simulation ? $"SIMULATION : {total} lot(s) seraient effacés." : $"{total} lot(s) effacé(s), journalisé(s) (lots_purges).");
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

    /// <summary>Familles nominatives (globales et surcharges), conservation des lots bruts, effacement automatique.</summary>
    private static async Task AfficherReglagesAsync(NpgsqlDataSource db)
    {
        await using (var cmd = db.CreateCommand("""
            SELECT f.famille, f.envoyee, f.libelle, (SELECT string_agg(c.nom_table || '.' || c.colonne, ', ' ORDER BY 1) FROM cloud_famille_colonne c WHERE c.famille = f.famille)
            FROM cloud_famille f ORDER BY 1
            """))
        await using (var r = await cmd.ExecuteReaderAsync())
        {
            Console.WriteLine("Familles nominatives (valeur globale) :");
            while (await r.ReadAsync())
                Console.WriteLine($"  [{(r.GetBoolean(1) ? "x" : " ")}] {r.GetString(0),-20} {r.GetString(2)} : {(r.IsDBNull(3) ? "-" : r.GetString(3))}");
        }
        await using (var cmd = db.CreateCommand("""
            SELECT o.officine_id, x.libelle, o.famille, o.envoyee FROM officine_famille o JOIN officine x ON x.id = o.officine_id ORDER BY 1, 3
            """))
        await using (var r = await cmd.ExecuteReaderAsync())
        {
            Console.WriteLine("Surcharges par officine :");
            var aucune = true;
            while (await r.ReadAsync())
            {
                aucune = false;
                Console.WriteLine($"  {r.GetGuid(0)}  {r.GetString(1)}  {r.GetString(2)} : {(r.GetBoolean(3) ? "envoyée" : "non envoyée")}");
            }
            if (aucune) Console.WriteLine("  (aucune)");
        }
        await using (var cmd = db.CreateCommand("""
            SELECT p.conservation_lots_ans, p.purge_lots_active,
                   (SELECT string_agg(o.id || ' : ' || o.conservation_lots_ans || ' ans', ', ') FROM officine o WHERE o.conservation_lots_ans IS NOT NULL)
            FROM cloud_parametre p WHERE p.id = 1
            """))
        await using (var r = await cmd.ExecuteReaderAsync())
        {
            if (await r.ReadAsync())
            {
                Console.WriteLine($"Conservation des lots bruts : {r.GetInt32(0)} ans (6 minimum){(r.IsDBNull(2) ? "" : " ; officines : " + r.GetString(2))}");
                Console.WriteLine($"Effacement automatique des lots trop anciens : {(r.GetBoolean(1) ? "ACTIVÉ" : "désactivé")}");
            }
        }
    }

    private static Guid? OfficineOption(string[] args) => Option(args, "--officine") is { } o
        ? Guid.TryParse(o, out var g) ? g : throw new ArgumentException("Code officine (uuid) attendu après --officine.")
        : null;

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
