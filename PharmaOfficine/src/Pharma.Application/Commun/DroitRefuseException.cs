namespace Pharma.Application.Commun;

/// <summary>L'employé n'a pas le droit nécessaire pour cette partie de l'action (renvoyé en 403).</summary>
public sealed class DroitRefuseException(string permission, string action)
    : Exception(permission == Securite.PermissionServiceExtensions.MarqueTitulaire
        ? $"Seul le pharmacien titulaire peut {action}."
        : $"Le droit « {permission} » est nécessaire pour {action}.")
{
    public string Permission { get; } = permission;
}
