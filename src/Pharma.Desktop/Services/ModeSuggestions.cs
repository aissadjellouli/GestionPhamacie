namespace Pharma.Desktop.Services;

/// <summary>Mode d'un champ avec propositions pendant la frappe (composant ChampSuggestions, docs/charte-graphique.md).</summary>
public enum ModeSuggestions
{
    /// <summary>Une entité à choisir (patient, médecin…) : « Aucun … pour « … » », lien « Changer » après le choix.</summary>
    ChoixObligatoire,
    /// <summary>Valeurs déjà saisies proposées ; le texte tapé reste accepté.</summary>
    ValeurLibre
}
