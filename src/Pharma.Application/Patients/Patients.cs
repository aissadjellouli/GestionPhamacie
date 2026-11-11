using Pharma.Domain.Commun;
using Pharma.Domain.Ventes;

namespace Pharma.Application.Patients;

public sealed record PatientResume(
    long Id, string Nom, string? Prenom, string? Telephone, string? NumAssure,
    long? OrganismeId, string? Organisme, decimal PlafondCredit, decimal SoldeCredit);

public sealed record NouveauPatient(string Nom, string? Prenom, string? Telephone, string? NumAssure, long? OrganismeId, decimal PlafondCredit, string? Email = null);

/// <summary>Fiche complète d'un patient, telle qu'on la modifie (SoldeCredit en lecture : la dette ne se modifie pas ici).</summary>
public sealed record FichePatient(long Id, string Nom, string? Prenom, DateOnly? DateNaissance, string? Telephone, string? Email, string? NumAssure,
    long? OrganismeId, bool Chronique, decimal PlafondCredit, decimal SoldeCredit = 0);

public sealed record Medecin(long Id, string Nom, string? Specialite);
public sealed record Organisme(long Id, string Code, string Nom, int DelaiPaiementJours = 30);

public interface IPatients
{
    Task<IReadOnlyList<PatientResume>> RechercherAsync(string texte, int limite, CancellationToken ct = default);
    Task<Patient?> TrouverAsync(long id, CancellationToken ct = default);
    /// <param name="creePar">Employé qui crée le patient (surveillance des crédits).</param>
    Task<long> CreerAsync(NouveauPatient patient, long? creePar, CancellationToken ct = default);
    Task<FichePatient?> FicheAsync(long id, CancellationToken ct = default);
    /// <summary>Remplace l'identité, les coordonnées, l'organisme, la case « chronique » et le plafond (pas la dette). Faux si absent.</summary>
    Task<bool> ModifierAsync(FichePatient fiche, CancellationToken ct = default);
}

