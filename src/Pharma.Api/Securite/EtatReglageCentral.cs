using Pharma.Application.Reglages;
using Pharma.Shared;

namespace Pharma.Api.Securite;

/// <summary>
/// Mutualisation des réglages (lot M5) : les routes de lecture des écrans de réglage répondent aussi par l'en-tête « X-Reglage-Central »
/// (mode en vigueur, état, version, ajouts locaux de la table), pour que l'écran grise ses champs sans appel de plus.
/// Choisi plutôt qu'un champ dans chaque réponse : trois de ces routes renvoient une liste ou une page (motifs, remises, règles des psychotropes) et
/// les contrats JSON et leurs tests ne changent pas. Une lecture impossible ne bloque jamais la route : sans en-tête, l'écran se comporte comme sans
/// serveur central, et le refus 409 « reglage_impose » de l'écriture reste la garde.
/// </summary>
public static class EtatReglageCentral
{
    public static TBuilder AvecEtatReglageCentral<TBuilder>(this TBuilder builder, string table) where TBuilder : IEndpointConventionBuilder
    {
        builder.AddEndpointFilter(async (ctx, suite) =>
        {
            var http = ctx.HttpContext;
            try
            {
                var depot = http.RequestServices.GetRequiredService<IReglagesMutualisesDepot>();
                var (mode, ajouts) = await depot.ModeEffectifAsync(table, http.RequestAborted);
                var etat = (await depot.EtatsAsync(http.RequestAborted)).FirstOrDefault(e => e.Table == table);
                http.Response.Headers[EtatReglageEntete.Nom] = new EtatReglageEntete(table, mode, etat?.Etat ?? "aucun", etat?.VersionRecue, ajouts).Formater();
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                http.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("ReglagesMutualises")
                    .LogWarning(e, "État du réglage central {Table} illisible : réponse sans en-tête", table);
            }
            return await suite(ctx);
        });
        return builder;
    }
}
