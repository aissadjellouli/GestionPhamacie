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

        // Clôtures de la période, paginées et filtrées par le serveur (employé, poste, ecart=true : avec un écart seulement)
        rapports.MapGet("/clotures", async (string? periode, DateOnly? date, DateOnly? du, DateOnly? au, string? employe, string? poste, bool? ecart,
                int? page, int? taille, ObtenirRapport obtenir, ClaimsPrincipal user, CancellationToken ct) =>
            {
                var r = await obtenir.CloturesPageAsync(Periode(periode, date, du, au), employe, poste, ecart == true,
                    Pharma.Application.Commun.DemandePage.Lire(page, taille), user.UtilisateurId(), ct);
                return Results.Ok(new CloturesPageDto(new PageDto<ClotureRapportDto>(
                    r.Page.Lignes.Select(c => new ClotureRapportDto(c.Employe, c.Poste, c.OuverteLe, c.FermeeLe, c.Attendu, c.Compte, c.Ecart, c.Annulations,
                        c.Remises, c.Retours, c.EmployeId)).ToList(), r.Page.Total, r.Page.Numero, r.Page.Taille),
                    r.EcartsVisibles, r.Employes.ToList(), r.Postes.ToList()));
            })
           .ExigerPermission(Permissions.RapportsVentes);

        // ?format=pdf (ou rien) : PDF ; csv ou excel : fichier CSV ; tout autre format : 400
        rapports.MapGet("/export", async (string? periode, DateOnly? date, DateOnly? du, DateOnly? au, string? format, ExporterRapport exporter, ClaimsPrincipal user, CancellationToken ct) =>
            {
                FormatExport f;
                switch (format?.Trim().ToLowerInvariant())
                {
                    case null or "" or "pdf": f = FormatExport.Pdf; break;
                    case "csv" or "excel": f = FormatExport.Excel; break;
                    default:
                        return Results.BadRequest(new ErreurDto($"Format d’export inconnu : {format} (pdf, csv ou excel).", "format"));
                }
                var (contenu, nom) = await exporter.ExecuterAsync(Periode(periode, date, du, au), f, user.UtilisateurId(), ct);
                return Results.File(contenu, f == FormatExport.Pdf ? "application/pdf" : "text/csv; charset=utf-8", nom);
            })
           .ExigerPermission(Permissions.RapportsVentes);

        // TVA d'achat à reverser par motif de sortie sans vente (titulaire)
        rapports.MapPut("/tva-sorties", async (TvaSortiesDto req, ModifierTvaSorties modifier, ClaimsPrincipal user, CancellationToken ct) =>
            {
                await modifier.ExecuterAsync(new TvaSorties(req.Perime, req.Casse, req.Don), user.UtilisateurId(), ct);
                return Results.NoContent();
            })
           .ExigerTitulaire();
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
            new SyntheseVentesDto(s.NombreVentes, s.ChiffreAffaires, s.PartOrganismes, s.CreditsAccordes, s.Remises, s.Annulations, s.Retours, s.MontantRetours,
                s.VentesMedicaments, s.VentesAutres, s.Shp, s.DonsPartiels, s.RemisesAutomatiques),
            r.Marge is { } m ? new MargeBruteDto(m.ChiffreAffairesHt, m.CoutAchat, m.Montant, m.Taux) : null,
            r.ParJour.Select(j => new VentesJourDto(j.Date, j.NombreVentes, j.ChiffreAffaires)).ToList(),
            r.TopProduits.Select(p => new ProduitVenduDto(p.Produit, p.Quantite, p.ChiffreAffaires)).ToList(),
            r.Clotures.Select(c => new ClotureRapportDto(c.Employe, c.Poste, c.OuverteLe, c.FermeeLe, c.Attendu, c.Compte, c.Ecart, c.Annulations, c.Remises, c.Retours, c.EmployeId)).ToList(),
            r.EcartsVisibles, r.Periode.ParSemaine,
            (r.Retours ?? []).Select(x => new RetourRapportDto(x.Ticket, x.DateVente, x.Vendeur, x.DateRetour, x.Poste, x.Employe, x.Produits, x.Montant,
                x.DetteDiminuee, x.DeduitDansLaPeriode, x.VendeurId, x.EmployeId)).ToList(),
            r.Sorties is { Count: > 0 } sorties ? VersDto(sorties) : null,
            r.Tva is { } t ? new TvaSortiesDto(t.Perime, t.Casse, t.Don) : null,
            r.ApresSorties is { } a ? new MargeApresSortiesDto(a.MargeBrute, a.PertesHt, a.TvaAReverser, a.Montant) : null);
    }

    private static SortiesRapportDto VersDto(IReadOnlyList<SortieSansVente> sorties)
    {
        static TotalSortiesDto T(TotalSorties t) => new(t.Cle, t.Perimes, t.Casses, t.Dons, t.Boites, t.Valeur);
        return new(T(SortiesRapport.Total("Total", sorties)),
            SortiesRapport.ParEmploye(sorties).Select(T).ToList(),
            SortiesRapport.ParProduit(sorties).Select(T).ToList(),
            sorties.Select(s => new SortieSansVenteDto(s.Date, s.Motif, s.Produit, s.Lot, s.Quantite, s.Valeur, s.Employe, s.Reference, s.EmployeId, s.TvaAchat)).ToList());
    }
}
