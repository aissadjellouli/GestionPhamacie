namespace Pharma.Application.SecondServeur;

/// <summary>
/// Source de vérité unique : l'officine a-t-elle un 2e serveur déclaré ? Oui si le réglage SecondServeur:AutreServeur est renseigné.
/// Sans 2e serveur déclaré, tout le mécanisme de bascule est inactif (surveillance du serveur écarté, lecture seule, saut des compteurs
/// à la promotion) : décision du titulaire du 2026-11-23.
/// </summary>
public static class DeuxServeursDeclares
{
    public static bool Est(string? autreServeur) => !string.IsNullOrWhiteSpace(autreServeur);
}
