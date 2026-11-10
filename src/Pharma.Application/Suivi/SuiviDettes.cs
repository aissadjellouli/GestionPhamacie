using Pharma.Application.Commun;
using Pharma.Application.Securite;
using Pharma.Domain.Commun;
using Pharma.Domain.Securite;

namespace Pharma.Application.Suivi;

/// <summary>
/// Somme due, lue dans v_suivi_dettes (039). Sens : « creance » (due à la pharmacie) ou « dette » (due par elle).
/// Categorie : patient, facture_patient, facture_entreprise, facture_organisme, bordereau, grossiste, effet.
/// Depuis : date depuis laquelle la somme est due (date d'établissement d'une facture) ; Echeance : date d'échéance d'une facture client, ou date de paiement prévue d'un effet.
/// </summary>
public sealed record SommeDue(string Sens, string Categorie, long? TiersId, string Tiers, string Libelle, DateOnly Depuis, DateOnly? Echeance,
    decimal Montant, bool Rejetee);

/// <summary>Somme due avec son ancienneté. EnAlerte : facture échue ou rejetée, somme plus ancienne que le délai réglé, ou effet à payer sous peu.</summary>
public sealed record LigneSuivi(SommeDue Somme, int Jours, string Tranche, bool EnAlerte);

/// <summary>Alerte par catégorie de tiers : nombre de sommes concernées, total, message prêt à afficher.</summary>
public sealed record AlerteDette(string Sens, string Categorie, int Nombre, decimal Montant, string Message);

public sealed record TotalTranche(string Sens, string Tranche, int Nombre, decimal Montant);

public sealed record SuiviDettes(int DelaiJours, DateOnly Jour, IReadOnlyList<LigneSuivi> Lignes, IReadOnlyList<TotalTranche> Tranches,
    IReadOnlyList<AlerteDette> Alertes)
{
    public decimal TotalCreances => Lignes.Where(l => l.Somme.Sens == "creance").Sum(l => l.Somme.Montant);
    /// <summary>Factures des grossistes à payer ; les effets déjà remis n'y sont pas (ils ont déjà diminué la dette).</summary>
    public decimal TotalDettes => Lignes.Where(l => l.Somme is { Sens: "dette", Categorie: not "effet" }).Sum(l => l.Somme.Montant);
}

public interface ISuiviDettes
{
    Task<IReadOnlyList<SommeDue>> SommesDuesAsync(CancellationToken ct = default);
    Task<int> DelaiAlerteAsync(CancellationToken ct = default);
    Task ModifierDelaiAlerteAsync(int jours, CancellationToken ct = default);
}

/// <summary>Ancienneté, tranches (échéancier) et alertes, à partir des sommes dues : calcul sans base, testable.</summary>
public static class RegleSuiviDettes
{
    /// <summary>Un effet est signalé quand son échéance arrive dans ce nombre de jours.</summary>
    public const int JoursAvantEcheance = 7;
    public static readonly string[] Tranches = ["0-30", "31-60", "61+"];

    public static bool EstFacture(string categorie) => categorie.StartsWith("facture_", StringComparison.Ordinal);

    public static string Tranche(int jours) => jours <= 30 ? Tranches[0] : jours <= 60 ? Tranches[1] : Tranches[2];

    public static SuiviDettes Construire(IEnumerable<SommeDue> sommes, int delaiJours, DateOnly aujourdhui)
    {
        var lignes = sommes.Where(s => s.Montant > 0).Select(s =>
        {
            var jours = Math.Max(aujourdhui.DayNumber - s.Depuis.DayNumber, 0);
            // Effet : à payer sous peu. Facture client : rejetée, ou échéance dépassée. Autres : plus anciennes que le délai réglé.
            var alerte = s.Categorie == "effet"
                ? s.Echeance is { } e && e.DayNumber - aujourdhui.DayNumber <= JoursAvantEcheance
                : s.Rejetee || (EstFacture(s.Categorie) && s.Echeance is { } due ? due < aujourdhui : jours > delaiJours);
            return new LigneSuivi(s, jours, Tranche(jours), alerte);
        }).OrderByDescending(l => l.EnAlerte).ThenByDescending(l => l.Jours).ThenBy(l => l.Somme.Tiers, StringComparer.CurrentCulture).ToList();

        var tranches = lignes.Where(l => l.Somme.Categorie != "effet")
            .GroupBy(l => (l.Somme.Sens, l.Tranche))
            .Select(g => new TotalTranche(g.Key.Sens, g.Key.Tranche, g.Count(), g.Sum(l => l.Somme.Montant)))
            .OrderBy(t => t.Sens).ThenBy(t => Array.IndexOf(Tranches, t.Tranche)).ToList();

        var alertes = lignes.Where(l => l.EnAlerte).GroupBy(l => (l.Somme.Sens, l.Somme.Categorie))
            .Select(g => new AlerteDette(g.Key.Sens, g.Key.Categorie, g.Count(), g.Sum(l => l.Somme.Montant),
                Message(g.Key.Categorie, g.Count(), g.Select(l => l.Somme.Tiers).Distinct().Count(), g.Sum(l => l.Somme.Montant), delaiJours,
                    g.Count(l => l.Somme.Rejetee))))
            .OrderBy(a => a.Sens).ThenByDescending(a => a.Montant).ToList();
        return new SuiviDettes(delaiJours, aujourdhui, lignes, tranches, alertes);
    }

