namespace Pharma.Desktop.Services;

/// <summary>
/// Identité du poste, lue dans les variables d'environnement du PC :
/// PHARMA_PHARMACIE (nom de la pharmacie) et PHARMA_POSTE (ex. « Poste caisse 1 »).
/// </summary>
public sealed class ConfigurationPoste
{
    public string NomLogiciel { get; } = "Pharma Officine";
    public string? Pharmacie { get; } = Lire("PHARMA_PHARMACIE");
    public string Poste { get; } = Lire("PHARMA_POSTE") ?? $"Poste {Environment.MachineName}";

    /// <summary>Ligne affichée sous le nom du logiciel : « Pharmacie X · Poste Y ».</summary>
    public string SousTitre => Pharmacie is null ? Poste : $"{Pharmacie} · {Poste}";

    private static string? Lire(string nom) =>
        Environment.GetEnvironmentVariable(nom) is { Length: > 0 } v ? v.Trim() : null;
}
