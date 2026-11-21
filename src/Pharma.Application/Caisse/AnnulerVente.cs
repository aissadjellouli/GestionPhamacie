using Pharma.Application.Commun;
using Pharma.Application.Securite;
using Pharma.Domain.Commun;
using Pharma.Domain.Securite;
using Pharma.Domain.Ventes;

namespace Pharma.Application.Caisse;

public sealed record LigneVenteAnnulable(string Produit, int Quantite, decimal Montant, string? Lot = null);

/// <summary>
/// Vente vue pour l'annulation. CaisseOuverte : la caisse où elle a été encaissée n'est pas clôturée (vente récente).
/// PayeSurVente (vente à crédit) : remboursements réellement versés par le patient, imputés à cette vente.
/// DansUnBordereau : sur un bordereau (en préparation ou déposé) ; Deposee : sur un bordereau sorti de « préparation » (script 155).
/// </summary>
public sealed record VenteAnnulable(
    long Id, string Numero, DateTime Date, string Vendeur, string? Patient, decimal Total, decimal PartPatient, decimal PartOrganisme,
    string? Mode, string Statut, bool CaisseOuverte, bool DansUnBordereau, IReadOnlyList<LigneVenteAnnulable> Lignes,
    decimal DejaRendu = 0, decimal SoldeCreditPatient = 0, string? FactureNumero = null, long? VendeurId = null, long? PatientId = null,
    decimal PayeSurVente = 0, string? Remise = null, RegularisationVente? Regularisation = null, decimal PayeParAvoir = 0,
    decimal AvoirRecrediteParRetours = 0, bool Deposee = false);

/// <summary>
/// Régularisation de part organisme active de la vente (script 097) : sort, montant mis à la charge du patient et part qu'il en a déjà
/// payée (lue sur v_vente_credit : la part à crédit de la vente est réputée payée d'abord, la régularisation ensuite).
/// </summary>
public sealed record RegularisationVente(long Id, string Sort, decimal MontantPatient, decimal PayeParLePatient);

/// <summary>
/// Régularisation à annuler dans la transaction de l'annulation (script 098) : jamais supprimée, statut annulée. DetteAnnulee : part due
/// annulée par écriture inverse ; Imputations : trop-payé déduit des autres ventes choisies ; Rembourse : trop-payé rendu par le tiroir
/// (sortie « Remboursement d'un client » dans la caisse ouverte du poste, bon de sortie) ; ValidePar : ayant droit au-delà du seuil.
/// </summary>
public sealed record RegularisationAAnnuler(long Id, string Sort, decimal DetteAnnulee, IReadOnlyList<(long VenteId, decimal Montant)> Imputations,
    decimal Rembourse, long? ValidePar = null, decimal Avoir = 0)
{
    public decimal Impute => Imputations.Sum(i => i.Montant);
}

/// <summary>
/// Annulation à écrire en une transaction : vente, stock (hors boîtes déjà rendues), boîtes, crédit, argent rendu.
/// Mode : mode de remboursement (espèces aussi pour la part d'un crédit déjà remboursée) ; ReductionDette : baisse de la dette.
/// ImputationsVente : trop-payé d'une vente à crédit déduit d'autres dettes du patient (choix, 2026-11-15) ; AvoirVente : avoir créé
/// (trop-payé laissé en avoir, avoir utilisé en paiement rendu à l'avoir), origine « annulation_vente » (script 106).
/// </summary>
public sealed record AnnulationAEcrire(
    long VenteId, string Motif, long UtilisateurId, long? ValidePar, bool Ancienne, string? Mode, decimal MontantRembourse,
    long? SessionCaisseId, string Poste, decimal ReductionDette = 0, long? PatientDetteId = null, bool AccordClient = false,
    decimal? DejaRenduLu = null, RegularisationAAnnuler? Regularisation = null,
    IReadOnlyList<(long VenteId, decimal Montant)>? ImputationsVente = null, decimal AvoirVente = 0);

