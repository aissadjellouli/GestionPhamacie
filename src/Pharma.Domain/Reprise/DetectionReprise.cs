using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Pharma.Domain.Reprise;

/// <summary>Ce que la reprise attend de l'ancien logiciel (tables de transit du schéma import).</summary>
public enum CibleReprise { Produits, Stock, Fournisseurs, Clients }

public enum NatureValeur { Texte, Identifiant, CodeBarre, Nombre, Date, Telephone }

/// <param name="Code">Colonne de la table de transit (ex. ref_source, nom).</param>
/// <param name="MotsCles">Mots trouvés dans les noms de colonnes des logiciels de gestion (normalisés : minuscules, sans accents).</param>
public sealed record ChampCible(string Code, string Libelle, bool Obligatoire, NatureValeur Nature, params string[] MotsCles);

public sealed record ColonneSource(string Nom, string Type, IReadOnlyList<string?> Exemples);
public sealed record TableSource(string Nom, long? Lignes, IReadOnlyList<ColonneSource> Colonnes);

/// <summary>Table proposée pour une cible et correspondance champ → colonne (null : pas trouvé). Score entre 0 et 1.</summary>
public sealed record PropositionReprise(CibleReprise Cible, string? Table, double Score, IReadOnlyDictionary<string, string?> Colonnes);

/// <summary>
/// Détection de la structure de l'ancien logiciel : les noms des tables et des colonnes, et les valeurs d'exemple
/// (codes-barres EAN-13 valides, dates, nombres, téléphones), donnent une correspondance PROPOSÉE avec un score.
/// Elle doit toujours être confirmée ou corrigée par une personne : le sens d'une colonne ne se devine pas toujours.
/// </summary>
public static partial class DetectionReprise
{
    public static readonly IReadOnlyDictionary<CibleReprise, ChampCible[]> Champs = new Dictionary<CibleReprise, ChampCible[]>
    {
        [CibleReprise.Produits] =
        [
            new("ref_source", "Identifiant du produit", true, NatureValeur.Identifiant, "code", "codeart", "code_art", "ref", "reference", "id", "idproduit", "id_produit", "cod", "num"),
            new("nom", "Désignation", true, NatureValeur.Texte, "designation", "libelle", "nom", "produit", "article", "denomination", "lib", "medicament"),
            new("code_barre", "Code-barres", false, NatureValeur.CodeBarre, "codebarre", "code_barre", "barre", "ean", "ean13", "gencod", "gtin", "cb", "codeb"),
            new("num_enregistrement", "N° d'enregistrement", false, NatureValeur.Identifiant, "enregistrement", "num_enr", "numenr", "amm", "decision", "nenr"),
            new("dosage", "Dosage", false, NatureValeur.Texte, "dosage", "dose"),
            new("forme", "Forme", false, NatureValeur.Texte, "forme", "presentation", "galenique"),
            new("prix_vente", "Prix de vente", false, NatureValeur.Nombre, "pv", "prix_vente", "prixvente", "ppa", "prix_public", "prixpublic", "pu", "prix"),
            new("prix_achat", "Prix d'achat", false, NatureValeur.Nombre, "pa", "prix_achat", "prixachat", "pachat", "cout", "pamp")
        ],
        [CibleReprise.Stock] =
        [
            new("ref_produit", "Produit", true, NatureValeur.Identifiant, "code", "codeart", "code_art", "ref", "ref_produit", "idproduit", "id_produit", "produit", "article", "cod"),
            new("quantite", "Quantité", true, NatureValeur.Nombre, "qte", "quantite", "stock", "qty", "quantity", "qt", "nb"),
            new("numero_lot", "N° de lot", false, NatureValeur.Texte, "lot", "nlot", "n_lot", "num_lot", "numlot", "nolot", "batch"),
            new("date_peremption", "Péremption", false, NatureValeur.Date, "peremption", "perem", "date_exp", "dateexp", "exp", "expiration", "ddp", "dlu", "dpe"),
            new("fournisseur_ref", "Grossiste", false, NatureValeur.Identifiant, "fournisseur", "frs", "codefrs", "code_frs", "grossiste", "idfournisseur", "fourn")
        ],
        [CibleReprise.Fournisseurs] =
        [
            new("ref_source", "Identifiant du grossiste", true, NatureValeur.Identifiant, "code", "codefrs", "code_frs", "ref", "id", "idfournisseur", "cod", "num", "numfrs", "num_frs"),
            new("raison_sociale", "Raison sociale", true, NatureValeur.Texte, "raison", "raison_sociale", "raisonsociale", "nom", "libelle", "designation", "societe"),
            new("telephone", "Téléphone", false, NatureValeur.Telephone, "tel", "telephone", "phone", "mobile", "gsm"),
            new("solde", "Solde (dette)", false, NatureValeur.Nombre, "solde", "dette", "du", "credit", "reste")
        ],
        [CibleReprise.Clients] =
        [
            new("ref_source", "Identifiant du client", true, NatureValeur.Identifiant, "code", "codecli", "code_cli", "ref", "id", "idclient", "cod", "num", "numcli", "num_cli", "nclient"),
            new("nom", "Nom", true, NatureValeur.Texte, "nom", "name", "nomclient"),
            new("prenom", "Prénom", false, NatureValeur.Texte, "prenom", "firstname"),
            new("telephone", "Téléphone", false, NatureValeur.Telephone, "tel", "telephone", "phone", "mobile", "gsm"),
            new("num_assure", "N° d'assuré", false, NatureValeur.Identifiant, "assure", "nss", "numassure", "num_assure", "chifa", "carte", "immatriculation"),
            new("solde_credit", "Crédit en cours", false, NatureValeur.Nombre, "credit", "solde", "dette", "reste", "du")
        ]
    };

