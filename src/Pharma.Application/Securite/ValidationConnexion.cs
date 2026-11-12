namespace Pharma.Application.Securite;

/// <summary>État d'un compte relu à chaque appel authentifié : actif, mot de passe à changer, connexions révoquées depuis.</summary>
public sealed record EtatConnexion(bool Actif, bool DoitChangerMotDePasse, DateTime? JetonsRevoquesLe);

public interface IEtatConnexions
{
    /// <summary>Null : aucun compte de cet identifiant.</summary>
    Task<EtatConnexion?> LireAsync(long utilisateurId, CancellationToken ct = default);
}

/// <summary>
/// Une connexion (jeton) reste valable tant que le compte est actif et qu'elle a été ouverte après la dernière révocation
/// (mot de passe changé ou réinitialisé, compte désactivé, code comptoir effacé, base restaurée : 063_revocation_connexions.sql).
/// </summary>
public static class ValidationConnexion
{
    /// <summary>Motif du refus, ou null si la connexion reste valable. emiseLeUtc : date d'émission du jeton (à la seconde).</summary>
    public static string? Refus(EtatConnexion? etat, DateTime emiseLeUtc, bool marqueMotDePasseAChanger)
    {
        if (etat is null || !etat.Actif) return "Compte désactivé : reconnectez-vous avec un compte actif.";
        // Le jeton ne garde que la seconde de son émission : un jeton délivré dans la seconde de la révocation (nouveau mot de
        // passe choisi) reste valable
        if (etat.JetonsRevoquesLe is { } revoque && emiseLeUtc < revoque.AddTicks(-(revoque.Ticks % TimeSpan.TicksPerSecond)))
            return "Connexion fermée (mot de passe changé, code effacé ou base restaurée) : reconnectez-vous.";
        // Mot de passe réinitialisé après l'ouverture de la connexion : seul un jeton « mot de passe à changer » est accepté
        if (etat.DoitChangerMotDePasse && !marqueMotDePasseAChanger)
            return "Mot de passe réinitialisé : reconnectez-vous.";
        return null;
    }
}
