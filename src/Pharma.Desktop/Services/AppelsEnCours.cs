using System.Net.Http;

namespace Pharma.Desktop.Services;

/// <summary>
/// Appels au serveur en cours, tous écrans confondus : la barre de chargement du haut de la fenêtre (BarreChargement) s'affiche
/// tant qu'il y en a au moins un.
/// </summary>
public sealed class AppelsEnCours
{
    private int nombre;

    public bool Actif => Volatile.Read(ref nombre) > 0;

    /// <summary>Passage de « aucun appel » à « au moins un appel », et retour.</summary>
    public event Action? Change;

    internal void Debut()
    {
        if (Interlocked.Increment(ref nombre) == 1) Change?.Invoke();
    }

    internal void Fin()
    {
        if (Interlocked.Decrement(ref nombre) == 0) Change?.Invoke();
    }
}

/// <summary>
/// Compte chaque requête envoyée par le client HTTP du poste (début et fin, même en cas d'erreur), et l'abandonne au-delà de son
/// délai (DelaisAppels) : l'écran affiche alors « Le serveur met trop de temps à répondre… » (ApiClient) au lieu d'attendre sans fin.
/// Le client HTTP n'a pas de délai propre (Timeout infini) : c'est celui-ci qui compte.
/// </summary>
public sealed class SuiviAppels(AppelsEnCours appels) : DelegatingHandler(new HttpClientHandler())
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage requete, CancellationToken ct)
    {
        appels.Debut();
        using var delai = CancellationTokenSource.CreateLinkedTokenSource(ct);
        delai.CancelAfter(DelaisAppels.Pour(requete.RequestUri?.AbsolutePath ?? ""));
        try { return await base.SendAsync(requete, delai.Token); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TaskCanceledException("Délai d'attente du serveur dépassé.");
        }
        finally { appels.Fin(); }
    }
}

/// <summary>
/// Délai d'abandon d'un appel au serveur (docs/navigation.md) : 30 secondes en général ; 2 minutes pour produire un document
/// (PDF, export, étiquettes, rapport) ; 10 minutes pour les opérations lourdes (sauvegarde, restauration, reprise de données,
/// import de la nomenclature ou de scans, envoi de documents à la GED).
/// </summary>
public static class DelaisAppels
{
    public static readonly TimeSpan Normal = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan Document = TimeSpan.FromMinutes(2);
    public static readonly TimeSpan Lourd = TimeSpan.FromMinutes(10);

    private static readonly string[] Lourds =
        ["/api/admin/sauvegardes/lancer", "/api/admin/reprise/", "/api/super-admin/restauration", "/api/admin/nomenclature", "/api/ged/",
         "/api/mobile/reception/scans/importer"];

    private static readonly string[] Documents = ["/pdf", "/export", "/etiquettes", "/rapport"];

    public static TimeSpan Pour(string chemin) =>
        Lourds.Any(l => chemin.StartsWith(l, StringComparison.OrdinalIgnoreCase)) ? Lourd
        : Documents.Any(d => chemin.Contains(d, StringComparison.OrdinalIgnoreCase)) ? Document
        : Normal;
}
