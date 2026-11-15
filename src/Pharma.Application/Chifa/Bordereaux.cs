using Pharma.Application.Commun;
using Pharma.Application.Referentiels;
using Pharma.Application.Securite;
using Pharma.Domain.Commun;
using Pharma.Domain.Referentiels;
using Pharma.Domain.Securite;
using Pharma.Domain.Ventes;

namespace Pharma.Application.Chifa;

// ---- Bordereaux Chifa / MDN (2026-11-15, script 110) ----

/// <summary>Bordereau et ses montants (v_bordereau) : Reste = attendu − versé − rejeté ; Ecart = déclaré − attendu.</summary>
public sealed record Bordereau(long Id, long OrganismeId, string Organisme, string Canal, long? CentrePayeurId, string? CentrePayeur,
    string Numero, DateOnly PeriodeDebut, DateOnly PeriodeFin, DateOnly? DateDepot, string Statut, decimal? MontantDeclare,
    int Ventes, decimal Attendu, decimal Rejete, int RejetsATraiter, decimal Verse, decimal Reste, DateTime CreeLe, string? CreePar,
    bool Impaye = false)
{
    public decimal? Ecart => RegleBordereau.Ecart(Attendu, MontantDeclare);
}

/// <summary>Facture (vente) d'un bordereau et son rejet éventuel.</summary>
public sealed record LigneBordereau(long VenteId, string NumeroVente, DateTime DateVente, long? PatientId, string? Patient, string? FactureChifa,
    decimal PartOrganisme, DateTime AjouteLe, DateTime? RejeteLe, string? Motif, decimal MontantRejete, string? Suite, DateTime? SuiteLe, string? SuitePar);

/// <summary>Part d'un versement d'organisme (trésorerie) imputée au bordereau.</summary>
public sealed record VersementBordereau(long MouvementId, DateOnly Date, string Compte, string? Reference, decimal Montant);

public sealed record HistoriqueBordereau(DateTime Date, string Employe, string Action, string? DetailsJson);

/// <summary>Vente Chifa / MDN à déposer (sur aucun bordereau) ; EnRetard : au-delà du délai réglé.</summary>
public sealed record VenteADeposer(long VenteId, string Numero, DateTime Date, long OrganismeId, string Organisme, string Canal,
    long? CentrePayeurId, string? CentrePayeur, long? PatientId, string? Patient, string? FactureChifa, decimal PartOrganisme, bool Redeposee, bool EnRetard);

/// <summary>Ce qu'il faut savoir d'une vente pour la rattacher, la rejeter ou réclamer le rejet au patient.</summary>
public sealed record VenteRattachable(long Id, string Numero, string Canal, string Statut, long? OrganismeId, long? CentrePayeurId, bool Exclue,
    bool SurUnBordereau, string? FactureChifa, bool RejeteeDefinitivement, decimal PartOrganisme, long? PatientId);

/// <summary>Filtre des bordereaux : Etat = statut, « impayes » (déposés non soldés), ou null (tous).</summary>
public sealed record FiltreBordereaux(long? OrganismeId = null, string? Etat = null, string? Recherche = null);

/// <summary>
/// Filtre des ventes à déposer : CentrePayeurId 0 = sans centre ; RetardSeulement ; AvecSansCentre : avec le centre choisi, aussi les
/// ventes sans centre (celles qu'un bordereau de ce centre peut recevoir).
/// </summary>
public sealed record FiltreVentesADeposer(long? OrganismeId = null, long? CentrePayeurId = null, string? Recherche = null, bool RetardSeulement = false,
    bool AvecSansCentre = false);

public sealed record BordereauAEcrire(long OrganismeId, string Canal, long? CentrePayeurId, string Numero, DateOnly PeriodeDebut, DateOnly PeriodeFin,
    decimal? MontantDeclare);

/// <summary>Compteurs (Accueil, alertes) : ventes à déposer (dont en retard), bordereaux impayés au-delà du délai.</summary>
public sealed record CompteursBordereaux(int VentesADeposer, int VentesEnRetard, decimal MontantADeposer, int BordereauxImpayes, decimal MontantImpaye,
    int RejetsATraiter);

