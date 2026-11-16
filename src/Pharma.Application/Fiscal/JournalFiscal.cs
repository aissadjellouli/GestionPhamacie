using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Pharma.Application.Caisse;
using Pharma.Application.Commun;
using Pharma.Application.Integrite;
using Pharma.Application.Securite;
using Pharma.Domain.Commun;

namespace Pharma.Application.Fiscal;

// ---- Journal fiscal des ventes (lot 3, M6b) ----
// La base écrit une ligne par événement fiscal (script 112 : déclencheurs différés, même transaction que l'opération) dans
// journal_vente, chaîné par la brique d'intégrité ; l'application scelle le journal à chaque clôture de caisse et à chaque fin de
// mois, imprime le sceau sur le rapport, archive les rapports, contrôle la continuité des n° de tickets et produit le récapitulatif TVA.

public static class JournalFiscal
{
    public const string Journal = "journal_vente";
    /// <summary>Clé de chaîne sous laquelle le contrôle de continuité est enregistré dans verification_integrite.</summary>
    public const string CleNumerotation = "numerotation";
    public const string TypeJournaliere = "journaliere";
    public const string TypeMensuelle = "mensuelle";
}

public sealed record LigneJournalFiscal(long Rang, long Id, string Evenement, string? NumeroTicket, DateTime SurvenuLe, string? Poste,
    string? Vendeur, decimal Ttc, decimal Ht, decimal Tva, decimal PartPatient, decimal PartOrganisme, string Paiements,
    decimal MontantMouvement, string? Motif, string Hash);

/// <summary>Une ligne du récapitulatif TVA : taux 0 = ventes exonérées (médicaments), sinon taxées [À VÉRIFIER art. 51 bis].</summary>
public sealed record LigneRecapTva(decimal Taux, decimal Ttc, decimal Ht, decimal Tva, long Evenements)
{
    public bool Exoneree => Taux == 0;
}

public sealed record AnomalieNumerotation(string Anomalie, string? Numero, long? VenteId)
{
    public string Libelle => Anomalie switch
    {
        "numero_manquant" => $"N° {Numero} manquant",
        "numero_hors_compteur" => $"N° {Numero} au-delà du compteur",
        "vente_sans_ligne" => $"Vente {Numero} sans ligne au journal",
        "annulation_sans_ligne" => $"Annulation de {Numero} sans ligne au journal",
        _ => $"{Anomalie} {Numero}"
    };
}

public sealed record TrouHistorique(long Du, long Au, long Manquants);

public sealed record TotauxFiscaux(long Evenements, decimal Ttc, decimal Ht, decimal Tva);

public sealed record ClotureFiscale(long Id, string Type, long? SessionCaisseId, DateOnly Du, DateOnly Au, long SceauId, DateTime ScelleLeUtc,
    long DernierRang, string Algorithme, string EmpreinteCle, byte[] Sceau, long Evenements, decimal Ttc, decimal Ht, decimal Tva,
    string? FichierArchive);

public sealed record NouvelleClotureFiscale(string Type, long? SessionCaisseId, DateOnly Du, DateOnly Au, long SceauId, TotauxFiscaux Totaux,
    string? FichierArchive, byte[]? EmpreinteArchive);

/// <summary>Paramètres [À VÉRIFIER] : conservation (6 ans au moins), format d'export DGI, séparateur CSV, fuseau des périodes.</summary>
public sealed record ParametresFiscaux(int ConservationAnnees, string FormatExport, string SeparateurCsv, string Fuseau);

public sealed record CompteurTickets(string Prefixe, long DernierNumero, long PremierNumeroContinu, DateTime MisEnServiceLe);

