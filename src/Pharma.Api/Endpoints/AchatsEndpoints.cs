using System.Security.Claims;
using Pharma.Api.Securite;
using Pharma.Application.Commun;
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
                Results.Ok((await lister.ExecuterAsync(ct)).Select(f => new FournisseurDto(f.Id, f.RaisonSociale, f.Telephone, f.Solde, f.DelaiRepriseJours, f.Email,
                    f.Adresse, f.Nif, f.Rc, f.Actif))))
           .ExigerPermission(Lecture);

        achats.MapPost("/fournisseurs", async (FicheGrossisteDto d, EnregistrerGrossiste enregistrer, ClaimsPrincipal user, CancellationToken ct) =>
                Results.Ok(new IdentifiantDto(await enregistrer.ExecuterAsync(new FicheGrossiste(d.Id, d.RaisonSociale ?? "", d.Telephone, d.Email, d.Adresse,
                    d.Nif, d.Rc, d.DelaiRepriseJours, d.Actif), user.UtilisateurId(), ct))))
           .ExigerPermission(Permissions.AchatsCommander);

        // ---- Commandes ----
        achats.MapGet("/commandes", async (long? fournisseurId, string? statut, DateOnly? du, DateOnly? au, string? q, int? page, int? taille, ListerCommandes lister, CancellationToken ct) =>
                Results.Ok(VersPage(await lister.ExecuterAsync(DemandePage.Lire(page, taille), FiltreAchats.Lire(fournisseurId, statut, du, au, q), ct), x => VersDto(x))))
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
        // N° de BL ou de facture (saisi ou scanné sur le QR code) déjà réceptionné pour ce grossiste
        achats.MapGet("/receptions/document", async (long fournisseurId, string? numero, ChercherDocumentReception chercher, ClaimsPrincipal user,
                CancellationToken ct) =>
            {
                var liste = await chercher.ExecuterAsync(fournisseurId, numero, user.UtilisateurId(), ct);
                return Results.Ok(new DocumentReceptionDto(RegleDocumentReception.Normaliser(numero),
                    liste.Select(r => new ReceptionResumeDto(r.Id, r.Fournisseur, r.NumeroBl, r.NumeroFacture, r.NumeroCommande, r.Date, 0, 0, r.MontantTtc,
                        r.FournisseurId)).ToList()));
            })
           .ExigerPermission(Permissions.AchatsReceptionner);

        achats.MapGet("/receptions", async (long? fournisseurId, DateOnly? du, DateOnly? au, string? q, int? page, int? taille, ListerReceptions lister, CancellationToken ct) =>
                Results.Ok(VersPage(await lister.ExecuterAsync(DemandePage.Lire(page, taille), FiltreAchats.Lire(fournisseurId, null, du, au, q), ct), r => new ReceptionResumeDto(
                    r.Id, r.Fournisseur, r.NumeroBl, r.NumeroFacture, r.NumeroCommande, r.Date, r.NombreLots, r.NombreBoites, r.MontantTtc, r.FournisseurId))))
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
                        l.PrixAchatHt, l.Gtin, l.NumerosSerie, l.LotSepare, l.EntrerBloque, l.RemisePct, l.Ppa, l.Shp,
                        l.Ug, l.UgCoutReparti, l.RemplacerPrixCatalogue)).ToList(), req.Timbre,
                    req.Blocage is { } b ? new Pharma.Application.Catalogue.ValidationBlocage(b.Accepte, b.ValidateurIdentifiant, b.ValidateurCode) : null),
                    user.UtilisateurId(), ct);
                return Results.Ok(new ReceptionEnregistreeDto(r.ReceptionId, r.MontantTtc,
                    r.Lots.Select(l => new LotRecuDto(l.LotId, l.Produit, l.NumeroLot, l.DatePeremption, l.Quantite, l.Etiquettes, l.LotExistant)).ToList()));
            })
           .ExigerPermission(Permissions.AchatsReceptionner);

        // Infos d'achat des produits d'une réception : SHP proposé, prix public du catalogue (contrôle du PPA facturé), TVA
        achats.MapGet("/produits/infos", async (string? ids, LireInfosAchat lire, CancellationToken ct) =>
            {
                var liste = (ids ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries).Select(x => long.TryParse(x, out var v) ? v : 0).Where(v => v > 0);
                return Results.Ok((await lire.ExecuterAsync(liste, ct))
                    .Select(i => new InfoAchatProduitDto(i.ProduitId, i.Classe, i.PrixPublic, i.TauxTva, i.ShpParBoite, i.BloqueAchat, i.MotifBlocage)).ToList());
            })
           .ExigerPermission(Permissions.AchatsReceptionner);

        achats.MapPost("/receptions/conflits", async (ReceptionRequete req, AnalyserReception analyser, ClaimsPrincipal user, CancellationToken ct) =>
                Results.Ok((await analyser.ExecuterAsync(req.FournisseurId,
                    req.Lignes.Select(l => new LigneReceptionSaisie(l.ProduitId, l.Nom, l.NumeroLot, l.DatePeremption, l.Quantite, l.PrixAchatHt)).ToList(),
                    user.UtilisateurId(), ct)).Select(c => new ConflitReceptionDto(c.Index,
                        c.Type == Pharma.Domain.Stock.ConflitLot.DateDifferente ? "date_differente" : "lot_bloque", c.Message, c.Peremptions))))
           .ExigerPermission(Permissions.AchatsReceptionner);

        // ---- Retours ----
        achats.MapGet("/retours", async (long? fournisseurId, string? statut, DateOnly? du, DateOnly? au, string? q, int? page, int? taille, ListerRetours lister, CancellationToken ct) =>
                Results.Ok(VersPage(await lister.ExecuterAsync(DemandePage.Lire(page, taille), FiltreAchats.Lire(fournisseurId, statut, du, au, q), ct), x => VersDto(x))))
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
                catch (ArgumentException) { throw new RegleMetierException("Motif de retour inconnu.", "motif_retour"); }
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

        achats.MapGet("/receptions/{id:long}/etiquettes", async (long id, ImprimerEtiquettesReception imprimer, ClaimsPrincipal user, CancellationToken ct) =>
            {
                var e = await imprimer.ExecuterAsync(id, user.UtilisateurId(), ct);
                return Results.Ok(new EtiquettesDto(e.Codes, Convert.ToBase64String(e.Pdf), e.Zpl));
            })
           .ExigerPermission(Permissions.AchatsReceptionner);


        // ---- Réception à l'aveugle (057) : comptage sans voir le BL, puis facture saisie et comparée ----
        achats.MapGet("/receptions/mode", async (ModeReception mode, CancellationToken ct) => Results.Ok(new ModeReceptionDto(await mode.AveugleAsync(ct))))
           .ExigerPermission(Lecture);

        achats.MapPost("/receptions/comptages", async (CompterLivraisonRequete req, CompterLivraison compter, ClaimsPrincipal user, CancellationToken ct) =>
                Results.Ok(new IdentifiantDto(await compter.ExecuterAsync(req.FournisseurId, req.NumeroBl,
                    req.Lignes.Select(l => new LigneComptee(l.ProduitId, l.Nom, l.NumeroLot, l.DatePeremption, l.Quantite, l.Gtin, l.NumerosSerie)).ToList(),
                    req.ScansMobile, user.UtilisateurId(), ct))))
           .ExigerPermission(Permissions.AchatsReceptionner);

        achats.MapGet("/receptions/comptages", async (ListerComptagesReception lister, CancellationToken ct) =>
                Results.Ok((await lister.ExecuterAsync(ct)).Select(c => new ComptageResumeDto(c.Id, c.FournisseurId, c.Fournisseur, c.NumeroBl, c.Statut,
                    c.Compteur, c.CompteLe, c.Factureur, c.FactureLe, c.Lignes.Count, c.Ecarts.Select(VersDto).ToList(),
                    c.BoitesManquantes, c.BoitesEnTrop, c.ValeurManquante)).ToList()))
           .ExigerPermission(Permissions.AchatsReceptionner, Permissions.AchatsValiderEcart);

        achats.MapGet("/receptions/comptages/{id:long}", async (long id, ObtenirComptageReception obtenir, CancellationToken ct) =>
            {
                var c = await obtenir.ExecuterAsync(id, ct);
                return Results.Ok(new ComptageAFacturerDto(c.Id, c.FournisseurId, c.NumeroBl, c.Compteur,
                    c.Lignes.Select(l => new LotAFacturerDto(l.ProduitId, l.Nom, l.NumeroLot, l.DatePeremption, l.Gtin)).ToList()));
            })
           .ExigerPermission(Permissions.AchatsReceptionner);

        achats.MapPost("/receptions/comptages/{id:long}/facturer", async (long id, ReceptionRequete req, FacturerComptage facturer, ClaimsPrincipal user,
                CancellationToken ct) =>
            {
                var r = await facturer.ExecuterAsync(id, VersSaisie(req), user.UtilisateurId(), ct);
                return Results.Ok(new ResultatFacturationDto(r.Reception is { } rec ? VersDto(rec) : null, r.Ecarts.Select(VersDto).ToList()));
            })
           .ExigerPermission(Permissions.AchatsReceptionner);

        achats.MapPost("/receptions/comptages/{id:long}/annuler", async (long id, AnnulerComptage annuler, ClaimsPrincipal user, CancellationToken ct) =>
            {
                await annuler.ExecuterAsync(id, user.UtilisateurId(), ct);
                return Results.NoContent();
            })
           .ExigerPermission(Permissions.AchatsReceptionner);

        achats.MapPost("/receptions/comptages/{id:long}/decision", async (long id, DecisionEcartReceptionRequete req, DeciderEcartReception decider,
                ClaimsPrincipal user, CancellationToken ct) =>
            {
                var r = await decider.ExecuterAsync(id, req.Valider, user.UtilisateurId(), ct);
                return r is null ? Results.NoContent() : Results.Ok(VersDto(r));
            })
           .ExigerPermission(Permissions.AchatsValiderEcart);

        achats.MapPost("/etiquettes", async (EtiquettesRequete req, ImprimerEtiquettes imprimer, ClaimsPrincipal user, CancellationToken ct) =>
            {
                var e = await imprimer.ExecuterAsync(req.LotId, req.Nombre, user.UtilisateurId(), ct);
                return Results.Ok(new EtiquettesDto(e.Codes, Convert.ToBase64String(e.Pdf), e.Zpl));
            })
           .ExigerPermission(Permissions.AchatsReceptionner);
    }

    private static IResult Fichier(DocumentGenere d) => Results.File(d.Contenu, "application/pdf", d.NomFichier);

    private static PageDto<TDto> VersPage<T, TDto>(Page<T> p, Func<T, TDto> convertir) =>
        new(p.Lignes.Select(convertir).ToList(), p.Total, p.Numero, p.Taille);

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

    private static SaisieReception VersSaisie(ReceptionRequete req) => new(req.FournisseurId, req.CommandeId, req.NumeroBl, req.NumeroFacture,
        req.Lignes.Select(l => new LigneReceptionSaisie(l.ProduitId, l.Nom, l.NumeroLot, l.DatePeremption, l.Quantite,
            l.PrixAchatHt, l.Gtin, l.NumerosSerie, l.LotSepare, l.EntrerBloque, l.RemisePct, l.Ppa, l.Shp,
            l.Ug, l.UgCoutReparti, l.RemplacerPrixCatalogue)).ToList(), req.Timbre,
        req.Blocage is { } b ? new Pharma.Application.Catalogue.ValidationBlocage(b.Accepte, b.ValidateurIdentifiant, b.ValidateurCode) : null);

    private static ReceptionEnregistreeDto VersDto(ReceptionEnregistree r) => new(r.ReceptionId, r.MontantTtc,
        r.Lots.Select(l => new LotRecuDto(l.LotId, l.Produit, l.NumeroLot, l.DatePeremption, l.Quantite, l.Etiquettes, l.LotExistant)).ToList());

    private static EcartComptageDto VersDto(EcartComptage e) => new(e.ProduitId, e.Nom, e.NumeroLot, e.Facture, e.Compte);
}
