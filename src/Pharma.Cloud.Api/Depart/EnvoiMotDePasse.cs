using MailKit.Net.Smtp;
using MailKit.Security;
using MimeKit;

namespace Pharma.Cloud.Api.Depart;

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

/// <summary>Résultat de l'envoi du mot de passe (sans le mot de passe ni le contenu du message) : affiché et noté au journal des départs.</summary>
public sealed record ResultatEnvoiMotDePasse(string Canal, string AdresseMasquee, bool Envoye, string? Raison);

/// <summary>
/// Envoi du mot de passe de l'archive au titulaire par e-mail (décision du titulaire du 2026-11-20), après création réussie de
/// l'archive : nom de l'officine, nom du fichier, début de l'empreinte SHA-256, mot de passe ; jamais l'archive ni un lien. Échec
/// non bloquant (l'archive reste valide, le mot de passe reste affiché une fois à la console) ; raison sans le mot de passe.
/// Règle des deux canaux : si un jour l'archive est remise par un lien envoyé par e-mail, le mot de passe ne doit plus partir par e-mail.
/// </summary>
public static class EnvoiMotDePasse
{
    public const string Canal = "e-mail";
    public const int LongueurEmpreinteMessage = 16;

    public static string Objet(string nomOfficine) => $"Archive de départ de l'officine {nomOfficine} : mot de passe";

    public static string Texte(string nomOfficine, ResultatExport export) =>
        $"""
        Bonjour,

        Voici le mot de passe de l'archive de départ de l'officine {nomOfficine}.

        Fichier : {Path.GetFileName(export.Chemin)}
        Début de son empreinte SHA-256 : {export.EmpreinteArchive[..LongueurEmpreinteMessage]}…
        Mot de passe : {export.MotDePasse.Valeur}

        L'archive vous est remise séparément ; ce message ne contient ni l'archive ni de lien. Ouvrez-la avec 7-Zip et ce mot de passe.
        Conservez ce mot de passe à part de l'archive.
        """;

    /// <summary>Envoie le mot de passe ; ne lève jamais d'exception (annulation comprise), renvoie le résultat à afficher et à noter.</summary>
    public static async Task<ResultatEnvoiMotDePasse> EnvoyerAsync(IEnvoiCourriel? envoi, string? reglagesManquants, string adresse,
        string nomOfficine, ResultatExport export, CancellationToken ct)
    {
        var masquee = AdresseCourriel.Masquer(adresse);
        if (envoi is null) return new(Canal, masquee, false, reglagesManquants ?? "envoi par e-mail non réglé");
        try
        {
            await envoi.EnvoyerAsync(adresse, Objet(nomOfficine), Texte(nomOfficine, export), ct);
            return new(Canal, masquee, true, null);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return new(Canal, masquee, false, "envoi annulé");
        }
        catch (Exception e)
        {
            // Raison courte, sans le mot de passe (retiré par précaution si un serveur le renvoyait dans son message)
            var raison = $"{e.GetType().Name} : {e.Message}".Replace(export.MotDePasse.Valeur, "***", StringComparison.Ordinal);
            return new(Canal, masquee, false, raison.Length > 300 ? raison[..300] : raison);
        }
    }
}
