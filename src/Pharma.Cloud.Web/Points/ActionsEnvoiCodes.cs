using System.Globalization;
using Pharma.Cloud.Api.Depart;
using Pharma.Cloud.Web.Codes;
using Pharma.Cloud.Web.Donnees;
using Pharma.Cloud.Web.Securite;

namespace Pharma.Cloud.Web.Points;

/// <summary>
/// Lot S7.6 (script central 037) : actions de l'écran « Envoi des codes » et du contact des comptes, ÉDITEUR seulement (le titulaire
/// est renvoyé vers « accès refusé » par le contrôle d'accès, et la base revérifie le rôle). Le mot de passe SMTP saisi est chiffré
/// ici (protection des données du central) avant de partir vers la base ; jamais réaffiché, jamais journalisé, jamais dans une
/// adresse. Après une action, retour à la page avec une clé de message (jamais un texte saisi).
/// </summary>
public static class ActionsEnvoiCodes
{
    public const string Page = "/envoi-codes";
    public const string PageComptes = "/comptes";

    /// <summary>Saisie du formulaire « Envoi des codes » (mot de passe gardé en mémoire le temps de la requête seulement).</summary>
    public sealed record Saisie(string? Canal, string? Serveur, int? Port, bool PortIllisible, string? Tls, string? Identifiant, string? Expediteur, string? MotDePasse)
    {
        public static Saisie Lire(IFormCollection f)
        {
            string? T(string nom) => f[nom].ToString().Trim() is { Length: > 0 } v ? v : null;
            var texte = T("port");
            int? port = int.TryParse(texte, NumberStyles.None, CultureInfo.InvariantCulture, out var p) ? p : null;
            // Mot de passe : jamais rogné (une espace peut en faire partie) ; vide = inchangé
            var mdp = f["mot_de_passe"].ToString();
            return new Saisie(T("canal"), T("serveur"), port, texte is not null && port is null, T("tls"), T("identifiant"), T("expediteur"),
                mdp.Length is > 0 and <= 256 ? mdp : null);
        }

        public override string ToString() => $"canal {Canal}, SMTP {Serveur}:{Port} {Tls}, mot de passe {(MotDePasse is null ? "inchangé" : "saisi (masqué)")}";
    }

