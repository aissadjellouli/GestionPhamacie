using Pharma.Domain.Commun;

namespace Pharma.Domain.Catalogue;

/// <summary>Fiche d'un produit créé à la main (absent de la nomenclature chargée).</summary>
public sealed record FicheProduit(
    string NomCommercial, string Dci, string Dosage, string Forme, decimal PrixPublic, ClasseReglementaire Classe,
    string? CodeBarre, decimal? TarifReference, bool Remboursable, decimal TauxTva, int StockMinimum);

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
        if (nom.Length > 120) throw new RegleMetierException("Nom commercial : 120 caractères au plus.");
        var dci = Requis(f.Dci, "DCI");
        var dosage = Requis(f.Dosage, "Dosage");
        var forme = Requis(f.Forme, "Forme");
        if (f.PrixPublic <= 0) throw new RegleMetierException("Le prix public doit être supérieur à 0.");
        if (f.TarifReference is <= 0) throw new RegleMetierException("Le tarif de référence doit être supérieur à 0.");
        if (f.Remboursable && f.TarifReference is null) throw new RegleMetierException("Un produit remboursable a besoin d’un tarif de référence.");
        if (f.TauxTva is < 0 or > 100) throw new RegleMetierException("Taux de TVA entre 0 et 100 %.");
        if (f.StockMinimum < 0) throw new RegleMetierException("Le stock minimum ne peut pas être négatif.");

        string? code = null;
        if (!string.IsNullOrWhiteSpace(f.CodeBarre))
        {
            code = f.CodeBarre.Trim();
            if (code.Length == 14 && code[0] == '0') code = code[1..];   // GTIN-14 du Data Matrix
            if (!Ean13.EstValide(code)) throw new RegleMetierException("Code-barres invalide : 13 chiffres avec une clé de contrôle correcte.");
            if (Ean13.Parse(code).EstInterne) throw new RegleMetierException("Les codes commençant par 20 sont réservés aux étiquettes internes de l’officine.");
        }

        return f with { NomCommercial = nom, Dci = dci, Dosage = dosage, Forme = forme, CodeBarre = code };
    }
}
