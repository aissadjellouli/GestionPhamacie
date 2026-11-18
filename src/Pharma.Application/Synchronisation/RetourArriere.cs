using Pharma.Application.Commun;
using Pharma.Application.Securite;
using Pharma.Domain.Commun;

namespace Pharma.Application.Synchronisation;

// ---- Synchronisation, lot S6.2 : base de l'officine revenue en arrière, nouvelle époque (docs/conception-synchronisation.md § 30) ----
// Détection : EnvoyerLotsSync (chaque passage, avant de former un lot). Alerte au titulaire : SurveillerSynchronisation (une par
// détection). Reprise : « Repartir sur une nouvelle époque », réservée au titulaire, refusée sans détection.

/// <summary>Séquence d'une table envoyée : dernier n° attribué avant et après l'avance (égaux : déjà au-delà du serveur central) ;
/// <paramref name="Erreur"/> : avance impossible pour cette table (verrou, valeur hors des bornes de la séquence…), Avant et Apres nuls.</summary>
public sealed record SequenceAvancee(string Table, long? Avant, long? Apres, string? Erreur = null);

public sealed record NouvelleEpoqueSync(int Epoque, int AncienneEpoque, int? Chargement);

/// <summary>Détection en cours (supervision, écran) : date, dernier lot vu au serveur central et formé ici, époque, avance des n° faite,
/// dernière alerte envoyée pour une détection.</summary>
public sealed record RetourArriereSync(DateTime LeUtc, long DernierLotCentral, long DernierLotLocal, int Epoque, bool SequencesAvancees, DateTime? AlerteLeUtc);

/// <summary>
/// « Repartir sur une nouvelle époque » (Administration › Synchronisation › État) : titulaire seulement, après un retour en arrière
/// détecté (refusée sinon), confirmée à l'écran avec l'époque affichée (refus si elle a changé entre-temps) ET par la ressaisie du
/// mot de passe du titulaire (même vérification que « Changer mon mot de passe » : essais comptés, blocage, refus journalisés).
/// Prend le verrou d'envoi (jamais pendant un passage). La nouvelle époque dépasse l'époque locale ET la plus grande époque connue du
/// serveur central pour l'officine (deux restaurations successives, 2e serveur) : le central doit répondre, sinon refus (nouvel essai).
/// Compteurs de lots à 0, lots et lignes de l'ancienne époque gardés, chargement complet demandé ; changement d'époque et journal
/// des actions dans la même transaction.
/// </summary>
public sealed class RepartirNouvelleEpoqueSync(IEnvoiSyncDepot depot, IPermissionService droits, VerrouSync verrou, IClientServeurCentral client,
    VerificationMotDePasseActuel motDePasse)
{
    public const string RefusSansDetection = "Aucun retour en arrière de la base n’est détecté : repartir sur une nouvelle époque n’est pas nécessaire.";
    public const string ChampMotDePasse = "mot_de_passe";

    public async Task<NouvelleEpoqueSync> ExecuterAsync(int epoqueAffichee, string? motDePasseTitulaire, long titulaireId, CancellationToken ct = default)
    {
        await droits.ExigerTitulaireAsync(titulaireId, "repartir sur une nouvelle époque de synchronisation", ct);
        await motDePasse.VerifierAsync(titulaireId, motDePasseTitulaire, "nouvelle_epoque_synchronisation", ChampMotDePasse, ct);
        if (!verrou.Prendre()) throw new RegleMetierException("Envoi de synchronisation en cours : réessayez dans un instant.");
        try
        {
            var p = await depot.ParametresAsync(ct);
            if (p.BaseEnLectureSeule) throw new RegleMetierException("Base en lecture seule (2e serveur en veille) : nouvelle époque impossible ici.");
            if (p.RetourArriereLe is null || p.RetourArriereCentral is not { } central) throw new RegleMetierException(RefusSansDetection);
            if (epoqueAffichee != p.Epoque)
                throw new RegleMetierException("L’époque a changé depuis l’affichage de l’écran : actualisez, puis recommencez si c’est encore nécessaire.");
            if (string.IsNullOrWhiteSpace(p.Adresse)) throw new RegleMetierException("Adresse du serveur central non réglée : nouvelle époque impossible.");
            int? epoqueCentral;
            try { epoqueCentral = await client.EpoqueMaxCentralAsync(p.Adresse, p.Epoque, ct); }
            catch (Exception e) when (e is ServeurCentralException or HttpRequestException || (e is TaskCanceledException && !ct.IsCancellationRequested))
            {
                throw new RegleMetierException("Serveur central injoignable : la plus grande époque qu’il connaît pour l’officine ne peut pas être lue. "
                    + "Rien n’a changé ; réessayez dans un instant.");
            }
            return await depot.NouvelleEpoqueAsync(titulaireId, epoqueCentral, p.Epoque, r => new Dictionary<string, object?>
            {
                ["epoque"] = $"{r.AncienneEpoque} -> {r.Epoque}",
                ["epoque_max_central"] = epoqueCentral,
                ["dernier_lot_central"] = central,
                ["dernier_lot_local"] = p.DernierLot,
                ["detecte_le"] = p.RetourArriereLe.Value.ToString("yyyy-MM-dd HH:mm:ss'Z'"),
                ["sequences_avancees"] = p.RetourArriereSequencesLe is not null,
                ["chargement"] = r.Chargement,
                ["validation"] = "mot_de_passe"
            }, ct);
        }
        finally { verrou.Rendre(); }
    }
}