public interface IAnnulationsVente
{
    Task<VenteAnnulable?> TrouverAsync(long? venteId, string? numero, CancellationToken ct = default);
    /// <summary>Dernières ventes encaissées sur la caisse ouverte du poste (les plus récentes d'abord).</summary>
    Task<IReadOnlyList<VenteAnnulable>> DernieresAsync(long sessionCaisseId, int limite, CancellationToken ct = default);
    /// <summary>Ventes encaissées ou annulées sur la caisse ouverte du poste, par page (les plus récentes d'abord).</summary>
    Task<Pharma.Application.Commun.Page<VenteAnnulable>> VentesSessionAsync(long sessionCaisseId, Pharma.Application.Commun.DemandePage page, CancellationToken ct = default);
    /// <summary>Annulation écrite en une transaction ; bon de sortie du tiroir si le trop-payé d'une régularisation est remboursé, sinon null.</summary>
    Task<MouvementTiroirEcrit?> AnnulerAsync(AnnulationAEcrire a, CancellationToken ct = default);
    /// <summary>Reste dû de chaque autre vente à crédit du patient (hors vente donnée), v_vente_credit (087).</summary>
    Task<IReadOnlyDictionary<long, decimal>> AutresDettesAsync(long patientId, long venteId, CancellationToken ct = default);
    /// <summary>Ventes de la session de caisse annulées (pour l'alerte au-delà du seuil).</summary>
    Task<int> AnnulationsSessionAsync(long sessionCaisseId, CancellationToken ct = default);
}

/// <summary>Regularisation* : effet de l'annulation de la régularisation de part organisme (dû annulé, trop-payé imputé, remboursé et son bon).</summary>
/// <remarks>TropPaye* : trop-payé d'une vente à crédit en partie payée (imputé, laissé en avoir) ; AvoirCree : total porté à l'avoir.</remarks>
public sealed record ResultatAnnulation(string Numero, decimal ARendre, string? Mode, string? ValidePar, decimal DetteDiminuee = 0,
    decimal RegularisationDetteAnnulee = 0, decimal RegularisationImputee = 0, decimal RegularisationRembourse = 0, MouvementTiroirEcrit? BonSortie = null,
    decimal TropPayeImpute = 0, decimal AvoirCree = 0);

/// <summary>Recherche d'une vente à annuler : par n° de ticket, ou les dernières ventes de la caisse du poste.</summary>
public sealed class RechercherVenteAnnulable(IAnnulationsVente annulations, ISessionsCaisse sessions)
{
    public async Task<IReadOnlyList<VenteAnnulable>> ExecuterAsync(string? numero, string? poste, CancellationToken ct = default)
    {
        if (!string.IsNullOrWhiteSpace(numero))
            return await annulations.TrouverAsync(null, numero.Trim().ToUpperInvariant(), ct) is { } v ? [v] : [];
        return await sessions.SessionDuPosteAsync(PosteCaisse.Exiger(poste), ct) is { } s
            ? await annulations.DernieresAsync(s.Id, 20, ct)
            : [];
    }

    /// <summary>
    /// Ventes de la caisse ouverte du poste, par page (règle des listes) ; un n° de ticket donné : cette vente seule, où qu'elle ait été faite.
    /// </summary>
    public async Task<Pharma.Application.Commun.Page<VenteAnnulable>> PageAsync(string? numero, string? poste, Pharma.Application.Commun.DemandePage page,
        CancellationToken ct = default)
    {
        if (!string.IsNullOrWhiteSpace(numero))
        {
            var v = await annulations.TrouverAsync(null, numero.Trim().ToUpperInvariant(), ct);
            return new(v is null ? [] : [v], v is null ? 0 : 1, 1, page.Taille);
        }
        return await sessions.SessionDuPosteAsync(PosteCaisse.Exiger(poste), ct) is { } s
            ? await annulations.VentesSessionAsync(s.Id, page, ct)
            : new([], 0, 1, page.Taille);
    }
}

