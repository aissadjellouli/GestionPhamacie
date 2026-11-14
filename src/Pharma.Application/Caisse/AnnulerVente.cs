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
/// </summary>
public sealed record VenteAnnulable(
    long Id, string Numero, DateTime Date, string Vendeur, string? Patient, decimal Total, decimal PartPatient, decimal PartOrganisme,
    string? Mode, string Statut, bool CaisseOuverte, bool DansUnBordereau, IReadOnlyList<LigneVenteAnnulable> Lignes,
    decimal DejaRendu = 0, decimal SoldeCreditPatient = 0, string? FactureNumero = null, long? VendeurId = null, long? PatientId = null,
    decimal PayeSurVente = 0);

/// <summary>
/// Annulation à écrire en une transaction : vente, stock (hors boîtes déjà rendues), boîtes, crédit, argent rendu.
/// Mode : mode de remboursement (espèces aussi pour la part d'un crédit déjà remboursée) ; ReductionDette : baisse de la dette.
/// </summary>
public sealed record AnnulationAEcrire(
    long VenteId, string Motif, long UtilisateurId, long? ValidePar, bool Ancienne, string? Mode, decimal MontantRembourse,
    long? SessionCaisseId, string Poste, decimal ReductionDette = 0, long? PatientDetteId = null, bool AccordClient = false,
    decimal? DejaRenduLu = null);

public interface IAnnulationsVente
{
    Task<VenteAnnulable?> TrouverAsync(long? venteId, string? numero, CancellationToken ct = default);
    /// <summary>Dernières ventes encaissées sur la caisse ouverte du poste (les plus récentes d'abord).</summary>
    Task<IReadOnlyList<VenteAnnulable>> DernieresAsync(long sessionCaisseId, int limite, CancellationToken ct = default);
    /// <summary>Ventes encaissées ou annulées sur la caisse ouverte du poste, par page (les plus récentes d'abord).</summary>
    Task<Pharma.Application.Commun.Page<VenteAnnulable>> VentesSessionAsync(long sessionCaisseId, Pharma.Application.Commun.DemandePage page, CancellationToken ct = default);
    Task AnnulerAsync(AnnulationAEcrire a, CancellationToken ct = default);
    /// <summary>Ventes de la session de caisse annulées (pour l'alerte au-delà du seuil).</summary>
    Task<int> AnnulationsSessionAsync(long sessionCaisseId, CancellationToken ct = default);
}

public sealed record ResultatAnnulation(string Numero, decimal ARendre, string? Mode, string? ValidePar, decimal DetteDiminuee = 0);

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
    public async Task<ResultatAnnulation> ExecuterAsync(long venteId, string? motif, long utilisateurId, string? poste,
        string? validateurIdentifiant, string? validateurCode, CancellationToken ct = default, bool solderDette = false, long? patientDetteId = null,
        bool accordClient = false)
    {
        var nomPoste = PosteCaisse.Exiger(poste);
        var texte = RegleAnnulation.Motif(motif);
        var v = await annulations.TrouverAsync(venteId, null, ct) ?? throw new RegleMetierException("Vente introuvable.");
        RegleAnnulation.VerifierAnnulable(v.Statut, v.DansUnBordereau, v.FactureNumero);

        long? validePar = null;
        string? nomValidateur = null;
        var habilite = await droits.APermissionAsync(utilisateurId, Permissions.CaisseAnnulerVente, ct);
        if (RegleAnnulation.Autorisation(v.CaisseOuverte, habilite) == AutorisationAnnulation.ValidationRequise)
        {
            if (string.IsNullOrWhiteSpace(validateurIdentifiant))
                throw new RegleMetierException("Vente d’une caisse déjà clôturée : le pharmacien ou l’assistant doit valider avec son code.");
            var validateur = await codes.VerifierAsync(validateurIdentifiant, validateurCode, nomPoste, ct);
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
        var (reductionDette, rembourse) = v.Mode == "credit"
            ? RegleAnnulation.RemboursementCredit(resteVente, resteVente, v.PayeSurVente, dette)
            : RegleAnnulation.Remboursement(v.Mode, v.PartPatient, v.DejaRendu, dette, solder);
        // Déduire de la dette une vente payée : jamais sans l'accord du client
        if (solder && reductionDette > 0 && !accordClient) throw RegleAnnulation.AccordClientManquant();
        var modeRemboursement = v.Mode == "credit" ? (rembourse > 0 ? "especes" : "credit") : v.Mode;
        if (modeRemboursement == "especes" && rembourse > 0 && session is null)
            throw new RegleMetierException("Ouvrez la caisse du poste : l’argent rendu au patient doit en sortir.");

        await annulations.AnnulerAsync(new AnnulationAEcrire(v.Id, texte, utilisateurId, validePar, !v.CaisseOuverte, modeRemboursement, rembourse,
            session?.Id, nomPoste, reductionDette, patientDette, solder && reductionDette > 0, v.DejaRendu), ct);
        await journal.EcrireAsync(utilisateurId, "vente_annulee", "vente", v.Id,
            new Dictionary<string, object?>
            {
                ["ticket"] = v.Numero, ["motif"] = texte, ["montant"] = v.Total, ["rembourse"] = rembourse, ["mode"] = v.Mode,
                ["mode_remboursement"] = modeRemboursement, ["deja_rendu"] = v.DejaRendu, ["dette_diminuee"] = reductionDette,
                ["ancienne"] = !v.CaisseOuverte, ["valide_par"] = validePar,
                ["dette_soldee_au_choix"] = solder && reductionDette > 0, ["patient_dette"] = reductionDette > 0 ? patientDette : null,
                ["accord_client"] = solder && reductionDette > 0 ? true : null
            }, validePar is null ? null : Permissions.CaisseAnnulerVente, ct);

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
        return new ResultatAnnulation(v.Numero, rembourse, modeRemboursement, nomValidateur, reductionDette);
    }
}
