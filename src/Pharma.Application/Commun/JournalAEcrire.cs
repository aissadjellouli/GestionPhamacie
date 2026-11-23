namespace Pharma.Application.Commun;

/// <summary>
/// Ce que la transaction d'écriture connaît quand elle inscrit une entrée de journal : la vente (identifiant et numéro) et, si
/// elle en a créé un, le numéro du bon de sortie de caisse.
/// </summary>
public sealed record ContexteJournal(long VenteId, string Numero, string? Bon = null);

/// <summary>
/// Entrée de journal obligatoire, écrite dans la transaction de l'opération qu'elle trace (jamais après la validation, avec un
/// jeton de requête qui peut être annulé) : l'opération et son journal réussissent ou échouent ensemble.
/// EnregistrementId vide : la vente de l'opération. PersonneAutorisee : employé qui a autorisé sur place (code d'un collègue).
/// </summary>
public sealed record JournalAEcrire(long UtilisateurId, string Action, string? TableCible, long? EnregistrementId,
    Func<ContexteJournal, IReadOnlyDictionary<string, object?>> Details, string? PermissionCode, long? PersonneAutorisee = null)
{
    /// <summary>Entrée dont le détail est connu d'avance.</summary>
    public static JournalAEcrire Fixe(long utilisateurId, string action, string? tableCible, long? enregistrementId,
        IReadOnlyDictionary<string, object?> details, string? permissionCode, long? personneAutorisee = null) =>
        new(utilisateurId, action, tableCible, enregistrementId, _ => details, permissionCode, personneAutorisee);
}
