using System.Net;
using Pharma.Application.Commun;

namespace Pharma.Application.SecondServeur;

// ---- Vague 2, lot V2-5 : relais du 2e serveur vers le serveur 1 (cas 3). plan-vague-2.md § 4, contrat-api.md § 2.6 ----------------

/// <summary>En-têtes ajoutés par le 2e serveur à une requête relayée (jamais acceptés tels quels d'un poste : retirés avant le relais).</summary>
public static class EnTetesRelais
{
    public const string Prefixe = "X-Pharma-Relais";
    public const string Serveur = "X-Pharma-Relais-Serveur";
    public const string Poste = "X-Pharma-Relais-Poste";
    public const string Origine = "X-Pharma-Relais-Origine";
    public const string Horodatage = "X-Pharma-Relais-Horodatage";
    public const string Nonce = "X-Pharma-Relais-Nonce";
    public const string Signature = "X-Pharma-Relais-Signature";

    public static bool EstEnTeteRelais(string nom) => nom.StartsWith(Prefixe, StringComparison.OrdinalIgnoreCase);
}

/// <summary>En-têtes de relais reçus, tels quels (null = absent) : valeurs non authentifiées, à ne jamais journaliser.</summary>
public sealed record EnTetesRelaisRecus(string? Serveur, string? Poste, string? Origine, string? Horodatage, string? Nonce, string? Signature);

public sealed record EnTetesRelaisSignes(string Serveur, string Poste, string Origine, string Horodatage, string Nonce, string Signature);

/// <summary>Pourquoi le relais est refusé (la réponse au poste est toujours le même 503 « serveur_en_veille » ; la cause va au journal).</summary>
public enum RefusRelais
{
    PosteInconnu,
    VerdictNonRelais,
    PosteSansRapport,
    PosteJoint,
    RapportPerime,
    Serveur1Injoignable
}

/// <summary>Décision de relayer ou non un poste : null = relais permis.</summary>
public interface ISituationRelais
{
    RefusRelais? Evaluer(string poste);
    /// <summary>Alerte « reseau_poste » permise : poste connu (copie répliquée) dont le dernier rapport est authentifié. Un poste inconnu ou anonyme ne déclenche jamais d'alerte.</summary>
    bool AlerteAutorisee(string poste);
    /// <summary>Note qu'une requête de ce poste vient d'être relayée (tolérance de retour à la voie directe, Q24).</summary>
    void Relayee(string poste);
}

/// <summary>
/// Relais permis seulement si : le verdict de la vigie est « Relais », ET le DERNIER rapport du poste (moins de <c>PosteVuSecondes</c>, 45 s)
/// dit « serveur 1 perdu », ET le 2e serveur joint encore l'API du serveur 1 (dernière sonde).
/// Tolérance Q24 : un poste dont une requête a été relayée il y a moins de <c>RetourVoieDirecteSecondes</c> (120 s) reste relayé tant que le 2e serveur joint l'API du
/// serveur 1, même si son dernier rapport dit « joint » ou si le verdict n'est plus Relais (le poste revient à la voie directe de lui-même après 2 minutes). Mémoire vive seulement.
/// </summary>
public sealed class SituationRelais(VigieSecondServeur vigie, RegistreRapportsPostes registre, SondeServeur1 sonde, IHorloge horloge, ReglagesBascule reglages) : ISituationRelais
{
    public const int PostesMax = 200;
    private readonly object verrou = new();
    private readonly Dictionary<string, DateTime> dernieresRelayees = new(StringComparer.OrdinalIgnoreCase);

    public RefusRelais? Evaluer(string poste)
    {
        var maintenant = horloge.MaintenantUtc;
        DateTime? dernier;
        lock (verrou) dernier = dernieresRelayees.TryGetValue(poste, out var d) ? d : null;
        return Decider(vigie.Dernier?.Verdict, registre.Dernier(poste), sonde.Derniere, maintenant, reglages, dernier);
    }

    public bool AlerteAutorisee(string poste) =>
        registre.Dernier(poste) is { Authentifie: true } && vigie.Connus is { } connus && connus.Contains(poste);

    public void Relayee(string poste)
    {
        var maintenant = horloge.MaintenantUtc;
        var fenetre = TimeSpan.FromSeconds(reglages.RetourVoieDirecteSecondes);
        lock (verrou)
        {
            if (!dernieresRelayees.ContainsKey(poste) && dernieresRelayees.Count >= PostesMax)
                foreach (var kv in dernieresRelayees.Where(kv => maintenant - kv.Value >= fenetre).ToList()) dernieresRelayees.Remove(kv.Key);
            if (dernieresRelayees.ContainsKey(poste) || dernieresRelayees.Count < PostesMax) dernieresRelayees[poste] = maintenant;
        }
    }

