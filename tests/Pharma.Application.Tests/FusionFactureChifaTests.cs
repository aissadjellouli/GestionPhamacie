using Pharma.Application.Caisse;
using Pharma.Application.Chifa;
using Pharma.Domain.Commun;
using Pharma.Domain.Securite;
using Pharma.Domain.Ventes;

namespace Pharma.Application.Tests;

/// <summary>Fusion du n° de facture Chifa (2026-11-15, script 101) : 8 chiffres, conflits entre l'ancienne colonne et chifa_facture.</summary>
public class FusionFactureChifaTests
{
    [Theory]
    [InlineData("06931126", true)]
    [InlineData("0693112", false)]     // 7 chiffres
    [InlineData("069311260", false)]   // 9 chiffres
    [InlineData("0693112A", false)]
    [InlineData(" 06931126", false)]   // format stocké : sans espace
    [InlineData(null, false)]
    public void Format_huit_chiffres(string? numero, bool attendu) => Assert.Equal(attendu, FormatFactureChifa.EstValide(numero));

    [Theory]
    [InlineData(" 06931126 ", "06931126")]
    public void Saisie_huit_chiffres_acceptee_espaces_retires(string saisie, string attendu) =>
        Assert.Equal(attendu, CanalOrganisme.NumeroFacture(saisie));

    [Theory]
    [InlineData("1234567")]
    [InlineData("123456789")]
    [InlineData("1234-567")]
    public void Saisie_hors_huit_chiffres_refusee(string saisie)
    {
        var ex = Assert.Throws<RegleMetierException>(() => CanalOrganisme.NumeroFacture(saisie));
        Assert.Equal("facture_chifa", ex.Champ);
    }

    [Theory]
    [InlineData("06931126", "06931127", true)]
    [InlineData("06931126", " 06931126 ", false)]   // identiques : pas de conflit
    [InlineData(null, "06931126", false)]           // ancienne colonne vide : recopie inutile
    [InlineData("06931126", null, false)]           // chifa_facture vide : recopie, pas de conflit
    [InlineData("  ", "06931126", false)]
    public void Conflit_detecte_si_les_deux_remplies_et_differentes(string? ancien, string? actuel, bool attendu) =>
        Assert.Equal(attendu, RegleConflitFactureChifa.EstConflit(ancien, actuel));

    [Fact]
    public async Task Trancher_ecrit_la_valeur_retenue_et_journalise()
    {
        var depot = new FauxConflits();
        var journal = new JournalEspion();
        var cas = new ConflitsFactureChifa(depot, new DroitsFactices(Permissions.AdminParametres), journal);
        Assert.True(await cas.TrancherAsync(7, " 06931127 ", 1));
        Assert.Equal((7L, "06931127"), depot.Ecrit);
        Assert.Equal([ConflitsFactureChifa.ActionTrancher], journal.Actions);
    }

    [Theory]
    [InlineData("")]
    [InlineData("0693112")]
    public async Task Trancher_refuse_une_valeur_hors_huit_chiffres(string valeur)
    {
        var depot = new FauxConflits();
        var ex = await Assert.ThrowsAsync<RegleMetierException>(() => new ConflitsFactureChifa(depot, new DroitsFactices(Permissions.AdminParametres), new JournalEspion()).TrancherAsync(7, valeur, 1));
        Assert.Equal("valeur_retenue", ex.Champ);
        Assert.Null(depot.Ecrit);
    }

    [Fact]
    public async Task Trancher_une_vente_sans_conflit_renvoie_faux() =>
        Assert.False(await new ConflitsFactureChifa(new FauxConflits(), new DroitsFactices(Permissions.AdminParametres), new JournalEspion()).TrancherAsync(99, "06931127", 1));

    [Fact]
    public async Task Trancher_un_numero_deja_pris_est_refuse()
    {
        var depot = new FauxConflits { Issue = IssueTrancheFacture.NumeroDejaPris };
        var ex = await Assert.ThrowsAsync<RegleMetierException>(() => new ConflitsFactureChifa(depot, new DroitsFactices(Permissions.AdminParametres), new JournalEspion()).TrancherAsync(7, "06931127", 1));
        Assert.Equal("valeur_retenue", ex.Champ);
    }

    private sealed class FauxConflits : IConflitsFactureChifa
    {
        public (long, string)? Ecrit;
        public IssueTrancheFacture Issue = IssueTrancheFacture.Ecrit;
        private readonly ConflitFactureChifa conflit = new(7, "T-000007", new DateTime(2026, 11, 15), "06931126", "06931127", null);

        public Task<IReadOnlyList<ConflitFactureChifa>> ListerAsync(bool aTrancher, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<ConflitFactureChifa>>([conflit]);
        public Task<ConflitFactureChifa?> LireAsync(long venteId, CancellationToken ct = default) =>
            Task.FromResult(venteId == conflit.VenteId ? conflit : null);
        public Task<IssueTrancheFacture> TrancherAsync(long venteId, string valeurRetenue, long utilisateurId, CancellationToken ct = default)
        {
            if (Issue == IssueTrancheFacture.Ecrit) Ecrit = (venteId, valeurRetenue);
            return Task.FromResult(Issue);
        }
    }
}
