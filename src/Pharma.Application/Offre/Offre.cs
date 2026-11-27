using Pharma.Application.Commun;
using Pharma.Application.Securite;
using Pharma.Domain.Commun;

namespace Pharma.Application.Offre;

/// <summary>Activation rangée dans la base (table activation_offre, script 172).</summary>
public sealed record ActivationEnregistree(long Id, string Code, DateTime EmiseLeUtc, string Source, DateTime SaisieLeUtc, string? SaisiePar,
    Guid? OrdreCentral, DateTime? RefuseeLeUtc, string? MotifRefus);

/// <summary>Identité de la pharmacie à laquelle une activation doit correspondre : code de l'officine et NIF (fiche de la pharmacie).</summary>
public sealed record IdentiteOffre(Guid Officine, string? Nif);

public interface IActivationsOffreDepot
{
    Task<IdentiteOffre> IdentiteAsync(CancellationToken ct = default);
    /// <summary>Activations, les plus récentes (date de signature) d'abord.</summary>
    Task<IReadOnlyList<ActivationEnregistree>> ListerAsync(CancellationToken ct = default);
    /// <summary>Range une activation contrôlée ; faux si ce code est déjà rangé.</summary>
    Task<bool> AjouterAsync(string code, DateTime emiseLeUtc, string source, long? saisiePar, Guid? ordreCentral, CancellationToken ct = default);
    /// <summary>Note une seule fois qu'une activation rangée ne passe plus le contrôle (refusee_le, motif) ; vrai si c'est la première fois.</summary>
    Task<bool> SignalerRefusAsync(long id, string motif, CancellationToken ct = default);
    /// <summary>Rappel de fin d'offre (étape « 90j » … « 1j » ou « grace ») déjà noté au journal des actions pour cette date de fin.</summary>
    Task<bool> RappelNoteAsync(string etape, DateOnly fin, CancellationToken ct = default);
    /// <summary>
    /// Note le rappel au journal des actions (offre_rappel), APRÈS sa mise en file : vrai s'il ne l'était pas encore. Noter d'abord
    /// perdrait le rappel si l'API s'arrêtait entre les deux (revue de code du 2026-11-27) ; au pire, un arrêt entre la mise en file et la
    /// note donne un rappel en double, jamais un rappel perdu.
    /// </summary>
    Task<bool> NoterRappelAsync(string etape, DateOnly fin, CancellationToken ct = default);
}

/// <summary>Limites de l'offre en vigueur, lues par les cas d'usage concernés (premier cas : le maximum de postes autorisés).</summary>
public interface ILimitesOffre
{
    Task<int> MaximumPostesAsync(CancellationToken ct = default);
}

/// <summary>Packs de l'offre en vigueur (lot O6) : routes, écrans et tâches d'un pack non inclus sont fermés.</summary>
public interface IPacksOffre
{
    Task<bool> InclusAsync(string pack, CancellationToken ct = default);
    Task<int> ScannersMobilesAsync(CancellationToken ct = default);
}

/// <summary>Action d'un pack non inclus dans l'offre : refusée (403, « Option non incluse — voir Mon offre »).</summary>
public sealed class OptionNonIncluseException(string pack) : Exception(PacksOffre.MessageNonInclus(pack))
{
    public string Pack { get; } = pack;
}

public static class PacksOffreExtensions
{
    /// <summary>Refuse l'action si le pack n'est pas inclus dans l'offre en vigueur.</summary>
    public static async Task ExigerPackAsync(this IPacksOffre packs, string pack, CancellationToken ct = default)
    {
        if (!await packs.InclusAsync(pack, ct)) throw new OptionNonIncluseException(pack);
    }
}

/// <summary>
/// Offre en vigueur gardée une minute (une lecture de la base et un contrôle de signature par minute au plus, pas à chaque requête) ;
/// vidée à chaque saisie ou réception d'un code. Singleton.
/// </summary>
public sealed class CacheOffre
{
    public static readonly TimeSpan Duree = TimeSpan.FromMinutes(1);
    private (EtatOffre Etat, DateTime LeUtc)? valeur;
    private readonly object verrou = new();

    public EtatOffre? Lire(DateTime maintenantUtc)
    {
        lock (verrou) return valeur is { } v && maintenantUtc - v.LeUtc < Duree && maintenantUtc >= v.LeUtc ? v.Etat : null;
    }

    public void Ecrire(EtatOffre etat, DateTime maintenantUtc) { lock (verrou) valeur = (etat, maintenantUtc); }

