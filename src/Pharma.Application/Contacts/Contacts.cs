using System.Text.RegularExpressions;
using Pharma.Application.Securite;
using Pharma.Domain.Commun;
using Pharma.Domain.Securite;

namespace Pharma.Application.Contacts;

/// <summary>Types de tiers qui ont une fiche contact.</summary>
public static class TypesContact
{
    public const string Patient = "patient";
    public const string Grossiste = "grossiste";
    public const string Organisme = "organisme";
    public const string Client = "client";
    public const string Employe = "employe";
    public static readonly string[] Tous = [Patient, Grossiste, Organisme, Client, Employe];
}

/// <summary>
/// Une information de la fiche. Genre : « telephone » ou « email » (le poste en fait un lien d'appel ou de courrier),
/// « montant_du » (somme due, mise en avant), sinon texte simple.
/// </summary>
public sealed record InfoContact(string Libelle, string Valeur, string? Genre = null);

/// <summary>
/// Fiche contact d'un tiers : ses coordonnées, puis sa situation utile (dette, factures ouvertes, rôle…).
/// Precision : sous-titre court (« Patient », « Grossiste », sigle de l'organisme, rôle de l'employé).
/// CoordonneesMasquees : les coordonnées d'un employé ne sont montrées qu'à lui-même et au titulaire.
/// </summary>
/// <remarks>Saisie : valeurs actuelles des coordonnées modifiables depuis la fiche (patient, grossiste, entreprise) ; Modifiable : l'employé a le droit de les changer.</remarks>
public sealed record FicheContact(string Type, long Id, string Nom, string Precision, bool Actif,
    IReadOnlyList<InfoContact> Coordonnees, IReadOnlyList<InfoContact> Situation, bool CoordonneesMasquees = false,
    CoordonneesContact? Saisie = null, bool Modifiable = false);

/// <summary>Coordonnées modifiables depuis la fiche contact. Adresse : grossiste et entreprise seulement (un patient n'en a pas).</summary>
public sealed record CoordonneesContact(string? Telephone, string? Email, string? Adresse);

public interface IContacts
{
    /// <summary>Fiche du tiers, ou null s'il n'existe pas. <paramref name="coordonneesEmploye"/> : faux pour masquer celles d'un employé.</summary>
    Task<FicheContact?> LireAsync(string type, long id, bool coordonneesEmploye, CancellationToken ct = default);

    /// <summary>Remplace le téléphone, l'e-mail (et l'adresse d'un grossiste ou d'une entreprise). Faux si le tiers n'existe pas.</summary>
    Task<bool> ModifierCoordonneesAsync(string type, long id, CoordonneesContact c, CancellationToken ct = default);
}

/// <summary>
/// Fiche contact d'un tiers, ouverte depuis n'importe quelle ligne qui le concerne. Droits : patient, droit de vendre (comme le
/// règlement des crédits) ; entreprise cliente, droit de facturer ; grossiste, organisme et employé, tout employé connecté
/// (la liste des grossistes est ouverte à tous). Coordonnées d'un employé : lui-même, le titulaire ou qui gère les utilisateurs.
/// </summary>
public sealed class ConsulterContact(IContacts contacts, IPermissionService droits)
{
    public async Task<FicheContact> ExecuterAsync(string? type, long id, long utilisateurId, CancellationToken ct = default)
    {
        type = type?.Trim().ToLowerInvariant();
        if (type is null || !TypesContact.Tous.Contains(type)) throw new RegleMetierException("Type de contact inconnu.");
        if (type == TypesContact.Patient) await droits.ExigerAsync(utilisateurId, Permissions.CaisseVendre, "consulter la fiche d'un patient", ct);
        if (type == TypesContact.Client) await droits.ExigerAsync(utilisateurId, Permissions.FacturesGerer, "consulter la fiche d'un client", ct);

        var coordonneesEmploye = type != TypesContact.Employe || id == utilisateurId
                                 || await droits.EstTitulaireAsync(utilisateurId, ct)
                                 || await droits.APermissionAsync(utilisateurId, Permissions.AdminUtilisateurs, ct);
        var fiche = await contacts.LireAsync(type, id, coordonneesEmploye, ct) ?? throw new RegleMetierException("Ce contact n'existe plus.");
        var modifiable = ModifierCoordonneesContact.Droit(type) is { } droit && await droits.APermissionAsync(utilisateurId, droit, ct);
        return fiche with { Modifiable = modifiable && fiche.Saisie is not null };
    }
}

