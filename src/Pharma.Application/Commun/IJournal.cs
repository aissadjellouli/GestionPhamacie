namespace Pharma.Application.Commun;

/// <summary>Journal des actions sensibles (table journal_audit, non modifiable).</summary>
public interface IJournal
{
    Task EcrireAsync(long utilisateurId, string action, string? tableCible, long? enregistrementId,
        IReadOnlyDictionary<string, object?> details, string? permissionCode, CancellationToken ct = default);
}

public sealed record EntreeJournal(
    DateTime Date, string Employe, string Action, string? DetailsJson, string? AutorisePar, string? Permission, long? EmployeId = null, long? AutoriseParId = null);

/// <summary>Filtres du journal : période (dates incluses), employé (partie du nom), catégorie d'actions (CategoriesJournal).</summary>
public sealed record FiltreJournal(DateOnly? Du, DateOnly? Au, string? Employe, IReadOnlyList<string>? Actions = null);

/// <summary>Catégories du journal : modifications (ancienne et nouvelle valeur) ; autorisations par code et blocages.</summary>
public static class CategoriesJournal
{
    public static readonly IReadOnlyDictionary<string, string[]> Actions = new Dictionary<string, string[]>
    {
        ["modifications"] =
        [
            "prix_modifie", "produit_modifie", "produit_cree", "patient_cree", "patient_modifie", "coordonnees_modifiees", "utilisateur_cree",
            "utilisateur_modifie", "droits_modifies", "code_comptoir", "pharmacie_modifiee", "parametres_caisse", "seuils_alerte", "seuil_stock",
            "seuils_inventaire", "tva_sorties", "delai_alerte_dettes", "reglages_surveillance", "reglages_notification", "reglages_sauvegarde",
            "destinataire_notification", "produit_bloque", "produit_debloque", "produit_sensible", "produit_non_sensible", "emplacement", "produit_range"
        ],
        ["autorisations"] = ["autorisation_code", "code_comptoir_bloque", "connexion_bloquee"]
    };
}

public interface IJournalLecture
{
    Task<IReadOnlyList<EntreeJournal>> DernieresAsync(int nombre, CancellationToken ct = default);
    /// <summary>Une page du journal, de la plus récente à la plus ancienne, avec le total des actions qui répondent aux filtres.</summary>
    Task<Page<EntreeJournal>> PageAsync(FiltreJournal filtre, DemandePage page, CancellationToken ct = default);
    /// <summary>Employés présents dans le journal (auteurs et personnes qui ont autorisé), pour préremplir le filtre « Employé ».</summary>
    Task<IReadOnlyList<string>> EmployesAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<string>>([]);
}
