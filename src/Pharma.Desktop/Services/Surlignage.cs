using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;

namespace Pharma.Desktop.Services;

/// <summary>Propositions pendant la frappe (docs/charte-graphique.md) : partie tapée surlignée (balise mark), sans majuscules ni accents.</summary>
public static class Surlignage
{
    public static RenderFragment Surligner(string? texte, string? saisie) => builder => Ecrire(builder, texte ?? "", saisie);

    private static void Ecrire(RenderTreeBuilder builder, string texte, string? saisie)
    {
        var (debut, longueur) = RechercheTexte.Position(texte, saisie);
        if (debut < 0) { builder.AddContent(0, texte); return; }
        builder.AddContent(1, texte[..debut]);
        builder.OpenElement(2, "mark");
        builder.AddContent(3, texte.Substring(debut, longueur));
        builder.CloseElement();
        builder.AddContent(4, texte[(debut + longueur)..]);
    }
}
