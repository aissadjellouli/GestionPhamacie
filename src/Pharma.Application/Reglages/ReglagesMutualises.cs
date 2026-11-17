using System.Globalization;
using System.Text.Json;
using Pharma.Application.Alertes;
using Pharma.Application.Commun;
using Pharma.Application.Notifications;
using Pharma.Application.Securite;
using Pharma.Domain.Commun;
using Pharma.Domain.Reglages;
using Pharma.Domain.Securite;

namespace Pharma.Application.Reglages;

// ---- Mutualisation des réglages, lot M4 : application des modèles du serveur central à l'officine (docs/conception-synchronisation.md § 16 bis.4 à 16 bis.7) ----
// Le modèle reçu (format mutu-v1) est rangé par la voie descendante (script 141) ; ce cas d'usage le valide (règles du domaine, puis contrôles qui
// demandent la base), puis l'applique dans UNE transaction par table (marqueur pharma.reglage_central posé par SET LOCAL). Tout ou rien par table (D9).

/// <summary>Une table de réglages devenue imposée par le serveur central (pour le message au titulaire).</summary>
/// <param name="ValeurRemplacee">La valeur de l'officine était différente : elle a été remplacée (l'ancienne est au journal).</param>
/// <param name="RemisesLocalesArretees">Remises locales en cours ou à venir arrêtées (jamais supprimées, D6).</param>
public sealed record ReglageImpose(string Table, string Libelle, bool ValeurRemplacee, int RemisesLocalesArretees);

/// <summary>État d'une table dans reglage_mutualise (script 141). Mode = mode en vigueur ; ModeRecu et VersionRecue = dernier modèle reçu.</summary>
public sealed record EtatReglage(string Table, string Mode, bool AjoutsLocaux, string? ModeRecu, long? VersionRecue, string? ModeleJson,
    long? VersionAppliquee, DateTime? AppliqueLe, string Etat, string? MotifRejet, long? VersionRejetee, string Origine, DateTime? ModifieLocalementLe, DateTime? RecuLe = null);

/// <summary>Nouvel état d'une table après examen d'un modèle. Les valeurs vides (null) laissent la colonne inchangée, sauf Etat et MotifRejet.</summary>
public sealed record MiseAJourEtat(string? Mode, bool? AjoutsLocaux, string Etat, long? VersionAppliquee, string? MotifRejet, long? VersionRejetee,
    string? Origine, bool Applique);

/// <summary>Effet d'une écriture : remises locales arrêtées (D6) et leur description pour le journal.</summary>
public sealed record ResultatEcriture(int RemisesLocalesArretees, string? RemisesArreteesJson);

/// <summary>Transaction d'application d'UNE table : verrou de sa ligne d'état, marqueur de session, lecture et écriture de la valeur, journal.</summary>
public interface ITransactionReglage : IAsyncDisposable
{
    /// <summary>État relu sous verrou (SELECT … FOR UPDATE).</summary>
    Task<EtatReglage> EtatAsync(CancellationToken ct = default);
    /// <summary>Valeur actuelle de l'officine, au format de la valeur du modèle (motifs : seulement ceux du modèle ; remises : celles du serveur central).</summary>
    Task<string> LireValeurAsync(string valeurModeleJson, CancellationToken ct = default);
    /// <summary>Contrôles qui demandent la base : catégorie existante, libellé déjà pris par un motif local, motif système, chevauchement avec une remise locale.</summary>
    Task<IReadOnlyList<string>> ControlerAsync(string valeurModeleJson, bool impose, CancellationToken ct = default);
    /// <param name="identique">La valeur de l'officine est déjà celle du modèle (rien à réécrire dans une table à ligne unique).</param>
    Task<ResultatEcriture> EcrireAsync(string valeurModeleJson, bool impose, bool identique, CancellationToken ct = default);
    Task EnregistrerEtatAsync(MiseAJourEtat etat, CancellationToken ct = default);
    Task JournaliserAsync(string action, string modeAvant, string modeApres, long? version, string? auteur, string? valeurAvantJson, string? valeurApresJson,
        long? utilisateurId, CancellationToken ct = default);
    Task ValiderAsync(CancellationToken ct = default);
}

