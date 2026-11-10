using Pharma.Application.Commun;
using Pharma.Application.Mobile;
using Pharma.Domain.Commun;

namespace Pharma.Application.Tests;

internal sealed class MobileEnMemoire : IMobile
{
    public static readonly DateOnly Aujourdhui = new(2026, 11, 8);
    private const char Gs = (char)29;
    public const string Ean = "6130000000107";
    public static readonly string DataMatrix = $"01061300000001071728013110SIN4-26{Gs}21SERIE01";

    public Dictionary<string, CodeResolu> Codes { get; } = new() { [Ean] = new(7, 70, "produit", null) };
    public CodeResolu? ParDataMatrix { get; set; } = new(7, 70, "boite", "en_stock");
    public List<LotFiche> Lots { get; } = [new(70, "SIN4-26", Aujourdhui.AddYears(1), false, 20, false, null, "Grossiste A", "ok")];
    public LotAConfirmer? Lot { get; set; } = new(70, "Sintrom", "SIN4-26", true);
    public (long Lot, DateOnly Date)? Confirme { get; private set; }
    public List<ScanReception> Scans { get; } = [];

    public Task<CodeResolu?> ResoudreCodeAsync(string code, CancellationToken ct = default) => Task.FromResult(Codes.GetValueOrDefault(code));
    public Task<CodeResolu?> ResoudreDataMatrixAsync(string gtin, string? lot, string? serie, CancellationToken ct = default) => Task.FromResult(ParDataMatrix);
    public Task<FicheProduit?> FicheAsync(long produitId, CancellationToken ct = default) =>
        Task.FromResult<FicheProduit?>(new(produitId, "Sintrom 4 mg cp", "Acénocoumarol", "4 mg", "comprimé", 300, Lots.Sum(l => l.Quantite), 1, "C-03-1", null, null, Lots));
    public Task<LotAConfirmer?> LotAsync(long lotId, CancellationToken ct = default) => Task.FromResult(Lot);
    public Task ConfirmerPeremptionAsync(long lotId, DateOnly date, CancellationToken ct = default) { Confirme = (lotId, date); return Task.CompletedTask; }
    public Task<long> AjouterScanAsync(NouveauScanReception s, CancellationToken ct = default)
    {
        var id = Scans.Count + 1L;
        Scans.Add(new(id, s.ProduitId, "Sintrom 4 mg cp", s.Gtin, s.NumeroLot, s.Peremption, s.NumeroSerie, "Préparateur", DateTime.UtcNow));
        return Task.FromResult(id);
    }
    public Task<IReadOnlyList<ScanReception>> ScansEnAttenteAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<ScanReception>>(Scans.ToList());
    public Task<bool> SupprimerScanAsync(long id, CancellationToken ct = default) => Task.FromResult(Scans.RemoveAll(s => s.Id == id) == 1);
    public List<long> Remis { get; } = [];
    public Task<int> RemettreEnAttenteAsync(IReadOnlyList<long> ids, CancellationToken ct = default) { Remis.AddRange(ids); return Task.FromResult(ids.Count); }
    public Task<IReadOnlyList<ScanReception>> ImporterScansAsync(CancellationToken ct = default)
    {
        var liste = Scans.ToList();
        Scans.Clear();
        return Task.FromResult<IReadOnlyList<ScanReception>>(liste);
    }
}

public class MobileApplicationTests
{
    private static IdentifierCode Identifier(MobileEnMemoire m) =>
        new(m, new DroitsFactices("stock.consulter"), new HorlogeFixe(MobileEnMemoire.Aujourdhui));

    [Fact]
    public async Task Code_fabricant_donne_le_produit_sans_lot()
    {
        var f = await Identifier(new MobileEnMemoire()).ExecuterAsync(MobileEnMemoire.Ean, 2);
        Assert.Equal(("produit", (long?)null), (f.Type, f.LotId));
        Assert.Equal("C-03-1", f.Produit.Emplacement);
    }

    [Fact]
    public async Task Boite_d_un_lot_bloque_ou_perime_ou_a_dater_est_signalee()
    {
        var m = new MobileEnMemoire();
        m.Lots[0] = m.Lots[0] with { Bloque = true, MotifBlocage = "Rappel ANPP", DatePeremption = MobileEnMemoire.Aujourdhui, DateAVerifier = true };
        var f = await Identifier(m).ExecuterAsync(MobileEnMemoire.DataMatrix, 2);
        Assert.Equal(70, f.LotId);
        Assert.Equal(3, f.Alertes.Count);
        Assert.Contains(f.Alertes, a => a.Contains("bloqué"));
        Assert.DoesNotContain(f.Alertes, a => a.Contains("ANPP"));            // motif libre : jamais vers le téléphone
        Assert.All(f.Produit.Lots, l => Assert.Null(l.MotifBlocage));
    }

    [Fact]
    public async Task Boite_deja_vendue_signalee()
    {
        var m = new MobileEnMemoire { ParDataMatrix = new(7, 70, "boite", "vendue") };
        var f = await Identifier(m).ExecuterAsync(MobileEnMemoire.DataMatrix, 2);
        Assert.Contains(f.Alertes, a => a.Contains("vendue"));
    }

    [Fact]
    public async Task Code_inconnu_refuse_avec_message_clair()
    {
        var ex = await Assert.ThrowsAsync<RegleMetierException>(() => Identifier(new MobileEnMemoire()).ExecuterAsync("123", 2));
        Assert.Contains("inconnu", ex.Message);
    }

