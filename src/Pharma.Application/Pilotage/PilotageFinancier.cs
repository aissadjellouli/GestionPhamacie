using System.Globalization;
using Pharma.Application.Administration;
using Pharma.Application.Commun;
using Pharma.Application.Psychotropes;
using Pharma.Application.Rapports;
using Pharma.Application.Securite;
using Pharma.Domain.Rapports;

namespace Pharma.Application.Pilotage;

// Tableau de bord financier du titulaire (lot 11, M5). Lecture seule : aucun chiffre fiscal n'est recalculé ici ; le chiffre
// d'affaires et la marge viennent des rapports (IRapports), la TVA par taux du journal fiscal (journal_vente_recap_tva).

/// <summary>Chiffre d'affaires TTC, nombre de ventes et marge brute d'une période.</summary>
public sealed record IndicateursVentes(DateOnly Du, DateOnly Au, decimal ChiffreAffaires, int NombreVentes, MargeBrute Marge)
{
    public decimal? PanierMoyen => CalculsPilotage.PanierMoyen(ChiffreAffaires, NombreVentes);
}

/// <summary>Valeur d'une période, de la période précédente et écart en %.</summary>
public sealed record Comparaison(decimal Actuel, decimal? Precedent)
{
    public decimal? VariationPct => Precedent is { } p ? CalculsPilotage.Variation(Actuel, p) : null;
}

/// <summary>
/// Répartition du chiffre de la période : comptant (espèces, carte, chèque, avoir), crédit patient, part Chifa (CNAS, CASNOS),
/// part MDN et autres organismes. Paiements et parts organismes des ventes validées de la période (retours non déduits).
/// </summary>
public sealed record RepartitionEncaissement(decimal Comptant, decimal Credit, decimal Chifa, decimal Mdn, decimal AutresOrganismes)
{
    public decimal Total => Comptant + Credit + Chifa + Mdn + AutresOrganismes;
}

/// <summary>Bordereau (Chifa ou MDN) qui n'est pas entièrement réglé. AgeJours : depuis le dépôt (null s'il n'est pas déposé).</summary>
public sealed record CreanceBordereau(long Id, string Numero, string Organisme, string Canal, string Statut, DateOnly? DateDepot, decimal Attendu,
    decimal Rejete, decimal Verse, decimal Reste, int? AgeJours)
{
    public string Tranche => CalculsPilotage.Tranche(AgeJours);
}

/// <summary>Montant restant dû, par tranche d'âge.</summary>
public sealed record MontantParTranche(string Tranche, int Nombre, decimal Montant);

/// <summary>
/// Créances : bordereaux non réglés (âge depuis le dépôt), part organismes des ventes pas encore portée sur un bordereau,
/// crédits patients (soldes, et restes dus des ventes à crédit par âge). Aucun nom de patient.
/// </summary>
public sealed record Creances(IReadOnlyList<CreanceBordereau> Bordereaux, decimal HorsBordereau, decimal CreditsPatients, int PatientsDebiteurs,
    IReadOnlyList<MontantParTranche> CreditsParAge)
{
    public decimal TotalBordereaux => Bordereaux.Sum(b => b.Reste);
    public IReadOnlyList<MontantParTranche> BordereauxParAge => CalculsPilotage.ParTranche(Bordereaux.Select(b => (b.AgeJours, b.Reste)));
}

/// <summary>
/// Stock au prix d'achat HT hors lots périmés et bloqués (même périmètre que le stock dormant), coût d'achat des boîtes
/// vendues sur la période, rotation et couverture calculées sur cette valeur, stock dormant, ruptures en cours.
/// ValeurExclue / LotsExclus : lots périmés ou bloqués encore en stock, affichés à part pour information (règle du 2026-11-16).
/// </summary>
public sealed record StockPilotage(decimal ValeurAchat, int Produits, decimal CoutVentesPeriode, int JoursPeriode, int SeuilDormantJours,
    int Dormants, decimal ValeurDormante, int RupturesOfficine, int RupturesGrossistes, decimal ValeurExclue = 0, int LotsExclus = 0)
{
    public decimal? RotationAnnuelle => CalculsPilotage.RotationAnnuelle(CoutVentesPeriode, JoursPeriode, ValeurAchat);
    public decimal? CouvertureJours => CalculsPilotage.CouvertureJours(CoutVentesPeriode, JoursPeriode, ValeurAchat);
}

