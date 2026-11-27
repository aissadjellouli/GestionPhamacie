using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Routing;

namespace Pharma.Desktop.Services;

/// <summary>
/// Retour à l'écran précédent (bouton « ← Retour », Alt + ←, bouton précédent de la souris), partout dans le poste.
/// À chaque changement d'écran, l'écran quitté est empilé avec son état (onglet, filtres, page, fenêtre ouverte, fiche
/// contact) ; « Retour » le rouvre tel qu'il était. L'historique est propre à la session : vidé à la connexion, à la
/// déconnexion et au changement d'utilisateur, pour ne jamais revenir sur l'écran d'un autre employé.
/// </summary>
/// <remarks>
/// Un écran qui a un état à retrouver appelle <see cref="Suivre"/> (que retenir en partant) et <see cref="Restaurer"/>
/// (au démarrage, avant son premier chargement). Un écran qui doit agir avant d'être quitté (la Caisse met son panier en
/// attente) appelle <see cref="AvantDeQuitter"/> ; s'il répond « non », on reste sur l'écran.
/// </remarks>
public sealed class HistoriqueNavigation(NavigationManager nav, FichesContact fiches, SessionPoste session) : IDisposable
{
    private sealed record Entree(string Adresse, Dictionary<string, string> Etat, (string Type, long Id, string Nom)? Fiche);

    /// <summary>Écrans d'où l'on ne revient pas et qui vident l'historique (sécurité : connexion, mot de passe imposé).</summary>
    private static readonly string[] Coupures = ["connexion", "mot-de-passe"];

    /// <summary>Écrans jamais rouverts par l'historique (accès du super administrateur sous double validation).</summary>
    private static readonly string[] JamaisRouverts = ["admin/restauration", "admin/reprise"];

    private const int Profondeur = 50;

    private readonly List<Entree> pile = [];
    private readonly Dictionary<string, (string Chemin, Action<EtatEcran> Retenir)> suivis = [];
    private readonly Dictionary<string, (string Chemin, Func<string, Task<bool>> Action)> avantDeQuitter = [];
    private Dictionary<string, string>? aRestaurer;
    private (string Type, long Id, string Nom)? ficheARouvrir;
    private bool retourEnCours;
    private Entree? entreeReprise;
    private IDisposable? abonnement;

    /// <summary>L'historique a changé (le bouton « Retour » s'active, se désactive ou change de libellé).</summary>
    public event Action? Change;

    public bool PeutRevenir => pile.Count > 0;

    /// <summary>Nom de l'écran où « Retour » ramène, pour le libellé du bouton.</summary>
    public string? EcranPrecedent => pile.Count > 0 ? LibelleEcran(pile[^1].Adresse) : null;

    /// <summary>Branché une fois par la mise en page.</summary>
    public void Demarrer()
    {
        if (abonnement is not null) return;
        abonnement = nav.RegisterLocationChangingHandler(AvantChangementAsync);
        session.AvantFermeture += Vider;
    }

    /// <summary>Que retenir de cet écran quand on le quitte. Une clé par écran ou composant (ex. « factures »).</summary>
    public void Suivre(string cle, Action<EtatEcran> retenir) => suivis[cle] = (CheminCourant, retenir);

    /// <summary>
    /// L'état retenu de ce composant quand on y revient par « Retour » ; null sur une arrivée normale. Lu une seule fois : un
    /// composant rouvert ensuite sur le même écran (ex. l'éditeur de facture) repart à vide.
    /// </summary>
    public EtatEcran? Restaurer(string cle)
    {
        if (aRestaurer is null) return null;
        var prefixe = cle + ".";
        var cles = aRestaurer.Keys.Where(k => k.StartsWith(prefixe, StringComparison.Ordinal)).ToList();
        var valeurs = cles.ToDictionary(k => k[prefixe.Length..], k => aRestaurer[k]);
        foreach (var k in cles) aRestaurer.Remove(k);
        return valeurs.Count == 0 ? null : new EtatEcran(valeurs);
    }

    /// <summary>
    /// Action à faire avant tout changement d'adresse depuis cet écran (même chemin compris : un onglet d'Achats en remplace
    /// un autre) ; elle reçoit l'adresse visée et répond false pour rester (l'action a échoué et l'écran l'affiche).
    /// </summary>
    public void AvantDeQuitter(string cle, Func<string, Task<bool>> action) => avantDeQuitter[cle] = (CheminCourant, action);

