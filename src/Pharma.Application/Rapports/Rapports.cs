using System.Globalization;
using System.Text;
using Pharma.Application.Administration;
using Pharma.Application.Commun;
using Pharma.Application.Securite;
using Pharma.Domain.Commun;
using Pharma.Domain.Rapports;
using Pharma.Domain.Securite;

namespace Pharma.Application.Rapports;

// ---- Modèles -------------------------------------------------------------------

/// <summary>
/// Ventes validées de la période (montants TTC, remises déduites) ; Annulations : ventes annulées.
/// ChiffreAffaires est net des retours clients enregistrés pendant la période (Retours, MontantRetours) : un retour
/// diminue le chiffre du jour où la caisse a repris les boîtes, pas celui du jour de la vente.
/// </summary>
/// <summary>VentesMedicaments / VentesAutres : montant des lignes (avant remise et retours) selon la catégorie du produit ; Shp : SHP compris.</summary>
public sealed record SyntheseVentes(int NombreVentes, decimal ChiffreAffaires, decimal PartOrganismes, decimal CreditsAccordes, decimal Remises, int Annulations,
    int Retours = 0, decimal MontantRetours = 0, decimal VentesMedicaments = 0, decimal VentesAutres = 0, decimal Shp = 0);

/// <summary>
/// Marge brute : chiffre d'affaires hors TVA (remises déduites) moins le coût d'achat HT des boîtes vendues,
/// au prix réel de chaque boîte identifiée, sinon au prix de son lot.
/// </summary>
public sealed record MargeBrute(decimal ChiffreAffairesHt, decimal CoutAchat)
{
    public decimal Montant => ChiffreAffairesHt - CoutAchat;
    public decimal? Taux => ChiffreAffairesHt == 0 ? null : Math.Round(Montant / ChiffreAffairesHt * 100, 1);
}

public sealed record VentesJour(DateOnly Date, int NombreVentes, decimal ChiffreAffaires);
public sealed record ProduitVendu(string Produit, int Quantite, decimal ChiffreAffaires);

/// <summary>Session de caisse clôturée. Attendu et Ecart : null sans le droit de voir les écarts.</summary>
/// <summary>Page des clôtures de la période ; employés et postes de la période pour les filtres.</summary>
public sealed record PageClotures(Commun.Page<ClotureRapport> Page, bool EcartsVisibles, IReadOnlyList<string> Employes, IReadOnlyList<string> Postes);

public sealed record ClotureRapport(string Employe, string Poste, DateTime OuverteLe, DateTime? FermeeLe,
    decimal? Attendu, decimal? Compte, decimal? Ecart, int Annulations, int Remises, int Retours = 0, long? EmployeId = null);

/// <summary>
/// Retour client lié à la période. DeduitDansLaPeriode : enregistré pendant la période, son montant diminue le chiffre de la période.
/// Sinon il porte sur une vente de la période mais a été enregistré un autre jour : il figure pour information, la recette du jour
/// de la vente ne change pas. Vendeur : employé de la vente ; Employe et Poste : ceux du retour. DetteDiminuee : part du montant
/// retirée de la dette du patient au lieu d'être rendue.
/// </summary>
public sealed record RetourRapport(string Ticket, DateTime DateVente, string Vendeur, DateTime DateRetour, string Poste, string Employe,
    string Produits, decimal Montant, decimal DetteDiminuee, bool DeduitDansLaPeriode, long? VendeurId = null, long? EmployeId = null);

/// <summary>
/// Boîtes sorties du stock sans vente pendant la période : Motif « perime », « casse » ou « echantillon » (don ou échantillon).
/// Valeur : coût d'achat HT des boîtes (prix d'achat retenu au moment de la sortie) ; null sans le droit « rapports.marges ».
/// Reference : motif saisi, et pour un don, à qui et pourquoi. TvaAchat : TVA d'achat de ces boîtes (valeur HT × taux du produit),
/// masquée comme la valeur.
/// </summary>
public sealed record SortieSansVente(DateTime Date, string Motif, string Produit, string Lot, int Quantite, decimal? Valeur, string Employe,
    string? Reference, long? EmployeId = null, decimal? TvaAchat = null);

