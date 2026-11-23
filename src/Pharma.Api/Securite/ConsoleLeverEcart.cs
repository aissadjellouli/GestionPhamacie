using System.Text;
using Pharma.Application.Commun;
using Pharma.Application.Securite;
using Pharma.Application.SecondServeur;

namespace Pharma.Api.Securite;

/// <summary>
/// Levée d'un faux positif « serveur écarté », sur le PC de ce serveur : Pharma.Api.exe --lever-ecart [--identifiant X] [--motif "texte"]
/// (le serveur ne démarre pas). Réservée à un compte titulaire (identifiant et mot de passe contrôlés) ; motif obligatoire (10 caractères
/// au moins) ; la levée est écrite dans serveur_ecarte (qui l'a levée, quand, pourquoi) et au journal des actions. L'API déjà en marche
/// rend la main à son prochain passage de surveillance (30 s). Journal du serveur : numéro de compte seulement, jamais le motif.
/// </summary>
public static class ConsoleLeverEcart
{
    public const string Argument = "--lever-ecart";
    public const int LongueurMotifMin = 10;
    public const string Action = "serveur_ecarte_leve";

    public static bool Concerne(string[] args) => args.Contains(Argument);

    public static async Task<int> ExecuterAsync(IServiceProvider services, string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        using var portee = services.CreateScope();
        var sp = portee.ServiceProvider;
        var journal = sp.GetRequiredService<ILoggerFactory>().CreateLogger("Pharma.ServeurEcarte");
        try
        {
            var depot = sp.GetRequiredService<IEcartsDepot>();
            if (await depot.LireActifAsync() is null)
                return Echec("Aucun écart actif sur ce serveur : rien à lever (un état « deux serveurs à égalité » ne se lève que par un redémarrage de l'API).");

            var identifiant = Option(args, "--identifiant") ?? Lire("Identifiant du titulaire : ");
            var motDePasse = LireSecret("Mot de passe : ");
            var compte = string.IsNullOrWhiteSpace(identifiant) ? null : await sp.GetRequiredService<IComptesLecture>().TrouverActifAsync(identifiant.Trim());
            var verificateur = sp.GetRequiredService<IVerificateurMotDePasse>();
            if (compte is null) verificateur.VerifierFactice(motDePasse);
            if (compte is null || !compte.Actif || !verificateur.Verifier(motDePasse, compte.MotDePasseHash))
                return Echec("Identifiant ou mot de passe faux : rien n'a été modifié.");
            if (!await sp.GetRequiredService<Pharma.Infrastructure.Securite.PermissionService>().EstTitulaireAsync(compte.Id))
                return Echec("Seul un compte titulaire peut lever un serveur écarté : rien n'a été modifié.");

            var motif = (Option(args, "--motif") ?? Lire("Motif de la levée (obligatoire, ce que vous avez vérifié) : ")).Trim();
            if (motif.Length < LongueurMotifMin)
                return Echec($"Motif obligatoire ({LongueurMotifMin} caractères au moins) : rien n'a été modifié.");

            Console.WriteLine("Vérifiez que l'autre serveur n'écrit plus (PostgreSQL arrêté ou remis en 2e serveur) avant de lever.");
            if (!await depot.LeverAsync(compte.Identifiant, motif))
                return Echec("L'écart a déjà été levé : rien n'a été modifié.");
            journal.LogWarning("Serveur écarté : écart levé par le compte {UtilisateurId}", compte.Id);
            try
            {
                await sp.GetRequiredService<IJournal>().EcrireAsync(compte.Id, Action, "serveur_ecarte", null,
                    new Dictionary<string, object?> { ["motif"] = motif }, null);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                journal.LogError(e, "Serveur écarté : levée écrite dans serveur_ecarte mais pas au journal des actions");
                Console.Error.WriteLine("Attention : la levée est enregistrée, mais son inscription au journal des actions a échoué (voir le journal du serveur).");
            }
            Console.WriteLine("Écart levé. L'API en marche reprend l'écriture dans les 30 secondes ; sinon elle redémarre en écriture.");
            return 0;
        }
        catch (Exception e) when (e is Npgsql.NpgsqlException or InvalidOperationException)
        {
            journal.LogError(e, "Serveur écarté : levée impossible");
            return Echec("Levée impossible (base injoignable ou script 163 non passé) : voir le journal du serveur.");
        }
    }

    /// <summary>Valeur placée après l'option (« --motif texte »), sinon null.</summary>
    public static string? Option(string[] args, string nom)
    {
        var i = Array.IndexOf(args, nom);
        return i >= 0 && i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal) ? args[i + 1] : null;
    }

    private static int Echec(string message)
    {
        Console.Error.WriteLine(message);
        return 1;
    }

    private static string Lire(string invite)
    {
        Console.Write(invite);
        return Console.ReadLine() ?? "";
    }

    /// <summary>Saisie masquée (sauf entrée redirigée).</summary>
    private static string LireSecret(string invite)
    {
        Console.Write(invite);
        if (Console.IsInputRedirected) return Console.ReadLine() ?? "";
        var sb = new StringBuilder();
        while (true)
        {
            var k = Console.ReadKey(intercept: true);
            if (k.Key == ConsoleKey.Enter) break;
            if (k.Key == ConsoleKey.Backspace) { if (sb.Length > 0) sb.Length--; continue; }
            if (!char.IsControl(k.KeyChar)) sb.Append(k.KeyChar);
        }
        Console.WriteLine();
        return sb.ToString();
    }
}