    public static RefusRelais? Decider(VerdictPromotion? verdict, RapportRetenu? rapport, Serveur1Vu? serveur1, DateTime maintenantUtc, ReglagesBascule reglages,
        DateTime? dernierRelaiUtc = null)
    {
        var refus = Strict(verdict, rapport, serveur1, maintenantUtc, reglages);
        if (refus is null or RefusRelais.Serveur1Injoignable) return refus;
        // Tolérance Q24 : déjà relayé depuis moins de RetourVoieDirecteSecondes, serveur 1 toujours joint par le 2e serveur
        return dernierRelaiUtc is { } d && maintenantUtc - d < TimeSpan.FromSeconds(reglages.RetourVoieDirecteSecondes) && serveur1 is { ApiJointe: true } ? null : refus;
    }

    private static RefusRelais? Strict(VerdictPromotion? verdict, RapportRetenu? rapport, Serveur1Vu? serveur1, DateTime maintenantUtc, ReglagesBascule reglages)
    {
        if (verdict != VerdictPromotion.Relais) return RefusRelais.VerdictNonRelais;
        if (rapport is null) return RefusRelais.PosteSansRapport;
        if (rapport.Serveur1Joint) return RefusRelais.PosteJoint;
        if (maintenantUtc - rapport.RecuLeUtc >= TimeSpan.FromSeconds(reglages.PosteVuSecondes)) return RefusRelais.RapportPerime;
        if (serveur1 is not { ApiJointe: true }) return RefusRelais.Serveur1Injoignable;
        return null;
    }
}

/// <summary>
/// Alerte « reseau_poste » vers le serveur 1 quand un poste commence à passer par le 2e serveur : au plus une fois par heure et par poste (le serveur 1 applique
/// aussi sa propre limite). Jamais bloquante pour le relais. Un envoi raté (serveur 1 muet, refus) n'est retenté qu'après une minute. Mémoire bornée.
/// </summary>
public sealed class AlerteRelaisPoste(IAlertesVersServeur1 alertes, IHorloge horloge, ITraceErreurs? trace = null)
{
    public static readonly TimeSpan Intervalle = TimeSpan.FromHours(1);
    public static readonly TimeSpan IntervalleReessai = TimeSpan.FromMinutes(1);
    public const int PostesMax = 200;
    /// <summary>Plafond global (tous postes confondus) d'alertes « reseau_poste » par heure : une rafale de faux postes ne peut pas inonder le titulaire.</summary>
    public const int PlafondParHeure = 5;
    public const int MessageMax = RecevoirAlerteInterServeurs.MessageMax;

    private sealed class Etat { public DateTime? PartieLe; public DateTime EssaiLe; }

    private readonly object verrou = new();
    private readonly Dictionary<string, Etat> postes = new(StringComparer.OrdinalIgnoreCase);
    private readonly Queue<DateTime> envois = new();

    public static string Message(string poste) => $"Problème réseau entre le poste {poste} et le serveur 1 : ce poste passe par le 2e serveur";

    /// <summary>Null = rien à faire (déjà signalé dans l'heure, essai récent, ou trop de postes) ; sinon la tâche d'envoi (déjà lancée).</summary>
    public Task? Signaler(string poste)
    {
        var maintenant = horloge.MaintenantUtc;
        lock (verrou)
        {
            while (envois.Count > 0 && maintenant - envois.Peek() >= Intervalle) envois.Dequeue();
            if (envois.Count >= PlafondParHeure) return null;
            if (postes.TryGetValue(poste, out var e))
            {
                if (e.PartieLe is { } partie && maintenant - partie < Intervalle) return null;
                if (maintenant - e.EssaiLe < IntervalleReessai) return null;
            }
            else
            {
                if (postes.Count >= PostesMax) Purger(maintenant);
                if (postes.Count >= PostesMax) return null;
                postes[poste] = e = new Etat();
            }
            e.EssaiLe = maintenant;
            envois.Enqueue(maintenant);
        }
        return Task.Run(() => EnvoyerAsync(poste, maintenant));
    }

