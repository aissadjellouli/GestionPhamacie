using Pharma.Domain.Commun;

namespace Pharma.Domain.Caisse;

/// <summary>
/// Détail saisi avec une sortie ou une entrée d'argent par le tiroir (F8, script 096). RegularisationVenteId : remboursement du trop-payé
/// de la régularisation Chifa de cette vente (script 099).
/// </summary>
public sealed record SaisieTiroir(decimal? Montant, string? Categorie = null, string? Libelle = null, long? FournisseurId = null,
    string? Beneficiaire = null, string? Reference = null, long? PatientId = null, string? Raison = null, long? RegularisationVenteId = null);

/// <summary>
/// Régularisation Chifa vue pour le remboursement de son trop-payé : vente, patient, montant à rendre, active (non annulée), bon du tiroir
/// qui l'a déjà rendu (non corrigé), null sinon.
/// </summary>
public sealed record RegularisationARendre(long Id, long VenteId, string Vente, long? PatientId, string? Patient, decimal ARendre, bool Active, string? BonRendu);

/// <summary>
/// Sorties et entrées d'argent par le tiroir (règle du titulaire, 2026-11-15) : chaque motif du tiroir porte un effet (aucun, sortie,
/// entrée) et le détail exigé. Montant obligatoire et strictement positif pour un motif à effet, interdit sinon ; une sortie ne dépasse
/// pas les espèces attendues dans le tiroir.
/// </summary>
public static class RegleTiroir
{
    public const string Aucun = "aucun";
    public const string Sortie = "sortie";
    public const string Entree = "entree";

    public const string Depense = "depense";
    public const string Fournisseur = "fournisseur";
    public const string Patient = "patient";
    public const string Coffre = "coffre";
    public const string Fond = "fond";

    public const int TexteMax = 200;

    /// <summary>Détails possibles pour chaque effet (contrainte ck_motif_reglable_effet).</summary>
    public static readonly IReadOnlyList<(string Code, string Libelle)> Details =
    [
        (Depense, "Petite dépense (catégorie de charge et libellé)"),
        (Fournisseur, "Paiement d’un livreur ou d’un fournisseur (grossiste et n° de BL/facture, ou nom)"),
        (Patient, "Remboursement d’un client (patient et raison)"),
        (Coffre, "Retrait vers le coffre"),
        (Fond, "Remise d’un fonds de caisse (du coffre au tiroir)")
    ];

    /// <summary>Effet et détail cohérents : aucun/aucun, sortie avec dépense, fournisseur, patient ou coffre, entrée avec fonds.</summary>
    public static (string Effet, string Detail) VerifierEffet(string? effet, string? detail)
    {
        var e = string.IsNullOrWhiteSpace(effet) ? Aucun : effet.Trim();
        var d = string.IsNullOrWhiteSpace(detail) ? Aucun : detail.Trim();
        var ok = (e, d) switch
        {
            (Aucun, Aucun) => true,
            (Sortie, Depense or Fournisseur or Patient or Coffre) => true,
            (Entree, Fond) => true,
            _ => false
        };
        return ok ? (e, d) : throw new RegleMetierException("Effet du motif incohérent : une sortie demande une dépense, un fournisseur, un patient ou le coffre ; une entrée, un fonds de caisse.", "effet");
    }

    /// <summary>Montant : obligatoire et &gt; 0 pour un motif à effet ; aucun montant pour les autres.</summary>
    public static decimal? VerifierMontant(string effet, decimal? montant)
    {
        if (effet == Aucun)
            return montant is > 0 ? throw new RegleMetierException("Ce motif ne porte pas de montant.", "montant") : null;
        if (montant is not > 0) throw new RegleMetierException("Indiquez le montant (supérieur à 0).", "montant");
        if (decimal.Round(montant.Value, 2) != montant.Value) throw new RegleMetierException("Montant : au centime près.", "montant");
        return montant;
    }

    /// <summary>Refus d'une sortie trop forte sans révéler l'attendu (comptage à l'aveugle, ou employé sans caisse.voir_ecart).</summary>
    public const string MessageEspecesInsuffisantes = "Montant supérieur aux espèces disponibles dans le tiroir.";