    public static void Mapper(RouteGroupBuilder g)
    {
        // Enregistrement (code de second facteur « action ») : contrôles de la base AVANT le code ; mot de passe remplacé seulement s'il est saisi
        g.MapPost("/envoi-codes", async (HttpContext ctx, DepotEcrans depot, CodesSecondFacteur codes, ChoixEnvoiCodes choix) =>
        {
            if (ctx.Session() is not { Editeur: true }) return Results.Redirect("/acces-refuse");
            var f = await ctx.Request.ReadFormAsync(ctx.RequestAborted);
            var s = Saisie.Lire(f);
            if (s.PortIllisible) return Results.Redirect($"{Page}?erreur=envoi_codes_port");
            // SMS seul sans fournisseur : plus aucun code ne partirait (connexion impossible pour tous, éditeur compris)
            if (s.Canal == "sms" && !choix.SmsDisponible) return Results.Redirect($"{Page}?erreur=envoi_codes_sms");
            string? chiffre = null;
            if (s.MotDePasse is not null)
            {
                if (choix.Protection is null) return Results.Redirect($"{Page}?erreur=envoi_codes_cles");
                chiffre = choix.Protection.Proteger(s.MotDePasse);
            }
            var statut = await depot.EnregistrerReglageEnvoiCodesAsync(ctx.EmpreinteJeton()!, codes.Empreinte("action", f["code"].ToString()), s.Canal, s.Serveur,
                s.Port, s.Tls, s.Identifiant, s.Expediteur, chiffre, ctx.RequestAborted);
            if (statut == "ok")
            {
                choix.Oublier();
                return Results.Redirect($"{Page}?ok=envoi_codes_enregistre");
            }
            return Results.Redirect($"{Page}?erreur={Cle("envoi_codes_", statut)}");
        }).RequireRateLimiting("code");

        // E-mail d'essai à une adresse saisie, avec les valeurs du formulaire (mot de passe saisi, sinon celui déjà enregistré) :
        // rien n'est enregistré ; résultat noté au journal des accès, sans l'adresse ni le détail de l'erreur
        g.MapPost("/envoi-codes/essai", async (HttpContext ctx, DepotEcrans depot, ChoixEnvoiCodes choix, ILoggerFactory journaux) =>
        {
            if (ctx.Session() is not { Editeur: true }) return Results.Redirect("/acces-refuse");
            var ct = ctx.RequestAborted;
            var jeton = ctx.EmpreinteJeton()!;
            var f = await ctx.Request.ReadFormAsync(ct);
            var adresse = f["adresse_essai"].ToString().Trim();
            if (!AdresseCourriel.EstValide(adresse)) return Results.Redirect($"{Page}?erreur=essai_adresse");
            var s = Saisie.Lire(f);
            // Même règle que la base (037, ck_parametre_envoi_codes_serveur) : nom d'hôte seul, sans « https:// », port ni espace
            if (s.Serveur is not null && !ServeurValide(s.Serveur)) return Results.Redirect($"{Page}?erreur=envoi_codes_serveur");
            var enregistre = await depot.ReglageEnvoiAsync(jeton, ct);
            // Le mot de passe enregistré ne part QUE vers le serveur et l'identifiant enregistrés : s'ils changent, il faut le ressaisir
            // (sinon une session éditeur détournée l'enverrait à un serveur de son choix)
            if (s.MotDePasse is null && enregistre is not null && !MemeServeur(enregistre, s))
            {
                await depot.NoterEnvoiAsync(jeton, "envoi_codes_essai", "email:incomplet", ct);
                return Results.Redirect($"{Page}?erreur=essai_mot_de_passe");
            }
            var smtp = s.PortIllisible || enregistre is null ? null
                : choix.Smtp(enregistre with { Serveur = s.Serveur, Port = s.Port, Tls = s.Tls ?? "", Identifiant = s.Identifiant, Expediteur = s.Expediteur }, s.MotDePasse);
            if (smtp is null)
            {
                await depot.NoterEnvoiAsync(jeton, "envoi_codes_essai", "email:incomplet", ct);
                return Results.Redirect($"{Page}?erreur=essai_incomplet");
            }
            try
            {
                using var delai = new CancellationTokenSource(EnvoisCodes.DelaiMaximum);   // indépendant de la requête du navigateur
                await choix.Courriel(smtp).EnvoyerAsync(adresse, "Essai d'envoi des codes · écrans du central",
                    """
                    Bonjour,

                    Cet e-mail d'essai a été envoyé depuis l'écran « Envoi des codes » des écrans du central.
                    Le réglage du serveur d'e-mail fonctionne : les codes de second facteur pourront partir par ce compte.

                    Aucun code n'est contenu dans ce message.
                    """, delai.Token);
            }
            catch (Exception e)   // délai dépassé compris (jeton propre de l'essai) : « pas parti », comme les autres échecs
            {
                var refusIdentifiants = ErreursSmtp.RefusIdentifiants(e);
                journaux.CreateLogger("Pharma.Cloud.Web.Codes").LogWarning("E-mail d'essai de l'envoi des codes en échec ({Type}{Statut})", e.GetType().Name,
                    refusIdentifiants ? ", identifiant ou mot de passe refusé par le serveur"
                    : e is MailKit.Net.Smtp.SmtpCommandException c ? ", code SMTP " + (int)c.StatusCode : "");
                await depot.NoterEnvoiAsync(jeton, "envoi_codes_essai", "email:echec", ct);
                return Results.Redirect($"{Page}?erreur={(refusIdentifiants ? "essai_identifiants" : "essai_echec")}");
            }
            await depot.NoterEnvoiAsync(jeton, "envoi_codes_essai", "email:ok", ct);
            return Results.Redirect($"{Page}?ok=essai_envoye");
        }).RequireRateLimiting("code");

        // Contact d'un compte (vide = inchangé, « - » = retiré, au moins un contact ; code de second facteur « action »)
        g.MapPost("/comptes/{compte:guid}/contact", async (Guid compte, HttpContext ctx, DepotEcrans depot, CodesSecondFacteur codes) =>
        {
            if (ctx.Session() is not { Editeur: true }) return Results.Redirect("/acces-refuse");
            var f = await ctx.Request.ReadFormAsync(ctx.RequestAborted);
            var statut = await depot.ModifierContactAsync(ctx.EmpreinteJeton()!, codes.Empreinte("action", f["code"].ToString()), compte,
                f["email"].ToString().Trim(), f["telephone"].ToString().Replace(" ", "", StringComparison.Ordinal).Trim(), ctx.RequestAborted);
            return Results.Redirect(statut == "ok"
                ? $"{PageComptes}?compte={compte}&ok=contact_modifie"
                : $"{PageComptes}?compte={compte}&erreur={Cle("contact_", statut)}");
        }).RequireRateLimiting("code");
    }