public interface IReglagesMutualisesDepot
{
    /// <summary>Synchronisation active, officine inscrite, base en écriture : sinon rien n'est appliqué (les modèles restent rangés).</summary>
    Task<bool> ApplicationPossibleAsync(CancellationToken ct = default);
    /// <summary>Mode en vigueur de la table (reglage_mode_effectif : « local » tant que la synchronisation est désactivée) et ajouts locaux permis.</summary>
    Task<(string Mode, bool AjoutsLocaux)> ModeEffectifAsync(string table, CancellationToken ct = default);
    Task<IReadOnlyList<EtatReglage>> EtatsAsync(CancellationToken ct = default);
    Task<ITransactionReglage> OuvrirAsync(string table, CancellationToken ct = default);
    /// <summary>Ligne du journal hors application d'un modèle (désactivation de la synchronisation).</summary>
    Task JournaliserAsync(string action, string table, string modeAvant, string modeApres, long? version, string? auteur, long? utilisateurId,
        CancellationToken ct = default);
}

/// <summary>Libellés des 11 tables pour les messages (sans donnée personnelle).</summary>
public static class LibellesReglages
{
    public static string Texte(string table) => table switch
    {
        TablesMutualisables.Fiscal => "Conservation fiscale",
        TablesMutualisables.AlertesPsychotropes => "Alertes des psychotropes",
        TablesMutualisables.Tracabilite => "Traçabilité des boîtes",
        TablesMutualisables.Serialisation => "Sérialisation",
        TablesMutualisables.Substitution => "Substitution",
        TablesMutualisables.Motifs => "Motifs et catégories",
        TablesMutualisables.FormulaireSms => "Formulaire d’accord SMS",
        TablesMutualisables.Surveillance => "Contrôle interne",
        TablesMutualisables.Alertes => "Seuils d’alerte de péremption et de stock",
        TablesMutualisables.Inventaire => "Seuils d’inventaire",
        TablesMutualisables.Remises => "Remises automatiques",
        _ => table
    };
}

/// <summary>Refus d'écrire dans une table imposée par le serveur central (409, champ « reglage_impose »).</summary>
public sealed class ReglageImposeException(string table)
    : Exception("Ce réglage est fixé par le serveur central.")
{
    public const string Champ = "reglage_impose";
    public string Table { get; } = table;
}

/// <summary>Le modèle du serveur central a changé (ou n'est plus à adopter) entre l'affichage et la demande (409).</summary>
public sealed class ModeleReglageChangeException(string message) : Exception(message);

/// <summary>Contrôle avant d'écrire, appelé par chacun des cas d'usage de saisie locale des 11 tables.</summary>
public interface IGardeReglagesCentraux
{
    /// <summary>Refus si la table est imposée (mode en vigueur).</summary>
    Task ExigerModifiableAsync(string table, CancellationToken ct = default);
    /// <summary>Ajout local (motif, remise) : refusé si la table est imposée et que le modèle n'accorde pas les ajouts locaux (remises : jamais en mode imposé, D6).</summary>
    Task ExigerAjoutLocalAsync(string table, CancellationToken ct = default);
    /// <summary>Modification d'un élément venu du modèle (motif ou remise du serveur central) : refusée si la table est imposée.</summary>
    Task ExigerElementLocalAsync(string table, bool venuDuCentral, CancellationToken ct = default);
}

public sealed class GardeReglagesCentraux(IReglagesMutualisesDepot depot) : IGardeReglagesCentraux
{
    public async Task ExigerModifiableAsync(string table, CancellationToken ct = default)
    {
        if ((await depot.ModeEffectifAsync(table, ct)).Mode == ValidationModele.ModeImpose) throw new ReglageImposeException(table);
    }

    public async Task ExigerAjoutLocalAsync(string table, CancellationToken ct = default)
    {
        var (mode, ajouts) = await depot.ModeEffectifAsync(table, ct);
        if (mode == ValidationModele.ModeImpose && (table == TablesMutualisables.Remises || !ajouts)) throw new ReglageImposeException(table);
    }

