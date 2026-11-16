using Pharma.Domain.Commun;
using Pharma.Domain.Ventes;

namespace Pharma.Application.Patients;

public sealed record PatientResume(
    long Id, string Nom, string? Prenom, string? Telephone, string? NumAssure,
    long? OrganismeId, string? Organisme, decimal PlafondCredit, decimal SoldeCredit, DateOnly? DateFinDroit = null);

public sealed record NouveauPatient(string Nom, string? Prenom, string? Telephone, string? NumAssure, long? OrganismeId, decimal PlafondCredit, string? Email = null);

/// <summary>Fiche complète d'un patient, telle qu'on la modifie (SoldeCredit en lecture : la dette ne se modifie pas ici).</summary>
/// <remarks>Lien et AssurePrincipalId : lien avec l'assuré et fiche de l'assuré principal (script 090, règles du titulaire 2026-11-15).</remarks>
public sealed record FichePatient(long Id, string Nom, string? Prenom, DateOnly? DateNaissance, string? Telephone, string? Email, string? NumAssure,
    long? OrganismeId, bool Chronique, decimal PlafondCredit, decimal SoldeCredit = 0, string? Lien = null, long? AssurePrincipalId = null,
    DateOnly? DateFinDroit = null);

/// <summary>Patient proche d'une fiche : son assuré principal, ses ayants droit ou une fiche de même n° d'assuré de base (12 chiffres).</summary>
public sealed record ProchePatient(long Id, string Nom, string? NumAssure, string? Lien, long? AssurePrincipalId);

/// <summary>
/// Lien d'un patient avec l'assuré (règles du titulaire, 2026-11-15, script 090) : liste fermée (assure, conjoint, enfant, ascendant,
/// autre ; vide accepté pour les fiches existantes) et fiche de l'assuré principal (vide pour l'assuré lui-même).
/// Contrôles : un patient n'est pas son propre assuré principal ; l'assuré principal a le lien « assure » ou vide et n'est lui-même
/// l'ayant droit de personne (pas de chaîne) ; lien « assure » : pas d'assuré principal. Rien n'est déduit du suffixe du n° d'assuré
/// (son sens n'est pas confirmé, docs/chifa.md) : le même n° de base ne fait que proposer un rattachement, que le vendeur accepte ou non.
/// </summary>
public static class FamillePatient
{
    public const string Assure = "assure", Conjoint = "conjoint", Enfant = "enfant", Ascendant = "ascendant", Autre = "autre";
    public const string ChampLien = "lien", ChampPrincipal = "assure_principal";
    public const int LongueurBase = 12;
    public static readonly IReadOnlyList<string> Liens = [Assure, Conjoint, Enfant, Ascendant, Autre];

    /// <summary>Lien saisi : vide → null ; sinon une des valeurs de la liste fermée (refus clair sinon).</summary>
    public static string? Lien(string? saisie)
    {
        if (string.IsNullOrWhiteSpace(saisie)) return null;
        var l = saisie.Trim().ToLowerInvariant();
        return Liens.Contains(l) ? l : throw new RegleMetierException("Lien inconnu : assuré, conjoint, enfant, ascendant ou autre.", ChampLien);
    }

    public static string Libelle(string? lien) => lien switch
    {
        Assure => "assuré", Conjoint => "conjoint", Enfant => "enfant", Ascendant => "ascendant", Autre => "autre", _ => ""
    };

    /// <summary>N° d'assuré de base : les 12 premiers chiffres (avant le suffixe) ; null si le n° en a moins.</summary>
    public static string? NumeroBase(string? numAssure) =>
        RegleNumAssure.Chiffres(numAssure) is { Length: >= LongueurBase } c ? c[..LongueurBase] : null;

    /// <summary>Peut servir d'assuré principal : lien « assure » ou vide, et pas lui-même ayant droit d'un autre.</summary>
    public static bool PeutEtrePrincipal(string? lien, long? assurePrincipalId) => lien is null or Assure && assurePrincipalId is null;