    /// <summary>
    /// Script central 038 : remise à zéro du mot de passe d'un AUTRE compte par l'éditeur (script central 040 : aussi pour régénérer un
    /// mot de passe provisoire expiré ; la durée de vie repart à zéro). Le mot de passe provisoire est tiré ici (16 caractères, comme la
    /// console), seule son empreinte BCrypt part vers la base ; il est gardé en mémoire 2 minutes pour la seule session de l'éditeur et
    /// montré UNE fois (page « mot de passe provisoire »), JAMAIS envoyé par e-mail (même canal que le code : les deux facteurs
    /// arriveraient ensemble) ; SMS seulement si un fournisseur est en service ; l'identifiant peut partir par e-mail (case) ; jamais
    /// écrit dans un journal, une adresse ou la console.
    /// </summary>
    public static void MapperRemise(RouteGroupBuilder g)
    {
        g.MapPost("/comptes/{compte:guid}/mot-de-passe", async (Guid compte, HttpContext ctx, DepotEcrans depot, CodesSecondFacteur codes, ComptesAffiches affiches,
            ChoixEnvoiCodes choix, Pharma.Shared.IEnvoiSmsMotDePasse sms, ReglagesEcrans reglages) =>
        {
            if (ctx.Session() is not { Editeur: true } session) return Results.Redirect("/acces-refuse");
            var retour = $"{PageComptes}?compte={compte}&action=mot-de-passe";
            // Son propre compte : refusé avant tout (la base le refuse aussi)
            if (compte == session.Compte) return Results.Redirect($"{retour}&erreur=remise_soi_meme");
            var f = await ctx.Request.ReadFormAsync(ctx.RequestAborted);
            var provisoire = Pharma.Shared.ComptesDistants.MotDePasseProvisoire();
            var statut = await depot.RemettreMotDePasseAsync(ctx.EmpreinteJeton()!, codes.Empreinte("action", f["code"].ToString()), compte, f["motif"].ToString(),
                f["confirmation"].ToString(), BCrypt.Net.BCrypt.HashPassword(provisoire, Pharma.Shared.ComptesDistants.CoutBcrypt), ctx.RequestAborted);
            if (statut != "ok") return Results.Redirect($"{retour}&erreur={Cle("remise_", statut)}");
            // Contact du compte tout juste remis à zéro (la base ne le rend que dans les 2 minutes qui suivent la remise)
            var d = await depot.DestinataireRemiseAsync(ctx.EmpreinteJeton()!, compte, ctx.RequestAborted);
            var transmis = await ActionsImportComptes.TransmettreAsync(ctx, depot, choix, sms, reglages,
                [new Pharma.Shared.CompteATransmettre(compte, d?.Identifiant ?? f["confirmation"].ToString().Trim().ToLowerInvariant(), d?.Role ?? "titulaire",
                    d?.Email, d?.Telephone, provisoire)],
                f[ActionsImportComptes.CaseIdentifiant].ToString() == "1", remise: true);
            return Results.Redirect($"{PageComptes}/mot-de-passe-affiche?compte={compte}&ref={affiches.Garder(session.Id, "remise", transmis)}");
        }).RequireRateLimiting("code");

        // Script central 039 : compte éditeur ou titulaire créé par l'éditeur (code de confirmation) ; mot de passe provisoire tiré ici,
        // empreinte BCrypt seule en base, montré UNE fois à l'éditeur (même page que la remise à zéro) ; script central 040 : identifiant
        // par e-mail si la case est cochée, mot de passe par SMS si possible, sinon « manuel »
        g.MapPost("/comptes/creer", async (HttpContext ctx, DepotEcrans depot, CodesSecondFacteur codes, ComptesAffiches affiches, ChoixEnvoiCodes choix,
            Pharma.Shared.IEnvoiSmsMotDePasse sms, ReglagesEcrans reglages) =>
        {
            if (ctx.Session() is not { Editeur: true } session) return Results.Redirect("/acces-refuse");
            var f = await ctx.Request.ReadFormAsync(ctx.RequestAborted);
            var provisoire = Pharma.Shared.ComptesDistants.MotDePasseProvisoire();
            var (statut, cree) = await depot.CreerCompteAsync(ctx.EmpreinteJeton()!, codes.Empreinte("action", f["code"].ToString()), f["identifiant"].ToString(),
                f["nom"].ToString(), f["role"].ToString(), f["telephone"].ToString(), f["email"].ToString(),
                BCrypt.Net.BCrypt.HashPassword(provisoire, Pharma.Shared.ComptesDistants.CoutBcrypt), ctx.RequestAborted);
            if (statut != "ok" || cree is not { } compte) return Results.Redirect($"{PageComptes}?action=creer&erreur={Cle("creation_", statut)}");
            // Mêmes normalisations que la base (identifiant en minuscules, téléphone sans espace, champs rognés)
            var l = Pharma.Shared.ImportComptesCsv.Normaliser(0, f["role"].ToString(), f["identifiant"].ToString(), f["nom"].ToString(), f["email"].ToString(),
                f["telephone"].ToString());
            var transmis = await ActionsImportComptes.TransmettreAsync(ctx, depot, choix, sms, reglages,
                [new Pharma.Shared.CompteATransmettre(compte, l.Identifiant, l.Role, l.Email, l.Telephone, provisoire)],
                f[ActionsImportComptes.CaseIdentifiant].ToString() == "1", remise: false);
            return Results.Redirect($"{PageComptes}/mot-de-passe-affiche?compte={compte}&ref={affiches.Garder(session.Id, "creation", transmis)}&cree=1");
        }).RequireRateLimiting("code");

        // Script central 040 : import de comptes (aperçu, un seul code, tout ou rien) et durée de vie d'un mot de passe provisoire
        ActionsImportComptes.Mapper(g);
        // Script central 040 : déconnexion après inactivité (réglage par rôle, signal d'activité de la page)
        ActionsImportComptes.MapperInactivite(g);
    }