    public async Task ExigerElementLocalAsync(string table, bool venuDuCentral, CancellationToken ct = default)
    {
        if (venuDuCentral) await ExigerModifiableAsync(table, ct);
    }
}

/// <summary>Écriture canonique d'un JSON pour comparer deux valeurs : clés triées, listes triées, nombres sans zéro final.</summary>
public static class JsonCanonique
{
    public static string Normaliser(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return Ecrire(doc.RootElement);
    }

    private static string Ecrire(JsonElement e) => e.ValueKind switch
    {
        JsonValueKind.Object => "{" + string.Join(",", e.EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal)
            .Select(p => JsonSerializer.Serialize(p.Name) + ":" + Ecrire(p.Value))) + "}",
        JsonValueKind.Array => "[" + string.Join(",", e.EnumerateArray().Select(Ecrire).Order(StringComparer.Ordinal)) + "]",
        JsonValueKind.String => JsonSerializer.Serialize(e.GetString()),
        JsonValueKind.Number => e.TryGetDecimal(out var d) ? (d / 1.0000000000000000000000000000m).ToString(CultureInfo.InvariantCulture) : e.GetRawText(),
        JsonValueKind.True => "true",
        JsonValueKind.False => "false",
        _ => "null"
    };
}

/// <summary>Résultat d'un passage (tables par issue).</summary>
public sealed record BilanReglages(IReadOnlyList<string> Appliquees, IReadOnlyList<string> Rejetees, IReadOnlyList<string> NonAdoptees,
    IReadOnlyList<string> Liberees, IReadOnlyList<ReglageImpose> Imposees)
{
    public static readonly BilanReglages Aucun = new([], [], [], [], []);
}

