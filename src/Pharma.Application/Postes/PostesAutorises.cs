using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Pharma.Application.Commun;
using Pharma.Application.Notifications;
using Pharma.Application.SecondServeur;
using Pharma.Application.Securite;

namespace Pharma.Application.Postes;

/// <summary>Modes et codes de refus de la liste des postes (mêmes valeurs que Pharma.Shared.PreuvePoste : test de parité).</summary>
public static class ConstantesPostes
{
    public const string ModeInactif = "inactif";
    public const string ModeTransition = "transition";
    public const string ModeExige = "exige";
    public const string NomPostePris = "nom_poste_pris";
    public const string PosteOccupe = "poste_occupe";

    /// <summary>Code d'enrôlement normalisé : majuscules, sans tiret ni espace (même règle que ConstantesPostes.NormaliserCode).</summary>
    public static string NormaliserCode(string? code) =>
        new((code ?? "").Where(c => !char.IsWhiteSpace(c) && c != '-').Select(char.ToUpperInvariant).ToArray());
}

/// <summary>Nom de poste vu à une connexion qui n'est pas un poste enrôlé.</summary>
public sealed record NomSansPreuve(string Nom, DateTime VuLe);

/// <summary>Code d'enrôlement créé (affiché une seule fois).</summary>
public sealed record CodeEnrolementCree(long PosteId, string Nom, string Code, DateTime ExpireLe);

// ---- Liste des postes autorisés (docs/taches/2026-11-24-postes-autorises/plan.md et contrat-api.md) ---------------------------------

/// <summary>Poste prouvé par sa signature : identifiant et nom actuel (table poste_autorise).</summary>
public sealed record PosteProuve(long Id, string Nom);

/// <summary>Clé d'un poste pour la vérification des preuves.</summary>
public sealed record PosteCle(long Id, string Nom, string Statut, byte[]? ClePublique, int Generation = 1);

/// <summary>
/// Réglage de la liste (table parametre_postes). Mode : plus lu depuis le script 174 (décision du titulaire du 2026-11-27 : preuve toujours
/// exigée, voir <see cref="RegistrePostesAutorises.ModeAsync"/>) ; gardé dans l'enregistrement pour la compatibilité des DTO. Le nombre maximum de postes n'est plus réglé ici : il vient de l'offre signée par
/// l'éditeur (Administration › Mon offre, script 172 ; décision du titulaire du 2026-11-26), sans possibilité de le modifier dans la base.
/// </summary>
public sealed record ReglagesPostes(string Mode, int DureeCodeMinutes)
{
    public const int DureeMin = 5, DureeMax = 60;
    public static readonly ReglagesPostes ParDefaut = new(ConstantesPostes.ModeExige, 15);
}

/// <summary>Poste lu pour l'écran du titulaire.</summary>
public sealed record PosteAutoriseLu(long Id, string Nom, string Statut, int Generation, DateTime CreeLe, DateTime? EnroleLe, DateTime? DernierePreuveLe,
    string? Ordinateur, string? EmpreinteCle, DateTime? CodeExpireLe, DateTime? RevoqueLe, string? MotifRevocation, long? CreePar);

/// <summary>Issue d'un enrôlement en base.</summary>
public sealed record EnrolementLu(long PosteId, string Nom, long? CreePar);

/// <summary>Refus d'une opération sur les postes : rendu en ProblemDetails (type urn:pharmaofficine:erreur:&lt;code&gt;).</summary>
public sealed class RefusPosteException(string code, int statut, string message) : Exception(message)
{
    public string Code { get; } = code;
    public int Statut { get; } = statut;
}

/// <summary>Résultat de la création d'un poste : son identifiant, ou rien si le nom est pris ou le maximum atteint.</summary>
public sealed record CreationPoste(long? Id, bool MaximumAtteint = false);

