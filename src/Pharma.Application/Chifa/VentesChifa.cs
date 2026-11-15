using Pharma.Application.Commun;
using Pharma.Application.Referentiels;
using Pharma.Application.Securite;
using Pharma.Domain.Commun;
using Pharma.Domain.Referentiels;
using Pharma.Domain.Securite;
using Pharma.Domain.Ventes;

namespace Pharma.Application.Chifa;

// ---- Volet Chifa : compléments (2026-11-15, modèle Wasfa, lot 1, script 100) ----

/// <summary>Centre payeur d'un organisme Chifa (CNAS ou CASNOS).</summary>
public sealed record CentrePayeur(long Id, long OrganismeId, string OrganismeCode, string Code, string Nom, bool Actif);

/// <summary>Vente Chifa telle que lue pour ses informations et son exclusion (Exclusion : motif lisible, null si non exclue).</summary>
public sealed record VenteChifa(long Id, string Numero, DateTime Date, string Canal, string Statut, bool DansBordereau, bool Rattachee,
    long? PatientId, string? Patient, string? NumAssure, long? OrganismeId, string? Organisme, string Vendeur, long VendeurId,
    decimal Total, decimal PartOrganisme, string? FactureChifa, InfosChifa Infos, string? CentrePayeur, string? SaisiPar, string? Majoration,
    DateTime? ExclueLe = null, string? ExcluePar = null, string? ExclusionCode = null, string? Exclusion = null, long? CentreHabituelId = null);

/// <summary>Filtre de la liste des ventes Chifa : recherche, centre payeur, exclues (« non » par défaut, « oui », « tous »).</summary>
public sealed record FiltreVentesChifa(string? Recherche, long? CentrePayeurId, string Exclues = FiltreVentesChifa.Non)
{
    public const string Non = "non";
    public const string Oui = "oui";
    public const string Tous = "tous";
    public static string Lire(string? exclues) => exclues?.Trim().ToLowerInvariant() switch
    {
        "oui" or "true" => Oui,
        "tous" => Tous,
        _ => Non
    };
}

public interface IVentesChifa
{
    Task<VenteChifa?> LireAsync(long venteId, CancellationToken ct = default);
    Task<Page<VenteChifa>> ListerAsync(FiltreVentesChifa filtre, DemandePage page, CancellationToken ct = default);
    /// <summary>Écrit les informations Chifa ; retenirCentre : le centre devient le centre habituel du patient. Faux si la vente est annulée entre-temps.</summary>
    Task<bool> EnregistrerInfosAsync(long venteId, InfosChifa infos, bool retenirCentre, CancellationToken ct = default);
    /// <summary>Exclut la vente si elle est encore Chifa, validée, non exclue et hors bordereau (faux sinon).</summary>
    Task<bool> ExclureAsync(long venteId, string motifCode, string? texte, long utilisateurId, CancellationToken ct = default);
    /// <summary>Rétablit la vente si elle est exclue (faux sinon).</summary>
    Task<bool> RetablirAsync(long venteId, CancellationToken ct = default);
    /// <summary>Employé actif d'identifiant donné : son nom, sinon null.</summary>
    Task<string?> EmployeActifAsync(long utilisateurId, CancellationToken ct = default);
    /// <summary>Employés actifs (choix « saisi dans Chifa par »).</summary>
    Task<IReadOnlyList<(long Id, string Nom)>> EmployesAsync(CancellationToken ct = default);
}

public interface ICentresPayeurs
{
    Task<Page<CentrePayeur>> ListerAsync(long? organismeId, bool? actif, string? recherche, DemandePage page, CancellationToken ct = default);
    Task<CentrePayeur?> LireAsync(long id, CancellationToken ct = default);
    /// <summary>Code de l'organisme, null s'il n'existe pas.</summary>
    Task<string?> CodeOrganismeAsync(long organismeId, CancellationToken ct = default);
    /// <summary>Crée (id null) ou modifie le centre ; code déjà pris pour l'organisme : RegleMetierException (409).</summary>
    Task<long> EnregistrerAsync(long? id, long organismeId, string code, string nom, bool actif, CancellationToken ct = default);
}


