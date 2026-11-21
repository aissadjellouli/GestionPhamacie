using MailKit.Net.Smtp;
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

    private ReglagesSmtpCentral(string hote, int port, string utilisateur, string motDePasse, string expediteur)
        => (Hote, Port, Utilisateur, MotDePasse, Expediteur) = (hote, port, utilisateur, motDePasse, expediteur);

    public string Hote { get; }
    public int Port { get; }
    public string Utilisateur { get; }
    public string MotDePasse { get; }
    public string Expediteur { get; }
    public SecureSocketOptions Securite => Port == 465 ? SecureSocketOptions.SslOnConnect : SecureSocketOptions.StartTls;

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
