using Pharma.Application.Offre;
using System.Security.Claims;
using Pharma.Api.Securite;
using Pharma.Application.Commun;
using Pharma.Application.Financements;
using Pharma.Domain.Commun;
using Pharma.Domain.Financements;
using Pharma.Shared;

namespace Pharma.Api.Endpoints;

/// <summary>Financements : prêts bancaires et leurs échéances, compte du titulaire, usage des sources (titulaire seul).</summary>
internal static class FinancementsEndpoints
{
    public static void MapFinancements(this RouteGroupBuilder api)
    {
        var f = api.MapGroup("/financements").ExigerTitulaire().ExigerPack(PacksOffre.Finances);

        // Prêts filtrés (état, texte, banque) et paginés par le serveur
        f.MapGet("/prets", async (string? etat, string? q, long? banque, int? page, int? taille, ConsulterFinancements c, ClaimsPrincipal user,
                CancellationToken ct) =>
            {
                var p = await c.PretsAsync(user.UtilisateurId(), FiltrePrets.Lire(etat, q, banque), DemandePage.Lire(page, taille), ct);
                return Results.Ok(new PageDto<PretDto>(p.Lignes.Select(VersDto).ToList(), p.Total, p.Numero, p.Taille));
            });

        f.MapGet("/prets/{id:long}", async (long id, ConsulterFinancements c, ClaimsPrincipal user, CancellationToken ct) =>
            {
                var d = await c.PretAsync(user.UtilisateurId(), id, ct);
                return Results.Ok(new PretDetailDto(VersDto(d.Pret), d.Echeances.Select(VersDto).ToList(),
                    d.Utilisations.Select(u => new UtilisationFinancementDto(u.Id, u.Source, u.PretId, u.Usage, u.Date, u.Montant, u.Libelle, u.Employe)).ToList(),
                    (d.Anticipes ?? []).Select(a => new RemboursementAnticipeDto(a.Id, a.Date, a.Capital, a.Frais, a.Moyen, a.Compte, a.Reference, a.Employe)).ToList()));
            });

        f.MapPost("/prets", async (PretRequete r, EnregistrerPret e, ClaimsPrincipal user, CancellationToken ct) =>
            Results.Ok(new IdentifiantDto(await e.CreerAsync(r.CompteId, r.Preteur, r.Reference, r.Objet, r.Montant, r.Date, user.UtilisateurId(), ct,
                r.Taux, r.DureeMois))));

        f.MapPut("/prets/{id:long}", async (long id, PretRequete r, EnregistrerPret e, ClaimsPrincipal user, CancellationToken ct) =>
            {
                await e.ModifierAsync(id, r.CompteId, r.Preteur, r.Reference, r.Objet, r.Montant, r.Date, user.UtilisateurId(), ct, r.Taux, r.DureeMois);
                return Results.NoContent();
            });

        f.MapPut("/prets/{id:long}/document", async (long id, DocumentPretRequete r, EnregistrerPret e, ClaimsPrincipal user, CancellationToken ct) =>
            {
                await e.JoindreDocumentAsync(id, r.Chemin, user.UtilisateurId(), ct);
                return Results.NoContent();
            });

        // Échéancier : contrôle seul (aperçu), puis enregistrement (refusé tant qu'une incohérence reste)
        f.MapPost("/prets/{id:long}/echeancier/controle", async (long id, EcheancierRequete r, EnregistrerEcheancier e, ClaimsPrincipal user, CancellationToken ct) =>
            Results.Ok((await e.ControlerAsync(id, r.Lignes.Select(VersLigne).ToList(), user.UtilisateurId(), ct)).Select(VersDto)));

        f.MapPut("/prets/{id:long}/echeancier", async (long id, EcheancierRequete r, EnregistrerEcheancier e, ClaimsPrincipal user, CancellationToken ct) =>
            {
                await e.ExecuterAsync(id, r.Lignes.Select(VersLigne).ToList(), user.UtilisateurId(), ct);
                return Results.NoContent();
            });

        // Import : fichier Excel ou CSV, ou texte lu sur le poste (PDF, scan) → tableau de cellules, avec la correspondance retenue pour la banque
        f.MapPost("/prets/{id:long}/tableau/fichier", async (long id, TableauFichierRequete r, EnregistrerEcheancier e, ConsulterFinancements c,
                ClaimsPrincipal user, CancellationToken ct) =>
            {
                byte[] contenu;
                try { contenu = Convert.FromBase64String(r.ContenuBase64); }
                catch (FormatException) { throw new RegleMetierException("Fichier illisible."); }
                var tableau = await e.LireFichierAsync(r.NomFichier, contenu, user.UtilisateurId(), ct);
                return Results.Ok(new TableauLuDto(tableau, await CorrespondanceAsync(c, id, user.UtilisateurId(), ct)));
            });

        f.MapPost("/prets/{id:long}/tableau/texte", async (long id, TableauTexteRequete r, EnregistrerEcheancier e, ConsulterFinancements c,
                ClaimsPrincipal user, CancellationToken ct) =>
            {
                var tableau = await e.LireTexteAsync(r.Lignes, user.UtilisateurId(), ct);
                return Results.Ok(new TableauLuDto(tableau, await CorrespondanceAsync(c, id, user.UtilisateurId(), ct)));
            });

        f.MapPost("/prets/{id:long}/tableau/apercu", async (long id, ApercuRequete r, EnregistrerEcheancier e, ClaimsPrincipal user, CancellationToken ct) =>
            {
                var a = await e.ApercuAsync(id, r.Lignes, VersCorrespondance(r.Correspondance), user.UtilisateurId(), ct);
                return Results.Ok(new ApercuEcheancierDto(a.Lignes.Select(VersDto).ToList(), a.Origines, a.Ecartees, a.Anomalies.Select(VersDto).ToList()));
            });

        f.MapPut("/prets/{id:long}/correspondance", async (long id, CorrespondanceDto r, EnregistrerEcheancier e, ClaimsPrincipal user, CancellationToken ct) =>
            {
                await e.RetenirCorrespondanceAsync(id, VersCorrespondance(r), user.UtilisateurId(), ct);
                return Results.NoContent();
            });

        // Échéances des prêts : état (a_payer par défaut, echues, a_venir, payees, toutes), référence ou banque
        f.MapGet("/echeances", async (string? etat, string? q, int? page, int? taille, ConsulterFinancements c, ClaimsPrincipal user, CancellationToken ct) =>
            {
                var p = await c.EcheancesAsync(user.UtilisateurId(), etat, q, DemandePage.Lire(page, taille), ct);
                return Results.Ok(new PageDto<EcheancePretDto>(p.Lignes.Select(VersDto).ToList(), p.Total, p.Numero, p.Taille));
            });

        f.MapPost("/echeances/{id:long}/paiement", async (long id, PaiementEcheanceRequete r, PayerEcheancePret payer, ClaimsPrincipal user,
                HttpRequest requete, CancellationToken ct) =>
            Results.Ok(new IdentifiantDto(await payer.ExecuterAsync(id, r.Date, r.Moyen, r.CompteId, r.Reference, r.PartTiroir, user.UtilisateurId(),
                requete.Poste(), ct, r.Montant))));

        // Remboursement anticipé d'une partie du capital (script 170)
        f.MapPost("/prets/{id:long}/remboursement-anticipe", async (long id, RemboursementAnticipeRequete r, RembourserPretParAnticipation rembourser,
                ClaimsPrincipal user, HttpRequest requete, CancellationToken ct) =>
            Results.Ok(new IdentifiantDto(await rembourser.ExecuterAsync(id, r.Date, r.Capital, r.Frais, r.Moyen, r.CompteId, r.Reference, r.PartTiroir,
                user.UtilisateurId(), requete.Poste(), ct))));

        // Sources : apport du titulaire et prêts dont une part reste à employer
        f.MapGet("/sources", async (ConsulterFinancements c, ClaimsPrincipal user, CancellationToken ct) =>
            Results.Ok((await c.SourcesAsync(user.UtilisateurId(), ct)).Select(s => new SourceFinancementDto(s.Code, s.Libelle, s.Disponible))));

        f.MapPost("/utilisations", async (UtilisationRequete r, UtiliserFinancement u, ClaimsPrincipal user, CancellationToken ct) =>
            Results.Ok(new IdentifiantDto(await u.ExecuterAsync(r.Source, r.Usage, r.Date, r.Montant, r.CompteId, r.Categorie, r.Libelle, r.Reference,
                user.UtilisateurId(), ct))));

        // Compte du titulaire : solde dû, totaux, mouvements filtrés (sens, période) et paginés
        f.MapGet("/titulaire", async (string? sens, DateOnly? du, DateOnly? au, int? page, int? taille, ConsulterFinancements c, ClaimsPrincipal user,
                CancellationToken ct) =>
            {
                var t = await c.CompteTitulaireAsync(user.UtilisateurId(), FiltreCompteTitulaire.Lire(sens, du, au), DemandePage.Lire(page, taille), ct);
                var p = t.Mouvements;
                return Results.Ok(new CompteTitulaireDto(t.SoldeDu, t.TotalApports, t.TotalRemboursements, new PageDto<MouvementTitulaireDto>(
                    p.Lignes.Select(m => new MouvementTitulaireDto(m.Date, m.Sens, m.Montant, m.Usage, m.Libelle, m.Employe)).ToList(), p.Total, p.Numero, p.Taille)));
            });

        f.MapPost("/titulaire/remboursements", async (RemboursementTitulaireRequete r, RembourserTitulaire rembourser, ClaimsPrincipal user, CancellationToken ct) =>
            Results.Ok(new IdentifiantDto(await rembourser.ExecuterAsync(r.Date, r.Montant, r.CompteId, r.Libelle, r.Reference, user.UtilisateurId(), ct))));
    }

