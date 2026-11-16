using Pharma.Application.Commun;
using Pharma.Application.Notifications;
using Pharma.Domain.Commun;

namespace Pharma.Application.Sauvegardes;

// ---- PC de secours : seconde copie de la dernière sauvegarde, vérifiée, et test de restauration (2026-11-16) ----------
// Les copies restent dans l'officine (autre disque, clé USB, partage réseau du PC de secours) et ne sont pas chiffrées.

/// <summary>
/// Dossier : second emplacement de la copie (null : pas de copie de secours). AlerteHeures : le titulaire est prévenu quand la dernière
/// copie réussie a plus de ce nombre d'heures. RegleLeUtc : date du réglage du dossier (point de départ du retard avant la 1re copie).
/// </summary>
public sealed record ReglagesCopieSecours(string? Dossier = null, int AlerteHeures = ReglagesCopieSecours.AlerteParDefaut, DateTime? RegleLeUtc = null)
{
    public const int AlerteParDefaut = 26;   // sauvegarde quotidienne, plus deux heures de marge
}

/// <summary>Dernière copie réussie, dernier échec, dernière alerte de retard et dernier test de restauration.</summary>
public sealed record EtatCopieSecours(
    DateTime? DerniereCopieUtc = null, string? DerniereCopie = null, long? Taille = null, string? Sha256 = null,
    DateTime? DernierEchecUtc = null, string? DernierEchec = null, DateTime? DerniereAlerteRetardUtc = null,
    DateTime? DernierTestUtc = null, bool? DernierTestReussi = null, string? DernierTestMessage = null);

/// <summary>Réglages et état de la copie de secours, gardés sur le PC serveur (hors de la base : lisibles même base perdue).</summary>
public interface ICopieSecoursDepot
{
    ReglagesCopieSecours Reglages();
    void EnregistrerReglages(ReglagesCopieSecours reglages);
    EtatCopieSecours Etat();
    void EnregistrerEtat(EtatCopieSecours etat);
}

/// <summary>Fichiers du second emplacement.</summary>
public interface IFichiersCopieSecours
{
    /// <summary>Faux si le dossier (clé USB, disque, partage réseau) est absent ou inaccessible ; le crée sinon.</summary>
    bool Accessible(string dossier);
    /// <summary>Copie complète ou rien (fichier temporaire « .partiel » renommé à la fin).</summary>
    void Copier(string source, string destination);
    long Taille(string fichier);
    /// <summary>Empreinte SHA-256 du fichier, en hexadécimal minuscule.</summary>
    string Sha256(string fichier);
    /// <summary>Empreinte notée à côté de la copie (fichier « .sha256 »), ou null.</summary>
    string? EmpreinteNotee(string copie);
    void NoterEmpreinte(string copie, string sha256);
    /// <summary>Copies pharmacie_*.dump du dossier, de la plus récente à la plus ancienne.</summary>
    IReadOnlyList<string> Copies(string dossier);
    bool Existe(string fichier);
    /// <summary>Supprime la copie et son empreinte notée.</summary>
    void Supprimer(string copie);
}

/// <summary>Restauration d'essai dans une base temporaire, supprimée ensuite ; la base en service n'est jamais touchée.</summary>
public interface ITestRestauration
{
    /// <summary>Renvoie le résumé du contenu restauré ; RegleMetierException si la sauvegarde ne se restaure pas.</summary>
    Task<string> TesterAsync(string fichier, CancellationToken ct = default);
}

