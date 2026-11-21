using Pharma.Domain.Commun;

namespace Pharma.Domain.Ventes;

/// <summary>Ce que devient la boîte reprise au patient : choix du pharmacien.</summary>
public enum DestinationRetour { Stock, Destruction }

/// <summary>
/// Répartition de ce que le retour rend au patient (script 158) : DetteVenteDiminuee (part à crédit de la vente retirée de sa dette),
/// RegularisationDiminuee (part impayée de la régularisation à sa charge retirée de sa dette), ImputeSurDettes (argent dû au patient
/// imputé sur ses dettes, comme un encaissement de dette, sans passer par le tiroir), ARendre (argent rendu selon le mode d'origine),
/// ChoixExige (le patient doit encore de l'argent : le vendeur a dû choisir), DetteRestante (dette avant l'imputation).
/// </summary>
public sealed record RemboursementRetour(decimal DetteVenteDiminuee, decimal RegularisationDiminuee, decimal ImputeSurDettes, decimal ARendre,
    bool ChoixExige, decimal DetteRestante);

/// <summary>
/// Retenue de la régularisation « rendu au patient » des boîtes rendues (script 159) : SurArgent (retenue sur l'argent payé que le retour
/// rend), SurAvoir (retenue sur l'avoir recrédité), Dette (reste non couvert, ajouté à la dette du patient).
/// </summary>
public sealed record RetenueRetour(decimal SurArgent, decimal SurAvoir, decimal Dette)
{
    public decimal Total => SurArgent + SurAvoir + Dette;
}

/// <summary>
/// Retour de médicaments par le patient après la vente, à l'appréciation du pharmacien : retour partiel possible,
/// boîtes remises en stock ou à détruire, part payée par le patient rendue au prorata.
/// Vente en tiers payant (décision du titulaire du 2026-11-21, option A, script 155) : avant tout dépôt (vente sur aucun bordereau),
/// la part de l'organisme des boîtes rendues est retirée de la créance, au prorata de la part retenue (part Chifa si la vente a été
/// régularisée après rapprochement de son volet, sinon part de la caisse ; majoration à part). Après dépôt : refusé. Une ligne sans
/// part organisme (non remboursée) se rend toujours.
/// </summary>
public static class RegleRetour
{
    /// <summary>Message exact du titulaire (2026-11-21) : vente sur un bordereau déposé, retour ou annulation impossibles dans le logiciel.</summary>
    public const string MessageDeposee = "Vente déposée à la CNAS : retour impossible dans le logiciel tant que la procédure de la CNAS n’est pas connue";

    public static void VerifierVente(string statut)
    {
        if (statut != "validee") throw new RegleMetierException("Seule une vente encaissée (non annulée) peut faire l’objet d’un retour.");
    }

    /// <summary>
    /// Boîte rendue qui a une part organisme : vente sur aucun bordereau (même condition que l'annulation). Déposée : refus (message du
    /// titulaire) ; sur un bordereau en préparation : la retirer d'abord du bordereau.
    /// </summary>
    public static void VerifierPartOrganisme(bool deposee, bool dansUnBordereau)
    {
        if (deposee) throw new RegleMetierException(MessageDeposee);
        if (dansUnBordereau)
            throw new RegleMetierException("Cette vente figure sur un bordereau Chifa en préparation : retirez-la d’abord du bordereau (Finances › Bordereaux Chifa), puis faites le retour.");
    }

    /// <summary>
    /// Rapport entre ce qui reste de la part retenue de l'organisme (hors majoration) et la part de la caisse des boîtes remboursées
    /// encore chez le patient (script 157) : 1 sans régularisation, part Chifa / part caisse après régularisation, quel que soit l'ordre
    /// du retour et du rattachement du volet ; 0 s'il ne reste rien (part retenue épuisée, ou plus aucune boîte remboursée).
    /// </summary>
    public static decimal RatioRetenu(decimal partRetenue, decimal sommeLignesOrganisme) =>
        sommeLignesOrganisme <= 0 || partRetenue <= 0 ? 0 : partRetenue / sommeLignesOrganisme;

    /// <summary>Part de la caisse (organisme) des boîtes d'une ligne encore chez le patient, non arrondie.</summary>
    public static decimal PartCaisseRestante(decimal montantOrganismeLigne, int quantiteVendue, int dejaRendue) =>
        quantiteVendue <= 0 || montantOrganismeLigne <= 0 ? 0
            : montantOrganismeLigne * Math.Max(quantiteVendue - dejaRendue, 0) / quantiteVendue;

    /// <summary>Part de l'organisme des boîtes rendues d'une ligne : prorata des boîtes, ramené à la part retenue (arrondi au centime).</summary>
    public static decimal MontantOrganisme(decimal montantOrganismeLigne, int quantiteVendue, int quantiteRendue, decimal ratioRetenu) =>
        quantiteVendue <= 0 || montantOrganismeLigne <= 0 || ratioRetenu <= 0 ? 0
            : Math.Round(montantOrganismeLigne * quantiteRendue * ratioRetenu / quantiteVendue, 2, MidpointRounding.AwayFromZero);

