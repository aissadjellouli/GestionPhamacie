using System.Security.Claims;

namespace Pharma.Api.Securite;

/// <summary>
/// Titulaire sur un PC non autorisé : Administration seulement (décision du titulaire du 2026-11-27). Depuis un PC qui ne prouve pas être un poste
/// autorisé (il s'annonce par X-Pharma-Poste sans preuve valide, ou avec la preuve d'un poste révoqué, remplacé ou inconnu), seul le pharmacien
/// titulaire se connecte, et seulement aux routes de la liste fermée ci-dessous : Postes autorisés, Mon offre, utilisateurs et droits,
/// notifications, pharmacie, imprimantes, sauvegardes, 2e serveur, synchronisation, plus la session (connexion, mot de passe, droits, affichage).
/// Jamais la caisse, la vente, le stock, les achats, les patients ni les finances. Ce PC n'est pas un poste : il n'entre pas dans le maximum de
/// postes, aucune caisse ne lui est liée.
/// <para>
/// Jeton : celui émis sur un PC non prouvé porte la revendication « acces = administration_seule » (JetonService), sans nom ni identifiant de
/// poste. Pourquoi une marque dans le jeton en plus du contrôle de la preuve à chaque requête : un jeton marqué reste limité à l'Administration
/// PARTOUT, même rejoué sans l'en-tête X-Pharma-Poste (comme un client sans poste) ou après l'autorisation de ce PC ; sans la marque, un jeton
/// sans poste ouvert sur ce PC passerait pour celui d'un client sans poste et ouvrirait tout. La preuve reste aussi contrôlée à chaque requête :
/// sur un PC non prouvé, un jeton non marqué est refusé comme avant.
/// </para>
/// </summary>
public static class AccesAdministrationSeule
{
    /// <summary>Code du refus (ProblemDetails, type urn:pharmaofficine:erreur:administration_seule) : 403, la session du poste reste ouverte.</summary>
    public const string Code = "administration_seule";

    /// <summary>Revendication du jeton émis sur un PC non prouvé.</summary>
    public const string Revendication = "acces";
    public const string Valeur = "administration_seule";

    /// <summary>Clé de HttpContext.Items : requête d'un PC non prouvé laissée passer jusqu'au contrôle du jeton (ControlePosteJeton).</summary>
    public const string ItemPcNonProuve = "Pharma.PcNonProuve";

    public const string Message =
        "Depuis un PC non autorisé, le titulaire n'a accès qu'à l'Administration ; autorisez ce PC (Administration › Postes autorisés) pour le reste.";

    public const string MessageConnexion = Pharma.Application.Securite.SeConnecter.MessageTitulaireSeulement;

    /// <summary>Routes ouvertes sans jeton sur un PC non prouvé : la connexion (réservée au titulaire, contrôlée par SeConnecter).</summary>
    public static readonly IReadOnlyList<string> RoutesAnonymes = ["/api/auth/connexion"];

    /// <summary>
    /// Liste FERMÉE des préfixes permis (segments entiers : « /api/offre » ne couvre pas « /api/offres »), établie d'après les appels des écrans
    /// d'administration permis sur un PC non prouvé (Pharma.Desktop, AdministrationSeule.Ecrans) ; un test vérifie que chaque appel de ces écrans
    /// est couvert et qu'aucun préfixe n'ouvre la caisse, la vente, le stock ou les finances. Toute modification : docs/securite-et-droits.md.
    /// </summary>
    public static readonly IReadOnlyList<string> Prefixes =
    [
        // Session : connexion, mot de passe provisoire, droits et affichage de l'employé connecté
        "/api/auth/connexion", "/api/auth/mot-de-passe", "/api/auth/permissions", "/api/auth/affichage",
        // Pharmacie et offre
        "/api/admin/postes", "/api/offre", "/api/admin/pharmacie", "/api/pharmacie", "/api/admin/organismes", "/api/medecins", "/api/wilayas",
        "/api/suggestions/specialite_medecin",
        // Équipe
        "/api/admin/utilisateurs", "/api/admin/droits", "/api/admin/notifications", "/api/parametres/formulaire-sms",
        // Matériel et sauvegardes
        "/api/admin/sauvegardes", "/api/admin/second-serveur", "/api/admin/synchronisation", "/api/reglages-mutualises"
    ];

    public static bool RouteAnonyme(PathString chemin) => RoutesAnonymes.Any(r => chemin.StartsWithSegments(r, StringComparison.OrdinalIgnoreCase));

    public static bool RouteAutorisee(PathString chemin) => Prefixes.Any(p => chemin.StartsWithSegments(p, StringComparison.OrdinalIgnoreCase));

    /// <summary>Requête d'un PC non prouvé (posé par PreuvePosteMiddleware sur une route non ouverte).</summary>
    public static bool PcNonProuve(HttpContext ctx) => ctx.Items.ContainsKey(ItemPcNonProuve);

    public static bool JetonMarque(ClaimsPrincipal user) =>
        user.Identity?.IsAuthenticated == true && user.FindFirstValue(Revendication) == Valeur;

    /// <summary>Le jeton créé pendant cette requête doit être marqué : PC non prouvé, ou jeton déjà marqué (un jeton marqué n'engendre que des jetons marqués).</summary>
    public static bool Demande(HttpContext? ctx) => ctx is not null && (PcNonProuve(ctx) || JetonMarque(ctx.User));

    /// <summary>Refus d'origine d'une requête d'un PC non prouvé (statut et code), appliqué si le jeton n'est pas marqué.</summary>
    internal const string ItemRefusOrigine = "Pharma.RefusOrigineAdministrationSeule";

    internal static void LaisserPasser(HttpContext ctx, int statut, string code)
    {
        ctx.Items[ItemPcNonProuve] = true;
        ctx.Items[ItemRefusOrigine] = (statut, code);
    }

    internal static (int Statut, string Code) RefusOrigine(HttpContext ctx) =>
        ctx.Items.TryGetValue(ItemRefusOrigine, out var r) && r is ValueTuple<int, string> t ? t : (StatusCodes.Status403Forbidden, Pharma.Shared.PreuvePoste.PosteNonAutorise);

    /// <summary>
    /// Contrôle après l'authentification (ControlePosteJeton). Vrai si la requête est traitée ici (suite appelée ou refus écrit), faux si le
    /// contrôle ordinaire du jeton s'applique. Jeton marqué : routes de la liste seulement (sinon 403 « administration_seule »), partout.
    /// PC non prouvé sans jeton marqué : la connexion seule sans jeton, sinon le refus d'origine (inchangé).
    /// </summary>
    public static async Task<bool> FiltrerAsync(HttpContext ctx, Func<Task> suite)
    {
        var marque = JetonMarque(ctx.User);
        if (!marque && !PcNonProuve(ctx)) return false;
        if (marque)
        {
            if (RouteAutorisee(ctx.Request.Path)) await suite();
            else await PreuvePosteMiddleware.RefuserAsync(ctx, StatusCodes.Status403Forbidden, Code);
            return true;
        }
        if (ctx.User.Identity?.IsAuthenticated != true && RouteAnonyme(ctx.Request.Path))
        {
            await suite();
            return true;
        }
        var (statut, code) = RefusOrigine(ctx);
        await PreuvePosteMiddleware.RefuserAsync(ctx, statut, code);
        return true;
    }
}
