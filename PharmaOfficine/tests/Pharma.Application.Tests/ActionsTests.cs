using Pharma.Application.Alertes;
using Pharma.Application.Tracabilite;
using Pharma.Domain.Commun;

namespace Pharma.Application.Tests;

internal sealed class AlertesEspion : IAlertesEcriture
{
    public (long Id, DateOnly Date)? Report { get; private set; }
    public (long Id, string? Action, long Utilisateur)? Traitement { get; private set; }
    public bool Existe { get; init; } = true;

    public Task<bool> ReporterAsync(long alerteId, DateOnly jusquAu, CancellationToken ct = default)
    {
        Report = (alerteId, jusquAu);
        return Task.FromResult(Existe);
    }

    public Task<bool> TraiterAsync(long alerteId, string? actionPrise, long utilisateurId, CancellationToken ct = default)
    {
        Traitement = (alerteId, actionPrise, utilisateurId);
        return Task.FromResult(Existe);
    }
}

internal sealed class RappelFactice(ResultatRappel? resultat) : IRappelLot
{
    public string? LotRecu { get; private set; }
    public string? MotifRecu { get; private set; }

    public Task<ResultatRappel?> RappelerAsync(long produitId, string numeroLot, string motif, long utilisateurId, CancellationToken ct = default)
    {
        (LotRecu, MotifRecu) = (numeroLot, motif);
        return Task.FromResult(resultat);
    }
}

public class ActionsTests
{
    private static readonly DateOnly Aujourdhui = new(2026, 11, 8);

    [Fact]
    public async Task Report_dans_le_passe_n_ecrit_rien()
    {
        var espion = new AlertesEspion();
        var cas = new ReporterAlerte(espion, new HorlogeFixe(Aujourdhui));

        await Assert.ThrowsAsync<RegleMetierException>(() => cas.ExecuterAsync(4, Aujourdhui));
        Assert.Null(espion.Report);
    }

    [Fact]
    public async Task Report_valide_transmis()
    {
        var espion = new AlertesEspion();
        Assert.True(await new ReporterAlerte(espion, new HorlogeFixe(Aujourdhui)).ExecuterAsync(4, Aujourdhui.AddDays(7)));
        Assert.Equal((4L, Aujourdhui.AddDays(7)), espion.Report);
    }

    [Fact]
    public async Task Alerte_deja_close_renvoie_faux() =>
        Assert.False(await new TraiterAlerte(new AlertesEspion { Existe = false }).ExecuterAsync(4, null, 1));

    [Theory]
    [InlineData("  ", null)]
    [InlineData(" retour fournisseur ", "retour fournisseur")]
    public async Task Action_prise_nettoyee(string saisie, string? attendue)
    {
        var espion = new AlertesEspion();
        await new TraiterAlerte(espion).ExecuterAsync(4, saisie, 7);
        Assert.Equal((4L, attendue, 7L), espion.Traitement);
    }

    [Fact]
    public async Task Action_prise_tronquee()
    {
        var espion = new AlertesEspion();
        await new TraiterAlerte(espion).ExecuterAsync(4, new string('x', 500), 7);
        Assert.Equal(TraiterAlerte.LongueurMaxAction, espion.Traitement!.Value.Action!.Length);
    }

    [Theory]
    [InlineData(0, "G7710", "rappel")]
    [InlineData(9, " ", "rappel")]
    [InlineData(9, "G7710", "")]
    public async Task Rappel_incomplet_refuse_sans_rien_bloquer(long produitId, string lot, string motif)
    {
        var rappel = new RappelFactice(null);
        await Assert.ThrowsAsync<RegleMetierException>(() => new RappelerLot(rappel).ExecuterAsync(produitId, lot, motif, 1));
        Assert.Null(rappel.LotRecu);
    }

    [Fact]
    public async Task Rappel_d_un_lot_inconnu_refuse()
    {
        var ex = await Assert.ThrowsAsync<RegleMetierException>(() =>
            new RappelerLot(new RappelFactice(null)).ExecuterAsync(9, "INCONNU", "rappel labo", 1));
        Assert.Contains("INCONNU", ex.Message);
    }

    [Fact]
    public async Task Rappel_valide_et_decompte_des_patients()
    {
        var resultat = new ResultatRappel("Insuline", "G7710", LotsBloques: 1, BoitesEnStock: 3,
        [
            new VenteARappeler("Patient A", "0550", DateTime.UtcNow, "T-1", 1, Anonyme: false),
            new VenteARappeler("Patient A", "0550", DateTime.UtcNow, "T-2", 2, Anonyme: false),
            new VenteARappeler("Vente anonyme", null, DateTime.UtcNow, "T-3", 1, Anonyme: true)
        ]);
        var rappel = new RappelFactice(resultat);

        var r = await new RappelerLot(rappel).ExecuterAsync(9, " G7710 ", " rappel labo ", 1);

        Assert.Equal(("G7710", "rappel labo"), (rappel.LotRecu, rappel.MotifRecu));
        Assert.Equal(4, r.BoitesVendues);
        Assert.Equal(1, r.PatientsAContacter);
        Assert.Equal(1, r.VentesAnonymes);
    }
}
