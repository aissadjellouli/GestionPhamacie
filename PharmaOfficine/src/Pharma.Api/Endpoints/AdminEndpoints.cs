using System.Security.Claims;
using Pharma.Api.Securite;
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
                return Results.Ok(new ParametresCaisseDto(p.ComptageAveugle, p.FondCaisseDefaut, p.SeuilEcart, p.DeconnexionMinutes));
            })
           .ExigerPermission(Permissions.AdminParametres);

        admin.MapPut("/caisse", async (ParametresCaisseDto req, ModifierParametresCaisse modifier, ClaimsPrincipal user, CancellationToken ct) =>
            {
                await modifier.ExecuterAsync(new ParametresCaisse(req.ComptageAveugle, req.FondCaisseDefaut, req.SeuilEcart, req.DeconnexionMinutes),
                    user.UtilisateurId(), ct);
                return Results.NoContent();
            })
           .ExigerPermission(Permissions.AdminParametres);

        admin.MapGet("/journal", async (ConsulterJournal consulter, CancellationToken ct) =>
                Results.Ok((await consulter.ExecuterAsync(ct)).Select(e =>
                    new EntreeJournalDto(e.Date, e.Employe, e.Action, e.DetailsJson, e.AutorisePar))))
           .ExigerPermission(Permissions.AdminParametres);

        // ---- Alertes et seuils ----
        admin.MapGet("/seuils", async (ObtenirSeuils obtenir, CancellationToken ct) =>
                Results.Ok(VersDto(await obtenir.ExecuterAsync(ct))))
           .ExigerPermission(Permissions.AdminParametres);

        admin.MapPut("/seuils", async (ReglagesSeuilsDto req, EnregistrerSeuils enregistrer, ClaimsPrincipal user, CancellationToken ct) =>
            {
                var g = req.General;
                var reglages = new ReglagesSeuils(
                    new ParametresAlerte(g.JoursOrange, g.JoursRouge, g.HorizonRisqueJours, g.DelaiFournisseurJours, g.JoursSecurite, g.JoursCibleCommande, g.JoursDormant),
                    req.Grossistes.Select(f => new SeuilGrossiste(f.FournisseurId, f.Nom, f.DelaiRepriseJours, f.JoursOrange, f.JoursRouge)).ToList(),
                    req.Exceptions.Select(e => new ExceptionProduit(e.ProduitId, e.Nom, e.JoursOrange, e.JoursRouge)).ToList());
                return Results.Ok(new EnregistrementSeuilsDto(await enregistrer.ExecuterAsync(reglages, user.UtilisateurId(), ct)));
            })
           .ExigerPermission(Permissions.AdminParametres);

        // ---- Utilisateurs et droits ----
        admin.MapGet("/droits", async (ObtenirDroits obtenir, CancellationToken ct) =>
            {
                var e = await obtenir.ExecuterAsync(ct);
                return Results.Ok(new EtatDroitsDto(
                    e.Utilisateurs.Select(u => new UtilisateurAdminDto(u.Id, u.NomComplet, u.Identifiant, u.RoleId, u.Role, u.RoleSysteme, u.Actif, u.DerniereConnexion)).ToList(),
                    e.Roles.Select(r => new RoleAdminDto(r.Id, r.Code, r.Nom, r.Systeme, r.RemiseMaxPct, r.Permissions)).ToList(),
                    e.Permissions.Select(p => new PermissionAdminDto(p.Code, p.Module, p.Libelle, p.Sensible)).ToList()));
            })
           .ExigerPermission(Permissions.AdminUtilisateurs);

        admin.MapPut("/droits", async (List<DroitsRoleDto> req, EnregistrerDroits enregistrer, ClaimsPrincipal user, CancellationToken ct) =>
            {
                await enregistrer.ExecuterAsync(req.Select(d => new DroitsRole(d.RoleId, d.RemiseMaxPct, d.Permissions)).ToList(), user.UtilisateurId(), ct);
                return Results.NoContent();
            })
           .ExigerPermission(Permissions.AdminUtilisateurs);

        admin.MapPost("/utilisateurs", async (NouvelUtilisateurDto req, CreerUtilisateur creer, ClaimsPrincipal user, CancellationToken ct) =>
                Results.Ok(new IdentifiantDto(await creer.ExecuterAsync(req.NomComplet, req.Identifiant, req.MotDePasse, req.RoleId, user.UtilisateurId(), ct))))
           .ExigerPermission(Permissions.AdminUtilisateurs);

        admin.MapPut("/utilisateurs/{id:long}", async (long id, ModificationUtilisateurDto req, ModifierUtilisateur modifier, ClaimsPrincipal user, CancellationToken ct) =>
            {
                await modifier.ExecuterAsync(id, req.NomComplet, req.RoleId, req.Actif, req.MotDePasse, user.UtilisateurId(), ct);
                return Results.NoContent();
            })
           .ExigerPermission(Permissions.AdminUtilisateurs);

        // ---- Langue et pharmacie ----
        admin.MapPut("/pharmacie", async (PharmacieDto req, ModifierPharmacie modifier, ClaimsPrincipal user, CancellationToken ct) =>
            {
                await modifier.ExecuterAsync(new IdentitePharmacie(req.Nom, req.Titulaire, req.Adresse, req.Telephone, req.Nif, req.ChifaOfficine, req.LangueDefaut),
                    user.UtilisateurId(), ct);
                return Results.NoContent();
            })
           .ExigerPermission(Permissions.AdminParametres);

        // Lecture pour tout employé connecté : nom de la pharmacie dans l'en-tête des postes
        api.MapGet("/pharmacie", async (ObtenirPharmacie obtenir, CancellationToken ct) =>
            {
                var p = await obtenir.ExecuterAsync(ct);
                return Results.Ok(new PharmacieDto(p.Nom, p.Titulaire, p.Adresse, p.Telephone, p.Nif, p.ChifaOfficine, p.LangueDefaut));
            })
           .RequireAuthorization();
    }

    private static ReglagesSeuilsDto VersDto(ReglagesSeuils s) => new(
        new ParametresAlerteDto(s.General.JoursOrange, s.General.JoursRouge, s.General.HorizonRisqueJours,
            s.General.DelaiFournisseurJours, s.General.JoursSecurite, s.General.JoursCibleCommande, s.General.JoursDormant),
        s.Grossistes.Select(f => new SeuilGrossisteDto(f.FournisseurId, f.Nom, f.DelaiRepriseJours, f.JoursOrange, f.JoursRouge)).ToList(),
        s.Exceptions.Select(e => new ExceptionProduitDto(e.ProduitId, e.Nom, e.JoursOrange, e.JoursRouge)).ToList());
}
