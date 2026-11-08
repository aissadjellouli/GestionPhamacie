using System.Security.Claims;
using Pharma.Api.Securite;
using Pharma.Application.Achats;
using Pharma.Domain.Achats;
using Pharma.Domain.Commun;
using Pharma.Domain.Securite;
using Pharma.Shared;

namespace Pharma.Api.Endpoints;

/// <summary>Achats : grossistes, commandes, réceptions, retours fournisseurs, étiquettes internes.</summary>
internal static class AchatsEndpoints
{
    private static readonly string[] Lecture =
        [Permissions.AchatsCommander, Permissions.AchatsReceptionner, Permissions.AchatsRetourFournisseur];

    public static void MapAchats(this RouteGroupBuilder api)
    {
        var achats = api.MapGroup("/achats");

        achats.MapGet("/fournisseurs", async (ListerFournisseurs lister, CancellationToken ct) =>
                Results.Ok((await lister.ExecuterAsync(ct)).Select(f => new FournisseurDto(f.Id, f.RaisonSociale, f.Telephone, f.Solde, f.DelaiRepriseJours))))
           .ExigerPermission(Lecture);

        // ---- Commandes ----
        achats.MapGet("/commandes", async (ListerCommandes lister, CancellationToken ct) =>
                Results.Ok((await lister.ExecuterAsync(ct)).Select(VersDto)))
           .ExigerPermission(Lecture);

        achats.MapGet("/commandes/{id:long}", async (long id, ObtenirCommande obtenir, CancellationToken ct) =>
                await obtenir.ExecuterAsync(id, ct) is { } c ? Results.Ok(VersDto(c)) : Results.NotFound(new ErreurDto("Commande introuvable.")))
           .ExigerPermission(Lecture);

        achats.MapGet("/suggestions", async (ListerSuggestionsCommande lister, CancellationToken ct) =>
                Results.Ok((await lister.ExecuterAsync(ct)).Select(s => new SuggestionCommandeDto(
                    s.ProduitId, s.Produit, s.Statut, s.Stock, s.QteSuggeree, s.DernierPrixHt, s.DernierFournisseurId, s.DernierFournisseur))))
           .ExigerPermission(Permissions.AchatsCommander);

        achats.MapPost("/commandes/lignes", async (AjoutCommandeRequete req, AjouterACommande ajouter, ClaimsPrincipal user, CancellationToken ct) =>
                Results.Ok(new IdentifiantDto(await ajouter.ExecuterAsync(req.FournisseurId, req.ProduitId, req.Quantite, req.PrixAchatHt, user.UtilisateurId(), ct))))
           .ExigerPermission(Permissions.AchatsCommander);

        achats.MapPut("/commandes/{id:long}/lignes/{ligneId:long}", async (long id, long ligneId, ModificationLigneCommandeRequete req,
                ModifierLigneCommande modifier, ClaimsPrincipal user, CancellationToken ct) =>
            {
                await modifier.ExecuterAsync(id, ligneId, req.Quantite, req.PrixAchatHt, user.UtilisateurId(), ct);
                return Results.NoContent();
            })
           .ExigerPermission(Permissions.AchatsCommander);

        achats.MapPost("/commandes/{id:long}/emettre", async (long id, EmettreCommande emettre, ClaimsPrincipal user, CancellationToken ct) =>
                Fichier(await emettre.ExecuterAsync(id, user.UtilisateurId(), ct)))
           .ExigerPermission(Permissions.AchatsCommander);

        achats.MapGet("/commandes/{id:long}/pdf", async (long id, ImprimerCommande imprimer, CancellationToken ct) =>
                await imprimer.ExecuterAsync(id, ct) is { } d ? Fichier(d) : Results.NotFound(new ErreurDto("Commande introuvable.")))
           .ExigerPermission(Lecture);

        achats.MapPost("/commandes/{id:long}/annuler", async (long id, AnnulerCommande annuler, ClaimsPrincipal user, CancellationToken ct) =>
            {
                await annuler.ExecuterAsync(id, user.UtilisateurId(), ct);
                return Results.NoContent();
            })
           .ExigerPermission(Permissions.AchatsCommander);

        // ---- Réceptions ----
        achats.MapGet("/receptions", async (ListerReceptions lister, CancellationToken ct) =>
                Results.Ok((await lister.ExecuterAsync(ct)).Select(r => new ReceptionResumeDto(
                    r.Id, r.Fournisseur, r.NumeroBl, r.NumeroFacture, r.NumeroCommande, r.Date, r.NombreLots, r.NombreBoites, r.MontantTtc))))
           .ExigerPermission(Lecture);

        achats.MapPost("/datamatrix", async (DataMatrixRequete req, LireDataMatrix lire, CancellationToken ct) =>
            {
                var b = await lire.ExecuterAsync(req.Code, ct);
                return Results.Ok(new BoiteLueDto(b.Gtin, b.NumeroLot, b.Peremption, b.NumeroSerie, b.ProduitId, b.Produit));
            })
           .ExigerPermission(Lecture);

        achats.MapPost("/receptions", async (ReceptionRequete req, Receptionner receptionner, ClaimsPrincipal user, CancellationToken ct) =>
            {
                var r = await receptionner.ExecuterAsync(new SaisieReception(req.FournisseurId, req.CommandeId, req.NumeroBl, req.NumeroFacture,
                    req.Lignes.Select(l => new LigneReceptionSaisie(l.ProduitId, l.Nom, l.NumeroLot, l.DatePeremption, l.Quantite,
                        l.PrixAchatHt, l.Gtin, l.NumerosSerie)).ToList()), user.UtilisateurId(), ct);
                return Results.Ok(new ReceptionEnregistreeDto(r.ReceptionId, r.MontantTtc,
                    r.Lots.Select(l => new LotRecuDto(l.LotId, l.Produit, l.NumeroLot, l.DatePeremption, l.Quantite, l.BoitesSansIdentite)).ToList()));
            })
           .ExigerPermission(Permissions.AchatsReceptionner);

        // ---- Retours ----
        achats.MapGet("/retours", async (ListerRetours lister, CancellationToken ct) =>
                Results.Ok((await lister.ExecuterAsync(ct)).Select(VersDto)))
           .ExigerPermission(Lecture);

        achats.MapGet("/lots/{id:long}/retour", async (long id, ObtenirLotRetournable obtenir, CancellationToken ct) =>
                await obtenir.ExecuterAsync(id, ct) is { } l
                    ? Results.Ok(VersDto(l))
                    : Results.NotFound(new ErreurDto("Lot introuvable.")))
           .ExigerPermission(Permissions.AchatsRetourFournisseur);

        achats.MapGet("/lots", async (long produitId, string? numeroLot, ListerLotsRetournables lister, CancellationToken ct) =>
                Results.Ok((await lister.ExecuterAsync(produitId, numeroLot, ct)).Select(VersDto)))
           .ExigerPermission(Permissions.AchatsRetourFournisseur);

        achats.MapPost("/retours", async (PreparationRetourRequete req, PreparerRetour preparer, ClaimsPrincipal user, CancellationToken ct) =>
            {
                MotifRetour motif;
                try { motif = CodesMetier.MotifRetourDepuisCode(req.Motif); }
                catch (ArgumentException) { throw new RegleMetierException("Motif de retour inconnu."); }
                return Results.Ok(new IdentifiantDto(await preparer.ExecuterAsync(motif,
                    req.Lots.Select(l => new LotARetourner(l.LotId, l.Quantite, l.CodesBoites ?? [])).ToList(), user.UtilisateurId(), ct)));
            })
           .ExigerPermission(Permissions.AchatsRetourFournisseur);

        achats.MapPost("/retours/{id:long}/envoyer", async (long id, SuivreRetour suivre, ClaimsPrincipal user, CancellationToken ct) =>
                Fichier(await suivre.EnvoyerAsync(id, user.UtilisateurId(), ct)))
           .ExigerPermission(Permissions.AchatsRetourFournisseur);

        achats.MapGet("/retours/{id:long}/pdf", async (long id, SuivreRetour suivre, CancellationToken ct) =>
                await suivre.ImprimerAsync(id, ct) is { } d ? Fichier(d) : Results.NotFound(new ErreurDto("Retour introuvable.")))
           .ExigerPermission(Lecture);

        achats.MapPost("/retours/{id:long}/avoir", async (long id, AvoirRequete req, SuivreRetour suivre, ClaimsPrincipal user, CancellationToken ct) =>
            {
                await suivre.EnregistrerAvoirAsync(id, req.Montant, user.UtilisateurId(), ct);
                return Results.NoContent();
            })
           .ExigerPermission(Permissions.AchatsRetourFournisseur);

        achats.MapPost("/retours/{id:long}/refuser", async (long id, RefusRetourRequete req, SuivreRetour suivre, ClaimsPrincipal user, CancellationToken ct) =>
            {
                await suivre.RefuserAsync(id, req.Motif, user.UtilisateurId(), ct);
                return Results.NoContent();
            })
           .ExigerPermission(Permissions.AchatsRetourFournisseur);

        // ---- Étiquettes internes ----
        achats.MapGet("/etiquettes/lots", async (ListerLotsAEtiqueter lister, CancellationToken ct) =>
                Results.Ok((await lister.ExecuterAsync(ct)).Select(l => new LotAEtiqueterDto(
                    l.LotId, l.Produit, l.NumeroLot, l.DatePeremption, l.Disponible, l.BoitesSansIdentite))))
           .ExigerPermission(Permissions.AchatsReceptionner, Permissions.StockConsulter);

        achats.MapPost("/etiquettes", async (EtiquettesRequete req, ImprimerEtiquettes imprimer, ClaimsPrincipal user, CancellationToken ct) =>
            {
                var e = await imprimer.ExecuterAsync(req.LotId, req.Nombre, user.UtilisateurId(), ct);
                return Results.Ok(new EtiquettesDto(e.Codes, Convert.ToBase64String(e.Pdf), e.Zpl));
            })
           .ExigerPermission(Permissions.AchatsReceptionner);
    }

