using Pharma.Application.Offre;
using System.Security.Claims;
using Pharma.Api.Securite;
using Pharma.Application.Commun;
using Pharma.Application.Tresorerie;
using Pharma.Domain.Securite;
using Pharma.Domain.Tresorerie;
using Pharma.Shared;

namespace Pharma.Api.Endpoints;

/// <summary>Trésorerie : coffre, banques, dépenses, versements (droit tresorerie.gerer).</summary>
internal static class TresorerieEndpoints
{
    public static void MapTresorerie(this RouteGroupBuilder api)
    {
        var t = api.MapGroup("/tresorerie").ExigerPermission(Permissions.TresorerieGerer).ExigerPack(PacksOffre.Finances);

        t.MapGet("/comptes", async (ConsulterTresorerie c, ClaimsPrincipal user, CancellationToken ct) =>
            Results.Ok((await c.ComptesAsync(user.UtilisateurId(), ct)).Select(VersDto)));

        t.MapPost("/comptes", async (CompteTresorerieRequete r, EnregistrerCompteTresorerie e, ClaimsPrincipal user, CancellationToken ct) =>
            Results.Ok(new IdentifiantDto(await e.ExecuterAsync(VersCompte(null, r), user.UtilisateurId(), ct))));

        t.MapPut("/comptes/{id:long}", async (long id, CompteTresorerieRequete r, EnregistrerCompteTresorerie e, ClaimsPrincipal user, CancellationToken ct) =>
            Results.Ok(new IdentifiantDto(await e.ExecuterAsync(VersCompte(id, r), user.UtilisateurId(), ct))));

        // Paginé et filtré par employé ; totaux de tout le filtre et employés de la période (liste du filtre)
        t.MapGet("/comptes/{id:long}/mouvements", async (long id, DateOnly du, DateOnly au, int? page, int? taille, string? employe,
            ConsulterTresorerie c, ClaimsPrincipal user, CancellationToken ct) =>
            {
                var r = await c.PageMouvementsAsync(id, du, au, employe, DemandePage.Lire(page, taille), user.UtilisateurId(), ct);
                var p = r.Page.Vers(m => new MouvementTresorerieDto(m.Date, m.Sens, m.Montant, m.Nature, m.Categorie, m.Libelle, m.Reference, m.Employe, m.Origine));
                return Results.Ok(new MouvementsTresoreriePageDto(new PageDto<MouvementTresorerieDto>(p.Lignes, p.Total, p.Numero, p.Taille),
                    r.TotalEntrees, r.TotalSorties, r.Employes.ToList()));
            });

        t.MapGet("/a-verser", async (ConsulterTresorerie c, ClaimsPrincipal user, CancellationToken ct) =>
            {
                var a = await c.AVerserAsync(user.UtilisateurId(), ct);
                return Results.Ok(new AVerserDto(
                    a.Cheques.Select(x => new ChequeARemettreDto(x.PaiementId, x.Date, x.Montant, x.Origine, x.Patient, x.PatientId)).ToList(),
                    a.Caisses.Select(x => new CaisseAVerserDto(x.SessionId, x.Poste, x.FermeeLe, x.OuvertePar, x.Compte, x.Propose)).ToList(),
                    a.Bordereaux.Select(x => new BordereauEnvoyeDto(x.Id, x.OrganismeId, x.Organisme, x.Numero, x.Montant, x.Attendu, x.Verse,
                        x.Rejete, x.DateDepot, x.Centre)).ToList()));
            });

        // Types de charges réglés par le titulaire (Administration › Motifs et catégories, script 094) : actives seulement ;
        // « tous=true » : aussi les désactivées, pour afficher le libellé des anciennes dépenses
        t.MapGet("/categories", async (bool? tous, Pharma.Application.Referentiels.IMotifsReglables motifs, CancellationToken ct) =>
            Results.Ok((await motifs.ListerAsync(Pharma.Domain.Referentiels.RegleMotifs.Charge, ct))
                .Where(m => m.Actif || tous == true).Select(c => new CodeLibelleDto(c.Code, c.Libelle))));

        t.MapPost("/mouvements", async (MouvementTresorerieRequete r, SaisirMouvementTresorerie s, ClaimsPrincipal user, CancellationToken ct) =>
            Results.Ok(new IdentifiantDto(await s.ExecuterAsync(r.CompteId, r.Date, r.Nature, r.Montant, r.Categorie, r.Libelle, r.Reference,
                r.OrganismeId, r.BordereauId, r.StatutBordereau, user.UtilisateurId(), r.Factures,
                r.Bordereaux?.Select(b => (b.BordereauId, b.Montant)).ToList(), ct))));

        t.MapPost("/transferts", async (TransfertRequete r, TransfererTresorerie s, ClaimsPrincipal user, CancellationToken ct) =>
            Results.Ok(new IdentifiantDto(await s.ExecuterAsync(r.De, r.Vers, r.Date, r.Montant, r.Reference, user.UtilisateurId(), ct))));

        t.MapPost("/remises-cheques", async (RemiseChequesRequete r, RemettreCheques s, ClaimsPrincipal user, CancellationToken ct) =>
            Results.Ok(new IdentifiantDto(await s.ExecuterAsync(r.CompteId, r.Date, r.Reference, r.Paiements, user.UtilisateurId(), ct))));

        t.MapPost("/caisses/{sessionId:long}/versement", async (long sessionId, MontantRequete r, VerserCaisseAuCoffre v, ClaimsPrincipal user, CancellationToken ct) =>
            Results.Ok(new IdentifiantDto(await v.ApresClotureAsync(sessionId, r.Montant, user.UtilisateurId(), ct))));

        // Pendant la journée, depuis la caisse ouverte du poste : l'attendu de cette caisse diminue d'autant
        t.MapPost("/caisse-du-poste/versement", async (MontantRequete r, VerserCaisseAuCoffre v, ClaimsPrincipal user, HttpRequest requete, CancellationToken ct) =>
            Results.Ok(new IdentifiantDto(await v.PendantJourneeAsync(r.Montant, requete.Poste(), user.UtilisateurId(), ct))));

        // Banques actives, pour choisir le compte débité d'un règlement de grossiste (droit achats.regler)
        api.MapGet("/achats/reglements/banques", async (ITresorerie tresorerie, CancellationToken ct) =>
                Results.Ok((await tresorerie.ComptesAsync(ct)).Where(c => c.Type == "banque" && c.Actif).Select(c => new CompteBancaireDto(c.Id, c.Nom, c.RecoitCarte))))
           .ExigerPermission(Permissions.AchatsRegler);
    }

    private static CompteTresorerieDto VersDto(CompteTresorerie c) =>
        new(c.Id, c.Type, c.Nom, c.Banque, c.NumeroCompte, c.RecoitCarte, c.Actif, c.SoldeInitial, c.DateInitiale, c.Solde);

    private static CompteAEcrire VersCompte(long? id, CompteTresorerieRequete r) =>
        new(id, r.Nom, r.Banque, r.NumeroCompte, r.RecoitCarte, r.SoldeInitial, r.DateInitiale, r.Actif);
}
