using System.Collections.Concurrent;

namespace Pharma.Application.Securite;

/// <summary>
/// Essais manqués de connexion par mot de passe, comptés par identifiant sur le serveur (en mémoire : un seul serveur par
/// pharmacie ; un redémarrage de l'API remet les compteurs à zéro). Même règle que le code comptoir : après
/// <see cref="EssaisMax"/> mots de passe faux, l'identifiant est bloqué quelques minutes.
/// </summary>
public sealed class EssaisConnexion
{
    public const int EssaisMax = 5;
    public static readonly TimeSpan Blocage = TimeSpan.FromMinutes(5);
    /// <summary>Un compteur sans nouvel essai pendant ce délai repart de zéro.</summary>
    public static readonly TimeSpan Oubli = TimeSpan.FromMinutes(15);
    private const int TailleMax = 10_000;

    private sealed record Etat(int Essais, DateTime DernierEssai, DateTime? BloqueJusqu);

    private readonly ConcurrentDictionary<string, Etat> _etats = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Fin du blocage si l'identifiant est bloqué à cet instant, sinon null.</summary>
    /// <remarks>
    /// Mémoire pleine d'identifiants encore bloqués (revue 2026-11-15, M6) : un identifiant non suivi est refusé quelques minutes
    /// plutôt que la mémoire grossir sans limite (les blocages expirés sont d'abord supprimés).
    /// </remarks>
    public DateTime? BloqueJusqu(string identifiant, DateTime maintenantUtc)
    {
        if (_etats.TryGetValue(identifiant, out var e)) return e.BloqueJusqu is { } b && b > maintenantUtc ? b : null;
        if (_etats.Count >= TailleMax) Purger(maintenantUtc);
        return _etats.Count >= TailleMax ? maintenantUtc + Blocage : null;
    }

    /// <summary>Compte un mot de passe faux ; vrai si cet essai déclenche le blocage.</summary>
    public bool NoterEchec(string identifiant, DateTime maintenantUtc)
    {
        if (_etats.Count >= TailleMax) Purger(maintenantUtc, identifiant);
        // Toujours pleine (que des blocages en cours) : identifiant non suivi refusé, rien n'est ajouté
        if (_etats.Count >= TailleMax && !_etats.ContainsKey(identifiant)) return true;
        var etat = _etats.AddOrUpdate(identifiant,
            _ => new Etat(1, maintenantUtc, null),
            (_, e) =>
            {
                // Blocage terminé ou compteur ancien : on repart de un
                var essais = e.BloqueJusqu is not null || maintenantUtc - e.DernierEssai > Oubli ? 1 : e.Essais + 1;
                return new Etat(essais, maintenantUtc, essais >= EssaisMax ? maintenantUtc + Blocage : null);
            });
        return etat.BloqueJusqu is not null;
    }

    public void NoterSucces(string identifiant) => _etats.TryRemove(identifiant, out _);

    /// <summary>Nombre d'identifiants suivis (tests).</summary>
    public int Nombre => _etats.Count;

    private void Purger(DateTime maintenantUtc, string? garder = null)
    {
        foreach (var (cle, e) in _etats)
            if ((e.BloqueJusqu ?? e.DernierEssai + Oubli) <= maintenantUtc)
                _etats.TryRemove(cle, out _);
        // Toujours plein (rafale d'identifiants inventés) : on oublie les compteurs non bloqués, les plus anciens d'abord, jusqu'à
        // la moitié de la taille. Une entrée encore bloquée n'est JAMAIS effacée (sinon la rafale lèverait le blocage) ;
        // la rafale elle-même est freinée par le limiteur de débit par adresse IP (Program.cs).
        if (_etats.Count < TailleMax) return;
        foreach (var (cle, _) in _etats.Where(p => (p.Value.BloqueJusqu is not { } b || b <= maintenantUtc) && !string.Equals(p.Key, garder, StringComparison.OrdinalIgnoreCase))
                     .OrderBy(p => p.Value.DernierEssai).ToList())
        {
            if (_etats.Count < TailleMax / 2) break;
            _etats.TryRemove(cle, out _);
        }
    }
}
