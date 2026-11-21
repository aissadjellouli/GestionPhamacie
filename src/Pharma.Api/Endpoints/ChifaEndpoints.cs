using System.Security.Claims;
using Pharma.Api.Securite;
using Pharma.Application.Chifa;
using Pharma.Application.Commun;
using Pharma.Domain.Securite;
using Pharma.Domain.Ventes;
using Pharma.Shared;

namespace Pharma.Api.Endpoints;

/// <summary>
/// Volet Chifa : compléments (2026-11-15, script 100) : centres payeurs (liste : droit de vendre ; ajout et modification : admin.parametres),
/// informations Chifa d'une vente (droit de vendre), exclusion du traitement et rétablissement (chifa.exclure), liste des ventes Chifa.
/// </summary>
internal static class ChifaEndpoints
{
    public static void MapChifaComplements(this RouteGroupBuilder api)
    {
        api.MapGet("/centres-payeurs", async (long? organismeId, bool? actif, string? q, int? page, int? taille, CentresPayeurs centres,
                ClaimsPrincipal user, CancellationToken ct) =>
            {
                var p = await centres.ListerAsync(organismeId, actif, q, DemandePage.Lire(page, taille), user.UtilisateurId(), ct);
                return Results.Ok(new PageDto<CentrePayeurDto>(p.Lignes.Select(Dto).ToList(), p.Total, p.Numero, p.Taille));
            })
           .ExigerPermission(Permissions.CaisseVendre)
           .GererErreursMetier();

        api.MapPost("/centres-payeurs", async (CentrePayeurSaisie s, CentresPayeurs centres, ClaimsPrincipal user, CancellationToken ct) =>
                Results.Ok(Dto(await centres.EnregistrerAsync(null, s.OrganismeId, s.Code, s.Nom, s.Actif, user.UtilisateurId(), ct))))
           .ExigerPermission(Permissions.AdminParametres)
           .GererErreursMetier();

        api.MapPut("/centres-payeurs/{id:long}", async (long id, CentrePayeurSaisie s, CentresPayeurs centres, ClaimsPrincipal user, CancellationToken ct) =>
                Results.Ok(Dto(await centres.EnregistrerAsync(id, s.OrganismeId, s.Code, s.Nom, s.Actif, user.UtilisateurId(), ct))))
           .ExigerPermission(Permissions.AdminParametres)
           .GererErreursMetier();

        // Ventes Chifa : exclues = non (défaut), oui, tous
        api.MapGet("/chifa/ventes", async (string? q, long? centrePayeurId, string? exclues, int? page, int? taille, ConsulterVentesChifa consulter,
                ClaimsPrincipal user, CancellationToken ct) =>
            {
                var p = await consulter.ListerAsync(new FiltreVentesChifa(q, centrePayeurId, FiltreVentesChifa.Lire(exclues)),
                    DemandePage.Lire(page, taille), user.UtilisateurId(), ct);
                return Results.Ok(new PageDto<VenteChifaDto>(p.Lignes.Select(Dto).ToList(), p.Total, p.Numero, p.Taille));
            })
           .ExigerPermission(Permissions.CaisseVendre)
           .GererErreursMetier();

        api.MapGet("/chifa/ventes/{id:long}", async (long id, ConsulterVentesChifa consulter, ClaimsPrincipal user, CancellationToken ct) =>
                Results.Ok(Dto(await consulter.LireAsync(id, user.UtilisateurId(), ct))))
           .ExigerPermission(Permissions.CaisseVendre)
           .GererErreursMetier();

        // Retours avec part organisme à corriger dans CHIFA-Officine (script 155) : etat = a_corriger (défaut), corriges, tous
        api.MapGet("/chifa/retours-a-corriger", async (string? q, string? etat, int? page, int? taille, ConsulterVentesChifa consulter,
                ClaimsPrincipal user, CancellationToken ct) =>
            {
                var p = await consulter.RetoursACorrigerAsync(new FiltreRetoursACorriger(q, FiltreRetoursACorriger.Lire(etat)),
                    DemandePage.Lire(page, taille), user.UtilisateurId(), ct);
                return Results.Ok(new PageDto<RetourACorrigerChifaDto>(p.Lignes.Select(r => new RetourACorrigerChifaDto(r.RetourId, r.RetourLe, r.RetourPar,
                    r.VenteId, r.Numero, r.DateVente, r.Patient, r.NumAssure, r.FactureChifa, r.Produits, r.PartOrganisme, r.Deposee, r.CorrigeLe,
                    r.CorrigePar)).ToList(), p.Total, p.Numero, p.Taille));
            })
           .ExigerPermission(Permissions.CaisseVendre)
           .GererErreursMetier();

        api.MapPost("/chifa/retours-a-corriger/{retourId:long}/corrige", async (long retourId, EnregistrerInfosChifa enregistrer, ClaimsPrincipal user,
                CancellationToken ct) =>
            {
                await enregistrer.MarquerRetourCorrigeAsync(retourId, user.UtilisateurId(), ct);
                return Results.NoContent();
            })
           .ExigerPermission(Permissions.CaisseVendre)
           .GererErreursMetier();

        api.MapGet("/chifa/employes", async (ConsulterVentesChifa consulter, ClaimsPrincipal user, CancellationToken ct) =>
                Results.Ok((await consulter.EmployesAsync(user.UtilisateurId(), ct)).Select(e => new EmployeChoixDto(e.Id, e.Nom)).ToList()))
           .ExigerPermission(Permissions.CaisseVendre)
           .GererErreursMetier();

        api.MapPut("/ventes/{id:long}/chifa", async (long id, ChifaInfosSaisie s, EnregistrerInfosChifa enregistrer, ClaimsPrincipal user, CancellationToken ct) =>
            {
                await enregistrer.ExecuterAsync(id, new InfosChifa(s.DateSoins, s.CentrePayeurId, s.SaisiParId, s.MajorationType, s.MajorationMontant),
                    s.RetenirCentre, user.UtilisateurId(), ct);
                return Results.NoContent();
            })
           .ExigerPermission(Permissions.CaisseVendre)
           .GererErreursMetier();

        api.MapPost("/ventes/{id:long}/chifa/exclusion", async (long id, ExclusionChifaRequete r, ExclureVenteChifa exclure, ClaimsPrincipal user, CancellationToken ct) =>
            {
                await exclure.ExclureAsync(id, r.MotifCode, r.Texte, user.UtilisateurId(), ct);
                return Results.NoContent();
            })
           .ExigerPermission(Permissions.ChifaExclure)
           .GererErreursMetier();

        api.MapDelete("/ventes/{id:long}/chifa/exclusion", async (long id, ExclureVenteChifa exclure, ClaimsPrincipal user, CancellationToken ct) =>
            {
                await exclure.RetablirAsync(id, user.UtilisateurId(), ct);
                return Results.NoContent();
            })
           .ExigerPermission(Permissions.ChifaExclure)
           .GererErreursMetier();

        // Conflits de n° de facture Chifa (script 101, fusion vers chifa_facture) : titulaire
        api.MapGet("/chifa/conflits-facture", async (bool? aTrancher, ConflitsFactureChifa conflits, ClaimsPrincipal user, CancellationToken ct) =>
                Results.Ok((await conflits.ListerAsync(aTrancher ?? true, user.UtilisateurId(), ct))
                    .Select(c => new ConflitFactureDto(c.VenteId, c.NumeroVente, c.DateVente, c.AncienNumero, c.NumeroActuel, c.ValeurRetenue,
                        c.TrancheLe, c.TranchePar)).ToList()))
           .ExigerTitulaire()
           .GererErreursMetier();

        api.MapPost("/chifa/conflits-facture/{venteId:long}", async (long venteId, TrancherConflitFactureRequete r, ConflitsFactureChifa conflits,
                ClaimsPrincipal user, CancellationToken ct) =>
                await conflits.TrancherAsync(venteId, r.ValeurRetenue, user.UtilisateurId(), ct) ? Results.NoContent() : Results.NotFound())
           .ExigerTitulaire()
           .GererErreursMetier();

        MapBordereaux(api);
    }

