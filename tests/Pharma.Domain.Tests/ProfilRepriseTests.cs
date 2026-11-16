using Pharma.Domain.Commun;
using Pharma.Domain.Reprise;

namespace Pharma.Domain.Tests;

public class ProfilRepriseTests
{
    private static ChampCible Champ(CibleReprise cible, string code) => DetectionReprise.Champs[cible].Single(c => c.Code == code);
    private static readonly ChampCible PrixVente = Champ(CibleReprise.Produits, "prix_vente");
    private static readonly ChampCible Peremption = Champ(CibleReprise.Stock, "date_peremption");
    private static readonly ChampCible Classe = Champ(CibleReprise.Produits, "classe");

    private static TransformationsReprise T(SeparateurDecimal s = SeparateurDecimal.Auto, string? date = null, Dictionary<string, string>? codes = null) =>
        new(s, date, codes ?? []);

    private static ProfilReprise Profil(params CorrespondanceProfil[] c) =>
        new(0, " Chifa Plus ", " 3.1 ", "a_completer", "  ", c, TransformationsReprise.Aucune);

    [Theory]
    [InlineData("1.234,50", SeparateurDecimal.Virgule, "1234.5")]
    [InlineData("12.500", SeparateurDecimal.Virgule, "12500")]
    [InlineData("12,5 DA", SeparateurDecimal.Virgule, "12.5")]
    [InlineData("1,234.50", SeparateurDecimal.Point, "1234.5")]
    [InlineData("12.500", SeparateurDecimal.Point, "12.5")]
    [InlineData("1.234", SeparateurDecimal.Point, "1.2340")]   // jamais « 1.234 », lu comme ambigu par import.nombre
    [InlineData("-3,25", SeparateurDecimal.Virgule, "-3.25")]
    public void Separateur_decimal_du_profil_leve_l_ambiguite(string valeur, SeparateurDecimal s, string attendu) =>
        Assert.Equal(attendu, ProfilsReprise.Transformer(PrixVente, valeur, T(s)));

    [Fact]
    public void Valeur_illisible_rendue_telle_quelle_pour_etre_signalee()
    {
        Assert.Equal("abc", ProfilsReprise.Transformer(PrixVente, "abc", T(SeparateurDecimal.Virgule)));
        Assert.Equal("31/02/2025", ProfilsReprise.Transformer(Peremption, "31/02/2025", T(date: "dd/MM/yyyy")));
        Assert.Equal("12.500", ProfilsReprise.Transformer(PrixVente, "12.500", T()));   // sans profil : rien n'est deviné
    }

    [Theory]
    [InlineData("03/04/2025", "MM/dd/yyyy", "2025-03-04")]
    [InlineData("03/04/2025", "dd/MM/yyyy", "2025-04-03")]
    [InlineData("20250403", "yyyyMMdd", "2025-04-03")]
    [InlineData("04/2025", "MM/yyyy", "04/2025")]   // sans jour : dernier jour du mois au chargement (import.date_src)
    public void Format_de_date_du_profil(string valeur, string format, string attendu) =>
        Assert.Equal(attendu, ProfilsReprise.Transformer(Peremption, valeur, T(date: format)));

    [Fact]
    public void Codes_de_classe_traduits_sans_tenir_compte_de_la_casse()
    {
        var t = T(codes: new() { ["P"] = "psychotrope", ["L1"] = "liste_i" });
        Assert.Equal("psychotrope", ProfilsReprise.Transformer(Classe, "p", t));
        Assert.Equal("liste_i", ProfilsReprise.Transformer(Classe, " L1 ", t));
        Assert.Equal("X", ProfilsReprise.Transformer(Classe, "X", t));
    }

    [Fact]
    public void Validation_nettoie_et_ecarte_les_cibles_non_reprises()
    {
        var p = ProfilsReprise.Valider(Profil(
            new(CibleReprise.Produits, " Feuil1 ", new Dictionary<string, string> { ["ref_source"] = " CODE ", ["nom"] = "", ["dosage"] = "DOS" }),
            new(CibleReprise.Stock, "  ", new Dictionary<string, string>())));
        Assert.Equal(("Chifa Plus", "3.1", (string?)null), (p.Logiciel, p.Version, p.Remarque));
        var c = Assert.Single(p.Correspondances);
        Assert.Equal("Feuil1", c.Table);
        Assert.Equal(new Dictionary<string, string> { ["ref_source"] = "CODE", ["dosage"] = "DOS" }, c.Colonnes);
    }

    [Fact]
    public void Validation_refuse_champ_inconnu_classe_inconnue_format_non_propose_et_cible_en_double()
    {
        Assert.Throws<RegleMetierException>(() => ProfilsReprise.Valider(Profil() with { Logiciel = " " }));
        Assert.Throws<RegleMetierException>(() => ProfilsReprise.Valider(Profil() with { Statut = "autre" }));
        Assert.Throws<RegleMetierException>(() => ProfilsReprise.Valider(Profil(
            new CorrespondanceProfil(CibleReprise.Produits, "F", new Dictionary<string, string> { ["inconnu"] = "X" }))));
        Assert.Throws<RegleMetierException>(() => ProfilsReprise.Valider(Profil(
            new CorrespondanceProfil(CibleReprise.Produits, "F", new Dictionary<string, string>()),
            new CorrespondanceProfil(CibleReprise.Produits, "G", new Dictionary<string, string>()))));
        Assert.Throws<RegleMetierException>(() => ProfilsReprise.Valider(Profil() with { Transformations = T(date: "dd MMMM yyyy") }));
        Assert.Throws<RegleMetierException>(() => ProfilsReprise.Valider(Profil() with { Transformations = T(codes: new() { ["P"] = "tableau_b" }) }));
    }

    [Fact]
    public void Historique_chifa_propose_sur_une_feuille_de_factures()
    {
        var factures = new TableSource("FACTURES_CHIFA", 3,
        [
            new("N_FACTURE", "Texte", ["F001", "F002"]), new("DATE_FACTURE", "Texte", ["2024-03-12", "2024-03-13"]),
            new("NSS", "Texte", ["123456789012"]), new("TOTAL", "Texte", ["1200", "850.5"])
        ]);
        var p = DetectionReprise.ProposerPour(CibleReprise.HistoriqueChifa, [factures]);
        Assert.Equal("FACTURES_CHIFA", p.Table);
        Assert.Equal("N_FACTURE", p.Colonnes["num_facture"]);
        Assert.Equal("DATE_FACTURE", p.Colonnes["date_facture"]);
        Assert.Equal("TOTAL", p.Colonnes["montant_total"]);
        Assert.True(DetectionReprise.EstPersonnelle("FACTURES_CHIFA", "NSS"));
    }
}
