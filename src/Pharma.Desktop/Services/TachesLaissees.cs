namespace Pharma.Desktop.Services;

/// <summary>
/// Tâches laissées sur ce poste : survivent au changement d'utilisateur. Facture et retour fournisseur survivent aussi au
/// redémarrage du poste quand le réglage de l'officine est « Chiffré » (FichierTachesLaissees : fichier chiffré du poste, relu à la
/// première connexion qui suit le démarrage, réécrit à chaque changement) ; « Désactivé » : mémoire seule, fichier supprimé.
/// La réception reste en mémoire : elle est déjà en attente sur le serveur.
/// La vente de la Caisse n'y figure pas : elle est reprise directement à la Caisse (SessionPoste.PrendreVenteAReprendre).
/// </summary>
public sealed class TachesLaissees
{
    private readonly List<TacheLaissee> taches = [];
    private readonly FichierTachesLaissees? fichier;
    private TacheLaissee? choisie;

    /// <param name="fichier">Fichier du poste (absent : mémoire seule). Rien n'est lu avant que le réglage soit connu (connexion).</param>
    public TachesLaissees(FichierTachesLaissees? fichier = null) => this.fichier = fichier;

    /// <summary>Réglage de l'officine en vigueur sur ce poste (null : pas encore lu depuis le démarrage).</summary>
    public string? Mode => fichier?.Mode;

    /// <summary>
    /// Réglage de l'officine lu à la connexion (et après son changement sur ce poste) : « chiffre » relit le fichier la première fois
    /// et le réécrit ; « desactive » le supprime, les tâches déjà sur le poste restent en mémoire jusqu'à sa fermeture.
    /// </summary>
    public void AppliquerReglage(string mode)
    {
        if (fichier is null) return;
        int ajoutees;
        lock (taches)
        {
            var ajout = fichier.Appliquer(mode, taches.ToList());
            taches.AddRange(ajout);
            ajoutees = ajout.Count;
        }
        if (ajoutees > 0) Change?.Invoke();
    }

    /// <summary>La liste a changé (le bandeau se met à jour).</summary>
    public event Action? Change;

    public void Ajouter(TacheLaissee t)
    {
        lock (taches)
        {
            taches.Add(t);
            fichier?.Ecrire(taches);
        }
        Change?.Invoke();
    }

    /// <summary>Tâches que cet employé peut reprendre (droit, et comptage à l'aveugle réservé).</summary>
    public IReadOnlyList<TacheLaissee> Visibles(SessionPoste session)
    {
        lock (taches)
            return taches.Where(t => session.A(t.Droit)
                && (!t.ReserveAuteur || t.LaisseePar == session.NomComplet || session.A("achats.valider_ecart"))).ToList();
    }

    /// <summary>L'employé clique « Reprendre » : la tâche quitte la liste (et le fichier du poste) et attend l'écran qui la rouvre.</summary>
    public void Choisir(TacheLaissee t)
    {
        lock (taches)
        {
            if (taches.Remove(t)) fichier?.Ecrire(taches);
        }
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
