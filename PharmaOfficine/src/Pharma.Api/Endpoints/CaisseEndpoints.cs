using System.Security.Claims;
using Pharma.Api.Securite;
using Pharma.Application.Caisse;
using Pharma.Domain.Commun;
using Pharma.Domain.Securite;
using Pharma.Domain.Ventes;
using Pharma.Shared;

namespace Pharma.Api.Endpoints;

internal static class CaisseEndpoints
{
    public static void MapCaisse(this RouteGroupBuilder api)
    {
        var caisse = api.MapGroup("/caisse");

        // Boîte scannée : lot exact servi, et avertissement si un lot proche de la péremption doit partir avant
        caisse.MapPost("/boite", async (CodeRequete req, ControlerBoiteCaisse controler, ClaimsPrincipal user, CancellationToken ct) =>
            {
                var b = await controler.ExecuterAsync(req.Code, user.UtilisateurId(), ct);
                if (b is null) return Results.NoContent();
                LotFefoDto Lot(LotCaisse l) => new(l.LotId, l.NumeroLot, l.DatePeremption, l.Quantite, l.Fournisseur, [], b.StockProduit);
                return Results.Ok(new BoiteCaisseDto(b.ProduitId, b.Produit, b.PrixPublic, b.StockProduit, Lot(b.Lot),
                    b.LotConseille is null ? null : Lot(b.LotConseille), b.Avertissement));
            })
           .ExigerPermission(Permissions.CaisseVendre);

        // La caisse est celle du poste (en-tête X-Pharma-Poste) : plusieurs employés vendent sur le même tiroir
        caisse.MapGet("/etat", async (ObtenirEtatCaisse obtenir, ClaimsPrincipal user, HttpRequest requete, CancellationToken ct) =>
            {
                var e = await obtenir.ExecuterAsync(user.UtilisateurId(), requete.Poste(), ct);
                return Results.Ok(new EtatCaisseDto(e.SessionId, e.OuverteLe, e.FondCaisse, e.Poste,
                    e.Parametres.ComptageAveugle, e.Parametres.FondCaisseDefaut, e.Parametres.SeuilEcart,
                    e.Parametres.DeconnexionMinutes, e.PeutVoirEcart, e.VentesEspecesVisibles, e.OuvertePar, user.ParCodeComptoir()));
            })
           .ExigerPermission(Permissions.CaisseVendre);

        caisse.MapPost("/ouvrir", async (OuvrirCaisseRequete req, OuvrirSessionCaisse ouvrir, ClaimsPrincipal user, HttpRequest requete, CancellationToken ct) =>
                Results.Ok(new IdentifiantDto(await ouvrir.ExecuterAsync(user.UtilisateurId(), requete.Poste() ?? req.Poste, req.FondCaisse, ct))))
           .ExigerPermission(Permissions.CaisseVendre);

        caisse.MapPost("/calculer", async (CommandeVenteDto req, Encaisser encaisser, ClaimsPrincipal user, CancellationToken ct) =>
            {
                var a = await encaisser.CalculerAsync(VersCommande(req), user.UtilisateurId(), ct);
                return Results.Ok(new ApercuVenteDto(a.MontantBrut, a.PartOrganisme, a.MontantRemise, a.PartPatient, a.MontantTotal, a.OrdonnanceRequise));
            })
           .ExigerPermission(Permissions.CaisseVendre);

        caisse.MapPost("/encaisser", async (CommandeVenteDto req, Encaisser encaisser, ClaimsPrincipal user, HttpRequest requete, CancellationToken ct) =>
            {
                var v = await encaisser.ExecuterAsync(VersCommande(req), user.UtilisateurId(), requete.Poste(), ct);
                return Results.Ok(new VenteEncaisseeDto(v.VenteId, v.Numero, v.MontantTotal, v.PartOrganisme, v.PartPatient, v.Mode.Code(), v.Rendu));
            })
           .ExigerPermission(Permissions.CaisseVendre);

        // Clôture : si le titulaire a choisi le versement automatique, le compté moins le fond par défaut part au coffre (pas à la relève)
        caisse.MapPost("/cloturer", async (ClotureRequete req, CloturerSessionCaisse cloturer, Pharma.Application.Tresorerie.VerserCaisseAuCoffre verser,
                ClaimsPrincipal user, HttpRequest requete, CancellationToken ct) =>
            {
                var vue = await cloturer.ExecuterAsync(user.UtilisateurId(), requete.Poste(), req.Saisies, ct);
                var verse = await verser.ApresClotureAutomatiqueAsync(vue.SessionId, user.UtilisateurId(), ct);
                return Results.Ok(VersDto(vue) with { VerseAuCoffre = verse });
            })
           .ExigerPermission(Permissions.CaisseVendre);

        // Ticket de caisse : contenu mis en page et imprimé par le poste (imprimante Windows)
        caisse.MapGet("/ventes/{id:long}/ticket", async (long id, ObtenirTicket obtenir, CancellationToken ct) =>
            {
                var t = await obtenir.ExecuterAsync(id, ct);
                return Results.Ok(new TicketDto(t.Numero, t.Date, t.Vendeur, t.Poste, t.Statut, t.Organisme, t.TauxTiersPayant,
                    t.Lignes.Select(l => new LigneTicketDto(l.Produit, l.Quantite, l.PrixUnitaire, l.Montant, l.PartOrganisme)).ToList(),
                    t.MontantBrut, t.Remise, t.PartOrganisme, t.PartPatient, t.Mode, t.Pharmacie, t.Adresse, t.Telephone, t.Nif));
            })
           .ExigerPermission(Permissions.CaisseVendre);

        // Ventes en attente du poste : mises de côté, reprises par n'importe quel employé du poste
        caisse.MapGet("/attente", async (GererVentesEnAttente gerer, HttpRequest requete, CancellationToken ct) =>
                Results.Ok((await gerer.ListerAsync(requete.Poste(), ct))
                    .Select(v => new VenteEnAttenteDto(v.Id, v.Libelle, v.Lignes, v.Total, v.Employe, v.CreeLe))))
           .ExigerPermission(Permissions.CaisseVendre);

        caisse.MapPost("/attente", async (MiseEnAttenteRequete req, GererVentesEnAttente gerer, ClaimsPrincipal user, HttpRequest requete, CancellationToken ct) =>
                Results.Ok(new IdentifiantDto(await gerer.MettreEnAttenteAsync(requete.Poste(), user.UtilisateurId(), req.Libelle, req.Lignes, req.Total, req.Contenu, ct))))
           .ExigerPermission(Permissions.CaisseVendre);

        caisse.MapPost("/attente/{id:long}/reprendre", async (long id, GererVentesEnAttente gerer, HttpRequest requete, CancellationToken ct) =>
                Results.Content(await gerer.ReprendreAsync(id, requete.Poste(), ct), "application/json"))
           .ExigerPermission(Permissions.CaisseVendre);

        caisse.MapDelete("/attente/{id:long}", async (long id, GererVentesEnAttente gerer, HttpRequest requete, CancellationToken ct) =>
            {
                await gerer.SupprimerAsync(id, requete.Poste(), ct);
                return Results.NoContent();
            })
           .ExigerPermission(Permissions.CaisseVendre);

        // Annulation d'une vente : recherche par n° de ticket, ou dernières ventes de la caisse du poste
        caisse.MapGet("/ventes", async (string? numero, RechercherVenteAnnulable rechercher, HttpRequest requete, CancellationToken ct) =>
                Results.Ok((await rechercher.ExecuterAsync(numero, requete.Poste(), ct)).Select(v => new VenteAnnulableDto(
                    v.Id, v.Numero, v.Date, v.Vendeur, v.Patient, v.Total, v.PartPatient, v.PartOrganisme, v.Mode, v.Statut,
                    v.CaisseOuverte, v.DansUnBordereau, v.Lignes.Select(l => new LigneVenteAnnulableDto(l.Produit, l.Quantite, l.Montant)).ToList(),
                    ARendre(v), v.FactureNumero))))
           .ExigerPermission(Permissions.CaisseVendre);

        // Vente récente : le caissier annule ; vente ancienne : validation sur place par le code d'un employé habilité
        caisse.MapPost("/ventes/{id:long}/annuler", async (long id, AnnulationVenteRequete req, AnnulerVente annuler, ClaimsPrincipal user,
                HttpRequest requete, CancellationToken ct) =>
            {
                var r = await annuler.ExecuterAsync(id, req.Motif, user.UtilisateurId(), requete.Poste(), req.ValidateurIdentifiant, req.ValidateurCode, ct);
                return Results.Ok(new AnnulationVenteDto(r.Numero, r.ARendre, r.Mode, r.ValidePar));
            })
           .ExigerPermission(Permissions.CaisseVendre);

        // Retour de médicaments par le patient : le pharmacien choisit, produit par produit, remis en stock ou à détruire
        caisse.MapGet("/ventes/{id:long}/retour", async (long id, LireVenteRetournable lire, CancellationToken ct) =>
            {
                var r = await lire.ExecuterAsync(id, ct);
                var v = r.Vente;
                return Results.Ok(new VenteRetournableDto(
                    new VenteAnnulableDto(v.Id, v.Numero, v.Date, v.Vendeur, v.Patient, v.Total, v.PartPatient, v.PartOrganisme, v.Mode, v.Statut,
                        v.CaisseOuverte, v.DansUnBordereau, v.Lignes.Select(l => new LigneVenteAnnulableDto(l.Produit, l.Quantite, l.Montant)).ToList(),
                        ARendre(v), v.FactureNumero),
                    r.Lignes.Select(l => new LigneRetournableDto(l.LigneVenteId, l.Produit, l.NumeroLot, l.QuantiteVendue, l.DejaRendue, l.MontantPatient,
                        l.Boites.Select(b => new BoiteVendueDto(b.Id, b.NumeroSerie)).ToList())).ToList()));
            })
           .ExigerPermission(Permissions.CaisseVendre);

        caisse.MapPost("/ventes/{id:long}/retour", async (long id, RetourClientRequete req, EnregistrerRetourClient enregistrer, ClaimsPrincipal user,
                HttpRequest requete, CancellationToken ct) =>
            {
                var r = await enregistrer.ExecuterAsync(id, req.Motif,
                    req.Lignes.Select(l => new LigneRetourDemandee(l.LigneVenteId, l.Quantite, l.Destination, l.Boites)).ToList(),
                    user.UtilisateurId(), requete.Poste(), req.ValidateurIdentifiant, req.ValidateurCode, ct);
                return Results.Ok(new RetourClientDto(r.Numero, r.ARendre, r.Mode, r.RemisEnStock, r.ADetruire, r.ValidePar));
            })
           .ExigerPermission(Permissions.CaisseVendre);

        caisse.MapGet("/validateurs-retour", async (Pharma.Application.Securite.ListerValidateurs lister, CancellationToken ct) =>
                Results.Ok((await lister.ExecuterAsync(Permissions.CaisseRetourEspeces, ct)).Select(v => new VendeurComptoirDto(v.Identifiant, v.NomComplet))))
           .ExigerPermission(Permissions.CaisseVendre);

        // Qui peut délivrer un psychotrope : affiché au vendeur qui n'a pas ce droit, dès que le produit est au panier
        caisse.MapGet("/delivrance-psychotropes", async (Pharma.Application.Securite.IDetenteursDroit detenteurs, CancellationToken ct) =>
                Results.Ok((await detenteurs.ListerAsync(Permissions.PsychotropesDelivrer, ct)).Select(d => new DetenteurDroitDto(d.NomComplet, d.CodeComptoir))))
           .ExigerPermission(Permissions.CaisseVendre);

        caisse.MapGet("/validateurs-annulation", async (Pharma.Application.Securite.ListerValidateurs lister, CancellationToken ct) =>
                Results.Ok((await lister.ExecuterAsync(Permissions.CaisseAnnulerVente, ct)).Select(v => new VendeurComptoirDto(v.Identifiant, v.NomComplet))))
           .ExigerPermission(Permissions.CaisseVendre);

        // Relève au changement d'équipe : comptage, clôture, et caisse rouverte aussitôt avec l'argent compté
        caisse.MapPost("/releve", async (ClotureRequete req, ReleverCaisse relever, ClaimsPrincipal user, HttpRequest requete, CancellationToken ct) =>
            {
                var r = await relever.ExecuterAsync(user.UtilisateurId(), requete.Poste(), req.Saisies, ct);
                return Results.Ok(new ReleveDto(VersDto(r.Cloture), r.NouvelleSessionId));
            })
           .ExigerPermission(Permissions.CaisseVendre);

        caisse.MapGet("/sessions/{id:long}/rapport", async (long id, ObtenirRapportCloture rapport, ClaimsPrincipal user, CancellationToken ct) =>
                await rapport.ExecuterAsync(id, user.UtilisateurId(), ct) is { } r
                    ? Results.File(r.Contenu, "application/pdf", r.NomFichier)
                    : Results.NotFound(new ErreurDto("Session de caisse introuvable.")))
           .ExigerPermission(Permissions.CaisseVendre);
    }

