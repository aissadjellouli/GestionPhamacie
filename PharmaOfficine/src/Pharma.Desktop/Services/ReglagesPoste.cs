using System.IO;
using System.Text.Json;

namespace Pharma.Desktop.Services;

/// <summary>
/// Réglages propres à ce PC (imprimantes), enregistrés dans C:\ProgramData\PharmaOfficine\poste.json :
/// chaque poste a ses propres imprimantes. Les variables d'environnement servent de valeur par défaut.
/// </summary>
public sealed class ReglagesPoste
{
    public sealed class Valeurs
    {
        /// <summary>Nom de l'imprimante Windows des tickets ; null : PHARMA_IMPRIMANTE_TICKETS, sinon l'imprimante par défaut.</summary>
        public string? ImprimanteTickets { get; set; }
        public int? LargeurTicketMm { get; set; }
        public bool TicketAutomatique { get; set; }
        /// <summary>Adresse IP de l'imprimante d'étiquettes (ZPL, port 9100).</summary>
        public string? ImprimanteEtiquettes { get; set; }
        /// <summary>Afficheur client : aucun, ecran (écran secondaire), vfd (2 lignes sur port série) ou les_deux.</summary>
        public string? Afficheur { get; set; }
        /// <summary>Numéro de l'écran client (1, 2…) ; null : le premier écran qui n'est pas le principal.</summary>
        public int? EcranClient { get; set; }
        public string? PortAfficheur { get; set; }
        public int? VitesseAfficheur { get; set; }
        /// <summary>epson (par défaut) ou cd5220.</summary>
        public string? ProtocoleAfficheur { get; set; }
    }

    private static readonly string Fichier =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "PharmaOfficine", "poste.json");

    public Valeurs Actuelles { get; private set; } = Lire();

    private static Valeurs Lire()
    {
        try
        {
            return File.Exists(Fichier) ? JsonSerializer.Deserialize<Valeurs>(File.ReadAllText(Fichier)) ?? new() : new();
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
        {
            return new();   // fichier illisible : réglages par défaut (variables d'environnement)
        }
    }

    /// <summary>Enregistre ; renvoie un message d'erreur lisible, ou null.</summary>
    public string? Enregistrer(Valeurs v)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Fichier)!);
            File.WriteAllText(Fichier, JsonSerializer.Serialize(v, new JsonSerializerOptions { WriteIndented = true }));
            Actuelles = v;
            return null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return $"Réglages non enregistrés ({Fichier}) : {e.Message}";
        }
    }

    /// <summary>Applique des réglages sans les enregistrer (impression d'essai).</summary>
    public void Appliquer(Valeurs v) => Actuelles = v;

    public static string CheminFichier => Fichier;
}
