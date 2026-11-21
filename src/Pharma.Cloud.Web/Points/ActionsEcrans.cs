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
        ["rattachement_inconnu"] = "Code de rattachement inconnu : vérifiez les 12 caractères, ou demandez un nouveau code.",
        ["rattachement_expire"] = "Code de rattachement expiré (30 minutes) : demandez-en un nouveau.",
        ["rattachement_utilise"] = "Code de rattachement déjà utilisé ou remplacé par un plus récent : demandez-en un nouveau.",
        ["rattachement_partie"] = "Cette officine est partie : rattachement impossible.",
        ["trop_d_essais"] = "Trop d'essais : attendez un quart d'heure avant de saisir un autre code.",
        ["officine_refusee"] = "Officine introuvable ou non rattachée à votre compte (ou droit absent).",
        // Lot S7.3 : ordres du titulaire
        ["ordre_refuse"] = "Ordre refusé : officine non rattachée, accès retiré ou droit « ordres » non accordé depuis le poste de l'officine.",
        ["ordre_hors_liste"] = "Ordre refusé : seuls l'essai de liaison, la sauvegarde et la vérification d'intégrité peuvent être demandés à distance.",
        ["ordre_partie"] = "Cette officine est partie : aucun ordre.",
        ["ordre_deja_en_cours"] = "Un ordre du même type attend déjà l'officine : attendez son résultat (ou son expiration, 24 heures).",
        ["ordre_trop_d_ordres"] = "Trop d'ordres envoyés : 10 par heure au plus.",
        // Lot S7.4 : réglages communs (rien n'est envoyé : tout ou rien)
        ["reglages_refuse"] = "Rien n'est envoyé : une officine choisie n'est pas rattachée à votre compte, son accès est retiré ou le droit « ordres » n'y est pas accordé depuis son poste.",
        ["reglages_partie"] = "Rien n'est envoyé : une officine choisie est partie.",
        ["reglages_deja_en_cours"] = "Rien n'est envoyé : une officine choisie a encore une proposition de réglages en attente (décision sur son poste, ou expiration après 24 heures).",
        ["reglages_trop_d_envois"] = "Trop d'envois de réglages : 5 par heure au plus.",
        ["reglages_hors_liste"] = "Rien n'est envoyé : réglage hors de la liste permise (seuils d'alerte de stock et de péremption seulement).",
        ["reglages_hors_bornes"] = "Rien n'est envoyé : une valeur n'est pas un nombre entier ou sort des bornes indiquées.",
        ["reglages_incoherent"] = "Rien n'est envoyé : l'alerte rouge doit être plus courte que l'alerte orange.",
        ["reglages_vide"] = "Rien n'est envoyé : saisissez au moins un réglage à proposer.",
        ["reglages_format"] = "Rien n'est envoyé : réglages illisibles.",
        ["reglages_aucune_officine"] = "Rien n'est envoyé : cochez au moins une officine.",
        ["reglages_trop_d_officines"] = "Rien n'est envoyé : 50 officines au plus par envoi.",
    };

    public static readonly Dictionary<string, string> Succes = new()
    {
        ["mdp"] = "Mot de passe changé.",
        ["code_envoye"] = "Code de confirmation envoyé : valable 5 minutes, usage unique.",
        ["certificat_revoque"] = "Certificat révoqué : refusé dès le prochain appel de l'officine.",
        ["serveur_revoque"] = "Certificats du serveur révoqués : refusés dès le prochain appel.",
        ["rattachee"] = "Officine rattachée à votre compte : tableau de bord et journal des accès.",
        ["ordre_envoye"] = "Ordre envoyé : l'officine le relève à son prochain passage (une minute environ) ; résultat ci-dessous.",
        ["reglages_envoyes"] = "Réglages proposés : chaque officine les relève, puis son titulaire les accepte ou les refuse sur le poste (24 heures) ; rien ne change avant. Suivi ci-dessous.",
    };

    public static string? Erreur(string? cle) => cle is not null && Erreurs.TryGetValue(cle, out var m) ? m : null;

    public static string? Reussite(string? cle) => cle is not null && Succes.TryGetValue(cle, out var m) ? m : null;
}

