using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.Mvc;
using Pharma.Cloud.Web.Codes;
using Pharma.Cloud.Web.Donnees;
using Pharma.Cloud.Web.Securite;
using Pharma.Shared;

namespace Pharma.Cloud.Web.Points;

/// <summary>Messages affichés (clé passée dans l'adresse après une action ; jamais un texte saisi renvoyé tel quel).</summary>
public static class Messages
{
    public static readonly Dictionary<string, string> Erreurs = new()
    {
        ["identifiants"] = "Identifiant ou mot de passe incorrect, ou compte bloqué un moment après plusieurs essais.",
        ["code_faux"] = "Code incorrect. Vérifiez-le et recommencez.",
        ["code_expire"] = "Code expiré ou déjà utilisé : demandez-en un nouveau.",
        ["code_epuise"] = "Trop d'essais : reconnectez-vous.",
        ["code_absent"] = "Aucun code en cours : demandez-en un.",
        ["trop_de_codes"] = "Trop de codes demandés : attendez un quart d'heure.",
        ["envoi"] = "Le code n'a pas pu être envoyé. Réessayez plus tard ou prévenez l'éditeur.",
        ["session"] = "Session terminée : reconnectez-vous.",
        ["mdp_actuel"] = "Mot de passe actuel incorrect.",
        ["mdp_identique"] = "Le nouveau mot de passe doit être différent de l'actuel.",
        ["mdp_confirmation"] = "Les deux saisies du nouveau mot de passe diffèrent.",
        ["regle"] = "Mot de passe refusé : 12 caractères au moins, des lettres et des chiffres, sans l'identifiant, pas trop répétitif.",
        ["motif"] = "Motif obligatoire : 5 à 300 caractères.",
        ["confirmation"] = "La confirmation ne correspond pas : ressaisissez exactement ce qui est demandé.",
        ["libelle"] = "Nom de l'officine obligatoire (200 caractères au plus).",
        ["serveur"] = "Serveur inconnu : principal ou second.",
        ["partie"] = "Officine partie : aucun nouveau code.",
        ["inconnu"] = "Élément introuvable ou déjà révoqué.",
        ["faux"] = "Code de confirmation incorrect.",
        ["expire"] = "Code de confirmation expiré : demandez-en un nouveau.",
        ["epuise"] = "Trop d'essais sur ce code : demandez-en un nouveau.",
        ["aucun"] = "Demandez d'abord un code de confirmation.",
        ["autorite"] = "Certificat public de l'autorité introuvable : code d'activation impossible (voir docs/ecrans-central.md).",
        ["officine"] = "Officine introuvable.",
    };

    public static readonly Dictionary<string, string> Succes = new()
    {
        ["mdp"] = "Mot de passe changé.",
        ["code_envoye"] = "Code de confirmation envoyé : valable 5 minutes, usage unique.",
        ["certificat_revoque"] = "Certificat révoqué : refusé dès le prochain appel de l'officine.",
        ["serveur_revoque"] = "Certificats du serveur révoqués : refusés dès le prochain appel.",
    };

    public static string? Erreur(string? cle) => cle is not null && Erreurs.TryGetValue(cle, out var m) ? m : null;

    public static string? Reussite(string? cle) => cle is not null && Succes.TryGetValue(cle, out var m) ? m : null;
}

/// <summary>Certificat PUBLIC de l'autorité (autorite.crt) : début de son empreinte épinglé dans chaque code d'activation. Jamais la clé.</summary>
public sealed class EpinglageAutorite(string chemin)
{
    public string? Empreinte()
    {
        if (!File.Exists(chemin)) return null;
        using var racine = X509Certificate2.CreateFromPem(File.ReadAllText(chemin));
        return Convert.ToHexStringLower(SHA256.HashData(racine.RawData));
    }
}

/// <summary>
/// Actions des écrans : formulaires classiques (POST, jeton anti-falsification vérifié par ASP.NET Core), puis redirection.
/// Aucune route de l'API des officines : ce serveur ne sert que des pages.
/// </summary>
public static class ActionsEcrans
{
    // Empreinte factice (tirée au démarrage) : vérifiée quand le compte est inconnu ou bloqué, pour une durée de réponse identique
    private static readonly string BcryptFactice = BCrypt.Net.BCrypt.HashPassword(Convert.ToBase64String(RandomNumberGenerator.GetBytes(18)), ComptesDistants.CoutBcrypt);