/// <summary>Accès aux tables et fonctions du script 112.</summary>
public interface IJournalFiscalDepot
{
    Task<Page<LigneJournalFiscal>> RechercherAsync(DateOnly du, DateOnly au, string? ticket, DemandePage page, CancellationToken ct = default);
    Task<IReadOnlyList<LigneJournalFiscal>> LignesAsync(DateOnly du, DateOnly au, CancellationToken ct = default);
    Task<IReadOnlyList<LigneRecapTva>> RecapTvaAsync(DateOnly du, DateOnly au, CancellationToken ct = default);
    Task<TotauxFiscaux> TotauxAsync(DateOnly du, DateOnly au, CancellationToken ct = default);
    Task<IReadOnlyList<AnomalieNumerotation>> ContinuiteAsync(CancellationToken ct = default);
    Task<IReadOnlyList<TrouHistorique>> TrousHistoriqueAsync(CancellationToken ct = default);
    Task<IReadOnlyList<ClotureFiscale>> CloturesAsync(DateOnly du, DateOnly au, CancellationToken ct = default);
    Task<ClotureFiscale?> ClotureSessionAsync(long sessionCaisseId, CancellationToken ct = default);
    Task<bool> MoisClotureAsync(DateOnly premierJour, CancellationToken ct = default);
    /// <summary>Sessions clôturées depuis la mise en service sans clôture fiscale (rattrapage de la tâche planifiée).</summary>
    Task<IReadOnlyList<long>> SessionsNonScelleesAsync(CancellationToken ct = default);
    Task<long> EnregistrerClotureAsync(NouvelleClotureFiscale cloture, CancellationToken ct = default);
    Task<ParametresFiscaux> ParametresAsync(CancellationToken ct = default);
    Task<CompteurTickets> CompteurAsync(CancellationToken ct = default);
}

/// <summary>Archivage local des rapports scellés (dossier du PC serveur) : rend le chemin relatif du fichier rangé.</summary>
public interface IArchivesFiscales
{
    Task<string> RangerAsync(string sousDossier, string nomFichier, byte[] contenu, CancellationToken ct = default);
}

/// <summary>Rapport fiscal d'une période (rapport mensuel scellé, export d'audit).</summary>
public sealed record RapportFiscal(string Titre, DateOnly Du, DateOnly Au, TotauxFiscaux Totaux, IReadOnlyList<LigneRecapTva> Tva,
    IReadOnlyList<ClotureFiscale> Sceaux, string FormatSceau, IReadOnlyList<AnomalieNumerotation> Anomalies, string? SceauDuRapport = null);

public interface IDocumentsFiscaux
{
    byte[] RapportFiscal(RapportFiscal rapport, string? pharmacie);
}

/// <summary>Texte du sceau imprimé sur un rapport (format d'export du sceau : parametre_integrite.format_export_sceau).</summary>
public static class TexteSceau
{
    public static string Pour(long sceauId, string algorithme, long dernierRang, string empreinteCle, byte[] sceau, string format) =>
        $"Sceau n° {sceauId} · {algorithme} · journal des ventes jusqu’à la ligne {dernierRang} · clé {empreinteCle} · {MessageSceau.Exporter(sceau, format)}";

    public static string Pour(ClotureFiscale c, string format) => Pour(c.SceauId, c.Algorithme, c.DernierRang, c.EmpreinteCle, c.Sceau, format);
}