/// <summary>
/// Lot S7.4 : saisie du formulaire des réglages communs. Champs « r.&lt;clé&gt; » (vide = réglage non proposé) ; une clé hors de la liste
/// fermée est GARDÉE pour être refusée (hors_liste) ; une valeur qui n'est pas un entier rend la saisie illisible (hors_bornes).
/// </summary>
public static class SaisieReglages
{
    public const string Prefixe = "r.";

    public static (Dictionary<string, int> Valeurs, bool Illisible) Lire(IFormCollection formulaire)
    {
        var valeurs = new Dictionary<string, int>(StringComparer.Ordinal);
        var illisible = false;
        foreach (var (nom, v) in formulaire.Where(f => f.Key.StartsWith(Prefixe, StringComparison.Ordinal)).Take(30))
        {
            var texte = v.ToString().Trim();
            if (texte.Length == 0) continue;
            var cle = nom[Prefixe.Length..];
            if (cle.Length is 0 or > 60) { illisible = true; continue; }
            if (int.TryParse(texte, System.Globalization.NumberStyles.AllowLeadingSign, System.Globalization.CultureInfo.InvariantCulture, out var n)) valeurs[cle] = n;
            else illisible = true;
        }
        // Maquette validée (lot S7.4) : un réglage choisi dans la liste et sa valeur
        if (formulaire["reglage"].ToString().Trim() is { Length: > 0 } choisi)
        {
            if (choisi.Length > 60) illisible = true;
            else if (int.TryParse(formulaire["valeur"].ToString().Trim(), System.Globalization.NumberStyles.AllowLeadingSign,
                         System.Globalization.CultureInfo.InvariantCulture, out var n)) valeurs[choisi] = n;
            else illisible = true;
        }
        return (valeurs, illisible);
    }

    /// <summary>Adresse de l'écran avec la saisie (réglages de la liste fermée et officines seulement) : « ?v=cle:120,cle:60&amp;o=uuid,uuid ».</summary>
    public static string Adresse(IReadOnlyDictionary<string, int> valeurs, IReadOnlyList<Guid> officines)
    {
        var v = string.Join(',', Pharma.Domain.Reglages.ReglagesCommuns.Liste.Where(r => valeurs.ContainsKey(r.Cle))
            .Select(r => $"{r.Cle}:{valeurs[r.Cle].ToString(System.Globalization.CultureInfo.InvariantCulture)}"));
        var o = string.Join(',', officines.Take(50));
        var parties = new[] { v.Length > 0 ? "v=" + v : null, o.Length > 0 ? "o=" + o : null }.Where(x => x is not null);
        var q = string.Join('&', parties);
        return "/mes-officines/reglages" + (q.Length > 0 ? "?" + q : "");
    }