/// <summary>Produit en stock sans vente depuis le seuil (ou jamais vendu). JoursSansVente : null s'il n'a jamais été vendu.</summary>
public sealed record ProduitDormant(long ProduitId, string Produit, string? Laboratoire, int Stock, decimal Valeur, DateTime? DerniereVente, int? JoursSansVente);

/// <summary>Réceptions d'un grossiste sur la période (montants des factures saisies).</summary>
public sealed record AchatsGrossiste(long FournisseurId, string Grossiste, int Receptions, decimal MontantHt, decimal MontantTtc);

/// <summary>Marge d'un produit ou d'un laboratoire (retours de la période déduits, comme la marge des rapports).</summary>
public sealed record MargeRegroupee(string Cle, int Quantite, decimal ChiffreAffairesHt, decimal CoutAchat)
{
    public decimal Marge => ChiffreAffairesHt - CoutAchat;
    public decimal? Taux => CalculsPilotage.TauxMarge(ChiffreAffairesHt, CoutAchat);
}

/// <summary>Rejets Chifa / MDN d'un motif, sur les ventes portées sur un bordereau déposé pendant la période.</summary>
public sealed record RejetsMotif(string Code, string Libelle, int Nombre, decimal Montant);

public sealed record RejetsChifa(int VentesDeposees, decimal MontantDepose, IReadOnlyList<RejetsMotif> ParMotif)
{
    public int Rejets => ParMotif.Sum(m => m.Nombre);
    public decimal MontantRejete => ParMotif.Sum(m => m.Montant);
    public decimal? TauxRejet => CalculsPilotage.Taux(Rejets, VentesDeposees);
    public decimal? TauxMotif(RejetsMotif m) => CalculsPilotage.Taux(m.Nombre, VentesDeposees);
}

/// <summary>Récapitulatif TVA par taux lu dans le journal fiscal des ventes (journal_vente_recap_tva), jamais recalculé.</summary>
public sealed record TvaParTaux(decimal Taux, decimal Ttc, decimal Ht, decimal Tva, long Evenements);

public sealed record TableauFinancier(PeriodeRapport Periode, IndicateursVentes Actuel, IndicateursVentes? Precedent, IReadOnlyList<VentesJour> ParJour,
    RepartitionEncaissement Repartition, Creances Creances, StockPilotage Stock, IReadOnlyList<AchatsGrossiste> Achats, RejetsChifa Rejets,
    IReadOnlyList<TvaParTaux> Tva)
{
    public Comparaison ChiffreAffaires => new(Actuel.ChiffreAffaires, Precedent?.ChiffreAffaires);
    public Comparaison MargeBrute => new(Actuel.Marge.Montant, Precedent?.Marge.Montant);
    public Comparaison NombreVentes => new(Actuel.NombreVentes, Precedent?.NombreVentes);
    public Comparaison? PanierMoyen => Actuel.PanierMoyen is { } p ? new(p, Precedent?.PanierMoyen) : null;
}

/// <summary>Calculs du tableau de bord (purs, testés).</summary>
public static class CalculsPilotage
{
    public static readonly string[] Tranches = ["0 à 30 jours", "31 à 60 jours", "61 à 90 jours", "plus de 90 jours", "non déposé"];

    public static decimal? PanierMoyen(decimal chiffreAffaires, int ventes) => ventes <= 0 ? null : Math.Round(chiffreAffaires / ventes, 2);

    /// <summary>Taux de marge sur le chiffre d'affaires HT, en % à une décimale (même règle que la marge des rapports).</summary>
    public static decimal? TauxMarge(decimal chiffreAffairesHt, decimal cout) =>
        chiffreAffairesHt == 0 ? null : Math.Round((chiffreAffairesHt - cout) / chiffreAffairesHt * 100, 1);

    /// <summary>Écart en % par rapport à la période précédente ; null si la période précédente est à zéro.</summary>
    public static decimal? Variation(decimal actuel, decimal precedent) =>
        precedent == 0 ? null : Math.Round((actuel - precedent) / Math.Abs(precedent) * 100, 1);

    public static decimal? Taux(int nombre, int sur) => sur <= 0 ? null : Math.Round((decimal)nombre / sur * 100, 1);