/// <summary>
/// Clôture journalière scellée : à la clôture (ou relève) d'une caisse, le journal des ventes est scellé, le rapport de clôture
/// (sceau imprimé) est archivé et la clôture fiscale enregistrée. Une session déjà scellée n'est pas rescellée.
/// </summary>
public sealed class ScellerClotureCaisse(ScellerJournal sceller, IJournalFiscalDepot depot, ISessionsCaisse sessions, IDocumentsPdf pdf,
    IArchivesFiscales archives, IIntegriteDepot integrite, Administration.IPharmacie pharmacie, IHorloge horloge, ITraceErreurs? trace = null)
{
    public async Task<ClotureFiscale?> ExecuterAsync(long sessionId, long? auteurId, CancellationToken ct = default)
    {
        if (await depot.ClotureSessionAsync(sessionId, ct) is { } deja) return deja;
        var detail = await sessions.DetailAsync(sessionId, ct) ?? throw new RegleMetierException("Session de caisse introuvable.");
        if (detail.FermeeLe is null) throw new RegleMetierException("La session n’est pas encore clôturée.");
        var du = DateOnly.FromDateTime(detail.OuverteLe.ToLocalTime());
        var au = horloge.Aujourdhui < du ? du : horloge.Aujourdhui;
        var sceau = await sceller.ExecuterAsync(JournalFiscal.Journal, du, au, MotifSceau.ClotureJournaliere, auteurId, ct: ct);
        var format = (await integrite.ParametresAsync(ct)).FormatExportSceau;
        var texte = TexteSceau.Pour(sceau.Id, sceau.Algorithme, sceau.DernierRang, sceau.EmpreinteCle, sceau.Sceau, format);

        string? fichier = null;
        byte[]? empreinte = null;
        try
        {
            var vendeurs = await sessions.VendeursAsync(sessionId, ct);
            var contenu = pdf.RapportCloture(detail with { Vendeurs = vendeurs.Select(v => v.Nom).ToList(), SceauFiscal = texte,
                LignesTiroir = await sessions.LignesTiroirAsync(sessionId, ct) }, true, (await sessions.ParametresAsync(ct)).SeuilEcart,
                (await pharmacie.LireAsync(ct)).Nom);
            fichier = await archives.RangerAsync($"{au:yyyy}/{au:MM}",
                $"{(detail.Releve ? "releve" : "cloture")}_{au:yyyyMMdd}_{sessionId}.pdf", contenu, ct);
            empreinte = SHA256.HashData(contenu);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            // Le sceau est enregistré : un archivage impossible ne l'annule pas (journal du serveur)
            trace?.Erreur($"Archivage du rapport de clôture scellé de la session {sessionId} impossible", e);
        }
        var totaux = await depot.TotauxAsync(du, au, ct);
        await depot.EnregistrerClotureAsync(new NouvelleClotureFiscale(JournalFiscal.TypeJournaliere, sessionId, du, au, sceau.Id, totaux,
            fichier, empreinte), ct);
        return await depot.ClotureSessionAsync(sessionId, ct);
    }
}

/// <summary>Clôture mensuelle scellée : sceau du journal, rapport mensuel (récapitulatif TVA, sceaux du mois, sceau du mois) archivé.</summary>
public sealed class ScellerMois(ScellerJournal sceller, IJournalFiscalDepot depot, IDocumentsFiscaux documents, IArchivesFiscales archives,
    IIntegriteDepot integrite, Administration.IPharmacie pharmacie, ITraceErreurs? trace = null)
{
    public async Task<bool> ExecuterAsync(DateOnly mois, CancellationToken ct = default)
    {
        var du = new DateOnly(mois.Year, mois.Month, 1);
        var au = du.AddMonths(1).AddDays(-1);
        if (await depot.MoisClotureAsync(du, ct)) return false;
        var sceau = await sceller.ExecuterAsync(JournalFiscal.Journal, du, au, MotifSceau.ClotureMensuelle, null, ct: ct);
        var format = (await integrite.ParametresAsync(ct)).FormatExportSceau;
        var totaux = await depot.TotauxAsync(du, au, ct);
        string? fichier = null;
        byte[]? empreinte = null;
        try
        {
            var rapport = new RapportFiscal($"Clôture mensuelle des ventes · {du.ToString("MMMM yyyy", CultureInfo.GetCultureInfo("fr-FR"))}",
                du, au, totaux, await depot.RecapTvaAsync(du, au, ct), await depot.CloturesAsync(du, au, ct), format,
                await depot.ContinuiteAsync(ct), TexteSceau.Pour(sceau.Id, sceau.Algorithme, sceau.DernierRang, sceau.EmpreinteCle, sceau.Sceau, format));
            var contenu = documents.RapportFiscal(rapport, (await pharmacie.LireAsync(ct)).Nom);
            fichier = await archives.RangerAsync($"{du:yyyy}/{du:MM}", $"cloture_mensuelle_{du:yyyyMM}.pdf", contenu, ct);
            empreinte = SHA256.HashData(contenu);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            trace?.Erreur($"Archivage du rapport mensuel scellé {du:yyyy-MM} impossible", e);
        }
        await depot.EnregistrerClotureAsync(new NouvelleClotureFiscale(JournalFiscal.TypeMensuelle, null, du, au, sceau.Id, totaux, fichier, empreinte), ct);
        return true;
    }
}

