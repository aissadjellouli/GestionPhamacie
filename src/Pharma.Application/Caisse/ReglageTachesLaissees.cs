using Pharma.Application.Commun;
using Pharma.Application.Securite;
using Pharma.Domain.Commun;

namespace Pharma.Application.Caisse;

/// <summary>
/// Réglage de l'officine (script 156, parametre_caisse.taches_laissees_poste) : comment chaque poste garde la facture en préparation et
/// le retour fournisseur laissés au « Changer d'utilisateur ». Deux choix seulement, jamais d'écriture en clair :
/// « chiffre » (défaut) : fichier du poste chiffré AES-256 par le compte Windows (DPAPI), relu au redémarrage ;
/// « desactive » : en mémoire seulement, perdus à la fermeture du poste ; le fichier du poste est supprimé à la prochaine connexion.
/// </summary>
public static class ConservationTachesLaissees
{
    public const string Chiffre = "chiffre";
    public const string Desactive = "desactive";

    public static readonly IReadOnlyList<string> Modes = [Chiffre, Desactive];

    /// <summary>Mode connu (espaces et majuscules tolérés), sinon refus désignant le champ « taches_laissees_poste ».</summary>
    public static string Verifier(string? mode)
    {
        var m = mode?.Trim().ToLowerInvariant();
        return m is Chiffre or Desactive
            ? m
            : throw new RegleMetierException("Facture et retour laissés : choisissez « Chiffré » ou « Désactivé ».", "taches_laissees_poste");
    }
}

/// <summary>Lecture et écriture du réglage (ligne unique de parametre_caisse).</summary>
public interface IReglageTachesLaissees
{
    Task<string> LireAsync(CancellationToken ct = default);
    Task EnregistrerAsync(string mode, CancellationToken ct = default);
}

/// <summary>Lu par chaque poste à la connexion (tout employé connecté) et par l'écran d'administration.</summary>
public sealed class ObtenirReglageTachesLaissees(IReglageTachesLaissees depot)
{
    public Task<string> ExecuterAsync(CancellationToken ct = default) => depot.LireAsync(ct);
}

/// <summary>Le titulaire seul change le réglage ; chaque changement est inscrit au journal des actions (avant / après).</summary>
public sealed class ModifierReglageTachesLaissees(IReglageTachesLaissees depot, IPermissionService droits, IJournal journal)
{
    public const string ActionJournal = "taches_laissees_poste";

    public async Task ExecuterAsync(string? mode, long utilisateurId, CancellationToken ct = default)
    {
        await droits.ExigerTitulaireAsync(utilisateurId, "régler la conservation de la facture et du retour laissés sur les postes", ct);
        var apres = ConservationTachesLaissees.Verifier(mode);
        var avant = await depot.LireAsync(ct);
        // Même valeur : rien à enregistrer ni à inscrire
        if (avant == apres) return;
        await depot.EnregistrerAsync(apres, ct);
        await journal.EcrireAsync(utilisateurId, ActionJournal, "parametre_caisse", 1,
            new Dictionary<string, object?> { ["avant"] = avant, ["apres"] = apres }, null, ct);
    }
}
