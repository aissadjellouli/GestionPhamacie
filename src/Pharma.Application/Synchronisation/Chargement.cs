using Pharma.Application.Commun;
using Pharma.Domain.Commun;

namespace Pharma.Application.Synchronisation;

// ---- Synchronisation, lot S6 : chargement complet (docs/conception-synchronisation.md § 13) ----
// À l'activation, à la réactivation, sur demande du titulaire ou quand le serveur central a perdu des lots qui ne sont plus
// disponibles ici, l'état existant de chaque table envoyée part au serveur central, minimisé comme la file. Étapes :
//   1. constitution : instantané pris dans la base (verrou court sur la file + image exportée, voir ChargementSyncSql), lignes
//      « instantane » inscrites dans la file avec ordre = repère ; aucun lot n'est formé pendant ce temps ;
//   2. annonce au serveur central (n°, époque, repère, dernier lot accusé ici) avant le premier lot qui les contient ;
//   3. envoi par les lots ordinaires (bornes 500 lignes / 1 Mo, reprise après coupure au premier lot non accusé) ;
//   4. fin, quand toutes les lignes de l'instantané sont dans des lots accusés : le serveur central signale (sans les effacer)
//      les lignes qu'il a et que l'officine n'a plus.

public static class EtatsChargement
{
    public const string Demande = "demande";
    public const string Constitution = "constitution";
    public const string Pret = "pret";
    public const string Annonce = "annonce";
    public const string Termine = "termine";
    public const string Remplace = "remplace";

    public static bool EnCours(string etat) => etat is Demande or Constitution or Pret or Annonce;
}

public static class MotifsChargement
{
    public const string Activation = "activation";
    public const string Demande = "demande";
    public const string Reactivation = "reactivation";
    public const string LotsManquants = "lots_manquants";
}

/// <summary>Chargement complet (sync_chargement, script 129) et sa progression.</summary>
/// <param name="Lignes">Lignes de l'instantané par table (vide avant la constitution).</param>
/// <param name="LignesAccusees">Lignes de l'instantané dans un lot accusé par le serveur central.</param>
public sealed record ChargementSync(int Numero, string Motif, string Etat, DateTime DemandeLe, int? Epoque, long? Repere,
    IReadOnlyDictionary<string, long> Lignes, long LignesTotal, long LignesAccusees, DateTime? TermineLe, long? AbsentesCentral);

/// <summary>Annonce du chargement au serveur central.</summary>
public sealed record DebutChargementSync(int Numero, int Epoque, long Repere, long ReprendreApres, string Motif, IReadOnlyList<string> Tables);

public sealed record ReponseDebutChargementSync(int Numero, long DernierRecu, long LotsAbandonnes);

public sealed record ReponseFinChargementSync(int Numero, long Absentes, IReadOnlyDictionary<string, long> AbsentesParTable);

/// <summary>Instantané reporté (base occupée, conflit de transaction) : nouvel essai au passage suivant, rien n'est perdu.</summary>
public sealed class ChargementReporteException(string message) : Exception(message);

public interface IChargementSyncDepot
{
    /// <summary>Chargement en cours, sinon le plus récent (null si aucun).</summary>
    Task<ChargementSync?> DernierAsync(CancellationToken ct = default);
    /// <summary>Demande un chargement (sync_demander_chargement) ; null si un chargement demandé est déjà en cours d'envoi.</summary>
    Task<int?> DemanderAsync(string motif, long? utilisateurId, CancellationToken ct = default);
    /// <summary>
    /// Prend l'instantané et inscrit ses lignes (état « pret ») ; <paramref name="progression"/> reçoit (tables faites, total).
    /// Base occupée : ChargementReporteException (rien n'est inscrit).
    /// </summary>
    Task ConstituerAsync(int numero, Action<int, int>? progression, CancellationToken ct = default);
    Task MarquerAnnonceAsync(int numero, long reprendreApres, CancellationToken ct = default);
    /// <summary>Toutes les lignes de l'instantané sont dans des lots accusés.</summary>
    Task<bool> ToutAccuseAsync(int numero, CancellationToken ct = default);
    Task TerminerAsync(int numero, long absentes, IReadOnlyDictionary<string, long> parTable, CancellationToken ct = default);
}

/// <summary>Appels du chargement complet au serveur central (même client, certificat et jeton que les lots).</summary>
public interface IClientChargementCentral
{
    Task<ReponseDebutChargementSync> DebuterAsync(string adresse, DebutChargementSync debut, CancellationToken ct = default);
    Task<ReponseFinChargementSync> TerminerAsync(string adresse, int numero, IReadOnlyDictionary<string, long> lignes, CancellationToken ct = default);
}