/// <summary>
/// Tâche planifiée : scelle chaque mois échu depuis la mise en service, et rattrape les clôtures de caisse non scellées (sceau en
/// échec au moment de la clôture).
/// </summary>
public sealed class ScellerPeriodesEchues(IJournalFiscalDepot depot, ScellerMois mois, ScellerClotureCaisse cloture, IHorloge horloge,
    ITraceErreurs? trace = null)
{
    public async Task<int> ExecuterAsync(CancellationToken ct = default)
    {
        var faits = 0;
        foreach (var session in await depot.SessionsNonScelleesAsync(ct))
            try
            {
                if (await cloture.ExecuterAsync(session, null, ct) is not null) faits++;
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                trace?.Erreur($"Sceau de la clôture de caisse {session} impossible", e);
            }
        var service = DateOnly.FromDateTime((await depot.CompteurAsync(ct)).MisEnServiceLe.ToLocalTime());
        var aujourdhui = horloge.Aujourdhui;
        for (var m = new DateOnly(service.Year, service.Month, 1); m < new DateOnly(aujourdhui.Year, aujourdhui.Month, 1); m = m.AddMonths(1))
            if (await mois.ExecuterAsync(m, ct)) faits++;
        return faits;
    }
}

/// <summary>
/// Contrôle de continuité des n° de tickets (vérification nocturne et « Vérifier maintenant ») : enregistré dans
/// verification_integrite (journal_vente, clé « numerotation ») ; une anomalie est signalée au titulaire.
/// </summary>
public sealed class ControlerNumerotation(IJournalFiscalDepot depot, IIntegriteDepot integrite, Notifications.IAlertesTitulaire? alertes = null,
    ITraceErreurs? trace = null)
{
    public async Task<IReadOnlyList<AnomalieNumerotation>> ExecuterAsync(long? declencheePar, CancellationToken ct = default)
    {
        var anomalies = await depot.ContinuiteAsync(ct);
        var premiere = anomalies.FirstOrDefault();
        await integrite.EnregistrerVerificationAsync(new NouvelleVerification(JournalFiscal.Journal, JournalFiscal.CleNumerotation, anomalies.Count, 0,
            premiere is null ? VerifierIntegrite.Ok : VerifierIntegrite.Rupture, null,
            premiere is null ? null : $"{anomalies.Count} anomalie(s) de numérotation, dont : {premiere.Libelle}", null, premiere?.Numero, declencheePar), ct);
        if (premiere is not null && alertes is not null)
            try
            {
                await alertes.SurveillanceAsync($"Journal des ventes : {anomalies.Count} anomalie(s) de numérotation des tickets ({premiere.Libelle}). Ne rien corriger, prévenir l’éditeur.",
                    urgent: true, ct);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                trace?.Erreur("Signalement d'une anomalie de numérotation en échec", e);
            }
        return anomalies;
    }
}

/// <summary>Écran Administration › Contrôle interne › Journal fiscal (titulaire seul).</summary>
public sealed record TableauJournalFiscal(Page<LigneJournalFiscal> Lignes, IReadOnlyList<LigneRecapTva> Tva, TotauxFiscaux Totaux,
    IReadOnlyList<ClotureFiscale> Clotures, IReadOnlyList<AnomalieNumerotation> Anomalies, IReadOnlyList<TrouHistorique> TrousHistorique,
    CompteurTickets Compteur, ParametresFiscaux Parametres, string FormatSceau, DerniereVerification? DerniereVerification);

