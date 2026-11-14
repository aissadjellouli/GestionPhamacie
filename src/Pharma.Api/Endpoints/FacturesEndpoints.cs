using System.Security.Claims;
using Pharma.Api.Securite;
using Pharma.Application.Administration;
using Pharma.Application.Factures;
using Pharma.Domain.Factures;
using Pharma.Domain.Securite;
using Pharma.Shared;

namespace Pharma.Api.Endpoints;

/// <summary>Factures clients (droit factures.gerer) ; organismes (titulaire).</summary>
internal static class FacturesEndpoints
{
    public static void MapFactures(this RouteGroupBuilder api)
    {
        var f = api.MapGroup("/factures").ExigerPermission(Permissions.FacturesGerer);

        f.MapGet("/candidates", async (string destinataire, long? patientId, long? organismeId, DateOnly du, DateOnly au, string? q,
                ConsulterFactures c, ClaimsPrincipal user, CancellationToken ct) =>
            Results.Ok((await c.CandidatesAsync(destinataire, patientId, organismeId, du, au, q, user.UtilisateurId(), ct)).Select(s => new LigneVenteSourceDto(
                s.Ligne.VenteId, s.Ligne.NumeroVente, s.DateVente, s.Ligne.LigneVenteId, s.Ligne.Designation, s.Ligne.Quantite, s.Ligne.MontantPatient,
                s.Ligne.MontantOrganisme, s.Ligne.RemiseVente + s.Ligne.DonVente, s.PatientId, s.Patient, s.OrganismeId, s.OrdonnanceId, s.OrdonnanceNumero, s.DejaFacturee))));

        f.MapGet("", async (string? q, string? destinataire, string? suivi, DateOnly? du, DateOnly? au, ConsulterFactures c, ClaimsPrincipal user,
                CancellationToken ct) =>
            Results.Ok((await c.ListerAsync(new FiltreFactures(q, destinataire, suivi, du, au), user.UtilisateurId(), ct)).Select(VersDto)));

        // Liste paginée, avec les totaux de toutes les factures qui répondent aux filtres
        f.MapGet("/page", async (string? q, string? destinataire, string? suivi, DateOnly? du, DateOnly? au, int? page, int? taille, ConsulterFactures c,
                ClaimsPrincipal user, CancellationToken ct) =>
            {
                var l = await c.PageAsync(new FiltreFactures(q, destinataire, suivi, du, au), Pharma.Application.Commun.DemandePage.Lire(page, taille), user.UtilisateurId(), ct);
                return Results.Ok(new FacturesPageDto(new PageDto<FactureResumeDto>(l.Page.Lignes.Select(VersDto).ToList(), l.Page.Total, l.Page.Numero, l.Page.Taille),
                    l.TotalTtc, l.TotalReste));
            });

        f.MapGet("/{id:long}", async (long id, int? version, ConsulterFactures c, ClaimsPrincipal user, CancellationToken ct) =>
            Results.Ok(VersDto(await c.DetailAsync(id, version, user.UtilisateurId(), ct))));

        f.MapGet("/{id:long}/historique", async (long id, ConsulterFactures c, ClaimsPrincipal user, CancellationToken ct) =>
            Results.Ok((await c.HistoriqueAsync(id, user.UtilisateurId(), ct))
                .Select(e => new EvenementFactureDto(e.Date, e.Libelle, e.Detail, e.Employe, e.EmployeId, e.Montant)).ToList()));

        f.MapGet("/{id:long}/pdf", async (long id, int? version, ConsulterFactures c, IDocumentsFactures pdf, IPharmacie pharmacie, ClaimsPrincipal user,
                CancellationToken ct) =>
            {
                var d = await c.DetailAsync(id, version, user.UtilisateurId(), ct);
                var nom = $"{d.Resume.Numero}{(version is { } v && v != d.Resume.Version ? $"_v{v}" : d.Resume.Version > 1 ? $"_v{d.Resume.Version}" : "")}.pdf";
                return Results.File(pdf.Facture(d, await pharmacie.LireAsync(ct)), "application/pdf", nom);
            });

        f.MapPost("", async (DemandeFactureDto r, Facturation fa, ClaimsPrincipal user, CancellationToken ct) =>
            {
                var (id, numero) = await fa.EmettreAsync(VersDemande(r), user.UtilisateurId(), ct);
                return Results.Ok(new FactureEmiseDto(id, numero));
            });

        f.MapPost("/{id:long}/rejet", async (long id, MotifRequete r, Facturation fa, ClaimsPrincipal user, CancellationToken ct) =>
            {
                await fa.RejeterAsync(id, r.Motif, user.UtilisateurId(), ct);
                return Results.NoContent();
            });

        f.MapPost("/{id:long}/lever-rejet", async (long id, MotifRequete r, Facturation fa, ClaimsPrincipal user, CancellationToken ct) =>
            {
                await fa.LeverRejetAsync(id, r.Motif, user.UtilisateurId(), ct);
                return Results.NoContent();
            });

        f.MapPost("/{id:long}/correction", async (long id, DemandeFactureDto r, Facturation fa, ClaimsPrincipal user, CancellationToken ct) =>
            {
                await fa.CorrigerAsync(id, VersDemande(r), r.Motif, user.UtilisateurId(), ct);
                return Results.NoContent();
            });

        f.MapPost("/{id:long}/remplacement", async (long id, DemandeFactureDto r, Facturation fa, ClaimsPrincipal user, CancellationToken ct) =>
            {
                var (nouvelle, numero) = await fa.RemplacerAsync(id, VersDemande(r), user.UtilisateurId(), ct);
                return Results.Ok(new FactureEmiseDto(nouvelle, numero));
            });

        f.MapPost("/{id:long}/avoir", async (long id, AvoirClientRequete r, Facturation fa, ClaimsPrincipal user, CancellationToken ct) =>
            {
                var (avoir, numero) = await fa.AvoirAsync(id, r.Montant, r.Motif, user.UtilisateurId(), ct);
                return Results.Ok(new FactureEmiseDto(avoir, numero));
            });

        f.MapPost("/{id:long}/suivi", async (long id, SuiviRequete r, Facturation fa, ClaimsPrincipal user, CancellationToken ct) =>
            {
                await fa.SuiviAsync(id, r.HorsSuivi, r.Motif, user.UtilisateurId(), ct);
                return Results.NoContent();
            });

        f.MapGet("/mentions-manquantes", async (MentionsFacture mentions, CancellationToken ct) => Results.Ok(await mentions.ManquantesAsync(ct)));

        f.MapGet("/clients", async (ConsulterFactures c, ClaimsPrincipal user, CancellationToken ct) =>
            Results.Ok((await c.ClientsAsync(user.UtilisateurId(), ct)).Select(x =>
                new ClientFactureDto(x.Id, x.RaisonSociale, x.Adresse, x.Telephone, x.Nif, x.Rc, x.Nis, x.ArticleImposition, x.Actif, x.Email, x.DelaiPaiementJours))));

        f.MapPost("/clients", async (ClientFactureDto x, Facturation fa, ClaimsPrincipal user, CancellationToken ct) =>
            Results.Ok(new IdentifiantDto(await fa.EnregistrerClientAsync(
                new ClientFacture(x.Id, x.RaisonSociale, x.Adresse, x.Telephone, x.Nif, x.Rc, x.Nis, x.ArticleImposition, x.Actif, x.Email, x.DelaiPaiementJours), user.UtilisateurId(), ct))));

        // Organismes (tiers payant et factures) : lecture pour qui facture, modification par le titulaire
        api.MapGet("/admin/organismes", async (IFactures factures, CancellationToken ct) =>
                Results.Ok((await factures.OrganismesAsync(ct)).Select(o => new OrganismeFicheDto(o.Id, o.Code, o.Nom, o.Adresse, o.Nif, o.Actif, o.Telephone, o.Email, o.DelaiPaiementJours))))
           .ExigerPermission(Permissions.AdminParametres);

        api.MapPost("/admin/organismes", async (OrganismeFicheDto o, Facturation fa, ClaimsPrincipal user, CancellationToken ct) =>
                Results.Ok(new IdentifiantDto(await fa.EnregistrerOrganismeAsync(new OrganismeFiche(o.Id, o.Code, o.Nom, o.Adresse, o.Nif, o.Actif, o.Telephone, o.Email, o.DelaiPaiementJours),
                    user.UtilisateurId(), ct))))
           .ExigerPermission(Permissions.AdminParametres);
    }

