using Pharma.Application.SecondServeur;

namespace Pharma.Api.Securite;

/// <summary>
/// Pharma.Api.exe --empreinte-jeton : affiche l'empreinte (16 caractères) de la clé des jetons (Jwt__Cle) de CE PC, à comparer à celle de l'autre
/// serveur (installation du 2e serveur, docs/second-serveur-local.md). Jamais la clé ni sa dérivée ; le serveur ne démarre pas.
/// </summary>
public static class ConsoleEmpreinteJeton
{
    public const string Argument = "--empreinte-jeton";

    public static bool Concerne(string[] args) => args.Contains(Argument);

    public static int Executer(IConfiguration config)
    {
        var cle = config["Jwt:Cle"];
        if (string.IsNullOrWhiteSpace(cle))
        {
            Console.Error.WriteLine("Jwt__Cle absente sur ce PC : aucune empreinte à afficher.");
            return 1;
        }
        Console.WriteLine($"Empreinte de la clé des jetons : {CleInterServeurs.Empreinte(cle)}");
        return 0;
    }
}
