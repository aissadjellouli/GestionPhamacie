using System.ComponentModel;
using System.Diagnostics;
using System.Text.RegularExpressions;

namespace Pharma.Desktop.Services;

/// <summary>
/// Création de la clé du poste en administrateur (D4, option 1) : le logiciel se relance avec « --creer-cle-poste &lt;nom&gt; » par la demande d'élévation de
/// Windows (UAC), crée la clé machine avec son descripteur de sécurité, ouvre le dossier de l'identité aux comptes du PC, puis s'arrête sans fenêtre. Le nom
/// de clé est contrôlé (forme fixe) : le processus élevé ne crée rien d'autre. L'usage courant du poste reste en compte standard.
/// </summary>
public static class CreationCleAdministrateur
{
    public const string Argument = "--creer-cle-poste";
    public const int Reussi = 0, Echec = 1, ArgumentsInvalides = 2;
    private static readonly Regex NomValide = new("^PharmaOfficine-Poste-[0-9a-f]{12}$", RegexOptions.CultureInvariant);
    private const int AnnuleParUtilisateur = 1223;   // ERROR_CANCELLED : demande d'élévation refusée

    public static bool NomAccepte(string? nom) => nom is not null && NomValide.IsMatch(nom);

    /// <summary>Ligne de commande du processus élevé ; null si ce n'est pas ce mode (démarrage normal du poste).</summary>
    public static int? Traiter(string[] args, Func<string, int> creer)
    {
        if (args.Length == 0 || args[0] != Argument) return null;
        if (args.Length != 2 || !NomAccepte(args[1])) return ArgumentsInvalides;
        return creer(args[1]);
    }

    /// <summary>Relance le logiciel en administrateur pour créer la clé nommée et attend sa fin (2 minutes au plus). Refus ou échec : InvalidOperationException.</summary>
    public static void Lancer(string nom)
    {
        if (!NomAccepte(nom)) throw new InvalidOperationException("Nom de clé du poste invalide.");
        var exe = Environment.ProcessPath
            ?? throw new InvalidOperationException("Chemin du logiciel inconnu : lancez Tracodex Officine en administrateur pour autoriser ce poste.");
        try
        {
            using var p = Process.Start(new ProcessStartInfo(exe, $"{Argument} {nom}") { UseShellExecute = true, Verb = "runas" })
                ?? throw new InvalidOperationException("Windows n'a pas lancé la création de la clé du poste.");
            if (!p.WaitForExit(TimeSpan.FromMinutes(2)))
                throw new InvalidOperationException("La création de la clé du poste n'a pas abouti en 2 minutes : réessayez.");
        }
        catch (Win32Exception e) when (e.NativeErrorCode == AnnuleParUtilisateur)
        {
            throw new InvalidOperationException(
                "Autorisation annulée : Windows demande une seule fois l'accord d'un administrateur du PC pour créer la clé de ce poste.", e);
        }
        catch (Win32Exception e)
        {
            throw new InvalidOperationException("Windows n'a pas lancé la création de la clé du poste en administrateur.", e);
        }
    }
}
