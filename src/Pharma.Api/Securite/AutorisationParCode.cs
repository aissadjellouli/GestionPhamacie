using Pharma.Application.Caisse;
using Pharma.Application.Commun;
using Pharma.Application.Securite;
using Pharma.Domain.Commun;
using Pharma.Infrastructure.Securite;

namespace Pharma.Api.Securite;

/// <summary>
/// Autorisation sur place : une action refusée faute de droit peut être rejouée avec le code comptoir d'un employé qui a ce droit
/// (en-têtes X-Pharma-Autorise-Par et X-Pharma-Autorise-Code). Le code est vérifié une seule fois par requête (essais comptés,
/// blocage après plusieurs erreurs) ; l'action est inscrite au journal avec « autorisé par ». Désactivable par le titulaire.
/// Seuls les droits nommés dans X-Pharma-Autorise-Droits (ceux que le valideur a acceptés au poste) sont accordés : un code saisi
/// pour vendre sans ordonnance n'autorise pas, dans la même opération, un dépassement du plafond de crédit.
/// </summary>
public sealed class AutorisationParCode(IHttpContextAccessor http, VerificationCodeComptoir codes, ISessionsCaisse sessions,
    PermissionService droits, AutorisationCourante courante, IJournal journal)
{
    public const string EnTeteIdentifiant = "X-Pharma-Autorise-Par";
    public const string EnTeteCode = "X-Pharma-Autorise-Code";
    public const string EnTeteDroits = "X-Pharma-Autorise-Droits";

    private bool verifie;
    private readonly HashSet<string> journalises = [];
    private CompteComptoir? valideur;

    /// <summary>Message du dernier échec (code faux, code bloqué, autorisation désactivée), à montrer au poste.</summary>
    public string? Erreur { get; private set; }

    /// <summary>Vrai si la requête porte une demande d'autorisation (en-têtes présents).</summary>
    public bool Demandee => http.HttpContext?.Request.Headers.ContainsKey(EnTeteIdentifiant) == true;

    public async Task<bool> ActiveeAsync(CancellationToken ct) => (await sessions.ParametresAsync(ct)).AutorisationParCode;

    private async Task<CompteComptoir?> ValideurAsync(CancellationToken ct)
    {
        if (verifie) return valideur;
        verifie = true;
        var requete = http.HttpContext?.Request;
        if (requete is null || !Demandee) return null;
        if (!await ActiveeAsync(ct)) { Erreur = "L’autorisation par code est désactivée (Administration › Caisse)."; return null; }
        var identifiant = Uri.UnescapeDataString(requete.Headers[EnTeteIdentifiant].ToString());
        var code = requete.Headers[EnTeteCode].ToString();
        try { valideur = await codes.VerifierAsync(identifiant, code, requete.Poste(), ct); }
        catch (RegleMetierException e) { Erreur = e.Message; }
        return valideur;
    }

    private HashSet<string> DroitsDemandes() =>
        (http.HttpContext?.Request.Headers[EnTeteDroits].ToString() ?? "")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToHashSet(StringComparer.Ordinal);

    /// <summary>Le valideur de la requête a-t-il ce droit ? (null : marque du titulaire)</summary>
    /// <param name="remisePct">Remise à valider : le maximum de remise du valideur doit la couvrir.</param>
    public async Task<bool> AutoriseAsync(long utilisateurId, string? permission, CancellationToken ct, decimal? remisePct = null)
    {
        var droit = permission ?? PermissionServiceExtensions.MarqueTitulaire;
        // Droit non demandé au valideur : refusé (le poste le lui présentera s'il le faut)
        if (!DroitsDemandes().Contains(droit)) return false;
        if (await ValideurAsync(ct) is not { } v || v.Id == utilisateurId) return false;
        var ok = permission is null || permission == PermissionServiceExtensions.MarqueTitulaire
            ? await droits.EstTitulaireAsync(v.Id, ct)
            : await droits.APermissionAsync(v.Id, permission, ct);
        if (!ok) { Erreur ??= $"{v.NomComplet} n’a pas ce droit : choisissez un employé qui l’a."; return false; }
        if (remisePct is { } pct && await droits.RemiseMaxAsync(v.Id, ct) is var max && max < pct)
        {
            Erreur ??= $"{v.NomComplet} peut accorder jusqu’à {max:0.##} % : pour {pct:0.##} %, choisissez un employé dont le maximum le permet.";
            return false;
        }
        (courante.AutoriseParId, courante.AutoriseParNom) = (v.Id, v.NomComplet);
        if (journalises.Add(droit))
        {
            await journal.EcrireAsync(utilisateurId, "autorisation_code", null, null,
                new Dictionary<string, object?> { ["droit"] = permission ?? "titulaire", ["action"] = http.HttpContext?.Request.Path.Value, ["autorise_par"] = v.NomComplet },
                permission, ct);
        }
        return true;
    }
}
