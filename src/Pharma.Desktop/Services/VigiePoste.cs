using System.IO;
using System.Net.Http;
using Pharma.Shared;

namespace Pharma.Desktop.Services;

/// <summary>Situation du poste vis-à-vis de la bascule sur le 2e serveur (lot V2-6).</summary>
public enum SituationBascule
{
    /// <summary>Rien à signaler (y compris : officine à un seul serveur).</summary>
    Normale,
    /// <summary>Vérification avec le 2e serveur en cours (consigne « attendre », ou serveur écarté en attente de la promotion du 2e).</summary>
    Attente,
    /// <summary>Les appels passent par le 2e serveur (consigne « relais »).</summary>
    Relais,
    /// <summary>Les deux serveurs sont bloqués (« timelines_egales ») : aucune bascule.</summary>
    Indecis,
    /// <summary>Poste coupé du réseau de la pharmacie (motif « isole ») : ventes suspendues, aucune bascule.</summary>
    Isole,
    /// <summary>Poste passé sur le 2e serveur, session conservée (message affiché 2 minutes).</summary>
    Basculee,
    /// <summary>Poste passé sur le 2e serveur, jeton refusé : reconnexion demandée, panier gardé en mémoire.</summary>
    Repli
}

/// <summary>Réponse de GET /api/healthcheck d'un serveur : joint (200), écarté, rôle annoncé (null : serveur plus ancien).</summary>
public sealed record EtatHealthcheck(bool Joint, bool Ecarte, string? Role);

/// <summary>Accès réseau de la vigie (doublé dans les tests ; jamais le client HTTP de l'application, qui passe par le relais).</summary>
public interface IReseauVigie
{
    /// <summary>Healthcheck de l'adresse : Joint = false si injoignable ou réponse autre que 200.</summary>
    Task<EtatHealthcheck> SonderAsync(Uri adresse, CancellationToken ct);

    /// <summary>POST /api/vigie/rapport au 2e serveur ; null si le 2e serveur ne répond pas (404 : pas de vigie, 429, réseau).</summary>
    Task<ConsigneVigieDto?> RapporterAsync(Uri secours, RapportPosteDto rapport, string? jeton, CancellationToken ct);

    /// <summary>Le jeton de l'employé est-il accepté par ce serveur ? false : refusé (401) ; null : indéterminé.</summary>
    Task<bool?> JetonAccepteAsync(Uri adresse, string jeton, CancellationToken ct);
}

public interface ISessionVigie
{
    bool EstConnecte { get; }
    string? Jeton { get; }
    /// <summary>Ferme la session avec un message pour l'écran de connexion (repli).</summary>
    void FermerPourRepli(string motif);
}

public interface IReglagesVigie
{
    ReglagesPoste.Valeurs Actuelles { get; }
    string? Enregistrer(ReglagesPoste.Valeurs valeurs);
}

/// <summary>Ce dont ApiClient a besoin pour renvoyer un encaissement interrompu par une bascule (Q25).</summary>
public interface IBasculePoste
{
    /// <summary>Augmente à chaque bascule effectuée.</summary>
    int Generation { get; }
    /// <summary>Une adresse de secours est réglée (sinon : comportement d'un seul serveur, rien à faire).</summary>
    bool Active { get; }
    /// <summary>Après un échec réseau : réexamen immédiat (rapport au 2e serveur, bascule éventuelle).</summary>
    Task ApresEchecAsync();
}

/// <summary>Reçoit les réponses 503 « serveur_ecarte » vues par le client HTTP du poste.</summary>
public interface IEcouteEcart
{
    Task EcartRecuAsync(string? motif);
}

/// <summary>
/// Vigie du poste (lot V2-6) : toutes les 15 s (5 s après un échec), sonde le serveur principal, rapporte au 2e serveur
/// (POST /api/vigie/rapport) et applique sa consigne (rester | attendre | relais | basculer) ; applique aussi les réponses 503
/// « serveur_ecarte » (bascule, indécis, isolé). Sans adresse de secours (un seul serveur) : aucun rapport, aucun relais, rien ne change.
/// Jamais de retour automatique vers l'ancien serveur ; le relais, lui, revient à la voie directe après 2 minutes de serveur principal joint.
/// </summary>
public sealed class VigiePoste : IBasculePoste, IEcouteEcart, IDisposable
{
    private static readonly TimeSpan AffichageBascule = TimeSpan.FromMinutes(2);

