using System.Globalization;
using System.Text;
using Pharma.Domain.Commun;

namespace Pharma.Infrastructure.Impression;

public sealed record EtiquetteLot(
    string NomProduit,
    string NumeroLot,
    DateOnly DatePeremption,
    decimal PrixPublic,
    string CodeFournisseur,   // ex. "GR-03", court pour tenir sur l'étiquette
    string CodeInterne);      // EAN-13 commençant par 20

/// <summary>
/// Etiquette 50 x 30 mm pour imprimante thermique ZPL à 203 dpi
/// (Zebra et la plupart des compatibles).
/// </summary>
public static class EtiquetteZpl
{
    public static string Generer(EtiquetteLot e, int copies)
    {
        if (!Ean13.EstValide(e.CodeInterne))
            throw new ArgumentException("Code interne EAN-13 invalide.");

        var fr = CultureInfo.GetCultureInfo("fr-FR");
        var prix = e.PrixPublic.ToString("N2", fr);
        var peremption = e.DatePeremption.ToString("MM/yyyy", fr);

        return new StringBuilder()
            .Append("^XA^CI28^PW400^LL240")
            .Append($"^FO16,12^A0N,24,24^FB368,1,0,L^FD{Nettoyer(e.NomProduit)}^FS")
            .Append($"^FO16,42^A0N,20,20^FDLot {Nettoyer(e.NumeroLot)}  Exp {peremption}  {Nettoyer(e.CodeFournisseur)}^FS")
            // ^BE = EAN-13 : on envoie 12 chiffres, l'imprimante calcule la clé
            .Append($"^FO60,70^BY2^BEN,90,Y,N^FD{e.CodeInterne[..12]}^FS")
            .Append($"^FO16,200^A0N,26,26^FD{prix} DA^FS")
            .Append($"^PQ{Math.Max(1, copies)}")
            .Append("^XZ")
            .ToString();
    }

    // ^ et ~ sont des caractères de commande en ZPL
    private static string Nettoyer(string texte) =>
        texte.Replace("^", " ").Replace("~", " ");
}
