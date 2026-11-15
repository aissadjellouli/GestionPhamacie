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
    /// <summary>Comptages à facturer, réceptions en écart et livraisons à recompter (les plus anciens d'abord).</summary>
    Task<IReadOnlyList<ComptageReception>> EnCoursAsync(CancellationToken ct = default);
    /// <summary>Comptage en cours (à facturer, en écart ou à recompter) du même grossiste et du même BL (sans casse ni espaces).</summary>
    Task<bool> ComptageOuvertAsync(long fournisseurId, string numeroBl, CancellationToken ct = default);
    /// <summary>Passe d'un statut à l'autre seulement si le comptage est encore au statut attendu (deux postes à la fois).</summary>
    Task<bool> ChangerStatutAsync(long id, string attendu, string nouveau, long? facturePar, long? decidePar, CancellationToken ct = default);
    Task<bool> EnregistrerEcartAsync(long id, string saisie, FusionComptage fusion, decimal valeurManquante, long facturePar, CancellationToken ct = default);
    /// <summary>Recomptage : nouvelles lignes, écart recalculé avec la facture gardée ; seulement depuis « à recompter ».</summary>
    Task<bool> RecompterAsync(long id, IReadOnlyList<LigneComptee> lignes, FusionComptage fusion, decimal valeurManquante, long utilisateurId,
        CancellationToken ct = default);
}

/// <summary>Réception à l'aveugle active (réglage du titulaire dans Contrôle interne).</summary>
public sealed class ModeReception(Surveillance.ISurveillance surveillance)
{
    public async Task<bool> AveugleAsync(CancellationToken ct = default) => (await surveillance.ParametresAsync(ct)).ReceptionAveugle;
}

internal static class LignesComptees
{
    public static List<LigneComptee> Normaliser(IReadOnlyList<LigneComptee> lignes) => lignes.Select(l => l with
    {
        NumeroLot = Pharma.Domain.Stock.RegleLot.Normaliser(l.NumeroLot),
        Gtin = string.IsNullOrWhiteSpace(l.Gtin) ? null : l.Gtin.Trim(),
        NumerosSerie = l.NumerosSerie?.Where(n => !string.IsNullOrWhiteSpace(n)).Select(n => n.Trim()).ToList()
    }).ToList();
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
        var normalisees = LignesComptees.Normaliser(lignes);
        RegleComptageReception.VerifierComptage(numeroBl, normalisees, horloge.Aujourdhui);
        // Un même BL n'est compté qu'une fois à la fois : sinon la livraison entrerait deux fois en stock
        if (await comptages.ComptageOuvertAsync(fournisseurId, numeroBl!.Trim(), ct))
            throw new RegleMetierException($"Le BL {numeroBl.Trim()} de {grossiste.RaisonSociale} est déjà compté et en cours (voir « Livraisons comptées »).", "numero_bl");
        var id = await comptages.CreerAsync(fournisseurId, numeroBl.Trim(), normalisees, scansMobile ?? [], utilisateurId, ct);
        await journal.EcrireAsync(utilisateurId, "comptage_reception", "comptage_reception", id,
            new Dictionary<string, object?> { ["grossiste"] = grossiste.RaisonSociale, ["bl"] = numeroBl.Trim(), ["lots"] = normalisees.Count,
                ["boites"] = normalisees.Sum(l => l.Quantite) }, Permissions.AchatsReceptionner, ct);
        return id;
    }
}

/// <summary>Livraisons en cours ; VoirQuantites : les quantités facturées et comptées ne sont montrées qu'à qui décide des écarts.</summary>
public sealed record ComptagesEnCours(IReadOnlyList<ComptageReception> Comptages, bool VoirQuantites);