    private readonly IReglagesVigie reglages;
    private readonly ISessionVigie session;
    private readonly AdresseApiCourante adresse;
    private readonly IReseauVigie reseau;
    private readonly PanierBascule panier;
    private readonly string nomPoste, version;
    private readonly string? variableApi;
    private readonly Func<DateTime> horloge;
    private readonly SemaphoreSlim verrou = new(1, 1);
    private readonly CancellationTokenSource arret = new();
    private readonly Action<Exception>? journal;
    private Task? boucle;

    private DateTime? echecDepuis, jointDepuis, basculeeLe;
    private bool ecartBascule;
    private int generation;

    public VigiePoste(IReglagesVigie reglages, ISessionVigie session, AdresseApiCourante adresse, IReseauVigie reseau, PanierBascule panier,
        string nomPoste, string? variableApi, string version, Func<DateTime>? horloge = null, Action<Exception>? journal = null)
    {
        this.journal = journal;
        (this.reglages, this.session, this.adresse, this.reseau, this.panier) = (reglages, session, adresse, reseau, panier);
        (this.nomPoste, this.variableApi, this.version) = (nomPoste, variableApi, version);
        this.horloge = horloge ?? (() => DateTime.UtcNow);
    }

    public SituationBascule Situation { get; private set; } = SituationBascule.Normale;
    public string? Message { get; private set; }

    /// <summary>Le poste a basculé sur le 2e serveur depuis son démarrage (barre d'état « Connecté · 2e serveur »).</summary>
    public bool SurSecours { get; private set; }

    public bool EnRelais => Situation == SituationBascule.Relais;
    public bool VentesSuspendues => Situation == SituationBascule.Isole;
    public int Generation => Volatile.Read(ref generation);
    public bool Active => BasculeSecours.SecoursConfigure(reglages.Actuelles);

    /// <summary>Levé (peut-être hors du fil de l'écran) quand la situation, le message ou l'adresse changent.</summary>
    public event Action? Change;

    /// <summary>Délai avant la prochaine étape : 15 s, 5 s tant que le serveur principal ne répond pas.</summary>
    public TimeSpan Cadence => TimeSpan.FromSeconds(echecDepuis is null ? ReglagesBasculeParDefaut.RapportPosteSecondes : ReglagesBasculeParDefaut.RapportPosteApresEchecSecondes);

    private Uri Serveur1() =>
        AdresseApiCourante.Lire(ReglagesPoste.AdresseApiDemarrage(reglages.Actuelles, variableApi)) ?? adresse.Initiale;

    private void Definir(SituationBascule situation, string? message)
    {
        if (Situation == situation && Message == message) return;
        (Situation, Message) = (situation, message);
        if (situation is SituationBascule.Basculee or SituationBascule.Repli) basculeeLe = horloge();
        Change?.Invoke();
    }

    /// <summary>Démarre la boucle d'arrière-plan (une seule fois).</summary>
    public void Demarrer() => boucle ??= Task.Run(() => BoucleAsync(arret.Token));

    private async Task BoucleAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try { await EtapeAsync(ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
            // Filet d'une boucle d'arrière-plan : une erreur imprévue d'une étape ne doit pas arrêter la vigie du poste
            catch (Exception e) when (e is not OutOfMemoryException) { journal?.Invoke(e); }
            try { await Task.Delay(Cadence, ct); }
            catch (OperationCanceledException) { return; }
        }
    }

    /// <summary>Une étape de la vigie : sonde, rapport, consigne (appelée par la boucle ; publique pour les tests).</summary>
    public async Task EtapeAsync(CancellationToken ct = default)
    {
        if (!Active) return;
        await verrou.WaitAsync(ct);
        try { await EtapeInterneAsync(ct); }
        finally { verrou.Release(); }
    }

