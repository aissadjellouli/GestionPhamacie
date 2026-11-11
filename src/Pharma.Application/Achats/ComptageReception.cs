using System.Text.Json;
using Pharma.Application.Commun;
using Pharma.Application.Notifications;
using Pharma.Application.Securite;
using Pharma.Domain.Achats;
using Pharma.Domain.Commun;
using Pharma.Domain.Securite;

namespace Pharma.Application.Achats;

// ---- Réception à l'aveugle : comptage, puis facture saisie par une autre personne et comparée ----

public static class StatutsComptage
{
    public const string Compte = "compte";
    public const string Ecart = "ecart";
    public const string Receptionne = "receptionne";
    public const string ARecompter = "a_recompter";
    public const string Annule = "annule";
}

/// <summary>Comptage d'une livraison. Lignes : boîtes comptées (jamais montrées à celui qui saisit la facture).</summary>
public sealed record ComptageReception(
    long Id, long FournisseurId, string Fournisseur, string NumeroBl, string Statut, long ComptePar, string Compteur, DateTime CompteLe,
    long? FacturePar, string? Factureur, DateTime? FactureLe, IReadOnlyList<LigneComptee> Lignes, IReadOnlyList<long> ScansMobile,
    string? Saisie, IReadOnlyList<EcartComptage> Ecarts, int BoitesManquantes, int BoitesEnTrop, decimal ValeurManquante);

public interface IComptagesReception
{
    Task<long> CreerAsync(long fournisseurId, string numeroBl, IReadOnlyList<LigneComptee> lignes, IReadOnlyList<long> scansMobile,
        long utilisateurId, CancellationToken ct = default);
    Task<ComptageReception?> LireAsync(long id, CancellationToken ct = default);
    /// <summary>Comptages à facturer et réceptions en écart (les plus anciens d'abord).</summary>
    Task<IReadOnlyList<ComptageReception>> EnCoursAsync(CancellationToken ct = default);
    /// <summary>Passe d'un statut à l'autre seulement si le comptage est encore au statut attendu (deux postes à la fois).</summary>
    Task<bool> ChangerStatutAsync(long id, string attendu, string nouveau, long? facturePar, long? decidePar, CancellationToken ct = default);
    Task<bool> EnregistrerEcartAsync(long id, string saisie, FusionComptage fusion, decimal valeurManquante, long facturePar, CancellationToken ct = default);
    Task LierReceptionAsync(long id, long receptionId, CancellationToken ct = default);
    /// <summary>Boîtes facturées absentes du carton : sorties du stock (boîte « manquante »). Valeur au prix d'achat.</summary>
    Task<decimal> SortirManquantsAsync(long receptionId, string numeroBl, IReadOnlyList<(long LotId, int Boites)> manquants, long utilisateurId,
        CancellationToken ct = default);
}

/// <summary>Réception à l'aveugle active (réglage du titulaire dans Surveillance).</summary>
public sealed class ModeReception(Surveillance.ISurveillance surveillance)
{
    public async Task<bool> AveugleAsync(CancellationToken ct = default) => (await surveillance.ParametresAsync(ct)).ReceptionAveugle;
}

/// <summary>Première étape : les boîtes du carton, comptées (scan ou saisie) sans voir le bon de livraison.</summary>
public sealed class CompterLivraison(IComptagesReception comptages, IAchats achats, IPermissionService droits, IHorloge horloge, IJournal journal)
{
    public async Task<long> ExecuterAsync(long fournisseurId, string? numeroBl, IReadOnlyList<LigneComptee> lignes, IReadOnlyList<long>? scansMobile,
        long utilisateurId, CancellationToken ct = default)
    {
        await droits.ExigerAsync(utilisateurId, Permissions.AchatsReceptionner, "compter une livraison", ct);
        if (await achats.FournisseurAsync(fournisseurId, ct) is not { } grossiste)
            throw new RegleMetierException("Grossiste introuvable.", "grossiste");
        if (!grossiste.Actif) throw new RegleMetierException($"{grossiste.RaisonSociale} est désactivé : réactivez-le dans l’onglet Grossistes.", "grossiste");
        var normalisees = lignes.Select(l => l with
        {
            NumeroLot = Pharma.Domain.Stock.RegleLot.Normaliser(l.NumeroLot),
            Gtin = string.IsNullOrWhiteSpace(l.Gtin) ? null : l.Gtin.Trim(),
            NumerosSerie = l.NumerosSerie?.Where(n => !string.IsNullOrWhiteSpace(n)).Select(n => n.Trim()).ToList()
        }).ToList();
        RegleComptageReception.VerifierComptage(numeroBl, normalisees, horloge.Aujourdhui);
        var id = await comptages.CreerAsync(fournisseurId, numeroBl!.Trim(), normalisees, scansMobile ?? [], utilisateurId, ct);
        await journal.EcrireAsync(utilisateurId, "comptage_reception", "comptage_reception", id,
            new Dictionary<string, object?> { ["grossiste"] = grossiste.RaisonSociale, ["bl"] = numeroBl.Trim(), ["lots"] = normalisees.Count,
                ["boites"] = normalisees.Sum(l => l.Quantite) }, Permissions.AchatsReceptionner, ct);
        return id;
    }
}

