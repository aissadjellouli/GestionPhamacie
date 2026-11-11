using Pharma.Application.Commun;
using Pharma.Application.Notifications;
using Pharma.Application.Securite;
using Pharma.Domain.Commun;
using Pharma.Domain.Securite;

namespace Pharma.Application.Surveillance;

// ---- Modèles ----------------------------------------------------------------

/// <summary>Ce qui se passe au comptoir sans laisser de vente (table evenement_caisse).</summary>
public static class TypesEvenementCaisse
{
    public const string LigneRetiree = "ligne_retiree";
    public const string TiroirOuvert = "tiroir_ouvert";
    public const string AttenteAbandonnee = "attente_abandonnee";
}

public sealed record EvenementCaisse(string Type, long UtilisateurId, string? Poste, long? SessionCaisseId,
    long? ProduitId = null, long? LotId = null, int? Quantite = null, decimal? Montant = null, string? Detail = null);

/// <summary>Anomalie créée (ou passée à « urgent ») par le calcul du jour : texte de notification sans donnée de patient.</summary>
public sealed record AnomalieNouvelle(long Id, string Niveau, string Notification);

/// <summary>Anomalie de surveillance (titulaire seul) : par employé et par jour, ou par patient (remises répétées).</summary>
public sealed record AlerteSurveillance(long Id, string Type, string Niveau, long? UtilisateurId, string? Employe, DateOnly Jour, string Message,
    decimal? Valeur, string Statut, DateTime MiseAJourLe, string? TraiteePar, DateTime? TraiteeLe, string? ActionPrise);

/// <summary>Seuils par employé et par jour ; horaires d'ouverture (null : ventes hors horaires non contrôlées).</summary>
public sealed record ParametresSurveillance(int SeuilLignesRetirees, int SeuilTiroir, int SeuilAttentesAbandonnees, int SeuilRemises,
    int SeuilRemisesPatient, TimeOnly? HeureOuverture, TimeOnly? HeureFermeture,
    int SeuilAjustements = 5, int SeuilCasses = 3, int SeuilEchantillons = 3, int JoursComptageSensibles = 7,
    bool ReceptionAveugle = true, int JoursAvoirRetour = 30);

// ---- Port -------------------------------------------------------------------

public interface ISurveillance
{
    Task EnregistrerAsync(EvenementCaisse evenement, CancellationToken ct = default);
    /// <summary>Calcul du jour (fonction SQL surveiller) : anomalies nouvelles, à notifier.</summary>
    Task<IReadOnlyList<AnomalieNouvelle>> SurveillerAsync(DateOnly jour, CancellationToken ct = default);
    Task<IReadOnlyList<AlerteSurveillance>> ListerAsync(bool traitees, int limite, CancellationToken ct = default);
    Task<bool> TraiterAsync(long id, string? actionPrise, long utilisateurId, CancellationToken ct = default);
    Task<ParametresSurveillance> ParametresAsync(CancellationToken ct = default);
    Task EnregistrerParametresAsync(ParametresSurveillance p, CancellationToken ct = default);
    Task<IReadOnlyList<ProduitSensible>> SensiblesAsync(CancellationToken ct = default);
    /// <summary>Marque ou démarque un produit sensible ; nom du produit, ou null s'il n'existe pas.</summary>
    Task<string?> MarquerSensibleAsync(long produitId, bool sensible, CancellationToken ct = default);
}

// ---- Cas d'usage --------------------------------------------------------------

/// <summary>
/// Calcul des anomalies du jour après une vente ou un événement de caisse ; les nouvelles sont notifiées au titulaire (SMS ou e-mail).
/// Jamais bloquant : la vente ou l'événement est déjà enregistré.
/// </summary>
public sealed class Surveiller(ISurveillance surveillance, IHorloge horloge, IAlertesTitulaire? alertes = null)
{
    public async Task ExecuterAsync(CancellationToken ct = default)
    {
        try
        {
            foreach (var a in await surveillance.SurveillerAsync(horloge.Aujourdhui, ct))
                if (alertes is not null) await alertes.SurveillanceAsync(a.Notification, a.Niveau == "urgent", ct);
        }
        catch (Exception e) when (e is not OperationCanceledException) { }
    }
}

/// <summary>
/// Le poste signale ce qui ne laisse pas de vente : ligne retirée du panier (boîtes, montant) ou tiroir ouvert sans vente
/// (droit caisse.ouvrir_tiroir). L'employé, le poste et sa caisse sont ceux de la requête.
/// </summary>
public sealed class SignalerEvenementCaisse(ISurveillance surveillance, Caisse.ISessionsCaisse sessions, IPermissionService droits, Surveiller surveiller, IJournal? journal = null)
{
    public async Task ExecuterAsync(string? type, long? produitId, long? lotId, int? quantite, decimal? montant, long utilisateurId, string? poste,
        CancellationToken ct = default)
    {
        if (type is not (TypesEvenementCaisse.LigneRetiree or TypesEvenementCaisse.TiroirOuvert))
            throw new RegleMetierException("Événement de caisse inconnu.");
        if (type == TypesEvenementCaisse.TiroirOuvert)
            await droits.ExigerAsync(utilisateurId, Permissions.CaisseOuvrirTiroir, "ouvrir le tiroir sans vente", ct);
        if (type == TypesEvenementCaisse.LigneRetiree && (produitId is null || quantite is not > 0))
            throw new RegleMetierException("Ligne retirée : produit et nombre de boîtes manquants.");
        var session = string.IsNullOrWhiteSpace(poste) ? null : await sessions.SessionDuPosteAsync(poste.Trim(), ct);
        await surveillance.EnregistrerAsync(new EvenementCaisse(type, utilisateurId, poste?.Trim(), session?.Id,
            type == TypesEvenementCaisse.LigneRetiree ? produitId : null, type == TypesEvenementCaisse.LigneRetiree ? lotId : null,
            quantite, montant is >= 0 ? montant : null), ct);
        if (type == TypesEvenementCaisse.TiroirOuvert && journal is not null)
            await journal.EcrireAsync(utilisateurId, "tiroir_ouvert", "session_caisse", session?.Id,
                new Dictionary<string, object?> { ["poste"] = poste?.Trim() }, Permissions.CaisseOuvrirTiroir, ct);
        await surveiller.ExecuterAsync(ct);
    }
}

