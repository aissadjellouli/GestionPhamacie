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
    bool ReceptionAveugle = true, int JoursAvoirRetour = 30,
    decimal SeuilCreditNouveauPatient = 5000, int JoursPatientRecent = 7, decimal SeuilCreditsJour = 20000, int JoursCreditSansRemboursement = 60,
    int SeuilAutorisationsValideur = 10, int SeuilAutorisationsEmploye = 5, int MinutesPostesSimultanes = 10, int SeuilChangementsPoste = 3,
    int SeuilBaissesPrix = 1, int SeuilSortiesTiroir = 5);

// ---- Port -------------------------------------------------------------------

public interface ISurveillance
{
    Task EnregistrerAsync(EvenementCaisse evenement, CancellationToken ct = default);
    /// <summary>Calcul du jour (fonction SQL surveiller) : anomalies nouvelles, à notifier.</summary>
    /// <param name="jour">Journée calculée ; null : la date du jour du serveur PostgreSQL.</param>
    Task<IReadOnlyList<AnomalieNouvelle>> SurveillerAsync(DateOnly? jour, CancellationToken ct = default);
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
/// Jamais bloquant : la vente ou l'événement est déjà enregistré ; un échec est inscrit au journal du serveur. La journée est celle
/// du serveur PostgreSQL (les ventes et les horaires sont comparés à son heure).
/// </summary>
public sealed class Surveiller(ISurveillance surveillance, IAlertesTitulaire? alertes = null, ITraceErreurs? trace = null)
{
    public async Task ExecuterAsync(CancellationToken ct = default)
    {
        try
        {
            foreach (var a in await surveillance.SurveillerAsync(null, ct))
                if (alertes is not null) await alertes.SurveillanceAsync(a.Notification, a.Niveau == "urgent", ct);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            trace?.Erreur("Calcul des anomalies de surveillance en échec", e);
        }
    }
}

/// <summary>
/// Le poste signale ce qui ne laisse pas de vente : ligne retirée du panier (boîtes, montant) ou tiroir ouvert sans vente
/// (droit caisse.ouvrir_tiroir). L'employé, le poste et sa caisse sont ceux de la requête.
/// Tiroir : motif obligatoire, choisi parmi les motifs actifs (« Autre » avec texte, script 094), inscrit avec l'événement et au journal.
/// </summary>
public sealed class SignalerEvenementCaisse(ISurveillance surveillance, Caisse.ISessionsCaisse sessions, IPermissionService droits, Surveiller surveiller,
    IJournal? journal = null, Referentiels.MotifsActifs? motifs = null, Caisse.OuvrirTiroirAvecEffet? avecEffet = null)
{
    /// <param name="saisie">Motif à effet (script 096) : montant et détail exigé ; ignoré pour une ligne retirée.</param>
    /// <returns>Bon de sortie (ou d'entrée) créé pour un motif à effet, null sinon.</returns>
    public async Task<Caisse.MouvementTiroirEcrit?> ExecuterAsync(string? type, long? produitId, long? lotId, int? quantite, decimal? montant, long utilisateurId,
        string? poste, CancellationToken ct = default, string? motifCode = null, string? motifTexte = null, Pharma.Domain.Caisse.SaisieTiroir? saisie = null)
    {
        if (type is not (TypesEvenementCaisse.LigneRetiree or TypesEvenementCaisse.TiroirOuvert))
            throw new RegleMetierException("Événement de caisse inconnu.");
        (string Code, string Libelle, string? Texte)? motif = null;
        if (type == TypesEvenementCaisse.TiroirOuvert)
        {
            await droits.ExigerAsync(utilisateurId, Permissions.CaisseOuvrirTiroir, "ouvrir le tiroir sans vente", ct);
            if (motifs is null) throw new RegleMetierException("Motifs d’ouverture du tiroir indisponibles.");
            motif = await motifs.ChoisirAsync(Pharma.Domain.Referentiels.RegleMotifs.Tiroir, motifCode, motifTexte, "de l’ouverture du tiroir", ct);
            // Motif à effet (sortie ou entrée d'argent, script 096) : montant, détail et écriture de trésorerie dans la même transaction
            var regle = (await motifs.ExecuterAsync(Pharma.Domain.Referentiels.RegleMotifs.Tiroir, ct)).First(m => m.Code == motif.Value.Code);
            if (regle.Effet != Pharma.Domain.Caisse.RegleTiroir.Aucun)
            {
                if (avecEffet is null) throw new RegleMetierException("Sorties et entrées du tiroir indisponibles.");
                var ecrit = await avecEffet.ExecuterAsync(regle, motif.Value.Libelle, saisie, utilisateurId, poste, ct);
                await surveiller.ExecuterAsync(ct);
                return ecrit;
            }
            Pharma.Domain.Caisse.RegleTiroir.VerifierMontant(regle.Effet, saisie?.Montant);
        }
        if (type == TypesEvenementCaisse.LigneRetiree && (produitId is null || quantite is not > 0))
            throw new RegleMetierException("Ligne retirée : produit et nombre de boîtes manquants.");
        var session = string.IsNullOrWhiteSpace(poste) ? null : await sessions.SessionDuPosteAsync(poste.Trim(), ct);
        await surveillance.EnregistrerAsync(new EvenementCaisse(type, utilisateurId, poste?.Trim(), session?.Id,
            type == TypesEvenementCaisse.LigneRetiree ? produitId : null, type == TypesEvenementCaisse.LigneRetiree ? lotId : null,
            quantite, montant is >= 0 ? montant : null, motif?.Libelle), ct);
        if (type == TypesEvenementCaisse.TiroirOuvert && journal is not null)
            await journal.EcrireAsync(utilisateurId, "tiroir_ouvert", "session_caisse", session?.Id,
                new Dictionary<string, object?> { ["poste"] = poste?.Trim(), ["motif"] = motif?.Libelle, ["motif_code"] = motif?.Code },
                Permissions.CaisseOuvrirTiroir, ct);
        await surveiller.ExecuterAsync(ct);
        return null;
    }
}

public sealed class ListerSurveillance(ISurveillance surveillance, IPermissionService droits)
{
    public async Task<IReadOnlyList<AlerteSurveillance>> ExecuterAsync(bool traitees, long utilisateurId, CancellationToken ct = default)
    {
        await droits.ExigerTitulaireAsync(utilisateurId, "consulter le contrôle interne", ct);
        return await surveillance.ListerAsync(traitees, traitees ? 200 : 500, ct);
    }
}

public sealed class TraiterSurveillance(ISurveillance surveillance, IPermissionService droits, Securite.VerificationMotDePasseActuel verification)
{
    public const string ChampMotDePasseTitulaire = "mot_de_passe_titulaire";

