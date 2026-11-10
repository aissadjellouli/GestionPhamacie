using Pharma.Domain.Commun;

namespace Pharma.Domain.Securite;

/// <summary>Mot de passe choisi par l'employé (premier changement obligatoire, puis à volonté).</summary>
public static class RegleMotDePasse
{
    public const int LongueurMin = 8;

    /// <summary>Revendication du jeton tant que le mot de passe provisoire n'est pas changé : aucun droit n'est accordé.</summary>
    public const string Revendication = "mot_de_passe";
    public const string AChanger = "a_changer";

    public static void Verifier(string? nouveau, string? confirmation, string? ancien)
    {
        if (string.IsNullOrEmpty(nouveau) || nouveau.Length < LongueurMin)
            throw new RegleMetierException($"Le nouveau mot de passe doit compter au moins {LongueurMin} caractères.");
        if (nouveau != confirmation) throw new RegleMetierException("La confirmation ne correspond pas au nouveau mot de passe.");
        if (nouveau == ancien) throw new RegleMetierException("Choisissez un mot de passe différent de l’actuel.");
    }
}
