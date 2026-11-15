using Pharma.Domain.Commun;

namespace Pharma.Domain.Factures;

public enum Destinataire { Patient, Entreprise, Organisme }

/// <summary>Ligne prête à facturer. Part : patient, organisme ou libre. Montant TTC (négatif pour une remise).</summary>
public sealed record LigneFacture(long? VenteId, long? LigneVenteId, string Part, string Designation, decimal Quantite, decimal PrixUnitaireTtc,
    decimal TauxTva, decimal MontantTtc);

/// <summary>
/// Ligne de vente facturable (quantité nette des retours, parts au prorata). ReductionLigne : réductions de la ligne
/// (remise automatique, remise du vendeur, don partiel), au prorata de la quantité nette ; 0 pour une vente antérieure aux
/// réductions par ligne (2026-11-14), dont la remise et le don restent portés par la vente.
/// </summary>
public sealed record LigneVendue(long VenteId, string NumeroVente, long LigneVenteId, string Designation, decimal TauxTva, decimal Quantite,
    decimal MontantPatient, decimal MontantOrganisme, decimal RemiseVente, decimal DonVente = 0, decimal ReductionLigne = 0);

public sealed record Totaux(decimal Ht, decimal Tva, decimal Ttc);

public static class RegleFacture
{
    /// <summary>Échéance proposée par défaut : date d'établissement plus ce nombre de jours.</summary>
    public const int DelaiEcheanceJours = 30;

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
    /// Mention d'une vente sur la facture adressée à un organisme (2026-11-15) : patient, n° d'assuré, n° d'ordonnance et médecin
    /// quand ils sont connus. Données de santé : jamais sur une facture de patient ou d'entreprise. Null si rien n'est connu.
    /// </summary>
    public static string? MentionOrganisme(string? patient, string? numAssure, string? ordonnance, string? medecin)
    {
        var parties = new List<string>();
        if (!string.IsNullOrWhiteSpace(patient)) parties.Add($"patient {patient.Trim()}");
        if (!string.IsNullOrWhiteSpace(numAssure)) parties.Add($"n° d’assuré {numAssure.Trim()}");
        if (!string.IsNullOrWhiteSpace(ordonnance)) parties.Add($"ordonnance {ordonnance.Trim()}");
        if (!string.IsNullOrWhiteSpace(medecin)) parties.Add($"médecin {medecin.Trim()}");
        return parties.Count == 0 ? null : string.Join(", ", parties);
    }

    /// <summary>
    /// Lignes d'une facture à partir de ventes : une ligne par produit (part concernée, quantité nette) ;
    /// pour la part patient, la remise de chaque vente devient une ligne négative par taux de TVA.
    /// </summary>
    public static IReadOnlyList<LigneFacture> DepuisVentes(Destinataire d, IEnumerable<LigneVendue> lignes,
        IReadOnlyDictionary<long, string>? mentionsOrganisme = null)
    {
        var part = Part(d);
        var resultat = new List<LigneFacture>();
        foreach (var vente in lignes.GroupBy(l => (l.VenteId, l.NumeroVente)).OrderBy(g => g.Key.NumeroVente, StringComparer.Ordinal))
        {
            // Facture d'organisme seulement (2026-11-15) : patient, n° d'assuré, ordonnance et médecin de la vente sur chaque ligne
            var mention = part == "organisme" && mentionsOrganisme is not null && mentionsOrganisme.TryGetValue(vente.Key.VenteId, out var m)
                && !string.IsNullOrWhiteSpace(m) ? $" · {m}" : "";
            // Réductions par ligne (2026-11-15) : la part patient de chaque ligne est facturée nette de ses propres réductions,
            // au taux de TVA du produit (HT, TVA et TTC = ce que le patient a payé pour ce produit).
            var reductionsParLigne = vente.Any(l => l.ReductionLigne > 0);
            foreach (var l in vente.Where(l => l.Quantite > 0))
            {
                var montant = part == "organisme" ? l.MontantOrganisme : l.MontantPatient - (reductionsParLigne ? l.ReductionLigne : 0);
                if (montant <= 0) continue;
                resultat.Add(new LigneFacture(l.VenteId, l.LigneVenteId, part, $"{l.Designation} (vente {vente.Key.NumeroVente}{mention})", l.Quantite,
                    Arrondi.Centime(montant / l.Quantite), l.TauxTva, montant));
            }
            if (reductionsParLigne) continue;
            // Vente antérieure aux réductions par ligne : remise et don de la vente répartis par taux de TVA
            var remise = vente.First().RemiseVente;
            var lignesVente = resultat.Where(r => r.VenteId == vente.Key.VenteId).ToList();
            if (part == "patient" && remise > 0)
                resultat.AddRange(Remises(vente.Key.VenteId, vente.Key.NumeroVente, remise, lignesVente));
            // Don partiel du reste à charge (réductions sur des médicaments) : lignes négatives « Don partiel », réparties comme la remise
            var don = vente.First().DonVente;
            if (part == "patient" && don > 0)
                resultat.AddRange(Remises(vente.Key.VenteId, vente.Key.NumeroVente, don, lignesVente, "Don partiel"));
        }
        return resultat;
    }

