using System.Security.Claims;
using Pharma.Api.Securite;
using Pharma.Application.Reprise;
using Pharma.Domain.Commun;
using Pharma.Domain.Reprise;
using Pharma.Shared;

namespace Pharma.Api.Endpoints;

/// <summary>Assistant de reprise de données (titulaire seulement) : l'ancienne base est lue par ODBC, en lecture seule.</summary>
internal static class RepriseEndpoints
{
    public static void MapReprise(this RouteGroupBuilder api)
    {
        var reprise = api.MapGroup("/admin/reprise").ExigerTitulaire();

        reprise.MapPost("/sources", async (DossierRepriseRequete req, ListerSourcesReprise lister, ClaimsPrincipal user, CancellationToken ct) =>
            Results.Ok((await lister.ExecuterAsync(req.Dossier, user.UtilisateurId(), ct))
                .Select(s => new SourceRepriseDto(s.Libelle, s.ChaineConnexion, s.Origine, s.Remarque, s.Pilote, s.Emplacement, s.Dsn))));

        reprise.MapGet("/pilotes", async (ListerSourcesReprise lister, ClaimsPrincipal user, CancellationToken ct) =>
            Results.Ok(await lister.PilotesAsync(user.UtilisateurId(), ct)));

        reprise.MapPost("/scan", async (ConnexionRepriseRequete req, ScannerSourceReprise scanner, ClaimsPrincipal user, CancellationToken ct) =>
            {
                var r = await scanner.ExecuterAsync(req.ChaineConnexion, user.UtilisateurId(), ct);
                return Results.Ok(new ScanRepriseDto(r.Info.Pilote, r.Info.Version,
                    r.Tables.Select(t => new TableSourceDto(t.Nom, t.Lignes, t.Colonnes.Select(c => new ColonneSourceDto(c.Nom, c.Type, c.Exemples)).ToList())).ToList(),
                    r.Propositions.Select(p => new PropositionRepriseDto(Code(p.Cible), p.Table, p.Score, p.Colonnes)).ToList(),
                    DetectionReprise.Champs.SelectMany(kv => kv.Value.Select(c => new ChampRepriseDto(Code(kv.Key), c.Code, c.Libelle, c.Obligatoire))).ToList()));
            });

        reprise.MapPost("/extraire", async (ExtractionRepriseRequete req, ExtraireReprise extraire, ClaimsPrincipal user, CancellationToken ct) =>
            {
                var r = await extraire.ExecuterAsync(req.ChaineConnexion, req.Logiciel,
                    req.Correspondances.Select(c => new CorrespondanceTable(Cible(c.Cible), c.Table, c.Colonnes)).ToList(), user.UtilisateurId(), ct);
                return Results.Ok(new ExtractionRepriseDto(r.SessionId, r.Lignes.ToDictionary(kv => Code(kv.Key), kv => kv.Value), r.Doublons, r.Bloquants));
            });

        reprise.MapGet("/sessions", async (ObtenirEtatReprise obtenir, ClaimsPrincipal user, CancellationToken ct) =>
            Results.Ok((await obtenir.SessionsAsync(user.UtilisateurId(), ct)).Select(VersDto)));

        reprise.MapGet("/sessions/{id:long}", async (long id, ObtenirEtatReprise obtenir, ClaimsPrincipal user, CancellationToken ct) =>
            await obtenir.ExecuterAsync(id, user.UtilisateurId(), ct) is { } e ? Results.Ok(VersDto(e)) : Results.NotFound(new ErreurDto("Session de reprise introuvable.")));

        reprise.MapPost("/sessions/{id:long}/produits", async (long id, DecisionProduitRepriseRequete req, DeciderProduitReprise decider, ClaimsPrincipal user, CancellationToken ct) =>
            {
                var decision = req.Decision switch
                {
                    "confirmer" => DecisionProduit.Confirmer, "autre" => DecisionProduit.Autre, "creer" => DecisionProduit.Creer,
                    _ => throw new RegleMetierException("Décision inconnue.")
                };
                return Results.Ok(new NombreDto(await decider.ExecuterAsync(id, req.RefSource, decision, req.ProduitId, user.UtilisateurId(), ct)));
            });

        reprise.MapPost("/sessions/{id:long}/charger", async (long id, ChargerReprise charger, ClaimsPrincipal user, CancellationToken ct) =>
            Results.Ok(VersDto(await charger.ExecuterAsync(id, user.UtilisateurId(), ct))));

        reprise.MapPost("/sessions/{id:long}/annuler", async (long id, AnnulerReprise annuler, ClaimsPrincipal user, CancellationToken ct) =>
            {
                await annuler.ExecuterAsync(id, user.UtilisateurId(), ct);
                return Results.NoContent();
            });
    }

    private static string Code(CibleReprise c) => c switch
    {
        CibleReprise.Produits => "produits", CibleReprise.Stock => "stock", CibleReprise.Fournisseurs => "fournisseurs", _ => "clients"
    };

    private static CibleReprise Cible(string code) => code switch
    {
        "produits" => CibleReprise.Produits, "stock" => CibleReprise.Stock, "fournisseurs" => CibleReprise.Fournisseurs, "clients" => CibleReprise.Clients,
        _ => throw new RegleMetierException($"Type de données inconnu : {code}.")
    };

    private static SessionRepriseDto VersDto(SessionReprise s) => new(s.Id, s.Logiciel, s.Debut, s.Statut);

    private static EtatRepriseDto VersDto(EtatReprise e) => new(VersDto(e.Session), e.Lignes, e.Rapprochement,
        e.AConfirmer.Select(p => new ProduitAConfirmerDto(p.RefSource, p.Nom, p.Dosage, p.CodeBarre, p.ProduitPropose, p.NomPropose, p.Score)).ToList(),
        e.Anomalies.Select(a => new AnomalieRepriseDto(a.Entite, a.RefSource, a.Gravite, a.Message)).ToList(),
        e.Verification.Select(v => new VerificationRepriseDto(v.Indicateur, v.Ancien, v.Nouveau, v.Ecart)).ToList());
}
