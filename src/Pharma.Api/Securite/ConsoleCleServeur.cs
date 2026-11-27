using Pharma.Application.Offre;
using Pharma.Infrastructure.Offre;

namespace Pharma.Api.Securite;

/// <summary>
/// Clé de CE serveur pour l'offre (décision du titulaire du 2026-11-27, point 5 « matériel ») ; le serveur ne démarre pas, la base n'est pas lue
/// (fonctionne aussi sur un 2e serveur en veille ou un PC de secours) :
/// <list type="bullet">
/// <item>Pharma.Api.exe --creer-cle-serveur : crée la clé (TPM, sinon logicielle non exportable avec un avertissement), en administrateur, à
/// l'installation de chaque serveur ; idempotente : une clé existante n'est jamais remplacée (son empreinte est affichée).</item>
/// <item>Pharma.Api.exe --empreinte-serveur : affiche l'empreinte à transmettre à l'éditeur (elle est aussi dans Administration › Mon offre).</item>
/// </list>
/// </summary>
public static class ConsoleCleServeur
{
    public const string ArgumentCreer = "--creer-cle-serveur", ArgumentEmpreinte = "--empreinte-serveur";

    public static bool Concerne(string[] args) => args.Contains(ArgumentCreer) || args.Contains(ArgumentEmpreinte);

    public static int Executer(string[] args, string racineDonnees) => Executer(args, CleServeurLocale.Creer(racineDonnees), Console.Out, Console.Error);

    public static int Executer(string[] args, ICleServeurLocale cle, TextWriter sortie, TextWriter erreurs)
    {
        try
        {
            if (args.Contains(ArgumentCreer))
            {
                var info = cle.CreerSiAbsente();
                sortie.WriteLine(info.Creee ? "Clé de ce serveur créée." : "La clé de ce serveur existe déjà : elle est gardée (jamais remplacée).");
                Ecrire(info, sortie);
                return 0;
            }
            if (cle.Lire() is not { } lue)
            {
                if (cle.AccesRefuse())
                {
                    erreurs.WriteLine("La clé de ce serveur existe, mais ce compte n’a pas le droit de la lire : relancez la commande en administrateur. "
                                      + "Ne la recréez pas (un nouveau code d’activation serait nécessaire).");
                    return 1;
                }
                erreurs.WriteLine($"Aucune clé sur ce serveur : créez-la avec Pharma.Api.exe {ArgumentCreer} (invite de commandes en administrateur).");
                return 1;
            }
            Ecrire(lue, sortie);
            return 0;
        }
        catch (InvalidOperationException e)
        {
            erreurs.WriteLine($"Refusé : {e.Message}");
            return 1;
        }
    }

    private static void Ecrire(InfoCleServeur info, TextWriter sortie)
    {
        sortie.WriteLine($"Fournisseur : {info.Fournisseur}");
        if (!info.Materielle)
            sortie.WriteLine("ATTENTION : clé LOGICIELLE (pas de TPM utilisable sur ce PC). Elle reste non exportable, mais elle est moins bien protégée "
                             + "qu'une clé TPM. Rien n'est bloqué. Si ce PC a un TPM désactivé (BIOS/UEFI), signalez-le : changer de clé demande "
                             + "ensuite un nouveau code de l'éditeur.");
        sortie.WriteLine($"Empreinte de ce serveur (à transmettre à l'éditeur) : {EmpreinteServeur.Afficher(info.Empreinte)}");
    }
}