/// <summary>Centres payeurs : liste paginée et filtrée (tout employé qui vend), ajout, modification et désactivation (admin.parametres).</summary>
public sealed class CentresPayeurs(ICentresPayeurs centres, IPermissionService droits, IJournal journal)
{
    public async Task<Page<CentrePayeur>> ListerAsync(long? organismeId, bool? actif, string? recherche, DemandePage page, long utilisateurId,
        CancellationToken ct = default)
    {
        await droits.ExigerAsync(utilisateurId, Permissions.CaisseVendre, "voir les centres payeurs", ct);
        var q = string.IsNullOrWhiteSpace(recherche) ? null : recherche.Trim()[..Math.Min(recherche.Trim().Length, 60)];
        return await centres.ListerAsync(organismeId, actif, q, page, ct);
    }

    /// <summary>Jamais de suppression : un centre se désactive (il reste sur les ventes passées).</summary>
    public async Task<CentrePayeur> EnregistrerAsync(long? id, long organismeId, string? code, string? nom, bool actif, long utilisateurId,
        CancellationToken ct = default)
    {
        await droits.ExigerAsync(utilisateurId, Permissions.AdminParametres, "gérer les centres payeurs", ct);
        var (c, n) = RegleCentrePayeur.Verifier(code, nom);
        var org = await centres.CodeOrganismeAsync(organismeId, ct);
        if (!Caisse.OrganismeVoletChifa.Accepte(org))
            throw new RegleMetierException("Un centre payeur appartient à la CNAS ou à la CASNOS.", "organisme");
        CentrePayeur? avant = null;
        if (id is { } i)
            avant = await centres.LireAsync(i, ct) ?? throw new RegleMetierException("Centre payeur introuvable.", "centre_payeur");
        var nouvelId = await centres.EnregistrerAsync(id, organismeId, c, n, actif, ct);
        await journal.EcrireAsync(utilisateurId, id is null ? "centre_payeur_cree" : "centre_payeur_modifie", "centre_payeur", nouvelId,
            new Dictionary<string, object?>
            {
                ["organisme"] = org!.Trim().ToUpperInvariant(), ["code"] = c, ["nom"] = n, ["actif"] = actif,
                ["avant"] = avant is null ? null : new { organisme = avant.OrganismeCode, code = avant.Code, nom = avant.Nom, actif = avant.Actif }
            }, Permissions.AdminParametres, ct);
        return (await centres.LireAsync(nouvelId, ct))!;
    }
}