    /// <summary>Composant fermé : ses actions de départ et son état ne sont plus demandés.</summary>
    public void Oublier(string cle)
    {
        avantDeQuitter.Remove(cle);
        suivis.Remove(cle);
    }

    /// <summary>Fiche contact à rouvrir après un retour (lue une fois par la fenêtre de fiche).</summary>
    public (string Type, long Id, string Nom)? PrendreFicheARouvrir()
    {
        var f = ficheARouvrir;
        ficheARouvrir = null;
        return f;
    }

    public void Retour()
    {
        if (pile.Count == 0) return;
        var e = pile[^1];
        pile.RemoveAt(pile.Count - 1);
        (aRestaurer, ficheARouvrir, retourEnCours, entreeReprise) = (e.Etat, e.Fiche, true, e);
        Change?.Invoke();
        nav.NavigateTo(e.Adresse);
    }

    public void Vider()
    {
        pile.Clear();
        (aRestaurer, ficheARouvrir) = (null, null);
        Change?.Invoke();
    }

    private async ValueTask AvantChangementAsync(LocationChangingContext contexte)
    {
        var depart = Relative(nav.Uri);
        var cible = Relative(contexte.TargetLocation);
        var retour = retourEnCours;
        retourEnCours = false;
        if (depart == cible) return;

        // Actions de départ de l'écran quitté (panier de la Caisse, réception en cours mis en attente) ; chacune juge l'adresse visée
        foreach (var (chemin, action) in avantDeQuitter.Values.ToList())
            if (chemin == Chemin(depart) && !await action(cible))
                {
                    contexte.PreventNavigation();
                    if (retour) RemettreRetourAnnule();
                    return;
                }

        if (EstIn(Coupures, cible)) { Vider(); return; }
        if (retour) return;

        // Arrivée normale : rien à restaurer ; l'écran quitté est empilé avec son état
        (aRestaurer, ficheARouvrir) = (null, null);
        if (EstIn(Coupures, depart) || EstIn(JamaisRouverts, depart)) return;
        var etat = new EtatEcran([]);
        foreach (var (cle, (chemin, retenir)) in suivis.ToList())
            if (chemin == Chemin(depart)) retenir(etat.Sous(cle));
        pile.Add(new Entree(depart, etat.Valeurs, fiches.Ouverte));
        if (pile.Count > Profondeur) pile.RemoveAt(0);
        Change?.Invoke();
    }

    // Retour refusé par l'écran quitté (ex. mise en attente impossible) : on reste, l'entrée reprise revient en haut de la pile
    private void RemettreRetourAnnule()
    {
        if (entreeReprise is not null) pile.Add(entreeReprise);
        (aRestaurer, ficheARouvrir, entreeReprise) = (null, null, null);
        Change?.Invoke();
    }

    private string CheminCourant => Chemin(Relative(nav.Uri));

    private string Relative(string adresse) => nav.ToBaseRelativePath(nav.ToAbsoluteUri(adresse).ToString()).TrimEnd('/');

    /// <summary>Chemin d'une adresse relative, sans paramètres (« achats?onglet=x » → « achats »).</summary>
    public static string Chemin(string relative) => relative.Split('?', '#')[0].TrimEnd('/');

    /// <summary>Valeur d'un paramètre d'une adresse relative (« achats?onglet=x », « onglet » → « x »), null s'il est absent.</summary>
    public static string? Parametre(string relative, string nom)
    {
        var i = relative.IndexOf('?');
        if (i < 0) return null;
        foreach (var paire in relative[(i + 1)..].Split('#')[0].Split('&'))
        {
            var (cle, valeur) = paire.IndexOf('=') is var e and >= 0 ? (paire[..e], paire[(e + 1)..]) : (paire, "");
            if (Uri.UnescapeDataString(cle) == nom) return Uri.UnescapeDataString(valeur.Replace('+', ' '));
        }
        return null;
    }

    private static bool EstIn(string[] ecrans, string relative) => ecrans.Contains(Chemin(relative));

