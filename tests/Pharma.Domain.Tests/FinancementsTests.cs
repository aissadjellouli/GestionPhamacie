using Pharma.Domain.Achats;
using Pharma.Domain.Commun;
using Pharma.Domain.Financements;

namespace Pharma.Domain.Tests;

public class RegleEcheancierTests
{
    private static readonly DateOnly DatePret = new(2026, 1, 10);

    /// <summary>Prêt de 300 000 DA en trois échéances mensuelles, intérêts, TVA sur une ligne.</summary>
    private static List<LigneEcheancier> Correct() =>
    [
        new(new DateOnly(2026, 2, 10), 100000, 3000, 570, 0, 103570, 200000),
        new(new DateOnly(2026, 3, 10), 100000, 2000, 0, 0, 102000, 100000),
        new(new DateOnly(2026, 4, 10), 100000, 1000, 0, 0, 101000, 0)
    ];

    [Fact]
    public void Echeancier_coherent_sans_anomalie() => Assert.Empty(RegleEcheancier.Controler(300000, DatePret, Correct()));

    [Fact]
    public void Capital_interets_tva_marge_egalent_l_echeance_au_centime()
    {
        var l = Correct();
        l[1] = l[1] with { Montant = 102000.01m };
        var a = Assert.Single(RegleEcheancier.Controler(300000, DatePret, l));
        Assert.Equal((2, "montant"), (a.Ligne, a.Champ));
        Assert.Contains("écart", a.Message);

        // Financement islamique : la marge compte dans l'échéance
        var m = Correct();
        m[2] = m[2] with { Interets = 0, Marge = 1000 };
        Assert.Empty(RegleEcheancier.Controler(300000, DatePret, m));
    }

    [Fact]
    public void Somme_du_capital_egale_au_montant_du_pret()
    {
        var a = Assert.Single(RegleEcheancier.Controler(300001, DatePret, Correct()));
        Assert.Equal((0, "capital"), (a.Ligne, a.Champ));
    }

    [Fact]
    public void Dates_croissantes_et_pas_avant_le_pret()
    {
        var l = Correct();
        l[2] = l[2] with { Date = new DateOnly(2026, 3, 10) };   // même date que la précédente
        Assert.Contains(RegleEcheancier.Controler(300000, DatePret, l), a => a is { Ligne: 3, Champ: "date" });
        var avant = Correct();
        avant[0] = avant[0] with { Date = new DateOnly(2026, 1, 5) };
        Assert.Contains(RegleEcheancier.Controler(300000, DatePret, avant), a => a is { Ligne: 1, Champ: "date" });
    }

    [Fact]
    public void Capital_restant_du_jamais_en_hausse()
    {
        var l = Correct();
        l[2] = l[2] with { CapitalRestant = 150000 };
        Assert.Contains(RegleEcheancier.Controler(300000, DatePret, l), a => a is { Ligne: 3, Champ: "capital_restant" });
        // Différé : capital nul, encours inchangé, admis
        List<LigneEcheancier> differe =
        [
            new(new DateOnly(2026, 2, 10), 0, 3000, 0, 0, 3000, 300000),
            new(new DateOnly(2026, 3, 10), 300000, 3000, 0, 0, 303000, 0)
        ];
        Assert.Empty(RegleEcheancier.Controler(300000, DatePret, differe));
    }

    [Fact]
    public void Montants_au_centime_et_positifs()
    {
        var l = Correct();
        l[0] = l[0] with { Interets = 3000.005m, Montant = 103570.005m };
        Assert.Contains(RegleEcheancier.Controler(300000, DatePret, l), a => a is { Ligne: 1, Champ: "interets" });
        Assert.Contains(RegleEcheancier.Controler(300000, DatePret, []), a => a.Ligne == 0);
    }

    [Fact]
    public void Enregistrement_refuse_tant_qu_une_incoherence_reste()
    {
        var l = Correct();
        l[1] = l[1] with { Montant = 1 };
        var ex = Assert.Throws<RegleMetierException>(() => RegleEcheancier.Exiger(300000, DatePret, l));
        Assert.Equal("echeancier", ex.Champ);
        RegleEcheancier.Exiger(300000, DatePret, Correct());
    }

