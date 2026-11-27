using System.Security.Cryptography;
using System.Text;

namespace Pharma.Application.Offre;

// ---- Renforcement de l'offre (décision du titulaire du 2026-11-27, point 5 : HORLOGE + MATÉRIEL, docs/offre-et-options.md) ----
// Matériel : chaque serveur de l'officine (principal, 2e serveur, PC de secours) a une clé non exportable (TPM, sinon logicielle) ; le code
// d'activation version 2 liste les empreintes des serveurs autorisés et l'API prouve à chaque évaluation qu'elle détient l'une de ces clés.
// Horloge : l'offre se calcule avec la date effective = max(date du serveur, date plancher) ; reculer l'horloge ne prolonge rien.

/// <summary>
/// Clé de CE serveur (création par « Pharma.Api.exe --creer-cle-serveur », en administrateur ; l'API ne fait que l'ouvrir). Doublure en mémoire
/// dans les tests.
/// </summary>
public interface ICleServeur
{
    /// <summary>Clé publique (SubjectPublicKeyInfo) de la clé de ce serveur ; null si elle n'existe pas ou n'est pas lisible par ce compte.</summary>
    byte[]? ClePublique();

    /// <summary>Signature ECDSA P-256 / SHA-256 (IEEE P1363) des données ; null si la clé n'existe pas ou n'est pas lisible.</summary>
    byte[]? Signer(byte[] donnees);
}

/// <summary>
/// Empreinte d'une clé de serveur : 16 premiers octets du SHA-256 de sa clé publique (SubjectPublicKeyInfo), en hexadécimal majuscule ;
/// affichée par groupes de 4 (« 1A2B-3C4D-… », 8 groupes). Comparée sans tirets ni espaces, sans distinction de casse.
/// </summary>
public static class EmpreinteServeur
{
    public const int Caracteres = 32;

    /// <summary>Empreinte normalisée (32 caractères hexadécimaux majuscules, sans tiret).</summary>
    public static string Calculer(byte[] clePublique) => Convert.ToHexString(SHA256.HashData(clePublique))[..Caracteres];

    /// <summary>Empreinte saisie normalisée, ou null si ce n'est pas une empreinte (32 caractères hexadécimaux une fois tirets et espaces ôtés).</summary>
    public static string? Normaliser(string? saisie)
    {
        var n = new string((saisie ?? "").Where(c => c is not ('-' or ' ' or '\t')).Select(char.ToUpperInvariant).ToArray());
        return n.Length == Caracteres && n.All(char.IsAsciiHexDigitUpper) ? n : null;
    }

    /// <summary>Empreinte normalisée affichée par groupes de 4 séparés par des tirets.</summary>
    public static string Afficher(string empreinte)
    {
        var n = Normaliser(empreinte) ?? empreinte;
        return string.Join('-', Enumerable.Range(0, (n.Length + 3) / 4).Select(i => n.Substring(i * 4, Math.Min(4, n.Length - i * 4))));
    }
}

/// <summary>Résultat de la preuve de détention de la clé du serveur.</summary>
/// <param name="Empreinte">Empreinte normalisée de la clé de ce serveur, null si la clé est absente.</param>
/// <param name="Autorisee">Vrai si l'empreinte est dans la liste ET si la signature d'un défi aléatoire par cette clé est juste.</param>
public sealed record PreuveServeur(string? Empreinte, bool Autorisee);

public static class PreuveCleServeur
{
    private static readonly byte[] Domaine = Encoding.UTF8.GetBytes("PharmaOfficine.Offre.DefiServeur.v1:");

    /// <summary>Empreinte de la clé de ce serveur (null si absente), sans preuve.</summary>
    public static string? Empreinte(ICleServeur? cle) => cle?.ClePublique() is { } spki ? EmpreinteServeur.Calculer(spki) : null;

