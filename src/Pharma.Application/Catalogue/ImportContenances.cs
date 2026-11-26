using System.Globalization;
using System.Text;
using Pharma.Application.Commun;
using Pharma.Application.Financements;
using Pharma.Application.Securite;
using Pharma.Domain.Commun;

namespace Pharma.Application.Catalogue;

/// <summary>Produit retrouvé par son n° interne, son n° d'enregistrement ou son code-barres ; Contenance : celle déjà renseignée.</summary>
public sealed record ProduitIdentifie(long Id, string Nom, string? NumEnregistrement, string? CodeBarre, int? Contenance);

/// <summary>Ligne retenue par l'aperçu : contenance à écrire ; Avant : contenance actuelle (correction), vide pour un produit à compléter.</summary>
public sealed record ContenanceImportee(int Ligne, long ProduitId, string Nom, int Contenance, int? Avant = null);

/// <summary>Ligne écartée par l'aperçu : n° de ligne du fichier, produit tel qu'écrit, motif.</summary>
public sealed record LigneEcartee(int Ligne, string Produit, string Motif);

public sealed record ApercuImportContenances(IReadOnlyList<ContenanceImportee> Retenues, IReadOnlyList<LigneEcartee> Ecartees, int LignesVides);

/// <summary>
/// Stock › Contenance manquante, import d'un fichier (décision du titulaire du 2026-11-26) : Excel ou CSV, lu par ses en-têtes. Colonne
/// « Contenance » obligatoire, et au moins une colonne qui désigne le produit : « N° interne » (fichier exporté par cet écran),
/// « N° d'enregistrement » ou « Code-barres » (fichier d'un grossiste, d'un laboratoire, ou rempli à la main). Aperçu d'abord, rien n'est
/// écrit : lignes retenues, lignes écartées avec leur motif (produit inconnu, contenance invalide, déjà renseignée, deux valeurs pour un
/// même produit). Une contenance déjà renseignée n'est jamais écrasée.
/// </summary>
public static class LectureImportContenances
{
    public const int LignesMax = 20000;

    private static readonly string[] EnTetesInterne = ["n interne", "no interne", "numero interne", "id produit", "identifiant"];
    private static readonly string[] EnTetesEnregistrement = ["n d enregistrement", "no d enregistrement", "numero d enregistrement", "n enregistrement", "enregistrement", "num enregistrement", "n de decision d enregistrement"];
    private static readonly string[] EnTetesCodeBarre = ["code barres", "code barre", "codebarre", "ean", "ean13", "gtin"];

    /// <summary>Colonnes repérées dans la ligne d'en-tête (première ligne qui contient « contenance »), sinon refus explicite.</summary>
    public static (int LigneEnTete, int Contenance, int? Interne, int? Enregistrement, int? CodeBarre, int? Nom) Colonnes(IReadOnlyList<IReadOnlyList<string?>> tableau)
    {
        for (var i = 0; i < Math.Min(tableau.Count, 20); i++)
        {
            var cellules = tableau[i].Select(Normaliser).ToList();
            var contenance = cellules.FindIndex(c => c.StartsWith("contenance", StringComparison.Ordinal));
            if (contenance < 0) continue;
            int? Trouver(string[] noms) => cellules.FindIndex(c => noms.Contains(c)) is var k and >= 0 ? k : null;
            var interne = Trouver(EnTetesInterne);
            var enregistrement = Trouver(EnTetesEnregistrement);
            var code = Trouver(EnTetesCodeBarre);
            if (interne is null && enregistrement is null && code is null)
                throw new RegleMetierException("Le fichier a une colonne « Contenance », mais aucune colonne qui désigne le produit : « N° interne », « N° d’enregistrement » ou « Code-barres ».", "fichier");
            int? nom = cellules.FindIndex(c => c is "produit" or "nom" or "designation" or "nom commercial") is var n and >= 0 ? n : null;
            return (i, contenance, interne, enregistrement, code, nom);
        }
        throw new RegleMetierException("Colonne « Contenance » introuvable dans les 20 premières lignes du fichier.", "fichier");
    }

    /// <summary>Contenance lue dans une cellule : entier de 1 à 10000 (« 30 », « 30,0 » acceptés) ; null si vide ; refus sinon.</summary>
    public static (int? Valeur, string? Refus) Valeur(string? cellule)
    {
        var t = cellule?.Trim();
        if (string.IsNullOrEmpty(t)) return (null, null);
        if (!decimal.TryParse(t.Replace(',', '.'), NumberStyles.Number, CultureInfo.InvariantCulture, out var d) || d != decimal.Truncate(d))
            return (null, $"contenance « {t} » : nombre entier attendu");
        if (d is < ContenanceProduit.Minimum or > ContenanceProduit.Maximum)
            return (null, $"contenance {d:0} : de {ContenanceProduit.Minimum} à {ContenanceProduit.Maximum}");
        return ((int)d, null);
    }

