using Pharma.Application.Commun;
using Pharma.Application.Securite;
using Pharma.Domain.Commun;
using Pharma.Domain.Securite;

namespace Pharma.Application.Reprise;

/// <summary>
/// Patient de la reprise non créé au chargement : son n° d'assuré (chiffres seuls, n° + suffixe) était déjà pris par un patient en base
/// (Origine « base ») ou par une ligne précédente du même export (« fichier »). Statut : a_valider, garde_existant, remplace, ayant_droit ;
/// Dette : rattachee ou abandonnee (null sans dette ou avant décision). Script 092.
/// </summary>
public sealed record DoublonPatientImport(long Id, long SessionId, string Logiciel, string RefSource, int? Ligne,
    string Nom, string? Prenom, string? Telephone, string NumAssure, decimal SoldeCredit, string Origine,
    long PatientExistantId, string ExistantNom, string? ExistantNumAssure, string? ExistantTelephone, decimal ExistantSolde,
    string Statut, string? Dette, long? PatientCreeId, string? DecidePar, DateTime? DecideLe);

/// <summary>Filtre de la liste : statut (vide : tous), texte cherché dans les noms et n° d'assuré.</summary>
public sealed record FiltreDoublonsImport(string? Statut = null, string? Recherche = null);

public enum DecisionDoublon { GarderExistant, Remplacer, AyantDroit }

public enum DetteDoublon { Rattacher, Abandonner }

public interface IDoublonsImport
{
    Task<Page<DoublonPatientImport>> ListerAsync(FiltreDoublonsImport filtre, DemandePage page, CancellationToken ct = default);
    Task<DoublonPatientImport?> LireAsync(long id, CancellationToken ct = default);
    /// <summary>Applique la décision en une transaction (import.decider_doublon_patient) ; renvoie la fiche patient concernée.</summary>
    Task<long> DeciderAsync(long id, DecisionDoublon decision, DetteDoublon? dette, string? numAssure, long utilisateurId, CancellationToken ct = default);
}

/// <summary>
/// Doublons d'import à valider : réservés au titulaire ou à l'employé qui a le droit « Valider les doublons d'import » (reprise.doublons,
/// script 093, 2026-11-15 ; avant : credit.accorder). Décision d'administration : jamais autorisable par le code comptoir d'un collègue.
/// </summary>
public static class DroitDoublonsImport
{
    public static async Task ExigerAsync(IPermissionService droits, long utilisateurId, string action, CancellationToken ct)
    {
        if (await droits.EstTitulaireAsync(utilisateurId, ct)) return;
        await droits.ExigerAsync(utilisateurId, Permissions.RepriseDoublons, action, ct);
    }

    public static readonly string[] Statuts = ["a_valider", "garde_existant", "remplace", "ayant_droit"];
}

public sealed class ListerDoublonsImport(IDoublonsImport doublons, IPermissionService droits)
{
    public async Task<Page<DoublonPatientImport>> ExecuterAsync(FiltreDoublonsImport filtre, DemandePage page, long utilisateurId, CancellationToken ct = default)
    {
        await DroitDoublonsImport.ExigerAsync(droits, utilisateurId, "consulter les doublons d’import", ct);
        var statut = string.IsNullOrWhiteSpace(filtre.Statut) ? null : filtre.Statut.Trim();
        if (statut is not null && !DroitDoublonsImport.Statuts.Contains(statut)) throw new RegleMetierException("Statut inconnu.", "statut");
        var recherche = string.IsNullOrWhiteSpace(filtre.Recherche) ? null : filtre.Recherche.Trim();
        if (recherche is { Length: > 100 }) throw new RegleMetierException("Recherche : 100 caractères au plus.", "recherche");
        return await doublons.ListerAsync(new FiltreDoublonsImport(statut, recherche), page, ct);
    }
}

/// <summary>
/// Décision sur un doublon d'import : garder la fiche existante, la remplacer par les données importées, ou créer le patient comme ayant
/// droit distinct (autre suffixe). Dette reprise non nulle : la rattacher ou l'abandonner, choix obligatoire. Journalisée.
/// </summary>
public sealed class DeciderDoublonImport(IDoublonsImport doublons, IPermissionService droits, IJournal journal)
{
    public async Task<long> ExecuterAsync(long id, DecisionDoublon decision, DetteDoublon? dette, string? numAssure, long utilisateurId, CancellationToken ct = default)
    {
        await DroitDoublonsImport.ExigerAsync(droits, utilisateurId, "valider un doublon d’import", ct);
        var d = await doublons.LireAsync(id, ct) ?? throw new RegleMetierException("Doublon d’import introuvable.");
        if (d.Statut != "a_valider") throw new RegleMetierException("Ce doublon a déjà été traité : rien n’a été modifié.");
        if (d.SoldeCredit != 0 && dette is null)
            throw new RegleMetierException($"Dette reprise de {d.SoldeCredit:N2} DA : choisissez de la rattacher ou de l’abandonner.", "dette");
        var num = numAssure?.Trim();
        if (decision == DecisionDoublon.AyantDroit)
        {
            if (string.IsNullOrEmpty(num)) throw new RegleMetierException("Saisissez le n° d’assuré de l’ayant droit (avec son suffixe).", "num_assure");
            if (num.Length > 40) throw new RegleMetierException("N° d’assuré : 40 caractères au plus.", "num_assure");
            if (Chiffres(num) == Chiffres(d.NumAssure)) throw new RegleMetierException("Même n° que la fiche existante : changez le suffixe de l’ayant droit.", "num_assure");
        }
        else num = null;

        var patient = await doublons.DeciderAsync(id, decision, d.SoldeCredit == 0 ? null : dette, num, utilisateurId, ct);
        await journal.EcrireAsync(utilisateurId, "reprise_doublon_patient", "import.doublon_patient", id, new Dictionary<string, object?>
        {
            ["decision"] = Code(decision),
            ["dette"] = d.SoldeCredit == 0 ? null : dette == DetteDoublon.Rattacher ? "rattachee" : "abandonnee",
            ["montant_dette"] = d.SoldeCredit,
            ["session"] = d.SessionId,
            ["ref_source"] = d.RefSource,
            ["patient_existant"] = d.PatientExistantId,
            ["patient"] = patient
        }, null, ct);
        return patient;
    }

    public static string Code(DecisionDoublon d) => d switch
    {
        DecisionDoublon.GarderExistant => "garder_existant", DecisionDoublon.Remplacer => "remplacer", _ => "ayant_droit"
    };

    public static string Chiffres(string? n) => new((n ?? "").Where(char.IsAsciiDigit).ToArray());
}