/// <summary>
/// Informations Chifa d'une vente (2026-11-15, script 100) : date des soins, centre payeur (de l'organisme de la vente, actif ou déjà
/// celui de la vente), employé qui l'a saisie dans CHIFA-Officine (défaut : le vendeur), majoration (D-3 : s'ajoute à la part organisme).
/// Modifiable au volet (Finances › Chifa) et au rattachement, tant que la vente n'est ni annulée ni dans un bordereau ; droit de vendre ;
/// journalisé (vente_chifa_infos).
/// </summary>
public sealed class EnregistrerInfosChifa(IVentesChifa ventes, ICentresPayeurs centres, IPermissionService droits, IJournal journal, IHorloge horloge,
    IMotifsReglables? motifs = null)
{
    public const string Action = "vente_chifa_infos";

    public async Task ExecuterAsync(long venteId, InfosChifa saisie, bool retenirCentre, long utilisateurId, CancellationToken ct = default)
    {
        await droits.ExigerAsync(utilisateurId, Permissions.CaisseVendre, "modifier les informations Chifa d’une vente", ct);
        var v = await ventes.LireAsync(venteId, ct) ?? throw new RegleMetierException("Vente introuvable.", "vente");
        if (v.Canal != "chifa") throw new RegleMetierException("Cette vente n’est pas facturée par Chifa.", "vente");
        if (v.Statut != "validee") throw new RegleMetierException("Cette vente est annulée : ses informations Chifa ne changent plus.", "vente");
        if (v.DansBordereau) throw new RegleMetierException("Cette vente est déjà dans un bordereau : ses informations Chifa ne changent plus.", "vente");
        var infos = RegleInfosChifa.Verifier(saisie, horloge.Aujourdhui);

        string? centre = null;
        if (infos.CentrePayeurId is { } cid)
        {
            var c = await centres.LireAsync(cid, ct) ?? throw new RegleMetierException("Centre payeur introuvable.", "centre_payeur");
            if (c.OrganismeId != v.OrganismeId)
                throw new RegleMetierException($"Le centre {c.Code} n’appartient pas à l’organisme de la vente ({v.Organisme}).", "centre_payeur");
            if (!c.Actif && cid != v.Infos.CentrePayeurId)
                throw new RegleMetierException($"Le centre {c.Code} est désactivé : choisissez-en un autre.", "centre_payeur");
            centre = $"{c.Code} · {c.Nom}";
        }
        var saisiPar = infos.SaisiPar ?? v.VendeurId;
        var employe = saisiPar == v.Infos.SaisiPar || saisiPar == v.VendeurId && v.Infos.SaisiPar is null
            ? v.SaisiPar ?? v.Vendeur
            : await ventes.EmployeActifAsync(saisiPar, ct) ?? throw new RegleMetierException("Employé introuvable ou désactivé.", "saisi_par");
        infos = infos with { SaisiPar = saisiPar };

        string? majoration = null;
        if (infos.MajorationCode is { } code && code != v.Infos.MajorationCode)
        {
            if (motifs is not null)
                majoration = (await new MotifsActifs(motifs).ChoisirAsync(RegleMotifs.MajorationChifa, code, null, "de la majoration", ct)).Libelle;
        }
        else majoration = infos.MajorationCode is null ? null : v.Majoration ?? infos.MajorationCode;

        if (!await ventes.EnregistrerInfosAsync(venteId, infos, retenirCentre && infos.CentrePayeurId is not null && v.PatientId is not null, ct))
            throw new RegleMetierException("Cette vente vient d’être annulée ou mise dans un bordereau.", "vente");
        await journal.EcrireAsync(utilisateurId, Action, "vente", venteId,
            new Dictionary<string, object?>
            {
                ["ticket"] = v.Numero, ["date_soins"] = infos.DateSoins?.ToString("yyyy-MM-dd"), ["centre_payeur"] = centre,
                ["saisi_par"] = employe, ["majoration"] = majoration, ["majoration_montant"] = infos.MajorationMontant,
                ["centre_habituel"] = retenirCentre && centre is not null && v.PatientId is not null,
                ["avant"] = new
                {
                    date_soins = v.Infos.DateSoins?.ToString("yyyy-MM-dd"), centre_payeur = v.CentrePayeur, saisi_par = v.SaisiPar,
                    majoration = v.Majoration, majoration_montant = v.Infos.MajorationMontant
                }
            }, Permissions.CaisseVendre, ct);
    }
}

/// <summary>
/// « Exclure du traitement » une vente Chifa (2026-11-15, script 100) : droit chifa.exclure (titulaire et assistant par défaut, D-8 ;
/// jamais par code comptoir) ; motif de la liste réglable « chifa_exclusion », « Autre » avec un texte (D-7) ; sans nouveau statut de vente.
/// La vente sort de « Ventes en attente de volet » et des candidates au rattachement. « Rétablir » la remet ; les deux sont journalisés.
/// </summary>
public sealed class ExclureVenteChifa(IVentesChifa ventes, IPermissionService droits, IJournal journal, IMotifsReglables? motifs = null)
{
    public const string ActionExclure = "vente_chifa_exclue";
    public const string ActionRetablir = "vente_chifa_retablie";