    private async Task EtapeInterneAsync(CancellationToken ct)
    {
        var v = reglages.Actuelles;
        if (!BasculeSecours.SecoursConfigure(v)) return;
        var secours = AdresseApiCourante.Lire(v.AdresseApiSecours)!;
        var automatique = BasculeSecours.Automatique(v);
        var serveur1 = Serveur1();
        var maintenant = horloge();

        var sonde = await reseau.SonderAsync(serveur1, ct);
        if (sonde.Joint) { echecDepuis = null; jointDepuis ??= maintenant; }
        else { jointDepuis = null; echecDepuis ??= maintenant; }
        var normal = sonde.Joint && !sonde.Ecarte;

        // Isolé ou indécis : reprise dès que le serveur principal répond normalement (plus de 503) ; jamais de bascule pour ces deux motifs
        if (Situation is SituationBascule.Isole or SituationBascule.Indecis && normal) Definir(SituationBascule.Normale, null);
        if (normal) ecartBascule = false;
        // Message de bascule affiché 2 minutes
        if (Situation is SituationBascule.Basculee or SituationBascule.Repli && basculeeLe is { } le && maintenant - le >= AffichageBascule)
            Definir(SituationBascule.Normale, null);

        // Rapport au 2e serveur, même case décochée (il doit connaître tous les postes)
        var rapport = new RapportPosteDto(nomPoste, sonde.Joint, echecDepuis, version, serveur1.ToString());
        var consigne = await reseau.RapporterAsync(secours, rapport, session.EstConnecte ? session.Jeton : null, ct);

        // Écart « bascule » reçu et 2e serveur pas encore principal : on réessaie à chaque étape
        if (ecartBascule && automatique && Situation is not (SituationBascule.Isole or SituationBascule.Indecis))
        {
            if ((await TenterBasculeAsync(ct)).Ok) return;
            Definir(SituationBascule.Attente, BasculeSecours.MessageAttente);
            return;
        }

        // Relais : retour à la voie directe après 2 minutes de serveur principal joint (Q24)
        var retourDirect = false;
        if (Situation == SituationBascule.Relais && normal && jointDepuis is { } depuis
            && maintenant - depuis >= TimeSpan.FromSeconds(ReglagesBasculeParDefaut.RetourVoieDirecteSecondes))
        {
            adresse.Changer(serveur1);
            Definir(SituationBascule.Normale, null);
            retourDirect = true;
        }

        if (Situation is SituationBascule.Isole or SituationBascule.Indecis) return;
        switch (BasculeSecours.PourConsigne(consigne?.Consigne, automatique))
        {
            case ActionBascule.Basculer:
                if (!(await TenterBasculeAsync(ct)).Ok && Situation != SituationBascule.Repli)
                    Definir(SituationBascule.Attente, BasculeSecours.MessageAttente);
                break;
            case ActionBascule.Relais:
                if (retourDirect || Situation == SituationBascule.Relais) break;
                adresse.Changer(secours);
                Definir(SituationBascule.Relais, BasculeSecours.MessageRelais);
                break;
            case ActionBascule.Attendre:
                if (Situation is SituationBascule.Normale or SituationBascule.Attente) Definir(SituationBascule.Attente, BasculeSecours.MessageAttente);
                break;
            default:
                if (Situation == SituationBascule.Attente && !ecartBascule) Definir(SituationBascule.Normale, null);
                break;
        }
    }

    /// <summary>Bascule seulement si le 2e serveur annonce « principal » (healthcheck) ; jamais vers une copie en veille.</summary>
    private async Task<(bool Ok, string? Refus)> TenterBasculeAsync(CancellationToken ct)
    {
        var v = reglages.Actuelles;
        if (AdresseApiCourante.Lire(v.AdresseApiSecours) is not { } secours) return (false, null);
        var h = await reseau.SonderAsync(secours, ct);
        if (!h.Joint || h.Ecarte || !BasculeSecours.EstPrincipal(h.Role))
            return (false, h.Joint ? BasculeSecours.MessageSecoursPasPrincipal : null);
        await EffectuerBasculeAsync(v, secours, ct);
        return (true, null);
    }

    private async Task EffectuerBasculeAsync(ReglagesPoste.Valeurs v, Uri secours, CancellationToken ct)
    {
        // Adresse courante changée d'un coup ; poste.json : AdresseApi et AdresseApiSecours échangées (Q27)
        var serveur1Avant = ReglagesPoste.AdresseApiDemarrage(v, variableApi);
        adresse.Changer(secours);
        var erreurReglages = reglages.Enregistrer(BasculeSecours.Echanger(v, serveur1Avant));
        Interlocked.Increment(ref generation);
        (SurSecours, echecDepuis, jointDepuis, ecartBascule) = (true, null, null, false);
        var suite = erreurReglages is null ? "" : BasculeSecours.MessageReglagesNonEnregistres;

        // Pas de déconnexion si le nouveau serveur accepte le jeton ; sinon repli : panier gardé en mémoire, écran de connexion
        if (session.EstConnecte && session.Jeton is { } jeton && await reseau.JetonAccepteAsync(secours, jeton, ct) == false)
        {
            await panier.DemanderAsync();
            Definir(SituationBascule.Repli, BasculeSecours.MessageRepli + suite);
            session.FermerPourRepli(BasculeSecours.MessageRepli);
            return;
        }
        Definir(SituationBascule.Basculee, BasculeSecours.MessageBascule + suite);
    }