    [Fact]
    public async Task Date_confirmee_seulement_pour_un_lot_a_verifier_et_journalisee()
    {
        var (m, journal) = (new MobileEnMemoire(), new JournalEspion());
        var cas = new ConfirmerPeremption(m, new DroitsFactices("stock.ajuster"), new HorlogeFixe(MobileEnMemoire.Aujourdhui), journal);
        await cas.ExecuterAsync(70, new DateOnly(2028, 1, 31), 2);
        Assert.Equal((70L, new DateOnly(2028, 1, 31)), m.Confirme);
        Assert.Equal(["peremption_verifiee"], journal.Actions);

        m.Lot = m.Lot! with { DateAVerifier = false };
        await Assert.ThrowsAsync<RegleMetierException>(() => cas.ExecuterAsync(70, new DateOnly(2028, 1, 31), 2));
        await Assert.ThrowsAsync<DroitRefuseException>(() =>
            new ConfirmerPeremption(m, new DroitsFactices("stock.consulter"), new HorlogeFixe(MobileEnMemoire.Aujourdhui), journal).ExecuterAsync(70, new DateOnly(2028, 1, 31), 3));
    }

    [Fact]
    public async Task Scan_de_reception_lit_le_data_matrix_et_refuse_la_meme_boite_deux_fois()
    {
        var (m, achats) = (new MobileEnMemoire(), new AchatsEnMemoire());
        achats.Gtins["06130000000107"] = (7, "Sintrom 4 mg cp");
        var cas = new ScannerBoiteReception(m, achats, new DroitsFactices("achats.receptionner"));

        var s = await cas.ExecuterAsync(MobileEnMemoire.DataMatrix, 2);
        Assert.Equal(("SIN4-26", (DateOnly?)new DateOnly(2028, 1, 31), "SERIE01"), (s.NumeroLot, s.Peremption, s.NumeroSerie));
        await Assert.ThrowsAsync<RegleMetierException>(() => cas.ExecuterAsync(MobileEnMemoire.DataMatrix, 2));

        await cas.ExecuterAsync(MobileEnMemoire.Ean, 2);            // boîte sans Data Matrix : produit seul
        Assert.Equal(2, m.Scans.Count);
    }

    [Fact]
    public async Task Boite_deja_etiquetee_refusee_a_la_reception()
    {
        var m = new MobileEnMemoire();
        m.Codes["2000000001289"] = new(7, 70, "boite", "en_stock");
        await Assert.ThrowsAsync<RegleMetierException>(() =>
            new ScannerBoiteReception(m, new AchatsEnMemoire(), new DroitsFactices("achats.receptionner")).ExecuterAsync("2000000001289", 2));
    }

    [Fact]
    public void Import_regroupe_par_produit_lot_et_peremption()
    {
        var d = new DateOnly(2028, 1, 31);
        ScanReception S(long id, string? lot, string? serie) => new(id, 7, "Sintrom", serie is null ? null : "06130000000107", lot, lot is null ? null : d, serie, "P", DateTime.UtcNow);
        var lignes = ImporterScansReception.Regrouper([S(1, "SIN4-26", "A"), S(2, "SIN4-26", "B"), S(3, null, null), S(4, null, null)]);

        Assert.Equal(2, lignes.Count);
        Assert.Contains(lignes, l => l.NumeroLot == "SIN4-26" && l.Quantite == 2 && l.Series.SequenceEqual(["A", "B"]));
        Assert.Contains(lignes, l => l.NumeroLot is null && l.Quantite == 2 && l.Series.Count == 0);
        Assert.Contains(lignes, l => l.NumeroLot == "SIN4-26" && l.ScanIds!.SequenceEqual([1L, 2L]));
    }
}

public class MobileCodeFabricantTests
{
    [Fact]
    public async Task Code_fabricant_signale_les_lots_bloques_et_perimes_du_produit()
    {
        var m = new MobileEnMemoire();
        m.Lots.Add(new(71, "VD3", MobileEnMemoire.Aujourdhui.AddYears(1), false, 5, true, "Rappel ANPP", "Grossiste B", "ok"));
        m.Lots.Add(new(72, "OLD1", MobileEnMemoire.Aujourdhui.AddDays(-3), false, 2, false, null, "Grossiste B", "perime"));
        var f = await new IdentifierCode(m, new DroitsFactices("stock.consulter"), new HorlogeFixe(MobileEnMemoire.Aujourdhui))
            .ExecuterAsync(MobileEnMemoire.Ean, 2);
        Assert.Equal(2, f.Alertes.Count);
        Assert.Contains(f.Alertes, a => a.Contains("VD3") && a.Contains("bloqué"));
        Assert.Contains(f.Alertes, a => a.Contains("OLD1") && a.Contains("périmé"));
    }
}

public class RemettreScansTests
{
    [Fact]
    public async Task Reception_abandonnee_remet_les_scans_en_attente_sans_doublon()
    {
        var m = new MobileEnMemoire();
        var n = await new RemettreScansEnAttente(m, new DroitsFactices("achats.receptionner")).ExecuterAsync([4, 5, 4], 2);
        Assert.Equal(2, n);
        Assert.Equal([4L, 5L], m.Remis);
        Assert.Equal(0, await new RemettreScansEnAttente(m, new DroitsFactices("achats.receptionner")).ExecuterAsync([], 2));
        await Assert.ThrowsAsync<DroitRefuseException>(() =>
            new RemettreScansEnAttente(m, new DroitsFactices("caisse.vendre")).ExecuterAsync([4], 3));
    }
}