    /// <summary>
    /// Nom affiché d'un écran, d'après son adresse : le titre (h1) de l'écran, sans sa rubrique (revue d'ergonomie du 2026-11-27, n° 10).
    /// Quelques titres trop longs pour le bouton « ← … » (200 px au plus, charte) sont raccourcis à leurs premiers mots ; le titre
    /// complet reste celui de l'écran. « écran précédent » seulement pour une adresse inconnue.
    /// </summary>
    public static string LibelleEcran(string relative) => Chemin(relative) switch
    {
        "" => "Alertes du jour",
        "accueil" => "Accueil",
        "caisse" => Parametre(relative, "rubrique") switch
        {
            "annulation" => "Annulation ou retour",
            "credit" => "Encaisser une dette",
            _ => "Caisse"
        },
        "caisse/etat" => "État de la caisse",
        "cloture" => Vrai(Parametre(relative, "intermediaire")) ? "Comptage intermédiaire"
            : Vrai(Parametre(relative, "releve")) ? "Relève de caisse" : "Clôture de caisse",
        "stock" => "Gestion du stock",
        "stock/registre" => "Registre des psychotropes",
        "stock/etiquettes" => "Étiquettes internes",
        "tracabilite" => "Traçabilité",
        "inventaire" => "Inventaire",
        "achats" => "Achats",
        "patients" => "Fichier des patients",
        "patients/droits-chifa" => "Droits expirés",
        "patients/attente" => "Patients en attente",
        "rapports" => "Rapports",
        "pilotage-financier" => "Tableau de bord financier",
        "factures" => "Factures",
        "tresorerie" => "Trésorerie",
        "dettes" => "Dettes et créances",
        "financements" => "Financements",
        "chifa" => "Chifa",
        "bordereaux-chifa" => "Bordereaux Chifa",
        "surveillance" => "Contrôle interne",
        "surveillance/risques" => "Pertes et vigilance",
        "surveillance/tiroir" => "Sorties du tiroir",
        "guide" => "Guide et retours",
        "mon-code" => "Mon code comptoir",
        "mot-de-passe" => "Mot de passe",
        "admin/conflits-facture-chifa" => "Conflits de facture Chifa",
        "admin/historique-chifa" => "Organismes repris",
        "admin/psychotropes" => "Psychotropes",
        "admin/restauration" => "Restauration",
        var c when c.StartsWith("admin", StringComparison.Ordinal) =>
            Pharma.Desktop.Composants.Mise.NavAdmin.LibelleEcran(c) ?? "Administration",
        _ => "écran précédent"
    };

    private static bool Vrai(string? valeur) => string.Equals(valeur, "true", StringComparison.OrdinalIgnoreCase) || valeur == "1";

    public void Dispose()
    {
        abonnement?.Dispose();
        session.AvantFermeture -= Vider;
    }
}

/// <summary>État d'un écran retenu pour le retour : des valeurs simples rangées par nom.</summary>
public sealed class EtatEcran
{
    private readonly string prefixe;
    internal Dictionary<string, string> Valeurs { get; }

    internal EtatEcran(Dictionary<string, string> valeurs, string prefixe = "") => (Valeurs, this.prefixe) = (valeurs, prefixe);

    internal EtatEcran Sous(string cle) => new(Valeurs, prefixe + cle + ".");

    public void Mettre(string nom, string? valeur) { if (!string.IsNullOrEmpty(valeur)) Valeurs[prefixe + nom] = valeur; }
    public void Mettre(string nom, long? valeur) => Mettre(nom, valeur?.ToString(System.Globalization.CultureInfo.InvariantCulture));
    public void Mettre(string nom, bool valeur) { if (valeur) Mettre(nom, "1"); }
    public void Mettre(string nom, DateOnly? valeur) => Mettre(nom, valeur?.ToString("yyyy-MM-dd"));

    public string? Texte(string nom) => Valeurs.GetValueOrDefault(prefixe + nom);
    public int? Entier(string nom) => int.TryParse(Texte(nom), out var v) ? v : null;
    public long? Long(string nom) => long.TryParse(Texte(nom), out var v) ? v : null;
    public bool Oui(string nom) => Texte(nom) == "1";
    public DateOnly? Date(string nom) => DateOnly.TryParseExact(Texte(nom), "yyyy-MM-dd", out var v) ? v : null;
}