    private static DemandeFacture VersDemande(DemandeFactureDto r) => new(r.Destinataire, r.PatientId, r.ClientId, r.OrganismeId, r.Date, r.Du, r.Au,
        r.Ventes ?? [], (r.Libres ?? []).Select(l => new LigneLibre(l.Designation, l.Quantite, l.PrixTtc, l.TauxTva)).ToList(), r.Commentaire, r.Echeance);

    private static FactureResumeDto VersDto(FactureResume r) => new(r.Id, r.Type, r.Numero, r.Date, r.Destinataire, r.DestinataireNom, r.Ttc, r.DejaPaye,
        r.Avoirs, r.Regle, r.Reste, r.Statut, r.Version, r.HorsSuivi, r.MotifHorsSuivi, r.RemplaceNumero, r.RemplaceeParNumero, r.OrigineNumero,
        r.MotifRejet, r.OrganismeId, r.Echeance, r.PatientId, r.ClientId);

    private static FactureDetailDto VersDto(FactureDetail d) => new(VersDto(d.Resume), d.PatientId, d.ClientId, d.DestinataireInfos, d.Du, d.Au, d.Ht, d.Tva,
        d.Commentaire, d.Motif, d.Employe,
        d.Lignes.Select(l => new LigneFactureDto(l.VenteId, l.LigneVenteId, l.Part, l.Designation, l.Quantite, l.PrixUnitaireTtc, l.TauxTva, l.MontantTtc)).ToList(),
        d.Versions, d.EmployeId, d.RegleParDette);
}
