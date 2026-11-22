using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Pharma.Cloud.Web.Donnees;

namespace Pharma.Cloud.Web.Securite;

/// <summary>Jeton de session : 32 octets tirés au sort, dans un cookie ; la base ne reçoit que son empreinte SHA-256.</summary>
public static class Jetons
{
    public const string Cookie = "__Host-pharma-ecrans";
    public const string CookieTheme = "__Host-pharma-ecrans-theme";
    public const string CookieAntiFalsification = "__Host-pharma-ecrans-af";

    public static string Nouveau() => Base64Url(RandomNumberGenerator.GetBytes(32));

    public static string Empreinte(string jeton) => Convert.ToHexStringLower(SHA256.HashData(Encoding.ASCII.GetBytes(jeton)));

    /// <summary>Cookie __Host- : Secure, HttpOnly, SameSite=Strict, Path=/, sans domaine, sans date (fermé avec le navigateur).</summary>
    public static CookieOptions Options() => new() { Secure = true, HttpOnly = true, SameSite = SameSiteMode.Strict, Path = "/", IsEssential = true };

    public static void Poser(HttpResponse reponse, string jeton) => reponse.Cookies.Append(Cookie, jeton, Options());

    public static void Retirer(HttpResponse reponse) => reponse.Cookies.Delete(Cookie, Options());

    private static string Base64Url(byte[] o) => Convert.ToBase64String(o).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}

/// <summary>Session de la requête (posée par le contrôle d'accès, lue par les pages et les actions).</summary>
public static class ContexteEcran
{
    private const string CleSession = "ecrans-session";
    private const string CleEmpreinte = "ecrans-empreinte";

    public static SessionEcran? Session(this HttpContext ctx) => ctx.Items[CleSession] as SessionEcran;

    public static string? EmpreinteJeton(this HttpContext ctx) => ctx.Items[CleEmpreinte] as string;

    internal static void Poser(HttpContext ctx, SessionEcran? session, string? empreinte)
    {
        ctx.Items[CleSession] = session;
        ctx.Items[CleEmpreinte] = empreinte;
    }
}

/// <summary>
/// Contrôle d'accès de chaque requête : session lue en base par l'empreinte du jeton, puis aiguillage selon l'étape.
/// Sans session : seulement /connexion et les fichiers statiques. Étape « code » : /code. Étape « mot_de_passe » : /mot-de-passe.
/// Session ouverte : écrans de l'éditeur (S7.1) ou écrans du titulaire (S7.2a), jamais ceux de l'autre rôle (page « accès refusé ») ;
/// la base revérifie de toute façon le rôle dans chaque fonction cloud_ecran_*.
/// </summary>
public static class ControleAcces
{
    private static readonly string[] Statiques = ["/css/", "/js/", "/img/", "/fonts/", "/favicon"];
    private static readonly string[] Publiques = ["/connexion", "/actions/connexion", "/actions/theme", "/actions/deconnexion"];
    private static readonly string[] EtapeCode = ["/code", "/actions/code", "/actions/renvoyer-code", "/actions/deconnexion", "/actions/theme"];
    private static readonly string[] EtapeMotDePasse = ["/mot-de-passe", "/actions/mot-de-passe", "/actions/deconnexion", "/actions/theme"];

    /// <summary>Écrans communs aux deux rôles en session ouverte.</summary>
    private static readonly string[] Communs = ["/mot-de-passe", "/actions/mot-de-passe", "/actions/deconnexion", "/actions/theme", "/mon-journal", "/acces-refuse",
        "/actions/activite"];

    /// <summary>Écrans du titulaire (liste fermée) : ses officines, la vue d'ensemble (S7.3), le rattachement, le code de confirmation
    /// d'un ordre (S7.3), le tableau de bord, le journal et les ordres d'une officine ; les réglages communs et leur envoi (S7.4).</summary>
    private static readonly string[] Titulaire = ["/mes-officines", "/mes-officines/ensemble", "/mes-officines/reglages", "/rattacher", "/actions/rattacher",
        "/actions/code-action", "/actions/mes-officines/reglages"];

    /// <summary>Accueil de chaque rôle (après la connexion, le code, le mot de passe).</summary>
    public static string Accueil(SessionEcran session) => session.Titulaire ? "/mes-officines" : "/officines";

    public static bool Statique(PathString chemin) => Statiques.Any(s => chemin.Value?.StartsWith(s, StringComparison.OrdinalIgnoreCase) == true);

