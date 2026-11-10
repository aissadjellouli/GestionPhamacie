namespace Pharma.Application.Reprise;

/// <summary>Un connecteur par logiciel concurrent pour la reprise de données (voir docs/mise-en-production.md).</summary>
public interface IConnecteurSource
{
    string LogicielSource { get; }
    Task ExporterVersTransitAsync(long sessionImportId, CancellationToken ct = default);
}
