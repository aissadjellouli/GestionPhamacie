using System.Globalization;
using Pharma.Cloud.Web.Codes;
using Pharma.Cloud.Web.Donnees;
using Pharma.Cloud.Web.Securite;
using Pharma.Shared;

namespace Pharma.Cloud.Web.Points;

/// <summary>
/// Script central 040, ÉDITEUR seulement (le contrôle d'accès renvoie le titulaire vers « accès refusé », la base revérifie le rôle) :
///   - import de comptes par fichier CSV : dépôt → aperçu ligne par ligne SANS code (gardé en mémoire, ApercusImport) → UN code pour
///     tout le fichier, tout ou rien ;
///   - durée de vie d'un mot de passe provisoire non utilisé (écran « Envoi des codes », code de confirmation) ;
///   - transmission commune à la création, à l'import et à la remise à zéro : identifiant par e-mail si la case est cochée (jamais le
///     mot de passe), mot de passe par SMS si un fournisseur est en service, sinon « manuel » ; liste montrée UNE fois (ComptesAffiches).
/// </summary>
public static class ActionsImportComptes
{
    public const string RouteApercu = "/actions/comptes/importer/apercu";
    public const long TailleRequeteMax = ImportComptesCsv.OctetsMax + 32 * 1024;
    public const string PageImport = "/comptes?action=importer";
    public const string CaseIdentifiant = "envoyer_identifiant";

