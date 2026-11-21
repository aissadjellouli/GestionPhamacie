using MailKit.Net.Smtp;
using Microsoft.AspNetCore.DataProtection;
using MailKit.Security;
using MimeKit;

namespace Pharma.Cloud.Api.Depart;

// Fichier partagé : compilé aussi par Pharma.Cloud.Web (lien dans son .csproj) pour l'envoi des codes de second facteur.
// Aucune dépendance au reste du serveur central (ni autorité, ni base).

/// <summary>
/// Réglages SMTP du compte d'envoi de l'éditeur, lus dans les variables d'environnement du serveur central (jamais dans appsettings,
/// un journal ou un fichier) : PHARMA_CLOUD_SMTP_HOTE, _PORT, _UTILISATEUR, _MOT_DE_PASSE, _EXPEDITEUR. TLS obligatoire : port 465 =
/// TLS direct (SSL), tout autre port = STARTTLS exigé ; jamais d'envoi en clair. ToString() ne montre pas le mot de passe.
/// </summary>
public sealed class ReglagesSmtpCentral
{
    public static readonly string[] Variables =
        ["PHARMA_CLOUD_SMTP_HOTE", "PHARMA_CLOUD_SMTP_PORT", "PHARMA_CLOUD_SMTP_UTILISATEUR", "PHARMA_CLOUD_SMTP_MOT_DE_PASSE", "PHARMA_CLOUD_SMTP_EXPEDITEUR"];

    private ReglagesSmtpCentral(string hote, int port, string utilisateur, string motDePasse, string expediteur, SecureSocketOptions? securite = null)
        => (Hote, Port, Utilisateur, MotDePasse, Expediteur, securiteChoisie) = (hote, port, utilisateur, motDePasse, expediteur, securite);

    private readonly SecureSocketOptions? securiteChoisie;

    public string Hote { get; }
    public int Port { get; }
    public string Utilisateur { get; }
    public string MotDePasse { get; }
    public string Expediteur { get; }
    /// <summary>TLS obligatoire : choisi par le réglage en base (starttls | ssl), sinon d'après le port (465 = TLS direct, autre = STARTTLS).</summary>
    public SecureSocketOptions Securite => securiteChoisie ?? (Port == 465 ? SecureSocketOptions.SslOnConnect : SecureSocketOptions.StartTls);

    /// <summary>
    /// Lot S7.6 (script central 037) : réglages venus du réglage « Envoi des codes » de l'administration du site (mot de passe déjà
    /// déchiffré par l'application). TLS : « starttls » (port 587, Gmail) ou « ssl » (TLS direct, port 465) ; jamais sans TLS.
    /// Rend null et la raison (nom du champ, jamais sa valeur) si un champ manque ou est invalide.
    /// </summary>
    public static (ReglagesSmtpCentral? Reglages, string? Manque) Creer(string? hote, int? port, string? tls, string? utilisateur, string? motDePasse, string? expediteur)
    {
        var absents = new List<string>();
        if (string.IsNullOrWhiteSpace(hote)) absents.Add("serveur");
        if (port is null) absents.Add("port");
        if (string.IsNullOrWhiteSpace(utilisateur)) absents.Add("identifiant");
        if (string.IsNullOrEmpty(motDePasse)) absents.Add("mot de passe");
        if (string.IsNullOrWhiteSpace(expediteur)) absents.Add("expéditeur");
        if (absents.Count > 0) return (null, $"réglage SMTP incomplet ({string.Join(", ", absents)})");
        if (port is < 1 or > 65535) return (null, "réglage SMTP invalide (port : nombre de 1 à 65535 attendu)");
        if (!AdresseCourriel.EstValide(expediteur!.Trim())) return (null, "réglage SMTP invalide (expéditeur : adresse e-mail attendue)");
        SecureSocketOptions securite = tls switch
        {
            "starttls" => SecureSocketOptions.StartTls,
            "ssl" => SecureSocketOptions.SslOnConnect,
            _ => SecureSocketOptions.None,
        };
        if (securite == SecureSocketOptions.None) return (null, "réglage SMTP invalide (TLS : starttls ou ssl ; jamais sans TLS)");
        return (new ReglagesSmtpCentral(hote!.Trim(), port!.Value, utilisateur!.Trim(), motDePasse!, expediteur.Trim(), securite), null);
    }

