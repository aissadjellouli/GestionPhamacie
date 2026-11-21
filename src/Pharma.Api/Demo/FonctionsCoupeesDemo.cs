using System.Text.RegularExpressions;
using Pharma.Application.Demo;
using Pharma.Shared;

namespace Pharma.Api.Demo;

/// <summary>
/// Routes coupées en démonstration (Demo:Actif ; docs/regles-metier.md, « Mode démonstration ») : refus 422 avec un message clair,
/// avant toute authentification. Les écrans de consultation (GET) des sauvegardes, de la synchronisation et du 2e serveur restent
/// lisibles ; leurs actions sont refusées. Les envois (e-mail, SMS) sont aussi refusés par les adaptateurs factices.
/// </summary>
public static class FonctionsCoupeesDemo
{
    private sealed record Regle(Regex Chemin, bool LectureAutorisee, string Fonction);

    // Routage d'ASP.NET Core insensible à la casse : les règles aussi
    private const RegexOptions Options = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;

    private static readonly Regle[] Regles =
    [
        new(new Regex("^/api/super-admin(/|$)", Options), false, "super admin (restauration de la base et de la GED, reprise de données, validations)"),
        new(new Regex("^/api/admin/reprise(/|$)", Options), false, "reprise de données d'un ancien logiciel (ODBC)"),
        new(new Regex("^/api/admin/synchronisation(/|$)", Options), true, "synchronisation avec le serveur central"),
        new(new Regex("^/api/admin/sauvegardes/secours(/|$)", Options), true, "copie de secours"),
        new(new Regex("^/api/admin/sauvegardes(/|$)", Options), true, "sauvegardes automatiques"),
        new(new Regex("^/api/admin/second-serveur(/|$)", Options), true, "second serveur"),
        new(new Regex(@"^/api/admin/notifications/destinataires/\d+/(code|test)/?$", Options), false, "envoi de SMS et d'e-mails"),
        new(new Regex("^/api/admin/notifications/rapport/?$", Options), false, "envoi de SMS et d'e-mails"),
        new(new Regex(@"^/api/achats/commandes/\d+/envoyer-email/?$", Options), false, "envoi d'e-mails")
    ];

    /// <summary>Fonction coupée visée par la requête (libellé du message), sinon null.</summary>
    public static string? FonctionCoupee(string methode, string? chemin)
    {
        if (string.IsNullOrEmpty(chemin)) return null;
        var lecture = HttpMethods.IsGet(methode) || HttpMethods.IsHead(methode) || HttpMethods.IsOptions(methode);
        return Regles.FirstOrDefault(r => r.Chemin.IsMatch(chemin) && !(lecture && r.LectureAutorisee))?.Fonction;
    }

    /// <summary>Middleware : 422 « Indisponible en démonstration : … » pour une fonction coupée, sinon la suite.</summary>
    public static async Task FiltrerAsync(HttpContext ctx, Func<Task> suite)
    {
        if (FonctionCoupee(ctx.Request.Method, ctx.Request.Path.Value) is { } fonction)
        {
            ctx.Response.StatusCode = StatusCodes.Status422UnprocessableEntity;
            await ctx.Response.WriteAsJsonAsync(new ErreurDto(ModeDemo.Indisponible(fonction)));
            return;
        }
        await suite();
    }
}