public sealed class ListerComptagesReception(IComptagesReception comptages, IPermissionService droits)
{
    public async Task<ComptagesEnCours> ExecuterAsync(long utilisateurId, CancellationToken ct = default)
    {
        var voir = await droits.APermissionAsync(utilisateurId, Permissions.AchatsValiderEcart, ct);
        if (!voir) await droits.ExigerAsync(utilisateurId, Permissions.AchatsReceptionner, "consulter les livraisons comptées", ct);
        return new ComptagesEnCours(await comptages.EnCoursAsync(ct), voir);
    }
}

public sealed class ObtenirComptageReception(IComptagesReception comptages, IPermissionService droits)
{
    public async Task<ComptageReception> ExecuterAsync(long id, long utilisateurId, CancellationToken ct = default)
    {
        await droits.ExigerAsync(utilisateurId, Permissions.AchatsReceptionner, "saisir la facture d’une livraison", ct);
        return await comptages.LireAsync(id, ct) ?? throw new RegleMetierException("Comptage introuvable.");
    }
}

/// <summary>
/// Comptage erroné (mauvais BL, mauvais grossiste) : avant la facture, par un réceptionnaire ; une fois la facture saisie (écart,
/// recomptage), seulement par qui décide des écarts.
/// </summary>
public sealed class AnnulerComptage(IComptagesReception comptages, IPermissionService droits, IJournal journal)
{
    public async Task ExecuterAsync(long id, long utilisateurId, CancellationToken ct = default)
    {
        var c = await comptages.LireAsync(id, ct) ?? throw new RegleMetierException("Comptage introuvable.");
        var permission = c.Statut == StatutsComptage.Compte ? Permissions.AchatsReceptionner : Permissions.AchatsValiderEcart;
        await droits.ExigerAsync(utilisateurId, permission, "annuler un comptage", ct);
        if (c.Statut is not (StatutsComptage.Compte or StatutsComptage.Ecart or StatutsComptage.ARecompter)
            || !await comptages.ChangerStatutAsync(id, c.Statut, StatutsComptage.Annule, null, utilisateurId, ct))
            throw new RegleMetierException("Ce comptage a déjà été traité (réceptionné ou annulé).");
        await journal.EcrireAsync(utilisateurId, "comptage_annule", "comptage_reception", id,
            new Dictionary<string, object?> { ["bl"] = c.NumeroBl, ["grossiste"] = c.Fournisseur, ["statut"] = c.Statut }, permission, ct);
    }
}

/// <summary>
/// Livraison à recompter (décidé sur un écart) : nouveau comptage à l'aveugle (ni facture ni ancien compte montrés), comparé à la
/// facture déjà saisie ; l'écart recalculé (ou nul) attend de nouveau la décision du titulaire ou d'un employé habilité.
/// </summary>
public sealed class RecompterLivraison(IComptagesReception comptages, IPermissionService droits, IHorloge horloge, IJournal journal,
    Surveillance.Surveiller? surveillance = null)
{
    public async Task<int> ExecuterAsync(long id, IReadOnlyList<LigneComptee> lignes, long utilisateurId, CancellationToken ct = default)
    {
        await droits.ExigerAsync(utilisateurId, Permissions.AchatsReceptionner, "recompter une livraison", ct);
        var c = await comptages.LireAsync(id, ct) ?? throw new RegleMetierException("Comptage introuvable.");
        if (c.Statut != StatutsComptage.ARecompter || c.Saisie is null) throw new RegleMetierException("Cette livraison n’est pas à recompter.");
        var normalisees = LignesComptees.Normaliser(lignes);
        RegleComptageReception.VerifierComptage(c.NumeroBl, normalisees, horloge.Aujourdhui);
        var fusion = RegleComptageReception.Fusionner(SaisieJson.Lire(c.Saisie).Lignes, normalisees);
        var valeur = SaisieJson.ValeurManquante(fusion);
        if (!await comptages.RecompterAsync(id, normalisees, fusion, valeur, utilisateurId, ct))
            throw new RegleMetierException("Cette livraison vient d’être recomptée sur un autre poste.");
        await journal.EcrireAsync(utilisateurId, "comptage_recompte", "comptage_reception", id,
            new Dictionary<string, object?>
            {
                ["bl"] = c.NumeroBl, ["grossiste"] = c.Fournisseur, ["compte_avant_par"] = c.Compteur, ["manquantes"] = fusion.BoitesManquantes,
                ["en_trop"] = fusion.BoitesEnTrop
            }, Permissions.AchatsReceptionner, ct);
        if (surveillance is not null) await surveillance.ExecuterAsync(ct);
        // Le recompteur ne voit pas le détail : seulement le nombre de lots encore en écart
        return fusion.Ecarts.Count;
    }
}

