using System.Net;
using System.Net.Http;
using System.Security.Authentication;

namespace Pharma.Shared;

/// <summary>Résultat d'un essai de liaison d'un poste avec une API (serveur principal ou 2e serveur).</summary>
public enum EtatLiaison
{
    /// <summary>L'API répond.</summary>
    Joignable,
    /// <summary>Port fermé, PC éteint, pare-feu, délai dépassé : rien ne répond.</summary>
    Injoignable,
    /// <summary>Le serveur répond mais le poste refuse son certificat (autorité absente du poste, ou nom/adresse absent du certificat).</summary>
    CertificatRefuse,
    /// <summary>Le serveur répond en HTTPS mais l'API n'est pas prête (2e serveur pas encore basculé).</summary>
    PasBascule
}

/// <summary>
/// Classement des erreurs de liaison d'un poste avec le serveur, et textes du bandeau. Le certificat n'est jamais contourné :
/// un refus de Windows est seulement reconnu pour afficher la bonne consigne.
/// </summary>
public static class LiaisonServeur
{
    /// <summary>Classe l'exception levée par une requête HTTP : certificat refusé (AuthenticationException, même imbriquée) ou injoignable.</summary>
    public static EtatLiaison Classer(Exception? ex)
    {
        for (var e = ex; e is not null; e = e.InnerException)
        {
            if (e is AuthenticationException) return EtatLiaison.CertificatRefuse;
            if (e is HttpRequestException { HttpRequestError: HttpRequestError.SecureConnectionError }) return EtatLiaison.CertificatRefuse;
        }
        return EtatLiaison.Injoignable;
    }

    /// <summary>Classe la réponse d'un serveur joignable : 2xx = joignable, sinon l'API n'est pas prête.</summary>
    public static EtatLiaison Classer(HttpStatusCode code) => (int)code is >= 200 and < 300 ? EtatLiaison.Joignable : EtatLiaison.PasBascule;

    /// <summary>Texte du bandeau pour le 2e serveur (bouton de bascule) ; null si joignable.</summary>
    public static string? MessageSecours(EtatLiaison etat) => etat switch
    {
        EtatLiaison.Injoignable =>
            "Le 2e serveur ne répond pas : vérifiez qu’il est allumé, que son service est démarré (il ne démarre qu’après la bascule) et que l’adresse saisie est exacte.",
        EtatLiaison.CertificatRefuse =>
            "Le certificat du 2e serveur est refusé par ce poste : installez l’autorité de la pharmacie sur ce poste (manuel, étape 5.4) ou vérifiez que l’adresse saisie est bien celle qui figure dans le certificat.",
        EtatLiaison.PasBascule =>
            "Le 2e serveur est joignable mais n’est pas encore basculé : sur le 2e serveur, l’administrateur (avec le titulaire) lance « Basculer sur le 2e serveur » (scripts\\second-serveur\\basculer-second-serveur.ps1), puis réessayez.",
        _ => null
    };

    /// <summary>Texte complémentaire du bandeau « Serveur injoignable » du serveur principal quand le certificat est refusé ; null sinon.</summary>
    public static string? MessagePrincipal(EtatLiaison etat) => etat == EtatLiaison.CertificatRefuse
        ? "Le certificat du serveur est refusé par ce poste : installez l’autorité de la pharmacie sur ce poste (manuel, étape 5.4) ou vérifiez l’adresse de l’API de ce poste (Administration, Imprimantes et étiquettes, Serveur et PC de secours)."
        : null;

    /// <summary>Ligne de journal du poste : état seulement, ni adresse ni donnée personnelle.</summary>
    public static string LigneJournal(string contexte, EtatLiaison etat, Exception? ex) =>
        $"Liaison {contexte} : {etat}" + (ex is null ? "" : $" ({ex.GetType().Name})");
}

/// <summary>Contrôle des adresses d'API saisies sur un poste (poste.json).</summary>
public static class AdresseApiPoste
{
    /// <summary>Adresse absolue http ou https ; sinon message d'erreur (bloquant). Vide : accepté.</summary>
    public static string? Controler(string? adresse) =>
        string.IsNullOrWhiteSpace(adresse) ? null
        : Uri.TryCreate(adresse.Trim(), UriKind.Absolute, out var u) && u.Scheme is "http" or "https" ? null
        : "Adresse invalide : saisissez par exemple https://<serveur>:5443/ (http://localhost:5080/ n’est permis que sur le PC serveur lui-même).";

    /// <summary>
    /// Avertissement (non bloquant) pour une adresse http:// qui n'est pas celle du PC lui-même : le réseau de l'officine est en HTTPS seul,
    /// le port 5080 n'écoute que sur le PC serveur. Null si l'adresse est vide, en https ou en http local.
    /// </summary>
    public static string? Avertir(string? adresse)
    {
        if (string.IsNullOrWhiteSpace(adresse) || !Uri.TryCreate(adresse.Trim(), UriKind.Absolute, out var u) || u.Scheme != "http") return null;
        if (u.IsLoopback) return null;
        return "Adresse en http:// : le réseau de l’officine n’accepte que https://<serveur>:5443/. http://localhost:5080/ n’est permis que sur le PC serveur lui-même ; sur un autre PC, ce poste ne pourra pas se connecter.";
    }
}
