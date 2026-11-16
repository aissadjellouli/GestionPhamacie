using System.Security.Claims;
using Pharma.Api.Securite;
using Pharma.Application.Achats;
using Pharma.Application.Catalogue;
using Pharma.Application.Commun;
using Pharma.Domain.Securite;
using Pharma.Shared;

namespace Pharma.Api.Endpoints;

/// <summary>
/// Ruptures et substitution (lot 7, M3b) : ruptures chez les grossistes (historique noté à l'import des catalogues), patients en
/// attente d'un produit (alerte au comptoir, SMS avec accord du patient), paramètre « équivalent exact seulement », liste blanche,
/// fabriqué en Algérie.
/// </summary>
internal static class RupturesEndpoints
{
    public static void MapRuptures(this RouteGroupBuilder api)
    {
        var a = api.MapGroup("/achats/ruptures");
        a.MapGet("/partout", async (string? recherche, int? page, int? taille, ConsulterRuptures c, CancellationToken ct) =>
                Results.Ok(VersDto((await c.PartoutAsync(recherche, DemandePage.Lire(page, taille), ct)).Vers(r =>
                    new RuptureProduitDto(r.ProduitId, r.Produit, r.Grossistes, r.Depuis, r.StockOfficine, r.PatientsEnAttente)))))
           .ExigerPermission(Permissions.AchatsCommander);
        a.MapGet("/historique", async (string? recherche, string? etat, int? page, int? taille, ConsulterRuptures c, CancellationToken ct) =>
                Results.Ok(VersDto((await c.HistoriqueAsync(recherche, etat, DemandePage.Lire(page, taille), ct)).Vers(r =>
                    new PeriodeRuptureDto(r.Id, r.ProduitId, r.Produit, r.FournisseurId, r.Grossiste, r.Debut, r.Fin)))))
           .ExigerPermission(Permissions.AchatsCommander);

        var d = api.MapGroup("/caisse/demandes-patients");
        d.MapGet("", async (string? recherche, string? statut, int? page, int? taille, ConsulterDemandesPatients c, CancellationToken ct) =>
                Results.Ok(VersDto((await c.PageAsync(recherche, statut, DemandePage.Lire(page, taille), ct)).Vers(VersDto))))
           .ExigerPermission(Permissions.CaisseVendre);
        d.MapGet("/alertes", async (ConsulterDemandesPatients c, CancellationToken ct) =>
            {
                var al = await c.AlertesAsync(ct);
                return Results.Ok(new AlertesDisponibiliteDto(al.Patients.Select(VersDto).ToList(),
                    al.VentesEnAttente.Select(v => new ProduitDuEnAttenteDto(v.PanierId, v.Libelle, v.ProduitId, v.Produit, v.Quantite, v.CreeLe,
                        v.StockOfficine, v.DisponibleGrossiste)).ToList()));
            })
           .ExigerPermission(Permissions.CaisseVendre);
        d.MapPost("", async (NouvelleDemandePatientRequete r, DemanderPrevenirPatient demander, ClaimsPrincipal user, CancellationToken ct) =>
                Results.Ok(await demander.ExecuterAsync(r.PatientId, r.ProduitId, r.Quantite, user.UtilisateurId(), ct)))
           .ExigerPermission(Permissions.CaisseVendre);
        d.MapPost("/{id:long}/prevenir", async (long id, PrevenirPatientRequete r, PrevenirPatient prevenir, ClaimsPrincipal user, CancellationToken ct) =>
                Results.Ok(await prevenir.ExecuterAsync(id, r.ParSms, user.UtilisateurId(), ct)))
           .ExigerPermission(Permissions.CaisseVendre);
        d.MapPost("/{id:long}/cloturer", async (long id, CloturerDemandeRequete r, CloturerDemandePatient cloturer, ClaimsPrincipal user, CancellationToken ct) =>
            {
                await cloturer.ExecuterAsync(id, r.Servie, user.UtilisateurId(), ct);
                return Results.NoContent();
            })
           .ExigerPermission(Permissions.CaisseVendre);

        api.MapGet("/patients/{id:long}/consentement-sms", async (long id, ConsentementSmsPatient c, FormulaireAccordSmsPatient f, CancellationToken ct) =>
            {
                var (accord, le) = await c.LireAsync(id, ct);
                var (recu, recuLe) = await f.LireRecuAsync(id, ct);
                return Results.Ok(new ConsentementSmsDto(accord, le, recu, recuLe));
            })
           .ExigerPermission(Permissions.CaisseVendre);
        // Formulaire d'accord SMS (2026-11-16) : impression, « formulaire signé reçu », texte réglé par le titulaire
        api.MapGet("/patients/{id:long}/formulaire-sms.pdf", async (long id, FormulaireAccordSmsPatient f, ClaimsPrincipal user, CancellationToken ct) =>
            {
                var (pdf, nom) = await f.ImprimerAsync(id, user.UtilisateurId(), ct);
                return Results.File(pdf, "application/pdf", nom);
            })
           .ExigerPermission(Permissions.CaisseVendre);
        api.MapPut("/patients/{id:long}/formulaire-sms", async (long id, FormulaireSmsRecuRequete r, FormulaireAccordSmsPatient f, ClaimsPrincipal user, CancellationToken ct) =>
            {
                await f.EnregistrerRecuAsync(id, r.Recu, user.UtilisateurId(), ct);
                return Results.NoContent();
            })
           .ExigerPermission(Permissions.CaisseVendre);
        api.MapGet("/parametres/formulaire-sms", async (FormulaireAccordSmsPatient f, CancellationToken ct) =>
            {
                var (texte, parDefaut) = await f.TexteAsync(ct);
                return Results.Ok(new TexteFormulaireSmsDto(texte, parDefaut));
            })
           .ExigerPermission(Permissions.AdminParametres)
           .AvecEtatReglageCentral(Pharma.Domain.Reglages.TablesMutualisables.FormulaireSms);
        api.MapPut("/parametres/formulaire-sms", async (TexteFormulaireSmsDto r, FormulaireAccordSmsPatient f, ClaimsPrincipal user, CancellationToken ct) =>
            {
                await f.ModifierTexteAsync(r.Texte, user.UtilisateurId(), ct);
                return Results.NoContent();
            })
           .ExigerPermission(Permissions.AdminParametres);
        api.MapPut("/patients/{id:long}/consentement-sms", async (long id, ConsentementSmsDto r, ConsentementSmsPatient c, ClaimsPrincipal user, CancellationToken ct) =>
            {
                await c.EnregistrerAsync(id, r.Accord, user.UtilisateurId(), ct);
                return Results.NoContent();
            })
           .ExigerPermission(Permissions.CaisseVendre);

        var s = api.MapGroup("/substitution");
        s.MapGet("/parametre", async (ConsulterParametreSubstitution c, CancellationToken ct) =>
            {
                var p = await c.ExecuterAsync(ct);
                return Results.Ok(new ParametreSubstitutionDto(p.EquivalentExactSeulement, p.ModifieLe, p.ModifiePar));
            })
           .AvecEtatReglageCentral(Pharma.Domain.Reglages.TablesMutualisables.Substitution);
        s.MapPut("/parametre", async (ParametreSubstitutionDto r, ModifierParametreSubstitution m, ClaimsPrincipal user, CancellationToken ct) =>
            {
                await m.ExecuterAsync(r.EquivalentExactSeulement, user.UtilisateurId(), ct);
                return Results.NoContent();
            })
           .ExigerPermission(Permissions.AdminParametres);
        s.MapGet("/liste-blanche", async (string? recherche, int? page, int? taille, GererListeBlanche g, CancellationToken ct) =>
                Results.Ok(VersDto((await g.PageAsync(recherche, DemandePage.Lire(page, taille), ct)).Vers(l =>
                    new LienListeBlancheDto(l.ProduitId, l.Produit, l.RemplacantId, l.Remplacant, l.CreeLe, l.CreePar)))))
           .ExigerPermission(Permissions.AdminParametres);
        s.MapPost("/liste-blanche", async (LienListeBlancheRequete r, GererListeBlanche g, ClaimsPrincipal user, CancellationToken ct) =>
            {
                await g.AjouterAsync(r.ProduitId, r.RemplacantId, user.UtilisateurId(), ct);
                return Results.NoContent();
            })
           .ExigerPermission(Permissions.AdminParametres);
        s.MapDelete("/liste-blanche/{produitId:long}/{remplacantId:long}", async (long produitId, long remplacantId, GererListeBlanche g,
                ClaimsPrincipal user, CancellationToken ct) =>
            {
                await g.RetirerAsync(produitId, remplacantId, user.UtilisateurId(), ct);
                return Results.NoContent();
            })
           .ExigerPermission(Permissions.AdminParametres);
        s.MapGet("/fabrication", async (string? recherche, bool? locale, int? page, int? taille, GererFabricationLocale g, CancellationToken ct) =>
                Results.Ok(VersDto((await g.PageAsync(recherche, locale, DemandePage.Lire(page, taille), ct)).Vers(f =>
                    new ProduitFabricationDto(f.ProduitId, f.Produit, f.Laboratoire, f.Saisie, f.Nomenclature)))))
           .ExigerPermission(Permissions.AdminParametres);
        s.MapPut("/fabrication/{produitId:long}", async (long produitId, FabricationLocaleRequete r, GererFabricationLocale g, ClaimsPrincipal user,
                CancellationToken ct) =>
            {
                await g.ModifierAsync(produitId, r.FabricationLocale, user.UtilisateurId(), ct);
                return Results.NoContent();
            })
           .ExigerPermission(Permissions.AdminParametres);
    }

    private static DemandePatientDto VersDto(DemandePatient d) => new(d.Id, d.PatientId, d.Patient, d.Telephone, d.ConsentementSms, d.ConsentementSmsLe,
        d.ProduitId, d.Produit, d.Quantite, d.CreeLe, d.Statut, d.PrevenuLe, d.PrevenuCanal, d.SmsErreur, d.StockOfficine, d.DisponibleGrossiste, d.Disponible);

    private static PageDto<T> VersDto<T>(Page<T> p) => new(p.Lignes, p.Total, p.Numero, p.Taille);
}
