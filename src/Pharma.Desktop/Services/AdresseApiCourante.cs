using System.Net.Http;

namespace Pharma.Desktop.Services;

/// <summary>
/// Adresse de l'API que ce poste appelle MAINTENANT (bascule sur le 2e serveur, relais, retour à la voie directe), changeable sans
/// redémarrage : le client HTTP garde son adresse de départ, RelaisAdresse réécrit schéma, hôte et port de chaque requête.
/// </summary>
public sealed class AdresseApiCourante
{
    private volatile Uri actuelle;

    public AdresseApiCourante(Uri initiale) => (Initiale, actuelle) = (initiale, initiale);

    /// <summary>Adresse prise au démarrage du poste (celle du client HTTP).</summary>
    public Uri Initiale { get; }

    public Uri Actuelle => actuelle;

    /// <summary>Levé après chaque changement d'adresse.</summary>
    public event Action? Changee;

    public void Changer(Uri adresse)
    {
        if (adresse == actuelle) return;
        actuelle = adresse;
        Changee?.Invoke();
    }

    /// <summary>Adresse d'API normalisée (barre finale) ; null si elle n'est pas une adresse http(s) absolue.</summary>
    public static Uri? Lire(string? adresse)
    {
        if (string.IsNullOrWhiteSpace(adresse)) return null;
        var texte = adresse.Trim();
        if (!texte.EndsWith('/')) texte += "/";
        return Uri.TryCreate(texte, UriKind.Absolute, out var uri) && (uri.Scheme is "http" or "https") ? uri : null;
    }

    /// <summary>Requête réécrite vers l'adresse courante : même chemin et même requête, hôte et port de la nouvelle adresse.</summary>
    public static Uri Reecrire(Uri requete, Uri initiale, Uri courante)
    {
        if (courante == initiale) return requete;
        var prefixeInitial = initiale.AbsolutePath.TrimEnd('/');
        var chemin = requete.AbsolutePath;
        var reste = prefixeInitial.Length > 0 && chemin.StartsWith(prefixeInitial, StringComparison.OrdinalIgnoreCase) ? chemin[prefixeInitial.Length..] : chemin;
        var b = new UriBuilder(requete)
        {
            Scheme = courante.Scheme,
            Host = courante.Host,
            Port = courante.Port,
            Path = courante.AbsolutePath.TrimEnd('/') + reste
        };
        return b.Uri;
    }
}

/// <summary>Réécrit l'adresse de chaque requête vers l'adresse courante du poste (voir AdresseApiCourante).</summary>
public sealed class RelaisAdresse(AdresseApiCourante adresse, HttpMessageHandler interne) : DelegatingHandler(interne)
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage requete, CancellationToken ct)
    {
        if (requete.RequestUri is { IsAbsoluteUri: true } uri)
            requete.RequestUri = AdresseApiCourante.Reecrire(uri, adresse.Initiale, adresse.Actuelle);
        return base.SendAsync(requete, ct);
    }
}
