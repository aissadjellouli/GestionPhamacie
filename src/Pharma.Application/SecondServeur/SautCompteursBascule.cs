namespace Pharma.Application.SecondServeur;

/// <summary>Résultat du saut des compteurs à la promotion : timeline notée, saut fait (faux : déjà fait pour cette timeline, ou première note), nombre de compteurs sautés.</summary>
public sealed record ResultatSautCompteurs(int Timeline, bool SautFait, int CompteursSautes);

/// <summary>
/// Bascule du 2e serveur, lot 7 (script 164, décision Q13) : à la promotion, chaque compteur visible saute une réserve (tickets +100 000,
/// factures +1 000, avoirs +1 000, bons de commande +1 000) pour qu'aucun n° n'existe sur les deux serveurs. Une seule fois par
/// timeline (le journal de la base le garantit, même après un redémarrage).
/// </summary>
public interface ISautCompteursBascule
{
    /// <summary>Lit la timeline de la base, saute les compteurs si elle est plus haute que la dernière notée, sinon ne fait rien.</summary>
    Task<ResultatSautCompteurs> AppliquerAsync(CancellationToken ct = default);
}

/// <summary>Suite à donner au démarrage de l'API quand le saut des compteurs a échoué.</summary>
public enum SuiteSautCompteurs
{
    /// <summary>Le démarrage continue (avertissement au journal).</summary>
    Poursuivre,
    /// <summary>Le démarrage est refusé : aucune écriture ne doit partir avec des compteurs non sautés.</summary>
    Refuser
}

public static class SautCompteursBascule
{
    /// <summary>Fonction ou table absente : le script 164 n'est pas encore passé sur cette base (base pas à jour, rien à sauter).</summary>
    public const string FonctionAbsente = "42883";
    public const string TableAbsente = "42P01";
    /// <summary>Base en lecture seule (2e serveur pas promu, ou serveur écarté) : aucune écriture n'est possible, donc aucun n° ne peut sortir.</summary>
    public const string LectureSeule = "25006";

    /// <summary>
    /// Le saut n'est appelé que si un 2e serveur est déclaré (une restauration peut faire monter la timeline d'une base seule : le saut
    /// reste alors celui du script 151), hors démonstration et sur une base en écriture.
    /// </summary>
    public static bool Applicable(bool deuxServeursDeclares, bool demoActive, bool? baseEnRecovery) =>
        deuxServeursDeclares && !demoActive && baseEnRecovery != true;

    /// <summary>
    /// Échec du saut. Base injoignable, script 164 absent ou base en lecture seule : le démarrage continue (rien ne peut être écrit sans
    /// ces conditions, et l'API refuse déjà de démarrer sur un 2e serveur non promu). Toute autre erreur : refus, pour ne jamais
    /// accepter d'écriture avec des compteurs qui auraient dû sauter.
    /// </summary>
    public static SuiteSautCompteurs Decider(string? sqlState, bool baseInjoignable) =>
        baseInjoignable || sqlState is FonctionAbsente or TableAbsente or LectureSeule ? SuiteSautCompteurs.Poursuivre : SuiteSautCompteurs.Refuser;
}
