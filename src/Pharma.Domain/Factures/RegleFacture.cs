using Pharma.Domain.Commun;

namespace Pharma.Domain.Factures;

public enum Destinataire { Patient, Entreprise, Organisme }

/// <summary>Ligne prête à facturer. Part : patient, organisme ou libre. Montant TTC (négatif pour une remise).</summary>
public sealed record LigneFacture(long? VenteId, long? LigneVenteId, string Part, string Designation, decimal Quantite, decimal PrixUnitaireTtc,
    decimal TauxTva, decimal MontantTtc);

/// <summary>Ligne de vente facturable (quantité nette des retours, parts au prorata).</summary>
public sealed record LigneVendue(long VenteId, string NumeroVente, long LigneVenteId, string Designation, decimal TauxTva, decimal Quantite,
    decimal MontantPatient, decimal MontantOrganisme, decimal RemiseVente);

public sealed record Totaux(decimal Ht, decimal Tva, decimal Ttc);

public static class RegleFacture
{
    public static Destinataire Lire(string? code) => code switch
    {
        "patient" => Destinataire.Patient,
        "entreprise" => Destinataire.Entreprise,
        "organisme" => Destinataire.Organisme,
        _ => throw new RegleMetierException("Destinataire inconnu : patient, entreprise ou organisme.")
    };

    public static string Code(this Destinataire d) => d switch
    {
        Destinataire.Patient => "patient",
        Destinataire.Entreprise => "entreprise",
        _ => "organisme"
    };

    /// <summary>F-2026-0001 (facture) ou AV-2026-0001 (avoir).</summary>
    public static string Numero(bool avoir, int annee, int sequence) => $"{(avoir ? "AV" : "F")}-{annee}-{sequence:0000}";

    /// <summary>Part facturée : l'organisme reçoit sa part prise en charge ; patient et entreprise, la part payée par le patient.</summary>
    public static string Part(Destinataire d) => d == Destinataire.Organisme ? "organisme" : "patient";

    /// <summary>
    /// Lignes d'une facture à partir de ventes : une ligne par produit (part concernée, quantité nette) ;
    /// pour la part patient, la remise de chaque vente devient une ligne négative.
    /// </summary>
    public static IReadOnlyList<LigneFacture> DepuisVentes(Destinataire d, IEnumerable<LigneVendue> lignes)
    {
        var part = Part(d);
        var resultat = new List<LigneFacture>();
        foreach (var vente in lignes.GroupBy(l => (l.VenteId, l.NumeroVente)).OrderBy(g => g.Key.NumeroVente, StringComparer.Ordinal))
        {
            foreach (var l in vente.Where(l => l.Quantite > 0))
            {
                var montant = part == "organisme" ? l.MontantOrganisme : l.MontantPatient;
                if (montant <= 0) continue;
                resultat.Add(new LigneFacture(l.VenteId, l.LigneVenteId, part, $"{l.Designation} (vente {vente.Key.NumeroVente})", l.Quantite,
                    decimal.Round(montant / l.Quantite, 2), l.TauxTva, montant));
            }
            var remise = vente.First().RemiseVente;
            if (part == "patient" && remise > 0 && resultat.Any(r => r.VenteId == vente.Key.VenteId))
                resultat.Add(new LigneFacture(vente.Key.VenteId, null, part, $"Remise (vente {vente.Key.NumeroVente})", 1, -remise, 0, -remise));
        }
        return resultat;
    }

    /// <summary>Ligne saisie à la main : désignation, quantité et prix positifs, TVA 0 à 100 %.</summary>
    public static LigneFacture Libre(string? designation, decimal quantite, decimal prixTtc, decimal tauxTva)
    {
        if (string.IsNullOrWhiteSpace(designation)) throw new RegleMetierException("Chaque ligne libre a une désignation.");
        if (quantite <= 0) throw new RegleMetierException($"« {designation.Trim()} » : la quantité doit être supérieure à 0.");
        if (prixTtc <= 0) throw new RegleMetierException($"« {designation.Trim()} » : le prix doit être supérieur à 0.");
        if (tauxTva is < 0 or > 100) throw new RegleMetierException($"« {designation.Trim()} » : taux de TVA invalide.");
        return new LigneFacture(null, null, "libre", designation.Trim(), quantite, decimal.Round(prixTtc, 2), tauxTva, decimal.Round(quantite * prixTtc, 2));
    }

