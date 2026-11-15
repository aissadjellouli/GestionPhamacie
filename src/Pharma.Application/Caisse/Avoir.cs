using Pharma.Application.Commun;
using Pharma.Application.Securite;
using Pharma.Domain.Caisse;
using Pharma.Domain.Commun;
using Pharma.Domain.Securite;
using Pharma.Domain.Ventes;

namespace Pharma.Application.Caisse;

/// <summary>Mouvement de l'avoir d'un patient (script 106). Sens : credit (l'avoir augmente) ou debit ; SoldeApres : solde après ce mouvement.</summary>
public sealed record MouvementAvoir(long Id, string Sens, string Origine, decimal Montant, decimal SoldeApres, long? VenteId, string? NumeroVente,
    string? Motif, DateTime CreeLe, string CreePar, string? BonSortie = null);

/// <summary>Avoir d'un patient : solde (jamais négatif) et une page de ses mouvements (du plus récent au plus ancien).</summary>
public sealed record SituationAvoir(long PatientId, decimal Solde, Page<MouvementAvoir> Mouvements);

/// <summary>Remboursement d'un avoir par le tiroir, à écrire en une transaction (sortie « Remboursement d'un client », bon lié).</summary>
public sealed record RemboursementAvoirAEcrire(long PatientId, decimal Montant, string Motif, long SessionCaisseId, string Poste, long UtilisateurId,
    long? ValidePar);

public interface IAvoirs
{
    /// <summary>Solde de l'avoir du patient (v_avoir_patient), 0 sans mouvement.</summary>
    Task<decimal> SoldeAsync(long patientId, CancellationToken ct = default);
    Task<Page<MouvementAvoir>> MouvementsAsync(long patientId, DemandePage page, CancellationToken ct = default);
    /// <summary>
    /// Sous verrou du patient : solde relu (refus s'il ne couvre pas le montant), sortie du tiroir (bon de sortie) et débit de l'avoir lié
    /// au bon. Rend le bon.
    /// </summary>
    Task<MouvementTiroirEcrit> RembourserAsync(RemboursementAvoirAEcrire r, CancellationToken ct = default);
}

/// <summary>Avoir d'un patient (droit caisse.vendre, comme les crédits) : solde et mouvements.</summary>
public sealed class ConsulterAvoir(IAvoirs avoirs)
{
    public async Task<SituationAvoir> ExecuterAsync(long patientId, DemandePage page, CancellationToken ct = default) =>
        new(patientId, Math.Max(await avoirs.SoldeAsync(patientId, ct), 0), await avoirs.MouvementsAsync(patientId, page, ct));
}

/// <summary>
/// Remboursement d'un avoir (décision du titulaire D-10, 2026-11-15) : sortie du tiroir « Remboursement d'un client » de la caisse ouverte
/// du poste, avec le même droit (caisse.ouvrir_tiroir) et le même seuil que les autres sorties (au-delà : caisse.cloturer, code comptoir
/// possible) ; bornée par les espèces du tiroir ; bon de sortie lié au débit de l'avoir ; journal.
/// </summary>
public sealed class RembourserAvoir(IAvoirs avoirs, ISessionsCaisse sessions, IPermissionService droits, IJournal journal,
    AutorisationCourante? autorisation = null)
{
    public const string Action = "avoir_rembourse";

    public async Task<MouvementTiroirEcrit> ExecuterAsync(long patientId, decimal montant, string? motif, long utilisateurId, string? poste,
        CancellationToken ct = default)
    {
        await droits.ExigerAsync(utilisateurId, Permissions.CaisseOuvrirTiroir, "rembourser un avoir par le tiroir", ct);
        var nomPoste = PosteCaisse.Exiger(poste);
        var texte = string.IsNullOrWhiteSpace(motif) ? "Remboursement de l’avoir" : motif.Trim();
        if (texte.Length > 300) throw new RegleMetierException("Motif : 300 caractères au plus.", "motif");
        montant = Arrondi.Centime(montant);
        RegleAvoir.VerifierRemboursement(montant, await avoirs.SoldeAsync(patientId, ct));
        var session = await sessions.SessionDuPosteAsync(nomPoste, ct)
                      ?? throw new RegleMetierException("Ouvrez la caisse du poste : le remboursement de l’avoir sort du tiroir.");
        await EspecesTiroir.VerifierAsync(sessions, droits, session, montant, utilisateurId, ct);
        long? validePar = null;
        var seuil = (await sessions.ParametresAsync(ct)).SeuilSortieTiroir;
        if (RegleTiroir.ValidationRequise(RegleTiroir.Sortie, montant, seuil))
        {
            await droits.ExigerAsync(utilisateurId, Permissions.CaisseCloturer, $"valider une sortie de caisse de plus de {seuil:N2} DA", ct);
            validePar = autorisation?.AutoriseParId ?? utilisateurId;
        }
        var bon = await avoirs.RembourserAsync(new RemboursementAvoirAEcrire(patientId, montant, texte, session.Id, nomPoste, utilisateurId, validePar), ct);
        await journal.EcrireAsync(utilisateurId, Action, "patient", patientId,
            new Dictionary<string, object?>
            {
                ["montant"] = montant, ["motif"] = texte, ["bon"] = bon.Numero, ["poste"] = nomPoste, ["valide_par"] = validePar
            }, Permissions.CaisseOuvrirTiroir, ct);
        return bon;
    }
}