    /// <summary>Bordereaux Chifa / MDN (script 110) : droit chifa.bordereaux pour tout (consultation, création, dépôt, rejets).</summary>
    private static void MapBordereaux(RouteGroupBuilder api)
    {
        var b = api.MapGroup("/chifa/bordereaux").ExigerPermission(Permissions.ChifaBordereaux);

        b.MapGet("", async (long? organismeId, string? etat, string? q, int? page, int? taille, GererBordereaux gerer, ClaimsPrincipal user, CancellationToken ct) =>
            {
                var p = await gerer.ListerAsync(new FiltreBordereaux(organismeId, string.IsNullOrWhiteSpace(etat) ? null : etat.Trim(), q),
                    DemandePage.Lire(page, taille), user.UtilisateurId(), ct);
                return Results.Ok(new PageDto<BordereauDto>(p.Lignes.Select(Dto).ToList(), p.Total, p.Numero, p.Taille));
            }).GererErreursMetier();

        b.MapGet("/compteurs", async (GererBordereaux gerer, ClaimsPrincipal user, CancellationToken ct) =>
            {
                var c = await gerer.CompteursAsync(user.UtilisateurId(), ct);
                return Results.Ok(new CompteursBordereauxDto(c.VentesADeposer, c.VentesEnRetard, c.MontantADeposer, c.BordereauxImpayes, c.MontantImpaye, c.RejetsATraiter));
            }).GererErreursMetier();

        b.MapGet("/a-deposer", async (long? organismeId, long? centrePayeurId, string? q, bool? retard, bool? sansCentre, int? page, int? taille,
                GererBordereaux gerer, ClaimsPrincipal user, CancellationToken ct) =>
            {
                var p = await gerer.VentesADeposerAsync(new FiltreVentesADeposer(organismeId, centrePayeurId, q, retard == true, sansCentre == true),
                    DemandePage.Lire(page, taille),
                    user.UtilisateurId(), ct);
                return Results.Ok(new PageDto<VenteADeposerDto>(p.Lignes.Select(v => new VenteADeposerDto(v.VenteId, v.Numero, v.Date, v.OrganismeId, v.Organisme,
                    v.Canal, v.CentrePayeurId, v.CentrePayeur, v.PatientId, v.Patient, v.FactureChifa, v.PartOrganisme, v.Redeposee, v.EnRetard)).ToList(),
                    p.Total, p.Numero, p.Taille));
            }).GererErreursMetier();

        b.MapGet("/{id:long}", async (long id, GererBordereaux gerer, ClaimsPrincipal user, CancellationToken ct) =>
            {
                var (bo, lignes, versements) = await gerer.FicheAsync(id, user.UtilisateurId(), ct);
                return Results.Ok(new FicheBordereauDto(Dto(bo), lignes.Select(Dto).ToList(),
                    versements.Select(v => new VersementBordereauDto(v.MouvementId, v.Date, v.Compte, v.Reference, v.Montant)).ToList()));
            }).GererErreursMetier();

        b.MapGet("/{id:long}/historique", async (long id, GererBordereaux gerer, ClaimsPrincipal user, CancellationToken ct) =>
                Results.Ok((await gerer.HistoriqueAsync(id, user.UtilisateurId(), ct))
                    .Select(h => new HistoriqueBordereauDto(h.Date, h.Employe, h.Action, h.DetailsJson)).ToList()))
            .GererErreursMetier();

        b.MapGet("/{id:long}/pdf", async (long id, GererBordereaux gerer, IDocumentsBordereau documents,
                Pharma.Application.Administration.IPharmacie pharmacie, ClaimsPrincipal user, CancellationToken ct) =>
            {
                var (bo, lignes, versements) = await gerer.FicheAsync(id, user.UtilisateurId(), ct);
                var nom = $"bordereau_{bo.Organisme}_{string.Concat(bo.Numero.Where(char.IsLetterOrDigit))}.pdf";
                return Results.File(documents.Bordereau(bo, lignes, versements, (await pharmacie.LireAsync(ct))?.Nom), "application/pdf", nom);
            }).GererErreursMetier();

        b.MapPost("", async (BordereauSaisie s, GererBordereaux gerer, ClaimsPrincipal user, CancellationToken ct) =>
                Results.Ok(Dto(await gerer.CreerAsync(s.OrganismeId, s.CentrePayeurId, s.Numero, s.PeriodeDebut, s.PeriodeFin, s.MontantDeclare,
                    user.UtilisateurId(), ct))))
            .GererErreursMetier();

        b.MapPut("/{id:long}", async (long id, BordereauSaisie s, GererBordereaux gerer, ClaimsPrincipal user, CancellationToken ct) =>
            {
                await gerer.ModifierAsync(id, s.Numero, s.PeriodeDebut, s.PeriodeFin, s.MontantDeclare, user.UtilisateurId(), ct);
                return Results.NoContent();
            }).GererErreursMetier();

        b.MapPost("/{id:long}/ventes", async (long id, AjoutVentesBordereauRequete r, GererBordereaux gerer, ClaimsPrincipal user, CancellationToken ct) =>
                Results.Ok(new IdentifiantDto(await gerer.AjouterVentesAsync(id, r.Ventes,
                    r.Toutes is { } t ? new FiltreVentesADeposer(null, t.CentrePayeurId, t.Recherche, t.RetardSeulement) : null, user.UtilisateurId(), ct))))
            .GererErreursMetier();

        b.MapPost("/{id:long}/factures", async (long id, AjoutFactureBordereauRequete r, GererBordereaux gerer, ClaimsPrincipal user, CancellationToken ct) =>
                Results.Ok(new CodeLibelleDto("vente", await gerer.AjouterParFactureAsync(id, r.FactureChifa, user.UtilisateurId(), ct))))
            .GererErreursMetier();

        b.MapDelete("/{id:long}/ventes/{venteId:long}", async (long id, long venteId, GererBordereaux gerer, ClaimsPrincipal user, CancellationToken ct) =>
            {
                await gerer.RetirerVenteAsync(id, venteId, user.UtilisateurId(), ct);
                return Results.NoContent();
            }).GererErreursMetier();

        b.MapPost("/{id:long}/depot", async (long id, DepotBordereauRequete r, GererBordereaux gerer, ClaimsPrincipal user, CancellationToken ct) =>
            {
                await gerer.DeposerAsync(id, r.Date, user.UtilisateurId(), ct);
                return Results.NoContent();
            }).GererErreursMetier();

        b.MapPost("/{id:long}/ventes/{venteId:long}/rejet", async (long id, long venteId, RejetChifaRequete r, GererBordereaux gerer, ClaimsPrincipal user,
                CancellationToken ct) =>
            {
                await gerer.RejeterAsync(id, venteId, r.MotifCode, r.Texte, r.Montant, r.Suite, user.UtilisateurId(), ct);
                return Results.NoContent();
            }).GererErreursMetier();

        b.MapPost("/{id:long}/ventes/{venteId:long}/suite", async (long id, long venteId, SuiteRejetRequete r, GererBordereaux gerer, ClaimsPrincipal user,
                CancellationToken ct) =>
            {
                await gerer.TraiterRejetAsync(id, venteId, r.Suite, user.UtilisateurId(), ct);
                return Results.NoContent();
            }).GererErreursMetier();
    }

