using Pharma.Domain.Commun;

namespace Pharma.Application.Demo;

/// <summary>
/// Mode démonstration (docs/demo-cloud.md, étape 2 ; réglage Demo:Actif de l'API, faux par défaut). Vrai seulement sur une base
/// qui porte le marqueur de démonstration (table <see cref="TableMarqueur"/>, posée par le seul script d'init de la démo) :
/// données fictives, fonctions qui sortent de l'officine coupées (SMS, e-mail, serveur central, sauvegardes, super admin, 2e serveur,
/// reprise ODBC, export Chifa), mots de passe des trois comptes de démonstration figés.
/// </summary>
public sealed record ModeDemo(bool Actif)
{
    /// <summary>Texte du bandeau du poste et de la page mobile, et du filigrane des PDF (en majuscules).</summary>
    public const string Libelle = "Démonstration — données fictives";

    /// <summary>Table du marqueur : créée par deploy/demo/init/10_base_officine.sh, jamais par une migration ni un seed.</summary>
    public const string TableMarqueur = "base_demonstration";

    /// <summary>Comptes communs de la démonstration (mot de passe de démonstration : docs/demo-cloud.md).</summary>
    public static readonly IReadOnlyList<string> ComptesDemo = ["titulaire", "assistant", "vendeur"];

    public static bool EstCompteDemo(string? identifiant) =>
        identifiant is not null && ComptesDemo.Contains(identifiant.Trim(), StringComparer.OrdinalIgnoreCase);

    /// <summary>Message du refus (422) d'une fonction coupée.</summary>
    public static string Indisponible(string fonction) =>
        $"Indisponible en démonstration : {fonction}. Cette fonction est coupée sur la démonstration (données fictives) ; elle fonctionne normalement dans l'officine.";

    /// <summary>Refus de la fonction si le mode démonstration est actif (règle métier : 422 au poste).</summary>
    public void Exiger(string fonction)
    {
        if (Actif) throw new RegleMetierException(Indisponible(fonction));
    }

    /// <summary>
    /// Démarrage en démonstration refusé si la base n'a pas le marqueur (ou s'il n'a pas pu être lu) : une base réelle n'est jamais
    /// ouverte avec les fonctions coupées et le bandeau « données fictives ». Null : démarrage permis.
    /// </summary>
    public static string? RefusDemarrage(bool actif, bool? marqueurPresent) =>
        !actif || marqueurPresent == true ? null
        : marqueurPresent == false
            ? $"Démarrage refusé : Demo:Actif=true mais la base n'est pas une base de démonstration (marqueur « {TableMarqueur} » absent). "
              + "Le mode démonstration ne s'ouvre que sur la base créée par deploy/demo/init/10_base_officine.sh (docs/demo-cloud.md). "
              + "Sur une base d'officine, retirez Demo:Actif."
            : $"Démarrage refusé : Demo:Actif=true mais le marqueur de démonstration (« {TableMarqueur} ») n'a pas pu être lu (base injoignable ?). "
              + "Vérifiez la chaîne de connexion et que la base est démarrée, puis relancez l'API.";

    /// <summary>
    /// Base de démonstration ouverte sans le mode démonstration, hors développement : avertissement (le démarrage n'est pas refusé,
    /// mais SMS, e-mails, synchronisation et sauvegardes fonctionneraient sur des données fictives). Null : rien à signaler.
    /// </summary>
    public static string? AvertissementDemarrage(bool actif, bool? marqueurPresent, bool developpement) =>
        actif || developpement || marqueurPresent != true ? null
        : $"ATTENTION : cette base porte le marqueur de démonstration (« {TableMarqueur} », données fictives) mais Demo:Actif n'est pas réglé : "
          + "SMS, e-mails, synchronisation et sauvegardes ne sont pas coupés. Réglez Demo:Actif=true (démonstration) ou utilisez la base de l'officine.";
}