    private static async Task<CorrespondanceDto?> CorrespondanceAsync(ConsulterFinancements c, long pretId, long utilisateurId, CancellationToken ct)
    {
        var pret = await c.PretAsync(utilisateurId, pretId, ct);
        return await c.CorrespondanceAsync(utilisateurId, pret.Pret, ct) is { } x
            ? new CorrespondanceDto(x.Date, x.Capital, x.Interets, x.Tva, x.Marge, x.Montant, x.Encours, x.LignesIgnorees, x.Assurance)
            : null;
    }

    private static PretDto VersDto(PretResume p) => new(p.Id, p.CompteId, p.Banque, p.Reference, p.Objet, p.Montant, p.DatePret, p.Document,
        p.CapitalRembourse, p.ResteDu, p.Utilise, p.Echeances, p.EcheancesPayees, p.EchuesNonPayees, p.ProchaineEcheance, p.ProchainMontant, p.Employe,
        p.Taux, p.DureeMois, p.ProchaineAssurance, p.EcheancierARemplacer, p.CapitalAnticipe);

    private static EcheancePretDto VersDto(EcheancePret e) => new(e.Id, e.PretId, e.Pret, e.Banque, e.Rang, VersDto(e.Ligne), e.PayeeLe, e.Moyen, e.Compte,
        e.ReferencePaiement, e.Employe, e.MontantPaye, e.CapitalPaye, e.ChargesPayees);

    private static LigneEcheancierDto VersDto(LigneEcheancier l) =>
        new(l.Date, l.Capital, l.Interets, l.Tva, l.Marge, l.Montant, l.CapitalRestant, l.Assurance);

    private static AnomalieEcheancierDto VersDto(AnomalieEcheancier a) => new(a.Ligne, a.Champ, a.Message);

    private static LigneEcheancier VersLigne(LigneEcheancierDto l) =>
        new(l.Date, l.Capital, l.Interets, l.Tva, l.Marge, l.Montant, l.CapitalRestant, l.Assurance);

    private static CorrespondanceColonnes VersCorrespondance(CorrespondanceDto c) =>
        new(c.Date, c.Capital, c.Interets, c.Tva, c.Marge, c.Montant, c.Encours, c.LignesIgnorees, c.Assurance);
}