/// <summary>
/// Facture saisie : réception (sans écart) ou réception bloquée jusqu'à la validation de l'écart. VoirQuantites : le détail facturé /
/// compté n'est montré qu'à qui décide des écarts (celui qui saisit la facture ne voit pas le compté).
/// </summary>
public sealed record ResultatFacturation(ReceptionEnregistree? Reception, IReadOnlyList<EcartComptage> Ecarts, bool VoirQuantites = false);

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
        // Saisie de facture : ayant droit prix.modifier seulement, quel que soit le prix (même une facture « tout à 0 »)
        await droits.ExigerAsync(utilisateurId, Permissions.PrixModifier, "saisir les prix de la facture d’achat", ct);
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
            // Comptage rattaché à la réception dans la même transaction
            try { r = await receptionner.ExecuterDepuisComptageAsync(saisie with { Lignes = fusion.Lignes }, utilisateurId, comptageId, null, ct); }
            catch
            {
                await comptages.ChangerStatutAsync(comptageId, StatutsComptage.Receptionne, StatutsComptage.Compte, null, null, CancellationToken.None);
                throw;
            }
            await journal.EcrireAsync(utilisateurId, "comptage_facture", "comptage_reception", comptageId,
                new Dictionary<string, object?> { ["bl"] = c.NumeroBl, ["reception"] = r.ReceptionId, ["compte_par"] = c.Compteur },
                Permissions.AchatsReceptionner, CancellationToken.None);   // livraison déjà en stock : le journal va jusqu'au bout
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
        return new ResultatFacturation(null, fusion.Ecarts, await droits.APermissionAsync(utilisateurId, Permissions.AchatsValiderEcart, ct));
    }
}

