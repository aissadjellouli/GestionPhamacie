using System.IO;
using Pharma.Shared;

namespace Pharma.Desktop.Services;

/// <summary>
/// Journal local de la liaison du poste avec le serveur : liaison.log du dossier des journaux du poste (<see cref="JournauxPoste"/>).
/// Une ligne à chaque changement d'état (jamais une ligne toutes les 15 secondes) ; état seulement, ni adresse ni donnée personnelle.
/// </summary>
public static class JournalLiaison
{
    private static readonly Dictionary<string, EtatLiaison> Derniers = [];
    private static readonly object Verrou = new();

    public static void Noter(string contexte, EtatLiaison etat, Exception? ex)
    {
        lock (Verrou)
        {
            if (Derniers.TryGetValue(contexte, out var avant) && avant == etat) return;
            Derniers[contexte] = etat;
            try
            {
                var dossier = JournauxPoste.Dossier;
                Directory.CreateDirectory(dossier);
                File.AppendAllText(Path.Combine(dossier, "liaison.log"),
                    $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {MasqueurJournal.Masquer(LiaisonServeur.LigneJournal(contexte, etat, ex))}{Environment.NewLine}");
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
    }
}