public sealed class ListerSurveillance(ISurveillance surveillance, IPermissionService droits)
{
    public async Task<IReadOnlyList<AlerteSurveillance>> ExecuterAsync(bool traitees, long utilisateurId, CancellationToken ct = default)
    {
        await droits.ExigerTitulaireAsync(utilisateurId, "consulter la surveillance de la caisse", ct);
        return await surveillance.ListerAsync(traitees, traitees ? 200 : 500, ct);
    }
}

public sealed class TraiterSurveillance(ISurveillance surveillance, IPermissionService droits)
{
    public async Task<bool> ExecuterAsync(long id, string? actionPrise, long utilisateurId, CancellationToken ct = default)
    {
        await droits.ExigerTitulaireAsync(utilisateurId, "traiter une anomalie de surveillance", ct);
        var action = string.IsNullOrWhiteSpace(actionPrise) ? null : actionPrise.Trim();
        return await surveillance.TraiterAsync(id, action is { Length: > 300 } ? action[..300] : action, utilisateurId, ct);
    }
}

public sealed class ReglerSurveillance(ISurveillance surveillance, IPermissionService droits, IJournal journal)
{
    public async Task<ParametresSurveillance> LireAsync(long utilisateurId, CancellationToken ct = default)
    {
        await droits.ExigerTitulaireAsync(utilisateurId, "régler la surveillance de la caisse", ct);
        return await surveillance.ParametresAsync(ct);
    }

    public async Task EnregistrerAsync(ParametresSurveillance p, long utilisateurId, CancellationToken ct = default)
    {
        await droits.ExigerTitulaireAsync(utilisateurId, "régler la surveillance de la caisse", ct);
        if (p.SeuilLignesRetirees < 1 || p.SeuilTiroir < 1 || p.SeuilAttentesAbandonnees < 1 || p.SeuilRemises < 1 || p.SeuilRemisesPatient < 1
            || p.SeuilAjustements < 1 || p.SeuilCasses < 1 || p.SeuilEchantillons < 1)
            throw new RegleMetierException("Chaque seuil doit être d’au moins 1.");
        if (p.JoursComptageSensibles is < 1 or > 90)
            throw new RegleMetierException("Comptage des produits sensibles : tous les 1 à 90 jours.", "jours_sensibles");
        if (p.JoursAvoirRetour is < 1 or > 365)
            throw new RegleMetierException("Retour sans avoir : délai de 1 à 365 jours.", "jours_avoir");
        if ((p.HeureOuverture is null) != (p.HeureFermeture is null))
            throw new RegleMetierException("Horaires : renseignez l’ouverture et la fermeture, ou aucune des deux (pas de contrôle).", "heure_ouverture");
        if (p.HeureOuverture is { } o && p.HeureFermeture is { } f && o >= f)
            throw new RegleMetierException("Horaires : l’ouverture doit précéder la fermeture.", "heure_ouverture");
        await surveillance.EnregistrerParametresAsync(p, ct);
        await journal.EcrireAsync(utilisateurId, "reglages_surveillance", "parametre_surveillance", 1,
            new Dictionary<string, object?>
            {
                ["lignes_retirees"] = p.SeuilLignesRetirees, ["tiroir"] = p.SeuilTiroir, ["attentes"] = p.SeuilAttentesAbandonnees,
                ["remises"] = p.SeuilRemises, ["remises_patient"] = p.SeuilRemisesPatient, ["ajustements"] = p.SeuilAjustements,
                ["casses"] = p.SeuilCasses, ["echantillons"] = p.SeuilEchantillons, ["comptage_sensibles_jours"] = p.JoursComptageSensibles,
                ["reception_aveugle"] = p.ReceptionAveugle, ["avoir_retour_jours"] = p.JoursAvoirRetour,
                ["horaires"] = p.HeureOuverture is { } a ? $"{a:HH\\:mm}-{p.HeureFermeture:HH\\:mm}" : null
            }, null, ct);
    }
}

/// <summary>Produit sensible (cher, très demandé, souvent volé) : compté à part et régulièrement (inventaire « Produits sensibles »).</summary>
public sealed record ProduitSensible(long ProduitId, string Nom, int Stock);

public sealed class GererProduitsSensibles(ISurveillance surveillance, IPermissionService droits, IJournal journal)
{
    public async Task<IReadOnlyList<ProduitSensible>> ListerAsync(long utilisateurId, CancellationToken ct = default)
    {
        await droits.ExigerTitulaireAsync(utilisateurId, "consulter les produits sensibles", ct);
        return await surveillance.SensiblesAsync(ct);
    }

    public async Task MarquerAsync(long produitId, bool sensible, long utilisateurId, CancellationToken ct = default)
    {
        await droits.ExigerTitulaireAsync(utilisateurId, "choisir les produits sensibles", ct);
        var nom = await surveillance.MarquerSensibleAsync(produitId, sensible, ct) ?? throw new RegleMetierException("Produit introuvable.");
        await journal.EcrireAsync(utilisateurId, sensible ? "produit_sensible" : "produit_non_sensible", "produit", produitId,
            new Dictionary<string, object?> { ["produit"] = nom }, null, ct);
    }
}
