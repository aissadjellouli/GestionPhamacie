using System.Security.Claims;
using Pharma.Api.Securite;
using Pharma.Application.Commun;
using Pharma.Application.Psychotropes;
using Pharma.Application.Stock;
using Pharma.Domain.Securite;
using Pharma.Shared;

namespace Pharma.Api.Endpoints;

internal static class RegistreEndpoints
{
    public static void MapRegistre(this RouteGroupBuilder api)
    {
        var registre = api.MapGroup("/registre").ExigerPermission(Permissions.PsychotropesRegistre);

        registre.MapGet("/produits", async (ListerProduitsRegistre lister, ClaimsPrincipal user, CancellationToken ct) =>
            Results.Ok((await lister.ExecuterAsync(user.UtilisateurId(), ct)).Select(p => new ProduitRegistreDto(p.Id, p.Nom))));

        // En-tête de l'écran : nombre d'inscriptions et balance mensuelle, calculés par la base ; période sans limite
        registre.MapGet("", async (long? produit, DateOnly? du, DateOnly? au, ConsulterRegistre consulter, IHorloge horloge,
                ClaimsPrincipal user, CancellationToken ct) =>
            {
                var r = await consulter.SyntheseAsync(user.UtilisateurId(), produit, du, au, horloge.Aujourdhui, ct);
                return Results.Ok(new RegistreDto(r.Du, r.Au, r.Produit, r.Inscriptions,
                    r.Balance.Select(b => new BalanceMensuelleDto(b.ProduitId, b.Produit, b.Annee, b.Mois, b.Entrees, b.Sorties, b.SoldeFin, b.SoldeReporte)).ToList(),
                    r.NomsMasques));
            });

        // Inscriptions paginées et filtrées par la base (règle des listes) : produit, période, opération ; numéros dans l'ordre du registre
        registre.MapGet("/inscriptions", async (long? produit, DateOnly? du, DateOnly? au, string? operation, int? page, int? taille,
                ConsulterRegistre consulter, IRegistreRenforceDepot renforce, IHorloge horloge, ClaimsPrincipal user, CancellationToken ct) =>
            {
                var r = await consulter.InscriptionsAsync(user.UtilisateurId(), produit, du, au, operation, DemandePage.Lire(page, taille),
                    horloge.Aujourdhui, ct);
                var operations = r.Operations.Select(t => new CodeLibelleDto(t, RegistreCsv.Libelle(t)))
                    .OrderBy(o => o.Libelle, StringComparer.CurrentCulture).ToList();
                // Écritures annulées (lot 8) : marquées, jamais modifiées
                var annulees = new Registre(default, default, null, [], [], false,
                    Annulations: await renforce.AnnulationsAsync(r.Page.Lignes.Select(l => l.Numero).ToList(), ct));
                var p = r.Page.Vers(l => new LigneRegistreDto(l.Numero, l.Date, l.ProduitId, l.Produit, l.NumeroLot, l.Type,
                    RegistreCsv.Libelle(l.Type), l.Entree, l.Sortie, l.SoldeProduit, l.SoldeLot, l.Patient, l.Medecin,
                    l.Ordonnance, l.Fournisseur, l.Reference, l.PorteurNom, l.PorteurAdresse, l.PorteurPiece, l.Employe, l.PatientId,
                    RegistreCsv.Annulation(annulees, l.Numero), l.DureeTraitementJours));
                return Results.Ok(new InscriptionsRegistreDto(new PageDto<LigneRegistreDto>(p.Lignes, p.Total, p.Numero, p.Taille), operations, r.NomsMasques));
            });

        // Export d'inspection (lot 8) : PDF, Excel (xlsx) ou CSV ; produit, période, patient, prescripteur ; sceau de la période.
        // medecin : identifiant du prescripteur choisi (filtre exact) ; prescripteur (texte, « nom contient ») gardé pour compatibilité
        registre.MapGet("/export", async (string? format, long? produit, DateOnly? du, DateOnly? au, long? patient, string? prescripteur, long? medecin,
                ExporterRegistre exporter, IHorloge horloge, ClaimsPrincipal user, CancellationToken ct) =>
            {
                var (contenu, nom, type) = await exporter.ExecuterAsync(format ?? "pdf", user.UtilisateurId(), produit, du, au, horloge.Aujourdhui, ct,
                    patient, prescripteur, medecin);
                return Results.File(contenu, type, nom);
            });

        // Correction par écriture d'annulation (titulaire) : la ligne n'est jamais modifiée
        registre.MapPost("/inscriptions/{numero:long}/annulation", async (long numero, AnnulationEcritureRequete req, AnnulerEcritureRegistre annuler,
                ClaimsPrincipal user, CancellationToken ct) =>
            {
                await annuler.ExecuterAsync(user.UtilisateurId(), numero, req.Motif, ct);
                return Results.NoContent();
            });

        registre.MapGet("/forcages", async (DateOnly? du, DateOnly? au, string? code, int? page, int? taille, ListerForcagesPsychotropes lister,
                IHorloge horloge, ClaimsPrincipal user, CancellationToken ct) =>
            {
                var fin = au ?? horloge.Aujourdhui;
                var p = await lister.ExecuterAsync(user.UtilisateurId(), du ?? fin.AddMonths(-1), fin, code, DemandePage.Lire(page, taille), ct);
                return Results.Ok(new PageDto<ForcageAlerteDto>(p.Lignes.Select(f => new ForcageAlerteDto(f.Id, f.Date, f.Employe, f.Valideur, f.Codes,
                    f.Messages, f.Motif, f.Patient, f.Medecin, f.NumeroOrdonnance, f.Produits)).ToList(), p.Total, p.Numero, p.Taille));
            });

        // Délivrance (droit psychotropes.delivrer, vérifié par le cas d'usage) : contrôle des alertes et forçage avec motif
        var psy = api.MapGroup("/psychotropes");
        psy.MapPost("/controle", async (DelivrancePsychotropeDto d, ControlerDelivrancePsychotrope controler, ClaimsPrincipal user, CancellationToken ct) =>
        {
            var r = await controler.ExecuterAsync(user.UtilisateurId(), Vers(d), ct);
            return Results.Ok(new ControleDelivranceDto(r.Alertes.Select(a => new AlertePsychotropeDto(a.Code, a.Libelle, a.Message, a.ExigeValideur)).ToList(),
                r.Repertoire, r.Avis));
        });
        psy.MapPost("/forcer", async (ForcageRequete req, ForcerAlertesPsychotropes forcer, ClaimsPrincipal user, HttpRequest requete, CancellationToken ct) =>
        {
            var id = await forcer.ExecuterAsync(user.UtilisateurId(), Vers(req.Delivrance), req.Motif, requete.Poste(), ct);
            return Results.Ok(new IdentifiantDto(id ?? 0));
        });

        // Réglages des alertes (titulaire)
        psy.MapGet("/regles", async (ParametrerAlertesPsychotropes p, ClaimsPrincipal user, CancellationToken ct) =>
            Results.Ok((await p.LireAsync(user.UtilisateurId(), ct)).Select(r => new RegleAlertePsychotropeDto(r.Code, r.Libelle, r.Active, r.Seuil,
                r.PeriodeJours, r.ExigerValideur, r.UnitesParJour)).ToList()))
           .AvecEtatReglageCentral(Pharma.Domain.Reglages.TablesMutualisables.AlertesPsychotropes);
        psy.MapPut("/regles", async (List<RegleAlertePsychotropeDto> regles, ParametrerAlertesPsychotropes p, ClaimsPrincipal user, CancellationToken ct) =>
        {
            await p.EnregistrerAsync(user.UtilisateurId(), regles.Select(r => new RegleAlertePsychotrope(r.Code, r.Libelle, r.Active, r.Seuil,
                r.PeriodeJours, r.ExigerValideur, r.UnitesParJour)).ToList(), ct);
            return Results.NoContent();
        });

        // Zone de l'officine (titulaire, script 118) : wilaya de l'officine et wilayas voisines
        psy.MapGet("/zone", async (ParametrerZonePsychotropes p, ClaimsPrincipal user, CancellationToken ct) =>
        {
            var z = await p.LireAsync(user.UtilisateurId(), ct);
            return Results.Ok(new ZoneOfficineDto(z.Officine, z.Voisines));
        });
        psy.MapPut("/zone", async (ZoneOfficineDto zone, ParametrerZonePsychotropes p, ClaimsPrincipal user, CancellationToken ct) =>
        {
            await p.EnregistrerAsync(user.UtilisateurId(), new ZoneOfficine(zone.Officine, zone.Voisines ?? []), ct);
            return Results.NoContent();
        });

        // Classes suivies (antibiotiques) : rapport (rapports.ventes), liste des DCI (titulaire)
        psy.MapGet("/classes", async (SuiviClassesTherapeutiques s, ClaimsPrincipal user, CancellationToken ct) =>
            Results.Ok((await s.ClassesAsync(user.UtilisateurId(), ct)).Select(c => new ClasseSuivieDto(c.Code, c.Libelle, c.Active, c.NombreDci)).ToList()));
        psy.MapGet("/classes/{code}/dci", async (string code, SuiviClassesTherapeutiques s, ClaimsPrincipal user, CancellationToken ct) =>
            Results.Ok((await s.DciAsync(user.UtilisateurId(), code, ct)).Select(d => new DciClasseDto(d.DciId, d.Nom)).ToList()));
        psy.MapGet("/dci", async (string? q, SuiviClassesTherapeutiques s, ClaimsPrincipal user, CancellationToken ct) =>
            Results.Ok((await s.ChercherDciAsync(user.UtilisateurId(), q ?? "", ct)).Select(d => new DciClasseDto(d.DciId, d.Nom)).ToList()));
        psy.MapPut("/classes/{code}/dci/{dciId:long}", async (string code, long dciId, SuiviClassesTherapeutiques s, ClaimsPrincipal user, CancellationToken ct) =>
        {
            await s.ModifierAsync(user.UtilisateurId(), code, dciId, ajouter: true, ct);
            return Results.NoContent();
        });
        psy.MapDelete("/classes/{code}/dci/{dciId:long}", async (string code, long dciId, SuiviClassesTherapeutiques s, ClaimsPrincipal user, CancellationToken ct) =>
        {
            await s.ModifierAsync(user.UtilisateurId(), code, dciId, ajouter: false, ct);
            return Results.NoContent();
        });
        psy.MapGet("/classes/{code}/suivi", async (string code, DateOnly? du, DateOnly? au, string? format, SuiviClassesTherapeutiques s, IHorloge horloge,
                ClaimsPrincipal user, CancellationToken ct) =>
            {
                var fin = au ?? horloge.Aujourdhui;
                var debut = du ?? new DateOnly(fin.Year, 1, 1);
                var lignes = await s.RapportAsync(user.UtilisateurId(), code, debut, fin, ct);
                if (format != "xlsx")
                    return Results.Ok(lignes.Select(l => new LigneSuiviClasseDto(l.Dci, l.Annee, l.Mois, l.Boites, l.Delivrances)).ToList());
                var feuille = new List<IReadOnlyList<object?>> { new object?[] { "Molécule (DCI)", "Mois", "Boîtes délivrées", "Ventes" } };
                feuille.AddRange(lignes.Select(l => (IReadOnlyList<object?>)new object?[] { l.Dci, $"{l.Mois:00}/{l.Annee}", l.Boites, l.Delivrances }));
                var contenu = ClasseurXlsx.Ecrire([new ClasseurXlsx.Feuille($"Suivi {code}", feuille)]);
                return Results.File(contenu, ExporterRegistre.TypeXlsx, $"suivi_{code}_{debut:yyyyMMdd}_{fin:yyyyMMdd}.xlsx");
            });
    }

    private static DelivrancePsychotrope Vers(DelivrancePsychotropeDto d) =>
        new(d.PatientId, d.MedecinId, d.NumeroOrdonnance, d.DatePrescription,
            (d.Lignes ?? []).Where(l => l.Quantite > 0).Select(l => new LignePsychotrope(l.ProduitId, l.Quantite)).ToList(), d.DureeTraitementJours);
}
