using System.Text.Json;
using Pharma.Application.Administration;
using Pharma.Application.Commun;
using Pharma.Application.Notifications;
using Pharma.Application.Securite;
using Pharma.Domain.Commun;

namespace Pharma.Application.Synchronisation;

// ---- Synchronisation, lots S5.1 et S5.5 : ordres envoyés par le serveur central (docs/conception-synchronisation.md § 24) ----
// Les ordres arrivent par la voie descendante (sync_ordre_recu, script 145). Ils sont appliqués ICI, par les mêmes cas d'usage et
// contrôles que les écrans. Un ordre n'est appliqué que si la valeur actuelle est encore celle vue au serveur central (« valeur
// attendue ») ; sinon il est refusé « modifié dans l'officine ». Les ordres sur les employés et les droits attendent la confirmation du
// titulaire, sauf le blocage d'un employé (protecteur, appliqué tout de suite). Jamais le compte ni le rôle d'un titulaire. Un ordre
// non traité expire à la date fixée par le serveur central (7 jours), contrôlée aussi avec l'horloge de l'officine.

public static class TypesOrdre
{
    public const string Essai = "essai";
    public const string EmployeCreer = "employe_creer";
    public const string EmployeModifier = "employe_modifier";
    public const string EmployeBloquer = "employe_bloquer";
    public const string EmployeDebloquer = "employe_debloquer";
    public const string DroitsRole = "droits_role";
    /// <summary>Lot S5.2 : prix public et/ou tarif de référence d'un produit (jamais le remboursement ni la TVA).</summary>
    public const string PrixProduit = "prix_produit";
    /// <summary>Lot S5.3 : nouvelle nomenclature nationale (fichier téléchargé au serveur central à la confirmation).</summary>
    public const string Nomenclature = "nomenclature";
    public static readonly IReadOnlySet<string> Connus = new HashSet<string>(StringComparer.Ordinal)
        { Essai, EmployeCreer, EmployeModifier, EmployeBloquer, EmployeDebloquer, DroitsRole, PrixProduit, Nomenclature };

    /// <summary>Confirmation du titulaire exigée avant application (décision S5-D3) ; le blocage et l'essai s'appliquent tout de suite.</summary>
    public static bool ConfirmationRequise(string type) => type is EmployeCreer or EmployeModifier or EmployeDebloquer or DroitsRole or Nomenclature;

    public static string Libelle(string type) => type switch
    {
        Essai => "Ordre d’essai",
        EmployeCreer => "Créer un employé",
        EmployeModifier => "Modifier un employé",
        EmployeBloquer => "Bloquer un employé",
        EmployeDebloquer => "Débloquer un employé",
        DroitsRole => "Droits d’un rôle",
        PrixProduit => "Prix d’un produit",
        Nomenclature => "Nomenclature nationale",
        _ => type
    };
}

public static class StatutsOrdre
{
    public const string AAppliquer = "a_appliquer";
    public const string AConfirmer = "a_confirmer";
    public const string Applique = "applique";
    public const string Refuse = "refuse";
    public const string Expire = "expire";
    public const string Annule = "annule";
    public static readonly IReadOnlySet<string> Tous = new HashSet<string>(StringComparer.Ordinal) { AAppliquer, AConfirmer, Applique, Refuse, Expire, Annule };
}

/// <summary>Ordre reçu du serveur central. Attendu et Donnees : objets JSON (format ordre-v1).</summary>
public sealed record OrdreRecu(Guid Id, string Type, string? Cible, string AttenduJson, string DonneesJson, string Auteur, DateTime CreeLeUtc,
    DateTime ExpireLeUtc, string Statut, string? Motif, DateTime RecuLeUtc, DateTime? TraiteLeUtc, long? TraitePar);

public sealed record PageOrdresRecus(IReadOnlyList<OrdreRecu> Ordres, int Total, int AConfirmer);

