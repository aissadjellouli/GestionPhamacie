using Microsoft.Extensions.Configuration;
using Pharma.Api.Securite;

namespace Pharma.Architecture.Tests;

/// <summary>Options des jetons tirées d'une configuration d'essai (section « Jwt »), comme le fait le conteneur de l'API.</summary>
internal static class JetonsEssai
{
    public static Microsoft.Extensions.Options.IOptions<ReglagesJwt> Options(IConfiguration config) =>
        Microsoft.Extensions.Options.Options.Create(config.GetSection("Jwt").Get<ReglagesJwt>() ?? new ReglagesJwt());
}