    private static readonly IReadOnlyDictionary<CibleReprise, string[]> IndicesTable = new Dictionary<CibleReprise, string[]>
    {
        [CibleReprise.Produits] = ["produit", "article", "medic", "prod", "catalogue", "nomenclature", "art"],
        [CibleReprise.Stock] = ["stock", "lot", "inventaire", "lots"],
        [CibleReprise.Fournisseurs] = ["fournisseur", "frs", "grossiste", "fourn", "labo"],
        [CibleReprise.Clients] = ["client", "patient", "malade", "assure", "cli"]
    };

    /// <summary>Seuil en dessous duquel aucune table n'est proposée.</summary>
    public const double ScoreMinimum = 0.35;

    public static IReadOnlyList<PropositionReprise> Proposer(IReadOnlyList<TableSource> tables) =>
        Enum.GetValues<CibleReprise>().Select(c => ProposerPour(c, tables)).ToList();

    public static PropositionReprise ProposerPour(CibleReprise cible, IReadOnlyList<TableSource> tables)
    {
        PropositionReprise? meilleure = null;
        foreach (var t in tables)
        {
            var p = Evaluer(cible, t);
            if (meilleure is null || p.Score > meilleure.Score) meilleure = p;
        }
        return meilleure is { Score: >= ScoreMinimum }
            ? meilleure
            : new PropositionReprise(cible, null, meilleure?.Score ?? 0, Champs[cible].ToDictionary(c => c.Code, _ => (string?)null));
    }

    /// <summary>Correspondance d'une table pour une cible : chaque champ prend la meilleure colonne libre.</summary>
    public static PropositionReprise Evaluer(CibleReprise cible, TableSource table)
    {
        var champs = Champs[cible];
        var libres = table.Colonnes.ToList();
        var correspondance = new Dictionary<string, string?>();
        double total = 0, poids = 0;

        // Les champs obligatoires choisissent en premier
        foreach (var champ in champs.OrderByDescending(c => c.Obligatoire))
        {
            var (col, scoreCol) = libres.Select(c => (c, Score(champ, c))).OrderByDescending(x => x.Item2).FirstOrDefault();
            var p = champ.Obligatoire ? 2.0 : 1.0;
            poids += p;
            if (col is not null && scoreCol >= 0.4)
            {
                correspondance[champ.Code] = col.Nom;
                libres.Remove(col);
                total += p * scoreCol;
            }
            else correspondance[champ.Code] = null;
        }

        var scoreChamps = poids == 0 ? 0 : total / poids;
        var obligatoiresTrouves = champs.Where(c => c.Obligatoire).All(c => correspondance[c.Code] is not null);
        var indice = IndicesTable[cible].Any(i => Normaliser(table.Nom).Contains(i)) ? 1.0 : 0.0;
        var score = obligatoiresTrouves ? 0.75 * scoreChamps + 0.25 * indice : 0.5 * scoreChamps * 0.5;
        if (table.Lignes == 0) score *= 0.5;    // table vide : sans doute pas la bonne
        return new PropositionReprise(cible, table.Nom, Math.Round(score, 3), correspondance);
    }

