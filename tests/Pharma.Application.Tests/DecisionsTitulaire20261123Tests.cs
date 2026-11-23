using Pharma.Application.Stock;
using Pharma.Domain.Commun;
using Pharma.Domain.Factures;

namespace Pharma.Application.Tests;

/// <summary>Décisions du titulaire du 2026-11-23 : don d'un lot bloqué ou périmé, balance du registre, année d'une facture corrigée.</summary>
public class DecisionsTitulaire20261123Tests
{
    [Fact]
    public async Task Don_d_un_lot_bloque_ou_perime_refuse_les_autres_sorties_restent_possibles()
    {
        var sorties = new SortiesEnMemoire();
        var horloge = new HorlogeFixe(new DateOnly(2026, 11, 23));
        var donneur = new DeclarerSortieStock(sorties, new DroitsFactices("stock.donner"), null, horloge);

        sorties.Lot = sorties.Lot with { Bloque = true };
        var bloque = await Assert.ThrowsAsync<RegleMetierException>(() => donneur.ExecuterAsync(9, 1, "echantillon", "Personne dans le besoin", 3));
        Assert.Contains("bloqué", bloque.Message);

        sorties.Lot = sorties.Lot with { Bloque = false, DatePeremption = new DateOnly(2026, 11, 23) };
        var perime = await Assert.ThrowsAsync<RegleMetierException>(() => donneur.ExecuterAsync(9, 1, "echantillon", "Personne dans le besoin", 3));
        Assert.Contains("périmé", perime.Message);
        Assert.Null(sorties.Ecrite);

        // Le même lot périmé reste sortable en périmé ou cassé
        sorties.Lot = sorties.Lot with { Bloque = true };
        var declarant = new DeclarerSortieStock(sorties, new DroitsFactices("stock.declarer_perime"), null, horloge);
        await declarant.ExecuterAsync(9, 1, "perime", null, 3);
        await declarant.ExecuterAsync(9, 1, "casse", "Boîte tombée", 3);
        Assert.Equal("casse", sorties.Ecrite!.Motif);

        // Lot sain et valide : le don passe
        sorties.Lot = sorties.Lot with { Bloque = false, DatePeremption = new DateOnly(2026, 11, 24) };
        await donneur.ExecuterAsync(9, 1, "echantillon", "Personne dans le besoin", 3);
        Assert.Equal("echantillon", sorties.Ecrite!.Motif);
    }

    [Fact]
    public void Balance_mensuelle_le_solde_reporte_est_le_solde_de_fin_du_mois_precedent()
    {
        // Janvier : ouverture 10, +5, -3 -> fin 12. Février : +0, -2 -> fin 10 (reporté 12). Avril (mars sans mouvement) : +1 -> fin 11 (reporté 10).
        var mois = new List<BalanceMensuelle>
        {
            new(1, "Alprazolam", 2026, 1, 5, 3, 12, 10),
            new(1, "Alprazolam", 2026, 2, 0, 2, 10),
            new(1, "Alprazolam", 2026, 4, 1, 0, 11),
        };
        var balance = ConsulterRegistre.Chainer(mois);
        Assert.Equal([10, 12, 10], balance.Select(b => b.SoldeReporte));
        Assert.All(balance, b => Assert.Equal(b.SoldeFin, b.SoldeReporte + b.Entrees - b.Sorties));
    }

    [Fact]
    public void Balance_mensuelle_premier_mois_sans_ouverture_garde_l_egalite()
    {
        // Période commencée en cours d'historique : pas de ligne d'ouverture, le reporté se déduit (fin - entrées + sorties)
        var balance = ConsulterRegistre.Chainer([new(1, "Alprazolam", 2026, 3, 4, 1, 20), new(1, "Alprazolam", 2026, 4, 0, 5, 15)]);
        Assert.Equal([17, 20], balance.Select(b => b.SoldeReporte));
        Assert.All(balance, b => Assert.Equal(b.SoldeFin, b.SoldeReporte + b.Entrees - b.Sorties));
    }

    [Fact]
    public void Facture_corrigee_jamais_vers_une_autre_annee_que_celle_du_numero()
    {
        RegleFacture.VerifierAnneeCorrection("F-2026-0042", new DateOnly(2026, 1, 2));
        RegleFacture.VerifierAnneeCorrection("F-2026-0042", new DateOnly(2026, 12, 31));
        var ex = Assert.Throws<RegleMetierException>(() => RegleFacture.VerifierAnneeCorrection("F-2026-0042", new DateOnly(2027, 1, 1)));
        Assert.Equal("date_facture", ex.Champ);
        Assert.Contains("2026", ex.Message);
        Assert.Throws<RegleMetierException>(() => RegleFacture.VerifierAnneeCorrection("AV-2026-0001", new DateOnly(2025, 12, 31)));
    }
}
