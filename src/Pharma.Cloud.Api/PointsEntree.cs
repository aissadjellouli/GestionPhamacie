using System.Security.Cryptography.X509Certificates;
using Pharma.Cloud.Api.Reception;
using Pharma.Cloud.Api.Securite;
using Pharma.Shared;

namespace Pharma.Cloud.Api;

/// <summary>Routes du serveur central (version 1). Erreurs en ProblemDetails ; journal sans donnée personnelle (officine, n° de lot).</summary>
public static class PointsEntree
{
    public static void MapServeurCentral(this WebApplication app)
    {
        var v1 = app.MapGroup("/api/v1");

        // Activation : code à usage unique + CSR -> certificat client (3 ans) ; aucun certificat client exigé
        v1.MapPost("/activation", async (DemandeActivationDto d, AutoriteCertification ac, DepotCentral depot, TimeProvider temps,
            ILoggerFactory lf, CancellationToken ct) =>
        {
            var log = lf.CreateLogger("Pharma.Cloud.Activation");
            if (CodeActivationSync.Lire(d.Code) is not { } code || d.OfficineId == Guid.Empty)
                return Results.Problem("Code d'activation mal formé.", statusCode: 400);
            if (!ac.EmpreinteRacine.StartsWith(code.Epinglage, StringComparison.Ordinal))
                return Results.Problem("Code d'activation inconnu, expiré ou déjà utilisé.", statusCode: 403);
            var empreinteCode = CodeActivationSync.EmpreinteSecret(code.Secret);
            var valable = await depot.CodeValableAsync(empreinteCode, ct);
            if (valable is null)
            {
                await depot.JournaliserAsync(null, null, "activation_refusee", "code inconnu, expiré ou déjà utilisé", ct);
                log.LogWarning("Activation refusée : code inconnu, expiré ou déjà utilisé");
                return Results.Problem("Code d'activation inconnu, expiré ou déjà utilisé.", statusCode: 403);
            }
            if (valable.DejaActivee && valable.Officine != d.OfficineId)
            {
                await depot.JournaliserAsync(valable.Officine, null, "activation_refusee", "code officine différent", ct);
                return Results.Problem("Ce code appartient à une autre officine.", statusCode: 403);
            }
            X509Certificate2 cert;
            try { cert = ac.SignerDemande(d.DemandePem, d.OfficineId, valable.Serveur, temps.GetUtcNow()); }
            catch (DemandeInvalideException e)
            {
                log.LogWarning("Activation refusée : {Motif}", e.Message);
                return Results.Problem("Demande de certificat refusée.", statusCode: 400);
            }
            using (cert)
            {
                var empreinte = AutoriteCertification.Empreinte(cert);
                if (!await depot.ActiverAsync(empreinteCode, d.OfficineId, empreinte, cert.SerialNumber, cert.NotAfter.ToUniversalTime(), ct))
                    return Results.Problem("Activation impossible (code déjà utilisé, ou code officine déjà inscrit ailleurs).", statusCode: 409);
                log.LogInformation("Officine {Officine} activée (serveur {Serveur})", d.OfficineId, valable.Serveur);
                return Results.Ok(new ReponseActivationDto(d.OfficineId, valable.Serveur, cert.ExportCertificatePem(), ac.RacinePem,
                    new DateTimeOffset(cert.NotAfter.ToUniversalTime())));
            }
        }).RequireRateLimiting("activation");

        // Jeton court lié au certificat présenté
        v1.MapPost("/jeton", async (HttpContext ctx, JetonsCentral jetons, CancellationToken ct) =>
        {
            var (etat, refus) = await CertificatAsync(ctx, ct);
            if (etat is null) return refus!;
            var (jeton, expire) = jetons.Emettre(etat.Value.Etat.Officine, etat.Value.Etat.Serveur, etat.Value.Empreinte, ctx.RequestServices.GetRequiredService<TimeProvider>().GetUtcNow());
            return Results.Ok(new ReponseJetonDto(jeton, expire));
        }).RequireRateLimiting("officine");

        // Dépôt d'un lot sync-v1 (corps = lot sérialisé) ; accusé
        v1.MapPost("/lots", async (HttpContext ctx, DepotCentral depot, ILoggerFactory lf, CancellationToken ct) =>
        {
            var (appelant, refus) = await AppelantAsync(ctx, ct);
            if (appelant is null) return refus!;
            var log = lf.CreateLogger("Pharma.Cloud.Lots");
            using var flux = new MemoryStream();
            await ctx.Request.Body.CopyToAsync(flux, ct);
            var contenu = flux.ToArray();
            var c = ControleLot.Controler(contenu, appelant.Officine);
            if (!c.Valide)
            {
                await depot.JournaliserAsync(appelant.Officine, appelant.EmpreinteCertificat, "lot_refuse", c.Refus, ct);
                log.LogWarning("Lot refusé pour l'officine {Officine} : {Motif}", appelant.Officine, c.Refus);
                return Results.Problem(ControleLot.MessageClient(c.CodeHttp), statusCode: c.CodeHttp);
            }
            var e = c.Lot!.EnTete;
            try
            {
                var (statut, dernier) = await depot.DeposerAsync(appelant.Officine, e.Epoque, e.NumeroLot, e.Empreinte, e.VersionContrat, contenu, ct);
                if (statut is StatutsLot.EmpreinteDifferente)
                {
                    await depot.JournaliserAsync(appelant.Officine, appelant.EmpreinteCertificat, "lot_anomalie", $"lot {e.Epoque}/{e.NumeroLot} : empreinte différente", ct);
                    log.LogWarning("Officine {Officine} : lot {Epoque}/{Numero} déjà reçu avec une autre empreinte", appelant.Officine, e.Epoque, e.NumeroLot);
                    return Results.Problem("Lot déjà reçu avec un autre contenu : anomalie signalée.", statusCode: 409);
                }
                if (statut is "quota_depasse")
                {
                    await depot.JournaliserAsync(appelant.Officine, appelant.EmpreinteCertificat, "quota_depasse", $"lot {e.Epoque}/{e.NumeroLot}", ct);
                    log.LogWarning("Officine {Officine} : quota journalier atteint (lot {Epoque}/{Numero})", appelant.Officine, e.Epoque, e.NumeroLot);
                    return Results.Problem("Quota journalier de l'officine atteint : nouvel essai plus tard.", statusCode: StatusCodes.Status429TooManyRequests);
                }
                // Voie descendante : l'accusé porte les éléments plus récents que la version donnée par l'officine (en-tête facultatif)
                ChangementsDescendantsDto? desc = null;
                if (long.TryParse(ctx.Request.Headers[ChangementsDescendantsDto.EnTeteVersion].ToString(), System.Globalization.NumberStyles.None,
                        System.Globalization.CultureInfo.InvariantCulture, out var vd))
                    desc = await depot.DescendantsAsync(appelant.Officine, vd, ct);
                if (statut is StatutsLot.Trou)
                    return Results.Json(new AccuseLotDto(e.Epoque, e.NumeroLot, statut, dernier, desc), statusCode: StatusCodes.Status409Conflict);
                log.LogInformation("Officine {Officine} : lot {Epoque}/{Numero} {Statut} ({Lignes} lignes)", appelant.Officine, e.Epoque, e.NumeroLot, statut, e.NombreLignes);
                return Results.Ok(new AccuseLotDto(e.Epoque, e.NumeroLot, statut, dernier, desc));
            }
            catch (Npgsql.PostgresException p) when (p.SqlState == "P0001" || p.SqlState.StartsWith("22", StringComparison.Ordinal) || p.SqlState.StartsWith("23", StringComparison.Ordinal))
            {
                // Détail technique sans valeur de donnée : code SQL, table, colonne, contrainte (jamais le texte d'erreur, qui peut citer une valeur)
                var detail = $"lot {e.Epoque}/{e.NumeroLot} : SQL {p.SqlState} {p.TableName} {p.ColumnName} {p.ConstraintName}".TrimEnd();
                await depot.JournaliserAsync(appelant.Officine, appelant.EmpreinteCertificat, "lot_refuse", detail, ct);
                log.LogWarning("Officine {Officine} : {Detail}", appelant.Officine, detail);
                return Results.Problem("Lot refusé : contenu incompatible avec la base centrale (détail au journal du serveur central).", statusCode: 422);
            }
        }).RequireRateLimiting("lots");

        // Dernier lot reçu d'une époque (reprise) et plus grande époque reçue de l'officine (champ facultatif, 2026-11-18)
        v1.MapGet("/etat", async (int epoque, HttpContext ctx, DepotCentral depot, CancellationToken ct) =>
        {
            var (appelant, refus) = await AppelantAsync(ctx, ct);
            if (appelant is null) return refus!;
            var (dernier, epoqueMax) = await depot.EtatAsync(appelant.Officine, epoque, ct);
            return Results.Ok(new EtatCentralDto(epoque, dernier, epoqueMax));
        }).RequireRateLimiting("officine");

        // Lot S6.1 (script 020) : plus grand id reçu par table, réservé à l'officine appelante (avance des séquences après une restauration)
        v1.MapGet("/maximums", async (HttpContext ctx, DepotCentral depot, CancellationToken ct) =>
        {
            var (appelant, refus) = await AppelantAsync(ctx, ct);
            if (appelant is null) return refus!;
            return Results.Ok(new MaximumsCentralDto(await depot.MaximumsAsync(appelant.Officine, ct)));
        }).RequireRateLimiting("officine");

        // Lot S6.5.3 (script 022) : déclaration d'un changement d'époque (« Repartir sur une nouvelle époque »), avant tout lot de la
        // nouvelle époque. Renvoi identique : 200 avec la déclaration existante ; autre contenu pour la même époque : 409 ; refus : 400.
        v1.MapPost("/epoque", async (DeclarationEpoqueDto d, HttpContext ctx, DepotCentral depot, ILoggerFactory journaux, CancellationToken ct) =>
        {
            var (appelant, refus) = await AppelantAsync(ctx, ct);
            if (appelant is null) return refus!;
            if (d.Refus() is { } motif) return Results.Problem(motif, statusCode: 400);
            var log = journaux.CreateLogger("Pharma.Cloud.Epoque");
            try
            {
                var rep = await depot.DeclarerEpoqueAsync(appelant.Officine, d, ct);
                log.LogInformation("Officine {Officine} : époque {Ancienne} -> {Nouvelle} déclarée (déjà reçue : {Deja}, avant le premier lot : {Avant})",
                    appelant.Officine, d.AncienneEpoque, d.NouvelleEpoque, rep.DejaDeclaree, rep.AvantPremierLot);
                return Results.Ok(rep);
            }
            catch (Npgsql.PostgresException p) when (p.SqlState == "23505")
            {
                return Results.Problem("Une autre déclaration de cette époque a déjà été reçue : la première fait foi.", statusCode: 409);
            }
            catch (Npgsql.PostgresException p) when (p.SqlState == "22023")
            {
                // Raison écrite au journal du central (époques, n° de lot ou fin de chaîne : aucune donnée de patient)
                log.LogWarning("Officine {Officine} : déclaration d'époque {Nouvelle} refusée : {Raison}", appelant.Officine, d.NouvelleEpoque, p.MessageText);
                return Results.Problem("Déclaration refusée par le serveur central (époques, n° de lot ou fins de chaîne).", statusCode: 400);
            }
        }).RequireRateLimiting("officine");

        // Voie descendante (script 005) : échange léger initié par l'officine à chaque passage, même sans lot à envoyer
        v1.MapPost("/echange", async (EchangeDescendantDto d, HttpContext ctx, DepotCentral depot, CancellationToken ct) =>
        {
            var (appelant, refus) = await AppelantAsync(ctx, ct);
            if (appelant is null) return refus!;
            if (d.VersionAppliquee < 0) return Results.Problem("Version descendante hors bornes.", statusCode: 400);
            return Results.Ok(await depot.DescendantsAsync(appelant.Officine, d.VersionAppliquee, ct));
        }).RequireRateLimiting("officine");

        // Lot S7.2b (script 033) : code de rattachement demandé par le poste (Administration › Synchronisation › Accès à distance).
        // Officine = celle du certificat et du jeton (AppelantAsync), jamais un champ de la demande ; code montré une seule fois
        // (réponse non mise en cache), jamais journalisé ; compte inconnu, non titulaire ou désactivé : même réponse neutre (422) ;
        // 5 demandes par heure et par officine (429 avec motif).
        v1.MapPost("/rattachement", async (DemandeCodeRattachementDto d, HttpContext ctx, Npgsql.NpgsqlDataSource db, ILoggerFactory lf, CancellationToken ct) =>
        {
            var (appelant, refus) = await AppelantAsync(ctx, ct);
            if (appelant is null) return refus!;
            ctx.Response.Headers.CacheControl = "no-store";
            var log = lf.CreateLogger("Pharma.Cloud.Rattachement");
            try
            {
                var (statut, code, expire) = await Comptes.RattachementPoste.DemanderAsync(db, appelant.Officine, appelant.EmpreinteCertificat, d.Compte, ct);
                if (Comptes.RattachementPoste.Reponse(statut) is { } r)
                {
                    log.LogWarning("Officine {Officine} : code de rattachement non délivré ({Statut})", appelant.Officine, statut);
                    return Results.Problem(r.Motif, statusCode: r.Code);
                }
                log.LogInformation("Officine {Officine} : code de rattachement délivré au poste (30 min)", appelant.Officine);
                return Results.Ok(new ReponseCodeRattachementDto(code!, expire!.Value));
            }
            catch (Npgsql.PostgresException p) when (p.SqlState == "42501")
            {
                // Certificat révoqué ou officine partie entre le contrôle de l'API et la base
                return Results.Problem("Certificat révoqué, échu ou inconnu.", statusCode: 401);
            }
        }).RequireRateLimiting("officine");

        // Lot S5.3 : téléchargement d'un fichier de référentiel (nomenclature) désigné par un ordre de cette officine ; 404 sinon
        v1.MapGet("/referentiels/{id:long}", async (long id, HttpContext ctx, DepotCentral depot, CancellationToken ct) =>
        {
            var (appelant, refus) = await AppelantAsync(ctx, ct);
            if (appelant is null) return refus!;
            var contenu = await depot.ReferentielAsync(appelant.Officine, id, ct);
            if (contenu is null)
            {
                await depot.JournaliserAsync(appelant.Officine, appelant.EmpreinteCertificat, "referentiel_refuse", $"fichier {id}", ct);
                return Results.Problem("Fichier inconnu ou non destiné à cette officine.", statusCode: 404);
            }
            await depot.JournaliserAsync(appelant.Officine, appelant.EmpreinteCertificat, "referentiel_telecharge", $"fichier {id}", ct);
            return Results.Bytes(contenu, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet");
        }).RequireRateLimiting("officine");

        // Lot S6 : début d'un chargement complet (avant le premier lot qui contient ses lignes « instantane »)
        v1.MapPost("/chargement/debut", async (DebutChargementDto d, HttpContext ctx, DepotCentral depot, ILoggerFactory lf, CancellationToken ct) =>
        {
            var (appelant, refus) = await AppelantAsync(ctx, ct);
            if (appelant is null) return refus!;
            if (d.Refus() is { } motif) return Results.Problem(motif, statusCode: 400);
            var log = lf.CreateLogger("Pharma.Cloud.Chargement");
            try
            {
                var (dernier, abandonnes) = await depot.DebuterChargementAsync(appelant.Officine, d, ct);
                ctx.RequestServices.GetRequiredService<SuiviChargementCentral>().Oublier(appelant.EmpreinteCertificat);
                log.LogInformation("Officine {Officine} : début du chargement {Numero} ({Motif}, {Tables} tables, {Abandonnes} lot(s) abandonné(s))",
                    appelant.Officine, d.Numero, d.Motif, d.Tables.Count, abandonnes);
                return Results.Ok(new ReponseDebutChargementDto(d.Numero, dernier, abandonnes));
            }
            catch (Npgsql.PostgresException p) when (p.SqlState == "P0001" && p.MessageText.StartsWith("quota_depasse", StringComparison.Ordinal))
            {
                // Trop de chargements complets sur 24 h (script 009) : refus temporaire, l'officine réessaiera plus tard
                await depot.JournaliserAsync(appelant.Officine, appelant.EmpreinteCertificat, "quota_depasse", $"début du chargement {d.Numero}", ct);
                log.LogWarning("Officine {Officine} : début du chargement {Numero} refusé (quota de chargements sur 24 h)", appelant.Officine, d.Numero);
                return Results.Problem("Trop de chargements complets sur 24 heures : réessayez plus tard.", statusCode: 429);
            }
            catch (Npgsql.PostgresException p) when (p.SqlState == "P0001")
            {
                await depot.JournaliserAsync(appelant.Officine, appelant.EmpreinteCertificat, "chargement_refuse", $"début du chargement {d.Numero} : SQL {p.SqlState}", ct);
                log.LogWarning("Officine {Officine} : début du chargement {Numero} refusé", appelant.Officine, d.Numero);
                return Results.Problem("Chargement refusé (détail au journal du serveur central).", statusCode: 422);
            }
        }).RequireRateLimiting("officine");

        // Lot S6 : fin d'un chargement complet (toutes ses lignes accusées) ; lignes absentes de l'officine signalées et gardées
        v1.MapPost("/chargement/fin", async (FinChargementDto d, HttpContext ctx, DepotCentral depot, ILoggerFactory lf, CancellationToken ct) =>
        {
            var (appelant, refus) = await AppelantAsync(ctx, ct);
            if (appelant is null) return refus!;
            if (d.Numero < 1 || d.Lignes is null || d.Lignes.Count > 300 || d.Lignes.Values.Any(v => v < 0))
                return Results.Problem("Fin de chargement mal formée.", statusCode: 400);
            var log = lf.CreateLogger("Pharma.Cloud.Chargement");
            try
            {
                var (absentes, parTable, vues) = await depot.TerminerChargementAsync(appelant.Officine, d.Numero,
                    System.Text.Json.JsonSerializer.Serialize(d.Lignes), ct);
                ctx.RequestServices.GetRequiredService<SuiviChargementCentral>().Oublier(appelant.EmpreinteCertificat);
                if (absentes > 0)
                    log.LogWarning("Officine {Officine} : chargement {Numero} terminé, {Absentes} ligne(s) absente(s) de l'officine signalée(s) et gardée(s)",
                        appelant.Officine, d.Numero, absentes);
                else log.LogInformation("Officine {Officine} : chargement {Numero} terminé", appelant.Officine, d.Numero);
                return Results.Ok(new ReponseFinChargementDto(d.Numero, absentes,
                    System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, long>>(parTable) ?? [],
                    System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, long>>(vues) ?? []));
            }
            catch (Npgsql.PostgresException p) when (p.SqlState == "P0001")
            {
                await depot.JournaliserAsync(appelant.Officine, appelant.EmpreinteCertificat, "chargement_refuse", $"fin du chargement {d.Numero} : SQL {p.SqlState}", ct);
                log.LogWarning("Officine {Officine} : fin du chargement {Numero} refusée", appelant.Officine, d.Numero);
                return Results.Problem("Fin de chargement refusée (chargement inconnu ou remplacé).", statusCode: 422);
            }
        }).RequireRateLimiting("officine");

        // Renouvellement avant l'échéance : nouveau certificat (nouvelle clé), l'ancien reste valable jusqu'à son échéance
        v1.MapPost("/certificat/renouveler", async (DemandeRenouvellementDto d, HttpContext ctx, AutoriteCertification ac, DepotCentral depot,
            TimeProvider temps, CancellationToken ct) =>
        {
            var (appelant, refus) = await AppelantAsync(ctx, ct);
            if (appelant is null) return refus!;
            X509Certificate2 cert;
            try { cert = ac.SignerDemande(d.DemandePem, appelant.Officine, appelant.Serveur, temps.GetUtcNow()); }
            catch (DemandeInvalideException) { return Results.Problem("Demande de certificat refusée.", statusCode: 400); }
            using (cert)
            {
                if (!await depot.RenouvelerAsync(appelant.EmpreinteCertificat, AutoriteCertification.Empreinte(cert), cert.SerialNumber, cert.NotAfter.ToUniversalTime(), ct))
                    return Results.Problem("Renouvellement refusé.", statusCode: 403);
                return Results.Ok(new ReponseActivationDto(appelant.Officine, appelant.Serveur, cert.ExportCertificatePem(), ac.RacinePem,
                    new DateTimeOffset(cert.NotAfter.ToUniversalTime())));
            }
        }).RequireRateLimiting("renouvellement");
    }

    /// <summary>Certificat client émis par l'autorité, connu, ni révoqué ni échu, officine non résiliée.</summary>
    private static async Task<((EtatCertificat Etat, string Empreinte)?, IResult?)> CertificatAsync(HttpContext ctx, CancellationToken ct)
    {
        var c = ctx.Connection.ClientCertificate ?? await ctx.Connection.GetClientCertificateAsync(ct);
        if (c is null) return (null, Results.Problem("Certificat client exigé.", statusCode: 401));
        var temps = ctx.RequestServices.GetRequiredService<TimeProvider>().GetUtcNow();
        if (!ctx.RequestServices.GetRequiredService<AutoriteCertification>().ValiderClient(c, temps))
            return (null, Results.Problem("Certificat client non reconnu.", statusCode: 401));
        var empreinte = AutoriteCertification.Empreinte(c);
        var depot = ctx.RequestServices.GetRequiredService<DepotCentral>();
        var etat = await depot.CertificatAsync(empreinte, ct);
        if (etat is null || etat.Revoque || etat.Resiliee || etat.Echeance <= temps)
        {
            await depot.JournaliserAsync(etat?.Officine, empreinte, "certificat_refuse", etat is null ? "inconnu" : etat.Revoque ? "révoqué" : etat.Resiliee ? "officine résiliée" : "échu", ct);
            return (null, Results.Problem("Certificat révoqué, échu ou inconnu.", statusCode: 401));
        }
        return ((etat, empreinte), null);
    }

    /// <summary>Jeton valide, lié au certificat présenté, et certificat toujours valable (révocation consultée à chaque appel).</summary>
    private static async Task<(AppelantSync?, IResult?)> AppelantAsync(HttpContext ctx, CancellationToken ct)
    {
        var (etat, refus) = await CertificatAsync(ctx, ct);
        if (etat is null) return (null, refus);
        var entete = ctx.Request.Headers.Authorization.ToString();
        if (!entete.StartsWith("Bearer ", StringComparison.Ordinal)) return (null, Results.Problem("Jeton d'accès exigé.", statusCode: 401));
        var appelant = await ctx.RequestServices.GetRequiredService<JetonsCentral>()
            .VerifierAsync(entete["Bearer ".Length..], etat.Value.Empreinte, ctx.RequestServices.GetRequiredService<TimeProvider>().GetUtcNow());
        if (appelant is null || appelant.Officine != etat.Value.Etat.Officine)
            return (null, Results.Problem("Jeton invalide, expiré ou non lié à ce certificat.", statusCode: 401));
        return (appelant, null);
    }
}