/// <summary>
/// Modification de la fiche d'un patient (droit de vendre, comme sa création à la caisse). Chaque champ changé est inscrit au
/// journal, avec l'ancienne et la nouvelle valeur. Un téléphone repris hors format reste accepté tant qu'on n'y touche pas.
/// </summary>
public sealed class ModifierPatient(IPatients patients, Commun.IJournal journal, Commun.IHorloge horloge)
{
    public async Task ExecuterAsync(FichePatient saisie, long utilisateurId, CancellationToken ct = default)
    {
        var actuelle = await patients.FicheAsync(saisie.Id, ct) ?? throw new RegleMetierException("Patient introuvable.");
        if (string.IsNullOrWhiteSpace(saisie.Nom)) throw new RegleMetierException("Le nom du patient est obligatoire.", "nom_patient");
        if (saisie.PlafondCredit < 0) throw new RegleMetierException("Le plafond de crédit ne peut pas être négatif.", "plafond");
        if (saisie.DateNaissance is { } dn && dn > horloge.Aujourdhui)
            throw new RegleMetierException("La date de naissance ne peut pas être dans le futur.", "date_naissance");
        static string? Net(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
        var tel = Net(saisie.Telephone) is { } t && t != actuelle.Telephone?.Trim() ? Contacts.RegleContact.Telephone(t) : Net(saisie.Telephone);
        var fiche = saisie with
        {
            Nom = saisie.Nom.Trim(), Prenom = Net(saisie.Prenom), Telephone = tel, Email = Contacts.RegleContact.Email(saisie.Email),
            NumAssure = Net(saisie.NumAssure), OrganismeId = saisie.OrganismeId is > 0 ? saisie.OrganismeId : null, SoldeCredit = actuelle.SoldeCredit
        };
        if (!await patients.ModifierAsync(fiche, ct)) throw new RegleMetierException("Patient introuvable.");

        var changes = new Dictionary<string, object?>();
        void Comparer(string nom, object? avant, object? apres) { if (!Equals(avant, apres)) changes[nom] = new { avant, apres }; }
        Comparer("nom", actuelle.Nom, fiche.Nom); Comparer("prenom", actuelle.Prenom, fiche.Prenom);
        Comparer("date_naissance", actuelle.DateNaissance, fiche.DateNaissance); Comparer("telephone", actuelle.Telephone, fiche.Telephone);
        Comparer("email", actuelle.Email, fiche.Email); Comparer("num_assure", actuelle.NumAssure, fiche.NumAssure);
        Comparer("organisme_id", actuelle.OrganismeId, fiche.OrganismeId); Comparer("chronique", actuelle.Chronique, fiche.Chronique);
        Comparer("plafond_credit", actuelle.PlafondCredit, fiche.PlafondCredit);
        if (changes.Count > 0)
            await journal.EcrireAsync(utilisateurId, "patient_modifie", "patient", fiche.Id, changes, Pharma.Domain.Securite.Permissions.CaisseVendre, ct);
    }
}

public sealed class LireFichePatient(IPatients patients)
{
    public async Task<FichePatient> ExecuterAsync(long id, CancellationToken ct = default) =>
        await patients.FicheAsync(id, ct) ?? throw new RegleMetierException("Patient introuvable.");
}

public interface IMedecins
{
    Task<IReadOnlyList<Medecin>> RechercherAsync(string texte, int limite, CancellationToken ct = default);
    Task<long> CreerAsync(string nom, string? specialite, CancellationToken ct = default);
    Task<bool> ModifierAsync(long id, string nom, string? specialite, CancellationToken ct = default);
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

/// <summary>Création rapide d'un patient depuis la caisse : l'employé qui le crée est gardé et la création inscrite au journal.</summary>
public sealed class CreerPatient(IPatients patients, Commun.IJournal? journal = null)
{
    public async Task<long> ExecuterAsync(NouveauPatient p, long? utilisateurId = null, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(p.Nom))
            throw new RegleMetierException("Le nom du patient est obligatoire.", "nom_patient");
        if (p.PlafondCredit < 0)
            throw new RegleMetierException("Le plafond de crédit ne peut pas être négatif.", "plafond");
        static string? Net(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
        var id = await patients.CreerAsync(p with
        {
            Nom = p.Nom.Trim(), Prenom = Net(p.Prenom), Telephone = Net(p.Telephone), NumAssure = Net(p.NumAssure),
            Email = Contacts.RegleContact.Email(p.Email)
        }, utilisateurId, ct);
        if (journal is not null && utilisateurId is { } u)
            await journal.EcrireAsync(u, "patient_cree", "patient", id,
                new Dictionary<string, object?> { ["patient"] = $"{p.Nom.Trim()} {Net(p.Prenom)}".Trim(), ["plafond_credit"] = p.PlafondCredit },
                Domain.Securite.Permissions.CaisseVendre, ct);
        return id;
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

/// <summary>Correction du nom ou de la spécialité d'un médecin prescripteur (droit de vendre, comme sa création à la caisse).</summary>
public sealed class ModifierMedecin(IMedecins medecins, Commun.IJournal journal)
{
    public async Task ExecuterAsync(long id, string? nom, string? specialite, long utilisateurId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(nom)) throw new RegleMetierException("Le nom du médecin est obligatoire.", "nom_medecin");
        var s = string.IsNullOrWhiteSpace(specialite) ? null : specialite.Trim();
        if (!await medecins.ModifierAsync(id, nom.Trim(), s, ct)) throw new RegleMetierException("Médecin introuvable.");
        await journal.EcrireAsync(utilisateurId, "medecin_modifie", "medecin", id,
            new Dictionary<string, object?> { ["nom"] = nom.Trim(), ["specialite"] = s }, Pharma.Domain.Securite.Permissions.CaisseVendre, ct);
    }
}

public sealed class CreerMedecin(IMedecins medecins)
{
    public Task<long> ExecuterAsync(string? nom, string? specialite, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(nom))
            throw new RegleMetierException("Le nom du médecin est obligatoire.", "nom_medecin");
        return medecins.CreerAsync(nom.Trim(), string.IsNullOrWhiteSpace(specialite) ? null : specialite.Trim(), ct);
    }
}

public sealed class ListerOrganismes(IOrganismes organismes)
{
    public Task<IReadOnlyList<Organisme>> ExecuterAsync(CancellationToken ct = default) => organismes.ListerAsync(ct);
}