/// <summary>
/// TVA d'achat à reverser selon le motif de la sortie (réglage du titulaire, d'après son comptable ; « non » par défaut).
/// </summary>
public sealed record TvaSorties(bool Perime, bool Casse, bool Don)
{
    public bool Reversee(string motif) => motif switch
    {
        "perime" => Perime,
        "casse" => Casse,
        "echantillon" => Don,
        _ => false
    };
}

/// <summary>Marge après pertes et dons : marge brute - valeur d'achat HT des boîtes sorties sans vente - TVA à reverser sur ces boîtes.</summary>
public sealed record MargeApresSorties(decimal MargeBrute, decimal PertesHt, decimal TvaAReverser)
{
    public decimal Montant => MargeBrute - PertesHt - TvaAReverser;

    public static MargeApresSorties Calculer(MargeBrute marge, IEnumerable<SortieSansVente> sorties, TvaSorties tva)
    {
        var liste = sorties.ToList();
        return new MargeApresSorties(marge.Montant, liste.Sum(s => s.Valeur ?? 0),
            liste.Where(s => tva.Reversee(s.Motif)).Sum(s => s.TvaAchat ?? 0));
    }
}

/// <summary>Total des sorties sans vente d'un employé, d'un produit ou d'un motif (Cle), en boîtes et en valeur d'achat HT.</summary>
public sealed record TotalSorties(string Cle, int Perimes, int Casses, int Dons, decimal? Valeur)
{
    public int Boites => Perimes + Casses + Dons;
}

/// <summary>Regroupements des sorties sans vente pour les rapports (écran, PDF, CSV).</summary>
public static class SortiesRapport
{
    public static string Libelle(string motif) => motif switch
    {
        "perime" => "Périmé",
        "casse" => "Casse",
        "echantillon" => "Don ou échantillon",
        _ => motif
    };

    public static TotalSorties Total(string cle, IEnumerable<SortieSansVente> sorties)
    {
        var liste = sorties.ToList();
        return new TotalSorties(cle,
            liste.Where(s => s.Motif == "perime").Sum(s => s.Quantite),
            liste.Where(s => s.Motif == "casse").Sum(s => s.Quantite),
            liste.Where(s => s.Motif == "echantillon").Sum(s => s.Quantite),
            // Valeur masquée (sans le droit « rapports.marges ») sur une sortie : pas de total en valeur
            liste.Any(s => s.Valeur is null) ? null : liste.Sum(s => s.Valeur!.Value));
    }

    /// <summary>Par employé, la plus forte valeur (sinon le plus de boîtes) en premier.</summary>
    public static IReadOnlyList<TotalSorties> ParEmploye(IEnumerable<SortieSansVente> sorties) =>
        Grouper(sorties, s => s.Employe);

    public static IReadOnlyList<TotalSorties> ParProduit(IEnumerable<SortieSansVente> sorties) =>
        Grouper(sorties, s => s.Produit);

    private static IReadOnlyList<TotalSorties> Grouper(IEnumerable<SortieSansVente> sorties, Func<SortieSansVente, string> cle) =>
        sorties.GroupBy(cle).Select(g => Total(g.Key, g))
               .OrderByDescending(t => t.Valeur ?? 0).ThenByDescending(t => t.Boites).ThenBy(t => t.Cle, StringComparer.CurrentCulture).ToList();
}

