using Pharma.Application.Commun;
using Pharma.Application.Securite;
using Pharma.Domain.Caisse;
using Pharma.Domain.Commun;
using Pharma.Domain.Securite;

namespace Pharma.Application.Caisse;

/// <summary>
/// Récapitulatif de la caisse ouverte, sans ses montants en espèces ni son total (comptage aveugle strict : ils donneraient
/// l'attendu). Carte, chèque, crédit et tiers payant : montants nets des remboursements.
/// </summary>
public sealed record ResumeCaisse(int NombreVentes, decimal Carte, decimal Cheque, decimal Credit, decimal TiersPayant,
    IReadOnlyList<(string Employe, int Ventes)> ParEmploye);

/// <summary>Comptage intermédiaire enregistré : l'écart n'est rendu qu'avec le droit de voir les écarts.</summary>
public sealed record ComptageIntermediaire(DateTime Le, string Employe, decimal Compte, decimal Attendu, decimal Ecart);

public interface IEtatIntermediaireCaisse
{
    Task<ResumeCaisse> ResumeAsync(long sessionId, CancellationToken ct = default);
    Task<IReadOnlyList<ComptageIntermediaire>> ComptagesAsync(long sessionId, CancellationToken ct = default);
    Task EnregistrerAsync(long sessionId, long utilisateurId, Comptage comptage, decimal attendu, decimal ecart, CancellationToken ct = default);
}

/// <summary>État de la caisse vu par un employé : comptages intermédiaires avec écart seulement s'il peut le voir.</summary>
public sealed record EtatIntermediaire(long SessionId, string Poste, DateTime OuverteLe, string? OuvertePar, decimal FondCaisse,
    ResumeCaisse Resume, IReadOnlyList<(DateTime Le, string Employe, decimal Compte, decimal? Ecart)> Comptages, bool PeutVoirEcart);

/// <summary>Résultat d'un comptage intermédiaire, présenté comme une clôture : détail et écart seulement avec le droit de voir les écarts.</summary>
public sealed record ResultatComptageIntermediaire(long SessionId, decimal Compte, decimal? FondCaisse, decimal? VentesEspeces, decimal? Attendu,
    decimal? Ecart, bool? AuDelaDuSeuil, decimal? SeuilEcart);

/// <summary>
/// État intermédiaire de la caisse du poste (droit de vendre) : récapitulatif sans espèces, et comptage du tiroir sans clôture.
/// Comptage aveugle strict : ni espèces, ni total, ni attendu avant le comptage ; après, l'écart pour qui peut le voir.
/// </summary>
public sealed class GererEtatIntermediaire(ISessionsCaisse sessions, IEtatIntermediaireCaisse etat, IPermissionService droits, IJournal journal)
{
    public async Task<EtatIntermediaire> LireAsync(long utilisateurId, string? poste, CancellationToken ct = default)
    {
        var session = await SessionOuverteAsync(poste, ct);
        var voirEcart = await droits.APermissionAsync(utilisateurId, Permissions.CaisseVoirEcart, ct);
        var comptages = (await etat.ComptagesAsync(session.Id, ct))
            .Select(c => (c.Le, c.Employe, c.Compte, voirEcart ? c.Ecart : (decimal?)null)).ToList();
        return new EtatIntermediaire(session.Id, session.Poste, session.OuverteLe, session.OuvertePar, session.FondCaisse,
            await etat.ResumeAsync(session.Id, ct), comptages, voirEcart);
    }

    public async Task<ResultatComptageIntermediaire> CompterAsync(long utilisateurId, string? poste, IReadOnlyDictionary<string, decimal> saisies,
        CancellationToken ct = default)
    {
        var session = await SessionOuverteAsync(poste, ct);
        var comptage = Comptage.Depuis(saisies);
        var parametres = await sessions.ParametresAsync(ct);
        // Même calcul que la clôture, sans clôturer : attendu = fond + espèces nettes de la session à cet instant
        var resultat = session.Cloturer(comptage, await sessions.VentesEspecesAsync(session.Id, ct), parametres.SeuilEcart);
        await etat.EnregistrerAsync(session.Id, utilisateurId, comptage, resultat.Attendu, resultat.Ecart, ct);
        await journal.EcrireAsync(utilisateurId, "comptage_intermediaire", "session_caisse", session.Id,
            new Dictionary<string, object?> { ["compte"] = resultat.Compte, ["attendu"] = resultat.Attendu, ["ecart"] = resultat.Ecart }, null, ct);
        return await droits.APermissionAsync(utilisateurId, Permissions.CaisseVoirEcart, ct)
            ? new ResultatComptageIntermediaire(session.Id, resultat.Compte, resultat.FondCaisse, resultat.VentesEspeces, resultat.Attendu, resultat.Ecart,
                resultat.AuDelaDuSeuil, parametres.SeuilEcart)
            : new ResultatComptageIntermediaire(session.Id, resultat.Compte, null, null, null, null, null, null);
    }

    private async Task<SessionCaisse> SessionOuverteAsync(string? poste, CancellationToken ct) =>
        await sessions.SessionDuPosteAsync(PosteCaisse.Exiger(poste), ct)
        ?? throw new RegleMetierException("Aucune caisse ouverte sur ce poste.");
}
