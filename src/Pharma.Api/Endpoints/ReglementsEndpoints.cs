using System.Security.Claims;
using Pharma.Api.Securite;
using Pharma.Application.Achats;
using Pharma.Application.Caisse;
using Pharma.Domain.Securite;
using Pharma.Shared;

namespace Pharma.Api.Endpoints;

/// <summary>Règlements : grossistes (droit achats.regler) et crédits clients au comptoir (caisse.vendre).</summary>
internal static class ReglementsEndpoints
{
    public static void MapReglements(this RouteGroupBuilder api)
    {
        var grossistes = api.MapGroup("/achats/reglements").ExigerPermission(Permissions.AchatsRegler);

        grossistes.MapGet("", async (ConsulterReglements consulter, ClaimsPrincipal user, CancellationToken ct) =>
            Results.Ok((await consulter.GrossistesAsync(user.UtilisateurId(), ct))
                .Select(g => new GrossisteAReglementDto(g.Id, g.Nom, g.Solde, g.ResteFactures, g.FacturesOuvertes))));

        grossistes.MapGet("/{id:long}", async (long id, ConsulterReglements consulter, ClaimsPrincipal user, CancellationToken ct) =>
            {
                var s = await consulter.SituationAsync(id, user.UtilisateurId(), ct);
                return Results.Ok(new SituationGrossisteDto(s.Id, s.Nom, s.Solde,
                    s.Factures.Select(f => new FactureFournisseurDto(f.ReceptionId, f.NumeroFacture, f.NumeroBl, f.Date, f.Montant, f.Regle, f.Reste)).ToList(),
                    s.Reglements.Select(r => new ReglementPasseDto(r.Id, r.Date, r.Montant, r.Moyen, r.Reference, r.Echeance, r.PartTiroir, r.Employe)).ToList()));
            });

        grossistes.MapPost("/{id:long}", async (long id, ReglementRequete req, EnregistrerReglement enregistrer, ClaimsPrincipal user,
                HttpRequest requete, CancellationToken ct) =>
                Results.Ok(new IdentifiantDto(await enregistrer.ExecuterAsync(id, req.Date, req.Montant, req.Moyen, req.Reference, req.Banque,
                    req.Echeance, req.PartTiroir, req.Commentaire, req.Imputations?.Select(i => new ImputationDemandee(i.ReceptionId, i.Montant)).ToList(),
                    user.UtilisateurId(), requete.Poste(), req.CompteId, ct))));

        var credits = api.MapGroup("/caisse/credits").ExigerPermission(Permissions.CaisseVendre);

        credits.MapGet("", async (string? q, ConsulterCredits consulter, CancellationToken ct) =>
            Results.Ok((await consulter.ListerAsync(q, ct)).Select(VersDto)));

        credits.MapGet("/{patientId:long}", async (long patientId, ConsulterCredits consulter, CancellationToken ct) =>
            {
                var s = await consulter.SituationAsync(patientId, ct);
                return Results.Ok(new SituationCreditDto(VersDto(s.Patient), s.Ventes.Select(v => new VenteACreditDto(v.Numero, v.Date, v.Montant)).ToList()));
            });

        credits.MapPost("/{patientId:long}/reglement", async (long patientId, ReglementCreditRequete req, EncaisserReglementCredit encaisser,
                ClaimsPrincipal user, HttpRequest requete, CancellationToken ct) =>
            {
                var r = await encaisser.ExecuterAsync(patientId, req.Montant, req.Mode, user.UtilisateurId(), requete.Poste(), ct);
                return Results.Ok(new RecuCreditDto(r.Numero, r.Patient, r.Montant, r.Mode, r.ResteDu));
            });
    }

    private static PatientEnCreditDto VersDto(PatientEnCredit p) => new(p.Id, p.Nom, p.Telephone, p.Solde, p.Plafond, p.DerniereVente);
}
