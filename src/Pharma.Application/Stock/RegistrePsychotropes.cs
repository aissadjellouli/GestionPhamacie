using System.Globalization;
using System.Text;
using Pharma.Application.Securite;
using Pharma.Domain.Commun;
using Pharma.Domain.Securite;

namespace Pharma.Application.Stock;

/// <summary>Ligne du registre : numéro d'ordre, entrée ou sortie, soldes du produit et du lot.</summary>
public sealed record LigneRegistre(
    long Numero, DateTime Date, long ProduitId, string Produit, string NumeroLot, string Type,
    int? Entree, int? Sortie, int? SoldeProduit, int SoldeLot,
    string? Patient, string? Medecin, string? Ordonnance, string? Fournisseur, string? Reference,
    string? PorteurNom, string? PorteurAdresse, string? PorteurPiece, string Employe);

/// <summary>Balance mensuelle des entrées et sorties d'un produit (arrêté du 23 août 1999, art. 4).</summary>
public sealed record BalanceMensuelle(long ProduitId, string Produit, int Annee, int Mois, int Entrees, int Sorties, int? SoldeFin);

public sealed record ProduitRegistre(long Id, string Nom);

public sealed record Registre(DateOnly Du, DateOnly Au, string? Produit, IReadOnlyList<LigneRegistre> Lignes,
    IReadOnlyList<BalanceMensuelle> Balance, bool NomsMasques);

public interface IRegistrePsychotropes
{
    /// <summary>Lignes de la période (bornes incluses, jours locaux), dans l'ordre des numéros.</summary>
    Task<IReadOnlyList<LigneRegistre>> LireAsync(long? produitId, DateOnly du, DateOnly au, CancellationToken ct = default);
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
/// </summary>
public sealed class ConsulterRegistre(IRegistrePsychotropes registre, IPermissionService droits)
{
    public const int JoursMax = 366;

    public async Task<Registre> ExecuterAsync(long utilisateurId, long? produitId, DateOnly? du, DateOnly? au, DateOnly aujourdhui,
        CancellationToken ct = default)
    {
        await droits.ExigerAsync(utilisateurId, Permissions.PsychotropesRegistre, "consulter le registre des psychotropes", ct);
        var fin = au ?? aujourdhui;
        var debut = du ?? new DateOnly(fin.Year, fin.Month, 1);
        if (debut > fin) throw new RegleMetierException("La date de début doit précéder la date de fin.");
        if (fin.DayNumber - debut.DayNumber >= JoursMax) throw new RegleMetierException("Période limitée à un an.");

        var lignes = await registre.LireAsync(produitId, debut, fin, ct);
        var masquer = !await droits.EstTitulaireAsync(utilisateurId, ct);
        if (masquer)
            lignes = lignes.Select(l => l with
            {
                Patient = Initiales(l.Patient), PorteurNom = Initiales(l.PorteurNom),
                PorteurAdresse = l.PorteurAdresse is null ? null : "•••", PorteurPiece = l.PorteurPiece is null ? null : "•••"
            }).ToList();

        string? nomProduit = produitId is null ? null : lignes.FirstOrDefault()?.Produit
            ?? (await registre.ProduitsAsync(ct)).FirstOrDefault(p => p.Id == produitId)?.Nom;
        return new Registre(debut, fin, nomProduit, lignes, Balance(lignes), masquer);
    }

    /// <summary>Entrées, sorties et solde de fin de mois, par produit.</summary>
    public static IReadOnlyList<BalanceMensuelle> Balance(IReadOnlyList<LigneRegistre> lignes) =>
        lignes.GroupBy(l => (l.ProduitId, l.Produit, l.Date.ToLocalTime().Year, l.Date.ToLocalTime().Month))
              .Select(g => new BalanceMensuelle(g.Key.ProduitId, g.Key.Produit, g.Key.Year, g.Key.Month,
                  g.Sum(l => l.Entree ?? 0), g.Sum(l => l.Sortie ?? 0), g.OrderBy(l => l.Numero).Last().SoldeProduit))
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

/// <summary>Export du registre consulté : PDF (à imprimer et signer) ou CSV pour Excel.</summary>
public sealed class ExporterRegistre(ConsulterRegistre consulter, IDocumentsRegistre documents, Administration.IPharmacie pharmacie)
{
    public async Task<(byte[] Contenu, string NomFichier, string Type)> ExecuterAsync(string format, long utilisateurId, long? produitId,
        DateOnly? du, DateOnly? au, DateOnly aujourdhui, CancellationToken ct = default)
    {
        var r = await consulter.ExecuterAsync(utilisateurId, produitId, du, au, aujourdhui, ct);
        var nom = $"registre_psychotropes_{r.Du:yyyyMMdd}_{r.Au:yyyyMMdd}";
        return format switch
        {
            "pdf" => (documents.RegistrePsychotropes(r, (await pharmacie.LireAsync(ct)).Nom), nom + ".pdf", "application/pdf"),
            "csv" => (RegistreCsv.Ecrire(r), nom + ".csv", "text/csv"),
            _ => throw new RegleMetierException("Format d'export inconnu : pdf ou csv.")
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

    public static byte[] Ecrire(Registre r)
    {
        var sb = new StringBuilder();
        void L(params object?[] champs) => sb.AppendLine(string.Join(';', champs.Select(Champ)));
        L("Registre des psychotropes et stupéfiants", $"du {r.Du:dd/MM/yyyy} au {r.Au:dd/MM/yyyy}", r.Produit ?? "tous les produits");
        L();
        L("N° d'ordre", "Date", "Produit", "Lot", "Opération", "Entrée", "Sortie", "Solde produit", "Solde lot",
          "Patient", "Médecin", "N° ordonnance", "Grossiste", "Référence", "Porteur", "Adresse du porteur", "Pièce d'identité", "Employé");
        foreach (var l in r.Lignes)
            L(l.Numero, l.Date.ToLocalTime().ToString("dd/MM/yyyy HH:mm", Fr), l.Produit, l.NumeroLot, Libelle(l.Type), l.Entree, l.Sortie,
              l.SoldeProduit, l.SoldeLot, l.Patient, l.Medecin, l.Ordonnance, l.Fournisseur, l.Reference,
              l.PorteurNom, l.PorteurAdresse, l.PorteurPiece, l.Employe);
        L();
        L("Balance mensuelle", "Mois", "Entrées", "Sorties", "Solde fin de mois");
        foreach (var b in r.Balance) L(b.Produit, $"{b.Mois:00}/{b.Annee}", b.Entrees, b.Sorties, b.SoldeFin);
        return [.. Encoding.UTF8.GetPreamble(), .. Encoding.UTF8.GetBytes(sb.ToString())];
    }

    private static string Champ(object? v)
    {
        var s = v switch { null => "", IFormattable f => f.ToString(null, Fr), _ => v.ToString() ?? "" };
        // Une cellule qui commence par = + - @ serait lue comme une formule par Excel
        if (s.Length > 0 && "=+-@".Contains(s[0]) && v is string) s = "'" + s;
        return s.Contains(';') || s.Contains('"') || s.Contains('\n') ? $"\"{s.Replace("\"", "\"\"")}\"" : s;
    }
}
