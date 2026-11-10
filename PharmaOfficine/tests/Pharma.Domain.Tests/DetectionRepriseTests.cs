using Pharma.Domain.Reprise;

namespace Pharma.Domain.Tests;

public class DetectionRepriseTests
{
    private static ColonneSource C(string nom, params string?[] ex) => new(nom, "VARCHAR", ex);

    private static readonly TableSource Articles = new("ARTICLES", 1200,
    [
        C("CODE_ART", "A001", "A002", "A003"), C("DESIGNATION", "DOLIPRANE 500MG", "AMOXIL 1G", "SINTROM 4MG"),
        C("CB", "6130000000015", "6130000000022", "6130000000107"), C("PV", "120,00", "350,50", "300"), C("PA", "90", "250", "200")
    ]);
    private static readonly TableSource Lots = new("STOCK_LOTS", 3000,
    [
        C("CODE_ART", "A001", "A001", "A002"), C("N_LOT", "L23", "L24", "AMX9"), C("DATE_EXP", "31/12/2027", "30/06/2028", "15/01/2027"),
        C("QTE", "12", "40", "3"), C("CODE_FRS", "F1", "F2", "F1")
    ]);
    private static readonly TableSource Frs = new("FOURNISSEURS", 4, [C("CODE_FRS", "F1", "F2"), C("RAISON_SOC", "Grossiste A", "Grossiste B"), C("SOLDE", "12500,00", "0")]);
    private static readonly TableSource Clients = new("CLIENTS", 900,
        [C("NUM_CLI", "C1", "C2"), C("NOM", "Patient Un", "Patient Deux"), C("TEL", "0550123456", "0661234567"), C("CREDIT", "1500", "0")]);
    private static readonly TableSource Journal = new("JOURNAL_CONNEXIONS", 50000, [C("ID", "1", "2"), C("UTILISATEUR", "admin", "caisse"), C("DATE_HEURE", "2026-01-01 08:00:00")]);

    [Fact]
    public void Chaque_table_utile_est_reconnue_avec_ses_colonnes()
    {
        var p = DetectionReprise.Proposer([Journal, Clients, Lots, Frs, Articles]).ToDictionary(x => x.Cible);
        var prod = p[CibleReprise.Produits];
        Assert.Equal("ARTICLES", prod.Table);
        Assert.Equal(("CODE_ART", "DESIGNATION", "CB", "PV", "PA"),
            (prod.Colonnes["ref_source"], prod.Colonnes["nom"], prod.Colonnes["code_barre"], prod.Colonnes["prix_vente"], prod.Colonnes["prix_achat"]));
        var stock = p[CibleReprise.Stock];
        Assert.Equal("STOCK_LOTS", stock.Table);
        Assert.Equal(("N_LOT", "DATE_EXP", "QTE", "CODE_FRS"),
            (stock.Colonnes["numero_lot"], stock.Colonnes["date_peremption"], stock.Colonnes["quantite"], stock.Colonnes["fournisseur_ref"]));
        Assert.Equal(("FOURNISSEURS", "RAISON_SOC", "SOLDE"),
            (p[CibleReprise.Fournisseurs].Table, p[CibleReprise.Fournisseurs].Colonnes["raison_sociale"], p[CibleReprise.Fournisseurs].Colonnes["solde"]));
        Assert.Equal(("CLIENTS", "NOM", "CREDIT"),
            (p[CibleReprise.Clients].Table, p[CibleReprise.Clients].Colonnes["nom"], p[CibleReprise.Clients].Colonnes["solde_credit"]));
    }

    [Fact]
    public void Table_sans_rapport_non_proposee() =>
        Assert.Null(DetectionReprise.ProposerPour(CibleReprise.Produits, [Journal]).Table);

    [Fact]
    public void Code_barre_reconnu_par_ses_valeurs_meme_sous_un_nom_inattendu()
    {
        var champ = DetectionReprise.Champs[CibleReprise.Produits].First(c => c.Code == "code_barre");
        Assert.True(DetectionReprise.Score(champ, C("ZONE7", "6130000000015", "6130000000022")) >= 0.4);
        Assert.True(DetectionReprise.Score(champ, C("ZONE8", "123", "456")) < 0.4);
    }

    [Theory]
    [InlineData("6130000000107", true)]
    [InlineData("6130000000108", false)]
    [InlineData("613000000010", false)]
    public void Cle_EAN13_verifiee(string code, bool attendu) => Assert.Equal(attendu, DetectionReprise.EstEan13(code));

    [Theory]
    [InlineData("CLIENTS", "NOM", true)]
    [InlineData("CLIENTS", "TEL", true)]
    [InlineData("ARTICLES", "NOM", false)]          // nom d'un produit : visible pour vérifier la correspondance
    [InlineData("FOURNISSEURS", "TELEPHONE", true)]
    public void Donnees_personnelles_masquees(string table, string colonne, bool attendu) =>
        Assert.Equal(attendu, DetectionReprise.EstPersonnelle(table, colonne));

    [Fact]
    public void Lectures_tolerantes()
    {
        Assert.Equal(new DateOnly(2027, 12, 31), DetectionReprise.LireDate("31/12/2027"));
        Assert.Equal(1234.5m, DetectionReprise.LireNombre("1 234,50"));
        Assert.Equal("prix_d_achat_ht", DetectionReprise.Normaliser("Prix d'achat HT"));
    }
}
