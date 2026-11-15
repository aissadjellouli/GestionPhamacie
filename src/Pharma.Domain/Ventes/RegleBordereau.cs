using System.Text.RegularExpressions;
using Pharma.Domain.Commun;

namespace Pharma.Domain.Ventes;

/// <summary>
/// Bordereaux Chifa / MDN (décisions du titulaire, 2026-11-15, script 110). Le n° est celui de CHIFA-Officine (saisi, jamais attribué
/// par le logiciel), unique par organisme. Un bordereau par organisme et par centre payeur (MDN : centre facultatif) ; il regroupe des
/// factures Chifa, une par vente. Statuts : en préparation → déposé → payé en partie / payé → soldé.
/// Montant attendu = somme des parts organisme retenues (après régularisation, majoration comprise) des ventes du bordereau.
/// Reste à recevoir = attendu − versements − montants rejetés.
/// </summary>
public static partial class RegleBordereau
{
    public const string Preparation = "preparation";
    public const string Depose = "depose";
    public const string PayePartiel = "paye_partiel";
    public const string Paye = "paye";
    public const string Solde = "solde";
    public static readonly string[] Statuts = [Preparation, Depose, PayePartiel, Paye, Solde];

    /// <summary>Suites d'un rejet : la vente redevient « à déposer », passe en perte, ou est réclamée au patient (dette).</summary>
    public const string Redeposer = "redeposer";
    public const string Perte = "perte";
    public const string Patient = "patient";
    public static readonly string[] Suites = [Redeposer, Perte, Patient];

    public const int DelaiDepotParDefaut = 7;
    public const int DelaiPaiementParDefaut = 45;
    public const int NumeroMax = 30;

    public static string Libelle(string statut) => statut switch
    {
        Preparation => "En préparation",
        Depose => "Déposé",
        PayePartiel => "Payé en partie",
        Paye => "Payé (rejets à traiter)",
        Solde => "Soldé",
        _ => statut
    };

