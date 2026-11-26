using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Pharma.Application.Offre;

// ---- Offre de base et options vendues à la demande (docs/offre-et-options.md, décisions du titulaire du 2026-11-26) ----
// Une activation est signée par l'éditeur avec sa clé privée, qui n'est jamais chez la pharmacie. L'API la contrôle avec la clé publique
// intégrée à son programme (CleEditeur) : une valeur changée à la main dans la base ne passe pas la signature. L'activation est liée à une
// pharmacie précise (code de l'officine ET NIF). Format du code : « PO1.<contenu JSON en base64url>.<signature ECDSA P-256 / SHA-256 en
// base64url, format IEEE P1363> ».

/// <summary>Packs en option (docs/offre-et-options.md, proposition du 2026-11-26) ; la liste fermée des codes acceptés.</summary>
public static class PacksOffre
{
    public static readonly IReadOnlyList<(string Code, string Libelle)> Tous =
    [
        ("A", "Contrôle et anti-fraude"),
        ("B", "Inventaire et traçabilité"),
        ("C", "Achats avancés"),
        ("D", "Gestion financière"),
        ("E", "Pilotage et notifications"),
        ("F", "Continuité de service"),
        ("G", "Multi-officines et accès à distance"),
        ("H", "Confort du comptoir"),
    ];

    public static bool Connu(string code) => Tous.Any(p => p.Code == code);

    public static string Libelle(string code) => Tous.FirstOrDefault(p => p.Code == code).Libelle ?? code;
}

/// <summary>Contenu d'une activation signée par l'éditeur.</summary>
/// <param name="Officine">Code de l'officine (sync_parametre.officine_id).</param>
/// <param name="Nif">NIF de la pharmacie, normalisé (<see cref="CodeActivation.NormaliserNif"/>).</param>
/// <param name="EmiseLeUtc">Date de signature : la plus récente activation valable l'emporte ; une plus ancienne est refusée à la saisie.</param>
/// <param name="Fin">Dernier jour de l'activation (null : définitive) ; délai de grâce de 15 jours ensuite.</param>
/// <param name="Transition">Code de transition « tout compris » de la mise en place (date de fin obligatoire).</param>
public sealed record ActivationOffre(Guid Officine, string Nif, DateTime EmiseLeUtc, IReadOnlyList<string> Packs, int Postes, int ScannersMobiles,
    DateOnly? Fin, bool Transition);

/// <summary>Code d'activation refusé : signature fausse, format illisible, contenu hors limites.</summary>
public sealed class ActivationInvalideException(string message) : Exception(message);

/// <summary>Lecture et écriture du code d'activation (format PO1). La signature n'est faite que par l'outil de l'éditeur (Pharma.Editeur).</summary>
public static class CodeActivation
{
    public const string Prefixe = "PO1";
    public const int LongueurMax = 4000, PostesMin = 1, PostesMax = 200, ScannersMax = 200;

    private sealed record ContenuJson(
        [property: JsonPropertyName("v")] int Version,
        [property: JsonPropertyName("officine")] Guid Officine,
        [property: JsonPropertyName("nif")] string Nif,
        [property: JsonPropertyName("emise")] DateTime Emise,
        [property: JsonPropertyName("packs")] string[] Packs,
        [property: JsonPropertyName("postes")] int Postes,
        [property: JsonPropertyName("scanners")] int Scanners,
        [property: JsonPropertyName("fin")] DateOnly? Fin,
        [property: JsonPropertyName("transition")] bool Transition);

    /// <summary>NIF comparé sans espaces, points ni tirets, en majuscules (« 0001 2345-6 » = « 000123456 »).</summary>
    public static string NormaliserNif(string? nif) =>
        new((nif ?? "").Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray());

    /// <summary>Contrôle les valeurs d'une activation (aussi avant la signature, dans l'outil de l'éditeur).</summary>
    public static void Controler(ActivationOffre a)
    {
        if (a.Officine == Guid.Empty) throw new ActivationInvalideException("Code de l’officine absent.");
        if (NormaliserNif(a.Nif) is not { Length: >= 5 and <= 30 } n || n != a.Nif)
            throw new ActivationInvalideException("NIF absent ou mal formé (5 à 30 lettres ou chiffres, sans espace).");
        if (a.Postes is < PostesMin or > PostesMax) throw new ActivationInvalideException($"Nombre de postes : de {PostesMin} à {PostesMax}.");
        if (a.ScannersMobiles is < 0 or > ScannersMax) throw new ActivationInvalideException($"Nombre de scanners mobiles : de 0 à {ScannersMax}.");
        if (a.Packs.Any(p => !PacksOffre.Connu(p)) || a.Packs.Distinct().Count() != a.Packs.Count)
            throw new ActivationInvalideException("Pack inconnu ou en double (A à H).");
        if (a.Transition && a.Fin is null) throw new ActivationInvalideException("Un code de transition a toujours une date de fin.");
        if (a.Fin is { } f && f < DateOnly.FromDateTime(a.EmiseLeUtc)) throw new ActivationInvalideException("Date de fin antérieure à la signature.");
    }

