using Pharma.Domain.Commun;
using Pharma.Domain.Tresorerie;

namespace Pharma.Domain.Tests;

public class RegleTresorerieTests
{
    private static readonly DateOnly Jour = new(2026, 11, 9);
    private static readonly DateOnly Depart = new(2026, 11, 1);

    [Fact]
    public void Depense_demande_une_categorie_et_un_libelle()
    {
        RegleTresorerie.VerifierMouvement(NatureMouvement.Depense, 1500, "Facture électricité", "energie", null, Jour, Jour, Depart);
        Assert.Throws<RegleMetierException>(() => RegleTresorerie.VerifierMouvement(NatureMouvement.Depense, 1500, "Facture", "inconnue", null, Jour, Jour, Depart));
        Assert.Throws<RegleMetierException>(() => RegleTresorerie.VerifierMouvement(NatureMouvement.Depense, 1500, " ", "energie", null, Jour, Jour, Depart));
    }

    [Fact]
    public void Versement_organisme_demande_l_organisme_mais_pas_de_libelle()
    {
        RegleTresorerie.VerifierMouvement(NatureMouvement.VersementOrganisme, 50000, null, null, 1, Jour, Jour, Depart);
        Assert.Throws<RegleMetierException>(() => RegleTresorerie.VerifierMouvement(NatureMouvement.VersementOrganisme, 50000, null, null, null, Jour, Jour, Depart));
    }

    [Fact]
    public void Montant_positif_date_ni_future_ni_avant_le_depart()
    {
        Assert.Throws<RegleMetierException>(() => RegleTresorerie.VerifierMontantEtDate(0, Jour, Jour, Depart));
        Assert.Throws<RegleMetierException>(() => RegleTresorerie.VerifierMontantEtDate(10.005m, Jour, Jour, Depart));
        Assert.Throws<RegleMetierException>(() => RegleTresorerie.VerifierMontantEtDate(10, Jour.AddDays(1), Jour, Depart));
        Assert.Throws<RegleMetierException>(() => RegleTresorerie.VerifierMontantEtDate(10, Depart.AddDays(-1), Jour, Depart));
    }

    [Fact]
    public void Transfert_entre_deux_comptes_differents() =>
        Assert.Throws<RegleMetierException>(() => RegleTresorerie.VerifierTransfert(3, 3));

    [Theory]
    [InlineData(23500, 5000, 18500)]
    [InlineData(4000, 5000, 0)]
    [InlineData(8000, 0, 8000)]
    public void Versement_automatique_laisse_le_fond_au_tiroir(decimal compte, decimal fond, decimal attendu) =>
        Assert.Equal(attendu, RegleTresorerie.VersementAutomatique(compte, fond));

    [Fact]
    public void Versement_d_une_caisse_au_plus_le_compte()
    {
        RegleTresorerie.VerifierVersementCaisse(18500, 23500);
        Assert.Throws<RegleMetierException>(() => RegleTresorerie.VerifierVersementCaisse(24000, 23500));
    }
}
