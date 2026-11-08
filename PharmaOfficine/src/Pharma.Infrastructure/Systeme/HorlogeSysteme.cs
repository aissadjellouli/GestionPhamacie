using Pharma.Application.Commun;

namespace Pharma.Infrastructure.Systeme;

public sealed class HorlogeSysteme : IHorloge
{
    public DateTime MaintenantUtc => DateTime.UtcNow;
    public DateOnly Aujourdhui => DateOnly.FromDateTime(DateTime.Now);
}
