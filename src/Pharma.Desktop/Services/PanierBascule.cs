namespace Pharma.Desktop.Services;

/// <summary>
/// Panier du poste mis de côté quand le nouveau serveur refuse le jeton (repli, Q26) : MÉMOIRE SEULEMENT, jamais de copie sur disque,
/// effacé après reprise ou à la fermeture du logiciel. Rendu à l'employé qui se reconnecte sur ce poste (même employé ou non).
/// </summary>
public sealed class PanierBascule
{
    private object? contenu;
    private readonly List<Func<Task>> demandeurs = [];
    private readonly object verrou = new();

    public bool EnAttente => contenu is not null;

    /// <summary>La caisse s'abonne : elle met son panier de côté (Garder) quand le poste passe en repli.</summary>
    public void Abonner(Func<Task> demande) { lock (verrou) demandeurs.Add(demande); }
    public void Desabonner(Func<Task> demande) { lock (verrou) demandeurs.Remove(demande); }

    /// <summary>Demande aux écrans de mettre leur panier de côté ; rendue quand tous ont fini (la session peut alors être fermée).</summary>
    public async Task DemanderAsync()
    {
        Func<Task>[] liste;
        lock (verrou) liste = [.. demandeurs];
        foreach (var d in liste)
        {
            try { await d(); }
            catch (Exception e) when (e is InvalidOperationException or ObjectDisposedException or TaskCanceledException) { }
        }
    }

    public void Garder(object panier) => contenu = panier;

    /// <summary>Rend le panier gardé (une seule fois) ; null s'il n'y en a pas ou s'il n'est pas du type attendu.</summary>
    public T? Reprendre<T>() where T : class
    {
        if (contenu is not T t) return null;
        contenu = null;
        return t;
    }

    public void Effacer() => contenu = null;
}