    public void Vider() { lock (verrou) valeur = null; }
}

/// <summary>Offre en vigueur.</summary>
/// <param name="Etat">base (aucune activation valable), active, grace (échue depuis moins de 15 jours).</param>
/// <param name="Alerte">Avertissement à montrer au titulaire (échéance proche, délai de grâce, activation refusée), sinon null.</param>
public sealed record EtatOffre(string Etat, IReadOnlyList<string> Packs, int Postes, int ScannersMobiles, DateOnly? Fin, bool Transition,
    DateTime? EmiseLeUtc, string? Alerte);

public static class EtatsOffre
{
    public const string Base = "base", Active = "active", Grace = "grace";
}

/// <summary>
/// Offre en vigueur (décisions du titulaire du 2026-11-26) : la plus récente activation rangée qui passe le contrôle (signature de
/// l'éditeur, même officine, même NIF) et n'est pas échue depuis plus de 15 jours. Sans activation valable : offre de base, 2 postes ;
/// une activation effacée ou modifiée dans la base ramène à l'offre de base, jamais à tout ouvert.
/// </summary>
public sealed class OffreCourante(IActivationsOffreDepot depot, IHorloge horloge, IReadOnlyList<byte[]>? clesPubliques = null, CacheOffre? cache = null)
    : ILimitesOffre, IPacksOffre
{
    /// <summary>Offre de base : 2 postes inclus (proposition du 2026-11-26, docs/offre-et-options.md).</summary>
    public const int PostesBase = 2;
    public const int GraceJours = 15;
    /// <summary>Rappels avant la fin (« go rappels » du 2026-11-27) : 3 mois, 2 mois, 1 mois, 15 jours, 7 jours, 1 jour ; le message de Mon offre
    /// s'affiche dès le premier.</summary>
    public static readonly IReadOnlyList<int> RappelsJours = [90, 60, 30, 15, 7, 1];
    public static int RappelJours => RappelsJours[0];

    private readonly IReadOnlyList<byte[]> cles = clesPubliques ?? CleEditeur.Publiques;

    public async Task<int> MaximumPostesAsync(CancellationToken ct = default) => (await LireAsync(ct)).Postes;

    public async Task<bool> InclusAsync(string pack, CancellationToken ct = default) => (await LireAsync(ct)).Packs.Contains(pack);

    public async Task<int> ScannersMobilesAsync(CancellationToken ct = default) => (await LireAsync(ct)).ScannersMobiles;

    /// <summary>Vide le cache (après une saisie ou une réception de code).</summary>
    public void Invalider() => cache?.Vider();

    /// <summary>Contrôle une activation pour CETTE pharmacie : signature, officine, NIF. Lève <see cref="ActivationInvalideException"/>.</summary>
    public ActivationOffre Controler(string code, IdentiteOffre identite)
    {
        var a = CodeActivation.Lire(code, cles);
        if (a.Officine != identite.Officine)
            throw new ActivationInvalideException("Ce code a été émis pour une autre pharmacie (code de l’officine différent).");
        var nif = CodeActivation.NormaliserNif(identite.Nif);
        if (nif.Length == 0) throw new ActivationInvalideException("NIF de la pharmacie non saisi : saisissez-le avant l’activation.");
        if (a.Nif != nif) throw new ActivationInvalideException("Ce code a été émis pour un autre NIF que celui de la pharmacie.");
        return a;
    }

    /// <summary>Jours restants avant la fin du délai de grâce (négatif : échue depuis plus de 15 jours).</summary>
    public static int JoursAvantFermeture(DateOnly fin, DateOnly aujourdhui) => fin.AddDays(GraceJours).DayNumber - aujourdhui.DayNumber;

    public async Task<EtatOffre> LireAsync(CancellationToken ct = default)
    {
        if (cache?.Lire(horloge.MaintenantUtc) is { } garde) return garde;
        var etat = await CalculerAsync(ct);
        cache?.Ecrire(etat, horloge.MaintenantUtc);
        return etat;
    }

    private async Task<EtatOffre> CalculerAsync(CancellationToken ct)
    {
        var identite = await depot.IdentiteAsync(ct);
        var aujourdhui = horloge.Aujourdhui;
        string? refus = null;
        foreach (var e in await depot.ListerAsync(ct))
        {
            ActivationOffre a;
            try { a = Controler(e.Code, identite); }
            catch (ActivationInvalideException x)
            {
                // Q4 : signature absente ou fausse, ou autre pharmacie : offre de base, signalé au titulaire (noté une fois au journal)
                await depot.SignalerRefusAsync(e.Id, x.Message, ct);
                refus ??= $"Activation du {e.EmiseLeUtc:dd/MM/yyyy} refusée : {x.Message}";
                continue;
            }
            if (a.Fin is { } fin && JoursAvantFermeture(fin, aujourdhui) < 0) continue;   // échue depuis plus de 15 jours
            string etat = EtatsOffre.Active;
            string? alerte = null;
            if (a.Fin is { } f)
            {
                var restants = f.DayNumber - aujourdhui.DayNumber;
                if (restants < 0)
                {
                    etat = EtatsOffre.Grace;
                    alerte = $"Offre échue le {f:dd/MM/yyyy} : délai de grâce jusqu’au {f.AddDays(GraceJours):dd/MM/yyyy}, puis retour à l’offre de base "
                             + "(aucune donnée effacée). Demandez un nouveau code à l’éditeur.";
                }
                else if (restants <= RappelJours)
                    alerte = $"{(a.Transition ? "Période de transition" : "Offre")} valable jusqu’au {f:dd/MM/yyyy} ({restants} jour(s)) : demandez un nouveau code à l’éditeur.";
            }
            return new EtatOffre(etat, a.Packs, a.Postes, a.ScannersMobiles, a.Fin, a.Transition, a.EmiseLeUtc, alerte ?? refus);
        }
        return new EtatOffre(EtatsOffre.Base, [], PostesBase, 0, null, false, null,
            refus ?? "Aucune activation : offre de base (2 postes). Saisissez le code d’activation fourni par l’éditeur.");
    }
}