    public static string LibelleSuite(string? suite) => suite switch
    {
        Redeposer => "Redéposer",
        Perte => "Passé en perte",
        Patient => "Réclamé au patient",
        null => "À traiter",
        _ => suite
    };

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9 /._-]*$")]
    private static partial Regex NumeroPermis();

    /// <summary>N° de bordereau saisi (celui de CHIFA-Officine) : 1 à 30 caractères, lettres, chiffres, espace, / . _ -.</summary>
    public static string Numero(string? numero)
    {
        var n = numero?.Trim() ?? "";
        if (n.Length == 0) throw new RegleMetierException("Indiquez le n° du bordereau (celui de CHIFA-Officine).", "numero");
        if (n.Length > NumeroMax) throw new RegleMetierException($"N° du bordereau : {NumeroMax} caractères au plus.", "numero");
        if (!NumeroPermis().IsMatch(n)) throw new RegleMetierException("N° du bordereau : lettres, chiffres, espace, / . _ - seulement.", "numero");
        return n;
    }

    /// <summary>Période (fin au plus tôt le début, pas dans le futur), date de dépôt (pas avant le début, pas dans le futur), montant déclaré ≥ 0.</summary>
    public static void VerifierEntete(DateOnly debut, DateOnly fin, DateOnly? depot, decimal? declare, DateOnly aujourdhui)
    {
        if (fin < debut) throw new RegleMetierException("Période : la fin précède le début.", "periode");
        if (debut > aujourdhui) throw new RegleMetierException("Période : elle ne peut pas commencer dans le futur.", "periode");
        if (depot is { } d && d > aujourdhui) throw new RegleMetierException("Date de dépôt : elle ne peut pas être dans le futur.", "date_depot");
        if (depot is { } d2 && d2 < debut) throw new RegleMetierException("Date de dépôt : elle précède le début de la période.", "date_depot");
        VerifierMontantDeclare(declare);
    }

    public static void VerifierMontantDeclare(decimal? declare)
    {
        if (declare is not { } m) return;
        if (m < 0) throw new RegleMetierException("Montant déclaré : il ne peut pas être négatif.", "montant_declare");
        if (decimal.Round(m, 2) != m) throw new RegleMetierException("Montant déclaré : deux décimales au plus.", "montant_declare");
        if (m > 100_000_000m) throw new RegleMetierException("Montant déclaré : 100 000 000 DA au plus.", "montant_declare");
    }

    /// <summary>Écart déclaré − attendu (null sans montant déclaré) : affiché en rouge s'il n'est pas nul.</summary>
    public static decimal? Ecart(decimal attendu, decimal? declare) => declare is { } d ? d - attendu : null;

    public static decimal Reste(decimal attendu, decimal verse, decimal rejete) => attendu - verse - rejete;

    /// <summary>
    /// Statut après un versement ou un rejet (un bordereau en préparation le reste) : rien reçu ni rejeté → déposé ; reste à recevoir →
    /// payé en partie (déposé s'il n'y a que des rejets) ; plus rien à recevoir → soldé, ou « payé » tant qu'un rejet n'a pas de suite.
    /// </summary>
    public static string StatutApres(string actuel, decimal attendu, decimal verse, decimal rejete, int rejetsATraiter)
    {
        if (actuel == Preparation) return Preparation;
        if (Reste(attendu, verse, rejete) > 0) return verse > 0 ? PayePartiel : Depose;
        if (verse == 0 && rejete == 0) return Depose;
        return rejetsATraiter > 0 ? Paye : Solde;
    }

    /// <summary>Ventes et en-tête modifiables seulement en préparation.</summary>
    public static void ExigerPreparation(string statut)
    {
        if (statut != Preparation)
            throw new RegleMetierException("Ce bordereau est déjà déposé : ses ventes ne changent plus (rejets et paiements seulement).", "statut");
    }

    /// <summary>Dépôt : bordereau en préparation, au moins une vente, date de dépôt pas dans le futur.</summary>
    public static void VerifierDepot(string statut, int ventes, DateOnly depot, DateOnly debut, DateOnly aujourdhui)
    {
        ExigerPreparation(statut);
        if (ventes == 0) throw new RegleMetierException("Ajoutez au moins une vente avant de déposer le bordereau.", "ventes");
        if (depot > aujourdhui) throw new RegleMetierException("Date de dépôt : elle ne peut pas être dans le futur.", "date_depot");
        if (depot < debut) throw new RegleMetierException("Date de dépôt : elle précède le début de la période.", "date_depot");
    }

    /// <summary>
    /// Une vente rejoint un bordereau en préparation si elle est encaissée, du même canal et du même organisme, non exclue, sur aucun
    /// autre bordereau, avec son n° de facture Chifa (canal chifa), et du même centre payeur (ou sans centre : il lui est donné).
    /// </summary>
    public static void VerifierRattachement(string canalBordereau, long organismeBordereau, long? centreBordereau,
        string canalVente, string statutVente, long? organismeVente, long? centreVente, bool exclue, bool surUnBordereau, string? factureChifa,
        bool rejeteDefinitivement, decimal partOrganisme)
    {
        if (statutVente != "validee") throw new RegleMetierException("Seule une vente encaissée (non annulée) va sur un bordereau.", "vente");
        if (canalVente != canalBordereau)
            throw new RegleMetierException(canalBordereau == "mdn" ? "Cette vente n’est pas facturée au MDN." : "Cette vente n’est pas facturée par Chifa.", "vente");
        if (organismeVente != organismeBordereau) throw new RegleMetierException("Cette vente est d’un autre organisme que le bordereau.", "vente");
        if (exclue) throw new RegleMetierException("Cette vente est exclue du traitement Chifa : rétablissez-la d’abord.", "vente");
        if (surUnBordereau) throw new RegleMetierException("Cette vente est déjà sur un bordereau.", "vente");
        if (rejeteDefinitivement) throw new RegleMetierException("Cette vente a été rejetée et passée en perte ou réclamée au patient : elle ne se redépose pas.", "vente");
        if (canalVente == "chifa" && string.IsNullOrWhiteSpace(factureChifa))
            throw new RegleMetierException("Cette vente n’a pas encore de n° de facture Chifa (volet à rattacher).", "vente");
        if (centreBordereau is { } c && centreVente is { } cv && cv != c)
            throw new RegleMetierException("Cette vente est d’un autre centre payeur que le bordereau.", "vente");
        if (partOrganisme <= 0) throw new RegleMetierException("Cette vente n’a pas de part organisme à facturer.", "vente");
    }

    /// <summary>Rejet d'une facture : bordereau déposé (non soldé), facture pas déjà rejetée, montant de 0 exclu à la part organisme.</summary>
    public static void VerifierRejet(string statut, bool dejaRejetee, decimal montant, decimal partOrganisme)
    {
        if (statut is not (Depose or PayePartiel or Paye))
            throw new RegleMetierException(statut == Preparation
                ? "Ce bordereau n’est pas encore déposé : retirez la vente au lieu de la rejeter."
                : "Ce bordereau est soldé : plus de rejet possible.", "statut");
        if (dejaRejetee) throw new RegleMetierException("Cette facture est déjà rejetée.", "vente");
        if (montant <= 0) throw new RegleMetierException("Montant rejeté : supérieur à 0.", "montant_rejete");
        if (decimal.Round(montant, 2) != montant) throw new RegleMetierException("Montant rejeté : deux décimales au plus.", "montant_rejete");
        if (montant > partOrganisme)
            throw new RegleMetierException($"Montant rejeté : au plus la part organisme de la vente ({partOrganisme:N2} DA).", "montant_rejete");
    }

    /// <summary>Suite d'un rejet : une seule fois ; « réclamer au patient » exige un patient sur la vente.</summary>
    public static string VerifierSuite(string? suite, bool rejetee, string? suiteActuelle, bool aUnPatient)
    {
        var s = suite?.Trim().ToLowerInvariant();
        if (!rejetee) throw new RegleMetierException("Cette facture n’est pas rejetée.", "vente");
        if (suiteActuelle is not null) throw new RegleMetierException("Ce rejet est déjà traité.", "suite");
        if (s is null || !Suites.Contains(s)) throw new RegleMetierException("Choisissez la suite du rejet : redéposer, passer en perte ou réclamer au patient.", "suite");
        if (s == Patient && !aUnPatient) throw new RegleMetierException("Cette vente n’a pas de patient : choisissez une autre suite.", "suite");
        return s;
    }

    /// <summary>
    /// Imputation d'un versement d'organisme sur des bordereaux : chacun déposé (non soldé) et du même organisme, montant de 0 exclu
    /// à son reste, total au plus le montant du versement. Un même bordereau une seule fois.
    /// </summary>
    public static void VerifierImputations(decimal montantVersement, long organismeId,
        IReadOnlyList<(long Id, string Numero, long OrganismeId, string Statut, decimal Reste, decimal Montant)> imputations)
    {
        if (imputations.Select(i => i.Id).Distinct().Count() != imputations.Count)
            throw new RegleMetierException("Un même bordereau est choisi deux fois.", "bordereaux");
        foreach (var i in imputations)
        {
            if (i.OrganismeId != organismeId) throw new RegleMetierException($"Le bordereau {i.Numero} est d’un autre organisme.", "bordereaux");
            if (i.Statut is not (Depose or PayePartiel))
                throw new RegleMetierException($"Le bordereau {i.Numero} n’attend plus de paiement : rechargez la liste.", "bordereaux");
            if (i.Montant <= 0) throw new RegleMetierException($"Bordereau {i.Numero} : montant imputé supérieur à 0.", "bordereaux");
            if (decimal.Round(i.Montant, 2) != i.Montant) throw new RegleMetierException($"Bordereau {i.Numero} : deux décimales au plus.", "bordereaux");
            if (i.Montant > i.Reste)
                throw new RegleMetierException($"Bordereau {i.Numero} : au plus son reste à recevoir ({i.Reste:N2} DA).", "bordereaux");
        }
        if (imputations.Sum(i => i.Montant) > montantVersement)
            throw new RegleMetierException("Le total imputé aux bordereaux dépasse le montant du versement.", "bordereaux");
    }

    /// <summary>En retard : au-delà du délai (jours) après la date de référence (vente à déposer, ou dépôt d'un bordereau impayé).</summary>
    public static bool EnRetard(DateOnly depuis, int delaiJours, DateOnly aujourdhui) => aujourdhui.DayNumber - depuis.DayNumber > delaiJours;
}
