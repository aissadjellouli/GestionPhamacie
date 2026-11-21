using System.Text.Json;
using Pharma.Application.Administration;
using Pharma.Application.Commun;
using Pharma.Application.Integrite;
using Pharma.Application.Notifications;
using Pharma.Application.Sauvegardes;
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
    /// <summary>Lot S5.4 : commande préparée au central, créée en brouillon (envoi au grossiste depuis l'officine seulement).</summary>
    public const string Commande = "commande";
    /// <summary>Référentiels, lot 1 : liste complète des DCI d'une classe suivie (antibiotiques), désignées par leur nom.</summary>
    public const string ClasseSuivie = "classe_suivie";
    /// <summary>Référentiels, lot 2 : organismes payeurs et centres payeurs (ajouts et corrections seulement).</summary>
    public const string Organismes = "organismes";
    /// <summary>Référentiels, lot 3 : prestations par code (ajouts et corrections).</summary>
    public const string Prestations = "prestations";
    /// <summary>Référentiels, lot 4 : indications à ajouter (par libellé).</summary>
    public const string Indications = "indications";
    /// <summary>Référentiels, lot 5 : DCI à ajouter (par nom).</summary>
    public const string Dci = "dci";
    /// <summary>Lot S7.3 : sauvegarde demandée par le titulaire à distance ; confirmée sur le poste (moment choisi par le titulaire local).</summary>
    public const string Sauvegarde = "sauvegarde";
    /// <summary>Lot S7.3 : vérification d'intégrité des journaux chaînés demandée à distance (lecture seule, appliquée tout de suite).</summary>
    public const string VerificationIntegrite = "verification_integrite";
    public static readonly IReadOnlySet<string> Connus = new HashSet<string>(StringComparer.Ordinal)
    {
        Essai, EmployeCreer, EmployeModifier, EmployeBloquer, EmployeDebloquer, DroitsRole, PrixProduit, Nomenclature, Commande, ClasseSuivie, Organismes,
        Prestations, Indications, Dci, Sauvegarde, VerificationIntegrite
    };

    /// <summary>Confirmation du titulaire exigée avant application (décision S5-D3) ; le blocage et l'essai s'appliquent tout de suite.</summary>
    public static bool ConfirmationRequise(string type) =>
        type is EmployeCreer or EmployeModifier or EmployeDebloquer or DroitsRole or Nomenclature or ClasseSuivie or Organismes
            or Prestations or Indications or Dci or Sauvegarde;

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
        Commande => "Commande en brouillon",
        ClasseSuivie => "Liste d’une classe suivie",
        Organismes => "Organismes et centres payeurs",
        Prestations => "Prestations",
        Indications => "Indications thérapeutiques",
        Dci => "Dénominations communes (DCI)",
        Sauvegarde => "Sauvegarde demandée à distance",
        VerificationIntegrite => "Vérification d’intégrité demandée à distance",
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
    /// <summary>
    /// Lot S5.4 : bon de commande EN BROUILLON avec toutes ses lignes, en une transaction, sans auteur local, rattaché à l'ordre
    /// (commande_ordre_central, script 146 : un même ordre ne crée jamais deux bons) ; journal « commande_preparee_central ».
    /// Renvoie le n° du bon (celui déjà créé si l'ordre a déjà été traité).
    /// </summary>
    Task<string> CreerCommandeAsync(long fournisseurId, IReadOnlyList<Pharma.Domain.Achats.LigneCommandeSaisie> lignes, string auteurCentral,
        Guid ordre, CancellationToken ct = default);
    /// <summary>
    /// Référentiels, lot 1 : compare la liste reçue (noms de DCI, sans tenir compte de la casse) à la classe suivie de l'officine ; avec
    /// <paramref name="appliquer"/>, ajoute et retire en une transaction (sous verrou de la classe) et écrit le journal
    /// « classe_suivie_mise_a_jour » au nom de <paramref name="par"/>. Les noms absents de la table des DCI sont ignorés et listés.
    /// </summary>
    Task<ChangementsClasseSuivie> ClasseSuivieAsync(string classe, IReadOnlyList<string> noms, bool appliquer, long? par, string auteurCentral,
        Guid ordre, CancellationToken ct = default);
    /// <summary>
    /// Référentiels, lot 5 : DCI de la liste absentes de l'officine (comparées sans tenir compte de la casse) ; avec
    /// <paramref name="appliquer"/>, ajoutées en une transaction avec le journal « dci_ajoutees » au nom de <paramref name="par"/>.
    /// Jamais de renommage ni de suppression.
    /// </summary>
    Task<ChangementsListe> AjouterDciAsync(IReadOnlyList<string> noms, bool appliquer, long? par, string auteurCentral, Guid ordre,
        CancellationToken ct = default);
}

