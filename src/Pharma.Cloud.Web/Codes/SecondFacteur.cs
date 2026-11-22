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
    /// <summary>Canal noté en base : fichier, email, sms ou les_deux (script central 037).</summary>
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

/// <summary>Production : e-mail par le compte SMTP du réglage « Envoi des codes » (script central 037, TLS obligatoire).</summary>
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

/// <summary>SMS : interface prévue, AUCUN fournisseur retenu ; chaque envoi est refusé (message clair et journal des accès).</summary>
public sealed class EnvoiCodeSmsIndisponible : IEnvoiCodeDistant
{
    public string Canal => "sms";

    public Task EnvoyerAsync(DestinataireCode destinataire, string code, CancellationToken ct) =>
        throw new NotSupportedException("Envoi par SMS non disponible : aucun fournisseur retenu.");
}

/// <summary>Résultat d'un envoi sur un canal (fichier, email, sms) : ok, echec, indisponible ou sans_contact ; type d'erreur pour le journal.</summary>
public sealed record ResultatEnvoiCode(string Canal, string Resultat, string? TypeErreur = null)
{
    public bool Parti => Resultat == "ok";

    /// <summary>Objet noté au journal des accès (liste fermée du script central 037) : « canal:résultat ».</summary>
    public string Objet => $"{Canal}:{Resultat}";
}

/// <summary>
/// Canal « les deux » (comme le super admin du poste : avec les deux, chaque code part sur les deux) : e-mail à l'adresse du compte
/// ET SMS au numéro du compte. Un canal en échec (SMS indisponible, compte sans numéro) n'empêche pas l'autre ; refus seulement
/// si aucun n'est parti.
/// </summary>
public sealed class EnvoiCodeLesDeux(IEnvoiCodeDistant courriel, IEnvoiCodeDistant sms) : IEnvoiCodeDistant
{
    public string Canal => "les_deux";

    public async Task EnvoyerAsync(DestinataireCode destinataire, string code, CancellationToken ct)
    {
        if (!(await EnvoyerChacunAsync(destinataire, code, ct)).Any(r => r.Parti))
            throw new InvalidOperationException("Code parti sur aucun canal (e-mail et SMS).");
    }

    public async Task<IReadOnlyList<ResultatEnvoiCode>> EnvoyerChacunAsync(DestinataireCode destinataire, string code, CancellationToken ct) =>
    [
        destinataire.Email is { Length: > 0 } ? await EnvoisCodes.UnAsync(courriel, destinataire, code, ct) : new ResultatEnvoiCode(courriel.Canal, "sans_contact"),
        destinataire.Telephone is { Length: > 0 } ? await EnvoisCodes.UnAsync(sms, destinataire, code, ct) : new ResultatEnvoiCode(sms.Canal, "sans_contact"),
    ];
}

/// <summary>
/// DÉVELOPPEMENT seulement (mode fichier) : le code est TOUJOURS écrit dans le fichier, PUIS envoyé par le canal du réglage en base
/// (e-mail) ; un envoi en échec est noté au journal de l'application sans bloquer la connexion (le fichier suffit en développement).
/// </summary>
public sealed class EnvoiCodeAvecCopieFichier(IEnvoiCodeDistant envoi, IEnvoiCodeDistant fichier, ILogger? journal = null) : IEnvoiCodeDistant
{
    public string Canal => envoi.Canal;

    public async Task EnvoyerAsync(DestinataireCode destinataire, string code, CancellationToken ct)
    {
        await fichier.EnvoyerAsync(destinataire, code, ct);
        foreach (var r in await EnvoisCodes.EnvoyerAsync(envoi, destinataire, code, ct))
            if (!r.Parti) journal?.LogWarning("Code de développement écrit dans le fichier, envoi {Objet} non parti ({Type})", r.Objet, r.TypeErreur ?? "-");
    }
}

/// <summary>Envoi d'un code sur le canal retenu, résultat par canal (jamais le code ni le contact dans le résultat).</summary>
public static class EnvoisCodes
{
    /// <summary>
    /// Délai propre d'un envoi (2026-11-22) : une fois le code créé, son envoi ne dépend plus de la requête du navigateur (deuxième
    /// clic, rechargement, pause du débogueur annulaient l'envoi en plein échange avec le serveur d'e-mail) ; au-delà, « echec ».
    /// </summary>
    public static readonly TimeSpan DelaiMaximum = TimeSpan.FromSeconds(30);

    public static async Task<IReadOnlyList<ResultatEnvoiCode>> EnvoyerAsync(IEnvoiCodeDistant envoi, DestinataireCode destinataire, string code, CancellationToken ct) =>
        envoi is EnvoiCodeLesDeux deux ? await deux.EnvoyerChacunAsync(destinataire, code, ct) : [await UnAsync(envoi, destinataire, code, ct)];

    internal static async Task<ResultatEnvoiCode> UnAsync(IEnvoiCodeDistant envoi, DestinataireCode destinataire, string code, CancellationToken ct)
    {
        try
        {
            await envoi.EnvoyerAsync(destinataire, code, ct);
            return new ResultatEnvoiCode(envoi.Canal, "ok");
        }
        catch (NotSupportedException e)
        {
            return new ResultatEnvoiCode(envoi.Canal, "indisponible", e.GetType().Name);
        }
        catch (OperationCanceledException e)
        {
            // Délai dépassé (jeton propre de l'envoi) : échec ordinaire, noté au journal comme les autres
            return new ResultatEnvoiCode(envoi.Canal, "echec", e.GetType().Name);
        }
        catch (Exception e)
        {
            return new ResultatEnvoiCode(envoi.Canal, "echec", e.GetType().Name);
        }
    }
}
