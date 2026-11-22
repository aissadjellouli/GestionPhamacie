using Pharma.Api.Securite;
using Pharma.Application.Securite;

namespace Pharma.Architecture.Tests;

/// <summary>
/// Le service de droits de l'API (PermissionsComptoir, qui enveloppe le service de la base) implémente lui-même chaque méthode de
/// IPermissionService : une méthode laissée à sa valeur par défaut de l'interface ne passe pas par la base (2026-11-22 : les refus
/// de droit affichaient « prix.modifier » au lieu de « Modifier un prix de vente »).
/// </summary>
public class PermissionsComptoirTests
{
    [Fact]
    public void Chaque_methode_du_service_de_droits_est_implementee_par_l_enveloppe_de_l_api()
    {
        var carte = typeof(PermissionsComptoir).GetInterfaceMap(typeof(IPermissionService));
        var parDefaut = carte.InterfaceMethods.Zip(carte.TargetMethods)
            .Where(m => m.Second.DeclaringType != typeof(PermissionsComptoir))
            .Select(m => m.First.Name).ToList();
        Assert.True(parDefaut.Count == 0, "Méthodes laissées à la valeur par défaut de l'interface : " + string.Join(", ", parDefaut));
    }
}