public sealed class ListerComptagesReception(IComptagesReception comptages)
{
    public Task<IReadOnlyList<ComptageReception>> ExecuterAsync(CancellationToken ct = default) => comptages.EnCoursAsync(ct);
}

public sealed class ObtenirComptageReception(IComptagesReception comptages)
{
    public async Task<ComptageReception> ExecuterAsync(long id, CancellationToken ct = default) =>
        await comptages.LireAsync(id, ct) ?? throw new RegleMetierException("Comptage introuvable.");
}

/// <summary>Comptage erroné (mauvais BL, mauvais grossiste) avant la saisie de la facture.</summary>
public sealed class AnnulerComptage(IComptagesReception comptages, IPermissionService droits, IJournal journal)
{
    public async Task ExecuterAsync(long id, long utilisateurId, CancellationToken ct = default)
    {
        await droits.ExigerAsync(utilisateurId, Permissions.AchatsReceptionner, "annuler un comptage", ct);
        var c = await comptages.LireAsync(id, ct) ?? throw new RegleMetierException("Comptage introuvable.");
        if (!await comptages.ChangerStatutAsync(id, StatutsComptage.Compte, StatutsComptage.Annule, null, utilisateurId, ct))
            throw new RegleMetierException("Ce comptage a déjà été traité (facture saisie ou annulé).");
        await journal.EcrireAsync(utilisateurId, "comptage_annule", "comptage_reception", id,
            new Dictionary<string, object?> { ["bl"] = c.NumeroBl, ["grossiste"] = c.Fournisseur }, Permissions.AchatsReceptionner, ct);
    }
}

/// <summary>Facture saisie : réception (sans écart) ou réception bloquée jusqu'à la validation de l'écart.</summary>
public sealed record ResultatFacturation(ReceptionEnregistree? Reception, IReadOnlyList<EcartComptage> Ecarts);

internal static class SaisieJson
{
    private static readonly JsonSerializerOptions Options = new();
    public static string Ecrire(SaisieReception s) => JsonSerializer.Serialize(s, Options);
    public static SaisieReception Lire(string json) => JsonSerializer.Deserialize<SaisieReception>(json, Options)
                                                      ?? throw new RegleMetierException("Facture enregistrée illisible.");

    /// <summary>Valeur des boîtes manquantes au prix facturé (net de remise).</summary>
    public static decimal ValeurManquante(FusionComptage f) =>
        f.Manquants.Sum(m => Math.Round(f.Lignes[m.Index].PrixNetHt * m.Boites, 2, MidpointRounding.AwayFromZero));
}