    /// <summary>En-tête comparé sans accents, sans casse ni ponctuation (« N° d’enregistrement » = « n d enregistrement »).</summary>
    public static string Normaliser(string? texte)
    {
        var sans = new StringBuilder();
        foreach (var c in (texte ?? "").Normalize(NormalizationForm.FormD))
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark) sans.Append(c);
        var s = sans.ToString().ToLowerInvariant().Replace('°', ' ').Replace('’', ' ').Replace('\'', ' ').Replace('-', ' ').Replace('_', ' ').Replace('.', ' ');
        return string.Join(' ', s.Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }

    /// <summary>Lignes du fichier rapprochées des produits : retenues (produit sans contenance) et écartées, avec leur motif.</summary>
    /// <param name="corriger">Contenances déjà renseignées remplacées par celles du fichier (case cochée par le titulaire) ; sinon écartées.</param>
    public static ApercuImportContenances Rapprocher(IReadOnlyList<IReadOnlyList<string?>> tableau, IReadOnlyList<ProduitIdentifie> produits, bool corriger = false)
    {
        var (enTete, colContenance, colInterne, colEnregistrement, colCode, colNom) = Colonnes(tableau);
        var parId = produits.ToDictionary(p => p.Id);
        var parEnregistrement = produits.Where(p => !string.IsNullOrWhiteSpace(p.NumEnregistrement)).GroupBy(p => p.NumEnregistrement!.Trim().ToUpperInvariant())
            .ToDictionary(g => g.Key, g => g.ToList());
        var parCode = produits.Where(p => !string.IsNullOrWhiteSpace(p.CodeBarre)).GroupBy(p => p.CodeBarre!.Trim())
            .ToDictionary(g => g.Key, g => g.ToList());
        var retenues = new List<ContenanceImportee>();
        var ecartees = new List<LigneEcartee>();
        var vides = 0;
        string? Cellule(IReadOnlyList<string?> l, int? i) => i is { } k && k < l.Count ? l[k]?.Trim() : null;

        for (var i = enTete + 1; i < tableau.Count; i++)
        {
            var l = tableau[i];
            var numero = i + 1;
            var (valeur, refus) = Valeur(Cellule(l, colContenance));
            var designation = Cellule(l, colNom) ?? Cellule(l, colEnregistrement) ?? Cellule(l, colCode) ?? Cellule(l, colInterne) ?? "";
            if (valeur is null && refus is null) { vides++; continue; }
            if (refus is not null) { ecartees.Add(new(numero, designation, refus)); continue; }

            List<ProduitIdentifie> candidats = [];
            if (Cellule(l, colInterne) is { Length: > 0 } interne && long.TryParse(interne, out var id) && parId.TryGetValue(id, out var p1)) candidats = [p1];
            else if (Cellule(l, colEnregistrement) is { Length: > 0 } enr && parEnregistrement.TryGetValue(enr.ToUpperInvariant(), out var p2)) candidats = p2;
            else if (Cellule(l, colCode) is { Length: > 0 } code && parCode.TryGetValue(code, out var p3)) candidats = p3;
            if (candidats.Count == 0) { ecartees.Add(new(numero, designation, "produit introuvable dans le catalogue")); continue; }
            if (candidats.Count > 1) { ecartees.Add(new(numero, designation, $"{candidats.Count} produits portent ce numéro : à saisir à l’écran")); continue; }
            var p = candidats[0];
            if (p.Contenance is { } deja && deja == valeur) { ecartees.Add(new(numero, p.Nom, $"contenance identique ({deja}) : rien à changer")); continue; }
            if (p.Contenance is { } dejaRenseignee && !corriger)
            { ecartees.Add(new(numero, p.Nom, $"contenance déjà renseignée ({dejaRenseignee}) : cochez « Corriger aussi » pour la remplacer")); continue; }
            retenues.Add(new(numero, p.Id, p.Nom, valeur!.Value, p.Contenance));
        }

        // Deux valeurs différentes pour un même produit : les deux lignes sont écartées ; la même valeur deux fois : une seule ligne
        var conflits = retenues.GroupBy(r => r.ProduitId).Where(g => g.Select(r => r.Contenance).Distinct().Count() > 1).Select(g => g.Key).ToHashSet();
        foreach (var r in retenues.Where(r => conflits.Contains(r.ProduitId)))
            ecartees.Add(new(r.Ligne, r.Nom, "plusieurs contenances différentes pour ce produit dans le fichier"));
        var finales = retenues.Where(r => !conflits.Contains(r.ProduitId)).GroupBy(r => r.ProduitId).Select(g => g.First()).ToList();
        return new ApercuImportContenances(finales, ecartees.OrderBy(e => e.Ligne).ToList(), vides);
    }

    /// <summary>Fichier exporté par l'écran (CSV « ; », UTF-8 avec BOM : Excel l'ouvre tel quel) : la colonne Contenance est à remplir.</summary>
    public static byte[] Exporter(IEnumerable<(ContenanceManquante Produit, string? NumEnregistrement, string? CodeBarre)> produits)
    {
        static string Champ(string? v) => v is null ? "" : v.Contains(';') || v.Contains('"') || v.Contains('\n') ? $"\"{v.Replace("\"", "\"\"")}\"" : v;
        var sb = new StringBuilder();
        sb.Append("N° interne;N° d'enregistrement;Code-barres;Produit;Conditionnement;Vendues (12 mois);Contenance\r\n");
        foreach (var (p, enr, code) in produits)
            sb.Append(string.Join(';', p.ProduitId.ToString(CultureInfo.InvariantCulture), Champ(enr), Champ(code), Champ(p.Nom), Champ(p.Conditionnement),
                p.Vendues12Mois.ToString(CultureInfo.InvariantCulture), "")).Append("\r\n");
        return [.. Encoding.UTF8.GetPreamble(), .. Encoding.UTF8.GetBytes(sb.ToString())];
    }
}

