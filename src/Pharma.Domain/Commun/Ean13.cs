namespace Pharma.Domain.Commun;

/// <summary>Code EAN-13. Les codes internes à l'officine commencent par 20 (préfixe GS1 réservé).</summary>
public readonly record struct Ean13
{
    public string Valeur { get; }

    private Ean13(string valeur) => Valeur = valeur;

    public bool EstInterne => Valeur.StartsWith("20", StringComparison.Ordinal);

    public static char Cle(string douzeChiffres)
    {
        if (douzeChiffres.Length != 12 || !douzeChiffres.All(char.IsAsciiDigit))
            throw new ArgumentException("Il faut exactement 12 chiffres.", nameof(douzeChiffres));

        var somme = 0;
        for (var i = 0; i < 12; i++)
            somme += (douzeChiffres[i] - '0') * (i % 2 == 0 ? 1 : 3);
        return (char)('0' + (10 - somme % 10) % 10);
    }

    public static bool EstValide(string? code) =>
        code is { Length: 13 } && code.All(char.IsAsciiDigit) && Cle(code[..12]) == code[12];

    public static Ean13 Parse(string code) =>
        EstValide(code) ? new Ean13(code) : throw new FormatException($"EAN-13 invalide : {code}");

    public static Ean13 Interne(long sequence)
    {
        if (sequence is < 0 or > 9_999_999_999)
            throw new ArgumentOutOfRangeException(nameof(sequence));
        var base12 = "20" + sequence.ToString("D10");
        return new Ean13(base12 + Cle(base12));
    }

    public override string ToString() => Valeur;
}
