namespace Pharma.Cloud.Api.Depart;

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
