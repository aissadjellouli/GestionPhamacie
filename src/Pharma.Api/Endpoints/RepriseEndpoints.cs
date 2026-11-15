using System.Security.Claims;
using Pharma.Api.Securite;
using Pharma.Application.Reprise;
using Pharma.Application.SuperAdmin;
using Pharma.Domain.Commun;
using Pharma.Domain.Reprise;
using Pharma.Shared;

namespace Pharma.Api.Endpoints;

/// <summary>
/// Assistant de reprise de données (super admin seulement) : l'ancienne base est lue par ODBC, en lecture seule. Extraction,
/// chargement et annulation demandent en plus le code reçu par SMS (ou e-mail). Données chargées attribuées au titulaire.
/// </summary>
internal static class RepriseEndpoints
{
    public static void MapReprise(this RouteGroupBuilder api)
    {
        var reprise = api.MapGroup("/admin/reprise").ExigerSuperAdmin();

        reprise.MapPost("/sources", async (DossierRepriseRequete req, ListerSourcesReprise lister, ClaimsPrincipal user, CancellationToken ct) =>
            Results.Ok((await lister.ExecuterAsync(req.Dossier, 0, ct))
                .Select(s => new SourceRepriseDto(s.Libelle, s.ChaineConnexion, s.Origine, s.Remarque, s.Pilote, s.Emplacement, s.Dsn))));

        reprise.MapGet("/pilotes", async (ListerSourcesReprise lister, ClaimsPrincipal user, CancellationToken ct) =>
            Results.Ok(await lister.PilotesAsync(0, ct)));

        reprise.MapPost("/scan", async (ConnexionRepriseRequete req, ScannerSourceReprise scanner, ClaimsPrincipal user, CancellationToken ct) =>
            {
                var r = await scanner.ExecuterAsync(req.ChaineConnexion, 0, ct);
                return Results.Ok(new ScanRepriseDto(r.Info.Pilote, r.Info.Version,
                    r.Tables.Select(t => new TableSourceDto(t.Nom, t.Lignes, t.Colonnes.Select(c => new ColonneSourceDto(c.Nom, c.Type, c.Exemples)).ToList())).ToList(),
                    r.Propositions.Select(p => new PropositionRepriseDto(Code(p.Cible), p.Table, p.Score, p.Colonnes)).ToList(),
                    DetectionReprise.Champs.SelectMany(kv => kv.Value.Select(c => new ChampRepriseDto(Code(kv.Key), c.Code, c.Libelle, c.Obligatoire))).ToList()));
            });

        reprise.MapPost("/extraire", async (ExtractionRepriseRequete req, ExtraireReprise extraire, ITitulaireLecture titulaire, CancellationToken ct) =>
            {
                var r = await extraire.ExecuterAsync(req.ChaineConnexion, req.Logiciel,
                    req.Correspondances.Requise().Select(c => new CorrespondanceTable(Cible(c.Cible), c.Table, c.Colonnes)).ToList(), await Titulaire(titulaire, ct), ct);
                return Results.Ok(new ExtractionRepriseDto(r.SessionId, r.Lignes.ToDictionary(kv => Code(kv.Key), kv => kv.Value), r.Doublons, r.Bloquants));
            })
            .ValidationSuperAdmin(ActionsSuperAdmin.RepriseExtraire);

        reprise.MapGet("/sessions", async (ObtenirEtatReprise obtenir, ClaimsPrincipal user, CancellationToken ct) =>
            Results.Ok((await obtenir.SessionsAsync(0, ct)).Select(VersDto)));

        reprise.MapGet("/sessions/{id:long}", async (long id, ObtenirEtatReprise obtenir, ClaimsPrincipal user, CancellationToken ct) =>
            await obtenir.ExecuterAsync(id, 0, ct) is { } e ? Results.Ok(VersDto(e)) : Results.NotFound(new ErreurDto("Session de reprise introuvable.")));

        reprise.MapPost("/sessions/{id:long}/produits", async (long id, DecisionProduitRepriseRequete req, DeciderProduitReprise decider, ITitulaireLecture titulaire, CancellationToken ct) =>
            {
                var decision = req.Decision switch
                {
                    "confirmer" => DecisionProduit.Confirmer, "autre" => DecisionProduit.Autre, "creer" => DecisionProduit.Creer,
                    _ => throw new RegleMetierException("Décision inconnue.")
                };
                return Results.Ok(new NombreDto(await decider.ExecuterAsync(id, req.RefSource, decision, req.ProduitId, await Titulaire(titulaire, ct), ct)));
            });

        reprise.MapPost("/sessions/{id:long}/charger", async (long id, ChargerReprise charger, ITitulaireLecture titulaire, CancellationToken ct) =>
            Results.Ok(VersDto(await charger.ExecuterAsync(id, await Titulaire(titulaire, ct), ct))))
            .ValidationSuperAdmin(ActionsSuperAdmin.RepriseCharger);

        reprise.MapPost("/sessions/{id:long}/annuler", async (long id, AnnulerReprise annuler, ITitulaireLecture titulaire, CancellationToken ct) =>
            {
                await annuler.ExecuterAsync(id, await Titulaire(titulaire, ct), ct);
                return Results.NoContent();
            })
            .ValidationSuperAdmin(ActionsSuperAdmin.RepriseAnnuler);

        // Doublons de n° d'assuré de la reprise (092) : droit « Valider les doublons d'import » (093, revérifié par le cas d'usage)
        var doublons = api.MapGroup("/reprise/doublons-patients").ExigerPermission(Pharma.Domain.Securite.Permissions.RepriseDoublons);

        doublons.MapGet("", async (string? statut, string? q, int? page, int? taille, ListerDoublonsImport lister, ClaimsPrincipal user, CancellationToken ct) =>
        {
            var p = await lister.ExecuterAsync(new FiltreDoublonsImport(statut, q), Pharma.Application.Commun.DemandePage.Lire(page, taille), user.UtilisateurId(), ct);
            return Results.Ok(new PageDto<DoublonImportDto>(p.Lignes.Select(VersDto).ToList(), p.Total, p.Numero, p.Taille));
        });

        doublons.MapPost("/{id:long}/decision", async (long id, DecisionDoublonImportRequete req, DeciderDoublonImport decider, ClaimsPrincipal user, CancellationToken ct) =>
        {
            var decision = req.Decision switch
            {
                "garder_existant" => DecisionDoublon.GarderExistant, "remplacer" => DecisionDoublon.Remplacer, "ayant_droit" => DecisionDoublon.AyantDroit,
                _ => throw new RegleMetierException("Décision inconnue.", "decision")
            };
            DetteDoublon? dette = req.Dette switch
            {
                null or "" => null, "rattacher" => DetteDoublon.Rattacher, "abandonner" => DetteDoublon.Abandonner,
                _ => throw new RegleMetierException("Choix de la dette inconnu.", "dette")
            };
            return Results.Ok(new IdentifiantDto(await decider.ExecuterAsync(id, decision, dette, req.NumAssure, user.UtilisateurId(), ct)));
        });
    }

