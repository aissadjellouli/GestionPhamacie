using Pharma.Domain.Catalogue;
using Pharma.Domain.Stock;
using Pharma.Domain.Ventes;

namespace Pharma.Domain.Commun;

/// <summary>
/// Codes texte des statuts, identiques aux valeurs autorisées par les contraintes CHECK de la base.
/// </summary>
public static class CodesMetier
{
    private static readonly Dictionary<ClasseReglementaire, string> Classes = new()
    {
        [ClasseReglementaire.Libre] = "libre",
        [ClasseReglementaire.ListeI] = "liste_i",
        [ClasseReglementaire.ListeII] = "liste_ii",
        [ClasseReglementaire.Psychotrope] = "psychotrope",
        [ClasseReglementaire.Stupefiant] = "stupefiant"
    };

    private static readonly Dictionary<StatutBoite, string> StatutsBoite = new()
    {
        [StatutBoite.EnStock] = "en_stock",
        [StatutBoite.Vendue] = "vendue",
        [StatutBoite.RetourneeFournisseur] = "retournee_fournisseur",
        [StatutBoite.Detruite] = "detruite",
        [StatutBoite.RetourClient] = "retour_client"
    };

    private static readonly Dictionary<TypeVente, string> TypesVente = new()
    {
        [TypeVente.Libre] = "libre",
        [TypeVente.TiersPayant] = "tiers_payant"
    };

    private static readonly Dictionary<StatutVente, string> StatutsVente = new()
    {
        [StatutVente.EnCours] = "en_cours",
        [StatutVente.Validee] = "validee",
        [StatutVente.Annulee] = "annulee"
    };

    private static readonly Dictionary<ModePaiement, string> ModesPaiement = new()
    {
        [ModePaiement.Especes] = "especes",
        [ModePaiement.Carte] = "carte",
        [ModePaiement.Cheque] = "cheque",
        [ModePaiement.Credit] = "credit"
    };

    private static readonly Dictionary<Achats.MotifRetour, string> MotifsRetour = new()
    {
        [Achats.MotifRetour.PeremptionProche] = "peremption_proche",
        [Achats.MotifRetour.Perime] = "perime",
        [Achats.MotifRetour.Defaut] = "defaut",
        [Achats.MotifRetour.ErreurLivraison] = "erreur_livraison",
        [Achats.MotifRetour.RappelLot] = "rappel_lot"
    };

    private static readonly Dictionary<Achats.StatutRetour, string> StatutsRetour = new()
    {
        [Achats.StatutRetour.Brouillon] = "brouillon",
        [Achats.StatutRetour.Envoye] = "envoye",
        [Achats.StatutRetour.AvoirRecu] = "avoir_recu",
        [Achats.StatutRetour.Refuse] = "refuse"
    };

    public static string Code(this Achats.MotifRetour v) => MotifsRetour[v];
    public static Achats.MotifRetour MotifRetourDepuisCode(string code) => Depuis(MotifsRetour, code);
    public static string Code(this Achats.StatutRetour v) => StatutsRetour[v];
    public static Achats.StatutRetour StatutRetourDepuisCode(string code) => Depuis(StatutsRetour, code);

    public static string Code(this ModePaiement v) => ModesPaiement[v];
    public static ModePaiement ModePaiementDepuisCode(string code) => Depuis(ModesPaiement, code);

    public static string Code(this ClasseReglementaire v) => Classes[v];
    public static string Code(this StatutBoite v) => StatutsBoite[v];
    public static string Code(this TypeVente v) => TypesVente[v];
    public static string Code(this StatutVente v) => StatutsVente[v];

    public static ClasseReglementaire ClasseDepuisCode(string code) => Depuis(Classes, code);
    public static StatutBoite StatutBoiteDepuisCode(string code) => Depuis(StatutsBoite, code);
    public static TypeVente TypeVenteDepuisCode(string code) => Depuis(TypesVente, code);
    public static StatutVente StatutVenteDepuisCode(string code) => Depuis(StatutsVente, code);

    private static T Depuis<T>(Dictionary<T, string> table, string code) where T : struct, Enum =>
        table.FirstOrDefault(p => p.Value == code) is { Value: not null } p
            ? p.Key
            : throw new ArgumentException($"Code {typeof(T).Name} inconnu : {code}", nameof(code));
}
