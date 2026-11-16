using System.Security.Claims;
using Pharma.Api.Securite;
using Pharma.Application.Synchronisation;
using Pharma.Domain.Securite;
using Pharma.Shared;

namespace Pharma.Api.Endpoints;

/// <summary>Écran « Admin › Synchronisation » (lot S2, droit admin.parametres) : réglages, activation, état, envoi immédiat.</summary>
public static class SynchronisationEndpoints
{
    public static void MapSynchronisation(this RouteGroupBuilder api)
    {
        var s = api.MapGroup("/admin/synchronisation").ExigerPermission(Permissions.AdminParametres);

        s.MapGet("", async (IEnvoiSyncDepot depot, ICertificatSync certificat, CancellationToken ct) =>
        {
            var p = await depot.ParametresAsync(ct);
            CertificatSyncInfo? c;
            try { c = certificat.Lire(); }
            catch (Exception e) when (e is InvalidOperationException or PlatformNotSupportedException or System.Security.Cryptography.CryptographicException) { c = null; }
            return Results.Ok(new EtatSynchronisationDto(p.Active, p.OfficineId?.ToString("D"), p.Adresse, p.InscriteLe is not null && c is not null,
                p.InscriteLe, p.DernierEnvoiLe, p.DernierAccuseLe, p.DernierLot, p.DernierAccuse, p.LignesEnAttente, p.LotsEnAttente,
                c?.EcheanceUtc, c?.Empreinte[..16], p.DerniereErreur, p.DerniereErreurLe, p.TextesLibres, p.EnvoyerMedecins, p.Epoque, p.VersionContrat,
                p.LotsAutresEpoques, p.BaseEnLectureSeule));
        });

        s.MapPut("/reglages", async (ReglagesSynchronisationDto d, ReglerSync regler, ClaimsPrincipal user, CancellationToken ct) =>
        {
            await regler.ExecuterAsync(new ReglagesSync(d.Active, d.Adresse, d.TextesLibres, d.EnvoyerMedecins), user.UtilisateurId(), ct);
            return Results.NoContent();
        });

        s.MapPost("/activation", async (ActivationSynchronisationDto d, ActiverSync activer, ClaimsPrincipal user, CancellationToken ct) =>
        {
            var c = await activer.ExecuterAsync(d.Code, user.UtilisateurId(), ct);
            return Results.Ok(new { echeance = c.EcheanceUtc, empreinte = c.Empreinte[..16] });
        });

        s.MapPost("/envoyer", async (EnvoyerLotsSync envoyer, CancellationToken ct) =>
        {
            var r = await envoyer.ExecuterAsync(DateTime.UtcNow, ct: ct);
            if (r.Arret == EnvoyerLotsSync.ArretEnCours) return Results.Problem(r.Arret, statusCode: StatusCodes.Status409Conflict);
            if (r.Erreur is not null) return Results.Problem(r.Erreur, statusCode: StatusCodes.Status502BadGateway);
            return Results.Ok(new ResultatEnvoiSynchronisationDto(r.LotsAccuses, r.Arret, r.Erreur));
        });
    }
}
