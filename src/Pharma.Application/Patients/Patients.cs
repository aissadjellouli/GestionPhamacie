using Pharma.Domain.Commun;
using Pharma.Domain.Ventes;

namespace Pharma.Application.Patients;

public sealed record PatientResume(
    long Id, string Nom, string? Prenom, string? Telephone, string? NumAssure,
    long? OrganismeId, string? Organisme, decimal PlafondCredit, decimal SoldeCredit);

public sealed record NouveauPatient(string Nom, string? Prenom, string? Telephone, string? NumAssure, long? OrganismeId, decimal PlafondCredit);

public sealed record Medecin(long Id, string Nom, string? Specialite);
public sealed record Organisme(long Id, string Code, string Nom);

public interface IPatients
{
    Task<IReadOnlyList<PatientResume>> RechercherAsync(string texte, int limite, CancellationToken ct = default);
    Task<Patient?> TrouverAsync(long id, CancellationToken ct = default);
    Task<long> CreerAsync(NouveauPatient patient, CancellationToken ct = default);
}

public interface IMedecins
{
    Task<IReadOnlyList<Medecin>> RechercherAsync(string texte, int limite, CancellationToken ct = default);
    Task<long> CreerAsync(string nom, string? specialite, CancellationToken ct = default);
}

public interface IOrganismes
{
    Task<IReadOnlyList<Organisme>> ListerAsync(CancellationToken ct = default);
}

/// <summary>Recherche au comptoir par nom, prénom, téléphone ou numéro d'assuré.</summary>
public sealed class RechercherPatients(IPatients patients)
{
    public Task<IReadOnlyList<PatientResume>> ExecuterAsync(string? texte, CancellationToken ct = default)
    {
        var t = texte?.Trim() ?? "";
        return t.Length < 2
            ? Task.FromResult<IReadOnlyList<PatientResume>>([])
            : patients.RechercherAsync(t, 20, ct);
    }
}

/// <summary>Création rapide d'un patient depuis la caisse.</summary>
public sealed class CreerPatient(IPatients patients)
{
    public Task<long> ExecuterAsync(NouveauPatient p, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(p.Nom))
            throw new RegleMetierException("Le nom du patient est obligatoire.");
        if (p.PlafondCredit < 0)
            throw new RegleMetierException("Le plafond de crédit ne peut pas être négatif.");
        static string? Net(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
        return patients.CreerAsync(p with
        {
            Nom = p.Nom.Trim(), Prenom = Net(p.Prenom), Telephone = Net(p.Telephone), NumAssure = Net(p.NumAssure)
        }, ct);
    }
}

public sealed class RechercherMedecins(IMedecins medecins)
{
    public Task<IReadOnlyList<Medecin>> ExecuterAsync(string? texte, CancellationToken ct = default)
    {
        var t = texte?.Trim() ?? "";
        return t.Length < 2 ? Task.FromResult<IReadOnlyList<Medecin>>([]) : medecins.RechercherAsync(t, 20, ct);
    }
}

public sealed class CreerMedecin(IMedecins medecins)
{
    public Task<long> ExecuterAsync(string? nom, string? specialite, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(nom))
            throw new RegleMetierException("Le nom du médecin est obligatoire.");
        return medecins.CreerAsync(nom.Trim(), string.IsNullOrWhiteSpace(specialite) ? null : specialite.Trim(), ct);
    }
}

public sealed class ListerOrganismes(IOrganismes organismes)
{
    public Task<IReadOnlyList<Organisme>> ExecuterAsync(CancellationToken ct = default) => organismes.ListerAsync(ct);
}
