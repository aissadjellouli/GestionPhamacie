using System.Globalization;
using System.Text;
using Pharma.Application.Commun;
using Pharma.Application.Securite;
using Pharma.Domain.Commun;
using Pharma.Domain.Securite;

namespace Pharma.Application.Stock;

/// <summary>Ligne du registre : numéro d'ordre, entrée ou sortie, soldes du produit et du lot.</summary>
public sealed record LigneRegistre(
    long Numero, DateTime Date, long ProduitId, string Produit, string NumeroLot, string Type,
    int? Entree, int? Sortie, int? SoldeProduit, int SoldeLot,
    string? Patient, string? Medecin, string? Ordonnance, string? Fournisseur, string? Reference,
    string? PorteurNom, string? PorteurAdresse, string? PorteurPiece, string Employe, long? PatientId = null,
    int? DureeTraitementJours = null);

/// <summary>
/// Balance mensuelle des entrées et sorties d'un produit (arrêté du 23 août 1999, art. 4). SoldeReporte : solde d'ouverture du
/// registre inscrit ce mois-là (stock repris), distinct des entrées. Les lignes antérieures à l'ouverture n'y sont pas comptées.
/// </summary>
public sealed record BalanceMensuelle(long ProduitId, string Produit, int Annee, int Mois, int Entrees, int Sorties, int? SoldeFin, int SoldeReporte = 0);

public sealed record ProduitRegistre(long Id, string Nom);

/// <summary>Registre complet d'une période (exports PDF et CSV : un an au plus).</summary>
/// <param name="Filtres">Filtres d'un export d'inspection (patient, prescripteur), imprimés en tête.</param>
/// <param name="Annulations">Écritures annulées (lot 8), par numéro d'ordre : jamais modifiées, marquées « annulée » avec le motif.</param>
/// <param name="Sceau">Sceau de la période (lot 8) : sceaux mensuels du journal chaîné et état de la chaîne.</param>
public sealed record Registre(DateOnly Du, DateOnly Au, string? Produit, IReadOnlyList<LigneRegistre> Lignes,
    IReadOnlyList<BalanceMensuelle> Balance, bool NomsMasques, string? Filtres = null,
    IReadOnlyDictionary<long, Psychotropes.AnnulationEcriture>? Annulations = null, string? Sceau = null);

/// <summary>En-tête de l'écran du registre : nombre d'inscriptions et balance mensuelle de la période, calculés par la base (période sans limite).</summary>
public sealed record SyntheseRegistre(DateOnly Du, DateOnly Au, string? Produit, int Inscriptions, IReadOnlyList<BalanceMensuelle> Balance, bool NomsMasques);

/// <summary>Une page d'inscriptions (filtrée par opération) et les opérations présentes sur la période, pour le filtre.</summary>
public sealed record InscriptionsRegistre(Page<LigneRegistre> Page, IReadOnlyList<string> Operations, bool NomsMasques);

public interface IRegistrePsychotropes
{
    /// <summary>Lignes de la période (bornes incluses, jours locaux), dans l'ordre des numéros.</summary>
    Task<IReadOnlyList<LigneRegistre>> LireAsync(long? produitId, DateOnly du, DateOnly au, CancellationToken ct = default);
    /// <summary>Une page des lignes de la période, dans l'ordre des numéros ; <paramref name="type"/> filtre l'opération (null : toutes).</summary>
    Task<Page<LigneRegistre>> LirePageAsync(long? produitId, DateOnly du, DateOnly au, string? type, DemandePage page, CancellationToken ct = default);
    /// <summary>Types d'opération présents sur la période.</summary>
    Task<IReadOnlyList<string>> TypesAsync(long? produitId, DateOnly du, DateOnly au, CancellationToken ct = default);
    /// <summary>Nombre de lignes et balance mensuelle (mois en heure locale) de la période, triée par produit puis mois.</summary>
    Task<(int Inscriptions, IReadOnlyList<BalanceMensuelle> Balance)> SyntheseAsync(long? produitId, DateOnly du, DateOnly au, CancellationToken ct = default);
    /// <summary>Produits classés psychotrope ou stupéfiant, ou ayant déjà une ligne au registre.</summary>
    Task<IReadOnlyList<ProduitRegistre>> ProduitsAsync(CancellationToken ct = default);
}