/// <summary>Accès aux tables poste_autorise et parametre_postes (script 167).</summary>
public interface IPostesAutorisesDepot
{
    Task<ReglagesPostes> ReglagesAsync(CancellationToken ct = default);
    Task EnregistrerReglagesAsync(ReglagesPostes reglages, CancellationToken ct = default);
    Task<PosteCle?> LireCleAsync(long id, CancellationToken ct = default);
    /// <summary>Noms (en minuscules) des postes non révoqués : noms réservés (D6).</summary>
    Task<IReadOnlySet<string>> NomsReservesAsync(CancellationToken ct = default);
    Task<Page<PosteAutoriseLu>> ListerAsync(string? statut, string? recherche, DemandePage page, CancellationToken ct = default);
    Task<PosteAutoriseLu?> LireAsync(long id, CancellationToken ct = default);
    Task<int> NombreNonRevoquesAsync(CancellationToken ct = default);
    /// <summary>Crée un poste en attente, sous verrou : compte des postes non révoqués et création d'un seul tenant, deux autorisations
    /// simultanées ne dépassent pas le maximum. Id null : nom déjà pris par un poste non révoqué, ou maximum atteint.</summary>
    Task<CreationPoste> CreerAsync(string nom, byte[] codeHache, DateTime expireLe, long creePar, int maximum, CancellationToken ct = default);
    /// <summary>Nouveau code d'un poste encore en attente (compteur d'échecs remis à zéro) ; faux si le poste n'est pas en attente.</summary>
    Task<bool> NouveauCodeAsync(long id, byte[] codeHache, DateTime expireLe, CancellationToken ct = default);
    /// <summary>Remplacement du PC d'un poste actif : clé retirée, génération + 1, retour en attente avec un nouveau code ; faux si non actif.</summary>
    Task<bool> RemplacerAsync(long id, byte[] codeHache, DateTime expireLe, CancellationToken ct = default);
    /// <summary>Renomme un poste non révoqué ; null si le poste n'existe pas ou est révoqué, faux si le nom est pris.</summary>
    Task<bool?> RenommerAsync(long id, string nom, CancellationToken ct = default);
    Task<bool> RevoquerAsync(long id, long revoquePar, string motif, CancellationToken ct = default);
    /// <summary>Enrôle le poste en attente dont le code a ce hachage (non expiré, moins de 5 échecs) ; null sinon.</summary>
    Task<EnrolementLu?> EnrolerAsync(byte[] codeHache, byte[] clePublique, string empreinte, string? ordinateur, int essaisMax, CancellationToken ct = default);
    /// <summary>Un code faux : un échec de plus sur tous les codes en attente. Rend le plus grand nombre d'échecs atteint.</summary>
    Task<int> NoterEchecCodeAsync(CancellationToken ct = default);
    /// <summary>Caisse ouverte, ventes ou réceptions en attente sous ce nom de poste.</summary>
    Task<bool> PosteOccupeAsync(string nom, CancellationToken ct = default);
    /// <summary>Noms vus à une connexion depuis 30 jours qui ne sont pas des postes non révoqués.</summary>
    Task<IReadOnlyList<NomSansPreuve>> NomsSansPreuveAsync(CancellationToken ct = default);
    Task NoterPreuveAsync(long id, CancellationToken ct = default);
}

/// <summary>Code d'enrôlement : 8 caractères sans caractères ambigus (affichés XXXX-XXXX) ; seul son hachage est gardé.</summary>
public static class CodeEnrolement
{
    public const string Alphabet = "23456789ABCDEFGHJKMNPQRSTUVWXYZ";
    public const int Longueur = 8;
    public const int EssaisMax = 5;

    public static string Nouveau()
    {
        var c = new char[Longueur];
        for (var i = 0; i < Longueur; i++) c[i] = Alphabet[RandomNumberGenerator.GetInt32(Alphabet.Length)];
        return new string(c, 0, 4) + "-" + new string(c, 4, 4);
    }

    public static byte[] Hacher(string code) => SHA256.HashData(Encoding.UTF8.GetBytes(ConstantesPostes.NormaliserCode(code)));
}

