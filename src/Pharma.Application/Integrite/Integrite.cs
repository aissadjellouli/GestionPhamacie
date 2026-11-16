using System.Globalization;
using System.Text;
using Pharma.Application.Commun;
using Pharma.Application.Notifications;
using Pharma.Application.Securite;
using Pharma.Domain.Commun;

namespace Pharma.Application.Integrite;

// ---- Intégrité des journaux (lot 2, solution mixte) ----
// La base chaîne chaque ligne (script 111 : empreinte SHA-256 reliée à la précédente) ; l'application scelle l'état d'un journal
// (HMAC-SHA256 avec une clé gardée hors de la base) et vérifie chaque nuit les chaînes et les sceaux.

/// <summary>Motifs d'un sceau (contrainte ck_sceau_integrite_motif).</summary>
public static class MotifSceau
{
    public const string ClotureJournaliere = "cloture_journaliere";
    public const string ClotureMensuelle = "cloture_mensuelle";
    public const string Manuel = "manuel";
    public static readonly IReadOnlySet<string> Tous = new HashSet<string> { ClotureJournaliere, ClotureMensuelle, Manuel };
}

/// <summary>
/// Port du scellement : HMAC calculé avec une clé secrète que la base ne voit jamais. Adaptateur local : fichier de clé protégé par
/// DPAPI machine (C:\ProgramData\PharmaOfficine) ; version cloud : adaptateur à venir (coffre de clés de l'hébergeur).
/// </summary>
public interface IScelleurIntegrite
{
    /// <summary>Algorithme du sceau (« HMAC-SHA256 »).</summary>
    string Algorithme { get; }
    /// <summary>Identifiant de la clé (début de son empreinte SHA-256, jamais la clé) : un changement de clé se voit sur le sceau.</summary>
    string EmpreinteCle { get; }
    byte[] Sceller(byte[] message);
    /// <summary>Comparaison en temps constant.</summary>
    bool Verifier(byte[] message, byte[] sceau);
}

/// <summary>
/// Contrôle complémentaire d'un journal chaîné, lancé par la vérification après la chaîne et les sceaux (ex. lot 8 : le registre des
/// psychotropes concorde avec son journal chaîné). Motif null : rien à signaler.
/// </summary>
public interface IControleJournal
{
    string JournalControle { get; }
    Task<(string? Motif, long? Rang, string? Attendu, string? Trouve)> ControlerAsync(CancellationToken ct = default);
}

/// <summary>Dernière ligne d'une chaîne (rang 0 et empreinte d'origine si le journal est vide).</summary>
public sealed record MaillonChaine(long Rang, long? Id, byte[] Hash);

/// <summary>Résultat de integrite_verifier pour une clé de chaîne : rupture vide si la chaîne est intacte.</summary>
public sealed record ResultatChaine(string CleChaine, long Lignes, long DernierRang, byte[]? DernierHash,
    long? RangRupture, long? IdRupture, string? Motif, string? Attendu, string? Trouve)
{
    public bool Intacte => RangRupture is null;
}

public sealed record JournalChaine(string Journal, string Libelle, string Algorithme, DateTime ActiveLe);

public sealed record SceauEnregistre(long Id, string Journal, string CleChaine, string Motif, DateOnly PeriodeDu, DateOnly PeriodeAu,
    DateTime ScelleLeUtc, long DernierRang, long? DernierId, byte[] DernierHash, string Algorithme, string EmpreinteCle, byte[] Sceau, long? AuteurId);

public sealed record NouveauSceau(string Journal, string CleChaine, string Motif, DateOnly PeriodeDu, DateOnly PeriodeAu, DateTime ScelleLeUtc,
    long DernierRang, long? DernierId, byte[] DernierHash, string Algorithme, string EmpreinteCle, byte[] Sceau, long? AuteurId);

public sealed record NouvelleVerification(string Journal, string? CleChaine, long Lignes, int Sceaux, string Statut, long? RangRupture,
    string? Motif, string? Attendu, string? Trouve, long? DeclencheePar);

public sealed record DerniereVerification(string Journal, string? CleChaine, DateTime VerifieLe, long Lignes, int Sceaux, string Statut,
    long? RangRupture, string? Motif, string? Attendu, string? Trouve, long? DeclencheePar);

public sealed record ParametresIntegrite(string AlgorithmeDefaut, int HeureVerification, string FormatExportSceau);

