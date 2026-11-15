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
                    b.LotConseille is null ? null : Lot(b.LotConseille), b.Avertissement, b.Emplacement, b.CouleurAvertissement, b.BoiteId));
            })
           .ExigerPermission(Permissions.CaisseVendre);

        // La caisse est celle du poste (en-tête X-Pharma-Poste) : plusieurs employés vendent sur le même tiroir
        caisse.MapGet("/etat", async (ObtenirEtatCaisse obtenir, ClaimsPrincipal user, HttpRequest requete, CancellationToken ct) =>
            {
                var e = await obtenir.ExecuterAsync(user.UtilisateurId(), requete.Poste(), ct);
                return Results.Ok(new EtatCaisseDto(e.SessionId, e.OuverteLe, e.FondCaisse, e.Poste,
                    e.Parametres.ComptageAveugle, e.Parametres.FondCaisseDefaut, e.Parametres.SeuilEcart,
                    e.Parametres.DeconnexionMinutes, e.PeutVoirEcart, e.VentesEspecesVisibles, e.OuvertePar, user.ParCodeComptoir(), e.Parametres.PlafondCreditDefaut));
            })
           .ExigerPermission(Permissions.CaisseVendre);

        caisse.MapPost("/ouvrir", async (OuvrirCaisseRequete req, OuvrirSessionCaisse ouvrir, ClaimsPrincipal user, HttpRequest requete, CancellationToken ct) =>
                Results.Ok(new IdentifiantDto(await ouvrir.ExecuterAsync(user.UtilisateurId(), requete.Poste() ?? req.Poste, req.FondCaisse, ct))))
           .ExigerPermission(Permissions.CaisseVendre);

        caisse.MapPost("/calculer", async (CommandeVenteDto req, Encaisser encaisser, ClaimsPrincipal user, CancellationToken ct) =>
            {
                var a = await encaisser.CalculerAsync(VersCommande(req), user.UtilisateurId(), ct);
                return Results.Ok(new ApercuVenteDto(a.MontantBrut, a.PartOrganisme, a.MontantRemise, a.PartPatient, a.MontantTotal, a.OrdonnanceRequise,
                    a.PrixLignes, a.Shp, a.TotalMedicaments, a.ProduitsBloques, a.SurOrdonnance,
                    a.Situations?.Select(s => new SituationLotDto(s.LotId, s.Situation, s.LotDelaisse, s.PeremptionDelaisse)).ToList(),
                    a.MontantDon, a.ReductionsAuto, a.Reductions?.Select(r => new ReductionLigneDto(r.RemiseAutoPct, r.PctApplique, r.Montant, r.DonPartiel)).ToList()));
            })
           .ExigerPermission(Permissions.CaisseVendre);

        // Volet Chifa scanné : vente reprise sans ressaisie (format du QR à confirmer avec l'éditeur de Chifa)
        caisse.MapPost("/volet-chifa", async (LireVoletChifaRequete req, LireVoletChifa lire, ClaimsPrincipal user, CancellationToken ct) =>
            {
                var v = await lire.ExecuterAsync(req.Texte, user.UtilisateurId(), ct);
                return Results.Ok(new VoletChifaDto(v.Qr.NumeroFacture, v.Qr.DateFacture, v.Qr.AssureComplet, v.Qr.Champ5, v.Qr.Champ6, v.Qr.DateSecondaire,
                    v.Qr.MontantOrdonnance, v.Lignes.Select(l => new LigneVoletChifaDto(l.Code, l.Quantite, l.PrixUnitaire, l.Produit?.Id, l.Produit?.Nom,
                        l.Produit?.PrixPublic)).ToList(), v.PatientId, v.Patient, v.OrganismeId, v.Qr.Brut, v.PatientNumAssure));
            })
           .ExigerPermission(Permissions.CaisseVendre);

        caisse.MapPost("/codes-chifa", async (AssocierCodeChifaRequete req, AssocierCodeChifa associer, ClaimsPrincipal user, CancellationToken ct) =>
                Results.Ok(new { Produit = await associer.ExecuterAsync(req.Code, req.ProduitId, user.UtilisateurId(), ct) }))
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
                var vue = await cloturer.ExecuterAsync(user.UtilisateurId(), requete.Poste(), req.Saisies.Requise(), ct, req.ConfirmerZero);
                var verse = await verser.ApresClotureAutomatiqueAsync(vue.SessionId, user.UtilisateurId(), ct);
                return Results.Ok(VersDto(vue) with { VerseAuCoffre = verse });
            })
           .ExigerPermission(Permissions.CaisseVendre);

        // Ticket en PDF (2026-11-15) : montré à l'écran quand l'imprimante de tickets du poste enregistre un fichier
        caisse.MapGet("/ventes/{id:long}/ticket.pdf", async (long id, decimal? rendu, ObtenirTicket obtenir, Pharma.Application.Caisse.IDocumentsTicket documents, CancellationToken ct) =>
            {
                var t = await obtenir.ExecuterAsync(id, ct);
                return Results.File(documents.Ticket(t, rendu), "application/pdf", $"ticket_{t.Numero}.pdf");
            })
           .ExigerPermission(Permissions.CaisseVendre);

        // Ticket de caisse : contenu mis en page et imprimé par le poste (imprimante Windows)
        caisse.MapGet("/ventes/{id:long}/ticket", async (long id, ObtenirTicket obtenir, CancellationToken ct) =>
            {
                var t = await obtenir.ExecuterAsync(id, ct);
                return Results.Ok(new TicketDto(t.Numero, t.Date, t.Vendeur, t.Poste, t.Statut, t.Organisme, t.TauxTiersPayant,
                    t.Lignes.Select(l => new LigneTicketDto(l.Produit, l.Quantite, l.PrixUnitaire, l.Montant, l.PartOrganisme, l.Shp, l.Medicament,
                        l.Reduction, l.PctReduction, l.DonPartiel)).ToList(),
                    t.MontantBrut, t.Remise, t.PartOrganisme, t.PartPatient, t.Mode, t.Pharmacie, t.Adresse, t.Telephone, t.Nif, t.Retours, t.DonPartiel));
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

        caisse.MapDelete("/attente/{id:long}", async (long id, GererVentesEnAttente gerer, HttpRequest requete, ClaimsPrincipal user, CancellationToken ct) =>
            {
                await gerer.SupprimerAsync(id, requete.Poste(), user.UtilisateurId(), ct);
                return Results.NoContent();
            })
           .ExigerPermission(Permissions.CaisseVendre);

        // Annulation d'une vente : recherche par n° de ticket, ou dernières ventes de la caisse du poste
        caisse.MapGet("/ventes", async (string? numero, RechercherVenteAnnulable rechercher, HttpRequest requete, CancellationToken ct) =>
                Results.Ok((await rechercher.ExecuterAsync(numero, requete.Poste(), ct)).Select(VersDto)))
           .ExigerPermission(Permissions.CaisseVendre);

        // Ventes de la caisse ouverte du poste, par page (règle des listes) ; ou la vente d'un n° de ticket
        caisse.MapGet("/ventes/page", async (string? numero, int? page, int? taille, RechercherVenteAnnulable rechercher, HttpRequest requete, CancellationToken ct) =>
            {
                var p = await rechercher.PageAsync(numero, requete.Poste(), Pharma.Application.Commun.DemandePage.Lire(page, taille), ct);
                return Results.Ok(new PageDto<VenteAnnulableDto>(p.Lignes.Select(VersDto).ToList(), p.Total, p.Numero, p.Taille));
            })
           .ExigerPermission(Permissions.CaisseVendre);

        // Remboursement sans ticket : la boîte rapportée désigne sa vente, ou les ventes récentes de ce produit (sans patient,
        // ou de ce patient s'il s'est nommé)
        caisse.MapGet("/ventes/par-boite", async (string? code, int? jours, long? patientId, string? lot, RechercherVenteParBoite rechercher,
                CancellationToken ct) =>
            {
                var r = await rechercher.ExecuterAsync(code, jours, patientId, lot, ct);
                return Results.Ok(new VentesParBoiteDto(r.Exacte, r.Produit, r.Lot, r.Note, r.Ventes.Select(VersDto).ToList()));
            })
           .ExigerPermission(Permissions.CaisseVendre);

        // Vente récente : le caissier annule ; vente ancienne : validation sur place par le code d'un employé habilité
        caisse.MapPost("/ventes/{id:long}/annuler", async (long id, AnnulationVenteRequete req, AnnulerVente annuler, ClaimsPrincipal user,
                HttpRequest requete, CancellationToken ct) =>
            {
                var r = await annuler.ExecuterAsync(id, req.Motif, user.UtilisateurId(), requete.Poste(), req.ValidateurIdentifiant, req.ValidateurCode, ct,
                    req.SolderDette, req.PatientDetteId, req.AccordClient);
                return Results.Ok(new AnnulationVenteDto(r.Numero, r.ARendre, r.Mode, r.ValidePar, r.DetteDiminuee));
            })
           .ExigerPermission(Permissions.CaisseVendre);

        // Retour de médicaments par le patient : le pharmacien choisit, produit par produit, remis en stock ou à détruire
        caisse.MapGet("/ventes/{id:long}/retour", async (long id, LireVenteRetournable lire, CancellationToken ct) =>
            {
                var r = await lire.ExecuterAsync(id, ct);
                var v = r.Vente;
                return Results.Ok(new VenteRetournableDto(
                    VersDto(v),
                    r.Lignes.Select(l => new LigneRetournableDto(l.LigneVenteId, l.Produit, l.NumeroLot, l.QuantiteVendue, l.DejaRendue, l.MontantPatient,
                        l.Boites.Select(b => new BoiteVendueDto(b.Id, b.NumeroSerie)).ToList())).ToList()));
            })
           .ExigerPermission(Permissions.CaisseVendre);

        caisse.MapPost("/ventes/{id:long}/retour", async (long id, RetourClientRequete req, EnregistrerRetourClient enregistrer, ClaimsPrincipal user,
                HttpRequest requete, CancellationToken ct) =>
            {
                var r = await enregistrer.ExecuterAsync(id, req.Motif,
                    req.Lignes.Requise().Select(l => new LigneRetourDemandee(l.LigneVenteId, l.Quantite, l.Destination, l.Boites)).ToList(),
                    user.UtilisateurId(), requete.Poste(), req.ValidateurIdentifiant, req.ValidateurCode, req.SolderDette, req.PatientDetteId, ct,
                    req.AccordClient);
                return Results.Ok(new RetourClientDto(r.Numero, r.ARendre, r.Mode, r.RemisEnStock, r.ADetruire, r.ValidePar, r.DetteDiminuee));
            })
           .ExigerPermission(Permissions.CaisseVendre);

        caisse.MapGet("/validateurs-retour", async (Pharma.Application.Securite.ListerValidateurs lister, CancellationToken ct) =>
                Results.Ok((await lister.ExecuterAsync(Permissions.CaisseRetourEspeces, ct)).Select(v => new VendeurComptoirDto(v.Identifiant, v.NomComplet))))
           .ExigerPermission(Permissions.CaisseVendre);

        // Qui peut délivrer un psychotrope : affiché au vendeur qui n'a pas ce droit, dès que le produit est au panier
        caisse.MapGet("/delivrance-psychotropes", async (Pharma.Application.Securite.IDetenteursDroit detenteurs, CancellationToken ct) =>
                Results.Ok((await detenteurs.ListerAsync(Permissions.PsychotropesDelivrer, ct)).Select(d => new DetenteurDroitDto(d.NomComplet, d.CodeComptoir))))
           .ExigerPermission(Permissions.CaisseVendre);

        // Qui peut autoriser sur place une action refusée faute de droit (droit donné, ou « role.titulaire ») ;
        // ?remise= : remise refusée (caisse.remise), seuls les employés dont le maximum de remise la couvre
        api.MapGet("/auth/valideurs", async (string? permission, decimal? remise, Pharma.Application.Securite.ListerValidateurs lister, ClaimsPrincipal user,
                CancellationToken ct) =>
                Results.Ok((await lister.ExecuterAsync(permission ?? Pharma.Application.Securite.PermissionServiceExtensions.MarqueTitulaire, ct, user.UtilisateurId(),
                        remise is > 0 ? remise : null))
                    .Select(v => new VendeurComptoirDto(v.Identifiant, v.NomComplet)).ToList()))
           .ExigerConnexion();

        // Qui peut valider un produit bloqué (à la vente ou à l'achat) avec son code comptoir
        api.MapGet("/produits/validateurs-blocage", async (Pharma.Application.Securite.ListerValidateurs lister, CancellationToken ct) =>
                Results.Ok((await lister.ExecuterAsync(Permissions.ProduitsBloquer, ct)).Select(v => new VendeurComptoirDto(v.Identifiant, v.NomComplet))))
           .ExigerConnexion();

        caisse.MapGet("/validateurs-annulation", async (Pharma.Application.Securite.ListerValidateurs lister, CancellationToken ct) =>
                Results.Ok((await lister.ExecuterAsync(Permissions.CaisseAnnulerVente, ct)).Select(v => new VendeurComptoirDto(v.Identifiant, v.NomComplet))))
           .ExigerPermission(Permissions.CaisseVendre);

        // Relève au changement d'équipe : comptage, clôture, et caisse rouverte aussitôt avec l'argent compté
        // État intermédiaire : récapitulatif sans espèces et comptage du tiroir sans clôture (la caisse reste ouverte)
        caisse.MapGet("/etat-intermediaire", async (GererEtatIntermediaire gerer, ClaimsPrincipal user, HttpRequest requete, CancellationToken ct) =>
            {
                var e = await gerer.LireAsync(user.UtilisateurId(), requete.Poste(), ct);
                return Results.Ok(new EtatIntermediaireDto(e.SessionId, e.Poste, e.OuverteLe, e.OuvertePar, e.FondCaisse, e.Resume.NombreVentes,
                    e.Resume.Carte, e.Resume.Cheque, e.Resume.Credit, e.Resume.TiersPayant,
                    e.Resume.ParEmploye.Select(x => new VentesEmployeDto(x.Employe, x.Ventes)).ToList(),
                    e.Comptages.Select(c => new ComptageIntermediaireDto(c.Le, c.Employe, c.Compte, c.Ecart)).ToList(), e.PeutVoirEcart));
            })
           .ExigerPermission(Permissions.CaisseVendre);

        caisse.MapPost("/comptage-intermediaire", async (ClotureRequete req, GererEtatIntermediaire gerer, ClaimsPrincipal user, HttpRequest requete,
                CancellationToken ct) =>
            {
                var r = await gerer.CompterAsync(user.UtilisateurId(), requete.Poste(), req.Saisies.Requise(), ct);
                // Même présentation que la clôture (écart seulement avec le droit de voir les écarts)
                return Results.Ok(new ClotureDto(r.SessionId, r.Compte, r.FondCaisse, r.VentesEspeces, r.Attendu, r.Ecart, r.AuDelaDuSeuil, r.SeuilEcart));
            })
           .ExigerPermission(Permissions.CaisseVendre);

        caisse.MapPost("/releve", async (ClotureRequete req, ReleverCaisse relever, ClaimsPrincipal user, HttpRequest requete, CancellationToken ct) =>
            {
                var r = await relever.ExecuterAsync(user.UtilisateurId(), requete.Poste(), req.Saisies.Requise(), ct, req.ConfirmerZero);
                return Results.Ok(new ReleveDto(VersDto(r.Cloture), r.NouvelleSessionId));
            })
           .ExigerPermission(Permissions.CaisseVendre);

        caisse.MapGet("/sessions/{id:long}/rapport", async (long id, ObtenirRapportCloture rapport, ClaimsPrincipal user, CancellationToken ct) =>
                await rapport.ExecuterAsync(id, user.UtilisateurId(), ct) is { } r
                    ? Results.File(r.Contenu, "application/pdf", r.NomFichier)
                    : Results.NotFound(new ErreurDto("Session de caisse introuvable.")))
           .ExigerPermission(Permissions.CaisseVendre);

        // Écarts de caisse récents : tuile de l'accueil du titulaire, marqués vus par lui (les autres employés ne voient rien)
        caisse.MapGet("/ecarts-recents", async (string? q, int? page, int? taille, ConsulterEcartsCaisseRecents consulter, ClaimsPrincipal user, CancellationToken ct) =>
            {
                var e = await consulter.ExecuterAsync(user.UtilisateurId(), q, Pharma.Application.Commun.DemandePage.Lire(page, taille), ct);
                return Results.Ok(new EcartsCaisseRecentsDto(e.Nombre,
                    e.Derniers.Select(x => new EcartCaisseRecentDto(x.SessionId, x.Poste, x.Employe, x.ClotureLe, x.Ecart, x.ClotureePar, x.OuverteLe, x.Releve)).ToList()));
            })
           .ExigerTitulaire();

        caisse.MapPost("/ecarts-recents/vus", async (AlertesVuesRequete d, MarquerEcartsCaisseVus marquer, ClaimsPrincipal user, CancellationToken ct) =>
                Results.Ok(new NombreDto(await marquer.ExecuterAsync(d.JusquA, user.UtilisateurId(), ct))))
           .ExigerTitulaire()
           .GererErreursMetier();
    }

    /// <summary>Ce que l'annulation rendrait (retours déjà rendus déduits, part d'un crédit déjà remboursée).</summary>
    private static decimal ARendre(Pharma.Application.Caisse.VenteAnnulable v) =>
        Pharma.Domain.Ventes.RegleAnnulation.Remboursement(v.Mode, v.PartPatient, v.DejaRendu, v.SoldeCreditPatient).ARendre;

    private static ClotureDto VersDto(ClotureVue c) =>
        new(c.SessionId, c.Compte, c.FondCaisse, c.VentesEspeces, c.Attendu, c.Ecart, c.AuDelaDuSeuil, c.SeuilEcart);

    /// <summary>Vente vue pour l'annulation ou le retour ; vente à crédit : dette de cette vente que l'annulation effacerait.</summary>
    private static VenteAnnulableDto VersDto(VenteAnnulable v) => new(
        v.Id, v.Numero, v.Date, v.Vendeur, v.Patient, v.Total, v.PartPatient, v.PartOrganisme, v.Mode, v.Statut,
        v.CaisseOuverte, v.DansUnBordereau, v.Lignes.Select(l => new LigneVenteAnnulableDto(l.Produit, l.Quantite, l.Montant, l.Lot)).ToList(),
        ARendre(v), v.FactureNumero, v.SoldeCreditPatient, v.VendeurId, v.PatientId,
        RegleAnnulation.Remboursement(v.Mode, v.PartPatient, v.DejaRendu, v.SoldeCreditPatient).ReductionDette);

    private static CommandeVente VersCommande(CommandeVenteDto d)
    {
        ModePaiement mode;
        try { mode = CodesMetier.ModePaiementDepuisCode(d.Mode); }
        catch (ArgumentException) { throw new RegleMetierException($"Mode de paiement inconnu : {d.Mode}."); }

        return new CommandeVente(
            d.Lignes.Requise().Select(l => new LigneCommande(l.ProduitId, l.LotId, l.Quantite, l.Boites, l.RaisonLot)).ToList(),
            d.TiersPayant, d.OrganismeId, d.TauxTiersPayant, d.PatientId,
            d.Ordonnance is { } o ? new OrdonnanceSaisie(o.MedecinId, o.DatePrescription, o.Numero, o.JetonScan, o.TexteReconnu,
                o.PorteurNom is null && o.PorteurAdresse is null && o.PorteurPiece is null ? null : new PorteurOrdonnance(o.PorteurNom, o.PorteurAdresse, o.PorteurPiece)) : null,
            d.RemisePct, mode, d.MontantRecu, d.ResteACharge,
            d.Blocage is { } b ? new Pharma.Application.Catalogue.ValidationBlocage(b.Accepte, b.ValidateurIdentifiant, b.ValidateurCode) : null,
            d.VoletChifa, d.RemiseAdditionnee, d.CleEncaissement);
    }
}