/// <summary>
/// Caches de la liste des postes (singleton) : réglage (durée du code), clés publiques et noms réservés, relus au plus toutes les 30 s ; vidés sur le
/// serveur qui modifie la liste. Réglage illisible (base arrêtée) : valeurs par défaut, noté au journal une fois par minute. Le mode n'est plus lu :
/// preuve toujours exigée (décision du titulaire du 2026-11-27, script 174), même base illisible (les clés illisibles rendent alors « vérification
/// indisponible », jamais un poste accepté sans preuve).
/// </summary>
public sealed class RegistrePostesAutorises(IPostesAutorisesDepot depot, IHorloge horloge, ITraceErreurs? trace = null)
{
    public static readonly TimeSpan DureeCache = TimeSpan.FromSeconds(30);
    private const int ClesMax = 1000;

    private (ReglagesPostes Valeur, DateTime Lu)? reglages;
    private (IReadOnlySet<string> Valeur, DateTime Lu)? reserves;
    private readonly ConcurrentDictionary<long, (PosteCle? Cle, DateTime Lu)> cles = new();
    private DateTime derniereErreur = DateTime.MinValue;

    public async Task<ReglagesPostes> ReglagesAsync(CancellationToken ct = default)
    {
        var maintenant = horloge.MaintenantUtc;
        if (reglages is { } r && maintenant - r.Lu < DureeCache && maintenant >= r.Lu) return r.Valeur;
        try
        {
            var lu = await depot.ReglagesAsync(ct);
            reglages = (lu, maintenant);
            return lu;
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            Noter("réglage des postes illisible (script 167 passé ?) : durée du code par défaut, preuve toujours exigée", e);
            reglages = (ReglagesPostes.ParDefaut, maintenant);
            return ReglagesPostes.ParDefaut;
        }
    }

    /// <summary>
    /// Mode de la liste : toujours « exige » (décision du titulaire du 2026-11-27 : chaque poste doit être autorisé avec son code, sinon pas de
    /// connexion). La colonne parametre_postes.mode n'est plus lue (script 174) : une valeur changée dans la base n'ouvre rien.
    /// </summary>
    public Task<string> ModeAsync(CancellationToken ct = default) => Task.FromResult(ConstantesPostes.ModeExige);

    /// <summary>Clé du poste (null : inconnu). Exception si la base est illisible.</summary>
    public async Task<PosteCle?> CleAsync(long id, CancellationToken ct = default)
    {
        var maintenant = horloge.MaintenantUtc;
        if (cles.TryGetValue(id, out var c) && maintenant - c.Lu < DureeCache && maintenant >= c.Lu) return c.Cle;
        var lu = await depot.LireCleAsync(id, ct);
        if (cles.Count >= ClesMax) cles.Clear();
        cles[id] = (lu, maintenant);
        return lu;
    }

    /// <summary>Le nom (sans la casse) est-il celui d'un poste non révoqué ? Base illisible : faux.</summary>
    public async Task<bool> NomReserveAsync(string nom, CancellationToken ct = default)
    {
        var maintenant = horloge.MaintenantUtc;
        if (reserves is not { } r || maintenant - r.Lu >= DureeCache || maintenant < r.Lu)
        {
            try { reserves = r = (await depot.NomsReservesAsync(ct), maintenant); }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                Noter("noms des postes autorisés illisibles", e);
                return false;
            }
        }
        return r.Valeur.Contains(nom.Trim().ToLowerInvariant());
    }

    /// <summary>Liste modifiée sur ce serveur : relecture immédiate.</summary>
    public void Invalider(long? id = null)
    {
        reglages = null;
        reserves = null;
        if (id is { } i) cles.TryRemove(i, out _);
        else cles.Clear();
    }

    private void Noter(string message, Exception e)
    {
        var maintenant = horloge.MaintenantUtc;
        if (maintenant - derniereErreur < TimeSpan.FromMinutes(1)) return;
        derniereErreur = maintenant;
        trace?.Erreur("Postes autorisés : " + message, e);
    }
}

/// <summary>
/// Dernière preuve d'un poste : écrite au plus une fois par minute et par poste, en tâche détachée, jamais sur un serveur en veille ni écarté (lecture seule).
/// Jamais bloquant ; une erreur est notée sans faire échouer la requête.
/// </summary>
public sealed class DernieresPreuvesPostes(IPostesAutorisesDepot depot, IVeilleServeur veille, IServeurEcarteDetecte ecart, IHorloge horloge, ITraceErreurs? trace = null)
{
    private readonly ConcurrentDictionary<long, DateTime> ecrites = new();