    /// <summary>
    /// Part organisme retirée par ce retour : au plus ce qui reste de la part retenue ; quand toutes les boîtes remboursées sont
    /// désormais rendues, le solde exact (les arrondis des retours successifs ne laissent ni ne retirent un centime de trop).
    /// </summary>
    public static decimal TotalOrganisme(decimal calcule, decimal partRetenue, decimal dejaRetiree, bool toutRembourseRendu)
    {
        var reste = Math.Max(partRetenue - dejaRetiree, 0);
        return toutRembourseRendu ? reste : Math.Min(calcule, reste);
    }

    /// <summary>Quantité rendue : au moins 1, au plus ce qui a été vendu moins ce qui a déjà été rendu.</summary>
    public static void VerifierQuantite(string produit, int quantite, int vendue, int dejaRendue)
    {
        if (quantite <= 0) throw new RegleMetierException($"{produit} : quantité rendue invalide.");
        if (quantite > vendue - dejaRendue)
            throw new RegleMetierException($"{produit} : {vendue - dejaRendue} boîte(s) au plus peuvent encore être rendues ({vendue} vendue(s), {dejaRendue} déjà rendue(s)).");
    }

    /// <summary>Part payée par le patient pour les boîtes rendues (au prorata, arrondie au centime).</summary>
    public static decimal Montant(decimal montantPatientLigne, int quantiteVendue, int quantiteRendue) =>
        quantiteVendue <= 0 ? 0 : Math.Round(montantPatientLigne * quantiteRendue / quantiteVendue, 2, MidpointRounding.AwayFromZero);

    /// <summary>
    /// Part du patient remise appliquée : les lignes portent le prix avant la remise de la vente ; le patient a payé
    /// partPatient (= somme des lignes - remise). Montant d'une ligne rendue au prorata de ce qui a été payé.
    /// </summary>
    public static decimal MontantPaye(decimal montantPatientLigne, int quantiteVendue, int quantiteRendue, decimal partPatient, decimal brutPatient) =>
        brutPatient <= 0 ? 0
            : Math.Round(Montant(montantPatientLigne, quantiteVendue, quantiteRendue) * Math.Min(partPatient / brutPatient, 1m), 2, MidpointRounding.AwayFromZero);

    /// <summary>
    /// Total à rendre pour ce retour : au plus ce qui reste de la part payée. Si tout est désormais rendu, le solde exact
    /// (les arrondis des retours successifs ne font ni perdre ni gagner un centime).
    /// </summary>
    public static decimal TotalARendre(decimal calcule, decimal partPatient, decimal dejaRendu, bool toutRendu)
    {
        var reste = Math.Max(partPatient - dejaRendu, 0);
        return toutRendu ? reste : Math.Min(calcule, reste);
    }

    /// <summary>
    /// Part de la régularisation « à la charge du patient » (rattachement du volet) d'une ligne rendue (décision du titulaire du
    /// 2026-11-21, script 158) : ce qui reste de l'écart mis à sa charge, au prorata de la part de la caisse des boîtes rendues sur
    /// celle des boîtes remboursées encore chez le patient (même base que la part organisme), arrondi au centime.
    /// Même formule pour la régularisation « rendu au patient » (script 159) : part de l'écart déjà rendue au patient, à retenir.
    /// </summary>
    public static decimal MontantRegularisation(decimal montantOrganismeLigne, int quantiteVendue, int quantiteRendue, decimal regularisationRestante,
        decimal partCaisseRestante) =>
        quantiteVendue <= 0 || montantOrganismeLigne <= 0 || regularisationRestante <= 0 || partCaisseRestante <= 0 ? 0
            : Math.Min(Math.Round(regularisationRestante * (montantOrganismeLigne * quantiteRendue / quantiteVendue) / partCaisseRestante, 2,
                MidpointRounding.AwayFromZero), regularisationRestante);

    /// <summary>Code du choix du vendeur quand le patient a des dettes : les déduire (recommandé) ou rendre quand même.</summary>
    public const string DettesDeduire = "deduire";
    public const string DettesRendre = "rendre";

