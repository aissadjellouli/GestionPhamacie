using Pharma.Domain.Commun;

namespace Pharma.Domain.Securite;

/// <summary>
/// Code personnel de 4 à 6 chiffres pour reprendre la caisse du poste sans la fermer. Plus facile à deviner
/// qu'un mot de passe : il ne donne accès qu'au comptoir, et 5 erreurs le bloquent quelques minutes.
/// </summary>
public static class CodeComptoir
{
    public const int EssaisMax = 5;
    /// <summary>Premier blocage ; chaque blocage suivant (sans bon code entre-temps) dure deux fois plus longtemps.</summary>
    public static readonly TimeSpan Blocage = TimeSpan.FromMinutes(5);
    /// <summary>Durée maximale d'un blocage.</summary>
    public static readonly TimeSpan BlocageMax = TimeSpan.FromHours(24);
    /// <summary>Codes faux d'affilée à partir desquels chaque blocage est signalé au titulaire (alerte du contrôle interne).</summary>
    public const int SeuilAlerte = 2 * EssaisMax;

    /// <summary>
    /// Blocage progressif : le compteur d'essais n'est remis à zéro que par un bon code (ou un nouveau code). Dès le 5e code faux,
    /// chaque essai faux bloque le code : 5 min, puis 10, 20, 40… jusqu'à 24 h. Null : pas de blocage à cet essai.
    /// </summary>
    public static TimeSpan? DureeBlocage(int essai)
    {
        if (essai < EssaisMax) return null;
        var facteur = Math.Pow(2, Math.Min(essai - EssaisMax, 20));
        var duree = TimeSpan.FromTicks((long)Math.Min(Blocage.Ticks * facteur, BlocageMax.Ticks));
        return duree;
    }

    /// <summary>
    /// Liste blanche des droits qu'un collègue peut accorder sur place avec son code comptoir (« Faire autoriser ») : gestes du
    /// comptoir et de la réception seulement. Jamais le rôle de titulaire, l'administration, la trésorerie, les financements,
    /// le contrôle interne, les notifications, les sauvegardes, ni la délivrance d'un psychotrope (le registre nomme celui qui délivre).
    /// </summary>
    public static readonly IReadOnlySet<string> AutorisablesParCollegue = new HashSet<string>(StringComparer.Ordinal)
    {
        Permissions.CaisseVendre, Permissions.CaisseSupprimerLigne, Permissions.CaisseAnnulerVente, Permissions.CaisseRemise,
        Permissions.CaisseRetourEspeces, Permissions.CaisseOuvrirTiroir, Permissions.CaisseVendreSansOrdonnance, Permissions.CaisseCloturer,
        Permissions.CreditAccorder, Permissions.CreditDepasserPlafond,
        Permissions.StockConsulter, Permissions.StockDonner,
        Permissions.PrixModifier, Permissions.ProduitsBloquer,
        Permissions.AchatsReceptionner, Permissions.AchatsValiderEcart, Permissions.AchatsRetourFournisseur,
    };

    public static bool AutorisableParCollegue(string? permission) =>
        permission is not null && (AutorisablesParCollegue.Contains(permission) || ActionsAutorisables.ContainsKey(permission));

    /// <summary>
    /// Droit d'une seule action accordable par le code d'un collègue (décision du titulaire, 2026-11-15) : payer un grossiste depuis le
    /// tiroir, ou annuler ce paiement, avec le code d'un ayant droit « achats.regler ». Le droit achats.regler lui-même reste hors de la
    /// liste blanche : le reste du module Achats n'est jamais ouvert par code.
    /// </summary>
    public const string ActionReglementGrossisteTiroir = "achats.regler:tiroir_reglement_grossiste";

    /// <summary>
    /// Rattacher un volet Chifa malgré des différences, ou choisir le sort de l'écart de part organisme (recette du 2026-11-15, D1) :
    /// accordable par le code d'un ayant droit « prix.modifier » pour cette seule action, y compris sur une connexion par code comptoir
    /// (où prix.modifier, hors comptoir, n'est jamais proposé).
    /// </summary>
    public const string ActionEcartVoletChifa = "prix.modifier:volet_chifa_ecart";

    /// <summary>
    /// Choisir « S'aligner sur Chifa » ou « Garder notre prix » pour une ligne de volet Chifa dont l'écart atteint le seuil, à
    /// l'encaissement (2026-11-15) : accordable par le code d'un ayant droit « prix.modifier » pour cette seule action, y compris sur une
    /// connexion par code comptoir. Le reste de prix.modifier reste fermé au code comptoir.
    /// </summary>
    public const string ActionChoixPrixChifa = "prix.modifier:choix_prix_chifa";

    /// <summary>Action accordable par code → droit que le valideur doit avoir.</summary>
    public static readonly IReadOnlyDictionary<string, string> ActionsAutorisables = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        [ActionReglementGrossisteTiroir] = Permissions.AchatsRegler,
        [ActionEcartVoletChifa] = Permissions.PrixModifier,
        [ActionChoixPrixChifa] = Permissions.PrixModifier
    };

    /// <summary>Droit que le valideur doit détenir : celui de l'action, sinon la permission elle-même.</summary>
    public static string DroitDuValideur(string permission) => ActionsAutorisables.GetValueOrDefault(permission) ?? permission;

    /// <summary>Nom de la revendication du jeton : connexion par code, limitée au comptoir.</summary>
    public const string Revendication = "portee";
    public const string Portee = "comptoir";

    public static void VerifierFormat(string? code)
    {
        if (code is null || code.Length is < 4 or > 6 || !code.All(char.IsAsciiDigit))
            throw new RegleMetierException("Le code comptoir doit compter 4 à 6 chiffres.", "code_comptoir");
        if (code.Distinct().Count() == 1)
            throw new RegleMetierException("Code trop simple : évitez les chiffres tous identiques.");
        var montant = code.Zip(code.Skip(1), (a, b) => b - a).Distinct().ToList();
        if (montant is [1] or [-1])
            throw new RegleMetierException("Code trop simple : évitez les suites (1234, 9876…).");
    }

    /// <summary>
    /// Droits utilisables avec une connexion par code : vente, crédit, délivrance, consultation du stock, et don (le don partiel du reste
    /// à charge d'un médicament se fait à la caisse).
    /// </summary>
    public static bool Autorise(string permission) =>
        permission.StartsWith("caisse.", StringComparison.Ordinal)
        || permission.StartsWith("credit.", StringComparison.Ordinal)
        || permission is Permissions.PsychotropesDelivrer or Permissions.StockConsulter or Permissions.StockDonner;
}
