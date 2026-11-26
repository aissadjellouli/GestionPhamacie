using Pharma.Application.Offre;
using System.Security.Claims;
using Pharma.Api.Securite;
using Pharma.Application.Serialisation;
using Pharma.Shared;

namespace Pharma.Api.Endpoints;

/// <summary>Sérialisation DataMatrix (script 120) : état de la file hors ligne, relance manuelle et réglages, réservés au titulaire.</summary>
internal static class SerialisationEndpoints
{
    public static void MapSerialisation(this RouteGroupBuilder api)
    {
        api.MapGet("/serialisation/etat", async (PiloterFileSerialisation piloter, CancellationToken ct) =>
            {
                var e = await piloter.EtatAsync(ct);
                var p = await piloter.ParametresAsync(ct);
                var ai = await piloter.IdentifiantsGs1Async(ct);
                return Results.Ok(new EtatSerialisationDto(e.EnAttente, e.Reussies, e.Refusees, e.DernierEssaiLe, e.DerniereErreur,
                    p.Actif, p.Connecteur, p.FacticeHorsLigne,
                    ai.Select(i => new IdentifiantGs1Dto(i.Code, i.Champ.ToString().ToLowerInvariant(), i.LongueurFixe, i.LongueurMax)).ToList()));
            })
           .ExigerTitulaire()
           .AvecEtatReglageCentral(Pharma.Domain.Reglages.TablesMutualisables.Serialisation).ExigerPack(PacksOffre.Inventaire);

        api.MapGet("/serialisation/file", async (string? statut, int? page, int? taille, PiloterFileSerialisation piloter, CancellationToken ct) =>
            {
                var numero = Math.Max(1, page ?? 1);
                var t = Math.Clamp(taille ?? 10, 1, 200);
                var (lignes, total) = await piloter.ListerAsync(string.IsNullOrWhiteSpace(statut) ? null : statut, numero, t, ct);
                return Results.Ok(new PageDto<OperationSerialisationDto>(lignes.Select(l => new OperationSerialisationDto(
                    l.Id, l.BoiteId, l.Produit, l.Gtin, l.NumeroSerie, l.Lot, LibellesSerialisation.Code(l.Operation), l.Statut,
                    l.Resultat is { } r ? LibellesSerialisation.Code(r) : null, l.Message, l.Essais, l.CreeLe, l.DernierEssaiLe, l.TraiteLe)).ToList(),
                    total, numero, t));
            })
           .ExigerTitulaire()
           .GererErreursMetier().ExigerPack(PacksOffre.Inventaire);

        // Relance d'une opération (id) ou de toute la file en attente, puis rejeu immédiat
        api.MapPost("/serialisation/relance", async (long? id, PiloterFileSerialisation piloter, ClaimsPrincipal user, CancellationToken ct) =>
            {
                var (n, b) = await piloter.RelancerAsync(id, user.UtilisateurId(), ct);
                return Results.Ok(new RelanceSerialisationDto(n, b.Reussies, b.Refusees, b.ConnecteurIndisponible));
            })
           .ExigerTitulaire()
           .GererErreursMetier().ExigerPack(PacksOffre.Inventaire);

        api.MapPut("/serialisation/parametres", async (ParametresSerialisationRequete r, PiloterFileSerialisation piloter,
                ClaimsPrincipal user, CancellationToken ct) =>
            {
                await piloter.EnregistrerAsync(r.Actif, r.FacticeHorsLigne, user.UtilisateurId(), ct);
                return Results.NoContent();
            })
           .ExigerTitulaire().ExigerPack(PacksOffre.Inventaire);
    }
}
