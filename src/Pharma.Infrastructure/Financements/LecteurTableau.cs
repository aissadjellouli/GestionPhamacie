using System.Text;
using Pharma.Application.Financements;
using Pharma.Domain.Commun;
using Pharma.Domain.Financements;
using Pharma.Infrastructure.Catalogue;

namespace Pharma.Infrastructure.Financements;

/// <summary>
/// Tableau d'échéances fourni par une banque : classeur Excel (.xlsx, première feuille qui contient des lignes) ou fichier CSV
/// (UTF-8, sinon Windows-1252 ; séparateur détecté). Cellules rendues en texte, sans rien interpréter : la correspondance des
/// colonnes et l'aperçu viennent ensuite.
/// </summary>
public sealed class LecteurTableau : ILecteurTableau
{
    static LecteurTableau() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    public IReadOnlyList<IReadOnlyList<string?>> Lire(string nomFichier, byte[] contenu)
    {
        var extension = Path.GetExtension(nomFichier).ToLowerInvariant();
        if (extension == ".xlsx")
        {
            try
            {
                using var flux = new MemoryStream(contenu);
                return LecteurXlsx.Lire(flux).Select(f => f.Lignes).FirstOrDefault(l => l.Any(x => x.Any(c => !string.IsNullOrWhiteSpace(c)))) ?? [];
            }
            catch (FormatException e)
            {
                throw new RegleMetierException(e.Message);
            }
        }
        if (extension is ".csv" or ".txt") return LectureEcheancier.Csv(Decoder(contenu));
        throw new RegleMetierException("Format non lu : choisissez un classeur Excel (.xlsx) ou un fichier CSV. Un PDF ou un scan se lit sur le poste.");
    }

    /// <summary>UTF-8 (avec ou sans BOM) s'il est valide, sinon Windows-1252 (exports Excel « CSV (séparateur : point-virgule) »).</summary>
    private static string Decoder(byte[] contenu)
    {
        try
        {
            return new UTF8Encoding(false, true).GetString(contenu).TrimStart('﻿');
        }
        catch (DecoderFallbackException)
        {
            return Encoding.GetEncoding(1252).GetString(contenu);
        }
    }
}