    /// <summary>
    /// Période de comparaison : jour → la veille ; semaine → la semaine d'avant (mêmes jours) ; mois → le mois d'avant
    /// (mêmes jours du mois, ramenés à sa fin) ; dates choisies → autant de jours juste avant.
    /// </summary>
    public static (DateOnly Du, DateOnly Au) PeriodePrecedente(TypePeriode type, DateOnly du, DateOnly au)
    {
        switch (type)
        {
            case TypePeriode.Jour: return (du.AddDays(-1), au.AddDays(-1));
            case TypePeriode.Semaine: return (du.AddDays(-7), au.AddDays(-7));
            case TypePeriode.Mois:
                var d = du.AddMonths(-1);
                var finMois = au.Day == DateTime.DaysInMonth(au.Year, au.Month);
                var a = finMois ? new DateOnly(d.Year, d.Month, DateTime.DaysInMonth(d.Year, d.Month)) : au.AddMonths(-1);
                return (d, a);
            default:
                var n = au.DayNumber - du.DayNumber + 1;
                return (du.AddDays(-n), du.AddDays(-1));
        }
    }

    /// <summary>Période arrêtée à aujourd'hui : une période en cours se compare à la même durée écoulée.</summary>
    public static DateOnly AuEffectif(DateOnly au, DateOnly aujourdhui) => au > aujourdhui ? aujourdhui : au;

    public static int AgeJours(DateOnly depuis, DateOnly aujourdhui) => Math.Max(0, aujourdhui.DayNumber - depuis.DayNumber);

    public static string Tranche(int? ageJours) => ageJours switch
    {
        null => Tranches[4],
        <= 30 => Tranches[0],
        <= 60 => Tranches[1],
        <= 90 => Tranches[2],
        _ => Tranches[3]
    };

    /// <summary>Montants par tranche d'âge, dans l'ordre des tranches, tranches vides omises.</summary>
    public static IReadOnlyList<MontantParTranche> ParTranche(IEnumerable<(int? AgeJours, decimal Montant)> montants) =>
        montants.GroupBy(m => Tranche(m.AgeJours))
            .Select(g => new MontantParTranche(g.Key, g.Count(), g.Sum(x => x.Montant)))
            .OrderBy(t => Array.IndexOf(Tranches, t.Tranche)).ToList();

    /// <summary>Dormant : en stock, et jamais vendu ou dernière vente plus ancienne que le seuil (même règle que l'alerte « dormant »).</summary>
    public static bool EstDormant(int stock, DateTime? derniereVente, DateTime maintenant, int seuilJours) =>
        stock > 0 && (derniereVente is null || derniereVente.Value < maintenant.AddDays(-seuilJours));

    /// <summary>Rotation annuelle : coût d'achat des ventes ramené à un an, divisé par la valeur actuelle du stock.</summary>
    public static decimal? RotationAnnuelle(decimal coutVentes, int jours, decimal valeurStock) =>
        valeurStock <= 0 || jours <= 0 ? null : Math.Round(coutVentes * 365 / jours / valeurStock, 1);

    /// <summary>Couverture : jours de ventes (au coût d'achat) que représente le stock actuel.</summary>
    public static decimal? CouvertureJours(decimal coutVentes, int jours, decimal valeurStock) =>
        coutVentes <= 0 || jours <= 0 ? null : Math.Round(valeurStock / (coutVentes / jours), 0);
}

public enum RegroupementMarge { Produit, Laboratoire }

public interface IPilotageFinancier
{
    Task<RepartitionEncaissement> RepartitionAsync(DateOnly du, DateOnly au, CancellationToken ct = default);
    /// <summary>Bordereaux dont le reste dû est positif (y compris en préparation) ; âge calculé par l'appelant.</summary>
    Task<IReadOnlyList<CreanceBordereau>> BordereauxNonReglesAsync(CancellationToken ct = default);
    /// <summary>Part organismes des ventes validées qui ne sont sur aucun bordereau ni exclues du circuit.</summary>
    Task<decimal> PartOrganismesHorsBordereauAsync(CancellationToken ct = default);
    Task<(decimal Total, int Patients)> CreditsPatientsAsync(CancellationToken ct = default);
    /// <summary>Restes dus des ventes à crédit, avec la date de la vente.</summary>
    Task<IReadOnlyList<(DateOnly DateVente, decimal Reste)>> RestesCreditAsync(CancellationToken ct = default);
    /// <summary>Valeur, dormants et ruptures ; CoutVentesPeriode et JoursPeriode sont remplis par l'appelant (marge des rapports).</summary>
    Task<StockPilotage> StockAsync(int? seuilDormantJours, CancellationToken ct = default);
    Task<Page<ProduitDormant>> DormantsAsync(int? seuilDormantJours, string? recherche, DemandePage page, CancellationToken ct = default);
    Task<IReadOnlyList<AchatsGrossiste>> AchatsParGrossisteAsync(DateOnly du, DateOnly au, CancellationToken ct = default);
    Task<Page<MargeRegroupee>> MargesAsync(DateOnly du, DateOnly au, RegroupementMarge regroupement, string? recherche, DemandePage page, CancellationToken ct = default);
    Task<RejetsChifa> RejetsAsync(DateOnly du, DateOnly au, CancellationToken ct = default);
    Task<IReadOnlyList<TvaParTaux>> TvaAsync(DateOnly du, DateOnly au, CancellationToken ct = default);
}

