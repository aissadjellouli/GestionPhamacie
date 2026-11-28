namespace Pharma.Shared;

/// <summary>
/// Garde-fou de la caisse au clavier (2026-11-28) : un code-barres scanné alors que le curseur est dans un champ de montant (« Montant
/// reçu », « Fond de caisse ») y est tapé comme un nombre. Un montant en espèces de 10 000 000 DA ou plus (8 chiffres) est refusé par
/// le poste et par le serveur : c'est un code scanné, pas un montant.
/// </summary>
public static class LimitesCaisse
{
    public const decimal MontantEspecesMax = 10_000_000m;

    /// <summary>Vrai si le montant ne peut pas être une somme en espèces remise au comptoir.</summary>
    public static bool Invraisemblable(decimal? montant) => montant is { } m && m >= MontantEspecesMax;

    public const string MessageRecu = "Montant reçu invraisemblable (un code-barres scanné dans ce champ ?) : saisissez le montant remis par le client.";
    public const string MessageFond = "Fond de caisse invraisemblable (un code-barres scanné dans ce champ ?) : saisissez le montant compté.";
}