    /// <summary>
    /// La remise d'une vente porte la TVA des produits remisés : elle est répartie par taux de TVA, au prorata des montants
    /// facturés (une ligne négative par taux), sinon la TVA de la facture ne baisserait pas avec la remise. Le dernier
    /// taux reçoit le reste : la somme des lignes est exactement la remise.
    /// </summary>
    private static IEnumerable<LigneFacture> Remises(long venteId, string numeroVente, decimal remise, IReadOnlyList<LigneFacture> lignesVente,
        string nature = "Remise")
    {
        var parTaux = lignesVente.GroupBy(l => l.TauxTva).Select(g => (Taux: g.Key, Montant: g.Sum(l => l.MontantTtc))).OrderBy(t => t.Taux).ToList();
        var total = parTaux.Sum(t => t.Montant);
        if (total <= 0) yield break;
        var reste = remise;
        for (var i = 0; i < parTaux.Count; i++)
        {
            var montant = i == parTaux.Count - 1 ? reste : Arrondi.Centime(remise * parTaux[i].Montant / total);
            reste -= montant;
            if (montant == 0) continue;
            var libelle = parTaux.Count == 1 ? $"{nature} (vente {numeroVente})" : $"{nature} (vente {numeroVente}, TVA {parTaux[i].Taux:0.##} %)";
            yield return new LigneFacture(venteId, null, "patient", libelle, 1, -montant, parTaux[i].Taux, -montant);
        }
    }

    /// <summary>Ligne saisie à la main : désignation, quantité et prix positifs, TVA 0 à 100 %.</summary>
    public static LigneFacture Libre(string? designation, decimal quantite, decimal prixTtc, decimal tauxTva)
    {
        if (string.IsNullOrWhiteSpace(designation)) throw new RegleMetierException("Chaque ligne libre a une désignation.");
        if (quantite <= 0) throw new RegleMetierException($"« {designation.Trim()} » : la quantité doit être supérieure à 0.");
        if (prixTtc <= 0) throw new RegleMetierException($"« {designation.Trim()} » : le prix doit être supérieur à 0.");
        if (tauxTva is < 0 or > 100) throw new RegleMetierException($"« {designation.Trim()} » : taux de TVA invalide.");
        return new LigneFacture(null, null, "libre", designation.Trim(), quantite, Arrondi.Centime(prixTtc), tauxTva, Arrondi.Centime(quantite * prixTtc));
    }