    /// <summary>Contrôles serveur du lien et de l'assuré principal (fiche existante : <paramref name="id"/> ; création : null).</summary>
    public static async Task VerifierAsync(IPatients patients, long? id, string? lien, long? assurePrincipalId, CancellationToken ct)
    {
        if (assurePrincipalId is not { } principal)
        {
            if (id is { } i && lien is not (null or Assure)) await SansAyantDroitAsync(patients, i, ct);
            return;
        }
        if (principal == id)
            throw new RegleMetierException("Un patient ne peut pas être son propre assuré principal.", ChampPrincipal);
        if (lien == Assure)
            throw new RegleMetierException("Lien « assuré » : la fiche est celle de l’assuré lui-même, sans assuré principal.", ChampPrincipal);
        var p = await patients.FicheAsync(principal, ct) ?? throw new RegleMetierException("Assuré principal introuvable.", ChampPrincipal);
        var nom = $"{p.Nom} {p.Prenom}".Trim();
        if (!PeutEtrePrincipal(p.Lien, p.AssurePrincipalId))
            throw new RegleMetierException($"{nom} est un ayant droit ({Libelle(p.Lien)}{(p.AssurePrincipalId is null ? "" : ", rattaché à un autre assuré")}) : " +
                "choisissez l’assuré lui-même comme assuré principal.", ChampPrincipal);
        if (id is { } moi) await SansAyantDroitAsync(patients, moi, ct);
    }

    /// <summary>Pas de chaîne : un patient dont d'autres sont les ayants droit ne peut pas devenir lui-même ayant droit.</summary>
    private static async Task SansAyantDroitAsync(IPatients patients, long id, CancellationToken ct)
    {
        var ayants = (await patients.ProchesAsync(id, ct)).Where(p => p.AssurePrincipalId == id).Select(p => p.Nom).ToList();
        if (ayants.Count > 0)
            throw new RegleMetierException($"Ce patient est l’assuré principal de {string.Join(", ", ayants)} : il ne peut pas être lui-même un ayant droit.",
                ChampPrincipal);
    }

    /// <summary>
    /// Proposition de rattachement (sans automatisme) : fiche de même n° de base, de suffixe différent, qui peut être assuré principal
    /// (lien « assure » d'abord) ; null si la fiche a déjà un assuré principal, est elle-même l'assuré ou n'a pas de n° de base.
    /// </summary>
    public static ProchePatient? Proposition(long id, string? numAssure, string? lien, long? assurePrincipalId, IEnumerable<ProchePatient> proches)
    {
        if (assurePrincipalId is not null || lien == Assure || NumeroBase(numAssure) is not { } b) return null;
        var moi = RegleNumAssure.Chiffres(numAssure);
        return proches.Where(p => p.Id != id && NumeroBase(p.NumAssure) == b && RegleNumAssure.Chiffres(p.NumAssure) != moi
                                  && PeutEtrePrincipal(p.Lien, p.AssurePrincipalId) && p.AssurePrincipalId != id)
            .OrderByDescending(p => p.Lien == Assure).ThenBy(p => p.Id).FirstOrDefault();
    }

    /// <summary>
    /// Lignes de la fiche : « Ayant droit de » (assuré principal), « Famille » (ayants droit, avec leur lien) et « Même carte » (fiches de
    /// même n° de base non rattachées à celle-ci).
    /// </summary>
    public static IReadOnlyList<(string Libelle, string Valeur)> Affichage(long id, string? numAssure, long? assurePrincipalId, IEnumerable<ProchePatient> proches)
    {
        var liste = proches.Where(p => p.Id != id).ToList();
        var lignes = new List<(string, string)>();
        if (assurePrincipalId is { } pid && liste.FirstOrDefault(p => p.Id == pid) is { } principal) lignes.Add(("Ayant droit de", principal.Nom));
        var famille = liste.Where(p => p.AssurePrincipalId == id).OrderBy(p => p.Id)
            .Select(p => p.Lien is { } l and not Assure ? $"{p.Nom} ({Libelle(l)})" : p.Nom).ToList();
        if (famille.Count > 0) lignes.Add(("Famille", string.Join(", ", famille)));
        var b = NumeroBase(numAssure);
        var carte = b is null ? [] : liste.Where(p => NumeroBase(p.NumAssure) == b && p.Id != assurePrincipalId && p.AssurePrincipalId != id
                                                      && (assurePrincipalId is null || p.AssurePrincipalId != assurePrincipalId))
            .OrderBy(p => p.Id).Select(p => $"{p.Nom} ({p.NumAssure})").ToList();
        if (carte.Count > 0) lignes.Add(("Même carte", string.Join(", ", carte)));
        return lignes;
    }
}

