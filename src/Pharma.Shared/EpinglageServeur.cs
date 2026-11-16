using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Pharma.Shared;

/// <summary>
/// Épinglage du serveur central (conception § 6.1, point 2) : l'officine n'accepte que le certificat HTTPS signé par l'autorité
/// du serveur central attendue, jamais un certificat « d'apparence valide » d'une autre autorité. Avant activation, l'autorité
/// est reconnue par le début de son empreinte SHA-256 porté par le code d'activation ; ensuite par son certificat enregistré.
/// </summary>
public static class EpinglageServeur
{
    /// <param name="autorite">Autorité enregistrée (après activation), sinon null.</param>
    /// <param name="epinglage">Début d'empreinte de l'autorité (code d'activation), si l'autorité n'est pas encore enregistrée.</param>
    public static bool Accepter(X509Certificate2? serveur, X509Chain? chaine, SslPolicyErrors erreurs, X509Certificate2? autorite, string? epinglage)
    {
        if (serveur is null) return false;
        if ((erreurs & (SslPolicyErrors.RemoteCertificateNameMismatch | SslPolicyErrors.RemoteCertificateNotAvailable)) != 0) return false;
        var racine = autorite;
        if (racine is null)
        {
            if (string.IsNullOrEmpty(epinglage) || chaine is null) return false;
            // Autorité envoyée par le serveur avec son certificat ; reconnue seulement par l'empreinte du code
            racine = chaine.ChainElements.Select(e => e.Certificate).Concat(chaine.ChainPolicy.ExtraStore)
                .FirstOrDefault(c => Convert.ToHexStringLower(SHA256.HashData(c.RawData)).StartsWith(epinglage, StringComparison.Ordinal));
            if (racine is null) return false;
        }
        using var ch = new X509Chain();
        ch.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        ch.ChainPolicy.CustomTrustStore.Add(racine);
        ch.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        ch.ChainPolicy.ApplicationPolicy.Add(new Oid("1.3.6.1.5.5.7.3.1"));
        return ch.Build(serveur) && ch.ChainElements[^1].Certificate.RawData.AsSpan().SequenceEqual(racine.RawData);
    }
}