    /// <summary>
    /// Ce que le retour rend au patient, hors avoir recrédité (décision du titulaire du 2026-11-21, script 158), en trois temps :
    /// 1) ce qui n'a pas été payé est retiré de sa dette : part à crédit de la vente (calculée avant, <paramref name="detteVenteDiminuee"/>),
    ///    puis part impayée de la régularisation mise à sa charge ;
    /// 2) le reste est de l'argent réellement payé (<paramref name="renduPartPatient"/> + part payée de la régularisation) ;
    /// 3) patient connu qui doit encore de l'argent après 1) : choix exigé du vendeur, « déduire » (imputé sur ses dettes comme un
    ///    encaissement de dette, au plus ce qu'il doit ; le surplus est rendu) ou « rendre » quand même ; sans dette ou patient inconnu,
    ///    tout est rendu selon le mode de paiement d'origine.
    /// </summary>
    /// <param name="detteVenteDiminuee">Part du patient de la vente à crédit retirée de sa dette (0 si la vente est payée).</param>
    /// <param name="renduPartPatient">Part du patient réellement payée, à rendre (hors avoir).</param>
    /// <param name="partRegularisation">Part de la régularisation à la charge du patient des boîtes rendues.</param>
    /// <param name="regularisationImpayee">Part de la régularisation encore due (non payée) avant ce retour.</param>
    /// <param name="dettePatient">Dette totale du patient avant ce retour (toutes ventes).</param>
    /// <param name="choix">null : pas encore choisi ; <see cref="DettesDeduire"/> ou <see cref="DettesRendre"/>.</param>
    public static RemboursementRetour Rembourser(decimal detteVenteDiminuee, decimal renduPartPatient, decimal partRegularisation,
        decimal regularisationImpayee, decimal dettePatient, bool patientConnu, string? choix)
    {
        if (choix is not null and not DettesDeduire and not DettesRendre)
            throw new RegleMetierException("Choix inconnu pour les dettes du patient.", "dettes_patient");
        var dette = Math.Max(dettePatient, 0);
        var r = Math.Max(partRegularisation, 0);
        // Part impayée de la régularisation des boîtes rendues : retirée de sa dette, jamais rendue
        var regulDiminuee = Math.Min(Math.Min(r, Math.Max(regularisationImpayee, 0)), Math.Max(dette - detteVenteDiminuee, 0));
        var paye = Math.Max(renduPartPatient, 0) + r - regulDiminuee;
        var detteRestante = Math.Max(dette - detteVenteDiminuee - regulDiminuee, 0);
        var choixExige = patientConnu && paye > 0 && detteRestante > 0;
        if (!choixExige) return new RemboursementRetour(detteVenteDiminuee, regulDiminuee, 0, paye, false, detteRestante);
        if (choix is null)
            throw new RegleMetierException(
                $"Ce patient doit encore {detteRestante:N2} DA : proposez-lui de déduire les {paye:N2} DA du retour de ses dettes (recommandé), ou rendez-les quand même.",
                "dettes_patient");
        var impute = choix == DettesDeduire ? Math.Min(paye, detteRestante) : 0;
        return new RemboursementRetour(detteVenteDiminuee, regulDiminuee, impute, paye - impute, true, detteRestante);
    }

    /// <summary>
    /// Retenue de la régularisation « rendu au patient » (décision du titulaire du 2026-11-21, script 159, même modèle que le script 158) :
    /// au rattachement du volet, l'organisme a couvert plus que prévu et la différence a été rendue au patient ; pour les boîtes rendues,
    /// cette part (<paramref name="retenue"/>, prorata de <see cref="MontantRegularisation"/>) est retenue sur ce que le retour lui rend,
    /// AVANT la règle des dettes (<see cref="Rembourser"/>) : d'abord sur l'argent qu'il a payé et que le retour lui rendrait, puis sur
    /// l'avoir recrédité. Le reste non couvert devient une dette du patient (relevé, journal), seulement après l'alerte au vendeur
    /// (<paramref name="detteConfirmee"/>). Vente sans patient : aucune dette possible, le retour est refusé (jamais de perte non tracée).
    /// </summary>
    /// <param name="argentPaye">Argent réellement payé que le retour rendrait (hors dette diminuée d'une vente à crédit, hors avoir).</param>
    /// <param name="avoirRecredite">Avoir que le retour recréditerait (vente payée en partie par avoir).</param>
    public static RetenueRetour Retenir(decimal retenue, decimal argentPaye, decimal avoirRecredite, bool patientConnu, bool detteConfirmee)
    {
        var r = Math.Max(retenue, 0);
        var surArgent = Math.Min(r, Math.Max(argentPaye, 0));
        var surAvoir = Math.Min(r - surArgent, Math.Max(avoirRecredite, 0));
        var dette = r - surArgent - surAvoir;
        if (dette > 0 && !patientConnu)
            throw new RegleMetierException(
                $"Vente sans patient : {dette:N2} DA déjà rendus au rattachement du volet Chifa ne sont pas couverts par ce retour et ne peuvent devenir la dette de personne. Retour impossible dans le logiciel : voyez le titulaire.",
                "dette_retenue");
        if (dette > 0 && !detteConfirmee)
            throw new RegleMetierException(
                $"Le patient a déjà reçu {r:N2} DA pour ces boîtes au rattachement du volet Chifa : le retour n’en couvre que {surArgent + surAvoir:N2} DA, {dette:N2} DA seront ajoutés à sa dette. Prévenez-le, puis confirmez.",
                "dette_retenue");
        return new RetenueRetour(surArgent, surAvoir, dette);
    }

    public static DestinationRetour Destination(string? code) => code switch
    {
        "stock" => DestinationRetour.Stock,
        "destruction" => DestinationRetour.Destruction,
        _ => throw new RegleMetierException("Choisissez pour chaque produit : remettre en stock ou à détruire.")
    };

    public static string Code(this DestinationRetour d) => d == DestinationRetour.Stock ? "stock" : "destruction";
}