    /// <summary>Nom d'hôte du serveur d'e-mail (règle du script central 037) : lettres, chiffres, points et tirets, 253 caractères au plus.</summary>
    public static bool ServeurValide(string serveur) =>
        System.Text.RegularExpressions.Regex.IsMatch(serveur, "^[A-Za-z0-9]([A-Za-z0-9.-]{0,251}[A-Za-z0-9])?$");

    /// <summary>Serveur, port, sécurité et identifiant saisis identiques à ceux enregistrés (casse du serveur ignorée).</summary>
    public static bool MemeServeur(ReglageEnvoiCodes enregistre, Saisie s) =>
        string.Equals(enregistre.Serveur, s.Serveur, StringComparison.OrdinalIgnoreCase) && enregistre.Port == s.Port
        && string.Equals(enregistre.Tls, s.Tls ?? "", StringComparison.Ordinal) && string.Equals(enregistre.Identifiant, s.Identifiant, StringComparison.Ordinal);

    /// <summary>Clé du message : préfixée si elle existe (envoi_codes_port…), sinon statut du code (faux, expire…), sinon erreur générale.</summary>
    private static string Cle(string prefixe, string statut) =>
        Messages.Erreurs.ContainsKey(prefixe + statut) ? prefixe + statut : Messages.Erreurs.ContainsKey(statut) ? statut : "envoi";
}
