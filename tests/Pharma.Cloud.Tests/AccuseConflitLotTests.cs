using System.Net;
using System.Net.Http.Headers;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Pharma.Cloud.Api;
using Pharma.Shared;

namespace Pharma.Cloud.Tests;

/// <summary>
/// Refus 409 d'un dépôt de lot, au vrai format HTTP (revue du 2026-11-22) : la réponse produite par le serveur central est relue par
/// la fonction du client de l'officine. Avant, « autre contenu » partait en ProblemDetails : l'officine n'en tirait aucun accusé et
/// ne détectait jamais le retour en arrière de sa base.
/// </summary>
public class AccuseConflitLotTests
{
    /// <summary>Exécute la réponse du central et la recopie en réponse HTTP telle que le client la reçoit.</summary>
    private static async Task<HttpResponseMessage> ReponseHttpAsync(IResult resultat)
    {
        var ctx = new DefaultHttpContext { RequestServices = new ServiceCollection().AddLogging().BuildServiceProvider() };
        var corps = new MemoryStream();
        ctx.Response.Body = corps;
        await resultat.ExecuteAsync(ctx);
        var reponse = new HttpResponseMessage((HttpStatusCode)ctx.Response.StatusCode) { Content = new ByteArrayContent(corps.ToArray()) };
        if (ctx.Response.ContentType is { } type) reponse.Content.Headers.ContentType = MediaTypeHeaderValue.Parse(type);
        return reponse;
    }

    [Theory]
    [InlineData(StatutsLot.EmpreinteDifferente)]
    [InlineData(StatutsLot.Trou)]
    public async Task Le_client_lit_l_accuse_du_refus_409_du_central(string statut)
    {
        using var reponse = await ReponseHttpAsync(PointsEntree.AccuseConflit(new AccuseLotDto(3, 12, statut, 15)));
        Assert.Equal(HttpStatusCode.Conflict, reponse.StatusCode);
        var accuse = await AccuseConflitLot.LireAsync(reponse);
        Assert.NotNull(accuse);
        Assert.Equal((3, 12L, statut, 15L), (accuse.Epoque, accuse.Numero, accuse.Statut, accuse.DernierRecu));
    }

    [Fact]
    public async Task Un_409_sans_accuse_n_en_donne_aucun()
    {
        // Central plus ancien (ProblemDetails) ou autre refus : pas d'accusé, le client lève l'erreur habituelle
        using var probleme = await ReponseHttpAsync(Results.Problem("Lot déjà reçu avec un autre contenu : anomalie signalée.", statusCode: 409));
        Assert.Null(await AccuseConflitLot.LireAsync(probleme));
        using var autre = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}", System.Text.Encoding.UTF8, "application/json") };
        Assert.Null(await AccuseConflitLot.LireAsync(autre));
    }
}