public static class PeriodeFiscale
{
    /// <summary>Période vérifiée : du ≤ au, au plus 366 jours (export et recherche).</summary>
    public static (DateOnly Du, DateOnly Au) Lire(DateOnly? du, DateOnly? au, DateOnly aujourdhui)
    {
        var a = au ?? aujourdhui;
        var d = du ?? new DateOnly(a.Year, a.Month, 1);
        if (a < d) throw new RegleMetierException("La période est inversée.", "periode");
        if (a.DayNumber - d.DayNumber > 366) throw new RegleMetierException("Période limitée à un an.", "periode");
        return (d, a);
    }
}

public sealed class ObtenirJournalFiscal(IJournalFiscalDepot depot, IIntegriteDepot integrite, IPermissionService droits, IHorloge horloge)
{
    public async Task<TableauJournalFiscal> ExecuterAsync(long utilisateurId, DateOnly? du, DateOnly? au, string? ticket, DemandePage page,
        CancellationToken ct = default)
    {
        await droits.ExigerTitulaireAsync(utilisateurId, "consulter le journal fiscal des ventes", ct);
        var (d, a) = PeriodeFiscale.Lire(du, au, horloge.Aujourdhui);
        var verif = (await integrite.DernieresVerificationsAsync(ct))
            .Where(v => v.Journal == JournalFiscal.Journal && v.CleChaine != JournalFiscal.CleNumerotation).OrderByDescending(v => v.VerifieLe).FirstOrDefault();
        return new TableauJournalFiscal(
            await depot.RechercherAsync(d, a, string.IsNullOrWhiteSpace(ticket) ? null : ticket.Trim(), page, ct),
            await depot.RecapTvaAsync(d, a, ct), await depot.TotauxAsync(d, a, ct), await depot.CloturesAsync(d, a, ct),
            await depot.ContinuiteAsync(ct), await depot.TrousHistoriqueAsync(ct), await depot.CompteurAsync(ct), await depot.ParametresAsync(ct),
            (await integrite.ParametresAsync(ct)).FormatExportSceau, verif);
    }
}

/// <summary>Export d'audit d'une période (CSV au format paramétré, ou PDF) avec les sceaux de la période ; inscrit au journal des actions.</summary>
public sealed class ExporterJournalFiscal(IJournalFiscalDepot depot, IIntegriteDepot integrite, IDocumentsFiscaux documents,
    Administration.IPharmacie pharmacie, IPermissionService droits, IHorloge horloge, IJournal? journal = null)
{
    public async Task<(byte[] Contenu, string NomFichier, string Type)> ExecuterAsync(long utilisateurId, DateOnly? du, DateOnly? au, string? format,
        CancellationToken ct = default)
    {
        await droits.ExigerTitulaireAsync(utilisateurId, "exporter le journal fiscal des ventes", ct);
        var (d, a) = PeriodeFiscale.Lire(du, au, horloge.Aujourdhui);
        var parametres = await depot.ParametresAsync(ct);
        var formatSceau = (await integrite.ParametresAsync(ct)).FormatExportSceau;
        var clotures = await depot.CloturesAsync(d, a, ct);
        var tva = await depot.RecapTvaAsync(d, a, ct);
        var nom = $"journal_fiscal_{d:yyyyMMdd}_{a:yyyyMMdd}";
        (byte[], string, string) resultat = format switch
        {
            "pdf" => (documents.RapportFiscal(new RapportFiscal("Export d’audit · journal des ventes", d, a, await depot.TotauxAsync(d, a, ct), tva, clotures,
                formatSceau, await depot.ContinuiteAsync(ct)), (await pharmacie.LireAsync(ct)).Nom), nom + ".pdf", "application/pdf"),
            "csv" or null or "" => (ExportFiscalCsv.Construire(parametres, d, a, await depot.LignesAsync(d, a, ct), tva, clotures, formatSceau),
                nom + ".csv", "text/csv"),
            _ => throw new RegleMetierException("Format d’export : csv ou pdf.")
        };
        if (journal is not null)
            await journal.EcrireAsync(utilisateurId, "export_journal_fiscal", JournalFiscal.Journal, null,
                new Dictionary<string, object?> { ["du"] = d, ["au"] = a, ["format"] = format ?? "csv" }, null, ct);
        return resultat;
    }
}