    /// <summary>Ce que l'annulation rendrait (retours déjà rendus déduits, part d'un crédit déjà remboursée).</summary>
    private static decimal ARendre(Pharma.Application.Caisse.VenteAnnulable v) =>
        Pharma.Domain.Ventes.RegleAnnulation.Remboursement(v.Mode, v.PartPatient, v.DejaRendu, v.SoldeCreditPatient).ARendre;

    private static ClotureDto VersDto(ClotureVue c) =>
        new(c.SessionId, c.Compte, c.FondCaisse, c.VentesEspeces, c.Attendu, c.Ecart, c.AuDelaDuSeuil, c.SeuilEcart);

    private static CommandeVente VersCommande(CommandeVenteDto d)
    {
        ModePaiement mode;
        try { mode = CodesMetier.ModePaiementDepuisCode(d.Mode); }
        catch (ArgumentException) { throw new RegleMetierException($"Mode de paiement inconnu : {d.Mode}."); }

        return new CommandeVente(
            d.Lignes.Select(l => new LigneCommande(l.ProduitId, l.LotId, l.Quantite)).ToList(),
            d.TiersPayant, d.OrganismeId, d.TauxTiersPayant, d.PatientId,
            d.Ordonnance is { } o ? new OrdonnanceSaisie(o.MedecinId, o.DatePrescription, o.Numero, o.JetonScan, o.TexteReconnu,
                o.PorteurNom is null && o.PorteurAdresse is null && o.PorteurPiece is null ? null : new PorteurOrdonnance(o.PorteurNom, o.PorteurAdresse, o.PorteurPiece)) : null,
            d.RemisePct, mode, d.MontantRecu);
    }
}
