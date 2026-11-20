using System.Collections.Concurrent;
using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.DataProtection;
using Pharma.Infrastructure.Securite;

namespace Pharma.Api.Securite;

/// <summary>
/// Protection des secrets locaux hors Windows : ASP.NET Core DataProtection (fourni par le framework, aucun paquet ajouté). Clés dans
/// <c>dossierCles</c>, chiffrées au repos par le certificat s'il est fourni (sinon protégées seulement par les droits du dossier).
/// Le dossier doit survivre aux redémarrages et aux mises à jour : le perdre rend illisibles les secrets protégés, comme un
/// changement de PC sous DPAPI. Préparé au premier usage seulement (un dossier inaccessible n'empêche pas l'API de démarrer).
/// </summary>
public sealed class ProtectionDataProtection : IProtectionDonnees
{
    public const string NomApplication = "PharmaOfficine";
    private readonly Lazy<IDataProtectionProvider> fournisseur;
    private readonly ConcurrentDictionary<string, IDataProtector> protecteurs = new();

    public ProtectionDataProtection(string dossierCles, string? certificatFichier = null, string? certificatMotDePasse = null)
    {
        DossierCles = dossierCles;
        fournisseur = new Lazy<IDataProtectionProvider>(() => Creer(dossierCles, certificatFichier, certificatMotDePasse),
            LazyThreadSafetyMode.ExecutionAndPublication);
        Description = $"DataProtection (clés dans {dossierCles}{(string.IsNullOrWhiteSpace(certificatFichier) ? ", sans certificat" : ", chiffrées par certificat")})";
    }

    public string Description { get; }
    public string DossierCles { get; }

    public byte[] Proteger(byte[] clair, string usage) => Protecteur(usage).Protect(clair);

    public byte[] Deproteger(byte[] protege, string usage) => Protecteur(usage).Unprotect(protege);

    private IDataProtector Protecteur(string usage) => protecteurs.GetOrAdd(usage, u => fournisseur.Value.CreateProtector(u));

    private static IDataProtectionProvider Creer(string dossier, string? certificatFichier, string? motDePasse)
    {
        Directory.CreateDirectory(dossier);
        FichiersProteges.RestreindreDossierSiPossible(dossier);
        var repertoire = new DirectoryInfo(dossier);
        if (string.IsNullOrWhiteSpace(certificatFichier))
            return DataProtectionProvider.Create(repertoire, b => b.SetApplicationName(NomApplication));
        var certificat = X509CertificateLoader.LoadPkcs12FromFile(certificatFichier, motDePasse);
        return DataProtectionProvider.Create(repertoire, b => b.SetApplicationName(NomApplication), certificat);
    }

    /// <summary>Implémentation retenue : DPAPI machine (Windows, sans réglage) ou DataProtection (hors Windows, ou réglage Protection:Mode).</summary>
    public static IProtectionDonnees Choisir(ReglagesProtection reglages, string racineDonnees, bool windows) =>
        ChoixProtection.Mode(reglages.Mode, windows) == ChoixProtection.Dpapi
            ? new ProtectionDpapiMachine()
            : new ProtectionDataProtection(ChoixProtection.DossierCles(reglages, racineDonnees), reglages.CertificatFichier, reglages.CertificatMotDePasse);
}