    /// <summary>Saisie relue depuis l'adresse (« v ») : seulement les clés de la liste fermée, valeurs entières.</summary>
    public static Dictionary<string, int> Relire(string? v)
    {
        var valeurs = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var morceau in (v ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries).Take(20))
            if (morceau.Split(':') is [var cle, var n] && Pharma.Domain.Reglages.ReglagesCommuns.Trouver(cle) is not null
                && int.TryParse(n, System.Globalization.NumberStyles.AllowLeadingSign, System.Globalization.CultureInfo.InvariantCulture, out var e))
                valeurs[cle] = e;
        return valeurs;
    }

    /// <summary>Officines relues depuis l'adresse (« o »).</summary>
    public static HashSet<Guid> Officines(string? o) =>
        (o ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries).Take(50).Select(x => Guid.TryParseExact(x, "D", out var g) ? g : Guid.Empty)
            .Where(g => g != Guid.Empty).ToHashSet();
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
                    return Results.Redirect(etape == "mot_de_passe" ? "/mot-de-passe" : ControleAcces.Accueil(ctx.Session()!));
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
            return Results.Redirect(statut == "ok" ? ControleAcces.Accueil(session) + "?ok=mdp" : "/connexion?erreur=session");
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
            return Results.Redirect(RetourLocal(retour, ctx.Session() is { } s ? ControleAcces.Accueil(s) : "/connexion"));
        });

        // Lot S7.2a : rattachement d'une officine au compte titulaire par un code (empreinte seule envoyée à la base ; refus comptés)
        g.MapPost("/rattacher", async ([FromForm] string? code, HttpContext ctx, DepotEcrans depot) =>
        {
            var (statut, officine) = await depot.RattacherAsync(ctx.EmpreinteJeton()!, ComptesDistants.EmpreinteCodeRattachement(code), ctx.RequestAborted);
            return statut == "ok" && officine is not null
                ? Results.Redirect($"/mes-officines/{officine}?ok=rattachee")
                : Results.Redirect($"/rattacher?erreur={(statut == "trop_d_essais" ? "trop_d_essais" : Cle("rattachement_" + statut))}");
        }).RequireRateLimiting("code");

        // Lot S7.3 : ordre à distance du titulaire (liste fermée, droit « ordres » accordé au poste, code de confirmation à usage unique)
        g.MapPost("/mes-officines/{officine:guid}/ordre", async (Guid officine, [FromForm] string? type, [FromForm] string? code, HttpContext ctx,
            DepotEcrans depot, CodesSecondFacteur codes) =>
        {
            var statut = await depot.CreerOrdreTitulaireAsync(ctx.EmpreinteJeton()!, officine, type, codes.Empreinte("action", code), ctx.RequestAborted);
            return Results.Redirect(statut == "ok"
                ? $"/mes-officines/{officine}/ordres?ok=ordre_envoye"
                : $"/mes-officines/{officine}/ordres?erreur={(Messages.Erreurs.ContainsKey("ordre_" + statut) ? "ordre_" + statut : Cle(statut))}");
        }).RequireRateLimiting("code");

        // Lot S7.4 : réglages communs envoyés à une ou plusieurs officines (liste fermée contrôlée ici puis par la base, droit « ordres »
        // sur chacune, tout ou rien, code de confirmation). Après un refus, la saisie revient dans l'adresse (nombres et identifiants seulement).
        g.MapPost("/mes-officines/reglages", async (HttpContext ctx, DepotEcrans depot, CodesSecondFacteur codes) =>
        {
            var formulaire = await ctx.Request.ReadFormAsync(ctx.RequestAborted);
            var (valeurs, illisible) = SaisieReglages.Lire(formulaire);
            var officines = formulaire["officine"].Select(o => Guid.TryParseExact(o, "D", out var g) ? g : Guid.Empty).Where(g => g != Guid.Empty)
                .Distinct().Take(51).ToList();
            var retour = SaisieReglages.Adresse(valeurs, officines);
            string statut;
            if (illisible) statut = "hors_bornes";
            else if (Pharma.Domain.Reglages.ReglagesCommuns.Controler(valeurs).Code is { } refus) statut = refus;
            else if (officines.Count == 0) statut = "aucune_officine";
            else
            {
                var r = await depot.EnvoyerReglagesAsync(ctx.EmpreinteJeton()!, officines, Pharma.Domain.Reglages.ReglagesCommuns.Json(valeurs),
                    codes.Empreinte("action", formulaire["code"].ToString()), ctx.RequestAborted);
                if (r.Statut == "ok") return Results.Redirect($"/mes-officines/reglages?ok=reglages_envoyes&n={r.Ordres}");
                statut = r.Statut;
            }
            var cle = Messages.Erreurs.ContainsKey("reglages_" + statut) ? "reglages_" + statut : Messages.Erreurs.ContainsKey(statut) ? statut : "reglages_refuse";
            return Results.Redirect($"{retour}{(retour.Contains('?') ? '&' : '?')}erreur={cle}");
        }).RequireRateLimiting("code");

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
