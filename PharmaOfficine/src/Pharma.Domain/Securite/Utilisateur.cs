namespace Pharma.Domain.Securite;

public class Utilisateur
{
    public long Id { get; init; }
    public string NomComplet { get; init; } = "";
    public string Identifiant { get; init; } = "";
    public string MotDePasseHash { get; init; } = "";
    public string? PinHash { get; init; }
    public long RoleId { get; init; }
    public bool Actif { get; init; } = true;
}
