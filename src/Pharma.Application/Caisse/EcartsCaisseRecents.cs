using Pharma.Application.Commun;
using Pharma.Application.Securite;
using Pharma.Domain.Commun;

namespace Pharma.Application.Caisse;

/// <summary>
/// Écart de caisse d'une session clôturée : poste, employé, date de clôture, montant (négatif : manque). <paramref name="Employe"/> : celui
/// qui a clôturé (compté) la caisse ou fait la relève quand <paramref name="ClotureePar"/> est vrai ; sinon (aucune trace de la clôture au
/// journal), celui qui l'a ouverte. <paramref name="ClotureLe"/> : date de la clôture (ou de la relève), jamais celle de l'ouverture, donnée
/// à part (<paramref name="OuverteLe"/>). <paramref name="Releve"/> : session close par une relève (une nouvelle session l'a suivie,
/// session_caisse.releve_de). Un comptage intermédiaire ne clôt pas la caisse et n'y figure pas.
/// </summary>
public sealed record EcartCaisseRecent(long SessionId, string Poste, string Employe, DateTime ClotureLe, decimal Ecart, bool ClotureePar = true,
    DateTime? OuverteLe = null, bool Releve = false);

/// <summary>Écarts récents pas encore vus par le titulaire : nombre total et les plus récents.</summary>
public sealed record EcartsCaisseNonVus(int Nombre, IReadOnlyList<EcartCaisseRecent> Derniers);

public interface IEcartsCaisseRecents
{
    /// <summary>Sessions clôturées depuis <paramref name="jours"/> jours avec un écart non nul, pas encore vues ; la plus récente d'abord, par page. Texte : poste ou employé (contient) ; null : toutes.</summary>
    Task<EcartsCaisseNonVus> NonVusAsync(int jours, string? texte, DemandePage page, CancellationToken ct = default);
    /// <summary>
    /// Marque vus les écarts non vus clôturés au plus tard en même temps que la session <paramref name="jusquA"/> (la plus récente
    /// affichée) : un écart arrivé depuis reste signalé.
    /// </summary>
    Task<int> MarquerVusAsync(long jusquA, long utilisateurId, CancellationToken ct = default);
}

/// <summary>
/// Tuile « Écarts de caisse récents » de l'accueil, réservée au titulaire : écarts non nuls des caisses clôturées depuis
/// <see cref="JoursRecents"/> jours, jusqu'à ce qu'il les marque vus. Complète le journal des actions et l'e-mail d'écart.
/// </summary>
public sealed class ConsulterEcartsCaisseRecents(IEcartsCaisseRecents ecarts, IPermissionService droits)
{
    public const int JoursRecents = 7;

    public async Task<EcartsCaisseNonVus> ExecuterAsync(long utilisateurId, string? texte = null, DemandePage? page = null, CancellationToken ct = default)
    {
        await droits.ExigerTitulaireAsync(utilisateurId, "consulter les écarts de caisse récents", ct);
        return await ecarts.NonVusAsync(JoursRecents, string.IsNullOrWhiteSpace(texte) ? null : texte.Trim(), page ?? new DemandePage(), ct);
    }
}

public sealed class MarquerEcartsCaisseVus(IEcartsCaisseRecents ecarts, IPermissionService droits)
{
    public async Task<int> ExecuterAsync(long jusquA, long utilisateurId, CancellationToken ct = default)
    {
        await droits.ExigerTitulaireAsync(utilisateurId, "marquer les écarts de caisse comme vus", ct);
        if (jusquA <= 0) throw new RegleMetierException("Aucun écart de caisse à marquer comme vu.");
        return await ecarts.MarquerVusAsync(jusquA, utilisateurId, ct);
    }
}
