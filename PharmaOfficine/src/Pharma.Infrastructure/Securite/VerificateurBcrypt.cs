using Pharma.Application.Administration;
using Pharma.Application.Securite;

namespace Pharma.Infrastructure.Securite;

/// <summary>Mots de passe hachés avec BCrypt (facteur de coût 11, valeur par défaut de la bibliothèque).</summary>
public sealed class VerificateurBcrypt : IVerificateurMotDePasse, IHacheurMotDePasse
{
    public bool Verifier(string motDePasse, string hash) => BCrypt.Net.BCrypt.Verify(motDePasse, hash);

    public string Hacher(string motDePasse) => BCrypt.Net.BCrypt.HashPassword(motDePasse);
}
