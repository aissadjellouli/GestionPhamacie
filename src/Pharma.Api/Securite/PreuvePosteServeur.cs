using System.Collections.Concurrent;
using Pharma.Application.Commun;
using Pharma.Application.Notifications;
using Pharma.Application.Postes;
using Pharma.Application.SecondServeur;
using Pharma.Shared;

namespace Pharma.Api.Securite;

// ---- Liste des postes autorisés : vérification des preuves et enrôlement (contrat-api.md § 1 à § 4), avec le code commun Pharma.Shared.PreuvePoste ----

/// <summary>En-têtes de preuve reçus (bruts).</summary>
public sealed record EnTetesPreuve(string? Id, string? Horodatage, string? Nonce, string? Preuve)
{
    public bool Presents => Id is not null || Horodatage is not null || Nonce is not null || Preuve is not null;
}

public enum IssuePreuve
{
    /// <summary>Aucun en-tête de preuve.</summary>
    Absente,
    Valide,
    /// <summary>Horodatage hors de la fenêtre de 120 s : le poste corrige son horloge et réessaie.</summary>
    Horloge,
    /// <summary>Poste révoqué, ou remplacé (sa clé a été retirée).</summary>
    Revoque,
    /// <summary>Signature fausse, nonce rejoué, poste inconnu ou en attente, en-têtes mal formés.</summary>
    NonProuve,
    /// <summary>Vérification impossible (cache des nonces plein, base illisible).</summary>
    Indisponible
}

public sealed record ResultatPreuve(IssuePreuve Issue, PosteProuve? Poste = null, long? PosteId = null)
{
    public static readonly ResultatPreuve Absente = new(IssuePreuve.Absente);

    public string? Code => Issue switch
    {
        IssuePreuve.Horloge => PreuvePoste.PreuveHorloge,
        IssuePreuve.Revoque => PreuvePoste.PosteRevoque,
        IssuePreuve.NonProuve => PreuvePoste.PosteNonProuve,
        IssuePreuve.Indisponible => PreuvePoste.PreuveIndisponible,
        _ => null
    };
}

/// <summary>
/// Vérification de la preuve d'une requête (contrat § 3) : horloge (± 120 s), nonce jamais vu depuis 5 min (mémoire de CE serveur, bornée), poste actif,
/// signature ECDSA de la chaîne canonique. Singleton. Le nonce n'est retenu qu'après une signature valide (un tiers ne peut pas remplir le cache).
/// </summary>
public sealed class VerificateurPreuvePoste(RegistrePostesAutorises registre, IHorloge horloge)
{
    public static readonly TimeSpan GardeNonces = TimeSpan.FromMinutes(5);
    public const int NoncesMax = 200_000;

    private readonly ConcurrentDictionary<string, DateTime> nonces = new(StringComparer.Ordinal);
    private DateTime prochainePurge = DateTime.MinValue;
    private readonly object verrouPurge = new();

    public async Task<ResultatPreuve> VerifierAsync(EnTetesPreuve e, string methode, string cible, string? authorization, CancellationToken ct = default)
    {
        if (!e.Presents) return ResultatPreuve.Absente;
        if (!long.TryParse(e.Id, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var id) || id <= 0
            || !long.TryParse(e.Horodatage, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var horodatage)
            || e.Nonce is not { Length: >= 16 and <= 64 } nonce || PreuvePoste.DepuisBase64Url(nonce) is not { Length: PreuvePoste.OctetsNonce }
            || string.IsNullOrEmpty(e.Preuve))
            return new(IssuePreuve.NonProuve);

        var maintenant = horloge.MaintenantUtc;
        var ecart = Math.Abs(maintenant.Subtract(DateTime.UnixEpoch).TotalSeconds - horodatage);
        if (ecart > PreuvePoste.FenetreHorlogeSecondes) return new(IssuePreuve.Horloge, PosteId: id);

        PosteCle? cle;
        try { cle = await registre.CleAsync(id, ct); }
        catch (Exception ex) when (ex is not OperationCanceledException) { return new(IssuePreuve.Indisponible, PosteId: id); }
        if (cle is null) return new(IssuePreuve.NonProuve, PosteId: id);
        // Révoqué, ou remplacé (de nouveau en attente après « Remplacer le PC ») : l'ancien PC reçoit « révoqué », message clair
        if (cle.Statut == "revoque" || (cle.Statut == "en_attente" && cle.Generation > 1)) return new(IssuePreuve.Revoque, PosteId: id);
        if (cle.Statut != "actif" || cle.ClePublique is null) return new(IssuePreuve.NonProuve, PosteId: id);

        var chaine = PreuvePoste.Chaine(methode, cible, horodatage, nonce, id, authorization);
        if (!PreuvePoste.Verifier(cle.ClePublique, chaine, e.Preuve)) return new(IssuePreuve.NonProuve, PosteId: id);

        Purger(maintenant);
        if (nonces.Count >= NoncesMax) return new(IssuePreuve.Indisponible, PosteId: id);
        if (!nonces.TryAdd(id + ":" + nonce, maintenant + GardeNonces)) return new(IssuePreuve.NonProuve, PosteId: id);
        return new(IssuePreuve.Valide, new PosteProuve(cle.Id, cle.Nom), id);
    }