public interface IDocumentsPilotageFinancier
{
    byte[] TableauFinancier(TableauFinancier t, IReadOnlyList<MargeRegroupee> marges, IReadOnlyList<ProduitDormant> dormants, string? pharmacie);
}

/// <summary>Accès : titulaire, ou droit « rapports.marges » (prix d'achat et marges).</summary>
public sealed class ObtenirPilotageFinancier(IPilotageFinancier pilotage, IRapports rapports, IPermissionService droits, IHorloge horloge)
{
    public const string Action = "consulter le tableau de bord financier";

    public async Task ExigerAccesAsync(long utilisateurId, CancellationToken ct)
    {
        if (!await droits.VoitPrixAchatAsync(utilisateurId, ct))
            throw new DroitRefuseException(Domain.Securite.Permissions.RapportsMarges, Action);
    }

    private DateOnly Aujourdhui => DateOnly.FromDateTime(horloge.MaintenantUtc.ToLocalTime());

    public async Task<TableauFinancier> ExecuterAsync(PeriodeRapport p, int? seuilDormantJours, long utilisateurId, CancellationToken ct = default)
    {
        await ExigerAccesAsync(utilisateurId, ct);
        var aujourdhui = Aujourdhui;
        var au = CalculsPilotage.AuEffectif(p.Au, aujourdhui);
        var actuel = await IndicateursAsync(p.Du, au, ct);
        IndicateursVentes? precedent = null;
        if (au >= p.Du)
        {
            var (pd, pa) = CalculsPilotage.PeriodePrecedente(p.Type, p.Du, au);
            precedent = await IndicateursAsync(pd, pa, ct);
        }

        var bordereaux = (await pilotage.BordereauxNonReglesAsync(ct))
            .Select(b => b with { AgeJours = b.DateDepot is { } d ? CalculsPilotage.AgeJours(d, aujourdhui) : null })
            .OrderByDescending(b => b.AgeJours ?? -1).ToList();
        var (credits, patients) = await pilotage.CreditsPatientsAsync(ct);
        var restes = await pilotage.RestesCreditAsync(ct);
        var creances = new Creances(bordereaux, await pilotage.PartOrganismesHorsBordereauAsync(ct), credits, patients,
            CalculsPilotage.ParTranche(restes.Select(r => ((int?)CalculsPilotage.AgeJours(r.DateVente, aujourdhui), r.Reste))));

        return new TableauFinancier(p, actuel, precedent, await rapports.ParJourAsync(p.Du, p.Au, ct),
            await pilotage.RepartitionAsync(p.Du, p.Au, ct), creances,
            (await pilotage.StockAsync(seuilDormantJours, ct)) with
            {
                CoutVentesPeriode = actuel.Marge.CoutAchat,
                JoursPeriode = Math.Max(0, au.DayNumber - p.Du.DayNumber + 1)
            },
            await pilotage.AchatsParGrossisteAsync(p.Du, p.Au, ct),
            await pilotage.RejetsAsync(p.Du, p.Au, ct),
            await pilotage.TvaAsync(p.Du, p.Au, ct));
    }

    private async Task<IndicateursVentes> IndicateursAsync(DateOnly du, DateOnly au, CancellationToken ct)
    {
        var s = await rapports.SyntheseAsync(du, au, ct);
        return new IndicateursVentes(du, au, s.ChiffreAffaires, s.NombreVentes, await rapports.MargeAsync(du, au, ct));
    }

    public async Task<Page<MargeRegroupee>> MargesAsync(PeriodeRapport p, RegroupementMarge regroupement, string? recherche, DemandePage page,
        long utilisateurId, CancellationToken ct = default)
    {
        await ExigerAccesAsync(utilisateurId, ct);
        return await pilotage.MargesAsync(p.Du, p.Au, regroupement, recherche, page, ct);
    }