/// <summary>
/// Export CSV « csv-v1 » [À VÉRIFIER : format DGI] : UTF-8 avec BOM, séparateur paramétré, trois sections précédées d'une ligne
/// « #SECTION » : LIGNES (une par événement, avec rang et empreinte), TVA (récapitulatif par taux), SCEAUX (clôtures de la période).
/// Montants avec un point décimal, dates en UTC ISO 8601.
/// </summary>
public static class ExportFiscalCsv
{
    public static byte[] Construire(ParametresFiscaux p, DateOnly du, DateOnly au, IReadOnlyList<LigneJournalFiscal> lignes,
        IReadOnlyList<LigneRecapTva> tva, IReadOnlyList<ClotureFiscale> clotures, string formatSceau)
    {
        var s = p.SeparateurCsv;
        var sb = new StringBuilder();
        void L(params object?[] champs) => sb.Append(string.Join(s, champs.Select(c => Champ(c, s)))).Append("\r\n");
        L("#FORMAT", p.FormatExport, "periode", du.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), au.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        L("#SECTION", "LIGNES");
        L("rang", "id", "evenement", "ticket", "date_utc", "poste", "vendeur", "ttc", "ht", "tva", "part_patient", "part_organisme", "paiements",
            "montant_mouvement", "motif", "empreinte");
        foreach (var l in lignes)
            L(l.Rang, l.Id, l.Evenement, l.NumeroTicket, l.SurvenuLe.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture),
                l.Poste, l.Vendeur, l.Ttc, l.Ht, l.Tva, l.PartPatient, l.PartOrganisme, l.Paiements, l.MontantMouvement, l.Motif, l.Hash);
        L("#SECTION", "TVA");
        L("taux", "regime", "ttc", "ht", "tva", "evenements");
        foreach (var t in tva) L(t.Taux, t.Exoneree ? "exonere" : "taxe", t.Ttc, t.Ht, t.Tva, t.Evenements);
        L("#SECTION", "SCEAUX");
        L("sceau_id", "type", "session", "du", "au", "scelle_le_utc", "dernier_rang", "algorithme", "cle", "sceau");
        foreach (var c in clotures)
            L(c.SceauId, c.Type, c.SessionCaisseId, c.Du.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), c.Au.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                c.ScelleLeUtc.ToString("yyyy-MM-dd'T'HH:mm:ss.ffffff'Z'", CultureInfo.InvariantCulture), c.DernierRang, c.Algorithme, c.EmpreinteCle,
                MessageSceau.Exporter(c.Sceau, formatSceau));
        return [.. Encoding.UTF8.GetPreamble(), .. Encoding.UTF8.GetBytes(sb.ToString())];
    }

    private static string Champ(object? v, string separateur)
    {
        var t = v switch
        {
            null => "",
            decimal d => d.ToString("0.00", CultureInfo.InvariantCulture),
            IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
            _ => v.ToString() ?? ""
        };
        // Neutralise une formule de tableur et protège séparateur, guillemets et retours à la ligne
        if (t.Length > 0 && "=+-@".Contains(t[0]) && v is string) t = "'" + t;
        return t.Contains(separateur) || t.Contains('"') || t.Contains('\n') || t.Contains('\r') ? "\"" + t.Replace("\"", "\"\"") + "\"" : t;
    }
}
