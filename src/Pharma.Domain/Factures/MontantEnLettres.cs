namespace Pharma.Domain.Factures;

/// <summary>Montant en toutes lettres, en français : « mille deux cent cinquante dinars et cinquante centimes ».</summary>
public static class MontantEnLettres
{
    private static readonly string[] Unites =
    [
        "zéro", "un", "deux", "trois", "quatre", "cinq", "six", "sept", "huit", "neuf", "dix",
        "onze", "douze", "treize", "quatorze", "quinze", "seize", "dix-sept", "dix-huit", "dix-neuf"
    ];

    private static readonly string[] Dizaines = ["", "", "vingt", "trente", "quarante", "cinquante", "soixante", "soixante", "quatre-vingt", "quatre-vingt"];

    public static string Dinars(decimal montant)
    {
        if (montant < 0) return "moins " + Dinars(-montant);
        var arrondi = decimal.Round(montant, 2);
        var entier = (long)decimal.Truncate(arrondi);
        var centimes = (int)((arrondi - entier) * 100);
        var texte = $"{Nombre(entier)} dinar{(entier > 1 ? "s" : "")}";
        if (centimes > 0) texte += $" et {Nombre(centimes)} centime{(centimes > 1 ? "s" : "")}";
        return char.ToUpperInvariant(texte[0]) + texte[1..];
    }

    public static string Nombre(long n)
    {
        if (n == 0) return "zéro";
        var morceaux = new List<string>();
        var milliards = n / 1_000_000_000; n %= 1_000_000_000;
        var millions = n / 1_000_000; n %= 1_000_000;
        var milliers = n / 1000; n %= 1000;
        if (milliards > 0) morceaux.Add($"{Centaines((int)milliards, false)} milliard{(milliards > 1 ? "s" : "")}");
        if (millions > 0) morceaux.Add($"{Centaines((int)millions, false)} million{(millions > 1 ? "s" : "")}");
        if (milliers > 0) morceaux.Add(milliers == 1 ? "mille" : $"{Centaines((int)milliers, false)} mille");
        if (n > 0) morceaux.Add(Centaines((int)n, true));
        return string.Join(" ", morceaux);
    }

    /// <summary>0 à 999. « cents » et « quatre-vingts » ne prennent le s qu'en fin de nombre (finale).</summary>
    private static string Centaines(int n, bool finale)
    {
        var c = n / 100;
        var reste = n % 100;
        var parts = new List<string>();
        if (c > 0)
            parts.Add(c == 1 ? "cent" : $"{Unites[c]} cent{(reste == 0 && finale ? "s" : "")}");
        if (reste > 0) parts.Add(MoinsDeCent(reste, finale));
        return string.Join(" ", parts);
    }

    private static string MoinsDeCent(int n, bool finale)
    {
        if (n < 20) return Unites[n];
        var d = n / 10;
        var u = n % 10;
        if (d is 7 or 9) u += 10; // soixante-dix, quatre-vingt-dix…
        var dizaine = Dizaines[d];
        if (u == 0) return d == 8 && finale ? "quatre-vingts" : dizaine;
        if ((u == 1 || u == 11) && d < 8) return $"{dizaine} et {Unites[u]}";
        return $"{dizaine}-{Unites[u]}";
    }
}
