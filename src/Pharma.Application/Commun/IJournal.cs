namespace Pharma.Application.Commun;

/// <summary>Journal des actions sensibles (table journal_audit, non modifiable).</summary>
public interface IJournal
{
    Task EcrireAsync(long utilisateurId, string action, string? tableCible, long? enregistrementId,
        IReadOnlyDictionary<string, object?> details, string? permissionCode, CancellationToken ct = default);
}

public sealed record EntreeJournal(
    DateTime Date, string Employe, string Action, string? DetailsJson, string? AutorisePar, string? Permission, long? EmployeId = null, long? AutoriseParId = null);

/// <summary>Filtres du journal : période (dates incluses) et employé (partie du nom).</summary>
public sealed record FiltreJournal(DateOnly? Du, DateOnly? Au, string? Employe);

public interface IJournalLecture
{
    Task<IReadOnlyList<EntreeJournal>> DernieresAsync(int nombre, CancellationToken ct = default);
    /// <summary>Une page du journal, de la plus récente à la plus ancienne, avec le total des actions qui répondent aux filtres.</summary>
    Task<Page<EntreeJournal>> PageAsync(FiltreJournal filtre, DemandePage page, CancellationToken ct = default);
}
