using System.IO;
using Pharma.Shared.Journalisation;

namespace Pharma.Desktop.Services;

/// <summary>
/// Dossier des journaux du poste (erreurs.log, liaison.log) : C:\Logs\PharmaOfficine\Poste\&lt;nom du PC&gt; (dossier commun des journaux,
/// décision du titulaire du 2026-11-28), commun aux comptes Windows du PC. Si ce dossier ne peut pas être créé ou écrit (PC non préparé,
/// compte sans droit) : l'ancien emplacement, %LOCALAPPDATA%\PharmaOfficine, propre à chaque compte Windows. Choisi une fois par lancement.
/// </summary>
public static class JournauxPoste
{
    private static readonly Lazy<string> Choisi = new(() =>
        DossiersJournaux.Choisir(DossiersJournaux.Windows(DossiersJournaux.Poste, Environment.MachineName), AncienDossier()));

    public static string AncienDossier() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PharmaOfficine");

    public static string Dossier => Choisi.Value;
}