public interface IOrdresCentrauxDepot
{
    /// <summary>Ordres à appliquer ou à confirmer (les plus anciens d'abord).</summary>
    Task<IReadOnlyList<OrdreRecu>> EnCoursAsync(CancellationToken ct = default);
    Task<OrdreRecu?> LireAsync(Guid id, CancellationToken ct = default);
    Task<PageOrdresRecus> ListerAsync(string? statut, int page, int taille, CancellationToken ct = default);
    /// <summary>Change l'état (sync_ordre_noter) : ordre, résultat envoyé au central, journal des actions.</summary>
    Task NoterAsync(Guid id, string statut, string? motif, long? par, CancellationToken ct = default);
    Task<bool> BaseEnLectureSeuleAsync(CancellationToken ct = default);
    /// <summary>
    /// Lot S5.2 : prix public et/ou tarif de référence (null : inchangé), écrits SEULEMENT si les valeurs actuelles sont encore celles
    /// attendues (vérifié sous verrou de la ligne) ; journal « prix_modifie » (source : ordre du serveur central, auteur, n° d'ordre),
    /// au nom du titulaire qui confirme ou sans auteur local. Faux : valeurs changées entre-temps, rien n'est écrit.
    /// </summary>
    Task<bool> ModifierPrixAsync(long produitId, decimal? prix, decimal? tarif, decimal prixAttendu, decimal? tarifAttendu, long? par,
        string auteurCentral, Guid ordre, CancellationToken ct = default);
}

/// <summary>L'ordre ne peut pas être appliqué tel quel (valeur changée dans l'officine, cible interdite ou introuvable) : refus définitif.</summary>
public sealed class OrdreInapplicableException(string message) : Exception(message);