    public static void Mapper(RouteGroupBuilder g)
    {
        // Dépôt du fichier : lecture (forme, séparateur, BOM, 200 lignes), puis avis de la base sur chaque ligne (doublon, identifiant
        // pris…) sans code ; l'aperçu reste en mémoire, l'adresse ne porte qu'une référence tirée au sort
        g.MapPost("/comptes/importer/apercu", async (IFormFile? fichier, HttpContext ctx, DepotEcrans depot, ApercusImport apercus) =>
        {
            if (ctx.Session() is not { Editeur: true } session) return Results.Redirect("/acces-refuse");
            if (fichier is null || fichier.Length == 0) return Results.Redirect($"{PageImport}&erreur=import_vide");
            if (fichier.Length > ImportComptesCsv.OctetsMax) return Results.Redirect($"{PageImport}&erreur=import_taille");
            byte[] contenu;
            await using (var flux = fichier.OpenReadStream())
            using (var memoire = new MemoryStream())
            {
                await flux.CopyToAsync(memoire, ctx.RequestAborted);
                contenu = memoire.ToArray();
            }
            var lecture = ImportComptesCsv.Lire(contenu);
            if (lecture.Refus is { } refus) return Results.Redirect($"{PageImport}&erreur=import_{refus}");
            var statuts = await depot.ApercuImportAsync(ctx.EmpreinteJeton()!, ImportComptesCsv.Json(lecture.Lignes), ctx.RequestAborted);
            var reference = apercus.Garder(session.Id, ImportComptesCsv.Fusionner(lecture.Lignes, statuts));
            return Results.Redirect($"{PageImport}&apercu={reference}");
        }).RequireRateLimiting("code");

        // Confirmation : nouvel avis de la base (rien n'est tiré si une ligne est devenue invalide), mots de passe provisoires tirés ici
        // (empreintes BCrypt seules vers la base), UN code pour tout le fichier, tout ou rien ; puis transmission et liste montrée une fois
        g.MapPost("/comptes/importer", async (IFormCollection f, HttpContext ctx, DepotEcrans depot, CodesSecondFacteur codes, ApercusImport apercus,
            ComptesAffiches affiches, ChoixEnvoiCodes choix, IEnvoiSmsMotDePasse sms, ReglagesEcrans reglages) =>
        {
            if (ctx.Session() is not { Editeur: true } session) return Results.Redirect("/acces-refuse");
            var ct = ctx.RequestAborted;
            var jeton = ctx.EmpreinteJeton()!;
            var reference = f["apercu"].ToString();
            if (apercus.Lire(reference, session.Id) is not { } lignes) return Results.Redirect($"{PageImport}&erreur=import_apercu");
            var retour = $"{PageImport}&apercu={reference}";
            if (lignes.Any(l => !l.Valable)) return Results.Redirect($"{retour}&erreur=import_lignes");
            var avis = ImportComptesCsv.Fusionner(lignes, await depot.ApercuImportAsync(jeton, ImportComptesCsv.Json(lignes), ct));
            if (avis.Any(l => !l.Valable))
            {
                apercus.Remplacer(reference, session.Id, avis);
                return Results.Redirect($"{retour}&erreur=import_lignes");
            }
            var provisoires = lignes.ToDictionary(l => l.Numero, _ => ComptesDistants.MotDePasseProvisoire());
            var empreintes = new System.Collections.Concurrent.ConcurrentDictionary<int, string>();
            await Parallel.ForEachAsync(provisoires, ct, (p, _) =>
            {
                empreintes[p.Key] = BCrypt.Net.BCrypt.HashPassword(p.Value, ComptesDistants.CoutBcrypt);
                return ValueTask.CompletedTask;
            });
            var resultat = await depot.ImporterComptesAsync(jeton, codes.Empreinte("action", f["code"].ToString()), ImportComptesCsv.Json(lignes, empreintes), ct);
            if (resultat.FirstOrDefault(r => r.Numero is null) is { Statut: { } global })
                return Results.Redirect($"{retour}&erreur={Cle("import_", global)}");
            if (resultat.Any(r => r.Statut != "ok"))
            {
                apercus.Remplacer(reference, session.Id, ImportComptesCsv.Fusionner(lignes, resultat.Where(r => r.Numero is not null)
                    .ToDictionary(r => r.Numero!.Value, r => r.Statut)));
                return Results.Redirect($"{retour}&erreur=import_lignes");
            }
            apercus.Retirer(reference);
            var parLigne = lignes.ToDictionary(l => l.Numero);
            var comptes = resultat.Where(r => r.Compte is not null && r.Numero is not null).OrderBy(r => r.Numero).Select(r =>
            {
                var l = parLigne[r.Numero!.Value];
                return new CompteATransmettre(r.Compte!.Value, l.Identifiant, l.Role, l.Email, l.Telephone, provisoires[l.Numero]);
            }).ToList();
            var transmis = await TransmettreAsync(ctx, depot, choix, sms, reglages, comptes, f[CaseIdentifiant].ToString() == "1", remise: false);
            return Results.Redirect($"/comptes/mot-de-passe-affiche?ref={affiches.Garder(session.Id, "import", transmis)}&import=1");
        }).RequireRateLimiting("code");

        // Durée de vie d'un mot de passe provisoire non utilisé (1 à 30 jours ; contrôle de la base avant le code)
        g.MapPost("/envoi-codes/duree", async (IFormCollection f, HttpContext ctx, DepotEcrans depot, CodesSecondFacteur codes) =>
        {
            if (ctx.Session() is not { Editeur: true }) return Results.Redirect("/acces-refuse");
            int? jours = int.TryParse(f["duree_jours"].ToString().Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var j) ? j : null;
            var statut = await depot.EnregistrerDureeProvisoireAsync(ctx.EmpreinteJeton()!, codes.Empreinte("action", f["code"].ToString()), jours, ctx.RequestAborted);
            return Results.Redirect(statut == "ok" ? $"{ActionsEnvoiCodes.Page}?ok=duree_enregistree" : $"{ActionsEnvoiCodes.Page}?erreur={Cle("duree_", statut)}");
        }).RequireRateLimiting("code");
    }

