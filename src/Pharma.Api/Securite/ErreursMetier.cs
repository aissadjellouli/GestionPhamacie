using Npgsql;
using Pharma.Application.Commun;
using Pharma.Domain.Commun;
using Pharma.Shared;

namespace Pharma.Api.Securite;

/// <summary>
/// Refus prévus (règle métier, droit manquant, refus d'un déclencheur de la base) : réponse claire au poste, sans les
/// traiter comme des pannes. Ils ne remontent plus jusqu'au gestionnaire global d'erreurs : pas d'inscription en erreur
/// au journal du serveur, et le débogueur ne s'arrête plus dessus. Les vraies pannes restent gérées par UseExceptionHandler.
/// </summary>
public static class ErreursMetier
{
    public static TBuilder GererErreursMetier<TBuilder>(this TBuilder builder) where TBuilder : IEndpointConventionBuilder
    {
        builder.AddEndpointFilter(async (ctx, suite) =>
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
                ctx.HttpContext.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("Pharma.Refus")
                    .LogInformation("{Chemin} refusé (droit {Droit}) : {Message}", ctx.HttpContext.Request.Path, e.Permission, e.Message);
                return await PermissionExtensions.RefusAsync(ctx.HttpContext, e.Permission, e.Message);
            }
            catch (PostgresException e) when (e.SqlState == "P0001")
            {
                return Refus(ctx.HttpContext, StatusCodes.Status422UnprocessableEntity, e.MessageText, e);
            }
            catch (PostgresException e) when (Traduire(e) is { } refus)
            {
                // Le détail technique (code, contrainte, table) reste au journal du serveur, jamais dans la réponse au poste
                ctx.HttpContext.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("Pharma.Refus")
                    .LogInformation("{Chemin} : refus de la base {Code} (contrainte {Contrainte}, table {Table}) : {Detail}",
                        ctx.HttpContext.Request.Path, e.SqlState, e.ConstraintName, e.TableName, e.MessageText);
                return Refus(ctx.HttpContext, refus.Statut, refus.Message, e);
            }
        });
        return builder;
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

    private static IResult Refus(HttpContext http, int statut, string message, Exception e, string? champ = null)
    {
        http.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("Pharma.Refus")
            .LogInformation("{Chemin} refusé ({Type}) : {Message}", http.Request.Path, e.GetType().Name, message);
        return Results.Json(new ErreurDto(message, champ), statusCode: statut);
    }
}
