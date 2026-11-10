using Pharma.Application.Caisse;
using Pharma.Application.Commun;
using Pharma.Application.Securite;
using Pharma.Domain.Achats;
using Pharma.Domain.Commun;
using Pharma.Domain.Securite;

namespace Pharma.Application.Achats;

public sealed record GrossisteAReglement(long Id, string Nom, decimal Solde, decimal ResteFactures, int FacturesOuvertes);

public sealed record FactureFournisseur(long ReceptionId, string? NumeroFacture, string? NumeroBl, DateTime Date, decimal Montant, decimal Regle, decimal Reste);

public sealed record ReglementPasse(long Id, DateOnly Date, decimal Montant, string Moyen, string? Reference, DateOnly? Echeance, decimal PartTiroir, string Employe);

/// <summary>Dette du grossiste (solde, avoirs et reprise compris), factures encore à payer, derniers règlements.</summary>
public sealed record SituationGrossiste(long Id, string Nom, decimal Solde, IReadOnlyList<FactureFournisseur> Factures, IReadOnlyList<ReglementPasse> Reglements);

public sealed record ReglementAEcrire(long FournisseurId, DateOnly Date, decimal Montant, MoyenReglement Moyen, string? Reference, string? Banque,
    DateOnly? Echeance, decimal PartTiroir, long? SessionCaisseId, long UtilisateurId, string? Commentaire,
    IReadOnlyList<(long ReceptionId, decimal Montant)> Imputations, long? CompteId = null);

public interface IReglementsFournisseurs
{
    Task<IReadOnlyList<GrossisteAReglement>> GrossistesAsync(CancellationToken ct = default);
    Task<SituationGrossiste?> SituationAsync(long fournisseurId, CancellationToken ct = default);
    Task<long> EnregistrerAsync(ReglementAEcrire r, CancellationToken ct = default);
}

public sealed class ConsulterReglements(IReglementsFournisseurs reglements, IPermissionService droits)
{
    public async Task<IReadOnlyList<GrossisteAReglement>> GrossistesAsync(long utilisateurId, CancellationToken ct = default)
    {
        await droits.ExigerAsync(utilisateurId, Permissions.AchatsRegler, "consulter les règlements des grossistes", ct);
        return await reglements.GrossistesAsync(ct);
    }

    public async Task<SituationGrossiste> SituationAsync(long fournisseurId, long utilisateurId, CancellationToken ct = default)
    {
        await droits.ExigerAsync(utilisateurId, Permissions.AchatsRegler, "consulter les règlements des grossistes", ct);
        return await reglements.SituationAsync(fournisseurId, ct) ?? throw new RegleMetierException("Grossiste introuvable.");
    }
}

/// <summary>Une facture choisie, avec la part du règlement qui lui est imputée.</summary>
public sealed record ImputationDemandee(long ReceptionId, decimal Montant);

/// <summary>
/// Règlement d'un grossiste (droit « achats.regler »), imputé facture par facture : les factures choisies, ou les plus anciennes
/// si aucune n'est choisie. La dette du grossiste diminue du montant réglé (le surplus éventuel réduit l'ancien solde, sans facture).
/// Espèces : un coffre à part ; le complément pris dans le tiroir sort de la caisse ouverte du poste.
/// </summary>
public sealed class EnregistrerReglement(IReglementsFournisseurs reglements, ISessionsCaisse sessions, IPermissionService droits, IHorloge horloge, IJournal journal)
{
    public async Task<long> ExecuterAsync(long fournisseurId, DateOnly? date, decimal montant, string? moyen, string? reference, string? banque,
        DateOnly? echeance, decimal partTiroir, string? commentaire, IReadOnlyList<ImputationDemandee>? imputations, long utilisateurId, string? poste,
        long? compteId = null, CancellationToken ct = default)
    {
        await droits.ExigerAsync(utilisateurId, Permissions.AchatsRegler, "régler un grossiste", ct);
        var m = RegleReglement.Moyen(moyen);
        var jour = date ?? horloge.Aujourdhui;
        RegleReglement.Verifier(montant, m, reference, echeance, partTiroir, jour, horloge.Aujourdhui);
        var situation = await reglements.SituationAsync(fournisseurId, ct) ?? throw new RegleMetierException("Grossiste introuvable.");
        if (montant > Math.Max(situation.Solde, 0) + 0.005m)
            throw new RegleMetierException($"Le montant dépasse la dette envers {situation.Nom} ({situation.Solde:N2} DA).");

        var restes = situation.Factures.ToDictionary(f => f.ReceptionId, f => f.Reste);
        IReadOnlyList<(long, decimal)> lignes = imputations is { Count: > 0 }
            ? imputations.Select(i => (i.ReceptionId, i.Montant)).ToList()
            : RegleReglement.ImputerAuxPlusAnciennes(montant, situation.Factures.Select(f => new FactureAPayer(f.ReceptionId, f.Date, f.Reste)));
        RegleReglement.VerifierImputations(montant, lignes, restes);

        long? sessionId = null;
        if (partTiroir > 0)
            sessionId = (await sessions.SessionDuPosteAsync(PosteCaisse.Exiger(poste), ct)
                        ?? throw new RegleMetierException("Ouvrez la caisse du poste pour prendre un complément dans le tiroir.")).Id;

        var id = await reglements.EnregistrerAsync(new ReglementAEcrire(fournisseurId, jour, montant, m,
            string.IsNullOrWhiteSpace(reference) ? null : reference.Trim(), string.IsNullOrWhiteSpace(banque) ? null : banque.Trim(),
            m == MoyenReglement.Effet ? echeance : null, partTiroir, sessionId, utilisateurId,
            string.IsNullOrWhiteSpace(commentaire) ? null : commentaire.Trim(), lignes, m == MoyenReglement.Especes ? null : compteId), ct);
        await journal.EcrireAsync(utilisateurId, "reglement_grossiste", "reglement_fournisseur", id,
            new Dictionary<string, object?>
            {
                ["grossiste"] = situation.Nom, ["montant"] = montant, ["moyen"] = m.Code(), ["reference"] = reference,
                ["part_tiroir"] = partTiroir, ["factures"] = lignes.Count
            }, Permissions.AchatsRegler, ct);
        return id;
    }
}