/// <summary>Changements d'une classe suivie : DCI ajoutées, retirées, noms inconnus de l'officine, nombre de noms reconnus.</summary>
public sealed record ChangementsClasseSuivie(bool ClasseConnue, IReadOnlyList<string> Ajouts, IReadOnlyList<string> Retraits,
    IReadOnlyList<string> Inconnues, int Reconnues)
{
    private static string Liste(IReadOnlyList<string> n) => n.Count == 0 ? "" : $" ({string.Join(", ", n.Take(5))}{(n.Count > 5 ? ", …" : "")})";

    public string Texte() => !ClasseConnue ? "Classe suivie inconnue dans l’officine."
        : $"{Ajouts.Count} ajout(s){Liste(Ajouts)}, {Retraits.Count} retrait(s){Liste(Retraits)}"
          + (Inconnues.Count > 0 ? $", {Inconnues.Count} DCI absente(s) de l’officine, ignorée(s){Liste(Inconnues)}" : "") + ".";
}

/// <summary>L'ordre ne peut pas être appliqué tel quel (valeur changée dans l'officine, cible interdite ou introuvable) : refus définitif.</summary>
public sealed class OrdreInapplicableException(string message) : Exception(message);

/// <summary>Application d'un ordre par les cas d'usage existants ; <paramref name="titulaireId"/> : titulaire qui confirme (null : automatique).</summary>
public sealed class ExecuterOrdreCentral(IUtilisateursAdmin admin, CreerUtilisateur creer, ModifierUtilisateur modifier, EnregistrerDroits droitsRole,
    Catalogue.IProduitsModification produits, IOrdresCentrauxDepot depot, Catalogue.ImporterNomenclature nomenclature,
    ITelechargementReferentiel referentiels, Achats.IAchats achats, IOrganismesCentraux organismes, IListesCentrales listes,
    Sauvegardes.LancerSauvegarde? sauvegarde = null, Integrite.VerifierIntegrite? integrite = null)
{
    /// <summary>Lignes au plus dans un ordre de commande (décision S5.4-D5).</summary>
    public const int LignesCommandeMax = 200;

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

    /// <returns>Résultat à remonter avec l'état « appliqué » (S7.3 : sauvegarde, vérification d'intégrité) ; null sinon.</returns>
    public async Task<string?> ExecuterAsync(OrdreRecu o, long? titulaireId, string? motDePasse, CancellationToken ct = default)
    {
        using var attendu = JsonDocument.Parse(o.AttenduJson);
        using var donnees = JsonDocument.Parse(o.DonneesJson);
        var a = attendu.RootElement;
        var d = donnees.RootElement;
        if (o.Type == TypesOrdre.Essai) return null;
        if (TypesOrdre.ConfirmationRequise(o.Type) && titulaireId is null)
            throw new InvalidOperationException("Ordre à confirmer par le titulaire : application automatique interdite.");
        // Lot S7.3 : ordres du titulaire à distance (liste fermée, sans donnée) ; rien ne modifie une vente, un stock, un prix ni un compte
        if (o.Type == TypesOrdre.Sauvegarde) return await SauvegarderAsync(titulaireId!.Value, ct);
        if (o.Type == TypesOrdre.VerificationIntegrite) return await VerifierIntegriteAsync(ct);
        var etat = await admin.LireAsync(ct);
        switch (o.Type)
        {
            case TypesOrdre.EmployeCreer:
            {
                var role = Role(etat, Texte(d, "role"));
                if (string.IsNullOrEmpty(motDePasse)) throw new RegleMetierException("Mot de passe provisoire obligatoire pour créer le compte.", "mot_de_passe");
                await creer.ExecuterAsync(Texte(d, "nom"), Texte(d, "identifiant"), motDePasse, role.Id, titulaireId!.Value, ct: ct);
                return null;
            }
            case TypesOrdre.EmployeModifier or TypesOrdre.EmployeBloquer or TypesOrdre.EmployeDebloquer:
            {
                var u = Employe(etat, o.Cible);
                VerifierAttenduEmploye(a, u, etat);
                if (o.Type == TypesOrdre.EmployeBloquer)
                {
                    // Protecteur, sans confirmation : seul l'état actif change (les sessions de l'employé tombent à l'appel suivant)
                    await admin.ModifierAsync(u.Id, u.NomComplet, u.RoleId, false, null, u.Telephone, u.Email, ct);
                    return null;
                }
                var roleId = u.RoleId;
                if (o.Type == TypesOrdre.EmployeModifier && d.TryGetProperty("role", out var r) && r.ValueKind == JsonValueKind.String)
                    roleId = Role(etat, r.GetString()).Id;
                var nom = o.Type == TypesOrdre.EmployeModifier && d.TryGetProperty("nom", out var n) && n.ValueKind == JsonValueKind.String
                    ? n.GetString()! : u.NomComplet;
                var actif = o.Type == TypesOrdre.EmployeDebloquer || u.Actif;
                await modifier.ExecuterAsync(u.Id, nom, roleId, actif, null, titulaireId!.Value, u.Telephone, u.Email, ct);
                return null;
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
                return null;
            }
            case TypesOrdre.Nomenclature:
                await ImporterNomenclatureAsync(d, titulaireId!.Value, ct);
                return null;
            case TypesOrdre.Commande:
                await CreerCommandeAsync(o, d, ct);
                return null;
            case TypesOrdre.ClasseSuivie:
            {
                var noms = NomsDci(d);
                var prevu = await depot.ClasseSuivieAsync(o.Cible ?? "", noms, false, null, o.Auteur, o.Id, ct);
                if (!prevu.ClasseConnue) throw new OrdreInapplicableException($"Classe suivie « {o.Cible} » inconnue dans l’officine.");
                // Garde-fou : une liste dont aucun nom n'est reconnu viderait la classe
                if (prevu.Reconnues == 0) throw new OrdreInapplicableException("Aucune DCI de la liste n’existe dans l’officine : rien n’est changé.");
                await depot.ClasseSuivieAsync(o.Cible!, noms, true, titulaireId, o.Auteur, o.Id, ct);
                return null;
            }
            case TypesOrdre.Organismes:
                await organismes.AppliquerAsync(d, titulaireId!.Value, ct);
                return null;
            case TypesOrdre.Prestations or TypesOrdre.Indications:
                await listes.AppliquerAsync(o.Type, d, titulaireId!.Value, ct);
                return null;
            case TypesOrdre.Dci:
                await depot.AjouterDciAsync(Textes(d, "dci"), true, titulaireId, o.Auteur, o.Id, ct);
                return null;
            case TypesOrdre.DroitsRole:
            {
                var role = Role(etat, o.Cible);
                var actuels = role.Permissions.Order(StringComparer.Ordinal).ToList();
                if (!a.TryGetProperty("permissions", out var pa) || pa.ValueKind != JsonValueKind.Array
                    || !pa.EnumerateArray().Select(x => x.GetString() ?? "").Order(StringComparer.Ordinal).SequenceEqual(actuels))
                    throw new OrdreInapplicableException($"Droits du rôle « {role.Nom} » {ModifieDansOfficine}.");
                var voulus = d.GetProperty("permissions").EnumerateArray().Select(x => x.GetString() ?? "").ToList();
                await droitsRole.ExecuterAsync([new DroitsRole(role.Id, voulus)], titulaireId!.Value, ct);
                return null;
            }
            default:
                throw new OrdreInapplicableException($"Type d’ordre non pris en charge : {o.Type}.");
        }
    }

    /// <summary>Lot S7.3 : sauvegarde demandée à distance, lancée au nom du titulaire qui confirme (mêmes règles que « Sauvegarder
    /// maintenant » : dossier réglé, disque présent, journal des sauvegardes). Dossier non réglé, disque absent ou échec : l'ordre RESTE
    /// à confirmer (le titulaire branche le disque et recommence, ou refuse) ; il expire sinon à son échéance.</summary>
    private async Task<string?> SauvegarderAsync(long titulaireId, CancellationToken ct)
    {
        if (sauvegarde is null) throw new OrdreInapplicableException("Sauvegarde indisponible sur ce serveur.");
        var r = await sauvegarde.ExecuterAsync(false, titulaireId, DateTime.Now, ct);
        if (r.Statut != "reussie") throw new RegleMetierException($"Sauvegarde en échec : {r.Message} L’ordre reste à confirmer.");
        return "sauvegarde réussie" + (r.TailleOctets is { } t ? $" ({t / 1048576m:N1} Mo)" : "");
    }

    /// <summary>Lot S7.3 : vérification d'intégrité demandée à distance (lecture seule, comme la vérification de la nuit) ; résultat en
    /// nombres seulement (aucune donnée de ligne) ; une rupture est signalée au titulaire par la vérification elle-même.</summary>
    private async Task<string?> VerifierIntegriteAsync(CancellationToken ct)
    {
        if (integrite is null) throw new OrdreInapplicableException("Vérification d’intégrité indisponible sur ce serveur.");
        var etats = await integrite.ExecuterAsync(null, ct);
        var ruptures = etats.Count(e => e.Statut == VerifierIntegrite.Rupture);
        var erreurs = etats.Count(e => e.Statut == VerifierIntegrite.Erreur);
        return $"{etats.Count} chaîne(s) vérifiée(s) : {etats.Count - ruptures - erreurs} intacte(s), {ruptures} rupture(s), {erreurs} en erreur";
    }

    /// <summary>Aperçu des changements d'un ordre à confirmer (référentiels), montré au titulaire avant sa décision ; null : sans objet.</summary>
    public async Task<string?> ApercuAsync(OrdreRecu o, CancellationToken ct = default)
    {
        using var donnees = JsonDocument.Parse(o.DonneesJson);
        var d = donnees.RootElement;
        return o.Type switch
        {
            TypesOrdre.ClasseSuivie => (await depot.ClasseSuivieAsync(o.Cible ?? "", NomsDci(d), false, null, o.Auteur, o.Id, ct)).Texte(),
            TypesOrdre.Organismes => (await organismes.CalculerAsync(d, ct)).Texte(),
            TypesOrdre.Prestations or TypesOrdre.Indications => (await listes.CalculerAsync(o.Type, d, ct)).Texte(),
            TypesOrdre.Dci => (await depot.AjouterDciAsync(Textes(d, "dci"), false, null, o.Auteur, o.Id, ct)).Texte(),
            _ => null
        };
    }

    private static List<string> NomsDci(JsonElement d) => Textes(d, "dci");

    private static List<string> Textes(JsonElement d, string nom) =>
        d.TryGetProperty(nom, out var l) && l.ValueKind == JsonValueKind.Array
            ? l.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!.Trim()).Where(x => x.Length > 0).ToList()
            : throw new OrdreInapplicableException($"Liste « {nom} » absente de l’ordre.");

    /// <summary>
    /// Lot S5.4 : mêmes contrôles que l'ajout d'un produit à l'écran (AjouterACommande) pour chaque ligne : grossiste actif, produit connu
    /// et non bloqué à l'achat, prix donné ou dernier prix d'achat, quantité. Une seule ligne refusée : tout l'ordre est refusé, rien n'est
    /// créé (décision S5.4-D4), avec le motif de chaque ligne.
    /// </summary>
    private async Task CreerCommandeAsync(OrdreRecu o, JsonElement d, CancellationToken ct)
    {
        var grossiste = long.TryParse(o.Cible, out var gid) ? await achats.FournisseurAsync(gid, ct) : null;
        if (grossiste is null) throw new OrdreInapplicableException($"Grossiste n° {o.Cible} introuvable dans l’officine.");
        if (!grossiste.Actif) throw new OrdreInapplicableException($"{grossiste.RaisonSociale} est désactivé dans l’officine.");
        List<(long Produit, int Quantite, decimal? Prix)> demandes;
        try
        {
            if (!d.TryGetProperty("lignes", out var l) || l.ValueKind != JsonValueKind.Array) throw new FormatException();
            demandes = l.EnumerateArray().Select(e => (e.GetProperty("produit").GetInt64(), e.GetProperty("quantite").GetInt32(),
                e.TryGetProperty("prix_achat_ht", out var p) && p.ValueKind == JsonValueKind.Number ? p.GetDecimal() : (decimal?)null)).ToList();
        }
        catch (Exception e) when (e is FormatException or InvalidOperationException or KeyNotFoundException)
        {
            throw new OrdreInapplicableException("Commande mal formée (lignes : n° de produit, quantité, prix facultatif).");
        }
        if (demandes.Count is 0 or > LignesCommandeMax)
            throw new OrdreInapplicableException($"Une commande compte de 1 à {LignesCommandeMax} lignes.");
        if (demandes.Select(x => x.Produit).Distinct().Count() != demandes.Count)
            throw new OrdreInapplicableException("Un même produit apparaît sur deux lignes.");
        var infos = (await achats.InfosAchatAsync(demandes.Select(x => x.Produit).ToList(), ct)).ToDictionary(i => i.ProduitId);
        var lignes = new List<Pharma.Domain.Achats.LigneCommandeSaisie>();
        var erreurs = new List<string>();
        foreach (var x in demandes)
        {
            var nom = await achats.NomProduitAsync(x.Produit, ct);
            if (nom is null) { erreurs.Add($"produit n° {x.Produit} introuvable"); continue; }
            if (infos.GetValueOrDefault(x.Produit) is { BloqueAchat: true } b)
            {
                erreurs.Add($"{nom} bloqué à l’achat{(b.MotifBlocage is { } m ? $" ({m})" : "")}");
                continue;
            }
            var prix = x.Prix ?? (await achats.DernierAchatAsync(x.Produit, grossiste.Id, ct))?.PrixAchatHt;
            if (prix is null) { erreurs.Add($"{nom} : aucun achat précédent, prix d’achat à indiquer"); continue; }
            lignes.Add(new Pharma.Domain.Achats.LigneCommandeSaisie(x.Produit, nom, x.Quantite, prix.Value));
        }
        if (erreurs.Count > 0) throw new OrdreInapplicableException($"Rien n’a été créé : {string.Join(" ; ", erreurs)}.");
        Pharma.Domain.Achats.RegleAchats.VerifierCommande(lignes);
        await depot.CreerCommandeAsync(grossiste.Id, lignes, o.Auteur, o.Id, ct);
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
                // Référentiels : l'aperçu des changements devient le motif affiché au titulaire
                string? apercu = null;
                try { apercu = await executer.ApercuAsync(o, ct); }
                catch (Exception e) when (e is OrdreInapplicableException or JsonException)
                {
                    await NoterEtPrevenirAsync(o, StatutsOrdre.Refuse, e is JsonException ? "ordre mal formé" : e.Message, ct);
                    n++;
                    continue;
                }
                await NoterEtPrevenirAsync(o, StatutsOrdre.AConfirmer,
                    o.Type == TypesOrdre.PrixProduit ? "écart de prix de plus de 20 % : confirmation du titulaire" : apercu, ct);
                n++;
                continue;
            }
            try
            {
                var resultat = await executer.ExecuterAsync(o, null, null, ct);
                await depot.NoterAsync(o.Id, StatutsOrdre.Applique, resultat, null, ct);
                if (o.Type is TypesOrdre.EmployeBloquer or TypesOrdre.Commande) await PrevenirAsync(o, StatutsOrdre.Applique, null, ct);
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
        // Lot S5.4 : bon créé en brouillon, à vérifier puis envoyer au grossiste depuis l'officine (décision S5-D4)
        var commande = o.Type == TypesOrdre.Commande && statut == StatutsOrdre.Applique;
        var (objet, phrase) = commande
            ? ("Synchronisation : commande préparée par le serveur central", "a créé un bon de commande en brouillon, à vérifier puis à envoyer au grossiste")
            : statut switch
        {
            StatutsOrdre.AConfirmer => ("Synchronisation : ordre du serveur central à confirmer", "attend votre confirmation"),
            StatutsOrdre.Refuse => ("Synchronisation : ordre du serveur central refusé", "a été refusé"),
            StatutsOrdre.Expire => ("Synchronisation : ordre du serveur central expiré", "a expiré sans être appliqué"),
            _ => ("Synchronisation : employé bloqué par le serveur central", "a été appliqué")
        };
        var detail = $"L’ordre « {TypesOrdre.Libelle(o.Type)} » envoyé par {o.Auteur} {phrase}{(motif is null ? "" : $" : {motif}")}."
            + (commande ? "\n\nÀ voir : Achats › Commandes (bon en brouillon)." : "\n\nÀ voir : Administration › Synchronisation › Ordres du central.");
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
        string? resultat;
        try
        {
            resultat = await executer.ExecuterAsync(o, titulaireId, motDePasse, ct);
        }
        catch (OrdreInapplicableException e)
        {
            // Refus définitif (valeur changée ici, cible interdite) ; une erreur de saisie (mot de passe trop court…) laisse l'ordre à confirmer
            await depot.NoterAsync(id, StatutsOrdre.Refuse, e.Message, titulaireId, ct);
            throw new RegleMetierException($"Ordre refusé : {e.Message}");
        }
        await depot.NoterAsync(id, StatutsOrdre.Applique, resultat, titulaireId, ct);
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
                TypesOrdre.Sauvegarde => "Sauvegarde de la base sur le disque externe, lancée à votre confirmation (choisissez un moment calme) ; rien d’autre ne change.",
                TypesOrdre.VerificationIntegrite => "Vérification des journaux chaînés (lecture seule) ; une rupture vous est signalée.",
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
                TypesOrdre.Commande => ResumeCommande(d, T(d, "grossiste") ?? $"grossiste n° {o.Cible}"),
                TypesOrdre.ClasseSuivie => $"Liste « {o.Cible} » envoyée ({(d.TryGetProperty("dci", out var dl) && dl.ValueKind == JsonValueKind.Array ? dl.GetArrayLength() : 0)} DCI) : "
                                           + "elle remplace la liste suivie de l’officine. Ajouts et retraits sous cette ligne.",
                TypesOrdre.Organismes => ResumeOrganismes(d),
                TypesOrdre.Prestations => $"{Nombre(d, "prestations")} prestation(s) : ajouts et corrections seulement, rien n’est supprimé ni désactivé.",
                TypesOrdre.Indications => $"{Nombre(d, "indications")} indication(s) à ajouter si elles manquent ; rien n’est renommé ni supprimé.",
                TypesOrdre.Dci => $"{Nombre(d, "dci")} DCI à ajouter si elles manquent ; rien n’est renommé ni supprimé.",
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

    private static int Nombre(JsonElement d, string nom) =>
        d.TryGetProperty(nom, out var l) && l.ValueKind == JsonValueKind.Array ? l.GetArrayLength() : 0;

    private static string ResumeOrganismes(JsonElement d)
    {
        var liste = d.TryGetProperty("organismes", out var l) && l.ValueKind == JsonValueKind.Array ? l.EnumerateArray().ToList() : [];
        var centres = liste.Sum(e => e.TryGetProperty("centres", out var c) && c.ValueKind == JsonValueKind.Array ? c.GetArrayLength() : 0);
        var sigles = liste.Select(e => e.TryGetProperty("code", out var c) ? c.GetString() : "?");
        return $"{liste.Count} organisme(s) ({string.Join(", ", sigles)}), {centres} centre(s) payeur(s) : ajouts et corrections seulement, "
               + "rien n’est supprimé ni désactivé.";
    }

    private static string ResumeCommande(JsonElement d, string grossiste)
    {
        var lignes = d.TryGetProperty("lignes", out var l) && l.ValueKind == JsonValueKind.Array ? l.EnumerateArray().ToList() : [];
        var premieres = lignes.Take(3).Select(e =>
            $"{(e.TryGetProperty("nom", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString() : $"produit n° {e.GetProperty("produit")}")}"
            + $" × {(e.TryGetProperty("quantite", out var q) ? q.ToString() : "?")}");
        return $"Bon en brouillon chez {grossiste} : {lignes.Count} ligne(s) ({string.Join(", ", premieres)}{(lignes.Count > 3 ? ", …" : "")}). "
               + "Envoyé au grossiste depuis l’officine seulement.";
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