    private async Task EnvoyerAsync(string poste, DateTime maintenant)
    {
        try
        {
            using var delai = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var message = Message(poste);
            if (message.Length > MessageMax) message = message[..MessageMax];
            if (await alertes.EnvoyerAsync(TypesAlerteInterServeurs.ReseauPoste, poste, message, maintenant, delai.Token))
                lock (verrou) { if (postes.TryGetValue(poste, out var e)) e.PartieLe = maintenant; }
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            trace?.Erreur("Relais : alerte « reseau_poste » non envoyée au serveur 1", e);
        }
        catch (OperationCanceledException) { /* délai dépassé : nouvel essai après une minute */ }
    }

    private void Purger(DateTime maintenant)
    {
        foreach (var kv in postes.Where(kv => maintenant - kv.Value.EssaiLe > Intervalle).ToList()) postes.Remove(kv.Key);
    }
}

/// <summary>Adresse d'origine annoncée par un relais : jamais une adresse qui donnerait un privilège local.</summary>
public static class OrigineRelais
{
    /// <summary>Adresse utilisable comme adresse d'origine d'un poste ; null sinon (illisible, boucle locale, non spécifiée, diffusion).</summary>
    public static IPAddress? Lire(string? texte)
    {
        if (string.IsNullOrWhiteSpace(texte) || texte.Length > SignatureInterServeurs.LongueurOrigineMax || !IPAddress.TryParse(texte, out var a)) return null;
        if (a.IsIPv4MappedToIPv6) a = a.MapToIPv4();
        if (IPAddress.IsLoopback(a) || a.Equals(IPAddress.Any) || a.Equals(IPAddress.IPv6Any) || a.Equals(IPAddress.None) || a.Equals(IPAddress.IPv6None)) return null;
        var o = a.GetAddressBytes();
        if (o.Length == 4 && o[0] >= 224) return null;      // multidiffusion et réservées
        if (o.Length == 16 && o[0] == 0xFF) return null;    // multidiffusion IPv6
        return a;
    }

    /// <summary>Texte annoncé pour une adresse (IPv4 mappée ramenée en IPv4) ; null si aucune adresse.</summary>
    public static string? Texte(IPAddress? a) => a is null ? null : (a.IsIPv4MappedToIPv6 ? a.MapToIPv4() : a).ToString();
}

/// <summary>
/// Limite de débit du relais, par poste ET par adresse d'origine (fenêtre fixe d'une minute) : borne le travail du 2e serveur et le nombre d'annonces signées
/// (donc de nonces) envoyées au serveur 1. En mémoire, bornée.
/// </summary>
public sealed class LimiteRelais(IHorloge horloge, int parMinute = LimiteRelais.ParMinuteParDefaut)
{
    public const int ParMinuteParDefaut = 600;   // 10 requêtes par seconde et par poste : bien au-delà d'un usage normal de caisse
    private const int ClesMax = 2000;
    private readonly object verrou = new();
    private readonly Dictionary<string, (DateTime Debut, int N)> fenetres = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Nombre de fenêtres en mémoire (tests de la borne).</summary>
    public int Taille { get { lock (verrou) return fenetres.Count; } }

    /// <summary>Vrai si la requête est permise (et comptée) pour ce poste et cette adresse ; faux dès que l'un des deux dépasse sa limite.</summary>
    public bool Autoriser(string poste, string origine)
    {
        var maintenant = horloge.MaintenantUtc;
        lock (verrou)
        {
            if (fenetres.Count >= ClesMax)
            {
                foreach (var kv in fenetres.Where(kv => maintenant - kv.Value.Debut >= TimeSpan.FromMinutes(1)).ToList()) fenetres.Remove(kv.Key);
                // Toujours plein (flot de noms ou d'adresses) : les plus anciennes fenêtres sont évincées, la mémoire reste bornée
                if (fenetres.Count >= ClesMax)
                    foreach (var kv in fenetres.OrderBy(kv => kv.Value.Debut).Take(ClesMax / 10).ToList()) fenetres.Remove(kv.Key);
            }
            var a = Compter("p|" + poste, maintenant);
            var b = Compter("a|" + origine, maintenant);
            return a && b;
        }
    }

    private bool Compter(string cle, DateTime maintenant)
    {
        if (!fenetres.TryGetValue(cle, out var f) || maintenant - f.Debut >= TimeSpan.FromMinutes(1)) f = (maintenant, 0);
        if (f.N >= parMinute) { fenetres[cle] = f; return false; }
        fenetres[cle] = (f.Debut, f.N + 1);
        return true;
    }
}
