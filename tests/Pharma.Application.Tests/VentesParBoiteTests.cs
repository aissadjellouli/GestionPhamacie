using Pharma.Application.Caisse;
using Pharma.Application.Mobile;
using Pharma.Domain.Commun;
using Pharma.Domain.Ventes;

namespace Pharma.Application.Tests;

internal sealed class VentesParBoiteEnMemoire : IVentesParBoite
{
    public long? VenteDeBoite { get; set; }
    public (long Produit, long? Lot, string? NumeroLot, DateOnly Depuis, long? Patient)? Demande { get; private set; }

    public Task<long?> VenteDeLaBoiteAsync(long boiteId, CancellationToken ct = default) => Task.FromResult(VenteDeBoite);
    public Task<IReadOnlyList<VenteAnnulable>> ParProduitAsync(long produitId, long? lotId, string? numeroLot, DateOnly depuis, long? patientId,
        CancellationToken ct = default)
    {
        Demande = (produitId, lotId, numeroLot, depuis, patientId);
        return Task.FromResult<IReadOnlyList<VenteAnnulable>>([]);
    }
    public Task<string?> NomProduitAsync(long produitId, CancellationToken ct = default) => Task.FromResult<string?>("Sintrom 4 mg cp");
}

public class VentesParBoiteTests
{
    private const string Etiquette = "2000000000107";
    private static readonly VenteAnnulable Vendue = new(12, "T-000012", new DateTime(2026, 11, 6, 10, 0, 0), "Vendeur Fictif", null,
        300, 300, 0, "especes", "validee", false, false, []);

    private static (RechercherVenteParBoite Cas, MobileEnMemoire Codes, VentesParBoiteEnMemoire Ventes) Monter()
    {
        var codes = new MobileEnMemoire();
        var ventes = new VentesParBoiteEnMemoire();
        return (new RechercherVenteParBoite(codes, ventes, new AnnulationsEnMemoire(Vendue), new HorlogeFixe(MobileEnMemoire.Aujourdhui)), codes, ventes);
    }

    [Fact]
    public async Task Code_barres_du_fabricant_ventes_sans_patient_des_7_derniers_jours()
    {
        var (cas, _, ventes) = Monter();
        var r = await cas.ExecuterAsync(MobileEnMemoire.Ean, null, null, null);
        Assert.False(r.Exacte);
        // Produit seulement (toutes ses boîtes portent ce code), ventes sans patient : jamais celles d'un autre patient
        Assert.Equal((7L, (long?)null, (string?)null, MobileEnMemoire.Aujourdhui.AddDays(-7), (long?)null), ventes.Demande);
    }

    [Fact]
    public async Task Client_nomme_ses_ventes_seulement_et_lot_saisi()
    {
        var (cas, _, ventes) = Monter();
        await cas.ExecuterAsync(MobileEnMemoire.Ean, 30, 4, " sin4-26 ");
        Assert.Equal((7L, (long?)null, "SIN4-26", MobileEnMemoire.Aujourdhui.AddDays(-30), (long?)4), ventes.Demande);
    }

    [Fact]
    public async Task Boite_identifiee_rattachee_a_sa_vente_la_vente_exacte()
    {
        var (cas, codes, ventes) = Monter();
        codes.Codes[Etiquette] = new CodeResolu(7, 70, "boite", "vendue", 500);
        ventes.VenteDeBoite = 12;
        var r = await cas.ExecuterAsync(Etiquette, null, null, null);
        Assert.True(r.Exacte);
        Assert.Equal("T-000012", Assert.Single(r.Ventes).Numero);
        Assert.Null(ventes.Demande);
    }

    [Fact]
    public async Task Boite_deja_rendue_refusee()
    {
        var (cas, codes, _) = Monter();
        codes.Codes[Etiquette] = new CodeResolu(7, 70, "boite", "retour_client", 500);
        var e = await Assert.ThrowsAsync<RegleMetierException>(() => cas.ExecuterAsync(Etiquette, null, null, null));
        Assert.Contains("déjà été rendue", e.Message);
    }

    [Fact]
    public async Task Boite_non_rattachee_recherche_par_produit_et_lot()
    {
        var (cas, codes, ventes) = Monter();
        codes.Codes[Etiquette] = new CodeResolu(7, 70, "boite", "en_stock", 500);
        var r = await cas.ExecuterAsync(Etiquette, null, null, null);
        Assert.False(r.Exacte);
        Assert.NotNull(r.Note);
        Assert.Equal((long?)70, ventes.Demande!.Value.Lot);
    }

    [Fact]
    public async Task Code_inconnu_refuse() =>
        await Assert.ThrowsAsync<RegleMetierException>(() => Monter().Cas.ExecuterAsync("1234567890", null, null, null));
}