    private static BordereauDto Dto(Bordereau b) => new(b.Id, b.OrganismeId, b.Organisme, b.Canal, b.CentrePayeurId, b.CentrePayeur, b.Numero,
        b.PeriodeDebut, b.PeriodeFin, b.DateDepot, b.Statut, RegleBordereau.Libelle(b.Statut), b.MontantDeclare, b.Ventes, b.Attendu, b.Rejete,
        b.RejetsATraiter, b.Verse, b.Reste, b.Ecart, b.CreeLe, b.CreePar, b.Impaye);

    private static LigneBordereauDto Dto(LigneBordereau l) => new(l.VenteId, l.NumeroVente, l.DateVente, l.PatientId, l.Patient, l.FactureChifa,
        l.PartOrganisme, l.AjouteLe, l.RejeteLe, l.Motif, l.MontantRejete, l.Suite, l.RejeteLe is null ? null : RegleBordereau.LibelleSuite(l.Suite),
        l.SuiteLe, l.SuitePar);

    private static CentrePayeurDto Dto(CentrePayeur c) => new(c.Id, c.OrganismeId, c.OrganismeCode, c.Code, c.Nom, c.Actif);

    private static VenteChifaDto Dto(VenteChifa v) => new(v.Id, v.Numero, v.Date, v.Statut, v.DansBordereau, v.Rattachee, v.PatientId, v.Patient, v.NumAssure,
        v.OrganismeId, v.Organisme, v.Vendeur, v.VendeurId, v.Total, v.PartOrganisme, v.FactureChifa,
        v.Infos.DateSoins, v.Infos.CentrePayeurId, v.CentrePayeur, v.Infos.SaisiPar, v.SaisiPar, v.Infos.MajorationCode, v.Majoration, v.Infos.MajorationMontant,
        v.ExclueLe, v.ExcluePar, v.Exclusion, v.CentreHabituelId);
}