/// <summary>
/// Rappels de fin d'offre au titulaire (décision Q5 du 2026-11-26, puis « go rappels » du 2026-11-27) : SMS ou e-mail à 90, 60, 30, 15, 7 et
/// 1 jour(s) avant la fin, puis au début du délai de grâce, une seule fois chacun pour une date de fin donnée ; seule l'étape la plus proche
/// est envoyée (une offre saisie à 10 jours de sa fin n'envoie que le rappel des 15 jours). L'écran Mon offre garde son message dès 90 jours.
/// Appelé une fois par jour par la tâche des notifications.
/// </summary>
public sealed class RappelsOffre(OffreCourante offre, IActivationsOffreDepot depot, Notifications.IAlertesTitulaire alertes, IHorloge horloge)
{
    public const string EtapeGrace = "grace";

    /// <summary>Nom de l'étape d'un rappel avant la fin (« 90j », « 60j » … « 1j »).</summary>
    public static string EtapeAvant(int jours) => $"{jours}j";

    /// <summary>
    /// Étape due pour cette date de fin ce jour-là, ou null : le plus petit seuil (90, 60, 30, 15, 7, 1 jours) qui couvre les jours restants
    /// (le dernier jour compte dans « 1j ») ; « grace » pendant le délai de grâce ; null au-delà de 90 jours ou après la grâce.
    /// </summary>
    public static string? Etape(DateOnly fin, DateOnly aujourdhui)
    {
        var restants = fin.DayNumber - aujourdhui.DayNumber;
        if (restants < 0) return OffreCourante.JoursAvantFermeture(fin, aujourdhui) >= 0 ? EtapeGrace : null;
        string? etape = null;
        foreach (var seuil in OffreCourante.RappelsJours)
            if (restants <= seuil) etape = EtapeAvant(seuil);
        return etape;
    }

    /// <returns>Étape envoyée, ou null.</returns>
    public async Task<string?> ExecuterAsync(CancellationToken ct = default)
    {
        var e = await offre.LireAsync(ct);
        if (e.Fin is not { } fin || Etape(fin, horloge.Aujourdhui) is not { } etape) return null;
        if (await depot.RappelNoteAsync(etape, fin, ct)) return null;
        var quoi = e.Transition ? "La période de transition" : "Votre offre";
        var (objet, detail, sms) = etape switch
        {
            EtapeGrace => ("Offre échue : délai de grâce",
                $"{quoi} a pris fin le {fin:dd/MM/yyyy}. Délai de grâce jusqu’au {fin.AddDays(OffreCourante.GraceJours):dd/MM/yyyy}, puis retour à l’offre de base (2 postes, options fermées, aucune donnée effacée). Demandez un nouveau code à l’éditeur (Administration › Mon offre).",
                $"Pharmacie : offre echue le {fin:dd/MM}, grace jusqu au {fin.AddDays(OffreCourante.GraceJours):dd/MM}. Demandez un code a l editeur."),
            _ => ("Offre bientôt échue",
                $"{quoi} prend fin le {fin:dd/MM/yyyy} ({fin.DayNumber - horloge.Aujourdhui.DayNumber} jour(s)). Demandez un nouveau code à l’éditeur (Administration › Mon offre) ; après la fin, {OffreCourante.GraceJours} jours de grâce puis retour à l’offre de base.",
                $"Pharmacie : offre valable jusqu au {fin:dd/MM}. Demandez un code a l editeur.")
        };
        // Mise en file d'abord, note ensuite : un arrêt entre les deux donne au pire un rappel en double, jamais un rappel perdu
        await alertes.OffreAsync(objet, detail, sms, ct);
        await depot.NoterRappelAsync(etape, fin, ct);
        return etape;
    }
}