    public void Noter(long posteId)
    {
        if (veille.EstVeille || ecart.Ecart is not null) return;
        var maintenant = horloge.MaintenantUtc;
        if (ecrites.TryGetValue(posteId, out var d) && maintenant - d < TimeSpan.FromMinutes(1) && maintenant >= d) return;
        if (ecrites.Count > 1000) ecrites.Clear();
        ecrites[posteId] = maintenant;
        _ = Task.Run(async () =>
        {
            try
            {
                using var delai = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                await depot.NoterPreuveAsync(posteId, delai.Token);
            }
            catch (Exception e) { trace?.Erreur("Postes autorisés : dernière preuve non écrite", e); }
        });
    }
}

/// <summary>
/// Alertes du titulaire sur les postes (au plus une par heure et par sujet) : poste révoqué qui se présente, codes d'enrôlement faux répétés.
/// Jamais bloquant.
/// </summary>
public sealed class AlertesPostes(IHorloge horloge)
{
    private readonly ConcurrentDictionary<string, DateTime> dernieres = new(StringComparer.Ordinal);

    public bool Autorisee(string sujet)
    {
        var maintenant = horloge.MaintenantUtc;
        if (dernieres.TryGetValue(sujet, out var d) && maintenant - d < TimeSpan.FromHours(1) && maintenant >= d) return false;
        if (dernieres.Count > 1000) dernieres.Clear();
        dernieres[sujet] = maintenant;
        return true;
    }
}

