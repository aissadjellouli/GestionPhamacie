namespace Pharma.Application.Commun;

/// <summary>Journal des actions sensibles (table journal_audit, non modifiable).</summary>
public interface IJournal
{
    Task EcrireAsync(long utilisateurId, string action, string? tableCible, long? enregistrementId,
        IReadOnlyDictionary<string, object?> details, string? permissionCode, CancellationToken ct = default);
}

public sealed record EntreeJournal(
    DateTime Date, string Employe, string Action, string? DetailsJson, string? AutorisePar, string? Permission);

public interface IJournalLecture
{
    Task<IReadOnlyList<EntreeJournal>> DernieresAsync(int nombre, CancellationToken ct = default);
}