    [Fact]
    public void Paiement_part_capital_et_charges_financieres()
    {
        var (capital, charges) = RegleEcheancier.Parts(new LigneEcheancier(new DateOnly(2026, 2, 10), 100000, 3000, 570, 250, 103820, 200000));
        Assert.Equal((100000m, 3820m), (capital, charges));
    }

    [Fact]
    public void Capital_restant_calcule_quand_la_banque_ne_le_donne_pas() =>
        Assert.Equal([200000m, 100000m, 0m], RegleEcheancier.Encours(300000, [100000, 100000, 100000]));
}

public class LectureEcheancierTests
{
    [Theory]
    [InlineData("1 234 567,89", 1234567.89)]
    [InlineData("1 234,5", 1234.5)]
    [InlineData("1.234.567,89", 1234567.89)]
    [InlineData("1,234,567.89", 1234567.89)]
    [InlineData("4166.666666667", 4166.67)]
    [InlineData("100 000,00 DA", 100000)]
    [InlineData("1,234", 1234)]
    [InlineData("-250,00", -250)]
    public void Montants_ecrits_par_une_banque_ou_un_tableur(string texte, double attendu) =>
        Assert.Equal((decimal)attendu, LectureEcheancier.Montant(texte));

    [Theory]
    [InlineData("Total")]
    [InlineData("")]
    [InlineData("12/05/2026")]
    public void Texte_qui_n_est_pas_un_montant(string texte) => Assert.Null(LectureEcheancier.Montant(texte));

    [Theory]
    [InlineData("15/01/2026", 2026, 1, 15)]
    [InlineData("5-2-26", 2026, 2, 5)]
    [InlineData("2026-03-31", 2026, 3, 31)]
    [InlineData("46037", 2026, 1, 15)]
    public void Dates_lues(string texte, int a, int m, int j) => Assert.Equal(new DateOnly(a, m, j), LectureEcheancier.Date(texte));

    [Theory]
    [InlineData("31/02/2026")]
    [InlineData("Échéance")]
    [InlineData("125000")]
    public void Texte_qui_n_est_pas_une_date(string texte) => Assert.Null(LectureEcheancier.Date(texte));

    [Fact]
    public void Correspondance_date_obligatoire_deux_montants_sur_trois_colonnes_distinctes()
    {
        new CorrespondanceColonnes(0, 1, 2, null, null, null, null).Verifier();
        Assert.Throws<RegleMetierException>(() => new CorrespondanceColonnes(0, 1, null, null, null, null, 4).Verifier());
        Assert.Throws<RegleMetierException>(() => new CorrespondanceColonnes(0, 1, 1, null, null, 3, null).Verifier());
        Assert.Throws<RegleMetierException>(() => new CorrespondanceColonnes(-1, 1, 2, null, null, 3, null).Verifier());
    }

    [Fact]
    public void Correspondance_appliquee_en_tete_ignoree_totaux_ecartes()
    {
        IReadOnlyList<IReadOnlyList<string?>> tableau =
        [
            ["N°", "Date", "Échéance", "Capital", "Intérêts", "TVA", "Encours"],
            ["1", "10/02/2026", "103 570,00", "100 000,00", "3 000,00", "570,00", "200 000,00"],
            ["2", "10/03/2026", "102 000,00", "100 000,00", "2 000,00", "", "100 000,00"],
            [null, null, null, null, null, null, null],
            ["3", "10/04/2026", "101 000,00", "100 000,00", "1 000,00", "0", "0,00"],
            ["Total", "", "306 570,00", "300 000,00", "6 000,00", "570,00", ""]
        ];
        var lu = LectureEcheancier.Appliquer(tableau, new CorrespondanceColonnes(1, 3, 4, 5, null, 2, 6, LignesIgnorees: 1), 300000);
        Assert.Equal(3, lu.Lignes.Count);
        Assert.Equal([2, 3, 5], lu.Origines);
        Assert.Single(lu.Ecartees);
        Assert.Empty(lu.Illisibles);
        Assert.Equal(new LigneEcheancier(new DateOnly(2026, 2, 10), 100000, 3000, 570, 0, 103570, 200000), lu.Lignes[0]);
        Assert.Empty(RegleEcheancier.Controler(300000, new DateOnly(2026, 1, 10), lu.Lignes));
    }

