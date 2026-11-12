namespace Pharma.Desktop.Services;

/// <summary>
/// Tâche longue laissée en cours au « Changer d'utilisateur » : le prochain employé connecté sur ce poste la voit dans un
/// bandeau et la reprend sans rien refaire. Type : reception (n° de la réception en attente sur le serveur), facture ou retour
/// (saisie en JSON). Droit : celui qu'il faut pour la reprendre. Comptage à l'aveugle : réservé à son auteur ou à qui décide des écarts.
/// </summary>
public sealed record TacheLaissee(string Type, string Libelle, string LaisseePar, string Droit, string Contenu, DateTime Le, bool ReserveAuteur = false);

/// <summary>
/// Tâches laissées sur ce poste (mémoire du poste : survit au changement d'utilisateur, pas au redémarrage du poste).
/// La vente de la Caisse n'y figure pas : elle est reprise directement à la Caisse (SessionPoste.PrendreVenteAReprendre).
/// </summary>
public sealed class TachesLaissees
{
    private readonly List<TacheLaissee> taches = [];
    private TacheLaissee? choisie;

    /// <summary>La liste a changé (le bandeau se met à jour).</summary>
    public event Action? Change;

    public void Ajouter(TacheLaissee t)
    {
        lock (taches) taches.Add(t);
        Change?.Invoke();
    }

    /// <summary>Tâches que cet employé peut reprendre (droit, et comptage à l'aveugle réservé).</summary>
    public IReadOnlyList<TacheLaissee> Visibles(SessionPoste session)
    {
        lock (taches)
            return taches.Where(t => session.A(t.Droit)
                && (!t.ReserveAuteur || t.LaisseePar == session.NomComplet || session.A("achats.valider_ecart"))).ToList();
    }

    /// <summary>L'employé clique « Reprendre » : la tâche quitte la liste et attend l'écran qui la rouvre.</summary>
    public void Choisir(TacheLaissee t)
    {
        lock (taches) taches.Remove(t);
        choisie = t;
        Change?.Invoke();
    }

    /// <summary>Lu une fois par l'écran concerné (Réceptions, Factures, Retours) à son ouverture.</summary>
    public TacheLaissee? PrendreChoisie(string type)
    {
        if (choisie?.Type != type) return null;
        var t = choisie;
        choisie = null;
        return t;
    }

    /// <summary>Écran où la tâche se reprend.</summary>
    public static string Ecran(TacheLaissee t) => t.Type switch
    {
        "reception" => "achats?onglet=receptions",
        "retour" => "achats?onglet=retours",
        "facture" => "factures",
        _ => ""
    };
}

/// <summary>Facture en préparation laissée : mode de l'éditeur, facture d'origine (correction, remplacement) et saisie.</summary>
public sealed record FactureLaissee(string Mode, long? OrigineId, int? OrigineVersion, string Preparation);
