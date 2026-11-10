namespace Pharma.Application.Commun;

/// <summary>Date et heure du serveur de l'officine (remplaçable en test).</summary>
public interface IHorloge
{
    DateTime MaintenantUtc { get; }
    DateOnly Aujourdhui { get; }
}
