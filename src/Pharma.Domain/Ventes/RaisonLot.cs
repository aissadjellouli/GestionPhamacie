namespace Pharma.Domain.Ventes;

/// <summary>
/// Pourquoi un lot plus récent est servi alors qu'un lot plus ancien du même produit est en stock (reçu avant, ou périmant avant).
/// « peremption » n'est jamais choisie par le vendeur : le logiciel la vérifie sur les dates enregistrées (le lot servi périme
/// avant les autres), le vendeur la valide. Les autres raisons sont choisies par le vendeur.
/// </summary>
public static class RaisonLot
{
    public const string Peremption = "peremption";
    public const string Introuvable = "introuvable";
    public const string Abimee = "abimee";
    public const string DemandeClient = "demande_client";

    /// <summary>Raisons au choix du vendeur.</summary>
    public static readonly IReadOnlySet<string> Manuelles = new HashSet<string> { Introuvable, Abimee, DemandeClient };

    public static string Libelle(string code) => code switch
    {
        Peremption => "péremption plus proche",
        Introuvable => "boîte de l’ancien lot introuvable",
        Abimee => "boîte de l’ancien lot abîmée",
        DemandeClient => "demande du client",
        _ => code
    };
}

/// <summary>Ce que la caisse doit demander pour un lot servi : rien, la validation de la raison vérifiée, ou le choix d'une raison.</summary>
public static class SituationLot
{
    public const string Aucune = "aucune";
    public const string Peremption = "peremption";
    public const string Obligatoire = "obligatoire";

    /// <summary>
    /// Raison retenue pour le lot, selon sa situation et le choix du vendeur. exiger : à l'encaissement (raison obligatoire) ;
    /// sinon (aperçu), une raison manquante n'est pas une erreur. Lève RegleMetier si la raison manque ou n'est pas permise.
    /// </summary>
    public static string? Raison(string situation, string? choix, string produit, string lot, string? lotDelaisse, bool exiger)
    {
        if (situation == Aucune) return null;
        if (choix is not null && choix != RaisonLot.Peremption && !RaisonLot.Manuelles.Contains(choix))
            throw new Commun.RegleMetierException($"{produit}, lot {lot} : raison inconnue.", "raison_lot");
        if (choix == RaisonLot.Peremption && situation != Peremption)
            throw new Commun.RegleMetierException(
                $"{produit}, lot {lot} : la péremption plus proche n’est pas vérifiée par le logiciel (lot {lotDelaisse} plus ancien). Choisissez une autre raison.", "raison_lot");
        if (choix is null && exiger)
            throw new Commun.RegleMetierException(situation == Peremption
                ? $"{produit}, lot {lot} : validez la raison « péremption plus proche » (lot {lotDelaisse} plus ancien en stock)."
                : $"{produit}, lot {lot} : un lot plus ancien ({lotDelaisse}) est en stock. Indiquez pourquoi vous servez ce lot.", "raison_lot");
        return choix;
    }
}