/// <summary>
/// Après chaque sauvegarde réussie : copie du fichier vers le second emplacement, vérifiée (taille et empreinte SHA-256 identiques),
/// empreinte notée à côté (fichier .sha256, contrôlé par scripts/basculer-pc-secours.ps1), copies au-delà de la conservation
/// supprimées. Échec : noté, titulaire prévenu ; la sauvegarde principale reste réussie.
/// </summary>
public sealed class CopierSauvegardeSecours(ICopieSecoursDepot depot, IFichiersCopieSecours fichiers, IAlertesTitulaire alertes, IHorloge horloge,
    ITraceErreurs? trace = null)
{
    /// <summary>Texte ajouté au message du journal des sauvegardes ; null si aucun second emplacement n'est réglé.</summary>
    public async Task<string?> ExecuterAsync(string sauvegarde, int conservation, CancellationToken ct = default)
    {
        var r = depot.Reglages();
        if (string.IsNullOrWhiteSpace(r.Dossier)) return null;
        var maintenant = horloge.MaintenantUtc;
        string cible, empreinte;
        long taille;
        try
        {
            if (!fichiers.Accessible(r.Dossier))
                throw new IOException($"le second emplacement {r.Dossier} est absent ou inaccessible (clé USB débranchée, PC de secours éteint ?)");
            cible = Path.Combine(r.Dossier, Path.GetFileName(sauvegarde));
            (empreinte, taille) = (fichiers.Sha256(sauvegarde), fichiers.Taille(sauvegarde));
            fichiers.Copier(sauvegarde, cible);
            if (fichiers.Taille(cible) != taille || !string.Equals(fichiers.Sha256(cible), empreinte, StringComparison.OrdinalIgnoreCase))
            {
                try { fichiers.Supprimer(cible); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
                throw new IOException("la copie diffère de la sauvegarde (taille ou empreinte SHA-256) : elle a été supprimée");
            }
            fichiers.NoterEmpreinte(cible, empreinte);
            foreach (var ancienne in fichiers.Copies(r.Dossier).Skip(Math.Max(1, conservation)))
                try { fichiers.Supprimer(ancienne); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            var message = $"Copie de secours impossible : {ex.Message}.";
            Noter(e => e with { DernierEchecUtc = maintenant, DernierEchec = ex.Message });
            await AlerteNonEnvoyeeSignalee.SansBlocageAsync(() => alertes.SauvegardeEnEchecAsync(false, message, ct), "copie de secours en échec", trace);
            return " " + message;
        }
        Noter(e => e with { DerniereCopieUtc = maintenant, DerniereCopie = cible, Taille = taille, Sha256 = empreinte, DerniereAlerteRetardUtc = null });
        return $" Copie de secours vérifiée (taille et SHA-256 {empreinte[..Math.Min(12, empreinte.Length)]}…) : {cible}.";
    }

    // L'état sert à l'écran et à l'alerte de retard : un échec d'écriture ne doit pas masquer la copie
    private void Noter(Func<EtatCopieSecours, EtatCopieSecours> changer)
    {
        try { depot.EnregistrerEtat(changer(depot.Etat())); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { trace?.Erreur("Copie de secours : état non enregistré", e); }
    }
}

/// <summary>
/// Vérifiée chaque minute par l'API : si la dernière copie réussie (ou, avant la première, le réglage du dossier) a plus de
/// AlerteHeures heures, le titulaire est prévenu ; au plus une alerte par période de AlerteHeures heures.
/// </summary>
public sealed class SurveillerCopieSecours(ICopieSecoursDepot depot, IAlertesTitulaire alertes, IHorloge horloge, ITraceErreurs? trace = null)
{
    /// <summary>Vrai si une alerte de retard a été envoyée.</summary>
    public async Task<bool> ExecuterAsync(CancellationToken ct = default)
    {
        var r = depot.Reglages();
        if (string.IsNullOrWhiteSpace(r.Dossier)) return false;
        var e = depot.Etat();
        if ((e.DerniereCopieUtc ?? r.RegleLeUtc) is not { } reference) return false;
        var maintenant = horloge.MaintenantUtc;
        var periode = TimeSpan.FromHours(r.AlerteHeures);
        if (maintenant - reference < periode) return false;
        if (e.DerniereAlerteRetardUtc is { } a && maintenant - a < periode) return false;
        depot.EnregistrerEtat(e with { DerniereAlerteRetardUtc = maintenant });
        var detail = e.DerniereCopieUtc is { } d
            ? $"La dernière copie de secours date du {d.ToLocalTime():dd/MM/yyyy à HH:mm} (plus de {r.AlerteHeures} h)."
            : $"Aucune copie de secours depuis le réglage du second emplacement (plus de {r.AlerteHeures} h).";
        detail += $" Vérifiez le second emplacement ({r.Dossier}) et la sauvegarde quotidienne.";
        await AlerteNonEnvoyeeSignalee.SansBlocageAsync(() => alertes.SauvegardeEnEchecAsync(false, detail, ct), "copie de secours en retard", trace);
        return true;
    }
}

/// <summary>Réglage du second emplacement (Administration › Sauvegardes › Copie de secours).</summary>
public sealed class ModifierReglagesCopieSecours(ICopieSecoursDepot depot, IJournal journal, IHorloge horloge)
{
    public async Task ExecuterAsync(string? dossier, int alerteHeures, long utilisateurId, CancellationToken ct = default)
    {
        var d = string.IsNullOrWhiteSpace(dossier) ? null : dossier.Trim();
        if (d is not null && !Path.IsPathFullyQualified(d))
            throw new RegleMetierException("Indiquez un chemin complet, par exemple F:\\CopieSecours ou \\\\PC-SECOURS\\Sauvegardes.", "dossier_secours");
        if (alerteHeures is < 1 or > 720) throw new RegleMetierException("Alerte de retard : entre 1 et 720 heures.", "alerte_heures");
        var avant = depot.Reglages();
        if (d is not null && avant.Dossier is { } principal && string.Equals(principal.TrimEnd('\\'), d.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
            throw new RegleMetierException("Le second emplacement doit être différent.", "dossier_secours");
        var regleLe = string.Equals(avant.Dossier, d, StringComparison.OrdinalIgnoreCase) ? avant.RegleLeUtc : (d is null ? null : horloge.MaintenantUtc);
        depot.EnregistrerReglages(new ReglagesCopieSecours(d, alerteHeures, regleLe));
        await journal.EcrireAsync(utilisateurId, "reglages_copie_secours", null, null,
            new Dictionary<string, object?> { ["dossier"] = d, ["alerte_heures"] = alerteHeures }, Domain.Securite.Permissions.AdminSauvegardes, ct);
    }
}

public sealed record ResultatTestRestauration(bool Reussi, string? Fichier, string Message);

/// <summary>
/// « Tester la restauration de la dernière copie » (titulaire) : empreinte SHA-256 contrôlée, restauration dans une base temporaire,
/// vérification du contenu, base supprimée ; résultat gardé (écran) et inscrit au journal des opérations.
/// </summary>
public sealed class TesterRestaurationCopie(ICopieSecoursDepot depot, IFichiersCopieSecours fichiers, ITestRestauration test, IJournal journal,
    IHorloge horloge)
{
    public async Task<ResultatTestRestauration> ExecuterAsync(long utilisateurId, CancellationToken ct = default)
    {
        var r = depot.Reglages();
        var e = depot.Etat();
        string? fichier = e.DerniereCopie is { } c && fichiers.Existe(c) ? c
            : !string.IsNullOrWhiteSpace(r.Dossier) && fichiers.Accessible(r.Dossier) ? fichiers.Copies(r.Dossier).FirstOrDefault() : null;
        if (fichier is null)
            throw new RegleMetierException("Aucune copie de secours à tester : réglez le second emplacement et faites une sauvegarde.");

        ResultatTestRestauration resultat;
        try
        {
            var notee = fichiers.EmpreinteNotee(fichier);
            if (notee is not null && !string.Equals(notee, fichiers.Sha256(fichier), StringComparison.OrdinalIgnoreCase))
                throw new RegleMetierException("La copie est abîmée : son empreinte SHA-256 ne correspond plus à celle notée lors de la copie.");
            var resume = await test.TesterAsync(fichier, ct);
            resultat = new ResultatTestRestauration(true, fichier,
                $"Restauration réussie dans une base temporaire, supprimée ensuite{(notee is null ? "" : " ; empreinte SHA-256 vérifiée")}. {resume}");
        }
        catch (Exception ex) when (ex is RegleMetierException or IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            resultat = new ResultatTestRestauration(false, fichier, $"Test de restauration échoué : {ex.Message}");
        }

        try { depot.EnregistrerEtat(depot.Etat() with { DernierTestUtc = horloge.MaintenantUtc, DernierTestReussi = resultat.Reussi, DernierTestMessage = resultat.Message }); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        await journal.EcrireAsync(utilisateurId, "test_restauration_copie_secours", null, null,
            new Dictionary<string, object?> { ["fichier"] = Path.GetFileName(fichier), ["reussi"] = resultat.Reussi, ["message"] = resultat.Message },
            Domain.Securite.Permissions.AdminSauvegardes, ct);
        return resultat;
    }
}
