using System.Security.Claims;
using Pharma.Api.Securite;
using Pharma.Application.Caisse;
using Pharma.Domain.Commun;
using Pharma.Domain.Securite;
using Pharma.Domain.Ventes;
using Pharma.Shared;

namespace Pharma.Api.Endpoints;

internal static class CaisseEndpoints
{
    public static void MapCaisse(this RouteGroupBuilder api)
    {
        var caisse = api.MapGroup("/caisse");

        caisse.MapGet("/etat", async (ObtenirEtatCaisse obtenir, ClaimsPrincipal user, CancellationToken ct) =>
            {
                var e = await obtenir.ExecuterAsync(user.UtilisateurId(), ct);
                return Results.Ok(new EtatCaisseDto(e.SessionId, e.OuverteLe, e.FondCaisse, e.Poste,
                    e.Parametres.ComptageAveugle, e.Parametres.FondCaisseDefaut, e.Parametres.SeuilEcart,
                    e.Parametres.DeconnexionMinutes, e.PeutVoirEcart, e.VentesEspecesVisibles));
            })
           .ExigerPermission(Permissions.CaisseVendre);

        caisse.MapPost("/ouvrir", async (OuvrirCaisseRequete req, OuvrirSessionCaisse ouvrir, ClaimsPrincipal user, CancellationToken ct) =>
                Results.Ok(new IdentifiantDto(await ouvrir.ExecuterAsync(user.UtilisateurId(), req.Poste, req.FondCaisse, ct))))
           .ExigerPermission(Permissions.CaisseVendre);

        caisse.MapPost("/calculer", async (CommandeVenteDto req, Encaisser encaisser, ClaimsPrincipal user, CancellationToken ct) =>
            {
                var a = await encaisser.CalculerAsync(VersCommande(req), user.UtilisateurId(), ct);
                return Results.Ok(new ApercuVenteDto(a.MontantBrut, a.PartOrganisme, a.MontantRemise, a.PartPatient, a.MontantTotal, a.OrdonnanceRequise));
            })
           .ExigerPermission(Permissions.CaisseVendre);

        caisse.MapPost("/encaisser", async (CommandeVenteDto req, Encaisser encaisser, ClaimsPrincipal user, CancellationToken ct) =>
            {
                var v = await encaisser.ExecuterAsync(VersCommande(req), user.UtilisateurId(), ct);
                return Results.Ok(new VenteEncaisseeDto(v.VenteId, v.Numero, v.MontantTotal, v.PartOrganisme, v.PartPatient, v.Mode.Code(), v.Rendu));
            })
           .ExigerPermission(Permissions.CaisseVendre);

        caisse.MapPost("/cloturer", async (ClotureRequete req, CloturerSessionCaisse cloturer, ClaimsPrincipal user, CancellationToken ct) =>
            {
                var c = await cloturer.ExecuterAsync(user.UtilisateurId(), req.Saisies, ct);
                return Results.Ok(new ClotureDto(c.SessionId, c.Compte, c.FondCaisse, c.VentesEspeces, c.Attendu, c.Ecart, c.AuDelaDuSeuil, c.SeuilEcart));
            })
           .ExigerPermission(Permissions.CaisseVendre);

        caisse.MapGet("/sessions/{id:long}/rapport", async (long id, ObtenirRapportCloture rapport, ClaimsPrincipal user, CancellationToken ct) =>
                await rapport.ExecuterAsync(id, user.UtilisateurId(), ct) is { } r
                    ? Results.File(r.Contenu, "application/pdf", r.NomFichier)
                    : Results.NotFound(new ErreurDto("Session de caisse introuvable.")))
           .ExigerPermission(Permissions.CaisseVendre);
    }

    private static CommandeVente VersCommande(CommandeVenteDto d)
    {
        ModePaiement mode;
        try { mode = CodesMetier.ModePaiementDepuisCode(d.Mode); }
        catch (ArgumentException) { throw new RegleMetierException($"Mode de paiement inconnu : {d.Mode}."); }

        return new CommandeVente(
            d.Lignes.Select(l => new LigneCommande(l.ProduitId, l.LotId, l.Quantite)).ToList(),
            d.TiersPayant, d.OrganismeId, d.TauxTiersPayant, d.PatientId,
            d.Ordonnance is { } o ? new OrdonnanceSaisie(o.MedecinId, o.DatePrescription, o.Numero, o.JetonScan, o.TexteReconnu) : null,
            d.RemisePct, mode, d.MontantRecu);
    }
}
