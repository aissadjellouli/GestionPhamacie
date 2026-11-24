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

public enum IssueRapport { Accepte, TropRapide, PosteDeuxAdresses, AuthentifieRecent, RegistrePlein }

/// <summary>
/// Mémoire (veille : aucune écriture en base) des derniers rapports. Nom de poste annoncé par deux adresses différentes à moins de
/// <c>PosteVuSecondes</c> d'intervalle : le second est ignoré (journalisé par l'appelant). Plus de 1 rapport par seconde par adresse : refusé (429).
/// </summary>
public sealed class RegistreRapportsPostes(ReglagesBascule reglages)
{
    private sealed record Entree(string Adresse, bool Serveur1Joint, DateTime RecuLeUtc, DateTime? PerteDepuisUtc, bool Authentifie, string Version, string AdresseServeur1);

    private static readonly TimeSpan IntervalleMinimum = TimeSpan.FromSeconds(1);
    /// <summary>Bornes de mémoire : postes distincts et adresses distinctes (un appelant du réseau peut inventer des noms et des adresses).</summary>
    public const int PostesMax = 500, AdressesMax = 2000;
    private readonly ConcurrentDictionary<string, Entree> postes = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, DateTime> parAdresse = new();
    private readonly object verrou = new();

    public IssueRapport Enregistrer(RapportPoste rapport, string adresse, bool authentifie, DateTime maintenantUtc)
    {
        lock (verrou)
        {
            if (parAdresse.TryGetValue(adresse, out var dernier) && maintenantUtc - dernier < IntervalleMinimum) return IssueRapport.TropRapide;
            if (!parAdresse.ContainsKey(adresse) && parAdresse.Count >= AdressesMax) EvincerAdresses();
            parAdresse[adresse] = maintenantUtc;
            var vu = TimeSpan.FromSeconds(reglages.PosteVuSecondes);
            postes.TryGetValue(rapport.Poste, out var avant);
            if (avant is not null && maintenantUtc - avant.RecuLeUtc < vu)
            {
                // Un rapport authentifié (jeton valide, poste lié) remplace un rapport anonyme du même nom venu d'une autre adresse ; l'inverse n'arrive jamais :
                // un rapport anonyme ne remplace pas un rapport authentifié récent (aucun tiers ne peut prendre la place d'un vrai poste)
                if (avant.Adresse != adresse && !(authentifie && !avant.Authentifie)) return IssueRapport.PosteDeuxAdresses;
                if (avant.Authentifie && !authentifie) return IssueRapport.AuthentifieRecent;
            }
            var perte = rapport.Serveur1Joint ? (DateTime?)null : avant is { Serveur1Joint: false, PerteDepuisUtc: { } p } ? p : maintenantUtc;
            if (!postes.ContainsKey(rapport.Poste) && postes.Count >= PostesMax && !FairePlace(authentifie)) return IssueRapport.RegistrePlein;
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

    // Registre plein : on évince d'abord les rapports anonymes les plus anciens (un flot de faux noms ne chasse jamais un poste authentifié) ; un rapport anonyme
    // est refusé s'il n'y a que des rapports authentifiés
    private bool FairePlace(bool authentifie)
    {
        var ancien = postes.Where(kv => !kv.Value.Authentifie).OrderBy(kv => kv.Value.RecuLeUtc).Select(kv => kv.Key).FirstOrDefault();
        if (ancien is null && !authentifie) return false;
        ancien ??= postes.OrderBy(kv => kv.Value.RecuLeUtc).Select(kv => kv.Key).First();
        postes.TryRemove(ancien, out _);
        return true;
    }

    private void EvincerAdresses()
    {
        foreach (var kv in parAdresse.OrderBy(kv => kv.Value).Take(Math.Max(1, AdressesMax / 10)).ToList()) parAdresse.TryRemove(kv.Key, out _);
    }

    // Oublie ce qui est vieux de plus de 10 minutes (un poste éteint, une adresse disparue) : mémoire bornée
    private void Purger(DateTime maintenantUtc)
    {
        var limite = maintenantUtc - TimeSpan.FromMinutes(10);
        foreach (var kv in postes.Where(kv => kv.Value.RecuLeUtc < limite).ToList()) postes.TryRemove(kv.Key, out _);
        foreach (var kv in parAdresse.Where(kv => kv.Value < limite).ToList()) parAdresse.TryRemove(kv.Key, out _);
    }
}
