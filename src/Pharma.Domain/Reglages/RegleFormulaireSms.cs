using Pharma.Domain.Commun;

namespace Pharma.Domain.Reglages;

/// <summary>Texte du formulaire d'accord SMS (table parametre_formulaire_sms), extrait de FormulaireAccordSmsPatient.ModifierTexteAsync.</summary>
public static class RegleFormulaireSms
{
    public const int LongueurMax = 4000;

    /// <summary>Texte rogné de 4000 caractères au plus (vide ou null accepté : texte par défaut).</summary>
    public static void VerifierLongueur(string? texte)
    {
        if (texte?.Trim() is { Length: > LongueurMax })
            throw new RegleMetierException($"Texte trop long (au plus {LongueurMax} caractères).", "texte_formulaire_sms");
    }

    /// <summary>
    /// Texte à enregistrer : vide ou identique au texte par défaut = null (retour au texte par défaut) ; sinon rogné, 4000 caractères au plus.
    /// </summary>
    public static string? Normaliser(string? texte, string texteParDefaut)
    {
        var t = string.IsNullOrWhiteSpace(texte) || texte.Trim() == texteParDefaut ? null : texte.Trim();
        VerifierLongueur(t);
        return t;
    }
}
