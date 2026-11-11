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

/// <summary>Marge : null sans le droit « rapports.marges ». EcartsVisibles : droit « caisse.voir_ecart ».</summary>
public sealed record RapportActivite(PeriodeRapport Periode, SyntheseVentes Synthese, MargeBrute? Marge,
    IReadOnlyList<VentesJour> ParJour, IReadOnlyList<ProduitVendu> TopProduits, IReadOnlyList<ClotureRapport> Clotures, bool EcartsVisibles,
    IReadOnlyList<RetourRapport>? Retours = null);

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

        return new RapportActivite(p,
            await rapports.SyntheseAsync(p.Du, p.Au, ct),
            voirMarge ? await rapports.MargeAsync(p.Du, p.Au, ct) : null,
            ParSemaine(p, await rapports.ParJourAsync(p.Du, p.Au, ct)),
            await rapports.TopProduitsAsync(p.Du, p.Au, NombreTopProduits, ct),
            clotures, voirEcarts, await rapports.RetoursAsync(p.Du, p.Au, ct));
    }

    /// <summary>Longue période : une barre par semaine (datée du lundi) au lieu d'une par jour.</summary>
    public static IReadOnlyList<VentesJour> ParSemaine(PeriodeRapport p, IReadOnlyList<VentesJour> jours) =>
        !p.ParSemaine ? jours
        : jours.GroupBy(j => PeriodeRapport.Lundi(j.Date))
               .Select(g => new VentesJour(g.Key < p.Du ? p.Du : g.Key, g.Sum(j => j.NombreVentes), g.Sum(j => j.ChiffreAffaires)))
               .OrderBy(s => s.Date).ToList();
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
