using Pharma.Application.Caisse;
using Pharma.Application.Financements;
using Pharma.Domain.Commun;
using Pharma.Domain.Financements;

namespace Pharma.Application.Tests;

/// <summary>
/// Prêts : échéance payée en plusieurs fois (charges d'abord, puis capital) et remboursement anticipé (décision du titulaire du
/// 2026-11-26, script 170).
/// </summary>
public class FinancementsPaiementPartielTests
{
    private static readonly LigneEcheancier Ligne = new(new DateOnly(2026, 2, 10), 100000, 3000, 570, 0, 103570, 200000);

    [Fact]
    public void Un_paiement_partiel_couvre_d_abord_les_charges_puis_le_capital_jamais_plus_que_le_reste()
    {
        Assert.Equal((0m, 2000m), RegleEcheancier.Imputer(Ligne, 0, 0, 2000));
        Assert.Equal((1430m, 3570m), RegleEcheancier.Imputer(Ligne, 0, 0, 5000));
        Assert.Equal((98570m, 0m), RegleEcheancier.Imputer(Ligne, 1430, 3570, 98570));     // le reste exact
        Assert.Equal("montant", Assert.Throws<RegleMetierException>(() => RegleEcheancier.Imputer(Ligne, 1430, 3570, 98570.01m)).Champ);
        Assert.Equal("montant", Assert.Throws<RegleMetierException>(() => RegleEcheancier.Imputer(Ligne, 0, 0, 0)).Champ);
        Assert.Equal("montant", Assert.Throws<RegleMetierException>(() => RegleEcheancier.Imputer(Ligne, 0, 0, 10.001m)).Champ);
    }

    [Fact]
    public void Le_capital_rembourse_par_anticipation_compte_dans_la_somme_du_capital_de_l_echeancier()
    {
        LigneEcheancier[] lignes = [Ligne, Ligne with { Date = new DateOnly(2026, 3, 10), CapitalRestant = 50000, Capital = 50000, Montant = 52570 }];
        Assert.Contains(RegleEcheancier.Controler(300000, new DateOnly(2026, 1, 10), lignes), a => a.Champ == "capital");
        Assert.DoesNotContain(RegleEcheancier.Controler(300000, new DateOnly(2026, 1, 10), lignes, 150000), a => a.Champ == "capital");
    }

    [Fact]
    public void Remboursement_anticipe_borne_au_capital_restant_du()
    {
        RegleRemboursementAnticipe.Verifier(1000, 50, 1000);
        Assert.Equal("capital", Assert.Throws<RegleMetierException>(() => RegleRemboursementAnticipe.Verifier(1000.01m, 0, 1000)).Champ);
        Assert.Equal("capital", Assert.Throws<RegleMetierException>(() => RegleRemboursementAnticipe.Verifier(0, 0, 1000)).Champ);
        Assert.Equal("frais", Assert.Throws<RegleMetierException>(() => RegleRemboursementAnticipe.Verifier(10, -1, 1000)).Champ);
    }

    private static (FinancementsEnMemoire F, JournalEspion J, PayerEcheancePret Payer, RembourserPretParAnticipation Anticiper) Banc()
    {
        var f = new FinancementsEnMemoire();
        var j = new JournalEspion();
        var horloge = new HorlogeFixe(new DateOnly(2026, 3, 1));
        var droits = new DroitsFactices { Titulaire = true };
        var tresorerie = new TresorerieEnMemoire();
        return (f, j, new PayerEcheancePret(f, new SessionsEnMemoire { Especes = 5000 }, droits, horloge, j, tresorerie),
            new RembourserPretParAnticipation(f, new SessionsEnMemoire { Especes = 5000 }, droits, horloge, j, tresorerie));
    }

    [Fact]
    public async Task Paiement_en_deux_fois_montant_transmis_avec_le_deja_paye_et_journal_du_reste()
    {
        var (f, j, payer, _) = Banc();
        await payer.ExecuterAsync(100, new DateOnly(2026, 2, 10), "especes", null, null, 0, 1, "CAISSE-1", default, 5000);
        var p = Assert.Single(f.Paiements);
        Assert.Equal((5000m, 0m), (p.Montant, p.DejaPaye));
        Assert.Equal(98570m, j.Details[0]["reste"]);

        f.Echeances[0] = f.Echeances[0] with { MontantPaye = 5000, CapitalPaye = 1430, ChargesPayees = 3570 };
        await payer.ExecuterAsync(100, new DateOnly(2026, 2, 12), "especes", null, null, 0, 1, "CAISSE-1");   // sans montant : le reste
        Assert.Equal((98570m, 5000m), (f.Paiements[1].Montant, f.Paiements[1].DejaPaye));
        await Assert.ThrowsAsync<RegleMetierException>(() => payer.ExecuterAsync(100, null, "especes", null, null, 0, 1, "CAISSE-1", default, 98570.01m));
    }

    [Fact]
    public async Task Remboursement_anticipe_refuse_tant_qu_une_echeance_est_entamee_puis_transmis_et_journalise()
    {
        var (f, j, _, anticiper) = Banc();
        f.Echeances[0] = f.Echeances[0] with { MontantPaye = 5000, CapitalPaye = 1430, ChargesPayees = 3570 };
        await Assert.ThrowsAsync<RegleMetierException>(() => anticiper.ExecuterAsync(5, new DateOnly(2026, 2, 20), 50000, 500, "especes", null, null, 0, 1, "CAISSE-1"));
        Assert.Empty(f.Anticipes);

        f.Echeances[0] = f.Echeances[0] with { MontantPaye = 0, CapitalPaye = 0, ChargesPayees = 0 };
        await Assert.ThrowsAsync<RegleMetierException>(() => anticiper.ExecuterAsync(5, null, 300000.01m, 0, "especes", null, null, 0, 1, "CAISSE-1"));
        await anticiper.ExecuterAsync(5, new DateOnly(2026, 2, 20), 50000, 500, "especes", null, null, 0, 1, "CAISSE-1");
        var a = Assert.Single(f.Anticipes);
        Assert.Equal((50000m, 500m, "especes"), (a.Capital, a.Frais, a.Moyen));
        Assert.Equal("pret_rembourse_anticipe", Assert.Single(j.Actions));
        Assert.Equal(true, j.Details[0]["echeancier_a_remplacer"]);
    }

    [Fact]
    public async Task L_echeancier_garde_en_tete_une_echeance_payee_en_partie_et_compte_le_capital_anticipe()
    {
        var f = new FinancementsEnMemoire();
        f.Echeances[0] = f.Echeances[0] with { MontantPaye = 5000, CapitalPaye = 1430, ChargesPayees = 3570 };
        f.Pret = f.Pret with { CapitalAnticipe = 100000 };
        var enregistrer = new EnregistrerEcheancier(f, new DroitsFactices { Titulaire = true }, new JournalEspion());
        // Échéance entamée gardée, puis une seule échéance de 100 000 : 100 000 + 100 000 + 100 000 anticipés = 300 000
        var lignes = new List<LigneEcheancier> { f.Echeances[0].Ligne, new(new DateOnly(2026, 3, 10), 100000, 1000, 0, 0, 101000, 0) };
        await enregistrer.ExecuterAsync(5, lignes, 1);
        Assert.Equal(1, f.Remplace!.Value.Payees);
        Assert.Single(f.Remplace.Value.Lignes);
        // Échéance entamée absente du nouveau tableau : refus
        await Assert.ThrowsAsync<RegleMetierException>(() => enregistrer.ExecuterAsync(5, [lignes[1] with { Capital = 200000, Montant = 201000 }], 1));
    }
}
