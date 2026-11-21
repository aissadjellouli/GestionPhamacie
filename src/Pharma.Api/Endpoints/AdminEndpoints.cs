using System.Security.Claims;
using Pharma.Api.Securite;
using Pharma.Application.Commun;
using Pharma.Application.Administration;
using Pharma.Application.Caisse;
using Pharma.Domain.Securite;
using Pharma.Shared;

namespace Pharma.Api.Endpoints;

internal static class AdminEndpoints
{
    public static void MapAdmin(this RouteGroupBuilder api)
    {
        var admin = api.MapGroup("/admin");

        admin.MapGet("/caisse", async (ObtenirParametresCaisse obtenir, CancellationToken ct) =>
            {
                var p = await obtenir.ExecuterAsync(ct);
                return Results.Ok(new ParametresCaisseDto(p.ComptageAveugle, p.FondCaisseDefaut, p.SeuilEcart, p.DeconnexionMinutes, p.VersementCloture, p.PlafondCreditDefaut,
                    p.SeuilAnnulations, p.AutorisationParCode, p.SeuilEcartChifa, DelaiVoletChifaJours: p.DelaiVoletChifaJours, SeuilSortieTiroir: p.SeuilSortieTiroir,
                    JoursAlerteFinDroit: p.JoursAlerteFinDroit, DelaiDepotChifaJours: p.DelaiDepotChifaJours,
                    DelaiPaiementBordereauJours: p.DelaiPaiementBordereauJours, ExigerProduitsRepriseHistorique: p.ExigerProduitsRepriseHistorique));
            })
           .ExigerPermission(Permissions.AdminParametres);

        admin.MapPut("/caisse", async (ParametresCaisseDto req, ModifierParametresCaisse modifier, ClaimsPrincipal user, CancellationToken ct) =>
            {
                await modifier.ExecuterAsync(new ParametresCaisse(req.ComptageAveugle, req.FondCaisseDefaut, req.SeuilEcart, req.DeconnexionMinutes, req.VersementCloture ?? "manuel", req.PlafondCreditDefaut,
                    req.SeuilAnnulations, req.AutorisationParCode, req.SeuilEcartChifa, DelaiVoletChifaJours: req.DelaiVoletChifaJours, SeuilSortieTiroir: req.SeuilSortieTiroir,
                    JoursAlerteFinDroit: req.JoursAlerteFinDroit, DelaiDepotChifaJours: req.DelaiDepotChifaJours,
                    DelaiPaiementBordereauJours: req.DelaiPaiementBordereauJours, ExigerProduitsRepriseHistorique: req.ExigerProduitsRepriseHistorique),
                    user.UtilisateurId(), ct);
                return Results.NoContent();
            })
           .ExigerPermission(Permissions.AdminParametres);

        // Facture et retour laissés au changement d'utilisateur (script 156) : lu par chaque poste à la connexion (tout employé
        // connecté, mot de passe provisoire compris : aucune donnée sensible), changé par le titulaire seul (journal avant / après)
        api.MapGet("/reglage-taches-laissees", async (ObtenirReglageTachesLaissees obtenir, CancellationToken ct) =>
                Results.Ok(new ReglageTachesLaisseesDto(await obtenir.ExecuterAsync(ct))))
           .ExigerConnexion(motDePasseProvisoire: true);

        admin.MapPut("/reglage-taches-laissees", async (ReglageTachesLaisseesDto req, ModifierReglageTachesLaissees modifier, ClaimsPrincipal user, CancellationToken ct) =>
            {
                await modifier.ExecuterAsync(req.Mode, user.UtilisateurId(), ct);
                return Results.NoContent();
            })
           .ExigerTitulaire();

        // Journal paginé, filtré par période et par employé
        admin.MapGet("/journal", async (DateOnly? du, DateOnly? au, string? employe, string? categorie, int? page, int? taille, ConsulterJournal consulter,
                CancellationToken ct) =>
            {
                var p = await consulter.PageAsync(du, au, employe, DemandePage.Lire(page, taille), ct, categorie);
                return Results.Ok(new PageDto<EntreeJournalDto>(
                    p.Lignes.Select(e => new EntreeJournalDto(e.Date, e.Employe, e.Action, e.DetailsJson, e.AutorisePar, e.EmployeId, e.AutoriseParId)).ToList(), p.Total, p.Numero, p.Taille));
            })
           .ExigerPermission(Permissions.AdminParametres);

        // Employés présents dans le journal : liste préremplie du filtre « Employé »
        admin.MapGet("/journal/employes", async (ConsulterJournal consulter, CancellationToken ct) => Results.Ok(await consulter.EmployesAsync(ct)))
           .ExigerPermission(Permissions.AdminParametres);

        // ---- Alertes et seuils ----
        admin.MapGet("/seuils-stock", async (ListerSeuilsStockManuels lister, CancellationToken ct) =>
                Results.Ok((await lister.ExecuterAsync(ct)).Select(VersDto)))
           .ExigerPermission(Permissions.AdminParametres);

        admin.MapGet("/seuils-stock/{produitId:long}", async (long produitId, ObtenirSeuilStock obtenir, CancellationToken ct) =>
                await obtenir.ExecuterAsync(produitId, ct) is { } s ? Results.Ok(VersDto(s)) : Results.NotFound(new ErreurDto("Produit introuvable.")))
           .ExigerPermission(Permissions.AdminParametres);

        admin.MapPut("/seuils-stock/{produitId:long}", async (long produitId, SeuilStockRequete req, DefinirSeuilStock definir, ClaimsPrincipal user, CancellationToken ct) =>
                Results.Ok(VersDto(await definir.ExecuterAsync(produitId, req.Seuil, user.UtilisateurId(), ct))))
           .ExigerPermission(Permissions.AdminParametres);

        admin.MapGet("/seuils", async (ObtenirSeuils obtenir, CancellationToken ct) =>
                Results.Ok(VersDto(await obtenir.ExecuterAsync(ct))))
           .ExigerPermission(Permissions.AdminParametres)
           .AvecEtatReglageCentral(Pharma.Domain.Reglages.TablesMutualisables.Alertes);

        admin.MapPut("/seuils", async (ReglagesSeuilsDto req, EnregistrerSeuils enregistrer, ClaimsPrincipal user, CancellationToken ct) =>
            {
                var g = req.General;
                var reglages = new ReglagesSeuils(
                    new ParametresAlerte(g.JoursOrange, g.JoursRouge, g.HorizonRisqueJours, g.DelaiFournisseurJours, g.JoursSecurite, g.JoursCibleCommande, g.JoursDormant,
                        g.SeuilLotDelaisse, g.SeuilLotDelaisseCollectif),
                    req.Grossistes.Requise().Select(f => new SeuilGrossiste(f.FournisseurId, f.Nom, f.DelaiRepriseJours, f.JoursOrange, f.JoursRouge)).ToList(),
                    req.Exceptions.Requise().Select(e => new ExceptionProduit(e.ProduitId, e.Nom, e.JoursOrange, e.JoursRouge)).ToList());
                return Results.Ok(new EnregistrementSeuilsDto(await enregistrer.ExecuterAsync(reglages, user.UtilisateurId(), ct)));
            })
           .ExigerPermission(Permissions.AdminParametres);

        // ---- Utilisateurs et droits ----
        admin.MapGet("/droits", async (ObtenirDroits obtenir, CancellationToken ct) =>
            {
                var e = await obtenir.ExecuterAsync(ct);
                return Results.Ok(new EtatDroitsDto(
                    e.Utilisateurs.Select(u => new UtilisateurAdminDto(u.Id, u.NomComplet, u.Identifiant, u.RoleId, u.Role, u.RoleSysteme, u.Actif, u.DerniereConnexion, u.CodeComptoir, u.Telephone, u.Email,
                        u.PeutDonner, u.RemiseMaxPct, u.GereRemisesAuto)).ToList(),
                    e.Roles.Select(r => new RoleAdminDto(r.Id, r.Code, r.Nom, r.Systeme, r.Permissions)).ToList(),
                    e.Permissions.Select(p => new PermissionAdminDto(p.Code, p.Module, p.Libelle, p.Sensible, p.ParPersonne)).ToList()));
            })
           .ExigerPermission(Permissions.AdminUtilisateurs);

        admin.MapPut("/droits", async (List<DroitsRoleDto> req, EnregistrerDroits enregistrer, ClaimsPrincipal user, CancellationToken ct) =>
            {
                await enregistrer.ExecuterAsync(req.Select(d => new DroitsRole(d.RoleId, d.Permissions)).ToList(), user.UtilisateurId(), ct);
                return Results.NoContent();
            })
           .ExigerPermission(Permissions.AdminUtilisateurs);

        admin.MapPost("/utilisateurs", async (NouvelUtilisateurDto req, CreerUtilisateur creer, ClaimsPrincipal user, CancellationToken ct) =>
                Results.Ok(new IdentifiantDto(await creer.ExecuterAsync(req.NomComplet, req.Identifiant, req.MotDePasse, req.RoleId, user.UtilisateurId(), req.Telephone, req.Email, ct))))
           .ExigerPermission(Permissions.AdminUtilisateurs);

        admin.MapPut("/utilisateurs/{id:long}", async (long id, ModificationUtilisateurDto req, ModifierUtilisateur modifier, ClaimsPrincipal user, CancellationToken ct) =>
            {
                await modifier.ExecuterAsync(id, req.NomComplet, req.RoleId, req.Actif, req.MotDePasse, user.UtilisateurId(), req.Telephone, req.Email, ct);
                return Results.NoContent();
            })
           .ExigerPermission(Permissions.AdminUtilisateurs);

        // Don et remise d'un employé : réglés par le titulaire seul (le cas d'usage le revérifie)
        admin.MapPut("/utilisateurs/{id:long}/don-remise", async (long id, DonRemiseEmployeDto req, ReglerDonRemise regler, ClaimsPrincipal user, CancellationToken ct) =>
            {
                await regler.ExecuterAsync(id, new DonRemiseEmploye(req.PeutDonner, req.RemiseMaxPct, req.GereRemisesAuto), user.UtilisateurId(), ct);
                return Results.NoContent();
            })
           .ExigerPermission(Permissions.AdminUtilisateurs);

        // Code comptoir oublié ou divulgué : effacé ici, l'employé en choisit un nouveau
        admin.MapDelete("/utilisateurs/{id:long}/code-comptoir", async (long id, Pharma.Application.Securite.EffacerCodeComptoir effacer, ClaimsPrincipal user, CancellationToken ct) =>
            {
                await effacer.ExecuterAsync(id, user.UtilisateurId(), ct);
                return Results.NoContent();
            })
           .ExigerPermission(Permissions.AdminUtilisateurs);

        // ---- Langue et pharmacie ----
        admin.MapPut("/pharmacie", async (PharmacieDto req, ModifierPharmacie modifier, ClaimsPrincipal user, CancellationToken ct) =>
            {
                await modifier.ExecuterAsync(new IdentitePharmacie(req.Nom, req.Titulaire, req.Adresse, req.Telephone, req.Nif, req.ChifaOfficine, req.LangueDefaut, req.Rc, req.Nis, req.ArticleImposition),
                    user.UtilisateurId(), ct);
                return Results.NoContent();
            })
           .ExigerPermission(Permissions.AdminParametres);

        // Lecture pour tout employé connecté : nom de la pharmacie dans l'en-tête des postes
        api.MapGet("/pharmacie", async (ObtenirPharmacie obtenir, CancellationToken ct) =>
            {
                var p = await obtenir.ExecuterAsync(ct);
                return Results.Ok(new PharmacieDto(p.Nom, p.Titulaire, p.Adresse, p.Telephone, p.Nif, p.ChifaOfficine, p.LangueDefaut, p.Rc, p.Nis, p.ArticleImposition));
            })
           .ExigerConnexion(motDePasseProvisoire: true, superAdmin: true);
    }

    private static SeuilStockDto VersDto(SeuilStockProduit s) =>
        new(s.ProduitId, s.Nom, s.Stock, s.VentesParJour, s.SeuilAuto, s.SeuilManuel, s.SeuilApplique, s.Statut);

    private static ReglagesSeuilsDto VersDto(ReglagesSeuils s) => new(
        new ParametresAlerteDto(s.General.JoursOrange, s.General.JoursRouge, s.General.HorizonRisqueJours,
            s.General.DelaiFournisseurJours, s.General.JoursSecurite, s.General.JoursCibleCommande, s.General.JoursDormant,
            s.General.SeuilLotDelaisse, s.General.SeuilLotDelaisseCollectif),
        s.Grossistes.Select(f => new SeuilGrossisteDto(f.FournisseurId, f.Nom, f.DelaiRepriseJours, f.JoursOrange, f.JoursRouge)).ToList(),
        s.Exceptions.Select(e => new ExceptionProduitDto(e.ProduitId, e.Nom, e.JoursOrange, e.JoursRouge)).ToList());
}
