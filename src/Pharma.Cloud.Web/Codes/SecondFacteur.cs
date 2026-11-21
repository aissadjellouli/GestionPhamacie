using System.Security.Cryptography;
using System.Text;
using Pharma.Cloud.Api.Depart;
using Pharma.Shared;

namespace Pharma.Cloud.Web.Codes;

/// <summary>
/// Codes de second facteur : 6 chiffres tirés au sort ; seule leur empreinte HMAC-SHA256 part en base. La clé HMAC vit dans la
/// mémoire du processus des écrans (tirée au démarrage) : une base copiée ne permet pas de retrouver un code par essais.
/// Conséquence : un redémarrage des écrans annule les codes en cours (5 min au plus). [À DÉCIDER à la mise en ligne] : clé
/// commune si plusieurs instances des écrans.
/// </summary>
public sealed class CodesSecondFacteur
{
    private readonly byte[] cle;

    public CodesSecondFacteur() : this(RandomNumberGenerator.GetBytes(32)) { }

    public CodesSecondFacteur(byte[] cle)
    {
        if (cle.Length < 32) throw new ArgumentException("Clé HMAC de 32 octets au moins.", nameof(cle));
        this.cle = cle;
    }

    public static string Tirer() => RandomNumberGenerator.GetInt32(0, 1_000_000).ToString("D6", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>Empreinte HMAC du code pour un objet (« connexion » ou « action ») ; null si le code saisi n'a pas la forme de 6 chiffres.</summary>
    public string? Empreinte(string objet, string? code)
    {
        var c = code?.Replace(" ", "", StringComparison.Ordinal).Trim();
        if (c is not { Length: 6 } || !c.All(char.IsAsciiDigit)) return null;
        return Convert.ToHexStringLower(HMACSHA256.HashData(cle, Encoding.UTF8.GetBytes(objet + "|" + c)));
    }
}

/// <summary>Destinataire d'un code (contact du compte, rendu par la base pour l'envoi seulement).</summary>
public sealed record DestinataireCode(string Identifiant, string? Email, string? Telephone);

/// <summary>Envoi d'un code de second facteur. Jamais le code dans un journal ni sur la console.</summary>
public interface IEnvoiCodeDistant
{
    /// <summary>Canal noté en base : fichier, email ou sms.</summary>
    string Canal { get; }

    Task EnvoyerAsync(DestinataireCode destinataire, string code, CancellationToken ct);
}

/// <summary>
/// DÉVELOPPEMENT SEULEMENT (Development et boucle locale, contrôlé par ReglagesEcrans) : le code est écrit dans
/// &lt;dossier&gt;\&lt;identifiant&gt;.txt (par défaut %LOCALAPPDATA%\PharmaOfficine\central-dev\codes), remplacé à chaque envoi.
/// </summary>
public sealed class EnvoiCodeFichier(string dossier, TimeProvider horloge) : IEnvoiCodeDistant
{
    public string Canal => "fichier";

    public async Task EnvoyerAsync(DestinataireCode destinataire, string code, CancellationToken ct)
    {
        if (!ComptesDistants.IdentifiantValide(destinataire.Identifiant))
            throw new ArgumentException("Identifiant invalide pour un nom de fichier.", nameof(destinataire));
        Directory.CreateDirectory(dossier);
        var chemin = Path.Combine(dossier, destinataire.Identifiant + ".txt");
        if (!string.Equals(Path.GetDirectoryName(Path.GetFullPath(chemin)), Path.GetFullPath(dossier).TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Chemin du code hors du dossier des codes.", nameof(destinataire));
        await File.WriteAllTextAsync(chemin,
            $"Code de second facteur des écrans du central (DÉVELOPPEMENT) : {code}{Environment.NewLine}" +
            $"Écrit le {horloge.GetLocalNow():yyyy-MM-dd HH:mm:ss}, valable 5 minutes, usage unique.{Environment.NewLine}", ct);
    }
}

/// <summary>Production : e-mail par le compte SMTP du central (réglages PHARMA_CLOUD_SMTP_*, TLS obligatoire).</summary>
public sealed class EnvoiCodeCourriel(IEnvoiCourriel courriel) : IEnvoiCodeDistant
{
    public string Canal => "email";

    public Task EnvoyerAsync(DestinataireCode destinataire, string code, CancellationToken ct) =>
        destinataire.Email is { Length: > 0 } adresse
            ? courriel.EnvoyerAsync(adresse, "Code de connexion aux écrans du central",
                $"""
                Bonjour,

                Votre code : {code}
                Il est valable 5 minutes et ne sert qu'une fois.

                Si vous n'êtes pas à l'origine de cette demande, ne communiquez ce code à personne et prévenez l'éditeur.
                """, ct)
            : throw new InvalidOperationException("Compte sans adresse e-mail.");
}

/// <summary>SMS : interface prévue, AUCUN fournisseur retenu (refusé au démarrage par ReglagesEcrans).</summary>
public sealed class EnvoiCodeSmsIndisponible : IEnvoiCodeDistant
{
    public string Canal => "sms";

    public Task EnvoyerAsync(DestinataireCode destinataire, string code, CancellationToken ct) =>
        throw new NotSupportedException("Envoi par SMS non disponible : aucun fournisseur retenu.");
}
