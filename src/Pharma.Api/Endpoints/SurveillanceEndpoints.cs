using System.Security.Claims;
using Pharma.Api.Securite;
using Pharma.Application.Surveillance;
using Pharma.Domain.Securite;
using Pharma.Shared;

namespace Pharma.Api.Endpoints;

/// <summary>
/// Surveillance de la caisse : le poste signale ce qui ne laisse pas de vente (ligne retirée du panier, tiroir ouvert) ;
/// le titulaire consulte et traite les anomalies, et règle les seuils.
/// </summary>
internal static class SurveillanceEndpoints
{
    public static void MapSurveillance(this RouteGroupBuilder api)
    {
        // Tout employé de caisse ; le tiroir demande en plus le droit « caisse.ouvrir_tiroir » (contrôlé par le cas d'usage)
        api.MapPost("/caisse/evenements", async (EvenementCaisseRequete req, SignalerEvenementCaisse signaler, ClaimsPrincipal user, HttpRequest requete,
                CancellationToken ct) =>
            {
                var s = req.Tiroir is { } d
                    ? new Pharma.Domain.Caisse.SaisieTiroir(d.Montant, d.Categorie, d.Libelle, d.FournisseurId, d.Beneficiaire, d.Reference, d.PatientId, d.Raison, d.RegularisationVenteId)
                    : null;
                var bon = await signaler.ExecuterAsync(req.Type, req.ProduitId, req.LotId, req.Quantite, req.Montant, user.UtilisateurId(), requete.Poste(), ct,
                    req.MotifCode, req.MotifTexte, s);
                return Results.Ok(new OuvertureTiroirDto(bon?.Id, bon?.Numero));
            })
           .ExigerPermission(Permissions.CaisseVendre);

        // Sorties et entrées du tiroir (script 096) : bon de sortie en PDF rouleau, correction par écriture inverse (droit caisse.cloturer)
        api.MapGet("/caisse/tiroir/{id:long}/bon.pdf", async (long id, int? largeur, Pharma.Application.Caisse.ObtenirBonSortie obtenir, ClaimsPrincipal user,
                CancellationToken ct) =>
                await obtenir.ExecuterAsync(id, largeur, user.UtilisateurId(), ct) is { } r
                    ? Results.File(r.Contenu, "application/pdf", r.NomFichier)
                    : Results.NotFound(new ErreurDto("Bon de sortie introuvable.")))
           .ExigerPermission(Permissions.CaisseVendre);

        api.MapPost("/caisse/tiroir/{id:long}/correction", async (long id, CorrectionTiroirRequete req, Pharma.Application.Caisse.CorrigerMouvementTiroir corriger,
                ClaimsPrincipal user, HttpRequest requete, CancellationToken ct) =>
            {
                var e = await corriger.ExecuterAsync(id, req.Motif, user.UtilisateurId(), requete.Poste(), ct);
                return Results.Ok(new OuvertureTiroirDto(e.Id, e.Numero));
            })
           .ExigerPermission(Permissions.CaisseVendre);

        // Annulation d'un règlement de grossiste payé depuis le tiroir (script 098) : droit achats.regler ou code d'un collègue qui l'a
        // (pour cette seule action), vérifié par le cas d'usage
        api.MapPost("/caisse/tiroir/{id:long}/annulation-reglement", async (long id, CorrectionTiroirRequete req,
                Pharma.Application.Caisse.AnnulerReglementGrossisteTiroir annuler, ClaimsPrincipal user, HttpRequest requete, CancellationToken ct) =>
            {
                var e = await annuler.ExecuterAsync(id, req.Motif, user.UtilisateurId(), requete.Poste(), ct);
                return Results.Ok(new OuvertureTiroirDto(e.Id, e.Numero));
            })
           .ExigerPermission(Permissions.CaisseVendre);

        var surveillance = api.MapGroup("/surveillance").ExigerTitulaire();
        surveillance.MapJournalFiscal();

        surveillance.MapGet("", async (bool? traitees, ListerSurveillance lister, ClaimsPrincipal user, CancellationToken ct) =>
            Results.Ok((await lister.ExecuterAsync(traitees == true, user.UtilisateurId(), ct)).Select(a => new AlerteSurveillanceDto(
                a.Id, a.Type, a.Niveau, a.UtilisateurId, a.Employe, a.Jour, a.Message, a.Valeur, a.Statut, a.MiseAJourLe, a.TraiteePar, a.TraiteeLe, a.ActionPrise))));

        surveillance.MapPost("/{id:long}/traiter", async (long id, TraiterSurveillanceRequete req, TraiterSurveillance traiter, ClaimsPrincipal user, CancellationToken ct) =>
            await traiter.ExecuterAsync(id, req.ActionPrise, user.UtilisateurId(), ct, req.MotDePasseTitulaire)
                ? Results.NoContent()
                : Results.NotFound(new ErreurDto("Anomalie introuvable ou déjà traitée : actualisez la liste.")));

        surveillance.MapGet("/parametres", async (ReglerSurveillance regler, ClaimsPrincipal user, CancellationToken ct) =>
            {
                var p = await regler.LireAsync(user.UtilisateurId(), ct);
                return Results.Ok(new ParametresSurveillanceDto(p.SeuilLignesRetirees, p.SeuilTiroir, p.SeuilAttentesAbandonnees, p.SeuilRemises,
                    p.SeuilRemisesPatient, p.HeureOuverture, p.HeureFermeture, p.SeuilAjustements, p.SeuilCasses, p.SeuilEchantillons, p.JoursComptageSensibles,
                    p.ReceptionAveugle, p.JoursAvoirRetour, p.SeuilCreditNouveauPatient, p.JoursPatientRecent, p.SeuilCreditsJour,
                    p.JoursCreditSansRemboursement, p.SeuilAutorisationsValideur, p.SeuilAutorisationsEmploye, p.MinutesPostesSimultanes,
                    p.SeuilChangementsPoste, p.SeuilBaissesPrix, p.SeuilSortiesTiroir));
            })
           .AvecEtatReglageCentral(Pharma.Domain.Reglages.TablesMutualisables.Surveillance);

        surveillance.MapPut("/parametres", async (ParametresSurveillanceDto req, ReglerSurveillance regler, ClaimsPrincipal user, CancellationToken ct) =>
            {
                await regler.EnregistrerAsync(new ParametresSurveillance(req.SeuilLignesRetirees, req.SeuilTiroir, req.SeuilAttentesAbandonnees,
                    req.SeuilRemises, req.SeuilRemisesPatient, req.HeureOuverture, req.HeureFermeture, req.SeuilAjustements, req.SeuilCasses,
                    req.SeuilEchantillons, req.JoursComptageSensibles, req.ReceptionAveugle, req.JoursAvoirRetour,
                    req.SeuilCreditNouveauPatient, req.JoursPatientRecent, req.SeuilCreditsJour, req.JoursCreditSansRemboursement,
                    req.SeuilAutorisationsValideur, req.SeuilAutorisationsEmploye, req.MinutesPostesSimultanes, req.SeuilChangementsPoste,
                    req.SeuilBaissesPrix, req.SeuilSortiesTiroir), user.UtilisateurId(), ct);
                return Results.NoContent();
            });

        // Contrôle interne : sorties et entrées du tiroir par motif, employé et période (script 096)
        surveillance.MapGet("/tiroir", async (DateOnly? du, DateOnly? au, long? employe, string? motif, string? sens, int? page, int? taille,
                Pharma.Application.Caisse.ListerMouvementsTiroir lister, ClaimsPrincipal user, CancellationToken ct) =>
            {
                var p = await lister.ExecuterAsync(new Pharma.Application.Caisse.FiltreMouvementsTiroir(du, au, employe, motif, sens),
                    Pharma.Application.Commun.DemandePage.Lire(page, taille), user.UtilisateurId(), ct);
                return Results.Ok(new PageMouvementsTiroirDto(p.Page.Lignes.Select(m => new MouvementTiroirDto(m.Id, m.Numero, m.Le, m.UtilisateurId, m.Employe,
                        m.Poste, m.Motif, m.Sens, m.Montant, m.Categorie, m.Libelle, m.Beneficiaire, m.Reference, m.Patient, m.Raison, m.ValidePar,
                        m.CorrigeId, m.MotifCorrection, m.Corrigee, m.ReglementFournisseurId)).ToList(), p.Page.Total, p.Page.Numero, p.Page.Taille, p.TotalSorties, p.TotalEntrees));
            });

        // Produits sensibles : comptés à part et régulièrement
        surveillance.MapGet("/sensibles", async (GererProduitsSensibles gerer, ClaimsPrincipal user, CancellationToken ct) =>
            Results.Ok((await gerer.ListerAsync(user.UtilisateurId(), ct)).Select(p => new ProduitSensibleDto(p.ProduitId, p.Nom, p.Stock))));

        surveillance.MapPut("/sensibles/{produitId:long}", async (long produitId, MarquerSensibleRequete req, GererProduitsSensibles gerer,
                ClaimsPrincipal user, CancellationToken ct) =>
            {
                await gerer.MarquerAsync(produitId, req.Sensible, user.UtilisateurId(), ct);
                return Results.NoContent();
            });

        // Écarts dans le temps (par jour, poste, employés présents) et risques par employé
        surveillance.MapGet("/risques", async (DateOnly? du, DateOnly? au, ObtenirTableauRisques obtenir, ClaimsPrincipal user, CancellationToken ct) =>
            {
                var t = await obtenir.ExecuterAsync(du, au, user.UtilisateurId(), ct);
                var noms = t.Noms;
                return Results.Ok(new TableauRisquesDto(t.Du, t.Au,
                    t.Jours.Select(j => new JourEcartsDto(j.Jour, j.Ecart, j.Manque, j.PerteStock, j.BoitesAjustees, j.ValeurFantomes, j.Anomalies,
                        j.Urgentes, j.Presents.Select(id => noms.GetValueOrDefault(id, $"Employé {id}")).Order().ToList(), j.Postes)).ToList(),
                    t.Employes.Select(e => new RisqueEmployeDto(e.Activite.UtilisateurId, e.Activite.Nom, e.Activite.Actif, e.Presence.JoursPresent,
                        e.Presence.JoursAvecPerte, e.Presence.PerteEnPresence, e.Presence.MoyennePresent, e.Presence.MoyenneAbsent, e.Presence.Signale,
                        e.Activite.ManqueCaisse, e.Activite.Clotures, e.Activite.Anomalies, e.Activite.Urgentes, e.Activite.Annulations,
                        e.Activite.RetoursClients, e.Activite.Remises, e.Activite.Credits, e.Activite.AutorisationsDemandees, e.Activite.AutorisationsDonnees,
                        e.Activite.Casses, e.Activite.Echantillons, e.Activite.BoitesAjustees)).ToList(),
                    Pharma.Domain.Rapports.RegleRisques.JoursPerteMin, Pharma.Domain.Rapports.RegleRisques.Facteur, Pharma.Domain.Rapports.RegleRisques.JoursAbsentMin));
            });

        // Intégrité des journaux chaînés : état de chaque chaîne et « Vérifier maintenant » (titulaire)
        surveillance.MapGet("/integrite", async (Pharma.Application.Integrite.ObtenirIntegrite obtenir, ClaimsPrincipal user, CancellationToken ct) =>
            Results.Ok(TableauIntegrite(await obtenir.ExecuterAsync(user.UtilisateurId(), ct))));
        surveillance.MapPost("/integrite/verifier", async (Pharma.Application.Integrite.VerifierIntegriteMaintenant verifier,
                Pharma.Application.Integrite.ObtenirIntegrite obtenir, Pharma.Application.Fiscal.ControlerNumerotation numerotation, ClaimsPrincipal user,
                CancellationToken ct) =>
            {
                await verifier.ExecuterAsync(user.UtilisateurId(), ct);
                // Lot 3 : continuité des n° de tickets (après le contrôle du titulaire fait par VerifierIntegriteMaintenant)
                await numerotation.ExecuterAsync(user.UtilisateurId(), ct);
                return Results.Ok(TableauIntegrite(await obtenir.ExecuterAsync(user.UtilisateurId(), ct)));
            });
    }