    /// <summary>Tableau de bord, journal, ordres ou retrait (script central 038) d'une officine du titulaire : /mes-officines/{uuid},
    /// …/journal, …/ordres, …/retirer.</summary>
    public static bool EcranOfficineTitulaire(string chemin)
    {
        var morceaux = chemin.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return morceaux.Length is 2 or 3 && string.Equals(morceaux[0], "mes-officines", StringComparison.OrdinalIgnoreCase)
               && Guid.TryParseExact(morceaux[1], "D", out _)
               && (morceaux.Length == 2 || morceaux[2].ToLowerInvariant() is "journal" or "ordres" or "retirer");
    }

    /// <summary>Lot S7.3 : envoi d'un ordre du titulaire, /actions/mes-officines/{uuid}/ordre (POST) ; script central 038 : retrait du
    /// rattachement (…/retrait) et message à afficher sur les postes (…/message).</summary>
    public static bool ActionOfficineTitulaire(string chemin)
    {
        var morceaux = chemin.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return morceaux.Length == 4 && string.Equals(morceaux[0], "actions", StringComparison.OrdinalIgnoreCase)
               && string.Equals(morceaux[1], "mes-officines", StringComparison.OrdinalIgnoreCase)
               && Guid.TryParseExact(morceaux[2], "D", out _) && morceaux[3].ToLowerInvariant() is "ordre" or "retrait" or "message";
    }

    /// <summary>Adresse de redirection, ou null si la requête peut passer (règle pure, testée).</summary>
    public static string? Redirection(PathString chemin, SessionEcran? session)
    {
        var c = chemin.Value ?? "/";
        if (Statique(chemin)) return null;
        bool Parmi(string[] liste) => liste.Any(p => string.Equals(c, p, StringComparison.OrdinalIgnoreCase));
        if (session is null) return Parmi(Publiques) ? null : "/connexion";
        return session.Etape switch
        {
            "code" => Parmi(EtapeCode) || Parmi(Publiques) ? null : "/code",
            "mot_de_passe" => Parmi(EtapeMotDePasse) || Parmi(Publiques) ? null : "/mot-de-passe",
            _ when session.Titulaire => c is "/" or "/officines" or "/connexion" or "/code" ? "/mes-officines"
                : Parmi(Communs) || Parmi(Titulaire) || EcranOfficineTitulaire(c) || ActionOfficineTitulaire(c) ? null : "/acces-refuse",
            // L'éditeur garde le code de confirmation de ses propres actions ; aucun écran ni aucune action du titulaire
            _ when session.Editeur => c is "/connexion" or "/code" ? "/officines"
                : (Parmi(Titulaire) && c != "/actions/code-action") || EcranOfficineTitulaire(c) || ActionOfficineTitulaire(c)
                  || c.StartsWith("/mes-officines/", StringComparison.OrdinalIgnoreCase)
                  || c.StartsWith("/actions/mes-officines/", StringComparison.OrdinalIgnoreCase) ? "/acces-refuse" : null,
            _ => Parmi(Communs) ? null : "/acces-refuse",
        };
    }

    public static async Task Appliquer(HttpContext ctx, RequestDelegate suite)
    {
        SessionEcran? session = null;
        string? empreinte = null;
        if (!Statique(ctx.Request.Path) && ctx.Request.Cookies[Jetons.Cookie] is { Length: > 20 and < 100 } jeton)
        {
            empreinte = Jetons.Empreinte(jeton);
            session = await ctx.RequestServices.GetRequiredService<DepotEcrans>().SessionAsync(empreinte, ctx.RequestAborted);
            if (session is null)
            {
                Jetons.Retirer(ctx.Response);
                empreinte = null;
            }
        }
        ContexteEcran.Poser(ctx, session, empreinte);
        if (Redirection(ctx.Request.Path, session) is { } vers)
        {
            ctx.Response.Redirect(vers);
            return;
        }
        await suite(ctx);
    }
}

/// <summary>
/// Code d'activation (ou, script central 038, mot de passe provisoire d'un compte remis à zéro) tout juste créé : gardé en MÉMOIRE
/// seulement, 2 minutes, pour la seule session qui l'a créé, et rendu une seule fois (page « code affiché » ou « mot de passe
/// provisoire ») ; jamais en base, dans un journal ni sur disque.
/// </summary>
public sealed class CodesAffiches(TimeProvider horloge)
{
    /// <summary>Script central 038 : « serveur » réservé au mot de passe provisoire d'un compte (l'identifiant est alors celui du compte).</summary>
    public const string MotDePasse = "mot_de_passe";

