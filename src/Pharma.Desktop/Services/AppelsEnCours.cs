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

/// <summary>Compte chaque requête envoyée par le client HTTP du poste (début et fin, même en cas d'erreur).</summary>
public sealed class SuiviAppels(AppelsEnCours appels) : DelegatingHandler(new HttpClientHandler())
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage requete, CancellationToken ct)
    {
        appels.Debut();
        try { return await base.SendAsync(requete, ct); }
        finally { appels.Fin(); }
    }
}