/// <summary>
/// Annulation d'une vente encaissée, sans limite de durée, à l'appréciation du pharmacien et de son assistant.
/// Vente récente (caisse encore ouverte) : le caissier annule seul. Vente ancienne : droit « caisse.annuler_vente »
/// de l'employé, ou validation sur place par le code comptoir d'un employé qui l'a. L'argent rendu sort de la caisse
/// ouverte du poste ; la clôture d'une caisse passée n'est jamais modifiée.
/// </summary>
public sealed class AnnulerVente(IAnnulationsVente annulations, ISessionsCaisse sessions, IPermissionService droits,
    VerificationCodeComptoir codes, IJournal journal, Notifications.IAlertesTitulaire? alertes = null, Patients.IPatients? patients = null,
    Securite.AutorisationCourante? autorisation = null, ITraceErreurs? trace = null)
{
    /// <summary>Journal de l'annulation d'une régularisation de part organisme (script 098).</summary>
    public const string ActionRegularisationAnnulee = "regularisation_part_organisme_annulee";

    public async Task<ResultatAnnulation> ExecuterAsync(long venteId, string? motif, long utilisateurId, string? poste,
        string? validateurIdentifiant, string? validateurCode, CancellationToken ct = default, bool solderDette = false, long? patientDetteId = null,
        bool accordClient = false, IReadOnlyList<(long VenteId, decimal Montant)>? tropPayeImputations = null, decimal? tropPayeRembourse = null,
        decimal? tropPayeAvoir = null)
    {
        var nomPoste = PosteCaisse.Exiger(poste);
        var texte = RegleAnnulation.Motif(motif);
        var v = await annulations.TrouverAsync(venteId, null, ct) ?? throw new RegleMetierException("Vente introuvable.");
        RegleAnnulation.VerifierAnnulable(v.Statut, v.DansUnBordereau, v.FactureNumero, v.Deposee);

        long? validePar = null;
        string? nomValidateur = null;
        var habilite = await droits.APermissionAsync(utilisateurId, Permissions.CaisseAnnulerVente, ct);
        if (RegleAnnulation.Autorisation(v.CaisseOuverte, habilite) == AutorisationAnnulation.ValidationRequise)
        {
            if (string.IsNullOrWhiteSpace(validateurIdentifiant))
                throw new RegleMetierException("Vente d’une caisse déjà clôturée : le pharmacien ou l’assistant doit valider avec son code.");
            var validateur = await codes.VerifierAsync(validateurIdentifiant, validateurCode, nomPoste, ct, utilisateurId);
            if (!await droits.APermissionAsync(validateur.Id, Permissions.CaisseAnnulerVente, ct))
                throw new DroitRefuseException(Permissions.CaisseAnnulerVente, "valider l’annulation d’une vente ancienne");
            (validePar, nomValidateur) = (validateur.Id, validateur.NomComplet);
        }
        else if (!v.CaisseOuverte)
            // Employé habilité : il valide lui-même ; droit donné sur place par le code d'un collègue : c'est ce collègue qui valide
            (validePar, nomValidateur) = autorisation?.AutoriseParId is { } par ? (par, autorisation.AutoriseParNom) : (utilisateurId, null);

        // L'argent rendu sort de la caisse ouverte du poste qui annule
        var session = await sessions.SessionDuPosteAsync(nomPoste, ct);
        // Déjà rendu par des retours : non rendu une seconde fois. Crédit : dette diminuée de ce qui reste dû, le reste rendu en espèces.
        // Vente payée à un patient qui doit de l'argent : au choix du caissier, l'argent à rendre est retiré de sa dette.
        // Vente sans patient : s'il s'est nommé à l'annulation (patientDetteId), c'est sa dette, relue ici, qui est diminuée.
        var dette = v.SoldeCreditPatient;
        long? patientDette = null;
        if (solderDette && v.Mode != "credit" && patientDetteId is { } pid)
        {
            if (v.Patient is not null) throw new RegleMetierException("Cette vente a déjà un patient : c’est sa dette qui peut être soldée.");
            var patient = (patients is null ? null : await patients.TrouverAsync(pid, ct)) ?? throw new RegleMetierException("Patient introuvable.");
            (dette, patientDette) = (patient.SoldeCredit, pid);
        }
        var solder = solderDette && v.Mode != "credit";
        var resteVente = Math.Max(v.PartPatient - v.DejaRendu, 0);
        // Avoir utilisé en paiement (script 106) : il revient à l'avoir du patient, jamais en espèces ; le reste suit le mode de la vente
        var avoirRendu = v.PatientId is null ? 0 : RegleAvoir.AnnulationPayeeParAvoir(resteVente, Math.Max(v.PayeParAvoir - v.AvoirRecrediteParRetours, 0)).AvoirRendu;
        var (reductionDette, rembourse) = v.Mode == "credit"
            ? RegleAnnulation.RemboursementCredit(resteVente, resteVente, v.PayeSurVente, dette)
            : RegleAnnulation.Remboursement(v.Mode, v.PartPatient - avoirRendu, v.DejaRendu, dette, solder);
        // Déduire de la dette une vente payée : jamais sans l'accord du client
        if (solder && reductionDette > 0 && !accordClient) throw RegleAnnulation.AccordClientManquant();
        // Vente à crédit en partie payée (2026-11-15) : le dû est simplement annulé (jamais remboursé) ; ce que le patient a réellement
        // payé (trop-payé) est réparti au choix, comme celui d'une régularisation annulée
        var tropPayeCredit = v.Mode == "credit" ? rembourse : 0;

        // Régularisation de part organisme (règles du titulaire 2026-11-15, script 098), annulée dans la même transaction. À la charge du
        // patient : 1) la part encore due (elle concerne cette vente) est annulée par écriture inverse, jamais remboursée ; 2) la part
        // réellement payée est répartie au choix du patient (obligatoire).
        decimal tropPayeRegul = 0, detteRegulAnnulee = 0;
        if (v.Regularisation is { } g0)
        {
            var detteRegul = v.PatientId is null ? 0 : Math.Max(0, v.SoldeCreditPatient - (patientDette is null ? reductionDette : 0));
            var e = Pharma.Domain.Ventes.RegularisationPartOrganisme.Annuler(g0.Sort, g0.MontantPatient, g0.PayeParLePatient, detteRegul);
            (detteRegulAnnulee, tropPayeRegul) = (e.DetteAnnulee, e.TropPaye);
        }

        // Trop-payé total en trois choix (2026-11-15, script 106) : solder d'autres dettes / rembourser par le tiroir / laisser en avoir ;
        // somme exactement égale au trop-payé. Part de la régularisation servie d'abord (ses montants restent inscrits sur elle).
        var tropPaye = tropPayeCredit + tropPayeRegul;
        var choix = new RepartitionTropPaye([], 0);
        if (tropPaye > 0)
        {
            var autres = v.PatientId is { } pat
                ? await annulations.AutresDettesAsync(pat, v.Id, ct)
                : (IReadOnlyDictionary<long, decimal>)new Dictionary<long, decimal>();
            choix = Pharma.Domain.Ventes.RegularisationPartOrganisme.Repartir(tropPaye, tropPayeImputations, tropPayeRembourse, autres, v.Id,
                tropPayeAvoir, v.PatientId is not null);
        }
        var imputeRegul = Math.Min(choix.Impute, tropPayeRegul);
        var rembourseRegul = Math.Min(choix.Rembourse, tropPayeRegul - imputeRegul);
        var avoirRegul = tropPayeRegul - imputeRegul - rembourseRegul;
        var (imputationsRegul, imputationsVente) = Scinder(choix.Imputations, imputeRegul);
        if (v.Mode == "credit") rembourse = choix.Rembourse - rembourseRegul;
        var avoirVente = choix.Avoir - avoirRegul + avoirRendu;

        var modeRemboursement = v.Mode == "credit" ? (rembourse > 0 ? "especes" : "credit") : v.Mode;
        if (modeRemboursement == "especes" && rembourse > 0 && session is null)
            throw new RegleMetierException("Ouvrez la caisse du poste : l’argent rendu au patient doit en sortir.");

        RegularisationAAnnuler? regul = null;
        if (v.Regularisation is { } g)
        {
            long? valideSortie = null;
            if (rembourseRegul > 0)
            {
                if (session is null) throw new RegleMetierException("Ouvrez la caisse du poste : le remboursement du patient sort du tiroir.");
                await EspecesTiroir.VerifierAsync(sessions, droits, session, rembourseRegul, utilisateurId, ct);
                var seuilSortie = (await sessions.ParametresAsync(ct)).SeuilSortieTiroir;
                if (Pharma.Domain.Caisse.RegleTiroir.ValidationRequise(Pharma.Domain.Caisse.RegleTiroir.Sortie, rembourseRegul, seuilSortie))
                {
                    await droits.ExigerAsync(utilisateurId, Permissions.CaisseCloturer, $"valider une sortie de caisse de plus de {seuilSortie:N2} DA", ct);
                    valideSortie = autorisation?.AutoriseParId ?? utilisateurId;
                }
            }
            regul = new RegularisationAAnnuler(g.Id, g.Sort, detteRegulAnnulee, imputationsRegul, rembourseRegul, valideSortie, avoirRegul);
        }

        var bon = await annulations.AnnulerAsync(new AnnulationAEcrire(v.Id, texte, utilisateurId, validePar, !v.CaisseOuverte, modeRemboursement, rembourse,
            session?.Id, nomPoste, reductionDette, patientDette, solder && reductionDette > 0, v.DejaRendu, regul, imputationsVente, avoirVente), ct);
        var imputeVente = imputationsVente.Sum(i => i.Montant);
        await journal.EcrireAsync(utilisateurId, "vente_annulee", "vente", v.Id,
            new Dictionary<string, object?>
            {
                ["ticket"] = v.Numero, ["motif"] = texte, ["montant"] = v.Total, ["rembourse"] = rembourse, ["mode"] = v.Mode,
                ["mode_remboursement"] = modeRemboursement, ["deja_rendu"] = v.DejaRendu, ["dette_diminuee"] = reductionDette,
                ["ancienne"] = !v.CaisseOuverte, ["valide_par"] = validePar,
                ["dette_soldee_au_choix"] = solder && reductionDette > 0, ["patient_dette"] = reductionDette > 0 ? patientDette : null,
                ["accord_client"] = solder && reductionDette > 0 ? true : null,
                // Trop-payé d'une vente à crédit en partie payée, et avoir (script 106)
                ["trop_paye"] = tropPayeCredit > 0 ? tropPayeCredit : null,
                ["trop_paye_vente_impute"] = imputeVente > 0 ? imputeVente : null,
                ["ventes_soldees"] = imputeVente > 0 ? imputationsVente.Select(i => new { vente = i.VenteId, montant = i.Montant }).ToList() : null,
                ["avoir_cree"] = avoirVente > 0 ? avoirVente : null,
                ["avoir_rendu"] = avoirRendu > 0 ? avoirRendu : null
            }, validePar is null ? null : Permissions.CaisseAnnulerVente, ct);

        if (regul is not null)
            await journal.EcrireAsync(utilisateurId, ActionRegularisationAnnulee, "vente", v.Id,
                new Dictionary<string, object?>
                {
                    ["ticket"] = v.Numero, ["regularisation"] = regul.Id, ["sort"] = regul.Sort, ["montant_patient"] = v.Regularisation!.MontantPatient,
                    ["paye_par_le_patient"] = v.Regularisation.PayeParLePatient, ["dette_annulee"] = regul.DetteAnnulee,
                    ["choix"] = new[] { regul.Impute > 0, regul.Rembourse > 0, regul.Avoir > 0 }.Count(x => x) > 1 ? "reparti"
                        : regul.Impute > 0 ? "solder_autres_dettes" : regul.Rembourse > 0 ? "rembourser" : regul.Avoir > 0 ? "avoir" : null,
                    ["trop_paye_impute"] = regul.Impute, ["ventes_soldees"] = regul.Imputations.Select(i => new { vente = i.VenteId, montant = i.Montant }).ToList(),
                    ["rembourse"] = regul.Rembourse, ["avoir"] = regul.Avoir, ["bon"] = bon?.Numero, ["valide_par"] = regul.ValidePar, ["motif"] = texte
                }, null, ct);

        // Au-delà du seuil réglé par le titulaire : journal et notification, une fois quand le seuil est franchi
        if (session is not null && (await sessions.ParametresAsync(ct)).SeuilAnnulations is var seuil and > 0
            && await annulations.AnnulationsSessionAsync(session.Id, ct) is var nombre && nombre == seuil + 1)
        {
            await journal.EcrireAsync(utilisateurId, "annulations_excessives", "session_caisse", session.Id,
                new Dictionary<string, object?> { ["poste"] = nomPoste, ["annulations"] = nombre, ["seuil"] = seuil }, null, ct);
            if (alertes is not null)
                // La notification ne bloque pas l'annulation (échec : journal du serveur, bandeau du titulaire)
                await Notifications.AlerteNonEnvoyeeSignalee.SansBlocageAsync(() => alertes.AnnulationsAsync(nomPoste, nombre, seuil, ct),
                    $"annulations sur la caisse {nomPoste}", trace);
        }
        return new ResultatAnnulation(v.Numero, rembourse, modeRemboursement, nomValidateur, reductionDette,
            regul?.DetteAnnulee ?? 0, regul?.Impute ?? 0, regul?.Rembourse ?? 0, bon, imputeVente, avoirVente + (regul?.Avoir ?? 0));
    }

    /// <summary>Imputations choisies scindées : la part de la régularisation d'abord (au plus partRegul), le reste pour la vente.</summary>
    internal static (List<(long VenteId, decimal Montant)> Regul, List<(long VenteId, decimal Montant)> Vente) Scinder(
        IReadOnlyList<(long VenteId, decimal Montant)> imputations, decimal partRegul)
    {
        List<(long, decimal)> regul = [], vente = [];
        var reste = partRegul;
        foreach (var (id, montant) in imputations)
        {
            var r = Math.Min(montant, reste);
            if (r > 0) regul.Add((id, r));
            if (montant - r > 0) vente.Add((id, montant - r));
            reste -= r;
        }
        return (regul, vente);
    }
}