/// <summary>
/// Applique les modèles reçus du serveur central (lot M4). Appelé après chaque échange descendant et au démarrage. Pour chaque table dont la version
/// reçue n'est pas appliquée (et pas déjà rejetée), ou imposée et modifiée localement depuis : validation (domaine, puis base), puis, dans UNE transaction :
/// <list type="bullet">
/// <item>imposée : la valeur de l'officine est remplacée (l'ancienne au journal) et le titulaire est prévenu (une fois par passage) ;</item>
/// <item>proposée : une valeur jamais modifiée ici (depuis l'installation) suit le modèle, dès la première réception ; une valeur modifiée est gardée (« non adopté », D2) ;</item>
/// <item>mode local : libération (D3), rien n'est effacé ;</item>
/// <item>valeur invalide : la table entière est rejetée (D9), avec son motif, sans message ; la valeur et le mode précédents restent.</item>
/// </list>
/// </summary>
public sealed class AppliquerReglagesMutualises(IReglagesMutualisesDepot depot, IMoteurAlertes? alertes = null, IAlertesTitulaire? titulaire = null,
    ITraceErreurs? trace = null)
{
    public async Task<BilanReglages> ExecuterAsync(CancellationToken ct = default)
    {
        if (!await depot.ApplicationPossibleAsync(ct)) return BilanReglages.Aucun;
        var etats = (await depot.EtatsAsync(ct)).ToDictionary(e => e.Table);
        List<string> appliquees = [], rejetees = [], nonAdoptees = [], liberees = [];
        List<ReglageImpose> imposees = [];
        foreach (var table in TablesMutualisables.Toutes)
        {
            if (!etats.TryGetValue(table, out var e) || !AAppliquer(e)) continue;
            try
            {
                var r = await TraiterAsync(table, adoption: false, utilisateurId: null, ct);
                switch (r.Issue)
                {
                    case Issue.Appliquee: appliquees.Add(table); break;
                    case Issue.Rejetee: rejetees.Add(table); break;
                    case Issue.NonAdoptee: nonAdoptees.Add(table); break;
                    case Issue.Liberee: liberees.Add(table); break;
                }
                if (r.Imposee is not null) imposees.Add(r.Imposee);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Une table en échec technique n'empêche pas les autres ; elle sera reprise au passage suivant
                trace?.Erreur($"Réglage mutualisé {table} : application impossible", ex);
            }
        }
        if (imposees.Count > 0 && titulaire is not null) await titulaire.ReglagesImposesAsync(imposees, ct);
        return new BilanReglages(appliquees, rejetees, nonAdoptees, liberees, imposees);
    }

    /// <summary>Table à examiner : version reçue ni appliquée ni rejetée, valeur non adoptée à réévaluer, ou imposée et modifiée localement depuis.</summary>
    public static bool AAppliquer(EtatReglage e) =>
        e is { VersionRecue: { } v, ModeRecu: not null, ModeleJson: not null }
        && e.VersionRejetee != v
        && (e.VersionAppliquee != v
            || e.Etat == EtatsReglage.NonAdopte
            // Libération ou rétablissement (D3) : le central republie la MÊME version avec un autre mode ; tant que le mode en vigueur diffère du mode reçu, il reste à appliquer
            || e.Mode != e.ModeRecu
            || (e.Mode == ValidationModele.ModeImpose && e.ModifieLocalementLe is { } m && (e.AppliqueLe is null || m > e.AppliqueLe)));

    /// <summary>
    /// Valeur modifiée sur place : origine « locale » avec une date de modification locale (posée par la garde de la base à chaque modification permise,
    /// script 142, et reprise du journal d'audit pour les modifications antérieures). Une table jamais modifiée depuis l'installation (origine « locale »
    /// sans date) ou dont la valeur vient du central (origine « central ») n'est pas modifiée : la première réception d'un modèle proposé l'applique,
    /// puis elle suit le modèle (D2).
    /// </summary>
    public static bool ModifieeLocalement(EtatReglage e) => e.Origine == OriginesReglage.Locale && e.ModifieLocalementLe is not null;

    /// <summary>« Adopter » : applique la valeur proposée à la demande du titulaire (appelé par AdopterReglageCentral).</summary>
    internal async Task AdopterAsync(string table, long versionModele, long utilisateurId, CancellationToken ct)
    {
        var r = await TraiterAsync(table, adoption: true, utilisateurId, ct, versionModele);
        if (r.Imposee is not null && titulaire is not null) await titulaire.ReglagesImposesAsync([r.Imposee], ct);
    }

    private enum Issue { Rien, Appliquee, Rejetee, NonAdoptee, Liberee }

    private sealed record Resultat(Issue Issue, ReglageImpose? Imposee = null);

    private async Task<Resultat> TraiterAsync(string table, bool adoption, long? utilisateurId, CancellationToken ct, long? versionAttendue = null)
    {
        await using var tx = await depot.OuvrirAsync(table, ct);
        var e = await tx.EtatAsync(ct);
        if (adoption)
        {
            if (e.ModeRecu != ValidationModele.ModePropose || e.ModeleJson is null || e.VersionRecue is null)
                throw new ModeleReglageChangeException("Il n’y a plus de valeur proposée par le serveur central pour ce réglage.");
            if (e.VersionRecue != versionAttendue)
                throw new ModeleReglageChangeException("Le serveur central a publié une nouvelle version de ce réglage : rechargez l’écran avant d’adopter.");
            if ((await depot.ModeEffectifAsync(table, ct)).Mode == ValidationModele.ModeLocal)
                throw new RegleMetierException("La synchronisation est désactivée : aucun réglage du serveur central n’est en vigueur.");
        }
        else if (!AAppliquer(e)) return new Resultat(Issue.Rien);

        var version = e.VersionRecue!.Value;
        var modeRecu = e.ModeRecu!;
        // Le jsonb de la base remet des espaces : retour à la forme compacte avant de contrôler la taille
        var element = Compacter(e.ModeleJson!);
        var erreurs = element is null ? ["Le modèle n’est pas un JSON valide."] : ValidationModele.ValiderElement(table, element).ToList();
        string? auteur = null, valeur = null;
        var ajouts = true;
        if (erreurs.Count == 0)
        {
            using var doc = JsonDocument.Parse(element!);
            var racine = doc.RootElement;
            if (racine.TryGetProperty("auteur", out var a)) auteur = a.GetString();
            if (racine.TryGetProperty("ajouts_locaux", out var aj)) ajouts = aj.GetBoolean();
            if (racine.TryGetProperty("valeur", out var v)) valeur = v.GetRawText();
        }
        var impose = modeRecu == ValidationModele.ModeImpose;
        if (erreurs.Count == 0 && valeur is not null) erreurs.AddRange(await tx.ControlerAsync(valeur, impose, ct));

        if (erreurs.Count > 0)
        {
            // D9 : la table entière est refusée ; la valeur en vigueur et le mode précédent restent ; pas de message au titulaire
            if (adoption) throw new RegleMetierException("Le réglage proposé n’est plus applicable : " + string.Join(" ", erreurs.Take(3)));
            await tx.EnregistrerEtatAsync(new MiseAJourEtat(null, null, EtatsReglage.Rejete, null, Resume(erreurs), version, null, false), ct);
            await tx.JournaliserAsync(ActionsReglage.Rejete, e.Mode, e.Mode, version, auteur, null,
                JsonSerializer.Serialize(new { erreurs = erreurs.Take(20).ToList() }), null, ct);
            await tx.ValiderAsync(ct);
            return new Resultat(Issue.Rejetee);
        }

        if (modeRecu == ValidationModele.ModeLocal)
        {
            // Libération (D3) : le mode redevient local ; la valeur n'est pas touchée
            await tx.EnregistrerEtatAsync(new MiseAJourEtat(ValidationModele.ModeLocal, null, EtatsReglage.Aucun, version, null, null, null, true), ct);
            if (e.Mode != ValidationModele.ModeLocal)
                await tx.JournaliserAsync(ActionsReglage.Libere, e.Mode, ValidationModele.ModeLocal, version, auteur, null, null, null, ct);
            await tx.ValiderAsync(ct);
            return new Resultat(e.Mode != ValidationModele.ModeLocal ? Issue.Liberee : Issue.Rien);
        }

        var locale = await tx.LireValeurAsync(valeur!, ct);
        var identique = JsonCanonique.Normaliser(locale) == JsonCanonique.Normaliser(valeur!);

        if (!adoption && !impose && !identique && ModifieeLocalement(e))
        {
            // D2 et première réception : valeur modifiée ici, mode proposé : gardée ; le modèle reste à adopter
            if (e.Etat != EtatsReglage.NonAdopte || e.Mode != ValidationModele.ModePropose || e.AjoutsLocaux != ajouts)
                await tx.EnregistrerEtatAsync(new MiseAJourEtat(ValidationModele.ModePropose, ajouts, EtatsReglage.NonAdopte, null, null, null, null, false), ct);
            await tx.ValiderAsync(ct);
            return new Resultat(Issue.NonAdoptee);
        }

        var ecriture = await tx.EcrireAsync(valeur!, impose, identique, ct);
        var rienDeNouveau = identique && ecriture.RemisesLocalesArretees == 0 && e.VersionAppliquee == version && e.Mode == modeRecu && e.Etat == EtatsReglage.Applique;
        await tx.EnregistrerEtatAsync(new MiseAJourEtat(modeRecu, ajouts, EtatsReglage.Applique, version, null, null, OriginesReglage.Central, true), ct);
        if (!rienDeNouveau)
        {
            var action = adoption ? ActionsReglage.Adopte : !identique && impose ? ActionsReglage.Remplace : ActionsReglage.Applique;
            await tx.JournaliserAsync(action, e.Mode, modeRecu, version, auteur, locale, valeur, adoption ? utilisateurId : null, ct);
        }
        if (ecriture.RemisesArreteesJson is not null)
            await tx.JournaliserAsync(ActionsReglage.Remplace, e.Mode, modeRecu, version, auteur, ecriture.RemisesArreteesJson, null, null, ct);
        await tx.ValiderAsync(ct);

        // Effets de bord des cas d'usage de saisie locale reproduits : les seuils d'alerte recalculent les alertes
        if (table == TablesMutualisables.Alertes && !identique && alertes is not null)
        {
            try { await alertes.GenererAsync(ct); }
            catch (Exception ex) when (ex is not OperationCanceledException) { trace?.Erreur("Réglage mutualisé : recalcul des alertes impossible", ex); }
        }

        var devientImposee = impose && (e.Mode != ValidationModele.ModeImpose || !identique || ecriture.RemisesLocalesArretees > 0);
        return new Resultat(Issue.Appliquee, devientImposee
            ? new ReglageImpose(table, LibellesReglages.Texte(table), !identique, ecriture.RemisesLocalesArretees)
            : null);
    }

    private static string? Compacter(string json)
    {
        try { return System.Text.Json.Nodes.JsonNode.Parse(json)?.ToJsonString(new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }); }
        catch (JsonException) { return null; }
    }

    private static string Resume(IReadOnlyList<string> erreurs)
    {
        var texte = string.Join(" ", erreurs);
        return texte.Length <= 500 ? texte : texte[..499] + "…";
    }
}

