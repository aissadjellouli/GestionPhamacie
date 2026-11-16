using System.Globalization;
using Pharma.Domain.Reglages;

namespace Pharma.Cloud.Api.Reglages;

/// <summary>Commande de l'outil d'administration des réglages mutualisés, après analyse des arguments.</summary>
public sealed record CommandeReglages(string Nom, IReadOnlyList<string> Positions, IReadOnlyDictionary<string, string> Options, IReadOnlySet<string> Drapeaux)
{
    public string? Option(string nom) => Options.TryGetValue(nom, out var v) ? v : null;

    public bool Drapeau(string nom) => Drapeaux.Contains(nom);

    /// <summary>Auteur obligatoire (D10) : 1 à 100 caractères.</summary>
    public string Auteur()
    {
        var a = Option("--auteur")?.Trim();
        if (string.IsNullOrEmpty(a)) throw new ArgumentException($"L'auteur est obligatoire : --auteur <nom> (1 à {ValidationModele.AuteurMax} caractères).");
        if (a.Length > ValidationModele.AuteurMax) throw new ArgumentException($"L'auteur est trop long ({a.Length} caractères, {ValidationModele.AuteurMax} au plus).");
        return a;
    }

    /// <summary>Motif facultatif (500 caractères au plus, limite du journal central).</summary>
    public string? Motif()
    {
        var m = Option("--motif")?.Trim();
        if (m is { Length: > 500 }) throw new ArgumentException("Le motif est trop long (500 caractères au plus).");
        return string.IsNullOrEmpty(m) ? null : m;
    }

    public string Table(int position = 0) => Positions[position];

    public Guid Officine(int position = 0) =>
        Guid.TryParse(Positions[position], out var g) ? g : throw new ArgumentException("Code officine (uuid) attendu.");
}

/// <summary>Analyse des arguments des commandes de réglages mutualisés (lot M6). Aucune connexion, aucune écriture.</summary>
public static class ArgumentsReglages
{
    private sealed record Spec(int PositionsMin, int PositionsMax, string[] Valeurs, string[] Drapeaux, string Usage);

    private static readonly Dictionary<string, Spec> Specs = new(StringComparer.Ordinal)
    {
        ["--reglages-mutualises"] = new(0, 1, [], [], "--reglages-mutualises [table]"),
        ["--publier-reglage"] = new(1, 1, ["--fichier", "--auteur", "--motif"], ["--simulation", "--oui"],
            "--publier-reglage <table> --fichier <json> --auteur <nom> [--motif <texte>] [--simulation] [--oui]"),
        ["--mode-reglage"] = new(2, 2, ["--auteur", "--motif"], ["--oui"], "--mode-reglage <table> <impose|propose|local> --auteur <nom> [--motif <texte>] [--oui]"),
        ["--modele-depuis-officine"] = new(2, 2, ["--fichier", "--mode"], ["--ecraser"],
            "--modele-depuis-officine <code officine> <table> --fichier <sortie> [--mode propose|impose] [--ecraser]"),
        ["--liberer-reglages"] = new(1, 1, ["--auteur", "--motif"], [], "--liberer-reglages <code officine> --auteur <nom> [--motif <texte>]"),
        ["--retablir-reglages"] = new(1, 1, ["--auteur", "--motif"], [], "--retablir-reglages <code officine> --auteur <nom> [--motif <texte>]"),
        ["--journal-reglages"] = new(0, 1, ["--depuis", "--limite"], [], "--journal-reglages [table] [--depuis AAAA-MM-JJ] [--limite <n>]")
    };

    public static IReadOnlyCollection<string> Commandes => Specs.Keys;

    public static bool Concerne(string commande) => Specs.ContainsKey(commande);

    public static CommandeReglages Analyser(string[] args)
    {
        if (args.Length == 0 || !Specs.TryGetValue(args[0], out var spec))
            throw new ArgumentException("Commande de réglages mutualisés inconnue.");
        var positions = new List<string>();
        var options = new Dictionary<string, string>(StringComparer.Ordinal);
        var drapeaux = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 1; i < args.Length; i++)
        {
            var t = args[i];
            if (!t.StartsWith("--", StringComparison.Ordinal)) { positions.Add(t); continue; }
            if (spec.Drapeaux.Contains(t)) { drapeaux.Add(t); continue; }
            if (!spec.Valeurs.Contains(t)) throw new ArgumentException($"Option inconnue : {t}. Attendu : {args[0]}{Suite(spec)}");
            if (i + 1 >= args.Length || args[i + 1].StartsWith("--", StringComparison.Ordinal))
                throw new ArgumentException($"Une valeur est attendue après {t}.");
            if (!options.TryAdd(t, args[++i])) throw new ArgumentException($"L'option {t} est donnée deux fois.");
        }
        if (positions.Count < spec.PositionsMin || positions.Count > spec.PositionsMax)
            throw new ArgumentException($"Arguments attendus : {spec.Usage}");
        return new CommandeReglages(args[0], positions, options, drapeaux);
    }

    private static string Suite(Spec s) => s.Usage[s.Usage.IndexOf(' ')..];

    /// <summary>Table de la liste fermée des 11 réglages mutualisables, sinon erreur avec la liste.</summary>
    public static string ExigerTable(string table) =>
        TablesMutualisables.Connue(table)
            ? table
            : throw new ArgumentException($"Table « {table} » non mutualisable. Tables possibles : {string.Join(", ", TablesMutualisables.Toutes)}.");

    public static DateTimeOffset? Depuis(string? valeur) =>
        valeur is null ? null
        : DateOnly.TryParseExact(valeur, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)
            ? new DateTimeOffset(d.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero)
            : throw new ArgumentException("Date attendue après --depuis : AAAA-MM-JJ.");

    public static int Limite(string? valeur) =>
        valeur is null ? 200
        : int.TryParse(valeur, NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n is >= 1 and <= 10000 ? n
        : throw new ArgumentException("Nombre attendu après --limite : de 1 à 10000.");
}