/// <summary>Accès aux tables et fonctions du script 111.</summary>
public interface IIntegriteDepot
{
    Task<IReadOnlyList<JournalChaine>> JournauxAsync(CancellationToken ct = default);
    Task<IReadOnlyList<ResultatChaine>> VerifierChaineAsync(string journal, CancellationToken ct = default);
    Task<MaillonChaine> DernierMaillonAsync(string journal, string cleChaine, CancellationToken ct = default);
    /// <summary>Empreinte de la ligne de ce rang, ou null si elle n'existe pas.</summary>
    Task<byte[]?> HashAuRangAsync(string journal, string cleChaine, long rang, CancellationToken ct = default);
    Task<long> EnregistrerSceauAsync(NouveauSceau sceau, CancellationToken ct = default);
    Task<IReadOnlyList<SceauEnregistre>> SceauxAsync(string journal, CancellationToken ct = default);
    Task EnregistrerVerificationAsync(NouvelleVerification verification, CancellationToken ct = default);
    /// <summary>Dernière vérification de chaque journal et clé de chaîne.</summary>
    Task<IReadOnlyList<DerniereVerification>> DernieresVerificationsAsync(CancellationToken ct = default);
    Task<ParametresIntegrite> ParametresAsync(CancellationToken ct = default);
    /// <summary>Rupture constatée par la tâche planifiée : inscrite au journal des actions sans employé (système).</summary>
    Task JournaliserRuptureSystemeAsync(string journal, IReadOnlyDictionary<string, object?> details, CancellationToken ct = default);
}

/// <summary>
/// Message scellé (format v1, docs/base-de-donnees.md) : champs séparés par « | », dans cet ordre :
/// sceau-v1, journal, clé de chaîne, motif, période du, période au (AAAA-MM-JJ), date du sceau (UTC, AAAA-MM-JJTHH:MM:SS.ffffffZ),
/// dernier rang, dernier id (vide si journal vide), dernière empreinte (hexadécimal minuscule). Encodé en UTF-8.
/// </summary>
public static class MessageSceau
{
    public static byte[] Canonique(string journal, string cleChaine, string motif, DateOnly du, DateOnly au, DateTime scelleLeUtc,
        long dernierRang, long? dernierId, byte[] dernierHash) =>
        Encoding.UTF8.GetBytes(string.Join('|',
            "sceau-v1", journal, cleChaine, motif,
            du.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), au.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            HorodatageUtc(scelleLeUtc),
            dernierRang.ToString(CultureInfo.InvariantCulture), dernierId?.ToString(CultureInfo.InvariantCulture) ?? "",
            Convert.ToHexStringLower(dernierHash)));

    public static byte[] Canonique(SceauEnregistre s) =>
        Canonique(s.Journal, s.CleChaine, s.Motif, s.PeriodeDu, s.PeriodeAu, s.ScelleLeUtc, s.DernierRang, s.DernierId, s.DernierHash);

    /// <summary>Horodatage à la microseconde (précision de PostgreSQL) : le sceau relu de la base se recalcule à l'identique.</summary>
    public static DateTime TronquerMicroseconde(DateTime utc) => new(utc.Ticks - utc.Ticks % 10, DateTimeKind.Utc);

    private static string HorodatageUtc(DateTime d) =>
        TronquerMicroseconde(d.Kind == DateTimeKind.Utc ? d : d.ToUniversalTime()).ToString("yyyy-MM-dd'T'HH:mm:ss.ffffff'Z'", CultureInfo.InvariantCulture);

    /// <summary>Sceau exporté (hexadécimal ou base64 selon parametre_integrite.format_export_sceau).</summary>
    public static string Exporter(byte[] sceau, string format) => format == "base64" ? Convert.ToBase64String(sceau) : Convert.ToHexStringLower(sceau);
}

/// <summary>
/// Scelle l'état d'un journal chaîné à un instant (API interne ; branchée sur les clôtures au lot 3, le sceau s'affichera sur le
/// rapport de clôture). Le sceau couvre la dernière ligne (rang, id, empreinte) : toute ligne antérieure modifiée ou supprimée casse
/// la chaîne, toute ligne finale supprimée ne correspond plus au sceau.
/// </summary>
public sealed class ScellerJournal(IIntegriteDepot depot, IScelleurIntegrite scelleur, IHorloge horloge)
{
    public const string CleOfficine = "officine";

    public async Task<SceauEnregistre> ExecuterAsync(string journal, DateOnly du, DateOnly au, string motif, long? auteurId,
        string cleChaine = CleOfficine, CancellationToken ct = default)
    {
        if (!MotifSceau.Tous.Contains(motif)) throw new RegleMetierException("Motif de sceau inconnu.", "motif");
        if (au < du) throw new RegleMetierException("La période est inversée.", "periode");
        if (!(await depot.JournauxAsync(ct)).Any(j => j.Journal == journal))
            throw new RegleMetierException($"Le journal « {journal} » n'est pas chaîné.");
        var dernier = await depot.DernierMaillonAsync(journal, cleChaine, ct);
        var le = MessageSceau.TronquerMicroseconde(horloge.MaintenantUtc);
        var sceau = scelleur.Sceller(MessageSceau.Canonique(journal, cleChaine, motif, du, au, le, dernier.Rang, dernier.Id, dernier.Hash));
        var nouveau = new NouveauSceau(journal, cleChaine, motif, du, au, le, dernier.Rang, dernier.Id, dernier.Hash, scelleur.Algorithme,
            scelleur.EmpreinteCle, sceau, auteurId);
        var id = await depot.EnregistrerSceauAsync(nouveau, ct);
        return new SceauEnregistre(id, journal, cleChaine, motif, du, au, le, dernier.Rang, dernier.Id, dernier.Hash, scelleur.Algorithme,
            scelleur.EmpreinteCle, sceau, auteurId);
    }
}

