using Pharma.Shared;

namespace Pharma.Desktop.Services;

/// <summary>Accord saisi pour un produit bloqué : confirmation de l'employé habilité, ou nom et code comptoir d'un habilité.</summary>
public sealed class AccordBlocage
{
    public bool Accepte { get; set; }
    public string? Validateur { get; set; }
    public string? Code { get; set; }

    /// <summary>Accord à envoyer au serveur ; null s'il n'y a rien de saisi. Un validateur choisi vaut acceptation.</summary>
    public ValidationBlocageDto? VersDto(bool habilite) =>
        habilite ? (Accepte ? new ValidationBlocageDto(true) : null)
        : string.IsNullOrEmpty(Validateur) ? null : new ValidationBlocageDto(true, Validateur, Code);

    /// <summary>Saisie suffisante pour tenter l'opération (le serveur vérifie le code et le droit).</summary>
    public bool Complet(bool habilite) => habilite ? Accepte : !string.IsNullOrEmpty(Validateur) && CodeComptoirSaisie.Erreur(Code, nouveauCode: false) is null;

    public void Effacer() => (Accepte, Validateur, Code) = (false, null, null);
}
