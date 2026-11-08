using System.Text.RegularExpressions;
using Pharma.Application.Ordonnances;

namespace Pharma.Infrastructure.Ordonnances;

/// <summary>
/// Images d'ordonnances dans un dossier du serveur (réglage Ordonnances:Dossier), à inclure dans la sauvegarde.
/// Le nom de fichier est généré ici : un jeton reçu du poste n'est accepté que s'il a exactement cette forme.
/// </summary>
public sealed partial class StockageOrdonnancesFichiers(string dossier) : IStockageOrdonnances
{
    public async Task<string> EnregistrerAsync(byte[] contenu, string extension, CancellationToken ct = default)
    {
        Directory.CreateDirectory(dossier);
        var jeton = $"{Guid.NewGuid():N}.{extension}";
        await File.WriteAllBytesAsync(Path.Combine(dossier, jeton), contenu, ct);
        return jeton;
    }

    public bool Existe(string jeton) => Jeton().IsMatch(jeton) && File.Exists(Path.Combine(dossier, jeton));

    [GeneratedRegex("^[0-9a-f]{32}\\.(jpg|png)$")]
    private static partial Regex Jeton();
}
