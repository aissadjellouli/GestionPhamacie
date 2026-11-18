using System.Security.Claims;
using Pharma.Api.Securite;
using Pharma.Application.Synchronisation;
using Pharma.Domain.Securite;
using Pharma.Shared;

namespace Pharma.Api.Endpoints;

/// <summary>Écran « Admin › Synchronisation » (lots S2 et S6, droit admin.parametres) : réglages, activation, état, envoi immédiat, rechargement complet.</summary>
public static class SynchronisationEndpoints
{
    public static void MapSynchronisation(this RouteGroupBuilder api)
    {
        // Lot S3 : bandeau « Synchronisation en attente » de la barre d'état (tout poste connecté ; titulaire et rôles ayant le droit sync.suivre)
        api.MapGet("/synchronisation/attente", async (ConsulterAttenteSync consulter, ClaimsPrincipal user, CancellationToken ct) =>
        {
            var b = await consulter.ExecuterAsync(user.UtilisateurId(), ct);
            return Results.Ok(new AttenteSynchronisationDto(b.Affiche, b.Depuis, b.Operations));
        }).ExigerConnexion();

        var s = api.MapGroup("/admin/synchronisation").ExigerPermission(Permissions.AdminParametres);

        s.MapGet("", async (IEnvoiSyncDepot depot, ICertificatSync certificat, IChargementSyncDepot chargements, SuiviChargementSync suivi,
            ISupervisionSyncDepot supervision, IPurgeSyncDepot purge, CancellationToken ct) =>
        {
            var p = await depot.ParametresAsync(ct);
            var ch = await chargements.DernierAsync(ct);
            var (enCours, faites, total) = suivi.Lire();
            var sup = await supervision.LireAsync(ct);
            var pg = await purge.EtatAsync(ct);
            CertificatSyncInfo? c;
            try { c = certificat.Lire(); }
            catch (Exception e) when (e is InvalidOperationException or PlatformNotSupportedException or System.Security.Cryptography.CryptographicException) { c = null; }
            return Results.Ok(new EtatSynchronisationDto(p.Active, p.OfficineId?.ToString("D"), p.Adresse, p.InscriteLe is not null && c is not null,
                p.InscriteLe, p.DernierEnvoiLe, p.DernierAccuseLe, p.DernierLot, p.DernierAccuse, p.LignesEnAttente, p.LotsEnAttente,
                c?.EcheanceUtc, c?.Empreinte[..16], p.DerniereErreur, p.DerniereErreurLe, p.TextesLibres, p.EnvoyerMedecins, p.Epoque, p.VersionContrat,
                p.LotsAutresEpoques, p.BaseEnLectureSeule,
                ch is null ? null : new ChargementSynchronisationDto(ch.Numero, ch.Motif, ch.Etat, ch.DemandeLe, ch.LignesTotal, ch.LignesAccusees,
                    enCours == ch.Numero ? faites : 0, enCours == ch.Numero ? total : 0, ch.TermineLe, ch.AbsentesCentral),
                p.FamillesEnvoyees, p.FamillesLuesLe, p.FamillesVersion,
                new SupervisionSynchronisationDto(sup.Reglages.BandeauMinutes, sup.Reglages.RetardHeures, sup.Reglages.ErreurMinutes, sup.Reglages.PurgeActive,
                    sup.Reglages.PurgeJours, sup.Attente.Depuis, sup.Attente.Operations, sup.ErreurNature, sup.ErreurDepuisLe,
                    new PurgeSynchronisationDto(pg.EpoquePurge, pg.DernierLotPurge, pg.DernierePurgeLe, pg.DernieresLignes, pg.TotalLignes, pg.DerniereErreur,
                        pg.LignesGardees, pg.OctetsFile)),
                sup.Integrite is not { } ic ? null : new IntegriteCentraleDto(ic.Statut, ic.VerifieLeUtc, ic.CompleteLeUtc, ic.AnomaliesTotal,
                    ic.DerniereAnomalieId, ic.SceauxOk, ic.SceauxAttente, ic.SceauxIncoherents, ic.TicketsVerifiesJusquA,
                    ic.Chaines.Select(c => new ChaineIntegriteCentraleDto(c.Journal, c.Cle, c.Mode, c.Statut, c.DernierRang, c.NonVerifiables)).ToList(),
                    ic.Anomalies.Select(a => new AnomalieIntegriteCentraleDto(a.Id, a.Nature, IntegriteCentrale.LibelleNature(a.Nature), a.Journal, a.Rang,
                        a.Ligne, a.Detail, a.DetecteeLeUtc)).ToList())));
        });

        // Lot S6 : rechargement complet demandé par le titulaire (traité par le passage d'envoi suivant)
        s.MapPost("/recharger", async (RechargerSync recharger, ClaimsPrincipal user, CancellationToken ct) =>
        {
            var n = await recharger.ExecuterAsync(user.UtilisateurId(), ct);
            return Results.Ok(new { numero = n });
        });

        s.MapPut("/reglages", async (ReglagesSynchronisationDto d, ReglerSync regler, ClaimsPrincipal user, CancellationToken ct) =>
        {
            await regler.ExecuterAsync(new ReglagesSync(d.Active, d.Adresse, d.TextesLibres, d.EnvoyerMedecins), user.UtilisateurId(), ct);
            return Results.NoContent();
        });

        // Lot S3 : seuils du bandeau et des alertes, conservation des lots accusés
        s.MapPut("/supervision", async (ReglagesSupervisionSynchronisationDto d, ReglerSupervisionSync regler, ClaimsPrincipal user, CancellationToken ct) =>
        {
            await regler.ExecuterAsync(new ReglagesSupervisionSync(d.BandeauMinutes, d.RetardHeures, d.ErreurMinutes, d.PurgeActive, d.PurgeJours), user.UtilisateurId(), ct);
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

        // Lots S5.1 et S5.5 : ordres reçus du serveur central (liste : admin.parametres ; confirmation et refus : titulaire seul)
        s.MapGet("/ordres", async (string? statut, int? page, int? taille, IOrdresCentrauxDepot ordres,
            Pharma.Application.Administration.IUtilisateursAdmin admin, CancellationToken ct) =>
        {
            if (statut is not null && !StatutsOrdre.Tous.Contains(statut)) return Results.Problem("État d’ordre inconnu.", statusCode: 400);
            var demande = Pharma.Application.Commun.DemandePage.Lire(page, taille);
            var p = await ordres.ListerAsync(statut, demande.Page, demande.Taille, ct);
            var etat = await admin.LireAsync(ct);
            var lignes = p.Ordres.Select(o => new OrdreCentralDto(o.Id, o.Type, TypesOrdre.Libelle(o.Type), ResumeOrdre.Texte(o, etat), o.Auteur,
                o.CreeLeUtc, o.ExpireLeUtc, o.Statut, o.Motif, o.RecuLeUtc, o.TraiteLeUtc, o.Type == TypesOrdre.EmployeCreer)).ToList();
            return Results.Ok(new PageOrdresCentrauxDto(new PageDto<OrdreCentralDto>(lignes, p.Total, demande.Page, demande.Taille), p.AConfirmer));
        });

        s.MapPost("/ordres/{id:guid}/confirmer", async (Guid id, ConfirmerOrdreCentralDto d, ConfirmerOrdreCentral confirmer, ClaimsPrincipal user,
            CancellationToken ct) =>
        {
            await confirmer.ExecuterAsync(id, d.MotDePasse, user.UtilisateurId(), ct);
            return Results.NoContent();
        });

        s.MapPost("/ordres/{id:guid}/refuser", async (Guid id, RefuserOrdreCentralDto d, RefuserOrdreCentral refuser, ClaimsPrincipal user,
            CancellationToken ct) =>
        {
            await refuser.ExecuterAsync(id, d.Motif, user.UtilisateurId(), ct);
            return Results.NoContent();
        });
    }
}