    /// <summary>Réglages complets, ou null et la raison (noms des variables manquantes ou invalides, jamais leurs valeurs).</summary>
    public static (ReglagesSmtpCentral? Reglages, string? Manque) Lire(Func<string, string?> variable)
    {
        var valeurs = Variables.ToDictionary(v => v, v => variable(v)?.Trim());
        var absentes = Variables.Where(v => string.IsNullOrEmpty(valeurs[v])).ToList();
        if (absentes.Count > 0) return (null, $"réglages SMTP du central incomplets (variable(s) absente(s) : {string.Join(", ", absentes)})");
        if (!int.TryParse(valeurs["PHARMA_CLOUD_SMTP_PORT"], out var port) || port is < 1 or > 65535)
            return (null, "réglages SMTP du central invalides (PHARMA_CLOUD_SMTP_PORT : nombre de 1 à 65535 attendu)");
        if (!AdresseCourriel.EstValide(valeurs["PHARMA_CLOUD_SMTP_EXPEDITEUR"]))
            return (null, "réglages SMTP du central invalides (PHARMA_CLOUD_SMTP_EXPEDITEUR : adresse e-mail attendue)");
        return (new ReglagesSmtpCentral(valeurs["PHARMA_CLOUD_SMTP_HOTE"]!, port, valeurs["PHARMA_CLOUD_SMTP_UTILISATEUR"]!,
            variable("PHARMA_CLOUD_SMTP_MOT_DE_PASSE")!, valeurs["PHARMA_CLOUD_SMTP_EXPEDITEUR"]!), null);
    }

    public override string ToString() => $"{Hote}:{Port} ({Securite}), expéditeur {Expediteur}, mot de passe masqué";
}

/// <summary>
/// Lot S7.6 (script central 037) : chiffrement du mot de passe SMTP du réglage « Envoi des codes » par la protection des données
/// d'ASP.NET Core (fournie par le framework, aucun paquet ajouté). La base ne reçoit que le texte chiffré (base64url). Clés dans
/// &lt;dossier du central (PHARMA_CLOUD_DOSSIER)&gt;\cles-protection, HORS dépôt, comme l'autorité et la clé des jetons ; sous Windows,
/// clés elles-mêmes chiffrées par DPAPI machine (lisibles par les écrans et la console sur la même machine) ; ailleurs, protégées
/// seulement par les droits du dossier. Seul un processus qui a ce dossier de clés relit le mot de passe. Jamais journalisé.
/// </summary>
public sealed class ProtectionSmtpCentral
{
    public const string SousDossier = "cles-protection";
    private const string NomApplication = "PharmaOfficine.Central";
    private const string Usage = "PharmaOfficine.Central.EnvoiCodes.MotDePasseSmtp.v1";

    private readonly Lazy<IDataProtector> protecteur;

    public ProtectionSmtpCentral(string dossierCles)
    {
        DossierCles = Path.GetFullPath(dossierCles);
        protecteur = new Lazy<IDataProtector>(() =>
        {
            Directory.CreateDirectory(DossierCles);
            var fournisseur = DataProtectionProvider.Create(new DirectoryInfo(DossierCles), b =>
            {
                b.SetApplicationName(NomApplication);
                if (OperatingSystem.IsWindows()) b.ProtectKeysWithDpapi(protectToLocalMachine: true);
            });
            return fournisseur.CreateProtector(Usage);
        }, LazyThreadSafetyMode.ExecutionAndPublication);
    }

    public string DossierCles { get; }

    /// <summary>Protection du dossier du central, ou null si le dossier n'est pas donné ou se trouve dans un dépôt de sources.</summary>
    public static ProtectionSmtpCentral? Depuis(string? dossierCles)
    {
        if (string.IsNullOrWhiteSpace(dossierCles)) return null;
        for (var d = new DirectoryInfo(Path.GetFullPath(dossierCles)); d is not null; d = d.Parent)
            if (Directory.Exists(Path.Combine(d.FullName, ".git")) || File.Exists(Path.Combine(d.FullName, "PharmaOfficine.slnx"))) return null;
        return new ProtectionSmtpCentral(dossierCles);
    }