    public static void Mapper(WebApplication app)
    {
        var g = app.MapGroup("/actions");

        g.MapPost("/connexion", async ([FromForm] string? identifiant, [FromForm(Name = "mot_de_passe")] string? motDePasse, HttpContext ctx,
            DepotEcrans depot, CodesSecondFacteur codes, IEnvoiCodeDistant envoi, ILoggerFactory journaux) =>
        {
            var ct = ctx.RequestAborted;
            var id = ComptesDistants.NormaliserIdentifiant(identifiant);
            (Guid? Compte, string? Empreinte) compte = ComptesDistants.IdentifiantValide(id) ? await depot.DebutConnexionAsync(id!, ct) : (null, null);
            // Toujours une vérification BCrypt (empreinte factice si le compte est inconnu ou bloqué) : même durée de réponse
            var reussi = BCrypt.Net.BCrypt.Verify(motDePasse ?? "", compte.Empreinte ?? BcryptFactice) && compte.Empreinte is not null;
            var jeton = Jetons.Nouveau();
            var statut = await depot.FinConnexionAsync(compte.Compte, reussi, reussi ? Jetons.Empreinte(jeton) : null, ct);
            if (statut != "code_attendu") return Results.Redirect("/connexion?erreur=identifiants");
            Jetons.Poser(ctx.Response, jeton);
            var envoye = await EnvoyerCodeAsync(depot, codes, envoi, Jetons.Empreinte(jeton), "connexion", journaux, ct);
            return Results.Redirect(envoye is null ? "/code" : $"/code?erreur={envoye}");
        }).RequireRateLimiting("connexion");

        g.MapPost("/renvoyer-code", async (HttpContext ctx, DepotEcrans depot, CodesSecondFacteur codes, IEnvoiCodeDistant envoi, ILoggerFactory journaux) =>
        {
            var erreur = await EnvoyerCodeAsync(depot, codes, envoi, ctx.EmpreinteJeton()!, "connexion", journaux, ctx.RequestAborted);
            return Results.Redirect(erreur is null ? "/code?ok=code_envoye" : $"/code?erreur={erreur}");
        }).RequireRateLimiting("code");

        g.MapPost("/code", async ([FromForm] string? code, HttpContext ctx, DepotEcrans depot, CodesSecondFacteur codes) =>
        {
            var nouveau = Jetons.Nouveau();
            var (statut, etape) = await depot.ValiderCodeConnexionAsync(ctx.EmpreinteJeton()!, codes.Empreinte("connexion", code), Jetons.Empreinte(nouveau), ctx.RequestAborted);
            switch (statut)
            {
                case "ok":
                    Jetons.Poser(ctx.Response, nouveau);
                    return Results.Redirect(etape == "mot_de_passe" ? "/mot-de-passe" : "/officines");
                case "faux": return Results.Redirect("/code?erreur=code_faux");
                case "expire": return Results.Redirect("/code?erreur=code_expire");
                case "aucun": return Results.Redirect("/code?erreur=code_absent");
                default:
                    Jetons.Retirer(ctx.Response);
                    return Results.Redirect("/connexion?erreur=code_epuise");
            }
        }).RequireRateLimiting("code");

        g.MapPost("/mot-de-passe", async ([FromForm] string? actuel, [FromForm] string? nouveau, [FromForm] string? confirmation, HttpContext ctx, DepotEcrans depot) =>
        {
            var session = ctx.Session()!;
            var ct = ctx.RequestAborted;
            var (_, empreinte) = await depot.DebutConnexionAsync(session.Identifiant, ct);
            if (empreinte is null || !BCrypt.Net.BCrypt.Verify(actuel ?? "", empreinte)) return Results.Redirect("/mot-de-passe?erreur=mdp_actuel");
            if (nouveau != confirmation) return Results.Redirect("/mot-de-passe?erreur=mdp_confirmation");
            if (ComptesDistants.RefusMotDePasse(nouveau, session.Identifiant) is not null) return Results.Redirect("/mot-de-passe?erreur=regle");
            if (BCrypt.Net.BCrypt.Verify(nouveau!, empreinte)) return Results.Redirect("/mot-de-passe?erreur=mdp_identique");
            var statut = await depot.ChangerMotDePasseAsync(ctx.EmpreinteJeton()!, BCrypt.Net.BCrypt.HashPassword(nouveau, ComptesDistants.CoutBcrypt), ct);
            return Results.Redirect(statut == "ok" ? "/officines?ok=mdp" : "/connexion?erreur=session");
        }).RequireRateLimiting("code");

        g.MapPost("/deconnexion", async (HttpContext ctx, DepotEcrans depot) =>
        {
            if (ctx.EmpreinteJeton() is { } e) await depot.DeconnexionAsync(e, ctx.RequestAborted);
            Jetons.Retirer(ctx.Response);
            return Results.Redirect("/connexion");
        });

        g.MapPost("/theme", ([FromForm] string? theme, [FromForm] string? retour, HttpContext ctx) =>
        {
            if (theme is "clair" or "sombre") ctx.Response.Cookies.Append(Jetons.CookieTheme, theme, Jetons.Options());
            else ctx.Response.Cookies.Delete(Jetons.CookieTheme, Jetons.Options());
            return Results.Redirect(RetourLocal(retour, "/officines"));
        });

        // Code de confirmation d'une action (second facteur à chaque action sensible)
        g.MapPost("/code-action", async ([FromForm] string? retour, HttpContext ctx, DepotEcrans depot, CodesSecondFacteur codes, IEnvoiCodeDistant envoi,
            ILoggerFactory journaux) =>
        {
            var erreur = await EnvoyerCodeAsync(depot, codes, envoi, ctx.EmpreinteJeton()!, "action", journaux, ctx.RequestAborted);
            return Results.Redirect(AvecParametre(RetourLocal(retour, "/officines"), erreur is null ? "ok=code_envoye" : $"erreur={erreur}"));
        }).RequireRateLimiting("code");

        g.MapPost("/creer-officine", async ([FromForm] string? code, [FromForm] string? motif, [FromForm] string? libelle, [FromForm] string? confirmation,
            [FromForm] string? serveur, HttpContext ctx, DepotEcrans depot, CodesSecondFacteur codes, EpinglageAutorite autorite, CodesAffiches affiches) =>
        {
            if (autorite.Empreinte() is not { } epinglage) return Results.Redirect("/officines/nouvelle?erreur=autorite");
            var (activation, empreinte) = CodeActivation(epinglage);
            var srv = serveur ?? "principal";
            var (statut, officine) = await depot.CreerOfficineAsync(ctx.EmpreinteJeton()!, codes.Empreinte("action", code), motif, libelle, confirmation, srv,
                empreinte, ctx.RequestAborted);
            if (statut != "ok" || officine is null) return Results.Redirect($"/officines/nouvelle?erreur={Cle(statut)}");
            var reference = affiches.Garder(ctx.Session()!.Id, officine.Value, srv, activation);
            return Results.Redirect($"/officines/{officine}/code-affiche?ref={reference}");
        });

        g.MapPost("/officines/{officine:guid}/nouveau-code", async (Guid officine, [FromForm] string? code, [FromForm] string? motif,
            [FromForm] string? confirmation, [FromForm] string? serveur, HttpContext ctx, DepotEcrans depot, CodesSecondFacteur codes,
            EpinglageAutorite autorite, CodesAffiches affiches) =>
        {
            var page = $"/officines/{officine}/nouveau-code";
            if (autorite.Empreinte() is not { } epinglage) return Results.Redirect($"{page}?erreur=autorite");
            var (activation, empreinte) = CodeActivation(epinglage);
            var srv = serveur ?? "principal";
            var statut = await depot.NouveauCodeActivationAsync(ctx.EmpreinteJeton()!, officine, codes.Empreinte("action", code), motif, confirmation, srv,
                empreinte, ctx.RequestAborted);
            if (statut != "ok") return Results.Redirect($"{page}?serveur={srv}&erreur={Cle(statut)}");
            var reference = affiches.Garder(ctx.Session()!.Id, officine, srv, activation);
            return Results.Redirect($"/officines/{officine}/code-affiche?ref={reference}");
        });

        g.MapPost("/officines/{officine:guid}/revoquer-certificat", async (Guid officine, [FromForm] string? code, [FromForm] string? motif,
            [FromForm] string? empreinte, [FromForm] string? confirmation, HttpContext ctx, DepotEcrans depot, CodesSecondFacteur codes) =>
        {
            var statut = await depot.RevoquerCertificatAsync(ctx.EmpreinteJeton()!, officine, codes.Empreinte("action", code), motif, empreinte, confirmation,
                ctx.RequestAborted);
            return Results.Redirect(statut == "ok"
                ? $"/officines/{officine}?rubrique=certificats&ok=certificat_revoque"
                : $"/officines/{officine}/revoquer-certificat?empreinte={Uri.EscapeDataString(empreinte ?? "")}&erreur={Cle(statut)}");
        });

        g.MapPost("/officines/{officine:guid}/revoquer-serveur", async (Guid officine, [FromForm] string? code, [FromForm] string? motif,
            [FromForm] string? serveur, [FromForm] string? confirmation, HttpContext ctx, DepotEcrans depot, CodesSecondFacteur codes) =>
        {
            var srv = serveur is "second" ? "second" : "principal";
            var statut = await depot.RevoquerServeurAsync(ctx.EmpreinteJeton()!, officine, codes.Empreinte("action", code), motif, srv, confirmation,
                ctx.RequestAborted);
            return Results.Redirect(statut == "ok"
                ? $"/officines/{officine}?rubrique=certificats&ok=serveur_revoque"
                : $"/officines/{officine}/revoquer-serveur?serveur={srv}&erreur={Cle(statut)}");
        });
    }

