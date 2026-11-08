using System.IO.Ports;
using System.Security.Cryptography;
using System.Text;
using MailKit.Net.Smtp;
using MailKit.Security;
using MimeKit;
using Pharma.Application.Notifications;

namespace Pharma.Infrastructure.Notifications;

/// <summary>
/// Chiffrement DPAPI, portée machine : seul le PC serveur qui a enregistré le mot de passe peut le relire.
/// Après restauration de la base sur un autre PC, le mot de passe SMTP est à ressaisir.
/// </summary>
public sealed class ProtectionDpapi : IProtectionSecret
{
    private static readonly byte[] Entropie = "PharmaOfficine.Notifications.Smtp"u8.ToArray();

    public byte[] Chiffrer(string secret)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Chiffrement DPAPI disponible sous Windows seulement.");
        return ProtectedData.Protect(Encoding.UTF8.GetBytes(secret), Entropie, DataProtectionScope.LocalMachine);
    }

    public string Dechiffrer(byte[] chiffre)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Chiffrement DPAPI disponible sous Windows seulement.");
        try
        {
            return Encoding.UTF8.GetString(ProtectedData.Unprotect(chiffre, Entropie, DataProtectionScope.LocalMachine));
        }
        catch (CryptographicException)
        {
            throw new InvalidOperationException("Mot de passe SMTP illisible sur ce PC (base restaurée ailleurs ?) : ressaisissez-le dans l'administration.");
        }
    }
}

/// <summary>Envoi par le serveur SMTP de la pharmacie (MailKit) ; aucun service cloud.</summary>
public sealed class EnvoiEmailMailKit : IEnvoiEmail
{
    public async Task EnvoyerAsync(ConnexionSmtp c, string adresse, string objet, string texte, string? html, CancellationToken ct = default)
    {
        var message = new MimeMessage();
        message.From.Add(new MailboxAddress("Pharma Officine", c.Expediteur));
        message.To.Add(MailboxAddress.Parse(adresse));
        message.Subject = objet;
        message.Body = new BodyBuilder { TextBody = texte, HtmlBody = html }.ToMessageBody();

        using var client = new SmtpClient { Timeout = 30_000 };
        // Port 465 : TLS direct ; sinon STARTTLS exigé si TLS demandé
        var securite = !c.Tls ? SecureSocketOptions.None : c.Port == 465 ? SecureSocketOptions.SslOnConnect : SecureSocketOptions.StartTls;
        await client.ConnectAsync(c.Serveur, c.Port, securite, ct);
        if (!string.IsNullOrEmpty(c.Identifiant))
            await client.AuthenticateAsync(c.Identifiant, c.MotDePasse ?? "", ct);
        await client.SendAsync(message, ct);
        await client.DisconnectAsync(true, ct);
    }
}

/// <summary>
/// SMS par modem GSM (commandes AT, mode texte) sur un port série. Un seul envoi à la fois.
/// Fonctionne sans internet : seul le réseau GSM est utilisé.
/// </summary>
public sealed class EnvoiSmsModemGsm : IEnvoiSms
{
    private static readonly SemaphoreSlim Verrou = new(1, 1);
    private const char CtrlZ = (char)26;

    public async Task EnvoyerAsync(string portSerie, string numero, string texte, CancellationToken ct = default)
    {
        await Verrou.WaitAsync(ct);
        try
        {
            using var port = new SerialPort(portSerie, 115200, Parity.None, 8, StopBits.One)
            {
                NewLine = "\r", ReadTimeout = 10_000, WriteTimeout = 10_000, Encoding = Encoding.ASCII, DtrEnable = true, RtsEnable = true
            };
            port.Open();
            await Commande(port, "AT", "OK", ct);
            await Commande(port, "AT+CMGF=1", "OK", ct);          // mode texte
            await Commande(port, "AT+CSCS=\"GSM\"", "OK", ct);    // jeu de caractères GSM (texte déjà sans accents)
            port.Write($"AT+CMGS=\"{numero}\"\r");
            await Attendre(port, ">", TimeSpan.FromSeconds(10), ct);
            port.Write(texte + CtrlZ);
            await Attendre(port, "+CMGS", TimeSpan.FromSeconds(60), ct);   // l'opérateur peut mettre du temps à répondre
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or TimeoutException or InvalidOperationException)
        {
            throw new InvalidOperationException($"Modem GSM ({portSerie}) indisponible : {ex.Message}", ex);
        }
        finally
        {
            Verrou.Release();
        }
    }

    private static async Task Commande(SerialPort port, string commande, string attendu, CancellationToken ct)
    {
        port.DiscardInBuffer();
        port.Write(commande + "\r");
        await Attendre(port, attendu, TimeSpan.FromSeconds(5), ct);
    }

    private static async Task Attendre(SerialPort port, string attendu, TimeSpan delai, CancellationToken ct)
    {
        var lu = new StringBuilder();
        var limite = DateTime.UtcNow + delai;
        while (DateTime.UtcNow < limite)
        {
            ct.ThrowIfCancellationRequested();
            if (port.BytesToRead > 0) lu.Append(port.ReadExisting());
            var texte = lu.ToString();
            if (texte.Contains(attendu, StringComparison.Ordinal)) return;
            if (texte.Contains("ERROR", StringComparison.Ordinal))
                throw new InvalidOperationException($"le modem a répondu : {texte.Trim()}");
            await Task.Delay(100, ct);
        }
        throw new TimeoutException($"pas de réponse « {attendu} » du modem.");
    }
}
