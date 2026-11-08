using System.Diagnostics;
using System.IO;
using System.Text;
using Pharma.Shared;

namespace Pharma.Desktop.Services;

/// <summary>
/// Liste des patients à contacter après un rappel de lot, en CSV lisible par Excel
/// (séparateur « ; », UTF-8 avec BOM). Enregistrée dans Documents\Pharma Officine\Rappels.
/// Contient des données de patients : fichier local au poste, à ne pas diffuser.
/// </summary>
public static class ExportRappel
{
    public static string Enregistrer(RappelLotDto rappel)
    {
        var dossier = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Pharma Officine", "Rappels");
        Directory.CreateDirectory(dossier);
        var nom = $"rappel_{Nettoyer(rappel.NumeroLot)}_{DateTime.Now:yyyyMMdd_HHmm}.csv";
        var chemin = Path.Combine(dossier, nom);

        var csv = new StringBuilder()
            .AppendLine($"Produit;{Champ(rappel.Produit)}")
            .AppendLine($"Lot;{Champ(rappel.NumeroLot)}")
            .AppendLine()
            .AppendLine("Patient;Téléphone;Date de vente;Ticket;Boîtes");
        foreach (var v in rappel.Ventes)
            csv.AppendLine(string.Join(';',
                Champ(v.Patient), Champ(v.Telephone ?? ""), Libelles.DateHeureLocale(v.DateVente), Champ(v.Ticket), v.Boites));

        File.WriteAllText(chemin, csv.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        return chemin;
    }

    /// <summary>Ouvre l'Explorateur sur le fichier.</summary>
    public static void Montrer(string chemin) =>
        Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{chemin}\"") { UseShellExecute = true });

    private static string Champ(string valeur) =>
        valeur.IndexOfAny([';', '"', '\n', '\r']) >= 0 ? $"\"{valeur.Replace("\"", "\"\"")}\"" : valeur;

    private static string Nettoyer(string texte) =>
        string.Concat(texte.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '_'));
}
