using Pharma.Domain.Rapports;

namespace Pharma.Domain.Tests;

public class RegleRisquesTests
{
    private static JourneePerte J(int jour, decimal perte, params long[] presents) => new(new DateOnly(2026, 11, jour), perte, presents);

    [Fact]
    public void Pertes_qui_reviennent_quand_le_meme_employe_travaille_le_signalent()
    {
        // L'employé 7 est là les jours de perte, l'employé 3 tous les jours
        var jours = new[] { J(1, 500, 3, 7), J(2, 0, 3), J(3, 800, 3, 7), J(4, 0, 3), J(5, 300, 3, 7), J(6, 0, 3) };
        var p7 = RegleRisques.Correler(jours, 7);
        Assert.Equal((3, 3, 1600m, 533.33m, 0m, true), (p7.JoursPresent, p7.JoursAvecPerte, p7.PerteEnPresence, p7.MoyennePresent, p7.MoyenneAbsent, p7.Signale));
        var p3 = RegleRisques.Correler(jours, 3);
        Assert.False(p3.Signale);                     // présent tous les jours : jamais absent, rien à comparer
        Assert.Equal(266.67m, p3.MoyennePresent);
    }

    [Fact]
    public void Pas_de_signalement_sous_trois_jours_de_perte_ni_sans_ecart_net()
    {
        var deuxJours = new[] { J(1, 500, 7), J(2, 900, 7), J(3, 0, 3), J(4, 0, 3), J(5, 0, 3) };
        Assert.False(RegleRisques.Correler([J(1, 500, 7), J(2, 500, 7), J(3, 500, 7), J(4, 0, 3), J(5, 0, 3)], 7).Signale);   // 2 jours d'absence seulement
        Assert.False(RegleRisques.Correler(deuxJours, 7).Signale);

        // Pertes aussi fortes en son absence : pas de signalement
        var partout = new[] { J(1, 500, 7), J(2, 500, 7), J(3, 500, 7), J(4, 400, 3), J(5, 400, 3) };
        Assert.False(RegleRisques.Correler(partout, 7).Signale);
    }

    [Fact]
    public void Employe_absent_toute_la_periode()
    {
        var p = RegleRisques.Correler([J(1, 500, 3)], 9);
        Assert.Equal((0, 0, 0m, 0m, 500m, false), (p.JoursPresent, p.JoursAvecPerte, p.PerteEnPresence, p.MoyennePresent, p.MoyenneAbsent, p.Signale));
    }
}
