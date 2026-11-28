namespace Pharma.Desktop.Services;

/// <summary>
/// État des rubriques repliables des menus de gauche (composant GroupeMenu, décision du titulaire du 2026-11-27) : ce que l'employé
/// a replié ou déplié reste ainsi tant que le poste est ouvert, d'un écran à l'autre, **pour cet employé seulement** (décision du titulaire
/// du 2026-11-28 : l'employé suivant sur le même poste retrouve ses propres choix, ou l'état par défaut). Sans choix de l'employé : l'état par défaut
/// (déplié, ou pour un thème de l'Administration, déplié seulement s'il contient l'écran affiché). Une rubrique repliée qui contient
/// l'élément affiché se rouvre quand l'employé arrive sur un autre élément (autre adresse) : l'élément affiché reste toujours visible.
/// </summary>
/// <param name="employe">Employé connecté (clé de ses choix) ; null ou vide : un seul ensemble de choix (doublures des tests).</param>
public sealed class EtatMenus(Func<string?>? employe = null)
{
    /// <summary>Clé de l'employé connecté sur ce poste (super admin à part ; personne de connecté : écran de connexion).</summary>
    public static string? Employe(bool connecte, bool superAdmin, string? nomComplet) =>
        !connecte ? null : (superAdmin ? "super-admin:" : "employe:") + nomComplet;

    /// <summary>Choix par employé (clé : employé connecté ; jamais écrits sur le disque, perdus à la fermeture du poste).</summary>
    private readonly Dictionary<string, Dictionary<string, (bool Ouvert, string Adresse)>> parEmploye = new(StringComparer.Ordinal);

    private Dictionary<string, (bool Ouvert, string Adresse)> Choix()
    {
        var cle = employe?.Invoke() ?? "";
        if (!parEmploye.TryGetValue(cle, out var c)) parEmploye[cle] = c = new(StringComparer.Ordinal);
        return c;
    }

    /// <summary>Rubrique dépliée ou non, à l'adresse affichée.</summary>
    public bool Ouvert(string cle, bool parDefaut, bool contientActif, string adresse)
    {
        var choix = Choix();
        if (!choix.TryGetValue(cle, out var c)) return parDefaut;
        if (!c.Ouvert && contientActif && c.Adresse != adresse)
        {
            choix.Remove(cle);
            return true;
        }
        return c.Ouvert;
    }

    /// <summary>Choix de l'employé (clic, Entrée ou Espace sur le titre de la rubrique), mémorisé avec l'adresse où il l'a fait.</summary>
    public void Choisir(string cle, bool ouvert, string adresse) => Choix()[cle] = (ouvert, adresse);
}
