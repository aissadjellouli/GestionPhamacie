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
                return Refus(ctx.HttpContext, StatusCodes.Status422UnprocessableEntity, e.Message, e);
            }
            catch (DroitRefuseException e)
            {
                return Refus(ctx.HttpContext, StatusCodes.Status403Forbidden, e.Message, e);
            }
            catch (PostgresException e) when (e.SqlState == "P0001")
            {
                return Refus(ctx.HttpContext, StatusCodes.Status422UnprocessableEntity, e.MessageText, e);
            }
        });
        return builder;
    }

    private static IResult Refus(HttpContext http, int statut, string message, Exception e)
    {
        http.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("Pharma.Refus")
            .LogInformation("{Chemin} refusé ({Type}) : {Message}", http.Request.Path, e.GetType().Name, message);
        return Results.Json(new ErreurDto(message), statusCode: statut);
    }
}