    /// <summary>
    /// Une sortie ne dépasse pas les espèces attendues dans le tiroir (fond et espèces de la session). L'attendu n'est cité que si
    /// <paramref name="montrerAttendu"/> (caisse hors comptage à l'aveugle et employé qui voit les écarts, recette du 2026-11-15, D2) :
    /// sinon message neutre, l'employé ne doit pas connaître l'attendu avant sa relève.
    /// </summary>
    public static void VerifierDansTiroir(decimal montant, decimal dansTiroir, bool montrerAttendu = false)
    {
        if (montant > dansTiroir)
            throw new RegleMetierException(montrerAttendu
                ? $"Le tiroir ne contient en principe que {dansTiroir.ToString("N2", System.Globalization.CultureInfo.GetCultureInfo("fr-FR"))} DA."
                : MessageEspecesInsuffisantes, "montant");
    }

    /// <summary>
    /// Remboursement du trop-payé d'une régularisation Chifa par le tiroir (recette du 2026-11-15, D4, script 099) : régularisation
    /// « rendu au patient » encore active, montant « à rendre » positif, pas déjà rendu par un bon non corrigé, même patient, montant
    /// égal au montant à rendre.
    /// </summary>
    public static void VerifierRemboursementRegularisation(RegularisationARendre r, decimal montant, long? patientId)
    {
        if (!r.Active || r.ARendre <= 0)
            throw new RegleMetierException($"La régularisation Chifa de la vente {r.Vente} n’a rien à rendre au patient.", "regularisation");
        if (r.BonRendu is { } bon)
            throw new RegleMetierException($"Le trop-payé de la vente {r.Vente} est déjà rendu (bon {bon}).", "regularisation");
        if (r.PatientId is null || r.PatientId != patientId)
            throw new RegleMetierException($"Le trop-payé de la vente {r.Vente} se rend au patient de la vente.", "patient");
        if (montant != r.ARendre)
            throw new RegleMetierException($"Le montant à rendre pour la vente {r.Vente} est de {r.ARendre.ToString("N2", System.Globalization.CultureInfo.GetCultureInfo("fr-FR"))} DA.", "montant");
    }

    /// <summary>Raison pré-remplie du remboursement : « Régularisation Chifa T-… ».</summary>
    public static string RaisonRegularisation(string vente) => $"Régularisation Chifa {vente}";

    /// <summary>Au-delà du seuil (Administration › Caisse) : validation d'un ayant droit. Un seuil de 0 fait valider toute sortie.</summary>
    public static bool ValidationRequise(string effet, decimal montant, decimal seuil) => effet == Sortie && montant > seuil;

    /// <summary>Détail exigé selon le motif ; rend la saisie nettoyée (textes rognés, champs hors détail retirés).</summary>
    public static SaisieTiroir VerifierDetail(string detail, SaisieTiroir s)
    {
        static string? T(string? x) => string.IsNullOrWhiteSpace(x) ? null : x.Trim();
        static string Exiger(string? x, string message, string champ)
        {
            var v = T(x) ?? throw new RegleMetierException(message, champ);
            return v.Length > TexteMax ? throw new RegleMetierException($"{TexteMax} caractères au plus.", champ) : v;
        }
        switch (detail)
        {
            case Depense:
                if (T(s.Categorie) is null) throw new RegleMetierException("Choisissez la catégorie de la dépense.", "categorie");
                return new SaisieTiroir(s.Montant, T(s.Categorie), Exiger(s.Libelle, "Indiquez le libellé de la dépense.", "libelle"));
            case Fournisseur:
                if (s.FournisseurId is > 0)
                    return new SaisieTiroir(s.Montant, FournisseurId: s.FournisseurId,
                        Reference: Exiger(s.Reference, "Indiquez le n° de BL ou de facture du grossiste.", "reference"));
                return new SaisieTiroir(s.Montant, Beneficiaire: Exiger(s.Beneficiaire, "Choisissez le grossiste ou indiquez le nom du livreur ou du fournisseur.", "beneficiaire"),
                    Reference: T(s.Reference) is { Length: > TexteMax } ? throw new RegleMetierException($"{TexteMax} caractères au plus.", "reference") : T(s.Reference));
            case Patient:
                if (s.PatientId is not > 0) throw new RegleMetierException("Choisissez le patient remboursé.", "patient");
                return new SaisieTiroir(s.Montant, PatientId: s.PatientId, Raison: Exiger(s.Raison, "Indiquez la raison du remboursement.", "raison"),
                    RegularisationVenteId: s.RegularisationVenteId is > 0 ? s.RegularisationVenteId : null);
            case Coffre:
            case Fond:
                return new SaisieTiroir(s.Montant);
            default:
                return new SaisieTiroir(null);
        }
    }
}
