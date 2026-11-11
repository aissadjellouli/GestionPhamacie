using Microsoft.Extensions.Logging;
using Pharma.Application.Commun;

namespace Pharma.Infrastructure.Systeme;

/// <summary>Erreurs non bloquantes inscrites au journal du serveur (fichiers de Journalisation).</summary>
public sealed class TraceErreurs(ILogger<TraceErreurs> logger) : ITraceErreurs
{
    public void Erreur(string message, Exception exception) => logger.LogError(exception, "{Message}", message);
}
