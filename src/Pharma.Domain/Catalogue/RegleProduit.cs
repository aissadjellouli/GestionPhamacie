using Pharma.Domain.Commun;

namespace Pharma.Domain.Catalogue;

/// <summary>Fiche d'un produit créé à la main (absent de la nomenclature chargée).</summary>
public sealed record FicheProduit(
    string NomCommercial, string Dci, string Dosage, string Forme, decimal PrixPublic, ClasseReglementaire Classe,
    string? CodeBarre, decimal? TarifReference, bool Remboursable, decimal TauxTva, int? StockMinimum,
    string? NumEnregistrement = null, string Categorie = "medicament", string? ListePsychotrope = null);

public static class RegleProduit
{
    /// <summary>
    /// Contrôle et normalise la fiche : espaces retirés, code-barres EAN-13 (un GTIN-14 commençant par 0 est ramené à 13 chiffres).
    /// Obligatoires : nom, DCI, dosage, forme, prix public, classe.
    /// </summary>
    public static FicheProduit Normaliser(FicheProduit f)
    {
        static string Requis(string? valeur, string champ) =>
            string.IsNullOrWhiteSpace(valeur) ? throw new RegleMetierException($"{champ} obligatoire.") : valeur.Trim();

        var nom = Requis(f.NomCommercial, "Nom commercial");
        if (nom.Length > 120) throw new RegleMetierException("Nom commercial : 120 caractères au plus.", "nom_commercial");
        var dci = Requis(f.Dci, "DCI");
        var dosage = Requis(f.Dosage, "Dosage");
        var forme = Requis(f.Forme, "Forme");
        if (f.PrixPublic <= 0) throw new RegleMetierException("Le prix public doit être supérieur à 0.", "prix_public");
        if (f.TarifReference is <= 0) throw new RegleMetierException("Le tarif de référence doit être supérieur à 0.", "tarif_reference");
        if (f.Remboursable && f.TarifReference is null) throw new RegleMetierException("Un produit remboursable a besoin d’un tarif de référence.", "tarif_reference");
        if (f.TauxTva is < 0 or > 100) throw new RegleMetierException("Taux de TVA entre 0 et 100 %.", "tva");
        if (f.StockMinimum is < 0 or > 100_000) throw new RegleMetierException("Le seuil d’alerte doit être entre 0 et 100 000 boîtes (vide : automatique).", "stock_minimum");

        string? code = null;
        if (!string.IsNullOrWhiteSpace(f.CodeBarre))
        {
            code = f.CodeBarre.Trim();
            if (code.Length == 14 && code[0] == '0') code = code[1..];   // GTIN-14 du Data Matrix
            if (!Ean13.EstValide(code)) throw new RegleMetierException("Code-barres invalide : 13 chiffres avec une clé de contrôle correcte.", "code_barre");
            if (Ean13.Parse(code).EstInterne) throw new RegleMetierException("Les codes commençant par 20 sont réservés aux étiquettes internes de l’officine.");
        }

        var num = string.IsNullOrWhiteSpace(f.NumEnregistrement) ? null : f.NumEnregistrement.Trim();
        var liste = f.Classe == ClasseReglementaire.Psychotrope && !string.IsNullOrWhiteSpace(f.ListePsychotrope) ? f.ListePsychotrope.Trim() : null;
        if (liste is not (null or "liste_i" or "liste_ii")) throw new RegleMetierException("Liste d'un psychotrope : liste I ou liste II.", "liste_psychotrope");
        var categorie = string.IsNullOrWhiteSpace(f.Categorie) ? "medicament" : f.Categorie.Trim();
        return f with { NomCommercial = nom, Dci = dci, Dosage = dosage, Forme = forme, CodeBarre = code, NumEnregistrement = num,
            Categorie = categorie, ListePsychotrope = liste };
    }

    /// <summary>
    /// Classe imposée quand un autre que le titulaire crée ou modifie un produit dont la DCI correspond à une
    /// substance surveillée, ou dont le n° d'enregistrement est classé sous registre par la nomenclature nationale
    /// (<paramref name="classeSurveillee"/> : psychotrope ou stupefiant, voir <see cref="ClasseSurveilleeRetenue"/>) alors que
    /// la classe choisie n'est pas soumise au registre : la classe de la substance. Null sinon (titulaire, DCI non surveillée,
    /// classe déjà sous registre). Le produit est alors « à vérifier » : invendable jusqu'à la confirmation du titulaire.
    /// </summary>
    public static ClasseReglementaire? ClasseImposee(ClasseReglementaire choisie, string? classeSurveillee, bool titulaire)
    {
        if (titulaire || classeSurveillee is not ("psychotrope" or "stupefiant")) return null;
        if (choisie is ClasseReglementaire.Psychotrope or ClasseReglementaire.Stupefiant) return null;
        return CodesMetier.ClasseDepuisCode(classeSurveillee);
    }

    /// <summary>
    /// Classe sous registre retenue entre celle de la substance surveillée qui correspond à la DCI (<paramref name="parDci"/>) et celle
    /// que propose la nomenclature nationale pour le n° d'enregistrement du produit (<paramref name="parNomenclature"/>) : la plus
    /// stricte (stupéfiant avant psychotrope), null si aucune des deux n'est sous registre.
    /// </summary>
    public static string? ClasseSurveilleeRetenue(string? parDci, string? parNomenclature) =>
        parDci == "stupefiant" || parNomenclature == "stupefiant" ? "stupefiant"
        : parDci == "psychotrope" || parNomenclature == "psychotrope" ? "psychotrope"
        : null;

    /// <summary>
    /// Message au créateur (ou à l'auteur de la modification) quand la classe a été imposée. <paramref name="numEnregistrement"/> : la
    /// classe vient de la nomenclature nationale (n° d'enregistrement du produit), et non de la DCI saisie.
    /// </summary>
    public static string MessageClasseImposee(string dci, ClasseReglementaire classe, string? numEnregistrement = null)
    {
        var libelle = classe == ClasseReglementaire.Stupefiant ? "stupéfiant" : "psychotrope";
        var origine = numEnregistrement is null
            ? $"La DCI « {dci} » correspond à une substance classée {libelle}"
            : $"Le n° d’enregistrement {numEnregistrement} correspond, dans la nomenclature nationale, à un produit classé {libelle}";
        return $"{origine} : le produit est enregistré en classe « {libelle} » "
            + "(registre obligatoire) et reste invendable jusqu’à ce que le titulaire confirme sa classe "
            + "(Administration › Nomenclature nationale, « Produits à vérifier »).";
    }
}