    /// <summary>Dossier des clés sous le dossier du central (PHARMA_CLOUD_DOSSIER), ou null.</summary>
    public static string? DossierDuCentral(string? dossierCentral) =>
        string.IsNullOrWhiteSpace(dossierCentral) ? null : Path.Combine(dossierCentral, SousDossier);

    public string Proteger(string motDePasse) => protecteur.Value.Protect(motDePasse);

    /// <summary>Mot de passe en clair, ou null si le texte ne se déchiffre pas (autre dossier de clés, texte abîmé).</summary>
    public string? Deproteger(string? chiffre)
    {
        if (string.IsNullOrEmpty(chiffre)) return null;
        try { return protecteur.Value.Unprotect(chiffre); }
        catch (System.Security.Cryptography.CryptographicException) { return null; }
        catch (FormatException) { return null; }
    }
}

/// <summary>Envoi d'un e-mail texte (petite interface : remplacée par un envoi factice dans les tests).</summary>
public interface IEnvoiCourriel
{
    Task EnvoyerAsync(string adresse, string objet, string texte, CancellationToken ct);
}

/// <summary>Envoi par le compte SMTP de l'éditeur (MailKit), TLS obligatoire, certificat du serveur vérifié.</summary>
public sealed class EnvoiCourrielSmtp(ReglagesSmtpCentral reglages) : IEnvoiCourriel
{
    public async Task EnvoyerAsync(string adresse, string objet, string texte, CancellationToken ct)
    {
        var message = new MimeMessage();
        message.From.Add(MailboxAddress.Parse(reglages.Expediteur));
        message.To.Add(MailboxAddress.Parse(adresse));
        message.Subject = objet;
        message.Body = new TextPart("plain") { Text = texte };
        using var client = new SmtpClient { Timeout = 30_000 };
        await client.ConnectAsync(reglages.Hote, reglages.Port, reglages.Securite, ct);
        await client.AuthenticateAsync(reglages.Utilisateur, reglages.MotDePasse, ct);
        await client.SendAsync(message, ct);
        await client.DisconnectAsync(true, ct);
    }
}

/// <summary>Classement d'un échec d'envoi SMTP (message à l'écran ou à la console ; jamais le détail du serveur).</summary>
public static class ErreursSmtp
{
    /// <summary>
    /// Refus de l'identifiant ou du mot de passe : AuthenticationException, ou réponse 535 / 5.7.8 (Gmail répond 535 puis coupe la
    /// connexion, que MailKit rend en SmtpProtocolException « unexpectedly disconnected »).
    /// </summary>
    public static bool RefusIdentifiants(Exception e) =>
        e is MailKit.Security.AuthenticationException
        || e is MailKit.Net.Smtp.SmtpCommandException { StatusCode: MailKit.Net.Smtp.SmtpStatusCode.AuthenticationInvalidCredentials }
        || (e is MailKit.Net.Smtp.SmtpProtocolException && (e.Message.Contains("5.7.8", StringComparison.Ordinal) || e.Message.Contains(" 535", StringComparison.Ordinal)));
}

/// <summary>Adresse e-mail saisie à la commande (--email) : contrôle de forme et forme masquée pour le journal des départs.</summary>
public static class AdresseCourriel
{
    public static bool EstValide(string? adresse) =>
        adresse is { Length: > 0 and <= 254 } a && a.Count(c => c == '@') == 1 && !a.Any(char.IsWhiteSpace)
        && a.IndexOf('@') is var i && i > 0 && a[(i + 1)..] is var domaine && domaine.Contains('.') && !domaine.StartsWith('.') && !domaine.EndsWith('.')
        && MailboxAddress.TryParse(a, out var m) && string.Equals(m.Address, a, StringComparison.Ordinal);

    /// <summary>Adresse contrôlée (espaces de bord retirés) ; refusée avant tout export si sa forme est invalide.</summary>
    public static string Valider(string adresse)
    {
        var a = adresse.Trim();
        return EstValide(a) ? a : throw new ArgumentException("Adresse e-mail du titulaire invalide (--email) : rien n'a été fait.");
    }

    /// <summary>« alice@exemple.fr » → « a***@exemple.fr » (seule forme notée au journal des départs).</summary>
    public static string Masquer(string adresse) => adresse.IndexOf('@') is > 0 and var i ? adresse[0] + "***" + adresse[i..] : "***";
}