public interface IDocumentsRegistre
{
    byte[] RegistrePsychotropes(Registre registre, string? pharmacie);
}

/// <summary>
/// Consultation du registre (droit psychotropes.registre). Noms du patient et du porteur en clair pour le
/// titulaire ; initiales pour les autres employés, à l'écran comme dans les exports.
/// À l'écran, la période n'a pas de limite : inscriptions lues par page et balance calculée par la base.
/// Le registre complet d'une période (exports PDF et CSV) est limité à un an par fichier.
/// </summary>
public sealed class ConsulterRegistre(IRegistrePsychotropes registre, IPermissionService droits)
{
    /// <summary>Durée maximale d'un export (PDF ou CSV), qui charge toutes les lignes de la période.</summary>
    public const int JoursMaxExport = 366;

    /// <summary>Registre complet de la période, pour les exports : un an au plus.</summary>
    /// <param name="patientId">Export d'inspection (lot 8) : lignes de ce patient seulement.</param>
    /// <param name="prescripteur">Export d'inspection (lot 8) : lignes dont le médecin contient ce texte.</param>
    public async Task<Registre> ExecuterAsync(long utilisateurId, long? produitId, DateOnly? du, DateOnly? au, DateOnly aujourdhui,
        CancellationToken ct = default, long? patientId = null, string? prescripteur = null)
    {
        var (debut, fin) = await PeriodeAsync(utilisateurId, du, au, aujourdhui, ct);
        if (fin.DayNumber - debut.DayNumber >= JoursMaxExport)
            throw new RegleMetierException("Export limité à un an par fichier : choisissez une période plus courte (un fichier par année).");

        var lignes = await registre.LireAsync(produitId, debut, fin, ct);
        // Filtres avant le masquage des noms (l'identifiant du patient est retiré hors titulaire)
        if (patientId is { } pid) lignes = lignes.Where(l => l.PatientId == pid).ToList();
        if (!string.IsNullOrWhiteSpace(prescripteur))
            lignes = lignes.Where(l => l.Medecin?.Contains(prescripteur.Trim(), StringComparison.CurrentCultureIgnoreCase) == true).ToList();
        var masquer = !await droits.EstTitulaireAsync(utilisateurId, ct);
        if (masquer) lignes = lignes.Select(Masquer).ToList();
        // Extrait par patient ou prescripteur : pas de balance (elle ne vaut que pour toutes les lignes du produit)
        var extrait = patientId is not null || !string.IsNullOrWhiteSpace(prescripteur);
        return new Registre(debut, fin, await NomProduitAsync(produitId, lignes, ct), lignes, extrait ? [] : Balance(lignes), masquer);
    }

    /// <summary>En-tête de l'écran : nombre d'inscriptions et balance mensuelle, sur une période sans limite.</summary>
    public async Task<SyntheseRegistre> SyntheseAsync(long utilisateurId, long? produitId, DateOnly? du, DateOnly? au, DateOnly aujourdhui,
        CancellationToken ct = default)
    {
        var (debut, fin) = await PeriodeAsync(utilisateurId, du, au, aujourdhui, ct);
        var (nombre, balance) = await registre.SyntheseAsync(produitId, debut, fin, ct);
        var masquer = !await droits.EstTitulaireAsync(utilisateurId, ct);
        string? nomProduit = produitId is null ? null
            : balance.FirstOrDefault()?.Produit ?? (await registre.ProduitsAsync(ct)).FirstOrDefault(p => p.Id == produitId)?.Nom;
        return new SyntheseRegistre(debut, fin, nomProduit, nombre, balance, masquer);
    }