    /// <summary>Code d'activation (format du poste : secret + début de l'empreinte de l'autorité) et empreinte du secret (seule gardée en base).</summary>
    public static (string Code, string Empreinte) CodeActivation(string empreinteAutorite)
    {
        var code = CodeActivationSync.Generer(empreinteAutorite);
        return (code, CodeActivationSync.EmpreinteSecret(CodeActivationSync.Lire(code)!.Value.Secret));
    }

    /// <summary>Adresse de retour locale seulement (jamais une autre origine).</summary>
    public static string RetourLocal(string? retour, string defaut) =>
        retour is { Length: > 1 and < 300 } r && r[0] == '/' && r[1] != '/' && r[1] != '\\' && !r.Contains("://", StringComparison.Ordinal) && !r.Contains('\\')
            ? r : defaut;

    private static string AvecParametre(string adresse, string parametre)
    {
        var sansMessage = string.Join('&', adresse.Split('?', 2) is [_, var q] ? q.Split('&').Where(p => !p.StartsWith("ok=") && !p.StartsWith("erreur=")) : []);
        var chemin = adresse.Split('?', 2)[0];
        return sansMessage.Length > 0 ? $"{chemin}?{sansMessage}&{parametre}" : $"{chemin}?{parametre}";
    }

    private static string Cle(string? statut) => statut is not null && Messages.Erreurs.ContainsKey(statut) ? statut : "officine";

    /// <summary>Tire un code, garde son empreinte HMAC en base, l'envoie ; rend null ou la clé du message d'erreur. Le code n'est jamais journalisé.</summary>
    private static async Task<string?> EnvoyerCodeAsync(DepotEcrans depot, CodesSecondFacteur codes, IEnvoiCodeDistant envoi, string empreinteJeton, string objet,
        ILoggerFactory journaux, CancellationToken ct)
    {
        var code = CodesSecondFacteur.Tirer();
        var (statut, destinataire) = await depot.CreerCodeAsync(empreinteJeton, objet, codes.Empreinte(objet, code)!, envoi.Canal, ct);
        if (statut == "trop_de_codes") return "trop_de_codes";
        if (statut != "envoye" || destinataire is null) return statut == "session" ? "session" : "envoi";
        try
        {
            await envoi.EnvoyerAsync(destinataire, code, ct);
            return null;
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            journaux.CreateLogger("Pharma.Cloud.Web.Codes").LogWarning("Envoi d'un code de second facteur en échec ({Canal}, {Type})", envoi.Canal, e.GetType().Name);
            return "envoi";
        }
    }
}
