using Pharma.Shared;

namespace Pharma.Desktop.Services;

/// <summary>
/// Compteurs du travail en attente, affichés dans le menu du haut et le menu de gauche (docs/navigation.md) :
/// réceptions en attente, bons de commande en brouillon, factures à encaisser (dont échues), sommes dues en alerte (créances et dettes, comme le compteur de l'écran Dettes et créances).
/// Chacun n'est lu que si l'employé a le droit de l'écran ; relus au changement d'écran, au plus une fois toutes les 30 secondes
/// (et aussitôt à la connexion). Un compte illisible (droit refusé, serveur injoignable) reste vide : rien n'est affiché.
/// </summary>
public sealed class CompteursAttente(ApiClient api, SessionPoste session)
{
    private static readonly TimeSpan Delai = TimeSpan.FromSeconds(30);
    private DateTime derniereLecture = DateTime.MinValue;
    private string? utilisateur;
    private bool enCours;

    public int? Receptions { get; private set; }
    public int? Brouillons { get; private set; }
    public int? AEncaisser { get; private set; }
    public int? Echues { get; private set; }
    public int? SommesEnAlerte { get; private set; }

    /// <summary>Travail en attente d'un domaine du menu du haut (somme de ses compteurs) ; urgent si une facture est échue.</summary>
    public (int Nombre, bool Urgent)? Domaine(string id) => id switch
    {
        "achats" when (Receptions ?? 0) + (Brouillons ?? 0) > 0 => ((Receptions ?? 0) + (Brouillons ?? 0), false),
        "finances" when (AEncaisser ?? 0) + (SommesEnAlerte ?? 0) > 0 => ((AEncaisser ?? 0) + (SommesEnAlerte ?? 0), (Echues ?? 0) + (SommesEnAlerte ?? 0) > 0),
        _ => null
    };

    /// <summary>Compteur d'un écran du menu de gauche (lien de l'écran).</summary>
    public (int Nombre, bool Urgent)? Ecran(string lien) => lien switch
    {
        "factures" when AEncaisser is > 0 => (AEncaisser.Value, Echues is > 0),
        "dettes" when SommesEnAlerte is > 0 => (SommesEnAlerte.Value, true),
        _ => null
    };

    public event Action? Change;

    public async Task ActualiserAsync(bool forcer = false)
    {
        if (!session.EstConnecte || session.SuperAdmin) { Vider(); return; }
        // Nouvel employé, ou ses droits viennent d'arriver (ils sont chargés après la connexion) : relecture aussitôt
        var cle = $"{session.NomComplet}|{session.Permissions.Count}";
        if (utilisateur != cle) { Vider(); utilisateur = cle; forcer = true; }
        if (enCours || (!forcer && DateTime.UtcNow - derniereLecture < Delai)) return;
        enCours = true;
        try
        {
            derniereLecture = DateTime.UtcNow;
            if (session.A("achats.receptionner"))
            {
                var (attentes, e) = await api.ReceptionsEnAttenteAsync();
                Receptions = e is null ? attentes.Count : null;
            }
            if (session.A("achats.commander"))
            {
                var (bons, e) = await api.CommandesAchatAsync(1, 5, statut: "brouillon");
                Brouillons = e is null ? bons?.Total : null;
            }
            if (session.A("factures.gerer"))
            {
                var (aEncaisser, e1) = await api.FacturesPageAsync(null, null, "a_encaisser", null, null, 1, 5);
                var (echues, e2) = await api.FacturesPageAsync(null, null, "echues", null, null, 1, 5);
                (AEncaisser, Echues) = (e1 is null ? aEncaisser?.Page.Total : null, e2 is null ? echues?.Page.Total : null);
            }
            if (session.A("tresorerie.gerer"))
            {
                var (suivi, e) = await api.SuiviDettesAsync();
                SommesEnAlerte = e is null ? suivi?.Alertes.Sum(a => a.Nombre) : null;
            }
        }
        finally { enCours = false; }
        Change?.Invoke();
    }

    private void Vider()
    {
        (Receptions, Brouillons, AEncaisser, Echues, SommesEnAlerte, utilisateur) = (null, null, null, null, null, null);
        derniereLecture = DateTime.MinValue;
    }
}
