using System.Security.Cryptography;
using Pharma.Application.Administration;
using Pharma.Application.Commun;
using Pharma.Application.Securite;
using Pharma.Domain.Commun;
using Pharma.Domain.Notifications;

namespace Pharma.Application.Notifications;

/// <summary>Code de vérification en cours d'un destinataire : haché, date d'expiration, codes faux déjà saisis.</summary>
public sealed record CodeVerification(string Hash, DateTime ExpireLeUtc, int Essais);

/// <summary>Codes de vérification des destinataires des notifications (068_verification_destinataire.sql).</summary>
public interface ICodesVerificationDestinataire
{
    /// <summary>Nouveau code (remplace le précédent), essais remis à zéro.</summary>
    Task EnregistrerAsync(long destinataireId, string hash, DateTime expireLeUtc, CancellationToken ct = default);
    Task<CodeVerification?> LireAsync(long destinataireId, CancellationToken ct = default);
    /// <summary>Un code faux de plus : nombre de codes faux saisis.</summary>
    Task<int> NoterEssaiAsync(long destinataireId, CancellationToken ct = default);
    Task EffacerAsync(long destinataireId, CancellationToken ct = default);
}

/// <summary>
/// « Vérifier » : un code à 6 chiffres est envoyé au destinataire (e-mail, et SMS si un modem est réglé), valable 15 minutes.
/// Seul le haché du code est gardé. Réservé au titulaire, inscrit au journal.
/// </summary>
public sealed class EnvoyerCodeVerification(INotificationsDepot depot, ICodesVerificationDestinataire codes, IHacheurMotDePasse hacheur,
    IPermissionService droits, IHorloge horloge, IJournal journal, IPharmacie? pharmacie = null)
{
    public const int DureeMinutes = 15;

    public async Task<int> ExecuterAsync(long destinataireId, long utilisateurId, CancellationToken ct = default)
    {
        await droits.ExigerTitulaireAsync(utilisateurId, "vérifier un destinataire", ct);
        var d = (await depot.DestinatairesAsync(ct)).FirstOrDefault(x => x.Id == destinataireId)
                ?? throw new RegleMetierException("Destinataire introuvable.");
        if (!d.Actif) throw new RegleMetierException("Destinataire désactivé : réactivez-le avant de le vérifier.");

        var code = RandomNumberGenerator.GetInt32(0, 1_000_000).ToString("D6");
        var reglages = await depot.ReglagesAsync(ct);
        // Type « test » : le seul que la base accepte vers un destinataire pas encore vérifié
        var contenu = new ContenuNotification(TypeNotification.Test, "Pharma Officine : code de vérification",
            $"Bonjour {d.Nom},\n\nVotre code de vérification : {code}\nValable {DureeMinutes} minutes.\n\n" +
            "Donnez-le au titulaire : il le saisit dans Administration › Notifications pour que cette adresse reçoive les alertes de la pharmacie.",
            null, $"Pharma Officine : code de verification {code}, valable {DureeMinutes} min.");
        var messages = NotificationService.Construire(contenu, [d], reglages.SmsActifs, await NotificationService.SignatureAsync(pharmacie, ct));
        if (messages.Count == 0)
            throw new RegleMetierException(d.CanalSms && !reglages.SmsActifs ? "Modem GSM non configuré : aucun SMS possible." : "Aucun canal utilisable pour ce destinataire.");

        await codes.EnregistrerAsync(destinataireId, hacheur.Hacher(code), horloge.MaintenantUtc.AddMinutes(DureeMinutes), ct);
        await depot.AjouterMessagesAsync(messages, ct);
        await journal.EcrireAsync(utilisateurId, "code_verification_envoye", "destinataire_notification", destinataireId,
            new Dictionary<string, object?> { ["destinataire"] = d.Nom, ["messages"] = messages.Count }, null, ct);
        return messages.Count;
    }
}

/// <summary>Le titulaire saisit le code reçu par le destinataire : juste, le destinataire est vérifié ; 5 codes faux, il faut en redemander un.</summary>
public sealed class VerifierCodeDestinataire(INotificationsDepot depot, ICodesVerificationDestinataire codes, IVerificateurMotDePasse verificateur,
    IPermissionService droits, IHorloge horloge, IJournal journal)
{
    public const int EssaisMax = 5;

    public async Task ExecuterAsync(long destinataireId, string? code, long utilisateurId, CancellationToken ct = default)
    {
        await droits.ExigerTitulaireAsync(utilisateurId, "vérifier un destinataire", ct);
        if (string.IsNullOrWhiteSpace(code)) throw new RegleMetierException("Saisissez le code reçu par le destinataire.", "code");
        var c = await codes.LireAsync(destinataireId, ct)
                ?? throw new RegleMetierException("Aucun code en cours : cliquez sur « Vérifier » pour en envoyer un.", "code");
        if (c.ExpireLeUtc <= horloge.MaintenantUtc)
            throw new RegleMetierException("Code expiré : cliquez sur « Vérifier » pour en envoyer un nouveau.", "code");
        if (c.Essais >= EssaisMax)
            throw new RegleMetierException($"{EssaisMax} codes faux : cliquez sur « Vérifier » pour en envoyer un nouveau.", "code");
        if (!verificateur.Verifier(code.Trim(), c.Hash))
        {
            var essais = await codes.NoterEssaiAsync(destinataireId, ct);
            throw new RegleMetierException(essais >= EssaisMax
                ? $"Code incorrect. {EssaisMax} codes faux : cliquez sur « Vérifier » pour en envoyer un nouveau."
                : $"Code incorrect ({EssaisMax - essais} essai(s) restant(s)).", "code");
        }
        if (!await depot.MarquerValideAsync(destinataireId, true, ct)) throw new RegleMetierException("Destinataire introuvable.");
        await codes.EffacerAsync(destinataireId, ct);
        await journal.EcrireAsync(utilisateurId, "destinataire_verifie", "destinataire_notification", destinataireId,
            new Dictionary<string, object?> { ["par"] = "code" }, null, ct);
    }
}
