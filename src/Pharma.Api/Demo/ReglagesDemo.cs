namespace Pharma.Api.Demo;

/// <summary>
/// Section « Demo » (docs/demo-cloud.md, étape 2). Actif est FAUX par défaut : dans l'officine, rien ne change. Vrai seulement dans
/// la démonstration (docker-compose de deploy/demo), sur une base qui porte le marqueur de démonstration (sinon refus de démarrer).
/// </summary>
public sealed class ReglagesDemo
{
    public const string Section = "Demo";

    public bool Actif { get; set; }

    /// <summary>
    /// Fichier Excel de la nomenclature nationale importé en démonstration quand la base n'en a pas (premier démarrage, puis après
    /// chaque remise à zéro). Vide : aucun import automatique.
    /// </summary>
    public string? FichierNomenclature { get; set; }
}