    /// <summary>Contenu à signer (UTF-8) : JSON canonique de l'activation.</summary>
    public static byte[] Contenu(ActivationOffre a)
    {
        Controler(a);
        var c = new ContenuJson(1, a.Officine, a.Nif, DateTime.SpecifyKind(a.EmiseLeUtc, DateTimeKind.Utc), [.. a.Packs.Order(StringComparer.Ordinal)],
            a.Postes, a.ScannersMobiles, a.Fin, a.Transition);
        return JsonSerializer.SerializeToUtf8Bytes(c);
    }

    /// <summary>Assemble le code à partir du contenu et de sa signature (outil de l'éditeur).</summary>
    public static string Composer(byte[] contenu, byte[] signature) => $"{Prefixe}.{Base64Url(contenu)}.{Base64Url(signature)}";

    /// <summary>
    /// Lit un code et contrôle sa signature avec la clé publique de l'éditeur (SubjectPublicKeyInfo). Lève
    /// <see cref="ActivationInvalideException"/> si le code est illisible, si la signature est fausse ou si le contenu est hors limites.
    /// </summary>
    public static ActivationOffre Lire(string? code, byte[] clePublique)
    {
        var texte = (code ?? "").Trim();
        if (texte.Length is 0 or > LongueurMax) throw new ActivationInvalideException("Code d’activation absent ou trop long.");
        var morceaux = texte.Split('.');
        if (morceaux.Length != 3 || morceaux[0] != Prefixe) throw new ActivationInvalideException("Code d’activation illisible (format inconnu).");
        byte[] contenu, signature;
        try { (contenu, signature) = (DeBase64Url(morceaux[1]), DeBase64Url(morceaux[2])); }
        catch (FormatException) { throw new ActivationInvalideException("Code d’activation illisible (caractères invalides)."); }
        if (clePublique.Length == 0) throw new ActivationInvalideException("Clé de l’éditeur absente de ce programme : aucune activation ne peut être contrôlée.");
        using (var ecdsa = ECDsa.Create())
        {
            try { ecdsa.ImportSubjectPublicKeyInfo(clePublique, out _); }
            catch (CryptographicException) { throw new ActivationInvalideException("Clé de l’éditeur illisible dans ce programme."); }
            if (!ecdsa.VerifyData(contenu, signature, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation))
                throw new ActivationInvalideException("Signature fausse : ce code n’a pas été émis par l’éditeur ou a été modifié.");
        }
        ContenuJson? c;
        try { c = JsonSerializer.Deserialize<ContenuJson>(contenu); }
        catch (JsonException) { throw new ActivationInvalideException("Contenu du code illisible."); }
        if (c is null || c.Version != 1) throw new ActivationInvalideException("Version du code d’activation inconnue de ce programme.");
        var a = new ActivationOffre(c.Officine, c.Nif ?? "", DateTime.SpecifyKind(c.Emise, DateTimeKind.Utc), c.Packs ?? [], c.Postes, c.Scanners, c.Fin, c.Transition);
        Controler(a);
        return a;
    }

    /// <summary>Contenu d'un code SANS contrôle de la signature (outil de l'éditeur, commande « lire »).</summary>
    public static ActivationOffre LireSansControle(string code)
    {
        var morceaux = code.Trim().Split('.');
        if (morceaux.Length != 3 || morceaux[0] != Prefixe) throw new ActivationInvalideException("Code d’activation illisible (format inconnu).");
        var c = JsonSerializer.Deserialize<ContenuJson>(DeBase64Url(morceaux[1])) ?? throw new ActivationInvalideException("Contenu du code illisible.");
        return new ActivationOffre(c.Officine, c.Nif ?? "", DateTime.SpecifyKind(c.Emise, DateTimeKind.Utc), c.Packs ?? [], c.Postes, c.Scanners, c.Fin, c.Transition);
    }

    private static string Base64Url(byte[] o) => Convert.ToBase64String(o).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[] DeBase64Url(string s)
    {
        if (s.Length == 0 || s.Any(ch => !(char.IsAsciiLetterOrDigit(ch) || ch is '-' or '_'))) throw new FormatException();
        var b = s.Replace('-', '+').Replace('_', '/');
        return Convert.FromBase64String(b.PadRight(b.Length + (4 - b.Length % 4) % 4, '='));
    }
}

/// <summary>
/// Clé publique de l'éditeur (SubjectPublicKeyInfo, base64), intégrée au programme : elle ne se règle ni dans la base ni dans un fichier
/// de configuration (sinon une pharmacie pourrait la remplacer par la sienne). Générée par « Pharma.Editeur generer-cle » ; la clé
/// privée reste chez l'éditeur (fichier chiffré par mot de passe, hors de tout dépôt). Vide : aucune activation n'est acceptée (offre de base).
/// </summary>
public static class CleEditeur
{
    public const string PubliqueBase64 = "";

    public static byte[] Publique => PubliqueBase64.Length == 0 ? [] : Convert.FromBase64String(PubliqueBase64);
}