    public async Task<Page<ProduitDormant>> DormantsAsync(int? seuilDormantJours, string? recherche, DemandePage page, long utilisateurId,
        CancellationToken ct = default)
    {
        await ExigerAccesAsync(utilisateurId, ct);
        return await pilotage.DormantsAsync(seuilDormantJours, recherche, page, ct);
    }
}

/// <summary>Export du tableau de bord : PDF ou classeur Excel (.xlsx), avec les 50 premières marges par produit et produits dormants.</summary>
public sealed class ExporterPilotageFinancier(ObtenirPilotageFinancier obtenir, IPilotageFinancier pilotage, IDocumentsPilotageFinancier documents,
    IPharmacie pharmacie)
{
    public const int LignesExport = 50;

    public async Task<(byte[] Contenu, string NomFichier)> ExecuterAsync(PeriodeRapport p, int? seuilDormantJours, FormatExport format,
        long utilisateurId, CancellationToken ct = default)
    {
        var t = await obtenir.ExecuterAsync(p, seuilDormantJours, utilisateurId, ct);
        var marges = (await pilotage.MargesAsync(p.Du, p.Au, RegroupementMarge.Produit, null, new DemandePage(1, LignesExport), ct)).Lignes;
        var dormants = (await pilotage.DormantsAsync(seuilDormantJours, null, new DemandePage(1, LignesExport), ct)).Lignes;
        var nom = $"tableau_financier_{p.Du:yyyyMMdd}_{p.Au:yyyyMMdd}";
        return format == FormatExport.Pdf
            ? (documents.TableauFinancier(t, marges, dormants, (await pharmacie.LireAsync(ct)).Nom), nom + ".pdf")
            : (PilotageFinancierXlsx.Ecrire(t, marges, dormants), nom + ".xlsx");
    }
}

public static class PilotageFinancierXlsx
{
    private static readonly CultureInfo Fr = CultureInfo.GetCultureInfo("fr-FR");