    /// <param name="motDePasseTitulaire">Mot de passe du titulaire, ressaisi : marquer une anomalie « traitée » la sort de la liste.</param>
    public async Task<bool> ExecuterAsync(long id, string? actionPrise, long utilisateurId, CancellationToken ct = default, string? motDePasseTitulaire = null)
    {
        await droits.ExigerTitulaireAsync(utilisateurId, "traiter un point du contrôle interne", ct);
        await verification.VerifierAsync(utilisateurId, motDePasseTitulaire, "traiter_anomalie_surveillance", ChampMotDePasseTitulaire, ct);
        var action = string.IsNullOrWhiteSpace(actionPrise) ? null : actionPrise.Trim();
        return await surveillance.TraiterAsync(id, action is { Length: > 300 } ? action[..300] : action, utilisateurId, ct);
    }
}

public sealed class ReglerSurveillance(ISurveillance surveillance, IPermissionService droits, IJournal journal, Reglages.IGardeReglagesCentraux? garde = null)
{
    public async Task<ParametresSurveillance> LireAsync(long utilisateurId, CancellationToken ct = default)
    {
        await droits.ExigerTitulaireAsync(utilisateurId, "régler le contrôle interne", ct);
        return await surveillance.ParametresAsync(ct);
    }

    public async Task EnregistrerAsync(ParametresSurveillance p, long utilisateurId, CancellationToken ct = default)
    {
        await droits.ExigerTitulaireAsync(utilisateurId, "régler le contrôle interne", ct);
        Pharma.Domain.Reglages.RegleSurveillance.VerifierSeuils(new Pharma.Domain.Reglages.SeuilsSurveillance(p.SeuilLignesRetirees, p.SeuilTiroir,
            p.SeuilAttentesAbandonnees, p.SeuilRemises, p.SeuilRemisesPatient, p.SeuilAjustements, p.SeuilCasses, p.SeuilEchantillons,
            p.JoursComptageSensibles, p.ReceptionAveugle, p.JoursAvoirRetour, p.SeuilCreditNouveauPatient, p.JoursPatientRecent, p.SeuilCreditsJour,
            p.JoursCreditSansRemboursement, p.SeuilAutorisationsValideur, p.SeuilAutorisationsEmploye, p.MinutesPostesSimultanes,
            p.SeuilChangementsPoste, p.SeuilBaissesPrix, p.SeuilSortiesTiroir));
        Pharma.Domain.Reglages.RegleSurveillance.VerifierHoraires(p.HeureOuverture, p.HeureFermeture);
        var avant = Valeurs(await surveillance.ParametresAsync(ct));
        // Journal des modifications : chaque réglage changé, avec l'ancienne et la nouvelle valeur
        var changes = Valeurs(p).Where(v => !Equals(avant.GetValueOrDefault(v.Key), v.Value))
            .ToDictionary(v => v.Key, v => (object?)new { avant = avant.GetValueOrDefault(v.Key), apres = v.Value });
        // Seuils imposés par le serveur central : refus si un seuil change ; les horaires d'ouverture restent locaux (D7)
        if (garde is not null && changes.Keys.Any(k => k != "horaires"))
            await garde.ExigerModifiableAsync(Pharma.Domain.Reglages.TablesMutualisables.Surveillance, ct);
        await surveillance.EnregistrerParametresAsync(p, ct);
        if (changes.Count > 0)
            await journal.EcrireAsync(utilisateurId, "reglages_surveillance", "parametre_surveillance", 1, changes, null, ct);
    }