/// <summary>Marge : null sans le droit « rapports.marges ». EcartsVisibles : droit « caisse.voir_ecart ».</summary>
public sealed record RapportActivite(PeriodeRapport Periode, SyntheseVentes Synthese, MargeBrute? Marge,
    IReadOnlyList<VentesJour> ParJour, IReadOnlyList<ProduitVendu> TopProduits, IReadOnlyList<ClotureRapport> Clotures, bool EcartsVisibles,
    IReadOnlyList<RetourRapport>? Retours = null, IReadOnlyList<SortieSansVente>? Sorties = null,
    TvaSorties? Tva = null, MargeApresSorties? ApresSorties = null);

public interface IRapports
{
    Task<SyntheseVentes> SyntheseAsync(DateOnly du, DateOnly au, CancellationToken ct = default);
    /// <summary>Un élément par jour de la période, y compris les jours sans vente.</summary>
    Task<IReadOnlyList<VentesJour>> ParJourAsync(DateOnly du, DateOnly au, CancellationToken ct = default);
    Task<IReadOnlyList<ProduitVendu>> TopProduitsAsync(DateOnly du, DateOnly au, int limite, CancellationToken ct = default);
    Task<MargeBrute> MargeAsync(DateOnly du, DateOnly au, CancellationToken ct = default);
    /// <summary>Sessions clôturées pendant la période.</summary>
    Task<IReadOnlyList<ClotureRapport>> CloturesAsync(DateOnly du, DateOnly au, CancellationToken ct = default);
    /// <summary>Retours enregistrés pendant la période, et retours (enregistrés un autre jour) sur les ventes de la période.</summary>
    Task<IReadOnlyList<RetourRapport>> RetoursAsync(DateOnly du, DateOnly au, CancellationToken ct = default);
    /// <summary>Boîtes sorties du stock sans vente pendant la période (périmés, casses, dons et échantillons), les plus récentes d'abord.</summary>
    Task<IReadOnlyList<SortieSansVente>> SortiesAsync(DateOnly du, DateOnly au, CancellationToken ct = default);
    Task<TvaSorties> TvaSortiesAsync(CancellationToken ct = default);
    Task ModifierTvaSortiesAsync(TvaSorties tva, CancellationToken ct = default);
}

public interface IDocumentsRapports
{
    byte[] RapportActivite(RapportActivite rapport, string? pharmacie);
}

// ---- Cas d'usage -----------------------------------------------------------------

/// <summary>Rapport d'activité d'un jour, d'une semaine ou d'un mois, selon les droits de l'employé.</summary>
public sealed class ObtenirRapport(IRapports rapports, IPermissionService droits)
{
    public const int NombreTopProduits = 10;

    public Task<RapportActivite> ExecuterAsync(TypePeriode type, DateOnly reference, long utilisateurId, CancellationToken ct = default) =>
        ExecuterAsync(PeriodeRapport.Calculer(type, reference), utilisateurId, ct);

    public async Task<RapportActivite> ExecuterAsync(PeriodeRapport p, long utilisateurId, CancellationToken ct = default)
    {
        await droits.ExigerAsync(utilisateurId, Permissions.RapportsVentes, "consulter les rapports", ct);
        var voirMarge = await droits.APermissionAsync(utilisateurId, Permissions.RapportsMarges, ct);
        var voirEcarts = await droits.APermissionAsync(utilisateurId, Permissions.CaisseVoirEcart, ct);

        var clotures = await rapports.CloturesAsync(p.Du, p.Au, ct);
        if (!voirEcarts) clotures = clotures.Select(c => c with { Attendu = null, Ecart = null }).ToList();

        var sorties = await rapports.SortiesAsync(p.Du, p.Au, ct);
        // Valeur d'achat des pertes et des dons : même droit que la marge (prix d'achat)
        if (!voirMarge) sorties = sorties.Select(s => s with { Valeur = null, TvaAchat = null }).ToList();
        var marge = voirMarge ? await rapports.MargeAsync(p.Du, p.Au, ct) : null;
        var tva = await rapports.TvaSortiesAsync(ct);

        return new RapportActivite(p,
            await rapports.SyntheseAsync(p.Du, p.Au, ct),
            marge,
            ParSemaine(p, await rapports.ParJourAsync(p.Du, p.Au, ct)),
            await rapports.TopProduitsAsync(p.Du, p.Au, NombreTopProduits, ct),
            clotures, voirEcarts, await rapports.RetoursAsync(p.Du, p.Au, ct), sorties, tva,
            marge is null ? null : MargeApresSorties.Calculer(marge, sorties, tva));
    }