/// <summary>
/// Écart entre le comptage et la facture : valider (le stock prend le compté, la dette suit la facture, les boîtes manquantes
/// sont à réclamer au grossiste) ou faire recompter. Titulaire, ou employé qui a le droit achats.valider_ecart : le titulaire
/// est alors prévenu (SMS ou e-mail selon ses réglages).
/// </summary>
public sealed class DeciderEcartReception(IComptagesReception comptages, Receptionner receptionner, IPermissionService droits, IJournal journal,
    IAlertesTitulaire? alertes = null, ITraceErreurs? trace = null)
{
    public async Task<ReceptionEnregistree?> ExecuterAsync(long comptageId, bool valider, long utilisateurId, CancellationToken ct = default)
    {
        await droits.ExigerAsync(utilisateurId, Permissions.AchatsValiderEcart, "valider un écart de réception", ct);
        var c = await comptages.LireAsync(comptageId, ct) ?? throw new RegleMetierException("Comptage introuvable.");
        if (c.Statut != StatutsComptage.Ecart || c.Saisie is null) throw new RegleMetierException("Cet écart a déjà été traité.");
        var titulaire = await droits.EstTitulaireAsync(utilisateurId, ct);
        // Personne ne décide de son propre écart (sauf le titulaire) : sinon un vol se maquille en boîtes « à réclamer au grossiste »
        if (!titulaire && (utilisateurId == c.ComptePar || utilisateurId == c.FacturePar))
            throw new RegleMetierException("Vous avez compté ou saisi la facture de cette livraison : l'écart doit être décidé par le titulaire ou un autre employé habilité.");

        if (!valider)
        {
            if (!await comptages.ChangerStatutAsync(comptageId, StatutsComptage.Ecart, StatutsComptage.ARecompter, null, utilisateurId, ct))
                throw new RegleMetierException("Cet écart vient d’être traité sur un autre poste.");
            await journal.EcrireAsync(utilisateurId, "ecart_reception_recompter", "comptage_reception", comptageId,
                new Dictionary<string, object?> { ["bl"] = c.NumeroBl, ["grossiste"] = c.Fournisseur }, Permissions.AchatsValiderEcart, ct);
            // Décision enregistrée : l'alerte ne la fait jamais échouer (journal du serveur, bandeau du titulaire)
            if (!titulaire && alertes is not null)
                await Notifications.AlerteNonEnvoyeeSignalee.SansBlocageAsync(() => alertes.SurveillanceAsync(
                    $"Ecart de reception BL {c.NumeroBl} ({c.Fournisseur}) : recomptage demande par un employe habilite.", false, ct),
                    $"écart de réception BL {c.NumeroBl}", trace);
            return null;
        }

        var saisie = SaisieJson.Lire(c.Saisie);
        var fusion = RegleComptageReception.Fusionner(saisie.Lignes, c.Lignes);
        if (!await comptages.ChangerStatutAsync(comptageId, StatutsComptage.Ecart, StatutsComptage.Receptionne, null, utilisateurId, ct))
            throw new RegleMetierException("Cet écart vient d’être traité sur un autre poste.");
        ReceptionEnregistree r;
        try
        {
            // Celui qui valide réceptionne (un produit bloqué à l'achat demande aussi son droit produits.bloquer) ; l'entrée en stock,
            // la sortie des boîtes manquantes et le rattachement du comptage forment une seule transaction : tout ou rien
            r = await receptionner.ExecuterDepuisComptageAsync(saisie with { Lignes = fusion.Lignes, Blocage = new Catalogue.ValidationBlocage(true) },
                utilisateurId, comptageId, fusion.Manquants, ct);
        }
        catch
        {
            await comptages.ChangerStatutAsync(comptageId, StatutsComptage.Receptionne, StatutsComptage.Ecart, null, null, CancellationToken.None);
            throw;
        }
        var aReclamer = r.AReclamer;
        await journal.EcrireAsync(utilisateurId, "ecart_reception_valide", "comptage_reception", comptageId,
            new Dictionary<string, object?>
            {
                ["bl"] = c.NumeroBl, ["grossiste"] = c.Fournisseur, ["reception"] = r.ReceptionId, ["compte_par"] = c.Compteur,
                ["facture_par"] = c.Factureur, ["manquantes"] = fusion.BoitesManquantes, ["en_trop"] = fusion.BoitesEnTrop, ["a_reclamer"] = aReclamer
            }, Permissions.AchatsValiderEcart, CancellationToken.None);   // livraison déjà en stock : le journal va jusqu'au bout
        if (!titulaire && alertes is not null)
            await Notifications.AlerteNonEnvoyeeSignalee.SansBlocageAsync(() => alertes.SurveillanceAsync(
                $"Ecart de reception BL {c.NumeroBl} ({c.Fournisseur}) valide par un employe habilite : {fusion.BoitesManquantes} boite(s) manquante(s) a reclamer ({aReclamer:0.00} DA), {fusion.BoitesEnTrop} en trop.",
                fusion.BoitesManquantes > 0, CancellationToken.None), $"écart de réception BL {c.NumeroBl}", trace);
        // Boîtes réellement entrées : sans les manquantes (ni leurs étiquettes, qui ne s'impriment pas)
        var lots = r.Lots.ToList();
        foreach (var m in fusion.Manquants)
            lots[m.Index] = lots[m.Index] with { Quantite = lots[m.Index].Quantite - m.Boites, Etiquettes = Math.Max(lots[m.Index].Etiquettes - m.Boites, 0) };
        return r with { Lots = lots };
    }
}
