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

        // Barre d'état (2026-11-21) : liaison avec le serveur central et avec le 2e serveur, tout employé connecté (aucun droit
        // d'administration) ; lecture minimale sans donnée personnelle ni réglage technique, gardée 30 secondes
        api.MapGet("/synchronisation/etat-poste", async (ConsulterLiaisonsPoste consulter, CancellationToken ct) =>
        {
            var e = await consulter.ExecuterAsync(ct);
            return Results.Ok(new EtatLiaisonsPosteDto(e.CentralActive, Dto(e.Central), e.LotsEnAttente, e.DernierEnvoiLe, e.SecondConfigure, Dto(e.Second)));
        }).ExigerConnexion();

        // Script 152 : encaissement suspendu au démarrage jusqu'au contrôle du serveur central (bandeau de la Caisse)
        api.MapGet("/caisse/attente-encaissement", async (IEnvoiSyncDepot depot, CancellationToken ct) =>
            Results.Ok(new AttenteEncaissementDto(await depot.AttenteEncaissementAsync(ct)))).ExigerPermission(Permissions.CaisseVendre);

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
                        a.Ligne, a.Detail, a.DetecteeLeUtc, a.Classee)).ToList(),
                    ic.RestaurationsListe.Select(b => new RestaurationCentraleDto(b.Id, b.Journal, b.AncienneEpoque, b.NouvelleEpoque, b.RangCommun,
                        b.FinAncienneRang, b.Lignes, b.DuUtc, b.AuUtc, b.MontantTtc, b.SceauxPerdus, b.Classe, b.PriseActe, b.TicketsReattribues)).ToList(),
                    ic.AnomaliesClassees),
                p.RetourArriereLe is not { } ra ? null : new RetourArriereSynchronisationDto(ra, p.Epoque, p.RetourArriereCentral ?? 0, p.DernierLot,
                    p.RetourArriereSequencesLe is not null, sup.RetourArriere?.AlerteLeUtc), p.DelaiControleDemarrage));
        });

        // Lot S6.2 : base revenue en arrière (restauration, bascule) : le titulaire repart sur une nouvelle époque (confirmation : époque
        // affichée et mot de passe du titulaire ressaisi), journalisé ; refusé sans détection. Le chargement complet part au passage suivant.
        s.MapPost("/nouvelle-epoque", async (NouvelleEpoqueSynchronisationDto d, RepartirNouvelleEpoqueSync repartir, ClaimsPrincipal user, CancellationToken ct) =>
        {
            var r = await repartir.ExecuterAsync(d.EpoqueAffichee, d.MotDePasse, user.UtilisateurId(), ct);
            return Results.Ok(new ResultatNouvelleEpoqueSynchronisationDto(r.Epoque, r.AncienneEpoque, r.Chargement));
        });

        // Lot S7.2b : accès à distance, code de rattachement de l'officine (celle du certificat) à un compte distant ; titulaire seulement,
        // mot de passe du titulaire ressaisi ; code rendu une fois, réponse non mise en cache, jamais gardé ni journalisé
        s.MapPost("/acces-distant/code", async (DemandeCodeAccesDistantDto d, GenererCodeAccesDistant generer, ClaimsPrincipal user, HttpContext ctx,
            CancellationToken ct) =>
        {
            ctx.Response.Headers.CacheControl = "no-store";
            var r = await generer.ExecuterAsync(d.Compte, d.MotDePasse, user.UtilisateurId(), ct);
            return Results.Ok(new CodeAccesDistantDto(r.Code, r.ExpireLeUtc));
        });

        // Lot S7.3 : comptes distants rattachés à l'officine, droits (dont « ordres », accordé seulement d'ici) et retrait ; titulaire
        // seulement, mot de passe ressaisi pour un changement (cas d'usage ComptesAccesDistant) ; réponses non mises en cache
        s.MapGet("/acces-distant/comptes", async (ComptesAccesDistant comptes, ClaimsPrincipal user, HttpContext ctx, CancellationToken ct) =>
        {
            ctx.Response.Headers.CacheControl = "no-store";
            var liste = await comptes.ListerAsync(user.UtilisateurId(), ct);
            return Results.Ok(liste.Select(c => new CompteDistantRattacheDto(c.Identifiant, c.Nom, c.Droits,
                new DateTimeOffset(DateTime.SpecifyKind(c.RattacheLeUtc, DateTimeKind.Utc)), c.Actif)).ToList());
        });
        s.MapPost("/acces-distant/droits", async (DemandeDroitsAccesDistantDto d, ComptesAccesDistant comptes, ClaimsPrincipal user, CancellationToken ct) =>
            Results.Ok(new ReponseCompteDistantDto(await comptes.ModifierDroitsAsync(d.Compte, d.Droits, d.MotDePasse, user.UtilisateurId(), ct))));
        s.MapPost("/acces-distant/retrait", async (DemandeRetraitAccesDistantDto d, ComptesAccesDistant comptes, ClaimsPrincipal user, CancellationToken ct) =>
            Results.Ok(new ReponseCompteDistantDto(await comptes.RetirerAsync(d.Compte, d.MotDePasse, user.UtilisateurId(), ct))));

        // Lot S6.5.4 (S6.5-D4) : prise d'acte du titulaire d'une restauration constatée par le serveur central (cause), journalisée,
        // envoyée au central ; une fois par époque
        s.MapPost("/integrite/prise-acte", async (PriseActeRestaurationDto d, PrendreActeRestaurationSync prendre, ClaimsPrincipal user, CancellationToken ct) =>
        {
            await prendre.ExecuterAsync(d.NouvelleEpoque, d.Cause, user.UtilisateurId(), ct);
            return Results.NoContent();
        });

        // Lot S6 : rechargement complet demandé par le titulaire (traité par le passage d'envoi suivant)
        s.MapPost("/recharger", async (RechargerSync recharger, ClaimsPrincipal user, CancellationToken ct) =>
        {
            var n = await recharger.ExecuterAsync(user.UtilisateurId(), ct);
            return Results.Ok(new { numero = n });
        });

        s.MapPut("/reglages", async (ReglagesSynchronisationDto d, ReglerSync regler, ClaimsPrincipal user, CancellationToken ct) =>
        {
            await regler.ExecuterAsync(new ReglagesSync(d.Active, d.Adresse, d.TextesLibres, d.EnvoyerMedecins, d.DelaiControleDemarrage), user.UtilisateurId(), ct);
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
            // Envoi suspendu (base revenue en arrière) : réponse normale, le poste montre le message comme un avertissement
            if (r.Suspendu) return Results.Ok(new ResultatEnvoiSynchronisationDto(r.LotsAccuses, null, null, r.Erreur));
            if (r.Erreur is not null) return Results.Problem(r.Erreur, statusCode: StatusCodes.Status502BadGateway);
            return Results.Ok(new ResultatEnvoiSynchronisationDto(r.LotsAccuses, r.Arret, r.Erreur));
        });

        // Lots S5.1 et S5.5 : ordres reçus du serveur central (liste : admin.parametres ; confirmation et refus : titulaire seul)
        s.MapGet("/ordres", async (string? statut, string? type, int? page, int? taille, IOrdresCentrauxDepot ordres,
            Pharma.Application.Administration.IUtilisateursAdmin admin, ReglagesCommunsOrdre reglagesCommuns, CancellationToken ct) =>
        {
            if (statut is not null && !StatutsOrdre.Tous.Contains(statut) && statut != StatutsOrdre.Traite) return Results.Problem("État d’ordre inconnu.", statusCode: 400);
            if (type is not null && !TypesOrdre.Connus.Contains(type)) return Results.Problem("Type d’ordre inconnu.", statusCode: 400);
            var demande = Pharma.Application.Commun.DemandePage.Lire(page, taille);
            var p = await ordres.ListerAsync(statut, demande.Page, demande.Taille, type, ct);
            var etat = await admin.LireAsync(ct);
            var lignes = new List<OrdreCentralDto>();
            foreach (var o in p.Ordres)
            {
                // Lot S7.4 : réglages communs à décider : avant (valeur actuelle de l'officine) / après (valeur proposée), relus à chaque affichage
                List<ComparaisonReglageDto>? comparaison = null;
                if (o.Type == TypesOrdre.Reglages && o.Statut == StatutsOrdre.AConfirmer)
                {
                    try { comparaison = (await reglagesCommuns.ComparerAsync(o, ct)).Select(c => new ComparaisonReglageDto(c.Libelle, c.Avant, c.Apres, c.Unite, c.Change)).ToList(); }
                    catch (OrdreInapplicableException) { comparaison = []; }
                }
                lignes.Add(new OrdreCentralDto(o.Id, o.Type, TypesOrdre.Libelle(o.Type), ResumeOrdre.Texte(o, etat), o.Auteur,
                    o.CreeLeUtc, o.ExpireLeUtc, o.Statut, o.Motif, o.RecuLeUtc, o.TraiteLeUtc, o.Type == TypesOrdre.EmployeCreer,
                    TypesOrdre.MotDePasseTitulaireRequis(o.Type), comparaison, Proposes(o),
                    o.TraitePar is { } par ? etat.Utilisateurs.FirstOrDefault(u => u.Id == par)?.NomComplet : null));
            }
            return Results.Ok(new PageOrdresCentrauxDto(new PageDto<OrdreCentralDto>(lignes, p.Total, demande.Page, demande.Taille), p.AConfirmer));
        });

        s.MapPost("/ordres/{id:guid}/confirmer", async (Guid id, ConfirmerOrdreCentralDto d, ConfirmerOrdreCentral confirmer, ClaimsPrincipal user,
            CancellationToken ct) =>
        {
            await confirmer.ExecuterAsync(id, d.MotDePasse, d.MotDePasseTitulaire, user.UtilisateurId(), ct);
            return Results.NoContent();
        });

        s.MapPost("/ordres/{id:guid}/refuser", async (Guid id, RefuserOrdreCentralDto d, RefuserOrdreCentral refuser, ClaimsPrincipal user,
            CancellationToken ct) =>
        {
            await refuser.ExecuterAsync(id, d.Motif, d.MotDePasseTitulaire, user.UtilisateurId(), ct);
            return Results.NoContent();
        });
    }

    /// <summary>Lot S7.4 : valeurs proposées d'un ordre de réglages communs (null pour les autres types, ou illisibles).</summary>
    private static List<ReglageProposeDto>? Proposes(OrdreRecu o)
    {
        if (o.Type != TypesOrdre.Reglages) return null;
        var (valeurs, erreurs) = Pharma.Domain.Reglages.ReglagesCommuns.Lire(o.DonneesJson);
        return erreurs.Count > 0 ? [] : valeurs.Select(v => Pharma.Domain.Reglages.ReglagesCommuns.Trouver(v.Key)!)
            .Select(r => new ReglageProposeDto(r.Libelle, valeurs[r.Cle], r.Unite)).ToList();
    }

    private static AnomalieLiaisonDto? Dto(AnomalieLiaison? a) => a is null ? null : new AnomalieLiaisonDto(a.Libelle, a.Niveau, a.Detail);
}