/// <summary>
/// Seconde étape : la facture du grossiste, saisie sans voir le compté, est comparée au comptage. Sans écart, la livraison
/// entre en stock ; avec écart, rien n'entre : le titulaire (ou un employé qui a le droit achats.valider_ecart) décide.
/// </summary>
public sealed class FacturerComptage(IComptagesReception comptages, Receptionner receptionner, IPermissionService droits, IHorloge horloge,
    IJournal journal, Surveillance.Surveiller? surveillance = null)
{
    public async Task<ResultatFacturation> ExecuterAsync(long comptageId, SaisieReception s, long utilisateurId, CancellationToken ct = default)
    {
        await droits.ExigerAsync(utilisateurId, Permissions.AchatsReceptionner, "saisir la facture d’une livraison", ct);
        var c = await comptages.LireAsync(comptageId, ct) ?? throw new RegleMetierException("Comptage introuvable.");
        if (c.Statut != StatutsComptage.Compte) throw new RegleMetierException("La facture de cette livraison a déjà été saisie.");
        if (s.FournisseurId != c.FournisseurId)
            throw new RegleMetierException($"Ce comptage est celui d’une livraison de {c.Fournisseur}.", "grossiste");
        var saisie = s with { NumeroBl = c.NumeroBl };
        var fusion = RegleComptageReception.Fusionner(saisie.Lignes, c.Lignes);

        if (fusion.Ecarts.Count == 0)
        {
            // Réservé avant l'entrée en stock : deux postes ne réceptionnent jamais la même livraison
            if (!await comptages.ChangerStatutAsync(comptageId, StatutsComptage.Compte, StatutsComptage.Receptionne, utilisateurId, null, ct))
                throw new RegleMetierException("La facture de cette livraison vient d’être saisie sur un autre poste.");
            ReceptionEnregistree r;
            try { r = await receptionner.ExecuterDepuisComptageAsync(saisie with { Lignes = fusion.Lignes }, utilisateurId, ct); }
            catch
            {
                await comptages.ChangerStatutAsync(comptageId, StatutsComptage.Receptionne, StatutsComptage.Compte, null, null, CancellationToken.None);
                throw;
            }
            await comptages.LierReceptionAsync(comptageId, r.ReceptionId, ct);
            await journal.EcrireAsync(utilisateurId, "comptage_facture", "comptage_reception", comptageId,
                new Dictionary<string, object?> { ["bl"] = c.NumeroBl, ["reception"] = r.ReceptionId, ["compte_par"] = c.Compteur },
                Permissions.AchatsReceptionner, ct);
            if (surveillance is not null) await surveillance.ExecuterAsync(ct);   // comptage et facture par la même personne
            return new ResultatFacturation(r, []);
        }

        // Écart : la facture est contrôlée puis gardée telle quelle (jamais de code comptoir), rien n'entre en stock
        RegleAchats.VerifierReception(c.NumeroBl, fusion.Lignes, horloge.Aujourdhui);
        RegleFactureAchat.Verifier(fusion.Lignes, saisie.Timbre);
        var valeur = SaisieJson.ValeurManquante(fusion);
        if (!await comptages.EnregistrerEcartAsync(comptageId, SaisieJson.Ecrire(saisie with { Blocage = null }), fusion, valeur, utilisateurId, ct))
            throw new RegleMetierException("La facture de cette livraison vient d’être saisie sur un autre poste.");
        await journal.EcrireAsync(utilisateurId, "ecart_reception", "comptage_reception", comptageId,
            new Dictionary<string, object?>
            {
                ["bl"] = c.NumeroBl, ["grossiste"] = c.Fournisseur, ["compte_par"] = c.Compteur, ["manquantes"] = fusion.BoitesManquantes,
                ["en_trop"] = fusion.BoitesEnTrop, ["valeur_manquante"] = valeur,
                ["lignes"] = fusion.Ecarts.Select(e => $"{e.Nom} lot {e.NumeroLot} : facturé {e.Facture}, compté {e.Compte}").ToList()
            }, Permissions.AchatsReceptionner, ct);
        if (surveillance is not null) await surveillance.ExecuterAsync(ct);   // anomalie et alerte au titulaire
        return new ResultatFacturation(null, fusion.Ecarts);
    }
}