    private static Dictionary<string, object?> Valeurs(ParametresSurveillance p) => new()
    {
        ["lignes_retirees"] = p.SeuilLignesRetirees, ["tiroir"] = p.SeuilTiroir, ["attentes"] = p.SeuilAttentesAbandonnees,
        ["remises"] = p.SeuilRemises, ["remises_patient"] = p.SeuilRemisesPatient, ["ajustements"] = p.SeuilAjustements,
        ["casses"] = p.SeuilCasses, ["echantillons"] = p.SeuilEchantillons, ["comptage_sensibles_jours"] = p.JoursComptageSensibles,
        ["reception_aveugle"] = p.ReceptionAveugle, ["avoir_retour_jours"] = p.JoursAvoirRetour,
        ["credit_nouveau_patient"] = p.SeuilCreditNouveauPatient, ["patient_recent_jours"] = p.JoursPatientRecent,
        ["credits_jour"] = p.SeuilCreditsJour, ["credit_sans_remboursement_jours"] = p.JoursCreditSansRemboursement,
        ["autorisations_valideur"] = p.SeuilAutorisationsValideur, ["autorisations_employe"] = p.SeuilAutorisationsEmploye,
        ["postes_minutes"] = p.MinutesPostesSimultanes, ["changements_poste"] = p.SeuilChangementsPoste, ["baisses_prix"] = p.SeuilBaissesPrix, ["sorties_tiroir"] = p.SeuilSortiesTiroir,
        ["horaires"] = p.HeureOuverture is { } a ? $"{a:HH\\:mm}-{p.HeureFermeture:HH\\:mm}" : null
    };
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