    /// <summary>Une page d'inscriptions, filtrée par opération (vide : toutes), sur une période sans limite.</summary>
    public async Task<InscriptionsRegistre> InscriptionsAsync(long utilisateurId, long? produitId, DateOnly? du, DateOnly? au, string? operation,
        DemandePage page, DateOnly aujourdhui, CancellationToken ct = default)
    {
        var (debut, fin) = await PeriodeAsync(utilisateurId, du, au, aujourdhui, ct);
        var types = await registre.TypesAsync(produitId, debut, fin, ct);
        var type = string.IsNullOrWhiteSpace(operation) ? null : operation;
        var p = await registre.LirePageAsync(produitId, debut, fin, type, page, ct);
        var masquer = !await droits.EstTitulaireAsync(utilisateurId, ct);
        if (masquer) p = p.Vers(Masquer);
        return new InscriptionsRegistre(p, types, masquer);
    }

    /// <summary>Droit vérifié ; période par défaut du 1er du mois à aujourd'hui ; début avant la fin.</summary>
    private async Task<(DateOnly Debut, DateOnly Fin)> PeriodeAsync(long utilisateurId, DateOnly? du, DateOnly? au, DateOnly aujourdhui, CancellationToken ct)
    {
        await droits.ExigerAsync(utilisateurId, Permissions.PsychotropesRegistre, "consulter le registre des psychotropes", ct);
        var fin = au ?? aujourdhui;
        var debut = du ?? new DateOnly(fin.Year, fin.Month, 1);
        if (debut > fin) throw new RegleMetierException("La date de début doit précéder la date de fin.");
        return (debut, fin);
    }

    private async Task<string?> NomProduitAsync(long? produitId, IReadOnlyList<LigneRegistre> lignes, CancellationToken ct) =>
        produitId is null ? null : lignes.FirstOrDefault()?.Produit ?? (await registre.ProduitsAsync(ct)).FirstOrDefault(p => p.Id == produitId)?.Nom;

    /// <summary>Hors titulaire : initiales du patient et du porteur, adresse et pièce masquées.</summary>
    private static LigneRegistre Masquer(LigneRegistre l) => l with
    {
        Patient = Initiales(l.Patient), PatientId = null, PorteurNom = Initiales(l.PorteurNom),
        PorteurAdresse = l.PorteurAdresse is null ? null : "•••", PorteurPiece = l.PorteurPiece is null ? null : "•••"
    };

    /// <summary>
    /// Solde reporté, entrées, sorties et solde de fin de mois, par produit. Le solde d'ouverture est un solde reporté, pas une
    /// entrée ; les lignes antérieures à l'ouverture (solde du produit non calculé) sont exclues de la balance.
    /// </summary>
    public static IReadOnlyList<BalanceMensuelle> Balance(IReadOnlyList<LigneRegistre> lignes) =>
        lignes.Where(l => l.SoldeProduit is not null)
              .GroupBy(l => (l.ProduitId, l.Produit, l.Date.ToLocalTime().Year, l.Date.ToLocalTime().Month))
              .Select(g => new BalanceMensuelle(g.Key.ProduitId, g.Key.Produit, g.Key.Year, g.Key.Month,
                  g.Where(l => l.Type != "ouverture").Sum(l => l.Entree ?? 0), g.Sum(l => l.Sortie ?? 0),
                  g.OrderBy(l => l.Numero).Last().SoldeProduit,
                  g.Where(l => l.Type == "ouverture").Sum(l => l.Entree ?? 0)))
              .OrderBy(b => b.Produit).ThenBy(b => b.Annee).ThenBy(b => b.Mois)
              .ToList();

    /// <summary>« Benali Karim » → « B. K. »</summary>
    public static string? Initiales(string? nom) => string.IsNullOrWhiteSpace(nom) ? nom
        : string.Join(' ', nom.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(m => char.ToUpperInvariant(m[0]) + "."));
}