/// <summary>Gestion de la liste par le titulaire (Administration › Postes) : chaque action est inscrite au journal, sans donnée personnelle.</summary>
public sealed class GererPostesAutorises(IPostesAutorisesDepot depot, RegistrePostesAutorises registre, IPermissionService droits, IJournal journal, IHorloge horloge,
    Offre.ILimitesOffre limites)
{
    public const string ActionAutorise = "poste_autorise", ActionRenomme = "poste_renomme", ActionCode = "poste_code_renouvele",
        ActionRemplace = "poste_remplace", ActionRevoque = "poste_revoque", ActionReglages = "postes_reglages", ActionEnrole = "poste_enrole",
        ActionCodeEchoue = "poste_code_echoue";
    public const int MotifMin = 5, MotifMax = 200;
    private const string Table = "poste_autorise";

    public async Task<Page<PosteAutoriseLu>> ListerAsync(long utilisateurId, string? statut, string? recherche, DemandePage page, CancellationToken ct = default)
    {
        await droits.ExigerTitulaireAsync(utilisateurId, "gérer les postes autorisés", ct);
        var s = statut is "en_attente" or "actif" or "revoque" ? statut : null;
        return await depot.ListerAsync(s, string.IsNullOrWhiteSpace(recherche) ? null : recherche.Trim(), page, ct);
    }

    /// <returns>Réglage (mode toujours « exige »), noms vus sans preuve, maximum de postes de l'offre en vigueur (lecture seule) et postes déjà comptés.</returns>
    public async Task<(ReglagesPostes Reglages, IReadOnlyList<NomSansPreuve> NomsSansPreuve, int Maximum, int Comptes)> ReglagesAsync(long utilisateurId,
        CancellationToken ct = default)
    {
        await droits.ExigerTitulaireAsync(utilisateurId, "gérer les postes autorisés", ct);
        return ((await depot.ReglagesAsync(ct)) with { Mode = ConstantesPostes.ModeExige }, await depot.NomsSansPreuveAsync(ct), await limites.MaximumPostesAsync(ct), await depot.NombreNonRevoquesAsync(ct));
    }

    /// <summary>Postes non révoqués (comptés dans le maximum).</summary>
    public Task<int> NombreComptesAsync(CancellationToken ct = default) => depot.NombreNonRevoquesAsync(ct);

    /// <summary>
    /// Enregistre la durée du code. Le mode demandé est ignoré (preuve toujours exigée, décision du titulaire du 2026-11-27) : « exige » est
    /// toujours enregistré. Rend toujours null (plus d'avertissement : le maximum vient de l'offre).
    /// </summary>
    public async Task<string?> ModifierReglagesAsync(long utilisateurId, ReglagesPostes demande, CancellationToken ct = default)
    {
        await droits.ExigerTitulaireAsync(utilisateurId, "régler la liste des postes autorisés", ct);
        demande = demande with { Mode = ConstantesPostes.ModeExige };
        if (demande.DureeCodeMinutes is < ReglagesPostes.DureeMin or > ReglagesPostes.DureeMax)
            throw new Domain.Commun.RegleMetierException($"Durée du code : de {ReglagesPostes.DureeMin} à {ReglagesPostes.DureeMax} minutes.", "dureeCodeMinutes");
        var avant = await depot.ReglagesAsync(ct);
        if (avant == demande) return null;
        await depot.EnregistrerReglagesAsync(demande, ct);
        registre.Invalider();
        await journal.EcrireAsync(utilisateurId, ActionReglages, "parametre_postes", 1, new Dictionary<string, object?>
        {
            ["mode_avant"] = avant.Mode, ["mode_apres"] = demande.Mode,
            ["duree_code_avant"] = avant.DureeCodeMinutes, ["duree_code_apres"] = demande.DureeCodeMinutes
        }, null, ct);
        return null;
    }

    public static string AvertissementMaximum(int comptes, int maximum) =>
        $"{comptes} postes sont déjà comptés pour un maximum de {maximum} : aucun n'est retiré, mais aucun nouveau ne pourra être autorisé.";

    public static string MessageMaximumAtteint(int maximum) =>
        $"Nombre maximum de postes atteint ({maximum}, fixé par votre offre) : révoquez un poste ou demandez des postes supplémentaires à l’éditeur (Administration › Mon offre).";

    public async Task<CodeEnrolementCree> AutoriserAsync(long utilisateurId, string? nom, CancellationToken ct = default, bool verifierTitulaire = true)
    {
        if (verifierTitulaire) await droits.ExigerTitulaireAsync(utilisateurId, "autoriser un poste", ct);
        var n = NomValide(nom);
        var reglages = await depot.ReglagesAsync(ct);
        var maximum = await limites.MaximumPostesAsync(ct);   // offre signée par l'éditeur (script 172)
        var code = CodeEnrolement.Nouveau();
        var expire = horloge.MaintenantUtc.AddMinutes(reglages.DureeCodeMinutes);
        var cree = await depot.CreerAsync(n, CodeEnrolement.Hacher(code), expire, utilisateurId, maximum, ct);
        if (cree.MaximumAtteint) throw new Domain.Commun.RegleMetierException(MessageMaximumAtteint(maximum), "nom");
        var id = cree.Id ?? throw NomPris(n);
        registre.Invalider(id);
        await journal.EcrireAsync(utilisateurId, ActionAutorise, Table, id, new Dictionary<string, object?> { ["nom"] = n }, null, ct);
        return new CodeEnrolementCree(id, n, code, expire);
    }

    public async Task<CodeEnrolementCree> NouveauCodeAsync(long utilisateurId, long id, CancellationToken ct = default)
    {
        await droits.ExigerTitulaireAsync(utilisateurId, "donner un nouveau code à un poste", ct);
        var poste = await depot.LireAsync(id, ct) ?? throw Introuvable();
        if (poste.Statut != "en_attente")
            throw new Domain.Commun.RegleMetierException("Ce poste n'attend pas de code : pour changer de PC, utilisez « Remplacer le PC ».");
        var reglages = await depot.ReglagesAsync(ct);
        var code = CodeEnrolement.Nouveau();
        var expire = horloge.MaintenantUtc.AddMinutes(reglages.DureeCodeMinutes);
        if (!await depot.NouveauCodeAsync(id, CodeEnrolement.Hacher(code), expire, ct))
            throw new Domain.Commun.RegleMetierException("Ce poste n'attend plus de code : rechargez la liste.");
        await journal.EcrireAsync(utilisateurId, ActionCode, Table, id, new Dictionary<string, object?> { ["nom"] = poste.Nom }, null, ct);
        return new CodeEnrolementCree(id, poste.Nom, code, expire);
    }

    public async Task<CodeEnrolementCree> RemplacerAsync(long utilisateurId, long id, CancellationToken ct = default)
    {
        await droits.ExigerTitulaireAsync(utilisateurId, "remplacer le PC d'un poste", ct);
        var poste = await depot.LireAsync(id, ct) ?? throw Introuvable();
        if (poste.Statut != "actif")
            throw new Domain.Commun.RegleMetierException("Seul un poste actif peut changer de PC (un poste en attente reçoit un nouveau code).");
        var reglages = await depot.ReglagesAsync(ct);
        var code = CodeEnrolement.Nouveau();
        var expire = horloge.MaintenantUtc.AddMinutes(reglages.DureeCodeMinutes);
        if (!await depot.RemplacerAsync(id, CodeEnrolement.Hacher(code), expire, ct))
            throw new Domain.Commun.RegleMetierException("Ce poste n'est plus actif : rechargez la liste.");
        registre.Invalider(id);
        await journal.EcrireAsync(utilisateurId, ActionRemplace, Table, id, new Dictionary<string, object?>
            { ["nom"] = poste.Nom, ["generation"] = poste.Generation + 1, ["ancienne_empreinte"] = poste.EmpreinteCle }, null, ct);
        return new CodeEnrolementCree(id, poste.Nom, code, expire);
    }

    public async Task RenommerAsync(long utilisateurId, long id, string? nom, CancellationToken ct = default)
    {
        await droits.ExigerTitulaireAsync(utilisateurId, "renommer un poste", ct);
        var n = NomValide(nom);
        var poste = await depot.LireAsync(id, ct) ?? throw Introuvable();
        if (poste.Statut == "revoque") throw new Domain.Commun.RegleMetierException("Un poste révoqué ne se renomme pas.");
        if (string.Equals(poste.Nom, n, StringComparison.Ordinal)) return;
        if (await depot.PosteOccupeAsync(poste.Nom, ct))
            throw new RefusPosteException(ConstantesPostes.PosteOccupe, 409,
                "Ce poste a une caisse ouverte, ou des ventes ou réceptions en attente sous son nom : clôturez-les avant de le renommer.");
        switch (await depot.RenommerAsync(id, n, ct))
        {
            case null: throw Introuvable();
            case false: throw NomPris(n);
        }
        registre.Invalider(id);
        await journal.EcrireAsync(utilisateurId, ActionRenomme, Table, id, new Dictionary<string, object?> { ["avant"] = poste.Nom, ["apres"] = n }, null, ct);
    }

    public async Task RevoquerAsync(long utilisateurId, long id, string? motif, CancellationToken ct = default)
    {
        await droits.ExigerTitulaireAsync(utilisateurId, "révoquer un poste", ct);
        var m = motif?.Trim() ?? "";
        if (m.Length is < MotifMin or > MotifMax || m.Any(char.IsControl))
            throw new Domain.Commun.RegleMetierException($"Motif obligatoire ({MotifMin} à {MotifMax} caractères).", "motif");
        var poste = await depot.LireAsync(id, ct) ?? throw Introuvable();
        if (poste.Statut == "revoque") return;
        if (!await depot.RevoquerAsync(id, utilisateurId, m, ct)) return;
        registre.Invalider(id);
        await journal.EcrireAsync(utilisateurId, ActionRevoque, Table, id, new Dictionary<string, object?> { ["nom"] = poste.Nom, ["motif"] = m }, null, ct);
    }

    public static string NomValide(string? nom) =>
        NomPoste.Clair(nom) ?? throw new Domain.Commun.RegleMetierException("Nom du poste obligatoire (1 à 100 caractères, sans caractère de contrôle).", "nom");

    private static RefusPosteException NomPris(string nom) =>
        new(ConstantesPostes.NomPostePris, 409, $"Le nom « {nom} » est déjà celui d'un autre poste (sans tenir compte des majuscules).");

    private static Domain.Commun.RegleMetierException Introuvable() => new("Poste introuvable : rechargez la liste.");
}
