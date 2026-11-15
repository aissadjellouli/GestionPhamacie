namespace Pharma.Domain.Caisse;

/// <summary>
/// Ticket et reçu en PDF au format rouleau (2026-11-15) : largeurs du marché 57, 58, 76, 80 et 112 mm ; toute autre valeur
/// (ou aucune) revient à 80 mm. La hauteur suit le contenu (pas de saut de page au milieu d'un ticket).
/// </summary>
public static class FormatTicket
{
    public const int LargeurParDefaut = 80;
    public static readonly IReadOnlyList<int> Largeurs = [57, 58, 76, 80, 112];

    public static int Borner(int? largeurMm) => largeurMm is int l && Largeurs.Contains(l) ? l : LargeurParDefaut;

    /// <summary>Rouleau étroit (57/58 mm) : police plus petite, comme à l'impression du poste.</summary>
    public static bool Etroit(int largeurMm) => largeurMm < 70;
}