    /// <summary>Longue période : une barre par semaine (datée du lundi) au lieu d'une par jour.</summary>
    /// <summary>
    /// Clôtures de caisse de la période, paginées et filtrées par le serveur (employé, poste, avec un écart seulement) ;
    /// listes d'employés et de postes de la période pour préremplir les filtres. Attendu, écart et filtre sur l'écart :
    /// seulement avec le droit de voir les écarts.
    /// </summary>
    public async Task<PageClotures> CloturesPageAsync(PeriodeRapport p, string? employe, string? poste, bool avecEcart, Commun.DemandePage page,
        long utilisateurId, CancellationToken ct = default)
    {
        await droits.ExigerAsync(utilisateurId, Permissions.RapportsVentes, "consulter les rapports", ct);
        var voirEcarts = await droits.APermissionAsync(utilisateurId, Permissions.CaisseVoirEcart, ct);
        var toutes = await rapports.CloturesAsync(p.Du, p.Au, ct);
        if (!voirEcarts) toutes = toutes.Select(c => c with { Attendu = null, Ecart = null }).ToList();
        var retenues = toutes.Where(c => (string.IsNullOrWhiteSpace(employe) || c.Employe == employe)
                && (string.IsNullOrWhiteSpace(poste) || c.Poste == poste)
                && (!avecEcart || !voirEcarts || (c.Ecart ?? 0) != 0)).ToList();
        return new PageClotures(
            new Commun.Page<ClotureRapport>(retenues.Skip(page.Decalage).Take(page.Taille).ToList(), retenues.Count, page.Page, page.Taille),
            voirEcarts, toutes.Select(c => c.Employe).Distinct().Order(StringComparer.CurrentCulture).ToList(),
            toutes.Select(c => c.Poste).Distinct().Order(StringComparer.CurrentCulture).ToList());
    }

    public static IReadOnlyList<VentesJour> ParSemaine(PeriodeRapport p, IReadOnlyList<VentesJour> jours) =>
        !p.ParSemaine ? jours
        : jours.GroupBy(j => PeriodeRapport.Lundi(j.Date))
               .Select(g => new VentesJour(g.Key < p.Du ? p.Du : g.Key, g.Sum(j => j.NombreVentes), g.Sum(j => j.ChiffreAffaires)))
               .OrderBy(s => s.Date).ToList();
}

/// <summary>TVA à reverser par motif de sortie : réservé au titulaire, inscrit au journal.</summary>
public sealed class ModifierTvaSorties(IRapports rapports, IPermissionService droits, IJournal journal)
{
    public async Task ExecuterAsync(TvaSorties tva, long utilisateurId, CancellationToken ct = default)
    {
        await droits.ExigerTitulaireAsync(utilisateurId, "régler la TVA à reverser sur les pertes et dons", ct);
        var avant = await rapports.TvaSortiesAsync(ct);
        await rapports.ModifierTvaSortiesAsync(tva, ct);
        await journal.EcrireAsync(utilisateurId, "tva_sorties", "parametre_rapport", 1,
            new Dictionary<string, object?>
            {
                ["perime"] = tva.Perime, ["casse"] = tva.Casse, ["don"] = tva.Don,
                ["avant"] = $"périmé {(avant.Perime ? "oui" : "non")}, casse {(avant.Casse ? "oui" : "non")}, don {(avant.Don ? "oui" : "non")}"
            }, null, ct);
    }
}