/// <summary>
/// Téléphone, e-mail et adresse d'un patient, d'un grossiste ou d'une entreprise cliente, corrigés depuis sa fiche contact ;
/// inscrit au journal. Droits : patient, droit de vendre (qui le crée à la caisse) ; grossiste, droit de commander ; entreprise,
/// droit de facturer. Les organismes et les employés se modifient dans leurs écrans d'administration.
/// </summary>
public sealed class ModifierCoordonneesContact(IContacts contacts, IPermissionService droits, Commun.IJournal journal)
{
    public static string? Droit(string type) => type switch
    {
        TypesContact.Patient => Permissions.CaisseVendre,
        TypesContact.Grossiste => Permissions.AchatsCommander,
        TypesContact.Client => Permissions.FacturesGerer,
        _ => null
    };

    private static readonly Dictionary<string, string> Tables = new()
    {
        [TypesContact.Patient] = "patient", [TypesContact.Grossiste] = "fournisseur", [TypesContact.Client] = "client_facture"
    };

    public async Task ExecuterAsync(string? type, long id, string? telephone, string? email, string? adresse, long utilisateurId, CancellationToken ct = default)
    {
        type = type?.Trim().ToLowerInvariant();
        if (type is null || Droit(type) is not { } droit)
            throw new RegleMetierException("Les coordonnées de ce contact se modifient dans son écran d'administration.");
        await droits.ExigerAsync(utilisateurId, droit, "modifier les coordonnées d'un contact", ct);
        var actuelles = (await contacts.LireAsync(type, id, true, ct))?.Saisie ?? throw new RegleMetierException("Ce contact n'existe plus.");
        // Téléphone repris tel quel de l'ancien logiciel : gardé sans contrôle tant qu'on n'y touche pas (sinon il bloquerait l'ajout d'un e-mail)
        var tel = string.IsNullOrWhiteSpace(telephone) ? null
            : telephone.Trim() == actuelles.Telephone?.Trim() ? actuelles.Telephone : RegleContact.Telephone(telephone);
        var c = new CoordonneesContact(tel, RegleContact.Email(email),
            type == TypesContact.Patient || string.IsNullOrWhiteSpace(adresse) ? null : adresse.Trim());
        if (c.Adresse is { Length: > 300 }) throw new RegleMetierException("L'adresse est trop longue (300 caractères au plus).", "adresse");
        if (!await contacts.ModifierCoordonneesAsync(type, id, c, ct)) throw new RegleMetierException("Ce contact n'existe plus.");
        await journal.EcrireAsync(utilisateurId, "coordonnees_modifiees", Tables[type], id,
            new Dictionary<string, object?> { ["type"] = type, ["telephone"] = c.Telephone, ["email"] = c.Email, ["adresse"] = c.Adresse }, droit, ct);
    }
}

/// <summary>Contrôle et mise en forme des coordonnées saisies (employés, organismes).</summary>
public static partial class RegleContact
{
    public const int LongueurMax = 120;

    /// <summary>Téléphone : chiffres, espaces, +, -, ., parenthèses ; 6 chiffres au moins. Vide : aucun.</summary>
    public static string? Telephone(string? saisi)
    {
        if (string.IsNullOrWhiteSpace(saisi)) return null;
        var t = Espaces().Replace(saisi.Trim(), " ");
        if (t.Length > 30 || !TelephoneValide().IsMatch(t) || t.Count(char.IsAsciiDigit) < 6)
            throw new RegleMetierException($"Numéro de téléphone invalide : « {t} ».", "telephone");
        return t;
    }

    /// <summary>E-mail : une adresse de la forme nom@domaine.ext, sans espace. Vide : aucune.</summary>
    public static string? Email(string? saisi)
    {
        if (string.IsNullOrWhiteSpace(saisi)) return null;
        var e = saisi.Trim();
        if (e.Length > LongueurMax || !EmailValide().IsMatch(e)) throw new RegleMetierException($"Adresse e-mail invalide : « {e} ».", "email");
        return e;
    }

    [GeneratedRegex(@"\s+")] private static partial Regex Espaces();
    [GeneratedRegex(@"^\+?[0-9 ().\-]+$")] private static partial Regex TelephoneValide();
    [GeneratedRegex(@"^[^@\s]+@[^@\s]+\.[^@\s]+$")] private static partial Regex EmailValide();
}
