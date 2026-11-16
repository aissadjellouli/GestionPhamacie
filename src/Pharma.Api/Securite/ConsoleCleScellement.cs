using System.Text;
using Pharma.Application.SecondServeur;
using Pharma.Domain.Commun;

namespace Pharma.Api.Securite;

/// <summary>
/// Clé de scellement sur le 2e serveur, en ligne de commande (l'API de secours ne sert pas tant que sa base est en lecture seule) :
///   Pharma.Api.exe --importer-cle &lt;fichier .pocle&gt; [--remplacer]  : mot de passe tapé par le titulaire, clé rechiffrée DPAPI machine ;
///   Pharma.Api.exe --empreinte-cle                                 : empreinte publique de la clé de ce PC (à comparer au principal).
/// Journal du serveur : empreinte seulement, jamais la clé ni le mot de passe.
/// </summary>
public static class ConsoleCleScellement
{
    public const string ArgumentImport = "--importer-cle";
    public const string ArgumentEmpreinte = "--empreinte-cle";

    public static bool Concerne(string[] args) => args.Contains(ArgumentImport) || args.Contains(ArgumentEmpreinte);

    public static int Executer(IServiceProvider services, string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        var cle = services.GetRequiredService<ICleIntegriteStockee>();
        var journal = services.GetRequiredService<ILoggerFactory>().CreateLogger("Pharma.CleScellement");
        try
        {
            if (args.Contains(ArgumentEmpreinte))
            {
                if (!cle.Presente) return Echec($"Aucune clé de scellement sur ce PC ({cle.Emplacement}).");
                var brute = cle.Lire();
                try { Console.WriteLine($"Empreinte de la clé de scellement : {CoffreCle.Empreinte(brute)} ({cle.Emplacement})"); }
                finally { System.Security.Cryptography.CryptographicOperations.ZeroMemory(brute); }
                return 0;
            }

            var i = Array.IndexOf(args, ArgumentImport);
            var fichier = i + 1 < args.Length ? args[i + 1] : "";
            if (fichier.Length == 0 || fichier.StartsWith("--", StringComparison.Ordinal) || !File.Exists(fichier))
                return Echec("Indiquez le fichier d'export de la clé : Pharma.Api.exe --importer-cle E:\\cle-scellement-….pocle");
            var motDePasse = LireSecret("Mot de passe du fichier de clé (choisi par le titulaire à l'export) : ");
            using var portee = services.CreateScope();
            var r = portee.ServiceProvider.GetRequiredService<ImporterCleScellement>().Executer(File.ReadAllBytes(fichier), motDePasse, args.Contains("--remplacer"));
            journal.LogWarning("Clé de scellement : {Message}", r.Message);
            Console.WriteLine(r.Message);
            return 0;
        }
        catch (RegleMetierException e)
        {
            journal.LogWarning("Clé de scellement : import refusé ({Motif})", e.Message);
            return Echec(e.Message);
        }
        catch (Exception e) when (e is InvalidOperationException or IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            return Echec(e.Message);
        }
    }

    private static int Echec(string message)
    {
        Console.Error.WriteLine(message);
        return 1;
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