public interface IBordereaux
{
    Task<Page<Bordereau>> ListerAsync(FiltreBordereaux filtre, DemandePage page, CancellationToken ct = default);
    Task<Bordereau?> LireAsync(long id, CancellationToken ct = default);
    Task<IReadOnlyList<LigneBordereau>> LignesAsync(long id, CancellationToken ct = default);
    Task<IReadOnlyList<VersementBordereau>> VersementsAsync(long id, CancellationToken ct = default);
    /// <summary>Journal du bordereau (création, ventes, dépôt, rejets, suites), du plus récent au plus ancien ; Details : JSON.</summary>
    Task<IReadOnlyList<HistoriqueBordereau>> HistoriqueAsync(long id, CancellationToken ct = default);
    Task<Page<VenteADeposer>> VentesADeposerAsync(FiltreVentesADeposer filtre, DemandePage page, CancellationToken ct = default);
    /// <summary>Ventes à déposer qui répondent au filtre (« toutes ») : identifiants seulement, au plus <paramref name="max"/>.</summary>
    Task<IReadOnlyList<long>> IdsVentesADeposerAsync(FiltreVentesADeposer filtre, int max, CancellationToken ct = default);
    Task<VenteRattachable?> VenteAsync(long venteId, CancellationToken ct = default);
    /// <summary>Vente encaissée portant ce n° de facture Chifa (null : aucune).</summary>
    Task<long?> VenteParFactureAsync(string factureChifa, CancellationToken ct = default);
    Task<CompteursBordereaux> CompteursAsync(CancellationToken ct = default);
    /// <summary>N° déjà pris pour l'organisme : RegleMetierException.</summary>
    Task<long> CreerAsync(BordereauAEcrire b, long utilisateurId, CancellationToken ct = default);
    /// <summary>Faux si le bordereau n'est plus en préparation.</summary>
    Task<bool> ModifierAsync(long id, string numero, DateOnly debut, DateOnly fin, decimal? declare, CancellationToken ct = default);
    /// <summary>Rattache les ventes (encore libres) à un bordereau en préparation ; centre de la vente vide : celui du bordereau. Nombre rattaché.</summary>
    Task<int> AjouterVentesAsync(long bordereauId, IReadOnlyList<long> ventes, long utilisateurId, CancellationToken ct = default);
    Task<bool> RetirerVenteAsync(long bordereauId, long venteId, CancellationToken ct = default);
    Task<bool> DeposerAsync(long id, DateOnly date, long utilisateurId, CancellationToken ct = default);
    /// <summary>Marque la facture rejetée (bordereau déposé non soldé) et recalcule le statut. Faux si déjà rejetée ou bordereau changé.</summary>
    Task<bool> RejeterAsync(long bordereauId, long venteId, string motifCode, string? texte, decimal montant, long utilisateurId, CancellationToken ct = default);
    /// <summary>
    /// Suite du rejet : redeposer (vente libérée), perte, patient (dette augmentée du montant rejeté, mouvement de crédit rattaché à la vente).
    /// Recalcule le statut. Faux si déjà traité.
    /// </summary>
    Task<bool> TraiterRejetAsync(long bordereauId, long venteId, string suite, long utilisateurId, string reference, CancellationToken ct = default);
}

/// <summary>PDF d'un bordereau : liste interne de contrôle (ventes, montants, rejets, paiements), pas le bordereau officiel de CHIFA-Officine.</summary>
public interface IDocumentsBordereau
{
    byte[] Bordereau(Bordereau b, IReadOnlyList<LigneBordereau> lignes, IReadOnlyList<VersementBordereau> versements, string? pharmacie);
}

