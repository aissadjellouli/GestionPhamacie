using System.Security.Claims;
using Pharma.Api.Securite;
using Pharma.Application.Rapports;
using Pharma.Domain.Rapports;
using Pharma.Domain.Securite;
using Pharma.Shared;

namespace Pharma.Api.Endpoints;

/// <summary>Rapports d'activité : ventes, produits, marge (droit rapports.marges), clôtures (écarts : caisse.voir_ecart).</summary>
internal static class RapportsEndpoints
{
    public static void MapRapports(this RouteGroupBuilder api)
    {
        var rapports = api.MapGroup("/rapports");

        // ?periode=jour|semaine|mois&date=AAAA-MM-JJ (jour de référence ; aujourd'hui par défaut)
        // ou ?periode=dates&du=AAAA-MM-JJ&au=AAAA-MM-JJ (dates choisies dans le calendrier)
        rapports.MapGet("", async (string? periode, DateOnly? date, DateOnly? du, DateOnly? au, ObtenirRapport obtenir, ClaimsPrincipal user, CancellationToken ct) =>
                Results.Ok(VersDto(await obtenir.ExecuterAsync(Periode(periode, date, du, au), user.UtilisateurId(), ct))))
           .ExigerPermission(Permissions.RapportsVentes);

        // ?format=pdf|excel
        rapports.MapGet("/export", async (string? periode, DateOnly? date, DateOnly? du, DateOnly? au, string? format, ExporterRapport exporter, ClaimsPrincipal user, CancellationToken ct) =>
            {
                var f = string.Equals(format, "excel", StringComparison.OrdinalIgnoreCase) ? FormatExport.Excel : FormatExport.Pdf;
                var (contenu, nom) = await exporter.ExecuterAsync(Periode(periode, date, du, au), f, user.UtilisateurId(), ct);
                return Results.File(contenu, f == FormatExport.Pdf ? "application/pdf" : "text/csv; charset=utf-8", nom);
            })
           .ExigerPermission(Permissions.RapportsVentes);
    }

    private static PeriodeRapport Periode(string? periode, DateOnly? date, DateOnly? du, DateOnly? au)
    {
        var aujourdhui = DateOnly.FromDateTime(DateTime.Now);
        return string.Equals(periode, "dates", StringComparison.OrdinalIgnoreCase)
            ? PeriodeRapport.Choisie(du ?? aujourdhui, au ?? aujourdhui, aujourdhui)
            : PeriodeRapport.Calculer(Type(periode), date ?? aujourdhui);
    }

    private static TypePeriode Type(string? periode) => periode?.ToLowerInvariant() switch
    {
        "jour" => TypePeriode.Jour,
        "mois" => TypePeriode.Mois,
        _ => TypePeriode.Semaine
    };

    private static RapportActiviteDto VersDto(RapportActivite r)
    {
        var s = r.Synthese;
        return new(r.Periode.Type.ToString().ToLowerInvariant(), r.Periode.Du, r.Periode.Au, r.Periode.Libelle,
            new SyntheseVentesDto(s.NombreVentes, s.ChiffreAffaires, s.PartOrganismes, s.CreditsAccordes, s.Remises, s.Annulations),
            r.Marge is { } m ? new MargeBruteDto(m.ChiffreAffairesHt, m.CoutAchat, m.Montant, m.Taux) : null,
            r.ParJour.Select(j => new VentesJourDto(j.Date, j.NombreVentes, j.ChiffreAffaires)).ToList(),
            r.TopProduits.Select(p => new ProduitVenduDto(p.Produit, p.Quantite, p.ChiffreAffaires)).ToList(),
            r.Clotures.Select(c => new ClotureRapportDto(c.Employe, c.Poste, c.OuverteLe, c.FermeeLe, c.Attendu, c.Compte, c.Ecart, c.Annulations, c.Remises)).ToList(),
            r.EcartsVisibles, r.Periode.ParSemaine);
    }
}
