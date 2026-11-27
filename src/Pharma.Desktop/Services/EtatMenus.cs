namespace Pharma.Desktop.Services;

/// <summary>
/// État des rubriques repliables des menus de gauche (composant GroupeMenu, décision du titulaire du 2026-11-27) : ce que l'employé
/// a replié ou déplié reste ainsi tant que le poste est ouvert, d'un écran à l'autre. Sans choix de l'employé : l'état par défaut
/// (déplié, ou pour un thème de l'Administration, déplié seulement s'il contient l'écran affiché). Une rubrique repliée qui contient
/// l'élément affiché se rouvre quand l'employé arrive sur un autre élément (autre adresse) : l'élément affiché reste toujours visible.
/// </summary>
public sealed class EtatMenus
{
    private readonly Dictionary<string, (bool Ouvert, string Adresse)> choix = new(StringComparer.Ordinal);

    /// <summary>Rubrique dépliée ou non, à l'adresse affichée.</summary>
    public bool Ouvert(string cle, bool parDefaut, bool contientActif, string adresse)
    {
        if (!choix.TryGetValue(cle, out var c)) return parDefaut;
        if (!c.Ouvert && contientActif && c.Adresse != adresse)
        {
            choix.Remove(cle);
            return true;
        }
        return c.Ouvert;
    }

    /// <summary>Choix de l'employé (clic, Entrée ou Espace sur le titre de la rubrique), mémorisé avec l'adresse où il l'a fait.</summary>
    public void Choisir(string cle, bool ouvert, string adresse) => choix[cle] = (ouvert, adresse);
}
