using Pharma.Application.Offre;
using System.Security.Claims;
using Pharma.Api.Securite;
using Pharma.Application.Commun;
using Pharma.Application.Pilotage;
using Pharma.Application.Rapports;
using Pharma.Domain.Rapports;
using Pharma.Shared;

namespace Pharma.Api.Endpoints;

/// <summary>
/// Tableau de bord financier du titulaire (lot 11) : lecture seule. Accès : titulaire ou droit « rapports.marges »
/// (vérifié par le cas d'usage, refus en 403). Période : mêmes paramètres que les rapports.
/// </summary>
internal static class PilotageFinancierEndpoints
{
    public static void MapPilotageFinancier(this RouteGroupBuilder api)
    {
        var g = api.MapGroup("/pilotage/financier").ExigerPack(PacksOffre.Finances);

        // ?periode=jour|semaine|mois&date=… ou ?periode=dates&du=…&au=… ; dormant=jours (seuil, sinon celui des alertes)
        g.MapGet("", async (string? periode, DateOnly? date, DateOnly? du, DateOnly? au, int? dormant, ObtenirPilotageFinancier obtenir,
                ClaimsPrincipal user, CancellationToken ct) =>
                Results.Ok(VersDto(await obtenir.ExecuterAsync(Periode(periode, date, du, au), dormant, user.UtilisateurId(), ct))))
           .ExigerConnexion();

        // Marge par produit ou par laboratoire (par=laboratoire), paginée et filtrée par le serveur
        g.MapGet("/marges", async (string? periode, DateOnly? date, DateOnly? du, DateOnly? au, string? par, string? recherche, int? page, int? taille,
                ObtenirPilotageFinancier obtenir, ClaimsPrincipal user, CancellationToken ct) =>
            {
                var regroupement = string.Equals(par, "laboratoire", StringComparison.OrdinalIgnoreCase) ? RegroupementMarge.Laboratoire : RegroupementMarge.Produit;
                var p = await obtenir.MargesAsync(Periode(periode, date, du, au), regroupement, recherche, DemandePage.Lire(page, taille), user.UtilisateurId(), ct);
                return Results.Ok(new PageDto<MargeRegroupeeDto>(
                    p.Lignes.Select(m => new MargeRegroupeeDto(m.Cle, m.Quantite, m.ChiffreAffairesHt, m.CoutAchat, m.Marge, m.Taux)).ToList(),
                    p.Total, p.Numero, p.Taille));
            })
           .ExigerConnexion();

        g.MapGet("/dormants", async (int? dormant, string? recherche, int? page, int? taille, ObtenirPilotageFinancier obtenir, ClaimsPrincipal user,
                CancellationToken ct) =>
            {
                var p = await obtenir.DormantsAsync(dormant, recherche, DemandePage.Lire(page, taille), user.UtilisateurId(), ct);
                return Results.Ok(new PageDto<ProduitDormantDto>(
                    p.Lignes.Select(d => new ProduitDormantDto(d.ProduitId, d.Produit, d.Laboratoire, d.Stock, d.Valeur, d.DerniereVente, d.JoursSansVente)).ToList(),
                    p.Total, p.Numero, p.Taille));
            })
           .ExigerConnexion();

        // ?format=pdf (ou rien) : PDF ; excel ou xlsx : classeur Excel
        g.MapGet("/export", async (string? periode, DateOnly? date, DateOnly? du, DateOnly? au, int? dormant, string? format,
                ExporterPilotageFinancier exporter, ClaimsPrincipal user, CancellationToken ct) =>
            {
                FormatExport f;
                switch (format?.Trim().ToLowerInvariant())
                {
                    case null or "" or "pdf": f = FormatExport.Pdf; break;
                    case "excel" or "xlsx": f = FormatExport.Excel; break;
                    default:
                        return Results.BadRequest(new ErreurDto($"Format d’export inconnu : {format} (pdf ou excel).", "format"));
                }
                var (contenu, nom) = await exporter.ExecuterAsync(Periode(periode, date, du, au), dormant, f, user.UtilisateurId(), ct);
                return Results.File(contenu, f == FormatExport.Pdf ? "application/pdf" : "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", nom);
            })
           .ExigerConnexion();
    }