    /// <summary>
    /// Prouve que CE serveur détient l'une des clés autorisées : l'empreinte de sa clé publique doit être dans la liste, puis un défi aléatoire
    /// (32 octets, préfixés d'un domaine propre à l'offre) signé par la clé doit se vérifier avec cette clé publique. Une clé publique copiée
    /// sans la clé privée (ou une doublure qui ment sur sa clé publique) ne passe pas.
    /// </summary>
    public static PreuveServeur Prouver(ICleServeur? cle, IReadOnlyCollection<string> autorisees)
    {
        byte[]? spki;
        try { spki = cle?.ClePublique(); }
        catch (CryptographicException) { spki = null; }
        if (spki is null) return new PreuveServeur(null, false);
        var empreinte = EmpreinteServeur.Calculer(spki);
        if (!autorisees.Contains(empreinte)) return new PreuveServeur(empreinte, false);
        var defi = new byte[Domaine.Length + 32];
        Domaine.CopyTo(defi, 0);
        RandomNumberGenerator.Fill(defi.AsSpan(Domaine.Length));
        try
        {
            if (cle!.Signer(defi) is not { } signature) return new PreuveServeur(empreinte, false);
            using var ecdsa = ECDsa.Create();
            ecdsa.ImportSubjectPublicKeyInfo(spki, out _);
            return new PreuveServeur(empreinte,
                ecdsa.VerifyData(defi, signature, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation));
        }
        catch (CryptographicException) { return new PreuveServeur(empreinte, false); }
    }
}

/// <summary>
/// Plus grande date déjà vue, gardée HORS de la base dans un fichier du serveur (C:\ProgramData\PharmaOfficine\Offre\date-plancher.json,
/// lisible et modifiable par le système et les administrateurs seulement). Une erreur de lecture rend null ; une erreur d'écriture est ignorée
/// (journal du serveur) : la caisse n'est jamais bloquée.
/// </summary>
public interface IDatePlancherFichier
{
    DateTime? Lire();
    void Ecrire(DateTime utc);
}

/// <summary>Date avec laquelle l'offre se calcule (échéance, grâce, rappels).</summary>
/// <param name="ServeurUtc">Date du serveur.</param>
/// <param name="PlancherUtc">Date plancher : la plus grande de la dernière date du journal fiscal des ventes et de la date du fichier ; null si aucune.</param>
/// <param name="EffectiveUtc">max(date du serveur, date plancher).</param>
/// <param name="Aujourdhui">Jour effectif (jour du serveur, ou jour de la date plancher si elle est plus récente).</param>
/// <param name="Reculee">Date du serveur en retard de plus d'une heure sur la date plancher.</param>
public sealed record DateEffectiveOffre(DateTime ServeurUtc, DateTime? PlancherUtc, DateTime EffectiveUtc, DateOnly Aujourdhui, bool Reculee)
{
    /// <summary>Retard toléré (changement d'heure, horloge un peu dérivée) avant l'alerte au titulaire.</summary>
    public static readonly TimeSpan Tolerance = TimeSpan.FromHours(1);

    public static DateEffectiveOffre Calculer(DateTime serveurUtc, DateOnly aujourdhuiServeur, DateTime? fiscaleUtc, DateTime? fichierUtc)
    {
        DateTime? plancher = (fiscaleUtc, fichierUtc) switch
        {
            ({ } a, { } b) => a > b ? a : b,
            ({ } a, null) => a,
            (null, { } b) => b,
            _ => null
        };
        if (plancher is not { } p || p <= serveurUtc) return new DateEffectiveOffre(serveurUtc, plancher, serveurUtc, aujourdhuiServeur, false);
        var jourPlancher = DateOnly.FromDateTime(DateTime.SpecifyKind(p, DateTimeKind.Utc).ToLocalTime());
        return new DateEffectiveOffre(serveurUtc, p, p, jourPlancher > aujourdhuiServeur ? jourPlancher : aujourdhuiServeur, p - serveurUtc > Tolerance);
    }

    /// <summary>Nouvelle valeur à écrire dans le fichier (au plus une fois par jour : seulement quand le jour UTC avance), sinon null.</summary>
    public static DateTime? AEcrire(DateTime effectiveUtc, DateTime? fichierUtc) =>
        fichierUtc is { } f && DateOnly.FromDateTime(f) >= DateOnly.FromDateTime(effectiveUtc) ? null : effectiveUtc;
}