public static class EtatsReglage
{
    public const string Aucun = "aucun";
    public const string Applique = "applique";
    public const string NonAdopte = "non_adopte";
    public const string Rejete = "rejete";
}

public static class OriginesReglage
{
    public const string Locale = "locale";
    public const string Central = "central";
}

/// <summary>Actions du journal d'audit (reglage_journaliser, script 141).</summary>
public static class ActionsReglage
{
    public const string Applique = "reglage_central_applique";
    public const string Remplace = "reglage_central_remplace";
    public const string Rejete = "reglage_central_rejete";
    public const string Adopte = "reglage_central_adopte";
    public const string Libere = "reglage_central_libere";
}

/// <summary>
/// « Adopter la valeur du serveur central » (mode proposé) : le titulaire demande d'appliquer la valeur du modèle à la place de la valeur de l'officine.
/// Droit : celui qui permet de saisir ce réglage localement (motifs, seuils, substitution, texte SMS : admin.parametres ; remises : caisse.remises_auto) ;
/// titulaire pour les autres tables. Tracé au journal avec l'employé (reglage_central_adopte), valeurs avant et après.
/// </summary>
public sealed class AdopterReglageCentral(AppliquerReglagesMutualises appliquer, IPermissionService droits)
{
    public static string? Permission(string table) => table switch
    {
        TablesMutualisables.Motifs or TablesMutualisables.Alertes or TablesMutualisables.Substitution or TablesMutualisables.FormulaireSms => Permissions.AdminParametres,
        TablesMutualisables.Remises => Permissions.CaisseRemisesAuto,
        _ => null
    };

