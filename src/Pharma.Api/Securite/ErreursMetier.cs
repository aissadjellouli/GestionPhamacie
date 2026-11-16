using Npgsql;
using Pharma.Application.Commun;
using Pharma.Domain.Commun;
using Pharma.Shared;

namespace Pharma.Api.Securite;

/// <summary>
/// Refus prévus (règle métier, droit manquant, refus d'un déclencheur de la base) : réponse claire au poste, sans les
/// traiter comme des pannes. Ils ne remontent plus jusqu'au gestionnaire global d'erreurs : pas d'inscription en erreur
/// au journal du serveur, et le débogueur ne s'arrête plus dessus. Les vraies pannes restent gérées par UseExceptionHandler.
/// Journal sans données personnelles : le message du refus (qui peut citer un patient, un médecin, un employé ou une
/// saisie libre) part au poste mais n'est jamais écrit au journal ; seuls le chemin, le type, le champ, le droit ou le
/// code de la base y figurent.
/// </summary>
public static class ErreursMetier
{
    public static TBuilder GererErreursMetier<TBuilder>(this TBuilder builder) where TBuilder : IEndpointConventionBuilder
    {
        builder.AddEndpointFilter(Filtrer);
        return builder;
    }

    /// <summary>Filtre des refus prévus (public pour les tests).</summary>
    public static async ValueTask<object?> Filtrer(EndpointFilterInvocationContext ctx, EndpointFilterDelegate suite)
    {
        try
        {
            return await suite(ctx);
        }
        catch (RegleMetierException e)
        {
            return Refus(ctx.HttpContext, StatusCodes.Status422UnprocessableEntity, e.Message, e, e.Champ);
        }
        catch (DroitRefuseException e)
        {
            Journal(ctx.HttpContext).LogInformation("{Chemin} refusé (droit {Droit})", ctx.HttpContext.Request.Path, e.Permission);
            return await PermissionExtensions.RefusAsync(ctx.HttpContext, e.Permission, e.Message, e.Seuil);
        }
        catch (PostgresException e) when (e.SqlState == "P0001")
        {
            return Refus(ctx.HttpContext, StatusCodes.Status422UnprocessableEntity, e.MessageText, e);
        }
        catch (PostgresException e) when (Traduire(e) is { } refus)
        {
            // Le détail technique (code, contrainte, table) reste au journal du serveur, jamais dans la réponse au poste ;
            // le texte de la base (qui peut citer la valeur saisie) n'est pas journalisé
            Journal(ctx.HttpContext).LogInformation("{Chemin} : refus de la base {Code} (contrainte {Contrainte}, table {Table})",
                ctx.HttpContext.Request.Path, e.SqlState, e.ConstraintName, e.TableName);
            return Refus(ctx.HttpContext, refus.Statut, refus.Message, e, journaliser: false);
        }
    }

    /// <summary>
    /// Refus techniques prévisibles de la base, traduits en message clair pour le poste (sans détail technique) :
    /// conflit avec un autre poste (interblocage, sérialisation) → 409, à réessayer ; doublon, valeur non permise
    /// ou hors limites → 422. Null pour les autres erreurs (vraies pannes).
    /// </summary>
    public static (int Statut, string Message)? Traduire(PostgresException e) => e.SqlState switch
    {
        PostgresErrorCodes.DeadlockDetected or PostgresErrorCodes.SerializationFailure =>
            (StatusCodes.Status409Conflict, "Opération en conflit avec un autre poste : réessayez."),
        PostgresErrorCodes.UniqueViolation =>
            (StatusCodes.Status422UnprocessableEntity, "Enregistrement refusé : cette donnée existe déjà. Vérifiez la saisie."),
        PostgresErrorCodes.CheckViolation =>
            (StatusCodes.Status422UnprocessableEntity, "Enregistrement refusé : une valeur saisie n’est pas permise. Vérifiez la saisie."),
        PostgresErrorCodes.NumericValueOutOfRange =>
            (StatusCodes.Status422UnprocessableEntity, "Enregistrement refusé : un nombre saisi est trop grand. Vérifiez la saisie."),
        PostgresErrorCodes.InvalidRowCountInResultOffsetClause =>
            (StatusCodes.Status422UnprocessableEntity, "Recherche refusée : numéro de page hors limites."),
        _ => null
    };

    private static ILogger Journal(HttpContext http) =>
        http.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("Pharma.Refus");

    private static IResult Refus(HttpContext http, int statut, string message, Exception e, string? champ = null, bool journaliser = true)
    {
        // Au journal : type et champ du refus (P0001 : code de la base), jamais le message
        if (journaliser)
            Journal(http).LogInformation("{Chemin} refusé ({Type}, champ {Champ}, statut {Statut})",
                http.Request.Path, e is PostgresException p ? $"{e.GetType().Name} {p.SqlState}" : e.GetType().Name, champ ?? "-", statut);
        return Results.Json(new ErreurDto(message, champ), statusCode: statut);
    }
}