    private sealed record Entree(long Session, Guid Officine, string Serveur, string Code, DateTimeOffset Expire);

    private readonly ConcurrentDictionary<string, Entree> entrees = new();

    public string Garder(long session, Guid officine, string serveur, string code)
    {
        foreach (var (cle, e) in entrees) if (e.Expire < horloge.GetUtcNow()) entrees.TryRemove(cle, out _);
        var reference = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));
        entrees[reference] = new Entree(session, officine, serveur, code, horloge.GetUtcNow().AddMinutes(2));
        return reference;
    }

    public (Guid Officine, string Serveur, string Code)? Prendre(string? reference, long session)
    {
        if (reference is null || !entrees.TryRemove(reference, out var e)) return null;
        return e.Session == session && e.Expire >= horloge.GetUtcNow() ? (e.Officine, e.Serveur, e.Code) : null;
    }
}

/// <summary>
/// Script central 040 : comptes créés, importés ou remis à zéro, avec leur mot de passe provisoire et le résultat des envois, gardés en
/// MÉMOIRE seulement, 2 minutes, pour la seule session de l'éditeur, et rendus une seule fois (page « mot de passe provisoire ») ;
/// jamais en base, dans un journal, une adresse ni sur disque.
/// </summary>
public sealed class ComptesAffiches(TimeProvider horloge)
{
    public sealed record Lot(string Nature, IReadOnlyList<Pharma.Shared.CompteTransmis> Comptes);

    private sealed record Entree(long Session, Lot Lot, DateTimeOffset Expire);

    private readonly ConcurrentDictionary<string, Entree> entrees = new();

    /// <param name="nature">creation, remise ou import.</param>
    public string Garder(long session, string nature, IReadOnlyList<Pharma.Shared.CompteTransmis> comptes)
    {
        foreach (var (cle, e) in entrees) if (e.Expire < horloge.GetUtcNow()) entrees.TryRemove(cle, out _);
        var reference = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));
        entrees[reference] = new Entree(session, new Lot(nature, comptes), horloge.GetUtcNow().AddMinutes(2));
        return reference;
    }

    public Lot? Prendre(string? reference, long session)
    {
        if (reference is null || !entrees.TryRemove(reference, out var e)) return null;
        return e.Session == session && e.Expire >= horloge.GetUtcNow() ? e.Lot : null;
    }
}

/// <summary>
/// Script central 040 : aperçu d'un fichier d'import (lignes lues et statut de chacune), gardé en MÉMOIRE seulement, 15 minutes, pour
/// la seule session de l'éditeur qui l'a déposé (référence tirée au sort dans l'adresse, jamais les lignes) ; retiré à la création.
/// </summary>
public sealed class ApercusImport(TimeProvider horloge)
{
    public static readonly TimeSpan Duree = TimeSpan.FromMinutes(15);

    private sealed record Entree(long Session, IReadOnlyList<Pharma.Shared.LigneCompteImport> Lignes, DateTimeOffset Expire);

    private readonly ConcurrentDictionary<string, Entree> entrees = new();

    public string Garder(long session, IReadOnlyList<Pharma.Shared.LigneCompteImport> lignes)
    {
        foreach (var (cle, e) in entrees)
            if (e.Expire < horloge.GetUtcNow() || e.Session == session) entrees.TryRemove(cle, out _);   // un aperçu à la fois par session
        var reference = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));
        entrees[reference] = new Entree(session, lignes, horloge.GetUtcNow().Add(Duree));
        return reference;
    }

    public IReadOnlyList<Pharma.Shared.LigneCompteImport>? Lire(string? reference, long session) =>
        reference is not null && entrees.TryGetValue(reference, out var e) && e.Session == session && e.Expire >= horloge.GetUtcNow() ? e.Lignes : null;

    /// <summary>Statuts mis à jour (nouvel avis de la base au moment de la confirmation) ; l'échéance ne change pas.</summary>
    public void Remplacer(string reference, long session, IReadOnlyList<Pharma.Shared.LigneCompteImport> lignes)
    {
        if (entrees.TryGetValue(reference, out var e) && e.Session == session) entrees[reference] = e with { Lignes = lignes };
    }

    public void Retirer(string? reference)
    {
        if (reference is not null) entrees.TryRemove(reference, out _);
    }
}