    public async Task ExecuterAsync(string table, long versionModele, long utilisateurId, CancellationToken ct = default)
    {
        if (!TablesMutualisables.Connue(table)) throw new RegleMetierException("Réglage inconnu.");
        var quoi = $"adopter le réglage du serveur central ({LibellesReglages.Texte(table)})";
        if (Permission(table) is { } p) await droits.ExigerAsync(utilisateurId, p, quoi, ct);
        else await droits.ExigerTitulaireAsync(utilisateurId, quoi, ct);
        await appliquer.AdopterAsync(table, versionModele, utilisateurId, ct);
    }
}

/// <summary>Désactivation de la synchronisation (D3) : les tables imposées redeviennent modifiables aussitôt (mode en vigueur « local »), valeurs gardées ; une ligne au journal par table.</summary>
public sealed class LibererReglagesMutualises(IReglagesMutualisesDepot depot)
{
    public async Task ExecuterAsync(long utilisateurId, CancellationToken ct = default)
    {
        foreach (var e in await depot.EtatsAsync(ct))
            if (e.Mode != ValidationModele.ModeLocal)
                await depot.JournaliserAsync(ActionsReglage.Libere, e.Table, e.Mode, ValidationModele.ModeLocal, e.VersionAppliquee, null, utilisateurId, ct);
    }
}