public sealed class ListerProduitsRegistre(IRegistrePsychotropes registre, IPermissionService droits)
{
    public async Task<IReadOnlyList<ProduitRegistre>> ExecuterAsync(long utilisateurId, CancellationToken ct = default)
    {
        await droits.ExigerAsync(utilisateurId, Permissions.PsychotropesRegistre, "consulter le registre des psychotropes", ct);
        return await registre.ProduitsAsync(ct);
    }
}

/// <summary>
/// Export du registre consulté : PDF (à imprimer et signer), Excel (.xlsx) ou CSV. Export d'inspection (lot 8) : filtres patient et
/// prescripteur en plus du produit et de la période, écritures annulées marquées, sceau de la période (journal chaîné).
/// </summary>
public sealed class ExporterRegistre(ConsulterRegistre consulter, IDocumentsRegistre documents, Administration.IPharmacie pharmacie,
    Psychotropes.IRegistreRenforceDepot? renforce = null, Integrite.IIntegriteDepot? integrite = null)
{
    public const string TypeXlsx = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";

    public async Task<(byte[] Contenu, string NomFichier, string Type)> ExecuterAsync(string format, long utilisateurId, long? produitId,
        DateOnly? du, DateOnly? au, DateOnly aujourdhui, CancellationToken ct = default, long? patientId = null, string? prescripteur = null)
    {
        if (format is not ("pdf" or "csv" or "xlsx")) throw new RegleMetierException("Format d'export inconnu : pdf, xlsx ou csv.");
        var r = await consulter.ExecuterAsync(utilisateurId, produitId, du, au, aujourdhui, ct, patientId, prescripteur);
        var filtres = new List<string>();
        if (patientId is not null) filtres.Add("patient : " + (r.Lignes.FirstOrDefault()?.Patient ?? "aucune ligne"));
        if (!string.IsNullOrWhiteSpace(prescripteur)) filtres.Add($"prescripteur : {prescripteur.Trim()}");
        r = r with
        {
            Filtres = filtres.Count == 0 ? null : string.Join(" · ", filtres),
            Annulations = renforce is null ? null : await renforce.AnnulationsAsync(r.Lignes.Select(l => l.Numero).ToList(), ct),
            Sceau = integrite is null ? null : await Psychotropes.ScellerRegistreMensuel.TexteSceauAsync(integrite, r.Du, r.Au, ct)
        };
        var nom = $"registre_psychotropes_{r.Du:yyyyMMdd}_{r.Au:yyyyMMdd}";
        return format switch
        {
            "pdf" => (documents.RegistrePsychotropes(r, (await pharmacie.LireAsync(ct)).Nom), nom + ".pdf", "application/pdf"),
            "xlsx" => (RegistreCsv.Xlsx(r), nom + ".xlsx", TypeXlsx),
            _ => (RegistreCsv.Ecrire(r), nom + ".csv", "text/csv")
        };
    }
}

public static class RegistreCsv
{
    private static readonly CultureInfo Fr = CultureInfo.GetCultureInfo("fr-FR");

    public static string Libelle(string type) => type switch
    {
        "ouverture" => "Solde d'ouverture",
        "entree" => "Entrée (réception)",
        "retour_client" => "Retour client",
        "retour_refuse" => "Retour refusé (réintégré)",
        "ecart_plus" => "Écart d'inventaire (+)",
        "sortie" => "Délivrance",
        "retour_fournisseur" => "Retour grossiste",
        "destruction" => "Destruction (périmé)",
        "casse" => "Casse",
        "ecart_moins" => "Écart d'inventaire (−)",
        _ => type
    };

    private static readonly string[] Titres =
    [
        "N° d'ordre", "Date", "Produit", "Lot", "Opération", "Entrée", "Sortie", "Solde produit", "Solde lot",
        "Patient", "Médecin", "N° ordonnance", "Grossiste", "Référence", "Porteur", "Adresse du porteur", "Pièce d'identité", "Employé",
        "Annulation", "Durée du traitement (jours)"
    ];