/// <summary>État d'une chaîne après vérification (une ligne par journal et clé de chaîne).</summary>
public sealed record EtatChaine(string Journal, string Libelle, string? CleChaine, long Lignes, int Sceaux, string Statut,
    long? RangRupture, string? Motif, string? Attendu, string? Trouve);

/// <summary>
/// Vérifie chaque journal chaîné et ses sceaux (tâche nocturne, ou « Vérifier maintenant » du titulaire) ; chaque résultat est
/// enregistré (verification_integrite). En cas de rupture : alerte au titulaire (SMS ou e-mail, mécanisme des notifications) et
/// inscription au journal des actions. Ne lève pas pour un journal en erreur : il est marqué « erreur » et les autres continuent.
/// </summary>
public sealed class VerifierIntegrite(IIntegriteDepot depot, IScelleurIntegrite scelleur, IAlertesTitulaire? alertes = null,
    IJournal? journalActions = null, ITraceErreurs? trace = null, IEnumerable<IControleJournal>? controles = null)
{
    public const string Ok = "ok";
    public const string Rupture = "rupture";
    public const string Erreur = "erreur";

    /// <param name="declencheePar">Titulaire qui a demandé la vérification ; null : tâche planifiée.</param>
    public async Task<IReadOnlyList<EtatChaine>> ExecuterAsync(long? declencheePar, CancellationToken ct = default)
    {
        var etats = new List<EtatChaine>();
        foreach (var j in await depot.JournauxAsync(ct))
        {
            IReadOnlyList<EtatChaine> resultats;
            try
            {
                resultats = await VerifierJournalAsync(j, ct);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                trace?.Erreur($"Vérification d'intégrité du journal {j.Journal} en échec", e);
                resultats = [new EtatChaine(j.Journal, j.Libelle, null, 0, 0, Erreur, null, "vérification impossible", null, null)];
            }
            foreach (var r in resultats)
            {
                await depot.EnregistrerVerificationAsync(new NouvelleVerification(r.Journal, r.CleChaine, r.Lignes, r.Sceaux, r.Statut,
                    r.RangRupture, r.Motif, r.Attendu, r.Trouve, declencheePar), ct);
                if (r.Statut != Ok) await SignalerAsync(r, declencheePar, ct);
            }
            etats.AddRange(resultats);
        }
        return etats;
    }

    private async Task<IReadOnlyList<EtatChaine>> VerifierJournalAsync(JournalChaine j, CancellationToken ct)
    {
        var chaines = await depot.VerifierChaineAsync(j.Journal, ct);
        var sceaux = await depot.SceauxAsync(j.Journal, ct);
        var cles = chaines.Select(c => c.CleChaine).Union(sceaux.Select(s => s.CleChaine)).OrderBy(c => c, StringComparer.Ordinal).ToList();
        if (cles.Count == 0) return [new EtatChaine(j.Journal, j.Libelle, ScellerJournal.CleOfficine, 0, 0, Ok, null, null, null, null)];

        var etats = new List<EtatChaine>();
        foreach (var cle in cles)
        {
            var c = chaines.FirstOrDefault(x => x.CleChaine == cle);
            var sceauxCle = sceaux.Where(s => s.CleChaine == cle).OrderBy(s => s.DernierRang).ToList();
            var lignes = c?.Lignes ?? 0;
            if (c is { Intacte: false })
            {
                etats.Add(new EtatChaine(j.Journal, j.Libelle, cle, lignes, sceauxCle.Count, Rupture, c.RangRupture, c.Motif, c.Attendu, c.Trouve));
                continue;
            }
            etats.Add(await VerifierSceauxAsync(j, cle, lignes, c?.DernierRang ?? 0, sceauxCle, ct)
                      ?? await ControlesAsync(j, cle, lignes, sceauxCle.Count, ct)
                      ?? new EtatChaine(j.Journal, j.Libelle, cle, lignes, sceauxCle.Count, Ok, null, null, null, null));
        }
        return etats;
    }

    /// <summary>Contrôles complémentaires du journal (lot 8 : concordance du registre des psychotropes avec son journal chaîné).</summary>
    private async Task<EtatChaine?> ControlesAsync(JournalChaine j, string cle, long lignes, int sceaux, CancellationToken ct)
    {
        if (controles is null || cle != ScellerJournal.CleOfficine) return null;
        foreach (var c in controles.Where(c => c.JournalControle == j.Journal))
        {
            var (motif, rang, attendu, trouve) = await c.ControlerAsync(ct);
            if (motif is not null) return new EtatChaine(j.Journal, j.Libelle, cle, lignes, sceaux, Rupture, rang, motif, attendu, trouve);
        }
        return null;
    }

    /// <summary>Premier sceau invalide (HMAC faux, ligne scellée disparue ou modifiée), ou null si tous sont bons.</summary>
    private async Task<EtatChaine?> VerifierSceauxAsync(JournalChaine j, string cle, long lignes, long dernierRang,
        List<SceauEnregistre> sceaux, CancellationToken ct)
    {
        foreach (var s in sceaux)
        {
            EtatChaine Rompu(string motif, string? attendu, string? trouve) =>
                new(j.Journal, j.Libelle, cle, lignes, sceaux.Count, Rupture, s.DernierRang, motif, attendu, trouve);

            if (s.EmpreinteCle != scelleur.EmpreinteCle)
                return Rompu($"sceau n° {s.Id} fait avec une autre clé de scellement", scelleur.EmpreinteCle, s.EmpreinteCle);
            if (!scelleur.Verifier(MessageSceau.Canonique(s), s.Sceau))
                return Rompu($"sceau n° {s.Id} invalide (sceau ou données du sceau modifiés)", null, Convert.ToHexStringLower(s.Sceau));
            if (s.DernierRang == 0) continue;   // journal vide au moment du sceau
            if (s.DernierRang > dernierRang)
                return Rompu($"sceau n° {s.Id} : dernière ligne scellée absente (lignes supprimées en fin de journal)",
                    "rang " + s.DernierRang, "rang " + dernierRang);
            var hash = await depot.HashAuRangAsync(j.Journal, cle, s.DernierRang, ct);
            if (hash is null || !hash.AsSpan().SequenceEqual(s.DernierHash))
                return Rompu($"sceau n° {s.Id} : la ligne scellée ne correspond plus", Convert.ToHexStringLower(s.DernierHash),
                    hash is null ? "absente" : Convert.ToHexStringLower(hash));
        }
        return null;
    }

    private async Task SignalerAsync(EtatChaine r, long? declencheePar, CancellationToken ct)
    {
        var details = new Dictionary<string, object?>
        {
            ["journal"] = r.Journal, ["cle_chaine"] = r.CleChaine, ["statut"] = r.Statut, ["rang"] = r.RangRupture, ["motif"] = r.Motif
        };
        try
        {
            if (declencheePar is { } u && journalActions is not null)
                await journalActions.EcrireAsync(u, "integrite_rupture", r.Journal, r.RangRupture, details, null, ct);
            else
                await depot.JournaliserRuptureSystemeAsync(r.Journal, details, ct);
            if (alertes is not null)
                await alertes.SurveillanceAsync(r.Statut == Rupture
                    ? $"Intégrité : le journal « {r.Libelle} » est rompu (ligne {r.RangRupture}, {r.Motif}). Ne rien corriger, prévenir l'éditeur."
                    : $"Intégrité : la vérification du journal « {r.Libelle} » a échoué.", urgent: true, ct);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            trace?.Erreur("Signalement d'une rupture d'intégrité en échec", e);
        }
    }
}

