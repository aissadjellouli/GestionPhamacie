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
public sealed class OffreCourante(IActivationsOffreDepot depot, IHorloge horloge, byte[]? clePublique = null, CacheOffre? cache = null)
    : ILimitesOffre, IPacksOffre
{
    /// <summary>Offre de base : 2 postes inclus (proposition du 2026-11-26, docs/offre-et-options.md).</summary>
    public const int PostesBase = 2;
    public const int GraceJours = 15, RappelJours = 30, RappelProcheJours = 7;

    private readonly byte[] cle = clePublique ?? CleEditeur.Publique;

    public async Task<int> MaximumPostesAsync(CancellationToken ct = default) => (await LireAsync(ct)).Postes;

    public async Task<bool> InclusAsync(string pack, CancellationToken ct = default) => (await LireAsync(ct)).Packs.Contains(pack);

    public async Task<int> ScannersMobilesAsync(CancellationToken ct = default) => (await LireAsync(ct)).ScannersMobiles;

    /// <summary>Vide le cache (après une saisie ou une réception de code).</summary>
    public void Invalider() => cache?.Vider();

    /// <summary>Contrôle une activation pour CETTE pharmacie : signature, officine, NIF. Lève <see cref="ActivationInvalideException"/>.</summary>
    public ActivationOffre Controler(string code, IdentiteOffre identite)
    {
        var a = CodeActivation.Lire(code, cle);
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