    public async Task ExclureAsync(long venteId, string? motifCode, string? texte, long utilisateurId, CancellationToken ct = default)
    {
        await droits.ExigerAsync(utilisateurId, Permissions.ChifaExclure, "exclure une vente Chifa du traitement", ct);
        var v = await ventes.LireAsync(venteId, ct) ?? throw new RegleMetierException("Vente introuvable.", "vente");
        RegleExclusionChifa.VerifierExclusion(v.Canal, v.Statut, v.ExclueLe is not null, v.DansBordereau);
        string code, libelle;
        string? precision;
        if (motifs is not null)
            (code, libelle, precision) = await new MotifsActifs(motifs).ChoisirAsync(RegleMotifs.ChifaExclusion, motifCode, texte, "de l’exclusion", ct);
        else
        {
            // Liste non branchée (tests) : code exigé, « Autre » avec texte
            if (string.IsNullOrWhiteSpace(motifCode)) throw new RegleMetierException("Choisissez le motif de l’exclusion.", "motif");
            code = motifCode.Trim();
            precision = string.IsNullOrWhiteSpace(texte) ? null : texte.Trim();
            if (code == RegleMotifs.Autre && precision is null) throw new RegleMetierException("Motif « Autre » : précisez le motif.", "motif_texte");
            if (code != RegleMotifs.Autre) precision = null;
            libelle = precision is null ? code : $"Autre : {precision}";
        }
        if (!await ventes.ExclureAsync(venteId, code, precision, utilisateurId, ct))
            throw new RegleMetierException("Cette vente vient d’être exclue, annulée ou mise dans un bordereau.", "vente");
        await journal.EcrireAsync(utilisateurId, ActionExclure, "vente", venteId,
            new Dictionary<string, object?>
            {
                ["ticket"] = v.Numero, ["motif"] = libelle, ["motif_code"] = code, ["patient"] = v.Patient, ["organisme"] = v.Organisme,
                ["part_organisme"] = v.PartOrganisme, ["facture_chifa"] = v.FactureChifa
            }, Permissions.ChifaExclure, ct);
    }

    public async Task RetablirAsync(long venteId, long utilisateurId, CancellationToken ct = default)
    {
        await droits.ExigerAsync(utilisateurId, Permissions.ChifaExclure, "rétablir une vente Chifa exclue", ct);
        var v = await ventes.LireAsync(venteId, ct) ?? throw new RegleMetierException("Vente introuvable.", "vente");
        RegleExclusionChifa.VerifierRetablissement(v.ExclueLe is not null);
        if (!await ventes.RetablirAsync(venteId, ct))
            throw new RegleMetierException("Cette vente vient d’être rétablie.", "vente");
        await journal.EcrireAsync(utilisateurId, ActionRetablir, "vente", venteId,
            new Dictionary<string, object?>
            {
                ["ticket"] = v.Numero, ["motif"] = v.Exclusion, ["exclue_le"] = v.ExclueLe, ["exclue_par"] = v.ExcluePar
            }, Permissions.ChifaExclure, ct);
    }
}

/// <summary>Liste des ventes Chifa (Finances › Chifa) : filtrée (recherche, centre, exclues) et paginée ; droit de vendre.</summary>
public sealed class ConsulterVentesChifa(IVentesChifa ventes, IPermissionService droits)
{
    public async Task<Page<VenteChifa>> ListerAsync(FiltreVentesChifa filtre, DemandePage page, long utilisateurId, CancellationToken ct = default)
    {
        await droits.ExigerAsync(utilisateurId, Permissions.CaisseVendre, "voir les ventes Chifa", ct);
        var q = string.IsNullOrWhiteSpace(filtre.Recherche) ? null : filtre.Recherche.Trim()[..Math.Min(filtre.Recherche.Trim().Length, 60)];
        return await ventes.ListerAsync(filtre with { Recherche = q }, page, ct);
    }

    public async Task<VenteChifa> LireAsync(long venteId, long utilisateurId, CancellationToken ct = default)
    {
        await droits.ExigerAsync(utilisateurId, Permissions.CaisseVendre, "voir une vente Chifa", ct);
        var v = await ventes.LireAsync(venteId, ct) ?? throw new RegleMetierException("Vente introuvable.", "vente");
        return v.Canal == "chifa" ? v : throw new RegleMetierException("Cette vente n’est pas facturée par Chifa.", "vente");
    }

    public async Task<IReadOnlyList<(long Id, string Nom)>> EmployesAsync(long utilisateurId, CancellationToken ct = default)
    {
        await droits.ExigerAsync(utilisateurId, Permissions.CaisseVendre, "choisir l’employé qui a saisi la vente dans Chifa", ct);
        return await ventes.EmployesAsync(ct);
    }
}
