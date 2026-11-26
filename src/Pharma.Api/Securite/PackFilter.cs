using Pharma.Application.Offre;

namespace Pharma.Api.Securite;

/// <summary>
/// Lot O6 (offre de base et options, docs/offre-et-options.md) : route d'un pack. Pack non inclus dans l'offre en vigueur : refus 403
/// « Option non incluse — voir Mon offre ». Packs avec des données (A à D) : les lectures (GET) restent permises (consultation seule).
/// Le serveur refuse lui-même : masquer un bouton au poste ne suffit pas.
/// </summary>
public static class PackExtensions
{
    public static TBuilder ExigerPack<TBuilder>(this TBuilder builder, string pack) where TBuilder : IEndpointConventionBuilder
    {
        builder.AddEndpointFilter(async (ctx, next) =>
        {
            var http = ctx.HttpContext;
            if (PacksOffre.LectureSeuleSiFerme(pack) && HttpMethods.IsGet(http.Request.Method)) return await next(ctx);
            var packs = http.RequestServices.GetRequiredService<IPacksOffre>();
            if (await packs.InclusAsync(pack, http.RequestAborted)) return await next(ctx);
            return Refus(pack);
        });
        return builder;
    }

    public static IResult Refus(string pack) =>
        Results.Json(new Pharma.Shared.ErreurDto(PacksOffre.MessageNonInclus(pack), null, false, $"option.{pack}"), statusCode: StatusCodes.Status403Forbidden);
}