/// <summary>
/// Écart entre le comptage et la facture : valider (le stock prend le compté, la dette suit la facture, les boîtes manquantes
/// sont à réclamer au grossiste) ou faire recompter. Titulaire, ou employé qui a le droit achats.valider_ecart : le titulaire
/// est alors prévenu (SMS ou e-mail selon ses réglages).
/// </summary>
public sealed class DeciderEcartReception(IComptagesReception comptages, Receptionner receptionner, IPermissionService droits, IJournal journal,
    IAlertesTitulaire? alertes = null)
{
    public async Task<ReceptionEnregistree?> ExecuterAsync(long comptageId, bool valider, long utilisateurId, CancellationToken ct = default)
    {
        await droits.ExigerAsync(utilisateurId, Permissions.AchatsValiderEcart, "valider un écart de réception", ct);
        var c = await comptages.LireAsync(comptageId, ct) ?? throw new RegleMetierException("Comptage introuvable.");
        if (c.Statut != StatutsComptage.Ecart || c.Saisie is null) throw new RegleMetierException("Cet écart a déjà été traité.");
        var titulaire = await droits.EstTitulaireAsync(utilisateurId, ct);

        if (!valider)
        {
            if (!await comptages.ChangerStatutAsync(comptageId, StatutsComptage.Ecart, StatutsComptage.ARecompter, null, utilisateurId, ct))
                throw new RegleMetierException("Cet écart vient d’être traité sur un autre poste.");
            await journal.EcrireAsync(utilisateurId, "ecart_reception_recompter", "comptage_reception", comptageId,
                new Dictionary<string, object?> { ["bl"] = c.NumeroBl, ["grossiste"] = c.Fournisseur }, Permissions.AchatsValiderEcart, ct);
            if (!titulaire && alertes is not null)
                await alertes.SurveillanceAsync($"Ecart de reception BL {c.NumeroBl} ({c.Fournisseur}) : recomptage demande par un employe habilite.", false, ct);
            return null;
        }

        var saisie = SaisieJson.Lire(c.Saisie);
        var fusion = RegleComptageReception.Fusionner(saisie.Lignes, c.Lignes);
        if (!await comptages.ChangerStatutAsync(comptageId, StatutsComptage.Ecart, StatutsComptage.Receptionne, null, utilisateurId, ct))
            throw new RegleMetierException("Cet écart vient d’être traité sur un autre poste.");
        ReceptionEnregistree r;
        try
        {
            // Celui qui valide réceptionne : un produit bloqué à l'achat demande aussi son droit produits.bloquer
            r = await receptionner.ExecuterDepuisComptageAsync(saisie with { Lignes = fusion.Lignes, Blocage = new Catalogue.ValidationBlocage(true) },
                utilisateurId, ct);
        }
        catch
        {
            await comptages.ChangerStatutAsync(comptageId, StatutsComptage.Receptionne, StatutsComptage.Ecart, null, null, CancellationToken.None);
            throw;
        }
        await comptages.LierReceptionAsync(comptageId, r.ReceptionId, ct);
        var aReclamer = fusion.Manquants.Count == 0 ? 0m
            : await comptages.SortirManquantsAsync(r.ReceptionId, c.NumeroBl,
                fusion.Manquants.Select(m => (r.Lots[m.Index].LotId, m.Boites)).ToList(), utilisateurId, ct);
        await journal.EcrireAsync(utilisateurId, "ecart_reception_valide", "comptage_reception", comptageId,
            new Dictionary<string, object?>
            {
                ["bl"] = c.NumeroBl, ["grossiste"] = c.Fournisseur, ["reception"] = r.ReceptionId, ["compte_par"] = c.Compteur,
                ["facture_par"] = c.Factureur, ["manquantes"] = fusion.BoitesManquantes, ["en_trop"] = fusion.BoitesEnTrop, ["a_reclamer"] = aReclamer
            }, Permissions.AchatsValiderEcart, ct);
        if (!titulaire && alertes is not null)
            await alertes.SurveillanceAsync(
                $"Ecart de reception BL {c.NumeroBl} ({c.Fournisseur}) valide par un employe habilite : {fusion.BoitesManquantes} boite(s) manquante(s) a reclamer ({aReclamer:0.00} DA), {fusion.BoitesEnTrop} en trop.",
                fusion.BoitesManquantes > 0, ct);
        // Boîtes réellement entrées : sans les manquantes (ni leurs étiquettes, qui ne s'impriment pas)
        var lots = r.Lots.ToList();
        foreach (var m in fusion.Manquants)
            lots[m.Index] = lots[m.Index] with { Quantite = lots[m.Index].Quantite - m.Boites, Etiquettes = Math.Max(lots[m.Index].Etiquettes - m.Boites, 0) };
        return r with { Lots = lots };
    }
}
