namespace Pharma.Domain.Catalogue;

public class Produit
{
    public long Id { get; init; }
    public string? NumEnregistrement { get; init; }
    public string? CodeBarre { get; init; }
    public string NomCommercial { get; init; } = "";
    public long? DciId { get; init; }
    public Dci? Dci { get; init; }
    public string? Dosage { get; init; }
    public string? Forme { get; init; }
    public decimal PrixPublic { get; init; }
    public decimal? TarifReference { get; init; }
    public bool Remboursable { get; init; }
    public ClasseReglementaire Classe { get; init; } = ClasseReglementaire.Libre;
    /// <summary>Seuil d'alerte saisi (boîtes) ; null : seuil automatique.</summary>
    public int? StockMinimum { get; init; }
    public bool Actif { get; init; } = true;
    /// <summary>Repris de l'ancien logiciel sans n° d'enregistrement : classe à confirmer par le titulaire, non vendable d'ici là (037).</summary>
    public bool AVerifier { get; init; }
    /// <summary>Liste I ou II d'un psychotrope (liste_i, liste_ii), pour son SHP ; null : à renseigner.</summary>
    public string? ListePsychotrope { get; init; }
    /// <summary>Catégorie (categorie_produit) : médicament, dermo-cosmétique, hygiène…</summary>
    public string Categorie { get; init; } = "medicament";
    /// <summary>Catégorie comptée dans les totaux « médicaments » (sinon « autres produits »).</summary>
    public bool EstMedicament { get; init; } = true;
    /// <summary>Bloqué à la vente par le titulaire ou l'assistant (046) ; MotifBlocage : la raison.</summary>
    public bool BloqueVente { get; init; }
    public string? MotifBlocage { get; init; }

    /// <summary>Supplément d'honoraire du pharmacien, par boîte, selon la liste du produit.</summary>
    public decimal ShpParBoite => Achats.RegleShp.ParBoite(Classe, ListePsychotrope);

    /// <summary>Psychotropes et stupéfiants : inscription au registre obligatoire.</summary>
    public bool EstSousRegistre =>
        Classe is ClasseReglementaire.Psychotrope or ClasseReglementaire.Stupefiant;

    /// <summary>Médicament de liste I ou II : vendu sur ordonnance, sauf par un employé qui a le droit de le vendre sans.</summary>
    public bool EstSurOrdonnance => Classe is ClasseReglementaire.ListeI or ClasseReglementaire.ListeII;
}
