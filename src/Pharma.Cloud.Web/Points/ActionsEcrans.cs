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
        // Lot S7.6 : envoi des codes réglé dans l'administration du site
        ["reglage_incomplet"] = "Envoi des codes non réglé : aucun code n'est parti. L'éditeur doit compléter « Envoi des codes » (serveur d'e-mail et mot de passe).",
        ["sms_indisponible"] = "Envoi des codes par SMS non disponible (aucun fournisseur retenu) : aucun code n'est parti. Prévenez l'éditeur.",
        ["sans_contact"] = "Votre compte n'a pas de contact pour le canal choisi (e-mail ou téléphone) : aucun code n'est parti. Prévenez l'éditeur.",
        // 2026-11-22 : les codes partent par e-mail (canal « E-mail », ou « E-mail et SMS » sans fournisseur SMS) et le compte n'a pas d'adresse
        ["sans_email"] = "Votre compte n'a pas d'adresse e-mail : aucun code n'est parti (les codes partent par e-mail, l'envoi par SMS n'est pas en service). Demandez à l'éditeur d'ajouter votre adresse e-mail (« Comptes »).",
        ["envoi_codes_canal"] = "Canal inconnu : e-mail, SMS ou les deux.",
        ["envoi_codes_serveur"] = "Serveur d'e-mail invalide : le nom seul, ex. smtp.gmail.com (sans « https:// », sans « :587 », sans espace).",
        ["envoi_codes_port"] = "Port invalide : un nombre de 1 à 65 535 (587 pour Gmail).",
        ["envoi_codes_tls"] = "Sécurité inconnue : STARTTLS ou TLS direct (jamais sans chiffrement).",
        ["envoi_codes_identifiant"] = "Identifiant invalide (254 caractères au plus, sans espace).",
        ["envoi_codes_expediteur"] = "Adresse d'expéditeur invalide.",
        ["envoi_codes_incomplet"] = "Rien n'est enregistré : l'e-mail exige le serveur, le port, l'identifiant, l'expéditeur et le mot de passe.",
        ["envoi_codes_cles"] = "Rien n'est enregistré : dossier des clés du central introuvable ou illisible (PHARMA_CLOUD_DOSSIER ou Ecrans:DossierCles, droits du dossier), le mot de passe ne peut pas être chiffré.",
        ["envoi_codes_sms"] = "Rien n'est enregistré : « SMS » seul est impossible tant qu'aucun fournisseur SMS n'est retenu (plus aucun code ne partirait). Choisissez « E-mail ».",
        ["essai_mot_de_passe"] = "Serveur, port, sécurité ou identifiant modifiés : ressaisissez le mot de passe d'application pour l'e-mail d'essai (le mot de passe enregistré ne part que vers le serveur enregistré).",
        ["essai_adresse"] ="Adresse de l'e-mail d'essai invalide.",
        ["essai_incomplet"] = "E-mail d'essai impossible : serveur, port, sécurité, identifiant, expéditeur et mot de passe nécessaires (saisis ou déjà enregistrés).",
        ["essai_identifiants"] = "Le serveur d'e-mail a refusé l'identifiant ou le mot de passe. Gmail : le mot de passe doit être un « mot de passe d'application » (16 lettres, validation en deux étapes activée), pas celui du compte ; l'identifiant est l'adresse Gmail complète.",
        ["essai_echec"] = "L'e-mail d'essai n'est pas parti : vérifiez le serveur, le port, la sécurité, l'identifiant et le mot de passe d'application. Aucun détail technique n'est affiché ici.",
        ["contact_inconnu"] = "Compte introuvable.",
        ["contact_email"] = "Adresse e-mail invalide.",
        ["contact_telephone"] = "Téléphone invalide : 8 à 15 chiffres, « + » permis en tête.",
        ["contact_sans_contact"] = "Refusé : un compte garde au moins un contact (e-mail ou téléphone).",
        ["contact_inchange"] = "Rien à changer : saisissez une nouvelle adresse, un nouveau numéro ou « - » pour retirer.",
        // Reste S7.2 (script central 038) : remise à zéro d'un mot de passe (éditeur), retrait d'un rattachement (titulaire)
        ["creation_identifiant"] = "Identifiant invalide : 3 à 60 caractères, minuscules, chiffres et « . _ - » (ex. titulaire.dupont).",
        ["creation_existe"] = "Cet identifiant est déjà pris : choisissez-en un autre.",
        ["creation_nom"] = "Nom obligatoire (120 caractères au plus).",
        ["creation_role"] = "Rôle : éditeur ou titulaire.",
        ["creation_contact"] = "Il faut au moins un contact (e-mail ou téléphone) : c'est là que partiront les codes du compte.",
        ["creation_email"] = "Adresse e-mail invalide.",
        ["creation_telephone"] = "Téléphone invalide : 8 à 15 chiffres, « + » permis en tête.",
        ["remise_inconnu"] = "Compte introuvable : rien n'est changé.",
        ["remise_soi_meme"] = "Votre propre mot de passe se change par « Mot de passe » (en-tête), jamais par une remise à zéro.",
        ["remise_inactif"] = "Compte désactivé : aucune remise à zéro.",
        ["remise_motif"] = "Motif obligatoire : 5 à 300 caractères (ex. « appel du titulaire, identité vérifiée »).",
        ["remise_confirmation"] = "Ressaisissez exactement l'identifiant du compte pour confirmer.",
        ["mdp_affiche"] = "Ce mot de passe provisoire a déjà été affiché (ou plus de 2 minutes ont passé) : il n'est plus visible. Refaites une remise à zéro si besoin.",
        ["retrait_refuse"] = "Officine introuvable, rattachée à un autre compte ou déjà retirée : rien n'est changé.",
        ["retrait_confirmation"] = "Ressaisissez exactement le nom de l'officine pour confirmer le retrait.",
        // Reste S7.3 (script central 038) : message à afficher sur les postes
        ["message_vide"] = "Message vide : saisissez le texte à afficher sur les postes.",
        ["message_trop_long"] = "Message trop long : 200 caractères au plus.",
        ["message_texte"] = "Message refusé : texte simple sur une ligne, sans balise (< >) ni caractère invisible.",
        ["message_lien"] = "Message refusé : aucun lien ni adresse de site (http, www., nom de domaine).",
        ["message_duree"] = "Durée d'affichage invalide : de 1 à 7 jours.",
        ["message_format"] = "Message illisible : rien n'est envoyé.",
        ["message_refuse"] = "Message refusé : officine non rattachée, accès retiré ou droit « ordres » non accordé depuis le poste de l'officine.",
        ["message_partie"] = "Cette officine est partie : aucun message.",
        ["message_deja_en_cours"] = "Un message attend déjà d'être relevé par l'officine : attendez son résultat (ou son expiration, 24 heures).",
        ["message_trop_d_ordres"] = "Trop d'ordres envoyés : 10 par heure au plus (messages compris).",
        // Script central 040 : durée de vie du mot de passe provisoire, import de comptes
        ["session_inactivite"] = "Session fermée après inactivité : reconnectez-vous.",
        ["inactivite_editeur"] = "Durée d'inactivité de l'éditeur invalide : un nombre entier de minutes, de 5 à 60.",
        ["inactivite_titulaire"] = "Durée d'inactivité du titulaire invalide : un nombre entier de minutes, de 5 à 60.",
        ["provisoire_expire"] = "Mot de passe provisoire expiré : demandez à l'éditeur une remise à zéro.",
        ["duree_duree"] = "Durée invalide : un nombre entier de jours, de 1 à 30.",
        ["import_vide"] = "Fichier vide ou absent : choisissez un fichier CSV (ligne d'en-tête, puis une ligne par compte).",
        ["import_taille"] = "Fichier trop gros : 128 Ko au plus (200 comptes).",
        ["import_encodage"] = "Fichier illisible : enregistrez-le en UTF-8 (« CSV UTF-8 » dans le tableur).",
        ["import_entete"] = "En-tête invalide : la première ligne doit nommer les colonnes role ; identifiant ; nom ; email ; telephone (séparateur « ; » ou « , »).",
        ["import_trop_de_lignes"] = "Trop de lignes : 200 comptes au plus par fichier. Rien n'est créé.",
        ["import_lignes"] = "Rien n'est créé : corrigez les lignes signalées dans le fichier, puis déposez-le de nouveau.",
        ["import_apercu"] = "Aperçu expiré ou introuvable (15 minutes) : déposez le fichier de nouveau. Rien n'est créé.",
        ["import_existe"] = "Rien n'est créé : un identifiant a été pris entre-temps. Déposez le fichier de nouveau pour voir lequel.",
        ["import_erreur"] = "L'import n'a pas abouti : rien n'est créé.",
    };

    /// <summary>Script central 040 : statut d'une ligne d'un fichier d'import, affiché dans l'aperçu (et par la console).</summary>
    public static readonly IReadOnlyDictionary<string, string> LignesImport = Pharma.Shared.ImportComptesCsv.LibellesStatuts;


    public static readonly Dictionary<string, string> Succes = new()
    {
        ["mdp"] = "Mot de passe changé.",
        ["code_envoye"] = "Code de confirmation envoyé : valable 5 minutes, usage unique.",
        ["certificat_revoque"] = "Certificat révoqué : refusé dès le prochain appel de l'officine.",
        ["serveur_revoque"] = "Certificats du serveur révoqués : refusés dès le prochain appel.",
        ["rattachee"] = "Officine rattachée à votre compte : tableau de bord et journal des accès.",
        ["ordre_envoye"] = "Ordre envoyé : l'officine le relève à son prochain passage (une minute environ) ; résultat ci-dessous.",
        ["reglages_envoyes"] = "Réglages proposés : chaque officine les relève, puis son titulaire les accepte ou les refuse sur le poste (24 heures) ; rien ne change avant. Suivi ci-dessous.",
        // Lot S7.6
        ["envoi_codes_enregistre"] = "Envoi des codes enregistré : appliqué aux prochains codes (dans 10 secondes au plus).",
        ["essai_envoye"] = "E-mail d'essai envoyé : vérifiez la boîte de réception (et les indésirables) de l'adresse saisie.",
        ["contact_modifie"] = "Contact du compte modifié (noté au journal de sécurité).",
        // Script central 038
        ["rattachement_retire"] = "Rattachement retiré : l'officine n'est plus accessible depuis votre compte ; ses ordres encore ouverts sont annulés.",
        // Script central 040
        ["inactivite_enregistree"] = "Déconnexion après inactivité enregistrée : appliquée dès la prochaine requête de chaque session.",
        ["duree_enregistree"] = "Durée de vie des mots de passe provisoires enregistrée : appliquée dès la prochaine connexion.",
        ["message_envoye"] = "Message envoyé : l'officine le relève à son prochain passage et l'affiche sur ses postes jusqu'à la date choisie (statut « Appliqué » = affiché).",
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
        // Jeton anti-falsification exigé pour TOUTES les actions (2026-11-21, script central 040 : constaté qu'une action qui lit le
        // formulaire elle-même, sans paramètre [FromForm], n'était pas vérifiée ; le cookie SameSite=Strict limitait déjà le risque)
        var g = app.MapGroup("/actions").WithMetadata(new Microsoft.AspNetCore.Antiforgery.RequireAntiforgeryTokenAttribute());

        g.MapPost("/connexion", async ([FromForm] string? identifiant, [FromForm(Name = "mot_de_passe")] string? motDePasse, HttpContext ctx,
            DepotEcrans depot, CodesSecondFacteur codes, ChoixEnvoiCodes envoi, ILoggerFactory journaux) =>
        {
            var ct = ctx.RequestAborted;
            var id = ComptesDistants.NormaliserIdentifiant(identifiant);
            (Guid? Compte, string? Empreinte) compte = ComptesDistants.IdentifiantValide(id) ? await depot.DebutConnexionAsync(id!, ct) : (null, null);
            // Toujours une vérification BCrypt (empreinte factice si le compte est inconnu ou bloqué) : même durée de réponse
            var reussi = BCrypt.Net.BCrypt.Verify(motDePasse ?? "", compte.Empreinte ?? BcryptFactice) && compte.Empreinte is not null;
            var jeton = Jetons.Nouveau();
            var statut = await depot.FinConnexionAsync(compte.Compte, reussi, reussi ? Jetons.Empreinte(jeton) : null, ct);
            // Script central 040 : mot de passe juste mais provisoire non utilisé dans sa durée de vie (jamais dit pour un mot de passe faux)
            if (statut == "provisoire_expire") return Results.Redirect("/connexion?erreur=provisoire_expire");
            if (statut != "code_attendu") return Results.Redirect("/connexion?erreur=identifiants");
            Jetons.Poser(ctx.Response, jeton);
            var envoye = await EnvoyerCodeAsync(depot, codes, envoi, Jetons.Empreinte(jeton), "connexion", journaux, ct);
            return Results.Redirect(envoye is null ? "/code" : $"/code?erreur={envoye}");
        }).RequireRateLimiting("connexion");

        g.MapPost("/renvoyer-code", async (HttpContext ctx, DepotEcrans depot, CodesSecondFacteur codes, ChoixEnvoiCodes envoi, ILoggerFactory journaux) =>
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

        // Script central 040 : motif « inactivite » quand la page se ferme d'elle-même (échéance d'inactivité atteinte) : message à la
        // connexion ; permise sans session (la base a pu fermer la session juste avant)
        g.MapPost("/deconnexion", async ([FromForm] string? motif, HttpContext ctx, DepotEcrans depot) =>
        {
            if (ctx.EmpreinteJeton() is { } e) await depot.DeconnexionAsync(e, ctx.RequestAborted);
            Jetons.Retirer(ctx.Response);
            return Results.Redirect(motif == "inactivite" ? "/connexion?erreur=session_inactivite" : "/connexion");
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

        // Script central 038 : retrait d'un rattachement par le titulaire lui-même (nom de l'officine ressaisi, code de confirmation)
        g.MapPost("/mes-officines/{officine:guid}/retrait", async (Guid officine, [FromForm] string? confirmation, [FromForm] string? code, HttpContext ctx,
            DepotEcrans depot, CodesSecondFacteur codes) =>
        {
            var (statut, _) = await depot.RetirerRattachementAsync(ctx.EmpreinteJeton()!, officine, confirmation, codes.Empreinte("action", code), ctx.RequestAborted);
            return Results.Redirect(statut == "ok"
                ? "/mes-officines?ok=rattachement_retire"
                : $"/mes-officines/{officine}/retirer?erreur={(Messages.Erreurs.ContainsKey("retrait_" + statut) ? "retrait_" + statut : Cle(statut))}");
        }).RequireRateLimiting("code");

        // Script central 038 : message à afficher sur les postes (texte simple contrôlé ici, puis par la base, puis par le poste ; droit
        // « ordres », code de confirmation). Après un refus, la saisie ne revient PAS dans l'adresse (texte libre : jamais dans une adresse).
        g.MapPost("/mes-officines/{officine:guid}/message", async (Guid officine, [FromForm] string? texte, [FromForm] string? jours, [FromForm] string? code,
            HttpContext ctx, DepotEcrans depot, CodesSecondFacteur codes) =>
        {
            var page = $"/mes-officines/{officine}/ordres";
            var t = (texte ?? "").Trim();
            string statut;
            if (!int.TryParse(jours, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var j)
                || !Pharma.Domain.Reglages.MessagePostes.JoursValides(j))
                statut = "duree";
            else if (Pharma.Domain.Reglages.MessagePostes.ControlerTexte(t) is { } refus) statut = refus;
            else statut = await depot.EnvoyerMessageAsync(ctx.EmpreinteJeton()!, officine, t, j, codes.Empreinte("action", code), ctx.RequestAborted);
            return Results.Redirect(statut == "ok"
                ? $"{page}?ok=message_envoye"
                : $"{page}?erreur={(Messages.Erreurs.ContainsKey("message_" + statut) ? "message_" + statut : Cle(statut))}");
        }).RequireRateLimiting("code");

        // Code de confirmation d'une action (second facteur à chaque action sensible)
        g.MapPost("/code-action", async ([FromForm] string? retour, HttpContext ctx, DepotEcrans depot, CodesSecondFacteur codes, ChoixEnvoiCodes envoi,
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

        // Lot S7.6 : envoi des codes (réglage, e-mail d'essai) et contact des comptes, éditeur seulement
        ActionsEnvoiCodes.Mapper(g);
        // Script central 038 : remise à zéro du mot de passe d'un autre compte, éditeur seulement
        ActionsEnvoiCodes.MapperRemise(g);
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

    /// <summary>
    /// Tire un code, garde son empreinte HMAC en base, l'envoie sur le canal du réglage (lot S7.6) ; rend null ou la clé du message
    /// d'erreur. Résultat de chaque canal noté au journal des accès (jamais le code, l'adresse ni le numéro).
    /// </summary>
    private static async Task<string?> EnvoyerCodeAsync(DepotEcrans depot, CodesSecondFacteur codes, ChoixEnvoiCodes choix, string empreinteJeton, string objet,
        ILoggerFactory journaux, CancellationToken ct)
    {
        var journal = journaux.CreateLogger("Pharma.Cloud.Web.Codes");
        var (envoi, refus) = await choix.ChoisirAsync(empreinteJeton, ct);
        if (envoi is null)
        {
            await depot.NoterEnvoiAsync(empreinteJeton, "code_envoi", "reglage:incomplet", ct);
            return refus ?? ChoixEnvoiCodes.RefusIncomplet;
        }
        var code = CodesSecondFacteur.Tirer();
        var (statut, destinataire) = await depot.CreerCodeAsync(empreinteJeton, objet, codes.Empreinte(objet, code)!, envoi.Canal, ct);
        if (statut is "trop_de_codes" or "sans_contact" or "session") return CleRefusCode(statut, envoi.Canal);
        if (statut != "envoye" || destinataire is null) return "envoi";
        // Code créé : l'envoi et sa trace ne dépendent plus de la requête du navigateur (délai propre de 30 s au plus)
        using var delai = new CancellationTokenSource(EnvoisCodes.DelaiMaximum);
        var resultats = await EnvoisCodes.EnvoyerAsync(envoi, destinataire, code, delai.Token);
        foreach (var r in resultats)
        {
            await depot.NoterEnvoiAsync(empreinteJeton, "code_envoi", r.Objet, CancellationToken.None);
            if (!r.Parti) journal.LogWarning("Envoi d'un code de second facteur : {Canal} {Resultat} ({Type})", r.Canal, r.Resultat, r.TypeErreur ?? "-");
        }
        if (resultats.Any(r => r.Parti)) return null;
        return resultats.All(r => r.Canal == "sms" && r.Resultat == "indisponible") ? "sms_indisponible"
            : resultats.All(r => r.Resultat == "sans_contact") ? CleRefusCode("sans_contact", envoi.Canal) : "envoi";
    }

    /// <summary>
    /// Clé du message d'un refus de code (règle pure, testée ; 2026-11-22) : compte sans contact alors que le code part par e-mail
    /// (canal « E-mail », ou « E-mail et SMS » ramené à l'e-mail sans fournisseur SMS) → « sans_email » (votre compte n'a pas d'adresse
    /// e-mail…) au lieu du message général ; autres statuts inchangés.
    /// </summary>
    public static string CleRefusCode(string statut, string canal) => statut == "sans_contact" && canal == "email" ? "sans_email" : statut;
}