    private static IResult Fichier(DocumentGenere d) => Results.File(d.Contenu, "application/pdf", d.NomFichier);

    private static CommandeAchatDto VersDto(CommandeResume c) =>
        new(c.Id, c.Numero, c.FournisseurId, c.Fournisseur, c.Date, c.Statut, c.NombreLignes, c.MontantHt);

    private static CommandeAchatDetailDto VersDto(CommandeDetail c) =>
        new(VersDto(c.Entete), c.Lignes.Select(l => new LigneCommandeAchatDto(l.Id, l.ProduitId, l.Produit, l.QteCommandee, l.QteRecue, l.PrixAchatHt)).ToList());

    private static LotRetournableDto VersDto(LotRetournable l) =>
        new(l.LotId, l.ProduitId, l.Produit, l.NumeroLot, l.DatePeremption, l.FournisseurId,
            l.Fournisseur, l.Disponible, l.PrixAchatHt, l.Bloque, l.Boites.Count);

    private static RetourDto VersDto(RetourResume r) =>
        new(r.Id, r.Numero, r.FournisseurId, r.Fournisseur, r.Motif, r.Statut, r.CreeLe, r.EnvoyeLe, r.AvoirRecuLe,
            r.MontantAttendu, r.MontantAvoir, r.Lignes.Select(l => new LigneRetourDto(l.Produit, l.NumeroLot, l.Quantite)).ToList());
}