    private static DoublonImportDto VersDto(DoublonPatientImport d) => new(d.Id, d.SessionId, d.Logiciel, d.RefSource, d.Ligne, d.Nom, d.Prenom, d.Telephone,
        d.NumAssure, d.SoldeCredit, d.Origine, d.PatientExistantId, d.ExistantNom, d.ExistantNumAssure, d.ExistantTelephone, d.ExistantSolde,
        d.Statut, d.Dette, d.PatientCreeId, d.DecidePar, d.DecideLe);

    /// <summary>Données chargées par le super admin : attribuées au pharmacien titulaire (registre des psychotropes compris).</summary>
    private static async Task<long> Titulaire(ITitulaireLecture titulaire, CancellationToken ct) =>
        await titulaire.IdAsync(ct)
        ?? throw new RegleMetierException("Aucun pharmacien titulaire actif dans la base : la reprise lui attribue les données chargées. Créez ou réactivez son compte d’abord.");

    private static string Code(CibleReprise c) => c switch
    {
        CibleReprise.Produits => "produits", CibleReprise.Stock => "stock", CibleReprise.Fournisseurs => "fournisseurs", _ => "clients"
    };

    private static CibleReprise Cible(string code) => code switch
    {
        "produits" => CibleReprise.Produits, "stock" => CibleReprise.Stock, "fournisseurs" => CibleReprise.Fournisseurs, "clients" => CibleReprise.Clients,
        _ => throw new RegleMetierException($"Type de données inconnu : {code}.")
    };

    private static SessionRepriseDto VersDto(SessionReprise s) => new(s.Id, s.Logiciel, s.Debut, s.Statut, s.LancePar, s.ChargeLe, s.ChargePar);

    private static EtatRepriseDto VersDto(EtatReprise e) => new(VersDto(e.Session), e.Lignes, e.Rapprochement,
        e.AConfirmer.Select(p => new ProduitAConfirmerDto(p.RefSource, p.Nom, p.Dosage, p.CodeBarre, p.ProduitPropose, p.NomPropose, p.Score)).ToList(),
        e.Anomalies.Select(a => new AnomalieRepriseDto(a.Entite, a.RefSource, a.Gravite, a.Message)).ToList(),
        e.Verification.Select(v => new VerificationRepriseDto(v.Indicateur, v.Ancien, v.Nouveau, v.Ecart)).ToList());
}
