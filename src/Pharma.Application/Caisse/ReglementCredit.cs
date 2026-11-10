using Pharma.Application.Commun;
using Pharma.Domain.Commun;
using Pharma.Domain.Ventes;

namespace Pharma.Application.Caisse;

public sealed record PatientEnCredit(long Id, string Nom, string? Telephone, decimal Solde, decimal Plafond, DateTime? DerniereVente);

public sealed record VenteACredit(string Numero, DateTime Date, decimal Montant);

public sealed record SituationCredit(PatientEnCredit Patient, IReadOnlyList<VenteACredit> Ventes);

public interface ICreditsClients
{
    Task<IReadOnlyList<PatientEnCredit>> AvecCreditAsync(string? recherche, CancellationToken ct = default);
    Task<SituationCredit?> SituationAsync(long patientId, CancellationToken ct = default);
    /// <summary>Paiement du patient (sans vente) dans la caisse, dette diminuée ; renvoie le n° du reçu.</summary>
    Task<long> EncaisserAsync(long patientId, decimal montant, string mode, long sessionCaisseId, long utilisateurId, CancellationToken ct = default);
}

public sealed record RecuCredit(long Numero, string Patient, decimal Montant, string Mode, decimal ResteDu);

/// <summary>Crédits clients au comptoir (droit caisse.vendre) : liste, situation, remboursement.</summary>
public sealed class ConsulterCredits(ICreditsClients credits)
{
    public Task<IReadOnlyList<PatientEnCredit>> ListerAsync(string? recherche, CancellationToken ct = default) =>
        credits.AvecCreditAsync(string.IsNullOrWhiteSpace(recherche) ? null : recherche.Trim(), ct);

    public async Task<SituationCredit> SituationAsync(long patientId, CancellationToken ct = default) =>
        await credits.SituationAsync(patientId, ct) ?? throw new RegleMetierException("Patient introuvable.");
}

/// <summary>Le patient rembourse tout ou partie de sa dette : l'argent entre dans la caisse ouverte du poste.</summary>
public sealed class EncaisserReglementCredit(ICreditsClients credits, ISessionsCaisse sessions, IJournal journal)
{
    public async Task<RecuCredit> ExecuterAsync(long patientId, decimal montant, string? mode, long utilisateurId, string? poste, CancellationToken ct = default)
    {
        var session = await sessions.SessionDuPosteAsync(PosteCaisse.Exiger(poste), ct)
                      ?? throw new RegleMetierException("Ouvrez la caisse du poste avant d’encaisser un règlement.");
        var s = await credits.SituationAsync(patientId, ct) ?? throw new RegleMetierException("Patient introuvable.");
        RegleReglementCredit.Verifier(montant, s.Patient.Solde, mode);
        var numero = await credits.EncaisserAsync(patientId, montant, mode!, session.Id, utilisateurId, ct);
        await journal.EcrireAsync(utilisateurId, "reglement_credit", "patient", patientId,
            new Dictionary<string, object?> { ["montant"] = montant, ["mode"] = mode, ["reste_du"] = s.Patient.Solde - montant, ["recu"] = numero }, null, ct);
        return new RecuCredit(numero, s.Patient.Nom, montant, mode!, s.Patient.Solde - montant);
    }
}