    [Fact]
    public void Colonne_absente_calculee_et_montant_illisible_signale()
    {
        IReadOnlyList<IReadOnlyList<string?>> tableau =
        [
            ["10/02/2026", "103 000,00", "3 000,00"],
            ["10/03/2026", "1O2 000,00", "2 000,00"]
        ];
        // Échéance et intérêts donnés : capital = échéance − intérêts ; encours calculé
        var lu = LectureEcheancier.Appliquer(tableau, new CorrespondanceColonnes(0, null, 2, null, null, 1, null), 200000);
        Assert.Equal((100000m, 103000m, 100000m), (lu.Lignes[0].Capital, lu.Lignes[0].Montant, lu.Lignes[0].CapitalRestant));
        var a = Assert.Single(lu.Illisibles);
        Assert.Equal((2, "montant"), (a.Ligne, a.Champ));
    }

    [Fact]
    public void Ligne_de_texte_lue_decoupee_en_cellules()
    {
        Assert.Equal(["Échéance", "15/01/2026", "100 000,00", "25 000,00", "125 000,00"],
            LectureEcheancier.Cellules("Échéance 15/01/2026 100 000,00 25 000,00 125 000,00"));
        Assert.Equal(["2", "10/03/2026", "102000.50"], LectureEcheancier.Cellules("2 10/03/2026 102000.50"));
    }

    [Fact]
    public void Csv_point_virgule_avec_guillemets()
    {
        var t = LectureEcheancier.Csv("Date;Capital;Libellé\r\n10/02/2026;\"100 000,00\";\"Échéance \"\"1\"\"; février\"\r\n\r\n10/03/2026;100000;x\n");
        Assert.Equal(3, t.Count);
        Assert.Equal(["10/02/2026", "100 000,00", "Échéance \"1\"; février"], t[1]);
    }
}

public class SourceEtCompteTitulaireTests
{
    [Fact]
    public void Source_lue_titulaire_ou_pret()
    {
        Assert.Null(SourceFinancement.Lire(" "));
        Assert.True(SourceFinancement.Lire("titulaire")!.EstTitulaire);
        Assert.Equal(12L, SourceFinancement.Lire("pret:12")!.PretId);
        Assert.Equal("pret:12", new SourceFinancement(12).Code);
        Assert.Equal("source", Assert.Throws<RegleMetierException>(() => SourceFinancement.Lire("pret:abc")).Champ);
    }

    [Fact]
    public void Solde_du_titulaire_apports_moins_remboursements_et_remboursement_borne()
    {
        var du = RegleCompteTitulaire.Solde([500000, 200000], [150000]);
        Assert.Equal(550000m, du);
        RegleCompteTitulaire.VerifierRemboursement(550000, du);
        Assert.Throws<RegleMetierException>(() => RegleCompteTitulaire.VerifierRemboursement(550000.01m, du));
        Assert.Throws<RegleMetierException>(() => RegleCompteTitulaire.VerifierRemboursement(0, du));
        Assert.Throws<RegleMetierException>(() => RegleCompteTitulaire.VerifierRemboursement(10.001m, du));
    }

    [Fact]
    public void Moyen_financement_jamais_choisi_comme_un_moyen_ordinaire_et_sans_reference_obligatoire()
    {
        Assert.Throws<RegleMetierException>(() => RegleReglement.Moyen("financement"));
        Assert.Throws<RegleMetierException>(() => RegleReglement.VerifierPaiementTraite("financement", null, null, 0, 100, new DateOnly(2026, 11, 9),
            new DateOnly(2026, 10, 1), new DateOnly(2026, 11, 9)));
        RegleReglement.Verifier(100, MoyenReglement.Financement, null, null, 0, new DateOnly(2026, 11, 9), new DateOnly(2026, 11, 9));
        Assert.Equal(("financement", false), (MoyenReglement.Financement.Code(), MoyenReglement.Financement.BanqueObligatoire()));
    }
}
