using Pharma.Application.Commun;
using Pharma.Application.Securite;
using Pharma.Domain.Commun;
using Pharma.Domain.Securite;
using Pharma.Domain.Ventes;

namespace Pharma.Application.Caisse;

public sealed record LigneVenteAnnulable(string Produit, int Quantite, decimal Montant);

/// <summary>Vente vue pour l'annulation. CaisseOuverte : la caisse où elle a été encaissée n'est pas clôturée (vente récente).</summary>
public sealed record VenteAnnulable(
    long Id, string Numero, DateTime Date, string Vendeur, string? Patient, decimal Total, decimal PartPatient, decimal PartOrganisme,
    string? Mode, string Statut, bool CaisseOuverte, bool DansUnBordereau, IReadOnlyList<LigneVenteAnnulable> Lignes,
    decimal DejaRendu = 0, decimal SoldeCreditPatient = 0, string? FactureNumero = null);

/// <summary>
/// Annulation à écrire en une transaction : vente, stock (hors boîtes déjà rendues), boîtes, crédit, argent rendu.
/// Mode : mode de remboursement (espèces aussi pour la part d'un crédit déjà remboursée) ; ReductionDette : baisse de la dette.
/// </summary>
public sealed record AnnulationAEcrire(
    long VenteId, string Motif, long UtilisateurId, long? ValidePar, bool Ancienne, string? Mode, decimal MontantRembourse,
    long? SessionCaisseId, string Poste, decimal ReductionDette = 0);

public interface IAnnulationsVente
{
    Task<VenteAnnulable?> TrouverAsync(long? venteId, string? numero, CancellationToken ct = default);
    /// <summary>Dernières ventes encaissées sur la caisse ouverte du poste (les plus récentes d'abord).</summary>
    Task<IReadOnlyList<VenteAnnulable>> DernieresAsync(long sessionCaisseId, int limite, CancellationToken ct = default);
    Task AnnulerAsync(AnnulationAEcrire a, CancellationToken ct = default);
}

public sealed record ResultatAnnulation(string Numero, decimal ARendre, string? Mode, string? ValidePar);

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
}

/// <summary>
/// Annulation d'une vente encaissée, sans limite de durée, à l'appréciation du pharmacien et de son assistant.
/// Vente récente (caisse encore ouverte) : le caissier annule seul. Vente ancienne : droit « caisse.annuler_vente »
/// de l'employé, ou validation sur place par le code comptoir d'un employé qui l'a. L'argent rendu sort de la caisse
/// ouverte du poste ; la clôture d'une caisse passée n'est jamais modifiée.
/// </summary>
public sealed class AnnulerVente(IAnnulationsVente annulations, ISessionsCaisse sessions, IPermissionService droits,
    VerificationCodeComptoir codes, IJournal journal)
{
    public async Task<ResultatAnnulation> ExecuterAsync(long venteId, string? motif, long utilisateurId, string? poste,
        string? validateurIdentifiant, string? validateurCode, CancellationToken ct = default)
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
            (validePar, nomValidateur) = (utilisateurId, null);   // employé habilité : il valide lui-même

        // L'argent rendu sort de la caisse ouverte du poste qui annule
        var session = await sessions.SessionDuPosteAsync(nomPoste, ct);
        // Déjà rendu par des retours : non rendu une seconde fois. Crédit : dette diminuée de ce qui reste dû, le reste rendu en espèces.
        var (reductionDette, rembourse) = RegleAnnulation.Remboursement(v.Mode, v.PartPatient, v.DejaRendu, v.SoldeCreditPatient);
        var modeRemboursement = v.Mode == "credit" ? (rembourse > 0 ? "especes" : "credit") : v.Mode;
        if (modeRemboursement == "especes" && rembourse > 0 && session is null)
            throw new RegleMetierException("Ouvrez la caisse du poste : l’argent rendu au patient doit en sortir.");

        await annulations.AnnulerAsync(new AnnulationAEcrire(v.Id, texte, utilisateurId, validePar, !v.CaisseOuverte, modeRemboursement, rembourse,
            session?.Id, nomPoste, reductionDette), ct);
        await journal.EcrireAsync(utilisateurId, "vente_annulee", "vente", v.Id,
            new Dictionary<string, object?>
            {
                ["ticket"] = v.Numero, ["motif"] = texte, ["montant"] = v.Total, ["rembourse"] = rembourse, ["mode"] = v.Mode,
                ["mode_remboursement"] = modeRemboursement, ["deja_rendu"] = v.DejaRendu, ["dette_diminuee"] = reductionDette,
                ["ancienne"] = !v.CaisseOuverte, ["valide_par"] = validePar
            }, validePar is null ? null : Permissions.CaisseAnnulerVente, ct);
        return new ResultatAnnulation(v.Numero, rembourse, modeRemboursement, nomValidateur);
    }
}