    /// <summary>Journal fiscal des ventes (lot 3, titulaire) : recherche, récapitulatif TVA, sceaux, continuité, export CSV ou PDF.</summary>
    public static void MapJournalFiscal(this RouteGroupBuilder surveillance)
    {
        surveillance.MapGet("/journal-fiscal", async (DateOnly? du, DateOnly? au, string? ticket, int? page, int? taille,
                Pharma.Application.Fiscal.ObtenirJournalFiscal obtenir, ClaimsPrincipal user, CancellationToken ct) =>
            Results.Ok(JournalFiscal(await obtenir.ExecuterAsync(user.UtilisateurId(), du, au, ticket,
                Pharma.Application.Commun.DemandePage.Lire(page, taille), ct))));
        surveillance.MapGet("/journal-fiscal/export", async (DateOnly? du, DateOnly? au, string? format,
                Pharma.Application.Fiscal.ExporterJournalFiscal exporter, ClaimsPrincipal user, CancellationToken ct) =>
            {
                var (contenu, nom, type) = await exporter.ExecuterAsync(user.UtilisateurId(), du, au, format, ct);
                return Results.File(contenu, type, nom);
            });
    }

    private static JournalFiscalDto JournalFiscal(Pharma.Application.Fiscal.TableauJournalFiscal t)
    {
        static string Ticket(Pharma.Application.Fiscal.CompteurTickets c, long n) => c.Prefixe + n.ToString().PadLeft(6, '0');
        return new JournalFiscalDto(
            new PageDto<LigneJournalFiscalDto>(t.Lignes.Lignes.Select(l => new LigneJournalFiscalDto(l.Rang, l.Evenement, l.NumeroTicket, l.SurvenuLe,
                l.Poste, l.Vendeur, l.Ttc, l.Ht, l.Tva, l.PartPatient, l.PartOrganisme, l.Paiements, l.MontantMouvement, l.Motif, l.Hash)).ToList(),
                t.Lignes.Total, t.Lignes.Numero, t.Lignes.Taille),
            t.Tva.Select(x => new LigneRecapTvaDto(x.Taux, x.Exoneree, x.Ttc, x.Ht, x.Tva, x.Evenements)).ToList(),
            t.Totaux.Evenements, t.Totaux.Ttc, t.Totaux.Ht, t.Totaux.Tva,
            t.Clotures.Select(c => new ClotureFiscaleDto(c.SceauId, c.Type, c.SessionCaisseId, c.Du, c.Au, c.ScelleLeUtc, c.DernierRang,
                Pharma.Application.Integrite.MessageSceau.Exporter(c.Sceau, t.FormatSceau), c.EmpreinteCle, c.Ttc, c.FichierArchive)).ToList(),
            t.Anomalies.Select(a => a.Libelle).ToList(),
            t.TrousHistorique.Select(h => h.Du == h.Au ? $"{Ticket(t.Compteur, h.Du)}" : $"{Ticket(t.Compteur, h.Du)} à {Ticket(t.Compteur, h.Au)} ({h.Manquants})").ToList(),
            Ticket(t.Compteur, t.Compteur.DernierNumero), Ticket(t.Compteur, t.Compteur.PremierNumeroContinu), t.Compteur.MisEnServiceLe,
            t.Parametres.ConservationAnnees, t.Parametres.FormatExport,
            t.DerniereVerification?.Statut, t.DerniereVerification?.VerifieLe, t.DerniereVerification?.Motif);
    }

    private static TableauIntegriteDto TableauIntegrite(Pharma.Application.Integrite.TableauIntegrite t)
    {
        var chaines = new List<ChaineIntegriteDto>();
        foreach (var j in t.Journaux)
        {
            var verifs = t.Verifications.Where(v => v.Journal == j.Journal).ToList();
            if (verifs.Count == 0)
                chaines.Add(new ChaineIntegriteDto(j.Journal, j.Libelle, j.Algorithme, null, null, false, 0, 0, null, null, null, null, null));
            foreach (var v in verifs)
                chaines.Add(new ChaineIntegriteDto(j.Journal, j.Libelle, j.Algorithme, v.CleChaine, v.VerifieLe, v.DeclencheePar is null, v.Lignes,
                    v.Sceaux, v.Statut, v.RangRupture, v.Motif, v.Attendu, v.Trouve));
        }
        return new TableauIntegriteDto(chaines, t.Parametres.HeureVerification, t.AlgorithmeSceau, t.EmpreinteCle, t.Parametres.FormatExportSceau);
    }
}
