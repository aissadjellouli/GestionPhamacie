namespace Pharma.Application.Commun;

/// <summary>L'employé n'a pas le droit nécessaire pour cette partie de l'action (renvoyé en 403).</summary>
/// <param name="seuil">
/// Remise refusée (caisse.remise) : pourcentage demandé ; la fenêtre « Faire autoriser » ne propose que les employés dont le
/// maximum de remise le couvre.
/// </param>
public sealed class DroitRefuseException(string permission, string action, string? message = null, decimal? seuil = null)
    : Exception(message ?? (permission == Securite.PermissionServiceExtensions.MarqueTitulaire
        ? $"Seul le pharmacien titulaire peut {action}."
        : $"Le droit « {permission} » est nécessaire pour {action}."))
{
    public string Permission { get; } = permission;
    public decimal? Seuil { get; } = seuil;
}