    /// <summary>Prix TTC : HT = TTC / (1 + taux), arrondi ligne par ligne ; TVA = TTC - HT.</summary>
    public static Totaux Calculer(IEnumerable<LigneFacture> lignes)
    {
        decimal ht = 0, ttc = 0;
        foreach (var l in lignes)
        {
            ttc += l.MontantTtc;
            ht += Arrondi.Centime(l.MontantTtc / (1 + l.TauxTva / 100m));
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
        if (string.IsNullOrWhiteSpace(motif)) throw new RegleMetierException("Indiquez le motif du rejet.", "motif");
    }

    /// <summary>Correction sur place ou remplacement : seulement après un rejet.</summary>
    /// <summary>L'organisme revient sur son rejet (ou l'a levé) : la facture redevient émise, le rejet reste dans son historique.</summary>
    /// <summary>Délai de paiement d'un organisme ou d'un client : de 1 à 365 jours.</summary>
    public static void VerifierDelaiPaiement(int jours)
    {
        if (jours is < 1 or > 365) throw new RegleMetierException("Le délai de paiement doit être compris entre 1 et 365 jours.", "delai_paiement");
    }

    public static void VerifierLeveeRejet(string type, string statut, string? motif)
    {
        if (type != "facture" || statut != "rejetee") throw new RegleMetierException("Seule une facture rejetée peut voir son rejet levé.");
        if (string.IsNullOrWhiteSpace(motif)) throw new RegleMetierException("Indiquez pourquoi le rejet est levé (il est inscrit à l'historique).", "motif");
    }

    public static void VerifierReprise(string statut)
    {
        if (statut != "rejetee") throw new RegleMetierException("Seule une facture rejetée peut être corrigée ou remplacée.");
    }

    /// <summary>Une facture rejetée puis payée en entier est soldée : il n'y a plus rien à corriger ni à remplacer.</summary>
    public static void VerifierReprise(string statut, decimal reste)
    {
        VerifierReprise(statut);
        if (reste <= 0) throw new RegleMetierException("Cette facture a été payée en entier après son rejet : elle est soldée, il n'y a plus rien à corriger.");
    }

    /// <summary>État affiché d'une facture rejetée : « soldée » dès qu'elle est payée en entier (le rejet reste dans son historique).</summary>
    public static bool RejetLeve(string statut, decimal reste, bool horsSuivi) => statut == "rejetee" && reste <= 0 && !horsSuivi;

    /// <summary>
    /// Avoir : sur une facture émise, motif obligatoire, montant au centime (plus de deux décimales : refusé), au plus le montant encore
    /// non couvert par d'autres avoirs.
    /// </summary>
    public static void VerifierAvoir(string type, string statut, decimal montantFacture, decimal avoirsDeja, decimal montant, string? motif)
    {
        if (type != "facture" || statut == "remplacee") throw new RegleMetierException("Un avoir se fait sur une facture en vigueur.");
        if (string.IsNullOrWhiteSpace(motif)) throw new RegleMetierException("Indiquez le motif de l’avoir.", "motif");
        if (montant <= 0) throw new RegleMetierException("Le montant de l’avoir doit être supérieur à 0.", "montant_avoir");
        if (!Arrondi.AuCentime(montant))
            throw new RegleMetierException("Le montant de l’avoir compte au plus deux décimales (centimes) : corrigez la saisie.", "montant_avoir");
        if (montant > montantFacture - avoirsDeja)
            throw new RegleMetierException($"L’avoir dépasse ce qui reste de la facture ({montantFacture - avoirsDeja:N2} DA).", "montant_avoir");
    }

    /// <summary>
    /// « Déjà réglé par le patient sur sa dette » affiché sur la facture (écran et PDF, 2026-11-15) : le réglé des ventes à crédit
    /// comprend la part des avoirs imputée sur la dette (part_credit), déjà affichée dans « Avoirs » ; elle en est retirée pour que
    /// total − déjà payé − déjà réglé − règlements − avoirs = reste.
    /// </summary>
    public static decimal RegleParDetteAffiche(decimal regleVentesCredit, decimal partAvoirsSurDette) =>
        Math.Max(0, regleVentesCredit - partAvoirsSurDette);

    /// <summary>
    /// Colonne « Payé / réglé » de la liste des factures (E6, 2026-11-15) : alignée sur le détail, où les avoirs ont leur propre ligne.
    /// TTC − reste compte l'avoir (y compris sa part imputée sur la dette) ; il en est retiré : payé au comptoir + règlements + réglé sur la dette.
    /// </summary>
    public static decimal PayeAfficheListe(decimal montantTtc, decimal reste, decimal avoirs) =>
        Math.Max(0, montantTtc - reste - avoirs);

    /// <summary>
    /// Avoir sur une facture dont des ventes sont à crédit (084, 2026-11-15) : la dette du patient diminue de
    /// min(avoir, reste encore dû à crédit de ces ventes), ventes les plus anciennes d'abord (restes donnés dans cet ordre).
    /// Renvoie la part de chaque vente ; leur somme est la part de l'avoir imputée sur la dette (facture_client.part_credit),
    /// que v_facture_client ne retire pas une seconde fois du reste de la facture. Facture déjà payée : aucune part.
    /// </summary>
    public static IReadOnlyList<(long VenteId, decimal Montant)> RepartirAvoirSurDette(decimal avoir, IEnumerable<(long VenteId, decimal Reste)> restesCredit)
    {
        var parts = new List<(long, decimal)>();
        var disponible = Math.Max(avoir, 0);
        foreach (var (vente, reste) in restesCredit)
        {
            if (disponible <= 0) break;
            if (reste <= 0) continue;
            var part = Math.Min(reste, disponible);
            parts.Add((vente, part));
            disponible -= part;
        }
        return parts;
    }

    /// <summary>Retrait du suivi de trésorerie : motif obligatoire (la facture reste valable et imprimable).</summary>
    public static void VerifierHorsSuivi(bool retirer, string? motif)
    {
        if (retirer && string.IsNullOrWhiteSpace(motif)) throw new RegleMetierException("Indiquez pourquoi la facture sort du suivi.", "motif");
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
