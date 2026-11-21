using Microsoft.Extensions.Logging;
using Pharma.Application.Chifa;
using Pharma.Application.Demo;
using Pharma.Application.Notifications;
using Pharma.Domain.Commun;

namespace Pharma.Infrastructure.Demo;

// Mode démonstration (Demo:Actif, docs/demo-cloud.md étape 2) : adaptateurs factices posés par AddInfrastructure à la place des
// envois réels. Rien ne sort de la démonstration ; le journal du serveur note la tentative sans adresse, numéro ni texte (aucune
// donnée personnelle ni code de vérification dans les journaux).

/// <summary>E-mails (notifications, bons de commande) : jamais envoyés en démonstration.</summary>
public sealed class EnvoiEmailDemo(ILogger<EnvoiEmailDemo> journal) : IEnvoiEmail, Pharma.Application.Achats.IEnvoiEmailPieceJointe
{
    public const string Fonction = "envoi d'e-mails";

    public Task EnvoyerAsync(ConnexionSmtp connexion, string adresse, string objet, string texte, string? html, CancellationToken ct = default) =>
        Refuser(pieceJointe: false);

    public Task EnvoyerAsync(ConnexionSmtp connexion, string adresse, string objet, string texte, string nomPieceJointe, byte[] pieceJointe,
        string typeMime, CancellationToken ct = default) =>
        Refuser(pieceJointe: true);

    private Task Refuser(bool pieceJointe)
    {
        journal.LogInformation("Démonstration : e-mail non envoyé (pièce jointe : {PieceJointe})", pieceJointe);
        throw new RegleMetierException(ModeDemo.Indisponible(Fonction));
    }
}

/// <summary>SMS (modem GSM) : jamais envoyés en démonstration ; le port série n'est jamais ouvert.</summary>
public sealed class EnvoiSmsDemo(ILogger<EnvoiSmsDemo> journal) : IEnvoiSms
{
    public const string Fonction = "envoi de SMS";

    public Task EnvoyerAsync(string portSerie, string numero, string texte, CancellationToken ct = default)
    {
        journal.LogInformation("Démonstration : SMS non envoyé ({Caracteres} caractères)", texte.Length);
        throw new RegleMetierException(ModeDemo.Indisponible(Fonction));
    }
}

/// <summary>Export Chifa réel (dossier du logiciel de la CNAS) : jamais lu en démonstration, aucune facture signée.</summary>
public sealed class ChifaConnecteurDemo(ILogger<ChifaConnecteurDemo> journal) : IChifaConnecteur
{
    public Task<IReadOnlyList<FactureChifa>> LireFacturesSigneesAsync(DateOnly depuis, CancellationToken ct = default)
    {
        journal.LogInformation("Démonstration : export Chifa non lu (dossier de la CNAS ignoré)");
        return Task.FromResult<IReadOnlyList<FactureChifa>>([]);
    }
}
