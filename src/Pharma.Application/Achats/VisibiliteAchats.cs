using Pharma.Application.Securite;
using Pharma.Domain.Securite;

namespace Pharma.Application.Achats;

/// <summary>
/// Ce qu'un employé voit des achats (décision du titulaire, 2026-11-15) : les prix d'achat (prix HT des bons, montants des bons,
/// des réceptions et des retours, dernier prix, prix des lots retournables) demandent le droit « rapports.marges » ; la dette envers
/// les grossistes demande le droit « achats.regler ». Le titulaire voit tout. Sans le droit, le serveur n'envoie pas la valeur (null).
/// </summary>
public sealed record VisibiliteAchats(bool PrixAchat, bool DetteGrossistes)
{
    public static async Task<VisibiliteAchats> LireAsync(IPermissionService droits, long utilisateurId, CancellationToken ct = default)
    {
        var titulaire = await droits.EstTitulaireAsync(utilisateurId, ct);
        return new(titulaire || await droits.APermissionAsync(utilisateurId, Permissions.RapportsMarges, ct),
            titulaire || await droits.APermissionAsync(utilisateurId, Permissions.AchatsRegler, ct));
    }

    /// <summary>Prix ou montant au prix d'achat : null sans le droit.</summary>
    public decimal? Prix(decimal valeur) => PrixAchat ? valeur : null;
    public decimal? Prix(decimal? valeur) => PrixAchat ? valeur : null;

    /// <summary>Dette envers un grossiste : null sans le droit.</summary>
    public decimal? Dette(decimal valeur) => DetteGrossistes ? valeur : null;
}
