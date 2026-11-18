using Pharma.Application.Synchronisation;

namespace Pharma.Api.Synchronisation;

/// <summary>Lot S5.3 : téléchargement d'un fichier de référentiel à l'adresse du serveur central enregistrée dans l'officine.</summary>
public sealed class TelechargementReferentielHttp(IEnvoiSyncDepot parametres, ClientServeurCentralHttp client) : ITelechargementReferentiel
{
    public async Task<byte[]> TelechargerAsync(long fichier, long octets, CancellationToken ct = default)
    {
        var p = await parametres.ParametresAsync(ct);
        if (string.IsNullOrWhiteSpace(p.Adresse)) throw new ServeurCentralException("adresse du serveur central non renseignée");
        return await client.TelechargerReferentielAsync(p.Adresse, fichier, octets, ct);
    }
}
