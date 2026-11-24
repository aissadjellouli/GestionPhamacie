using System.Text;
using Pharma.Application.Postes;
using Pharma.Application.Securite;

namespace Pharma.Api.Securite;

/// <summary>
/// Autorisation d'un poste sur le PC serveur, pour le démarrage (aucun titulaire encore connecté depuis un poste enrôlé) :
/// Pharma.Api.exe --autoriser-poste "CAISSE-1" [--identifiant X] (le serveur ne démarre pas). Réservée à un compte titulaire (identifiant et mot de
/// passe contrôlés). Crée le poste en attente et affiche son code d'enrôlement (valable la durée réglée, 15 min par défaut), à saisir sur le poste.
/// Inscrit au journal des actions comme depuis l'écran Administration › Postes.
/// </summary>
public static class ConsoleAutoriserPoste
{
    public const string Argument = "--autoriser-poste";

    public static bool Concerne(string[] args) => args.Contains(Argument);

    public static async Task<int> ExecuterAsync(IServiceProvider services, string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        using var portee = services.CreateScope();
        var sp = portee.ServiceProvider;
        var journal = sp.GetRequiredService<ILoggerFactory>().CreateLogger("Pharma.PostesAutorises");
        try
        {
            var nom = ConsoleLeverEcart.Option(args, Argument) ?? Lire("Nom du poste à autoriser (ex. CAISSE-1) : ");
            var identifiant = ConsoleLeverEcart.Option(args, "--identifiant") ?? Lire("Identifiant du titulaire : ");
            var motDePasse = LireSecret("Mot de passe : ");
            var compte = string.IsNullOrWhiteSpace(identifiant) ? null : await sp.GetRequiredService<IComptesLecture>().TrouverActifAsync(identifiant.Trim());
            var verificateur = sp.GetRequiredService<IVerificateurMotDePasse>();
            if (compte is null) verificateur.VerifierFactice(motDePasse);
            if (compte is null || !compte.Actif || !verificateur.Verifier(motDePasse, compte.MotDePasseHash))
                return Echec("Identifiant ou mot de passe faux : rien n'a été créé.");
            if (!await sp.GetRequiredService<Pharma.Infrastructure.Securite.PermissionService>().EstTitulaireAsync(compte.Id))
                return Echec("Seul un compte titulaire peut autoriser un poste : rien n'a été créé.");

            var code = await sp.GetRequiredService<GererPostesAutorises>().AutoriserAsync(compte.Id, nom, verifierTitulaire: false);
            journal.LogInformation("Poste {PosteId} autorisé depuis la console par le compte {UtilisateurId}", code.PosteId, compte.Id);
            Console.WriteLine($"Poste « {code.Nom} » autorisé. Code à saisir sur ce poste : {code.Code}");
            Console.WriteLine($"Valable jusqu'à {code.ExpireLe.ToLocalTime():HH:mm} (une seule fois).");
            return 0;
        }
        catch (Pharma.Domain.Commun.RegleMetierException e) { return Echec(e.Message + " Rien n'a été créé."); }
        catch (RefusPosteException e) { return Echec(e.Message + " Rien n'a été créé."); }
        catch (Exception e) when (e is Npgsql.NpgsqlException or InvalidOperationException)
        {
            journal.LogError(e, "Postes autorisés : autorisation impossible depuis la console");
            return Echec("Autorisation impossible (base injoignable ou script 167 non passé) : voir le journal du serveur.");
        }
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