/// <summary>
/// Finances › Chifa › Bordereaux (droit chifa.bordereaux) : liste, fiche, création (n° de CHIFA-Officine saisi), rattachement des ventes
/// à déposer (liste ou n° de facture Chifa), dépôt, rejets et leurs suites ; tout est journalisé.
/// </summary>
public sealed class GererBordereaux(IBordereaux bordereaux, ICentresPayeurs centres, IPermissionService droits, IJournal journal, IHorloge horloge,
    IMotifsReglables? motifs = null)
{
    public const int ToutesMax = 500;

    private Task Exiger(long u, string quoi, CancellationToken ct) => droits.ExigerAsync(u, Permissions.ChifaBordereaux, quoi, ct);

    public async Task<Page<Bordereau>> ListerAsync(FiltreBordereaux filtre, DemandePage page, long utilisateurId, CancellationToken ct = default)
    {
        await Exiger(utilisateurId, "voir les bordereaux Chifa", ct);
        return await bordereaux.ListerAsync(filtre with { Recherche = Court(filtre.Recherche) }, page, ct);
    }

    public async Task<(Bordereau Bordereau, IReadOnlyList<LigneBordereau> Lignes, IReadOnlyList<VersementBordereau> Versements)> FicheAsync(long id,
        long utilisateurId, CancellationToken ct = default)
    {
        await Exiger(utilisateurId, "voir un bordereau Chifa", ct);
        var b = await bordereaux.LireAsync(id, ct) ?? throw new RegleMetierException("Bordereau introuvable.", "bordereau");
        return (b, await bordereaux.LignesAsync(id, ct), await bordereaux.VersementsAsync(id, ct));
    }

    public async Task<IReadOnlyList<HistoriqueBordereau>> HistoriqueAsync(long id, long utilisateurId, CancellationToken ct = default)
    {
        await Exiger(utilisateurId, "voir l’historique d’un bordereau Chifa", ct);
        return await bordereaux.HistoriqueAsync(id, ct);
    }

    public async Task<Page<VenteADeposer>> VentesADeposerAsync(FiltreVentesADeposer filtre, DemandePage page, long utilisateurId, CancellationToken ct = default)
    {
        await Exiger(utilisateurId, "voir les ventes Chifa à déposer", ct);
        return await bordereaux.VentesADeposerAsync(filtre with { Recherche = Court(filtre.Recherche) }, page, ct);
    }

    public async Task<CompteursBordereaux> CompteursAsync(long utilisateurId, CancellationToken ct = default)
    {
        await Exiger(utilisateurId, "voir les bordereaux Chifa", ct);
        return await bordereaux.CompteursAsync(ct);
    }

    /// <summary>Nouveau bordereau en préparation : organisme Chifa (CNAS, CASNOS) avec son centre payeur, ou MDN (centre facultatif).</summary>
    public async Task<Bordereau> CreerAsync(long organismeId, long? centrePayeurId, string? numero, DateOnly debut, DateOnly fin, decimal? declare,
        long utilisateurId, CancellationToken ct = default)
    {
        await Exiger(utilisateurId, "créer un bordereau Chifa", ct);
        var n = RegleBordereau.Numero(numero);
        RegleBordereau.VerifierEntete(debut, fin, null, declare, horloge.Aujourdhui);
        var code = (await centres.CodeOrganismeAsync(organismeId, ct))?.Trim().ToUpperInvariant()
                   ?? throw new RegleMetierException("Organisme introuvable.", "organisme");
        var canal = code == "MDN" ? "mdn" : Caisse.OrganismeVoletChifa.Accepte(code) ? "chifa"
            : throw new RegleMetierException("Un bordereau est adressé à la CNAS, à la CASNOS ou au MDN.", "organisme");
        string? centre = null;
        if (centrePayeurId is > 0)
        {
            var c = await centres.LireAsync(centrePayeurId.Value, ct) ?? throw new RegleMetierException("Centre payeur introuvable.", "centre_payeur");
            if (c.OrganismeId != organismeId) throw new RegleMetierException($"Le centre {c.Code} n’appartient pas à {code}.", "centre_payeur");
            centre = $"{c.Code} · {c.Nom}";
        }
        else if (canal == "chifa") throw new RegleMetierException("Choisissez le centre payeur : un bordereau par centre.", "centre_payeur");
        var id = await bordereaux.CreerAsync(new BordereauAEcrire(organismeId, canal, centrePayeurId is > 0 ? centrePayeurId : null, n, debut, fin, declare),
            utilisateurId, ct);
        await journal.EcrireAsync(utilisateurId, "bordereau_cree", "bordereau", id,
            new Dictionary<string, object?>
            {
                ["numero"] = n, ["organisme"] = code, ["centre_payeur"] = centre, ["periode"] = $"{debut:yyyy-MM-dd} → {fin:yyyy-MM-dd}",
                ["montant_declare"] = declare
            }, Permissions.ChifaBordereaux, ct);
        return (await bordereaux.LireAsync(id, ct))!;
    }

    /// <summary>En-tête modifiable en préparation seulement (n°, période, montant déclaré) ; le montant déclaré reste modifiable ensuite.</summary>
    public async Task ModifierAsync(long id, string? numero, DateOnly debut, DateOnly fin, decimal? declare, long utilisateurId, CancellationToken ct = default)
    {
        await Exiger(utilisateurId, "modifier un bordereau Chifa", ct);
        var b = await bordereaux.LireAsync(id, ct) ?? throw new RegleMetierException("Bordereau introuvable.", "bordereau");
        RegleBordereau.ExigerPreparation(b.Statut);
        var n = RegleBordereau.Numero(numero);
        RegleBordereau.VerifierEntete(debut, fin, null, declare, horloge.Aujourdhui);
        if (!await bordereaux.ModifierAsync(id, n, debut, fin, declare, ct))
            throw new RegleMetierException("Ce bordereau vient d’être déposé : rechargez la fiche.", "statut");
        await journal.EcrireAsync(utilisateurId, "bordereau_modifie", "bordereau", id,
            new Dictionary<string, object?>
            {
                ["numero"] = n, ["periode"] = $"{debut:yyyy-MM-dd} → {fin:yyyy-MM-dd}", ["montant_declare"] = declare,
                ["avant"] = new { numero = b.Numero, debut = b.PeriodeDebut.ToString("yyyy-MM-dd"), fin = b.PeriodeFin.ToString("yyyy-MM-dd"), montant_declare = b.MontantDeclare }
            }, Permissions.ChifaBordereaux, ct);
    }

    /// <summary>Rattache des ventes cochées (ou « toutes » celles du filtre) à un bordereau en préparation ; chacune contrôlée.</summary>
    public async Task<int> AjouterVentesAsync(long bordereauId, IReadOnlyList<long>? ventes, FiltreVentesADeposer? toutes, long utilisateurId,
        CancellationToken ct = default)
    {
        await Exiger(utilisateurId, "rattacher des ventes à un bordereau", ct);
        var b = await bordereaux.LireAsync(bordereauId, ct) ?? throw new RegleMetierException("Bordereau introuvable.", "bordereau");
        RegleBordereau.ExigerPreparation(b.Statut);
        var ids = (ventes ?? []).ToList();
        if (toutes is not null)
            // « Toutes » : celles du filtre pour l'organisme du bordereau, de son centre ou sans centre (bordereau sans centre : tous)
            ids.AddRange(await bordereaux.IdsVentesADeposerAsync(toutes with
            {
                OrganismeId = b.OrganismeId, CentrePayeurId = b.CentrePayeurId, AvecSansCentre = b.CentrePayeurId is not null,
                Recherche = Court(toutes.Recherche)
            }, ToutesMax, ct));
        ids = ids.Distinct().ToList();
        if (ids.Count == 0) throw new RegleMetierException("Cochez au moins une vente à déposer.", "ventes");
        if (ids.Count > ToutesMax) throw new RegleMetierException($"{ToutesMax} ventes au plus à la fois.", "ventes");
        var numeros = new List<string>();
        foreach (var id in ids)
        {
            var v = await bordereaux.VenteAsync(id, ct) ?? throw new RegleMetierException("Vente introuvable.", "vente");
            try
            {
                Verifier(b, v);
            }
            catch (RegleMetierException e)
            {
                throw new RegleMetierException($"Vente {v.Numero} : {e.Message}", e.Champ);
            }
            numeros.Add(v.Numero);
        }
        var n = await bordereaux.AjouterVentesAsync(bordereauId, ids, utilisateurId, ct);
        if (n != ids.Count)
            throw new RegleMetierException("Une des ventes vient d’être mise sur un autre bordereau, ou le bordereau a été déposé : rechargez la liste.", "ventes");
        await journal.EcrireAsync(utilisateurId, "bordereau_ventes_ajoutees", "bordereau", bordereauId,
            new Dictionary<string, object?> { ["bordereau"] = b.Numero, ["ventes"] = numeros, ["nombre"] = n }, Permissions.ChifaBordereaux, ct);
        return n;
    }

    /// <summary>Scan ou saisie du n° de facture Chifa : la vente correspondante est rattachée (mêmes contrôles). Retourne son n° de vente.</summary>
    public async Task<string> AjouterParFactureAsync(long bordereauId, string? factureChifa, long utilisateurId, CancellationToken ct = default)
    {
        await Exiger(utilisateurId, "rattacher des ventes à un bordereau", ct);
        var f = new string((factureChifa ?? "").Where(char.IsLetterOrDigit).ToArray());
        if (f.Length == 0) throw new RegleMetierException("Scannez ou saisissez le n° de facture Chifa.", "facture");
        var venteId = await bordereaux.VenteParFactureAsync(f, ct)
                      ?? throw new RegleMetierException($"Aucune vente encaissée avec la facture Chifa {f}.", "facture");
        await AjouterVentesAsync(bordereauId, [venteId], null, utilisateurId, ct);
        return (await bordereaux.VenteAsync(venteId, ct))!.Numero;
    }

    public async Task RetirerVenteAsync(long bordereauId, long venteId, long utilisateurId, CancellationToken ct = default)
    {
        await Exiger(utilisateurId, "retirer une vente d’un bordereau", ct);
        var b = await bordereaux.LireAsync(bordereauId, ct) ?? throw new RegleMetierException("Bordereau introuvable.", "bordereau");
        RegleBordereau.ExigerPreparation(b.Statut);
        var v = await bordereaux.VenteAsync(venteId, ct) ?? throw new RegleMetierException("Vente introuvable.", "vente");
        if (!await bordereaux.RetirerVenteAsync(bordereauId, venteId, ct))
            throw new RegleMetierException("Cette vente n’est plus sur ce bordereau en préparation : rechargez la fiche.", "vente");
        await journal.EcrireAsync(utilisateurId, "bordereau_vente_retiree", "bordereau", bordereauId,
            new Dictionary<string, object?> { ["bordereau"] = b.Numero, ["vente"] = v.Numero }, Permissions.ChifaBordereaux, ct);
    }

    /// <summary>Dépôt à l'organisme : le bordereau et ses ventes sont figés (une vente d'un bordereau déposé ne s'annule pas).</summary>
    public async Task DeposerAsync(long id, DateOnly? date, long utilisateurId, CancellationToken ct = default)
    {
        await Exiger(utilisateurId, "déposer un bordereau Chifa", ct);
        var b = await bordereaux.LireAsync(id, ct) ?? throw new RegleMetierException("Bordereau introuvable.", "bordereau");
        var jour = date ?? horloge.Aujourdhui;
        RegleBordereau.VerifierDepot(b.Statut, b.Ventes, jour, b.PeriodeDebut, horloge.Aujourdhui);
        if (!await bordereaux.DeposerAsync(id, jour, utilisateurId, ct))
            throw new RegleMetierException("Ce bordereau vient d’être déposé : rechargez la fiche.", "statut");
        await journal.EcrireAsync(utilisateurId, "bordereau_depose", "bordereau", id,
            new Dictionary<string, object?>
            {
                ["bordereau"] = b.Numero, ["date_depot"] = jour.ToString("yyyy-MM-dd"), ["ventes"] = b.Ventes, ["attendu"] = b.Attendu,
                ["montant_declare"] = b.MontantDeclare, ["ecart"] = b.Ecart
            }, Permissions.ChifaBordereaux, ct);
    }

    /// <summary>Rejet d'une facture d'un bordereau déposé : motif (liste « rejet_chifa », « Autre » avec texte), montant ≤ part organisme ; suite facultative.</summary>
    public async Task RejeterAsync(long bordereauId, long venteId, string? motifCode, string? texte, decimal montant, string? suite, long utilisateurId,
        CancellationToken ct = default)
    {
        await Exiger(utilisateurId, "noter le rejet d’une facture Chifa", ct);
        var b = await bordereaux.LireAsync(bordereauId, ct) ?? throw new RegleMetierException("Bordereau introuvable.", "bordereau");
        var ligne = (await bordereaux.LignesAsync(bordereauId, ct)).FirstOrDefault(l => l.VenteId == venteId)
                    ?? throw new RegleMetierException("Cette vente n’est pas sur ce bordereau.", "vente");
        RegleBordereau.VerifierRejet(b.Statut, ligne.RejeteLe is not null, montant, ligne.PartOrganisme);
        string code, libelle;
        string? precision;
        if (motifs is not null)
            (code, libelle, precision) = await new MotifsActifs(motifs).ChoisirAsync(RegleMotifs.RejetChifa, motifCode, texte, "du rejet", ct);
        else
        {
            if (string.IsNullOrWhiteSpace(motifCode)) throw new RegleMetierException("Choisissez le motif du rejet.", "motif");
            code = motifCode.Trim();
            precision = code == RegleMotifs.Autre && !string.IsNullOrWhiteSpace(texte) ? texte.Trim() : null;
            if (code == RegleMotifs.Autre && precision is null) throw new RegleMetierException("Motif « Autre » : précisez le motif.", "motif_texte");
            libelle = precision is null ? code : $"Autre : {precision}";
        }
        if (!await bordereaux.RejeterAsync(bordereauId, venteId, code, precision, montant, utilisateurId, ct))
            throw new RegleMetierException("Cette facture vient d’être rejetée, ou le bordereau a changé : rechargez la fiche.", "vente");
        await journal.EcrireAsync(utilisateurId, "facture_chifa_rejetee", "bordereau", bordereauId,
            new Dictionary<string, object?>
            {
                ["bordereau"] = b.Numero, ["vente"] = ligne.NumeroVente, ["facture_chifa"] = ligne.FactureChifa, ["motif"] = libelle,
                ["montant_rejete"] = montant, ["part_organisme"] = ligne.PartOrganisme
            }, Permissions.ChifaBordereaux, ct);
        if (!string.IsNullOrWhiteSpace(suite)) await TraiterRejetAsync(bordereauId, venteId, suite, utilisateurId, ct);
    }

    /// <summary>Suite d'un rejet (une seule fois) : redéposer, passer en perte, réclamer au patient (sa dette augmente du montant rejeté).</summary>
    public async Task TraiterRejetAsync(long bordereauId, long venteId, string? suite, long utilisateurId, CancellationToken ct = default)
    {
        await Exiger(utilisateurId, "traiter le rejet d’une facture Chifa", ct);
        var b = await bordereaux.LireAsync(bordereauId, ct) ?? throw new RegleMetierException("Bordereau introuvable.", "bordereau");
        var ligne = (await bordereaux.LignesAsync(bordereauId, ct)).FirstOrDefault(l => l.VenteId == venteId)
                    ?? throw new RegleMetierException("Cette vente n’est pas sur ce bordereau.", "vente");
        var s = RegleBordereau.VerifierSuite(suite, ligne.RejeteLe is not null, ligne.Suite, ligne.PatientId is not null);
        if (!await bordereaux.TraiterRejetAsync(bordereauId, venteId, s, utilisateurId, $"Rejet Chifa · bordereau {b.Numero}", ct))
            throw new RegleMetierException("Ce rejet vient d’être traité : rechargez la fiche.", "suite");
        await journal.EcrireAsync(utilisateurId, "rejet_chifa_traite", "bordereau", bordereauId,
            new Dictionary<string, object?>
            {
                ["bordereau"] = b.Numero, ["vente"] = ligne.NumeroVente, ["suite"] = RegleBordereau.LibelleSuite(s), ["montant"] = ligne.MontantRejete,
                ["patient"] = s == RegleBordereau.Patient ? ligne.Patient : null
            }, Permissions.ChifaBordereaux, ct);
    }

    private static void Verifier(Bordereau b, VenteRattachable v) =>
        RegleBordereau.VerifierRattachement(b.Canal, b.OrganismeId, b.CentrePayeurId, v.Canal, v.Statut, v.OrganismeId, v.CentrePayeurId, v.Exclue,
            v.SurUnBordereau, v.FactureChifa, v.RejeteeDefinitivement, v.PartOrganisme);

    private static string? Court(string? q) => string.IsNullOrWhiteSpace(q) ? null : q.Trim()[..Math.Min(q.Trim().Length, 60)];
}
