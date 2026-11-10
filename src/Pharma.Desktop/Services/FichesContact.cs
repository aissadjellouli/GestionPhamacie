namespace Pharma.Desktop.Services;

/// <summary>
/// Fiche contact ouverte depuis n'importe quel écran : un nom cliquable (LienContact) demande l'ouverture, la fenêtre unique
/// (FenetreContact, posée dans la mise en page) l'affiche. Une seule fiche à la fois, au-dessus de tout le reste.
/// </summary>
public sealed class FichesContact
{
    public (string Type, long Id, string Nom)? Ouverte { get; private set; }
    public event Action? Changee;

    public void Ouvrir(string type, long id, string nom)
    {
        Ouverte = (type, id, nom);
        Changee?.Invoke();
    }

    public void Fermer()
    {
        Ouverte = null;
        Changee?.Invoke();
    }
}
