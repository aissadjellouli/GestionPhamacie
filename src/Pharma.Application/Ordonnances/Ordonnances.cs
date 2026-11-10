using Pharma.Domain.Catalogue;
using Pharma.Domain.Commun;

namespace Pharma.Application.Ordonnances;

/// <summary>Produit du catalogue rapproché d'une ligne lue sur l'ordonnance (score de 0 à 1).</summary>
public sealed record ProduitCandidat(
    long ProduitId, string NomCommercial, string? Dci, string? Dosage, string? Forme,
    decimal PrixPublic, int Stock, ClasseReglementaire Classe, double Score);

/// <summary>Ligne lue et produits proposés ; NombreBoites quand l'ordonnance l'écrit (« 2 boîtes »), sinon null.</summary>
public sealed record LigneLue(string Texte, IReadOnlyList<ProduitCandidat> Candidats, int? NombreBoites = null);

/// <summary>Résultat de l'analyse : lignes rapprochées du catalogue, et nombre de lignes sans correspondance.</summary>
public sealed record AnalyseOrdonnance(IReadOnlyList<LigneLue> Lignes, int LignesIgnorees);

public interface IRapprochementCatalogue
{
    /// <summary>Produits actifs dont le nom ou la DCI ressemble au texte, meilleurs d'abord, score minimal compris.</summary>
    Task<IReadOnlyList<ProduitCandidat>> RapprocherAsync(string texte, int limite, double scoreMinimal, CancellationToken ct = default);
}

public interface IStockageOrdonnances
{
    /// <summary>Enregistre l'image et renvoie son jeton (nom de fichier généré, jamais fourni par le poste).</summary>
    Task<string> EnregistrerAsync(byte[] contenu, string extension, CancellationToken ct = default);
    bool Existe(string jeton);
}

/// <summary>
/// Rapproche les lignes lues par l'OCR du poste avec le catalogue. Ne décide rien : le caissier valide chaque ligne.
/// </summary>
public sealed partial class AnalyserOrdonnance(IRapprochementCatalogue catalogue)
{
    public const int MaxLignes = 60;
    public const int CandidatsParLigne = 3;
    public const double ScoreMinimal = 0.45;

    public async Task<AnalyseOrdonnance> ExecuterAsync(IReadOnlyList<string> lignes, CancellationToken ct = default)
    {
        var resultat = new List<LigneLue>();
        var ignorees = 0;
        foreach (var brute in lignes.Take(MaxLignes))
        {
            var texte = Nettoyer(brute);
            if (!PeutEtreUnMedicament(texte)) { ignorees++; continue; }
            var candidats = await catalogue.RapprocherAsync(texte, CandidatsParLigne, ScoreMinimal, ct);
            if (candidats.Count == 0) { ignorees++; continue; }
            // Un même produit lu sur deux lignes (nom puis posologie) n'est proposé qu'une fois
            if (resultat.Any(l => l.Candidats[0].ProduitId == candidats[0].ProduitId)) continue;
            resultat.Add(new LigneLue(texte, candidats, NombreBoites(texte)));
        }
        return new AnalyseOrdonnance(resultat, ignorees);
    }

    private static string Nettoyer(string? ligne) =>
        string.Join(' ', (ligne ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).Trim(' ', '-', '•', '*', '.', ':');

    /// <summary>« 2 boîtes », « 3 boites », « 1 bte » : nombre écrit sur la ligne (1 à 20), proposé au caissier qui le valide.</summary>
    private static int? NombreBoites(string texte) =>
        Boites().Match(texte) is { Success: true } m && int.TryParse(m.Groups[1].Value, out var n) && n is >= 1 and <= 20 ? n : null;

    [System.Text.RegularExpressions.GeneratedRegex(@"(\d{1,2})\s*(?:bo(?:i|î)tes?|btes?)\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase)]
    private static partial System.Text.RegularExpressions.Regex Boites();

    /// <summary>Au moins 4 lettres : écarte les dates, numéros et signatures lus par l'OCR.</summary>
    private static bool PeutEtreUnMedicament(string texte) => texte.Count(char.IsLetter) >= 4 && texte.Length <= 200;
}

/// <summary>Image de l'ordonnance (JPEG, PNG) conservée sur le serveur et liée ensuite à la vente.</summary>
public sealed class EnregistrerScanOrdonnance(IStockageOrdonnances stockage)
{
    public const int TailleMax = 10 * 1024 * 1024;

    public Task<string> ExecuterAsync(byte[] contenu, CancellationToken ct = default)
    {
        if (contenu.Length == 0)
            throw new RegleMetierException("Image d’ordonnance vide.");
        if (contenu.Length > TailleMax)
            throw new RegleMetierException("Image d’ordonnance trop lourde (10 Mo au maximum).");
        var extension = Format(contenu)
            ?? throw new RegleMetierException("Format d’image non reconnu : JPEG ou PNG attendu.");
        return stockage.EnregistrerAsync(contenu, extension, ct);
    }

    /// <summary>Le format est lu dans le contenu (signature du fichier), jamais dans un nom fourni par le poste.</summary>
    private static string? Format(byte[] c) =>
        c is [0xFF, 0xD8, 0xFF, ..] ? "jpg"
        : c is [0x89, 0x50, 0x4E, 0x47, ..] ? "png"
        : null;
}