/// <summary>Écran Administration › Contrôle interne › Intégrité des journaux (titulaire seul).</summary>
public sealed record TableauIntegrite(IReadOnlyList<JournalChaine> Journaux, IReadOnlyList<DerniereVerification> Verifications,
    ParametresIntegrite Parametres, string AlgorithmeSceau, string EmpreinteCle);

public sealed class ObtenirIntegrite(IIntegriteDepot depot, IScelleurIntegrite scelleur, IPermissionService droits)
{
    public async Task<TableauIntegrite> ExecuterAsync(long utilisateurId, CancellationToken ct = default)
    {
        await droits.ExigerTitulaireAsync(utilisateurId, "consulter l'intégrité des journaux", ct);
        return new TableauIntegrite(await depot.JournauxAsync(ct), await depot.DernieresVerificationsAsync(ct), await depot.ParametresAsync(ct),
            scelleur.Algorithme, scelleur.EmpreinteCle);
    }
}

public sealed class VerifierIntegriteMaintenant(VerifierIntegrite verifier, IPermissionService droits)
{
    public async Task<IReadOnlyList<EtatChaine>> ExecuterAsync(long utilisateurId, CancellationToken ct = default)
    {
        await droits.ExigerTitulaireAsync(utilisateurId, "vérifier l'intégrité des journaux", ct);
        return await verifier.ExecuterAsync(utilisateurId, ct);
    }
}
