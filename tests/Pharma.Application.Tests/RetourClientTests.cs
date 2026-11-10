using Pharma.Application.Caisse;
using Pharma.Application.Commun;
using Pharma.Application.Securite;
using Pharma.Domain.Commun;
using Pharma.Domain.Ventes;

namespace Pharma.Application.Tests;

internal sealed class RetoursEnMemoire(VenteRetournable vente) : IRetoursClient
{
    public RetourAEcrire? Ecrit { get; private set; }
    public Task<VenteRetournable?> LireAsync(long id, CancellationToken ct = default) => Task.FromResult<VenteRetournable?>(vente);
    public Task EnregistrerAsync(RetourAEcrire r, CancellationToken ct = default) { Ecrit = r; return Task.CompletedTask; }
}

public class RetourClientTests
{
    private static VenteRetournable Vente(string mode = "especes", decimal partOrganisme = 0) => new(
        new VenteAnnulable(20, "T-000020", DateTime.UtcNow, "Vendeur", null, 590, 590, partOrganisme, mode, "validee", false, false, []),
        [
            new LigneRetournable(1, 10, "Sirop fictif", "S1", 3, 0, 350, []),
            new LigneRetournable(2, 11, "Comprimé fictif", "C1", 2, 0, 240, [new BoiteVendue(100, "SN-A"), new BoiteVendue(101, "SN-B")])
        ]);

    private static (EnregistrerRetourClient Cas, RetoursEnMemoire Stock) Monter(VenteRetournable v, SessionsEnMemoire? sessions = null)
    {
        var stock = new RetoursEnMemoire(v);
        var codes = new CodesEnMemoire();
        codes.Comptes[8] = new CompteComptoir(8, "assistante", "Assistante Fictive", "hash:4826", 0, null);
        var droits = new DroitsParEmploye(new() { [5] = ["caisse.vendre"], [8] = ["caisse.vendre", "caisse.retour_especes"] });
        var journal = new JournalEspion();
        return (new EnregistrerRetourClient(stock, sessions ?? new SessionsEnMemoire(), droits,
            new VerificationCodeComptoir(codes, new VerificateurFactice(), new HorlogeFixe(new DateOnly(2026, 11, 9)), journal), journal), stock);
    }

    [Fact]
    public async Task Le_caissier_doit_faire_valider_le_retour()
    {
        var (cas, stock) = Monter(Vente());
        LigneRetourDemandee[] l = [new(1, 1, "stock", null)];
        await Assert.ThrowsAsync<RegleMetierException>(() => cas.ExecuterAsync(20, "Traitement arrêté", l, 5, "Caisse 1", null, null));
        var r = await cas.ExecuterAsync(20, "Traitement arrêté", l, 5, "Caisse 1", "assistante", "4826");
        Assert.Equal(("Assistante Fictive", 116.67m, 1), (r.ValidePar, r.ARendre, r.RemisEnStock));
        Assert.Equal(8, stock.Ecrit!.ValidePar);
    }

    [Fact]
    public async Task Boites_identifiees_cochees_et_destination_au_choix()
    {
        var (cas, stock) = Monter(Vente());
        await Assert.ThrowsAsync<RegleMetierException>(() =>
            cas.ExecuterAsync(20, "Mauvais dosage", [new(2, 1, "destruction", null)], 8, "Caisse 1", null, null));
        var r = await cas.ExecuterAsync(20, "Mauvais dosage", [new(2, 1, "destruction", [101]), new(1, 2, "stock", null)], 8, "Caisse 1", null, null);
        Assert.Equal((2, 1, 353.33m), (r.RemisEnStock, r.ADetruire, r.ARendre));
        Assert.Equal(DestinationRetour.Destruction, stock.Ecrit!.Lignes[0].Destination);
        Assert.Equal([101L], stock.Ecrit.Lignes[0].Boites);
    }

    [Fact]
    public async Task Plus_que_vendu_et_tiers_payant_refuses()
    {
        var (cas, _) = Monter(Vente());
        await Assert.ThrowsAsync<RegleMetierException>(() => cas.ExecuterAsync(20, "Erreur", [new(1, 4, "stock", null)], 8, "Caisse 1", null, null));
        var (tp, _) = Monter(Vente(partOrganisme: 280));
        await Assert.ThrowsAsync<RegleMetierException>(() => tp.ExecuterAsync(20, "Erreur", [new(1, 1, "stock", null)], 8, "Caisse 1", null, null));
    }

    [Fact]
    public async Task Credit_sans_argent_rendu_et_especes_sans_caisse_ouverte_refusees()
    {
        var (credit, stock) = Monter(Vente("credit"), new SessionsEnMemoire { Ouverte = null });
        var r = await credit.ExecuterAsync(20, "Erreur", [new(1, 3, "stock", null)], 8, "Caisse 1", null, null);
        Assert.Equal((0m, 350m), (r.ARendre, stock.Ecrit!.Montant));
        var (especes, _) = Monter(Vente(), new SessionsEnMemoire { Ouverte = null });
        await Assert.ThrowsAsync<RegleMetierException>(() => especes.ExecuterAsync(20, "Erreur", [new(1, 1, "stock", null)], 8, "Caisse 1", null, null));
    }
}
