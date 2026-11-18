using Pharma.Application.Commun;
using Pharma.Application.Securite;
using Pharma.Domain.Securite;

namespace Pharma.Application.Patients;

/// <summary>
/// Ligne de Patients › « Fichier des patients ». SoldeCredit : dette non soldée (null sans le droit qui montre les crédits).
/// EtatDroits : « expires » ou « bientot » pour un patient CNAS/CASNOS (même règle que la liste de suivi), sinon null.
/// </summary>
public sealed record LigneFichierPatient(long Id, string Nom, string? Prenom, string? Telephone, string? NumAssure, string? Organisme,
    DateOnly? DateFinDroit, bool Chronique, bool ConsentementSms, decimal? SoldeCredit, string? EtatDroits = null);

/// <summary>
/// Filtres du fichier des patients, déjà contrôlés. Texte : nom, prénom, n° d'assuré ou téléphone. OrganismeId / SansOrganisme :
/// un organisme, ou les patients sans organisme (non assurés). ACredit : dette non soldée (vrai) ou nulle (faux).
/// Droits : « expires_ou_bientot », « expires » ou « bientot » (patients CNAS/CASNOS, même règle que la liste de suivi des droits).
/// </summary>
public sealed record FiltreFichierPatients(string? Texte, long? OrganismeId, bool SansOrganisme, bool? Chronique, bool? ACredit,
    string? Droits, bool? AccordSms, DateOnly Aujourdhui, int JoursAlerte);

public interface IFichierPatients
{
    Task<Page<LigneFichierPatient>> PageAsync(FiltreFichierPatients filtre, DemandePage page, CancellationToken ct = default);
}

/// <summary>Valeurs des listes déroulantes du fichier, telles qu'envoyées par le poste (bornées : toute autre valeur est ignorée).</summary>
public sealed record SaisieFiltreFichierPatients(string? Texte = null, string? Organisme = null, bool? Chronique = null, bool? Credit = null,
    string? Droits = null, bool? Sms = null);

/// <summary>
/// Patients › « Fichier des patients » (2026-11-18) : tous les patients, filtrés et paginés par le serveur. Droits : ceux de la
/// recherche de patients et de la fiche patient (droit de vendre) ; le filtre « à crédit » et le montant de la dette suivent le droit
/// qui montre déjà la dette d'un patient (fiche contact « Dette en cours », relevé de dette de la Caisse : droit de vendre).
/// Aucune donnée de plus que la fiche patient : nom, téléphone, organisme, n° d'assuré, fin de droits, chronique, accord SMS, dette.
/// </summary>
public sealed class ListerFichierPatients(IFichierPatients fichier, Caisse.ISessionsCaisse caisse, IHorloge horloge, IPermissionService droits)
{
    /// <summary>Droit de consulter le fichier : celui de la recherche de patients et de la fiche patient.</summary>
    public const string Droit = Permissions.CaisseVendre;
    /// <summary>Droit qui montre la dette d'un patient (fiche contact, relevé de dette de la Caisse).</summary>
    public const string DroitCredit = Permissions.CaisseVendre;
    public const int LongueurTexteMax = 100;
    public static readonly string[] EtatsDroits = ["expires_ou_bientot", "expires", "bientot"];

    public async Task<Page<LigneFichierPatient>> ExecuterAsync(SaisieFiltreFichierPatients saisie, long utilisateurId, DemandePage page,
        CancellationToken ct = default)
    {
        await droits.ExigerAsync(utilisateurId, Droit, "consulter le fichier des patients", ct);
        var voitCredit = await droits.APermissionAsync(utilisateurId, DroitCredit, ct);

        var texte = string.IsNullOrWhiteSpace(saisie.Texte) ? null : saisie.Texte.Trim();
        if (texte is { Length: > LongueurTexteMax }) texte = texte[..LongueurTexteMax];
        var (organismeId, sansOrganisme) = saisie.Organisme?.Trim() switch
        {
            "aucun" => ((long?)null, true),
            { } o when long.TryParse(o, out var id) && id > 0 => (id, false),
            _ => (null, false)
        };
        var etat = saisie.Droits is { } d && EtatsDroits.Contains(d) ? d : null;
        var jours = Math.Max(0, (await caisse.ParametresAsync(ct)).JoursAlerteFinDroit);

        var filtre = new FiltreFichierPatients(texte, organismeId, sansOrganisme, saisie.Chronique, voitCredit ? saisie.Credit : null,
            etat, saisie.Sms, horloge.Aujourdhui, jours);
        var resultat = await fichier.PageAsync(filtre, page, ct);
        return resultat.Vers(l => l with
        {
            SoldeCredit = voitCredit ? l.SoldeCredit : null,
            EtatDroits = l.Organisme is "CNAS" or "CASNOS" && l.DateFinDroit is { } fin
                ? DroitsChifa.EtatCreditSansTiersPayant(fin, horloge.Aujourdhui, jours) : null
        });
    }
}