    /// <summary>« Annulée le … par … : motif » (écriture d'annulation, lot 8), ou null.</summary>
    public static string? Annulation(Registre r, long numero) =>
        r.Annulations?.GetValueOrDefault(numero) is { } a
            ? $"Annulée le {a.Date.ToLocalTime().ToString("dd/MM/yyyy HH:mm", Fr)} par {a.Employe} : {a.Motif}" : null;

    private static object?[] Cellules(Registre r, LigneRegistre l) =>
    [
        l.Numero, l.Date.ToLocalTime().ToString("dd/MM/yyyy HH:mm", Fr), l.Produit, l.NumeroLot, Libelle(l.Type), l.Entree, l.Sortie,
        l.SoldeProduit, l.SoldeLot, l.Patient, l.Medecin, l.Ordonnance, l.Fournisseur, l.Reference,
        l.PorteurNom, l.PorteurAdresse, l.PorteurPiece, l.Employe, Annulation(r, l.Numero), l.DureeTraitementJours
    ];

    private static string Entete(Registre r) =>
        $"du {r.Du:dd/MM/yyyy} au {r.Au:dd/MM/yyyy} · {r.Produit ?? "tous les produits"}{(r.Filtres is { } f ? " · " + f : "")}";

    public static byte[] Ecrire(Registre r)
    {
        var sb = new StringBuilder();
        void L(params object?[] champs) => sb.AppendLine(string.Join(';', champs.Select(Champ)));
        L("Registre des psychotropes et stupéfiants", Entete(r));
        if (r.Sceau is not null) L(r.Sceau);
        L();
        L(Titres);
        foreach (var l in r.Lignes) L(Cellules(r, l));
        L();
        L("Balance mensuelle", "Mois", "Solde reporté", "Entrées", "Sorties", "Solde fin de mois");
        foreach (var b in r.Balance) L(b.Produit, $"{b.Mois:00}/{b.Annee}", b.SoldeReporte == 0 ? null : b.SoldeReporte, b.Entrees, b.Sorties, b.SoldeFin);
        return [.. Encoding.UTF8.GetPreamble(), .. Encoding.UTF8.GetBytes(sb.ToString())];
    }

    /// <summary>Classeur Excel : feuilles « Registre », « Balance », « Sceau » (lot 8).</summary>
    public static byte[] Xlsx(Registre r)
    {
        var registre = new List<IReadOnlyList<object?>> { Titres };
        registre.AddRange(r.Lignes.Select(l => (IReadOnlyList<object?>)Cellules(r, l)));
        var balance = new List<IReadOnlyList<object?>> { new object?[] { "Produit", "Mois", "Solde reporté", "Entrées", "Sorties", "Solde fin de mois" } };
        balance.AddRange(r.Balance.Select(b => (IReadOnlyList<object?>)new object?[]
            { b.Produit, $"{b.Mois:00}/{b.Annee}", b.SoldeReporte == 0 ? null : b.SoldeReporte, b.Entrees, b.Sorties, b.SoldeFin }));
        var sceau = new List<IReadOnlyList<object?>>
        {
            new object?[] { "Registre des psychotropes et stupéfiants" }, new object?[] { Entete(r) },
            new object?[] { r.Sceau ?? "Sceau non disponible" },
            new object?[] { r.NomsMasques ? "Noms des patients et porteurs abrégés (édition hors titulaire)" : null }
        };
        return Psychotropes.ClasseurXlsx.Ecrire([new("Registre", registre), new("Balance", balance), new("Sceau", sceau)]);
    }

    private static string Champ(object? v)
    {
        var s = v switch { null => "", IFormattable f => f.ToString(null, Fr), _ => v.ToString() ?? "" };
        // Une cellule qui commence par = + - @ serait lue comme une formule par Excel
        if (s.Length > 0 && "=+-@".Contains(s[0]) && v is string) s = "'" + s;
        return s.Contains(';') || s.Contains('"') || s.Contains('\n') ? $"\"{s.Replace("\"", "\"\"")}\"" : s;
    }
}
