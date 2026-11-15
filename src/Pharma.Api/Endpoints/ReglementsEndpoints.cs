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
                    s.Reglements.Select(r => new ReglementPasseDto(r.Id, r.Date, r.Montant, r.Moyen, r.Reference, r.Echeance, r.PartTiroir, r.Employe, r.EmployeId)).ToList(),
                    (s.Mouvements ?? []).Select(m => new MouvementGrossisteDto(m.Date, m.Type, m.Montant, m.SoldeApres, m.Employe, m.Reference, m.EmployeId)).ToList(), s.MouvementsTotal));
            });

        // Relevé de la dette envers un grossiste, paginé
        grossistes.MapGet("/{id:long}/mouvements", async (long id, DateOnly? du, DateOnly? au, string? type, int? page, int? taille, ConsulterReglements consulter, ClaimsPrincipal user, CancellationToken ct) =>
            {
                var p = await consulter.MouvementsAsync(id, Pharma.Application.Commun.DemandePage.Lire(page, taille), Pharma.Application.Commun.FiltreReleve.Lire(du, au, type), user.UtilisateurId(), ct);
                return Results.Ok(new PageDto<MouvementGrossisteDto>(
                    p.Lignes.Select(m => new MouvementGrossisteDto(m.Date, m.Type, m.Montant, m.SoldeApres, m.Employe, m.Reference, m.EmployeId)).ToList(), p.Total, p.Numero, p.Taille));
            });

        grossistes.MapPost("/{id:long}", async (long id, ReglementRequete req, EnregistrerReglement enregistrer, ClaimsPrincipal user,
                HttpRequest requete, CancellationToken ct) =>
                Results.Ok(new IdentifiantDto(await enregistrer.ExecuterAsync(id, req.Date, req.Montant, req.Moyen, req.Reference, req.Banque,
                    req.Echeance, req.PartTiroir, req.Commentaire, req.Imputations?.Select(i => new ImputationDemandee(i.ReceptionId, i.Montant)).ToList(),
                    user.UtilisateurId(), requete.Poste(), req.CompteId, req.Source, ct))));

        // Traites à payer (échues ou à 7 jours) ; le paiement est confirmé par le titulaire
        var traites = api.MapGroup("/achats/traites").ExigerPermission(Permissions.AchatsRegler);

        // Filtrée et paginée par le serveur : état (a_payer par défaut, echues, a_venir, payees, toutes), grossiste ou n° de traite
        traites.MapGet("", async (string? etat, string? q, int? page, int? taille, ConsulterTraites consulter, ClaimsPrincipal user, CancellationToken ct) =>
            {
                var t = await consulter.ExecuterAsync(user.UtilisateurId(), etat, q, Pharma.Application.Commun.DemandePage.Lire(page, taille), ct);
                var p = t.Traites;
                return Results.Ok(new TraitesAPayerDto(new PageDto<TraiteAPayerDto>(p.Lignes.Select(x => new TraiteAPayerDto(x.ReglementId, x.Numero,
                    x.FournisseurId, x.Grossiste, x.Reference, x.BanqueGrossiste, x.DateRemise, x.Echeance, x.Montant, x.CompteId, x.Banque,
                    x.PayeeLe, x.MoyenPaiement)).ToList(), p.Total, p.Numero, p.Taille), t.SoldeCoffre));
            });

        traites.MapPost("/{id:long}/paiement", async (long id, PaiementTraiteRequete req, PayerTraite payer, ClaimsPrincipal user,
                HttpRequest requete, CancellationToken ct) =>
                Results.Ok(new IdentifiantDto(await payer.ExecuterAsync(id, req.Date, req.Moyen, req.CompteId, req.Reference, req.PartTiroir,
                    user.UtilisateurId(), requete.Poste(), req.Source, ct))))
           .ExigerTitulaire();

        var credits = api.MapGroup("/caisse/credits").ExigerPermission(Permissions.CaisseVendre);

        credits.MapGet("", async (string? q, ConsulterCredits consulter, CancellationToken ct) =>
            Results.Ok((await consulter.ListerAsync(q, ct)).Select(VersDto)));

        // Patients en crédit par page (règle des listes)
        credits.MapGet("/page", async (string? q, int? page, int? taille, ConsulterCredits consulter, CancellationToken ct) =>
            {
                var p = await consulter.PageAsync(q, Pharma.Application.Commun.DemandePage.Lire(page, taille), ct);
                return Results.Ok(new PageDto<PatientEnCreditDto>(p.Lignes.Select(VersDto).ToList(), p.Total, p.Numero, p.Taille));
            });

        credits.MapGet("/{patientId:long}", async (long patientId, ConsulterCredits consulter, CancellationToken ct) =>
            {
                var s = await consulter.SituationAsync(patientId, ct);
                return Results.Ok(new SituationCreditDto(VersDto(s.Patient),
                    s.Ventes.Select(v => new VenteACreditDto(v.Numero, v.Date, v.Montant, v.Regle, v.Reste, v.Id, v.Facture, v.Remise)).ToList(),
                    (s.Mouvements ?? []).Select(m => new MouvementCreditDto(m.Date, m.Type, m.Montant, m.SoldeApres, m.Vente, m.Employe, m.Poste, m.Reference, m.EmployeId, m.Remise)).ToList(),
                    s.DetteReprise, s.MouvementsTotal));
            });

        // Relevé de dette paginé (mouvements plus anciens que la première page de la fiche)
        credits.MapGet("/{patientId:long}/mouvements", async (long patientId, DateOnly? du, DateOnly? au, string? type, int? page, int? taille, ConsulterCredits consulter, CancellationToken ct) =>
            {
                var p = await consulter.MouvementsAsync(patientId, Pharma.Application.Commun.DemandePage.Lire(page, taille), Pharma.Application.Commun.FiltreReleve.Lire(du, au, type), ct);
                return Results.Ok(new PageDto<MouvementCreditDto>(
                    p.Lignes.Select(m => new MouvementCreditDto(m.Date, m.Type, m.Montant, m.SoldeApres, m.Vente, m.Employe, m.Poste, m.Reference, m.EmployeId, m.Remise)).ToList(),
                    p.Total, p.Numero, p.Taille));
            });

        // Relevé de la dette à remettre au patient
        credits.MapGet("/{patientId:long}/releve", async (long patientId, ImprimerReleveCredit imprimer, CancellationToken ct) =>
            {
                var (contenu, nom) = await imprimer.ExecuterAsync(patientId, ct);
                return Results.File(contenu, "application/pdf", nom);
            });

        // Correction de la dette par le titulaire, avec motif : inscrite au relevé et au journal
        credits.MapPost("/{patientId:long}/correction", async (long patientId, CorrectionCreditRequete req, CorrigerDetteCredit corriger,
                ClaimsPrincipal user, HttpRequest requete, CancellationToken ct) =>
            {
                await corriger.ExecuterAsync(patientId, req.NouvelleDette, req.Motif, user.UtilisateurId(), requete.Poste(), ct);
                return Results.NoContent();
            })
           .ExigerTitulaire();

        credits.MapPost("/{patientId:long}/reglement", async (long patientId, ReglementCreditRequete req, EncaisserReglementCredit encaisser,
                ClaimsPrincipal user, HttpRequest requete, CancellationToken ct) =>
            {
                var r = await encaisser.ExecuterAsync(patientId, req.Montant, req.Mode, user.UtilisateurId(), requete.Poste(), req.Ventes, ct);
                return Results.Ok(new RecuCreditDto(r.Numero, r.Patient, r.Montant, r.Mode, r.ResteDu));
            });
    }

    private static PatientEnCreditDto VersDto(PatientEnCredit p) => new(p.Id, p.Nom, p.Telephone, p.Solde, p.Plafond, p.DerniereVente);
}
