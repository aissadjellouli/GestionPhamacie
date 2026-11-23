using Pharma.Application.Commun;

namespace Pharma.Application.SecondServeur;

/// <summary>
/// Lecture de l'état du serveur 1 par appel signé de GET /api/inter-serveurs/etat (implémentation : <c>LecteurEtatServeur1Signe</c> de l'API, sur le client signé du lot V2-2).
/// Retourne null si le serveur 1 ne donne pas de réponse valable (réseau, délai de 3 s, réponse non signée ou fausse, 401/403) : jamais de promotion sur une erreur d'outillage.
/// </summary>
public interface ILecteurEtatServeur1
{
    Task<EtatServeur1?> EtatServeur1Async(CancellationToken ct = default);
}

/// <summary>État du serveur 1 tel que répondu par son API (champs de EtatInterServeursDto).</summary>
public sealed record EtatServeur1(string Serveur, string Role, int? Timeline, bool EnRecovery, string? VersionApi, string? EmpreinteJeton);

/// <summary>Journal fichier des décisions de la vigie (noms de postes seulement, aucune donnée patient).</summary>
public interface IJournalDecisions
{
    Task EcrireAsync(string evenement, object donnees, CancellationToken ct = default);
}

/// <summary>Sondes du 2e serveur vers le serveur 1 : base (relevé existant) ET API (appel signé), 3 s maximum chacune ; mémorise depuis quand chacune est muette.</summary>
public sealed class SondeServeur1(IReleveServeurs releves, ILecteurEtatServeur1 client)
{
    public static readonly TimeSpan Delai = TimeSpan.FromSeconds(3);
    private DateTime? baseMuette, apiMuette;
    private Serveur1Vu? derniere;

    public Serveur1Vu? Derniere => derniere;

    public async Task<Serveur1Vu> SonderAsync(DateTime maintenantUtc, CancellationToken ct = default)
    {
        var tb = Avec(releves.AutreAsync, ct);
        var ta = Avec(client.EtatServeur1Async, ct);
        var baseOk = await tb is not null;
        var apiOk = await ta is not null;
        baseMuette = baseOk ? null : baseMuette ?? maintenantUtc;
        apiMuette = apiOk ? null : apiMuette ?? maintenantUtc;
        return derniere = new Serveur1Vu(baseOk, apiOk, baseMuette, apiMuette);
    }

    // Délai de 3 s ; une erreur de sonde (hors annulation demandée) vaut « muet »
    private static async Task<T?> Avec<T>(Func<CancellationToken, Task<T?>> appel, CancellationToken ct) where T : class
    {
        using var delai = CancellationTokenSource.CreateLinkedTokenSource(ct);
        delai.CancelAfter(Delai);
        try { return await appel(delai.Token); }
        catch (Exception e) when (e is not OperationCanceledException || !ct.IsCancellationRequested) { return null; }
    }
}

public enum StatutRapport { Accepte, TropRapide, Ignore }

public sealed record ConsigneRapport(string Role, string Consigne, string? Message);

public sealed record ReponseRapport(StatutRapport Statut, ConsigneRapport? Consigne);

/// <summary>
/// Vigie du 2e serveur : reçoit les rapports des postes, évalue périodiquement <see cref="DecisionPromotion"/> et renvoie la consigne.
/// NE PROMEUT RIEN : expose <see cref="Dernier"/> et <see cref="Confirmations"/> (nombre d'évaluations consécutives au même verdict,
/// 5 s d'écart : « décision confirmée 2 fois ») que le lot V2-4 lira.
/// </summary>
public sealed class VigieSecondServeur(
    RegistreRapportsPostes registre, IPostesConnusDepot postes, SondeServeur1 sonde, IReleveServeurs releves, IJournalDecisions journal,
    IHorloge horloge, ReglagesBascule reglages)
{
    private readonly DateTime demarrage = horloge.MaintenantUtc;
    private ResultatDecision? dernier;
    private int confirmations;

    public ResultatDecision? Dernier => dernier;
    public int Confirmations => confirmations;
    public DateTime DemarrageUtc => demarrage;

    /// <summary>Traite un rapport : mémorisé puis consigne d'après le verdict en cours (« attendre » tant qu'aucune évaluation n'existe).</summary>
    public async Task<ReponseRapport> TraiterRapportAsync(RapportPoste rapport, string adresse, bool authentifie, CancellationToken ct = default)
    {
        var maintenant = horloge.MaintenantUtc;
        switch (registre.Enregistrer(rapport, adresse, authentifie, maintenant))
        {
            case IssueRapport.TropRapide: return new ReponseRapport(StatutRapport.TropRapide, null);
            case IssueRapport.PosteDeuxAdresses:
                await journal.EcrireAsync("poste_deux_adresses", new { poste = rapport.Poste }, ct);
                return new ReponseRapport(StatutRapport.Ignore, new ConsigneRapport(ConstantesVigie.RoleVeille, ConstantesVigie.Rester, null));
        }
        var verdict = dernier?.Verdict ?? VerdictPromotion.Attendre;
        var consigne = ConsigneDuVerdict.Pour(verdict, rapport.Serveur1Joint);
        return new ReponseRapport(StatutRapport.Accepte,
            new ConsigneRapport(ConstantesVigie.RoleVeille, consigne, consigne == ConstantesVigie.Attendre ? ConsigneDuVerdict.MessageAttente : null));
    }

    /// <summary>Un tour de vigie (toutes les <c>SondeSecondes</c>) : sondes, postes connus (copie), décision, journal (changement de verdict seulement).</summary>
    public async Task<ResultatDecision> EvaluerAsync(CancellationToken ct = default)
    {
        var maintenant = horloge.MaintenantUtc;
        var s = await sonde.SonderAsync(maintenant, ct);
        var lus = await postes.LireAsync(ct);
        var connus = PostesConnus.Calculer(lus.Postes, lus.DerniereDonneeRecueUtc, TimeSpan.FromMinutes(reglages.PosteActifMinutes));
        var r = DecisionPromotion.Evaluer(new EntreeDecision(maintenant, releves.AutreServeurRegle, connus, registre.Rapports(), s, demarrage, reglages));
        var change = dernier is null || dernier.Verdict != r.Verdict;
        confirmations = change ? 1 : confirmations + 1;
        dernier = r;
        if (change)
            await journal.EcrireAsync("verdict", new
            {
                verdict = r.Verdict.ToString(), raison = r.Raison, connus = r.Connus, votes = r.Votes, votesAuthentifies = r.VotesAuthentifies,
                joignent = r.Joignent, alerte = r.TypeAlerte, serveur1 = new { s.BaseJointe, s.ApiJointe }, derniereDonneeRecue = lus.DerniereDonneeRecueUtc
            }, ct);
        return r;
    }
}
