using Pharma.Domain.Commun;

namespace Pharma.Domain.Alertes;

public static class RegleAlerte
{
    /// <summary>Une alerte reportée doit réapparaître plus tard : jamais aujourd'hui ni dans le passé.</summary>
    public static void VerifierReport(DateOnly jusquAu, DateOnly aujourdhui)
    {
        if (jusquAu <= aujourdhui)
            throw new RegleMetierException(
                $"La date de report doit être postérieure à aujourd’hui ({aujourdhui:dd/MM/yyyy}).");
    }
}