/// <summary>Médecin prescripteur ; coordonnées facultatives (script 082).</summary>
/// <remarks>Wilaya : code de wilaya d'exercice (01 à 58, script 118), facultatif.</remarks>
public sealed record Medecin(long Id, string Nom, string? Specialite, string? Telephone = null, string? Adresse = null, string? Email = null,
    string? NumeroOrdre = null, string? CodeCnas = null, string? Wilaya = null);

/// <summary>Saisie d'un médecin (création ou correction) : le nom seul est obligatoire.</summary>
public sealed record SaisieMedecin(string? Nom, string? Specialite, string? Telephone = null, string? Adresse = null, string? Email = null,
    string? NumeroOrdre = null, string? CodeCnas = null, string? Wilaya = null)
{
    public const int AdresseMax = 300, NumeroOrdreMax = 30, TexteMax = 120;

    /// <summary>Contrôle et met en forme la saisie : nom obligatoire, téléphone et e-mail comme pour un patient, longueurs bornées.</summary>
    public Medecin Verifier(long id = 0)
    {
        if (string.IsNullOrWhiteSpace(Nom)) throw new RegleMetierException("Le nom du médecin est obligatoire.", "nom_medecin");
        var nom = Nom.Trim();
        if (nom.Length > TexteMax) throw new RegleMetierException($"Nom du médecin : {TexteMax} caractères au plus.", "nom_medecin");
        var specialite = Net(Specialite);
        if (specialite?.Length > TexteMax) throw new RegleMetierException($"Spécialité : {TexteMax} caractères au plus.", "specialite");
        var adresse = Net(Adresse);
        if (adresse?.Length > AdresseMax) throw new RegleMetierException($"Adresse du médecin : {AdresseMax} caractères au plus.", "adresse_medecin");
        var ordre = Net(NumeroOrdre);
        if (ordre?.Length > NumeroOrdreMax) throw new RegleMetierException($"N° d’ordre : {NumeroOrdreMax} caractères au plus.", "numero_ordre");
        var cnas = Net(CodeCnas);
        if (cnas?.Length > NumeroOrdreMax) throw new RegleMetierException($"Code CNAS : {NumeroOrdreMax} caractères au plus.", "code_cnas");
        return new Medecin(id, nom, specialite, Contacts.RegleContact.Telephone(Telephone), adresse, Contacts.RegleContact.Email(Email), ordre, cnas,
            Psychotropes.RegleWilaya.Normaliser(Wilaya, "wilaya_medecin"));
    }

    private static string? Net(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
}
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
    /// <summary>
    /// Patient (autre que <paramref name="sauf"/>) dont le n° d'assuré, réduit à ses chiffres (n° + suffixe), vaut <paramref name="chiffres"/> ;
    /// null s'il n'y en a pas (index unique ux_patient_num_assure, script 089).
    /// </summary>
    Task<(long Id, string Nom)?> AssureUtiliseAsync(string chiffres, long? sauf, CancellationToken ct = default) =>
        Task.FromResult<(long Id, string Nom)?>(null);
    /// <summary>Assuré principal, ayants droit et fiches de même n° d'assuré de base (12 chiffres) du patient (script 090).</summary>
    Task<IReadOnlyList<ProchePatient>> ProchesAsync(long id, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<ProchePatient>>([]);
}

/// <summary>
/// Un seul patient par n° d'assuré (décision du titulaire, 2026-11-15, script 089) : l'unicité porte sur le n° réduit à ses chiffres,
/// suffixe de l'ayant droit compris (« 690755003842/80 » et « 690755003842/01 » : deux patients ; « 690755003842 80 » = « 690755003842/80 »).
/// </summary>
public static class RegleNumAssure
{
    public const string Champ = "num_assure";
    public const string DebutMessage = "N° d’assuré déjà utilisé par ";

    /// <summary>Chiffres du n° d'assuré (n° + suffixe) ; null si le n° n'en contient aucun (non soumis à l'unicité).</summary>
    public static string? Chiffres(string? numAssure) =>
        numAssure is null ? null : new string(numAssure.Where(char.IsAsciiDigit).ToArray()) is { Length: > 0 } c ? c : null;

    public static string Message(string nom) => $"{DebutMessage}{nom} : ouvrez sa fiche plutôt que d’en créer une seconde.";

    /// <summary>Refus clair si le n° d'assuré est déjà celui d'un autre patient.</summary>
    public static async Task VerifierAsync(IPatients patients, string? numAssure, long? sauf, CancellationToken ct)
    {
        if (Chiffres(numAssure) is not { } c) return;
        if (await patients.AssureUtiliseAsync(c, sauf, ct) is { } autre)
            throw new RegleMetierException(Message(autre.Nom), Champ);
    }
}

/// <summary>
/// Modification de la fiche d'un patient (droit de vendre, comme sa création à la caisse). Chaque champ changé est inscrit au
/// journal, avec l'ancienne et la nouvelle valeur. Un téléphone repris hors format reste accepté tant qu'on n'y touche pas.
/// </summary>
public sealed class ModifierPatient(IPatients patients, Commun.IJournal journal, Commun.IHorloge horloge, Securite.IPermissionService? droits = null)
{
    public async Task ExecuterAsync(FichePatient saisie, long utilisateurId, CancellationToken ct = default)
    {
        var actuelle = await patients.FicheAsync(saisie.Id, ct) ?? throw new RegleMetierException("Patient introuvable.");
        if (string.IsNullOrWhiteSpace(saisie.Nom)) throw new RegleMetierException("Le nom du patient est obligatoire.", "nom_patient");
        if (saisie.PlafondCredit < 0) throw new RegleMetierException("Le plafond de crédit ne peut pas être négatif.", "plafond");
        // Relever le plafond de crédit : droit de dépasser le plafond (titulaire compris par ses droits) ; le baisser ou le laisser
        // tel quel reste permis avec le seul droit de vendre, comme le reste de la fiche
        if (saisie.PlafondCredit > actuelle.PlafondCredit)
            await PlafondCredit.ExigerDroitAsync(droits, utilisateurId, "relever le plafond de crédit d’un patient", ct);
        if (saisie.DateNaissance is { } dn && dn > horloge.Aujourdhui)
            throw new RegleMetierException("La date de naissance ne peut pas être dans le futur.", "date_naissance");
        static string? Net(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
        var tel = Net(saisie.Telephone) is { } t && t != actuelle.Telephone?.Trim() ? Contacts.RegleContact.Telephone(t) : Net(saisie.Telephone);
        var fiche = saisie with
        {
            Nom = saisie.Nom.Trim(), Prenom = Net(saisie.Prenom), Telephone = tel, Email = Contacts.RegleContact.Email(saisie.Email),
            NumAssure = Net(saisie.NumAssure), OrganismeId = saisie.OrganismeId is > 0 ? saisie.OrganismeId : null, SoldeCredit = actuelle.SoldeCredit,
            Lien = FamillePatient.Lien(saisie.Lien), AssurePrincipalId = saisie.AssurePrincipalId is > 0 ? saisie.AssurePrincipalId : null
        };
        if (RegleNumAssure.Chiffres(fiche.NumAssure) != RegleNumAssure.Chiffres(actuelle.NumAssure))
            await RegleNumAssure.VerifierAsync(patients, fiche.NumAssure, fiche.Id, ct);
        if (fiche.Lien != actuelle.Lien || fiche.AssurePrincipalId != actuelle.AssurePrincipalId)
            await FamillePatient.VerifierAsync(patients, fiche.Id, fiche.Lien, fiche.AssurePrincipalId, ct);
        if (!await patients.ModifierAsync(fiche, ct)) throw new RegleMetierException("Patient introuvable.");

        var changes = new Dictionary<string, object?>();
        void Comparer(string nom, object? avant, object? apres) { if (!Equals(avant, apres)) changes[nom] = new { avant, apres }; }
        Comparer("nom", actuelle.Nom, fiche.Nom); Comparer("prenom", actuelle.Prenom, fiche.Prenom);
        Comparer("date_naissance", actuelle.DateNaissance, fiche.DateNaissance); Comparer("telephone", actuelle.Telephone, fiche.Telephone);
        Comparer("email", actuelle.Email, fiche.Email); Comparer("num_assure", actuelle.NumAssure, fiche.NumAssure);
        Comparer("organisme_id", actuelle.OrganismeId, fiche.OrganismeId); Comparer("chronique", actuelle.Chronique, fiche.Chronique);
        Comparer("plafond_credit", actuelle.PlafondCredit, fiche.PlafondCredit);
        Comparer("date_fin_droit", actuelle.DateFinDroit, fiche.DateFinDroit);
        Comparer("lien", actuelle.Lien, fiche.Lien); Comparer("assure_principal_id", actuelle.AssurePrincipalId, fiche.AssurePrincipalId);
        if (changes.Count > 0)
            await journal.EcrireAsync(utilisateurId, "patient_modifie", "patient", fiche.Id, changes, Pharma.Domain.Securite.Permissions.CaisseVendre, ct);
    }
}

public sealed class LireFichePatient(IPatients patients)
{
    public async Task<FichePatient> ExecuterAsync(long id, CancellationToken ct = default) =>
        await patients.FicheAsync(id, ct) ?? throw new RegleMetierException("Patient introuvable.");

    /// <summary>Assuré principal, ayants droit et fiches de même n° de base : proposition de rattachement et choix de l'assuré principal.</summary>
    public Task<IReadOnlyList<ProchePatient>> ProchesAsync(long id, CancellationToken ct = default) => patients.ProchesAsync(id, ct);
}

public interface IMedecins
{
    Task<IReadOnlyList<Medecin>> RechercherAsync(string texte, int limite, CancellationToken ct = default);
    /// <summary>Médecins sans wilaya (à renseigner), filtrés par nom si <paramref name="texte"/> est donné (script 118).</summary>
    Task<IReadOnlyList<Medecin>> SansWilayaAsync(string? texte, int limite, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<Medecin>>([]);
    /// <summary>Crée le médecin (son Id est ignoré) et renvoie son identifiant.</summary>
    Task<long> CreerAsync(Medecin medecin, CancellationToken ct = default);
    /// <summary>Remplace le nom, la spécialité et les coordonnées ; faux si le médecin n'existe pas.</summary>
    Task<bool> ModifierAsync(Medecin medecin, CancellationToken ct = default);
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

/// <summary>Plafond de crédit d'un patient : le relever exige le droit de dépasser le plafond (revue B2).</summary>
public static class PlafondCredit
{
    /// <summary>Droit « credit.depasser_plafond », ou titulaire ; refus : DroitRefuseException (autorisable par le code d'un collègue).</summary>
    public static async Task ExigerDroitAsync(Securite.IPermissionService? droits, long utilisateurId, string action, CancellationToken ct)
    {
        if (droits is null || await droits.EstTitulaireAsync(utilisateurId, ct)) return;
        await Securite.PermissionServiceExtensions.ExigerAsync(droits, utilisateurId, Pharma.Domain.Securite.Permissions.CreditDepasserPlafond, action, ct);
    }
}

/// <summary>
/// Création rapide d'un patient depuis la caisse : l'employé qui le crée est gardé et la création inscrite au journal.
/// Un plafond de crédit supérieur au plafond par défaut (Administration › Caisse) exige le droit de dépasser le plafond.
/// </summary>
public sealed class CreerPatient(IPatients patients, Commun.IJournal? journal = null, Securite.IPermissionService? droits = null,
    Caisse.ISessionsCaisse? caisse = null)
{
    public async Task<long> ExecuterAsync(NouveauPatient p, long? utilisateurId = null, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(p.Nom))
            throw new RegleMetierException("Le nom du patient est obligatoire.", "nom_patient");
        if (p.PlafondCredit < 0)
            throw new RegleMetierException("Le plafond de crédit ne peut pas être négatif.", "plafond");
        if (droits is not null && utilisateurId is { } auteur)
        {
            var defaut = caisse is null ? 0m : (await caisse.ParametresAsync(ct)).PlafondCreditDefaut;
            if (p.PlafondCredit > defaut)
                await PlafondCredit.ExigerDroitAsync(droits, auteur, "créer un patient avec un plafond de crédit supérieur au plafond par défaut", ct);
        }
        static string? Net(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
        await RegleNumAssure.VerifierAsync(patients, Net(p.NumAssure), null, ct);
        var id = await patients.CreerAsync(p with
        {
            Nom = p.Nom.Trim(), Prenom = Net(p.Prenom), Telephone = Contacts.RegleContact.Telephone(p.Telephone), NumAssure = Net(p.NumAssure),
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

    /// <summary>Filtre « sans wilaya » de l'écran des médecins : 50 au plus, par nom.</summary>
    public const int LimiteSansWilaya = 50;

    public Task<IReadOnlyList<Medecin>> SansWilayaAsync(string? texte, CancellationToken ct = default) =>
        medecins.SansWilayaAsync(string.IsNullOrWhiteSpace(texte) ? null : texte.Trim(), LimiteSansWilaya, ct);
}

/// <summary>
/// Correction d'un médecin prescripteur (droit de vendre, comme sa création à la caisse) : nom, spécialité et coordonnées
/// (téléphone, adresse, e-mail, n° d'ordre, code CNAS, facultatifs) ; inscrite au journal.
/// </summary>
public sealed class ModifierMedecin(IMedecins medecins, Commun.IJournal journal)
{
    public async Task ExecuterAsync(long id, SaisieMedecin saisie, long utilisateurId, CancellationToken ct = default)
    {
        var m = saisie.Verifier(id);
        if (!await medecins.ModifierAsync(m, ct)) throw new RegleMetierException("Médecin introuvable.");
        await journal.EcrireAsync(utilisateurId, "medecin_modifie", "medecin", id,
            new Dictionary<string, object?>
            {
                ["nom"] = m.Nom, ["specialite"] = m.Specialite, ["telephone"] = m.Telephone, ["adresse"] = m.Adresse,
                ["email"] = m.Email, ["numero_ordre"] = m.NumeroOrdre, ["code_prestataire"] = m.CodeCnas, ["wilaya"] = m.Wilaya
            }, Pharma.Domain.Securite.Permissions.CaisseVendre, ct);
    }
}

/// <summary>Création d'un médecin prescripteur à la caisse : nom obligatoire, spécialité et coordonnées facultatives.</summary>
public sealed class CreerMedecin(IMedecins medecins)
{
    public Task<long> ExecuterAsync(SaisieMedecin saisie, CancellationToken ct = default) => medecins.CreerAsync(saisie.Verifier(), ct);
}

public sealed class ListerOrganismes(IOrganismes organismes)
{
    public Task<IReadOnlyList<Organisme>> ExecuterAsync(CancellationToken ct = default) => organismes.ListerAsync(ct);
}