    /// <summary>Score d'une colonne pour un champ : nom (mots-clés) et valeurs d'exemple.</summary>
    public static double Score(ChampCible champ, ColonneSource colonne)
    {
        var nom = Normaliser(colonne.Nom);
        var jetons = nom.Split('_', StringSplitOptions.RemoveEmptyEntries);
        var compact = nom.Replace("_", "");
        double scoreNom = 0;
        foreach (var mot in champ.MotsCles)
        {
            var m = mot.Replace("_", "");
            if (compact == m || jetons.Contains(mot)) { scoreNom = 1; break; }
            if (m.Length >= 3 && compact.Contains(m)) scoreNom = Math.Max(scoreNom, 0.7);
        }

        var valeurs = colonne.Exemples.Where(v => !string.IsNullOrWhiteSpace(v)).Select(v => v!.Trim()).ToList();
        double scoreValeurs = valeurs.Count == 0 ? 0.5 : champ.Nature switch
        {
            NatureValeur.CodeBarre => Part(valeurs, EstEan13),
            NatureValeur.Date => Math.Max(Part(valeurs, v => LireDate(v) is not null), TypeDate(colonne.Type) ? 1 : 0),
            NatureValeur.Nombre => Part(valeurs, v => LireNombre(v) is not null),
            NatureValeur.Telephone => Part(valeurs, v => Telephone().IsMatch(v.Replace(" ", "").Replace(".", "").Replace("-", ""))),
            NatureValeur.Identifiant => Part(valeurs, v => v.Length <= 30),
            _ => Part(valeurs, v => v.Any(char.IsLetter))
        };

        // Un vrai code-barres se reconnaît à ses valeurs, même sous un nom inattendu
        if (champ.Nature == NatureValeur.CodeBarre && scoreValeurs >= 0.8) scoreNom = Math.Max(scoreNom, 0.6);
        return scoreNom == 0 ? 0.2 * scoreValeurs : 0.65 * scoreNom + 0.35 * scoreValeurs;
    }

    /// <summary>Colonne de données personnelles : ses exemples sont masqués à l'écran (noms et téléphones de patients).</summary>
    public static bool EstPersonnelle(string table, string colonne)
    {
        var c = Normaliser(colonne);
        if (Regex.IsMatch(c, "(prenom|tel|phone|mobile|gsm|adresse|email|mail|assure|nss|naissance|carte|immatriculation)")) return true;
        return Regex.IsMatch(Normaliser(table), "(client|patient|malade|assure)") && Regex.IsMatch(c, "(^|_)nom");
    }

    public static string Normaliser(string s)
    {
        var d = s.Trim().ToLowerInvariant().Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder();
        foreach (var ch in d)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(ch) == UnicodeCategory.NonSpacingMark) continue;
            sb.Append(char.IsLetterOrDigit(ch) ? ch : '_');
        }
        return Regex.Replace(sb.ToString(), "_+", "_").Trim('_');
    }

    public static bool EstEan13(string v) =>
        v.Length == 13 && v.All(char.IsAsciiDigit) &&
        (10 - v.Take(12).Select((c, i) => (c - '0') * (i % 2 == 0 ? 1 : 3)).Sum() % 10) % 10 == v[12] - '0';

    public static DateOnly? LireDate(string v)
    {
        string[] formats = ["dd/MM/yyyy", "d/M/yyyy", "yyyy-MM-dd", "dd-MM-yyyy", "dd.MM.yyyy", "MM/yyyy", "yyyyMMdd", "dd/MM/yy",
                            "yyyy-MM-dd HH:mm:ss", "dd/MM/yyyy HH:mm:ss", "yyyy-MM-dd HH:mm:ss.fff"];
        return DateTime.TryParseExact(v, formats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? DateOnly.FromDateTime(d) : null;
    }

    public static decimal? LireNombre(string v)
    {
        var s = v.Replace(" ", "").Replace(" ", "").Replace("DA", "", StringComparison.OrdinalIgnoreCase);
        if (s.Contains(',') && !s.Contains('.')) s = s.Replace(',', '.');
        else if (s.Contains(',') && s.Contains('.')) s = s.Replace(",", "");
        return decimal.TryParse(s, NumberStyles.Number, CultureInfo.InvariantCulture, out var n) ? n : null;
    }

    private static bool TypeDate(string type) => Regex.IsMatch(type.ToLowerInvariant(), "date|time");

    private static double Part(List<string> valeurs, Func<string, bool> test) => valeurs.Count(test) / (double)valeurs.Count;

    [GeneratedRegex(@"^(\+213|0)[5-7]\d{8}$|^0\d{8,9}$")]
    private static partial Regex Telephone();
}