    /// <summary>
    /// Bouton manuel du bandeau : bascule sans redémarrage, seulement si le 2e serveur annonce « principal ».
    /// Renvoie le message à montrer en cas de refus, sinon null.
    /// </summary>
    public async Task<string?> BasculerManuellementAsync(CancellationToken ct = default)
    {
        if (!Active) return "Aucune adresse de secours n'est réglée sur ce poste.";
        await verrou.WaitAsync(ct);
        try
        {
            var (ok, refus) = await TenterBasculeAsync(ct);
            return ok ? null : refus ?? "Le 2e serveur ne répond pas.";
        }
        finally { verrou.Release(); }
    }

    /// <summary>Réponse 503 « serveur_ecarte » reçue par n'importe quel appel du poste.</summary>
    public async Task EcartRecuAsync(string? motif)
    {
        if (!Active) return;   // un seul serveur : comportement actuel
        await verrou.WaitAsync();
        try
        {
            var v = reglages.Actuelles;
            if (!BasculeSecours.SecoursConfigure(v)) return;
            switch (BasculeSecours.PourEcart(motif, BasculeSecours.Automatique(v)))
            {
                case ActionBascule.Isole:
                    ecartBascule = false;
                    Definir(SituationBascule.Isole, BasculeSecours.MessageIsole);
                    break;
                case ActionBascule.Indecis:
                    ecartBascule = false;
                    Definir(SituationBascule.Indecis, BasculeSecours.MessageIndecis);
                    break;
                case ActionBascule.Basculer:
                    ecartBascule = true;
                    if (!(await TenterBasculeAsync(arret.Token)).Ok && Situation != SituationBascule.Repli)
                        Definir(SituationBascule.Attente, BasculeSecours.MessageAttente);
                    break;
            }
        }
        finally { verrou.Release(); }
    }

    public async Task ApresEchecAsync()
    {
        if (!Active) return;
        try { await EtapeAsync(arret.Token); }
        catch (OperationCanceledException) { }
    }

    public void Dispose()
    {
        arret.Cancel();
        arret.Dispose();
        verrou.Dispose();
    }
}

/// <summary>
/// Lit les réponses 503 « serveur_ecarte » de toutes les requêtes du poste et les confie à la vigie AVANT de rendre la réponse à l'appelant :
/// la bascule est faite quand l'appelant voit l'échec (il peut alors renvoyer, voir ApiClient.EncaisserAsync).
/// </summary>
public sealed class SurveillanceEcart(IEcouteEcart ecoute, HttpMessageHandler interne) : DelegatingHandler(interne)
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage requete, CancellationToken ct)
    {
        var reponse = await base.SendAsync(requete, ct);
        if (reponse.StatusCode != System.Net.HttpStatusCode.ServiceUnavailable) return reponse;
        if (LireMotifEcart(await LireCorpsAsync(reponse, ct)) is not { } ecart) return reponse;
        try { await ecoute.EcartRecuAsync(ecart.Motif); }
        catch (Exception e) when (e is HttpRequestException or InvalidOperationException or ObjectDisposedException) { }
        return reponse;
    }

    private static async Task<string?> LireCorpsAsync(HttpResponseMessage reponse, CancellationToken ct)
    {
        try
        {
            if (reponse.Content is null) return null;
            await reponse.Content.LoadIntoBufferAsync(ct);   // le corps reste lisible par l'appelant
            return await reponse.Content.ReadAsStringAsync(ct);
        }
        catch (Exception e) when (e is HttpRequestException or IOException or ObjectDisposedException) { return null; }
    }

    /// <summary>Corps d'un 503 : (motif) si c'est un « serveur_ecarte » (motif null : absent) ; null pour tout autre 503.</summary>
    public static (string? Motif, bool Ecarte)? LireMotifEcart(string? corps)
    {
        if (string.IsNullOrWhiteSpace(corps)) return null;
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(corps);
            var racine = doc.RootElement;
            if (racine.ValueKind != System.Text.Json.JsonValueKind.Object) return null;
            if (!racine.TryGetProperty("code", out var code) || code.ValueKind != System.Text.Json.JsonValueKind.String
                || code.GetString() != "serveur_ecarte") return null;
            var motif = racine.TryGetProperty("motif", out var m) && m.ValueKind == System.Text.Json.JsonValueKind.String ? m.GetString() : null;
            return (motif, true);
        }
        catch (System.Text.Json.JsonException) { return null; }
    }
}