    private static PeriodeRapport Periode(string? periode, DateOnly? date, DateOnly? du, DateOnly? au)
    {
        var aujourdhui = DateOnly.FromDateTime(DateTime.Now);
        if (string.Equals(periode, "dates", StringComparison.OrdinalIgnoreCase))
            return PeriodeRapport.Choisie(du ?? aujourdhui, au ?? aujourdhui, aujourdhui);
        var type = periode?.ToLowerInvariant() switch
        {
            "jour" => TypePeriode.Jour,
            "semaine" => TypePeriode.Semaine,
            _ => TypePeriode.Mois
        };
        return PeriodeRapport.Calculer(type, date ?? aujourdhui);
    }

    private static ComparaisonDto C(Comparaison c) => new(c.Actuel, c.Precedent, c.VariationPct);

    private static TableauFinancierDto VersDto(TableauFinancier t)
    {
        static MontantParTrancheDto T(MontantParTranche x) => new(x.Tranche, x.Nombre, x.Montant);
        var cr = t.Creances;
        return new TableauFinancierDto(t.Periode.Type.ToString().ToLowerInvariant(), t.Periode.Du, t.Periode.Au, t.Periode.Libelle,
            t.Precedent?.Du, t.Precedent?.Au, C(t.ChiffreAffaires), C(t.NombreVentes), t.PanierMoyen is { } pm ? C(pm) : null, C(t.MargeBrute),
            t.Actuel.Marge.ChiffreAffairesHt, t.Actuel.Marge.CoutAchat, t.Actuel.Marge.Taux, t.Precedent?.Marge.Taux,
            t.ParJour.Select(j => new VentesJourDto(j.Date, j.NombreVentes, j.ChiffreAffaires)).ToList(),
            new RepartitionEncaissementDto(t.Repartition.Comptant, t.Repartition.Credit, t.Repartition.Chifa, t.Repartition.Mdn, t.Repartition.AutresOrganismes, t.Repartition.Total),
            new CreancesDto(cr.Bordereaux.Select(b => new CreanceBordereauDto(b.Id, b.Numero, b.Organisme, b.Canal, b.Statut, b.DateDepot, b.Attendu, b.Rejete,
                    b.Verse, b.Reste, b.AgeJours, b.Tranche)).ToList(), cr.TotalBordereaux, cr.BordereauxParAge.Select(T).ToList(),
                cr.HorsBordereau, cr.CreditsPatients, cr.PatientsDebiteurs, cr.CreditsParAge.Select(T).ToList()),
            new StockPilotageDto(t.Stock.ValeurAchat, t.Stock.Produits, t.Stock.CoutVentesPeriode, t.Stock.RotationAnnuelle, t.Stock.CouvertureJours,
                t.Stock.SeuilDormantJours, t.Stock.Dormants, t.Stock.ValeurDormante, t.Stock.RupturesOfficine, t.Stock.RupturesGrossistes,
                t.Stock.ValeurExclue, t.Stock.LotsExclus),
            t.Achats.Select(a => new AchatsGrossisteDto(a.FournisseurId, a.Grossiste, a.Receptions, a.MontantHt, a.MontantTtc)).ToList(),
            new RejetsChifaDto(t.Rejets.VentesDeposees, t.Rejets.MontantDepose, t.Rejets.Rejets, t.Rejets.MontantRejete, t.Rejets.TauxRejet,
                t.Rejets.ParMotif.Select(m => new RejetsMotifDto(m.Code, m.Libelle, m.Nombre, m.Montant, t.Rejets.TauxMotif(m))).ToList()),
            t.Tva.Select(x => new TvaParTauxDto(x.Taux, x.Ttc, x.Ht, x.Tva, x.Evenements)).ToList());
    }
}