    /// <summary>Prix TTC : HT = TTC / (1 + taux), arrondi ligne par ligne ; TVA = TTC - HT.</summary>
    public static Totaux Calculer(IEnumerable<LigneFacture> lignes)
    {
        decimal ht = 0, ttc = 0;
        foreach (var l in lignes)
        {
            ttc += l.MontantTtc;
            ht += decimal.Round(l.MontantTtc / (1 + l.TauxTva / 100m), 2);
        }
        return new Totaux(ht, ttc - ht, ttc);
    }

    public static void VerifierEmission(IReadOnlyList<LigneFacture> lignes)
    {
        if (lignes.Count == 0) throw new RegleMetierException("La facture ne contient aucune ligne : choisissez des ventes ou ajoutez une ligne libre.");
        if (Calculer(lignes).Ttc <= 0) throw new RegleMetierException("Le total de la facture doit être supérieur à 0.");
    }

    /// <summary>Rejet par l'organisme ou le client : facture émise seulement, motif obligatoire.</summary>
    public static void VerifierRejet(string type, string statut, string? motif)
    {
        if (type != "facture") throw new RegleMetierException("Un avoir ne se rejette pas.");
        if (statut != "emise") throw new RegleMetierException("Seule une facture émise peut être rejetée.");
        if (string.IsNullOrWhiteSpace(motif)) throw new RegleMetierException("Indiquez le motif du rejet.");
    }

    /// <summary>Correction sur place ou remplacement : seulement après un rejet.</summary>
    public static void VerifierReprise(string statut)
    {
        if (statut != "rejetee") throw new RegleMetierException("Seule une facture rejetée peut être corrigée ou remplacée.");
    }

    /// <summary>Avoir : sur une facture émise, motif obligatoire, au plus le montant encore non couvert par d'autres avoirs.</summary>
    public static void VerifierAvoir(string type, string statut, decimal montantFacture, decimal avoirsDeja, decimal montant, string? motif)
    {
        if (type != "facture" || statut == "remplacee") throw new RegleMetierException("Un avoir se fait sur une facture en vigueur.");
        if (string.IsNullOrWhiteSpace(motif)) throw new RegleMetierException("Indiquez le motif de l’avoir.");
        if (montant <= 0) throw new RegleMetierException("Le montant de l’avoir doit être supérieur à 0.");
        if (montant > montantFacture - avoirsDeja)
            throw new RegleMetierException($"L’avoir dépasse ce qui reste de la facture ({montantFacture - avoirsDeja:N2} DA).");
    }

    /// <summary>Retrait du suivi de trésorerie : motif obligatoire (la facture reste valable et imprimable).</summary>
    public static void VerifierHorsSuivi(bool retirer, string? motif)
    {
        if (retirer && string.IsNullOrWhiteSpace(motif)) throw new RegleMetierException("Indiquez pourquoi la facture sort du suivi.");
    }

    /// <summary>Règlements imputés aux factures choisies, dans l'ordre, au plus le reste de chacune.</summary>
    public static IReadOnlyList<(long FactureId, decimal Montant)> Imputer(decimal montant, IEnumerable<(long FactureId, decimal Reste)> factures)
    {
        var liste = new List<(long, decimal)>();
        var restant = montant;
        foreach (var (id, reste) in factures)
        {
            if (restant <= 0) break;
            var part = Math.Min(restant, reste);
            if (part > 0) liste.Add((id, part));
            restant -= part;
        }
        return liste;
    }
}
