using Pharma.Domain.Commun;

namespace Pharma.Application.Securite;

/// <summary>Affichage du poste choisi par l'employé : auto (suit Windows), clair ou sombre.</summary>
public interface IPreferencesAffichage
{
    Task<string> LireAsync(long utilisateurId, CancellationToken ct = default);
    Task EnregistrerAsync(long utilisateurId, string affichage, CancellationToken ct = default);
}

public static class Affichage
{
    public static readonly string[] Valeurs = ["auto", "clair", "sombre"];

    public static string Verifier(string? affichage) =>
        affichage is not null && Valeurs.Contains(affichage)
            ? affichage
            : throw new RegleMetierException("Affichage inconnu : auto, clair ou sombre.");
}

/// <summary>Chaque employé choisit son propre affichage, sans droit particulier (y compris connecté par code comptoir).</summary>
public sealed class ChoisirAffichage(IPreferencesAffichage preferences)
{
    public Task ExecuterAsync(long utilisateurId, string? affichage, CancellationToken ct = default) =>
        preferences.EnregistrerAsync(utilisateurId, Affichage.Verifier(affichage), ct);
}

public sealed class LireAffichage(IPreferencesAffichage preferences)
{
    public Task<string> ExecuterAsync(long utilisateurId, CancellationToken ct = default) => preferences.LireAsync(utilisateurId, ct);
}