public enum FormatExport { Pdf, Excel }

/// <summary>Export du rapport : PDF (impression) ou fichier CSV ouvert par Excel.</summary>
public sealed class ExporterRapport(ObtenirRapport obtenir, IDocumentsRapports documents, IPharmacie pharmacie)
{
    public Task<(byte[] Contenu, string NomFichier)> ExecuterAsync(TypePeriode type, DateOnly reference, FormatExport format,
        long utilisateurId, CancellationToken ct = default) =>
        ExecuterAsync(PeriodeRapport.Calculer(type, reference), format, utilisateurId, ct);

    public async Task<(byte[] Contenu, string NomFichier)> ExecuterAsync(PeriodeRapport periode, FormatExport format,
        long utilisateurId, CancellationToken ct = default)
    {
        var r = await obtenir.ExecuterAsync(periode, utilisateurId, ct);
        var nom = $"rapport_{periode.Type.ToString().ToLowerInvariant()}_{r.Periode.Du:yyyyMMdd}"
                  + (periode.Type == TypePeriode.Dates ? $"_{r.Periode.Au:yyyyMMdd}" : "");
        return format == FormatExport.Pdf
            ? (documents.RapportActivite(r, (await pharmacie.LireAsync(ct)).Nom), nom + ".pdf")
            : (RapportCsv.Ecrire(r), nom + ".csv");
    }
}

/// <summary>CSV au format d'Excel en français : séparateur « ; », virgule décimale, UTF-8 avec BOM.</summary>
public static class RapportCsv
{
    private static readonly CultureInfo Fr = CultureInfo.GetCultureInfo("fr-FR");