    /// <summary>
    /// Déconnexion après inactivité (script central 040) : réglage par rôle (5 à 60 minutes, code de confirmation ; contrôle de la base
    /// avant le code) et signal d'activité de la page. Le signal ne fait rien d'autre que passer par le contrôle d'accès (qui lit la
    /// session, donc note l'activité en base) : la page ne l'envoie que si l'utilisateur a cliqué, tapé ou défilé (une fois par minute au
    /// plus) ou choisi « Rester connecté » ; aucune requête automatique ne prolonge une session.
    /// </summary>
    public static void MapperInactivite(RouteGroupBuilder g)
    {
        g.MapPost("/envoi-codes/inactivite", async (IFormCollection f, HttpContext ctx, DepotEcrans depot, CodesSecondFacteur codes) =>
        {
            if (ctx.Session() is not { Editeur: true }) return Results.Redirect("/acces-refuse");
            int? Minutes(string nom) => int.TryParse(f[nom].ToString().Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var m) ? m : null;
            var statut = await depot.EnregistrerInactiviteAsync(ctx.EmpreinteJeton()!, codes.Empreinte("action", f["code"].ToString()),
                Minutes("inactivite_editeur"), Minutes("inactivite_titulaire"), ctx.RequestAborted);
            return Results.Redirect(statut == "ok" ? $"{ActionsEnvoiCodes.Page}?ok=inactivite_enregistree" : $"{ActionsEnvoiCodes.Page}?erreur={Cle("inactivite_", statut)}");
        }).RequireRateLimiting("code");

        // Signal d'activité (jeton anti-falsification exigé) : secondes restantes avant la déconnexion, d'après l'échéance glissante
        g.MapPost(RouteActiviteRelative, (IFormCollection _, HttpContext ctx) => ctx.Session() is { Ouverte: true } s
            ? Results.Json(new { restant = SecondesRestantes(s, DateTime.UtcNow) })
            : Results.Unauthorized());
    }

    public const string RouteActivite = "/actions/activite";
    private const string RouteActiviteRelative = "/activite";

    /// <summary>Secondes avant la déconnexion après inactivité (échéance glissante de la session ouverte), 0 au plus tôt.</summary>
    public static int SecondesRestantes(SessionEcran session, DateTime maintenantUtc) =>
        (int)Math.Clamp((DateTime.SpecifyKind(session.ExpireLe, DateTimeKind.Utc) - maintenantUtc).TotalSeconds, 0, 24 * 3600);

    /// <summary>
    /// Transmission des comptes (création, import, remise à zéro) : e-mail de l'identifiant par le compte SMTP du réglage « Envoi des
    /// codes » (réglage incomplet ou illisible : « échec »), SMS du mot de passe si possible ; résultats notés au journal des accès.
    /// </summary>
    public static async Task<IReadOnlyList<CompteTransmis>> TransmettreAsync(HttpContext ctx, DepotEcrans depot, ChoixEnvoiCodes choix, IEnvoiSmsMotDePasse sms,
        ReglagesEcrans reglages, IReadOnlyList<CompteATransmettre> comptes, bool envoyerIdentifiant, bool remise)
    {
        var jeton = ctx.EmpreinteJeton()!;
        var ct = ctx.RequestAborted;
        Func<string, string, string, CancellationToken, Task>? courriel = null;
        if (envoyerIdentifiant && comptes.Any(c => c.Email is not null)
            && await depot.ReglageEnvoiAsync(jeton, ct) is { } reglage && choix.Smtp(reglage) is { } smtp)
        {
            var envoi = choix.Courriel(smtp);
            courriel = envoi.EnvoyerAsync;
        }
        var journal = ctx.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("Pharma.Cloud.Web.Comptes");
        // Comptes créés : les envois (délai propre par envoi) et leur trace ne dépendent plus de la requête du navigateur
        var transmis = await TransmissionComptes.TransmettreAsync(comptes, envoyerIdentifiant, courriel, sms, reglages.AdressePublique, remise,
            (compte, evenement, objet, jt) => depot.NoterEnvoiCompteAsync(jeton, compte, evenement, objet, jt), CancellationToken.None);
        var echecs = transmis.Count(t => t.Email == "echec");
        if (echecs > 0) journal.LogWarning("Identifiant par e-mail non parti pour {Echecs} compte(s) sur {Total}", echecs, transmis.Count);
        return transmis;
    }

    /// <summary>Clé du message : préfixée si elle existe, sinon statut du code (faux, expire…), sinon erreur générale de l'import.</summary>
    private static string Cle(string prefixe, string statut) =>
        Messages.Erreurs.ContainsKey(prefixe + statut) ? prefixe + statut : Messages.Erreurs.ContainsKey(statut) ? statut : "import_erreur";
}