    private void Purger(DateTime maintenant)
    {
        if (maintenant < prochainePurge) return;
        lock (verrouPurge)
        {
            if (maintenant < prochainePurge) return;
            prochainePurge = maintenant + TimeSpan.FromSeconds(30);
            foreach (var kv in nonces)
                if (kv.Value <= maintenant) nonces.TryRemove(kv.Key, out _);
        }
    }
}


/// <summary>
/// Enrôlement par le poste (route anonyme, limite de débit des connexions) : code à usage unique, clé publique P-256, preuve de possession signée par la
/// nouvelle clé. Toute erreur de code donne la même réponse (code_invalide). Cinq échecs : tous les codes en attente sont invalidés, alerte au titulaire.
/// </summary>
public sealed class EnrolerPoste(IPostesAutorisesDepot depot, RegistrePostesAutorises registre, IJournal journal, IAlertesTitulaire alertes,
    AlertesPostes limite, IHorloge horloge, ITraceErreurs? trace = null)
{
    public async Task<EnrolementPosteReponse> ExecuterAsync(EnrolementPosteRequete? r, CancellationToken ct = default)
    {
        var code = PreuvePoste.NormaliserCode(r?.Code);
        if (r is null || code.Length != CodeEnrolement.Longueur || code.Any(c => !CodeEnrolement.Alphabet.Contains(c)))
            throw CodeFaux();
        byte[] spki;
        try { spki = r.ClePublique is { Length: > 0 and <= 512 } c ? Convert.FromBase64String(c) : throw CleFausse(); }
        catch (FormatException) { throw CleFausse(); }
        using (var cle = PreuvePoste.ClePublique(spki))
            if (cle is null) throw CleFausse();
        var maintenant = horloge.MaintenantUtc.Subtract(DateTime.UnixEpoch).TotalSeconds;
        if (Math.Abs(maintenant - r.Horodatage) > PreuvePoste.FenetreHorlogeSecondes) throw CleFausse("Heure du poste trop éloignée de celle du serveur : réglez l'horloge du PC.");
        if (!PreuvePoste.Verifier(spki, PreuvePoste.ChaineEnrolement(code, r.Horodatage, spki), r.Preuve)) throw CleFausse();
        var ordinateur = NomPoste.Clair(r.Ordinateur);

        var lu = await depot.EnrolerAsync(CodeEnrolement.Hacher(code), spki, PreuvePoste.EmpreinteCle(spki), ordinateur, CodeEnrolement.EssaisMax, ct);
        if (lu is null)
        {
            var essais = await depot.NoterEchecCodeAsync(ct);
            if (essais >= CodeEnrolement.EssaisMax && limite.Autorisee("codes_faux"))
                await AlerterAsync("Postes : codes d'autorisation faux répétés",
                    $"{CodeEnrolement.EssaisMax} codes d'autorisation de poste faux ont été saisis : les codes en attente sont invalidés. Donnez un nouveau code au poste à autoriser (Administration › Postes).", ct);
            throw CodeFaux();
        }
        registre.Invalider(lu.PosteId);
        // Inscrit au nom du titulaire qui a autorisé le poste (l'enrôlement se fait sans employé connecté)
        if (lu.CreePar is { } par)
            await journal.EcrireAsync(par, GererPostesAutorises.ActionEnrole, "poste_autorise", lu.PosteId,
                new Dictionary<string, object?> { ["nom"] = lu.Nom, ["empreinte"] = PreuvePoste.EmpreinteCle(spki), ["ordinateur"] = ordinateur }, null, ct);
        return new EnrolementPosteReponse(lu.PosteId, lu.Nom);
    }

    private async Task AlerterAsync(string objet, string texte, CancellationToken ct)
    {
        try { await alertes.SecuriteAsync(objet + " : " + texte, true, ct); }
        catch (Exception e) when (e is not OperationCanceledException) { trace?.Erreur("Postes autorisés : alerte non envoyée", e); }
    }

    private static RefusPosteException CodeFaux() =>
        new(PreuvePoste.CodeInvalide, 400, "Code faux, expiré ou déjà utilisé : demandez un nouveau code au titulaire (Administration › Postes).");

    private static RefusPosteException CleFausse(string? message = null) =>
        new(PreuvePoste.CleInvalide, 400, message ?? "Clé du poste refusée : relancez l'autorisation de ce poste.");
}
