using Pharma.Application.Securite;

namespace Pharma.Infrastructure.Securite;

public sealed class VerificateurBcrypt : IVerificateurMotDePasse
{
    public bool Verifier(string motDePasse, string hash) => BCrypt.Net.BCrypt.Verify(motDePasse, hash);
}