/// <summary>Port de l'import : produits à rapprocher et produits à exporter.</summary>
public interface IContenancesImport
{
    /// <summary>Produits actifs ou non, avec leurs identifiants et leur contenance (rapprochement d'un fichier).</summary>
    Task<IReadOnlyList<ProduitIdentifie>> ProduitsAsync(CancellationToken ct = default);
    /// <summary>Produits actifs sans contenance, les plus vendus d'abord, avec n° d'enregistrement et code-barres (export).</summary>
    Task<IReadOnlyList<(ContenanceManquante Produit, string? NumEnregistrement, string? CodeBarre)>> AExporterAsync(CancellationToken ct = default);
}

/// <summary>Export, aperçu et enregistrement de l'import (même droit que la fiche produit : catalogue.creer).</summary>
public sealed class ImporterContenances(IContenancesImport import, IContenancesManquantes depot, ILecteurTableau lecteur, IPermissionService droits,
    IJournal journal)
{
    public const int TailleMax = 10 * 1024 * 1024;
    public const string Motif = "import d’un fichier (Contenances)";

    public async Task<byte[]> ExporterAsync(long utilisateurId, CancellationToken ct = default)
    {
        await droits.ExigerAsync(utilisateurId, Domain.Securite.Permissions.CatalogueCreer, "exporter les contenances manquantes", ct);
        return LectureImportContenances.Exporter(await import.AExporterAsync(ct));
    }

    public async Task<ApercuImportContenances> ApercuAsync(long utilisateurId, string? nomFichier, byte[] contenu, CancellationToken ct = default, bool corriger = false)
    {
        await droits.ExigerAsync(utilisateurId, Domain.Securite.Permissions.CatalogueCreer, "importer des contenances", ct);
        if (contenu.Length == 0) throw new RegleMetierException("Fichier vide.", "fichier");
        if (contenu.Length > TailleMax) throw new RegleMetierException("Fichier trop volumineux (10 Mo au plus).", "fichier");
        var extension = Path.GetExtension(nomFichier ?? "").ToLowerInvariant();
        if (extension is not (".xlsx" or ".csv" or ".txt")) throw new RegleMetierException("Fichier Excel (.xlsx) ou CSV attendu.", "fichier");
        var tableau = lecteur.Lire(nomFichier!, contenu);
        if (tableau.Count > LectureImportContenances.LignesMax + 1) throw new RegleMetierException($"Fichier trop long ({LectureImportContenances.LignesMax} lignes au plus).", "fichier");
        return LectureImportContenances.Rapprocher(tableau, await import.ProduitsAsync(ct), corriger);
    }

    /// <summary>
    /// Écrit les contenances retenues par l'aperçu (Avant : valeur lue par l'aperçu, vide pour compléter) ; un produit changé entre-temps
    /// est compté à part, jamais écrasé.
    /// </summary>
    public async Task<(int Ecrites, int DejaRenseignees)> EnregistrerAsync(long utilisateurId, IReadOnlyList<(long ProduitId, int Contenance, int? Avant)> lignes,
        CancellationToken ct = default)
    {
        await droits.ExigerAsync(utilisateurId, Domain.Securite.Permissions.CatalogueCreer, "importer des contenances", ct);
        if (lignes.Count == 0) throw new RegleMetierException("Aucune contenance à enregistrer.");
        if (lignes.Count > LectureImportContenances.LignesMax) throw new RegleMetierException("Trop de lignes en une fois.");
        if (lignes.FirstOrDefault(l => !ContenanceProduit.SaisieValide(l.Contenance)) is { ProduitId: > 0 } faux)
            throw new RegleMetierException($"Contenance invalide ({faux.Contenance}) : de {ContenanceProduit.Minimum} à {ContenanceProduit.Maximum}.");
        var (ecrites, deja) = (0, 0);
        foreach (var (produitId, contenance, avant) in lignes.DistinctBy(l => l.ProduitId))
        {
            var ecrit = avant is null ? await depot.EnregistrerSiVideAsync(produitId, contenance, ct) : await depot.CorrigerAsync(produitId, avant, contenance, ct);
            if (ecrit is not { } nom) { deja++; continue; }
            ecrites++;
            await journal.EcrireAsync(utilisateurId, "produit_modifie", "produit", produitId, new Dictionary<string, object?>
            {
                ["produit"] = nom,
                ["contenance"] = new Dictionary<string, object?> { ["avant"] = avant, ["apres"] = contenance },
                ["motif"] = Motif
            }, Domain.Securite.Permissions.CatalogueCreer, ct);
        }
        return (ecrites, deja);
    }
}
