namespace Pharma.Desktop.Services;

/// <summary>
/// Identité du poste, lue dans les variables d'environnement du PC :
/// PHARMA_PHARMACIE (nom de la pharmacie) et PHARMA_POSTE (ex. « Poste caisse 1 »).
/// </summary>
public sealed class ConfigurationPoste
{
    public string NomLogiciel { get; } = "Tracodex Officine";
    public string? Pharmacie { get; } = Lire("PHARMA_PHARMACIE");
    /// <summary>Nom donné au poste (PHARMA_POSTE), null s'il n'est pas déclaré (barre d'état : nom de l'ordinateur, voir NomCourt).</summary>
    public string? PosteDeclare { get; } = Lire("PHARMA_POSTE");
    public string Poste => PosteDeclare ?? $"Poste {Environment.MachineName}";
    /// <summary>Nom court du poste pour la barre d'état : PHARMA_POSTE s'il est déclaré, sinon le nom de l'ordinateur (comme la caisse).</summary>
    public string NomCourt => PosteDeclare ?? Environment.MachineName;

    /// <summary>Ligne affichée sous le nom du logiciel : « Pharmacie X · Poste Y ».</summary>
    public string SousTitre => Pharmacie is null ? Poste : $"{Pharmacie} · {Poste}";

    private static string? Lire(string nom) =>
        Environment.GetEnvironmentVariable(nom) is { Length: > 0 } v ? v.Trim() : null;
}