/// <summary>Progression de la constitution en cours (en mémoire : l'instantané n'est visible en base qu'à sa validation).</summary>
public sealed class SuiviChargementSync
{
    private int numero, faites, total;
    public (int Numero, int Faites, int Total) Lire() => (Volatile.Read(ref numero), Volatile.Read(ref faites), Volatile.Read(ref total));
    public void Debuter(int n) { Volatile.Write(ref faites, 0); Volatile.Write(ref total, 0); Volatile.Write(ref numero, n); }
    public void Avancer(int f, int t) { Volatile.Write(ref total, t); Volatile.Write(ref faites, f); }
    public void Finir() => Volatile.Write(ref numero, 0);
}

/// <summary>Étapes du chargement complet appelées par chaque passage d'envoi (EnvoyerLotsSync), sous VerrouSync.</summary>
public sealed class ChargementCompletSync(IChargementSyncDepot depot, IClientChargementCentral client, SuiviChargementSync suivi)
{
    /// <summary>Avant l'envoi des lots : constitue l'instantané demandé, puis l'annonce au serveur central.</summary>
    public async Task PreparerAsync(string adresse, long dernierAccuse, CancellationToken ct = default)
    {
        var c = await depot.DernierAsync(ct);
        if (c is null || !EtatsChargement.EnCours(c.Etat)) return;
        if (c.Etat is EtatsChargement.Demande or EtatsChargement.Constitution)
        {
            suivi.Debuter(c.Numero);
            try { await depot.ConstituerAsync(c.Numero, suivi.Avancer, ct); }
            finally { suivi.Finir(); }
            c = await depot.DernierAsync(ct) ?? throw new InvalidOperationException("Chargement disparu après sa constitution.");
        }
        if (c.Etat == EtatsChargement.Pret)
        {
            if (c.Epoque is not { } epoque || c.Repere is not { } repere) throw new InvalidOperationException($"Chargement {c.Numero} prêt sans repère.");
            var r = await client.DebuterAsync(adresse, new DebutChargementSync(c.Numero, epoque, repere, dernierAccuse, c.Motif,
                c.Lignes.Keys.Order(StringComparer.Ordinal).ToList()), ct);
            if (r.Numero != c.Numero) throw new ServeurCentralException($"Réponse du serveur central pour le chargement {r.Numero} au lieu de {c.Numero}.");
            await depot.MarquerAnnonceAsync(c.Numero, dernierAccuse, ct);
        }
    }

    /// <summary>Après l'envoi : fin du chargement quand toutes ses lignes sont accusées ; renvoie le chargement terminé (sinon null).</summary>
    public async Task<ReponseFinChargementSync?> TerminerSiAccuseAsync(string adresse, CancellationToken ct = default)
    {
        var c = await depot.DernierAsync(ct);
        if (c is not { Etat: EtatsChargement.Annonce } || !await depot.ToutAccuseAsync(c.Numero, ct)) return null;
        var r = await client.TerminerAsync(adresse, c.Numero, c.Lignes, ct);
        if (r.Numero != c.Numero) throw new ServeurCentralException($"Réponse du serveur central pour le chargement {r.Numero} au lieu de {c.Numero}.");
        await depot.TerminerAsync(c.Numero, r.Absentes, r.AbsentesParTable, ct);
        return r;
    }

    /// <summary>Lots perdus par le serveur central et plus disponibles ici : rechargement complet (false s'il a déjà été essayé sans succès).</summary>
    public async Task<bool> DemanderPourLotsManquantsAsync(CancellationToken ct = default)
    {
        var c = await depot.DernierAsync(ct);
        if (c is { Motif: MotifsChargement.LotsManquants, Etat: EtatsChargement.Annonce }) return false;
        return await depot.DemanderAsync(MotifsChargement.LotsManquants, null, ct) is not null;
    }
}

/// <summary>Bouton « Recharger toutes les données » (droit admin.parametres) : demande journalisée ; l'envoi la traite.</summary>
public sealed class RechargerSync(IEnvoiSyncDepot envoi, IChargementSyncDepot depot, IJournal journal)
{
    public async Task<int> ExecuterAsync(long utilisateurId, CancellationToken ct = default)
    {
        var p = await envoi.ParametresAsync(ct);
        if (p.BaseEnLectureSeule) throw new RegleMetierException("Base en lecture seule (2e serveur en veille) : rechargement impossible ici.");
        if (!p.Active) throw new RegleMetierException("Activez d'abord la synchronisation (sous-rubrique Réglages).");
        if (p.InscriteLe is null) throw new RegleMetierException("Activez d'abord ce serveur auprès du serveur central (sous-rubrique Activation).");
        var n = await depot.DemanderAsync(MotifsChargement.Demande, utilisateurId, ct)
            ?? throw new RegleMetierException("Un rechargement complet est déjà en cours d'envoi : attendez sa fin.");
        await journal.EcrireAsync(utilisateurId, "synchronisation_rechargement", "sync_chargement", n,
            new Dictionary<string, object?> { ["motif"] = MotifsChargement.Demande }, null, ct);
        return n;
    }
}