/// <summary>Saisie d'une activation (écran « Mon offre », titulaire) ou réception par le serveur central (ordre activation_offre).</summary>
public sealed class GererOffre(IActivationsOffreDepot depot, OffreCourante offre, IPermissionService droits, IJournal journal, IHorloge horloge)
{
    public const string ActionActivee = "offre_activee";
    public const string SourceCode = "code", SourceCentral = "central";

    public async Task<(EtatOffre Etat, IdentiteOffre Identite, IReadOnlyList<ActivationEnregistree> Historique, int PostesComptes)> LireAsync(
        long utilisateurId, Func<CancellationToken, Task<int>> postesComptes, CancellationToken ct = default)
    {
        await droits.ExigerTitulaireAsync(utilisateurId, "consulter l’offre de la pharmacie", ct);
        return (await offre.LireAsync(ct), await depot.IdentiteAsync(ct), await depot.ListerAsync(ct), await postesComptes(ct));
    }

    /// <summary>Saisie d'un code par le titulaire (ou par l'éditeur lors de la mise en place, connecté en titulaire).</summary>
    public async Task<ActivationOffre> SaisirAsync(long utilisateurId, string? code, CancellationToken ct = default)
    {
        await droits.ExigerTitulaireAsync(utilisateurId, "saisir un code d’activation", ct);
        var a = await RangerAsync(code, SourceCode, utilisateurId, null, ct);
        await journal.EcrireAsync(utilisateurId, ActionActivee, "activation_offre", null, Details(a, SourceCode), null, ct);
        return a;
    }

    /// <summary>Activation transmise par le serveur central (Q2, Q7) : contrôlée ici comme une saisie ; le central ne peut pas en créer.</summary>
    public Task<ActivationOffre> RecevoirDuCentralAsync(string? code, Guid ordre, CancellationToken ct = default) =>
        RangerAsync(code, SourceCentral, null, ordre, ct);

    private async Task<ActivationOffre> RangerAsync(string? code, string source, long? par, Guid? ordre, CancellationToken ct)
    {
        ActivationOffre a;
        try { a = offre.Controler(code ?? "", await depot.IdentiteAsync(ct)); }
        catch (ActivationInvalideException x) { throw new RegleMetierException(x.Message, "code"); }
        if (a.Fin is { } fin && OffreCourante.JoursAvantFermeture(fin, horloge.Aujourdhui) < 0)
            throw new RegleMetierException($"Code échu depuis le {fin:dd/MM/yyyy} (délai de grâce passé) : demandez un nouveau code à l’éditeur.", "code");
        var enVigueur = await offre.LireAsync(ct);
        if (enVigueur.EmiseLeUtc is { } derniere && a.EmiseLeUtc < derniere)
            throw new RegleMetierException($"Code plus ancien que l’activation en place (signée le {derniere:dd/MM/yyyy}) : il n’est pas pris.", "code");
        if (!await depot.AjouterAsync(code!.Trim(), a.EmiseLeUtc, source, par, ordre, ct))
            throw new RegleMetierException("Ce code est déjà enregistré.", "code");
        offre.Invalider();
        return a;
    }

    private static Dictionary<string, object?> Details(ActivationOffre a, string source) => new()
    {
        ["source"] = source, ["signee_le"] = a.EmiseLeUtc, ["packs"] = string.Join(",", a.Packs), ["postes"] = a.Postes,
        ["scanners_mobiles"] = a.ScannersMobiles, ["fin"] = a.Fin?.ToString("yyyy-MM-dd"), ["transition"] = a.Transition
    };
}