    public static byte[] Ecrire(RapportActivite r)
    {
        var sb = new StringBuilder();
        void L(params object?[] champs) => sb.AppendLine(string.Join(';', champs.Select(Champ)));

        L("Rapport d'activité", r.Periode.Libelle);
        L();
        L("Synthèse", "Montant (DA)");
        L("Chiffre d'affaires TTC (retours déduits)", r.Synthese.ChiffreAffaires);
        L("Retours clients de la période (nombre)", r.Synthese.Retours);
        L("Retours clients de la période (montant déduit)", r.Synthese.MontantRetours);
        L("Nombre de ventes", r.Synthese.NombreVentes);
        L("Part organismes (Chifa) à recevoir", r.Synthese.PartOrganismes);
        L("Crédits clients accordés", r.Synthese.CreditsAccordes);
        L("Remises accordées", r.Synthese.Remises);
        L("Ventes de médicaments (avant remises et retours)", r.Synthese.VentesMedicaments);
        L("Ventes d'autres produits (avant remises et retours)", r.Synthese.VentesAutres);
        L("dont SHP (honoraire du pharmacien)", r.Synthese.Shp);
        L("Ventes annulées", r.Synthese.Annulations);
        if (r.Marge is { } m)
        {
            L("Chiffre d'affaires HT", m.ChiffreAffairesHt);
            L("Coût d'achat HT des boîtes vendues", m.CoutAchat);
            L("Marge brute", m.Montant);
            L("Taux de marge (%)", m.Taux);
            if (r.ApresSorties is { } a)
            {
                L("Pertes et dons au prix d'achat HT", a.PertesHt);
                L("TVA à reverser sur les pertes et dons", a.TvaAReverser);
                L("Marge après pertes et dons", a.Montant);
            }
        }
        L();
        L(r.Periode.ParSemaine ? "Semaine du" : "Date", "Ventes", "Chiffre d'affaires (DA)");
        foreach (var j in r.ParJour) L(j.Date.ToString("dd/MM/yyyy", Fr), j.NombreVentes, j.ChiffreAffaires);
        L();
        L("Produit", "Boîtes vendues", "Chiffre d'affaires (DA)");
        foreach (var p in r.TopProduits) L(p.Produit, p.Quantite, p.ChiffreAffaires);
        L();
        L("Employé", "Poste", "Ouverture", "Clôture", "Attendu", "Compté", "Écart", "Annulations", "Remises", "Retours");
        foreach (var c in r.Clotures)
            L(c.Employe, c.Poste, c.OuverteLe.ToLocalTime().ToString("dd/MM/yyyy HH:mm", Fr), c.FermeeLe?.ToLocalTime().ToString("dd/MM/yyyy HH:mm", Fr),
              c.Attendu, c.Compte, c.Ecart, c.Annulations, c.Remises, c.Retours);

        if (r.Retours is { Count: > 0 } retours)
        {
            L();
            L("Retours clients", "Vente du", "Vendeur", "Retour le", "Poste du retour", "Retour enregistré par", "Produits rendus", "Montant (DA)",
              "Dont déduit de la dette du patient (DA)", "Chiffre diminué");
            foreach (var x in retours)
                L(x.Ticket, x.DateVente.ToLocalTime().ToString("dd/MM/yyyy HH:mm", Fr), x.Vendeur, x.DateRetour.ToLocalTime().ToString("dd/MM/yyyy HH:mm", Fr),
                  x.Poste, x.Employe, x.Produits, x.Montant, x.DetteDiminuee,
                  x.DeduitDansLaPeriode ? "déduit sur cette période" : $"déduit le {x.DateRetour.ToLocalTime().ToString("dd/MM/yyyy", Fr)}, pas sur cette période");
        }

        if (r.Sorties is { Count: > 0 } sorties)
        {
            var total = SortiesRapport.Total("Total", sorties);
            L();
            L("Sorties de stock sans vente", "Périmés (boîtes)", "Casses (boîtes)", "Dons et échantillons (boîtes)", "Valeur d'achat HT (DA)");
            L(total.Cle, total.Perimes, total.Casses, total.Dons, total.Valeur);
            L();
            L("Sorties par employé", "Périmés (boîtes)", "Casses (boîtes)", "Dons et échantillons (boîtes)", "Valeur d'achat HT (DA)");
            foreach (var t in SortiesRapport.ParEmploye(sorties)) L(t.Cle, t.Perimes, t.Casses, t.Dons, t.Valeur);
            L();
            L("Sorties par produit", "Périmés (boîtes)", "Casses (boîtes)", "Dons et échantillons (boîtes)", "Valeur d'achat HT (DA)");
            foreach (var t in SortiesRapport.ParProduit(sorties)) L(t.Cle, t.Perimes, t.Casses, t.Dons, t.Valeur);
            L();
            L("Date", "Motif", "Produit", "Lot", "Boîtes", "Valeur d'achat HT (DA)", "TVA d'achat (DA)", "TVA reversée", "Employé", "Bénéficiaire ou raison");
            foreach (var s in sorties)
                L(s.Date.ToLocalTime().ToString("dd/MM/yyyy HH:mm", Fr), SortiesRapport.Libelle(s.Motif), s.Produit, s.Lot, s.Quantite, s.Valeur, s.TvaAchat,
                  r.Tva is { } t ? (t.Reversee(s.Motif) ? "oui" : "non") : null, s.Employe, s.Reference);
        }

        return [.. Encoding.UTF8.GetPreamble(), .. Encoding.UTF8.GetBytes(sb.ToString())];
    }

    private static string Champ(object? v)
    {
        var s = v switch
        {
            null => "",
            decimal d => d.ToString("0.00", Fr),
            IFormattable f => f.ToString(null, Fr),
            _ => v.ToString() ?? ""
        };
        // Une cellule qui commence par = + - @ serait lue comme une formule par Excel
        if (s.Length > 0 && "=+-@".Contains(s[0]) && v is string) s = "'" + s;
        return s.Contains(';') || s.Contains('"') || s.Contains('\n') ? $"\"{s.Replace("\"", "\"\"")}\"" : s;
    }
}
