using System.Net;

namespace Pharma.Cloud.Web;

/// <summary>
/// Mode d'envoi des codes de second facteur : Fichier (développement seulement, prioritaire) ou Reglage (lot S7.6 : canal et SMTP
/// lus en base à chaque envoi, écran « Envoi des codes » de l'administration du site ; « courriel » est l'ancien nom de ce mode).
/// </summary>
public enum ModeCodes { Fichier, Reglage }

/// <summary>Réglages retenus au démarrage des écrans (jamais de secret ici : la connexion vient d'une variable d'environnement).</summary>
/// <param name="DossierCles">Lot S7.6 : clés de la protection des données qui chiffrent le mot de passe SMTP (Ecrans:DossierCles, sinon
/// &lt;PHARMA_CLOUD_DOSSIER&gt;\cles-protection) ; null : aucun mot de passe SMTP ne peut être enregistré ni relu.</param>
public sealed record ReglagesEcrans(IPAddress Adresse, int Port, ModeCodes Mode, string Connexion, string CertificatAutorite, string DossierCodes,
    string? DossierCles = null)
{
    public const string VariableConnexion = "PHARMA_CLOUD_ECRANS_CONNEXION";
    public const string VariableDossier = "PHARMA_CLOUD_DOSSIER";
    public const int PortParDefaut = 5094;

    /// <summary>Dossier des codes en mode fichier (développement seulement) : %LOCALAPPDATA%\PharmaOfficine\central-dev\codes.</summary>
    public static string DossierCodesDeveloppement() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PharmaOfficine", "central-dev", "codes");

    /// <summary>
    /// Décide si les écrans démarrent : (réglages, null) ou (null, motif du refus). Refus : adresse autre que la boucle locale en
    /// développement ; mode « fichier » hors développement ou hors boucle locale ; mode SMS (aucun fournisseur) ; connexion ou
    /// certificat public de l'autorité absents ; port invalide ou égal à celui du serveur central.
    /// </summary>
    public static (ReglagesEcrans? Reglages, string? Refus) Evaluer(bool developpement, IConfiguration configuration, Func<string, string?> variable)
    {
        var texteAdresse = configuration["Ecrans:Adresse"] is { Length: > 0 } a ? a.Trim() : "127.0.0.1";
        if (!IPAddress.TryParse(texteAdresse, out var adresse))
            return (null, $"Écrans refusés : adresse « {texteAdresse} » invalide.");
        var locale = IPAddress.IsLoopback(adresse);
        if (developpement && !locale)
            return (null, $"Écrans refusés : en développement, seule la boucle locale est permise (adresse « {texteAdresse} »).");
        var port = configuration.GetValue("Ecrans:Port", PortParDefaut);
        if (port is < 1024 or > 65535 || port == configuration.GetValue("Cloud:Port", 5090) || port == 5090)
            return (null, $"Écrans refusés : port {port} invalide (1024 à 65535, différent du serveur central).");
        var texteMode = (configuration["Ecrans:ModeCodes"] ?? "reglage").Trim().ToLowerInvariant();
        ModeCodes mode;
        switch (texteMode)
        {
            case "fichier":
                if (!developpement || !locale)
                    return (null, "Écrans refusés : le mode « fichier » des codes de second facteur est réservé au développement sur la boucle locale.");
                mode = ModeCodes.Fichier;
                break;
            case "reglage" or "courriel":
                mode = ModeCodes.Reglage;
                break;
            case "sms":
                return (null, "Écrans refusés : envoi des codes par SMS non disponible (aucun fournisseur retenu) ; le canal se choisit dans l'écran « Envoi des codes » (Ecrans:ModeCodes = reglage).");
            default:
                return (null, $"Écrans refusés : mode des codes « {texteMode} » inconnu (fichier ou reglage).");
        }
        if (variable(VariableConnexion) is not { Length: > 0 } connexion)
            return (null, $"Écrans refusés : variable {VariableConnexion} absente (connexion rattachée au rôle pharma_cloud_ecrans).");
        var certificat = configuration["Ecrans:CertificatAutorite"] is { Length: > 0 } c ? c
            : variable(VariableDossier) is { Length: > 0 } d ? Path.Combine(d, "autorite.crt") : null;
        if (certificat is null)
            return (null, $"Écrans refusés : certificat public de l'autorité introuvable (Ecrans:CertificatAutorite ou {VariableDossier}).");
        if (!certificat.EndsWith(".crt", StringComparison.OrdinalIgnoreCase))
            return (null, "Écrans refusés : seul le certificat PUBLIC de l'autorité (.crt) est lu, jamais sa clé.");
        var dossierCodes = configuration["Ecrans:DossierCodes"] is { Length: > 0 } dc ? dc : DossierCodesDeveloppement();
        var dossierCles = configuration["Ecrans:DossierCles"] is { Length: > 0 } dk ? dk
            : Pharma.Cloud.Api.Depart.ProtectionSmtpCentral.DossierDuCentral(variable(VariableDossier));
        return (new ReglagesEcrans(adresse, port, mode, connexion, certificat, dossierCodes, dossierCles), null);
    }
}
