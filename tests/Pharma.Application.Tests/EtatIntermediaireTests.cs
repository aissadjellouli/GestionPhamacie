using Pharma.Application.Caisse;
using Pharma.Domain.Caisse;
using Pharma.Domain.Commun;

namespace Pharma.Application.Tests;

internal sealed class EtatIntermediaireEnMemoire : IEtatIntermediaireCaisse
{
    public List<ComptageIntermediaire> Comptages { get; } = [];
    public ResumeCaisse Resume { get; set; } = new(3, 1200, 0, 500, 900, [("Vendeur A", 2), ("Dr Titulaire", 1)]);

    public Task<ResumeCaisse> ResumeAsync(long sessionId, CancellationToken ct = default) => Task.FromResult(Resume);
    public Task<IReadOnlyList<ComptageIntermediaire>> ComptagesAsync(long sessionId, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<ComptageIntermediaire>>(Comptages);
    public Task EnregistrerAsync(long sessionId, long utilisateurId, Comptage comptage, decimal attendu, decimal ecart, CancellationToken ct = default)
    {
        Comptages.Add(new ComptageIntermediaire(DateTime.Now, $"Employé {utilisateurId}", comptage.Total, attendu, ecart));
        return Task.CompletedTask;
    }
}

public class EtatIntermediaireTests
{
    private static readonly Dictionary<string, decimal> Tiroir = new() { ["b1000"] = 7, ["b500"] = 1 };   // 7 500 DA

    [Fact]
    public async Task Comptage_sans_cloture_enregistre_et_cache_attendu_et_ecart_sans_le_droit()
    {
        var sessions = new SessionsEnMemoire { Especes = 3000 };   // attendu = 5 000 + 3 000 = 8 000
        var etat = new EtatIntermediaireEnMemoire();
        var journal = new JournalEspion();
        var cas = new GererEtatIntermediaire(sessions, etat, new DroitsFactices("caisse.vendre"), journal);

        var r = await cas.CompterAsync(5, "Caisse 1", Tiroir);
        Assert.Equal(7500, r.Compte);
        Assert.Null(r.Attendu);
        Assert.Null(r.Ecart);
        Assert.Null(sessions.Cloture);                                   // la caisse reste ouverte
        var c = Assert.Single(etat.Comptages);
        Assert.Equal((8000m, -500m), (c.Attendu, c.Ecart));              // gardés pour qui peut voir les écarts
        Assert.Equal("comptage_intermediaire", Assert.Single(journal.Actions));

        var lu = await cas.LireAsync(5, "Caisse 1");
        Assert.Null(Assert.Single(lu.Comptages).Ecart);
        Assert.False(lu.PeutVoirEcart);
    }

    [Fact]
    public async Task Avec_le_droit_de_voir_les_ecarts_l_ecart_est_donne_apres_le_comptage()
    {
        var sessions = new SessionsEnMemoire { Especes = 3000 };
        var cas = new GererEtatIntermediaire(sessions, new EtatIntermediaireEnMemoire(), new DroitsFactices("caisse.vendre", "caisse.voir_ecart"), new JournalEspion());

        var r = await cas.CompterAsync(5, "Caisse 1", Tiroir);
        Assert.Equal((8000m, -500m, false), (r.Attendu, r.Ecart, r.AuDelaDuSeuil));
        Assert.Equal(-500m, Assert.Single((await cas.LireAsync(5, "Caisse 1")).Comptages).Ecart);
    }

    [Fact]
    public async Task Sans_caisse_ouverte_rien_a_compter()
    {
        var cas = new GererEtatIntermediaire(new SessionsEnMemoire { Ouverte = null }, new EtatIntermediaireEnMemoire(), new DroitsFactices(), new JournalEspion());
        await Assert.ThrowsAsync<RegleMetierException>(() => cas.LireAsync(5, "Caisse 1"));
        await Assert.ThrowsAsync<RegleMetierException>(() => cas.CompterAsync(5, "Caisse 1", Tiroir));
    }
}
