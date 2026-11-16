using System.Security.Claims;
using Pharma.Api.Securite;
using Pharma.Application.Achats;
using Pharma.Application.Commun;
using Pharma.Application.Securite;
using Pharma.Domain.Commun;
using Pharma.Domain.Securite;
using Pharma.Shared;

namespace Pharma.Api.Endpoints;

/// <summary>
/// Multi-grossistes (lot 6) : profil du fichier de chaque grossiste, import du catalogue / de la disponibilité, comparaison des
/// grossistes pour la liste à commander, répartition d'une commande (un bon par grossiste), envoi du bon par e-mail.
/// Prix d'achat (catalogue, dernier achat) : envoyés seulement avec le droit « rapports.marges » (ou au titulaire).
/// </summary>
internal static class MultiGrossistesEndpoints
{
    public static void MapMultiGrossistes(this RouteGroupBuilder api)
    {
        var g = api.MapGroup("/achats");

        g.MapGet("/catalogues/profils/{fournisseurId:long}", async (long fournisseurId, ConsulterProfilCatalogue consulter, CancellationToken ct) =>
            {
                var p = await consulter.ExecuterAsync(fournisseurId, ct);
                return Results.Ok(new ProfilCatalogueDto(p.FournisseurId, p.ColonneCode, p.ColonneCodeBarre, p.ColonneDesignation, p.ColonnePrixHt,
                    p.ColonneDisponibilite, p.ColonneDelai, p.ValeursIndisponible, p.ValeursDisponible, p.ModifieLe));
            })
           .ExigerPermission(Permissions.AchatsCommander);

        g.MapPut("/catalogues/profils/{fournisseurId:long}", async (long fournisseurId, ProfilCatalogueDto d, EnregistrerProfilCatalogue enregistrer,
                ClaimsPrincipal user, CancellationToken ct) =>
            {
                await enregistrer.ExecuterAsync(new ProfilCatalogue(fournisseurId, d.ColonneCode, d.ColonneCodeBarre, d.ColonneDesignation, d.ColonnePrixHt,
                    d.ColonneDisponibilite, d.ColonneDelai, d.ValeursIndisponible, d.ValeursDisponible), user.UtilisateurId(), ct);
                return Results.NoContent();
            })
           .ExigerPermission(Permissions.AchatsCommander);

        g.MapPost("/catalogues/colonnes", (FichierCatalogueRequete r, LireColonnesCatalogue lire) =>
                Results.Ok(lire.Executer(Fichier(r))))
           .ExigerPermission(Permissions.AchatsCommander);

        g.MapPost("/catalogues/{fournisseurId:long}/import", async (long fournisseurId, FichierCatalogueRequete r, ImporterCatalogueGrossiste importer,
                ClaimsPrincipal user, CancellationToken ct) =>
            {
                var b = await importer.ExecuterAsync(fournisseurId, Fichier(r), user.UtilisateurId(), ct);
                return Results.Ok(new BilanImportCatalogueDto(b.ImportId, b.LignesLues, b.LignesRapprochees, b.LignesIgnorees, b.NonRapprochees,
                    b.Anomalies.Select(a => new AnomalieCatalogueDto(a.NumeroLigne, a.Message)).ToList()));
            })
           .ExigerPermission(Permissions.AchatsCommander);

        g.MapGet("/catalogues/imports", async (long? grossiste, int? page, int? taille, ListerImportsCatalogue lister, CancellationToken ct) =>
                Results.Ok(VersDto((await lister.ExecuterAsync(grossiste, DemandePage.Lire(page, taille), ct)).Vers(i => new ImportCatalogueDto(
                    i.Id, i.FournisseurId, i.Grossiste, i.NomFichier, i.ImporteLe, i.Utilisateur, i.LignesLues, i.LignesRapprochees, i.LignesIgnorees)))))
           .ExigerPermission(Permissions.AchatsCommander);

        g.MapGet("/commander", async (string? recherche, string? statut, int? page, int? taille, ComparerGrossistes comparer,
                IPermissionService droits, ClaimsPrincipal user, CancellationToken ct) =>
            {
                var v = await VisibiliteAchats.LireAsync(droits, user.UtilisateurId(), ct);
                var p = await comparer.PageAsync(recherche, statut, DemandePage.Lire(page, taille), ct);
                return Results.Ok(VersDto(p.Vers(c => new ComparaisonProduitDto(c.Produit.ProduitId, c.Produit.Produit, c.Produit.Statut, c.Produit.Stock,
                    c.Produit.QteSuggeree,
                    c.Offres.Select(o => new OffreGrossisteDto(o.FournisseurId, o.Grossiste, v.Prix(o.PrixHt), o.Disponible, o.QuantiteDisponible,
                        o.DelaiJours, o.CatalogueLe, v.Prix(o.DernierPrixAchatHt), o.DernierAchatLe)).ToList(),
                    c.Proposition.FournisseurId, c.Proposition.Motif))));
            })
           .ExigerPermission(Permissions.AchatsCommander);

        g.MapPost("/commander/repartir", async (RepartitionRequete r, RepartirCommande repartir, ClaimsPrincipal user, CancellationToken ct) =>
                Results.Ok((await repartir.ExecuterAsync(r.Lignes.Select(l => new LigneRepartition(l.ProduitId, l.FournisseurId, l.Quantite)).ToList(),
                    user.UtilisateurId(), ct)).Select(b => new BonRepartiDto(b.CommandeId, b.FournisseurId, b.Grossiste, b.Lignes)).ToList()))
           .ExigerPermission(Permissions.AchatsCommander);

        g.MapPost("/commandes/{id:long}/envoyer-email", async (long id, EnvoyerBonParEmail envoyer, ClaimsPrincipal user, CancellationToken ct) =>
            {
                var b = await envoyer.ExecuterAsync(id, user.UtilisateurId(), ct);
                return Results.Ok(new BonEnvoyeDto(b.Numero, b.Adresse, b.Bon.NomFichier, Convert.ToBase64String(b.Bon.Contenu)));
            })
           .ExigerPermission(Permissions.AchatsCommander);
    }

    private static FichierGrossiste Fichier(FichierCatalogueRequete r)
    {
        try { return new FichierGrossiste(r.NomFichier, Convert.FromBase64String(r.ContenuBase64)); }
        catch (FormatException) { throw new RegleMetierException("Fichier illisible."); }
    }

    private static PageDto<T> VersDto<T>(Page<T> p) => new(p.Lignes, p.Total, p.Numero, p.Taille);
}
