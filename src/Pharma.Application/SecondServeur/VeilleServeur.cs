namespace Pharma.Application.SecondServeur;

// ---- Bascule du 2e serveur, vague 2, lot V2-1 : API en « veille » (2026-11-23) ------------------------------------------------
// docs/taches/2026-11-23-bascule-2e-serveur/plan-vague-2.md § 2 et contrat-api.md. Sur le 2e serveur (base en recovery), l'API démarre
// au lieu d'être refusée, mais seulement si un 2e serveur est déclaré (DeuxServeursDeclares) : sans lui, le refus de démarrage reste.

public static class ModeVeille
{
    /// <summary>Code de sortie du processus quand la veille s'arrête pour redémarrer en principal (le service Windows le relance).</summary>
    public const int CodeSortieRedemarrage = 10;

    /// <summary>Vrai si l'API démarre en veille : 2e serveur déclaré, base en lecture seule (recovery), hors démonstration.</summary>
    public static bool AuDemarrage(bool deuxServeursDeclares, bool? baseEnRecovery, bool demoActive) =>
        deuxServeursDeclares && baseEnRecovery == true && !demoActive;
}

/// <summary>
/// État « veille » de cette API (singleton). Point d'accroche des lots suivants : V2-4 appelle <see cref="DemanderRedemarrage"/> juste après
/// pg_promote() ; la tâche de surveillance l'appelle quand elle voit la base sortie de recovery (promotion par le script manuel).
/// </summary>
public interface IVeilleServeur
{
    bool EstVeille { get; }
    bool RedemarrageDemande { get; }
    string? MotifRedemarrage { get; }
    /// <summary>Levé une seule fois, sur le fil de l'appelant : l'hôte doit s'arrêter avec <see cref="ModeVeille.CodeSortieRedemarrage"/>.</summary>
    event Action<string>? RedemarrageDemandeLeve;
    /// <summary>Sans effet hors veille ou si déjà demandé.</summary>
    void DemanderRedemarrage(string motif);
}

public sealed class VeilleServeur(bool estVeille) : IVeilleServeur
{
    private readonly object _verrou = new();
    private string? _motif;

    public bool EstVeille { get; } = estVeille;
    public bool RedemarrageDemande { get { lock (_verrou) return _motif is not null; } }
    public string? MotifRedemarrage { get { lock (_verrou) return _motif; } }
    public event Action<string>? RedemarrageDemandeLeve;

    public void DemanderRedemarrage(string motif)
    {
        if (!EstVeille) return;
        lock (_verrou) { if (_motif is not null) return; _motif = motif; }
        RedemarrageDemandeLeve?.Invoke(motif);
    }
}

/// <summary>Promotion vue sur la base locale : deux relevés successifs « plus en recovery » (un seul relevé ne décide jamais).</summary>
public sealed class SuiviPromotionVeille
{
    private bool _dejaVu;

    /// <summary>Vrai dès le 2e relevé consécutif montrant une base en écriture. Relevé illisible (null) ou en recovery : on repart de zéro.</summary>
    public bool Observer(ReleveServeur? local)
    {
        if (local is { EnRecovery: false })
        {
            if (_dejaVu) return true;
            _dejaVu = true;
            return false;
        }
        _dejaVu = false;
        return false;
    }
}