    public static byte[] Ecrire(TableauFinancier t, IReadOnlyList<MargeRegroupee> marges, IReadOnlyList<ProduitDormant> dormants)
    {
        static List<object?> L(params object?[] v) => [.. v];
        var synthese = new List<IReadOnlyList<object?>>
        {
            L("Indicateur", "Période", "Période précédente", "Écart (%)"),
            L("Période", $"{t.Actuel.Du.ToString("dd/MM/yyyy", Fr)} au {t.Actuel.Au.ToString("dd/MM/yyyy", Fr)}",
                t.Precedent is { } pp ? $"{pp.Du.ToString("dd/MM/yyyy", Fr)} au {pp.Au.ToString("dd/MM/yyyy", Fr)}" : null, null),
            L("Chiffre d'affaires TTC (DA)", t.ChiffreAffaires.Actuel, t.ChiffreAffaires.Precedent, t.ChiffreAffaires.VariationPct),
            L("Nombre de ventes", t.NombreVentes.Actuel, t.NombreVentes.Precedent, t.NombreVentes.VariationPct),
            L("Panier moyen (DA)", t.PanierMoyen?.Actuel, t.PanierMoyen?.Precedent, t.PanierMoyen?.VariationPct),
            L("Chiffre d'affaires HT (DA)", t.Actuel.Marge.ChiffreAffairesHt, t.Precedent?.Marge.ChiffreAffairesHt, null),
            L("Marge brute (DA)", t.MargeBrute.Actuel, t.MargeBrute.Precedent, t.MargeBrute.VariationPct),
            L("Taux de marge (%)", t.Actuel.Marge.Taux, t.Precedent?.Marge.Taux, null),
            L(),
            L("Répartition", "Montant (DA)"),
            L("Comptant (espèces, carte, chèque, avoir)", t.Repartition.Comptant),
            L("Crédit patients", t.Repartition.Credit),
            L("Part Chifa (CNAS, CASNOS)", t.Repartition.Chifa),
            L("Part MDN", t.Repartition.Mdn),
            L("Autres organismes", t.Repartition.AutresOrganismes),
            L(),
            L("Stock", "Valeur"),
            L("Valeur du stock au prix d'achat HT (DA)", t.Stock.ValeurAchat),
            L("Produits en stock", t.Stock.Produits),
            L("Lots périmés ou bloqués exclus, pour information (DA)", t.Stock.ValeurExclue),
            L("Rotation annuelle", t.Stock.RotationAnnuelle),
            L("Couverture (jours)", t.Stock.CouvertureJours),
            L($"Produits dormants (sans vente depuis {t.Stock.SeuilDormantJours} jours)", t.Stock.Dormants),
            L("Valeur dormante (DA)", t.Stock.ValeurDormante),
            L("Ruptures à l'officine", t.Stock.RupturesOfficine),
            L("Produits en rupture chez un grossiste", t.Stock.RupturesGrossistes),
        };
        var creances = new List<IReadOnlyList<object?>>
        {
            L("Bordereau", "Organisme", "Statut", "Déposé le", "Âge (jours)", "Tranche", "Attendu (DA)", "Rejeté (DA)", "Versé (DA)", "Reste (DA)")
        };
        creances.AddRange(t.Creances.Bordereaux.Select(b => (IReadOnlyList<object?>)L(b.Numero, b.Organisme, b.Statut,
            b.DateDepot?.ToString("dd/MM/yyyy", Fr), b.AgeJours, b.Tranche, b.Attendu, b.Rejete, b.Verse, b.Reste)));
        creances.Add(L());
        creances.Add(L("Part organismes hors bordereau (DA)", t.Creances.HorsBordereau));
        creances.Add(L("Crédits patients (DA)", t.Creances.CreditsPatients, "patients débiteurs", t.Creances.PatientsDebiteurs));
        creances.Add(L("Restes des ventes à crédit", "Ventes", "Montant (DA)"));
        creances.AddRange(t.Creances.CreditsParAge.Select(c => (IReadOnlyList<object?>)L(c.Tranche, c.Nombre, c.Montant)));

        var achats = new List<IReadOnlyList<object?>> { L("Grossiste", "Réceptions", "Montant HT (DA)", "Montant TTC (DA)") };
        achats.AddRange(t.Achats.Select(a => (IReadOnlyList<object?>)L(a.Grossiste, a.Receptions, a.MontantHt, a.MontantTtc)));

        var rejets = new List<IReadOnlyList<object?>> { L("Motif", "Rejets", "Montant rejeté (DA)", "Taux (% des ventes déposées)") };
        rejets.AddRange(t.Rejets.ParMotif.Select(m => (IReadOnlyList<object?>)L(m.Libelle, m.Nombre, m.Montant, t.Rejets.TauxMotif(m))));
        rejets.Add(L("Total", t.Rejets.Rejets, t.Rejets.MontantRejete, t.Rejets.TauxRejet));
        rejets.Add(L("Ventes déposées", t.Rejets.VentesDeposees, t.Rejets.MontantDepose));

        var tva = new List<IReadOnlyList<object?>> { L("Taux (%)", "TTC (DA)", "HT (DA)", "TVA (DA)", "Événements") };
        tva.AddRange(t.Tva.Select(x => (IReadOnlyList<object?>)L(x.Taux, x.Ttc, x.Ht, x.Tva, x.Evenements)));

        var feuilleMarges = new List<IReadOnlyList<object?>> { L("Produit", "Boîtes", "CA HT (DA)", "Coût d'achat HT (DA)", "Marge (DA)", "Taux (%)") };
        feuilleMarges.AddRange(marges.Select(m => (IReadOnlyList<object?>)L(m.Cle, m.Quantite, m.ChiffreAffairesHt, m.CoutAchat, m.Marge, m.Taux)));

        var feuilleDormants = new List<IReadOnlyList<object?>> { L("Produit", "Laboratoire", "Stock", "Valeur HT (DA)", "Dernière vente", "Jours sans vente") };
        feuilleDormants.AddRange(dormants.Select(d => (IReadOnlyList<object?>)L(d.Produit, d.Laboratoire, d.Stock, d.Valeur,
            d.DerniereVente?.ToLocalTime().ToString("dd/MM/yyyy", Fr), d.JoursSansVente)));

        return ClasseurXlsx.Ecrire(
        [
            new ClasseurXlsx.Feuille("Synthèse", synthese),
            new ClasseurXlsx.Feuille("Créances", creances),
            new ClasseurXlsx.Feuille("Achats", achats),
            new ClasseurXlsx.Feuille("Rejets Chifa", rejets),
            new ClasseurXlsx.Feuille("TVA", tva),
            new ClasseurXlsx.Feuille("Marges produits", feuilleMarges),
            new ClasseurXlsx.Feuille("Stock dormant", feuilleDormants),
        ]);
    }
}
