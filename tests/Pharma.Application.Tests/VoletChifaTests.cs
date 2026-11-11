using Pharma.Application.Caisse;
using Pharma.Application.Commun;
using Pharma.Domain.Commun;
using Pharma.Domain.Ventes;

namespace Pharma.Application.Tests;

internal sealed class VoletsChifaEnMemoire : IVoletsChifa
{
    public Dictionary<string, ProduitChifa> Codes { get; } = [];
    public string? VenteExistante { get; set; }
    public Task<string?> VenteDuVoletAsync(string n, CancellationToken ct = default) => Task.FromResult(VenteExistante);
    public Task<IReadOnlyDictionary<string, ProduitChifa>> ProduitsAsync(IEnumerable<string> codes, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyDictionary<string, ProduitChifa>>(codes.Where(Codes.ContainsKey).ToDictionary(c => c, c => Codes[c]));
    public Task<(long Id, string Nom, string NumAssure)?> PatientAsync(string a, string s, CancellationToken ct = default) =>
        Task.FromResult<(long, string, string)?>(a == "690755003842" ? (12L, "Benali Amine", "690755003842/80") : null);
    public Task<long?> OrganismeAsync(string code, CancellationToken ct = default) => Task.FromResult<long?>(code == "CNAS" ? 1L : null);
    public Task<(string Produit, string? Ancien)?> AssocierAsync(string code, long produitId, long u, CancellationToken ct = default)
    {
        if (produitId != 18) return Task.FromResult<(string, string?)?>(null);
        var ancien = Codes.GetValueOrDefault(code)?.Nom;
        Codes[code] = new ProduitChifa(18, "VIT D3", 156.48m);
        return Task.FromResult<(string, string?)?>(("VIT D3", ancien));
    }
}

public class VoletChifaTests
{
    private const string Volet = "06930126;11/11/2026;690755003842;80;24;2;09/11/2026;07607,3,156.48;";

    [Fact]
    public async Task Volet_lu_assure_retrouve_code_inconnu_a_associer()
    {
        var volets = new VoletsChifaEnMemoire();
        var v = await new LireVoletChifa(volets, new DroitsFactices("caisse.vendre")).ExecuterAsync(Volet, 3);
        Assert.Equal(("06930126", 12L, 1L), (v.Qr.NumeroFacture, v.PatientId, v.OrganismeId));
        Assert.Null(v.Lignes.Single().Produit);                     // code 07607 pas encore associé

        var journal = new JournalEspion();
        await new AssocierCodeChifa(volets, new DroitsFactices("caisse.vendre"), journal).ExecuterAsync("07607", 18, 3);
        Assert.Equal(["code_chifa_associe"], journal.Actions);
        v = await new LireVoletChifa(volets, new DroitsFactices("caisse.vendre")).ExecuterAsync(Volet, 3);
        Assert.Equal(18, v.Lignes.Single().Produit!.Id);             // reconnu au volet suivant
    }

    [Fact]
    public async Task Volet_scanne_en_clavier_qwerty_relu()
    {
        // « 06930126;… » tapé par une douchette QWERTY sur un poste AZERTY : chiffres de la rangée AZERTY, « ; » devenu « m »
        var qwerty = new string(Volet.Select(c => c switch
        {
            '0' => 'à', '1' => '&', '2' => 'é', '3' => '"', '4' => '\'', '5' => '(', '6' => '-', '7' => 'è', '8' => '_', '9' => 'ç',
            ';' => 'm', '/' => '!', '.' => ':', ',' => ';', _ => c
        }).ToArray());
        var v = await new LireVoletChifa(new VoletsChifaEnMemoire(), new DroitsFactices("caisse.vendre")).ExecuterAsync(qwerty, 3);
        Assert.Equal(156.48m, v.Lignes.Single().PrixUnitaire);
    }

    [Fact]
    public async Task Volet_deja_repris_ou_illisible_refuse()
    {
        var lire = new LireVoletChifa(new VoletsChifaEnMemoire { VenteExistante = "T-000042" }, new DroitsFactices("caisse.vendre"));
        var ex = await Assert.ThrowsAsync<RegleMetierException>(() => lire.ExecuterAsync(Volet, 3));
        Assert.Contains("T-000042", ex.Message);
        await Assert.ThrowsAsync<RegleMetierException>(() => lire.ExecuterAsync("26/F-015695", 3));
        await Assert.ThrowsAsync<DroitRefuseException>(() =>
            new LireVoletChifa(new VoletsChifaEnMemoire(), new DroitsFactices("stock.consulter")).ExecuterAsync(Volet, 3));
        await Assert.ThrowsAsync<RegleMetierException>(() =>
            new AssocierCodeChifa(new VoletsChifaEnMemoire(), new DroitsFactices("caisse.vendre"), new JournalEspion()).ExecuterAsync("07607", 99, 3));
    }

    [Fact]
    public async Task Volet_garde_sur_la_vente_seulement_en_tiers_payant()
    {
        var ventes = new VentesEspion();
        var cas = new Encaisser(new CatalogueEnMemoire(), new PatientsEnMemoire(), ventes, new SessionsEnMemoire(), new DroitsFactices("caisse.vendre"),
            new HorlogeFixe(CatalogueEnMemoire.Aujourdhui), new ScansEnMemoire());
        var commande = new CommandeVente([new LigneCommande(2, 20, 1)], false, null, null, null, null, 0, ModePaiement.Especes, null, VoletChifa: Volet);
        var ex = await Assert.ThrowsAsync<RegleMetierException>(() => cas.ExecuterAsync(commande, 5, "Caisse 1"));
        Assert.Equal("volet_chifa", ex.Champ);
        Assert.Null(ventes.Recu);
    }
}
