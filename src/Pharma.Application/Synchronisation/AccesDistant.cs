using Pharma.Application.Commun;
using Pharma.Application.Securite;
using Pharma.Domain.Commun;

namespace Pharma.Application.Synchronisation;

/// <summary>
/// Lot S7.2b (docs/ecrans-central.md § 3 ter) : Administration › Synchronisation › Accès à distance. Le titulaire tire lui-même le
/// code de rattachement de son officine à un compte distant (écrans du serveur central, « Rattacher une officine »). Titulaire
/// seulement, mot de passe du titulaire ressaisi (même vérification que « Repartir sur une nouvelle époque » : essais comptés,
/// blocage, refus journalisés). Refus clair si la synchronisation n'est pas activée. L'officine est celle du certificat de ce
/// serveur (jamais choisie). Le code est rendu une fois, jamais gardé ni journalisé ; le journal des actions note la délivrance
/// (échéance seulement, ni le code ni l'identifiant du compte distant).
/// </summary>
public sealed class GenererCodeAccesDistant(IEnvoiSyncDepot depot, ICertificatSync certificat, IPermissionService droits,
    VerificationMotDePasseActuel motDePasse, IClientServeurCentral client, IJournal journal)
{
    public const string ChampCompte = "compte";
    public const string ChampMotDePasse = "mot_de_passe";
    public const string RefusNonActivee =
        "Synchronisation non activée : l’accès à distance passe par le serveur central. Activez d’abord la synchronisation (sous-rubriques Réglages et Activation).";
    public const string RefusLectureSeule = "Base en lecture seule (2e serveur en veille) : demandez le code depuis le serveur principal.";
    public const string RefusCompte = "Identifiant du compte distant attendu : 3 à 60 caractères, minuscules, chiffres, « . _ - » (ex. titulaire.dupont).";
    public const string RefusNeutre =
        "Code non délivré : vérifiez l’identifiant du compte distant (compte titulaire actif des écrans du serveur central).";
    public const string RefusLimite = "Trop de codes demandés pour cette officine (5 par heure au plus) : réessayez plus tard.";
    public const string RefusInjoignable = "Serveur central injoignable : aucun code n’a été délivré. Réessayez dans un instant.";

    /// <summary>Synchronisation utilisable pour l'accès à distance : activée, adresse réglée, serveur activé (certificat non échu).</summary>
    public static bool Activee(ParametresEnvoiSync p, CertificatSyncInfo? c, DateTime maintenantUtc) =>
        p.Active && !string.IsNullOrWhiteSpace(p.Adresse) && p.InscriteLe is not null && p.OfficineId is not null
        && c is not null && c.EcheanceUtc > maintenantUtc;

    /// <summary>Identifiant du compte distant ramené en minuscules sans espaces autour, ou null s'il est mal formé ; même contrôle que
    /// ComptesDistants.IdentifiantValide (Pharma.Shared, test d'égalité) et que la base centrale.</summary>
    public static string? NormaliserCompte(string? compte)
    {
        var c = compte?.Trim().ToLowerInvariant();
        return c is not null && System.Text.RegularExpressions.Regex.IsMatch(c, "^[a-z0-9][a-z0-9._-]{2,59}$") ? c : null;
    }

    public async Task<CodeRattachementSync> ExecuterAsync(string? compte, string? motDePasseTitulaire, long titulaireId, CancellationToken ct = default)
    {
        await droits.ExigerTitulaireAsync(titulaireId, "générer un code d’accès à distance", ct);
        var p = await depot.ParametresAsync(ct);
        if (p.BaseEnLectureSeule) throw new RegleMetierException(RefusLectureSeule);
        CertificatSyncInfo? c;
        try { c = certificat.Lire(); }
        catch (Exception e) when (e is InvalidOperationException or PlatformNotSupportedException or System.Security.Cryptography.CryptographicException) { c = null; }
        if (!Activee(p, c, DateTime.UtcNow)) throw new RegleMetierException(RefusNonActivee);
        var identifiant = NormaliserCompte(compte) ?? throw new RegleMetierException(RefusCompte, ChampCompte);
        await motDePasse.VerifierAsync(titulaireId, motDePasseTitulaire, "code_acces_distant", ChampMotDePasse, ct);

        CodeRattachementSync r;
        try { r = await client.CodeRattachementAsync(p.Adresse!, identifiant, ct); }
        catch (ServeurCentralException e) when (e.CodeHttp == 429) { throw new RegleMetierException(RefusLimite); }
        catch (ServeurCentralException e) when (e.CodeHttp == 422) { throw new RegleMetierException(RefusNeutre, ChampCompte); }
        catch (Exception e) when (e is ServeurCentralException or HttpRequestException || (e is TaskCanceledException && !ct.IsCancellationRequested))
        {
            throw new RegleMetierException(RefusInjoignable);
        }
        await journal.EcrireAsync(titulaireId, "code_acces_distant", null, null, new Dictionary<string, object?>
        {
            ["echeance"] = r.ExpireLeUtc.ToString("yyyy-MM-dd HH:mm:ss'Z'"),
            ["validation"] = "mot_de_passe"
        }, null, ct);
        return r;
    }
}