    private static string Message(string categorie, int nombre, int tiers, decimal montant, int delai, int rejetees)
    {
        var da = montant.ToString("N2", System.Globalization.CultureInfo.GetCultureInfo("fr-FR")) + " DA";
        var rejet = rejetees > 0 ? $", dont {rejetees} rejetée(s)" : "";
        return categorie switch
        {
            "patient" => $"{tiers} patient(s) ont une dette de plus de {delai} jours : {da} à recouvrer.",
            "facture_patient" => $"{nombre} facture(s) de patients échue(s){rejet} : {da} à encaisser.",
            "facture_entreprise" => $"{nombre} facture(s) d'entreprises échue(s){rejet} : {da} à encaisser.",
            "facture_organisme" => $"{nombre} facture(s) d'organismes échue(s){rejet} : {da} à encaisser.",
            "bordereau" => $"{nombre} bordereau(x) d'organismes envoyés depuis plus de {delai} jours ou rejetés en partie : {da} à recevoir.",
            "grossiste" => $"{nombre} facture(s) de {tiers} grossiste(s) à payer depuis plus de {delai} jours : {da}.",
            "effet" => $"{nombre} effet(s) à payer dans les {JoursAvantEcheance} jours : {da} à prévoir en banque.",
            _ => $"{nombre} somme(s) due(s) : {da}."
        };
    }
}

/// <summary>Dettes et créances de tous les tiers, avec échéancier et alertes (droit « tresorerie.gerer »).</summary>
public sealed class ObtenirSuiviDettes(ISuiviDettes suivi, IPermissionService droits, IHorloge horloge)
{
    public async Task<SuiviDettes> ExecuterAsync(long utilisateurId, CancellationToken ct = default)
    {
        await droits.ExigerAsync(utilisateurId, Permissions.TresorerieGerer, "consulter les dettes et créances", ct);
        return await CalculerAsync(ct);
    }

    /// <summary>Sans contrôle de droit : pour le tableau de bord du titulaire, qui a déjà vérifié le sien.</summary>
    public async Task<SuiviDettes> CalculerAsync(CancellationToken ct = default) =>
        RegleSuiviDettes.Construire(await suivi.SommesDuesAsync(ct), await suivi.DelaiAlerteAsync(ct), horloge.Aujourdhui);
}

/// <summary>Le titulaire règle l'ancienneté à partir de laquelle une dette ou une créance est signalée ; inscrit au journal.</summary>
public sealed class ModifierDelaiAlerteDettes(ISuiviDettes suivi, IPermissionService droits, IJournal journal)
{
    public async Task ExecuterAsync(int jours, long utilisateurId, CancellationToken ct = default)
    {
        await droits.ExigerTitulaireAsync(utilisateurId, "régler le délai d'alerte des dettes", ct);
        if (jours is < 1 or > 365) throw new RegleMetierException("Délai d'alerte entre 1 et 365 jours.", "delai_alerte");
        var avant = await suivi.DelaiAlerteAsync(ct);
        await suivi.ModifierDelaiAlerteAsync(jours, ct);
        await journal.EcrireAsync(utilisateurId, "delai_alerte_dettes", "parametre_caisse", 1,
            new Dictionary<string, object?> { ["avant"] = avant, ["apres"] = jours }, Permissions.AdminParametres, ct);
    }
}