/// <summary>Application d'un ordre par les cas d'usage existants ; <paramref name="titulaireId"/> : titulaire qui confirme (null : automatique).</summary>
public sealed class ExecuterOrdreCentral(IUtilisateursAdmin admin, CreerUtilisateur creer, ModifierUtilisateur modifier, EnregistrerDroits droitsRole,
    Catalogue.IProduitsModification produits, IOrdresCentrauxDepot depot, Catalogue.ImporterNomenclature nomenclature,
    ITelechargementReferentiel referentiels)
{
    public const string ModifieDansOfficine = "modifié dans l’officine depuis l’envoi de l’ordre";
    /// <summary>Écart de prix au-delà duquel le titulaire confirme (décision S5.2-D1) : 20 %.</summary>
    public const decimal EcartPrixMax = 0.20m;

    /// <summary>Écart trop grand (ou valeur actuelle absente ou nulle) : confirmation du titulaire.</summary>
    public static bool EcartTropGrand(decimal? actuel, decimal? nouveau) =>
        nouveau is { } n && (actuel is not { } a || a <= 0 || Math.Abs(n - a) / a > EcartPrixMax);

    /// <summary>Confirmation du titulaire exigée pour cet ordre : par type (employés, droits), ou prix hors de l'écart permis.</summary>
    public async Task<bool> ConfirmationRequiseAsync(OrdreRecu o, CancellationToken ct = default)
    {
        if (TypesOrdre.ConfirmationRequise(o.Type)) return true;
        if (o.Type != TypesOrdre.PrixProduit || !long.TryParse(o.Cible, out var id) || await produits.LireAsync(id, ct) is not { } fiche) return false;
        using var donnees = JsonDocument.Parse(o.DonneesJson);
        return EcartTropGrand(fiche.PrixPublic, Montant(donnees.RootElement, "prix_public"))
               || EcartTropGrand(fiche.TarifReference, Montant(donnees.RootElement, "tarif_reference"));
    }

    /// <summary>
    /// Lot S5.3 : télécharge le fichier désigné, vérifie taille et empreinte SHA-256, puis l'importe comme un import manuel
    /// (journal, produits « à vérifier ») ; même version déjà chargée : rien ne change (décision S5.3-D5). Central injoignable :
    /// l'ordre reste à confirmer ; fichier refusé (404), différent ou illisible : refus définitif.
    /// </summary>
    private async Task ImporterNomenclatureAsync(JsonElement d, long titulaireId, CancellationToken ct)
    {
        var fichier = d.TryGetProperty("fichier", out var f) && f.TryGetInt64(out var fid) ? fid
            : throw new OrdreInapplicableException("Ordre de nomenclature sans n° de fichier.");
        var empreinte = d.TryGetProperty("empreinte", out var e) && e.ValueKind == JsonValueKind.String ? e.GetString()! : "";
        var octets = d.TryGetProperty("octets", out var t) && t.TryGetInt64(out var n) ? n : 0;
        if (octets is <= 0 or > Catalogue.ImporterNomenclature.TailleMax)
            throw new OrdreInapplicableException("Taille du fichier de nomenclature absente ou trop grande.");
        byte[] contenu;
        try { contenu = await referentiels.TelechargerAsync(fichier, octets, ct); }
        catch (ServeurCentralException x) when (x.CodeHttp == 404)
        {
            throw new OrdreInapplicableException("Fichier de nomenclature inconnu du serveur central.");
        }
        catch (ServeurCentralException x)
        {
            throw new RegleMetierException($"Téléchargement de la nomenclature impossible ({x.Message}) : l’ordre reste à confirmer, réessayez plus tard.");
        }
        if (contenu.LongLength != octets || !string.Equals(Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(contenu)), empreinte, StringComparison.Ordinal))
            throw new OrdreInapplicableException("Fichier de nomenclature reçu différent de celui envoyé (empreinte) : rien n’est importé.");
        var nom = d.TryGetProperty("nom_fichier", out var nf) && nf.ValueKind == JsonValueKind.String ? nf.GetString() : null;
        using var flux = new MemoryStream(contenu, writable: false);
        try { await nomenclature.ImporterSiNouvelleAsync(flux, nom, titulaireId, ct); }
        catch (RegleMetierException x) { throw new OrdreInapplicableException($"Fichier de nomenclature refusé : {x.Message}"); }
    }

    private static decimal? Montant(JsonElement e, string nom) =>
        e.TryGetProperty(nom, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDecimal() : null;

    public async Task ExecuterAsync(OrdreRecu o, long? titulaireId, string? motDePasse, CancellationToken ct = default)
    {
        using var attendu = JsonDocument.Parse(o.AttenduJson);
        using var donnees = JsonDocument.Parse(o.DonneesJson);
        var a = attendu.RootElement;
        var d = donnees.RootElement;
        if (o.Type == TypesOrdre.Essai) return;
        if (TypesOrdre.ConfirmationRequise(o.Type) && titulaireId is null)
            throw new InvalidOperationException("Ordre à confirmer par le titulaire : application automatique interdite.");
        var etat = await admin.LireAsync(ct);
        switch (o.Type)
        {
            case TypesOrdre.EmployeCreer:
            {
                var role = Role(etat, Texte(d, "role"));
                if (string.IsNullOrEmpty(motDePasse)) throw new RegleMetierException("Mot de passe provisoire obligatoire pour créer le compte.", "mot_de_passe");
                await creer.ExecuterAsync(Texte(d, "nom"), Texte(d, "identifiant"), motDePasse, role.Id, titulaireId!.Value, ct: ct);
                return;
            }
            case TypesOrdre.EmployeModifier or TypesOrdre.EmployeBloquer or TypesOrdre.EmployeDebloquer:
            {
                var u = Employe(etat, o.Cible);
                VerifierAttenduEmploye(a, u, etat);
                if (o.Type == TypesOrdre.EmployeBloquer)
                {
                    // Protecteur, sans confirmation : seul l'état actif change (les sessions de l'employé tombent à l'appel suivant)
                    await admin.ModifierAsync(u.Id, u.NomComplet, u.RoleId, false, null, u.Telephone, u.Email, ct);
                    return;
                }
                var roleId = u.RoleId;
                if (o.Type == TypesOrdre.EmployeModifier && d.TryGetProperty("role", out var r) && r.ValueKind == JsonValueKind.String)
                    roleId = Role(etat, r.GetString()).Id;
                var nom = o.Type == TypesOrdre.EmployeModifier && d.TryGetProperty("nom", out var n) && n.ValueKind == JsonValueKind.String
                    ? n.GetString()! : u.NomComplet;
                var actif = o.Type == TypesOrdre.EmployeDebloquer || u.Actif;
                await modifier.ExecuterAsync(u.Id, nom, roleId, actif, null, titulaireId!.Value, u.Telephone, u.Email, ct);
                return;
            }
            case TypesOrdre.PrixProduit:
            {
                var fiche = long.TryParse(o.Cible, out var pid) ? await produits.LireAsync(pid, ct) : null;
                if (fiche is null) throw new OrdreInapplicableException($"Produit n° {o.Cible} introuvable dans l’officine.");
                decimal? prixAttendu = Montant(a, "prix_public"), tarifAttendu = Montant(a, "tarif_reference");
                if (prixAttendu != fiche.PrixPublic || tarifAttendu != fiche.TarifReference)
                    throw new OrdreInapplicableException($"Prix de « {fiche.NomCommercial} » {ModifieDansOfficine}.");
                decimal? prix = Montant(d, "prix_public"), tarif = Montant(d, "tarif_reference");
                // Mêmes contrôles que la fiche produit (ModifierProduit) pour ce qui peut changer ici
                if (prix is <= 0) throw new OrdreInapplicableException("Le prix public doit être supérieur à 0.");
                if (tarif is <= 0) throw new OrdreInapplicableException("Le tarif de référence doit être supérieur à 0.");
                if (titulaireId is null && (EcartTropGrand(fiche.PrixPublic, prix) || EcartTropGrand(fiche.TarifReference, tarif)))
                    throw new InvalidOperationException("Écart de prix au-delà de 20 % : confirmation du titulaire exigée.");
                if (!await depot.ModifierPrixAsync(fiche.Id, prix, tarif, fiche.PrixPublic, fiche.TarifReference, titulaireId, o.Auteur, o.Id, ct))
                    throw new OrdreInapplicableException($"Prix de « {fiche.NomCommercial} » {ModifieDansOfficine}.");
                return;
            }
            case TypesOrdre.Nomenclature:
                await ImporterNomenclatureAsync(d, titulaireId!.Value, ct);
                return;
            case TypesOrdre.DroitsRole:
            {
                var role = Role(etat, o.Cible);
                var actuels = role.Permissions.Order(StringComparer.Ordinal).ToList();
                if (!a.TryGetProperty("permissions", out var pa) || pa.ValueKind != JsonValueKind.Array
                    || !pa.EnumerateArray().Select(x => x.GetString() ?? "").Order(StringComparer.Ordinal).SequenceEqual(actuels))
                    throw new OrdreInapplicableException($"Droits du rôle « {role.Nom} » {ModifieDansOfficine}.");
                var voulus = d.GetProperty("permissions").EnumerateArray().Select(x => x.GetString() ?? "").ToList();
                await droitsRole.ExecuterAsync([new DroitsRole(role.Id, voulus)], titulaireId!.Value, ct);
                return;
            }
            default:
                throw new OrdreInapplicableException($"Type d’ordre non pris en charge : {o.Type}.");
        }
    }

    private static RoleAdmin Role(EtatDroits etat, string? code)
    {
        var role = etat.Roles.FirstOrDefault(r => r.Code == code) ?? throw new OrdreInapplicableException($"Rôle « {code} » inconnu dans l’officine.");
        if (role.Systeme) throw new OrdreInapplicableException("Le rôle de pharmacien titulaire ne se donne ni ne se modifie à distance.");
        return role;
    }

    private static UtilisateurAdmin Employe(EtatDroits etat, string? cible)
    {
        var u = long.TryParse(cible, out var id) ? etat.Utilisateurs.FirstOrDefault(x => x.Id == id) : null;
        if (u is null) throw new OrdreInapplicableException($"Employé n° {cible} introuvable dans l’officine.");
        if (u.RoleSysteme) throw new OrdreInapplicableException("Le compte d’un pharmacien titulaire ne se modifie jamais à distance.");
        return u;
    }

    /// <summary>Valeurs vues au serveur central (rôle, état actif, nom s'il était connu) : toutes encore égales, sinon refus.</summary>
    private static void VerifierAttenduEmploye(JsonElement a, UtilisateurAdmin u, EtatDroits etat)
    {
        var codeRole = etat.Roles.FirstOrDefault(r => r.Id == u.RoleId)?.Code;
        if ((a.TryGetProperty("role", out var r) && r.GetString() != codeRole)
            || (a.TryGetProperty("actif", out var ac) && ac.ValueKind is JsonValueKind.True or JsonValueKind.False && ac.GetBoolean() != u.Actif)
            || (a.TryGetProperty("nom", out var n) && n.GetString() != u.NomComplet))
            throw new OrdreInapplicableException($"Employé « {u.Identifiant} » {ModifieDansOfficine}.");
    }

    private static string? Texte(JsonElement e, string nom) => e.TryGetProperty(nom, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}

/// <summary>
/// Passage automatique (après chaque passage de synchronisation) : expire les ordres périmés, met « à confirmer » ceux qui l'exigent,
/// applique les autres. Alerte au titulaire pour un ordre à confirmer, refusé ou expiré, et pour un blocage appliqué (décision S5.1-D1).
/// Rien sur une base en lecture seule (2e serveur en veille). Jamais sur le chemin d'une vente.
/// </summary>
public sealed class AppliquerOrdresCentraux(IOrdresCentrauxDepot depot, ExecuterOrdreCentral executer, IHorloge horloge, IAlertesTitulaire alertes,
    ITraceErreurs? trace = null)
{
    /// <returns>Nombre d'ordres dont l'état a changé.</returns>
    public async Task<int> ExecuterAsync(CancellationToken ct = default)
    {
        if (await depot.BaseEnLectureSeuleAsync(ct)) return 0;
        var n = 0;
        foreach (var o in await depot.EnCoursAsync(ct))
        {
            if (horloge.MaintenantUtc >= o.ExpireLeUtc)
            {
                await NoterEtPrevenirAsync(o, StatutsOrdre.Expire, "non traité avant sa date limite (horloge de l’officine)", ct);
                n++;
                continue;
            }
            if (o.Statut != StatutsOrdre.AAppliquer) continue;
            if (!TypesOrdre.Connus.Contains(o.Type))
            {
                await NoterEtPrevenirAsync(o, StatutsOrdre.Refuse, $"type d’ordre non pris en charge par cette version : {o.Type}", ct);
                n++;
                continue;
            }
            if (await executer.ConfirmationRequiseAsync(o, ct))
            {
                await NoterEtPrevenirAsync(o, StatutsOrdre.AConfirmer,
                    o.Type == TypesOrdre.PrixProduit ? "écart de prix de plus de 20 % : confirmation du titulaire" : null, ct);
                n++;
                continue;
            }
            try
            {
                await executer.ExecuterAsync(o, null, null, ct);
                await depot.NoterAsync(o.Id, StatutsOrdre.Applique, null, null, ct);
                if (o.Type == TypesOrdre.EmployeBloquer) await PrevenirAsync(o, StatutsOrdre.Applique, null, ct);
            }
            catch (Exception e) when (e is OrdreInapplicableException or RegleMetierException or JsonException or KeyNotFoundException)
            {
                await NoterEtPrevenirAsync(o, StatutsOrdre.Refuse, e is JsonException or KeyNotFoundException ? "ordre mal formé" : e.Message, ct);
            }
            n++;
        }
        return n;
    }

    private async Task NoterEtPrevenirAsync(OrdreRecu o, string statut, string? motif, CancellationToken ct)
    {
        await depot.NoterAsync(o.Id, statut, motif, null, ct);
        await PrevenirAsync(o, statut, motif, ct);
    }

    private Task PrevenirAsync(OrdreRecu o, string statut, string? motif, CancellationToken ct)
    {
        var (objet, phrase) = statut switch
        {
            StatutsOrdre.AConfirmer => ("Synchronisation : ordre du serveur central à confirmer", "attend votre confirmation"),
            StatutsOrdre.Refuse => ("Synchronisation : ordre du serveur central refusé", "a été refusé"),
            StatutsOrdre.Expire => ("Synchronisation : ordre du serveur central expiré", "a expiré sans être appliqué"),
            _ => ("Synchronisation : employé bloqué par le serveur central", "a été appliqué")
        };
        var detail = $"L’ordre « {TypesOrdre.Libelle(o.Type)} » envoyé par {o.Auteur} {phrase}{(motif is null ? "" : $" : {motif}")}."
            + "\n\nÀ voir : Administration › Synchronisation › Ordres du central.";
        return AlerteNonEnvoyeeSignalee.SansBlocageAsync(() => alertes.SynchronisationAsync(objet, detail,
            $"{objet} : {TypesOrdre.Libelle(o.Type)}.", ct), $"ordre du serveur central ({statut})", trace);
    }
}

/// <summary>Confirmation par le titulaire d'un ordre « à confirmer » ; mot de passe provisoire pour une création de compte.</summary>
public sealed class ConfirmerOrdreCentral(IOrdresCentrauxDepot depot, ExecuterOrdreCentral executer, IPermissionService droits, IHorloge horloge)
{
    public async Task<string> ExecuterAsync(Guid id, string? motDePasse, long titulaireId, CancellationToken ct = default)
    {
        await droits.ExigerTitulaireAsync(titulaireId, "confirmer un ordre du serveur central", ct);
        var o = await depot.LireAsync(id, ct) ?? throw new RegleMetierException("Ordre introuvable.");
        if (o.Statut != StatutsOrdre.AConfirmer) throw new RegleMetierException("Cet ordre n’attend plus de confirmation.");
        if (horloge.MaintenantUtc >= o.ExpireLeUtc)
        {
            await depot.NoterAsync(id, StatutsOrdre.Expire, "non traité avant sa date limite (horloge de l’officine)", null, ct);
            throw new RegleMetierException("Cet ordre a expiré : il n’est pas appliqué.");
        }
        try
        {
            await executer.ExecuterAsync(o, titulaireId, motDePasse, ct);
        }
        catch (OrdreInapplicableException e)
        {
            // Refus définitif (valeur changée ici, cible interdite) ; une erreur de saisie (mot de passe trop court…) laisse l'ordre à confirmer
            await depot.NoterAsync(id, StatutsOrdre.Refuse, e.Message, titulaireId, ct);
            throw new RegleMetierException($"Ordre refusé : {e.Message}");
        }
        await depot.NoterAsync(id, StatutsOrdre.Applique, null, titulaireId, ct);
        return StatutsOrdre.Applique;
    }
}

/// <summary>Refus par le titulaire d'un ordre pas encore appliqué, avec son motif (envoyé au serveur central, filtré comme les textes libres).</summary>
public sealed class RefuserOrdreCentral(IOrdresCentrauxDepot depot, IPermissionService droits)
{
    public async Task ExecuterAsync(Guid id, string? motif, long titulaireId, CancellationToken ct = default)
    {
        await droits.ExigerTitulaireAsync(titulaireId, "refuser un ordre du serveur central", ct);
        var m = (motif ?? "").Trim();
        if (m.Length is < 3 or > 300) throw new RegleMetierException("Motif du refus : 3 à 300 caractères.", "motif");
        var o = await depot.LireAsync(id, ct) ?? throw new RegleMetierException("Ordre introuvable.");
        if (o.Statut is not (StatutsOrdre.AConfirmer or StatutsOrdre.AAppliquer)) throw new RegleMetierException("Cet ordre est déjà traité.");
        await depot.NoterAsync(id, StatutsOrdre.Refuse, "refusé par le titulaire : " + m, titulaireId, ct);
    }
}

/// <summary>Lot S5.3 : téléchargement d'un fichier de référentiel du serveur central (adresse et certificat de l'officine).</summary>
public interface ITelechargementReferentiel
{
    /// <summary>Contenu du fichier n° <paramref name="fichier"/> ; plus de <paramref name="octets"/> octets reçus ou erreur : ServeurCentralException.</summary>
    Task<byte[]> TelechargerAsync(long fichier, long octets, CancellationToken ct = default);
}

/// <summary>Résumé lisible d'un ordre (écran) : employé et rôle nommés d'après la base de l'officine.</summary>
public static class ResumeOrdre
{
    private static string Taille(JsonElement d) =>
        d.TryGetProperty("octets", out var o) && o.TryGetInt64(out var n)
            ? (n / 1048576m).ToString("N1", System.Globalization.CultureInfo.GetCultureInfo("fr-FR")) + " Mo" : "taille inconnue";

    public static string Texte(OrdreRecu o, EtatDroits etat)
    {
        try
        {
            using var dd = JsonDocument.Parse(o.DonneesJson);
            using var aa = JsonDocument.Parse(o.AttenduJson);
            var d = dd.RootElement;
            var a = aa.RootElement;
            string? T(JsonElement e, string n) => e.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
            string NomRole(string? code) => etat.Roles.FirstOrDefault(r => r.Code == code)?.Nom ?? code ?? "?";
            string Employe() => long.TryParse(o.Cible, out var id) && etat.Utilisateurs.FirstOrDefault(u => u.Id == id) is { } u
                ? $"{u.NomComplet} ({u.Identifiant})" : $"employé n° {o.Cible}";
            return o.Type switch
            {
                TypesOrdre.Essai => "Vérification de la liaison : rien ne change.",
                TypesOrdre.EmployeCreer => $"Nouveau compte « {T(d, "nom")} », identifiant {T(d, "identifiant")}, rôle {NomRole(T(d, "role"))}.",
                TypesOrdre.EmployeModifier => $"{Employe()} : "
                    + string.Join(", ", new[]
                    {
                        T(d, "role") is { } r ? $"rôle {NomRole(T(a, "role"))} → {NomRole(r)}" : null,
                        T(d, "nom") is { } n ? $"nom → « {n} »" : null
                    }.Where(x => x is not null)) + ".",
                TypesOrdre.EmployeBloquer => $"{Employe()} : bloqué (plus aucune connexion).",
                TypesOrdre.EmployeDebloquer => $"{Employe()} : débloqué (connexion de nouveau possible).",
                TypesOrdre.DroitsRole => ResumeDroits(o, a, d, NomRole(o.Cible)),
                TypesOrdre.Nomenclature => $"Nouvelle version « {T(d, "libelle")} » ({Taille(d)}) : remplace la nomenclature chargée ; "
                                           + "les produits concernés passeront « à vérifier ».",
                TypesOrdre.PrixProduit => $"{T(d, "nom") ?? $"Produit n° {o.Cible}"} : "
                    + string.Join(" ; ", new[] { Changement(a, d, "prix_public", "prix"), Changement(a, d, "tarif_reference", "tarif de référence") }
                        .Where(x => x is not null)) + ".",
                _ => $"Type inconnu : {o.Type}."
            };
        }
        catch (JsonException)
        {
            return "Ordre illisible.";
        }
    }

    private static readonly System.Globalization.CultureInfo Fr = System.Globalization.CultureInfo.GetCultureInfo("fr-FR");

    private static string? Changement(JsonElement a, JsonElement d, string nom, string libelle)
    {
        if (!d.TryGetProperty(nom, out var n) || n.ValueKind != JsonValueKind.Number) return null;
        var avant = a.TryGetProperty(nom, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDecimal().ToString("N2", Fr) + " DA" : "aucun";
        return $"{libelle} {avant} → {n.GetDecimal().ToString("N2", Fr)} DA";
    }

    private static string ResumeDroits(OrdreRecu o, JsonElement a, JsonElement d, string role)
    {
        var avant = a.TryGetProperty("permissions", out var pa) ? pa.EnumerateArray().Select(x => x.GetString() ?? "").ToHashSet() : [];
        var apres = d.TryGetProperty("permissions", out var pd) ? pd.EnumerateArray().Select(x => x.GetString() ?? "").ToHashSet() : [];
        var ajout = apres.Except(avant).Order(StringComparer.Ordinal).ToList();
        var retrait = avant.Except(apres).Order(StringComparer.Ordinal).ToList();
        return $"Rôle {role} : " + (ajout.Count + retrait.Count == 0 ? "aucun changement" :
            string.Join(" ; ", new[] { ajout.Count > 0 ? "ajout de " + string.Join(", ", ajout) : null,
                                       retrait.Count > 0 ? "retrait de " + string.Join(", ", retrait) : null }.Where(x => x is not null))) + ".";
    }
}
