using System.Collections.Concurrent;

namespace Pharma.Application.SecondServeur;

// ---- Vigie du 2e serveur (V2-3) : postes connus, mémoire des rapports ---------------------------------------------------------

/// <summary>Un poste et l'instant (heure de la base) où la copie le dit vu : voir <see cref="IPostesConnusDepot"/>.</summary>
public sealed record PosteVuEnBase(string Poste, DateTime VuLeUtc);

/// <summary>
/// Ce que la copie répliquée contient. DerniereDonneeRecueUtc = instant de la dernière transaction rejouée par la copie
/// (pg_last_xact_replay_timestamp) ; null = inconnu (rien rejoué depuis le démarrage de la copie).
/// </summary>
public sealed record PostesConnusLus(DateTime? DerniereDonneeRecueUtc, IReadOnlyList<PosteVuEnBase> Postes);

/// <summary>Lecture, dans la copie répliquée (jamais dans un registre en mémoire), des postes qui ont une connexion d'employé.</summary>
// SOURCE DÉFINITIVE À FOURNIR : (1) liste des postes avec leur instant « vu » écrit par le serveur 1 (donc répliqué) ; (2) DerniereDonneeRecueUtc = pg_last_xact_replay_timestamp (jamais now()).
public interface IPostesConnusDepot
{
    Task<PostesConnusLus> LireAsync(CancellationToken ct = default);
}

public static class PostesConnus
{
    /// <summary>
    /// Postes connus = vus dans les <paramref name="fenetre"/> qui précèdent <paramref name="referenceUtc"/>.
    /// PIÈGE ÉVITÉ : la référence est l'instant de la DERNIÈRE DONNÉE REÇUE par la copie, JAMAIS l'heure courante. Après une coupure la
    /// copie est figée ; avec l'heure courante la liste fondrait à zéro et un seul vote suffirait. Référence inconnue : null (aucune décision).
    /// </summary>
    public static IReadOnlySet<string>? Calculer(IEnumerable<PosteVuEnBase> postes, DateTime? referenceUtc, TimeSpan fenetre)
    {
        if (referenceUtc is not { } reference) return null;
        return postes.Where(p => p.VuLeUtc <= reference && reference - p.VuLeUtc < fenetre)
            .Select(p => p.Poste).ToHashSet(StringComparer.OrdinalIgnoreCase);
    }
}

/// <summary>Corps de POST /api/vigie/rapport côté Application (le DTO de fil est Pharma.Shared.RapportPosteDto).</summary>
public sealed record RapportPoste(string Poste, bool Serveur1Joint, DateTime? EchecDepuisUtc, string VersionPoste, string AdresseServeur1);

public enum IssueRapport { Accepte, TropRapide, PosteDeuxAdresses }

/// <summary>
/// Mémoire (veille : aucune écriture en base) des derniers rapports. Nom de poste annoncé par deux adresses différentes à moins de
/// <c>PosteVuSecondes</c> d'intervalle : le second est ignoré (journalisé par l'appelant). Plus de 1 rapport par seconde par adresse : refusé (429).
/// </summary>
public sealed class RegistreRapportsPostes(ReglagesBascule reglages)
{
    private sealed record Entree(string Adresse, bool Serveur1Joint, DateTime RecuLeUtc, DateTime? PerteDepuisUtc, bool Authentifie, string Version, string AdresseServeur1);

    private static readonly TimeSpan IntervalleMinimum = TimeSpan.FromSeconds(1);
    private readonly ConcurrentDictionary<string, Entree> postes = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, DateTime> parAdresse = new();
    private readonly object verrou = new();

    public IssueRapport Enregistrer(RapportPoste rapport, string adresse, bool authentifie, DateTime maintenantUtc)
    {
        lock (verrou)
        {
            if (parAdresse.TryGetValue(adresse, out var dernier) && maintenantUtc - dernier < IntervalleMinimum) return IssueRapport.TropRapide;
            parAdresse[adresse] = maintenantUtc;
            var vu = TimeSpan.FromSeconds(reglages.PosteVuSecondes);
            postes.TryGetValue(rapport.Poste, out var avant);
            if (avant is not null && avant.Adresse != adresse && maintenantUtc - avant.RecuLeUtc < vu) return IssueRapport.PosteDeuxAdresses;
            var perte = rapport.Serveur1Joint ? (DateTime?)null : avant is { Serveur1Joint: false, PerteDepuisUtc: { } p } ? p : maintenantUtc;
            postes[rapport.Poste] = new Entree(adresse, rapport.Serveur1Joint, maintenantUtc, perte, authentifie, rapport.VersionPoste, rapport.AdresseServeur1);
            Purger(maintenantUtc);
            return IssueRapport.Accepte;
        }
    }

    /// <summary>Derniers rapports (tous, y compris anciens : le filtre de fraîcheur est celui de la décision).</summary>
    public IReadOnlyList<RapportRetenu> Rapports() =>
        postes.Select(kv => new RapportRetenu(kv.Key, kv.Value.Serveur1Joint, kv.Value.RecuLeUtc, kv.Value.PerteDepuisUtc, kv.Value.Authentifie)).ToList();

    public RapportRetenu? Dernier(string poste) =>
        postes.TryGetValue(poste, out var e) ? new RapportRetenu(poste, e.Serveur1Joint, e.RecuLeUtc, e.PerteDepuisUtc, e.Authentifie) : null;

    // Oublie ce qui est vieux de plus de 10 minutes (un poste éteint, une adresse disparue) : mémoire bornée
    private void Purger(DateTime maintenantUtc)
    {
        var limite = maintenantUtc - TimeSpan.FromMinutes(10);
        foreach (var kv in postes.Where(kv => kv.Value.RecuLeUtc < limite).ToList()) postes.TryRemove(kv.Key, out _);
        foreach (var kv in parAdresse.Where(kv => kv.Value < limite).ToList()) parAdresse.TryRemove(kv.Key, out _);
    }
}
